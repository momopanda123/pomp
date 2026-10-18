# Pomp UI protocol (WebView2 chat UI ⇄ VS host)

This is the contract between the chat page and the host.

- The chat page is `src/Pomp.WebUI`, hosted in WebView2 inside the `Pomp` tool window.
- The host is the `Pomp.Core` session controller.
- The page never talks to omp or to Visual Studio APIs directly (design Layer 1 → Layer 2 only).
- C# mirror: `src/Pomp.Contracts/Ui/UiProtocol.cs`.

## Transport

### Channel and envelope

- Page → host: `window.chrome.webview.postMessage(obj)`. The argument is an object, not a string.
- Host → page: `CoreWebView2.PostWebMessageAsJson(json)`.
- The page listens with `window.chrome.webview.addEventListener("message", e => e.data)`. `e.data` is already an object.
- Envelope: `{ "type": string, "id"?: string, "payload"?: object }`.
- JSON conventions: camelCase property names, enums as camelCase strings, absent optional fields are omitted.
- Unknown `type` values must be ignored in both directions (forward compatibility).

### Startup and replay

- The page sends `ui/ready` once its listeners are installed.
- The host queues everything posted before that, then replays it in order, starting with `host/hello`.
- The host keeps a bounded log of the current session's transcript: the `chat/*` messages, reset by `session/new`.
- After every `ui/ready` the host sends `chat/replayBegin`, that log and `chat/replayEnd`.
  A reloaded page therefore shows the conversation again.

### Inbound limits

The VS host enforces these limits. The constants live in `WebViewUiChannel`.

| limit | value |
|---|---|
| message size | at most 4 MiB, checked before parsing |
| `type` length | at most 64 characters |
| `ui/log` message size | at most 64 KiB |
| `ui/log` rate | token bucket of 200 messages/s, burst 400 |

- A message dropped for size is answered with `error` / `message_too_large`.
- The `ui/log` token bucket is reset on attach and on navigation.
- Excess `ui/log` lines are dropped with one throttled warning.
- Other message types are never rate-limited.
- The page refuses to send a message whose text exceeds omp's per-message limit, about 1 MiB of UTF-8. It keeps the draft.
- Both permission requests and generic questions block new chat submissions and retries. Editable drafts are preserved.
- Generic input/editor Enter handling ignores IME composition, including legacy keyCode 229.

### Outbound queue and delivery failure

When the page is slow, the host folds messages into the message still waiting:

- consecutive `chat/assistantDelta` with the same message and kind, up to 65,536 characters per folded message;
- consecutive `chat/toolUpdate` for the same tool call.

The queue is bounded:

| limit | value |
|---|---|
| one message | at most 2 MiB of UTF-8 JSON |
| outbox | at most 8 MiB and 10,000 messages |
| before `ui/ready` | at most 2,000 messages |
| one post | must complete within 30 s |
| pending approvals and dialogs together | at most 128 requests and 4 MiB |

- When the queue is full, further `chat/toolUpdate` and `chat/notice` messages are dropped and logged.
- Delivery enters a **failed** state instead of silently losing history when:
  - any other message does not fit;
  - a post fails;
  - a post does not complete within 30 s.
- On entering the failed state:
  - pending approvals are denied;
  - pending dialogs are cancelled;
  - the queue is cleared;
  - one `error` with code `ui_delivery_failed` and `retryable: false` is queued.
- Nothing else is delivered until the page sends `ui/ready` again.
- That `ui/ready` starts a new delivery epoch. Its replay begins with the `ui_delivery_failed` error, so the page knows the transcript may be incomplete.
- Pending approvals and dialogs are replayed on every `ui/ready`. A request beyond their limit is denied or cancelled at once.
- Otherwise everything is delivered in order.

### Host-internal messages

The VS host handles these page messages in the WebView channel. They never reach the session.

#### `ui/toggleFocus`

- The page sends it when the `shortcuts.toggleFocus` chord of `host/hello` is pressed inside the page.
- The default chord is `Alt+J`, set in Tools > Options > Pomp > General > Shortcuts.
- Meta and AltGr do not match. IME composition, key repeat and keyCode 229 are ignored.
- The page app does not send it.

#### `ui/pasteFiles`

- The chat input sends it for a paste of files or an image without plain text. A page cannot read their paths.
- The VS host reads the clipboard on its UI thread: the file drop list, else the `PNG` format or the bitmap.
- The session puts the files into the context basket with `composer/prefill { files }`.
- An image is first saved as `<root>/.pomp/local/clipboard/clipboard-<time>.png`.
- Folders and missing files are left out.

#### `ui/dropFiles`

- The page sends it when files are dropped anywhere on it, with `chrome.webview.postMessageWithAdditionalObjects`: the payload is `{}` and the dropped `FileList` goes as the additional objects.
- The VS host reads each `CoreWebView2File.Path` from `AdditionalObjects` and puts the files into the context basket as for `ui/pasteFiles`. Folders and missing files are left out.
- `AdditionalObjects` needs WebView2 SDK 1.0.1901.177 (earlier SDKs have the property but throw) and Runtime 113. VS 2017/2019 ship that SDK. VS 2022+ use their own copy, read through reflection.
- Where the runtime lacks the API, the page sends a `ui/status` warning instead. Where the host's WebView2 copy lacks it, the host logs a warning.
- The page cancels every file drop, so the WebView never navigates to a dropped file. Text dragged from the editor still drops into the chat input.

#### `ui/status`

Payload: `{ text, level: "error"|"warning"|"info" }`.

- These are the page's short notices that are not drawn inside the page. Sources:
  - the chat input and its chips;
  - the conversation header;
  - the partner tab strip;
  - the queue.
- Examples: a failed model, thinking or permission change; why a message was not sent; a lost target; a failed rename or conversation-list request; a failed Live/bookmark change; a failed queue action.
- The VS host shows the text as `POMP: <text>` in the VS status bar, on one line and at most 300 characters.
- The VS host logs errors and warnings. A repeated notice is logged once.
- Dialog and form field errors stay where they are.
- When the page cannot post the message because its channel to Visual Studio failed, the notice shows in the banner above the chat instead.
  It stays until a later notice gets through or the banner is closed.

#### `ui/editorFollow`

Payload: `{ requestId, action: "set", live?, bookmarks? }`.

- It changes only the specified persisted editor toggles.
- The channel intercepts it before session dispatch. It causes no model request, token usage, or engine restart.
- `host/editorFollow` carries `{ live, bookmarks, requestId?, error? }`. It is sent:
  - after page readiness;
  - after Options/menu changes;
  - after each request, echoing its `requestId`, with `error` on failure.
- The page disables these controls until this capability/state payload arrives.
- The page renders only acknowledged values, and reports failed delivery or a missing reply.
- Before forwarding authoritative `session/info` to the page, the VS channel passes `workingDirectory`, `sessionId`, and `isStreaming` to the editor follow service on the UI thread.
- Session switches do not issue an extra model request or unconditionally erase other sessions' records.

## Structured case feedback

### The `present_case_feedback` tool

It accepts only `{ cases: [{ caseId, title, bodyMarkdown, image?, diagram? }] }`.

| field | meaning |
|---|---|
| `image` | workspace-relative path of an existing png/jpg/gif/webp/bmp, ≤ 1 MiB so its data URL fits one page message |
| `diagram` | Mermaid source, ≤ 4,000 characters |

- At most 8 cases may have an image.
- Its result text names the proposal: `(proposal <32 hex>)` inside the text.
- The page shows the proposal as cards inside the assistant reply, in place of that tool call.
- There is one comment box per case and one send for all. There is no separate feedback panel.
- The `case-review` skill tells the model when to use it.

### Ownership and enforcement

- The host issues the proposal ID and captures the current conversation and original turn target.
- Model arguments cannot choose the destination.
- The loaded `pomp-feedback-guard` extension marker is required at execution.
- The extension refuses subagent and unknown callers using the engine's caller identity.
- This is cooperative engine enforcement, not isolation against a compromised engine.
- A stale connection or ended originating turn cannot register a proposal.

### Messages

| type | direction | meaning |
|---|---|---|
| `feedback/command` | UI → host | Refresh, open, save a draft, submit or delete. |
| `feedback/image` | UI → host | Request a case image. |
| `feedback/imageResult` | host → UI | The image as a data URL, or an error. |
| `feedback/state` | host → UI | Feedback snapshot. |

#### `feedback/command`

- Fields: `action: refresh|open|draft|submit|delete`, `requestId`, `root`, `sessionId`.
- Mutations also carry `proposalId` and `revision`.
- Draft and submit also require `draftRevision` and `selections: [{ caseId, selected, comment, extra }]`.
- The cards send `selected` = the comment is not empty, and `extra: ""`.
- A submit with no commented case is refused.
- The queued message holds only the commented cases: id, title, comment. Its shape:
  `User feedback with comments on <n> of <total> cases (proposal <id>, revision <r>).` and further text,
  then a newline and `{"cases":[{caseId,title,comment,extra?}]}`.
- The page shows a message of exactly that shape as the cases and comments in the user bubble.
  In the queue and history preview it shows a one-line summary.
- Any other text, or one that does not parse, is shown as typed.

#### `feedback/image` and `feedback/imageResult`

- Request: `feedback/image { requestId, root, sessionId, proposalId, caseId }`.
- Answer: `feedback/imageResult { requestId, proposalId, caseId, dataUrl?, error? }`.
- When the host reads the image it re-checks the case's image path: inside the workspace, no links, type, size.
- It answers a `data:image/...;base64` URL. The page shows nothing else as an image.
- Nothing comes from the network.

#### `feedback/state`

- Fields: `requestId` matching the request when requested, `root`, `sessionId`, `entries`, optional `proposal`, `error`, and `notice`.
- Creation publishes an unsolicited snapshot.

### Revisions and storage

- Proposal revision and draft revision are separate.
- A stale draft version is refused, not overwritten.
- Storage is local and workspace-scoped under the POMP data directory.
- Mutations take an exclusive cross-process mutation lease.
- Full storage is reported, not evicted.

| limit | value |
|---|---|
| proposals | at most 100 |
| cases per proposal | at most 32 |
| case body | at most 8,000 characters |
| comment, addition | at most 2,000 characters each |

### State transitions

States: `draft → submitted → dispatching → accepted`.

- Draft save never requests a model.
- Final submission preserves the cases and selections as one prompt to the stored target.
- A durable dispatch claim precedes queue admission.
- `accepted` means host queue admission, **not** model success or permission to change files.
- Existing approval and workflow checks remain in force.
- A `submitted` record can retry identical content if no dispatch claim was taken.
- `dispatching` after interruption is uncertain and is never automatically resent.
- Accepted records are not resent.
- The chat queue is in memory and is not restored on application exit.
- An accepted record is historical evidence of admission, not proof that the request survived an exit or ran. Inspect chat and queue.

### Rendering

- Card bodies use the existing safe Markdown renderer.
- No arbitrary HTML, scripts, automatic external image fetches, or external publishing are introduced.
- Dirty UI drafts must survive unrelated snapshots.

## Host → UI

Host text the page shows says what happened, the current state or a reason.

- This covers errors, notices, state `message`s, row `note`s and approval bodies.
- It carries no instructions, retry advice or shortcut text.
- An input-validation error may name the accepted limits.
- Text written for the model is exempt: tool results and prompts. Tool cards show it as conversation content.

| type | payload | notes |
|---|---|---|
| `host/hello` | `{ version, vsVersion?, locale?, includeEditorContextByDefault, composerPredictions, approvalMode, shortcuts }` | First message after `ui/ready`. See "host/hello" below. |
| `host/editorFollow` | `{ live, bookmarks, requestId?, error? }` | Editor follow capability and applied settings. See "Host-internal messages". |
| `bridge/state` | `{ state, message?, attempt?, nextRetryMs?, reason? }` | Engine connection state. See "bridge/state" below. |
| `workspace/trustState` | `{ requestId?, root, required, trusted, fingerprint, files: [{ path, kind, detail? }], truncated?, error? }` | Workspace engine configuration needing trust. See "Workspace trust". |
| `theme/changed` | `{ kind: "dark"\|"light", colors: { [cssVar]: value }, fontFamily?, fontSizePx? }` | Apply every `colors` entry. |
| `session/info` | `{ model?, thinkingLevel?, sessionId?, sessionName?, contextUsage?, isStreaming, workingDirectory?, fastMode? }` | Session state for the composer. See "session/info" below. |
| `chat/turnStarted` | `{ promptId, clientMessageId?, text?, attachments?, editorContext?, team?, agent?, agentInstance?, main? }` | The host took a user message as a prompt. See "chat/turnStarted" below. |
| `chat/assistantStart` | `{ messageId }` | New assistant bubble. |
| `chat/assistantDelta` | `{ messageId, kind: "text"\|"thinking", delta }` | Append; render markdown incrementally. |
| `chat/assistantNarration` | `{ messageId, text }` | The ended thinking block was narration. See "chat/assistantNarration" below. |
| `chat/assistantEnd` | `{ messageId, stopReason?, errorMessage? }` | Finalize the bubble. |
| `chat/toolStart` | `{ toolCallId, toolName, args?, isHostTool }` | Collapsible tool card inside the current turn. |
| `chat/toolUpdate` | `{ toolCallId, text }` | Progress text; replace, not append. |
| `chat/toolEnd` | `{ toolCallId, toolName, isError, resultText?, durationMs? }` | `resultText` is already truncated by the host. |
| `chat/turnEnd` | `{ promptId, status: "completed"\|"aborted"\|"error", error?: { code, message, retryable } }` | Re-enable input; show an error banner on `error`. |
| `chat/notice` | `{ level: "info"\|"warning"\|"error", message }` | Inline system note in the transcript. |
| `chat/advisorNotes` | `{ notes: [{ advisor, severity?, note }] }` | Advisor notes to the chat agent, one card. See "Advisors". |
| `permission/request` | `{ requestId, source: "hostTool"\|"ompApproval", kind, toolName, title, message?, tasks?: [{ agent, task }], target?, diff?, timeoutMs?, canRemember }` | Approval card inside the transcript. See "Request cards". |
| `permission/dismiss` | `{ requestId, reason? }` | The card closes without an answer. |
| `permission/modeResult` | `{ requestId, ok, mode, error? }` | Exactly one answer per `permission/mode`. |
| `dialog/request` | `{ requestId, method: "select"\|"confirm"\|"input"\|"editor", title?, message?, options?, placeholder?, defaultValue?, timeoutMs? }` | Generic question from an omp extension. See "Request cards". |
| `dialog/dismiss` | `{ requestId, reason? }` | |
| `session/fastResult` | `{ requestId?, ok, error?, enabled, active }` | Exactly one answer per `session/fast`. |
| `knowledge/draft` | `{ root, path, startLine, endLine, text }` | The editor's POMP ▸ Add to Knowledge. See "Knowledge". |
| `composer/prefill` | `{ text?, files?: [{ path, fullPath }], includeEditorContext, send, target?, pickTarget? }` | Input from the code editor's POMP menu. See "composer/prefill" below. |
| `work/open` | `{ view }` | Open that view of the work tab. See "work/open" below. |
| `engine/info` | `{ source: "bundled"\|"settings"\|"installed", version?, verified, expectedVersion?, path? }` | Which omp runs. See "Engine version" below. |
| `session/newResult` | `{ requestId?, status: "completed"\|"failed"\|"cancelled", message? }` | Exactly one answer per `session/new`. See "session/newResult" below. |
| `workspace/files` | `{ requestId, files: [{ path, fullPath }], truncated, error? }` | Answer to `workspace/findFiles` / `workspace/pickFiles`. |
| `chat/replayBegin` | `{ truncated }` | Drop the transcript and rebuild it from the following `chat/*` messages. |
| `chat/replayEnd` | `{}` | End of the replayed transcript; live messages follow. |
| `agents/state` | `{ root?, roles: Role[], teams: Team[], diagnostics: string[], main, rolesHidden?, teamsHidden? }` | Agent roles and teams state. |
| `agents/reply` | `{ requestId, ok, error?, name?, transcript?, template?, preview? }` | Answer to `agents/command`. |
| `computer/state` | `{ policy: "off"\|"perConversation", browser, confirmReads, desktop, pending, available, message? }` | Computer use of this conversation. |
| `computer/reply` | `{ requestId, ok, error? }` | Exactly one answer per `computer/set`. |
| `error` | `{ code, message, retryable }` | A host-level error not tied to a turn. |

`kind` of a permission request: `fileWrite` `projectChange` `processExec` `debuggerControl` `breakpointChange`
`bookmarkChange` `packageChange` `vcsWrite` `testRun` `other`.

The page keeps at most 2,000,000 characters of each kind (text, thinking) per assistant message; past that it stops appending and
shows that the answer was cut.

### host/hello

- It is the first message after `ui/ready`.
- It is sent again when:
  - the language option changes the effective locale. The option is Tools > Options > Pomp > General > Chat > Language: Automatic / 한국어 / English;
  - the approval mode changes;
  - the shortcuts change.
- Nothing else resets it. The page compares nothing; it applies what it gets.
- `locale` is `ko-KR`/`en-US` for an explicit choice, else VS's UI culture.
- `approvalMode` is omp's approval mode in effect: `alwaysAsk` \| `write` \| `yolo`.
  The composer's permission-mode chip shows it. `permission/mode` changes it.

`shortcuts` come from Tools > Options > Pomp > General > Shortcuts.

- They are normalized chord strings such as `"Ctrl+Alt+T"`.
- A key may also be `,` `.` `;` `'`.
- An empty string means none.

| shortcut | default |
|---|---|
| `send` | `Enter`; the other value is `Ctrl+Enter` |
| `sendTargetPicker` | `Ctrl+Alt+T` |
| `previousTarget` | `Alt+,` |
| `nextTarget` | `Alt+.` |
| `contextBasket` | `Alt+;` |
| `editorContext` | `Alt+'` |
| `liveFollow` | `Alt+L` |
| `liveBookmarks` | `Alt+K` |
| `permissionMode` | `Alt+Q` |
| `allow` | `Alt+A` |
| `deny` | `Alt+D` |
| `permissionsView` | `Ctrl+Alt+P` |
| `toggleFocus` | `Alt+J` |

### host/editorFollow

