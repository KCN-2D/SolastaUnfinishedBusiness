using System;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

internal static class PointPoolContext
{
    internal static bool IsSpellSelectionPool(HeroDefinitions.PointsPoolType poolType)
    {
        return poolType is HeroDefinitions.PointsPoolType.Cantrip or
            HeroDefinitions.PointsPoolType.Spell or HeroDefinitions.PointsPoolType.CantripOrSpell;
    }

    internal static string GetPoolTag(
        HeroDefinitions.PointsPoolType poolType,
        string featureTag,
        string extraSpellsTag)
    {
        return IsSpellSelectionPool(poolType) ? featureTag + extraSpellsTag : featureTag;
    }

    internal static void ApplyFeaturePool(
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinitionPointPool feature,
        string featureTag)
    {
        var pools = heroBuildingData.PointPoolStacks[feature.PoolType].ActivePools;
        var poolTag = GetPoolTag(feature.PoolType, featureTag, feature.ExtraSpellsTag);

        // Refreshing an already granted feature must preserve its choices and remaining points.
        // The native check used featureTag but inserted poolTag, missing every tagged duplicate.
        if (pools.ContainsKey(poolTag))
        {
            return;
        }

        var pool = new PointPool(feature.PoolAmount, feature.RestrictedChoices, feature.UniqueChoices)
        {
            Description = feature.GuiPresentation.Description
        };

        if (IsSpellSelectionPool(feature.PoolType))
        {
            pool.SpellListOverride = feature.SpellListOverride;
            pool.MinSpellLevel = feature.MinSpellLevel;
            pool.MaxSpellLevel = feature.MaxSpellLevel;
            pool.ExtraSpellsTag = feature.ExtraSpellsTag;
            pool.RitualOnly = feature.RitualOnly;
        }

        pools.Add(poolTag, pool);
    }

    internal static void RemovePoolsOfTag(CharacterHeroBuildingData heroBuildingData, string featureTag)
    {
        foreach (var stack in heroBuildingData.PointPoolStacks)
        {
            var pools = stack.Value.ActivePools;

            // Resolve ownership from each pool's own suffix. A global tag catalog cannot
            // account for mod features, and prefix matching confuses class levels 1 and 15.
            var matchingTags = pools.Where(entry =>
                    string.Equals(entry.Key, featureTag, StringComparison.Ordinal) ||
                    string.Equals(entry.Key,
                        GetPoolTag(stack.Key, featureTag, entry.Value.ExtraSpellsTag), StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var poolTag in matchingTags)
            {
                pools.Remove(poolTag);
            }
        }
    }
}
