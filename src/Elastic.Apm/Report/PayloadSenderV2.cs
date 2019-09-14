using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Api;
using Elastic.Apm.Config;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;

namespace Elastic.Apm.Report
{
	/// <summary>
	/// Responsible for sending the data to the server. Implements Intake V2.
	/// Each instance creates its own thread to do the work. Therefore, instances should be reused if possible.
	/// </summary>
	internal class PayloadSenderV2 : IPayloadSender, IDisposable
	{
		internal const string ThreadName = "ElasticApmPayloadSender";

		internal readonly Api.System System;

		private readonly IBatchSender _batchSender;

		private readonly CancellationTokenSource _cancellationTokenSource;

		private readonly EventsQueue _eventsQueue;

		private readonly AgentSpinLock _isDisposeStarted = new AgentSpinLock();

		private readonly IApmLogger _logger;

		private readonly int _maxBatchEventCount;

		private readonly SingleThreadTaskScheduler _singleThreadTaskScheduler;

		internal PayloadSenderV2(IApmLogger logger, IConfigurationReader configurationReader, Service service, Api.System system,
			IBatchSender batchSender = null, IAgentTimer agentTimer = null
		)
		{
			_logger = logger?.Scoped(nameof(PayloadSenderV2));

			System = system;

			_cancellationTokenSource = new CancellationTokenSource();
			_singleThreadTaskScheduler = new SingleThreadTaskScheduler(logger, _cancellationTokenSource.Token);

			_maxBatchEventCount = configurationReader.MaxBatchEventCount;
			var maxQueueEventCount = configurationReader.MaxQueueEventCount;
			if (maxQueueEventCount < _maxBatchEventCount)
			{
				_logger?.Error()
					?.Log(
						"MaxQueueEventCount is less than MaxBatchEventCount - using MaxBatchEventCount as MaxQueueEventCount."
						+ " MaxQueueEventCount: {MaxQueueEventCount}."
						+ " MaxBatchEventCount: {MaxBatchEventCount}.",
						configurationReader.MaxQueueEventCount, configurationReader.MaxBatchEventCount);

				maxQueueEventCount = _maxBatchEventCount;
			}
			_eventsQueue = new EventsQueue(logger, maxQueueEventCount, configurationReader.DiscardEventAge,
				_maxBatchEventCount, configurationReader.FlushInterval, agentTimer ?? new AgentTimer());

			_batchSender = batchSender ?? new BatchSender(logger, configurationReader, service, system);

			Task.Factory.StartNew(
				() =>
				{
#pragma warning disable 4014
					DoWork();
#pragma warning restore 4014
				}, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, _singleThreadTaskScheduler);
		}

		internal Thread Thread => _singleThreadTaskScheduler.Thread;

		public void QueueTransaction(ITransaction transaction) => QueueEvent(transaction, transaction.Id, "Transaction");

		public void QueueSpan(ISpan span) => QueueEvent(span, span.Id, "Span");

		public void QueueMetrics(IMetricSet metricSet) => QueueEvent(metricSet, TimeUtils.FormatTimestampForLog(metricSet.TimeStamp), "MetricSet");

		public void QueueError(IError error) => QueueEvent(error, error.Id, "Error");

		private void QueueEvent(object eventObj, string dbgEventObjId, string dbgEventKind)
		{
			ThrowIfDisposed();

			_eventsQueue.Enqueue(eventObj, dbgEventObjId, dbgEventKind);
		}

		private async Task DoWork()
		{
			var batchToSend = new List<object>( /* capacity: */ _maxBatchEventCount);
			while (true)
			{
				await _eventsQueue.ReceiveAsync(batchToSend, _cancellationTokenSource.Token);
				await _batchSender.SendBatchAsync(batchToSend, _cancellationTokenSource.Token);
				batchToSend.Clear();
			}
			// ReSharper disable once FunctionNeverReturns
		}

