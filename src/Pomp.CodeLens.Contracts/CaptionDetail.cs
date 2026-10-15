namespace Pomp.CodeLens.Contracts
{
    /// <summary>Source location only. A details popup never carries source text or authorizes inference.</summary>
    public sealed class CaptionDetail
    {
        public string Path { get; set; } = string.Empty;
        public int Start { get; set; }
        public int Length { get; set; }
    }
}
