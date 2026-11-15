using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>A backend-owned conversation, independent of any provider's transport.</summary>
    public interface IConversationSession : IPompSession, IEditorSessionContextSource
    {
        string BackendId { get; }
        string? ConversationId { get; }
        string? ConversationFile { get; }
        string ConversationRoot { get; }
        bool ConversationRunning { get; }
        /// <summary>An approval or question of this conversation waits for the user.</summary>
        bool ConversationWaiting { get; }
        event EventHandler? ConversationStateChanged;
        void InitializeConversation(string root, string? id, string? file, ConversationPartnerPayload? partner);
        Task WaitForConversationAsync(CancellationToken token);
        void FreezeWorkspace();
        void UnfreezeWorkspace();
        ConversationTabsStatePayload ConversationTabs { get; }
        ConversationPlanPayload? ConversationPlan { get; }
        Task RunHistoryCommandAsync(JObject args, Func<string, string, bool>? ownedElsewhere = null);
        Task RunPlanCommandAsync(UiMessage message, Action<UiMessage> respond);
        void PrefillComposer(ComposerPrefillPayload prefill);
        Task<int> PasteIntoBasketAsync(IReadOnlyList<string> paths, byte[]? png, CancellationToken token);
    }
}
