using System;
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

namespace Pomp.Features
{
    /// <summary>
    /// Runs <c>omp usage --json</c> hidden (no window, redirected output, never an interactive prompt): the process is started through
    /// the session's launcher, so a test can substitute it. The output is bounded in time and size; a process that exceeds either is
    /// ended. Failures are reported with fixed, credential-free texts (stderr only reaches the debug log, without URL queries).
    /// </summary>
    internal static class OmpUsageRunner
    {
        internal const int MaxOutputBytes = 2 * 1024 * 1024;
        private const int MaxErrorBytes = 16 * 1024;
        private static readonly Regex UrlQuery = new Regex("(https?://[^\\s?#\"']*)[?#]\\S*", RegexOptions.CultureInvariant);

        /// <returns>The complete standard output of a run that exited with code 0.</returns>
        /// <exception cref="ProviderSetupException">The run could not start, timed out, printed too much or failed.</exception>
        public static async Task<string> RunAsync(IOmpProcessLauncher launcher, OmpLaunchOptions launch, TimeSpan timeout, int maxOutputBytes, IPompLogger logger, CancellationToken cancellationToken)
        {
            IOmpProcess process;
            try { process = launcher.Start(launch); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                logger.Warn("omp usage could not be started: " + ex.GetType().Name);
                throw new ProviderSetupException(SetupText.Get("사용량을 확인하지 못했습니다.", "Usage could not be checked."));
            }

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

                    var overflow = 0;
                    var stdout = ReadBoundedAsync(process.StandardOutput, maxOutputBytes, () =>
                    {
                        Interlocked.Exchange(ref overflow, 1);
                        Kill(process, logger);
                    });
                    var stderr = ReadBoundedAsync(process.StandardError, MaxErrorBytes, null);
                    var all = Task.WhenAll(stdout, stderr, exited.Task);
                    using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        limit.CancelAfter(timeout);
                        try { await all.WithCancellation(limit.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            Kill(process, logger);
                            all.Observe();
                            if (cancellationToken.IsCancellationRequested) throw;
                            throw new ProviderSetupException(SetupText.Get(
                                "사용량 조회가 시간 초과되었습니다 (" + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "초).",
                                "The usage lookup timed out (" + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s)."));
                        }
                    }

                    if (Volatile.Read(ref overflow) == 1)
                        throw new ProviderSetupException(SetupText.Get("사용량 데이터가 너무 많습니다.", "The usage data is too large."));
                    var code = process.ExitCode;
                    if (code.HasValue && code.Value != 0)
                    {
                        logger.Debug("omp usage exited with code " + code.Value.ToString(CultureInfo.InvariantCulture) + ": " + Describe(stderr.Result));
                        throw new ProviderSetupException(SetupText.Get(
                            "사용량 조회가 코드 " + code.Value.ToString(CultureInfo.InvariantCulture) + "(으)로 끝났습니다.",
                            "The usage lookup exited with code " + code.Value.ToString(CultureInfo.InvariantCulture) + "."));
                    }

                    return new UTF8Encoding(false).GetString(stdout.Result);
                }
                finally { process.Exited -= onExited; }
            }
        }

        private static void Kill(IOmpProcess process, IPompLogger logger)
        {
            try { process.Kill(); }
            catch (Exception ex) { logger.Debug("Killing omp usage failed: " + ex.GetType().Name); }
        }

        private static string Describe(byte[] stderr)
        {
            var text = SetupSanitize.Line(new UTF8Encoding(false).GetString(stderr), 300);
            return text == null ? "(no stderr)" : UrlQuery.Replace(text, "$1?...");
        }

