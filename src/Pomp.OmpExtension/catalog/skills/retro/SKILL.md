---
name: retro
description: "Distil a span of repository history into a look-back report: deliveries, pace, work mix, per-person notes and trends. Use when the user wants a retrospective or a summary of recent work, or says /skill:retro."
---

# Retrospective from history (pomp catalog)

Distil a span of repository history into a frank, compact report — what got delivered, at what rhythm, with which quality signals, by whom — and the shift versus the previous report. Alongside the prose, store a machine-readable snapshot so the following run can chart trends.

The source tree is read-only; report and snapshot are the only new files. This run is long, so keep phases A-H on the `todo` tool. History queries go through bash as read-only git, which triggers approval prompts: use a small set of commands and explain once what each reads.

## A. Set the frame

1. Span: a week unless asked otherwise; accept hour spans (`24h`), day spans (`14d`, `30d`) and a compare flag. Day spans open at local midnight of day one; hour spans reach back from the present moment. Think in local time throughout and leave time-zone configuration untouched.
2. Branch: from local refs take the integration branch where present, else the current checkout; announce the chosen ref with its newest commit date. Never fetch unless the user agrees — if the remote might be ahead, say so and ask. No hard-coded branch names.
3. People: treat the configured git identity as "you" and all other authors as teammates; unify evident aliases (several names or mails, one human) and segregate bots.

## B. Collect the evidence

Query with plain `git shortlog` / `git log` over bash — `manage_git` cannot reach history — and launch the independent queries together:

- Commit roster: author, timestamp, subject.
- Numstat sweep: per file per commit, the lines added and removed, which later feeds size bands, hotspots and the test share.
- Change-set identifiers lifted from merge commits or subjects.
- Totals per author.
- The tracked-file roster, to gauge the test-file count and how many of them shifted during the span.

Strip generated, binary, vendored and lock files from line counts; count each change a single time (`--no-merges` or first-parent). Classify test files by each repo's own habits (under .NET: `*Tests.cs` files, test projects) rather than any universal pattern.

## C. Clock the pace

- Headlines: contributors, commits, merged change sets; added, removed and net lines; added lines' test share; release span for repos that version releases; active days, session counts, throughput per active hour.
- Daily contour: hourly commit counts in local time drawn as text bars — or as an SVG drawn via the `diagram` skill; remark on peaks, lulls, single- versus double-humped days, after-hours clusters.
- Sessions: sort every commit time; a gap over 45 minutes closes one session. Long means 50 minutes up, micro under 20 minutes or a solitary commit, medium the band between — pad solitary commits with a stated 15 minutes. Report active time in total, mean session length, and lines per hour of active work.
- Streaks: runs of consecutive local days each holding a commit, team-wide and personal. Declare the rule for a still-silent today (recommended: the streak survives until midnight).
- For spans reaching two weeks, append weekly buckets: commits, lines, sessions, test share, fix share.

## D. Describe the work

- Size bands per change set, splitting at 100, 500 and 1500 lines.
- Hotspots: the ten hardest-worked files, marking five touches and up; test and production files listed apart; files that only bookkeep releases (version, changelog, manifests) in their own group.
- Work mix: bucket by the conventional-commit prefix on each subject (feat, fix, docs, test, refactor, chore) and draw the shares as percentage bars. For repos without prefixes, use keyword heuristics and disclose the unclassified remainder. Fix shares above half whisper "ship, then patch" — a hint, never a verdict.
- Focus and peak: focus share is the commit fraction landing in the busiest top folder — concentration versus scatter; the peak is the biggest change set, sized and explained.

## E. People (drop this phase for solo repos)

Sketch each contributor: commits, lines, three busiest areas, work mix, hours of peak activity, test share, largest piece. One compact table serves — the user leads, teammates follow by commit count — and stays descriptive. The reader earns the full first-person portrait (rhythm plus focus figures); every teammate gets two or three sentences: praise tied to particular commits, plus one growth pointer grounded in a number. Tally AI co-author trailers apart. Describe; do not rank. Anything beyond the table waits for an explicit ask.

Standing rule: these numbers proxy weakly and must never feed judgments about individuals.

## F. Trends on disk

- Open the latest stored snapshot and set previous / current / delta side by side with arrows, flagging which direction counts as good. Restrict deltas to equal-length windows, else normalise per day. If no snapshot exists, declare this the first record.
- Persist one versioned-schema JSON snapshot (span, ref, totals, streaks, per-author figures, digest line) and one markdown report, both dated by the span's final day as `retro-YYYY-MM-DD.json` / `retro-YYYY-MM-DD.md`, in a user-named workspace folder or an established project spot (suggest `retros` beneath `docs`). Keep clear of `.pomp/` and engine configuration paths.

## G. Compose the report

Start from a single digest line over the headline figures, move through shifts since the previous report, pace, shape, the personal section and the team section, and finish with actions: three wins, three improvements pinned to specific commits, three habits tiny enough to adopt immediately. Markdown renders in chat — keep tables tight. Render a one-page HTML view through `page-design` only if asked.

## H. Compare mode

Render the span and the equal-length span just before it, align their metrics with direction arrows, and annotate the largest movements briefly.

Completion checklist: span and ref announced; per-person commit counts reconcile with the grand total, hourly buckets likewise; every applied threshold stated; snapshot and report saved under dated names; deltas confined to comparable windows only; every praise and growth note anchored to evidence — commits or figures; status reads complete, complete with gaps (no prior snapshot, say), or blocked (empty history or an empty span).
