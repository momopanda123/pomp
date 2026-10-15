using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.Features
{
    /// <summary>One knowledge (a user tag) as the editor's "Add to Knowledge" submenu lists it.</summary>
    public sealed class KnowledgeTagInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary><c>#RRGGBB</c>.</summary>
        public string Color { get; set; } = string.Empty;
    }

    /// <summary>Lines of a source file on their way into a knowledge (1-based, inclusive).</summary>
    public sealed class KnowledgeDraft
    {
        public string FullPath { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        /// <summary>The text of those lines as the editor shows them now.</summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>A saved piece of a knowledge that is anchored to lines of one file, for the editor's line colors.</summary>
    public sealed class KnowledgeLineMark
    {
        public string KnowledgeId { get; set; } = string.Empty;
        public string PieceId { get; set; } = string.Empty;
        /// <summary><c>#RRGGBB</c> of the knowledge.</summary>
        public string Color { get; set; } = string.Empty;
        /// <summary>Where the lines were last seen (1-based, inclusive); the editor looks there first.</summary>
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        /// <summary>The saved lines and their <see cref="KnowledgeText.Hash"/>, to find them again after edits.</summary>
        public string Text { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    /// <summary>What the Visual Studio editor needs from the knowledge of the open workspace (implemented by the session).</summary>
    public interface IKnowledgeEditor
    {
        /// <summary>Every knowledge of the workspace, by name; empty without a workspace or when the store cannot be read.</summary>
        IReadOnlyList<KnowledgeTagInfo> ListKnowledge();

        /// <summary>
        /// Adds the lines to that knowledge as a piece anchored to them (a duplicate of a saved piece adds nothing). Throws
        /// <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/> with a message for the user.
        /// </summary>
        Task AddKnowledgeLinesAsync(string knowledgeId, KnowledgeDraft lines, CancellationToken cancellationToken);

        /// <summary>Opens the page's new-knowledge form with these lines as its first piece (Tools › Knowledge).</summary>
        void OpenKnowledgeDraft(KnowledgeDraft lines);

        /// <summary>The saved anchored pieces in one file (full path); empty when there are none or the store cannot be read.</summary>
        IReadOnlyList<KnowledgeLineMark> KnowledgeLines(string fullPath);

        /// <summary>Raised after any change of the knowledge (any thread).</summary>
        event EventHandler? KnowledgeChanged;
    }

    /// <summary>The knowledge options of Tools › Options › Pomp (implemented by the options page).</summary>
    public interface IKnowledgeSettings
    {
        /// <summary>Agents may list and read project knowledge on their own; what they read stays selected for the conversation.</summary>
        bool KnowledgeAgentRead { get; }

        /// <summary>A knowledge that is used in its optimized form is optimized again when its pieces change.</summary>
        bool KnowledgeAutoOptimize { get; }

        /// <summary>The editor colors the lines of anchored pieces with their knowledge's color.</summary>
        bool KnowledgeLineColors { get; }
    }

    /// <summary>How lines are compared: shared by the store (duplicates, anchors) and the editor (finding moved lines).</summary>
    public static class KnowledgeText
    {
        /// <summary>LF line breaks, no trailing white space on any line, no leading or trailing blank lines.</summary>
        public static string Normalize(string? text)
        {
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.TrimEnd());
            return string.Join("\n", lines).Trim('\n');
        }

        /// <summary>Lowercase hex SHA-256 of <see cref="Normalize"/>.</summary>
        public static string Hash(string? text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(text)));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }
}
