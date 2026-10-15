---
name: anatomy
description: "Explain a technical idea as a crafted, interactive isometric SVG figure: a real machine whose working is the concept, built from many small shaded parts and framed with a live readout, with optional WebGL effects and a beta 3D mode. Use for an isometric illustration, an explanatory figure for docs, a blog or an article, a premium technical drawing, an interactive explainer, or an SVG showing how a system or algorithm works. Needs Node.js 20.10+ and Chrome for the checks, or says /skill:anatomy."
---

# Anatomy figures (pomp)

A figure made with this skill looks like a small instrument on a bench: a heavy base on feet, rails with tick rulers, a carriage on bushings, knurled knobs, coil springs, a dial whose needle moves, screws as tiny dots. The reader understands the idea because they recognise the machine, and every number the figure shows comes from the same model that moves its parts.

## Where things are

`skill://anatomy` is this skill's folder; in bash it resolves to the directory. It is install content: read and copy from it, never write into it.

- `skill://anatomy/kit/`: the drawing library (camera, solids, parts, SVG renderer, round parts, pipes, geometry audit, WebGL layer, 3D runtime).
- `skill://anatomy/scripts/`: the headless-Chrome checks and helpers.
- `skill://anatomy/references/`: the long guides, named by the sections below.
- `skill://anatomy/examples/`: eight complete figures as sources. A framework-free example builds with `node build.mjs`, which writes a standalone HTML page next to the script; `--light` builds the light theme.

Before building anything, copy `kit/`, `scripts/` and the example you start from into the workspace (for example under `tools/anatomy/`), keeping their relative layout so the examples' `../../kit/` imports and the scripts' `../kit` reads keep working, and build and check there. Nothing needs installing: the Node side uses only built-in modules, the React side uses the project's own React 18 or newer. The checks look for Chrome on the usual paths; when it is elsewhere, set `CHROME_PATH` to a Chrome or Chromium binary.

Open the closest example before you start:

- `examples/dial-indicator/`: the smallest, a dial gauge on a stand, about 300 lines.
- `examples/desk-computer/`: framework-free SVG, about 85 solids; a keyboard you can type on, the text appearing on a CRT drawn in the plane of the case front.
- `examples/arcade-cabinet/`: a dense cutaway, about 240 solids and 15 cables, with a coin that rolls, trips a switch and sends a pulse along its cable; camera placement for a cutaway and `node build.mjs --audit`.
- `examples/raptor-engine/`: a rocket engine in a test stand with lathe-turned parts, routed pipes, 11 assemblies that come apart and a WebGL plume between the back and front SVG. The reference for dense pipework, smooth shading and shader quality.
- `examples/ripple-tank/`: a ripple tank seen from 44° up; two WebGL layers registered to the isometric planes and driven by a pure optics model in `model.mjs`.
- `examples/test-rig/`: a production React figure, 53 solids; a glass button on a spring carriage, a probe that presses and pulls, a dial gauge reading the lean. The quality bar for the drawing itself.
- `examples/spot-plate/`: a second React figure; glass drops from one distance field that merge when close, a fan of feeler blades setting the distance. The reference for morphing shapes and one control driving many parts.
- `examples/turning-dial/`: the beta 3D mode; a combination dial turning in true 3D with a small robot hand, about 90 live metal parts, a hinge chain and a free body that breaks off. `stress.mjs` beside it is a 380-part performance test.

## The one idea

**The object is the explanation.** Do not draw boxes, arrows and labels. Invent a real, well-made physical thing whose working *is* the concept, then build it the way the real thing is built. Mappings that worked: a cache key that skips work became a pin-tumbler lock whose plug turns only when every pin matches; a separable blur became a bed-of-nails stamp beside two combs on rails.

Two ways this fails, both seen in review: **too abstract** (stacked plates, floating slabs or labelled boxes read as nothing) and **too flat** (one big slab with a few lines has no volume, no parts, nothing to discover). The cure for both is a recognisable machine built from many small, precise, purposeful parts.

