using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.VisualStudio.Interop;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Commands
{
    /// <summary>
    /// Runs the Visual Studio command bound to a key the chat page passed on (<c>ui/vsKey</c>): keys pressed inside the WebView2 never
    /// reach the IDE's key bindings, so F5 in the chat input did nothing. The command is the one bound to the chord in the Global scope
    /// of the current key bindings (Tools > Options > Environment > Keyboard), so a remapped key runs what the user mapped.
    /// <para>
    /// There is no reverse lookup from a key to its command: the bindings of every command are read once, in batches between which the
    /// UI thread handles input, keeping only the chords the page passes on (<see cref="IsForwarded"/>). A key used more than
    /// <see cref="RereadAfter"/> after the last reading has them read again, so a binding changed in the Keyboard options applies from a
    /// following key on; a binding removed meanwhile is checked at once.
    /// </para>
    /// </summary>
    internal sealed class VsGlobalKeys
    {
        private const int BatchSize = 200;
        private static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(15);
        private static readonly TimeSpan RereadAfter = TimeSpan.FromSeconds(30);

        private readonly AsyncPackage package;
        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly IPompLogger logger;
        private Dictionary<string, List<BoundCommand>>? bindings;
        private JoinableTask<Dictionary<string, List<BoundCommand>>>? reading;
        private DateTime readUtc = DateTime.MinValue;

        public VsGlobalKeys(AsyncPackage package, JoinableTaskFactory joinableTaskFactory, IPompLogger logger)
        {
            this.package = package;
            this.joinableTaskFactory = joinableTaskFactory;
            this.logger = logger;
        }

        /// <summary>
        /// The chords the chat page passes on, in the page's canonical form ("Ctrl+Alt+Shift+Key"): F1–F12 and Break with any modifiers,
        /// and Ctrl+Shift+B (Build Solution); not F12 (developer tools), Shift+F10 (a field's context menu) or Alt+F4. Mirror of
        /// <c>vsKeyChord</c> in src/Pomp.WebUI/src/shortcuts.ts.
        /// </summary>
        public static bool IsForwarded(string chord)
        {
            if (chord == "Ctrl+Shift+B") return true;
            if (chord == "F12" || chord == "Shift+F10" || chord == "Alt+F4") return false;
            var key = chord.Substring(chord.LastIndexOf('+') + 1);
            if (key == "Break") return true;
            return key.Length >= 2 && key.Length <= 3 && key[0] == 'F' && int.TryParse(key.Substring(1), out var number) && number >= 1 && number <= 12
                && key[1] != '0';
        }

        /// <summary>Reads the key bindings ahead of the first key, so that one runs without waiting for them.</summary>
        public void Prepare()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (bindings == null) StartReading().Task.Forget(logger, "Reading the Visual Studio key bindings");
        }

        /// <summary>Posts the command bound to <paramref name="chord"/> in the Global scope; false when the key is not bound or its command is unavailable now.</summary>
        public async Task<bool> RunAsync(string chord, CancellationToken cancellationToken)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (!IsForwarded(chord)) return false;
            var map = bindings ?? await StartReading();
            try
            {
                return Post(map, chord);
            }
            finally
            {
                if (DateTime.UtcNow - readUtc > RereadAfter) StartReading().Task.Forget(logger, "Reading the Visual Studio key bindings");
            }
        }

        private JoinableTask<Dictionary<string, List<BoundCommand>>> StartReading()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (reading == null || reading.IsCompleted) reading = joinableTaskFactory.RunAsync(ReadAsync);
            return reading;
        }

        private async Task<Dictionary<string, List<BoundCommand>>> ReadAsync()
        {
            var token = package.DisposalToken;
            await joinableTaskFactory.SwitchToMainThreadAsync(token);
            if (!(await package.GetServiceAsync(typeof(SDTE)) is DTE2 dte)) throw new InvalidOperationException("Visual Studio's DTE is unavailable.");
            var scope = VsKeyBindings.ScopeNames(dte).Global + "::";
            var map = new Dictionary<string, List<BoundCommand>>(StringComparer.Ordinal);
            var commands = dte.Commands;
            var count = commands.Count;
            for (var index = 1; index <= count; index++)
            {
                try
                {
                    var command = commands.Item(index);
                    foreach (var chord in GlobalChords(command, scope))
                    {
                        if (!Guid.TryParse(command.Guid, out var group)) break;
                        if (!map.TryGetValue(chord, out var bound)) map[chord] = bound = new List<BoundCommand>();
                        bound.Add(new BoundCommand(group, command.ID, command.Name));
                    }
                }
                catch (Exception)
                {
                    // A command that cannot be read (a package that failed to load) has no usable binding.
                }

                if (index % BatchSize == 0 && index < count)
                {
                    // A real turn of the message loop between batches: re-posting to the UI thread at once would run ahead of pending input.
                    await TaskScheduler.Default;
                    await Task.Delay(BatchPause, token);
                    await joinableTaskFactory.SwitchToMainThreadAsync(token);
                }
            }

            bindings = map;
            readUtc = DateTime.UtcNow;
            return map;
        }

        /// <summary>The forwarded chords bound to <paramref name="command"/> in the Global scope (single-step chords only).</summary>
        private static IEnumerable<string> GlobalChords(Command command, string scope)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var binding in (command.Bindings as object[] ?? new object[0]).OfType<string>())
            {
                if (!binding.StartsWith(scope, StringComparison.Ordinal)) continue;
                var chord = Canonical(binding.Substring(scope.Length));
                if (chord != null && IsForwarded(chord)) yield return chord;
            }
        }

        /// <summary>
        /// A Visual Studio key ("Ctrl+Shift+Alt+F5") in the page's canonical order ("Ctrl+Alt+Shift+F5"); null for a two-step chord
        /// ("Ctrl+K, Ctrl+C") or one that does not parse.
        /// </summary>
        private static string? Canonical(string vsChord)
        {
            if (vsChord.IndexOf(',') >= 0) return null;
            bool ctrl = false, alt = false, shift = false;
            string? key = null;
            foreach (var raw in vsChord.Split('+'))
            {
                var part = raw.Trim();
                if (part == "Ctrl") ctrl = true;
                else if (part == "Alt") alt = true;
                else if (part == "Shift") shift = true;
                else if (key != null || part.Length == 0) return null;
                else key = part;
            }

            return key == null ? null : (ctrl ? "Ctrl+" : string.Empty) + (alt ? "Alt+" : string.Empty) + (shift ? "Shift+" : string.Empty) + key;
        }

        /// <summary>
        /// Posts the first command still bound to the chord that is available now (the shell routes it like a key binding: the active
        /// window, then the global command targets).
        /// </summary>
        private bool Post(Dictionary<string, List<BoundCommand>> map, string chord)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!map.TryGetValue(chord, out var bound))
            {
                logger.Debug("No Visual Studio command is bound to " + chord + " in the Global scope.");
                return false;
            }

            if (!(Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SDTE)) is DTE2 dte)
                || !(Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell)) return false;
            var scope = VsKeyBindings.ScopeNames(dte).Global + "::";
            foreach (var candidate in bound)
            {
                bool usable;
                try
                {
                    var command = dte.Commands.Item(candidate.Group.ToString("B"), candidate.Id);
                    usable = GlobalChords(command, scope).Contains(chord, StringComparer.Ordinal) && command.IsAvailable;
                }
                catch (Exception) { continue; }
                if (!usable) continue;
                var group = candidate.Group;
                object? argument = null;
                ErrorHandler.ThrowOnFailure(shell.PostExecCommand(ref group, (uint)candidate.Id, 0, ref argument));
                logger.Debug(chord + " in the chat ran " + candidate.Name + ".");
                return true;
            }

            logger.Debug(chord + " in the chat: " + string.Join(", ", bound.Select(item => item.Name)) + " is unavailable now.");
            return false;
        }

        private readonly struct BoundCommand
        {
            public BoundCommand(Guid group, int id, string name)
            {
                Group = group;
                Id = id;
                Name = name;
            }

            public Guid Group { get; }

            public int Id { get; }

            public string Name { get; }
        }
    }
}
