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
    /// <c>{"version":1,"&lt;rows&gt;":[...]}</c>, or for a <see cref="Shape.Shared"/> store <c>&lt;root&gt;/.pomp/&lt;name&gt;.json</c>, which
    /// is committed. Every row is an object with a unique 32-hex <c>id</c>, known fields only and whatever the store's own row check
    /// accepts. Reads are bounded and strict and fail closed: a broken file throws and is never reset. A mutation holds the store's
    /// process gate and a cross-process lease (<c>.lock</c> in <c>.pomp/local</c>, no sharing) across read, change, limits and atomic
    /// write, so it never writes a file that its own read would refuse. A store an earlier POMP kept in the data directory
    /// (<c>&lt;folder&gt;/&lt;hash of the root&gt;.json</c>), or a shared one it kept in <c>.pomp/local</c>, is moved here on first use.
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
            /// <summary>Kept in <c>.pomp</c> itself and committed: written indented, so git shows and merges it by line.</summary>
            public bool Shared { get; set; }
        }

        private static readonly ConcurrentDictionary<string, object> Gates = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly Regex IdRegex = new Regex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

        private readonly Shape shape;
        private readonly string file, lockFile;
        private readonly object gate;

        internal WorkspaceJsonFile(string dataDirectory, string workspaceRoot, Shape shape)
            : this(shape, shape.Shared ? SharedPathFor(dataDirectory, workspaceRoot, shape) : PathFor(dataDirectory, shape.Folder, workspaceRoot, shape.FileName),
                   shape.Shared ? Path.Combine(ProjectData.LocalFolder(workspaceRoot), NameOf(shape) + ".lock") : null) { }

        private WorkspaceJsonFile(Shape shape, string file, string? lockFile = null)
        {
            this.shape = shape;
            this.file = file;
            this.lockFile = lockFile ?? file + ".lock";
            gate = Gates.GetOrAdd(shape.Folder, _ => new object());
        }

        private static string NameOf(Shape shape) => string.IsNullOrEmpty(shape.FileName) ? shape.Folder + ".json" : shape.FileName;

        /// <summary><c>.pomp/&lt;name&gt;</c>; the file an earlier POMP kept in <c>.pomp/local</c> (or, before that, in the data directory) is moved there first.</summary>
        private static string SharedPathFor(string dataDirectory, string workspaceRoot, Shape shape)
        {
            var target = Path.Combine(ProjectData.ProjectFolder(workspaceRoot), NameOf(shape));
            var local = PathFor(dataDirectory, shape.Folder, workspaceRoot, shape.FileName);
            try
            {
                CheckpointStore.CheckDirectPath(target);
                if (!File.Exists(target) && File.Exists(local)) File.Move(local, target);
            }
            catch (IOException) { } // Another window moved it first, or it cannot move: the next use tries again.
            catch (UnauthorizedAccessException) { }
            return target;
        }

        /// <summary>A store still at its old place in the data directory, to be read (and imported) only: nothing is created or moved.</summary>
        internal static WorkspaceJsonFile AtOldLocation(string dataDirectory, string workspaceRoot, Shape shape) => new WorkspaceJsonFile(shape, OldPath(dataDirectory, shape.Folder, workspaceRoot));

        /// <summary>A store at <paramref name="file"/> (outside any workspace, such as the user's own), with its lease next to it.</summary>
        internal static WorkspaceJsonFile At(string file, Shape shape) => new WorkspaceJsonFile(shape, Path.GetFullPath(file));

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
                using (Lease())
                {
                    var rows = ReadLocked();
                    var result = change(rows);
                    var text = Serialize(rows);
                    Write(text);
                    return result;
                }
        }

        /// <summary>
        /// Like <see cref="Mutate{T}"/> over this store and <paramref name="other"/> together, each under its own gate and lease. Each
        /// file is replaced atomically, the pair is not: <paramref name="change"/> says which file to write first (the one rows moved into,
        /// so an interruption leaves a row twice rather than nowhere).
        /// </summary>
        internal T MutateWith<T>(WorkspaceJsonFile other, Func<JArray, JArray, (T Result, bool OtherFirst)> change)
        {
            lock (gate)
                using (Lease())
                lock (other.gate)
                    using (other.Lease())
                    {
                        var rows = ReadLocked(); var others = other.ReadLocked();
                        var (result, otherFirst) = change(rows, others);
                        var text = Serialize(rows); var otherText = other.Serialize(others);
                        if (otherFirst) { other.Write(otherText); Write(text); }
                        else { Write(text); other.Write(otherText); }
                        return result;
                    }
        }

        private FileStream Lease()
        {
            CheckpointStore.CheckDirectPath(lockFile);
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            try { return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(PompLanguage.Pick(shape.Busy.Ko, shape.Busy.En), ex);
            }
        }

        /// <summary>The file text for <paramref name="rows"/>; throws when it is over the store's limits (nothing is written then).</summary>
        private string Serialize(JArray rows)
        {
            var text = new JObject { ["version"] = 1, [shape.Rows] = rows }.ToString(shape.Shared ? Formatting.Indented : Formatting.None);
            if (rows.Count > shape.MaxRows || Encoding.UTF8.GetByteCount(text) > shape.MaxBytes) throw new InvalidOperationException(PompLanguage.Pick(shape.Full.Ko, shape.Full.En));
            return shape.Shared ? text + "\n" : text;
        }

        private void Write(string text)
        {
            CheckpointStore.CheckDirectPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, text);
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
