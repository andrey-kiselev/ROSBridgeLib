﻿using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.IO;
using System;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using SimpleJSON;
using Newtonsoft.Json;
using UnityEngine;
using ROSBridgeLib.sensor_msgs;

/**
 * This class handles the connection with the external ROS world, deserializing
 * json messages into appropriate instances of packets and messages.
 * 
 * This class also provides a mechanism for having the callback's exectued on the rendering thread.
 * (Remember, Unity has a single rendering thread, so we want to do all of the communications stuff away
 * from that. 
 * 
 * The one other clever thing that is done here is that we only keep 1 (the most recent!) copy of each message type
 * that comes along.
 * 
 * Version History
 * 3.1 - changed methods to start with an upper case letter to be more consistent with c#
 * style.
 * 3.0 - modification from hand crafted version 2.0
 * 
 * @author Michael Jenkin, Robert Codd-Downey and Andrew Speers
 * @version 3.1
 */

namespace ROSBridgeLib
{
	public class ROSBridgeWebSocketConnection
	{
		private class RenderTask
		{
			private Type _subscriber;
			private string _topic;
			private ROSBridgeMsg _msg;

			public RenderTask( Type subscriber, string topic, ROSBridgeMsg msg )
			{
				_subscriber = subscriber;
				_topic = topic;
				_msg = msg;
			}

			public Type getSubscriber()
			{
				return _subscriber;
			}

			public ROSBridgeMsg getMsg()
			{
				return _msg;
			}

			public string getTopic()
			{
				return _topic;
			}
		};
		private string _host;
		private int _port;
		private WebSocket _ws;
		private System.Threading.Thread _myThread;
		private List<Type> _subscribers; // our subscribers
		private List<Type> _publishers; //our publishers
		private Type _serviceResponse; // to deal with service responses
		private string _serviceName = null;
		private string _serviceValues = null;
		private List<RenderTask> _taskQ = new List<RenderTask> ();

		private object _queueLock = new object ();

