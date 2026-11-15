import { useCallback, useEffect, useRef, useState } from 'react';
import { newId } from './format';
import { useT, type Translate } from './i18n';
import { send, type SetupCommand } from './protocol';
import type { SetupReply } from './setup';

/** A command whose answer never comes (host busy or gone) must not leave a spinner running for ever. */
export const REPLY_TIMEOUT_MS = 30_000;

/** A sign-out is `omp auth-broker logout` (up to 20 s) plus a new helper reading the provider list: its answer may come late. */
export const LOGOUT_REPLY_TIMEOUT_MS = 60_000;

/**
 * Switching sharing or saving its address may wait for the running share to stop; connecting to a sharing PC waits until its gateway
 * admits the new token (up to 15 s, at most 30 s in all): their answers may come late.
 */
export const SHARE_REPLY_TIMEOUT_MS = 60_000;

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
  | 'shareEnable'
  | 'shareConfigure'
  | 'shareNewCode'
  | 'shareRevoke'
  | 'connectSharedModels'
  | 'logout'
  | 'models'
  | 'refreshModels'
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
  /** The host's answer, kept here the first time it is seen: the reply list is short and an old answer falls off it. */
  answer?: { error?: string };
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
 * {@link SetupReply}). The Accounts, Remote and Agents pages share one (created in `App`, so that an answer that arrives while its
 * page is off screen is still there on return); the composer pickers have their own, each with its own slots.
 * `translate`: for a caller above the language provider (`App`); everyone else gets the page's language from the context.
 */
export function useSetupCommands(replies: SetupReply[], translate?: Translate): SetupCommands {
  const contextTranslate = useT();
  const t = translate ?? contextTranslate;
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
    // The newest reply with this id is the answer (the host may answer one request again); the remembered one stands in once it has
    // fallen off the short reply list.
    const answer = replies.find((item) => item.requestId === entry.requestId) ?? entry.answer;
    if (answer) return { pending: false, error: answer.error, done: !answer.error };
    if (entry.timedOut) return { pending: false, error: t('setup.noReply'), done: false, timedOut: true };
    return { pending: true, done: false };
  };

  // An answer is remembered with its command: the reply list keeps only the newest few, and a command whose answer fell off it must not
  // look unanswered again.
  useEffect(() => {
    setSent((previous) => {
      let next = previous;
      for (const slot of Object.keys(previous) as Slot[]) {
        const entry = previous[slot];
        if (!entry || entry.unreachable) continue;
        const reply = replies.find((item) => item.requestId === entry.requestId);
        if (!reply || (entry.answer && entry.answer.error === reply.error)) continue;
        if (next === previous) next = { ...previous };
        next[slot] = { ...entry, answer: reply.error ? { error: reply.error } : {} };
      }
      return next;
    });
  }, [sent, replies]);

  // Give up on commands that nobody answers.
  useEffect(() => {
    const timers = Object.values(sent).flatMap((entry) =>
      entry && !entry.unreachable && !entry.timedOut && !entry.answer && !replies.some((reply) => reply.requestId === entry.requestId)
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

/**
 * For a page that is unmounted while its tab is off screen and shares its commands with the other pages: leaving the page forgets the
 * answers the user has already seen there (a note, an error, "saved"), so they are not shown again on return. A command that is still
 * waiting is kept, and so is a slot for which `keep` says so: its answer arrives while the page is away and is shown on return.
 */
export function useRetireSeenOnLeave(cmd: SetupCommands, slots: readonly Slot[], keep?: (slot: Slot) => boolean): void {
  const latest = useRef({ cmd, keep });
  latest.current = { cmd, keep };
  useEffect(() => () => {
    const { cmd: current, keep: kept } = latest.current;
    for (const slot of slots) {
      if (current.sent[slot] && !current.feedback(slot).pending && !kept?.(slot)) current.clear(slot);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
}
