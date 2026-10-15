using System;
using System.Collections.Generic;
using System.IO;

namespace Pomp.Hosting
{
    /// <summary>Outcome of <see cref="OmpExecutableResolver.Resolve"/>.</summary>
    public sealed class OmpExecutableResolution
    {
        public OmpExecutableResolution(string? path, string? source, IReadOnlyList<string> searched, string? configuredPathProblem)
        {
            Path = path;
            Source = source;
            Searched = searched;
            ConfiguredPathProblem = configuredPathProblem;
        }

        /// <summary>Full path of the omp executable, or null when none was found.</summary>
        public string? Path { get; }

        /// <summary>Where it came from: <c>settings</c>, <c>extension</c>, <c>PATH</c> or <c>well-known</c>.</summary>
        public string? Source { get; }

        /// <summary>Every location that was checked, in order.</summary>
        public IReadOnlyList<string> Searched { get; }

        /// <summary>Set when a path was configured in the options but does not exist (resolution then continued with the other sources).</summary>
        public string? ConfiguredPathProblem { get; }

        public bool Found => Path != null;
    }

    /// <summary>
    /// Locates the omp executable: the path from the options page, then the copy bundled in the VSIX
    /// (<c>&lt;extension&gt;\omp\omp.exe</c>), then PATH (<c>omp.exe</c>, <c>omp.cmd</c>, <c>omp.bat</c>), then well-known install
    /// locations (<c>%LOCALAPPDATA%\omp\omp.exe</c>, <c>%USERPROFILE%\.bun\bin\omp.exe</c>, <c>%APPDATA%\npm\omp.cmd</c>).
    /// </summary>
    public sealed class OmpExecutableResolver
    {
        private static readonly string[] ExecutableNames = { "omp.exe", "omp.cmd", "omp.bat" };

        private readonly Func<string, bool> _fileExists;
        private readonly Func<string, string?> _getEnvironmentVariable;

        public OmpExecutableResolver()
            : this(null, null)
        {
        }

        /// <param name="fileExists">File existence probe (tests inject one).</param>
        /// <param name="getEnvironmentVariable">Environment lookup (tests inject one).</param>
        public OmpExecutableResolver(Func<string, bool>? fileExists, Func<string, string?>? getEnvironmentVariable)
        {
            _fileExists = fileExists ?? File.Exists;
            _getEnvironmentVariable = getEnvironmentVariable ?? (name => Environment.GetEnvironmentVariable(name));
        }

        /// <param name="configuredPath">The explicit path from the options page (may be null/empty).</param>
        /// <param name="extensionDirectory">Install folder of the extension (for the bundled copy).</param>
        public OmpExecutableResolution Resolve(string? configuredPath, string? extensionDirectory)
        {
            var searched = new List<string>();
            string? problem = null;

            var configured = Normalize(configuredPath);
            if (configured.Length > 0)
            {
                var found = ProbeConfigured(configured, searched);
                if (found != null)
                {
                    return new OmpExecutableResolution(found, "settings", searched, null);
                }

                problem = PompLanguage.Pick("지정한 실행 파일 경로가 없어요: " + configured, "The configured executable path does not exist: " + configured);
            }

            if (!string.IsNullOrWhiteSpace(extensionDirectory))
            {
                var bundled = SafeCombine(extensionDirectory!, "omp", "omp.exe");
                if (bundled != null)
                {
                    searched.Add(bundled);
                    if (_fileExists(bundled))
                    {
                        return new OmpExecutableResolution(bundled, "extension", searched, problem);
                    }
                }
            }

            var pathVariable = _getEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (var rawDirectory in pathVariable!.Split(';'))
                {
                    var directory = Normalize(rawDirectory);
                    if (directory.Length == 0)
                    {
                        continue;
                    }

                    foreach (var name in ExecutableNames)
                    {
                        var candidate = SafeCombine(directory, name);
                        if (candidate == null)
                        {
                            continue;
                        }

                        searched.Add(candidate);
                        if (_fileExists(candidate))
                        {
                            return new OmpExecutableResolution(candidate, "PATH", searched, problem);
                        }
                    }
                }
            }

            foreach (var candidate in WellKnownLocations())
            {
                searched.Add(candidate);
                if (_fileExists(candidate))
                {
                    return new OmpExecutableResolution(candidate, "well-known", searched, problem);
                }
            }

            return new OmpExecutableResolution(null, null, searched, problem);
        }

        /// <summary>The message shown when nothing was found.</summary>
        public static string DescribeNotFound(OmpExecutableResolution resolution)
        {
            var message = PompLanguage.Pick("POMP 에이전트 실행 파일을 찾지 못했어요.", "The POMP agent executable was not found.");
            if (resolution.ConfiguredPathProblem != null)
            {
                message = resolution.ConfiguredPathProblem + " " + message;
            }

            return message;
        }

        private string? ProbeConfigured(string configured, List<string> searched)
        {
            var expanded = SafeExpand(configured);
            searched.Add(expanded);
            if (_fileExists(expanded))
            {
                return expanded;
            }

            // A folder, or a name without extension.
            var candidates = new List<string>();
            if (string.IsNullOrEmpty(Path.GetExtension(expanded)))
            {
                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    candidates.Add(expanded + extension);
                }
            }

            foreach (var name in ExecutableNames)
            {
                var inFolder = SafeCombine(expanded, name);
                if (inFolder != null)
                {
                    candidates.Add(inFolder);
                }
            }

            foreach (var candidate in candidates)
            {
                searched.Add(candidate);
                if (_fileExists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private IEnumerable<string> WellKnownLocations()
        {
            var localAppData = _getEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var path = SafeCombine(localAppData!, "omp", "omp.exe");
                if (path != null)
                {
                    yield return path;
                }
            }

            var userProfile = _getEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                var path = SafeCombine(userProfile!, ".bun", "bin", "omp.exe");
                if (path != null)
                {
                    yield return path;
                }
            }

            var appData = _getEnvironmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(appData))
            {
                var path = SafeCombine(appData!, "npm", "omp.cmd");
                if (path != null)
                {
                    yield return path;
                }
            }
        }

        private static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var trimmed = path!.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            }

            return trimmed;
        }

        private static string SafeExpand(string path)
        {
            try
            {
                return Environment.ExpandEnvironmentVariables(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static string? SafeCombine(params string[] parts)
        {
            try
            {
                return Path.Combine(parts);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
