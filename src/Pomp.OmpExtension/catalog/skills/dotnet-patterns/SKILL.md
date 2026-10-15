---
name: dotnet-patterns
description: "Everyday C# and .NET craft: explicit data shapes, async end to end, options validation, and a review-smell list. Use when writing or reviewing C# or restructuring a .NET service layer, or says /skill:dotnet-patterns."
---

# Everyday C# and .NET craft (pomp catalog)

C# guidance for code that says what it means, stays correct under concurrency, resists misuse, and stays testable. The project leads: copy naming, DI habits, logging, and error conventions from the files under change, and consult this skill only where the project is silent. Each section names the project kinds it fits — desktop, VSIX, and console work skips the ASP.NET-only parts. Visual Studio extension code always marshals to the UI thread; the ban on blocking waits applies there too.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Start from the project's settings

1. Before proposing anything, find the target framework, language version, nullable context, analyzer set, and `.editorconfig` severities (the `vs-project` skill says where). Suggestions needing a newer language or runtime are on the table only when the project can move there. Formatting is not prose here: `.editorconfig`, analyzers, and `dotnet format` enforce it mechanically.
2. Conventions in the touched files outrank every default below.

## 2. Data shapes and collaborators

1. Choose shapes first: records, init-only or constructor-set members, and required members where the language allows, for values and transfer objects; reserve mutable classes for things with identity and a lifecycle. Immutable values travel across threads without locks and give predictable equality and caching.
2. Hand collaborators in through constructors; a class never constructs its own. Add an interface only at a seam where substitution earns its keep — I/O, clock, randomness, outside services — and register it in the container. One interface per class with no second adapter is speculative indirection (seam vocabulary: `skill://codebase-design`).
3. Lifetimes must compose: a singleton holding a scoped dependency traps it past its scope — the host flags this in development. Build the object graph in one auditable place.

## 3. Async, cancellation, settings

1. Keep async whole: no blocking on tasks, no fire-and-forget. Awaiting several tasks together rethrows only the first failure; the others wait in the combined task's exception collection.
2. Pass the caller's cancellation token into every awaited call, so abandoned requests stop spending resources. Launch independent calls together and await the group; cap the fan-out for big inputs.
3. Bind settings to typed options from named sections (snapshot, per-scope, or live with notifications) and validate at startup — a wrong value then kills the process at deploy time, not at the first request that stumbles on it.

## 4. Failures, queries, HTTP

1. Return expected outcomes (bad input, missing item, conflict) as values carrying an error kind rather than bare strings; reserve exceptions for the genuinely unexpected (full taxonomy: `skill://error-handling`). Guard arguments at the boundary; guard clauses keep the main path flat.
2. Query lean: read-only queries skip change tracking and fetch only needed columns; hunt per-row queries inside loops. A context is scoped and single-threaded; split queries relieve row multiplication from several collection includes.
3. ASP.NET Core only: group related routes and authorize the group; return typed outcomes spelling out the result (creation with a location header, resource-missing and bad-request variants with bodies). Put timing, correlation ids, and error mapping in the pipeline, arranged so post-processing still runs when downstream throws. Order matters: exception handler first, authentication before authorization.

## 5. Prove it

Build and test with the host tools (`build_solution`, `manage_tests`) when offered, otherwise `dotnet` via bash. Cure nullable and analyzer warnings at their source; never suppress to silence them. Finished when: zero new warnings; green tests; no fresh blocking waits; every new I/O method takes and forwards a token; every new registration composes with its dependencies' lifetimes.

## 6. Smells for reviewers

When reviewing, flag each with file and line, a severity (defect, risk, nit), and one concrete fix. Leave code outside the diff alone.

- Concurrency: async void beyond event handlers (its exceptions kill the process); blocking a task; orphaned background tasks; mutable statics; discarded tokens.
- Error handling: empty or log-and-continue catches; catch-alls adding no context; rethrows dropping the stack (bare `throw` keeps it).
- Design: inline-built collaborators; public fields; dynamic or reflection cleverness in business rules; over-long constructor lists (an overloaded class).
- Resources: undisposed disposables; per-call HTTP clients (share via the client factory or a long-lived instance — sockets exhaust and DNS goes stale otherwise).
- Cost: full tables loaded then filtered in memory; heavy string concatenation in hot loops.

Log through message templates with named placeholders, not interpolation; hot paths deserve source-generated log methods.
