using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// The rows of one workspace store in the workspace's own data folder: <c>&lt;root&gt;/.pomp/local/&lt;name&gt;.json</c> holding
    /// <c>{"version":1,"&lt;rows&gt;":[...]}</c>. Every row is an object with a unique 32-hex <c>id</c>, known fields only and
    /// whatever the store's own row check accepts. Reads are bounded and strict and fail closed: a broken file throws and is
    /// never reset. A mutation holds the store's process gate and a cross-process lease (<c>.lock</c>, no sharing) across
    /// read, change, limits and atomic write, so it never writes a file that its own read would refuse. A store an earlier POMP
    /// kept in the data directory (<c>&lt;folder&gt;/&lt;hash of the root&gt;.json</c>) is moved here on first use.
    /// </summary>
    internal sealed class WorkspaceJsonFile
    {
        /// <summary>What one store keeps and how its problems read to the user.</summary>
        internal sealed class Shape
        {
            /// <summary>Where an earlier POMP kept the store under the data directory; also the new file's name unless <see cref="FileName"/> is set.</summary>
            public string Folder { get; set; } = "";
            /// <summary>The file in <c>.pomp/local</c>; <c>&lt;Folder&gt;.json</c> when empty.</summary>
            public string FileName { get; set; } = "";
            public string Rows { get; set; } = "";
            public int MaxBytes { get; set; }
            public int MaxRows { get; set; }
            public HashSet<string> Fields { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Types and meaning of one row (the id is already checked); throws when the row is invalid.</summary>
            public Action<JObject> CheckRow { get; set; } = _ => { };
            public (string Ko, string En) Busy { get; set; }
            public (string Ko, string En) Full { get; set; }
            /// <summary>Prefix of the read failure; the cause follows it.</summary>
            public (string Ko, string En) Unreadable { get; set; }
        }

        private static readonly ConcurrentDictionary<string, object> Gates = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly Regex IdRegex = new Regex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

        private readonly Shape shape;
        private readonly string file, lockFile;
        private readonly object gate;

        internal WorkspaceJsonFile(string dataDirectory, string workspaceRoot, Shape shape) : this(shape, PathFor(dataDirectory, shape.Folder, workspaceRoot, shape.FileName)) { }

        private WorkspaceJsonFile(Shape shape, string file)
        {
            this.shape = shape;
            this.file = file;
            lockFile = file + ".lock";
            gate = Gates.GetOrAdd(shape.Folder, _ => new object());
        }

        /// <summary>A store still at its old place in the data directory, to be read (and imported) only: nothing is created or moved.</summary>
        internal static WorkspaceJsonFile AtOldLocation(string dataDirectory, string workspaceRoot, Shape shape) => new WorkspaceJsonFile(shape, OldPath(dataDirectory, shape.Folder, workspaceRoot));

        /// <summary>The store file.</summary>
        internal string FilePath => file;

        /// <summary>
        /// The store file of a workspace: <c>.pomp/local/&lt;name&gt;</c> (<c>&lt;folder&gt;.json</c> unless a name is given). The file an
        /// earlier POMP kept in the data directory is moved there first.
        /// </summary>
        internal static string PathFor(string dataDirectory, string folder, string workspaceRoot, string? name = null)
            => ProjectData.File(workspaceRoot, string.IsNullOrEmpty(name) ? folder + ".json" : name!, OldPath(dataDirectory, folder, workspaceRoot));

        /// <summary>Where an earlier POMP kept a store: the full root without a trailing separator, case-folded on Windows, hashed.</summary>
        internal static string OldPath(string dataDirectory, string folder, string workspaceRoot)
        {
            var root = Path.GetFullPath(workspaceRoot).TrimEnd('\\', '/');
            return Path.Combine(dataDirectory, folder, ContentHash.Compute(Path.DirectorySeparatorChar == '\\' ? root.ToLowerInvariant() : root) + ".json");
        }

        /// <summary>The checked rows; empty when the store has no file yet. Throws when the file is unreadable.</summary>
        internal JArray Read()
        {
            lock (gate) return ReadLocked();
        }

        /// <summary>Applies <paramref name="change"/> to the current rows and saves them; nothing is written when it throws.</summary>
        internal T Mutate<T>(Func<JArray, T> change)
        {
            lock (gate)
            {
                CheckpointStore.CheckDirectPath(lockFile);
                Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
                FileStream lease;
                try { lease = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(PompLanguage.Pick(shape.Busy.Ko, shape.Busy.En), ex);
                }

                using (lease)
                {
                    var rows = ReadLocked();
                    var result = change(rows);
                    var text = new JObject { ["version"] = 1, [shape.Rows] = rows }.ToString(Formatting.None);
                    if (rows.Count > shape.MaxRows || Encoding.UTF8.GetByteCount(text) > shape.MaxBytes) throw new InvalidOperationException(PompLanguage.Pick(shape.Full.Ko, shape.Full.En));
                    CheckpointStore.CheckDirectPath(file);
                    AtomicFile.WriteAllText(file, text);
                    return result;
                }
            }
        }

        private JArray ReadLocked()
        {
            CheckpointStore.CheckDirectPath(file);
            if (!File.Exists(file)) return new JArray();
            try
            {
                if (new FileInfo(file).Length > shape.MaxBytes) throw new InvalidDataException("The file is too large.");
                using var reader = new JsonTextReader(new StringReader(File.ReadAllText(file, new UTF8Encoding(false, true)))) { MaxDepth = 6, DateParseHandling = DateParseHandling.None };
                var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("Trailing content.");
                if (root.Properties().Any(p => p.Name != "version" && p.Name != shape.Rows) || root["version"]?.Type != JTokenType.Integer || (int)root["version"]! != 1
                    || root[shape.Rows] is not JArray rows || rows.Count > shape.MaxRows) throw new InvalidDataException("Invalid store.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in rows)
                {
                    if (token is not JObject row || row.Properties().Any(p => !shape.Fields.Contains(p.Name))) throw new InvalidDataException("Invalid entry.");
                    if (row["id"]?.Type != JTokenType.String || !IdRegex.IsMatch((string)row["id"]!) || !ids.Add((string)row["id"]!)) throw new InvalidDataException("Invalid entry id.");
                    shape.CheckRow(row);
                }

                return rows;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick(shape.Unreadable.Ko, shape.Unreadable.En) + ex.Message, ex);
            }
        }
    }
}
