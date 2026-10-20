using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Pomp.Internal;

namespace Pomp.Hosting
{
    // The composer's command palette lists POMP's own commands and enabled skills under plain names (docs/ui-protocol.md "Slash commands");
    // the engine knows them by their registered names. The text the user typed stays as it is in the conversation; only the prompt that
    // goes to the engine is rewritten, here.
    public sealed partial class PompSession
    {
        /// <summary>
        /// POMP's user-facing commands: the plain name in the palette → the name the POMP extension registers in the engine (the pomp-
        /// prefix keeps them clear of the engine's own commands). Typing the engine name still works; it is just not listed.
        /// <c>goal</c> is the host's own start of a goal run (<see cref="HandleGoalAsync"/>): it runs as the workflow command.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string> PompCommandNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflow"] = "pomp-workflow",
            ["goal"] = "pomp-workflow",
            ["plan"] = "pomp-plan",
            ["map"] = "pomp-map",
            ["review"] = "pomp-review",
            ["max"] = "pomp-max",
            ["rules"] = "pomp-rules",
            ["resources"] = "pomp-resources",
            ["guard"] = "pomp-guard",
            ["vs-status"] = "pomp-context",
            ["skill"] = "pomp-skill",
            ["command"] = "pomp-command",
            ["recipe"] = "pomp-recipe",
        };

        /// <summary>POMP's commands that hand their text to the model: the context basket's checked references go with them.</summary>
        private static readonly HashSet<string> ReferenceCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "pomp-workflow", "pomp-plan", "pomp-map", "pomp-review", "pomp-skill", "pomp-command", "pomp-recipe",
        };

        private static readonly Regex SlashToken = new Regex(@"\A(\s*)/([^\s/]+)(?=\s|\z)", RegexOptions.CultureInvariant);

        /// <summary>
        /// The engine text for what the user typed. A POMP command name becomes its registered name; a plain skill name becomes
        /// <c>/skill:name</c> when the engine lists that skill and no real command has the plain name (a real command always wins).
        /// Anything else, including names typed in their engine form, is returned untouched.
        /// </summary>
        internal string ResolveSlashCommand(string text)
        {
            var match = SlashToken.Match(text);
            if (!match.Success) return text;
            var name = match.Groups[2].Value;
            JArray rows;
            lock (_stateLock) rows = _availableCommands;
            // A command the user (or another extension) defined under the plain name always wins; an engine builtin does not, when POMP's command does the same job.
            var own = false; var builtin = false; var skill = false;
            foreach (var row in rows.OfType<JObject>())
            {
                var known = row.GetString("name");
                if (known == name) { if (row.GetString("source") == "builtin") builtin = true; else own = true; }
                else if (known != null && known.Length == name.Length + 6 && known.StartsWith("skill:", StringComparison.Ordinal) && string.CompareOrdinal(known, 6, name, 0, name.Length) == 0) skill = true;
            }
            string? target = null;
            if (PompCommandNames.TryGetValue(name, out var pomp)) { if (!own) target = pomp; }
            else if (!own && !builtin && skill) target = "skill:" + name;
            return target == null ? text : match.Groups[1].Value + "/" + target + text.Substring(match.Length);
        }

        /// <summary>
        /// The engine text for a slash command with the context basket's checked references (the page sends them only for such a
        /// command, see slashCommands.ts slashTakesReferences): they follow the command's own text when it hands that text to the model
        /// (<see cref="TakesReferences"/>); any other command goes unchanged. A space always follows the name, which the engine reads up to it.
        /// </summary>
        internal string ComposeSlashCommand(string text, IReadOnlyList<string> attachments)
        {
            var resolved = ResolveSlashCommand(text);
            if (attachments.Count == 0) return resolved;
            var match = SlashToken.Match(resolved);
            if (!match.Success || !TakesReferences(match.Groups[2].Value)) return resolved;
            var args = resolved.Substring(match.Length).Trim();
            return "/" + match.Groups[2].Value + " " + (args.Length > 0 ? args + "\n\n" : "\n") + PromptDisplay.CommandReferencesHeader(CanReadBuffer()) + "\n" + string.Join("\n", attachments.Select(path => "- " + path));
        }

        /// <summary>One of POMP's task commands, a skill or a prompt file (by engine name): their text goes to the model.</summary>
        private bool TakesReferences(string name)
        {
            if (ReferenceCommands.Contains(name) || name.StartsWith("skill:", StringComparison.Ordinal)) return true;
            JArray rows;
            lock (_stateLock) rows = _availableCommands;
            return rows.OfType<JObject>().Any(row => row.GetString("name") == name && row.GetString("source") == "file");
        }
    }
}
