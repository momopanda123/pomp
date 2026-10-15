using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Diagnostics;

namespace Pomp.Internal
{
    /// <summary>Result of a guarded adapter call: <see cref="Succeeded"/> is false when it timed out or threw.</summary>
    internal readonly struct GuardResult<T>
    {
        public GuardResult(bool succeeded, T value)
        {
            Succeeded = succeeded;
            Value = value;
        }

        public bool Succeeded { get; }

        public T Value { get; }
    }

    /// <summary>
    /// Runs a Visual Studio adapter call defensively: a timeout, exceptions and cancellation all turn into "failed".
    /// Adapters switch to the UI thread internally and could stall (modal dialog, busy UI); nothing in pomp may hang on them.
    /// </summary>
    internal static class AdapterGuard
    {
        public static async Task<GuardResult<T>> TryRunAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, IPompLogger logger, string what, CancellationToken cancellationToken)
        {
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    linked.CancelAfter(timeout);
                    var task = call(linked.Token);
                    if (await AsyncUtil.WaitForAsync(task, timeout).ConfigureAwait(false))
                    {
                        return new GuardResult<T>(true, await task.ConfigureAwait(false));
                    }

                    linked.Cancel();
                    task.Observe();
                    logger.Debug(what + " timed out after " + timeout.TotalSeconds.ToString("0.#") + "s.");
                    return new GuardResult<T>(false, default!);
                }
            }
            catch (OperationCanceledException)
            {
                return new GuardResult<T>(false, default!);
            }
            catch (Exception ex)
            {
                logger.Debug(what + " failed: " + ex.Message);
                return new GuardResult<T>(false, default!);
            }
        }

        /// <summary>Like <see cref="TryRunAsync{T}"/> but returns <c>default</c> (null for reference types) on failure.</summary>
        public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, IPompLogger logger, string what, CancellationToken cancellationToken)
        {
            var result = await TryRunAsync(call, timeout, logger, what, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.Value : default!;
        }
    }

    internal static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Like <see cref="WriteAllText(string, string)"/>, keeping a UTF-8 byte order mark when the file had one.</summary>
        public static void WriteAllText(string path, string content, bool byteOrderMark) =>
            WriteAllText(path, byteOrderMark ? "﻿" + content : content);

        /// <summary>Writes a file through a temp file in the same folder and an atomic replace, so readers never see a partial file.</summary>
        public static void WriteAllText(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content, Utf8NoBom);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(temp, path, null);
                        }
                        else
                        {
                            File.Move(temp, path);
                        }

                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        // A reader has the file open right now: retry shortly.
                        Thread.Sleep(20 * (attempt + 1));
                    }
                    catch (UnauthorizedAccessException) when (attempt < 4)
                    {
                        Thread.Sleep(20 * (attempt + 1));
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                    // Leave the stray temp file; it is harmless.
                }
            }
        }
    }

    internal static class ArgumentSplitter
    {
        /// <summary>
        /// Splits a raw argument string like a Windows command line (CommandLineToArgvW rules): whitespace separates, double
        /// quotes group, <c>\"</c> is a literal quote, backslashes are literal unless they precede a quote.
        /// </summary>
        public static List<string> Split(string? commandLine)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return result;
            }

            var text = commandLine!;
            var i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (i >= text.Length)
                {
                    break;
                }

                var argument = new StringBuilder();
                var inQuotes = false;
                var hadQuotes = false;
                while (i < text.Length)
                {
                    var backslashes = 0;
                    while (i < text.Length && text[i] == '\\')
                    {
                        backslashes++;
                        i++;
                    }

                    if (i < text.Length && text[i] == '"')
                    {
                        argument.Append('\\', backslashes / 2);
                        if (backslashes % 2 == 1)
                        {
                            argument.Append('"');
                        }
                        else if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            // "" inside a quoted section is a literal quote.
                            argument.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                            hadQuotes = true;
                        }

                        i++;
                        continue;
                    }

                    argument.Append('\\', backslashes);
                    if (i >= text.Length)
                    {
                        break;
                    }

                    if (!inQuotes && char.IsWhiteSpace(text[i]))
                    {
                        break;
                    }

                    argument.Append(text[i]);
                    i++;
                }

                if (argument.Length > 0 || hadQuotes)
                {
                    result.Add(argument.ToString());
                }
            }

            return result;
        }
    }
}
