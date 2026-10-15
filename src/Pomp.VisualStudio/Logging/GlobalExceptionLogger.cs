using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Pomp.Diagnostics;

namespace Pomp.VisualStudio.Logging
{
    /// <summary>
    /// Logs unhandled exceptions to the ActivityLog (through <see cref="IPompLogger"/>): AppDomain-level, unobserved task
    /// exceptions and UI dispatcher exceptions. Only exceptions with a Pomp frame are attributed to Pomp; nothing is marked
    /// as handled, Visual Studio's own handling is untouched.
    /// </summary>
    internal sealed class GlobalExceptionLogger : IDisposable
    {
        private readonly IPompLogger logger;
        private Dispatcher? dispatcher;
        private bool installed;

        public GlobalExceptionLogger(IPompLogger logger)
        {
            this.logger = logger;
        }

        /// <summary>Hooks the handlers. Call on the UI thread (the dispatcher hook binds to the calling thread).</summary>
        public void Install()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (installed)
            {
                return;
            }

            installed = true;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += OnDispatcherUnhandledException;
        }

        public void Dispose()
        {
            if (!installed)
            {
                return;
            }

            installed = false;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            if (dispatcher != null)
            {
                dispatcher.UnhandledException -= OnDispatcherUnhandledException;
                dispatcher = null;
            }
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            if (InvolvesPomp(exception))
            {
                logger.Error($"Unhandled exception (terminating={e.IsTerminating}, Pomp frames present=True).", exception);
                return;
            }

            // Somebody else's exception (another extension, Visual Studio itself): it must not show up as a Pomp error in the
            // ActivityLog. One Info line (file log and Output pane only, never the ActivityLog) is enough for a support case.
            logger.Info($"Unhandled exception without Pomp frames (terminating={e.IsTerminating}): {Describe(e.ExceptionObject, exception)}");
        }

        private static string Describe(object? exceptionObject, Exception? exception)
        {
            if (exception == null)
            {
                return exceptionObject?.ToString() ?? "(no exception object)";
            }

            var message = exception.Message ?? string.Empty;
            var lineEnd = message.IndexOfAny(new[] { '\r', '\n' });
            return exception.GetType().FullName + ": " + (lineEnd >= 0 ? message.Substring(0, lineEnd) : message);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (InvolvesPomp(e.Exception))
            {
                logger.Error("Unobserved task exception.", e.Exception);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (InvolvesPomp(e.Exception))
            {
                logger.Error("Unhandled UI thread exception.", e.Exception);
            }
        }

        private static bool InvolvesPomp(Exception? exception)
        {
            if (exception == null)
            {
                return false;
            }

            var text = exception.StackTrace ?? string.Empty;
            if (text.IndexOf(" Pomp.", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            var aggregate = exception as AggregateException;
            if (aggregate != null)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (InvolvesPomp(inner))
                    {
                        return true;
                    }
                }
            }

            return InvolvesPomp(exception.InnerException);
        }
    }
}
