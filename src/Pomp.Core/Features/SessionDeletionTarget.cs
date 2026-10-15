using System;
using System.Collections.Generic;
using System.IO;
using Pomp.Hosting;

namespace Pomp.Features
{
    internal sealed class SessionDeletionTarget
    {
        public SessionDeletionTarget(string path, string sessionId, string artifacts)
        {
            Path = path; SessionId = sessionId; Artifacts = artifacts;
            var info = new FileInfo(path); length = info.Length; modified = info.LastWriteTimeUtc;
        }
        private readonly long length;
        private readonly DateTime modified;
        public string Path { get; }
        public string SessionId { get; }
        public string Artifacts { get; }
        public IReadOnlyList<string> Paths => Directory.Exists(Artifacts) ? new[] { Artifacts, Path } : new[] { Path };
        public void Validate()
        {
            CheckpointStore.CheckDirectPath(Path);
            if (!File.Exists(Path) || (File.GetAttributes(Path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException(PompLanguage.Pick("대화 파일을 확인할 수 없어요.", "The conversation file is unavailable."));
            var info = new FileInfo(Path);
            if (info.Length != length || info.LastWriteTimeUtc != modified) throw new IOException("Conversation file changed before recycling.");
            CheckpointStore.CheckDirectPath(Artifacts);
            if (File.Exists(Artifacts)) throw new InvalidOperationException(PompLanguage.Pick("대화 자료 폴더가 올바르지 않아요.", "The conversation artifacts folder is invalid."));
            if (!Directory.Exists(Artifacts)) return;
            var folders = new Stack<string>(); folders.Push(Artifacts); var count = 0;
            while (folders.Count > 0)
                foreach (var entry in Directory.EnumerateFileSystemEntries(folders.Pop()))
                {
                    if (++count > 20000) throw new InvalidOperationException(PompLanguage.Pick("대화 자료가 너무 많아요.", "The conversation has too many artifacts."));
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException(PompLanguage.Pick("대화 자료에 연결된 경로가 있어요.", "The conversation artifacts contain a linked path."));
                    if ((attributes & FileAttributes.Directory) != 0) folders.Push(entry);
                }
        }
    }
}
