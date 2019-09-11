using System;
using System.Threading;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Tests.Mocks;
using Elastic.Apm.Tests.TestHelpers;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;
using Xunit.Abstractions;
using static Elastic.Apm.Tests.TestHelpers.FluentAssertionsUtils;

namespace Elastic.Apm.Tests.HelpersTests
{
	public class AgentTimeInstantTests
	{
		private readonly IApmLogger _logger;

		public AgentTimeInstantTests(ITestOutputHelper testOutputHelper) => _logger = new XunitOutputLogger(testOutputHelper);

		internal interface IAgentTimeInstantSourceForTest
		{
			AgentTimeInstant Now { get; }

			TimeSpan WaitForSomeTimeToPass();
		}

		public static TheoryData AgentTimeInstantSources => new TheoryData<IAgentTimeInstantSourceForTest>
		{
			new MockAgentTimerInstantSourceForTest(), new AgentTimerInstantSourceForTest()
		};

		public static TheoryData IncompatibleAgentTimeInstantSources => new TheoryData<IAgentTimeInstantSourceForTest, IAgentTimeInstantSourceForTest>
		{
			{ new AgentTimerInstantSourceForTest(), new AgentTimerInstantSourceForTest() },
			{ new MockAgentTimerInstantSourceForTest(), new MockAgentTimerInstantSourceForTest() },
			{ new AgentTimerInstantSourceForTest(), new MockAgentTimerInstantSourceForTest() },
			{ new MockAgentTimerInstantSourceForTest(), new AgentTimerInstantSourceForTest() },
		};

		[Theory]
		[MemberData(nameof(AgentTimeInstantSources))]
		internal void Instant_arithmetics(IAgentTimeInstantSourceForTest timeInstantSource)
		{
			timeInstantSource.WaitForSomeTimeToPass();
			var i1 = timeInstantSource.Now;
			var i1B = timeInstantSource.Now;
			i1.Equals(i1B).Should().Be(i1 == i1B);
			i1.Equals((object)i1B).Should().Be(i1 == i1B);
			(i1 != i1B).Should().Be(!(i1 == i1B));

			var diffBetweenI21 = timeInstantSource.WaitForSomeTimeToPass();
			var i2 = timeInstantSource.Now;
			var i2B = timeInstantSource.Now;
			i2.Equals(i2B).Should().Be(i2 == i2B);
			i2.Equals((object)i2B).Should().Be(i2 == i2B);
			(i2 != i2B).Should().Be(!(i2 == i2B));

			i1.Equals(i2).Should().BeFalse();
			i1.Equals((object)i2).Should().BeFalse();
			(i1 == i2).Should().BeFalse();
			(i1 != i2).Should().BeTrue();

			(i1 < i2).Should().BeTrue();
			(i2 < i1).Should().BeFalse();
			(i1 < i1B).Should().Be(i1 != i1B);
			(i1B < i1).Should().BeFalse();

			(i1 > i2).Should().BeFalse();
			(i2 > i1).Should().BeTrue();
			(i2 > i2B).Should().BeFalse();
			(i1B > i1).Should().Be(i1B != i1);
			(i2B > i2).Should().Be(i2B != i2);

			(i1 <= i2).Should().BeTrue();
			(i2 <= i1).Should().BeFalse();
			(i1 <= i1B).Should().BeTrue();
			(i1B <= i1).Should().Be(i1B == i1);
			(i2 <= i2B).Should().BeTrue();
			(i2B <= i2).Should().Be(i2B == i2);

			(i1 >= i2).Should().BeFalse();
			(i2 >= i1).Should().BeTrue();
			(i1 >= i1B).Should().Be(i1 == i1B);
			(i1B >= i1).Should().BeTrue();
			(i2 >= i2B).Should().Be(i2 == i2B);
			(i2B >= i2).Should().BeTrue();

			(i1 + diffBetweenI21 <= i2).Should().BeTrue();
			// ReSharper disable once InvertIf
			if (i1 + diffBetweenI21 == i2)
			{
				var i2C = i1;
				i2C += diffBetweenI21;
				i2C.Should().Be(i2);

				(i2 - i1).Should().Be(diffBetweenI21);
				var i1C = i2;
				i1C -= diffBetweenI21;
				i1C.Should().Be(i1);
			}
		}

