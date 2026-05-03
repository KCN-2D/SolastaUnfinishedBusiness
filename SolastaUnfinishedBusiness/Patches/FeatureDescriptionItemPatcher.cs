using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FeatureDescriptionItemPatcher
{
    private const string BackgroundFeatDisplayFeaturePrefix = "FeatureBackgroundFeatDisplay_";
    private const float SelectionFeatureItemPadding = 4f;
    private const float SelectionFeatureControlSpacing = 4f;

    private static readonly AccessTools.FieldRef<FeatureDescriptionItem, FeatureDefinition> FeatureRef =
        AccessTools.FieldRefAccess<FeatureDescriptionItem, FeatureDefinition>("<Feature>k__BackingField");

    private static readonly AccessTools.FieldRef<FeatureDescriptionItem, List<FeatureDefinition>> AvailableFeaturesRef =
        AccessTools.FieldRefAccess<FeatureDescriptionItem, List<FeatureDefinition>>("availableFeatures");

    private static readonly AccessTools.FieldRef<FeatureDefinitionFeatureSet, int> DefaultSelectionRef =
        AccessTools.FieldRefAccess<FeatureDefinitionFeatureSet, int>("defaultSelection");

    private static RulesetCharacterHero CurrentHero => ServiceRepository
        .GetService<ICharacterBuildingService>()
        ?.CurrentLocalHeroCharacter;

    private static bool TryGetSelectedFeatureChoice(
        FeatureDescriptionItem item,
        out FeatureDefinition choiceFeature)
    {
        choiceFeature = null;

        if (item == null)
        {
            return false;
        }

        var availableFeatures = AvailableFeaturesRef(item);

        if (availableFeatures is not { Count: > 0 })
        {
            return false;
        }

        var index = -1;

        if (item.choiceDropdown && item.choiceDropdown.gameObject.activeSelf)
        {
            index = item.choiceDropdown.value;
        }
        else if (item.choiceSelector && item.choiceSelector.gameObject.activeSelf)
        {
            index = item.choiceSelector.CurrentSelection;
        }

        if (index < 0 || index >= availableFeatures.Count)
        {
            index = availableFeatures.IndexOf(item.SelectedFeature);
        }

        if (index < 0 || index >= availableFeatures.Count)
        {
            return false;
        }

        choiceFeature = availableFeatures[index];

        return true;
    }

    internal static bool TryGetSelectedHumanOriginFeatChoice(
        FeatureDescriptionItem item,
        out FeatureDefinition choiceFeature)
    {
        choiceFeature = null;

        return item != null &&
               Tabletop2024Context.IsHumanOriginFeatSelectionFeature(FeatureRef(item)) &&
               TryGetSelectedFeatureChoice(item, out choiceFeature);
    }

    internal static bool TryPersistBackgroundAsiDefaultSelection(FeatureDescriptionItem item)
    {
        if (item == null ||
            FeatureRef(item) is not FeatureDefinitionFeatureSet featureSet ||
            !Tabletop2024Context.IsBackgroundAsiSelectionFeature(featureSet) ||
            !TryGetSelectedFeatureChoice(item, out var choiceFeature))
        {
            return false;
        }

        var index = featureSet.FeatureSet.IndexOf(choiceFeature);

        if (index < 0)
        {
            return false;
        }

        DefaultSelectionRef(featureSet) = index;

        return true;
    }

    private static void SaveHumanOriginFeatSelection(FeatureDescriptionItem item, bool clearTraining)
    {
        if (TryGetSelectedHumanOriginFeatChoice(item, out var choiceFeature))
        {
            Tabletop2024Context.TrySaveHumanOriginFeatSelection(CurrentHero, choiceFeature, clearTraining);
        }
    }

    private static void RestoreHumanOriginFeatSelection(FeatureDescriptionItem item)
    {
        if (!Tabletop2024Context.IsHumanOriginFeatSelectionFeature(FeatureRef(item)))
        {
            return;
        }

        var hero = CurrentHero;
        var availableFeatures = AvailableFeaturesRef(item);

        if (availableFeatures is not { Count: > 0 })
        {
            return;
        }

        if (!Tabletop2024Context.TryGetHumanOriginSelectionFeature(hero, out var selectionFeature))
        {
            return;
        }

        var index = availableFeatures.IndexOf(selectionFeature);

        if (index < 0)
        {
            return;
        }

        if (item.choiceDropdown && item.choiceDropdown.gameObject.activeSelf)
        {
            item.choiceDropdown.SetValueWithoutNotify(index);
            item.choiceDropdown.RefreshShownValue();
        }

        if (item.choiceSelector && item.choiceSelector.gameObject.activeSelf)
        {
            item.choiceSelector.CurrentSelection = index;
            item.choiceSelector.RefreshCurrent();
            item.choiceSelector.RefreshTooltip();
        }
    }

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

    internal static bool RefreshSelectionFeatureDisplayLayout(FeatureDescriptionItem item)
    {
        if (!item || !IsSelectionFeatureItem(item))
        {
            return false;
        }

        var itemRect = item.GetComponent<RectTransform>();

        if (!itemRect)
        {
            return false;
        }

        var state = item.GetComponent<SelectionFeatureItemLayoutState>() ??
                    item.gameObject.AddComponent<SelectionFeatureItemLayoutState>();

        state.Restore(item);

        var cursor = 0f;
        var changed = false;

        changed |= PlaceSelectionFeatureText(item.baseFeatureLabel, ref cursor);
        changed |= PlaceSelectionFeatureText(item.featureDescription, ref cursor);
        changed |= PlaceSelectionFeatureControl(item.choiceDropdown ? item.choiceDropdown.transform as RectTransform : null,
            ref cursor);
        changed |= PlaceSelectionFeatureControl(item.choiceSelector ? item.choiceSelector.transform as RectTransform : null,
            ref cursor);
        changed |= PlaceSelectionFeatureControl(item.levelGroup, ref cursor);

        var requiredHeight = Mathf.Ceil(Mathf.Max(1f, cursor + SelectionFeatureItemPadding));

        if (Mathf.Abs(requiredHeight - itemRect.rect.height) > 0.5f)
        {
            itemRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);
            changed = true;
        }

        var layout = item.GetComponent<LayoutElement>() ?? item.gameObject.AddComponent<LayoutElement>();

        if (Mathf.Abs(layout.preferredHeight - requiredHeight) > 0.5f ||
            Mathf.Abs(layout.minHeight - requiredHeight) > 0.5f)
        {
            layout.preferredHeight = requiredHeight;
            layout.minHeight = requiredHeight;
            changed = true;
        }

        if (changed)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(itemRect);
        }

        return changed;
    }

    internal static bool IsSelectionFeatureItem(FeatureDescriptionItem item)
    {
        return item &&
               (item.GetComponentInParent<CharacterStageRaceSelectionPanel>() ||
                item.GetComponentInParent<CharacterStageClassSelectionPanel>() ||
                item.GetComponentInParent<CharacterStageSubclassSelectionPanel>());
    }

    private static bool PlaceSelectionFeatureText(GuiLabel label, ref float cursor)
    {
        if (!label || !label.gameObject.activeInHierarchy || label.TMP_Text == null)
        {
            return false;
        }

        var text = label.TMP_Text;
        var rectTransform = label.RectTransform;

        if (!rectTransform)
        {
            return false;
        }

        var changed = false;
        var position = rectTransform.anchoredPosition;

        if (Mathf.Abs(position.y + cursor) > 0.5f)
        {
            rectTransform.anchoredPosition = new Vector2(position.x, -cursor);
            changed = true;
        }

        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.maxVisibleLines = 99999;

        var width = rectTransform.rect.width;
        var preferredHeight = width > 0f
            ? text.GetPreferredValues(text.text, width, 0f).y
            : rectTransform.rect.height;
        var height = Mathf.Ceil(Mathf.Max(text.fontSize * 1.45f, preferredHeight));

        if (Mathf.Abs(height - rectTransform.rect.height) > 0.5f)
        {
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            changed = true;
        }

        cursor += height;

        return changed;
    }

    private static bool PlaceSelectionFeatureControl(RectTransform rectTransform, ref float cursor)
    {
        if (!rectTransform || !rectTransform.gameObject.activeInHierarchy)
        {
            return false;
        }

        cursor += SelectionFeatureControlSpacing;

        var position = rectTransform.anchoredPosition;
        var changed = false;

        if (Mathf.Abs(position.y + cursor) > 0.5f)
        {
            rectTransform.anchoredPosition = new Vector2(position.x, -cursor);
            changed = true;
        }

        cursor += rectTransform.rect.height;

        return changed;
    }

    private sealed class SelectionFeatureItemLayoutState : MonoBehaviour
    {
        private readonly RectTransformState titleState = new();
        private readonly RectTransformState descriptionState = new();
        private readonly RectTransformState dropdownState = new();
        private readonly RectTransformState selectorState = new();
        private readonly RectTransformState levelState = new();

        private bool saved;
        private Vector2 itemSizeDelta;
        private bool hadLayoutElement;
        private float minHeight;
        private float preferredHeight;

        internal void Restore(FeatureDescriptionItem item)
        {
            Save(item);

            if (item.TryGetComponent<RectTransform>(out var itemRect))
            {
                itemRect.sizeDelta = itemSizeDelta;
            }

            RestoreLayoutElement(item);
            titleState.Restore(item.baseFeatureLabel ? item.baseFeatureLabel.RectTransform : null);
            descriptionState.Restore(item.featureDescription ? item.featureDescription.RectTransform : null);
            dropdownState.Restore(item.choiceDropdown ? item.choiceDropdown.transform as RectTransform : null);
            selectorState.Restore(item.choiceSelector ? item.choiceSelector.transform as RectTransform : null);
            levelState.Restore(item.levelGroup);
        }

        private void Save(FeatureDescriptionItem item)
        {
            if (saved)
            {
                return;
            }

            saved = true;

            if (item.TryGetComponent<RectTransform>(out var itemRect))
            {
                itemSizeDelta = itemRect.sizeDelta;
            }

            if (item.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                hadLayoutElement = true;
                minHeight = layoutElement.minHeight;
                preferredHeight = layoutElement.preferredHeight;
            }

            titleState.Save(item.baseFeatureLabel ? item.baseFeatureLabel.RectTransform : null);
            descriptionState.Save(item.featureDescription ? item.featureDescription.RectTransform : null);
            dropdownState.Save(item.choiceDropdown ? item.choiceDropdown.transform as RectTransform : null);
            selectorState.Save(item.choiceSelector ? item.choiceSelector.transform as RectTransform : null);
            levelState.Save(item.levelGroup);
        }

        private void RestoreLayoutElement(FeatureDescriptionItem item)
        {
            if (!item.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                return;
            }

            if (!hadLayoutElement)
            {
                layoutElement.minHeight = -1f;
                layoutElement.preferredHeight = -1f;

                return;
            }

            layoutElement.minHeight = minHeight;
            layoutElement.preferredHeight = preferredHeight;
        }
    }

    private sealed class RectTransformState
    {
        private bool saved;
        private Vector2 anchoredPosition;
        private Vector2 sizeDelta;
        private bool hasText;
        private bool wordWrapping;
        private TextOverflowModes overflowMode;
        private int maxVisibleLines;
        private bool autoSizeTextContainer;

        internal void Save(RectTransform rectTransform)
        {
            if (!rectTransform)
            {
                return;
            }

            saved = true;
            anchoredPosition = rectTransform.anchoredPosition;
            sizeDelta = rectTransform.sizeDelta;

            if (!rectTransform.TryGetComponent<TMP_Text>(out var text))
            {
                return;
            }

            hasText = true;
            wordWrapping = text.enableWordWrapping;
            overflowMode = text.overflowMode;
            maxVisibleLines = text.maxVisibleLines;
            autoSizeTextContainer = text.autoSizeTextContainer;
        }

        internal void Restore(RectTransform rectTransform)
        {
            if (!saved || !rectTransform)
            {
                return;
            }

            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = sizeDelta;

            if (!hasText || !rectTransform.TryGetComponent<TMP_Text>(out var text))
            {
                return;
            }

            text.enableWordWrapping = wordWrapping;
            text.overflowMode = overflowMode;
            text.maxVisibleLines = maxVisibleLines;
            text.autoSizeTextContainer = autoSizeTextContainer;
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

            RestoreHumanOriginFeatSelection(__instance);
            SaveHumanOriginFeatSelection(__instance, false);
            RefreshSelectionFeatureDisplayLayout(__instance);

            if (TryGetBackgroundFeatDisplayPanel(__instance, out var backgroundPanel))
            {
                RefreshBackgroundFeatDisplayLayout(__instance, backgroundPanel);
            }
        }
    }
}
