using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Elastic.Apm.Tests.Mocks
{
	internal class CustomActionMockBatchSender : MockBatchSender
	{
		private readonly Func<IEnumerable<object>, CancellationToken, Task> _customAction;

		internal CustomActionMockBatchSender(Func<IEnumerable<object>, CancellationToken, Task> customAction = null) => _customAction = customAction;

		public override Task SendBatchAsync(IEnumerable<object> batch, CancellationToken cancellationToken) =>
			_customAction(batch, cancellationToken);
	}
}
