---
name: e2e-testing
description: "Keep a small trustworthy browser-test suite: risk-picked journeys, user-like locators, condition waits, isolation, evidence, flake handling. Use for browser tests, page objects, or flaky end-to-end runs, or says /skill:e2e-testing."
---

# Browser tests that stay believed (pomp catalog)

Grow and tend end-to-end suites that drive genuine user journeys in a real browser, cry wolf rarely, and record enough evidence for armchair diagnosis of failures. Anything cheaper layers prove belongs to `skill://csharp-testing`. Desktop automation wants UI Automation frameworks, not browser drivers; load testing and visual critique sit elsewhere.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Tool and charter

1. Adopt the project's incumbent tool — .NET shops usually run Playwright for .NET with a test adapter, or Selenium. A second tool is forbidden.
2. Charter a lean suite: a few journeys chosen by business risk (sign-in, the central create/read flow, money-adjacent flows on doubles) rather than screen-by-screen coverage. Browser minutes are dear; breadth lives in cheaper layers.
3. Browser binaries download separately from the package. Any step fetching tools or browsers needs the user's explicit approval first — describe, never fetch. This skill ships no scripts.

## 2. Authoring

1. Frame each journey as a brief story: defined start state, observable end state.
2. Find elements as users do: role, label, or visible text leads; a dedicated test-id attribute backs it up; deep CSS chains and generated class names never. User-like locators weather redesigns and double as accessibility probes (see `skill://accessibility`).
3. Encapsulate each page or component in a compact object offering intent-level actions (search for, submit order) with locators in one spot; the tests then read as stories holding the assertions.
4. Wait for states, not seconds: trust auto-waiting and retrying assertions; await a concrete response or element state where the UI hinges on a network call. Fixed sleeps are out, and so is waiting on network idle — pages poll and stream forever.
5. Isolate: every test provisions or resets its own data — API or database setup (see `skill://api-design`) beats click-through scaffolding — leans on no sibling, and tidies up. Share one saved login session instead of signing in per test.
6. Record by default: a trace at the first retry, a screenshot at failure, video kept for failures only. Artefacts land in an ignored output folder.
7. Anything touching production stays reversible; test accounts, sandboxes, and feature switches only. Money-moving flows await their confirming response patiently and never run against production.

## 3. Execution

Launch the app via the host run capability or the project's own command. Locally, observe each fresh test headed once, then headless; hammer it repeatedly and in parallel to shake out flakes pre-merge. In CI, install browsers in a separate cached step, cap retries low (infrastructure noise only), and publish reports plus artefacts even on red. Declare the wide browser matrix (Chromium, Firefox, WebKit, emulated mobile) cheaply — schedule it, while every change runs one browser.

Summarise runs briefly in chat: verdict, counts, each failure with file:line and artefact path. A shareable HTML report via the `page-design` skill appears only on request. Traces and videos open in the tool's own viewer; the skill points at paths.

## 4. Taming flakes

Reproduce solo many times, study the trace, then cure the cause: absent wait, shared data, mid-animation assertions, clock reliance, order coupling. Quarantine buys time only: skip or expect-fail with a tracking-issue link and an owner; mass retries are not a cure. CI retries still disclose the flaky verdict.

Finished when: fresh tests survive ten straight local runs plus a parallel run; sleeps are gone; failures bring trace and screenshot; tests own their data lifecycle; the suite fits the team's time budget; quarantined cases link issues.
