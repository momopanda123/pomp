using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Json;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // The context meter's breakdown (docs/ui-protocol.md "session/info"): after each turn, and when a conversation is confirmed, the
    // engine's /context command runs through the host command queue and its output is parsed into session/info.contextUsage.breakdown.
    // The VS and MCP shares of the tool definitions are the host's estimates; the basket estimate answers workspace/countBasketTokens.
    public sealed partial class PompSession
    {
        private const string ContextCommand = "/context";
        private const string ContextCommandName = "context";
        private static readonly Regex AnsiCodes = new Regex("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        private static readonly Regex ContextHeadLine = new Regex(@"^\s*Context window:\s*(\d+)\s+tokens\s*\((\d+)%\s*used\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ContextRowLine = new Regex(@"^\s*(?<label>[A-Za-z][A-Za-z -]*?)\s+\[[^\]]*\]\s*(?<percent>\d+)%\s+(?<tokens>\d+)\s+tokens\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly object _contextLock = new object();
        private TaskCompletionSource<string>? _contextAnswer;
        private ContextBreakdownPayload? _contextBreakdown;
        private string? _contextBreakdownSession;
        private bool _contextRefreshQueued;
        /// <summary>The engine's catalog lists <c>context</c> as a builtin (guarded by _stateLock); a prompt template or skill of that name does not count.</summary>
        private bool _contextBuiltin;
        private string? _contextFailureSession;
        private int _contextFailures;
        /// <summary>After this many measurements of one conversation came back refused, late or unreadable, the host stops asking for it.</summary>
        private const int MaxContextFailures = 2;

        /// <summary>
        /// The engine's <c>/context</c> output as the page's breakdown, or null when <paramref name="text"/> is not that output
        /// (its first line names the window; every row is "label [bar] N% M tokens", colour codes removed). Rows the engine does not
        /// print are zero; unknown rows are ignored.
        /// </summary>
        public static ContextBreakdownPayload? ParseContextOutput(string? text, DateTime measuredAtUtc)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var plain = AnsiCodes.Replace(text!, string.Empty);
            var lines = plain.Split('\n');
            var head = lines.Select(line => ContextHeadLine.Match(line.TrimEnd('\r'))).FirstOrDefault(match => match.Success);
            if (head == null || !long.TryParse(head.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var window) || window <= 0) return null;
            var result = new ContextBreakdownPayload { ContextWindow = window, MeasuredAt = measuredAtUtc.ToString("o", CultureInfo.InvariantCulture) };
            var rows = 0;
            var messagesRow = false;
            foreach (var raw in lines)
            {
                var match = ContextRowLine.Match(raw.TrimEnd('\r'));
                if (!match.Success || !long.TryParse(match.Groups["tokens"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens)) continue;
                switch (match.Groups["label"].Value.Trim().ToLowerInvariant())
                {
                    case "system prompt": result.SystemPrompt = tokens; break;
                    case "system tools": result.SystemTools = tokens; break;
                    case "system context": result.SystemContext = tokens; break;
                    case "skills": result.Skills = tokens; break;
                    case "messages": result.Messages = tokens; messagesRow = true; break;
                    case "auto-compact buf":
                    case "auto-compact buffer": result.AutoCompactBuffer = tokens; break;
                    case "free": result.Free = tokens; break;
                    default: continue;
                }
                rows++;
            }
            result.MessagesEstimated = !messagesRow;
            return rows > 0 ? result : null;
        }

        /// <summary>
        /// A characters-based token estimate for text the engine has not tokenized (the basket, tool definitions): ASCII at four
        /// characters per token, every other character as one token. Never used where the engine reports a count.
        /// </summary>
        public static long EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            long ascii = 0, other = 0;
            foreach (var c in text!) { if (c < 128) ascii++; else other++; }
            return (ascii + 3) / 4 + other;
        }

        /// <summary>
        /// Splits the engine's tool-definition total into the VS host tools' and the MCP tools' shares, by the size of the definitions
        /// the host knows (its own with their schemas; MCP tools by name and description plus a schema allowance). The rest is the
        /// engine's own tools. Both shares are estimates and never exceed the total.
        /// </summary>
        public static void EstimateToolShares(ContextBreakdownPayload breakdown, IEnumerable<HostToolDefinition> hostTools, IEnumerable<ToolSelectionPayload> conversationTools)
        {
            if (breakdown == null) throw new ArgumentNullException(nameof(breakdown));
            long hostChars = 0;
            foreach (var definition in hostTools ?? Enumerable.Empty<HostToolDefinition>())
            {
                if (definition.Hidden == true) continue;
                hostChars += definition.Name.Length + definition.Description.Length + (definition.Parameters?.ToString(Formatting.None).Length ?? 0);
            }
            long mcpChars = 0;
            foreach (var tool in conversationTools ?? Enumerable.Empty<ToolSelectionPayload>())
            {
                if (!tool.Active || !string.Equals(tool.Group, "mcp", StringComparison.Ordinal)) continue;
                mcpChars += tool.Name.Length + (tool.Description?.Length ?? 0) + 160;
            }
            var host = Math.Min(breakdown.SystemTools, (hostChars + 3) / 4);
            var mcp = Math.Min(breakdown.SystemTools - host, (mcpChars + 3) / 4);
            breakdown.HostTools = host;
            breakdown.McpTools = mcp;
        }

        /// <summary>
        /// When the engine printed no "Messages" row (<see cref="ContextBreakdownPayload.MessagesEstimated"/>), the conversation share is
        /// the engine's usage total (<paramref name="usageTokens"/>, the last request's size) minus the rows it did print; never negative,
        /// and zero while the usage is unknown.
        /// </summary>
        public static void EstimateMessages(ContextBreakdownPayload breakdown, long usageTokens)
        {
            if (!breakdown.MessagesEstimated) return;
            var printed = breakdown.SystemPrompt + breakdown.SystemTools + breakdown.SystemContext + breakdown.Skills;
            breakdown.Messages = usageTokens > printed ? usageTokens - printed : 0;
        }

        /// <summary>True once /context was run (or refused by the engine) for <paramref name="sessionId"/>: ApplyState then stops asking for it.</summary>
        private bool ContextMeasured(string? sessionId)
        {
            lock (_contextLock) return !string.IsNullOrEmpty(sessionId) && string.Equals(sessionId, _contextBreakdownSession, StringComparison.Ordinal);
        }

        /// <summary>The breakdown measured for <paramref name="sessionId"/>, attached to a fresh contextUsage of session/info; null for another conversation.</summary>
        private ContextBreakdownPayload? ContextBreakdownFor(string? sessionId)
        {
            lock (_contextLock) return !string.IsNullOrEmpty(sessionId) && string.Equals(sessionId, _contextBreakdownSession, StringComparison.Ordinal) ? _contextBreakdown : null;
        }

        /// <summary>The conversation was compacted: its breakdown no longer holds, so the next session/info goes without it and ApplyState measures it again.</summary>
        private void ForgetContextBreakdown()
        {
            lock (_contextLock) { _contextBreakdown = null; _contextBreakdownSession = null; }
        }

        /// <summary>
        /// Remembers whether the engine's raw catalog (<paramref name="rows"/>) lists <c>context</c> with <c>source: "builtin"</c>. Only the
        /// builtin is a local command whose output the host can read; a prompt template, skill or extension command named "context" would
        /// start a model turn, whose end would ask for the next measurement — a loop of billed turns — so it never counts.
        /// </summary>
        private void NoteContextCommand(JArray? rows)
        {
            var builtin = rows != null && rows.OfType<JObject>().Any(row =>
            {
                var name = row.GetString("name");
                if (name != null && name.StartsWith("/", StringComparison.Ordinal)) name = name.Substring(1);
                return string.Equals(name, ContextCommandName, StringComparison.Ordinal) && string.Equals(row.GetString("source"), "builtin", StringComparison.Ordinal);
            });
            lock (_stateLock) _contextBuiltin = builtin;
        }

        /// <summary>True while the engine's command catalog lists the builtin <c>context</c> command (see <see cref="NoteContextCommand"/>).</summary>
        private bool EngineOffersContextCommand()
        {
            lock (_stateLock) return _contextBuiltin;
        }

        /// <summary>True once <see cref="MaxContextFailures"/> measurements of <paramref name="sessionId"/> failed: no further attempt for that conversation.</summary>
        private bool ContextGaveUp(string? sessionId)
        {
            lock (_contextLock) return _contextFailures >= MaxContextFailures && !string.IsNullOrEmpty(sessionId) && string.Equals(sessionId, _contextFailureSession, StringComparison.Ordinal);
        }

        private void NoteContextFailure(string sessionId)
        {
            lock (_contextLock)
            {
                if (!string.Equals(sessionId, _contextFailureSession, StringComparison.Ordinal)) { _contextFailureSession = sessionId; _contextFailures = 0; }
                _contextFailures++;
            }
        }

        /// <summary>The command catalog changed: a conversation that could not be measured before (catalog not read yet) is measured now.</summary>
        private void ContextCommandCatalogChanged()
        {
            string? sessionId; bool streaming;
            lock (_stateLock) { sessionId = _info.SessionId; streaming = _info.IsStreaming; }
            if (!string.IsNullOrEmpty(sessionId) && !streaming && !ContextMeasured(sessionId)) ScheduleContextRefresh();
        }

        /// <summary>
        /// Queues one /context run behind the chat commands (at most one waiting): after a terminal agent_end and whenever session/info
        /// names a conversation the breakdown was not measured for yet. Nothing is queued unless the engine offers the command.
        /// </summary>
        private void ScheduleContextRefresh()
        {
            if (!EngineOffersContextCommand()) return;
            string? sessionId;
            lock (_stateLock) sessionId = _info.SessionId;
            if (ContextGaveUp(sessionId)) return;
            lock (_contextLock)
            {
                // One waiting at most, and nothing while a measurement is still waiting for its output (an agent_end seen meanwhile is
                // not a reason for another one).
                if (_contextRefreshQueued || _contextAnswer != null) return;
                _contextRefreshQueued = true;
            }
            lock (_stateLock) _commands.Enqueue(RefreshContextBreakdownAsync);
        }

        private async Task RefreshContextBreakdownAsync()
        {
            lock (_contextLock) _contextRefreshQueued = false;
            string? sessionId; bool busy; OmpConnection? connection;
            lock (_stateLock) { sessionId = _info.SessionId; busy = _info.IsStreaming || _info.HasPendingAsyncWork; connection = _epochConnection; }
            if (_bridge.State != BridgeState.Running || busy || string.IsNullOrEmpty(sessionId) || _ui.PendingRequestCount > 0 || !EngineOffersContextCommand() || ContextGaveUp(sessionId)) return;
            // Whatever happens below, this conversation counts as measured: a refusal must not queue the command again and again.
            lock (_contextLock) _contextBreakdownSession = sessionId;

            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_contextLock) _contextAnswer = answer;
            string? text = null;
            try
            {
                var response = await _bridge.SendCommandAsync(OmpCommands.Prompt(ContextCommand), TimeSpan.FromSeconds(10), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) { NoteContextFailure(sessionId!); _logger.Debug("The engine refused /context: " + (response.Error ?? "no reason")); return; }
                var done = await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromSeconds(5), _disposeCts.Token)).ConfigureAwait(false);
                if (done == answer.Task) text = answer.Task.Result;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.Debug("Reading the context breakdown failed: " + ex.Message); return; }
            finally
            {
                lock (_contextLock) if (_contextAnswer == answer) _contextAnswer = null;
            }

            var breakdown = ParseContextOutput(text, DateTime.UtcNow);
            if (breakdown == null) { NoteContextFailure(sessionId!); _logger.Debug("The engine's /context output was not recognized."); return; }
            List<ToolSelectionPayload> tools;
            long usageTokens;
            lock (_stateLock) { tools = _toolsView.Tools.ToList(); usageTokens = _info.ContextUsage?.Tokens ?? 0; }
            EstimateToolShares(breakdown, _registry.GetDefinitions(), tools);
            EstimateMessages(breakdown, usageTokens);

            SessionInfoPayload info;
            lock (_stateLock)
            {
                if (!ReferenceEquals(connection, _epochConnection) || !string.Equals(_info.SessionId, sessionId, StringComparison.Ordinal)) return;
                lock (_contextLock) { _contextBreakdown = breakdown; _contextBreakdownSession = sessionId; _contextFailures = 0; _contextFailureSession = null; }
                info = CloneInfo(_info);
                if (info.ContextUsage == null) info.ContextUsage = new ContextUsagePayload { Tokens = breakdown.SystemPrompt + breakdown.SystemTools + breakdown.SystemContext + breakdown.Skills + breakdown.Messages, ContextWindow = breakdown.ContextWindow };
                info.ContextUsage = new ContextUsagePayload { Tokens = info.ContextUsage.Tokens, ContextWindow = info.ContextUsage.ContextWindow, Percent = info.ContextUsage.Percent, Breakdown = breakdown };
                if (info.ContextUsage.Percent <= 0 && info.ContextUsage.ContextWindow > 0) info.ContextUsage.Percent = info.ContextUsage.Tokens * 100.0 / info.ContextUsage.ContextWindow;
                _info = info; _infoJson = PompJson.Serialize(info);
            }
            _ui.Post(UiMessageTypes.SessionInfo, info, UiDelivery.State);
        }

        /// <summary>
        /// True when <paramref name="frame"/> is the output of the /context a refresh is waiting for (then it is not a chat notice). The
        /// command queue is serial and a builtin answers before its prompt response, so the first command output in that window is its
        /// answer — also when the text is not the layout the host can read (that counts as a failed measurement, never as a notice).
        /// </summary>
        private bool ObserveContextOutput(OmpFrame frame)
        {
            var text = frame.Json.GetString("text");
            if (text == null) return false;
            lock (_contextLock)
            {
                if (_contextAnswer == null) return false;
                _contextAnswer.TrySetResult(text);
                return true;
            }
        }

        /// <summary>What the editor context of a message carries (the options' current values, or the defaults for a host without them).</summary>
        private EditorContextLimits CurrentEditorContextLimits() => (_settings as IEditorContextSettings)?.EditorContextLimits ?? EditorContextLimits.Default;

        /// <summary>
        /// workspace/countBasketTokens: what the next message adds beyond its text — the editor context (when on, gathered exactly as a
        /// send would) and the attachment list — as a characters-based estimate (<see cref="EstimateTokens"/>).
        /// </summary>
        private async Task HandleCountBasketTokensAsync(JObject args)
        {
            var requestId = args.GetString("requestId");
            if (string.IsNullOrEmpty(requestId) || requestId!.Length > 128) return;
            var reply = new BasketTokensPayload { RequestId = requestId };
            try
            {
                string root;
                lock (_stateLock)
                {
                    root = _workingDirectory;
                    var session = args.GetString("sessionId");
                    if (!string.IsNullOrEmpty(session) && !string.Equals(session, _info.SessionId, StringComparison.Ordinal)) throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
                }
                if (!string.Equals(args.GetString("root") ?? string.Empty, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
                var paths = (args["attachments"] as JArray ?? new JArray()).OfType<JObject>().Select(item => item.GetString("path") ?? string.Empty)
                    .Where(path => path.Length > 0 && path.Length <= 4096 && !path.Any(char.IsControl)).ToList();
                if (paths.Count > 0)
                    reply.Attachments = EstimateTokens(PromptDisplay.AttachmentsHeader(CanReadBuffer()) + "\n" + string.Join("\n", paths.Select(path => "- " + path)) + "\n\n");
                if (args.GetBool("includeEditorContext") == true && _bridge.State == BridgeState.Running)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                    // The builder may wait the editor-context timeout twice (the parallel queries, then the buffer read).
                    var wait = CurrentEditorContextLimits().Timeout;
                    timeout.CancelAfter(wait + wait + TimeSpan.FromSeconds(1));
                    var contextTask = _contextBuilder.BuildAsync(timeout.Token);
                    contextTask.Observe();
                    var context = await contextTask.WithCancellation(timeout.Token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(context?.Text)) reply.EditorContext = EstimateTokens(context!.Text + "\n\n");
                }
                reply.Tokens = reply.Attachments + reply.EditorContext;
                reply.Ok = true;
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { return; }
            // Only the host's own refusals are worded; anything else (an adapter or editor failure) gets a fixed text, never its message.
            catch (Exception ex) { reply.Ok = false; reply.Error = ex is InvalidOperationException ? ex.Message : PompLanguage.Pick("바구니 크기를 셀 수 없어요.", "The basket could not be measured."); }
            _ui.Post(UiMessageTypes.WorkspaceBasketTokens, reply, UiDelivery.State);
        }
    }
}
