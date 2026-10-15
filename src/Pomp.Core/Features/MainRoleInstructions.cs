using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// The instructions of POMP's own role (Agents › 역할 › POMP): <c>APPEND_SYSTEM.md</c>, which the engine appends to the chat agent's
    /// system prompt. The engine takes the project's (<c>&lt;root&gt;/.omp</c>, then <c>.claude</c>, <c>.codex</c>, <c>.gemini</c>) before the
    /// user's (<c>~/.omp/agent</c>) and reads it when it starts; POMP writes the project's <c>.omp</c> file or the user's.
    /// </summary>
    public sealed class MainRoleInstructions
    {
        public const string FileName = "APPEND_SYSTEM.md";
        public const int MaxChars = 24000;
        private const long MaxFileBytes = 1024 * 1024;
        private static readonly string[] ProjectFolders = { ".omp", ".claude", ".codex", ".gemini" };

        private readonly string? root;
        private readonly string userHome;

        public MainRoleInstructions(string? workspaceRoot, string userHome)
        {
            root = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot);
            this.userHome = Path.GetFullPath(userHome);
        }

        public string? ProjectFile => root == null ? null : Path.Combine(root, ".omp", FileName);
        public string UserFile => Path.Combine(userHome, ".omp", "agent", FileName);

        /// <summary>The file the engine takes now and the scope it belongs to; null when there is none.</summary>
        public (string Scope, string Path)? Effective()
        {
            if (root != null)
            {
                foreach (var folder in ProjectFolders)
                {
                    var path = Path.Combine(root, folder, FileName);
                    if (File.Exists(path)) return ("project", path);
                }
            }
            return File.Exists(UserFile) ? ("user", UserFile) : ((string, string)?)null;
        }

        /// <summary>What the engine would take if it started now (file and content): a running engine that started with another is behind.</summary>
        public string Fingerprint()
        {
            try
            {
                var effective = Effective();
                return effective == null ? string.Empty : effective.Value.Path + "\0" + AgentRoster.ComputeFileHash(effective.Value.Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "unreadable";
            }
        }

        /// <summary><c>{ scope, project?: { exists, text, hash? }, user: { exists, text, hash? } }</c>; no <c>project</c> without a workspace.</summary>
        public JObject Read()
        {
            var result = new JObject { ["scope"] = Effective()?.Scope };
            if (ProjectFile != null) result["project"] = ReadOne(ProjectFile);
            result["user"] = ReadOne(UserFile);
            return result;
        }

        private static JObject ReadOne(string path)
        {
            if (!File.Exists(path)) return new JObject { ["exists"] = false, ["text"] = string.Empty };
            if (new FileInfo(path).Length > MaxFileBytes)
                throw new InvalidOperationException(PompLanguage.Pick("지시 파일이 너무 커요.", "The instructions file is too large."));
            var bytes = File.ReadAllBytes(path);
            var text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
            return new JObject { ["exists"] = true, ["text"] = text, ["hash"] = AgentRoster.ComputeSha256(bytes) };
        }

        /// <summary>
        /// Writes the instructions of <paramref name="scope"/>; blank text removes the file (the engine then takes the next one).
        /// <paramref name="expectedHash"/> is the hash the page read: the write is refused when the file changed since.
        /// </summary>
        public void Save(string scope, string text, string? expectedHash)
        {
            var path = scope == "project" ? ProjectFile ?? throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 열려 있지 않아요.", "No workspace is open."))
                : scope == "user" ? UserFile
                : throw new ArgumentException(PompLanguage.Pick($"알 수 없는 범위예요: '{scope}'", $"Unknown scope: '{scope}'"), nameof(scope));
            text ??= string.Empty;
            if (text.Length > MaxChars) throw new InvalidOperationException(PompLanguage.Pick("지시가 너무 길어요.", "The instructions are too long."));
            CheckpointStore.CheckDirectPath(path);
            if (File.Exists(path))
            {
                if (string.IsNullOrEmpty(expectedHash) || !string.Equals(AgentRoster.ComputeFileHash(path), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(PompLanguage.Pick("파일이 다른 프로세스에 의해 변경되었어요 (해시 불일치).", "File was modified by another process (hash mismatch)."));
            }
            else if (!string.IsNullOrEmpty(expectedHash))
            {
                throw new InvalidOperationException(PompLanguage.Pick("파일이 다른 프로세스에 의해 변경되었어요 (해시 불일치).", "File was modified by another process (hash mismatch)."));
            }

            var body = text.Replace("\r\n", "\n").Trim();
            if (body.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            AtomicFile.WriteAllText(path, body + "\n");
        }
    }
}
