# Pomp UI protocol (WebView2 chat UI ⇄ VS host)

Contract between the chat page (`src/Pomp.WebUI`, hosted in WebView2 inside the `Pomp` tool window) and the
host (`Pomp.Core` session controller). The page never talks to omp or to Visual Studio APIs directly
(design Layer 1 → Layer 2 only). C# mirror: `src/Pomp.Contracts/Ui/UiProtocol.cs`.

## Transport

- Page → host: `window.chrome.webview.postMessage(obj)` (an object, not a string).
- Host → page: `CoreWebView2.PostWebMessageAsJson(json)`; the page listens with
  `window.chrome.webview.addEventListener("message", e => e.data)` (`e.data` is already an object).
- Envelope: `{ "type": string, "id"?: string, "payload"?: object }`.
- JSON conventions: camelCase property names, enums as camelCase strings, absent optional fields are omitted.
- The page sends `ui/ready` once its listeners are installed. The host queues everything posted before that
  and then replays it in order, starting with `host/hello`. The host keeps a bounded log of the current session's
  transcript (`chat/*` messages, reset by `session/new`); after every `ui/ready` it sends `chat/replayBegin`, that log and
  `chat/replayEnd`, so a reloaded page shows the conversation again.
- Unknown `type` values must be ignored (forward compatibility), both directions.
- Inbound limits (enforced by the VS host, constants in `WebViewUiChannel`): at most 4 MiB per message (checked before
  parsing; a dropped message is answered with `error` / `message_too_large`), `type` at most 64 characters, and for `ui/log`
  only: at most 64 KiB and a token bucket of 200 messages/s (burst 400, reset on attach and on navigation; excess log lines are
  dropped with one throttled warning). Other message types are never rate-limited. The page refuses to send a message whose
  text exceeds omp's per-message limit (about 1 MiB of UTF-8) and keeps the draft.
  Both permission requests and generic questions block new chat submissions/retries while preserving editable drafts.
  Generic input/editor Enter handling ignores IME composition, including legacy keyCode 229.
- Outbound, the host folds consecutive `chat/assistantDelta` (same message and kind, up to 65,536 characters per folded
  message) and `chat/toolUpdate` (same tool call) into the message still waiting when the page is slow. The queue is bounded:
  at most 2 MiB of UTF-8 JSON per message, 8 MiB and 10,000 messages in the outbox, and 2,000 messages before `ui/ready`.
  When it is full, further `chat/toolUpdate` and `chat/notice` messages are dropped (logged); any other message that does not
  fit — or a post that fails or does not complete within 30 s — puts delivery into a **failed** state instead of silently losing
  history: pending approvals are denied, pending dialogs cancelled, the queue is cleared and one `error` with code
  `ui_delivery_failed` (`retryable: false`) is queued. Nothing else is delivered until the page sends `ui/ready` again; that
  ready starts a new delivery epoch whose replay begins with the `ui_delivery_failed` error, so the page knows the transcript
  may be incomplete. Pending approvals and dialogs together are limited to 128 requests and 4 MiB (they are replayed on every
  `ui/ready`); a request beyond that is denied/cancelled at once. Otherwise everything is delivered in order.
- `ui/toggleFocus` is host-internal: the page sends it when the `shortcuts.toggleFocus` chord of `host/hello` (default `Alt+J`,
  Tools > Options > Pomp > General > Shortcuts) is pressed inside the page (no Meta or AltGr; IME composition, key repeat and keyCode 229 are
  ignored), and the VS host handles it in the
  WebView channel (it never reaches the session). The page app does not send it.
- `ui/pasteFiles` is host-internal too: the chat input sends it for a paste of files or an image without plain text (a page cannot
  read their paths). The VS host reads the clipboard on its UI thread (the file drop list; else the `PNG` format or the bitmap) and the
  session puts the files into the context basket with `composer/prefill { files }`; an image is first saved as
  `<root>/.pomp/local/clipboard/clipboard-<time>.png`. Folders and missing files are left out.
- `ui/status` `{ text, level: "error"|"warning"|"info" }` is host-internal as well: the page's short notices of the chat input, its
  chips, the conversation header, the partner tab strip and the queue (a failed model, thinking or permission change, why a message was
  not sent, a lost target, a failed rename or conversation-list request, a failed Live/bookmark change, a failed queue action) are not
  drawn inside the page. The VS host shows the text, on one line and at most
  300 characters, as `POMP: <text>` in the VS status bar, and logs errors and warnings (a repeated notice is logged once). Dialog and form
  field errors stay where they are. When the page cannot post the message (its channel to Visual Studio failed), the notice shows in the
  banner above the chat instead, until a later notice gets through or the banner is closed.
- `ui/editorFollow` is also host-internal: `{ requestId, action: "set", live?, bookmarks? }` changes only the specified persisted editor toggles.
  The channel intercepts it before session dispatch, so it causes no model request, token usage, or engine restart.
  `host/editorFollow` carries `{ live, bookmarks, requestId?, error? }`
  after page readiness, Options/menu changes, and each request (echoing its `requestId`, with `error` on failure).
  The page disables these controls until this capability/state payload arrives, renders only acknowledged values, and reports failed delivery or a missing reply.
  Before forwarding authoritative `session/info` to the page, the VS channel passes `workingDirectory`, `sessionId`, and `isStreaming`
  to the editor follow service on the UI thread. Session switches do not issue an extra model request or unconditionally erase other sessions' records.

## Structured case feedback

`present_case_feedback` accepts only `{ cases: [{ caseId, title, bodyMarkdown, image?, diagram? }] }`: `image` is a workspace-relative
path of an existing png/jpg/gif/webp/bmp (≤ 1 MiB, so its data URL fits one page message; at most 8 cases with an image), `diagram` is Mermaid source (≤ 4,000 characters).
Its result text names the proposal: `… (proposal <32 hex>) …`. The page shows the proposal as cards inside the assistant reply in
place of that tool call (one comment box per case, one send for all); there is no separate feedback panel. The `case-review` skill
tells the model when to use it.
The host issues the proposal ID and captures the current conversation and original turn target;
model arguments cannot choose the destination. The loaded `pomp-feedback-guard` extension marker
is required at execution, and the extension refuses subagent and unknown callers using the engine's
caller identity. This is cooperative engine enforcement, not isolation against a compromised engine.
A stale connection or ended originating turn cannot register a proposal.

- UI → host: `feedback/command` with `action: refresh|open|draft|submit|delete`, `requestId`,
  `root`, `sessionId`, and for mutations `proposalId`, `revision`; draft/submit also require
  `draftRevision` and `selections: [{ caseId, selected, comment, extra }]`. The cards send `selected` = the comment is not empty and
  `extra: ""`; a submit with no commented case is refused, and the queued message holds only the commented cases (id, title, comment):
  `User feedback with comments on <n> of <total> cases (proposal <id>, revision <r>). …` then a newline and `{"cases":[{caseId,title,comment,extra?}]}`.
  The page shows a message of exactly that shape as the cases and comments (user bubble) or a one-line summary (queue, history
  preview); any other text, or one that does not parse, is shown as typed.
- UI → host: `feedback/image { requestId, root, sessionId, proposalId, caseId }` → host → UI `feedback/imageResult { requestId,
  proposalId, caseId, dataUrl?, error? }`. The host re-checks the case's image path (inside the workspace, no links, type, size)
  when it reads it and answers a `data:image/...;base64` URL; the page shows nothing else as an image. Nothing comes from the network.
- Host → UI: `feedback/state` with matching `requestId` when requested, `root`, `sessionId`,
  `entries`, optional `proposal`, `error`, and `notice`. Creation publishes an unsolicited snapshot.
- Proposal revision and draft revision are separate. A stale draft version is refused, not overwritten.
  Storage is local, workspace-scoped under the POMP data directory, with an exclusive cross-process
  mutation lease. At most 100 proposals, 32 cases per proposal, 8,000 characters per case body,
  and 2,000 characters each per comment/addition are accepted. Full storage is reported, not evicted.
- State transitions: `draft → submitted → dispatching → accepted`. Draft save never requests a model.
  Final submission preserves the cases and selections as one prompt to the stored target. A durable
  dispatch claim precedes queue admission. `accepted` means host queue admission, **not** model success
  or permission to change files. Existing approval and workflow checks remain in force.
- A `submitted` record can retry identical content if no dispatch claim was taken. `dispatching`
  after interruption is uncertain and never automatically resent. Accepted records are not resent.
  The chat queue is in memory and is not restored on application exit; an accepted record is historical
  evidence of admission, not proof that the request survived an exit or ran. Inspect chat and queue.
- Card bodies use the existing safe Markdown renderer. No arbitrary HTML, scripts, automatic external
  image fetches, or external publishing are introduced. Dirty UI drafts must survive unrelated snapshots.

## Host → UI

Host text the page shows (errors, notices, state `message`s, row `note`s, approval bodies) says what happened, the current
state or a reason. It carries no instructions, retry advice or shortcut text; an input-validation error may name the
accepted limits. Text written for the model (tool results, prompts) is exempt; tool cards show it as conversation content.

