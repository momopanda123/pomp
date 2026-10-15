using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>Edits only native project/active-profile configuration; imported third-party configurations stay read-only.</summary>
    internal sealed class NativeMcpConfig
    {
        internal const string Mask = "••••";
        private const int MaximumBytes = 1024 * 1024;
        private readonly string project, user;
        internal bool Changed { get; private set; }
        /// <summary>The workspace whose <c>.omp/mcp.json</c> this edits.</summary>
        internal string ProjectRoot { get; }
        /// <summary>The profile this edits (null = the default one); the catalog installs MCP entries into the same scope files.</summary>
        internal string? Profile { get; }
        private sealed class FileState { internal string Path = "", Hash = "missing"; internal JObject Json = new JObject(); }
        internal NativeMcpConfig(string root, string? home, string? profile = null)
        {
            if (profile != null && !Regex.IsMatch(profile, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z")) throw new ArgumentException(PompLanguage.Pick("프로필 이름이 올바르지 않아요.", "Invalid profile name."));
            Profile = profile;
            ProjectRoot = Path.GetFullPath(root);
            project = Path.Combine(ProjectRoot, ".omp", "mcp.json");
            user = profile == null ? Path.Combine(ToolResourceFiles.Home(home), ".omp", "agent", "mcp.json") : Path.Combine(ToolResourceFiles.Home(home), ".omp", "profiles", profile, "agent", "mcp.json");
        }
        private static FileState Read(string path)
        {
            CheckpointStore.CheckDirectPath(path); var state = new FileState { Path = path };
            if (!File.Exists(path)) return state;
            var bytes = ToolResourceFiles.Read(path, MaximumBytes); state.Hash = ToolResourceFiles.Hash(bytes);
            using var reader = new JsonTextReader(new StringReader(ToolResourceFiles.Text(bytes))) { MaxDepth = 20, DateParseHandling = DateParseHandling.None };
            state.Json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonException("Trailing MCP configuration content.");
            if (state.Json["mcpServers"] != null && state.Json["mcpServers"] is not JObject) throw new JsonException("mcpServers is not an object.");
            foreach (var key in new[] { "enabledServers", "disabledServers" }) if (state.Json[key] != null && (state.Json[key] is not JArray values || values.Any(v => v.Type != JTokenType.String))) throw new JsonException(key + " is not a string array.");
            return state;
        }
        // Opaque row hashes pin both full native files for project edits, because stock toggles also clean user overrides.
        // Create-only saves re-read under Gate and merge one entry, so adding to an existing empty file needs no snapshot hash.
        private static string RowHash(FileState file, FileState overrides, string name) => ToolResourceFiles.Hash(file.Path + "\n" + file.Hash + "\n" + overrides.Path + "\n" + overrides.Hash + "\n" + name);
        /// <summary>The raw project file (for the catalog's installed checks); null when it cannot be read.</summary>
        internal JObject? ProjectMcp()
        {
            lock (ToolResourceFiles.Gate)
            {
                try { return Read(project).Json; }
                catch { return null; }
            }
        }
        /// <summary>The raw user (active-profile) file (for the catalog's installed checks); null when it cannot be read.</summary>
        internal JObject? UserMcp()
        {
            lock (ToolResourceFiles.Gate)
            {
                try { return Read(user).Json; }
                catch { return null; }
            }
        }
        /// <summary>The server entry the row <paramref name="name"/> in <paramref name="scope"/> was installed from (<c>pompCatalog</c>), or null.</summary>
        internal static string? CatalogMarker(JObject? file, string name) => (file?["mcpServers"] as JObject)?[name] is JObject server ? (string?)server["pompCatalog"] : null;
        internal McpStatePayload View()
        {
            lock (ToolResourceFiles.Gate)
            {
                var result = new McpStatePayload(); FileState? overrides = null;
                try { overrides = Read(user); } catch { result.Message = PompLanguage.Pick("사용자 MCP 설정을 읽을 수 없어요.", "Cannot read the user MCP configuration."); }
                foreach (var scope in new[] { "project", "user" })
                {
                    try
                    {
                        var file = scope == "user" ? overrides ?? throw new IOException() : Read(project);
                        foreach (var property in (file.Json["mcpServers"] as JObject ?? new JObject()).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                        {
                            try { ValidateName(property.Name); } catch { result.Message = PompLanguage.Pick("이름이 올바르지 않은 MCP 항목은 생략했어요.", "MCP entries with invalid names were omitted."); continue; }
                            var server = property.Value as JObject; var type = (string?)server?["type"] ?? (server?["url"] == null ? "stdio" : "http");
                            var row = new McpServerPayload { Name = property.Name, Scope = scope, Type = type is "http" or "sse" ? type : "stdio", Hash = overrides == null ? null : RowHash(file, overrides, property.Name) };
                            try
                            {
                                ValidateName(property.Name); if (server == null) throw new ArgumentException(); Validate(server);
                                row.Command = (string?)server["command"]; row.Url = (string?)server["url"]; row.Args = (server["args"] as JArray)?.Select(v => (string)v!).ToList();
                                row.Env = (server["env"] as JObject)?.Properties().ToDictionary(p => p.Name, _ => Mask, StringComparer.Ordinal);
                                row.Headers = (server["headers"] as JObject)?.Properties().ToDictionary(p => p.Name, _ => Mask, StringComparer.Ordinal);
                                row.CatalogId = (string?)server["pompCatalog"];
                                row.Enabled = !Contains(overrides?.Json, "disabledServers", property.Name) && (Contains(overrides?.Json, "enabledServers", property.Name) || (bool?)server["enabled"] != false);
                            }
                            catch { row.Error = PompLanguage.Pick("MCP 서버 설정이 올바르지 않아요.", "Invalid MCP server configuration."); }
                            result.Servers.Add(row);
                        }
                    }
                    catch { result.Message = PompLanguage.Pick("MCP 설정을 읽을 수 없어요.", "Cannot read the MCP configuration."); }
                }
                // No count limit: every server is sent while the message fits the UI hub (StateBudget); the rest are counted.
                var left = StateBudget.Fit(result.Servers, StateBudget.Bytes(new UiMessage(UiMessageTypes.McpState, new McpStatePayload { Message = result.Message, RestartRequired = result.RestartRequired })) + 2048);
                if (left > 0) result.Message = PompLanguage.Pick(left + "개 MCP 서버는 목록이 너무 커서 표시하지 않아요.", left + " MCP servers are not shown: the list is too large.");
                return result;
            }
        }
        internal static void ValidateName(string name)
        {
            if (name.Length is < 1 or > 100 || !Regex.IsMatch(name, @"\A[a-zA-Z0-9_.:-]+(?: [a-zA-Z0-9_.:-]+)*\z")) throw new ArgumentException(PompLanguage.Pick("MCP 서버 이름이 올바르지 않아요.", "The MCP server name is invalid."));
        }
        private static void Validate(JObject server)
        {
            string? String(string key, int max) { if (server[key] == null) return null; if (server[key]!.Type != JTokenType.String) throw new ArgumentException(); var value = (string)server[key]!; if (value.Length > max || value.IndexOf('\0') >= 0) throw new ArgumentException(); return value; }
            var type = String("type", 10) ?? (server["url"] == null ? "stdio" : "http"); var command = String("command", 4096); var url = String("url", 4096);
            if (type == "stdio") { if (string.IsNullOrWhiteSpace(command) || command!.Any(char.IsControl) || url != null) throw new ArgumentException(); }
            else if (type is "http" or "sse") { if (command != null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0) throw new ArgumentException(); }
            else throw new ArgumentException();
            if (server["enabled"] != null && server["enabled"]!.Type != JTokenType.Boolean) throw new ArgumentException();
            if (server["args"] != null && (server["args"] is not JArray args || args.Count > 200 || args.Any(v => v.Type != JTokenType.String || ((string)v!).Length > 4096 || ((string)v!).Contains('\0')))) throw new ArgumentException();
            if (server["env"] != null)
            {
                if (server["env"] is not JObject env || env.Count > 100) throw new ArgumentException();
                foreach (var pair in env.Properties()) if (!Regex.IsMatch(pair.Name, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z") || pair.Value.Type != JTokenType.String || ((string)pair.Value!).Length > 8192 || ((string)pair.Value!).Contains('\0')) throw new ArgumentException();
            }
            if (server["headers"] != null)
            {
                if (type == "stdio" || server["headers"] is not JObject headers || headers.Count > 100) throw new ArgumentException();
                foreach (var pair in headers.Properties()) if (!Regex.IsMatch(pair.Name, @"\A[!#$%&'*+\-.^_`|~A-Za-z0-9]{1,128}\z") || pair.Value.Type != JTokenType.String || ((string)pair.Value!).Length > 8192 || ((string)pair.Value!).Any(c => c < ' ' && c != '\t' || c == 0x7F)) throw new ArgumentException();
            }
            if (server["pompCatalog"] != null && (server["pompCatalog"]!.Type != JTokenType.String || !Regex.IsMatch((string)server["pompCatalog"]!, @"\A[a-z0-9-]{2,64}\z"))) throw new ArgumentException();
        }
        private static bool Contains(JObject? config, string key, string name) => (config?[key] as JArray)?.Any(v => (string?)v == name) == true;
        private static void RemoveOverride(JObject config, string key, string name)
        {
            if (config[key] is not JArray list) return;
            foreach (var item in list.Where(v => (string?)v == name).ToArray()) item.Remove(); if (list.Count == 0) config.Remove(key);
        }
        private void Write(FileState file)
        {
            // Only a file POMP creates gets the schema hint; existing files keep exactly their own keys.
            if (file.Hash == "missing" && file.Json["$schema"] == null) file.Json["$schema"] ="https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json";
            var text = file.Json.ToString(Formatting.Indented) + "\n"; if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new IOException(PompLanguage.Pick("MCP 설정이 너무 커요.", "The MCP configuration is too large."));
            CheckpointStore.CheckDirectPath(file.Path); AtomicFile.WriteAllText(file.Path, text); Changed = true;
        }
        internal void Mutate(McpCommandPayload command)
        {
            ValidateName(command.Name ?? ""); var name = command.Name!;
            lock (ToolResourceFiles.Gate)
            {
                var overrides = Read(user); var file = command.Scope == "user" ? overrides : command.Scope == "project" ? Read(project) : throw new ArgumentException(PompLanguage.Pick("범위를 고르지 않았어요.", "No scope was chosen."));
                var servers = file.Json["mcpServers"] as JObject ?? new JObject(); var existing = servers[name];
                if (command.Action == "save" && command.ExpectedHash == null)
                {
                    if (existing != null || (command.Scope == "project" && overrides.Json["mcpServers"]?[name] != null) || Contains(overrides.Json, "disabledServers", name) || Contains(overrides.Json, "enabledServers", name)) throw new InvalidOperationException(PompLanguage.Pick("이 서버 이름이 이미 있어요.", "This server name already exists."));
                }
                else if (existing == null || command.ExpectedHash == null || command.ExpectedHash != RowHash(file, overrides, name)) throw new InvalidOperationException(PompLanguage.Pick("설정이 불러온 뒤 바뀌었어요.", "Configuration changed since loaded."));
                var oldUser = overrides.Json.ToString(Formatting.None);
                if (command.Action == "save")
                {
                    var input = command.Server ?? throw new ArgumentException(PompLanguage.Pick("서버 설정이 비어 있어요.", "The server configuration is empty.")); var updated = existing is JObject old ? (JObject)old.DeepClone() : new JObject();
                    foreach (var key in new[] { "type", "command", "args", "env", "headers", "url", "enabled", "pompCatalog" })
                    {
                        if (input[key] != null) updated[key] = input[key]!.DeepClone(); else if (key is "command" or "url" or "args" or "env" or "headers") updated.Remove(key);
                    }
                    if (updated["env"] is JObject env) foreach (var pair in env.Properties().ToArray()) if ((string?)pair.Value == Mask)
                    {
                        var oldValue = (existing as JObject)?["env"]?[pair.Name]; if (oldValue == null || oldValue.Type != JTokenType.String) throw new ArgumentException(PompLanguage.Pick("환경 변수 값이 올바르지 않아요.", "The environment variable value is invalid.")); pair.Value = oldValue.DeepClone();
                    }
                    if (updated["headers"] is JObject headers) foreach (var pair in headers.Properties().ToArray()) if ((string?)pair.Value == Mask)
                    {
                        var oldValue = (existing as JObject)?["headers"]?[pair.Name]; if (oldValue == null || oldValue.Type != JTokenType.String) throw new ArgumentException(PompLanguage.Pick("헤더 값이 올바르지 않아요.", "The header value is invalid.")); pair.Value = oldValue.DeepClone();
                    }
                    try { Validate(updated); } catch { throw new ArgumentException(PompLanguage.Pick("MCP 서버 설정이 올바르지 않아요.", "Invalid MCP server configuration.")); }
                    servers[name] = updated;
                    if ((bool?)updated["enabled"] != false) RemoveOverride(overrides.Json, "disabledServers", name);
                    RemoveOverride(overrides.Json, "enabledServers", name);
                }
                else if (command.Action == "delete") { servers.Remove(name); RemoveOverride(overrides.Json, "enabledServers", name); RemoveOverride(overrides.Json, "disabledServers", name); }
                else if (command.Action is "enable" or "disable")
                {
                    if (existing is not JObject server) throw new ArgumentException(PompLanguage.Pick("서버 설정이 올바르지 않아요.", "The server configuration is invalid."));
                    server["enabled"] = command.Action == "enable";
                    if (command.Action == "enable") RemoveOverride(overrides.Json, "disabledServers", name);
                    RemoveOverride(overrides.Json, "enabledServers", name);
                }
                else throw new ArgumentException(PompLanguage.Pick("알 수 없는 MCP 동작이에요.", "Unknown MCP action."));
                file.Json["mcpServers"] = servers;
                // Each file is atomic, not both files together. Changed records a partial write so the host still requires restart/retry.
                if (!ReferenceEquals(file, overrides) && oldUser != overrides.Json.ToString(Formatting.None)) Write(overrides);
                Write(file);
            }
        }
    }
}
