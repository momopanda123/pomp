import type { PermissionRulesState, ProjectPolicyView, UiToHost } from '../protocol';

/**
 * Dev-only answers to `permission/rules` (Tools › 권한): remembered grants, trusted repositories and the project policy, with revoke,
 * untrust, save and delete applied to this in-memory copy. `?permissions=empty` starts with nothing; `?permissions=invalid` with an unreadable policy.
 */
export function createMockPermissions(emit: (payload: PermissionRulesState) => void) {
  const query = new URLSearchParams(window.location.search).get('permissions');
  const empty = query === 'empty';
  let revision = 1;
  let session = empty ? [] : ['omp:bash@C:\\workspace', 'host:vs_build@C:\\workspace'];
  let persistent = empty ? [] : ['omp:write@C:\\workspace', 'omp:fetch'];
  let trusted = empty ? [] : [
    { root: 'C:\\workspace', trustedUtc: '2026-10-11T03:12:00Z', files: 3 },
    { root: 'C:\\Users\\dev\\source\\repos\\OrderService\\very\\deep\\folder\\structure', trustedUtc: '2026-10-02T11:40:00Z', files: 1 },
  ];
  let policy: ProjectPolicyView = empty ? { file: 'C:\\workspace\\.pomp\\permissions.json', status: 'none' }
    : query === 'invalid' ? { file: 'C:\\workspace\\.pomp\\permissions.json', status: 'invalid', error: 'Invalid project policy rule.', hash: 'policy-bad' }
    : { file: 'C:\\workspace\\.pomp\\permissions.json', status: 'valid', requireReviewedEdits: true, requireSandbox: false, hash: `policy-${revision}`, rules: [
      { tool: '*', effect: 'allow' },
      { tool: 'bash', effect: 'allow', commands: ['dotnet test', 'dotnet build'] },
      { tool: 'write', effect: 'allow', paths: ['src/**', 'tests/**'] },
      { tool: 'fetch', effect: 'deny' },
    ] };
  return (command: UiToHost['permission/rules']): void => {
    let error: string | undefined;
    switch (command.action) {
      case 'revoke':
        if (command.scope === 'session') session = session.filter(key => key !== command.key);
        else persistent = persistent.filter(key => key !== command.key);
        break;
      case 'untrust': trusted = trusted.filter(entry => entry.root !== command.workspace); break;
      case 'savePolicy':
        if ((command.expectedHash ?? null) !== (policy.hash ?? null)) error = 'The policy file changed. Load it again.';
        else if (command.policy) policy = { file: policy.file, status: 'valid', ...command.policy, hash: `policy-${++revision}` };
        break;
      case 'deletePolicy':
        if (command.expectedHash !== policy.hash) error = 'The policy file changed. Load it again.';
        else policy = { file: policy.file, status: 'none' };
        break;
      case 'openPolicy': console.info('[mock host] open', policy.file); break;
    }
    window.setTimeout(() => emit({ requestId: command.requestId, root: command.root ?? '', sessionId: command.sessionId ?? '', session: [...session], persistent: [...persistent],
      approvalMode: 'AlwaysAsk', discoveredExtensions: false, blockNetworkTools: false, error, projectPolicy: structuredClone(policy), trustedWorkspaces: structuredClone(trusted) }), 160);
  };
}
