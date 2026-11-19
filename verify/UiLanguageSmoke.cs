using System;
using System.IO;
using FmPlayerTalk;

public static class UiLanguageSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-player-talk-ui-language-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "ui-language.txt");
        Require(UiLanguage.ReadSelection(path) == UiLanguageChoice.Auto, "A new profile follows the game language.");
        foreach (var choice in Enum.GetValues<UiLanguageChoice>())
        {
            Require(UiLanguage.TrySaveSelection(path, choice), "Save a supported selection.");
            Require(UiLanguage.ReadSelection(path) == choice, "Restore the chosen selection.");
        }
        Require(!UiLanguage.TrySaveSelection(path, (UiLanguageChoice)int.MaxValue)
            && UiLanguage.ReadSelection(path) == UiLanguageChoice.Korean, "Reject invalid choices without overwriting the profile.");
        File.WriteAllText(path, "Unknown");
        Require(UiLanguage.ReadSelection(path) == UiLanguageChoice.Auto, "Recover from an invalid setting.");
        Require(!UiLanguage.TrySaveSelection(Path.Combine(path, "blocked.txt"), UiLanguageChoice.Japanese),
            "A blocked profile path leaves persistence unavailable without throwing.");
        Require(UiLanguage.MatchGameLanguage("Simplified Chinese") == UiLanguageChoice.Chinese &&
            UiLanguage.MatchGameLanguage("zh-TW") == UiLanguageChoice.Chinese &&
            UiLanguage.MatchGameLanguage("ja-JP") == UiLanguageChoice.Japanese &&
            UiLanguage.MatchGameLanguage("Korean") == UiLanguageChoice.Korean &&
            UiLanguage.MatchGameLanguage("English (US)") == UiLanguageChoice.English &&
            UiLanguage.MatchGameLanguage("Deutsch") == UiLanguageChoice.English, "Resolve native tags and fallback.");
        foreach (var language in new[] { UiLanguageChoice.Chinese, UiLanguageChoice.English, UiLanguageChoice.Japanese, UiLanguageChoice.Korean })
        {
            var draft = UiLanguage.Translate("查找球员", language) + ": Test Player";
            Require(PlayerIntroductionRules.ParseTarget(draft) == "Test Player", "The localized introduction button submits a recognized request.");
            Require(UiLanguage.Translate("Kitahara 조민준", language) == "Kitahara 조민준", "Keep unknown data intact.");
            var message = UiLanguage.Translate("预览「{0}」的历史（{1} 份记录）", language);
            Require(string.Format(message, "SampleSave", 7).Contains("SampleSave"), "Keep values when rendering localized templates.");
            Require(UiLanguage.Translate("发送", language) == (language switch
            {
                UiLanguageChoice.English => "Send",
                UiLanguageChoice.Japanese => "送信",
                UiLanguageChoice.Korean => "보내기",
                _ => "发送"
            }), "Render the expected label for each choice.");
        }
        Console.WriteLine("PASS: UI language selection, persistence, localized commands, and untranslated game content.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
