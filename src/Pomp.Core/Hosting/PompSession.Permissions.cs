using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        /// <summary>
        /// A starter project policy: default deny with one allow-all rule. Normal approvals still apply; having a policy also blocks
        /// tool working directories outside the workspace and lets project hooks run (HookExecutionPolicy). The user narrows it in the editor.
        /// </summary>
        internal const string ProjectPolicyTemplate = "{\n  \"version\": 1,\n  \"default\": \"deny\",\n  \"requireReviewedEdits\": false,\n  \"requireSandbox\": false,\n  \"rules\": [\n    { \"tool\": \"*\", \"effect\": \"allow\" }\n  ]\n}\n";

        /// <summary>The project policy as the host enforces it (read-only): none, valid with its rules, or invalid (which blocks tools).</summary>
        private JObject ProjectPolicyView(string root)
        {
            var view = new JObject { ["file"] = root.Length == 0 ? "" : Path.Combine(root, ".pomp", "permissions.json") };
            // Any existing file, valid or not, gets the hash the editor's save or delete must name.
            try { if (root.Length > 0 && File.Exists((string)view["file"]!)) view["hash"] = ToolResourceFiles.Hash(ToolResourceFiles.Read((string)view["file"]!, PolicyFileLimit)); }
            catch (Exception ex) { _logger.Debug("Could not hash the project policy: " + ex.Message); }
            try
            {
                var policy = root.Length == 0 ? null : ProjectExecutionPolicy.Read(root);
                if (policy == null) { view["status"] = ProjectExecutionPolicy.Exists(root) ? "invalid" : "none"; return view; }
                view["status"] = "valid";
                view["requireSandbox"] = (bool?)policy["requireSandbox"] == true;
                view["requireReviewedEdits"] = (bool?)policy["requireReviewedEdits"] == true;
                view["rules"] = new JArray(((JArray)policy["rules"]!).OfType<JObject>().Select(rule => new JObject
                {
                    ["tool"] = rule["tool"], ["effect"] = rule["effect"], ["paths"] = rule["paths"]?.DeepClone(), ["commands"] = rule["commands"]?.DeepClone(),
                }));
            }
            catch (Exception ex) { view["status"] = "invalid"; view["error"] = TextUtil.Truncate(ex.Message, 500); }
            return view;
        }

        /// <summary>The largest policy file the editor reads to hash; enforcement itself accepts at most 32 KiB.</summary>
        private const int PolicyFileLimit = 1024 * 1024;

        /// <summary>
        /// Writes the policy from the Tools tab editor with <c>version: 1</c> and <c>default: "deny"</c>, after the checks enforcement
        /// applies. A null <paramref name="expectedHash"/> creates the file (never over an existing one); otherwise the file must still
        /// have that hash. The page is the only caller: no model tool reaches this.
        /// </summary>
        private static void SaveProjectPolicy(string root, JObject? draft, string? expectedHash)
        {
            if (root.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 열려 있지 않아요.", "No workspace is open."));
            if (draft == null || draft["rules"] is not JArray rules) throw new ArgumentException(PompLanguage.Pick("정책이 비어 있어요.", "The policy is empty."));
            var policy = new JObject
            {
                ["version"] = 1, ["default"] = "deny",
                ["requireReviewedEdits"] = draft["requireReviewedEdits"]?.Type == JTokenType.Boolean && (bool)draft["requireReviewedEdits"]!,
                ["requireSandbox"] = draft["requireSandbox"]?.Type == JTokenType.Boolean && (bool)draft["requireSandbox"]!,
                ["rules"] = new JArray(rules.Select((rule, index) => PolicyRule(rule, index + 1))),
            };
            ProjectExecutionPolicy.Validate(policy);
            var text = policy.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
            var bytes = new UTF8Encoding(false).GetBytes(text);
            if (bytes.Length > 32768) throw new ArgumentException(PompLanguage.Pick("정책 파일이 너무 커요.", "The policy file is too large."));
            var file = Path.Combine(root, ".pomp", "permissions.json");
            lock (ToolResourceFiles.Gate)
            {
                CheckpointStore.CheckDirectPath(file);
                if (expectedHash == null) { ToolResourceFiles.WriteNew(file, bytes); return; }
                if (!File.Exists(file) || ToolResourceFiles.Hash(ToolResourceFiles.Read(file, PolicyFileLimit)) != expectedHash) throw new InvalidOperationException(PolicyChanged());
                AtomicFile.WriteAllText(file, text);
            }
        }

        /// <summary>One editor row as enforcement expects it; empty path/command lists are left out (absent means any).</summary>
        private static JObject PolicyRule(JToken token, int number)
        {
            string Bad(string ko, string en) => PompLanguage.Pick(number + "번 규칙: " + ko, "Rule " + number + ": " + en);
            if (token is not JObject row) throw new ArgumentException(Bad("형식이 올바르지 않아요.", "the row is invalid."));
            var tool = ((string?)row["tool"] ?? "").Trim();
            if (!Regex.IsMatch(tool, @"\A(?:\*|[a-zA-Z0-9_.:-]{1,100})\z")) throw new ArgumentException(Bad("도구 이름이 올바르지 않아요.", "the tool name is invalid."));
            var effect = (string?)row["effect"];
            if (effect != "allow" && effect != "deny") throw new ArgumentException(Bad("효과가 올바르지 않아요.", "the effect is invalid."));
            var rule = new JObject { ["tool"] = tool, ["effect"] = effect };
            foreach (var key in new[] { "paths", "commands" })
            {
                if (row[key] == null || row[key]!.Type == JTokenType.Null) continue;
                if (row[key] is not JArray list || list.Any(v => v.Type != JTokenType.String)) throw new ArgumentException(Bad("경로·명령 형식이 올바르지 않아요.", "the paths or commands have an invalid format."));
                var values = list.Select(v => ((string)v!).Trim()).Where(v => v.Length > 0).ToList();
                if (values.Any(v => v.Length > 4096 || v.IndexOf('\0') >= 0)) throw new ArgumentException(Bad("경로·명령이 너무 길어요.", "a path or command is too long."));
                if (key == "paths" && values.Any(v => Path.IsPathRooted(v) || Regex.IsMatch(v, @"\A[A-Za-z]:") || v.StartsWith("\\", StringComparison.Ordinal) || v.Replace('\\', '/').Split('/').Contains("..") || v.IndexOf('~') >= 0))
                    throw new ArgumentException(Bad("경로가 올바르지 않아요.", "a path is invalid."));
                if (values.Count > 0) rule[key] = new JArray(values);
            }
            return rule;
        }

        private static void DeleteProjectPolicy(string root, string? expectedHash)
        {
            if (root.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 열려 있지 않아요.", "No workspace is open."));
            var file = Path.Combine(root, ".pomp", "permissions.json");
            lock (ToolResourceFiles.Gate)
            {
                CheckpointStore.CheckDirectPath(file);
                if (expectedHash == null || !File.Exists(file) || ToolResourceFiles.Hash(ToolResourceFiles.Read(file, PolicyFileLimit)) != expectedHash) throw new InvalidOperationException(PolicyChanged());
                File.Delete(file);
            }
        }

        private static string PolicyChanged() => PompLanguage.Pick("정책 파일이 그 사이 바뀌었어요.", "The policy file changed meanwhile.");

        /// <summary>Opens the policy in the editor; creates the starter file first only when asked and none exists (never overwrites).</summary>
        private async Task OpenProjectPolicyAsync(string root, bool create)
        {
            if (root.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 열려 있지 않아요.", "No workspace is open."));
            var file = Path.Combine(root, ".pomp", "permissions.json");
            CheckpointStore.CheckDirectPath(file);
            if (!File.Exists(file))
            {
                if (!create) throw new FileNotFoundException(PompLanguage.Pick("프로젝트 정책 파일이 없어요.", "There is no project policy file."));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                CheckpointStore.CheckDirectPath(file);
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new System.Text.UTF8Encoding(false).GetBytes(ProjectPolicyTemplate);
                    stream.Write(bytes, 0, bytes.Length);
                }
                _logger.Info("Created a starter project permission policy at " + file + ".");
            }
            var opened = await AdapterGuard.TryRunAsync(token => _adapters.Editor.OpenDocumentAsync(file, null, token), _options.AdapterTimeout, _logger, "open project policy", _disposeCts.Token).ConfigureAwait(false);
            if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(PompLanguage.Pick("정책 파일을 VS 편집기에서 열지 못했어요.", "Could not open the policy in the VS editor."));
        }

        private async Task HandlePermissionRulesAsync(JObject args)
        {
            var reply = new JObject { ["requestId"] = args.GetString("requestId") };
            try
            {
                var action = args.GetString("action") ?? "refresh";
                if (action == "openPolicy" || action == "createPolicy")
                {
                    string root;
                    lock (_stateLock)
                    {
                        if (args.GetString("sessionId") != (_info.SessionId ?? "") || args.GetString("root") != _workingDirectory)
                            throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
                        root = _workingDirectory;
                    }
                    await OpenProjectPolicyAsync(root, action == "createPolicy").ConfigureAwait(false);
                }
                else if (action == "savePolicy" || action == "deletePolicy")
                {
                    string root;
                    lock (_stateLock)
                    {
                        if (args.GetString("sessionId") != (_info.SessionId ?? "") || args.GetString("root") != _workingDirectory)
                            throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
                        root = _workingDirectory;
                    }
                    var expected = args["expectedHash"]?.Type == JTokenType.String ? (string?)args["expectedHash"] : null;
                    if (action == "savePolicy") SaveProjectPolicy(root, args["policy"] as JObject, expected);
                    else DeleteProjectPolicy(root, expected);
                    _logger.Info((action == "savePolicy" ? "Saved" : "Deleted") + " the project permission policy from the Tools tab.");
                }
                else if (action == "revoke")
                {
                    var scope = args.GetString("scope");
                    if (scope != "session" && scope != "persistent") throw new ArgumentException(PompLanguage.Pick("알 수 없는 허용 범위예요.", "Unknown permission scope."));
                    lock (_stateLock)
                    {
                        if (args.GetString("sessionId") != (_info.SessionId ?? "") || args.GetString("root") != _workingDirectory)
                            throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
                    }
                    // Outside the state lock: a persistent revoke writes the IDE options, which may wait for the IDE's main thread.
                    _gate.Revoke(args.GetString("key") ?? "", scope == "persistent");
                }
                else if (action == "untrust")
                {
                    RevokeWorkspaceTrust(args.GetString("workspace") ?? "");
                }
                else if (action != "refresh") throw new ArgumentException(PompLanguage.Pick("알 수 없는 권한 관리 동작이에요.", "Unknown permission management action."));
            }
            catch (Exception ex) { reply["error"] = ex.Message; }
            // The hook list says whether hooks can run, which depends on the policy file.
            if (args.GetString("action") is "savePolicy" or "deletePolicy" or "createPolicy") RefreshHooks();
            string current;
            lock (_stateLock) { reply["root"] = current = _workingDirectory; reply["sessionId"] = _info.SessionId ?? ""; }
            reply["projectPolicy"] = ProjectPolicyView(current);
            reply["session"] = new JArray(); reply["persistent"] = new JArray();
            try
            {
                reply["session"] = new JArray(_gate.SessionGrants);
                reply["persistent"] = new JArray(_settings.AlwaysAllowedPolicyKeys.OrderBy(key => key, StringComparer.Ordinal));
                reply["trustedWorkspaces"] = TrustedWorkspacesView();
                reply["approvalMode"] = _settings.ApprovalMode.ToString();
                reply["discoveredExtensions"] = _settings.LoadDiscoveredOmpExtensions;
                reply["blockNetworkTools"] = _settings.BlockOmpNetworkTools;
            }
            catch (Exception ex) { reply["error"] = ex.Message; }
            _ui.Post("permission/rulesState", reply, UiDelivery.State);
        }

        /// <summary>
        /// The composer's permission-mode chip (<c>permission/mode</c>): the mode is a launch setting, so the host's settings writer
        /// saves it and raises Changed, after which omp is restarted and the conversation restored (a new host/hello carries the mode).
        /// Refused while a reply is in progress: the restart would cut it off.
        /// </summary>
        /// <summary>The approval mode the running engine was started with; null before the first start.</summary>
        private OmpApprovalMode? _launchedApprovalMode;
        private DateTime _approvalModeChangedUtc;

        /// <summary>
        /// True while a permission-mode change made from the chat window waits for the engine restart that applies it (the host restarts
        /// omp after a short debounce; the engine's own approval flag is stale until then). Bounded to 15 s so a missed restart cannot
        /// hold the conversation.
        /// </summary>
        private bool ApprovalRestartPending()
        {
            lock (_stateLock)
                return _launchedApprovalMode.HasValue && _launchedApprovalMode.Value != _settings.ApprovalMode && DateTime.UtcNow - _approvalModeChangedUtc < TimeSpan.FromSeconds(15);
        }

        private async Task HandlePermissionModeAsync(JObject args)
        {
            var requestId = args.GetString("requestId") ?? string.Empty;
            var mode = _settings.ApprovalMode;
            string? error = null;
            try
            {
                if (!OmpApprovalModeExtensions.TryParseUiValue(args.GetString("mode"), out var wanted))
                    throw new InvalidOperationException(PompLanguage.Pick("권한 모드 값이 올바르지 않아요.", "The permission mode is invalid."));
                if (!(_settings is IApprovalModeWriter writer))
                    throw new InvalidOperationException(PompLanguage.Pick("권한 모드를 바꿀 수 없어요.", "The permission mode cannot be changed."));
                bool busy;
                lock (_stateLock) busy = _info.IsStreaming || _info.HasPendingAsyncWork;
                if (wanted != mode && (busy || _ui.PendingRequestCount > 0))
                    throw new InvalidOperationException(PompLanguage.Pick("응답이 진행 중이에요.", "A reply is in progress."));
                if (wanted != mode)
                {
                    await writer.SetApprovalModeAsync(wanted, _disposeCts.Token).ConfigureAwait(false);
                    lock (_stateLock) _approvalModeChangedUtc = DateTime.UtcNow;
                    _logger.Info("Approval mode set to " + wanted + " from the chat window (" + mode + " before).");
                }
                mode = wanted;
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.Warn("Permission mode change refused: " + ex.Message);
            }

            _ui.Post(UiMessageTypes.PermissionModeResult, new PermissionModeResultPayload { RequestId = requestId, Ok = error == null, Mode = mode, Error = error }, UiDelivery.State);
        }
    }
}
