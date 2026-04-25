using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.FightingStyles;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses;
using TA;
using static RuleDefinitions;
using static FeatureDefinitionCastSpell;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterBuildingManagerPatcher
{
    internal static bool TryResolveFeatGrantedPointPoolTags(
        CharacterBuildingManager manager,
        RulesetCharacterHero hero,
        string extraSpellsTag,
        out string classTag,
        out string applyTag,
        out string activePoolTag)
    {
        classTag = null;
        applyTag = null;
        activePoolTag = null;

        if (manager == null ||
            hero == null ||
            string.IsNullOrEmpty(extraSpellsTag))
        {
            return false;
        }

        manager.GetLastAssignedClassAndLevel(hero, out var classDefinition, out var level);

        if (!classDefinition)
        {
            classDefinition = LevelUpHelper.GetSelectedClass(hero);

            if (classDefinition)
            {
                level = hero.ClassesHistory.Count(x => x == classDefinition);

                if (level <= 0)
                {
                    level = 1;
                }
            }
        }

        if (!classDefinition && hero.ClassesHistory is { Count: > 0 })
        {
            classDefinition = hero.ClassesHistory.Last();
            level = hero.ClassesHistory.Count(x => x == classDefinition);
        }

        if (!classDefinition)
        {
            return false;
        }

        level = System.Math.Max(level, 1);
        classTag = AttributeDefinitions.GetClassTag(classDefinition, level);
        applyTag = classTag + extraSpellsTag;
        activePoolTag = applyTag + extraSpellsTag;

        return !string.IsNullOrEmpty(classTag) &&
               !string.IsNullOrEmpty(applyTag) &&
               !string.IsNullOrEmpty(activePoolTag);
    }

    private static bool TryResolveFeatGrantedPointPoolTags(
        CharacterBuildingManager manager,
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinitionPointPool pointPool,
        out string classTag,
        out string applyTag,
        out string activePoolTag)
    {
        classTag = null;
        applyTag = null;
        activePoolTag = null;

        if (manager == null ||
            heroBuildingData?.HeroCharacter == null ||
            pointPool == null)
        {
            return false;
        }

        return TryResolveFeatGrantedPointPoolTags(
            manager,
            heroBuildingData.HeroCharacter,
            pointPool.ExtraSpellsTag,
            out classTag,
            out applyTag,
            out activePoolTag);
    }

    private static bool EnsureFeatGrantedPointPoolActiveTag(
        CharacterHeroBuildingData heroBuildingData,
        HeroDefinitions.PointsPoolType poolType,
        string expectedActivePoolTag,
        HashSet<string> keysBefore)
    {
        if (heroBuildingData == null ||
            string.IsNullOrEmpty(expectedActivePoolTag) ||
            !heroBuildingData.PointPoolStacks.TryGetValue(poolType, out var pointPoolStack))
        {
            return false;
        }

        if (pointPoolStack.ActivePools.ContainsKey(expectedActivePoolTag))
        {
            return true;
        }

        var addedKeys = pointPoolStack.ActivePools.Keys
            .Where(key => !keysBefore.Contains(key))
            .ToArray();

        if (addedKeys.Length != 1 ||
            !pointPoolStack.ActivePools.TryGetValue(addedKeys[0], out var pointPool))
        {
#if DEBUG
            Main.Log(
                $"EnsureFeatGrantedPointPoolActiveTag: expected={expectedActivePoolTag}, added=[{string.Join(", ", addedKeys)}]");
#endif
            return false;
        }

        pointPoolStack.ActivePools.Remove(addedKeys[0]);
        pointPoolStack.ActivePools[expectedActivePoolTag] = pointPool;

        return true;
    }

    private static bool IsFeatGrantedSpellOrCantripPointPool(FeatureDefinitionPointPool pointPool)
    {
        return pointPool is
        {
            PoolType: HeroDefinitions.PointsPoolType.Cantrip or HeroDefinitions.PointsPoolType.Spell
        } &&
               !string.IsNullOrEmpty(pointPool.ExtraSpellsTag);
    }

    private static bool IsFeatGrantedMetamagicAdeptPointPool(FeatureDefinitionPointPool pointPool)
    {
        return pointPool is { PoolType: HeroDefinitions.PointsPoolType.Metamagic } &&
               string.Equals(
                   pointPool.Name,
                   MetamagicContext.FeatMetamagicAdeptPointPoolTag,
                   StringComparison.Ordinal);
    }

    private static bool TryResolveLastAssignedClassTag(
        CharacterBuildingManager manager,
        CharacterHeroBuildingData heroBuildingData,
        out string classTag)
    {
        classTag = null;

        if (manager == null ||
            heroBuildingData?.HeroCharacter == null)
        {
            return false;
        }

        var hero = heroBuildingData.HeroCharacter;

        manager.GetLastAssignedClassAndLevel(hero, out var classDefinition, out var level);

        if (!classDefinition)
        {
            classDefinition = LevelUpHelper.GetSelectedClass(hero);

            if (classDefinition)
            {
                level = hero.ClassesHistory.Count(x => x == classDefinition);

                if (level <= 0)
                {
                    level = 1;
                }
            }
        }

        if (!classDefinition && hero.ClassesHistory is { Count: > 0 })
        {
            classDefinition = hero.ClassesHistory.Last();
            level = hero.ClassesHistory.Count(x => x == classDefinition);
        }

        if (!classDefinition)
        {
            return false;
        }

        level = Math.Max(level, 1);
        classTag = AttributeDefinitions.GetClassTag(classDefinition, level);

        return !string.IsNullOrEmpty(classTag);
    }

    private sealed class PointPoolSnapshot(PointPool pointPool)
    {
        private readonly string Description = pointPool?.Description;
        private readonly int MaxPoints = pointPool?.maxPoints ?? 0;
        private readonly int RemainingPoints = pointPool?.remainingPoints ?? 0;
        private readonly List<string> RestrictedChoices = pointPool?.RestrictedChoices?.ToList() ?? [];
        private readonly bool UniqueChoices = pointPool?.UniqueChoices ?? false;

        internal PointPool Restore()
        {
            var pointPool = new PointPool(MaxPoints, RestrictedChoices, UniqueChoices)
            {
                Description = Description
            };

            pointPool.maxPoints = MaxPoints;
            pointPool.remainingPoints = RemainingPoints;

            return pointPool;
        }
    }

    private sealed class FeatGrantedMetamagicPoolRollback
    {
        private readonly Dictionary<string, PointPoolSnapshot> ActivePools = [];
        private readonly bool HadPointPoolStack;
        private readonly string[] Tags;

        private FeatGrantedMetamagicPoolRollback(
            CharacterHeroBuildingData heroBuildingData,
            string[] tags)
        {
            Tags = tags;
            HadPointPoolStack = heroBuildingData.PointPoolStacks.TryGetValue(
                HeroDefinitions.PointsPoolType.Metamagic,
                out var pointPoolStack);

            if (HadPointPoolStack)
            {
                foreach (var tag in Tags)
                {
                    if (pointPoolStack.ActivePools.TryGetValue(tag, out var pointPool))
                    {
                        ActivePools[tag] = new PointPoolSnapshot(pointPool);
                    }
                }
            }
        }

        internal static FeatGrantedMetamagicPoolRollback Capture(
            CharacterBuildingManager manager,
            CharacterHeroBuildingData heroBuildingData,
            FeatDefinition feat)
        {
            if (manager == null ||
                heroBuildingData == null ||
                feat == null ||
                !TryResolveLastAssignedClassTag(manager, heroBuildingData, out var classTag))
            {
                return null;
            }

            var tags = feat.Features
                .OfType<FeatureDefinitionPointPool>()
                .Where(IsFeatGrantedMetamagicAdeptPointPool)
                .Select(pointPool => classTag + pointPool.ExtraSpellsTag + pointPool.ExtraSpellsTag)
                .Where(tag => !string.IsNullOrEmpty(tag))
                .ToHashSet(StringComparer.Ordinal);

            if (tags.Count == 0)
            {
                return null;
            }

            return new FeatGrantedMetamagicPoolRollback(heroBuildingData, [.. tags]);
        }

        internal void Restore(CharacterHeroBuildingData heroBuildingData)
        {
            if (heroBuildingData == null)
            {
                return;
            }

            if (heroBuildingData.PointPoolStacks.TryGetValue(
                    HeroDefinitions.PointsPoolType.Metamagic,
                    out var pointPoolStack))
            {
                foreach (var tag in Tags)
                {
                    pointPoolStack.ActivePools.Remove(tag);
                }

                foreach (var entry in ActivePools)
                {
                    pointPoolStack.ActivePools[entry.Key] = entry.Value.Restore();
                }

                if (!HadPointPoolStack && pointPoolStack.ActivePools.Count == 0)
                {
                    heroBuildingData.PointPoolStacks.Remove(HeroDefinitions.PointsPoolType.Metamagic);
                }
            }
            else if (ActivePools.Count > 0)
            {
                pointPoolStack = new PointPoolStack(HeroDefinitions.PointsPoolType.Metamagic);
                heroBuildingData.PointPoolStacks[HeroDefinitions.PointsPoolType.Metamagic] = pointPoolStack;

                foreach (var entry in ActivePools)
                {
                    pointPoolStack.ActivePools[entry.Key] = entry.Value.Restore();
                }
            }
        }
    }

    private static bool TryResolveFeatGrantedMetamagicAdeptPointPoolTags(
        CharacterBuildingManager manager,
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinitionPointPool pointPoolFeature,
        out string applyTag,
        out string activePoolTag)
    {
        applyTag = null;
        activePoolTag = null;

        if (heroBuildingData == null ||
            !IsFeatGrantedMetamagicAdeptPointPool(pointPoolFeature) ||
            !TryResolveLastAssignedClassTag(manager, heroBuildingData, out var classTag))
        {
            return false;
        }

        applyTag = classTag + pointPoolFeature.ExtraSpellsTag;
        activePoolTag = applyTag + pointPoolFeature.ExtraSpellsTag;

        return !string.IsNullOrEmpty(applyTag) &&
               !string.IsNullOrEmpty(activePoolTag);
    }

    private static int GetFeatGrantedMetamagicSelectedCount(
        CharacterHeroBuildingData heroBuildingData,
        string activePoolTag)
    {
        if (heroBuildingData?.LevelupTrainedMetamagicOptions == null ||
            string.IsNullOrEmpty(activePoolTag) ||
            !heroBuildingData.LevelupTrainedMetamagicOptions.TryGetValue(activePoolTag, out var selectedOptions) ||
            selectedOptions == null)
        {
            return 0;
        }

        return selectedOptions
            .Where(option => option != null && !string.IsNullOrEmpty(option.Name))
            .Select(option => option.Name)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static bool ApplyFeatGrantedMetamagicAdeptPointPool(
        CharacterBuildingManager manager,
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinitionPointPool pointPoolFeature,
        bool incrementExistingPool)
    {
        if (!TryResolveFeatGrantedMetamagicAdeptPointPoolTags(
                manager,
                heroBuildingData,
                pointPoolFeature,
                out var applyTag,
                out var activePoolTag))
        {
            return false;
        }

        var existingKeys = heroBuildingData.PointPoolStacks.TryGetValue(pointPoolFeature.PoolType, out var pointPoolStack)
            ? pointPoolStack.ActivePools.Keys.ToHashSet()
            : [];

        if (pointPoolStack?.ActivePools.TryGetValue(activePoolTag, out var pool) == true)
        {
            if (incrementExistingPool)
            {
                pool.maxPoints += pointPoolFeature.poolAmount;
                pool.remainingPoints = Math.Min(
                    pool.maxPoints,
                    Math.Max(0, pool.remainingPoints) + pointPoolFeature.poolAmount);

                return true;
            }

            return false;
        }

        manager.ApplyFeatureDefinitionPointPool(heroBuildingData, pointPoolFeature, applyTag);

        var changed = EnsureFeatGrantedPointPoolActiveTag(
            heroBuildingData,
            pointPoolFeature.PoolType,
            activePoolTag,
            existingKeys);

        if (!heroBuildingData.PointPoolStacks.TryGetValue(pointPoolFeature.PoolType, out pointPoolStack) ||
            !pointPoolStack.ActivePools.TryGetValue(activePoolTag, out pool))
        {
            return changed;
        }

        if (pool.maxPoints < pointPoolFeature.poolAmount)
        {
            pool.maxPoints = pointPoolFeature.poolAmount;
            changed = true;
        }

        if (pool.remainingPoints <= 0 &&
            GetFeatGrantedMetamagicSelectedCount(heroBuildingData, activePoolTag) == 0)
        {
            pool.remainingPoints = pointPoolFeature.poolAmount;
            changed = true;
        }

        return changed;
    }

    private static bool RemoveFeatGrantedMetamagicAdeptPointPool(
        CharacterBuildingManager manager,
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinitionPointPool pointPoolFeature)
    {
        if (!TryResolveFeatGrantedMetamagicAdeptPointPoolTags(
                manager,
                heroBuildingData,
                pointPoolFeature,
                out _,
                out var activePoolTag))
        {
            return false;
        }

        var changed = heroBuildingData.LevelupTrainedMetamagicOptions?.Remove(activePoolTag) == true;

        if (!heroBuildingData.PointPoolStacks.TryGetValue(pointPoolFeature.PoolType, out var pointPoolStack) ||
            !pointPoolStack.ActivePools.TryGetValue(activePoolTag, out var pool))
        {
            return changed;
        }

        pool.maxPoints -= pointPoolFeature.poolAmount;

        if (pool.maxPoints <= 0)
        {
            pointPoolStack.ActivePools.Remove(activePoolTag);
        }

        return true;
    }

    private static string FindFeatGrantedPointPoolKeyFallback(
        CharacterHeroBuildingData heroBuildingData,
        HeroDefinitions.PointsPoolType poolType,
        string expectedActivePoolTag,
        string extraSpellsTag)
    {
        if (heroBuildingData == null ||
            string.IsNullOrEmpty(expectedActivePoolTag) ||
            !heroBuildingData.PointPoolStacks.TryGetValue(poolType, out var pointPoolStack))
        {
            return null;
        }

        if (pointPoolStack.ActivePools.ContainsKey(expectedActivePoolTag))
        {
            return expectedActivePoolTag;
        }

        if (string.IsNullOrEmpty(extraSpellsTag))
        {
            return null;
        }

        var suffix = extraSpellsTag + extraSpellsTag;
        var matchingKeys = pointPoolStack.ActivePools.Keys
            .Where(key => !string.IsNullOrEmpty(key) &&
                          key.EndsWith(suffix, StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        return matchingKeys.Length == 1 ? matchingKeys[0] : null;
    }

    private static bool TryEnsureExpectedFeatGrantedPointPoolKey(
        CharacterHeroBuildingData heroBuildingData,
        HeroDefinitions.PointsPoolType poolType,
        string expectedActivePoolTag,
        string extraSpellsTag,
        out bool changed)
    {
        changed = false;

        if (heroBuildingData == null ||
            string.IsNullOrEmpty(expectedActivePoolTag) ||
            !heroBuildingData.PointPoolStacks.TryGetValue(poolType, out var pointPoolStack))
        {
            return false;
        }

        if (pointPoolStack.ActivePools.ContainsKey(expectedActivePoolTag))
        {
            return true;
        }

        var existingKey = FindFeatGrantedPointPoolKeyFallback(
            heroBuildingData,
            poolType,
            expectedActivePoolTag,
            extraSpellsTag);

        if (string.IsNullOrEmpty(existingKey) ||
            existingKey == expectedActivePoolTag ||
            !pointPoolStack.ActivePools.TryGetValue(existingKey, out var pointPool))
        {
            return false;
        }

        pointPoolStack.ActivePools.Remove(existingKey);
        pointPoolStack.ActivePools[expectedActivePoolTag] = pointPool;
        changed = true;

        return true;
    }

    private static bool TryGetFeatGrantedPointPoolForUpdate(
        CharacterHeroBuildingData heroBuildingData,
        HeroDefinitions.PointsPoolType poolType,
        string expectedActivePoolTag,
        string extraSpellsTag,
        out string activePoolKey,
        out PointPool pointPool)
    {
        activePoolKey = null;
        pointPool = null;

        if (heroBuildingData == null ||
            !heroBuildingData.PointPoolStacks.TryGetValue(poolType, out var pointPoolStack))
        {
            return false;
        }

        activePoolKey = FindFeatGrantedPointPoolKeyFallback(
            heroBuildingData,
            poolType,
            expectedActivePoolTag,
            extraSpellsTag);

        return !string.IsNullOrEmpty(activePoolKey) &&
               pointPoolStack.ActivePools.TryGetValue(activePoolKey, out pointPool);
    }

    internal static PointPool ResolveFeatGrantedSpellSelectionPointPool(
        ICharacterBuildingService characterBuildingService,
        CharacterHeroBuildingData heroBuildingData,
        HeroDefinitions.PointsPoolType poolType,
        string tag,
        FeatureDefinitionCastSpell spellFeature)
    {
        var currentPool = characterBuildingService?.GetPointPoolOfTypeAndTag(heroBuildingData, poolType, tag);

        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            characterBuildingService is not CharacterBuildingManager manager ||
            heroBuildingData?.HeroCharacter == null ||
            spellFeature == null ||
            poolType is not HeroDefinitions.PointsPoolType.Spell and not HeroDefinitions.PointsPoolType.Cantrip)
        {
            return currentPool;
        }

        var spellTag = spellFeature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();

        if (spellTag == null ||
            string.IsNullOrEmpty(spellTag.Name) ||
            !TryResolveFeatGrantedPointPoolTags(
                manager,
                heroBuildingData.HeroCharacter,
                spellTag.Name,
                out _,
                out _,
                out var activePoolTag))
        {
            return currentPool;
        }

        if (TryGetFeatGrantedPointPoolForUpdate(
                heroBuildingData,
                poolType,
                activePoolTag,
                spellTag.Name,
                out _,
                out var featPointPool))
        {
            return featPointPool;
        }

        _ = EnsureFeatGrantedPointPoolsForTrainedFeats(characterBuildingService, heroBuildingData);

        return TryGetFeatGrantedPointPoolForUpdate(
            heroBuildingData,
            poolType,
            activePoolTag,
            spellTag.Name,
            out _,
            out featPointPool)
            ? featPointPool
            : currentPool;
    }

    internal static bool SyncFeatGrantedPointPoolsForTrainedFeats(
        ICharacterBuildingService characterBuildingService,
        CharacterHeroBuildingData heroBuildingData)
    {
        return EnsureFeatGrantedPointPoolsForTrainedFeats(characterBuildingService, heroBuildingData);
    }

    internal static bool EnsureFeatGrantedPointPoolsForTrainedFeats(
        ICharacterBuildingService characterBuildingService,
        CharacterHeroBuildingData heroBuildingData)
    {
        if (characterBuildingService is not CharacterBuildingManager manager ||
            heroBuildingData?.LevelupTrainedFeats == null)
        {
            return false;
        }

        var rebuilt = false;

        foreach (var feat in heroBuildingData.LevelupTrainedFeats
                     .SelectMany(entry => entry.Value ?? [])
                     .Where(feat => feat != null)
                     .Distinct())
        {
            foreach (var pointPoolFeature in feat.Features
                         .OfType<FeatureDefinitionPointPool>()
                         .Where(IsFeatGrantedSpellOrCantripPointPool))
            {
                if (!TryResolveFeatGrantedPointPoolTags(
                        manager,
                        heroBuildingData,
                        pointPoolFeature,
                        out _,
                        out var applyTag,
                        out var activePoolTag))
                {
                    continue;
                }

                if (TryEnsureExpectedFeatGrantedPointPoolKey(
                        heroBuildingData,
                        pointPoolFeature.PoolType,
                        activePoolTag,
                        pointPoolFeature.ExtraSpellsTag,
                        out var normalized))
                {
                    rebuilt |= normalized;

                    if (heroBuildingData.PointPoolStacks.TryGetValue(pointPoolFeature.PoolType, out var pointPoolStack) &&
                        pointPoolStack.ActivePools.TryGetValue(activePoolTag, out var pool) &&
                        pool.maxPoints < pointPoolFeature.poolAmount)
                    {
                        pool.maxPoints = pointPoolFeature.poolAmount;
                        rebuilt = true;
                    }

                    continue;
                }

                var existingKeys = heroBuildingData.PointPoolStacks.TryGetValue(pointPoolFeature.PoolType, out var stack)
                    ? stack.ActivePools.Keys.ToHashSet()
                    : [];

                manager.ApplyFeatureDefinitionPointPool(heroBuildingData, pointPoolFeature, applyTag);

                if (EnsureFeatGrantedPointPoolActiveTag(
                        heroBuildingData,
                        pointPoolFeature.PoolType,
                        activePoolTag,
                        existingKeys))
                {
                    rebuilt = true;
                }
            }

            foreach (var pointPoolFeature in feat.Features
                         .OfType<FeatureDefinitionPointPool>()
                         .Where(IsFeatGrantedMetamagicAdeptPointPool))
            {
                rebuilt |= ApplyFeatGrantedMetamagicAdeptPointPool(
                    manager,
                    heroBuildingData,
                    pointPoolFeature,
                    false);
            }
        }

        return rebuilt;
    }

    private static void SanitizeManagedTabletopTrainedFeatsForTag(
        CharacterHeroBuildingData heroBuildingData,
        string tag,
        PointPool pointPool,
        FeatDefinition selectedFeat = null,
        ICharacterBuildingService service = null)
    {
        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            heroBuildingData == null ||
            string.IsNullOrEmpty(tag) ||
            service == null ||
            !heroBuildingData.LevelupTrainedFeats.TryGetValue(tag, out var trainedFeats) ||
            trainedFeats == null ||
            trainedFeats.Count == 0)
        {
            return;
        }

        var currentFeats = trainedFeats
            .Where(feat => feat != null)
            .ToList();

        if (currentFeats.Count == 0)
        {
            heroBuildingData.LevelupTrainedFeats.Remove(tag);

            return;
        }

        FeatDefinition resolvedSelectedFeat = null;

        if (selectedFeat != null)
        {
            Tabletop2024Context.TryResolveTrainableModeAwareFeat(selectedFeat, out resolvedSelectedFeat);
        }

        var needsSanitize = pointPool == null || currentFeats.Count != trainedFeats.Count;
        var distinctResolvedFeats = new List<FeatDefinition>();

        foreach (var currentFeat in currentFeats)
        {
            if (!Tabletop2024Context.TryResolveTrainableModeAwareFeat(currentFeat, out var resolvedFeat) ||
                !Tabletop2024Context.IsDisplayableManagedTabletopLeaf(resolvedFeat) ||
                pointPool != null &&
                !Tabletop2024Context.TryPrepareIndependentFeatTraining(
                    heroBuildingData,
                    tag,
                    resolvedFeat,
                    service))
            {
                needsSanitize = true;
                continue;
            }

            if (distinctResolvedFeats.Any(existingFeat =>
                    Tabletop2024Context.AreEquivalentTabletopFeatNames(existingFeat.Name, resolvedFeat.Name)))
            {
                needsSanitize = true;
                continue;
            }

            distinctResolvedFeats.Add(resolvedFeat);
        }

        if (resolvedSelectedFeat != null &&
            currentFeats.Any(currentFeat =>
                !Tabletop2024Context.AreEquivalentTabletopFeatNames(currentFeat.Name, resolvedSelectedFeat.Name)))
        {
            needsSanitize = true;
        }

        if (pointPool != null &&
            pointPool.maxPoints <= 1 &&
            distinctResolvedFeats.Count > 1)
        {
            needsSanitize = true;
        }

        if (!needsSanitize)
        {
            return;
        }

        service.UntrainFeats(heroBuildingData, tag);
        heroBuildingData.LevelupTrainedFeats.Remove(tag);
        Tabletop2024Context.ClearPendingFeatSelection(heroBuildingData.HeroCharacter, tag);
    }

    private static List<FeatDefinition> SnapshotDisplayFeatsForFinalize(RulesetCharacterHero hero)
    {
        if (hero?.GetHeroBuildingData() is not { } heroBuildingData)
        {
            return [];
        }

        var snapshot = new List<FeatDefinition>();

        AddDisplayableFinalizeFeats(
            snapshot,
            heroBuildingData.LevelupTrainedFeats?.Values
                .Where(feats => feats != null)
                .SelectMany(feats => feats));

        FeatDefinition backgroundFeat = null;

        if (Tabletop2024Context.TryGetBackgroundBonusFeatForDisplay(hero, heroBuildingData, out backgroundFeat))
        {
            AddDisplayableFinalizeFeats(snapshot, [backgroundFeat]);
        }

        FeatDefinition humanOriginFeat = null;

        if (Tabletop2024Context.TryGetHumanOriginFeatForFinalizeSnapshot(hero, heroBuildingData, out humanOriginFeat))
        {
            AddDisplayableFinalizeFeats(snapshot, [humanOriginFeat]);
        }

        LogFinalizeDisplaySnapshot(hero, snapshot, backgroundFeat, humanOriginFeat);

        return snapshot;
    }

    private static void AddDisplayableFinalizeFeats(
        List<FeatDefinition> snapshot,
        IEnumerable<FeatDefinition> feats)
    {
        if (snapshot == null || feats == null)
        {
            return;
        }

        foreach (var feat in feats.Where(IsDisplayableFinalizeFeat))
        {
            if (snapshot.Any(existingFeat =>
                    existingFeat != null &&
                    existingFeat.Name == feat.Name))
            {
                continue;
            }

            snapshot.Add(feat);
        }
    }

    private static bool IsDisplayableFinalizeFeat(FeatDefinition feat)
    {
        if (feat == null)
        {
            return false;
        }

        if (Tabletop2024Context.IsManagedTabletopFeat(feat))
        {
            return Tabletop2024Context.IsDisplayableManagedTabletopLeaf(feat);
        }

        return feat.GetFirstSubFeatureOfType<IGroupedFeat>() == null &&
               !Tabletop2024Context.IsTabletopContainerGroup(feat) &&
               !Tabletop2024Context.IsNonSelectableTabletopGroup(feat) &&
               Tabletop2024Context.GetCanonicalTabletopFeatName(feat.Name) != "FeatSkilled";
    }

    private static void EnsureDisplayFeatsPresentInHeroTrainedFeats(
        RulesetCharacterHero hero,
        IEnumerable<FeatDefinition> trainedFeats)
    {
        if (hero == null)
        {
            return;
        }

        hero.trainedFeats ??= [];

        var addedFeatNames = new List<string>();

        foreach (var feat in trainedFeats?.Where(IsDisplayableFinalizeFeat) ?? [])
        {
            feat.GuiPresentation.hidden = false;

            if (hero.trainedFeats.Any(existingFeat =>
                    existingFeat != null &&
                    existingFeat.Name == feat.Name))
            {
                continue;
            }

            hero.trainedFeats.Add(feat);
            addedFeatNames.Add(feat.Name);
        }

        foreach (var feat in hero.trainedFeats.Where(IsDisplayableFinalizeFeat))
        {
            feat.GuiPresentation.hidden = false;
        }

        LogFinalizeDisplaySync(hero, addedFeatNames);
    }

    [Conditional("DEBUG")]
    private static void LogFinalizeDisplaySnapshot(
        RulesetCharacterHero hero,
        IEnumerable<FeatDefinition> snapshot,
        FeatDefinition backgroundFeat,
        FeatDefinition humanOriginFeat)
    {
        var snapshotNames = snapshot?.Where(feat => feat != null).Select(feat => feat.Name) ?? [];

        Main.Log(
            $"Finalize display snapshot: hero={hero?.Name ?? "null"} guid={(hero != null ? hero.Guid.ToString() : "null")} " +
            $"use2024={Main.Settings.EnableTabletopFeatRules2024} " +
            $"background={backgroundFeat?.Name ?? "null"} humanOrigin={humanOriginFeat?.Name ?? "null"} " +
            $"snapshot=[{string.Join(", ", snapshotNames)}]");
    }

    [Conditional("DEBUG")]
    private static void LogFinalizeDisplaySync(
        RulesetCharacterHero hero,
        IEnumerable<string> addedFeatNames)
    {
        var trainedFeatNames = hero?.trainedFeats?.Where(feat => feat != null).Select(feat => feat.Name) ?? [];
        var addedNames = addedFeatNames?.Where(name => !string.IsNullOrEmpty(name)) ?? [];

        Main.Log(
            $"Finalize display sync: hero={hero?.Name ?? "null"} guid={(hero != null ? hero.Guid.ToString() : "null")} " +
            $"trained=[{string.Join(", ", trainedFeatNames)}] " +
            $"added=[{string.Join(", ", addedNames)}]");
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.CreateNewCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CreateNewCharacter_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterBuildingManager __instance)
        {
            //PATCH: registers the hero getting created
            Tabletop2024Context.ClearPendingFeatSelections(__instance.CurrentLocalHeroCharacter);
            FeatsContext.ClearFeatSubPanel2024UiState();
            LevelUpHelper.RegisterHero(__instance.CurrentLocalHeroCharacter, false);
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.TrainInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TrainInvocation_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            CharacterBuildingManager __instance,
            CharacterHeroBuildingData heroBuildingData,
            InvocationDefinition invocation,
            ref bool checkPool)
        {
            //PATCH: do not check or modify point pools when dealing with custom invocations
            if (invocation is InvocationDefinitionCustom)
            {
                checkPool = false;
            }

            if (invocation.GrantedFeature is not FeatureDefinitionPointPool featureDefinitionPointPool)
            {
                return;
            }

            if (!heroBuildingData.PointPoolStacks.TryGetValue(
                    featureDefinitionPointPool.PoolType, out var pointPoolStack))
            {
                return;
            }

            var hero = heroBuildingData.HeroCharacter;

            __instance.GetLastAssignedClassAndLevel(hero, out var classDefinition, out var level);

            var finaTag = AttributeDefinitions.GetClassTag(classDefinition, level) +
                          featureDefinitionPointPool.ExtraSpellsTag;

            if (pointPoolStack.ActivePools
                .TryGetValue(finaTag + featureDefinitionPointPool.ExtraSpellsTag, out var pool))
            {
                pool.maxPoints += featureDefinitionPointPool.poolAmount;
            }
            else
            {
                __instance.ApplyFeatureDefinitionPointPool(heroBuildingData, featureDefinitionPointPool, finaTag);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UnlearnInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UnlearnInvocation_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            InvocationDefinition invocation,
            ref bool checkPool)
        {
            //PATCH: do not check or modify point pools when dealing with custom invocations
            if (invocation is InvocationDefinitionCustom)
            {
                checkPool = false;
            }
        }
    }

    private static void UndoGrantPool(
        CharacterBuildingManager __instance,
        CharacterHeroBuildingData heroBuildingData,
        InvocationDefinition invocation)
    {
        if (invocation.GrantedFeature is not FeatureDefinitionPointPool featureDefinitionPointPool)
        {
            return;
        }

        if (!heroBuildingData.PointPoolStacks.TryGetValue(featureDefinitionPointPool.PoolType,
                out var pointPoolStack))
        {
            return;
        }

        var hero = heroBuildingData.HeroCharacter;

        __instance.GetLastAssignedClassAndLevel(hero, out var classDefinition, out var level);

        var finaTag = AttributeDefinitions.GetClassTag(classDefinition, level) +
                      featureDefinitionPointPool.ExtraSpellsTag + featureDefinitionPointPool.ExtraSpellsTag;

        if (!pointPoolStack.ActivePools.TryGetValue(finaTag, out var pool))
        {
            return;
        }

        pool.maxPoints -= featureDefinitionPointPool.poolAmount;

        if (pool.maxPoints == 0)
        {
            pointPoolStack.ActivePools.Remove(finaTag);
        }
    }


    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UntrainInvocations))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UntrainInvocations_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            CharacterBuildingManager __instance,
            CharacterHeroBuildingData heroBuildingData,
            string tag)
        {
            if (!heroBuildingData.LevelupTrainedInvocations.TryGetValue(tag, out var invocations))
            {
                return;
            }

            foreach (var invocation in invocations)
            {
                //PATCH: do not check or modify point pools when dealing with custom invocations
                if (invocation is InvocationDefinitionCustom) { continue; }

                UndoGrantPool(__instance, heroBuildingData, invocation);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UntrainInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UntrainInvocation_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterBuildingManager __instance,
            CharacterHeroBuildingData heroBuildingData,
            InvocationDefinition invocation,
            string tag)
        {
            //PATCH: do not check or modify point pools when dealing with custom invocations
            if (invocation is not InvocationDefinitionCustom)
            {
                UndoGrantPool(__instance, heroBuildingData, invocation);

                return true;
            }

            if (heroBuildingData.LevelupTrainedInvocations.TryGetValue(tag, out var value))
            {
                value.Remove(invocation);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UndoUnlearnInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UndoUnlearnInvocation_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterHeroBuildingData heroBuildingData,
            InvocationDefinition invocation,
            string tag)
        {
            //PATCH: do not check or modify point pools when dealing with custom invocations
            if (invocation is not InvocationDefinitionCustom)
            {
                return true;
            }

            if (heroBuildingData.UnlearnedInvocations.TryGetValue(tag, out var value))
            {
                value.Remove(invocation);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.LevelUpCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LevelUpCharacter_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] RulesetCharacterHero hero, ref bool force)
        {
            //PATCH: forces no experience on level up setting
            if (Main.Settings.NoExperienceOnLevelUp)
            {
                force = true;
            }

            //PATCH: registers the hero leveling up
            LevelUpHelper.RegisterHero(hero, true);
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.FinalizeCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FinalizeCharacter_Patch
    {
        private static void GrantCantripFromFightingStyle(
            CharacterBuildingManager characterBuildingManager,
            RulesetCharacterHero hero,
            FeatureDefinitionCastSpell featureDefinitionCastSpell)
        {
            var spellTag = featureDefinitionCastSpell.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();

            if (spellTag == null)
            {
                return;
            }

            characterBuildingManager.GetLastAssignedClassAndLevel(hero, out var classDefinition, out var level);

            var classTag = AttributeDefinitions.GetClassTag(classDefinition, level);
            var tag = spellTag.Name;
            var finalTag = classTag + tag + tag;
            var heroBuildingData = hero.GetHeroBuildingData();

            // grant cantrips from selection or fixed list
            if (!heroBuildingData.AcquiredCantrips.TryGetValue(finalTag, out var cantrips))
            {
                return;
            }

            foreach (var cantrip in cantrips)
            {
                hero.GrantCantrip(cantrip, featureDefinitionCastSpell);
            }
        }

        [UsedImplicitly]
        public static void Prefix(
            [NotNull] CharacterBuildingManager __instance,
            [NotNull] RulesetCharacterHero hero,
            out List<FeatDefinition> __state)
        {
            __state = SnapshotDisplayFeatsForFinalize(hero);
            var buildingData = hero.GetHeroBuildingData();

            //PATCH: grants race features
            LevelUpHelper.GrantRaceFeatures(__instance, hero);

            //PATCH: grants repertoires and cantrips from backgrounds
            if (hero.ClassesHistory.Count == 1)
            {
                foreach (var featureDefinitionCastSpell in hero.BackgroundDefinition.Features
                             .OfType<FeatureDefinitionCastSpell>())
                {
                    hero.GrantSpellRepertoire(featureDefinitionCastSpell, null, null, hero.RaceDefinition);

                    __instance.GrantCantripsAndSpellsByTag(
                        buildingData, AttributeDefinitions.TagBackground, featureDefinitionCastSpell);
                }

                //PATCH: grants the power spell points to any created hero including pre-gen ones (SPELL_POINTS)
                SpellPointsContext.GrantPowerSpellPoints(hero);
            }

            //PATCH: grants repertoire and selected cantrips from Blessed Warrior if not there yet
            if (hero.TrainedFightingStyles.Any(x => x.Name == BlessedWarrior.Name) &&
                hero.SpellRepertoires.All(x => x.spellCastingFeature != BlessedWarrior.CastSpellBlessedWarrior))
            {
                hero.GrantSpellRepertoire(BlessedWarrior.CastSpellBlessedWarrior, null, null, null);
                GrantCantripFromFightingStyle(__instance, hero, BlessedWarrior.CastSpellBlessedWarrior);
            }

            //PATCH: grants repertoire and selected cantrips from Blessed Warrior if not there yet
            if (hero.TrainedFightingStyles.Any(x => x.Name == DruidicWarrior.Name) &&
                hero.SpellRepertoires.All(x => x.spellCastingFeature != DruidicWarrior.CastSpellDruidicWarrior))
            {
                hero.GrantSpellRepertoire(DruidicWarrior.CastSpellDruidicWarrior, null, null, null);
                GrantCantripFromFightingStyle(__instance, hero, DruidicWarrior.CastSpellDruidicWarrior);
            }

            //PATCH: grants custom features
            LevelUpHelper.GrantCustomFeaturesFromFeats(hero);
            LevelUpHelper.GrantCustomFeatures(hero);
        }

        [UsedImplicitly]
        public static void Postfix(
            CharacterBuildingManager __instance,
            [NotNull] RulesetCharacterHero hero,
            List<FeatDefinition> __state)
        {
            //PATCH: grants cantrip that for whatever reason vanilla has a hard time granting ;-)
            GrantCantripFromCustomAcquiredPool(hero, "Thaumaturge");
            GrantCantripFromCustomAcquiredPool(hero, "DomainNature");
            GrantCantripFromCustomAcquiredPool(hero, "PactTome");
            GrantCantripFromCustomAcquiredPool(hero, "PrimalOrder");

            //PATCH: grant spells for these 2 subs as pools with tags aren't granted from subs if not at sub 1st level
            var selectedClass = LevelUpHelper.GetSelectedClass(hero);

            if (selectedClass == DatabaseHelper.CharacterClassDefinitions.Wizard)
            {
                hero.GrantAcquiredSpellWithTagFromSubclassPool(WizardAbjuration.SpellTag);
                hero.GrantAcquiredSpellWithTagFromSubclassPool(WizardEvocation.SpellTag);
            }

            //PATCH: grants spell repertoires and respective selected spells from feats
            LevelUpHelper.GrantSpellsOrCantripsFromFeatCastSpell(__instance, hero);

            //PATCH: keeps spell repertoires sorted by class title but ancestry one is always kept first
            LevelUpHelper.SortHeroRepertoires(hero);

            //PATCH: keeps displayable leaf feats visible in UI lists after finalize without re-granting features
            EnsureDisplayFeatsPresentInHeroTrainedFeats(hero, __state);
            Tabletop2024Context.ClearPendingFeatSelections(hero);
            FeatsContext.ClearFeatSubPanel2024UiState();
            GuiWrapperContext.RecacheFeats();

            //PATCH: adds whole list caster spells to KnownSpells collection to improve the MC spell selection UI
            // LevelUpContext.UpdateKnownSpellsForWholeCasters(hero);

            //PATCH: unregisters the hero leveling up
            LevelUpHelper.UnregisterHero(hero);
        }

        private static void GrantCantripFromCustomAcquiredPool(RulesetCharacterHero hero, string name)
        {
            var repertoire = hero.SpellRepertoires
                .FirstOrDefault(x => LevelUpHelper.IsRepertoireFromSelectedClassSubclass(hero, x));

            if (repertoire == null)
            {
                return;
            }

            var heroBuildingData = hero.GetHeroBuildingData();
            var selectedClassLevel = LevelUpHelper.GetSelectedClassLevel(hero);

            var selectedClass = LevelUpHelper.GetSelectedClass(hero);
            var classTag = AttributeDefinitions.GetClassTag(selectedClass, selectedClassLevel);
            var classPoolName = $"{classTag}{name}";

            // consider cantrips from classes
            if (heroBuildingData.AcquiredCantrips.TryGetValue(classPoolName, out var cantrips1))
            {
                foreach (var cantrip in cantrips1)
                {
                    hero.GrantCantrip(cantrip, repertoire.SpellCastingFeature, name);
                }
            }

            // consider cantrips from feats / invocations / etc.
            classPoolName = $"{classTag}{name}{name}";

            if (heroBuildingData.AcquiredCantrips.TryGetValue(classPoolName, out var cantrips2))
            {
                foreach (var cantrip in cantrips2)
                {
                    hero.GrantCantrip(cantrip, repertoire.SpellCastingFeature, name);
                }
            }

            var selectedSubclass = LevelUpHelper.GetSelectedSubclass(hero);

            if (!selectedSubclass)
            {
                return;
            }

            // consider cantrips from subclasses
            var subclassTag = AttributeDefinitions.GetSubclassTag(selectedClass, selectedClassLevel, selectedSubclass);
            var subclassPoolName = $"{subclassTag}{name}";

            if (!heroBuildingData.AcquiredCantrips.TryGetValue(subclassPoolName, out var cantrips3))
            {
                return;
            }

            foreach (var cantrip in cantrips3)
            {
                hero.GrantCantrip(cantrip, repertoire.SpellCastingFeature, name);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.AssignClassLevel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AssignClassLevel_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] RulesetCharacterHero hero, CharacterClassDefinition classDefinition)
        {
            //PATCH: captures the desired class
            LevelUpHelper.SetSelectedClass(hero, classDefinition);

            //PATCH: ensures this doesn't get executed in the class panel level up screen
            var isLevelingUp = LevelUpHelper.IsLevelingUp(hero);
            var isClassSelectionStage = LevelUpHelper.IsClassSelectionStage(hero);
            var result = isLevelingUp && isClassSelectionStage;

            if (result)
            {
                //PATCH: grants items for new class if required
                LevelUpHelper.GrantItemsIfRequired(hero);
            }

            return !result;
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.AssignSubclass))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AssignSubclass_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] RulesetCharacterHero hero, CharacterSubclassDefinition subclassDefinition)
        {
            //PATCH: captures the desired sub class
            LevelUpHelper.SetSelectedSubclass(hero, subclassDefinition);
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.GrantFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GrantFeatures_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] RulesetCharacterHero hero)
        {
            //PATCH: ensures this doesn't get executed in the class panel level up screen
            var isLevelingUp = LevelUpHelper.IsLevelingUp(hero);
            var isClassSelectionStage = LevelUpHelper.IsClassSelectionStage(hero);

            return !(isLevelingUp && isClassSelectionStage);
        }

        [UsedImplicitly]
        public static void Postfix(
            RulesetCharacterHero hero,
            List<FeatureDefinition> grantedFeatures,
            string tag)
        {
            //PATCH: support for `FeatureDefinitionGrantCustomInvocations`
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            //PATCH: fixes the particular case where we get double invocation pools if hero is MC
            var heroBuildingData = hero.GetHeroBuildingData();

            if (heroBuildingData.PointPoolStacks
                    .TryGetValue(HeroDefinitions.PointsPoolType.Invocation, out var pointPoolStack) &&
                hero.ClassesAndLevels
                    .TryGetValue(DatabaseHelper.CharacterClassDefinitions.Warlock, out var levels))
            {
                var goodTag =
                    AttributeDefinitions.GetClassTag(DatabaseHelper.CharacterClassDefinitions.Warlock, levels);

                foreach (var badKey in pointPoolStack.ActivePools.Keys.Where(x => x != goodTag).ToArray())
                {
                    pointPoolStack.ActivePools.Remove(badKey);
                }
            }

            FeatureDefinitionGrantInvocations.GrantInvocations(hero, tag, grantedFeatures);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var getInvocationProficiencies = typeof(RulesetCharacterHero).GetMethod("get_InvocationProficiencies");
            var customInvocationsProficiencies =
                new Func<RulesetCharacterHero, List<string>>(CustomInvocationSubPanel
                        .OnlyStandardInvocationProficiencies)
                    .Method;

            return instructions
                //PATCH: don't offer invocations unlearn on non Warlock classes (MULTICLASS)
                .ReplaceCalls(getInvocationProficiencies,
                    "CharacterBuildingManager.GrantFeatures",
                    new CodeInstruction(OpCodes.Call, customInvocationsProficiencies));
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.RemoveActiveFeaturesFromHeroByTag))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RemoveActiveFeaturesFromHeroByTag_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetCharacterHero hero, string tag)
        {
            //PATCH: support for `FeatureDefinitionGrantCustomInvocations`
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            if (hero.ActiveFeatures.TryGetValue(tag, out var features))
            {
                FeatureDefinitionGrantInvocations.RemoveInvocations(hero, tag, features);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.ClearPrevious))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ClearPrevious_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetCharacterHero hero, string tag)
        {
            //PATCH: support for `FeatureDefinitionGrantCustomInvocations`
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            if (tag == AttributeDefinitions.TagRace)
            {
                Tabletop2024Context.RemoveHumanOriginFeatPointPool(hero?.GetHeroBuildingData());
            }

            if (hero.ActiveFeatures.TryGetValue(tag, out var features))
            {
                FeatureDefinitionGrantInvocations.RemoveInvocations(hero, tag, features);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.ApplyFeatureDefinitionPointPool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyFeatureDefinitionPointPool_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterHeroBuildingData heroBuildingData, FeatureDefinition feature)
        {
            return !Tabletop2024Context.TryApplyHumanOriginFeatPointPool(heroBuildingData, feature);
        }

        [UsedImplicitly]
        public static void Postfix(
            CharacterHeroBuildingData heroBuildingData,
            FeatureDefinition feature,
            string __2)
        {
            if (!Main.Settings.EnableTabletopFeatRules2024 ||
                heroBuildingData == null ||
                feature is not FeatureDefinitionPointPool pointPoolFeature ||
                pointPoolFeature.PoolType != HeroDefinitions.PointsPoolType.Feat ||
                !heroBuildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Feat, out var pointPoolStack))
            {
                return;
            }

            var poolTag = __2 + pointPoolFeature.ExtraSpellsTag;

            if (!pointPoolStack.ActivePools.TryGetValue(poolTag, out var pointPool) &&
                !pointPoolStack.ActivePools.TryGetValue(__2, out pointPool))
            {
                return;
            }

            Tabletop2024Context.NormalizeModeAwareFeatPointPool(pointPool);
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UnassignLastClassLevel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UnassignLastClassLevel_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] RulesetCharacterHero hero)
        {
            //PATCH: un-captures the desired class
            LevelUpHelper.SetSelectedClass(hero, null);

            //PATCH: ensures this doesn't get executed in the class panel level up screen
            var isLevelingUp = LevelUpHelper.IsLevelingUp(hero);
            var isClassSelectionStage = LevelUpHelper.IsClassSelectionStage(hero);
            var result = isLevelingUp && isClassSelectionStage;

            if (result)
            {
                //PATCH: removes items from new class if required
                LevelUpHelper.RemoveItemsIfRequired(hero);
            }

            return !result;
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UnassignLastSubclass))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UnassignLastSubclass_Patch
    {
        private static void ResetCantripsPool(RulesetCharacterHero hero, string poolName)
        {
            var buildingData = hero.GetHeroBuildingData();

            if (buildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Cantrip, out var pointPool))
            {
                pointPool.ActivePools.Remove(poolName);
            }
        }

        [UsedImplicitly]
        public static bool Prefix([NotNull] RulesetCharacterHero hero)
        {
            //PATCH: avoid Domain Nature to break level up with the cantrip pool it gets
            ResetCantripsPool(hero,
                $"{AttributeDefinitions.TagSubclass}Cleric" +
                (Main.Settings.EnableClericToLearnDomainAtLevel3 ? 3 : 1) +
                "DomainNatureDomainNature");

            //PATCH: un-captures the desired subclass
            LevelUpHelper.SetSelectedSubclass(hero, null);

            //PATCH: ensures this doesn't get executed in the class panel level up screen
            var isLevelingUp = LevelUpHelper.IsLevelingUp(hero);
            var isClassSelectionStage = LevelUpHelper.IsClassSelectionStage(hero);
            var result = isLevelingUp && isClassSelectionStage;

            return !result;
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UntrainLastFightingStyle))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UntrainLastFightingStyle_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterBuildingManager __instance, [NotNull] RulesetCharacterHero hero)
        {
            //PATCH: ensures this doesn't get executed in the class panel level up screen
            var isLevelingUp = LevelUpHelper.IsLevelingUp(hero);
            var isClassSelectionStage = LevelUpHelper.IsClassSelectionStage(hero);
            var result = isLevelingUp && isClassSelectionStage;

            if (result ||
                hero.TrainedFightingStyles.Count <= 0)
            {
                return !result;
            }

            //PATCH: remove point pools assigned from fighting styles
            var heroBuildingData = hero.GetHeroBuildingData();
            var fightingStyle = hero.TrainedFightingStyles[hero.TrainedFightingStyles.Count - 1];

            foreach (var featureDefinitionPointPool in fightingStyle.Features.OfType<FeatureDefinitionPointPool>())
            {
                if (!heroBuildingData.PointPoolStacks.TryGetValue(featureDefinitionPointPool.PoolType,
                        out var pointPoolStack))
                {
                    continue;
                }

                __instance.GetLastAssignedClassAndLevel(hero, out var classDefinition, out var level);

                var finaTag = AttributeDefinitions.GetClassTag(classDefinition, level) +
                              featureDefinitionPointPool.ExtraSpellsTag + featureDefinitionPointPool.ExtraSpellsTag;

                if (!pointPoolStack.ActivePools.TryGetValue(finaTag, out var pool))
                {
                    continue;
                }

                pool.maxPoints -= featureDefinitionPointPool.poolAmount;

                if (pool.maxPoints == 0)
                {
                    pointPoolStack.ActivePools.Remove(finaTag);
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.EnumerateKnownAndAcquiredSpells))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumerateKnownAndAcquiredSpells_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            [NotNull] CharacterHeroBuildingData heroBuildingData,
            List<SpellDefinition> __result)
        {
            //PATCH: ensures the level up process only presents / offers spells from current class
            LevelUpHelper.EnumerateKnownAndAcquiredSpells(heroBuildingData, __result);
        }
    }

    //PATCH: gets the correct spell feature for the selected class
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.GetSpellFeature))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetSpellFeature_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            [NotNull] CharacterHeroBuildingData heroBuildingData,
            string tag,
            ref FeatureDefinitionCastSpell __result)
        {
            var hero = heroBuildingData.HeroCharacter;

            //PATCH: support cast spell granted from feat
            foreach (var featureDefinitionCastSpell in heroBuildingData.levelupTrainedFeats.SelectMany(x =>
                         x.Value.SelectMany(y => y.Features).OfType<FeatureDefinitionCastSpell>()))
            {
                var spellTag = featureDefinitionCastSpell.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();

                if (spellTag == null || !tag.EndsWith(spellTag.Name))
                {
                    continue;
                }

                __result = featureDefinitionCastSpell;

                return false;
            }

            //PATCH: support cast spell granted from fighting style
            if (tag.EndsWith(BlessedWarrior.Name) || tag.EndsWith(DruidicWarrior.Name))
            {
                var castSpell = hero.TrainedFightingStyles[hero.TrainedFightingStyles.Count - 1].Features
                    .OfType<FeatureDefinitionCastSpell>().First();

                if (castSpell)
                {
                    var spellTag = castSpell.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();

                    if (spellTag != null && tag.EndsWith(spellTag.Name))
                    {
                        __result = castSpell;

                        return false;
                    }
                }
            }

            var isMulticlass = LevelUpHelper.IsMulticlass(hero);
            if (!isMulticlass)
            {
                return true;
            }

            var selectedClass = LevelUpHelper.GetSelectedClass(hero);

            if (!selectedClass)
            {
                return true;
            }

            var localTag = tag;

            __result = null;

            if (localTag.StartsWith(AttributeDefinitions.TagClass))
            {
                localTag = AttributeDefinitions.TagClass + selectedClass.Name;
            }
            else if (localTag.StartsWith(AttributeDefinitions.TagSubclass))
            {
                localTag = AttributeDefinitions.TagSubclass + selectedClass.Name;
            }

            // PATCH
            foreach (var activeFeature in hero.ActiveFeatures.Where(x => x.Key.StartsWith(localTag)))
            {
                foreach (var featureDefinition in activeFeature.Value
                             .OfType<FeatureDefinitionCastSpell>())
                {
                    __result = featureDefinition;

                    return false;
                }
            }

            if (!localTag.StartsWith(AttributeDefinitions.TagSubclass))
            {
                return false;
            }

            localTag = AttributeDefinitions.TagClass + selectedClass.Name;

            // PATCH
            foreach (var activeFeature in hero.ActiveFeatures.Where(x => x.Key.StartsWith(localTag)))
            {
                foreach (var featureDefinition in activeFeature.Value
                             .OfType<FeatureDefinitionCastSpell>())
                {
                    __result = featureDefinition;

                    return false;
                }
            }

            return false;
        }
    }

    //PATCH: ensures the level up process don't get stuck if race uses fixed list and hero is a caster on level 1
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.SetupSpellPointPools))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetupSpellPointPools_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterBuildingManager __instance,
            CharacterHeroBuildingData heroBuildingData,
            FeatureDefinitionCastSpell featureDefinitionCastSpell,
            string tag)
        {
            heroBuildingData.TempAcquiredCantripsNumber = 0;
            heroBuildingData.TempAcquiredSpellsNumber = 0;
            heroBuildingData.TempUnlearnedSpellsNumber = 0;
            heroBuildingData.TempAcquiredAnyCantripOrSpellNumber = 0;

            __instance.ApplyFeatureCastSpell(heroBuildingData, featureDefinitionCastSpell);

            // this IF is only difference from original game code (in original block is always executed)
            if (tag != AttributeDefinitions.TagRace ||
                featureDefinitionCastSpell.SpellKnowledge != SpellKnowledge.FixedList)
            {
                __instance.SetPointPool(heroBuildingData, HeroDefinitions.PointsPoolType.Cantrip, tag,
                    heroBuildingData.TempAcquiredCantripsNumber);
                __instance.SetPointPool(heroBuildingData, HeroDefinitions.PointsPoolType.Spell, tag,
                    heroBuildingData.TempAcquiredSpellsNumber);
                __instance.SetPointPool(heroBuildingData, HeroDefinitions.PointsPoolType.CantripOrSpell, tag,
                    heroBuildingData.TempAcquiredAnyCantripOrSpellNumber);
            }

            if (heroBuildingData.HeroCharacter.ActiveFeatures.TryGetValue(tag, out var value))
            {
                heroBuildingData.HeroCharacter.BrowseFeaturesOfType<FeatureDefinitionCastSpell>(
                    value,
                    (feature, s) => __instance.LearnFixedSpells(heroBuildingData, feature, s), tag);
            }

            return false;
        }
    }

    //PATCH: ensures the level up process only offers slots from the leveling up class
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UpgradeSpellPointPools))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UpgradeSpellPointPools_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterBuildingManager __instance,
            [NotNull] CharacterHeroBuildingData heroBuildingData)
        {
            var hero = heroBuildingData.HeroCharacter;
            var selectedClass = LevelUpHelper.GetSelectedClass(hero);
            var selectedSubclass = LevelUpHelper.GetSelectedSubclass(hero);
            var selectedClassLevel = LevelUpHelper.GetSelectedClassLevel(hero);

            // we filter out any repertoire that was granted from feats
            foreach (var spellRepertoire in hero.SpellRepertoires
                         .Where(x => !x.SpellCastingFeature.HasSubFeatureOfType<FeatHelpers.SpellTag>()))
            {
                var poolName = string.Empty;
                var maxPoints = 0;

                switch (spellRepertoire.SpellCastingFeature.SpellCastingOrigin)
                {
                    // short circuit if the feature is for another class (change from native code)
                    case CastingOrigin.Class when spellRepertoire.SpellCastingClass != selectedClass:
                        continue;
                    case CastingOrigin.Class:
                        poolName = AttributeDefinitions.GetClassTag(selectedClass, selectedClassLevel);
                        break;
                    // short circuit if the feature is for another subclass (change from native code)
                    case CastingOrigin.Subclass when spellRepertoire.SpellCastingSubclass != selectedSubclass:
                        continue;
                    case CastingOrigin.Subclass:
                        poolName = AttributeDefinitions.GetSubclassTag(
                            selectedClass, selectedClassLevel, selectedSubclass);
                        break;
                    case CastingOrigin.Race:
                        poolName = AttributeDefinitions.TagRace;
                        break;
                    case CastingOrigin.Monster:
                        break;
                    default:
                        throw new ArgumentException("spellRepertoire.SpellCastingFeature.SpellCastingOrigin");
                }

                if (__instance.HasAnyActivePoolOfType(heroBuildingData, HeroDefinitions.PointsPoolType.Cantrip)
                    && heroBuildingData.PointPoolStacks[HeroDefinitions.PointsPoolType.Cantrip].ActivePools
                        .TryGetValue(poolName, out var pointPool))
                {
                    maxPoints = pointPool.MaxPoints;
                }

                heroBuildingData.TempAcquiredCantripsNumber = 0;
                heroBuildingData.TempAcquiredSpellsNumber = 0;
                heroBuildingData.TempUnlearnedSpellsNumber = 0;

                __instance.ApplyFeatureCastSpell(heroBuildingData, spellRepertoire.SpellCastingFeature);
                __instance.SetPointPool(heroBuildingData, HeroDefinitions.PointsPoolType.Cantrip, poolName,
                    heroBuildingData.TempAcquiredCantripsNumber + maxPoints);
                __instance.SetPointPool(heroBuildingData, HeroDefinitions.PointsPoolType.Spell, poolName,
                    heroBuildingData.TempAcquiredSpellsNumber);
                __instance.SetPointPool(heroBuildingData, HeroDefinitions.PointsPoolType.SpellUnlearn, poolName,
                    heroBuildingData.TempUnlearnedSpellsNumber);

                if (heroBuildingData.HeroCharacter.ActiveFeatures.TryGetValue(poolName, out var value))
                {
                    heroBuildingData.HeroCharacter.BrowseFeaturesOfType<FeatureDefinitionCastSpell>(
                        value,
                        (feature, s) => __instance.LearnFixedSpells(heroBuildingData, feature, s), poolName);
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.IsFeatMatchingPrerequisites))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsFeatMatchingPrerequisites_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            FeatDefinition feat,
            out (bool active, bool disableLevel, bool disableRace, bool disableCastSpell) __state)
        {
            __state = Tabletop2024Context.PushModFeatPrerequisiteOverride(
                Tabletop2024Context.ShouldForceManagedFeatPrerequisites(feat));
        }

        [UsedImplicitly]
        public static void Postfix(
            ref bool __result,
            CharacterHeroBuildingData heroBuildingData,
            FeatDefinition feat,
            ref bool isSameFamilyPrerequisite,
            (bool active, bool disableLevel, bool disableRace, bool disableCastSpell) __state)
        {
            Tabletop2024Context.RestoreModFeatPrerequisiteOverride(__state);

            //PATCH: fixes being able to select feats from same family when more than 1 feat selection is possible aat same time
            //vanilla code doesn't check if we already have selected feats from same family
            if (!__result || !feat.HasFamilyTag || string.IsNullOrEmpty(feat.FamilyTag))
            {
                return;
            }

            if (!heroBuildingData.levelupTrainedFeats.Any(pair =>
                    pair.Value.Any(f => f.HasFamilyTag && f.FamilyTag == feat.FamilyTag)))
            {
                return;
            }

            __result = false;
            isSameFamilyPrerequisite = true;
        }
    }

    //PATCH: considers subclass morphotype preferences
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.AssignDefaultMorphotypes))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AssignDefaultMorphotypes_Patch
    {
        private static RangedInt PreferedSkinColors(
            RacePresentation racePresentation,
            [NotNull] CharacterHeroBuildingData heroBuildingData)
        {
            var subRaceDefinition = heroBuildingData.HeroCharacter.SubRaceDefinition;

            return subRaceDefinition
                ? subRaceDefinition.RacePresentation.PreferedSkinColors
                : racePresentation.PreferedSkinColors;
        }

        private static RangedInt PreferedHairColors(
            RacePresentation racePresentation,
            [NotNull] CharacterHeroBuildingData heroBuildingData)
        {
            var subRaceDefinition = heroBuildingData.HeroCharacter.SubRaceDefinition;

            return subRaceDefinition
                ? subRaceDefinition.RacePresentation.PreferedHairColors
                : racePresentation.PreferedHairColors;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var preferedSkinColorsMethod = typeof(RacePresentation).GetMethod("get_PreferedSkinColors");
            var preferedHairColorsColorsMethod = typeof(RacePresentation).GetMethod("get_PreferedHairColors");
            var myPreferedSkinColorsMethod =
                new Func<RacePresentation, CharacterHeroBuildingData, RangedInt>(PreferedSkinColors).Method;
            var myPreferedHairColorsColorsMethod =
                new Func<RacePresentation, CharacterHeroBuildingData, RangedInt>(PreferedHairColors).Method;

            return instructions
                .ReplaceCalls(preferedSkinColorsMethod,
                    "CharacterBuildingManager.AssignDefaultMorphotypes.PreferedSkinColors",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, myPreferedSkinColorsMethod))
                .ReplaceCalls(preferedHairColorsColorsMethod,
                    "CharacterBuildingManager.AssignDefaultMorphotypes.PreferedHairColors",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, myPreferedHairColorsColorsMethod));
        }
    }

    //PATCH: apply point pools assigned from fighting styles
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.TrainFightingStyle))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TrainFightingStyle_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            CharacterBuildingManager __instance,
            RulesetCharacterHero hero,
            FightingStyleDefinition fightingStyle)
        {
            var heroBuildingData = hero.GetHeroBuildingData();

            foreach (var featureDefinitionPointPool in fightingStyle.Features.OfType<FeatureDefinitionPointPool>())
            {
                if (!heroBuildingData.PointPoolStacks.TryGetValue(featureDefinitionPointPool.PoolType,
                        out var pointPoolStack))
                {
                    continue;
                }

                __instance.GetLastAssignedClassAndLevel(hero, out var classDefinition, out var level);

                var finaTag = AttributeDefinitions.GetClassTag(classDefinition, level) +
                              featureDefinitionPointPool.ExtraSpellsTag;

                if (pointPoolStack.ActivePools
                    .TryGetValue(finaTag + featureDefinitionPointPool.ExtraSpellsTag, out var pool))
                {
                    pool.maxPoints += featureDefinitionPointPool.poolAmount;
                }
                else
                {
                    __instance.ApplyFeatureDefinitionPointPool(heroBuildingData, featureDefinitionPointPool, finaTag);
                }
            }

            LevelUpHelper.RebuildCharacterStageProficiencyPanel(heroBuildingData.LevelingUp);
        }
    }

    //PATCH: apply point pools assigned from feats
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.TrainFeat))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TrainFeat_Patch
    {
        private sealed class State
        {
            internal CharacterHeroBuildingData HeroBuildingData;
            internal FeatDefinition Feat;
            internal string Tag;
            internal int TrainedCountBefore;
            internal bool ValidatedManagedLeaf;
            internal FeatGrantedMetamagicPoolRollback MetamagicRollback;
            internal bool AppliedFeatGrantedMetamagicPool;
        }

        private static int GetEquivalentTrainedFeatCount(
            CharacterHeroBuildingData heroBuildingData,
            string tag,
            FeatDefinition feat)
        {
            return heroBuildingData != null &&
                   feat != null &&
                   !string.IsNullOrEmpty(tag) &&
                   heroBuildingData.LevelupTrainedFeats.TryGetValue(tag, out var feats)
                ? feats.Count(x => x &&
                                   Tabletop2024Context.AreEquivalentTabletopFeatNames(x.Name, feat.Name))
                : 0;
        }

        private static bool ApplyFeatGrantedMetamagicPointPools(
            CharacterBuildingManager manager,
            CharacterHeroBuildingData heroBuildingData,
            FeatDefinition feat,
            bool featAlreadyTrained)
        {
            if (manager == null ||
                heroBuildingData == null ||
                feat == null ||
                featAlreadyTrained)
            {
                return false;
            }

            var changed = false;

            foreach (var pointPoolFeature in feat.Features
                         .OfType<FeatureDefinitionPointPool>()
                         .Where(IsFeatGrantedMetamagicAdeptPointPool))
            {
                changed |= ApplyFeatGrantedMetamagicAdeptPointPool(
                    manager,
                    heroBuildingData,
                    pointPoolFeature,
                    true);
            }

            return changed;
        }

        private static bool ApplyFeatGrantedPointPools(
            CharacterBuildingManager manager,
            CharacterHeroBuildingData heroBuildingData,
            FeatDefinition feat)
        {
            if (manager == null ||
                heroBuildingData == null ||
                feat == null ||
                !feat.Features.OfType<FeatureDefinitionPointPool>().Any(IsFeatGrantedSpellOrCantripPointPool))
            {
                return false;
            }

            return SyncFeatGrantedPointPoolsForTrainedFeats(manager, heroBuildingData);
        }

        [UsedImplicitly]
        private static bool Prefix(
            CharacterBuildingManager __instance,
            CharacterHeroBuildingData heroBuildingData,
            ref FeatDefinition feat,
            string tag,
            out object __state)
        {
            __state = default;
            var buildingService = ServiceRepository.GetService<ICharacterBuildingService>();
            FeatDefinition resolvedFeat = null;
            var hero = heroBuildingData?.HeroCharacter;
            var validatedManagedLeaf = false;

            if (Tabletop2024Context.TryGetPendingFeatSelection(hero, tag, out var pendingFeat))
            {
                feat = pendingFeat;
            }
            else if (Tabletop2024Context.TryGetHumanOriginFeatToTrain(hero, tag, out var humanOriginFeat))
            {
                feat = humanOriginFeat;
            }
            else if (Tabletop2024Context.TryGetSingleOriginRestrictedFeatDefinition(
                         heroBuildingData,
                         tag,
                         out var backgroundOriginFeat))
            {
                feat = backgroundOriginFeat;
            }

            if (feat != null &&
                !Tabletop2024Context.TryResolveTrainableModeAwareFeat(feat, out resolvedFeat))
            {
                Tabletop2024Context.ClearPendingFeatSelection(hero, tag);
                return false;
            }

            if (resolvedFeat != null)
            {
                feat = resolvedFeat;
            }

            if (feat?.GetFirstSubFeatureOfType<IGroupedFeat>() != null)
            {
                Tabletop2024Context.ClearPendingFeatSelection(hero, tag);
                return false;
            }

            if (Main.Settings.EnableTabletopFeatRules2024 &&
                feat != null &&
                Tabletop2024Context.IsManagedTabletopFeat(feat))
            {
                SanitizeManagedTabletopTrainedFeatsForTag(
                    heroBuildingData,
                    tag,
                    buildingService?.GetPointPoolOfTypeAndTag(heroBuildingData, HeroDefinitions.PointsPoolType.Feat, tag),
                    feat,
                    buildingService);

                if (!Tabletop2024Context.TryPrepareIndependentFeatTraining(
                        heroBuildingData,
                        tag,
                        feat,
                        buildingService))
                {
                    Tabletop2024Context.ClearPendingFeatSelection(hero, tag);
                    return false;
                }

                validatedManagedLeaf = Tabletop2024Context.IsSelectableManagedTabletopFeatLeaf(feat);
            }

            if (feat == null)
            {
                Tabletop2024Context.ClearPendingFeatSelection(hero, tag);
                return false;
            }

            if (Tabletop2024Context.IsDuplicateHumanOriginFeatChoice(heroBuildingData?.HeroCharacter, tag, feat.Name))
            {
                Tabletop2024Context.ClearPendingFeatSelection(hero, tag);
                return false;
            }

            if (!Tabletop2024Context.IsFeatMatchingPrerequisites(
                    buildingService,
                    heroBuildingData,
                    feat,
                    out _))
            {
                Tabletop2024Context.ClearPendingFeatSelection(hero, tag);
                return false;
            }

            var trainedCountBefore = GetEquivalentTrainedFeatCount(heroBuildingData, tag, feat);
            var metamagicRollback = FeatGrantedMetamagicPoolRollback.Capture(__instance, heroBuildingData, feat);
            var appliedFeatGrantedMetamagicPool = ApplyFeatGrantedMetamagicPointPools(
                __instance,
                heroBuildingData,
                feat,
                trainedCountBefore > 0);

            if (appliedFeatGrantedMetamagicPool)
            {
                LevelUpHelper.RebuildCharacterStageProficiencyPanel(heroBuildingData.LevelingUp);
            }

            __state = new State
            {
                HeroBuildingData = heroBuildingData,
                Feat = feat,
                Tag = tag,
                TrainedCountBefore = trainedCountBefore,
                ValidatedManagedLeaf = validatedManagedLeaf,
                MetamagicRollback = metamagicRollback,
                AppliedFeatGrantedMetamagicPool = appliedFeatGrantedMetamagicPool
            };

            return true;
        }

        [UsedImplicitly]
        private static void Postfix(
            CharacterBuildingManager __instance,
            object __state)
        {
            if (__state is not State state)
            {
                return;
            }

            Tabletop2024Context.ClearPendingFeatSelection(state.HeroBuildingData?.HeroCharacter, state.Tag);

            if (state.HeroBuildingData == null ||
                state.Feat == null)
            {
                return;
            }

            var trainedCountAfter = GetEquivalentTrainedFeatCount(state.HeroBuildingData, state.Tag, state.Feat);

            if (trainedCountAfter <= state.TrainedCountBefore &&
                state.ValidatedManagedLeaf &&
                !string.IsNullOrEmpty(state.Tag) &&
                Tabletop2024Context.IsSelectableManagedTabletopFeatLeaf(state.Feat))
            {
                if (!state.HeroBuildingData.LevelupTrainedFeats.TryGetValue(state.Tag, out var feats))
                {
                    feats = [];
                    state.HeroBuildingData.LevelupTrainedFeats[state.Tag] = feats;
                }

                if (!feats.Any(existingFeat => existingFeat &&
                                              Tabletop2024Context.AreEquivalentTabletopFeatNames(
                                                  existingFeat.Name,
                                                  state.Feat.Name)))
                {
                    feats.Add(state.Feat);
                    trainedCountAfter = GetEquivalentTrainedFeatCount(state.HeroBuildingData, state.Tag, state.Feat);
#if DEBUG
                    Main.Log(
                        $"TrainFeat_Patch fallback added managed 2024 feat {state.Feat.Name} to tag {state.Tag}.");
#endif
                }
            }

            var featSelectedForTraining =
                !string.IsNullOrEmpty(state.Tag) &&
                __instance.IsFeatSelectedForTraining(state.HeroBuildingData, state.Feat, state.Tag);

            if (trainedCountAfter <= state.TrainedCountBefore &&
                !featSelectedForTraining)
            {
                if (state.AppliedFeatGrantedMetamagicPool)
                {
                    state.MetamagicRollback?.Restore(state.HeroBuildingData);
                    LevelUpHelper.RebuildCharacterStageProficiencyPanel(state.HeroBuildingData.LevelingUp);
                }

                return;
            }

            var rebuilt = state.AppliedFeatGrantedMetamagicPool;

            if (ApplyFeatGrantedPointPools(__instance, state.HeroBuildingData, state.Feat))
            {
                rebuilt = true;
            }

            if (rebuilt)
            {
                LevelUpHelper.RebuildCharacterStageProficiencyPanel(state.HeroBuildingData.LevelingUp);
            }
        }
    }

    //PATCH: remove point pools assigned from feats
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UntrainFeat))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UntrainFeat_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            CharacterBuildingManager __instance,
            CharacterHeroBuildingData heroBuildingData,
            FeatDefinition feat)
        {
            foreach (var featureDefinitionPointPool in feat.Features.OfType<FeatureDefinitionPointPool>())
            {
                if (IsFeatGrantedMetamagicAdeptPointPool(featureDefinitionPointPool))
                {
                    _ = RemoveFeatGrantedMetamagicAdeptPointPool(__instance, heroBuildingData, featureDefinitionPointPool);

                    continue;
                }

                if (!TryResolveFeatGrantedPointPoolTags(
                    __instance,
                    heroBuildingData,
                    featureDefinitionPointPool,
                    out _,
                    out _,
                    out var activePoolTag) ||
                    !TryGetFeatGrantedPointPoolForUpdate(
                        heroBuildingData,
                        featureDefinitionPointPool.PoolType,
                        activePoolTag,
                        featureDefinitionPointPool.ExtraSpellsTag,
                        out var activePoolKey,
                        out var pool))
                {
                    continue;
                }

                pool.maxPoints -= featureDefinitionPointPool.poolAmount;

                if (pool.maxPoints == 0 &&
                    heroBuildingData.PointPoolStacks.TryGetValue(featureDefinitionPointPool.PoolType,
                        out var pointPoolStack))
                {
                    pointPoolStack.ActivePools.Remove(activePoolKey);
                }
            }
        }

        [UsedImplicitly]
        public static void Postfix(CharacterHeroBuildingData heroBuildingData)
        {
            Tabletop2024Context.ClearPendingFeatSelections(heroBuildingData?.HeroCharacter);
        }
    }

    //PATCH: remove point pools assigned from feats
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.UntrainFeats))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UntrainFeats_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            CharacterBuildingManager __instance,
            CharacterHeroBuildingData heroBuildingData)
        {
            foreach (var feat in heroBuildingData.LevelupTrainedFeats
                         .SelectMany(entry => entry.Value)
                         .Where(feat => feat))
            {
                foreach (var featureDefinitionPointPool in feat.Features.OfType<FeatureDefinitionPointPool>())
                {
                    if (IsFeatGrantedMetamagicAdeptPointPool(featureDefinitionPointPool))
                    {
                        _ = RemoveFeatGrantedMetamagicAdeptPointPool(
                            __instance,
                            heroBuildingData,
                            featureDefinitionPointPool);

                        continue;
                    }

                    if (!TryResolveFeatGrantedPointPoolTags(
                        __instance,
                        heroBuildingData,
                        featureDefinitionPointPool,
                        out _,
                        out _,
                        out var activePoolTag) ||
                        !TryGetFeatGrantedPointPoolForUpdate(
                            heroBuildingData,
                            featureDefinitionPointPool.PoolType,
                            activePoolTag,
                            featureDefinitionPointPool.ExtraSpellsTag,
                            out var activePoolKey,
                            out var pool))
                    {
                        continue;
                    }

                    pool.maxPoints -= featureDefinitionPointPool.poolAmount;

                    if (pool.maxPoints == 0 &&
                        heroBuildingData.PointPoolStacks.TryGetValue(featureDefinitionPointPool.PoolType,
                            out var pointPoolStack))
                    {
                        pointPoolStack.ActivePools.Remove(activePoolKey);
                    }
                }
            }
        }

        [UsedImplicitly]
        public static void Postfix(CharacterHeroBuildingData heroBuildingData)
        {
            Tabletop2024Context.ClearPendingFeatSelections(heroBuildingData?.HeroCharacter);
        }
    }
}
