using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;

namespace Pomp.Hosting
{
    // Role memory (docs/architecture.md §8.9): the knowledge assigned to each agent role, handed to the omp extension through
    // POMP_STATE_DIR/role-memory.json (that role's notes in its subagent system prompt, as reference material), and the
    // conversation's selected knowledge through session-knowledge.json (the main session's system prompt).
    public sealed partial class PompSession
    {
        internal const string RoleMemoryFileName = "role-memory.json";
        internal const string SessionKnowledgeFileName = "session-knowledge.json";
        private const int RoleMemoryNotes = 10;
        private const int RoleMemoryChars = 8000;

        private string? _roleMemoryDirectory;

        /// <summary>Serializes the state-file writes against their deletion in <see cref="PompSession.Dispose"/>.</summary>
        private readonly object _knowledgeStateFileLock = new object();

        /// <summary>At each omp start, before the spawn: the directory the extension reads, with the current notes.</summary>
        private void PrepareRoleMemory(string stateDirectory)
        {
            lock (_stateLock) _roleMemoryDirectory = stateDirectory;
            WriteRoleMemory();
            WriteSessionKnowledge();
        }

        /// <summary>
        /// Rewrites the file from the store. Never throws: an unreadable store writes no notes (stale memory must not linger), and a
        /// failed write leaves the extension with the previous file or none, which only means less reference material.
        /// </summary>
        private void WriteRoleMemory()
        {
            string? directory; string root;
            lock (_stateLock) { directory = _roleMemoryDirectory; root = _workingDirectory; }
            if (directory == null) return;
            var roles = new JObject();
            try
            {
                if (root.Length > 0)
                    foreach (var pair in new KnowledgeStore(_environment.DataDirectory, root).RoleKnowledge(RoleMemoryNotes, RoleMemoryChars))
                        roles[pair.Key] = new JArray(pair.Value.Select(note => new JObject { ["title"] = note.Title, ["body"] = note.Body }));
            }
            catch (Exception ex)
            {
                roles = new JObject();
                _logger.Warn("Role memory was left empty: " + ex.Message);
            }

            WriteStateFile(directory, RoleMemoryFileName, new JObject { ["version"] = 1, ["root"] = root, ["roles"] = roles });
        }

        /// <summary>
        /// Rewrites the conversation's selected knowledge for the extension (selection order, 20 items / 24,000 chars). Never
        /// throws; a missing selection or a failed write leaves the previous file or none, which only means less reference material.
        /// </summary>
        private void WriteSessionKnowledge()
        {
            string? directory; string root; string session;
            lock (_stateLock) { directory = _roleMemoryDirectory; root = _workingDirectory; }
            lock (_knowledgeLock) session = _knowledgeSession;
            if (directory == null) return;
            var items = new JArray();
            try
            {
                if (root.Length > 0 && session.Length > 0)
                    foreach (var item in new KnowledgeStore(_environment.DataDirectory, root).SelectedItems(KnowledgeSelectionFor(session), 20, 24000))
                        items.Add(new JObject { ["title"] = item.Title, ["body"] = item.Body });
            }
            catch (Exception ex)
            {
                items = new JArray();
                _logger.Warn("Session knowledge was left empty: " + ex.Message);
            }

            WriteStateFile(directory, SessionKnowledgeFileName, new JObject { ["version"] = 1, ["root"] = root, ["sessionId"] = session, ["items"] = items });
        }

        private void WriteStateFile(string directory, string fileName, JObject content)
        {
            // A late recovery or state refresh racing Dispose must not recreate the deleted state directory (checked inside
            // the lock: a write either finishes before the deletion or sees the disposal).
            lock (_knowledgeStateFileLock)
            {
                if (Volatile.Read(ref _disposed) == 1) return;
                try
                {
                    var file = Path.Combine(directory, fileName);
                    Directory.CreateDirectory(directory);
                    CheckpointStore.CheckDirectPath(file);
                    AtomicFile.WriteAllText(file, content.ToString(Formatting.None));
                }
                catch (Exception ex) { _logger.Warn(fileName + " was not written: " + ex.Message); }
            }
        }
    }
}