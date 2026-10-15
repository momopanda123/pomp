using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Json;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// Saved workflows edited from the Tools tab: <c>.pomp/workflows/&lt;name&gt;.json</c> (project and user).
    /// Files the page cannot edit stay listed with a note so they can still be opened or deleted.
    /// </summary>
    internal sealed class WorkflowCatalog
    {
        internal const int MaxBytes = 32768;
        private const int MaxSteps = 32;
        private readonly string root, home;
        private static readonly Regex NewName = new Regex(@"\A[a-z0-9-]{2,40}\z", RegexOptions.CultureInvariant);
        internal WorkflowCatalog(string root, string? home) { this.root = Path.GetFullPath(root); this.home = ToolResourceFiles.Home(home); }
        internal string DirectoryFor(string scope) => Path.Combine(ToolResourceFiles.Scope(scope, root, home), ".pomp", "workflows");

        /// <summary>The state message sends task and steps while the whole message stays within this many bytes, in list order; the rest of the workflows are summaries.</summary>
        internal const int DetailBudget = 1024 * 1024;
        /// <summary>
        /// The state message never passes this many bytes, even as summaries: a message above 2 MiB is dropped by the UI hub, and the page's whole queue with it.
        /// Workflows that would not fit are left out (with a diagnostic); the margin below 2 MiB takes that diagnostic.
        /// </summary>
        internal const int StateLimit = 1792 * 1024;

        internal WorkflowsStatePayload View()
        {
            lock (ToolResourceFiles.Gate)
            {
                var result = new WorkflowsStatePayload();
                var sizes = new List<(int Full, int Summary)>(); // Per listed workflow: its JSON bytes with and without task and steps.
                long detail = 0; var detailed = true; // Task and steps stay in memory only while they could still fit the budget.
                foreach (var scope in new[] { "project", "user" })
                {
                    var folder = DirectoryFor(scope);
                    try
                    {
                        CheckpointStore.CheckDirectPath(folder);
                        if (!Directory.Exists(folder)) continue;
                        foreach (var file in FilesIn(folder))
                        {
                            try
                            {
                                var name = Path.GetFileNameWithoutExtension(file);
                                if (name.Length == 0) { Diagnostic(Path.GetFileName(file) + ": " + PompLanguage.Pick("이름이 없는 파일은 표시하지 않아요.", "A file without a name is not shown.")); continue; }
                                var probe = ToolResourceFiles.Examine(file, MaxBytes, folderChecked: true); // The folder was checked just above: a walk up the path per file would dominate a big folder.
                                var info = new WorkflowInfoPayload { Name = name, Scope = scope, File = file, Hash = probe.Hash ?? "" };
                                if (probe.Unreadable) { info.Unreadable = true; info.Note = probe.Problem; }
                                else Describe(info, probe.Text);
                                info.StepCount = info.Steps?.Count ?? 0;
                                var full = Bytes(info);
                                var task = info.Task; var steps = info.Steps;
                                info.Task = null; info.Steps = null; info.Summary = true;
                                var summary = Bytes(info);
                                if (detailed && (detail += Math.Max(0, full - summary)) <= DetailBudget) { info.Task = task; info.Steps = steps; info.Summary = null; }
                                else detailed = false;
                                sizes.Add((full, summary)); result.Workflows.Add(info);
                            }
                            catch (Exception ex) { Diagnostic(Path.GetFileName(file) + ": " + ex.Message); }
                        }
                    }
                    catch (Exception ex) { Diagnostic(scope + ": " + ex.Message); }
                }

                Fit(result, sizes);
                return result;
                void Diagnostic(string message) { if (result.Diagnostics.Count < 20) result.Diagnostics.Add(message.Substring(0, Math.Min(500, message.Length))); }
            }
        }

        /// <summary>
        /// Keeps the state message small enough for the UI hub. Every workflow keeps its summary fields (name, scope, file, hash, description, note, unreadable, stepCount);
        /// task and steps stay only for the workflows of the front of the list that fit the budget together with the summaries of all the others, and any other
        /// workflow becomes a summary (<c>summary: true</c>, no task or steps). Workflows past <see cref="StateLimit"/> are left out and the diagnostics say how many.
        /// </summary>
        private static void Fit(WorkflowsStatePayload state, List<(int Full, int Summary)> sizes)
        {
            // What the message costs with no workflow in it, plus every workflow as a summary (and its comma).
            long total = Bytes(new UiMessage(UiMessageTypes.WorkflowsState, new WorkflowsStatePayload { Diagnostics = state.Diagnostics }));
            var count = 0;
            while (count < sizes.Count && total + sizes[count].Summary + 1 <= StateLimit) total += sizes[count++].Summary + 1;
            if (count < sizes.Count)
            {
                var left = sizes.Count - count;
                state.Workflows.RemoveRange(count, left); sizes.RemoveRange(count, left);
                state.Diagnostics.Insert(0, PompLanguage.Pick(left + "개 워크플로는 표시하지 않아요.", left + " workflows are not shown."));
                if (state.Diagnostics.Count > 20) state.Diagnostics.RemoveRange(20, state.Diagnostics.Count - 20);
            }
            // Upgrading a workflow from summary to full adds its details; the first one that would pass the budget, and every later one, stays a summary.
            for (var index = 0; index < count && state.Workflows[index].Summary == null; index++)
            {
                total += sizes[index].Full - sizes[index].Summary;
                if (total <= DetailBudget) continue;
                for (; index < count; index++) { var info = state.Workflows[index]; info.Task = null; info.Steps = null; info.Summary = true; }
            }
        }

        /// <summary>The bytes of a payload (or a whole UI message) as the UI hub counts them: its JSON in UTF-8.</summary>
        private static int Bytes(object payload) => System.Text.Encoding.UTF8.GetByteCount(PompJson.Serialize(payload));

        /// <summary>Every workflow file of a scope folder, in listing order.</summary>
        private static string[] FilesIn(string folder) => Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();

        /// <summary>Anything the in-page editor cannot round-trip (duplicate/unknown keys, wrong shapes) becomes a note with empty task/steps.</summary>
        private static void Describe(WorkflowInfoPayload info, string text)
        {
            string? error = null; var description = "";
            try
            {
                using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
                var document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonException(PompLanguage.Pick("워크플로 파일 끝에 내용이 더 있어요.", "Trailing workflow content."));
                foreach (var property in document.Properties())
                    if (property.Name != "version" && property.Name != "description" && property.Name != "task" && property.Name != "steps")
                        throw new JsonException(PompLanguage.Pick("알 수 없는 워크플로 항목이에요: " + property.Name, "Unknown workflow key: " + property.Name));
                if (document["version"] is not JValue version || version.Type != JTokenType.Integer || version.Value<long>() != 1)
                    throw new JsonException(PompLanguage.Pick("워크플로 version이 올바르지 않아요.", "Invalid workflow version."));
                if (document["description"] is not JValue rawDescription || rawDescription.Type != JTokenType.String)
                    throw new JsonException(PompLanguage.Pick("워크플로 설명이 올바르지 않아요.", "Invalid workflow description."));
                description = (string?)rawDescription.Value ?? "";
                if (description.Length > 500 || description.Any(char.IsControl))
                    throw new JsonException(PompLanguage.Pick("워크플로 설명이 올바르지 않아요.", "Invalid workflow description."));
                if (document["task"] is not JValue rawTask || rawTask.Type != JTokenType.String)
                    throw new JsonException(PompLanguage.Pick("워크플로 작업 내용이 올바르지 않아요.", "Invalid workflow task."));
                var task = (string?)rawTask.Value ?? "";
                if (task.Length is < 1 or > 16000 || task.Contains('\0'))
                    throw new JsonException(PompLanguage.Pick("워크플로 작업 내용이 올바르지 않아요.", "Invalid workflow task."));
                if (document["steps"] is not JArray rawSteps || rawSteps.Count > MaxSteps)
                    throw new JsonException(PompLanguage.Pick("워크플로 단계가 너무 많아요.", "Too many workflow steps."));
                var steps = new List<string>();
                foreach (var item in rawSteps)
                {
                    if (item is not JValue step || step.Type != JTokenType.String) throw new JsonException(PompLanguage.Pick("워크플로 단계가 올바르지 않아요.", "Invalid workflow step."));
                    var value = (string?)step.Value ?? "";
                    if (value.Length is < 1 or > 500 || value.Any(char.IsControl)) throw new JsonException(PompLanguage.Pick("워크플로 단계가 올바르지 않아요.", "Invalid workflow step."));
                    steps.Add(value);
                }

                info.Description = description; info.Task = task; info.Steps = steps;
            }
            catch (JsonException ex) { error = ex.Message; }
            if (error == null) return;
            info.Note = error.Length > 500 ? error.Substring(0, 500) : error;
            info.Description = description; info.Task = ""; info.Steps = new List<string>();
        }

        internal string Create(string scope, string name, WorkflowDraftPayload? draft)
        {
            if (!NewName.IsMatch(name)) throw new ArgumentException(PompLanguage.Pick("이름이 올바르지 않아요.", "The name is invalid."));
            var (description, task, steps) = RequireDraft(draft);
            var bytes = Compose(description, task, steps);
            lock (ToolResourceFiles.Gate)
            {
                var folder = DirectoryFor(scope);
                CheckpointStore.CheckDirectPath(folder);
                var file = Path.Combine(folder, name + ".json");
                ToolResourceFiles.WriteNew(file, bytes);
                return file;
            }
        }

        internal string Update(string scope, string name, string? expectedHash, WorkflowDraftPayload? draft)
        {
            var (description, task, steps) = RequireDraft(draft);
            var bytes = Compose(description, task, steps);
            var file = Existing(scope, name);
            var current = ToolResourceFiles.Examine(file, MaxBytes);
            if (current.Unreadable) throw new InvalidOperationException(current.Problem);
            ToolResourceFiles.Replace(file, bytes, expectedHash, MaxBytes);
            return file;
        }

        internal void Delete(string scope, string name, string? expectedHash)
        {
            lock (ToolResourceFiles.Gate)
                ToolResourceFiles.DeleteIfHash(Existing(scope, name), expectedHash, PompLanguage.Pick("워크플로가 바뀌었어요.", "The workflow changed."));
        }

        /// <summary>
        /// One workflow with its task and steps, read the way the list reads it (for the page to open one the list sent as a summary). A file the editor cannot
        /// load because of its form (a note, empty task and steps) is returned as the list shows it; a missing file or one that cannot be read at all is an error.
        /// </summary>
        internal WorkflowInfoPayload Get(string scope, string name)
        {
            lock (ToolResourceFiles.Gate)
            {
                var file = Existing(scope, name);
                if (!File.Exists(file)) throw new FileNotFoundException(PompLanguage.Pick("워크플로 파일이 더 이상 없어요.", "The workflow file no longer exists."));
                var probe = ToolResourceFiles.Examine(file, MaxBytes);
                if (probe.Unreadable) throw new InvalidOperationException(probe.Problem);
                var info = new WorkflowInfoPayload { Name = name, Scope = scope, File = file, Hash = probe.Hash ?? "" };
                Describe(info, probe.Text);
                info.StepCount = info.Steps?.Count ?? 0;
                return info;
            }
        }

        /// <summary>The file of an existing workflow, for opening in the editor.</summary>
        internal string FileOf(string scope, string name)
        {
            var file = Existing(scope, name);
            if (!File.Exists(file)) throw new FileNotFoundException(PompLanguage.Pick("워크플로 파일이 더 이상 없어요.", "The workflow file no longer exists."));
            return file;
        }

        private static (string Description, string Task, List<string> Steps) RequireDraft(WorkflowDraftPayload? draft)
        {
            if (draft == null) throw new ArgumentException(PompLanguage.Pick("워크플로 내용이 비어 있어요.", "The workflow is empty."));
            var description = draft.Description ?? "";
            if (description.Length > 500 || description.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("워크플로 설명이 올바르지 않아요.", "Invalid workflow description."));
            var task = draft.Task ?? "";
            if (task.Length is < 1 or > 16000 || task.Contains('\0')) throw new ArgumentException(PompLanguage.Pick("워크플로 작업 내용이 올바르지 않아요.", "Invalid workflow task."));
            var steps = draft.Steps ?? new List<string>();
            if (steps.Count > MaxSteps || steps.Any(step => step == null || step.Length is < 1 or > 500 || step.Any(char.IsControl)))
                throw new ArgumentException(PompLanguage.Pick("워크플로 단계가 올바르지 않아요.", "Invalid workflow steps."));
            return (description, task, new List<string>(steps));
        }

        /// <summary>Indented JSON with "\n" line endings, strict UTF-8 without BOM, at most <see cref="MaxBytes"/> bytes (what the listing reads).</summary>
        private static byte[] Compose(string description, string task, List<string> steps)
        {
            var document = new JObject { ["version"] = 1, ["description"] = description, ["task"] = task, ["steps"] = new JArray(steps) };
            var text = document.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
            return ToolResourceFiles.Encode(text, MaxBytes, "워크플로 파일", "workflow file");
        }

        /// <summary>The file of a listed workflow inside its scope folder: one plain file name, never a path out of it.</summary>
        private string Existing(string scope, string name) => ToolResourceFiles.ExistingFile(DirectoryFor(scope), name, ".json", PompLanguage.Pick("워크플로 이름이 올바르지 않아요.", "Invalid workflow name."));
    }
}
