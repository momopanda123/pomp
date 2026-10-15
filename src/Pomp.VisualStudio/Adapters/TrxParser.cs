using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Pomp.Adapters;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Reads bounded TRX data without resolving entities, DTDs or external resources.</summary>
    internal static class TrxParser
    {
        internal const int MaxResults = 100;
        internal const int MaxMessageCharacters = 4000;
        internal const int MaxStackCharacters = 8000;
        private const long MaxTrxCharacters = 64L * 1024 * 1024;
        private static readonly XNamespace Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        public static TestRunResult ParseFile(string path, string project)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = XmlReader.Create(stream, Settings()))
            {
                return Parse(reader, project);
            }
        }

        internal static TestRunResult ParseText(string xml, string project)
        {
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, Settings()))
            {
                return Parse(reader, project);
            }
        }

        private static XmlReaderSettings Settings() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxTrxCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        private static TestRunResult Parse(XmlReader reader, string project)
        {
            reader.MoveToContent();
            if (reader.LocalName != "TestRun" || reader.NamespaceURI != Namespace.NamespaceName)
            {
                throw new XmlException("The result file is not a supported Visual Studio TRX TestRun.");
            }

            var result = new TestRunResult();
            var observedTotal = 0;
            var observedPassed = 0;
            var observedFailed = 0;
            var observedSkipped = 0;
            string? summaryOutcome = null;
            string? runError = null;
            string? failureLog = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != Namespace.NamespaceName)
                {
                    continue;
                }

                if (reader.LocalName == "ResultSummary")
                {
                    summaryOutcome = reader.GetAttribute("outcome");
                }
                else if (reader.LocalName == "Counters")
                {
                    result.Total = Counter(reader, "total");
                    result.Passed = Counter(reader, "passed");
                    result.Failed = Sum(Counter(reader, "failed"), Counter(reader, "error"), Counter(reader, "timeout"),
                        Counter(reader, "aborted"), Counter(reader, "inconclusive"), Counter(reader, "notRunnable"), Counter(reader, "disconnected"));
                    result.Skipped = Sum(Counter(reader, "notExecuted"), Counter(reader, "skipped"), Counter(reader, "pending"));
                }
                else if (reader.LocalName == "UnitTestResult")
                {
                    XElement element;
                    using (var subtree = reader.ReadSubtree())
                    {
                        element = XElement.Load(subtree);
                    }

                    var outcome = NormalizeOutcome((string?)element.Attribute("outcome"));
                    var error = element.Element(Namespace + "Output")?.Element(Namespace + "ErrorInfo");
                    var duration = (string?)element.Attribute("duration");
                    var item = new TestOutcomeInfo
                    {
                        Project = project,
                        Name = TestRunnerText.Truncate((string?)element.Attribute("testName") ?? "(unnamed test)", 4096),
                        Outcome = outcome,
                        Message = TestRunnerText.Truncate(error?.Element(Namespace + "Message")?.Value, MaxMessageCharacters),
                        StackTrace = TestRunnerText.Truncate(error?.Element(Namespace + "StackTrace")?.Value, MaxStackCharacters),
                        DurationMs = TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero ? time.TotalMilliseconds : (double?)null,
                    };
                    observedTotal = Sum(observedTotal, 1);
                    if (outcome == "Passed")
                    {
                        observedPassed = Sum(observedPassed, 1);
                    }
                    else if (outcome == "Skipped" || outcome == "NotExecuted")
                    {
                        observedSkipped = Sum(observedSkipped, 1);
                    }
                    else
                    {
                        observedFailed = Sum(observedFailed, 1);
                    }

                    AddResult(result, item);
                }
                else if (reader.LocalName == "RunInfo" && !string.Equals(reader.GetAttribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(reader.GetAttribute("outcome"), "Warning", StringComparison.OrdinalIgnoreCase))
                {
                    using (var subtree = reader.ReadSubtree())
                    {
                        var info = XElement.Load(subtree);
                        var message = info.Element(Namespace + "Text")?.Value;
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            if (message!.TrimEnd().EndsWith("[FAIL]", StringComparison.Ordinal) && message.TrimStart().StartsWith("[xUnit.net ", StringComparison.Ordinal))
                            {
                                failureLog = TestRunnerText.JoinError(failureLog, message);
                            }
                            else
                            {
                                runError = TestRunnerText.JoinError(runError, message);
                            }
                        }
                    }
                }
            }

            result.Passed = Math.Max(result.Passed, observedPassed);
            result.Failed = Math.Max(result.Failed, observedFailed);
            result.Skipped = Math.Max(result.Skipped, observedSkipped);
            result.Total = Math.Max(Math.Max(result.Total, observedTotal), Sum(result.Passed, result.Failed, result.Skipped));
            // Adapters (notably xUnit) also emit RunInfo Error records for ordinary assertion failures.
            // Those are already represented by Failed results; do not turn a completed failed run into a tool error.
            if (summaryOutcome != null && (string.Equals(summaryOutcome, "Aborted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(summaryOutcome, "Error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(summaryOutcome, "Timeout", StringComparison.OrdinalIgnoreCase)))
            {
                result.Error = runError ?? "The test platform reported an incomplete run (" + summaryOutcome + ").";
            }
            else
            {
                result.Error = runError ?? (result.Failed == 0 ? failureLog : null);
            }

            return result;
        }

        internal static void Merge(TestRunResult target, TestRunResult source)
        {
            target.Total = Sum(target.Total, source.Total);
            target.Passed = Sum(target.Passed, source.Passed);
            target.Failed = Sum(target.Failed, source.Failed);
            target.Skipped = Sum(target.Skipped, source.Skipped);
            if (!string.IsNullOrWhiteSpace(source.Error))
            {
                target.Error = TestRunnerText.JoinError(target.Error, source.Error!);
            }

            foreach (var item in source.Results)
            {
                AddResult(target, item);
            }
        }

        private static void AddResult(TestRunResult result, TestOutcomeInfo item)
        {
            var priority = Priority(item.Outcome);
            var index = result.Results.FindIndex(other => Priority(other.Outcome) > priority);
            if (index < 0)
            {
                if (result.Results.Count >= MaxResults)
                {
                    return;
                }

                index = result.Results.Count;
            }

            result.Results.Insert(index, item);
            if (result.Results.Count > MaxResults)
            {
                result.Results.RemoveAt(MaxResults);
            }
        }

        private static int Priority(string outcome) => outcome == "Passed" ? 2 : outcome == "Skipped" || outcome == "NotExecuted" ? 1 : 0;

        private static string NormalizeOutcome(string? value)
        {
            if (string.Equals(value, "Passed", StringComparison.OrdinalIgnoreCase))
            {
                return "Passed";
            }

            if (string.Equals(value, "NotExecuted", StringComparison.OrdinalIgnoreCase))
            {
                return "NotExecuted";
            }

            return string.Equals(value, "Skipped", StringComparison.OrdinalIgnoreCase) ? "Skipped" : "Failed";
        }

        private static int Counter(XmlReader reader, string name) => int.TryParse(reader.GetAttribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count >= 0 ? count : 0;

        private static int Sum(params int[] values) => (int)Math.Min(int.MaxValue, values.Sum(v => (long)Math.Max(0, v)));
    }
}
