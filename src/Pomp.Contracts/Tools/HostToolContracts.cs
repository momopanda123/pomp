using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>
    /// Wire shape of one entry of <c>set_host_tools</c> (omp <c>RpcHostToolDefinition</c>).
    /// Serialized with <see cref="Json.PompJson"/> (camelCase, nulls omitted).
    /// </summary>
    public sealed class HostToolDefinition
    {
        public string Name { get; set; } = string.Empty;

        public string? Label { get; set; }

        public string Description { get; set; } = string.Empty;

        /// <summary>JSON Schema of the arguments object.</summary>
        public JObject Parameters { get; set; } = new JObject { ["type"] = "object" };

        public bool? Hidden { get; set; }

        /// <summary><c>essential</c> (always in the prompt) or <c>discoverable</c> (omp default for host tools).</summary>
        public string? LoadMode { get; set; }
    }

    /// <summary>One content block of a host tool result (only text for now).</summary>
    public sealed class HostToolContent
    {
        public string Type { get; set; } = "text";

        public string? Text { get; set; }

        public static HostToolContent FromText(string text) => new HostToolContent { Type = "text", Text = text };
    }

    /// <summary>Result returned to omp through <c>host_tool_result</c>.</summary>
    public sealed class HostToolResult
    {
        public HostToolResult(IReadOnlyList<HostToolContent> content, bool isError = false, JObject? details = null)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            IsError = isError;
            Details = details;
        }

        public IReadOnlyList<HostToolContent> Content { get; }

        /// <summary>When true omp surfaces the text as a tool error.</summary>
        public bool IsError { get; }

        /// <summary>Optional structured data for renderers; not shown to the model.</summary>
        public JObject? Details { get; }

        public static HostToolResult Text(string text, JObject? details = null) =>
            new HostToolResult(new[] { HostToolContent.FromText(text) }, false, details);

        public static HostToolResult Error(string message) =>
            new HostToolResult(new[] { HostToolContent.FromText(message) }, true);
    }

    /// <summary>A single execution request for a host tool.</summary>
    public sealed class HostToolInvocation
    {
        public HostToolInvocation(string hostCallId, string toolCallId, string toolName, JObject arguments, IProgress<string>? progress = null, object? origin = null)
        {
            HostCallId = hostCallId;
            ToolCallId = toolCallId;
            ToolName = toolName;
            Arguments = arguments ?? new JObject();
            Progress = progress;
            Origin = origin;
        }

        /// <summary>Host transport identity, never populated from model arguments or wire JSON.</summary>
        public object? Origin { get; }

        /// <summary>Id of the <c>host_tool_call</c> frame (used for update/result/cancel).</summary>
        public string HostCallId { get; }

        /// <summary>Model-side tool call id.</summary>
        public string ToolCallId { get; }

        public string ToolName { get; }

        public JObject Arguments { get; }

        /// <summary>Streams partial output back to omp as <c>host_tool_update</c>.</summary>
        public IProgress<string>? Progress { get; }
    }

    /// <summary>
    /// A Visual Studio capability exposed to omp as a host tool (design: CustomToolRegistry entries).
    /// Tools are VS-agnostic: they talk to Visual Studio only through <c>Pomp.Adapters</c> interfaces.
    /// </summary>
    public interface IHostTool
    {
        HostToolDefinition Definition { get; }

        /// <summary>
        /// Approval category for this particular call (argument dependent, e.g. manage_nuget list vs install).
        /// <see cref="PermissionKind.None"/> means the call runs without asking the user.
        /// </summary>
        PermissionKind ResolvePermission(HostToolInvocation invocation);

        /// <summary>
        /// Builds the content of the approval dialog (target, impact, diff preview). Only called when
        /// <see cref="ResolvePermission"/> is not <see cref="PermissionKind.None"/>. May return null to use a generic prompt.
        /// </summary>
        Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken);

        /// <summary>Runs the tool. Expected failures are returned as <see cref="HostToolResult.Error"/>, not thrown.</summary>
        Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken cancellationToken);
    }

    /// <summary>A mutation tool whose validated preview and execution share one per-call frozen plan.</summary>
    public interface IPreparedHostTool : IHostTool
    {
        /// <summary>Read-only preparation of mutation calls (non-None permission), performed even for remembered grants. Failure must prevent execution. Read-only actions use ExecuteAsync directly.</summary>
        Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Owns a prepared mutation until approval and execution finish. Dispose is called on every outcome.
    /// Implementations must keep execution inputs private and immutable, independent of the permission DTO.
    /// </summary>
    public interface IPreparedHostToolCall : IDisposable
    {
        PermissionRequest Permission { get; }

        /// <summary>Executes only the prepared plan, revalidating external state before mutation.</summary>
        Task<HostToolResult> ExecuteAsync(CancellationToken cancellationToken);
    }

    /// <summary>Catalogue of host tools offered to omp during the handshake.</summary>
    public interface ICustomToolRegistry
    {
        void Register(IHostTool tool);

        IReadOnlyList<HostToolDefinition> GetDefinitions();

        bool TryGet(string toolName, out IHostTool tool);
    }

    /// <summary>
    /// Names of the host tools (design: CustomToolRegistry table plus read_buffer, get_editor_context
    /// and add_project_file from the data-flow sequences, and the IDE feature map: project file removal/renaming, MSBuild
    /// properties, the Output window, Git, tests, breakpoint changes, debugger control and bookmarks). Also used as keys of
    /// the omp approval overlay.
    /// </summary>
    public static class HostToolNames
    {
        public const string GetEditorContext = "get_editor_context";
        public const string ReadBuffer = "read_buffer";
        public const string EditBufferHashline = "edit_buffer_hashline";
        public const string QuerySolution = "query_solution";
        public const string AddProjectFile = "add_project_file";
        public const string RemoveProjectFile = "remove_project_file";
        public const string RenameProjectFile = "rename_project_file";
        public const string GetMsBuildProperties = "get_msbuild_properties";
        public const string ReadErrorList = "read_error_list";
        public const string ReadOutputWindow = "read_output_window";
        public const string BuildSolution = "build_solution";
        public const string ManageNuget = "manage_nuget";
        public const string ManageTests = "manage_tests";
        public const string ManageGit = "manage_git";
        public const string ListBreakpoints = "list_breakpoints";
        public const string ManageBreakpoints = "manage_breakpoints";
        public const string GetDebugState = "get_debug_state";
        public const string ControlDebugger = "control_debugger";
        public const string ListBookmarks = "list_bookmarks";
        public const string AddBookmark = "add_bookmark";

        public static readonly IReadOnlyList<string> All = new[]
        {
            GetEditorContext,
            ReadBuffer,
            EditBufferHashline,
            QuerySolution,
            AddProjectFile,
            RemoveProjectFile,
            RenameProjectFile,
            GetMsBuildProperties,
            ReadErrorList,
            ReadOutputWindow,
            BuildSolution,
            ManageNuget,
            ManageTests,
            ManageGit,
            ListBreakpoints,
            ManageBreakpoints,
            GetDebugState,
            ControlDebugger,
            ListBookmarks,
            AddBookmark,
        };
    }
}
