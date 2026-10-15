# Folding thin modules together (codebase-design)

Open this page while planning a restructuring. Sort every dependency of the cluster first — its class dictates how the merged module gets tested at its seam.

## Four dependency classes

1. **Self-contained**: pure in-memory logic, zero I/O. Always safe to fold; exercise it straight through the fresh interface; no adapter wanted.
2. **Faithful local double**: a lightweight stand-in exists — a small local database engine, a scratch directory for storage. Fold once the double is in place; the suite runs against it; the seam remains internal, so the interface exposes no port.
3. **Our own service over the wire**: set a port at the seam. The module keeps the rules; the transport arrives as an injected adapter — networked in production, in-memory under test. Port plus twin adapters lets the rules live as one deep module while deployment still spans parts.
4. **Outside service beyond our control**: receive it through an injected port; tests substitute a fake.

## Seam discipline

- A port earns its existence only with two adapters behind it (live and test, as a rule); one adapter buys a mere detour.
- Internal seams plus one external seam may coexist in a deep module — but the interface must never betray the internal ones, even where tests lean on them.

## Test tactics: supplant, never stack

- Once the deepened interface has tests, retire the old unit tests aimed at the thin modules: they duplicate coverage and weld the suite to internals.
- Aim new tests at externally visible behaviour through the interface, never at inner state; such tests outlive internal refactors. A test breaking on every implementation change is vaulting the interface.
