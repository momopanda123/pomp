import { useCallback, useEffect, useState } from 'react';
import { newId } from './format';
import { useT } from './i18n';
import { send, type SetupCommand } from './protocol';
import type { SetupReply } from './setup';

/** A command whose answer never comes (host busy or gone) must not leave a spinner running for ever. */
export const REPLY_TIMEOUT_MS = 30_000;

/** A sign-out is `omp auth-broker logout` (up to 20 s) plus a new helper reading the provider list: its answer may come late. */
export const LOGOUT_REPLY_TIMEOUT_MS = 60_000;

/** What a command was for: its answer's error is shown next to the control that caused it. */
export type Slot =
  | 'refresh'
  | 'providers'
  | 'login'
  | 'cancel'
  | 'answer'
  | 'saveKey'
  | 'removeKey'
  | 'saveLocalServer'
  | 'removeLocalServer'
  | 'logout'
  | 'models'
  | 'setModel'
  | 'setEditorModel'
  | 'resolveEditorModel'
  | 'thinking'
  | 'setThinking'
  | 'usage';

interface Sent {
  requestId: string;
  /** What the command was about (a provider id, "console"): lets a row find its own answer. */
  tag?: string;
  /** `postMessage` failed: nothing reached the host. */
  unreachable?: boolean;
  timedOut?: boolean;
  timeoutMs: number;
}

export interface Feedback {
  pending: boolean;
  error?: string;
  done: boolean;
  /** `error` is only "the host never answered": a command that was already seen working (a running sign-in) can ignore it. */
  timedOut?: boolean;
}

export interface SetupCommands {
  /** Slot -> the command last posted for it. */
  sent: Partial<Record<Slot, Sent>>;
  /** Posts a command and remembers it under `slot`, so that its answer can be matched. False when nothing reached the host. */
  post: (slot: Slot, make: (requestId: string) => SetupCommand, tag?: string, timeoutMs?: number) => boolean;
  feedback: (slot: Slot) => Feedback;
  /** Forgets the command (and so its answer / error) of a slot. */
  clear: (slot: Slot) => void;
}

/**
 * Posting `setup/command`s and matching their answers (`setup/state` with the same `requestId`, kept by the reducer as
 * {@link SetupReply}). Used by the account panel and by the composer pickers, each with its own slots.
 */
export function useSetupCommands(replies: SetupReply[]): SetupCommands {
  const t = useT();
  const [sent, setSent] = useState<Partial<Record<Slot, Sent>>>({});

  const post = useCallback<SetupCommands['post']>((slot, make, tag, timeoutMs = REPLY_TIMEOUT_MS) => {
    const requestId = newId();
    const ok = send('setup/command', make(requestId));
    setSent((previous) => ({ ...previous, [slot]: { requestId, tag, timeoutMs, ...(ok ? {} : { unreachable: true }) } }));
    return ok;
  }, []);

  const clear = useCallback((slot: Slot) => setSent((previous) => (previous[slot] ? { ...previous, [slot]: undefined } : previous)), []);

  const feedback = (slot: Slot): Feedback => {
    const entry = sent[slot];
    if (!entry) return { pending: false, done: false };
    if (entry.unreachable) return { pending: false, error: t('error.hostUnavailable'), done: false };
    const reply = replies.find((item) => item.requestId === entry.requestId);
    if (reply) return { pending: false, error: reply.error, done: !reply.error };
    if (entry.timedOut) return { pending: false, error: t('setup.noReply'), done: false, timedOut: true };
    return { pending: true, done: false };
  };

  // Give up on commands that nobody answers.
  useEffect(() => {
    const timers = Object.values(sent).flatMap((entry) =>
      entry && !entry.unreachable && !entry.timedOut && !replies.some((reply) => reply.requestId === entry.requestId)
        ? [
            window.setTimeout(() => {
              setSent((previous) => {
                const next = { ...previous };
                for (const slot of Object.keys(next) as Slot[]) {
                  if (next[slot]?.requestId === entry.requestId) next[slot] = { ...next[slot]!, timedOut: true };
                }
                return next;
              });
            }, entry.timeoutMs),
          ]
        : [],
    );
    return () => timers.forEach((timer) => window.clearTimeout(timer));
  }, [sent, replies]);

  return { sent, post, feedback, clear };
}
