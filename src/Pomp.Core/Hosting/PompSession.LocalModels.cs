using System;
using System.Collections.Concurrent;
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
    // Saved model servers (local ones and other PCs' shares) list their models when the engine starts. Before a model list is read the
    // host asks the POMP extension to read the servers again (extensions/local-models.ts, /pomp-local-models): a model another PC no
    // longer shares (it signed out of that provider) goes, a new one comes, without a restart.
    public sealed partial class PompSession
    {
        private const string LocalModelsCommand = "pomp-local-models";
        private const string LocalModelsResultPrefix = "pomp-local-models-result:";
        private static readonly TimeSpan LocalModelsTimeout = TimeSpan.FromSeconds(10);
        private readonly ConcurrentDictionary<string, LocalModelsTicket> _localModelTickets = new ConcurrentDictionary<string, LocalModelsTicket>(StringComparer.Ordinal);

        /// <summary>
        /// Has the engine read the saved model servers again (nothing to do without servers, while it answers or without the extension).
        /// A server that does not answer keeps its list; a failure only means the list stays as it was, so it is logged, not raised.
        /// </summary>
        private async Task RefreshLocalModelsAsync(CancellationToken token)
        {
            if (_providerCredentials.LocalServersLaunchValue() == null || !_settings.LoadOmpExtensions || _bridge.State != BridgeState.Running) return;
            OmpConnection? connection;
            lock (_stateLock)
            {
                // An engine command typed during a reply waits for it; the list is read as it is instead.
                if (_info.IsStreaming || _sessionSwitchUncertain) return;
                connection = _epochConnection;
            }
            if (connection == null) return;
            var id = Guid.NewGuid().ToString("N");
            var ticket = new LocalModelsTicket(connection);
            _localModelTickets[id] = ticket;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(LocalModelsTimeout);
                    await RequireEngineCommandsAsync(connection, timeout.Token, LocalModelsCommand).ConfigureAwait(false);
                    var response = await connection.SendCommandAsync(OmpCommands.Prompt("/" + LocalModelsCommand + " " + id), LocalModelsTimeout, timeout.Token).ConfigureAwait(false);
                    if (!response.Success) { _logger.Warn("The engine did not read the saved model servers again."); return; }
                    var counts = await ticket.Completion.Task.WithCancellation(timeout.Token).ConfigureAwait(false);
                    _logger.Debug("Saved model servers read again: " + counts);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { _logger.Warn("The saved model servers did not answer in time; their model lists stay as they were."); }
            catch (InvalidOperationException ex) { _logger.Debug("The saved model servers were not read again: " + ex.Message); }
            catch (Exception ex) when (ex is OmpBridgeException || ex is TimeoutException) { _logger.Debug("The saved model servers were not read again: " + ex.Message); }
            finally { _localModelTickets.TryRemove(id, out _); }
        }

        /// <summary>The extension's answer to <see cref="RefreshLocalModelsAsync"/>; never shown in the chat.</summary>
        private bool HandleLocalModelsNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(LocalModelsResultPrefix, StringComparison.Ordinal)) return false;
            if (message.Length > 4096) return true;
            try
            {
                var payload = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(LocalModelsResultPrefix.Length))));
                var id = (string?)payload["id"];
                if (id != null && _localModelTickets.TryGetValue(id, out var ticket) && ReferenceEquals(ticket.Origin, origin))
                    ticket.Completion.TrySetResult((int?)payload["servers"] + " server(s), " + (int?)payload["models"] + " model(s)");
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException) { _logger.Debug("Ignored a malformed model server refresh result."); }
            return true;
        }

        private sealed class LocalModelsTicket
        {
            public LocalModelsTicket(OmpConnection origin) { Origin = origin; }
            public OmpConnection Origin { get; }
            public TaskCompletionSource<string> Completion { get; } = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
