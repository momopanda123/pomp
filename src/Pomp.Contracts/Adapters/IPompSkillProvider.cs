using System.Collections.Generic;
namespace Pomp.Adapters
{
    /// <summary>Optional MEF contract for already installed VSIX packages. Export this interface from a thread-safe provider.
    /// Return metadata only; POMP loads Markdown lazily from the provider assembly's directory. No scripts are executed.</summary>
    public interface IPompSkillProvider
    {
        IEnumerable<PompSkillDefinition> GetSkills();
    }
    public sealed class PompSkillDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        /// <summary>Relative .md file below the directory containing this provider's assembly. No absolute paths, links or traversal.</summary>
        public string RelativePath { get; set; } = string.Empty;
    }
}
