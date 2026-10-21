using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Feats;
using static RuleDefinitions;
using static FeatureDefinitionAttributeModifier;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private const string BackgroundAcademicName = "Academic";
    private const string BackgroundAcolyteName = "Acolyte";
    private const string BackgroundAesceticName = "Aescetic_Background";
    private const string BackgroundAristocratName = "Aristocrat";
    private const string BackgroundArtistName = "Artist_Background";
    private const string BackgroundDevotedName = "BackgroundDevoted";
    private const string BackgroundFarmerName = "BackgroundFarmer";
    private const string BackgroundLawkeeperName = "Lawkeeper";
    private const string BackgroundLowlifeName = "Lowlife";
    private const string BackgroundMilitiaName = "BackgroundMilitia";
    private const string BackgroundOccultistName = "Occultist_Background";
    private const string BackgroundPhilosopherName = "Philosopher";
    private const string BackgroundSellSwordName = "SellSword";
    private const string BackgroundSpyName = "Spy";
    private const string BackgroundTroublemakerName = "BackgroundTroublemaker";
    private const string BackgroundWandererName = "Wanderer";
    private const string HumanOriginFeatFeatureSetName = "FeatureSetHumanOriginFeat2024";
    private const string HumanOriginFeatChoiceFeatureSetPrefix = "FeatureSetHumanOriginFeat2024_";
    private const string HumanOriginFeatPointPoolName = "PointPoolHumanOriginFeat2024";
    private const string BackgroundFeatPointPoolPrefix = "PointPoolBackgroundFeat_";
    private const string BackgroundFeatDisplayPrefix = "FeatureBackgroundFeatDisplay_";
    private const string HumanOriginFeatTag = "02RaceHumanOriginFeat2024";
    private const string ToolGamingSetDiceTypeName = "GamingSetDiceType";
    private const string ToolMusicalInstrumentLyreTypeName = "MusicalInstrumentLyreType";
    private const string HalfElfVersatileBloodlineFeatureSetName =
        "FeatureSetHalfElfVersatileBloodlineSavingThrowChoice2024";
    private const string HalfElfVersatileBloodlineFeaturePrefix =
        "SavingThrowAffinityHalfElfVersatileBloodline";

    private static readonly Dictionary<string, (string A, string B, string C)> BackgroundAbilitySets = new()
    {
        { BackgroundAcademicName, (AttributeDefinitions.Dexterity, AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom) },
        { BackgroundAcolyteName, (AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma) },
        { BackgroundAesceticName, (AttributeDefinitions.Constitution, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma) },
        { BackgroundAristocratName, (AttributeDefinitions.Strength, AttributeDefinitions.Intelligence, AttributeDefinitions.Charisma) },
        { BackgroundArtistName, (AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Charisma) },
        { BackgroundLawkeeperName, (AttributeDefinitions.Strength, AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom) },
        { BackgroundLowlifeName, (AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence) },
        { BackgroundOccultistName, (AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence, AttributeDefinitions.Charisma) },
        { BackgroundPhilosopherName, (AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom) },
        { BackgroundSellSwordName, (AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution) },
        { BackgroundSpyName, (AttributeDefinitions.Dexterity, AttributeDefinitions.Intelligence, AttributeDefinitions.Charisma) },
        { BackgroundWandererName, (AttributeDefinitions.Dexterity, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma) },
        { BackgroundDevotedName, (AttributeDefinitions.Strength, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma) },
        { BackgroundFarmerName, (AttributeDefinitions.Strength, AttributeDefinitions.Constitution, AttributeDefinitions.Wisdom) },
        { BackgroundMilitiaName, (AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Wisdom) },
        { BackgroundTroublemakerName, (AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution, AttributeDefinitions.Charisma) }
    };

    private static readonly Dictionary<string, (string Feat, string Tool)[]> BackgroundFeatSets = new()
    {
        {
            BackgroundAcademicName,
            [
                ("FeatSharpEye2024", ToolTypeDefinitions.ScrollKitType.Name),
                ("FeatGroupPortalJumper2024", ToolTypeDefinitions.EnchantingToolType.Name)
            ]
        },
        {
            BackgroundAcolyteName,
            [
                ("FeatMagicInitiateCleric", ToolMusicalInstrumentLyreTypeName),
                ("FeatMusician2024", ToolTypeDefinitions.ScrollKitType.Name)
            ]
        },
        {
            BackgroundAesceticName,
            [
                ("FeatHealer", ToolTypeDefinitions.HerbalismKitType.Name),
                ("FeatMagicInitiateDruid", ToolTypeDefinitions.ScrollKitType.Name)
            ]
        },
        {
            BackgroundAristocratName,
            [
                ("FeatSkilled", ToolMusicalInstrumentLyreTypeName),
                ("FeatLucky", ToolGamingSetDiceTypeName)
            ]
        },
        {
            BackgroundArtistName,
            [
                ("FeatMusician2024", ToolTypeDefinitions.DisguiseKitType.Name),
                ("FeatSkilled", ToolTypeDefinitions.ArtisanToolSmithToolsType.Name)
            ]
        },
        {
            BackgroundLawkeeperName,
            [
                ("FeatSavageAttack", ToolTypeDefinitions.ArtisanToolSmithToolsType.Name),
                ("FeatTavernBrawler2024", ToolTypeDefinitions.ScrollKitType.Name)
            ]
        },
        {
            BackgroundLowlifeName,
            [
                ("FeatTavernBrawler2024", ToolTypeDefinitions.ThievesToolsType.Name),
                ("FeatAlert", ToolTypeDefinitions.PoisonersKitType.Name)
            ]
        },
        {
            BackgroundOccultistName,
            [
                ("FeatGroupPortalJumper2024", ToolTypeDefinitions.EnchantingToolType.Name),
                ("FeatGroupArcaneOmens2024", ToolTypeDefinitions.PoisonersKitType.Name)
            ]
        },
        {
            BackgroundPhilosopherName,
            [
                ("FeatMagicInitiateWizard", ToolTypeDefinitions.EnchantingToolType.Name),
                ("FeatSharpEye2024", ToolMusicalInstrumentLyreTypeName)
            ]
        },
        {
            BackgroundSellSwordName,
            [
                ("FeatSurvivor2024", ToolTypeDefinitions.ArtisanToolSmithToolsType.Name),
                ("FeatTough", ToolGamingSetDiceTypeName)
            ]
        },
        {
            BackgroundSpyName,
            [
                ("FeatGroupArcaneEloquence2024", ToolTypeDefinitions.DisguiseKitType.Name),
                ("FeatMagicInitiateWizard", ToolTypeDefinitions.ThievesToolsType.Name)
            ]
        },
        {
            BackgroundWandererName,
            [
                ("FeatMagicInitiateDruid", ToolTypeDefinitions.HerbalismKitType.Name),
                ("FeatSurvivor2024", ToolTypeDefinitions.PoisonersKitType.Name)
            ]
        },
        {
            BackgroundDevotedName,
            [
                ("FeatGroupArcaneOmens2024", ToolGamingSetDiceTypeName),
                ("FeatMagicInitiateCleric", ToolTypeDefinitions.ScrollKitType.Name)
            ]
        },
        {
            BackgroundFarmerName,
            [
                ("FeatTough", ToolTypeDefinitions.ArtisanToolSmithToolsType.Name),
                ("FeatHealer", ToolTypeDefinitions.HerbalismKitType.Name)
            ]
        },
        {
            BackgroundMilitiaName,
            [
                ("FeatAlert", ToolGamingSetDiceTypeName),
                ("FeatSavageAttack", ToolTypeDefinitions.HerbalismKitType.Name)
            ]
        },
        {
            BackgroundTroublemakerName,
            [
                ("FeatLucky", ToolGamingSetDiceTypeName),
                ("FeatGroupArcaneEloquence2024", ToolTypeDefinitions.ThievesToolsType.Name)
            ]
        }
    };

    private static readonly Dictionary<string, (string SkillA, string SkillB)> BackgroundSkillSets = new()
    {
        { BackgroundAcademicName, (SkillDefinitions.Investigation, SkillDefinitions.Perception) },
        { BackgroundAcolyteName, (SkillDefinitions.Insight, SkillDefinitions.Religion) },
        { BackgroundAesceticName, (SkillDefinitions.Medecine, SkillDefinitions.Religion) },
        { BackgroundAristocratName, (SkillDefinitions.History, SkillDefinitions.Persuasion) },
        { BackgroundArtistName, (SkillDefinitions.Acrobatics, SkillDefinitions.Performance) },
        { BackgroundLawkeeperName, (SkillDefinitions.Investigation, SkillDefinitions.Intimidation) },
        { BackgroundLowlifeName, (SkillDefinitions.Insight, SkillDefinitions.Stealth) },
        { BackgroundOccultistName, (SkillDefinitions.Arcana, SkillDefinitions.Religion) },
        { BackgroundPhilosopherName, (SkillDefinitions.Arcana, SkillDefinitions.History) },
        { BackgroundSellSwordName, (SkillDefinitions.Athletics, SkillDefinitions.Intimidation) },
        { BackgroundSpyName, (SkillDefinitions.Stealth, SkillDefinitions.Deception) },
        { BackgroundWandererName, (SkillDefinitions.Stealth, SkillDefinitions.Survival) },
        { BackgroundDevotedName, (SkillDefinitions.Investigation, SkillDefinitions.Persuasion) },
        { BackgroundFarmerName, (SkillDefinitions.AnimalHandling, SkillDefinitions.Nature) },
        { BackgroundMilitiaName, (SkillDefinitions.Athletics, SkillDefinitions.Perception) },
        { BackgroundTroublemakerName, (SkillDefinitions.Deception, SkillDefinitions.SleightOfHand) }
    };

    private static readonly string[] Tabletop2024OriginFeatNames =
    [
        "FeatAlert",
        "FeatGroupArcaneEloquence2024",
        "FeatGroupArcaneOmens2024",
        "FeatHealer",
        "FeatLucky",
        "FeatMagicInitiateCleric",
        "FeatMagicInitiateDruid",
        "FeatMagicInitiateWizard",
        "FeatMusician2024",
        "FeatGroupPortalJumper2024",
        "FeatSavageAttack",
        "FeatSharpEye2024",
        "FeatSkilled",
        "FeatSurvivor2024",
        "FeatTavernBrawler2024",
        "FeatTough"
    ];

    private static readonly string[] HalfElfVersatileBloodlineRaceNames = ["HalfElf", "RaceHalfElfVariant"];

    private static readonly string[] HalfElfVersatileBloodlineSavingThrowAttributes =
    [
        AttributeDefinitions.Dexterity,
        AttributeDefinitions.Intelligence,
        AttributeDefinitions.Wisdom,
        AttributeDefinitions.Charisma
    ];

    private enum RecordedBackgroundFeatMatch
    {
        None,
        Unique,
        Ambiguous
    }

    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> BackgroundAsiFeatures = new();
    private static readonly HashSet<FeatureDefinitionFeatureSet> BackgroundAsiFeatureLookup = [];
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusGrantFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundBonusDisplayFeatures = new();
    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> BackgroundOriginFeatFeatures = new();
    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> BackgroundOriginFeatChoiceFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundSkillFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition> BackgroundStoryFeatures = new();
    private static readonly Dictionary<string, FeatureDefinition[]> BackgroundOriginalFeatures = new();
    private static readonly Dictionary<string, FeatureDefinitionFeatureSet> HumanOriginFeatChoiceFeatures = new();
    private static readonly Dictionary<string, string> HumanOriginFeatSelections = new();
    private static FeatureDefinitionFeatureSet HumanOriginFeatFeatureSet;
    private static FeatureDefinitionPointPool HumanOriginFeatPointPool;
    private static FeatureDefinitionSavingThrowAffinity SavingThrowAffinityHalfElfVersatileBloodlineDexterity2024;
    private static FeatureDefinitionSavingThrowAffinity SavingThrowAffinityHalfElfVersatileBloodlineIntelligence2024;
    private static FeatureDefinitionSavingThrowAffinity SavingThrowAffinityHalfElfVersatileBloodlineWisdom2024;
    private static FeatureDefinitionSavingThrowAffinity SavingThrowAffinityHalfElfVersatileBloodlineCharisma2024;
    private static FeatureDefinitionFeatureSet HalfElfVersatileBloodlineFeatureSet;
    private static bool _backgroundOptionsLoaded;

    internal static bool IsBackgroundAsiEnabled()
    {
        return Main.Settings.EnableTabletopFeatRules2024 && Main.Settings.EnableBackgroundASI;
    }

    internal static bool IsAlternateHumanEffectivelyEnabled()
    {
        return Main.Settings.EnableAlternateHuman && !IsBackgroundAsiEnabled();
    }

    internal static bool IsBackgroundBonusFeatsEnabled()
    {
        return IsBackgroundAsiEnabled() &&
               Main.Settings.EnableBackgroundBonusFeats;
    }

    internal static bool IsBackgroundOriginFeatChoicesEnabled()
    {
        return IsBackgroundBonusFeatsEnabled() && Main.Settings.EnableBackgroundOriginFeatChoices;
    }

    internal static bool IsFlexibleBackgroundsEffectivelyEnabled()
    {
        return Main.Settings.EnableFlexibleBackgrounds && !IsBackgroundBonusFeatsEnabled();
    }

    internal static bool IsFlexibleRacesEffectivelyEnabled()
    {
        return Main.Settings.EnableFlexibleRaces && !IsBackgroundAsiEnabled();
    }

    internal static void ApplyBackgroundOptions()
    {
        if (!ApplyBackgroundDefinitionRules())
        {
            return;
        }

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

    private static bool ApplyBackgroundDefinitionRules()
    {
        RefreshModeAwareOriginFeatDefinitions();
        FeatsContext.SwitchFirstLevelTotalFeats();

        if (!_backgroundOptionsLoaded)
        {
            return false;
        }

        FlexibleRacesContext.SwitchFlexibleRaces();

        var backgroundBonusFeatsEnabled = IsBackgroundBonusFeatsEnabled();

        if (backgroundBonusFeatsEnabled)
        {
            // Restore ordinary grants before the 2024 package replaces them.
            FlexibleBackgroundsContext.SwitchFlexibleBackgrounds();
        }

        SwitchBackgroundASI();
        SwitchBackgroundBonusFeats();

        if (!backgroundBonusFeatsEnabled)
        {
            // The original snapshot can contain flexible grants from an earlier setting.
            // Reconcile the current preference after that snapshot has been restored.
            FlexibleBackgroundsContext.SwitchFlexibleBackgrounds();
        }

        return true;
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
            var featureSet = BuildBackgroundAsiFeatureSet(backgroundName, attributes.A, attributes.B, attributes.C);

            BackgroundAsiFeatures[backgroundName] = featureSet;
            BackgroundAsiFeatureLookup.Add(featureSet);
        }

        foreach (var featName in Tabletop2024OriginFeatNames)
        {
            BackgroundBonusGrantFeatures[featName] = BuildOriginFeatPointPool(featName);

            BackgroundBonusDisplayFeatures[featName] = BuildOriginFeatDisplayFeature(featName);
        }

        foreach (var entry in BackgroundSkillSets)
        {
            BackgroundSkillFeatures[entry.Key] = BuildBackground2024SkillProficiency(
                entry.Key, entry.Value.SkillA, entry.Value.SkillB);
        }

        foreach (var backgroundName in BackgroundFeatSets.Keys)
        {
            BackgroundOriginFeatFeatures[backgroundName] = BuildBackgroundOriginFeatFeatureSet(backgroundName);
        }

        if (TryGetDefinition<FeatureDefinition>("ProficiencySpyLanguage", out var spyLanguage))
        {
            BackgroundStoryFeatures[BackgroundSpyName] = spyLanguage;
        }

        HumanOriginFeatPointPool = BuildHumanOriginFeatPointPool();
        HumanOriginFeatFeatureSet = BuildHumanOriginFeatFeatureSet();
        HalfElfVersatileBloodlineFeatureSet = BuildHalfElfVersatileBloodlineFeatureSet();
        RefreshModeAwareOriginFeatDefinitions();
    }

    internal static void RefreshModeAwareOriginFeatDefinitions()
    {
        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        foreach (var featName in Tabletop2024OriginFeatNames)
        {
            if (BackgroundBonusGrantFeatures.TryGetValue(featName, out var grantedFeature) &&
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

            if (BackgroundBonusDisplayFeatures.TryGetValue(featName, out var displayFeature) &&
                TryResolveModeAwareFeatDefinition(featName, out var displayFeatDefinition))
            {
                displayFeature.GuiPresentation.title = "Feature/&BackgroundBonusFeatTitle";
                displayFeature.GuiPresentation.description = FormatOriginFeatGainDescription(displayFeatDefinition);
                displayFeature.GuiPresentation.spriteReference = displayFeatDefinition?.GuiPresentation?.spriteReference;
            }
        }

        if (HumanOriginFeatPointPool)
        {
            HumanOriginFeatPointPool.RestrictedChoices.Clear();

            foreach (var featName in Tabletop2024OriginFeatNames)
            {
                var featDefinition = GetOriginFeatDefinition(featName);

                if (featDefinition &&
                    !HumanOriginFeatPointPool.RestrictedChoices.Contains(featDefinition.Name))
                {
                    HumanOriginFeatPointPool.RestrictedChoices.Add(featDefinition.Name);
                }
            }
        }

        foreach (var featName in Tabletop2024OriginFeatNames)
        {
            if (!HumanOriginFeatChoiceFeatures.TryGetValue(featName, out var choiceFeature))
            {
                continue;
            }

            var featDefinition = GetOriginFeatDefinition(featName);

            if (!featDefinition)
            {
                continue;
            }

            choiceFeature.GuiPresentation.title = featDefinition.GuiPresentation.title;
            choiceFeature.GuiPresentation.description = featDefinition.GuiPresentation.description;
            choiceFeature.GuiPresentation.spriteReference = featDefinition.GuiPresentation.spriteReference;
        }

        if (HumanOriginFeatFeatureSet)
        {
            HumanOriginFeatFeatureSet.FeatureSet.Clear();

            foreach (var featName in Tabletop2024OriginFeatNames)
            {
                if (HumanOriginFeatChoiceFeatures.TryGetValue(featName, out var choiceFeature))
                {
                    HumanOriginFeatFeatureSet.FeatureSet.Add(choiceFeature);
                }
            }
        }

        foreach (var entry in BackgroundOriginFeatFeatures)
        {
            var backgroundName = entry.Key;
            var featureSet = entry.Value;
            featureSet.FeatureSet.Clear();
            featureSet.FeatureSet.AddRange(BackgroundFeatSets[backgroundName].Select(choice =>
                BackgroundOriginFeatChoiceFeatures[GetBackgroundOriginFeatChoiceName(backgroundName, choice.Feat)]));
        }
    }

    private static bool TryGetBackgroundFeatName(string backgroundName, out string featName)
    {
        featName = null;

        if (string.IsNullOrEmpty(backgroundName))
        {
            return false;
        }

        if (!BackgroundFeatSets.TryGetValue(backgroundName, out var feats))
        {
            return false;
        }

        featName = feats[0].Feat;

        return true;
    }

    private static string GetOriginFeatChoiceName(string featName)
    {
        var canonicalName = GetCanonicalTabletopFeatName(featName);

        if (string.IsNullOrEmpty(canonicalName) ||
            Tabletop2024OriginFeatNames.Contains(canonicalName) ||
            !TryGetDefinition<FeatDefinition>(featName, out var feat))
        {
            return canonicalName;
        }

        // Ability variants keep their own identity; their parent determines the origin choice.
        return EnumerateRestrictionAliasesForFeat(feat, null)
                   .FirstOrDefault(Tabletop2024OriginFeatNames.Contains) ?? canonicalName;
    }

    private static bool AreEquivalentOriginFeatNames(string left, string right)
    {
        return !string.IsNullOrEmpty(left) &&
               !string.IsNullOrEmpty(right) &&
               GetOriginFeatChoiceName(left) == GetOriginFeatChoiceName(right);
    }

    private static IEnumerable<string> EnumerateTaggedRecordedBackgroundFeatNames(
        CharacterHeroBuildingData buildingData)
    {
        if (buildingData?.LevelupTrainedFeats == null)
        {
            yield break;
        }

        foreach (var trainedFeatEntry in buildingData.LevelupTrainedFeats)
        {
            if (IsHumanOriginFeatTag(trainedFeatEntry.Key) || trainedFeatEntry.Value == null)
            {
                continue;
            }

            var isBackgroundTag = trainedFeatEntry.Key == AttributeDefinitions.TagBackground;
            string sourceFeatName = null;
            if (!isBackgroundTag && !TryGetBackgroundOriginFeatNameFromTag(trainedFeatEntry.Key, out sourceFeatName))
            {
                continue;
            }

            foreach (var recordedFeatName in trainedFeatEntry.Value
                         .Where(feat => feat != null)
                         .Select(feat => feat.Name))
            {
                if (Tabletop2024OriginFeatNames.Contains(GetOriginFeatChoiceName(recordedFeatName)) &&
                    (isBackgroundTag ||
                     AreEquivalentOriginFeatNames(sourceFeatName, recordedFeatName)))
                {
                    yield return recordedFeatName;
                }
            }
        }
    }

    private static void AddDistinctEquivalentFeatName(List<string> featNames, string featName)
    {
        if (!string.IsNullOrEmpty(featName) &&
            featNames.All(existingFeatName => !AreEquivalentTabletopFeatNames(existingFeatName, featName)))
        {
            featNames.Add(featName);
        }
    }

    private static IEnumerable<string> EnumerateMarkedRecordedBackgroundFeatNames(RulesetCharacterHero hero)
    {
        if (hero?.BackgroundDefinition == null)
        {
            yield break;
        }

        var backgroundFeatures = hero.ActiveFeatures.TryGetValue(AttributeDefinitions.TagBackground, out var features)
            ? features ?? []
            : [];
        var sourcedFeatures = hero.FeaturesOrigin?
            .Where(entry => ReferenceEquals(entry.Value.source, hero.BackgroundDefinition))
            .Select(entry => entry.Key) ?? [];
        var acquiredFeatNames = (hero.TrainedFeats?.Select(feat => feat?.Name) ?? [])
            .Concat(hero.FeatProficiencies ?? [])
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .ToArray();

        foreach (var feature in RulesetActorExtensions.FlattenFeatureList(
                     backgroundFeatures.Concat(sourcedFeatures).ToList()))
        {
            if (feature == null)
            {
                continue;
            }

            if (!TryGetOriginFeatNameFromMarker(feature.Name, BackgroundFeatDisplayPrefix, out var markerFeatName) &&
                !TryGetBackgroundOriginFeatNameFromTag(feature.Name, out markerFeatName))
            {
                continue;
            }

            // A real background marker identifies the origin; acquired leaves only refine that source's choice.
            var acquiredNames = acquiredFeatNames
                .Where(name => AreEquivalentOriginFeatNames(name, markerFeatName))
                .ToArray();

            foreach (var featName in acquiredNames.Length > 0 ? acquiredNames : [markerFeatName])
            {
                yield return featName;
            }
        }
    }

    private static RecordedBackgroundFeatMatch GetRecordedBackgroundFeatMatch(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out string featName)
    {
        featName = null;

        var displayHero = hero ?? buildingData?.HeroCharacter;
        if (displayHero?.BackgroundDefinition == null)
        {
            return RecordedBackgroundFeatMatch.None;
        }

        var recordedFeatNames = new List<string>();

        foreach (var taggedFeatName in EnumerateTaggedRecordedBackgroundFeatNames(buildingData))
        {
            AddDistinctEquivalentFeatName(recordedFeatNames, taggedFeatName);
        }

        if (recordedFeatNames.Count == 0)
        {
            foreach (var markedFeatName in EnumerateMarkedRecordedBackgroundFeatNames(displayHero))
            {
                AddDistinctEquivalentFeatName(recordedFeatNames, markedFeatName);
            }
        }

        featName = recordedFeatNames.Count == 1 ? recordedFeatNames[0] : null;

        return recordedFeatNames.Count switch
        {
            0 => RecordedBackgroundFeatMatch.None,
            1 => RecordedBackgroundFeatMatch.Unique,
            _ => RecordedBackgroundFeatMatch.Ambiguous
        };
    }

    private static bool TryGetEffectiveBackgroundFeatName(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out string featName)
    {
        featName = null;

        var displayHero = hero ?? buildingData?.HeroCharacter;

        if (displayHero?.BackgroundDefinition == null)
        {
            return false;
        }

        if (buildingData == null)
        {
            TryGetReservedHeroBuildingData(displayHero, out buildingData);
        }

        var match = GetRecordedBackgroundFeatMatch(
            displayHero,
            buildingData,
            out featName);

        if (match == RecordedBackgroundFeatMatch.Unique)
        {
            return true;
        }

        if (match == RecordedBackgroundFeatMatch.Ambiguous)
        {
            return false;
        }

        return !IsBackgroundOriginFeatChoicesEnabled() &&
               buildingData != null &&
               !LevelUpHelper.IsLevelingUp(displayHero) &&
               TryGetBackgroundFeatName(displayHero.BackgroundDefinition.Name, out featName);
    }

    private static bool TryGetReservedHeroBuildingData(
        RulesetCharacterHero hero,
        out CharacterHeroBuildingData heroBuildingData)
    {
        heroBuildingData = null;

        return hero != null && hero.TryGetHeroBuildingData(out heroBuildingData);
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
            .Select(GetOriginFeatChoiceName)
            .ToHashSet();

        if (restrictedChoices.Count != 1)
        {
            return false;
        }

        var backgroundFeatName = restrictedChoices.Single();

        if (heroBuildingData.HeroCharacter?.BackgroundDefinition == null ||
            !Tabletop2024OriginFeatNames.Contains(backgroundFeatName) ||
            tag != AttributeDefinitions.TagBackground &&
            (!TryGetBackgroundOriginFeatNameFromTag(tag, out var sourceFeatName) ||
             !AreEquivalentOriginFeatNames(sourceFeatName, backgroundFeatName)))
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

        if (hero?.BackgroundDefinition == null)
        {
            return false;
        }

        // Inspection uses an actual background grant, never the current configuration's mapping.
        return GetRecordedBackgroundFeatMatch(hero, null, out var featName) == RecordedBackgroundFeatMatch.Unique &&
               FeatsContext.TryResolveDisplayFeatDefinition(featName, out featDefinition);
    }

    internal static bool TryGetBackgroundBonusFeatForDisplay(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        if (!_backgroundOptionsLoaded ||
            !IsBackgroundAsiEnabled() ||
            !IsBackgroundBonusFeatsEnabled() ||
            (hero?.BackgroundDefinition ?? buildingData?.HeroCharacter?.BackgroundDefinition) == null)
        {
            return false;
        }

        return TryGetEffectiveBackgroundFeatName(hero, buildingData, out var featName) &&
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
            !IsBackgroundAsiEnabled() ||
            displayHero?.RaceDefinition?.Name != "Human" ||
            !TryGetHumanOriginFeatNameFromBuildingOrSelection(displayHero, buildingData, out var featName))
        {
            return false;
        }

        return TryResolveHumanOriginFeatDisplayDefinition(displayHero, buildingData, featName, out featDefinition);
    }

    internal static bool TryGetHumanOriginFeatForFinalizeSnapshot(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        var displayHero = hero ?? buildingData?.HeroCharacter;

        if (!_backgroundOptionsLoaded ||
            !IsBackgroundAsiEnabled() ||
            displayHero?.RaceDefinition?.Name != "Human" ||
            !TryGetHumanOriginFeatNameFromBuildingOrSelection(displayHero, buildingData, out var featName))
        {
            return false;
        }

        return TryResolveHumanOriginFeatDisplayDefinition(displayHero, buildingData, featName, out featDefinition);
    }

    private static bool TryResolveHumanOriginFeatDisplayDefinition(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        string featName,
        out FeatDefinition featDefinition)
    {
        var trainedFeats = buildingData?.LevelupTrainedFeats.TryGetValue(HumanOriginFeatTag, out var selected) == true
            ? selected
            : (hero?.TrainedFeats ?? []).Concat((hero?.FeatProficiencies ?? [])
                .Select(name => TryGetDefinition<FeatDefinition>(name, out var feat) ? feat : null));
        var recordedFeats = trainedFeats
            .Where(feat => feat && AreEquivalentOriginFeatNames(feat.Name, featName))
            .Distinct()
            .ToArray();

        if (recordedFeats.Length == 1)
        {
            featDefinition = recordedFeats[0];

            return true;
        }

        if (!TryResolveModeAwareFeatDefinition(featName, out featDefinition))
        {
            return false;
        }

        if (TryGetTrainedOrSelectedDescendant(
                buildingData, hero, HumanOriginFeatTag, featDefinition, out var selectedFeat))
        {
            featDefinition = selectedFeat;
        }
        else
        {
            var matchingFeats = (hero?.TrainedFeats ?? [])
                .Where(feat => feat != null && AreEquivalentOriginFeatNames(feat.Name, featName))
                .Distinct()
                .ToArray();

            if (matchingFeats.Length == 1)
            {
                featDefinition = matchingFeats[0];
            }
        }

        return true;
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
            hero?.RaceDefinition?.Name != "Human")
        {
            return false;
        }

        if (TryGetRecordedHumanOriginFeatForDisplay(hero, buildingData, sourceFeature, out var featDefinition))
        {
            displayFeature = featDefinition;
        }
        else
        {
            fallbackTitle = Gui.Localize("Feature/&PointPoolHumanOriginFeatTitle");
        }

        return true;
    }

    private static bool TryGetRecordedHumanOriginFeatForDisplay(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        FeatureDefinition sourceFeature,
        out FeatDefinition featDefinition)
    {
        featDefinition = null;

        if (hero?.RaceDefinition?.Name != "Human")
        {
            return false;
        }

        if (TryGetHumanOriginFeatNameFromBuildingOrSelection(hero, buildingData, out var selectedFeatName))
        {
            return TryResolveHumanOriginFeatDisplayDefinition(hero, buildingData, selectedFeatName, out featDefinition);
        }

        var recordedFeatNames = (hero.TrainedFeats?.Select(feat => feat?.Name) ?? [])
            .Concat(hero.FeatProficiencies ?? [])
            .Where(IsHumanOriginFeatChoiceName)
            .Select(GetCanonicalTabletopFeatName)
            .Distinct()
            .ToArray();
        var markerFeatNames = new HashSet<string>();

        CollectHumanOriginMarkerFeatNames(hero.FeaturesOrigin?.Keys, markerFeatNames);
        CollectHumanOriginMarkerFeatNames(hero.FeaturesToBrowse, markerFeatNames);

        if (sourceFeature != null && TryGetHumanOriginFeatName(sourceFeature, out var markerFeatName))
        {
            markerFeatNames.Add(markerFeatName);
        }

        GetRecordedBackgroundFeatMatch(hero, buildingData, out var backgroundFeatName);

        var candidateFeatNames = recordedFeatNames
            .Where(name => !AreEquivalentOriginFeatNames(name, backgroundFeatName))
            .ToArray();

        // The native exclusion-set marker can still be its default choice. Acquired feats,
        // including their ability variants, take precedence over that display-only marker.
        if (candidateFeatNames.Length != 1)
        {
            candidateFeatNames = candidateFeatNames
                .Where(name => markerFeatNames.Any(marker => AreEquivalentOriginFeatNames(name, marker)))
                .ToArray();
        }

        if (candidateFeatNames.Length == 1)
        {
            return TryResolveHumanOriginFeatDisplayDefinition(hero, buildingData, candidateFeatNames[0], out featDefinition);
        }

        // A repeatable feat may have been acquired from both the race and the background.
        var fallbackFeatNames = markerFeatNames.Where(name =>
                recordedFeatNames.All(recorded => AreEquivalentOriginFeatNames(recorded, backgroundFeatName)) &&
                recordedFeatNames.Any(recorded => AreEquivalentOriginFeatNames(recorded, name)) &&
                TryResolveModeAwareFeatDefinition(name, out var feat) && SkillFeats.IsRepeatable(feat))
            .ToArray();

        return fallbackFeatNames.Length == 1 &&
               TryResolveHumanOriginFeatDisplayDefinition(hero, buildingData, fallbackFeatNames[0], out featDefinition);
    }

    internal static bool TryGetHumanOriginFeatForExistingHeroMarker(
        RulesetCharacterHero hero,
        out FeatDefinition featDefinition)
    {
        return TryGetRecordedHumanOriginFeatForDisplay(hero, null, null, out featDefinition);
    }

    internal static void SwitchBackgroundASI()
    {
        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        var enableBackgroundAsi = IsBackgroundAsiEnabled();

        SwitchBackgroundAsiFeatures(enableBackgroundAsi);
        SwitchRacialAsiFeaturesForBackgroundAsi(enableBackgroundAsi);
        SwitchHumanBackgroundAsiFeatures(enableBackgroundAsi);
        SwitchHalfElfVersatileBloodlineBonus(enableBackgroundAsi);
    }

    private static void SwitchBackgroundAsiFeatures(bool enableBackgroundAsi)
    {
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
    }

    private static void SwitchRacialAsiFeaturesForBackgroundAsi(bool enableBackgroundAsi)
    {
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
                else if (!IsFlexibleRacesEffectivelyEnabled() && !hasFeature)
                {
                    raceDefinition.FeatureUnlocks.Add(new FeatureUnlockByLevel(featureDefinition, 1));
                }
            }

            raceDefinition.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }
    }

    private static void SwitchHumanBackgroundAsiFeatures(bool enableBackgroundAsi)
    {
        if (!TryGetDefinition<CharacterRaceDefinition>("Human", out var human))
        {
            return;
        }

        RemoveMatchingFeature(human.FeatureUnlocks, FeatureDefinitionAttributeModifiers.AttributeModifierHumanAbilityScoreIncrease);
        RemoveMatchingFeature(human.FeatureUnlocks, FeatureDefinitionPointPools.PointPoolAbilityScoreImprovement);
        RemoveMatchingFeature(human.FeatureUnlocks, PointPoolHumanSkillPool);
        RemoveMatchingFeature(human.FeatureUnlocks, HumanOriginFeatFeatureSet);
        RemoveMatchingFeature(human.FeatureUnlocks, HumanOriginFeatPointPool);

        if (enableBackgroundAsi)
        {
            AddFeatureUnlock(human.FeatureUnlocks, PointPoolHumanSkillPool);
            AddFeatureUnlock(human.FeatureUnlocks, HumanOriginFeatFeatureSet);
            AddFeatureUnlock(human.FeatureUnlocks, HumanOriginFeatPointPool);
        }
        else
        {
            if (IsAlternateHumanEffectivelyEnabled())
            {
                AddFeatureUnlock(human.FeatureUnlocks, PointPoolHumanSkillPool);
            }

            FeatureDefinition humanAsiFeature = IsAlternateHumanEffectivelyEnabled()
                ? FeatureDefinitionPointPools.PointPoolAbilityScoreImprovement
                : FeatureDefinitionAttributeModifiers.AttributeModifierHumanAbilityScoreIncrease;

            AddFeatureUnlock(human.FeatureUnlocks, humanAsiFeature);
        }

        human.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private static void SwitchHalfElfVersatileBloodlineBonus(bool enableBackgroundAsi)
    {
        if (!HalfElfVersatileBloodlineFeatureSet)
        {
            return;
        }

        var raceDatabase = DatabaseRepository.GetDatabase<CharacterRaceDefinition>();

        foreach (var raceName in HalfElfVersatileBloodlineRaceNames)
        {
            var raceDefinition = raceDatabase.GetElement(raceName, true);

            if (!raceDefinition)
            {
                continue;
            }

            RemoveMatchingFeature(raceDefinition.FeatureUnlocks, HalfElfVersatileBloodlineFeatureSet);

            if (enableBackgroundAsi)
            {
                AddFeatureUnlock(raceDefinition.FeatureUnlocks, HalfElfVersatileBloodlineFeatureSet);
            }

            raceDefinition.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }
    }

    internal static bool HasDuplicateHumanOriginFeat(RulesetCharacterHero hero, out string featName)
    {
        featName = null;

        if (!TryGetBackgroundFeatForHumanOriginValidation(hero, out var backgroundFeatName))
        {
            return false;
        }

        if (!TryGetHumanOriginSelectionName(hero, out var humanFeatName) &&
            !TryGetHumanOriginTrainedFeatName(hero, out humanFeatName))
        {
            return false;
        }

        if (!IsNonRepeatableOriginFeatDuplicate(humanFeatName, backgroundFeatName))
        {
            return false;
        }

        featName = GetOriginFeatChoiceName(backgroundFeatName);

        return true;
    }

    internal static bool IsDuplicateHumanOriginFeatChoice(
        RulesetCharacterHero hero,
        string tag,
        string featName)
    {
        return IsHumanOriginFeatTag(tag) &&
               TryGetBackgroundFeatForHumanOriginValidation(hero, out var backgroundFeatName) &&
               IsNonRepeatableOriginFeatDuplicate(featName, backgroundFeatName);
    }

    private static bool TryGetBackgroundFeatForHumanOriginValidation(
        RulesetCharacterHero hero,
        out string featName)
    {
        featName = null;

        return _backgroundOptionsLoaded &&
               IsBackgroundBonusFeatsEnabled() &&
               hero?.RaceDefinition?.Name == "Human" &&
               hero.BackgroundDefinition != null &&
               TryGetEffectiveBackgroundFeatName(hero, null, out featName);
    }

    private static bool IsNonRepeatableOriginFeatDuplicate(string featName, string backgroundFeatName)
    {
        return AreEquivalentOriginFeatNames(featName, backgroundFeatName) &&
               (!TryResolveModeAwareFeatDefinition(featName, out var featDefinition) ||
                !SkillFeats.IsRepeatable(featDefinition));
    }

    internal static bool IsHumanOriginFeatSelectionFeature(FeatureDefinition feature)
    {
        return _backgroundOptionsLoaded && IsBackgroundAsiEnabled() && feature == HumanOriginFeatFeatureSet;
    }

    internal static bool IsHumanOriginFeatChoiceFeature(FeatureDefinition feature)
    {
        return _backgroundOptionsLoaded &&
               IsBackgroundAsiEnabled() &&
               HumanOriginFeatFeatureSet?.FeatureSet.Contains(feature) == true;
    }

    internal static bool IsBackgroundAsiSelectionFeature(FeatureDefinition feature)
    {
        return _backgroundOptionsLoaded &&
               IsBackgroundAsiEnabled() &&
               feature is FeatureDefinitionFeatureSet featureSet &&
               BackgroundAsiFeatureLookup.Contains(featureSet);
    }

    internal static bool IsHalfElfVersatileBloodlineSelectionFeature(FeatureDefinition feature)
    {
        return _backgroundOptionsLoaded &&
               IsBackgroundAsiEnabled() &&
               feature == HalfElfVersatileBloodlineFeatureSet;
    }

    internal static bool TryGetHalfElfVersatileBloodlineInspectionDisplayFeature(
        FeatureDefinition sourceFeature,
        out FeatureDefinitionFeatureSet parentFeature,
        out FeatureDefinition selectedFeature)
    {
        parentFeature = null;
        selectedFeature = null;

        if (!_backgroundOptionsLoaded ||
            !HalfElfVersatileBloodlineFeatureSet ||
            !HalfElfVersatileBloodlineFeatureSet.FeatureSet.Contains(sourceFeature))
        {
            return false;
        }

        parentFeature = HalfElfVersatileBloodlineFeatureSet;
        selectedFeature = sourceFeature;

        return true;
    }

    internal static bool IsPersistedCharacterCreationFeatureSetSelection(FeatureDefinition feature)
    {
        return IsBackgroundAsiSelectionFeature(feature) ||
               IsHalfElfVersatileBloodlineSelectionFeature(feature) ||
               IsBackgroundOriginFeatChoicesEnabled() &&
               BackgroundOriginFeatFeatures.ContainsValue(feature as FeatureDefinitionFeatureSet);
    }

    internal static bool TryGetHumanOriginFeatLearnStepTitle(
        HeroDefinitions.PointsPoolType poolType,
        string tag,
        out string title)
    {
        title = null;

        if (!_backgroundOptionsLoaded ||
            !IsBackgroundAsiEnabled() ||
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
            HumanOriginFeatFeatureSet?.FeatureSet.Contains(choiceFeature) != true ||
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
            if (TryGetReservedHeroBuildingData(hero, out var heroBuildingData))
            {
                ClearHumanOriginFeatTraining(heroBuildingData);
                SyncHumanOriginFeatPools(heroBuildingData);
            }
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

    internal static void ClearHumanOriginFeatSelection(RulesetCharacterHero hero)
    {
        if (hero != null)
        {
            HumanOriginFeatSelections.Remove(GetHumanOriginSelectionKey(hero));
        }
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

    internal static bool ShouldSuppressReplacedBackgroundFeatPointPool(
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinition feature)
    {
        var hero = heroBuildingData?.HeroCharacter;

        if (!_backgroundOptionsLoaded ||
            !IsBackgroundBonusFeatsEnabled() ||
            hero?.BackgroundDefinition == null ||
            !TryGetBackgroundOriginFeatNameFromTag(feature?.Name, out var activeFeatName))
        {
            return false;
        }

        var match = GetRecordedBackgroundFeatMatch(hero, heroBuildingData, out var recordedFeatName);

        return match == RecordedBackgroundFeatMatch.Ambiguous ||
               match == RecordedBackgroundFeatMatch.Unique &&
               !AreEquivalentOriginFeatNames(recordedFeatName, activeFeatName);
    }

    internal static void RemoveHumanOriginFeatPointPool(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return;
        }

        ClearHumanOriginFeatTraining(heroBuildingData);
        RemoveHumanOriginFeatActivePool(heroBuildingData);
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

        if (!TryGetReservedHeroBuildingData(hero, out var heroBuildingData) ||
            !heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack) ||
            !pointPoolStack.ActivePools.ContainsKey(HumanOriginFeatTag) ||
            !heroBuildingData.LevelupTrainedFeats.TryGetValue(HumanOriginFeatTag, out var trainedFeats))
        {
            return false;
        }

        var trainedFeat = trainedFeats.FirstOrDefault(feat =>
            feat && IsHumanOriginFeatChoiceName(feat.Name));

        if (!trainedFeat)
        {
            return false;
        }

        featName = GetCanonicalTabletopFeatName(trainedFeat.Name);

        return true;
    }

    private static bool IsHumanOriginFeatChoiceName(string featName)
    {
        var canonicalFeatName = GetOriginFeatChoiceName(featName);

        return !string.IsNullOrEmpty(canonicalFeatName) &&
               Tabletop2024OriginFeatNames.Contains(canonicalFeatName);
    }

    private static bool TryGetHumanOriginFeatNameFromBuildingOrSelection(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData,
        out string featName)
    {
        featName = null;

        if (buildingData?.LevelupTrainedFeats.TryGetValue(HumanOriginFeatTag, out var trainedFeats) == true)
        {
            var trainedFeatName = trainedFeats?
                .Where(feat => feat != null)
                .Select(feat => GetCanonicalTabletopFeatName(feat.Name))
                .FirstOrDefault(name => IsHumanOriginFeatChoiceName(name));

            if (!string.IsNullOrEmpty(trainedFeatName))
            {
                featName = trainedFeatName;

                return true;
            }
        }

        if (hero == null ||
            !HumanOriginFeatSelections.TryGetValue(GetHumanOriginSelectionKey(hero), out var selectedFeatName) ||
            !IsHumanOriginFeatChoiceName(selectedFeatName))
        {
            return false;
        }

        featName = GetCanonicalTabletopFeatName(selectedFeatName);

        return true;
    }

    internal static bool IsHumanOriginFeatTag(string tag)
    {
        return tag == HumanOriginFeatTag;
    }

    internal static bool IsBackgroundOriginFeatTag(string tag)
    {
        return TryGetBackgroundOriginFeatNameFromTag(tag, out _);
    }

    private static bool TryGetBackgroundOriginFeatNameFromTag(string tag, out string featName)
    {
        return TryGetOriginFeatNameFromMarker(tag, BackgroundFeatPointPoolPrefix, out featName);
    }

    private static bool TryGetOriginFeatNameFromMarker(string markerName, string prefix, out string featName)
    {
        featName = null;

        if (string.IsNullOrEmpty(markerName) ||
            !markerName.StartsWith(prefix, System.StringComparison.Ordinal))
        {
            return false;
        }

        var sourceFeatName = markerName.Substring(prefix.Length);
        if (!Tabletop2024OriginFeatNames.Contains(GetOriginFeatChoiceName(sourceFeatName)) ||
            !TryGetDefinition<FeatDefinition>(sourceFeatName, out _))
        {
            return false;
        }

        featName = sourceFeatName;

        return true;
    }

    internal static bool ShouldUseHumanOriginFeat(CharacterHeroBuildingData heroBuildingData)
    {
        return IsValidHumanOriginFeatSelectionHero(heroBuildingData?.HeroCharacter);
    }

    internal static bool SyncHumanOriginFeatPools(CharacterHeroBuildingData heroBuildingData)
    {
        if (heroBuildingData == null)
        {
            return false;
        }

        var changed = false;

        if (!ShouldUseHumanOriginFeat(heroBuildingData))
        {
            ClearPendingFeatSelection(heroBuildingData.HeroCharacter, HumanOriginFeatTag);
            ClearHumanOriginFeatTraining(heroBuildingData);
            changed |= RemoveHumanOriginFeatActivePool(heroBuildingData);

            return changed;
        }

        changed |= EnsureHumanOriginFeatActivePool(heroBuildingData);

        return changed;
    }

    private static bool TryGetHumanOriginSelectionName(RulesetCharacterHero hero, out string featName)
    {
        featName = null;

        return IsValidHumanOriginFeatSelectionHero(hero) &&
               HumanOriginFeatSelections.TryGetValue(GetHumanOriginSelectionKey(hero), out featName) &&
               Tabletop2024OriginFeatNames.Contains(featName);
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
            if (!TryGetHumanOriginFeatName(featureSet, out var featName))
            {
                continue;
            }

            featNames.Add(featName);
        }
    }

    private static bool IsValidHumanOriginFeatSelectionHero(RulesetCharacterHero hero)
    {
        return _backgroundOptionsLoaded &&
               IsBackgroundAsiEnabled() &&
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
            !heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack))
        {
            return false;
        }

        var restrictedChoices = TryGetHumanOriginSelectionName(heroBuildingData.HeroCharacter, out var featName)
            ? [featName]
            : HumanOriginFeatPointPool.RestrictedChoices.ToList();
        var pool = new PointPool(
            HumanOriginFeatPointPool.poolAmount,
            restrictedChoices,
            HumanOriginFeatPointPool.UniqueChoices)
        {
            Description = HumanOriginFeatPointPool.GuiPresentation.Description
        };

        NormalizeModeAwareFeatPointPool(pool);

        if (pointPoolStack.ActivePools.TryGetValue(HumanOriginFeatTag, out var existingPool))
        {
            if (existingPool.RestrictedChoices.SequenceEqual(pool.RestrictedChoices))
            {
                return false;
            }

            existingPool.RestrictedChoices.Clear();
            existingPool.RestrictedChoices.AddRange(pool.RestrictedChoices);

            return true;
        }

        pointPoolStack.ActivePools.Add(HumanOriginFeatTag, pool);

        return true;
    }

    private static bool RemoveHumanOriginFeatActivePool(CharacterHeroBuildingData heroBuildingData)
    {
        return heroBuildingData?.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack) ==
               true &&
               pointPoolStack.ActivePools.Remove(HumanOriginFeatTag);
    }

    internal static void SwitchBackgroundBonusFeats()
    {
        if (!_backgroundOptionsLoaded)
        {
            return;
        }

        var enableBackgroundBonusFeats = IsBackgroundBonusFeatsEnabled();

        foreach (var backgroundName in BackgroundFeatSets.Keys)
        {
            if (!TryGetBackgroundFeatName(backgroundName, out var featName) ||
                !TryGetDefinition<CharacterBackgroundDefinition>(backgroundName, out var background) ||
                !BackgroundBonusGrantFeatures.TryGetValue(featName, out var grantedFeature))
            {
                continue;
            }

            BackgroundBonusDisplayFeatures.TryGetValue(featName, out var displayFeature);
            BackgroundSkillFeatures.TryGetValue(backgroundName, out var skillFeature);
            var defaultChoice = BackgroundOriginFeatChoiceFeatures[GetBackgroundOriginFeatChoiceName(backgroundName, featName)];
            BackgroundStoryFeatures.TryGetValue(backgroundName, out var storyFeature);

            CaptureOriginalBackgroundFeatures(backgroundName, background);
            RemoveManagedBackgroundBonusFeatures(
                background,
                storyFeature);
            RestoreOriginalBackgroundFeatures(backgroundName, background);

            if (!enableBackgroundBonusFeats)
            {
                continue;
            }

            background.Features.RemoveAll(IsSuppressedBackground2024Feature);

            var insertIndex = GetBackgroundFeatureInsertIndex(backgroundName, background);
            var featuresToInsert = new List<FeatureDefinition>();

            if (IsBackgroundOriginFeatChoicesEnabled())
            {
                featuresToInsert.Add(BackgroundOriginFeatFeatures[backgroundName]);
            }
            else
            {
                AddBackgroundFeaturesIfValid(featuresToInsert, grantedFeature, displayFeature);
            }

            AddBackgroundFeaturesIfValid(featuresToInsert, skillFeature);

            if (!IsBackgroundOriginFeatChoicesEnabled())
            {
                AddBackgroundFeaturesIfValid(featuresToInsert,
                    defaultChoice.FeatureSet.OfType<FeatureDefinitionProficiency>().Single());
            }

            AddBackgroundFeaturesIfValid(featuresToInsert, storyFeature);

            background.Features.InsertRange(insertIndex, featuresToInsert);
        }
    }

    private static void AddBackgroundFeaturesIfValid(
        List<FeatureDefinition> features,
        params FeatureDefinition[] featuresToAdd)
    {
        foreach (var feature in featuresToAdd)
        {
            if (feature)
            {
                features.Add(feature);
            }
        }
    }

    private static FeatureDefinitionFeatureSet BuildBackgroundAsiFeatureSet(
        string backgroundName,
        string attrA,
        string attrB,
        string attrC)
    {
        string FormatTitle() => Gui.Format("Feature/&FeatureSetBackgroundASITitle",
            GetAbilityAbbreviation(attrA), GetAbilityAbbreviation(attrB), GetAbilityAbbreviation(attrC));
        string FormatPlusOneTitle() => Gui.Format("Feature/&FeatureSetBackgroundASI111Title",
            GetAbilityAbbreviation(attrA), GetAbilityAbbreviation(attrB), GetAbilityAbbreviation(attrC));
        string FormatPlusOneDescription() => Gui.Format("Feature/&FeatureSetBackgroundASI111Description",
            GetAbilityAbbreviation(attrA), GetAbilityAbbreviation(attrB), GetAbilityAbbreviation(attrC));

        var plusOneA = BuildAttributeModifier(backgroundName, attrA, 1);
        var plusOneB = BuildAttributeModifier(backgroundName, attrB, 1);
        var plusOneC = BuildAttributeModifier(backgroundName, attrC, 1);
        var plusTwoA = BuildAttributeModifier(backgroundName, attrA, 2);
        var plusTwoB = BuildAttributeModifier(backgroundName, attrB, 2);
        var plusTwoC = BuildAttributeModifier(backgroundName, attrC, 2);

        var plusOneSet = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSetBackgroundASI_{backgroundName}_PlusOneEach")
            .SetGuiPresentation(FormatPlusOneTitle(), FormatPlusOneDescription())
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Union)
            .AddFeatureSet(plusOneA, plusOneB, plusOneC)
            .AddCustomSubFeatures(new FormattedDefinitionText(FormatPlusOneTitle, FormatPlusOneDescription))
            .AddToDB();

        var plusTwoChoiceA = BuildBackgroundAsiChoiceSet(backgroundName, attrA, attrB, attrC, plusTwoA);
        var plusTwoChoiceB = BuildBackgroundAsiChoiceSet(backgroundName, attrB, attrA, attrC, plusTwoB);
        var plusTwoChoiceC = BuildBackgroundAsiChoiceSet(backgroundName, attrC, attrA, attrB, plusTwoC);

        return FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSetBackgroundASI_{backgroundName}")
            .SetGuiPresentation(FormatTitle(), "Feature/&FeatureSetBackgroundASIDescription")
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
            .AddFeatureSet(plusTwoChoiceA, plusTwoChoiceB, plusTwoChoiceC, plusOneSet)
            .AddCustomSubFeatures(new FormattedDefinitionText(title: FormatTitle))
            .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildBackgroundAsiChoiceSet(
        string backgroundName,
        string plusTwoAttribute,
        string optionA,
        string optionB,
        FeatureDefinitionAttributeModifier plusTwoFeature)
    {
        string FormatTitle() => Gui.Format("Feature/&FeatureSetBackgroundASI2Plus1Title",
            GetAbilityAbbreviation(plusTwoAttribute), GetAbilityAbbreviation(optionA), GetAbilityAbbreviation(optionB));
        string FormatDescription() => Gui.Format("Feature/&FeatureSetBackgroundASI2Plus1Description",
            GetAbilityAbbreviation(plusTwoAttribute), GetAbilityAbbreviation(optionA), GetAbilityAbbreviation(optionB));

        var pointPool = FeatureDefinitionPointPoolBuilder
            .Create($"PointPoolBackgroundASI_{backgroundName}_{plusTwoAttribute}")
            .SetGuiPresentationNoContent(true)
            .SetPool(HeroDefinitions.PointsPoolType.AbilityScore, 1)
            .RestrictChoices(optionA, optionB)
            .AddToDB();

        return FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSetBackgroundASI_{backgroundName}_{plusTwoAttribute}")
            .SetGuiPresentation(FormatTitle(), FormatDescription())
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Union)
            .AddFeatureSet(plusTwoFeature, pointPool)
            .AddCustomSubFeatures(new FormattedDefinitionText(FormatTitle, FormatDescription))
            .AddToDB();
    }

    private static FeatureDefinitionAttributeModifier BuildAttributeModifier(string backgroundName, string attribute, int amount)
    {
        var name = $"AttributeModifierBackgroundASI_{backgroundName}_{attribute}_{amount}";

        if (TryGetDefinition<FeatureDefinitionAttributeModifier>(name, out var attributeModifier))
        {
            return attributeModifier;
        }

        return FeatureDefinitionAttributeModifierBuilder
            .Create(name)
            .SetGuiPresentationNoContent(true)
            .SetModifier(AttributeModifierOperation.Additive, attribute, amount)
            .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildHalfElfVersatileBloodlineFeatureSet()
    {
        var savingThrowAffinities = HalfElfVersatileBloodlineSavingThrowAttributes
            .Select(BuildHalfElfVersatileBloodlineSavingThrowAffinity)
            .ToArray();

        SavingThrowAffinityHalfElfVersatileBloodlineDexterity2024 = savingThrowAffinities[0];
        SavingThrowAffinityHalfElfVersatileBloodlineIntelligence2024 = savingThrowAffinities[1];
        SavingThrowAffinityHalfElfVersatileBloodlineWisdom2024 = savingThrowAffinities[2];
        SavingThrowAffinityHalfElfVersatileBloodlineCharisma2024 = savingThrowAffinities[3];

        return FeatureDefinitionFeatureSetBuilder
            .Create(HalfElfVersatileBloodlineFeatureSetName)
            .SetGuiPresentation(Category.Feature)
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
            .AddFeatureSet(
                SavingThrowAffinityHalfElfVersatileBloodlineDexterity2024,
                SavingThrowAffinityHalfElfVersatileBloodlineIntelligence2024,
                SavingThrowAffinityHalfElfVersatileBloodlineWisdom2024,
                SavingThrowAffinityHalfElfVersatileBloodlineCharisma2024)
            .AddToDB();
    }

    private static FeatureDefinitionSavingThrowAffinity BuildHalfElfVersatileBloodlineSavingThrowAffinity(
        string attribute)
    {
        return FeatureDefinitionSavingThrowAffinityBuilder
            .Create($"{HalfElfVersatileBloodlineFeaturePrefix}{attribute}2024")
            .SetGuiPresentation(Category.Feature)
            .SetModifiers(FeatureDefinitionSavingThrowAffinity.ModifierType.AddDice, DieType.D1, 2, false, attribute)
            .AddToDB();
    }

    private static FeatureDefinition BuildOriginFeatPointPool(string featName)
    {
        return FeatureDefinitionPointPoolBuilder
            .Create($"{BackgroundFeatPointPoolPrefix}{featName}")
            .SetGuiPresentationNoContent(true)
            .SetPool(HeroDefinitions.PointsPoolType.Feat, 1)
            .RestrictChoices(featName)
            .AddToDB();
    }

    private static FeatureDefinitionPointPool BuildHumanOriginFeatPointPool()
    {
        var choices = Tabletop2024OriginFeatNames
            .Select(GetOriginFeatDefinition)
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
        foreach (var featName in Tabletop2024OriginFeatNames)
        {
            BuildHumanOriginFeatChoiceFeatureSet(featName);
        }

        var choices = Tabletop2024OriginFeatNames
            .Select(featName => HumanOriginFeatChoiceFeatures.TryGetValue(featName, out var choiceFeature)
                ? choiceFeature
                : null)
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
        var featDefinition = GetOriginFeatDefinition(featName);

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
            .AddCustomSubFeatures(new FormattedDefinitionText(
                () => GetOriginFeatDefinition(featName)?.FormatTitle(),
                () => GetOriginFeatDefinition(featName)?.FormatDescription()))
            .AddToDB();

        HumanOriginFeatChoiceFeatures[featName] = choiceFeature;

        return choiceFeature;
    }

    internal static string FormatOriginFeatGainDescription(BaseDefinition featDefinition)
    {
        return FormatOriginFeatGainDescription(featDefinition?.FormatTitle());
    }

    internal static string FormatOriginFeatGainDescription(string featTitle)
    {
        return Gui.Format("Feature/&OriginFeatGainDescription", featTitle);
    }

    private static FeatureDefinition BuildOriginFeatDisplayFeature(string featName)
    {
        TryResolveModeAwareFeatDefinition(featName, out var featDefinition);

        string FormatDescription()
        {
            TryResolveModeAwareFeatDefinition(featName, out var currentFeatDefinition);

            return FormatOriginFeatGainDescription(currentFeatDefinition);
        }

        return FeatureDefinitionBuilder
            .Create($"{BackgroundFeatDisplayPrefix}{featName}")
            .SetGuiPresentation(
                "Feature/&BackgroundBonusFeatTitle",
                FormatDescription(),
                featDefinition)
            .AddCustomSubFeatures(new FormattedDefinitionText(description: FormatDescription))
            .AddToDB();
    }

    private static bool IsHumanOriginInspectionSourceFeature(FeatureDefinition sourceFeature)
    {
        return sourceFeature != null &&
               (sourceFeature == HumanOriginFeatFeatureSet ||
                sourceFeature is FeatureDefinitionFeatureSet featureSet &&
                HumanOriginFeatChoiceFeatures.ContainsValue(featureSet));
    }

    private static FeatureDefinition BuildBackground2024SkillProficiency(
        string backgroundName,
        string skillA,
        string skillB)
    {
        string FormatDescription() => Gui.Format("Feature/&Background2024SkillsDescription",
            GetDefinition<SkillDefinition>(skillA).FormatTitle(),
            GetDefinition<SkillDefinition>(skillB).FormatTitle());

        return FeatureDefinitionProficiencyBuilder
            .Create($"ProficiencyBackground2024_{backgroundName}_Skills")
            .SetGuiPresentation("Feature/&Background2024SkillsTitle", FormatDescription())
            .SetProficiencies(ProficiencyType.Skill, skillA, skillB)
            .AddCustomSubFeatures(new FormattedDefinitionText(description: FormatDescription))
            .AddToDB();
    }

    private static string GetBackgroundOriginFeatChoiceName(string backgroundName, string featName)
    {
        return $"FeatureSetBackgroundOriginFeat_{backgroundName}_{featName}";
    }

    private static FeatureDefinitionFeatureSet BuildBackgroundOriginFeatFeatureSet(string backgroundName)
    {
        foreach (var choice in BackgroundFeatSets[backgroundName])
        {
            BuildBackgroundOriginFeatChoice(backgroundName, choice.Feat, choice.Tool);
        }

        return FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSetBackgroundOriginFeat_{backgroundName}")
            .SetGuiPresentation("Feature/&BackgroundBonusFeatTitle", string.Empty)
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
            .AddToDB();
    }

    internal static bool TryFormatBackgroundOriginFeatChoiceDescription(
        FeatureDefinition feature,
        FeatureDefinition choice,
        out string description)
    {
        description = null;

        if (feature is not FeatureDefinitionFeatureSet featureSet ||
            !BackgroundOriginFeatFeatures.ContainsValue(featureSet) ||
            choice is not FeatureDefinitionFeatureSet choiceSet ||
            !featureSet.FeatureSet.Contains(choiceSet))
        {
            return false;
        }

        description = choiceSet.FeatureSet.OfType<FeatureDefinitionProficiency>().Single().FormatDescription();

        return true;
    }

    private static FeatDefinition GetOriginFeatDefinition(string featName)
    {
        return IndependentTabletopFeatByCanonicalName.TryGetValue(GetCanonicalTabletopFeatName(featName), out var feat)
            ? feat
            : GetDefinition<FeatDefinition>(featName);
    }

    private static void BuildBackgroundOriginFeatChoice(string backgroundName, string featName, string toolName)
    {
        var name = GetBackgroundOriginFeatChoiceName(backgroundName, featName);

        if (BackgroundOriginFeatChoiceFeatures.ContainsKey(name))
        {
            return;
        }

        var toolFeature = BuildBackground2024ToolProficiency(backgroundName, toolName);

        var featDefinition = GetOriginFeatDefinition(featName);
        string FormatTitle() => featDefinition.FormatTitle();
        string FormatDescription() => featDefinition.FormatDescription();

        BackgroundOriginFeatChoiceFeatures[name] = FeatureDefinitionFeatureSetBuilder
            .Create(name)
            .SetGuiPresentation(FormatTitle(), FormatDescription(), featDefinition)
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Union)
            .AddFeatureSet(BackgroundBonusGrantFeatures[featName], toolFeature)
            .AddCustomSubFeatures(new FormattedDefinitionText(FormatTitle, FormatDescription))
            .AddToDB();
    }

    private static FeatureDefinitionProficiency BuildBackground2024ToolProficiency(
        string backgroundName,
        string toolName)
    {
        var name = $"ProficiencyBackgroundOriginFeat_{backgroundName}_{toolName}";

        if (TryGetDefinition<FeatureDefinitionProficiency>(name, out var toolFeature))
        {
            return toolFeature;
        }

        string FormatDescription() => Gui.Format("Feature/&Background2024ToolDescription",
            GetDefinition<ToolTypeDefinition>(toolName).FormatTitle());

        return FeatureDefinitionProficiencyBuilder
            .Create(name)
            .SetGuiPresentation("Feature/&Background2024ToolTitle", FormatDescription())
            .SetProficiencies(ProficiencyType.Tool, toolName)
            .AddCustomSubFeatures(new FormattedDefinitionText(description: FormatDescription))
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

        var flexibleBackgroundsEnabled = IsFlexibleBackgroundsEffectivelyEnabled();
        var insertIndex = GetBackgroundFeatureInsertIndex(backgroundName, background);

        foreach (var feature in originalFeatures.Where(feature =>
                     flexibleBackgroundsEnabled || !FlexibleBackgroundsContext.IsFlexibleBackgroundFeature(feature)))
        {
            var featureIndex = background.Features.IndexOf(feature);

            if (featureIndex >= insertIndex)
            {
                insertIndex = featureIndex + 1;
                continue;
            }

            if (featureIndex >= 0)
            {
                background.Features.RemoveAt(featureIndex);
                insertIndex--;
            }

            // Restore the original display order before flexible grants are reconciled.
            background.Features.Insert(insertIndex++, feature);
        }
    }

    private static void RemoveManagedBackgroundBonusFeatures(
        CharacterBackgroundDefinition background,
        FeatureDefinition storyFeature)
    {
        background.Features.RemoveAll(feature =>
            IsManagedBackgroundBonusFeature(feature) ||
            feature == storyFeature);
    }

    private static bool IsManagedBackgroundBonusFeature(FeatureDefinition feature)
    {
        return BackgroundBonusGrantFeatures.ContainsValue(feature) ||
               BackgroundBonusDisplayFeatures.ContainsValue(feature) ||
               BackgroundOriginFeatFeatures.ContainsValue(feature as FeatureDefinitionFeatureSet) ||
               BackgroundSkillFeatures.ContainsValue(feature) ||
               BackgroundOriginFeatChoiceFeatures.Values.Any(choice => choice.FeatureSet.Contains(feature));
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

    private static void AddFeatureUnlock([NotNull] List<FeatureUnlockByLevel> unlocks, FeatureDefinition feature)
    {
        if (!feature || unlocks.Exists(unlock => unlock.FeatureDefinition == feature))
        {
            return;
        }

        unlocks.Add(new FeatureUnlockByLevel(feature, 1));
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
