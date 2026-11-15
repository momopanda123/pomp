using System;
using System.IO;
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
    // Voice input for the composer (docs/architecture.md "음성 입력"). The microphone button runs omp's draft-only dictation over RPC:
    // dictation_start opens the PC's default microphone inside the engine and transcribes on this PC; omp reports the capture's phase,
    // its whole preview after every change and one terminal frame. Nothing reaches a model and nothing is sent: the page puts the text
    // into the draft. One capture per engine. It ends when the page leaves this conversation's tab, or when the engine stops.
    public sealed partial class PompSession
    {
        private const string DictationIdle = "idle";
        private const int MaxDictationCharacters = 24000;

        /// <summary>omp flushes the speech it still holds before it answers a stop: bounded, so a stuck model cannot hold the button.</summary>
        private static readonly TimeSpan DictationStopTimeout = TimeSpan.FromSeconds(120);

        private readonly object _dictationLock = new object();
        private string _dictationPhase = DictationIdle; // idle | starting | recording | transcribing
        private string? _dictationCapture;              // the running capture, or the last one that ended (the page tells them apart by it)
        private string _dictationText = string.Empty;
        private string? _dictationError;
        private OmpConnection? _dictationOrigin;
        private bool _dictationStopping;
        private string? _dictationOverlay;              // model and language of the overlay last written

        private DictationOptions DictationSettings => (_settings as IDictationSettings)?.Dictation ?? DictationOptions.Default;

        private UiMessage DictationStateMessage()
        {
            lock (_dictationLock)
            {
                return new UiMessage(UiMessageTypes.DictationState, new JObject
                {
                    ["state"] = _dictationPhase,
                    ["captureId"] = _dictationCapture,
                    ["text"] = _dictationText,
                    ["error"] = _dictationError,
                });
            }
        }

        private void PublishDictationState()
        {
            var message = DictationStateMessage();
            _ui.Post(message.Type, message.Payload, UiDelivery.State);
        }

        /// <summary>The options the overlay is written with, remembered so a later change of them can be told from any other setting.</summary>
        private DictationOptions DictationOptionsForOverlay()
        {
            var options = DictationSettings;
            lock (_dictationLock) _dictationOverlay = Dictation.EngineModel(options.Model) + "\n" + Dictation.EngineLanguage(options);
            return options;
        }

        /// <summary>The model, the language or POMP's display language changed: omp re-reads the overlay, so the next capture uses them.</summary>
        private void DictationSettingsChanged()
        {
            var options = DictationSettings;
            var current = Dictation.EngineModel(options.Model) + "\n" + Dictation.EngineLanguage(options);
            lock (_dictationLock) if (_dictationOverlay == null || _dictationOverlay == current) return;
            if (_bridge.State != BridgeState.Running) return;
            try { RewriteLiveOverlay(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _logger.Warn("Could not update the voice input settings of the running engine: " + ex.Message); }
        }

        /// <summary>dictation/set from the page: answers with one dictation/reply; the capture itself is told by dictation/state.</summary>
        private async Task HandleDictationSetAsync(JObject payload)
        {
            var requestId = payload.GetString("requestId") ?? string.Empty;
            string? error = null;
            try
            {
                if (payload.GetBool("on") == true) error = StartDictation(requestId);
                else await StopDictationAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is OmpBridgeException || ex is InvalidOperationException || ex is TimeoutException || ex is IOException) { error = ex.Message; }
            _ui.Post(UiMessageTypes.DictationReply, new JObject { ["requestId"] = requestId, ["ok"] = error == null, ["error"] = error }, UiDelivery.State);
        }

        /// <summary>
        /// Starts a capture; null once omp was asked, else why not. omp answers only when the microphone records, which takes minutes the
        /// first time (it downloads its speech runtime and the model), so that answer is awaited in the background.
        /// </summary>
        private string? StartDictation(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                return PompLanguage.Pick("음성 입력 요청이 올바르지 않아요.", "The voice input request is invalid.");
            OmpConnection? origin;
            lock (_stateLock) origin = _epochConnection;
            if (origin == null || _bridge.State != BridgeState.Running) return PompLanguage.Pick("연결되어 있지 않아요.", "Not connected.");
            lock (_dictationLock)
            {
                if (_dictationPhase != DictationIdle) return PompLanguage.Pick("이미 음성 입력 중이에요.", "Voice input is already running.");
                _dictationPhase = "starting";
                _dictationCapture = id;
                _dictationText = string.Empty;
                _dictationError = null;
                _dictationOrigin = origin;
                _dictationStopping = false;
            }

            PublishDictationState();
            AwaitDictationStartAsync(origin, id).Forget(_logger, "voice input start");
            return null;
        }

        private async Task AwaitDictationStartAsync(OmpConnection origin, string id)
        {
            string? error;
            try
            {
                var response = await origin.SendCommandAsync(OmpCommands.DictationStart(id), Timeout.InfiniteTimeSpan, _disposeCts.Token).ConfigureAwait(false);
                if (response.Success) return;
                error = response.Error ?? string.Empty;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is OmpBridgeException || ex is InvalidOperationException || ex is IOException) { error = ex.Message; }
            // omp ends a start it refused with dictation_end as well; an engine without the command only answers.
            lock (_dictationLock)
                if (_dictationCapture == id && ReferenceEquals(origin, _dictationOrigin) && _dictationStopping) return;
            FailDictation(id, origin, error);
        }

        /// <summary>Stops the running capture and waits for omp to flush it (the final text is omp's dictation_end); nothing without one.</summary>
        private async Task StopDictationAsync()
        {
            OmpConnection? origin; string? id;
            lock (_dictationLock)
            {
                if (_dictationPhase == DictationIdle || _dictationStopping) return;
                origin = _dictationOrigin; id = _dictationCapture;
                _dictationStopping = true;
            }

            if (origin == null || id == null) return;
            ResponseFrame response;
            try { response = await origin.SendCommandAsync(OmpCommands.DictationStop(), DictationStopTimeout, _disposeCts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OmpBridgeException || ex is InvalidOperationException || ex is TimeoutException || ex is IOException)
            {
                FailDictation(id, origin, ex.Message);
                return;
            }

            // Normally omp's dictation_end came first and these find nothing left to end.
            if (response.Success) EndDictation(id, origin, (response.Data as JObject)?.GetString("text"), null);
            else FailDictation(id, origin, response.Error ?? string.Empty);
        }

        private void FailDictation(string id, OmpConnection? origin, string engineError)
        {
            if (EndDictation(id, origin, null, Dictation.FailureMessage(engineError))) _logger.Warn("Voice input failed: " + engineError);
        }

        /// <summary>The capture's one end: its final text (null keeps the last preview) and what failed, then idle. False when it had ended.</summary>
        private bool EndDictation(string id, OmpConnection? origin, string? text, string? error)
        {
            lock (_dictationLock)
            {
                if (_dictationPhase == DictationIdle || _dictationCapture != id || (origin != null && !ReferenceEquals(origin, _dictationOrigin))) return false;
                _dictationPhase = DictationIdle;
                if (text != null) _dictationText = BoundDictation(text);
                _dictationError = error;
                _dictationOrigin = null;
            }

            PublishDictationState();
            return true;
        }

        /// <summary>omp's dictation frames of the running capture; those of an earlier capture or connection are dropped.</summary>
        private void HandleDictationFrame(OmpFrame frame)
        {
            var id = frame.Json.GetString("captureId");
            if (id == null) return;
            var origin = _bridge.OriginOf(frame);
            if (frame.Type == OmpFrameTypes.DictationEnd)
            {
                var error = frame.Json.GetString("error");
                var text = frame.Json.GetString("text") ?? string.Empty;
                if (error == null) EndDictation(id, origin, text, null);
                else if (EndDictation(id, origin, text, Dictation.FailureMessage(error))) _logger.Warn("Voice input failed: " + error);
                return;
            }

            lock (_dictationLock)
            {
                if (_dictationPhase == DictationIdle || _dictationCapture != id || !ReferenceEquals(origin, _dictationOrigin)) return;
                if (frame.Type == OmpFrameTypes.DictationState)
                {
                    var state = frame.Json.GetString("state");
                    if (state != "starting" && state != "recording" && state != "transcribing") return;
                    _dictationPhase = state!;
                }
                else
                {
                    _dictationText = BoundDictation(frame.Json.GetString("text") ?? string.Empty);
                }
            }

            PublishDictationState();
        }

        /// <summary>The page left this conversation (another tab, the window closed): the microphone must not stay open unseen.</summary>
        private void StopUnattendedDictation()
        {
            lock (_dictationLock) if (_dictationPhase == DictationIdle) return;
            StopDictationAsync().Forget(_logger, "voice input stop");
        }

        /// <summary>omp stopped or restarts: its capture went with it.</summary>
        private void DictationBridgeStateChanged(BridgeState state)
        {
            if (state == BridgeState.Running) return;
            string? id;
            lock (_dictationLock) id = _dictationPhase == DictationIdle ? null : _dictationCapture;
            if (id != null) EndDictation(id, null, null, PompLanguage.Pick("연결이 끊겨 음성 입력을 끝냈어요.", "Voice input ended because the connection was lost."));
        }

        private static string BoundDictation(string text) => text.Length <= MaxDictationCharacters ? text : text.Substring(0, MaxDictationCharacters);
    }
}
