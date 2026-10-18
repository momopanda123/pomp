---
name: drawio
description: "Draw chosen parts of this workspace (types and members, projects and dependencies, flows, states, data models) as editable draw.io diagrams whose shapes are linked to the code, and carry the user's later edits of such a diagram back into the code. Use when the user wants a draw.io / diagrams.net / .drawio diagram of the project, wants to design or change code by editing a diagram, or says a .drawio file was edited and should be applied."
---

# draw.io diagrams linked to code (pomp)

Two jobs share one file format:

- **Draw**: code → a `.drawio` file whose shapes carry links to the code they stand for.
- **Apply**: the user edited that file in draw.io → understand what changed and change the code to match, after the user agrees.

`skill://drawio` is this skill's folder. Its helper `scripts/drawio.ps1` (Windows PowerShell 5.1+, nothing to install) reads, checks and diffs diagrams. It is install content: run it in place, never edit it or copy it into the project. In bash, from the workspace root, with the URL quoted inside `realpath` exactly like this (PowerShell needs the real path):

```
powershell -NoProfile -ExecutionPolicy Bypass -File "$(realpath "skill://drawio/scripts/drawio.ps1")" <command> "<file>" -Workspace .
```

Commands: `read` (pages and cells as JSON, labels as plain text), `check` (structure; exit 1 on errors), `snapshot` (save the baseline), `diff` (changes since the baseline; exit 3 when there is none; `-Baseline <.json or .drawio>` compares with another version), `decode [-Write]` (draw.io may save pages compressed; this prints or rewrites them as plain XML). Exit 2 means the file is missing or not a draw.io file.

## 1. Files

- Diagrams live in the workspace, by default `docs/diagrams/<topic>.drawio` (or where the user says). One topic per file; separate views of the topic are separate pages (`<diagram>` elements).
- Write plain, uncompressed XML (`compressed="false"`). If a file arrives compressed, run `decode -Write` before editing it as text.
- The baseline is `.pomp/local/drawio/<path of the diagram>.json`, written by `snapshot` every time you finish writing a diagram. Apply diffs against it. It is local data: never commit it, never edit it by hand.
- The helper reads the file on disk. Read and change a `.drawio` only with the disk tools (`read`, `edit`, `write`), never with the VS buffer tools (`read_buffer`, `edit_buffer_hashline`): a buffer edit stays unsaved, so `check`, `diff` and `snapshot` would see the old file and the baseline would go stale. If the diagram is open in VS with unsaved changes (POMP blocks the disk tools then), ask the user to save or close it first. Code files follow POMP's normal rules.
- POMP's preview does not render `.drawio`. The user opens the file with draw.io (desktop app, or app.diagrams.net › Open from › Device), edits it and saves it under the same name. Say so when you hand over a diagram.

## 2. The link contract

Every shape or connector that stands for code is a draw.io `<object>` wrapping its `mxCell`. draw.io keeps these attributes (Edit Data, Ctrl+M) and the cell `id` through edits; that is what makes Apply possible.

```xml
<object id="type-OrderService" label="OrderService" pompKind="class" pompRef="src/Orders/OrderService.cs#Shop.Orders.OrderService">
  <mxCell style="..." vertex="1" parent="1"><mxGeometry x="40" y="40" width="220" height="104" as="geometry" /></mxCell>
</object>
```

- `id`: unique, stable, readable: a kind prefix plus the name at the time the shape is created (`type-OrderService`, `m-OrderService-PlaceAsync`, `proj-Shop.Core`, `e-inherits-A-B`). Letters, digits, `-`, `_`, `.` only. `diff` matches shapes by id, so an existing shape keeps its id when it is renamed or moved; only draw.io's generated ids on new shapes are replaced. Never reuse an id for something else.
- `pompKind`: what it is. Shapes: `project`, `module`, `namespace`, `folder`, `file`, `component`, `class`, `interface`, `struct`, `record`, `enum`, `member`, `table`, `column`, `state`, `start`, `end`, `step`, `decision`, `participant`, `message`, `note`. Connectors: `inherits`, `implements`, `references` (project/package reference), `depends` (uses), `calls`, `contains`, `transition`, `relation` (data model), `flow`.
- `pompRef`: where it lives: the workspace-relative path with forward slashes, `#`, and the fully qualified symbol (`src/Orders/OrderService.cs#Shop.Orders.OrderService.PlaceAsync(Shop.Orders.Order)`). A project: the project file path. A flow step: the method's ref plus `:<line>` as a hint (the symbol wins when lines drift). Connectors take no `pompRef`.
- `pompSig` on members and columns: the declaration as written in code (`public Task<Receipt> PlaceAsync(Order order, CancellationToken token)`), so a rename can be told from a signature change.
- Shapes without `pompKind` (title, legend, decoration) are ignored by Apply. Text the user adds, or a shape marked `pompKind="note"`, is read as an instruction during Apply.
- Containment is real nesting: members are children of their type (`parent="type-..."`), types may sit inside a namespace or project container. Moving a child to another container means moving it in code.

