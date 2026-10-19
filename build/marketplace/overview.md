# POMP — an AI coding partner inside Visual Studio

POMP runs an AI coding agent next to your code and lets it work with
Visual Studio itself: the live editor buffers (including unsaved changes), the solution and its projects, the Error List,
builds, tests, the debugger, Git and more. You chat with the agent in a tool window; changes pass through an approval
gate (with session or saved rules where permitted). Everything ships inside the extension, so there is nothing else
to install.

## What the agent can do

| Area | Tools |
|---|---|
| Editor | read the active document and selection, read any workspace file from its live buffer, edit buffers with a hash-checked patch (one undo step per file, never saved automatically), symbol outline of very large files |
| Solution | browse projects and files, add / rename project files (POMP's `remove_project_file` stays permanently disabled; file deletion uses the approval-gated `delete` tool), read evaluated MSBuild properties |
| Build and diagnostics | build the solution or a configuration, read the Error List and Output window panes |
| Packages | list NuGet packages; install, update or uninstall one package in an exact project after approval (configured sources; dependencies and package/project code may run) |
| Tests | discover and run tests of the solution's test projects, both with approval |
| Debugger | inspect the debug state and call stack, add, remove, enable or disable breakpoints (conditions, hit counts, tracepoints), start, continue, pause, stop and step |
| Bookmarks | list and add bookmarks |
| Git | status and diff, commit and push (with approval) |

## Agents and teams

* Open a role agent (for example a reviewer or the built-in scout) as its own partner tab in the chat and talk to it directly.
  Each tab is its own conversation with its own list; `+` in the tab starts a new conversation with that agent or team.
  The main conversation can hand parts of a task to agents; agents do not start further agents.
* Each role chooses its own model, thinking level and tool scope, from any provider you connected.
* Teams of 2–6 roles run with a goal and a mode (leader decides, in parallel, in order). Member status, tool count, time,
  recent tools and individual stop controls appear on the chat task card, in the partner tab and in **Agents > Running**.
  Manage definitions and event handling in **Agents > Roles · Teams · Advisors · Automation** (advisors watch the main agent's work and comment).
* **Work > Progress · Changes · Code check** brings together plans/workflows/automatic continuation, pending proposals and
  isolated results with applied history/restore, and agent findings with Visual Studio compiler errors/warnings. Select
  items to send one request to the current target without editor context (50 items / 12,000 characters).
* **Tools > Conversation · Permissions · Skills · MCP · Rules · Workflows · Knowledge** manages conversation tools and
  screen input control, approvals, skills, MCP servers (plus the prepared GitHub and Context7 servers), prompt rules, workflows and knowledge. **Applied now** shows the next prompt
  rules. Use skills from their rows; search indexes are maintained automatically.
* Sub-view chips show item counts; zero is dimmed but clickable. Before a choice is made in this conversation, an empty
  current view yields to the first view with items. Lists are tables with switches, badges and row icons, search and pages of
  20; an empty list shows its column header and empty rows without guidance text. Creation actions stay in the card header and
  file-backed lists, including applied changes, keep refresh.

## You stay in control

* Anything that changes files, projects, packages, the debugger, runs a build or tests, or writes to Git asks first, with an exact
  diff when there is one. Allow once, for the session, always (kept in Tools > Options > Pomp), or deny with a note for the agent.
* Edits go into the editor buffer as one undo step per file and are never saved to disk by POMP.
* Safety follows read/write/exec approval tiers, an approval mode that asks every time by default, per-tool
  allow/prompt/deny policies, and bash pattern rules (including a built-in block for critical destructive
  commands). The `delete` and `move` file tools are approval-gated. File-tool paths are checked against the
  solution folder, but there is no OS sandbox for shell or MCP tools. POMP's experimental AppContainer runner stays
  off by default and is not offered as a sandbox.
* The network tools (web search, URL reads, GitHub, browser) are allowed by default and every call is logged; the option
  *Block network tools* turns them off.
* Usage statistics (approvals per tool, edit accuracy, chat load time, restarts) stay in a local file
  (`%LOCALAPPDATA%\Pomp\usage-metrics.json`) and are never sent anywhere.

## License and publisher

POMP itself is source-available under the **PolyForm Noncommercial License 1.0.0**: free for personal and noncommercial
use, with no commercial use permitted. See the package's root `LICENSE.txt` for the full terms. Bundled open-source components
remain under their own licenses, including **MIT**; see the bundled components' `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`
in the package.
The Marketplace publisher ID is **`momopanda123`**, and the extension's price category is **free**. There is no public source
repository yet, so repository/MoreInfo URLs are omitted.

## Requirements

* Visual Studio 2017 15.9, 2019 16.10+, 2022 or 2026 (Community, Professional or Enterprise). Two listings:
  **POMP (VS2017)**, **POMP (VS2019)** and **POMP** (covers both VS 2022 and VS 2026) — install the one for your Visual Studio.
* The WebView2 runtime 111 or later (included with Windows 11 and current Visual Studio installs).
* An account with a model provider (subscription sign-in) or an API key. Nothing else needs to be installed; no terminal is needed.

## Getting started

1. Open **View > Other Windows > POMP** (or Shift+Alt+Q).
2. The Accounts tab opens the first time: sign in with a subscription (OpenAI, Anthropic, Muse Code,
   Google Antigravity or any other listed provider; a browser opens) or add an API key. Once connected, the dot at the top right turns green.
   **Helper model** offers Fast model / Same as the chat model / Choose a specific model for completions, ask edits, captions,
   knowledge conversion and composer predictions. Usage labels are **5 hours / Weekly**; unreported windows are hidden.
3. Select some code and press Shift+Alt+E to explain it, or ask in the chat, then approve the changes you want. Approved
   command jobs show status, output and cancel on their chat tool card; conversations can be deleted after confirmation.

Alt+J moves focus between chat and editor; Alt+M opens Ask POMP, which asks about the selection or caret or proposes an edit to it. Alt+/ adds the selected lines to a knowledge. Shift+Alt+N starts a new conversation.
Every key is set in Tools > Options > Pomp > General > Shortcuts.

The editor's right-click menu shows **Ask POMP**, then **POMP**. The submenu groups
code actions (Explain This Code, Review This Code, Fix This Code, Write Tests), context (Add to Context Basket), sending to
an agent or team, chat (Open POMP, New Conversation, Toggle Editor ↔ Chat Focus), then toggles (Inline Completions, Code Captions).
Ask POMP is not repeated inside it. Shift+Alt+E/R/F/U/I run the code and context actions in a text editor. There is no **Tools > POMP**
menu (everything in it was already in the editor menu or the chat window); **View > Other Windows > POMP** opens the chat window.
