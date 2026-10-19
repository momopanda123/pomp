export interface ConversationPartner { kind: 'main' | 'agent' | 'team'; id?: string }
/** An open agent or team tab: its own conversation. */
export interface ConversationTab { sessionId: string; partner: ConversationPartner; title?: string }
/** The current conversation's partner and the workspace's chat tabs; `main` is the Default (POMP) tab's conversation. */
export interface TabsState { sessionId?: string | null; partner: ConversationPartner; title?: string; message?: string; main?: string | null; tabs: ConversationTab[] }
export interface CommandReply { requestId: string; ok: boolean; error?: string }
export interface QueueItem {
  id: string; text: string; attachments?: { path: string }[]; includeEditorContext: boolean;
  team?: string; agent?: string; agentInstance?: string; createdAt: string;
  /** It goes to this conversation itself and is no slash command, so the queue's steer action can send it now. */
  canSteer?: boolean;
}
export interface QueueState { sessionId?: string | null; items: QueueItem[]; paused: boolean; reason?: string }
export interface HistoryFilter { kind?: 'team' | 'agent' | 'dm'; id?: string; status?: string; includePinned: boolean }
export interface AgentHistoryMember { agent: string; jobId: string; status: string; toolCount: number; tokens: number | null; durationMs: number | null; result?: string | null }
export interface AgentHistoryEntry {
  id: string; sessionId: string; promptId?: string; target: { kind: 'team' | 'agent' | 'dm'; id: string; title: string };
  text: string; startedAt: string; endedAt?: string; status: string; pinned: boolean; label?: string; members: AgentHistoryMember[];
}
export interface AgentHistoryState { entries: AgentHistoryEntry[]; truncated: boolean }
export interface ConversationHostToUi {
  'conversation/tabsState': TabsState;
  'conversation/tabReply': CommandReply;
  'chat/queued': { clientMessageId: string; itemId: string };
  'chat/queueState': QueueState;
  'chat/queueReply': CommandReply;
  'agents/historyState': AgentHistoryState;
  'agents/historyReply': CommandReply;
}
export interface ConversationUiToHost {
  /** open: the partner's tab (a new conversation when it has none); activate: a tab's conversation (no sessionId = Default); close: a tab. */
  'conversation/tab': { requestId: string; action: 'open' | 'activate' | 'close'; partner?: ConversationPartner; sessionId?: string };
  /** steer: the item leaves the queue and goes to the running agent now (with nothing running it is sent normally). */
  'chat/queue': { requestId: string; action: 'edit' | 'remove' | 'move' | 'clear' | 'pause' | 'resume' | 'steer'; id?: string; text?: string; index?: number };
  'agents/history': { requestId: string; action: 'refresh' | 'delete' | 'clear' | 'pin' | 'unpin' | 'label' | 'rerun'; id?: string; label?: string; filter?: HistoryFilter };
}
