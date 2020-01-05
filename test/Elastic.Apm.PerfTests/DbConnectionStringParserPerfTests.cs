using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Elastic.Apm.Api;
using Elastic.Apm.Helpers;
using Elastic.Apm.Tests.Mocks;

namespace Elastic.Apm.PerfTests
{
	public class DbConnectionStringParserPerfTests
	{
		private static readonly string[] PreparedConnectionStrings = PrepareConnectionStrings();
		private SimpleMultiThreadExecutor _multiThreadExecutor;

		private static string[] PrepareConnectionStrings()
		{
			var result = new string[DbConnectionStringParser.MaxCacheSize * 10];
			for (var i = 0; i < result.Length; ++i)
				result[i] = @"Server=myServerAddress;Database=myDataBase;User Id=myUsername;Password=myPassword;Index=" + i;
			return result;
		}

		private static string GetPreparedConnectionString(int i) => PreparedConnectionStrings[i % PreparedConnectionStrings.Length];

		[ParamsAllValues]
		// [Params(ConnectionStringDistribution.FewDifferent)]
		// [Params(ConnectionStringDistribution.ManyDifferentFewFrequentlyUsed)]
		public ConnectionStringDistributionT ConnectionStringDistribution { get; set; }

		[ParamsAllValues]
		// [Params(DbConnectionStringParser.ThreadLocalCache.Strategy.FirstMax)]
		public DbConnectionStringParser.ThreadLocalCache.Strategy CacheStrategy { get; set; }

		[ParamsSource(nameof(NumberOfThreadsVariants))]
		public int NumberOfThreads { get; set; }

		private const int NumberOfIterations = 100_000;

		[GlobalSetup]
		public void GlobalSetup()
		{
			_multiThreadExecutor = new SimpleMultiThreadExecutor(NumberOfThreads);
		}

		[GlobalCleanup]
		public void GlobalCleanup()
		{
			_multiThreadExecutor.Dispose();
		}

		public IEnumerable<int> NumberOfThreadsVariants() => MultiThreadPerfTestsUtils.NumberOfThreadsVariants();

		public enum ConnectionStringDistributionT
		{
			FewDifferent,
			ManyDifferentFewFrequent,
			AllDifferent
		}

		[Benchmark(Baseline = true)]
		public void ThreadLocal() =>
			TestImpl(new DbConnectionStringParser(new NoopLogger(), new DbConnectionStringParser.ThreadLocalCache(CacheStrategy)));

		[Benchmark]
		public void ConcurrentDictionary() =>
			TestImpl(new DbConnectionStringParser(new NoopLogger(), new ConcurrentDictionaryCache(CacheStrategy)));

		private void TestImpl(DbConnectionStringParser dbConnectionStringParser)
		{
			_multiThreadExecutor.Execute(threadIndex =>
			{
				foreach (var connectionString in SelectConnectionStrings())
				{
					var destination = dbConnectionStringParser.ExtractDestination(connectionString);
					if (destination.Address != "myServerAddress")
						throw new AssertionFailedException($"connectionString: `{connectionString}'. destination: {destination}.");
				}
			});
		}

		private IEnumerable<string> SelectConnectionStrings()
		{
			switch (ConnectionStringDistribution)
			{
				case ConnectionStringDistributionT.FewDifferent:
					for (var i = 0; i < NumberOfIterations; ++i) yield return GetPreparedConnectionString(i % DbConnectionStringParser.MaxCacheSize);
					yield break;

				case ConnectionStringDistributionT.AllDifferent:
					for (var i = 0; i < NumberOfIterations; ++i) yield return GetPreparedConnectionString(i);
					yield break;

				case ConnectionStringDistributionT.ManyDifferentFewFrequent:
					for (var i = 0; i < NumberOfIterations; ++i)
					{
						if (i % 10 == 0)
							yield return GetPreparedConnectionString(i / 10 % DbConnectionStringParser.MaxCacheSize);
						else
							yield return GetPreparedConnectionString(i);
					}
					yield break;

				default: throw new ArgumentOutOfRangeException();
			}
		}

		private class ConcurrentDictionaryCache : DbConnectionStringParser.ICache
		{
			private readonly ConcurrentDictionary<string, Destination> _dict = new ConcurrentDictionary<string, Destination>();
			private volatile int _count;

			private readonly DbConnectionStringParser.ThreadLocalCache.Strategy _strategy;

			internal ConcurrentDictionaryCache(DbConnectionStringParser.ThreadLocalCache.Strategy strategy)
			{
				_strategy = strategy;
			}

			public bool TryGetValue(string connectionString, out Destination destination) =>
				_dict.TryGetValue(connectionString, out destination);

			public void Add(string connectionString, Destination destination)
			{
				if (_count < DbConnectionStringParser.MaxCacheSize)
				{
					if (_dict.TryAdd(connectionString, destination)) Interlocked.Increment(ref _count);
					return;
				}

				switch (_strategy)
				{
					case DbConnectionStringParser.ThreadLocalCache.Strategy.FirstMax:
						return;

					case DbConnectionStringParser.ThreadLocalCache.Strategy.ResetWhenMax:
						_dict.Clear();
						_dict.TryAdd(connectionString, destination);
						return;

					default: throw new ArgumentOutOfRangeException();
				}
			}
		}
	}
}
