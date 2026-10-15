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
    /// Which knowledge is selected in each conversation (<c>.pomp/local/knowledge-sessions.json</c>): the page's
    /// context basket, continued in every message of the conversation. The newest 200 conversations are kept; a malformed file
    /// fails closed (selections fall back to empty and nothing is overwritten).
    /// </summary>
    internal sealed class KnowledgeSelection
    {
        private const int MaxRows = 200;

        private static readonly WorkspaceJsonFile.Shape Shape = new WorkspaceJsonFile.Shape
        {
            Folder = "knowledge-sessions", Rows = "selections", MaxBytes = 1024 * 1024, MaxRows = MaxRows,
            Fields = new HashSet<string>(StringComparer.Ordinal) { "id", "sessionId", "ids", "updatedAt" },
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

        internal IReadOnlyList<string> Get(string sessionId)
        {
            var id = RowId(sessionId);
            var row = store.Read().OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == id && (string?)r["sessionId"] == sessionId);
            return row == null ? Array.Empty<string>() : ((JArray)row["ids"]!).Select(t => (string)t!).ToList();
        }

        /// <summary>Saves the selection (unknown ids are the caller's to filter); the oldest conversations fall out past 200.</summary>
        internal void Set(string sessionId, IEnumerable<string> ids)
        {
            var list = ids.Distinct(StringComparer.Ordinal).ToList();
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
                row["ids"] = new JArray(list);
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
            if (row["sessionId"]?.Type != JTokenType.String || row["updatedAt"]?.Type != JTokenType.String || row["ids"] is not JArray ids)
                throw new InvalidDataException("Invalid selection field type.");
            if (string.IsNullOrEmpty((string)row["sessionId"]!)) throw new InvalidDataException("Invalid selection session.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in ids)
            {
                if (token.Type != JTokenType.String || ((string)token!).Length != 32 || !seen.Add((string)token!))
                    throw new InvalidDataException("Invalid selection ids.");
            }
        }
    }
}
