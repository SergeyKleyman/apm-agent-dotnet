using System;
using System.Collections.Generic;
using Elastic.Apm.Logging;

namespace Elastic.Apm.Config
{
	internal class EnvironmentConfigurationReader : AbstractConfigurationReader, IConfigurationReader
	{
		internal const string Origin = "environment";

		private readonly Lazy<double> _spanFramesMinDurationInMilliseconds;

		private readonly Lazy<int> _stackTraceLimit;

		public EnvironmentConfigurationReader(IApmLogger logger = null) : base(logger)
		{
			_spanFramesMinDurationInMilliseconds
				= new Lazy<double>(() =>
					ParseSpanFramesMinDurationInMilliseconds(Read(ConfigConsts.EnvVarNames.StackTraceLimit)));

			_stackTraceLimit = new Lazy<int>(() => ParseStackTraceLimit(Read(ConfigConsts.EnvVarNames.StackTraceLimit)));
		}

		public string CaptureBody => ParseCaptureBody(Read(ConfigConsts.EnvVarNames.CaptureBody));

		public List<string> CaptureBodyContentTypes =>
			ParseCaptureBodyContentTypes(Read(ConfigConsts.EnvVarNames.CaptureBodyContentTypes), CaptureBody);

		public bool CaptureHeaders => ParseCaptureHeaders(Read(ConfigConsts.EnvVarNames.CaptureHeaders));

		public TimeSpan DiscardEventAge => ParseFlushInterval(Read(ConfigConsts.EnvVarNames.DiscardEventAge));

		public TimeSpan FlushInterval => ParseFlushInterval(Read(ConfigConsts.EnvVarNames.FlushInterval));

		public LogLevel LogLevel => ParseLogLevel(Read(ConfigConsts.EnvVarNames.LogLevel));

		public int MaxBatchEventCount => ParseMaxBatchEventCount(Read(ConfigConsts.EnvVarNames.MaxBatchEventCount));

		public int MaxQueueEventCount => ParseMaxQueueEventCount(Read(ConfigConsts.EnvVarNames.MaxQueueEventCount));

		public double MetricsIntervalInMilliseconds => ParseMetricsInterval(Read(ConfigConsts.EnvVarNames.MetricsInterval));

		public string SecretToken => ParseSecretToken(Read(ConfigConsts.EnvVarNames.SecretToken));

		public IReadOnlyList<Uri> ServerUrls => ParseServerUrls(Read(ConfigConsts.EnvVarNames.ServerUrls));

		public string ServiceName => ParseServiceName(Read(ConfigConsts.EnvVarNames.ServiceName));

		public string ServiceVersion => ParseServiceVersion(Read(ConfigConsts.EnvVarNames.ServiceVersion));

		public double SpanFramesMinDurationInMilliseconds => _spanFramesMinDurationInMilliseconds.Value;

		public int StackTraceLimit => _stackTraceLimit.Value;

		public double TransactionSampleRate => ParseTransactionSampleRate(Read(ConfigConsts.EnvVarNames.TransactionSampleRate));

		private static ConfigurationKeyValue Read(string key) =>
			new ConfigurationKeyValue(key, Environment.GetEnvironmentVariable(key)?.Trim(), Origin);
	}
}
