using System;
using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Json;
using Pomp.Protocol;
using Pomp.Ui;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Composition
{
    /// <summary>
    /// Temporary <see cref="IPompSession"/> that keeps the tool window usable end to end before the real core
    /// (<c>Pomp.Core.PompSession</c> + <c>Pomp.Tools.HostToolCatalog</c>) is wired in: it answers the page's
    /// <c>ui/ready</c> with <c>host/hello</c> and a stopped bridge state, and explains itself to <c>chat/send</c>.
    /// Replace it in <see cref="PompServices.CreateSession"/>.
    /// </summary>
    internal sealed class PlaceholderSession : IPompSession
    {
        private const string NotWiredMessage = "Core not wired yet";

        private readonly IPompHostEnvironment environment;
        private readonly IPompSettings settings;
        private readonly IPompLogger logger;
        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly StoppedOmpBridge bridge = new StoppedOmpBridge();
        private readonly object gate = new object();
        private IUiChannel? channel;

        public PlaceholderSession(IPompHostEnvironment environment, IPompSettings settings, IPompLogger logger, JoinableTaskFactory joinableTaskFactory)
        {
            this.environment = environment;
            this.settings = settings;
            this.logger = logger;
            this.joinableTaskFactory = joinableTaskFactory;
        }

        public IOmpProcessBridge Bridge => bridge;

        public void AttachUi(IUiChannel uiChannel)
        {
            IUiChannel? previous;
            lock (gate)
            {
                previous = channel;
                channel = uiChannel;
            }

            if (previous != null)
            {
                previous.MessageReceived -= OnMessageReceived;
            }

            uiChannel.MessageReceived += OnMessageReceived;
            if (uiChannel.IsReady)
            {
                PostHello();
            }
        }

        public void DetachUi(IUiChannel uiChannel)
        {
            lock (gate)
            {
                if (!ReferenceEquals(channel, uiChannel))
                {
                    return;
                }

                channel = null;
            }

            uiChannel.MessageReceived -= OnMessageReceived;
        }

        public Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            logger.Info("PlaceholderSession: omp is not started (core not wired yet).");
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken)
        {
            logger.Info("PlaceholderSession: restart requested (nothing to restart, core not wired yet).");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IUiChannel? current;
            lock (gate)
            {
                current = channel;
                channel = null;
            }

            if (current != null)
            {
                current.MessageReceived -= OnMessageReceived;
            }

            bridge.Dispose();
        }

        private void OnMessageReceived(object? sender, UiMessage message)
        {
            switch (message.Type)
            {
                case UiMessageTypes.UiReady:
                    PostHello();
                    break;

                case UiMessageTypes.ChatSend:
                    PostNotice("info", NotWiredMessage + ": the agent is not connected in this build, so your message was not sent.");
                    break;

                case UiMessageTypes.SessionNew:
                case UiMessageTypes.BridgeRestart:
                    PostNotice("info", NotWiredMessage + ": there is no agent session to " + (message.Type == UiMessageTypes.SessionNew ? "reset." : "restart."));
                    break;

                case UiMessageTypes.LogOpen:
                    environment.ShowLog();
                    break;

                case UiMessageTypes.SettingsOpen:
                    environment.ShowSettings();
                    break;

                case UiMessageTypes.UiLog:
                    ForwardUiLog(message.Payload);
                    break;

                default:
                    // Unknown or irrelevant types are ignored by design (forward compatibility).
                    break;
            }
        }

        private void PostHello()
        {
            Post(new UiMessage(UiMessageTypes.HostHello, new HostHelloPayload
            {
                Version = environment.HostVersion,
                VsVersion = environment.VsVersion,
                Locale = PompLanguage.Locale,
                IncludeEditorContextByDefault = settings.IncludeEditorContextByDefault,
                ComposerPredictions = settings.ComposerPredictions,
            }));

            Post(new UiMessage(UiMessageTypes.BridgeState, new BridgeStatePayload
            {
                State = BridgeState.Stopped,
                Message = NotWiredMessage,
            }));
        }

        private void PostNotice(string level, string message) =>
            Post(new UiMessage(UiMessageTypes.ChatNotice, new ChatNoticePayload { Level = level, Message = message }));

        private void ForwardUiLog(JObject? payload)
        {
            var log = PompJson.ToObject<UiLogPayload>(payload);
            if (log == null || string.IsNullOrEmpty(log.Message))
            {
                return;
            }

            var level = PompLogLevel.Info;
            switch ((log.Level ?? string.Empty).ToLowerInvariant())
            {
                case "debug":
                    level = PompLogLevel.Debug;
                    break;
                case "warning":
                case "warn":
                    level = PompLogLevel.Warning;
                    break;
                case "error":
                    level = PompLogLevel.Error;
                    break;
            }

            logger.Log(level, "[ui] " + log.Message);
        }

        private void Post(UiMessage message)
        {
            IUiChannel? target;
            lock (gate)
            {
                target = channel;
            }

            if (target == null)
            {
                return;
            }

            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await target.PostAsync(message);
                }
                catch (Exception ex)
                {
                    logger.Warn("PlaceholderSession: posting '" + message.Type + "' to the UI failed.", ex);
                }
            });
        }
    }
}
