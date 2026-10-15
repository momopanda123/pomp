using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private const string AdvisorCommand = "pomp-advisor";
        private const string AdvisorResult = "pomp-advisor-result:";
        private readonly ConcurrentDictionary<string, AdvisorTicket> _advisorTickets = new ConcurrentDictionary<string, AdvisorTicket>(StringComparer.Ordinal);
        private bool _advisorAvailable;
        private sealed class AdvisorTicket
        {
            public AdvisorTicket(AdvisorOperationContext context) { Context = context; }
            public AdvisorOperationContext Context { get; }
            public TaskCompletionSource<JObject> Completion { get; } = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private AdvisorOperationContext? CaptureAdvisorContext()
        {
            lock (_stateLock)
            {
                var origin = _epochConnection; var session = _info.SessionId; var epoch = _epoch;
                if (!_advisorAvailable || origin == null || origin.IsClosed || _bridgeState.State != BridgeState.Running || epoch.IsCancellationRequested
                    || _sessionSwitchUncertain || _recoveryStatus != "ready" || Volatile.Read(ref _workspaceSwitching) != 0 || string.IsNullOrEmpty(session)) return null;
                return new AdvisorOperationContext
                {
                    Identity = origin, SessionId = session!, Token = epoch.Token,
                    IsCurrent = () => { lock (_stateLock) return !_epoch.IsCancellationRequested && ReferenceEquals(origin, _epochConnection) && ReferenceEquals(epoch, _epoch)
                        && _bridgeState.State == BridgeState.Running && _info.SessionId == session && !_sessionSwitchUncertain && _recoveryStatus == "ready" && Volatile.Read(ref _workspaceSwitching) == 0; },
                };
            }
        }
        private void ObserveAdvisorContext()
        {
            object? identity; string? session;
            lock (_stateLock) { identity = _advisorAvailable && _bridgeState.State == BridgeState.Running && !_epoch.IsCancellationRequested && !_sessionSwitchUncertain && Volatile.Read(ref _workspaceSwitching) == 0 ? _epochConnection : null; session = identity == null ? null : _info.SessionId; }
            _advisors.Observe(identity, session);
        }
        // The internal bridge command is detected from the raw list and never published to the slash picker.
        private void NoteAdvisorCommand(JArray? rows) => _advisorAvailable = rows?.OfType<JObject>().Any(row => (string?)row["name"] == AdvisorCommand || (string?)row["name"] == "/" + AdvisorCommand) == true;
        private async Task<JObject> SendAdvisorAsync(AdvisorOperationContext context, JObject command, CancellationToken token)
        {
            if (!context.IsCurrent() || context.Identity is not OmpConnection origin) throw new InvalidOperationException("stale-session");
            var request = Guid.NewGuid().ToString("N"); var ticket = new AdvisorTicket(context);
            command = (JObject)command.DeepClone();
            command["sessionId"] = context.SessionId;
            command["expires"] = context.Expires;
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.ToString(Formatting.None)));
            var prompt = OmpCommands.Prompt("/" + AdvisorCommand + " " + encoded + " --request " + request);
            if (Encoding.UTF8.GetByteCount(prompt.ToString(Formatting.None)) > MaxPromptFrameBytes) throw new ArgumentException("document-too-large");
            _advisorTickets[request] = ticket;
            try
            {
                var response = await origin.SendCommandAsync(prompt, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                // Arbitrary engine error text might contain part of the instructions argument; never relay or log it.
                if (!response.Success) throw new InvalidOperationException("advisor-failed");
                if (!await AsyncUtil.WaitForAsync(ticket.Completion.Task, TimeSpan.FromSeconds(15)).WithCancellation(token).ConfigureAwait(false)) throw new TimeoutException();
                token.ThrowIfCancellationRequested(); if (!context.IsCurrent()) throw new InvalidOperationException("stale-session");
                return await ticket.Completion.Task.ConfigureAwait(false);
            }
            finally { _advisorTickets.TryRemove(request, out _); }
        }
        private bool HandleAdvisorNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(AdvisorResult, StringComparison.Ordinal)) return false;
            // Malformed, expired and late management replies are consumed too; none can become a chat notice.
            if (message.Length > 900000 + AdvisorResult.Length) return true;
            try
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(AdvisorResult.Length)));
                using var reader = new JsonTextReader(new System.IO.StringReader(text)) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
                var result = JObject.Load(reader);
                var request = result["request"]?.Type == JTokenType.String ? (string?)result["request"] : null;
                if (request != null && _advisorTickets.TryGetValue(request, out var ticket) && ReferenceEquals(ticket.Context.Identity, origin)
                    && ticket.Context.IsCurrent()) ticket.Completion.TrySetResult(result);
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException) { /* No payload or parser text is logged. */ }
            return true;
        }
    }
}
