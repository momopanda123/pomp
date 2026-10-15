---
name: hexagonal-architecture
description: "Keep business rules independent of frameworks and storage: ports, adapters, one composition root, per-boundary tests, slice-by-slice migration. Use for layering new code or untangling mixed code, or says /skill:hexagonal-architecture."
---

# Business rules at the centre, tech at the rim (pomp catalog)

Erect or recover a shape where domain rules and use-case orchestration lean only on abstractions they own, and every brush with database, network, UI, or framework hides inside swappable adapters assembled in one spot. Rewards: infrastructure-free tests, replaceable technology, many front doors (web, CLI, queue) over one behaviour. The generic seam vocabulary (module, interface, seam, adapter, depth) lives in `skill://codebase-design` and stays there. Partial adoption is legitimate — hexagonalize the hurting slices only. Thin CRUD, scripts, and prototypes should stay flat; ceremony there costs more than it saves.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Terms

- Domain: entities, value objects, rules; framework, serializer, ORM, and logging imports banned.
- Use case (application layer): a single named operation with explicit input and output; orchestrates domain plus ports; protocol and storage details banned.
- Inbound port: what the application extends to callers. Outbound port: what it demands from outside, worded as a capability (keep orders, take payment, tell time, mint ids) — capability wording lets technologies swap beneath an untouched port.
- Adapter: translation between a port and a concrete technology or protocol, both ways (controllers, consumers, repositories, SDK wrappers). Shape-shifting between layers happens here alone.
- Composition root: the lone assembly point handing adapters to use cases; service locators and hidden globals banned.
- Dependency rule: inward only. Domain leans on nothing external; application leans on domain plus its own port contracts; adapters lean on application. Whoever needs a capability declares its port.

## 2. Growing a fresh use case

1. Christen the use case; freeze input and output shapes purged of transport types.
2. Recast each outside interaction as an outbound port. Interfaces normally neighbour the use cases, entering the domain solely for authentic business concepts.
3. Orchestrate over ports; cover the use case first with unit tests on in-memory fakes.
4. Save adapters for last, at the rim. Inbound adapters morph requests, messages, or command lines into use-case input and render outcomes (failures included) back (inbound silhouette: `skill://api-design`); outbound adapters ground a port in a real API, ORM, or SDK.
5. Assemble at the composition root.
6. Check inputs at the borders, guard invariants in the domain, convert errors crossing layers (see `skill://error-handling`).

## 3. Unknotting legacy code

1. Carve one vertical slice — a single endpoint or job — that churns often and fails cheaply. Slice ballots for the user go through `present_case_feedback` cards (the `case-review` skill).
2. Nail existing behaviour with characterization tests (failing-first loop: `skill://test-driven-development`).
3. Sheathe the infrastructure-touching code in outbound ports first, then hoist orchestration into a use case behind the old controller.
4. Centralize construction early so fresh dependencies stop leaking inward.
5. Keep an escape hatch (alternate route or feature flag) per converted slice until production certifies parity, then advance. The trap to dodge is the all-at-once rewrite. Illustrations, when earned, come from the `diagram` skill as SVG.

## 4. Testing each border

Domain: bare tests, doubles none. Use cases: in-memory fakes behind outbound ports, asserting outcomes plus the interactions that count. Ports: one shared contract suite executed against every adapter, making interchangeability proven rather than typed. Inbound adapters: protocol mapping and error mapping. Adapter integration: genuine infrastructure for serialization, queries, timeouts, retries (see `skill://csharp-testing`, `skill://database-migrations`). Journeys end to end: a precious few.

## 5. .NET and C++ mapping

Usual solution cut: reference-free domain project; application referencing domain; infrastructure referencing application; host project referencing all and rooting composition, typically per-layer registration extensions. Survey projects and references first (solution-structure tools where offered). Whether a repository wrapper over the context pays is a per-project call — either way, query shapes stay port-side. Namespace guards via architecture tests. C++: abstract bases or template/concept ports, constructor injection, per-layer static libraries whose link order betrays illegal reaches, inner public headers clean of outer includes.

Warning signs: ORM or web types in domain code; request objects in use cases; raw rows escaping use cases; adapters phoning each other; wiring strewn about; vendor-shaped ports; purposeless per-class interfaces.

Finished when: inward references only (confirmed by reference/include sweep); outside contact exclusively ported; use cases green without infrastructure; assembly in one inspectable place; errors converted per border; migrated slices keep the old road open until proven.
