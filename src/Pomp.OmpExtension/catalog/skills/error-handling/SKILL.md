---
name: error-handling
description: "Classify failures, translate them at boundaries, retry wisely with breakers, and write safe user-facing text. Use for error design, resilience review, retry logic or failure wording, or says /skill:error-handling."
---

# Failure, by design (pomp catalog)

Settle how software signals, ferries, handles, and presents failure: silence nowhere, programmatic reactions for callers, safe wording for users, full context for operators. HTTP payload silhouettes are `skill://api-design`; exposure risks are `skill://security-review`; root-cause technique is `skill://systematic-debugging`.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Ground rules

1. Raise each failure where its meaning is understood; neither postpone nor camouflage it.
2. Errors are structured data — stable code, safe message, optional details — never bare strings.
3. Users and developers read different texts: the kind one travels out, the thorough one lands in logs.
4. Every catch either handles, translates and rethrows, or logs with context. Empty catches are defects.
5. Client-visible codes belong to the contract: catalogue and version them.

## 2. Routine

1. Sort failures: anticipated conditions (validation, missing, conflict, unauthorized, throttled, dependency down) versus genuine faults (bugs, corrupt state). Anticipated ones return or throw dedicated types; faults bubble to the outermost handler.
2. Pick the carrier per scene: result values where failure is routine and nearby (parsing, lookups, user input); exceptions where failure must unwind frames or arrives from the platform; a single idiom per layer. Native code honours its incumbent idiom (exceptions, status codes, expected-style returns) and never blends two in one module.
3. Stamp stable codes on error types and wrap causes, preserving the original story. Hold transport concerns (HTTP statuses) outside domain types, mapping codes at the edge — domain logic then serves web, CLI, and queue callers equally.
4. Translate centrally at the edge: known errors become responses or UI messages, everything else a generic fault, with the complete tale plus correlation id logged.
5. Retry solely transient failures on idempotent (or key-guarded) operations — retries of side-effecting calls duplicate effects; back off exponentially with jitter to desynchronize client waves; cap attempts and overall time; obey server retry hints; never retry caller errors; set timeouts before anything else.
6. Break circuits against persistently failing dependencies: closed, open past a failure threshold, half-open probes; breakers shield the dependency and liberate caller threads. Pair with fallbacks and bulkheads. Resilience libraries bundle retry, timeout, breaker, and hedging as composable policies attachable to HTTP client factories.
7. Address users with brief actionable sentences per code, a generic fallback for unknown codes, exception text never; hand over the correlation id for support.
8. Give background and detached work a top-level handler of its own — nobody awaits it.

## 3. .NET and native mechanics

- Rethrow bare to preserve the stack; filter catches conditionally; spare deep library code from base-exception catches. Flatten parallel-work aggregates before mapping.
- ASP.NET Core funnels translation through exception-handler middleware or handler services with problem-details output; desktop apps wire dispatcher, thread, and unobserved-task hooks against silent loss or crashes; on current runtimes an escaping background-service exception can halt the entire host by default.
- Native: destructors and cleanup paths never throw; scoped resource guards keep early returns and exceptions equally safe; C-API and COM borders convert codes into house style immediately.
- Cancellation is routine flow: let it unwind quietly and keep it out of fault logs — nobody gets paged over a closed window.

## 4. Sweep

Hunt empty catches, catch-alls, fire-and-forget calls, stringly errors, unconditional retry loops. Surface findings through host diagnostics (`read_error_list`, `read_output_window`) where offered.

Finished when: empty and log-and-ignore catches are gone; boundaries translate; user text hides internals; logs chain causes with correlation ids; retries are conditional, capped, and jittered; async faults reach handlers; public codes are documented and tested.
