using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Elastic.Apm.Helpers
{
	internal class AgentTimer: IAgentTimer
	{
		private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

		public AgentTimeInstant Now => new AgentTimeInstant(this, _stopwatch.Elapsed);

		public async Task Delay(AgentTimeInstant relativeToInstant, TimeSpan delay, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var now = Now;
			var delayRemainder = delay - (now - relativeToInstant);
			if (delayRemainder <= TimeSpan.Zero) return;

			await Task.Delay(delayRemainder, cancellationToken);
		}
	}
}
