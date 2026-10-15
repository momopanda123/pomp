using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.Adapters;
using Pomp.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Totals of the Error List plus its first items (errors first): what a finished build reports.</summary>
    internal sealed class ErrorListSummary
    {
        public ErrorListSummary(int errorCount, int warningCount, List<ErrorListItem> items)
        {
            ErrorCount = errorCount;
            WarningCount = warningCount;
            Items = items;
        }

        /// <summary>Every error in the list, however many items <see cref="Items"/> holds.</summary>
        public int ErrorCount { get; }

        /// <summary>Every warning in the list, however many items <see cref="Items"/> holds.</summary>
        public int WarningCount { get; }

        /// <summary>At most the requested number of items; errors before warnings.</summary>
        public List<ErrorListItem> Items { get; }
    }

    /// <summary>
    /// Error List contents. Reads the Error List's table (which carries the error code that DTE does not expose) and falls
    /// back to DTE2 <c>ErrorItems</c> when the table is unavailable. The table is what the grid shows, i.e. it follows the
    /// user's Error List scope, severity toggles and search filter.
    /// </summary>
    internal sealed class ErrorListAdapter : IObservableErrorListAdapter, IDisposable
    {
        // EnvDTE.Constants.vsWindowKindErrorList
        private static readonly Guid ErrorListWindowGuid = new Guid("D78612C7-9962-4B83-95D9-268046DAD23A");

        /// <summary>
        /// The DTE fallback of the summary costs one COM call per item, all on the UI thread: it counts in batches of this many items and
        /// lets the message loop run in between.
        /// </summary>
        private const int DteBatchSize = 500;

        /// <summary>How long the UI thread is left alone between two batches of the DTE fallback.</summary>
        private static readonly TimeSpan DteBatchPause = TimeSpan.FromMilliseconds(10);

        private readonly VsAdapterContext context;
        private IWpfTableControl? observedTable;
        // The Error List's table exists only once its window has been shown; until then this checks for it so a later open refreshes.
        private System.Windows.Threading.DispatcherTimer? tableProbe;
        private Workspace? workspace;
        private EnvDTE.DocumentEvents? documents;
        private EnvDTE.BuildEvents? builds;
        private EnvDTE.TextEditorEvents? edits;
        private bool disposed;
        public event EventHandler? Changed;

        public ErrorListAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task InitializeAsync(CancellationToken token)
        {
            await context.Jtf.SwitchToMainThreadAsync(token);
            try
            {
                documents = context.Dte.Events.DocumentEvents; documents.DocumentSaved += OnSaved;
                builds = context.Dte.Events.BuildEvents; builds.OnBuildDone += OnBuilt;
                edits = context.Dte.Events.TextEditorEvents; edits.LineChanged += OnEdited;
                workspace = context.ComponentModel.GetService<VisualStudioWorkspace>();
                if (workspace != null) workspace.WorkspaceChanged += OnWorkspaceChanged;
                EnsureErrorListCreated(); ObserveTable();
            }
            catch (Exception ex) { context.LogUnexpected("Subscribe diagnostic events", ex); }
        }
        private void OnSaved(EnvDTE.Document document) => Raise();
        private void OnBuilt(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action) => Raise();
        private void OnEdited(EnvDTE.TextPoint start, EnvDTE.TextPoint end, int hint) => Raise();
        private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs args) => Raise();
        private void OnEntriesChanged(object? sender, EventArgs args) => Raise();
        private void Raise() { if (!disposed) Changed?.Invoke(this, EventArgs.Empty); }
        private void ObserveTable()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var table = (Package.GetGlobalService(typeof(SVsErrorList)) as IErrorList)?.TableControl;
            if (ReferenceEquals(table, observedTable)) return;
            if (observedTable != null) observedTable.EntriesChanged -= OnEntriesChanged;
            observedTable = table;
            if (observedTable != null) observedTable.EntriesChanged += OnEntriesChanged;
            if (observedTable == null && tableProbe == null && !disposed)
            {
                tableProbe = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
                tableProbe.Tick += OnTableProbe; tableProbe.Start();
            }
            else if (observedTable != null && tableProbe != null) { tableProbe.Stop(); tableProbe.Tick -= OnTableProbe; tableProbe = null; }
        }
        private void OnTableProbe(object? sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            ObserveTable();
            if (observedTable != null) Raise();
        }
        public void Dispose()
        {
            disposed = true;
            if (workspace != null) workspace.WorkspaceChanged -= OnWorkspaceChanged;
            _ = context.Jtf.RunAsync(async () =>
            {
                await context.Jtf.SwitchToMainThreadAsync();
                try
                {
                    if (observedTable != null) observedTable.EntriesChanged -= OnEntriesChanged;
                    if (tableProbe != null) { tableProbe.Stop(); tableProbe.Tick -= OnTableProbe; tableProbe = null; }
                    if (documents != null) documents.DocumentSaved -= OnSaved;
                    if (builds != null) builds.OnBuildDone -= OnBuilt;
                    if (edits != null) edits.LineChanged -= OnEdited;
                }
                catch (Exception) { /* IDE shutdown */ }
            });
        }
        private static IReadOnlyList<ErrorListItem> Ordered(List<ErrorListItem> items, int limit, string? root)
        {
            string PathOf(ErrorListItem item)
            {
                if (string.IsNullOrWhiteSpace(item.File)) return "";
                return !string.IsNullOrWhiteSpace(root) && new PathGuard(root!).TryResolve(item.File!, out var full, out _) ? full.Replace(System.IO.Path.DirectorySeparatorChar, '/') : item.File!.Replace(System.IO.Path.DirectorySeparatorChar, '/');
            }
            string Bound(string? value, int maximum) => value == null ? "" : value.Length <= maximum ? value : value.Substring(0, maximum);
            // Deduplicate before the cap, using the same content fields as the problem identity.
            return items.Select(item => new { Item = item, Path = PathOf(item), Code = Bound(item.Code, 100), Message = Bound(item.Description, 3000), Line = Math.Max(0, Math.Min(10000000, item.Line ?? 0)) })
                .OrderBy(row => row.Item.Severity).ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Line)
                .ThenBy(row => row.Code, StringComparer.Ordinal).ThenBy(row => row.Message, StringComparer.Ordinal)
                .ThenBy(row => row.Item.Column ?? 0).ThenBy(row => row.Item.Project, StringComparer.Ordinal)
                .GroupBy(row => new { row.Path, row.Code, row.Message, row.Line }).Select(group => group.First().Item).Take(limit).ToArray();
        }
        private static bool Included(ErrorListItem item, ErrorListFilter filter)
        {
            if (!HasDiagnostic(item)) return false;
            return string.IsNullOrWhiteSpace(filter.WorkspaceRoot) || string.IsNullOrWhiteSpace(item.File) || new PathGuard(filter.WorkspaceRoot!).TryResolve(item.File, out _, out _);
        }
        public async Task<IReadOnlyList<ErrorListItem>> ReadAsync(ErrorListFilter filter, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            return Read(filter ?? new ErrorListFilter());
        }

        /// <summary>Synchronous read for callers that already run on the UI thread.</summary>
        internal IReadOnlyList<ErrorListItem> Read(ErrorListFilter filter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnsureErrorListCreated(); ObserveTable();

            var maxItems = filter.MaxItems > 0 ? filter.MaxItems : int.MaxValue;
            var fromTable = TryReadTable(filter, maxItems);
            if (fromTable != null)
            {
                return fromTable;
            }

            return ReadDte(filter, maxItems);
        }

        /// <summary>
        /// Counts every error and warning in the Error List (no cap) and returns the first <paramref name="maxItems"/> items, errors
        /// first. Used by the build adapter so the reported totals do not depend on how many items are handed back. The table is read in
        /// one go; the DTE fallback counts all entries in batches that give the UI thread's message loop a turn in between, and
        /// honours <paramref name="cancellationToken"/> between batches. Runs (and returns) on the UI thread.
        /// </summary>
        internal async Task<ErrorListSummary> ReadSummaryAsync(int maxItems, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            EnsureErrorListCreated();

            return TrySummarizeTable(maxItems) ?? await SummarizeDteAsync(maxItems, cancellationToken);
        }

        /// <summary>Creates (without showing) the Error List tool window; its table only fills once the window exists.</summary>
        private void EnsureErrorListCreated()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var guid = ErrorListWindowGuid;
                context.UiShell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref guid, out _);
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Creating the Error List window", ex);
            }
        }

        private IReadOnlyList<ErrorListItem>? TryReadTable(ErrorListFilter filter, int maxItems)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!(Package.GetGlobalService(typeof(SVsErrorList)) is IErrorList errorList) || errorList.TableControl == null)
                {
                    return null;
                }

                var wanted = filter.Severities == null || filter.Severities.Count == 0 ? null : new HashSet<ErrorSeverity>(filter.Severities);
                var items = new List<ErrorListItem>();

                foreach (var entry in errorList.TableControl.Entries)
                {
                    var severity = ToSeverity(GetValue<object>(entry, StandardTableKeyNames.ErrorSeverity));
                    if (wanted != null && !wanted.Contains(severity))
                    {
                        continue;
                    }

                    var project = GetValue<string>(entry, StandardTableKeyNames.ProjectName);
                    if (!string.IsNullOrEmpty(filter.Project) && !string.Equals(project, filter.Project, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var row = CreateItem(entry, severity, project);
                    if (Included(row, filter)) items.Add(row);

                    if (!filter.OrderByLocation && items.Count >= maxItems)
                    {
                        break;
                    }
                }

                return filter.OrderByLocation ? Ordered(items, maxItems, filter.WorkspaceRoot) : items;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                context.LogUnexpected("Reading the Error List table (falling back to DTE)", ex);
                return null;
            }
        }

        private ErrorListSummary? TrySummarizeTable(int maxItems)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!(Package.GetGlobalService(typeof(SVsErrorList)) is IErrorList errorList) || errorList.TableControl == null)
                {
                    return null;
                }

                var errors = new List<ErrorListItem>();
                var warnings = new List<ErrorListItem>();
                var errorCount = 0;
                var warningCount = 0;

                foreach (var entry in errorList.TableControl.Entries)
                {
                    var severity = ToSeverity(GetValue<object>(entry, StandardTableKeyNames.ErrorSeverity));
                    var row = CreateItem(entry, severity, GetValue<string>(entry, StandardTableKeyNames.ProjectName));
                    if (!HasDiagnostic(row)) continue;
                    if (severity == ErrorSeverity.Error)
                    {
                        errorCount++;
                        if (errors.Count < maxItems)
                        {
                            errors.Add(CreateItem(entry, severity, GetValue<string>(entry, StandardTableKeyNames.ProjectName)));
                        }
                    }
                    else if (severity == ErrorSeverity.Warning)
                    {
                        warningCount++;
                        if (warnings.Count < maxItems)
                        {
                            warnings.Add(CreateItem(entry, severity, GetValue<string>(entry, StandardTableKeyNames.ProjectName)));
                        }
                    }
                }

                return new ErrorListSummary(errorCount, warningCount, TakeErrorsFirst(errors, warnings, maxItems));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                context.LogUnexpected("Summarizing the Error List table (falling back to DTE)", ex);
                return null;
            }
        }

        private static bool HasDiagnostic(ErrorListItem row) => !string.IsNullOrWhiteSpace(row.Description) || !string.IsNullOrWhiteSpace(row.Code);

        private static ErrorListItem CreateItem(ITableEntryHandle entry, ErrorSeverity severity, string? project)
        {
            var line = GetValue<object>(entry, StandardTableKeyNames.Line);
            var column = GetValue<object>(entry, StandardTableKeyNames.Column);

            return new ErrorListItem
            {
                Severity = severity,
                Code = NullIfEmpty(GetValue<string>(entry, StandardTableKeyNames.ErrorCode)),
                Description = GetValue<string>(entry, StandardTableKeyNames.Text) ?? string.Empty,
                File = NullIfEmpty(GetValue<string>(entry, StandardTableKeyNames.DocumentName)),
                Line = ToOneBased(line),
                Column = ToOneBased(column),
                Project = NullIfEmpty(project),
            };
        }

        private static List<ErrorListItem> TakeErrorsFirst(List<ErrorListItem> errors, List<ErrorListItem> warnings, int maxItems)
        {
            var items = new List<ErrorListItem>(Math.Min(maxItems, errors.Count + warnings.Count));
            items.AddRange(errors.Take(maxItems));
            if (items.Count < maxItems)
            {
                items.AddRange(warnings.Take(maxItems - items.Count));
            }

            return items;
        }

        private IReadOnlyList<ErrorListItem> ReadDte(ErrorListFilter filter, int maxItems)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var wanted = filter.Severities == null || filter.Severities.Count == 0 ? null : new HashSet<ErrorSeverity>(filter.Severities);
            var items = new List<ErrorListItem>();
            var errorItems = ((DTE2)context.Dte).ToolWindows.ErrorList.ErrorItems;
            var count = errorItems.Count;

            for (var index = 1; index <= count && (filter.OrderByLocation || items.Count < maxItems); index++)
            {
                try
                {
                    var item = errorItems.Item(index);
                    var severity = ToSeverity(item.ErrorLevel);
                    if (wanted != null && !wanted.Contains(severity))
                    {
                        continue;
                    }

                    var project = item.Project;
                    if (!string.IsNullOrEmpty(filter.Project) && !string.Equals(project, filter.Project, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var row = CreateDteItem(item, severity, project);
                    if (Included(row, filter)) items.Add(row);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // An item that vanished while enumerating is skipped.
                }
            }

            return filter.OrderByLocation ? Ordered(items, maxItems, filter.WorkspaceRoot) : items;
        }

        /// <summary>
        /// DTE fallback of <see cref="ReadSummaryAsync"/>: the totals cover ALL entries. The COM calls stay on the UI thread, in batches of
        /// <see cref="DteBatchSize"/> entries; between batches <see cref="PauseBetweenBatchesAsync"/> lets the message loop run.
        /// </summary>
        private async Task<ErrorListSummary> SummarizeDteAsync(int maxItems, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var errors = new List<ErrorListItem>();
            var warnings = new List<ErrorListItem>();
            var errorCount = 0;
            var warningCount = 0;

            var errorItems = ((DTE2)context.Dte).ToolWindows.ErrorList.ErrorItems;
            var count = errorItems.Count;
            for (var index = 1; index <= count; index++)
            {
                try
                {
                    var item = errorItems.Item(index);
                    var severity = ToSeverity(item.ErrorLevel);
                    if (string.IsNullOrWhiteSpace(item.Description)) continue;
                    if (severity == ErrorSeverity.Error)
                    {
                        errorCount++;
                        if (errors.Count < maxItems)
                        {
                            errors.Add(CreateDteItem(item, severity, item.Project));
                        }
                    }
                    else if (severity == ErrorSeverity.Warning)
                    {
                        warningCount++;
                        if (warnings.Count < maxItems)
                        {
                            warnings.Add(CreateDteItem(item, severity, item.Project));
                        }
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // An item that vanished while enumerating is skipped.
                }

                if (index % DteBatchSize == 0 && index < count)
                {
                    await PauseBetweenBatchesAsync(cancellationToken);

                    // The list may have shrunk while the message loop ran; entries that were added meanwhile are not chased.
                    count = Math.Min(count, errorItems.Count);
                }
            }

            return new ErrorListSummary(errorCount, warningCount, TakeErrorsFirst(errors, warnings, maxItems));
        }

        /// <summary>
        /// Gives the UI thread's message loop a real turn between two batches of COM calls, then continues on the UI thread. Re-posting
        /// the continuation to the UI thread right away (<c>SwitchToMainThreadAsync(alwaysYield: true)</c>) would be handled ahead of
        /// pending input again and again, so the thread pool takes a short turn first. Throws when <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        private async Task PauseBetweenBatchesAsync(CancellationToken cancellationToken)
        {
            await TaskScheduler.Default;
            await Task.Delay(DteBatchPause, cancellationToken);
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
        }

        private static ErrorListItem CreateDteItem(ErrorItem item, ErrorSeverity severity, string? project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var line = item.Line;
            var column = item.Column;
            return new ErrorListItem
            {
                Severity = severity,
                Description = item.Description ?? string.Empty,
                File = NullIfEmpty(item.FileName),
                Line = line > 0 ? line : (int?)null,
                Column = column > 0 ? column : (int?)null,
                Project = NullIfEmpty(project),
            };
        }

        private static T? GetValue<T>(ITableEntryHandle entry, string key)
            where T : class
        {
            return entry.TryGetValue(key, out var value) ? value as T : null;
        }

        private static ErrorSeverity ToSeverity(object? category)
        {
            if (category == null)
            {
                return ErrorSeverity.Message;
            }

            switch (Convert.ToInt32(category))
            {
                case (int)__VSERRORCATEGORY.EC_ERROR:
                    return ErrorSeverity.Error;
                case (int)__VSERRORCATEGORY.EC_WARNING:
                    return ErrorSeverity.Warning;
                default:
                    return ErrorSeverity.Message;
            }
        }

        private static ErrorSeverity ToSeverity(vsBuildErrorLevel level)
        {
            switch (level)
            {
                case vsBuildErrorLevel.vsBuildErrorLevelHigh:
                    return ErrorSeverity.Error;
                case vsBuildErrorLevel.vsBuildErrorLevelMedium:
                    return ErrorSeverity.Warning;
                default:
                    return ErrorSeverity.Message;
            }
        }

        /// <summary>Table lines/columns are 0-based (the grid shows +1); anything negative or missing means "no location".</summary>
        private static int? ToOneBased(object? zeroBased)
        {
            if (zeroBased == null)
            {
                return null;
            }

            var value = Convert.ToInt32(zeroBased);
            return value >= 0 ? value + 1 : (int?)null;
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
