---
name: finishing-a-branch
description: "Land finished work by the user's chosen route — leave it, or commit and push a branch — with a green suite and no destructive git. Use when implementation is done; not for half-done or exploratory work, or says /skill:finishing-a-branch."
---

# Finishing a branch (pomp)

When implementation is done, carry the work to the ending the user picks — losing nothing and taking no irreversible step nobody asked for. Not for half-done or exploratory work. Apply this skill when its trigger clearly matches; leave trivial requests alone.

1. Go green first: run the full build plus the whole test suite against the exact tree about to land (verify with the host build/test tools before claiming success: `build_solution` plus `read_error_list`, `manage_tests` or `dotnet test`). Any failure: list the failures and stop; offer no choices until green. An earlier result says nothing about the current tree.
2. Read the situation: branch name, upstream ahead/behind, and staged/unstaged/untracked groups via `manage_git` status. Dirty documents must be saved or closed first — ask the user to do that before anything else, since VS-held edits escape git reads and branch moves under an open solution trigger reload prompts. Record the workspace path. Isolation in POMP is team-only: never hand-create worktrees or extra checkouts, and leave isolated copies from host runs and anyone else's workspace untouched.
3. Name the base branch — the earlier conversation, the plan, or upstream config usually reveals it: the branch the work was cut from. When unsure, propose your best guess and ask. A wrong base costs much to unwind.
4. Offer a fixed menu as a numbered list and end the turn (use `present_case_feedback` cards only if an option needs its own commentary):
   - leave everything as it is;
   - commit and push the branch, then open a review request — or hand the branch name over and say opening the request is the user's job.
   Local merging is not offered: the host git tool covers status/diff/commit/push only, and switching branches under the open solution forces VS reloads — the user merges in Visual Studio or through the review request. Offer no discard option of any kind.
5. Execute the choice with the host git tool only, each commit and push passing through its approval card (never bypass review cards or invent side-channel approvals):
   - Leave: state the branch name and workspace path.
   - Commit and push: commit with a clear message, push with upstream tracking, then open a review request with whatever forge tooling the repo uses — or hand the branch name over and say opening the request is the user's job. Keep the branch: never delete it — branch deletion is destructive git and needs the user's explicit ask. A refused push means the remote moved on: investigate, never force-push unless the user asks outright (the host tool offers no force push).
   - Removal is reachable solely when the user volunteers it unprompted: spell out what dies (the branch, its commits, uncommitted and untracked content) and demand one fixed confirmation word typed out; casual assent never counts. Only then carry out the confirmed removal.
6. Done means: suite green on the landed tree; base confirmed; the user's pick carried out; nothing deleted without matching confirmation; close by stating the outcome, the branch's location, and any link.
