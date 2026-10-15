---
name: cpp-testing
description: "Write, wire and fix native C++ tests: framework inventory, isolation, per-case discovery, failure triage, coverage and sanitizers. Use for C++ unit tests, adapters, flaky native suites or checker builds, or says /skill:cpp-testing."
---

# Testing native C++ (pomp catalog)

A dependable routine for native suites: adopt the project's framework, isolate tests until repeatable, surface every case individually in the runner, chase failures to their cause, and switch on coverage and runtime checkers when requested or when the symptoms warrant. Failing-first discipline is `skill://test-driven-development`; root-cause method is `skill://systematic-debugging`.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Survey, then scope

1. Survey the landscape: the incumbent framework (GoogleTest/GoogleMock, Catch2, doctest, Boost.Test, or Microsoft's native framework), build system (CMake plus CTest, MSBuild projects), folder layout, discovery mechanism. Extend it; do not replace it.
2. Separate layers: unit tests for lone logic, integration tests for components touching files or sockets, fixtures in their own folder — distinct targets or labels so each layer runs alone. MSBuild projects need new C++ files registered; CMake globs absorb them.
3. Dependencies stay reproducible: favour the established package manager or a vendored copy over configure-time fetching, which needs network and a pin. Anything downloading tools or binaries needs the user's explicit approval first — describe, never fetch. This skill ships no scripts.

## 2. Isolate

1. Feed dependencies through interfaces or constructor arguments; reach for fakes on stateful collaborators and mocks only to nail an interaction — mocks couple to call order, fakes to outcomes. Mocking demands virtual (or template-injected) seams; carve a narrow interface first where substitution is impossible.
2. Shun globals; fixtures reset the unavoidable ones. Hand each test a private scratch directory and delete it after. Banish clocks, sockets, and disk from unit tests; freeze random seeds. Synchronise threads with primitives and bounded waits, not pauses.
3. Assert fatally on preconditions later lines need, non-fatally on independent checks, so a single run surfaces multiple issues. Parameterized and typed tests tabulate case matrices.

## 3. Discover, run, triage

1. Expose every case individually — CTest build-time discovery, the Visual Studio adapter, CMake presets or Open Folder flows — never one opaque binary. Failures then name their case, and reruns cost little.
2. Run narrow to wide: one case by name filter, its suite, then the full set with failure output on. Prefer the host runner (`manage_tests`) when offered; `ctest` via bash stays for CMake folders.
3. Triage drill: rerun the lone failure; plant scoped logging by the assertion; rerun under the address checker; broaden only once the cause is repaired. Repeat and shuffle flags smoke out order coupling and flakes.
4. Exercise both debug and release — undefined behaviour and timing faults often haunt one. Under MSVC the test binary's runtime-library mode must match the framework's, or linking fails.

## 4. Coverage and checkers

1. Coverage: instrument the test target only — global flags perturb the shipped build and poison comparisons. Baseline and compare under identical flags; strip system headers from reports. In-IDE coverage is an edition extra; compiler instrumentation and outside tools cover the rest.
2. Checkers: offer each as an opt-in build — they slow runs and shift layout. Memory and undefined-behaviour checkers coexist in one build; the thread checker demands its own, being incompatible with the memory checker. MSVC carries the address checker; thread and undefined-behaviour checkers live mostly in clang-driven toolchains. Windows-side leak and corruption hunts lean on the CRT debug heap plus Application Verifier.
3. Fuzzing and property tests reward pure parsers and invariant-rich functions — only where the project already owns the tooling.

Finished when: fresh tests fail for the right cause on broken production code; filtered, suite, and full runs pass; repeated and shuffled runs hold green; no stray temp files; an ordered checker build is clean; no fresh compiler warnings at the project's level.
