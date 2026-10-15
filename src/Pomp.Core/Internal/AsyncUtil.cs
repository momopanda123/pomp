using System;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Diagnostics;

namespace Pomp.Internal
{
    /// <summary>Small async helpers missing from netstandard2.0 (no Task.WaitAsync, no fire-and-forget guard).</summary>
    internal static class AsyncUtil
    {
        /// <summary>Awaits <paramref name="task"/> but stops waiting (throwing) when the token is cancelled; the task itself keeps running.</summary>
        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                return await task.ConfigureAwait(false);
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
            {
                if (task != await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return await task.ConfigureAwait(false);
        }

        /// <summary>Non-generic overload of <see cref="WithCancellation{T}"/>.</summary>
        public static async Task WithCancellation(this Task task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
            {
                if (task != await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            await task.ConfigureAwait(false);
        }

        /// <summary>Waits until the task completes or the timeout elapses. Returns true when the task completed (successfully or not).</summary>
        public static async Task<bool> WaitForAsync(Task task, TimeSpan timeout)
        {
            if (task.IsCompleted)
            {
                return true;
            }

            using (var delayCts = new CancellationTokenSource())
            {
                var delay = Task.Delay(timeout, delayCts.Token);
                var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (winner == task)
                {
                    delayCts.Cancel();
                    return true;
                }

                return false;
            }
        }

        /// <summary>Runs <paramref name="task"/> without awaiting it; failures are logged instead of going unobserved.</summary>
        public static void Forget(this Task task, IPompLogger logger, string description)
        {
            if (task.IsCompleted)
            {
                if (task.IsFaulted)
                {
                    LogFailure(logger, description, task.Exception);
                }

                return;
            }

            task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        LogFailure(logger, description, t.Exception);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Marks a task's exception as observed without logging (best-effort abandoned work).</summary>
        public static void Observe(this Task task)
        {
            if (task.IsCompleted)
            {
                var ignored = task.Exception;
                return;
            }

            task.ContinueWith(
                t =>
                {
                    var ignored = t.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void LogFailure(IPompLogger logger, string description, AggregateException? exception)
        {
            var inner = exception?.GetBaseException();
            if (inner is OperationCanceledException)
            {
                return;
            }

            try
            {
                logger.Error(description + " failed: " + inner?.Message, inner);
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
