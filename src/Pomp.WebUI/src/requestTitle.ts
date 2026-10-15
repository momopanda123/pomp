import type { Translate } from './i18n';
import { parseTaskApproval } from './permissionSummary';
import type { DialogRequestPayload, PermissionRequestPayload } from './protocol';

/** Title of a permission request as the card shows it (and as the record in the transcript keeps it). */
export function permissionTitle(request: PermissionRequestPayload, t: Translate): string {
  const isOmpApproval = request.source === 'ompApproval';
  if (isOmpApproval && request.toolName === 'task' && (request.tasks || parseTaskApproval(request.message))) return t('perm.task.title');
  if (!isOmpApproval) return request.title;
  // The name users see for the tool (the workflow has a display name); the remembered key itself is the raw name.
  return t('perm.omp.title', { tool: request.toolName === 'composer_workflow' ? t('tool.workflow') : request.toolName });
}

export function dialogTitle(request: DialogRequestPayload, t: Translate): string {
  return request.title ?? t('dialog.title');
}
