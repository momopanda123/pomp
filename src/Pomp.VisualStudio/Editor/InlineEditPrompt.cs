using System;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Pomp.Hosting;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>What the POMP prompt at the selection does with the text.</summary>
    internal enum PromptAction
    {
        /// <summary>A review proposal that changes the selection (the inline edit).</summary>
        Edit,

        /// <summary>A chat message about the selection, sent at once with the editor context.</summary>
        Ask,

        /// <summary>The text goes into the chat input (editor context on), to be continued there.</summary>
        ToChatInput,
    }

    internal sealed class PromptResult
    {
        public PromptResult(string text, PromptAction action) { Text = text; Action = action; }

        public string Text { get; }

        public PromptAction Action { get; }
    }

    /// <summary>
    /// The POMP prompt at the selection or the caret (Pomp.InlineEdit: main action "ask in chat", other action "propose an edit").
    /// Enter runs the main action, Ctrl+Enter the other one, Shift+Enter moves the text into the chat input, Esc cancels; Enter while
    /// an IME composes is the IME's. It is a small modal VS dialog next to the selection, not a popup over the editor: while a text
    /// view is the active pane, Visual Studio turns Enter, Escape, Backspace, Tab and the arrows into editor commands before a popup's
    /// text box sees them (Enter replaced the selection with a line break). A modal dialog suspends that. It is drawn in the current VS
    /// theme's colors: a separate window does not see the shell's theme resources (a resource reference stayed white in the dark theme),
    /// so the colors are read once from VSColorTheme. The dialog frame VS adds to a DialogWindow (WS_EX_DLGMODALFRAME, a thick light
    /// frame drawn by DWM that ignores the theme) is removed once the window exists; only the 1 px themed border remains.
    /// </summary>
    internal sealed class InlineEditPrompt : DialogWindow
    {
        private readonly TextBox input;
        private PromptAction chosen;

        private InlineEditPrompt(PromptAction main, bool canEdit)
        {
            var other = main == PromptAction.Edit ? PromptAction.Ask : PromptAction.Edit;
            Title = PompLanguage.Pick("POMP에게 질문", "Ask POMP");
            WindowStyle = WindowStyle.None;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            HasMaximizeButton = false;
            HasMinimizeButton = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Themed(EnvironmentColors.ToolWindowBackgroundColorKey);
            Foreground = Themed(EnvironmentColors.ToolWindowTextColorKey);
            BorderBrush = Themed(EnvironmentColors.ToolWindowBorderColorKey);
            BorderThickness = new Thickness(1);

            var title = new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 0) };
            input = new TextBox
            {
                MinWidth = 420, MaxWidth = 600, MaxLength = 2000, Margin = new Thickness(8, 6, 8, 4), Padding = new Thickness(3, 2, 3, 2),
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = false,
                Background = Themed(CommonControlsColors.TextBoxBackgroundColorKey), Foreground = Themed(CommonControlsColors.TextBoxTextColorKey),
                CaretBrush = Themed(CommonControlsColors.TextBoxTextColorKey), BorderBrush = Themed(CommonControlsColors.TextBoxBorderFocusedColorKey),
                SelectionBrush = Themed(CommonControlsColors.ButtonBorderFocusedColorKey),
            };
            System.Windows.Automation.AutomationProperties.SetName(input, Title);
            var style = ButtonStyle();
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 4, 8), MaxWidth = 600 };
            Button AddAction(PromptAction action, string keys, bool primary)
            {
                var label = action == PromptAction.Edit ? PompLanguage.Pick("코드 수정 제안", "Propose an edit")
                    : action == PromptAction.Ask ? PompLanguage.Pick("채팅으로 질문", "Ask in chat")
                    : PompLanguage.Pick("채팅 입력창으로", "Move to chat input");
                var button = new Button { Content = label, MinWidth = 96, Margin = new Thickness(0, 0, 6, 4), Style = style, Tag = primary ? "primary" : null };
                // The key goes to screen readers only; the dialog shows no shortcut text.
                System.Windows.Automation.AutomationProperties.SetAcceleratorKey(button, keys);
                button.Click += (_, __) => Choose(action);
                if (action == PromptAction.Edit && !canEdit) button.IsEnabled = false;

                buttons.Children.Add(button);
                return button;
            }

            // A fixed order whatever the main action is: ask in chat, move to the chat input, propose an edit, cancel.
            AddAction(PromptAction.Ask, main == PromptAction.Ask ? "Enter" : "Ctrl+Enter", primary: main == PromptAction.Ask);
            AddAction(PromptAction.ToChatInput, "Shift+Enter", primary: false);
            AddAction(PromptAction.Edit, main == PromptAction.Edit ? "Enter" : "Ctrl+Enter", primary: main == PromptAction.Edit);
            var cancel = new Button { Content = PompLanguage.Pick("취소", "Cancel"), IsCancel = true, MinWidth = 72, Margin = new Thickness(0, 0, 6, 4), Style = style };
            System.Windows.Automation.AutomationProperties.SetAcceleratorKey(cancel, "Esc");
            buttons.Children.Add(cancel);
            PreviewKeyDown += (_, e) =>
            {
                // Enter while an IME composes arrives as ImeProcessed and is left to the IME.
                if (e.Key != Key.Enter) return;
                var modifiers = Keyboard.Modifiers;
                var action = modifiers == ModifierKeys.None ? main : modifiers == ModifierKeys.Control ? other : modifiers == ModifierKeys.Shift ? PromptAction.ToChatInput : (PromptAction?)null;
                e.Handled = true;
                if (action != null) Choose(action.Value);
            };

            var panel = new StackPanel();
            panel.Children.Add(title);
            panel.Children.Add(input);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, __) => { input.Focus(); Keyboard.Focus(input); };
            // A borderless window has no title bar to drag: the panel's empty parts move it.
            MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is StackPanel || e.OriginalSource is TextBlock) DragMove(); };

            void Choose(PromptAction action)
            {
                if (string.IsNullOrWhiteSpace(input.Text) || (action == PromptAction.Edit && !canEdit)) { input.Focus(); return; }
                chosen = action;
                DialogResult = true;
            }
        }

        /// <summary>
        /// Asks for the text and what to do with it; null when cancelled. <paramref name="anchor"/> is in screen pixels (below the caret
        /// line). <paramref name="canEdit"/> false disables "propose an edit" (the document or the selections are too large or unsaved).
        /// </summary>
        public static PromptResult? Ask(IWpfTextView view, Point anchor, PromptAction main, bool canEdit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var prompt = new InlineEditPrompt(main, canEdit);
            var source = PresentationSource.FromVisual(view.VisualElement);
            var place = source?.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.Transform(anchor) : anchor;
            // Keep the dialog on the work area; its size is known only after layout, so this uses a generous estimate.
            var area = SystemParameters.WorkArea;
            prompt.Left = Math.Max(area.Left, Math.Min(place.X, area.Right - 640));
            prompt.Top = Math.Max(area.Top, Math.Min(place.Y + 4, area.Bottom - 200));
            return prompt.ShowModal() == true ? new PromptResult(prompt.input.Text.Trim(), prompt.chosen) : null;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        /// <summary>Removes the non-client dialog frame (see the class summary) and colors the 1 px border Windows 11 draws itself like the theme.</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
            const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000;
            const int WS_EX_DLGMODALFRAME = 0x00000001, WS_EX_WINDOWEDGE = 0x00000100, WS_EX_CLIENTEDGE = 0x00000200, WS_EX_STATICEDGE = 0x00020000;
            const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;
            SetWindowLong(handle, GWL_STYLE, GetWindowLong(handle, GWL_STYLE) & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU));
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) & ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE));
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
            try
            {
                // DWMWA_BORDER_COLOR (Windows 11; older Windows returns an error, which is fine): a COLORREF, 0x00BBGGRR.
                var color = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBorderColorKey);
                var border = color.R | (color.G << 8) | (color.B << 16);
                DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
            }
        }

        private static SolidColorBrush Themed(ThemeResourceKey key)
        {
            var color = VSColorTheme.GetThemedColor(key);
            var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        /// <summary>A flat button in the theme's colors (the default WPF button keeps its light hover look in a dark theme).</summary>
        private static Style ButtonStyle()
        {
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.BackgroundProperty, Themed(CommonControlsColors.ButtonColorKey));
            border.SetValue(Border.BorderBrushProperty, Themed(CommonControlsColors.ButtonBorderColorKey));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            border.SetValue(Border.PaddingProperty, new Thickness(10, 3, 10, 3));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var defaulted = new Trigger { Property = FrameworkElement.TagProperty, Value = "primary" };
            defaulted.Setters.Add(new Setter(Border.BorderBrushProperty, Themed(CommonControlsColors.ButtonBorderDefaultColorKey), "border"));
            template.Triggers.Add(defaulted);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Themed(CommonControlsColors.ButtonHoverColorKey), "border"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, Themed(CommonControlsColors.ButtonBorderHoverColorKey), "border"));
            template.Triggers.Add(hover);
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, Themed(CommonControlsColors.ButtonBorderFocusedColorKey), "border"));
            template.Triggers.Add(focused);
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Themed(CommonControlsColors.ButtonPressedColorKey), "border"));
            template.Triggers.Add(pressed);
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Themed(CommonControlsColors.ButtonTextColorKey)));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            return style;
        }
    }
}