| type | payload | notes |
|---|---|---|
| `host/hello` | `{ version, vsVersion?, locale?, includeEditorContextByDefault, composerPredictions, approvalMode, shortcuts }` | first message after `ui/ready`; sent again (nothing else resets) when the language option (Tools > Options > Pomp > General > Chat > Language: Automatic / 한국어 / English) changes the effective locale, and when the approval mode or the shortcuts change (the page compares nothing; it applies what it gets). `approvalMode` is omp's approval mode in effect: `alwaysAsk` \| `write` \| `yolo` (the composer's permission-mode chip shows it; `permission/mode` changes it). `shortcuts` = `{ send: "Enter"\|"Ctrl+Enter", sendTargetPicker, previousTarget, nextTarget, contextBasket, editorContext, liveFollow, liveBookmarks, permissionMode, allow, deny, permissionsView, toggleFocus }` from Tools > Options > Pomp > General > Shortcuts, normalized chord strings ("Ctrl+Alt+T"; a key may also be `,` `.` `;` `'`; empty = none; defaults Enter, Ctrl+Alt+T, Alt+, , Alt+. , Alt+; , Alt+' , Alt+L, Alt+K, Alt+Q, Alt+A, Alt+D, Ctrl+Alt+P, Alt+J). `locale` is `ko-KR`/`en-US` for an explicit choice, else VS's UI culture |
| `host/editorFollow` | `{ live, bookmarks, requestId?, error? }` | VS-host editor capability and applied settings; independent toggles, live Options/menu synchronization, and correlated replies to `ui/editorFollow`. |
| `bridge/state` | `{ state, message?, attempt?, nextRetryMs?, reason? }` | `state`: `stopped` `starting` `handshaking` `running` `reconnecting` `failed` `stopping`. Input is enabled only in `running`. `failed` shows *Restart* and *Show log* actions, except with `reason: "setupRequired"` (omp has no model provider yet), where the page shows the engine setup panel instead (see "Engine setup"), and `reason: "workspaceTrustRequired"` (the workspace has engine configuration the user has not trusted), where the chat shows the trust card instead (see "Workspace trust"). |
| `workspace/trustState` | `{ requestId?, root, required, trusted, fingerprint, files: [{ path, kind, detail? }], truncated?, error? }` | what omp would load from the workspace that runs code, starts processes, changes approvals or includes outside files (`kind`: `settings`, `mcp`, `tools`, `lsp`, `plugins`, `extensions`, `include`; `detail`: e.g. "MCP 서버 2개" or the outside file an include pulls in). Posted when omp is about to start and as the answer to `workspace/trust`. See "Workspace trust". |
| `theme/changed` | `{ kind: "dark"\|"light", colors: { [cssVar]: value }, fontFamily?, fontSizePx? }` | apply every `colors` entry with `document.documentElement.style.setProperty(name, value)` |
| `session/info` | `{ model?: { provider, id, name? }, thinkingLevel?, sessionId?, sessionName?, contextUsage?: { tokens, contextWindow, percent, breakdown? }, isStreaming, workingDirectory?, fastMode?: { available, enabled, active } }` | `contextUsage.breakdown` = `{ systemPrompt, systemTools, hostTools, mcpTools, systemContext, skills, messages, messagesEstimated?, autoCompactBuffer, free, contextWindow, measuredAt }` (tokens; `messagesEstimated` = the engine printed no Messages row for this model, so `messages` is the host's estimate: the usage total minus the printed rows, shown with ≈): the engine's own `/context` figures, which the host runs through the command queue after every terminal `agent_end` and once for a conversation it has not measured yet (first state, new or switched session), and only while the engine's command catalog lists `context` with `source: "builtin"` (a prompt template or skill of that name never counts: it would start a model turn); a measurement never starts while one is still waiting for its output, and after two refused, late or unreadable measurements in one conversation the host stops asking for that conversation; absent until then. `hostTools` and `mcpTools` are the host's estimates of the VS and MCP shares inside `systemTools` (definition sizes, four characters per token). The meter paints one segment per source in a fixed order (prompt, tools, MCP, context files, skills, messages) and lists them in its popover; the bar's filled width is still `percent`. composer (context meter, model / effort chips, fast toggle). `fastMode` comes from `get_state` (`fastModeEnabled`/`fastModeActive`); `available` = the model's provider family has a priority tier (openai, openai-codex, google, anthropic-messages, openrouter of those classes) or fast mode is on. `contextUsage` is refreshed after every finished assistant message, not during streaming. A new `sessionId` while the transcript has items means the earlier conversation is gone from omp (the UI says so once). |
| `chat/turnStarted` | `{ promptId, clientMessageId?, text?, attachments?: [{ path }], editorContext?: { path, startLine?, endLine?, caretLine? }, team?, agent?, agentInstance?, main?, from?: { kind: "agent"\|"team", id } }` | the host took the user message `clientMessageId` as prompt `promptId` (posted before omp sees it). Exactly one `chat/turnEnd` with the same `promptId` follows — possibly at once for validation or engine errors. A busy submission is acknowledged by `chat/queued` instead and has no turn yet. `text`/`attachments` are the user's input as typed (no editor context): a page that shows no bubble for `clientMessageId` (a replay, or a prompt started elsewhere) creates the user bubble from them; otherwise they are ignored. `editorContext` names the active file actually prepended to the prompt and updates both optimistic and replayed bubbles with a muted reference chip. Its `path` is workspace-relative with forward slashes inside the workspace, otherwise the full path (also the tooltip). Lines are 1-based: `startLine`/`endLine` are an inclusive non-empty selection range (an end at column 1 excludes that line); otherwise `caretLine` labels the nearby editor lines. It is omitted for context-off sends, slash commands, or no active document. Restored history recovers it only from POMP's leading `<vs_context>` block, after any host routing brief and origin marker. `team`/`agent`/`agentInstance` give the destination the message actually went to (the explicit target, or the conversation partner the host applied), so a replayed bubble keeps its "→ target" badge; `main: true` when POMP itself answered (no badge), so a retry after a partner change still goes to POMP. `from` is the validated `chat/send.from`: a bubble the page creates from this message belongs to that agent/team tab as well as to its destination. Restored history of another session shows the extension's routing brief (`<!-- pomp-team:… -->` etc.) at the start of the user text instead, and gives `from` from the origin marker `<!-- pomp-from:<kind>:<id> -->` the host recorded in the prompt (never shown: the host removes it from `text`); session titles, previews and archive pages leave both out. |
| `chat/assistantStart` | `{ messageId }` | new assistant bubble |
| `chat/assistantDelta` | `{ messageId, kind: "text"\|"thinking", delta }` | append; render markdown incrementally |
| `chat/assistantNarration` | `{ messageId, text }` | the thinking block that just ended was narration, prose the model addressed to the user that the provider sent as thinking (omp 18.12+ `narration: true`, e.g. Claude Opus 5.5 between tool calls): move `text`, the end of that message's thinking, to its answer text. A thinking that does not end with `text` (capped) is left as it is. A message delivered whole, a restored conversation and the conversation preview show narration as answer text directly |
| `chat/assistantEnd` | `{ messageId, stopReason?, errorMessage? }` | finalize the bubble |
| `chat/toolStart` | `{ toolCallId, toolName, args?, isHostTool }` | collapsible tool card inside the current turn |
| `chat/toolUpdate` | `{ toolCallId, text }` | progress text (replace, not append) |
| `chat/toolEnd` | `{ toolCallId, toolName, isError, resultText?, durationMs? }` | `resultText` is already truncated by the host |
| `chat/turnEnd` | `{ promptId, status: "completed"\|"aborted"\|"error", error?: { code, message, retryable } }` | re-enable input; show error banner on `error` (codes below) |
| `chat/notice` | `{ level: "info"\|"warning"\|"error", message }` | inline system note in the transcript |
| `chat/advisorNotes` | `{ notes: [{ advisor, severity?, note }] }` | notes advisors gave the chat agent, one card in the transcript (see "Advisors") |
| `permission/request` | `{ requestId, source: "hostTool"\|"ompApproval", kind, toolName, title, message?, tasks?: [{ agent, task }], target?, diff?, timeoutMs?, canRemember }` | an approval card inside the transcript, see "Request cards" below. `canRemember` absent = `true`. |
| `permission/dismiss` | `{ requestId, reason? }` | the card closes without an answer (timed out / cancelled by omp) and leaves its record in the transcript |
| `permission/modeResult` | `{ requestId, ok, mode, error? }` | exactly one answer per `permission/mode`; `mode` is the mode in effect after the answer. A changed mode restarts omp (bridge/state `stopping` → … → `running`, the conversation is restored) and a new `host/hello` carries it. |
| `dialog/request` | `{ requestId, method: "select"\|"confirm"\|"input"\|"editor", title?, message?, options?, placeholder?, defaultValue?, timeoutMs? }` | generic question from an omp extension, shown as a card inside the transcript (see "Request cards") |
| `dialog/dismiss` | `{ requestId, reason? }` | |
| `session/fastResult` | `{ requestId?, ok, error?, enabled, active }` | exactly one answer per `session/fast`; `session/info.fastMode` follows |
| `knowledge/draft` | `{ root, path, startLine, endLine, text }` | the editor's POMP ▸ Add to Knowledge; see "Knowledge" |
| `composer/prefill` | `{ text?, files?: [{ path, fullPath }], includeEditorContext, send, target?: { kind: "agent"\|"team", id }, pickTarget? }` | the code editor's POMP menu (explain, review, ask, add file, and "to agent or team", which sends `target`: the page selects it as the one-message send target, shows the main conversation tab and focuses the input; the chip then shows that target's picture and name; past the menu's 15 entries its "show all" sends `pickTarget`: the main conversation is shown with the send-target picker open). The page shows the chat, adds `files` to the context basket, turns the editor context on when asked, and puts `text` into an empty input (an unsent draft is kept and the text appended, never sent). `send` sends it once, right away, if the chat can take a message; otherwise the text stays in the input. A page that was not ready gets it once, at the end of its `ui/ready` snapshot (if within a minute). Bounded: text ≤ 4000, ≤ 20 files |
| `work/open` | `{ view }` | open that view of the work tab (`review`, `plan`, …). An event, not state: sent once when the user started something outside the page, today after an inline edit proposal is staged (the review opens at once). Ignored while a permission/dialog is up and for unknown views. The page also accepts the Tools ids (`extensions`, `instructions`, `index`, and the older `skills`, `mcp`, `rules`, `workflows`), and `hooks` opens Agents › 규칙. |
| `engine/info` | `{ source: "bundled"\|"settings"\|"installed", version?, verified, expectedVersion?, path? }` | which omp runs (state, also in the `ui/ready` snapshot; sent at every engine start). `bundled` + `verified: true` = the engine this POMP release ships, size and SHA-256 checked; `settings` = the omp path of Tools > Options > Pomp; `installed` = a development package without an engine uses an omp found on PATH / usual folders. `expectedVersion` is the omp version the release ships (absent without a bundled engine). For an unverified engine named `omp`, `version` arrives in a second `engine/info` after one `omp --version` run. The page shows it at the bottom of the ⋯ menu and, while unverified, a dismissible warning above the chat. See docs/architecture.md §4 "엔진 버전". |
| `session/newResult` | `{ requestId?, status: "completed"\|"failed"\|"cancelled", message? }` | exactly one answer per `session/new`; the page clears its transcript only on `completed` and shows `message` otherwise (`cancelled`: an omp extension vetoed it). A non-null `requestId` that is not the page's pending `session/new` (late, duplicate or abandoned) is ignored; an absent/null id keeps the legacy meaning (a new session started outside the page, e.g. by a VS command) |
| `workspace/files` | `{ requestId, files: [{ path, fullPath }], truncated, error? }` | answer to `workspace/findFiles` / `workspace/pickFiles` with the same `requestId`; `path` is workspace-relative (forward slashes) when inside the workspace, else the full path. A late answer to a superseded request is ignored by the page. |
| `chat/replayBegin` | `{ truncated }` | after `ui/ready`: drop the transcript (messages, tool cards, turn state) and rebuild it from the following `chat/*` messages; `truncated` = the oldest part of the session is no longer available (show one notice) |
| `chat/replayEnd` | `{}` | end of the replayed transcript; live messages follow. A turn that was still open in the replay stays open (its `chat/turnEnd` comes live). |
| `agents/state` | `{ root?, roles: Role[], teams: Team[], diagnostics: string[], main, rolesHidden?, teamsHidden? }` | agent roles and teams state; included in `ui/ready` snapshot and published on change / workspace change. No count limit: a list too large for one message keeps its front and counts the rest in `rolesHidden` / `teamsHidden` |
| `agents/reply` | `{ requestId, ok, error?, name?, transcript?, template?, preview? }` | answer to `agents/command`; `transcript: { agent, messages: [{ role: "user"|"assistant"|"toolResult"|"tool", text }], truncated }` (system/developer messages and engine reminder blocks are removed; an assistant turn that only submitted a result carries that result as `text`); `template`/`preview` for team export/import |
| `computer/state` | `{ policy: "off"\|"perConversation", browser, confirmReads, desktop, pending, available, message? }` | computer use of this conversation (docs/agent-teams.md §4); state, also in the `ui/ready` snapshot. `desktop` = on now; `available` = the toggle can switch it on (policy not off and the extension's guard loaded); `message` says why it is unavailable or why it was switched off (another conversation, an engine restart) |
| `computer/reply` | `{ requestId, ok, error? }` | exactly one answer per `computer/set` |
| `error` | `{ code, message, retryable }` | a host-level error not tied to a turn. Sent when the VS host drops an inbound message that is too large (`message_too_large`; a page waiting for `chat/turnStarted` treats it as a failed send) and when outbound delivery failed (`ui_delivery_failed`, see Transport: approvals were denied and the transcript may be incomplete) |

`kind` of a permission request: `fileWrite` `projectChange` `processExec` `debuggerControl` `breakpointChange`
`bookmarkChange` `packageChange` `vcsWrite` `testRun` `other`.

### `chat/turnEnd` error codes

| code | when | retryable |
|---|---|---|
| `timeout` | omp did not acknowledge the prompt within 30 s; it may still run, so the UI must not resend blindly | no |
| `message_too_large` | the prompt frame would exceed omp's 1 MiB frame limit (editor context included) | no |
| `send_failed` | writing the prompt to omp failed (pipe closed, omp not running) | yes |
| omp's `code` or `prompt_rejected` | omp answered the `prompt` command with `success: false` | no |
| `connection_lost` | The agent process ended while the turn was open (the message says why, incl. stderr) | yes |
| `provider_error` / `http_<status>` | omp's `prompt_result` reported a provider error; `retryable` comes from omp | per omp |

A turn stopped with *Stop* ends as `aborted`: normally from omp's own `prompt_result`, otherwise by the host once omp has
been idle for the settle delay (5 s).

## UI → Host

| type | payload | notes |
|---|---|---|
| `ui/ready` | `{}` | |
| `ui/log` | `{ level: "debug"\|"info"\|"warning"\|"error", message }` | page-side diagnostics into the host log (≤ 64 KiB) |
| `ui/editorFollow` | `{ requestId, action: "set", live?, bookmarks? }` | VS host only; persist specified toggles. Answered by `host/editorFollow`; never forwarded to omp. |
| `chat/send` | `{ clientMessageId, text, includeEditorContext, attachments?: [{ path }], team?: string, agent?: string, agentInstance?: string, main?: true, from?: { kind: "agent"\|"team", id: string } }` | an idle message starts a turn; a busy message enters the host command queue and receives `chat/queued`, not `chat/turnStarted`. `attachments` are full paths (from `workspace/files`); the host lists them in the prompt for omp to read. `team` is an optional team id; when set, the message is dispatched to that team through `/pomp-team`. `agent` (a role name) makes it a new assignment run by exactly that agent (`/pomp-agent`); `agentInstance` (a subagent id from this conversation's `workspace/backgroundState` jobs) delivers it to that existing agent over omp's IRC (`/pomp-dm`). At most one of `team`/`agent`/`agentInstance`; an unknown target ends the turn with `send_failed` (docs/agent-teams.md §3.1). `main: true` makes POMP itself answer whatever the conversation partner is (page actions of the main conversation: work plan continue, code-review discuss, editor menu auto-send; retries of POMP answers); combined with another target it ends the turn with `send_failed`. `from` names the agent/team tab the message was sent from; the page sends it only when that tab sends to a different one-message target (another agent, team, running instance, or `main`), never for an editor or work request that moves the view to Default (POMP). It is view only and never routes the message: the host keeps it only when `kind` is `agent`/`team` and `id` is a valid role/team name (the partner-id rule), otherwise ignores it. A kept `from` rides along in `chat/turnStarted.from` (also for a queued message) and is recorded as `<!-- pomp-from:<kind>:<id> -->` before the composed prompt (after the extension's routing brief), so a restored transcript returns it; a slash command for POMP itself gets no marker, because omp parses the command only at the start. |
| `workspace/countDraftTokens` | `{ requestId, root, sessionId, modelId, provider, text }` | the basket details' draft counter. The host asks a hidden POMP extension command, which counts with the engine's local tokenizer for the current model (no provider request). Answered by `workspace/tokenCount` `{ requestId, tokens, estimated, method: "catalog-native"|"fallback-native"|"byte-upperbound"|"byte-estimate"|"empty", encoding?, error? }`; `estimated: true` (no exact tokenizer for the model, or the tokenizer is unavailable) shows the value with ≈. |
| `workspace/countBasketTokens` | `{ requestId, root, sessionId?, includeEditorContext, attachments?: [{ path }] }` | what the next message adds beyond its text: the editor context (gathered exactly as a send would, when it is on) and the attachment list, as a characters-based estimate (ASCII four per token, other characters one each — not the model's tokenizer). Answered by `workspace/basketTokens` `{ requestId, ok, tokens, editorContext, attachments, error? }`; the meter shows it as a hatched "basket" segment after the filled part. The page sends it when the attachments or the editor-context switch change (debounced) and after each new breakdown. |
| `workspace/findFiles` | `{ requestId, query, limit? }` | files for the `@` mention list: case-insensitive substring of the workspace-relative path (empty query = open documents first), `limit` 1..100 (default 30). Answered by `workspace/files`. |
| `workspace/pickFiles` | `{ requestId }` | the host shows a multi-select file dialog (starting in the workspace root). Answered by `workspace/files` (empty `files` when the user closed the dialog). |
| `chat/stop` | `{}` | abort the running turn (design: cancel): pending omp approvals are released (denied) first |
| `permission/mode` | `{ requestId, mode: "alwaysAsk"\|"write"\|"yolo" }` | the composer's permission-mode chip. The host saves the option (Tools > Options > Pomp > General > Agent > Approval mode) and, when it changed, restarts omp with the new `--approval-mode`; refused while a reply is in progress or when the host cannot write settings. Answered by `permission/modeResult`. The host's own gate applies the mode at once: `write` answers file, project, breakpoint and bookmark changes of VS tools without asking (process runs, tests, debugger, Git and package changes still ask), `yolo` answers everything except omp's forced prompts (a `Reason:` line, provider safety checks) and omp's computer and browser helpers, which always ask. |
| `permission/respond` | `{ requestId, decision: "allow"\|"allowForSession"\|"alwaysAllow"\|"deny", note? }` | exactly one answer per request. With `canRemember: false` the host treats `allowForSession`/`alwaysAllow` as `allow`. `note` reaches the model only for host tools. |
| `dialog/respond` | `{ requestId, value?, confirmed?, cancelled? }` | `select`/`input`/`editor` → `value`; `confirm` → `confirmed`; closing → `cancelled: true` |
| `session/fast` | `{ requestId, enabled }` | omp `set_fast_mode` (the priority service tier of the current model's family, omp's `/fast`); not queued behind chat commands; at most 4 in flight |
| `session/new` | `{ requestId }` | start a fresh omp session; the page keeps its transcript (and disables sending) until `session/newResult` |
| `bridge/restart` | `{}` | restart the omp process (after `failed`) |
| `workspace/trust` | `{ requestId, action: "refresh"\|"trust"\|"open", root?, fingerprint?, path? }` | `refresh` re-scans; `trust` records the `fingerprint` the page showed for `root` and starts omp (refused with `error` and the current list when the files changed meanwhile or the workspace changed); `open` opens one listed `path` in the VS editor. Not queued behind chat commands. Answered by `workspace/trustState`. |
| `log/open` | `{}` | show the pomp log in VS |
| `settings/open` | `{}` | open Tools > Options > Pomp |
| `setup/command` | `{ requestId, action, provider?, key?, modelId?, level?, promptId?, value? }` | engine setup (accounts, login, API keys, model, thinking level, usage); see "Engine setup" |
| `session/history` | `{ requestId, action, … }` | session list / switch / preview / rename / compact; see "Sessions" |
| `agents/command` | `{ requestId, action: "refresh"\|"saveRole"\|"deleteRole"\|"saveTeam"\|"deleteTeam"\|"transcript"\|"exportTeam"\|"openTeamExport"\|"previewTeamImport"\|"importTeam"\|"openRole"\|"builtinRole", scope?, role?, team?, name?, expectedHash?, subagentId?, sessionId?, text?, previewHash? }` | agent role and team management; answered by `agents/reply`; see "Agents and teams" |
| `computer/set` | `{ requestId, desktop }` | switch computer use of this conversation on or off: the host runs omp's `/computer on\|off` (no model request) in order with the chat commands; switching on is refused during a reply. Answered by `computer/reply`. 입력한 `/computer`는 페이지가 처리하며 `/computer:…`·특이 공백을 포함한 변형이 호스트에 도달해도 가로채 거부한다 (`send_failed`) |
| `workspace/agentPlan` | `{ requestId, sessionId, base: [{ name, tasks: [{ content, status, blocker? }] }], phases: [...] }` | the user's edit of the agent work plan (`session/info.todoPhases`), sent to omp as `set_todos` without a model request. Answered by `workspace/agentPlanResult` `{ requestId, ok, error? }`. See "Agent work plan editing" |
| `workspace/agentPlanPage` | `{ requestId, sessionId, page, size, query? }` | one page of the WHOLE plan (any number of tasks), answered by `workspace/agentPlanPageResult` `{ requestId, ok, error?, revision, editable, page, size, total, phases: [{ index, name, taskCount }], rows: [{ phase, task, content, status, blocker? }] }`; `phase`/`task` index the whole plan, `query` filters task content, blocker and phase name, `total` counts the matches and `page` is clamped |
| `workspace/agentPlanEdit` | `{ requestId, sessionId, revision, op: "addTask"|"updateTask"|"deleteTask"|"addPhase"|"renamePhase"|"deletePhase", phase?, task?, name?, content?, status?, blocker? }` | one change to the whole plan, refused unless `revision` is the plan omp holds now; answered by `workspace/agentPlanResult`. See "Agent work plan editing" |

### Agent work plan editing

- The host handles `workspace/agentPlan` in order with the chat commands, so no prompt starts between its checks and the edit. It refuses while a turn, an engine run, background work, a host tool, an approval/question or an unpaused queue item is active, while the conversation is recovering or switching, and when `sessionId` is not the current conversation.
- `base` must equal the plan omp holds now, exactly (re-read with `get_state` just before the edit); otherwise the edit is refused and nothing changes. A plan beyond the display limits (20 phases, 200 tasks, names ≤ 200, content ≤ 600, blocker ≤ 300 characters), with control characters or an unknown status is not editable here.
- `phases`: statuses `pending`, `in_progress`, `completed`, `blocked`, `abandoned`; non-blank one-line name and content; at least one task per phase; an empty blocker is dropped. omp (18.4.3 through 18.12.1) does not validate `set_todos`, so the host does.
- The Work tab shows the whole plan page by page: `session/info` carries `planTaskCount`, `planOpenCount` and `planRevision` (a hash of the whole plan) next to the first 20 phases / 200 tasks in `todoPhases`, and the page asks `workspace/agentPlanPage` again whenever the revision changes. Each row is edited or deleted, tasks and phases are added, phases renamed or deleted with `workspace/agentPlanEdit` under the same guards as above; there is no limit on the number of phases or tasks (names ≤ 200, content ≤ 600, blocker ≤ 300 characters; only a plan whose `set_todos` frame would exceed about 1 MiB is refused), deleting the last task of a phase removes the phase, and an empty plan takes edits without a revision.
- omp keeps the edit in memory only: the model's next todo update builds on it and records it in the conversation, but an engine restart, session switch or context compaction re-reads the last plan recorded in the conversation. When unfinished items remain, omp may ask the model to continue at the end of the next run. The page shows these limits in the editor.

## Conversation partners, command queue and agent run history

All three state messages below are included in every `ui/ready` snapshot and republished on change. Commands are ordered with chat sends on the host's serial command queue. Each command receives its matching reply `{ requestId, ok, error? }` exactly once.

### Conversation partner

- `conversation/partner` (UI → host): `{ requestId, partner: { kind: "main"|"agent"|"team", id? } }`, answered by `conversation/partnerReply`.
- `conversation/partnerState` (host → UI): `{ sessionId?, partner: { kind, id? }, instance?, title?, message? }`.
- With no explicit `team`, `agent`, `agentInstance` or `main`, `chat/send` uses the current conversation partner. A one-message target overrides it without changing the preference. Only usable, non-shadowed roster roles/teams can be selected.
- Agent partners start with `/pomp-agent`; the host remembers the first newly started matching job in that turn. Subsequent messages use `/pomp-dm` while that id remains in this conversation's background jobs. If it disappeared (including after engine restart), the next message starts a new assignment and `message` explains the fallback. Team partners use `/pomp-team` on every message.
- Preferences are persisted per omp session id in the workspace's `.pomp/local/partners.json`, atomically, with no entry count limit: past 4 MiB the conversations used longest ago are removed first. Damaged files reset with a log warning. Process-local instances are not persisted. Session-list `entries[].partner` is `{ kind, id? }` for an agent/team or `null` for main.

### Host command queue

- `chat/queued` (host → UI): `{ clientMessageId, itemId }`. This acknowledges a queued submission, not a started turn.
- `chat/queueState` (host → UI): `{ sessionId?, items: [{ id, text, attachments?, includeEditorContext, team?, agent?, agentInstance?, createdAt }], paused, reason? }`. `sessionId` is the original conversation owning the queue, and is null when empty. The page can compare it with `session/info.sessionId` to disable resume in another conversation; the host also refuses that resume.
- `chat/queue` (UI → host): `{ requestId, action: "edit"|"remove"|"move"|"clear"|"pause"|"resume", id?, text?, index? }`, answered by `chat/queueReply`. Move uses a zero-based final index; edit changes only typed text. Clear removes all items but does not implicitly resume a paused queue.
- The queue belongs to this VS window, is in memory only, and retains typed text, attachments, editor-context choice and captured routing target. Agent-partner continuation is resolved at dispatch, so successive queued messages can continue the remembered instance. Normal completion dispatches the first item through normal `chat/send` handling with a fresh client message id.
- Error, abort, Stop, session change or engine restart pauses retained items. Retained items never resume automatically: the user must resume in the original conversation or clear the items. If the queue is empty, the first fresh submission starts an unpaused queue; a submission received before Stop remains paused. The host never supplies omp `streamingBehavior` (`steer`/`followUp`).
- No item count limit; each text must fit the normal prompt frame limit, and the complete queue state has a 1,800 KiB UTF-8 budget to fit the outbound envelope. Rejection is a host `error` with `code: "queue_rejected"`, `retryable: false`; no turn starts.

### Agent run history

- `agents/historyState` (host → UI): `{ entries: [{ id, sessionId, promptId?, target: { kind: "team"|"agent"|"dm", id, title }, text, startedAt, endedAt?, status: "running"|"completed"|"aborted"|"error", pinned, label?, members: [{ agent, jobId, status, toolCount, tokens: number | null, durationMs: number | null, result: string | null }] }], truncated }`. `result` is the member's final submitted result (≤ 8,000 characters); usage is null when the engine reported none.
- `agents/history` (UI → host): `{ requestId, action: "refresh"|"delete"|"clear"|"pin"|"unpin"|"label"|"rerun", id?, label?, filter?: { kind?, id?, status?, includePinned? } }`, answered by `agents/historyReply`. Refresh republishes state and replies ok. Label is at most 80 characters without control characters. Clear applies all supplied filters and retains pinned entries unless `includePinned: true`; explicit delete can remove a pinned entry.
- Rerun uses the recorded typed text and exact target, without editor context or attachments. It sends immediately when idle or enters the host queue when busy or when retained items exist. An empty queue's stale pause does not delay an idle rerun. A vanished direct-message target remains a direct-message target and fails normal validation rather than being silently redirected.
- `promptId` links a run entry to `chat/turnStarted.promptId`. Each `workspace/backgroundState` job also includes `promptId` (null without an open turn), assigned by the host once when it first observes that job and kept through progress/end; engine-provided associations are not trusted. These ids are at most 128 characters without control characters and are reset with the engine/session job identity.
- Team, agent and direct-message turns retain only typed text (at most 4,000 characters), target metadata, timestamps/status and allowlisted metrics for jobs started during the turn. Job progress/end updates those metrics. No composed prompts, editor context, attachment paths, tool payloads or file contents are stored.
- Storage is `<DataDirectory>/agent-history/<workspace hash>.json`, atomically, with no entry limit: the file keeps its newest entries while it fits one UI message (1.75 MiB); oldest unpinned entries are removed first, then oldest pinned entries if necessary. Damaged files reset with a log warning. `truncated` indicates the size bound removed entries.

## Tools, skills and native MCP management

도구 탭은 **대화 설정 · 권한 · 스킬 · MCP · 규칙 · 워크플로 · 지식**. 대화 설정에는 사용할 도구·화면 입력 제어를 두고, 스킬 행의
**채팅에서 사용**으로 명령을 채팅 입력에 넣는다(전송하지 않음). 규칙은 **지금 적용 중 → 규칙** 순서이며 첫 구역은
다음 프롬프트에 적용할 규칙이다. MCP는 **MCP 서버 → 추가할 수 있는 서버**(카탈로그) 순서다. 코드 검색 인덱스는 어휘 갱신 및 로컬 임베딩 옵션 변경에 따른 벡터 재구축·중지를 자동으로 처리한다.
하위 화면 칩에는 항목 수를 표시하고 0은 흐리지만 누를 수 있다. 직접 선택하지 않았고 현재 화면이 비었으면 항목이 있는 첫 화면으로
간다. 선택은 이 대화 동안 유지한다. 목록은 열 이름이 있는 표(스위치·배지·줄 끝 아이콘, 검색과 20개씩 페이지)이고, 빈 목록은 열 이름과
빈 줄만 보이며 안내 문구는 두지 않는다. 카드 머리의 생성 버튼과 파일 기반 목록·적용된 변경 기록의 새로 고침은 비어 있어도 유지한다.

These are state messages, not transcript events: `tools/state`, `skills/state` and `mcp/state` are included in every
`ui/ready` snapshot before transcript replay and republished on lifecycle/resource changes. Each command receives
its matching reply with the original `requestId`. Commands use the host serial queue shared with chat submissions.
Tool selection is refused while a reply, inline request, pending approval/question or uncertain session switch is active;
skill/MCP changes are likewise refused (metadata refresh is still allowed). Busy-at-receipt changes do not wait to
become idle and then silently apply.

### Main-conversation tools

- `tools/state`: `{ sessionId?, tools: [{ name, group: "files"|"edit"|"exec"|"web"|"agents"|"host"|"mcp"|"other", server?, description?, active, defaultActive }], pending, available, message? }`.
  At most 300 registered tools, grouped and sorted by server/name; descriptions are bounded to 200 characters.
- `tools/command`: `{ requestId, action: "refresh"|"set"|"reset", active?: string[] }` → `tools/reply { requestId, ok, error? }`.
  `set` replaces the visible selection; active registered tools beyond the 300-row catalog remain unchanged.
- The POMP extension's local `/pomp-tools list|set <json-array>|reset` command uses stock
  `getAllTools`, `getActiveTools` and awaited `setActiveTools`. No inference or user turn is started. The host confirms
  the extension command exists before sending it; missing support reports `available: false`, never a model prompt.
  Results use a reserved base64 notification prefix and host-generated correlation id, bound to the originating engine
  connection/session. Malformed, stale and management notifications are consumed rather than displayed as chat notices.
- Startup active tools are defaults, including active host RPC tools registered later. New/switched conversations and
  engine restarts restore defaults. Selections are process-local, not persisted per saved session. Disconnect clears the
  available catalog. This changes the main conversation only: subagent definitions and tool approval/policy rules stay intact.
- Host tools are grouped by the actual host registry. MCP grouping preserves actual engine names and uses configured
  server names when possible; stock canonical names use `mcp__<sanitized-server>_<tool>`, while double-underscore aliases
  are recognized defensively. Grouping never rewrites a registered tool name.

컴퓨터 제어 권한은 `extensions/computer-guard.ts`가 검증한다. `SettingsManager.create(ctx.cwd).rawValue(...)`로 **현재 세션의
실제 computer/browser 활성 설정**을 읽고, 활성 기능에 `computer-state.json`의 같은 `sessionId`·기능별 POMP 허용이 없으면
`eval`을 막는다(동적 코드·prelude도 바깥 eval에서 차단). 직접 `computer`·`browser` 호출도 같은 세션의 해당 기능 허용과 메인
세션이 필요하다. 설정 API를 읽지 못하면 `eval`을 차단하고, 상태 파일을 읽지 못하면 활성 기능의 `eval`과 직접 제어 호출을
차단한다. 하위 에이전트는 화면·브라우저를 쓸 수 없으며 제어 허용이 있거나 상태 파일을 읽지 못하면 `eval`도 차단한다.
환경 변수만으로는 허용되지 않는다. `/computer`의 모든 명령 경계 철자(콜론, 특이 공백, FormKC 정규화 포함)는 POMP가 가로채며
엔진으로 직접 넘기지 않는다. 이 가드는 엔진의 **세션 범위 설정 API**에 기대므로 엔진 업그레이드마다 실제 설정 해석과 권한 우회를 재검증한다.

### Skills

- `skills/state`: `{ skills: [{ name, description, source: "project"|"user"|"omp"|"installed"|"pomp", file?, hash?, invocable, note?, unreadable?, enabled?, catalogId?, catalogUpdate?, defaultEnabled?, category? }], diagnostics: string[] }`.
  At most 100 project and 100 user rows (other rows are capped separately, so they never push those out) and 20 bounded
  diagnostics. `invocable` means the live engine advertises `/skill:<name>`. `pomp` rows are the skills built into POMP (see
  *Skill switches, MCP headers and the POMP catalog*): read-only, listed even before the engine reports them, with
  `defaultEnabled` (the state POMP ships them with) and `category` (the catalog group key).
- `skills/command`: `{ requestId, action: "refresh"|"create"|"import"|"delete"|"open"|"read"|"update"|"enable"|"disable"|"reset", scope?: "project"|"user", name?, description?, body?, content?, expectedHash? }`
  → `skills/reply { requestId, ok, error?, document? }`. `read` and `update` are described under *In-page resource editing* below;
  `enable`/`disable`/`reset` under *Skill switches, MCP headers and the POMP catalog*. Scopeless `read` serves a `pomp` row too
  (the page copies a built-in skill with `read` + `create`).
- `import` takes pasted `content` (complete UTF-8 Markdown, ≤32 KiB), validates the existing catalog metadata/body limits, requires its declared name to match the chosen lowercase/digit/hyphen name (2–40 characters) and a nonempty description. It writes a temporary sibling then moves without replacing an existing `SKILL.md`; project/user scope and direct-path checks remain mandatory. It neither executes content nor grants permissions. The editor opens the saved file; restart the engine to refresh command availability. No URL fetch or package execution is involved.
- The generated overlay supplies `skills.customDirectories` in precedence order: `<workspace>/.pomp/skills`, then
  `<home>/.pomp/skills`, then the user's and the workspace's own directories, then POMP's bundled catalog skills last.
  Stock native discovery reads only one-level `*/SKILL.md` with a description. Native described
  one-level POMP skills are omitted from the legacy resource injection/list; deeper or undescribed skills remain in
  `/resources` and can be explicitly loaded through `/skill` (registered as `pomp-resources` and `pomp-skill`). The legacy explicit loader stays available.
- The host scans POMP skills at depth ≤3 with a 2,000-entry budget per scope (one broken entry does not end its folder), files ≤32 KiB and nonempty bodies ≤24,000
  characters. It conservatively accepts single-line name/description scalars (≤500-character descriptions);
  complex YAML metadata is diagnosed and can still appear as engine-only metadata when the engine reports it.
  Project custom-directory entries take precedence over user entries with the same name, and both over a built-in skill
  with the same name (a user's own copy wins; the built-in row stays hidden while the copy is listed).
- Native command metadata (`source: "skill"`, `name: "skill:<name>"`) contains no file path. Engine-only rows therefore
  do not invent a VS-open target. Local POMP rows include a verified file and exact-byte hash. Installed VSIX rows reuse
  cached metadata-only discovery and verified package paths and are read-only/non-invocable (the model reads them with the
  `installed_skills` host tool). No installed skill body is loaded for the catalog. Row `note`s and state `message`s carry state or
  a reason only, never instructions or shortcut text (the page shows no hint text).
- Create requires `^[a-z0-9-]{2,40}$` and a 1–500-character single-line description, uses `CreateNew` for
  `<scope>/.pomp/skills/<name>/SKILL.md`, then opens the template through the existing VS editor adapter. No overwrite.
  Delete resolves one unambiguous POMP-scope row, requires its current hash and removes the containing directory only
  if empty. Open resolves catalog identity rather than accepting a page-supplied path. Linked/reparse paths are refused.
  Refresh/restart determines native invocation after creation/deletion; disconnect clears invocation availability.

### Rule files

- `rules/state`: `{ rules: [{ name, scope: "project"|"user", file, hash, description, alwaysApply, globs: string[], note?, unreadable? }], diagnostics: string[] }` —
  top-level `<workspace>/.pomp/rules/*.mdc` and `~/.pomp/rules/*.mdc` (no file count limit, 32 KiB each; a list too large for one message keeps its front and counts the rest in `diagnostics`). `note` marks frontmatter only the
  engine extension can interpret (it remains authoritative: lib/project-rules.ts parses the YAML and decides what applies). Included in the
  `ui/ready` snapshot and refreshed with the other resources.
- `rules/active` (host → UI): `{ sessionId?, rules: [{ name, file, always, globs: string[] }] }` — the rules that apply to the next prompt for the
  active document, as decided by the engine extension (`lib/project-rules.ts`); posted when the active document, the rules or the conversation
  change, and in the `ui/ready` snapshot. Tools › 규칙 › 지금 적용 중 shows it.
- `rules/command`: `{ requestId, action: "refresh"|"create"|"import"|"open"|"delete"|"read"|"update", scope?, name?, description?, alwaysApply?, globs?, body?, content?, expectedHash? }`
  answered by `rules/reply` `{ requestId, ok, error?, document? }`. Names are 2–40 lowercase letters, digits and hyphens. `create` writes JSON-quoted
  frontmatter (description ≤ 500 single-line characters, ≤ 32 single-line globs of ≤ 200 characters) and `import` writes the pasted strict
  UTF-8 content (≤ 32 KiB, closed frontmatter, non-empty body); both write a sibling temporary file and move it into place without overwriting,
  then open the file in the VS editor. `delete` requires the listed `hash`. Nothing runs a model; a rule never grants tool permissions.

### In-page resource editing

Every item a user writes (skill, rule, saved workflow, project hook, project policy) is created, edited and deleted in the page; the
file stays the source of truth and "open in VS" is a secondary action. Common rules: new names are `^[a-z0-9-]{2,40}$` (an existing file
the list shows keeps its own name and stays editable and deletable); create never overwrites (sibling temporary + move); update and
delete name the hash the page last saw and are refused when the file changed since (refresh, then retry); linked/reparse paths are
refused; nothing here runs a model or grants a permission; these commands come only from the page, never from a model tool.
Every write is refused when the file it would write exceeds the limit its reader applies, counted in UTF-8 bytes (skills, rules and
workflows 32,768 bytes, hooks 65,536; a Korean character is 3 bytes); the page blocks such a save with a short "too large" error
and shows no byte counter.
A row with `unreadable: true` (over the limit or not UTF-8, e.g. written by another program; for skills also a SKILL.md that does not parse
or repeats a taken name, then named by its folder, or `<relative path>/SKILL.md` when that is taken too — pass the name back unchanged)
carries a `note` and a streamed whole-file hash: the page offers only open in VS and delete (`read`/`update` refuse with the note). Above
16 MiB the hash is empty (`""`; `null` for skills) and the row can only be opened. Lists show every file a create can produce; a scope at
its cap (skills 100, rules 100 per scope; hooks 20; workflows have none) refuses create and import with a message, and files beyond a cap are
counted in `diagnostics`.

- Skills and rules — `create` takes an optional `body` (empty: the starter body, as before); `create` and `import` no longer open the VS editor.
  `read { scope?, name }` answers `reply.document = { content, hash, form? }` for one listed row (an installed row: scope omitted;
  engine-only rows have no file). `form` is present only when every frontmatter key is one the form edits — skills
  `{ description, body }`, rules `{ description, alwaysApply, globs, body }`; otherwise the page edits `content` as text.
  `update { scope, name, expectedHash }` plus either the form fields (the host writes the frontmatter as `create` does) or `content`
  (validated as `import`: same limits, a skill's declared name must stay the same). The page copies a built-in skill by `read` and
  `create` with the body.
- `workflows/state` `{ workflows: [{ name, scope, file, hash, description, stepCount, task?, steps?, summary?, note?, unreadable? }], diagnostics }` — every file (only when the summaries alone would pass 1.75 MiB are trailing ones left out and counted in `diagnostics`);
  `task`/`steps` ride along while the message stays under 1 MiB, later entries are `summary: true` and `workflows/command` `get { scope, name }`
  answers `workflows/reply { workflow }` with the full file when one is started or edited;
  `<workspace>/.pomp/workflows/<name>.json` and `~/.pomp/workflows/<name>.json`, each ≤ 32 KiB:
  `{ "version": 1, "description": "...", "task": "...", "steps": ["...", ...] }` (description ≤ 500 single-line characters,
  task 1–16,000 characters, ≤ 32 single-line steps of 1–500 characters; any other key or shape → `note`, open/delete only).
  `workflows/command` `{ requestId, action: "refresh"|"create"|"update"|"delete"|"open"|"get", scope?, name?, workflow?: { description, task, steps }, expectedHash? }`
  → `workflows/reply`. Starting one is `workspace/composer` `start` (below); the page then sends the chat message.
- `hooks/state` `{ hooks: [{ id, name, file, hash, description, event: "onFileSave"|"onBuild"|"", glob, timeoutSeconds, enabled, script, note?, unreadable? }], diagnostics, policy: "none"|"valid"|"invalid" }`
  — the top-level `<workspace>/.pomp/hooks/*.md` files (first 20) as ProjectHooks parses them; `id` is the file name without `.md`.
  A file that does not parse is listed with `note` (open/delete only). `hooks/command`
  `{ requestId, action: "refresh"|"create"|"update"|"setEnabled"|"delete"|"open", id?, name?, hook?: { description, event, glob, timeoutSeconds, enabled, script }, enabled?, expectedHash? }`
  → `hooks/reply`. The host writes `---` scalar frontmatter (`name`, `description`, `event`, `enabled`, `glob`, `timeoutSeconds`) and
  the script in one ```powershell fence, then re-parses the result with ProjectHook.Parse before writing. Running a hook is unchanged:
  the conversation switch, a valid project policy and a one-time approval per run.
- `permission/rules` `savePolicy { policy: { requireReviewedEdits, requireSandbox, rules: [{ tool, effect, paths?, commands? }] }, expectedHash }`
  writes `.pomp/permissions.json` with `version: 1` and `default: "deny"` after the same validation ProjectExecutionPolicy applies
  (`expectedHash: null` = the file must not exist yet); `deletePolicy { expectedHash }` removes it. `projectPolicy.hash` is the hash of
  the file's bytes whenever one exists. The policy is read for every tool check, so a save applies to the next call. The page sends the
  hash of the file its draft (or delete confirmation) was made from, never a newer one: when any reply shows a different file, the
  draft turns stale and can only be reloaded from the current file.
- `permission/rules` `persistent` keys are `<host|omp>:<tool>@<workspace>` (`%`, `;`, `,`, space and controls of the path escaped as
  `%XX`); a key without `@<workspace>` is from an older version and no longer answers anything. `trustedWorkspaces: [{ root, trustedUtc,
  files }]` lists trusted workspaces; `untrust { workspace }` forgets one (omp restarts when it is the current workspace).
- `workspace/composer` `start { root, sessionId, id, revision, task, steps }` begins a new run from a saved workflow when none is active:
  the same guards as a new saved task, but unpaused (discovery when steps are given, otherwise planning). It sends no model request.
- `agents/command` `openRole { scope, name }` opens a project/user role file the form cannot edit in the VS editor.
- Applied changes are listed a page at a time: `workspace/history` `refresh { page, size, query? }` answers `workspace/historyState` `{ root,
  entries, total, page, size, query, message }` (newest first, `query` matches file paths, the last view is kept for later pushes).
  Records are no longer limited to the newest 20; only the 500 MB checkpoint size limit removes the oldest. `delete { ids }` removes
  finished records (a prepared one is refused unless its record was last written over an hour ago: an interrupted apply) and `restore { ids }` stages one combined undo proposal (records that changed the same file,
  or more than 10 files, are refused); the single `id` form still works; `workspace/terminal` `clear` drops finished jobs and `cancelAll` cancels unfinished ones; `workspace/codeReview`
  `deleteRun { runId }` removes one previous run.

### Native MCP files

- `mcp/state`: `{ servers: [{ name, scope: "project"|"user", type: "stdio"|"http"|"sse", command?, args?, env?: { [name]: "••••" }, url?, enabled, error?, hash? }], restartRequired, message? }`.
  Only native writable project/user definitions are listed (no count limit; a list too large for one message keeps its front and says how many it left out in `message`); imported third-party configurations are not edited.
- `mcp/command`: `{ requestId, action: "refresh"|"save"|"delete"|"enable"|"disable"|"test"|"restart", scope?, name?, server?, expectedHash? }`
  → `mcp/reply { requestId, ok, error?, output? }`. There is no separate hashes map in state.
- Paths are `<workspace>/.omp/mcp.json` and `<home>/.omp/agent/mcp.json`; a named active profile uses
  `<home>/.omp/profiles/<profile>/agent/mcp.json`. `--profile`/`--alias` (including `=value` forms) take precedence over
  `OMP_PROFILE`/`PI_PROFILE`; empty or `default` selects the default path. Home is injectable for host self-tests.
- Every environment value in state is masked as `••••`. Saving the mask preserves that variable's existing actual value;
  a new variable needs a real value. Invalid-file/server errors never include raw environment configuration. Commands,
  arguments and URL remain ordinary editable fields, not secret fields.
- `save` without `expectedHash` is **create-only**, including a missing/empty native file. An existing same-scope row or
  relevant user definition/override collision refuses it. Existing save/delete/toggle requires the opaque current `row.hash`;
  stale hashes require refresh. Rename with a previous row hash is unsupported.
- A process-wide lock protects fresh native-file reads before mutation. One server/override is edited from current disk
  data, not a UI snapshot; unknown top-level/server keys and unrelated entries are preserved. Opaque project-row hashes
  cover both native scope and user files because toggles also clean user overrides; hashes are deliberately conservative.
  Enable removes stale user denylist/allowlist entries and sets the writable definition enabled; disable sets it false and
  clears a stale allowlist. Native files are ≤1 MiB, duplicate JSON keys/trailing content are rejected, and linked/reparse
  ancestors are refused. Writes use unique same-directory temporaries plus atomic replace/move and cleanup.
- Writes are atomic **per file**, not a two-file transaction or a cross-process lock. A successful user-override cleanup
  followed by a failed project write is reported as failure but still sets `restartRequired`; refresh/retry is required.
  External editors/other VS processes are not serialized by the in-process lock.
- Any successful/partial edit sets `restartRequired`; stock RPC `/mcp reload` cannot reconnect the live session. Restart
  uses the normal host restart path, must actually reach running, and clears the banner on a successful new engine start.
- Test verifies the stock builtin MCP command exists, then runs local `/mcp test <name>` without inference. Correlated
  current-engine `command_output` is captured, terminal escapes removed and bounded to 8 KiB UTF-8, returned only in
  `mcp/reply.output`, never a chat notice. Stock currently reads only the first whitespace token, so testing server names
  containing spaces is refused instead of testing a different server accidentally. A command-level failure or absent
  output fails the reply; connection success/failure detail remains the stock test's output.

### Skill switches, MCP headers and the POMP catalog

- Skills can be switched off without deleting them. `skills/state` rows carry `enabled` (absent = on). `skills/command`
  adds `enable` and `disable` with `name` (any source, including engine and plugin rows) and `reset` with `name` (clears
  the explicit on/off of that skill, back to its default; on a skill without overrides it is idempotent). POMP keeps its
  own per-skill on/off overrides in its data folder and writes the engine overlay's `disabledExtensions` as the effective
  off-set merged with the `disabledExtensions` of the user's `~/.omp/agent/config.yml` and the workspace's
  `.omp/config.yml` (an overlay array replaces the lower layers, so POMP must merge them; the same merge applies to
  `skills.customDirectories`). The engine watches the overlay and applies the change without a restart.
- The catalog skills are built into POMP: every entry of `omp-extension/catalog/catalog.json` with `kind: "skill"` is
  available out of the box and listed in Tools › 스킬 as a `pomp` row (with `catalogId`, `defaultEnabled` and `category`),
  whether or not the engine has reported it yet. Only the essentials are on by default (`defaultEnabled`, absent = off);
  built-ins and user skills default to on. The engine discovers the catalog skills from the overlay's catalog directory
  (last in `skills.customDirectories`) and lists the enabled ones (name + description) to the model, which loads them
  itself — as slash commands and on plain-language asks alike. Enabling a skill also switches on everything in its
  transitive `requires`; disabling never cascades. `pomp` rows are built into POMP and read-only (`update`/`delete` refuse; the page
  offers only the switch, use and open). `catalogUpdate` is deprecated and always false.
- MCP rows and `save` take `headers` for http/sse servers (e.g. `Authorization`). Header values are masked like `env`
  (`••••` keeps the stored value). `catalogId` is stored in the server entry as `pompCatalog` and returned on the row.
- `catalog/state`: `{ entries: [{ id, kind: "mcp", title, description, category, version, publisher?, license?,
  scopes, installed, requires, needs: ("network"|"token"|"browser"|"node")[], inputs: [{ key, label, secret, required }] }], message? }`.
  The catalog is a file bundled with POMP (`omp-extension/catalog/catalog.json`) and is part of every `ui/ready` snapshot.
  It lists MCP entries only (they pin an exact package version or release and say what they need); catalog skills live on
  the skills list instead.
- The host registers every catalog MCP entry once in the user's native `mcp.json` (switched off, inputs empty, `pompCatalog` kept),
  when resources refresh; the ids it registered are kept in `<data>/seeded-mcp.json`, so a server the user deleted is never registered
  again and a later POMP adds only new entries. A server of the user's own with the same name is left alone. After that it is an
  ordinary server row (edit, switch, test, delete).
- `catalog/command`: `{ requestId, action: "refresh"|"install", id?, scope?: "project"|"user", inputs? }` → `catalog/reply
  { requestId, ok, error? }`. `install` writes one create-only MCP server to that scope's native `mcp.json` with
  `enabled: false`, the pinned command or URL and the entered inputs (an entry with a secret input is user scope only;
  an optional secret left empty omits that variable and keeps the project scope allowed); switching it on and the
  restart stay with the MCP row. Every MCP change re-sends `catalog/state`. Installing a skill id is rejected (skills are built in). No install downloads or runs
  anything; an `npx` server fetches its pinned package when the engine starts it. The host reads only `~/.omp/agent/config.yml`
  (or the active profile's) and `<workspace>/.omp/config.yml` for the merge (a missing, unreadable or richer-than-expected
  file is ignored with a warning, never a failed start); other project settings files (`.omp/settings.json`, `.claude/settings.json`)
  still apply beneath the overlay for keys the overlay does not set.

### Preview

- `preview/open`: `{ path }` opens a `.svg`, `.html` or `.htm` file inside the workspace in a VS document tab. The host
  resolves the path against the workspace root and refuses anything outside it, any linked/reparse path and files over
  2 MiB; a refusal arrives as a chat notice. The tab's WebView2 has scripts, web messages, host objects, dev tools,
  context menus, downloads and new windows switched off, serves the file from a virtual host with a CSP that allows no
  script and no network, and cancels every navigation away from that one file. Light/dark sets `data-theme` on the
  file's root element (the theme contract of the bundled `diagram` and `page-design` skills); width (full or 390 px),
  zoom and redraw are host controls drawn with the VS theme styles. A tab starts in the light or dark theme of VS and
  follows a VS theme change (background and file theme). The tab redraws when the file changes on disk; a file that is
  removed, grows over the limit or leaves the workspace later shows the refusal inside the tab.
## Slash commands

`commands/state` (host → UI) `{ commands: [{ name, source?, description?, hint?, subcommands?: [{ name, description? }] }], hidden? }` is
the list omp reports (`get_available_commands` once the engine runs, then every `available_commands_update`), with no
count limit: past the UI hub's message size the descriptions go first, then the last commands, counted in `hidden`; names without the leading `/`; `{ commands: [] }` while the engine is not running. It is posted after
`ui/ready` too. `source` is where the engine got the command from (`builtin`, `extension`, `skill`, `file`, `custom`,
`mcp_prompt`; absent from an older host). The page merges the list with its own commands into the palette (the `/` button
and typed `/…` completion) and sends any other `/name …` text as a normal `chat/send` (omp runs extension, skill and prompt
commands itself).

**One entry per function, under one plain name.** The palette shows, in this order:

1. the page's own commands, which it handles itself and never sends on: `/new /sessions /rename /model /thinking /effort /permissions /compact
   /accounts /login /usage /work /agents /computer /agent /team /goal /help` (`/goal` opens the goal card, see below);
2. POMP's engine commands under plain names, each with a short label and only when the engine lists the registered name;
3. the engine's other commands by name only (no description, no argument syntax);
4. the enabled skills, plain names, after a "스킬" / "Skills" heading (a row of the list, not an option).

`/effort` opens the thinking-level picker like `/thinking`; `/permissions` opens the permission-mode picker of the composer's chip
(`pomp-permissions` stays registered for the host's guard check and keeps its own name).

A command that hands its text to the model takes the context basket's checked files: POMP's `workflow`, `plan`, `map`, `review`,
`skill`, `command` and `recipe`, a skill, or a prompt file (source `file`). The page sends the checked paths as `attachments` with such a
command only (then empties the basket, as for a message); the host puts them after the command's own text under "Work only on these
workspace references…" and keeps a space after the name. Any other command goes without attachments and the basket stays.

POMP's engine commands are registered in the engine with a `pomp-` prefix (it keeps them clear of the engine's own commands);
the palette lists the plain name and the host sends the registered one:

| plain name | registered as | does |
|---|---|---|
| `/workflow <task>` | `pomp-workflow` | starts a staged POMP Workflow (plan, discover, edit through change review, verify) |
| `/plan <task>` | `pomp-plan` | a plan-only turn, see "Plan-only turns" below |
| `/map [focus]` | `pomp-map` | explains a bounded structural map of the codebase |
| `/review [focus]` | `pomp-review` | reviews the source with the host's diagnostics and evidence-backed findings |
| `/max [model]` | `pomp-max` | chooses an authenticated model with at least a one-million-token context |
| `/rules` | `pomp-rules` | shows the scoped rules that apply and their diagnostics |
| `/resources` | `pomp-resources` | lists the `.pomp` skills, commands and agent recipes |
| `/guard` | `pomp-guard` | shows the pre-edit guard mode and the files with unsaved Visual Studio changes |
| `/vs-status` | `pomp-context` | shows the Visual Studio connection (host, solution, unsaved files, offered tools) |
| `/skill NAME`, `/command NAME`, `/recipe NAME` | `pomp-skill`, `pomp-command`, `pomp-recipe` | load a `.pomp/skills`, `.pomp/commands` or `.pomp/agents` Markdown resource into the conversation |

(`/goal` is the page's; the host never receives it as text.) `/review`는 터미널 UI 전용이 아니다. 번들 엔진의 custom `/review`는
headless 텍스트 모드에서 로컬 리뷰를 실행하고 GitHub PR URL 또는 `pr://owner/repo/N`과 선택적인 검토 초점도 받는다. 엔진이
같은 이름의 non-builtin 명령(`file`·`custom`·`mcp_prompt`·다른 확장 등)을 광고하면 그 명령이 호스트의 `/review` 매핑보다 우선한다.
`builtin` 항목은 POMP 대응 명령이 우선하며, 직접 정의한 짧은 이름은 유지하고 POMP 대응 명령은 그 이름으로 나열하지 않는다.
The registered names (`/pomp-workflow …`) keep working when typed, and are not listed. POMP's plumbing commands (`pomp-tools`,
`pomp-inline`, `pomp-stop`, `pomp-feedback-guard`, `pomp-agent`, `pomp-dm`, `pomp-team`) are never listed either; typing them
still sends them. `/pomp-compact` no longer exists: the engine's `compact` is dispatchable over RPC and POMP's compaction hook
(`session.compacting`) already adds the Visual Studio facts to every compaction, so the page's `/compact [instructions]`
(`session/history` `compact`) is the one command; a typed `/pomp-compact …` runs it.

**Mapping at send.** `chat/send` carries the text exactly as typed (the transcript and its replay show `/workflow ship it`); only
the prompt that goes to the engine is rewritten, by the host (`PompSession.ResolveSlashCommand`), when the text starts with
`/name`: a plain name of the table becomes its registered command; a plain skill name becomes `/skill:name`, when the engine
lists `skill:name` and no real command has that plain name (a command always wins); anything else, including `/skill:name`,
a registered name and unknown names, is sent untouched.

**Skills.** Every `skill:<name>` row of the engine's list (built-in, user and project skills; the engine omits the ones switched
off in Tools › 스킬) is listed as `/<name>` in the skills group, sorted by name. A skill the page knows to be switched off
(`skills/state` rows with `enabled: false`) is not listed even before the engine's list catches up. A skill whose plain name a
real command already has stays reachable as `/skill:<name>` (listed under that name); names that are not safe to type
(containing `/`) are listed as `skill:…` among the engine's commands.

## Plan-only turns

`/plan <task>` (typed; the palette lists it when the engine lists `pomp-plan`) asks for a plan and changes nothing. The host
sends `/pomp-plan <task>`; the extension module `plan-guard.ts` arms its guard and sends the plan prompt. During that one run
the guard allows only reading and searching (`read`, `glob`, `grep`, `find`, `ast_grep`, read-only `lsp` actions), the web
(`web_search`, `fetch`), `todo` (the plan, which shows in Work › 진행 where the user edits and runs it), `ask`, and the POMP
host tools that only read (`read_buffer`, `search_codebase`, `build_codebase_context`, `review_source`, …); every other
`tool_call` — edits, `bash`, `eval`, `task` (no subagent starts), MCP and extension tools, the host's edit, build, test, Git,
debugger, terminal and workflow tools — is refused with a short reason. The guard is lifted only by the end of the run (an
`agent_end` that is not followed by a continuation), a new or switched session, or its two-hour age ceiling; nothing the model
can call or run lifts it. The model can put a turn it is already in under the same guard with the extension tool
`enter_plan_only` (no arguments; "just plan it"). `/plan` without a task shows an inline error in the composer and sends
nothing. It is an engine-side filter, not an OS sandbox; the host's approvals and the project policy apply on top.

## Automatic continuation and goal runs

Automatic continuation ("자동 계속", Work › 진행) sends the follow-up prompt of an active POMP Workflow by itself, up to six
times, and only after the user turned it on for that workflow. Two limits end it, whichever is hit first: the six runs, and an
optional **token budget**.

- `workspace/composerAuto` (UI → host) `{ action: "status"|"enable"|"disable", requestId, root, sessionId, id, revision,
  tokenBudget? }`; `enable` takes `tokenBudget`, a whole number from 10,000 to 50,000,000 (absent or empty = none; a string of
  digits with thousands commas is accepted; anything else is refused with the reason in `message`, nothing is armed). The
  answer is `workspace/composerAutoState` (host → UI) `{ root, sessionId, id, enabled, remaining, dispatching, message,
  requestId?, tokenBudget?, tokensUsed? }`; `tokenBudget` and `tokensUsed` are present while armed with a budget.
- **What is counted:** the model calls of the conversation since the moment of enabling, per finished assistant message
  (`message_end`, `message.usage`): `input + output + cacheWrite`, cache reads left out; when a provider reports only
  `totalTokens`, that. It is the engine's own definition of the "tokens" of an agent run, because with prompt caching every
  call reads the whole cached context again, so summing cache reads says nothing about work done. Subagent (member) runs are
  not counted; their progress metrics remain in `workspace/backgroundState`, linked to the chat task card.
- **When it stops:** the host looks at the budget on every tick (once a second) while armed, so a run that finished — or is
  still running — past the budget ends the continuation there, with the message "Token budget (N) used up." (Korean
  "토큰 예산(N)을 모두 썼어요."); a run is never started over budget (a run that was planned just before the budget ran out is
  not current anymore and is dropped). A running turn is not aborted. The six-run cap still applies independently
  ("Six follow-up agent runs were used.").
- The panel shows a labelled "토큰 예산" field while the continuation is off (validated on the page too: an inline error and
  nothing sent), and the status "남은 실행 N회 · 토큰 x / y" while armed with a budget.

A **goal run** (`/goal <text>`) starts a POMP Workflow on the text and arms automatic continuation for the workflow that start
creates, optionally within a token budget; the count starts at the start of the goal (its first run belongs to the goal).

- Typing `/goal <text>` (the page's command; `/goal` without text is an inline composer error, `/goal` in an agent or team tab
  is refused) opens the **goal card**: a request card at the end of the conversation with the goal text (editable), a
  "토큰 예산" field, the question "이 목표를 자동으로 이어서 실행할까요?" and 시작 / 취소 (Enter starts, Esc cancels; Shift+Enter
  is a line break in the text; the keys act 0.5 s after the card appears; 시작 waits while a reply runs). Nothing is sent before
  the answer.
- `workspace/goal` (UI → host) `{ requestId, action: "start"|"cancel", root?, sessionId?, text?, tokenBudget?, fromRequest? }`
  answers the card. `start` runs on the chat command queue and is refused with a reason (`workspace/goalState`) when the
  text is empty or over 16,000 characters, the budget is wrong, `root`/`sessionId` are not the current ones, there is no
  workspace or conversation, the conversation is busy (a reply, a pending question, unfinished terminal or agent work), a
  workflow is already active or a goal is starting, or the engine does not list `pomp-workflow`. Otherwise the host registers
  the goal (token baseline = now), sends the prompt with the user's bubble `/goal <text>` and `/pomp-workflow <text>` to the
  engine (exactly what `/workflow` sends), and answers `{ requestId, ok: true, stage: "started" }`. `cancel` only clears the
  card the model asked for (`fromRequest`).
- Binding: the first active workflow of this conversation that appears after the start (a new id) is the one the goal armed
  automatic continuation for (`tokenBudget`, six runs, the first follow-up as soon as the first run is over). No workflow within
  ten minutes, or the goal's turn ended without one, or the workflow is paused or belongs to another conversation: nothing is
  armed, and the reason is the state message and a chat notice. A manual chat message, Stop, a conversation switch, a new
  conversation or an engine restart cancels a goal that is still waiting, like it ends an armed continuation.
- `workspace/goalState` (host → UI) `{ requestId, ok, stage: "started"|"cancelled"|"refused", error? }`.
- The model can ask for a goal run with the host tool `request_goal_run` `{ goal, tokenBudget? }`. It posts
  `workspace/goalRequest` (host → UI) `{ requestId, root, sessionId, text, tokenBudget? }`, which opens the very same card
  (the host keeps it for a page that reloads, until it is answered; a request for another conversation is ignored), and
  returns "nothing has started" to the model. Only the user's `workspace/goal` `start` starts anything. Gates, like
  `present_case_feedback`: exactly one running turn, the epoch connection, a ready conversation, the engine's
  `pomp-feedback-guard` (which keeps subagents from calling it: `ctx.agent.kind` must be `main`) and `pomp-workflow`, an open
  chat window, no active workflow, no goal starting.

## Composer predictions

- `composer/predict` (UI → host): `{ requestId, sessionId, text }` — the whole draft (≤ 4,000 characters), sent after typing pauses for 500 ms
  while `host/hello.composerPredictions` is true. Answered by `composer/prediction` `{ requestId, text?, error? }` (a one-line continuation,
  ≤ 200 characters; `error` is a code the page ignores). The host runs at most one at a time for the current conversation (a newer
  request cancels the older) through the editor-model mode `prompt` (6 s, 48 tokens, reasoning off) and never logs the draft.
- `permission/request.tasks` lists every assignment of a `task` tool approval (`agent`, full `task` text) when the host can match the
  approval to exactly one pending task call of that conversation (display only; the approve/deny path is unchanged). Without it the page
  shows `message` as received.

## Sessions

`session/history` (UI → host) is answered by exactly one `session/historyState` with the same `requestId`:

| action | fields | effect |
|---|---|---|
| `refresh` | | list only |
| `switch` | `root`, `id` | `switch_session` to that entry (refused while streaming, with an open approval/question or unfinished terminal jobs); when omp cannot restore the saved model (omp 18.6.3 and later: `Could not restore model …`) the host sends the switch again with `provider`/`modelId` of the current model and posts an info notice; then the transcript is replayed |
| `messages` | `sessionId`, `cursor?` | a page (40) of the current session's user/assistant texts |
| `preview` | `id` | read-only preview of any listed entry without switching: the host reads that session file (the same opaque `id` resolution as `switch`; at most 1 MiB read) and answers `preview: { id, messages: [{ role, text, truncated? }], truncated }` with the first 20 user/assistant text messages (≤ 2000 characters each) |
| `rename` | `name` | `set_session_name` on the current session; 1..120 characters, no control characters |
| `delete` | `id` | moves that listed session file (and its artifacts folder) of this workspace to the Recycle Bin, never a permanent delete; the current conversation is replaced by a new one first. POMP's own per-session data (recent entry, knowledge selection, partner preference, queued items, run history) is dropped. Anything not in the listing, outside the engine sessions folder or containing links is refused |
| `compact` | `instructions?` | omp `compact` of the current session (≤ 2000 characters of custom instructions); `session/info` shows the new context usage |

```
session/historyState: { requestId?, error?, root,
  current?: { sessionId, name? },
  entries: [{ id, sessionId?, title, name?, modifiedUtc?, visitedUtc?, messageCount?, current?, partner? }],
  messages?, nextCursor?, totalMessages?, sessionId?,
  preview?: { id, messages: [{ role, text, truncated? }], truncated } }
```

`entries` are this workspace's omp sessions, newest first (no count limit; rows that would take the message past the UI hub's size are left out and counted in `hidden`): the sessions POMP has seen, plus the other
`.jsonl` files in the folder of the current session file (omp keeps one folder per working directory). `title` is the
session name, else the first user message (≤ 120 characters), else the date. `id` is an opaque handle that only
`switch` understands (the host resolves it to a file inside that folder or to a remembered session; never a path from
the page).

## Workspace trust

omp (18.4.3 through 18.12.1) trusts every project: from the workspace it loads settings that can auto-approve tools, MCP servers (started with the
engine's environment), tool code, and files that context files include with `@path`, even outside the workspace (the list:
docs/architecture.md "작업 영역 신뢰"). Before starting omp the host scans for these; when some exist and the user has not trusted
exactly that set (`fingerprint` over paths, contents and include targets) for this folder, omp is not started: `workspace/trustState`
lists the files and `bridge/state` becomes `failed` with `reason: "workspaceTrustRequired"` (not retried). The chat shows a card in
place of the connection banner: the files (each with *파일 열기*), *신뢰하고 시작* (`workspace/trust` `trust` with the fingerprint shown) and
*시작 안 함* (keeps the card; nothing is sent). A changed set (git pull, an agent's edit) asks again on the next start; Tools › 권한 ›
신뢰한 저장소 revokes. The trust list lives in POMP's data folder, never in the workspace.

## Engine setup (accounts / login / model / usage)

A fresh install has no model provider: omp exits at start-up with "No models available". The host does not retry that
(`bridge/state` goes straight to `failed` with `reason: "setupRequired"`) and the page shows the setup panel in place of
the failure banner. The same panel ("계정 · 모델") opens from the status bar at any time; the model and the thinking
level are also switched from the composer.

### Sign-in without a terminal: the auth helper

omp can only start its RPC mode once a model is usable, so sign-in (first run included) runs in a separate, short-lived
**auth helper** omp process that the host owns: `omp --mode rpc --no-session --no-extensions --no-skills --no-rules
--model openai/gpt --api-key pomp-setup-placeholder` (placeholder key passed on the command line only; `OPENAI_BASE_URL`
points at a closed loopback port, so nothing can reach OpenAI with it; the helper never prompts a model). It shares the
user's omp profile, so a credential it stores is the one the chat engine uses; it writes no session and no settings.
The helper starts on demand (provider list, login) and exits after 2 idle minutes, on dispose, and before the engine
is restarted after a first sign-in. Its RPC is used for:

- `get_login_providers` → `{ providers: [{ id, name, available, authenticated }] }` (about 80 providers). A sign-in method that
  stores its credential under another provider (omp 18.8 `storeCredentialsAs`: `openai-codex-device` → `openai-codex`,
  `zai-coding-plan` → `zai`) stays in `providers`, but the host always sends it as `authenticated: false`: omp answers it from
  the other provider's stored credential and names no target, so it would read as a second account of the same sign-in;
- `login { providerId }` → omp sends `extension_ui_request`s while it runs: `open_url { url, launchUrl?, instructions? }`
  (the host opens `launchUrl ?? url` in the default browser — http/https only — and shows it in `login`), `notify`
  (progress text) and `input { title, placeholder }` (e.g. "paste the authorization code"; shown as `login.prompt`,
  answered with `answerPrompt`). The response comes when the sign-in finished (or failed). A provider that needs a
  secret typed in a terminal answers with an error; the page then offers `loginConsole` (the old console sign-in).

### `setup/state` (host → UI)

Posted after `ui/ready`, after every change, and once per `setup/command` (with its `requestId`; `error` says why that
command failed). All fields except `requestId`/`error` are the full current state.

```
{ requestId?, error?,
  providers?: [{ id, name, authenticated, available }], providersError?,   // from the auth helper (listProviders)
  login: { state: "idle"|"running"|"succeeded"|"failed", provider?, message?,
           url?, instructions?,                       // open_url of the running sign-in (already opened in the browser)
           progress?,                                 // latest notify text
           prompt?: { id, message, placeholder? } },  // the sign-in waits for this input
  apiKeyProviders: [{ id, label, envVar }],  // the only providers a key can be saved for (host allowlist)
  keys: [{ provider, envVar, savedUtc }],    // saved keys; the key itself never leaves the host
  models?: [{ provider, id, name? }], modelsError?, currentModel?: { provider, id },
  thinkingLevels?: string[], currentThinking?, // get_available_thinking_levels for the current model; session level
  usage?: { fetchedAt, reports: [UsageReport], error? },
  usageLoading?: boolean,
  editorModel: { choice: "auto"|"chat"|"model",       // 보조 모델: completions, ask edits, captions, knowledge conversion, composer predictions; apart from chat
                 model?: { provider, id },             // choice "model" (provider is "" for a value typed in Tools > Options without one)
                 effective?: { provider, id, name? },  // what the engine resolved it to (resolveEditorModel); dropped when the engine
                                                       // restarts or the chat model changes (re-resolved by the host if it was known)
                 resolving?: true, error? },
  restartRecommended? }                      // credentials changed and the running engine does not offer them yet
                                             // localServers: [{ id, name, address, hasKey, savedUtc }] — saved model servers (never the key);
                                             // model lists show provider `id` by `name`

UsageReport = { provider, account?, plan?,   // account: email / account id label; plan: the subscription plan when omp names it (Codex metadata.planType, e.g. "prolite")
  limits: [{ label, window?, resetsAt?,      // window: e.g. "5 Hour", "7 Day"; resetsAt: ISO 8601 (UTC)
             usedFraction?, used?, limit?, unit?,   // usedFraction 0..1 when known
             status: "ok"|"warning"|"exhausted"|"unknown",
             shared?, tier?, modelId? }] }          // omp scope: shared by every model of the account, or one model family
                                                    // (tier: opus, sonnet, fable, spark…) / one model id. The composer shows the
                                                    // shared limits plus the current model's own (as omp does); all in its tooltip
```

### `setup/command` actions (UI → host)

| action | fields | effect |
|---|---|---|
| `refresh` | | re-post `setup/state` |
| `listProviders` | | start the auth helper if needed and fill `providers` (or `providersError`) |
| `login` | `provider` | RPC sign-in through the auth helper. `provider` must match `[a-z0-9][a-z0-9.-]{0,63}`. One at a time. Success → `login.state: "succeeded"`, `providers` refreshed |
| `answerPrompt` | `promptId`, `value?` | answers `login.prompt` (absent `value` = cancel the prompt); ≤ 4096 characters |
| `cancelLogin` | | cancels the running sign-in (the helper process is ended) |
| `loginConsole` | `provider?` | fallback: `omp login [provider]` in its own console window; exit code 0 → `succeeded` |
| `saveKey` | `provider`, `key` | `provider` from `apiKeyProviders`; `key` 8..1024 printable characters without spaces. Encrypted for the current Windows user (DPAPI) under the POMP data folder and passed to every later omp start as `envVar` |
| `removeKey` | `provider` | deletes the saved key |
| `saveLocalServer` | `id?`, `name`, `address`, `key?` | saves a model server the user runs (any OpenAI-compatible server), or replaces the one with `id`. `name` 1..40 characters, unique (case-insensitive); `address` an absolute http(s) URL without user information or spaces (≤ 2,048); `key` optional, 8..1024 printable characters, encrypted like the API keys (an empty `key` keeps the saved one). Stored in `credentials\local-models.json` under the POMP data folder with an id `local-<slug>` that never changes. Every later omp start gets the servers as `POMP_LOCAL_MODELS` (`[{ id, name, address, key? }]`, ≤ 30,000 characters: saving more is refused); the POMP extension `local-models.ts` registers each as provider `id` with the models of `<address>/models` (or `<address>/v1/models`), `api: openai-completions`. `restartRecommended` as for key changes |
| `removeLocalServer` | `id` | deletes the saved server |
| `logout` | `provider` | signs out of every sign-in omp stored for the provider (omp has no per-account logout over RPC: the host ends the auth helper and runs `omp auth-broker logout <provider>` hidden in POMP's own folder with no saved keys in its environment, ≤ 20 s, only exit code 0 counts). Refused while a sign-in runs. The answer carries the re-read `providers` (a provider still in it is authenticated another way: POMP-saved API keys and environment variables stay) and drops that provider's usage; `restartRecommended` as for key changes |
| `listModels` | | `get_available_models` (engine running) → `models` / `modelsError` |
| `setModel` | `provider`, `modelId` | `set_model` for the current session; `session/info` reports the new model; omp remembers it as its default |
| `listThinking` | | `get_available_thinking_levels` (engine running) → `thinkingLevels` |
| `setThinking` | `level` | `set_thinking_level`; one of `thinkingLevels` |
| `setEditorModel` | `choice`: `auto` \| `chat` \| `model`, `provider?`, `modelId?` | saves the helper model (the options value "보조 모델" / "Helper model": empty, `@chat` or `provider/id`), then resolves it. `auto` = omp's fast model `@smol` (the configured fast role, else omp's built-in priority list) |
| `resolveEditorModel` | | asks the engine which model the current choice means (the POMP extension's `/pomp-inline` in mode `resolve`; no model is called) → `editorModel.effective` or `editorModel.error` |
| `listUsage` | | runs `omp usage --json` (hidden, ≤ 45 s; asks each signed-in provider for its limits) → `usage`; `usageLoading` while it runs. The page asks for it when the account panel opens (at most once a minute) and on its refresh button |

After a successful sign-in or `saveKey` while the engine is not running, the host restarts it by itself. While it runs,
the host re-reads `get_available_models`: when the new provider's models are listed the change is live (no restart);
otherwise the state carries `restartRecommended: true` and the page offers *Restart* (`bridge/restart`).

계정 화면의 **보조 모델** 선택은 **빠른 모델 / 채팅 모델과 같게 / 모델 직접 선택**. 직접 고른 모델은 모델 이름과 그 아래 공급자
표시 이름으로 보인다. `provider`·`modelId`는 프로토콜 식별자이며, 선택기의 마우스 오버 제목은 채팅 모델 선택기와 같은 식별자를
유지한다. 사용량 구간은 **5시간 / 주간**이고 보고되지 않은 구간은 숨긴다. 옵션 페이지는
**일반 / General**이며 분류·설정 이름은 POMP 언어를 따른다.

## Request cards (design: 권한 카드)

- A pending `permission/request` or `dialog/request` is a card at the end of the transcript, where the conversation is — never a
  modal overlay. Only the oldest pending request is active (the counter "2 / 3" shows the rest); the transcript, the composer and the
  other tabs stay usable, and the chat tab shows a dot while a request waits and another tab is active. After the answer (or a
  dismiss / timeout) the card stays as a one-line record: title and outcome (허용함 / 거부함 / 항상 허용함 / 이 대화에서 허용함 / 만료됨 /
  취소함 / 답변함; a host dismiss without an answer: 처리됨).
- Keyboard: with the focus inside the card, `Enter` = the primary answer (allow once / confirm / the highlighted option), `Esc` =
  deny / cancel; in the deny-note field `Enter` denies with the note. `Enter` counts only while the card is on screen (a card below
  the fold, after the user scrolled up, takes no focus and ignores Enter). The chords `shortcuts.allow` and `shortcuts.deny` of
  `host/hello` (defaults `Alt+A` / `Alt+D`, Tools > Options > Pomp > General > Shortcuts) answer the active card from anywhere on the page. A page chord other than `send` holds Ctrl or Alt, or is an F key; the host and
  the page replace anything else (`Enter`, `A`, `Shift+D`) with the default, so typing never answers a card.
  Nothing answers during IME composition, on key repeat, or within 500 ms of the card appearing (arming). The card takes the focus on
  arrival unless the user is typing an unsent message; the focus returns afterwards.
- Buttons: **허용** (`allow`, this call only), **거부** (`deny`), **항상 허용** (`alwaysAllow`, persisted; the options
  page can revoke it). A secondary action offers `allowForSession`. An optional note is sent with `deny` for host tools;
  for `source: "ompApproval"` the note field is hidden (omp has no channel for it).
- `canRemember: false` (omp prompts it shows regardless of rules: a `Reason:` line from the tool's own policy, or a
  `Provider safety checks:` section from the model provider): offer only *allow once* and *deny*; the page shows no note
  about it.
- Session grants last for one omp process: they are cleared on reconnect/restart and on `session/new`.
- Show `title`, `toolName`, `target`, and `message` (monospace, preserve line breaks — for omp approvals it is
  omp's own prompt text such as `Allow tool: bash` followed by the command). A host tool's `message` holds the operation
  details (command, timeout, files) and at most one consequence sentence.
- When `diff` is present render it with the Diff view (unified diff; inline mode first, side-by-side later). For
  `edit_buffer_hashline` it is the complete, exact change that will be applied (never truncated; the tool refuses a patch whose
  diff exceeds 180,000 characters).
- A reply is removed from the page only after it was handed to the host. If the host is unavailable, posting throws, or the
  reply is too large (the page checks the UTF-8 envelope and the encoded omp reply), the card stays with an inline error,
  keeps the user's input and can be retried or cancelled; nothing is truncated silently. The same applies to `dialog/respond`.
- `timeoutMs` → visible countdown; the host resolves the request itself when it expires and sends `permission/dismiss`.
- While a request is pending the composer stays usable (so the user can type a follow-up), but *Send* is disabled and a new
  conversation, a session switch and a retry wait for the answer.

## Theme variables (`theme/changed.colors`)

`--pomp-bg`, `--pomp-fg`, `--pomp-muted-fg`, `--pomp-border`, `--pomp-panel-bg`, `--pomp-input-bg`,
`--pomp-input-fg`, `--pomp-input-border`, `--pomp-focus-border`, `--pomp-button-bg`, `--pomp-button-fg`,
`--pomp-button-hover-bg`, `--pomp-button-secondary-bg`, `--pomp-button-secondary-fg`, `--pomp-link`,
`--pomp-accent`, `--pomp-error`, `--pomp-warning`, `--pomp-success`, `--pomp-code-bg`, `--pomp-selection-bg`,
`--pomp-scrollbar`, `--pomp-diff-add-bg`, `--pomp-diff-del-bg`.

The page must define sensible dark defaults for all of them in CSS so it renders before the first `theme/changed`.

## Keyboard (design: 포커스 정책)

- `Esc` inside the tool window returns focus to the composer.
- `Enter` sends, `Shift+Enter` inserts a newline (`host/hello.shortcuts.send` = `Enter`); with `Ctrl+Enter` only Ctrl+Enter sends and
  Enter inserts a newline. While busy (a turn of this page is open or `session/info.isStreaming`), the Send button becomes Stop and
  *New chat* is disabled.
- The page's own chords come from `host/hello.shortcuts` (Tools > Options > Pomp > General > Shortcuts): the one-message send-target picker
  (default `Ctrl+Alt+T`), the permission-mode picker (none), allow / deny of the active request card (`Alt+A` / `Alt+D`) and Tools ›
  Permissions (`Ctrl+Alt+P`). A chord is Ctrl/Alt/Shift plus one key; none of them acts during IME composition, on repeat, or with
  AltGraph. The Live/bookmark icons' tooltips and accessible names are just their names; the current chord is in `aria-keyshortcuts`.
  `liveFollow` (`Alt+L`) and `liveBookmarks` (`Alt+K`) send only `ui/editorFollow`, never a model prompt.
  Clicking either icon or invoking either chord from chat focuses the composer; other editable fields keep their keyboard.
  Matching global VS commands `Pomp.ToggleLiveFollow` and `Pomp.ToggleLiveBookmarks` apply the same persisted settings without opening chat or moving editor focus.
- Focus toggling between the editor and the tool window is the VS command `Pomp.ToggleFocus` (default `Alt+J`, global). Its key
  and the keys of the other POMP commands are set in Tools > Options > Pomp > General > Shortcuts and applied to the IDE key bindings; inside
  the page the same chord arrives as `host/hello.shortcuts.toggleFocus` and the page sends `ui/toggleFocus` (WebView2 keeps
  keystrokes from reaching VS).
- The editor prompt uses `Alt+M` (`Pomp.InlineEdit`) in the Text Editor scope; the code actions are
  `Shift+Alt+E` explain, `R` review, `F` fix, `U` tests, `I` add file, `Alt+/` add to knowledge (Text Editor); `Shift+Alt+Q` opens the chat and `Shift+Alt+N`
  starts a new conversation (global). All single-stroke chords, every one changeable in the same options page.

## Workspace review messages

작업 탭은 **진행 · 변경 · 코드 점검**. 변경은 **대기 중**(제안·격리 결과)과 **적용됨**(되돌리기 검토 가능한 적용 기록)을 전환으로 보여 주며
칩은 대기 중 항목만 센다. 진행에는 계획·워크플로·자동 계속을 두고, 맨 아래 대화형 터미널 카드는 옵션과 엔진 지원이 모두 있을 때만
보인다(번들 엔진은 미지원). 승인된 명령 작업은 `origin.sessionId`·`origin.toolCallId`로 채팅의 해당 도구 카드에 붙여 상태·출력·취소를 보여 준다.

`workspace/problemsState`는 `{ root, items: [{ id, source: "agent"|"compiler", severity: "error"|"warning", code, message, path, line, column?, project?, evidence?, reporter? }],
truncated, updatedUtc }`. 에이전트 지적(개수 제한 없음, `evidence`는 지적이 인용한 코드 4,000자까지, `reporter`는 보고한 에이전트: `main`(대화의 POMP) 또는 역할 이름 — POMP 확장이 `report_review_findings` 호출마다 숨은 알림 `pomp-review-reporter:`로 호스트에 알린다)과 VS 컴파일러 오류·경고(개수 제한 없음)만 합친다. 둘을 합친 목록이 화면 메시지 한 개(1.75 MiB)를 넘으면 앞부분만 보내고 `truncated`가 참이다. `workspace/problems`는
`{ requestId, action: "refresh"|"open"|"dismiss", id? }`, 답은 `workspace/problemsReply { requestId, ok, error? }`. `dismiss`는 에이전트 항목만 지운다.
코드 점검 화면은 **전체 / 오류 / 경고 / 에이전트** 필터·선택·**코드 열기**·에이전트 항목 **삭제**·새로 고침을 제공한다. 선택한 최대 50개·요청 전체
12,000자를 **에이전트에게 해결 요청**으로 한 `chat/send`에 넣는다. 현재 보낼 대상에 보내고 편집기 컨텍스트·첨부·바구니는 붙이지 않는다.

`workspace/reviewState` carries the proposal id, revision, status, complete bounded file/hunk previews, selected hunk IDs, diagnostics and per-file outcomes.
The UI sends `workspace/review` with `action` (`refresh`, `select`, `validate`, `apply`, `discard`), proposal `id` and exact `revision`; selection includes `path` and integer `selected` IDs.
With no open proposal the user can make one from their own edits: `editorChanges { requestId }` answers `workspace/editorChanges { requestId, action, ok, error?, files: [{ path, unsaved }] }` (workspace files whose editor or disk text differs from git HEAD, or from the saved file when untracked), and `fromEditor { requestId, paths }` (1–10 of them) stages a proposal with `origin: "editor"` from the original to the current text. Applying it leaves the original plus the selected changes in the editor (unselected changes are taken out) and records a checkpoint like any apply; discarding changes nothing.
The UI never supplies replacement source during acceptance. Stale revisions are ignored and refreshed. Validation does not grant write permission; apply requests a separate one-time host approval.
A completed proposal cannot be applied twice. A new page requests `refresh`; the host also republishes the current proposal on page-ready.

## Agents and teams

`agents/state` (host → UI) carries discoverable agent roles and teams across project, user, and bundled scopes:

```json
{
  "root": "C:/Project/repo",
  "roles": [
    {
      "name": "reviewer-strict",
      "title": "Strict Reviewer",
      "job": "Code review specialist",
      "description": "Reviews code changes for security, logic errors and edge cases.",
      "instructions": "...",
      "model": "openai-codex/gpt-6-luna",
      "thinkingLevel": "high",
      "tools": "readOnly",
      "scope": "project",
      "file": ".pomp/agents/reviewer-strict.md",
      "hash": "0123456789abcdef...",
      "editable": true,
      "error": null,
      "note": null
    }
  ],
  "teams": [
    {
      "id": "feature-dev",
      "title": "Feature Team",
      "goal": "Design, implement and test requested features",
      "members": ["architect", "implementer", "reviewer-strict"],
      "mode": "lead",
      "instructions": "Coordinate using agent://<id>.",
      "membersOnly": true,
      "scope": "project",
      "file": ".pomp/teams/feature-dev.json",
      "hash": "0123456789abcdef...",
      "missing": [],
      "error": null
    }
  ],
  "diagnostics": []
}
```

`error`: the file cannot be used (no frontmatter, no description, too large); such a role is not a team member. `note`: the role works in
omp but POMP lists it read-only (multi-line YAML values, block lists, model lists that a rewrite would corrupt). `tools`: `all` (no
`tools` key), `readOnly` (exactly read, find, grep, glob, lsp, yield), `custom` (anything else, including `tools: []`). There is no
rename: a role's name is its file name, a team's id its file name.

The UI sends `agents/command` to manage roles and teams, answered by `agents/reply`:

| action | parameters | effect |
|---|---|---|
| `refresh` | | republishes `agents/state` |
| `builtinRole` | `name` (a bundled role) | answers `role: { description, instructions, model?, thinkingLevel?, tools }`: the bundled role as the engine defines it, read once per engine with `omp agents unpack --dir <temp>` (the form's "edit" of a bundled role) |
| `saveRole` | `scope` ("project" \| "user"), `role` (`role.builtin: true` for an edited bundled role), `expectedHash?` | creates or atomically updates a role; `expectedHash` required when modifying an existing file. A new file for `builtin` starts from the engine's definition of that role: the frontmatter the form does not write (`output`, `spawns`, ...) is kept, and `tools: "custom"` keeps the role's tool list (also when editing any existing file). The file replaces the bundled role for the engine; deleting it brings the bundled role back. Bundled names are allowed as role names; only `main` and `sub` are reserved |
| `deleteRole` | `scope`, `name`, `expectedHash` | deletes a role file; `expectedHash` required |
| `saveTeam` | `scope`, `team`, `expectedHash?` | creates or atomically updates team JSON; `expectedHash` required when modifying an existing file |
| `deleteTeam` | `scope`, `name` (or `id`), `expectedHash` | deletes a team file; `expectedHash` required |
| `transcript` | `subagentId`, `sessionId?` | queries subagent transcript via `get_subagent_messages`; returns at most 40 text-only messages (<= 4,000 chars each, <= 64 KB total) with `truncated` flag |
| `exportTeam` | `scope`, `name` | answers `template`: a `pomp-team-template` v1 JSON text (team + its user-defined member roles; bundled roles by name only), ≤ 256 KiB |
| `openTeamExport` | `scope`, `name` | writes the same text under `%LOCALAPPDATA%\Pomp\exports\` and opens it in the editor |
| `previewTeamImport` | `text`, `scope` | answers `preview: { hash, team: { id, title, mode, members, status }, roles: [{ name, title, status }], problems }`; status `new`\|`same`\|`conflict` (roles also `bundled`\|`existing`) |
| `importTeam` | `text`, `scope`, `previewHash` | creates the new roles, then the team, only as new files; refuses when the preview hash changed, a name conflicts or `problems` is not empty; removes the files it created when a later write fails |
| `mainRole` | | answers `main: { scope, project?: { exists, text, hash? }, user: { exists, text, hash? } }`: POMP's own role instructions (below) |
| `saveMain` | `scope`, `instructions`, `expectedHash?` | writes that scope's `APPEND_SYSTEM.md`; blank text removes it; refused when the file's hash is not `expectedHash` |

POMP's own role (Agents › 역할 › POMP, the chat agent): `agents/state` carries `main: { avatar?, scope?, restartRequired }`. Its picture is
stored like a built-in role's (`setAvatar` with scope `user`, name `main`; images keyed `bundled:main`). Its instructions are the engine's
`APPEND_SYSTEM.md`, appended to the chat agent's system prompt: `<root>/.omp/APPEND_SYSTEM.md` (project; the engine also reads `.claude`, `.codex`,
`.gemini`) before `~/.omp/agent/APPEND_SYSTEM.md` (user); `scope` names the one the engine takes. `agents/command mainRole` replies
`main: { scope, project?: { exists, text, hash? }, user: { … } }`; `saveMain { scope, instructions, expectedHash? }` writes that scope's file
(blank text removes it; refused when the file's hash is not `expectedHash`). The engine reads the file only when it starts: the host keeps what it
was at the last start (`Starting`), and `restartRequired` is true while the engine runs with other instructions; `bridge/restart` applies them.

Teams carry `isolation` (boolean, default false): members work in isolated copies and their changes come back as isolated results to review and apply (see "Isolated team runs").

### Knowledge

One list per workspace, managed in Tools › Knowledge; the role and team editors and the context basket read the same list. A knowledge is a user tag (name and color, no predefined tags) holding **pieces**.

- `knowledge/state` (host → UI): `{ requestId?, root, sessionId?, selected: string[], optimizing?: string, items: KnowledgeItem[], hidden?: number, error? }`. No count limit: items that would pass one message (1.75 MiB) are left out and counted in `hidden`. `selected` = the knowledge ids selected for the current conversation; `optimizing` = the id being optimized. Unasked pushes have no `requestId`.
  `KnowledgeItem = { id, name, color: "#rrggbb", scope: "project"|"personal", use: "original"|"optimized", revision, createdAt, updatedAt, roles: string[], teams: string[], candidate: boolean, optimized?: { text, createdAt, stale }, pieces: [{ id, text, source: "user"|"agent"|"editor", status: "saved"|"candidate", createdAt, anchor?: { path, startLine, endLine, status: "current"|"moved"|"changed"|"missing" } }] }`.
  A candidate knowledge (all pieces candidates, created by an agent proposal) is not listed by the page; its pieces appear only as proposals.
- `knowledge/draft` (host → UI): `{ root, path, startLine, endLine, text }` from the editor's POMP ▸ Add to Knowledge (no list of knowledge in the menu); the page shows Tools › Knowledge with the add form holding that piece, where the person picks a knowledge by name (up to 8 names containing the typed text) or names a new one; it is sent as `create`, which adds to the knowledge of that name (path workspace-relative, lines 1-based inclusive). A page that is not ready gets it at the end of its `ui/ready` snapshot, like `composer/prefill`.
- `knowledge/command` (UI → host): `{ requestId, root, action, ... }`; each is answered by one full `knowledge/state` with the same `requestId`. `root` must be the session's workspace; changes need the knowledge's current `revision`.

| `action` | Fields | Effect |
|---|---|---|
| `refresh` | — | current state |
| `create` | `knowledge: { name, color, scope }`, `piece?: { text, anchor?: { path, startLine, endLine } }` | new knowledge; when the name exists (case-insensitive) the piece is added to it instead |
| `update` | `id`, `revision`, `knowledge: { name, color, scope, use }` | renaming onto another existing name is an error |
| `delete` | `id`, `revision` | removes the knowledge |
| `addPiece` | `id`, `revision`, `piece: { text, anchor? }` | adds a saved piece (a duplicate adds nothing) |
| `updatePiece` | `id`, `revision`, `pieceId`, `text` | an anchored piece keeps its path and range |
| `deletePiece` | `id`, `revision`, `pieceId` | the last piece of a candidate knowledge deletes it |
| `acceptPiece` | `id`, `revision`, `pieceId` | candidate → saved (duplicates removed) |
| `optimize` | `id`, `revision` | runs the editor model; a state with `optimizing` follows, then the result or `error` |
| `assign` | `kind: "role"\|"team"`, `name`, `ids` | afterwards exactly the knowledge in `ids` lists `name` in its roles/teams |
| `select` | `ids` | the selection of the current conversation |

- Limits: name 1–40 characters (no control characters), piece text ≤ 8,000, anchor ≤ 200 lines, optimized text ≤ 16,000; 100 knowledge, 100 pieces each, 20 candidate pieces in total, store ≤ 4 MiB. Stored in the workspace's `.pomp/local/knowledge.json` (git-ignored); the former `%LOCALAPPDATA%\Pomp\knowledge\` store is migrated once and then removed. Scopes are POMP usage scopes, not access control.
- Duplicates: pieces of one knowledge whose normalized text (LF, no trailing white space, no leading/trailing blank lines) and anchor path are equal are kept once.
- Used content: `original` = the saved pieces joined by a blank line (an anchored piece as "`<path>` lines a–b:" plus a fenced block); `optimized` = the stored optimized text while its source hash matches the original, otherwise the original (`stale`). Optimize uses the editor-model mode `knowledge` (60 s, 4,096 tokens, original ≤ 24,000 characters). With the option `Optimize knowledge automatically` an optimized knowledge is optimized again about 5 s after its pieces change.
- Anchors: when building the state the host looks for the stored lines in the file (files over 2 MiB count as `missing`): `current` at the stored range, `moved` when found exactly once elsewhere (the range is updated without a revision change), `changed` when only the first and last lines are found in order, otherwise `missing`.
- Delivery to the model, all inside blocks marked as reference material (not instructions that change tool permissions, approvals or host policy; tags inside are neutralized):
  - Selected knowledge of the conversation (20 / 24,000 characters) → `POMP_STATE_DIR/session-knowledge.json` `{ version: 1, root, sessionId, items: [{ title, body }] }`; the omp extension appends it to the main session's system prompt as `<pomp_knowledge>` when root and session match. A resumed conversation keeps its selection; a new one starts empty.
  - Knowledge assigned to a team (newest 10 / 12,000 characters) → a `<team_knowledge>` block appended to the messages the user sends to that team. Restored transcripts hide the block.
  - Knowledge assigned to a role (newest 10 / 8,000 characters per role) → `POMP_STATE_DIR/role-memory.json` v1 (title = knowledge name); the extension adds it to that role's subagent system prompt as `<role_memory>`.
- Host tools: `propose_knowledge { tag, text }` adds a **candidate** piece to the knowledge named `tag`, or a new candidate project knowledge (never personal); only the user accepts it. `read_knowledge { ids? }` (only with the option `Agents read knowledge`; read-only) lists project knowledge `{ id, name, pieces, chars }` or returns the used content of `ids` (≤ 24,000 characters) and adds them to the conversation's selection. Personal knowledge is never listed or returned. Both post an unasked `knowledge/state`.

### Advisors

Agents › 조언자 manages the advisors of the engine's `WATCHDOG.yml` (project: `<VCS root or engine cwd>/WATCHDOG.yml`; user: the engine's agent
directory; an existing `.yaml` is used as the engine would). The host reads, validates and writes the file itself with the engine's rules and asks the
engine to apply it; the engine extension (`extensions/advisor.ts`) turns the advisors on and off.

- `advisors/state` (host → UI, posted whenever it changes; the `status` command asks for it): `{ supported, sessionId?, enabled, status, advisors: [{ name, model?, status, contextTokens?, contextWindow?, cost? }],
  files: [{ scope, path }], roster: [{ name, scope, enabled, model? }], implicitDefault, updatedUtc }`. The host normalizes the top-level `status` and every
  `advisors[].status` to exactly `"off" | "no-model" | "active" | "paused" | "unknown"` (engine values: running → active, no_model → no-model,
  disabled/stopped → off, idle/waiting/suspended → paused, anything else → unknown). `implicitDefault` means no file defines an advisor, so the engine's
  default advisor runs. `contextTokens`/`contextWindow`/`cost` (US dollars) are the figures the engine's advisor status reports; the page shows them as
  context percent and spend.
- `advisors/command` (UI → host): `{ requestId, action: "load"|"save"|"enable"|"disable"|"status"|"history"|"open"|"select"|"openHistory", scope?, document?, baseHash?, path?, name?, on? }`, answered by
  `advisors/reply` `{ requestId, ok, error?, scope?, path?, exists?, hash?, document?, warnings?, applied?, active?, history?, truncated?, notes? }`.
  - `load` returns the scope's `document` `{ instructions?, maxNotesPerUpdate?, advisors: [{ name, enabled?, model?, reviewMode?: "turn"|"agent-end",
    reviewInterval?, syncBacklog?: "off"|"1"|"3"|"5"|"strict", tools?: string[] (absent = default tools, empty = none), instructions?, maxNotesPerUpdate? }] }`
    and its `hash`. `save` writes `document` only when `baseHash` still matches (otherwise a conflict error; the page offers reload). A saved file whose
    instructions pull in other files with `@` is stored but not applied: the reply has `applied: false` and the page shows "다음 대화부터 적용".
  - `select { name, on }` turns one advisor on or off in this conversation only: the host keeps the conversation's choices (advisor slug → on, over the
    files' `enabled`) and sends the engine extension `apply { choices, enabled }`, which applies the files' advisors with those choices
    (`applyAdvisorConfigs`, files untouched) and sets the conversation's on/off. Turned on while the conversation runs none, only that advisor starts;
    with none left on, the conversation's advisors stop. Without advisor files it switches the engine's own `default` advisor. Every other command
    carries the choices too, so a `save` re-applies the files with them. `enable`/`disable` remain for the whole conversation.
    `open` opens one exact `path` of the latest `files` snapshot in VS.
  - `history { name? }` returns `notes`: every note the advisors gave in this conversation, oldest first (`[{ advisor, severity?: "nit"|"concern"|"blocker",
    note, at? }]`, read from the session's "advisor" custom messages; no count limit: the newest that fit 160 KiB, 8,000 characters each), and `history`: the engine's compact
    advisor transcript, only advisor `name`'s part when given (its "### Advisor: name (…)" section), or `null` when that advisor does not run. Only its
    newest part, cut at a line start and at most 256 KiB of UTF-8, is sent; `truncated` says so. `openHistory { name }` writes that transcript to
    `<DataDirectory>/exports/advisor-<slug>-<time>.md` (the newest ten are kept) and opens it in VS; it fails while that advisor does not run.
  - `chat/advisorNotes` (host → UI, part of the transcript): `{ notes: [{ advisor, severity?, note }] }` for each "advisor" custom message the engine
    delivers to the chat agent (shown as one card; restored with the conversation). The host refreshes `advisors/state` after every run.
  - The built-in advisor is the entry named `default`. Like the engine's own editor, the page saves a lone `default` entry with no other settings as no
    entry at all, so saving the built-in advisor without changes, or resuming it after a pause, leaves the built-in advisor in place. Each advisor
    row's switch (`select`) is the page's on/off control; an entry's `enabled: false` is edited as **일시 정지** in its form.
  - Requests carry the session and a 15-second expiry, so a request queued past the host's timeout never runs. Advisor tools pass through the normal
    tool approvals.

### Event rules between agents and teams

- `agentHooks/command` (UI → host): `{ requestId, root, sessionId, action: "refresh"|"save"|"delete"|"autoForward", id?, revision?, rule?, enabled? }`; answered by `agentHooks/state`
  `{ requestId, root, sessionId, autoForward, forwards, maxForwards, log: [{ at, rule, event, source, outcome }], rules, error? }` (also sent unsolicited after rules fire).
- A rule: `{ name, enabled, event: "agent.completed"|"agent.failed"|"team.completed"|"team.failed"|"feedback.waiting", sourceKind: "any"|"agent"|"team", sourceId?, action: "notify"|"forward", targetKind?: "main"|"agent"|"team", targetId?, message }`.
  At most 30 per workspace, stored in the workspace's `.pomp/local/agent-hooks.json`. They are separate from the IDE save/build hooks of `.pomp/hooks`.
- `notify` posts a chat notice. `forward` queues an ordinary chat message (`clientMessageId` `hook-…`, editor context off, the event summary in an `<event_summary>` data block) through the chat queue and approvals, and only while this conversation's `autoForward` is on. `autoForward` starts off and turns off on a new engine connection or another conversation.
  The page has one switch for it, Agents › 자동화 › 자동화 규칙 **이 대화에서 사용**, which sends `autoForward` and the project-hooks `enable` together.
- Each event fires once; an agent job that was already finished when first seen raises nothing; events of a turn started by a rule (and of jobs first seen during it) never forward again; at most 10 forwards per conversation; a forward never resumes a paused queue; a team's result cannot be forwarded back to the same team.
- Routines: the IDE events `ide.saved` (rule `glob`, workspace-relative, default `**`, ≤ 200 characters), `ide.build.succeeded` and `ide.build.failed` take `sourceKind: "any"` only. They are raised only while the conversation is idle; a rule waits 60 seconds between messages and is skipped while its previous message is still queued or running. The event summary holds the relative path or the build outcome only.

### Isolated team runs

The engine overlay sets `task.isolation.enabled: true`, `merge: patch`, `apply: false` and `worktree.base` to the workspace's `.pomp/local/wt`. A team with `isolation` runs only in a Git workspace and always starts new members; the extension blocks `isolated` task requests outside such a run and member tasks without `isolated: true` inside it.

When a member ends (a terminal `subagent_lifecycle` frame with its `sessionFile`), the host picks up the engine's patch next to that session file and parses it strictly: strict UTF-8, ≤ 2 MiB, text add/modify/delete only (no renames, copies, binaries, `..`, drive, backslash or control-character paths), ≤ 100 files. Paths are resolved from the Git repository root; a result touching anything outside the workspace (solution folder) is listed as `blocked`. At most 10 results wait (`pending` or `blocked`); further ones are only reported with their patch path.

- `workspace/isolationState` (host → UI): `{ requestId?: string|null, items: [{ id, root, agent, job, status: "pending"|"blocked"|"applying"|"applied"|"failed", message, createdAt, patchPath, files: [{ path, kind: "create"|"modify"|"delete", added, removed }] }], hidden?: number }`. Pushed when a result arrives or changes (`requestId: null`) and as the answer to every command (its `requestId`). The page shows only items whose `root` is the current workspace. Applied and failed items stay for reference until discarded; there is no count limit (the oldest of them go first when the held patches pass 64 MiB). A message that would pass the UI hub's size sends the newest items and counts the rest in `hidden`.
- `workspace/isolation` (UI → host): `{ action: "refresh"|"apply"|"discard"|"openPatch", requestId, root?, id? }`. Every action except `refresh` needs the item `id` and the current workspace `root`. `apply` checks the project policy for `pomp_stage_workspace_edit`, refuses files with unsaved editor changes, non-UTF-8 files, missing files to change, existing files to create and any context mismatch, then shows one approval card with the patch; after approval it plans again and writes nothing if a file changed meanwhile. Files are written directly (BOM and line endings kept, written files restored on failure) with a checkpoint in the change history. `discard` removes the item only; the patch file stays. A refused action keeps the item and puts the reason in its `message`.

### Background agent progress and team correlation

`workspace/backgroundState` includes enhanced progress metrics, parent tool call link, and team correlation for subagent jobs:

- `progress`: `{ toolCount: number, tokens: number, cost: number, durationMs: number, recentTools: string[], resolvedModel?: string, thinkingLevel?: string }`. Up to 5 recent tool names (<= 80 chars each).
- `parentToolCallId?`: string (<= 256 chars), identifying the parent `task` tool invocation.
- `team?`: string (<= 40 chars), the team ID active for the conversation turn that spawned the subagent job.
- `promptId`: string or null (<= 128 chars, no controls), the host turn open when this job was first observed; kept through later progress and terminal states.
- `parentToolCallId`가 있는 구성원은 채팅의 해당 `task` 카드에 상태·도구 수·시간·최근 도구와 개별 중지를 붙인다. 상대 탭에서도 같은
  카드가 보인다. 해당 호출 카드가 없는 현재 구성원만 접을 수 있는 진행 줄에 나타난다.

### Stopping one member

`workspace/backgroundState.stopAvailable` is true while the engine lists POMP's `pomp-stop` extension command (docs/agent-teams.md §14). Then `workspace/background { action: "stop", requestId, root, sessionId, id }` (no `generation`) stops that running member only: the host sends `/pomp-stop <id> --request <id>` and waits up to 10 seconds for the extension's `pomp-stop-result:` notification, which never reaches the transcript. The answer is the `workspace/backgroundState` carrying `controlRequestId` with `controlPending: false`; `controlMessage` says what happened or why not (not running, not started by this conversation, engine without the internals). Every control request (`pause`, `resume`, `cancel`, `stop`) is answered that way, refused ones included. With the engine's own control protocol (`controlVersion: 1`) the page keeps pause/resume/cancel and does not add Stop.
