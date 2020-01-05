using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Elastic.Apm.Helpers;
using FluentAssertions;

namespace Elastic.Apm.Tests.TestHelpers
{
	internal static class MultiThreadsTestUtils
	{
		internal static int NumberOfThreadsForTest => Math.Max(Environment.ProcessorCount, 2);

		internal static IList<TResult> TestOnThreads<TResult>(Func<int, TResult> threadAction) =>
			TestOnThreads(NumberOfThreadsForTest, threadAction);

		internal static TResult[] TestOnThreads<TResult>(int numberOfThreads, Func<int, TResult> threadAction)
		{
			numberOfThreads.Should().BeGreaterThan(1);

			using var startBarrier = new Barrier(numberOfThreads);
			var results = new TResult[numberOfThreads];
			var exceptions = new Exception[numberOfThreads];
			var threads = Enumerable.Range(0, numberOfThreads).Select(i => new Thread(() => EachThreadDo(i))).ToList();

			foreach (var thread in threads) thread.Start();
			foreach (var thread in threads) thread.Join();

			var threadWithExceptionIndexes =
				exceptions.ZipWithIndex().Where(indexAndEx => indexAndEx.Item2 != null).Select(indexAndEx => indexAndEx.Item1).ToArray();

			if (! threadWithExceptionIndexes.IsEmpty())
			{
				throw new AggregateException("Exception was thrown out of at least one thread's action."
					+ $" Number of threads that thrown exceptions: {threadWithExceptionIndexes.Length}, "
					+ $" their indexes: {string.Join(", ", threadWithExceptionIndexes)}."
					+ $" Total number of threads: {numberOfThreads}."
					, exceptions.Where(ex => ex != null));
			}

			return results;

			void EachThreadDo(int threadIndex)
			{
				// ReSharper disable once AccessToDisposedClosure
				startBarrier.SignalAndWait();

				try
				{
					results[threadIndex] = threadAction(threadIndex);
				}
				catch (Exception ex)
				{
					exceptions[threadIndex] = ex;
				}
			}
		}
	}
}
