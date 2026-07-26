using System;
using System.Collections;
using System.Collections.Generic;
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
    private const string SideLabelProxyName = "UB_VerticalSideLabelProxy";
    private const float CjkTwoLineSpacing = -6f;
    private const int DeferredSpellBoxFitFrames = 2;
    private const int DeferredActionItemCaptionFitFrames = 2;
    private const int DeferredConstrainedLabelFitFrames = 2;
    private const float PreferredSizeTolerance = 0.5f;
    private const float TitleMinFontScale = 0.72f;
    private const float TitleAbsoluteMinFontSize = 8f;
    private const float ActionCaptionMinFontScale = 0.58f;
    private const float ActionCaptionAbsoluteMinFontSize = 7f;
    private const float TagMinFontScale = 0.65f;
    private const float TagAbsoluteMinFontSize = 7f;
    private const float SideLabelMinFontScale = 0.52f;
    private const float SideLabelAbsoluteMinFontSize = 6f;
    private const float CjkSideLabelLineSpacing = -10f;
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
    private static readonly string[] VerticalLineGlyphCandidates = ["\uFE31", "\uFE32", "\uFF5C"];
    private static readonly string[] VerticalEnDashGlyphCandidates = ["\uFE32", "\uFE31", "\uFF5C"];
    private static readonly Dictionary<int, Dictionary<string, string>> VerticalGlyphCache = [];
    private static readonly Dictionary<string, VerticalGlyphRule> VerticalGlyphRules = new(StringComparer.Ordinal)
    {
        ["\u002D"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u2010"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u2011"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u2012"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u2013"] = new VerticalGlyphRule(VerticalEnDashGlyphCandidates, "|"),
        ["\u2014"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u2015"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u2212"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u30FC"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\uFF70"] = new VerticalGlyphRule(VerticalLineGlyphCandidates, "|"),
        ["\u3001"] = new VerticalGlyphRule(new[] { "\uFE11" }),
        ["\u3002"] = new VerticalGlyphRule(new[] { "\uFE12" }),
        ["\uFF0C"] = new VerticalGlyphRule(new[] { "\uFE10" }),
        ["\uFF1A"] = new VerticalGlyphRule(new[] { "\uFE13" }),
        ["\uFF1B"] = new VerticalGlyphRule(new[] { "\uFE14" }),
        ["\uFF01"] = new VerticalGlyphRule(new[] { "\uFE15" }),
        ["\uFF1F"] = new VerticalGlyphRule(new[] { "\uFE16" }),
        ["\u0028"] = new VerticalGlyphRule(new[] { "\uFE35" }),
        ["\u0029"] = new VerticalGlyphRule(new[] { "\uFE36" }),
        ["\uFF08"] = new VerticalGlyphRule(new[] { "\uFE35" }),
        ["\uFF09"] = new VerticalGlyphRule(new[] { "\uFE36" }),
        ["\uFF5B"] = new VerticalGlyphRule(new[] { "\uFE37" }),
        ["\uFF5D"] = new VerticalGlyphRule(new[] { "\uFE38" }),
        ["\u3014"] = new VerticalGlyphRule(new[] { "\uFE39" }),
        ["\u3015"] = new VerticalGlyphRule(new[] { "\uFE3A" }),
        ["\u3010"] = new VerticalGlyphRule(new[] { "\uFE3B" }),
        ["\u3011"] = new VerticalGlyphRule(new[] { "\uFE3C" }),
        ["\u300A"] = new VerticalGlyphRule(new[] { "\uFE3D" }),
        ["\u300B"] = new VerticalGlyphRule(new[] { "\uFE3E" }),
        ["\u3008"] = new VerticalGlyphRule(new[] { "\uFE3F" }),
        ["\u3009"] = new VerticalGlyphRule(new[] { "\uFE40" }),
        ["\u300C"] = new VerticalGlyphRule(new[] { "\uFE41" }),
        ["\u300D"] = new VerticalGlyphRule(new[] { "\uFE42" }),
        ["\u300E"] = new VerticalGlyphRule(new[] { "\uFE43" }),
        ["\u300F"] = new VerticalGlyphRule(new[] { "\uFE44" }),
        ["\u3016"] = new VerticalGlyphRule(new[] { "\uFE45" }),
        ["\u3017"] = new VerticalGlyphRule(new[] { "\uFE46" }),
        ["\uFF3B"] = new VerticalGlyphRule(new[] { "\uFE47" }),
        ["\uFF3D"] = new VerticalGlyphRule(new[] { "\uFE48" })
    };

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

    internal static void FitConstrainedSingleLine(GuiLabel label, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        FitConstrainedSingleLine(label?.TMP_Text, minFontScale, absoluteMin);
    }

    internal static void FitConstrainedSingleLine(TMP_Text text, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!text ||
            !text.gameObject.activeInHierarchy)
        {
            return;
        }

        ApplyConstrainedSingleLineFit(text, minFontScale, absoluteMin);

        var deferredFit = text.GetComponent<DeferredConstrainedLabelFit>() ??
                          text.gameObject.AddComponent<DeferredConstrainedLabelFit>();

        deferredFit.Schedule(text, minFontScale, absoluteMin);
    }

    internal static void FitSideLabel(GuiLabel label)
    {
        if (!label)
        {
            return;
        }

        FitSideLabel(label.TMP_Text);
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

    private static void FitSideLabel(TMP_Text text)
    {
        if (!text)
        {
            return;
        }

        var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();

        state.Capture(text);

        var sourceText = state.GetSideLabelSourceText(text.text);
        var useVerticalCjk = ShouldUseCjkSideLabel(sourceText);

        if (!useVerticalCjk)
        {
            RestoreSideLabelText(text, state, sourceText);
            FitSingleLine(text);
            return;
        }

        var formattedText = BuildVerticalText(sourceText, text.font, out var textElementCount);

        if (!TryGetFontSizeBounds(
                text,
                state,
                SideLabelMinFontScale,
                SideLabelAbsoluteMinFontSize,
                out var maxFontSize,
                out var minFontSize))
        {
            RestoreSideLabelText(text, state, sourceText);
            FitSingleLine(text);
            return;
        }

        if (!TryGetTextContentSize(text, out var sourceSize))
        {
            RestoreSideLabelText(text, state, sourceText);
            FitSingleLine(text);
            return;
        }

        var availableSize = GetEffectiveSideLabelSize(text, sourceSize);
        var proxy = state.GetOrCreateSideLabelProxy(text);

        if (!proxy)
        {
            RestoreSideLabelText(text, state, sourceText);
            FitSingleLine(text);
            return;
        }

        PrepareSideLabelProxy(
            text,
            proxy,
            formattedText,
            Math.Max(1, textElementCount),
            maxFontSize,
            minFontSize,
            availableSize);

        if (!DoesSideLabelFit(proxy, availableSize, minFontSize, textElementCount, out _))
        {
            RestoreSideLabelText(text, state, sourceText);
            FitSingleLine(text);
            return;
        }

        text.enabled = false;
        proxy.enabled = true;
        proxy.gameObject.SetActive(true);
        proxy.SetLayoutDirty();
        proxy.SetVerticesDirty();
        state.RememberSideLabelText(sourceText, sourceText);
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

        // Forms are reused and their TMP layout can be reset without changing the text or rectangle.
        // Recalculate instead of trusting the card-title fit signature.
        ApplyActionCaptionBaseStyle(text, state);

        var fontSize = Mathf.Clamp(GetSingleLineFontSize(text, availableSize, maxFontSize), minFontSize, maxFontSize);

        ApplyCardTextFit(text, 1, false, fontSize, state);
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

    private static void RestoreSideLabelText(TMP_Text text, TextFitState state, string sourceText)
    {
        state.HideSideLabelProxy();
        text.enabled = state.OriginalEnabled;
        text.text = sourceText;
        text.rectTransform.localRotation = state.OriginalLocalRotation;
        text.alignment = state.OriginalAlignment;
        text.lineSpacing = state.OriginalLineSpacing;
        state.RememberSideLabelText(sourceText, sourceText);
    }

    private static bool ShouldUseCjkSideLabel(string text)
    {
        return Main.Settings.FixAsianLanguagesTextWrap &&
               TranslatorContext.HasCJKChar(text) &&
               !ContainsRichText(text);
    }

    private static bool ContainsRichText(string text)
    {
        return !string.IsNullOrEmpty(text) &&
               text.IndexOf('<') >= 0 &&
               text.IndexOf('>') >= 0;
    }

    private static bool DoesSideLabelFit(
        TMP_Text text,
        Vector2 availableSize,
        float fontSize,
        int maxVisibleLines,
        out Vector2 preferredSize)
    {
        preferredSize = GetPreferredSize(
            text,
            fontSize,
            false,
            Math.Max(1, maxVisibleLines),
            CjkSideLabelLineSpacing,
            availableSize.x);

        return preferredSize.x <= availableSize.x + PreferredSizeTolerance &&
               preferredSize.y <= availableSize.y + PreferredSizeTolerance;
    }

    private static Vector2 GetEffectiveSideLabelSize(TMP_Text text, Vector2 sourceSize)
    {
        if (!IsSideLabelRotated(text))
        {
            return sourceSize;
        }

        return new Vector2(sourceSize.y, sourceSize.x);
    }

    private static bool IsSideLabelRotated(TMP_Text text)
    {
        if (!text.rectTransform)
        {
            return false;
        }

        var z = Mathf.Repeat(text.rectTransform.localEulerAngles.z, 180f);

        return Mathf.Abs(z - 90f) <= 1f;
    }

    private static void PrepareSideLabelProxy(
        TMP_Text source,
        TMP_Text proxy,
        string formattedText,
        int maxVisibleLines,
        float maxFontSize,
        float minFontSize,
        Vector2 availableSize)
    {
        proxy.gameObject.SetActive(true);
        proxy.enabled = false;

        var proxyRect = proxy.rectTransform;

        proxyRect.anchorMin = new Vector2(0.5f, 0.5f);
        proxyRect.anchorMax = new Vector2(0.5f, 0.5f);
        proxyRect.pivot = new Vector2(0.5f, 0.5f);
        proxyRect.anchoredPosition = Vector2.zero;
        proxyRect.sizeDelta = availableSize;
        proxyRect.localScale = Vector3.one;
        proxyRect.localRotation = source.rectTransform
            ? Quaternion.Inverse(source.rectTransform.localRotation)
            : Quaternion.identity;

        CopySideLabelProxyStyle(source, proxy);

        proxy.text = formattedText;
        proxy.alignment = TextAlignmentOptions.Center;
        proxy.enableAutoSizing = true;
        proxy.enableWordWrapping = false;
        proxy.maxVisibleLines = maxVisibleLines;
        proxy.overflowMode = TextOverflowModes.Ellipsis;
        proxy.autoSizeTextContainer = false;
        proxy.fontSizeMax = maxFontSize;
        proxy.fontSizeMin = minFontSize;
        proxy.lineSpacing = CjkSideLabelLineSpacing;
    }

    private static void CopySideLabelProxyStyle(TMP_Text source, TMP_Text proxy)
    {
        proxy.font = source.font;
        proxy.fontSharedMaterial = source.fontSharedMaterial;
        proxy.spriteAsset = source.spriteAsset;
        proxy.color = source.color;
        proxy.fontSize = source.fontSize;
        proxy.fontStyle = source.fontStyle;
        proxy.characterSpacing = source.characterSpacing;
        proxy.wordSpacing = source.wordSpacing;
        proxy.paragraphSpacing = source.paragraphSpacing;
        proxy.margin = Vector4.zero;
        proxy.raycastTarget = false;
        proxy.richText = false;
    }

    private static string BuildVerticalText(string text, TMP_FontAsset font, out int textElementCount)
    {
        textElementCount = 0;

        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length * 2);
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            var textElement = enumerator.GetTextElement();
            var verticalTextElement = GetVerticalTextElement(font, textElement);

            if (textElementCount > 0)
            {
                builder.Append('\n');
            }

            builder.Append(verticalTextElement);
            textElementCount++;
        }

        return builder.ToString();
    }

    private static string GetVerticalTextElement(TMP_FontAsset font, string textElement)
    {
        var fontKey = font ? font.GetInstanceID() : 0;

        if (!VerticalGlyphCache.TryGetValue(fontKey, out var fontCache))
        {
            fontCache = new Dictionary<string, string>(StringComparer.Ordinal);
            VerticalGlyphCache.Add(fontKey, fontCache);
        }

        if (fontCache.TryGetValue(textElement, out var cachedTextElement))
        {
            return cachedTextElement;
        }

        var verticalTextElement = ResolveVerticalTextElement(font, textElement);

        fontCache.Add(textElement, verticalTextElement);

        return verticalTextElement;
    }

    private static string ResolveVerticalTextElement(TMP_FontAsset font, string textElement)
    {
        if (string.IsNullOrEmpty(textElement) ||
            !VerticalGlyphRules.TryGetValue(textElement, out var rule))
        {
            return textElement;
        }

        foreach (var candidate in rule.PreferredCandidates)
        {
            if (HasTextElement(font, candidate))
            {
                return candidate;
            }
        }

        return rule.ForcedFallback ?? textElement;
    }

    private static bool HasTextElement(TMP_FontAsset font, string textElement)
    {
        if (!font ||
            string.IsNullOrEmpty(textElement))
        {
            return true;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(textElement);

        if (!enumerator.MoveNext())
        {
            return false;
        }

        var firstElement = enumerator.GetTextElement();

        return !enumerator.MoveNext() &&
               firstElement.Length == 1 &&
               font.HasCharacter(firstElement[0]);
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

    private static void ApplyConstrainedSingleLineFit(
        TMP_Text text,
        float minFontScale,
        float absoluteMin)
    {
        if (!text)
        {
            return;
        }

        var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();

        state.Capture(text);

        if (!TryGetFontSizeBounds(
                text,
                state,
                minFontScale,
                absoluteMin,
                out var maxFontSize,
                out var minFontSize))
        {
            return;
        }

        if (!TryGetTextContentSize(text, out var availableSize) ||
            string.IsNullOrEmpty(text.text))
        {
            ApplyConstrainedSingleLineStyle(text, maxFontSize, state);

            return;
        }

        var preferredSize = GetPreferredSize(
            text,
            maxFontSize,
            false,
            1,
            state.OriginalLineSpacing,
            float.PositiveInfinity);
        var widthFontSize = preferredSize.x > 0f
            ? maxFontSize * availableSize.x / preferredSize.x
            : maxFontSize;

        // These labels sit on deliberately shallow one-line plates. TMP's regular
        // auto-sizing also fits the font's line metrics to that height, which shrinks
        // even short captions. Only reduce the original size when the actual text is
        // wider than the plate.
        ApplyConstrainedSingleLineStyle(
            text,
            Mathf.Clamp(widthFontSize, minFontSize, maxFontSize),
            state);
    }

    private static void ApplyConstrainedSingleLineStyle(
        TMP_Text text,
        float fontSize,
        TextFitState state)
    {
        text.enableAutoSizing = false;
        text.enableWordWrapping = false;
        text.maxVisibleLines = state.OriginalMaxVisibleLines;
        // The HUD plates are shallower than the font's normal line metrics. Preserve
        // their original vertical overflow policy so a short caption is not clipped or
        // ellipsized merely because only horizontal fitting was requested.
        text.overflowMode = state.OriginalOverflowMode;
        text.autoSizeTextContainer = false;
        text.fontSize = fontSize;
        text.fontSizeMax = state.OriginalFontSizeMax;
        text.fontSizeMin = fontSize;
        ApplyCjkLineSpacing(text, false, state);
        text.SetLayoutDirty();
        text.SetVerticesDirty();
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

    private sealed class VerticalGlyphRule
    {
        internal VerticalGlyphRule(string[] preferredCandidates, string forcedFallback = null)
        {
            PreferredCandidates = preferredCandidates;
            ForcedFallback = forcedFallback;
        }

        internal string ForcedFallback { get; }

        internal string[] PreferredCandidates { get; }
    }

    private sealed class TextFitState : MonoBehaviour
    {
        internal float OriginalFontSizeMax { get; private set; }
        internal float OriginalLineSpacing { get; private set; }
        internal int OriginalMaxVisibleLines { get; private set; }
        internal TextOverflowModes OriginalOverflowMode { get; private set; }
        internal Quaternion OriginalLocalRotation { get; private set; }
        internal TextAlignmentOptions OriginalAlignment { get; private set; }
        internal bool OriginalEnabled { get; private set; }

        private bool Captured { get; set; }
        private Vector2 LastAvailableSize { get; set; }
        private float LastAbsoluteMin { get; set; }
        private bool LastCjkCompactSpacing { get; set; }
        private TMP_FontAsset LastFont { get; set; }
        private float LastMaxFontSize { get; set; }
        private float LastMinFontScale { get; set; }
        private string LastMode { get; set; }
        private string LastText { get; set; }
        private string LastSideLabelFormattedText { get; set; }
        private string LastSideLabelSourceText { get; set; }
        private TMP_Text SideLabelProxy { get; set; }

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
            OriginalMaxVisibleLines = text.maxVisibleLines;
            OriginalOverflowMode = text.overflowMode;
            OriginalLocalRotation = text.rectTransform
                ? text.rectTransform.localRotation
                : Quaternion.identity;
            OriginalAlignment = text.alignment;
            OriginalEnabled = text.enabled;
            Captured = true;
        }

        internal TMP_Text GetOrCreateSideLabelProxy(TMP_Text source)
        {
            if (SideLabelProxy)
            {
                return SideLabelProxy;
            }

            if (!source || !source.rectTransform)
            {
                return null;
            }

            var gameObject = new GameObject(SideLabelProxyName, typeof(RectTransform), typeof(TextMeshProUGUI))
            {
                layer = source.gameObject.layer
            };

            gameObject.transform.SetParent(source.rectTransform, false);

            SideLabelProxy = gameObject.GetComponent<TextMeshProUGUI>();
            SideLabelProxy.enabled = false;
            SideLabelProxy.raycastTarget = false;
            SideLabelProxy.gameObject.SetActive(false);

            return SideLabelProxy;
        }

        internal void HideSideLabelProxy()
        {
            if (!SideLabelProxy)
            {
                return;
            }

            SideLabelProxy.enabled = false;
            SideLabelProxy.gameObject.SetActive(false);
        }

        internal string GetSideLabelSourceText(string currentText)
        {
            return string.Equals(currentText, LastSideLabelFormattedText, StringComparison.Ordinal)
                ? LastSideLabelSourceText
                : currentText;
        }

        internal void RememberSideLabelText(string sourceText, string formattedText)
        {
            LastSideLabelSourceText = sourceText;
            LastSideLabelFormattedText = formattedText;
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

    private sealed class DeferredConstrainedLabelFit : MonoBehaviour
    {
        private float AbsoluteMin { get; set; }

        private Coroutine Coroutine { get; set; }

        private TMP_Text Text { get; set; }

        private float MinFontScale { get; set; }

        internal void Schedule(TMP_Text text, float minFontScale, float absoluteMin)
        {
            Text = text;
            MinFontScale = minFontScale;
            AbsoluteMin = absoluteMin;

            if (Coroutine == null)
            {
                Coroutine = StartCoroutine(ApplyLater());
            }
        }

        private IEnumerator ApplyLater()
        {
            for (var i = 0; i < DeferredConstrainedLabelFitFrames; i++)
            {
                yield return null;
                ApplyConstrainedSingleLineFit(Text, MinFontScale, AbsoluteMin);
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
