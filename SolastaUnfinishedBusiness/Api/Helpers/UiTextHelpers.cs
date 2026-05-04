using System;
using System.Collections;
using System.Globalization;
using System.Text;
using I2.Loc;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Api.Helpers;

internal static class UiTextHelpers
{
    private const int CardFitSearchIterations = 8;
    private const float CjkTwoLineSpacing = -6f;
    private const int DeferredSpellBoxFitFrames = 2;
    private const int DeferredActionItemCaptionFitFrames = 2;
    private const float PreferredSizeTolerance = 0.5f;
    private const float TitleMinFontScale = 0.72f;
    private const float TitleAbsoluteMinFontSize = 8f;
    private const float ActionCaptionMinFontScale = 0.58f;
    private const float ActionCaptionAbsoluteMinFontSize = 7f;
    private const float TagMinFontScale = 0.65f;
    private const float TagAbsoluteMinFontSize = 7f;
    private const float StatTitleMinFontScale = 0.62f;
    private const float StatTitleAbsoluteMinFontSize = 7f;
    private const float StatValueMinFontScale = 0.72f;
    private const float StatValueAbsoluteMinFontSize = 8f;
    private const int MaxSpellLevel = 9;

    private static readonly (string Roman, int Level)[] SpellLevelTokens =
    [
        ("VIII", 8),
        ("VII", 7),
        ("VI", 6),
        ("IX", 9),
        ("IV", 4),
        ("III", 3),
        ("II", 2),
        ("V", 5),
        ("I", 1)
    ];

    private static readonly string[] SpellLevelTitleLabels = new string[MaxSpellLevel + 1];
    private static readonly string[] SpellLevelBodyLabels = new string[MaxSpellLevel + 1];

    private static string SpellLevelBodyLanguageCode { get; set; }

    internal static string NormalizeSpellLevelBodyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        EnsureSpellLevelBodyCache();

        foreach (var (roman, level) in SpellLevelTokens)
        {
            var bodyLabel = SpellLevelBodyLabels[level];

            if (string.IsNullOrEmpty(bodyLabel))
            {
                continue;
            }

            var titleLabel = SpellLevelTitleLabels[level];

            if (!string.IsNullOrEmpty(titleLabel) &&
                !string.Equals(titleLabel, bodyLabel, StringComparison.Ordinal))
            {
                text = text.Replace(titleLabel, bodyLabel);
            }

            if (ContainsWholeAsciiToken(text, roman))
            {
                text = ReplaceWholeAsciiToken(text, roman, bodyLabel);
            }
        }

