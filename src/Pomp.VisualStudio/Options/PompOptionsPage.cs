using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Pomp.Hosting;
using Pomp.Features;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.VisualStudio.Options
{
    /// <summary>How long Live change marks stay before they are dropped (the editor menu's Clear Changes drops them at any time).</summary>
    public enum LiveChangeRetention { LastRequest, UntilSaved, Conversation }

    /// <summary>Where the Live +/− marks go: after the end of the code line, or in a column left of the code.</summary>
    public enum LiveChangeMarkPosition { Right, Left }

    /// <summary>How the Live comparison shows before and after: one above the other, or side by side.</summary>
    public enum LiveCompareLayout { Vertical, Horizontal }

    internal sealed class PompDisplayNameAttribute : DisplayNameAttribute
    {
        private readonly string korean, english;
        public PompDisplayNameAttribute(string korean, string english) { this.korean = korean; this.english = english; }
        public override string DisplayName => PompLanguage.Pick(korean, english);
    }
    internal sealed class PompCategoryAttribute : CategoryAttribute
    {
        private readonly string korean, english;
        public PompCategoryAttribute(string korean, string english) : base(english) { this.korean = korean; this.english = english; }
        internal string LocalizedName => PompLanguage.Pick(korean, english);
        protected override string GetLocalizedString(string value) => LocalizedName;
    }

    // CategoryAttribute caches its first translation. Property descriptors read the POMP attributes afresh after a language change.
    internal sealed class PompOptionsDescriptionProvider : TypeDescriptionProvider
    {
        public PompOptionsDescriptionProvider(TypeDescriptionProvider parent) : base(parent) { }
        public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object instance) => new Descriptor(base.GetTypeDescriptor(objectType, instance));
        private sealed class Descriptor : CustomTypeDescriptor
        {
            public Descriptor(ICustomTypeDescriptor parent) : base(parent) { }
            public override PropertyDescriptorCollection GetProperties() => Wrap(base.GetProperties());
            public override PropertyDescriptorCollection GetProperties(Attribute[] attributes) => Wrap(base.GetProperties(attributes));
            private static PropertyDescriptorCollection Wrap(PropertyDescriptorCollection properties) => new PropertyDescriptorCollection(properties.Cast<PropertyDescriptor>().Select(p => (PropertyDescriptor)new Property(p)).ToArray(), true);
        }
        private sealed class Property : PropertyDescriptor
        {
            private readonly PropertyDescriptor inner;
            public Property(PropertyDescriptor inner) : base(inner) { this.inner = inner; }
            public override string Category => (Attributes[typeof(PompCategoryAttribute)] as PompCategoryAttribute)?.LocalizedName ?? inner.Category;
            public override string DisplayName => (Attributes[typeof(PompDisplayNameAttribute)] as PompDisplayNameAttribute)?.DisplayName ?? inner.DisplayName;
            public override Type ComponentType => inner.ComponentType;
            public override Type PropertyType => inner.PropertyType;
            public override bool IsReadOnly => inner.IsReadOnly;
            public override bool CanResetValue(object component) => inner.CanResetValue(component);
            public override object GetValue(object component) => inner.GetValue(component);
            public override void ResetValue(object component) => inner.ResetValue(component);
            public override void SetValue(object component, object value) => inner.SetValue(component, value);
            public override bool ShouldSerializeValue(object component) => inner.ShouldSerializeValue(component);
        }
    }

    /// <summary>
    /// Tools &gt; Options &gt; Pomp &gt; General. Visual Studio hands out one page instance per package, and it serves as the
    /// session's <see cref="IPompSettings"/>. The page's own properties are what the Options dialog edits, so they can hold
    /// drafts the user has not applied yet (or will cancel). <see cref="IPompSettings"/> therefore reads an immutable snapshot
    /// of the applied values, taken only when the settings are loaded from or saved to storage.
    /// </summary>
    [Guid(PompGuids.OptionsPageGuidString)]
    [ComVisible(true)]
    public class PompOptionsPage : DialogPage, IPompSettings, IInlineModelSettings, IInlineModelSettingsWriter, IApprovalModeWriter, IUiShortcutSettings, IEditorShortcutSettings, ILocalEmbeddingSettings, IIsolatedExecutionSettings, IInteractiveTerminalSettings, IComputerUseSettings, IKnowledgeSettings, IEditorContextSettings
    {
        static PompOptionsPage() => TypeDescriptor.AddProvider(new PompOptionsDescriptionProvider(TypeDescriptor.GetProvider(typeof(PompOptionsPage))), typeof(PompOptionsPage));

        private static readonly char[] KeySeparators = { ';', ',', '\r', '\n', '\t', ' ' };

        private readonly object gate = new object();
        private string alwaysAllowedPolicyKeysText = string.Empty;
        private AppliedSettings applied;

        public PompOptionsPage()
        {
            // The defaults are in effect until LoadSettingsFromStorage runs.
            applied = AppliedSettings.Capture(this);
        }

        [PompCategory("규칙", "Rules")]
        [PompDisplayName("규칙 파일 열기", "Open rule file in VS editor")]
        [Description("")]
        [Editor(typeof(RuleFileEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string RuleFileToEdit { get; set; } = string.Empty;

        // ---- Agent process ---------------------------------------------------------------------------

        [PompCategory("고급", "Advanced")]
        [PompDisplayName("에이전트 실행 파일 경로", "Agent executable path")]
        [Description("")]
        public string OmpExecutablePath { get; set; } = string.Empty;

        [PompCategory("에이전트", "Agent")]
        [PompDisplayName("승인 모드", "Approval mode")]
        [Description("")]
        [TypeConverter(typeof(ApprovalModeConverter))]
        public OmpApprovalMode ApprovalMode { get; set; } = OmpApprovalMode.AlwaysAsk;

        [PompCategory("에이전트", "Agent")]
        [PompDisplayName("모델", "Model")]
        [Description("")]
        public string Model { get; set; } = string.Empty;

        [PompCategory("에이전트", "Agent")]
        [PompDisplayName("추론 수준", "Thinking level")]
        [Description("")]
        public string ThinkingLevel { get; set; } = string.Empty;

        [PompCategory("고급", "Advanced")]
        [PompDisplayName("추가 실행 인자", "Extra launch arguments")]
        [Description("")]
        public string ExtraArguments { get; set; } = string.Empty;

        [PompCategory("고급", "Advanced")]
        [PompDisplayName("에이전트 질문을 채팅에 표시", "Show agent questions in chat")]
        [Description("")]
        public bool UseRpcUiMode { get; set; }

        [PompCategory("고급", "Advanced")]
        [PompDisplayName("POMP 확장 불러오기", "Load POMP extensions")]
        [Description("")]
        public bool LoadOmpExtensions { get; set; } = true;

        [PompCategory("고급", "Advanced")]
        [PompDisplayName("발견된 확장 불러오기", "Load discovered extensions")]
        [Description("")]
        public bool LoadDiscoveredOmpExtensions { get; set; } = false;

        [PompCategory("에이전트", "Agent")]
        [PompDisplayName("네트워크 도구 차단", "Block network tools")]
        [Description("")]
        public bool BlockOmpNetworkTools { get; set; } = false;

        [PompCategory("에이전트", "Agent")]
        [PompDisplayName("편집 뒤 확인", "Verify after edits")]
        [Description("")]
        public bool VerifyAfterEdits { get; set; } = true;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("자동 인라인 완성 켜기", "Enable automatic inline completions")]
        [Description("")]
        public bool EnableInlineCompletions { get; set; } = false;

        [Browsable(false)]
        public bool InlineCompletionsEnabled => Applied.EnableInlineCompletions;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 수정 따라가기", "Follow Live edits")]
        [Description("")]
        public bool LiveFollowEnabled { get; set; } = false;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 북마크", "Bookmark Live edits")]
        [Description("")]
        public bool LiveBookmarksEnabled { get; set; } = false;

        // How a Live change is drawn: each part on its own; the editor menu's Show Changes (LiveChangesVisible) shows or hides them all.
        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 줄 배경색", "Live change line background")]
        [Description("")]
        public bool LiveChangeBackground { get; set; } = true;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 +/− 표시", "Live change +/− marks")]
        [Description("")]
        public bool LiveChangeSymbols { get; set; } = true;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 +/− 표시 위치", "Live change +/− mark position")]
        [Description("")]
        [TypeConverter(typeof(LiveChangeMarkPositionConverter))]
        public LiveChangeMarkPosition LiveChangeMarkPosition { get; set; } = LiveChangeMarkPosition.Right;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 비교 창 배치", "Live comparison layout")]
        [Description("")]
        [TypeConverter(typeof(LiveCompareLayoutConverter))]
        public LiveCompareLayout LiveCompareLayout { get; set; } = LiveCompareLayout.Vertical;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 내용 표시", "Show Live changes")]
        [Description("")]
        public bool LiveChangesVisible { get; set; } = true;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 내용 유지", "Keep Live changes")]
        [Description("")]
        [TypeConverter(typeof(LiveChangeRetentionConverter))]
        public LiveChangeRetention LiveChangeRetention { get; set; } = LiveChangeRetention.LastRequest;

        /// <summary>Allowed range of <see cref="LiveHistoryLimit"/>; values outside it are clamped when applied.</summary>
        internal const int MinLiveHistoryLimit = 10, MaxLiveHistoryLimit = 100000;

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("Live 변경 기록 수(파일당)", "Live change history per file")]
        [Description("")]
        public int LiveHistoryLimit { get; set; } = 1000;

        [Browsable(false)]
        public bool AppliedLiveFollowEnabled => Applied.LiveFollowEnabled;
        [Browsable(false)]
        public bool AppliedLiveBookmarksEnabled => Applied.LiveBookmarksEnabled;
        [Browsable(false)]
        public bool AppliedLiveChangeBackground => Applied.LiveChangeBackground;
        [Browsable(false)]
        public bool AppliedLiveChangeSymbols => Applied.LiveChangeSymbols;
        [Browsable(false)]
        public LiveChangeMarkPosition AppliedLiveChangeMarkPosition => Applied.LiveChangeMarkPosition;
        [Browsable(false)]
        public LiveCompareLayout AppliedLiveCompareLayout => Applied.LiveCompareLayout;
        [Browsable(false)]
        public bool AppliedLiveChangesVisible => Applied.LiveChangesVisible;
        [Browsable(false)]
        public LiveChangeRetention AppliedLiveChangeRetention => Applied.LiveChangeRetention;
        [Browsable(false)]
        public int AppliedLiveHistoryLimit => Applied.LiveHistoryLimit;

        public void SetLiveEditorFeatures(bool? live = null, bool? bookmarks = null, bool? changesVisible = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (live is bool follow)
                ApplyOneSetting(nameof(LiveFollowEnabled), () => LiveFollowEnabled = follow, current => current.WithLive(follow: follow));
            if (bookmarks is bool marks)
                ApplyOneSetting(nameof(LiveBookmarksEnabled), () => LiveBookmarksEnabled = marks, current => current.WithLive(bookmarks: marks));
            if (changesVisible is bool visible)
                ApplyOneSetting(nameof(LiveChangesVisible), () => LiveChangesVisible = visible, current => current.WithLive(changesVisible: visible));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("보조 모델", "Helper model")]
        [Description("")]
        public string InlineAssistantModel { get; set; } = string.Empty;

        string? IInlineModelSettings.InlineModel => Applied.InlineAssistantModel;

        /// <summary>The on/off editor features switched from the editor's POMP menu: applied and saved at once (see <see cref="ApplyOneSetting"/>).</summary>
        public void SetEditorFeatures(bool? inlineCompletions = null, bool? codeCaptions = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (inlineCompletions is bool completions)
                ApplyOneSetting(nameof(EnableInlineCompletions), () => EnableInlineCompletions = completions, current => current.WithEditor(inlineCompletions: completions));
            if (codeCaptions is bool captions)
                ApplyOneSetting(nameof(EnableCodeCaptions), () => EnableCodeCaptions = captions, current => current.WithEditor(codeCaptions: captions));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The editor model chosen in the chat window: applied and saved at once (see <see cref="ApplyOneSetting"/>).</summary>
        async System.Threading.Tasks.Task IInlineModelSettingsWriter.SetInlineModelAsync(string value, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var model = value ?? string.Empty;
            ApplyOneSetting(nameof(InlineAssistantModel), () => InlineAssistantModel = model, current => current.WithEditor(inlineModel: model));
        }

        /// <summary>
        /// The approval mode chosen in the chat window: applied and saved at once (see <see cref="ApplyOneSetting"/>), then
        /// <see cref="Changed"/> is raised so the services restart omp with the new launch flag.
        /// </summary>
        async System.Threading.Tasks.Task IApprovalModeWriter.SetApprovalModeAsync(OmpApprovalMode mode, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            ApplyOneSetting(nameof(ApprovalMode), () => ApprovalMode = mode, current => current.WithApprovalMode(mode));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Applies and stores one setting changed outside the Options dialog, and nothing else: the dialog may be open with unapplied edits
        /// of other settings, which must neither be saved (<see cref="DialogPage.SaveSettingsToStorage"/> would save them all) nor be
        /// lost. The dialog's copy shows the new value.
        /// </summary>
        private void ApplyOneSetting(string propertyName, Action setDraft, Func<AppliedSettings, AppliedSettings> apply)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var property = TypeDescriptor.GetProperties(AutomationObject)[propertyName];
            lock (gate)
            {
                Volatile.Write(ref applied, apply(applied));
                setDraft();
                if (property != null) SaveSetting(property);
            }
        }

        [PompCategory("편집기", "Editor")]
        [PompDisplayName("POMP 코드 캡션 표시", "Show POMP code captions")]
        [Description("")]
        public bool EnableCodeCaptions { get; set; } = false;

        [Browsable(false)]
        public bool CodeCaptionsEnabled => Applied.EnableCodeCaptions;

#if VS2017
        // VS 2017 has no CodeLens details presenter: the option is kept (settings round-trip) but not shown, and never takes effect.
        [Browsable(false)]
#else
        [PompCategory("편집기", "Editor")]
        [PompDisplayName("캡션에 기본 CodeLens 사용", "Use native CodeLens for POMP captions")]
        [Description("")]
#endif
        public bool UseNativeCodeLensCaptions { get; set; } = false;

        [Browsable(false)]
#if VS2017
        public bool NativeCodeLensCaptionsEnabled => false;
#else
        public bool NativeCodeLensCaptionsEnabled => Applied.EnableCodeCaptions && Applied.UseNativeCodeLensCaptions;
#endif


        [PompCategory("코드 검색", "Code search")]
        [PompDisplayName("로컬 임베딩 켜기", "Enable local embeddings")]
        [Description("")]
        public bool EnableLocalEmbeddings { get; set; } = false;

        [PompCategory("코드 검색", "Code search")]
        [PompDisplayName("로컬 임베딩 주소", "Local embedding endpoint")]
        [Description("")]
        public string LocalEmbeddingEndpoint { get; set; } = "http://127.0.0.1:11434/v1/embeddings";

        [PompCategory("코드 검색", "Code search")]
        [PompDisplayName("로컬 임베딩 모델", "Local embedding model")]
        [Description("")]
        public string LocalEmbeddingModel { get; set; } = string.Empty;

        LocalEmbeddingConfiguration ILocalEmbeddingSettings.EmbeddingConfiguration
        {
            get { var values = Applied; return new LocalEmbeddingConfiguration(values.EnableLocalEmbeddings, values.LocalEmbeddingEndpoint, values.LocalEmbeddingModel); }
        }

        [PompCategory("권한", "Permissions")]
        [PompDisplayName("실험적 AppContainer 실행기 켜기", "Enable experimental AppContainer runner")]
        [Description("")]
        public bool EnableExperimentalIsolation { get; set; } = false;

        bool IIsolatedExecutionSettings.EnableExperimentalIsolation => Applied.EnableExperimentalIsolation;

        [PompCategory("권한", "Permissions")]
        [PompDisplayName("대화형 터미널 켜기", "Enable interactive terminal")]
        [Description("")]
        public bool EnableInteractiveTerminal { get; set; } = false;
        bool IInteractiveTerminalSettings.EnableInteractiveTerminal => Applied.EnableInteractiveTerminal;

        // ---- Computer use (docs/agent-teams.md §4) ---------------------------------------------------

        [PompCategory("컴퓨터 사용", "Computer use")]
        [PompDisplayName("컴퓨터 사용", "Computer use")]
        [Description("")]
        [TypeConverter(typeof(ComputerUsePolicyConverter))]
        public ComputerUsePolicy ComputerUseMode { get; set; } = ComputerUsePolicy.PerConversation;

        [PompCategory("컴퓨터 사용", "Computer use")]
        [PompDisplayName("브라우저 제어", "Browser control")]
        [Description("")]
        public bool BrowserControl { get; set; } = false;

        [PompCategory("컴퓨터 사용", "Computer use")]
        [PompDisplayName("화면 보기도 매번 확인", "Ask before screenshots too")]
        [Description("")]
        public bool ComputerConfirmReads { get; set; } = false;

        ComputerUseOptions IComputerUseSettings.ComputerUse
        {
            get { var values = Applied; return new ComputerUseOptions(values.ComputerUseMode, values.BrowserControl, values.ComputerConfirmReads); }
        }

        // ---- Knowledge (Tools > Knowledge in the chat window) ----------------------------------------

        [PompCategory("지식", "Knowledge")]
        [PompDisplayName("에이전트가 지식 읽기", "Agents read knowledge")]
        [Description("")]
        public bool KnowledgeAgentRead { get; set; } = false;

        [PompCategory("지식", "Knowledge")]
        [PompDisplayName("지식 자동 변환", "Optimize knowledge automatically")]
        [Description("")]
        public bool KnowledgeAutoOptimize { get; set; } = false;

        [PompCategory("지식", "Knowledge")]
        [PompDisplayName("지식 줄 색", "Knowledge line colors")]
        [Description("")]
        public bool KnowledgeLineColors { get; set; } = true;

        bool IKnowledgeSettings.KnowledgeAgentRead => Applied.KnowledgeAgentRead;
        bool IKnowledgeSettings.KnowledgeAutoOptimize => Applied.KnowledgeAutoOptimize;
        bool IKnowledgeSettings.KnowledgeLineColors => Applied.KnowledgeLineColors;

        // ---- Chat ------------------------------------------------------------------------------------

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("언어", "Language")]
        [Description("")]
        [TypeConverter(typeof(LanguageConverter))]
        public PompUiLanguage Language { get; set; } = PompUiLanguage.Auto;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 기본 포함", "Include editor context by default")]
        [Description("")]
        public bool IncludeEditorContextByDefault { get; set; } = true;

        // What the editor context carries (EditorContextLimits clamps every value to its range when the options are applied).

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 커서 위아래 줄 수", "Editor context lines around caret")]
        [Description("")]
        public int EditorContextLines { get; set; } = 30;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 큰 파일 기준 줄 수", "Editor context large file (lines)")]
        [Description("")]
        public int EditorContextLargeFileThreshold { get; set; } = 10000;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 큰 파일 커서 위아래 줄 수", "Editor context lines around caret in a large file")]
        [Description("")]
        public int EditorContextLargeFileLines { get; set; } = 100;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 선택 글 최대 글자 수", "Editor context selected text max characters")]
        [Description("")]
        public int EditorContextSelectedChars { get; set; } = 2000;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 한 줄 최대 글자 수", "Editor context line max characters")]
        [Description("")]
        public int EditorContextLineChars { get; set; } = 300;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 주변 코드 최대 글자 수", "Editor context code max characters")]
        [Description("")]
        public int EditorContextWindowChars { get; set; } = 8000;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 오류 최대 개수", "Editor context max errors")]
        [Description("")]
        public int EditorContextErrors { get; set; } = 10;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("편집기 문맥 VS 응답 대기(초)", "Editor context VS wait (seconds)")]
        [Description("")]
        public int EditorContextTimeoutSeconds { get; set; } = 2;

        EditorContextLimits IEditorContextSettings.EditorContextLimits => Applied.EditorContextLimits;

        [PompCategory("채팅", "Chat")]
        [PompDisplayName("입력 예측 문장", "Composer predictions")]
        [Description("")]
        public bool ComposerPredictions { get; set; } = false;

        // ---- Permissions -----------------------------------------------------------------------------

        // ---- Shortcuts (docs/settings.md "단축키") -------------------------------------------------------

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("보내기", "Send")]
        [Description("")]
        public string ShortcutSend { get; set; } = "Enter";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("보낼 대상 고르기", "Pick send target")]
        [Description("")]
        public string ShortcutSendTargetPicker { get; set; } = "Ctrl+Alt+T";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("이전 보낼 대상", "Previous send target")]
        [Description("")]
        public string ShortcutPreviousTarget { get; set; } = "Alt+,";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("다음 보낼 대상", "Next send target")]
        [Description("")]
        public string ShortcutNextTarget { get; set; } = "Alt+.";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("컨텍스트 바구니", "Context basket")]
        [Description("")]
        public string ShortcutContextBasket { get; set; } = "Alt+;";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("편집기 컨텍스트 포함", "Include editor context")]
        [Description("")]
        public string ShortcutEditorContext { get; set; } = "Alt+'";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("Live 변경 따라가기 전환", "Toggle Live edit following")]
        [Description("")]
        public string ShortcutLiveFollow { get; set; } = "Alt+L";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("POMP 북마크 전환", "Toggle POMP bookmarks")]
        [Description("")]
        public string ShortcutLiveBookmarks { get; set; } = "Alt+K";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("권한 모드", "Permission mode")]
        [Description("")]
        public string ShortcutPermissionMode { get; set; } = "Alt+Q";

        /// <summary>
        /// Which shortcut defaults the stored settings know (0: saved before the permission-mode chord had a default). Not shown; stored
        /// with the other settings on the next Apply.
        /// </summary>
        [Browsable(false)]
        public int ShortcutDefaultsVersion { get; set; }

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("허용", "Allow")]
        [Description("")]
        public string ShortcutAllow { get; set; } = "Alt+A";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("거부", "Deny")]
        [Description("")]
        public string ShortcutDeny { get; set; } = "Alt+D";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("권한 보기", "Permissions view")]
        [Description("")]
        public string ShortcutPermissionsView { get; set; } = "Ctrl+Alt+P";

        // Visual Studio commands (applied to the IDE key bindings by VsKeyBindings; VS orders modifiers Ctrl, Shift, Alt).
        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("POMP 열기", "Open POMP")]
        [Description("")]
        public string ShortcutOpenPomp { get; set; } = "Shift+Alt+Q";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("새 대화", "New conversation")]
        [Description("")]
        public string ShortcutNewConversation { get; set; } = "Shift+Alt+N";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("이 코드 설명", "Explain this code")]
        [Description("")]
        public string ShortcutExplainCode { get; set; } = "Shift+Alt+E";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("이 코드 리뷰", "Review this code")]
        [Description("")]
        public string ShortcutReviewCode { get; set; } = "Shift+Alt+R";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("이 코드 고치기", "Fix this code")]
        [Description("")]
        public string ShortcutFixCode { get; set; } = "Shift+Alt+F";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("테스트 작성", "Write tests")]
        [Description("")]
        public string ShortcutWriteTests { get; set; } = "Shift+Alt+U";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("컨텍스트 바구니에 추가", "Add to context basket")]
        [Description("")]
        public string ShortcutAddFileToContext { get; set; } = "Shift+Alt+I";

        // A new name, so a value saved for the former Alt+K default does not keep the key off its new default.
        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("POMP에게 질문", "Ask POMP")]
        [Description("")]
        public string ShortcutInlinePrompt { get; set; } = "Alt+M";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("지식에 추가", "Add to knowledge")]
        [Description("")]
        public string ShortcutKnowledgeTag { get; set; } = "Alt+/";

        [PompCategory("단축키", "Shortcuts")]
        [PompDisplayName("편집기·채팅 포커스 전환", "Toggle editor and chat focus")]
        [Description("")]
        public string ShortcutToggleFocus { get; set; } = "Alt+J";

        UiShortcutsPayload IUiShortcutSettings.Shortcuts
        {
            get
            {
                var values = Applied;
                return UiShortcutChords.NormalizePayload(values.ShortcutSend, values.ShortcutSendTargetPicker, values.ShortcutPermissionMode, values.ShortcutAllow, values.ShortcutDeny, values.ShortcutPermissionsView, values.ShortcutToggleFocus,
                    values.ShortcutPreviousTarget, values.ShortcutNextTarget, values.ShortcutContextBasket, values.ShortcutEditorContext, values.ShortcutLiveFollow, values.ShortcutLiveBookmarks);
            }
        }

        EditorShortcutsPayload IEditorShortcutSettings.EditorShortcuts
        {
            get
            {
                var values = Applied;
                return UiShortcutChords.NormalizeEditorPayload(values.ShortcutOpenPomp, values.ShortcutNewConversation, values.ShortcutExplainCode, values.ShortcutReviewCode, values.ShortcutFixCode,
                    values.ShortcutWriteTests, values.ShortcutAddFileToContext, values.ShortcutInlinePrompt, values.ShortcutToggleFocus, values.ShortcutKnowledgeTag, values.ShortcutLiveFollow, values.ShortcutLiveBookmarks);
            }
        }

        [PompCategory("권한", "Permissions")]
        [PompDisplayName("항상 허용된 권한", "Always-allowed permissions")]
        [Description("")]
        public string AlwaysAllowedPolicyKeys
        {
            get
            {
                lock (gate)
                {
                    return alwaysAllowedPolicyKeysText;
                }
            }

            set
            {
                lock (gate)
                {
                    alwaysAllowedPolicyKeysText = value ?? string.Empty;
                }
            }
        }

        // ---- IPompSettings ---------------------------------------------------------------------------

        /// <inheritdoc />
        /// <remarks>Raised on the UI thread after the options dialog applied (saved) new values.</remarks>
        public event EventHandler? Changed;

        private AppliedSettings Applied => Volatile.Read(ref applied);

        string? IPompSettings.OmpExecutablePath => Applied.OmpExecutablePath;

        OmpApprovalMode IPompSettings.ApprovalMode => Applied.ApprovalMode;

        string? IPompSettings.Model => Applied.Model;

        string? IPompSettings.ThinkingLevel => Applied.ThinkingLevel;

        string? IPompSettings.ExtraArguments => Applied.ExtraArguments;

        bool IPompSettings.UseRpcUiMode => Applied.UseRpcUiMode;

        bool IPompSettings.LoadOmpExtensions => Applied.LoadOmpExtensions;

        bool IPompSettings.LoadDiscoveredOmpExtensions => Applied.LoadDiscoveredOmpExtensions;

        bool IPompSettings.BlockOmpNetworkTools => Applied.BlockOmpNetworkTools;

        bool IPompSettings.VerifyAfterEdits => Applied.VerifyAfterEdits;

        bool IPompSettings.IncludeEditorContextByDefault => Applied.IncludeEditorContextByDefault;

        bool IPompSettings.ComposerPredictions => Applied.ComposerPredictions;

        IReadOnlyCollection<string> IPompSettings.AlwaysAllowedPolicyKeys => Applied.AlwaysAllowedPolicyKeys;

        /// <summary>
        /// Adds a key to the applied rules and persists that one setting. Does not raise <see cref="Changed"/> (the caller is the
        /// permission gate itself).
        /// </summary>
        void IPompSettings.AddAlwaysAllowed(string policyKey)
        {
            var key = policyKey?.Trim();
            if (key == null || key.Length == 0)
            {
                return;
            }

            lock (gate)
            {
                var current = applied;
                if (current.AlwaysAllowedPolicyKeys.Contains(key, StringComparer.Ordinal))
                {
                    return;
                }

                Volatile.Write(ref applied, current.WithKeys(current.AlwaysAllowedPolicyKeys.Concat(new[] { key }).ToList()));

                // The dialog's copy gets the key too, so applying an open Options dialog afterwards keeps the new rule.
                var draft = ParseKeys(alwaysAllowedPolicyKeysText);
                if (!draft.Contains(key, StringComparer.Ordinal))
                {
                    draft.Add(key);
                    alwaysAllowedPolicyKeysText = FormatKeys(draft);
                }
            }

            PersistPolicyKeysInBackground();
        }

        void IPompSettings.RemoveAlwaysAllowed(string policyKey)
        {
            var key = policyKey?.Trim();
            if (key == null || key.Length == 0)
            {
                return;
            }

            lock (gate)
            {
                var current = applied;
                if (!current.AlwaysAllowedPolicyKeys.Contains(key, StringComparer.Ordinal))
                {
                    return;
                }

                Volatile.Write(ref applied, current.WithKeys(current.AlwaysAllowedPolicyKeys.Where(k => !string.Equals(k, key, StringComparison.Ordinal)).ToList()));

                var draft = ParseKeys(alwaysAllowedPolicyKeysText);
                if (draft.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal)) > 0)
                {
                    alwaysAllowedPolicyKeysText = FormatKeys(draft);
                }
            }

            PersistPolicyKeysInBackground();
        }

        /// <inheritdoc />
        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            // Settings saved while the permission-mode chord had no default keep a blank there: that blank was the old default, not a
            // choice, so it takes the new default (Alt+Q) once.
            if (ShortcutDefaultsVersion < 1)
            {
                if (string.IsNullOrWhiteSpace(ShortcutPermissionMode)) ShortcutPermissionMode = "Alt+Q";
                ShortcutDefaultsVersion = 1;
            }

            TakeAppliedSnapshot();
        }

        /// <inheritdoc />
        public override void SaveSettingsToStorage()
        {
            base.SaveSettingsToStorage();
            TakeAppliedSnapshot();
        }

        /// <inheritdoc />
        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);

            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                // What the dialog shows now is what was applied (the base class saved it).
                TakeAppliedSnapshot();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        private static List<string> ParseKeys(string? text) =>
            (text ?? string.Empty)
                .Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private static string FormatKeys(IEnumerable<string> keys) => string.Join(";", keys);

        private void TakeAppliedSnapshot()
        {
            AppliedSettings snapshot;
            lock (gate)
            {
                snapshot = AppliedSettings.Capture(this);
                Volatile.Write(ref applied, snapshot);
            }

            PompLanguage.Choice = snapshot.Language;
        }

        /// <summary>
        /// Stores the applied always-allow rules, and nothing else: the Options dialog may be open with unapplied edits of this and
        /// every other setting, which must neither be saved (<see cref="DialogPage.SaveSettingsToStorage"/> would save them all)
        /// nor be lost.
        /// </summary>
        private void PersistPolicyKeysInBackground()
        {
            _ = ThreadHelper.JoinableTaskContext.Factory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskContext.Factory.SwitchToMainThreadAsync();
                    var property = TypeDescriptor.GetProperties(AutomationObject)[nameof(AlwaysAllowedPolicyKeys)];
                    if (property == null)
                    {
                        return;
                    }

                    lock (gate)
                    {
                        var draft = alwaysAllowedPolicyKeysText;
                        alwaysAllowedPolicyKeysText = FormatKeys(applied.AlwaysAllowedPolicyKeys);
                        try
                        {
                            SaveSetting(property);
                        }
                        finally
                        {
                            alwaysAllowedPolicyKeysText = draft;
                        }
                    }
                }
                catch (Exception)
                {
                    // Best effort: the applied value stays authoritative for this VS session.
                }
            });
        }

        public sealed class LanguageConverter : EnumConverter
        {
            public LanguageConverter() : base(typeof(PompUiLanguage)) { }

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
            {
                if (destinationType == typeof(string) && value is PompUiLanguage language)
                {
                    if (!Enum.IsDefined(typeof(PompUiLanguage), language)) language = PompUiLanguage.Auto;
                    // DialogPage persists through invariant conversion, not the friendly property-grid labels.
                    if (culture == null || culture.Equals(CultureInfo.InvariantCulture)) return language.ToString();
                    return language switch
                    {
                        PompUiLanguage.Korean => PompLanguage.Pick("한국어", "Korean"),
                        PompUiLanguage.English => PompLanguage.Pick("영어", "English"),
                        _ => PompLanguage.Pick("자동", "Automatic"),
                    };
                }

                return base.ConvertTo(context, culture, value, destinationType);
            }

            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                if (value is string text)
                {
                    text = text.Trim();
                    if (string.Equals(text, nameof(PompUiLanguage.Auto), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "Automatic (Visual Studio language)", StringComparison.OrdinalIgnoreCase) || text == "자동" || text == "Automatic") return PompUiLanguage.Auto;
                    if (string.Equals(text, nameof(PompUiLanguage.Korean), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "한국어 (Korean)", StringComparison.OrdinalIgnoreCase) || text == "한국어") return PompUiLanguage.Korean;
                    if (string.Equals(text, nameof(PompUiLanguage.English), StringComparison.OrdinalIgnoreCase) || text == "영어") return PompUiLanguage.English;
                    // A numeric value (an older store that kept the enum as a number) maps to its member.
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && Enum.IsDefined(typeof(PompUiLanguage), number)) return (PompUiLanguage)number;
                }

                return PompUiLanguage.Auto;
            }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
                new StandardValuesCollection(new[] { PompUiLanguage.Auto, PompUiLanguage.Korean, PompUiLanguage.English });
        }

        /// <summary>Shows the approval modes with the chat chip's labels; the stored value stays the member name.</summary>
        public sealed class ApprovalModeConverter : EnumConverter
        {
            private static readonly (OmpApprovalMode Mode, string Korean, string English)[] Labels =
            {
                (OmpApprovalMode.AlwaysAsk, "항상 확인", "Always ask"),
                (OmpApprovalMode.Write, "쓰기 자동 승인", "Auto-write"),
                (OmpApprovalMode.Yolo, "모두 자동 승인", "Auto-all"),
            };

            public ApprovalModeConverter() : base(typeof(OmpApprovalMode)) { }

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
            {
                if (destinationType == typeof(string) && value is OmpApprovalMode mode)
                {
                    if (!Enum.IsDefined(typeof(OmpApprovalMode), mode)) mode = OmpApprovalMode.AlwaysAsk;
                    // DialogPage persists through invariant conversion, not the friendly property-grid labels.
                    if (culture == null || culture.Equals(CultureInfo.InvariantCulture)) return mode.ToString();
                    foreach (var entry in Labels)
                    {
                        if (entry.Mode == mode) return PompLanguage.Pick(entry.Korean, entry.English);
                    }
                }

                return base.ConvertTo(context, culture, value, destinationType);
            }

            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                if (value is string text)
                {
                    text = text.Trim();
                    foreach (var entry in Labels)
                    {
                        if (string.Equals(text, entry.Mode.ToString(), StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(text, entry.English, StringComparison.OrdinalIgnoreCase) || text == entry.Korean) return entry.Mode;
                    }

                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && Enum.IsDefined(typeof(OmpApprovalMode), number)) return (OmpApprovalMode)number;
                }

                return OmpApprovalMode.AlwaysAsk;
            }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
                new StandardValuesCollection(new[] { OmpApprovalMode.AlwaysAsk, OmpApprovalMode.Write, OmpApprovalMode.Yolo });
        }

        /// <summary>Shows the comparison layouts in the POMP language; the stored value stays the member name.</summary>
        public sealed class LiveCompareLayoutConverter : EnumConverter
        {
            public LiveCompareLayoutConverter() : base(typeof(LiveCompareLayout)) { }

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
            {
                if (destinationType == typeof(string) && value is LiveCompareLayout layout)
                {
                    if (culture == null || culture.Equals(CultureInfo.InvariantCulture)) return layout == LiveCompareLayout.Horizontal ? "Horizontal" : "Vertical";
                    return layout == LiveCompareLayout.Horizontal ? PompLanguage.Pick("가로 (좌우로 나란히)", "Side by side") : PompLanguage.Pick("세로 (위아래로)", "Stacked");
                }
                return base.ConvertTo(context, culture, value, destinationType);
            }

            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                if (value is string text)
                {
                    text = text.Trim();
                    if (text == "가로 (좌우로 나란히)" || string.Equals(text, "Side by side", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Horizontal", StringComparison.OrdinalIgnoreCase)) return LiveCompareLayout.Horizontal;
                }
                return LiveCompareLayout.Vertical;
            }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
                new StandardValuesCollection(new[] { LiveCompareLayout.Vertical, LiveCompareLayout.Horizontal });
        }

        /// <summary>Shows the Live mark positions in the POMP language; the stored value stays the member name.</summary>
        public sealed class LiveChangeMarkPositionConverter : EnumConverter
        {
            public LiveChangeMarkPositionConverter() : base(typeof(LiveChangeMarkPosition)) { }

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
            {
                if (destinationType == typeof(string) && value is LiveChangeMarkPosition position)
                {
                    if (culture == null || culture.Equals(CultureInfo.InvariantCulture)) return position == LiveChangeMarkPosition.Left ? "Left" : "Right";
                    return position == LiveChangeMarkPosition.Left ? PompLanguage.Pick("코드 왼쪽", "Left of the code") : PompLanguage.Pick("코드 오른쪽", "Right of the code");
                }
                return base.ConvertTo(context, culture, value, destinationType);
            }

            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                if (value is string text)
                {
                    text = text.Trim();
                    if (text == "코드 왼쪽" || string.Equals(text, "Left of the code", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Left", StringComparison.OrdinalIgnoreCase)) return LiveChangeMarkPosition.Left;
                }
                return LiveChangeMarkPosition.Right;
            }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
                new StandardValuesCollection(new[] { LiveChangeMarkPosition.Right, LiveChangeMarkPosition.Left });
        }

        /// <summary>Shows the Live change retention choices in the POMP language; the stored value stays the member name.</summary>
        public sealed class LiveChangeRetentionConverter : EnumConverter
        {
            private static readonly (LiveChangeRetention Value, string Korean, string English)[] Labels =
            {
                (LiveChangeRetention.LastRequest, "마지막 수정 요청만", "Last editing request only"),
                (LiveChangeRetention.UntilSaved, "파일을 저장할 때까지", "Until the file is saved"),
                (LiveChangeRetention.Conversation, "대화 동안 모두", "Whole conversation"),
            };

            public LiveChangeRetentionConverter() : base(typeof(LiveChangeRetention)) { }

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
            {
                if (destinationType == typeof(string) && value is LiveChangeRetention retention)
                {
                    if (!Enum.IsDefined(typeof(LiveChangeRetention), retention)) retention = LiveChangeRetention.LastRequest;
                    if (culture == null || culture.Equals(CultureInfo.InvariantCulture)) return retention.ToString();
                    foreach (var entry in Labels)
                        if (entry.Value == retention) return PompLanguage.Pick(entry.Korean, entry.English);
                }
                return base.ConvertTo(context, culture, value, destinationType);
            }

            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                if (value is string text)
                {
                    text = text.Trim();
                    foreach (var entry in Labels)
                        if (string.Equals(text, entry.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(text, entry.English, StringComparison.OrdinalIgnoreCase) || text == entry.Korean) return entry.Value;
                }
                return LiveChangeRetention.LastRequest;
            }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
                new StandardValuesCollection(new[] { LiveChangeRetention.LastRequest, LiveChangeRetention.UntilSaved, LiveChangeRetention.Conversation });
        }

        /// <summary>The settings in effect. Immutable; replaced as a whole.</summary>
        public sealed class ComputerUsePolicyConverter : EnumConverter
        {
            public ComputerUsePolicyConverter() : base(typeof(ComputerUsePolicy)) { }

            public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
            {
                if (destinationType == typeof(string) && value is ComputerUsePolicy policy)
                {
                    if (culture == null || culture.Equals(CultureInfo.InvariantCulture)) return policy.ToString();
                    return policy == ComputerUsePolicy.Off ? PompLanguage.Pick("사용 안 함", "Off") : PompLanguage.Pick("대화별", "Per conversation");
                }
                return base.ConvertTo(context, culture, value, destinationType);
            }

            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            {
                if (value is string text)
                {
                    text = text.Trim();
                    if (text == "사용 안 함" || string.Equals(text, "Off", StringComparison.OrdinalIgnoreCase)) return ComputerUsePolicy.Off;
                    if (text == "대화별" || string.Equals(text, "Per conversation", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "PerConversation", StringComparison.OrdinalIgnoreCase)) return ComputerUsePolicy.PerConversation;
                }
                return base.ConvertFrom(context, culture, value);
            }
        }

        private sealed class AppliedSettings
        {
            private AppliedSettings(PompOptionsPage page)
            {
                OmpExecutablePath = NullIfBlank(page.OmpExecutablePath);
                ApprovalMode = page.ApprovalMode;
                ShortcutSend = page.ShortcutSend;
                ShortcutSendTargetPicker = page.ShortcutSendTargetPicker;
                ShortcutPreviousTarget = page.ShortcutPreviousTarget;
                ShortcutNextTarget = page.ShortcutNextTarget;
                ShortcutContextBasket = page.ShortcutContextBasket;
                ShortcutEditorContext = page.ShortcutEditorContext;
                ShortcutLiveFollow = page.ShortcutLiveFollow;
                ShortcutLiveBookmarks = page.ShortcutLiveBookmarks;
                ShortcutPermissionMode = page.ShortcutPermissionMode;
                ShortcutAllow = page.ShortcutAllow;
                ShortcutDeny = page.ShortcutDeny;
                ShortcutPermissionsView = page.ShortcutPermissionsView;
                ShortcutOpenPomp = page.ShortcutOpenPomp;
                ShortcutNewConversation = page.ShortcutNewConversation;
                ShortcutExplainCode = page.ShortcutExplainCode;
                ShortcutReviewCode = page.ShortcutReviewCode;
                ShortcutFixCode = page.ShortcutFixCode;
                ShortcutWriteTests = page.ShortcutWriteTests;
                ShortcutAddFileToContext = page.ShortcutAddFileToContext;
                ShortcutInlinePrompt = page.ShortcutInlinePrompt;
                ShortcutKnowledgeTag = page.ShortcutKnowledgeTag;
                ShortcutToggleFocus = page.ShortcutToggleFocus;
                Model = NullIfBlank(page.Model);
                ThinkingLevel = NullIfBlank(page.ThinkingLevel);
                ExtraArguments = NullIfBlank(page.ExtraArguments);
                UseRpcUiMode = page.UseRpcUiMode;
                LoadOmpExtensions = page.LoadOmpExtensions;
                LoadDiscoveredOmpExtensions = page.LoadDiscoveredOmpExtensions;
                BlockOmpNetworkTools = page.BlockOmpNetworkTools;
                VerifyAfterEdits = page.VerifyAfterEdits;
                Language = page.Language;
                IncludeEditorContextByDefault = page.IncludeEditorContextByDefault;
                EditorContextLimits = new EditorContextLimits(page.EditorContextLines, page.EditorContextLargeFileLines, page.EditorContextLargeFileThreshold,
                    page.EditorContextSelectedChars, page.EditorContextLineChars, page.EditorContextWindowChars, page.EditorContextErrors, page.EditorContextTimeoutSeconds);
                ComposerPredictions = page.ComposerPredictions;
                EnableCodeCaptions = page.EnableCodeCaptions;
                LiveFollowEnabled = page.LiveFollowEnabled;
                LiveBookmarksEnabled = page.LiveBookmarksEnabled;
                LiveChangeBackground = page.LiveChangeBackground;
                LiveChangeSymbols = page.LiveChangeSymbols;
                LiveChangeMarkPosition = page.LiveChangeMarkPosition == LiveChangeMarkPosition.Left ? LiveChangeMarkPosition.Left : LiveChangeMarkPosition.Right;
                LiveCompareLayout = page.LiveCompareLayout == LiveCompareLayout.Horizontal ? LiveCompareLayout.Horizontal : LiveCompareLayout.Vertical;
                LiveChangeRetention = Enum.IsDefined(typeof(LiveChangeRetention), page.LiveChangeRetention) ? page.LiveChangeRetention : LiveChangeRetention.LastRequest;
                LiveChangesVisible = page.LiveChangesVisible;
                LiveHistoryLimit = Math.Max(MinLiveHistoryLimit, Math.Min(MaxLiveHistoryLimit, page.LiveHistoryLimit));
                UseNativeCodeLensCaptions = page.UseNativeCodeLensCaptions;
                EnableExperimentalIsolation = page.EnableExperimentalIsolation;
                EnableInteractiveTerminal = page.EnableInteractiveTerminal;
                EnableLocalEmbeddings = page.EnableLocalEmbeddings;
                LocalEmbeddingEndpoint = page.LocalEmbeddingEndpoint?.Trim() ?? "";
                LocalEmbeddingModel = page.LocalEmbeddingModel?.Trim() ?? "";
                EnableInlineCompletions = page.EnableInlineCompletions;
                InlineAssistantModel = NullIfBlank(page.InlineAssistantModel);
                ComputerUseMode = page.ComputerUseMode;
                BrowserControl = page.BrowserControl;
                ComputerConfirmReads = page.ComputerConfirmReads;
                KnowledgeAgentRead = page.KnowledgeAgentRead;
                KnowledgeAutoOptimize = page.KnowledgeAutoOptimize;
                KnowledgeLineColors = page.KnowledgeLineColors;
                AlwaysAllowedPolicyKeys = ParseKeys(page.AlwaysAllowedPolicyKeys).AsReadOnly();
            }

            private AppliedSettings(AppliedSettings source, IReadOnlyCollection<string> keys)
            {
                OmpExecutablePath = source.OmpExecutablePath;
                ApprovalMode = source.ApprovalMode;
                ShortcutSend = source.ShortcutSend;
                ShortcutSendTargetPicker = source.ShortcutSendTargetPicker;
                ShortcutPreviousTarget = source.ShortcutPreviousTarget;
                ShortcutNextTarget = source.ShortcutNextTarget;
                ShortcutContextBasket = source.ShortcutContextBasket;
                ShortcutEditorContext = source.ShortcutEditorContext;
                ShortcutLiveFollow = source.ShortcutLiveFollow;
                ShortcutLiveBookmarks = source.ShortcutLiveBookmarks;
                ShortcutPermissionMode = source.ShortcutPermissionMode;
                ShortcutAllow = source.ShortcutAllow;
                ShortcutDeny = source.ShortcutDeny;
                ShortcutPermissionsView = source.ShortcutPermissionsView;
                ShortcutOpenPomp = source.ShortcutOpenPomp;
                ShortcutNewConversation = source.ShortcutNewConversation;
                ShortcutExplainCode = source.ShortcutExplainCode;
                ShortcutReviewCode = source.ShortcutReviewCode;
                ShortcutFixCode = source.ShortcutFixCode;
                ShortcutWriteTests = source.ShortcutWriteTests;
                ShortcutAddFileToContext = source.ShortcutAddFileToContext;
                ShortcutInlinePrompt = source.ShortcutInlinePrompt;
                ShortcutKnowledgeTag = source.ShortcutKnowledgeTag;
                ShortcutToggleFocus = source.ShortcutToggleFocus;
                Model = source.Model;
                ThinkingLevel = source.ThinkingLevel;
                ExtraArguments = source.ExtraArguments;
                UseRpcUiMode = source.UseRpcUiMode;
                LoadOmpExtensions = source.LoadOmpExtensions;
                LoadDiscoveredOmpExtensions = source.LoadDiscoveredOmpExtensions;
                BlockOmpNetworkTools = source.BlockOmpNetworkTools;
                VerifyAfterEdits = source.VerifyAfterEdits;
                Language = source.Language;
                IncludeEditorContextByDefault = source.IncludeEditorContextByDefault;
                EditorContextLimits = source.EditorContextLimits;
                ComposerPredictions = source.ComposerPredictions;
                EnableCodeCaptions = source.EnableCodeCaptions;
                LiveFollowEnabled = source.LiveFollowEnabled;
                LiveBookmarksEnabled = source.LiveBookmarksEnabled;
                LiveChangeBackground = source.LiveChangeBackground;
                LiveChangeSymbols = source.LiveChangeSymbols;
                LiveChangeMarkPosition = source.LiveChangeMarkPosition;
                LiveCompareLayout = source.LiveCompareLayout;
                LiveChangeRetention = source.LiveChangeRetention;
                LiveChangesVisible = source.LiveChangesVisible;
                LiveHistoryLimit = source.LiveHistoryLimit;
                UseNativeCodeLensCaptions = source.UseNativeCodeLensCaptions;
                EnableExperimentalIsolation = source.EnableExperimentalIsolation;
                EnableInteractiveTerminal = source.EnableInteractiveTerminal;
                EnableLocalEmbeddings = source.EnableLocalEmbeddings;
                LocalEmbeddingEndpoint = source.LocalEmbeddingEndpoint;
                LocalEmbeddingModel = source.LocalEmbeddingModel;
                EnableInlineCompletions = source.EnableInlineCompletions;
                InlineAssistantModel = source.InlineAssistantModel;
                ComputerUseMode = source.ComputerUseMode;
                BrowserControl = source.BrowserControl;
                ComputerConfirmReads = source.ComputerConfirmReads;
                KnowledgeAgentRead = source.KnowledgeAgentRead;
                KnowledgeAutoOptimize = source.KnowledgeAutoOptimize;
                KnowledgeLineColors = source.KnowledgeLineColors;
                AlwaysAllowedPolicyKeys = keys;
            }

            public string? OmpExecutablePath { get; }

            public OmpApprovalMode ApprovalMode { get; private set; }

            // Raw chord texts; UiShortcutChords normalizes them when the host reads them.
            public string? ShortcutSend { get; }
            public string? ShortcutSendTargetPicker { get; }
            public string? ShortcutPreviousTarget { get; }
            public string? ShortcutNextTarget { get; }
            public string? ShortcutContextBasket { get; }
            public string? ShortcutEditorContext { get; }
            public string? ShortcutLiveFollow { get; }
            public string? ShortcutLiveBookmarks { get; }
            public string? ShortcutPermissionMode { get; }
            public string? ShortcutAllow { get; }
            public string? ShortcutDeny { get; }
            public string? ShortcutPermissionsView { get; }
            public string? ShortcutOpenPomp { get; }
            public string? ShortcutNewConversation { get; }
            public string? ShortcutExplainCode { get; }
            public string? ShortcutReviewCode { get; }
            public string? ShortcutFixCode { get; }
            public string? ShortcutWriteTests { get; }
            public string? ShortcutAddFileToContext { get; }
            public string? ShortcutInlinePrompt { get; }
            public string? ShortcutKnowledgeTag { get; }
            public string? ShortcutToggleFocus { get; }

            public string? Model { get; }

            public string? ThinkingLevel { get; }

            public string? ExtraArguments { get; }

            public bool UseRpcUiMode { get; }

            public bool LoadOmpExtensions { get; }

            public bool LoadDiscoveredOmpExtensions { get; }

            public bool BlockOmpNetworkTools { get; }

            public bool VerifyAfterEdits { get; }

            public PompUiLanguage Language { get; }

            public bool IncludeEditorContextByDefault { get; }

            public EditorContextLimits EditorContextLimits { get; }

            public bool ComposerPredictions { get; }

            public bool EnableCodeCaptions { get; private set; }
            public bool LiveFollowEnabled { get; private set; }
            public bool LiveBookmarksEnabled { get; private set; }
            public bool LiveChangeBackground { get; }
            public bool LiveChangeSymbols { get; }
            public LiveChangeMarkPosition LiveChangeMarkPosition { get; }
            public LiveCompareLayout LiveCompareLayout { get; }
            public LiveChangeRetention LiveChangeRetention { get; }
            public bool LiveChangesVisible { get; private set; }
            public int LiveHistoryLimit { get; }
            public bool UseNativeCodeLensCaptions { get; }
            public bool EnableExperimentalIsolation { get; }
            public bool EnableInteractiveTerminal { get; }
            public bool EnableLocalEmbeddings { get; }
            public string LocalEmbeddingEndpoint { get; }
            public string LocalEmbeddingModel { get; }

            public bool EnableInlineCompletions { get; private set; }

            public string? InlineAssistantModel { get; private set; }

            public ComputerUsePolicy ComputerUseMode { get; }
            public bool BrowserControl { get; }
            public bool ComputerConfirmReads { get; }

            public bool KnowledgeAgentRead { get; }
            public bool KnowledgeAutoOptimize { get; }
            public bool KnowledgeLineColors { get; }

            public IReadOnlyCollection<string> AlwaysAllowedPolicyKeys { get; }

            public static AppliedSettings Capture(PompOptionsPage page) => new AppliedSettings(page);

            public AppliedSettings WithKeys(List<string> keys) => new AppliedSettings(this, keys.AsReadOnly());

            /// <summary>This snapshot with the approval mode changed outside the Options dialog.</summary>
            public AppliedSettings WithApprovalMode(OmpApprovalMode mode)
            {
                var copy = (AppliedSettings)MemberwiseClone();
                copy.ApprovalMode = mode;
                return copy;
            }

            public AppliedSettings WithLive(bool? follow = null, bool? bookmarks = null, bool? changesVisible = null)
            {
                var copy = (AppliedSettings)MemberwiseClone();
                if (follow.HasValue) copy.LiveFollowEnabled = follow.Value;
                if (bookmarks.HasValue) copy.LiveBookmarksEnabled = bookmarks.Value;
                if (changesVisible.HasValue) copy.LiveChangesVisible = changesVisible.Value;
                return copy;
            }

            /// <summary>This snapshot with editor settings changed outside the Options dialog.</summary>
            public AppliedSettings WithEditor(bool? inlineCompletions = null, bool? codeCaptions = null, string? inlineModel = null)
            {
                var copy = (AppliedSettings)MemberwiseClone();
                if (inlineCompletions != null) copy.EnableInlineCompletions = inlineCompletions.Value;
                if (codeCaptions != null) copy.EnableCodeCaptions = codeCaptions.Value;
                if (inlineModel != null) copy.InlineAssistantModel = NullIfBlank(inlineModel);
                return copy;
            }
        }
    }
}
