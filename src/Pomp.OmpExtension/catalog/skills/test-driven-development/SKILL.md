---
name: test-driven-development
description: "Start every behaviour change from a test seen failing for the intended reason, then write the least code that passes. Use for features, bug fixes, and behaviour-changing refactors; skip only with the user's consent for disposable or generated code, or says /skill:test-driven-development."
---

# Test-driven development (pomp)

Open each behaviour change with a test you have watched fail for the right reason; that is what gives a later pass its meaning. Skips need user consent — throwaway prototypes, generated code, bare configuration. A stated user preference beats this skill; for any skip, name what stays unprotected. Apply this skill when its trigger clearly matches; leave trivial requests alone.

## 1. The cycle

1. **Red:** one behaviour per test, a name declaring the expected result, the real unit under test (doubles only where slowness or outside systems demand them). C# wrinkle: a test referencing a missing type won't compile — a crash, not the assertion failure the loop demands. Add a bare stub — signature alone, body throwing not-implemented — so the maiden run dies asserting.
2. **Confirm the red run** — never skip it — by executing just this test. Demand a true assertion failure with the message you foresaw, rooted in the missing behaviour, not in a typo or broken setup. A test passing at once checks something already there; fix the test. On a crash, repair it and repeat the run until the failure is genuine.
3. **Green:** write the tiniest implementation that passes the test — no spare options, no drive-by clean-ups.
4. **Confirm green:** the new test passes, then the full suite passes with spotless output — zero errors, zero warnings. A task scoping you to one test file bounds your edits, never your test runs. Report every failing test you saw, regardless of who caused it.
5. **Refactor, only while green:** tidy duplicates, names, and helpers; behaviour stays fixed and the suite stays green.
6. **Next failing test.**

Bug fixes ride the same loop: reproduce with a failing test first (cause-hunting belongs to systematic-debugging), then fix. A fix with no failing-first test proves nothing.

## 2. Code written before its test

Exploratory code you wrote in the current task gets thrown away and the work begins from the test instead; reusing it as a crib, reworking it, or even glancing at it counts as testing after the fact, because knowing the code bends the test toward remembered cases, so such tests confirm instead of discovering. This rule touches only code the agent produced in this task — user-written code is exempt — and anything sizeable needs asking before deletion.

## 3. Honest tests

- Start by naming the production edit that should flip this test red, and judge whether that edit spells a defect or a mere design choice. If no defect suggests itself, rebuild the test around outward behaviour.
- Derive expectations away from the implementation: literal values, hand-computed fixtures, tables with results typed by hand.
- No change detectors: don't pin constants, message wording, or internal shapes; assert the behaviour that flows from the choice. Don't test scripts or documents by grepping their source; run them on prepared input and inspect output, side effects, and exit status.
- Test the guarantees your code offers at its borders; framework internals are the framework's business. Trivial accessors and pass-throughs earn tests only when they validate, convert, default, compute, rule, or show effects.
- Doubles: never assert on the double itself; swap out just the sluggish or outside layer; pin argument values, call counts, and sequence where they form part of the agreement; one fixture per path; mirror genuine data shapes completely. Keep test-only teardown in test helpers, off production classes.
- Before wrapping up, run a mental mutation pass: imagine plausible harm — flipped conditions, swapped or shifted arguments, skipped writes, silent defaults, nasty inputs (empty, blank, missing, out of range, forbidden, garbled) — and demand a test that goes red for each; harm no test catches signals unguarded behaviour.

## 4. Running tests in POMP

Prefer `manage_tests` when offered; else `dotnet test` — name-filtered for one test, bare for the whole suite. Build failures surface through `build_solution` with `read_error_list`. Legacy-style projects need new test files registered through `add_project_file`, while SDK-style projects pick them up on their own. Files open with unsaved edits in Visual Studio block disk writes — reach for the buffer tools; if those are absent, ask for a save first. With no test project around, propose creating one and await approval. Verify with the host build/test tools before claiming success.

When testing feels hard, that is design feedback: simplify the interface, pass dependencies in, share helpers — and ask the user when still stuck.
