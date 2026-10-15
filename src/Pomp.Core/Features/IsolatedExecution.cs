using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Pomp.Features
{
    public sealed class IsolatedInput
    {
        public IsolatedInput(string path, string text) { Path = path; Text = text; }
        public string Path { get; }
        public string Text { get; }
    }
    public sealed class IsolatedRequest
    {
        public IsolatedRequest(string command, IReadOnlyList<IsolatedInput> files, int timeoutSeconds)
        { Command = command; Files = files; TimeoutSeconds = timeoutSeconds; Validate(); }
        public string Command { get; }
        public IReadOnlyList<IsolatedInput> Files { get; }
        public int TimeoutSeconds { get; }
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Command) || Command.Length > 8000 || Command.IndexOf('\0') >= 0 || TimeoutSeconds < 1 || TimeoutSeconds > 600 || Files.Count > 20 || Files.Sum(f => (long)f.Text.Length) > 2000000) throw new ArgumentException("Isolated request exceeds its command/file/time bounds.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                var path = Pomp.Internal.PortableRelativePath.Validate(file.Path);
                if (path.Length == 0 || path.Length > 1024 || !paths.Add(path) || file.Text.Length > 500000 || file.Text.IndexOf('\0') >= 0) throw new ArgumentException("Invalid or duplicate isolated input.");

            }
        }
    }
    public sealed class IsolatedResult
    {
        public int? ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
        public string CleanupWarning { get; set; } = "";
        public bool OutputTruncated { get; set; }
        public bool TerminationConfirmed { get; set; }
        public bool StartedInContainer { get; set; }
    }
    public interface IIsolatedProcessRunner
    {
        bool Available { get; }
        /// <summary>Caller obtains a one-use approval for this frozen command/input set before calling. Never falls back to ordinary execution.</summary>
        Task<IsolatedResult> RunAsync(IsolatedRequest request, CancellationToken token);
    }
    public interface IIsolatedExecutionSettings { bool EnableExperimentalIsolation { get; } }
    public interface IIsolatedExecutionAdapters { IIsolatedProcessRunner IsolatedRunner { get; } }
}
