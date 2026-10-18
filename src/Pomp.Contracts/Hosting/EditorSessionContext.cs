namespace Pomp.Hosting
{
    /// <summary>Host-only editor provenance; immutable and independent of WebView message delivery.</summary>
    public readonly struct EditorSessionContext
    {
        public EditorSessionContext(string workspaceRoot, string sessionId, bool streaming, string taskId, string sessionTitle)
        { WorkspaceRoot = workspaceRoot; SessionId = sessionId; IsStreaming = streaming; TaskId = taskId; SessionTitle = sessionTitle; }
        public string WorkspaceRoot { get; }
        public string SessionId { get; }
        public bool IsStreaming { get; }
        /// <summary>The admitted prompt identity, shared by every edit in that task; empty when no task is authoritative.</summary>
        public string TaskId { get; }
        /// <summary>The conversation's title; empty until the conversation has one.</summary>
        public string SessionTitle { get; }
    }

    /// <summary>Optional host capability used at the instant an editor change commits.</summary>
    public interface IEditorSessionContextSource
    {
        EditorSessionContext EditorSessionContext { get; }
    }
}
