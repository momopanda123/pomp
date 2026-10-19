using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>Whether a conversation may switch on omp's computer use (docs/architecture.md §8.5).</summary>
    public enum ComputerUsePolicy
    {
        /// <summary>Off in every new conversation; the user switches it on for one conversation (default).</summary>
        PerConversation,

        /// <summary>Never: omp's computer helpers are denied and the page hides the toggle.</summary>
        Off,
    }

    /// <summary>The options behind computer use: who may switch it on, browser control, and whether inspection asks too.</summary>
    public sealed class ComputerUseOptions
    {
        public static readonly ComputerUseOptions Default = new ComputerUseOptions(ComputerUsePolicy.PerConversation, browser: false, confirmReads: false);

        public ComputerUseOptions(ComputerUsePolicy policy, bool browser, bool confirmReads)
        {
            Policy = policy;
            Browser = browser;
            ConfirmReads = confirmReads;
        }

        public ComputerUsePolicy Policy { get; }

        /// <summary>omp's browser helpers (open and drive web pages); read when omp starts.</summary>
        public bool Browser { get; }

        /// <summary>Screenshots, window lists and accessibility reads ask too (otherwise only input, focus and clipboard writes ask).</summary>
        public bool ConfirmReads { get; }
    }

    /// <summary>Settings that carry <see cref="ComputerUseOptions"/>; a host without them gets <see cref="ComputerUseOptions.Default"/>.</summary>
    public interface IComputerUseSettings
    {
        ComputerUseOptions ComputerUse { get; }
    }

    public static class ComputerUse
    {
        /// <summary>The state file the extension's guard reads (<c>POMP_STATE_DIR</c>); a missing or unreadable file fails closed.</summary>
        public const string StateFileName = "computer-state.json";

        /// <summary>
        /// The approval policy omp gets for its <c>computer</c> helpers (<c>tools.approval.computer</c>). omp asks for a helper call through
        /// its tool UI, which only <c>--mode rpc-ui</c> connects (<paramref name="toolUi"/>, the option "Show agent questions in chat"); under
        /// <c>--mode rpc</c> that question can never be answered and the call fails ("requires approval but no interactive UI is available").
        /// So: <c>deny</c> when computer use is off; with the tool UI, <c>prompt</c> (every call asks) when inspection must ask too, else
        /// omp's tiers (inspection runs, input asks; yolo runs everything); without it, nothing in yolo (every call runs unasked) and
        /// <c>allow</c> in the other modes, where the Eval call that drives the screen asks first (Eval is exec).
        /// </summary>
        public static string? ApprovalPolicy(ComputerUseOptions options, bool yolo, bool toolUi)
        {
            if (options.Policy == ComputerUsePolicy.Off) return "deny";
            if (toolUi) return options.ConfirmReads ? "prompt" : null;
            return yolo ? null : "allow";
        }

        /// <summary>
        /// What omp answered to <c>/computer on|off</c> (its <c>command_output</c> text): true = on, false = off, null = not an answer to it.
        /// <paramref name="unavailable"/> is set when omp refused to switch it on (no Eval runtime, or a platform without the native addon).
        /// </summary>
        public static bool? ParseToggleOutput(string? text, out bool unavailable)
        {
            unavailable = false;
            if (string.IsNullOrEmpty(text)) return null;
            if (text!.StartsWith("Computer use enabled for this session", StringComparison.Ordinal)) return true;
            if (text.StartsWith("Computer use disabled for this session", StringComparison.Ordinal)) return false;
            if (text.StartsWith("Computer use is unavailable", StringComparison.Ordinal)) { unavailable = true; return false; }
            return null;
        }

        /// <summary>True for a typed <c>/computer</c> command (any arguments); the toggle is the page's, so the host refuses those.</summary>
        public static bool IsComputerCommand(string? text)
        {
            var trimmed = (text ?? string.Empty).TrimStart();
            try { trimmed = trimmed.Normalize(System.Text.NormalizationForm.FormKC).TrimStart(); }
            catch (ArgumentException) { /* Invalid Unicode still uses the conservative command boundary. */ }
            return trimmed.StartsWith("/computer", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 9 || !(char.IsLetterOrDigit(trimmed[9]) || trimmed[9] == '-' || trimmed[9] == '_'));
        }

        /// <summary>Writes the conversation's state for the extension's guard (atomic: the guard never reads half a file).</summary>
        public static void WriteState(string stateDirectory, string? sessionId, bool desktop, bool browser)
        {
            var state = new JObject
            {
                ["sessionId"] = sessionId,
                ["desktop"] = desktop,
                ["browser"] = browser,
                ["updatedAt"] = DateTime.UtcNow.ToString("o"),
            };
            AtomicFile.WriteAllText(Path.Combine(stateDirectory, StateFileName), state.ToString());
        }
    }
}
