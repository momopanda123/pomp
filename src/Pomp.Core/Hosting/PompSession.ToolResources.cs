using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>Saved workflows (<c>.pomp/workflows/*.json</c>) and project hook files (<c>.pomp/hooks/*.md</c>) edited from the Tools tab.</summary>
    public sealed partial class PompSession
    {
        private WorkflowsStatePayload _workflowsView = new WorkflowsStatePayload();
        private HooksStatePayload _hooksView = new HooksStatePayload();

        private WorkflowCatalog WorkflowFiles() { lock (_stateLock) return new WorkflowCatalog(_workingDirectory.Length == 0 ? Path.Combine(_environment.DataDirectory, "workspace") : _workingDirectory, _options.UserHome); }

        /// <summary>Hooks belong to a workspace: without one the list is empty and nothing can be written.</summary>
        private HookCatalog HookFiles() { lock (_stateLock) return new HookCatalog(_workingDirectory); }

        private WorkflowsStatePayload WorkflowsView() { lock (_stateLock) return _workflowsView; }

        private HooksStatePayload HooksView() { lock (_stateLock) return _hooksView; }

        private void PublishWorkflows() { lock (_resourcePublishLock) _ui.Post(UiMessageTypes.WorkflowsState, WorkflowsView(), UiDelivery.State); }

        private void PublishHooks() { lock (_resourcePublishLock) _ui.Post(UiMessageTypes.HooksState, HooksView(), UiDelivery.State); }

        private void RefreshWorkflows()
        {
            WorkflowsStatePayload view;
            try { view = WorkflowFiles().View(); }
            catch (Exception ex) { view = new WorkflowsStatePayload(); view.Diagnostics.Add(ResourceError(ex)); }
            lock (_stateLock) _workflowsView = view;
            PublishWorkflows();
        }

        /// <summary>Also after a project policy change: the list says whether hooks can run at all.</summary>
        private void RefreshHooks()
        {
            HooksStatePayload view;
            try { view = HookFiles().View(); }
            catch (Exception ex) { view = new HooksStatePayload(); view.Diagnostics.Add(ResourceError(ex)); }
            lock (_stateLock) _hooksView = view;
            PublishHooks();
        }

        /// <summary>Workflow files only: nothing here starts a workflow or sends a model request (Work › 진행 starts one).</summary>
        private async Task HandleWorkflowsAsync(JObject payload)
        {
            var id = payload.GetString("requestId") ?? ""; string? error = null; WorkflowInfoPayload? read = null;
            try
            {
                var command = new UiMessage("", payload).PayloadAs<WorkflowsCommandPayload>() ?? throw new ArgumentException(); string? open = null;
                // Reading one workflow for the editor changes nothing, so it stays allowed while a reply runs, like a refresh.
                if (command.Action != "refresh" && command.Action != "get" && ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
                if (command.Action == "create") WorkflowFiles().Create(command.Scope ?? "", command.Name ?? "", command.Workflow);
                else if (command.Action == "update") WorkflowFiles().Update(command.Scope ?? "", command.Name ?? "", command.ExpectedHash, command.Workflow);
                else if (command.Action == "delete") WorkflowFiles().Delete(command.Scope ?? "", command.Name ?? "", command.ExpectedHash);
                else if (command.Action == "open") open = WorkflowFiles().FileOf(command.Scope ?? "", command.Name ?? "");
                else if (command.Action == "get") read = WorkflowFiles().Get(command.Scope ?? "", command.Name ?? "");
                else if (command.Action != "refresh") throw new ArgumentException(PompLanguage.Pick("알 수 없는 워크플로 동작이에요.", "Unknown workflows action."));
                if (read == null) RefreshWorkflows(); // A get leaves the list as it is (and does not scan the folder again).
                if (open != null) await OpenResourceAsync(open, "open workflow", PompLanguage.Pick("워크플로 파일을 VS 편집기에서 열지 못했어요.", "Could not open the workflow in the VS editor.")).ConfigureAwait(false);
            }
            catch (Exception ex) { error = ResourceError(ex); }
            _ui.Post(UiMessageTypes.WorkflowsReply, error == null && read != null ? WorkflowReply(id, read) : new UiCommandReplyPayload { RequestId = id, Ok = error == null, Error = error }, UiDelivery.State);
        }

        /// <summary>The answer to a <c>get</c>: <c>{ requestId, ok: true, workflow: { name, scope, file, hash, description, task, steps, note? } }</c>.</summary>
        private static JObject WorkflowReply(string requestId, WorkflowInfoPayload workflow)
        {
            var body = new JObject { ["name"] = workflow.Name, ["scope"] = workflow.Scope, ["file"] = workflow.File, ["hash"] = workflow.Hash, ["description"] = workflow.Description,
                ["task"] = workflow.Task ?? "", ["steps"] = new JArray(workflow.Steps ?? new List<string>()) };
            if (workflow.Note != null) body["note"] = workflow.Note;
            return new JObject { ["requestId"] = requestId, ["ok"] = true, ["workflow"] = body };
        }

        /// <summary>Hook files only: running a hook still needs the conversation switch, a valid project policy and a one-time approval.</summary>
        private async Task HandleHooksAsync(JObject payload)
        {
            var id = payload.GetString("requestId") ?? ""; string? error = null;
            try
            {
                var command = new UiMessage("", payload).PayloadAs<HooksCommandPayload>() ?? throw new ArgumentException(); string? open = null;
                if (command.Action != "refresh" && ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
                if (command.Action == "create") HookFiles().Create(command.Name ?? "", command.Hook);
                else if (command.Action == "update") HookFiles().Update(command.Id ?? "", command.ExpectedHash, command.Hook);
                else if (command.Action == "setEnabled") HookFiles().SetEnabled(command.Id ?? "", command.ExpectedHash, command.Enabled ?? throw new ArgumentException(PompLanguage.Pick("켜기 또는 끄기를 고르지 않았어요.", "No on/off value was chosen.")));
                else if (command.Action == "delete") HookFiles().Delete(command.Id ?? "", command.ExpectedHash);
                else if (command.Action == "open") open = HookFiles().FileOf(command.Id ?? "");
                else if (command.Action != "refresh") throw new ArgumentException(PompLanguage.Pick("알 수 없는 훅 동작이에요.", "Unknown hooks action."));
                RefreshHooks();
                if (open != null) await OpenResourceAsync(open, "open hook", PompLanguage.Pick("훅 파일을 VS 편집기에서 열지 못했어요.", "Could not open the hook in the VS editor.")).ConfigureAwait(false);
            }
            catch (Exception ex) { error = ResourceError(ex); }
            _ui.Post(UiMessageTypes.HooksReply, new UiCommandReplyPayload { RequestId = id, Ok = error == null, Error = error }, UiDelivery.State);
        }

        private async Task OpenResourceAsync(string file, string what, string failure)
        {
            CheckpointStore.CheckDirectPath(file);
            if (!File.Exists(file)) throw new FileNotFoundException(PompLanguage.Pick("파일이 더 이상 없어요.", "The file no longer exists."));
            var opened = await AdapterGuard.TryRunAsync(token => _adapters.Editor.OpenDocumentAsync(file, null, token), _options.AdapterTimeout, _logger, what, _disposeCts.Token).ConfigureAwait(false);
            if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(failure);
        }
    }
}
