using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pomp.Bridge
{
    /// <summary>
    /// Builds Windows command lines. netstandard2.0 has no <c>ProcessStartInfo.ArgumentList</c>, so arguments are quoted
    /// here following the CommandLineToArgvW / MSVCRT rules, and batch files (<c>.cmd</c>/<c>.bat</c>) are wrapped in
    /// <c>cmd.exe /d /e:on /v:off /s /c "..."</c> with cmd-specific quoting.
    /// </summary>
    internal static class WindowsCommandLine
    {
        private static readonly char[] NeedsQuoting = { ' ', '\t', '\n', '\v', '"' };

        /// <summary>Quotes one argument so that CommandLineToArgvW (and the MSVC runtime) parse it back to exactly the same string.</summary>
        public static string QuoteArgument(string argument)
        {
            if (argument == null)
            {
                throw new ArgumentNullException(nameof(argument));
            }

            if (argument.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("A command-line argument cannot contain NUL.", nameof(argument));
            }

            if (argument.Length > 0 && argument.IndexOfAny(NeedsQuoting) < 0)
            {
                return argument;
            }

            var quoted = new StringBuilder(argument.Length + 2);
            quoted.Append('"');
            var i = 0;
            while (true)
            {
                var backslashes = 0;
                while (i < argument.Length && argument[i] == '\\')
                {
                    i++;
                    backslashes++;
                }

                if (i == argument.Length)
                {
                    // Backslashes before the closing quote must be doubled.
                    quoted.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[i] == '"')
                {
                    quoted.Append('\\', (backslashes * 2) + 1);
                    quoted.Append('"');
                }
                else
                {
                    quoted.Append('\\', backslashes);
                    quoted.Append(argument[i]);
                }

                i++;
            }

            quoted.Append('"');
            return quoted.ToString();
        }

        /// <summary>Joins arguments into one command-line tail (executable not included).</summary>
        public static string Join(IEnumerable<string> arguments)
        {
            var builder = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteArgument(argument));
            }

            return builder.ToString();
        }

        /// <summary>True for <c>.cmd</c>/<c>.bat</c> files, which CreateProcess cannot run directly with safe argument passing.</summary>
        public static bool IsBatchFile(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The <c>Arguments</c> string for running a batch file through the command interpreter:
        /// <c>/d /e:on /v:off /s /c "&lt;batch&gt; args..."</c>. With <c>/s</c> cmd strips exactly the outer quote pair and runs the rest
        /// verbatim; <c>/e:on</c> keeps the <c>%var:~,%</c> escaping of <see cref="QuoteForCmd"/> working and <c>/v:off</c> stops a
        /// machine-wide delayed-expansion setting from expanding <c>!name!</c> inside arguments.
        /// </summary>
        public static string BuildCmdArguments(string batchPath, IEnumerable<string> arguments)
        {
            if (batchPath == null)
            {
                throw new ArgumentNullException(nameof(batchPath));
            }

            var inner = new StringBuilder();
            inner.Append(QuoteForCmd(batchPath, forceQuotes: true));
            foreach (var argument in arguments)
            {
                inner.Append(' ');
                inner.Append(QuoteForCmd(argument, forceQuotes: false));
            }

            return "/d /e:on /v:off /s /c \"" + inner + "\"";
        }

        /// <summary>
        /// Quotes an argument for a command line that cmd.exe parses first and a batch file/program parses second.
        /// Inside double quotes cmd treats <c>&amp; | &lt; &gt; ( ) ^</c> literally; embedded quotes are doubled (which cmd's
        /// quote tracking survives and the MSVC runtime decodes back to one quote), backslashes before a quote are doubled,
        /// and <c>%</c> is neutralised with the <c>%%cd:~,%</c> trick so environment variables are never expanded.
        /// </summary>
        internal static string QuoteForCmd(string argument, bool forceQuotes)
        {
            if (argument == null)
            {
                throw new ArgumentNullException(nameof(argument));
            }

            if (argument.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new ArgumentException("Arguments passed through cmd.exe cannot contain line breaks or NUL.", nameof(argument));
            }

            if (!forceQuotes && argument.Length > 0 && IsSafeForCmd(argument))
            {
                return argument;
            }

            var quoted = new StringBuilder(argument.Length + 8);
            quoted.Append('"');
            var backslashes = 0;
            foreach (var c in argument)
            {
                switch (c)
                {
                    case '\\':
                        backslashes++;
                        break;
                    case '"':
                        quoted.Append('\\', backslashes * 2);
                        backslashes = 0;
                        quoted.Append("\"\"");
                        break;
                    case '%':
                        quoted.Append('\\', backslashes);
                        backslashes = 0;
                        quoted.Append("%%cd:~,%");
                        break;
                    default:
                        quoted.Append('\\', backslashes);
                        backslashes = 0;
                        quoted.Append(c);
                        break;
                }
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        private static bool IsSafeForCmd(string argument)
        {
            foreach (var c in argument)
            {
                var safe = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '_' || c == '-' || c == '.' || c == ':' || c == '\\' || c == '/' || c == '@' || c == '+';
                if (!safe)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
