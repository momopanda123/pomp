using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        // Serialize publications without holding _stateLock while calling the UI hub (its snapshot takes that lock).
        private readonly object _commandCatalogLock = new object();
        private JArray _availableCommands = new JArray();
        private long _commandCatalogRevision;

        private static JArray NormalizeCommands(JToken? value, bool subcommands = false)
        {
            var commands = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            if (value is not JArray rows) return new JArray();
            foreach (var row in rows.OfType<JObject>())
            {
                var name = row.GetString("name");
                if (name == null) continue;
                if (name.StartsWith("/", StringComparison.Ordinal)) name = name.Substring(1);
                if (name == AdvisorCommand) continue;
                if (name.Length < 1 || name.Length > 64 || !IsCommandLetterOrDigit(name[0])
                    || name.Any(c => !IsCommandLetterOrDigit(c) && c != ':' && c != '.' && c != '_' && c != '-')) continue;
                if (commands.ContainsKey(name)) continue;
                var command = new JObject { ["name"] = name };
                // Where the engine got the command from (builtin, extension, skill, file, custom, mcp_prompt): the page groups skills by it.
                var source = row.GetString("source");
                if (source != null && source.Length <= 20 && source.All(c => (c >= 'a' && c <= 'z') || c == '_')) command["source"] = source;
                var description = CommandText(row.GetString("description"));
                if (description != null) command["description"] = description;
                if (!subcommands)
                {
                    var hint = CommandText(row.GetObject("input").GetString("hint"));
                    if (hint != null) command["hint"] = hint;
                    var children = NormalizeCommands(row["subcommands"], true);
                    if (children.Count > 0) command["subcommands"] = children;
                }
                commands.Add(name, command);
            }
            return new JArray(commands.Values);
        }

        private static bool IsCommandLetterOrDigit(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        private static string? CommandText(string? value)
        {
            if (value == null) return null;
            var text = new string(value.Where(c => !char.IsControl(c)).Take(300).ToArray());
            return text.Length == 0 ? null : text;
        }

        private JObject CommandsSnapshot()
        {
            lock (_stateLock) return _bridgeState.State == BridgeState.Running ? CommandsPayload(_availableCommands) : new JObject { ["commands"] = new JArray() };
        }

        /// <summary>
        /// The page's command list. No count limit: every command while the message fits the UI hub (StateBudget); past it the descriptions
        /// go first, then the last commands, which are counted (<c>hidden</c>), never dropped silently.
        /// </summary>
        private static JObject CommandsPayload(JArray commands)
        {
            var rows = commands.OfType<JObject>().Select(row => (JObject)row.DeepClone()).ToList();
            if (StateBudget.Bytes(new UiMessage(UiMessageTypes.CommandsState, new JObject { ["commands"] = new JArray(rows) })) > StateBudget.Limit)
                foreach (var row in rows)
                {
                    row.Remove("description"); row.Remove("hint");
                    foreach (var sub in (row["subcommands"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) sub.Remove("description");
                }
            var left = StateBudget.Fit(rows, 2048);
            var payload = new JObject { ["commands"] = new JArray(rows) };
            if (left > 0) payload["hidden"] = left;
            return payload;
        }

        private void ObserveCommands(OmpFrame frame)
        {
            var origin = _bridge.OriginOf(frame);
            var normalized = NormalizeCommands(frame.Json["commands"]);
            lock (_commandCatalogLock)
            {
                bool publish;
                lock (_stateLock)
                {
                    if (origin == null || origin.IsClosed || !ReferenceEquals(origin, _epochConnection) || _epoch.IsCancellationRequested) return;
                    // An event is newer than any get_available_commands request already in flight, even if its contents match.
                    _commandCatalogRevision++;
                    ObserveSkillCommands(frame.Json["commands"] as JArray ?? new JArray());
                    NoteContextCommand(frame.Json["commands"] as JArray);
                    NoteAdvisorCommand(frame.Json["commands"] as JArray);
                    Task.Run(() => { ObserveAdvisorContext(); _advisors.Refresh(); }).Forget(_logger, "advisor availability");
                    if (JToken.DeepEquals(_availableCommands, normalized)) return;
                    _availableCommands = normalized;
                    publish = _bridgeState.State == BridgeState.Running;
                }
                if (publish) _ui.Post(UiMessageTypes.CommandsState, CommandsPayload(normalized), UiDelivery.State);
                PublishMemberStopAvailability(normalized);
                ContextCommandCatalogChanged();
            }
        }

        private void ClearCommands()
        {
            lock (_commandCatalogLock)
            {
                lock (_stateLock) { _availableCommands = new JArray(); _commandCatalogRevision++; _contextBuiltin = false; _advisorAvailable = false; }
                ObserveAdvisorContext();
                _ui.Post(UiMessageTypes.CommandsState, new JObject { ["commands"] = new JArray() }, UiDelivery.State);
                PublishMemberStopAvailability(new JArray());
            }
        }

        private async Task ReadCommandsAsync(OmpConnection connection, CancellationToken token, long revision)
        {
            try
            {
                var response = await connection.SendCommandAsync(new JObject { ["type"] = "get_available_commands" }, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                if (!response.Success || response.Data?["commands"] is not JArray rows)
                {
                    _logger.Warn("Could not read omp commands: " + (response.Error ?? "Malformed command list."));
                    return;
                }
                var normalized = NormalizeCommands(rows);
                lock (_commandCatalogLock)
                {
                    lock (_stateLock)
                    {
                        if (token.IsCancellationRequested || connection.IsClosed || !ReferenceEquals(connection, _epochConnection)
                            || _bridgeState.State != BridgeState.Running || revision != _commandCatalogRevision) return;
                        ObserveSkillCommands(rows);
                        NoteContextCommand(rows);
                        NoteAdvisorCommand(rows);
                        Task.Run(() => { ObserveAdvisorContext(); _advisors.Refresh(); }).Forget(_logger, "advisor availability");
                        if (JToken.DeepEquals(_availableCommands, normalized)) return;
                        _availableCommands = normalized;
                        _commandCatalogRevision++;
                    }
                    _ui.Post(UiMessageTypes.CommandsState, CommandsPayload(normalized), UiDelivery.State);
                    PublishMemberStopAvailability(normalized);
                    ContextCommandCatalogChanged();
                }
            }
            catch (Exception ex) { _logger.Debug("Could not read omp commands: " + ex.Message); }
        }

        private void PublishRunningCommands()
        {
            OmpConnection? connection; CancellationToken token; long revision;
            lock (_commandCatalogLock)
            {
                lock (_stateLock) { connection = _epochConnection; token = _epoch.Token; revision = _commandCatalogRevision; }
                _ui.Post(UiMessageTypes.CommandsState, CommandsSnapshot(), UiDelivery.State);
            }
            if (connection != null) ReadCommandsAsync(connection, token, revision).Forget(_logger, "omp commands");
        }
    }
}
