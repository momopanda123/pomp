using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Hosting;

namespace Pomp.Features
{
    public sealed class ShadowFile
    {
        public ShadowFile(string path, string before, string after)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A workspace file identity is required.", nameof(path));
            Path = path; Before = before ?? throw new ArgumentNullException(nameof(before));
            After = after ?? throw new ArgumentNullException(nameof(after));
            BeforeHash = ContentHash.Compute(before); AfterHash = ContentHash.Compute(after);
        }
        public string Path { get; }
        public string Before { get; }
        public string After { get; }
        public string BeforeHash { get; }
        public string AfterHash { get; }
    }

    /// <summary>A frozen revision handed to the IDE's shadow compiler. No disk writes are performed by this model.</summary>
    public sealed class ShadowSnapshot
    {
        internal ShadowSnapshot(long revision, IReadOnlyList<ShadowFile> files) { Revision = revision; Files = files; }
        public long Revision { get; }
        public IReadOnlyList<ShadowFile> Files { get; }
    }

    public sealed class ShadowDiagnostic
    {
        public ShadowDiagnostic(string message, bool isError, string? path = null, int? line = null)
        { Message = message; IsError = isError; Path = path; Line = line; }
        public string Message { get; }
        public bool IsError { get; }
        public string? Path { get; }
        public int? Line { get; }
    }

    public sealed class ShadowValidation
    {
        public ShadowValidation(bool completed, IEnumerable<ShadowDiagnostic> diagnostics)
        { Completed = completed; Diagnostics = diagnostics.ToList().AsReadOnly(); }
        /// <summary>False for missing compiler/cancelled/unsupported validation. No diagnostics is not sufficient evidence.</summary>
        public bool Completed { get; }
        public IReadOnlyList<ShadowDiagnostic> Diagnostics { get; }
        public bool CanApply => Completed && !Diagnostics.Any(d => d.IsError);
    }

    public interface ISourceGeneratorShadowValidator : IShadowValidator
    {
        IShadowValidator WithSourceGenerators(bool enabled);
    }

    public interface IShadowValidator
    {
        /// <summary>Validate the frozen overlay without mutating user buffers or saved files.</summary>
        Task<ShadowValidation> ValidateAsync(ShadowSnapshot snapshot, CancellationToken cancellationToken);
    }

    /// <summary>Bounded in-memory overlay and hunk selections. Any edit invalidates validation of an earlier revision.</summary>
    public sealed class ShadowWorkspace
    {
        public const int MaxFiles = 100;
        public const int MaxCharacters = 16_000_000;
        private readonly object gate = new object();
        private readonly Dictionary<string, Review> files = new Dictionary<string, Review>(StringComparer.OrdinalIgnoreCase);
        private long revision;
        private long validatedRevision = -1;
        private ShadowValidation? validation;
        public long Revision { get { lock (gate) return revision; } }
        public bool CanApply { get { lock (gate) return validatedRevision == revision && validation?.CanApply == true && files.Values.Any(r => r.Selected.Count > 0); } }

        public LineDiff Stage(ShadowFile file, CancellationToken cancellationToken = default)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            var diff = LineDiff.Create(file.Before, file.After, cancellationToken);
            return StagePrepared(file.Path, diff);
        }

        internal LineDiff StagePrepared(string path, LineDiff diff)
        {
            if (string.IsNullOrWhiteSpace(path) || diff == null) throw new ArgumentException("A source path and immutable diff are required.");
            lock (gate)
            {
                if (!files.ContainsKey(path) && files.Count >= MaxFiles) throw new InvalidOperationException("Too many files.");
                var total = files.Where(p => !string.Equals(p.Key, path, StringComparison.OrdinalIgnoreCase))
                    .Sum(p => (long)p.Value.Diff.Before.Length + p.Value.Diff.After.Length);
                if (total + diff.Before.Length + diff.After.Length > MaxCharacters) throw new InvalidOperationException("The proposed changes are too large.");
                files[path] = new Review(path, diff, new HashSet<int>(diff.Hunks.Select(h => h.Id)));
                Invalidate();
            }
            return diff;
        }

        public void Select(string path, IEnumerable<int> hunkIds)
        {
            if (hunkIds == null) throw new ArgumentNullException(nameof(hunkIds));
            lock (gate)
            {
                if (!files.TryGetValue(path, out var review)) throw new ArgumentException(PompLanguage.Pick("알 수 없는 섀도 파일이에요.", "Unknown shadow file."), nameof(path));
                var selected = new HashSet<int>(hunkIds);
                if (selected.Any(id => id < 0 || id >= review.Diff.Hunks.Count)) throw new ArgumentException(PompLanguage.Pick("알 수 없는 변경 덩어리예요.", "Unknown diff hunk."));
                if (review.Selected.SetEquals(selected)) return;
                files[path] = new Review(review.Path, review.Diff, selected); Invalidate();
            }
        }

        public ShadowSnapshot Snapshot()
        {
            lock (gate)
            {
                return new ShadowSnapshot(revision, files.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new ShadowFile(r.Path, r.Diff.Before, r.Diff.ApplySelection(r.Selected)))
                    .Where(f => f.Before != f.After).ToList().AsReadOnly());
            }
        }

        public async Task<ShadowValidation> ValidateAsync(IShadowValidator validator, CancellationToken cancellationToken)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            var snapshot = Snapshot();
            var result = await validator.ValidateAsync(snapshot, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == null) throw new InvalidOperationException(PompLanguage.Pick("섀도 검증기가 결과를 반환하지 않았어요.", "The shadow validator returned no result."));
            lock (gate)
            {
                if (snapshot.Revision == revision) { validatedRevision = revision; validation = result; }
            }
            return result;
        }

        /// <summary>Returns the exact validated candidate, never a newer unvalidated selection.</summary>
        public ShadowSnapshot GetValidatedSnapshot()
        {
            lock (gate)
            {
                if (!CanApply) throw new InvalidOperationException(PompLanguage.Pick("현재 섀도 리비전이 검증을 통과하지 못했어요.", "The current shadow revision has not passed validation."));
                var snapshot = Snapshot();
                if (snapshot.Files.Count == 0) throw new InvalidOperationException(PompLanguage.Pick("적용할 선택된 변경이 없어요.", "No selected changes to apply."));
                return snapshot;
            }
        }

        public void Clear() { lock (gate) { files.Clear(); Invalidate(); } }
        private void Invalidate() { revision++; validatedRevision = -1; validation = null; }
        private sealed class Review
        {
            public Review(string path, LineDiff diff, HashSet<int> selected) { Path = path; Diff = diff; Selected = selected; }
            public string Path { get; }
            public LineDiff Diff { get; }
            public HashSet<int> Selected { get; }
        }
    }
}
