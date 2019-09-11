using Elastic.Apm.Helpers;
using FluentAssertions;
using Xunit;
using static Elastic.Apm.Tests.TestHelpers.FluentAssertionsUtils;

namespace Elastic.Apm.Tests.HelpersTests
{
	public class AssertionTests
	{
		[Fact]
		internal void level_Disabled()
		{
			var assertion = new Assertion.Impl(AssertionLevel.Disabled);

			assertion.IsEnabled.Should().BeFalse();
			assertion.IsOnLevelEnabled.Should().BeFalse();

			var counter = 0;

			assertion.IfEnabled?.That(++counter == 1, $"Dummy message {++counter}");
			counter.Should().Be(0);

			assertion.IfEnabled?.That(++counter != 1, $"Dummy message {++counter}");
			counter.Should().Be(0);

			assertion.IfOnLevelEnabled?.That(++counter == 1, $"Dummy message {++counter}");
			counter.Should().Be(0);

			assertion.IfOnLevelEnabled?.That(++counter != 1, $"Dummy message {++counter}");
			counter.Should().Be(0);

			assertion.DoIfEnabled(assert =>
			{
				assert.That(++counter == 1, $"Dummy message {++counter}");
				counter.Should().Be(0);

				assert.That(++counter != 1, $"Dummy message {++counter}");
				counter.Should().Be(0);
			});

			assertion.DoIfEnabled(assert =>
			{
				assert.That(++counter == 1, $"Dummy message {++counter}");
				counter.Should().Be(0);

				assert.That(++counter != 1, $"Dummy message {++counter}");
				counter.Should().Be(0);
			});
		}

		[Fact]
		internal void level_O1()
		{
			var assertion = new Assertion.Impl(AssertionLevel.O1);

			assertion.IsEnabled.Should().BeTrue();
			assertion.IsOnLevelEnabled.Should().BeFalse();

			var counter = 0;
			assertion.IfEnabled?.That(++counter == 1, $"Dummy message {++counter}");
			counter.Should().Be(2);
			counter = 0;

			AsAction(() => assertion.IfEnabled?.That(++counter != 1, $"Dummy message {++counter}")).Should()
				.ThrowExactly<AssertionFailedException>()
				.WithMessage("Dummy message 2");
			counter.Should().Be(2);
			counter = 0;

			assertion.IfOnLevelEnabled?.That(++counter == 1, $"Dummy message {++counter}");
			counter.Should().Be(0);

			assertion.IfOnLevelEnabled?.That(++counter != 1, $"Dummy message {++counter}");
			counter.Should().Be(0);

			assertion.DoIfEnabled(assert =>
			{
				assert.That(++counter == 1, $"Dummy message {++counter}");
				counter.Should().Be(2);
				counter = 0;

				AsAction(() => assertion.IfEnabled?.That(++counter != 1, $"Dummy message {++counter}")).Should()
					.ThrowExactly<AssertionFailedException>()
					.WithMessage("Dummy message 2");
				counter.Should().Be(2);
				counter = 0;
			});

			assertion.DoIfEnabled(assert =>
			{
				assert.That(++counter == 1, $"Dummy message {++counter}");
				counter.Should().Be(0);

				assert.That(++counter != 1, $"Dummy message {++counter}");
				counter.Should().Be(0);
			});
		}

	}
}
