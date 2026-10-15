using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Pomp.Hosting;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// Project hook files edited from the Tools tab: top-level <c>.pomp/hooks/*.md</c>.
    /// Files that do not parse stay listed with a note so they can still be opened or deleted.
    /// </summary>
    internal sealed class HookCatalog
    {
        internal const int MaxBytes = 65536;
        private static readonly Regex IdName = new Regex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
        private readonly string root;
        internal HookCatalog(string root) { this.root = string.IsNullOrEmpty(root) ? "" : Path.GetFullPath(root); }

        internal HooksStatePayload View()
        {
            var result = new HooksStatePayload { Policy = Policy() };
            if (root.Length == 0) return result;
            lock (ToolResourceFiles.Gate)
            {
                string folder;
                try { folder = HooksFolder(); }
                catch (Exception ex) { Diagnostic(ex.Message); return result; }
                try
                {
                    if (!Directory.Exists(folder)) return result;
                    var files = FilesIn(folder);
                    foreach (var file in files)
                    {
                        try
                        {
                            CheckpointStore.CheckDirectPath(file);
                            var id = Path.GetFileNameWithoutExtension(file);
                            if (id.Length == 0) { Diagnostic(Path.GetFileName(file) + ": " + PompLanguage.Pick("이름이 없는 파일은 표시하지 않아요.", "A file without a name is not shown.")); continue; }
                            var probe = ToolResourceFiles.Examine(file, MaxBytes);
                            var info = new HookInfoPayload { Id = id, Name = id, File = file, Hash = probe.Hash ?? "" };
                            if (probe.Unreadable) { info.Unreadable = true; info.Note = probe.Problem; }
                            else
                            {
                                try { Fill(info, file, probe.Text); }
                                catch (Exception ex)
                                {
                                    info.Note = Bound(ex.Message); info.Event = ""; info.Script = "";
                                }
                            }

                            result.Hooks.Add(info);
                        }
                        catch (Exception ex) { Diagnostic(Path.GetFileName(file) + ": " + ex.Message); }
                    }
                }
                catch (Exception ex) { Diagnostic(ex.Message); }
                // No count limit: every hook is sent while the message fits the UI hub (StateBudget); the rest are counted, never silently dropped.
                var left = StateBudget.Fit(result.Hooks, StateBudget.Bytes(new UiMessage(UiMessageTypes.HooksState, new HooksStatePayload { Diagnostics = result.Diagnostics, Policy = result.Policy })) + 2048);
                if (left > 0) { result.Diagnostics.Insert(0, PompLanguage.Pick(left + "개 훅은 목록이 너무 커서 표시하지 않아요.", left + " hooks are not shown: the list is too large.")); if (result.Diagnostics.Count > 20) result.Diagnostics.RemoveRange(20, result.Diagnostics.Count - 20); }
                return result;
                void Diagnostic(string message) { if (result.Diagnostics.Count < 20) result.Diagnostics.Add(Bound(message)); }
            }
        }

        /// <summary>Every top-level hook file, in the runtime's order.</summary>
        private static string[] FilesIn(string folder) => Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

        private static void Fill(HookInfoPayload info, string file, string text)
        {
            var hook = ProjectHook.Parse(file, text);
            info.Name = hook.Name; info.Description = hook.Description; info.Event = hook.Event;
            info.Glob = hook.Glob; info.TimeoutSeconds = hook.TimeoutSeconds; info.Enabled = hook.Enabled; info.Script = hook.Script;
        }

        private string Policy()
        {
            if (root.Length == 0) return "none";
            try
            {
                if (!ProjectExecutionPolicy.Exists(root)) return "none";
                return ProjectExecutionPolicy.Read(root) != null ? "valid" : "invalid";
            }
            catch { return "invalid"; }
        }

        internal string Create(string name, HookDraftPayload? draft)
        {
            // New hooks follow the other resources' names; existing files keep whatever id ProjectHooks accepts.
            if (!Regex.IsMatch(name ?? "", @"\A[a-z0-9-]{2,40}\z", RegexOptions.CultureInvariant)) throw new ArgumentException(PompLanguage.Pick("이름이 올바르지 않아요.", "The name is invalid."));
            var id = RequireId(name!);
            var (description, @event, glob, timeout, enabled, script) = RequireDraft(draft);
            lock (ToolResourceFiles.Gate)
            {
                var folder = HooksFolder();
                var text = Compose(id, description, @event, enabled, glob, timeout, script);
                ProjectHook.Parse(id + ".md", text);
                var file = Path.Combine(folder, id + ".md");
                ToolResourceFiles.WriteNew(file, Encode(text));
                return file;
            }
        }

        internal string Update(string id, string? expectedHash, HookDraftPayload? draft)
        {
            var (description, @event, glob, timeout, enabled, script) = RequireDraft(draft);
            lock (ToolResourceFiles.Gate)
            {
                var file = Existing(id);
                var current = ReadParsed(file);
                var text = Compose(current.Name, description, @event, enabled, glob, timeout, script);
                ProjectHook.Parse(file, text);
                ToolResourceFiles.Replace(file, Encode(text), expectedHash, MaxBytes);
                return file;
            }
        }

        internal void SetEnabled(string id, string? expectedHash, bool enabled)
        {
            lock (ToolResourceFiles.Gate)
            {
                var file = Existing(id);
                var current = ReadParsed(file);
                var text = Compose(current.Name, current.Description, current.Event, enabled, current.Glob, current.TimeoutSeconds, current.Script);
                ProjectHook.Parse(file, text);
                ToolResourceFiles.Replace(file, Encode(text), expectedHash, MaxBytes);
            }
        }

        internal void Delete(string id, string? expectedHash)
        {
            lock (ToolResourceFiles.Gate)
            {
                var file = Existing(id);
                if (!File.Exists(file)) throw new FileNotFoundException(PompLanguage.Pick("훅 파일이 더 이상 없어요.", "The hook file no longer exists."));
                ToolResourceFiles.DeleteIfHash(file, expectedHash, PompLanguage.Pick("훅이 바뀌었어요.", "The hook changed."));
            }
        }

        /// <summary>The file of an existing hook, for opening in the editor.</summary>
        internal string FileOf(string id)
        {
            var file = Existing(id);
            if (!File.Exists(file)) throw new FileNotFoundException(PompLanguage.Pick("훅 파일이 더 이상 없어요.", "The hook file no longer exists."));
            return file;
        }

        /// <summary>Scalar frontmatter plus the script in one powershell fence; always re-parsed before writing.</summary>
        private static string Compose(string displayName, string description, string @event, bool enabled, string glob, int timeout, string script)
        {
            var text = new StringBuilder("---\n");
            text.Append("name: ").Append(JsonConvert.SerializeObject(displayName)).Append('\n');
            if (description.Length > 0) text.Append("description: ").Append(JsonConvert.SerializeObject(description)).Append('\n');
            text.Append("event: ").Append(@event).Append('\n');
            text.Append("enabled: ").Append(enabled ? "true" : "false").Append('\n');
            text.Append("glob: ").Append(JsonConvert.SerializeObject(glob)).Append('\n');
            text.Append("timeoutSeconds: ").Append(timeout.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("---\n\n```powershell\n").Append(script).Append("\n```\n");
            return text.ToString();
        }

        private static (string Description, string Event, string Glob, int Timeout, bool Enabled, string Script) RequireDraft(HookDraftPayload? draft)
        {
            if (draft == null) throw new ArgumentException(PompLanguage.Pick("훅 내용이 비어 있어요.", "The hook is empty."));
            var @event = draft.Event ?? "";
            if (@event != "onFileSave" && @event != "onBuild") throw new ArgumentException(PompLanguage.Pick("훅 이벤트가 올바르지 않아요.", "The hook event is invalid."));
            var description = draft.Description ?? "";
            if (description.Length > 500 || description.Any(char.IsControl) || description.Contains('\r') || description.Contains('\n'))
                throw new ArgumentException(PompLanguage.Pick("훅 설명이 올바르지 않아요.", "The hook description is invalid."));
            // The build event carries no path filter; onBuild hooks always watch everything.
            var glob = @event == "onBuild" ? "**" : (draft.Glob ?? "");
            ProjectHook.Pattern(glob);
            if (draft.TimeoutSeconds is < 1 or > 3600) throw new ArgumentException(PompLanguage.Pick("timeoutSeconds가 올바르지 않아요.", "timeoutSeconds is invalid."));
            var script = (draft.Script ?? "").Replace("\r\n", "\n").Trim();
            if (script.Length is < 1 or > 6000 || script.Contains('\0')) throw new ArgumentException(PompLanguage.Pick("훅 스크립트가 올바르지 않아요.", "The hook script is invalid."));
            if (script.Contains("```")) throw new ArgumentException(PompLanguage.Pick("훅 스크립트가 올바르지 않아요.", "The hook script is invalid."));
            return (description, @event, glob, draft.TimeoutSeconds, draft.Enabled, script);
        }

        /// <summary>The parsed hook, or an error when the file cannot be loaded or does not parse (only parsable files can be edited).</summary>
        private static ProjectHook ReadParsed(string file)
        {
            if (!File.Exists(file)) throw new FileNotFoundException(PompLanguage.Pick("훅 파일이 더 이상 없어요.", "The hook file no longer exists."));
            var probe = ToolResourceFiles.Examine(file, MaxBytes);
            if (probe.Unreadable) throw new InvalidOperationException(probe.Problem);
            try { return ProjectHook.Parse(file, probe.Text); }
            catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        }

        /// <summary>New hook ids are checked by <see cref="Create"/>; a listed one may be any plain file name here.</summary>
        private static string RequireId(string id)
        {
            if (string.IsNullOrEmpty(id) || !IdName.IsMatch(id)) throw new ArgumentException(PompLanguage.Pick("훅 이름이 올바르지 않아요.", "Invalid hook name."));
            return id;
        }

        private string HooksFolder()
        {
            if (root.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("열린 작업 영역이 없어요.", "No workspace is open."));
            var folder = Path.GetFullPath(Path.Combine(root, ".pomp", "hooks"));
            CheckpointStore.CheckDirectPath(folder);
            return folder;
        }

        /// <summary>The file of a listed hook inside the hooks folder: one plain file name, never a path out of it.</summary>
        private string Existing(string id) => ToolResourceFiles.ExistingFile(HooksFolder(), id, ".md", PompLanguage.Pick("훅 이름이 올바르지 않아요.", "Invalid hook name."));

        private static byte[] Encode(string text) => ToolResourceFiles.Encode(text, MaxBytes, "훅 파일", "hook file");

        private static string Bound(string value) => value.Length > 500 ? value.Substring(0, 500) : value;
    }
}
