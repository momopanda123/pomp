# Glossary page layout (domain-modeling)

Open this page only while writing glossary entries.

## Single-context file (`GLOSSARY.md`, repository root)

1. Level-1 heading: the context's name.
2. A sentence or two: what ground the context covers, what it exists for.
3. Entries as a flat list. Introduce a subheading only where entries visibly cluster; otherwise no grouping.

Anatomy of one entry:

- Approved term, bold, opening the entry.
- Definition below: two sentences at most, stating what the thing IS rather than what it does.
- Closing line, italic: the names forbidden for this concept.

Illustrative shape (not real content):

```markdown
# Order taking

How customers place orders in the shop, from basket to confirmation.

**Basket**
A customer's not-yet-confirmed selection of goods.
_Do not say: cart, shopping bag_
```

## Admission rules

- Project words only. Whatever the codebase leans on, generic programming vocabulary (retry behaviour, logging utilities, string helpers) is refused: test every candidate for "is this ours?".
- Exactly one victor per concept. The italic line is where defeated synonyms go, so future contributors stop minting them.
- Nothing but definitions. Specifications, scratch notes, decision logs and implementation detail are all refused. A bloated glossary is repaired by excision first; a context split follows only if the pruned file still straddles two domains.
- Module-shape words from `codebase-design` (module, seam, interface, depth and kin) are design vocabulary, not domain words — they stay out, while domain nouns become the names new modules take.

## Multi-context map (`GLOSSARY-MAP.md`, repository root)

1. Heading.
2. Contexts: each a link to its glossary plus its one-line role.
3. Inter-context links: each states its direction and what travels across (events, shared types).
