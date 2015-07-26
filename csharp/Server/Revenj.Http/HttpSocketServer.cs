﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.ServiceModel;
using System.Threading;
using Revenj.Api;
using Revenj.DomainPatterns;

namespace Revenj.Http
{
	internal sealed class HttpSocketServer
	{
		private static readonly TraceSource TraceSource = new TraceSource("Revenj.Server");
		private static readonly string MissingBasicAuth = "Basic realm=\"" + Environment.MachineName + "\"";

		private static int MessageSizeLimit = 8 * 1024 * 1024;

		private readonly IServiceProvider Locator;
		private readonly Socket Socket;
		private readonly Routes Routes;
		private readonly HttpAuth Authentication;

		private readonly ConcurrentStack<HttpSocketContext> Contexts = new ConcurrentStack<HttpSocketContext>();

		public HttpSocketServer(IServiceProvider locator)
		{
			this.Locator = locator;
			Socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
			//Socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27, 0);
			var endpoints = new List<IPEndPoint>();
			foreach (string key in ConfigurationManager.AppSettings.Keys)
			{
				if (key.StartsWith("HttpAddress", StringComparison.InvariantCultureIgnoreCase))
				{
					var addr = new Uri(ConfigurationManager.AppSettings[key]);
					IPAddress ip;
					if (!IPAddress.TryParse(addr.Host, out ip))
					{
						var ips = Dns.GetHostAddresses(addr.Host);
						foreach (var i in ips)
							if (i.AddressFamily == AddressFamily.InterNetworkV6)
								endpoints.Add(new IPEndPoint(i, addr.Port));
					}
					else endpoints.Add(new IPEndPoint(ip, addr.Port));
				}
			}
			if (endpoints.Count == 0)
				endpoints.Add(new IPEndPoint(IPAddress.Any, 8999));
			foreach (var ep in endpoints)
			{
				Socket.Bind(ep);
				Console.WriteLine("Bound to: " + ep);
			}
			var maxLen = ConfigurationManager.AppSettings["Revenj.ContentLengthLimit"];
			if (!string.IsNullOrEmpty(maxLen)) MessageSizeLimit = int.Parse(maxLen);
			Routes = new Routes(locator);
			var customAuth = ConfigurationManager.AppSettings["CustomAuth"];
			if (!string.IsNullOrEmpty(customAuth))
			{
				var authType = Type.GetType(customAuth);
				if (!typeof(HttpAuth).IsAssignableFrom(authType))
					throw new ConfigurationErrorsException("Custom auth does not inherit from HttpAuth. Please inherit from " + typeof(HttpAuth).FullName);
				Authentication = locator.Resolve<HttpAuth>(authType);
			}
			else Authentication = locator.Resolve<HttpAuth>();
			for (int i = 0; i < Environment.ProcessorCount * 3; i++)
				Contexts.Push(new HttpSocketContext("http://127.0.0.1/", MessageSizeLimit));
		}

		public void Run()
		{
			try
			{
				var backlog = ConfigurationManager.AppSettings["Revenj.Backlog"];
				if (backlog != null)
					Socket.Listen(int.Parse(backlog));
				else
					Socket.Listen(1000);
				TraceSource.TraceEvent(TraceEventType.Start, 1002);
				ThreadPool.SetMinThreads(64 + Environment.ProcessorCount * 3, 64 + Environment.ProcessorCount * 3);
				Console.WriteLine("Http server running");
				while (true)
				{
					try
					{
						var socket = Socket.Accept();
						if (socket.Connected)
							ThreadPool.QueueUserWorkItem(ProcessSocketThread, socket);
					}
					catch (HttpListenerException ex)
					{
						Console.WriteLine(ex.ToString());
						TraceSource.TraceEvent(TraceEventType.Error, 5401, "{0}", ex);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.ToString());
				TraceSource.TraceEvent(TraceEventType.Error, 5402, "{0}", ex);
				throw;
			}
			finally
			{
				TraceSource.TraceEvent(TraceEventType.Stop, 1002);
				Socket.Shutdown(SocketShutdown.Both);
			}
		}

		private void ProcessSocketThread(object state)
		{
			var socket = (Socket)state;
			socket.Blocking = true;
			HttpSocketContext ctx;
			if (!Contexts.TryPop(out ctx))
				ctx = new HttpSocketContext(socket.LocalEndPoint.ToString(), MessageSizeLimit);
			try
			{
				while (ctx.Process(socket))
				{
					RouteMatch match;
					var route = Routes.Find(ctx.HttpMethod, ctx.RawUrl, ctx.AbsolutePath, out match);
					if (route == null)
					{
						var unknownRoute = "Unknown route " + ctx.RawUrl + " on method " + ctx.HttpMethod;
						ctx.ReturnError(socket, 404, unknownRoute, false);
						break;
					}
					var auth = Authentication.TryAuthorize(ctx.GetRequestHeader("authorization"), ctx.RawUrl, route);
					if (auth.Principal != null)
					{
						ctx.ForRouteWithAuth(match, auth.Principal);
						ThreadContext.Request = ctx;
						ThreadContext.Response = ctx;
						Thread.CurrentPrincipal = auth.Principal;
						using (var stream = route.Handle(match.BoundVars, ctx.Stream))
						{
							var keepAlive = ctx.Return(stream, socket);
							if (keepAlive && socket.Connected) continue;
							socket.Close();
							break;
						}
					}
					else if (auth.SendAuthenticate)
					{
						ctx.AddHeader("WWW-Authenticate", MissingBasicAuth);
						ctx.ReturnError(socket, (int)auth.ResponseCode, auth.Error, true);
						break;
					}
					else
					{
						ctx.ReturnError(socket, (int)auth.ResponseCode, auth.Error, true);
						break;
					}
				}
			}
			catch (SecurityException sex)
			{
				try { ctx.ReturnError(socket, (int)HttpStatusCode.Forbidden, sex.Message, true); }
				catch (Exception ex)
				{
					Console.WriteLine(ex.Message);
					TraceSource.TraceEvent(TraceEventType.Error, 5404, "{0}", ex);
				}
			}
			catch (ActionNotSupportedException anse)
			{
				try { ctx.ReturnError(socket, 404, anse.Message, true); }
				catch (Exception ex)
				{
					Console.WriteLine(ex.Message);
					TraceSource.TraceEvent(TraceEventType.Error, 5404, "{0}", ex);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
				TraceSource.TraceEvent(TraceEventType.Error, 5403, "{0}", ex);
				try { ctx.ReturnError(socket, 500, ex.Message, false); }
				catch (Exception ex2)
				{
					Console.WriteLine(ex2.Message);
					TraceSource.TraceEvent(TraceEventType.Error, 5404, "{0}", ex2);
				}
			}
			finally
			{
				Contexts.Push(ctx);
			}
		}
	}
}