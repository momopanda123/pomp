using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Pomp.Diagnostics;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>
    /// Reads the two omp settings layers POMP's <c>--config</c> overlay would otherwise wipe: top-level
    /// <c>disabledExtensions</c> and <c>skills.customDirectories</c> from the user's
    /// <c>~/.omp/agent/config.yml</c> (or a named profile's) and the workspace's <c>.omp/config.yml</c>.
    /// omp replaces arrays wholesale at each layer (docs/settings.md "Merge rules"), so the overlay must
    /// repeat the merged values or a user's own settings would silently stop applying.
    /// Only the small YAML subset the engine's own <c>yaml</c> parser and this reader agree on is taken
    /// (plain, single- and double-quoted single-line scalars, block and flow lists, <c>#</c> comments,
    /// optional <c>---</c>/<c>...</c> markers); anything richer is ignored for that key, and a missing
    /// or unreadable file is ignored too (a warning is logged, the engine start never fails).
    /// </summary>
    internal static class OmpConfigLayers
    {
        /// <summary>Config files are small; anything bigger is not read at all.</summary>
        internal const int MaxBytes = 1024 * 1024;
        private const int MaxLines = 20000;
        private const int MaxListItems = 2000;
        private const int MaxScalar = 4096;

        internal static string UserConfigPath(string? home, string? profile)
        {
            var agent = profile == null
                ? Path.Combine(ToolResourceFiles.Home(home), ".omp", "agent")
                : Path.Combine(ToolResourceFiles.Home(home), ".omp", "profiles", profile, "agent");
            var preferred = Path.Combine(agent, "config.yml");
            // config.yml is canonical; an existing config.yaml is loaded and updated in place instead.
            return File.Exists(preferred) || !File.Exists(Path.Combine(agent, "config.yaml")) ? preferred : Path.Combine(agent, "config.yaml");
        }

        internal static string ProjectConfigPath(string root) => Path.Combine(Path.GetFullPath(root), ".omp", "config.yml");

        internal sealed class LayerValues
        {
            internal readonly List<string> DisabledExtensions = new List<string>();
            internal readonly List<string> CustomDirectories = new List<string>();
        }

        internal static LayerValues Read(string path, IPompLogger? logger)
        {
            var values = new LayerValues();
            string text;
            try
            {
                CheckpointStore.CheckDirectPath(path);
                if (!File.Exists(path)) return values;
                text = ToolResourceFiles.Text(ToolResourceFiles.Read(path, MaxBytes));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                logger?.Warn("Could not read the omp configuration " + path + ": " + ex.Message);
                return values;
            }

            foreach (var value in ScalarsAt(text, new[] { "disabledExtensions" })) values.DisabledExtensions.Add(value);
            foreach (var value in ScalarsAt(text, new[] { "skills", "customDirectories" })) values.CustomDirectories.Add(value);
            return values;
        }

        /// <summary>The string items of the block or flow list at <paramref name="path"/> (a lone scalar counts as one item, as omp's settings do).</summary>
        internal static IReadOnlyList<string> ScalarsAt(string text, IReadOnlyList<string> path)
        {
            var items = new List<string>();
            try
            {
                Collect(text, path, items);
            }
            catch
            {
                // Not the subset this reader understands: ignore the key.
                return Array.Empty<string>();
            }

            return items;
        }

        private static void Collect(string text, IReadOnlyList<string> path, List<string> items)
        {
            if (text.IndexOf('\0') >= 0) throw new InvalidOperationException();
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            if (lines.Length > MaxLines) throw new InvalidOperationException();
            // The indentation stack of the mapping nesting the current line: "skills" at column 0, then "customDirectories" deeper.
            var stack = new List<(int Indent, string Key)> { (-1, "") };
            var index = 0;
            while (index < lines.Length)
            {
                var line = lines[index++];
                if (line.Length == 0 || line.Trim().Length == 0) continue;
                var trimmed = line.TrimStart(' ');
                if (trimmed.StartsWith("---", StringComparison.Ordinal) || trimmed.StartsWith("...", StringComparison.Ordinal)) continue;
                if (trimmed[0] == '#' || trimmed[0] == '\t' || trimmed[0] == '%') continue;
                var indent = line.Length - trimmed.Length;
                while (stack.Count > 1 && indent <= stack[stack.Count - 1].Indent) stack.RemoveAt(stack.Count - 1);
                if (trimmed[0] == '-')
                {
                    // A list item without its key line above is foreign syntax (or a list where a mapping was expected): stop this level.
                    stack.Clear(); stack.Add((-1, ""));
                    continue;
                }

                var colon = MappingColon(trimmed);
                if (colon < 0)
                {
                    stack.Clear(); stack.Add((-1, ""));
                    continue;
                }

                var key = trimmed.Substring(0, colon).Trim();
                if (key.Length == 0) throw new InvalidOperationException();
                var rest = trimmed.Substring(colon + 1).Trim();
                if (rest.StartsWith("<<", StringComparison.Ordinal) || rest.StartsWith("*", StringComparison.Ordinal)) throw new InvalidOperationException();
                var depth = stack.Count - 1;
                var onPath = depth < path.Count && string.Equals(key, path[depth], StringComparison.Ordinal)
                    && stack.Skip(1).Select(s => s.Key).SequenceEqual(path.Take(depth), StringComparer.Ordinal)
                    && (depth == 0 || indent > stack[stack.Count - 1].Indent);
                if (onPath)
                {
                    if (depth + 1 == path.Count)
                    {
                        ReadValue(lines, ref index, rest, indent, items);
                    }
                    else if (rest.Length == 0 || rest[0] == '#')
                    {
                        stack.Add((indent, key));
                        continue;
                    }
                }
                else if (rest.Length == 0 || rest[0] == '#')
                {
                    // A mapping this reader does not follow: its nested keys are not top-level keys.
                    stack.Add((indent, key));
                    continue;
                }

                // Any other key at this level (or a value where a mapping was expected) closes the nesting below it.
                while (stack.Count > depth + 1) stack.RemoveAt(stack.Count - 1);
            }
        }

        /// <summary>The <c>key:</c> colon: a colon followed by a space, the end of the line, or a comment; quoted keys are the engine's.</summary>
        private static int MappingColon(string trimmed)
        {
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (c == '"' || c == '\'') return -1;
                if (c != ':') continue;
                if (i + 1 == trimmed.Length || trimmed[i + 1] == ' ' || trimmed[i + 1] == '\t' || trimmed[i + 1] == '#') return i;
            }

            return -1;
        }

        private static void ReadValue(string[] lines, ref int index, string rest, int indent, List<string> items)
        {
            if (rest.Length > 0 && rest[0] != '#')
            {
                if (rest[0] == '[')
                {
                    ReadFlowList(rest, items);
                    return;
                }

                if (rest[0] == '|' || rest[0] == '>') throw new InvalidOperationException();
                var scalar = ReadScalar(rest);
                if (scalar != null && items.Count < MaxListItems) items.Add(scalar);
                return;
            }

            // A block list on the lines below: every item indented more than the key, "- " at one indentation.
            var itemIndent = -1;
            while (index < lines.Length)
            {
                var line = lines[index];
                if (line.Trim().Length == 0 || line.TrimStart(' ').StartsWith("#", StringComparison.Ordinal)) { index++; continue; }
                var trimmed = line.TrimStart(' ');
                if (trimmed[0] == '\t') throw new InvalidOperationException();
                var lineIndent = line.Length - trimmed.Length;
                if (lineIndent <= indent) return;
                if (!trimmed.StartsWith("-", StringComparison.Ordinal) || (trimmed.Length > 1 && trimmed[1] != ' ' && trimmed[1] != '\t')) return;
                if (itemIndent < 0) itemIndent = lineIndent;
                if (lineIndent != itemIndent) throw new InvalidOperationException();
                index++;
                var item = trimmed.Substring(1).Trim();
                if (item.Length == 0 || item[0] == '#' || item[0] == '-' || item[0] == '[' || item[0] == '{' || item[0] == '|' || item[0] == '>' || item[0] == '*' || item[0] == '&') throw new InvalidOperationException();
                var scalar = ReadScalar(item);
                if (scalar == null) throw new InvalidOperationException();
                if (items.Count < MaxListItems) items.Add(scalar);
            }
        }

        private static void ReadFlowList(string rest, List<string> items)
        {
            var position = 1;
            while (true)
            {
                while (position < rest.Length && rest[position] == ' ') position++;
                if (position >= rest.Length) throw new InvalidOperationException();
                if (rest[position] == ']')
                {
                    var tail = rest.Substring(position + 1).Trim();
                    if (tail.Length > 0 && tail[0] != '#') throw new InvalidOperationException();
                    return;
                }

                string? item;
                if (rest[position] == '"') item = ReadDouble(rest, ref position);
                else if (rest[position] == '\'') item = ReadSingle(rest, ref position);
                else
                {
                    var end = position;
                    while (end < rest.Length && rest[end] != ',' && rest[end] != ']') end++;
                    if (end == position) throw new InvalidOperationException();
                    item = PlainScalar(rest.Substring(position, end - position).Trim());
                    if (item == null) throw new InvalidOperationException();
                    position = end;
                }

                if (item == null) throw new InvalidOperationException();
                if (items.Count < MaxListItems) items.Add(item);
                while (position < rest.Length && rest[position] == ' ') position++;
                if (position >= rest.Length) throw new InvalidOperationException();
                if (rest[position] == ']') { position++; var tail = rest.Substring(position).Trim(); if (tail.Length > 0 && tail[0] != '#') throw new InvalidOperationException(); return; }
                if (rest[position] != ',') throw new InvalidOperationException();
                position++;
            }
        }

        private static string? ReadScalar(string rest)
        {
            var position = 0;
            string? scalar;
            if (rest[0] == '"') scalar = ReadDouble(rest, ref position);
            else if (rest[0] == '\'') scalar = ReadSingle(rest, ref position);
            else
            {
                // A plain scalar ends before " #"; quoting and indicators are the engine's.
                var end = rest.Length;
                var hash = rest.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) end = hash;
                scalar = PlainScalar(rest.Substring(0, end).Trim());
                var tail = rest.Substring(end).Trim();
                if (tail.Length > 0 && tail[0] != '#') return null;
                return scalar;
            }

            if (scalar == null) return null;
            var rest2 = rest.Substring(position).Trim();
            return rest2.Length == 0 || rest2[0] == '#' ? scalar : null;
        }

        /// <summary>A plain scalar in block context: no indicator first, no ": " inside, no trailing colon, and not null/boolean/number-like.</summary>
        private static string? PlainScalar(string value)
        {
            if (value.Length == 0 || value.Length > MaxScalar || value.IndexOf('\0') >= 0) return null;
            if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) >= 0) return null;
            if (value.Contains(": ") || value[value.Length - 1] == ':' || value.Contains(" #")) return null;
            if (IsNonString(value)) return null;
            return value;
        }

        private static bool IsNonString(string value)
        {
            if (value is "~" or "null" or "Null" or "NULL" or "true" or "True" or "TRUE" or "false" or "False" or "FALSE") return true;
            if ((value[0] == '-' || value[0] == '+' || value[0] == '.' || (value[0] >= '0' && value[0] <= '9')) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
            return false;
        }

        private static string? ReadDouble(string value, ref int position)
        {
            var builder = new StringBuilder(); position++;
            while (position < value.Length)
            {
                if (builder.Length > MaxScalar) return null;
                var c = value[position++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }
                if (position >= value.Length) return null;
                var escape = value[position++];
                if (escape == '"' || escape == '\\' || escape == '/') builder.Append(escape);
                else if (escape == 'n') builder.Append('\n');
                else if (escape == 't') builder.Append('\t');
                else if (escape == 'u' && position + 4 <= value.Length && int.TryParse(value.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) && (code < 0xD800 || code > 0xDFFF)) { builder.Append((char)code); position += 4; }
                else return null;
            }

            return null;
        }

        private static string? ReadSingle(string value, ref int position)
        {
            var builder = new StringBuilder(); position++;
            while (position < value.Length)
            {
                if (builder.Length > MaxScalar) return null;
                var c = value[position++];
                if (c != '\'') { builder.Append(c); continue; }
                if (position < value.Length && value[position] == '\'') { builder.Append('\''); position++; continue; }
                return builder.ToString();
            }

            return null;
        }
    }
}
