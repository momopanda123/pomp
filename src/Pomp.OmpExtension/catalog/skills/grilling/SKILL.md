---
name: grilling
description: "Pressure-test a plan or decision by interviewing its owner in rounds: each round asks every question that is ready, each with a recommendation worded so a plain yes accepts it. Use to stress-test thinking or draw out open choices, or says /skill:grilling."
---

# Settle a plan by questioning its owner (pomp)

Turn a proposal into confirmed decisions. Treat the subject as a decision tree: each answer settles one node and can reveal further nodes below it. The session ends when no node is left open and the owner has confirmed the whole picture. Other skills use this interview instead of inventing their own.

## Ground rules

1. The user owns every decision. Never answer for them, never proceed past an unanswered question, and never start building, editing or committing before the closing confirmation.
2. Establishing facts is your work; judgement is theirs. Whatever you can settle from files, tools, settings or observed behaviour, you settle yourself, so the user's attention goes only to choices.
3. Question in batches (rounds), unless the user prefers strictly one question per turn — that preference, for instance as an always-apply rule, overrides the batch layout, and then you ask only the most blocking item each turn.
4. This interview is for choices, not for facts you could find, for questions only a prototype or a measurement can answer, or for someone after a straight answer or a quick fix.

## Running the interview

1. Track the open decisions and what each waits on, refreshing the list as answers arrive; use the `todo` tool when offered, one item per decision.
2. Each round asks the frontier: the open decisions blocked by nothing still open, so none of them requires guessing another answer first. The frontier is a judgement call — if the user says two items belonged apart, reopen that branch next round.
3. First clear your own homework: anything a frontier item needs that you can look up, look up (read files, search, run checks, or delegate to a headless subagent through the `task` tool, following POMP's delegation rules — facts only, never decisions). Never stall a round on a lookup still running: hold back only the items depending on it and send the rest at once.
4. Send the round as numbered items the user answers by number: a compact heading per item, a body (several paragraphs or an option list when needed), your recommendation on its own marked line, and a visible divider between items.
5. Phrase every item as your recommendation, so that a plain "yes" accepts exactly your suggestion: to recommend dropping something, ask whether to drop it; for options, build the recommended option into the question and cover the rest in the body. Keep the phrasing positive — in some languages yes/no answers to negative questions are ambiguous.
6. Stop and wait for the reply.
7. Absorb the reply: settle what was decided, add newly surfaced decisions, and move anything invalidated by a same-round answer into the next round. Loop back to step 2.
8. When nothing is open anymore, summarise the agreed picture and ask for explicit confirmation before anything else happens.

Example: the plan is a nightly digest mail. Open: A) which events qualify, B) how failures retry (answerable only once A is known), C) the subject-line format (independent). The first round covers A and C; B follows once A is settled.

## Why rounds

- Single questions drip away turns; asking everything at once produces answers that later answers would have changed. Frontier batches give speed without staleness: many questions usually settle in a few rounds.
- Attaching your recommendation to each item lets the user merely confirm or correct — and a wrong recommendation shows how you read the situation.
- Question count is uncapped: some plans need three questions, others fifty. A dragging session usually means the scope is too wide; split it. Steer length in plain words.

## In POMP

- Write rounds as plain numbered text in the user's language, bold titles, one marked recommendation line each; no special glyphs needed.
- One option-heavy item may use `present_case_feedback` cards when per-option comments help (see the `case-review` skill); a full round stays plain text. Subagents cannot show cards.
- Calling skills load this interview with an explicit read of `skill://grilling`.
- This skill stands alone and needs no other catalog skill.
