using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
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

    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> BackgroundAsiFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusGrantFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusDisplayFeatures = new();
    private static bool _backgroundOptionsLoaded;

    internal static bool IsAlternateHumanEffectivelyEnabled()
    {
        return Main.Settings.EnableBackgroundASI || Main.Settings.EnableAlternateHuman;
    }

    internal static bool IsAlternateHumanForcedByBackgroundASI()
    {
        return Main.Settings.EnableBackgroundASI;
    }

    internal static void ApplyBackgroundOptions()
    {
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

        if (!enableBackgroundAsi)
        {
            FeatureDefinition humanAsiFeature = IsAlternateHumanEffectivelyEnabled()
                ? FeatureDefinitionPointPools.PointPoolAbilityScoreImprovement
                : FeatureDefinitionAttributeModifiers.AttributeModifierHumanAbilityScoreIncrease;

            if (!human.FeatureUnlocks.Exists(unlock => unlock.FeatureDefinition == humanAsiFeature))
            {
                human.FeatureUnlocks.Add(new FeatureUnlockByLevel(humanAsiFeature, 1));
            }
        }

        human.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchBackgroundBonusFeats()
    {
        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        var enableBackgroundBonusFeats = Main.Settings.EnableBackgroundBonusFeats;

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

            if (!Main.Settings.EnableBackgroundBonusFeats)
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
        unlocks.RemoveAll(unlock => unlock.FeatureDefinition == toRemove);
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
