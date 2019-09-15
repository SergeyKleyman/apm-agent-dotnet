using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Helpers;
using Elastic.Apm.Tests.TestHelpers;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;
using static Elastic.Apm.Tests.TestHelpers.FluentAssertionsUtils;

namespace Elastic.Apm.Tests.Mocks
{
	public class MockAgentTimerTests
	{
		[Fact]
		internal void Now_without_FastForward_returns_the_same_instant()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			var i1 = agentTimer.Now;
			var i1B = agentTimer.Now;
			i1.Equals(i1B).Should().BeTrue();
			i1.Equals((object)i1B).Should().BeTrue();
			(i1 == i1B).Should().BeTrue();
			(i1 != i1B).Should().BeFalse();

			var diffBetweenI21 = 0.Days() + 9.Hours() + 8.Minutes() + 7.Seconds() + 6.Milliseconds();
			agentTimer.FastForward(diffBetweenI21);
			var i2 = agentTimer.Now;
			var i2B = agentTimer.Now;
			i1.Equals(i2).Should().BeFalse();
			i1.Equals((object)i2).Should().BeFalse();
			(i2 == i2B).Should().BeTrue();

			(i1 == i2).Should().BeFalse();
			(i1 != i2).Should().BeTrue();

			(i1 + diffBetweenI21).Should().Be(i2);
			var i2C = i1;
			i2C += diffBetweenI21;
			i2C.Should().Be(i2);
		}

