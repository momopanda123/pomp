using System;
using System.Text;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>One-line rendering of Error List / build diagnostics, shared by read_error_list and build_solution.</summary>
    internal static class DiagnosticFormat
    {
        public const int MaxMessageLength = 400;

        public static string SeverityWord(ErrorSeverity severity)
        {
            switch (severity)
            {
                case ErrorSeverity.Error:
                    return "error";
                case ErrorSeverity.Warning:
                    return "warning";
                default:
                    return "message";
            }
        }

        /// <summary><c>severity code file(line,col): message [project]</c>; missing parts are left out.</summary>
        public static string FormatItem(ErrorListItem item, Func<string, string> displayPath)
        {
            var builder = new StringBuilder();
            builder.Append(SeverityWord(item.Severity));

            if (!string.IsNullOrWhiteSpace(item.Code))
            {
                builder.Append(' ').Append(item.Code!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.File))
            {
                builder.Append(' ').Append(displayPath(item.File!.Trim()));
                if (item.Line.HasValue && item.Line.Value > 0)
                {
                    builder.Append('(').Append(item.Line.Value);
                    if (item.Column.HasValue && item.Column.Value > 0)
                    {
                        builder.Append(',').Append(item.Column.Value);
                    }

                    builder.Append(')');
                }
            }

            var message = TextUtil.ToSingleLine(item.Description, MaxMessageLength);
            builder.Append(": ").Append(message.Length == 0 ? "(no message)" : message);

            if (!string.IsNullOrWhiteSpace(item.Project))
            {
                builder.Append(" [").Append(item.Project!.Trim()).Append(']');
            }

            return builder.ToString();
        }
    }
}
