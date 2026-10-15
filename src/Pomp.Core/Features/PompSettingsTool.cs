using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// <c>pomp_settings</c>: read-only facts about POMP itself for the model — the keyboard shortcuts of the chat window and of the
    /// Visual Studio commands as they are set right now, the main settings and where each one is changed. A question about POMP's
    /// own keys or settings is then answered from the live values instead of "there is nothing". Never changes anything.
    /// </summary>
    internal sealed class PompSettingsTool : IHostTool
    {
        private const string OptionsPage = "Tools > Options > Pomp";
        private const string ShortcutsPage = OptionsPage + " > Shortcuts";
        private readonly IPompSettings settings;
        private readonly IPompHostEnvironment environment;

        public PompSettingsTool(IPompSettings settings, IPompHostEnvironment environment)
        {
            this.settings = settings;
            this.environment = environment;
        }

        public HostToolDefinition Definition { get; } = new HostToolDefinition
        {
            Name = "pomp_settings",
            Label = "POMP settings",
            Description = "Read POMP's own current configuration: the keyboard shortcuts of the chat window and of the Visual Studio editor commands "
                + "(all set in Tools > Options > Pomp > Shortcuts), the main settings (approval mode, model, thinking level, language, editor context "
                + "default, extension and network options) and where each one is changed. Use it whenever the user asks how to do something in POMP, "
                + "which key does what, or where a POMP setting lives. Read-only; it changes nothing.",
            Parameters = new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false },
        };

        public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;

        public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken) => Task.FromResult<PermissionRequest?>(null);

        public Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(HostToolResult.Text(Snapshot().ToString(Formatting.None)));

        /// <summary>The facts as the model receives them.</summary>
        public JObject Snapshot()
        {
            var chat = (settings as IUiShortcutSettings)?.Shortcuts ?? new UiShortcutsPayload();
            var editor = (settings as IEditorShortcutSettings)?.EditorShortcuts ?? new EditorShortcutsPayload();
            return new JObject
            {
                ["pompVersion"] = environment.HostVersion,
                ["shortcuts"] = new JObject
                {
                    ["where"] = ShortcutsPage + " (every key below; the Visual Studio command keys are applied to the IDE's key bindings when the options are applied)",
                    ["chatWindow"] = new JObject
                    {
                        ["send"] = chat.Send + (chat.Send == "Ctrl+Enter" ? " (Enter inserts a line break)" : " (Shift+Enter inserts a line break)"),
                        ["pickSendTarget"] = Key(chat.SendTargetPicker),
                        ["previousSendTarget"] = Key(chat.PreviousTarget),
                        ["nextSendTarget"] = Key(chat.NextTarget),
                        ["contextBasket"] = Key(chat.ContextBasket) + " (arrow keys move between its files, Space checks one)",
                        ["includeEditorContext"] = Key(chat.EditorContext),
                        ["permissionModePicker"] = Key(chat.PermissionMode),
                        ["allowRequest"] = Key(chat.Allow),
                        ["denyRequest"] = Key(chat.Deny),
                        ["permissionsView"] = Key(chat.PermissionsView),
                        ["toggleEditorChatFocus"] = Key(editor.ToggleFocus),
                    },
                    ["visualStudio"] = new JObject
                    {
                        ["explainThisCode"] = Key(editor.ExplainCode) + " (text editor)",
                        ["reviewThisCode"] = Key(editor.ReviewCode) + " (text editor)",
                        ["fixThisCode"] = Key(editor.FixCode) + " (text editor)",
                        ["writeTests"] = Key(editor.WriteTests) + " (text editor)",
                        ["addFileToChatContext"] = Key(editor.AddFileToContext) + " (text editor)",
                        ["inlineEdit"] = Key(editor.InlineEdit) + " (text editor)",
                        ["addToKnowledge"] = Key(editor.KnowledgeTag) + " (text editor)",
                        ["openPomp"] = Key(editor.OpenPomp) + " (global)",
                        ["newConversation"] = Key(editor.NewConversation) + " (global)",
                        ["toggleEditorChatFocus"] = Key(editor.ToggleFocus) + " (global)",
                    },
                    ["mouse"] = "The same commands are in the editor's right-click POMP menu and in Tools > POMP.",
                },
                ["settings"] = new JObject
                {
                    ["where"] = OptionsPage,
                    ["approvalMode"] = settings.ApprovalMode.ToCliValue() + " (also the first chip of the chat composer bar)",
                    ["model"] = settings.Model ?? "default (the model chip in the composer bar)",
                    ["thinkingLevel"] = settings.ThinkingLevel ?? "default (the level chip in the composer bar)",
                    ["includeEditorContextByDefault"] = settings.IncludeEditorContextByDefault,
                    ["composerPredictions"] = settings.ComposerPredictions,
                    ["loadPompExtensions"] = settings.LoadOmpExtensions,
                    ["loadDiscoveredExtensions"] = settings.LoadDiscoveredOmpExtensions,
                    ["blockNetworkTools"] = settings.BlockOmpNetworkTools,
                    ["verifyAfterEdits"] = settings.VerifyAfterEdits,
                    ["showAgentQuestionsInChat"] = settings.UseRpcUiMode,
                    ["agentExecutablePath"] = settings.OmpExecutablePath ?? "built in",
                },
                ["inTheChatWindow"] = new JObject
                {
                    ["accounts"] = "Accounts tab: sign in, API keys, editor model",
                    ["permissions"] = "Tools tab > Permissions: always-allowed entries and the project policy",
                    ["computerUse"] = "Tools tab > This conversation: the screen-control switch for this conversation",
                    ["rulesSkillsHooksWorkflowsMcp"] = "Tools tab",
                    ["agentsAndTeams"] = "Agents tab",
                },
                ["note"] = "These are the live values; the user may have changed them, so quote them rather than defaults. A chord is Ctrl/Alt/Shift plus one key; 'none' means no key is assigned.",
            };
        }

        private static string Key(string? chord) => string.IsNullOrEmpty(chord) ? "none" : chord!;
    }
}
