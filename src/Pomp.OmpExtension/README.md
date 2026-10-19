# Pomp.OmpExtension

The omp-side pieces of pomp (Visual Studio 2019/2022/2026 host). Plain TypeScript, zero dependencies, no build step:
omp (Bun) loads the `.ts` files directly.

**Compatibility:** tested with omp **18.12.1** (earlier with 18.10.4, 18.10.3, 18.8.6 and 18.4.3) (`can1357/oh-my-pi`); requires omp **>= 18.3.2** (`ctx.agent`, used to keep the
guidance out of subagents; `tool_call` `additionalContext`, used by `warn` mode, exists since 18.3.1). The API facts below
were read from the upstream docs and source (`main`, tree `33f887a`, the 18.4.x line). Loading the package into a real
omp 18.8.6 was verified by an out-of-repo test: all modules load, every `/pomp-*` command (including `/pomp-context`, `/pomp-guard`) is registered,
`/pomp-context` completes and no `extension_error` frame appears. omp 18.12.1 was checked in Visual Studio: the commands are listed, `/pomp-context`
completes, chat turns with tool approvals and editor edits complete and the host log shows no extension error (18.10.4 and 18.10.3 before it also
covered the advisor and built-in role paths). **Not yet verified live:** the dirty-file block, `warn`
mode and skill discovery (see "Verification" and "Open questions").

