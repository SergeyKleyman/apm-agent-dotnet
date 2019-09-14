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
			, AgentTimeInstant relativeToInstant, TimeSpan timeout)
		{
			var timeOutTimerTcs = new CancellationTokenSource();
			Task timeOutTask = null;
			try
			{
				timeOutTask = timer.Delay(relativeToInstant, timeout, timeOutTimerTcs.Token);
				var completedTask = await Task.WhenAny(taskToAwait, timeOutTask);
				if (completedTask == taskToAwait)
				{
					await taskToAwait;
					return true;
				}

				Assertion.IfEnabled?.That(completedTask == timeOutTask
					, $"{nameof(completedTask)}: {completedTask}, {nameof(timeOutTask)}: timeOutTask, {nameof(taskToAwait)}: taskToAwait");
				// no need to cancel timeout timer if it has been triggered
				timeOutTask = null;
				return false;
			}
			finally
			{
				if (timeOutTask != null) timeOutTimerTcs.Cancel();
				timeOutTimerTcs.Dispose();
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
			, AgentTimeInstant relativeToInstant, TimeSpan timeout)
		{
			var hasTaskToAwaitCompletedBeforeTimeout = await TryAwaitOrTimeout(timer, (Task)taskToAwait, relativeToInstant, timeout);
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
		internal static async Task AwaitOrTimeout(this IAgentTimer timer, Task taskToAwait, AgentTimeInstant relativeToInstant, TimeSpan timeout)
		{
			if (await TryAwaitOrTimeout(timer, taskToAwait, relativeToInstant, timeout)) return;
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
			, AgentTimeInstant relativeToInstant, TimeSpan timeout)
		{
			var (hasTaskToAwaitCompletedBeforeTimeout, result) = await TryAwaitOrTimeout(timer, taskToAwait, relativeToInstant, timeout);
			if (hasTaskToAwaitCompletedBeforeTimeout) return result;
			throw new TimeoutException();
		}
	}
}