		public void Dispose()
		{
			var isAcquiredByThisCall = _isDisposeStarted.TryAcquire();
			if (!isAcquiredByThisCall)
			{
				_logger.Debug()?.Log("Dispose called but there was already one started before this call - just exiting");
				return;
			}

			_logger.Debug()?.Log("Starting Dispose");

			_logger.Debug()?.Log("Signalling _cancellationTokenSource");
			_cancellationTokenSource.Cancel();

			_logger.Debug()?.Log("Waiting for _singleThreadTaskScheduler thread `{ThreadName}' to exit", _singleThreadTaskScheduler.Thread.Name);
			_singleThreadTaskScheduler.Thread.Join();

			_logger.Debug()?.Log("_singleThreadTaskScheduler thread exited - disposing of _cancellationTokenSource and exiting");

			_cancellationTokenSource.Dispose();
		}

		private void ThrowIfDisposed()
		{
			if (_isDisposeStarted.IsAcquired)
				throw new ObjectDisposedException( /* objectName: */ nameof(PayloadSenderV2));
		}

		//Credit: https://stackoverflow.com/a/30726903/1783306
		private sealed class SingleThreadTaskScheduler : TaskScheduler
		{
			[ThreadStatic]
			private static bool _isExecuting;

			private readonly IApmLogger _logger;
			private readonly CancellationToken _cancellationToken;

			private readonly BlockingCollection<Task> _taskQueue;

			public SingleThreadTaskScheduler(IApmLogger logger, CancellationToken cancellationToken)
			{
				_logger = logger?.Scoped(nameof(SingleThreadTaskScheduler));
				_cancellationToken = cancellationToken;
				_taskQueue = new BlockingCollection<Task>();
				Thread = new Thread(RunOnCurrentThread) { Name = ThreadName, IsBackground = true };
				Thread.Start();
			}

			internal Thread Thread { get; }

			private void RunOnCurrentThread()
			{
				_logger.Debug()?.Log("`{ThreadName}' thread started", Thread.CurrentThread.Name);

				_isExecuting = true;

				try
				{
					foreach (var task in _taskQueue.GetConsumingEnumerable(_cancellationToken)) TryExecuteTask(task);

					_logger.Debug()?.Log("`{ThreadName}' thread is about to exit normally", Thread.CurrentThread.Name);
				}
				catch (OperationCanceledException ex)
				{
					_logger.Debug()
						?.LogException(ex, "`{ThreadName}' thread is about to exit because it was cancelled, which is expected on shutdown",
							Thread.CurrentThread.Name);
				}
				catch (Exception ex)
				{
					_logger.Error()?.LogException(ex, "`{ThreadName}' thread is about to exit because of exception", Thread.CurrentThread.Name);
				}
				finally
				{
					_isExecuting = false;
				}
			}

			protected override IEnumerable<Task> GetScheduledTasks() => null;

			protected override void QueueTask(Task task) => _taskQueue.Add(task, _cancellationToken);

			protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
			{
				// We'd need to remove the task from queue if it was already queued.
				// That would be too hard.
				if (taskWasPreviouslyQueued) return false;

				return _isExecuting && TryExecuteTask(task);
			}
		}

		private class EventsQueue
		{
			private readonly IAgentTimer _agentTimer;
			private readonly TimeSpan _discardEventAge;
			private readonly TimeSpan _flushInterval;

			private readonly object _lock = new object();

			private readonly IApmLogger _logger;
			private readonly int _maxBatchEventCount;
			private readonly int _maxQueueEventCount;
			private readonly Queue<QueuedEvent> _queue;

			internal EventsQueue(IApmLogger logger, int maxQueueEventCount, TimeSpan discardEventAge, int maxBatchEventCount, TimeSpan flushInterval,
				IAgentTimer agentTimer
			)
			{
				_logger = logger?.Scoped($"{nameof(PayloadSenderV2)}.{nameof(EventsQueue)}");
				_agentTimer = agentTimer;

				_maxQueueEventCount = maxQueueEventCount;
				_maxBatchEventCount = maxBatchEventCount;
				_discardEventAge = discardEventAge;
				_flushInterval = flushInterval;

				_queue = new Queue<QueuedEvent>(_maxQueueEventCount);
			}

