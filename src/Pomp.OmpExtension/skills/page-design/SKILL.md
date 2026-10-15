---
name: page-design
description: "Design and build a single self-contained HTML page or screen mock-up in the workspace: layout, colour tokens, type scale, spacing, components, 390px behaviour, light/dark preview support. Use when the user wants a designed page or mock-up kept as a file, or says /skill:page-design."
---

# Page design as one HTML file (pomp)

Build a single self-contained `.html` file in the user's workspace (under `docs/` or a folder the user names; never outside the workspace) with the normal file tools: a finished one-page document or a mock-up of a screen, with markup, styles and inline SVG imagery all in that one file, looking complete with no scripting. POMP shows a Preview card for the file; in your reply give the file path and mention that it can be previewed from the card.

Hard limits (the preview renders with scripts disabled, no network, no navigation):
- No `<script>`, no event-handler attributes, no external resources at all: no CDN, no web fonts, no remote images, no external CSS. Everything inline; system font stacks only.
- Nothing may depend on scripting: show each state statically (e.g. empty and filled variants side by side) instead of faking interactivity.

## 1. Conventions first

1. Authority order: what the user explicitly asked for, then the project's existing design conventions (look in its docs and code for palette, type and component patterns and reuse them), then this skill's defaults, which only fill gaps.
2. Match the request's weight: functional material gets restraint (solid palette, steady spacing, clear type hierarchy, no showpiece elements); a distinctive stance only when the request asks for a lasting or public piece. When unsure, choose restraint. One exception: with an open-ended showpiece brief, work like a lead designer whose client has already dismissed templated work and expects a point of view — one defensible aesthetic gamble is allowed.
3. Review your own result by measuring the code with the self-check list. The rendered page stays invisible to you, so whenever the doubt is about taste, ask the user to judge in the Preview card.

## 2. Brief before building

Write a short brief as an HTML comment heading the file before coding: topic, audience, the one job the page must do; the palette as named values; the typefaces and each one's role; the composition in one sentence. Every colour and font in the code must trace back to it. Use real content, never filler; draw visual ideas from the subject, not from habit.

- When the brief leaves the topic open, table a concrete subject, audience and job in one chat question and wait for confirmation before designing (remembered preferences count as hints) — never pick the subject unilaterally. When no genuine content arrives with the request, write copy specific to the subject and label it proposed: off-the-shelf wording brands a page as a template.
- Draw visual thinking from the subject's own trade — its tools, materials, units, vocabulary, artefacts, physical shapes. Apply the swap test: change the topic mentally, and a design that survives unchanged is generic.
- Lasting or public pieces earn two passes. Planning pass: settle a handful of named colours (four to six), assign each typeface its role, sketch the layout in a sentence plus a compact text wireframe (try two and compare), choose an alignment (left, centred, justified), and name what will distinguish this page from its neighbours. Review pass: hold the plan beside the brief, and beside whatever you would hand in for any comparable request, and rework every part that reads as default. Log the outcome in the brief comment as a "changed:" line (every revision plus its reason) alongside a "rejected:" line (abandoned directions, so later passes leave them buried). With an open-ended showpiece brief, lay two or three directions before the user through `present_case_feedback` (named palettes, type roles, text wireframe) ahead of any building.

## 3. Tokens and type

- Define the palette as custom properties (background, surface, ink, muted ink, line, one accent, plus success/caution/error kept distinct from the accent). Nudge neutrals toward the accent hue instead of flat greys. Components take colours only through these properties.
- One type scale from a system stack with explicit fallbacks. Body about 16px with generous leading and about 65 characters per line (capped near 80); serif settings may run a touch longer but then need extra leading over sans. Headings with balanced wrapping; tabular numerals where figures align in columns; sizes in `rem` so text zoom works.
- Compose the page from a single typeface, or from two that stand clearly apart (serif body beneath sans headings, say) — two lookalikes are worse than one. Inside system stacks, voice comes from picking across families (mono, serif, humanist sans, condensed or wide where offered) and from handling size contrast, weight, case and spacing. Headline setting is design, not wrapping.
- Reserve small uppercase letter-spaced labels for brief category or status tags in which the case genuinely speeds scanning — no kicker crowning each heading; each label owes the reader a reason to exist.
- Break these type habits: singling out one headline word with bold, italic or colour; capitalising labels by default; floating small labels over content that wants none.

## 4. Layout, spacing, components

- One spacing scale (e.g. 4 / 8 / 16 / 24 / 32 px); space siblings with `gap` on flex/grid containers, not per-element margins, and give each spacing decision a single owner.
- Outer container: capped reading width, gutters of at least 16 px at every width (top/bottom spacing via `padding-block`, never a shorthand that resets the sides).
- Reuse a small component set (button, card, tag, table, input, notice); keep variants few and consistent. Structural furniture — rules, borders, dividers, numbering, kickers, labels — must mirror genuine structure: reach for markers that number only for true sequences like steps or a timeline, and confirm the sequence first. Microcopy is part of the design: fix the page's main message first, then deliver it in as few words as possible; reader vocabulary, active voice, errors pairing cause with remedy, no placeholder text.
- Borrow names from the reader's world rather than the implementation ("Alerts" list, not "event subscription rules"); explain what a thing does or is in plain terms — salesmanship stays out. Refer to each operation by one term from its trigger to its confirmation (an "Archive" control confirming with "Archived"); buttons announce their result ("Apply edits", not a bare "Submit"). For failures and empty states, state cause with remedy in the voice of the system, exactly and unapologetically; empty views name the next action. Write plainly and conversationally: active voice, ordinary verbs, initial capitals only plus proper nouns, no filler; one text element, one job.

## 5. Narrow width (390 px)

