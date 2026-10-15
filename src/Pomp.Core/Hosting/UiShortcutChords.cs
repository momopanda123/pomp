using System;
using System.Collections.Generic;
using System.Globalization;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>
    /// Keyboard chords of the chat page as written in Tools > Options > Pomp > Shortcuts: optional Ctrl, Alt, Shift and exactly one key
    /// (a letter, a digit, F1-F12, a named key, or for the page one of , . ; '). Mirror of src/Pomp.WebUI/src/shortcuts.ts.
    /// </summary>
    public static class UiShortcutChords
    {
        private static readonly Dictionary<string, string> NamedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = "Enter", ["return"] = "Enter", ["esc"] = "Esc", ["escape"] = "Esc", ["space"] = "Space", ["tab"] = "Tab",
            ["backspace"] = "Backspace", ["delete"] = "Delete", ["del"] = "Delete", ["insert"] = "Insert", ["ins"] = "Insert",
            ["home"] = "Home", ["end"] = "End", ["pageup"] = "PageUp", ["pagedown"] = "PageDown",
            ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        };

        /// <summary>The chord in its canonical form ("Ctrl+Alt+T", "Esc", "F5"), or null when it is empty or malformed.</summary>
        public static string? Normalize(string? text) => Normalize(text, editor: false);

        /// <summary>As <see cref="Normalize(string?)"/>; a Visual Studio command chord may also use the key '/'.</summary>
        private static string? Normalize(string? text, bool editor)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Like the page: empty segments are skipped and at most four remain (three modifiers and the key).
            var parts = new List<string>();
            foreach (var raw in text!.Split('+'))
            {
                if (raw.Trim().Length > 0)
                {
                    parts.Add(raw.Trim());
                }
            }

            if (parts.Count == 0 || parts.Count > 4)
            {
                return null;
            }

            bool ctrl = false, alt = false, shift = false;
            string? key = null;
            foreach (var part in parts)
            {
                if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    ctrl = true;
                }
                else if (part.Equals("alt", StringComparison.OrdinalIgnoreCase))
                {
                    alt = true;
                }
                else if (part.Equals("shift", StringComparison.OrdinalIgnoreCase))
                {
                    shift = true;
                }
                else if (key != null)
                {
                    return null;
                }
                else
                {
                    key = editor && part == "/" ? "/" : !editor && part.Length == 1 && ",.;'".IndexOf(part[0]) >= 0 ? part : NormalizeKey(part);
                    if (key == null)
                    {
                        return null;
                    }
                }
            }

            if (key == null)
            {
                return null;
            }

            return (ctrl ? "Ctrl+" : string.Empty) + (alt ? "Alt+" : string.Empty) + (shift ? "Shift+" : string.Empty) + key;
        }

        /// <summary>
        /// True when a normalized chord can be a page shortcut: it holds Ctrl or Alt, or its key is F1-F12. A bare key or Shift+key
        /// ("Enter", "A", "Shift+D") types into the composer and the fields, so it never answers a card or opens a picker.
        /// </summary>
        public static bool IsPageChord(string? normalized)
        {
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            if (normalized!.StartsWith("Ctrl+", StringComparison.Ordinal) || normalized.StartsWith("Alt+", StringComparison.Ordinal))
            {
                return true;
            }

            var key = normalized.StartsWith("Shift+", StringComparison.Ordinal) ? normalized.Substring(6) : normalized;
            return key.Length >= 2 && key[0] == 'F' && char.IsDigit(key[1]);
        }

        /// <summary>
        /// The shortcuts for the page: each chord is normalized when valid, empty when the value is blank (explicitly none) and the
        /// default otherwise (also when it lacks Ctrl/Alt and is not an F key, see <see cref="IsPageChord"/>); <c>send</c> is "Enter"
        /// or "Ctrl+Enter".
        /// </summary>
        public static UiShortcutsPayload NormalizePayload(string? send, string? sendTargetPicker, string? permissionMode, string? allow, string? deny, string? permissionsView, string? toggleFocus = null,
            string? previousTarget = null, string? nextTarget = null, string? contextBasket = null, string? editorContext = null)
        {
            var defaults = new UiShortcutsPayload();
            return new UiShortcutsPayload
            {
                Send = "Ctrl+Enter".Equals(send?.Trim(), StringComparison.OrdinalIgnoreCase) ? "Ctrl+Enter" : "Enter",
                SendTargetPicker = Chord(sendTargetPicker, defaults.SendTargetPicker),
                PreviousTarget = previousTarget == null ? defaults.PreviousTarget : Chord(previousTarget, defaults.PreviousTarget),
                NextTarget = nextTarget == null ? defaults.NextTarget : Chord(nextTarget, defaults.NextTarget),
                ContextBasket = contextBasket == null ? defaults.ContextBasket : Chord(contextBasket, defaults.ContextBasket),
                EditorContext = editorContext == null ? defaults.EditorContext : Chord(editorContext, defaults.EditorContext),
                PermissionMode = Chord(permissionMode, defaults.PermissionMode),
                Allow = Chord(allow, defaults.Allow),
                Deny = Chord(deny, defaults.Deny),
                PermissionsView = Chord(permissionsView, defaults.PermissionsView),
                ToggleFocus = toggleFocus == null ? defaults.ToggleFocus : Chord(toggleFocus, defaults.ToggleFocus),
            };
        }

        /// <summary>
        /// The Visual Studio command shortcuts (Tools > Options > Pomp > Shortcuts): each chord normalized when valid and usable
        /// (<see cref="IsPageChord"/>), empty when the value is blank (no key) and the default otherwise.
        /// </summary>
        public static EditorShortcutsPayload NormalizeEditorPayload(string? openPomp, string? newConversation, string? explainCode, string? reviewCode, string? fixCode,
            string? writeTests, string? addFileToContext, string? inlineEdit, string? toggleFocus, string? knowledgeTag)
        {
            var defaults = new EditorShortcutsPayload();
            return new EditorShortcutsPayload
            {
                OpenPomp = Chord(openPomp, defaults.OpenPomp),
                NewConversation = Chord(newConversation, defaults.NewConversation),
                ExplainCode = Chord(explainCode, defaults.ExplainCode),
                ReviewCode = Chord(reviewCode, defaults.ReviewCode),
                FixCode = Chord(fixCode, defaults.FixCode),
                WriteTests = Chord(writeTests, defaults.WriteTests),
                AddFileToContext = Chord(addFileToContext, defaults.AddFileToContext),
                InlineEdit = Chord(inlineEdit, defaults.InlineEdit),
                ToggleFocus = Chord(toggleFocus, defaults.ToggleFocus),
                KnowledgeTag = Chord(knowledgeTag, defaults.KnowledgeTag, editor: true),
            };
        }

        private static readonly Dictionary<string, string> VsKeyNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Backspace"] = "Bkspce", ["Delete"] = "Del", ["Insert"] = "Ins", ["PageUp"] = "PgUp", ["PageDown"] = "PgDn",
            ["Up"] = "Up Arrow", ["Down"] = "Down Arrow", ["Left"] = "Left Arrow", ["Right"] = "Right Arrow",
        };

        /// <summary>
        /// A normalized chord as Visual Studio writes a key binding: "scope::Ctrl+Shift+Alt+Key" (the IDE orders the modifiers Ctrl,
        /// Shift, Alt and names some keys differently). Null for an empty chord (no binding).
        /// </summary>
        public static string? VsBinding(string? normalized, string scope)
        {
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            var parts = normalized!.Split('+');
            var key = parts[parts.Length - 1];
            var ctrl = Array.IndexOf(parts, "Ctrl") >= 0;
            var alt = Array.IndexOf(parts, "Alt") >= 0;
            var shift = Array.IndexOf(parts, "Shift") >= 0;
            var vsKey = VsKeyNames.TryGetValue(key, out var named) ? named : key;
            return scope + "::" + (ctrl ? "Ctrl+" : string.Empty) + (shift ? "Shift+" : string.Empty) + (alt ? "Alt+" : string.Empty) + vsKey;
        }

        private static string Chord(string? value, string fallback, bool editor = false)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = Normalize(value, editor);
            return normalized != null && IsPageChord(normalized) ? normalized : fallback;
        }

        private static string? NormalizeKey(string part)
        {
            if (part.Length == 1)
            {
                var c = part[0];
                return c <= 'z' && char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c).ToString() : null;
            }

            if ((part[0] == 'f' || part[0] == 'F') && int.TryParse(part.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number >= 1 && number <= 12 && part.Length <= 3 && part[1] != '0')
            {
                return "F" + number.ToString(CultureInfo.InvariantCulture);
            }

            return NamedKeys.TryGetValue(part, out var named) ? named : null;
        }
    }
}
