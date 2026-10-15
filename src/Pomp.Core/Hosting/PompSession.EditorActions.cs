using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Features;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // The editor's POMP menu (explain, review, ask, add file): it fills the chat input through the page, so a message goes the usual way.
    public sealed partial class PompSession
    {
        private ComposerPrefillPayload? _pendingPrefill;
        private DateTime _pendingPrefillAt;
        private WorkOpenPayload? _pendingWorkOpen;
        private DateTime _pendingWorkOpenAt;

        /// <summary>
        /// Puts <paramref name="prefill"/> into the chat input. A page that is not ready yet (the tool window was just opened) gets it with its
        /// <c>ui/ready</c> snapshot, if that comes within a minute.
        /// </summary>
        public void PrefillComposer(ComposerPrefillPayload prefill)
        {
            if (prefill == null) throw new ArgumentNullException(nameof(prefill));
            var bounded = new ComposerPrefillPayload
            {
                Text = prefill.Text == null ? null : prefill.Text.Length <= 4000 ? prefill.Text : prefill.Text.Substring(0, 4000),
                Files = prefill.Files?.Where(file => file != null && file.FullPath.Length > 0 && file.FullPath.Length <= 1024 && file.Path.Length <= 1024).ToList(),
                IncludeEditorContext = prefill.IncludeEditorContext,
                Send = prefill.Send,
                Target = prefill.Target != null && (prefill.Target.Kind == "agent" || prefill.Target.Kind == "team") && prefill.Target.Id.Length > 0 && prefill.Target.Id.Length <= 200
                    ? new ComposerPrefillTarget { Kind = prefill.Target.Kind, Id = prefill.Target.Id } : null,
            };
            bounded.PickTarget = prefill.PickTarget && bounded.Target == null;
            // Parked first, then taken back if the page is ready: the hub's lock (IsReady) is never taken inside the state
            // lock, because the page's ready handler takes the state lock (TakePendingUiRequests) inside the hub's.
            lock (_stateLock)
            {
                _pendingPrefill = bounded;
                _pendingPrefillAt = DateTime.UtcNow;
            }

            if (!_ui.IsReady)
            {
                return;
            }

            lock (_stateLock)
            {
                if (!ReferenceEquals(_pendingPrefill, bounded))
                {
                    return; // A ready snapshot delivered it (or a newer request replaced it).
                }

                _pendingPrefill = null;
            }

            _ui.Post(UiMessageTypes.ComposerPrefill, bounded, UiDelivery.State);
        }

        /// <summary>
        /// The chat input's paste of files or an image (Ctrl+V; the VS side reads the clipboard): the files go into the context basket as they
        /// are, an image is saved as a PNG in the workspace's <c>.pomp/local/clipboard</c> (POMP's own data, kept out of commits) and goes in
        /// as that file. Folders are left out. Returns how many files went in.
        /// </summary>
        public async Task<int> PasteIntoBasketAsync(IReadOnlyList<string> paths, byte[]? png, CancellationToken token)
        {
            var root = await _environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
            var prefix = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            ComposerPrefillFile Basket(string full) => new ComposerPrefillFile
            {
                Path = prefix != null && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length).Replace('\\', '/') : full,
                FullPath = full,
            };
            var files = new List<ComposerPrefillFile>();
            foreach (var path in paths)
            {
                try { var full = Path.GetFullPath(path); if (File.Exists(full)) files.Add(Basket(full)); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException) { }
            }
            if (png != null && png.Length > 0)
            {
                var folder = ProjectData.Folder(string.IsNullOrEmpty(root) ? Path.Combine(_environment.DataDirectory, "workspace") : root!, "clipboard");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "clipboard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                File.WriteAllBytes(file, png);
                files.Add(Basket(file));
            }
            if (files.Count > 0) PrefillComposer(new ComposerPrefillPayload { Files = files });
            return files.Count;
        }

        /// <summary>
        /// Opens a view of the work tab after something the user started outside the page (an inline edit proposal). A page that is
        /// reloading gets it with its <c>ui/ready</c> snapshot, if that comes within a minute.
        /// </summary>
        private void OpenWorkView(string view)
        {
            var open = new WorkOpenPayload { View = view };
            // Same parking order as PrefillComposer.
            lock (_stateLock)
            {
                _pendingWorkOpen = open;
                _pendingWorkOpenAt = DateTime.UtcNow;
            }

            if (!_ui.IsReady)
            {
                return;
            }

            lock (_stateLock)
            {
                if (!ReferenceEquals(_pendingWorkOpen, open))
                {
                    return;
                }

                _pendingWorkOpen = null;
            }

            _ui.Post(UiMessageTypes.WorkOpen, open, UiDelivery.State);
        }

        /// <summary>The requests waiting for the page (each once; dropped when older than a minute): the work view, then the chat input.</summary>
        private IEnumerable<UiMessage> TakePendingUiRequests()
        {
            var messages = new List<UiMessage>();
            lock (_stateLock)
            {
                var now = DateTime.UtcNow;
                if (_pendingWorkOpen != null && now - _pendingWorkOpenAt < TimeSpan.FromMinutes(1)) messages.Add(new UiMessage(UiMessageTypes.WorkOpen, _pendingWorkOpen));
                if (_pendingPrefill != null && now - _pendingPrefillAt < TimeSpan.FromMinutes(1)) messages.Add(new UiMessage(UiMessageTypes.ComposerPrefill, _pendingPrefill));
                _pendingWorkOpen = null;
                _pendingPrefill = null;
            }

            var draft = TakePendingKnowledgeDraft();
            if (draft != null) messages.Add(new UiMessage(UiMessageTypes.KnowledgeDraft, draft));
            return messages;
        }
    }
}
