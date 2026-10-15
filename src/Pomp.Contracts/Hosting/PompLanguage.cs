using System;
using System.Globalization;
using System.Threading;

namespace Pomp.Hosting
{
    /// <summary>Language of POMP's own texts (chat window, host messages, editor captions, dialogs). Persisted by the options page.</summary>
    public enum PompUiLanguage
    {
        /// <summary>Follow Visual Studio's display language: Korean in a Korean VS, English otherwise.</summary>
        Auto = 0,
        Korean = 1,
        English = 2,
    }

    /// <summary>
    /// The language POMP texts are written in, process-wide. The options page sets <see cref="Choice"/> when its settings are
    /// loaded or applied; everything that picks between a Korean and an English text asks <see cref="IsKorean"/> or
    /// <see cref="Pick"/> at the moment it builds the text. Texts already shown keep their language; the chat page follows
    /// <see cref="Changed"/> through a new host/hello.
    /// </summary>
    public static class PompLanguage
    {
        private static int choice; // PompUiLanguage

        /// <summary>Raised (on the thread that changed <see cref="Choice"/>) when the effective language changed.</summary>
        public static event EventHandler? Changed;

        public static PompUiLanguage Choice
        {
            get => (PompUiLanguage)Volatile.Read(ref choice);
            set
            {
                if (!Enum.IsDefined(typeof(PompUiLanguage), value))
                {
                    value = PompUiLanguage.Auto;
                }

                var before = Locale;
                Interlocked.Exchange(ref choice, (int)value);
                if (!string.Equals(before, Locale, StringComparison.Ordinal))
                {
                    Changed?.Invoke(null, EventArgs.Empty);
                }
            }
        }

        /// <summary>True when POMP texts are Korean.</summary>
        public static bool IsKorean => Choice switch
        {
            PompUiLanguage.Korean => true,
            PompUiLanguage.English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko",
        };

        /// <summary>The locale sent to the chat page (host/hello): a fixed tag for an explicit choice, else VS's UI culture.</summary>
        public static string Locale => Choice switch
        {
            PompUiLanguage.Korean => "ko-KR",
            PompUiLanguage.English => "en-US",
            _ => CultureInfo.CurrentUICulture.Name,
        };

        /// <summary>The Korean or the English text, whichever is in effect.</summary>
        public static string Pick(string korean, string english) => IsKorean ? korean : english;
    }
}
