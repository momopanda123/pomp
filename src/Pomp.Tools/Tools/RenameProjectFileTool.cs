namespace Pomp.Tools
{
    /// <summary>Renames one approved, identity-bound project item through Visual Studio's project system.</summary>
    internal sealed class RenameProjectFileTool : ProjectItemChangeTool
    {
        public RenameProjectFileTool(HostToolContext context)
            : base(context, CreateDefinition(), rename: true)
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.RenameProjectFile,
                Label = "Rename Project File",
                Description = "Rename one file in its existing folder through Visual Studio's project system after approval of its exact source and destination. " +
                    "Only files of exactly one loaded C# or VB MSBuild project are supported. Existing destinations are never overwritten. " +
                    "newName must be a plain file name, not a path; reserved device names, leading/trailing whitespace and trailing dots are refused. " +
                    "Case-only renames, linked files outside the project folder, grouped/dependent items and dirty documents are refused. " +
                    "File and project identities are rechecked after approval. Inspect reported partial states before retrying.",
                Parameters = new SchemaBuilder()
                    .String("path", "Workspace-relative or absolute path of a file that belongs to a loaded project.", required: true)
                    .String("newName", "New file name only (no folder), e.g. OrderService.cs.", required: true)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }
    }
}
