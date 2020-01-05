using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Elastic.Apm.Helpers;
using Elastic.Apm.Tests.TestHelpers;

// ReSharper disable AccessToDisposedClosure

namespace Elastic.Apm.PerfTests
{
	internal class SimpleMultiThreadExecutor : IDisposable
	{
		private readonly int _numberOfThreads;
		private readonly Exception[] _exceptions;
		private readonly Barrier _jobFinished;
		private readonly Barrier _jobToExecuteIsReady;
		private readonly Thread[] _threads;
		private Action<int> _currentJob;
		private bool _isDisposed;

		internal SimpleMultiThreadExecutor(int numberOfThreads)
		{
			_numberOfThreads = numberOfThreads;
			_exceptions = new Exception[numberOfThreads];
			_jobToExecuteIsReady = new Barrier(numberOfThreads + 1);
			_jobFinished = new Barrier(numberOfThreads + 1);
			using var startBarrier = new Barrier(numberOfThreads + 1);
			_threads = Enumerable.Range(0, numberOfThreads).Select(i => new Thread(() => EachThreadDo(i, startBarrier))).ToArray();
			foreach (var thread in _threads) thread.Start();
			startBarrier.SignalAndWait();
		}

		internal IReadOnlyList<TResult> Execute<TResult>(Func<int, TResult> execAtEachThread)
		{
			if (_isDisposed) throw new ObjectDisposedException(nameof(SimpleMultiThreadExecutor));

			var results = new TResult[_numberOfThreads];
			_currentJob = threadIndex => results[threadIndex] = execAtEachThread(threadIndex);
			_jobToExecuteIsReady.SignalAndWait();
			_jobFinished.SignalAndWait();
			_currentJob = null;

			var threadWithExceptionIndexes =
				_exceptions.ZipWithIndex().Where(indexAndEx => indexAndEx.Item2 != null).Select(indexAndEx => indexAndEx.Item1).ToArray();

			if (! threadWithExceptionIndexes.IsEmpty())
			{
				throw new AggregateException("Exception was thrown out of at least one thread's action."
					+ $" Number of threads that thrown exceptions: {threadWithExceptionIndexes.Length}, "
					+ $" their indexes: {string.Join(", ", threadWithExceptionIndexes)}."
					+ $" Total number of threads: {_numberOfThreads}."
					, _exceptions.Where(ex => ex != null));
			}

			return results;
		}

		internal void Execute(Action<int> execAtEachThread) => Execute<object>(threadIndex => { execAtEachThread(threadIndex); return null; });

		public void Dispose()
		{
			if (_isDisposed) return;
			_isDisposed = true;
			_currentJob = null;
			_jobToExecuteIsReady.SignalAndWait();
			foreach (var thread in _threads) thread.Join();
			_jobToExecuteIsReady.Dispose();
			_jobFinished.Dispose();
		}

		private void EachThreadDo(int threadIndex, Barrier startBarrier)
		{
			startBarrier.SignalAndWait();
			while (true)
			{
				_jobToExecuteIsReady.SignalAndWait();
				if (_currentJob == null) break;
				try
				{
					_currentJob(threadIndex);
				}
				catch (Exception ex)
				{
					_exceptions[threadIndex] = ex;
				}
				_jobFinished.SignalAndWait();
			}
		}
	}
}
