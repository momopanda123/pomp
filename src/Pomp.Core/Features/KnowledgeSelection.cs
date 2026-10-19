using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>
    /// Which knowledge is selected in each conversation (<c>.pomp/local/knowledge-sessions.json</c>): the page's context basket,
    /// continued in every message of the conversation. A conversation keeps what it picked besides the default knowledge (<c>ids</c>)
    /// and the default knowledge it unticked (<c>excluded</c>); every other default knowledge is selected in it. The newest 200
    /// conversations are kept; a malformed file fails closed (selections fall back to empty and nothing is overwritten).
    /// </summary>
    internal sealed class KnowledgeSelection
    {
        private const int MaxRows = 200;

        private static readonly WorkspaceJsonFile.Shape Shape = new WorkspaceJsonFile.Shape
        {
            Folder = "knowledge-sessions", Rows = "selections", MaxBytes = 1024 * 1024, MaxRows = MaxRows,
            Fields = new HashSet<string>(StringComparer.Ordinal) { "id", "sessionId", "ids", "excluded", "updatedAt" },
            CheckRow = CheckRow,
            Busy = ("다른 창에서 지식 선택을 쓰고 있어요.", "The knowledge selection is busy in another window."),
            Full = ("지식 선택이 가득 찼어요.", "The knowledge selection is full."),
            Unreadable = ("지식 선택을 읽지 못했어요: ", "The knowledge selection could not be read: "),
        };

        private readonly WorkspaceJsonFile store;

        internal KnowledgeSelection(string dataDirectory, string workspaceRoot) => store = new WorkspaceJsonFile(dataDirectory, workspaceRoot, Shape);

        /// <summary>The row id for a conversation: the first 32 hex digits of SHA-256 of its session id.</summary>
        internal static string RowId(string sessionId)
        {
            using var sha = SHA256.Create();
            var builder = new StringBuilder(32);
            foreach (var b in sha.ComputeHash(Encoding.UTF8.GetBytes(sessionId ?? ""))) builder.Append(b.ToString("x2"));
            return builder.ToString(0, 32);
        }

        internal sealed class Choice
        {
            internal static readonly Choice None = new Choice(Array.Empty<string>(), Array.Empty<string>());
            internal Choice(IReadOnlyList<string> picked, IReadOnlyList<string> excluded) { Picked = picked; Excluded = excluded; }
            /// <summary>Knowledge picked besides the defaults.</summary>
            internal IReadOnlyList<string> Picked { get; }
            /// <summary>Default knowledge unticked in this conversation.</summary>
            internal IReadOnlyList<string> Excluded { get; }
        }

        internal Choice Get(string sessionId)
        {
            var id = RowId(sessionId);
            var row = store.Read().OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id && (string?)r["sessionId"] == sessionId);
            if (row == null) return Choice.None;
            return new Choice(((JArray)row["ids"]!).Select(t => (string)t!).ToList(), (row["excluded"] as JArray)?.Select(t => (string)t!).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>());
        }

        /// <summary>Saves the choice (unknown ids are the caller's to filter); the oldest conversations fall out past 200.</summary>
        internal void Set(string sessionId, Choice choice)
        {
            var picked = choice.Picked.Distinct(StringComparer.Ordinal).ToList();
            var excluded = choice.Excluded.Distinct(StringComparer.Ordinal).ToList();
            store.Mutate(rows =>
            {
                var id = RowId(sessionId);
                var row = rows.OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id);
                if (row == null)
                {
                    row = new JObject { ["id"] = id, ["sessionId"] = sessionId, ["ids"] = new JArray(), ["updatedAt"] = "" };
                    rows.Add(row);
                }

                row["sessionId"] = sessionId;
                row["ids"] = new JArray(picked);
                if (excluded.Count > 0) row["excluded"] = new JArray(excluded); else row.Remove("excluded");
                row["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
                // Newest last (eviction order); the id stays stable so a re-selected conversation keeps its row.
                rows.Remove(row);
                rows.Add(row);
                while (rows.Count > MaxRows) rows.RemoveAt(0);
                return id;
            });
        }

        internal void Remove(string sessionId) => store.Mutate(rows =>
        {
            foreach (var row in rows.OfType<JObject>().Where(r => (string?)r["sessionId"] == sessionId).ToArray()) row.Remove();
            return RowId(sessionId);
        });

        private static void CheckRow(JObject row)
        {
            if (row["sessionId"]?.Type != JTokenType.String || row["updatedAt"]?.Type != JTokenType.String || row["ids"] is not JArray ids
                || (row["excluded"] != null && row["excluded"] is not JArray))
                throw new InvalidDataException("Invalid selection field type.");
            if (string.IsNullOrEmpty((string)row["sessionId"]!)) throw new InvalidDataException("Invalid selection session.");
            foreach (var list in new[] { ids, row["excluded"] as JArray ?? new JArray() })
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in list)
                {
                    if (token.Type != JTokenType.String || ((string)token!).Length != 32 || !seen.Add((string)token!))
                        throw new InvalidDataException("Invalid selection ids.");
                }
            }
        }
    }
}
