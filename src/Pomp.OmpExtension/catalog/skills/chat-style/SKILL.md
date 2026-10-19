---
name: chat-style
description: "Reply formatting for the POMP chat in Visual Studio: bold one-line conclusion first, ### headings with one emoji, traffic-light status marks, blank line between items, --- between sections. Use for every user-facing reply in POMP chat that reports status, findings, options or next steps, or when the user says /skill:chat-style."
---

# POMP chat reply style

A reply format that reads well in the POMP chat window. Use it for status reports, findings, options and next steps.
A short answer of one or two sentences is plain text: no headings, no dividers.

## Structure
1. Start with a `### 📌` conclusion heading and a bold one-line conclusion. Conclusion first, evidence after.
2. Put a `---` divider between sections (a heading and its items).
3. Leave one blank line between items.
4. End with a `### ➡️` next-steps heading: what the user has to decide or what you will do. Start each decision with `❓`.
5. Leave out a heading that would have no content.

## Heading emoji (fixed)
- 📌 conclusion · summary
- 📋 current state · list · rules that apply
- 🔍 investigation · what was checked
- ⚠️ risks · cautions · what could not be checked
- ➡️ next steps

Write the heading text in the user's language, keeping the emoji: in Korean `### 📌 결론`, `### 📋 현재 상태`, `### 🔍 확인한 것`, `### ⚠️ 확인하지 못한 것`, `### ➡️ 다음 할 일`; in English `### 📌 Conclusion`, `### 📋 Current state`, `### 🔍 Findings`, `### ⚠️ Risks`, `### ➡️ Next steps`.

## Status marks (one at the start of a line)
- 🟢 confirmed · passed · done: only what you checked with a tool in this turn
- 🟡 caution · unchecked · needs a decision · inference (add `[INFERENCE]`)
- 🔴 failed · risk · cannot be undone
- 🔵 note · extra detail

## Rules
- At most two or three emoji on a line; more makes it harder to read.
- Use kind emoji (📁 ⚙️ 🧩 and the like) only where a list needs them to tell items apart, never inside a sentence.
- Code, paths and commands go in `backticks`; quote raw text such as error logs as it is, without emoji.
- Use a table only when items are compared. Do not put long sentences in table cells.
- Keep facts and inferences apart. Mark what you did not see with 🟡 and say what you did not see.
- Give only numbers and files you actually read. When you do not know, say so.

## Do not
- Do not use LaTeX color (`\textcolor`, `\colorbox`): this chat does not render it and shows the source text.
- Do not decorate sentences with emoji or invent a new emoji for each heading.

## Example (Korean user)
```
### 📌 결론

**빌드 실패 원인은 빠진 NuGet 패키지 하나**

---

### 📋 현재 상태

🟢 `dotnet restore` 후 다시 빌드해 오류 CS0246이 사라진 것을 확인함

🟡 테스트는 아직 실행하지 않음

---

### ➡️ 다음 할 일

❓ 테스트까지 돌릴까요?
```
