using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Pomp.VisualStudio.Interop
{
    /// <summary>Small Win32 surface used for focus detection, loader diagnostics and the native Bookmarks tree.</summary>
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

        // ---- Bookmarks window tree (a native SysTreeView32 owned by the UI thread) ----------------------------------

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, ref TreeViewItem item);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TreeViewItem
        {
            public uint Mask;
            public IntPtr Item;
            public uint State;
            public uint StateMask;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Text;
            public int TextMax;
            public int Image;
            public int SelectedImage;
            public int Children;
            public IntPtr Param;
        }

        private const int TvmGetNextItem = 0x110A, TvmSelectItem = 0x110B, TvmSetItemW = 0x113F;
        private const int TvgnRoot = 0, TvgnNext = 1, TvgnChild = 4, TvgnCaret = 9;
        private const uint TvifText = 1;

        /// <summary>
        /// The tree view of this (UI) thread that holds <paramref name="item"/>, or zero. Items are matched by walking each tree
        /// with its own handles, so a handle from another tree is never passed to a tree that does not own it.
        /// </summary>
        internal static IntPtr FindTreeViewContaining(IntPtr item)
        {
            if (item == IntPtr.Zero) return IntPtr.Zero;
            var found = IntPtr.Zero;
            var name = new StringBuilder(32);
            bool Visit(IntPtr window, IntPtr param)
            {
                name.Clear();
                if (GetClassNameW(window, name, name.Capacity) > 0 && name.ToString() == "SysTreeView32" && TreeContains(window, item)) found = window;
                return found == IntPtr.Zero;
            }
            EnumThreadWindows(GetCurrentThreadId(), (top, param) =>
            {
                Visit(top, param);
                if (found == IntPtr.Zero) EnumChildWindows(top, Visit, IntPtr.Zero);
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            return found;
        }

        internal static bool TreeContains(IntPtr tree, IntPtr item)
        {
            var pending = new Stack<IntPtr>();
            for (var node = SendMessageW(tree, TvmGetNextItem, (IntPtr)TvgnRoot, IntPtr.Zero); node != IntPtr.Zero; node = SendMessageW(tree, TvmGetNextItem, (IntPtr)TvgnNext, node))
                pending.Push(node);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (node == item) return true;
                for (var child = SendMessageW(tree, TvmGetNextItem, (IntPtr)TvgnChild, node); child != IntPtr.Zero; child = SendMessageW(tree, TvmGetNextItem, (IntPtr)TvgnNext, child))
                    pending.Push(child);
            }
            return false;
        }

        internal static IntPtr GetTreeSelection(IntPtr tree) => SendMessageW(tree, TvmGetNextItem, (IntPtr)TvgnCaret, IntPtr.Zero);

        /// <summary>Selects a tree item (selection only: the tree does not take keyboard focus).</summary>
        internal static void SelectTreeItem(IntPtr tree, IntPtr item) => SendMessageW(tree, TvmSelectItem, (IntPtr)TvgnCaret, item);

        internal static void SetTreeItemText(IntPtr tree, IntPtr item, string text)
        {
            var data = new TreeViewItem { Mask = TvifText, Item = item, Text = text };
            SendMessageW(tree, TvmSetItemW, IntPtr.Zero, ref data);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        /// <summary>The window with keyboard focus in this thread's input state (zero when none).</summary>
        internal static IntPtr GetFocusedWindow() => GetFocus();

        /// <summary>Gives keyboard focus back to <paramref name="window"/> when it still exists and lost it.</summary>
        internal static void RestoreFocus(IntPtr window)
        {
            if (window != IntPtr.Zero && GetFocus() != window && IsWindow(window)) SetFocus(window);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr window);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        /// <summary>
        /// True when this process owns the foreground window and <paramref name="mainWindow"/> takes input: Visual Studio is the
        /// active application and no modal dialog is open over it.
        /// </summary>
        internal static bool IsActiveAndModeless(IntPtr mainWindow)
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || mainWindow == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out var processId);
            return processId == GetCurrentProcessId() && IsWindowEnabled(mainWindow);
        }

        // VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2.
        private static readonly int[] MouseButtons = { 0x01, 0x02, 0x04, 0x05, 0x06 };

        /// <summary>True while a mouse button is physically held (a window activated now was clicked).</summary>
        internal static bool IsMouseButtonDown()
        {
            foreach (var key in MouseButtons)
            {
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            }

            return false;
        }

        /// <summary>True while Ctrl is physically held (Ctrl+Tab, Ctrl+Alt+L and similar window switches).</summary>
        internal static bool IsCtrlDown() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint { public int X; public int Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out CursorPoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(CursorPoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        /// <summary>
        /// True when the mouse pointer is over <paramref name="topLevel"/> itself (its root window), not over another top-level window
        /// such as a dialog in front of it: a click there was on Visual Studio's own windows.
        /// </summary>
        internal static bool IsCursorOver(IntPtr topLevel)
        {
            if (topLevel == IntPtr.Zero || !GetCursorPos(out var point)) return false;
            var window = WindowFromPoint(point);
            return window != IntPtr.Zero && GetAncestor(window, 2) == topLevel; // GA_ROOT
        }
    }
}
