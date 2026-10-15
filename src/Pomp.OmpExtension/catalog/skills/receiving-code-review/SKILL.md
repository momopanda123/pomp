---
name: receiving-code-review
description: "Check review feedback against the real code and answer with substance: the fix, a pointed question, or argued pushback. Use whenever feedback arrives — from the user, the reviewer role, or pasted in — mainly when an item looks wrong or unclear, or says /skill:receiving-code-review."
---

# Receiving code review (pomp)

Treat each feedback batch — user chat, reviewer-role report, case-card comments, pasted-in remarks — as claims to verify against the code itself. Answer with substance: neither blind obedience nor hollow agreement. (Card comments cite their cases by id; uncommented cases were merely skipped — never approval.) Apply this skill when its trigger clearly matches; leave trivial requests alone.

1. Read the entire batch before touching any of it.
2. Restate each item in your own technical words; whatever resists restating marks the question to ask.
3. Settle the murky items first: while any item stays unclear, implement nothing — items may couple, and half a reading builds the wrong thing. Name the items you grasp and the ones needing answers.
4. Verify each item against the code: hunt down genuine usages, study neighbouring lines, execute the relevant test; for feedback on uncommitted work, read the saved diff or patch file.
5. Sort each item: welcome and correct; correct yet out of scope; mistaken or risky; not checkable with what you have.
6. Work in this order: breakage and security first, trivial corrections next, heavy logic and restructuring last. Handle items singly, verifying with the host build/test tools before claiming success after each; behaviour-changing fixes follow test-driven-development order (if switched on); an item reporting a failure of unknown cause goes to systematic-debugging (if switched on). End with a regression pass.
7. Answer with substance: the fix made, a pointed question, or argued pushback.

Weigh by source: the user is trusted — clarify vague scope, then proceed directly. A reviewer or outsider: ask first whether the point holds in this exact code (what depends on the current shape, what would break, whether the shape exists for compatibility or platform reasons) and whether they saw the whole picture. If mistaken, answer with reasons; if unverifiable, name what is missing and ask for direction; if it clashes with a user decision, halt and bring it to the user before altering anything. "Do it properly" suggestions: search for real use first — unused means propose removal rather than expansion; used earns the full treatment. Contested findings the user must rule on go to the main agent for `present_case_feedback` cards.

Push back on technical grounds only — harm (regressions, compatibility breaks), irrelevance or missing context, or overrides of user-owned architecture (escalate those) — with reasoning, specific questions, and pointers to code or passing tests, never defensively.

Style, in any language: skip hollow agreement, praise, and thanks — the fix itself shows the feedback landed. A valid item earns a brief factual note naming the change and its place. Proved wrong after objecting, say so factually with what you verified and what comes next; no long apology, no defence of the old stance.
