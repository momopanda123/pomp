---
name: database-migrations
description: "Plan schema and data changes that survive live traffic: expand-then-contract, online locks, batching, EF Core and SQL Server specifics. Use for migrations, backfills, zero-downtime release plans or migration reviews, or says /skill:database-migrations."
---

# Evolving a live database (pomp catalog)

Shape schema and data changes so they land on a busy database, replay identically in every environment, and recover cleanly — with app and schema compatible at each instant of the rollout. Query tuning is elsewhere; backup policy is `skill://security-review/cloud-and-infrastructure`.

Skill ids below are catalog ids; this skill works when they are absent — apply the named idea directly.

## 1. Standing rules

1. Version-control every change as a migration file; ad-hoc statements leave no trail and replay nowhere.
2. Freeze a migration once it has run anywhere shared — fixes ship as further migrations. Rewriting history forks environments silently.
3. Production moves forward only: "rollback" means a corrective migration or a restore. Down scripts serve development convenience.
4. Split schema work from data work into separate migrations — locks, failure modes, and transaction lengths all differ.
5. Rehearse against production-scale data: instant over a hundred rows can seize locks for minutes over tens of millions. Locking and timing exist only on the user's data, so request or build a rehearsal copy.

## 2. Routine

1. Pin down engine and version, the migration runner, and who applies what where (developer, pipeline, release engineer) — this fixes the delivery form: script, bundle, or startup hook. Stay with the repository's incumbent (EF Core migrations, a state-based database project, a code-first runner).
2. Grade the change: purely additive; additive yet lock-heavy; breaking (rename, type shift, drop); data reshaping.
3. Route breaking changes through expand-then-contract over separate releases: introduce the new shape (nullable or defaulted); dual-write from the app; backfill history; move reads across; retire the old writes; drop the old shape in a later migration once no running code names it. Where the user weighs expand-contract against a maintenance window, lay the strategies out as `present_case_feedback` cards (the `case-review` skill) and sketch the timeline with the `diagram` skill.
4. Generate via the tool, then read the emitted SQL: runners rarely capture online index construction, deferred constraint validation, or batched writes — hand-edit those stretches.
5. On large tables: create indexes online or concurrently; introduce constraints unchecked, then validate them; impose a lock timeout so the change dies fast rather than queuing behind long transactions and jamming everyone; backfill in small throttled batches committing between rounds and resumable mid-way.
6. Prove it: migrate a blank database and a production-shaped twin; clock it; run the app tests; walk the recovery path for real.
7. Deliver a reviewed script or bundle plus a written recovery plan. Put generated SQL before the user before any database feels it; shared and production databases stay untouched without explicit approval. DDL is not transactional everywhere — a failed migration may half-apply, so keep migrations small and idempotent where feasible.

## 3. EF Core, SQL Server, and a PostgreSQL footnote

- EF Core: property renames scaffold as drop-plus-add, annihilating data unless rewritten as a rename; the model snapshot is versioned and merge-conflicts; idempotent scripts and migration bundles are the production outputs; startup auto-apply gambles with multiple instances and mighty credentials.
- SQL Server: online index support varies by edition; the system rename procedure orphans dependents silently; giant updates escalate locks and bloat the log — batch them. Lean on SQL Server Object Explorer and database projects on the VS side.
- PostgreSQL in brief: constant-default column adds are metadata-only on current releases; a required column without default errors on non-empty tables; flipping a column to NOT NULL scans under a heavy lock unless a validated check constraint already guarantees it; concurrent index builds refuse open transactions (unwrap them in the runner) and may strand an invalid index on failure.

## 4. Pre-apply bar

Reversible, or flagged irreversible; no marathon exclusive locks; fresh columns nullable or defaulted; indexes online; backfill detached and batched; rehearsed on lifelike data; recovery written; app code fluent in both schema generations through the overlap.
