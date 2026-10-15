using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Tools;
using Pomp.Ui;
namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private CancellationTokenSource? _contextSuggestions;
        private async Task SuggestContextAsync(JObject args)
        {
            var id = args["requestId"]?.Type == JTokenType.String ? (string?)args["requestId"] : null;
            if (string.IsNullOrEmpty(id) || id!.Length > 128) return;
            using var search = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            Cancel(Interlocked.Exchange(ref _contextSuggestions, search));
            search.CancelAfter(TimeSpan.FromSeconds(15));
            var answer = new JObject { ["requestId"] = id, ["files"] = new JArray() };
            try
            {
                if (_codeSearch == null) throw new InvalidOperationException(PompLanguage.Pick("현재 코드 검색을 사용할 수 없어요.", "Live code search is unavailable."));
                var query = args["query"]?.Type == JTokenType.String ? (string?)args["query"] : null;
                if (string.IsNullOrWhiteSpace(query) || query!.Length > 2000) throw new ArgumentException(PompLanguage.Pick("초안 검색어가 올바르지 않아요.", "The draft query is invalid."));
                var root = await _environment.GetWorkspaceRootAsync(search.Token).ConfigureAwait(false);
                if (args["root"]?.Type != JTokenType.String || !string.Equals(root, (string?)args["root"], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
                var excluded = args["excludePaths"] as JArray ?? new JArray();
                if (excluded.Any(p => p.Type != JTokenType.String || ((string)p!).Length > 32768)) throw new ArgumentException(PompLanguage.Pick("첨부 경로가 올바르지 않아요.", "The attached paths are invalid."));
                var seen = new System.Collections.Generic.HashSet<string>(excluded.Select(p => ((string)p!).Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
                var result = await _codeSearch.ExecuteAsync(new HostToolInvocation("suggest-" + id, "context", "search_codebase", new JObject { ["query"] = query, ["limit"] = 20 }), search.Token).ConfigureAwait(false);
                if (result.IsError) throw new InvalidOperationException(string.Join(" ", result.Content.Select(c => c.Text)));
                if (!string.Equals(root, await _environment.GetWorkspaceRootAsync(search.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("검색 중에 작업 영역이 바뀌었어요.", "Workspace changed during search."));
                var hits = result.Details?["hits"] as JArray ?? new JArray();
                answer["root"] = root;
                answer["mode"] = result.Details?["mode"];
                answer["notices"] = result.Details?["notices"];
                answer["notice"] = result.Details?["embeddingQueryNotice"];
                answer["files"] = new JArray(hits.OfType<JObject>().Where(hit => !string.IsNullOrWhiteSpace((string?)hit["path"]) && seen.Add(((string)hit["path"]!).Replace('/', '\\'))).Take(5).Select(hit => new JObject {
                    ["fullPath"] = hit["path"], ["path"] = WorkspaceFileSearch.Relative(root, (string)hit["path"]!),
                    ["kind"] = "code", ["symbol"] = hit["symbol"], ["line"] = hit["startLine"] }));
            }
            catch (OperationCanceledException) { answer["error"] = PompLanguage.Pick("컨텍스트 검색이 취소되었거나 시간이 초과됐어요.", "Context search cancelled or timed out."); }
            catch (Exception ex) { answer["error"] = ex.Message.Length > 3000 ? ex.Message.Substring(0, 3000) : ex.Message; }
            finally { Interlocked.CompareExchange(ref _contextSuggestions, null, search); }
            if (!_disposeCts.IsCancellationRequested) _ui.Post("workspace/contextSuggestions", answer);
        }
    }
}
