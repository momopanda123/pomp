using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>Enables or removes one workspace's model-free bundled tools in Antigravity, without starting either application.</summary>
    public sealed class AntigravityMcpSetup
    {
        internal const int MaximumBytes = 1024 * 1024;
        private static readonly string[] CliExecutables = { "agy.exe", "agy.cmd", "agy.bat", "agy" };
        private readonly string? root;
        private readonly string home;
        private readonly string extensionDirectory;
        private readonly Func<string?> installationLocator;
        private sealed class Snapshot
        {
            internal string Path = "", Hash = "missing";
            internal JObject Json = new JObject();
            internal bool Exists, Bom;
            internal string Revision => ToolResourceFiles.Hash(Path + "\n" + Hash);
        }

        /// <param name="workspaceRoot">An actual project directory, never the host's internal fallback workspace.</param>
        /// <param name="userHome">User profile whose Antigravity configuration is edited; null selects the current profile.</param>
        /// <param name="extensionDirectory">Installed POMP directory containing the verified omp/engine.json and omp/omp.exe.</param>
        /// <param name="installationLocator">Optional installation lookup for isolated filesystem tests. The returned executable must exist.</param>
        public AntigravityMcpSetup(string? workspaceRoot, string? userHome, string extensionDirectory, Func<string?>? installationLocator = null)
        {
            root = string.IsNullOrWhiteSpace(workspaceRoot) ? null : NormalizeRoot(workspaceRoot!);
            home = ToolResourceFiles.Home(userHome);
            this.extensionDirectory = extensionDirectory;
            this.installationLocator = installationLocator ?? FindInstallation;
        }

        /// <summary>Read-only prerequisite/configuration snapshot. No credentials, model API, app or engine process is accessed.</summary>
        public AntigravityMcpStatePayload Inspect()
        {
            lock (ToolResourceFiles.Gate)
            {
                var view = new AntigravityMcpStatePayload { WorkspaceRoot = root, ServerName = root == null ? null : ServerName(root) };
                try
                {
                    view.ConfigPath = ConfigPath();
                    var file = Read(view.ConfigPath);
                    view.ConfigHash = file.Revision;
                    var existing = view.ServerName == null ? null : (file.Json["mcpServers"] as JObject)?[view.ServerName];
                    if (existing != null && !Owns(existing)) throw new InvalidOperationException(CollisionMessage());
                    view.Enabled = existing != null;
                    view.CanDisable = view.Enabled;
                    view.ApplicationPath = ExistingInstallation(installationLocator());
                    view.Installed = view.ApplicationPath != null;
                    var bundle = BundledEngine.Inspect(extensionDirectory);
                    view.Configured = existing is JObject server && bundle.Status == BundledEngineStatus.Verified &&
                        string.Equals((string?)server["command"], bundle.Path, StringComparison.OrdinalIgnoreCase) && JToken.DeepEquals(server["args"], Arguments(root!));
                    view.Message = !view.Installed ? PompLanguage.Pick("Antigravity 공식 앱 또는 agy CLI를 먼저 설치해 주세요.", "Install the official Antigravity app or agy CLI first.") :
                        root == null || !Directory.Exists(root) ? PompLanguage.Pick("실제 프로젝트 폴더를 먼저 열어 주세요. POMP 내부 작업 폴더는 사용할 수 없어요.", "Open an actual project folder first; POMP's internal workspace cannot be used.") :
                        bundle.Status != BundledEngineStatus.Verified ? PompLanguage.Pick("검증된 POMP 번들 엔진이 필요해요. POMP를 다시 설치해 주세요.", "A verified POMP bundled engine is required. Reinstall POMP.") : null;
                    view.CanConfigure = view.Message == null;
                    if (view.Message == null) view.Message = view.Configured ? ConfiguredMessage() : PompLanguage.Pick(
                        "이 프로젝트의 MCP를 설정할 수 있어요. 파일 편집과 OS 명령 실행이 허용되며 작업 폴더는 샌드박스가 아니에요.",
                        "Ready to configure this project's MCP. File edits and OS commands are enabled; the working directory is not a sandbox.");
                }
                catch (Exception ex) when (IsConfigurationError(ex))
                {
                    view.CanConfigure = false;
                    view.Message = PompLanguage.Pick("Antigravity 설정을 변경할 수 없어요: ", "Cannot change Antigravity configuration: ") + SafeProblem(ex);
                }
                return view;
            }
        }

        /// <summary>Atomically enables or removes only the snapshot's owned workspace entry; stale, malformed and foreign entries are refused unchanged.</summary>
        public AntigravityMcpStatePayload SetEnabled(string? expectedName, string? expectedHash, bool enabled)
        {
            lock (ToolResourceFiles.Gate)
            {
                if (root == null || !string.Equals(expectedName, ServerName(root), StringComparison.Ordinal))
                    throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요. 다시 확인해 주세요.", "The workspace changed. Refresh before configuring."));
                var view = Inspect();
                if (enabled && !view.CanConfigure) throw new InvalidOperationException(view.Message);
                if (expectedHash == null || !string.Equals(expectedHash, view.ConfigHash, StringComparison.Ordinal)) throw Changed();
                var file = Read(ConfigPath());
                if (!string.Equals(file.Revision, expectedHash, StringComparison.Ordinal)) throw Changed();
                var servers = file.Json["mcpServers"] as JObject ?? new JObject();
                var existing = servers[expectedName!];
                if (existing != null && !Owns(existing)) throw new InvalidOperationException(CollisionMessage());
                if (!enabled)
                {
                    // An already absent entry is a true no-op, even when no configuration file exists.
                    if (existing == null) return view;
                    servers.Remove(expectedName!);
                }
                else
                {
                    var bundle = BundledEngine.Inspect(extensionDirectory);
                    if (bundle.Status != BundledEngineStatus.Verified) throw new InvalidOperationException("The bundled POMP engine is no longer verified.");
                    var server = existing as JObject ?? new JObject { ["timeoutSeconds"] = 600 };
                    var args = Arguments(root);
                    // A repeat click is a true no-op, including original whitespace, BOM and write time.
                    if (server["command"]?.Type == JTokenType.String && (string?)server["command"] == bundle.Path && JToken.DeepEquals(server["args"], args)) return view;
                    server["command"] = bundle.Path;
                    server["args"] = args;
                    servers[expectedName!] = server;
                    file.Json["mcpServers"] = servers;
                }
                var content = (file.Bom ? "\uFEFF" : "") + file.Json.ToString(Formatting.Indented) + Environment.NewLine;
                var bytes = ToolResourceFiles.Encode(content, MaximumBytes, "Antigravity 설정", "Antigravity configuration");
                Commit(file, bytes);
                view = Inspect();
                if (!enabled) view.Message = PompLanguage.Pick(
                    "이 프로젝트의 POMP 도구 설정을 제거했어요. Antigravity 앱 또는 agy CLI에서 MCP를 다시 불러와야 할 수 있어요. 연결 상태는 확인하지 않았어요.",
                    "This project's POMP tools configuration was removed. Antigravity or agy CLI may need an MCP reload. Connection state has not been verified.");
                return view;
            }
        }

        private string ConfigPath()
        {
            var canonical = Path.Combine(home, ".gemini", "config", "mcp_config.json");
            CheckpointStore.CheckDirectPath(canonical);
            return canonical;
        }

        private static Snapshot Read(string path)
        {
            CheckpointStore.CheckDirectPath(path);
            if (Directory.Exists(path)) throw new IOException("The MCP configuration path is a directory.");
            var file = new Snapshot { Path = path, Exists = File.Exists(path) };
            if (!file.Exists) return file;
            var bytes = ToolResourceFiles.Read(path, MaximumBytes);
            file.Hash = ToolResourceFiles.Hash(bytes);
            file.Bom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
            var text = ToolResourceFiles.Text(bytes);
            if (string.IsNullOrWhiteSpace(text)) return file;
            using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
            file.Json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonException("Trailing MCP configuration content.");
            if (file.Json["mcpServers"] != null && file.Json["mcpServers"] is not JObject) throw new JsonException("mcpServers is not an object.");
            return file;
        }

        private void Commit(Snapshot file, byte[] bytes)
        {
            var directory = Path.GetDirectoryName(file.Path)!;
            CheckpointStore.CheckDirectPath(file.Path);
            Directory.CreateDirectory(directory);
            CheckpointStore.CheckDirectPath(file.Path);
            var temporary = Path.Combine(directory, ".pomp-antigravity-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                // Recheck after preparing the temp file, immediately before the atomic swap. Close read handles
                // before replacing the destination, matching ToolResourceFiles.Replace on Windows.
                if (ConfigPath() != file.Path || Read(file.Path).Revision != file.Revision) throw Changed();
                CheckpointStore.CheckDirectPath(temporary);
                CheckpointStore.CheckDirectPath(file.Path);
                if (file.Exists) File.Replace(temporary, file.Path, null);
                else File.Move(temporary, file.Path); // Never overwrite a concurrent creator.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>Session-scoped ACP tool configuration; does not read or mutate Antigravity's global settings.</summary>
        internal static JObject CreateAcpServer(string workspaceRoot, string extensionDirectory)
        {
            var workspace = NormalizeRoot(workspaceRoot);
            if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException("The ACP workspace does not exist.");
            var bundle = BundledEngine.Inspect(extensionDirectory);
            if (bundle.Status != BundledEngineStatus.Verified)
                throw new InvalidOperationException(PompLanguage.Pick("OMP 도구 연결에는 검증된 POMP 번들 엔진이 필요해요.", "OMP tools require a verified POMP bundled engine."));
            return new JObject
            {
                ["name"] = ServerName(workspace), ["command"] = bundle.Path,
                ["args"] = Arguments(workspace), ["env"] = new JArray(),
            };
        }

        private static JArray Arguments(string workspace) => new JArray("mcp-server", "--cwd", workspace, "--allow-write", "--allow-exec");
        private bool Owns(JToken entry)
        {
            if (entry is not JObject server || server["command"]?.Type != JTokenType.String || server["args"] is not JArray args ||
                args.Count < 3 || args.Count > 5 || args.Any(arg => arg.Type != JTokenType.String) ||
                (string?)args[0] != "mcp-server" || (string?)args[1] != "--cwd" ||
                server["serverUrl"] != null || server["url"] != null || server["type"] != null && (server["type"]!.Type != JTokenType.String || (string?)server["type"] != "stdio")) return false;
            try
            {
                var command = (string)server["command"]!;
                if (!Path.IsPathRooted(command) || !string.Equals(Path.GetFileName(command), "omp.exe", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(Path.GetDirectoryName(command)), "omp", StringComparison.OrdinalIgnoreCase) ||
                    !Path.IsPathRooted((string)args[2]!) || !string.Equals(NormalizeRoot((string)args[2]!), root, StringComparison.OrdinalIgnoreCase)) return false;
                var flags = args.Skip(3).Values<string>().ToArray();
                return flags.Distinct(StringComparer.Ordinal).Count() == flags.Length && flags.All(flag => flag is "--allow-write" or "--allow-exec");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException) { return false; }
        }

        private static string NormalizeRoot(string path)
        {
            if (!Path.IsPathRooted(path)) throw new ArgumentException("An absolute workspace root is required.");
            var full = Path.GetFullPath(path);
            return full.Length == Path.GetPathRoot(full)!.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        private static string ServerName(string workspace) => "pomp_" + ToolResourceFiles.Hash(workspace.Replace('\\', '/').ToUpperInvariant()).Substring(0, 20);
        private static InvalidOperationException Changed() => new InvalidOperationException(PompLanguage.Pick("Antigravity 설정이 바뀌었어요. 다시 확인해 주세요.", "Antigravity configuration changed. Refresh before configuring."));
        private static string CollisionMessage() => PompLanguage.Pick("이 서버 이름은 다른 설정이 사용 중이에요. 기존 항목은 변경하지 않았어요.", "This server name belongs to another configuration. The existing entry was not changed.");
        private static string ConfiguredMessage() => PompLanguage.Pick("설정 파일이 준비됐어요. Antigravity 앱에서 MCP를 새로 고치거나 다시 시작해 주세요. CLI에서는 /mcp에서 다시 불러오세요. 연결이나 로그인은 확인하지 않았어요.", "Configuration is ready. Refresh MCP in the Antigravity app or restart it if needed; in agy CLI, reload from /mcp. Connection and sign-in have not been verified.");
        private static bool IsConfigurationError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is JsonException || ex is SecurityException;
        // JSON errors may quote configuration values (including credentials): never send those details to the page.
        private static string SafeProblem(Exception ex) => ex is JsonException || ex is DecoderFallbackException ? PompLanguage.Pick("JSON/UTF-8 형식이 올바르지 않거나 중복 키가 있어요. 파일은 그대로 유지했어요.", "Invalid JSON/UTF-8 or duplicate keys. The file was left unchanged.") : ex.Message;

        private static string? ExistingInstallation(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path!) || !File.Exists(path)) return null;
            var name = Path.GetFileName(path);
            if (!string.Equals(name, "Antigravity.exe", StringComparison.OrdinalIgnoreCase) && !CliExecutables.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(path!);
        }

        internal static string? FindCliInstallation(string? localAppData, string? searchPath)
        {
            if (!string.IsNullOrEmpty(localAppData))
            {
                var standard = ExistingInstallation(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
                if (standard != null) return standard;
            }
            foreach (var entry in (searchPath ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                    if (!Path.IsPathRooted(directory)) continue;
                    foreach (var executable in CliExecutables)
                    {
                        var found = ExistingInstallation(Path.Combine(directory, executable));
                        if (found != null) return found;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException || ex is SecurityException) { }
            }
            return null;
        }

        /// <summary>Official app/CLI install folders, PATH agy and Windows registration; never starts a process.</summary>
        public static string? FindInstallation()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(folder)) continue;
                foreach (var relative in new[] { Path.Combine("Programs", "antigravity", "Antigravity.exe"), Path.Combine("Antigravity", "Antigravity.exe"), Path.Combine("Google", "Antigravity", "Antigravity.exe") })
                {
                    var found = ExistingInstallation(Path.Combine(folder, relative));
                    if (found != null) return found;
                }
            }
            var cli = FindCliInstallation(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("PATH"));
            if (cli != null) return cli;
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var app = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Antigravity.exe");
                    var found = ExistingInstallation((app?.GetValue(null) as string)?.Trim('"'));
                    if (found != null) return found;
                    using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        var label = entry?.GetValue("DisplayName") as string;
                        if (label == null || !label.StartsWith("Antigravity", StringComparison.OrdinalIgnoreCase)) continue;
                        var folder = entry?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(folder)) found = ExistingInstallation(Path.Combine(folder!.Trim('"'), "Antigravity.exe"));
                        if (found != null) return found;
                        var icon = entry?.GetValue("DisplayIcon") as string;
                        if (icon != null)
                        {
                            var suffix = icon.LastIndexOf(',');
                            if (suffix >= 0 && int.TryParse(icon.Substring(suffix + 1), out _)) icon = icon.Substring(0, suffix);
                            found = ExistingInstallation(icon.Trim().Trim('"'));
                            if (found != null) return found;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException || ex is NotSupportedException) { }
            }
            return null;
        }
    }
}
