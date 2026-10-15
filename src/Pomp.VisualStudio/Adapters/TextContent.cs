namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Text helpers shared by the buffer adapter: stale-detection hash and line ending detection.</summary>
    internal static class TextContent
    {
        /// <summary>
        /// The <c>ContentHash</c> contract of <see cref="Pomp.Adapters.BufferSnapshot"/> (<see cref="Pomp.Adapters.ContentHash"/>): the
        /// very same function the tools use, so a hash computed here always matches theirs.
        /// </summary>
        public static string ComputeHash(string text) => Pomp.Adapters.ContentHash.Compute(text);

        /// <summary>The dominant line ending: "\r\n" when CRLF is at least as frequent as bare LF (and present), "\n" otherwise; "\r\n" for text without line breaks.</summary>
        public static string DetectLineEnding(string text)
        {
            var crlf = 0;
            var lf = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }

                if (i > 0 && text[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }

            if (crlf == 0 && lf == 0)
            {
                return "\r\n";
            }

            return crlf >= lf ? "\r\n" : "\n";
        }

        /// <summary>Cuts <paramref name="text"/> to <paramref name="maxChars"/>, appending a marker when it was longer.</summary>
        public static string Truncate(string? text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text!.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxChars) + "... [truncated " + (text.Length - maxChars).ToString() + " chars]";
        }
    }
}
