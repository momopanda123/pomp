---
name: accessibility
description: "Build and audit UI against WCAG 2.2 AA: roles and names, contrast, keyboard, focus, errors, desktop mappings, plus a manual test script. Use for UI review, keyboard support, labelling or a11y audits, or says /skill:accessibility."
---

# UI open to everyone (pomp catalog)

Commission, implement, and assess interfaces perceptible, operable, comprehensible, and assistive-technology-friendly — as verifiable properties, not aspirations. Purely visual styling calls belong elsewhere. So do legal conformance claims: agent audits never certify, and neither rendered contrast nor screen-reader behaviour is visible from source — admit both and deliver the manual script.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Compass

Four bearings steer the checks: perceivable, operable, understandable, robust. Favour semantic native controls over bespoke ones — semantics, keyboard conduct, and platform integration arrive gratis. Assistive tools consume an accessibility tree of name, role, and value; the craft is keeping that tree honest, with focus order and visibility deliberately choreographed.

## 2. Routine

1. Pin the UI stack and the review's bounds (screens, components, flows); adopt the matching vocabulary (section 3).
2. Name each widget's true role first (button, link, tab, toggle, dialog); reach for the platform native; custom roles only where natives run out — and then custom keyboard conduct too.
3. Perceivable: text equivalents for every non-text element, or explicit decoration; contrast floors of 4.5:1 for text (3:1 for large text, icons, control edges) computed from real values — eyeballing deceives, brand palettes most of all; reflow to narrow widths and enlarged text without forfeiting function; colour never the lone messenger.
4. Operable: full keyboard operation in sensible order beneath a conspicuous, high-contrast focus marker that stays uncovered (bare visibility is 2.4.7; unobscured focus is 2.4.11); traps nowhere; dialogs capturing focus while open yet yielding to Escape or explicit close; focus restored to the invoker as menus and dialogs shut — strays here strand keyboard and screen-reader travellers; pointer targets no smaller than 24 CSS pixels a side (platform touch guidance commonly asks 44 points and up — a separate bar); single-pointer stand-ins for every drag.
5. Understandable: steady navigation and help placement; errors naming the field and proposing the remedy (failure wording: `skill://error-handling`); never re-demanding user-supplied data; authentication passable without memory or transcription ordeals alone.
6. Robust: truthful name, role, and state on every control; live regions voicing dynamic status; correct patterns on custom widgets.
7. Corroborate: static markup-or-XAML review with values, then a compact manual script for the user — keyboard only, a screen reader, 200–400% zoom or enlarged text, high-contrast mode — because automation catches a minority of barriers. Focus-order sketches via the `diagram` skill only when they clarify.
8. Report each finding with criterion, location, user impact, and remedy; gravest impact first.

## 3. Dialects

- Web: natives first (button, anchor, label-for, fieldset, headings, landmarks); ARIA patches gaps only — faulty ARIA underperforms none; accessible names on icon-only buttons; placeholders never posing as labels; positive tab indexes shunned; zoom unblocked.
- Windows desktop: UI Automation is the substrate. WPF exposes name, help text, labelled-by, and live-region behaviour through automation properties, plus peers for custom controls; WinForms offers accessible name, description, role, tab order, and mnemonics; WinUI mirrors automation properties; MAUI carries semantic properties; system high-contrast and text scaling always honoured.

WCAG 2.2 arrivals with levels: dragging alternatives and minimum target size (2.5.7, 2.5.8, both AA); unobscured focus (2.4.11, AA); steady help placement (3.2.6, A); no repeated entry (3.3.7, A); authentication minimum (3.3.8, AA). Retired: parsing 4.1.1.

Hall of shame: click handlers on anonymous containers minus role and keys; errors signalled by red borders alone; modals leaking focus or ignoring keyboards; alt text narrating "image of"; nameless icon buttons; tab order brawling with visual order.

## 4. Free kit for the manual pass

Accessibility Insights for Windows and web (automated sweeps, tab-stop maps); Narrator and NVDA readers; UI Automation inspectors exposing the tree. Role-driven test locators double as accessibility probes (see `skill://e2e-testing`).

Finished when: interactive elements all carry name, role, state, and a keyboard route; contrast figures computed and logged; focus evident everywhere and stage-managed for dialogs and menus; errors in words; targets sized; reflow confirmed or flagged open; the manual script delivered.
