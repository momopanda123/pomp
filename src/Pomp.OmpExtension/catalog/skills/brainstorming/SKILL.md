---
name: brainstorming
description: "Settle the design with the user before building: a probe, a small change, or a full design, each with its own approval gate. Use for build/add/change requests; not for questions about code, fully specified edits, bug reports, or designs ready to plan, or says /skill:brainstorming."
---

# Brainstorming before building (pomp)

Turn a vague request into a user-approved design before any building starts: no code, scaffolding, installs, or new projects until then. Looking around read-only is always allowed; changes wait until the track's approval exists. Apply this skill when a request clearly needs a design decision; do not force it onto trivial requests (a one-line edit fully specified, a direct factual question).

## 1. Announce a track

Pick one and say so in one sentence, so the user can overrule a wrong sizing. If undecided, choose the heavier track. Movement is upward only: when surprise complexity appears, pause, inform the user, and move to a heavier track.

- **Probe:** the user probes feasibility, or accepts rough answers. The deliverable is knowledge, not code. State the open question plus the intended experiment in two or three sentences; after a nod, run the least costly experiment that still answers reliably, then report a recommendation. Anything built is labelled disposable and never carried into the product.
- **Small change:** a tightly bounded tweak to a flow the repository already contains (e.g. an extra choice on a current command, an adjusted validation rule). The flow under change has to exist in the repo for reading; if that flow is not in the repo yet, this is large work. Ask just the questions that count, sketch a compact design in the conversation (a paragraph or a short section), then stop and wait for an explicit yes. This track yields no spec document and no plan.
- **Large work:** a new project or subsystem, restructuring, or any interface change others rely on. Run section 2.

Each approval covers just the stage presented: a yes to the idea is not a yes to an unwritten document; a conversational yes unlocks drafting the design document, and approving that document unlocks planning. Each follow-up task gets its own classification and approval.

## 2. Large-work path

1. **Split first:** if the request packs several independent subsystems, map the parts, their dependencies, and a build order; run this track for the first part only, and hand every part separate design, plan, and build.
2. **Intent:** who gains, which outcome, how success shows. With no intent stated, lead with one question about the deeper purpose before any feature talk; if the request already holds it, mirror it back instead of asking.
3. **Read-back:** a short note of outcome, limits, and success measure, keeping what the user said separate from your guesses. Accept their corrections; the fixed note becomes the brief that all later choices answer to.
4. **Questions:** ask remaining questions singly, one per message, offering choices when suitable. Put each question in the reply text and end the turn; the user's next message is the reply.
5. **Options:** present two or three candidate approaches with trade-offs, your pick first with its rationale; cut every unneeded feature. For competing options use `present_case_feedback` cards when that tool is offered (one card per option with its trade-off; then hold for their comments — a card left uncommented never means yes). The `diagram` skill draws flows and structures as SVG pictures; the `page-design` skill sets layouts side by side in one HTML page. Make the visual offer a single time, in its own message, when that kind of question first comes up; after a no, stay silent on visuals until the user raises them.
6. **Design walk-through** section by section, each sized to its difficulty — a sentence for the plain parts, up to a couple of hundred words for the subtle ones — confirming each before moving on. Cover component layout, each component's role, data flow, failure handling, and the verification approach. Split into single-purpose units with slim interfaces, each readable and testable alone. In existing code, match surrounding patterns and include only clean-ups this task needs.
7. **Record the agreed design** in a document (file it per project convention, or ask the user once where it should live; never commit without being asked), then review it once as a stranger would: contradictions between parts, requirements with two readings (pick one and note it), scope beyond a single plan, gaps or placeholder text. Repair issues directly; a single pass only. Optionally, a read-only subagent vets the document against those five points and returns approval or a list of issues.
8. **Have the user read the document** and revise as asked; once approved, continue: large work goes to writing-plans, a small-change yes goes to test-driven-development (use each if switched on).

Track the chosen track's steps with the todo tool.
