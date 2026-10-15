using System;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Diagnostics;

namespace Pomp.VisualStudio.Interop
{
    /// <summary>Small helpers for fire-and-forget calls whose failures must still be observed and logged.</summary>
    internal static class AsyncHelpers
    {
        /// <summary>Observes <paramref name="task"/>; a failure is logged as a warning instead of going unobserved.</summary>
        public static void Forget(this Task task, IPompLogger logger, string description)
        {
            if (task.IsCompleted)
            {
                if (task.IsFaulted)
                {
                    logger.Warn(description + " failed.", task.Exception?.GetBaseException());
                }

                return;
            }

            _ = task.ContinueWith(
                t => logger.Warn(description + " failed.", t.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
