using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Json;
using Pomp.Ui;
using Pomp.VisualStudio.Interop;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>
    /// <see cref="IUiChannel"/> over a WebView2 (docs/ui-protocol.md): host to page with
    /// <c>PostWebMessageAsJson</c>, page to host with <c>WebMessageAsJson</c>.
    /// Messages posted before the page sent <c>ui/ready</c> are queued and replayed in order; sends always happen on the
    /// UI thread and keep FIFO order regardless of the posting thread.
    /// <para>
    /// The queue never outlives a document or a WebView: it is dropped when a new document starts loading, when a WebView is
    /// attached or detached, and when <c>ui/ready</c> arrives, and every dropped entry is completed (as "not delivered") so no
    /// caller of <see cref="PostAsync"/> waits for a message that will never be sent. <c>UiHub</c> posts nothing before the first
    /// <c>ui/ready</c> of an attach (it keeps its own pre-ready list) and answers every <c>ui/ready</c> with a fresh snapshot, so
    /// whatever was queued while the page was not ready (after a reload the hub keeps posting for a moment) is stale by
    /// definition and would only replay half a turn into the new page ahead of that snapshot.
    /// </para>
    /// <para>
    /// Page to host messages, in the order they are checked: the source must be the trusted origin (else ignored); the text must be at
    /// most <see cref="MaxInboundMessageChars"/> characters, checked before it is parsed (else the message is dropped, logged, and
    /// the page is told through a host to UI <c>error</c> message with code <c>message_too_large</c>, because a page that
    /// optimistically entered its "sending" state would otherwise wait for an answer that never comes); it must parse and carry a type
    /// of at most <see cref="MaxMessageTypeLength"/> characters (else ignored). Only <c>ui/log</c>, the page's diagnostics feed, is limited further:
    /// at most <see cref="MaxLogMessageChars"/> characters and <see cref="MaxMessagesPerSecond"/> messages per second (token
    /// bucket, <see cref="MaxMessageBurst"/> burst); excess <c>ui/log</c> messages are dropped silently (one throttled log
    /// warning). Every other message type is never rate limited: dropping a <c>chat/send</c> or a <c>permission/respond</c>
    /// would strand the page. Ignored messages are logged once per <see cref="RejectLogInterval"/> at most.
    /// </para>
    /// </summary>
    internal sealed class WebViewUiChannel : IUiChannel, IDisposable
    {
        /// <summary>Host-chrome message the page sends for the editor ↔ chat focus chord (host/hello.shortcuts.toggleFocus); never forwarded to the session.</summary>
        public const string ToggleFocusMessageType = "ui/toggleFocus";

        /// <summary>Host-chrome message the page sends when files or an image are pasted into the chat input (only VS can read their paths); never forwarded to the session.</summary>
        public const string PasteFilesMessageType = "ui/pasteFiles";

        /// <summary>
        /// Host-chrome message the page sends with the files dropped on it (<c>postMessageWithAdditionalObjects</c>; the paths come only
        /// as the message's additional objects, never in its JSON); never forwarded to the session.
        /// </summary>
        public const string DropFilesMessageType = "ui/dropFiles";

        public const string EditorFollowMessageType = "ui/editorFollow";

        /// <summary>Host-chrome message: a short notice of the page (a failed control action, a warning) for the VS status bar; never forwarded to the session.</summary>
        public const string StatusMessageType = "ui/status";

        /// <summary>
        /// Largest page to host message accepted (characters of the JSON text, checked before it is parsed): 4 MiB. A larger one is
        /// dropped and the page is told (<see cref="MessageTooLargeCode"/>).
        /// </summary>
        public const int MaxInboundMessageChars = 4 * 1024 * 1024;

        /// <summary>Longest accepted message type string.</summary>
        public const int MaxMessageTypeLength = 64;

        /// <summary>Largest <c>ui/log</c> message accepted (characters of the JSON text): 64 KiB; a log line is short.</summary>
        public const int MaxLogMessageChars = 64 * 1024;

        /// <summary>
        /// Sustained <c>ui/log</c> rate accepted, per second (the only rate limited message type); the page normally logs a handful of
        /// lines per minute.
        /// </summary>
        public const double MaxMessagesPerSecond = 200;

        /// <summary>Short <c>ui/log</c> bursts above the sustained rate are tolerated up to this many messages (the token bucket's capacity).</summary>
        public const double MaxMessageBurst = 400;

        /// <summary>Code of the host to UI <c>error</c> message sent when a page message exceeded <see cref="MaxInboundMessageChars"/>.</summary>
        public const string MessageTooLargeCode = "message_too_large";

        private const int MaxQueuedMessages = 2000;

        /// <summary>Rejected inbound messages are logged at most once per this interval (with a count of the ones skipped).</summary>
        private static readonly TimeSpan RejectLogInterval = TimeSpan.FromSeconds(5);

        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly IPompLogger logger;
        private readonly string trustedHost;
        private readonly object gate = new object();
        private readonly object rejectGate = new object();
        private readonly object rateGate = new object();
        private readonly Queue<PendingMessage> queue = new Queue<PendingMessage>();
        private CoreWebView2? core;
        private bool ready;
        private bool pumping;
        private bool disposed;
        private bool overflowLogged;
        // The Live failure last logged and shown; UI thread only. The same failure on every following message is not repeated.
        private string? lastLiveFailure;
        private DateTime lastRejectLogUtc = DateTime.MinValue;
        private int suppressedRejects;
        private double rateTokens = MaxMessageBurst;
        private long rateStamp = Stopwatch.GetTimestamp();

        public WebViewUiChannel(JoinableTaskFactory joinableTaskFactory, IPompLogger logger, string trustedHost)
        {
            this.joinableTaskFactory = joinableTaskFactory;
            this.logger = logger;
            this.trustedHost = trustedHost;
        }

        /// <inheritdoc />
        public event EventHandler<UiMessage>? MessageReceived;

        /// <summary>Raised on the UI thread after <c>ui/ready</c> was processed (queue flushed, session notified).</summary>
        public event EventHandler? PageReady;

        /// <summary>Raised on the UI thread when the page reports the focus-toggle shortcut.</summary>
        public event EventHandler? ToggleFocusRequested;

        /// <summary>Raised on the UI thread when files or an image were pasted into the chat input.</summary>
        public event EventHandler? PasteFilesRequested;

        /// <summary>Raised on the UI thread with the full paths of the files dropped on the page.</summary>
        public event EventHandler<IReadOnlyList<string>>? FilesDropped;

        public event EventHandler<UiMessage>? EditorFollowRequested;

        /// <summary>Raised on the UI thread when the page reports a notice for the VS status bar.</summary>
        public event EventHandler<UiMessage>? StatusRequested;

        /// <summary>Raised on the UI thread when updating the Live editor display for a session message failed.</summary>
        public event EventHandler<Exception>? LiveFailed;

        /// <summary>Observes authoritative session state on the UI thread before the page sees it.</summary>
        public Action<UiMessage>? ObserveSession { get; set; }

        /// <inheritdoc />
        public bool IsReady
        {
            get
            {
                lock (gate)
                {
                    return ready;
                }
            }
        }

        /// <inheritdoc />
        public Task PostAsync(UiMessage message, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var json = PompJson.Serialize(message);
            PendingMessage pendingMessage;
            var startPump = false;
            bool isReady;

            lock (gate)
            {
                if (disposed)
                {
                    return Task.CompletedTask;
                }

                isReady = ready;
                pendingMessage = new PendingMessage(json, cancellationToken, message.Type == UiMessageTypes.SessionInfo ? message : null);
                if (queue.Count >= MaxQueuedMessages)
                {
                    var dropped = queue.Dequeue();
                    dropped.Completion.TrySetResult(false);
                    if (!overflowLogged)
                    {
                        overflowLogged = true;
                        logger.Warn("UI message queue overflow: dropping the oldest queued messages (the page is not ready).");
                    }
                }

                queue.Enqueue(pendingMessage);

                if (ready && core != null && !pumping)
                {
                    pumping = true;
                    startPump = true;
                }
            }

            if (startPump)
            {
                StartPump();
            }

            // Not ready yet: the message is queued, which is all the caller can wait for.
            return isReady ? pendingMessage.Completion.Task : Task.CompletedTask;
        }

        /// <summary>Binds the channel to an initialised WebView2 (UI thread). Whatever was queued for a previous WebView is dropped.</summary>
        public void Attach(CoreWebView2 webView)
        {
            List<PendingMessage>? stale;
            lock (gate)
            {
                core = webView;
                ready = false;
                stale = TakeQueueLocked();
            }

            CompleteDropped(stale);
            ResetRateLimit();

            webView.WebMessageReceived += OnWebMessageReceived;
            webView.NavigationStarting += OnNavigationStarting;
        }

        /// <summary>
        /// Unbinds from the current WebView2 (UI thread). Whatever is still queued can no longer be sent and is completed as "not
        /// delivered" (a caller that awaited it, such as the hub's outbox drain, must not wait for ever); a later
        /// <see cref="Attach"/> starts from an empty queue and the pump starts again with the next message posted while the page is ready.
        /// </summary>
        public void Detach()
        {
            CoreWebView2? previous;
            List<PendingMessage>? stale;
            lock (gate)
            {
                previous = core;
                core = null;
                ready = false;
                stale = TakeQueueLocked();
            }

            // Completed outside the lock, like every other completion in this class.
            CompleteDropped(stale);

            if (previous != null)
            {
                try
                {
                    previous.WebMessageReceived -= OnWebMessageReceived;
                    previous.NavigationStarting -= OnNavigationStarting;
                }
                catch (Exception)
                {
                    // The WebView may already be torn down.
                }
            }
        }

        /// <summary>Raises <see cref="MessageReceived"/> as if the page had sent the message (used by host commands).</summary>
        public void InjectFromHost(UiMessage message) => RaiseMessageReceived(message);

        public void Dispose()
        {
            List<PendingMessage> remaining;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                remaining = new List<PendingMessage>(queue);
                queue.Clear();
            }

            Detach();
            foreach (var item in remaining)
            {
                item.Completion.TrySetResult(false);
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // A new document is about to load: the previous page's ui/ready no longer applies, and neither does anything queued for it.
            if (e.Cancel || e.IsRedirected)
            {
                return;
            }

            List<PendingMessage>? stale;
            lock (gate)
            {
                ready = false;
                stale = TakeQueueLocked();
            }

            CompleteDropped(stale);
            ResetRateLimit();
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                if (!IsTrustedSource(e.Source))
                {
                    LogRejected("a web message from an untrusted source (" + Shorten(e.Source) + ")");
                    return;
                }

                string json;
                try
                {
                    json = e.WebMessageAsJson;
                }
                catch (ArgumentException)
                {
                    return;
                }

                HandleMessage(json, e);
            }
            catch (Exception ex)
            {
                logger.Error("Handling a message from the chat page failed.", ex);
            }
        }

        /// <summary>
        /// The checks and the dispatch of one message of the trusted page (UI thread), in the order the class remarks describe: size,
        /// then parse, then the type specific rules, then dispatch.
        /// </summary>
        private void HandleMessage(string json, CoreWebView2WebMessageReceivedEventArgs args)
        {
            // The size is checked before the text is handed to the JSON parser.
            if (json.Length > MaxInboundMessageChars)
            {
                LogRejected("an oversized message from the chat page (" + json.Length.ToString(CultureInfo.InvariantCulture)
                            + " characters; the limit is " + MaxInboundMessageChars.ToString(CultureInfo.InvariantCulture) + ")");

                // Not silent: the page optimistically entered its "sending" state (or removed an answered approval) when it sent this.
                NotifyPageMessageTooLarge(json.Length);
                return;
            }

            var message = TryParse(json);
            if (message == null)
            {
                return;
            }

            // ui/log is the only limited type; everything else (chat/send, permission/respond, ui/ready, ...) is delivered.
            if (message.Type == UiMessageTypes.UiLog && !AcceptLogMessage(json.Length))
            {
                return;
            }

            if (message.Type == ToggleFocusMessageType)
            {
                ToggleFocusRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (message.Type == PasteFilesMessageType)
            {
                PasteFilesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (message.Type == DropFilesMessageType)
            {
                var paths = DroppedPaths(args);
                if (paths.Count > 0) FilesDropped?.Invoke(this, paths);
                return;
            }

            if (message.Type == EditorFollowMessageType)
            {
                EditorFollowRequested?.Invoke(this, message);
                return;
            }

            if (message.Type == StatusMessageType)
            {
                StatusRequested?.Invoke(this, message);
                return;
            }

            if (message.Type == UiMessageTypes.UiReady)
            {
                HandleReady();
            }

            RaiseMessageReceived(message);

            if (message.Type == UiMessageTypes.UiReady)
            {
                PageReady?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// The full paths of the files a <c>ui/dropFiles</c> message carries as additional objects. Read through reflection: the x64
        /// package compiles against an older WebView2 and VS 2022+ run their own copy, which may lack a working <c>AdditionalObjects</c>
        /// (added in SDK 1.0.1774.30 but broken until 1.0.1901.177; runtime 113); such a copy or runtime yields no paths and a log line.
        /// </summary>
        private List<string> DroppedPaths(CoreWebView2WebMessageReceivedEventArgs args)
        {
            var paths = new List<string>();
            try
            {
                if (args.GetType().GetProperty("AdditionalObjects")?.GetValue(args) is System.Collections.IEnumerable objects)
                {
                    foreach (var item in objects)
                    {
                        if (item?.GetType().GetProperty("Path")?.GetValue(item) is string path && path.Length > 0) paths.Add(path);
                    }
                }
                else
                {
                    logger.Warn("Files were dropped on the chat page, but this WebView2 cannot pass their paths (needs SDK 1.0.1901.177 or newer).");
                }
            }
            catch (Exception ex) when (ex is System.Reflection.TargetInvocationException || ex is NotImplementedException || ex is InvalidCastException || ex is System.Runtime.InteropServices.COMException)
            {
                logger.Warn("Reading the files dropped on the chat page failed (WebView2 SDK older than 1.0.1901.177 or Runtime older than 113): " + (ex.InnerException ?? ex).Message);
            }

            return paths;
        }

        private void HandleReady()
        {
            // Whatever is still queued was posted while the page was not ready (after a reload, UiHub keeps posting for a moment):
            // the hub answers this ui/ready with a fresh snapshot, so replaying the old messages ahead of it would only confuse the page.
            List<PendingMessage>? stale;
            lock (gate)
            {
                ready = true;
                stale = TakeQueueLocked();
            }

            CompleteDropped(stale);
        }

        private UiMessage? TryParse(string json)
        {
            JObject obj;
            try
            {
                obj = PompJson.ParseObject(json);
            }
            catch (JsonException ex)
            {
                LogRejected("a malformed message from the chat page (" + Shorten(ex.Message) + ")");
                return null;
            }

            if (!(obj["type"] is JValue typeValue) || typeValue.Type != JTokenType.String)
            {
                return null;
            }

            var type = (string?)typeValue;
            if (string.IsNullOrEmpty(type))
            {
                return null;
            }

            if (type!.Length > MaxMessageTypeLength)
            {
                LogRejected("a message whose type is longer than " + MaxMessageTypeLength.ToString(CultureInfo.InvariantCulture) + " characters");
                return null;
            }

            var id = obj["id"] is JValue idValue && idValue.Type == JTokenType.String ? (string?)idValue : null;
            return new UiMessage { Type = type, Id = id, Payload = obj["payload"] as JObject };
        }

        /// <summary>
        /// Tells the page that one of its messages was dropped for its size, through the channel's normal ordered outbound path.
        /// Skipped quietly when no page is attached; never throws.
        /// </summary>
        private void NotifyPageMessageTooLarge(int characters)
        {
            try
            {
                lock (gate)
                {
                    if (disposed || core == null)
                    {
                        return;
                    }
                }

                var notice = new UiMessage(
                    UiMessageTypes.Error,
                    new UiErrorInfo
                    {
                        Code = MessageTooLargeCode,
                        Message = "The chat page sent a message of " + characters.ToString(CultureInfo.InvariantCulture)
                                  + " characters; the limit is " + MaxInboundMessageChars.ToString(CultureInfo.InvariantCulture)
                                  + ". It was not delivered.",
                        Retryable = false,
                    });

                // The pump posts from the UI thread (this handler runs there) and keeps the FIFO order; a failure is observed and logged.
                PostAsync(notice).Forget(logger, "Telling the chat page that its message was too large");
            }
            catch (Exception ex)
            {
                try
                {
                    logger.Debug("Could not tell the chat page that its message was too large: " + ex.Message);
                }
                catch (Exception)
                {
                    // Logging must never throw.
                }
            }
        }

        /// <summary>
        /// Limits for <c>ui/log</c>, the only rate limited message type: at most <see cref="MaxLogMessageChars"/> characters and the
        /// token bucket. Returns false (after one throttled log warning) when the message must be dropped, silently for the page.
        /// </summary>
        private bool AcceptLogMessage(int characters)
        {
            if (characters > MaxLogMessageChars)
            {
                LogRejected("an oversized ui/log message (" + characters.ToString(CultureInfo.InvariantCulture)
                            + " characters; the limit is " + MaxLogMessageChars.ToString(CultureInfo.InvariantCulture) + ")");
                return false;
            }

            if (!TryTakeRateToken())
            {
                LogRejected("ui/log messages from the chat page above the limit of " + MaxMessagesPerSecond.ToString("0", CultureInfo.InvariantCulture)
                            + " per second (burst " + MaxMessageBurst.ToString("0", CultureInfo.InvariantCulture) + ")");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Token bucket for the <c>ui/log</c> stream: <see cref="MaxMessagesPerSecond"/> sustained, <see cref="MaxMessageBurst"/> burst.
        /// Returns false when the message must be ignored.
        /// </summary>
        private bool TryTakeRateToken()
        {
            lock (rateGate)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsedSeconds = (now - rateStamp) / (double)Stopwatch.Frequency;
                rateStamp = now;
                if (elapsedSeconds > 0)
                {
                    rateTokens = Math.Min(MaxMessageBurst, rateTokens + (elapsedSeconds * MaxMessagesPerSecond));
                }

                if (rateTokens < 1)
                {
                    return false;
                }

                rateTokens -= 1;
                return true;
            }
        }

        /// <summary>A new document starts with a full bucket: a log flood from the previous page must not cost the next page its first log lines.</summary>
        private void ResetRateLimit()
        {
            lock (rateGate)
            {
                rateTokens = MaxMessageBurst;
                rateStamp = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>Logs a rejected inbound message at most once per <see cref="RejectLogInterval"/>; never throws.</summary>
        private void LogRejected(string what)
        {
            try
            {
                var suppressed = 0;
                lock (rejectGate)
                {
                    var now = DateTime.UtcNow;
                    if (now - lastRejectLogUtc < RejectLogInterval)
                    {
                        suppressedRejects++;
                        return;
                    }

                    lastRejectLogUtc = now;
                    suppressed = suppressedRejects;
                    suppressedRejects = 0;
                }

                logger.Warn("Ignoring " + what
                            + (suppressed > 0 ? " [" + suppressed.ToString(CultureInfo.InvariantCulture) + " more ignored since the last entry]" : string.Empty) + ".");
            }
            catch (Exception)
            {
                // Logging must never throw.
            }
        }

        private static string Shorten(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(empty)";
            }

            const int Limit = 120;
            return text!.Length <= Limit ? text : text.Substring(0, Limit);
        }

        private void RaiseMessageReceived(UiMessage message)
        {
            var handlers = MessageReceived;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<UiMessage>)handler)(this, message);
                }
                catch (Exception ex)
                {
                    logger.Error("A UI message handler failed for '" + message.Type + "'.", ex);
                }
            }
        }

        private bool IsTrustedSource(string? source)
        {
            if (string.IsNullOrEmpty(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
                   && string.Equals(uri.Host, trustedHost, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Empties the queue and returns what was in it (null when it was empty). Must be called with the lock held.</summary>
        private List<PendingMessage>? TakeQueueLocked()
        {
            if (queue.Count == 0)
            {
                return null;
            }

            var items = new List<PendingMessage>(queue);
            queue.Clear();
            overflowLogged = false;
            return items;
        }

        private static void CompleteDropped(List<PendingMessage>? items)
        {
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                item.Completion.TrySetResult(false);
            }
        }

        private void StartPump()
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await joinableTaskFactory.SwitchToMainThreadAsync();
                    PumpOnUiThread();
                }
                catch (Exception ex)
                {
                    logger.Error("Posting queued messages to the chat page failed.", ex);
                    lock (gate)
                    {
                        pumping = false;
                    }
                }
            });
        }

        private void PumpOnUiThread()
        {
            while (true)
            {
                PendingMessage item;
                CoreWebView2 target;
                lock (gate)
                {
                    if (!ready || core == null || queue.Count == 0)
                    {
                        pumping = false;
                        return;
                    }

                    item = queue.Dequeue();
                    target = core;
                }

                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled();
                    continue;
                }

                if (item.SessionMessage != null && ObserveSession != null)
                {
                    try
                    {
                        ObserveSession(item.SessionMessage);
                        lastLiveFailure = null;
                    }
                    catch (Exception ex)
                    {
                        // Live is a display: its failure is reported in the VS status bar and the log, never as a chat `error` (the
                        // page would take that for a failed send or session switch). A failure that persists (every streamed message
                        // would hit it) is logged and shown once, until an observation succeeds again.
                        var failure = ex.GetType().FullName + ": " + ex.Message;
                        if (failure != lastLiveFailure)
                        {
                            lastLiveFailure = failure;
                            logger.Error("Observing Live editor session state failed.", ex);
                            LiveFailed?.Invoke(this, ex);
                        }
                    }
                }

                try
                {
                    target.PostWebMessageAsJson(item.Json);
                    item.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    logger.Warn("PostWebMessageAsJson failed.", ex);
                    item.Completion.TrySetException(ex);
                }
            }
        }

        private sealed class PendingMessage
        {
            public PendingMessage(string json, CancellationToken cancellationToken, UiMessage? sessionMessage)
            {
                Json = json;
                CancellationToken = cancellationToken;
                SessionMessage = sessionMessage;
            }

            public string Json { get; }

            public UiMessage? SessionMessage { get; }

            public CancellationToken CancellationToken { get; }

            public TaskCompletionSource<bool> Completion { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