**Every figure is highly detailed**, however small the request. What scales with the size is how far the detail reaches: a Figure gets the 4× sweep over its subject and mechanism, a Hero or an Epic over the whole drawing.

- Build it the way the real object is built: every screw, seam, bevel, bushing, label plate, cable with its connector, scale with its ticks, and the parts that hold the parts.
- Two looks: at 1× one clear machine; at 4× every region still has something true to find. A bare face or an empty corner is unfinished work.
- Purposeful, never noise: each part sits on another part, ends on an edge, and fades rather than stops.
- Check last: zoom to 4× before finishing and add the real parts that are missing wherever a region is plain.

## Workflow

Work in this order. The concept and the parts list come before any geometry; most of the quality is decided there.

1. **Size it first.** Name the size before any other work.
   - **Figure**: one mechanism, 40–80 parts, at most one WebGL layer. Name it and build it without asking; do not quote a time (it often finishes well under an hour).
   - **Hero**: a dense object, several WebGL layers or one 3D subject, 80–150 parts, roughly 2–4 hours. Say so, then build unless the user asked for something quick.
   - **Epic**: a whole vehicle, building or plant, or 3D on more than one subject, 6 hours or more. Do not start it: give the estimate, propose a Hero that keeps the core of the idea, and build the Epic only when the user chooses it.
   - Whatever the size, have a page open in the browser within the first hour and deepen it from there. Note the time at each step; when a step runs past twice its share, tell the user where it stands.
