﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Web;
using Elastic.Apm.Api;
using Elastic.Apm.DiagnosticSource;
using Elastic.Apm.DistributedTracing;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Model;

[assembly:
	InternalsVisibleTo(
		"Elastic.Apm.AspNetFullFramework.Tests, PublicKey=002400000480000094000000060200000024000052534131000400000100010051df3e4d8341d66c6dfbf35b2fda3627d08073156ed98eef81122b94e86ef2e44e7980202d21826e367db9f494c265666ae30869fb4cd1a434d171f6b634aa67fa8ca5b9076d55dc3baa203d3a23b9c1296c9f45d06a45cf89520bef98325958b066d8c626db76dd60d0508af877580accdd0e9f88e46b6421bf09a33de53fe1")]


namespace Elastic.Apm.AspNetFullFramework
{
	public class ElasticApmModule : IHttpModule
	{
		private static readonly bool IsCaptureHeadersEnabled;
		private static readonly IApmLogger Logger;

		static ElasticApmModule()
		{
			var configReader = new FullFrameworkConfigReader(ConsoleLogger.Instance);
			var agentComponents = new AgentComponents(configurationReader: configReader);
			// Logger should be set ASAP because other initialization steps depend on it
			Logger = agentComponents.Logger.Scoped(nameof(ElasticApmModule));

			Logger.Debug()
				?.Log($"Entered {nameof(ElasticApmModule)} static ctor" +
					$"; .NET Runtime description: {PlatformDetection.DotNetRuntimeDescription}" +
					$"; IIS: {IisVersion}");

			// FindAspNetVersion uses Logger
			var aspNetVersion = FindAspNetVersion();
			Logger.Debug()?.Log($"ASP.NET version: {aspNetVersion}");

			SetServiceInformation(agentComponents.Service, aspNetVersion);

			Agent.Setup(agentComponents);

			IsCaptureHeadersEnabled = Agent.Instance.ConfigurationReader.CaptureHeaders;

			Agent.Instance.Subscribe(new HttpDiagnosticsSubscriber());
		}

		// We can store current transaction because each IHttpModule is used for at most one request at a time
		// For example see https://bytes.com/topic/asp-net/answers/324305-httpmodule-multithreading-request-response-corelation
		private Transaction _currentTransaction;

		private HttpApplication _httpApp;
		private static Version IisVersion => HttpRuntime.IISVersion;

		private static void SetServiceInformation(Service service, string aspNetVersion)
		{
			service.Framework = new Framework { Name = "ASP.NET", Version = aspNetVersion };
			service.Language = new Language { Name = "C#" }; //TODO
		}

		public void Init(HttpApplication httpApp)
		{
			_httpApp = httpApp;
			_httpApp.BeginRequest += OnBeginRequest;
			_httpApp.EndRequest += OnEndRequest;
		}

		public void Dispose()
		{
			if (_httpApp != null)
			{
				_httpApp.BeginRequest -= OnBeginRequest;
				_httpApp.EndRequest -= OnEndRequest;
				_httpApp = null;
			}
		}

		private void OnBeginRequest(object eventSender, EventArgs eventArgs)
		{
			Logger.Debug()?.Log("Incoming request processing started - starting trace...");

			try
			{
				ProcessBeginRequest(eventSender);
			}
			catch (Exception ex)
			{
				Logger.Error()?.Log("Processing BeginRequest event failed. Exception: {Exception}", ex);
			}
		}

		private void OnEndRequest(object eventSender, EventArgs eventArgs)
		{
			Logger.Debug()?.Log("Incoming request processing finished - ending trace...");

			try
			{
				ProcessEndRequest(eventSender);
			}
			catch (Exception ex)
			{
				Logger.Error()?.Log("Processing EndRequest event failed. Exception: {Exception}", ex);
			}
		}

		private void ProcessBeginRequest(object eventSender)
		{
			var httpApp = (HttpApplication)eventSender;
			var httpRequest = httpApp.Context.Request;

			var distributedTracingData = ExtractIncomingDistributedTracingData(httpRequest);
			if (distributedTracingData != null)
			{
				Logger.Debug()
					?.Log(
						"Incoming request with {TraceParentHeaderName} header. DistributedTracingData: {DistributedTracingData} - continuing trace",
						TraceParent.TraceParentHeaderName, distributedTracingData);

				_currentTransaction = Agent.Instance.TracerInternal.StartTransactionInternal(
					$"{httpRequest.HttpMethod} {httpRequest.Path}",
					ApiConstants.TypeRequest,
					distributedTracingData);
			}
			else
			{
				Logger.Debug()?.Log("Incoming request doesn't have valid incoming distributed tracing data - starting trace with new trace id.");
				_currentTransaction = Agent.Instance.TracerInternal.StartTransactionInternal(
					$"{httpRequest.HttpMethod} {httpRequest.Path}",
					ApiConstants.TypeRequest);
			}

			if (_currentTransaction.IsSampled) FillSampledTransactionContextRequest(httpRequest, _currentTransaction);
		}

