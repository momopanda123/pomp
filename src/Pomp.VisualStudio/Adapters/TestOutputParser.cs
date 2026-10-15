using System;
using System.Collections.Generic;
using System.IO;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Incremental, bounded parsing of the English .NET test platform discovery output.</summary>
    internal sealed class TestOutputParser
    {
        private const string ListHeader = "The following Tests are available:";
        private readonly int maximum;
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private bool inTests;

        public TestOutputParser(int maximum = 2000)
        {
            this.maximum = Math.Max(0, Math.Min(2000, maximum));
        }

        public List<string> Names { get; } = new List<string>();

        public bool HeaderFound { get; private set; }

        public bool NoTestsFound { get; private set; }

        public bool Truncated { get; private set; }

        public void AddLine(string? line)
        {
            if (line == null)
            {
                return;
            }

            var text = line.Trim().TrimStart('﻿');
            if (string.Equals(text, ListHeader, StringComparison.OrdinalIgnoreCase))
            {
                HeaderFound = true;
                inTests = true;
                return;
            }

            if (text.StartsWith("No test is available in ", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("No tests were found", StringComparison.OrdinalIgnoreCase))
            {
                NoTestsFound = true;
            }

            // The platform indents each complete display name. Do not split names on spaces, commas or generic syntax,
            // and never reinterpret arbitrary (including localized) build/log output as a list of test names.
            if (!inTests || text.Length == 0 || line.Length == 0 || !char.IsWhiteSpace(line[0]))
            {
                if (text.Length > 0)
                {
                    inTests = false;
                }

                return;
            }

            if (seen.Contains(text))
            {
                return;
            }

            if (Names.Count >= maximum)
            {
                Truncated = true;
                return;
            }

            seen.Add(text);
            Names.Add(TestRunnerText.Truncate(text, 4096));
        }

        public static TestOutputParser Parse(string text, int maximum = 2000)
        {
            var parser = new TestOutputParser(maximum);
            using (var reader = new StringReader(text ?? string.Empty))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    parser.AddLine(line);
                }
            }

            return parser;
        }
    }
}
