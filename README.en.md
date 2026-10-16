# POMP

[한국어](README.md) | **English**

**An AI coding partner inside Visual Studio.** Chat with an AI agent in a tool window next to the editor and have it read and
change code, build, run tests and debug. Supports Visual Studio 2019 (16.10+), 2022 and 2026.

| Component | Version |
|---|---|
| POMP | 0.5.5 |
| omp | 18.10.4 |

![POMP intro](docs/media/pomp-intro.gif)

<!-- howto-videos -->
## Feature videos

The videos show the Korean UI. Shortcuts are left out because every key can be changed in Tools > Options > Pomp.

**Explain code from the editor** — Select code, right-click › POMP › Explain. The answer appears in the chat.

![Explain code from the editor](docs/media/howto-explain.gif)

**Ask POMP in place** — Ask about the selected lines where they are and send it to the chat.

![Ask POMP in place](docs/media/howto-ask.gif)

**Approve a fix, then the Work tab** — Read the diff on the request card; allowed, it goes into the editor buffer and the Work tab keeps the record.

![Approve a fix, then the Work tab](docs/media/howto-fix.gif)

**Selected lines as knowledge** — Keep selected lines as named knowledge to send along with chats and agents.

![Selected lines as knowledge](docs/media/howto-knowledge.gif)

**Context basket** — Put files into the basket and ask about just those.

![Context basket](docs/media/howto-basket.gif)

<table>
  <tr>
    <td align="center" valign="top" width="50%"><b>Slash commands, @ mentions, permission mode, send target</b><br>Pick commands and files in the input; choose the permission mode and who receives the message.<br><br><img src="docs/media/howto-slash.gif" width="400" alt="Slash commands, @ mentions, permission mode, send target"></td>
    <td align="center" valign="top" width="50%"><b>Delegating to an agent</b><br>Open a role agent as a partner tab and hand it work.<br><br><img src="docs/media/howto-partner.gif" width="400" alt="Delegating to an agent"></td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%"><b>Advisors</b><br>&nbsp;<br>An advisor watches the main agent and comments on its work.<br><br><img src="docs/media/howto-advisor.gif" width="400" alt="Advisors"></td>
    <td align="center" valign="top" width="50%"><b>Role pictures and POMP's instructions</b><br>Give each role a picture and edit POMP's own instructions.<br><br><img src="docs/media/howto-roles.gif" width="400" alt="Role pictures and POMP's instructions"></td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%"><b>Creating a team</b><br>Group roles into a team.<br><br><img src="docs/media/howto-team.gif" width="400" alt="Creating a team"></td>
    <td align="center" valign="top" width="50%"><b>Automation rules</b><br>Attach a notice, a chat message or a script to events such as a failed build.<br><br><img src="docs/media/howto-automation.gif" width="400" alt="Automation rules"></td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%"><b>Tools tab</b><br>&nbsp;<br>Conversation tools, permissions, skills, MCP, rules, workflows and knowledge in one place.<br><br><img src="docs/media/howto-tools.gif" width="400" alt="Tools tab"></td>
    <td align="center" valign="top" width="50%"><b>Accounts, models and thinking level</b><br>Connect accounts, then pick the model and the thinking level under the input.<br><br><img src="docs/media/howto-accounts.gif" width="400" alt="Accounts, models and thinking level"></td>
  </tr>
</table>
<!-- /howto-videos -->

POMP's agent uses Visual Studio itself as its tools: the open editor buffers (including unsaved changes), the solution and its
projects, the Error List, builds, tests, the debugger, NuGet and Git. It asks before it changes or runs anything, and shows
the exact diff of what will change. POMP ships as one VSIX, so there is nothing else to install.

## Features

- **Right from the editor** — select code and use the right-click **POMP** menu or a shortcut to have it explained, reviewed,
  fixed or covered by tests. **Ask POMP** asks about the selection in place: send it to the chat, move it to the input box, or
  get an edit proposal for the selection. Each prompt carries the file and line range it came from. Selected lines can be kept as
  color-marked **knowledge** that goes along with a chat or an agent.
- **Context basket** — put files, folders and symbols into the basket above the input box; the checked ones are the scope of the
  next message and of slash commands and skills. **Include editor context** adds the file and selection you are looking at.
  Frequent sets are saved as presets.
- **Only what you approve is applied** — changes are shown as a diff and go into the editor buffer exactly as approved. One undo
  step per file; saving stays with you. Builds, tests, Git, NuGet and debugger actions also wait for approval. On an approval card
  `Enter` allows and `Esc` denies.
