using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// The knowledge a workspace can use, from two files listed as one: <c>project</c> knowledge in the workspace's
    /// <c>.pomp/knowledge.json</c> (committed, so the project's people share it) and <c>user</c> knowledge in the user's own
    /// <c>~/.pomp/knowledge.json</c> (this PC, every project). Each knowledge is a user tag (a name and a color) holding pieces of
    /// text, used as written (<c>original</c>) or as an editor-model summary (<c>optimized</c>). A knowledge reaches a model when the
    /// conversation selects it, when a role or team it is checked for works, or in every conversation when it is a default.
    /// A model can only add <c>candidate</c> pieces; the user accepts, edits or deletes them. Duplicates (same normalized
    /// text and anchor path as another saved piece) are kept only once. Nothing is evicted silently, and a malformed file
    /// fails closed (mutations throw and never overwrite it). Pieces stay in the order they were added; knowledge newest first.
    /// </summary>
    internal sealed class KnowledgeStore
    {
        /// <summary>Each file's size: knowledge, pieces and candidates have no count limit of their own.</summary>
        internal const int MaxBytes = 4 * 1024 * 1024;
        internal const int MaxNameChars = 40;
        internal const int MaxPieceChars = 8000;
        internal const int MaxOptimizedChars = 16000;
        internal const string FileName = "knowledge.json";

        /// <summary>Distinct hues a new knowledge takes in turn (a color default, not a tag).</summary>
        internal static readonly string[] ColorRotation = new[] { "#E5484D", "#F76B15", "#FFB224", "#46A758", "#12A594", "#0090FF", "#8E4EC6", "#D6409F" };

        /// <summary>The first rotation color none of <paramref name="used"/> has (case-insensitive); the first one when all are taken.</summary>
        internal static string DefaultColor(IEnumerable<string> used)
        {
            var taken = new HashSet<string>(used.Select(color => (color ?? "").ToUpperInvariant()), StringComparer.Ordinal);
            return ColorRotation.FirstOrDefault(color => !taken.Contains(color)) ?? ColorRotation[0];
        }

        private static readonly Regex TeamNameRegex = new Regex(@"\A[a-z0-9-]{2,40}\z", RegexOptions.CultureInvariant);
        private static readonly Regex IdRegex = new Regex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
        private static readonly Regex ColorRegex = new Regex(@"\A#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant);
        private static readonly Regex HashRegex = new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> RowFields = new HashSet<string>(StringComparer.Ordinal) { "id", "name", "color", "scope", "use", "default", "pieces", "optimized", "roles", "teams", "createdAt", "updatedAt", "revision" };

        private static readonly WorkspaceJsonFile.Shape Shape = new WorkspaceJsonFile.Shape
        {
            Folder = "knowledge-v2", FileName = FileName, Rows = "knowledge", MaxBytes = MaxBytes, MaxRows = int.MaxValue, Shared = true,
            Fields = RowFields, CheckRow = CheckRow,
            Busy = ("다른 창에서 지식 저장소를 쓰고 있어요.", "The knowledge store is busy in another window."),
            Full = ("지식 저장소가 가득 찼어요.", "The knowledge store is full."),
            Unreadable = ("지식 저장소 파일을 읽지 못했어요: ", "The knowledge store could not be read: "),
        };

        private static readonly WorkspaceJsonFile.Shape UserShape = new WorkspaceJsonFile.Shape
        {
            Folder = "knowledge-user", Rows = "knowledge", MaxBytes = MaxBytes, MaxRows = int.MaxValue,
            Fields = RowFields, CheckRow = CheckRow,
            Busy = ("다른 창에서 PC 전체 지식을 쓰고 있어요.", "This PC's knowledge is busy in another window."),
            Full = ("PC 전체 지식 저장소가 가득 찼어요.", "This PC's knowledge store is full."),
            Unreadable = ("PC 전체 지식 파일(~/.pomp/knowledge.json)을 읽지 못했어요: ", "This PC's knowledge file (~/.pomp/knowledge.json) could not be read: "),
        };

        private readonly WorkspaceJsonFile store;
        private readonly WorkspaceJsonFile user;
        private readonly WorkspaceJsonFile v1;

        internal KnowledgeStore(string dataDirectory, string workspaceRoot, string? userHome)
        {
            store = new WorkspaceJsonFile(dataDirectory, workspaceRoot, Shape);
            user = WorkspaceJsonFile.At(UserFile(userHome), UserShape);
            v1 = WorkspaceJsonFile.AtOldLocation(dataDirectory, workspaceRoot, V1Shape);
        }

        /// <summary><c>~/.pomp/knowledge.json</c>: the knowledge of every project on this PC.</summary>
        internal static string UserFile(string? userHome) => Path.Combine(ToolResourceFiles.Home(userHome), ".pomp", FileName);

        /// <summary>Both files as one list, newest first. A row is in the file of its scope; an id seen twice (an interrupted move) counts once, the project's.</summary>
        private JArray ReadAll() => Merge(store.Read(), user.Read());

        private static JArray Merge(JArray project, JArray mine)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<JObject>();
            foreach (var (rowsOf, scope) in new[] { (project, "project"), (mine, "user") })
                foreach (var row in rowsOf.OfType<JObject>())
                {
                    if (!seen.Add((string)row["id"]!)) continue;
                    var copy = (JObject)row.DeepClone();
                    // The file decides the scope ("personal", an earlier scope, was project knowledge too).
                    copy["scope"] = scope;
                    rows.Add(copy);
                }
            return new JArray(rows.Select((row, index) => (row, index)).OrderByDescending(x => (string)x.row["createdAt"]!, StringComparer.Ordinal).ThenBy(x => x.index).Select(x => x.row));
        }

        /// <summary>Applies <paramref name="change"/> to the merged list and writes each knowledge back to the file of its scope.</summary>
        private T MutateAll<T>(Func<JArray, T> change) => store.MutateWith(user, (project, mine) =>
        {
            var before = new HashSet<string>(mine.OfType<JObject>().Select(r => (string)r["id"]!), StringComparer.Ordinal);
            var merged = Merge(project, mine);
            var result = change(merged);
            project.Clear(); mine.Clear();
            foreach (var row in merged.OfType<JObject>()) ((string?)row["scope"] == "user" ? mine : project).Add(row);
            // The file a knowledge moved into is written first: an interruption leaves it in both files, never in none.
            return (result, mine.OfType<JObject>().Any(r => !before.Contains((string)r["id"]!)));
        });

        internal sealed class Knowledge
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Color { get; set; } = "";
            public string Scope { get; set; } = "project";
            public string Use { get; set; } = "original";
            public List<Piece> Pieces { get; set; } = new List<Piece>();
            public Optimized? Optimized { get; set; }
            public List<string> Roles { get; set; } = new List<string>();
            public List<string> Teams { get; set; } = new List<string>();
            public string CreatedAt { get; set; } = "";
            public string UpdatedAt { get; set; } = "";
            public int Revision { get; set; } = 1;
            /// <summary>Used in every conversation, agent and team without being selected.</summary>
            public bool Default { get; set; }
        }

        internal sealed class Piece
        {
            public string Id { get; set; } = "";
            public string Text { get; set; } = "";
            public string Source { get; set; } = "user";
            public string Status { get; set; } = "saved";
            public Anchor? Anchor { get; set; }
            public string CreatedAt { get; set; } = "";
        }

        internal sealed class Anchor
        {
            public string Path { get; set; } = "";
            public int StartLine { get; set; }
            public int EndLine { get; set; }
            public string Hash { get; set; } = "";
        }

        internal sealed class Optimized
        {
            public string Text { get; set; } = "";
            public string SourceHash { get; set; } = "";
            public string CreatedAt { get; set; } = "";
            public string? Model { get; set; }
        }

        /// <summary>A piece on its way into the store (the anchor range, if any, is 1-based and inclusive).</summary>
        internal sealed class PieceInput
        {
            public string Text { get; set; } = "";
            public AnchorInput? Anchor { get; set; }
        }

        internal sealed class AnchorInput
        {
            public string Path { get; set; } = "";
            public int StartLine { get; set; }
            public int EndLine { get; set; }
        }

        /// <summary>What an agent proposal made of itself: a new candidate knowledge, a candidate piece, or nothing new.</summary>
        internal sealed class ProposeOutcome
        {
            public string KnowledgeId { get; set; } = "";
            public string? PieceId { get; set; }
            public bool NewKnowledge { get; set; }
            public bool AlreadySaved { get; set; }
        }

        /// <summary>Newest first. Throws when the file is unreadable (the caller reports it; nothing is reset).</summary>
        internal IReadOnlyList<Knowledge> List()
        {
            EnsureMigrated();
            return ReadAll().OfType<JObject>().Select(ToKnowledge).ToList();
        }

        internal Knowledge? FindByName(string name)
        {
            EnsureMigrated();
            return ReadAll().OfType<JObject>().Where(row => string.Equals((string?)row["name"], name, StringComparison.OrdinalIgnoreCase))
                .Select(ToKnowledge).FirstOrDefault();
        }

        /// <summary>The knowledge, or an error when the id is unknown or the revision is stale.</summary>
        internal Knowledge Get(string id, int revision)
        {
            EnsureMigrated();
            var row = ReadAll().OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id);
            if (row == null || (int)row["revision"]! != revision) throw new InvalidOperationException(PompLanguage.Pick("지식이 바뀌었거나 지워졌어요.", "The knowledge changed or was deleted."));
            return ToKnowledge(row);
        }

        /// <summary>Saved pieces as written, in order, joined by a blank line; an anchored piece names its file and lines.</summary>
        internal static string OriginalContent(Knowledge knowledge) =>
            string.Join("\n\n", knowledge.Pieces.Where(p => p.Status == "saved").Select(p => RenderPiece(p.Text, p.Anchor)));

        /// <summary>The hash the optimized text was made from (<see cref="OriginalContent"/>); fresh only when it still matches.</summary>
        internal static string OriginalHash(Knowledge knowledge) => KnowledgeText.Hash(OriginalContent(knowledge));

        /// <summary>What the knowledge contributes: the optimized text while it is fresh, else the original.</summary>
        internal static string UsedContent(Knowledge knowledge) =>
            knowledge.Use == "optimized" && knowledge.Optimized != null && knowledge.Optimized.SourceHash == OriginalHash(knowledge)
                ? knowledge.Optimized.Text : OriginalContent(knowledge);

        internal static string RenderPiece(string text, Anchor? anchor) =>
            anchor == null ? text : "`" + anchor.Path + "` lines " + anchor.StartLine + "–" + anchor.EndLine + ":\n```\n" + text + "\n```";

        /// <summary>The used content of every default knowledge not in <paramref name="excluded"/>, newest first, within the budgets.</summary>
        internal IReadOnlyList<(string Id, string Title, string Body)> DefaultItems(int maxItems, int maxChars, IEnumerable<string> excluded)
        {
            var result = new List<(string Id, string Title, string Body)>();
            var used = 0;
            var skip = new HashSet<string>(excluded, StringComparer.Ordinal);
            foreach (var knowledge in List().Where(k => k.Default && !skip.Contains(k.Id)))
            {
                var body = UsedContent(knowledge);
                if (body.Length == 0) continue;
                used += knowledge.Name.Length + body.Length;
                if (used > maxChars || result.Count >= maxItems) break;
                result.Add((knowledge.Id, knowledge.Name, body));
            }
            return result;
        }

        /// <summary>Makes the knowledge a default (ticked in every conversation's basket until unticked there) or an ordinary one.</summary>
        internal void SetDefault(string id, int revision, bool on)
        {
            EnsureMigrated();
            MutateAll(rows =>
            {
                var row = Find(rows, id, revision);
                if (((bool?)row["default"] == true) == on) return id;
                if (on) row["default"] = true; else row.Remove("default");
                Touch(row);
                return id;
            });
        }

        private static void CheckScope(string scope)
        {
            if (scope != "project" && scope != "user") throw new ArgumentException(PompLanguage.Pick("범위가 올바르지 않아요.", "The scope is invalid."));
        }

        /// <summary>Every role's knowledge, newest first, at most <paramref name="maxNotes"/> and <paramref name="maxChars"/> of title and body each.</summary>
        internal IReadOnlyDictionary<string, List<(string Title, string Body)>> RoleKnowledge(int maxNotes, int maxChars)
        {
            var result = new SortedDictionary<string, List<(string Title, string Body)>>(StringComparer.Ordinal);
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            var full = new HashSet<string>(StringComparer.Ordinal);
            foreach (var knowledge in List())
            {
                var body = UsedContent(knowledge);
                if (body.Length == 0) continue;
                foreach (var role in knowledge.Roles)
                {
                    if (full.Contains(role)) continue;
                    used.TryGetValue(role, out var chars);
                    chars += knowledge.Name.Length + body.Length;
                    if (chars > maxChars) { full.Add(role); continue; }
                    if (!result.TryGetValue(role, out var notes)) result[role] = notes = new List<(string Title, string Body)>();
                    notes.Add((knowledge.Name, body));
                    used[role] = chars;
                    if (notes.Count >= maxNotes) full.Add(role);
                }
            }

            return result;
        }

        /// <summary>Saved content for one team, newest first, within <paramref name="maxChars"/> of title and body.</summary>
        internal IReadOnlyList<(string Title, string Body)> TeamKnowledgeItems(string team, int maxNotes, int maxChars)
        {
            var result = new List<(string Title, string Body)>();
            var used = 0;
            foreach (var knowledge in List().Where(k => k.Teams.Contains(team)))
            {
                var body = UsedContent(knowledge);
                if (body.Length == 0) continue;
                used += knowledge.Name.Length + body.Length;
                if (used > maxChars || result.Count >= maxNotes) break;
                result.Add((knowledge.Name, body));
            }

            return result;
        }

        /// <summary>The used content of the selected knowledge, in selection order, within the budgets (missing ids skipped).</summary>
        internal IReadOnlyList<(string Title, string Body)> SelectedItems(IEnumerable<string> ids, int maxItems, int maxChars)
        {
            var byId = List().ToDictionary(k => k.Id, StringComparer.Ordinal);
            var result = new List<(string Title, string Body)>();
            var used = 0;
            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id, out var knowledge)) continue;
                var body = UsedContent(knowledge);
                if (body.Length == 0) continue;
                used += knowledge.Name.Length + body.Length;
                if (used > maxChars || result.Count >= maxItems) break;
                result.Add((knowledge.Name, body));
            }

            return result;
        }

        /// <summary>Creates the knowledge (a null color takes the next rotation color); a duplicate name is an error.</summary>
        internal string Create(string name, string? color, string scope, PieceInput? piece)
        {
            name = CheckName(name);
            color = color == null ? null : CheckColor(color);
            CheckScope(scope);
            var text = piece == null ? null : CheckText(piece.Text);
            var anchor = piece?.Anchor == null ? null : CheckAnchor(piece!.Anchor!);
            EnsureMigrated();
            return MutateAll(rows =>
            {
                if (rows.OfType<JObject>().Any(r => string.Equals((string?)r["name"], name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(PompLanguage.Pick("같은 이름의 지식이 있어요.", "A knowledge with this name already exists."));
                var now = DateTimeOffset.UtcNow.ToString("O");
                var id = Guid.NewGuid().ToString("N");
                var row = new JObject
                {
                    ["id"] = id, ["name"] = name, ["color"] = color ?? DefaultColor(rows.OfType<JObject>().Select(r => (string?)r["color"] ?? "")),
                    ["scope"] = scope, ["use"] = "original", ["pieces"] = new JArray(), ["roles"] = new JArray(), ["teams"] = new JArray(),
                    ["createdAt"] = now, ["updatedAt"] = now, ["revision"] = 1,
                };
                if (text != null) ((JArray)row["pieces"]!).Add(PieceRow(Guid.NewGuid().ToString("N"), text, "user", "saved", anchor, now));
                rows.Insert(0, row);
                return id;
            });
        }

        internal void Update(string id, int revision, string name, string color, string scope, string use)
        {
            name = CheckName(name);
            color = CheckColor(color);
            CheckScope(scope);
            if (use != "original" && use != "optimized") throw new ArgumentException(PompLanguage.Pick("사용 방식이 올바르지 않아요.", "The knowledge use is invalid."));
            EnsureMigrated();
            MutateAll(rows =>
            {
                var row = Find(rows, id, revision);
                if (rows.OfType<JObject>().Any(r => r != row && string.Equals((string?)r["name"], name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(PompLanguage.Pick("같은 이름의 지식이 있어요.", "A knowledge with this name already exists."));
                row["name"] = name;
                row["color"] = color;
                row["scope"] = scope;
                row["use"] = use;
                Touch(row);
                return id;
            });
        }

        internal void Delete(string id, int revision)
        {
            EnsureMigrated();
            MutateAll(rows => { rows.Remove(Find(rows, id, revision)); return id; });
        }

        /// <summary>
        /// Adds the piece; a duplicate of another saved piece adds nothing and answers that piece's id (no revision bump).
        /// </summary>
        internal string AddPiece(string id, int revision, PieceInput piece, string source, string status)
        {
            var text = CheckText(piece.Text);
            var anchor = piece.Anchor == null ? null : CheckAnchor(piece.Anchor);
            if (source != "user" && source != "agent" && source != "editor") throw new ArgumentException(PompLanguage.Pick("출처가 올바르지 않아요.", "The piece source is invalid."));
            if (status != "saved" && status != "candidate") throw new ArgumentException(PompLanguage.Pick("상태가 올바르지 않아요.", "The piece status is invalid."));
            EnsureMigrated();
            return MutateAll(rows =>
            {
                var row = Find(rows, id, revision);
                var pieces = (JArray)row["pieces"]!;
                var dupe = pieces.OfType<JObject>().FirstOrDefault(p => (string?)p["status"] == "saved" && SameText((string?)p["text"], text) && SamePath(p["anchor"] as JObject, anchor));
                if (dupe != null) return (string)dupe["id"]!;
                var pieceId = Guid.NewGuid().ToString("N");
                pieces.Add(PieceRow(pieceId, text, source, status, anchor, DateTimeOffset.UtcNow.ToString("O")));
                Touch(row);
                return pieceId;
            });
        }

        /// <summary>An anchored piece keeps its path and range; its hash follows the text. Editing into a duplicate of another saved piece removes the edited piece instead.</summary>
        internal void UpdatePiece(string id, int revision, string pieceId, string text)
        {
            text = CheckText(text);
            EnsureMigrated();
            MutateAll(rows =>
            {
                var row = Find(rows, id, revision);
                var pieces = (JArray)row["pieces"]!;
                var piece = pieces.OfType<JObject>().FirstOrDefault(p => (string?)p["id"] == pieceId)
                    ?? throw new InvalidOperationException(PompLanguage.Pick("조각이 바뀌었거나 지워졌어요.", "The piece changed or was deleted."));
                var anchor = piece["anchor"] as JObject;
                if (pieces.OfType<JObject>().Any(p => p != piece && (string?)p["status"] == "saved" && SameText((string?)p["text"], text) && SamePath(p["anchor"] as JObject, anchor)))
                    pieces.Remove(piece);
                else
                {
                    piece["text"] = text;
                    if (anchor != null) anchor["hash"] = KnowledgeText.Hash(text);
                }

                Touch(row);
                return id;
            });
        }

        /// <summary>Deleting the last piece of a candidate knowledge deletes the knowledge itself.</summary>
        internal void DeletePiece(string id, int revision, string pieceId)
        {
            EnsureMigrated();
            MutateAll(rows =>
            {
                var row = Find(rows, id, revision);
                var pieces = (JArray)row["pieces"]!;
                var piece = pieces.OfType<JObject>().FirstOrDefault(p => (string?)p["id"] == pieceId)
                    ?? throw new InvalidOperationException(PompLanguage.Pick("조각이 바뀌었거나 지워졌어요.", "The piece changed or was deleted."));
                var wasCandidate = (string?)piece["status"] == "candidate";
                pieces.Remove(piece);
                if (pieces.Count == 0 && wasCandidate) rows.Remove(row);
                else Touch(row);
                return id;
            });
        }

        /// <summary>A candidate becomes knowledge only through the user; accepting into a duplicate of another saved piece removes the accepted piece instead.</summary>
        internal void AcceptPiece(string id, int revision, string pieceId)
        {
            EnsureMigrated();
            MutateAll(rows =>
            {
                var row = Find(rows, id, revision);
                var pieces = (JArray)row["pieces"]!;
                var piece = pieces.OfType<JObject>().FirstOrDefault(p => (string?)p["id"] == pieceId)
                    ?? throw new InvalidOperationException(PompLanguage.Pick("조각이 바뀌었거나 지워졌어요.", "The piece changed or was deleted."));
                if ((string?)piece["status"] != "candidate") throw new InvalidOperationException(PompLanguage.Pick("이미 저장된 조각이에요.", "This piece is already saved."));
                if (pieces.OfType<JObject>().Any(p => p != piece && (string?)p["status"] == "saved" && SameText((string?)p["text"], (string?)piece["text"]) && SamePath(p["anchor"] as JObject, piece["anchor"] as JObject)))
                    pieces.Remove(piece);
                else piece["status"] = "saved";
                Touch(row);
                return id;
            });
        }

        /// <summary>
        /// Stores the editor-model result; false (nothing written) when the knowledge is gone or its pieces changed since
        /// <paramref name="sourceHash"/> was taken. Overlong text is cut to the limit (normalized again, so it stays readable).
        /// </summary>
        internal bool SetOptimized(string id, string text, string sourceHash, string? model, bool useOptimized = false)
        {
            text = KnowledgeText.Normalize(text);
            if (text.Length > MaxOptimizedChars) text = KnowledgeText.Normalize(text.Substring(0, MaxOptimizedChars));
            if (text.Length == 0) throw new ArgumentException(PompLanguage.Pick("변환본이 올바르지 않아요.", "The optimized text is invalid."));
            if (sourceHash == null || !HashRegex.IsMatch(sourceHash)) throw new ArgumentException(PompLanguage.Pick("변환본이 올바르지 않아요.", "The optimized text is invalid."));
            if (model != null && (model.Length == 0 || model.Length > 200 || model.Any(char.IsControl))) throw new ArgumentException(PompLanguage.Pick("변환본이 올바르지 않아요.", "The optimized text is invalid."));
            EnsureMigrated();
            return MutateAll(rows =>
            {
                var row = rows.OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id);
                if (row == null) return false;
                if (KnowledgeText.Hash(OriginalOfRow(row)) != sourceHash) return false;
                var optimized = new JObject { ["text"] = text, ["sourceHash"] = sourceHash, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O") };
                if (model != null) optimized["model"] = model;
                row["optimized"] = optimized;
                if (useOptimized) row["use"] = "optimized";
                Touch(row);
                return true;
            });
        }

        /// <summary>After it, exactly the knowledge in <paramref name="ids"/> has <paramref name="name"/> in its roles or teams.</summary>
        internal void Assign(string kind, string name, IEnumerable<string> ids)
        {
            if (kind != "role" && kind != "team") throw new ArgumentException(PompLanguage.Pick("대상이 올바르지 않아요.", "The assign target is invalid."));
            if (name == null || !TeamNameRegex.IsMatch(name))
                throw new ArgumentException(PompLanguage.Pick(kind == "role" ? "역할 이름이 올바르지 않아요." : "팀 ID가 올바르지 않아요.", kind == "role" ? "The role name is invalid." : "The team id is invalid."));
            var wanted = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (wanted.Any(i => i == null || !IdRegex.IsMatch(i))) throw new ArgumentException(PompLanguage.Pick("지식이 바뀌었거나 지워졌어요.", "The knowledge changed or was deleted."));
            EnsureMigrated();
            var known = new HashSet<string>(ReadAll().OfType<JObject>().Select(r => (string)r["id"]!), StringComparer.Ordinal);
            if (!wanted.All(known.Contains)) throw new InvalidOperationException(PompLanguage.Pick("지식이 바뀌었거나 지워졌어요.", "The knowledge changed or was deleted."));
            MutateAll(rows =>
            {
                foreach (var row in rows.OfType<JObject>())
                {
                    var list = (JArray)row[kind == "role" ? "roles" : "teams"]!;
                    var has = list.Any(t => (string?)t == name);
                    if (wanted.Contains((string)row["id"]!))
                    {
                        if (has) continue;
                        list.Add(name);
                        Touch(row);
                    }
                    else if (has)
                    {
                        list.Where(t => (string?)t == name).ToList().ForEach(t => list.Remove(t));
                        Touch(row);
                    }
                }

                return name;
            });
        }

        /// <summary>An agent proposal: a candidate piece in the named knowledge of either scope (a new candidate project knowledge when the name is new).</summary>
        internal ProposeOutcome Propose(string tag, string text)
        {
            var name = CheckName(tag);
            text = CheckText(text);
            EnsureMigrated();
            return MutateAll(rows =>
            {
                var row = rows.OfType<JObject>().FirstOrDefault(r => string.Equals((string?)r["name"], name, StringComparison.OrdinalIgnoreCase));
                if (row != null)
                {
                    var pieces = (JArray)row["pieces"]!;
                    var dupe = pieces.OfType<JObject>().FirstOrDefault(p => (string?)p["status"] == "saved" && SameText((string?)p["text"], text) && (p["anchor"] as JObject)?["path"] == null);
                    if (dupe != null) return new ProposeOutcome { KnowledgeId = (string)row["id"]!, AlreadySaved = true };
                    var pieceId = Guid.NewGuid().ToString("N");
                    pieces.Add(PieceRow(pieceId, text, "agent", "candidate", null, DateTimeOffset.UtcNow.ToString("O")));
                    Touch(row);
                    return new ProposeOutcome { KnowledgeId = (string)row["id"]!, PieceId = pieceId };
                }

                var now = DateTimeOffset.UtcNow.ToString("O");
                var id = Guid.NewGuid().ToString("N");
                var created = new JObject
                {
                    ["id"] = id, ["name"] = name, ["color"] = DefaultColor(rows.OfType<JObject>().Select(r => (string?)r["color"] ?? "")),
                    ["scope"] = "project", ["use"] = "original",
                    ["pieces"] = new JArray(PieceRow(Guid.NewGuid().ToString("N"), text, "agent", "candidate", null, now)),
                    ["roles"] = new JArray(), ["teams"] = new JArray(), ["createdAt"] = now, ["updatedAt"] = now, ["revision"] = 1,
                };
                rows.Insert(0, created);
                return new ProposeOutcome { KnowledgeId = id, PieceId = (string)((JArray)created["pieces"]!)[0]!["id"]!, NewKnowledge = true };
            });
        }

        /// <summary>Silent anchor moves found while building the view: new lines without a revision bump or timestamp change.</summary>
        internal void Reanchor(IEnumerable<(string KnowledgeId, string PieceId, int Start, int End)> moves)
        {
            var list = moves.ToList();
            if (list.Count == 0) return;
            EnsureMigrated();
            MutateAll(rows =>
            {
                foreach (var move in list)
                {
                    var piece = rows.OfType<JObject>().Where(r => (string?)r["id"] == move.KnowledgeId)
                        .SelectMany(r => ((JArray)r["pieces"]!).OfType<JObject>()).FirstOrDefault(p => (string?)p["id"] == move.PieceId);
                    var anchor = piece?["anchor"] as JObject;
                    if (anchor == null) continue;
                    anchor["startLine"] = move.Start;
                    anchor["endLine"] = move.End;
                }

                return list.Count;
            });
        }

        private static string OriginalOfRow(JObject row) => string.Join("\n\n", ((JArray)row["pieces"]!).OfType<JObject>()
            .Where(p => (string?)p["status"] == "saved").Select(p => RenderPiece((string)p["text"]!, ToAnchor(p["anchor"] as JObject))));

        private static bool SameText(string? a, string? b) => KnowledgeText.Normalize(a) == KnowledgeText.Normalize(b);

        private static bool SamePath(JObject? a, Anchor? b) => string.Equals((string?)a?["path"], b?.Path, StringComparison.OrdinalIgnoreCase);

        private static bool SamePath(JObject? a, JObject? b) => string.Equals((string?)a?["path"], (string?)b?["path"], StringComparison.OrdinalIgnoreCase);

        private static string CheckName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > MaxNameChars || name.Any(char.IsControl))
                throw new ArgumentException(PompLanguage.Pick("지식 이름이 올바르지 않아요.", "The knowledge name is invalid."));
            return name;
        }

        private static string CheckColor(string color)
        {
            if (color == null || !ColorRegex.IsMatch(color)) throw new ArgumentException(PompLanguage.Pick("지식 색이 올바르지 않아요.", "The knowledge color is invalid."));
            return color.ToUpperInvariant();
        }

        private static string CheckText(string text)
        {
            text = KnowledgeText.Normalize(text);
            if (text.Length == 0 || text.Length > MaxPieceChars) throw new ArgumentException(PompLanguage.Pick("내용이 올바르지 않아요.", "The text is invalid."));
            return text;
        }

        private static Anchor CheckAnchor(AnchorInput anchor)
        {
            string path;
            try { path = PortableRelativePath.Validate(anchor.Path); }
            catch (ArgumentException) { throw new ArgumentException(PompLanguage.Pick("줄 위치가 올바르지 않아요.", "The line anchor is invalid.")); }
            if (anchor.StartLine < 1 || anchor.EndLine < anchor.StartLine || anchor.EndLine > anchor.StartLine + 199)
                throw new ArgumentException(PompLanguage.Pick("줄 위치가 올바르지 않아요.", "The line anchor is invalid."));
            return new Anchor { Path = path, StartLine = anchor.StartLine, EndLine = anchor.EndLine };
        }

        private static void Touch(JObject row)
        {
            row["revision"] = checked((int)row["revision"]! + 1);
            row["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        }

        private static JObject Find(JArray rows, string id, int revision)
        {
            var row = rows.OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id);
            if (row == null || (int)row["revision"]! != revision) throw new InvalidOperationException(PompLanguage.Pick("지식이 바뀌었거나 지워졌어요.", "The knowledge changed or was deleted."));
            return row;
        }

        private static JObject PieceRow(string id, string text, string source, string status, Anchor? anchor, string createdAt)
        {
            var row = new JObject { ["id"] = id, ["text"] = text, ["source"] = source, ["status"] = status, ["createdAt"] = createdAt };
            if (anchor != null) row["anchor"] = new JObject { ["path"] = anchor.Path, ["startLine"] = anchor.StartLine, ["endLine"] = anchor.EndLine, ["hash"] = KnowledgeText.Hash(text) };
            return row;
        }

        private static Knowledge ToKnowledge(JObject row) => new Knowledge
        {
            Id = (string)row["id"]!, Name = (string)row["name"]!, Color = (string)row["color"]!, Scope = (string)row["scope"]!, Use = (string)row["use"]!,
            Pieces = ((JArray)row["pieces"]!).OfType<JObject>().Select(p => new Piece
            {
                Id = (string)p["id"]!, Text = (string)p["text"]!, Source = (string)p["source"]!, Status = (string)p["status"]!,
                Anchor = ToAnchor(p["anchor"] as JObject), CreatedAt = (string)p["createdAt"]!,
            }).ToList(),
            Optimized = row["optimized"] as JObject is JObject o ? new Optimized
            {
                Text = (string)o["text"]!, SourceHash = (string)o["sourceHash"]!, CreatedAt = (string)o["createdAt"]!, Model = (string?)o["model"],
            } : null,
            Roles = ((JArray)row["roles"]!).Select(t => (string)t!).ToList(),
            Teams = ((JArray)row["teams"]!).Select(t => (string)t!).ToList(),
            CreatedAt = (string)row["createdAt"]!, UpdatedAt = (string)row["updatedAt"]!, Revision = (int)row["revision"]!, Default = (bool?)row["default"] == true,
        };

        private static Anchor? ToAnchor(JObject? anchor) => anchor == null ? null : new Anchor
        {
            Path = (string)anchor["path"]!, StartLine = (int)anchor["startLine"]!, EndLine = (int)anchor["endLine"]!, Hash = (string)anchor["hash"]!,
        };

        /// <summary>Row types and meaning: wrong types, contents or anchor hashes make the whole store unreadable (it stays as it is).</summary>
        private static void CheckRow(JObject row)
        {
            if (new[] { "name", "color", "scope", "use", "createdAt", "updatedAt" }.Any(f => row[f]?.Type != JTokenType.String)
                || row["revision"]?.Type != JTokenType.Integer || (int)row["revision"]! < 1
                || row["pieces"] is not JArray pieces || row["roles"] is not JArray || row["teams"] is not JArray
                || (row["optimized"] != null && row["optimized"]!.Type != JTokenType.Object))
                throw new InvalidDataException("Invalid knowledge field type.");
            CheckName((string)row["name"]!);
            CheckColor((string)row["color"]!);
            // "personal" is an earlier scope: it reads as project knowledge (the file decides the scope).
            if ((string)row["scope"]! is not ("project" or "user" or "personal") || (string)row["use"]! is not ("original" or "optimized"))
                throw new InvalidDataException("Invalid knowledge scope or use.");
            if (row["default"] != null && row["default"]!.Type != JTokenType.Boolean) throw new InvalidDataException("Invalid knowledge default.");
            var pieceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in pieces)
            {
                if (token is not JObject piece || piece.Properties().Any(p => p.Name is not ("id" or "text" or "source" or "status" or "anchor" or "createdAt")))
                    throw new InvalidDataException("Invalid knowledge piece.");
                if (piece["id"]?.Type != JTokenType.String || !IdRegex.IsMatch((string)piece["id"]!) || !pieceIds.Add((string)piece["id"]!))
                    throw new InvalidDataException("Invalid piece id.");
                if (piece["text"]?.Type != JTokenType.String || piece["createdAt"]?.Type != JTokenType.String) throw new InvalidDataException("Invalid knowledge piece.");
                CheckText((string)piece["text"]!);
                if (KnowledgeText.Normalize((string)piece["text"]!) != (string)piece["text"]!) throw new InvalidDataException("The piece text is not normalized.");
                if ((string)piece["source"]! is not ("user" or "agent" or "editor") || (string)piece["status"]! is not ("saved" or "candidate"))
                    throw new InvalidDataException("Invalid piece source or status.");
                if (piece["anchor"] is JObject anchor)
                {
                    if (anchor.Properties().Any(p => p.Name is not ("path" or "startLine" or "endLine" or "hash"))
                        || anchor["path"]?.Type != JTokenType.String || anchor["hash"]?.Type != JTokenType.String
                        || anchor["startLine"]?.Type != JTokenType.Integer || anchor["endLine"]?.Type != JTokenType.Integer)
                        throw new InvalidDataException("Invalid piece anchor.");
                    var start = (int)anchor["startLine"]!;
                    var end = (int)anchor["endLine"]!;
                    try { PortableRelativePath.Validate((string)anchor["path"]!); }
                    catch (ArgumentException ex) { throw new InvalidDataException("Invalid anchor path.", ex); }
                    if (start < 1 || end < start || end > start + 199) throw new InvalidDataException("Invalid anchor range.");
                    if ((string)anchor["hash"]! != KnowledgeText.Hash((string)piece["text"]!)) throw new InvalidDataException("The anchor hash does not match the text.");
                }
                else if (piece["anchor"] != null) throw new InvalidDataException("Invalid piece anchor.");
            }

            if (row["optimized"] is JObject optimized)
            {
                if (optimized.Properties().Any(p => p.Name is not ("text" or "sourceHash" or "createdAt" or "model"))
                    || optimized["text"]?.Type != JTokenType.String || optimized["sourceHash"]?.Type != JTokenType.String || optimized["createdAt"]?.Type != JTokenType.String
                    || (optimized["model"] != null && optimized["model"]!.Type != JTokenType.String))
                    throw new InvalidDataException("Invalid optimized text.");
                var text = KnowledgeText.Normalize((string)optimized["text"]!);
                if (text.Length == 0 || text.Length > MaxOptimizedChars || text != (string)optimized["text"]!) throw new InvalidDataException("Invalid optimized text.");
                if (!HashRegex.IsMatch((string)optimized["sourceHash"]!)) throw new InvalidDataException("Invalid optimized source hash.");
                if ((string?)optimized["model"] is string model && (model.Length == 0 || model.Length > 200 || model.Any(char.IsControl)))
                    throw new InvalidDataException("Invalid optimized model.");
            }

            foreach (var field in new[] { "roles", "teams" })
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in (JArray)row[field]!)
                {
                    if (token.Type != JTokenType.String || !TeamNameRegex.IsMatch((string)token!) || !names.Add((string)token!))
                        throw new InvalidDataException("Invalid knowledge " + field + ".");
                }
            }
        }

        // ---- one-time migration from the v1 store (read from its old place in the data directory, removed once imported) ----

        private static readonly WorkspaceJsonFile.Shape V1Shape = new WorkspaceJsonFile.Shape
        {
            Folder = "knowledge", Rows = "entries", MaxBytes = 1024 * 1024, MaxRows = 200,
            Fields = new HashSet<string>(StringComparer.Ordinal) { "id", "kind", "scope", "team", "role", "title", "body", "status", "source", "createdAt", "updatedAt", "revision" },
            CheckRow = CheckV1Row,
            Busy = ("다른 창에서 지식 저장소를 쓰고 있어요.", "The knowledge store is busy in another window."),
            Full = ("지식 저장소가 가득 찼어요.", "The knowledge store is full."),
            Unreadable = ("지식 저장소 파일을 읽지 못했어요: ", "The knowledge store could not be read: "),
        };

        /// <summary>
        /// Brings an old v1 file in the data directory into the v2 store (unless that exists already) and removes it, so that
        /// deleting <c>.pomp</c> cannot make it come back.
        /// </summary>
        private void EnsureMigrated()
        {
            var old = v1.FilePath;
            if (!File.Exists(old)) return;
            if (!File.Exists(store.FilePath)) Import();
            try { File.Delete(old); File.Delete(old + ".lock"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Creates the v2 store from the v1 one once: each entry becomes a piece of the knowledge named by its title (cut to
        /// 40 chars; same names merge, and a cut title is kept as the piece's first line). Team/role scopes become project
        /// knowledge assigned to that team/role. Only the newest 100 knowledge and 100 pieces each survive absurd old stores.
        /// </summary>
        private void Import()
        {
            // Strict like the old store: a broken v1 file fails closed (nothing is written anywhere, the file stays).
            var entries = v1.Read().OfType<JObject>().ToList();
            MutateAll(rows =>
            {
                if (rows.OfType<JObject>().Any(r => (string?)r["scope"] == "project")) return 0; // Another process (or window) migrated first.
                var groups = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                var order = new List<JObject>();
                foreach (var entry in entries)
                {
                    var title = (string)entry["title"]!;
                    var name = title.Length > MaxNameChars ? title.Substring(0, MaxNameChars) : title;
                    var text = KnowledgeText.Normalize(title.Length > MaxNameChars ? title + "\n" + (string)entry["body"]! : (string)entry["body"]!);
                    if (text.Length == 0) continue;
                    if (text.Length > MaxPieceChars) text = KnowledgeText.Normalize(text.Substring(0, MaxPieceChars));
                    if (!groups.TryGetValue(name, out var row))
                    {
                        var scope = (string)entry["scope"]!;
                        row = new JObject
                        {
                            ["id"] = Guid.NewGuid().ToString("N"), ["name"] = name, ["color"] = ColorRotation[order.Count % ColorRotation.Length],
                            ["scope"] = "project", ["use"] = "original",
                            ["pieces"] = new JArray(), ["roles"] = new JArray(), ["teams"] = new JArray(),
                            ["createdAt"] = entry["createdAt"], ["updatedAt"] = entry["updatedAt"], ["revision"] = 1,
                        };
                        groups[name] = row;
                        order.Add(row);
                    }

                    if ((string)entry["scope"]! == "role") AddUnique((JArray)row["roles"]!, (string)entry["role"]!);
                    if ((string)entry["scope"]! == "team") AddUnique((JArray)row["teams"]!, (string)entry["team"]!);
                    if (string.Compare((string)entry["createdAt"]!, (string)row["createdAt"]!, StringComparison.Ordinal) < 0) row["createdAt"] = entry["createdAt"];
                    var pieces = (JArray)row["pieces"]!;
                    pieces.Add(new JObject
                    {
                        ["id"] = Guid.NewGuid().ToString("N"), ["text"] = text, ["source"] = (string)entry["source"]!,
                        ["status"] = (string)entry["status"]! == "candidate" ? "candidate" : "saved", ["createdAt"] = entry["createdAt"],
                    });
                }

                foreach (var row in order) rows.Add(row);
                return order.Count;
            });
        }

        private static void AddUnique(JArray list, string name)
        {
            if (!list.Any(t => (string?)t == name)) list.Add(name);
        }

        private static void CheckV1Row(JObject row)
        {
            if (new[] { "kind", "scope", "title", "body", "status", "source", "createdAt", "updatedAt" }.Any(f => row[f]?.Type != JTokenType.String)
                || row["revision"]?.Type != JTokenType.Integer || (int)row["revision"]! < 1
                || (row["team"] != null && row["team"]!.Type != JTokenType.String) || (row["role"] != null && row["role"]!.Type != JTokenType.String))
                throw new InvalidDataException("Invalid knowledge entry field type.");
            if ((string)row["status"]! is not ("saved" or "candidate") || (string)row["source"]! is not ("user" or "agent")) throw new InvalidDataException("Invalid knowledge entry status.");
            var kind = (string)row["kind"]!;
            var scope = (string)row["scope"]!;
            var team = (string?)row["team"];
            var role = (string?)row["role"];
            var title = (string)row["title"]!;
            var body = (string)row["body"]!;
            if (kind != "note" && kind != "prompt") throw new InvalidDataException("Invalid entry kind.");
            if (scope != "project" && scope != "personal" && scope != "team" && scope != "role") throw new InvalidDataException("Invalid entry scope.");
            if ((scope == "team") != (team != null) || (team != null && !TeamNameRegex.IsMatch(team))) throw new InvalidDataException("Invalid entry team.");
            if ((scope == "role") != (role != null) || (role != null && !TeamNameRegex.IsMatch(role))) throw new InvalidDataException("Invalid entry role.");
            if (title.Length == 0 || title.Length > 120 || title.Any(char.IsControl)) throw new InvalidDataException("Invalid entry title.");
            if (body.Length == 0 || body.Length > 8000 || body.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                throw new InvalidDataException("Invalid entry body.");
        }
    }
}
