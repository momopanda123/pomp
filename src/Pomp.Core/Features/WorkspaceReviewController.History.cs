using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Features
{
    // The applied-changes history (checkpoints): listing, restore review and deletion.
    internal sealed partial class WorkspaceReviewController
    {
        private const int HistoryDeleteRecords = 1000, HistoryRestoreRecords = 20, HistoryRestoreFiles = 10;
        // The view the page last asked for (page, size, path query): every later push (after an apply, restore or delete) republishes it.
        // Listings run one at a time and read the view when they start, so an older read never posts after a newer one.
        private readonly object historyView = new object(), historyListing = new object();
        private int historyPage, historySize = CheckpointStore.DefaultPageSize;
        private string historyQuery = string.Empty;

        public async Task PublishHistoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                var workspaceRoot = await environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
                await Task.Run(() => {
                    lock (historyListing)
                    {
                        var (page, size, query) = HistoryView();
                        var listing = new CheckpointStore(environment.DataDirectory, workspaceRoot).ListPage(page, size, query);
                        lock (historyView) historyPage = listing.Page; // A page past the end settles on the last one.
                        ui.Post(HistoryStateMessage, HistoryState(workspaceRoot, listing.Entries, listing.Total, listing.Page, listing.Size, query, ""), UiDelivery.State);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { } // Cancelled (a closing session): the page keeps the list it has; an empty one would read as "no history".
            catch (Exception ex)
            {
                var (_, size, query) = HistoryView();
                ui.Post(HistoryStateMessage, HistoryState(null, new JArray(), 0, 0, size, query, ex.Message), UiDelivery.State);
            }
        }
        public async Task HandleHistoryAsync(JObject args, CancellationToken cancellationToken)
        {
            var action = (string?)args["action"];
            if (action != "restore" && action != "delete")
            {
                // A refresh names the whole view; whatever it leaves out is the default.
                lock (historyView)
                {
                    historyPage = IntArgument(args["page"], 0); historySize = CheckpointStore.ClampSize(IntArgument(args["size"], CheckpointStore.DefaultPageSize));
                    historyQuery = CheckpointStore.NormalizeQuery(args["query"]?.Type == JTokenType.String ? (string?)args["query"] : null);
                }
                await PublishHistoryAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            string error = string.Empty;
            string? proposalId = null;
            var deleted = 0;
            try
            {
                var workspaceRoot = await environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(workspaceRoot, (string?)args["root"], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "The workspace changed."));
                var history = new CheckpointStore(environment.DataDirectory, workspaceRoot);
                if (action == "delete")
                {
                    var ids = RequestedIds(args, HistoryDeleteRecords, PompLanguage.Pick("한 번에 지울 기록은 " + HistoryDeleteRecords + "개까지예요.", "Up to " + HistoryDeleteRecords + " records can be deleted at once."));
                    var removal = await Task.Run(() => history.DeleteMany(ids), cancellationToken).ConfigureAwait(false);
                    deleted = removal.Deleted; error = removal.Error ?? string.Empty;
                }
                else
                {
                    var ids = RequestedIds(args, HistoryRestoreRecords, PompLanguage.Pick("한 번에 되돌릴 기록은 " + HistoryRestoreRecords + "개까지예요.", "Up to " + HistoryRestoreRecords + " records can be restored at once."));
                    var files = await Task.Run(() => history.ReadApplied(ids, HistoryRestoreFiles), cancellationToken).ConfigureAwait(false);
                    var proposals = new JArray();
                    foreach (var file in files)
                    {
                        var captured = await adapters.CaptureForReviewAsync(workspaceRoot, file.Path, file.AfterHash, file.Before, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(captured.Before, file.After, StringComparison.Ordinal)) throw new InvalidOperationException(PompLanguage.Pick("이 체크포인트 이후 소스가 바뀌었어요: " + file.Path, "The source changed since this checkpoint: " + file.Path));
                        proposals.Add(new JObject { ["path"] = file.Path, ["beforeHash"] = file.AfterHash, ["after"] = file.Before });
                    }
                    var staged = await ProposeAsync(new JObject { ["files"] = proposals }, cancellationToken, workspaceRoot, files.ToDictionary(f => f.Path, f => f.After, StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);
                    if (staged.IsError) error = string.Join(" ", staged.Content.Select(c => c.Text));
                    else proposalId = (string?)staged.Details?["proposalId"];
                }
            }
            catch (Exception ex) { error = ex.Message; }
            await PublishHistoryAsync(cancellationToken).ConfigureAwait(false);
            if (error.Length > 0) ui.Post(HistoryStateMessage, new JObject { ["error"] = error }, UiDelivery.State);
            var reply = new JObject { ["requestId"] = (string?)args["requestId"], ["root"] = (string?)args["root"], ["proposalId"] = proposalId, ["ok"] = error.Length == 0 && (action == "delete" || proposalId != null), ["error"] = error.Length == 0 ? null : error };
            if (action == "delete") reply["deleted"] = deleted;
            ui.Post("workspace/historyReply", reply);
        }

        private (int Page, int Size, string Query) HistoryView() { lock (historyView) return (historyPage, historySize, historyQuery); }
        private static JObject HistoryState(string? workspaceRoot, JArray entries, int total, int page, int size, string query, string text)
        {
            var state = new JObject();
            if (workspaceRoot != null) state["root"] = workspaceRoot;
            state["entries"] = entries; state["total"] = total; state["page"] = page; state["size"] = size; state["query"] = query; state["message"] = text;
            return state;
        }
        /// <summary>A number the page sent (a missing or foreign value is the default), kept inside the int range.</summary>
        private static int IntArgument(JToken? token, int fallback)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return fallback;
            var value = token.Value<double>();
            return double.IsNaN(value) ? fallback : (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value));
        }
        /// <summary>The record ids of a delete or restore: the <c>ids</c> list, else the single <c>id</c>; at most <paramref name="limit"/>.</summary>
        private static List<string> RequestedIds(JObject args, int limit, string tooMany)
        {
            var ids = new List<string>();
            if (args["ids"] is JArray list && list.Count > 0)
            {
                if (list.Count > limit) throw new ArgumentException(tooMany);
                foreach (var item in list) ids.Add(item.Type == JTokenType.String ? (string)item! : string.Empty); // Anything else is refused as a bad id by the store.
            }
            else if (args["id"]?.Type == JTokenType.String) ids.Add((string)args["id"]!);
            if (ids.Count == 0) throw new ArgumentException(PompLanguage.Pick("선택한 기록이 없어요.", "No record is selected."));
            return ids;
        }
    }
}
