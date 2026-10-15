using System;
using System.Runtime.InteropServices;
using System.Threading;
using Pomp.Hosting;

namespace Pomp.Tools
{
    /// <summary>
    /// A failure whose message is meant for the model as-is (validation problems, missing files, ...).
    /// Thrown from helpers and turned into <see cref="HostToolResult.Error"/> by the tool base class.
    /// </summary>
    internal sealed class ToolException : Exception
    {
        public ToolException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Maps exceptions thrown by adapters and helpers to short model-facing error results.</summary>
    internal static class ToolErrors
    {
        private const int MaxMessageLength = 300;

        public static HostToolResult FromException(string toolName, Exception exception, CancellationToken cancellationToken)
        {
            return HostToolResult.Error(Describe(toolName, exception, cancellationToken));
        }

        public static string Describe(string toolName, Exception exception, CancellationToken cancellationToken)
        {
            switch (exception)
            {
                case ToolException tool:
                    return toolName + ": " + tool.Message;
                case OperationCanceledException _ when cancellationToken.IsCancellationRequested:
                    return toolName + " was cancelled.";
                case OperationCanceledException _:
                    return toolName + ": the operation was cancelled or timed out inside Visual Studio.";
                case NotImplementedException _:
                    return toolName + " is not available: the Visual Studio adapter behind it is not implemented yet.";
                case TimeoutException _:
                    return toolName + " timed out.";
                case AggregateException aggregate when aggregate.InnerExceptions.Count == 1:
                    return Describe(toolName, aggregate.InnerExceptions[0], cancellationToken);
                case COMException com:
                    return toolName + ": " + PompLanguage.Pick("Visual Studio 오류", "Visual Studio error") + " (0x" + com.HResult.ToString("X8") + "): " + ComMessage(com);
                default:
                    return toolName + " failed: " + exception.GetType().Name + ": " + ShortMessage(exception);
            }
        }

        private static string ComMessage(COMException exception)
        {
            var text = ShortMessage(exception);
            // .NET appends the HRESULT already carried by the wrapper; keep the actual exception message once.
            var hresult = text.IndexOf("HRESULT", StringComparison.OrdinalIgnoreCase);
            var duplicate = hresult >= 0 ? text.LastIndexOf(" (", hresult, StringComparison.Ordinal) : -1;
            if (duplicate >= 0 && text.EndsWith(")", StringComparison.Ordinal))
                text = text.Substring(0, duplicate).TrimEnd();
            return text;
        }

        /// <summary>First line of the exception message, trimmed and length-limited (never a stack trace).</summary>
        public static string ShortMessage(Exception exception)
        {
            var message = exception.Message ?? string.Empty;
            var lineEnd = message.IndexOfAny(new[] { '\r', '\n' });
            if (lineEnd >= 0)
            {
                message = message.Substring(0, lineEnd);
            }

            message = message.Trim();
            if (message.Length == 0)
            {
                return "(no message)";
            }

            return TextUtil.Truncate(message, MaxMessageLength);
        }
    }
}
