using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;

namespace Pomp.Features
{
    public sealed class ReviewFinding
    {
        public ReviewFinding(string origin, string severity, string code, string message, string path, int line, string sourceHash = "", string evidence = "", string reporter = "")
        {
            Origin = origin; Severity = severity; Code = code; Message = message; Path = path; Line = line; SourceHash = sourceHash; Evidence = evidence; Reporter = reporter;
            Id = ContentHash.Compute(origin + "\n" + path + "\n" + line + "\n" + code + "\n" + message + "\n" + sourceHash);
        }
        public string Id { get; }
        public string Origin { get; }
        public string Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public string Path { get; }
        public int Line { get; }
        public string SourceHash { get; }
        public string Evidence { get; }
        /// <summary>The agent that reported an agent finding: "main" (the chat's POMP) or a member's role name; empty when not known.</summary>
        public string Reporter { get; }
    }
    public sealed class CodeReviewResult
    {
        public CodeReviewResult(IEnumerable<ReviewFinding> findings, IEnumerable<string> notices)
        { Findings = findings.ToArray(); Notices = notices.ToArray(); }
        public IReadOnlyList<ReviewFinding> Findings { get; }
        public IReadOnlyList<string> Notices { get; }
    }
    public interface ICodeReviewSource
    {
        Task<CodeReviewResult> ReviewAsync(string root, string? path, CancellationToken token);
        Task<bool> VerifyEvidenceAsync(string root, string path, string hash, int line, string evidence, CancellationToken token);
        Task<bool> NavigateAsync(string root, string path, int line, CancellationToken token);
    }
    public interface ICodeReviewAdapters { ICodeReviewSource CodeReview { get; } }
}