## 3. Draw

1. **Scope.** Settle which part (folders, namespaces, projects, a feature, one method) and which view; ask once only if the request leaves it open. Pick the view by the question: structure → class view; build or layering → project view; behaviour → flowchart or sequence; lifecycle → state view; persistence → data model. More than about 30 linked shapes on a page: split into pages or narrow the scope.
2. **Read the code.** Every linked shape must exist in code: find definitions and references (language server tools when offered, else search and read). Never invent members or relations; leave out what you did not verify.
3. **Lay out** on a 10-unit grid, positions computed rather than eyeballed. Class views: base types and interfaces on top, implementations below, 40-60 between boxes. Project views: lower layers at the bottom, dependencies pointing down. Flows: top to bottom, decisions branching sideways. Box width about `max(160, 7 × longest line + 24)`; member rows 26 high, type header 26. A connector never runs through a box it does not connect: place connected boxes in the same row or column with a clear lane between them, or route the connector with waypoints (`<Array as="points"><mxPoint x="..." y="..." /></Array>` inside its `mxGeometry`). Put connector labels where no box covers them.
4. **Notation** (copy the style strings as written):

| What | style |
|---|---|
| class, struct, record, table | `swimlane;fontStyle=1;align=center;verticalAlign=top;childLayout=stackLayout;horizontal=1;startSize=26;horizontalStack=0;resizeParent=1;resizeParentMax=0;resizeLast=0;collapsible=1;marginBottom=0;` |
| interface, abstract class | same, with `fontStyle=3` (bold italic) |
| enum | same, with `fillColor=#f5f5f5;` |
| member, column (child row) | `text;strokeColor=none;fillColor=none;align=left;verticalAlign=top;spacingLeft=4;spacingRight=4;overflow=hidden;rotatable=0;points=[[0,0.5],[1,0.5]];portConstraint=eastwest;` |
| project, module, component | `rounded=1;whiteSpace=wrap;` |
| namespace, folder container | `swimlane;startSize=24;fillColor=none;dashed=1;` |
| step, state | `rounded=1;arcSize=30;whiteSpace=wrap;` |
| decision | `rhombus;whiteSpace=wrap;` |
| start / end | `ellipse;whiteSpace=wrap;` |
| note | `shape=note;whiteSpace=wrap;size=14;align=left;spacingLeft=6;` |
| inherits | `endArrow=block;endFill=0;endSize=12;edgeStyle=orthogonalEdgeStyle;` |
| implements | `endArrow=block;endFill=0;endSize=12;dashed=1;edgeStyle=orthogonalEdgeStyle;` |
| references, depends | `endArrow=open;endSize=10;dashed=1;edgeStyle=orthogonalEdgeStyle;` |
| calls, flow, transition | `endArrow=classic;edgeStyle=orthogonalEdgeStyle;` (label = message, condition or event) |
| relation (data model) | `endArrow=ERmany;startArrow=ERmandOne;edgeStyle=entityRelationEdgeStyle;` |

   Member labels in the language's own notation, one per row (`+ PlaceAsync(order: Order): Task<Receipt>` or the C# form `+ Task<Receipt> PlaceAsync(Order order)`; pick one per file). Leave `html=1` out of your styles: labels are then plain text, and the only escaping is XML's (`&amp;`, `&lt;`, `&gt;`, `&quot;`; generics as `Task&lt;Receipt&gt;`). A line break inside a label is `&#xa;`. Shapes the user adds in draw.io usually carry `html=1`; the helper turns their HTML labels into plain text, so read labels from `read`/`diff`, not from the raw XML.
5. **Write** the file:

