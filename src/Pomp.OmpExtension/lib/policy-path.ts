/** Conservative names for policy prefixes/targets, excluding Windows aliases and device paths. */
export function validatePolicyRelativePath(value: string): void {
 if (!value || value.length > 4096) throw new Error('Invalid relative policy path');
 for (const part of value.replaceAll('\\', '/').split('/').filter(p => p !== '' && p !== '.')) {
  const stem=part.split('.')[0].trimEnd().toUpperCase();
  if(part.length>240||part==='..'||part.endsWith('.')||part.endsWith(' ')||/[\x00-\x1f\x7f-\x9f:*?"<>|~]/.test(part)||['CON','PRN','AUX','NUL','CONIN$','CONOUT$'].includes(stem)||/^(?:COM|LPT)[1-9¹²³]$/.test(stem))throw new Error('Use ordinary relative path names, without Windows devices, streams, aliases or traversal');
 }
}
