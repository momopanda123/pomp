using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Pomp.Diagnostics;
using Pomp.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Buffer reads that see unsaved changes, and hash-validated edits applied as one undo transaction
    /// (design: TextBufferAdapter).
    /// </summary>
    internal sealed partial class TextBufferAdapter : ITextBufferAdapter
    {
        /// <summary>Edit tag so other components can recognise edits made by Pomp.</summary>
        internal static readonly object PompEditTag = new object();

        private readonly VsAdapterContext context;

        public TextBufferAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task<BufferSnapshot?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            var fullPath = VsAdapterContext.NormalizePath(path);
            if (fullPath == null)
            {
                return null;
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var open = TryCaptureOpenBuffer(fullPath);
            if (open != null)
            {
                // The snapshot is immutable, so extracting and hashing a large buffer does not need the UI thread.
                await TaskScheduler.Default;
                cancellationToken.ThrowIfCancellationRequested();

                var openText = open.Snapshot.GetText();
                return new BufferSnapshot
                {
                    Path = fullPath,
                    Text = openText,
                    ContentHash = TextContent.ComputeHash(openText),
                    IsOpenInEditor = open.IsOpenInEditor,
                    IsDirty = open.IsDirty,
                    LineEnding = TextContent.DetectLineEnding(openText),
                };
            }

            // Not open in an editor: the disk content is what the user sees. Do the file I/O off the UI thread.
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(fullPath))
            {
                return null;
            }

            var text = ReadDiskText(fullPath);
            return new BufferSnapshot
            {
                Path = fullPath,
                Text = text,
                ContentHash = TextContent.ComputeHash(text),
                IsOpenInEditor = false,
                IsDirty = false,
                LineEnding = TextContent.DetectLineEnding(text),
            };
        }

        public async Task<BufferEditResult> ApplyEditsAsync(BufferEditRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var fullPath = VsAdapterContext.NormalizePath(request.Path);
            if (fullPath == null)
            {
                return new BufferEditResult { Error = "The path is not valid: '" + request.Path + "'." };
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            try
            {
                if (!IsApprovedWorkspace(request))
                {
                    return WorkspaceChanged();
                }

                var buffer = DocumentTable.TryGetTextBuffer(context, fullPath);
                if (buffer == null)
                {
                    // The file-system check must not run on the UI thread (a UNC path can block for the SMB timeout).
                    await TaskScheduler.Default;
                    var exists = File.Exists(fullPath);
                    await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

                    if (!exists)
                    {
                        return new BufferEditResult { Error = "The file does not exist: " + fullPath };
                    }

                    // Never open a document for an edit approved under a workspace that is no longer the current one.
                    if (!IsApprovedWorkspace(request))
                    {
                        return WorkspaceChanged();
                    }

                    // The contract says an edit target that is not open gets opened in the editor first.
                    OpenEditTarget(fullPath);
                    buffer = DocumentTable.TryGetTextBuffer(context, fullPath);
                    if (buffer == null)
                    {
                        return new BufferEditResult { Error = "The file could not be opened in a text editor: " + fullPath };
                    }
                }

                // Check-and-apply happens in one synchronous block on the UI thread: nothing can edit the buffer in between.
                var before = buffer.CurrentSnapshot;
                var result = Apply(buffer, request);
                if (result.Applied && string.IsNullOrEmpty(result.Error))
                    NotifyApplied(fullPath, buffer, before);
                return result;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                context.LogUnexpected("Applying edits to '" + fullPath + "'", ex);
                return new BufferEditResult { Error = ex.Message };
            }
        }

        /// <summary>The state of an open buffer captured on the UI thread; the text is extracted from the immutable snapshot later.</summary>
        private sealed class OpenBufferCapture
        {
            public OpenBufferCapture(ITextSnapshot snapshot, bool isOpenInEditor, bool isDirty)
            {
                Snapshot = snapshot;
                IsOpenInEditor = isOpenInEditor;
                IsDirty = isDirty;
            }

            public ITextSnapshot Snapshot { get; }

            public bool IsOpenInEditor { get; }

            public bool IsDirty { get; }
        }

        private OpenBufferCapture? TryCaptureOpenBuffer(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var buffer = DocumentTable.TryGetTextBuffer(context, fullPath);
            if (buffer == null)
            {
                return null;
            }

            var snapshot = buffer.CurrentSnapshot;
            var dirty = DocumentTable.IsDirty(context, fullPath);
            if (dirty == null && context.TextDocuments.TryGetTextDocument(buffer, out var textDocument))
            {
                dirty = textDocument.IsDirty;
            }

            return new OpenBufferCapture(snapshot, DocumentTable.FindFrame(context, fullPath) != null, dirty ?? false);
        }

        /// <summary>Largest file the disk fallback reads; bigger files are refused before they are loaded.</summary>
        internal const long MaxDiskFileBytes = 16L * 1024 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly UTF8Encoding LenientUtf8 = new UTF8Encoding(false, false);

        /// <summary>
        /// Reads a file the way Visual Studio shows it when it is not open: byte order marks first, then strict UTF-8, otherwise
        /// the system ANSI code page (Visual Studio's own fallback; think Korean CP949 sources). Runs off the UI thread.
        /// Files above <see cref="MaxDiskFileBytes"/> are refused with an <see cref="IOException"/> before they are read.
        /// </summary>
        internal static string ReadDiskText(string fullPath)
        {
            byte[] bytes;
            var count = 0;

            // Share ReadWrite/Delete: a compiler or another process may hold the file open for writing.
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                var length = stream.Length;
                if (length > MaxDiskFileBytes)
                {
                    throw new IOException("The file is too large to read (" + FormatSize(length) + "; the limit is " + FormatSize(MaxDiskFileBytes) + "): " + fullPath);
                }

                bytes = new byte[(int)length];
                while (count < bytes.Length)
                {
                    var read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0)
                    {
                        break;
                    }

                    count += read;
                }
            }

            return DecodeText(bytes, count);
        }

        /// <summary>The text of <paramref name="count"/> bytes without a byte order mark; the hash is computed over exactly this text.</summary>
        internal static string DecodeText(byte[] bytes, int count)
        {
            if (count >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return Encoding.UTF32.GetString(bytes, 4, count - 4);
            }

            if (count >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                return new UTF32Encoding(true, false).GetString(bytes, 4, count - 4);
            }

            if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return LenientUtf8.GetString(bytes, 3, count - 3);
            }

            if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, count - 2);
            }

            if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, count - 2);
            }

            try
            {
                return StrictUtf8.GetString(bytes, 0, count);
            }
            catch (DecoderFallbackException)
            {
                // Not valid UTF-8 and no byte order mark: an ANSI file. Encoding.Default is the system ANSI code page on .NET Framework.
                return Encoding.Default.GetString(bytes, 0, count);
            }
        }

        private static string FormatSize(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
        }

        /// <summary>Validates and applies the request against <paramref name="buffer"/>. UI thread only.</summary>
        private BufferEditResult Apply(ITextBuffer buffer, BufferEditRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // The decisive check: this block runs synchronously on the UI thread, so no solution switch can happen until it is done.
            if (!IsApprovedWorkspace(request))
            {
                return WorkspaceChanged();
            }

            var snapshot = buffer.CurrentSnapshot;
            var currentText = snapshot.GetText();
            var currentHash = TextContent.ComputeHash(currentText);

            if (!string.Equals(currentHash, request.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return new BufferEditResult
                {
                    Stale = true,
                    NewContentHash = currentHash,
                    Error = "The buffer changed since it was read (expected " + request.ExpectedContentHash + ", now " + currentHash + ").",
                };
            }

            if (request.Edits == null || request.Edits.Count == 0)
            {
                return IsExpectedResult(request, currentHash)
                    ? new BufferEditResult { Applied = true, NewContentHash = currentHash }
                    : UnexpectedResult(currentHash);
            }

            // CheckEditAccess is a thread-ownership check (ITextBuffer.TakeThreadOwnership), not a read-only test: read-only
            // documents and regions are reported by ITextEdit.Replace / HasFailedChanges below.
            if (!buffer.CheckEditAccess())
            {
                return new BufferEditResult { Error = "The buffer cannot be edited from this thread (another thread owns it).", NewContentHash = currentHash };
            }

            var eol = TextContent.DetectLineEnding(currentText);
            var planResult = LineEditPlanner.Plan(currentText, request.Edits, eol, out var plan);
            if (planResult != null)
            {
                return new BufferEditResult { Error = planResult, NewContentHash = currentHash };
            }

            // The caller may have approved a preview of the result: refuse before touching the buffer if this plan differs from it.
            if (!IsExpectedResult(request, TextContent.ComputeHash(LineEditPlanner.ApplyTo(currentText, plan))))
            {
                return UnexpectedResult(currentHash);
            }

            // The buffer's undo manager (created on demand for a buffer that only lives in the Running Document Table) listens to its
            // changes and records them; a bare registry history would stay empty and leave nothing to undo.
            var history = context.UndoManagers.GetTextBufferUndoManager(buffer).TextBufferUndoHistory;

            try
            {
                using (var undo = history.CreateTransaction(string.IsNullOrWhiteSpace(request.UndoDescription) ? "Pomp edit" : request.UndoDescription))
                {
                    using (var edit = buffer.CreateEdit(EditOptions.None, null, PompEditTag))
                    {
                        foreach (var change in plan)
                        {
                            if (!edit.Replace(change.Start, change.Length, change.NewText))
                            {
                                edit.Cancel();
                                return new BufferEditResult
                                {
                                    Error = "Part of the range is read-only; nothing was changed.",
                                    NewContentHash = currentHash,
                                };
                            }
                        }

                        if (edit.HasFailedChanges)
                        {
                            edit.Cancel();
                            return new BufferEditResult { Error = "Some changes could not be applied; nothing was changed.", NewContentHash = currentHash };
                        }

                        edit.Apply();

                        // A Changing handler (source-control checkout, read-only prompt...) can veto the whole edit.
                        if (edit.Canceled && !Changed(buffer, snapshot))
                        {
                            return new BufferEditResult
                            {
                                Error = "Visual Studio cancelled the edit (for example a source-control or read-only check); nothing was changed.",
                                NewContentHash = currentHash,
                            };
                        }
                    }

                    undo.Complete();
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) && Changed(buffer, snapshot))
            {
                // The text already changed (only completing the undo step or an event handler failed): never report "nothing changed".
                context.LogUnexpected("Completing an edit of '" + request.Path + "'", ex);
                string? hash = null;
                try
                {
                    hash = TextContent.ComputeHash(buffer.CurrentSnapshot.GetText());
                }
                catch (Exception)
                {
                    // Reported without a hash: the caller treats the result as unconfirmed.
                }

                return new BufferEditResult { Applied = true, NewContentHash = hash, Error = "The edit was applied, but completing it failed: " + ex.Message };
            }

            var newText = buffer.CurrentSnapshot.GetText();
            return new BufferEditResult { Applied = true, NewContentHash = TextContent.ComputeHash(newText) };
        }

        private void NotifyApplied(string path, ITextBuffer buffer, ITextSnapshot before)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ReferenceEquals(before, buffer.CurrentSnapshot)) return;
            try
            {
                var services = Composition.PompServices.Current;
                if (services == null || (!services.Settings.AppliedLiveFollowEnabled && !services.Settings.AppliedLiveBookmarksEnabled)) return;
                services.RefreshEditorSession();
                services.EditorFollow.Record(path, buffer, before, buffer.CurrentSnapshot);
            }
            catch (Exception ex)
            {
                // An editor decoration failure cannot turn an already committed edit into a failed tool result.
                context.LogUnexpected("Following the applied edit of '" + path + "'", ex);
                if (Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsStatusbar)) is Microsoft.VisualStudio.Shell.Interop.IVsStatusbar status)
                    status.SetText(Pomp.Hosting.PompLanguage.Pick("POMP: 수정은 적용했지만 Live·책갈피 표시를 갱신하지 못했어요: ", "POMP: the edit was applied, but its Live/Bookmark display failed: ") + ex.Message);
            }
        }

        private void OpenEditTarget(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var settings = Composition.PompServices.Current?.Settings;
            // Live keeps the chat focused: open without activation, but as a visible tab, so an edited file never sits in a hidden
            // frame when the follow is skipped (the user is busy in an editor) or superseded by a later edit.
            if (settings?.AppliedLiveFollowEnabled == true || settings?.AppliedLiveBookmarksEnabled == true)
                Editor.EditorFollowService.OpenDocumentNoActivate(context, path, show: true);
            else
                EditorAdapter.OpenDocumentOnUiThread(context, path, null);
        }

        /// <summary>Whether <paramref name="buffer"/> moved past <paramref name="before"/> (only this synchronous UI-thread edit can have done it).</summary>
        private static bool Changed(ITextBuffer buffer, ITextSnapshot before) =>
            buffer.CurrentSnapshot.Version.VersionNumber != before.Version.VersionNumber;

        private bool IsApprovedWorkspace(BufferEditRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return string.IsNullOrEmpty(request.ExpectedWorkspaceRoot) || context.IsCurrentWorkspaceRoot(request.ExpectedWorkspaceRoot!);
        }

        private static BufferEditResult WorkspaceChanged() => new BufferEditResult
        {
            Error = "The Visual Studio workspace changed after the edit was approved; nothing was changed. Read the file again and prepare a new edit.",
        };

        private static bool IsExpectedResult(BufferEditRequest request, string resultHash) =>
            string.IsNullOrEmpty(request.ExpectedResultContentHash)
            || string.Equals(resultHash, request.ExpectedResultContentHash, StringComparison.OrdinalIgnoreCase);

        private static BufferEditResult UnexpectedResult(string currentHash) => new BufferEditResult
        {
            Error = "The editor would produce a different result than the approved change; nothing was changed.",
            NewContentHash = currentHash,
        };
    }
}
