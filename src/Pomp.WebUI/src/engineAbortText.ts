import type { Translate } from './i18n';

const ABORT_LINE = /^(?:Request was aborted|Interrupted by user|Host tool "[^"\r\n]+" was aborted)(?: \([^\r\n]*\))?(?=\r?$)/mu;

export function isEngineAbort(text: string): boolean {
  return ABORT_LINE.test(text);
}

/** Translate only the engine's abort states; leave arbitrary model/tool output intact. */
export function engineAbortText(text: string, t: Translate): string {
  return text.replace(/^(?:Request was aborted|Interrupted by user)(?: \([^\r\n]*\))?(?=\r?$)/gmu, t('bridge.stopped'))
    .replace(/^Host tool "([^"\r\n]+)" was aborted(?: \([^\r\n]*\))?(?=\r?$)/gmu, (_line, name: string) => t('error.hostToolStopped', { name }));
}
