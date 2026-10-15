/**
 * Engine setup (accounts / login / API keys / model / usage): pure helpers shared by the reducer, the account panel and the
 * composer pickers. Nothing here ever holds a secret: an API key or a sign-in code lives only in the input that produced it and
 * in the one `setup/command` that carries it.
 */
import type { MessageKey, Translate } from './i18n';
import {
  SETUP_REQUIRED,
  type BridgeStatePayload,
  type SetupEditorModel,
  type SetupLogin,
  type SetupLoginStateName,
  type SetupModel,
  type SetupProvider,
  type SetupStatePayload,
  type UsageLimit,
  type UsageReport,
  type UsageStatus,
} from './protocol';

/** What the page keeps of the host's usage report: `fetchedAt` is normalised to milliseconds since the epoch. */
export interface SetupUsage {
  fetchedAt?: number;
  reports: UsageReport[];
  error?: string;
}

/** What the page keeps of a `setup/state`: the payload without the answer-to-a-command fields (those go to {@link SetupReply}). */
export type SetupState = Omit<SetupStatePayload, 'requestId' | 'error' | 'usage'> & { usage?: SetupUsage };

/** The answer to one of this page's `setup/command`s: `error` is why it failed (absent = it worked). */
export interface SetupReply {
  requestId: string;
  error?: string;
}

/** Replies kept for matching: only the commands the user just issued matter, so the list stays tiny. */
export const MAX_SETUP_REPLIES = 16;

/** The host's key rule (docs/ui-protocol.md `saveKey`): 8..1024 printable characters without spaces. */
export const MIN_API_KEY_CHARS = 8;
export const MAX_API_KEY_CHARS = 1024;

/** The host's limit for the answer to a sign-in prompt (`answerPrompt`). */
export const MAX_PROMPT_ANSWER_CHARS = 4096;

/** Usage older than this is asked for again when the account panel opens (docs/ui-protocol.md `listUsage`). */
export const USAGE_MAX_AGE_MS = 60_000;

/** Defensive caps for lists from the host (a runaway list must not freeze the page). */
const MAX_PROVIDERS = 500;
const MAX_MODELS = 5000;
const MAX_REPORTS = 100;
const MAX_LIMITS = 50;
const MAX_LEVELS = 32;

const LOGIN_STATES: readonly SetupLoginStateName[] = ['idle', 'running', 'succeeded', 'failed'];
const USAGE_STATUSES: readonly UsageStatus[] = ['ok', 'warning', 'exhausted', 'unknown'];

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null;
const text = (value: unknown): string | undefined => (typeof value === 'string' ? value : undefined);
const finite = (value: unknown): number | undefined => (typeof value === 'number' && Number.isFinite(value) ? value : undefined);

/** Rows of a host list that have all the fields the panel renders; anything else is skipped instead of crashing the page. */
function rows<T>(value: unknown, pick: (row: Record<string, unknown>) => T | null, max = Number.POSITIVE_INFINITY): T[] {
  if (!Array.isArray(value)) return [];
  const out: T[] = [];
  for (const row of value) {
    if (out.length >= max) break;
    const picked = isRecord(row) ? pick(row) : null;
    if (picked) out.push(picked);
  }
  return out;
}

/** `fetchedAt` as milliseconds since the epoch: ISO text, or a number (seconds are told apart from milliseconds by magnitude). */
export function parseFetchedAt(value: unknown): number | undefined {
  if (typeof value === 'number' && Number.isFinite(value) && value > 0) return value < 1e11 ? value * 1000 : value;
  if (typeof value === 'string' && value) {
    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? undefined : parsed;
  }
  return undefined;
}

function normalizeLogin(value: unknown): SetupLogin {
  const raw = isRecord(value) ? value : {};
  const login: SetupLogin = { state: LOGIN_STATES.find((state) => state === raw.state) ?? 'idle' };
  const provider = text(raw.provider);
  if (provider !== undefined) login.provider = provider;
  for (const key of ['message', 'url', 'instructions', 'progress'] as const) {
    const field = text(raw[key]);
    if (field) login[key] = field;
  }
  const prompt = isRecord(raw.prompt) ? raw.prompt : undefined;
  if (prompt && typeof prompt.id === 'string' && prompt.id) {
    const placeholder = text(prompt.placeholder);
    login.prompt = placeholder
      ? { id: prompt.id, message: text(prompt.message) ?? '', placeholder }
      : { id: prompt.id, message: text(prompt.message) ?? '' };
  }
  return login;
}