- VS-host editor capability and applied settings.
- `live` and `bookmarks` are independent toggles.
- It carries live Options/menu synchronization and correlated replies to `ui/editorFollow`.
- Live follows an applied edit 400 ms after the last one: the caret moves to its first changed line and the line is centred.
  It is skipped while an editor has keyboard focus or IME composition; a selection in that file keeps the caret and selection.

### bridge/state

- `state`: `stopped` `starting` `handshaking` `running` `reconnecting` `failed` `stopping`.
- Input is enabled only in `running`.
- `failed` shows *Restart* and *Show log* actions, with two exceptions:
  - `reason: "setupRequired"`: omp has no model provider yet. The page shows the engine setup panel instead. See "Engine setup (accounts / login / model / usage)".
  - `reason: "workspaceTrustRequired"`: the workspace has engine configuration the user has not trusted. The chat shows the trust card instead. See "Workspace trust".

### workspace/trustState

- It lists what omp would load from the workspace that runs code, starts processes, changes approvals or includes outside files.
- `kind`: `settings`, `mcp`, `tools`, `lsp`, `plugins`, `extensions`, `include`.
- `detail`: for example "MCP 서버 2개" or the outside file an include pulls in.
- It is posted when omp is about to start, and as the answer to `workspace/trust`.
- See "Workspace trust".

### theme/changed

- Apply every `colors` entry with `document.documentElement.style.setProperty(name, value)`.

### session/info

Payload:

| field | meaning |
|---|---|
| `model` | `{ provider, id, name? }` |
| `thinkingLevel` | thinking level |
| `sessionId`, `sessionName` | the session |
| `contextUsage` | `{ tokens, contextWindow, percent, breakdown? }` |
| `isStreaming` | a turn is streaming |
| `workingDirectory` | working directory |
| `fastMode` | `{ available, enabled, active }` |

It feeds the composer: the context meter, the model / effort chips and the fast toggle.

**Context usage**

- `contextUsage` is refreshed after every finished assistant message, not during streaming.
- The bar's filled width is `percent`.

`contextUsage.breakdown` = `{ systemPrompt, systemTools, hostTools, mcpTools, systemContext, skills, messages, messagesEstimated?, autoCompactBuffer, free, contextWindow, measuredAt }`.
Values are tokens.

- The figures are the engine's own `/context` figures.
- `messagesEstimated`: the engine printed no Messages row for this model.
  Then `messages` is the host's estimate: the usage total minus the printed rows. It is shown with ≈.
- `hostTools` and `mcpTools` are the host's estimates of the VS and MCP shares inside `systemTools`.
  They are based on definition sizes, four characters per token.
- The meter paints one segment per source in a fixed order: prompt, tools, MCP, context files, skills, messages.
  It lists them in its popover.
- `breakdown` is absent until a measurement exists.

When the host measures:

- The host runs `/context` through the command queue after every terminal `agent_end`.
- It also runs it once for a conversation it has not measured yet: first state, new or switched session.
- It runs only while the engine's command catalog lists `context` with `source: "builtin"`.
  A prompt template or skill of that name never counts: it would start a model turn.
- A measurement never starts while one is still waiting for its output.
- After two refused, late or unreadable measurements in one conversation, the host stops asking for that conversation.

**Fast mode**

- `fastMode` comes from `get_state` (`fastModeEnabled`/`fastModeActive`).
- `available` is true when the model's provider family has a priority tier, or when fast mode is on.
  Families with a priority tier: openai, openai-codex, google, anthropic-messages, openrouter of those classes.

**Session change**

- A new `sessionId` while the transcript has items means the earlier conversation is gone from omp.
  The UI says so once.

### chat/turnStarted

Payload fields:

| field | meaning |
|---|---|
| `promptId` | the prompt the host started |
| `clientMessageId` | the user message the host took |
| `text`, `attachments: [{ path }]` | the user's input as typed, without editor context |
| `editorContext` | `{ path, startLine?, endLine?, caretLine? }`: the active file actually prepended to the prompt |
| `team`, `agent`, `agentInstance` | the destination the message actually went to |
| `main` | `true` when the conversation answered itself (no one-message target) |

**Turn lifecycle**

- The host took the user message `clientMessageId` as prompt `promptId`.
- It is posted before omp sees the prompt.
- Exactly one `chat/turnEnd` with the same `promptId` follows. It may come at once for validation or engine errors.
- A busy submission is acknowledged by `chat/queued` instead and has no turn yet.

**Text and attachments**

- A page that shows no bubble for `clientMessageId` creates the user bubble from them.
  This happens for a replay, or a prompt started elsewhere.
- Otherwise they are ignored.

**Editor context**

- It updates both optimistic and replayed bubbles with a muted reference chip.
- Its `path` is workspace-relative with forward slashes inside the workspace, otherwise the full path.
  The full path is also the tooltip.
- Lines are 1-based.
- `startLine`/`endLine` are an inclusive non-empty selection range. An end at column 1 excludes that line.
- Otherwise `caretLine` labels the nearby editor lines.
- It is omitted for context-off sends, slash commands, or no active document.
- Restored history recovers it only from POMP's leading `<vs_context>` block, after any host routing brief (and the tab marker an earlier POMP wrote).

**Destination**

- The destination is the explicit target, or the conversation partner the host applied.
- A replayed bubble thus keeps its "→ target" badge.
- With `main: true` there is no badge, and a retry goes to the conversation itself again.

**Restored history of another session**

- It shows the extension's routing brief, such as `<!-- pomp-team:… -->`, at the start of the user text.
- An earlier POMP put a tab marker `<!-- pomp-from:<kind>:<id> -->` after it; the host never shows that marker.
- Session titles, previews and archive pages leave both out.

### chat/assistantNarration

- The thinking block that just ended was narration: prose the model addressed to the user that the provider sent as thinking.
- The engine marks it with omp 18.12+ `narration: true`, for example Claude Opus 5.5 between tool calls.
- The page moves `text`, the end of that message's thinking, to its answer text.
- A thinking that does not end with `text` is left as it is. This happens when it was capped.
- A message delivered whole, a restored conversation and the conversation preview show narration as answer text directly.

### permission/request, permission/dismiss and permission/modeResult

- `permission/request`: an approval card inside the transcript. See "Request cards (design: 권한 카드)".
- `canRemember` absent means `true`.
- `permission/dismiss`: the card closes without an answer because it timed out or omp cancelled it.
  It leaves its record in the transcript.
- `permission/modeResult`: `mode` is the mode in effect after the answer.
- A changed mode restarts omp: bridge/state goes `stopping` and on to `running`, and the conversation is restored.
- A new `host/hello` carries the changed mode.

### session/fastResult

- Exactly one answer per `session/fast`.
- `session/info.fastMode` follows.

### composer/prefill

It comes from the code editor's POMP menu: explain, review, ask, add file, and "to agent or team".

| field | meaning |
|---|---|
| `text` | text for the input |
| `files: [{ path, fullPath }]` | files for the context basket |
| `includeEditorContext` | turn the editor context on |
| `send` | send once, right away |
| `target` | `{ kind: "agent"\|"team", id }`, sent by "to agent or team" |
| `pickTarget` | open the send-target picker |

- With `target`, the page selects it as the one-message send target, shows the main conversation tab and focuses the input.
  The chip then shows that target's picture and name.
- Past the menu's 15 entries, its "show all" sends `pickTarget`.
  The main conversation is shown with the send-target picker open.
- The page shows the chat and adds `files` to the context basket.
- It turns the editor context on when asked.
- It puts `text` into an empty input. An unsent draft is kept and the text appended, never sent.
- `send` sends it once, right away, if the chat can take a message. Otherwise the text stays in the input.
- A page that was not ready gets it once, at the end of its `ui/ready` snapshot, if within a minute.
- Bounded: text ≤ 4000, ≤ 20 files.

### work/open

- It opens that view of the work tab: `review`, `plan`, and others.
- It is an event, not state.
- It is sent once when the user started something outside the page.
  Today that is after an inline edit proposal is staged; the review opens at once.
- It is ignored while a permission/dialog is up, and for unknown views.
- The page also accepts the Tools ids: `extensions`, `instructions`, `index`, and the older `skills`, `mcp`, `rules`, `workflows`.
- `hooks` opens Agents › 규칙.
- `background` opens Agents › 실행 중 (Running).

### Engine version

`engine/info` says which omp runs.

- It is state, also in the `ui/ready` snapshot.
- It is sent at every engine start.

| `source` | meaning |
|---|---|
| `bundled` | the engine this POMP release ships; with `verified: true` its size and SHA-256 were checked |
| `settings` | the omp path of Tools > Options > Pomp |
| `installed` | a development package without an engine uses an omp found on PATH / usual folders |

- `expectedVersion` is the omp version the release ships. It is absent without a bundled engine.
- For an unverified engine named `omp`, `version` arrives in a second `engine/info` after one `omp --version` run.
- The page shows it at the bottom of the ⋯ menu.
- While the engine is unverified, the page shows a dismissible warning above the chat.
- See docs/architecture.md §4 "엔진 버전".

### session/newResult

- Exactly one answer per `session/new`.
- The page clears its transcript only on `completed`. Otherwise it shows `message`.
- `cancelled`: an omp extension vetoed it.
- A non-null `requestId` that is not the page's pending `session/new` is ignored: late, duplicate or abandoned.
- An absent/null id keeps the legacy meaning: a new session started outside the page, for example by a VS command.

### workspace/files

- Answer to `workspace/findFiles` / `workspace/pickFiles` with the same `requestId`.
- `path` is workspace-relative with forward slashes when inside the workspace, else the full path.
- The page ignores a late answer to a superseded request.

### chat/replayBegin and chat/replayEnd

- `chat/replayBegin` is sent after `ui/ready`.
- The page drops the transcript: messages, tool cards, turn state. It rebuilds it from the following `chat/*` messages.
- `truncated`: the oldest part of the session is no longer available. Show one notice.
- `chat/replayEnd` ends the replayed transcript. Live messages follow.
- A turn that was still open in the replay stays open. Its `chat/turnEnd` comes live.

### agents/state and agents/reply

- `agents/state` is included in the `ui/ready` snapshot and published on change and on workspace change.
- It has no count limit. A list too large for one message keeps its front and counts the rest in `rolesHidden` / `teamsHidden`.
- `agents/reply` is the answer to `agents/command`.
- `transcript`: `{ agent, messages: [{ role: "user"|"assistant"|"toolResult"|"tool", text }], truncated }`.
  - System/developer messages and engine reminder blocks are removed.
  - An assistant turn that only submitted a result carries that result as `text`.
- `template`/`preview` are for team export/import.

### computer/state

- Computer use of this conversation; see docs/architecture.md §8.5.
- It is state, also in the `ui/ready` snapshot.
- `desktop`: on now.
- `available`: the toggle can switch it on. The policy is not off and the extension's guard is loaded.
- `message`: why it is unavailable, or why it was switched off, for example by another conversation or an engine restart.

### error

A host-level error not tied to a turn. It is sent when:

- the VS host drops an inbound message that is too large: `message_too_large`.
  A page waiting for `chat/turnStarted` treats it as a failed send.
- outbound delivery failed: `ui_delivery_failed`. Approvals were denied and the transcript may be incomplete. See "Transport".

### `chat/turnEnd` error codes

| code | when | retryable |
|---|---|---|
| `timeout` | omp did not acknowledge the prompt within 30 s. It may still run, so the UI must not resend blindly. | no |
| `message_too_large` | The prompt frame would exceed omp's 1 MiB frame limit, editor context included. | no |
| `send_failed` | Writing the prompt to omp failed: pipe closed, omp not running. | yes |
| omp's `code` or `prompt_rejected` | omp answered the `prompt` command with `success: false`. | no |
| `connection_lost` | The agent process ended while the turn was open. The message says why, incl. stderr. | yes |
| `provider_error` / `http_<status>` | omp's `prompt_result` reported a provider error. `retryable` comes from omp. | per omp |

- A turn stopped with *Stop* ends as `aborted`.
- Normally that comes from omp's own `prompt_result`.
- Otherwise the host ends it once omp has been idle for the settle delay of 5 s.

## UI → Host

