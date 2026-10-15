using System;
using System.IO;
using System.Security;

namespace Pomp.Tools
{
    /// <summary>
    /// Confines path arguments of host tools to the workspace root. Relative paths resolve against the root; every
    /// path is normalized with <see cref="Path.GetFullPath(string)"/> and rejected unless the result is the root
    /// itself or lies below it (".." escapes, other drives, UNC and device paths, drive-relative forms).
    /// Comparison is case-insensitive on Windows. Existing reparse points below the selected root are rejected
    /// rather than followed. The selected root is trusted; this preflight check is not a race-free filesystem sandbox.
    /// </summary>
    public sealed class PathGuard
    {
        private readonly bool _windows;
        private readonly StringComparison _comparison;
        private readonly string _rootPrefix;

        /// <summary>Creates a guard for <paramref name="workspaceRoot"/> (case-insensitive on Windows).</summary>
        public PathGuard(string workspaceRoot)
            : this(workspaceRoot, Path.DirectorySeparatorChar == '\\')
        {
        }

        public PathGuard(string workspaceRoot, bool ignoreCase)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                throw new ArgumentException("The workspace root must not be empty.", nameof(workspaceRoot));
            }

            _windows = Path.DirectorySeparatorChar == '\\';
            _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            var candidate = workspaceRoot.Trim();
            if (!IsFullyQualified(candidate))
            {
                throw new ArgumentException("The workspace root must be an absolute path: " + candidate, nameof(workspaceRoot));
            }

            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                throw new ArgumentException("The workspace root is not a valid path: " + ex.Message, nameof(workspaceRoot), ex);
            }

            Root = TrimTrailingSeparators(full);
            _rootPrefix = EndsWithSeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        }

        /// <summary>Normalized workspace root without a trailing separator (except for drive roots such as "C:\").</summary>
        public string Root { get; }

        /// <summary>
        /// Resolves <paramref name="path"/> (absolute or relative to <see cref="Root"/>) to a normalized full path.
        /// Returns false with a model-facing <paramref name="error"/> when it is invalid or outside the workspace.
        /// </summary>
        public bool TryResolve(string? path, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;

            if (path == null || path.Trim().Length == 0)
            {
                error = "The path is empty.";
                return false;
            }

            var candidate = path.Trim();
            if (!TryValidateSyntax(candidate, out error))
            {
                return false;
            }

            var combined = IsFullyQualified(candidate) ? candidate : Path.Combine(Root, candidate);
            string full;
            try
            {
                full = Path.GetFullPath(combined);
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                error = "'" + candidate + "' is not a valid path: " + ShortReason(ex);
                return false;
            }

            if (!IsInsideRoot(full))
            {
                error = "Path '" + candidate + "' is outside the workspace root (" + Root + "). Only paths inside the workspace can be accessed; use a path relative to the workspace root.";
                return false;
            }

            if (!CheckReparsePoints(full, out error))
            {
                return false;
            }

            fullPath = full;
            return true;
        }

        private bool CheckReparsePoints(string fullPath, out string error)
        {
            error = string.Empty;
            var relative = fullPath.Length > _rootPrefix.Length ? fullPath.Substring(_rootPrefix.Length) : string.Empty;
            var current = Root;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "Path traverses a symbolic link, junction or other reparse point: '" + current + "'. Use a direct path inside the workspace instead.";
                        return false;
                    }
                }
                catch (FileNotFoundException)
                {
                    // New files are valid targets; no later component can exist below a missing parent.
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (Exception ex) when (IsPathException(ex) || ex is UnauthorizedAccessException)
                {
                    error = "Cannot verify path '" + current + "': " + ShortReason(ex);
                    return false;
                }
            }

            return true;
        }

        /// <summary>Like <see cref="TryResolve"/> but throws <see cref="ArgumentException"/> with the error message.</summary>
        public string Resolve(string? path)
        {
            if (!TryResolve(path, out var fullPath, out var error))
            {
                throw new ArgumentException(error, nameof(path));
            }

            return fullPath;
        }

        /// <summary>True when the normalized full path is the root or below it.</summary>
        public bool IsInsideRoot(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return false;
            }

            var trimmed = TrimTrailingSeparators(fullPath);
            if (string.Equals(trimmed, Root, _comparison))
            {
                return true;
            }

            return fullPath.StartsWith(_rootPrefix, _comparison);
        }

        /// <summary>
        /// Display form of a path for model output: relative to the root with forward slashes ("." for the root itself).
        /// Paths outside the root are returned unchanged; relative inputs only get their separators normalized.
        /// Never throws.
        /// </summary>
        public string ToDisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path ?? string.Empty;
            }

            if (!IsFullyQualified(path))
            {
                return path.Replace('\\', '/');
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                return path;
            }

            if (!IsInsideRoot(full))
            {
                return path;
            }

            var trimmed = TrimTrailingSeparators(full);
            if (trimmed.Length <= Root.Length)
            {
                return ".";
            }

            return trimmed.Substring(_rootPrefix.Length).Replace('\\', '/');
        }

        private bool TryValidateSyntax(string candidate, out string error)
        {
            error = string.Empty;
            if (candidate.IndexOf('\0') >= 0)
            {
                error = "The path contains a NUL character.";
                return false;
            }

            foreach (var c in candidate)
            {
                if (IsBidirectionalControl(c))
                {
                    error = "The path contains a bidirectional text control character (U+" + ((int)c).ToString("X4") + ").";
                    return false;
                }
            }

            if (!_windows)
            {
                return true;
            }

            var p = candidate.Replace('/', '\\');
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                error = @"Device and extended-length paths (\\?\ and \\.\) are not allowed; use a normal path.";
                return false;
            }

            var scanFrom = 0;
            if (p.Length >= 2 && IsAsciiLetter(p[0]) && p[1] == ':')
            {
                if (p.Length == 2 || p[2] != '\\')
                {
                    error = "Drive-relative path '" + candidate + "' is not supported; use a path relative to the workspace root or a full path under " + Root + ".";
                    return false;
                }

                scanFrom = 2;
            }
            else if (p[0] == '\\')
            {
                if (p.Length == 1 || p[1] != '\\')
                {
                    error = "Path '" + candidate + "' starts with a separator but names no drive; use a path relative to the workspace root (for example 'src/File.cs') or a full path under " + Root + ".";
                    return false;
                }

                scanFrom = 2;
            }

            for (var i = scanFrom; i < p.Length; i++)
            {
                var c = p[i];
                if (c < ' ' || c == '<' || c == '>' || c == '"' || c == '|' || c == '*' || c == '?' || c == ':')
                {
                    error = "The path contains the invalid character '" + (c < ' ' ? "\\u" + ((int)c).ToString("x4") : c.ToString()) + "'.";
                    return false;
                }
            }

            foreach (var segment in p.Substring(scanFrom).Split('\\'))
            {
                // "." and ".." are handled by normalization; longer runs of dots are not valid names and are treated
                // inconsistently by Windows components, so they are refused outright.
                if (segment.Length >= 3 && segment.Trim('.').Length == 0)
                {
                    error = "The path contains the invalid segment '" + segment + "'.";
                    return false;
                }

                if (segment.Length > 0 && segment != "." && segment != ".." &&
                    (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)))
                {
                    error = "The path contains an ambiguous Windows segment ending with a dot or space: '" + segment + "'.";
                    return false;
                }

                if (IsReservedDeviceName(segment))
                {
                    error = "The path contains the reserved Windows device name '" + segment + "' (CON, NUL, COM1, ...), which is not a file.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Bidirectional text controls can make a path read differently from what it is (display spoofing).</summary>
        private static bool IsBidirectionalControl(char c) =>
            (c >= '\x202A' && c <= '\x202E') || (c >= '\x2066' && c <= '\x2069') || c == '\x200E' || c == '\x200F' || c == '\x061C';

        /// <summary>
        /// Windows maps names such as CON, NUL or COM1 (with or without an extension, trailing dots or spaces) to devices;
        /// opening them can block or read from the console, so they are never valid file names here.
        /// </summary>
        private static bool IsReservedDeviceName(string segment)
        {
            var name = segment;
            var dot = name.IndexOf('.');
            if (dot >= 0)
            {
                name = name.Substring(0, dot);
            }

            name = name.TrimEnd(' ');
            if (name.Length < 3 || name.Length > 7)
            {
                return false;
            }

            switch (name.ToUpperInvariant())
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "CONIN$":
                case "CONOUT$":
                    return true;
            }

            if (name.Length == 4)
            {
                var prefix = name.Substring(0, 3).ToUpperInvariant();
                var digit = name[3];
                var isDigit = (digit >= '1' && digit <= '9') || digit == '\x00B9' || digit == '\x00B2' || digit == '\x00B3';
                return isDigit && (prefix == "COM" || prefix == "LPT");
            }

            return false;
        }

        private bool IsFullyQualified(string path)
        {
            if (!_windows)
            {
                return Path.IsPathRooted(path);
            }

            var p = path.Replace('/', '\\');
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            return p.Length >= 3 && IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
        }

        private static string TrimTrailingSeparators(string path)
        {
            var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
            var end = path.Length;
            while (end > rootLength && IsSeparator(path[end - 1]))
            {
                end--;
            }

            return end == path.Length ? path : path.Substring(0, end);
        }

        private static bool EndsWithSeparator(string path) => path.Length > 0 && IsSeparator(path[path.Length - 1]);

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private static bool IsPathException(Exception ex) =>
            ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException || ex is IOException;

        private static string ShortReason(Exception ex)
        {
            var message = ex.Message ?? string.Empty;
            var lineEnd = message.IndexOfAny(new[] { '\r', '\n' });
            return lineEnd >= 0 ? message.Substring(0, lineEnd) : message;
        }
    }
}