function normalizeLimit(row: Record<string, unknown>): UsageLimit {
  const limit: UsageLimit = {
    label: text(row.label) ?? '',
    status: USAGE_STATUSES.find((status) => status === row.status) ?? 'unknown',
  };
  const window = text(row.window);
  if (window) limit.window = window;
  const resetsAt = text(row.resetsAt);
  if (resetsAt) limit.resetsAt = resetsAt;
  const usedFraction = finite(row.usedFraction);
  if (usedFraction !== undefined) limit.usedFraction = usedFraction;
  const used = finite(row.used);
  if (used !== undefined) limit.used = used;
  const max = finite(row.limit);
  if (max !== undefined) limit.limit = max;
  const unit = text(row.unit);
  if (unit) limit.unit = unit;
  if (typeof row.shared === 'boolean') limit.shared = row.shared;
  const tier = text(row.tier);
  if (tier && /^[a-z][a-z0-9_-]{0,31}$/.test(tier)) limit.tier = tier;
  const modelId = text(row.modelId);
  if (modelId) limit.modelId = modelId;
  return limit;
}

function normalizeUsage(value: unknown): SetupUsage | undefined {
  if (!isRecord(value)) return undefined;
  const usage: SetupUsage = {
    reports: rows(
      value.reports,
      (row): UsageReport | null => {
        if (typeof row.provider !== 'string' || !row.provider) return null;
        const report: UsageReport = { provider: row.provider, limits: rows(row.limits, normalizeLimit, MAX_LIMITS) };
        const account = text(row.account);
        if (account) report.account = account;
        const plan = text(row.plan);
        if (plan && /^[A-Za-z0-9][A-Za-z0-9 _.-]{0,39}$/.test(plan)) report.plan = plan;
        return report;
      },
      MAX_REPORTS,
    ),
  };
  const fetchedAt = parseFetchedAt(value.fetchedAt);
  if (fetchedAt !== undefined) usage.fetchedAt = fetchedAt;
  const error = text(value.error);
  if (error) usage.error = error;
  return usage;
}

/** Model identities are never truncated: a shortened id could select a different model. Display text is bounded. */
function normalizeEditorModel(value: unknown): SetupEditorModel {
  const raw = isRecord(value) && !Array.isArray(value) ? value : {};
  const editor: SetupEditorModel = { choice: raw.choice === 'chat' || raw.choice === 'model' ? raw.choice : 'auto' };
  const identity = (value: unknown): { provider: string; id: string } | undefined => {
    if (!isRecord(value) || Array.isArray(value)) return undefined;
    const valid = (field: unknown, max: number): field is string => typeof field === 'string' && field.trim().length > 0 && field.length <= max && !/[\u0000-\u001f\u007f]/u.test(field);
    return valid(value.provider, 128) && valid(value.id, 512) ? { provider: value.provider, id: value.id } : undefined;
  };
  const model = identity(raw.model);
  if (editor.choice === 'model' && model) editor.model = model;
  const effective = identity(raw.effective);
  if (effective) {
    const name = isRecord(raw.effective) ? text(raw.effective.name)?.slice(0, 512) : undefined;
    editor.effective = name ? { ...effective, name } : effective;
  }
  if (raw.resolving === true) editor.resolving = true;
  const error = text(raw.error)?.slice(0, 4000);
  if (error) editor.error = error;
  return editor;
}

/**
 * A defensive copy of a `setup/state` payload. Only the documented fields are copied (so a field the host should not have sent
 * is dropped, never kept), missing lists become empty, an unknown login state reads as idle. Absent optional fields stay absent:
 * `providers`, `models`, `thinkingLevels` and `usage` mean "not loaded", an empty list means "loaded, nothing in it".
 */
