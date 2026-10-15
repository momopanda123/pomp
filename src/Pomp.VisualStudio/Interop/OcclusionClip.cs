using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;

namespace Pomp.VisualStudio.Interop
{
    /// <summary>
    /// Keeps a native child window (the WebView2 host) clipped to the part that windows above it do not cover.
    /// WebView2 draws through DirectComposition, which Windows composes over the top-level window's redirected content
    /// whatever the child z-order: an auto-hide tool window sliding out over the chat (Server Explorer, Toolbox) is above
    /// the browser in z-order yet painted underneath it. A window region does clip that content, so the host's region is
    /// kept equal to its rectangle minus every visible window above it, at each level up to the top-level window.
    /// Window events of this process (shown, hidden, destroyed, reordered, moved, reparented) trigger one coalesced
    /// recompute on the UI thread; while a clip is applied it is also re-checked periodically, because Visual Studio may
    /// park a slide-out window elsewhere without an event that names a window under the chat's top-level window.
    /// </summary>
    internal sealed class OcclusionClip : IDisposable
    {
        private const uint EventObjectDestroy = 0x8001;
        private const uint EventObjectReorder = 0x8004;
        private const uint EventObjectLocationChange = 0x800B;
        private const uint EventObjectParentChange = 0x800F;
        private static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(250);
        private const uint WinEventOutOfContext = 0;
        private const int ObjIdWindow = 0;
        private const int ChildIdSelf = 0;
        private const uint GaParent = 1;
        private const uint GaRoot = 2;
        private const uint GwHwndPrev = 3;
        private const int RgnDiff = 4;
        private const int MaxWindowsScanned = 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect : IEquatable<Rect>
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public bool Equals(Rect other) =>
                Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        }

        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr gdiObject);

        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly IPompLogger logger;
        private readonly Func<IntPtr> targetWindow;
        private readonly WinEventProc callback;
        private readonly IntPtr[] hooks;
        private readonly List<Rect> appliedCovers = new List<Rect>();
        private IntPtr clippedWindow;
        private Rect clippedBounds;
        private bool pending;
        private bool recheckScheduled;
        private bool disposed;

        /// <summary>Must be created (and disposed) on the UI thread that owns the target window.</summary>
        public OcclusionClip(JoinableTaskFactory joinableTaskFactory, IPompLogger logger, Func<IntPtr> targetWindow)
        {
            this.joinableTaskFactory = joinableTaskFactory;
            this.logger = logger;
            this.targetWindow = targetWindow;

            // Kept in a field: the hooks call it until UnhookWinEvent. Out-of-context events arrive through this
            // thread's message loop, so the callback runs on the UI thread.
            callback = OnWinEvent;
            var processId = (uint)Process.GetCurrentProcess().Id;
            hooks = new[]
            {
                SetWinEventHook(EventObjectDestroy, EventObjectReorder, IntPtr.Zero, callback, processId, 0, WinEventOutOfContext),
                SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, callback, processId, 0, WinEventOutOfContext),
                SetWinEventHook(EventObjectParentChange, EventObjectParentChange, IntPtr.Zero, callback, processId, 0, WinEventOutOfContext),
            };
        }

        /// <summary>Schedules a recompute (e.g. after the target window was shown or replaced).</summary>
        public void Invalidate()
        {
            if (disposed || pending)
            {
                return;
            }

            pending = true;
            joinableTaskFactory.RunAsync(async () =>
            {
                // Always yields: a burst of window events (a slide-out animation) collapses into one recompute.
#if VS2017
                // Microsoft.VisualStudio.Threading 15.8 (VS 2017) has no alwaysYield overload.
                await System.Threading.Tasks.Task.Yield();
                await joinableTaskFactory.SwitchToMainThreadAsync();
#else
                await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
#endif
                Update();
            }).Task.Forget(logger, "Clipping the chat to the panes above it");
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var hook in hooks)
            {
                if (hook != IntPtr.Zero)
                {
                    UnhookWinEvent(hook);
                }
            }

            Release();
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            if (disposed || pending || window == IntPtr.Zero || objectId != ObjIdWindow || childId != ChildIdSelf)
            {
                return;
            }

            // A new cover can only be a window under the target's top-level window. Once a clip is applied, any event may
            // be the cover going away (a destroyed window no longer has a top-level window to compare).
            var target = targetWindow();
            if (target != IntPtr.Zero && (clippedWindow != IntPtr.Zero || GetAncestor(window, GaRoot) == GetAncestor(target, GaRoot)))
            {
                Invalidate();
            }
        }

        /// <summary>While a clip is applied, recomputes it every <see cref="RecheckInterval"/> until nothing covers the target.</summary>
        private void ScheduleRecheck()
        {
            if (recheckScheduled || disposed)
            {
                return;
            }

            recheckScheduled = true;
            joinableTaskFactory.RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(RecheckInterval).ConfigureAwait(false);
                await joinableTaskFactory.SwitchToMainThreadAsync();
                recheckScheduled = false;
                if (clippedWindow != IntPtr.Zero)
                {
                    Update();
                }
            }).Task.Forget(logger, "Re-checking the chat clip");
        }

        private void Update()
        {
            pending = false;
            if (disposed)
            {
                return;
            }

            var target = targetWindow();
            if (target != clippedWindow)
            {
                // A new WebView2 host: the old one (if any) is being destroyed with its region.
                clippedWindow = IntPtr.Zero;
                appliedCovers.Clear();
            }

            if (target == IntPtr.Zero || !IsWindowVisible(target) || !GetWindowRect(target, out var bounds))
            {
                return;
            }

            var covers = FindCovers(target, bounds);
            if (target == clippedWindow && bounds.Equals(clippedBounds) && SameCovers(covers))
            {
                ScheduleRecheck();
                return;
            }

            if (covers.Count == 0)
            {
                if (clippedWindow != IntPtr.Zero)
                {
                    SetWindowRgn(target, IntPtr.Zero, true);
                }

                clippedWindow = IntPtr.Zero;
                appliedCovers.Clear();
                return;
            }

            // Window-region coordinates are relative to the window's own top-left corner.
            var region = CreateRectRgn(0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            foreach (var cover in covers)
            {
                var hole = CreateRectRgn(cover.Left - bounds.Left, cover.Top - bounds.Top, cover.Right - bounds.Left, cover.Bottom - bounds.Top);
                CombineRgn(region, region, hole, RgnDiff);
                DeleteObject(hole);
            }

            // On success the system owns the region.
            if (SetWindowRgn(target, region, true) == 0)
            {
                DeleteObject(region);
                return;
            }

            clippedWindow = target;
            clippedBounds = bounds;
            appliedCovers.Clear();
            appliedCovers.AddRange(covers);
            ScheduleRecheck();
        }

        /// <summary>Overlaps of <paramref name="bounds"/> with visible windows above the target or above any of its ancestors.</summary>
        private static List<Rect> FindCovers(IntPtr target, Rect bounds)
        {
            var covers = new List<Rect>();
            var root = GetAncestor(target, GaRoot);
            var scanned = 0;
            for (var level = target; level != IntPtr.Zero && level != root; level = GetAncestor(level, GaParent))
            {
                for (var above = GetWindow(level, GwHwndPrev); above != IntPtr.Zero; above = GetWindow(above, GwHwndPrev))
                {
                    if (++scanned > MaxWindowsScanned)
                    {
                        return covers;
                    }

                    if (IsWindowVisible(above) && GetWindowRect(above, out var rect) && Intersect(bounds, rect, out var overlap))
                    {
                        covers.Add(overlap);
                    }
                }
            }

            return covers;
        }

        private static bool Intersect(Rect a, Rect b, out Rect overlap)
        {
            overlap = new Rect
            {
                Left = Math.Max(a.Left, b.Left),
                Top = Math.Max(a.Top, b.Top),
                Right = Math.Min(a.Right, b.Right),
                Bottom = Math.Min(a.Bottom, b.Bottom),
            };
            return overlap.Left < overlap.Right && overlap.Top < overlap.Bottom;
        }

        private bool SameCovers(List<Rect> covers)
        {
            if (covers.Count != appliedCovers.Count)
            {
                return false;
            }

            for (var i = 0; i < covers.Count; i++)
            {
                if (!covers[i].Equals(appliedCovers[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private void Release()
        {
            var window = clippedWindow;
            clippedWindow = IntPtr.Zero;
            appliedCovers.Clear();
            if (window != IntPtr.Zero && IsWindow(window))
            {
                SetWindowRgn(window, IntPtr.Zero, true);
            }
        }
    }
}
