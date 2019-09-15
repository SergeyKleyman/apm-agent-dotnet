using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Tests.TestHelpers;
using FluentAssertions;

namespace Elastic.Apm.Tests.Mocks
{
	[DebuggerDisplay(nameof(_dbgName) + " = {" + nameof(_dbgName) + "}" + ", " + nameof(Now) + " = {" + nameof(Now) + "}")]
	internal class MockAgentTimer : IAgentTimerForTesting
	{
		private const string ThisClassName = nameof(MockAgentTimer);

		private readonly string _dbgName;
		private readonly DelayItems _delayItems = new DelayItems();
		private readonly AgentSpinLock _fastForwardSpinLock = new AgentSpinLock();

		private readonly IApmLogger _logger;

		internal MockAgentTimer(string dbgName = null, IApmLogger logger = null)
		{
			Now = new AgentTimeInstant(this, TimeSpan.Zero);
			_dbgName = dbgName ?? "#" + RuntimeHelpers.GetHashCode(this).ToString("X");
			_logger = logger == null ? (IApmLogger)new NoopLogger() : logger.Scoped($"{ThisClassName}-{_dbgName}");
		}

		public bool IsMockTime => true;

		public AgentTimeInstant Now
		{
			get => _delayItems.Now;

			private set => _delayItems.Now = value;
		}

		public IReadOnlyList<Task> PendingDelayTasks => _delayItems.DelayTasks;

		public int PendingDelayTasksCount => _delayItems.Count;

		public async Task Delay(AgentTimeInstant relativeToInstant, TimeSpan delay, CancellationToken cancellationToken = default)
		{
			var totalMilliseconds = (long)delay.TotalMilliseconds;
			if (totalMilliseconds < 0 || totalMilliseconds > int.MaxValue)
				throw new ArgumentOutOfRangeException(nameof(delay), $"Invalid {nameof(delay)} argument value: {delay}");

			relativeToInstant.IsCompatible(this).Should().BeTrue();

			cancellationToken.ThrowIfCancellationRequested();

			var whenToTrigger = relativeToInstant + delay;
			var triggerTcs = new TaskCompletionSource<object>();

			var (now, delayItemId) = _delayItems.Add(whenToTrigger, triggerTcs, cancellationToken);
			if (!delayItemId.HasValue)
			{
				_logger.Trace()?.Log($"Delay item already reached its trigger time. When to trigger: {whenToTrigger}. now: {now}.");
				return;
			}

			_logger.Trace()?.Log($"Added delay item. When to trigger: {whenToTrigger}. Delay item ID: {delayItemId.Value}");

			cancellationToken.Register(() => DelayCancelled(delayItemId.Value));

			await triggerTcs.Task;
		}

		internal void FastForward(TimeSpan timeSpanToFastForward, string dbgGoalDescription = null)
		{
			_logger.Debug()?.Log($"Fast forwarding... timeSpanToFastForward: {timeSpanToFastForward}, dbgGoalDescription: {dbgGoalDescription}");

			using (var acq = _fastForwardSpinLock.TryAcquireWithDisposable())
			{
				if (!acq.IsAcquired)
					throw new InvalidOperationException($"{nameof(FastForward)} should not be called while the previous call is still in progress");

				var targetNow = Now + timeSpanToFastForward;

				while (true)
				{
					var delayItem = _delayItems.RemoveEarliestToTrigger(targetNow);
					if (!delayItem.HasValue) break;

					Assertion.IfEnabled?.That(delayItem.Value.WhenToTrigger >= Now, "Delay item should not have past trigger time");
					Now = delayItem.Value.WhenToTrigger;
					_logger.Trace()?.Log($"Notifying delay item... Delay item ID: {delayItem.Value.Id}");
					delayItem.Value.TriggerTcs.SetResult(null);
				}

				Now = targetNow;
			}
		}

		public void WaitForTimeToPassAndUntil(TimeSpan timeSpan, Func<bool> untilCondition = null, string dbgGoalDescription = null)
		{
			FastForward(timeSpan, dbgGoalDescription);
			untilCondition?.Invoke().Should().BeTrue();
		}

