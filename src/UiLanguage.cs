using System;
using System.IO;
using System.Linq;

namespace FmPlayerTalk;

internal enum UiLanguageChoice
{
    Auto,
    Chinese,
    English,
    Japanese,
    Korean
}

internal static partial class UiLanguage
{
    private static readonly string SettingsPath = Path.Combine(ModDataPath.Root, "ui-language.txt");
    private static UiLanguageChoice gameLanguage = UiLanguageChoice.English;

    internal static UiLanguageChoice Selection { get; private set; } = ReadSelection(SettingsPath);
    internal static UiLanguageChoice Effective => Selection == UiLanguageChoice.Auto ? gameLanguage : Selection;
    internal static int Revision { get; private set; }

    internal static bool Set(UiLanguageChoice choice)
    {
        if (choice == Selection)
            return true;
        if (!TrySaveSelection(SettingsPath, choice))
            return false;
        Selection = choice;
        Revision++;
        return true;
    }

    internal static bool ApplyGameLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;
        var previous = Effective;
        gameLanguage = MatchGameLanguage(language);
        if (Effective == previous)
            return false;
        Revision++;
        return true;
    }

    internal static string Get(string text) => Translate(text, Effective);

    internal static string Translate(string text, UiLanguageChoice language)
    {
        if (language == UiLanguageChoice.Chinese ||
            !(Translations.TryGetValue(text, out var entry) || SettingsTranslations.TryGetValue(text, out entry)))
            return text;
        return language switch
        {
            UiLanguageChoice.Japanese => entry.Japanese,
            UiLanguageChoice.Korean => entry.Korean,
            _ => entry.English
        };
    }

    internal static UiLanguageChoice MatchGameLanguage(string language)
    {
        var key = new string(language.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key switch
        {
            "zh" or "zhcn" or "zhsg" or "zhtw" or "zhhk" or "zhmo" or "zhhans" or "zhhant" or
                "chinese" or "simplifiedchinese" or "chinesesimplified" or "traditionalchinese" or "chinesetraditional" or
                "中文" or "简体中文" or "繁體中文" or "簡體中文" or "繁体中文" => UiLanguageChoice.Chinese,
            "ja" or "jajp" or "japanese" or "日本語" => UiLanguageChoice.Japanese,
            "ko" or "kokr" or "korean" or "한국어" => UiLanguageChoice.Korean,
            _ => UiLanguageChoice.English
        };
    }

    internal static UiLanguageChoice ReadSelection(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 64)
                return UiLanguageChoice.Auto;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim() switch
            {
                nameof(UiLanguageChoice.Chinese) => UiLanguageChoice.Chinese,
                nameof(UiLanguageChoice.English) => UiLanguageChoice.English,
                nameof(UiLanguageChoice.Japanese) => UiLanguageChoice.Japanese,
                nameof(UiLanguageChoice.Korean) => UiLanguageChoice.Korean,
                _ => UiLanguageChoice.Auto
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return UiLanguageChoice.Auto;
        }
    }

    internal static bool TrySaveSelection(string path, UiLanguageChoice choice)
    {
        if (!Enum.IsDefined(choice))
            return false;

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, choice.ToString());
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