export function normalizeSetup(payload: SetupStatePayload): SetupState {
  const source: Record<string, unknown> = isRecord(payload) ? payload : {};

  const next: SetupState = {
    login: normalizeLogin(source.login),
    editorModel: normalizeEditorModel(source.editorModel),
    apiKeyProviders: rows(source.apiKeyProviders, (row) =>
      typeof row.id === 'string' && row.id ? { id: row.id, label: text(row.label) || row.id, envVar: text(row.envVar) ?? '' } : null,
    ),
    keys: rows(source.keys, (row) =>
      typeof row.provider === 'string' && row.provider ? { provider: row.provider, envVar: text(row.envVar) ?? '', savedUtc: text(row.savedUtc) ?? '' } : null,
    ),
    localServers: rows(source.localServers, (row) =>
      typeof row.id === 'string' && /^local-[a-z0-9-]{1,40}$/.test(row.id) && typeof row.name === 'string' && typeof row.address === 'string'
        ? { id: row.id, name: row.name, address: row.address, hasKey: row.hasKey === true, savedUtc: text(row.savedUtc) ?? '' } : null,
    ),
  };
  localServerNames.clear();
  for (const server of next.localServers) localServerNames.set(server.id, server.name);

  if (Array.isArray(source.providers)) {
    const seen = new Set<string>();
    next.providers = rows(
      source.providers,
      (row): SetupProvider | null => {
        if (typeof row.id !== 'string' || !row.id || row.id.length > 128 || seen.has(row.id)) return null;
        seen.add(row.id);
        return { id: row.id, name: text(row.name) || row.id, authenticated: row.authenticated === true, available: row.available !== false };
      },
      MAX_PROVIDERS,
    );
  }
  const providersError = text(source.providersError);
  if (providersError) next.providersError = providersError;

  if (Array.isArray(source.models)) {
    next.models = rows(
      source.models,
      (row): SetupModel | null => {
        if (typeof row.provider !== 'string' || typeof row.id !== 'string' || !row.id) return null;
        const name = text(row.name);
        return name ? { provider: row.provider, id: row.id, name } : { provider: row.provider, id: row.id };
      },
      MAX_MODELS,
    );
  }
  const modelsError = text(source.modelsError);
  if (modelsError) next.modelsError = modelsError;
  const current = isRecord(source.currentModel) ? source.currentModel : undefined;
  if (current && typeof current.provider === 'string' && typeof current.id === 'string' && current.id) {
    next.currentModel = { provider: current.provider, id: current.id };
  }

  if (Array.isArray(source.thinkingLevels)) {
    const levels: string[] = [];
    for (const level of source.thinkingLevels) {
      if (typeof level === 'string' && level && level.length <= 64 && !levels.includes(level) && levels.length < MAX_LEVELS) levels.push(level);
    }
    next.thinkingLevels = levels;
  }
  const currentThinking = text(source.currentThinking);
  if (currentThinking) next.currentThinking = currentThinking;

  const usage = normalizeUsage(source.usage);
  if (usage) next.usage = usage;
  if (source.usageLoading === true) next.usageLoading = true;
  if (source.restartRecommended === true) next.restartRecommended = true;
  return next;
}

/** Appends the answer of a command (newest last); the oldest answers fall off. */
export function recordReply(replies: SetupReply[], requestId: string, error: string | undefined): SetupReply[] {
  const next: SetupReply = error ? { requestId, error } : { requestId };
  return [...replies.filter((reply) => reply.requestId !== requestId), next].slice(-MAX_SETUP_REPLIES);
}

/** omp failed to start because it has no model provider: the page shows the setup panel instead of the failure banner. */
export const isSetupRequired = (bridge: Pick<BridgeStatePayload, 'state' | 'reason'>): boolean =>
  bridge.state === 'failed' && bridge.reason === SETUP_REQUIRED;

export type ApiKeyProblem = 'short' | 'long' | 'chars';

/** Why `key` (already trimmed) cannot be sent, or null when the host would accept its shape (no whitespace or control characters). */
export function apiKeyProblem(key: string): ApiKeyProblem | null {
  if (/[\s\u0000-\u001f\u007f]/u.test(key)) return 'chars';
  if (key.length > MAX_API_KEY_CHARS) return 'long';
  if (key.length < MIN_API_KEY_CHARS) return 'short';
  return null;
}

/** A saved-on date for the key list; the raw text when it is not a date, empty when there is nothing to show. */
export function formatSavedDate(savedUtc: string, locale: string): string {
  if (!savedUtc) return '';
  const date = new Date(savedUtc);
  if (Number.isNaN(date.getTime())) return savedUtc;
  return date.toLocaleDateString(locale, { year: 'numeric', month: '2-digit', day: '2-digit' });
}

/** Stable string for a model, used as a list key (provider and id can both contain any character). */
export const modelKey = (model: { provider: string; id: string }): string => JSON.stringify([model.provider, model.id]);

