using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Pomp.VisualStudio.Interop
{
    /// <summary>Small Win32 surface used for focus detection and loader diagnostics.</summary>
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Active;
            public IntPtr Focus;
            public IntPtr Capture;
            public IntPtr MenuOwner;
            public IntPtr MoveSize;
            public IntPtr CaretWindow;
            public Rect Caret;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        private const int SwHide = 0;
        private const int SwShowNoActivate = 8;

        /// <summary>Hides or shows a native child window without activating it (no-op for a null handle).</summary>
        internal static void SetChildWindowVisible(IntPtr window, bool visible)
        {
            if (window != IntPtr.Zero)
            {
                ShowWindow(window, visible ? SwShowNoActivate : SwHide);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder fileName, uint size);

        /// <summary>
        /// True when the window that currently has keyboard focus (in whichever process owns it, WebView2 renders in a
        /// separate browser process) is <paramref name="container"/> or one of its descendants.
        /// </summary>
        public static bool IsFocusWithin(IntPtr container)
        {
            if (container == IntPtr.Zero)
            {
                return false;
            }

            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };

            // Thread id 0 = the foreground thread (the one that owns the focus window).
            if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero)
            {
                return false;
            }

            return info.Focus == container || IsChild(container, info.Focus);
        }

        /// <summary>Full path of a module already loaded in this process, or null.</summary>
        public static string? GetLoadedModulePath(string moduleName)
        {
            var handle = GetModuleHandleW(moduleName);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new StringBuilder(1024);
            return GetModuleFileNameW(handle, buffer, (uint)buffer.Capacity) > 0 ? buffer.ToString() : moduleName;
        }
    }
}