2. **Truth first.** Collect the real numbers and behaviour of the thing being explained: sizes, counts, thresholds, curves. Readout, motion and caption must all be true. Where you can, write the model as a small pure module (like `rig.ts` in the test rig) and drive everything from it.
3. **Invent the object.** Pick a machine, tool, instrument or piece of furniture whose mechanism maps one-to-one onto the concept, and say in one sentence what moves and what that motion means. Never copy a reference image: take its finish, not its object.
4. **Parts list.** Write at least 40 parts (40–80 for a Figure, 80–150 for a Hero, more for an Epic), grouped: base (plate, feet, groove, corner screws, label plate, ruler strip), structure (posts, rails, frame bars, end stops), mechanism (carriage, bushings, springs, plunger, needle), controls (knurled knob, thumb screw, crown), and the subject, the one lit part. Each part sits on another part. `skill://anatomy/references/craft.md` has a vocabulary of parts with recipes.
5. **Plan the world.** World units are about 1 px of the real thing: x and y on the floor, z up. Lay out plans as rounded rectangles `{x, y, w, d, r}` and circles. Camera: azimuth 45° by default (35–60° to show the long side), elevation 30° (higher, like the ripple tank's 44°, to look into something open). Fit with `fitProjection(points, width, height, { pad, azimuth })`.
6. **Build geometry once.** Precompute every static path in a geometry module with the kit: `slabOf`, `cylinder`, `extrude`, `planOutline`, `topTicks`, `knurl`, `coil`, `radialTicks` and so on. Round parts on any axis come from `kit/lathe.mjs`, pipes and cables from `kit/tube.mjs`. Moving parts are built once at rest and moved with `translateAlong`; only parts whose shape changes are rebuilt per frame.
7. **Paint back to front, bottom to top.** Sort repeated parts with `depthOf`. Split anything that wraps another part into a back half and a front half; a moving part is drawn in the slot where it physically sits. Once a figure has pipes, cables or round parts, stop tuning keys by eye: record each item's shape as you draw it into `A.recorder(P, { order, spring })`, let `A.settle(R, P)` measure true depth wherever two items overlap, and end the build with `A.auditOrExit(R, P)`. Route pipes before drawing them (real ports at both ends, legs along the structure, crossings on straights; `craft.md`, section 8). Record the true shape of what you draw: a stand-in that silences the audit is a bug, and the audit fails one. A rod sliding out of a gland is one rod from `G.rodOut`, recorded with `A.slide`.
8. **Frame it.** Put the figure in a card: `Fig n` top left, a short title top right, the interaction hint bottom left, the live readout bottom right. Legend keys and a true caption go under the card; words stay out of the drawing apart from at most one tiny engraved label.
9. **Make it live.** Calm, time-based motion: exponential follow or real springs. An idle tour loops when nobody touches it; the figure answers pointer and keyboard, stops when offscreen, and goes still under reduced motion. Write attributes on refs and do not re-render per frame. See `skill://anatomy/references/motion.md`.
10. **Verify like a critic.** The owner will zoom in to find faults, so zoom in first and read every PNG you make.
    - Capture desktop, phone (390 wide), hover and several moments of motion with `scripts/capture.mjs`; use `scripts/drive.mjs` for sequences, the apart pose and the `--no-webgl` fallback.
    - **Audit to zero.** With pipes, cables or round parts, `node build.mjs --audit` must end `audit passed` with every failing line at 0: clearance at rest, apart and in mid-flight, terminals, loose ends, folds, self-overlap, crowding, stand-in shapes, support, wobble, saddles and depth order. Fix an interlock in the geometry, never with a key bias.
    - **Line check to zero.** The audit never sees line markup. `node scripts/lines.mjs page.html --out shots/lines.png` (also on the `-light` page, and in every resting pose through `drive.mjs`'s `["lines"]` action) must print `line ends: 0`: every seam, rule, rib, tick and pointer ends on a stroke, a dot or an outline. Read the ringed PNG.
    - **4× close-ups of every junction in both themes** (build `--light` too), assembled and apart: every fitting and crossing, every curved body (no tone staircase), every place a texture ends, every sliding rod at both ends of its stroke, every window with text or shader content.
    - **Shaders:** a contact sheet slowed with `["slow", 0.05]` across every transition; nothing pops, no colour switch, edges read 0. In light, log ink per frame through each transition (it must follow the light monotonically) and crop where the ink meets strokes at 8–16× (`verify.md`, "Light-theme shaders").
    - Put your PNG beside the examples and ask: is it instantly a real object, rich in small parts with volume, every line ending on something, detailed at 1× and still rewarding at 4×, with no bare face or empty corner? Iterate until yes. Checklist: `skill://anatomy/references/verify.md`.

## The kit

| File | Use it for |
|---|---|
| `kit/iso-kit.ts` | The whole core in one file: camera, solids, parts library, SVG string renderer, card and page builders, and the CSS (`ISO_CSS`). Copy it into a TypeScript project. |
| `kit/iso-kit.mjs` | The same core as plain ESM for Node scripts. |
| `kit/iso.css` | `ISO_CSS` written out. Import once and put `class="iso"` (plus `data-theme="light"` for light) on a wrapper or `<body>`. |
| `kit/react/draw.tsx` | Server-safe React components: `Solid`, `Line`, `Wire`, `Dots`, `FaceText`, `IsoFigure`, `Plate`. |
| `kit/react/live.tsx` | `"use client"` hooks: `useReadout`, `useLoop`, `useEased`, `useInView`, `useStillness`, plus `spring` and `follow`. |
| `kit/lathe.mjs` | Round parts along any axis: `frameAlong`, `lathe`, `solidOf`, `disc`, `ringBand`, `sphereOf`, `saddleOf`, `bandsOf`, `ribsOf` and `dotsOf` on round surfaces, `rodOut` for a sliding rod, `fillet` for pipe routes. |
| `kit/tube.mjs` | `tubePieces` splits a 3D pipe or cable into depth-sortable chunks (`breaks`, `gaps` under collars, end caps, `rings` for a braid or corrugation); `tubeSvg` draws one; `crossingsOf` finds where a route passes through a clamp, `runsOf` the drawn runs. |
| `kit/audit.mjs` | Proves the geometry: `recorder` collects items, shapes and routes as the helpers draw them; `settle` ray-casts every overlapping pair for true depth, at rest, apart and in the replayed explode, and sets the painter's keys; `auditOrExit` is the `--audit` hook. `kit.md`, section 12. |
| `kit/gl.mjs` | The WebGL layer for shaders inside the drawing: `GLSL_ISO` (the camera in GLSL, with `rayOf` and `pixelOf`), `glCamera(P)`, `glLayer(...)`, the envelopes `smooth`, `burst`, `settle`. For 3D, `GLSL_TURN`, `glslPose(name)` and `coverOf`. |
| `kit/turn.mjs`, `kit/turn-build.mjs`, `kit/turn-audit.mjs` | The optional 3D mode: `turning(P, { recorder })` with groups (turn, slide, hinge, free), live layers and materials; the runtime (`TURN.mount`, `TURN.cover`, `TURN.poseUniforms`, `TURN_CSS`); and `orbitOrExit`, the orbit audit. |
| `scripts/capture.mjs` | Headless Chrome screenshots, close-ups and console checks, WebGL on. |
| `scripts/drive.mjs` | Scripted headless Chrome with WebGL: move, click, key, slow motion, wait-until, eval, shots and contact sheets that catch pops; `--no-webgl` and `--preload`. |
| `scripts/lines.mjs` | The line-end check on a built page; rings the failures with `--out`, exits 1 on any. |
| `scripts/inline-kit.mjs` | `kitScript()`, `glScript()` and `turnScript()` return the kit, the WebGL layer and the 3D runtime as source to inline into a standalone page. |
| `scripts/turn-check.mjs`, `scripts/turn-bench.mjs` | 3D figures: fidelity against the kit builders, a z-buffer order check, pixel pops, line ends at 24 angles and frame timing; Node CPU time per part kind. |

Pick the path that fits the project:

- **Standalone HTML, a static SVG, a blog post or any non-React site.** Write a Node build script like `examples/dial-indicator/build.mjs`: import `kit/iso-kit.mjs`, assemble strings with `solidSvg`, `lineSvg`, `dotsSvg`, `wireSvg` and `faceTextSvg`, wrap them with `figureSvg` and `plateHtml`, and write the page with `pageHtml({ script: kitScript() + live })` (`kitScript() + glScript() + live` with a shader). For a lone `.svg` file use `figureSvg({ standalone: "dark" | "light" })`, which embeds the styles.
- **React or Next.js.** Copy `kit/` into the project (for example `components/iso/`). Keep geometry in plain `.ts` modules, build the static art in a server component with the `draw.tsx` components, and pass it as element props to one `"use client"` live component that moves things. `skill://anatomy/references/react.md` explains the split and a trap with large pages.

## Craft rules (short form)

These separate a crafted figure from a diagram; `skill://anatomy/references/craft.md` gives the reasoning and recipes.

- **Solids, not wireframes.** Every part is filled in paper colour and painted in order, so things in front hide things behind. No see-through edges, no dashed hidden lines.
- **Let the kit shade.** `extrude` returns four side tones by facing plus a lighter top. Keep it for anything with height; use `flat` only for floors and inlays thinner than 1 unit.
- **Tone hierarchy.** Base and feet `lo`, structure `mid`, subject `hi`. Exactly one accent is `lit` (white in dark, black in light): the part the reader should watch. Grooves `faint`, minor ticks `lo`, major ticks `mid`, the zero mark `hi`. Colour only when colour is the subject.
- **Small precise detail, never noise.** Bevels of 0.4–1.6, grooves inset 3–5 from an edge, screws as 0.4–0.55 dots with a ring at plate corners, knurls on knobs, ruler ticks every 4–5 units with a major every fifth, a label plate with three rule lines. Each detail belongs to a part.
- **Lines resolve.** Every line ends on something: a face, another line or a terminal dot. Connectors are `Wire`s whose terminal dots land on objects. No leader lines, balloons, dimension arrows or floating labels.
- **No pipe or cable passes through anything**, at rest, apart or in any frame of the explode. Keep about 1.5 radii of clearance to everything but a line's own connection, run lines beside the structure rather than across the subject's face, and gap a pipe wherever a fitting wraps it. Parts meet flush, tangent or rim-on-surface, never sunk in: a flange lands on its port (on a saddle pad, `G.saddleOf`, when the port is curved), a branch leaves from a ball tee, and every end finishes on a fitting, connector shell or terminal dot.
- **Every crossing is drawn in true depth order**, proved by `A.settle` and the audit, not tuned by eye. Lines cross on straights, well apart in depth; a bend, end or clamp landing on another line's outline reads as a joint; a leg that points at the viewer and then turns pinches to a V (leave such a port through an elbow ball); a route never doubles back on screen. A pair that cannot be ordered interlocks, and the fix is a cut or a tangent contact, never a bias.
- **Curved surfaces shade smoothly, never in a staircase.** Mark curved knots smooth (`[s, r, 1]`) so `lathe` shades them from one monotone curve that also draws the outline; give any curved body larger than a fitting an analytic profile with `smooth: { slope }`, cut long bodies with `G.bandsOf`, and use `G.sphereOf` for balls. Tone bands taper to nothing; seams go under the outline (`inner`).
- **A texture ends on a real edge or fades.** Ribs, bolt circles, rule lines, seams and grooves never stop mid-surface: `ribsOf` and `dotsOf` take `fade`, a seam on a box wraps its corners (`k.sideSeam`), a line on one face runs edge to edge, vents are closed slots, ticks hang from a ruler strip's edge, and a line that must stop ramps out with `fadeLineSvg`. A texture on a pipe follows its cross-section through every chunk joint (`tubePieces(…, { rings })`), never a dashed stroke down the centreline. Only rule lines standing for printed text, a needle's tail and a pointer's tip may stop on a bare face, marked `free`.
- **Shaders never pop or cut off.** Every uniform rides an envelope with an attack and a decay, phase changes are crossfades driven by the physical quantity, every term reaches zero before any bound or canvas edge, and the effect lives in world space in the figure's own camera.
- **Things on faces fit them.** Text a machine displays fits its window at its widest string and is clipped to it; content seen through an opening is clipped by the opening as well as by its own plane, with the recess walls drawn.
- **Volume.** Real thickness, things standing on other things, a soft halo under the base and under anything lifted.
- **Composition.** Centre the figure at about 55–65% of the card, keep one scale across a set of figures, and make sure it still reads at 390 px wide.

## WebGL inside the drawing

Use a shader only for what lines cannot draw: fire and exhaust, water and caustics, real lamp light, glow, steam. The parts stay SVG. Read `skill://anatomy/references/webgl.md` before writing any shader.

- **Three layers.** A back SVG holds what the effect covers, the canvas holds the effect (`pointer-events: none`), a front SVG holds what covers it. Split a part, or the effect, when it is on both sides.
- **World space, same camera.** Build every shader point from `onFloor`, `onWallX` and `onWallY`, or march the pixel's ray `rayOf(vb) + t·towardViewer()`. Size everything in world units times `uK`, so anything on a plane registers exactly with that plane's SVG lines.
- **Nothing pops.** Every uniform follows an envelope with attack and decay (`burst` for events, `settle` for levels); phase changes are crossfades in which colour shifts continuously with the physical quantity, and a fade-out only gets darker.
- **No hard cut-offs.** Every term reaches zero before any `if`, bound, canvas edge or cell edge of a cell noise.
- **Seen through an opening, masked twice**: in its own plane and by the opening in the face plane, multiplied (`webgl.md`, section 5).
- **Blending.** `tonemap()` and dither. In dark, a canvas that only adds light uses premultiplied output with `mix-blend-mode: screen`; one that must also darken stays normal-blended.
- **Light theme ships, designed for paper.** Normal premultiplied alpha, never screen. Write the colour the paper should show and solve for the ink: a glow is a saturated body with a pale core and a coloured rim, never a darkened dark-theme colour; ink only grows with light, fades once as a whole, and ends at each stroke's inner edge. Measure ink per frame (`webgl.md`, section 8).
- **Restraint.** Cap the device ratio at 1.5, render only while the effect is active and onscreen, give reduced motion one still frame, and keep the SVG readable without WebGL.

## 3D: figures that turn (beta)

3D is an option like WebGL. Use it when a part moving in depth is the idea: a dial that turns, fingers that flex, a piece that breaks off and falls. The rest of the figure stays static kit art. It is beta, proved on its example and one test figure only: turn one subject at a time and keep the clutter around it static (a dense 380-part hand measured about 20 fps at 1440). Read `skill://anatomy/references/3d.md` first.

- **Live parts are the kit's own picture.** Each frame, convex kit primitives are re-projected and re-shaded by the kit's rules; a turn by θ is the kit drawing at azimuth `a − θ`.
- **Order comes from separating planes**, fixed in the group, in the links' common frame, or solved per frame; the audit proves there are no cycles at any angle or clip frame.
- **Contacts are flush, tangent or rim-on-flat.** Joints are coaxial knuckle discs on the child group touching the parent's link. No live line spans two moving groups: a cable crosses a joint through a clip on each side, or reaches the turntable through a slip ring at the hub.
- **Live tubes bend in near-horizontal planes or inside fittings, never along the view.** A bend needs R ≥ 2r / sin²(e − θmax), θmax including every hinge's pitch range: 8r flat, 21r on a ±12° hinge. Each route runs from mount surface to mount surface with its ports in `gaps`.
- **Materials remap the ramp** (gold, chrome, steel, gunmetal, rubber, brass, copper, each with a light and a dark set); tones still come from facing.
- **Free groups break off without a jump**; coaxial parts are fixed pictures with turning textures.
- **Shaders follow the motion**: anchor them to a group with `controller.anchor` or `toGroup`, occlude a front canvas with `coverOf`, and keep world effects out of group space.
- **Verify with `--audit`** (`turn audit passed`) and `scripts/turn-check.mjs` (`--fidelity`, `--order`, `--pops`, `--lines`, `--perf`); iterate with `--quick --only=…`. Only a full run proves the figure, and it takes 15–20 minutes for 30 parts, so run it in the background.

## References

All under `skill://anatomy/references/`:

- `craft.md`: the look in depth: parts vocabulary with code, tone tables, painter's order, round parts, pipes and harnesses, exploded views, screens on faces, and the anti-patterns that got figures rejected. Read it before designing.
- `kit.md`: the API, with signatures and examples.
- `motion.md`: live behaviour (loops, springs, tours, pointer, keyboard, accessibility, reduced motion), envelopes for one-shot events, exploded views and depth re-sorting, in vanilla JS and React.
- `react.md`: the React/Next.js server/client split, element props and the large-payload key-warning trap.
- `verify.md`: the `capture.mjs` and `drive.mjs` commands, the review checklist (shaders included) and common faults.
- `webgl.md`: shaders inside the drawing: the layer stack, the camera contract, ray-marching, the API with a minimal page, the craft rules, the light theme, and how to verify them.
- `walkthrough.md`: how the test rig was designed, step by step, from concept to code.
- `3d.md`: the 3D mode: groups and poses, live layers, convex parts, materials, free groups, hinge chains, painter's order, the orbit audit, browser checks, performance and limits.

Finished figures stay in the user's workspace; this skill sends nothing anywhere.
