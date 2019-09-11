using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Report;

namespace Elastic.Apm.Tests.Mocks
{
	internal class MockBatchSender : IBatchSender
	{
		internal readonly struct SendArgs
		{
			internal SendArgs(bool isCancellationRequested, IEnumerable<object> batch)
			{
				IsCancellationRequested = isCancellationRequested;
				Batch = ImmutableArray.CreateRange(batch);
				CurrentThread = Thread.CurrentThread;
			}

			internal readonly bool IsCancellationRequested;
			internal readonly ImmutableArray<object> Batch;
			internal readonly Thread CurrentThread;
		}

		internal ImmutableList<SendArgs> SendCalls = ImmutableList<SendArgs>.Empty;

		public virtual Task SendBatchAsync(IEnumerable<object> batch, CancellationToken cancellationToken)
		{
			var builder = SendCalls.ToBuilder();
			builder.Add(new SendArgs(cancellationToken.IsCancellationRequested, batch));
			SendCalls = builder.ToImmutable();
			return Task.CompletedTask;
		}

		internal void Clear() => SendCalls = ImmutableList<SendArgs>.Empty;
	}
}
