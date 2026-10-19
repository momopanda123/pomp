using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Ui;
using Pomp.VisualStudio.Composition;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Commands
{
    /// <summary>
    /// Applies the Visual Studio command shortcuts of Tools > Options > Pomp > Shortcuts to the IDE's key bindings: at start-up and
    /// after every options apply. The VSCT defines no default bindings any more, so the options page is the one place the keys live
    /// (the same values show in Tools > Options > Environment > Keyboard: search for "Pomp.").
    /// </summary>
    internal sealed class VsKeyBindings
    {
        private static readonly (int Id, Func<EditorShortcutsPayload, string> Chord, bool Global)[] Map =
        {
            (PompCommandIds.OpenPomp, s => s.OpenPomp, true),
            (PompCommandIds.NewSession, s => s.NewConversation, true),
            (PompCommandIds.ToggleFocus, s => s.ToggleFocus, true),
            (PompCommandIds.ToggleLiveFollow, s => s.LiveFollow, true),
            (PompCommandIds.ToggleLiveBookmarks, s => s.LiveBookmarks, true),
            (PompCommandIds.ExplainCode, s => s.ExplainCode, false),
            (PompCommandIds.ReviewCode, s => s.ReviewCode, false),
            (PompCommandIds.FixCode, s => s.FixCode, false),
            (PompCommandIds.WriteTests, s => s.WriteTests, false),
            (PompCommandIds.AddFileToContext, s => s.AddFileToContext, false),
            (PompCommandIds.InlineEdit, s => s.InlineEdit, false),
            (PompCommandIds.KnowledgeTag, s => s.KnowledgeTag, false),
        };

        private readonly PompPackage package;
        private readonly PompServices services;
        private int applying;
        // The first start of a new VS instance (or the first after installing) refuses some bindings for that whole session; every start
        // applies them again, so the next start sets them. Refused bindings are tried again a few times, 20 s apart, and reported once.
        private const int MaxAttempts = 4;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(20);

        private VsKeyBindings(PompPackage package, PompServices services)
        {
            this.package = package;
            this.services = services;
        }

        /// <summary>Applies the current options now and again after every options apply.</summary>
        public static VsKeyBindings Install(PompPackage package, PompServices services)
        {
            var bindings = new VsKeyBindings(package, services);
            services.Settings.Changed += (sender, args) => bindings.Schedule();
            bindings.Schedule();
            return bindings;
        }

        private void Schedule() => Schedule(1);

        private void Schedule(int attempt)
        {
            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    if (attempt > 1) await Task.Delay(RetryDelay, package.DisposalToken);
                    if (await ApplyAsync(attempt == MaxAttempts, package.DisposalToken) && attempt < MaxAttempts) Schedule(attempt + 1);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { services.Logger.Warn("Applying the POMP key bindings failed: " + ex.Message); }
            });
        }

        /// <summary>True when some binding was refused (and should be tried again).</summary>
        private async Task<bool> ApplyAsync(bool lastAttempt, CancellationToken token)
        {
            if (Interlocked.Exchange(ref applying, 1) == 1) return false;
            try
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!(await package.GetServiceAsync(typeof(SDTE)) is DTE2 dte)) return false;
                var shortcuts = (services.Settings as IEditorShortcutSettings)?.EditorShortcuts ?? new EditorShortcutsPayload();
                var scopes = ScopeNames(dte);
                var changed = 0; var refused = new List<string>(); string? reason = null;
                foreach (var entry in Map)
                {
                    Command command;
                    try { command = dte.Commands.Item(PompGuids.CommandSet.ToString("B"), entry.Id); }
                    catch (Exception ex) { services.Logger.Debug("POMP command 0x" + entry.Id.ToString("X") + " has no binding slot: " + ex.Message); continue; }
                    var wanted = UiShortcutChords.VsBinding(entry.Chord(shortcuts), entry.Global ? scopes.Global : scopes.TextEditor);
                    var desired = wanted == null ? new object[0] : new object[] { wanted };
                    var current = (command.Bindings as object[] ?? new object[0]).OfType<string>().ToArray();
                    if (current.Length == desired.Length && current.Zip(desired.OfType<string>(), string.Equals).All(same => same)) continue;
                    try { command.Bindings = desired; changed++; }
                    catch (Exception ex)
                    {
                        refused.Add(wanted ?? command.Name);
                        reason ??= ex.Message;
                    }
                }
                if (changed > 0) services.Logger.Info("POMP key bindings applied (" + changed + " changed).");
                if (refused.Count > 0)
                {
                    if (!lastAttempt) services.Logger.Info("POMP key bindings: " + refused.Count + " refused, trying again in " + (int)RetryDelay.TotalSeconds + " s.");
                    else services.Logger.Warn("Visual Studio refused " + refused.Count + " POMP key bindings this session (" + string.Join(", ", refused)
                        + "): " + reason + " They are applied again at the next start.");
                }
                return refused.Count > 0;
            }
            finally { Volatile.Write(ref applying, 0); }
        }

        /// <summary>
        /// Key binding strings carry the scope in the IDE's language ("Global::", "전역::"): the names are read from commands whose
        /// default keys are global (Solution Explorer, Save All, Undo) and text-editor (Format Document, Comment Selection, Line Delete,
        /// Line Cut). Several per scope, since the user can remove any one key; the English names are the fallback, which only an
        /// English IDE accepts.
        /// </summary>
        internal static (string Global, string TextEditor) ScopeNames(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return (ScopeOf(dte, new[] { "View.SolutionExplorer", "File.SaveAll", "Edit.Undo" }, "Global"),
                ScopeOf(dte, new[] { "Edit.FormatDocument", "Edit.CommentSelection", "Edit.LineDelete", "Edit.LineCut" }, "Text Editor"));
        }

        private static string ScopeOf(DTE2 dte, string[] commandNames, string fallback)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var commandName in commandNames)
            {
                try
                {
                    var bindings = (dte.Commands.Item(commandName).Bindings as object[] ?? new object[0]).OfType<string>();
                    foreach (var binding in bindings)
                    {
                        var separator = binding.IndexOf("::", StringComparison.Ordinal);
                        if (separator > 0) return binding.Substring(0, separator);
                    }
                }
                catch (Exception) { }
            }
            return fallback;
        }
    }
}
