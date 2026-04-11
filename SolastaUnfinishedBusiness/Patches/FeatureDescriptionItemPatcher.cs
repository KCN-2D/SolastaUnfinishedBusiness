using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FeatureDescriptionItemPatcher
{
    private const string BackgroundFeatDisplayFeaturePrefix = "FeatureBackgroundFeatDisplay_";

    private static readonly AccessTools.FieldRef<FeatureDescriptionItem, FeatureDefinition> FeatureRef =
        AccessTools.FieldRefAccess<FeatureDescriptionItem, FeatureDefinition>("<Feature>k__BackingField");

    private static bool TryGetBackgroundFeatDisplayPanel(
        FeatureDescriptionItem item,
        out CharacterStageBackgroundSelectionPanel backgroundPanel)
    {
        backgroundPanel = item.GetComponentInParent<CharacterStageBackgroundSelectionPanel>();

        return backgroundPanel &&
               FeatureRef(item) is { Name: var featureName } &&
               featureName.StartsWith(BackgroundFeatDisplayFeaturePrefix, System.StringComparison.Ordinal);
    }

    private static float GetRequiredHeight([NotNull] RectTransform rectTransform, float padding)
    {
        return rectTransform.rect.height - rectTransform.anchoredPosition.y + padding;
    }

    private static void RefreshBackgroundFeatDisplayLayout(
        FeatureDescriptionItem item,
        CharacterStageBackgroundSelectionPanel backgroundPanel)
    {
        var titleLabel = item.baseFeatureLabel;
        var titleText = titleLabel?.TMP_Text;
        var descriptionLabel = item.featureDescription;
        var descriptionText = descriptionLabel?.TMP_Text;

        if (titleText == null)
        {
            return;
        }

        titleText.enableWordWrapping = true;
        titleText.overflowMode = TextOverflowModes.Overflow;
        titleText.autoSizeTextContainer = true;

        if (descriptionLabel)
        {
            descriptionLabel.gameObject.SetActive(true);
        }

        if (descriptionText != null)
        {
            descriptionText.enableWordWrapping = true;
            descriptionText.overflowMode = TextOverflowModes.Overflow;
            descriptionText.autoSizeTextContainer = true;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(titleLabel.RectTransform);
        LayoutRebuilder.ForceRebuildLayoutImmediate(descriptionLabel?.RectTransform);

        var requiredHeight = GetRequiredHeight(titleLabel.RectTransform, 12f);

        if (descriptionLabel && descriptionLabel.gameObject.activeSelf)
        {
            requiredHeight = Mathf.Max(requiredHeight, GetRequiredHeight(descriptionLabel.RectTransform, 12f));
        }

        if (item.levelGroup && item.levelGroup.gameObject.activeSelf)
        {
            requiredHeight = Mathf.Max(requiredHeight, GetRequiredHeight(item.levelGroup, 8f));
        }

        var itemRect = item.GetComponent<RectTransform>();

        if (itemRect)
        {
            itemRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);
            LayoutRebuilder.ForceRebuildLayoutImmediate(itemRect);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(backgroundPanel.backgroundFeaturesList);
        LayoutRebuilder.ForceRebuildLayoutImmediate(backgroundPanel.backgroundFeaturesGroup);

        if (backgroundPanel.backgroundFeaturesScrollview?.content)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(backgroundPanel.backgroundFeaturesScrollview.content);
        }
    }

    //PATCH: Disables choices dropdown for features already taken on previous levels (MULTICLASS)
    [HarmonyPatch(typeof(FeatureDescriptionItem), nameof(FeatureDescriptionItem.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] FeatureDescriptionItem __instance)
        {
            var hero = Global.LevelUpHero;

            if (hero != null && LevelUpHelper.IsClassSelectionStage(hero))
            {
                __instance.choiceDropdown.gameObject.SetActive(false);
            }

            if (!TryGetBackgroundFeatDisplayPanel(__instance, out var backgroundPanel))
            {
                return;
            }

            RefreshBackgroundFeatDisplayLayout(__instance, backgroundPanel);
        }
    }
}
