---
name: csharp-testing
description: "Choose the cheapest .NET test layer and write deterministic tests with clear failures: units, in-process host tests, real-engine persistence tests, F# extras. Use when adding or reviewing C#/F# tests or chasing flakes, or says /skill:csharp-testing."
---

# .NET tests worth keeping (pomp catalog)

Tests that lock in observable behaviour, execute quickly and repeatably, and point at the cause when red. The failing-first loop is `skill://test-driven-development`; this skill adds the .NET mechanics. Browser journeys are `skill://e2e-testing`; migration safety is `skill://database-migrations`.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Fit the solution

Survey the test projects, framework (xUnit, MSTest, NUnit), doubling library, assertion flavour, and naming habit first; write in that house style. One framework per solution — a second doubles tooling and habits. A class instance per test (xUnit) makes the constructor setup and disposal teardown; class and collection fixtures share costly state deliberately, since parallel classes punish shared statics with flakes.

## 2. Spend the cheapest layer that bites

- Pure logic: plain unit tests, faked collaborators.
- HTTP behaviour: in-process host tests with chosen services swapped (the test project must see the app's entry-point type).
- Persistence or SQL: a genuine engine of the production family. The in-memory stand-in lacks constraints, transactions, and relational translation, so SQL behaviour needs a lightweight real engine or a container. Container tests need a running runtime (see `skill://docker-patterns`) plus tags excluding them from quick runs.

## 3. Compose tests

1. Setup, one action, assertions on the result. Name by behaviour plus condition; one logical expectation each (several asserts over the same outcome are fine). Red then reads as a sentence about the breakage.
2. Tabulate input variations as theory-style cases: inline values for scalars, member or class sources for objects. Negative cases earn dedicated rows pinning the expected failure kind or message.
3. Double by need: stubs supply return values; verified calls only when the call is the behaviour itself (a stored write, a published event) — interaction assertions beyond that snap on innocent refactors; fakes carry stateful doubles.
4. Container-backed suites: boot the engine during async setup, lay down the schema, share a single instance per fixture for speed, dispose in async cleanup, and isolate data per test with its own rows or transaction. Shared fixtures sell isolation for time — spend consciously.
5. Inject time, randomness, and ids (a fakeable clock), and seed generators. Deterministic inputs kill the commonest flakes at the root.
6. Async tests return `Task` — async void has no place here. Exercise cancellation: a cancelled token should surface the cancellation exception, or the token should demonstrably reach the dependency.

## 4. Execute

Start from the tightest filter, widen to the project, then the solution; coverage last, through the project's own collector. Use `manage_tests` when offered (Test Explorer terms, `runsettings` for parallelism and timeouts), else `dotnet test` via bash. Live unit testing is an IDE edition extra — never a dependency.

## 5. Review, flakes, licences

Reject: tests of internals, shared mutable state, sleeps in async tests (await the real condition with a timeout), assertions on rendered strings, sprawling multi-purpose assertions, untested cancellation. Static extension logging evades mocks — fake the logger when log output is behaviour. Assertion-library licences can shift between majors at the version pinned here; framework asserts remain the fallback.

## 6. F# notes

Property-based checks suit functions with crisp invariants (usable from C# too). Records of functions fake more easily than interfaces. Quotation assertions echo the entire failing expression. Backtick identifiers allow sentence-long test names.

Finished when: fresh tests go red on broken behaviour and green otherwise; nothing sleeps, and nothing leans on order, clock, locale, or network; slow or container-backed tests carry labels; filtered, project, and full runs all pass.
