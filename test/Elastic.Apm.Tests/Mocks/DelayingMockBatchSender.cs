using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Helpers;

namespace Elastic.Apm.Tests.Mocks
{
	internal class DelayingMockBatchSender : MockBatchSender
	{
		private readonly IAgentTimer _agentTimer;
		private readonly object _timeToDelayLock = new object();

		internal DelayingMockBatchSender(TimeSpan timeToDelay, IAgentTimer agentTimer)
		{
			_agentTimer = agentTimer;
			TimeToDelay = timeToDelay;
		}

		private TimeSpan _timeToDelay;

		internal TimeSpan TimeToDelay
		{
			get
			{
				TimeSpan localCopy;
				lock (_timeToDelayLock) localCopy = _timeToDelay;
				return localCopy;
			}

			set
			{
				lock (_timeToDelayLock) _timeToDelay = value;
			}
		}

		public override async Task SendBatchAsync(IEnumerable<object> batch, CancellationToken cancellationToken)
		{
			await _agentTimer.Delay(_agentTimer.Now, TimeToDelay, cancellationToken);
			await base.SendBatchAsync(batch, cancellationToken);
		}
	}
}
