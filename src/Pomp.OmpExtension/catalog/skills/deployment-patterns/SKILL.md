---
name: deployment-patterns
description: "Ship safely: rollout strategy, pipeline shape, health signals, config validation, rollback, readiness review, workload orchestration basics. Use for CI/CD design, release prep or deploy audits, or says /skill:deployment-patterns."
---

# Releases that behave (pomp catalog)

Blueprint or audit the road from merge to production: fitting rollout tactics, pipeline proof per stage, runtime health voices, configuration supply and scrutiny, and the exit route when a release sours. Designing and auditing only — never executing. Publishing, artefact pushes, and production mutations await an explicit order naming the target. Image and Compose craft is `skill://docker-patterns`; cloud posture is `skill://security-review/cloud-and-infrastructure`; branch disposition is `skill://finishing-a-branch`.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Routine

1. Chart the status quo: hosting target, artefact kind, pipeline files, environments, approvers, today's rollback duration. Live-environment truth is unknowable from files — stamp such items "needs human confirmation".
2. Fit the rollout tactic to risk and platform (section 2). Ballots belonging to the user become `present_case_feedback` cards (the `case-review` skill); tactic sketches become SVGs via the `diagram` skill.
3. Stage the pipeline as mounting proof. Per pull request: static analysis, build, unit plus integration tests, preview environment optionally. Past merge: repeat, then forge the deployable artefact a single time, land it on staging, smoke-test it (see `skill://e2e-testing`), collect mandated approvals, ship to production, verify. Promote the identical artefact across environments — tested equals shipped; configuration alone varies.
4. Voice health three ways: a cheap liveness probe (the process answers), a readiness probe (serving dependencies reachable), optionally a rich internal status page signalling degradation via failure codes and withheld from the public. Busy-yet-healthy instances drain; they are not shot. ASP.NET Core bundles a health-check framework with distinct liveness and readiness lanes.
5. Source every environment-specific value from environment or secret store, never code; validate required settings at boot and refuse to start when wrong — misconfiguration then perishes in the pipeline, not before users; document each knob.
6. Engineer the retreat: tag and keep the previous artefact; hold schema compatible across at least one release (see `skill://database-migrations`) so the app rolls back solo; gate risky behaviour behind switches flippable without deploying — shipping code and releasing behaviour stay decoupled; alarm on post-release error and latency regressions; rehearse the retreat on staging.
7. Walk the readiness review (section 3) ahead of first production and major changes, checking items off in the `todo` tool; log unknowns.

## 2. Tactics

- Rolling: instances replaced progressively; zero downtime with gradual exposure, at the price of dual-version coexistence demanding two-way compatibility. The default.
- Blue-green: twin full environments with instant traffic cutover; fallback in a heartbeat and a clean seam, bought with doubled iron mid-switch; for critical services and hairy cutovers. Some platforms swap deployment slots to the same effect.
- Canary: a trickle of genuine traffic to the newcomer, widened on healthy signals; faults surface under true load; demands traffic-splitting plus sharp monitoring; for hot services and chancy changes, sharper still with switches.
- Desktop and extension wares: staged channels or rings (insiders, pre-release flag, broad availability) with pause and rewind levers.

## 3. Readiness review

- Shippable: green tests everywhere; reproducible builds from pinned inputs; secrets nowhere in code or settings; boot-time settings validation; honest health endpoints.
- Observable: rate, latency, and error metrics exported; structured, personal-data-free, searchable logs; error-threshold alarms; uptime watch on the health endpoint.
- Survivable: resource ceilings and scaling bounds; TLS throughout; a written, rehearsed rollback; migrations proven on production-scale data; backups.
- Defensible: dependency vulnerability sweep; permitted origins and throttles; verified authentication and authorization; baseline security headers.
- Operable: runbooks for the probable failures; on-call and escalation charted.

Check it off as a `todo` list; unknowns recorded, never wished away.

## 4. .NET packaging

Publishing balances size, startup, and portability (framework-dependent against self-contained, optional trimming and ahead-of-time paths); archive symbol files for post-mortems, managed and native. Hosted pipelines natively restore, build, test, and publish.

## 5. Orchestrated workloads in brief

Where containers land on an orchestrator rather than bare hosts, hold workload definitions to this bar. Live-cluster writes (applies, deletes, scale-to-zero, rollbacks) wait for explicit approval — announce the active context and namespace first, and favour dry-run plus diff. Secret contents never print; without cluster access, review files.

1. Several replicas; rolling updates never dipping under desired capacity; immutable image references, floating tags banned; a graceful shutdown window; standard labels (app, version, environment).
2. Pod lockdown: non-root user and group, escalation barred, read-only root with in-memory scratch for writers, capabilities fully dropped, a restrictive syscall profile.
3. Requests plus limits on each container — requests steer scheduling and autoscaler arithmetic, limits shield neighbours. Measure to size; hug requests with memory limits for predictability; wield CPU limits gingerly, since they throttle.
4. Three probes: startup spanning slow boots (retiring crude initial delays), liveness sensing only a wedged process — downstream dependencies banned, lest one database hiccup reboot the fleet — readiness advertising "route to me".
5. Reach and config: cluster-internal by default; external ingress with TLS and automated certificates solely where justified. Plain values in config maps; sensitive values in secrets, recalling that stored secrets are merely encoded short of encryption-at-rest or an external manager.
6. Least privilege: one service account per workload; cluster-API tokens unmounted unless the app phones the cluster; then namespace-scoped roles with minimal verbs and, ideally, named resources; cluster-admin bindings shunned.
7. Staying power: autoscaling (requests mandatory; production floor of two), disruption budgets for critical or stateful loads, per-namespace quotas on shared clusters. Batch jobs terminate (on-failure or never, not always); schedules bound history and concurrency.
8. Symptom triage: restart loops — prior logs, events, then command, env, config, probes, memory ceiling; pull failures — tag, registry secret, egress; pending — capacity, selectors, taints, orphaned storage claims; memory kills — raise the ceiling or plug the leak; traffic woes — selector-label match, readiness, endpoints, ports, port-forward probes.

Finished when: stages span build, tests, artefact, staging, smoke, and production verification; one artefact promotes throughout; the retreat is named and drilled; liveness and readiness speak separately; boot validates config; the readiness review leaves no critical unchecked.