```xml
<mxfile host="pomp" compressed="false" version="24.0.0">
  <diagram id="classes" name="Classes">
    <mxGraphModel dx="1000" dy="800" grid="1" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="1169" pageHeight="827" math="0" shadow="0">
      <root>
        <mxCell id="0" />
        <mxCell id="1" parent="0" />
        <!-- linked shapes, then connectors -->
      </root>
    </mxGraphModel>
  </diagram>
</mxfile>
```

   Children's `mxGeometry` is relative to the parent (member rows at `x="0"`, `y` = 26, 52, ...; width = parent width). A connector: `<object id="e-..." label="" pompKind="inherits"><mxCell style="..." edge="1" parent="1" source="type-B" target="type-A"><mxGeometry relative="1" as="geometry" /></mxCell></object>`.
6. Run `check` and fix every error, then `snapshot`.
7. Reply with the path, one line per page saying what it shows, how to open and edit it (draw.io desktop or app.diagrams.net; keep the file name; Edit Data shows the link), and that the user can ask you to apply their edits.

## 4. Apply edits to the code

1. **Find the edits.** Run `diff`. No baseline (exit 3): if the file is tracked by git, write the committed version to a temp file outside the workspace and pass it as `-Baseline`; otherwise compare each linked shape with its code and treat the differences as the edits, telling the user that is what you did. `unchangedFile: true` means nothing to apply (if the user says they edited it, the edits are probably not saved yet: ask them to save in draw.io). Cosmetic counts (moved, resized, recoloured) never change code.
2. **Check the code still matches the baseline** for everything the edits touch (read each `pompRef`). If the code changed since the diagram was drawn in a way that conflicts with an edit, report the conflict; do not guess.
3. **Translate every change** (a `changes` entry names the fields; `pomp`, `styleKeys` and `parent`/`source`/`target` carry their before and after):

| Diagram edit | Meaning in code |
|---|---|
| label of a linked shape changed | rename (type, member, project, state, table, column); member label with different parameters or type → signature change, callers updated |
| `pompSig` edited by the user | change the declaration to it |
| new row inside a type (no `pompRef`) | new member parsed from the label (visibility, name, parameters, type) |
| new type box | new type, in the folder and namespace of its container or of its neighbours |
| linked shape removed | delete it; list every reference first |
| row or box moved to another container (`parent` changed) | move the member or type there |
| connector added, removed or reconnected | add, remove or change the relation: base type, implemented interface, project reference, dependency (constructor parameter, field, call) |
| connector style changed between notations (`styleKeys`) | change the relation kind (e.g. association → inheritance) |
| flow, sequence or state edits | behaviour change: describe it in words, then plan the code change |
| note or free text added | an instruction from the user; follow it if it is clear, else ask |
| shape copied (check warns that a `pompRef` appears twice) | a new element; ignore the copied link; ask what it stands for if the label does not say |
| new project box or page | ask before creating projects or larger structures |

   Anything you cannot map with confidence becomes a question, not an edit.
4. **Propose before editing**: a numbered list, each line "diagram edit → code change (files)", then the open questions. Wait for the user's go-ahead unless they already asked to apply directly; deletions, moves and signature changes always need it. For several open choices use `present_case_feedback` cards when that tool is offered.
5. **Change the code** with the normal tools and POMP's rules (buffer tools for unsaved documents, rename through the language server when offered, approvals). Build and run the tests the change touches with the host tools when offered; fix what you broke.
6. **Bring the diagram up to date** from the code, with the disk tools (§1): give new shapes their `pompKind`, `pompRef`, `pompSig`, and a stable `id` in place of draw.io's generated one (update the `source`/`target`/`parent` that point at it); correct labels to the code's names; keep the user's positions, sizes and colours; keep their notes until handled, then remove the ones you carried out. Run `check`, then `snapshot`; `diff` right after must report `unchangedFile: true`.
7. **Report**: what changed in code, anything not applied and why, and the diagram is in sync again.

## Self-check

1. Every linked shape and connector exists in code; nothing invented; each shape has a unique stable `id`, a `pompKind` and (shapes) a workspace-relative `pompRef`.
2. One view per page, within about 30 linked shapes; notation styles from the table; labels escaped.
3. `check` reports no errors; the diagram was written with the disk tools, and `snapshot` ran after the last write (`diff` then says `unchangedFile: true`).
4. Apply: every diff entry was mapped, asked about or reported as cosmetic; nothing edited before the user agreed; the diagram and its baseline were refreshed afterwards.
