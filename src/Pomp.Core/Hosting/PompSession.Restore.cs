using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        /// <summary>Most messages put back into the chat after a session switch (the newest ones).</summary>
        internal const int RestoredMessageLimit = 120;

        private const int RestoredTextLimit = 20000;

        /// <summary>
        /// After switching to another omp session the chat would otherwise be empty: its user and assistant texts (the user's own
        /// words, without POMP's context blocks; no thinking and no tool calls) are posted as ordinary, already finished chat
        /// turns, so they also survive a page reload through the transcript replay.
        /// </summary>
        private async Task RestoreTranscriptAsync(OmpConnection connection, CancellationToken token)
        {
            var response = await connection.SendCommandAsync(new JObject { ["type"] = "get_messages" }, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            if (!response.Success || !(response.Data?["messages"] is JArray all))
            {
                PostNotice("warning", SetupText.Get("이전 대화 내용을 불러오지 못했어요.", "The earlier conversation could not be shown here."));
                return;
            }

            string root;
            lock (_stateLock) root = _workingDirectory;
            var shown = new List<(string Role, string Text, ChatEditorContext? EditorContext, ConversationPartnerPayload? From, ChatAdvisorNotesPayload? Notes)>();
            foreach (var message in all.OfType<JObject>())
            {
                var role = message.GetString("role");
                if (role == "custom" && message.GetString("customType") == "advisor" && !(message["display"]?.Type == JTokenType.Boolean && !(bool)message["display"]!))
                {
                    // The notes advisors gave, where they came in the conversation.
                    var notes = AdvisorNotesPayload(message);
                    if (notes.Notes.Count > 0) shown.Add(("advisor", string.Empty, null, null, notes));
                    continue;
                }

                if (role != "user" && role != "assistant")
                {
                    continue;
                }

                var text = MessageText(message["content"]);
                var editorContext = role == "user" ? PromptDisplay.EditorContext(text, root) : null;
                // The agent or team tab a message to another target was sent from, so the restored chat keeps it there.
                var from = role == "user" ? PromptDisplay.Origin(text) : null;
                if (role == "user")
                {
                    text = PromptDisplay.UserText(text, keepRoutingBrief: true);
                }

                if (text.Length > 0)
                {
                    shown.Add((role!, text.Length > RestoredTextLimit ? text.Substring(0, RestoredTextLimit) : text, editorContext, from, null));
                }
            }

            if (shown.Count == 0)
            {
                return;
            }

            var skipped = Math.Max(0, shown.Count - RestoredMessageLimit);
            token.ThrowIfCancellationRequested();
            PostNotice("info", skipped > 0
                ? SetupText.Get("이전 대화의 최근 메시지 " + RestoredMessageLimit + "개를 다시 표시했어요 (텍스트만).", "Showing the last " + RestoredMessageLimit + " messages of this conversation (text only).")
                : SetupText.Get("이전 대화 내용을 다시 표시했어요 (텍스트만).", "Showing this conversation again (text only)."));

            string? openPrompt = null;
            var index = 0;
            foreach (var (role, text, editorContext, from, notes) in shown.Skip(skipped))
            {
                index++;
                if (notes != null)
                {
                    _ui.Post(UiMessageTypes.ChatAdvisorNotes, notes);
                    continue;
                }

                var id = "restored-" + index.ToString(CultureInfo.InvariantCulture);
                if (role == "user")
                {
                    if (openPrompt != null)
                    {
                        _ui.Post(UiMessageTypes.ChatTurnEnd, new ChatTurnEndPayload { PromptId = openPrompt, Status = "completed" });
                    }

                    openPrompt = id;
                    _ui.Post(UiMessageTypes.ChatTurnStarted, new ChatTurnStartedPayload { PromptId = id, Text = text, EditorContext = editorContext, From = from });
                    continue;
                }

                if (openPrompt == null)
                {
                    // An answer before any question (a trimmed history): give it a turn of its own.
                    openPrompt = id + "-turn";
                    _ui.Post(UiMessageTypes.ChatTurnStarted, new ChatTurnStartedPayload { PromptId = openPrompt, Text = string.Empty });
                }

                _ui.Post(UiMessageTypes.ChatAssistantStart, new ChatAssistantStartPayload { MessageId = id });
                _ui.Post(UiMessageTypes.ChatAssistantDelta, new ChatAssistantDeltaPayload { MessageId = id, Kind = "text", Delta = text });
                _ui.Post(UiMessageTypes.ChatAssistantEnd, new ChatAssistantEndPayload { MessageId = id, StopReason = "stop" });
            }

            if (openPrompt != null)
            {
                _ui.Post(UiMessageTypes.ChatTurnEnd, new ChatTurnEndPayload { PromptId = openPrompt, Status = "completed" });
            }
        }

        private static string MessageText(JToken? content)
        {
            if (content?.Type == JTokenType.String)
            {
                return ((string?)content ?? string.Empty).Trim();
            }

            // Narration (thinking the model addressed to the user) restores as answer text, as it showed live.
            return string.Join("\n\n", (content as JArray ?? new JArray()).OfType<JObject>()
                .Select(block => (block.AnswerText() ?? string.Empty).Trim())
                .Where(text => text.Length > 0));
        }
    }
}
