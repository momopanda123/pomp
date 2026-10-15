using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pomp.Permissions;
using Pomp.Internal;
namespace Pomp.Features
{
    /// <summary>
    /// Optional project tool policy (<c>.pomp/permissions.json</c>): when present it is default-deny and only narrows what the
    /// normal approvals allow; when absent nothing extra is blocked. Never an OS isolation guarantee.
    /// </summary>
    internal static class ProjectExecutionPolicy
    {
        /// <summary>Something exists at the policy path (a broken or unreadable policy still counts: it must fail closed, not be ignored).</summary>
        internal static bool Exists(string root)
        {
            if(string.IsNullOrWhiteSpace(root))return false;
            try
            {
                var file=Path.Combine(root,".pomp","permissions.json");
                return File.Exists(file)||Directory.Exists(file);
            }
            catch(Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return true;
            }
        }

        internal static JObject? Read(string root)
        {
            if(string.IsNullOrWhiteSpace(root))return null;
            var file=Path.Combine(root,".pomp","permissions.json");CheckpointStore.CheckDirectPath(file);
            if(!File.Exists(file))
            {
                if(Directory.Exists(file))throw new InvalidOperationException("Project permission policy is not a file.");
                return null;
            }
            if(new FileInfo(file).Length>32768)throw new InvalidOperationException("Project permission policy is too large.");
            string text;
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var bytes=new byte[32769];int total=0,read;
                while(total<bytes.Length&&(read=stream.Read(bytes,total,bytes.Length-total))>0)total+=read;
                if(total>32768)throw new InvalidOperationException("Project permission policy is too large.");
                text=new System.Text.UTF8Encoding(false,true).GetString(bytes,0,total).TrimStart('\uFEFF');
            }
            var policy=JObject.Parse(text);
            Validate(policy);
            return policy;
        }
        /// <summary>The checks enforcement applies to a parsed policy; the Tools tab editor runs the same ones before it writes the file.</summary>
        internal static void Validate(JObject policy)
        {
            if(policy["version"]?.Type!=JTokenType.Integer||(int?)policy["version"]!=1||(string?)policy["default"]!="deny"||!(policy["rules"] is JArray rules)||policy.Properties().Any(p=>!new[]{"version","default","rules","requireSandbox","requireReviewedEdits"}.Contains(p.Name)))throw new InvalidOperationException("Invalid project permission policy.");
            foreach(var flag in new[]{"requireSandbox","requireReviewedEdits"})if(policy[flag]!=null&&policy[flag]!.Type!=JTokenType.Boolean)throw new InvalidOperationException("Invalid project restriction flag.");
            foreach(var item in rules)
            {
                if(item is not JObject rule || rule["tool"]?.Type!=JTokenType.String || !Regex.IsMatch((string)rule["tool"]!, @"\A(?:\*|[a-zA-Z0-9_.:-]{1,100})\z") || rule["effect"]?.Type!=JTokenType.String || !new[]{"allow","deny"}.Contains((string)rule["effect"]!) || rule.Properties().Any(p=>!new[]{"tool","effect","paths","commands"}.Contains(p.Name))) throw new InvalidOperationException("Invalid project policy rule.");
                foreach(var key in new[]{"paths","commands"}) if(rule[key]!=null)
                {
                    if(rule[key] is not JArray values || values.Count==0 || values.Any(v=>v.Type!=JTokenType.String || v.ToString().Length==0 || v.ToString().Length>4096 || v.ToString().Contains('\0'))) throw new InvalidOperationException("Invalid project policy constraint.");
                    if(key=="paths" && values.Any(v=>Path.IsPathRooted(v.ToString()) || Regex.IsMatch(v.ToString(), @"\A[A-Za-z]:") || v.ToString().StartsWith("\\",StringComparison.Ordinal) || v.ToString().Replace('\\','/').Split('/').Contains(".."))) throw new InvalidOperationException("Policy path is invalid.");
                    if(key=="paths")foreach(var value in values)ValidatePolicyPath(value.ToString());
                }
            }
        }
        // Without .pomp/permissions.json the normal approval model applies (reads run, changes and execution ask first): installing the
        // extension must be enough. A policy file only narrows that; project hooks alone require one (HookExecutionPolicy).
        internal const string MissingPolicy = "No .pomp/permissions.json is configured.";
        public static string? CheckTool(string root, string tool, JObject args)
        {
            try
            {
                var policy=Read(root); if(policy==null)return null;
                if((bool?)policy["requireSandbox"]==true)return "Required OS sandbox has not been verified; project tools are blocked.";
                var cwd=args["cwd"]; if(cwd==null || cwd.Type==JTokenType.Null)cwd=args["workdir"];
                if(cwd!=null && cwd.Type!=JTokenType.Null && (cwd.Type!=JTokenType.String || !SamePath(Path.GetFullPath(root),Path.GetFullPath(Path.Combine(root,(string)cwd!)))))return "Tool-specific working-directory override is not allowed by the project filter.";
                var allowed=false;
                foreach(var rule in ((JArray)policy["rules"]!).Cast<JObject>())
                {
                    if((string?)rule["tool"]!="*" && (string?)rule["tool"]!=tool)continue;
                    var deny=(string?)rule["effect"]=="deny";
                    if(rule["paths"] is JArray paths && !PathsMatch(root,paths,args,deny))continue;
                    if(rule["commands"] is JArray commands && (args["command"]?.Type!=JTokenType.String || !commands.Values<string>().Contains((string)args["command"]!,StringComparer.Ordinal)))continue;
                    if(deny)return "Denied by a matching .pomp/permissions.json rule.";
                    allowed=true;
                }
                return allowed ? null : "Denied by .pomp/permissions.json: no matching allow rule.";
            }
            catch(Exception ex){return "Project tool policy cannot be established: "+ex.Message;}
        }
        public static void RequireTool(string root,string tool,JObject args)
        {
            var error=CheckTool(root,tool,args);if(error!=null)throw new InvalidOperationException(error);
        }
        private static void ValidatePolicyPath(string value)
        {
            if(value.Contains('~'))throw new InvalidOperationException("Policy path is invalid.");
            var normalized=string.Join("/",value.Replace('\\','/').Split('/').Where(part=>part.Length>0 && part!="."));
            if(normalized.Length>0)PortableRelativePath.Validate(normalized,4096);
        }
        private static bool SamePath(string a,string b)=>string.Equals(a.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),b.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
        private static bool ContainsPath(string folder,string target)
        {
            var prefix=folder.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            return SamePath(folder,target)||target.StartsWith(prefix,Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
        }
        private static bool PathsMatch(string root,JArray folders,JObject args,bool deny)
        {
            var targets=new List<string>();
            foreach(var key in new[]{"path","file","file_path","filePath","source","destination","oldPath","newPath"})if(args[key]?.Type==JTokenType.String)targets.Add((string)args[key]!);
            if(args["files"] is JArray files)foreach(var file in files.Take(101)) { if(file.Type==JTokenType.String)targets.Add((string)file!);else if(file is JObject item&&item["path"]?.Type==JTokenType.String)targets.Add((string)item["path"]!); }
            if(args["paths"] is JArray paths)foreach(var value in paths.Take(101))if(value.Type==JTokenType.String)targets.Add((string)value!);
            if(targets.Count==0||targets.Count>100)return deny;
            root=Path.GetFullPath(root);
            bool Match(string value)
            {
                try
                {
                    if(value.Length>4096||value.Contains('\0'))return deny;
                    if(Regex.IsMatch(value,@"\A[A-Za-z]:(?![/\\])"))return deny;
                    var full=Path.GetFullPath(Path.Combine(root,value));if(!ContainsPath(root,full))return deny;
                    var relative=full.Substring(root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                    if(relative.Length>0)ValidatePolicyPath(relative);
                    CheckpointStore.CheckDirectPath(full);
                    return folders.Values<string>().Any(folder=>ContainsPath(Path.GetFullPath(Path.Combine(root,folder!)),full));
                }
                catch{return deny;}
            }
            return deny?targets.Any(Match):targets.All(Match);
        }
        public static string? CheckHostPermission(string root,PermissionKind kind)
        {
            if(kind==PermissionKind.None)return null;
            try
            {
                var policy=Read(root);
                if(policy==null)return null;
                if((bool?)policy?["requireSandbox"]==true)return "Project requires OS isolation, which has not been accepted as safe. This mutation/execution is blocked.";
                if((bool?)policy?["requireReviewedEdits"]==true)return "Project requires review-only source editing. Use propose_workspace_edits and the explicit shadow-review apply UI. Direct mutations, build/test/shell execution and generators are blocked; ordinary approval cannot bypass this restriction.";
                return null;
            }
            catch(Exception ex){return "Project policy cannot be established: "+ex.Message;}
        }
        public static void ThrowIfIsolationRequired(string root)
        {
            var error=CheckHostPermission(root,PermissionKind.ProcessExec);
            if(error!=null)throw new InvalidOperationException(error);
        }
    }
}
