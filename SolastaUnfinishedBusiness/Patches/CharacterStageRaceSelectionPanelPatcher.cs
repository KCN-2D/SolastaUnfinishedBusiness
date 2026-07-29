using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageRaceSelectionPanelPatcher
{
    private const float RaceContentPadding = 8f;
    private const float RaceTitleFallbackWidth = 290f;

    private static bool TrySaveHumanOriginFeatSelection(
        CharacterStageRaceSelectionPanel panel,
        FeatureDescriptionItem featureDescriptionItem)
    {
        if (panel?.currentHero == null ||
            featureDescriptionItem == null ||
            !FeatureDescriptionItemPatcher.TryGetSelectedHumanOriginFeatChoice(featureDescriptionItem, out var choiceFeature))
        {
            return false;
        }

        return Tabletop2024Context.TrySaveHumanOriginFeatSelection(
            panel.currentHero,
            choiceFeature,
            true);
    }

    private static void RefreshRaceContentLayout(CharacterStageRaceSelectionPanel panel)
    {
        if (!panel || !panel.selectedRaceTitle)
        {
            return;
        }

        var table = panel.selectedRaceTitle.transform.parent as RectTransform;

        if (!table)
        {
            return;
        }

        var featureItems = panel.GetComponentsInChildren<FeatureDescriptionItem>(false);
        var changed = RefreshSelectionFeatureItems(featureItems);

        changed |= PlaceDirectRaceContentChildren(
            table,
            GetRaceContentWidth(panel, featureItems),
            panel.selectedRaceTitle.RectTransform,
            panel.selectedSubraceTitle ? panel.selectedSubraceTitle.RectTransform : null);

        if (!changed)
        {
            return;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(table);

        if (table.parent is RectTransform parent)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
        }
    }

    private static bool RefreshSelectionFeatureItems(IEnumerable<FeatureDescriptionItem> featureItems)
    {
        var changed = false;

        foreach (var item in featureItems)
        {
            changed |= FeatureDescriptionItemPatcher.RefreshSpeciesBaseWalkSpeedDescription(item);
            changed |= FeatureDescriptionItemPatcher.RefreshSelectionFeatureDisplayLayout(item);
        }

        return changed;
    }

    private static float GetRaceContentWidth(
        CharacterStageRaceSelectionPanel panel,
        IEnumerable<FeatureDescriptionItem> featureItems)
    {
        foreach (var item in featureItems)
        {
            var itemRect = item.GetComponent<RectTransform>();

            if (itemRect && itemRect.rect.width > 1f)
            {
                return itemRect.rect.width;
            }
        }

        var titleRect = panel.selectedRaceTitle?.RectTransform;

        if (!titleRect)
        {
            return RaceTitleFallbackWidth;
        }

        return Mathf.Min(titleRect.rect.width, RaceTitleFallbackWidth);
    }

    private static bool RefreshRaceTitle(GuiLabel label, float width)
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

        var state = label.GetComponent<RaceTitleLayoutState>() ??
                    label.gameObject.AddComponent<RaceTitleLayoutState>();

        state.Restore(label);

        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.maxVisibleLines = 99999;

        if (width <= 1f)
        {
            return false;
        }

        var changed = false;

        if (Mathf.Abs(width - rectTransform.rect.width) > 0.5f)
        {
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            changed = true;
        }

        var requiredHeight = Mathf.Ceil(Mathf.Max(
            text.fontSize * 1.45f,
            text.GetPreferredValues(text.text, width, 0f).y));

        changed |= SetPreferredHeight(rectTransform, requiredHeight);

        return changed;
    }

    private static bool PlaceDirectRaceContentChildren(
        RectTransform table,
        float titleWidth,
        RectTransform raceTitleRect,
        RectTransform subraceTitleRect)
    {
        var changed = false;
        var cursor = 0f;

        for (var i = 0; i < table.childCount; i++)
        {
            if (table.GetChild(i) is not RectTransform child || !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(child);

            var requiredHeight = Mathf.Ceil(GetDirectRaceContentChildHeight(child, raceTitleRect, subraceTitleRect,
                titleWidth));
            var position = child.anchoredPosition;

            if (Mathf.Abs(position.y + cursor) > 0.5f)
            {
                child.anchoredPosition = new Vector2(position.x, -cursor);
                changed = true;
            }

            changed |= SetPreferredHeight(child, requiredHeight);

            cursor += requiredHeight + RaceContentPadding;
        }

        var tableHeight = Mathf.Ceil(Mathf.Max(1f, cursor));

        changed |= SetPreferredHeight(table, tableHeight);

        return changed;
    }

    private static float GetDirectRaceContentChildHeight(
        RectTransform child,
        RectTransform raceTitle,
        RectTransform subraceTitle,
        float titleWidth)
    {
        if (child == raceTitle)
        {
            RefreshRaceTitle(raceTitle.GetComponent<GuiLabel>(), titleWidth);

            return raceTitle.rect.height;
        }

        if (child == subraceTitle)
        {
            RefreshRaceTitle(subraceTitle.GetComponent<GuiLabel>(), titleWidth);

            return subraceTitle.rect.height;
        }

        return Mathf.Max(1f, GetActiveChildrenHeight(child, RaceContentPadding));
    }

    private static bool SetPreferredHeight(RectTransform rectTransform, float height)
    {
        if (!rectTransform)
        {
            return false;
        }

        var changed = false;

        if (Mathf.Abs(height - rectTransform.rect.height) > 0.5f)
        {
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            changed = true;
        }

        var layout = rectTransform.GetComponent<LayoutElement>() ??
                     rectTransform.gameObject.AddComponent<LayoutElement>();

        if (Mathf.Abs(layout.preferredHeight - height) > 0.5f ||
            Mathf.Abs(layout.minHeight - height) > 0.5f)
        {
            layout.preferredHeight = height;
            layout.minHeight = height;
            changed = true;
        }

        return changed;
    }

    private static float GetActiveChildrenHeight(RectTransform parent, float padding)
    {
        var hasChild = false;
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        var corners = new Vector3[4];

        for (var i = 0; i < parent.childCount; i++)
        {
            if (parent.GetChild(i) is not RectTransform child || !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            child.GetWorldCorners(corners);

            for (var j = 0; j < corners.Length; j++)
            {
                var local = parent.InverseTransformPoint(corners[j]);

                minY = Mathf.Min(minY, local.y);
                maxY = Mathf.Max(maxY, local.y);
                hasChild = true;
            }
        }

        return hasChild ? maxY - minY + padding : parent.rect.height;
    }

    private sealed class RaceTitleLayoutState : MonoBehaviour
    {
        private bool saved;
        private Vector2 sizeDelta;
        private bool wordWrapping;
        private TextOverflowModes overflowMode;
        private int maxVisibleLines;
        private bool hadLayoutElement;
        private float minHeight;
        private float preferredHeight;

        internal void Restore(GuiLabel label)
        {
            Save(label);

            var rectTransform = label.RectTransform;

            rectTransform.sizeDelta = sizeDelta;

            if (label.TMP_Text)
            {
                label.TMP_Text.enableWordWrapping = wordWrapping;
                label.TMP_Text.overflowMode = overflowMode;
                label.TMP_Text.maxVisibleLines = maxVisibleLines;
            }

            if (!label.TryGetComponent<LayoutElement>(out var layout))
            {
                return;
            }

            if (!hadLayoutElement)
            {
                layout.minHeight = -1f;
                layout.preferredHeight = -1f;

                return;
            }

            layout.minHeight = minHeight;
            layout.preferredHeight = preferredHeight;
        }

        private void Save(GuiLabel label)
        {
            if (saved)
            {
                return;
            }

            saved = true;
            sizeDelta = label.RectTransform.sizeDelta;

            if (label.TMP_Text)
            {
                wordWrapping = label.TMP_Text.enableWordWrapping;
                overflowMode = label.TMP_Text.overflowMode;
                maxVisibleLines = label.TMP_Text.maxVisibleLines;
            }

            if (!label.TryGetComponent<LayoutElement>(out var layout))
            {
                return;
            }

            hadLayoutElement = true;
            minHeight = layout.minHeight;
            preferredHeight = layout.preferredHeight;
        }
    }

    [HarmonyPatch(typeof(CharacterStageRaceSelectionPanel), nameof(CharacterStageRaceSelectionPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterStageRaceSelectionPanel __instance)
        {
            //PATCH: avoids a restart when enabling / disabling races on the Mod UI panel
            var visibleRaces = DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
                .Where(x => !x.GuiPresentation.Hidden)
                .ToArray();
            var subRaces = new HashSet<CharacterRaceDefinition>();

            __instance.eligibleRaces.Clear();
            __instance.sortedSubRaces.Clear();
            __instance.selectedSubRace.Clear();

            foreach (var characterRaceDefinition in visibleRaces.Where(x => x.SubRaces is { Count: > 0 }))
            {
                if (characterRaceDefinition.SubRaces.Count > __instance.maxSubRacesPerRace)
                {
                    __instance.maxSubRacesPerRace = characterRaceDefinition.SubRaces.Count;
                }

                foreach (var subRace in characterRaceDefinition.SubRaces)
                {
                    subRaces.Add(subRace);
                }
            }

            var raceDefinitions = visibleRaces
                .Where(x => !subRaces.Contains(x))
                .OrderBy(x => x.FormatTitle())
                .ToArray();

            __instance.eligibleRaces.SetRange(raceDefinitions);

            for (var key = 0; key < raceDefinitions.Length; ++key)
            {
                var raceDefinition = raceDefinitions[key];

                __instance.selectedSubRace[key] = 0;
                __instance.sortedSubRaces.Add(raceDefinition, []);

                if (raceDefinition.SubRaces.Count == 0)
                {
                    continue;
                }

                foreach (var subRace in raceDefinition.SubRaces.Where(x => !x.GuiPresentation.Hidden))
                {
                    __instance.sortedSubRaces[raceDefinition].Add(subRace);
                }

                __instance.sortedSubRaces[raceDefinition].Sort(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageRaceSelectionPanel), nameof(CharacterStageRaceSelectionPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageRaceSelectionPanel __instance)
        {
            RefreshRaceContentLayout(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageRaceSelectionPanel),
        nameof(CharacterStageRaceSelectionPanel.OnFeatureChoiceChangedCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnFeatureChoiceChangedCb_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            [NotNull] CharacterStageRaceSelectionPanel __instance,
            [CanBeNull] FeatureDescriptionItem __0)
        {
            FeatureDescriptionItemPatcher.TryPersistCharacterCreationFeatureSetDefaultSelection(__0);
            TrySaveHumanOriginFeatSelection(__instance, __0);
        }
    }

}
