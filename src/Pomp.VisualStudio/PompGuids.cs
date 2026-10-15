using System;

namespace Pomp.VisualStudio
{
    /// <summary>GUIDs shared by the package attributes, the VSCT file and the command wiring.</summary>
    internal static class PompGuids
    {
        /// <summary>Package GUID; must equal <c>guidPompPackage</c> in Pomp.vsct.</summary>
        public const string PackageGuidString = "02f7219a-95d5-4512-bade-dc77eb05f090";

        /// <summary>Command set GUID; must equal <c>guidPompCmdSet</c> in Pomp.vsct.</summary>
        public const string CommandSetGuidString = "c5eb69b1-9b4c-4566-b74f-30adaf961614";

        public const string ToolWindowGuidString = "4eafd046-a7af-4046-bddb-e2703e65731f";

        public const string OptionsPageGuidString = "2bc18b3b-ff11-4062-83ca-4625528c3e9d";

        public static readonly Guid CommandSet = new Guid(CommandSetGuidString);
    }

    /// <summary>Command IDs; must match the IDSymbols of <c>guidPompCmdSet</c> in Pomp.vsct.</summary>
    internal static class PompCommandIds
    {
        public const int ShowChatView = 0x0100;

        public const int ShowChatTools = 0x0101;

        public const int NewSession = 0x0102;

        public const int ToggleFocus = 0x0103;
        public const int InlineEdit = 0x0104;

        // The code editor's POMP menu (also in Tools > POMP for the toggles).
        public const int ExplainCode = 0x0105;
        public const int ReviewCode = 0x0106;
        public const int AddFileToContext = 0x0108;
        public const int ToggleInlineCompletions = 0x0109;
        public const int ToggleCodeCaptions = 0x010A;
        public const int FixCode = 0x010B;
        public const int WriteTests = 0x010C;
        // The editor menu's "add to knowledge": the selected lines go to the chat window's knowledge form.
        public const int KnowledgeTag = 0x010E;

        // The editor menu's "to agent or team" submenu and its dynamic items (DynamicItemStart: one id per entry, every id from this start on, so it stays above all other ids).
        public const int SendToTargetMenu = 0x1102;
        public const int SendToTargetStart = 0x2000;
    }
}
