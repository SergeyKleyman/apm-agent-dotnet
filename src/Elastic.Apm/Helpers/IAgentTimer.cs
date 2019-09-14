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

		/// <summary>
		/// It's recommended to use this method (or another TryAwaitOrTimeout or AwaitOrTimeout method)
		/// instead of just Task.WhenAny(taskToAwait, Task.Delay(timeout))
		/// because this method cancels the timer for timeout while <c>Task.Delay(timeout)</c>.
		/// If the number of “zombie” timer jobs starts becoming significant, performance could suffer.
		///
		/// For more detailed explanation see https://devblogs.microsoft.com/pfxteam/crafting-a-task-timeoutafter-method/
		/// </summary>
		/// <returns><c>true</c> if <c>taskToAwait</c> completed before the timeout, <c>false</c> otherwise</returns>
		internal static async Task<bool> TryAwaitOrTimeout(this IAgentTimer timer, Task taskToAwait
			, AgentTimeInstant relativeToInstant, TimeSpan timeout, CancellationToken cancellationToken = default)
		{
			var timeoutDelayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			Task timeoutDelayTask = null;
			try
			{
				timeoutDelayTask = timer.Delay(relativeToInstant, timeout, timeoutDelayCts.Token);
				var completedTask = await Task.WhenAny(taskToAwait, timeoutDelayTask);
				if (completedTask == taskToAwait)
				{
					await taskToAwait;
					return true;
				}

				Assertion.IfEnabled?.That(completedTask == timeoutDelayTask
					, $"{nameof(completedTask)}: {completedTask}, {nameof(timeoutDelayTask)}: timeOutTask, {nameof(taskToAwait)}: taskToAwait");
				// no need to cancel timeout timer if it has been triggered
				timeoutDelayTask = null;
				return false;
			}
			finally
			{
				if (timeoutDelayTask != null) timeoutDelayCts.Cancel();
				timeoutDelayCts.Dispose();
			}
		}

		/// <summary>
		/// It's recommended to use this method (or another TryAwaitOrTimeout or AwaitOrTimeout method)
		/// instead of just Task.WhenAny(taskToAwait, Task.Delay(timeout))
		/// because this method cancels the timer for timeout while <c>Task.Delay(timeout)</c>.
		/// If the number of “zombie” timer jobs starts becoming significant, performance could suffer.
		///
		/// For more detailed explanation see https://devblogs.microsoft.com/pfxteam/crafting-a-task-timeoutafter-method/
		/// </summary>
		/// <returns>(<c>true</c>, result of <c>taskToAwait</c>) if <c>taskToAwait</c> completed before the timeout, <c>false</c> otherwise</returns>
		internal static async Task<ValueTuple<bool, TResult>> TryAwaitOrTimeout<TResult>(this IAgentTimer timer, Task<TResult> taskToAwait
			, AgentTimeInstant relativeToInstant, TimeSpan timeout, CancellationToken cancellationToken = default)
		{
			var hasTaskToAwaitCompletedBeforeTimeout =
				await TryAwaitOrTimeout(timer, (Task)taskToAwait, relativeToInstant, timeout, cancellationToken);
			return (hasTaskToAwaitCompletedBeforeTimeout, hasTaskToAwaitCompletedBeforeTimeout ? await taskToAwait : default);
		}

		/// <summary>
		/// It's recommended to use this method (or another TryAwaitOrTimeout or AwaitOrTimeout method)
		/// instead of just Task.WhenAny(taskToAwait, Task.Delay(timeout))
		/// because this method cancels the timer for timeout while <c>Task.Delay(timeout)</c>.
		/// If the number of “zombie” timer jobs starts becoming significant, performance could suffer.
		///
		/// For more detailed explanation see https://devblogs.microsoft.com/pfxteam/crafting-a-task-timeoutafter-method/
		/// </summary>
		/// <exception cref="TimeoutException">Thrown when timeout expires before <c>taskToAwait</c> completes</exception>
		internal static async Task AwaitOrTimeout(this IAgentTimer timer, Task taskToAwait, AgentTimeInstant relativeToInstant, TimeSpan timeout
			, CancellationToken cancellationToken = default)
		{
			if (await TryAwaitOrTimeout(timer, taskToAwait, relativeToInstant, timeout, cancellationToken)) return;
			throw new TimeoutException();
		}

		/// <summary>
		/// It's recommended to use this method (or another TryAwaitOrTimeout or AwaitOrTimeout method)
		/// instead of just Task.WhenAny(taskToAwait, Task.Delay(timeout))
		/// because this method cancels the timer for timeout while <c>Task.Delay(timeout)</c>.
		/// If the number of “zombie” timer jobs starts becoming significant, performance could suffer.
		///
		/// For more detailed explanation see https://devblogs.microsoft.com/pfxteam/crafting-a-task-timeoutafter-method/
		/// </summary>
		/// <exception cref="TimeoutException">Thrown when timeout expires before <c>taskToAwait</c> completes</exception>
		/// <returns>(<c>true</c>, result of <c>taskToAwait</c>) if <c>taskToAwait</c> completed before the timeout, <c>false</c> otherwise</returns>
		internal static async Task<TResult> AwaitOrTimeout<TResult>(this IAgentTimer timer, Task<TResult> taskToAwait
			, AgentTimeInstant relativeToInstant, TimeSpan timeout, CancellationToken cancellationToken = default)
		{
			var (hasTaskToAwaitCompletedBeforeTimeout, result) =
				await TryAwaitOrTimeout(timer, taskToAwait, relativeToInstant, timeout, cancellationToken);
			if (hasTaskToAwaitCompletedBeforeTimeout) return result;
			throw new TimeoutException();
		}
	}
}