		[Fact]
		public void Delay_simple_test()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var delayTask = agentTimer.Delay(agentTimer.Now, 2.Days());
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(1.Day());
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(1.Day());
			delayTask.IsCompleted.Should().BeTrue();
		}

		[Fact]
		public void Delay_fast_forward_past_trigger_time()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var delayTask = agentTimer.Delay(agentTimer.Now, 2.Minutes());
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Minutes());
			delayTask.IsCompleted.Should().BeTrue();
		}

		[Fact]
		public void calling_FastForward_while_one_already_in_progress_throws()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var reachedBeforeInnerFastForward = false;
			var reachedAfterInnerFastForward = false;
			agentTimer.Delay(agentTimer.Now, 1.Hour())
				.AttachSynchronousContinuation(() =>
				{
					AsAction(() =>
						{
							reachedBeforeInnerFastForward = true;
							agentTimer.FastForward(1.Minute());
							reachedAfterInnerFastForward = true;
						})
						.Should()
						.ThrowExactly<InvalidOperationException>()
						.WithMessage($"*{nameof(MockAgentTimer.FastForward)}*");
				});

			agentTimer.FastForward(2.Hours());

			reachedBeforeInnerFastForward.Should().BeTrue();
			reachedAfterInnerFastForward.Should().BeFalse();
		}

		[Fact]
		public void Cancel_Delay()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			using (var cancellationTokenSource = new CancellationTokenSource())
			{
				var continuationCalled = false;

				agentTimer.Delay(agentTimer.Now, 30.Minutes(), cancellationTokenSource.Token)
					.AttachSynchronousContinuation(task =>
					{
						continuationCalled.Should().BeFalse();
						continuationCalled = true;
						task.IsCanceled.Should().BeTrue();
					});

				agentTimer.FastForward(20.Minutes());

				cancellationTokenSource.Cancel();
				continuationCalled.Should().BeTrue();

				agentTimer.FastForward(20.Minutes());
			}
		}

		[Fact]
		public void Cancel_already_triggered_Delay()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			using (var cancellationTokenSource = new CancellationTokenSource())
			{
				var continuationCalled = false;

				agentTimer.Delay(agentTimer.Now, 30.Minutes(), cancellationTokenSource.Token)
					.AttachSynchronousContinuation(task =>
					{
						continuationCalled.Should().BeFalse();
						continuationCalled = true;
						task.IsCanceled.Should().BeFalse();
					});

				agentTimer.FastForward(30.Minutes());
				continuationCalled.Should().BeTrue();

				cancellationTokenSource.Cancel();
			}
		}

		[Fact]
		public void Two_Delays_longer_first()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var t1 = 7.Seconds();
			var t2 = 6.Seconds();
			var startInstant = agentTimer.Now;
			var delayTaskA = agentTimer.Delay(agentTimer.Now, t1 + t2);
			var delayTaskB = agentTimer.Delay(agentTimer.Now, t1);
			delayTaskA.IsCompleted.Should().BeFalse();
			delayTaskB.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(t1);
			agentTimer.Now.Should().Be(startInstant + t1);
			delayTaskA.IsCompleted.Should().BeFalse();
			delayTaskB.IsCompleted.Should().BeTrue();
			agentTimer.FastForward(t2);
			agentTimer.Now.Should().Be(startInstant + t1 + t2);
			delayTaskA.IsCompleted.Should().BeTrue();
		}

		[Fact]
		public void Add_Delay_in_continuation()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var t1 = 7.Minutes();
			var t2 = 7.Seconds();
			var t3 = 7.Days();

			var invocationCounter = 0;
			var startInstant = agentTimer.Now;

			agentTimer.Delay(agentTimer.Now, t1)
				.AttachSynchronousContinuation(() =>
				{
					(++invocationCounter).Should().Be(1);
					agentTimer.Now.Should().Be(startInstant + t1);

					agentTimer.Delay(agentTimer.Now, t2).AttachSynchronousContinuation(() =>
					{
						(++invocationCounter).Should().Be(2);
						agentTimer.Now.Should().Be(startInstant + t1 + t2);
					});
				});

			agentTimer.Delay(agentTimer.Now, t1 + t2 + t3).AttachSynchronousContinuation(() =>
			{
				(++invocationCounter).Should().Be(3);
				agentTimer.Now.Should().Be(startInstant + t1 + t2 + t3);
			});

			agentTimer.FastForward(t1 + t2 + t3);
		}

		[Fact]
		public void Delay_with_relativeToInstant_in_the_past()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var t1 = 7.Seconds();
			var t2 = 11.Seconds();
			var timeToWait = t1 + t2;

			// We take time instant at the start of a "atomic" calculation
			var now = agentTimer.Now;
			// Let's assume there's a long calculation that takes some time
			agentTimer.FastForward(t1);
			// and the conclusion of the calculation that we need to delay for timeToWait
			// (relative to `now` time instant)
			var delayTask = agentTimer.Delay(now, timeToWait);
			delayTask.IsCompleted.Should().BeFalse();

			agentTimer.FastForward(t2);

			delayTask.IsCompleted.Should().BeTrue();
		}

		[Fact]
		public void Delay_with_target_time_is_already_in_the_past()
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var timeToWait = 3.Seconds();

			// We take time instant at the start of a "atomic" calculation
			var now = agentTimer.Now;
			// Let's assume there's a long calculation that takes some time
			agentTimer.FastForward(timeToWait + 5.Seconds());
			// and the conclusion of the calculation that we need to delay for timeToWait
			// (relative to `now` time instant)
			var delayTask = agentTimer.Delay(now, timeToWait);
			delayTask.IsCompleted.Should().BeTrue();
		}

		internal interface IAwaitOrTimeoutTaskVariant
		{
			Task TryAwaitOrTimeoutCall(IAgentTimer agentTimer, TimeSpan timeout, CancellationToken cancellationToken = default);
			Task AwaitOrTimeoutCall(IAgentTimer agentTimer, TimeSpan timeout, CancellationToken cancellationToken = default);

			void CompleteTaskSuccessfully();
			void VerifyTryAwaitCompletedSuccessfully(Task tryAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask);
			void VerifyAwaitCompletedSuccessfully(Task awaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask);

			void VerifyTryAwaitTimeout(Task tryAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask);
			void VerifyAwaitTimeout(Task tryAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask);

			void CancelTask();
			void VerifyCancelled(Task xyzAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask);

			void FaultTask();
			void VerifyFaulted(Task xyzAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask);

			void VerifyDelayCancelled(Task awaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask);
		}

		private class AwaitOrTimeoutTaskVariant<TResult>: IAwaitOrTimeoutTaskVariant
		{
			private readonly TaskCompletionSource<TResult> _taskToAwaitTcs = new TaskCompletionSource<TResult>();
			private readonly bool _isVoid;
			private readonly TResult _resultValue;
			private readonly CancellationToken _cancellationToken = new CancellationToken(true);
			private readonly DummyTestException _dummyTestException = new DummyTestException();

			internal AwaitOrTimeoutTaskVariant()
			{
				_isVoid = true;
				_resultValue = default;
			}

			internal AwaitOrTimeoutTaskVariant(TResult resultValue)
			{
				_isVoid = false;
				resultValue.Should().NotBe(default);
				_resultValue = resultValue;
			}

			public Task TryAwaitOrTimeoutCall(IAgentTimer agentTimer, TimeSpan timeout, CancellationToken cancellationToken = default) =>
				_isVoid
					? (Task)agentTimer.TryAwaitOrTimeout((Task)_taskToAwaitTcs.Task, agentTimer.Now, timeout, cancellationToken)
					: agentTimer.TryAwaitOrTimeout(_taskToAwaitTcs.Task, agentTimer.Now, timeout, cancellationToken);

			public Task AwaitOrTimeoutCall(IAgentTimer agentTimer, TimeSpan timeout, CancellationToken cancellationToken = default) =>
				_isVoid
					? agentTimer.AwaitOrTimeout((Task)_taskToAwaitTcs.Task, agentTimer.Now, timeout, cancellationToken)
					: agentTimer.AwaitOrTimeout(_taskToAwaitTcs.Task, agentTimer.Now, timeout, cancellationToken);

			private void UnpackTryAwaitOrTimeoutTaskResult(Task tryAwaitOrTimeoutTask, out bool hasTaskToAwaitCompleted
				, out TResult taskToAwaitResult)
			{
				if (_isVoid)
				{
					hasTaskToAwaitCompleted = ((Task<bool>)tryAwaitOrTimeoutTask).Result;
					taskToAwaitResult = default;
				}
				else
					(hasTaskToAwaitCompleted, taskToAwaitResult) = ((Task<ValueTuple<bool, TResult>>)tryAwaitOrTimeoutTask).Result;
			}

			public void CompleteTaskSuccessfully()
			{
				_taskToAwaitTcs.Task.IsCompleted.Should().BeFalse();
				_taskToAwaitTcs.SetResult(_resultValue);
			}

			public void VerifyTryAwaitCompletedSuccessfully(Task tryAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask)
			{
				tryAwaitOrTimeoutTask.IsCompletedSuccessfully.Should().BeTrue();

				UnpackTryAwaitOrTimeoutTaskResult(tryAwaitOrTimeoutTask, out var hasTaskToAwaitCompleted, out var taskToAwaitResult);
				hasTaskToAwaitCompleted.Should().BeTrue();
				taskToAwaitResult.Should().Be(_resultValue);

				_taskToAwaitTcs.Task.IsCompletedSuccessfully.Should().BeTrue();
				_taskToAwaitTcs.Task.Result.Should().Be(_resultValue);

				VerifyFinalAgentTimerState(agentTimer, delayTask);
			}

			public void VerifyAwaitCompletedSuccessfully(Task awaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask)
			{
				awaitOrTimeoutTask.IsCompletedSuccessfully.Should().BeTrue();

				if (! _isVoid) ((Task<TResult>)awaitOrTimeoutTask).Result.Should().Be(_resultValue);

				_taskToAwaitTcs.Task.IsCompletedSuccessfully.Should().BeTrue();
				_taskToAwaitTcs.Task.Result.Should().Be(_resultValue);

				VerifyFinalAgentTimerState(agentTimer, delayTask);
			}

			public void VerifyTryAwaitTimeout(Task tryAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask)
			{
				tryAwaitOrTimeoutTask.IsCompletedSuccessfully.Should().BeTrue();

				UnpackTryAwaitOrTimeoutTaskResult(tryAwaitOrTimeoutTask, out var hasTaskToAwaitCompleted, out var taskToAwaitResult);
				hasTaskToAwaitCompleted.Should().BeFalse();
				taskToAwaitResult.Should().Be(default(TResult));

				_taskToAwaitTcs.Task.IsCompleted.Should().BeFalse();

				VerifyFinalAgentTimerState(agentTimer, delayTask, /* wasDelayCancelled: */ false);
			}

			public void VerifyAwaitTimeout(Task awaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask)
			{
				awaitOrTimeoutTask.IsFaulted.Should().BeTrue();
				awaitOrTimeoutTask.Exception.InnerExceptions.Should().ContainSingle();
				awaitOrTimeoutTask.Exception.InnerException.Should().BeOfType<TimeoutException>();

				_taskToAwaitTcs.Task.IsCompleted.Should().BeFalse();

				VerifyFinalAgentTimerState(agentTimer, delayTask, /* wasDelayCancelled: */ false);
			}

			public void CancelTask()
			{
				_taskToAwaitTcs.Task.IsCompleted.Should().BeFalse();
				var trySetCanceledRetVal = _taskToAwaitTcs.TrySetCanceled(_cancellationToken);
				trySetCanceledRetVal.Should().BeTrue();
			}

			public void VerifyCancelled(Task xyzAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask)
			{
				xyzAwaitOrTimeoutTask.IsCanceled.Should().BeTrue();
				// ReSharper disable once PossibleNullReferenceException
				OperationCanceledException ex = null;
				try
				{
					// ReSharper disable once MethodSupportsCancellation
					xyzAwaitOrTimeoutTask.Wait();
				}
				catch (AggregateException caughtEx)
				{
					caughtEx.InnerExceptions.Should().ContainSingle();
					ex = (OperationCanceledException)caughtEx.InnerException;
				}
				// ReSharper disable once PossibleNullReferenceException
				ex.CancellationToken.Should().Be(_cancellationToken);

				_taskToAwaitTcs.Task.IsCanceled.Should().BeTrue();

				VerifyFinalAgentTimerState(agentTimer, delayTask);
			}

			public void FaultTask()
			{
				_taskToAwaitTcs.Task.IsCompleted.Should().BeFalse();
				_taskToAwaitTcs.SetException(_dummyTestException);
			}

			public void VerifyFaulted(Task xyzAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask)
			{
				xyzAwaitOrTimeoutTask.IsFaulted.Should().BeTrue();
				// ReSharper disable once PossibleNullReferenceException
				xyzAwaitOrTimeoutTask.Exception.InnerException.Should().Be(_dummyTestException);

				_taskToAwaitTcs.Task.IsFaulted.Should().BeTrue();

				VerifyFinalAgentTimerState(agentTimer, delayTask);
			}

			public void VerifyDelayCancelled(Task xyzAwaitOrTimeoutTask, MockAgentTimer agentTimer, Task delayTask)
			{
				xyzAwaitOrTimeoutTask.IsCanceled.Should().BeFalse();

				_taskToAwaitTcs.Task.IsCompleted.Should().BeFalse();

				VerifyFinalAgentTimerState(agentTimer, delayTask);
			}

			private static void VerifyFinalAgentTimerState(MockAgentTimer agentTimer, Task delayTask, bool wasDelayCancelled = true)
			{
				agentTimer.DelayItemsCount.Should().Be(0);
				if (wasDelayCancelled)
					delayTask.IsCanceled.Should().BeTrue();
				else
					delayTask.IsCompletedSuccessfully.Should().BeTrue();
			}
		}

		public static TheoryData AwaitOrTimeoutTaskVariantsToTest => new TheoryData<string, IAwaitOrTimeoutTaskVariant>
		{
			{ "Task", new AwaitOrTimeoutTaskVariant<object>() },
			{ "Task<int>, 123", new AwaitOrTimeoutTaskVariant<int>(123) },
			{ "Task<string>, `456'", new AwaitOrTimeoutTaskVariant<string>("456") }
		};

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void TryAwaitOrTimeout_task_completed_successfully_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var tryAwaitOrTimeoutTask = variant.TryAwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			tryAwaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			variant.CompleteTaskSuccessfully();

			variant.VerifyTryAwaitCompletedSuccessfully(tryAwaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void TryAwaitOrTimeout_task_timed_out_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var tryAwaitOrTimeoutTask = variant.TryAwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();

			agentTimer.FastForward(5.Seconds());

			variant.VerifyTryAwaitTimeout(tryAwaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void TryAwaitOrTimeout_task_cancelled_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var tryAwaitOrTimeoutTask = variant.TryAwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			tryAwaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			variant.CancelTask();

			variant.VerifyCancelled(tryAwaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void TryAwaitOrTimeout_task_faulted_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var tryAwaitOrTimeoutTask = variant.TryAwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			tryAwaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			variant.FaultTask();

			variant.VerifyFaulted(tryAwaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void TryAwaitOrTimeout_Delay_cancelled_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var cts = new CancellationTokenSource();
			var tryAwaitOrTimeoutTask = variant.TryAwaitOrTimeoutCall(agentTimer, 5.Seconds(), cts.Token);
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			tryAwaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			cts.Cancel();

			variant.VerifyDelayCancelled(tryAwaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void AwaitOrTimeout_task_completed_successfully_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var awaitOrTimeoutTask = variant.AwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			awaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			variant.CompleteTaskSuccessfully();

			variant.VerifyAwaitCompletedSuccessfully(awaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void AwaitOrTimeout_task_timed_out_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var awaitOrTimeoutTask = variant.AwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();

			agentTimer.FastForward(5.Seconds());

			variant.VerifyAwaitTimeout(awaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void AwaitOrTimeout_task_cancelled_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var awaitOrTimeoutTask = variant.AwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			awaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			variant.CancelTask();

			variant.VerifyCancelled(awaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void AwaitOrTimeout_task_faulted_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var awaitOrTimeoutTask = variant.AwaitOrTimeoutCall(agentTimer, 5.Seconds());
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			awaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			variant.FaultTask();

			variant.VerifyFaulted(awaitOrTimeoutTask, agentTimer, delayTask);
		}

		[Theory]
		[MemberData(nameof(AwaitOrTimeoutTaskVariantsToTest))]
		internal void AwaitOrTimeout_Delay_cancelled_test(string dbgVariantDesc, IAwaitOrTimeoutTaskVariant variant)
		{
			var agentTimer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			agentTimer.DelayItemsCount.Should().Be(0, $"because {nameof(dbgVariantDesc)}: {dbgVariantDesc}");
			var cts = new CancellationTokenSource();
			var awaitOrTimeoutTask = variant.AwaitOrTimeoutCall(agentTimer, 5.Seconds(), cts.Token);
			agentTimer.DelayItemsCount.Should().Be(1);
			var delayTask = agentTimer.DelayTasks.First();
			delayTask.IsCompleted.Should().BeFalse();
			agentTimer.FastForward(3.Seconds());
			awaitOrTimeoutTask.IsCompleted.Should().BeFalse();

			cts.Cancel();

			variant.VerifyDelayCancelled(awaitOrTimeoutTask, agentTimer, delayTask);
		}
	}

	internal static class MockAgentTimerTestsExtensions
	{
		internal static Task AttachSynchronousContinuation(this Task thisObj, Action action) =>
			thisObj.ContinueWith(_ => action(), TaskContinuationOptions.ExecuteSynchronously);

		internal static Task AttachSynchronousContinuation(this Task thisObj, Action<Task> action) =>
			thisObj.ContinueWith(action, TaskContinuationOptions.ExecuteSynchronously);
	}
}
