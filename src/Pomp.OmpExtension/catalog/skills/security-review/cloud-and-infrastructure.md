# Cloud and infrastructure security (security-review)

Open this page for infrastructure-as-code, role and permission definitions, pipeline files, deployment settings, or logging and backup configuration — and ahead of a first production release. Read repository files only. Any step touching a live account — querying or changing — waits for the user's explicit per-action approval, favouring preview forms (what-if, plan) over applies. Files alone can never confirm live cloud state; the report must admit that.

## Routine

1. Name the provider(s) and the artefacts at hand: templates, pipeline definitions, deploy scripts, app settings. Ask which environments count as production.
2. Tour the areas below, pinning each setting — or its absence — to file and line.
3. Order findings by internet exposure and blast radius first: public stores, wildcard grants, plaintext secrets outrank everything; hygiene trails last.
4. Recommend the gentlest tightening that keeps deploys green (trimmed actions, trimmed resource scope, private network paths) and warn what might break.
5. Emit a pre-release gate list, flagging items answerable only in the live console.

## Ground to cover

- Identity and privilege: owner identities retired from daily use; second factors on every privileged human; workload identities instead of durable keys; grants narrowed by action and resource, wildcards nowhere; recurring access reviews purging dormant credentials.
- Secrets and keys: confined to the platform secret store — never code, images, plain settings, or logs; rotation automated where offered; secret access itself audited. A managed secret store beats plain environment variables.
- Network: databases and internals unreachable from the internet; admin ports gated behind bastion or VPN; enumerated, narrow inbound rules; restrained outbound where feasible; flow logging enabled.
- Detection: login failures, admin actions, and permission changes landing in central, tamper-resistant logs kept as long as the compliance regime demands (on the order of 90 days); anomaly alerts wired.
- Delivery pipeline: federated short-lived credentials displacing stored cloud keys — nothing durable left to steal or to forget rotating; a minimal token for the pipeline itself; secret, dependency, and image scanning as gates; protected branches, mandatory review, signed commits or artefacts where policy insists.
- Edge: application firewall with managed rules, throttling, bot and DDoS shields, strict TLS, hardened response headers.
- Resilience: automated backups with fitting retention, point-in-time recovery, deletion guards, and a written recovery plan whose objectives survived a genuine restore — untested backups are wishes, not safeguards.
- Recurring misconfigurations: world-readable object storage; internet-facing managed databases; ingress open to all; wildcard roles; unencrypted stores; factory credentials.

## Microsoft-flavoured mappings

Entra ID roles plus managed identities for workload identity; Key Vault for secrets and rotation; network security groups, private endpoints, and firewalls for network; Azure Monitor with Log Analytics for detection; Defender for Cloud for posture; workload identity federation for hosted pipelines over stored service principals; Front Door or Application Gateway with WAF at the edge; Azure Backup and geo-redundant storage for resilience.

Watch Visual Studio publish profiles — per-user files can harbour deploy credentials — and settings files that may embed connection strings: exclude both from version control and choose identity-based access (managed identity, federated sign-in) over embedded keys.

Finished when: findings cite file and setting; severity tracks exposure; proposals are the narrowest viable; console-only items stand apart; nothing echoes credentials or account identifiers beyond what the user already shared.
