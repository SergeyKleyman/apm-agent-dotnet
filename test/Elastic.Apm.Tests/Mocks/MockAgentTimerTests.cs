using System;
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
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			var i1 = timer.Now;
			var i1B = timer.Now;
			i1.Equals(i1B).Should().BeTrue();
			i1.Equals((object)i1B).Should().BeTrue();
			(i1 == i1B).Should().BeTrue();
			(i1 != i1B).Should().BeFalse();

			var diffBetweenI21 = 0.Days() + 9.Hours() + 8.Minutes() + 7.Seconds() + 6.Milliseconds();
			timer.FastForward(diffBetweenI21);
			var i2 = timer.Now;
			var i2B = timer.Now;
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
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var delayTask = timer.Delay(timer.Now, 2.Days());
			delayTask.IsCompleted.Should().BeFalse();
			timer.FastForward(1.Day());
			delayTask.IsCompleted.Should().BeFalse();
			timer.FastForward(1.Day());
			delayTask.IsCompleted.Should().BeTrue();
		}

		[Fact]
		public void Delay_fast_forward_past_trigger_time()
		{
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var delayTask = timer.Delay(timer.Now, 2.Minutes());
			delayTask.IsCompleted.Should().BeFalse();
			timer.FastForward(3.Minutes());
			delayTask.IsCompleted.Should().BeTrue();
		}

		[Fact]
		public void calling_FastForward_while_one_already_in_progress_throws()
		{
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var reachedBeforeInnerFastForward = false;
			var reachedAfterInnerFastForward = false;
			timer.Delay(timer.Now, 1.Hour())
				.AttachSynchronousContinuation(() =>
				{
					AsAction(() =>
						{
							reachedBeforeInnerFastForward = true;
							timer.FastForward(1.Minute());
							reachedAfterInnerFastForward = true;
						})
						.Should()
						.ThrowExactly<InvalidOperationException>()
						.WithMessage($"*{nameof(MockAgentTimer.FastForward)}*");
				});

			timer.FastForward(2.Hours());

			reachedBeforeInnerFastForward.Should().BeTrue();
			reachedAfterInnerFastForward.Should().BeFalse();
		}

		[Fact]
		public void Cancel_Delay()
		{
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			using (var cancellationTokenSource = new CancellationTokenSource())
			{
				var continuationCalled = false;

				timer.Delay(timer.Now, 30.Minutes(), cancellationTokenSource.Token)
					.AttachSynchronousContinuation(task =>
					{
						continuationCalled.Should().BeFalse();
						continuationCalled = true;
						task.IsCanceled.Should().BeTrue();
					});

				timer.FastForward(20.Minutes());

				cancellationTokenSource.Cancel();
				continuationCalled.Should().BeTrue();

				timer.FastForward(20.Minutes());
			}
		}

		[Fact]
		public void Cancel_already_triggered_Delay()
		{
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());
			using (var cancellationTokenSource = new CancellationTokenSource())
			{
				var continuationCalled = false;

				timer.Delay(timer.Now, 30.Minutes(), cancellationTokenSource.Token)
					.AttachSynchronousContinuation(task =>
					{
						continuationCalled.Should().BeFalse();
						continuationCalled = true;
						task.IsCanceled.Should().BeFalse();
					});

				timer.FastForward(30.Minutes());
				continuationCalled.Should().BeTrue();

				cancellationTokenSource.Cancel();
			}
		}

		[Fact]
		public void Two_Delays_longer_first()
		{
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var t1 = 7.Seconds();
			var t2 = 6.Seconds();
			var startInstant = timer.Now;
			var delayTaskA = timer.Delay(timer.Now, t1 + t2);
			var delayTaskB = timer.Delay(timer.Now, t1);
			delayTaskA.IsCompleted.Should().BeFalse();
			delayTaskB.IsCompleted.Should().BeFalse();
			timer.FastForward(t1);
			timer.Now.Should().Be(startInstant + t1);
			delayTaskA.IsCompleted.Should().BeFalse();
			delayTaskB.IsCompleted.Should().BeTrue();
			timer.FastForward(t2);
			timer.Now.Should().Be(startInstant + t1 + t2);
			delayTaskA.IsCompleted.Should().BeTrue();
		}

		[Fact]
		public void Add_Delay_in_continuation()
		{
			var timer = new MockAgentTimer(DbgUtils.GetCurrentMethodName());

			var t1 = 7.Minutes();
			var t2 = 7.Seconds();
			var t3 = 7.Days();

			var invocationCounter = 0;
			var startInstant = timer.Now;

			timer.Delay(timer.Now, t1)
				.AttachSynchronousContinuation(() =>
				{
					(++invocationCounter).Should().Be(1);
					timer.Now.Should().Be(startInstant + t1);

					timer.Delay(timer.Now, t2).AttachSynchronousContinuation(() =>
					{
						(++invocationCounter).Should().Be(2);
						timer.Now.Should().Be(startInstant + t1 + t2);
					});
				});

			timer.Delay(timer.Now, t1 + t2 + t3).AttachSynchronousContinuation(() =>
			{
				(++invocationCounter).Should().Be(3);
				timer.Now.Should().Be(startInstant + t1 + t2 + t3);
			});

			timer.FastForward(t1 + t2 + t3);
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
