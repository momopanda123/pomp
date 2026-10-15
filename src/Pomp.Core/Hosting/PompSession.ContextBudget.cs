using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Features;
using Pomp.Internal;
namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private async Task<ContextBudget?> ReadContextBudgetAsync(CancellationToken token)
        {
            string session; OmpConnection? connection;
            lock (_stateLock) { if (_sessionSwitchUncertain || _bridge.State != BridgeState.Running) return null; session = _info.SessionId ?? ""; connection = _epochConnection; }
            if (session.Length == 0) return null;
            try
            {
                var response = await _bridge.SendCommandAsync(new JObject { ["type"] = "get_state" }, TimeSpan.FromSeconds(3), token).WithCancellation(token).ConfigureAwait(false);
                lock (_stateLock) if (_sessionSwitchUncertain || _info.SessionId != session || !ReferenceEquals(connection, _epochConnection)) return null;
                return response.Success && response.Data is JObject state ? ContextBudget.FromState(state, session) : null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return null; } // disclose unavailable estimate in the context result; keep bounded source access usable
        }
    }
}