		private static DistributedTracingData ExtractIncomingDistributedTracingData(HttpRequest httpRequest)
		{
			var headerValue = httpRequest.Headers.Get(TraceParent.TraceParentHeaderName);
			if (headerValue == null)
			{
				Logger.Debug()
					?.Log("Incoming request doesn't have {TraceParentHeaderName} header - " +
						"it means request doesn't have incoming distributed tracing data", TraceParent.TraceParentHeaderName);
				return null;
			}
			return TraceParent.TryExtractTraceparent(headerValue);
		}

		private static void FillSampledTransactionContextRequest(HttpRequest httpRequest, ITransaction transaction)
		{
			var httpRequestUrl = httpRequest.Url;
			var queryString = httpRequestUrl.Query;
			var rawUrlPathAndQuery = httpRequest.RawUrl;
			var fullUrl = httpRequestUrl.AbsoluteUri;
			if (queryString.IsEmpty())
			{
				// Uri.Query returns empty string both when query string is empty ("http://host/path?") and
				// when there's no query string at all ("http://host/path") so we need a way to distinguish between these cases
				if (rawUrlPathAndQuery.IndexOf('?') == -1)
					queryString = null;
				else if (!fullUrl.IsEmpty() && fullUrl[fullUrl.Length - 1] != '?')
					fullUrl += "?";
			}
			else if (queryString[0] == '?')
				queryString = queryString.Substring(1, queryString.Length - 1);
			var url = new Url
			{
				Full = fullUrl,
				HostName = httpRequestUrl.Host,
				Protocol = "HTTP",
				Raw = fullUrl,
				PathName = httpRequestUrl.AbsolutePath,
				Search = queryString
			};

			transaction.Context.Request = new Request(httpRequest.HttpMethod, url)
			{
				Socket = new Socket { Encrypted = httpRequest.IsSecureConnection, RemoteAddress = httpRequest.UserHostAddress },
				HttpVersion = GetHttpVersion(httpRequest.ServerVariables["SERVER_PROTOCOL"]),
				Headers = IsCaptureHeadersEnabled ? ConvertHeaders(httpRequest.Headers) : null
			};
		}

		private static string GetHttpVersion(string protocolString)
		{
			switch (protocolString)
			{
				case "HTTP/1.0":
					return "1.0";
				case "HTTP/1.1":
					return "1.1";
				case "HTTP/2.0":
					return "2.0";
				default:
					return protocolString.Replace("HTTP/", string.Empty);
			}
		}

		private static Dictionary<string, string> ConvertHeaders(NameValueCollection httpHeaders)
		{
			var convertedHeaders = new Dictionary<string, string>();
			foreach (var headerName in httpHeaders.AllKeys)
			{
				var headerValue = httpHeaders.Get(headerName);
				if (headerValue != null) convertedHeaders.Add(headerName, headerValue);
			}
			return convertedHeaders;
		}

		private void ProcessEndRequest(object eventSender)
		{
			var httpApp = (HttpApplication)eventSender;
			var httpCtx = httpApp.Context;
			var httpResponse = httpCtx.Response;

			_currentTransaction.Result = Transaction.StatusCodeToResult("HTTP", httpResponse.StatusCode);

			if (_currentTransaction.IsSampled)
			{
				FillSampledTransactionContextResponse(httpResponse, _currentTransaction);
				FillSampledTransactionContextUser(httpCtx, _currentTransaction);
			}

			_currentTransaction?.End();
			_currentTransaction = null;
		}

		private void FillSampledTransactionContextResponse(HttpResponse httpResponse, ITransaction transaction) =>
			transaction.Context.Response = new Response
			{
				Finished = true,
				StatusCode = httpResponse.StatusCode,
				Headers = IsCaptureHeadersEnabled ? ConvertHeaders(httpResponse.Headers) : null
			};

		private void FillSampledTransactionContextUser(HttpContext httpCtx, Transaction transaction)
		{
			var userIdentity = httpCtx.User?.Identity;
			if (userIdentity == null || !userIdentity.IsAuthenticated) return;

			transaction.Context.User = new User { UserName = userIdentity.Name };

			Logger.Debug()?.Log("Captured user - {CapturedUser}", transaction.Context.User);
		}

		private static string FindAspNetVersion()
		{
			string result;
			try
			{
				// We would like to report the same ASP.NET version as the one printed at the bottom of the error page
				// (see https://github.com/microsoft/referencesource/blob/master/System.Web/ErrorFormatter.cs#L431)
				// It is stored in VersionInfo.EngineVersion
				// (see https://github.com/microsoft/referencesource/blob/3b1eaf5203992df69de44c783a3eda37d3d4cd10/System.Web/Util/versioninfo.cs#L91)
				// which is unfortunately an internal property of an internal class in System.Web assembly so we use reflection to get it
				var versionInfoType = typeof(HttpRuntime).Assembly.GetType("System.Web.Util.VersionInfo");
				var engineVersionProperty = versionInfoType.GetProperty("EngineVersion",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				result = (string)engineVersionProperty.GetValue(null);
			}
			catch (Exception ex)
			{
				result = "N/A";
				Logger.Error()?.LogException(ex, $"Failed to obtain ASP.NET version - {result} will be used");
			}

			return result;
		}
	}
}
