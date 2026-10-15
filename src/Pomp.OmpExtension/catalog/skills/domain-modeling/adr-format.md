# Decision-record page layout (domain-modeling)

Open this page only while writing a decision record.

## Files

- One file per decision inside the decisions folder (`docs/adr/` unless the project keeps its own): `NNNN-short-slug.md`, four-digit sequence, next number one past the current maximum. Folder and first record are born together.
- An established project convention for place, numbering or template always wins over this layout.

## Body

- Title stating the decision, then one to three sentences: background, choice, rationale. One paragraph is a finished record — the point is that a choice and its reason survive, not that a template is filled.

## Optional extras (genuine value only)

- Status (`proposed`, `accepted`, `deprecated`, `superseded` naming its successor) once decisions begin to be revisited.
- Contenders considered, where the rejected paths deserve remembering.
- Consequences, where the ripple effects are not self-evident.

## What earns a record

Still gated by the three tests (painful to reverse, baffling without background, picked over real rivals): code-invisible constraints (law, contracts); rejected options whose reasoning a newcomer could not guess, so the proposal stays buried; conscious detours from the obvious path, shielding them from well-meant "fixes"; ownership and scope lines, "no" included; locking technology bets (months to swap, not each library); cross-context integration; the system's overall form.
