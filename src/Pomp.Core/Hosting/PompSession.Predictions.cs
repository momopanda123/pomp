using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Features;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private readonly object _composerPredictionLock = new object();
        private CancellationTokenSource? _composerPrediction;

        private void CancelComposerPrediction()
        {
            lock (_composerPredictionLock) _composerPrediction?.Cancel();
        }

        private async Task PredictComposerAsync(JObject payload)
        {
            var requestId = payload["requestId"]?.Type == JTokenType.String ? (string?)payload["requestId"] : null;
            if (string.IsNullOrEmpty(requestId) || requestId!.Length > 128) return;
            var result = new ComposerPredictionPayload { RequestId = requestId };
            CancellationTokenSource? request = null;
            CancellationTokenSource? deadline = null;
            string? session = null;
            OmpConnection? origin = null;
            try
            {
                var text = payload["text"]?.Type == JTokenType.String ? (string?)payload["text"] : null;
                session = payload["sessionId"]?.Type == JTokenType.String ? (string?)payload["sessionId"] : null;
                if (text == null || text.Length > 4000 || text.IndexOf('\0') >= 0 || string.IsNullOrEmpty(session) || session!.Length > 128)
                    result.Error = "invalid-request";
                else if (!_settings.ComposerPredictions) result.Error = "disabled";
                else
                {
                    lock (_stateLock)
                    {
                        origin = _epochConnection;
                        if (_bridge.State != BridgeState.Running || origin == null || string.IsNullOrEmpty(_info.SessionId)) result.Error = "disconnected";
                        else if (_sessionSwitchUncertain || session != _info.SessionId) result.Error = "conversation-changed";
                    }
                    if (result.Error == null)
                    {
                        lock (_composerPredictionLock)
                        {
                            _composerPrediction?.Cancel();
                            request = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                            _composerPrediction = request;
                        }
                        deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(6));
                        var model = EditorModelSetting.EngineSpec((_settings as IInlineModelSettings)?.InlineModel);
                        var inference = SendInlineAsync("prompt", new InlineRequest { Selection = text! }, model,
                            TimeSpan.FromSeconds(6), waitForSlot: true, deadline.Token, expectedSession: session);
                        // Cancel the stale UI wait, not the chat agent; the transport keeps its slot until the extension returns.
                        var cancelled = Task.Delay(Timeout.Infinite, deadline.Token);
                        if (await Task.WhenAny(inference, cancelled).ConfigureAwait(false) != inference)
                        {
                            _ = inference.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            throw new OperationCanceledException(deadline.Token);
                        }
                        var reply = await inference.ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        result.Text = PredictionText(reply?.Text ?? string.Empty);
                    }
                }
            }
            catch (Exception error)
            {
                result.Error = error is InlineErrorException inline ? inline.Code
                    : error is InlineBusyException ? "busy"
                    : error is OperationCanceledException ? request?.IsCancellationRequested == true ? "cancelled" : "timed-out"
                    : error is TimeoutException ? "timed-out"
                    : "request-failed";
            }
            finally
            {
                if (request != null)
                {
                    lock (_composerPredictionLock)
                    {
                        if (ReferenceEquals(_composerPrediction, request)) _composerPrediction = null;
                        request.Dispose();
                    }
                    deadline?.Cancel();
                    deadline?.Dispose();
                }
            }
            if (!_settings.ComposerPredictions) result.Error = "disabled";
            else if (origin != null)
            {
                lock (_stateLock)
                {
                    if (_sessionSwitchUncertain || session != _info.SessionId || !ReferenceEquals(origin, _epochConnection))
                        result.Error = "conversation-changed";
                }
            }
            if (result.Error != null) result.Text = null;
            if (Volatile.Read(ref _disposed) == 0) _ui.Post(UiMessageTypes.ComposerPrediction, result, UiDelivery.State);
        }

        private static string PredictionText(string text)
        {
            var end = text.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0) text = text.Substring(0, end);
            return Bound(text, 200);
        }
    }
}
