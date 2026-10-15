---
name: case-review
description: "Show several options, scenarios, test cases or plan steps as separate cards the user comments on one by one (POMP chat, Visual Studio). Use when the user asks to compare alternatives, review cases, or give feedback per item, or says /skill:case-review."
---

# Case review cards (pomp)

The `present_case_feedback` host tool shows cases as cards inside your reply in the POMP chat. Each card has its own comment box; the user writes comments on the cases they care about and sends them all at once. Their comments arrive as the next user message. Use this instead of a long list when the user should react to each item separately.

## How
1. Work out the cases first (read code, run checks) so each card is concrete. 2 to 8 cases is the useful range (32 at most).
2. Call `present_case_feedback` once with `cases`: for each case
   - `caseId`: short stable id, lowercase letters, digits and `-` (e.g. `retry-on-401`).
   - `title`: one line, at most 120 characters, saying what the case is.
   - `bodyMarkdown`: what it is, why, and its trade-off or expected result; short. Markdown is rendered safely (no HTML).
   - `image` (optional): a workspace-relative path of an image that already exists in the workspace (png, jpg, gif, webp or bmp, at most 1 MiB), such as a screenshot or design file the user has. Only refer to files you saw exist; at most 8 cases with an image. Do not download images.
   - `diagram` (optional): Mermaid source (flowchart, sequence, state, class or ER diagram) when a picture explains the case better than text, at most 4000 characters. Keep labels plain text.
3. After the call, say in one or two sentences what the cards are for, then stop. Do not choose a case yourself and do not continue the work until the user's comments arrive.
4. When the comments arrive, they name the cases by `caseId` and title. Act on them; a case without a comment was not commented on — do not treat that as approval.

## Limits
- Cards are only for the user's feedback: they run nothing and approve no tool or file change.
- A subagent cannot present cards; report the cases to your parent conversation instead.
- If the tool is not in your tool list, present the cases as a numbered list and ask for comments per number.
