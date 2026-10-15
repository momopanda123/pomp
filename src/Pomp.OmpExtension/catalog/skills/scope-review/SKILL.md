---
name: scope-review
description: "Put a written plan under product-level scrutiny before it is built: is it the right problem, the right size, worth building. Use when the user wants a plan challenged or its scope settled, or says /skill:scope-review."
---

# Scope review (pomp catalog)

Judge a written plan, spec or proposal as a product decision before it gets built: does it attack the right problem, is its size proportionate to its value, will it survive contact with real use? Your output is a verdict plus a settled scope — in, deferred, out — not changes to the project.

Neighbouring skills: grilling leads the user through open decisions question by question; this one runs the analysis itself and brings the user only route choices and findings to rule on. Code review searches written code for defects; here code is consulted solely to establish what already exists, and the plan itself is what stands trial.

Stay read-only: inspect code and history, sketch figures, change no project file. You may create only diagram files and, on request, a markdown record of the review (a user-named workspace folder, else `docs/`). Use the `diagram` skill for SVG figures; quick sketches may use Mermaid in a card. Put the passes in section 7 on the `todo` tool so each gets its verdict.

## 1. Learn what exists

1. Map every sub-problem the plan mentions onto what the repository already provides; flag whatever the plan would duplicate.
2. Glance over recent history on the affected paths (read-only `git log` through bash).
3. For large plans, farm independent areas out to `task` subagents — the built-in reviewer agent role if this session offers one, otherwise a general subagent — and reconcile the findings.

## 2. Interrogate the premise

- Might the stated problem be a proxy for the real one? Name the outcome the requester seeks, and ask whether this plan is the shortest credible path there.
- Picture the plan cancelled: who is genuinely harmed?
- Advising "stop this and pursue that instead" is an acceptable result.

## 3. Sense of direction

Draft three brief snapshots — the project now, the project once this plan lands, the project a year hence as it should be — and judge whether the plan pulls toward that future or off course.

## 4. Weigh alternatives

Offer two or three substantially different approaches, one of which is the leanest that could work and one the strongest long-term shape. Cover for each: a label, a single-sentence gist, the cost (small / medium / large / extra-large), the risk (low / medium / high), what speaks for and against it, and what it reuses. Recommend one and give the reason. Present them with `present_case_feedback` cards (see the `case-review` skill) and halt — proceed only after user comments pick a direction.

## 5. Fix the ambition

Settle on one posture with the user and keep it unless you announce a change:

- Stretch: reach for more; each addition stands as its own proposal.
- Curated stretch: today's scope is the minimum; further ideas arrive as a menu.
- Hold: the scope is sealed; effort goes into making it robust.
- Trim: keep only what the core result needs; defer the remainder.

Rules of thumb: a fresh capability invites stretch; improving an existing one invites curated stretch; repairs and restructurings invite hold; plans sprawling across far more than a dozen files invite trim.

Then work the posture: under stretch, imagine the ideal, hunt for large gains at small extra cost, and gather delight ideas; under curated stretch, do the hold pass first and then the menu; under hold, probe complexity and isolate the minimal viable change set; under trim, draft the cut list and schedule the rest for later.

## 6. Walk the build in advance

Rehearse the probable stages — setup, main logic, integration, testing and polish — and drag into the light each choice an implementer would otherwise make alone. Answer it or file it as an open question.

## 7. Examination passes

Close each pass with a verdict: "nothing found" passes, but "not applicable" without examination does not. Cast every finding as a decision — the issue, the candidate answers, your recommendation. Unconnected findings can share one card set; linked ones proceed sequentially.

- Construction: where components divide and how tightly they couple; every data movement stressed three ways (absent value, empty value, failed upstream call); state machines; how to roll back; the public contract (compatibility, shapes, versioning, limits); storage (migrations, schema, indexes, integrity rules, query patterns).
- Breakage: enumerate failure points, each with its concrete kind, trigger, handler and user-visible result (a catch-all handler smells); awkward moments (double activation, mid-action abandonment, sluggish or dropped connection, stale state, extreme input lengths, empty results); the new threat surface (inputs, secrets, authorization, dependencies, injection paths, sensitive data, audit trail); behaviour under tenfold and hundredfold load.
- Assurance: map tests onto each new flow, background or scheduled task, external integration and failure path — state the test kind per item and mark bare spots; operational readiness: fresh signals, alerts, runbooks, plus an answer per new path to "what tells us this broke" (observability is part of the work, not garnish).
- Craftsmanship: organisation, repetition, naming, how evenly errors are handled, excess and shortfall, complexity. For UI-bearing plans: the sequence the eye follows; loading, empty and error states; behaviour on small screens; accessibility; harmony with the established idiom; consequences for user trust.

## 8. Working principles

Let reversibility govern speed: reversible calls go fast on roughly 70 percent confidence; only big irreversible ones earn deliberation. For each success question, pose its failure mirror. Regard focus as subtraction — ruled-out items shape the result as much as included ones. Mistrust stand-in metrics: do they still mirror user value? Document the why behind hard calls; prize small inputs with large effects.

## 9. Wrap up

- Illustrate the non-trivial (flows, state machines, dependency sets) through the `diagram` skill.
- Defer deliberately: each postponed item carries its reason and the event that would revive it — never a bare "later".
- The closing record: chosen posture, the three keenest challenges, the recommended direction, settled scope, deferrals with reasons, explicit exclusions, and one status: blocked for missing information, complete, or complete with open concerns.
- If an implementation plan emerges, mirror it into the todo/plan tool (Work › 진행) where the user can track and edit it; the markdown file remains the secondary copy. If the user wants this examination repeated on future plans, direct them to POMP workflows (Tools › 지침 › 워크플로, `/workflow`) rather than inventing a checklist.

Completion checklist: posture agreed; alternatives compared; four pass verdicts recorded; each non-trivial flow drawn or excused in writing; each finding resolved (accepted, deferred, rejected); in/deferred/out lists written; project files untouched.
