using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using I2.Loc;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal struct LanguageEntry
{
    public string Code;
    public string Text;
    public string Directory;
    [UsedImplicitly] public string SourceCode;
}

internal static class TranslatorContext
{
    private const string UnofficialLanguagesFolderPrefix = "UnofficialTranslations/";

    internal const string English = "en";

    internal static readonly string[] AvailableLanguages =
    [
        "de", "en", "es", "fr", "ja", "it", "ko", "pt", "ru", "zh-CN"
    ];

    internal static readonly List<LanguageEntry> Languages = [];

    /// <summary>
    ///     Maps unofficial language codes to official language codes.
    /// </summary>
    private static Dictionary<string, string> SourceCodeCache { get; } = new();

    private static LanguageEntry[] AvailableUnofficialLanguagesCache { get; set; }

    public static bool IsCJKChar(char c)
    {
        return IsInRange(c, 0x1100, 0x11FF) || // Hangul Jamo
               IsInRange(c, 0x3000, 0x303F) || // CJK Symbols and Punctuation
               IsInRange(c, 0x3040, 0x309F) || // Hiragana
               IsInRange(c, 0x30A0, 0x30FF) || // Katakana
               IsInRange(c, 0x31F0, 0x31FF) || // Katakana Phonetic Extensions
               IsInRange(c, 0x3130, 0x318F) || // Hangul Compatibility Jamo
               IsInRange(c, 0x3400, 0x4DBF) || // CJK Unified Ideographs Extension A
               IsInRange(c, 0x4E00, 0x9FFF) || // CJK Unified Ideographs
               IsInRange(c, 0xAC00, 0xD7AF) || // Hangul Syllables
               IsInRange(c, 0xF900, 0xFAFF) || // CJK Compatibility Ideographs
               IsInRange(c, 0xFF00, 0xFFEF); // Halfwidth and Fullwidth Forms
    }