			private TaskCompletionSource<object> _pendingReceiveTcs;

			private void TryToDequeueDataToSend(AgentTimeInstant now, List<object> listToFill)
			{
				Assertion.IfEnabled?.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");
				Assertion.IfEnabled?.That(listToFill.IsEmpty(),
					$"{nameof(listToFill)} should be empty. {nameof(listToFill)}.Count: {listToFill.Count}");

				if (_queue.Count >= _maxBatchEventCount)
				{
					for (var i = 0; i < _maxBatchEventCount; ++i) listToFill.Add(_queue.Dequeue().EventObject);
					_logger.Trace()
						?.Log("Filled a batch to send (queue had at least MaxBatchEventCount events)."
							+ " Batch size: {BatchSize}."
							+ " Current state: {EventsQueueCurrentState}.",
							listToFill.Count,
							DbgCurrentStateToString());
					return;
				}

				for (var i = 0; i < _maxBatchEventCount && ! _queue.IsEmpty() ; ++i)
				{
					if (_queue.Peek().WhenEnqueued + _flushInterval > now) break;

					listToFill.Add(_queue.Dequeue().EventObject);
				}

				if (listToFill.IsEmpty())
				{
					_logger.Trace()
						?.Log("There is no data that should be sent. Current state: {EventsQueueCurrentState}.", DbgCurrentStateToString());
				}
				else
				{
					_logger.Trace()
						?.Log("Filled a batch to send (queue had events ready to be flushed)."
							+ " Batch size: {BatchSize}."
							+ " Current state: {EventsQueueCurrentState}.",
							listToFill.Count,
							DbgCurrentStateToString());
				}
			}

			private bool IsFull()
			{
				Assertion.IfEnabled?.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");

				return _queue.Count == _maxQueueEventCount;
			}

			internal void Enqueue(object eventObj, string dbgEventObjId, string dbgEventKind) =>
				DoUnderLock(() =>
				{
					var now = _agentTimer.Now;

					_logger.Trace()
						?.Log("Trying to add event to the queue..."
							+ " Event kind: {EventKind}, ID: {EventId}, event: {Event}."
							+ " Current state: {EventsQueueCurrentState}.",
							dbgEventKind, dbgEventObjId, eventObj, DbgCurrentStateToString());

					if (IsFull())
					{
						TryToFreeSpace(now);
						if (IsFull())
						{
							_logger.Trace()
								?.Log("Failed to add event to the queue - because the queue is full."
									+ " Event kind: {EventKind}, ID: {EventId}, event: {Event}."
									+ " Current state: {EventsQueueCurrentState}.",
									dbgEventKind, dbgEventObjId, eventObj, DbgCurrentStateToString());
							return;
						}
					}

					var queueWasEmptyBefore = _queue.IsEmpty();
					_queue.Enqueue(new QueuedEvent(eventObj, dbgEventObjId, dbgEventKind, /* whenEnqueued: */ now));
					_logger.Trace()
						?.Log("Added event to the queue."
							+ " Event kind: {EventKind}, ID: {EventId}, event: {Event}."
							+ " Current state: {EventsQueueCurrentState}.",
							dbgEventKind, dbgEventObjId, eventObj, DbgCurrentStateToString());

					if (_pendingReceiveTcs == null)
					{
						_logger.Trace()?.Log("There is no pending receive to notify");
						return;
					}

					if (queueWasEmptyBefore || _queue.Count >= _maxBatchEventCount)
					{
						_logger.Trace()?.Log("Notifying pending receive..."
							+ " queueWasEmptyBefore: {queueWasEmptyBefore}."
							+ " _queue.Count >= _maxBatchEventCount: {_queue.Count >= _maxBatchEventCount}."
							, queueWasEmptyBefore, _queue.Count >= _maxBatchEventCount);
						var trySetResultRetVal =_pendingReceiveTcs.TrySetResult(null);
						if (! trySetResultRetVal)
							_logger.Trace()?.Log("Pending receive was already notified before but it still didn't handle the previous notification");
						return;
					}

					_logger.Trace()?.Log("There was no change that requires notifying pending receive");
				});

