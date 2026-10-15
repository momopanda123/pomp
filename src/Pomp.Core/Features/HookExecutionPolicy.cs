using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
namespace Pomp.Features
{
    /// <summary>Conservative mirror of the project filter for host-initiated hooks; never a shell sandbox.</summary>
    internal static class HookExecutionPolicy
    {
        public static void Check(string root,string script,string command)
            => CheckPair(root, script, command, "run_project_hook", "run_terminal");
        public static void CheckInteractive(string root, string text, bool input)
            => CheckPair(root, text, text, "interactive_terminal", "interactive_terminal", input);
        private static void CheckPair(string root, string script, string command, string first, string second, bool input = false)
        {
            var policy=ProjectExecutionPolicy.Read(root);if(policy==null)throw new InvalidOperationException(ProjectExecutionPolicy.MissingPolicy);
            if((bool?)policy["requireSandbox"]==true)throw new InvalidOperationException(PompLanguage.Pick("정책이 요구하는 OS 샌드박스가 없어 실행하지 않았어요.", "The policy requires an OS sandbox, which is unavailable; nothing ran."));
            if((bool?)policy["requireReviewedEdits"]==true)throw new InvalidOperationException(PompLanguage.Pick("검토 전용 프로젝트라 실행하지 않았어요.", "Review-only project; nothing ran."));
            var rules=(JArray)policy["rules"]!;
            var allowHook=false;var allowTerminal=false;var denied=false;
            foreach(var item in rules)
            {
                if(!(item is JObject rule)||rule["tool"]?.Type!=JTokenType.String||!Regex.IsMatch((string)rule["tool"]!,@"\A(?:\*|[a-zA-Z0-9_.:-]{1,100})\z")||rule["effect"]?.Type!=JTokenType.String||!new[]{"allow","deny"}.Contains((string)rule["effect"]!)||rule.Properties().Any(p=>!new[]{"tool","effect","paths","commands"}.Contains(p.Name)))throw new InvalidOperationException(PompLanguage.Pick("프로젝트 정책 규칙이 올바르지 않아요.", "A project policy rule is invalid."));
                foreach(var key in new[]{"paths","commands"}) if(rule[key]!=null)
                {
                    if(!(rule[key] is JArray values)||values.Count==0||values.Count>30||values.Any(v=>v.Type!=JTokenType.String||v.ToString().Length==0||v.ToString().Length>4096||v.ToString().Contains('\0')))throw new InvalidOperationException(PompLanguage.Pick("정책 조건이 올바르지 않아요.", "A policy constraint is invalid."));
                    if(key=="paths"&&values.Any(v=>Path.IsPathRooted(v.ToString())||v.ToString().Replace('\\','/').Split('/').Contains("..")))throw new InvalidOperationException(PompLanguage.Pick("정책 경로가 올바르지 않아요.", "A policy path is invalid."));
                }
                var tool=(string)rule["tool"]!;if(tool!="*"&&tool!=first&&tool!=second)continue;
                var deny=(string)rule["effect"]! == "deny";
                // A denied command may be assembled across key chunks; never try to infer a safe partial input.
                if(input && deny){denied=true;continue;}
                // Arbitrary script effects cannot be inferred from the triggering file. A path allow cannot authorize them.
                if((rule["paths"]!=null || (input && rule["commands"]!=null))&&!deny)continue;
                var hookMatch=(tool=="*"||tool==first)&&(!(rule["commands"] is JArray hookCommands)||hookCommands.Values<string>().Contains(script,StringComparer.Ordinal));
                var terminalMatch=(tool=="*"||tool==second)&&(!(rule["commands"] is JArray terminalCommands)||(terminalCommands.Values<string>().Contains(command,StringComparer.Ordinal)||(deny&&terminalCommands.Values<string>().Contains(script,StringComparer.Ordinal))));
                if(deny&&(hookMatch||terminalMatch))denied=true;
                else if(!deny){allowHook|=hookMatch;allowTerminal|=terminalMatch;}
            }
            if(denied||!allowHook||!allowTerminal)throw new InvalidOperationException(PompLanguage.Pick("프로젝트 정책이 이 실행을 허용하지 않아요.", "The project policy does not allow this execution."));
        }
    }
}
