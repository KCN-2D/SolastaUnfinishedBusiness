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
    private const int DeferredSingleLineFitFrames = 2;
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

    internal static void FitResourceCounter(GuiLabel label)
    {
        var text = label?.TMP_Text;

        if (!text)
        {
            return;
        }

        // Bind can populate a hidden panel; refit once its native layout becomes active.
        ApplyConstrainedSingleLineFit(text, StatValueMinFontScale, StatValueAbsoluteMinFontSize);
        ScheduleSingleLineFit(text, StatValueMinFontScale, StatValueAbsoluteMinFontSize, false);
    }

    internal static void FitConstrainedSingleLine(GuiLabel label, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        FitConstrainedSingleLine(label?.TMP_Text, minFontScale, absoluteMin);
    }

    internal static void FitSettingsTabs(SettingsPanel panel)
    {
        if (!panel || !panel.tabTogglesContainer)
        {
            return;
        }

        var layout = panel.GetComponent<SettingsTabsLayoutState>() ??
                     panel.gameObject.AddComponent<SettingsTabsLayoutState>();
        layout.Schedule(panel);
    }

    internal static void FitSettingItemCaption(SettingItem item)
    {
        if (item is SettingKeyMappingItem keyMapping)
        {
            FitSettingKeyMapping(keyMapping);
            return;
        }

        ScheduleSettingCaption(item, item?.TitleLabel);
    }

    internal static void FitSettingKeyMapping(SettingKeyMappingItem item)
    {
        if (!item || !item.TitleLabel?.TMP_Text || item.bindingBoxes.Length == 0 || !item.orSeparator)
        {
            return;
        }

        var layout = item.GetComponent<SettingKeyMappingLayoutState>() ??
                     item.gameObject.AddComponent<SettingKeyMappingLayoutState>();
        layout.Schedule(item);
    }

    internal static void FitSettingChoiceCaption(SettingRadioChoice choice)
    {
        ScheduleSettingCaption(choice, choice?.titleLabel);

        if (choice && choice.GetComponentInParent<SettingRadioListItem>() is { } owner)
        {
            FitSettingItemCaption(owner);
        }
    }

    private static void ScheduleSettingCaption(GuiBehaviour row, GuiLabel label)
    {
        if (!row || !label || !label.TMP_Text)
        {
            return;
        }

        var layout = row.GetComponent<SettingCaptionLayoutState>() ??
                     row.gameObject.AddComponent<SettingCaptionLayoutState>();
        layout.Schedule(row, label.TMP_Text);
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

        ScheduleSingleLineFit(text, minFontScale, absoluteMin, false);
    }

    internal static Rect GetWorldRect(RectTransform rectTransform)
    {
        if (!rectTransform)
        {
            return default;
        }

        var corners = new Vector3[4];

        rectTransform.GetWorldCorners(corners);
        var xMin = Mathf.Min(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        var yMin = Mathf.Min(corners[0].y, corners[1].y, corners[2].y, corners[3].y);
        var xMax = Mathf.Max(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        var yMax = Mathf.Max(corners[0].y, corners[1].y, corners[2].y, corners[3].y);

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    internal static Rect GetWorldTextBounds(TMP_Text text)
    {
        if (!text)
        {
            return default;
        }

        text.ForceMeshUpdate();

        var bounds = text.textBounds;
        var min = bounds.min;
        var max = bounds.max;
        var bottomLeft = text.transform.TransformPoint(min.x, min.y, 0);
        var topLeft = text.transform.TransformPoint(min.x, max.y, 0);
        var bottomRight = text.transform.TransformPoint(max.x, min.y, 0);
        var topRight = text.transform.TransformPoint(max.x, max.y, 0);
        var xMin = Mathf.Min(bottomLeft.x, topLeft.x, bottomRight.x, topRight.x);
        var xMax = Mathf.Max(bottomLeft.x, topLeft.x, bottomRight.x, topRight.x);
        var yMin = Mathf.Min(bottomLeft.y, topLeft.y, bottomRight.y, topRight.y);
        var yMax = Mathf.Max(bottomLeft.y, topLeft.y, bottomRight.y, topRight.y);

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
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
        float absoluteMin = TitleAbsoluteMinFontSize, int maxVisibleLines = 2)
    {
        if (!label)
        {
            return;
        }

        FitCardTitle(label.TMP_Text, minFontScale, absoluteMin, maxVisibleLines);
    }

    internal static void FitSingleLine(TMP_Text text, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!text)
        {
            return;
        }

        ApplySingleLineFit(text, minFontScale, absoluteMin);
        ScheduleSingleLineFit(text, minFontScale, absoluteMin, true);
    }

    private static void ApplySingleLineFit(TMP_Text text, float minFontScale, float absoluteMin)
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

        // Native TMP preferred-height autosizing can leave fallback-font submeshes uninitialized.
        // Measure explicitly and keep the chosen size fixed, as the card-title fitter does.
        var fontSize = TryGetTextContentSize(text, out var availableSize)
            ? Mathf.Clamp(GetSingleLineFontSize(text, availableSize, maxFontSize), minFontSize, maxFontSize)
            : maxFontSize;

        ApplyCardTextFit(text, 1, false, fontSize, state);
    }

    private static void ScheduleSingleLineFit(
        TMP_Text text,
        float minFontScale,
        float absoluteMin,
        bool constrainHeight)
    {
        var deferredFit = text.GetComponent<DeferredSingleLineFit>() ??
                          text.gameObject.AddComponent<DeferredSingleLineFit>();

        deferredFit.Schedule(text, minFontScale, absoluteMin, constrainHeight);
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
        float absoluteMin = TitleAbsoluteMinFontSize, int maxVisibleLines = 2)
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
        maxVisibleLines = Math.Max(1, maxVisibleLines);
        var fitKind = maxVisibleLines == 2 ? nameof(FitCardTitle) : $"{nameof(FitCardTitle)}:{maxVisibleLines}";

        if (state.HasFitSignature(
                fitKind,
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
                fitKind,
                text,
                availableSize,
                minFontScale,
                absoluteMin,
                useCjkCompactSpacing);
            return;
        }

        ApplyCardTextFit(text, maxVisibleLines, true,
            GetWrappedFontSize(text, availableSize, maxFontSize, minFontSize, maxVisibleLines, state), state);
        state.RememberFitSignature(
            fitKind,
            text,
            availableSize,
            minFontScale,
            absoluteMin,
            useCjkCompactSpacing);
    }

    internal static Vector2 GetReadableTitleMinimumSize(TMP_Text text, float minFontScale, float absoluteMin,
        out float singleLineWidth)
    {
        singleLineWidth = 0f;
        var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();
        state.Capture(text);
        if (!TryGetFontSizeBounds(text, state, minFontScale, absoluteMin, out var maximum, out var minimum))
        {
            return Vector2.zero;
        }

        var minimumLine = MeasureTitleGlyphs(text, minimum, false, 0f, state, out _);
        var maximumLine = MeasureTitleGlyphs(text, maximum, false, 0f, state, out _);
        singleLineWidth = minimumLine.x + 4f;
        var low = minimumLine.x * 0.5f;
        var high = singleLineWidth;

        // Long words may need more than half the one-line width. Measure the actual
        // localized wrapping rather than assuming that a two-line limit preserves every glyph.
        for (var iteration = 0; iteration < CardFitSearchIterations; iteration++)
        {
            var width = (low + high) * 0.5f;
            var glyphs = MeasureTitleGlyphs(text, minimum, true, width, state, out var lines);
            if (lines <= 2 && glyphs.x + 4f <= width)
            {
                high = width;
            }
            else
            {
                low = width;
            }
        }

        // Fallback-font glyphs and their outline can exceed TMP's preferred line
        // height. Reserve padding around both rows instead of clipping their ink.
        return new Vector2(Mathf.Ceil(high + 2f), Mathf.Ceil(maximumLine.y * 2.5f + 8f));
    }

    internal static void FitReadableTitle(TMP_Text text, float minFontScale, float absoluteMin,
        int maxVisibleLines = 2)
    {
        var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();
        state.Capture(text);
        if (!TryGetFontSizeBounds(text, state, minFontScale, absoluteMin, out var maximum, out var minimum) ||
            !TryGetTextContentSize(text, out var available))
        {
            return;
        }

        var compactSpacing = ShouldUseCjkCompactLineSpacing(text);
        maxVisibleLines = Math.Max(1, maxVisibleLines);
        var fitKind = $"{nameof(FitReadableTitle)}:{maxVisibleLines}";
        if (state.HasFitSignature(fitKind, text, available, minFontScale, absoluteMin, compactSpacing))
        {
            return;
        }

        var singleLine = MeasureTitleGlyphs(text, maximum, false, 0f, state, out _);
        var wrap = singleLine.x + 4f > available.x || singleLine.y + 4f > available.y;
        var fontSize = maximum;
        if (wrap)
        {
            var low = minimum;
            var high = maximum;
            for (var iteration = 0; iteration < CardFitSearchIterations; iteration++)
            {
                var candidate = (low + high) * 0.5f;
                var glyphs = MeasureTitleGlyphs(text, candidate, true, available.x, state, out var lines);
                if (lines <= maxVisibleLines && glyphs.x + 4f <= available.x && glyphs.y + 4f <= available.y)
                {
                    low = candidate;
                }
                else
                {
                    high = candidate;
                }
            }

            fontSize = low;
        }

        ApplyCardTextFit(text, wrap ? maxVisibleLines : 1, wrap, fontSize, state);
        text.overflowMode = TextOverflowModes.Overflow;
        text.ForceMeshUpdate(true);
        state.RememberFitSignature(fitKind, text, available, minFontScale, absoluteMin, compactSpacing);
    }

    private static Vector2 MeasureTitleGlyphs(TMP_Text text, float fontSize, bool wrap, float width,
        TextFitState state, out int lines)
    {
        var rect = text.rectTransform;
        var size = rect.sizeDelta;
        var previousFontSize = text.fontSize;
        var autoSizing = text.enableAutoSizing;
        var wrapping = text.enableWordWrapping;
        var visibleLines = text.maxVisibleLines;
        var overflow = text.overflowMode;
        var spacing = text.lineSpacing;
        var container = text.autoSizeTextContainer;
        try
        {
            text.enableAutoSizing = false;
            text.autoSizeTextContainer = false;
            text.enableWordWrapping = wrap;
            text.maxVisibleLines = int.MaxValue;
            text.overflowMode = TextOverflowModes.Overflow;
            text.fontSize = fontSize;
            ApplyCjkLineSpacing(text, wrap, state);
            var margin = text.margin;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                (wrap ? width : Mathf.Max(1f, text.preferredWidth) * 2f) + margin.x + margin.z);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Max(rect.rect.height, fontSize * Math.Max(1, text.text.Length) * 3f));
            text.ForceMeshUpdate(true);
            lines = text.textInfo.lineCount;
            return text.textBounds.size;
        }
        finally
        {
            rect.sizeDelta = size;
            text.fontSize = previousFontSize;
            text.enableAutoSizing = autoSizing;
            text.enableWordWrapping = wrapping;
            text.maxVisibleLines = visibleLines;
            text.overflowMode = overflow;
            text.lineSpacing = spacing;
            text.autoSizeTextContainer = container;
        }
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

    private static float GetWrappedFontSize(
        TMP_Text text,
        Vector2 availableSize,
        float maxFontSize,
        float minFontSize,
        int maxVisibleLines,
        TextFitState state)
    {
        if (!DoesWrappedTextFit(text, availableSize, minFontSize, maxVisibleLines, state))
        {
            return minFontSize;
        }

        var low = minFontSize;
        var high = maxFontSize;

        for (var i = 0; i < CardFitSearchIterations; i++)
        {
            var mid = (low + high) * 0.5f;

            if (DoesWrappedTextFit(text, availableSize, mid, maxVisibleLines, state))
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

    private static bool DoesWrappedTextFit(
        TMP_Text text, Vector2 availableSize, float fontSize, int maxVisibleLines, TextFitState state)
    {
        var preferredSize = GetPreferredSize(
            text,
            fontSize,
            true,
            maxVisibleLines,
            ShouldUseCjkCompactLineSpacing(text) ? CjkTwoLineSpacing : state.OriginalLineSpacing,
            availableSize.x);

        // This TMP version sums automatic wrapped-line widths in preferred.x.
        // The wrapping width is already supplied to its layout calculation; use
        // the complete wrapped height to choose the font size.
        return preferredSize.y <= availableSize.y + PreferredSizeTolerance;
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

        var layout = spellBox.GetComponent<SpellBoxLayoutState>() ??
                     spellBox.gameObject.AddComponent<SpellBoxLayoutState>();
        layout.Apply(spellBox);
        FitSpellBoxSourceTitle(spellBox);
    }

    internal static void BeginSpellBoxGridLayout(SpellsByLevelGroup group)
    {
        GetSpellBoxGridLayout(group.spellsTable, group)?.Begin();
    }

    internal static void EndSpellBoxGridLayout(SpellsByLevelGroup group)
    {
        GetSpellBoxGridLayout(group.spellsTable, group)?.End();
    }

    internal static void RestoreSpellBoxLayout(SpellBox box)
    {
        box.GetComponent<SpellBoxLayoutState>()?.Restore();
    }

    private static SpellBoxGridLayoutState GetSpellBoxGridLayout(Transform parent, SpellsByLevelGroup group = null)
    {
        var grid = parent ? parent.GetComponent<GridLayoutGroup>() : null;
        if (!grid)
        {
            return null;
        }

        var state = grid.GetComponent<SpellBoxGridLayoutState>() ??
                    grid.gameObject.AddComponent<SpellBoxGridLayoutState>();
        for (var ancestor = parent; ancestor && !group; ancestor = ancestor.parent)
        {
            group = ancestor.GetComponent<SpellsByLevelGroup>();
        }

        state.Capture(grid, group);
        return state;
    }

    private sealed class SpellBoxLayoutState : MonoBehaviour
    {
        private bool _captured;
        private float _titleHeight;
        private Vector2 _imageOffsetMin;
        private Vector2 _imageOffsetMax;
        private Vector2 _backgroundOffsetMin;
        private Vector2 _backgroundOffsetMax;
        private float _backgroundParentHeight;
        private bool _preserveAspect;
        private string _text;
        private TMP_FontAsset _font;
        private float _width;
        private float _fontSize;
        private float _height;
        private bool _cjkSpacing;
        private SpellBox _box;
        private SpellBoxGridLayoutState _grid;
        private float _nativeCardHeight;
        private float _nativeImageHeight;
        private float _minimumImageHeight;
        private readonly List<(RectTransform Transform, Vector2 Position, Graphic[] Graphics)> _statusGroups = [];
        private readonly Vector3[] _corners = new Vector3[4];

        internal float RequiredHeight { get; private set; }

        internal void Apply(SpellBox box)
        {
            var text = box.titleLabel?.TMP_Text;
            var image = box.spellImage;
            if (!text)
            {
                return;
            }

            if (!box.titleGroup || !box.titleTransform || !image)
            {
                var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();
                state.Capture(text);
                ApplyCardTextFit(text, int.MaxValue, true, state.OriginalFontSizeMax, state);
                text.overflowMode = TextOverflowModes.Overflow;
                return;
            }

            var imageRect = image.rectTransform;
            _box = box;
            _grid = GetSpellBoxGridLayout(box.transform.parent);
            if (!_captured)
            {
                _titleHeight = SpellBox.TitleGroupMinHeight;
                _imageOffsetMin = imageRect.offsetMin;
                _imageOffsetMax = imageRect.offsetMax;
                _preserveAspect = image.preserveAspect;
                if (box.titleBackground)
                {
                    var background = box.titleBackground.rectTransform;
                    _backgroundOffsetMin = background.offsetMin;
                    _backgroundOffsetMax = background.offsetMax;
                    _backgroundParentHeight = ((RectTransform)background.parent).rect.height;
                }

                _nativeCardHeight = _grid ? _grid.NativeCellHeight : box.RectTransform.rect.height;
                _nativeImageHeight = Mathf.Max(1f, _imageOffsetMax.y - _imageOffsetMin.y +
                    imageRect.parent.GetComponent<RectTransform>().rect.height *
                    (imageRect.anchorMax.y - imageRect.anchorMin.y));
                CaptureStatusGroup(box.preparationGroup);
                CaptureStatusGroup(box.selectecToLearnGroup);
                CaptureStatusGroup(box.availableToLearnGroup);
                CaptureStatusGroup(box.ritualGroup);
                CaptureStatusGroup(box.unlearnedGroup);
                _minimumImageHeight = Mathf.Max(_minimumImageHeight, _nativeImageHeight);
                _captured = true;
            }

            // Native Refresh keeps the non-hovered title background at one row,
            // while allowing its text to grow over the icon. Reserve the complete
            // title at its native font size, then move the icon with the background.
            var cjkSpacing = ShouldUseCjkCompactLineSpacing(text);
            if (_text != text.text || _font != text.font ||
                Mathf.Abs(_width - text.rectTransform.rect.width) > PreferredSizeTolerance ||
                Mathf.Abs(_fontSize - text.fontSize) > 0.01f || _cjkSpacing != cjkSpacing ||
                text.enableAutoSizing || !text.enableWordWrapping ||
                text.maxVisibleLines != int.MaxValue || text.overflowMode != TextOverflowModes.Overflow)
            {
                var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();
                state.Capture(text);
                ApplyCardTextFit(text, int.MaxValue, true, state.OriginalFontSizeMax, state);
                text.overflowMode = TextOverflowModes.Overflow;
                text.ForceMeshUpdate(true);
                _height = Mathf.Max(Mathf.Ceil(text.textBounds.size.y) + 8f, _titleHeight);
                RequiredHeight = _nativeCardHeight + _height - _titleHeight +
                                 _minimumImageHeight - _nativeImageHeight;
                _text = text.text;
                _font = text.font;
                _width = text.rectTransform.rect.width;
                _fontSize = text.fontSize;
                _cjkSpacing = cjkSpacing;
            }

            if (_grid)
            {
                _grid.Refresh();
            }

            ApplyGeometry(_grid ? _grid.CellHeight : RequiredHeight);
        }

        internal void ApplyGeometry(float cardHeight)
        {
            if (!_captured || !_box)
            {
                return;
            }

            _box.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, cardHeight);
            var image = _box.spellImage;
            var imageRect = image.rectTransform;
            var cardGrowth = cardHeight - _nativeCardHeight;
            // A grid row shares one cell height. Align its artwork without stretching
            // short-title images or shrinking long-title images to fit that cell.
            var headerHeight = Mathf.Max(_height,
                _titleHeight + cardGrowth - (_minimumImageHeight - _nativeImageHeight));
            _box.titleGroup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, headerHeight);
            _box.titleTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, headerHeight);
            var titleGrowth = headerHeight - _titleHeight;
            if (_box.titleBackground)
            {
                var background = _box.titleBackground.rectTransform;
                var parentGrowth = ((RectTransform)background.parent).rect.height - _backgroundParentHeight;
                // Keep the native background's overscan inside the title mask while
                // extending its visible plate with the complete shared header.
                background.offsetMin = _backgroundOffsetMin + Vector2.up *
                    (parentGrowth * (1f - background.anchorMin.y) - titleGrowth);
                background.offsetMax = _backgroundOffsetMax + Vector2.up *
                    (parentGrowth * (1f - background.anchorMax.y));
            }

            imageRect.offsetMin = _imageOffsetMin + Vector2.down * (cardGrowth * imageRect.anchorMin.y);
            imageRect.offsetMax = _imageOffsetMax + Vector2.up *
                (cardGrowth * (1f - imageRect.anchorMax.y) - titleGrowth);
            image.preserveAspect = _preserveAspect;
            FitStatusGroups(imageRect, Mathf.Abs(cardGrowth) <= PreferredSizeTolerance &&
                Mathf.Abs(titleGrowth) <= PreferredSizeTolerance);
        }

        private void CaptureStatusGroup(RectTransform group)
        {
            if (!group)
            {
                return;
            }

            var graphics = group.GetComponentsInChildren<Graphic>(true);
            _statusGroups.Add((group, group.anchoredPosition, graphics));
            var minimum = float.PositiveInfinity;
            var maximum = float.NegativeInfinity;
            foreach (var graphic in graphics)
            {
                if (!graphic)
                {
                    continue;
                }

                GetVerticalBounds(graphic.rectTransform, out var low, out var high);
                minimum = Mathf.Min(minimum, low);
                maximum = Mathf.Max(maximum, high);
            }

            if (!float.IsPositiveInfinity(minimum))
            {
                // Reserve every native status state before binding, including states
                // that become visible after the inactive card is first shown.
                _minimumImageHeight = Mathf.Max(_minimumImageHeight, maximum - minimum);
            }
        }

        private void FitStatusGroups(RectTransform image, bool restoreOnly)
        {
            GetVerticalBounds(image, out var imageMin, out var imageMax);
            foreach (var (transform, position, graphics) in _statusGroups)
            {
                if (!transform)
                {
                    continue;
                }

                transform.anchoredPosition = position;
                if (restoreOnly || !transform.gameObject.activeSelf)
                {
                    continue;
                }

                var minimum = float.PositiveInfinity;
                var maximum = float.NegativeInfinity;
                foreach (var graphic in graphics)
                {
                    if (!graphic || !graphic.enabled)
                    {
                        continue;
                    }

                    var active = true;
                    for (var ancestor = graphic.transform; ancestor && ancestor != transform; ancestor = ancestor.parent)
                    {
                        if (!ancestor.gameObject.activeSelf)
                        {
                            active = false;
                            break;
                        }
                    }

                    if (!active)
                    {
                        continue;
                    }

                    GetVerticalBounds(graphic.rectTransform, out var low, out var high);
                    minimum = Mathf.Min(minimum, low);
                    maximum = Mathf.Max(maximum, high);
                }

                if (float.IsPositiveInfinity(minimum))
                {
                    continue;
                }

                // Native status icons are siblings of the artwork. Keep their original size,
                // but move any icon that the expanded title would otherwise cover into the artwork.
                var lowerOffset = imageMin - minimum;
                var upperOffset = imageMax - maximum;
                var offset = lowerOffset <= upperOffset ? Mathf.Clamp(0f, lowerOffset, upperOffset) : upperOffset;
                if (Mathf.Abs(offset) <= PreferredSizeTolerance)
                {
                    continue;
                }

                var movement = _box.RectTransform.TransformVector(new Vector3(0f, offset, 0f));
                var shift = transform.parent.InverseTransformVector(movement);
                transform.anchoredPosition = position + new Vector2(shift.x, shift.y);
            }
        }

        private void GetVerticalBounds(RectTransform rect, out float minimum, out float maximum)
        {
            rect.GetWorldCorners(_corners);
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;
            foreach (var corner in _corners)
            {
                var height = _box.RectTransform.InverseTransformPoint(corner).y;
                minimum = Mathf.Min(minimum, height);
                maximum = Mathf.Max(maximum, height);
            }
        }

        internal void Restore()
        {
            if (!_captured)
            {
                return;
            }

            _height = _titleHeight;
            RequiredHeight = _nativeCardHeight;
            _text = null;
            ApplyGeometry(_nativeCardHeight);
            _grid?.Refresh();
        }

        private void OnEnable()
        {
            if (_captured && _box && _text != null)
            {
                KeepSpellBoxTextInside(_box);
            }
        }

        private void OnDisable()
        {
            // Hiding a bound card keeps its measured title. Unbind and reparenting
            // restore the native geometry when the card actually leaves its list.
            _grid?.Refresh();
        }

        private void OnTransformParentChanged()
        {
            Restore();
            _grid = null;
        }
    }

    private sealed class SpellBoxGridLayoutState : MonoBehaviour
    {
        private GridLayoutGroup _grid;
        private SpellsByLevelGroup _group;
        private Vector2 _nativeCellSize;
        private int _nativeColumns;
        private float _nativeTableWidth;
        private float _nativeGroupWidth;
        private bool _binding;
        private bool _refreshing;

        internal float NativeCellHeight => _nativeCellSize.y;
        internal float CellHeight => _grid.cellSize.y;

        internal void Capture(GridLayoutGroup grid, SpellsByLevelGroup group)
        {
            if (_grid)
            {
                if (!_group && group)
                {
                    _group = group;
                    _nativeGroupWidth = group.RectTransform.rect.width;
                }

                return;
            }

            _grid = grid;
            _group = group;
            _nativeCellSize = grid.cellSize;
            CaptureBoundSize();
        }

        private void CaptureBoundSize()
        {
            _nativeColumns = _grid.constraintCount;
            _nativeTableWidth = ((RectTransform)_grid.transform).rect.width;
            _nativeGroupWidth = _group ? _group.RectTransform.rect.width : _nativeTableWidth;
        }

        internal void Begin()
        {
            RestoreNativeSize();
            _binding = true;
        }

        internal void End()
        {
            CaptureBoundSize();
            _binding = false;
            Refresh();
        }

        internal void Refresh()
        {
            if (_binding || _refreshing || !_grid || !_grid.gameObject.activeSelf)
            {
                return;
            }

            var height = _nativeCellSize.y;
            var count = 0;
            foreach (Transform child in _grid.transform)
            {
                if (!child.gameObject.activeSelf || !child.GetComponent<SpellBox>())
                {
                    continue;
                }

                count++;
                var layout = child.GetComponent<SpellBoxLayoutState>();
                if (layout)
                {
                    height = Mathf.Max(height, layout.RequiredHeight);
                }
            }

            var columns = _nativeColumns;
            // These native tables have a fixed height and scroll horizontally.
            // Fit rows to that height instead of letting taller cards escape the viewport.
            if (_group && _grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount &&
                height > _nativeCellSize.y + PreferredSizeTolerance)
            {
                var available = ((RectTransform)_grid.transform).rect.height - _grid.padding.vertical;
                var rows = Mathf.Max(1, Mathf.FloorToInt((available + _grid.spacing.y) / (height + _grid.spacing.y)));
                columns = Mathf.Max(columns, Mathf.CeilToInt(count / (float)rows));
            }

            if (Mathf.Abs(_grid.cellSize.y - height) <= PreferredSizeTolerance &&
                _grid.constraintCount == columns)
            {
                return;
            }

            _refreshing = true;
            try
            {
                _grid.cellSize = new Vector2(_nativeCellSize.x, height);
                _grid.constraintCount = columns;
                var table = (RectTransform)_grid.transform;
                var expanded = height > _nativeCellSize.y + PreferredSizeTolerance;
                var width = expanded ? Mathf.Max(_nativeTableWidth,
                    _grid.padding.horizontal + columns * _grid.cellSize.x +
                    Mathf.Max(0, columns - 1) * _grid.spacing.x) : _nativeTableWidth;
                table.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                if (_group)
                {
                    _group.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                        expanded ? Mathf.Max(_nativeGroupWidth, width) : _nativeGroupWidth);
                }

                foreach (Transform child in _grid.transform)
                {
                    if (child.gameObject.activeSelf)
                    {
                        child.GetComponent<SpellBoxLayoutState>()?.ApplyGeometry(height);
                    }
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(table);
                RefreshScrollWidth();
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void RefreshScrollWidth()
        {
            var table = _group ? _group.transform.parent as RectTransform : null;
            var layout = table ? table.GetComponent<HorizontalLayoutGroup>() : null;
            if (!layout || !table.gameObject.activeSelf)
            {
                return;
            }

            var fitter = table.GetComponent<ContentSizeFitter>();
            if (!fitter || fitter.horizontalFit == ContentSizeFitter.FitMode.Unconstrained)
            {
                var totalWidth = 0f;
                var lastWidth = 0f;
                foreach (Transform child in table)
                {
                    if (!child.gameObject.activeSelf || !child.GetComponent<SpellsByLevelGroup>())
                    {
                        continue;
                    }

                    lastWidth = ((RectTransform)child).rect.width + layout.spacing;
                    totalWidth += lastWidth;
                }

                var scroll = table.GetComponentInParent<ScrollRect>();
                totalWidth += scroll ? ((RectTransform)scroll.transform).rect.width - lastWidth :
                    layout.padding.horizontal - layout.spacing;
                table.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(0f, totalWidth));
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        }

        private void RestoreNativeSize()
        {
            if (!_grid)
            {
                return;
            }

            _grid.cellSize = _nativeCellSize;
            _grid.constraintCount = _nativeColumns;
            ((RectTransform)_grid.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _nativeTableWidth);
            if (_group)
            {
                _group.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _nativeGroupWidth);
            }
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void OnDisable()
        {
            _binding = false;
        }
    }

    private static void FitSpellBoxSourceTitle(SpellBox spellBox)
    {
        var label = spellBox.autoPreparedTitle;
        var text = label?.TMP_Text;
        var group = spellBox.autoPreparedGroup;
        var card = spellBox.transform as RectTransform;

        if (!text || !group || !card)
        {
            FitSingleLine(label, TagMinFontScale, TagAbsoluteMinFontSize);
            return;
        }

        // Ordinary spells have no acquisition-source badge. Native Bind/Refresh
        // still calls this helper, but hidden or empty badges need no layout pass.
        if (!group.gameObject.activeSelf || string.IsNullOrEmpty(text.text))
        {
            return;
        }

        var layout = group.GetComponent<HorizontalLayoutGroup>();
        var extraWidth = layout ? layout.padding.horizontal :
            Mathf.Max(0f, group.rect.width - text.rectTransform.rect.width);

        if (layout)
        {
            var childCount = 0;

            foreach (Transform child in group)
            {
                if (!child.gameObject.activeSelf || child is not RectTransform childRect ||
                    child.GetComponent<LayoutElement>() is { ignoreLayout: true })
                {
                    continue;
                }

                childCount++;

                if (childRect != text.rectTransform)
                {
                    extraWidth += Mathf.Max(0f, LayoutUtility.GetPreferredWidth(childRect));
                }
            }

            extraWidth += Mathf.Max(0, childCount - 1) * layout.spacing;
        }
        var availableWidth = card.rect.width - extraWidth;

        if (availableWidth <= 0f)
        {
            return;
        }

        var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();
        state.Capture(text);

        var preferredSize = GetPreferredSize(text, state.OriginalFontSizeMax, false, 1,
            state.OriginalLineSpacing, float.PositiveInfinity);
        var layoutElement = text.GetComponent<LayoutElement>() ?? text.gameObject.AddComponent<LayoutElement>();

        // Bound the native content-size fitter before fitting text; otherwise long source names grow past the card.
        layoutElement.minWidth = Mathf.Min(layoutElement.minWidth, availableWidth);
        layoutElement.preferredWidth = Mathf.Min(preferredSize.x + text.margin.x + text.margin.z, availableWidth);

        var wrappedSize = GetPreferredSize(text, state.OriginalFontSizeMax, true, 2,
            state.OriginalLineSpacing, availableWidth);
        var originalHeight = Mathf.Max(state.OriginalRectHeight, state.OriginalFontSizeMax);
        layoutElement.preferredHeight = Mathf.Clamp(wrappedSize.y + text.margin.y + text.margin.w,
            originalHeight, originalHeight * 2f);

        if (layout)
        {
            layout.childControlHeight = true;
        }

        if (group.GetComponent<ContentSizeFitter>() is { } fitter)
        {
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(group);
        FitCardTitle(label, TagMinFontScale, TagAbsoluteMinFontSize);
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

    private sealed class SettingKeyMappingLayoutState : MonoBehaviour
    {
        private readonly List<(TMP_Text Text, string Value, TMP_FontAsset Font, bool Active)> _texts = [];
        private SettingKeyMappingItem _row;
        private RectTransform _table;
        private TMP_Text _separator;
        private LayoutElement _element;
        private float _rowHeight;
        private float _boxHeight;
        private float _captionHeight;
        private float _boxWidth;
        private float _leftInset;
        private float _rightInset;
        private float _verticalInset;
        private float _gap;
        private float _minimumHeight;
        private float _preferredHeight;
        private float _lastWidth = -1f;
        private bool _lastSecondary;
        private bool _dirty;
        private int _deferredFrames;

        internal void Schedule(SettingKeyMappingItem row)
        {
            _row = row;
            _dirty = true;
            _deferredFrames = DeferredSingleLineFitFrames;
        }

        private void LateUpdate()
        {
            if (!_row || !_row.gameObject.activeInHierarchy || IsCanvasRebuildInProgress())
            {
                return;
            }

            if (_deferredFrames > 0)
            {
                _deferredFrames--;
                return;
            }

            var rect = _row.RectTransform;
            var width = rect.rect.width;
            var secondary = _row.bindingBoxes.Length > 1 && _row.bindingBoxes[1].gameObject.activeSelf;
            if (width <= 0f || !_dirty && !HasChanged(width, secondary))
            {
                return;
            }

            if (!_table)
            {
                var caption = _row.TitleLabel.RectTransform;
                _table = _row.bindingBoxes[0].parent as RectTransform;
                _separator = _row.orSeparator.GetComponent<TMP_Text>();
                if (!_table || !_separator)
                {
                    return;
                }

                _rowHeight = rect.rect.height;
                _boxHeight = _row.bindingBoxes[0].rect.height;
                _boxWidth = _row.bindingBoxes[0].rect.width;
                _captionHeight = caption.rect.height;
                var captionLeft = rect.InverseTransformPoint(caption.TransformPoint(caption.rect.min)).x;
                var tableRight = rect.InverseTransformPoint(_table.TransformPoint(_table.rect.max)).x;
                _leftInset = Mathf.Max(0f, captionLeft - rect.rect.xMin);
                _rightInset = Mathf.Max(0f, rect.rect.xMax - tableRight);
                _verticalInset = Mathf.Max(0f, (_rowHeight - Mathf.Max(_captionHeight, _boxHeight)) / 2f);
                var group = _table.GetComponent<HorizontalLayoutGroup>();
                _gap = group ? Mathf.Max(0f, group.spacing) : 10f;
                if (group)
                {
                    group.enabled = false;
                }

                if (_table.GetComponent<ContentSizeFitter>() is { } fitter)
                {
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                }

                _element = _row.GetComponent<LayoutElement>() ?? _row.gameObject.AddComponent<LayoutElement>();
                _minimumHeight = _element.minHeight;
                _preferredHeight = _element.preferredHeight;
                AddText(_row.TitleLabel.TMP_Text);
                AddText(_separator);
                foreach (var label in _row.bindingLabels)
                {
                    AddText(label.TMP_Text);
                }

                foreach (var label in _row.unboundLabels)
                {
                    AddText(label.TMP_Text);
                }
            }

            var title = _row.TitleLabel.TMP_Text;
            Prepare(title, true);
            Prepare(_separator, false);
            var separatorWidth = Mathf.Ceil(_separator.GetPreferredValues(_separator.text).x + 2f);
            var count = secondary ? 2 : 1;
            var widths = new float[count];
            var controlsWidth = secondary ? separatorWidth + _gap * 2f : 0f;
            for (var i = 0; i < count; i++)
            {
                var text = BindingText(i);
                Prepare(text, false);
                widths[i] = Mathf.Max(_boxWidth, Mathf.Ceil(text.GetPreferredValues(text.text).x + 12f));
                controlsWidth += widths[i];
            }

            var innerWidth = Mathf.Max(1f, width - _leftInset - _rightInset);
            var captionWidth = innerWidth - controlsWidth - _gap * 2f;
            // Reserve a readable caption column. Long chords get the full row below
            // it instead of spilling into the separator or shrinking the font.
            var nativeControlsWidth = _boxWidth * count + (secondary ? separatorWidth + _gap * 2f : 0f);
            var stacked = captionWidth < innerWidth * 0.3f ||
                          controlsWidth > nativeControlsWidth + PreferredSizeTolerance &&
                          title.GetPreferredValues(title.text).x > captionWidth;
            if (stacked)
            {
                captionWidth = innerWidth;
            }

            if (controlsWidth > innerWidth)
            {
                var available = Mathf.Max(1f, innerWidth - (secondary ? separatorWidth + _gap * 2f : 0f));
                var total = controlsWidth - (secondary ? separatorWidth + _gap * 2f : 0f);
                for (var i = 0; i < count; i++)
                {
                    widths[i] *= available / total;
                }

                controlsWidth = innerWidth;
            }

            var controlsHeight = _boxHeight;
            for (var i = 0; i < count; i++)
            {
                var text = BindingText(i);
                Prepare(text, true);
                var preferred = GetPreferredSize(text, text.fontSize, true, int.MaxValue, text.lineSpacing,
                    Mathf.Max(1f, widths[i] - 12f));
                controlsHeight = Mathf.Max(controlsHeight, Mathf.Ceil(preferred.y + 4f));
            }

            controlsHeight = Mathf.Max(controlsHeight, _separator.GetPreferredValues(_separator.text).y);
            var captionSize = GetPreferredSize(title, title.fontSize, true, int.MaxValue, title.lineSpacing,
                captionWidth);
            var captionHeight = Mathf.Max(_captionHeight, Mathf.Ceil(captionSize.y));
            var contentHeight = stacked ? captionHeight + _gap + controlsHeight :
                Mathf.Max(captionHeight, controlsHeight);
            var height = Mathf.Max(_rowHeight, contentHeight + _verticalInset * 2f);
            _element.minHeight = Mathf.Max(_minimumHeight, height);
            _element.preferredHeight = Mathf.Max(_preferredHeight, height);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            Place(title.rectTransform, _leftInset,
                stacked ? _verticalInset + controlsHeight + _gap : (height - captionHeight) / 2f,
                captionWidth, captionHeight);
            Place(_table, stacked ? _leftInset : width - _rightInset - controlsWidth,
                stacked ? _verticalInset : (height - controlsHeight) / 2f, controlsWidth, controlsHeight);
            Place(_row.bindingBoxes[0], 0f, 0f, widths[0], controlsHeight);
            if (secondary)
            {
                Place(_row.orSeparator, widths[0] + _gap, 0f, separatorWidth, controlsHeight);
                Place(_row.bindingBoxes[1], widths[0] + separatorWidth + _gap * 2f, 0f,
                    widths[1], controlsHeight);
            }

            _lastWidth = width;
            _lastSecondary = secondary;
            for (var i = 0; i < _texts.Count; i++)
            {
                var text = _texts[i].Text;
                _texts[i] = (text, text.text, text.font, text.isActiveAndEnabled);
            }

            _dirty = false;
            if (rect.parent is RectTransform settingsTable)
            {
                LayoutRebuilder.MarkLayoutForRebuild(settingsTable);
            }
        }

        private TMP_Text BindingText(int index)
        {
            return _row.bindingLabels[index].gameObject.activeSelf
                ? _row.bindingLabels[index].TMP_Text
                : _row.unboundLabels[index].TMP_Text;
        }

        private void AddText(TMP_Text text)
        {
            Prepare(text, true);
            _texts.Add((text, null, null, false));
        }

        private bool HasChanged(float width, bool secondary)
        {
            if (Mathf.Abs(width - _lastWidth) >= PreferredSizeTolerance || secondary != _lastSecondary)
            {
                return true;
            }

            foreach (var (text, value, font, active) in _texts)
            {
                if (text.text != value || text.font != font || text.isActiveAndEnabled != active)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Prepare(TMP_Text text, bool wrap)
        {
            var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();
            state.Capture(text);
            text.enableAutoSizing = false;
            text.fontSize = state.OriginalFontSizeMax;
            text.enableWordWrapping = wrap;
            text.maxVisibleCharacters = int.MaxValue;
            text.maxVisibleLines = int.MaxValue;
            text.overflowMode = TextOverflowModes.Overflow;
            text.lineSpacing = state.OriginalLineSpacing;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }

    private sealed class SettingCaptionLayoutState : MonoBehaviour
    {
        private GuiBehaviour _row;
        private TMP_Text _text;
        private TextFitState _textState;
        private LayoutElement _element;
        private float _rowHeight;
        private float _captionHeight;
        private float _minimumHeight;
        private float _preferredHeight;
        private RectTransform _radioTable;
        private float _radioTableHeight;
        private float _extraHeight;
        private float _lastWidth = -1f;
        private string _lastText;
        private TMP_FontAsset _lastFont;
        private bool _dirty;
        private int _deferredFrames;

        internal void Schedule(GuiBehaviour row, TMP_Text text)
        {
            _row = row;
            _text = text;
            _dirty = true;
            // Native derived Bind methods and the parent layout finish after ApplyText.
            _deferredFrames = DeferredSingleLineFitFrames;
        }

        private void Invalidate()
        {
            _dirty = true;
        }

        private void LateUpdate()
        {
            if (!_row || !_text || !_row.gameObject.activeInHierarchy ||
                (!_text.isActiveAndEnabled && _row is not SettingRadioListItem) || IsCanvasRebuildInProgress())
            {
                return;
            }

            if (_deferredFrames > 0)
            {
                _deferredFrames--;
                return;
            }

            var caption = _text.rectTransform;
            var width = caption.rect.width;
            if (width <= 0f || string.IsNullOrEmpty(_text.text) ||
                !_dirty && Mathf.Abs(width - _lastWidth) < PreferredSizeTolerance &&
                _lastText == _text.text && _lastFont == _text.font)
            {
                return;
            }

            if (!_textState)
            {
                _textState = _text.GetComponent<TextFitState>() ?? _text.gameObject.AddComponent<TextFitState>();
                _textState.Capture(_text);
                _rowHeight = _row.RectTransform.rect.height;
                _captionHeight = caption.rect.height;
                _element = _row.GetComponent<LayoutElement>() ?? _row.gameObject.AddComponent<LayoutElement>();
                _minimumHeight = _element.minHeight;
                _preferredHeight = _element.preferredHeight;
                if (_row is SettingRadioListItem owner)
                {
                    _radioTable = owner.togglesTable;
                    _radioTableHeight = _radioTable ? _radioTable.rect.height : 0f;
                }
            }

            _text.enableAutoSizing = false;
            _text.fontSize = _textState.OriginalFontSizeMax;
            _text.enableWordWrapping = true;
            _text.maxVisibleLines = int.MaxValue;
            _text.maxVisibleCharacters = int.MaxValue;
            _text.overflowMode = TextOverflowModes.Overflow;
            _text.lineSpacing = _textState.OriginalLineSpacing;

            var preferred = _text.isActiveAndEnabled
                ? GetPreferredSize(_text, _text.fontSize, true, int.MaxValue, _text.lineSpacing, width)
                : Vector2.zero;
            var captionHeight = Mathf.Max(_captionHeight,
                Mathf.Ceil(preferred.y + _text.margin.y + _text.margin.w));
            var rowHeight = _rowHeight;
            var radioTableHeight = _radioTableHeight;
            var extraHeight = captionHeight - _captionHeight;
            var choicesExtraHeight = 0f;
            if (_row is SettingRadioListItem radio && radio.settingTypeRadioListAttribute != null)
            {
                rowHeight = radio.settingTypeRadioListAttribute.DisplayHeader
                    ? radio.expandedHeight
                    : radio.regularHeight;

                if (_radioTable)
                {
                    // A pooled radio can change its native header mode on Bind.
                    // Stretched tables follow that base height; fixed tables do not.
                    radioTableHeight += (rowHeight - _rowHeight) *
                                        (_radioTable.anchorMax.y - _radioTable.anchorMin.y);
                }

                if (_radioTable)
                {
                    foreach (Transform child in _radioTable)
                    {
                        if (child.gameObject.activeSelf &&
                            child.GetComponent<SettingCaptionLayoutState>() is { } choiceLayout &&
                            choiceLayout._text && choiceLayout._text.isActiveAndEnabled)
                        {
                            choicesExtraHeight = Mathf.Max(choicesExtraHeight, choiceLayout._extraHeight);
                        }
                    }
                }

                // Captions and radio choices occupy parallel columns. Preserve their
                // native insets and reserve the height required by the taller column.
                extraHeight = Mathf.Max(extraHeight, choicesExtraHeight);
            }

            rowHeight += extraHeight;
            _element.minHeight = Mathf.Max(_minimumHeight, rowHeight);
            _element.preferredHeight = Mathf.Max(_preferredHeight, rowHeight);
            _row.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, rowHeight);
            if (_radioTable)
            {
                // Resize the parent first: a vertically stretched table must not
                // receive the same extra height twice when the row subsequently grows.
                _radioTable.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                    radioTableHeight + choicesExtraHeight);
                LayoutRebuilder.MarkLayoutForRebuild(_radioTable);
            }

            // Keep the native caption width: controls already occupy the remaining column.
            caption.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, captionHeight);
            LayoutRebuilder.MarkLayoutForRebuild(_row.RectTransform);
            if (_row.RectTransform.parent is RectTransform table)
            {
                LayoutRebuilder.MarkLayoutForRebuild(table);
            }

            _lastWidth = width;
            _lastText = _text.text;
            _lastFont = _text.font;
            _extraHeight = extraHeight;
            _dirty = false;

            if (_row is SettingRadioChoice &&
                _row.GetComponentInParent<SettingRadioListItem>() is { } radioOwner)
            {
                radioOwner.GetComponent<SettingCaptionLayoutState>()?.Invalidate();
            }
        }
    }

    private sealed class SettingsTabsLayoutState : MonoBehaviour
    {
        private readonly List<(SettingsTabToggle Tab, float Width)> _tabs = [];
        private SettingsPanel _panel;
        private ScrollRect _scroll;
        private HorizontalLayoutGroup _layout;
        private float _originalSpacing;
        private float _lastWidth;
        private int _lastPadding;
        private string _lastLanguage;
        private bool _lastGamepad;
        private bool _dirty;

        internal void Schedule(SettingsPanel panel)
        {
            if (!_panel)
            {
                _panel = panel;
                _scroll = panel.tabTogglesContainer.GetComponentInParent<ScrollRect>();
                _layout = panel.tabTogglesContainer.GetComponent<HorizontalLayoutGroup>();
                _originalSpacing = _layout ? _layout.spacing : 0f;
            }

            _dirty = true;
        }

        private void LateUpdate()
        {
            if (!_panel || !_panel.Visible || !_scroll || !_layout)
            {
                return;
            }

            // The native viewport follows the content fitter. The scroll view itself
            // retains the panel's available width, including after a language change.
            var viewport = (RectTransform)_scroll.transform;
            var wrapper = _layout.transform.parent.GetComponent<HorizontalLayoutGroup>();
            var padding = _layout.padding.horizontal + (wrapper ? wrapper.padding.horizontal : 0);
            var width = viewport.rect.width - padding;
            var language = LocalizationManager.CurrentLanguageCode;

            if (width <= 0f || (!_dirty && Mathf.Abs(width - _lastWidth) < PreferredSizeTolerance &&
                                padding == _lastPadding && _lastLanguage == language &&
                                _lastGamepad == Gui.GamepadActive))
            {
                return;
            }

            _dirty = false;
            _lastWidth = width;
            _lastPadding = padding;
            _lastLanguage = language;
            _lastGamepad = Gui.GamepadActive;
            _tabs.Clear();
            var totalWidth = 0f;

            foreach (var tab in _panel.tabToggles.Values)
            {
                if (!tab || !tab.gameObject.activeSelf || !tab.title?.TMP_Text)
                {
                    continue;
                }

                var text = tab.title.TMP_Text;
                var state = text.GetComponent<TextFitState>() ?? text.gameObject.AddComponent<TextFitState>();
                state.Capture(text);
                text.enableAutoSizing = false;
                text.fontSize = state.OriginalFontSizeMax;
                var preferredWidth = Mathf.Max(1f, text.GetPreferredValues(text.text).x);
                _tabs.Add((tab, preferredWidth));
                totalWidth += preferredWidth;
            }

            if (_tabs.Count == 0)
            {
                return;
            }

            // The native content fitter sizes the strip from all localized titles, but
            // leaves its fixed gaps unchanged when that strip exceeds the scroll viewport.
            var gaps = _tabs.Count - 1;
            _layout.spacing = gaps > 0
                ? Mathf.Clamp((width - totalWidth) / gaps, Mathf.Max(0f, _originalSpacing * 0.25f),
                    Mathf.Max(0f, _originalSpacing))
                : _originalSpacing;
            var scale = Mathf.Min(1f, Mathf.Max(0f, width - gaps * _layout.spacing) / totalWidth);

            foreach (var (tab, preferredWidth) in _tabs)
            {
                var tabWidth = preferredWidth * scale;
                tab.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, tabWidth);
                tab.selectionBar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, tabWidth);
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel.tabTogglesContainer);

            foreach (var (tab, _) in _tabs)
            {
                ApplyConstrainedSingleLineFit(tab.title.TMP_Text, TagMinFontScale, TagAbsoluteMinFontSize);
            }

            if (_scroll.content)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_scroll.content);
            }

            _scroll.horizontalNormalizedPosition = 0f;
        }
    }

    private sealed class TextFitState : MonoBehaviour
    {
        internal float OriginalFontSizeMax { get; private set; }
        internal float OriginalLineSpacing { get; private set; }
        internal float OriginalRectHeight { get; private set; }
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
        private float LastRenderedFontSize { get; set; }
        private int LastRenderedLines { get; set; }
        private bool LastRenderedWrapping { get; set; }
        private TextOverflowModes LastRenderedOverflow { get; set; }
        private float LastRenderedSpacing { get; set; }
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
            OriginalRectHeight = text.rectTransform ? text.rectTransform.rect.height : 0f;
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
                   !text.enableAutoSizing &&
                   Mathf.Abs(text.fontSize - LastRenderedFontSize) <= 0.01f &&
                   text.maxVisibleLines == LastRenderedLines &&
                   text.enableWordWrapping == LastRenderedWrapping &&
                   text.overflowMode == LastRenderedOverflow &&
                   Mathf.Abs(text.lineSpacing - LastRenderedSpacing) <= 0.01f &&
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
            LastRenderedFontSize = text.fontSize;
            LastRenderedLines = text.maxVisibleLines;
            LastRenderedWrapping = text.enableWordWrapping;
            LastRenderedOverflow = text.overflowMode;
            LastRenderedSpacing = text.lineSpacing;
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

    private sealed class DeferredSingleLineFit : MonoBehaviour
    {
        private float AbsoluteMin { get; set; }

        private bool ConstrainHeight { get; set; }

        private Coroutine Coroutine { get; set; }

        private TMP_Text Text { get; set; }

        private float MinFontScale { get; set; }

        internal void Schedule(TMP_Text text, float minFontScale, float absoluteMin, bool constrainHeight)
        {
            Text = text;
            MinFontScale = minFontScale;
            AbsoluteMin = absoluteMin;
            ConstrainHeight = constrainHeight;
            OnEnable();
        }

        private IEnumerator ApplyLater()
        {
            for (var i = 0; i < DeferredSingleLineFitFrames; i++)
            {
                yield return null;

                if (!Text || !Text.enabled)
                {
                    continue;
                }

                if (ConstrainHeight)
                {
                    ApplySingleLineFit(Text, MinFontScale, AbsoluteMin);
                }
                else
                {
                    ApplyConstrainedSingleLineFit(Text, MinFontScale, AbsoluteMin);
                }
            }

            Coroutine = null;
        }

        private void OnEnable()
        {
            if (Text && gameObject.activeInHierarchy && Coroutine == null)
            {
                Coroutine = StartCoroutine(ApplyLater());
            }
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
