using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Protocol;

namespace Pomp.Hosting
{
    // Stopping one team member (docs/architecture.md §8.12). The official engine has no RPC for it; POMP's extension command
    // /pomp-stop does what the engine's collaboration "kill" does. Offered only while the engine lists that command, and the
    // extension checks again that the member is running and was started by this conversation.
    public sealed partial class PompSession
    {
        private const string MemberStopCommand = "pomp-stop";
        private const string MemberStopResultPrefix = "pomp-stop-result:";
        private static readonly TimeSpan MemberStopWait = TimeSpan.FromSeconds(10);

        private sealed class MemberStopTicket
        {
            public MemberStopTicket(OmpConnection origin) { Origin = origin; }
            public OmpConnection Origin { get; }
            public TaskCompletionSource<JObject> Completion { get; } = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentDictionary<string, MemberStopTicket> _memberStops = new ConcurrentDictionary<string, MemberStopTicket>(StringComparer.Ordinal);

        /// <summary>Called with the engine's normalized command list, outside the state lock.</summary>
        private void PublishMemberStopAvailability(JArray commands) =>
            _backgroundAgents.SetStopAvailable(commands.OfType<JObject>().Any(c => (string?)c["name"] == MemberStopCommand));

        /// <summary>Stops one member; throws with the engine's reason when it did not stop. The member id was checked against the shown jobs.</summary>
        private async Task StopMemberAsync(string id, CancellationToken token)
        {
            OmpConnection? origin;
            lock (_stateLock) origin = _bridgeState.State == BridgeState.Running ? _epochConnection : null;
            if (origin == null) throw new InvalidOperationException(PompLanguage.Pick("연결되어 있지 않아요.", "Not connected."));
            var request = Guid.NewGuid().ToString("N");
            var ticket = new MemberStopTicket(origin);
            _memberStops[request] = ticket;
            try
            {
                // An extension command runs at once, also while the main turn streams; it starts no turn of its own.
                var response = await origin.SendCommandAsync(OmpCommands.Prompt("/" + MemberStopCommand + " " + id + " --request " + request), MemberStopWait, token).ConfigureAwait(false);
                if (!response.Success) throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("중지 요청이 거부됐어요.", "The stop request was refused."));
                if (!await AsyncUtil.WaitForAsync(ticket.Completion.Task, MemberStopWait).WithCancellation(token).ConfigureAwait(false))
                    throw new TimeoutException(PompLanguage.Pick("중지 결과가 오지 않았어요.", "The stop result did not arrive."));
                var result = await ticket.Completion.Task.ConfigureAwait(false);
                if ((bool?)result["stopped"] != true)
                    throw new InvalidOperationException(TextUtil.Truncate((string?)result["error"] ?? PompLanguage.Pick("구성원을 멈추지 못했어요.", "The member was not stopped."), 500));
            }
            finally { _memberStops.TryRemove(request, out _); }
        }

        private bool HandleMemberStopNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(MemberStopResultPrefix, StringComparison.Ordinal)) return false;
            // Consume malformed and late results too: they never belong in the transcript.
            if (message.Length > 20000) return true;
            try
            {
                var result = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(MemberStopResultPrefix.Length))));
                var request = result["request"]?.Type == JTokenType.String ? (string?)result["request"] : null;
                if (request != null && _memberStops.TryGetValue(request, out var ticket) && ReferenceEquals(ticket.Origin, origin)) ticket.Completion.TrySetResult(result);
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException)
            { _logger.Debug("Ignored a malformed member stop result."); }
            return true;
        }
    }
}
