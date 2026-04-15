using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
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
    private const string BackgroundTroublemakerName = "BackgroundTroublemaker";
    private const string FeatSkilledName = "FeatSkilled";
    private const string HumanOriginFeatFeatureSetName = "FeatureSetHumanOriginFeat2024";
    private const string HumanOriginFeatChoiceFeatureSetPrefix = "FeatureSetHumanOriginFeat2024_";
    private const string HumanOriginFeatPointPoolName = "PointPoolHumanOriginFeat2024";
    private const string HumanOriginFeatSkilledPointPoolName = "PointPoolHumanOriginFeatSkilled";
    private const string HumanOriginFeatTag = "02RaceHumanOriginFeat2024";

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
        { "Spy", (AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution, AttributeDefinitions.Charisma) },
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
        { "Lawkeeper", "FeatAlert" },
        { "Lowlife", "FeatAlert" },
        { "Occultist_Background", "FeatLucky" },
        { "Philosopher", "FeatMagicInitiateWizard" },
        { "SellSword", "FeatSavageAttack" },
        { "Spy", "FeatAlert" },
        { "Wanderer", "FeatLucky" },
        { BackgroundDevotedName, "FeatMagicInitiateCleric" },
        { BackgroundFarmerName, "FeatTough" },
        { BackgroundMilitiaName, "FeatAlert" },
        { BackgroundTroublemakerName, FeatSkilledName }
    };

    private static readonly HashSet<string> OriginRestrictedFeatNames =
    [
        "FeatAlert",
        "FeatHealer",
        "FeatLucky",
        "FeatMagicInitiateBard",
        "FeatMagicInitiateCleric",
        "FeatMagicInitiateWizard",
        "FeatSavageAttack",
        "FeatTough"
    ];

    private static readonly string[] HumanOriginFeatNames =
    [
        "FeatAlert",
        "FeatHealer",
        "FeatLucky",
        "FeatMagicInitiateBard",
        "FeatMagicInitiateCleric",
        "FeatMagicInitiateWizard",
        "FeatSavageAttack",
        "FeatTough",
        FeatSkilledName
    ];
    
    private static readonly HashSet<string> HumanOriginFeatChoiceNames = [..HumanOriginFeatNames];

    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> BackgroundAsiFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusGrantFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusDisplayFeatures = new();
    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> HumanOriginFeatChoiceFeatures = new();
    private static readonly Dictionary<string, string> HumanOriginFeatSelections = new();
    private static FeatureDefinitionFeatureSet HumanOriginFeatFeatureSet;
    private static FeatureDefinitionPointPool HumanOriginFeatPointPool;
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

        FeatsContext.SwitchFirstLevelTotalFeats();

        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        SwitchBackgroundASI();
        SwitchBackgroundBonusFeats();
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

            if (featName != FeatSkilledName)
            {
                BackgroundBonusDisplayFeatures[featName] = BuildOriginFeatDisplayFeature(featName);
            }
        }

        HumanOriginFeatPointPool = BuildHumanOriginFeatPointPool();
        HumanOriginFeatFeatureSet = BuildHumanOriginFeatFeatureSet();
    }

    internal static HashSet<string> GetActiveOriginRestrictedFeatNames(RulesetCharacterHero hero)
    {
        HashSet<string> result = [];

        if (!_backgroundOptionsLoaded || hero?.GetHeroBuildingData() is not { } heroBuildingData)
        {
            return result;
        }

        if (!heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack))
        {
            return result;
        }

        var service = ServiceRepository.GetService<ICharacterBuildingService>();

        foreach (var tag in pointPoolStack.ActivePools.Keys)
        {
            var pointPool = service.GetPointPoolOfTypeAndTag(heroBuildingData, HeroDefinitions.PointsPoolType.Feat, tag);

            if (pointPool?.RestrictedChoices == null || pointPool.RestrictedChoices.Count == 0)
            {
                continue;
            }

            if (IsHumanOriginFeatTag(tag))
            {
                continue;
            }

            foreach (var featName in pointPool.RestrictedChoices.Where(OriginRestrictedFeatNames.Contains))
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

        if (!_backgroundOptionsLoaded || heroBuildingData == null || string.IsNullOrEmpty(tag))
        {
            return false;
        }

        var service = ServiceRepository.GetService<ICharacterBuildingService>();
        var pointPool = service.GetPointPoolOfTypeAndTag(heroBuildingData, HeroDefinitions.PointsPoolType.Feat, tag);

        if (pointPool?.RestrictedChoices is not { Count: 1 })
        {
            return false;
        }

        var candidate = pointPool.RestrictedChoices[0];

        if (!OriginRestrictedFeatNames.Contains(candidate))
        {
            return false;
        }

        featName = candidate;

        return true;
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

        if (humanFeatName != backgroundFeatName)
        {
            return false;
        }

        featName = backgroundFeatName;

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
               featName == backgroundFeatName;
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
            !IsHumanOriginFeatTag(tag))
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
            !TryGetDefinition<FeatDefinition>(featName, out featDefinition))
        {
            return false;
        }

        return true;
    }

    internal static bool TryApplyHumanOriginFeatPointPool(
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinition feature)
    {
        if (!_backgroundOptionsLoaded ||
            feature != HumanOriginFeatPointPool ||
            heroBuildingData == null ||
            !heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack))
        {
            return false;
        }

        if (pointPoolStack.ActivePools.ContainsKey(HumanOriginFeatTag))
        {
            return true;
        }

        var pool = new PointPool(
            HumanOriginFeatPointPool.poolAmount,
            HumanOriginFeatPointPool.RestrictedChoices,
            HumanOriginFeatPointPool.UniqueChoices)
        {
            Description = HumanOriginFeatPointPool.GuiPresentation.Description
        };

        pointPoolStack.ActivePools.Add(HumanOriginFeatTag, pool);

        return true;
    }

    internal static void RemoveHumanOriginFeatPointPool(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return;
        }

        if (heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack))
        {
            pointPoolStack.ActivePools.Remove(HumanOriginFeatTag);
        }

        heroBuildingData.LevelupTrainedFeats.Remove(HumanOriginFeatTag);
    }

    internal static void ClearHumanOriginFeatTraining(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return;
        }

        ServiceRepository.GetService<ICharacterBuildingService>().UntrainFeats(heroBuildingData, HumanOriginFeatTag);
        heroBuildingData.LevelupTrainedFeats.Remove(HumanOriginFeatTag);
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

        var trainedFeat = trainedFeats.FirstOrDefault(feat => HumanOriginFeatChoiceNames.Contains(feat.Name));

        if (!trainedFeat)
        {
            return false;
        }

        featName = trainedFeat.Name;

        return true;
    }

    private static bool IsHumanOriginFeatTag(string tag)
    {
        return tag == HumanOriginFeatTag;
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

    private static bool IsValidHumanOriginFeatSelectionHero(RulesetCharacterHero hero)
    {
        return _backgroundOptionsLoaded &&
               Main.Settings.EnableBackgroundASI &&
               hero?.RaceDefinition?.Name == "Human";
    }

    private static string GetHumanOriginSelectionKey(RulesetCharacterHero hero)
    {
        return hero.Guid.ToString();
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

            background.Features.RemoveAll(feature => feature == grantedFeature || feature == displayFeature);

            if (!enableBackgroundBonusFeats)
            {
                continue;
            }

            var insertIndex = GetBackgroundFeatureInsertIndex(backgroundName, background);

            background.Features.Insert(insertIndex, grantedFeature);

            if (displayFeature)
            {
                background.Features.Insert(insertIndex + 1, displayFeature);
            }
        }

        if (TryGetDefinition<CharacterBackgroundDefinition>(BackgroundDevotedName, out var devotedBackground) &&
            TryGetDefinition<FeatureDefinition>("BonusCantripsBackgroundDevoted", out var devotedBonus))
        {
            devotedBackground.Features.RemoveAll(feature =>
                feature == devotedBonus || feature == PointPoolBackgroundLanguageChoice_one);

            devotedBackground.Features.Add(
                enableBackgroundBonusFeats
                    ? PointPoolBackgroundLanguageChoice_one
                    : devotedBonus);
        }

        SwitchAddOriginFeatsToAutoLearn();
    }

    internal static void SwitchAddOriginFeatsToAutoLearn()
    {
        foreach (var characterClass in DatabaseRepository.GetDatabase<CharacterClassDefinition>())
        {
            foreach (var featName in OriginRestrictedFeatNames)
            {
                characterClass.featAutolearnPreference.RemoveAll(name => name == featName);
            }

            if (!IsBackgroundBonusFeatsEnabled())
            {
                continue;
            }

            foreach (var featName in OriginRestrictedFeatNames)
            {
                if (!characterClass.featAutolearnPreference.Contains(featName))
                {
                    characterClass.featAutolearnPreference.Add(featName);
                }
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
        var choices = HumanOriginFeatNames
            .Select(BuildHumanOriginFeatChoice)
            .Where(feat => feat)
            .Select(feat => feat.Name)
            .ToArray();

        return choices.Length == 0
            ? null
            : FeatureDefinitionPointPoolBuilder
                .Create(HumanOriginFeatPointPoolName)
                .SetGuiPresentation(
                    "Feature/&PointPoolHumanOriginFeatTitle",
                    "Feature/&PointPoolHumanOriginFeatDescription")
                .SetPool(HeroDefinitions.PointsPoolType.Feat, 1)
                .RestrictChoices(choices)
                .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildHumanOriginFeatFeatureSet()
    {
        var choices = HumanOriginFeatNames
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
        var featDefinition = BuildHumanOriginFeatChoice(featName);

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
        if (featName == FeatSkilledName)
        {
            return BuildHumanOriginFeatSkilledFeat();
        }

        return TryGetDefinition<FeatDefinition>(featName, out var featDefinition)
            ? featDefinition
            : null;
    }

    private static FeatDefinition BuildHumanOriginFeatSkilledFeat()
    {
        if (TryGetDefinition<FeatDefinition>(FeatSkilledName, out var featDefinition))
        {
            return featDefinition;
        }

        var skillPool = FeatureDefinitionPointPoolBuilder
            .Create(HumanOriginFeatSkilledPointPoolName)
            .SetGuiPresentationNoContent(true)
            .SetPool(HeroDefinitions.PointsPoolType.Skill, 3)
            .AddToDB();

        featDefinition = FeatDefinitionBuilder
            .Create(FeatSkilledName)
            .SetGuiPresentation("Feature/&PointPoolSkilledTitle", "Feature/&PointPoolSkilledDescription")
            .SetFeatures(skillPool)
            .AddToDB();

        featDefinition.GuiPresentation.hidden = true;

        return featDefinition;
    }

    private static FeatureDefinition BuildOriginFeatDisplayFeature(string featName)
    {
        var featDefinition = GetDefinition<FeatDefinition>(featName);

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