		[Theory]
		[MemberData(nameof(IncompatibleAgentTimeInstantSources))]
		internal void Instants_from_different_timers_can_be_compared_for_equality(
			IAgentTimeInstantSourceForTest source1,
			IAgentTimeInstantSourceForTest source2
		)
		{
			var i1 = source1.Now;
			var i2 = source2.Now;
			_logger.Debug()?.Log("i1: {i1}", i1.ToStringDetailed());
			_logger.Debug()?.Log("i2: {i2}", i2.ToStringDetailed());
			(i1 == i2).Should().BeFalse();
			i1.Equals(i2).Should().BeFalse();
			i1.Equals((object)i2).Should().BeFalse();
		}

		[Theory]
		[MemberData(nameof(IncompatibleAgentTimeInstantSources))]
		internal void operations_on_Instants_from_different_timers_throw(
			IAgentTimeInstantSourceForTest source1,
			IAgentTimeInstantSourceForTest source2
		)
		{
			var i1 = source1.Now;
			var i2 = source2.Now;

			AsAction(() => DummyNoopFunc(i2 - i1))
				.Should()
				.ThrowExactly<InvalidOperationException>()
				.WithMessage("*illegal to perform operation op_Subtraction *");

			AsAction(() => DummyNoopFunc(i2 > i1))
				.Should()
				.ThrowExactly<InvalidOperationException>()
				.WithMessage("*illegal to perform operation op_GreaterThan *");

			AsAction(() => DummyNoopFunc(i2 >= i1))
				.Should()
				.ThrowExactly<InvalidOperationException>()
				.WithMessage("*illegal to perform operation op_GreaterThanOrEqual *");

			AsAction(() => DummyNoopFunc(i2 < i1))
				.Should()
				.ThrowExactly<InvalidOperationException>()
				.WithMessage("*illegal to perform operation op_LessThan *");

			AsAction(() => DummyNoopFunc(i2 <= i1))
				.Should()
				.ThrowExactly<InvalidOperationException>()
				.WithMessage("*illegal to perform operation op_LessThanOrEqual *");

			// ReSharper disable once UnusedParameter.Local
			void DummyNoopFunc<T>(T _) { }
		}

		private class AgentTimerInstantSourceForTest : IAgentTimeInstantSourceForTest
		{
			private readonly AgentTimer _timer = new AgentTimer();

			public AgentTimeInstant Now => _timer.Now;

			public TimeSpan WaitForSomeTimeToPass()
			{
				var beforeSleep = Now;
				Thread.Sleep(100);
				var afterSleep = Now;
				return afterSleep - beforeSleep;
			}
		}

		private class MockAgentTimerInstantSourceForTest : IAgentTimeInstantSourceForTest
		{
			private readonly MockAgentTimer _timer;
			private readonly ThreadSafeLongCounter _waitCount = new ThreadSafeLongCounter();

			internal MockAgentTimerInstantSourceForTest(string dbgName = null) => _timer = new MockAgentTimer(dbgName);

			public AgentTimeInstant Now => _timer.Now;

			public TimeSpan WaitForSomeTimeToPass()
			{
				var waitCount = _waitCount.Increment();
				var waitTimeSpan = waitCount % 2 == 0
					? 1.Day() + 2.Hours() + 3.Minutes() + 4.Seconds() + 5.Milliseconds()
					: 0.Days() + 9.Hours() + 8.Minutes() + 7.Seconds() + 6.Milliseconds();
				_timer.FastForward(waitTimeSpan);
				return waitTimeSpan;
			}
		}
	}
}
