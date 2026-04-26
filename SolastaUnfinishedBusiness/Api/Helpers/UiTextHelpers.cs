using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Api.Helpers;

internal static class UiTextHelpers
{
    private const float TitleMinFontScale = 0.72f;
    private const float TitleAbsoluteMinFontSize = 8f;
    private const float TagMinFontScale = 0.65f;
    private const float TagAbsoluteMinFontSize = 7f;
    private const float StatTitleMinFontScale = 0.62f;
    private const float StatTitleAbsoluteMinFontSize = 7f;
    private const float StatValueMinFontScale = 0.72f;
    private const float StatValueAbsoluteMinFontSize = 8f;

    internal static void FitSingleLine(GuiLabel label, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!label)
        {
            return;
        }

        FitSingleLine(label.TMP_Text, minFontScale, absoluteMin);
    }

    internal static void FitSingleLine(TMP_Text text, float minFontScale = TitleMinFontScale,
        float absoluteMin = TitleAbsoluteMinFontSize)
    {
        if (!text)
        {
            return;
        }

        var maxFontSize = text.enableAutoSizing && text.fontSizeMax > 0f
            ? text.fontSizeMax
            : text.fontSize;

        if (maxFontSize <= 0f)
        {
            return;
        }

        text.enableAutoSizing = true;
        text.enableWordWrapping = false;
        text.maxVisibleLines = 1;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.autoSizeTextContainer = false;
        text.fontSizeMax = maxFontSize;
        text.fontSizeMin = Mathf.Min(maxFontSize, Mathf.Max(absoluteMin, maxFontSize * minFontScale));
    }

    internal static void KeepSpellBoxTextInside(SpellBox spellBox)
    {
        if (!spellBox)
        {
            return;
        }

        if (IsCanvasRebuildInProgress())
        {
            return;
        }

        FitSingleLine(spellBox.titleLabel);
        FitSingleLine(spellBox.autoPreparedTitle, TagMinFontScale, TagAbsoluteMinFontSize);
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
}
