using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// Workspace trust (docs/architecture.md "작업 영역 신뢰"). omp (18.4.3 through 18.10.4) applies no project trust: from the workspace it reads settings
    /// that can auto-approve tools or redirect endpoints, starts MCP servers (with the engine's environment, API keys included), imports
    /// tool code, and inlines whatever its context files include with <c>@path</c>, inside the workspace or not. POMP starts omp in a
    /// workspace that has any of these only after the user trusted exactly that set for that folder; a changed set asks again.
    /// </summary>
    internal sealed class WorkspaceTrust
    {
        private const int MaxListed = 200, MaxDepth = 4, MaxFolders = 2000, MaxIncludeDepth = 5, MaxIncludeFiles = 40;
        /// <summary>The trusted-workspace list has no count limit: the newest trusts stay while the file fits this many bytes.</summary>
        private const int MaxStoreBytes = 1024 * 1024;
        private const long MaxHashBytes = 16L * 1024 * 1024, MaxTextBytes = 1024 * 1024;

        // Single files omp reads from the workspace: settings layers (can set tools.approval.*, bash.patterns, shellPath, endpoints),
        // MCP server lists of every provider, project plugin manifests, and LSP server lists.
        private static readonly (string Path, string Kind)[] Files = new[]
        {
            ("WATCHDOG.yml", "advisors"), ("WATCHDOG.yaml", "advisors"), (".omp/WATCHDOG.yml", "advisors"), (".omp/WATCHDOG.yaml", "advisors"),
            (".omp/config.yml", "settings"), (".omp/settings.json", "settings"), (".claude/settings.json", "settings"), (".cursor/settings.json", "settings"),
            (".gemini/settings.json", "settings"), ("opencode.json", "settings"), ("opencode.jsonc", "settings"), (".opencode/opencode.json", "settings"),
            (".opencode/opencode.jsonc", "settings"), (".codex/config.toml", "settings"),
            (".omp/mcp.json", "mcp"), (".omp/.mcp.json", "mcp"), ("mcp.json", "mcp"), (".mcp.json", "mcp"), (".claude/.mcp.json", "mcp"), (".claude/mcp.json", "mcp"),
            (".cursor/mcp.json", "mcp"), (".windsurf/mcp_config.json", "mcp"), (".vscode/mcp.json", "mcp"),
            (".omp/plugins/package.json", "plugins"), (".omp/plugins/installed_plugins.json", "plugins"), (".omp/plugins/omp-plugins.lock.json", "plugins"), (".omp/plugin-overrides.json", "plugins"),
        }.Concat(
            from folder in new[] { "", ".omp/", ".claude/", ".codex/", ".gemini/" }
            from name in new[] { "lsp.json", ".lsp.json", "lsp.yaml", ".lsp.yaml", "lsp.yml", ".lsp.yml" }
            select (folder + name, "lsp")).ToArray();

        // Script files omp imports as tools in every session, and (only when POMP loads discovered extensions) as extensions, hooks or commands.
        private static readonly string[] ToolFolders = { ".omp/tools", ".claude/tools", ".codex/tools" };
        private static readonly string[] ExtensionFolders = { ".omp/extensions", ".omp/hooks", ".claude/hooks", ".codex/hooks", ".omp/commands", ".claude/commands", ".codex/commands", ".gemini/commands" };

        // Language servers omp starts on first use (editing a matching file, the lsp tool): before PATH, it takes a default server's command
        // from the workspace's own tool folders when their marker files are in the root (omp 18.4.3, 18.8.6, 18.10.3 and 18.10.4 lsp/config.ts and lsp/defaults.json;
        // both tables follow the bundled engine).
        private static readonly string[] PythonMarkers = { "pyproject.toml", "ty.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "pyrightconfig.json", "ruff.toml", ".ruff.toml" };
        private static readonly (string[] Markers, string Folder)[] LocalToolFolders =
        {
            (new[] { "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml" }, "node_modules/.bin"),
            (PythonMarkers, ".venv/bin"), (PythonMarkers, ".venv/Scripts"), (PythonMarkers, "venv/bin"), (PythonMarkers, "venv/Scripts"),
            (PythonMarkers, ".env/bin"), (PythonMarkers, ".env/Scripts"),
            (new[] { "Gemfile", "Gemfile.lock" }, "vendor/bundle/bin"), (new[] { "Gemfile", "Gemfile.lock" }, "bin"),
            (new[] { "go.mod", "go.sum", "go.work" }, "bin"),
        };
        private static readonly (string Command, string[] Markers)[] LanguageServers =
        {
            ("rust-analyzer", new[] { "Cargo.toml", "rust-analyzer.toml" }),
            ("tlapm_lsp", new[] { "*.tla" }),
            ("clangd", new[] { "compile_commands.json", "CMakeLists.txt", ".clangd", ".clang-format", "Makefile" }),
            ("zls", new[] { "build.zig", "build.zig.zon", "zls.json" }),
            ("gopls", new[] { "go.mod", "go.work", "go.sum" }),
            ("typescript-language-server", new[] { "package.json", "tsconfig.json", "jsconfig.json" }),
            ("tsc", new[] { "package.json", "tsconfig.json", "jsconfig.json" }),
            ("biome", new[] { "biome.json", "biome.jsonc" }),
            ("vscode-eslint-language-server", new[] { ".eslintrc", ".eslintrc.js", ".eslintrc.json", ".eslintrc.yml", "eslint.config.js", "eslint.config.mjs" }),
            ("deno", new[] { "deno.json", "deno.jsonc", "deno.lock" }),
            ("vscode-html-language-server", new[] { "package.json", ".git" }),
            ("vscode-css-language-server", new[] { "package.json", ".git" }),
            ("vscode-json-language-server", new[] { "package.json", ".git" }),
            ("tailwindcss-language-server", new[] { "tailwind.config.js", "tailwind.config.ts", "tailwind.config.mjs", "tailwind.config.cjs" }),
            ("svelteserver", new[] { "svelte.config.js", "svelte.config.mjs", "package.json" }),
            ("vue-language-server", new[] { "vue.config.js", "nuxt.config.js", "nuxt.config.ts", "package.json" }),
            ("astro-ls", new[] { "astro.config.mjs", "astro.config.js", "astro.config.ts" }),
            ("pyright-langserver", new[] { "pyproject.toml", "pyrightconfig.json", "setup.py", "setup.cfg", "requirements.txt", "Pipfile" }),
            ("basedpyright-langserver", new[] { "pyproject.toml", "pyrightconfig.json", "setup.py", "requirements.txt" }),
            ("pylsp", new[] { "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile" }),
            ("ty", new[] { "pyproject.toml", "ty.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile" }),
            ("ruff", new[] { "pyproject.toml", "ruff.toml", ".ruff.toml" }),
            ("jdtls", new[] { "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", ".project" }),
            ("kotlin-lsp", new[] { "build.gradle", "build.gradle.kts", "pom.xml", "settings.gradle", "settings.gradle.kts" }),
            ("metals", new[] { "build.sbt", "build.sc", "build.gradle", "pom.xml" }),
            ("haskell-language-server-wrapper", new[] { "stack.yaml", "cabal.project", "hie.yaml", "package.yaml", "*.cabal" }),
            ("ocamllsp", new[] { "dune-project", "dune-workspace", "*.opam", ".ocamlformat" }),
            ("elixir-ls", new[] { "mix.exs", "mix.lock" }),
            ("expert", new[] { "mix.exs", "mix.lock" }),
            ("erlang_ls", new[] { "rebar.config", "erlang.mk", "rebar.lock" }),
            ("gleam", new[] { "gleam.toml" }),
            ("solargraph", new[] { "Gemfile", ".solargraph.yml", "Rakefile" }),
            ("ruby-lsp", new[] { "Gemfile", ".ruby-version", ".ruby-gemset" }),
            ("rubocop", new[] { ".rubocop.yml", "Gemfile" }),
            ("bash-language-server", new[] { ".git" }),
            ("lua-language-server", new[] { ".luarc.json", ".luarc.jsonc", ".luacheckrc", ".stylua.toml", "stylua.toml" }),
            ("intelephense", new[] { "composer.json", "composer.lock", ".git" }),
            ("phpactor", new[] { "composer.json", ".phpactor.json", ".phpactor.yml" }),
            ("omnisharp", new[] { "*.sln", "*.csproj", "omnisharp.json", ".git" }),
            ("yaml-language-server", new[] { ".git" }),
            ("terraform-ls", new[] { ".terraform", "terraform.tfstate", "*.tf" }),
            ("docker-langserver", new[] { "Dockerfile", "docker-compose.yml", "docker-compose.yaml", ".dockerignore" }),
            ("helm_ls", new[] { "Chart.yaml", "Chart.yml" }),
            ("nixd", new[] { "flake.nix", "default.nix", "shell.nix" }),
            ("nil", new[] { "flake.nix", "default.nix", "shell.nix" }),
            ("ols", new[] { "ols.json", ".git" }),
            ("dart", new[] { "pubspec.yaml", "pubspec.lock" }),
            ("marksman", new[] { ".marksman.toml", ".git" }),
            ("texlab", new[] { ".latexmkrc", "latexmkrc", ".texlabroot", "texlabroot", "Tectonic.toml" }),
            ("graphql-lsp", new[] { ".graphqlrc", ".graphqlrc.json", ".graphqlrc.yml", ".graphqlrc.yaml", "graphql.config.js" }),
            ("prisma-language-server", new[] { "schema.prisma", "prisma/schema.prisma" }),
            ("vim-language-server", new[] { ".git" }),
            ("emmet-language-server", new[] { ".git" }),
            ("sourcekit-lsp", new[] { "Package.swift", "*.xcodeproj", "*.xcworkspace", "project.yml", ".swiftpm" }),
            ("swiftlint", new[] { ".swiftlint.yml", ".swiftlint.yaml", "Package.swift", "*.xcodeproj" }),
        };

        // Context and system-prompt files whose @path includes omp expands with no containment (absolute, ~, ..).
        private static readonly string[] ContextFiles =
        {
            "WATCHDOG.yml", "WATCHDOG.yaml", ".omp/WATCHDOG.yml", ".omp/WATCHDOG.yaml",
            "AGENTS.md", "CLAUDE.md", "GEMINI.md", ".omp/AGENTS.md", ".claude/CLAUDE.md", ".agent/AGENTS.md", ".agents/AGENTS.md", ".github/copilot-instructions.md",
            ".omp/SYSTEM.md", ".omp/SYSTEM_TEMPLATE.md", ".claude/SYSTEM.md", ".codex/SYSTEM.md", ".gemini/system.md", ".agent/SYSTEM.md", ".agents/SYSTEM.md",
        };

        // omp's own include syntax (discovery/at-imports.ts): "@path" at a line start or after a blank, outside code spans and fences.
        private static readonly Regex Include = new Regex(@"(^|[ \t])@([./~A-Za-z0-9_-][^\s]*)", RegexOptions.CultureInvariant);
        private static readonly Regex TrailingPunctuation = new Regex(@"[.,;:!?)\]}""']+$", RegexOptions.CultureInvariant);
        private static readonly Regex Script = new Regex(@"\.(?:ts|js|mjs|cjs)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly string _store;
        private readonly string _home;
        private readonly IPompLogger _logger;
        private readonly object _gate = new object();

        /// <param name="dataDirectory">POMP's data folder: the trust list lives there, outside every workspace.</param>
        /// <param name="home">The user profile (what <c>~</c> means in an include).</param>
        internal WorkspaceTrust(string dataDirectory, string home, IPompLogger logger)
        {
            _store = Path.Combine(dataDirectory, "workspace-trust.json");
            _home = home;
            _logger = logger;
        }

        /// <summary>What omp would load from <paramref name="root"/> now, and whether the user trusted exactly that.</summary>
        internal WorkspaceTrustStatePayload Evaluate(string root, bool includeExtensions)
        {
            var normalized = Normalize(root);
            var state = new WorkspaceTrustStatePayload { Root = normalized };
            if (normalized.Length == 0 || !Directory.Exists(normalized)) return state;
            var entries = Scan(normalized, includeExtensions, out var truncated);
            state.Required = entries.Count > 0;
            state.Truncated = truncated || entries.Count > MaxListed;
            state.Files = entries.Take(MaxListed).Select(e => new WorkspaceTrustFilePayload { Path = e.Path, Kind = e.Kind, Detail = e.Detail }).ToList();
            if (!state.Required) return state;
            state.Fingerprint = Fingerprint(entries);
            lock (_gate) state.Trusted = Load().Any(w => Same(w.Root, normalized) && w.Fingerprint == state.Fingerprint);
            return state;
        }

        /// <summary>Records trust in the set the user saw (<paramref name="fingerprint"/>); refuses when the files changed since.</summary>
        internal WorkspaceTrustStatePayload Trust(string root, string? fingerprint, bool includeExtensions)
        {
            var state = Evaluate(root, includeExtensions);
            if (!state.Required) return state;
            if (string.IsNullOrEmpty(fingerprint) || fingerprint != state.Fingerprint)
                throw new InvalidOperationException(PompLanguage.Pick("그 사이 설정 파일이 바뀌었어요.", "The configuration changed meanwhile."));
            lock (_gate)
            {
                var all = Load().Where(w => !Same(w.Root, state.Root)).ToList();
                all.Insert(0, new TrustedWorkspace { Root = state.Root, Fingerprint = state.Fingerprint, TrustedUtc = DateTimeOffset.UtcNow.ToString("O"), Files = state.Files.Count });
                Save(all);
            }

            _logger.Info("The user trusted the engine configuration of " + state.Root + " (" + state.Files.Count + " files).");
            state.Trusted = true;
            return state;
        }

        /// <summary>The set as it is before POMP changes a file in it for the user (Tools › 확장 › MCP); see <see cref="KeepAfterOwnWrite"/>.</summary>
        internal Snapshot Before(string root, bool includeExtensions)
        {
            var normalized = Normalize(root);
            if (normalized.Length == 0 || !Directory.Exists(normalized)) return new Snapshot(normalized, includeExtensions, false, new List<Entry>());
            var entries = Scan(normalized, includeExtensions, out _);
            var fingerprint = Fingerprint(entries);
            bool accepted;
            lock (_gate) accepted = entries.Count == 0 || Load().Any(w => Same(w.Root, normalized) && w.Fingerprint == fingerprint);
            return new Snapshot(normalized, includeExtensions, accepted, entries);
        }

        /// <summary>
        /// After POMP wrote <paramref name="relativePath"/> on the user's request in its own editor: when the set before needed no trust
        /// or was trusted, and nothing else in it changed, the new set is trusted too, so the user's own change does not ask again.
        /// A set the user never trusted stays untrusted, and any other change (an agent's edit, a new file) still asks.
        /// </summary>
        internal void KeepAfterOwnWrite(Snapshot before, string relativePath)
        {
            if (!before.Accepted) return;
            var entries = Scan(before.Root, before.IncludeExtensions, out _);
            if (entries.Count == 0) return;
            var fingerprint = Fingerprint(entries);
            if (fingerprint == Fingerprint(before.Entries)) return;
            IEnumerable<string> Others(IEnumerable<Entry> list) =>
                list.Where(e => !string.Equals(e.Path, relativePath, StringComparison.OrdinalIgnoreCase)).Select(e => e.Kind + "\0" + e.Path + "\0" + e.Hash + "\0" + e.Detail);
            if (!Others(before.Entries).SequenceEqual(Others(entries))) return;
            try
            {
                lock (_gate)
                {
                    var all = Load().Where(w => !Same(w.Root, before.Root)).ToList();
                    all.Insert(0, new TrustedWorkspace { Root = before.Root, Fingerprint = fingerprint, TrustedUtc = DateTimeOffset.UtcNow.ToString("O"), Files = Math.Min(entries.Count, MaxListed) });
                    Save(all);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                // Not kept: the chat asks for trust again, which is the safe side.
                _logger.Warn("Could not keep the workspace trust after a change in POMP: " + ex.Message);
                return;
            }

            _logger.Info("Kept the trust of " + before.Root + " after the user changed " + relativePath + " in POMP.");
        }

        /// <summary>Forgets the trust of <paramref name="root"/>; true when there was one.</summary>
        internal bool Revoke(string root)
        {
            var normalized = Normalize(root);
            lock (_gate)
            {
                var all = Load();
                var kept = all.Where(w => !Same(w.Root, normalized)).ToList();
                if (kept.Count == all.Count) return false;
                Save(kept);
            }

            _logger.Info("The trust of " + normalized + " was revoked.");
            return true;
        }

        /// <summary>Trusted workspaces, newest first (for Tools › 권한).</summary>
        internal IReadOnlyList<TrustedWorkspace> List()
        {
            lock (_gate) return Load();
        }

        /// <summary>The full path of a file the last scan listed for <paramref name="root"/> (for opening it in the editor); null for anything else.</summary>
        internal string? FileOf(string root, string relativePath, bool includeExtensions)
        {
            var state = Evaluate(root, includeExtensions);
            if (!state.Files.Any(f => string.Equals(f.Path, relativePath, StringComparison.OrdinalIgnoreCase))) return null;
            var full = Path.GetFullPath(Path.Combine(state.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return Inside(state.Root, full) && File.Exists(full) ? full : null;
        }

        // ---- scanning ------------------------------------------------------------------------------------------------------

        internal sealed class Entry
        {
            public Entry(string path, string kind, string? detail, string hash) { Path = path; Kind = kind; Detail = detail; Hash = hash; }
            public string Path { get; }
            public string Kind { get; }
            public string? Detail { get; }
            public string Hash { get; }
        }

        /// <summary>One scan of a workspace, kept across a change POMP makes (<see cref="Before"/>).</summary>
        internal sealed class Snapshot
        {
            internal Snapshot(string root, bool includeExtensions, bool accepted, List<Entry> entries) { Root = root; IncludeExtensions = includeExtensions; Accepted = accepted; Entries = entries; }
            internal string Root { get; }
            internal bool IncludeExtensions { get; }
            /// <summary>The set needed no trust, or the user trusted exactly it.</summary>
            internal bool Accepted { get; }
            internal IReadOnlyList<Entry> Entries { get; }
        }

        private List<Entry> Scan(string root, bool includeExtensions, out bool truncated)
        {
            var entries = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var limited = false;
            void Add(string relative, string kind, string? detail)
            {
                if (!seen.Add(relative + "\0" + detail)) return;
                if (entries.Count >= MaxListed * 2) { limited = true; return; }
                entries.Add(new Entry(relative, kind, detail, HashOf(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))));
            }

            foreach (var (relative, kind) in Files)
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) Add(relative, kind, kind == "mcp" || kind == "settings" ? ServerCount(full) : null);
            }

            foreach (var folder in ToolFolders) foreach (var file in Scripts(root, folder, false)) Add(file, "tools", null);
            foreach (var file in LanguageServerPrograms(root)) Add(file, "lsp", null);
            if (includeExtensions) foreach (var folder in ExtensionFolders) foreach (var file in Scripts(root, folder, true)) Add(file, "extensions", null);
            foreach (var (file, target) in OutsideIncludes(root)) Add(file, "include", target);

            truncated = limited;
            return entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Detail, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Script files (and, for extensions, package manifests) under <paramref name="folder"/>, bounded in depth and count. A folder too
        /// big to walk (e.g. a link to a drive) is reported itself, so it still needs trust.
        /// </summary>
        private static IEnumerable<string> Scripts(string root, string folder, bool manifests)
        {
            var start = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(start)) yield break;
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((start, 0));
            var found = 0;
            var visited = 0;
            while (pending.Count > 0 && found < MaxListed)
            {
                if (++visited > MaxFolders)
                {
                    yield return folder;
                    yield break;
                }

                var (current, depth) = pending.Pop();
                string[] files, folders;
                try
                {
                    files = Directory.GetFiles(current);
                    folders = depth < MaxDepth ? Directory.GetDirectories(current) : Array.Empty<string>();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(file);
                    if ((Script.IsMatch(name) && !name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)) || (manifests && name.Equals("package.json", StringComparison.OrdinalIgnoreCase)))
                    {
                        found++;
                        yield return Relative(root, file);
                    }
                }

                foreach (var child in folders.OrderByDescending(f => f, StringComparer.Ordinal))
                    if (!Path.GetFileName(child).Equals("node_modules", StringComparison.OrdinalIgnoreCase)) pending.Push((child, depth + 1));
            }
        }

        /// <summary>The workspace's own programs omp would start as a default language server: per server, the first match as omp resolves it.</summary>
        private static IEnumerable<string> LanguageServerPrograms(string root)
        {
            string[] names;
            try
            {
                names = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToArray()!;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }

            // omp checks a marker by its existence in the root (file or folder); "*" markers match the root's entry names.
            bool Present(string[] markers) => markers.Any(marker => marker.IndexOf('*') >= 0
                ? names.Any(name => Regex.IsMatch(name, "^" + Regex.Escape(marker).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                : File.Exists(Path.Combine(root, marker.Replace('/', Path.DirectorySeparatorChar))) || Directory.Exists(Path.Combine(root, marker.Replace('/', Path.DirectorySeparatorChar))));

            var folders = LocalToolFolders.Where(f => Present(f.Markers)).Select(f => Path.Combine(root, f.Folder.Replace('/', Path.DirectorySeparatorChar))).ToList();
            var found = new List<string>();
            if (folders.Count == 0) return found;
            foreach (var (command, markers) in LanguageServers)
            {
                if (!Present(markers)) continue;
                var program = folders.SelectMany(folder => new[] { "", ".exe", ".cmd", ".bat" }.Select(extension => Path.Combine(folder, command + extension))).FirstOrDefault(File.Exists);
                if (program != null) found.Add(Relative(root, program));
            }

            return found;
        }

        /// <summary>Context files (or files they include from inside the workspace) that include a file outside it, with that file.</summary>
        private IEnumerable<(string File, string Target)> OutsideIncludes(string root)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<(string Path, int Depth)>(ContextFiles.Select(f => (Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar)), 0)));
            while (pending.Count > 0 && visited.Count < MaxIncludeFiles)
            {
                var (file, depth) = pending.Dequeue();
                if (!visited.Add(file) || !File.Exists(file)) continue;
                string text;
                try
                {
                    if (new FileInfo(file).Length > MaxTextBytes) continue;
                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var target in Includes(text, Path.GetDirectoryName(file)!))
                {
                    if (!Inside(root, target)) { if (File.Exists(target)) yield return (Relative(root, file), Display(target)); }
                    else if (depth + 1 < MaxIncludeDepth) pending.Enqueue((target, depth + 1));
                }
            }
        }

        /// <summary>The paths omp's include expansion would resolve in <paramref name="text"/> (fences and inline code skipped, as omp does).</summary>
        private IEnumerable<string> Includes(string text, string directory)
        {
            char fence = '\0';
            var fenceLength = 0;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.TrimStart(' ', '\t');
                var run = trimmed.Length > 0 && (trimmed[0] == '`' || trimmed[0] == '~') ? trimmed.TakeWhile(c => c == trimmed[0]).Count() : 0;
                if (run >= 3)
                {
                    if (fence == '\0') { fence = trimmed[0]; fenceLength = run; continue; }
                    if (trimmed[0] == fence && run >= fenceLength) { fence = '\0'; continue; }
                }

                if (fence != '\0' || line.IndexOf('@') < 0) continue;
                foreach (Match match in Include.Matches(line))
                {
                    var at = match.Index + match.Groups[1].Length;
                    if (line.Take(at).Count(c => c == '`') % 2 == 1) continue; // inside an inline code span (close enough to omp's toggle)
                    var path = TrailingPunctuation.Replace(match.Groups[2].Value, "");
                    if (path.Length == 0) continue;
                    string resolved;
                    try
                    {
                        resolved = path == "~" ? _home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(_home, path.Substring(2)) : Path.IsPathRooted(path) ? path : Path.Combine(directory, path);
                        resolved = Path.GetFullPath(resolved);
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                    {
                        continue;
                    }

                    yield return resolved;
                }
            }
        }

        private string Display(string path) =>
            Inside(_home, path) ? "~" + path.Substring(Normalize(_home).Length).Replace('\\', '/') : path;

        /// <summary>"MCP 서버 N개" for JSON configuration that lists MCP servers; null when it lists none or cannot be read.</summary>
        private static string? ServerCount(string file)
        {
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                if (new FileInfo(file).Length > MaxTextBytes) return null;
                using var reader = new JsonTextReader(new StringReader(File.ReadAllText(file))) { MaxDepth = 64 };
                var json = JObject.Load(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                var servers = json["mcpServers"] as JObject ?? json["servers"] as JObject ?? (json["mcp"] as JObject)?["servers"] as JObject ?? json["mcp"] as JObject;
                return servers == null || servers.Count == 0 ? null : PompLanguage.Pick("MCP 서버 " + servers.Count + "개", servers.Count + " MCP server" + (servers.Count == 1 ? "" : "s"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        /// <summary>SHA-256 of the whole file, bounded; a marker when it cannot be read (still a change when it becomes readable).</summary>
        private static string HashOf(string file)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // A large program (a language server can be tens of MB) is pinned by size and write time instead of its content.
                if (stream.Length > MaxHashBytes) return "large:" + stream.Length + ":" + File.GetLastWriteTimeUtc(file).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var sha = SHA256.Create();
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "unreadable";
            }
        }

        private static string Fingerprint(IEnumerable<Entry> entries)
        {
            var text = new StringBuilder();
            foreach (var entry in entries) text.Append(entry.Kind).Append('\0').Append(entry.Path).Append('\0').Append(entry.Hash).Append('\0').Append(entry.Detail).Append('\n');
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        // ---- the trust list --------------------------------------------------------------------------------------------------

        internal sealed class TrustedWorkspace
        {
            public string Root { get; set; } = string.Empty;
            public string Fingerprint { get; set; } = string.Empty;
            public string TrustedUtc { get; set; } = string.Empty;
            public int Files { get; set; }
        }

        private List<TrustedWorkspace> Load()
        {
            try
            {
                CheckpointStore.CheckDirectPath(_store);
                if (!File.Exists(_store)) return new List<TrustedWorkspace>();
                var json = JObject.Parse(File.ReadAllText(_store));
                return (json["workspaces"] as JArray ?? new JArray()).OfType<JObject>()
                    .Select(w => new TrustedWorkspace { Root = (string?)w["root"] ?? "", Fingerprint = (string?)w["fingerprint"] ?? "", TrustedUtc = (string?)w["trustedUtc"] ?? "", Files = (int?)w["files"] ?? 0 })
                    .Where(w => w.Root.Length > 0 && w.Fingerprint.Length > 0).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException || ex is ArgumentException)
            {
                // An unreadable list trusts nothing (fail closed); the next trust rewrites it.
                _logger.Warn("Could not read the workspace trust list: " + ex.Message);
                return new List<TrustedWorkspace>();
            }
        }

        private void Save(List<TrustedWorkspace> all)
        {
            CheckpointStore.CheckDirectPath(_store);
            Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
            string Compose() => new JObject
            {
                ["version"] = 1,
                ["workspaces"] = new JArray(all.Select(w => new JObject { ["root"] = w.Root, ["fingerprint"] = w.Fingerprint, ["trustedUtc"] = w.TrustedUtc, ["files"] = w.Files })),
            }.ToString(Formatting.Indented);
            var text = Compose();
            // The oldest trusts go first when the file would pass its size (they are asked again, never assumed).
            while (all.Count > 1 && System.Text.Encoding.UTF8.GetByteCount(text) > MaxStoreBytes) { all.RemoveRange(Math.Max(1, all.Count - Math.Max(1, all.Count / 20)), Math.Min(all.Count - 1, Math.Max(1, all.Count / 20))); text = Compose(); }
            AtomicFile.WriteAllText(_store, text);
        }

        // ---- paths -----------------------------------------------------------------------------------------------------------

        private static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return string.Empty; }
        }

        private static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        private static bool Inside(string root, string path)
        {
            var prefix = Normalize(root) + Path.DirectorySeparatorChar;
            var full = Normalize(path);
            return full.Length > 0 && (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Same(full, root));
        }

        private static string Relative(string root, string file) => Normalize(file).Substring(Normalize(root).Length + 1).Replace('\\', '/');
    }
}