- **Agents and teams** — open role agents such as a reviewer, architect or scout as partner tabs and talk to each one directly.
  An agent can hand parts of a task to other agents. Teams run several roles together; the chat's task card shows each member's
  status and stops members one by one. Every role has a picture (shape, color or image); the bundled roles and POMP's own
  instructions can be edited too. **Advisors** watch the main agent's work beside the conversation and comment; they are switched
  on and off per conversation, with a history of their notes. **Automation** attaches notifications, chat messages or scripts to
  events such as an agent finishing, a failed build or a saved file.
- **Work tab** — **Progress** (plan, workflows, automatic continuation), **Changes** (pending proposals, isolated results, applied
  history and restore) and **Code check** (agent findings plus Visual Studio compiler errors and warnings, sent as one fix request;
  each finding shows the reporting agent's picture and name).
- **Tools tab** — **Conversation · Permissions · Skills · MCP · Rules · Workflows · Knowledge**. Choose the tools for each
  conversation; add, edit and delete skills, rules, workflows and knowledge in the page's tables; add or test MCP servers or
  install the prepared ones (GitHub, Context7). Bundled skills for design, testing, debugging and documentation figures only
  need to be switched on.
- **Several accounts, several models** — sign in with a subscription (OpenAI, Anthropic and others), add an API key, or add a
  model server you run yourself (any OpenAI-compatible address). Pick any connected provider's model per chat and per agent;
  side jobs such as completions, captions and composer predictions use the **Helper model**. Usage limits (5 hours, weekly) are
  shown under the input box and refresh as soon as a window resets.
- **A `.pomp` folder in the project** — project-scoped agents, teams, rules, workflows, skills, hooks and permission policies live
  in `.pomp\` and travel with the repository; POMP's own data such as conversations, knowledge and presets lives in `.pomp\local\`
  (excluded from commits). Deleting the folder resets POMP for that project.

## Screenshots

<table>
  <tr>
    <td align="center"><img src="docs/media/chat-explain.png" width="260" alt="Explaining selected code"><br>Explaining selected code</td>
    <td align="center"><img src="docs/media/approval-card.png" width="260" alt="Approval card"><br>Approval before running</td>
    <td align="center"><img src="docs/media/partner-delegation.png" width="260" alt="Delegating to an agent"><br>Delegating to an agent</td>
  </tr>
</table>

![POMP in Visual Studio](docs/media/vs-overview.png)

The screenshots show the Korean UI. The UI follows the Visual Studio display language; to force English, use
**Tools > Options > Pomp > General > Chat > Language**.

## Install

Requirements
- Windows 10/11 with Visual Studio 2019 16.10+, 2022 or 2026 (Community, Professional or Enterprise)
- Microsoft Edge WebView2 Runtime 111 or later (already present on Windows 11 and with current Visual Studio)
- An AI model provider account (subscription sign-in) or an API key

2017 and 2019 are 32-bit programs, 2022 and 2026 are 64-bit). One 64-bit package covers both 2022 and 2026:

| Package | Name in Manage Extensions | Installs into |
|---|---|---|
| `Pomp.VS2017-<version>.vsix` | POMP (VS2017) | Visual Studio 2017 15.9 (no native CodeLens captions) |
| `Pomp.VS2019-<version>.vsix` | POMP (VS2019) | Visual Studio 2019 16.10 – 16.11 |
| `Pomp.VS2022-<version>.vsix` | POMP (VS2022) | Visual Studio 2022 (17.x) · 2026 (18.x) |

Steps
1. **Visual Studio Marketplace** (listing in preparation): in Visual Studio, open **Extensions > Manage Extensions** and search
   for `POMP`.
2. **VSIX file**: pick the package for your version (table above), close all Visual Studio windows and double-click it. To build
   the packages yourself, run `powershell -NoProfile -File build\build.ps1` (output in `artifacts\`); see the
   [development guide](docs/development.md) (Korean) for prerequisites.
3. Reopen Visual Studio and open **View > Other Windows > POMP** (or `Shift+Alt+Q`).

## Quick start

1. **Connect a provider.** The Accounts tab opens the first time. Sign in with a subscription (pick a provider tile under **Add an account** —
   OpenAI, Anthropic, Muse Code and Google Antigravity come first, search for the rest; a browser opens), enter an API key, which is stored
   encrypted for your Windows user, or add a local model server. Once a provider is connected, the dot at the top right turns green.
2. **Ask about code.** Select a method and press `Shift+Alt+E` (or right-click > **POMP > Explain This Code**). The answer appears
   in the Chat tab; the chip under your prompt shows the file and lines that were sent.
3. **Chat.** `Enter` sends, `Shift+Enter` adds a line, `@` mentions files/folders/symbols, `/` lists commands
   (`/new`, `/sessions`, `/model`, `/effort`, `/permissions`, `/compact`, `/help`). The chips under the input box choose the model, thinking
   level (brain icon), fast mode (lightning) and the permission mode. Messages sent while the agent is answering are queued. Optional
   composer predictions appear after typing pauses; Tab accepts and Esc dismisses.
4. **Approve changes.** When the agent wants to change a file or run something, an approval card shows the diff: **Allow**,
   **Deny**, **Always allow** or **Allow for this session** (the last two apply to every request of that tool, and the card
   says so). Applied edits undo with one `Ctrl+Z` per file; POMP never saves for you.
5. **Hand work to an agent.** Click `+` in the partner tab row of the chat and pick a role (for example **reviewer**). Messages in
   that tab start the agent as a subagent after an approval card; once allowed it runs to the end without asking per tool,
   including any agents it delegates to. Follow member status and individual stop controls on the task card in the chat or
   partner tab. Create roles and teams in **Agents > Roles / Teams**; advisors live in **Advisors**, event rules and project hooks in **Automation**.

## Shortcuts

| Keys | Action |
|---|---|
| `Shift+Alt+Q` | Open POMP |
| `Shift+Alt+N` | New conversation |
| `Shift+Alt+E` / `R` / `F` / `U` | Explain / review / fix / write tests for this code |
| `Shift+Alt+I` | Add the current file to the chat context |
| `Alt+M` | Ask POMP |
| `Alt+/` | Add the selected lines to a knowledge |
| `Alt+J` | Switch focus between editor and chat |
| `Alt+;` / `Alt+'` | Open the context basket / toggle editor context |
| `Ctrl+Alt+T`, `Alt+,` / `Alt+.` | Send-target (agent or team) list / previous and next target |
| `Alt+Q` | Choose the permission mode |
| `Alt+A` / `Alt+D` | Allow / deny the approval card |

Every key, including the chat window's, is set in Tools > Options > Pomp > General > Shortcuts.

## Documentation

The detailed docs are in Korean.

| Document | Contents |
|---|---|
| [Overview](docs/overview.md) | What POMP is, how it works, what it can do, safety model |
| [Quick start](docs/quick-start.md) | From install to the first question, change and delegation |
| [Screen guide](docs/screen-guide.md) | Every tab and button, with real screenshots |
| [Agents and teams](docs/agent-teams.md) | Roles, teams, partners, member stop and automation |
| [Development](docs/development.md) | Build, debugging, layout, implemented scope |
| [Architecture](docs/architecture.md) · [UI protocol](docs/ui-protocol.md) | Implementation reference and internal message formats |
| [Marketplace overview](build/marketplace/overview.md) | English feature and safety summary |

## Safety

- The default approval mode asks every time (`Always ask`). File and project changes, builds, tests, shell commands, Git, NuGet
  and debugger actions run only after approval. "Allow for this session" and "Always allow" stop asking for that tool.
- Allowing a subagent (`task`) lets it run to the end without asking per tool; the approval card says so.
- POMP is not an OS sandbox. Review an untrusted repository's tool rules, MCP settings and other executable configuration
  in the workspace trust card before starting.
- The network tools (web search, URL reads, GitHub, browser) are allowed by default and logged; the option
  *Block network tools* turns them off.
- Usage statistics stay in `%LOCALAPPDATA%\Pomp\usage-metrics.json` on this PC and are never sent anywhere.

## License

**POMP** (this repository's code, documents and UI) is source-available software made and distributed by
**`momopanda123`** under the **PolyForm Noncommercial License 1.0.0**: free for personal and noncommercial use, no
commercial use. See [`LICENSE.txt`](LICENSE.txt). Marketplace publisher ID: **`momopanda123`**.

POMP's conversation and agent engine **omp** is the official release binary of
[oh-my-pi](https://github.com/can1357/oh-my-pi) (**MIT licensed**), and POMP's PolyForm license does not
apply to it. The VSIX's `omp/THIRD-PARTY-NOTICES.txt` carries the licenses of omp itself and its bundled open-source
components (MIT and others). Other bundled open-source components keep their own licenses as well (see the bundled
components' `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` in the package).

Other companies' product and service names in these documents and in the product are trademarks of their owners. POMP is not
affiliated with them and uses the names only to refer to what it works with.
