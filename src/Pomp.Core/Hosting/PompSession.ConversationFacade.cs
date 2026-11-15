using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession : IConversationSession
    {
        public string BackendId => "omp";
        string? IConversationSession.ConversationId => ConversationId;
        string? IConversationSession.ConversationFile => ConversationFile;
        string IConversationSession.ConversationRoot => ConversationRoot;
        bool IConversationSession.ConversationRunning => ConversationRunning;
        bool IConversationSession.ConversationWaiting => ConversationWaiting;
        event EventHandler? IConversationSession.ConversationStateChanged
        {
            add => ConversationStateChanged += value;
            remove => ConversationStateChanged -= value;
        }
        void IConversationSession.InitializeConversation(string root, string? id, string? file, ConversationPartnerPayload? partner) => InitializeConversation(root, id, file, partner);
        Task IConversationSession.WaitForConversationAsync(CancellationToken token) => WaitForConversationAsync(token);
        void IConversationSession.FreezeWorkspace() => FreezeWorkspace();
        void IConversationSession.UnfreezeWorkspace() => UnfreezeWorkspace();
        ConversationTabsStatePayload IConversationSession.ConversationTabs => ConversationTabs;
        ConversationPlanPayload? IConversationSession.ConversationPlan => ConversationPlan;
        Task IConversationSession.RunHistoryCommandAsync(JObject args, Func<string, string, bool>? ownedElsewhere) => RunHistoryCommandAsync(args, ownedElsewhere);
        Task IConversationSession.RunPlanCommandAsync(UiMessage message, Action<UiMessage> respond) => RunPlanCommandAsync(message, respond);
    }
}
