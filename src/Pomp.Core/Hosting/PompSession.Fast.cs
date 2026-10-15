using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // UI → host session/fast: omp's fast mode (the priority service tier of the current model's family, omp's /fast).
    public sealed partial class PompSession
    {
        private int _fastModeRequests;

        /// <summary>
        /// Sends <c>set_fast_mode</c> and answers with one <c>session/fastResult</c>; the toggle then follows <c>session/info</c>.
        /// Not queued behind chat commands, like the model and thinking pickers: omp applies the tier to its next request.
        /// </summary>
        private async Task HandleFastModeAsync(SessionFastPayload request)
        {
            var answer = new SessionFastResultPayload { RequestId = request.RequestId };
            if (Interlocked.Increment(ref _fastModeRequests) > 4)
            {
                // A stuck engine must not collect an unbounded number of toggles: the page sends one at a time anyway.
                Interlocked.Decrement(ref _fastModeRequests);
                answer.Error = SetupText.Get("이전 fast 모드 요청이 아직 끝나지 않았습니다.", "The previous fast mode request has not finished yet.");
                _ui.Post(UiMessageTypes.SessionFastResult, answer, UiDelivery.State);
                return;
            }

            try
            {
                if (_bridge.State != BridgeState.Running)
                {
                    answer.Error = SetupText.Get("연결되어 있지 않습니다.", "Not connected.");
                }
                else
                {
                    var response = await _bridge.SendCommandAsync(
                        new JObject { ["type"] = "set_fast_mode", ["enabled"] = request.Enabled }, TimeSpan.FromSeconds(10), _disposeCts.Token)
                        .ConfigureAwait(false);
                    if (response.Success)
                    {
                        var data = response.Data as JObject;
                        answer.Ok = true;
                        answer.Enabled = data?.GetBool("enabled") ?? request.Enabled;
                        answer.Active = data?.GetBool("active") ?? false;
                    }
                    else
                    {
                        answer.Error = string.IsNullOrWhiteSpace(response.Error)
                            ? SetupText.Get("fast 모드를 바꾸지 못했습니다.", "Fast mode could not be changed.")
                            : StripTerminalEscapes(response.Error!);
                    }
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("set_fast_mode failed: " + ex.Message);
                answer.Error = ex.Message;
            }
            finally
            {
                Interlocked.Decrement(ref _fastModeRequests);
            }

            _ui.Post(UiMessageTypes.SessionFastResult, answer, UiDelivery.State);
            RequestInfoRefresh();
        }
    }
}