			internal async Task ReceiveAsync(List<object> listToFill, CancellationToken cancellationToken)
			{
				if (! listToFill.IsEmpty())
				{
					throw new ArgumentException(
						$"{nameof(listToFill)} should be empty but instead {nameof(listToFill)}.Count is {listToFill.Count}",
						nameof(listToFill));
				}

				if (listToFill.Capacity < _maxBatchEventCount)
				{
					throw new ArgumentException(
						$"{nameof(listToFill)} capacity ({listToFill.Capacity}) should not be lower than MaxBatchEventCount ({_maxBatchEventCount})",
						nameof(listToFill));
				}

				var dbgIterationCount = 1L;
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();

					var now = _agentTimer.Now;
					var timeUntilNextFlush = TryReceive(listToFill, dbgIterationCount, now);
					if (!listToFill.IsEmpty()) return;

					using (cancellationToken.Register(() => _pendingReceiveTcs.TrySetCanceled(cancellationToken)))
					{
						if (timeUntilNextFlush.HasValue)
							// ReSharper disable once PossibleInvalidOperationException
							await _agentTimer.AwaitOrTimeout(_pendingReceiveTcs.Task, now, timeUntilNextFlush.Value, cancellationToken);
						else
							await _pendingReceiveTcs.Task;
					}

					++dbgIterationCount;
				}
			}

			private TimeSpan? TryReceive(List<object> listToFill, long dbgIterationCount, AgentTimeInstant now)
			{
				TimeSpan? timeUntilNextFlush;
				DoUnderLock(() =>
				{
					// ReSharper disable once AccessToModifiedClosure
					if (dbgIterationCount == 1)
						Assertion.IfEnabled?.That(_pendingReceiveTcs == null, "At most one ReceiveAsync should be in progress at any time");

					_logger.Trace()
						?.Log("Trying to receive data..."
							+ " Iteration #{IterationCount}."
							+ " Current state: {EventsQueueCurrentState}."
							, dbgIterationCount, DbgCurrentStateToString());

					TryToDequeueDataToSend(now, listToFill);
					if (!listToFill.IsEmpty())
					{
						_pendingReceiveTcs = null;
						return;
					}

					_pendingReceiveTcs = new TaskCompletionSource<object>();
					timeUntilNextFlush = CalcTimeLeftToNextFlush(now);

					_logger.Trace()
						?.Log("Awaiting data to become available..."
							+ " Iteration #{IterationCount}."
							+ " Time until next flush: {TimeUntilNextFlush}."
							+ " Current state: {EventsQueueCurrentState}.",
							dbgIterationCount,
							timeUntilNextFlush?.ToString() ?? "N/A (queue is empty)",
							DbgCurrentStateToString());
				});
				return timeUntilNextFlush;
			}

			private void TryToFreeSpace(AgentTimeInstant now)
			{
				Assertion.IfEnabled?.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");
				Assertion.IfEnabled?.That(IsFull(), nameof(TryToFreeSpace) + " should be called only if " + nameof(IsFull) + " returns true");

				while (!_queue.IsEmpty())
				{
					var oldestEvent = _queue.Peek();
					if (oldestEvent.WhenEnqueued + _discardEventAge > now)
					{
						_logger.Trace()
							?.Log("There are no more events that can be discarded to free up space in the queue."
								+ " now: {Now}."
								+ " Current state: {EventsQueueCurrentState}.",
								now,
								DbgCurrentStateToString());
						break;
					}

					_logger.Trace()
						?.Log("Discarding event because queue is full and event's age is larger than DiscardEventAge."
							+ " Queued event: {QueuedEvent}, age: {QueuedEventAge}."
							+ " now: {Now}."
							+ " Current state: {EventsQueueCurrentState}."
							+ " Queued event object: {Event}.",
							oldestEvent, now - oldestEvent.WhenEnqueued,
							now,
							DbgCurrentStateToString(),
							oldestEvent.EventObject);

					_queue.Dequeue();
				}
			}

