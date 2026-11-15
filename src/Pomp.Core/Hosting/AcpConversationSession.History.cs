using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.AgentClients;
using Pomp.Ui;

namespace Pomp.Hosting
{
    internal sealed partial class AcpConversationSession
    {
        private readonly HashSet<string> _issuedHistoryIds = new HashSet<string>(StringComparer.Ordinal);
        private JArray _historyEntries = new JArray();

        public ConversationTabsStatePayload ConversationTabs
        {
            get
            {
                lock (_sync) return new ConversationTabsStatePayload
                {
                    Root = _root, SessionId = PublicId(_agentSessionId), Title = _title, Backend = Backend,
                    Partner = new ConversationPartnerPayload(),
                };
            }
        }
        public ConversationPlanPayload? ConversationPlan
        {
            get
            {
                lock (_sync)
                {
                    if (_agentSessionId == null) return null;
                    return new ConversationPlanPayload
                    {
                        SessionId = PublicId(_agentSessionId)!, Backend = Backend, Title = _title, Partner = new ConversationPartnerPayload(),
                        Ready = _ready && !_frozen && !_disposed, IsStreaming = _turn != null,
                        PlanTaskCount = _plan.Count, PlanOpenCount = _planOpen, PlanRevision = _planRevision,
                        Editable = false, CanContinue = false, CanRun = false, CanStop = true,
                    };
                }
            }
        }

        internal string ResolveHistoryId(JObject args)
        {
            lock (_sync)
            {
                if (_disposed || _frozen || !SameRoot((string?)args["root"] ?? "", _root) ||
                    !_issuedHistoryIds.Contains((string?)args["id"] ?? ""))
                    throw new InvalidOperationException(PompLanguage.Pick("다른 작업 영역의 기록이거나 현재 목록에 없는 대화예요. 기록을 새로 고쳐 주세요.",
                        "This conversation was not issued for this workspace. Refresh history first."));
                return (string)args["id"]!;
            }
        }

