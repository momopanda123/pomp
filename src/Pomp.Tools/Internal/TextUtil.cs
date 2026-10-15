using System;
using System.Collections.Generic;
using System.Text;

namespace Pomp.Tools
{
    /// <summary>Text helpers shared by the tools: line splitting, content hashing and single-line shortening.</summary>
    internal static class TextUtil
    {
        /// <summary>Marks text that was cut (U+2026 HORIZONTAL ELLIPSIS).</summary>
        public const string Ellipsis = "\x2026";

        private const int BinarySniffLength = 8000;

        /// <summary>Converts CRLF and lone CR line endings to LF.</summary>
        public static string NormalizeLineEndings(string text)
        {
            if (text.IndexOf('\r') < 0)
            {
                return text;
            }

            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>
        /// Splits CR/LF-normalized text at LF and Visual Studio Unicode separators into addressable lines. Like omp's hashline, a trailing newline does not create
        /// an extra empty line, and empty text has no lines.
        /// </summary>
        public static List<string> SplitLines(string normalizedText)
        {
            var lines = new List<string>(normalizedText.Split(new[] { '\n', '\u0085', '\u2028', '\u2029' }));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

        /// <summary>The shared <see cref="Pomp.Adapters.ContentHash"/> of LF-normalized text (same value as the VS adapter computes).</summary>
        public static string ComputeContentHash(string normalizedText) => Pomp.Adapters.ContentHash.Compute(normalizedText);

        /// <summary>True for a 64-character hexadecimal string (a SHA-256 digest).</summary>
        public static bool IsContentHash(string? value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (var c in value)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The 4-uppercase-hex snapshot tag: the first four characters of the content hash.</summary>
        public static string TagFromHash(string contentHash) => contentHash.Substring(0, 4).ToUpperInvariant();

        /// <summary>Heuristic binary detection: a NUL character near the start of the text.</summary>
        public static bool LooksBinary(string text)
        {
            var length = Math.Min(text.Length, BinarySniffLength);
            return text.IndexOf('\0', 0, length) >= 0;
        }

        /// <summary>Collapses all whitespace runs (including line breaks) to single spaces and shortens the result.</summary>
        public static string ToSingleLine(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Math.Min(text!.Length, maxLength + 8));
            var pendingSpace = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return Truncate(builder.ToString(), maxLength);
        }

        /// <summary>Cuts <paramref name="text"/> to <paramref name="maxLength"/> characters and appends an ellipsis.</summary>
        public static string Truncate(string text, int maxLength)
        {
            if (text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, Math.Max(0, maxLength)) + Ellipsis;
        }
    }
}