        /// <summary>Reads to the end of the stream keeping at most <paramref name="max"/> bytes; <paramref name="onOverflow"/> (when given) stops the read instead of discarding. Shared with <see cref="OmpLogoutRunner"/>.</summary>
        internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int max, Action? onOverflow)
        {
            var buffer = new byte[8192];
            using (var memory = new MemoryStream())
            {
                while (true)
                {
                    int read;
                    try { read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) { break; }
                    if (read == 0) break;
                    var room = max - (int)memory.Length;
                    if (read > room)
                    {
                        if (onOverflow != null)
                        {
                            onOverflow();
                            break;
                        }

                        // Keep draining so the process never blocks on a full pipe.
                        if (room > 0) memory.Write(buffer, 0, room);
                        continue;
                    }

                    memory.Write(buffer, 0, read);
                }

                return memory.ToArray();
            }
        }
    }

    /// <summary>
    /// Turns <c>omp usage --json</c> output (<c>{ generatedAt, reports: [...], accountsWithoutUsage, disabledCredentials, capacity }</c>) into the
    /// <c>usage</c> object of <c>setup/state</c>. Everything is parsed defensively: malformed entries are dropped, sizes are capped
    /// (64 reports, 32 limits each, 200 characters per string, no control characters, about 256 KiB in total) and times become ISO 8601 UTC.
    /// </summary>
    internal static class OmpUsageReader
    {
        internal const int MaxReports = 64;
        internal const int MaxLimits = 32;
        private const int MaxText = 200;
        private const int MaxResultChars = 256 * 1024;
        private static readonly string[] Statuses = { "ok", "warning", "exhausted", "unknown" };

        /// <exception cref="ProviderSetupException">The text is not usage output at all.</exception>
        public static JObject Parse(string? text, DateTimeOffset now)
        {
            var root = ReadRoot(text);
            if (root == null || !(root["reports"] is JArray reports))
                throw new ProviderSetupException(SetupText.Get("사용량 데이터가 올바르지 않습니다.", "The usage data is invalid."));
            var result = new JArray();
            var size = 0;
            foreach (var item in reports.OfType<JObject>())
            {
                var report = ParseReport(item);
                if (report == null) continue;
                var length = report.ToString(Formatting.None).Length;
                if (size + length > MaxResultChars) break;
                size += length;
                result.Add(report);
                if (result.Count == MaxReports) break;
            }

            return new JObject { ["fetchedAt"] = Time(root["generatedAt"]) ?? Iso(now), ["reports"] = result };
        }

        /// <summary>The previous usage (or an empty one) carrying the reason the latest run failed.</summary>
        public static JObject WithError(JObject? previous, string error, DateTimeOffset now)
        {
            var result = previous != null ? (JObject)previous.DeepClone() : new JObject { ["fetchedAt"] = Iso(now), ["reports"] = new JArray() };
            result["error"] = string.IsNullOrEmpty(error) ? SetupText.Get("사용량을 불러오지 못했습니다.", "Usage could not be loaded.") : error;
            return result;
        }

        private static JObject? ReadRoot(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var start = text!.IndexOf('{');
            if (start < 0) return null;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text.Substring(start)))
                {
                    MaxDepth = 16, DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double,
                })
                {
                    return JObject.Load(reader);
                }
            }
            catch (JsonException) { return null; }
        }

        private static JObject? ParseReport(JObject report)
        {
            var provider = SetupSanitize.Line(report.GetString("provider"), MaxText);
            if (provider == null) return null;
            var limits = new JArray();
            foreach (var item in (report["limits"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var limit = ParseLimit(item);
                if (limit != null) limits.Add(limit);
                if (limits.Count == MaxLimits) break;
            }

            if (limits.Count == 0) return null;
            var result = new JObject { ["provider"] = provider };
            var account = AccountLabel(report);
            if (account != null) result["account"] = account;
            // The subscription plan when the provider names it (Codex: metadata.planType, e.g. "prolite", "plus", "pro").
            var metadata = report["metadata"] as JObject;
            var plan = SetupSanitize.Line(metadata?.GetString("planType") ?? metadata?.GetString("plan") ?? metadata?.GetString("subscriptionType"), 40);
            if (plan != null) result["plan"] = plan;
            result["limits"] = limits;
            return result;
        }

        private static JObject? ParseLimit(JObject limit)
        {
            var label = SetupSanitize.Line(limit.GetString("label"), MaxText) ?? SetupSanitize.Line(limit.GetString("id"), MaxText);
            if (label == null) return null;
            var amount = limit["amount"] as JObject;
            var window = limit["window"] as JObject;
            var windowLabel = window != null
                ? SetupSanitize.Line(window.GetString("label") ?? window.GetString("id"), MaxText)
                : limit["window"]?.Type == JTokenType.String ? SetupSanitize.Line((string?)limit["window"], MaxText) : null;
            var resetsAt = Time(window?["resetsAt"] ?? window?["resetAt"]) ?? Time(limit["resetsAt"]);
            var used = NonNegative(amount, "used");
            var max = NonNegative(amount, "limit");
            var usedFraction = Fraction(amount, "usedFraction");
            if (usedFraction == null)
            {
                var remaining = Fraction(amount, "remainingFraction");
                if (remaining != null) usedFraction = 1 - remaining.Value;
                else if (used != null && max != null && max.Value > 0) usedFraction = Math.Min(1, used.Value / max.Value);
            }

            var unit = SetupSanitize.Token(amount?.GetString("unit"));
            var statusText = limit.GetString("status");
            var status = Statuses.Contains(statusText) ? statusText : null;
            // A label alone says nothing.
            if (usedFraction == null && used == null && max == null && resetsAt == null && status == null) return null;
            var result = new JObject { ["label"] = label };
            if (windowLabel != null) result["window"] = windowLabel;
            if (resetsAt != null) result["resetsAt"] = resetsAt;
            if (usedFraction != null) result["usedFraction"] = usedFraction.Value;
            if (used != null) result["used"] = used.Value;
            if (max != null) result["limit"] = max.Value;
            if (unit != null) result["unit"] = unit;
            result["status"] = status ?? "unknown";
            // Which models the limit counts: shared by all of the account's models, or one model family ("tier": opus, fable,
            // spark...) / one model id. The page shows the shared limits and the current model's own.
            if (limit["scope"] is JObject scope)
            {
                if (scope["shared"]?.Type == JTokenType.Boolean) result["shared"] = (bool)scope["shared"]!;
                var tier = SetupSanitize.Token(scope.GetString("tier")?.ToLowerInvariant());
                if (tier != null) result["tier"] = tier;
                var modelId = SetupSanitize.Line(scope.GetString("modelId"), MaxText);
                if (modelId != null) result["modelId"] = modelId;
            }

            return result;
        }

        private static double? NonNegative(JObject? source, string name)
        {
            var value = source.GetDouble(name);
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= 0 ? value : null;
        }

        private static double? Fraction(JObject? source, string name)
        {
            var value = source.GetDouble(name);
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? Math.Max(0, Math.Min(1, value.Value)) : (double?)null;
        }

        /// <summary>The label of the account a report belongs to: an e-mail address first, then whatever account name or id omp reports.</summary>
        private static string? AccountLabel(JObject report)
        {
            var metadata = report["metadata"] as JObject;
            // An e-mail address is text; the other labels may be numeric ids.
            var label = Label(metadata?["email"], false) ?? Label(report["email"], false) ?? Label(report["account"], true) ?? Label(metadata?["account"], true)
                ?? Label(metadata?["username"], false) ?? Label(metadata?["accountId"], true) ?? Label(report["accountId"], true) ?? Label(metadata?["userId"], true);
            if (label != null) return label;
            foreach (var limit in (report["limits"] as JArray ?? new JArray()).OfType<JObject>())
            {
                label = Label((limit["scope"] as JObject)?["accountId"], true);
                if (label != null) return label;
            }

            return null;
        }

        private static string? Label(JToken? token, bool allowNumber)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.String: return SetupSanitize.Line((string?)token, MaxText);
                case JTokenType.Integer: return allowNumber ? ((long)token).ToString(CultureInfo.InvariantCulture) : null;
                case JTokenType.Object:
                    var obj = (JObject)token;
                    return Label(obj["email"], false) ?? Label(obj["name"], false) ?? Label(obj["id"], true) ?? Label(obj["accountId"], true);
                default: return null;
            }
        }

        /// <summary>An epoch time in milliseconds (or seconds), a numeric string or an ISO 8601 string as ISO 8601 UTC; null when it is none of these.</summary>
        internal static string? Time(JToken? token)
        {
            if (token == null) return null;
            DateTimeOffset value;
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    if (!FromEpoch((double)token, out value)) return null;
                    break;
                case JTokenType.String:
                    var text = ((string?)token)?.Trim();
                    if (string.IsNullOrEmpty(text) || text!.Length > 40) return null;
                    if (text.All(c => c >= '0' && c <= '9'))
                    {
                        if (!double.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || !FromEpoch(number, out value)) return null;
                    }
                    else if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)) return null;
                    break;
                default:
                    return null;
            }

            return value.Year < 2000 || value.Year > 2200 ? null : Iso(value);
        }

        private static bool FromEpoch(double number, out DateTimeOffset value)
        {
            value = default;
            if (double.IsNaN(number) || double.IsInfinity(number) || number < 0) return false;
            // Seconds for anything before 1973 read as milliseconds.
            var milliseconds = number < 1e11 ? number * 1000 : number;
            if (milliseconds > 253402300799999d) return false;
            value = DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
            return true;
        }

        private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
