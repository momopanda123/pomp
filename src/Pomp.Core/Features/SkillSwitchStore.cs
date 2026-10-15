using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// POMP's per-skill on/off overrides (<c>disabled-skills.json</c> in POMP's data folder, per user
    /// on this machine). Each set holds engine skill names; a name in neither set follows its default
    /// (a catalog skill: its <c>defaultEnabled</c>; anything else: on). The engine overlay's
    /// <c>disabledExtensions</c> is the effective off-set merged with the user's and the workspace's own
    /// <c>disabledExtensions</c> (see <see cref="OmpConfigLayers"/>); the merge keeps applying a user's
    /// hand-written disables that POMP's overlay would otherwise wipe. Names come from the engine
    /// (its command catalog), so anything the engine reports is accepted here, bounded in length (and the file in bytes).
    /// A file written by the older single-list store (<c>{disabled: [...]}</c>) loads as explicit offs.
    /// </summary>
    internal sealed class SkillSwitchStore
    {
        /// <summary>The file's size: the switches have no count limit of their own.</summary>
        internal const int MaxBytes = 1024 * 1024;
        internal const int MaxNameLength = 256;
        internal const string FileName = "disabled-skills.json";
        private readonly string file;

        internal SkillSwitchStore(string dataDirectory)
        {
            file = Path.Combine(Path.GetFullPath(dataDirectory), FileName);
        }

        internal sealed class Overrides
        {
            internal readonly HashSet<string> On = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Off = new HashSet<string>(StringComparer.Ordinal);
        }

        internal Overrides Load()
        {
            var result = new Overrides();
            try
            {
                CheckpointStore.CheckDirectPath(file);
                if (!File.Exists(file)) return result;
                var bytes = ToolResourceFiles.Read(file, MaxBytes);
                using var reader = new JsonTextReader(new StringReader(ToolResourceFiles.Text(bytes))) { MaxDepth = 5, DateParseHandling = DateParseHandling.None };
                var json = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonException("Trailing content.");
                if (json is not JObject root) throw new JsonException("Not a skill-switch file.");
                // The older single-list shape migrates to explicit offs; the next successful switch rewrites the file.
                if (root["on"] == null && root["off"] == null && root["disabled"] is JArray legacy)
                {
                    foreach (var name in Names(legacy)) result.Off.Add(name);
                    return result;
                }

                if (root["on"] is JArray on) foreach (var name in Names(on)) result.On.Add(name);
                else if (root["on"] != null) throw new JsonException("Not a skill-switch file.");
                if (root["off"] is JArray off) foreach (var name in Names(off)) result.Off.Add(name);
                else if (root["off"] != null) throw new JsonException("Not a skill-switch file.");
                // A name in both sets (hand-edited) reads as off: the safer direction.
                result.On.ExceptWith(result.Off);
                return result;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                // A broken store overrides nothing; the next successful switch rewrites it.
                return new Overrides();
            }
        }

        private static List<string> Names(JArray list)
        {
            var names = new List<string>();
            foreach (var item in list)
            {
                if (item.Type != JTokenType.String) throw new JsonException("Not a skill-switch file.");
                var name = (string)item!;
                ValidateName(name);
                if (!names.Contains(name)) names.Add(name);
            }

            return names;
        }

        internal static void ValidateName(string name)
        {
            // Engine-reported names: anything printable without whitespace or control characters (a namespaced "ns/dup" or a colon form included).
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new ArgumentException(PompLanguage.Pick("스킬 이름이 올바르지 않아요.", "The skill name is invalid."));
        }

        /// <summary>Switches <paramref name="name"/> explicitly on or off; true when the stored sets changed.</summary>
        internal bool Set(string name, bool enabled)
        {
            return SetMany(new[] { name }, enabled);
        }

        /// <summary>Switches every name in <paramref name="names"/> explicitly on or off; true when the stored sets changed.</summary>
        internal bool SetMany(IEnumerable<string> names, bool enabled)
        {
            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            foreach (var name in wanted) ValidateName(name);
            lock (ToolResourceFiles.Gate)
            {
                var current = Load();
                var changed = false;
                foreach (var name in wanted)
                {
                    if (enabled) { changed |= current.Off.Remove(name); changed |= current.On.Add(name); }
                    else { changed |= current.On.Remove(name); changed |= current.Off.Add(name); }
                }

                if (!changed) return false;
                Save(current);
                return true;
            }
        }

        /// <summary>Clears both overrides of <paramref name="name"/> (back to its default); true when the stored sets changed.</summary>
        internal bool Reset(string name)
        {
            ValidateName(name);
            lock (ToolResourceFiles.Gate)
            {
                var current = Load();
                if (!current.On.Remove(name) && !current.Off.Remove(name)) return false;
                Save(current);
                return true;
            }
        }

        private void Save(Overrides overrides)
        {
            var text = new JObject { ["version"] = 1,
                ["on"] = new JArray(overrides.On.OrderBy(n => n, StringComparer.Ordinal)),
                ["off"] = new JArray(overrides.Off.OrderBy(n => n, StringComparer.Ordinal)) }.ToString(Formatting.None);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidOperationException(PompLanguage.Pick("스킬 켜기·끄기 파일이 너무 커요.", "The skill switch file is too large."));
            AtomicFile.WriteAllText(file, text);
        }
    }
}
