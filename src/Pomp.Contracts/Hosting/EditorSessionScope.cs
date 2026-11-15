using System;
using System.Threading;

namespace Pomp.Hosting
{
    /// <summary>
    /// The conversation on whose behalf the current asynchronous flow runs (a host tool call, a disk edit report). Editor adapters are
    /// shared by every conversation, so what they record (Live marks, bookmarks) is attributed through this scope, not to whichever
    /// conversation the chat shows at that moment. Null outside such a flow.
    /// </summary>
    public static class EditorSessionScope
    {
        private static readonly AsyncLocal<EditorSessionContext?> current = new AsyncLocal<EditorSessionContext?>();

        public static EditorSessionContext? Current => current.Value;

        /// <summary>Sets the owner for the calls made until the result is disposed (it flows across awaits); disposing restores the previous one.</summary>
        public static IDisposable Enter(EditorSessionContext? context)
        {
            var previous = current.Value;
            current.Value = context;
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly EditorSessionContext? previous;
            private bool disposed;

            public Restore(EditorSessionContext? previous) { this.previous = previous; }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                current.Value = previous;
            }
        }
    }
}
