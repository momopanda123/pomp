---
name: systematic-debugging
description: "Find and confirm the true origin of a defect before editing: observe, compare, one hypothesis at a time, then fix once and prove it. Use for failing tests, bug reports, and surprising behaviour; not for building new features, or says /skill:systematic-debugging."
---

# Systematic debugging (pomp)

Stop guessing at patches: for every defect, pin down and confirm its true source before changing code, then fix that source once and prove the fix. It pays off most precisely where shortcuts beckon: time pressure, a tempting one-liner, dead earlier tries, sunk hours, or an insistent voice prescribing the cure. Trivial bugs get no pass; on them the method runs fast. Apply this skill when its trigger clearly matches; leave trivial requests alone. Not for building new features; a stop-gap the user demands gets labelled a workaround — never called the fix — while the cause stays open.

## 1. Stages (finish one before the next)

1. **Observe.** Start from facts the host already keeps: messages via `read_error_list`, failing-test details via `manage_tests`, runtime logs via `read_output_window`. Study each error and warning end to end — full trace, file, line, code; the answer is often named outright. Reproduce it at will with precise steps; while it stays flaky, collect facts instead of theories. When essentials are missing, ask a single pointed question per turn — never a battery of them. History triage: ask if it ever worked. For a suspected regression, inspect the implicated files' recent history (read-only git log over those paths; blame the suspect lines; plus a `manage_git` diff of uncommitted changes): the cause lives in the good-vs-bad difference. Bisecting for real relocates the checkout — ask the user first, and have them save open work. For cross-component failures, plant short-lived probes on every boundary logging inputs, outputs, config arrival, and inner state. One probed run exposes the guilty boundary; study that one alone. Deep-stack failures get backward tracing (section 3).
2. **Compare.** Find a healthy counterpart and study it whole, not skimmed. Tabulate all deltas between healthy and broken, however small, plus the broken side's dependencies.
3. **Hypothesise.** Voice a single hypothesis plus its grounds: this source, because of that evidence. Probe it with the smallest edit that settles it, one variable per experiment; never pack multiple fixes into a run. If refuted, frame a fresh hypothesis instead of stacking edits. Admit ignorance openly, then research or ask; never bluff. Keep a visible log of dead hypotheses (the todo tool does nicely). Three dead hypotheses in a row ends guessing: tell the user the fault might be structural, and present three exits: chase one fresh, plainly stated hypothesis; hand the case to someone who knows the codebase; leave the probes behind to catch the next occurrence. Outside lookup stays off by default; with both a search tool on hand and the user's consent, look up just the generic error category — hostnames, paths, and data scrubbed away, never workspace specifics.
4. **Correct.** Start with a failing automated reproduction (see test-driven-development if switched on); then a single fix striking the source, minus incidental tidy-ups; then the proof: reproduction green, suite unharmed, original symptom gone — verify with the host build/test tools before claiming success. Footprint check: a fix spanning more than roughly five files usually misjudges the cause or hides a redesign — stop for the user's go-ahead first. Remove every temporary probe. Track dead fixes: under three, loop back to stage 1 with fresh facts; at three, halt patching and interrogate the design — fixes begetting new faults elsewhere, each wanting wider surgery, each baring hidden coupling mean the design itself is suspect — consult the user before trying again.

## 2. Match recurring families

Before theorising freely, match the symptoms against recurring families — the match suggests a hypothesis, never a verdict:

- environment drift: passes here, fails there — target framework, flags, configuration, data, or versions;
- boundary failure: timeouts and odd replies at component or service seams;
- timing: flaky, order-sensitive, shared mutable state (awaiting async work on the UI thread);
- state integrity: half-applied updates, callback or transaction ordering;
- absent values: null or optional values used unguarded (watch nullable warnings);
- stale data: caches, reused computations, stale build output.

Defects clustering in one file group point at design, not luck — check the file history, past reports, and any known-issue list the project keeps.

## 3. Trace backwards

Open at the failing line; ask who called it with what; ascend the callers to the spot where the poisoned value or state was born; mend it there. Where reading falls short, instrument briefly before the risky spot — not after the crash — recording arguments, folder, environment, timestamp, and a full stack trace. In test runs send diagnostics to standard error. When an unknown test pollutes shared state, bisect with name-filtered group runs to corner the polluter.

## 4. Guard every layer

One fresh check alone can be dodged — via other callers, future refactors, or doubles that skip it. After fixing the source, walk every stage the data crosses and shield each: entries (toss bad values immediately), the operation core (sanity for its own job), context awareness (decline perilous acts in touchy settings), and after-the-fact logs. Test each shield by attempting to sneak past its predecessor. Goal: render the bug class impossible by construction, not just patched.

## 5. Wait for conditions, not durations

Flaky timing-sensitive tests usually stem from hard-coded sleeps picked by guesswork. Swap fixed sleeps for polls on the true condition — arrival, state, count, file presence — ticking fast, with a compulsory timeout whose error names the awaited thing, re-reading live state each round. In C# tests prefer awaiting a completion source or event with a timeout over any fixed wait. Only exception: timing as the actual subject (debounce, throttle) — await the trigger first, then hold for a span computed from known timings, noting why.

## 6. Report

Close with a chat report using fixed fields: what was seen, what caused it, the change with its files, the proof, where the test lives, links to earlier incidents or structural remarks, and a status of complete, complete-with-caveats, or blocked-and-escalated. Deliver it in chat; only write it to a dated workspace file or send it as a `propose_knowledge` note when the user asks for a kept record.

## Evidence in POMP

Draw evidence from the host: build/runtime text from `read_error_list`, steady reproductions from `manage_tests`, live state from the debugger set (`manage_breakpoints`, `control_debugger`, `get_debug_state`) where offered, and runtime logs from `read_output_window`; otherwise bash with name-filtered `dotnet test`. Launching the debugger or the app disturbs the user's live session — get consent first unless asked, and clean up breakpoints you planted. Should the trail truly end at environment, timing, or a third party, log the ground covered and fit handling (retries, timeouts, clear errors) with logging for next time. "No cause found" is usually declared too soon.

Warning signs: patch first, understand later; multiple changes in one run; cures proposed before the data is traced; trying yet again after two failures; user pushback asking for proof — return to stage 1. Compressing a stage is allowed; omitting one is not.
