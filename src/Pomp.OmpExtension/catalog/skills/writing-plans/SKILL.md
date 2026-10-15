---
name: writing-plans
description: "Turn a settled design into tasks, spelled-out interfaces, and verifiable steps for a context-free executor. Use for multi-step work on an approved design, before any coding; not for single-step edits, probes, or designs still unapproved, or says /skill:writing-plans."
---

# Writing plans (pomp)

Write a plan so complete that a skilled executor unfamiliar with this repo and this design can carry it out. The plan records just what the executor cannot settle alone: change locations, names and signatures, values fixed by the design, and per-task checks. The rest stays the executor's call. Behind it: single source for every fact, no extras beyond the ask, tests first, small steps. Apply this skill when its trigger clearly matches; leave trivial requests alone.

## 1. Shape

1. **Scope check:** work spanning separate subsystems should already be split; if not, propose one plan per subsystem. Each plan must yield something runnable and testable on its own.
2. **File map before tasks:** name each file to create or touch, with the single responsibility it owns. One responsibility per file; neighbours that change together live together; split by responsibility, not by layer; follow project habits (break up an oversized file only if this work needs it). Small files mean fewer bad edits.
3. **Cut tasks** where separate judgement could land differently: a reviewer accepting one part while rejecting its neighbour means two tasks; otherwise keep them merged. Chores — docs, bootstrapping, settings, boilerplate — never stand alone; fold each into the first task needing it. Each task ends with something independently testable.
4. **Front block:** the goal in one sentence, the approach in a few, the stack, and which design this implements. Plus two lists:
   - **Global constraints:** each rule holding everywhere (versions, libraries, naming, platforms), copied exactly from the design; inherited by every task.
   - **Blind spots:** gaps where implied inputs lack test coverage: the situations most likely to harm real users first, each paired with the behaviour such a user would expect. Each blind spot earns its pinning test in the task owning that code.
5. **Task block:** files (new, touched with line ranges, tests); the neighbour interfaces it uses and offers, signatures spelled out; then the steps — add the failing test, watch it fail, add the smallest fix, watch it pass.

## 2. Step test

Apply the two-reader test to each step: if two skilled executors could read it and produce different code, pin down one more detail; otherwise leave it. Test steps carry assertions as code with the exact values from the design; code steps name signature, file, and fixed values, adding how-to text only where signature plus tests leave the logic open; check steps name the command and the success output; references to other tasks point at their interface blocks instead of repeating code. Every line must earn its place: cut anything whose absence forces no guess that matters.

## 3. Cross-check and hand off

Check that all design requirements own a task (add tasks for orphans), that names stay uniform, that each step passes the two-reader test, that each blind spot owns a pinning test. If the plan outgrows the design, shrink code blocks to declarations plus checks. Hand off: mirror the task list into the engine's todo/plan tool — it feeds Work › 진행, where the user follows and edits progress — and save the full plan where the project keeps plans or where the user wants it; the markdown file is secondary. Point the user at it, and check it matches their intent; never commit without being asked. When the plan describes a repeatable procedure, point at POMP workflows (Tools › 지침 › 워크플로, `/workflow`) as its reusable home instead of inventing another format.

## 4. Running the plan

- **Inline:** carry out every task yourself with the todo tool: one item per task, failing test first, then code, then proof.
- **Delegated:** hand each worker via `task` just its own task block plus the constraints and the neighbour interfaces it needs; check each result before starting the next. Independent tasks may be split across subagents with the `task` tool only when their file sets are disjoint, following POMP's delegation rules.
- After the last task, request one independent review of the finished change — POMP's built-in reviewer role when available, else a plain subagent. Commits stay optional: they need the user's go-ahead through POMP's approval cards, and many users commit by hand. Tests run via `manage_tests`, else `dotnet test` filtered by name; builds via `build_solution`; on legacy-style projects, registering a new source file gets its own plan item through `add_project_file`, while SDK-style projects need nothing. Order each task's steps per test-driven-development (if switched on); verify with the host build/test tools before claiming success at each verification step. If you keep a progress file for resuming after context loss, put it in a scratch spot outside tracked files.
