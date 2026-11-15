using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // A message to another agent or team (docs/architecture.md §8.3) is delivered to that partner's own conversation tab: the tab that is
    // open for it, else one opened in the background (the visible tab stays). There it is an ordinary message of that tab. The sending
    // conversation is told where the message stands and, once, how it ended.
    public sealed partial class PompConversationHost : IPartnerRelayHost
    {
        /// <summary>Outcomes kept for conversations that are not shown; the oldest give way.</summary>
        private const int MaxRelayBacklog = 100;

        private sealed class RelayDelivery
        {
            public RelayDelivery(PartnerRelay relay) { Relay = relay; }
            public PartnerRelay Relay { get; }
            public PompSession? Target { get; set; }
            public string Status { get; set; } = "sending";
            public string? Detail { get; set; }
            public string? TargetSession { get; set; }
            public bool Done { get; set; }
        }

        private sealed class RelayOutcome
        {
            public string Root { get; set; } = "";
            public string Session { get; set; } = "";
            public RelayUpdate Update { get; set; } = new RelayUpdate();
        }

        private readonly Dictionary<string, RelayDelivery> _relays = new Dictionary<string, RelayDelivery>(StringComparer.Ordinal);
        private readonly List<RelayOutcome> _relayBacklog = new List<RelayOutcome>();

        string? IPartnerRelayHost.Refusal(PompSession source)
        {
            lock (_gate)
            {
                if (_disposed || !_channels.ContainsKey(source)) return PompLanguage.Pick("요청한 대화가 닫혔어요.", "The requesting conversation is closed.");
                if (_root.Length == 0) return PompLanguage.Pick("연결된 작업 영역이 없어요.", "No workspace is connected.");
                if (_workspaceEpoch.IsCancellationRequested || !SameRoot(source.ConversationRoot, _root))
                    return PompLanguage.Pick("작업 영역이 바뀌는 중이에요.", "The workspace is changing.");
            }
            return null;
        }

        void IPartnerRelayHost.Deliver(PompSession source, PartnerRelay relay)
        {
            var delivery = new RelayDelivery(relay);
            lock (_gate) _relays[relay.Id] = delivery;
            // With the tab commands: a tab is never opened twice, and messages to one partner arrive in the order they were sent.
            _tabCommands.Enqueue(() => DeliverRelayAsync(delivery)).Forget(_logger, "message to another conversation tab");
        }

        RelayUpdate? IPartnerRelayHost.StatusOf(string relayId)
        {
            lock (_gate)
                return _relays.TryGetValue(relayId, out var delivery) && !delivery.Done
                    ? new RelayUpdate { RelayId = relayId, Status = delivery.Status, Detail = delivery.Detail, SessionId = delivery.TargetSession, Target = delivery.Relay.Target }
                    : null;
        }

        IReadOnlyList<RelayUpdate> IPartnerRelayHost.TakeBacklog(string root, string sessionId)
        {
            lock (_gate)
            {
                var mine = _relayBacklog.Where(outcome => outcome.Session == sessionId && SameRoot(outcome.Root, root)).ToList();
                foreach (var outcome in mine) _relayBacklog.Remove(outcome);
                return mine.Select(outcome => outcome.Update).ToList();
            }
        }

        private bool HasRelayBacklog() { lock (_gate) return _relayBacklog.Count > 0; }

        private async Task DeliverRelayAsync(RelayDelivery delivery)
        {
            var relay = delivery.Relay;
            try
            {
                string root; CancellationToken epoch;
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost));
                    if (delivery.Done) return;
                    root = _root; epoch = _workspaceEpoch.Token;
                }
                if (epoch.IsCancellationRequested || !SameRoot(relay.Root, root))
                    throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "The workspace changed."));
                // The partner's open tab, else its most recent conversation, else a new one: what opening the tab would show.
                var id = _tabs.OpenSession(root, relay.Target) ?? MostRecent(root, relay.Target);
                var (session, created) = await OpenChildAsync(root, id, relay.Target, "omp", null, false, epoch).ConfigureAwait(false);
                if (!(session is PompSession target)) throw new InvalidOperationException(PompLanguage.Pick("에이전트·팀 대화는 OMP 연결에서만 열 수 있어요.", "Agent and team conversations require the OMP backend."));
                try
                {
                    epoch.ThrowIfCancellationRequested();
                    if (target.ConversationId is string targetId) _tabs.Observe(root, targetId, created ? relay.Target : null);
                }
                catch { if (created) RemoveChild(target); throw; }
                PublishTabs();
                lock (_gate)
                {
                    if (delivery.Done) return;
                    if (!_channels.ContainsKey(target)) throw new InvalidOperationException(PompLanguage.Pick("받는 탭이 닫혔어요.", "That tab was closed."));
                    delivery.Target = target;
                }
                ForwardRelay(delivery, new RelayUpdate { RelayId = relay.Id, Status = "delivered", SessionId = target.ConversationId });
                target.AcceptRelay(relay);
            }
            catch (OperationCanceledException)
            {
                FinishRelay(delivery, new RelayUpdate { RelayId = relay.Id, Status = "failed", Error = PompLanguage.Pick("작업 영역이 바뀌었어요.", "The workspace changed.") });
            }
            catch (Exception ex)
            {
                _logger.Warn("A message could not be delivered to another conversation tab: " + ex.Message);
                FinishRelay(delivery, new RelayUpdate { RelayId = relay.Id, Status = "failed", Error = ex.Message });
            }
        }

        /// <summary>The receiving conversation reports a state of the message, or its end.</summary>
        private void OnRelayChanged(object? sender, RelayChangedEventArgs e)
        {
            RelayDelivery? delivery;
            lock (_gate) _relays.TryGetValue(e.Update.RelayId, out delivery);
            if (delivery == null) return;
            if (e.Update.Terminal) FinishRelay(delivery, e.Update);
            else ForwardRelay(delivery, e.Update);
        }

        /// <summary>The tab a message went to is gone (closed, another workspace, the window closes): the message ends there.</summary>
        private void StopRelaysTo(PompSession target)
        {
            RelayDelivery[] open;
            lock (_gate) open = _relays.Values.Where(delivery => ReferenceEquals(delivery.Target, target) && !delivery.Done).ToArray();
            foreach (var delivery in open)
                FinishRelay(delivery, new RelayUpdate
                {
                    RelayId = delivery.Relay.Id, Status = "stopped", SessionId = delivery.TargetSession,
                    Error = PompLanguage.Pick("받는 탭이 닫혔어요.", "That tab was closed."),
                });
        }

        private void FinishRelay(RelayDelivery delivery, RelayUpdate update)
        {
            lock (_gate)
            {
                if (delivery.Done) return;
                delivery.Done = true; _relays.Remove(delivery.Relay.Id);
                update.SessionId ??= delivery.TargetSession;
            }
            ForwardRelay(delivery, update);
        }

        /// <summary>
        /// To the conversation that sent the message, when it is shown by a live child. A state that cannot be shown is dropped (the next
        /// one or a restore tells it again); an outcome waits until that conversation is shown again in this window.
        /// </summary>
        private void ForwardRelay(RelayDelivery delivery, RelayUpdate update)
        {
            var relay = delivery.Relay;
            update.Target = relay.Target; update.RunId = relay.RunId; update.FromHook = relay.FromHook;
            PompSession? source;
            lock (_gate)
            {
                if (_disposed) return;
                if (!update.Terminal) { delivery.Status = update.Status; delivery.Detail = update.Detail; delivery.TargetSession = update.SessionId ?? delivery.TargetSession; }
                source = _channels.Keys.OfType<PompSession>().FirstOrDefault(session => session.ConversationId == relay.SourceSession && SameRoot(session.ConversationRoot, relay.Root));
            }
            if (source != null && source.ApplyRelayUpdate(relay.SourceSession, update)) return;
            if (!update.Terminal) return;
            lock (_gate)
            {
                _relayBacklog.Add(new RelayOutcome { Root = relay.Root, Session = relay.SourceSession, Update = update });
                if (_relayBacklog.Count > MaxRelayBacklog) _relayBacklog.RemoveAt(0);
            }
            // It may have become showable between the attempt and the note.
            source?.PullRelayBacklog();
        }
    }
}
