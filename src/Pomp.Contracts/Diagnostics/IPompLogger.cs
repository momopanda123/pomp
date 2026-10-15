using System;

namespace Pomp.Diagnostics
{
    public enum PompLogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
    }

    /// <summary>
    /// Logging sink. The VS host routes it to the ActivityLog and the "Pomp" output pane;
    /// tests use an in-memory or console implementation. Implementations must be thread-safe.
    /// </summary>
    public interface IPompLogger
    {
        void Log(PompLogLevel level, string message, Exception? exception = null);
    }

    public static class PompLoggerExtensions
    {
        public static void Debug(this IPompLogger logger, string message) => logger.Log(PompLogLevel.Debug, message);

        public static void Info(this IPompLogger logger, string message) => logger.Log(PompLogLevel.Info, message);

        public static void Warn(this IPompLogger logger, string message, Exception? exception = null) =>
            logger.Log(PompLogLevel.Warning, message, exception);

        public static void Error(this IPompLogger logger, string message, Exception? exception = null) =>
            logger.Log(PompLogLevel.Error, message, exception);
    }

    public sealed class NullPompLogger : IPompLogger
    {
        public static readonly NullPompLogger Instance = new NullPompLogger();

        private NullPompLogger()
        {
        }

        public void Log(PompLogLevel level, string message, Exception? exception = null)
        {
        }
    }
}
