using System;
using System.Threading;
using System.Threading.Tasks;

namespace Elastic.Apm.Helpers
{
	internal interface IAgentTimer
	{
		AgentTimeInstant Now { get; }

		Task Delay(AgentTimeInstant relativeToInstant, TimeSpan delay, CancellationToken cancellationToken);
	}

	internal static class AgentTimerExtensions
	{
		internal static Task Delay(this IAgentTimer timer, AgentTimeInstant relativeToInstant, TimeSpan delay) =>
			timer.Delay(relativeToInstant, delay, default);
	}
}
