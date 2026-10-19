using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Json;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // Side questions (/btw, docs/architecture.md "Side questions"): omp answers them over the conversation's context beside the running
    // work, without tools and outside the transcript. The host only relays; the page keeps the topic it shows.
    public sealed partial class PompSession
    {
        private const int MaxBtwQuestionChars = 20000;

        private async Task HandleBtwAskAsync(JObject args)
        {
            var requestId = (string?)args["requestId"] ?? string.Empty;
            try
            {
                var ask = PompJson.ToObject<BtwAskPayload>(args) ?? throw new ArgumentException(PompLanguage.Pick("질문 형식이 올바르지 않아요.", "The question is invalid."));
                var question = (ask.Question ?? string.Empty).Trim();
                if (question.Length == 0) throw new ArgumentException(PompLanguage.Pick("질문이 비어 있어요.", "The question is empty."));
                if (question.Length > MaxBtwQuestionChars) throw new ArgumentException(PompLanguage.Pick("질문이 너무 길어요.", "The question is too long."));
                var connection = BtwConnection();
                var response = await connection.SendCommandAsync(OmpCommands.Btw(question, string.IsNullOrWhiteSpace(ask.RecordId) ? null : ask.RecordId), _options.PromptTimeout, _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("질문을 거부했어요.", "The question was rejected."));
                _ui.Post(UiMessageTypes.BtwReply, new BtwReplyPayload { RequestId = requestId, Ok = true, Record = response.Data?["record"] as JObject }, UiDelivery.State);
            }
            catch (Exception ex) when (!_disposeCts.IsCancellationRequested)
            {
                _ui.Post(UiMessageTypes.BtwReply, new BtwReplyPayload { RequestId = requestId, Ok = false, Error = ex is TimeoutException ? PompLanguage.Pick("제한 시간 안에 답이 없었어요.", "No answer in time.") : ex.Message }, UiDelivery.State);
            }
        }

        /// <summary>Closing the panel: cancels the topic's running answer. A side question never affects the conversation's run.</summary>
        private async Task HandleBtwCancelAsync(JObject args)
        {
            var recordId = PompJson.ToObject<BtwCancelPayload>(args)?.RecordId;
            try
            {
                var response = await BtwConnection().SendCommandAsync(OmpCommands.BtwCancel(string.IsNullOrWhiteSpace(recordId) ? null : recordId), TimeSpan.FromSeconds(15), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) _logger.Warn("The side question could not be cancelled: " + response.Error);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("The side question could not be cancelled: " + ex.Message); }
        }

        private OmpConnection BtwConnection()
        {
            lock (_stateLock)
            {
                if (_epochConnection == null || _epochConnection.IsClosed || _bridge.State != BridgeState.Running)
                    throw new OmpBridgeException(PompLanguage.Pick("연결되어 있지 않아요.", "Not connected."));
                return _epochConnection;
            }
        }

        /// <summary>btw_record / btw_delta of the current process → btw/record / btw/delta (not part of the replayed transcript).</summary>
        private void HandleBtwFrame(OmpFrame frame)
        {
            var origin = _bridge.OriginOf(frame);
            lock (_stateLock) if (origin != null && (origin.IsClosed || !ReferenceEquals(origin, _epochConnection))) return;
            if (frame.Type == OmpFrameTypes.BtwRecord)
            {
                if (frame.Json["record"] is JObject record && record.GetString("id") is string) _ui.Post(UiMessageTypes.BtwRecord, new BtwRecordPayload { Record = record }, UiDelivery.State);
                return;
            }

            var recordId = frame.Json.GetString("recordId");
            var delta = frame.Json.GetString("delta");
            if (!string.IsNullOrEmpty(recordId) && !string.IsNullOrEmpty(delta)) _ui.Post(UiMessageTypes.BtwDelta, new BtwDeltaPayload { RecordId = recordId!, Delta = delta! }, UiDelivery.State);
        }
    }
}
