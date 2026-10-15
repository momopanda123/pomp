# Weighing rival interfaces (codebase-design)

Open this page when alternatives are wanted for a chosen candidate. First drafts are seldom the strongest, so several sharply different designs go head to head.

## Procedure

1. Brief the user before anything else: the constraints binding every candidate interface; the dependencies with their classes (see the `deepening` page); one rough sketch — concrete enough to ground the constraints, never a proposal. Present it, then launch step 2 immediately, so reading and drafting overlap.
2. Dispatch three or more analysts concurrently per POMP's delegation rules (parallel `task` calls; each brief restates the vocabulary inline, since analysts share no context), each bound to a different bias:
   - (a) flattering the everyday caller, so the common path costs nearly nothing;
   - (b) the tiniest viable interface, one to three entries;
   - (c) widest latitude for growth and extension;
   - (d) where it fits, ports-and-adapters around seam-crossing dependencies.
   Every brief independently carries the engineering facts (files in play, coupling tightness, dependency class, what must hide) in both vocabularies (`codebase-design` terms plus the project's glossary). Headless runs carry no approval of their own — the parent call is the boundary — so analysis only. Without a `task` tool, draft the designs one after another in the main thread.
3. Each analyst delivers five parts: the interface (types, methods, parameters, invariants, ordering, failures); a worked usage sample; what the seam conceals; the dependency-and-adapter arrangement; strengths and weaknesses (where leverage runs rich, where thin).
4. Unveil the designs sequentially — one digestible at a time — then score them on three axes: depth, locality, seam placement. Close with a firm verdict of your own, a hybrid of the best where that wins. Commit to a view; never hand back a bare menu.

## Presentation

- One case-review card per design (`present_case_feedback`, see the `case-review` skill) gathers per-design remarks; the verdict itself lives in chat text, since cards only collect feedback and subagents cannot display them.
- A compact SVG per design may follow the bundled `diagram` skill — for example a mass sketch contrasting the interface block with the implementation block.