        return text;
    }

    internal static void FitSingleLine(GuiLabel label, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!label)
        {
            return;
        }

        FitSingleLine(label.TMP_Text, minFontScale, absoluteMin);
    }

    internal static void FitCardTitle(GuiLabel label, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!label)
        {
            return;
        }

        FitCardTitle(label.TMP_Text, minFontScale, absoluteMin);
    }

    internal static void FitSingleLine(TMP_Text text, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!text)
        {
            return;
        }

        if (!TryGetFontSizeBounds(text, null, minFontScale, absoluteMin, out var maxFontSize, out var minFontSize))
        {
            return;
        }

        ApplyAutoTextFit(text, 1, false, maxFontSize, minFontSize);
    }

    internal static void FitCardTitle(TMP_Text text, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!text)
        {
            return;
        }

        var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();

        state.Capture(text);

        if (!TryGetFontSizeBounds(text, state, minFontScale, absoluteMin, out var maxFontSize, out var minFontSize))
        {
            return;
        }

        if (!TryGetTextContentSize(text, out var availableSize))
        {
            ApplyCardTextFit(text, 1, false, maxFontSize, state);
            return;
        }

        var useCjkCompactSpacing = ShouldUseCjkCompactLineSpacing(text);

        if (state.HasFitSignature(
                nameof(FitCardTitle),
                text,
                availableSize,
                minFontScale,
                absoluteMin,
                useCjkCompactSpacing))
        {
            return;
        }

        var singleLineFontSize = GetSingleLineFontSize(text, availableSize, maxFontSize);

        if (singleLineFontSize >= minFontSize)
        {
            ApplyCardTextFit(text, 1, false, Mathf.Min(maxFontSize, singleLineFontSize), state);
            state.RememberFitSignature(
                nameof(FitCardTitle),
                text,
                availableSize,
                minFontScale,
                absoluteMin,
                useCjkCompactSpacing);
            return;
        }

        ApplyCardTextFit(text, 2, true, GetTwoLineFontSize(text, availableSize, maxFontSize, minFontSize, state), state);
        state.RememberFitSignature(
            nameof(FitCardTitle),
            text,
            availableSize,
            minFontScale,
            absoluteMin,
            useCjkCompactSpacing);
    }

    internal static void FitActionItemCaption(CharacterActionItemForm form)
    {
        if (!CanFitActionItemCaption(form))
        {
            return;
        }

        ApplyActionItemCaptionFit(form);
        ScheduleActionItemCaptionFit(form);
    }

    private static bool CanFitActionItemCaption(CharacterActionItemForm form)
    {
        return form &&
               form.captionLabel?.tmpText != null &&
               form.captionLabel.tmpText.gameObject.activeInHierarchy &&
               form.captionLabel.tmpText.rectTransform;
    }

    private static void ApplyActionItemCaptionFit(CharacterActionItemForm form)
    {
        if (!CanFitActionItemCaption(form))
        {
            return;
        }

        var text = form.captionLabel.tmpText;

        FitActionCaption(text);
        text.alignment = TextAlignmentOptions.Bottom;
    }

    private static void ScheduleActionItemCaptionFit(CharacterActionItemForm form)
    {
        if (!form.gameObject.activeInHierarchy)
        {
            return;
        }

        var runner = form.GetComponent<DeferredActionItemCaptionFit>() ??
                     form.gameObject.AddComponent<DeferredActionItemCaptionFit>();

        runner.Schedule(form);
    }

    private static void FitActionCaption(TMP_Text text)
    {
        var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();

        state.Capture(text);

        if (!TryGetFontSizeBounds(
                text,
                state,
                ActionCaptionMinFontScale,
                ActionCaptionAbsoluteMinFontSize,
                out var maxFontSize,
                out var minFontSize))
        {
            return;
        }

        if (!TryGetTextContentSize(text, out var availableSize))
        {
            ApplyActionCaptionBaseStyle(text, state);
            return;
        }

        var useCjkCompactSpacing = ShouldUseCjkCompactLineSpacing(text);

        if (state.HasFitSignature(
                nameof(FitActionCaption),
                text,
                availableSize,
                ActionCaptionMinFontScale,
                ActionCaptionAbsoluteMinFontSize,
                useCjkCompactSpacing))
        {
            return;
        }

        ApplyActionCaptionBaseStyle(text, state);

        var fontSize = Mathf.Clamp(GetSingleLineFontSize(text, availableSize, maxFontSize), minFontSize, maxFontSize);

        ApplyCardTextFit(text, 1, false, fontSize, state);
        state.RememberFitSignature(
            nameof(FitActionCaption),
            text,
            availableSize,
            ActionCaptionMinFontScale,
            ActionCaptionAbsoluteMinFontSize,
            useCjkCompactSpacing);
    }

    private static void ApplyActionCaptionBaseStyle(TMP_Text text, TextFitState state)
    {
        text.enableAutoSizing = false;
        text.enableWordWrapping = false;
        text.maxVisibleLines = 1;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.autoSizeTextContainer = false;
        text.fontSizeMax = state.OriginalFontSizeMax;
        ApplyCjkLineSpacing(text, false, state);
        text.SetLayoutDirty();
        text.SetVerticesDirty();
    }

    private static bool TryGetFontSizeBounds(
        TMP_Text text,
        TextFitState state,
        float minFontScale,
        float absoluteMin,
        out float maxFontSize,
        out float minFontSize)
    {
        maxFontSize = state is { OriginalFontSizeMax: > 0f }
            ? state.OriginalFontSizeMax
            : text.enableAutoSizing && text.fontSizeMax > 0f
            ? text.fontSizeMax
            : text.fontSize;
        minFontSize = 0f;

        if (maxFontSize <= 0f)
        {
            return false;
        }

        minFontSize = Mathf.Min(maxFontSize, Mathf.Max(absoluteMin, maxFontSize * minFontScale));

        return true;
    }

    private static void ApplyAutoTextFit(
        TMP_Text text,
        int maxVisibleLines,
        bool enableWordWrapping,
        float maxFontSize,
        float minFontSize)
    {
        text.enableAutoSizing = true;
        text.enableWordWrapping = enableWordWrapping;
        text.maxVisibleLines = maxVisibleLines;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.autoSizeTextContainer = false;
        text.fontSizeMax = maxFontSize;
        text.fontSizeMin = minFontSize;
        ApplyCjkLineSpacing(text, maxVisibleLines > 1 && enableWordWrapping, text.GetComponent<TextFitState>());
        text.SetLayoutDirty();
        text.SetVerticesDirty();
    }

    private static void ApplyCardTextFit(
        TMP_Text text,
        int maxVisibleLines,
        bool enableWordWrapping,
        float fontSize,
        TextFitState state)
    {
        text.enableAutoSizing = false;
        text.enableWordWrapping = enableWordWrapping;
        text.maxVisibleLines = maxVisibleLines;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.autoSizeTextContainer = false;
        text.fontSize = fontSize;
        text.fontSizeMax = state.OriginalFontSizeMax;
        text.fontSizeMin = fontSize;
        ApplyCjkLineSpacing(text, maxVisibleLines > 1 && enableWordWrapping, state);
        text.SetLayoutDirty();
        text.SetVerticesDirty();
    }

    private static bool TryGetTextContentSize(TMP_Text text, out Vector2 availableSize)
    {
        availableSize = default;

        if (!text.rectTransform)
        {
            return false;
        }

        var rect = text.rectTransform.rect;
        var margin = text.margin;

        availableSize = new Vector2(rect.width - margin.x - margin.z, rect.height - margin.y - margin.w);

        return availableSize.x > 0f && availableSize.y > 0f;
    }

    private static float GetSingleLineFontSize(TMP_Text text, Vector2 availableSize, float maxFontSize)
    {
        if (string.IsNullOrEmpty(text.text))
        {
            return maxFontSize;
        }

        var preferredSize = GetPreferredSize(
            text,
            maxFontSize,
            false,
            1,
            GetOriginalLineSpacing(text),
            float.PositiveInfinity);

        var widthFontSize = preferredSize.x > 0f
            ? maxFontSize * availableSize.x / preferredSize.x
            : maxFontSize;
        var heightFontSize = preferredSize.y > 0f
            ? maxFontSize * availableSize.y / preferredSize.y
            : maxFontSize;

        return Mathf.Min(maxFontSize, widthFontSize, heightFontSize);
    }

    private static float GetTwoLineFontSize(
        TMP_Text text,
        Vector2 availableSize,
        float maxFontSize,
        float minFontSize,
        TextFitState state)
    {
        if (!DoesWrappedTextFit(text, availableSize, minFontSize, state))
        {
            return minFontSize;
        }

        var low = minFontSize;
        var high = maxFontSize;

        for (var i = 0; i < CardFitSearchIterations; i++)
        {
            var mid = (low + high) * 0.5f;

            if (DoesWrappedTextFit(text, availableSize, mid, state))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static bool DoesWrappedTextFit(TMP_Text text, Vector2 availableSize, float fontSize, TextFitState state)
    {
        var preferredSize = GetPreferredSize(
            text,
            fontSize,
            true,
            2,
            ShouldUseCjkCompactLineSpacing(text) ? CjkTwoLineSpacing : state.OriginalLineSpacing,
            availableSize.x);

        return preferredSize.x <= availableSize.x + PreferredSizeTolerance &&
               preferredSize.y <= availableSize.y + PreferredSizeTolerance;
    }

    private static Vector2 GetPreferredSize(
        TMP_Text text,
        float fontSize,
        bool enableWordWrapping,
        int maxVisibleLines,
        float lineSpacing,
        float width)
    {
        var previousAutoSizing = text.enableAutoSizing;
        var previousFontSize = text.fontSize;
        var previousLineSpacing = text.lineSpacing;
        var previousMaxVisibleLines = text.maxVisibleLines;
        var previousWordWrapping = text.enableWordWrapping;

        try
        {
            text.enableAutoSizing = false;
            text.enableWordWrapping = enableWordWrapping;
            text.maxVisibleLines = maxVisibleLines;
            text.fontSize = fontSize;
            text.lineSpacing = lineSpacing;

            return text.GetPreferredValues(text.text, width, float.PositiveInfinity);
        }
        finally
        {
            text.enableAutoSizing = previousAutoSizing;
            text.enableWordWrapping = previousWordWrapping;
            text.maxVisibleLines = previousMaxVisibleLines;
            text.fontSize = previousFontSize;
            text.lineSpacing = previousLineSpacing;
        }
    }

    private static float GetOriginalLineSpacing(TMP_Text text)
    {
        var state = text.GetComponent<TextFitState>();

        return state ? state.OriginalLineSpacing : text.lineSpacing;
    }

    private static void ApplyCjkLineSpacing(TMP_Text text, bool allowCompactSpacing, TextFitState state)
    {
        var compactSpacing = allowCompactSpacing && ShouldUseCjkCompactLineSpacing(text);

        text.lineSpacing = compactSpacing
            ? CjkTwoLineSpacing
            : state?.OriginalLineSpacing ?? text.lineSpacing;
    }

    private static bool ShouldUseCjkCompactLineSpacing(TMP_Text text)
    {
        return Main.Settings.FixAsianLanguagesTextWrap && TranslatorContext.HasCJKChar(text.text);
    }

    private static void EnsureSpellLevelBodyCache()
    {
        var languageCode = LocalizationManager.CurrentLanguageCode ?? string.Empty;

        if (string.Equals(SpellLevelBodyLanguageCode, languageCode, StringComparison.Ordinal))
        {
            return;
        }

        SpellLevelBodyLanguageCode = languageCode;

        for (var level = 1; level <= MaxSpellLevel; level++)
        {
            var titleTerm = $"Rules/&SpellLevel{level}FormatTitle";
            var titleLabel = Gui.Localize(titleTerm);

            SpellLevelTitleLabels[level] = IsMissingLocalization(titleLabel, titleTerm)
                ? null
                : titleLabel;

            var bodyTerm = $"Tooltip/&SpellLevel{level}BodyText";
            var bodyLabel = Gui.Localize(bodyTerm);

            SpellLevelBodyLabels[level] = IsMissingLocalization(bodyLabel, bodyTerm)
                ? BuildFallbackSpellLevelBodyLabel(level, SpellLevelTitleLabels[level])
                : bodyLabel;
        }
    }

    private static bool IsMissingLocalization(string value, string term)
    {
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value, term, StringComparison.Ordinal);
    }

    private static string BuildFallbackSpellLevelBodyLabel(int level, string titleLabel)
    {
        var numericLevel = level.ToString(CultureInfo.InvariantCulture);

        if (string.IsNullOrEmpty(titleLabel))
        {
            return numericLevel;
        }

        foreach (var (roman, romanLevel) in SpellLevelTokens)
        {
            if (romanLevel != level || !ContainsWholeAsciiToken(titleLabel, roman))
            {
                continue;
            }

            var replaced = ReplaceWholeAsciiToken(titleLabel, roman, numericLevel);

            if (!string.Equals(replaced, titleLabel, StringComparison.Ordinal))
            {
                return replaced;
            }
        }

        return titleLabel.IndexOf(numericLevel, StringComparison.Ordinal) >= 0
            ? titleLabel
            : numericLevel;
    }

    private static bool ContainsWholeAsciiToken(string text, string token)
    {
        var index = 0;

        while (index < text.Length)
        {
            var found = text.IndexOf(token, index, StringComparison.Ordinal);

            if (found < 0)
            {
                return false;
            }

            if (IsWholeAsciiToken(text, found, token.Length))
            {
                return true;
            }

            index = found + token.Length;
        }

        return false;
    }

    private static string ReplaceWholeAsciiToken(string text, string token, string replacement)
    {
        var index = 0;
        StringBuilder builder = null;

        while (index < text.Length)
        {
            var found = text.IndexOf(token, index, StringComparison.Ordinal);

            if (found < 0)
            {
                if (builder != null)
                {
                    builder.Append(text, index, text.Length - index);
                }

                break;
            }

            if (!IsWholeAsciiToken(text, found, token.Length))
            {
                if (builder != null)
                {
                    builder.Append(text, index, found + token.Length - index);
                }

                index = found + token.Length;
                continue;
            }

            builder ??= new StringBuilder(text.Length + replacement.Length);
            builder.Append(text, index, found - index);
            builder.Append(replacement);
            index = found + token.Length;
        }

        return builder?.ToString() ?? text;
    }

    private static bool IsWholeAsciiToken(string text, int index, int length)
    {
        var previous = index - 1;
        var next = index + length;

        return (previous < 0 || !IsAsciiLetterOrDigit(text[previous])) &&
               (next >= text.Length || !IsAsciiLetterOrDigit(text[next]));
    }

    private static bool IsAsciiLetterOrDigit(char character)
    {
        return character >= 'A' && character <= 'Z' ||
               character >= 'a' && character <= 'z' ||
               character >= '0' && character <= '9';
    }

    internal static void KeepSpellBoxTextInside(SpellBox spellBox)
    {
        if (!spellBox)
        {
            return;
        }

        if (IsCanvasRebuildInProgress())
        {
            ScheduleSpellBoxTextFit(spellBox);
            return;
        }

        ApplySpellBoxTextFit(spellBox);
    }

    private static void ApplySpellBoxTextFit(SpellBox spellBox)
    {
        if (!spellBox)
        {
            return;
        }

        FitCardTitle(spellBox.titleLabel);
        FitSingleLine(spellBox.autoPreparedTitle, TagMinFontScale, TagAbsoluteMinFontSize);
    }

    private static void ScheduleSpellBoxTextFit(SpellBox spellBox)
    {
        if (!spellBox.gameObject.activeInHierarchy)
        {
            ApplySpellBoxTextFit(spellBox);
            return;
        }

        var runner = spellBox.GetComponent<DeferredSpellBoxTextFit>() ??
                     spellBox.gameObject.AddComponent<DeferredSpellBoxTextFit>();

        runner.Schedule(spellBox);
    }

    private static bool IsCanvasRebuildInProgress()
    {
        return CanvasUpdateRegistry.IsRebuildingLayout() || CanvasUpdateRegistry.IsRebuildingGraphics();
    }

    internal static void FitCharacterStatBox(CharacterStatBox box)
    {
        if (!box)
        {
            return;
        }

        FitSingleLine(box.titleLabel, StatTitleMinFontScale, StatTitleAbsoluteMinFontSize);
        FitSingleLine(box.ValueLabel, StatValueMinFontScale, StatValueAbsoluteMinFontSize);
    }

    internal static void KeepCharacterStatsPanelTextInside(CharacterStatsPanel panel)
    {
        if (!panel)
        {
            return;
        }

        FitCharacterStatBox(panel.armorClassBox);
        FitCharacterStatBox(panel.initiativeBox);
        FitCharacterStatBox(panel.moveBox);
        FitCharacterStatBox(panel.proficiencyBox);
        FitCharacterStatBox(panel.hitPointBox);
        FitCharacterStatBox(panel.hitDiceBox);
        FitSingleLine(panel.healthLabel, StatValueMinFontScale, StatValueAbsoluteMinFontSize);
        FitSingleLine(panel.maxHealthLabel, StatValueMinFontScale, StatValueAbsoluteMinFontSize);
    }

    private sealed class TextFitState : MonoBehaviour
    {
        internal float OriginalFontSizeMax { get; private set; }
        internal float OriginalLineSpacing { get; private set; }

        private bool Captured { get; set; }
        private Vector2 LastAvailableSize { get; set; }
        private float LastAbsoluteMin { get; set; }
        private bool LastCjkCompactSpacing { get; set; }
        private TMP_FontAsset LastFont { get; set; }
        private float LastMaxFontSize { get; set; }
        private float LastMinFontScale { get; set; }
        private string LastMode { get; set; }
        private string LastText { get; set; }

        internal void Capture(TMP_Text text)
        {
            if (Captured)
            {
                return;
            }

            OriginalFontSizeMax = text.enableAutoSizing && text.fontSizeMax > 0f
                ? text.fontSizeMax
                : text.fontSize;
            OriginalLineSpacing = text.lineSpacing;
            Captured = true;
        }

        internal bool HasFitSignature(
            string mode,
            TMP_Text text,
            Vector2 availableSize,
            float minFontScale,
            float absoluteMin,
            bool cjkCompactSpacing)
        {
            return string.Equals(LastMode, mode, StringComparison.Ordinal) &&
                   string.Equals(LastText, text.text, StringComparison.Ordinal) &&
                   LastFont == text.font &&
                   Mathf.Abs(LastMaxFontSize - OriginalFontSizeMax) <= 0.01f &&
                   Mathf.Abs(LastMinFontScale - minFontScale) <= 0.001f &&
                   Mathf.Abs(LastAbsoluteMin - absoluteMin) <= 0.01f &&
                   LastCjkCompactSpacing == cjkCompactSpacing &&
                   (LastAvailableSize - availableSize).sqrMagnitude <= 1f;
        }

        internal void RememberFitSignature(
            string mode,
            TMP_Text text,
            Vector2 availableSize,
            float minFontScale,
            float absoluteMin,
            bool cjkCompactSpacing)
        {
            LastMode = mode;
            LastText = text.text;
            LastFont = text.font;
            LastMaxFontSize = OriginalFontSizeMax;
            LastMinFontScale = minFontScale;
            LastAbsoluteMin = absoluteMin;
            LastCjkCompactSpacing = cjkCompactSpacing;
            LastAvailableSize = availableSize;
        }
    }

    private sealed class DeferredActionItemCaptionFit : MonoBehaviour
    {
        private Coroutine Coroutine { get; set; }

        private CharacterActionItemForm Form { get; set; }

        internal void Schedule(CharacterActionItemForm form)
        {
            Form = form;

            if (Coroutine != null)
            {
                return;
            }

            Coroutine = StartCoroutine(ApplyLater());
        }

        private IEnumerator ApplyLater()
        {
            for (var i = 0; i < DeferredActionItemCaptionFitFrames; i++)
            {
                yield return null;
                ApplyActionItemCaptionFit(Form);
            }

            Coroutine = null;
        }

        private void OnDisable()
        {
            if (Coroutine == null)
            {
                return;
            }

            StopCoroutine(Coroutine);
            Coroutine = null;
        }
    }

    private sealed class DeferredSpellBoxTextFit : MonoBehaviour
    {
        private Coroutine Coroutine { get; set; }

        private SpellBox SpellBox { get; set; }

        internal void Schedule(SpellBox spellBox)
        {
            SpellBox = spellBox;

            if (Coroutine != null)
            {
                StopCoroutine(Coroutine);
            }

            Coroutine = StartCoroutine(ApplyLater());
        }

        private IEnumerator ApplyLater()
        {
            for (var i = 0; i < DeferredSpellBoxFitFrames; i++)
            {
                yield return null;
                ApplySpellBoxTextFit(SpellBox);
            }

            Coroutine = null;
        }

        private void OnDisable()
        {
            if (Coroutine == null)
            {
                return;
            }

            StopCoroutine(Coroutine);
            Coroutine = null;
        }
    }
}
