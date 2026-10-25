using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Pomp.Diagnostics;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>
    /// Brings the mouse pointer back over Visual Studio's own (WPF) surfaces, such as the code editor. With Windows' "Hide pointer while
    /// typing" on, WebView2 runtimes from 152 hide the pointer while one types in the chat and show it again only when the mouse moves
    /// over a WebView (MicrosoftEdge/WebView2Feedback #5687, #5708). The chat's window shares Visual Studio's input queue, so the pointer
    /// stayed hidden over the editor until the mouse went back over the chat. On a mouse move over WPF content a pointer hidden by that
    /// display count is shown again; a pointer Windows hides by itself is left to Windows.
    /// </summary>
    internal sealed class CursorVisibilityGuard : IDisposable
    {
        private const int CursorShowing = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Cursor;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorInfo(ref CursorInfo info);

        [DllImport("user32.dll")]
        private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

        private readonly IPompLogger logger;
        private bool installed;
        private bool reported;

        public CursorVisibilityGuard(IPompLogger logger)
        {
            this.logger = logger;
        }

        /// <summary>Hooks WPF input. Call on the UI thread (the display count belongs to the calling thread's input queue).</summary>
        public void Install()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (installed)
            {
                return;
            }

            installed = true;
            InputManager.Current.PostProcessInput += OnPostProcessInput;
        }

        private void OnPostProcessInput(object sender, ProcessInputEventArgs e)
        {
            if (e.StagingItem.Input.RoutedEvent != Mouse.MouseMoveEvent)
            {
                return;
            }

            // Check every move. A pointer can cross onto WPF content and stop within 100 ms of the last
            // visible check; throttling then leaves it hidden until another mouse movement.
            try
            {
                var info = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
                if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) != 0)
                {
                    return;
                }

                // Hidden by the display count when it is below zero: raise it back to zero. A count already at zero or above means Windows
                // hid the pointer itself (it shows it again on its own), so the one step taken to find out is undone.
                var count = ShowCursor(true);
                if (count > 0)
                {
                    ShowCursor(false);
                    return;
                }

                for (var steps = 0; count < 0 && steps < 32; steps++)
                {
                    count = ShowCursor(true);
                }

                if (!reported)
                {
                    reported = true;
                    logger.Info("The mouse pointer was hidden over Visual Studio after typing in the chat (WebView2 hide-pointer-while-typing); it is shown again.");
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Could not check the mouse pointer: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (!installed)
            {
                return;
            }

            installed = false;
            // The hook belongs to the UI thread's InputManager; off that thread (Visual Studio shutting down) there is nothing to unhook.
            if (ThreadHelper.CheckAccess())
            {
                InputManager.Current.PostProcessInput -= OnPostProcessInput;
            }
        }
    }
}
