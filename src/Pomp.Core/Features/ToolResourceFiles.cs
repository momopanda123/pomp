using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>One resource file as the catalogs list it: loadable into the in-page editor, or why not.</summary>
    internal sealed class ExaminedFile
    {
        /// <summary>The whole file (at most the limit) when the editor can load it; null when it is over the limit or not valid UTF-8.</summary>
        internal byte[]? Bytes;
        /// <summary>The decoded text without a leading BOM; empty unless <see cref="Bytes"/> is set.</summary>
        internal string Text = "";
        /// <summary>SHA-256 of the whole file in the format of <see cref="ToolResourceFiles.Hash(byte[])"/>; null above <see cref="ToolResourceFiles.MaxHashBytes"/>.</summary>
        internal string? Hash;
        /// <summary>Why the editor cannot load the file and what to do about it (shown as the row's note); null when it can.</summary>
        internal string? Problem;
        internal bool Unreadable => Bytes == null;
    }

    internal static class ToolResourceFiles
    {
        internal static readonly object Gate = new object();
        /// <summary>Files up to this size get a (streamed) hash; a bigger one can be opened from the page but not deleted.</summary>
        internal const long MaxHashBytes = 16L * 1024 * 1024;
        private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);
        private static readonly string[] Devices = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        internal static byte[] Read(string file, int limit)
        {
            CheckpointStore.CheckDirectPath(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > limit) throw new IOException(PompLanguage.Pick("파일이 너무 커요.", "The file is too large."));
            using var memory = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + count > limit) throw new IOException(PompLanguage.Pick("파일이 너무 커요.", "The file is too large."));
                memory.Write(buffer, 0, count);
            }
            CheckpointStore.CheckDirectPath(file); return memory.ToArray();
        }
        /// <summary>
        /// A file for listing: its text and hash when the editor can load it (at most <paramref name="limit"/> bytes of valid UTF-8),
        /// else the reason, and the hash of the whole file (streamed) so that it can still be deleted from the page.
        /// <paramref name="folderChecked"/>: the caller checked the folder for links and lists many files, so only the file itself is checked (a walk up the path per file would dominate a big folder).
        /// </summary>
        internal static ExaminedFile Examine(string file, int limit, bool folderChecked = false)
        {
            if (folderChecked) CheckNotLinked(file); else CheckpointStore.CheckDirectPath(file);
            var result = new ExaminedFile();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bytes = ReadUpTo(stream, limit + 1);
                if (bytes.Length <= limit)
                {
                    result.Hash = Hash(bytes);
                    try { result.Text = Strict.GetString(bytes).TrimStart('﻿'); result.Bytes = bytes; }
                    catch (DecoderFallbackException) { result.Problem = PompLanguage.Pick("파일이 UTF-8 텍스트가 아니에요.", "The file is not UTF-8 text."); }
                }
                else
                {
                    stream.Position = 0;
                    result.Hash = HashOpen(stream, MaxHashBytes);
                    result.Problem = result.Hash != null
                        ? PompLanguage.Pick("파일이 너무 커서 열 수 없어요.", "The file is too large to open.")
                        : PompLanguage.Pick("파일이 너무 커서 열 수 없어요.", "The file is too large to open.");
                }
            }

            if (folderChecked) CheckNotLinked(file); else CheckpointStore.CheckDirectPath(file);
            return result;
        }
        private static byte[] ReadUpTo(Stream stream, int max)
        {
            using var memory = new MemoryStream(); var buffer = new byte[4096]; int count;
            while (memory.Length < max && (count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, max - memory.Length))) > 0) memory.Write(buffer, 0, count);
            return memory.ToArray();
        }
        /// <summary>Refuses a symbolic link or junction at <paramref name="entry"/> itself (its parents were checked by the caller): the cheap check for walking many entries.</summary>
        internal static void CheckNotLinked(string entry)
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException(PompLanguage.Pick("경로에 심볼릭 링크나 정션이 있어요.", "The path contains a symbolic link or junction."));
        }
        /// <summary>Writes a new file through a sibling temporary file moved into place: never an overwrite (including a concurrent writer of the same name) and never a half-written file.</summary>
        internal static void WriteNew(string file, byte[] bytes)
        {
            lock (Gate)
            {
                var folder = Path.GetDirectoryName(file)!;
                CheckpointStore.CheckDirectPath(file);
                Directory.CreateDirectory(folder);
                CheckpointStore.CheckDirectPath(file);
                var temporary = Path.Combine(folder, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }

                    CheckpointStore.CheckDirectPath(file);
                    CheckpointStore.CheckDirectPath(temporary);
                    File.Move(temporary, file);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        /// <summary>
        /// Hash-pinned replace: re-reads the file (≤ <paramref name="limit"/> bytes) and refuses with an
        /// <see cref="InvalidOperationException"/> when <paramref name="expectedHash"/> is null or no longer matches,
        /// then writes through a unique sibling temporary file swapped in atomically.
        /// </summary>
        internal static void Replace(string file, byte[] bytes, string? expectedHash, int limit)
        {
            lock (Gate)
            {
                CheckpointStore.CheckDirectPath(file);
                byte[] current;
                try { current = Read(file, limit); }
                catch (IOException) when (File.Exists(file) && new FileInfo(file).Length > limit)
                {
                    throw new InvalidOperationException(PompLanguage.Pick("파일이 너무 커서 바꿀 수 없어요.", "The file is too large to change."));
                }

                if (expectedHash == null || !string.Equals(Hash(current), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(PompLanguage.Pick("파일이 바뀌었어요.", "The file changed."));
                var folder = Path.GetDirectoryName(file)!;
                var temporary = Path.Combine(folder, ".update-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }

                    CheckpointStore.CheckDirectPath(file);
                    CheckpointStore.CheckDirectPath(temporary);
                    if (File.Exists(file)) File.Replace(temporary, file, null);
                    else File.Move(temporary, file);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        /// <summary>
        /// Deletes a file of any size up to <see cref="MaxHashBytes"/> when its whole content still has <paramref name="expectedHash"/>:
        /// the file stays open (no writers) from the hash to the delete. Anything else is an <see cref="InvalidOperationException"/> with <paramref name="changed"/>.
        /// </summary>
        internal static void DeleteIfHash(string file, string? expectedHash, string changed)
        {
            lock (Gate)
            {
                CheckpointStore.CheckDirectPath(file);
                if (string.IsNullOrEmpty(expectedHash) || !File.Exists(file)) throw new InvalidOperationException(changed);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var hash = HashOpen(stream, MaxHashBytes);
                if (hash == null || !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(changed);
                CheckpointStore.CheckDirectPath(file);
                File.Delete(file);
            }
        }
        /// <summary>Pasted Markdown as strict UTF-8 of at most <paramref name="limit"/> bytes; a longer one is refused with both sizes.</summary>
        internal static byte[] DecodeImport(string content, int limit, string koreanNoun, string englishNoun)
        {
            int count;
            try { count = Strict.GetByteCount(content); }
            catch (EncoderFallbackException) { throw new ArgumentException(PompLanguage.Pick("Markdown이 올바른 Unicode가 아니에요.", "The Markdown is not valid Unicode.")); }
            if (count > limit) throw TooLarge(koreanNoun, englishNoun, limit, count);
            return Strict.GetBytes(content);
        }
        /// <summary>Text about to be written as strict UTF-8 of at most <paramref name="limit"/> bytes (the limit the reader enforces); a longer one is refused with both sizes.</summary>
        internal static byte[] Encode(string text, int limit, string koreanNoun, string englishNoun)
        {
            int count;
            try { count = Strict.GetByteCount(text); }
            catch (EncoderFallbackException) { throw new ArgumentException(PompLanguage.Pick("텍스트가 올바른 Unicode가 아니에요.", "The text is not valid Unicode.")); }
            if (count > limit) throw TooLarge(koreanNoun, englishNoun, limit, count);
            return Strict.GetBytes(text);
        }
        /// <summary>"스킬 파일이 너무 커요." (the noun is the Korean/English subject, e.g. 스킬 파일 / skill file).</summary>
        internal static ArgumentException TooLarge(string koreanNoun, string englishNoun, int limit, long actual) => new ArgumentException(PompLanguage.Pick(
            koreanNoun + "이 너무 커요.",
            "The " + englishNoun + " is too large."));
        internal static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        internal static string ScopeName(string scope) => scope == "user" ? PompLanguage.Pick("사용자", "user") : PompLanguage.Pick("프로젝트", "project");
        /// <summary>
        /// An existing file by a page-supplied name: one plain file name (no separators, invalid characters, device names or dot names),
        /// a direct child of <paramref name="folder"/> that no link leads through. Existence is the caller's check; the stricter
        /// new-name rule is applied to create/import only. <paramref name="invalid"/> is the message for a name that is not acceptable.
        /// </summary>
        internal static string ExistingFile(string folder, string name, string extension, string invalid)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.Length + extension.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Array.IndexOf(Devices, name.Split('.')[0].TrimEnd(' ').ToUpperInvariant()) >= 0)
                throw new ArgumentException(invalid);
            string directory, file;
            try
            {
                directory = Path.GetFullPath(folder).TrimEnd('\\', '/');
                file = Path.GetFullPath(Path.Combine(directory, name + extension));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { throw new ArgumentException(invalid); }
            if (!string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(file), name + extension, StringComparison.Ordinal))
                throw new ArgumentException(invalid);
            CheckpointStore.CheckDirectPath(file);
            return file;
        }
        internal static string Text(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        internal static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(bytes)); }
        internal static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
        /// <summary>The same hash as <see cref="Hash(byte[])"/>, streamed; null above <paramref name="cap"/> bytes.</summary>
        internal static string? HashFile(string file, long cap = MaxHashBytes)
        {
            CheckpointStore.CheckDirectPath(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = HashOpen(stream, cap);
            CheckpointStore.CheckDirectPath(file);
            return hash;
        }
        private static string? HashOpen(Stream stream, long cap)
        {
            if (stream.Length > cap) return null;
            using var sha = SHA256.Create(); var buffer = new byte[81920]; long total = 0; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += count; if (total > cap) return null;
                sha.TransformBlock(buffer, 0, count, null, 0);
            }

            sha.TransformFinalBlock(buffer, 0, 0);
            return Hex(sha.Hash!);
        }
        private static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        internal static string Home(string? home) => Path.GetFullPath(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        internal static string Scope(string scope, string root, string home) => scope == "project" ? root : scope == "user" ? home : throw new ArgumentException(PompLanguage.Pick("범위를 고르지 않았어요.", "No scope was chosen."));
    }
}