		private static string GetMessageType( Type t )
		{
			return (string)t.GetMethod ( "GetMessageType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ).Invoke ( null, null );
		}

		private static string GetMessageTopic( Type t )
		{
			return (string)t.GetMethod ( "GetMessageTopic", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ).Invoke ( null, null );
		}

		private static ROSBridgeMsg ParseMessage( Type t, JSONNode node )
		{
			return (ROSBridgeMsg)t.GetMethod ( "ParseMessage", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ).Invoke ( null, new object[] { node } );
		}

		private static void Update( Type t, ROSBridgeMsg msg, UnityEngine.GameObject gameObject )
		{
			t.GetMethod ( "CallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ).Invoke ( null, new object[] { msg, gameObject } );
		}

		private static void ServiceResponse( Type t, string service, string yaml )
		{
			t.GetMethod ( "ServiceCallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ).Invoke ( null, new object[] { service, yaml } );
		}

		private static void IsValidServiceResponse( Type t )
		{
			if ( t.GetMethod ( "ServiceCallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ) == null )
				throw new Exception ( "invalid service response handler" );
		}

		private static void IsValidSubscriber( Type t )
		{
			if ( t.GetMethod ( "CallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ) == null )
				throw new Exception ( "missing Callback method" );
			if ( t.GetMethod ( "GetMessageType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ) == null )
				throw new Exception ( "missing GetMessageType method" );
			if ( t.GetMethod ( "GetMessageTopic", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ) == null )
				throw new Exception ( "missing GetMessageTopic method" );
			if ( t.GetMethod ( "ParseMessage", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ) == null )
				throw new Exception ( "missing ParseMessage method" );
		}

		private static void IsValidPublisher( Type t )
		{
			if ( t.GetMethod ( "GetMessageType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ) == null )
				throw new Exception ( "missing GetMessageType method" );
			if ( t.GetMethod ( "GetMessageTopic", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy ) == null )
				throw new Exception ( "missing GetMessageTopic method" );
		}

		// Unity gameObject the connector is attached to
		private UnityEngine.GameObject _gameObject;

		/**
		 * Make a connection to a host/port. 
		 * This does not actually start the connection, use Connect to do that.
		 */
		public ROSBridgeWebSocketConnection( string host, int port, UnityEngine.GameObject gameObject )
		{
			_host = host;
			_port = port;
			_myThread = null;
			_subscribers = new List<Type> ();
			_publishers = new List<Type> ();
			_gameObject = gameObject;
		}

		/**
		 * Add a service response callback to this connection.
		 */
		public void AddServiceResponse( Type serviceResponse )
		{
			IsValidServiceResponse ( serviceResponse );
			_serviceResponse = serviceResponse;
		}

		/**
		 * Add a subscriber callback to this connection. There can be many subscribers.
		 */
		public void AddSubscriber( Type subscriber )
		{
			IsValidSubscriber ( subscriber );
			_subscribers.Add ( subscriber );
		}

		/**
		 * Add a publisher to this connection. There can be many publishers.
		 */
		public void AddPublisher( Type publisher )
		{
			IsValidPublisher ( publisher );
			_publishers.Add ( publisher );
		}

		/**
		 * Connect to the remote ros environment.
		 */
		public void Connect()
		{
			_applicationIsPlaying = true;
			_myThread = new System.Threading.Thread ( Run );
			_myThread.Start ();
		}

		// flag to interrupt the loop
		// https://msdn.microsoft.com/en-us/library/7a2f3ay4(v=vs.90).aspx
		private volatile bool _applicationIsPlaying;

		/**
		 * Disconnect from the remote ros environment.
		 */
		public void Disconnect()
		{
			_applicationIsPlaying = false;
			_myThread.Join ();
			//_myThread.Abort (); // Abort() does not guarantee that the thread is stopped
			if ( _ws != null )
			{
				foreach ( Type p in _subscribers )
				{
					try
					{
						_ws.Send ( ROSBridgeMsg.UnSubscribe ( GetMessageTopic ( p ) ) );
						UnityEngine.Debug.Log ( "Send " + ROSBridgeMsg.UnSubscribe ( GetMessageTopic ( p ) ) );
					} catch
					{
						UnityEngine.Debug.LogWarning ( "Sending " + ROSBridgeMsg.UnSubscribe ( GetMessageTopic ( p ) ) + " failed." );
					}
				}
				foreach ( Type p in _publishers )
				{
					try
					{
						_ws.Send ( ROSBridgeMsg.UnAdvertise ( GetMessageTopic ( p ) ) );
						UnityEngine.Debug.Log ( "Send " + ROSBridgeMsg.UnAdvertise ( GetMessageTopic ( p ) ) );
					} catch
					{
						UnityEngine.Debug.LogWarning ( "Sending " + ROSBridgeMsg.UnAdvertise ( GetMessageTopic ( p ) ) + " failed." );
					}
				}
			}
			_ws.Close ();
		}

		private void Run()
		{
			_ws = new WebSocket ( _host + ":" + _port );
			//			_ws.Compression = CompressionMethod.Deflate;
			_ws.Log.Level = LogLevel.Trace;
			//			_ws.Log.File = "socket.log";
			_ws.OnError += ( sender, e ) => { UnityEngine.Debug.LogError ( "Error: " + e.Message ); };
			_ws.OnClose += ( sender, e ) => { UnityEngine.Debug.Log ( "Connection closed: " + e.Reason ); };

			_ws.OnMessage += ( sender, e ) => this.OnMessage ( e.Data );
			_ws.Connect ();
			if ( _ws != null )
			{
				foreach ( Type p in _subscribers )
				{
					_ws.Send ( ROSBridgeMsg.Subscribe ( GetMessageTopic ( p ), GetMessageType ( p ) ) );
					UnityEngine.Debug.Log ( "Sending " + ROSBridgeMsg.Subscribe ( GetMessageTopic ( p ), GetMessageType ( p ) ) );
				}
				foreach ( Type p in _publishers )
				{
					_ws.Send ( ROSBridgeMsg.Advertise ( GetMessageTopic ( p ), GetMessageType ( p ) ) );
					UnityEngine.Debug.Log ( "Sending " + ROSBridgeMsg.Advertise ( GetMessageTopic ( p ), GetMessageType ( p ) ) );
				}
			}
			while ( _applicationIsPlaying )
			{
				Thread.Sleep ( 1000 );
			}
		}

		private void OnMessage( string s )
		{
			//		 	UnityEngine.Debug.Log ("Got a message: " + s);
			Stopwatch stopwatch = Stopwatch.StartNew ();
			if ( (s != null) && !s.Equals ( "" ) )
			{

				try
				{
					//					ImageMsgJSON dc_msg = JsonConvert.DeserializeObject <ImageMsgJSON> (s);
					JSONNode node = JSONNode.Parse ( s ); // this can throw exceptions!!!
														  //UnityEngine.Debug.Log ("Parsed it");
					string op = node["op"];
					//UnityEngine.Debug.Log ("Operation is " + op);
					if ( "publish".Equals ( op ) )
					{
						string topic = node["topic"];
						//						UnityEngine.Debug.Log ("Got a message on " + topic);
						foreach ( Type p in _subscribers )
						{
							if ( topic.Equals ( GetMessageTopic ( p ) ) )
							{
								//UnityEngine.Debug.Log ("And will parse it " + GetMessageTopic (p));
								ROSBridgeMsg msg = ParseMessage ( p, node["msg"] );
								RenderTask newTask = new RenderTask ( p, topic, msg );
								lock ( _queueLock )
								{
									bool found = false;
									for ( int i = 0; i < _taskQ.Count; i++ )
									{
										if ( _taskQ[i].getTopic ().Equals ( topic ) )
										{
											_taskQ.RemoveAt ( i );
											_taskQ.Insert ( i, newTask );
											found = true;
											break;
										}
									}
									if ( !found )
										_taskQ.Add ( newTask );
								}

							}
						}
					} else if ( "service_response".Equals ( op ) )
					{
						UnityEngine.Debug.Log ( "Got service response " + node.ToString () );
						_serviceName = node["service"];
						_serviceValues = (node["values"] == null) ? "" : node["values"].ToString ();
					} else
					{
						UnityEngine.Debug.LogWarning ( "Must write code here for other messages" );
					}
				} catch ( Exception e )
				{
					UnityEngine.Debug.LogException ( e );
				}
			} else
			{
				UnityEngine.Debug.LogError ( "Got an empty message from the web socket" );
			}
			stopwatch.Stop ();
			//			UnityEngine.Debug.Log ("Message importing time: " + stopwatch.ElapsedMilliseconds);
		}

		public void Render()
		{
			RenderTask newTask = null;
			lock ( _queueLock )
			{
				if ( _taskQ.Count > 0 )
				{
					newTask = _taskQ[0];
					_taskQ.RemoveAt ( 0 );
				}
			}
			if ( newTask != null )
				Update ( newTask.getSubscriber (), newTask.getMsg (), _gameObject );

			if ( _serviceName != null )
			{
				ServiceResponse ( _serviceResponse, _serviceName, _serviceValues );
				_serviceName = null;
			}
		}

		public void Publish( String topic, ROSBridgeMsg msg )
		{
			//			if(_ws != null && _ws.IsConnected && _ws.IsAlive) { //this call take a lot of time
			if ( _ws != null )
			{
				string s = ROSBridgeMsg.Publish ( topic, msg.ToYAMLString () );
				//				UnityEngine.Debug.Log ("Sending " + s);
				_ws.Send ( s );
			}
		}

		public void CallService( string service, string args )
		{
			if ( _ws != null )
			{
				string s = ROSBridgeMsg.CallService ( service, args );
				//				UnityEngine.Debug.Log ("Sending " + s);
				_ws.Send ( s );
			}
		}
	}
}
