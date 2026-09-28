using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly Dictionary<FeatureDefinition, FeatureDefinition> ClericDomainProficiencyReplacements = [];
    private static readonly List<(CharacterSubclassDefinition Subclass, FeatureUnlockByLevel Unlock,
        FeatureDefinition Original, int Level)> ClericDomainProficiencyUnlocks = [];
    private static bool _clericDomainProficienciesLoaded;

    private static void SwitchClericDomainProficiencies()
    {
        if (!_clericDomainProficienciesLoaded)
        {
            foreach (var subclass in ClericDomains)
            {
                foreach (var unlock in subclass.FeatureUnlocks)
                {
                    var original = unlock.FeatureDefinition;
                    var replacement = FilterClericDomainProficiencies(original);

                    if (original != replacement)
                    {
                        ClericDomainProficiencyUnlocks.Add((subclass, unlock, original, unlock.Level));
                    }
                }
            }

            _clericDomainProficienciesLoaded = true;
        }

        foreach (var (subclass, unlock, original, level) in ClericDomainProficiencyUnlocks)
        {
            subclass.FeatureUnlocks.Remove(unlock);
            unlock.featureDefinition = Main.Settings.EnableClericDivineOrder2024
                ? ClericDomainProficiencyReplacements[original]
                : original;
            unlock.level = GetClericDomainFeatureLevel(level);

            if (unlock.FeatureDefinition != null)
            {
                subclass.FeatureUnlocks.Add(unlock);
            }

            subclass.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }

        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return;
        }

        foreach (var character in characterService.PartyCharacters)
        {
            if (character.RulesetCharacter is RulesetCharacterHero hero)
            {
                UpdateClericDomainProficiencies(hero);
                hero.RefreshProficiencies();
            }
        }
    }

    private static int GetClericDomainFeatureLevel(int level)
    {
        return Main.Settings.EnableClericToLearnDomainAtLevel3 && level < 3 ? 3 : level;
    }

    private static FeatureDefinition FilterClericDomainProficiencies(FeatureDefinition feature)
    {
        if (ClericDomainProficiencyReplacements.TryGetValue(feature, out var replacement))
        {
            return replacement;
        }

        // Seed before walking children: a shared or cyclic graph must not be traversed repeatedly.
        ClericDomainProficiencyReplacements.Add(feature, feature);

        if (feature is FeatureDefinitionProficiency
            { ProficiencyType: ProficiencyType.Armor or ProficiencyType.Weapon })
        {
            replacement = null;
        }
        else if (feature is FeatureDefinitionFeatureSet featureSet)
        {
            var children = featureSet.FeatureSet.Select(FilterClericDomainProficiencies)
                .Where(child => child != null).ToArray();

            if (children.SequenceEqual(featureSet.FeatureSet))
            {
                return feature;
            }

            var subFeatures = featureSet.GetCustomSubFeatures();

            if (children.Length == 0 && subFeatures.Count == 0)
            {
                replacement = null;
            }
            else if (children.Length == 1 && subFeatures.Count == 0 &&
                     featureSet.Mode == FeatureDefinitionFeatureSet.FeatureSetMode.Union)
            {
                replacement = children[0];
            }
            else
            {
                // Clone only the affected path, preserving shared definitions and unrelated skill/tool grants.
                replacement = FeatureDefinitionFeatureSetBuilder
                    .Create(featureSet, featureSet.Name + "DivineOrder2024")
                    .SetFeatureSet(children)
                    .AddCustomSubFeatures(subFeatures.ToArray())
                    .AddCustomSubFeatures(new FormattedDefinitionText(description: () =>
                        string.Join("\n\n", children.Select(child => child.FormatDescription()))))
                    .AddToDB();
            }
        }
        else
        {
            return feature;
        }

        ClericDomainProficiencyReplacements[feature] = replacement;
        return replacement;
    }

    internal static void UpdateClericDomainProficiencies(RulesetCharacterHero hero)
    {
        if (!hero.ClassesAndSubclasses.TryGetValue(Cleric, out var subclass))
        {
            return;
        }

        var classLevel = hero.GetClassLevel(Cleric);
        var tags = Enumerable.Range(1, classLevel)
            .Select(level => AttributeDefinitions.GetSubclassTag(Cleric, level, subclass))
            .ToArray();

        foreach (var (_, _, original, level) in ClericDomainProficiencyUnlocks
                     .Where(entry => entry.Subclass == subclass))
        {
            var replacement = ClericDomainProficiencyReplacements[original];
            var featureLevel = GetClericDomainFeatureLevel(level);
            var desired = Main.Settings.EnableClericDivineOrder2024 ? replacement : original;

            foreach (var tag in tags)
            {
                if (hero.ActiveFeatures.TryGetValue(tag, out var features))
                {
                    features.RemoveAll(feature => feature == original || feature == replacement);
                }
            }

            if (desired == null || classLevel < featureLevel)
            {
                continue;
            }

            var activeTag = AttributeDefinitions.GetSubclassTag(Cleric, featureLevel, subclass);

            if (!hero.ActiveFeatures.TryGetValue(activeTag, out var activeFeatures))
            {
                activeFeatures = [];
                hero.ActiveFeatures.Add(activeTag, activeFeatures);
            }

            activeFeatures.TryAdd(desired);
        }
    }
}
