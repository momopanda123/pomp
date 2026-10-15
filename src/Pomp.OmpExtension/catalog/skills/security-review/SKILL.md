---
name: security-review
description: "Walk a change across its trust boundaries before it ships: injection, auth, secrets, exposure — findings with evidence, fixes with tests. Use for auth, input handling, endpoints, secrets or pre-release review, or says /skill:security-review."
---

# Reviewing a change for security (pomp catalog)

Before accepting code that reaches across trust boundaries — identity, hostile input, secrets, storage, outbound calls — hunt the flaws a reviewer would block on, each backed by evidence, and repair them without dulling the feature. Cloud and pipeline hardening is the companion page `skill://security-review/cloud-and-infrastructure`. Finish with an independent review via POMP's reviewer role when the session provides it (through the `task` tool), else a plain subagent.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Draw the scope

Read the diff or the named files and enumerate what the change reaches: protected assets, ways in (requests, files, messages, command lines), the identities involved, outside systems. No list, no spine. Stay read-only unless the assignment is to fix. On big diffs, fan categories out to subagents under POMP's delegation rules and merge the returns.

## 2. Tour the categories, most exposed first

For each category locate the real code path, judge pass, risk, or defect, and record file and line. A checked-off list without locations is theatre; every finding must be verifiable by a human.

- Secrets: absent from source, sample configs, logs, build output, and history; arriving from environment or vault; distinct per environment; ignore rules covering env files. Never echo a discovered secret — cite its location and call for rotation, since one commit treats it as leaked forever.
- Input: boundary checks on type, length, range, and shape, allowlist-minded; denylists overlook variants nobody imagined. Uploads face size, declared-type, extension, and content-sniffing checks. Prefer rejection over scrubbing.
- Injection: parameterized queries always, string-built never; the same discipline for shell commands, paths, and templates.
- Identity and session: tokens and credentials stored beyond script reach (HttpOnly, Secure, SameSite cookies rather than web storage); brief lifetimes; working logout and rotation.
- Authorization: server-side, per sensitive action, against owner or role, ahead of the work; deny by default. Verifying login without ownership is the confused-deputy flaw. Client-side enforcement is decoration.
- Output: untrusted HTML allowlist-sanitized or framework-encoded; a tight content security policy with no inline-script or eval escapes; framing and sniffing defences on.
- Mutating requests: anti-forgery tokens or equivalent alongside same-site cookies.
- Abuse: throttling keyed by address and by identity, strictest on costly or sensitive operations (limit headers: `skill://api-design`).
- Exposure: logs and error bodies clean of credentials, tokens, card data, and traces; users get generic wording, server logs keep the detail (user-visible failures: `skill://error-handling`).
- Supply chain: committed lock files, CI vulnerability audits, an update rhythm, few fresh dependencies.
- Transport and crypto: TLS throughout, certificate validation never bypassed; established primitives, library password hashing, secure token randomness.

## 3. .NET and native specifics

- ASP.NET Core: deny-by-default via a fallback authorization policy; antiforgery on cookie-authenticated form posts; Razor encodes unless raw-HTML helpers opt out; rate-limiting middleware on current runtimes; user-secrets for development, environment or vault for production; restore-time package audits on recent SDKs.
- Data access: ORM queries stay safe until raw-SQL APIs meet concatenated strings; interpolated variants parameterize, raw ones do not.
- .NET hazards: unsafe deserializers and type-name-driven polymorphic deserialization of hostile data, XML readers with external entities on, user segments in path joins, server-side fetches of user-supplied URLs.
- Native C/C++: bounds and integer overflow, format strings, use-after-free; build with stack protection, control-flow guard, warnings-as-errors; annotations plus static analysis; no banned string routines; checker builds for tests (see `skill://cpp-testing`).

## 4. Findings to closure

1. Each defect ships with a one-sentence impact, a minimal repair, and the test that should have caught it: anonymous call refused, wrong role refused, hostile input rejected, floods throttled.
2. Fix only on request or when fixing is the task; otherwise report. For per-finding triage with the user, show `present_case_feedback` cards (the `case-review` skill).
3. Rebuild and retest; prove the hole closed rather than moved (client-only validation moved it).
4. Close by naming residual risks and unreviewed ground. History secret-scanning may need tooling the user lacks — describe it, never fetch it.

Finished when: findings carry locations and severities; the report leaks no secret values; each fix owns a test or a documented reason for none; build and tests pass; inapplicable categories are declared, not silently skipped.

## Companion page

- `skill://security-review/cloud-and-infrastructure`: read-only audit of cloud and pipeline posture — identity, secrets, network, logging, pipeline, edge, backups. Open it for infrastructure definitions or first-production readiness.
