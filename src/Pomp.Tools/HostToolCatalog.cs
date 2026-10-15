using System;
using System.Collections.Generic;
using System.Linq;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Hosting;

namespace Pomp.Tools
{
    /// <summary>
    /// Factory of the built-in host tools (<see cref="HostToolNames.All"/> order).
    /// Tools reach Visual Studio only through <see cref="IVsAdapterFactory"/>; the adapters are resolved lazily on
    /// each call, so creating the catalog never touches Visual Studio.
    /// </summary>
    public static class HostToolCatalog
    {
        /// <summary>
        /// Tools that are not ready to offer to omp (unimplemented or awaiting safety validation). <see cref="CreateDefault(IVsAdapterFactory, IPompHostEnvironment)"/>
        /// leaves them out, so omp is never offered a tool that can only fail; <see cref="CreateAll(IVsAdapterFactory, IPompHostEnvironment)"/>
        /// still builds them.
        /// </summary>
        public static readonly IReadOnlyCollection<string> NotImplemented = Array.AsReadOnly(new[]
        {
            // Permanently disabled: file deletion follows omp's own approval-gated delete tool.
            HostToolNames.RemoveProjectFile,
        });

        /// <summary>The tools offered to omp, with a fresh <see cref="BufferSnapshotStore"/>.</summary>
        public static IReadOnlyList<IHostTool> CreateDefault(IVsAdapterFactory adapters, IPompHostEnvironment environment)
        {
            return CreateDefault(adapters, environment, new BufferSnapshotStore(), NullPompLogger.Instance);
        }

        /// <summary>The tools offered to omp; unexpected adapter failures (COM errors...) are logged to <paramref name="logger"/>.</summary>
        public static IReadOnlyList<IHostTool> CreateDefault(IVsAdapterFactory adapters, IPompHostEnvironment environment, IPompLogger logger)
        {
            return CreateDefault(adapters, environment, new BufferSnapshotStore(), logger);
        }

        /// <summary>
        /// The tools offered to omp, sharing <paramref name="snapshots"/> (for example to clear it when the solution changes).
        /// </summary>
        public static IReadOnlyList<IHostTool> CreateDefault(IVsAdapterFactory adapters, IPompHostEnvironment environment, BufferSnapshotStore snapshots)
        {
            return CreateDefault(adapters, environment, snapshots, NullPompLogger.Instance);
        }

        /// <summary>The tools offered to omp: every built-in tool except <see cref="NotImplemented"/>.</summary>
        public static IReadOnlyList<IHostTool> CreateDefault(IVsAdapterFactory adapters, IPompHostEnvironment environment, BufferSnapshotStore snapshots, IPompLogger logger)
        {
            var tools = CreateAll(adapters, environment, snapshots, logger)
                .Where(tool => !NotImplemented.Contains(tool.Definition.Name))
                .ToArray();
            return Array.AsReadOnly(tools);
        }

        /// <summary>Every built-in tool, including the ones in <see cref="NotImplemented"/>, with a fresh <see cref="BufferSnapshotStore"/>.</summary>
        public static IReadOnlyList<IHostTool> CreateAll(IVsAdapterFactory adapters, IPompHostEnvironment environment)
        {
            return CreateAll(adapters, environment, new BufferSnapshotStore(), NullPompLogger.Instance);
        }

        /// <summary>Every built-in tool, including the ones in <see cref="NotImplemented"/>, sharing <paramref name="snapshots"/>.</summary>
        public static IReadOnlyList<IHostTool> CreateAll(IVsAdapterFactory adapters, IPompHostEnvironment environment, BufferSnapshotStore snapshots)
        {
            return CreateAll(adapters, environment, snapshots, NullPompLogger.Instance);
        }

        /// <summary>Every built-in tool (one per <see cref="HostToolNames.All"/> entry, in that order).</summary>
        public static IReadOnlyList<IHostTool> CreateAll(IVsAdapterFactory adapters, IPompHostEnvironment environment, BufferSnapshotStore snapshots, IPompLogger logger)
        {
            var context = new HostToolContext(adapters, environment, snapshots, logger);
            var tools = new IHostTool[]
            {
                new GetEditorContextTool(context),
                new ReadBufferTool(context),
                new EditBufferHashlineTool(context),
                new QuerySolutionTool(context),
                new AddProjectFileTool(context),
                new RemoveProjectFileTool(context),
                new RenameProjectFileTool(context),
                new GetMsBuildPropertiesTool(context),
                new ReadErrorListTool(context),
                new ReadOutputWindowTool(context),
                new BuildSolutionTool(context),
                new ManageNugetTool(context),
                new ManageTestsTool(context),
                new ManageGitTool(context),
                new ListBreakpointsTool(context),
                new ManageBreakpointsTool(context),
                new GetDebugStateTool(context),
                new ControlDebuggerTool(context),
                new ListBookmarksTool(context),
                new AddBookmarkTool(context),
            };
            return Array.AsReadOnly(tools);
        }
    }
}