/** Inverse of {@link modelKey}; null for anything that is not a key made by it. */
export function parseModelKey(value: string): { provider: string; id: string } | null {
  try {
    const parsed: unknown = JSON.parse(value);
    if (Array.isArray(parsed) && parsed.length === 2 && typeof parsed[0] === 'string' && typeof parsed[1] === 'string') {
      return { provider: parsed[0], id: parsed[1] };
    }
  } catch {
    // not a model key
  }
  return null;
}

// ---- Providers -----------------------------------------------------------------------------------------------------------

/** Shown first and as big buttons on the first run, when the host lists them (docs: popular providers first). */
export const POPULAR_PROVIDERS: readonly string[] = ['openai-codex', 'anthropic', 'muse-code', 'google-antigravity'];

/** The host's rule for the `provider` of `login` (docs/ui-protocol.md): anything else is refused there, so it is not offered here. */
export const isProviderId = (id: string): boolean => /^[a-z0-9][a-z0-9.-]{0,63}$/.test(id);

/** The popular providers that the host lists, in {@link POPULAR_PROVIDERS} order. */
export function popularProviders(providers: readonly SetupProvider[]): SetupProvider[] {
  const out: SetupProvider[] = [];
  for (const id of POPULAR_PROVIDERS) {
    const found = providers.find((provider) => provider.id === id);
    if (found) out.push(found);
  }
  return out;
}

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true });

/** Providers sorted by display name, narrowed by a search over name and id (every word must match, case-insensitive). */
export function filterProviders(providers: readonly SetupProvider[], query: string): SetupProvider[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  const matched = words.length === 0 ? [...providers] : providers.filter((provider) => {
    const haystack = `${provider.name} ${provider.id}`.toLowerCase();
    return words.every((word) => haystack.includes(word));
  });
  return matched.sort((a, b) => collator.compare(a.name, b.name) || collator.compare(a.id, b.id));
}

/** The display name of a provider: the host's name when the list is loaded and knows it, else the id. */
export function localizedProviderName(name: string, t: Translate): string {
  return name.replace(/Subscription/gi, t('setup.provider.subscription')).replace(/Sign in/gi, t('setup.provider.signIn'));
}

/** A listed provider's display name (providerLabel: company or service, no plan, no model list); the id when the list does not have it. */
/** The engine's own provider name (the chat tab's model picker keeps it as it was). */
export function hostProviderName(providers: readonly SetupProvider[] | undefined, id: string): string {
  return providers?.find((provider) => provider.id === id)?.name || localServerNames.get(id) || id;
}

/** The names of the saved local model servers by provider id, from the latest setup state: model lists show a server by its name. */
const localServerNames = new Map<string, string>();

/** A local server address as the host takes it: absolute http(s), no user information, no spaces. */
export function localAddressProblem(address: string): boolean {
  if (!address || address.length > 2048 || /[\s\u0000-\u001f\u007f]/u.test(address)) return true;
  try {
    const url = new URL(address);
    return (url.protocol !== 'http:' && url.protocol !== 'https:') || !!url.username || !!url.password || !url.hostname;
  } catch { return true; }
}

export function providerName(providers: readonly SetupProvider[] | undefined, id: string): string {
  const found = providers?.find((provider) => provider.id === id);
  return found ? providerLabel(id, found.name) : localServerNames.get(id) ?? id;
}

/** How an account is connected: an engine sign-in (a subscription) or an API key (saved here or in the environment). */
export type AccountKind = 'subscription' | 'apiKey';

/** The account tab's names by provider id: the company or service only — never a plan ("Plus/Pro") or a list of models. */
const PROVIDER_LABELS: Readonly<Record<string, string>> = {
  'openai-codex': 'OpenAI', 'openai-codex-device': 'OpenAI', openai: 'OpenAI', 'azure-openai': 'Azure OpenAI',
  anthropic: 'Anthropic', 'muse-code': 'Muse Code', 'github-copilot': 'GitHub Copilot',
  'google-gemini-cli': 'Google Gemini CLI', 'google-antigravity': 'Google Antigravity', google: 'Google Gemini',
  xai: 'xAI', zai: 'Z.AI', 'zai-coding-plan': 'Z.AI', 'kimi-code': 'Kimi Code', 'qwen-portal': 'Qwen',
};

