---
name: diagram
description: "Draw flowcharts, architecture diagrams, sequence diagrams, state diagrams and dependency graphs as hand-authored SVG files in the workspace, legible in the light and dark preview. Use when the user wants a diagram kept as a file, or says /skill:diagram."
---

# Diagrams as SVG files (pomp)

Write the diagram as a standalone `.svg` file in the user's workspace (under `docs/` or a folder the user names; never outside the workspace) with the normal file tools. POMP shows a Preview card for the file; in your reply give the file path, a one-sentence caption stating the figure's claim, and mention that it can be previewed from the card. A quick sketch inside a case-review card may use Mermaid in the card's `diagram` field instead; anything the user keeps goes in an SVG file.

## 1. Decide whether to draw

1. Draw only when the picture shows relationships - order, flow, structure, choice - faster than prose. If one or two sentences do the job, write prose instead.
2. Fix the single claim the figure makes (e.g. "retries happen before the circuit opens"). One figure, one claim.
3. Keep an element only if erasing it would weaken the claim. The drawing must carry behaviour - who calls whom, in what order, what moves, what is decided - not just names of parts.
4. When the claim compares options, draw them on one common base so only the differing parts change.
5. Scale detail to the decision: a minor question gets a handful of shapes; ordering or ownership consequences earn participants and sequence marks. Neither a sketch that hides the consequence nor a map of the whole system.

## 2. Pick the type and its notation

**Flowchart** - steps and decisions over time; read top to bottom.
- Start/end: pill (fully rounded rectangle). Steps: rectangle. Decisions: diamond with every exit labelled by its condition; merge branches back explicitly. One obvious entry point, one main direction, no avoidable line crossings.

**Sequence** - who talks to whom, in what order; time runs downward.
- Participants as labelled boxes across the top, each with a vertical dashed lifeline below it.
- Calls: solid arrows labelled with the message; returns: dashed arrows; thin activation bars on a lifeline while it is busy. Top-to-bottom order must be unambiguous.

**Architecture / structure** - parts and their relations; tiers stack vertically.
- Boundaries as labelled enclosing regions (layer, service, owner); boxes for the components inside them.
- Arrows for dependencies or data flow, each labelled with what crosses (verb or payload); the arrow direction alone must show which way the dependency runs. Group by layer or owner.

**State** - modes of one thing and what moves it between them.
- States as rounded rectangles; a filled dot with an arrow marks the initial state. Every transition arrow labelled with its event (plus guard or action when needed). Every state reachable; no dead ends unless they are the point.

**Dependency graph** - what needs what (packages, projects, tasks).
- Nodes as rectangles; each arrow runs from the dependent to the dependency. Label arrows only when kinds of dependency differ (e.g. build vs run); otherwise one caption sentence states the arrow meaning.

## 3. Lay out, label, connect

- Grid: place everything on a regular spacing unit (multiples of 8): shared baselines, uniform gaps, aligned centres. Never eyeball positions.
- Labels: 1-3 words at about 12 units, centred with `text-anchor="middle"` (`start` for left-aligned lists). Longer explanations go in the chat caption, not inside the drawing. System sans-serif stack only.
- Arrows: one `<marker>` in `<defs>` - a filled triangle using the same ink variable as the lines - referenced with `marker-end`. A second marker only if the accent colour needs arrows. Never images.
- Colour: one ink for lines, labels and arrowheads; at most one accent hue for the single item that deserves attention; confirm the accent reads on both light and dark grounds.

## 4. Write the file

