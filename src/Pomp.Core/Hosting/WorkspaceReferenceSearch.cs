using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Features;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>Bounded metadata-only folder/document discovery. No document bytes or linked trees are read.</summary>
    internal static class WorkspaceReferenceSearch
    {
        private const int MaxEntries = 20000;
        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", ".svn", ".hg", ".idea", "node_modules", "packages", "TestResults", ".agents" };
        private static readonly HashSet<string> Documents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".md", ".mdc", ".markdown", ".txt", ".rst", ".adoc", ".asciidoc", ".html", ".htm" };
        public static Task<WorkspaceFilesPayload> FindAsync(string? root,string query,string kind,int limit,CancellationToken token)
            => Task.Run(()=>Find(root,query,kind,limit,token),token);
        private static WorkspaceFilesPayload Find(string? root,string query,string kind,int limit,CancellationToken token)
        {
            if(kind!="folder" && kind!="document")throw new ArgumentException(PompLanguage.Pick("알 수 없는 참조 종류예요.", "Unknown reference kind."));
            if(string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))return new WorkspaceFilesPayload{Error=PompLanguage.Pick("작업 영역이 열려 있지 않아요.", "No workspace is open.")};
            root=Path.GetFullPath(root!);CheckpointStore.CheckDirectPath(root);
            query=(query??"").Trim().Replace('\\','/');if(query.Length>300)return new WorkspaceFilesPayload{Error=PompLanguage.Pick("참조 검색어가 너무 길어요.", "Reference query is too long.")};
            limit=Math.Max(1,Math.Min(WorkspaceFileSearch.MaxLimit,limit));
            var matches=new List<WorkspaceFileEntry>();var pending=new Queue<string>();pending.Enqueue(root);
            var watch=Stopwatch.StartNew();int scanned=0;bool limited=false,unreadable=false;
            while(pending.Count>0)
            {
                token.ThrowIfCancellationRequested();
                if(scanned>=MaxEntries || watch.ElapsedMilliseconds>=1500){limited=true;break;}
                var directory=pending.Dequeue();
                try
                {
                    // Recheck before enumerating each directory; skip reparse points rather than recursively following them.
                    if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)continue;
                    foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if(++scanned>MaxEntries || watch.ElapsedMilliseconds>=1500){limited=true;break;}
                        FileAttributes attributes;
                        try{attributes=File.GetAttributes(entry);}catch(Exception ex)when(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException){unreadable=true;continue;}
                        if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Hidden|FileAttributes.System))!=0)continue;
                        bool folder=(attributes&FileAttributes.Directory)!=0;
                        if(folder && (Skip.Contains(Path.GetFileName(entry)) || Pomp.Features.ProjectData.IsLocalFolder(entry)))continue;
                        if(folder)pending.Enqueue(entry);
                        if(folder!=(kind=="folder"))continue;
                        if(!folder && !Documents.Contains(Path.GetExtension(entry)))continue;
                        var relative=WorkspaceFileSearch.Relative(root,entry);
                        if(relative.IndexOf(query,StringComparison.OrdinalIgnoreCase)<0)continue;
                        matches.Add(new WorkspaceFileEntry{FullPath=entry,Path=folder?relative.TrimEnd('/')+"/":relative,Kind=kind});
                    }
                }
                catch(Exception ex)when(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException){unreadable=true;}
                if(limited)break;
            }
            var ranked=matches.OrderBy(m=>Path.GetFileName(m.FullPath).StartsWith(query,StringComparison.OrdinalIgnoreCase)?0:1).ThenBy(m=>m.Path.Length).ThenBy(m=>m.Path,StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
            return new WorkspaceFilesPayload{Files=ranked,Truncated=limited||matches.Count>limit,Error=unreadable?PompLanguage.Pick("일부 작업 영역 항목을 검사하지 못했어요.", "Some workspace entries could not be inspected."):null};
        }
    }
}
