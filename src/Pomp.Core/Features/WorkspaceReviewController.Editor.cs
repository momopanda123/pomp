using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Ui;

namespace Pomp.Features
{
    // The user's own edits in the IDE as a proposal: list candidates, stage original -> current, apply the selection back to the buffer.
    internal sealed partial class WorkspaceReviewController
    {
        public const string EditorChangesMessage = "workspace/editorChanges";
        private string proposalOrigin = "agent";
        /// <summary>Full current text at staging time, per staged path; only for an editor proposal. Validation compiles original -> selected result while the apply writes captured current -> selected result.</summary>
        private Dictionary<string, string>? editorCurrents;

        private Task HandleEditorChangesAsync(JObject args, string action, CancellationToken cancellationToken) =>
            action == "fromEditor" ? StageFromEditorAsync(args, cancellationToken) : ListEditorChangesAsync(args, cancellationToken);

        private async Task ListEditorChangesAsync(JObject args, CancellationToken cancellationToken)
        {
            var requestId = (string?)args["requestId"] ?? string.Empty;
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!(adapters is IEditorChangeSource source)) throw new InvalidOperationException(PompLanguage.Pick("편집기 변경을 읽을 수 없어요.", "Editor changes are unavailable."));
                var workspaceRoot = await environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                var entries = await source.ListAsync(workspaceRoot, linked.Token).ConfigureAwait(false);
                ui.Post(EditorChangesMessage, new JObject { ["requestId"] = requestId, ["action"] = "editorChanges", ["ok"] = true,
                    ["files"] = new JArray(entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.Ordinal)
                        .Select(e => new JObject { ["path"] = e.Path, ["unsaved"] = e.Unsaved })) });
            }
            catch (OperationCanceledException) { } // Closing: the page is gone with the session.
            catch (Exception ex) { ui.Post(EditorChangesMessage, new JObject { ["requestId"] = requestId, ["action"] = "editorChanges", ["ok"] = false, ["error"] = ex.Message }); }
        }

        private async Task StageFromEditorAsync(JObject args, CancellationToken cancellationToken)
        {
            var requestId = (string?)args["requestId"] ?? string.Empty;
            string error;
            try { error = await TryStageFromEditorAsync(args, cancellationToken).ConfigureAwait(false) ?? string.Empty; }
            catch (OperationCanceledException) { error = PompLanguage.Pick("작업이 취소됐어요.", "Operation cancelled."); }
            catch (Exception ex) { error = PompLanguage.Pick("제안을 만들지 못했어요: " + ex.Message, "Could not create the proposal: " + ex.Message); }
            ui.Post(EditorChangesMessage, new JObject { ["requestId"] = requestId, ["action"] = "fromEditor", ["ok"] = error.Length == 0, ["error"] = error.Length == 0 ? null : error });
        }

        /// <summary>Stages the picked editor texts as an editor proposal. Returns the UI error, or null on success (the proposal arrives through <see cref="StateMessage"/>).</summary>
        private async Task<string?> TryStageFromEditorAsync(JObject args, CancellationToken cancellationToken)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var admissionError = AdmissionCheck?.Invoke(); if (admissionError != null) return admissionError;
            if (!(adapters is IEditorChangeSource source)) return PompLanguage.Pick("편집기 변경을 읽을 수 없어요.", "Editor changes are unavailable.");
            if (!(args["paths"] is JArray requested) || requested.Count == 0 || requested.Count > 10 || requested.Any(p => p.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)p)))
                return PompLanguage.Pick("선택한 파일이 올바르지 않아요.", "The selected files are invalid.");
            if (!await operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return PompLanguage.Pick("다른 검토 작업이 진행 중이에요.", "Another review operation is in progress.");
            try
            {
                lock (gate) if (shadow != null && status != "done") return PompLanguage.Pick("검토 중인 제안이 있어요.", "A proposal is already awaiting review.");
                var workspaceRoot = await environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                var texts = (await source.ReadAsync(workspaceRoot, requested.Select(p => (string)p!).ToArray(), linked.Token).ConfigureAwait(false))
                    .Where(t => t != null && t.Original != t.Current).ToList();
                if (texts.Count == 0) return PompLanguage.Pick("선택한 파일에 변경이 없어요.", "The selected files have no changes.");
                var candidate = new ShadowWorkspace();
                var candidateDiffs = new Dictionary<string, LineDiff>(StringComparer.OrdinalIgnoreCase);
                var candidateSelections = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                var currents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                long previewCharacters = 0;
                foreach (var text in texts)
                {
                    if (string.IsNullOrWhiteSpace(text.Path) || candidateDiffs.ContainsKey(text.Path)) return PompLanguage.Pick("같은 파일이 두 번 들어왔어요.", "A file was listed more than once.");
                    if (text.Original.Length > 500000 || text.Current.Length > 500000) return PompLanguage.Pick("제안이 너무 커요.", "The proposal is too large.");
                    if (text.Original.IndexOf('\0') >= 0 || text.Current.IndexOf('\0') >= 0) return PompLanguage.Pick("텍스트가 아닌 파일이 들어왔어요.", "A selected file is not text.");
                    var diff = candidate.Stage(new ShadowFile(text.Path, text.Original, text.Current), linked.Token);
                    previewCharacters += diff.Hunks.Sum(h => (long)h.Before.Length + h.After.Length + 150);
                    if (previewCharacters > 180000) return PompLanguage.Pick("제안이 너무 커요.", "The proposal is too large.");
                    candidateDiffs.Add(text.Path, diff); candidateSelections.Add(text.Path, new HashSet<int>(diff.Hunks.Select(h => h.Id))); currents.Add(text.Path, text.Current);
                }
                if (candidate.Snapshot().Files.Count == 0) return PompLanguage.Pick("선택한 파일에 변경이 없어요.", "The selected files have no changes.");
                linked.Token.ThrowIfCancellationRequested();
                if (!string.Equals(workspaceRoot, await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return PompLanguage.Pick("작업 영역이 바뀌었어요.", "The workspace changed.");
                linked.Token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    shadow = candidate; diffs = candidateDiffs; selections = candidateSelections; root = workspaceRoot;
                    id = Guid.NewGuid().ToString("N"); status = "reviewing"; message = string.Empty; validation = null; result = null;
                    proposalOrigin = "editor"; editorCurrents = currents;
                }
                Publish();
                return null;
            }
            finally { operation.Release(); }
        }

        /// <summary>The apply/checkpoint snapshot: validated as staged for an agent proposal; for an editor proposal see <see cref="ForEditor"/>.</summary>
        private ShadowSnapshot ApplySnapshot(ShadowWorkspace current) => ForEditor(current.GetValidatedSnapshot());

        /// <summary>
        /// An editor proposal as the editor sees it: every staged file from its captured current text (what the buffer holds, so the live-source
        /// checks of validation and apply pass and undo restores the user's full text) to the original plus its selected hunks. A file with no
        /// selected hunk goes back to its original; a file whose result equals its current text is left out. Agent proposals pass unchanged.
        /// </summary>
        private ShadowSnapshot ForEditor(ShadowSnapshot snapshot)
        {
            Dictionary<string, string>? currents; Dictionary<string, LineDiff> originals;
            lock (gate) { currents = proposalOrigin == "editor" ? editorCurrents : null; originals = diffs; }
            if (currents == null) return snapshot;
            var chosen = snapshot.Files.ToDictionary(file => file.Path, file => file.After, StringComparer.OrdinalIgnoreCase);
            var files = new List<ShadowFile>(currents.Count);
            foreach (var pair in currents.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var after = chosen.TryGetValue(pair.Key, out var selected) ? selected : originals.TryGetValue(pair.Key, out var diff) ? diff.Before : null;
                if (after == null) throw new InvalidOperationException(PompLanguage.Pick("제안을 만들 때의 편집기 내용을 잃었어요.", "The captured editor text of the proposal was lost."));
                if (after != pair.Value) files.Add(new ShadowFile(pair.Key, pair.Value, after));
            }
            return new ShadowSnapshot(snapshot.Revision, files.AsReadOnly());
        }

        /// <summary>The validator for the open proposal: an editor proposal is compiled as <see cref="ForEditor"/> maps it (nothing to compile when the editor already holds the result).</summary>
        private IShadowValidator ValidatorFor(IShadowValidator validator)
        {
            lock (gate) return proposalOrigin == "editor" ? new EditorValidator(this, validator) : validator;
        }

        private sealed class EditorValidator : IShadowValidator
        {
            private readonly WorkspaceReviewController owner; private readonly IShadowValidator inner;
            public EditorValidator(WorkspaceReviewController owner, IShadowValidator inner) { this.owner = owner; this.inner = inner; }
            public Task<ShadowValidation> ValidateAsync(ShadowSnapshot snapshot, CancellationToken cancellationToken)
            {
                var mapped = owner.ForEditor(snapshot);
                return mapped.Files.Count == 0 ? Task.FromResult(new ShadowValidation(true, Array.Empty<ShadowDiagnostic>())) : inner.ValidateAsync(mapped, cancellationToken);
            }
        }
    }
}