- No page-level horizontal scrolling at 390 px: multi-column rows wrap or collapse to one column; every flex/grid item holding prose, code or tables gets `min-width: 0`; images and SVG cap at `max-width: 100%`; never a minimum width above the viewport.
- Inherently wide content (code blocks, tables, wide figures) scrolls sideways only inside its own wrapper (`overflow-x: auto`), never by widening the page.

## 6. Build it

- Well-formed markup: `<!doctype html>`, `<html lang="...">`, viewport meta, a concise 2-4 word `<title>` plus a separate meta description, quoted attributes, closed elements. `body` gets explicit background and text colours from the properties, never transparent. (The preview sets `data-theme` on the `<html>` element; see Theme contract.)
- Give the first screen to whatever owns this topic most — a headline, a static product view, an illustration, an excerpt of real data — and choose it on purpose. Treat the stock opening (oversized figure, small caption beneath, row of supporting numbers, decorative flourish) as a last resort, kept only where nothing else serves.
- Imagery only as small inline SVG, themed from the page stylesheet through `currentColor` and the same properties (no separate `<style>` inside the SVG, no `<script>` inside it either), or as pure CSS. No animation.
- Allow a single flourish: one memorable element, everything else restrained — and in the final pass, cut one ornament the brief does not need.

## 7. Accessibility

- Contrast: body text at least 4.5:1 against its background (3:1 for large text and UI boundaries); confirm the accent on both themes.
- Visible keyboard focus on every link, button and input (`:focus-visible` outline).
- Touch targets at least 44 px.
- Semantic markup: real headings in order, real `<button>` / `<a>` / `<label>` + `<input>`, alt text on informative images.

## 8. Starter tokens

```css
:root {
  --bg: #ffffff; --panel: #f2f2f5; --ink: #1a1a1a;
  --muted: #5c5c66; --line: #8a8a94; --accent: #0b5fff;
  --ok: #1a7f37; --warn: #9a6700; --err: #cf222e;
  color-scheme: light;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #141418; --panel: #1e1e24; --ink: #e8e8ec;
    --muted: #a7a7b3; --line: #6e6e79; --accent: #7aa5ff;
    --ok: #4ac26b; --warn: #d4a72c; --err: #f47067;
    color-scheme: dark;
  }
}
:root[data-theme="dark"] {
  --bg: #141418; --panel: #1e1e24; --ink: #e8e8ec;
  --muted: #a7a7b3; --line: #6e6e79; --accent: #7aa5ff;
  --ok: #4ac26b; --warn: #d4a72c; --err: #f47067;
  color-scheme: dark;
}
body { background-color: var(--bg); color: var(--ink); }
```

## Theme contract

The preview forces a theme by setting `data-theme="light"` or `data-theme="dark"` on the root element of the file; with no attribute (or `data-theme="auto"`) the file follows the OS setting. Leave `data-theme` unset in the file itself. Support all three paths with this exact pattern:

1. Define every colour as a custom property on `:root`, holding the light value. Set `color-scheme: light` on `:root` as well.
2. Inside `@media (prefers-color-scheme: dark)`, override the same properties on `:root:not([data-theme="light"])`, with `color-scheme: dark`.
3. After the media block, repeat the dark values on `:root[data-theme="dark"]`, with `color-scheme: dark`. No rule is needed for `data-theme="light"`: the root defaults are light and step 2 already excludes it.
4. Use colours only through these properties (or `currentColor` derived from them). Never a literal colour on any element, and never a colour declared only inside an override block.

## Self-check

Re-read the written file and verify each item before answering:
1. One file, in the workspace; brief comment on top; every colour and font traces to the brief; project conventions reused where they exist.
2. No `<script>`, no `on*` attributes, no external URLs anywhere (markup, CSS, inline SVG); system fonts only; the page is complete without scripting.
3. Narrow: columns collapse at 390 px, gutters of at least 16 px, no page-level sideways scroll; wide content scrolls only in its own wrapper.
4. Theme contract holds: properties on `:root`, media override, explicit-attribute override, `color-scheme` set; `body` colours explicit; no literal colours, none declared only in an override.
5. One type scale; body about 65 characters per line; no filler text.
6. Contrast 4.5:1 / 3:1, visible focus, 44 px targets, semantic markup, labelled inputs.
7. No overlaps, no spacing undone by specificity, title and description present, no animation.
8. The brief states subject, audience and job (user-confirmed where the request skipped them) and keeps a revision log.
9. The section-9 watchlist contributes nothing uninvited: each entry needs a request or a subject-based defence; no universal kickers; headlines free of one-word emphasis.
10. Numbering marks true sequences only; borders, rules and labels each defend the structure they express.
11. A single memorable element; the final pass deleted one ornament.
12. Operations keep one name from control through confirmation to error text; errors state cause plus remedy with no apology; empty states name the next move.

## 9. Default-look watchlist

Close lasting or public pieces with this critique pass: each entry below is legitimate on request or by subject, and suspect as reflex. (Authority order holds: an explicit request beats this list.)

- Palette: warm paper-cream grounds under heavy high-contrast serifs with burnt-orange / clay accents; near-black grounds lit by single electric or acid accents.
- Layout: newspaper tics (hairline rules, square corners, packed columns); pages chopped into matching rounded boxes sharing a radius with a soft grey shadow, gradients as decoration.
- Ornament tics: capital-letter kickers crowning every heading; metadata laced with interpuncts; labels shaped "TERM - fragment" on spaced dashes; muddy near-black where plain black fits; captions shrunk into monospace; arrow glyphs trailing links and buttons.
- Even this skill's own pulls — neutrals tinted toward the accent, dark surfaces near-black — need a subject-based case each time, not silent reuse.