- Root: `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 W H" role="img" aria-labelledby="title desc">`, sized from the drawing's actual extent (no stock size), `width="100%"` and no fixed height so it scales fluidly. (The preview sets `data-theme` on the `<svg>` element; see Theme contract.)
- Keep drawings narrow when you can (about 640 units or less): at 390 px preview width, 12-unit text on a 900-unit-wide drawing shrinks to about 5 px. Split wide figures instead of shrinking text below ~11 units.
- First children: `<title id="title">` stating the claim, then `<desc id="desc">` summarising the content in one or two sentences.
- One `<style>` element holding the theme properties, the media query and all element rules. Theme colours reach elements only through CSS (stylesheet rules or inline `style="..."`): presentation attributes cannot use `var()`. The exception is `currentColor` - set `color` on the root from a variable, then `fill="currentColor"` works as an attribute.
- Hand-write from basic elements (`line`, `rect`, `circle`, `path`, `polygon`, `text`). If an outline needs long path data, simplify the drawing.
- Forbidden: `<script>`, event-handler attributes, `<foreignObject>`, and any external reference (no `http(s)`, no external images or fonts). `url(#id)` may only point at an id defined in the same file.
- Keep the file under ~100 KB.

## 5. Starter skeleton

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360" width="100%" role="img" aria-labelledby="title desc">
  <title id="title">Claim in one sentence</title>
  <desc id="desc">What the figure shows, in one or two sentences.</desc>
  <style>
    :root {
      --bg: #ffffff; --panel: #f2f2f5; --ink: #1a1a1a;
      --muted: #5c5c66; --line: #8a8a94; --accent: #0b5fff;
      color-scheme: light;
    }
    @media (prefers-color-scheme: dark) {
      :root:not([data-theme="light"]) {
        --bg: #141418; --panel: #1e1e24; --ink: #e8e8ec;
        --muted: #a7a7b3; --line: #6e6e79; --accent: #7aa5ff;
        color-scheme: dark;
      }
    }
    :root[data-theme="dark"] {
      --bg: #141418; --panel: #1e1e24; --ink: #e8e8ec;
      --muted: #a7a7b3; --line: #6e6e79; --accent: #7aa5ff;
      color-scheme: dark;
    }
    svg { background-color: var(--bg); color: var(--ink); }
    text { font-family: system-ui, "Segoe UI", Arial, sans-serif; font-size: 12px; fill: var(--ink); }
  </style>
  <defs>
    <marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
      <path d="M0,0 L10,5 L0,10 z" style="fill: var(--ink);" />
    </marker>
  </defs>
  <!-- shapes here; arrows use marker-end="url(#arrow)" -->
</svg>
```

## Theme contract

The preview forces a theme by setting `data-theme="light"` or `data-theme="dark"` on the root element of the file; with no attribute (or `data-theme="auto"`) the file follows the OS setting. Leave `data-theme` unset in the file itself. Support all three paths with this exact pattern:

1. Define every colour as a custom property on `:root`, holding the light value. Set `color-scheme: light` on `:root` as well.
2. Inside `@media (prefers-color-scheme: dark)`, override the same properties on `:root:not([data-theme="light"])`, with `color-scheme: dark`.
3. After the media block, repeat the dark values on `:root[data-theme="dark"]`, with `color-scheme: dark`. No rule is needed for `data-theme="light"`: the root defaults are light and step 2 already excludes it.
4. Use colours only through these properties (or `currentColor` derived from them). Never a literal colour on any element, and never a colour declared only inside an override block.

## Self-check

Re-read the written file and verify each item before answering:
1. Prose would not have been faster; the figure makes one claim, stated in `<title>` and in your reply.
2. Every element serves the claim; every connector labelled (or self-explanatory); a legend only for a recurring code.
3. The type's notation is honoured (decision exits labelled; sequence order unambiguous; boundaries and edge labels present; states reachable; dependency direction stated).
4. Shared baselines, uniform gaps; labels 1-3 words at ~12 units.
5. `viewBox` matches the content extent; fluid width; text legible at preview widths.
6. Ink plus at most one accent, all through theme properties; accent legible on both themes.
7. Theme contract holds: properties on `:root`, media override, explicit-attribute override, `color-scheme` set; no literal colours, none declared only in an override.
8. `<title>` and `<desc>` present, `role="img"`, referenced ids exist.
9. No script, handlers, external references or `foreignObject`; every internal reference resolves; file under ~100 KB.
