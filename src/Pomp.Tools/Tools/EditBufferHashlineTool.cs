using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>Validated hashline edits with one frozen, complete approval preview and one undo transaction per file.</summary>
    /// <remarks>
    /// All PUT/CUT anchors address original snapshot lines, never the results of an earlier operation. TAGs are host-only,
    /// must be known and unambiguous, and anchors/ranges must have been fully shown by a read. Preparation validates every
    /// file before approval; execution rechecks every full hash before any write, and the adapter checks each hash atomically.
    /// Multi-file edits are not an atomic transaction: a later failure reports the files already changed without rollback.
    /// </remarks>
    internal sealed class EditBufferHashlineTool : HostToolBase, IPreparedHostTool
    {
        public EditBufferHashlineTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            // The limit is stated in the description and enforced by the parser; schemas stay within the keyword subset every provider accepts.
            var parameters = new SchemaBuilder()
                .String("input", "Patch text: one or more [path#TAG] sections followed by PUT/CUT operations. Maximum " + HashlinePatch.MaxInputCharacters + " characters.", required: true, minLength: 1)
                .Build();
            return new HostToolDefinition
            {
                Name = HostToolNames.EditBufferHashline,
                Label = "Edit Buffer (hashline)",
                Description =
                    "Edit text through Visual Studio's editor buffer, preserving unsaved changes and using one undo step per file; needs file-write approval with a complete diff. " +
                    "Read files with read_buffer first. `input` contains [path#TAG] sections copied from read_buffer/get_editor_context, followed by operations on ORIGINAL lines: " +
                    "`PUT N.=M:` replaces inclusive lines N..M with following `+text` rows; `PUT <N:` / `PUT >N:` insert before/after line N; " +
                    "`PUT >$:` appends; `CUT N.=M` deletes. Each PUT needs at least one + row; a lone + means an empty line. " +
                    "An empty file accepts PUT <1: or PUT >$:. Operations must be ordered and non-overlapping; duplicate insertion positions are rejected. " +
                    "Anchors and complete ranges must have been shown. Optional *** Begin Patch / *** End Patch wrappers must be paired. " +
                    "Block anchors, registers, REM and MV are unsupported. Maximum " + HashlinePatch.MaxFiles + " files, " + HashlinePatch.MaxOperations + " operations, " +
                    HashlinePatch.MaxPatchLines + " rows and " + HashlinePatch.MaxInputCharacters + " characters per call. " +
                    "Changes are not saved to disk. Multi-file edits can partially succeed; no cross-file rollback is promised. Fresh tags do not mark new lines as seen: read again before a subsequent anchored edit.",
                Parameters = parameters,
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.FileWrite;

        public async Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (invocation == null)
            {
                throw new ToolException("No invocation was provided.");
            }

            var arguments = new ToolArguments(invocation.Arguments, "input");
            var input = arguments.RequiredString("input", trim: false);
            if (arguments.TryGetError(Name, out var invalid))
            {
                throw new ToolException(invalid.Content[0].Text ?? "Invalid arguments.");
            }

            var sections = HashlinePatch.Parse(input);
            var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<HashlineFilePlan>();
            long characters = 0;
            foreach (var section in sections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!guard.TryResolve(section.Path, out var path, out var pathError))
                {
                    throw new ToolException(pathError);
                }

                if (!paths.Add(path))
                {
                    throw new ToolException("Duplicate file section for '" + guard.ToDisplayPath(path) + "'; put its ordered operations in a single section.");
                }

                var display = guard.ToDisplayPath(path);
                if (Directory.Exists(path))
                {
                    throw new ToolException("'" + display + "' is a directory, not a text file.");
                }

                if (!Context.Snapshots.TryGetUnambiguous(path, section.Tag, out var entry))
                {
                    throw new ToolException("Unknown, evicted or ambiguous TAG '" + section.Tag + "' for '" + display + "'. Use read_buffer to read the file again.");
                }

                var snapshot = await ReadCheckedAsync(path, display, cancellationToken).ConfigureAwait(false);
                var file = HashlineFilePlan.Create(section, entry, snapshot.Text, path, display);
                characters += file.OriginalText.Length + file.ResultText.Length;
                if (characters > BufferSnapshotStore.DefaultMaxTotalCharacters)
                {
                    throw new ToolException("The prepared patch exceeds the total text budget; split it into smaller calls.");
                }

                files.Add(file);
            }

            var frozen = files.AsReadOnly();
            var diff = HashlineDiff.Create(frozen);
            cancellationToken.ThrowIfCancellationRequested();
            var operations = CountOperations(sections);
            var permission = NewPermissionRequest(PermissionKind.FileWrite,
                files.Count == 1 ? PompLanguage.Pick("파일을 편집할까요?", "Edit the file?") : PompLanguage.Pick("여러 파일을 편집할까요?", "Edit multiple files?"),
                PompLanguage.Pick("작업: " + operations + "개\n파일: " + files.Count + "개", "Operations: " + operations + "\nFiles: " + files.Count),
                string.Join(", ", DisplayPaths(frozen)));
            permission.Diff = diff;
            return new PreparedCall(this, frozen, guard.Root, permission);
        }

        public override async Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            using (var prepared = await PrepareAsync(invocation, cancellationToken).ConfigureAwait(false))
            {
                return prepared.Permission;
            }
        }

        protected override Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(Fail("Execution requires the exact prepared call after approval; direct execution is not allowed."));

        private async Task<BufferSnapshot> ReadCheckedAsync(string path, string display, CancellationToken cancellationToken)
        {
            var snapshot = await Adapters.TextBuffers.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
            {
                throw new ToolException("File not found: '" + display + "'. Read the file again before editing.");
            }

            if (snapshot.Text == null || snapshot.Text.Length > BufferSnapshotStore.DefaultMaxCharactersPerSnapshot)
            {
                throw new ToolException("'" + display + "' exceeds the supported text snapshot size.");
            }

            if (snapshot.Text.IndexOf('\0') >= 0)
            {
                throw new ToolException("'" + display + "' contains NUL characters; only text files can be edited.");
            }

            var actualHash = TextUtil.ComputeContentHash(TextUtil.NormalizeLineEndings(snapshot.Text));
            if (!TextUtil.IsContentHash(snapshot.ContentHash) || !string.Equals(actualHash, snapshot.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("The adapter returned an inconsistent full content hash for '" + display + "'; no edit can be approved or applied.");
            }

            return snapshot;
        }

        private async Task<HostToolResult> ExecutePreparedAsync(IReadOnlyList<HashlineFilePlan> files, string root, CancellationToken cancellationToken)
        {
            var outcomes = new List<JObject>();
            string? failure = null;
            var next = 0;
            var attempted = false;
            try
            {
                var guard = await GetSameGuardAsync(root, cancellationToken).ConfigureAwait(false);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidatePath(guard, file);
                    var snapshot = await ReadCheckedAsync(file.Path, file.DisplayPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(TextUtil.ComputeContentHash(TextUtil.NormalizeLineEndings(snapshot.Text)), file.ExpectedHash, StringComparison.Ordinal))
                    {
                        throw new ToolException("'" + file.DisplayPath + "' changed while awaiting approval; all-file prevalidation failed. Read the file again.");
                    }
                }

                for (; next < files.Count;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    guard = await GetSameGuardAsync(root, cancellationToken).ConfigureAwait(false);
                    var file = files[next];
                    ValidatePath(guard, file);
                    attempted = true;
                    var request = file.ToAdapterRequest();
                    request.ExpectedWorkspaceRoot = root; // re-checked by the adapter right before it edits, on the UI thread
                    var result = await Adapters.TextBuffers.ApplyEditsAsync(request, cancellationToken).ConfigureAwait(false);
                    if (result == null || (result.Applied && result.Stale))
                    {
                        throw new ToolException("The editor adapter returned no reliable application result.");
                    }

                    if (!result.Applied)
                    {
                        outcomes.Add(new JObject
                        {
                            ["path"] = file.DisplayPath,
                            ["status"] = result.Stale ? "stale" : "failed",
                        });
                        failure = "'" + file.DisplayPath + "' was not changed: " + TextUtil.ToSingleLine(result.Error ?? (result.Stale ? "the full hash changed; read the file again." : "the editor rejected the edit."), 500);
                        next++;
                        attempted = false;
                        break;
                    }

                    var outcome = new JObject { ["path"] = file.DisplayPath, ["status"] = "applied" };
                    outcomes.Add(outcome);
                    attempted = false;
                    next++;
                    if (!RecordApplied(file, result, outcome))
                    {
                        failure = "'" + file.DisplayPath + "' was applied, but the adapter did not confirm the approved result. Further files were not attempted; read the file again before retrying.";
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        // The text is the approved result, but Visual Studio reported a problem while finishing the edit (e.g. its undo step).
                        var reported = "Applied as approved, but Visual Studio reported: " + TextUtil.ToSingleLine(result.Error, 300) + " The single undo step may be incomplete.";
                        outcome["warning"] = outcome["warning"] == null ? reported : (string?)outcome["warning"] + " " + reported;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ToolErrors.Describe(Name, ex, cancellationToken);
                if (attempted && next < files.Count)
                {
                    Context.Snapshots.Remove(files[next].Path);
                    outcomes.Add(new JObject { ["path"] = files[next].DisplayPath, ["status"] = "uncertain" });
                    failure += " The adapter failed during application; this file may have changed. Read it again before retrying.";
                    next++;
                }
            }

            for (; next < files.Count; next++)
            {
                outcomes.Add(new JObject { ["path"] = files[next].DisplayPath, ["status"] = "notAttempted" });
            }

            return BuildResult(outcomes, failure);
        }

        private async Task<PathGuard> GetSameGuardAsync(string root, CancellationToken cancellationToken)
        {
            var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(root, guard.Root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("The workspace changed after preparation; prepare and approve a new patch.");
            }

            return guard;
        }

        private static void ValidatePath(PathGuard guard, HashlineFilePlan file)
        {
            if (!guard.TryResolve(file.Path, out var path, out var error))
            {
                throw new ToolException(error);
            }

            if (!string.Equals(path, file.Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("The target path changed after preparation.");
            }
        }

        private bool RecordApplied(HashlineFilePlan file, BufferEditResult result, JObject outcome)
        {
            if (!TextUtil.IsContentHash(result.NewContentHash) || !string.Equals(result.NewContentHash, file.ResultHash, StringComparison.OrdinalIgnoreCase))
            {
                Context.Snapshots.Remove(file.Path);
                outcome["warning"] = "The adapter applied the edit but did not confirm the expected result hash. Read the file again; no fresh tag is being issued.";
                return false;
            }

            var lines = TextUtil.SplitLines(file.ResultText);
            var entry = Context.Snapshots.Record(file.Path, file.ResultHash, lines, 1, 0);
            if (entry == null)
            {
                outcome["warning"] = "The edit was applied, but the result is too large to retain an edit snapshot. Read the file again.";
                return true;
            }

            if (!Context.Snapshots.TryGetUnambiguous(file.Path, entry.Tag, out _))
            {
                outcome["warning"] = "The edit was applied, but its new TAG collides with a retained version. Read the file again; no ambiguous tag is being issued.";
                return true;
            }

            outcome["tag"] = entry.Tag;
            outcome["totalLines"] = lines.Count;
            return true;
        }

        private static HostToolResult BuildResult(IReadOnlyList<JObject> outcomes, string? failure)
        {
            var builder = new StringBuilder();
            var applied = 0;
            var uncertain = false;
            foreach (var outcome in outcomes)
            {
                if ((string?)outcome["status"] == "applied")
                {
                    applied++;
                }
                else if ((string?)outcome["status"] == "uncertain")
                {
                    uncertain = true;
                }
            }

            if (failure == null)
            {
                builder.Append("Applied editor-buffer edits to ").Append(applied).Append(" file(s), one undo transaction per file; not saved to disk.");
                foreach (var outcome in outcomes)
                {
                    if (outcome["warning"] != null)
                    {
                        builder.Append(" Check the warnings below.");
                        break;
                    }
                }
            }
            else
            {
                builder.Append(applied > 0 ? "Partial failure: " : uncertain ? "Application outcome uncertain: " : "No files were applied: ")
                    .Append(failure);
                if (applied > 0)
                {
                    builder.Append(" Earlier applied files remain changed; no cross-file rollback was performed.");
                }
            }

            foreach (var outcome in outcomes)
            {
                var display = (string?)outcome["path"] ?? string.Empty;
                var tag = (string?)outcome["tag"];
                builder.Append('\n');
                if (tag != null)
                {
                    builder.Append(HashlineFormat.Header(display, tag)).Append(" [applied; ").Append((int?)outcome["totalLines"])
                        .Append(" lines; new lines have not been shown—read_buffer again before an anchored edit]");
                }
                else
                {
                    builder.Append(display).Append(": ").Append((string?)outcome["status"]);
                }

                if (outcome["warning"] != null)
                {
                    builder.Append(" — ").Append((string?)outcome["warning"]);
                }
            }

            return new HostToolResult(new[] { HostToolContent.FromText(builder.ToString()) }, failure != null,
                new JObject { ["files"] = new JArray(outcomes), ["appliedFiles"] = applied, ["partial"] = failure != null && applied > 0, ["uncertain"] = uncertain });
        }

        private static IEnumerable<string> DisplayPaths(IReadOnlyList<HashlineFilePlan> files)
        {
            foreach (var file in files)
            {
                yield return file.DisplayPath;
            }
        }

        private static int CountOperations(IReadOnlyList<HashlineSection> sections)
        {
            var count = 0;
            foreach (var section in sections)
            {
                count += section.Operations.Count;
            }

            return count;
        }

        private sealed class PreparedCall : IPreparedHostToolCall
        {
            private readonly EditBufferHashlineTool _owner;
            private IReadOnlyList<HashlineFilePlan>? _files;
            private readonly string _root;
            private int _state;

            public PreparedCall(EditBufferHashlineTool owner, IReadOnlyList<HashlineFilePlan> files, string root, PermissionRequest permission)
            {
                _owner = owner;
                _files = files;
                _root = root;
                Permission = permission;
            }

            public PermissionRequest Permission { get; }

            public Task<HostToolResult> ExecuteAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                {
                    return Task.FromResult(_owner.Fail("This prepared call has already been executed or disposed; prepare and approve a new call."));
                }

                var files = Interlocked.Exchange(ref _files, null);
                return files == null
                    ? Task.FromResult(_owner.Fail("This prepared call was disposed; prepare and approve a new call."))
                    : _owner.ExecutePreparedAsync(files, _root, cancellationToken);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _state, 2);
                Interlocked.Exchange(ref _files, null);
            }
        }
    }
}
