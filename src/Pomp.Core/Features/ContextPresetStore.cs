using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Internal;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>Named file-reference presets per workspace, as many as fit the 1 MiB store. Stores paths/order/exclusions, never source content.</summary>
    public sealed class ContextPresetStore
    {
        private const int MaxStoreBytes = 1024 * 1024;
        private readonly string root, file;
        public ContextPresetStore(string dataDirectory, string workspaceRoot)
        {
            root = Path.GetFullPath(workspaceRoot);
            var key = Path.DirectorySeparatorChar == '\\' ? root.ToLowerInvariant() : root;
            file = ProjectData.File(root, "context-presets.json", Path.Combine(dataDirectory, "context-presets", ContentHash.Compute(key) + ".json"));
        }
        public JObject Handle(JObject request)
        {
            CheckpointStore.CheckDirectPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            CheckpointStore.CheckDirectPath(file + ".lock");
            using var lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var rows = Read();
            var action = (string?)request["action"] ?? "refresh";
            if (action != "refresh")
            {
                if (!string.Equals(root, (string?)request["root"], Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
                if (action == "save")
                {
                    var name = ((string?)request["name"] ?? "").Trim();
                    if (name.Length == 0 || name.Length > 64 || name.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("프리셋 이름이 올바르지 않아요.", "The preset name is invalid."));
                    if (!(request["files"] is JArray files) || request["includeEditorContext"]?.Type != JTokenType.Boolean)
                        throw new ArgumentException(PompLanguage.Pick("프리셋이 올바르지 않아요.", "The preset is invalid."));
                    var safeFiles = ValidateFiles(files);
                    // With an id the save edits that preset (rename, or new files); without one a matching name is overwritten.
                    var id = (string?)request["id"];
                    var sameName = rows.OfType<JObject>().FirstOrDefault(r => string.Equals((string?)r["name"], name, StringComparison.OrdinalIgnoreCase));
                    var existing = id == null ? sameName : rows.OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id)
                        ?? throw new InvalidOperationException(PompLanguage.Pick("프리셋이 더 이상 없어요.", "Preset no longer exists."));
                    if (id != null && sameName != null && sameName != existing)
                        throw new InvalidOperationException(PompLanguage.Pick("같은 이름의 프리셋이 있어요.", "A preset with this name already exists."));
                    var preset = new JObject { ["id"] = (string?)existing?["id"] ?? Guid.NewGuid().ToString("N"), ["name"] = name,
                        ["includeEditorContext"] = (bool)request["includeEditorContext"]!, ["files"] = safeFiles };
                    if (existing == null) rows.Add(preset); else existing.Replace(preset);
                }
                else if (action == "delete")
                {
                    var id = (string?)request["id"];
                    var existing = rows.OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id);
                    if (existing == null) throw new InvalidOperationException(PompLanguage.Pick("프리셋이 더 이상 없어요.", "Preset no longer exists."));
                    existing.Remove();
                }
                else throw new ArgumentException(PompLanguage.Pick("알 수 없는 프리셋 명령이에요.", "Unknown preset command."));
                var data = new JObject { ["version"] = 1, ["root"] = root, ["presets"] = rows };
                var json = data.ToString(Formatting.None);
                if (Encoding.UTF8.GetByteCount(json) > MaxStoreBytes) throw new InvalidOperationException(PompLanguage.Pick("컨텍스트 프리셋 저장소가 너무 커요.", "The context preset store is too large."));
                AtomicFile.WriteAllText(file, json);
            }
            return new JObject { ["root"] = root, ["presets"] = rows };
        }
        private JArray Read()
        {
            if (!File.Exists(file)) return new JArray();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxStoreBytes) throw new InvalidOperationException(PompLanguage.Pick("컨텍스트 프리셋 저장소가 너무 커요.", "The context preset store is too large."));
            using var text = new StreamReader(stream, Encoding.UTF8, true);
            using var reader = new JsonTextReader(text) { MaxDepth = 8 };
            var data = JObject.Load(reader);
            if ((int?)data["version"] != 1 || !string.Equals((string?)data["root"], root, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !(data["presets"] is JArray rows))
                throw new InvalidOperationException(PompLanguage.Pick("컨텍스트 프리셋 저장소 형식이 올바르지 않아요.", "The context preset store is invalid."));
            foreach (var row in rows)
            {
                if (!(row is JObject) || !Guid.TryParseExact((string?)row["id"], "N", out _) || row["name"]?.Type != JTokenType.String || ((string)row["name"]!).Length > 64 || row["includeEditorContext"]?.Type != JTokenType.Boolean || !(row["files"] is JArray files))
                    throw new InvalidOperationException(PompLanguage.Pick("컨텍스트 프리셋 항목의 형식이 올바르지 않아요.", "Context preset entry is malformed."));
                row["files"] = ValidateFiles(files);
            }
            return rows;
        }
        private static JArray ValidateFiles(JArray files)
        {
            var safe = new JArray();
            foreach (var entry in files)
            {
                if (entry["fullPath"]?.Type != JTokenType.String || entry["path"]?.Type != JTokenType.String) throw new ArgumentException(PompLanguage.Pick("프리셋 파일 경로가 올바르지 않아요.", "Invalid preset file path."));
                var full = (string)entry["fullPath"]!; var display = (string)entry["path"]!;
                if (full.Length == 0 || full.Length > 4096 || display.Length > 4096 || full.Any(char.IsControl) || display.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("프리셋 파일 경로가 올바르지 않아요.", "Invalid preset file path."));
                if (entry["excluded"] != null && entry["excluded"]!.Type != JTokenType.Boolean) throw new ArgumentException(PompLanguage.Pick("프리셋 제외 여부가 올바르지 않아요.", "The preset exclusion is invalid."));
                safe.Add(new JObject { ["path"] = display, ["fullPath"] = full, ["excluded"] = (bool?)entry["excluded"] ?? false });
            }
            return safe;
        }
    }
}
