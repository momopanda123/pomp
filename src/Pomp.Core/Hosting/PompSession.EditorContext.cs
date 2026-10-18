namespace Pomp.Hosting
{
    public sealed partial class PompSession : IEditorSessionContextSource
    {
        public EditorSessionContext EditorSessionContext
        {
            get
            {
                lock (_stateLock)
                {
                    var taskId = string.Empty;
                    // Chat admission serializes prompts. Never guess which task owns an edit during an uncertain switch.
                    if (!_sessionSwitchUncertain && _turns.Count == 1)
                        foreach (var turn in _turns.Values) taskId = turn.PromptId;
                    return new EditorSessionContext(_info.WorkingDirectory ?? string.Empty,
                        _sessionSwitchUncertain ? string.Empty : _info.SessionId ?? string.Empty, _info.IsStreaming, taskId,
                        _sessionSwitchUncertain ? string.Empty : _info.SessionName ?? string.Empty);
                }
            }
        }
    }
}