| Piece | File | What it does |
|---|---|---|
| **pomp-context** extension | `extensions/pomp-context.ts` | Under pomp (`POMP_HOST=1`): appends a short "Visual Studio host" section to the main agent's system prompt. It names only the host tools this conversation can call (offered in `POMP_HOST_TOOLS` **and** enabled in the session, `xd://`-mounted ones included; an agent conversation has its role's tools only, so a Reviewer tab is told about none): read files open in VS with `read_buffer`, use `query_solution` / `read_error_list` / `build_solution`, add files/packages with `add_project_file` / `manage_nuget`, and, when no buffer-edit tool is callable, ask the user to save a file with unsaved changes before editing it. Announces the open solution once per session and again when `vs-state.json` reports a different one, and announces the applicable scoped `.mdc` rules as a hidden message re-sent only when their text changes (both keep the system prompt byte-stable for the provider's prompt cache). Registers `/pomp-context` (status, including how many offered VS tools this conversation can call) and `/pomp-rules`: typed, it notifies the rules that apply now and the rule-file diagnostics; the host's `--request <id>` read gets only the hidden `pomp-rules-result:` notice. |
| **pre-edit** hook | `extensions/pre-edit.ts` | `tool_call` handler that blocks omp's disk-writing tools on files that are **open and dirty in VS**. The reason tells the model to use `read_buffer` + `edit_buffer_hashline` when the session can call both, otherwise to ask the user to save the file (Ctrl+S) and retry with the normal tools. Registers `/pomp-guard` (status). |
| **live-edits** | `extensions/live-edits.ts` | Live for omp's own file tools. `tool_call` reads each target of `edit` / `write` / `ast_edit` / `apply_patch` from disk (files up to 2 MiB) and asks the host to hold the existing ones (hidden confirm `pomp-disk-edit-hold`, waited for 3 s at most), so VS neither reloads an open file nor asks about the outside change while the tool writes. `tool_result` sends those texts to the host (hidden notice `pomp-disk-edit:`, with `error` for a failed call), over the main conversation's channel, so subagent calls count too. A call denied or blocked after `tool_call` has no `tool_result`: `turn_end` / `agent_end` report the session's leftover calls with `error`, so their files are released at once (the host's two-minute limit is the last resort). The host ends the hold and shows the difference to the new text in the editor like a host edit (follow, +/- marks, bookmarks), in any language. Never blocks a call: every failure is swallowed. |
| **network** hook | `extensions/network.ts` | With `POMP_NETWORK=block`, blocks recognized network tool calls and URL reads in addition to the host overlay. Not a shell/MCP network sandbox. |
| **verify-gate** | `extensions/verify-gate.ts` | Appends a fixed "Evidence before completion" section to the main agent's system prompt, and, with the "Verify after edits" option on (`POMP_VERIFY_GATE=1`, default), continues a settling turn once via `session_stop` when source edits were not verified afterwards. Skips docs-only edits, missing solutions, workflow runs and turns that already report evidence. A `write` to an internal URL (`xd://<tool>`, `agent://`, `local://`, …) is not a source edit (`lib/tool-targets.ts`), so reporting review findings through `write xd://report_review_findings` does not trigger it. |
| **untrusted-content** | `extensions/untrusted-content.ts` | `tool_result` hook that labels web/MCP/browser results as external data (one passive line outside the result; the result text is untouched). |
| **compaction-context** | `extensions/compaction-context.ts` | `session.compacting` hook that adds POMP/VS facts (solution, unsaved buffers, workflow phase, denied approvals) to automatic compaction summaries. It is why POMP has no compaction command of its own: the chat's `/compact` runs the engine's. |
| **plan-guard** | `extensions/plan-guard.ts` | The plan-only turn: the `pomp-plan` command (the chat lists it as `/plan`), the `enter_plan_only` tool (the model puts the turn it is in under the same guard) and the `tool_call` guard. During that run only reading, searching, the web, `todo` and read-only host tools are allowed; everything else is refused with a short reason. Only the end of the run (or a new/switched session, or a two-hour ceiling) lifts it; no tool or command does. A cooperative filter, not an OS sandbox. |
| **plan-edits** | `extensions/plan-edits.ts` | The `pomp-plan-note` command: the host sends what the user changed in the work plan (Work > Progress). The note reaches the main agent once, with its next tool result while it works (`tool_result` additional context) or before its next run (`before_agent_start` hidden message), and tells it to keep the plan as it is now instead of writing back the plan it remembers. |
| **workflows** | `extensions/workflows.ts` | The `pomp-workflow`, `pomp-map`, `pomp-review` and `pomp-max` commands (their texts name only host tools the conversation can call; `/pomp-map` refuses without `build_codebase_context`); a `tool_call` hook that tells the host (hidden notice `pomp-review-reporter:`, sent over the main conversation's channel) which agent makes each `report_review_findings` call, so Work › 코드 점검 shows who reported a finding. Never blocks a tool. |
| **features-guidance** | `extensions/features-guidance.ts` | Appends one section to the main agent's system prompt: what `/plan`, `/goal`, `/workflow` and Work > Progress do and when to suggest them, naming the model-callable tools `enter_plan_only`, `request_goal_run` (a host tool: it only shows the user a confirmation card) and `propose_knowledge` only when the conversation can call them; and where a requested skill must be written (`.pomp/skills/<name>/SKILL.md` in the workspace or the user's home) so Tools › Skills lists it. A pure function of the conversation's tool set; no per-request text. |
| **reply-style** | `extensions/reply-style.ts` | While the built-in `chat-style` skill is on (listed in the engine's `<skills>` block; Tools › Skills switches it off), appends its instructions to the main agent's system prompt, because a formatting rule is not a task the model matches by description. A user's own `.pomp/skills/chat-style/SKILL.md` (project, then home) wins over the built-in file. |
| **local-models** | `extensions/local-models.ts` | Registers the model servers saved in the account tab (any OpenAI-compatible server: name, address, optional key; the host passes them as `POMP_LOCAL_MODELS`) as providers `local-...` whose models are the server's `/models` (or `/v1/models`) list, read at start and on catalog refresh (`fetchDynamicModels`). |

**Command names.** The commands this package registers carry a `pomp-` prefix inside the engine (`pomp-workflow`, `pomp-plan`, `pomp-map`, `pomp-review`, `pomp-max`, `pomp-rules`, `pomp-permissions`, `pomp-resources`, `pomp-guard`, `pomp-context`, `pomp-skill`, `pomp-command`, `pomp-recipe`) so they never clash with the engine's own commands. The chat lists and accepts plain names (`/workflow`, `/plan`, …, `/vs-status` for `pomp-context`; `/permissions` is the chat's permission-mode picker, so `pomp-permissions` keeps only its registered name) and the host sends the registered one (docs/ui-protocol.md, "Slash commands"); typing the registered name still works. `pomp-compact` was removed (the engine's `compact`, dispatchable over RPC, does the job).
| **time-context** | `extensions/time-context.ts` | Announces the local date/time and zone as a hidden message, re-sent only when the day changes. |
| **case-review** skill | `skills/case-review/SKILL.md` | Tells the model when and how to show cases as comment cards with `present_case_feedback` (per-case comment, optional workspace image or Mermaid diagram, then wait for the user's comments). The user can also call it as `/skill:case-review`. |
| **vs-project** skill | `skills/vs-project/SKILL.md` | Lazily loaded knowledge: SDK-style vs legacy csproj, TFMs, LangVersion, Nullable, MSBuild props/items, `Directory.Build.props`, NuGet, `.sln`/`.slnx`, VSIX basics. Only its name + description sit in the system prompt; the body is read on demand (`read skill://vs-project`). |
| shared helpers | `lib/vs-state.ts`, `lib/tool-targets.ts`, `lib/announce.ts` | `vs-state.json` reader, Windows path matching, extraction of target files from tool input, on-change hidden-message announcements. |
| manifest | `package.json` | `omp.extensions` lists the extension modules in load order. |
| overlay sample | `pomp-overlay.sample.yml` | `tools.approval.<tool>: allow` for the 18 currently offered host tools (the host itself writes only the tools it offers). |

In omp's default runtime there is no separate hook engine: `--hook` is an alias of `-e`, and hook files are loaded as
extension modules that use the same `tool_call` event. The pre-edit hook is therefore an extension module listed in the
manifest; nothing is placed under `hooks/pre|post/`.

## Loading contract (what the C# host must do)

```
omp --mode rpc
    --cwd "<solution dir>"
    --approval-mode always-ask
    --config "<absolute path>\pomp-overlay.yml"
    -e "<absolute path>\Pomp.OmpExtension"
```

* **`-e` must point at this folder** (the directory containing `package.json`), not at a `.ts` file. The manifest tells omp
  which modules to run, and only a directory lets omp's `omp-plugins` provider also discover `skills/vs-project`.
  Explicit `-e` roots load even with `--no-extensions` (pass it if user/project extensions must not load).
* Give every argument as its own argv element (`ProcessStartInfo.ArgumentList`) and keep `-e <dir>` **followed by another
  flag** (or last). For `-e`/`--hook` values that look like an absolute Windows path but do not end in `.ts`/`.js`, omp's
  parser tries to glue following non-flag tokens onto the value (to repair unquoted paths with spaces;
  `cli/args.ts`, `consumeBuiltInStringValue`). RPC mode takes no positional arguments, so nothing follows in practice.
* `--config` must be an absolute path to an existing, valid YAML mapping: any overlay problem is a hard startup error.
  `PI_CONFIG_FILES` (`;`-separated on Windows) is an equivalent env var.
* Do **not** pass `--no-ui` (approvals are `extension_ui_request` dialogs) or `--no-skills`.
* Environment for the child process:

| Variable | Use |
|---|---|
| `POMP_HOST=1` | **Required.** Without it both modules register nothing (safe when the folder is loaded outside pomp). |
| `POMP_STATE_DIR` | **Required for the guard and the solution notice.** Directory holding `vs-state.json`. |
| `POMP_PRE_EDIT_MODE` | Optional: `block` (default), `warn`, `off`. |
| `POMP_HOST_TOOLS` | Comma-separated names of the host tools offered to omp in this process (e.g. `get_editor_context,read_buffer,query_solution,…`). The texts name a host tool only when it is in this list **and** the session can call it (`lib/vs-state.ts` `callableHostTools`: `pi.getActiveTools()`, which in omp 18.12.1 is `AgentSession.getEnabledToolNames()`, the active tools plus the `xd://`-mounted ones). **Absent = none known** (conservative wording that names no host tool). The text only depends on the two *sets*, so it stays identical while the conversation's tools stay the same (prompt cache); changing the tools already changes the cached prefix. |
| `POMP_VERIFY_GATE` | `1` (default) lets the verification gate continue a settling turn once after unverified source edits; `0` turns the nudge off (the static evidence section still applies). |
| `POMP_HOST_VERSION`, `POMP_VS_VERSION` | Only shown by `/pomp-context`. |
| `POMP_SOLUTION_ROOT` | Not read by these two modules (the state file carries `solutionRoot`); the permission guard compares the engine's working directory with it. |
| `POMP_WORKTREE_BASE` | Folder of isolated team members' copies (docs/architecture.md §8.11). The permission guard lets a subagent work in `<base>\<id>\m` and judges it by the workspace's `.pomp/permissions.json`, never the copy's. |

Strict alternative (no ambient extension discovery, load errors abort startup): repeat
`--trusted-extension "<abs>\extensions\pre-edit.ts" --trusted-extension "<abs>\extensions\pomp-context.ts" --trusted-extension "<abs>\extensions\network.ts"` instead of `-e`
(the trusted and regular loading modes cannot be combined) and add `skills.customDirectories: [<abs>\skills]` to the overlay, because trusted files get
no sibling discovery.

### Host tools (`set_host_tools`)

* **No prefix.** Host tools keep their exact names in omp's registry (`RpcHostToolBridge` reports `definition.name`,
  `SessionTools` registers `finalTool.name`, the approval key is `tool.name`), so the overlay keys are the names
  `get_editor_context, read_buffer, edit_buffer_hashline, query_solution, add_project_file, rename_project_file,
  get_msbuild_properties, read_error_list, read_output_window, build_solution, manage_tests, manage_git,
  list_breakpoints, manage_breakpoints, get_debug_state, control_debugger, list_bookmarks, add_bookmark`. A name that collides with an
  existing tool makes `set_host_tools` fail; none of these collide with omp built-ins.
* **Send `loadMode: "essential"` for every tool.** Omitted, omp treats host tools as `discoverable`, and because
  `tools.xdev` defaults to `true` they are then hidden behind `xd://` devices (`read xd://`, `write xd://<tool>`) instead
  of appearing in the tool list. (`normalizeHostToolDefinitions` keeps only `name, label, description, parameters, hidden,
  loadMode, readsSkillUris`; a host cannot declare an approval tier, so unlisted tools count as `exec`.)
* The overlay's `allow` entries stop omp from prompting for them under `always-ask`; omp's own tools keep the mode.
* The host may offer a subset of these names (a tool that is not ready yet — currently `remove_project_file` pending Windows recycle safety checks — is not offered;
  `edit_buffer_hashline` and `add_project_file` are) and lists the offered ones in `POMP_HOST_TOOLS`; the texts in this
  package never name a tool that is not in that list, nor one the session cannot call (an agent conversation enables only its
  role's tools, `extensions/tools.ts`). Test discovery, like test execution, needs host approval because it may build and execute project code.

### `vs-state.json` obligations

Schema (as specified by the host): `{ version, updatedAt, solutionPath, solutionRoot, activeDocument, openDocuments:
[{ path, isDirty }] }` (property names are matched case-insensitively; a UTF-8 BOM is fine). The extension only trusts
absolute `path`s with `isDirty === true`. The host should replace the file
atomically and promptly whenever a document's dirty flag flips or the solution changes: the guard is exactly as accurate as
this file. Reads retry briefly on `ENOENT`/sharing violations/torn reads (the file is replaced atomically on Windows), then
fail open.

### Health checks

* `get_available_commands` (and the `available_commands_update` frame) lists `pomp-context` and `pomp-guard`
  (`source: "extension"`) when both modules loaded, and `skill:vs-project` (`source: "skill"`) when the skill was
  discovered. Sending `/pomp-guard` or `/pomp-context` as a `prompt` finishes locally (`agentInvoked: false`) and
  shows mode, state availability, the unsaved-file count (and, for `/pomp-context`, how many of the offered host tools
  this conversation can call) through an `extension_ui_request` `notify`.
* Load failures of an `-e` module are written to omp's **stderr** (RPC mode is non-interactive); runtime handler errors
  arrive as `extension_error` frames. The hidden solution announcement is a `custom` message with
  `customType: "pomp-solution"` and `display: false`: do not render it.

## pre-edit hook

* **Tools covered** (real omp 18.x names; omp has no delete or move tool): `edit` (all modes: hashline `[PATH#TAG]` /
  `MV DEST` / `REM`, `apply_patch` `*** Add|Update|Delete File` / `*** Move to`, `patch` incl. `rename`, `replace`,
  `sloppy`), `write`, `ast_edit` (`paths` may be files, directories or globs; a scope containing a dirty file is blocked)
  and the mutating `lsp` actions: `rename` (unless `apply: false`) and `code_actions` with `apply: true` on their `file`,
  and `rename_file` (unless `apply: false`) on its source, which may be a directory: everything below it counts. `apply:
  false` previews write nothing and are not blocked. Destination paths of moves/renames count too. `ast_edit` and `lsp`
  are "discoverable" tools, which omp mounts behind `xd://` by default; the wrapped tool still emits its own `tool_call`
  for a `write xd://<tool>` dispatch, and that inner call is what the guard checks (the outer `write` to an `xd://` URL is
  ignored).
* **`ast_edit` scopes** are normalized like omp's search-scope pipeline before they are matched: surrounding double quotes
  are stripped (`"real/Dirty.ts"`), and several targets flattened into one entry (`a; b`, `a, b`, `a b`) are recovered,
  except that a name which exists as written is never split (so files with spaces keep working). A glob is matched as its
  fixed directory prefix plus a pattern; the prefix is compared both as spelled and as its real path, so `alias/*.ts`
  with `alias` a junction to `real` covers `real/Dirty.ts`, and glob characters in the real path are matched literally.
  File targets of `edit` / `write` / `lsp` also accept a surrounding pair of double quotes (whether every omp file tool
  strips them is not verified, and a Windows file name never contains one, so accepting them can only add a block).
* **How it blocks.** The handler returns `{ block: true, reason }` from the `tool_call` event. omp fires this at
  argument-prep time, before scheduling and before the approval prompt, so nothing is written and the user is never asked
  to approve a call that will be refused; `reason` is returned to the model as the tool error. The reason names the files
  and, when the session can call `read_buffer` and `edit_buffer_hashline`, says to use them; otherwise it tells the model to ask
  the user to save the files in Visual Studio (Ctrl+S), then retry with the normal tools, and never to overwrite unsaved
  changes. `POMP_PRE_EDIT_MODE=warn` lets the call run and returns `{ additionalContext }` instead (omp delivers that only
  after a successful call).
* **Why dirty state instead of hashes.** The design sketches "verify the hash before an edit, inject a warning on conflict".
  A hook cannot do that: omp's built-in `edit` addresses the file *on disk* with omp's own hashline tags, whereas the host's
  `read_buffer` / `edit_buffer_hashline` tags describe the *editor buffer* (and are verified host-side). What actually loses
  work is "the buffer has unsaved changes the disk copy does not", and that is what the host reports in `vs-state.json`.
  `warn` mode is the design's "inject a warning"; `block` (default) is stricter.
* **Paths.** Windows semantics: relative paths resolve against `ctx.cwd`, separators and case are ignored, and real paths
  (junctions, `subst`, short names) are compared as well. The normalization mirrors omp's own tool path handling (`@` / stray
  `:` prefixes, `file://`, `\\?\`, `~`, `/c/...` and `/mnt/c/...` aliases, copied `[path#TAG]` wrappers).
* **Fail open.** omp treats a throwing or slow `tool_call` handler as a block for *every* tool, so the handler catches
  everything: missing, garbled or oversized state, missing `POMP_STATE_DIR` or odd tool input all mean "allow".
* **Not covered**: `bash`/`eval`/`git` (e.g. `sed -i`, `dotnet format`, `git checkout`), MCP tools, workspace-wide effects
  of an LSP rename on files other than `file`, and *reads* of a dirty file (`read`/`grep` return the stale disk copy; only
  the system-prompt guidance steers the model to `read_buffer`).

## pomp-context

* Hooks `before_agent_start` (fires for each new prompt) and returns the system prompt with the guidance appended
  (idempotent, main agent only: subagents may not have the host tools) plus, when needed, a hidden custom message
  (`attribution: "agent"`) such as `Visual Studio solution: <path> (root <dir>).` or `... solution changed: now <path>; was
  <old>.` Whether it was already sent is derived from the session branch (`custom_message` entries), so the handler is
  stateless, tolerates omp's re-runs of the hook, and works across `new_session` / `switch_session`.
* Deliberately **not** injected: active file, selection, dirty flags (volatile; the host puts the editor summary into the
  prompt, and `get_editor_context` exists). The guidance is a pure function of the guard mode and of the set of host tools
  the session can call, so it is byte-identical while the conversation's tools stay the same and keeps the provider
  prompt-cache prefix stable (computed on every prompt, so a role's tools applied right after a conversation starts are
  respected). Size (block mode): about 0.9 k characters with no host tool callable, 1.3 k for the 8 tools the host offers today, 1.1 k for all
  11 (roughly 230 / 330 / 275 tokens at 4 characters per token).
* Scoped `.mdc` rules (up to 5 rules / 24,000 chars, chosen by the active file) and the project resource catalog are
  **not** part of the system prompt either: each travels as a hidden custom message (`pomp-rules`, `pomp-catalog`) that is
  re-sent only when its rendered text changes, headed by a line saying it supersedes earlier messages of its kind. What was
  already announced is derived from the session branch, so the messages survive hook re-runs and session switches, and are
  re-announced when compaction drops them.
* The text follows `POMP_PRE_EDIT_MODE` (it only claims disk edits are blocked in `block` mode) and the callable host tools
  (it names `edit_buffer_hashline`, `add_project_file`, `manage_nuget`, … only when the session can call them; otherwise it
  says: ask the user to save a file with unsaved changes, SDK-style projects pick up new source files automatically, prefer
  `dotnet add package` for package changes). With `POMP_HOST_TOOLS` absent, or in an agent conversation whose role lists no
  host tool (the bundled `reviewer`, `scout`, …), no host tool is named at all.
* `skills/vs-project/SKILL.md` is static, so it is phrased conditionally ("if `add_project_file` is among your tools …").
  Keep it LF-terminated (omp before 18.4.2 handled CRLF-terminated `SKILL.md` files incorrectly).

## Verification

* **Loaded into a real omp 18.12.1** in Visual Studio (18.10.4, 18.10.3, 18.8.6 and 18.4.3 before it, the earlier ones by an out-of-repo test with no provider calls): both commands (`pomp-context`,
  `pomp-guard`) are present, `/pomp-context` completes and no `extension_error` frame appears.
* Type-checked with `tsc --strict` against the real `ExtensionAPI` / event types copied from the upstream tree above
  (a negative test confirmed the types bite), with `verbatimModuleSyntax` + `erasableSyntaxOnly` so the files are plain,
  strippable TypeScript. Re-run after the `POMP_HOST_TOOLS` and the scope-matching changes: no errors in this package.
* 64 behavioural tests with a mock `ExtensionAPI`, run under Node 24 type stripping: 38 for the guard and the state reader
  (block/allow matrix for all edit modes, path spellings, junctions, glob scopes, fail-open cases, retry logic, solution
  announcement dedupe) and 15 for `POMP_HOST_TOOLS` (parsing; texts identical for any order of the same set; 300 random
  tool subsets in all three modes never name an unoffered tool; empty-set wording; sizes; `SKILL.md` LF-only and
  conditional) and 11 for the `ast_edit` scope rules (quotes, whitespace/comma/semicolon lists, globs through a junction,
  literal names with spaces, glob characters in a real path), quoted file targets and `lsp` `rename_file` (directory
  source, `apply: false`).
  These tests are kept outside the repository.
* omp's real `resolveExtensionDirectory` run against this folder: it selects both modules in manifest order.

## Open questions / assumptions

1. **Not verified live** (only loading was): that a dirty-file `edit` / `write` / `ast_edit` is refused with the reason
   above in a running session, the `warn` mode's `additionalContext` delivery, and skill discovery (`skill:vs-project` in
   `get_available_commands`). The first real Visual Studio session should confirm them.
2. `vs-state.json` freshness is the host's job (see above). A stale `isDirty` yields a wrong block/allow; the block reason
   tells the model to have the user save the file (or, when offered, to use the buffer tools), so it is recoverable.
3. If the host omits `loadMode: "essential"`, the guidance names tools the model can only reach through `xd://`.
4. The hook trusts `ctx.cwd` as the base for relative paths (true for the main session; a `task` worktree-isolated subagent
   edits different files anyway).
5. `edit` mode can differ per model (hashline by default, `replace` for some model families); all modes are parsed.
6. omp builds newer than 18.12.1 may rename events or fields; the handlers only use `before_agent_start`, `tool_call`,
   `registerCommand`, `sessionManager.getBranch()` and `ctx.agent.kind`.


## Project policy required

Without `.pomp/permissions.json` nothing extra is enforced: omp's and the host's normal approvals apply, so installing POMP is enough. With the file, unmatched tools are denied; the POMP host also checks the rules before/after approval and refuses startup (and every prompt) in that workspace if this package's policy module is disabled, absent or not registered. The guard reads the file on every tool call, so a saved change applies from the next call without a restart. A `write xd://<tool>` call is omp's transport to a mounted tool (discoverable VS tools, `ast_edit`, `lsp`): the guard ignores that outer `write` and judges the mounted tool's own `tool_call` by its name, so allowing a VS tool does not also need `write`. Existing user policy files are never overwritten or widened automatically. `/pomp-permissions` remains a read-only status command.

Inspect `examples/permissions.readonly.json` for a small host read/search/workflow allowlist, or `examples/permissions.reviewed-src.json` for review-only source proposals under `src`. Copy the chosen contents yourself into the target workspace's `.pomp/permissions.json`, adapting paths and tools only after review. Do not replace an existing policy blindly. Neither example allows shell/process execution, native raw writes, external plugins or arbitrary new tool names. Normal one-use source-application approval still applies. These are cooperative tool filters, not an OS filesystem/network sandbox.

`schemas/permissions.schema.json` supplies a standalone JSON Schema for editor association. Runtime path containment and symlink checks remain authoritative; schema validation alone does not authorize an action.
