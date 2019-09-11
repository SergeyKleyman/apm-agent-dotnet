using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Api;
using Elastic.Apm.Config;
using Elastic.Apm.Logging;
using Elastic.Apm.Metrics;
using Elastic.Apm.Model;
using Elastic.Apm.Report.Serialization;

namespace Elastic.Apm.Report
{
	internal class BatchSender : IBatchSender
	{
		private static readonly int DnsTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;

		private readonly HttpClient _httpClient;

		private readonly IApmLogger _logger;
		private readonly Metadata _metadata;

		private readonly PayloadItemSerializer _payloadItemSerializer = new PayloadItemSerializer();

		public BatchSender(IApmLogger logger, IConfigurationReader configurationReader, Service service, Api.System system,
			HttpMessageHandler handler = null
		)
		{
			_metadata = new Metadata { Service = service, System = system };

			_logger = logger?.Scoped(nameof(BatchSender));

			var serverUrlBase = configurationReader.ServerUrls.First();
			var servicePoint = ServicePointManager.FindServicePoint(serverUrlBase);

			servicePoint.ConnectionLeaseTimeout = DnsTimeout;
			servicePoint.ConnectionLimit = 20;

			_httpClient = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = serverUrlBase };
			_httpClient.DefaultRequestHeaders.UserAgent.Add(
				new ProductInfoHeaderValue($"elasticapm-{Consts.AgentName}", AdaptUserAgentValue(service.Agent.Version)));
			_httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("System.Net.Http",
				AdaptUserAgentValue(typeof(HttpClient).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version)));
			_httpClient.DefaultRequestHeaders.UserAgent.Add(
				new ProductInfoHeaderValue(AdaptUserAgentValue(service.Runtime.Name), AdaptUserAgentValue(service.Runtime.Version)));

			if (configurationReader.SecretToken != null)
			{
				_httpClient.DefaultRequestHeaders.Authorization =
					new AuthenticationHeaderValue("Bearer", configurationReader.SecretToken);
			}

			// Replace invalid characters by underscore. All invalid characters can be found at
			// https://github.com/dotnet/corefx/blob/e64cac6dcacf996f98f0b3f75fb7ad0c12f588f7/src/System.Net.Http/src/System/Net/Http/HttpRuleParser.cs#L41
			string AdaptUserAgentValue(string value)
			{
				return Regex.Replace(value, "[ /()<>@,:;={}?\\[\\]\"\\\\]", "_");
			}
		}

		public async Task SendBatchAsync(IEnumerable<object> batch, CancellationToken cancellationToken)
		{
			try
			{
				var metadataJson = _payloadItemSerializer.SerializeObject(_metadata);
				var ndjson = new StringBuilder();
				ndjson.AppendLine("{\"metadata\": " + metadataJson + "}");

				// ReSharper disable once PossibleMultipleEnumeration
				foreach (var item in batch)
				{
					var serialized = _payloadItemSerializer.SerializeObject(item);
					switch (item)
					{
						case Transaction _:
							ndjson.AppendLine("{\"transaction\": " + serialized + "}");
							break;
						case Span _:
							ndjson.AppendLine("{\"span\": " + serialized + "}");
							break;
						case Error _:
							ndjson.AppendLine("{\"error\": " + serialized + "}");
							break;
						case MetricSet _:
							ndjson.AppendLine("{\"metricset\": " + serialized + "}");
							break;
					}
					_logger?.Trace()?.Log("Serialized item to send: {ItemToSend} as {SerializedItemToSend}", item, serialized);
				}

				var content = new StringContent(ndjson.ToString(), Encoding.UTF8, "application/x-ndjson");

				var result = await _httpClient.PostAsync(Consts.IntakeV2Events, content, cancellationToken);

				if (result != null && !result.IsSuccessStatusCode)
					_logger?.Error()?.Log("Failed sending event. {ApmServerResponse}", await result.Content.ReadAsStringAsync());
				else
				{
					// ReSharper disable once PossibleMultipleEnumeration
					_logger?.Debug()
						?.Log($"Sent items to server: {Environment.NewLine}{{items}}", string.Join($",{Environment.NewLine}", batch));
				}
			}
			catch (Exception e)
			{
				// ReSharper disable once PossibleMultipleEnumeration
				_logger?.Warning()
					?.LogException(
						e, "Failed sending events. Following events were not transferred successfully to the server ({ApmServerUrl}):\n{items}",
						_httpClient.BaseAddress,
						string.Join($",{Environment.NewLine}", batch));
			}
		}

		internal class Metadata
		{
			// ReSharper disable once UnusedAutoPropertyAccessor.Global - used by Json.Net
			public Service Service { get; set; }

			public Api.System System { get; set; }
		}
	}
}
