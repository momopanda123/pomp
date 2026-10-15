using System;
using System.Globalization;
using Pomp.Adapters;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Parses NUL-delimited porcelain v2 without splitting or unquoting file names.</summary>
    internal static class GitStatusParser
    {
        public static GitStatus Parse(string text, string repositoryRoot)
        {
            var result = new GitStatus { IsRepository = true, RepositoryRoot = repositoryRoot };
            var offset = 0;
            while (offset < text.Length)
            {
                var record = ReadRecord(text, ref offset);
                if (record.Length == 0)
                {
                    continue;
                }

                if (record[0] == '#')
                {
                    ReadHeader(record, result);
                    continue;
                }

                switch (record[0])
                {
                    case '?':
                        Require(record.StartsWith("? ", StringComparison.Ordinal) && record.Length > 2);
                        result.Changes.Add(new GitFileChange { Path = record.Substring(2), IndexStatus = '?', WorkTreeStatus = '?' });
                        break;
                    case '1':
                        result.Changes.Add(ReadChange(record, 8));
                        break;
                    case '2':
                        var rename = ReadChange(record, 9);
                        rename.OriginalPath = ReadRecord(text, ref offset);
                        Require(rename.OriginalPath.Length > 0);
                        result.Changes.Add(rename);
                        break;
                    case 'u':
                        result.Changes.Add(ReadChange(record, 10));
                        break;
                    case '!': // Ignored files are not changes (normally absent without --ignored).
                        break;
                    default:
                        throw new FormatException("Git returned an unsupported porcelain v2 record.");
                }
            }

            return result;
        }

        private static string ReadRecord(string text, ref int offset)
        {
            var end = text.IndexOf('\0', offset);
            Require(end >= 0);
            var value = text.Substring(offset, end - offset);
            offset = end + 1;
            return value;
        }

        private static GitFileChange ReadChange(string record, int separatorsBeforePath)
        {
            Require(record.Length >= 5 && record[1] == ' ' && record[4] == ' ');
            var offset = 0;
            for (var i = 0; i < separatorsBeforePath; i++)
            {
                var space = record.IndexOf(' ', offset);
                Require(space >= offset);
                offset = space + 1;
            }

            Require(offset < record.Length);
            return new GitFileChange
            {
                Path = record.Substring(offset),
                IndexStatus = record[2] == '.' ? ' ' : record[2],
                WorkTreeStatus = record[3] == '.' ? ' ' : record[3],
            };
        }

        private static void ReadHeader(string record, GitStatus result)
        {
            if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var oid = record.Substring(13);
                result.HeadCommit = oid == "(initial)" ? null : oid;
            }
            else if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var head = record.Substring(14);
                result.Branch = head == "(detached)" ? null : head;
            }
            else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                result.Upstream = record.Substring(18);
            }
            else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = record.Substring(12).Split(' ');
                Require(parts.Length == 2 && parts[0].StartsWith("+", StringComparison.Ordinal) && parts[1].StartsWith("-", StringComparison.Ordinal));
                Require(int.TryParse(parts[0].Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var ahead));
                Require(int.TryParse(parts[1].Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var behind));
                result.Ahead = ahead;
                result.Behind = behind;
            }
        }

        private static void Require(bool condition)
        {
            if (!condition)
            {
                throw new FormatException("Git returned incomplete or malformed porcelain v2 output.");
            }
        }
    }
}
