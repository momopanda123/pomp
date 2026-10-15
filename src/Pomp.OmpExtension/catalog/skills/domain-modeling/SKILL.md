---
name: domain-modeling
description: "Sharpen a project's vocabulary while design is discussed: maintain its glossary (one preferred word per concept) and a sparse log of weighty decisions. Use when talk turns to names, terms or decision records, or says /skill:domain-modeling."
---

# Hold the project's words steady (pomp)

Two small documents carry how the team thinks: (a) a glossary assigning exactly one approved name to each project concept; (b) a thin record of the decisions a newcomer would otherwise mistake for errors. Your job is keeping both truthful while design conversation unfolds — amending the model as it is discussed, not summarising afterwards. Merely borrowing a defined word needs no procedure; anyone can read the file in one line.

## Ground rules

1. Reach for this skill when names come up: overloaded words, rival names for one idea, pending edits to the glossary or the records, a session pinning down what something is.
2. Stay out for simple reads, for specs, notes or task lists (neither document hosts those), for everyday programming vocabulary, and for the shape of modules (the `codebase-design` catalog skill; carry on without it if it is switched off).

## Placement

1. Survey existing habits before inventing any: a decisions folder (`doc/adr`, `architecture/decisions`, ...), a language document already in use, its numbering, template and tongue. Adopt what exists; the defaults below apply only to a clean slate.
2. Default, one context: a single glossary at the repository root (`GLOSSARY.md`) and decision records (ADRs) under `docs/adr/`.
3. Default, several contexts: a root map (`GLOSSARY-MAP.md`) naming each context (link plus one-line role) and the links between them (direction and what travels: events, shared types). Every context then owns its glossary and `docs/adr/`; decisions spanning the system stay at the root.
4. Deduce the layout: a map means several contexts — read it to pick the glossary; a lone root glossary means one; neither means you create the root glossary with the first agreed term. When contexts multiply, decide which one a topic inhabits, asking when unsure.
5. Nothing is scaffolded empty: files and folders come into being only to hold real content. In .NET work the `vs-project` skill and the host's solution tools help align projects or namespaces with contexts.

## Reactions, by trigger

1. Wording agreed — record it immediately and continue. Terms are never stockpiled for a final batch.
2. A choice looks durable — admit it to the records only if all three hold: reversing it later would hurt; a later reader needs the background to understand it; genuine alternatives were weighed for stated reasons. Otherwise decline, naming the test that failed.
3. The user contradicts the glossary — stop at once and ask which sense they mean. Never resolve this silently.
4. A word is loose or double-edged — coin one sharp name per sense and win agreement (an "item" might be a catalogue entry somewhere and a basket line elsewhere).
5. A concept edge is debated — invent concrete borderline cases that force a verdict on where one idea ends and the next begins.
6. The user narrates behaviour — verify in the code first. If their account and the code part ways, lay both out and let them rule. Cross-checks reach code and committed documents only, never the issue tracker, so a dispute settled there long ago may resurface.

## File shapes

- Glossary: heading with the context's name, a sentence or two on its scope and purpose, then entries. An entry opens with the approved term in bold, follows with a definition of two sentences at most — what the thing IS, never what it does — and closes with an italic line banishing the rival names. See `skill://domain-modeling/glossary-format` when writing.
- Records: one file each under the decisions folder; a brief context, the choice, the reason — a paragraph can be the whole record. Optional status, rejected alternatives and consequences join only when genuinely informative. See `skill://domain-modeling/adr-format` when writing.
- Draft in the team's working tongue (Korean or English); hold each file's structural labels steady.

## Writing

- Save with the file tools (`write`, `edit`) under POMP's normal approval — no separate permission ritual — then report each change in one line. Never write through disk tools to a file holding unsaved Visual Studio edits; POMP's pre-edit guard forbids it.
- Glossary and records are ordinary workspace documents. Where an agreed term or decision is also durable team knowledge worth keeping in POMP, offer it additionally through `propose_knowledge` (the user accepts); never invent side memory files for that.
- Definitions only, project words only. A glossary ballooning into a spec means implementation detail and decisions crept in — strip those before ever splitting contexts. A healthy glossary contracts about as often as it expands.
- Anything you file unverified reads as fact to later sessions: mark fresh entries draft until confirmed.
- This skill stands alone; it never depends on `improve-architecture`.
