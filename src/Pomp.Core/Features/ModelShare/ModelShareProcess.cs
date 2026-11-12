using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features.ModelShare
{
    /// <summary>
    /// One long-running omp server of model sharing (<c>auth-broker serve</c> / <c>auth-gateway serve</c>) started through the session's kind of
    /// launcher (kill-on-close job object). Its stdout and stderr are drained so it never blocks on a full pipe; only a short tail is kept,
    /// and that tail is redacted before anyone reads it.
    /// </summary>
    internal sealed class ModelShareProcess : IDisposable
    {
        private const int TailCharacters = 2048;
        private static readonly Regex TokenLike = new Regex("(?i)(bearer\\s+)?[A-Za-z0-9_\\-.~+/=]{24,}", RegexOptions.CultureInvariant);
        private readonly IOmpProcess _process;
        private readonly IReadOnlyList<string> _secrets;
        private readonly StringBuilder _tail = new StringBuilder();
        private readonly object _tailLock = new object();
        private readonly Action<ModelShareProcess> _onExit;
        private int _disposed;
        private int _exitReported;

        private ModelShareProcess(IOmpProcess process, string name, IReadOnlyList<string> secrets, Action<ModelShareProcess> onExit)
        {
            _process = process;
            Name = name;
            _secrets = secrets;
            _onExit = onExit;
        }

        /// <summary><c>auth-broker</c> or <c>auth-gateway</c> (shown in texts).</summary>
        public string Name { get; }

        public bool HasExited => _process.HasExited;

        public int? ExitCode => _process.ExitCode;

        /// <summary>Starts the process and its drains; <paramref name="onExit"/> runs once on any thread when it exits (also when it already had).</summary>
        public static ModelShareProcess Start(IOmpProcessLauncher launcher, OmpLaunchOptions launch, string name, IReadOnlyList<string> secrets, IPompLogger logger, Action<ModelShareProcess> onExit)
        {
            var process = launcher.Start(launch);
            var server = new ModelShareProcess(process, name, secrets, onExit);
            process.Exited += server.OnExited;
            Task.Run(() => server.DrainAsync(process.StandardOutput)).Forget(logger, name + " output");
            Task.Run(() => server.DrainAsync(process.StandardError)).Forget(logger, name + " errors");
            if (process.HasExited) server.OnExited(process, EventArgs.Empty);
            return server;
        }

        /// <summary>The last output lines with token-like text removed, as one short line; null when it printed nothing.</summary>
        public string? Tail()
        {
            string text;
            lock (_tailLock) text = _tail.ToString();
            return Redact(text, _secrets, 300);
        }

        /// <summary>Removes the given secrets and anything token-like (long base64/hex runs, bearer values) and bounds the text to one line.</summary>
        internal static string? Redact(string? text, IEnumerable<string> secrets, int max)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var value = text!;
            foreach (var secret in secrets)
            {
                if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "***");
            }

            value = TokenLike.Replace(value, "***");
            return SetupSanitize.Line(value, max);
        }

        private async Task DrainAsync(Stream stream)
        {
            var decoder = new UTF8Encoding(false).GetDecoder();
            var buffer = new byte[4096];
            var chars = new char[new UTF8Encoding(false).GetMaxCharCount(buffer.Length)];
            while (true)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) { return; }
                if (read == 0) return;
                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                lock (_tailLock)
                {
                    _tail.Append(chars, 0, count);
                    if (_tail.Length > TailCharacters) _tail.Remove(0, _tail.Length - TailCharacters);
                }
            }
        }

        private void OnExited(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _exitReported, 1) == 1) return;
            _onExit(this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _process.Exited -= OnExited;
            try { _process.Kill(); }
            catch (Exception) { /* Already gone. */ }
            try { _process.Dispose(); }
            catch (Exception) { /* Shutdown is best effort. */ }
        }
    }

    /// <summary>Runs <c>omp auth-broker token --json</c> / <c>omp auth-gateway token --json</c> hidden and returns the printed token (never logged).</summary>
    internal static class ModelShareTokenCommand
    {
        private const int MaxOutputBytes = 64 * 1024;
        private const int MaxErrorBytes = 16 * 1024;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <exception cref="ProviderSetupException">The command could not start, timed out, failed or printed no token.</exception>
        public static async Task<string> RunAsync(IOmpProcessLauncher launcher, OmpLaunchOptions launch, string name, IPompLogger logger, CancellationToken cancellationToken)
        {
            IOmpProcess process;
            try { process = launcher.Start(launch); }
            catch (Exception ex)
            {
                logger.Warn("omp " + name + " token could not be started: " + ex.GetType().Name);
                throw new ProviderSetupException(FailedText(name));
            }

            byte[]? stdout = null;
            using (process)
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler onExited = (sender, e) => exited.TrySetResult(true);
                process.Exited += onExited;
                try
                {
                    if (process.HasExited) exited.TrySetResult(true);
                    try { process.StandardInput.Dispose(); }
                    catch (Exception) { /* Nothing is written to it. */ }

                    var output = OmpUsageRunner.ReadBoundedAsync(process.StandardOutput, MaxOutputBytes, null);
                    var errors = OmpUsageRunner.ReadBoundedAsync(process.StandardError, MaxErrorBytes, null);
                    var all = Task.WhenAll(output, errors, exited.Task);
                    using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        limit.CancelAfter(Timeout);
                        try { await all.WithCancellation(limit.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            try { process.Kill(); }
                            catch (Exception) { /* Already gone. */ }
                            all.Observe();
                            cancellationToken.ThrowIfCancellationRequested();
                            logger.Warn("omp " + name + " token timed out.");
                            throw new ProviderSetupException(FailedText(name));
                        }
                    }

                    stdout = output.Result;
                    var code = process.ExitCode;
                    if (code.HasValue && code.Value != 0)
                    {
                        var detail = ModelShareProcess.Redact(new UTF8Encoding(false).GetString(errors.Result), Array.Empty<string>(), 300);
                        logger.Warn("omp " + name + " token exited with code " + code.Value.ToString(CultureInfo.InvariantCulture) + ": " + (detail ?? "(no stderr)"));
                        throw new ProviderSetupException(FailedText(name));
                    }

                    var token = Parse(new UTF8Encoding(false).GetString(stdout));
                    if (token == null)
                    {
                        logger.Warn("omp " + name + " token printed no usable token.");
                        throw new ProviderSetupException(FailedText(name));
                    }

                    return token;
                }
                finally
                {
                    process.Exited -= onExited;
                    if (stdout != null) Array.Clear(stdout, 0, stdout.Length);
                }
            }
        }

        /// <summary>The token from <c>--json</c> output: a JSON string, or an object with a string <c>token</c>.</summary>
        internal static string? Parse(string text)
        {
            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text.Trim())) { DateParseHandling = DateParseHandling.None })
                {
                    root = JToken.Load(reader);
                }
            }
            catch (JsonException) { return null; }

            var value = root.Type == JTokenType.String ? (string?)root : root is JObject item && item["token"]?.Type == JTokenType.String ? (string?)item["token"] : null;
            if (string.IsNullOrEmpty(value) || value!.Length > 4096 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
            return value;
        }

        private static string FailedText(string name) => SetupText.Get(
            "모델 공유용 " + name + " 토큰을 준비하지 못했습니다.",
            "The " + name + " token for model sharing could not be prepared.");
    }
}
