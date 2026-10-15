---
name: codebase-onboarding
description: "Turn an unfamiliar repository into a map, a guide and a short instruction file, optionally a persona walkthrough. Use for first sessions, teammate tours, instruction files or subsystem explainers, or says /skill:codebase-onboarding."
---

# Charting unknown code (pomp catalog)

Grasp a repository fast and cheaply, then bank the learning where people and future agents reuse it: a skimmable guide, a compact instruction file, optionally a narrated tour for a named reader. Two-file questions, implementation, design critique, and product documentation want other tools.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Four gears

- Full pass: guide plus instruction file.
- Instructions only: discovery runs, guide skipped.
- Refresh: absorb the incumbent file first, merge discoveries, flag additions and edits.
- Tour: persona-led walk across chosen code.

## 2. Discovery routine

1. Reconnoitre through search and listings, not cover-to-cover reading; parallelize independent probes — `task` subagents per territory on big repositories (build, entry points, tests, docs, CI), honouring POMP's delegation rules. Harvest: manifests, framework fingerprints, entry points, a two-level tree minus generated folders, build and tooling configs, test layout, CI definitions, container files, incumbent docs.
2. Map the architecture: languages and versions, frameworks and pivotal libraries, stores, build and CI/CD gear; the overall cut (lone app, sprawling solution, monorepo, service fleet, plugin host); interface idiom; each top-level directory's role (obvious ones excused). Project graphs come from the solution-structure host tool (`query_solution`) where offered. Then shadow one representative request or user gesture from door to disk and back, citing every file crossed. Glossary and decision records defer to `skill://domain-modeling`.
3. Read the room's habits: file and type naming, test names and homes, error idiom, injection versus statics, async idiom, logging, formatter and analyzer edicts; branch and commit manners from fresh history ("history unavailable" for shallow or empty clones). Durable conventions bound for keeping travel through `propose_knowledge`, not improvised memory files.
4. Distrust surfaces: code overrules config when they disagree — configs decay quicker. Anchor every quoted command at its definition (project, script, pipeline); build once where tooling permits.
5. Emit outputs (section 3), labelling the undetermined as such rather than inventing. Secrets in configs never migrate into outputs. Guide illustrations arrive via the `diagram` skill as SVG; a portable guide or tour may become one self-contained HTML page via `page-design`; otherwise markdown with path:line pointers in chat or a docs file.

## 3. Deliverable shapes

- Guide: two-to-three-sentence purpose; stack table; architecture sketch or paragraph; entry points; directory atlas; one request lifecycle; conventions; everyday tasks with genuine commands; a "where do I look to..." index; open questions. Two-minute read; the README stays unrepeated.
- Instruction file: stack digest, observed style edicts, test/build/run commands, project atlas, workflow manners — capped near a hundred lines since every session loads it; admit only dependencies that bend how code is written. POMP engines read an AGENTS.md context file; where one lives, enrich it and honour its bespoke rules — extend hand-written instructions, since humans usually recorded deliberate choices.
- Tour: fix persona and depth (newcomer ~ten stops, glance ~six, architect ~sixteen, review/incident/feature/bug/security ~nine). Every stop is a confirmed location — real file, in-range lines, text anchors where lines drift — in four beats: the sight, its mechanism, its weight for this reader, its easy-to-miss corner. Arc: orientation, module atlas, core path, an edge case, a next step. Lead with a location, never throat-clearing. Review tours open on changed files; monorepos narrow to relevant packages.

## 4. Visual Studio and native tells

Solution files and project flavours; CMake lists and presets; shared build sheets (directory props/targets, central versions, SDK pins, source configs, editorconfig, workload files); launch profiles and runtime settings; test projects betrayed by package references; pipeline definitions; VSIX manifests; migration folders and database projects; doorways (Program, App startup, main/WinMain, package classes). Project-file vocabulary: the `vs-project` skill.

Finished when: cited paths and commands all exist; gaps itemized; the instruction file fits its budget without parroting the README; incumbent instructions survive; tour anchors check out; private data stayed out.