| type | payload | meaning |
|---|---|---|
| `ui/ready` | `{}` | The page is ready. |
| `ui/log` | `{ level: "debug"\|"info"\|"warning"\|"error", message }` | Page-side diagnostics into the host log. |
| `ui/editorFollow` | `{ requestId, action: "set", live?, bookmarks? }` | Persist the specified editor-follow toggles. |
| `chat/send` | see [`chat/send`](#chatsend) | Send a message or queue it while busy. |
| `workspace/countDraftTokens` | `{ requestId, root, sessionId, modelId, provider, text, attachments?: [{ path }] }` | Count the draft, attachments and knowledge with the local tokenizer. |
| `workspace/countBasketTokens` | `{ requestId, root, sessionId?, includeEditorContext, attachments?: [{ path }] }` | Estimate what the next message adds beyond its text. |
| `workspace/findFiles` | `{ requestId, query, limit? }` | Files for the `@` mention list. |
| `workspace/pickFiles` | `{ requestId }` | Show a multi-select file dialog. |
| `chat/stop` | `{}` | Abort the running turn (design: cancel). |
| `permission/mode` | `{ requestId, mode: "alwaysAsk"\|"write"\|"yolo" }` | The composer's permission-mode chip. |
| `permission/respond` | `{ requestId, decision: "allow"\|"allowForSession"\|"alwaysAllow"\|"deny", note? }` | Answer one approval request. |
| `dialog/respond` | `{ requestId, value?, confirmed?, cancelled? }` | Answer one dialog. |
| `session/fast` | `{ requestId, enabled }` | Toggle omp fast mode. |
| `session/new` | `{ requestId }` | Start a fresh omp session. |
| `bridge/restart` | `{}` | Restart the omp process after `failed`. |
| `workspace/trust` | `{ requestId, action: "refresh"\|"trust"\|"open", root?, fingerprint?, path? }` | Workspace trust actions. |
| `log/open` | `{}` | Show the pomp log in VS. |
| `settings/open` | `{}` | Open Tools > Options > Pomp. |
| `setup/command` | `{ requestId, action, provider?, key?, modelId?, level?, promptId?, value? }` | Engine setup; see "Engine setup". |
| `session/history` | `{ requestId, action }` plus action fields | Session list, switch, preview, rename, compact; see "Sessions". |
| `agents/command` | see [`agents/command`](#agentscommand) | Agent role and team management; see "Agents and teams". |
| `computer/set` | `{ requestId, desktop }` | Switch computer use of this conversation on or off. |
| `workspace/agentPlan` | `{ requestId, sessionId, base: [{ name, tasks: [{ content, status, blocker? }] }], phases }` | Replace the agent work plan. |
| `workspace/agentPlanPage` | `{ requestId, sessionId, page, size, query? }` | Read one page of the whole plan. |
| `workspace/agentPlanEdit` | see [`workspace/agentPlanEdit`](#workspaceagentplanedit) | One change to the whole plan. |

### Message details

#### `ui/log`

- The message is limited to 64 KiB.

#### `ui/editorFollow`

- VS host only.
- Answered by `host/editorFollow`.
- Never forwarded to omp.

#### `chat/send`

Payload:

```
{ clientMessageId, text, includeEditorContext, attachments?: [{ path }],
  team?: string, agent?: string, agentInstance?: string, main?: true }
```

**Turn start and queueing**

- A message sent while idle starts a turn.
- A message sent while busy enters the host command queue.
- A queued message receives `chat/queued`, not `chat/turnStarted`.

**Attachments**

- `attachments` are full paths, taken from `workspace/files`.
- The host lists them in the prompt for omp to read.

**Targets**

- `team` is an optional team id. When set, the message is dispatched to that team through `/pomp-team`.
- `agent` is a role name. It makes the message a new assignment run by exactly that agent (`/pomp-agent`).
- `agentInstance` is a subagent id from this conversation's `workspace/backgroundState` jobs.
- `agentInstance` delivers the message to that existing agent over omp's IRC (`/pomp-dm`).
- At most one of `team`/`agent`/`agentInstance` may be set.
- An unknown target ends the turn with `send_failed` (docs/architecture.md §8.3).
- `main: true` makes the conversation answer itself, without a team conversation's team routing.
- Uses of `main: true`: page actions such as work plan continue, code-review discuss and editor menu auto-send; retries of such answers.
- Editor menu requests belong to the Default (POMP) tab's conversation: from an agent or team tab the page switches there first and the request waits in the input.
- `main` combined with another target ends the turn with `send_failed`.
- Without a target, the current conversation's partner applies ("Conversation tabs" below): an agent conversation gets the message as typed, a team conversation through `/pomp-team`.

#### `workspace/countDraftTokens`

- This is the basket's **Count tokens** button.
- The host counts one text: the attachment list as a send writes it, the draft, and the conversation's knowledge block
  (ticked defaults and picks, as the extension's `<pomp_knowledge>` block renders it).
- The host asks a hidden POMP extension command, which counts with the engine's local tokenizer for the current model; no provider request is made.
- Over 128,000 characters, or while a reply runs, the host answers with a characters-based estimate.
- Answered by `workspace/tokenCount`:

```
{ requestId, tokens, estimated,
  method: "catalog-native"|"fallback-native"|"byte-upperbound"|"byte-estimate"|"empty",
  encoding?, error? }
```

- `estimated: true` means there is no exact tokenizer for the model, or the tokenizer is unavailable.
- An estimated value is shown with ≈.

#### `workspace/countBasketTokens`

- It covers what the next message adds beyond its text: the editor context and the attachment list.
- The editor context is gathered exactly as a send would, when it is on.
- The result is a characters-based estimate: ASCII counts four per token, other characters one each.
- It is not the model's tokenizer.
- Answered by `workspace/basketTokens` `{ requestId, ok, tokens, editorContext, attachments, error? }`.
- The meter shows it as a hatched "basket" segment after the filled part.
- The page sends it, debounced, when the attachments or the editor-context switch change.
- The page also sends it after each new breakdown.

#### `workspace/findFiles`

- Matching is a case-insensitive substring of the workspace-relative path.
- An empty query lists open documents first.
- `limit` is 1..100, default 30.
- Answered by `workspace/files`.

#### `workspace/pickFiles`

- The dialog starts in the workspace root.
- Answered by `workspace/files`.
- `files` is empty when the user closed the dialog.

#### `chat/stop`

- Pending omp approvals are released (denied) first.

#### `permission/mode`

- The host saves the option Tools > Options > Pomp > General > Agent > Approval mode.
- When the mode changed, the host restarts omp with the new `--approval-mode`.
- Refused while a reply is in progress.
- Refused when the host cannot write settings.
- Answered by `permission/modeResult`.
- The host's own gate applies the mode at once.
- `write` answers file, project, breakpoint and bookmark changes of VS tools without asking.
- Under `write`, process runs, tests, debugger, Git and package changes still ask.
- `yolo` answers everything except omp's forced prompts: a `Reason:` line and provider safety checks.
- omp's computer and browser helpers always ask, also under `yolo`.

#### `permission/respond`

- Exactly one answer per request.
- With `canRemember: false` the host treats `allowForSession`/`alwaysAllow` as `allow`.
- `note` reaches the model only for host tools.

#### `dialog/respond`

- `select`/`input`/`editor` answer with `value`.
- `confirm` answers with `confirmed`.
- Closing answers with `cancelled: true`.

#### `session/fast`

- Runs omp `set_fast_mode`: the priority service tier of the current model's family, omp's `/fast`.
- Not queued behind chat commands.
- At most 4 in flight.

#### `session/new`

- The page keeps its transcript and disables sending until `session/newResult`.

#### `workspace/trust`

- `refresh` re-scans.
- `trust` records the `fingerprint` the page showed for `root` and starts omp.
- `trust` is refused with `error` and the current list when the files changed meanwhile or the workspace changed.
- `open` opens one listed `path` in the VS editor.
- Not queued behind chat commands.
- Answered by `workspace/trustState`.

#### `agents/command`

Payload:

```
{ requestId,
  action: "refresh"|"saveRole"|"deleteRole"|"saveTeam"|"deleteTeam"|"transcript"
        |"exportTeam"|"openTeamExport"|"previewTeamImport"|"importTeam"|"openRole"|"builtinRole",
  scope?, role?, team?, name?, expectedHash?, subagentId?, sessionId?, text?, previewHash? }
```

- Answered by `agents/reply`.
- See "Agents and teams".

#### `computer/set`

- The host runs omp's `/computer on|off`, with no model request.
- It runs in order with the chat commands.
- Switching on is refused during a reply.
- Answered by `computer/reply`.
- The page handles a typed `/computer` itself.
- Variants such as `/computer:<suffix>` or ones with unusual whitespace that reach the host are intercepted and refused (`send_failed`).

#### `workspace/agentPlan`

- The user's edit of the agent work plan (`session/info.todoPhases`).
- Sent to omp as `set_todos` without a model request.
- Answered by `workspace/agentPlanResult` `{ requestId, ok, error? }`.
- See "Agent work plan editing".

#### `workspace/agentPlanPage`

- Returns one page of the WHOLE plan, with any number of tasks.
- Answered by `workspace/agentPlanPageResult`:

```
{ requestId, ok, error?, revision, editable, page, size, total,
  phases: [{ index, name, taskCount }],
  rows: [{ phase, task, content, status, blocker? }] }
```

- `phase`/`task` index the whole plan.
- `query` filters task content, blocker and phase name.
- `total` counts the matches.
- `page` is clamped.

#### `workspace/agentPlanEdit`

Payload:

```
{ requestId, sessionId, revision,
  op: "addTask"|"updateTask"|"deleteTask"|"addPhase"|"renamePhase"|"deletePhase",
  phase?, task?, name?, content?, status?, blocker? }
```

- Refused unless `revision` is the plan omp holds now.
- Answered by `workspace/agentPlanResult`.
- See "Agent work plan editing".

### Agent work plan editing

**Guards**

- The host handles `workspace/agentPlan` in order with the chat commands.
- So no prompt starts between its checks and the edit.
- Refused while any of these is active: a turn, an engine run, background work, a host tool, an approval/question, an unpaused queue item.
- Refused while the conversation is recovering or switching.
- Refused when `sessionId` is not the current conversation.

**`base`**

- `base` must equal the plan omp holds now, exactly.
- The host re-reads it with `get_state` just before the edit.
- Otherwise the edit is refused and nothing changes.
- These plans are not editable through `workspace/agentPlan`:
  - a plan beyond the display limits: 20 phases, 200 tasks, names ≤ 200, content ≤ 600, blocker ≤ 300 characters;
  - a plan with control characters;
  - a plan with an unknown status.

**`phases`**

- Statuses: `pending`, `in_progress`, `completed`, `blocked`, `abandoned`.
- Name and content must be non-blank and one line.
- Each phase needs at least one task.
- An empty blocker is dropped.
- omp (18.4.3 through 18.12.1) does not validate `set_todos`, so the host does.

**Paged editing in the Work tab**

- The Work tab shows the whole plan page by page.
- `session/info` carries `planTaskCount`, `planOpenCount` and `planRevision` next to `todoPhases`.
- `planRevision` is a hash of the whole plan.
- `todoPhases` holds the first 20 phases / 200 tasks.
- The page asks `workspace/agentPlanPage` again whenever the revision changes.
- Each row is edited or deleted with `workspace/agentPlanEdit`, under the same guards as above.
- Tasks and phases are added, and phases renamed or deleted, the same way.
- There is no limit on the number of phases or tasks.
- Text limits still apply: names ≤ 200, content ≤ 600, blocker ≤ 300 characters.
- Only a plan whose `set_todos` frame would exceed about 1 MiB is refused.
- Deleting the last task of a phase removes the phase.
- An empty plan takes edits without a revision.

**Persistence**

- omp keeps the edit in memory only.
- The model's next todo update builds on it and records it in the conversation.
- An engine restart, session switch or context compaction re-reads the last plan recorded in the conversation.
- When unfinished items remain, omp may ask the model to continue at the end of the next run.
- The page shows these limits in the editor.

## Conversation tabs, command queue and agent run history

- All three state messages below are included in every `ui/ready` snapshot.
- They are republished on change.
- Commands are ordered with chat sends on the host's serial command queue.

### Conversation tabs

Every chat tab is its own omp conversation: the Default (POMP) tab's, and one per open agent or team tab.

| type | direction | payload |
|---|---|---|
| `conversation/tab` | UI → host | `{ requestId, action: "open"\|"activate"\|"close", partner?: { kind: "agent"\|"team", id }, sessionId? }` |
| `conversation/tabReply` | host → UI | `{ requestId, ok, error? }`: only for `close` of a tab that is not the current conversation. |
| `conversation/tabsState` | host → UI | `{ sessionId?, partner: { kind, id? }, title?, message?, main?, tabs: [{ sessionId, partner, title? }] }` |

**Actions**

- `open` with `partner`: switches to that partner's open tab, or starts a new, empty conversation for it (`new_session`) and opens its tab. A partner never has two open tabs.
- `activate` with `sessionId`: switches to that open tab's conversation. Without `sessionId`: the Default tab's conversation (`main`), or a new POMP conversation when it has none.
- `close` with `sessionId`: closes the tab; the conversation stays in the conversation list. Closing the current tab then activates the Default tab.
- A tab whose conversation the engine never wrote (no message was sent) starts a new conversation for its partner instead of switching.
- `open`, `activate` and `close` of the current tab change the conversation: they answer with exactly one `session/newResult { requestId, status, message? }` and replay the transcript like a `session/history` switch. They are refused while a reply, approval, question or background work runs.

**State**

- `sessionId`/`partner`/`title`: the current conversation and its partner (`main` for a POMP conversation).
- `main`: the Default tab's conversation id, or `null`.
- `tabs`: the open agent and team tabs in order. The page also shows the current conversation as a tab when its tab was closed.
- `message`: a team conversation's note that its previous members are gone.

**Routing**

- With no explicit `team`, `agent`, `agentInstance` or `main`, `chat/send` uses the current conversation's partner.
- An agent conversation sends the message as typed: the engine extension (`extensions/partner.ts`) gives that conversation the role's instructions, model, thinking level and tools. No `/pomp-agent` and no subagent.
- A team conversation sends every message with `/pomp-team`, as before, inside its own conversation.
- A one-message target (the composer's send-target chip) overrides the partner for that message.

**Persistence**

- `<workspace>\.pomp\local\chat-tabs.json`, written atomically: `{ version: 1, main, conversations: [{ sessionId, partner: { kind, id }, open }] }`.
- A conversation that is not listed is a POMP conversation. The engine extension reads the partners from this file.
- There is no entry count limit. Past 1 MiB, closed conversations listed first are removed first. Damaged files reset with a log warning.
- Session-list `entries[].partner` is `{ kind, id }` for an agent or team conversation, or `null` for a POMP conversation.
- The earlier `.pomp/local/partners.json` is not read any more: those conversations are POMP conversations.

### Host command queue

| type | direction | payload |
|---|---|---|
| `chat/queued` | host → UI | `{ clientMessageId, itemId }` |
| `chat/queueState` | host → UI | see below |
| `chat/queue` | UI → host | `{ requestId, action: "edit"\|"remove"\|"move"\|"clear"\|"pause"\|"resume", id?, text?, index? }` |
| `chat/queueReply` | host → UI | Reply to `chat/queue`. |

`chat/queueState` payload:

```
{ sessionId?,
  items: [{ id, text, attachments?, includeEditorContext, team?, agent?, agentInstance?, createdAt }],
  paused, reason? }
```

**Messages**

- `chat/queued` acknowledges a queued submission, not a started turn.
- `chat/queueState.sessionId` is the original conversation owning the queue; it is null when the queue is empty.
- The page can compare it with `session/info.sessionId` to disable resume in another conversation.
- The host also refuses that resume.
- `move` uses a zero-based final index.
- `edit` changes only typed text.
- `clear` removes all items but does not implicitly resume a paused queue.

**Scope and dispatch**

- The queue belongs to this VS window and is in memory only.
- It retains typed text, attachments, editor-context choice and captured routing target.
- Agent-partner continuation is resolved at dispatch.
- So successive queued messages can continue the remembered instance.
- Normal completion dispatches the first item through normal `chat/send` handling, with a fresh client message id.

**Pausing**

- Error, abort, Stop, session change or engine restart pauses retained items.
- Retained items never resume automatically.
- The user must resume in the original conversation or clear the items.
- If the queue is empty, the first fresh submission starts an unpaused queue.
- A submission received before Stop remains paused.
- The host never supplies omp `streamingBehavior` (`steer`/`followUp`).

**Limits**

- There is no item count limit.
- Each text must fit the normal prompt frame limit.
- The complete queue state has a 1,800 KiB UTF-8 budget, to fit the outbound envelope.
- Rejection is a host `error` with `code: "queue_rejected"`, `retryable: false`.
- On rejection no turn starts.

### Agent run history

| type | direction | payload |
|---|---|---|
| `agents/historyState` | host → UI | see below |
| `agents/history` | UI → host | see below |
| `agents/historyReply` | host → UI | Reply to `agents/history`. |

`agents/historyState` payload:

```
{ entries: [{ id, sessionId, promptId?,
              target: { kind: "team"|"agent"|"dm", id, title },
              text, startedAt, endedAt?,
              status: "running"|"completed"|"aborted"|"error",
              pinned, label?,
              members: [{ agent, jobId, status, toolCount,
                          tokens: number | null, durationMs: number | null,
                          result: string | null }] }],
  truncated }
```

- `result` is the member's final submitted result, at most 8,000 characters.
- Usage is null when the engine reported none.

`agents/history` payload:

```
{ requestId, action: "refresh"|"delete"|"clear"|"pin"|"unpin"|"label"|"rerun",
  id?, label?, filter?: { kind?, id?, status?, includePinned? } }
```

**Actions**

- `refresh` republishes state and replies ok.
- `label` is at most 80 characters, without control characters.
- `clear` applies all supplied filters.
- `clear` retains pinned entries unless `includePinned: true`.
- An explicit `delete` can remove a pinned entry.

**Rerun**

- Rerun uses the recorded typed text and exact target.
- It uses no editor context and no attachments.
- It sends immediately when idle.
- It enters the host queue when busy or when retained items exist.
- An empty queue's stale pause does not delay an idle rerun.
- A vanished direct-message target remains a direct-message target.
- It then fails normal validation rather than being silently redirected.

**`promptId`**

- `promptId` links a run entry to `chat/turnStarted.promptId`.
- Each `workspace/backgroundState` job also includes `promptId`; it is null without an open turn.
- The host assigns it once, when it first observes that job, and keeps it through progress/end.
- Engine-provided associations are not trusted.
- These ids are at most 128 characters, without control characters.
- They are reset with the engine/session job identity.

**What is stored**

- Team, agent and direct-message turns are recorded.
- Retained: typed text (at most 4,000 characters), target metadata, timestamps/status.
- Retained: allowlisted metrics for jobs started during the turn.
- Job progress/end updates those metrics.
- Not stored: composed prompts, editor context, attachment paths, tool payloads, file contents.

**Storage**

- Storage is the workspace's `.pomp/local/agent-history.json`, written atomically.
  The former `<DataDirectory>/agent-history/<workspace hash>.json` is moved there on first use.
- There is no entry limit.
- The file keeps its newest entries while it fits one UI message (1.75 MiB).
- Oldest unpinned entries are removed first, then oldest pinned entries if necessary.
- Damaged files reset with a log warning.
- `truncated` indicates that the size bound removed entries.

## Tools, skills and native MCP management

**Tools tab layout**

- The Tools tab has the sub-screens **대화 설정 · 권한 · 스킬 · MCP · 규칙 · 워크플로 · 지식**.
  These are conversation settings, permissions, skills, MCP, rules, workflows and knowledge.
- Conversation settings hold the tools to use and the screen-input control.
- A skill row's **채팅에서 사용** action puts the command into the chat input. It does not send it.
- Rules are ordered **지금 적용 중 → 규칙**. The first section holds the rules that apply to the next prompt.
- MCP is ordered **MCP 서버 → 추가할 수 있는 서버**. The second section is the catalog.
- The code search index handles lexical refresh automatically.
- It also rebuilds or stops the vector index automatically when the local embedding option changes.
- Sub-screen chips show the item count. A count of 0 is dimmed but still clickable.
- If the user has not chosen a sub-screen and the current one is empty, the page goes to the first sub-screen with items.
- The selection is kept for the current conversation.
- Lists are tables with column names: switches, badges, row-end icons, search and pages of 20.
- An empty list shows only the column names and an empty row. It has no guidance text.
- The create button in the card header stays visible even when the list is empty.
- The refresh action of file-based lists and of the applied-change history also stays when empty.

**State and command rules**

- `tools/state`, `skills/state` and `mcp/state` are state messages, not transcript events.
- They are included in every `ui/ready` snapshot before transcript replay.
- They are republished on lifecycle and resource changes.
- Each command receives its matching reply with the original `requestId`.
- Commands use the host serial queue shared with chat submissions.
- Tool selection is refused while any of these is active: a reply, an inline request, a pending approval or question,
  or an uncertain session switch.
- Skill and MCP changes are refused in the same situations. Metadata refresh is still allowed.
- A change that arrives while busy is refused. It does not wait for idle and then apply silently.

### Main-conversation tools

| type | payload | meaning |
| --- | --- | --- |
| `tools/state` | see below | Registered tools of the main conversation and their selection. |
| `tools/command` | `{ requestId, action, active? }` | Refresh, set or reset the active tool selection. |
| `tools/reply` | `{ requestId, ok, error? }` | Result of one `tools/command`. |

#### `tools/state`

```
{ sessionId?,
  tools: [{ name, group: "files"|"edit"|"exec"|"web"|"agents"|"host"|"mcp"|"other",
            server?, description?, active, defaultActive }],
  pending, available, message? }
```

- At most 300 registered tools are listed.
- Tools are grouped and sorted by server and name.
- Descriptions are bounded to 200 characters.

#### `tools/command`

- `action` is `"refresh"`, `"set"` or `"reset"`. `active` is a `string[]`.
- `set` replaces the visible selection.
- Active registered tools beyond the 300-row catalog remain unchanged by `set`.

**Engine side: `/pomp-tools`**

- The POMP extension's local command is `/pomp-tools list|set <json-array>|reset`.
- It uses stock `getAllTools`, `getActiveTools` and awaited `setActiveTools`.
- No inference or user turn is started.
- The host confirms the extension command exists before sending it.
- Missing support reports `available: false`. It never becomes a model prompt.
- Results use a reserved base64 notification prefix and a host-generated correlation id.
- Results are bound to the originating engine connection and session.
- Malformed, stale and management notifications are consumed. They are not displayed as chat notices.

**Defaults and lifetime**

- The tools active at startup are the defaults. This includes active host RPC tools registered later.
- New or switched conversations and engine restarts restore the defaults.
- Selections are process-local. They are not persisted per saved session.
- Disconnect clears the available catalog.
- This changes the main conversation only. Subagent definitions and tool approval/policy rules stay intact.

**Grouping**

- Host tools are grouped by the actual host registry.
- MCP grouping preserves the actual engine names and uses configured server names when possible.
- Stock canonical MCP names use `mcp__<sanitized-server>_<tool>`.
- Double-underscore aliases are recognized defensively.
- Grouping never rewrites a registered tool name.

**Computer-control guard**

- Computer-control permission is validated by `extensions/computer-guard.ts`.
- The guard reads the current session's actual computer/browser enabled settings
  through `SettingsManager.create(ctx.cwd).rawValue(...)`.
- For an enabled capability, `computer-state.json` must hold a POMP allowance for the same `sessionId` and that capability.
  Without it, `eval` is blocked.
- Dynamic code and the prelude are blocked at the outer `eval` as well.
- Direct `computer` and `browser` calls need that capability's allowance for the same session.
  They also need the main session.
- If the settings API cannot be read, `eval` is blocked.
- If the state file cannot be read, `eval` and direct control calls are blocked for enabled capabilities.
- Subagents cannot use the screen or the browser.
- A subagent's `eval` is also blocked when a control allowance exists or the state file cannot be read.
- Environment variables alone grant nothing.
- POMP intercepts every command-boundary spelling of `/computer` and never passes it to the engine directly.
  This includes colons, unusual whitespace and FormKC normalization.
- The guard relies on the engine's **session-scoped settings API**.
  Every engine upgrade therefore re-verifies the actual settings resolution and permission bypass.

**Screen & input control note**

- While `computer-state.json` holds `desktop: true` for the current `sessionId`, the same module appends one English section to the main agent's system prompt.
  Its heading is `## Screen & input control (pomp)`.
- The section says that screen & input control is enabled for this conversation and that the model uses the Eval tool's `computer` prelude (docs: `xd://eval/computer`).
  It forbids launching or using external programs or CLIs (such as Orca) for GUI control.
- It is decided in `before_agent_start` on every turn from the state file. When the switch is off, there is no section.
- Subagents never get it.

### Skills

| type | payload | meaning |
| --- | --- | --- |
| `skills/state` | `{ skills, diagnostics }` | Skill rows from every source plus scan diagnostics. |
| `skills/command` | see below | Manage, read, edit or switch one skill. |
| `skills/reply` | `{ requestId, ok, error?, document?, skillImport? }` | Result of one `skills/command`. |

#### `skills/state`

```
{ skills: [{ name, description, source: "project"|"user"|"omp"|"installed"|"pomp",
             file?, hash?, invocable, note?, unreadable?, enabled?, catalogId?,
             catalogUpdate?, defaultEnabled?, category? }],
  diagnostics: string[] }
```

- At most 100 project rows and 100 user rows are listed.
- Other rows are capped separately, so they never push project or user rows out.
- At most 20 bounded diagnostics are sent.
- `invocable` means the live engine advertises `/skill:<name>`.
- `pomp` rows are the skills built into POMP. See "Skill switches, MCP headers and the POMP catalog".
- `pomp` rows are read-only and are listed even before the engine reports them.
- `defaultEnabled` is the state POMP ships the skill with.
- `category` is the catalog group key.

#### `skills/command`

```
{ requestId,
  action: "refresh"|"create"|"pick"|"import"|"delete"|"open"|"read"|"update"|"enable"|"disable"|"reset",
  scope?: "project"|"user", name?, description?, body?, content?, expectedHash?, ticket? }
```

- `read` and `update` are described under "In-page resource editing".
- `enable`, `disable` and `reset` are described under "Skill switches, MCP headers and the POMP catalog".
- A `read` without scope serves a `pomp` row too.
- The page copies a built-in skill with `read` followed by `create`.

**`pick` and `import`**

The Import button imports a skill file from disk. It never takes pasted text.

1. `pick` shows the host's open-file dialog, filtered to `SKILL.md` and `*.md`.
   - The reply carries `skillImport: { ticket, name, description, file, files, bytes }`.
   - Without `skillImport` the user closed the dialog.
   - The dialog runs outside the command queue. The page waits up to 10 minutes for the reply.
2. The page shows that preview with a scope choice.
3. `import` sends `scope` and `ticket`. The page never names a path.

What a chosen file brings:

- A file named `SKILL.md` brings its whole folder, such as `references/` and `scripts/`.
  - `.git` folders are skipped.
  - At most 500 files, 32 MiB and 6 folder levels. A bigger folder is refused.
- Any other `.md` file is copied alone as `SKILL.md`.

Name and checks:

- The name is the frontmatter `name`, else the folder name (`SKILL.md`) or the file name.
- It must match `^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$`.
- The `SKILL.md` text must be UTF-8 of at most 32 KiB with a nonempty body.
- A readable frontmatter needs a nonempty `description`.
- A frontmatter POMP's simple reader cannot read (folded text, lists) is still imported.
  The engine reads full YAML. The row then offers open and delete only.

Writing:

- `import` reads the file again. When the name changed since `pick`, it refuses and asks for a new pick.
- It refuses when the scope already has a skill or folder with that name.
- The copy goes into a hidden staging folder in `<scope>/.pomp/skills/`, which is then moved to `<name>/` whole.
- Linked/reparse entries are refused. Nothing is executed and no permission is granted.

**Engine rediscovery**

- `refresh`, `create`, `import`, `update` and `delete` send omp's `/reload-plugins` after the list refresh.
  The engine then rediscovers its skills and file commands without a restart or a model request.
- The reply waits for that reload. The page waits up to 60 seconds for any skills reply.
- Its `Plugins reloaded.` output is not shown as a chat notice.
- While a reply or request runs, the reload waits for the end of that reply.
- At the end of every reply the host compares the `SKILL.md` files of both scopes (path, size, write time) with what the engine last saw.
  On a change it refreshes the list and reloads the engine. A skill the model wrote is usable right after its reply.
- The model's system prompt names the two skill folders and the file format (`features-guidance`).
- The built-in `chat-style` skill is on by default. While the engine lists it, the `reply-style` module appends its instructions to the main agent's system prompt, because the model does not pick a formatting skill by its description. A user's own `.pomp/skills/chat-style/SKILL.md` (project, then home) wins over the built-in file.

**`create`, `delete`, `open`**

- `create` requires a name matching `^[a-z0-9-]{2,40}$`.
- `create` requires a single-line description of 1–500 characters.
- `create` uses `CreateNew` for `<scope>/.pomp/skills/<name>/SKILL.md`.
- `create` never overwrites. The new file is edited in the page; only `open` opens the VS editor.
- `delete` resolves one unambiguous POMP-scope row and requires its current hash.
- `delete` removes the containing directory only if it is empty.
- `open` resolves the catalog identity. It does not accept a page-supplied path.
- Linked/reparse paths are refused.
- Disconnect clears invocation availability.

**Discovery directories**

- The generated overlay supplies `skills.customDirectories` in this precedence order:
  1. `<workspace>/.pomp/skills`
  2. `<home>/.pomp/skills`
  3. the user's own directories, then the workspace's own directories
  4. POMP's bundled catalog skills, last
- The overlay also writes `skills.enableAgentsUser`, `enableAgentsProject`, `enableClaudeUser`, `enableClaudeProject` and `enableCodexUser` as `false`.
  The engine therefore loads no skills from other programs' folders: `~/.agents`, `<workspace>/.agents` (and `.agent`), `~/.claude`, `<workspace>/.claude` and `~/.codex`.
  Their skills never reach the engine's command list, so they are not rows either.
- The engine's own folders stay on: `~/.omp/agent/skills` and `<workspace>/.omp/skills` (omp's `enablePiUser`/`enablePiProject` keys, not written).
- Stock native discovery reads only one-level `*/SKILL.md` files that have a description.
- Native described one-level POMP skills are omitted from the legacy resource injection and list.
- Deeper or undescribed skills remain in `/resources`.
- They can be loaded explicitly through `/skill`. The commands are registered as `pomp-resources` and `pomp-skill`.
- The legacy explicit loader stays available.

**Host scan limits**

- The host scans POMP skills at depth ≤3.
- The budget is 2,000 entries per scope. One broken entry does not end the scan of its folder.
- Files are at most 32 KiB.
- Bodies must be nonempty and at most 24,000 characters.
- The host conservatively accepts single-line `name` and `description` scalars.
- Descriptions are at most 500 characters.
- Complex YAML metadata is diagnosed.
  Such a skill can still appear as engine-only metadata when the engine reports it.
- Project custom-directory entries take precedence over user entries with the same name.
- Both take precedence over a built-in skill with the same name.
- A user's own copy wins: the built-in row stays hidden while the copy is listed.

**Row sources**

- Native command metadata contains no file path. It has `source: "skill"` and `name: "skill:<name>"`.
- Engine-only rows therefore do not invent a VS-open target.
- Local POMP rows include a verified file and an exact-byte hash.
- Installed VSIX rows reuse cached metadata-only discovery and verified package paths.
- Installed VSIX rows are read-only and non-invocable.
- The model reads installed skills with the `installed_skills` host tool.
- No installed skill body is loaded for the catalog.
- Row `note` values and state `message` values carry a state or a reason only.
- They never carry instructions or shortcut text. The page shows no hint text.

### Rule files

| type | payload | meaning |
| --- | --- | --- |
| `rules/state` | `{ rules, diagnostics }` | Rule files of the project and user scopes. |
| `rules/active` | `{ sessionId?, rules }` | Host → UI. Rules that apply to the next prompt. |
| `rules/command` | see below | Manage, read or edit one rule file. |
| `rules/reply` | `{ requestId, ok, error?, document? }` | Result of one `rules/command`. |

#### `rules/state`

```
{ rules: [{ name, scope: "project"|"user", file, hash, description,
            alwaysApply, globs: string[], note?, unreadable? }],
  diagnostics: string[] }
```

- Lists top-level `<workspace>/.pomp/rules/*.mdc` and `~/.pomp/rules/*.mdc`.
- There is no file count limit. Each file is at most 32 KiB.
- A list too large for one message keeps its front and counts the rest in `diagnostics`.
- `note` marks frontmatter that only the engine extension can interpret.
- The engine extension remains authoritative: `lib/project-rules.ts` parses the YAML and decides what applies.
- Included in the `ui/ready` snapshot and refreshed with the other resources.

#### `rules/active`

```
{ sessionId?, rules: [{ name, file, always, globs: string[] }] }
```

- The rules that apply to the next prompt for the active document.
- The engine extension decides them in `lib/project-rules.ts`.
- Posted when the active document, the rules or the conversation change.
- Also included in the `ui/ready` snapshot.
- Shown in Tools › 규칙 › 지금 적용 중.

#### `rules/command`

```
{ requestId, action: "refresh"|"create"|"import"|"open"|"delete"|"read"|"update",
  scope?, name?, description?, alwaysApply?, globs?, body?, content?, expectedHash? }
```

- Names are 2–40 lowercase letters, digits and hyphens.
- `create` writes JSON-quoted frontmatter.
- Its description is at most 500 single-line characters.
- It has at most 32 single-line globs of at most 200 characters each.
- `import` writes the pasted strict UTF-8 content.
- The content is at most 32 KiB, has closed frontmatter and a non-empty body.
- `create` and `import` write a sibling temporary file and move it into place without overwriting.
- `create` and `import` then open the file in the VS editor.
- `delete` requires the listed `hash`.
- Nothing runs a model. A rule never grants tool permissions.

### In-page resource editing

Every item a user writes is created, edited and deleted in the page.
This covers skills, rules, saved workflows, project hooks and the project policy.
The file stays the source of truth. "Open in VS" is a secondary action.

**Common rules**

- New names match `^[a-z0-9-]{2,40}$`.
- An existing file the list shows keeps its own name and stays editable and deletable.
- Create never overwrites. It writes a sibling temporary file and moves it.
- Update and delete name the hash the page last saw.
- They are refused when the file changed since. Refresh, then retry.
- Linked/reparse paths are refused.
- Nothing here runs a model or grants a permission.
- These commands come only from the page, never from a model tool.

**Size limits**

- Every write is refused when the file it would write exceeds the limit its reader applies.
- The limit is counted in UTF-8 bytes. A Korean character is 3 bytes.
- Skills, rules and workflows: 32,768 bytes. Hooks: 65,536 bytes.
- The page blocks such a save with a short "too large" error. It shows no byte counter.

**Unreadable rows**

- A row has `unreadable: true` when the file is over the limit or not UTF-8, for example written by another program.
- For skills, it also covers a `SKILL.md` that does not parse or repeats a taken name.
- Such a skill is named by its folder.
  If that is taken too, it is named `<relative path>/SKILL.md`. Pass the name back unchanged.
- An unreadable row carries a `note` and a streamed whole-file hash.
- The page offers only "open in VS" and delete. `read` and `update` refuse with the note.
- Above 16 MiB the hash is empty: `""`, or `null` for skills. Such a row can only be opened.

**Caps**

- Lists show every file a create can produce.
- Caps: skills 100 per scope, rules 100 per scope, hooks 20. Workflows have no cap.
- A scope at its cap refuses create and import with a message.
- Files beyond a cap are counted in `diagnostics`.

#### Skills and rules

- `create` takes an optional `body`. An empty body gives the starter body, as before.
- `create` and `import` no longer open the VS editor.
- `read { scope?, name }` answers `reply.document = { content, hash, form? }` for one listed row.
- For an installed row the scope is omitted. Engine-only rows have no file.
- `form` is present only when every frontmatter key is one the form edits.
- Form fields for skills: `{ description, body }`.
- Form fields for rules: `{ description, alwaysApply, globs, body }`.
- Otherwise the page edits `content` as text.
- `update { scope, name, expectedHash }` carries either the form fields or `content`.
- With form fields, the host writes the frontmatter as `create` does.
- `content` is validated as `import` is: same limits, and a skill's declared name must stay the same.
- The page copies a built-in skill by `read` and `create` with the body.

#### Workflows

| type | payload | meaning |
| --- | --- | --- |
| `workflows/state` | `{ workflows, diagnostics }` | Saved workflow files of both scopes. |
| `workflows/command` | see below | Manage, edit or fetch one workflow. |
| `workflows/reply` | `{ workflow }` for `get` | Result of one `workflows/command`. |

```
workflows/state:   { workflows: [{ name, scope, file, hash, description, stepCount,
                                   task?, steps?, summary?, note?, unreadable? }],
                     diagnostics }
workflows/command: { requestId, action: "refresh"|"create"|"update"|"delete"|"open"|"get",
                     scope?, name?, workflow?: { description, task, steps }, expectedHash? }
```

- `workflows/state` lists every file.
- Only when the summaries alone would pass 1.75 MiB are trailing ones left out and counted in `diagnostics`.
- `task` and `steps` ride along while the message stays under 1 MiB.
- Later entries are sent with `summary: true`.
- `get { scope, name }` answers `workflows/reply { workflow }` with the full file.
  The page uses it when a workflow is started or edited.
- Files are `<workspace>/.pomp/workflows/<name>.json` and `~/.pomp/workflows/<name>.json`, each ≤ 32 KiB.
- Starting a workflow is `workspace/composer` `start`, described below. The page then sends the chat message.

| file key | rule |
| --- | --- |
| `version` | `1` |
| `description` | At most 500 single-line characters. |
| `task` | 1–16,000 characters. |
| `steps` | Array of at most 32 single-line steps of 1–500 characters each. |

- Any other key or shape gives a `note`. Such a row allows open and delete only.

#### Project hooks

| type | payload | meaning |
| --- | --- | --- |
| `hooks/state` | `{ hooks, diagnostics, policy }` | Project hook files as ProjectHooks parses them. |
| `hooks/command` | see below | Manage, edit or switch one hook. |
| `hooks/reply` | | Result of one `hooks/command`. |

```
hooks/state:   { hooks: [{ id, name, file, hash, description, event: "onFileSave"|"onBuild"|"",
                           glob, timeoutSeconds, enabled, script, note?, unreadable? }],
                 diagnostics, policy: "none"|"valid"|"invalid" }
hooks/command: { requestId, action: "refresh"|"create"|"update"|"setEnabled"|"delete"|"open",
                 id?, name?, hook?: { description, event, glob, timeoutSeconds, enabled, script },
                 enabled?, expectedHash? }
```

- Lists the top-level `<workspace>/.pomp/hooks/*.md` files, first 20.
- `id` is the file name without `.md`.
- A file that does not parse is listed with `note`. It allows open and delete only.
- The host writes `---` scalar frontmatter: `name`, `description`, `event`, `enabled`, `glob`, `timeoutSeconds`.
- The script is written in one `` ```powershell `` fence.
- The host re-parses the result with `ProjectHook.Parse` before writing.
- Running a hook is unchanged.
  It needs the conversation switch, a valid project policy and a one-time approval per run.

#### Project policy and persistent permissions

**`permission/rules` `savePolicy`**

```
savePolicy { policy: { requireReviewedEdits, requireSandbox,
                       rules: [{ tool, effect, paths?, commands? }] },
             expectedHash }
```

- Writes `.pomp/permissions.json` with `version: 1` and `default: "deny"`.
- It applies the same validation as ProjectExecutionPolicy first.
- `expectedHash: null` means the file must not exist yet.
- `deletePolicy { expectedHash }` removes the file.
- `projectPolicy.hash` is the hash of the file's bytes whenever the file exists.
- The policy is read for every tool check, so a save applies to the next call.
- The page sends the hash of the file its draft or delete confirmation was made from, never a newer one.
- When any reply shows a different file, the draft turns stale. It can then only be reloaded from the current file.

**`permission/rules` persistent keys and trusted workspaces**

- `persistent` keys are `<host|omp>:<tool>@<workspace>`.
- In the workspace path, `%`, `;`, `,`, space and control characters are escaped as `%XX`.
- A key without `@<workspace>` is from an older version and no longer answers anything.
- `trustedWorkspaces: [{ root, trustedUtc, files }]` lists the trusted workspaces.
- `untrust { workspace }` forgets one. omp restarts when it is the current workspace.

#### Starting a workflow and opening role files

- `workspace/composer` `start { root, sessionId, id, revision, task, steps }` begins a new run from a saved workflow.
- It works only when no run is active.
- It uses the same guards as a new saved task, but the run is not paused.
- The run starts with discovery when steps are given, otherwise with planning.
- It sends no model request.
- `agents/command` `openRole { scope, name }` opens a project or user role file in the VS editor.
  It is used for a role file the form cannot edit.

#### Applied changes, terminal jobs and review runs

- Applied changes are listed a page at a time.
- `workspace/history` `refresh { page, size, query? }` answers `workspace/historyState`.
- `workspace/historyState` is `{ root, entries, total, page, size, query, message }`.
- Entries are newest first. `query` matches file paths.
- The last view is kept for later pushes.
- Records are no longer limited to the newest 20. Only the 500 MB checkpoint size limit removes the oldest.
- `delete { ids }` removes finished records.
- A prepared record is refused unless it was last written over an hour ago, which means an interrupted apply.
- `restore { ids }` stages one combined undo proposal.
- `restore` refuses records that changed the same file, or more than 10 files.
- The single `id` form still works.
- Edit proposals from an inline edit take the document's line ending, so a CRLF file never gets LF lines and only real changes show as hunks.
- `workspace/terminal` `clear` drops finished jobs. `cancelAll` cancels unfinished ones.
- `workspace/codeReview` `deleteRun { runId }` removes one previous run.

### Native MCP files

| type | payload | meaning |
| --- | --- | --- |
| `mcp/state` | `{ servers, restartRequired, message? }` | Native writable MCP server definitions. |
| `mcp/command` | see below | Manage, switch, test or restart MCP servers. |
| `mcp/reply` | `{ requestId, ok, error?, output? }` | Result of one `mcp/command`. |

```
mcp/state:   { servers: [{ name, scope: "project"|"user", type: "stdio"|"http"|"sse",
                           command?, args?, env?: { [name]: "••••" }, url?,
                           enabled, error?, hash? }],
               restartRequired, message? }
mcp/command: { requestId, action: "refresh"|"save"|"delete"|"enable"|"disable"|"test"|"restart",
               scope?, name?, server?, expectedHash? }
```

**Listing and paths**

- Only native writable project and user definitions are listed.
- There is no count limit.
- A list too large for one message keeps its front and says in `message` how many it left out.
- Imported third-party configurations are not edited.
- There is no separate hashes map in state.
- Paths are `<workspace>/.omp/mcp.json` and `<home>/.omp/agent/mcp.json`.
- A named active profile uses `<home>/.omp/profiles/<profile>/agent/mcp.json`.
- `--profile` and `--alias`, including their `=value` forms, take precedence over `OMP_PROFILE` and `PI_PROFILE`.
- An empty profile or `default` selects the default path.
- Home is injectable for host self-tests.

**Secrets**

- Every environment value in state is masked as `••••`.
- Saving the mask preserves that variable's existing actual value.
- A new variable needs a real value.
- Invalid-file and server errors never include raw environment configuration.
- Command, arguments and URL remain ordinary editable fields, not secret fields.

**Save, delete and toggle**

- `save` without `expectedHash` is **create-only**. This includes a missing or empty native file.
- An existing same-scope row refuses it.
- A collision with a relevant user definition or override also refuses it.
- Saving, deleting or toggling an existing row requires the opaque current `row.hash`.
- Stale hashes require a refresh.
- Rename with a previous row hash is unsupported.

**File handling**

- A process-wide lock protects fresh native-file reads before mutation.
- One server or override is edited from current disk data, not from a UI snapshot.
- Unknown top-level keys, unknown server keys and unrelated entries are preserved.
- Opaque project-row hashes cover both the native scope file and the user file, because toggles also clean user overrides.
- Hashes are deliberately conservative.
- Enable removes stale user denylist and allowlist entries and sets the writable definition enabled.
- Disable sets it to false and clears a stale allowlist.
- Native files are at most 1 MiB.
- Duplicate JSON keys and trailing content are rejected.
- Linked/reparse ancestors are refused.
- Writes use unique same-directory temporaries plus atomic replace or move, and clean up.
- Writes are atomic **per file**. They are not a two-file transaction or a cross-process lock.
- A successful user-override cleanup followed by a failed project write is reported as a failure.
  It still sets `restartRequired`. Refresh or retry is required.
- External editors and other VS processes are not serialized by the in-process lock.

**Restart**

- Any successful or partial edit sets `restartRequired`.
- Stock RPC `/mcp reload` cannot reconnect the live session.
- `restart` uses the normal host restart path and must actually reach running.
- A successful new engine start clears the banner.

**Test**

- `test` verifies that the stock builtin MCP command exists.
- It then runs local `/mcp test <name>` without inference.
- Correlated current-engine `command_output` is captured.
- Terminal escapes are removed and the output is bounded to 8 KiB UTF-8.
- The output is returned only in `mcp/reply.output`, never as a chat notice.
- Stock currently reads only the first whitespace token of the name.
- Testing a server name containing spaces is therefore refused, so a different server is never tested by accident.
- A command-level failure or absent output fails the reply.
- Connection success or failure detail remains the stock test's output.

### Skill switches, MCP headers and the POMP catalog

**Skill switches**

- Skills can be switched off without deleting them.
- `skills/state` rows carry `enabled`. Absent means on.
- `skills/command` `enable` and `disable` take `name`. They work on any source, including engine and plugin rows.
- `skills/command` `reset` takes `name` and clears the explicit on/off of that skill, back to its default.
- `reset` on a skill without overrides is idempotent.
- POMP keeps its own per-skill on/off overrides in its data folder.
- POMP writes the engine overlay's `disabledExtensions` as the effective off-set.
- That off-set is merged with the `disabledExtensions` of the user's `~/.omp/agent/config.yml`.
- It is also merged with the workspace's `.omp/config.yml`.
- An overlay array replaces the lower layers, so POMP must merge them.
- The same merge applies to `skills.customDirectories`.
- The engine watches the overlay and applies the change without a restart.

**Built-in catalog skills**

- The catalog skills are built into POMP.
- Every entry of `omp-extension/catalog/catalog.json` with `kind: "skill"` is available out of the box.
- Each is listed in Tools › 스킬 as a `pomp` row with `catalogId`, `defaultEnabled` and `category`.
- The row is listed whether or not the engine has reported the skill yet.
- Only the essentials are on by default. This is `defaultEnabled`; absent means off.
- Built-ins and user skills default to on.
- The engine discovers the catalog skills from the overlay's catalog directory, last in `skills.customDirectories`.
- The engine lists the enabled ones to the model by name and description.
- The model loads them itself — as slash commands and on plain-language asks alike.
- Enabling a skill also switches on everything in its transitive `requires`.
- Disabling never cascades.
- `pomp` rows are built into POMP and read-only. `update` and `delete` refuse.
- The page offers only the switch, use and open for `pomp` rows.
- `catalogUpdate` is deprecated and always false.

**MCP headers**

- MCP rows and `save` take `headers` for http and sse servers, for example `Authorization`.
- Header values are masked like `env`. Saving `••••` keeps the stored value.
- `catalogId` is stored in the server entry as `pompCatalog` and returned on the row.

**Catalog messages**

| type | payload | meaning |
| --- | --- | --- |
| `catalog/state` | `{ entries, message? }` | MCP entries of the bundled catalog. |
| `catalog/command` | `{ requestId, action, id?, scope?, inputs? }` | Refresh the catalog or install one MCP entry. |
| `catalog/reply` | `{ requestId, ok, error? }` | Result of one `catalog/command`. |

```
catalog/state:   { entries: [{ id, kind: "mcp", title, description, category, version,
                               publisher?, license?, scopes, installed, requires,
                               needs: ("network"|"token"|"browser"|"node")[],
                               inputs: [{ key, label, secret, required }] }],
                   message? }
catalog/command: { requestId, action: "refresh"|"install", id?, scope?: "project"|"user", inputs? }
```

**`catalog/state`**

- The catalog is a file bundled with POMP: `omp-extension/catalog/catalog.json`.
- It is part of every `ui/ready` snapshot.
- It lists MCP entries only.
- Each entry pins an exact package version or release and says what it needs.
- Catalog skills live on the skills list instead.
- Every MCP change re-sends `catalog/state`.

**Catalog MCP seeding**

- When resources refresh, the host registers every catalog MCP entry once in the user's native `mcp.json`.
- A seeded entry is switched off, has empty inputs and keeps `pompCatalog`.
- The ids the host registered are kept in `<data>/seeded-mcp.json`.
- A server the user deleted is therefore never registered again.
- A later POMP adds only new entries.
- A server of the user's own with the same name is left alone.
- After seeding, the entry is an ordinary server row: edit, switch, test, delete.

**`install`**

- Writes one create-only MCP server to that scope's native `mcp.json`.
- The server has `enabled: false`, the pinned command or URL and the entered inputs.
- An entry with a secret input is user scope only.
- An optional secret left empty omits that variable and keeps the project scope allowed.
- Switching the server on and the restart stay with the MCP row.
- Installing a skill id is rejected, because skills are built in.
- No install downloads or runs anything.
- An `npx` server fetches its pinned package when the engine starts it.

**Config merge sources**

- For the merge, the host reads only `~/.omp/agent/config.yml` (or the active profile's) and `<workspace>/.omp/config.yml`.
- A missing, unreadable or richer-than-expected file is ignored with a warning. It never fails the start.
- Other project settings files still apply beneath the overlay for keys the overlay does not set.
  These are `.omp/settings.json` and `.claude/settings.json`.

### Preview

| type | payload | meaning |
| --- | --- | --- |
| `preview/open` | `{ path }` | Open a workspace `.svg`, `.html` or `.htm` file in a VS document tab. |

**Path checks**

- The host resolves `path` against the workspace root.
- It refuses anything outside the workspace, any linked/reparse path and files over 2 MiB.
- A refusal arrives as a chat notice.

**Sandbox**

- The tab's WebView2 has these switched off: scripts, web messages, host objects, dev tools, context menus,
  downloads and new windows.
- The file is served from a virtual host with a CSP that allows no script and no network.
- Every navigation away from that one file is cancelled.

**Controls and theme**

- Light/dark sets `data-theme` on the file's root element.
- This is the theme contract of the bundled `diagram` and `page-design` skills.
- Width, zoom and redraw are host controls drawn with the VS theme styles.
- Width is either full or 390 px.
- A tab starts in the light or dark theme of VS.
- It follows a VS theme change, both the background and the file theme.

**File changes**

- The tab redraws when the file changes on disk.
- If the file is later removed, grows over the limit or leaves the workspace, the refusal is shown inside the tab.

## Slash commands

| type | direction | payload |
|---|---|---|
| `commands/state` | host → UI | `{ commands: [{ name, source?, description?, hint?, subcommands?: [{ name, description? }] }], hidden? }` |

**The list**

- It is the list omp reports: `get_available_commands` once the engine runs, then every `available_commands_update`.
- It has no count limit.
- Past the UI hub's message size, the descriptions go first, then the last commands; dropped commands are counted in `hidden`.
- Names come without the leading `/`.
- While the engine is not running, the host sends `{ commands: [] }`.
- It is posted after `ui/ready` too.
- `source` is where the engine got the command from: `builtin`, `extension`, `skill`, `file`, `custom` or `mcp_prompt`.
- `source` is absent from an older host.
- The page merges the list with its own commands into the palette: the `/` button and typed `/` completion.
- The page sends any other `/name` text as a normal `chat/send`; omp runs extension, skill and prompt commands itself.

### Palette

**One entry per function, under one plain name.** The palette shows, in this order:

1. The page's own commands, which it handles itself and never sends on:
   `/new /sessions /rename /model /thinking /effort /permissions /compact /accounts /login /usage /work /agents /computer /agent /team /goal /help`.
   `/goal` opens the goal card, see "Goal runs".
2. POMP's engine commands under plain names, each with a short label, and only when the engine lists the registered name.
3. The engine's other commands, by name only, with no description and no argument syntax.
4. The enabled skills, plain names, after a "스킬" / "Skills" heading. The heading is a row of the list, not an option.

- `/effort` opens the thinking-level picker, like `/thinking`.
- `/permissions` opens the permission-mode picker of the composer's chip.
- `pomp-permissions` stays registered for the host's guard check and keeps its own name.

### Attachments with commands

- A command that hands its text to the model takes the context basket's files.
- Such commands are: POMP's `workflow`, `plan`, `map`, `review`, `skill`, `command` and `recipe`, a skill, or a prompt file (source `file`).
- The page sends the basket's paths as `attachments` with such a command only, then empties the basket, as for a message.
- The host puts them after the command's own text under "Work only on these workspace references…".
- The host keeps a space after the name.
- Any other command goes without attachments, and the basket stays.

### POMP engine commands

POMP's engine commands are registered in the engine with a `pomp-` prefix, which keeps them clear of the engine's own commands.
The palette lists the plain name; the host sends the registered one.

| plain name | registered as | does |
|---|---|---|
| `/workflow <task>` | `pomp-workflow` | Starts a staged POMP Workflow: plan, discover, edit through change review, verify. |
| `/plan <task>` | `pomp-plan` | A plan-only turn, see "Plan-only turns". |
| `/map [focus]` | `pomp-map` | Explains a bounded structural map of the codebase. |
| `/review [focus]` | `pomp-review` | Reviews the source with the host's diagnostics and evidence-backed findings. |
| `/max [model]` | `pomp-max` | Chooses an authenticated model with at least a one-million-token context. |
| `/rules` | `pomp-rules` | Shows the scoped rules that apply and their diagnostics. |
| `/resources` | `pomp-resources` | Lists the `.pomp` skills, commands and agent recipes. |
| `/guard` | `pomp-guard` | Shows the pre-edit guard mode and the files with unsaved Visual Studio changes. |
| `/vs-status` | `pomp-context` | Shows the Visual Studio connection: host, solution, unsaved files, offered tools. |
| `/skill NAME`, `/command NAME`, `/recipe NAME` | `pomp-skill`, `pomp-command`, `pomp-recipe` | Load a `.pomp/skills`, `.pomp/commands` or `.pomp/agents` Markdown resource into the conversation. |

- `/goal` is the page's command; the host never receives it as text.
- `/review` is not terminal-UI-only.
  - The bundled engine's custom `/review` runs a local review in headless text mode.
  - It also accepts a GitHub PR URL or `pr://owner/repo/N`, and an optional review focus.
- When the engine advertises a non-builtin command of the same name, that command wins over the host's `/review` mapping.
  Examples: `file`, `custom`, `mcp_prompt`, another extension.
- For `builtin` entries, the POMP counterpart wins.
- A short name the user defined is kept; the POMP counterpart is then not listed under that name.
- The registered names, such as `/pomp-workflow`, keep working when typed, and are not listed.
- POMP's plumbing commands are never listed; typing them still sends them.
  They are `pomp-tools`, `pomp-inline`, `pomp-stop`, `pomp-feedback-guard`, `pomp-agent`, `pomp-dm` and `pomp-team`.
- `/pomp-compact` no longer exists.
  - The engine's `compact` is dispatchable over RPC.
  - POMP's compaction hook (`session.compacting`) already adds the Visual Studio facts to every compaction.
  - So the page's `/compact [instructions]` (`session/history` `compact`) is the one command.
  - A typed `/pomp-compact` with arguments runs it.

### Mapping at send

- `chat/send` carries the text exactly as typed; the transcript and its replay show `/workflow ship it`.
- Only the prompt that goes to the engine is rewritten, by the host (`PompSession.ResolveSlashCommand`), when the text starts with `/name`:
  - A plain name of the table becomes its registered command.
  - A plain skill name becomes `/skill:name` when the engine lists `skill:name` and no real command has that plain name.
    A command always wins.
  - Anything else is sent untouched: `/skill:name`, a registered name, and unknown names.

### Skills in the palette

- Every `skill:<name>` row of the engine's list is listed as `/<name>` in the skills group, sorted by name.
- That covers built-in, user and project skills; the engine omits the ones switched off in Tools › 스킬.
- A skill the page knows to be switched off (`skills/state` rows with `enabled: false`) is not listed.
  This holds even before the engine's list catches up.
- A skill whose plain name a real command already has stays reachable as `/skill:<name>`, listed under that name.
- Names that are not safe to type, because they contain `/`, are listed as `skill:<name>` among the engine's commands.

## Plan-only turns

`/plan <task>` asks for a plan and changes nothing.
It is typed; the palette lists it when the engine lists `pomp-plan`.

**Flow**

- The host sends `/pomp-plan <task>`.
- The extension module `plan-guard.ts` arms its guard and sends the plan prompt.
- `/plan` without a task shows an inline error in the composer and sends nothing.

**Allowed during that one run**

- Reading and searching: `read`, `glob`, `grep`, `find`, `ast_grep`, read-only `lsp` actions.
- The web: `web_search`, `fetch`.
- `todo`: the plan, which shows in Work › 진행, where the user edits and runs it.
- `ask`.
- The POMP host tools that only read: `read_buffer`, `search_codebase`, `build_codebase_context`, `review_source`, and the like.

**Refused**

- Every other `tool_call` is refused with a short reason.
- This includes edits, `bash`, `eval`, `task` (no subagent starts), MCP and extension tools.
- It also includes the host's edit, build, test, Git, debugger, terminal and workflow tools.

**Lifting the guard**

- Only these lift it: the end of the run, a new or switched session, or its two-hour age ceiling.
- The end of the run is an `agent_end` that is not followed by a continuation.
- Nothing the model can call or run lifts it.
- The model can put a turn it is already in under the same guard with the extension tool `enter_plan_only`.
  It takes no arguments ("just plan it").
- It is an engine-side filter, not an OS sandbox; the host's approvals and the project policy apply on top.

## Automatic continuation

Automatic continuation ("자동 계속", Work › 진행) sends the follow-up prompt of an active POMP Workflow by itself.

- It runs up to six times.
- It runs only after the user turned it on for that workflow.
- Two limits end it, whichever is hit first: the six runs, and an optional **token budget**.

| type | direction | payload |
|---|---|---|
| `workspace/composerAuto` | UI → host | `{ action: "status"\|"enable"\|"disable", requestId, root, sessionId, id, revision, tokenBudget? }` |
| `workspace/composerAutoState` | host → UI | `{ root, sessionId, id, enabled, remaining, dispatching, message, requestId?, tokenBudget?, tokensUsed? }` |

**`tokenBudget`**

- `enable` takes `tokenBudget`, a whole number from 10,000 to 50,000,000.
- Absent or empty means none.
- A string of digits with thousands commas is accepted.
- Anything else is refused with the reason in `message`; nothing is armed.
- In the answer, `tokenBudget` and `tokensUsed` are present while armed with a budget.

**What is counted**

- The model calls of the conversation since the moment of enabling.
- Per finished assistant message (`message_end`, `message.usage`): `input + output + cacheWrite`; cache reads are left out.
- When a provider reports only `totalTokens`, that value is used.
- This is the engine's own definition of the "tokens" of an agent run.
- Reason: with prompt caching, every call reads the whole cached context again.
  Summing cache reads says nothing about work done.
- Subagent (member) runs are not counted.
- Their progress metrics remain in `workspace/backgroundState`, linked to the chat task card.

**When it stops**

- While armed, the host looks at the budget on every tick, once a second.
- A run that finished past the budget, or is still running past it, ends the continuation there.
- The message is "Token budget (N) used up." (Korean "토큰 예산(N)을 모두 썼어요.").
- A run is never started over budget.
- A run that was planned just before the budget ran out is not current anymore and is dropped.
- A running turn is not aborted.
- The six-run cap still applies independently ("Six follow-up agent runs were used.").

**Panel**

- While the continuation is off, the panel shows a labelled "토큰 예산" field.
- The page validates it too: an inline error, and nothing is sent.
- While armed with a budget, the status reads "남은 실행 N회 · 토큰 x / y".

## Goal runs

A **goal run** (`/goal <text>`) starts a POMP Workflow on the text.
It arms automatic continuation for the workflow that start creates, optionally within a token budget.
The count starts at the start of the goal; its first run belongs to the goal.

| type | direction | payload |
|---|---|---|
| `workspace/goal` | UI → host | `{ requestId, action: "start"\|"cancel", root?, sessionId?, text?, tokenBudget?, fromRequest? }` |
| `workspace/goalState` | host → UI | `{ requestId, ok, stage: "started"\|"cancelled"\|"refused", error? }` |
| `workspace/goalRequest` | host → UI | `{ requestId, root, sessionId, text, tokenBudget? }` |

**The goal card**

- Typing `/goal <text>` opens the **goal card**. `/goal` is the page's command.
- `/goal` without text is an inline composer error.
- `/goal` in an agent or team tab is refused.
- The card is a request card at the end of the conversation.
- It holds the goal text (editable), a "토큰 예산" field, and the question "이 목표를 자동으로 이어서 실행할까요?".
- Buttons: 시작 / 취소.
- Enter starts, Esc cancels; Shift+Enter is a line break in the text.
- The keys act 0.5 s after the card appears.
- 시작 waits while a reply runs.
- Nothing is sent before the answer.

**`workspace/goal`**

- It answers the card.
- `start` runs on the chat command queue.
- `start` is refused with a reason (`workspace/goalState`) when:
  - the text is empty or over 16,000 characters;
  - the budget is wrong;
  - `root`/`sessionId` are not the current ones;
  - there is no workspace or conversation;
  - the conversation is busy: a reply, a pending question, unfinished terminal or agent work;
  - a workflow is already active, or a goal is starting;
  - the engine does not list `pomp-workflow`.
- Otherwise the host registers the goal, with token baseline = now.
- The host sends the prompt with the user's bubble `/goal <text>`, and `/pomp-workflow <text>` to the engine.
  That is exactly what `/workflow` sends.
- The host answers `{ requestId, ok: true, stage: "started" }`.
- `cancel` only clears the card the model asked for (`fromRequest`).

**Binding**

- The first active workflow of this conversation that appears after the start, with a new id, is the bound one.
- The goal arms automatic continuation for it: `tokenBudget`, six runs, the first follow-up as soon as the first run is over.
- Nothing is armed when:
  - no workflow appears within ten minutes;
  - the goal's turn ended without one;
  - the workflow is paused or belongs to another conversation.
- In that case the reason is the state message and a chat notice.
- These cancel a goal that is still waiting, like they end an armed continuation:
  a manual chat message, Stop, a conversation switch, a new conversation, an engine restart.

**Model request: `request_goal_run`**

- The model can ask for a goal run with the host tool `request_goal_run` `{ goal, tokenBudget? }`.
- It posts `workspace/goalRequest`, which opens the very same card.
- The host keeps the request for a page that reloads, until it is answered.
- A request for another conversation is ignored.
- It returns "nothing has started" to the model.
- Only the user's `workspace/goal` `start` starts anything.
- Gates, like `present_case_feedback`:
  - exactly one running turn;
  - the epoch connection;
  - a ready conversation;
  - the engine's `pomp-feedback-guard`, which keeps subagents from calling it: `ctx.agent.kind` must be `main`;
  - the engine's `pomp-workflow`;
  - an open chat window;
  - no active workflow;
  - no goal starting.

## Composer predictions

| type | direction | payload |
|---|---|---|
| `composer/predict` | UI → host | `{ requestId, sessionId, text }` |
| `composer/prediction` | host → UI | `{ requestId, text?, error? }` |

- `text` of `composer/predict` is the whole draft, at most 4,000 characters.
- It is sent after typing pauses for 500 ms, while `host/hello.composerPredictions` is true.
- The answer `text` is a one-line continuation, at most 200 characters.
- `error` is a code the page ignores.
- The host runs at most one at a time for the current conversation; a newer request cancels the older.
- It runs through the editor-model mode `prompt`: 6 s, 48 tokens, reasoning off.
- The host never logs the draft.

**`permission/request.tasks`**

- It lists every assignment of a `task` tool approval: `agent` and the full `task` text.
- It is present when the host can match the approval to exactly one pending task call of that conversation.
- It is display only; the approve/deny path is unchanged.
- Without it, the page shows `message` as received.

## Sessions

`session/history` (UI → host) is answered by exactly one `session/historyState` with the same `requestId`.

| action | fields | effect |
|---|---|---|
| `refresh` | | List only. |
| `switch` | `root`, `id` | `switch_session` to that entry, then the transcript is replayed. |
| `messages` | `sessionId`, `cursor?` | A page (40) of the current session's user/assistant texts. |
| `preview` | `id` | Read-only preview of any listed entry, without switching. |
| `rename` | `name` | `set_session_name` on the current session. |
| `delete` | `id` | Moves that listed session file to the Recycle Bin. |
| `compact` | `instructions?` | omp `compact` of the current session. |

**`switch`**

- Refused while streaming, with an open approval/question, or with unfinished terminal jobs.
- When omp cannot restore the saved model, the host sends the switch again with `provider`/`modelId` of the current model.
  omp 18.6.3 and later report this as `Could not restore model …`.
- The host then posts an info notice.
- Then the transcript is replayed.

**`preview`**

- The host reads that session file, with the same opaque `id` resolution as `switch`.
- At most 1 MiB is read.
- The answer is `preview: { id, messages: [{ role, text, truncated? }], truncated }`.
- It holds the first 20 user/assistant text messages, at most 2000 characters each.

**`rename`**

- `name` is 1..120 characters, with no control characters.

**`delete`**

- It moves that listed session file of this workspace, and its artifacts folder, to the Recycle Bin.
- It is never a permanent delete.
- The current conversation is replaced by a new one first.
- POMP's own per-session data is dropped: recent entry, knowledge selection, partner preference, queued items, run history.
- Refused: anything not in the listing, outside the engine sessions folder, or containing links.

**`compact`**

- Custom instructions are at most 2000 characters.
- `session/info` shows the new context usage.

```
session/historyState: { requestId?, error?, root,
  current?: { sessionId, name? },
  entries: [{ id, sessionId?, title, name?, modifiedUtc?, visitedUtc?, messageCount?, current?, partner? }],
  messages?, nextCursor?, totalMessages?, sessionId?,
  preview?: { id, messages: [{ role, text, truncated? }], truncated } }
```

**`entries`**

- They are this workspace's omp sessions, newest first.
- There is no count limit.
- Rows that would take the message past the UI hub's size are left out and counted in `hidden`.
- They include the sessions POMP has seen.
- They also include the other `.jsonl` files in the folder of the current session file.
  omp keeps one folder per working directory.
- `title` is the session name, else the first user message (at most 120 characters), else the date.
- `id` is an opaque handle that only `switch` understands.
- The host resolves it to a file inside that folder or to a remembered session; it is never a path from the page.

## Workspace trust

omp (18.4.3 through 18.12.1) trusts every project. From the workspace it loads:

- settings that can auto-approve tools;
- MCP servers, started with the engine's environment;
- tool code;
- files that context files include with `@path`, even outside the workspace.

The full list is in docs/architecture.md "작업 영역 신뢰".

**Gate before start**

- Before starting omp, the host scans for these.
- The user trusts exactly one set per folder; `fingerprint` is computed over paths, contents and include targets.
- When some exist and the user has not trusted exactly that set for this folder, omp is not started.
- `workspace/trustState` then lists the files.
- `bridge/state` becomes `failed` with `reason: "workspaceTrustRequired"`; it is not retried.

**Card**

- The chat shows a card in place of the connection banner.
- It lists the files, each with *파일 열기*.
- *신뢰하고 시작* sends `workspace/trust` `trust` with the fingerprint shown.
- *시작 안 함* keeps the card; nothing is sent.

**Changes and revocation**

- A changed set, such as from a git pull or an agent's edit, asks again on the next start.
- Tools › 권한 › 신뢰한 저장소 revokes trust.
- The trust list lives in POMP's data folder, never in the workspace.

## Engine setup (accounts / login / model / usage)

- A fresh install has no model provider: omp exits at start-up with "No models available".
- The host does not retry that case: `bridge/state` goes straight to `failed` with `reason: "setupRequired"`.
- The page then shows the setup panel in place of the failure banner.
- The same panel ("계정 · 모델") opens from the status bar at any time.
- The model and the thinking level are also switched from the composer.

### Sign-in without a terminal: the auth helper

omp can only start its RPC mode once a model is usable.
So sign-in, first run included, runs in a separate, short-lived **auth helper** omp process that the host owns:

```
omp --mode rpc --no-session --no-extensions --no-skills --no-rules --model openai/gpt --api-key pomp-setup-placeholder
```

- The placeholder key is passed on the command line only.
- `OPENAI_BASE_URL` points at a closed loopback port, so nothing can reach OpenAI with it.
- The helper never prompts a model.
- It shares the user's omp profile, so a credential it stores is the one the chat engine uses.
- It writes no session and no settings.
- It starts on demand: provider list, login.
- It exits after 2 idle minutes, on dispose, and before the engine is restarted after a first sign-in.

Helper RPC calls:

| call | result | use |
|---|---|---|
| `get_login_providers` | `{ providers: [{ id, name, available, authenticated }] }` | Provider list, about 80 providers. |
| `login { providerId }` | Response when the sign-in finished or failed | Runs one sign-in. |

**`get_login_providers`**

- Some sign-in methods store their credential under another provider (omp 18.8 `storeCredentialsAs`).
  Examples: `openai-codex-device` → `openai-codex`, `zai-coding-plan` → `zai`.
- Such a method stays in `providers`, but the host always sends it as `authenticated: false`.
- Reason: omp answers it from the other provider's stored credential and names no target.
  It would otherwise read as a second account of the same sign-in.

**`login { providerId }`**

While it runs, omp sends `extension_ui_request`s:

| request | fields | host handling |
|---|---|---|
| `open_url` | `url`, `launchUrl?`, `instructions?` | Opens `launchUrl ?? url` in the default browser, http/https only; shown in `login`. |
| `notify` | progress text | Shown as progress. |
| `input` | `title`, `placeholder` | Shown as `login.prompt`; answered with `answerPrompt`. |

- An `input` example: "paste the authorization code".
- A provider that needs a secret typed in a terminal answers with an error.
- The page then offers `loginConsole`, the old console sign-in.

### `setup/state` (host → UI)

- Posted after `ui/ready`, after every change, and once per `setup/command`.
- The per-command post carries that command's `requestId`; `error` says why that command failed.
- All fields except `requestId`/`error` are the full current state.

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
  editorModel: { choice: "auto"|"chat"|"model",
                 model?: { provider, id },
                 effective?: { provider, id, name? },
                 resolving?: true, error? },
  localServers: [{ id, name, address, hasKey, savedUtc }],
  restartRecommended? }

UsageReport = { provider, account?, plan?,
  limits: [{ label, window?, resetsAt?,
             usedFraction?, used?, limit?, unit?,
             status: "ok"|"warning"|"exhausted"|"unknown",
             shared?, tier?, modelId? }] }
```

**Field notes**

| field | meaning |
|---|---|
| `providers`, `providersError` | From the auth helper, filled by `listProviders`. |
| `login.url`, `login.instructions` | `open_url` of the running sign-in, already opened in the browser. |
| `login.progress` | Latest `notify` text. |
| `login.prompt` | The sign-in waits for this input. |
| `apiKeyProviders` | The only providers a key can be saved for: a host allowlist. |
| `keys` | Saved keys; the key itself never leaves the host. |
| `thinkingLevels` | `get_available_thinking_levels` for the current model. |
| `currentThinking` | The session's thinking level. |
| `localServers` | Saved model servers, never the key. |
| `restartRecommended` | Credentials changed and the running engine does not offer them yet. |

- Model lists show a provider `id` of a local server by its `name`.

**`editorModel`**

- The helper model ("보조 모델"), separate from the chat model.
- It serves completions, ask edits, captions, knowledge conversion and composer predictions.
- `model` applies to choice `"model"`.
  Its `provider` is `""` for a value typed in Tools > Options without a provider.
- `effective` is what the engine resolved the choice to (`resolveEditorModel`).
- `effective` is dropped when the engine restarts or the chat model changes.
  The host re-resolves it if it was known.

**`UsageReport`**

- `account`: an email or account id label.
- `plan`: the subscription plan when omp names it, e.g. Codex `metadata.planType` `"prolite"`.
- `window`: e.g. `"5 Hour"`, `"7 Day"`.
- `resetsAt`: ISO 8601, UTC.
- `usedFraction`: 0..1, when known.
- `shared`, `tier`, `modelId` give the omp scope of a limit:
  - `shared`: shared by every model of the account.
  - `tier`: one model family, e.g. opus, sonnet, fable, spark.
  - `modelId`: one model id.
- The composer shows the shared limits plus the current model's own, as omp does; its tooltip shows all of them.

### `setup/command` actions (UI → host)

| action | fields | rule |
|---|---|---|
| `refresh` | | Re-posts `setup/state`. |
| `listProviders` | | Starts the auth helper if needed; fills `providers` or `providersError`. |
| `login` | `provider` | RPC sign-in through the auth helper; one at a time. |
| `answerPrompt` | `promptId`, `value?` | Answers `login.prompt`; ≤ 4096 characters. |
| `cancelLogin` | | Cancels the running sign-in. |
| `loginConsole` | `provider?` | Fallback: `omp login [provider]` in its own console window. |
| `saveKey` | `provider`, `key` | Saves an encrypted API key. |
| `removeKey` | `provider` | Deletes the saved key. |
| `saveLocalServer` | `id?`, `name`, `address`, `key?` | Saves or replaces a user-run model server. |
| `removeLocalServer` | `id` | Deletes the saved server. |
| `logout` | `provider` | Signs out of every sign-in omp stored for the provider. |
| `listModels` | | `get_available_models` → `models` / `modelsError`. |
| `setModel` | `provider`, `modelId` | `set_model` for the current session. |
| `listThinking` | | `get_available_thinking_levels` → `thinkingLevels`. |
| `setThinking` | `level` | `set_thinking_level`; one of `thinkingLevels`. |
| `setEditorModel` | `choice`, `provider?`, `modelId?` | Saves the helper model, then resolves it. |
| `resolveEditorModel` | | Asks the engine which model the current choice means. |
| `listUsage` | | Runs `omp usage --json` → `usage`. |

**`login`**

- `provider` must match `[a-z0-9][a-z0-9.-]{0,63}`.
- Success → `login.state: "succeeded"`, and `providers` is refreshed.

**`answerPrompt`**

- An absent `value` cancels the prompt.

**`cancelLogin`**

- The helper process is ended.

**`loginConsole`**

- Exit code 0 → `succeeded`.

**`saveKey`**

- `provider` must be one of `apiKeyProviders`.
- `key`: 8..1024 printable characters without spaces.
- The key is encrypted for the current Windows user (DPAPI) under the POMP data folder.
- It is passed to every later omp start as `envVar`.

**`saveLocalServer`**

- Saves a model server the user runs: any OpenAI-compatible server.
- With `id`, it replaces that server.
- `name`: 1..40 characters, unique, case-insensitive.
- `address`: an absolute http(s) URL without user information or spaces, ≤ 2,048 characters.
- `key`: optional, 8..1024 printable characters, encrypted like the API keys.
- An empty `key` keeps the saved one.
- Stored in `credentials\local-models.json` under the POMP data folder.
- Each server gets an id `local-<slug>` that never changes.
- Every later omp start gets the servers as `POMP_LOCAL_MODELS`: `[{ id, name, address, key? }]`.
- `POMP_LOCAL_MODELS` is limited to 30,000 characters; saving more is refused.
- The POMP extension `local-models.ts` registers each server as provider `id`.
- Its models come from `<address>/models`, or `<address>/v1/models`, with `api: openai-completions`.
- `restartRecommended` is set as for key changes.

**`logout`**

- omp has no per-account logout over RPC.
- The host ends the auth helper and runs `omp auth-broker logout <provider>`.
- That command runs hidden, in POMP's own folder, with no saved keys in its environment, ≤ 20 s.
- Only exit code 0 counts.
- Refused while a sign-in runs.
- The answer carries the re-read `providers`.
- A provider still listed there is authenticated another way: POMP-saved API keys and environment variables stay.
- The answer drops that provider's usage.
- `restartRecommended` is set as for key changes.

**`listModels`, `listThinking`**

- Need a running engine.

**`setModel`**

- `session/info` reports the new model.
- omp remembers it as its default.

**`setEditorModel`**

- `choice`: `auto` | `chat` | `model`.
- Saves the options value "보조 모델" / "Helper model": empty, `@chat` or `provider/id`.
- `auto` = omp's fast model `@smol`: the configured fast role, else omp's built-in priority list.

**`resolveEditorModel`**

- Uses the POMP extension's `/pomp-inline` in mode `resolve`; no model is called.
- Result: `editorModel.effective` or `editorModel.error`.

**`listUsage`**

- Runs hidden, ≤ 45 s; asks each signed-in provider for its limits.
- `usageLoading` is set while it runs.
- The page asks for it when the account panel opens, at most once a minute, and on its refresh button.

**After a credential change**

- After a successful sign-in or `saveKey` while the engine is not running, the host restarts it by itself.
- While the engine runs, the host re-reads `get_available_models`.
- When the new provider's models are listed, the change is live; no restart.
- Otherwise the state carries `restartRecommended: true` and the page offers *Restart* (`bridge/restart`).

**Account screen labels**

- The **보조 모델** choice offers **빠른 모델 / 채팅 모델과 같게 / 모델 직접 선택**: fast model, same as chat model, pick a model.
- A picked model shows the model name with the provider display name below it.
- `provider` and `modelId` are protocol identifiers.
- The picker's hover title keeps the same identifiers as the chat model picker.
- Usage windows are **5시간 / 주간**: 5 hours and weekly. Windows that are not reported are hidden.
- The options page is **일반 / General**; category and setting names follow the POMP language.

## Request cards (design: 권한 카드)

**Placement**

- A pending `permission/request` or `dialog/request` is a card at the end of the transcript, where the conversation is.
- It is never a modal overlay.
- Only the oldest pending request is active; the counter "2 / 3" shows the rest.
- The transcript, the composer and the other tabs stay usable.
- The chat tab shows a dot while a request waits and another tab is active.
- After the answer, a dismiss or a timeout, the card stays as a one-line record: title and outcome.
- Outcomes: 허용함 / 거부함 / 항상 허용함 / 이 대화에서 허용함 / 만료됨 / 취소함 / 답변함.
- A host dismiss without an answer shows 처리됨.

**Keyboard**

- With the focus inside the card, `Enter` = the primary answer: allow once, confirm, or the highlighted option.
- `Esc` = deny or cancel.
- In the deny-note field, `Enter` denies with the note.
- `Enter` counts only while the card is on screen.
  A card below the fold, after the user scrolled up, takes no focus and ignores Enter.
- The chords `shortcuts.allow` and `shortcuts.deny` of `host/hello` answer the active card from anywhere on the page.
- Their defaults are `Alt+A` / `Alt+D`, set in Tools > Options > Pomp > General > Shortcuts.
- A page chord other than `send` holds Ctrl or Alt, or is an F key.
- The host and the page replace anything else, e.g. `Enter`, `A`, `Shift+D`, with the default; so typing never answers a card.
- Nothing answers during IME composition, on key repeat, or within 500 ms of the card appearing (arming).
- The card takes the focus on arrival unless the user is typing an unsent message.
- The focus returns afterwards.

**Buttons**

| button | answer | meaning |
|---|---|---|
| **허용** | `allow` | This call only. |
| **거부** | `deny` | Deny. |
| **항상 허용** | `alwaysAllow` | Persisted; the options page can revoke it. |
| secondary action | `allowForSession` | Allow for the session. |

- An optional note is sent with `deny` for host tools.
- For `source: "ompApproval"` the note field is hidden; omp has no channel for it.
- `canRemember: false` marks omp prompts that omp shows regardless of rules.
  These are a `Reason:` line from the tool's own policy, or a `Provider safety checks:` section from the model provider.
- With `canRemember: false`, offer only *allow once* and *deny*; the page shows no note about it.
- Session grants last for one omp process.
- They are cleared on reconnect/restart and on `session/new`.

**Content**

- Show `title`, `toolName`, `target` and `message`.
- `message` is monospace and preserves line breaks.
- For omp approvals, `message` is omp's own prompt text, such as `Allow tool: bash` followed by the command.
- A host tool's `message` holds the operation details: command, timeout, files.
  It holds at most one consequence sentence.
- When `diff` is present, render it with the Diff view: unified diff, inline mode first, side-by-side later.
- For `edit_buffer_hashline`, `diff` is the complete, exact change that will be applied; never truncated.
- That tool refuses a patch whose diff exceeds 180,000 characters.

**Replies**

- A reply is removed from the page only after it was handed to the host.
- Failure cases: the host is unavailable, posting throws, or the reply is too large.
- The page checks the UTF-8 envelope and the encoded omp reply for size.
- On failure the card stays with an inline error and keeps the user's input.
- It can be retried or cancelled; nothing is truncated silently.
- The same applies to `dialog/respond`.
- `timeoutMs` → a visible countdown.
- When it expires, the host resolves the request itself and sends `permission/dismiss`.
- While a request is pending, the composer stays usable, so the user can type a follow-up.
- *Send* is disabled meanwhile.
- A new conversation, a session switch and a retry wait for the answer.

## Theme variables (`theme/changed.colors`)

| group | variables |
|---|---|
| base | `--pomp-bg`, `--pomp-fg`, `--pomp-muted-fg`, `--pomp-border`, `--pomp-panel-bg` |
| input | `--pomp-input-bg`, `--pomp-input-fg`, `--pomp-input-border`, `--pomp-focus-border` |
| button | `--pomp-button-bg`, `--pomp-button-fg`, `--pomp-button-hover-bg`, `--pomp-button-secondary-bg`, `--pomp-button-secondary-fg` |
| status | `--pomp-link`, `--pomp-accent`, `--pomp-error`, `--pomp-warning`, `--pomp-success` |
| other | `--pomp-code-bg`, `--pomp-selection-bg`, `--pomp-scrollbar`, `--pomp-diff-add-bg`, `--pomp-diff-del-bg` |

- The page must define sensible dark defaults for all of them in CSS.
- So it renders before the first `theme/changed`.

## Keyboard (design: 포커스 정책)

**Composer**

- `Esc` inside the tool window returns focus to the composer.
- `Enter` sends and `Shift+Enter` inserts a newline (`host/hello.shortcuts.send` = `Enter`).
- With `Ctrl+Enter`, only Ctrl+Enter sends and Enter inserts a newline.
- While busy, the Send button becomes Stop and *New chat* is disabled.
- Busy means a turn of this page is open or `session/info.isStreaming`.

**Page chords**

The page's own chords come from `host/hello.shortcuts`, set in Tools > Options > Pomp > General > Shortcuts:

| chord | default |
|---|---|
| One-message send-target picker | `Ctrl+Alt+T` |
| Permission-mode picker | none |
| Allow / deny of the active request card | `Alt+A` / `Alt+D` |
| Tools › Permissions | `Ctrl+Alt+P` |
| `liveFollow` | `Alt+L` |
| `liveBookmarks` | `Alt+K` |

- A chord is Ctrl/Alt/Shift plus one key.
- None of them acts during IME composition, on repeat, or with AltGraph.
- The Live/bookmark icons' tooltips and accessible names are just their names.
- The current chord is in `aria-keyshortcuts`.
- `liveFollow` and `liveBookmarks` send only `ui/editorFollow`, never a model prompt.
- Clicking either icon or invoking either chord from chat focuses the composer.
- Other editable fields keep their keyboard.
- The matching global VS commands are `Pomp.ToggleLiveFollow` and `Pomp.ToggleLiveBookmarks`.
- They apply the same persisted settings without opening chat or moving editor focus.

**Focus toggle**

- Focus toggling between the editor and the tool window is the VS command `Pomp.ToggleFocus`: default `Alt+J`, global.
- Its key and the keys of the other POMP commands are set in Tools > Options > Pomp > General > Shortcuts.
- They are applied to the IDE key bindings.
- Inside the page the same chord arrives as `host/hello.shortcuts.toggleFocus`; the page then sends `ui/toggleFocus`.
- Reason: WebView2 keeps keystrokes from reaching VS.

**Editor commands**

| command | chord | scope |
|---|---|---|
| Editor prompt (`Pomp.InlineEdit`) | `Alt+M` | Text Editor |
| Explain | `Shift+Alt+E` | Text Editor |
| Review | `Shift+Alt+R` | Text Editor |
| Fix | `Shift+Alt+F` | Text Editor |
| Tests | `Shift+Alt+U` | Text Editor |
| Add file | `Shift+Alt+I` | Text Editor |
| Add to knowledge | `Alt+/` | Text Editor |
| Open the chat | `Shift+Alt+Q` | global |
| New conversation | `Shift+Alt+N` | global |

- All are single-stroke chords, every one changeable in the same options page.

## Workspace review messages

**Work tabs**

- The work tabs are **진행 · 변경 · 코드 점검**: progress, changes, code check.
- Changes toggles between **대기 중** and **적용됨**.
- **대기 중** (pending) holds proposals and isolated results.
- **적용됨** (applied) holds apply records whose revert can be reviewed.
- The chip counts pending items only.
- Progress holds the plan, the workflow and automatic continuation.
- The interactive terminal card at the bottom shows only when both the option and engine support are present.
- The bundled engine does not support it.
- An approved command job attaches to its tool card in the chat via `origin.sessionId` and `origin.toolCallId`.
- That card shows the job's status, output and cancel.

**Code check messages**

| type | payload | meaning |
|---|---|---|
| `workspace/problemsState` | `{ root, items, truncated, updatedUtc }` | Host → UI: combined problem list. |
| `workspace/problems` | `{ requestId, action: "refresh"\|"open"\|"dismiss", id? }` | UI → host: act on the list. |
| `workspace/problemsReply` | `{ requestId, ok, error? }` | Host → UI: answer to `workspace/problems`. |

Item fields: `{ id, source: "agent"|"compiler", severity: "error"|"warning", code, message, path, line, column?, project?, evidence?, reporter? }`.

- The list merges only agent findings and VS compiler errors/warnings; neither has a count limit.
- `evidence`: the code the finding quotes, up to 4,000 characters.
- `reporter`: the reporting agent, `main` for the conversation's POMP, or a role name.
- The POMP extension tells the host the reporter with a hidden notice `pomp-review-reporter:` on each `report_review_findings` call.
- When the merged list exceeds one page message (1.75 MiB), only its first part is sent and `truncated` is true.
- `dismiss` removes agent items only.

**Code check screen**

- Filters: **전체 / 오류 / 경고 / 에이전트**: all, errors, warnings, agent.
- It offers selection, **코드 열기** (open code), **삭제** (delete) for agent items, and refresh.
- **에이전트에게 해결 요청** (ask the agent to fix) puts up to 50 selected items in one `chat/send`.
- The whole request is limited to 12,000 characters.
- It goes to the current send target.
- Editor context, attachments and the basket are not attached.

**Review proposals**

| type | payload | meaning |
|---|---|---|
| `workspace/reviewState` | proposal state | Host → UI: the current proposal. |
| `workspace/review` | `action`, `id`, `revision`, `path?`, `selected?` | UI → host: act on a proposal. |
| `workspace/editorChanges` | `{ requestId, action, ok, error?, files: [{ path, unsaved }] }` | Host → UI: answer to `editorChanges`. |

- `workspace/reviewState` carries the proposal id, revision, status, complete bounded file/hunk previews.
  It also carries selected hunk IDs, diagnostics and per-file outcomes.
- `workspace/review` `action`: `refresh`, `select`, `validate`, `apply`, `discard`.
- It sends the proposal `id` and the exact `revision`.
- `select` includes `path` and integer `selected` IDs.

**Proposals from the user's own edits**

- With no open proposal, the user can make one from their own edits.
- `editorChanges { requestId }` is answered by `workspace/editorChanges`.
- Its `files` are workspace files whose editor or disk text differs from git HEAD, or from the saved file when untracked.
- `fromEditor { requestId, paths }`, with 1–10 paths, stages a proposal with `origin: "editor"`.
- That proposal goes from the original to the current text.
- Applying it leaves the original plus the selected changes in the editor; unselected changes are taken out.
- It records a checkpoint like any apply.
- Discarding it changes nothing.

**Rules**

- The UI never supplies replacement source during acceptance.
- Stale revisions are ignored and refreshed.
- Validation does not grant write permission; apply requests a separate one-time host approval.
- A completed proposal cannot be applied twice.
- A new page requests `refresh`.
- The host also republishes the current proposal on page-ready.

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

**Role fields**

| field | meaning |
|---|---|
| `error` | The file cannot be used, e.g. no frontmatter, no description, too large. Such a role is not a team member. |
| `note` | The role works in omp, but POMP lists it read-only. |
| `tools` | `all`, `readOnly` or `custom`. |

- `note` cases: multi-line YAML values, block lists, model lists that a rewrite would corrupt.
- `tools: all` means there is no `tools` key.
- `tools: readOnly` means exactly read, find, grep, glob, lsp, yield.
- `tools: custom` means anything else, including `tools: []`.
- There is no rename. A role's name is its file name; a team's id is its file name.
- Teams carry `isolation` (boolean, default false).
  With it, members work in isolated copies.
  Their changes come back as isolated results to review and apply; see "Isolated team runs".

**Nested subagents**

- The overlay writes `task.maxRecursionDepth: 1` in every approval mode: only the main agent spawns subagents.
- omp gives subagents no `task` tool. A spawn attempt from inside a subagent (eval `agent()`/`workpool()`) fails with omp's
  "Cannot spawn another agent at task depth 1; maximum depth is 1."
- The main agent's `task` approval is unchanged.

### `agents/command` (UI → host)

The UI sends `agents/command` to manage roles and teams. Each is answered by `agents/reply`.

| action | parameters | effect |
|---|---|---|
| `refresh` | | Republishes `agents/state`. |
| `builtinRole` | `name`, a bundled role | Answers the bundled role as the engine defines it. |
| `saveRole` | `scope`, `role`, `expectedHash?` | Creates or atomically updates a role. |
| `deleteRole` | `scope`, `name`, `expectedHash` | Deletes a role file. |
| `saveTeam` | `scope`, `team`, `expectedHash?` | Creates or atomically updates team JSON. |
| `deleteTeam` | `scope`, `name` or `id`, `expectedHash` | Deletes a team file. |
| `transcript` | `subagentId`, `sessionId?` | Queries a subagent transcript. |
| `exportTeam` | `scope`, `name` | Answers `template`, a team template JSON text. |
| `openTeamExport` | `scope`, `name` | Writes the export text to a file and opens it. |
| `previewTeamImport` | `text`, `scope` | Answers an import `preview`. |
| `importTeam` | `text`, `scope`, `previewHash` | Imports the roles and the team as new files. |
| `mainRole` | | Answers `main`, POMP's own role instructions. |
| `saveMain` | `scope`, `instructions`, `expectedHash?` | Writes that scope's `APPEND_SYSTEM.md`. |

**`builtinRole`**

- Answers `role: { description, instructions, model?, thinkingLevel?, tools }`.
- The bundled role is read once per engine with `omp agents unpack --dir <temp>`.
- This is the form's "edit" of a bundled role.

**`saveRole`**

- `scope` is `"project"` or `"user"`.
- `role.builtin: true` marks an edited bundled role.
- `expectedHash` is required when modifying an existing file.
- A new file for `builtin` starts from the engine's definition of that role.
- Frontmatter the form does not write, such as `output` and `spawns`, is kept.
- `tools: "custom"` keeps the role's tool list. This also applies when editing any existing file.
- The file replaces the bundled role for the engine. Deleting it brings the bundled role back.
- Bundled names are allowed as role names. Only `main` and `sub` are reserved.

**`deleteRole`, `saveTeam`, `deleteTeam`**

- `deleteRole` and `deleteTeam` require `expectedHash`.
- `saveTeam` requires `expectedHash` when modifying an existing file.

**`transcript`**

- Queries via `get_subagent_messages`.
- Returns at most 40 text-only messages, each <= 4,000 chars, <= 64 KB total.
- Carries a `truncated` flag.

**`exportTeam` and `openTeamExport`**

- `template` is a `pomp-team-template` v1 JSON text, ≤ 256 KiB.
- It holds the team and its user-defined member roles. Bundled roles are included by name only.
- `openTeamExport` writes the same text under `%LOCALAPPDATA%\Pomp\exports\` and opens it in the editor.

**`previewTeamImport`**

- Answers `preview: { hash, team: { id, title, mode, members, status }, roles: [{ name, title, status }], problems }`.
- `status` is `new`, `same` or `conflict`. Roles may also be `bundled` or `existing`.

**`importTeam`**

- Creates the new roles, then the team, only as new files.
- Refuses when the preview hash changed, a name conflicts, or `problems` is not empty.
- Removes the files it created when a later write fails.

**`mainRole` and `saveMain`**

- `mainRole` answers `main: { scope, project?: { exists, text, hash? }, user: { exists, text, hash? } }`.
- `saveMain` writes that scope's `APPEND_SYSTEM.md`. Blank text removes it.
- `saveMain` is refused when the file's hash is not `expectedHash`.

**POMP's own role**

This is Agents › 역할 › POMP, the chat agent.

- `agents/state` carries `main: { avatar?, scope?, restartRequired }`.
- Its picture is stored like a built-in role's: `setAvatar` with scope `user`, name `main`. Images are keyed `bundled:main`.
- Its instructions are the engine's `APPEND_SYSTEM.md`, appended to the chat agent's system prompt.
- Project file: `<root>/.omp/APPEND_SYSTEM.md`. The engine also reads `.claude`, `.codex`, `.gemini`.
- User file: `~/.omp/agent/APPEND_SYSTEM.md`. The project file is read before the user file.
- `scope` names the one the engine takes.
- The engine reads the file only when it starts.
- The host keeps what the file was at the last start, i.e. `Starting`.
- `restartRequired` is true while the engine runs with other instructions. `bridge/restart` applies them.

### Knowledge

One list per workspace, managed in Tools › Knowledge: the workspace's project knowledge and this PC's user knowledge together.
The role and team editors and the context basket read the same list.
A knowledge is a user tag holding **pieces**. A tag has a name and color; there are no predefined tags.

| type | direction | meaning |
|---|---|---|
| `knowledge/state` | host → UI | The full knowledge list and selection. |
| `knowledge/draft` | host → UI | A piece from the editor to add. |
| `knowledge/command` | UI → host | A knowledge change or query. |

#### `knowledge/state`

Payload: `{ requestId?, root, sessionId?, selected: string[], optimizing?: string, items: KnowledgeItem[], hidden?: number, error? }`.

- There is no count limit.
- Items that would pass one message, 1.75 MiB, are left out and counted in `hidden`.
- `selected` lists the knowledge ids selected for the current conversation: every default knowledge it did not untick, then what it picked.
- `select` with the ticked ids stores what was picked besides the defaults and which defaults were unticked
  (`.pomp/local/knowledge-sessions.json` rows `{ ids, excluded? }`). A default made later is ticked in every conversation that did not untick it.
- `optimizing` is the id being optimized.
- Unasked pushes have no `requestId`.

`KnowledgeItem`:

```
{ id, name, color: "#rrggbb", scope: "project"|"user", use: "original"|"optimized", revision, createdAt, updatedAt,
  roles: string[], teams: string[], candidate: boolean, default: boolean, optimized?: { text, createdAt, stale },
  pieces: [{ id, text, source: "user"|"agent"|"editor", status: "saved"|"candidate", createdAt,
             anchor?: { path, startLine, endLine, status: "current"|"moved"|"changed"|"missing" } }] }
```

- A candidate knowledge has all pieces as candidates and was created by an agent proposal.
- The page does not list a candidate knowledge. Its pieces appear only as proposals.

#### `knowledge/draft`

Payload: `{ root, path, startLine, endLine, text }`.

- Sent from the editor's POMP ▸ Add to Knowledge. The menu has no list of knowledge.
- The page shows Tools › Knowledge with the add form holding that piece.
- The person picks a knowledge by name or names a new one.
- The name picker shows up to 8 names containing the typed text.
- It is sent as `create`, which adds to the knowledge of that name.
- `path` is workspace-relative. Lines are 1-based inclusive.
- A page that is not ready gets it at the end of its `ui/ready` snapshot, like `composer/prefill`.

#### `knowledge/command`

Payload: `{ requestId, root, action }` plus the action's fields.

- Each command is answered by one full `knowledge/state` with the same `requestId`.
- `root` must be the session's workspace.
- Changes need the knowledge's current `revision`.

| `action` | Fields | Effect |
|---|---|---|
| `refresh` | — | Current state. |
| `create` | `knowledge: { name, color, scope }`, `piece?: { text, anchor?: { path, startLine, endLine } }` | New knowledge. |
| `update` | `id`, `revision`, `knowledge: { name, color, scope, use }` | Updates the knowledge. |
| `delete` | `id`, `revision` | Removes the knowledge. |
| `setDefault` | `id`, `revision`, `on` | Makes the knowledge a default or an ordinary one. |
| `addPiece` | `id`, `revision`, `piece: { text, anchor? }` | Adds a saved piece. |
| `updatePiece` | `id`, `revision`, `pieceId`, `text` | Changes a piece's text. |
| `deletePiece` | `id`, `revision`, `pieceId` | Deletes a piece. |
| `acceptPiece` | `id`, `revision`, `pieceId` | Candidate → saved. |
| `optimize` | `id`, `revision` | Runs the editor model. |
| `assign` | `kind: "role"\|"team"`, `name`, `ids` | Assigns knowledge to a role or team. |
| `select` | `ids` | Sets the selection of the current conversation. |

- `create`: when the name exists, case-insensitive, the piece is added to it instead.
- `update`: renaming onto another existing name is an error.
- `addPiece`: a duplicate adds nothing.
- `updatePiece`: an anchored piece keeps its path and range.
- `deletePiece`: deleting the last piece of a candidate knowledge deletes the knowledge.
- `acceptPiece`: duplicates are removed.
- `optimize`: a state with `optimizing` follows, then the result or `error`.
- `assign`: afterwards exactly the knowledge in `ids` lists `name` in its roles/teams.

#### Limits and storage

- Name: 1–40 characters, no control characters.
- Piece text ≤ 8,000. Anchor ≤ 200 lines. Optimized text ≤ 16,000.
- Knowledge, pieces and candidate pieces have no count limit of their own; the store size bounds them.
- Each file ≤ 4 MiB.
- `project`: the workspace's `.pomp/knowledge.json` (indented), which is committed. Its lease file stays in `.pomp/local`.
  A file an earlier POMP kept in `.pomp/local/knowledge.json` is moved there on first use.
- `user`: `~/.pomp/knowledge.json` (`%USERPROFILE%`), listed in every workspace on this PC. Its lease is next to it.
- A change takes both leases. Switching the scope moves the knowledge to the other file; the file it moves into is written first.
- An earlier scope `personal` reads as `project`.
- The model cannot write either file with an always-allow approval: every write asks.
- The former `%LOCALAPPDATA%\Pomp\knowledge\` store is migrated once and then removed.

#### Duplicates

- Pieces of one knowledge with equal normalized text and equal anchor path are kept once.
- Normalized text: LF, no trailing white space, no leading or trailing blank lines.

#### Used content

- `original`: the saved pieces joined by a blank line.
- An anchored piece appears as "`<path>` lines a–b:" plus a fenced block.
- `optimized`: the stored optimized text while its source hash matches the original.
- Otherwise `optimized` uses the original, and the item is `stale`.
- Optimize uses the editor-model mode `knowledge`: 60 s, 4,096 tokens, original ≤ 24,000 characters.
- With the option `Optimize knowledge automatically`, an optimized knowledge is optimized again about 5 s after its pieces change.

#### Anchors

When building the state, the host looks for the stored lines in the file.

- Files over 2 MiB count as `missing`.
- `current`: found at the stored range.
- `moved`: found exactly once elsewhere. The range is updated without a revision change.
- `changed`: only the first and last lines are found, in order.
- `missing`: otherwise.

#### Delivery to the model

All delivery is inside blocks marked as reference material.
These are not instructions that change tool permissions, approvals or host policy. Tags inside are neutralized.

- **Default knowledge** (`default: true`)
  - Ticked from the start in every conversation's basket; unticking it leaves it out of that conversation only.
  - Every subagent gets the defaults the conversation did not untick from `role-memory.json` `defaults` as its own `<pomp_knowledge>` block.
  - Limit: 20 items / 24,000 characters.
- **Selected knowledge of the conversation** (ticked defaults first)
  - Limit: 20 items / 24,000 characters.
  - Written to `POMP_STATE_DIR/session-knowledge.json` as `{ version: 1, root, sessionId, items: [{ title, body }] }`.
  - The omp extension appends it to the main session's system prompt as `<pomp_knowledge>` when root and session match.
  - A resumed conversation keeps its choice. A new one starts with the defaults ticked.
- **Knowledge assigned to a team**
  - Limit: newest 10 / 12,000 characters.
  - Sent as a `<team_knowledge>` block appended to the messages the user sends to that team.
  - Restored transcripts hide the block.
- **Knowledge assigned to a role**
  - Limit: newest 10 / 8,000 characters per role.
  - Written to `POMP_STATE_DIR/role-memory.json` v1 (`roles`, plus `defaults`). The title is the knowledge name.
  - The extension adds it to that role's subagent system prompt as `<role_memory>`.
- The main agent's system prompt names both knowledge files (`features-guidance`).

#### Host tools

- `propose_knowledge { tag, text }` adds a **candidate** piece to the knowledge named `tag`.
- Without such a knowledge, it creates a new candidate project knowledge.
- Only the user accepts a candidate.
- It posts an unasked `knowledge/state`.

### Advisors

Agents › 조언자 manages the advisors of the engine's `WATCHDOG.yml`.

- Project file: `<VCS root or engine cwd>/WATCHDOG.yml`.
- User file: in the engine's agent directory.
- An existing `.yaml` is used as the engine would.
- The host reads, validates and writes the file itself with the engine's rules, then asks the engine to apply it.
- The engine extension `extensions/advisor.ts` turns the advisors on and off.

| type | direction | meaning |
|---|---|---|
| `advisors/state` | host → UI | Advisor status, files and roster. |
| `advisors/command` | UI → host | An advisor action. |
| `advisors/reply` | host → UI | The answer to one command. |
| `chat/advisorNotes` | host → UI | Advisor notes delivered to the chat agent. |

#### `advisors/state`

Payload:

```
{ supported, sessionId?, enabled, status,
  advisors: [{ name, model?, status, contextTokens?, contextWindow?, cost? }],
  files: [{ scope, path }], roster: [{ name, scope, enabled, model? }], implicitDefault, updatedUtc }
```

- Posted whenever it changes. The `status` command asks for it.
- The host normalizes the top-level `status` and every `advisors[].status`.
- Allowed values: exactly `"off" | "no-model" | "active" | "paused" | "unknown"`.
- Engine mapping: running → active; no_model → no-model; disabled/stopped → off; idle/waiting/suspended → paused.
- Any other engine value → unknown.
- `implicitDefault`: no file defines an advisor, so the engine's default advisor runs.
- `contextTokens`, `contextWindow` and `cost` are the figures the engine's advisor status reports. `cost` is in US dollars.
- The page shows them as context percent and spend.

#### `advisors/command` and `advisors/reply`

Command payload: `{ requestId, action, scope?, document?, baseHash?, path?, name?, on? }`.

Reply payload: `{ requestId, ok, error?, scope?, path?, exists?, hash?, document?, warnings?, applied?, active?, history?, truncated?, notes? }`.

| action | meaning |
|---|---|
| `load` | Returns the scope's `document` and its `hash`. |
| `save` | Writes `document` when `baseHash` still matches. |
| `enable` | Turns advisors on; remains for the whole conversation. |
| `disable` | Turns advisors off; remains for the whole conversation. |
| `status` | Asks for `advisors/state`. |
| `history` | Returns advisor notes and transcript. |
| `open` | Opens one file in VS. |
| `select` | Turns one advisor on or off in this conversation. |
| `openHistory` | Exports an advisor transcript and opens it. |

**`load`**

`document` shape:

```
{ instructions?, maxNotesPerUpdate?,
  advisors: [{ name, enabled?, model?, reviewMode?: "turn"|"agent-end", reviewInterval?,
               syncBacklog?: "off"|"1"|"3"|"5"|"strict", tools?: string[], instructions?, maxNotesPerUpdate? }] }
```

- `tools` absent means default tools. Empty means none.

**`save`**

- Writes only when `baseHash` still matches. Otherwise it is a conflict error, and the page offers reload.
- A saved file whose instructions pull in other files with `@` is stored but not applied.
- In that case the reply has `applied: false`, and the page shows "다음 대화부터 적용".

**`select { name, on }`**

- Turns one advisor on or off in this conversation only.
- The host keeps the conversation's choices: advisor slug → on, over the files' `enabled`.
- The host sends the engine extension `apply { choices, enabled }`.
- The extension applies the files' advisors with those choices via `applyAdvisorConfigs`. Files are untouched.
- The extension also sets the conversation's on/off.
- Turned on while the conversation runs none, only that advisor starts.
- With none left on, the conversation's advisors stop.
- Without advisor files, it switches the engine's own `default` advisor.
- Every other command carries the choices too, so a `save` re-applies the files with them.

**`enable` / `disable`**

- These remain for the whole conversation.

**`open`**

- Opens one exact `path` of the latest `files` snapshot in VS.

**`history { name? }`**

- `notes`: every note the advisors gave in this conversation, oldest first.
- Note shape: `[{ advisor, severity?: "nit"|"concern"|"blocker", note, at? }]`.
- Notes are read from the session's "advisor" custom messages.
- No count limit: the newest notes that fit 160 KiB are sent, 8,000 characters each.
- `history`: the engine's compact advisor transcript.
- With `name`, only that advisor's part is sent: its "### Advisor: name (…)" section.
- `history` is `null` when that advisor does not run.
- Only the newest part is sent, cut at a line start, at most 256 KiB of UTF-8. `truncated` says so.

**`openHistory { name }`**

- Writes that transcript to `<DataDirectory>/exports/advisor-<slug>-<time>.md` and opens it in VS.
- The newest ten exports are kept.
- It fails while that advisor does not run.

#### `chat/advisorNotes`

Payload: `{ notes: [{ advisor, severity?, note }] }`.

- Part of the transcript.
- Sent for each "advisor" custom message the engine delivers to the chat agent.
- Shown as one card and restored with the conversation.
- The host refreshes `advisors/state` after every run.

#### Built-in advisor and page behavior

- The built-in advisor is the entry named `default`.
- Like the engine's own editor, the page saves a lone `default` entry with no other settings as no entry at all.
- So saving the built-in advisor without changes, or resuming it after a pause, leaves the built-in advisor in place.
- Each advisor row's switch, `select`, is the page's on/off control.
- An entry's `enabled: false` is edited as **일시 정지** in its form.
- Requests carry the session and a 15-second expiry, so a request queued past the host's timeout never runs.
- Advisor tools pass through the normal tool approvals.

### Event rules between agents and teams

| type | direction | meaning |
|---|---|---|
| `agentHooks/command` | UI → host | Lists, saves, deletes rules or sets `autoForward`. |
| `agentHooks/state` | host → UI | Rules, forward state and log. |

- `agentHooks/command` payload: `{ requestId, root, sessionId, action, id?, revision?, rule?, enabled? }`.
- `action` is `"refresh"|"save"|"delete"|"autoForward"`.
- `agentHooks/state` payload: `{ requestId, root, sessionId, autoForward, forwards, maxForwards, log: [{ at, rule, event, source, outcome }], rules, error? }`.
- `agentHooks/state` answers each command. It is also sent unsolicited after rules fire.

**Rule shape**

```
{ name, enabled,
  event: "agent.completed"|"agent.failed"|"team.completed"|"team.failed"|"feedback.waiting",
  sourceKind: "any"|"agent"|"team", sourceId?,
  action: "notify"|"forward", targetKind?: "main"|"agent"|"team", targetId?, message }
```

- There is no rule count limit. The rules file is at most 256 KiB.
- Stored in the workspace's `.pomp/local/agent-hooks.json`.
- They are separate from the IDE save/build hooks of `.pomp/hooks`.

**Actions**

- `notify` posts a chat notice.
- `forward` queues an ordinary chat message through the chat queue and approvals.
- A forwarded message has `clientMessageId` `hook-` plus a suffix, and editor context off.
- The event summary is in an `<event_summary>` data block.
- `forward` works only while this conversation's `autoForward` is on.
- `autoForward` starts off.
- `autoForward` turns off on a new engine connection or another conversation.
- The page has one switch for it: Agents › 자동화 › 자동화 규칙 **이 대화에서 사용**.
- That switch sends `autoForward` and the project-hooks `enable` together.

**Firing limits**

- Each event fires once.
- An agent job that was already finished when first seen raises nothing.
- Events of a turn started by a rule never forward again.
- The same holds for jobs first seen during such a turn.
- At most 10 forwards per conversation.
- A forward never resumes a paused queue.
- A team's result cannot be forwarded back to the same team.

**Routines: IDE events**

- Events: `ide.saved`, `ide.build.succeeded`, `ide.build.failed`. They take `sourceKind: "any"` only.
- `ide.saved` uses the rule's `glob`: workspace-relative, default `**`, ≤ 200 characters.
- They are raised only while the conversation is idle.
- A rule waits 60 seconds between messages.
- A rule is skipped while its previous message is still queued or running.
- The event summary holds the relative path or the build outcome only.

### Isolated team runs

**Engine setup**

- The engine overlay sets `task.isolation.enabled: true`, `merge: patch`, `apply: false`.
- It sets `worktree.base` to the workspace's `.pomp/local/wt`.
- A team with `isolation` runs only in a Git workspace and always starts new members.
- The extension blocks `isolated` task requests outside such a run.
- The extension blocks member tasks without `isolated: true` inside such a run.

**Result pickup**

- A member ends with a terminal `subagent_lifecycle` frame carrying its `sessionFile`.
- The host then picks up the engine's patch next to that session file and parses it strictly.
- Strict UTF-8, ≤ 2 MiB. There is no file count limit per result.
- Only text add/modify/delete. No renames, copies or binaries.
- No paths with `..`, a drive, a backslash or control characters.
- Paths are resolved from the Git repository root.
- A result touching anything outside the workspace, i.e. the solution folder, is listed as `blocked`.
- There is no result count limit. A result that does not fit the 64 MiB of held patches is only reported with its patch path.

| type | direction | meaning |
|---|---|---|
| `workspace/isolationState` | host → UI | The isolated results list. |
| `workspace/isolation` | UI → host | Refresh, apply, discard or open a result's patch. |

#### `workspace/isolationState`

Payload:

```
{ requestId?: string|null,
  items: [{ id, root, agent, job, status: "pending"|"blocked"|"applying"|"applied"|"failed", message, createdAt, patchPath,
            files: [{ path, kind: "create"|"modify"|"delete", added, removed }] }],
  hidden?: number }
```

- Pushed with `requestId: null` when a result arrives or changes.
- Also the answer to every command, with its `requestId`.
- The page shows only items whose `root` is the current workspace.
- Applied and failed items stay for reference until discarded.
- There is no count limit. When held patches pass 64 MiB, the oldest applied and failed items go first.
- A message that would pass the UI hub's size sends the newest items and counts the rest in `hidden`.

#### `workspace/isolation`

Payload: `{ action: "refresh"|"apply"|"discard"|"openPatch", requestId, root?, id? }`.

- Every action except `refresh` needs the item `id` and the current workspace `root`.
- A refused action keeps the item and puts the reason in its `message`.

**`apply`**

- Checks the project policy for `pomp_stage_workspace_edit`.
- Refuses files with unsaved editor changes.
- Refuses non-UTF-8 files, missing files to change, existing files to create, and any context mismatch.
- Then shows one approval card with the patch.
- After approval it plans again and writes nothing if a file changed meanwhile.
- Files are written directly. BOM and line endings are kept.
- Written files are restored on failure.
- A checkpoint is added to the change history.

**`discard`**

- Removes the item only. The patch file stays.

### Background agent progress and team correlation

`workspace/backgroundState` includes progress metrics, a parent tool call link, and team correlation for subagent jobs.

| field | meaning |
|---|---|
| `progress` | Progress metrics of the job. |
| `parentToolCallId?` | The parent `task` tool invocation. |
| `team?` | The team ID active for the turn that spawned the job. |
| `promptId` | The host turn open when this job was first observed. |

- `progress`: `{ toolCount: number, tokens: number, cost: number, durationMs: number, recentTools: string[], resolvedModel?: string, thinkingLevel?: string }`.
- `recentTools` holds up to 5 recent tool names, <= 80 chars each.
- `parentToolCallId` is a string, <= 256 chars.
- `team` is a string, <= 40 chars. It is the team of the conversation turn that spawned the subagent job.
- `promptId` is a string or null, <= 128 chars, no controls.
- `promptId` is kept through later progress and terminal states.

**Display**

- A member with `parentToolCallId` attaches to the matching `task` card in the chat.
- The card shows status, tool count, time, recent tools and an individual stop.
- The same card is also visible in the partner tab.
- Only current members without such a call card appear in the collapsible progress row.
- Agents › 실행 중 (Running) lists every job of the snapshot: running ones first, ended ones by `lastUpdate`, latest first.
  A `live` snapshot of another conversation is not listed. Each row shows the role, id, description, `team`, status, `progress.durationMs`, the end time (`lastUpdate`) and the same individual stop.
  Opening the view sends `workspace/background { action: "refresh" }`.

### Stopping one member

- `workspace/backgroundState.stopAvailable` is true while the engine lists POMP's `pomp-stop` extension command.
  See docs/architecture.md §8.12.
- Request: `workspace/background { action: "stop", requestId, root, sessionId, id }`, with no `generation`.
- It stops that running member only.
- The host sends `/pomp-stop <id> --request <id>`.
- It waits up to 10 seconds for the extension's `pomp-stop-result:` notification.
- That notification never reaches the transcript.
- The answer is the `workspace/backgroundState` carrying `controlRequestId` with `controlPending: false`.
- `controlMessage` says what happened or why not.
- Reasons include: not running, not started by this conversation, engine without the internals.
- Every control request is answered that way, refused ones included: `pause`, `resume`, `cancel`, `stop`.
- With the engine's own control protocol, `controlVersion: 1`, the page keeps pause/resume/cancel and does not add Stop.