    public static bool HasCJKChar(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        foreach (var c in s)
        {
            if (IsCJKChar(c))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasCJKCharQuick(string s)
    {
        return !string.IsNullOrEmpty(s) && IsCJKChar(s[0]);
    }

    private static bool IsInRange(char c, int start, int end)
    {
        return c >= start && c <= end;
    }

    internal static void EarlyLoad()
    {
        Languages.Clear();
        SourceCodeCache.Clear();

        if (Main.Settings.DisableUnofficialTranslations)
        {
            Main.Info("Unofficial translations support disabled.");

            return;
        }

        if (!Directory.Exists(Path.Combine(Main.ModFolder, UnofficialLanguagesFolderPrefix)))
        {
            Main.Error("Unofficial translations not found.");

            return;
        }

        LoadCustomLanguages(GetEnabledUnofficialLanguages());

        if (Languages.Count == 0)
        {
            return;
        }

        LoadCustomTerms();
        LoadCustomFonts();
    }

    internal static IReadOnlyList<LanguageEntry> GetAvailableUnofficialLanguages()
    {
        return AvailableUnofficialLanguagesCache ??= DetectAvailableUnofficialLanguages(false).ToArray();
    }

    private static IEnumerable<LanguageEntry> GetEnabledUnofficialLanguages()
    {
        var languages = GetAvailableUnofficialLanguages();

        if (!Main.Settings.FilterUnofficialTranslationsByLanguage)
        {
            return languages;
        }

        return languages.Where(language => Main.Settings.EnabledUnofficialTranslationLanguages
            .Any(code => string.Equals(code, language.Code, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<LanguageEntry> DetectAvailableUnofficialLanguages(bool log)
    {
        var path = Path.Combine(Main.ModFolder, UnofficialLanguagesFolderPrefix);

        if (!Directory.Exists(path))
        {
            return [];
        }

        var cultureInfos = CultureInfo.GetCultures(CultureTypes.AllCultures);
        var directoryInfo = new DirectoryInfo(path);
        var languages = new List<LanguageEntry>();

        foreach (var directory in directoryInfo.GetDirectories().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var code = directory.Name;
            var cultureInfo = cultureInfos.FirstOrDefault(o => o.Name == code);

            if (File.Exists($"{directory.FullName}/info.json"))
            {
                var info = JsonConvert.DeserializeObject<JObject>(File.ReadAllText($"{directory.FullName}/info.json"));

                languages.Add(new LanguageEntry
                {
                    Code = code,
                    Text = info["NativeName"]?.ToString() ?? code,
                    Directory = directory.FullName,
                    SourceCode = info["SourceCode"]?.ToString() ?? string.Empty
                });

                if (log)
                {
                    Main.Info($"Language {code} detected.");
                }
            }
            else if (cultureInfo != null)
            {
                if (LocalizationManager.HasLanguage(cultureInfo.DisplayName))
                {
                    if (log)
                    {
                        Main.Error($"Language {code} from {directory.Name} already in game.");
                    }
                }
                else
                {
                    languages.Add(new LanguageEntry
                    {
                        Code = code,
                        Text = cultureInfo.TextInfo.ToTitleCase(cultureInfo.NativeName),
                        Directory = directory.FullName
                    });

                    if (log)
                    {
                        Main.Info($"Language {code} detected.");
                    }
                }
            }
            else if (log)
            {
                Main.Error($"Language {code} illegal!");
            }
        }

        return languages;
    }

    private static void LoadCustomLanguages(IEnumerable<LanguageEntry> languages)
    {
        foreach (var language in languages)
        {
            Languages.Add(language);

            if (!string.IsNullOrEmpty(language.SourceCode))
            {
                SourceCodeCache[language.Code] = language.SourceCode;
            }

            Main.Info($"Language {language.Code} detected.");
        }
    }

    private static void LoadCustomFonts()
    {
        var loadJapaneseFont = Languages.Any(UsesJapaneseFont);
        var loadKoreanFont = Languages.Any(UsesKoreanFont);

        if (!loadJapaneseFont && !loadKoreanFont)
        {
            return;
        }

        var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

        if (loadJapaneseFont)
        {
            LoadJapaneseFont(allFonts);
        }

        if (loadKoreanFont)
        {
            LoadKoreanFont(allFonts);
        }
    }

    private static void LoadJapaneseFont(IEnumerable<TMP_FontAsset> allFonts)
    {
        var fullFilename = Path.Combine(Main.ModFolder, $"{UnofficialLanguagesFolderPrefix}JapaneseHanSans.unity3d");

        if (!File.Exists(fullFilename))
        {
            Main.Error($"Loading the font bundle {fullFilename}.");

            return;
        }

        var fontBundle = AssetBundle.LoadFromFile(fullFilename);

        AddFont("NotoSansJP-Light SDF", fontBundle, allFonts, "Noto-Light SDF", "Noto-Thin SDF");
        AddFont("NotoSansJP-Regular SDF", fontBundle, allFonts, "Noto-Regular SDF", "LiberationSans SDF");
        AddFont("NotoSansJP-Bold SDF", fontBundle, allFonts, "Noto-Bold SDF");
    }

    private static void LoadKoreanFont(IEnumerable<TMP_FontAsset> allFonts)
    {
        var fullFilename = Path.Combine(Main.ModFolder, $"{UnofficialLanguagesFolderPrefix}KoreanHanSans.unity3d");

        if (!File.Exists(fullFilename))
        {
            Main.Error($"Loading the font bundle {fullFilename}.");

            return;
        }

        var fontBundle = AssetBundle.LoadFromFile(fullFilename);

        AddFont("SourceHanSansK-Light SDF", fontBundle, allFonts, "Noto-Light SDF", "Noto-Thin SDF");
        AddFont("SourceHanSansK-Regular SDF", fontBundle, allFonts, "Noto-Regular SDF", "LiberationSans SDF");
        AddFont("SourceHanSansK-Bold SDF", fontBundle, allFonts, "Noto-Bold SDF");
    }

    private static bool UsesJapaneseFont(LanguageEntry language)
    {
        return IsLanguageCode(language.Code, "ja") ||
               IsLanguageCode(language.SourceCode, "ja") ||
               IsChineseLanguage(language.Code) ||
               IsChineseLanguage(language.SourceCode);
    }

    private static bool UsesKoreanFont(LanguageEntry language)
    {
        return IsLanguageCode(language.Code, "ko") || IsLanguageCode(language.SourceCode, "ko");
    }

    private static bool IsLanguageCode(string code, string languageCode)
    {
        return string.Equals(code, languageCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChineseLanguage(string code)
    {
        return !string.IsNullOrEmpty(code) && code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadCustomTerms()
    {
        var languageSourceData = LocalizationManager.Sources[0];

        // load new language terms
        foreach (var language in Languages)
        {
            // add language
            languageSourceData.AddLanguage(language.Text, language.Code);

            var languageIndex = languageSourceData.GetLanguageIndex(language.Text);

            // add terms
            var directoryInfo = new DirectoryInfo(language.Directory);
            var files = directoryInfo.GetFiles("*.txt").OrderBy(x => x.Name, StringComparer.Ordinal);
            var separator = new[] { '=' };

            foreach (var file in files)
            {
                using var sr = new StreamReader(file.FullName);

                while (sr.ReadLine() is { } line)
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    var split = line.Split(separator, 2);

                    if (split.Length != 2)
                    {
                        Main.Error($"Skipping line [{line}] in file [{file.FullName}]");

                        continue;
                    }

                    var term = split[0];
                    var text = split[1];

                    languageSourceData.AddTerm(term).Languages[languageIndex] = text;
                }
            }
        }
    }

    private static void AddFont(
        string fontName,
        AssetBundle fontBundle,
        IEnumerable<TMP_FontAsset> allFonts,
        params string[] fontsToAppend)
    {
        var modFontAsset = fontBundle.LoadAsset<TMP_FontAsset>($"{fontName}.asset");

        if (!modFontAsset)
        {
            Main.Error($"Font asset {fontName} not found.");

            return;
        }

        var fontNamesToAppend = new HashSet<string>(fontsToAppend, StringComparer.Ordinal);

        foreach (var tmpFontAsset in allFonts.Where(x => fontNamesToAppend.Contains(x.name)))
        {
            tmpFontAsset.fallbackFontAssetTable.Add(modFontAsset);

            Main.Info($"Font asset {fontName} loaded.");
        }
    }

    private static bool IsModTerm(string fullName, string languageCode)
    {
        return fullName.StartsWith(languageCode) && fullName.EndsWith($"{languageCode}.txt");
    }

    private static bool IsFixedTerm(string fullName, string languageCode)
    {
        return fullName == $"Fixes-{languageCode}.txt";
    }

    [UsedImplicitly]
    internal static IEnumerable<string> GetTranslations(string languageCode, Func<string, string, bool> validate)
    {
        using var zipStream = new MemoryStream(Properties.Resources.Translations);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        foreach (var entry in zip.Entries.Where(x => validate(x.FullName, languageCode)))
        {
            using var dataStream = entry.Open();
            using var data = new StreamReader(dataStream);

            while (!data.EndOfStream)
            {
                yield return data.ReadLine();
            }
        }
    }

    private static Dictionary<string, string> GetTermsDict(
        string languageCode,
        Func<string, string, bool> validate)
    {
        var result = new Dictionary<string, string>();
        var separator = new[] { '=' };

        if (SourceCodeCache.TryGetValue(languageCode, out var sourceCode))
        {
            // if has source language, use it
            languageCode = sourceCode;
        }

        foreach (var line in GetTranslations(languageCode, validate))
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var split = line.Split(separator, 2);

            if (split.Length != 2)
            {
                Main.Error($"cannot parse line {line}");
                continue;
            }

            var term = split[0];
            var text = split[1];

            if (result.ContainsKey(term))
            {
                Main.Error($"duplicate term {term}");
            }
            else
            {
                result.Add(term, text);
            }
        }

        return result;
    }

    internal static void Load()
    {
        var languageCode = LocalizationManager.CurrentLanguageCode;

        var englishTerms = GetTermsDict(English, IsModTerm);
        var currentLanguageTerms = languageCode != English ? GetTermsDict(languageCode, IsModTerm) : englishTerms;
        var fixedTerms = GetTermsDict(languageCode, IsFixedTerm);

        var languageSourceData = LocalizationManager.Sources[0];
        var languageIndex = languageSourceData.GetLanguageIndex(LocalizationManager.CurrentLanguage);

        // loads mod translations
        // we loop on default EN terms collection as this is the one to be trusted
        var lineCount = 0;

        foreach (var term in englishTerms.Keys)
        {
            // if we find a translated term them we use it otherwise fall back to EN default
            if (!currentLanguageTerms.TryGetValue(term, out var text))
            {
                text = englishTerms[term];
            }

            AddTerm(term, text);

            lineCount++;
        }

        Main.Info($"{lineCount} {languageCode} translation terms loaded of {currentLanguageTerms.Count} provided.");

        // loads official translations fixes
        lineCount = 0;

        foreach (var term in fixedTerms.Keys)
        {
            var text = fixedTerms[term];

            AddTerm(term, text);

            lineCount++;
        }

        Main.Info($"{lineCount} {languageCode} translation fixes loaded.");

        // creates a report on missing terms
        if (languageCode == English)
        {
            return;
        }

        var termsToAdd = englishTerms.Keys.Except(currentLanguageTerms.Keys).ToArray();

        if (termsToAdd.Length != 0)
        {
            Main.Info("ADD THESE TERMS:");

            foreach (var term in termsToAdd)
            {
                Main.Info($"{term} is missing from {languageCode} translation assets");
            }
        }

        var termsToDelete = currentLanguageTerms.Keys.Except(englishTerms.Keys);

        if (!termsToDelete.Any())
        {
            return;
        }

        Main.Info("DELETE THESE TERMS:");

        foreach (var term in currentLanguageTerms.Keys.Except(englishTerms.Keys))
        {
            Main.Info($"{term} must be deleted from {languageCode} translation assets");
        }

        return;

        void AddTerm(string term, string text)
        {
            var termData = languageSourceData.GetTermData(term);

            if (termData?.Languages[languageIndex] != null)
            {
                // ReSharper disable once InvocationIsSkipped
                Main.Log($"term {term} overwritten with text {text}");
                termData.Languages[languageIndex] = text;
            }
            else
            {
                languageSourceData.AddTerm(term).Languages[languageIndex] = text;
            }
        }
    }

    internal static bool HasTranslation(string term)
    {
        return LocalizationManager.Sources[0].ContainsTerm(term);
    }
}
