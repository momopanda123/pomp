import { toolsTranslate } from '../../i18n/toolsStrings';
import { HOOK_BYTES, composeHook } from '../../resourceBytes';
import type { HookDraft, HookEvent, HookInfo, ToolsReply } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import { Field } from '../ui';
import { bytesOver } from './InPageDialog';

/** The script part of the automation form: a PowerShell hook file (`.pomp/hooks/*.md`). */
export interface HookDraftFields { event: HookEvent; glob: string; timeout: string; description: string; script: string }
export const emptyHookDraft = (): HookDraftFields => ({ event: 'onFileSave', glob: '**', timeout: '120', description: '', script: '' });
export const hookDraftOf = (hook: HookInfo): HookDraftFields => ({ event: hook.event === 'onBuild' ? 'onBuild' : 'onFileSave', glob: hook.glob || '**', timeout: String(hook.timeoutSeconds ?? 120), description: hook.description ?? '', script: hook.script ?? '' });

// eslint-disable-next-line no-control-regex
const control = /[\x00-\x1f\x7f]/;
/** The host's rule (ProjectHook.Pattern): relative, at most 300 characters, only *, ** and ? as wildcards. */
function validGlob(glob: string): boolean {
  return glob.length > 0 && glob.length <= 300 && !control.test(glob) && !/^[\\/]/.test(glob) && !glob.includes(':') &&
    !/[[\]{}(),]/.test(glob) && !glob.replace(/\\/g, '/').split('/').includes('..');
}

/**
 * Validates and saves a hook (create for a new one, update for `original`). Returns the validation text, or the host reply.
 * New hooks take the lowercase name rule; an existing file keeps its name. The file limit counts UTF-8 bytes, the script limit characters, as the host does.
 */
export async function saveHook(controller: ToolsController, locale: string, name: string, enabled: boolean, draft: HookDraftFields, original?: HookInfo): Promise<string | ToolsReply> {
  const t = toolsTranslate(locale);
  const fileName = original?.name ?? name.trim(); const timeout = Number(draft.timeout);
  const glob = draft.event === 'onFileSave' ? draft.glob.trim() || '**' : '**'; const script = draft.script.replace(/\r\n/g, '\n');
  const over = bytesOver(t, composeHook(fileName, draft.description.trim(), draft.event, enabled, glob, timeout, script), HOOK_BYTES);
  if (over) return over;
  if ((!original && !/^[a-z0-9-]{2,40}$/.test(fileName)) || control.test(draft.description) || draft.description.length > 500 ||
    !validGlob(glob) || !Number.isInteger(timeout) || timeout < 1 || timeout > 3600 || script.trim().length < 1 || script.trim().length > 6000 || script.includes('```')) return t('hooks.invalid');
  const hook: HookDraft = { description: draft.description.trim(), event: draft.event, glob, timeoutSeconds: timeout, enabled, script };
  return original
    ? controller.command('hooks', { action: 'update', id: original.id, expectedHash: original.hash, hook })
    : controller.command('hooks', { action: 'create', name: fileName, hook });
}

/** Signal, target files, time limit, description and script of a hook; the name, action and on/off live in the shared form. */
export function HookFields({ draft, locale, onChange }: { draft: HookDraftFields; locale: string; onChange: (patch: Partial<HookDraftFields>) => void }) {
  const t = toolsTranslate(locale);
  return <>
    <div className="ui-field-row">
      <Field label={t('hooks.event')}>{id => <select id={id} data-testid="pshooks-event" value={draft.event} onChange={event => onChange({ event: event.target.value as HookEvent })}>
        <option value="onFileSave">{t('hooks.eventSave')}</option><option value="onBuild">{t('hooks.eventBuild')}</option></select>}</Field>
      <Field label={t('hooks.timeout')}>{id => <input id={id} type="number" data-testid="pshooks-timeout" min={1} max={3600} value={draft.timeout} onChange={event => onChange({ timeout: event.target.value })} />}</Field>
    </div>
    {draft.event === 'onFileSave' && <Field label={t('hooks.glob')}>{id => <input id={id} data-testid="pshooks-glob" className="ui-mono" maxLength={300} spellCheck={false} value={draft.glob} onChange={event => onChange({ glob: event.target.value })} />}</Field>}
    <Field label={t('hooks.description')}>{id => <input id={id} data-testid="pshooks-description" maxLength={500} value={draft.description} onChange={event => onChange({ description: event.target.value })} />}</Field>
    <Field label={t('hooks.script')}>{id => <textarea id={id} data-testid="pshooks-script" className="ui-mono" rows={8} spellCheck={false} value={draft.script} onChange={event => onChange({ script: event.target.value })} />}</Field>
  </>;
}
