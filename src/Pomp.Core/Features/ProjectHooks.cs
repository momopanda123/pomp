using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
namespace Pomp.Features
{
    public sealed class ProjectEvent : EventArgs
    {
        public ProjectEvent(string name, string root, string path = "", string detail = "", bool? succeeded = null) { Name = name; Root = root; Path = path; Detail = detail; Succeeded = succeeded; }
        public string Name { get; }
        public string Root { get; }
        public string Path { get; }
        public string Detail { get; }
        /// <summary>A build's outcome when the IDE reports one; null for saves, a Clean or an unknown outcome.</summary>
        public bool? Succeeded { get; }
    }
    public interface IProjectEventSource { event EventHandler<ProjectEvent>? Occurred; }
    public interface IProjectEventAdapters { IProjectEventSource ProjectEvents { get; } }
    public sealed class ProjectHook
    {
        public string Name { get; private set; } = "";
        public string Description { get; private set; } = "";
        public string File { get; private set; } = "";
        public string Hash { get; private set; } = "";
        public string Event { get; private set; } = "";
        public string Glob { get; private set; } = "**";
        public string Script { get; private set; } = "";
        public int TimeoutSeconds { get; private set; } = 120;
        public bool Enabled { get; private set; }
        /// <summary>Restricted scalar YAML frontmatter. No aliases, nested values, tags or executable config.</summary>
        public static ProjectHook Parse(string file, string text)
        {
            if (text.Length > 16000) throw new ArgumentException(PompLanguage.Pick("훅 파일이 너무 커요.", "The hook file is too large."));
            var normalized = text.TrimStart('\uFEFF').Replace("\r\n","\n");
            var match = Regex.Match(normalized, @"\A---\n([\s\S]*?)\n---(?:\n|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success) throw new ArgumentException(PompLanguage.Pick("훅 프런트매터가 올바르지 않아요.", "The hook frontmatter is invalid."));
            var values = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var line in match.Groups[1].Value.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#",StringComparison.Ordinal)) continue;
                var colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0])) throw new ArgumentException(PompLanguage.Pick("훅 필드가 올바르지 않아요.", "The hook field is invalid."));
                var key = line.Substring(0,colon).Trim(); var raw = line.Substring(colon+1).Trim();
                if (!new[]{"name","event","enabled","glob","timeoutSeconds","description"}.Contains(key) || values.ContainsKey(key)) throw new ArgumentException(PompLanguage.Pick("알 수 없거나 중복된 훅 필드예요: " + key, "Unknown or duplicate hook field: " + key));
                string value;
                if (raw.StartsWith("\"",StringComparison.Ordinal)) { var scalar = JToken.Parse(raw); if (scalar.Type != JTokenType.String) throw new ArgumentException(PompLanguage.Pick("훅 스칼라 값이 올바르지 않아요.", "Invalid hook scalar value.")); value = scalar.ToString(); }
                else if (raw.StartsWith("'",StringComparison.Ordinal)) { if (raw.Length<2 || !Regex.IsMatch(raw,@"\A'(?:[^']|'')*'\z",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))) throw new ArgumentException(PompLanguage.Pick("따옴표로 감싼 스칼라 값이 닫히지 않았어요.", "Unclosed quoted scalar.")); value=raw.Substring(1,raw.Length-2).Replace("''", "'"); }
                else { if (raw.Length==0 || "*&!|>[{".Contains(raw[0])) throw new ArgumentException(PompLanguage.Pick("훅 스칼라 값이 올바르지 않아요.", "Invalid hook scalar value.")); value=raw; }
                if (value.Length>500 || value.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("훅 스칼라 값이 올바르지 않아요.", "Invalid hook scalar value."));
                values.Add(key,value);
            }
            string Get(string key,string fallback) => values.TryGetValue(key,out var value) ? value : fallback;
            var hook = new ProjectHook { Name=Get("name",System.IO.Path.GetFileNameWithoutExtension(file)),Description=Get("description",""),File=file,Hash=ContentHash.Compute(text),Event=Get("event",""),Glob=Get("glob","**") };
            if (!Regex.IsMatch(hook.Name,@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z")) throw new ArgumentException(PompLanguage.Pick("훅 이름이 올바르지 않아요.", "Invalid hook name."));
            if (hook.Event!="onFileSave" && hook.Event!="onBuild") throw new ArgumentException(PompLanguage.Pick("훅 이벤트가 올바르지 않아요.", "The hook event is invalid."));
            var enabled=Get("enabled","false"); if(enabled!="true"&&enabled!="false") throw new ArgumentException(PompLanguage.Pick("enabled가 올바르지 않아요.", "enabled is invalid."));hook.Enabled=enabled=="true";
            if(!int.TryParse(Get("timeoutSeconds","120"),out var timeout)||timeout<1||timeout>3600) throw new ArgumentException(PompLanguage.Pick("timeoutSeconds가 올바르지 않아요.", "timeoutSeconds is invalid."));hook.TimeoutSeconds=timeout;
            Pattern(hook.Glob);
            var body=normalized.Substring(match.Length).Trim();
            if(body.StartsWith("```",StringComparison.Ordinal)) {
                var first=body.IndexOf('\n'); if(first<0||!new[]{"```","```powershell","```pwsh"}.Contains(body.Substring(0,first))||!body.EndsWith("\n```",StringComparison.Ordinal))throw new ArgumentException(PompLanguage.Pick("훅 본문이 올바르지 않아요.", "The hook body is invalid."));
                body=body.Substring(first+1,body.Length-first-5).Trim();
            }
            if(string.IsNullOrWhiteSpace(body)||body.Length>6000||body.Contains('\0'))throw new ArgumentException(PompLanguage.Pick("훅 스크립트가 올바르지 않아요.", "The hook script is invalid."));hook.Script=body;
            return hook;
        }
        internal static Regex Pattern(string glob)
        {
            if(glob.Length==0||glob.Length>300||System.IO.Path.IsPathRooted(glob)||glob.Contains(':')||glob.IndexOfAny(new[]{'[',']','{','}','(',')',','})>=0||glob.Replace('\\','/').Split('/').Contains(".."))throw new ArgumentException(PompLanguage.Pick("glob이 올바르지 않아요.", "The glob is invalid."));
            var pattern=glob.Replace('\\','/');var output=new StringBuilder("\\A");
            for(int i=0;i<pattern.Length;i++) {
                if(pattern[i]=='*'&&i+1<pattern.Length&&pattern[i+1]=='*') {i++;if(i+1<pattern.Length&&pattern[i+1]=='/'){i++;output.Append("(?:.*/)?");}else output.Append(".*");}
                else if(pattern[i]=='*')output.Append("[^/]*");else if(pattern[i]=='?')output.Append("[^/]");else output.Append(Regex.Escape(pattern[i].ToString()));
            }
            return new Regex(output.Append("\\z").ToString(),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        }
        public bool Matches(ProjectEvent value,string relativePath) => Enabled&&Event==value.Name&&(Event=="onBuild"||Pattern(Glob).IsMatch(relativePath.Replace('\\','/')));
        public string Command(ProjectEvent value)
        {
            string Quote(string text) {if(text.Length>1024||text.Contains('\0')||text.Contains('\r')||text.Contains('\n'))throw new ArgumentException(PompLanguage.Pick("훅 이벤트 데이터가 올바르지 않아요.", "Invalid hook event data."));return "'"+text.Replace("'","''")+"'";}
            var command="$POMP_FILE = "+Quote(value.Path)+"; $POMP_EVENT = "+Quote(value.Name)+"; $POMP_EVENT_DETAIL = "+Quote(value.Detail)+";\n"+Script;
            if(command.Length>8000)throw new ArgumentException(PompLanguage.Pick("훅 명령이 너무 길어요.", "The hook command is too long."));return command;
        }
        public static IReadOnlyList<ProjectHook> Load(string root,IList<string> diagnostics,CancellationToken token)
        {
            var dir=System.IO.Path.Combine(root,".pomp","hooks");CheckpointStore.CheckDirectPath(dir);if(!Directory.Exists(dir))return Array.Empty<ProjectHook>();
            var result=new List<ProjectHook>();var files=Directory.EnumerateFiles(dir,"*.md",SearchOption.TopDirectoryOnly).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var file in files) {token.ThrowIfCancellationRequested();try {CheckpointStore.CheckDirectPath(file);using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(stream.Length>64000)throw new ArgumentException(PompLanguage.Pick("훅 파일이 너무 커요.", "The hook file is too large."));using var reader=new StreamReader(stream,new UTF8Encoding(false,true),true);var hook=Parse(file,reader.ReadToEnd());CheckpointStore.CheckDirectPath(file);result.Add(hook);}catch(Exception ex)when(!(ex is OperationCanceledException)){diagnostics.Add(System.IO.Path.GetFileName(file)+": "+(ex.Message.Length>500?ex.Message.Substring(0,500):ex.Message));}}
            return result.AsReadOnly();
        }
    }
}