			private string DbgCurrentStateToString()
			{
				Assertion.IfEnabled?.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");

				return new ToStringBuilder(/* className */ "")
				{
					{ $"{nameof(_queue)}.Count", _queue.Count },
					{ $"{nameof(_queue)}.Peek()", _queue.IsEmpty() ? "N/A (queue is empty)" : _queue.Peek().ToString() },
					{ $"{nameof(_maxQueueEventCount)}", _maxQueueEventCount },
					{ $"{nameof(_maxBatchEventCount)}", _maxBatchEventCount },
					{ $"{nameof(_discardEventAge)}", _discardEventAge },
					{ $"{nameof(_flushInterval)}", _flushInterval }
				}.ToString();
			}

			private TimeSpan? CalcTimeLeftToNextFlush(AgentTimeInstant now)
			{
				Assertion.IfEnabled?.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");

				if (_queue.IsEmpty()) return null;

				return _queue.Peek().WhenEnqueued + _flushInterval - now;
			}

			private void AssertValid()
			{
				if (!Assertion.IsEnabled) return;
				// ReSharper disable once PossibleInvalidOperationException
				var assert = Assertion.IfEnabled.Value;

				assert.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");

				assert.That(_queue.Count <= _maxQueueEventCount,
					$"_queue.Count <= _maxQueueEventCount. Current state: {DbgCurrentStateToString()}");

				if (Assertion.IsOnLevelEnabled) AssertValidOnLevel();

				void AssertValidOnLevel()
				{
					if (_queue.IsEmpty()) return;

					var prevQueuedEvent = _queue.Peek();
					var index = 0;
					// ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
					foreach (var currentQueuedEvent in _queue)
					{
						if (index == 0) continue;

						assert.That(prevQueuedEvent.WhenEnqueued <= currentQueuedEvent.WhenEnqueued,
							$"Events at indexes {index - 1} and {index} are not in ascending order by {nameof(QueuedEvent.WhenEnqueued)} time" +
							" as they should be");

						prevQueuedEvent = currentQueuedEvent;
						++index;
					}
				}
			}

			private void DoUnderLock(Action doAction)
			{
				lock (_lock)
				{
					AssertValid();
					try
					{
						doAction();
					}
					finally
					{
						AssertValid();
					}
				}
			}

			[DebuggerDisplay(nameof(WhenEnqueued) + " = {" + nameof(WhenEnqueued) + "}"
				+ ", " + nameof(_dbgEventKind) + " = {" + nameof(_dbgEventKind) + "}"
				+ ", " + nameof(_dbgEventObjectId) + " = {" + nameof(_dbgEventObjectId) + "}")]
			private readonly struct QueuedEvent
			{
				internal QueuedEvent(object eventObject, string dbgEventObjectId, string dbgEventKind, AgentTimeInstant whenEnqueued)
				{
					EventObject = eventObject;
					_dbgEventObjectId = dbgEventObjectId;
					_dbgEventKind = dbgEventKind;
					WhenEnqueued = whenEnqueued;
				}

				internal readonly object EventObject;
				private readonly string _dbgEventObjectId;
				private readonly string _dbgEventKind;
				internal readonly AgentTimeInstant WhenEnqueued;

				public override string ToString() => new ToStringBuilder(nameof(QueuedEvent))
				{
					{ nameof(WhenEnqueued), WhenEnqueued }, { nameof(_dbgEventKind), _dbgEventKind }, { nameof(_dbgEventObjectId), _dbgEventObjectId }
				}.ToString();
			}
		}
	}
}
