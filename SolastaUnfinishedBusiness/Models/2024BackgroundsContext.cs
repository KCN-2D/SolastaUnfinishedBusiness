using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using static RuleDefinitions;
using static FeatureDefinitionAttributeModifier;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private const string BackgroundDevotedName = "BackgroundDevoted";
    private const string BackgroundFarmerName = "BackgroundFarmer";
    private const string BackgroundMilitiaName = "BackgroundMilitia";
    private const string BackgroundSpyName = "Spy";
    private const string BackgroundTroublemakerName = "BackgroundTroublemaker";
    private const string FeatSkilledName = "FeatSkilled";
    private const string HumanOriginFeatFeatureSetName = "FeatureSetHumanOriginFeat2024";
    private const string HumanOriginFeatChoiceFeatureSetPrefix = "FeatureSetHumanOriginFeat2024_";
    private const string HumanOriginFeatPointPoolName = "PointPoolHumanOriginFeat2024";
    private const string HumanOriginFeatSkilledPointPoolName = "PointPoolHumanOriginFeatSkilled";
    private const string HumanOriginFeatTag = "02RaceHumanOriginFeat2024";
    private const string HumanOriginFeatSkilledSkillTag = "02RaceHumanOriginFeat2024_SkilledSkills";

    private static readonly Dictionary<string, (string A, string B, string C)> BackgroundAbilitySets = new()
    {
        { "Academic", (AttributeDefinitions.Dexterity, AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom) },
        { "Acolyte", (AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma) },
        { "Aescetic_Background", (AttributeDefinitions.Constitution, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma) },
        { "Aristocrat", (AttributeDefinitions.Strength, AttributeDefinitions.Intelligence, AttributeDefinitions.Charisma) },
        { "Artist_Background", (AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Charisma) },
        { "Lawkeeper", (AttributeDefinitions.Strength, AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom) },
        { "Lowlife", (AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence) },
        { "Occultist_Background", (AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence, AttributeDefinitions.Charisma) },
        { "Philosopher", (AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom) },
        { "SellSword", (AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution) },
        { BackgroundSpyName, (AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution, AttributeDefinitions.Charisma) },
        { "Wanderer", (AttributeDefinitions.Dexterity, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma) },
        { BackgroundDevotedName, (AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom) },
        { BackgroundFarmerName, (AttributeDefinitions.Strength, AttributeDefinitions.Constitution, AttributeDefinitions.Wisdom) },
        { BackgroundMilitiaName, (AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Wisdom) },
        { BackgroundTroublemakerName, (AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution, AttributeDefinitions.Charisma) }
    };

    private static readonly Dictionary<string, string> BackgroundFeatSets = new()
    {
        { "Academic", FeatSkilledName },
        { "Acolyte", "FeatMagicInitiateCleric" },
        { "Aescetic_Background", "FeatHealer" },
        { "Aristocrat", FeatSkilledName },
        { "Artist_Background", "FeatMagicInitiateBard" },
        { "Lawkeeper", "FeatSavageAttack" },
        { "Lowlife", "FeatLucky" },
        { "Occultist_Background", "FeatMagicInitiateWarlock" },
        { "Philosopher", "FeatMagicInitiateWizard" },
        { "SellSword", "FeatSavageAttack" },
        { BackgroundSpyName, "FeatAlert" },
        { "Wanderer", "FeatMagicInitiateDruid" },
        { BackgroundDevotedName, "FeatLucky" },
        { BackgroundFarmerName, "FeatTough" },
        { BackgroundMilitiaName, "FeatAlert" },
        { BackgroundTroublemakerName, "FeatMagicInitiateSorcerer" }
    };

    private static readonly Dictionary<string, (string SkillA, string SkillB, string ToolA, string ToolB)> BackgroundProficiencySets =
        new()
    {
        { "Academic", (SkillDefinitions.Investigation, SkillDefinitions.Perception, ToolTypeDefinitions.ScrollKitType.Name, ToolTypeDefinitions.EnchantingToolType.Name) },
        { "Acolyte", (SkillDefinitions.Insight, SkillDefinitions.Religion, ToolTypeDefinitions.HerbalismKitType.Name, ToolTypeDefinitions.ScrollKitType.Name) },
        { "Aescetic_Background", (SkillDefinitions.Medecine, SkillDefinitions.Religion, ToolTypeDefinitions.HerbalismKitType.Name, ToolTypeDefinitions.ScrollKitType.Name) },
        { "Aristocrat", (SkillDefinitions.History, SkillDefinitions.Persuasion, "GamingSetDiceType", "MusicalInstrumentLyreType") },
        { "Artist_Background", (SkillDefinitions.Acrobatics, SkillDefinitions.Performance, "MusicalInstrumentLyreType", ToolTypeDefinitions.DisguiseKitType.Name) },
        { "Lawkeeper", (SkillDefinitions.Investigation, SkillDefinitions.Intimidation, ToolTypeDefinitions.ScrollKitType.Name, ToolTypeDefinitions.ArtisanToolSmithToolsType.Name) },
        { "Lowlife", (SkillDefinitions.Insight, SkillDefinitions.Stealth, ToolTypeDefinitions.ThievesToolsType.Name, ToolTypeDefinitions.PoisonersKitType.Name) },
        { "Occultist_Background", (SkillDefinitions.Arcana, SkillDefinitions.Religion, ToolTypeDefinitions.EnchantingToolType.Name, ToolTypeDefinitions.ScrollKitType.Name) },
        { "Philosopher", (SkillDefinitions.Arcana, SkillDefinitions.History, ToolTypeDefinitions.ScrollKitType.Name, ToolTypeDefinitions.EnchantingToolType.Name) },
        { "SellSword", (SkillDefinitions.Athletics, SkillDefinitions.Intimidation, "GamingSetDiceType", ToolTypeDefinitions.ArtisanToolSmithToolsType.Name) },
        { BackgroundSpyName, (SkillDefinitions.Stealth, SkillDefinitions.Deception, ToolTypeDefinitions.DisguiseKitType.Name, ToolTypeDefinitions.ThievesToolsType.Name) },
        { "Wanderer", (SkillDefinitions.Stealth, SkillDefinitions.Survival, ToolTypeDefinitions.HerbalismKitType.Name, ToolTypeDefinitions.PoisonersKitType.Name) },
        { BackgroundDevotedName, (SkillDefinitions.Investigation, SkillDefinitions.Persuasion, ToolTypeDefinitions.ScrollKitType.Name, ToolTypeDefinitions.HerbalismKitType.Name) },
        { BackgroundFarmerName, (SkillDefinitions.AnimalHandling, SkillDefinitions.Nature, ToolTypeDefinitions.HerbalismKitType.Name, ToolTypeDefinitions.ArtisanToolSmithToolsType.Name) },
        { BackgroundMilitiaName, (SkillDefinitions.Athletics, SkillDefinitions.Perception, ToolTypeDefinitions.ArtisanToolSmithToolsType.Name, "GamingSetDiceType") },
        { BackgroundTroublemakerName, (SkillDefinitions.Deception, SkillDefinitions.SleightOfHand, "GamingSetDiceType", ToolTypeDefinitions.ThievesToolsType.Name) }
    };

    private static readonly HashSet<string> OriginRestrictedFeatNames =
    [
        "FeatAlert",
        "FeatHealer",
        "FeatLucky",
        "FeatMagicInitiateBard",
        "FeatMagicInitiateCleric",
        "FeatMagicInitiateDruid",
        "FeatMagicInitiateSorcerer",
        "FeatMagicInitiateWarlock",
        "FeatMagicInitiateWizard",
        "FeatSavageAttack",
        "FeatTough"
    ];

    private static readonly string[] HumanOriginFeatSelectionNames =
    [
        "FeatAlert",
        "FeatHealer",
        "FeatLucky",
        "FeatMagicInitiateBard",
        "FeatMagicInitiateCleric",
        "FeatMagicInitiateDruid",
        "FeatMagicInitiateSorcerer",
        "FeatMagicInitiateWarlock",
        "FeatMagicInitiateWizard",
        "FeatSavageAttack",
        "FeatTough",
        FeatSkilledName
    ];

    private static readonly string[] HumanOriginTrainableFeatNames =
    [
        "FeatAlert",
        "FeatHealer",
        "FeatLucky",
        "FeatMagicInitiateBard",
        "FeatMagicInitiateCleric",
        "FeatMagicInitiateDruid",
        "FeatMagicInitiateSorcerer",
        "FeatMagicInitiateWarlock",
        "FeatMagicInitiateWizard",
        "FeatSavageAttack",
        "FeatTough"
    ];

    private static readonly HashSet<string> HumanOriginFeatChoiceNames = [.. HumanOriginFeatSelectionNames];

    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> BackgroundAsiFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusGrantFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusDisplayFeatures = new();
    private static readonly Dictionary<string, (FeatureDefinition Skills, FeatureDefinition Tool)> BackgroundProficiencyFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundStoryCompatibilityFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition[]> BackgroundOriginalFeatures = new();
    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> HumanOriginFeatChoiceFeatures = new();
    private static readonly Dictionary<string, string> HumanOriginFeatSelections = new();
    private static FeatureDefinitionFeatureSet HumanOriginFeatFeatureSet;
    private static FeatureDefinition SkilledDisplayFeature;
    private static FeatureDefinitionPointPool HumanOriginFeatPointPool;
    private static FeatureDefinitionPointPool HumanOriginFeatSkilledSkillPointPool;
    private static bool _backgroundOptionsLoaded;

    internal static bool IsAlternateHumanEffectivelyEnabled()
    {
        return Main.Settings.EnableAlternateHuman && !Main.Settings.EnableBackgroundASI;
    }

    internal static bool IsBackgroundBonusFeatsEnabled()
    {
        return Main.Settings.EnableBackgroundASI && Main.Settings.EnableBackgroundBonusFeats;
    }

    internal static void ApplyBackgroundOptions()
    {
        if (NormalizeBackgroundOptionSettings())
        {
            FlexibleBackgroundsContext.SwitchFlexibleBackgrounds();
        }

        RefreshModeAwareOriginFeatDefinitions();
        FeatsContext.SwitchFirstLevelTotalFeats();

        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        SwitchBackgroundASI();
        SwitchBackgroundBonusFeats();
        FeatsContext.ClearFeatSubPanel2024UiState();

        if (Main.Settings.EnableTabletopFeatRules2024)
        {
            RefreshManagedTabletopContainerMappings();
        }
        else
        {
            ClearManagedTabletopContainerMappingsForInactiveMode();
            FeatsContext.RefreshFeatVisibilityFromSettings();
            GuiWrapperContext.RecacheFeats();
        }
    }

    internal static void Load2024BackgroundsASIAndFeats()
    {
        if (_backgroundOptionsLoaded)
        {
            return;
        }

        _backgroundOptionsLoaded = true;

        foreach (var backgroundAbilitySet in BackgroundAbilitySets)
        {
            var backgroundName = backgroundAbilitySet.Key;
            var attributes = backgroundAbilitySet.Value;

            BackgroundAsiFeatures[backgroundName] =
                BuildBackgroundAsiFeatureSet(backgroundName, attributes.A, attributes.B, attributes.C);
        }

        foreach (var featName in BackgroundFeatSets.Values.Distinct())
        {
            BackgroundBonusGrantFeatures[featName] = featName == FeatSkilledName
                ? BuildSkilledPointPool()
                : BuildOriginFeatPointPool(featName);

            BackgroundBonusDisplayFeatures[featName] = BuildOriginFeatDisplayFeature(featName);
        }

        foreach (var backgroundProficiencySet in BackgroundProficiencySets)
        {
            var backgroundName = backgroundProficiencySet.Key;
            var proficiencies = backgroundProficiencySet.Value;

            BackgroundProficiencyFeatures[backgroundName] = (
                BuildBackground2024SkillProficiency(backgroundName, proficiencies.SkillA, proficiencies.SkillB),
                BuildBackground2024ToolProficiency(backgroundName, proficiencies.ToolA, proficiencies.ToolB));
        }

        if (TryGetDefinition<FeatureDefinition>("ProficiencySpyLanguage", out var spyLanguage))
        {
            BackgroundStoryCompatibilityFeatures[BackgroundSpyName] = spyLanguage;
        }

        HumanOriginFeatPointPool = BuildHumanOriginFeatPointPool();
        HumanOriginFeatSkilledSkillPointPool = EnsureHumanOriginSkilledPointPool();
        HumanOriginFeatFeatureSet = BuildHumanOriginFeatFeatureSet();
        RefreshModeAwareOriginFeatDefinitions();
    }

    internal static void RefreshModeAwareOriginFeatDefinitions()
    {
        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        foreach (var featName in BackgroundFeatSets.Values.Distinct())
        {
            if (featName != FeatSkilledName &&
                BackgroundBonusGrantFeatures.TryGetValue(featName, out var grantedFeature) &&
                grantedFeature is FeatureDefinitionPointPool pointPool)
            {
                pointPool.RestrictedChoices.Clear();

                if (TryResolveModeAwareFeatDefinition(featName, out var featDefinition))
                {
                    pointPool.RestrictedChoices.Add(featDefinition.Name);
                }
                else
                {
                    pointPool.RestrictedChoices.Add(featName);
                }
            }

            if (featName != FeatSkilledName &&
                BackgroundBonusDisplayFeatures.TryGetValue(featName, out var displayFeature) &&
                TryResolveModeAwareFeatDefinition(featName, out var displayFeatDefinition))
            {
                displayFeature.GuiPresentation.title = displayFeatDefinition.GuiPresentation.title;
                displayFeature.GuiPresentation.description = "Feature/&BackgroundBonusFeatShortDescription";
                displayFeature.GuiPresentation.spriteReference = displayFeatDefinition.GuiPresentation.spriteReference;
            }
        }

        if (HumanOriginFeatPointPool)
        {
            HumanOriginFeatPointPool.RestrictedChoices.Clear();

            foreach (var featName in HumanOriginTrainableFeatNames)
            {
                var featDefinition = BuildHumanOriginFeatChoice(featName);

                if (featDefinition &&
                    !HumanOriginFeatPointPool.RestrictedChoices.Contains(featDefinition.Name))
                {
                    HumanOriginFeatPointPool.RestrictedChoices.Add(featDefinition.Name);
                }
            }
        }

        foreach (var featName in HumanOriginFeatSelectionNames)
        {
            if (!HumanOriginFeatChoiceFeatures.TryGetValue(featName, out var choiceFeature))
            {
                continue;
            }

            var featDefinition = BuildHumanOriginFeatSelectionFeature(featName);

            if (!featDefinition)
            {
                continue;
            }

            choiceFeature.GuiPresentation.title = featDefinition.GuiPresentation.title;
            choiceFeature.GuiPresentation.description = featDefinition.GuiPresentation.description;
            choiceFeature.GuiPresentation.spriteReference = featDefinition.GuiPresentation.spriteReference;
        }
    }

    internal static HashSet<string> GetActiveOriginRestrictedFeatNames(RulesetCharacterHero hero)
    {
        HashSet<string> result = [];

        if (!_backgroundOptionsLoaded ||
            !IsBackgroundBonusFeatsEnabled() ||
            hero?.GetHeroBuildingData() is not { } heroBuildingData)
        {
            return result;
        }

        if (!heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack))
        {
            return result;
        }

        var service = ServiceRepository.GetService<ICharacterBuildingService>();

        if (service == null)
        {
            return result;
        }

        foreach (var tag in pointPoolStack.ActivePools.Keys)
        {
            var pointPool = service.GetPointPoolOfTypeAndTag(heroBuildingData, HeroDefinitions.PointsPoolType.Feat, tag);

            var restrictedChoices = GetModeAwareRestrictedChoiceNames(pointPool);

            if (restrictedChoices.Count == 0)
            {
                continue;
            }

            if (IsHumanOriginFeatTag(tag))
            {
                continue;
            }

            foreach (var featName in restrictedChoices
                         .Select(GetCanonicalTabletopFeatName)
                         .Where(OriginRestrictedFeatNames.Contains))
            {
                result.Add(featName);
            }
        }

        return result;
    }

    internal static bool TryGetSingleOriginRestrictedFeat(
        CharacterHeroBuildingData heroBuildingData,
        string tag,
        out string featName)
    {
        featName = null;

        if (!_backgroundOptionsLoaded ||
            !IsBackgroundBonusFeatsEnabled() ||
            heroBuildingData == null ||
            string.IsNullOrEmpty(tag) ||
            IsHumanOriginFeatTag(tag))
        {
            return false;
        }

        var service = ServiceRepository.GetService<ICharacterBuildingService>();
        var pointPool = service?.GetPointPoolOfTypeAndTag(heroBuildingData, HeroDefinitions.PointsPoolType.Feat, tag);
        var restrictedChoices = GetModeAwareRestrictedChoiceNames(pointPool)
            .Select(GetCanonicalTabletopFeatName)
            .ToHashSet();

        if (restrictedChoices.Count == 0)
        {
            return false;
        }

        if (heroBuildingData.HeroCharacter?.BackgroundDefinition == null ||
            !BackgroundFeatSets.TryGetValue(heroBuildingData.HeroCharacter.BackgroundDefinition.Name, out var backgroundFeatName) ||
            backgroundFeatName == FeatSkilledName ||
            !OriginRestrictedFeatNames.Contains(backgroundFeatName))
        {
            return false;
        }

        if (!restrictedChoices.Contains(backgroundFeatName))
        {
            return false;
        }

        featName = backgroundFeatName;

        return true;
    }

    internal static bool TryGetSingleOriginRestrictedFeatDefinition(
        CharacterHeroBuildingData heroBuildingData,
        string tag,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        return TryGetSingleOriginRestrictedFeat(heroBuildingData, tag, out var featName) &&
               TryResolveModeAwareFeatDefinition(featName, out featDefinition);
    }

    internal static bool TryGetBackgroundBonusFeatForDisplay(
        RulesetCharacterHero hero,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        return hero?.BackgroundDefinition != null &&
               BackgroundFeatSets.TryGetValue(hero.BackgroundDefinition.Name, out var featName) &&
               featName != FeatSkilledName &&
               FeatsContext.TryResolveDisplayFeatDefinition(featName, out featDefinition);
    }

    internal static bool TryGetBackgroundBonusFeatForDisplay(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        return _backgroundOptionsLoaded &&
               Main.Settings.EnableBackgroundASI &&
               IsBackgroundBonusFeatsEnabled() &&
               (hero?.BackgroundDefinition != null || buildingData?.HeroCharacter?.BackgroundDefinition != null) &&
               BackgroundFeatSets.TryGetValue(
                   (hero?.BackgroundDefinition ?? buildingData?.HeroCharacter?.BackgroundDefinition).Name,
                   out var featName) &&
               featName != FeatSkilledName &&
               TryResolveModeAwareFeatDefinition(featName, out featDefinition);
    }

    internal static bool TryGetHumanOriginFeatForDisplay(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out FeatDefinition featDefinition)
    {
        return TryGetHumanOriginFeatForCharacterBuildingDisplay(hero, buildingData, out featDefinition);
    }

    internal static bool TryGetHumanOriginFeatForCharacterBuildingDisplay(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        var displayHero = hero ?? buildingData?.HeroCharacter;

        if (!_backgroundOptionsLoaded ||
            !Main.Settings.EnableBackgroundASI ||
            displayHero?.RaceDefinition?.Name != "Human" ||
            !TryGetHumanOriginFeatNameForBuildingDisplay(displayHero, buildingData, out var featName) ||
            featName == FeatSkilledName)
        {
            return false;
        }

        return TryResolveModeAwareFeatDefinition(featName, out featDefinition);
    }

    internal static bool TryGetHumanOriginFeatForFinalizeSnapshot(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        var displayHero = hero ?? buildingData?.HeroCharacter;

        if (!_backgroundOptionsLoaded ||
            !Main.Settings.EnableBackgroundASI ||
            displayHero?.RaceDefinition?.Name != "Human" ||
            !TryGetHumanOriginFeatNameForFinalizeSnapshot(displayHero, buildingData, out var featName) ||
            featName == FeatSkilledName)
        {
            return false;
        }

        return TryResolveModeAwareFeatDefinition(featName, out featDefinition);
    }

    internal static bool TryGetHumanOriginInspectionDisplayFeature(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        FeatureDefinition sourceFeature,
        out BaseDefinition displayFeature,
        out string fallbackTitle)
    {
        displayFeature = null;
        fallbackTitle = null;

        if (!IsHumanOriginInspectionSourceFeature(sourceFeature) ||
            !_backgroundOptionsLoaded ||
            !Main.Settings.EnableBackgroundASI ||
            hero?.RaceDefinition?.Name != "Human")
        {
            return false;
        }

        var candidateFeatNames = new HashSet<string>();
        var backgroundFeatName = hero.BackgroundDefinition != null &&
                                 BackgroundFeatSets.TryGetValue(hero.BackgroundDefinition.Name, out var configuredBackgroundFeatName)
            ? GetCanonicalTabletopFeatName(configuredBackgroundFeatName)
            : null;

        if (TryGetHumanOriginFeatNameFromBuildingOrSelection(hero, buildingData, true, out var selectedFeatName))
        {
            TryAddHumanOriginInspectionCandidate(candidateFeatNames, selectedFeatName, backgroundFeatName, true);
        }

        foreach (var trainedFeat in hero.TrainedFeats ?? [])
        {
            TryAddHumanOriginInspectionCandidate(candidateFeatNames, trainedFeat?.Name, backgroundFeatName, true);
        }

        foreach (var featName in hero.FeatProficiencies ?? [])
        {
            TryAddHumanOriginInspectionCandidate(candidateFeatNames, featName, backgroundFeatName, true);
        }

        foreach (var trainedFeat in buildingData?.LevelupTrainedFeats?.Values
                     .Where(feats => feats != null)
                     .SelectMany(feats => feats) ?? [])
        {
            TryAddHumanOriginInspectionCandidate(candidateFeatNames, trainedFeat?.Name, backgroundFeatName, true);
        }

        if (candidateFeatNames.Count == 1)
        {
            var candidateFeatName = candidateFeatNames.First();

            if (candidateFeatName == FeatSkilledName)
            {
                displayFeature = EnsureSkilledDisplayFeature();

                return true;
            }

            if (TryResolveModeAwareFeatDefinition(candidateFeatName, out var featDefinition))
            {
                displayFeature = featDefinition;

                return true;
            }
        }

        fallbackTitle = $"{hero.RaceDefinition.FormatTitle()}: {Gui.Localize("Feature/&PointPoolHumanOriginFeatTitle")}";

        return true;
    }

    internal static bool TryGetHumanOriginFeatForExistingHeroMarker(
        RulesetCharacterHero hero,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        if (hero?.RaceDefinition?.Name != "Human")
        {
            return false;
        }

        var candidateFeatNames = new HashSet<string>();

        CollectHumanOriginMarkerFeatNames(hero.FeaturesOrigin?.Keys, candidateFeatNames);

        if (candidateFeatNames.Count == 0)
        {
            CollectHumanOriginMarkerFeatNames(hero.FeaturesToBrowse, candidateFeatNames);
        }

        return candidateFeatNames.Count == 1 &&
               FeatsContext.TryResolveDisplayFeatDefinition(candidateFeatNames.First(), out featDefinition);
    }

    internal static void SwitchBackgroundASI()
    {
        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        var enableBackgroundAsi = Main.Settings.EnableBackgroundASI;

        DisableAlternateHumanWhenBackgroundAsiEnabled();

        foreach (var backgroundAsiFeature in BackgroundAsiFeatures)
        {
            var backgroundName = backgroundAsiFeature.Key;
            var featureSet = backgroundAsiFeature.Value;

            if (!TryGetDefinition<CharacterBackgroundDefinition>(backgroundName, out var background))
            {
                continue;
            }

            background.Features.RemoveAll(feature => feature == featureSet);

            if (enableBackgroundAsi)
            {
                background.Features.Insert(0, featureSet);
            }
        }

        var raceDatabase = DatabaseRepository.GetDatabase<CharacterRaceDefinition>();

        foreach (var removedFeatureNames in FlexibleRacesContext.RemovedFeatureNames)
        {
            var raceName = removedFeatureNames.Key;
            var featureNames = removedFeatureNames.Value;
            var raceDefinition = raceDatabase.GetElement(raceName, true);

            if (!raceDefinition)
            {
                continue;
            }

            foreach (var featureName in featureNames)
            {
                if (!TryGetDefinition<FeatureDefinition>(featureName, out var featureDefinition))
                {
                    continue;
                }

                var hasFeature = raceDefinition.FeatureUnlocks.Exists(unlock => unlock.FeatureDefinition == featureDefinition);

                if (enableBackgroundAsi)
                {
                    if (hasFeature)
                    {
                        RemoveMatchingFeature(raceDefinition.FeatureUnlocks, featureDefinition);
                    }
                }
                else if (!Main.Settings.EnableFlexibleRaces && !hasFeature)
                {
                    raceDefinition.FeatureUnlocks.Add(new FeatureUnlockByLevel(featureDefinition, 1));
                }
            }

            raceDefinition.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }

        if (!TryGetDefinition<CharacterRaceDefinition>("Human", out var human))
        {
            return;
        }

        RemoveMatchingFeature(human.FeatureUnlocks, FeatureDefinitionAttributeModifiers.AttributeModifierHumanAbilityScoreIncrease);
        RemoveMatchingFeature(human.FeatureUnlocks, FeatureDefinitionPointPools.PointPoolAbilityScoreImprovement);
        RemoveMatchingFeature(human.FeatureUnlocks, HumanOriginFeatFeatureSet);
        RemoveMatchingFeature(human.FeatureUnlocks, HumanOriginFeatPointPool);
        RemoveLegacyHumanOriginFeatFeatures(human.FeatureUnlocks);

        if (enableBackgroundAsi)
        {
            RemoveAlternateHumanBonusFeatPool(human.FeatureUnlocks);
            AddFeatureUnlock(human.FeatureUnlocks, PointPoolHumanSkillPool);
            AddFeatureUnlock(human.FeatureUnlocks, HumanOriginFeatFeatureSet);
            AddFeatureUnlock(human.FeatureUnlocks, HumanOriginFeatPointPool);
        }
        else
        {
            HumanOriginFeatSelections.Clear();

            if (!Main.Settings.EnableAlternateHuman)
            {
                RemoveMatchingFeature(human.FeatureUnlocks, PointPoolHumanSkillPool);
            }

            FeatureDefinition humanAsiFeature = IsAlternateHumanEffectivelyEnabled()
                ? FeatureDefinitionPointPools.PointPoolAbilityScoreImprovement
                : FeatureDefinitionAttributeModifiers.AttributeModifierHumanAbilityScoreIncrease;

            AddFeatureUnlock(human.FeatureUnlocks, humanAsiFeature);
        }

        human.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static bool HasDuplicateHumanOriginFeat(RulesetCharacterHero hero, out string featName)
    {
        featName = null;

        if (!_backgroundOptionsLoaded ||
            !IsBackgroundBonusFeatsEnabled() ||
            hero?.RaceDefinition?.Name != "Human" ||
            hero.BackgroundDefinition == null ||
            !BackgroundFeatSets.TryGetValue(hero.BackgroundDefinition.Name, out var backgroundFeatName))
        {
            return false;
        }

        if (!TryGetHumanOriginSelectionName(hero, out var humanFeatName) &&
            !TryGetHumanOriginTrainedFeatName(hero, out humanFeatName))
        {
            return false;
        }

        if (!AreEquivalentTabletopFeatNames(humanFeatName, backgroundFeatName))
        {
            return false;
        }

        featName = GetCanonicalTabletopFeatName(backgroundFeatName);

        return true;
    }

    internal static bool IsDuplicateHumanOriginFeatChoice(
        RulesetCharacterHero hero,
        string tag,
        string featName)
    {
        return _backgroundOptionsLoaded &&
               IsBackgroundBonusFeatsEnabled() &&
               IsHumanOriginFeatTag(tag) &&
               hero?.RaceDefinition?.Name == "Human" &&
               hero.BackgroundDefinition != null &&
               BackgroundFeatSets.TryGetValue(hero.BackgroundDefinition.Name, out var backgroundFeatName) &&
               AreEquivalentTabletopFeatNames(featName, backgroundFeatName);
    }

    internal static bool IsHumanOriginFeatSelectionFeature(FeatureDefinition feature)
    {
        return _backgroundOptionsLoaded && Main.Settings.EnableBackgroundASI && feature == HumanOriginFeatFeatureSet;
    }

    internal static bool TryGetHumanOriginFeatLearnStepTitle(
        HeroDefinitions.PointsPoolType poolType,
        string tag,
        out string title)
    {
        title = null;

        if (!_backgroundOptionsLoaded ||
            !Main.Settings.EnableBackgroundASI ||
            poolType != HeroDefinitions.PointsPoolType.Feat ||
            !TryGetHumanOriginFeatTagTitle(tag, out title))
        {
            return false;
        }

        return true;
    }

    internal static bool TryGetHumanOriginFeatTagTitle(string tag, out string title)
    {
        title = null;

        if (!IsHumanOriginFeatTag(tag))
        {
            return false;
        }

        title = Gui.Localize("Feature/&PointPoolHumanOriginFeatTitle");

        return true;
    }

    internal static bool TrySaveHumanOriginFeatSelection(
        RulesetCharacterHero hero,
        FeatureDefinition choiceFeature,
        bool clearTraining)
    {
        if (!IsValidHumanOriginFeatSelectionHero(hero) ||
            !TryGetHumanOriginFeatName(choiceFeature, out var featName))
        {
            return false;
        }

        var key = GetHumanOriginSelectionKey(hero);
        var changed = !HumanOriginFeatSelections.TryGetValue(key, out var currentFeatName) ||
                      currentFeatName != featName;

        HumanOriginFeatSelections[key] = featName;

        if (clearTraining && changed)
        {
            ClearHumanOriginFeatTraining(hero.GetHeroBuildingData());
            SyncHumanOriginFeatPools(hero.GetHeroBuildingData());
        }

        return true;
    }

    internal static bool TryGetHumanOriginSelectionFeature(RulesetCharacterHero hero, out FeatureDefinition feature)
    {
        feature = null;

        if (!TryGetHumanOriginSelectionName(hero, out var featName) ||
            !HumanOriginFeatChoiceFeatures.TryGetValue(featName, out var choiceFeature))
        {
            return false;
        }

        feature = choiceFeature;

        return true;
    }

    internal static bool TryGetHumanOriginFeatToTrain(
        RulesetCharacterHero hero,
        string tag,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        if (!IsValidHumanOriginFeatSelectionHero(hero) ||
            !IsHumanOriginFeatTag(tag) ||
            !TryGetHumanOriginSelectionName(hero, out var featName) ||
            featName == FeatSkilledName ||
            !TryResolveModeAwareFeatDefinition(featName, out featDefinition))
        {
            return false;
        }

        return true;
    }

    internal static bool TryApplyHumanOriginFeatPointPool(
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinition feature)
    {
        if (feature != HumanOriginFeatPointPool)
        {
            return false;
        }

        if (heroBuildingData == null)
        {
            return true;
        }

        SyncHumanOriginFeatPools(heroBuildingData);

        return true;
    }

    internal static void RemoveHumanOriginFeatPointPool(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return;
        }

        ClearHumanOriginFeatTraining(heroBuildingData);
        ClearHumanOriginSkilledSkillTraining(heroBuildingData);
        RemoveHumanOriginFeatActivePool(heroBuildingData);
        RemoveHumanOriginSkilledSkillActivePool(heroBuildingData);
        RemoveStaleHumanOriginSkilledPools(heroBuildingData);
    }

    internal static void ClearHumanOriginFeatTraining(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return;
        }

        ServiceRepository.GetService<ICharacterBuildingService>()?.UntrainFeats(heroBuildingData, HumanOriginFeatTag);
        heroBuildingData.LevelupTrainedFeats.Remove(HumanOriginFeatTag);
    }

    internal static void EnsureHumanOriginFeatStateMatchesRace(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return;
        }

        if (ShouldUseHumanOriginFeat(heroBuildingData))
        {
            SyncHumanOriginFeatPools(heroBuildingData);

            return;
        }

        RemoveHumanOriginFeatPointPool(heroBuildingData);
    }

    private static bool TryGetHumanOriginTrainedFeatName(RulesetCharacterHero hero, out string featName)
    {
        featName = null;

        if (hero?.GetHeroBuildingData() is not { } heroBuildingData ||
            !heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack) ||
            !pointPoolStack.ActivePools.ContainsKey(HumanOriginFeatTag) ||
            !heroBuildingData.LevelupTrainedFeats.TryGetValue(HumanOriginFeatTag, out var trainedFeats))
        {
            return false;
        }

        var trainedFeat = trainedFeats.FirstOrDefault(feat =>
            feat && HumanOriginFeatChoiceNames.Contains(GetCanonicalTabletopFeatName(feat.Name)));

        if (!trainedFeat)
        {
            return false;
        }

        featName = GetCanonicalTabletopFeatName(trainedFeat.Name);

        return true;
    }

    private static bool IsHumanOriginFeatChoiceName(string featName, bool includeSkilled)
    {
        var canonicalFeatName = GetCanonicalTabletopFeatName(featName);

        return !string.IsNullOrEmpty(canonicalFeatName) &&
               HumanOriginFeatChoiceNames.Contains(canonicalFeatName) &&
               (includeSkilled || canonicalFeatName != FeatSkilledName);
    }

    private static bool TryGetHumanOriginFeatNameFromBuildingOrSelection(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        bool includeSkilled,
        out string featName)
    {
        featName = null;

        if (buildingData?.LevelupTrainedFeats.TryGetValue(HumanOriginFeatTag, out var trainedFeats) == true)
        {
            var trainedFeatName = trainedFeats?
                .Where(feat => feat != null)
                .Select(feat => GetCanonicalTabletopFeatName(feat.Name))
                .FirstOrDefault(name => IsHumanOriginFeatChoiceName(name, includeSkilled));

            if (!string.IsNullOrEmpty(trainedFeatName))
            {
                featName = trainedFeatName;

                return true;
            }
        }

        if (hero == null ||
            !HumanOriginFeatSelections.TryGetValue(GetHumanOriginSelectionKey(hero), out var selectedFeatName) ||
            !IsHumanOriginFeatChoiceName(selectedFeatName, includeSkilled))
        {
            return false;
        }

        featName = GetCanonicalTabletopFeatName(selectedFeatName);

        return true;
    }

    private static bool TryGetHumanOriginFeatNameForBuildingDisplay(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out string featName)
    {
        return TryGetHumanOriginFeatNameFromBuildingOrSelection(hero, buildingData, false, out featName);
    }

    private static bool TryGetHumanOriginFeatNameForFinalizeSnapshot(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out string featName)
    {
        featName = null;

        if (hero?.RaceDefinition?.Name != "Human")
        {
            return false;
        }

        return TryGetHumanOriginFeatNameFromBuildingOrSelection(hero, buildingData, false, out featName);
    }

    internal static bool IsHumanOriginFeatTag(string tag)
    {
        return tag == HumanOriginFeatTag;
    }

    internal static bool IsHumanOriginSkilledSkillTag(string tag)
    {
        return tag == HumanOriginFeatSkilledSkillTag;
    }

    internal static bool ShouldUseHumanOriginFeat(CharacterHeroBuildingData heroBuildingData)
    {
        return _backgroundOptionsLoaded &&
               Main.Settings.EnableBackgroundASI &&
               heroBuildingData?.HeroCharacter?.RaceDefinition?.Name == "Human" &&
               !LevelUpHelper.IsLevelingUp(heroBuildingData.HeroCharacter);
    }

    internal static bool IsHumanOriginSkilledSelected(RulesetCharacterHero hero)
    {
        return TryGetHumanOriginSelectionName(hero, out var featName) && featName == FeatSkilledName;
    }

    internal static bool SyncHumanOriginFeatPools(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return false;
        }

        var changed = RemoveStaleHumanOriginSkilledPools(heroBuildingData);

        if (!ShouldUseHumanOriginFeat(heroBuildingData))
        {
            ClearPendingFeatSelection(heroBuildingData.HeroCharacter, HumanOriginFeatTag);
            ClearHumanOriginFeatTraining(heroBuildingData);
            ClearHumanOriginSkilledSkillTraining(heroBuildingData);
            changed |= RemoveHumanOriginFeatActivePool(heroBuildingData);
            changed |= RemoveHumanOriginSkilledSkillActivePool(heroBuildingData);

            return changed;
        }

        if (IsHumanOriginSkilledSelected(heroBuildingData.HeroCharacter))
        {
            ClearPendingFeatSelection(heroBuildingData.HeroCharacter, HumanOriginFeatTag);
            ClearHumanOriginFeatTraining(heroBuildingData);
            changed |= RemoveHumanOriginFeatActivePool(heroBuildingData);
            changed |= EnsureHumanOriginSkilledSkillActivePool(heroBuildingData);

            return changed;
        }

        ClearHumanOriginSkilledSkillTraining(heroBuildingData);
        changed |= RemoveHumanOriginSkilledSkillActivePool(heroBuildingData);
        changed |= EnsureHumanOriginFeatActivePool(heroBuildingData);

        return changed;
    }

    private static bool TryGetHumanOriginSelectionName(RulesetCharacterHero hero, out string featName)
    {
        featName = null;

        return IsValidHumanOriginFeatSelectionHero(hero) &&
               HumanOriginFeatSelections.TryGetValue(GetHumanOriginSelectionKey(hero), out featName) &&
               HumanOriginFeatChoiceNames.Contains(featName);
    }

    private static bool TryGetHumanOriginFeatName(FeatureDefinition choiceFeature, out string featName)
    {
        featName = null;

        if (!choiceFeature)
        {
            return false;
        }

        foreach (var humanOriginFeatChoiceFeature in HumanOriginFeatChoiceFeatures)
        {
            if (humanOriginFeatChoiceFeature.Value == choiceFeature)
            {
                featName = humanOriginFeatChoiceFeature.Key;

                return true;
            }
        }

        return false;
    }

    private static bool TryGetHumanOriginFeatNameFromMarker(string featureSetName, out string featName)
    {
        featName = null;

        if (string.IsNullOrEmpty(featureSetName) ||
            !featureSetName.StartsWith(HumanOriginFeatChoiceFeatureSetPrefix, System.StringComparison.Ordinal))
        {
            return false;
        }

        var candidateFeatName = featureSetName.Substring(HumanOriginFeatChoiceFeatureSetPrefix.Length);

        if (!HumanOriginFeatChoiceNames.Contains(candidateFeatName))
        {
            return false;
        }

        featName = candidateFeatName;

        return true;
    }

    private static void CollectHumanOriginMarkerFeatNames(
        IEnumerable<FeatureDefinition> features,
        HashSet<string> featNames)
    {
        if (features == null || featNames == null)
        {
            return;
        }

        foreach (var featureSet in features.OfType<FeatureDefinitionFeatureSet>())
        {
            if (!featureSet.Name.StartsWith(HumanOriginFeatChoiceFeatureSetPrefix, System.StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryGetHumanOriginFeatName(featureSet, out var featName) &&
                !TryGetHumanOriginFeatNameFromMarker(featureSet.Name, out featName))
            {
                continue;
            }

            if (featName != FeatSkilledName)
            {
                featNames.Add(featName);
            }
        }
    }

    private static bool IsValidHumanOriginFeatSelectionHero(RulesetCharacterHero hero)
    {
        return _backgroundOptionsLoaded &&
               Main.Settings.EnableBackgroundASI &&
               hero?.RaceDefinition?.Name == "Human" &&
               !LevelUpHelper.IsLevelingUp(hero);
    }

    private static string GetHumanOriginSelectionKey(RulesetCharacterHero hero)
    {
        return hero.Guid.ToString();
    }

    private static bool EnsureHumanOriginFeatActivePool(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null ||
            HumanOriginFeatPointPool == null ||
            !heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack) ||
            pointPoolStack.ActivePools.ContainsKey(HumanOriginFeatTag))
        {
            return false;
        }

        var pool = new PointPool(
            HumanOriginFeatPointPool.poolAmount,
            HumanOriginFeatPointPool.RestrictedChoices,
            HumanOriginFeatPointPool.UniqueChoices)
        {
            Description = HumanOriginFeatPointPool.GuiPresentation.Description
        };

        NormalizeModeAwareFeatPointPool(pool);
        pointPoolStack.ActivePools.Add(HumanOriginFeatTag, pool);

        return true;
    }

    private static bool RemoveHumanOriginFeatActivePool(CharacterHeroBuildingData heroBuildingData)
    {
        return heroBuildingData?.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack) ==
               true &&
               pointPoolStack.ActivePools.Remove(HumanOriginFeatTag);
    }

    private static bool EnsureHumanOriginSkilledSkillActivePool(CharacterHeroBuildingData heroBuildingData)
    {
        var pointPool = EnsureHumanOriginSkilledPointPool();

        if (heroBuildingData == null ||
            pointPool == null ||
            !heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Skill, out var pointPoolStack) ||
            pointPoolStack.ActivePools.ContainsKey(HumanOriginFeatSkilledSkillTag))
        {
            return false;
        }

        pointPoolStack.ActivePools.Add(
            HumanOriginFeatSkilledSkillTag,
            new PointPool(pointPool.poolAmount, pointPool.RestrictedChoices, pointPool.UniqueChoices)
            {
                Description = pointPool.GuiPresentation.Description
            });

        return true;
    }

    private static bool RemoveHumanOriginSkilledSkillActivePool(CharacterHeroBuildingData heroBuildingData)
    {
        return heroBuildingData?.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Skill, out var pointPoolStack) ==
               true &&
               pointPoolStack.ActivePools.Remove(HumanOriginFeatSkilledSkillTag);
    }

    private static void ClearHumanOriginSkilledSkillTraining(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return;
        }

        ServiceRepository.GetService<ICharacterBuildingService>()?.UntrainSkills(
            heroBuildingData,
            HumanOriginFeatSkilledSkillTag);
        heroBuildingData.LevelupTrainedSkills.Remove(HumanOriginFeatSkilledSkillTag);
    }

    private static bool RemoveStaleHumanOriginSkilledPools(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return false;
        }

        var changed = false;
        var buildingService = ServiceRepository.GetService<ICharacterBuildingService>();
        const string stalePrefix = "FeatGrantedPointPool:FeatSkilled";

        foreach (var pointPoolStack in heroBuildingData.PointPoolStacks.Values)
        {
            foreach (var staleTag in pointPoolStack.ActivePools.Keys
                         .Where(tag => tag.StartsWith(stalePrefix, System.StringComparison.Ordinal))
                         .ToArray())
            {
                pointPoolStack.ActivePools.Remove(staleTag);
                changed = true;
            }
        }

        foreach (var staleTag in heroBuildingData.LevelupTrainedSkills.Keys
                     .Where(tag => tag.StartsWith(stalePrefix, System.StringComparison.Ordinal))
                     .ToArray())
        {
            buildingService?.UntrainSkills(heroBuildingData, staleTag);
            heroBuildingData.LevelupTrainedSkills.Remove(staleTag);
            changed = true;
        }

        return changed;
    }

    internal static void SwitchBackgroundBonusFeats()
    {
        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        var enableBackgroundBonusFeats = IsBackgroundBonusFeatsEnabled();

        foreach (var backgroundFeatSet in BackgroundFeatSets)
        {
            var backgroundName = backgroundFeatSet.Key;
            var featName = backgroundFeatSet.Value;

            if (!TryGetDefinition<CharacterBackgroundDefinition>(backgroundName, out var background) ||
                !BackgroundBonusGrantFeatures.TryGetValue(featName, out var grantedFeature))
            {
                continue;
            }

            BackgroundBonusDisplayFeatures.TryGetValue(featName, out var displayFeature);
            BackgroundProficiencyFeatures.TryGetValue(backgroundName, out var proficiencyFeatures);
            BackgroundStoryCompatibilityFeatures.TryGetValue(backgroundName, out var storyCompatibilityFeature);

            CaptureOriginalBackgroundFeatures(backgroundName, background);
            RemoveManagedBackgroundBonusFeatures(
                background,
                grantedFeature,
                displayFeature,
                proficiencyFeatures,
                storyCompatibilityFeature);
            RestoreOriginalBackgroundFeatures(backgroundName, background);

            if (!enableBackgroundBonusFeats)
            {
                continue;
            }

            background.Features.RemoveAll(IsSuppressedBackground2024Feature);

            var insertIndex = GetBackgroundFeatureInsertIndex(backgroundName, background);

            background.Features.Insert(insertIndex, grantedFeature);

            if (displayFeature)
            {
                background.Features.Insert(insertIndex + 1, displayFeature);
            }

            if (proficiencyFeatures.Skills)
            {
                background.Features.Insert(insertIndex + (displayFeature ? 2 : 1), proficiencyFeatures.Skills);
            }

            if (proficiencyFeatures.Tool)
            {
                background.Features.Insert(insertIndex + (displayFeature ? 3 : 2), proficiencyFeatures.Tool);
            }

            if (storyCompatibilityFeature)
            {
                background.Features.Insert(insertIndex + (displayFeature ? 4 : 3), storyCompatibilityFeature);
            }
        }

        SwitchAddOriginFeatsToAutoLearn();
    }

    internal static void SwitchAddOriginFeatsToAutoLearn()
    {
        foreach (var characterClass in DatabaseRepository.GetDatabase<CharacterClassDefinition>())
        {
            foreach (var featName in characterClass.featAutolearnPreference
                         .Where(featName => OriginRestrictedFeatNames.Contains(GetCanonicalTabletopFeatName(featName)))
                         .ToArray())
            {
                characterClass.featAutolearnPreference.RemoveAll(name => name == featName);
            }
        }
    }

    private static FeatureDefinitionFeatureSet BuildBackgroundAsiFeatureSet(
        string backgroundName,
        string attrA,
        string attrB,
        string attrC)
    {
        var plusOneA = BuildAttributeModifier(backgroundName, attrA, 1);
        var plusOneB = BuildAttributeModifier(backgroundName, attrB, 1);
        var plusOneC = BuildAttributeModifier(backgroundName, attrC, 1);
        var plusTwoA = BuildAttributeModifier(backgroundName, attrA, 2);
        var plusTwoB = BuildAttributeModifier(backgroundName, attrB, 2);
        var plusTwoC = BuildAttributeModifier(backgroundName, attrC, 2);

        var plusOneSet = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSetBackgroundASI_{backgroundName}_PlusOneEach")
            .SetGuiPresentation(
                Gui.Format("Feature/&FeatureSetBackgroundASI111Title",
                    GetAbilityAbbreviation(attrA), GetAbilityAbbreviation(attrB), GetAbilityAbbreviation(attrC)),
                Gui.Format("Feature/&FeatureSetBackgroundASI111Description",
                    GetAbilityAbbreviation(attrA), GetAbilityAbbreviation(attrB), GetAbilityAbbreviation(attrC)))
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Union)
            .AddFeatureSet(plusOneA, plusOneB, plusOneC)
            .AddToDB();

        var plusTwoChoiceA = BuildBackgroundAsiChoiceSet(backgroundName, attrA, attrB, attrC, plusTwoA);
        var plusTwoChoiceB = BuildBackgroundAsiChoiceSet(backgroundName, attrB, attrA, attrC, plusTwoB);
        var plusTwoChoiceC = BuildBackgroundAsiChoiceSet(backgroundName, attrC, attrA, attrB, plusTwoC);

        return FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSetBackgroundASI_{backgroundName}")
            .SetGuiPresentation(
                Gui.Format("Feature/&FeatureSetBackgroundASITitle",
                    GetAbilityAbbreviation(attrA), GetAbilityAbbreviation(attrB), GetAbilityAbbreviation(attrC)),
                "Feature/&FeatureSetBackgroundASIDescription")
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
            .AddFeatureSet(plusTwoChoiceA, plusTwoChoiceB, plusTwoChoiceC, plusOneSet)
            .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildBackgroundAsiChoiceSet(
        string backgroundName,
        string plusTwoAttribute,
        string optionA,
        string optionB,
        FeatureDefinitionAttributeModifier plusTwoFeature)
    {
        var pointPool = FeatureDefinitionPointPoolBuilder
            .Create($"PointPoolBackgroundASI_{backgroundName}_{plusTwoAttribute}")
            .SetGuiPresentationNoContent(true)
            .SetPool(HeroDefinitions.PointsPoolType.AbilityScore, 1)
            .RestrictChoices(optionA, optionB)
            .AddToDB();

        return FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSetBackgroundASI_{backgroundName}_{plusTwoAttribute}")
            .SetGuiPresentation(
                Gui.Format("Feature/&FeatureSetBackgroundASI2Plus1Title",
                    GetAbilityAbbreviation(plusTwoAttribute), GetAbilityAbbreviation(optionA), GetAbilityAbbreviation(optionB)),
                Gui.Format("Feature/&FeatureSetBackgroundASI2Plus1Description",
                    GetAbilityAbbreviation(plusTwoAttribute), GetAbilityAbbreviation(optionA), GetAbilityAbbreviation(optionB)))
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Union)
            .AddFeatureSet(plusTwoFeature, pointPool)
            .AddToDB();
    }

    private static FeatureDefinitionAttributeModifier BuildAttributeModifier(string backgroundName, string attribute, int amount)
    {
        return FeatureDefinitionAttributeModifierBuilder
            .Create($"AttributeModifierBackgroundASI_{backgroundName}_{attribute}_{amount}")
            .SetGuiPresentationNoContent(true)
            .SetModifier(AttributeModifierOperation.Additive, attribute, amount)
            .AddToDB();
    }

    private static FeatureDefinition BuildOriginFeatPointPool(string featName)
    {
        return FeatureDefinitionPointPoolBuilder
            .Create($"PointPoolBackgroundFeat_{featName}")
            .SetGuiPresentationNoContent(true)
            .SetPool(HeroDefinitions.PointsPoolType.Feat, 1)
            .RestrictChoices(featName)
            .AddToDB();
    }

    private static FeatureDefinitionPointPool BuildHumanOriginFeatPointPool()
    {
        var choices = HumanOriginTrainableFeatNames
            .Select(BuildHumanOriginFeatChoice)
            .Where(feat => feat)
            .Select(feat => feat.Name)
            .ToArray();

        return choices.Length == 0
            ? null
            : FeatureDefinitionPointPoolBuilder
                .Create(HumanOriginFeatPointPoolName)
                .SetGuiPresentationNoContent(true)
                .SetPool(HeroDefinitions.PointsPoolType.Feat, 1)
                .RestrictChoices(choices)
                .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildHumanOriginFeatFeatureSet()
    {
        var choices = HumanOriginFeatSelectionNames
            .Select(BuildHumanOriginFeatChoiceFeatureSet)
            .Where(feature => feature)
            .ToArray();

        return choices.Length == 0
            ? null
            : FeatureDefinitionFeatureSetBuilder
                .Create(HumanOriginFeatFeatureSetName)
                .SetGuiPresentation(
                    "Feature/&PointPoolHumanOriginFeatTitle",
                    "Feature/&PointPoolHumanOriginFeatDescription")
                .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
                .AddFeatureSet(choices)
                .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildHumanOriginFeatChoiceFeatureSet(string featName)
    {
        var featDefinition = BuildHumanOriginFeatSelectionFeature(featName);

        if (!featDefinition)
        {
            return null;
        }

        var choiceFeature = FeatureDefinitionFeatureSetBuilder
            .Create($"{HumanOriginFeatChoiceFeatureSetPrefix}{featName}")
            .SetGuiPresentation(
                featDefinition.GuiPresentation.Title,
                featDefinition.GuiPresentation.Description,
                featDefinition)
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Union)
            .AddToDB();

        HumanOriginFeatChoiceFeatures[featName] = choiceFeature;

        return choiceFeature;
    }

    private static FeatDefinition BuildHumanOriginFeatChoice(string featName)
    {
        return TryResolveModeAwareFeatDefinition(featName, out var featDefinition)
            ? featDefinition
            : null;
    }

    private static BaseDefinition BuildHumanOriginFeatSelectionFeature(string featName)
    {
        return featName == FeatSkilledName
            ? EnsureSkilledDisplayFeature()
            : BuildHumanOriginFeatChoice(featName);
    }

    internal static FeatureDefinition EnsureSkilledDisplayFeature()
    {
        if (SkilledDisplayFeature)
        {
            return SkilledDisplayFeature;
        }

        SkilledDisplayFeature = FeatureDefinitionBuilder
            .Create("FeatureSkilledDisplay")
            .SetGuiPresentation("Feature/&PointPoolSkilledTitle", "Feature/&PointPoolSkilledDescription")
            .AddToDB();

        return SkilledDisplayFeature;
    }

    internal static FeatureDefinitionPointPool EnsureHumanOriginSkilledPointPool()
    {
        if (HumanOriginFeatSkilledSkillPointPool)
        {
            return HumanOriginFeatSkilledSkillPointPool;
        }

        HumanOriginFeatSkilledSkillPointPool = FeatureDefinitionPointPoolBuilder
            .Create(HumanOriginFeatSkilledPointPoolName)
            .SetGuiPresentation("Feature/&PointPoolSkilledTitle", "Feature/&PointPoolSkilledDescription")
            .SetPool(HeroDefinitions.PointsPoolType.Skill, 3)
            .AddToDB();

        return HumanOriginFeatSkilledSkillPointPool;
    }

    private static FeatureDefinition BuildOriginFeatDisplayFeature(string featName)
    {
        if (featName == FeatSkilledName)
        {
            var skilledDisplayFeature = EnsureSkilledDisplayFeature();

            return FeatureDefinitionBuilder
                .Create($"FeatureBackgroundFeatDisplay_{featName}")
                .SetGuiPresentation(
                    skilledDisplayFeature.GuiPresentation.Title,
                    "Feature/&BackgroundBonusFeatShortDescription",
                    skilledDisplayFeature)
                .AddToDB();
        }

        var featDefinition = TryResolveModeAwareFeatDefinition(featName, out var resolvedFeat)
            ? resolvedFeat
            : GetDefinition<FeatDefinition>(featName);

        return FeatureDefinitionBuilder
            .Create($"FeatureBackgroundFeatDisplay_{featName}")
            .SetGuiPresentation(
                featDefinition.GuiPresentation.Title,
                "Feature/&BackgroundBonusFeatShortDescription",
                featDefinition)
            .AddToDB();
    }

    private static FeatureDefinition BuildSkilledPointPool()
    {
        return FeatureDefinitionPointPoolBuilder
            .Create("PointPoolBackgroundFeatSkilled")
            .SetGuiPresentation("Feature/&PointPoolSkilledTitle", "Feature/&PointPoolSkilledDescription")
            .SetPool(HeroDefinitions.PointsPoolType.Skill, 3)
            .AddToDB();
    }

    private static bool IsHumanOriginInspectionSourceFeature(FeatureDefinition sourceFeature)
    {
        return sourceFeature != null &&
               (sourceFeature == HumanOriginFeatFeatureSet ||
                sourceFeature is FeatureDefinitionFeatureSet featureSet &&
                (HumanOriginFeatChoiceFeatures.Values.Contains(featureSet) ||
                 featureSet.Name.StartsWith(HumanOriginFeatChoiceFeatureSetPrefix, System.StringComparison.Ordinal)));
    }

    private static void TryAddHumanOriginInspectionCandidate(
        HashSet<string> featNames,
        string featName,
        string backgroundFeatName,
        bool includeSkilled)
    {
        var canonicalFeatName = GetCanonicalTabletopFeatName(featName);

        if (!IsHumanOriginFeatChoiceName(canonicalFeatName, includeSkilled) ||
            !string.IsNullOrEmpty(backgroundFeatName) &&
            AreEquivalentTabletopFeatNames(canonicalFeatName, backgroundFeatName))
        {
            return;
        }

        featNames.Add(canonicalFeatName);
    }

    private static FeatureDefinition BuildBackground2024SkillProficiency(
        string backgroundName,
        string skillA,
        string skillB)
    {
        return FeatureDefinitionProficiencyBuilder
            .Create($"ProficiencyBackground2024_{backgroundName}_Skills")
            .SetGuiPresentation(
                "Feature/&Background2024SkillsTitle",
                Gui.Format("Feature/&Background2024SkillsDescription",
                    GetDefinition<SkillDefinition>(skillA).FormatTitle(),
                    GetDefinition<SkillDefinition>(skillB).FormatTitle()))
            .SetProficiencies(ProficiencyType.Skill, skillA, skillB)
            .AddToDB();
    }

    private static FeatureDefinition BuildBackground2024ToolProficiency(
        string backgroundName,
        string toolA,
        string toolB)
    {
        return FeatureDefinitionPointPoolBuilder
            .Create($"PointPoolBackground2024_{backgroundName}_Tool")
            .SetGuiPresentation(
                "Feature/&Background2024ToolTitle",
                "Feature/&Background2024ToolDescription")
            .SetPool(HeroDefinitions.PointsPoolType.Tool, 1)
            .RestrictChoices(toolA, toolB)
            .AddToDB();
    }

    private static void CaptureOriginalBackgroundFeatures(
        string backgroundName,
        CharacterBackgroundDefinition background)
    {
        if (BackgroundOriginalFeatures.ContainsKey(backgroundName))
        {
            return;
        }

        BackgroundOriginalFeatures[backgroundName] = background.Features
            .Where(feature => BackgroundAsiFeatures.Values.All(asiFeature => asiFeature != feature) &&
                              !IsManagedBackgroundBonusFeature(feature))
            .ToArray();
    }

    private static void RestoreOriginalBackgroundFeatures(
        string backgroundName,
        CharacterBackgroundDefinition background)
    {
        if (!BackgroundOriginalFeatures.TryGetValue(backgroundName, out var originalFeatures))
        {
            return;
        }

        foreach (var feature in originalFeatures.Where(feature => !background.Features.Contains(feature)))
        {
            background.Features.Add(feature);
        }
    }

    private static void RemoveManagedBackgroundBonusFeatures(
        CharacterBackgroundDefinition background,
        FeatureDefinition grantedFeature,
        FeatureDefinition displayFeature,
        (FeatureDefinition Skills, FeatureDefinition Tool) proficiencyFeatures,
        FeatureDefinition storyCompatibilityFeature)
    {
        background.Features.RemoveAll(feature =>
            feature == grantedFeature ||
            feature == displayFeature ||
            feature == proficiencyFeatures.Skills ||
            feature == proficiencyFeatures.Tool ||
            feature == storyCompatibilityFeature);
    }

    private static bool IsManagedBackgroundBonusFeature(FeatureDefinition feature)
    {
        return BackgroundBonusGrantFeatures.ContainsValue(feature) ||
               BackgroundBonusDisplayFeatures.ContainsValue(feature) ||
               BackgroundProficiencyFeatures.Values.Any(features =>
                   feature == features.Skills || feature == features.Tool);
    }

    private static bool IsSuppressedBackground2024Feature(FeatureDefinition feature)
    {
        return feature is FeatureDefinitionProficiency ||
               feature is FeatureDefinitionPointPool ||
               feature is FeatureDefinitionCastSpell ||
               feature is FeatureDefinitionAttackModifier;
    }

    private static int GetBackgroundFeatureInsertIndex(
        string backgroundName,
        CharacterBackgroundDefinition background)
    {
        if (!BackgroundAsiFeatures.TryGetValue(backgroundName, out var backgroundAsiFeature))
        {
            return 0;
        }

        var backgroundAsiIndex = background.Features.IndexOf(backgroundAsiFeature);

        return backgroundAsiIndex >= 0 ? backgroundAsiIndex + 1 : 0;
    }

    private static void RemoveMatchingFeature([NotNull] List<FeatureUnlockByLevel> unlocks, BaseDefinition toRemove)
    {
        if (!toRemove)
        {
            return;
        }

        unlocks.RemoveAll(unlock => unlock.FeatureDefinition == toRemove);
    }

    private static void RemoveAlternateHumanBonusFeatPool([NotNull] List<FeatureUnlockByLevel> unlocks)
    {
        if (Main.Settings.TotalFeatsGrantedFirstLevel == 0)
        {
            RemoveMatchingFeature(unlocks, PointPoolBonusFeat);

            return;
        }

        var name = $"PointPool{Main.Settings.TotalFeatsGrantedFirstLevel + 1}BonusFeats";

        if (TryGetDefinition<FeatureDefinitionPointPool>(name, out var pointPool))
        {
            RemoveMatchingFeature(unlocks, pointPool);
        }
    }

    private static void RemoveLegacyHumanOriginFeatFeatures([NotNull] List<FeatureUnlockByLevel> unlocks)
    {
        if (TryGetDefinition<FeatureDefinitionFeatureSet>(HumanOriginFeatFeatureSetName, out var featureSet))
        {
            RemoveMatchingFeature(unlocks, featureSet);
        }
    }

    private static void AddFeatureUnlock([NotNull] List<FeatureUnlockByLevel> unlocks, FeatureDefinition feature)
    {
        if (!feature || unlocks.Exists(unlock => unlock.FeatureDefinition == feature))
        {
            return;
        }

        unlocks.Add(new FeatureUnlockByLevel(feature, 1));
    }

    private static void DisableAlternateHumanWhenBackgroundAsiEnabled()
    {
        if (Main.Settings.EnableBackgroundASI)
        {
            Main.Settings.EnableAlternateHuman = false;
        }
    }

    private static bool NormalizeBackgroundOptionSettings()
    {
        var flexibleBackgroundsChanged = false;

        if (!Main.Settings.EnableBackgroundASI)
        {
            Main.Settings.EnableBackgroundBonusFeats = false;
        }
        else if (Main.Settings.EnableBackgroundBonusFeats)
        {
            flexibleBackgroundsChanged = Main.Settings.EnableFlexibleBackgrounds;
            Main.Settings.EnableFlexibleBackgrounds = false;
        }

        DisableAlternateHumanWhenBackgroundAsiEnabled();

        return flexibleBackgroundsChanged;
    }

    private static string GetAbilityAbbreviation(string attribute)
    {
        return attribute switch
        {
            AttributeDefinitions.Strength => "STR",
            AttributeDefinitions.Dexterity => "DEX",
            AttributeDefinitions.Constitution => "CON",
            AttributeDefinitions.Intelligence => "INT",
            AttributeDefinitions.Wisdom => "WIS",
            AttributeDefinitions.Charisma => "CHA",
            _ => attribute.Substring(0, 3).ToUpperInvariant()
        };
    }
}
