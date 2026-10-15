using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Standalone reusable storage + validation core for structured case feedback (assistant-generated plans, progress,
    /// test results, alternatives rendered as cards; user selects cases and writes per-case comments). Bounded and
    /// isolated: personal user data lives in the workspace's <c>.pomp/local/case-feedback.json</c>, which that folder's own .gitignore keeps out of commits.
    /// Main owns integration (model-facing creation, UI state protocol, durable feedback dispatch and queue
    /// acceptance). Cross-process at-most-once dispatch claim: <see cref="TryClaimDispatch"/> moves a submitted
    /// proposal to dispatching under the filesystem lease; only the claim holder may complete it via
    /// <see cref="MarkAccepted"/>. A dispatching row left behind by a crash is never retried automatically
    /// (its outcome is uncertain).
    ///
    /// Failure policy: a malformed or oversized store file fails CLOSED on mutations (create/draft/submit/claim/
    /// accept/delete throw and never overwrite user feedback); list/get report unavailable instead. At capacity,
    /// creation is refused with an actionable error; nothing is silently evicted. PrepareSubmit of identical content
    /// is idempotent (returns the same host-issued submission token); a conflicting resubmit is rejected.
    /// </summary>
    internal sealed class CaseFeedbackStore
    {
        internal const int MaxProposals = 100;
        internal const int MaxBytes = 1 * 1024 * 1024;
        internal const int MaxCases = 32;
        internal const int MaxTitleChars = 120;
        internal const int MaxBodyChars = 8000;
        internal const int MaxCommentChars = 2000;
        internal const int MaxExtraChars = 2000;
        internal const int MaxSubmitBytes = 256 * 1024;
        internal const int MaxTargetTitleChars = 40;
        internal const int MaxSessionChars = 256;
        internal const int MaxImagePathChars = 260;
        /// <summary>1 MiB: its base64 data URL (about 1.4 MB) must fit one page message (UiHub drops messages over 2 MiB).</summary>
        internal const int MaxImageBytes = 1024 * 1024;
        internal const int MaxDiagramChars = 4000;
        internal const int MaxImageCases = 8;

        internal static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        private static readonly Regex CaseIdRegex = new Regex(@"^[a-z0-9-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex GuidNRegex = new Regex(@"^[0-9a-f]{32}$", RegexOptions.Compiled);
        private static readonly Regex TargetNameRegex = new Regex(@"^[a-z0-9-]{2,40}$", RegexOptions.Compiled);
        private static readonly Regex InstanceIdRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.Compiled);
        private static readonly HashSet<string> ProposalFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "proposalId", "sessionId", "target", "revision", "status", "cases", "draft", "submitted", "createdAt",
        };
        private static readonly HashSet<string> TargetFields = new HashSet<string>(StringComparer.Ordinal) { "kind", "id", "title" };
        private static readonly HashSet<string> CaseFields = new HashSet<string>(StringComparer.Ordinal) { "caseId", "title", "bodyMarkdown", "image", "diagram" };
        private static readonly HashSet<string> DraftFields = new HashSet<string>(StringComparer.Ordinal) { "revision", "selections" };
        private static readonly HashSet<string> SelectionFields = new HashSet<string>(StringComparer.Ordinal) { "caseId", "selected", "comment", "extra" };
        private static readonly HashSet<string> SubmittedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "proposalId", "sessionId", "revision", "submissionToken", "selections", "submittedAt",
        };

        private static void RejectUnknownFields(JObject obj, HashSet<string> allowed, string what)
        {
            foreach (var prop in obj.Properties())
                if (!allowed.Contains(prop.Name))
                    throw new InvalidDataException("Unknown " + what + " field.");
        }

        private static readonly object Gate = new object();

        private readonly string file;
        private readonly string lockFile;
        private readonly IPompLogger logger;

        public CaseFeedbackStore(string dataDirectory, string workspaceRoot, IPompLogger logger)
        {
            file = WorkspaceJsonFile.PathFor(dataDirectory, "case-feedback", workspaceRoot);
            lockFile = file + ".lock";
            this.logger = logger;
        }

        public sealed class CaseInput
        {
            public string CaseId { get; set; } = "";
            public string Title { get; set; } = "";
            public string BodyMarkdown { get; set; } = "";
            public string? Image { get; set; }
            public string? Diagram { get; set; }
        }

        public sealed class DraftSelection
        {
            public string CaseId { get; set; } = "";
            public bool Selected { get; set; }
            public string? Comment { get; set; }
            public string? Extra { get; set; }
        }

        public sealed class ProposalSummary
        {
            public string ProposalId { get; set; } = "";
            public string SessionId { get; set; } = "";
            public AgentRunTargetPayload Target { get; set; } = new AgentRunTargetPayload();
            public int Revision { get; set; }
            public int CaseCount { get; set; }
            public string Status { get; set; } = "";
            public string CreatedAt { get; set; } = "";
        }

        public sealed class ProposalCase
        {
            public string CaseId { get; set; } = "";
            public string Title { get; set; } = "";
            public string BodyMarkdown { get; set; } = "";
            public string? Image { get; set; }
            public string? Diagram { get; set; }
        }

        public sealed class ProposalView
        {
            public string ProposalId { get; set; } = "";
            public string SessionId { get; set; } = "";
            public AgentRunTargetPayload Target { get; set; } = new AgentRunTargetPayload();
            public int Revision { get; set; }
            public string Status { get; set; } = "";
            public List<ProposalCase> Cases { get; set; } = new List<ProposalCase>();
            public List<DraftSelection> Draft { get; set; } = new List<DraftSelection>();
            public int DraftRevision { get; set; }
            public SubmittedFeedback? Submitted { get; set; }
            public string CreatedAt { get; set; } = "";
        }

        public sealed class SubmittedFeedback
        {
            public string ProposalId { get; set; } = "";
            public string SessionId { get; set; } = "";
            public int Revision { get; set; }
            public string SubmissionToken { get; set; } = "";
            public List<DraftSelection> Selections { get; set; } = new List<DraftSelection>();
            public string SubmittedAt { get; set; } = "";
        }

        /// <summary>
        /// Filesystem lease serializing every mutation's read+validate+save across processes (multiple VS
        /// instances). Same-process callers additionally share <see cref="Gate"/>. The lease file is opened
        /// <c>FileShare.None</c> and held for the whole mutation; acquisition failure refuses the operation
        /// (no automatic destructive retry). Read-only List/Get/Unavailability take no lease and stay bounded.
        /// </summary>
        private FileStream AcquireLease()
        {
            CheckpointStore.CheckDirectPath(lockFile);
            var dir = Path.GetDirectoryName(lockFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            CheckpointStore.CheckDirectPath(lockFile);
            try
            {
                return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick(
                    "다른 창에서 제안 보관함을 쓰고 있어요.",
                    "Case feedback storage is busy in another window."), ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick(
                    "다른 창에서 제안 보관함을 쓰고 있어요.",
                    "Case feedback storage is busy in another window."), ex);
            }
        }

        /// <summary>Store unavailable (malformed/oversized file): reads report this instead of data.</summary>
        public sealed class StoreUnavailable
        {
            public string Reason { get; set; } = "";
        }

        /// <summary>Creates a host-issued proposal (proposalId is a fresh GUID-N). Cases must be non-empty, unique, bounded.</summary>
        public string CreateProposal(string sessionId, AgentRunTargetPayload target, IList<CaseInput> cases, string? workspaceRoot = null)
        {
            ValidateSession(sessionId);
            ValidateTarget(target);
            var validCases = ValidateCases(cases, workspaceRoot);
            lock (Gate)
            using (AcquireLease())
            {
                var state = ReadLocked();
                if (state.Count >= MaxProposals)
                    throw new InvalidOperationException(PompLanguage.Pick(
                        "제안 보관함이 가득 찼어요.",
                        "The proposal store is full."));
                var id = Guid.NewGuid().ToString("N");
                state.Insert(0, new JObject
                {
                    ["proposalId"] = id,
                    ["sessionId"] = sessionId,
                    ["target"] = new JObject { ["kind"] = target.Kind, ["id"] = target.Id, ["title"] = target.Title },
                    ["revision"] = 1,
                    ["status"] = "draft",
                    ["cases"] = new JArray(validCases.Select(c => EncodeCase(c))),
                    ["draft"] = new JObject { ["revision"] = 0, ["selections"] = new JArray() },
                    ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
                });
                Save(state);
                return id;
            }
        }

        /// <summary>Newest first, optionally filtered by session. Empty when the store is unavailable (see <see cref="Unavailability"/>).</summary>
        public IReadOnlyList<ProposalSummary> List(string? sessionId = null)
        {
            lock (Gate)
            {
                try
                {
                    return ListCore(ReadLocked(), sessionId);
                }
                catch (Exception ex)
                {
                    logger.Warn("Case feedback store unavailable for reads: " + ex.Message);
                    return new List<ProposalSummary>().AsReadOnly();
                }
            }
        }

        internal (IReadOnlyList<ProposalSummary> Entries, ProposalView? Proposal) Snapshot(string sessionId, string? proposalId)
        {
            if (proposalId != null && !GuidNRegex.IsMatch(proposalId))
                throw new ArgumentException(PompLanguage.Pick("제안 ID가 올바르지 않아요.", "Invalid proposal id."), nameof(proposalId));
            lock (Gate)
            {
                var state = ReadLocked();
                var entries = ListCore(state, sessionId);
                var selectedId = proposalId ?? entries.FirstOrDefault()?.ProposalId;
                var entry = state.OfType<JObject>().FirstOrDefault(e => (string?)e["proposalId"] == selectedId);
                if (entry != null && (string?)entry["sessionId"] != sessionId)
                    throw new InvalidOperationException(PompLanguage.Pick("다른 대화의 피드백이에요.", "Feedback belongs to another conversation."));
                return (entries, entry == null ? null : ViewOf(entry));
            }
        }

        private static IReadOnlyList<ProposalSummary> ListCore(JArray state, string? sessionId)
        {
            return state.OfType<JObject>()
                .Where(e => sessionId == null || (string?)e["sessionId"] == sessionId)
                .Select(e => new ProposalSummary
                {
                    ProposalId = (string?)e["proposalId"] ?? "",
                    SessionId = (string?)e["sessionId"] ?? "",
                    Target = ReadTarget(e["target"] as JObject),
                    Revision = (int?)e["revision"] ?? 0,
                    CaseCount = (e["cases"] as JArray)?.Count ?? 0,
                    Status = (string?)e["status"] ?? "",
                    CreatedAt = (string?)e["createdAt"] ?? "",
                }).ToList().AsReadOnly();
        }

        /// <summary>Null when the store is unavailable; otherwise the original proposal plus latest draft and any submitted feedback.</summary>
        public ProposalView? Get(string proposalId)
        {
            if (string.IsNullOrWhiteSpace(proposalId) || !GuidNRegex.IsMatch(proposalId))
                throw new ArgumentException(PompLanguage.Pick("제안 ID가 올바르지 않아요.", "Invalid proposal id."), nameof(proposalId));
            lock (Gate)
            {
                try
                {
                    var entry = ReadLocked().OfType<JObject>().FirstOrDefault(e => (string?)e["proposalId"] == proposalId);
                    return entry == null ? null : ViewOf(entry);
                }
                catch (Exception ex)
                {
                    logger.Warn("Case feedback store unavailable for reads: " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>Null when the store is readable; otherwise why reads are unavailable (mutations throw in that state).</summary>
        public StoreUnavailable? Unavailability()
        {
            lock (Gate)
            {
                return Inspect();
            }
        }

        /// <summary>
        /// Persists a draft bound to proposal + session + revision. The stored draft revision increments on each
        /// save; when <paramref name="expectedDraftRevision"/> is provided it must equal the currently stored
        /// draft revision, otherwise the save is refused (stale multi-window draft). Rejects foreign sessions
        /// and stale proposal revisions.
        /// </summary>
        public void SaveDraft(string proposalId, string sessionId, int revision, IList<DraftSelection> selections, int? expectedDraftRevision = null)
        {
            lock (Gate)
            using (AcquireLease())
            {
                var state = ReadLocked();
                var entry = Find(state, proposalId);
                CheckSessionRevision(entry, sessionId, revision);
                if ((string?)entry["status"] != "draft")
                    throw new InvalidOperationException(PompLanguage.Pick("이미 제출된 제안이에요.", "Proposal was already submitted."));
                var draft = entry["draft"] as JObject ?? new JObject { ["revision"] = 0, ["selections"] = new JArray() };
                var current = (int?)draft["revision"] ?? 0;
                if (expectedDraftRevision.HasValue && expectedDraftRevision.Value != current)
                    throw new InvalidOperationException(PompLanguage.Pick("다른 창에서 초안이 바뀌었어요.", "The draft changed in another window."));
                entry["draft"] = new JObject { ["revision"] = checked(current + 1), ["selections"] = EncodeSelections(ValidateSelections(entry, selections)) };
                Save(state);
            }
        }

        /// <summary>
        /// Validates and persists the submitted feedback snapshot as one record with a host-issued submission token.
        /// Storage only: it does not dispatch to the engine. Resubmitting identical content returns the same token
        /// (idempotent, including retries while dispatching); conflicting content on an already-submitted proposal
        /// is rejected. When <paramref name="expectedDraftRevision"/> is provided it must equal the stored draft
        /// revision.
        /// </summary>
        public SubmittedFeedback PrepareSubmit(string proposalId, string sessionId, int revision, IList<DraftSelection> selections, int? expectedDraftRevision = null)
        {
            lock (Gate)
            using (AcquireLease())
            {
                var state = ReadLocked();
                var entry = Find(state, proposalId);
                CheckSessionRevision(entry, sessionId, revision);
                var draft = entry["draft"] as JObject;
                var draftRevision = (int?)draft?["revision"] ?? 0;
                if (expectedDraftRevision.HasValue && expectedDraftRevision.Value != draftRevision)
                    throw new InvalidOperationException(PompLanguage.Pick("다른 창에서 초안이 바뀌었어요.", "The draft changed in another window."));
                var valid = ValidateSelections(entry, selections);
                var payload = new JArray(valid.Select(s => new JObject { ["caseId"] = s.CaseId, ["selected"] = s.Selected, ["comment"] = s.Comment ?? "", ["extra"] = s.Extra ?? "" }));
                if (Encoding.UTF8.GetByteCount(payload.ToString(Formatting.None)) > MaxSubmitBytes)
                    throw new InvalidOperationException(PompLanguage.Pick("제출 내용이 너무 커요.", "Submission is too large."));
                var existing = entry["submitted"] as JObject;
                var status = (string?)entry["status"];
                if ((status == "submitted" || status == "dispatching") && existing != null)
                {
                    if (JToken.DeepEquals(existing["selections"], payload))
                    {
                        return new SubmittedFeedback
                        {
                            ProposalId = proposalId,
                            SessionId = sessionId,
                            Revision = revision,
                            SubmissionToken = (string?)existing["submissionToken"] ?? "",
                            Selections = valid,
                            SubmittedAt = (string?)existing["submittedAt"] ?? "",
                        };
                    }
                    throw new InvalidOperationException(PompLanguage.Pick("이미 다른 내용으로 제출된 제안이에요.", "Proposal was already submitted with different content."));
                }
                if (status != "draft")
                    throw new InvalidOperationException(PompLanguage.Pick("이미 제출된 제안이에요.", "Proposal was already submitted."));
                var token = Guid.NewGuid().ToString("N");
                var submitted = new JObject
                {
                    ["proposalId"] = proposalId,
                    ["sessionId"] = sessionId,
                    ["revision"] = revision,
                    ["submissionToken"] = token,
                    ["selections"] = payload,
                    ["submittedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                };
                entry["submitted"] = submitted;
                entry["status"] = "submitted";
                entry["draft"] = new JObject { ["revision"] = draftRevision, ["selections"] = payload.DeepClone() };
                Save(state);
                return new SubmittedFeedback
                {
                    ProposalId = proposalId,
                    SessionId = sessionId,
                    Revision = revision,
                    SubmissionToken = token,
                    Selections = valid,
                    SubmittedAt = (string?)submitted["submittedAt"] ?? "",
                };
            }
        }

        /// <summary>
        /// Cross-process at-most-once dispatch claim, under the filesystem lease. A submitted proposal whose
        /// token, session and revision all match moves to dispatching and this returns true: the caller now owns
        /// dispatch. A dispatching or accepted proposal with a full match returns false (already claimed or done).
        /// Any mismatch (token, session, revision) or any other status refuses by throwing. A dispatching row
        /// left behind by a crash is never retried automatically (its outcome is uncertain).
        /// </summary>
        public bool TryClaimDispatch(string proposalId, string sessionId, int revision, string submissionToken)
        {
            ValidateSubmissionToken(submissionToken);
            lock (Gate)
            using (AcquireLease())
            {
                var state = ReadLocked();
                var entry = Find(state, proposalId);
                CheckSessionRevision(entry, sessionId, revision);
                var status = (string?)entry["status"];
                if (status == "dispatching" || status == "accepted")
                {
                    if (string.Equals((string?)entry["submitted"]?["submissionToken"], submissionToken, StringComparison.Ordinal))
                        return false;
                    throw new InvalidOperationException(PompLanguage.Pick("제출 토큰이 일치하지 않아요.", "Submission token does not match."));
                }
                if (status != "submitted" || entry["submitted"] == null)
                    throw new InvalidOperationException(PompLanguage.Pick("제출되지 않은 제안이에요.", "The proposal was not submitted."));
                if (!string.Equals((string?)entry["submitted"]!["submissionToken"], submissionToken, StringComparison.Ordinal))
                    throw new InvalidOperationException(PompLanguage.Pick("제출 토큰이 일치하지 않아요.", "Submission token does not match."));
                entry["status"] = "dispatching";
                Save(state);
                return true;
            }
        }

        /// <summary>
        /// Records acceptance of a claimed (dispatching) proposal. Requires the submission token plus matching
        /// proposal/session/revision. Submitted rows must go through <see cref="TryClaimDispatch"/> first.
        /// Queue/engine acceptance itself is main's routing responsibility.
        /// </summary>
        public void MarkAccepted(string proposalId, string sessionId, int revision, string submissionToken)
        {
            ValidateSubmissionToken(submissionToken);
            lock (Gate)
            using (AcquireLease())
            {
                var state = ReadLocked();
                var entry = Find(state, proposalId);
                CheckSessionRevision(entry, sessionId, revision);
                if ((string?)entry["status"] != "dispatching" || entry["submitted"] == null)
                    throw new InvalidOperationException(PompLanguage.Pick("전송 중인 제안이 아니에요.", "The proposal is not being dispatched."));
                if (!string.Equals((string?)entry["submitted"]!["submissionToken"], submissionToken, StringComparison.Ordinal))
                    throw new InvalidOperationException(PompLanguage.Pick("제출 토큰이 일치하지 않아요.", "Submission token does not match."));
                entry["status"] = "accepted";
                Save(state);
            }
        }

        /// <summary>
        /// Deletes a proposal row under the lease (cleanup). Dispatching rows are refused to retain uncertain
        /// evidence. Requires matching session and proposal revision.
        /// </summary>
        public void Delete(string proposalId, string sessionId, int revision)
        {
            lock (Gate)
            using (AcquireLease())
            {
                var state = ReadLocked();
                var entry = Find(state, proposalId);
                CheckSessionRevision(entry, sessionId, revision);
                if (string.Equals((string?)entry["status"], "dispatching", StringComparison.Ordinal))
                    throw new InvalidOperationException(PompLanguage.Pick("전송 중인 제안이에요.", "The proposal is being dispatched."));
                state.Remove(entry);
                Save(state);
            }
        }

        private static readonly Dictionary<string, string> ImageMimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
        };

        /// <summary>
        /// The text of an exception for the page. .NET appends the parameter name to an ArgumentException's message
        /// ("... (Parameter 'cases')"): useful in a log or to the model, meaningless to the user. Other exceptions keep their text.
        /// </summary>
        internal static string UserMessage(Exception ex)
        {
            var text = ex.Message;
            if (ex is ArgumentException argument && !string.IsNullOrEmpty(argument.ParamName))
            {
                // The runtime words (and, on .NET Framework, localizes) that suffix itself: take it from an empty message instead of matching its wording.
                var suffix = new ArgumentException(string.Empty, argument.ParamName).Message;
                var at = suffix.Length == 0 ? -1 : text.LastIndexOf(suffix, StringComparison.Ordinal);
                if (at >= 0) text = text.Remove(at, suffix.Length);
            }
            return text;
        }

        /// <summary>
        /// Reads one case image as a data URL. Re-validates the stored path against the current workspace (the file
        /// may have changed since the proposal was created), refuses every invalid state by throwing, and never holds
        /// the filesystem lease across unrelated work: validation and read are one bounded call.
        /// </summary>
        internal string ReadCaseImage(string proposalId, string sessionId, string caseId, string workspaceRoot)
        {
            var image = FindImagePath(proposalId, sessionId, caseId);
            try { ValidateCaseImage(image, workspaceRoot); }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick("사례 이미지가 올바르지 않아요: " + UserMessage(ex), "Invalid case image: " + UserMessage(ex)), ex);
            }
            string full;
            try { full = Path.GetFullPath(Path.Combine(workspaceRoot, image)); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick("사례 이미지 경로가 올바르지 않아요: " + UserMessage(ex), "Invalid case image path: " + UserMessage(ex)), ex);
            }
            var prefix = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException(PompLanguage.Pick("사례 이미지가 작업 영역 밖에 있어요.", "The case image is outside the workspace."));
            byte[] bytes;
            try
            {
                // CheckDirectPath inspects the entry itself (never follows a dangling link): reparse points anywhere on the path refuse first.
                CheckpointStore.CheckDirectPath(full);
                var info = new FileInfo(full);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The path contains a symbolic link or junction.");
                if (!info.Exists)
                    throw new FileNotFoundException(image);
                if (info.Length > MaxImageBytes)
                    throw new IOException(PompLanguage.Pick("사례 이미지가 너무 커요.", "The case image is too large."));
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaxImageBytes)
                        throw new IOException(PompLanguage.Pick("사례 이미지가 너무 커요.", "The case image is too large."));
                    bytes = new byte[stream.Length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) throw new IOException(PompLanguage.Pick("사례 이미지를 읽지 못했어요.", "Could not read the case image."));
                        offset += read;
                    }
                }
                CheckpointStore.CheckDirectPath(full);
            }
            catch (InvalidOperationException) { throw; }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick("사례 이미지가 올바르지 않아요: " + UserMessage(ex), "Invalid case image: " + UserMessage(ex)), ex);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new InvalidOperationException(PompLanguage.Pick("사례 이미지를 읽을 수 없어요: " + UserMessage(ex), "Case image is unreadable: " + UserMessage(ex)), ex);
            }
            if (!ImageMimeTypes.TryGetValue(Path.GetExtension(image) ?? "", out var mime))
                throw new InvalidOperationException(PompLanguage.Pick("사례 이미지 형식이 올바르지 않아요.", "The case image format is invalid."));
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }

        private static void ValidateSubmissionToken(string submissionToken)
        {
            if (string.IsNullOrWhiteSpace(submissionToken) || !GuidNRegex.IsMatch(submissionToken))
                throw new ArgumentException(PompLanguage.Pick("제출 토큰이 올바르지 않아요.", "Invalid submission token."), nameof(submissionToken));
        }

        /// <summary>The stored image path of one case (lease-free read; throws when the row is unavailable or has no image).</summary>
        internal string FindImagePath(string proposalId, string sessionId, string caseId)
        {
            lock (Gate)
            {
                var entry = Find(ReadLocked(), proposalId);
                if (!string.Equals((string?)entry["sessionId"], sessionId, StringComparison.Ordinal))
                    throw new InvalidOperationException(PompLanguage.Pick("다른 대화의 피드백이에요.", "Feedback belongs to another conversation."));
                var row = ((entry["cases"] as JArray) ?? new JArray()).OfType<JObject>()
                    .FirstOrDefault(c => string.Equals((string?)c["caseId"], caseId, StringComparison.Ordinal));
                if (row == null)
                    throw new InvalidOperationException(PompLanguage.Pick("사례를 찾을 수 없어요.", "Case was not found."));
                var image = (string?)row["image"];
                if (string.IsNullOrEmpty(image))
                    throw new InvalidOperationException(PompLanguage.Pick("이 사례에는 이미지가 없어요.", "This case has no image."));
                return image!;
            }
        }

        private static JObject Find(JArray state, string proposalId)
        {
            if (string.IsNullOrWhiteSpace(proposalId) || !GuidNRegex.IsMatch(proposalId))
                throw new ArgumentException(PompLanguage.Pick("제안 ID가 올바르지 않아요.", "Invalid proposal id."), nameof(proposalId));
            var entry = state.OfType<JObject>().FirstOrDefault(e => (string?)e["proposalId"] == proposalId);
            if (entry == null)
                throw new InvalidOperationException(PompLanguage.Pick("제안을 찾을 수 없어요.", "Proposal was not found."));
            return entry;
        }

        private static void CheckSessionRevision(JObject entry, string sessionId, int revision)
        {
            ValidateSession(sessionId);
            if (revision < 1 || revision > int.MaxValue / 2)
                throw new ArgumentException(PompLanguage.Pick("리비전이 올바르지 않아요.", "Invalid revision."), nameof(revision));
            if (!string.Equals((string?)entry["sessionId"], sessionId, StringComparison.Ordinal))
                throw new InvalidOperationException(PompLanguage.Pick("다른 대화의 제안이에요.", "Proposal belongs to another conversation."));
            if (revision != (int?)entry["revision"])
                throw new InvalidOperationException(PompLanguage.Pick("제안이 바뀌었어요.", "Proposal changed."));
        }

        /// <summary>Newline, tab and CR are allowed in free text; every other control character is rejected.</summary>
        private static bool HasForbiddenControl(string value) =>
            value.Any(c => char.IsControl(c) && c != '\n' && c != '\t' && c != '\r');

        private static void ValidateSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > MaxSessionChars || sessionId.Any(char.IsControl))
                throw new ArgumentException(PompLanguage.Pick("대화 ID가 올바르지 않아요.", "Invalid session id."), nameof(sessionId));
        }

        private static void ValidateTarget(AgentRunTargetPayload target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target.Kind != "agent" && target.Kind != "team" && target.Kind != "dm" && target.Kind != "main")
                throw new ArgumentException(PompLanguage.Pick("대상 종류가 올바르지 않아요.", "Invalid target kind."), nameof(target));
            // No arbitrary ids or paths: agent/team names are slugs; dm ids are engine job ids; main takes no id.
            bool idOk = target.Kind == "main"
                ? string.IsNullOrEmpty(target.Id)
                : target.Kind == "dm"
                    ? InstanceIdRegex.IsMatch(target.Id ?? "")
                    : TargetNameRegex.IsMatch(target.Id ?? "");
            if (!idOk)
                throw new ArgumentException(PompLanguage.Pick("대상 ID가 올바르지 않아요.", "Invalid target id."), nameof(target));
            if (target.Title == null || target.Title.Length > MaxTargetTitleChars || target.Title.Any(char.IsControl))
                throw new ArgumentException(PompLanguage.Pick("대상 이름이 올바르지 않아요.", "The target title is invalid."), nameof(target));
        }

        private static List<CaseInput> ValidateCases(IList<CaseInput> cases, string? workspaceRoot)
        {
            if (cases == null || cases.Count == 0 || cases.Count > MaxCases)
                throw new ArgumentException(PompLanguage.Pick("사례가 올바르지 않아요.", "The cases are invalid."), nameof(cases));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<CaseInput>(cases.Count);
            var imageCount = 0;
            foreach (var c in cases)
            {
                if (c == null || !CaseIdRegex.IsMatch(c.CaseId ?? ""))
                    throw new ArgumentException(PompLanguage.Pick("사례 ID가 올바르지 않아요.", "The case id is invalid."), nameof(cases));
                if (!seen.Add(c.CaseId!))
                    throw new ArgumentException(PompLanguage.Pick($"중복된 사례 ID가 있어요: {c.CaseId}", $"Duplicate case id: {c.CaseId}"), nameof(cases));
                if (string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > MaxTitleChars || c.Title.Any(char.IsControl))
                    throw new ArgumentException(PompLanguage.Pick("사례 제목이 올바르지 않아요.", "The case title is invalid."), nameof(cases));
                if (string.IsNullOrWhiteSpace(c.BodyMarkdown) || c.BodyMarkdown.Length > MaxBodyChars)
                    throw new ArgumentException(PompLanguage.Pick("사례 본문이 올바르지 않아요.", "The case body is invalid."), nameof(cases));
                if (Encoding.UTF8.GetByteCount(c.BodyMarkdown) > MaxBodyChars * 4)
                    throw new ArgumentException(PompLanguage.Pick("사례 본문이 너무 커요.", "Case body is too large."), nameof(cases));
                var image = ValidateCaseImage(c.Image, workspaceRoot);
                if (image != null) imageCount++;
                result.Add(new CaseInput { CaseId = c.CaseId!, Title = c.Title, BodyMarkdown = c.BodyMarkdown, Image = image, Diagram = ValidateCaseDiagram(c.Diagram) });
            }
            if (imageCount > MaxImageCases)
                throw new ArgumentException(PompLanguage.Pick("이미지가 있는 사례가 너무 많아요.", "Too many cases carry an image."), nameof(cases));
            return result;
        }

        private static JObject EncodeCase(CaseInput c)
        {
            var row = new JObject { ["caseId"] = c.CaseId, ["title"] = c.Title, ["bodyMarkdown"] = c.BodyMarkdown };
            // Stored rows keep image/diagram only when present, so older rows stay valid.
            if (c.Image != null) row["image"] = c.Image;
            if (c.Diagram != null) row["diagram"] = c.Diagram;
            return row;
        }

        private static ProposalCase ReadCase(JObject c) => new ProposalCase
        {
            CaseId = (string?)c["caseId"] ?? "",
            Title = (string?)c["title"] ?? "",
            BodyMarkdown = (string?)c["bodyMarkdown"] ?? "",
            Image = (string?)c["image"],
            Diagram = (string?)c["diagram"],
        };

        /// <summary>
        /// A case image path, or null when the case has no image. Paths are workspace-relative, resolved under
        /// the workspace root. When <paramref name="workspaceRoot"/> is null the existence and size checks run
        /// at image-read time instead; the shape rules still apply now. Returns the path verbatim.
        /// </summary>
        internal static string? ValidateCaseImage(string? image, string? workspaceRoot)
        {
            if (image == null) return null;
            if (image.Length == 0 || image.Length > MaxImagePathChars || image.Any(char.IsControl))
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 경로가 올바르지 않아요.", "The case image path is invalid."), "cases");
            // No absolute or rooted paths, no drive letters or stream/ADS syntax, no parent escapes.
            if (Path.IsPathRooted(image) || image.Contains(':') || image.Replace('\\', '/').Split('/').Contains(".."))
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 경로가 올바르지 않아요.", "The case image path is invalid."), "cases");
            var extension = Path.GetExtension(image);
            if (!ImageExtensions.Contains(extension ?? "", StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 형식이 올바르지 않아요.", "The case image format is invalid."), "cases");
            if (workspaceRoot == null) return image;
            // The file must already exist inside the root: resolve it, then check containment and the entry itself.
            string full;
            try { full = Path.GetFullPath(Path.Combine(workspaceRoot, image)); }
            catch (Exception ex)
            {
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 경로가 올바르지 않아요: " + UserMessage(ex), "Invalid case image path: " + UserMessage(ex)), "cases");
            }
            var prefix = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException(PompLanguage.Pick("사례 이미지가 작업 영역 밖에 있어요.", "The case image is outside the workspace."), "cases");
            // A missing file is reported as missing (the link walk below would otherwise misname it); a dangling link is missing too.
            if (!File.Exists(full))
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 파일을 찾을 수 없어요: " + image, "Case image file was not found: " + image), "cases");
            // Refuse any reparse point (symlink/junction) in the path from the root down, including the file itself.
            try { CheckpointStore.CheckDirectPath(full); }
            catch (IOException)
            {
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 경로에 심볼릭 링크나 정션이 있어요.", "The case image path contains a symbolic link or junction."), "cases");
            }
            FileInfo info;
            try { info = new FileInfo(full); }
            catch (Exception ex)
            {
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 파일을 확인할 수 없어요: " + UserMessage(ex), "Could not inspect the case image file: " + UserMessage(ex)), "cases");
            }
            if (!info.Exists)
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 파일을 찾을 수 없어요: " + image, "Case image file was not found: " + image), "cases");
            // A missing path reports Attributes as -1 (all bits set): only a file that exists can be a link.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException(PompLanguage.Pick("사례 이미지 경로에 심볼릭 링크나 정션이 있어요.", "The case image path contains a symbolic link or junction."), "cases");
            if (info.Length > MaxImageBytes)
                throw new ArgumentException(PompLanguage.Pick("사례 이미지가 너무 커요.", "The case image is too large."), "cases");
            return image;
        }

        /// <summary>Mermaid source for a case diagram, or null when the case has none. Trimmed text is stored.</summary>
        internal static string? ValidateCaseDiagram(string? diagram)
        {
            if (diagram == null) return null;
            var text = diagram.Trim();
            if (text.Length == 0 || text.Length > MaxDiagramChars || text.Contains('\0'))
                throw new ArgumentException(PompLanguage.Pick("사례 다이어그램이 올바르지 않아요.", "The case diagram is invalid."), "cases");
            return text;
        }

        private static List<DraftSelection> ValidateSelections(JObject entry, IList<DraftSelection> selections)
        {
            if (selections == null || selections.Count > MaxCases)
                throw new ArgumentException(PompLanguage.Pick("선택 항목이 올바르지 않아요.", "Invalid selections."), nameof(selections));
            var known = new HashSet<string>(((entry["cases"] as JArray) ?? new JArray()).OfType<JObject>().Select(c => (string?)c["caseId"] ?? ""), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<DraftSelection>(selections.Count);
            foreach (var s in selections)
            {
                if (s == null || !known.Contains(s.CaseId))
                    throw new ArgumentException(PompLanguage.Pick("제안에 없는 사례가 포함되어 있어요.", "Selections contain an unknown case."), nameof(selections));
                if (!seen.Add(s.CaseId))
                    throw new ArgumentException(PompLanguage.Pick($"중복된 사례 선택이 있어요: {s.CaseId}", $"Duplicate selection for case: {s.CaseId}"), nameof(selections));
                var comment = s.Comment ?? "";
                var extra = s.Extra ?? "";
                if (comment.Length > MaxCommentChars || HasForbiddenControl(comment))
                    throw new ArgumentException(PompLanguage.Pick("의견이 올바르지 않아요.", "The comment is invalid."), nameof(selections));
                if (extra.Length > MaxExtraChars || HasForbiddenControl(extra))
                    throw new ArgumentException(PompLanguage.Pick("추가 제안이 올바르지 않아요.", "The additional proposal is invalid."), nameof(selections));
                if (Encoding.UTF8.GetByteCount(comment) + Encoding.UTF8.GetByteCount(extra) > MaxSubmitBytes)
                    throw new ArgumentException(PompLanguage.Pick("선택 항목이 너무 커요.", "Selection is too large."), nameof(selections));
                result.Add(new DraftSelection { CaseId = s.CaseId, Selected = s.Selected, Comment = comment, Extra = extra });
            }
            return result;
        }

        private static JArray EncodeSelections(List<DraftSelection> selections) =>
            new JArray(selections.Select(s => new JObject { ["caseId"] = s.CaseId, ["selected"] = s.Selected, ["comment"] = s.Comment ?? "", ["extra"] = s.Extra ?? "" }));

        private static AgentRunTargetPayload ReadTarget(JObject? raw) => new AgentRunTargetPayload
        {
            Kind = (string?)raw?["kind"] ?? "",
            Id = (string?)raw?["id"] ?? "",
            Title = (string?)raw?["title"] ?? "",
        };

        private static ProposalView ViewOf(JObject entry)
        {
            var draft = entry["draft"] as JObject;
            var submitted = entry["submitted"] as JObject;
            return new ProposalView
            {
                ProposalId = (string?)entry["proposalId"] ?? "",
                SessionId = (string?)entry["sessionId"] ?? "",
                Target = ReadTarget(entry["target"] as JObject),
                Revision = (int?)entry["revision"] ?? 0,
                Status = (string?)entry["status"] ?? "",
                Cases = ((entry["cases"] as JArray) ?? new JArray()).OfType<JObject>().Select(ReadCase).ToList(),
                Draft = ((draft?["selections"] as JArray) ?? new JArray()).OfType<JObject>().Select(s => new DraftSelection
                {
                    CaseId = (string?)s["caseId"] ?? "",
                    Selected = (bool?)s["selected"] == true,
                    Comment = (string?)s["comment"],
                    Extra = (string?)s["extra"],
                }).ToList(),
                DraftRevision = (int?)draft?["revision"] ?? 0,
                Submitted = submitted == null ? null : new SubmittedFeedback
                {
                    ProposalId = (string?)submitted["proposalId"] ?? "",
                    SessionId = (string?)submitted["sessionId"] ?? "",
                    Revision = (int?)submitted["revision"] ?? 0,
                    SubmissionToken = (string?)submitted["submissionToken"] ?? "",
                    Selections = ((submitted["selections"] as JArray) ?? new JArray()).OfType<JObject>().Select(s => new DraftSelection
                    {
                        CaseId = (string?)s["caseId"] ?? "",
                        Selected = (bool?)s["selected"] == true,
                        Comment = (string?)s["comment"],
                        Extra = (string?)s["extra"],
                    }).ToList(),
                    SubmittedAt = (string?)submitted["submittedAt"] ?? "",
                },
                CreatedAt = (string?)entry["createdAt"] ?? "",
            };
        }

        private void Save(JArray state)
        {
            var encoded = new JObject { ["version"] = 1, ["proposals"] = state }.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(encoded) > MaxBytes)
                throw new InvalidOperationException(PompLanguage.Pick("제안 보관함이 가득 찼어요.", "The proposal store is full."));
            CheckpointStore.CheckDirectPath(file);
            // Called only with the filesystem lease held (see AcquireLease): the read+validate+save sequence is
            // serialized across processes, so no compare-then-write race remains.
            AtomicFile.WriteAllText(file, encoded);
        }

        /// <summary>
        /// Bounded read used by mutations (lease held) and lease-free reads. Throws (fail closed) when the file is
        /// malformed or oversized, never resets it.
        /// </summary>
        private JArray ReadLocked()
        {
            CheckpointStore.CheckDirectPath(file);
            if (!File.Exists(file))
            {
                return new JArray();
            }
            // Byte budget on the decoded text, not just the raw length: overlong UTF-8 sequences or a hostile
            // encoding could otherwise inflate memory past the budget while stream.Length looks acceptable.
            string text;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > MaxBytes)
                        throw new InvalidOperationException(PompLanguage.Pick("제안 보관함 파일이 너무 커요.", "Case feedback file is too large."));
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        var sb = new StringBuilder();
                        var buffer = new char[16 * 1024];
                        int read;
                        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sb.Append(buffer, 0, read);
                            if (sb.Length > MaxBytes)
                                throw new InvalidOperationException(PompLanguage.Pick("제안 보관함 파일이 너무 커요.", "Case feedback file is too large."));
                        }
                        text = sb.ToString();
                    }
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick("제안 보관함을 읽을 수 없어요: " + UserMessage(ex), "Case feedback store is unreadable: " + UserMessage(ex)), ex);
            }
            JObject raw;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 12, DateParseHandling = DateParseHandling.None })
                {
                    raw = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("Trailing content.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick("제안 보관함 JSON 형식이 올바르지 않아요.", "Case feedback JSON is invalid."), ex);
            }
            RejectUnknownFields(raw, new HashSet<string>(StringComparer.Ordinal) { "version", "proposals" }, "store");
            if (raw["version"]?.Type != JTokenType.Integer || (int?)raw["version"] != 1 || raw["proposals"] is not JArray rows || rows.Count > MaxProposals)
                throw new InvalidOperationException(PompLanguage.Pick("제안 보관함 형식이 올바르지 않아요.", "Case feedback store has an invalid format."));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var row in rows.OfType<JObject>()) ValidateRow(row, seen);
                if (rows.Any(r => r is not JObject)) throw new InvalidDataException("Invalid proposal entry.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick("제안 보관함에 잘못된 항목이 있어요: " + UserMessage(ex), "Case feedback store has an invalid entry: " + UserMessage(ex)), ex);
            }
            return rows;
        }

        private StoreUnavailable? Inspect()
        {
            try
            {
                ReadLocked();
                return null;
            }
            catch (Exception ex)
            {
                return new StoreUnavailable { Reason = ex.Message };
            }
        }

        private static void RequireStrings(JObject row, params string[] fields)
        {
            if (fields.Any(field => row[field]?.Type != JTokenType.String))
                throw new InvalidDataException("Invalid stored text field type.");
        }

        private static void ValidateRow(JObject row, HashSet<string> seen)
        {
            RejectUnknownFields(row, ProposalFields, "proposal");
            RequireStrings(row, "proposalId", "sessionId", "status", "createdAt");
            if (row["revision"]?.Type != JTokenType.Integer) throw new InvalidDataException("Invalid revision type.");
            var id = (string?)row["proposalId"];
            if (string.IsNullOrEmpty(id) || !GuidNRegex.IsMatch(id) || !seen.Add(id!)) throw new InvalidDataException("Invalid proposal id.");
            var session = (string?)row["sessionId"];
            if (string.IsNullOrWhiteSpace(session) || session!.Length > MaxSessionChars || session.Any(char.IsControl)) throw new InvalidDataException("Invalid session id.");
            var target = row["target"] as JObject;
            if (target == null) throw new InvalidDataException("Missing target.");
            RejectUnknownFields(target, TargetFields, "target");
            RequireStrings(target, "kind", "id", "title");
            var kind = (string?)target["kind"];
            if (kind != "agent" && kind != "team" && kind != "dm" && kind != "main") throw new InvalidDataException("Invalid target kind.");
            var targetId = (string?)target["id"] ?? "";
            bool targetIdOk = kind == "main" ? targetId.Length == 0 : kind == "dm" ? InstanceIdRegex.IsMatch(targetId) : TargetNameRegex.IsMatch(targetId);
            if (!targetIdOk) throw new InvalidDataException("Invalid target id.");
            if (((string?)target["title"] ?? "").Length > MaxTargetTitleChars) throw new InvalidDataException("Invalid target title.");
            if ((int?)row["revision"] != 1) throw new InvalidDataException("Invalid revision.");
            var status = (string?)row["status"];
            if (status != "draft" && status != "submitted" && status != "dispatching" && status != "accepted") throw new InvalidDataException("Invalid status.");
            var cases = row["cases"] as JArray;
            if (cases == null || cases.Count == 0 || cases.Count > MaxCases) throw new InvalidDataException("Invalid cases.");
            var caseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in cases.OfType<JObject>())
            {
                RejectUnknownFields(c, CaseFields, "case");
                RequireStrings(c, "caseId", "title", "bodyMarkdown");
                var caseId = (string?)c["caseId"] ?? "";
                if (!CaseIdRegex.IsMatch(caseId) || !caseIds.Add(caseId)) throw new InvalidDataException("Invalid case id.");
                var title = (string?)c["title"] ?? "";
                if (title.Length == 0 || title.Length > MaxTitleChars || title.Any(char.IsControl)) throw new InvalidDataException("Invalid case title.");
                var body = (string?)c["bodyMarkdown"] ?? "";
                if (body.Length == 0 || body.Length > MaxBodyChars) throw new InvalidDataException("Invalid case body.");
                // Rows written before image/diagram existed carry neither field; when present both must re-validate.
                var image = (string?)c["image"];
                if (c["image"] != null)
                {
                    if (c["image"]!.Type != JTokenType.String || string.IsNullOrEmpty(image) || image!.Length > MaxImagePathChars || image.Any(char.IsControl))
                        throw new InvalidDataException("Invalid case image.");
                }
                var diagram = (string?)c["diagram"];
                if (c["diagram"] != null)
                {
                    if (c["diagram"]!.Type != JTokenType.String || diagram!.Length == 0 || diagram.Length > MaxDiagramChars || diagram.Contains('\0'))
                        throw new InvalidDataException("Invalid case diagram.");
                }
            }
            if (cases.Any(c => c is not JObject)) throw new InvalidDataException("Invalid case entry.");
            var draft = row["draft"] as JObject;
            if (draft == null) throw new InvalidDataException("Missing draft.");
            RejectUnknownFields(draft, DraftFields, "draft");
            ValidateSelectionArray(draft["selections"] as JArray, caseIds);
            if (draft["revision"]?.Type != JTokenType.Integer || (int?)draft["revision"] < 0) throw new InvalidDataException("Invalid draft revision.");
            if ((string?)row["createdAt"] == null || ((string?)row["createdAt"] ?? "").Length > 64) throw new InvalidDataException("Invalid createdAt.");
            if (status == "draft" && row["submitted"] != null) throw new InvalidDataException("Draft proposal carries submitted feedback.");
            var submitted = row["submitted"] as JObject;
            if (status == "submitted" || status == "dispatching" || status == "accepted")
            {
                if (submitted == null) throw new InvalidDataException("Missing submitted feedback.");
                RejectUnknownFields(submitted, SubmittedFields, "submitted");
                RequireStrings(submitted, "proposalId", "sessionId", "submissionToken", "submittedAt");
                if (submitted["revision"]?.Type != JTokenType.Integer) throw new InvalidDataException("Invalid submitted revision type.");
                if ((string?)submitted["proposalId"] != id) throw new InvalidDataException("Submitted proposal mismatch.");
                if (!string.Equals((string?)submitted["sessionId"], session, StringComparison.Ordinal)) throw new InvalidDataException("Submitted session mismatch.");
                if ((int?)submitted["revision"] != 1) throw new InvalidDataException("Submitted revision mismatch.");
                if ((string?)submitted["submissionToken"] == null || !GuidNRegex.IsMatch((string?)submitted["submissionToken"] ?? "")) throw new InvalidDataException("Missing submission token.");
                if (((string?)submitted["submittedAt"] ?? "").Length == 0 || ((string?)submitted["submittedAt"] ?? "").Length > 64) throw new InvalidDataException("Invalid submittedAt.");
                ValidateSelectionArray(submitted["selections"] as JArray, caseIds);
            }
        }

        private static void ValidateSelectionArray(JArray? selections, HashSet<string> caseIds)
        {
            if (selections == null)
            {
                throw new InvalidDataException("Missing selections.");
            }
            if (selections.Count > MaxCases) throw new InvalidDataException("Too many selections.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in selections.OfType<JObject>())
            {
                RejectUnknownFields(s, SelectionFields, "selection");
                if (s["caseId"]?.Type != JTokenType.String || s["selected"]?.Type != JTokenType.Boolean
                    || s["comment"]?.Type != JTokenType.String || s["extra"]?.Type != JTokenType.String)
                    throw new InvalidDataException("Invalid selection field types.");
                var caseId = (string?)s["caseId"] ?? "";
                if (!caseIds.Contains(caseId) || !seen.Add(caseId)) throw new InvalidDataException("Unknown or duplicate selection.");
                var comment = (string?)s["comment"] ?? "";
                var extra = (string?)s["extra"] ?? "";
                if (comment.Length > MaxCommentChars || HasForbiddenControl(comment)) throw new InvalidDataException("Comment too large or invalid.");
                if (extra.Length > MaxExtraChars || HasForbiddenControl(extra)) throw new InvalidDataException("Extra proposal too large or invalid.");
            }
            if (selections.Any(s => s is not JObject)) throw new InvalidDataException("Invalid selection entry.");
        }
    }
}
