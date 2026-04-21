using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class CharacterInspectionScreenEnhancement
{
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

    internal static bool EnhanceFeatureList(
        CharacterInformationPanel panel,
        RectTransform table,
        List<FeatureUnlockByLevel> features,
        string insufficientLevelFormat,
        TooltipDefinitions.AnchorMode tooltipAnchorMode)
    {
        var inspectedHero = GetInspectedHero(panel);
        var buildingData = inspectedHero?.GetHeroBuildingData();

        while (table.childCount < features.Count)
        {
            Gui.GetPrefabFromPool(panel.featurePrefab, table);
        }

        var index = 0;

        foreach (var feature in features)
        {
            var child = table.GetChild(index);

            child.gameObject.SetActive(true);

            var label = child.GetComponent<GuiLabel>();
            var noLevel = feature.Level == 0;
            var title = feature.FeatureDefinition.FormatTitle();

            label.Text = title + (!noLevel ? $" ({feature.Level})" : string.Empty);
            Gui.HexaKeyToColor(noLevel ? Gui.ColorAlmostWhite : Gui.ColorNegative, out var color);
            label.TMP_Text.color = color;

            var tooltip = child.GetComponent<GuiTooltip>();
            var provider = new CustomTooltipProvider(feature.FeatureDefinition, null);

            if (Tabletop2024Context.TryGetHumanOriginInspectionDisplayFeature(
                    inspectedHero,
                    buildingData,
                    feature.FeatureDefinition,
                    out var displayFeature,
                    out var fallbackTitle))
            {
                if (displayFeature)
                {
                    label.Text = displayFeature.FormatTitle() + (!noLevel ? $" ({feature.Level})" : string.Empty);
                    provider.BaseDefinition = displayFeature;
                    provider.SetSubtitle(Gui.Localize("Feature/&PointPoolHumanOriginFeatTitle"));
                    tooltip.Content = displayFeature.FormatDescription();
                }
                else
                {
                    label.Text = fallbackTitle + (!noLevel ? $" ({feature.Level})" : string.Empty);
                    tooltip.Content = feature.FeatureDefinition.FormatDescription();
                }
            }
            else if (feature.FeatureDefinition is FeatureDefinitionPower)
            {
                var guiPowerDefinition = ServiceRepository.GetService<IGuiWrapperService>()
                    .GetGuiPowerDefinition(feature.FeatureDefinition.Name);

                tooltip.Content = guiPowerDefinition.Description;
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
            else
            {
                tooltip.Content = feature.FeatureDefinition.FormatDescription();
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

            ++index;
        }

        for (var count = features.Count; count < table.childCount; ++count)
        {
            table.GetChild(count).gameObject.SetActive(false);
        }

        return false;
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
