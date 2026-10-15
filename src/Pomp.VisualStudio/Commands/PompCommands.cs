using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Hosting;
using Pomp.VisualStudio.Composition;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Commands
{
    /// <summary>Registers the Pomp menu commands (see Pomp.vsct) with Visual Studio.</summary>
    internal static class PompCommands
    {
        public static async Task InitializeAsync(PompPackage package, PompServices services, CancellationToken cancellationToken)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (!(await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService))
            {
                services.Logger.Error("IMenuCommandService is not available; Pomp commands are not registered.");
                return;
            }

            var actions = services.Commands;
            Register(commandService, services, PompCommandIds.ShowChatView, "Show chat", () => actions.ShowChatAsync(focus: true));
            Register(commandService, services, PompCommandIds.ShowChatTools, "Show chat", () => actions.ShowChatAsync(focus: true), ("POMP 열기", "Open POMP"));
            Register(commandService, services, PompCommandIds.NewSession, "New session", () => actions.NewSessionAsync(), ("새 대화", "New Conversation"));
            Register(commandService, services, PompCommandIds.InlineEdit, "Ask POMP", () => actions.ShowInlineEditAsync(), ("POMP에게 질문", "Ask POMP"));
            Register(commandService, services, PompCommandIds.ToggleFocus, "Toggle focus", () => actions.ToggleFocusAsync(), ("에디터 ↔ 채팅 포커스 전환", "Toggle Editor ↔ Chat Focus"));
            Register(commandService, services, PompCommandIds.ExplainCode, "Explain code", () => actions.ExplainCodeAsync(), ("이 코드 설명", "Explain This Code"));
            Register(commandService, services, PompCommandIds.ReviewCode, "Review code", () => actions.ReviewCodeAsync(), ("이 코드 리뷰", "Review This Code"));
            Register(commandService, services, PompCommandIds.FixCode, "Fix code", () => actions.FixCodeAsync(), ("이 코드 고치기", "Fix This Code"));
            Register(commandService, services, PompCommandIds.WriteTests, "Write tests", () => actions.WriteTestsAsync(), ("테스트 작성", "Write Tests"));
            Register(commandService, services, PompCommandIds.AddFileToContext, "Add file to context", () => actions.AddFileToContextAsync(), ("파일을 채팅 컨텍스트에 추가", "Add File to Chat Context"));
            // The selected lines open the chat window's knowledge form, where the knowledge is picked by name or named.
            Register(commandService, services, PompCommandIds.KnowledgeTag, "Add to knowledge", () => actions.AddToKnowledgeAsync(),
                ("지식에 추가", "Add to Knowledge"), isEnabled: () => services.KnowledgeEditor != null);
            Register(commandService, services, PompCommandIds.ToggleInlineCompletions, "Toggle inline completions", () => actions.ToggleInlineCompletionsAsync(),
                ("인라인 자동완성", "Inline Completions"), () => services.Settings.InlineCompletionsEnabled);
            Register(commandService, services, PompCommandIds.ToggleCodeCaptions, "Toggle code captions", () => actions.ToggleCodeCaptionsAsync(),
                ("코드 캡션", "Code Captions"), () => services.Settings.CodeCaptionsEnabled);
            // The command keys come from Tools > Options > Pomp > Shortcuts (applied now and after every options apply).
            VsKeyBindings.Install(package, services);
            RegisterSendToTargets(commandService, services, actions);
        }

        /// <summary>
        /// POMP > To Agent or Team in the code editor: one dynamic item per usable agent role and team of the workspace (the roster is read
        /// when the first item is queried, i.e. each time the submenu opens); choosing one fills the chat input with that send target and the
        /// editor context. "(none)" when the workspace has neither.
        /// </summary>
        private static void RegisterSendToTargets(OleMenuCommandService commandService, PompServices services, PompCommandActions actions)
        {
            var menu = new OleMenuCommand((sender, args) => { }, new CommandID(PompGuids.CommandSet, PompCommandIds.SendToTargetMenu));
            menu.BeforeQueryStatus += (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                menu.Text = PompLanguage.Pick("에이전트·팀에게", "To Agent or Team");
            };
            commandService.AddCommand(menu);

            var targets = new System.Collections.Generic.List<EditorSendTarget>();
            DynamicItemCommand? command = null;
            int Index() => command!.MatchedCommandId == 0 ? 0 : command.MatchedCommandId - PompCommandIds.SendToTargetStart;
            // The menu shows at most MenuItems entries; past that the last one is "show all" (the chat's target picker has every entry).
            bool ShowAll(int index) => targets.Count > MenuItems && index == MenuItems - 1;
            command = new DynamicItemCommand(PompCommandIds.SendToTargetStart, () => Math.Max(1, Math.Min(targets.Count, MenuItems)), (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var index = Index();
                command!.MatchedCommandId = 0;
                var showAll = ShowAll(index);
                var target = !showAll && index < targets.Count ? targets[index] : null;
                if (target == null && !showAll) return;
                _ = services.JoinableTaskFactory.RunAsync(async () =>
                {
                    try { if (showAll) await actions.PickTargetAsync(); else await actions.SendToTargetAsync(target!.Kind, target.Id); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { services.Logger.Error("The 'Send to agent or team' command failed.", ex); }
                });
            });
            command.BeforeQueryStatus += (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var index = Index();
                if (index == 0)
                {
                    targets.Clear();
                    try { if (services.Session is PompSession session) targets.AddRange(session.ListEditorSendTargets()); }
                    catch (Exception ex) { services.Logger.Debug("Reading the agent roster for the editor menu failed: " + ex.Message); }
                }
                var showAll = ShowAll(index);
                var target = !showAll && index < targets.Count ? targets[index] : null;
                command!.Visible = index == 0 || target != null || showAll;
                command.Enabled = target != null || showAll;
                command.Text = showAll ? PompLanguage.Pick("모두 보기", "Show all") : MenuText(target != null ? target.Title : PompLanguage.Pick("(없음)", "(none)"));
                command.MatchedCommandId = 0;
            };
            commandService.AddCommand(command);
        }

        /// <summary>
        /// The most entries the editor's target submenu shows, so it never needs scrolling (2026-10-15, user decision; VS itself draws at most
        /// 100 dynamic items). A larger roster shows the first ones and "show all", which opens the chat's send-target picker.
        /// </summary>
        private const int MenuItems = 15;

        /// <summary>A roster title as menu text: control characters become spaces and '&amp;' is doubled so VS does not read it as a mnemonic.</summary>
        private static string MenuText(string title)
        {
            var chars = new char[title.Length * 2];
            var n = 0;
            foreach (var c in title)
            {
                if (c == '&') { chars[n++] = '&'; chars[n++] = '&'; }
                else chars[n++] = char.IsControl(c) ? ' ' : c;
            }
            return new string(chars, 0, n);
        }

        /// <summary>
        /// A DynamicItemStart command: answers for as many ids from <c>start</c> on as <c>count</c> says (the current entries, no count limit;
        /// VS stops asking at the first id nobody answers) and remembers which one VS asked about. <c>start</c> is above every other id of the
        /// command set, so the range never claims one of them.
        /// </summary>
        private sealed class DynamicItemCommand : OleMenuCommand
        {
            private readonly int start;
            private readonly Func<int> count;

            public DynamicItemCommand(int start, Func<int> count, EventHandler invoke) : base(invoke, new CommandID(PompGuids.CommandSet, start))
            {
                this.start = start;
                this.count = count;
            }

            public override bool DynamicItemMatch(int cmdId)
            {
                // The first id always answers: its status reads the roster, which sets how many follow.
                if (cmdId < start || (cmdId > start && cmdId - start >= count())) return false;
                MatchedCommandId = cmdId;
                return true;
            }
        }

        /// <param name="text">The menu text in Korean and English (POMP language option); null keeps the .vsct text.</param>
        /// <param name="isChecked">For an on/off command: whether it is on (shown as a check mark).</param>
        /// <param name="isEnabled">For a command that cannot always run: whether it can now (grayed out otherwise).</param>
        private static void Register(OleMenuCommandService commandService, PompServices services, int commandId, string name, Func<Task> action,
            (string Korean, string English)? text = null, Func<bool>? isChecked = null, Func<bool>? isEnabled = null)
        {
            var id = new CommandID(PompGuids.CommandSet, commandId);
            var command = new OleMenuCommand(
                (sender, args) =>
                {
                    _ = services.JoinableTaskFactory.RunAsync(async () =>
                    {
                        try
                        {
                            await action();
                        }
                        catch (OperationCanceledException)
                        {
                            // Package disposed while the command ran.
                        }
                        catch (Exception ex)
                        {
                            services.Logger.Error("The '" + name + "' command failed.", ex);
                        }
                    });
                },
                id);
            if (text != null || isChecked != null || isEnabled != null)
            {
                command.BeforeQueryStatus += (sender, args) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (text != null) command.Text = PompLanguage.Pick(text.Value.Korean, text.Value.English);
                    if (isChecked != null) command.Checked = isChecked();
                    if (isEnabled != null) command.Enabled = isEnabled();
                };
            }

            commandService.AddCommand(command);
        }
    }
}
