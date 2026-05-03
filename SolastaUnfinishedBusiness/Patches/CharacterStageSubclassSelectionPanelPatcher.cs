using System.Diagnostics.CodeAnalysis;
using System.Collections;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageSubclassSelectionPanelPatcher
{
    private const float FeaturesBottomPadding = 32f;
    private const float FeaturesViewportGap = 6f;
    private const string FeaturesViewportName = "SelectedSubclassFeaturesViewport";
    private const float MinFeaturesViewportHeight = 1f;
    private const float FeaturesScrollSensitivityMin = 36f;
    private const float FeaturesScrollSensitivityViewportRatio = 0.10f;
    private const float FeaturesScrollSensitivityMax = 96f;

    private static void RefreshSubclassFeaturesLayout(CharacterStageSubclassSelectionPanel panel)
    {
        if (!panel || !panel.selectedSubclassFeaturesList || !panel.selectedSubclassFeaturesGroup)
        {
            return;
        }

        var featureItems = panel.selectedSubclassFeaturesList.GetComponentsInChildren<FeatureDescriptionItem>(false);

        if (featureItems.Length == 0)
        {
            return;
        }

        foreach (var item in featureItems)
        {
            FeatureDescriptionItemPatcher.RefreshSelectionFeatureDisplayLayout(item);
        }

        ConfigureSubclassFeaturesScroll(panel);

        RebuildSubclassFeaturesLayout(panel);
        panel.StartCoroutine(RefreshSubclassFeaturesLayoutNextFrame(panel));
    }

    private static void ConfigureSubclassFeaturesScroll(CharacterStageSubclassSelectionPanel panel)
    {
        var featuresGroup = panel.selectedSubclassFeaturesGroup;
        var content = panel.selectedSubclassFeaturesList;
        var table = featuresGroup.parent as RectTransform;

        if (!featuresGroup || !content || !table)
        {
            return;
        }

        var viewport = GetOrCreateSubclassFeaturesViewport(featuresGroup, content);

        DisableLegacyFeaturesGroupScroll(featuresGroup);
        MoveContentToViewport(content, viewport);
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);

        var contentHeight = Mathf.Ceil(Mathf.Max(MinFeaturesViewportHeight, content.rect.height));
        var headerHeight = GetFeaturesHeaderHeight(featuresGroup, viewport);
        var topInset = headerHeight > 0f ? headerHeight + FeaturesViewportGap : 0f;
        var followingReservedHeight = GetFollowingActiveChildrenHeight(table, featuresGroup);
        var availableGroupHeight = GetAvailableFeaturesGroupHeight(table, featuresGroup, followingReservedHeight);
        var availableViewportHeight = Mathf.Max(MinFeaturesViewportHeight, availableGroupHeight - topInset);
        var viewportHeight = Mathf.Ceil(Mathf.Clamp(contentHeight, MinFeaturesViewportHeight, availableViewportHeight));
        var groupHeight = Mathf.Ceil(topInset + viewportHeight);

        SetRectHeightPreservingTop(featuresGroup, groupHeight);
        SetViewportBelowHeader(viewport, topInset);

        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(0f, 1f);
        content.pivot = new Vector2(0f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);

        var image = viewport.GetComponent<Image>() ?? viewport.gameObject.AddComponent<Image>();

        image.color = Color.clear;
        image.raycastTarget = true;

        if (!viewport.GetComponent<RectMask2D>())
        {
            viewport.gameObject.AddComponent<RectMask2D>();
        }

        var scrollRect = viewport.GetComponent<ScrollRect>() ?? viewport.gameObject.AddComponent<ScrollRect>();

        scrollRect.content = content;
        scrollRect.viewport = viewport;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.horizontalScrollbar = null;
        scrollRect.verticalScrollbar = null;
        scrollRect.scrollSensitivity = Mathf.Clamp(
            viewportHeight * FeaturesScrollSensitivityViewportRatio,
            FeaturesScrollSensitivityMin,
            FeaturesScrollSensitivityMax);
        scrollRect.verticalNormalizedPosition = 1f;
    }

    private static float GetAvailableFeaturesGroupHeight(
        RectTransform table,
        RectTransform featuresGroup,
        float followingReservedHeight)
    {
        var topLimit = GetFeaturesGroupTopLimit(table, featuresGroup);
        var bottomLimit = table.rect.yMin + followingReservedHeight;

        return Mathf.Max(MinFeaturesViewportHeight, topLimit - bottomLimit);
    }

    private static RectTransform GetOrCreateSubclassFeaturesViewport(RectTransform featuresGroup, RectTransform content)
    {
        var viewport = featuresGroup.Find(FeaturesViewportName) as RectTransform;

        if (viewport)
        {
            return viewport;
        }

        var viewportObject = new GameObject(FeaturesViewportName, typeof(RectTransform));

        viewport = viewportObject.transform as RectTransform;
        viewport.SetParent(featuresGroup, false);
        viewport.SetSiblingIndex(content.GetSiblingIndex());

        return viewport;
    }

    private static RectTransform GetSubclassFeaturesViewport(RectTransform featuresGroup)
    {
        return featuresGroup ? featuresGroup.Find(FeaturesViewportName) as RectTransform : null;
    }

    private static void DisableLegacyFeaturesGroupScroll(RectTransform featuresGroup)
    {
        var scrollRect = featuresGroup.GetComponent<ScrollRect>();

        if (scrollRect)
        {
            scrollRect.enabled = false;
            scrollRect.content = null;
            scrollRect.viewport = null;
            scrollRect.horizontal = false;
            scrollRect.vertical = false;
        }

        var image = featuresGroup.GetComponent<Image>();

        if (image)
        {
            image.raycastTarget = false;
        }
    }

    private static void MoveContentToViewport(RectTransform content, RectTransform viewport)
    {
        if (content.parent != viewport)
        {
            content.SetParent(viewport, false);
        }

        content.SetSiblingIndex(0);
    }

    private static void SetRectHeightPreservingTop(RectTransform rectTransform, float height)
    {
        var top = rectTransform.anchoredPosition.y + rectTransform.rect.height * (1f - rectTransform.pivot.y);

        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        rectTransform.anchoredPosition = new Vector2(
            rectTransform.anchoredPosition.x,
            top - height * (1f - rectTransform.pivot.y));

        var layout = rectTransform.GetComponent<LayoutElement>() ?? rectTransform.gameObject.AddComponent<LayoutElement>();

        layout.minHeight = height;
        layout.preferredHeight = height;
        layout.flexibleHeight = -1f;
    }

    private static void SetViewportBelowHeader(RectTransform viewport, float topInset)
    {
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = new Vector2(0f, -topInset);
    }

    private static void RebuildSubclassFeaturesLayout(CharacterStageSubclassSelectionPanel panel)
    {
        if (!panel || !panel.selectedSubclassFeaturesList || !panel.selectedSubclassFeaturesGroup)
        {
            return;
        }

        var viewport = GetSubclassFeaturesViewport(panel.selectedSubclassFeaturesGroup);

        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.selectedSubclassFeaturesList);

        if (viewport)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(viewport);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.selectedSubclassFeaturesGroup);

        if (panel.selectedSubclassFeaturesGroup.parent is RectTransform parent)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
        }
    }

    private static float GetFeaturesHeaderHeight(RectTransform featuresGroup, RectTransform viewport)
    {
        var height = 0f;

        for (var i = 0; i < featuresGroup.childCount; i++)
        {
            if (featuresGroup.GetChild(i) is not RectTransform child ||
                child == viewport ||
                !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            height = Mathf.Max(height, Mathf.Max(child.rect.height, LayoutUtility.GetPreferredHeight(child)));
        }

        return Mathf.Ceil(height);
    }

    private static float GetFeaturesGroupTopLimit(RectTransform table, RectTransform featuresGroup)
    {
        var spacing = table.TryGetComponent<VerticalLayoutGroup>(out var layoutGroup) ? layoutGroup.spacing : 0f;
        RectTransform previousActiveChild = null;

        for (var i = 0; i < table.childCount; i++)
        {
            if (table.GetChild(i) is not RectTransform child)
            {
                continue;
            }

            if (child == featuresGroup)
            {
                break;
            }

            if (child.gameObject.activeInHierarchy)
            {
                previousActiveChild = child;
            }
        }

        return previousActiveChild
            ? GetLocalBounds(table, previousActiveChild).yMin - spacing
            : table.rect.yMax;
    }

    private static float GetFollowingActiveChildrenHeight(RectTransform table, RectTransform featuresGroup)
    {
        var reservedHeight = FeaturesBottomPadding;
        var afterFeaturesGroup = false;
        var spacing = table.TryGetComponent<VerticalLayoutGroup>(out var layoutGroup) ? layoutGroup.spacing : 0f;

        for (var i = 0; i < table.childCount; i++)
        {
            if (table.GetChild(i) is not RectTransform child)
            {
                continue;
            }

            if (child == featuresGroup)
            {
                afterFeaturesGroup = true;

                continue;
            }

            if (!afterFeaturesGroup || !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            reservedHeight += spacing + Mathf.Max(child.rect.height, LayoutUtility.GetPreferredHeight(child));
        }

        return reservedHeight;
    }

    private static Rect GetLocalBounds(RectTransform parent, RectTransform child)
    {
        var corners = new Vector3[4];

        child.GetWorldCorners(corners);

        var first = parent.InverseTransformPoint(corners[0]);
        var minX = first.x;
        var maxX = first.x;
        var minY = first.y;
        var maxY = first.y;

        for (var i = 1; i < corners.Length; i++)
        {
            var local = parent.InverseTransformPoint(corners[i]);

            minX = Mathf.Min(minX, local.x);
            maxX = Mathf.Max(maxX, local.x);
            minY = Mathf.Min(minY, local.y);
            maxY = Mathf.Max(maxY, local.y);
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static IEnumerator RefreshSubclassFeaturesLayoutNextFrame(CharacterStageSubclassSelectionPanel panel)
    {
        yield return null;

        if (!panel)
        {
            yield break;
        }

        Canvas.ForceUpdateCanvases();
        ConfigureSubclassFeaturesScroll(panel);
        RebuildSubclassFeaturesLayout(panel);
        Canvas.ForceUpdateCanvases();
    }

    [HarmonyPatch(typeof(CharacterStageSubclassSelectionPanel),
        nameof(CharacterStageSubclassSelectionPanel.FillSubclassFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillSubclassFeatures_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterStageSubclassSelectionPanel __instance)
        {
            RefreshSubclassFeaturesLayout(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageSubclassSelectionPanel),
        nameof(CharacterStageSubclassSelectionPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterStageSubclassSelectionPanel __instance)
        {
            __instance.compatibleSubclasses.RemoveAll(
                subclass => !StrictTabletopSelectionContext.IsSubclassAllowedForCurrentMode(subclass));

            //PATCH: changes the subclasses layout to allow more offering
            var table = __instance.subclassesTable;
            var tableParent = table.parent;
            var gridLayoutGroup = table.GetComponent<GridLayoutGroup>();
            var rectTransform = tableParent.parent.parent.GetComponent<RectTransform>();
            var mask = tableParent.GetComponent<Mask>();

            gridLayoutGroup.spacing = new Vector2(50, 100);
            gridLayoutGroup.constraintCount = 3;
            rectTransform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            rectTransform.anchoredPosition = new Vector2(rectTransform.anchoredPosition.x, 30f);
            mask.rectTransform.sizeDelta = new Vector2(0, 250);

            //PATCH: sorts the sub classes panel by Title
            if (!Main.Settings.EnableSortingSubclasses)
            {
                return;
            }

            __instance.compatibleSubclasses.Sort(Sorting.CompareTitle);
        }
    }

    [HarmonyPatch(typeof(CharacterStageSubclassSelectionPanel),
        nameof(CharacterStageSubclassSelectionPanel.UpdateRelevance))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UpdateRelevance_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterStageSubclassSelectionPanel __instance)
        {
            //PATCH: updates this panel relevance (MULTICLASS)
            if (LevelUpHelper.IsLevelingUp(__instance.currentHero)
                && LevelUpHelper.RequiresDeity(__instance.currentHero))
            {
                __instance.isRelevant = false;
            }
        }
    }
}
