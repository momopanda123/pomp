using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// The editor model (docs/architecture.md "편집기 모델"): the model of completions, inline edits and code captions, chosen apart from
    /// the chat model. It is the options value <see cref="IInlineModelSettings.InlineModel"/>: empty = automatic (omp's fast model,
    /// <c>@smol</c>), <c>@chat</c> = the chat model, anything else a model string omp resolves (normally <c>provider/id</c>).
    /// </summary>
    internal static class EditorModelSetting
    {
        public const string Chat = "@chat";
        public const string Fast = "@smol";

        /// <summary>The model string sent to the engine for this options value; null = the chat model.</summary>
        public static string? EngineSpec(string? setting)
        {
            var value = (setting ?? string.Empty).Trim();
            if (value.Length == 0) return Fast;
            if (string.Equals(value, Chat, StringComparison.OrdinalIgnoreCase)) return null;
            return value.Length <= 512 ? value : value.Substring(0, 512);
        }

        /// <summary><c>{ choice, model? }</c> of <c>setup/state.editorModel</c>. A value typed in the options without a provider has an empty provider.</summary>
        public static JObject Describe(string? setting)
        {
            var value = (setting ?? string.Empty).Trim();
            if (value.Length == 0) return new JObject { ["choice"] = "auto" };
            if (string.Equals(value, Chat, StringComparison.OrdinalIgnoreCase)) return new JObject { ["choice"] = "chat" };
            var slash = value.IndexOf('/');
            var provider = slash > 0 ? value.Substring(0, slash) : string.Empty;
            var id = slash > 0 ? value.Substring(slash + 1) : value;
            return new JObject { ["choice"] = "model", ["model"] = new JObject { ["provider"] = Bound(provider, 200), ["id"] = Bound(id, 200) } };
        }

        private static string Bound(string text, int max) => text.Length <= max ? text : text.Substring(0, max);
    }

    internal sealed partial class ProviderSetup
    {
        private JObject? _editorEffective;
        private bool _editorResolving;
        private string? _editorError;
        private int _editorGeneration;
        private bool _editorBusy;
        private bool _editorRetrying;

        /// <summary>Asks the running engine which model an engine spec means now (<c>{ provider, id, name? }</c>); set by the session.</summary>
        public Func<string?, CancellationToken, Task<JObject?>>? ResolveEditorModel { get; set; }

        private string? EditorSetting => (_settings as IInlineModelSettings)?.InlineModel;

        private JObject EditorModelState()
        {
            var state = EditorModelSetting.Describe(EditorSetting);
            if (_editorEffective != null) state["effective"] = _editorEffective.DeepClone();
            if (_editorResolving) state["resolving"] = true;
            if (_editorError != null) state["error"] = _editorError;
            return state;
        }

        /// <summary>The chat model or the engine changed: what "same as chat" or "automatic" means may have changed too.</summary>
        private void ForgetEditorModel()
        {
            _editorEffective = null;
            _editorError = null;
            _editorBusy = false;
            // A resolution still running belongs to the old generation and will not report back: its spinner goes now.
            _editorResolving = false;
            _editorGeneration++;
        }

        private async Task SetEditorModelAsync(JObject request)
        {
            if (!(_settings is IInlineModelSettingsWriter writer)) throw new ProviderSetupException(SetupText.Get("보조 모델을 바꿀 수 없습니다.", "The helper model cannot be changed."));
            string value;
            switch (Text(request, "choice"))
            {
                case "auto": value = string.Empty; break;
                case "chat": value = EditorModelSetting.Chat; break;
                case "model":
                    var provider = Text(request, "provider");
                    var id = Text(request, "modelId");
                    if (!ValidModelPart(provider) || !ValidModelPart(id)) throw new ProviderSetupException(SetupText.Get("모델 공급자나 ID 형식이 올바르지 않습니다.", "Model provider or ID is invalid."));
                    value = provider + "/" + id;
                    break;
                default: throw new ProviderSetupException(SetupText.Get("보조 모델 선택이 올바르지 않습니다.", "Invalid helper model choice."));
            }

            await writer.SetInlineModelAsync(value, _token).ConfigureAwait(false);
            lock (_lock) ForgetEditorModel();
            await ResolveEditorModelAsync().ConfigureAwait(false);
        }

        /// <summary>Asks the engine which model the current choice means (no model is called). Failures are shown, not thrown.</summary>
        private async Task ResolveEditorModelAsync()
        {
            var resolve = ResolveEditorModel;
            if (resolve == null) return;
            int generation;
            lock (_lock)
            {
                if (_bridgeState() != BridgeState.Running)
                {
                    _editorResolving = false;
                    _editorBusy = false;
                    _editorError = SetupText.Get("연결되어 있지 않아 어떤 모델인지 확인할 수 없어요.", "Not connected, so the model cannot be checked.");
                    return;
                }

                _editorResolving = true;
                _editorError = null;
                generation = ++_editorGeneration;
            }

            Publish();
            JObject? model = null;
            string? error = null;
            var busy = false;
            try
            {
                model = await resolve(EditorModelSetting.EngineSpec(EditorSetting), _token).ConfigureAwait(false);
                if (model == null) error = SetupText.Get("이 모델을 찾지 못했어요.", "This model was not found.");
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.Debug("The helper model could not be resolved: " + ex.Message);
                busy = ex is InvalidOperationException;
                error = ex is InvalidOperationException
                    ? SetupText.Get("지금은 확인할 수 없어요.", "Not available right now.")
                    : SetupText.Get("보조 모델을 확인하지 못했어요.", "The helper model could not be checked.");
            }
            finally
            {
                lock (_lock)
                {
                    if (generation == _editorGeneration)
                    {
                        _editorResolving = false;
                        _editorEffective = model;
                        _editorError = error;
                        _editorBusy = busy;
                    }
                }
            }

            if (busy) RetryEditorModelLater();
        }

        /// <summary>
        /// A resolution refused because the chat was busy (a reply or an approval) is tried again every 5 seconds for up to 2 minutes,
        /// so the page shows the model once the chat is idle without being opened again.
        /// </summary>
        private void RetryEditorModelLater()
        {
            lock (_lock)
            {
                if (_editorRetrying) return;
                _editorRetrying = true;
            }

            Task.Run(async () =>
            {
                try
                {
                    for (var attempt = 0; attempt < 24; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), _token).ConfigureAwait(false);
                        lock (_lock) if (!_editorBusy || _disposed) return;
                        await ResolveEditorModelAsync().ConfigureAwait(false);
                        Publish();
                        lock (_lock) if (!_editorBusy) return;
                    }
                }
                finally
                {
                    lock (_lock) _editorRetrying = false;
                }
            }).Forget(_logger, "helper model retry");
        }
    }
}