/** Sign-in methods that store as another provider (the host's StoredUnder): what tells their tile from that provider's. */
const SIGN_IN_METHODS: Readonly<Record<string, { ko: string; en: string }>> = {
  'openai-codex-device': { ko: '장치 코드', en: 'Device code' },
};

const PLAN = '(?:plus|pro|max|team|business|enterprise)';

/** A host name without what is not the provider: parenthesised details, plan names, "Subscription", "Sign in". */
export function cleanProviderName(name: string): string {
  const cleaned = name
    .replace(/\s*[([][^)\]]*[)\]]/g, ' ')
    .replace(new RegExp(`\\b${PLAN}(?:\\s*/\\s*${PLAN})+\\b`, 'gi'), ' ')
    .replace(/\b(?:subscription|sign[ -]?in)\b/gi, ' ')
    .replace(new RegExp(`\\s+${PLAN}\\s*$`, 'i'), '')
    .replace(/[\s·:|/–—-]+$/u, '')
    .replace(/\s{2,}/g, ' ')
    .trim();
  return cleaned || name.trim();
}

/** The one name the account tab shows for a provider (connected accounts, add-account tiles, API keys). */
export function providerLabel(id: string, hostName?: string): string {
  return PROVIDER_LABELS[id] ?? cleanProviderName(hostName && hostName.trim() ? hostName : id);
}

/** The sign-in method of a provider entry that stores as another provider, e.g. the device-code sign-in; undefined for the rest. */
export function providerMethod(id: string, locale: string): string | undefined {
  const method = SIGN_IN_METHODS[id];
  return method && (locale.toLowerCase().startsWith('ko') ? method.ko : method.en);
}

/** The sign-in URL as a normalised http(s) URL, or undefined for anything else (it must never become a link or be opened). */
export function safeHttpUrl(value: string | undefined): string | undefined {
  if (!value || value.length > 8192) return undefined;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : undefined;
  } catch {
    return undefined;
  }
}

/** A failure that says the provider needs a terminal (a secret typed in a console): the console sign-in is offered. */
export const loginNeedsTerminal = (message: string | undefined): boolean =>
  message !== undefined && /terminal|console|tty|interactive|stdin|secret|password|터미널|콘솔|비밀/i.test(message);

// ---- Models --------------------------------------------------------------------------------------------------------------

export interface ModelGroup {
  provider: string;
  label: string;
  models: SetupModel[];
}

/** Models grouped by provider, in the order the host lists them. `label` turns a provider id into what is shown. */
export function groupModels(models: readonly SetupModel[], label: (provider: string) => string = (provider) => provider): ModelGroup[] {
  const byProvider = new Map<string, ModelGroup>();
  for (const model of models) {
    const group = byProvider.get(model.provider);
    if (group) group.models.push(model);
    else byProvider.set(model.provider, { provider: model.provider, label: label(model.provider), models: [model] });
  }
  // Providers by name, and the models of each by name (numbers by value: "GPT-5.6" before "GPT-6").
  for (const group of byProvider.values()) group.models.sort((a, b) => modelCollator.compare(a.name || a.id, b.name || b.id) || modelCollator.compare(a.id, b.id));
  return [...byProvider.values()].sort((a, b) => modelCollator.compare(a.label, b.label) || modelCollator.compare(a.provider, b.provider));
}
const modelCollator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/** Models narrowed by a search over name, id and provider (every word must match, case-insensitive). */
export function filterModels(models: readonly SetupModel[], query: string, label: (provider: string) => string = (provider) => provider): SetupModel[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return [...models];
  return models.filter((model) => {
    const haystack = `${model.name ?? ''} ${model.id} ${model.provider} ${label(model.provider)}`.toLowerCase();
    return words.every((word) => haystack.includes(word));
  });
}

// ---- Thinking levels -------------------------------------------------------------------------------------------------------

const THINKING_KEYS: Readonly<Record<string, MessageKey>> = {
  off: 'thinking.off',
  minimal: 'thinking.minimal',
  low: 'thinking.low',
  medium: 'thinking.medium',
  high: 'thinking.high',
  xhigh: 'thinking.xhigh',
  max: 'thinking.max',
  auto: 'thinking.auto',
};

/** A thinking level in the user's language; a level this page does not know is shown as the host wrote it. */
export function thinkingLabel(t: Translate, level: string): string {
  const key = Object.hasOwn(THINKING_KEYS, level) ? THINKING_KEYS[level] : undefined;
  return key ? t(key) : level;
}
