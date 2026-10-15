import { useEffect, useRef, useState } from 'react';
import { newId } from '../format';
import { useT } from '../i18n';
import { onMessage, send, type CaseFeedbackProposal, type CaseFeedbackSelection, type UiToHost } from '../protocol';
import { CaseDiagram } from './CaseDiagram';
import { Markdown } from './Markdown';
import { revealControls } from './RevealControls';
import './caseFeedback.css';

type Action = UiToHost['feedback/command']['action'];
type Comments = Record<string, string>;

const MAX_COMMENT = 2000;
const REQUEST_TIMEOUT_MS = 15000;
const DRAFT_DELAY_MS = 1200;

/** The proposal id the host puts in the tool result: `… (proposal <id>) …`. */
export function caseProposalId(resultText: string | undefined): string | null {
  return /\(proposal ([0-9a-f]{32})\)/.exec(resultText ?? '')?.[1] ?? null;
}

const commentsFrom = (proposal: CaseFeedbackProposal, draft: CaseFeedbackSelection[] = proposal.draft): Comments =>
  Object.fromEntries(proposal.cases.map(item => [item.caseId, draft.find(entry => entry.caseId === item.caseId)?.comment ?? '']));
const sameComments = (a: Comments, b: Comments) => Object.keys({ ...a, ...b }).every(key => (a[key] ?? '') === (b[key] ?? ''));

/**
 * Cases the assistant presented with present_case_feedback, shown inside its reply (docs/screen-guide.md): one comment box per
 * case, drafts kept by the host, and one button that sends every written comment at once through the conversation's queue.
 * A case without a comment is not sent. Nothing here runs a model or approves a tool.
 */
