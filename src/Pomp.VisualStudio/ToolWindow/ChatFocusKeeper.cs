using System;
using System.Windows.Interop;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.Diagnostics;
using Pomp.VisualStudio.Adapters;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Interop;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>
    /// Puts the keyboard focus back into the chat when a build or a run (F5, Ctrl+F5) that started while the focus was in the chat has
    /// ended and Visual Studio left it elsewhere: leaving debugging, Visual Studio activates the code editor, and the started program
    /// takes the foreground meanwhile. One who kept typing would type into the code.
    /// <para>
    /// A session starts at the first <c>OnBuildBegin</c> or <c>OnEnterRunMode</c> in design mode and ends when design mode returns
    /// (after a run) or shortly after <c>OnBuildDone</c> (a build, or Ctrl+F5, which runs without the debugger). The focus goes back
    /// once Visual Studio is the active application again with no modal dialog open (the program's window, or the "continue with the
    /// last successful build?" question, may be in front), after Visual Studio has settled its own window activation; for a few seconds
    /// after that, a window Visual Studio activates itself is undone as well.
    /// </para>
    /// <para>
    /// Never against the user: the focus stays where it is when the user moved it during the session. That is a window activated by a
    /// click (a mouse button held), or, outside the activations Visual Studio makes itself around a mode change, by a Ctrl chord
    /// (Ctrl+Tab) or by a Visual Studio key pressed in the chat (F7); the chat's own focus toggle; or break mode (the user debugs in the
    /// editor). Clicking back into the chat re-arms the restore.
    /// </para>
    /// </summary>
    internal sealed class ChatFocusKeeper : IDisposable
    {
        /// <summary>Window activations this long after a session transition are Visual Studio's own (window layout switch, editor activation).</summary>
        private static readonly TimeSpan TransitionGrace = TimeSpan.FromSeconds(2);

        /// <summary>A build that does not lead into a run within this time ended the session (Ctrl+F5 and a plain build stay in design mode).</summary>
        private static readonly TimeSpan BuildEndDelay = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// The focus moves once Visual Studio has been active and modeless, and has activated no other window itself, for this long:
        /// its own activation on leaving debugging comes first. Short, since keys typed meanwhile go to the editor.
        /// </summary>
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

        /// <summary>After the focus went back, a later activation of another window by Visual Studio is undone too, for this long.</summary>
        private static readonly TimeSpan Watch = TimeSpan.FromSeconds(3);

        /// <summary>A program still running after this long (Ctrl+F5) is no longer part of the user's flow: the restore is dropped.</summary>
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

        /// <summary>A window activated this soon after a Visual Studio key in the chat was activated by that key's command.</summary>
        private static readonly TimeSpan ChatKeyEffect = TimeSpan.FromSeconds(1);

        /// <summary>A build or run that begins this soon after a Visual Studio key in the chat was started by that key.</summary>
        private static readonly TimeSpan ChatKeyStart = TimeSpan.FromSeconds(10);

        private readonly PompServices services;
        private readonly PompChatControl chat;
        private readonly VsAdapterContext context;
        private readonly DispatcherTimer timer;
        private readonly Guid chatWindowKind = new Guid(PompGuids.ToolWindowGuidString);
        private DebuggerEvents? debuggerEvents;
        private BuildEvents? buildEvents;
        private WindowEvents? windowEvents;

        private bool active;
        private bool ran;
        private bool restoreChat;
        private bool userMoved;
        private bool waiting;
        private int buildGeneration;
        private DateTime graceUntilUtc;
        private DateTime waitDeadlineUtc;
        private DateTime readySinceUtc;
        private DateTime watchUntilUtc;
        private DateTime foreignActivationUtc;
        private DateTime restoredUtc;
        private DateTime chatKeyUtc = DateTime.MinValue;
        private bool disposed;

        public ChatFocusKeeper(PompServices services, PompChatControl chat)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.services = services;
            this.chat = chat;
            context = new VsAdapterContext(services.Package, services.JoinableTaskFactory, services.Logger);
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += OnTick;
            try
            {
                var events = context.Dte.Events;
                // The event objects are COM sinks: kept in fields for as long as the handlers are attached.
                debuggerEvents = events.DebuggerEvents;
                buildEvents = events.BuildEvents;
                windowEvents = events.WindowEvents;
                debuggerEvents.OnEnterRunMode += OnEnterRunMode;
                debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
                debuggerEvents.OnEnterDesignMode += OnEnterDesignMode;
                buildEvents.OnBuildBegin += OnBuildBegin;
                buildEvents.OnBuildDone += OnBuildDone;
                windowEvents.WindowActivated += OnWindowActivated;
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Subscribing to the build and debugger events for the chat focus", ex);
            }
        }

        /// <summary>The user sent the focus from the chat to the editor themselves (the chat's focus toggle): it stays there.</summary>
        public void UserLeftChat()
        {
            if (active || waiting) userMoved = true;
        }

        /// <summary>
        /// A Visual Studio key was pressed in the chat (<c>ui/vsKey</c>): a window its command activates at once (F7 shows the code) is the
        /// user's choice, unless it is one Visual Studio activates around a mode change (Shift+F5 stops debugging and shows the editor).
        /// </summary>
        public void NoteChatKey() => chatKeyUtc = DateTime.UtcNow;

        private void OnBuildBegin(vsBuildScope scope, vsBuildAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!active && InDesignMode()) Begin();
        }

        private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
        {
            if (!active) return;
            Grace();
            var generation = ++buildGeneration;
            var delay = new DispatcherTimer(DispatcherPriority.Background) { Interval = BuildEndDelay };
            delay.Tick += (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                delay.Stop();
                // F5 enters run mode after its build: that session ends with design mode instead.
                if (!disposed && active && !ran && generation == buildGeneration && InDesignMode()) End();
            };
            delay.Start();
        }

        private void OnEnterRunMode(dbgEventReason reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!active) Begin();
            ran = true;
            Grace();
        }

        private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
        {
            // Visual Studio shows the code where the program stopped and the user debugs there.
            if (active) userMoved = true;
        }

        private void OnEnterDesignMode(dbgEventReason reason)
        {
            if (!active) return;
            Grace();
            End();
        }

        private void OnWindowActivated(Window gotFocus, Window lostFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!active && !waiting) return;
            // A click counts only on Visual Studio's own windows: a button of a dialog in front ("build?", "run the last successful
            // build?") is no choice of where to type next, and Visual Studio activates its Output or Error List right after it.
            var main = System.Windows.Application.Current?.MainWindow;
            var clicked = NativeMethods.IsMouseButtonDown() && main != null && NativeMethods.IsCursorOver(new WindowInteropHelper(main).Handle);
            if (IsChat(gotFocus))
            {
                // The user clicked back into the chat: that is where the focus belongs at the end.
                if (clicked)
                {
                    restoreChat = true;
                    userMoved = false;
                }

                return;
            }

            // Ctrl is still held for a moment after Ctrl+F5 or Ctrl+Shift+B, while Visual Studio activates its own windows.
            var now = DateTime.UtcNow;
            if (clicked || (now >= graceUntilUtc && (NativeMethods.IsCtrlDown() || now - chatKeyUtc < ChatKeyEffect)))
            {
                if (!userMoved) services.Logger.Debug("Chat focus: the user moved the focus to " + WindowCaption(gotFocus) + (clicked ? " (click)." : " (key)."));
                userMoved = true;
            }
            else if (waiting) foreignActivationUtc = now;
        }

        private static string WindowCaption(Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return window?.Caption ?? "a window"; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is InvalidOperationException) { return "a window"; }
        }

        private void Begin()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // A restore still waiting (a build whose run started late) carries over into the new session.
            var carried = waiting && restoreChat && !userMoved;
            StopWaiting();
            active = true;
            ran = false;
            userMoved = false;
            // A key pressed in the chat (F5, Ctrl+Shift+B) started this: Visual Studio may already have shown the Output window and
            // taken the focus by the time the build begins.
            restoreChat = carried || chat.HasKeyboardFocus || DateTime.UtcNow - chatKeyUtc < ChatKeyStart;
            services.Logger.Debug("Chat focus: a build or run started (chat had the focus: " + restoreChat + ").");
            Grace();
        }

        private void End()
        {
            active = false;
            services.Logger.Debug("Chat focus: the build or run ended (restore: " + restoreChat + ", user moved the focus: " + userMoved + ").");
            if (!restoreChat || userMoved) return;
            waiting = true;
            waitDeadlineUtc = DateTime.UtcNow + MaxWait;
            readySinceUtc = DateTime.MaxValue;
            foreignActivationUtc = DateTime.MinValue;
            restoredUtc = DateTime.MinValue;
            timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var now = DateTime.UtcNow;
            if (disposed || userMoved || !restoreChat || now > waitDeadlineUtc)
            {
                StopWaiting();
                return;
            }

            var main = System.Windows.Application.Current?.MainWindow;
            if (main == null || !NativeMethods.IsActiveAndModeless(new WindowInteropHelper(main).Handle))
            {
                readySinceUtc = DateTime.MaxValue;
                return;
            }

            if (readySinceUtc == DateTime.MaxValue)
            {
                readySinceUtc = now;
                watchUntilUtc = now + Watch;
            }

            if (now - readySinceUtc < Settle || now - foreignActivationUtc < Settle) return;
            // Once, and again only after Visual Studio activated another window since.
            if (restoredUtc == DateTime.MinValue || restoredUtc < foreignActivationUtc)
            {
                restoredUtc = now;
                if (!chat.HasKeyboardFocus) Restore();
            }

            if (now > watchUntilUtc) StopWaiting();
        }

        private void Restore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // The shell's active pane as well as the WebView focus: otherwise the shell gives the focus back to the editor tab it activated.
                if (services.Package.FindToolWindow(typeof(PompChatToolWindow), 0, false)?.Frame is IVsWindowFrame frame) ErrorHandler.ThrowOnFailure(frame.Show());
                chat.RestoreFocus();
                services.Logger.Info("The build or run ended: the keyboard focus is back in the chat, where it was when it started.");
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Returning the focus to the chat after a build or run", ex);
            }
        }

        private void StopWaiting()
        {
            waiting = false;
            timer.Stop();
        }

        private void Grace() => graceUntilUtc = DateTime.UtcNow + TransitionGrace;

        private bool InDesignMode()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return context.Dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsChat(Window? window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return window != null && Guid.TryParse(window.ObjectKind, out var kind) && kind == chatWindowKind;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            disposed = true;
            StopWaiting();
            timer.Tick -= OnTick;
            try
            {
                if (debuggerEvents != null)
                {
                    debuggerEvents.OnEnterRunMode -= OnEnterRunMode;
                    debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
                    debuggerEvents.OnEnterDesignMode -= OnEnterDesignMode;
                }

                if (buildEvents != null)
                {
                    buildEvents.OnBuildBegin -= OnBuildBegin;
                    buildEvents.OnBuildDone -= OnBuildDone;
                }

                if (windowEvents != null) windowEvents.WindowActivated -= OnWindowActivated;
            }
            catch (Exception)
            {
                // IDE shutdown.
            }

            debuggerEvents = null;
            buildEvents = null;
            windowEvents = null;
        }
    }
}
