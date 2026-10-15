export interface ConversationPartner { kind: 'main' | 'agent' | 'team'; id?: string }
export interface PartnerState { sessionId?: string | null; partner: ConversationPartner; instance?: string; title?: string; message?: string }
export interface CommandReply { requestId: string; ok: boolean; error?: string }
export interface QueueItem {
  id: string; text: string; attachments?: { path: string }[]; includeEditorContext: boolean;
  team?: string; agent?: string; agentInstance?: string; createdAt: string;
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
  'conversation/partnerState': PartnerState;
  'conversation/partnerReply': CommandReply;
  'chat/queued': { clientMessageId: string; itemId: string };
  'chat/queueState': QueueState;
  'chat/queueReply': CommandReply;
  'agents/historyState': AgentHistoryState;
  'agents/historyReply': CommandReply;
}
export interface ConversationUiToHost {
  'conversation/partner': { requestId: string; partner: ConversationPartner };
  'chat/queue': { requestId: string; action: 'edit' | 'remove' | 'move' | 'clear' | 'pause' | 'resume'; id?: string; text?: string; index?: number };
  'agents/history': { requestId: string; action: 'refresh' | 'delete' | 'clear' | 'pin' | 'unpin' | 'label' | 'rerun'; id?: string; label?: string; filter?: HistoryFilter };
}

export function partnerTarget(partner?: ConversationPartner): { kind: 'agent' | 'team'; id: string } | undefined {
  return partner && partner.kind !== 'main' && partner.id ? { kind: partner.kind, id: partner.id } : undefined;
}
