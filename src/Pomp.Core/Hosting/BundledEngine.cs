using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Pomp.Internal;

namespace Pomp.Hosting
{
    /// <summary>What the check of the omp engine bundled in the VSIX found.</summary>
    internal enum BundledEngineStatus
    {
        /// <summary>No <c>omp\engine.json</c>: a development package built without an engine (<c>-p:PompBundleOmp=false</c>).</summary>
        NotBundled,

        /// <summary><c>omp\omp.exe</c> has exactly the size and SHA-256 that the package's <c>omp\engine.json</c> pins.</summary>
        Verified,

        /// <summary>The manifest is there but the engine is missing, unreadable or different from what was packaged.</summary>
        Invalid,
    }

    internal sealed class BundledEngineCheck
    {
        public BundledEngineCheck(BundledEngineStatus status, string? path, string? version, string? problem)
        {
            Status = status;
            Path = path;
            Version = version;
            Problem = problem;
        }

        public BundledEngineStatus Status { get; }

        /// <summary><c>&lt;extension&gt;\omp\omp.exe</c> (also when it is missing).</summary>
        public string? Path { get; }

        /// <summary>The omp version the package pins (e.g. 18.8.6), from its manifest.</summary>
        public string? Version { get; }

        /// <summary>Why the engine is not usable (Invalid only).</summary>
        public string? Problem { get; }
    }

    /// <summary>
    /// The omp engine that a POMP release ships (docs/architecture.md "엔진 버전"): every VSIX carries <c>omp\omp.exe</c> together with
    /// <c>omp\engine.json</c> (<c>{ version, sha256, size, asset, source }</c>, written by the build from engines/omp/engine.props). The engine
    /// is used only when it is exactly the packaged file, so a POMP release and its omp are updated together and nothing else (an
    /// <c>omp update</c>, a different omp on PATH) changes the engine behind POMP's back.
    /// </summary>
    internal static class BundledEngine
    {
        internal const string ManifestName = "engine.json";
        private const long MaxManifestBytes = 16 * 1024;
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex VersionPattern = new Regex(@"^\d+(\.\d+){1,3}([-+][0-9A-Za-z.-]{1,40})?$", RegexOptions.CultureInvariant);

        // The hash of a 240 MB engine takes about a second: computed once per file version (path, size, last write time) per process.
        private static readonly ConcurrentDictionary<string, string> Hashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static BundledEngineCheck Inspect(string? extensionDirectory)
        {
            if (string.IsNullOrWhiteSpace(extensionDirectory)) return new BundledEngineCheck(BundledEngineStatus.NotBundled, null, null, null);
            string folder, manifestPath, enginePath;
            try
            {
                folder = System.IO.Path.Combine(System.IO.Path.GetFullPath(extensionDirectory!), "omp");
                manifestPath = System.IO.Path.Combine(folder, ManifestName);
                enginePath = System.IO.Path.Combine(folder, "omp.exe");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return new BundledEngineCheck(BundledEngineStatus.NotBundled, null, null, null);
            }

            if (!File.Exists(manifestPath)) return new BundledEngineCheck(BundledEngineStatus.NotBundled, enginePath, null, null);

            string? version = null;
            try
            {
                var info = new FileInfo(manifestPath);
                if (info.Length > MaxManifestBytes) return Invalid(enginePath, null, "engine.json is too large.");
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                version = manifest.GetString("version");
                var expectedHash = manifest.GetString("sha256")?.ToLowerInvariant();
                var expectedSize = manifest.GetLong("size");
                if (version == null || !VersionPattern.IsMatch(version) || expectedHash == null || !Sha256Pattern.IsMatch(expectedHash) || expectedSize == null || expectedSize <= 0)
                    return Invalid(enginePath, null, "engine.json is invalid.");

                var engine = new FileInfo(enginePath);
                if (!engine.Exists) return Invalid(enginePath, version, "omp.exe is missing.");
                if (engine.Length != expectedSize) return Invalid(enginePath, version, "omp.exe has " + engine.Length + " bytes instead of " + expectedSize + ".");
                var key = enginePath + "|" + engine.Length + "|" + engine.LastWriteTimeUtc.Ticks;
                var actualHash = Hashes.GetOrAdd(key, _ => Sha256(enginePath));
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                {
                    Hashes.TryRemove(key, out _);
                    return Invalid(enginePath, version, "omp.exe does not match the SHA-256 the package pins.");
                }

                return new BundledEngineCheck(BundledEngineStatus.Verified, enginePath, version, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is InvalidCastException)
            {
                return Invalid(enginePath, version, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static BundledEngineCheck Invalid(string enginePath, string? version, string problem) =>
            new BundledEngineCheck(BundledEngineStatus.Invalid, enginePath, version, problem);

        private static string Sha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var text = new char[hash.Length * 2];
                for (var i = 0; i < hash.Length; i++)
                {
                    text[i * 2] = "0123456789abcdef"[hash[i] >> 4];
                    text[i * 2 + 1] = "0123456789abcdef"[hash[i] & 15];
                }

                return new string(text);
            }
        }

        /// <summary>
        /// Why the omp that <paramref name="resolution"/> found must not be started, or null when it may: a package whose bundled engine is
        /// damaged or replaced starts no omp at all (not the chat engine, not the sign-in helper, not <c>omp usage</c>) unless the options
        /// name one — no silent fallback to whatever omp the PC has.
        /// </summary>
        public static string? Refusal(OmpExecutableResolution resolution, string? extensionDirectory, out BundledEngineCheck bundle)
        {
            bundle = Inspect(extensionDirectory);
            var overridden = resolution.Found && resolution.Source == "settings";
            if (bundle.Status != BundledEngineStatus.Invalid || overridden) return null;
            return PompLanguage.Pick(
                "POMP 설치 파일이 없거나 바뀌었어요.",
                "POMP's installed files are missing or were changed.");
        }

        /// <summary>The version an <c>omp --version</c> run printed ("omp/18.8.6" → "18.8.6"), or null.</summary>
        public static string? ParseVersionOutput(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            var match = Regex.Match(output, @"(?:^|[/\s v])(\d+\.\d+(?:\.\d+){0,2}(?:[-+][0-9A-Za-z.-]{1,40})?)", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
