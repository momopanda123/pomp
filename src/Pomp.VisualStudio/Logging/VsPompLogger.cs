using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;

namespace Pomp.VisualStudio.Logging
{
    /// <summary>
    /// Thread-safe <see cref="IPompLogger"/>: writes to the "Pomp" Output window pane, to the ActivityLog
    /// (warnings and errors only) and to a rolling file under %LOCALAPPDATA%\Pomp\logs.
    /// Callable from any thread; all Visual Studio calls are marshalled to the UI thread. <see cref="Log"/> never does file I/O on
    /// the calling thread: the file is written by one background thread (<see cref="BackgroundLogFile"/>), so a line reaches the
    /// file a moment after the call returns.
    /// </summary>
    internal sealed class VsPompLogger : IPompLogger, IDisposable
    {
        /// <summary>Identifies the "Pomp" pane in the Output window.</summary>
        public static readonly Guid OutputPaneGuid = new Guid("8eb6b516-dabf-4537-ba41-62fae0c6dac6");

        public const string OutputPaneName = "Pomp";

        private const string ActivityLogSource = "Pomp";
        private const long MaxLogFileBytes = 2 * 1024 * 1024;
        private const int MaxPendingEntries = 5000;

        /// <summary>Most formatted lines waiting for the file writer; further lines are dropped (and reported once the writer caught up).</summary>
        private const int MaxQueuedFileLines = 5000;

        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly ConcurrentQueue<Entry> pending = new ConcurrentQueue<Entry>();
        private readonly BackgroundLogFile fileLog;
        private readonly string logFilePath;
        private readonly PompLogLevel minimumLevel;
        private int flushScheduled;
        private int pendingCount;
        private int disposed;
        private IVsOutputWindowPane? pane;

        public VsPompLogger(JoinableTaskFactory joinableTaskFactory, string dataDirectory)
        {
            this.joinableTaskFactory = joinableTaskFactory;
            var logsDirectory = Path.Combine(dataDirectory, "logs");
            logFilePath = Path.Combine(logsDirectory, "pomp-vs.log");
            fileLog = new BackgroundLogFile(logFilePath, MaxLogFileBytes, MaxQueuedFileLines, DescribeDroppedLines);
#if DEBUG
            minimumLevel = PompLogLevel.Debug;
#else
            minimumLevel = string.Equals(Environment.GetEnvironmentVariable("POMP_LOG_DEBUG"), "1", StringComparison.Ordinal)
                ? PompLogLevel.Debug
                : PompLogLevel.Info;
#endif
        }

        /// <summary>Full path of the log file (also used by "Show log" fallbacks).</summary>
        public string LogFilePath => logFilePath;

        public void Log(PompLogLevel level, string message, Exception? exception = null)
        {
            if (level < minimumLevel || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var text = Format(level, message, exception);

            // Queued for the background writer: no file I/O on this (possibly UI) thread.
            fileLog.Enqueue(text);

            if (Interlocked.Increment(ref pendingCount) > MaxPendingEntries)
            {
                // The UI thread is not draining (busy or shutting down): drop instead of growing without bound.
                Interlocked.Decrement(ref pendingCount);
                return;
            }

            pending.Enqueue(new Entry(level, text));
            ScheduleFlush();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            // Only signals the writer to write out what is queued and finish: Dispose runs on the UI thread and must not wait for disk I/O.
            fileLog.Dispose();
            pane = null;
        }

        private static string DescribeDroppedLines(int count) =>
            Format(PompLogLevel.Warning, count.ToString(CultureInfo.InvariantCulture) + " log lines were dropped because the log writer could not keep up.", null);

        private static string Format(PompLogLevel level, string message, Exception? exception)
        {
            var sb = new StringBuilder(message.Length + 48);
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ").Append(message);
            if (exception != null)
            {
                sb.Append(Environment.NewLine).Append(exception);
            }

            return sb.ToString();
        }

        private void ScheduleFlush()
        {
            if (Interlocked.CompareExchange(ref flushScheduled, 1, 0) != 0)
            {
                return;
            }

            _ = joinableTaskFactory.RunAsync(FlushOnMainThreadAsync);
        }

        private async System.Threading.Tasks.Task FlushOnMainThreadAsync()
        {
            try
            {
                await joinableTaskFactory.SwitchToMainThreadAsync();
                Flush();
            }
            catch (Exception)
            {
                // Shutting down or cancelled; the file log already has the entries.
            }
            finally
            {
                Volatile.Write(ref flushScheduled, 0);
                if (!pending.IsEmpty && Volatile.Read(ref disposed) == 0)
                {
                    ScheduleFlush();
                }
            }
        }

        private void Flush()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var outputPane = EnsurePane();
            var batch = new StringBuilder();
            var count = 0;
            while (count < 500 && pending.TryDequeue(out var entry))
            {
                count++;
                Interlocked.Decrement(ref pendingCount);
                batch.AppendLine(entry.Text);

                if (entry.Level == PompLogLevel.Error)
                {
                    ActivityLog.LogError(ActivityLogSource, entry.Text);
                }
                else if (entry.Level == PompLogLevel.Warning)
                {
                    ActivityLog.LogWarning(ActivityLogSource, entry.Text);
                }
            }

            if (batch.Length > 0 && outputPane != null)
            {
                outputPane.OutputStringThreadSafe(batch.ToString());
            }
        }

        private IVsOutputWindowPane? EnsurePane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (pane != null)
            {
                return pane;
            }

            try
            {
                if (!(Package.GetGlobalService(typeof(SVsOutputWindow)) is IVsOutputWindow outputWindow))
                {
                    return null;
                }

                var paneGuid = OutputPaneGuid;
                outputWindow.CreatePane(ref paneGuid, OutputPaneName, 1, 0);
                if (outputWindow.GetPane(ref paneGuid, out var created) == 0)
                {
                    pane = created;
                }
            }
            catch (Exception)
            {
                pane = null;
            }

            return pane;
        }

        /// <summary>Brings the "Pomp" pane to the front of the Output window.</summary>
        internal void ActivatePane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var outputPane = EnsurePane();
            outputPane?.Activate();
        }

        private readonly struct Entry
        {
            public Entry(PompLogLevel level, string text)
            {
                Level = level;
                Text = text;
            }

            public PompLogLevel Level { get; }

            public string Text { get; }
        }
    }
}