		private void DelayCancelled(long delayItemId)
		{
			var delayItem = _delayItems.RemoveById(delayItemId);
			if (!delayItem.HasValue)
			{
				_logger.Debug()?.Log($"DelayItem with ID: {delayItemId} was not found (it's possible that it was already completed) - exiting");
				return;
			}

			_logger.Trace()?.Log($"Cancelling delay item... Delay item ID: {delayItem.Value.Id}");
			var cancelled = delayItem.Value.TriggerTcs.TrySetCanceled(delayItem.Value.CancellationToken);
			Assertion.IfEnabled?.That(cancelled,
				$"Delay item task should not be in any final state before we cancel it because it was in {nameof(_delayItems)} list");
		}

		public override string ToString() =>
			new ToStringBuilder(ThisClassName) { { nameof(_dbgName), _dbgName }, { nameof(Now), Now } }.ToString();

		private readonly struct DelayItem
		{
			internal readonly long Id;
			internal readonly AgentTimeInstant WhenToTrigger;
			internal readonly TaskCompletionSource<object> TriggerTcs;
			internal readonly CancellationToken CancellationToken;

			internal DelayItem(long id, AgentTimeInstant whenToTrigger, TaskCompletionSource<object> triggerTcs, CancellationToken cancellationToken)
			{
				Id = id;
				WhenToTrigger = whenToTrigger;
				TriggerTcs = triggerTcs;
				CancellationToken = cancellationToken;
			}
		}

		private class DelayItems
		{
			private readonly List<DelayItem> _items = new List<DelayItem>();
			private readonly object _lock = new object();
			private long _nextItemId = 1;
			private AgentTimeInstant _now;

			internal int Count => DoUnderLock(() => _items.Count);

			internal IReadOnlyList<Task> DelayTasks => DoUnderLock(() => _items.Select(d => d.TriggerTcs.Task).ToList());

			internal AgentTimeInstant Now
			{
				get => DoUnderLock(() =>
				{
					var localCopy = _now;
					return localCopy;
				});

				set => DoUnderLock(() => { _now = value; });
			}

			internal (AgentTimeInstant, long?) Add(AgentTimeInstant whenToTrigger, TaskCompletionSource<object> triggerTcs,
				CancellationToken cancellationToken
			) =>
				DoUnderLock(() =>
				{
					if (whenToTrigger <= _now) return (_now, (long?)null);

					var newItemId = _nextItemId++;

					var newItemIndex = _items.TakeWhile(item => whenToTrigger >= item.WhenToTrigger).Count();
					_items.Insert(newItemIndex, new DelayItem(newItemId, whenToTrigger, triggerTcs, cancellationToken));
					return (_now, newItemId);
				});

			internal DelayItem? RemoveById(long itemId) =>
				DoUnderLock<DelayItem?>(() =>
				{
					var index = _items.FindIndex(delayItem => delayItem.Id == itemId);
					if (index == -1) return null;

					if (Assertion.IsEnabled && index != _items.Count - 1)
					{
						Assertion.IfEnabled?.That(_items.FindIndex(index + 1, delayItem => delayItem.Id == itemId) == -1,
							"There should be at most one item with each ID");
					}

					return RemoveAtReturnCopy(index);
				});

			internal DelayItem? RemoveEarliestToTrigger(AgentTimeInstant whenToTrigger) =>
				DoUnderLock<DelayItem?>(() =>
				{
					if (_items.IsEmpty() || _items[0].WhenToTrigger > whenToTrigger) return null;

					return RemoveAtReturnCopy(0);
				});

			private void AssertValid()
			{
				if (!Assertion.IsEnabled) return;

				Assertion.IfEnabled?.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");

				for (var i = 0; i < _items.Count - 1; ++i)
				{
					Assertion.IfEnabled?.That(_items[i].WhenToTrigger <= _items[i + 1].WhenToTrigger,
						"Delay items should be in ascending order by trigger time");
				}
			}

			private TResult DoUnderLock<TResult>(Func<TResult> doFunc)
			{
				lock (_lock)
				{
					AssertValid();
					try
					{
						return doFunc();
					}
					finally
					{
						AssertValid();
					}
				}
			}

			private void DoUnderLock(Action doAction) =>
				DoUnderLock(() =>
				{
					doAction();
					return (object)null;
				});

			private DelayItem RemoveAtReturnCopy(int index)
			{
				Assertion.IfEnabled?.That(Monitor.IsEntered(_lock), "Current thread should hold the lock");

				var itemCopy = _items[index];
				_items.RemoveAt(index);
				return itemCopy;
			}
		}
	}
}
