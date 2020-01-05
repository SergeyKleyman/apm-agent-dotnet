using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Elastic.Apm.Helpers;
using Elastic.Apm.Tests.TestHelpers;

// ReSharper disable PossibleMultipleEnumeration

namespace Elastic.Apm.PerfTests
{
	public class ConcurrentDictionaryVsThreadLocal
	{
		private const string PreparedStringsPrefix = @"Server=myServerAddress;Database=myDataBase;User Id=myUsername;Password=myPassword;Index=";
		private static readonly string[] PreparedStrings = PrepareStrings();

		private interface IDictionaryToTest<in TKey, TValue>
		{
			int Count { get; }

			bool TryAdd(TKey key, TValue value);

			bool TryGetValue(TKey key, out TValue value);
		}

		private static string[] PrepareStrings()
		{
			var result = new string[1_000];
			for (var i = 0; i < result.Length; ++i) result[i] = PreparedStringsPrefix + i;
			return result;
		}

		private static string GetPreparedString(int i) => PreparedStrings[i];

		public class AddOnceReadManyTimes
		{
			private const int MaxDictionarySize = 100;

			private const int NumberOfTimesToRead = 10_000_000;
			private SimpleMultiThreadExecutor _multiThreadExecutor;

			// [Params(MaxDictionarySize, MaxDictionarySize * 2, MaxDictionarySize * 10)]
			[Params(MaxDictionarySize, MaxDictionarySize * 2)]
			public int NumberOfDifferentKeys { get; set; }

			[ParamsSource(nameof(NumberOfThreadsVariants))]
			public int NumberOfThreads { get; set; }

			[ParamsAllValues]
			public bool VerifyCorrectness { get; set; }

			public IEnumerable<int> NumberOfThreadsVariants() => MultiThreadPerfTestsUtils.NumberOfThreadsVariants();

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

			[Benchmark(Baseline = true)]
			public void ThreadLocalDictionary() =>
				TestImpl(
					new ThreadLocalDictionary<string, int>()
					, addedCount => addedCount == MaxDictionarySize * NumberOfThreads);

			[Benchmark]
			public void ConcurrentDictionaryBuiltinCount() =>
				TestImpl(
					new ConcurrentDictionaryBuiltinCount<string, int>()
					, addedCount => MaxDictionarySize <= addedCount && addedCount < MaxDictionarySize + NumberOfThreads);

			[Benchmark]
			public void ConcurrentDictionarySeparateCount() =>
				TestImpl(
					new ConcurrentDictionarySeparateCount<string, int>()
					, addedCount => MaxDictionarySize <= addedCount && addedCount < MaxDictionarySize + NumberOfThreads);

			private void TestImpl(IDictionaryToTest<string, int> dict, Func<int, bool> verifyAddedCount)
			{
				var addedCounts = _multiThreadExecutor.Execute(_ =>
				{
					var addedCount = 0;
					NumberOfTimesToRead.Repeat(i =>
					{
						var expectedValue = i % NumberOfDifferentKeys;
						var key = GetPreparedString(expectedValue);
						if (dict.TryGetValue(key, out var actualValue))
						{
							if (VerifyCorrectness && actualValue != expectedValue)
								throw new AssertionFailedException($"i: {i}. NumberOfDifferentKeys: {NumberOfDifferentKeys}.");
						}
						else if (dict.Count < MaxDictionarySize)
						{
							var wasAdded = dict.TryAdd(key, expectedValue);
							if (VerifyCorrectness && wasAdded) ++addedCount;
						}
					});
					return addedCount;
				});

				var actualAddedCount = addedCounts.Sum();
				if (VerifyCorrectness && !verifyAddedCount(actualAddedCount))
					throw new AssertionFailedException($"actualAddedCount: {actualAddedCount}. addedCounts: {string.Join(", ", addedCounts)}.");
			}
		}

		private class ThreadLocalDictionary<TKey, TValue> : IDictionaryToTest<TKey, TValue>
		{
			private readonly ThreadLocal<Dictionary<TKey, TValue>> _dict =
				new ThreadLocal<Dictionary<TKey, TValue>>(() => new Dictionary<TKey, TValue>());

			public int Count => _dict.Value.Count;

			public bool TryGetValue(TKey key, out TValue value) => _dict.Value.TryGetValue(key, out value);

			public bool TryAdd(TKey key, TValue value) => _dict.Value.TryAdd(key, value);
		}

		private class ConcurrentDictionaryBuiltinCount<TKey, TValue> : IDictionaryToTest<TKey, TValue>
		{
			private readonly ConcurrentDictionary<TKey, TValue> _dict = new ConcurrentDictionary<TKey, TValue>();

			public int Count => _dict.Count;

			public bool TryGetValue(TKey key, out TValue value) => _dict.TryGetValue(key, out value);

			public bool TryAdd(TKey key, TValue value) => _dict.TryAdd(key, value);
		}

		private class ConcurrentDictionarySeparateCount<TKey, TValue> : IDictionaryToTest<TKey, TValue>
		{
			private readonly ConcurrentDictionary<TKey, TValue> _dict = new ConcurrentDictionary<TKey, TValue>();
			private int _count;

			public int Count => Volatile.Read(ref _count);

			public bool TryGetValue(TKey key, out TValue value) => _dict.TryGetValue(key, out value);

			public bool TryAdd(TKey key, TValue value)
			{
				var retVal = _dict.TryAdd(key, value);
				if (retVal) Interlocked.Increment(ref _count);
				return retVal;
			}
		}
	}
}