export function CaseCards({ proposalId, root, sessionId }: { proposalId: string; root?: string; sessionId?: string }) {
  const t = useT();
  const [proposal, setProposal] = useState<CaseFeedbackProposal | null>(null);
  const [comments, setComments] = useState<Comments>({});
  const [saved, setSaved] = useState<Comments>({});
  const [pending, setPending] = useState<Action | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const request = useRef<{ id: string; action: Action; sent?: Comments; timer: number } | null>(null);
  const latest = useRef({ comments, saved, proposal }); latest.current = { comments, saved, proposal };
  const dirty = proposal !== null && !sameComments(comments, saved);

  const post = (action: Action, sent?: Comments): boolean => {
    const current = latest.current.proposal;
    if (request.current || !root || !sessionId) return false;
    const id = newId();
    const timer = window.setTimeout(() => {
      if (request.current?.id !== id) return;
      request.current = null; setPending(null); setError(t('setup.noReply'));
    }, REQUEST_TIMEOUT_MS);
    request.current = { id, action, sent, timer };
    const selections = sent && current ? current.cases.map(item => {
      const comment = (sent[item.caseId] ?? '').slice(0, MAX_COMMENT);
      return { caseId: item.caseId, selected: comment.trim() !== '', comment, extra: '' };
    }) : undefined;
    const ok = send('feedback/command', { action, requestId: id, root, sessionId, proposalId,
      ...(current && action !== 'open' ? { revision: current.revision, draftRevision: current.draftRevision } : {}),
      ...(selections ? { selections } : {}) });
    if (!ok) { window.clearTimeout(timer); request.current = null; setError(t('error.hostUnavailable')); return false; }
    setPending(action); setError('');
    return true;
  };

  useEffect(() => onMessage(message => {
    if (message.type !== 'feedback/state') return;
    const state = message.payload;
    if (state.root !== root || state.sessionId !== sessionId) return;
    const own = request.current && state.requestId === request.current.id ? request.current : null;
    const next = state.proposal?.proposalId === proposalId ? state.proposal : null;
    if (!own && !next) return;
    if (own) { window.clearTimeout(own.timer); request.current = null; setPending(null); }
    if (own && state.error) { setError(state.error); return; }
    if (own) setNotice(state.notice ?? '');
    if (!next) { if (own?.action === 'open') setError(t('cases.unavailable')); return; }
    const host = commentsFrom(next);
    const { comments: typed, saved: baseline } = latest.current;
    setProposal(next);
    if (own?.action === 'draft' && own.sent) {
      // Whatever was typed after the save stays; the saved text becomes the clean baseline.
      setSaved(own.sent);
      return;
    }
    // Opening, sending or a push from the host: adopt its draft unless the user has unsaved typing.
    if (own?.action === 'submit' || own?.action === 'open' || sameComments(typed, baseline)) { setComments(host); setSaved(host); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [proposalId, root, sessionId]);

  // Open once per conversation; a reconnect or a reopened conversation shows the host's current state.
  useEffect(() => {
    setProposal(null); setComments({}); setSaved({});
    post('open');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [proposalId, root, sessionId]);

  // Drafts save themselves a moment after typing stops, one request at a time.
  useEffect(() => {
    if (!dirty || pending || proposal?.status !== 'draft') return;
    const timer = window.setTimeout(() => post('draft', { ...latest.current.comments }), DRAFT_DELAY_MS);
    return () => window.clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [comments, dirty, pending, proposal?.status]);

  if (!proposal) return <section className="case-cards" data-testid={`cases-${proposalId}`}>
    <p className="case-cards-status" role="status">{error ? revealControls(error) : t('cases.loading')}</p>
    {error && <button type="button" className="btn subtle small" onClick={() => post('open')}>{t('cases.reload')}</button>}
  </section>;

  // Typing goes on while a draft saves; only a send in flight (or a sent proposal) locks the comments.
  const locked = proposal.status !== 'draft' || pending === 'submit';
  const written = proposal.cases.filter(item => (comments[item.caseId] ?? '').trim() !== '').length;
  const canResend = proposal.status === 'submitted' && !dirty;
  const canSend = written > 0 && !pending && (proposal.status === 'draft' || canResend);
  const status = proposal.status === 'accepted' ? t('cases.accepted') : proposal.status === 'submitted' ? t('cases.submitted')
    : proposal.status === 'dispatching' ? t('cases.uncertain') : pending === 'draft' ? t('cases.saving') : !dirty && written > 0 ? t('cases.saved') : '';
  return <section className="case-cards" data-testid={`cases-${proposalId}`} aria-label={t('cases.label', { count: proposal.cases.length })}>
    <ol className="case-cards-list">
      {proposal.cases.map((item, index) => <li key={item.caseId} className="case-card" data-testid={`case-${item.caseId}`}>
        <h4 className="case-card-title"><span className="case-card-number">{index + 1}</span>{revealControls(item.title)}</h4>
        {item.bodyMarkdown && <Markdown text={item.bodyMarkdown} />}
        {item.image && root && sessionId && <CaseImage root={root} sessionId={sessionId} proposalId={proposalId} caseId={item.caseId} title={item.title} />}
        {item.diagram && <CaseDiagram source={item.diagram} label={t('cases.diagram', { title: item.title })} />}
        <label className="case-card-comment">
          <span>{t('cases.comment')}</span>
          <textarea data-testid={`case-comment-${item.caseId}`} rows={2} maxLength={MAX_COMMENT} value={comments[item.caseId] ?? ''}
            readOnly={locked}
            onChange={event => setComments(previous => ({ ...previous, [item.caseId]: event.target.value }))} />
        </label>
      </li>)}
    </ol>
    <div className="case-cards-foot">
      {(status || error || notice) && <p className="case-cards-status" role={error ? 'alert' : 'status'}>{revealControls(error || status || notice)}</p>}
      <button type="button" className="btn primary small" data-testid={`cases-send-${proposalId}`} disabled={!canSend}
        onClick={() => { if (canSend) post('submit', { ...comments }); }}>{t('cases.send', { count: written })}</button>
    </div>
  </section>;
}

const IMAGE_TIMEOUT_MS = 15000;
/** The host answers with the whole image (up to ~1 MiB) and its outbox is bounded: a few at a time, the rest wait. */
const MAX_IMAGE_REQUESTS = 2;
const IMAGE_DATA_URL = /^data:image\/(png|jpeg|gif|webp|bmp);base64,/;

type ImageOutcome = { url: string } | { error: string; kind?: 'timeout' | 'hostUnavailable' | 'invalid' };
const images = new Map<string, string>();
/** Requests that are queued or in flight, by image: cards showing the same image share one request. */
const imageLoads = new Map<string, Promise<ImageOutcome>>();
const imageQueue: (() => void)[] = [];
let imagesInFlight = 0;

/**
 * Asks the host for one image: at most {@link MAX_IMAGE_REQUESTS} requests are out at a time page-wide, and one that gets no answer
 * ends as a timeout (the host drops posts while the page is not ready, so a lost reply is real). Only a success is cached.
 */
function loadCaseImage(key: string, args: { root: string; sessionId: string; proposalId: string; caseId: string }): Promise<ImageOutcome> {
  const shared = imageLoads.get(key);
  if (shared) return shared;
  let resolve!: (outcome: ImageOutcome) => void;
  const load = new Promise<ImageOutcome>(done => { resolve = done; });
  imageLoads.set(key, load);
  const start = () => {
    imagesInFlight++;
    const requestId = newId();
    let finished = false;
    const finish = (outcome: ImageOutcome) => {
      if (finished) return;
      finished = true; off(); window.clearTimeout(timer);
      if ('url' in outcome) images.set(key, outcome.url);
      imageLoads.delete(key); imagesInFlight--; resolve(outcome);
      imageQueue.shift()?.();
    };
    const off = onMessage(message => {
      if (message.type !== 'feedback/imageResult' || message.payload.requestId !== requestId) return;
      const value = message.payload.dataUrl;
      finish(value && IMAGE_DATA_URL.test(value) ? { url: value } : message.payload.error ? { error: message.payload.error } : { error: '', kind: 'invalid' });
    });
    const timer = window.setTimeout(() => finish({ error: '', kind: 'timeout' }), IMAGE_TIMEOUT_MS);
    if (!send('feedback/image', { requestId, ...args })) finish({ error: '', kind: 'hostUnavailable' });
  };
  if (imagesInFlight < MAX_IMAGE_REQUESTS) start(); else imageQueue.push(start);
  return load;
}

/** A workspace image the host reads on request (only for this proposal's case); nothing is fetched from the network. */
function CaseImage({ root, sessionId, proposalId, caseId, title }: { root: string; sessionId: string; proposalId: string; caseId: string; title: string }) {
  const t = useT();
  const key = `${proposalId}:${caseId}`;
  const [outcome, setOutcome] = useState<ImageOutcome | null>(() => { const url = images.get(key); return url ? { url } : null; });
  const [attempt, setAttempt] = useState(0);
  // One request per image and attempt: re-renders (a new translate function included) never ask again; Retry is a new attempt.
  useEffect(() => {
    const cached = images.get(key);
    if (cached) { setOutcome({ url: cached }); return; }
    setOutcome(null);
    let current = true;
    void loadCaseImage(key, { root, sessionId, proposalId, caseId }).then(result => { if (current) setOutcome(result); });
    return () => { current = false; };
  }, [key, root, sessionId, proposalId, caseId, attempt]);
  if (!outcome) return <p className="case-card-note" role="status" data-testid={`case-image-loading-${caseId}`}>{t('cases.imageLoading')}</p>;
  if ('error' in outcome) {
    const reason = outcome.kind === 'timeout' ? t('setup.noReply') : outcome.kind === 'hostUnavailable' ? t('error.hostUnavailable') : outcome.kind === 'invalid' ? t('cases.imageInvalid') : outcome.error;
    return <>
      <p className="case-card-note" role="status" data-testid={`case-image-error-${caseId}`}>{revealControls(t('cases.imageError', { error: reason }))}</p>
      {/* The card is a column flexbox: without align-self the button would stretch across it. */}
      <button type="button" className="btn subtle small" style={{ alignSelf: 'flex-start' }} data-testid={`case-image-retry-${caseId}`} onClick={() => setAttempt(count => count + 1)}>{t('cases.imageRetry')}</button>
    </>;
  }
  return <img className="case-card-image" src={outcome.url} alt={t('cases.image', { title })} />;
}
