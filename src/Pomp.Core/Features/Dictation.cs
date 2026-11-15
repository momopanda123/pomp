using System;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>The on-device speech model voice input runs with (docs/architecture.md "음성 입력"): a size against accuracy trade-off.</summary>
    public enum DictationModel
    {
        /// <summary>Whisper small (default): about 190 MB.</summary>
        Balanced,

        /// <summary>Whisper base: about 60 MB, the quickest and the least accurate.</summary>
        Fast,

        /// <summary>Whisper large-v3-turbo: about 600 MB, the most accurate and the slowest.</summary>
        Accurate,
    }

    /// <summary>The options behind voice input: the speech model and the spoken language.</summary>
    public sealed class DictationOptions
    {
        public static readonly DictationOptions Default = new DictationOptions(DictationModel.Balanced, null);

        public DictationOptions(DictationModel model, string? language)
        {
            Model = model;
            Language = Dictation.NormalizeLanguage(language);
        }

        public DictationModel Model { get; }

        /// <summary>A two- or three-letter language code ("ko", "en", "ja"), "auto" (the model detects it), or null: POMP's display language.</summary>
        public string? Language { get; }
    }

    /// <summary>Settings that carry <see cref="DictationOptions"/>; a host without them gets <see cref="DictationOptions.Default"/>.</summary>
    public interface IDictationSettings
    {
        DictationOptions Dictation { get; }
    }

    public static class Dictation
    {
        public const string AutomaticLanguage = "auto";

        /// <summary>The language as the options keep it: lower-case code or "auto"; null for blank or anything else.</summary>
        public static string? NormalizeLanguage(string? language)
        {
            var text = language?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text)) return null;
            if (text == AutomaticLanguage) return text;
            if (text!.Length < 2 || text.Length > 3) return null;
            foreach (var c in text) if (c < 'a' || c > 'z') return null;
            return text;
        }

        /// <summary>omp's <c>modelRoles.dictation</c> value: the model's entry in omp's keyless on-device speech catalog.</summary>
        public static string EngineModel(DictationModel model)
        {
            switch (model)
            {
                case DictationModel.Fast: return "local/whisper-base";
                case DictationModel.Accurate: return "local/whisper-large-v3-turbo";
                default: return "local/whisper-small";
            }
        }

        /// <summary>omp's <c>stt.language</c>: a language code, or empty for the model's own detection.</summary>
        public static string EngineLanguage(DictationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Language == AutomaticLanguage) return string.Empty;
            return options.Language ?? (PompLanguage.IsKorean ? "ko" : "en");
        }

        /// <summary>
        /// What the user is told for a failure omp reported (English engine text): the known causes in POMP's language, anything else as omp
        /// said it (one bounded line).
        /// </summary>
        public static string FailureMessage(string? engineError)
        {
            var error = (engineError ?? string.Empty).Trim();
            if (error.StartsWith("Failed to open the default microphone", StringComparison.Ordinal))
                return PompLanguage.Pick("마이크를 찾을 수 없어요. 마이크 연결과 Windows의 마이크 접근 권한을 확인해 주세요.", "No microphone was found. Check that one is connected and that Windows allows desktop apps to use it.");
            if (error.IndexOf("Failed to download speech model", StringComparison.Ordinal) >= 0 || error.IndexOf("Failed to install", StringComparison.OrdinalIgnoreCase) >= 0)
                return PompLanguage.Pick("음성 모델을 내려받지 못했어요. 처음 쓸 때는 인터넷 연결이 필요해요.", "The speech model could not be downloaded. The first use needs an internet connection.");
            if (error.StartsWith("Speech-to-text is disabled", StringComparison.Ordinal) || error.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase))
                return PompLanguage.Pick("이 엔진은 음성 입력을 지원하지 않아요.", "This engine does not support voice input.");
            if (error.StartsWith("End live voice", StringComparison.Ordinal) || error.StartsWith("Session is changing", StringComparison.Ordinal) || error.StartsWith("A dictation capture is already active", StringComparison.Ordinal))
                return PompLanguage.Pick("지금은 음성 입력을 시작할 수 없어요. 잠시 뒤에 다시 해 주세요.", "Voice input cannot start right now. Try again in a moment.");
            if (error.Length == 0) return PompLanguage.Pick("음성 입력에 실패했어요.", "Voice input failed.");
            var line = error.Replace('\r', ' ').Replace('\n', ' ');
            return line.Length <= 300 ? line : line.Substring(0, 300);
        }
    }
}
