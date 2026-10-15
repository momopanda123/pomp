namespace Pomp.Adapters
{
    /// <summary>Line separators recognized by the Visual Studio text model. CRLF is one separator.</summary>
    public static class TextLineBreaks
    {
        public static bool IsBreak(char value) => value == '\r' || value == '\n' || value == '\u0085'
            || value == '\u2028' || value == '\u2029';

        /// <summary>For patch syntax only. Source content hashes deliberately preserve Unicode separators.</summary>
        public static string NormalizePatch(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace('\u0085', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n');
    }
}