        public async Task RunHistoryCommandAsync(JObject args, Func<string, string, bool>? ownedElsewhere = null)
        {
            var request = (string?)args["requestId"];
            string? error = null;
            try
            {
                var action = (string?)args["action"] ?? "refresh";
                if (action != "refresh") throw new NotSupportedException(PompLanguage.Pick(
                    "이 ACP 연결은 기록 조회·복원·닫기만 지원해요. 이름 변경·압축·삭제는 지원하지 않아요.",
                    "This ACP connection supports listing, restoring and closing conversations, not rename, compaction or deletion."));
                AcpConnection connection; string root; long generation;
                lock (_sync)
                {
                    ThrowIfDisposed();
                    if (_connection == null || !_initialized || !HasSessionCapability("list")) throw new NotSupportedException(PompLanguage.Pick("ACP 대화 기록을 쓸 수 없어요.", "ACP session history is unavailable."));
                    connection = _connection; root = _root; generation = _generation;
                }
                var entries = new JArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var cursors = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                do
                {
                    var parameters = new JObject { ["cwd"] = root };
                    if (cursor != null) parameters["cursor"] = cursor;
                    var result = await MetadataAsync(connection, "session/list", parameters, _lifetime.Token).ConfigureAwait(false);
                    if (result["sessions"] is not JArray sessions) throw new InvalidOperationException(PompLanguage.Pick("ACP가 올바르지 않은 대화 기록을 보냈어요.", "ACP returned an invalid history page."));
                    foreach (var session in sessions.OfType<JObject>())
                    {
                        var id = (string?)session["sessionId"];
                        if (string.IsNullOrWhiteSpace(id) || id!.Length > 2000 || id.Any(char.IsControl) ||
                            !SameRoot((string?)session["cwd"] ?? "", root) || !seen.Add(id)) continue;
                        var publicId = PublicId(id)!;
                        entries.Add(new JObject
                        {
                            ["id"] = publicId, ["sessionId"] = publicId, ["backend"] = Backend,
                            ["title"] = (string?)session["title"] ?? "Antigravity ACP", ["name"] = session["title"]?.DeepClone(),
                            ["modifiedUtc"] = session["updatedAt"]?.DeepClone(), ["current"] = id == _agentSessionId,
                            ["partner"] = new JObject { ["kind"] = "main" },
                        });
                    }
                    cursor = (string?)result["nextCursor"];
                    if (cursor != null && (!cursors.Add(cursor) || entries.Count > 4000))
                        throw new InvalidOperationException(PompLanguage.Pick("ACP 대화 기록이 표시 한도를 넘거나 같은 페이지를 반복해서 보냈어요.", "ACP history exceeds the display limit or returned a repeated page cursor."));
                } while (cursor != null);
                lock (_sync)
                {
                    RequireConnection(connection, generation);
                    _historyEntries = entries;
                    _issuedHistoryIds.Clear();
                    foreach (var entry in entries.OfType<JObject>()) _issuedHistoryIds.Add((string)entry["id"]!);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            JObject response;
            lock (_sync) response = new JObject
            {
                ["requestId"] = request, ["root"] = _root, ["entries"] = _historyEntries.DeepClone(), ["error"] = error,
                ["current"] = _agentSessionId == null ? null : new JObject { ["sessionId"] = PublicId(_agentSessionId), ["name"] = _title },
            };
            _ui.Post(UiMessageTypes.SessionHistoryState, response, UiDelivery.State);
        }

        public async Task RunPlanCommandAsync(UiMessage message, Action<UiMessage> respond)
        {
            var args = message.Payload ?? new JObject();
            var type = message.Type == UiMessageTypes.AgentPlanPage ? UiMessageTypes.AgentPlanPageResult : UiMessageTypes.AgentPlanResult;
            try
            {
                lock (_sync)
                {
                    if (_disposed || _frozen || !SameRoot((string?)args["root"] ?? "", _root) || (string?)args["sessionId"] != PublicId(_agentSessionId) || _agentSessionId == null)
                        throw new InvalidOperationException(PompLanguage.Pick("이 작업 계획은 다른 대화나 작업 영역의 것이에요.", "The plan belongs to another conversation or workspace."));
                }
                if (message.Type == UiMessageTypes.AgentPlanPage)
                {
                    JObject page;
                    lock (_sync)
                    {
                        var size = (int)Math.Max(1, Math.Min(200, (long?)args["size"] ?? 50));
                        var query = ((string?)args["query"] ?? "").Trim();
                        if (query.Length > 300) query = query.Substring(0, 300);
                        var rows = _plan.OfType<JObject>().Select((entry, index) => new { Entry = entry, Index = index })
                            .Where(row => query.Length == 0 || ((string?)row.Entry["content"] ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                "Antigravity".IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        var number = (int)Math.Max(0, Math.Min(rows.Count == 0 ? 0 : (rows.Count - 1) / size, (long?)args["page"] ?? 0));
                        page = new JObject
                        {
                            ["requestId"] = (string?)args["requestId"], ["ok"] = true, ["revision"] = _planRevision,
                            ["editable"] = false, ["editableWhileWorking"] = false,
                            ["page"] = number, ["size"] = size, ["total"] = rows.Count, ["completedCount"] = _plan.Count - _planOpen,
                            ["phases"] = _plan.Count == 0 ? new JArray() : new JArray(new JObject { ["index"] = 0, ["name"] = "Antigravity", ["taskCount"] = _plan.Count }),
                            ["rows"] = new JArray(rows.Skip(number * size).Take(size).Select(row => new JObject
                            {
                                ["phase"] = 0, ["task"] = row.Index, ["content"] = (string?)row.Entry["content"] ?? "", ["status"] = (string?)row.Entry["status"] ?? "pending",
                            })),
                        };
                    }
                    respond(new UiMessage(type, page)); return;
                }
                if (message.Type != UiMessageTypes.AgentPlanAction || (string?)args["action"] != "stop")
                    throw new NotSupportedException(PompLanguage.Pick("ACP 계획은 읽기 전용이에요. 계획 변경은 대화로 요청해 주세요.", "ACP plans are read-only. Request plan changes in the conversation."));
                await StopAsync().ConfigureAwait(false);
                respond(new UiMessage(type, new UiCommandReplyPayload { RequestId = (string?)args["requestId"] ?? "", Ok = true }));
            }
            catch (Exception ex)
            {
                respond(new UiMessage(type, new UiCommandReplyPayload { RequestId = (string?)args["requestId"] ?? "", Ok = false, Error = ex.Message }));
            }
        }
    }
}
