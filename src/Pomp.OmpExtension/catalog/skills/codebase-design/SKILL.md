---
name: codebase-design
description: "Terms and rules for building deep modules with small interfaces: eight terms, four principles, three testability habits, plus guides for merging thin modules and comparing alternative interfaces. Use when shaping an interface or placing a seam, or says /skill:codebase-design."
---

# A common tongue for module shape (pomp)

Look up, don't launch. This skill is a dictionary with judgement attached: it defines no process, emits no file and sets no finish line. Answer the design question already before you; begin no fresh exploration and dispatch no subagents without an explicit ask from the user or the skill driving the session.

Its purpose: settle "is this piece well cut?" into shared terms. A good answer reads: rich capability behind a compact interface, that interface sitting where change actually enters, and tests arriving through the same door the callers use. Callers gain leverage, maintainers gain locality, and testing stays cheap.

## Eight terms

Anatomy of a code fragment:

1. **module** — any fragment presenting an interface over hidden workings, at any scale: a lone function, a class, a package, a slice through several tiers. Folders are irrelevant. Never substitute "unit", "component" or "service".
2. **interface** — everything a caller must grasp to use the module safely: call shapes, the invariants to preserve, required call order, failure behaviour, configuration, time and resource cost. Far wider than a language keyword or a public-method list; never reduce it to "API" or "signature". (.NET readers: the whole public surface counts, not just the C# keyword.)
3. **implementation** — the workings behind the interface. Not interchangeable with adapter: adapter says which slot at a seam a piece fills; implementation says what sits inside. The two vary freely.

Where change plugs in:

4. **seam** — a spot where behaviour can be exchanged without touching the code there. In this vocabulary: simply the module's interface location. Placing it and filling it are two separate design decisions. Never call it a "boundary" (domain-driven design already owns that word).
5. **adapter** — the concrete piece currently filling a seam and honouring its interface. Names the occupied slot, not the substance.

Judging the cut, and who it serves:

6. **depth** — payoff per unit of interface learned: what a user of the module (code or test) gains for each thing it must learn about the interface. A deep interface repays brief study richly; a shallow one costs nearly as much to learn as the code beneath. Assessed through leverage alone — never through body-to-interface line ratios, which reward padding.
7. **leverage** — depth's gift to callers: each learned fragment unlocks wide capability, and one implementation serves call sites and tests by the dozen.
8. **locality** — depth's gift to maintainers: edits, faults, comprehension and verification converge in one place rather than scattering across callers, so one fix lands everywhere at once.

The chain: a module offers one interface; depth judges that interface; the seam marks its position; an adapter fills the seam; depth pays callers in leverage and maintainers in locality. Hold every term to exactly this meaning — vague synonyms re-open settled arguments.

## Four principles

1. Earn every seam with real variation: one adapter means speculative plumbing; two — normally live plus test — means a genuine seam. Name the second adapter for each seam proposed.
2. One door for callers and tests alike. An urge to test below the interface says the cut is wrong.
3. The removal experiment: picture the module deleted. Trivial residue means it only forwarded calls; the same knot reappearing in each of N callers means it carried genuine weight. Rule on every proposed extraction, plainly.
4. Depth lives at the interface; the interior of a deep module may be many small, caller-invisible, replaceable parts. Internal seams serving its own tests are fine (.NET: internal visibility plus `InternalsVisibleTo`) but must never leak into the interface. While drafting an interface, press three questions: fewer methods? simpler parameters? more complexity moved inside? Cover invariants, sequencing and failure behaviour — not types alone.

## Three testability habits

1. Shrink the surface: a trim interface means fewer cases to cover and lighter setup.
2. Hand back results rather than mutating shared state (a scheduler returning the next run time, not stamping a field).
3. Accept collaborators as parameters instead of building them (a formatter receiving a clock, not sampling the wall time; in .NET: constructor injection).
4. Corroborate testability claims with the host's build, test and diagnostic tools. And remember the vocabulary's limit: it describes deep modules but cannot police them — keeping stray references from vaulting the interface needs enforcement (visibility rules, dependency checks, architecture tests).

## Companion pages

- `skill://codebase-design/deepening` — folding a cluster of thin modules into one, steered by their dependency kinds. Open when a restructuring is planned.
- `skill://codebase-design/design-twice` — weighing several sharply different interface designs via parallel subagents. Open when alternatives are wanted for a chosen candidate.
