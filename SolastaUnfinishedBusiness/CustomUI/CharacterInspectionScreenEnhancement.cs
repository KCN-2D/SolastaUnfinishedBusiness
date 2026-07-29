using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class CharacterInspectionScreenEnhancement
{
    private const string DragonbornDraconicChoiceFeatureSetName = "FeatureSetDragonbornDraconicChoice";

    private static Transform ClassSelector { get; set; }

    private static int SelectedClassIndex { get; set; }

    private static void HideClassBadge([NotNull] Transform child)
    {
        child.GetComponent<CharacterInformationBadge>().Unbind();
        child.gameObject.SetActive(false);
    }

    [CanBeNull]
    private static RulesetCharacterHero GetInspectedHero(CharacterInformationPanel panel)
    {
        return Global.InspectedHero ?? panel?.InspectedCharacter?.RulesetCharacter as RulesetCharacterHero;
    }

    private static bool TryFindChoiceFeatureFromExclusionSet(
        IEnumerable<FeatureDefinition> features,
        System.Func<FeatureDefinition, bool> isMatchingChoice,
        out FeatureDefinition choiceFeature)
    {
        foreach (var featureDefinition in features)
        {
            if (featureDefinition is not FeatureDefinitionFeatureSet
                {
                    Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion
                } definitionFeatureSet || !definitionFeatureSet.FeatureSet.Any(isMatchingChoice))
            {
                continue;
            }

            choiceFeature = featureDefinition;

            return true;
        }

        choiceFeature = null;
        return false;
    }

    [CanBeNull]
    internal static CharacterClassDefinition SelectedClass
    {
        get
        {
            var hero = Global.InspectedHero;
            var classesAndLevels = hero?.ClassesAndLevels;
            var classesCount = classesAndLevels?.Count ?? 0;

            if (classesCount == 0)
            {
                return null;
            }

            SelectedClassIndex = Mathf.Clamp(SelectedClassIndex, 0, classesCount - 1);

            return classesAndLevels.Keys.ElementAtOrDefault(SelectedClassIndex);
        }
    }

    internal static void ResetInspectionState()
    {
        SelectedClassIndex = 0;
    }

    [NotNull]
    internal static string GetSelectedClassSearchTerm(string original)
    {
        var selectedClass = SelectedClass;

        return original
               + (!selectedClass
                   ? string.Empty
                   : selectedClass.Name);
    }

    internal static void EnumerateClassBadges([NotNull] CharacterInformationPanel __instance)
    {
        var badgeDefinitions = __instance.badgeDefinitions;
        var classBadgesTable = __instance.classBadgesTable;
        var classBadgePrefab = __instance.classBadgePrefab;
        var hero = Global.InspectedHero;
        var selectedClass = SelectedClass;

        if (hero == null || !selectedClass)
        {
            badgeDefinitions.Clear();

            for (var childIndex = 0; childIndex < classBadgesTable.childCount; ++childIndex)
            {
                HideClassBadge(classBadgesTable.GetChild(childIndex));
            }

            return;
        }

        badgeDefinitions.SetRange(hero.ClassesAndSubclasses
            .Where(x => x.Key == selectedClass)
            .Select(classesAndSubclass => classesAndSubclass.Value));

        if (hero.DeityDefinition && (selectedClass == Paladin || selectedClass == Cleric))
        {
            badgeDefinitions.Add(hero.DeityDefinition);
        }

        badgeDefinitions.AddRange(GetTrainedFightingStyles());

        while (classBadgesTable.childCount < badgeDefinitions.Count)
        {
            Gui.GetPrefabFromPool(classBadgePrefab, classBadgesTable);
        }

        var index = 0;

        foreach (var badgeDefinition in badgeDefinitions)
        {
            var child = classBadgesTable.GetChild(index);

            child.gameObject.SetActive(true);
            child.GetComponent<CharacterInformationBadge>().Bind(badgeDefinition, classBadgesTable);
            ++index;
        }

        for (; index < classBadgesTable.childCount; ++index)
        {
            HideClassBadge(classBadgesTable.GetChild(index));
        }
    }

    [NotNull]
    // ReSharper disable once ReturnTypeCanBeEnumerable.Local
    private static HashSet<FightingStyleDefinition> GetTrainedFightingStyles()
    {
        var hero = Global.InspectedHero;
        var selectedClass = SelectedClass;
        var classBadges = new HashSet<FightingStyleDefinition>();

        if (hero == null || !selectedClass)
        {
            return classBadges;
        }

        var classLevelFightingStyles = hero.ActiveFeatures
            .Where(x => x.Key.Contains(AttributeDefinitions.TagClass))
            .SelectMany(x => x.Value
                .OfType<FeatureDefinitionFightingStyleChoice>(), (x, _) => x.Key)
            .ToList();

        for (var i = 0; i < classLevelFightingStyles.Count && i < hero.TrainedFightingStyles.Count; ++i)
        {
            if (classLevelFightingStyles[i].Contains(selectedClass.Name))
            {
                classBadges.Add(hero.TrainedFightingStyles[i]);
            }
        }

        return classBadges;
    }

    private static bool TryFindChoiceFeature(
        CharacterInformationPanel panel,
        FeatureDefinition subFeature,
        out FeatureDefinition choiceFeature)
    {
        if (TryFindChoiceFeature(subFeature, panel.InspectedCharacter.MainClassDefinition.FeatureUnlocks.Select(
                featureUnlock => featureUnlock.FeatureDefinition), out choiceFeature))
        {
            return true;
        }

        var subclass = panel.InspectedCharacter.SubclassDefinition;

        if (subclass != null
            && TryFindChoiceFeature(subFeature, subclass.FeatureUnlocks.Select(x => x.FeatureDefinition),
                out choiceFeature))
        {
            return true;
        }

        choiceFeature = null;
        return false;
    }

    internal static bool TryFindChoiceFeature(FeatureDefinition subFeature, IEnumerable<FeatureDefinition> features,
        out FeatureDefinition choiceFeature)
    {
        return TryFindChoiceFeatureFromExclusionSet(features, choice => choice == subFeature, out choiceFeature);
    }

    internal static bool TryFindChoiceFeature(
        string subFeature,
        RulesetCharacterHero hero,
        out FeatureDefinition choiceFeature)
    {
        foreach (var def in hero.ClassesAndLevels.Keys)
        {
            if (TryFindChoiceFeature(subFeature, def.featureUnlocks.Select(x => x.FeatureDefinition),
                    out choiceFeature))
            {
                return true;
            }

        }
        
        foreach (var def in hero.ClassesAndSubclasses.Values)
        {
            if (TryFindChoiceFeature(subFeature, def.featureUnlocks.Select(x => x.FeatureDefinition),
                    out choiceFeature))
            {
                return true;
            }

        }
        
        choiceFeature = null;
        return false;
    }
    
    internal static bool TryFindChoiceFeature(string subFeature, IEnumerable<FeatureDefinition> features,
        out FeatureDefinition choiceFeature)
    {
        return TryFindChoiceFeatureFromExclusionSet(features, choice => choice.Name == subFeature, out choiceFeature);
    }

    private static bool TryGetDragonbornDraconicChoiceInspectionDisplayFeature(
        FeatureDefinition sourceFeature,
        out FeatureDefinitionFeatureSet parentFeature,
        out FeatureDefinition selectedFeature)
    {
        parentFeature = null;
        selectedFeature = null;

        var featureSetDatabase = DatabaseRepository.GetDatabase<FeatureDefinitionFeatureSet>();
        var dragonbornDraconicChoice = featureSetDatabase.GetElement(DragonbornDraconicChoiceFeatureSetName, true);

        if (!dragonbornDraconicChoice ||
            !dragonbornDraconicChoice.FeatureSet.Contains(sourceFeature))
        {
            return false;
        }

        parentFeature = dragonbornDraconicChoice;
        selectedFeature = sourceFeature;

        return true;
    }

    private static void BindParentChoiceFeatureDisplay(
        GuiLabel label,
        GuiTooltip tooltip,
        CustomTooltipProvider provider,
        FeatureUnlockByLevel feature,
        FeatureDefinition parentFeature,
        FeatureDefinition selectedFeature,
        bool noLevel)
    {
        var title = parentFeature.FormatTitle();
        var description = CustomTooltipProvider.FormatDescription(selectedFeature);

        label.Text = title + (!noLevel ? $" ({feature.Level})" : string.Empty);
        provider.SetTitle(title);
        provider.SetSubtitle(selectedFeature.FormatTitle());
        provider.SetDescription(description);
        tooltip.Content = string.IsNullOrEmpty(description)
            ? CustomTooltipProvider.GetActivationContent(selectedFeature)
            : description;
    }

    internal static bool EnhanceFeatureList(
        CharacterInformationPanel panel,
        RectTransform table,
        List<FeatureUnlockByLevel> features,
        string insufficientLevelFormat,
        TooltipDefinitions.AnchorMode tooltipAnchorMode)
    {
        var inspectedHero = GetInspectedHero(panel);
        CharacterHeroBuildingData buildingData = null;

        if (inspectedHero != null)
        {
            inspectedHero.TryGetHeroBuildingData(out buildingData);
        }

        while (table.childCount < features.Count)
        {
            Gui.GetPrefabFromPool(panel.featurePrefab, table);
        }

        var index = 0;

        foreach (var feature in features)
        {
            var child = table.GetChild(index);

            BindFeatureRow(panel, child, feature, insufficientLevelFormat, tooltipAnchorMode, inspectedHero, buildingData);
            ++index;
        }

        for (var count = features.Count; count < table.childCount; ++count)
        {
            HideFeatureRow(panel, table.GetChild(count));
        }

        return false;
    }

    private static void BindFeatureRow(
        CharacterInformationPanel panel,
        Transform child,
        FeatureUnlockByLevel feature,
        string insufficientLevelFormat,
        TooltipDefinitions.AnchorMode tooltipAnchorMode,
        RulesetCharacterHero inspectedHero,
        CharacterHeroBuildingData buildingData)
    {
        child.gameObject.SetActive(true);
        RestoreFeatureRowPresentation(panel, child);

        var label = child.GetComponent<GuiLabel>();
        var noLevel = feature.Level == 0;
        var title = feature.FeatureDefinition.FormatTitle();

        label.Text = title + (!noLevel ? $" ({feature.Level})" : string.Empty);
        Gui.HexaKeyToColor(noLevel ? Gui.ColorAlmostWhite : Gui.ColorNegative, out var color);
        label.TMP_Text.color = color;

        var tooltip = child.GetComponent<GuiTooltip>();
        var provider = new CustomTooltipProvider(feature.FeatureDefinition, null);

        tooltip.Content = CustomTooltipProvider.GetActivationContent(feature.FeatureDefinition);

        if (Tabletop2024Context.TryGetHumanOriginInspectionDisplayFeature(
                inspectedHero,
                buildingData,
                feature.FeatureDefinition,
                out var displayFeature,
                out var fallbackTitle))
        {
            var humanOriginTitle = Gui.Localize("Feature/&PointPoolHumanOriginFeatTitle");

            provider.SetTitle(humanOriginTitle);

            if (displayFeature)
            {
                var description = Tabletop2024Context.FormatOriginFeatGainDescription(displayFeature);

                label.Text = humanOriginTitle + (!noLevel ? $" ({feature.Level})" : string.Empty);
                provider.SetSubtitle(displayFeature.FormatTitle());
                provider.SetDescription(description);

                if (!string.IsNullOrEmpty(description))
                {
                    tooltip.Content = description;
                }
            }
            else
            {
                label.Text = fallbackTitle + (!noLevel ? $" ({feature.Level})" : string.Empty);
            }
        }
        else if (feature.FeatureDefinition is FeatureDefinitionPower)
        {
            var guiPowerDefinition = ServiceRepository.GetService<IGuiWrapperService>()
                .GetGuiPowerDefinition(feature.FeatureDefinition.Name);

            if (!CustomTooltipProvider.IsUnavailableContent(guiPowerDefinition.Description))
            {
                tooltip.Content = guiPowerDefinition.Description;
            }
        }
        else if (Tabletop2024Context.TryGetHalfElfVersatileBloodlineInspectionDisplayFeature(
                     feature.FeatureDefinition,
                     out var halfElfParentFeature,
                     out var halfElfSelectedFeature))
        {
            BindParentChoiceFeatureDisplay(
                label,
                tooltip,
                provider,
                feature,
                halfElfParentFeature,
                halfElfSelectedFeature,
                noLevel);
        }
        else if (TryGetDragonbornDraconicChoiceInspectionDisplayFeature(
                     feature.FeatureDefinition,
                     out var dragonbornParentFeature,
                     out var dragonbornSelectedFeature))
        {
            BindParentChoiceFeatureDisplay(
                label,
                tooltip,
                provider,
                feature,
                dragonbornParentFeature,
                dragonbornSelectedFeature,
                noLevel);
        }
        else if (TryFindChoiceFeature(panel, feature.FeatureDefinition, out var choiceFeature))
        {
            label.Text = Gui.Format("{1} ({0})", feature.FeatureDefinition.FormatTitle(),
                choiceFeature.FormatTitle());

            if (feature.FeatureDefinition.GuiPresentation.Description == Gui.NoLocalization)
            {
                provider.BaseDefinition = choiceFeature;
                provider.SetSubtitle(feature.FeatureDefinition.GuiPresentation.Title);
            }
            else
            {
                provider.SetSubtitle(choiceFeature.GuiPresentation.Title);
            }
        }
        tooltip.TooltipClass = "FeatDefinition";
        tooltip.DataProvider = provider;
        tooltip.Context = panel.InspectedCharacter?.RulesetCharacter;
        tooltip.AnchorMode = tooltipAnchorMode;

        if (!noLevel)
        {
            var levelRequirement = Gui.Format(insufficientLevelFormat, feature.Level.ToString());

            provider.SetPrerequisites(levelRequirement);
        }
    }

    private static void HideFeatureRow(CharacterInformationPanel panel, Transform child)
    {
        RestoreFeatureRowPresentation(panel, child);
        child.gameObject.SetActive(false);
    }

    private static void RestoreFeatureRowPresentation(CharacterInformationPanel panel, Transform child)
    {
        var state = child.GetComponent<FeatureRowPresentationState>() ??
                    child.gameObject.AddComponent<FeatureRowPresentationState>();

        state.Capture(panel.featurePrefab);
        state.Restore(child);
    }

    private sealed class FeatureRowPresentationState : MonoBehaviour
    {
        private bool Captured { get; set; }

        private bool HasCanvasGroup { get; set; }

        private bool HasLayoutElement { get; set; }

        private bool HasText { get; set; }

        private Color[] ImageColors { get; set; }

        private float CanvasGroupAlpha { get; set; }

        private bool CanvasGroupBlocksRaycasts { get; set; }

        private bool CanvasGroupIgnoreParentGroups { get; set; }

        private bool CanvasGroupInteractable { get; set; }

        private bool LayoutElementIgnoreLayout { get; set; }

        private float LayoutElementMinWidth { get; set; }

        private float LayoutElementMinHeight { get; set; }

        private float LayoutElementPreferredWidth { get; set; }

        private float LayoutElementPreferredHeight { get; set; }

        private float LayoutElementFlexibleWidth { get; set; }

        private float LayoutElementFlexibleHeight { get; set; }

        private int LayoutElementPriority { get; set; }

        private Vector4 TextMargin { get; set; }

        private TextAlignmentOptions TextAlignment { get; set; }

        private bool TextAutoSizeTextContainer { get; set; }

        private bool TextEnableAutoSizing { get; set; }

        private bool TextEnableWordWrapping { get; set; }

        private int TextMaxVisibleLines { get; set; }

        private TextOverflowModes TextOverflowMode { get; set; }

        private float TextFontSize { get; set; }

        private float TextFontSizeMin { get; set; }

        private float TextFontSizeMax { get; set; }

        private float TextLineSpacing { get; set; }

        internal void Capture(GameObject source)
        {
            if (Captured)
            {
                return;
            }

            source = source ? source : gameObject;

            var images = source.GetComponentsInChildren<Image>(true);

            if (images.Length > 0)
            {
                ImageColors = new Color[images.Length];

                for (var i = 0; i < images.Length; i++)
                {
                    ImageColors[i] = images[i].color;
                }
            }

            if (source.TryGetComponent<CanvasGroup>(out var canvasGroup))
            {
                HasCanvasGroup = true;
                CanvasGroupAlpha = canvasGroup.alpha;
                CanvasGroupBlocksRaycasts = canvasGroup.blocksRaycasts;
                CanvasGroupIgnoreParentGroups = canvasGroup.ignoreParentGroups;
                CanvasGroupInteractable = canvasGroup.interactable;
            }

            if (source.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                HasLayoutElement = true;
                LayoutElementIgnoreLayout = layoutElement.ignoreLayout;
                LayoutElementMinWidth = layoutElement.minWidth;
                LayoutElementMinHeight = layoutElement.minHeight;
                LayoutElementPreferredWidth = layoutElement.preferredWidth;
                LayoutElementPreferredHeight = layoutElement.preferredHeight;
                LayoutElementFlexibleWidth = layoutElement.flexibleWidth;
                LayoutElementFlexibleHeight = layoutElement.flexibleHeight;
                LayoutElementPriority = layoutElement.layoutPriority;
            }

            var text = GetText(source);

            if (text)
            {
                HasText = true;
                TextMargin = text.margin;
                TextAlignment = text.alignment;
                TextAutoSizeTextContainer = text.autoSizeTextContainer;
                TextEnableAutoSizing = text.enableAutoSizing;
                TextEnableWordWrapping = text.enableWordWrapping;
                TextMaxVisibleLines = text.maxVisibleLines;
                TextOverflowMode = text.overflowMode;
                TextFontSize = text.fontSize;
                TextFontSizeMin = text.fontSizeMin;
                TextFontSizeMax = text.fontSizeMax;
                TextLineSpacing = text.lineSpacing;
            }

            Captured = true;
        }

        internal void Restore(Transform child)
        {
            var target = child.gameObject;

            if (ImageColors is { Length: > 0 })
            {
                var images = target.GetComponentsInChildren<Image>(true);
                var count = Mathf.Min(ImageColors.Length, images.Length);

                for (var i = 0; i < count; i++)
                {
                    images[i].color = ImageColors[i];
                }
            }

            if (HasCanvasGroup && target.TryGetComponent<CanvasGroup>(out var canvasGroup))
            {
                canvasGroup.alpha = CanvasGroupAlpha;
                canvasGroup.blocksRaycasts = CanvasGroupBlocksRaycasts;
                canvasGroup.ignoreParentGroups = CanvasGroupIgnoreParentGroups;
                canvasGroup.interactable = CanvasGroupInteractable;
            }

            if (HasLayoutElement && target.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                layoutElement.ignoreLayout = LayoutElementIgnoreLayout;
                layoutElement.minWidth = LayoutElementMinWidth;
                layoutElement.minHeight = LayoutElementMinHeight;
                layoutElement.preferredWidth = LayoutElementPreferredWidth;
                layoutElement.preferredHeight = LayoutElementPreferredHeight;
                layoutElement.flexibleWidth = LayoutElementFlexibleWidth;
                layoutElement.flexibleHeight = LayoutElementFlexibleHeight;
                layoutElement.layoutPriority = LayoutElementPriority;
            }

            var text = GetText(target);

            if (!HasText || !text)
            {
                return;
            }

            text.margin = TextMargin;
            text.alignment = TextAlignment;
            text.autoSizeTextContainer = TextAutoSizeTextContainer;
            text.enableAutoSizing = TextEnableAutoSizing;
            text.enableWordWrapping = TextEnableWordWrapping;
            text.maxVisibleLines = TextMaxVisibleLines;
            text.overflowMode = TextOverflowMode;
            text.fontSize = TextFontSize;
            text.fontSizeMin = TextFontSizeMin;
            text.fontSizeMax = TextFontSizeMax;
            text.lineSpacing = TextLineSpacing;
            text.SetLayoutDirty();
            text.SetVerticesDirty();
        }

        private static TMP_Text GetText(GameObject target)
        {
            if (!target)
            {
                return null;
            }

            var label = target.GetComponent<GuiLabel>();

            return label ? label.TMP_Text : target.GetComponent<TMP_Text>();
        }
    }

    internal static void SwapClassAndBackground(CharacterInformationPanel panel)
    {
        var backGroup = panel.transform.Find("BackgroundGroup")?.GetComponent<RectTransform>();
        var classGroup = panel.transform.Find("ClassGroup")?.GetComponent<RectTransform>();

        if (!classGroup || !backGroup)
        {
            return;
        }

        backGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 32, 662);
        backGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Bottom, 32, 458);

        classGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Right, 32, 662);
        classGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 32, 856);

        //this is actually top-right one
        var child = backGroup.Find("OrnamentBottomRight")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 5, 50);
        }

        child = backGroup.Find("BackgroundImageMask")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0, 218);
        }

        child = backGroup.Find("BackgroundDescriptionGroup")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Bottom, 65, 175);
        }

        child = classGroup.Find("ClassFeaturesGroup")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 20, 642);
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 260, 590);

            var sizeDelta = child.sizeDelta;

            child.sizeDelta = new Vector2(sizeDelta.x, sizeDelta.y - 100);
        }

        child = classGroup.Find("ClassDescriptionGroup")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Right, 0, 355);
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0, 270);
        }

        classGroup.FindChildRecursive("OrnamentBottomLeft")?.gameObject.SetActive(false);

        //
        // setup class buttons for MC scenarios
        //

        ResetInspectionState();

        var hero = Global.InspectedHero;

        // abort on a SC hero
        if (hero?.ClassesAndLevels == null || hero.ClassesAndLevels.Count == 1)
        {
            if (ClassSelector)
            {
                ClassSelector.gameObject.SetActive(false);
            }

            return;
        }

        Transform labelsGroup;

        if (!ClassSelector)
        {
            var voice = backGroup.FindChildRecursive("Voice");

            ClassSelector = Object.Instantiate(voice, classGroup.transform);
            ClassSelector.name = "Classes";
            ClassSelector.FindChildRecursive("PlayAudio").gameObject.SetActive(false);
            ClassSelector.FindChildRecursive("HeaderGroup").gameObject.SetActive(false);

            labelsGroup = ClassSelector.FindChildRecursive("LabelsGroup");

            var firstButton = labelsGroup.GetChild(0);

            for (var i = labelsGroup.childCount; i < MulticlassContext.MaxClasses; i++)
            {
                Object.Instantiate(firstButton, firstButton.parent);
            }
        }
        else
        {
            ClassSelector.gameObject.SetActive(true);

            labelsGroup = ClassSelector.FindChildRecursive("LabelsGroup");
        }

        var classesTitles = hero.ClassesAndLevels.Select(x => x.Key.FormatTitle()).ToArray();
        var classesCount = classesTitles.Length;

        for (var i = 0; i < classesCount; i++)
        {
            var childToggle = labelsGroup.GetChild(i);
            var labelChoiceToggle = childToggle.GetComponent<LabelChoiceToggle>();
            var uiToggle = childToggle.GetComponent<Toggle>();

            childToggle.gameObject.SetActive(true);

            labelChoiceToggle.Bind(i, classesTitles[i], x =>
            {
                if (!uiToggle.isOn)
                {
                    return;
                }

                SelectedClassIndex = x;
                panel.RefreshNow();

                for (var c = 0; c < classesCount; ++c)
                {
                    if (c != x)
                    {
                        labelsGroup.GetChild(c).GetComponent<LabelChoiceToggle>().Refresh(false, true);
                    }
                }
            });
        }

        labelsGroup.GetChild(0).GetComponent<Toggle>().isOn = true;

        for (var i = classesCount; i < MulticlassContext.MaxClasses; i++)
        {
            labelsGroup.GetChild(i).gameObject.SetActive(false);
        }
    }
}
