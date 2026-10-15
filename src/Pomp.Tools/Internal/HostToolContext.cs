using System;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Hosting;

namespace Pomp.Tools
{
    /// <summary>Everything a tool needs from its surroundings: the adapters, the host environment, the snapshot store and the log.</summary>
    internal sealed class HostToolContext
    {
        public HostToolContext(IVsAdapterFactory adapters, IPompHostEnvironment environment, BufferSnapshotStore snapshots, IPompLogger? logger = null)
        {
            Adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            Snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            Logger = logger ?? NullPompLogger.Instance;
        }

        public IVsAdapterFactory Adapters { get; }

        public IPompHostEnvironment Environment { get; }

        public BufferSnapshotStore Snapshots { get; }

        /// <summary>Where unexpected adapter failures are recorded (the VS host forwards warnings to the ActivityLog).</summary>
        public IPompLogger Logger { get; }

        /// <summary>Guard for the current workspace root (queried per call: the root changes with the open solution).</summary>
        public async Task<PathGuard> GetPathGuardAsync(CancellationToken cancellationToken)
        {
            string? root;
            try
            {
                root = await Environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ToolException("The workspace root is not available: " + ToolErrors.ShortMessage(ex));
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ToolException("No workspace root is available (open a solution or folder in Visual Studio).");
            }

            try
            {
                return new PathGuard(root!);
            }
            catch (ArgumentException ex)
            {
                throw new ToolException("The workspace root is not usable: " + ex.Message);
            }
        }

        /// <summary>Like <see cref="GetPathGuardAsync"/> but returns null when no usable root exists (display falls back to absolute paths).</summary>
        public async Task<PathGuard?> TryGetPathGuardAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ToolException)
            {
                return null;
            }
        }

        /// <summary>Path formatter for model output; identity when there is no guard.</summary>
        public static Func<string, string> DisplayFunc(PathGuard? guard) => guard == null ? (Func<string, string>)(p => p) : guard.ToDisplayPath;
    }
}
