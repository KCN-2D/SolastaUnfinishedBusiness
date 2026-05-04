using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;
using static HeroDefinitions.PointsPoolType;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageProficiencySelectionPanelPatcher
{
    // Keep auto-trained fixed background feat steps suppressed on revisit within the same proficiency stage.
    private static readonly HashSet<string> AutoLearnedOriginFeatSteps = [];
    private static readonly HashSet<string> AutoTrainedHumanOriginFeatSteps = [];
    private static readonly Random AutoFeatRandom = new();
    private static bool _autoTrainingOriginFeat;
    private static bool _autoTrainingHumanOriginFeat;
    private static bool _syncingFeatGrantedPointPools;
    private static bool _syncingHumanOriginPools;

    private static bool IsMetamagicAdeptLearnStep(LearnStepItem item)
    {
        return item &&
               item.PoolType == Metamagic &&
               string.Equals(item.Tag, MetamagicContext.FeatMetamagicAdeptPointPoolTag, StringComparison.Ordinal);
    }

    private static bool IsEldritchAdeptInvocationLearnStep(LearnStepItem item)
    {
        return item &&
               item.PoolType == Invocation &&
               string.Equals(item.Tag, OtherFeats.FeatEldritchAdeptPointPool, StringComparison.Ordinal);
    }

    private static bool ContainsMetamagicOptionByName(
        IEnumerable<MetamagicOptionDefinition> options,
        MetamagicOptionDefinition option)
    {
        return option != null &&
               options != null &&
               options.Any(existingOption => existingOption != null &&
                                             string.Equals(
                                                 existingOption.Name,
                                                 option.Name,
                                                 StringComparison.Ordinal));
    }

    private static bool IsMetamagicOptionSelectedForTag(
        CharacterHeroBuildingData buildingData,
        string tag,
        MetamagicOptionDefinition option)
    {
        return buildingData?.LevelupTrainedMetamagicOptions != null &&
               !string.IsNullOrEmpty(tag) &&
               buildingData.LevelupTrainedMetamagicOptions.TryGetValue(tag, out var selectedOptions) &&
               ContainsMetamagicOptionByName(selectedOptions, option);
    }

    private static bool IsMetamagicOptionTrainedOutsideTag(
        CharacterHeroBuildingData buildingData,
        string tag,
        MetamagicOptionDefinition option)
    {
        if (buildingData?.HeroCharacter != null &&
            ContainsMetamagicOptionByName(buildingData.HeroCharacter.TrainedMetamagicOptions, option))
        {
            return true;
        }

        return buildingData?.LevelupTrainedMetamagicOptions != null &&
               buildingData.LevelupTrainedMetamagicOptions
                   .Where(entry => !string.Equals(entry.Key, tag, StringComparison.Ordinal))
                   .Any(entry => ContainsMetamagicOptionByName(entry.Value, option));
    }

    private static int GetFeatGrantedMetamagicSelectedCount(CharacterHeroBuildingData buildingData, string tag)
    {
        return buildingData?.LevelupTrainedMetamagicOptions != null &&
               !string.IsNullOrEmpty(tag) &&
               buildingData.LevelupTrainedMetamagicOptions.TryGetValue(tag, out var selectedOptions) &&
               selectedOptions != null
            ? selectedOptions
                .Where(option => option != null && !string.IsNullOrEmpty(option.Name))
                .Select(option => option.Name)
                .Distinct(StringComparer.Ordinal)
                .Count()
            : 0;
    }

    private static MetamagicOptionDefinition[] GetAutoTrainableFeatGrantedMetamagicOptions(
        CharacterHeroBuildingData buildingData,
        LearnStepItem item,
        PointPool pointPool)
    {
        if (!IsMetamagicAdeptLearnStep(item) ||
            buildingData == null ||
            pointPool == null)
        {
            return [];
        }

        return MetamagicContext.GetVisibleMetamagicOptions()
            .Where(option => option != null)
            .Where(option => pointPool.RestrictedChoices is not { Count: > 0 } ||
                             pointPool.RestrictedChoices.Contains(option.Name))
            .Where(option => !IsMetamagicOptionSelectedForTag(buildingData, item.Tag, option))
            .Where(option => !IsMetamagicOptionTrainedOutsideTag(buildingData, item.Tag, option))
            .ToArray();
    }

    private static bool HasAvailableFeatGrantedMetamagicAutoChoice(
        CharacterHeroBuildingData buildingData,
        LearnStepItem item,
        ICharacterBuildingService service)
    {
        if (!IsMetamagicAdeptLearnStep(item) ||
            buildingData == null ||
            service == null)
        {
            return false;
        }

        var pointPool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);

        return pointPool is { remainingPoints: > 0 } &&
               GetAutoTrainableFeatGrantedMetamagicOptions(buildingData, item, pointPool).Length > 0;
    }

    private static LearnStepItem CurrentStepItem(CharacterStageProficiencySelectionPanel __instance)
    {
        if (__instance?.learnStepsTable == null ||
            __instance.currentLearnStep < 0)
        {
            return null;
        }

        var table = __instance.learnStepsTable;

        if (__instance.currentLearnStep < table.childCount)
        {
            var directChild = table.GetChild(__instance.currentLearnStep);
            var directItem = directChild.GetComponent<LearnStepItem>();

            if (directChild.gameObject.activeInHierarchy && directItem)
            {
                return directItem;
            }
        }

        var activeIndex = 0;

        for (var i = 0; i < table.childCount; i++)
        {
            var child = table.GetChild(i);

            if (!child.gameObject.activeInHierarchy)
            {
                continue;
            }

            var item = child.GetComponent<LearnStepItem>();

            if (!item)
            {
                continue;
            }

            if (activeIndex == __instance.currentLearnStep)
            {
                return item;
            }

            activeIndex++;
        }

        return null;
    }

    private static string BuildAutoLearnKey(CharacterStageProficiencySelectionPanel __instance, LearnStepItem item)
    {
        return $"{__instance.currentHero?.Guid}:{__instance.currentLearnStep}:{item.PoolType}:{item.Tag}";
    }

    private static IEnumerable<LearnStepItem> EnumerateLearnStepItems(CharacterStageProficiencySelectionPanel __instance)
    {
        if (__instance?.learnStepsTable == null)
        {
            yield break;
        }

        for (var i = 0; i < __instance.learnStepsTable.childCount; i++)
        {
            var item = __instance.learnStepsTable.GetChild(i).GetComponent<LearnStepItem>();

            if (item)
            {
                yield return item;
            }
        }
    }

    private static bool TryLocalizeResolved(string key, out string title)
    {
        title = Gui.Localize(key);

        return !string.IsNullOrEmpty(title) &&
               title != key &&
               !title.Contains("/&");
    }

    private static bool TryGetEldritchAdeptInvocationLearnStepTitle(
        LearnStepItem item,
        out string title)
    {
        title = null;

        if (!IsEldritchAdeptInvocationLearnStep(item))
        {
            return false;
        }

        if (!TryLocalizeResolved("Tooltip/&InvocationTitle", out var invocationTitle) &&
            !TryLocalizeResolved("Action/&CastInvocationTitle", out invocationTitle))
        {
            invocationTitle = "Invocation";
        }

        title = invocationTitle;

        return true;
    }

    private static bool TryGetLearnStepTitleOverride(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item,
        out string title)
    {
        title = null;

        if (__instance?.currentHero == null || !item)
        {
            return false;
        }

        if (Tabletop2024Context.IsHumanOriginSkilledSkillTag(item.Tag))
        {
            title =
                $"{Gui.Localize("Feature/&PointPoolHumanOriginFeatTitle")}: {Gui.Localize("Feature/&PointPoolSkilledTitle")}";

            return true;
        }

        if (item.PoolType == Skill &&
            Tabletop2024Context.IsBackgroundSkilledSkillTag(item.Tag))
        {
            title =
                $"{Gui.Localize("Feature/&BackgroundBonusFeatTitle")}: {Gui.Localize("Feature/&PointPoolSkilledTitle")}";

            return true;
        }

        if (Tabletop2024Context.TryGetHumanOriginFeatLearnStepTitle(item.PoolType, item.Tag, out var humanOriginTitle))
        {
            title = humanOriginTitle;

            if (Tabletop2024Context.TryGetHumanOriginFeatForDisplay(
                    __instance.currentHero,
                    __instance.currentHero.GetHeroBuildingData(),
                    out var humanOriginFeat))
            {
                title = $"{humanOriginTitle}: {humanOriginFeat.FormatTitle()}";
            }
            else if (Tabletop2024Context.TryGetHumanOriginSelectionFeature(__instance.currentHero, out var feature))
            {
                title = $"{humanOriginTitle}: {feature.FormatTitle()}";
            }

            return true;
        }

        if (TryGetEldritchAdeptInvocationLearnStepTitle(item, out title))
        {
            return true;
        }

        if (item.PoolType == Feat &&
            Tabletop2024Context.TryGetSingleOriginRestrictedFeatDefinition(
                __instance.currentHero.GetHeroBuildingData(),
                item.Tag,
                out var feat))
        {
            title = $"{Gui.Localize("Feature/&BackgroundBonusFeatTitle")}: {feat.FormatTitle()}";

            return true;
        }

        if (item.PoolType == Feat &&
            __instance.currentHero.GetHeroBuildingData()?.LevelupTrainedFeats.TryGetValue(item.Tag, out var trainedFeats) ==
            true &&
            trainedFeats.FirstOrDefault(trainedFeat => trainedFeat != null) is { } selectedFeat)
        {
            var baseTitle = Gui.LocalizeFeatTagTitle(item.Tag);

            if (string.IsNullOrEmpty(baseTitle) || baseTitle.Contains("/&"))
            {
                baseTitle = Gui.Localize("Tooltip/&FeatTitle");
            }

            title = $"{baseTitle}: {selectedFeat.FormatTitle()}";

            return true;
        }

        return false;
    }

    private static void RefreshLearnStepTitles(CharacterStageProficiencySelectionPanel __instance)
    {
        foreach (var item in EnumerateLearnStepItems(__instance))
        {
            if (!TryGetLearnStepTitleOverride(__instance, item, out var title))
            {
                continue;
            }

            item.headerLabelActive.Text = title;
            item.headerLabelInactive.Text = title;
        }
    }

    private static bool HasTrainedFeat(CharacterHeroBuildingData buildingData, string tag, FeatDefinition feat = null)
    {
        return buildingData != null &&
               !string.IsNullOrEmpty(tag) &&
               buildingData.LevelupTrainedFeats.TryGetValue(tag, out var feats) &&
               (feat == null
                   ? feats.Count > 0
                   : feats.Any(x => x &&
                                    Tabletop2024Context.AreEquivalentTabletopFeatNames(x.Name, feat.Name)));
    }

    private static int GetTrainedFeatCount(CharacterHeroBuildingData buildingData, string tag)
    {
        return buildingData != null &&
               !string.IsNullOrEmpty(tag) &&
               buildingData.LevelupTrainedFeats.TryGetValue(tag, out var feats)
            ? feats.Count
            : 0;
    }

    private static int GetEquivalentTrainedFeatCount(
        CharacterHeroBuildingData buildingData,
        string tag,
        FeatDefinition feat)
    {
        return buildingData != null &&
               feat != null &&
               !string.IsNullOrEmpty(tag) &&
               buildingData.LevelupTrainedFeats.TryGetValue(tag, out var feats)
            ? feats.Count(x => x &&
                               Tabletop2024Context.AreEquivalentTabletopFeatNames(x.Name, feat.Name))
            : 0;
    }

    private static bool SyncManagedPointPools(CharacterStageProficiencySelectionPanel __instance)
    {
        var buildingData = __instance?.currentHero?.GetHeroBuildingData();

        if (buildingData == null ||
            __instance.CharacterBuildingService == null ||
            _syncingHumanOriginPools ||
            _syncingFeatGrantedPointPools)
        {
            return false;
        }

        var changed = false;
        _syncingHumanOriginPools = true;
        _syncingFeatGrantedPointPools = true;

        try
        {
            changed |= Tabletop2024Context.SyncHumanOriginFeatPools(buildingData);
            changed |= CharacterBuildingManagerPatcher.SyncFeatGrantedPointPoolsForTrainedFeats(
                __instance.CharacterBuildingService,
                buildingData);
        }
        finally
        {
            _syncingFeatGrantedPointPools = false;
            _syncingHumanOriginPools = false;
        }

        return changed;
    }

    private static bool HasInvalidLearnStepBindings(CharacterStageProficiencySelectionPanel __instance)
    {
        var buildingData = __instance?.currentHero?.GetHeroBuildingData();
        var service = __instance?.CharacterBuildingService;

        if (buildingData == null ||
            service == null)
        {
            return false;
        }

        foreach (var item in EnumerateLearnStepItems(__instance))
        {
            if (!item ||
                !item.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag) != null)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    [HarmonyPatch(typeof(CharacterStagePanel), nameof(CharacterStagePanel.OnPreRefresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnPreRefresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStagePanel __instance)
        {
            if (__instance is not CharacterStageProficiencySelectionPanel proficiencySelectionPanel)
            {
                return;
            }

            var changed = SyncManagedPointPools(proficiencySelectionPanel);
            var hasInvalidLearnStepBindings = HasInvalidLearnStepBindings(proficiencySelectionPanel);

            if ((changed || hasInvalidLearnStepBindings) &&
                proficiencySelectionPanel.currentHero?.GetHeroBuildingData() is { } heroBuildingData)
            {
                LevelUpHelper.RebuildCharacterStageProficiencyPanel(heroBuildingData.LevelingUp);
            }
        }
    }

    private static bool IsHumanOriginFeatStep(LearnStepItem item)
    {
        return item &&
               Tabletop2024Context.TryGetHumanOriginFeatLearnStepTitle(item.PoolType, item.Tag, out _);
    }

    private static bool IsSingleOriginFeatStep(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item,
        out FeatDefinition feat)
    {
        feat = null;

        return item &&
               item.PoolType == Feat &&
               Tabletop2024Context.IsBackgroundBonusFeatsEnabled() &&
               Tabletop2024Context.TryGetSingleOriginRestrictedFeatDefinition(
                   __instance?.currentHero?.GetHeroBuildingData(),
                   item.Tag,
                   out feat);
    }

    private static bool TryResolveTrainableFeat(
        CharacterStageProficiencySelectionPanel __instance,
        FeatDefinition feat,
        out FeatDefinition resolvedFeat)
    {
        resolvedFeat = feat;

        return __instance?.CharacterBuildingService != null &&
               Tabletop2024Context.TryResolveTrainableModeAwareFeat(feat, out resolvedFeat);
    }

    private static bool TryTrainSingleOriginFeat(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item,
        bool useAutoTrainGuard)
    {
        if (_autoTrainingOriginFeat ||
            !IsSingleOriginFeatStep(__instance, item, out var feat) ||
            __instance?.currentHero?.GetHeroBuildingData() is not { } buildingData ||
            __instance.CharacterBuildingService == null ||
            ServiceRepository.GetService<IHeroBuildingCommandService>() is not { } heroBuildingCommandService)
        {
            return false;
        }

        if (!TryResolveTrainableFeat(__instance, feat, out feat))
        {
            return false;
        }

        var alreadySelected = HasTrainedFeat(buildingData, item.Tag, feat);

        string autoTrainKey = null;

        if (useAutoTrainGuard)
        {
            autoTrainKey = BuildAutoLearnKey(__instance, item);

            if (!AutoLearnedOriginFeatSteps.Add(autoTrainKey))
            {
                return false;
            }
        }

        heroBuildingCommandService.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
        {
            try
            {
                _autoTrainingOriginFeat = true;
                var trainedFeatCount = GetEquivalentTrainedFeatCount(buildingData, item.Tag, feat);

                if (!alreadySelected &&
                    !Tabletop2024Context.TryPrepareIndependentFeatTraining(
                        buildingData,
                        item.Tag,
                        feat,
                        __instance.CharacterBuildingService))
                {
                    if (useAutoTrainGuard)
                    {
                        AutoLearnedOriginFeatSteps.Remove(autoTrainKey);
                    }

                    __instance.ResetWasClickedFlag();

                    return;
                }

                if (!alreadySelected)
                {
                    __instance.CharacterBuildingService.TrainFeat(buildingData, feat, item.Tag, true);
                }

                var hasTrainedFeat = alreadySelected ||
                                     GetEquivalentTrainedFeatCount(buildingData, item.Tag, feat) > trainedFeatCount;

                __instance.OnPreRefresh();
                __instance.RefreshNow();

                if (hasTrainedFeat)
                {
                    __instance.MoveToNextLearnStep();
                }
                else if (useAutoTrainGuard)
                {
                    AutoLearnedOriginFeatSteps.Remove(autoTrainKey);
                }

                __instance.ResetWasClickedFlag();
            }
            catch
            {
                if (useAutoTrainGuard)
                {
                    AutoLearnedOriginFeatSteps.Remove(autoTrainKey);
                }

                throw;
            }
            finally
            {
                _autoTrainingOriginFeat = false;
            }
        });

        return true;
    }

    private static bool TryAutoTrainSingleOriginFeat(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item)
    {
        return TryTrainSingleOriginFeat(__instance, item, true);
    }

    private static bool TryTrainHumanOriginFeat(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item,
        bool useAutoTrainGuard)
    {
        if (_autoTrainingHumanOriginFeat ||
            !item ||
            item.PoolType != Feat ||
            !Tabletop2024Context.TryGetHumanOriginFeatToTrain(__instance.currentHero, item.Tag, out var feat) ||
            __instance?.currentHero?.GetHeroBuildingData() is not { } buildingData ||
            __instance.CharacterBuildingService == null ||
            ServiceRepository.GetService<IHeroBuildingCommandService>() is not { } heroBuildingCommandService ||
            Tabletop2024Context.IsDuplicateHumanOriginFeatChoice(__instance.currentHero, item.Tag, feat.Name))
        {
            return false;
        }

        if (!TryResolveTrainableFeat(__instance, feat, out feat))
        {
            return false;
        }

        var alreadySelected = HasTrainedFeat(buildingData, item.Tag, feat);

        string autoTrainKey = null;

        if (useAutoTrainGuard)
        {
            autoTrainKey = BuildAutoLearnKey(__instance, item);

            if (!AutoTrainedHumanOriginFeatSteps.Add(autoTrainKey))
            {
                return false;
            }
        }

        heroBuildingCommandService.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
        {
            try
            {
                _autoTrainingHumanOriginFeat = true;
                var trainedFeatCount = GetEquivalentTrainedFeatCount(buildingData, item.Tag, feat);

                if (!alreadySelected &&
                    !Tabletop2024Context.TryPrepareIndependentFeatTraining(
                        buildingData,
                        item.Tag,
                        feat,
                        __instance.CharacterBuildingService))
                {
                    if (useAutoTrainGuard)
                    {
                        AutoTrainedHumanOriginFeatSteps.Remove(autoTrainKey);
                    }

                    __instance.ResetWasClickedFlag();

                    return;
                }

                if (!alreadySelected)
                {
                    Tabletop2024Context.ClearHumanOriginFeatTraining(buildingData);
                    __instance.CharacterBuildingService.TrainFeat(buildingData, feat, item.Tag, true);
                }

                var hasTrainedFeat = alreadySelected ||
                                     GetEquivalentTrainedFeatCount(buildingData, item.Tag, feat) > trainedFeatCount;

                __instance.OnPreRefresh();
                __instance.RefreshNow();

                if (hasTrainedFeat)
                {
                    __instance.MoveToNextLearnStep();
                }
                else if (useAutoTrainGuard)
                {
                    AutoTrainedHumanOriginFeatSteps.Remove(autoTrainKey);
                }

                __instance.ResetWasClickedFlag();
            }
            catch
            {
                if (useAutoTrainGuard)
                {
                    AutoTrainedHumanOriginFeatSteps.Remove(autoTrainKey);
                }

                throw;
            }
            finally
            {
                _autoTrainingHumanOriginFeat = false;
            }
        });

        return true;
    }

    private static bool TryAutoTrainHumanOriginFeat(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item)
    {
        return TryTrainHumanOriginFeat(__instance, item, true);
    }

    private static void CollectAutoSelectableFeats(
        HashSet<FeatDefinition> candidates,
        FeatDefinition feat,
        FeatDefinition parentGroupedFeat,
        HashSet<string> restrictedChoices,
        ICharacterBuildingService service,
        CharacterHeroBuildingData buildingData,
        string tag)
    {
        if (feat.GetFirstSubFeatureOfType<IGroupedFeat>() is { } groupedFeat)
        {
            if (!Tabletop2024Context.IsAllowedInGameFeatSelectionByConfiguration(feat) ||
                !Tabletop2024Context.IsVisibleInGameFeatSelection(feat) ||
                Tabletop2024Context.IsNonSelectableTabletopGroup(feat))
            {
                return;
            }

            var subFeats = Main.Settings.EnableTabletopFeatRules2024
                ? Tabletop2024Context.GetAllowedGameFeatChildren(feat)
                : groupedFeat.GetSubFeats(true);

            foreach (var subFeat in subFeats)
            {
                CollectAutoSelectableFeats(candidates, subFeat, feat, restrictedChoices, service, buildingData, tag);
            }

            return;
        }

        if (!Tabletop2024Context.IsAllowedInGameFeatSelectionByConfiguration(feat))
        {
            return;
        }

        if (!Tabletop2024Context.MatchesRestrictedChoice(feat, parentGroupedFeat, restrictedChoices))
        {
            return;
        }

        if (!Tabletop2024Context.IsFeatMatchingPrerequisites(service, buildingData, feat, out _) ||
            service.IsFeatKnownOrTrained(buildingData, feat) ||
            service.IsFeatSelectedForTraining(buildingData, feat, tag))
        {
            return;
        }

        candidates.Add(feat);
    }

    private static List<FeatDefinition> GetVisibleAutoSelectableFeats(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item)
    {
        if (!item ||
            item.PoolType != Feat ||
            __instance?.currentHero?.GetHeroBuildingData() is not { } buildingData ||
            __instance.CharacterBuildingService == null)
        {
            return [];
        }

        var service = __instance.CharacterBuildingService;
        var pointPool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);

        if (pointPool == null)
        {
            return [];
        }

        var restrictedChoices = Tabletop2024Context.GetModeAwareRestrictedChoiceNames(pointPool).ToHashSet();
        var candidates = new HashSet<FeatDefinition>();

        foreach (var feat in Tabletop2024Context.GetGameFeatSelectionCatalogRoots())
        {
            CollectAutoSelectableFeats(candidates, feat, null, restrictedChoices, service, buildingData, item.Tag);
        }

        return [.. candidates.OrderBy(feat => feat.FormatTitle())];
    }

    private static bool TryTrainVisibleFeat(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item,
        Random rng)
    {
        if (!item ||
            item.PoolType != Feat ||
            __instance?.currentHero?.GetHeroBuildingData() is not { } buildingData ||
            __instance.CharacterBuildingService == null ||
            ServiceRepository.GetService<IHeroBuildingCommandService>() is not { } heroBuildingCommandService)
        {
            return false;
        }

        var candidates = GetVisibleAutoSelectableFeats(__instance, item);

        if (candidates.Count == 0)
        {
            return false;
        }

        var selectedFeat = candidates[(rng ?? AutoFeatRandom).Next(candidates.Count)];

        if (!TryResolveTrainableFeat(__instance, selectedFeat, out selectedFeat) ||
            !Tabletop2024Context.TryPrepareIndependentFeatTraining(
                buildingData,
                item.Tag,
                selectedFeat,
                __instance.CharacterBuildingService))
        {
            return false;
        }

        heroBuildingCommandService.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
        {
            var trainedFeatCount = GetEquivalentTrainedFeatCount(buildingData, item.Tag, selectedFeat);

            if (!HasTrainedFeat(buildingData, item.Tag, selectedFeat))
            {
                __instance.CharacterBuildingService.TrainFeat(buildingData, selectedFeat, item.Tag, true);
            }

            var hasTrainedFeat =
                GetEquivalentTrainedFeatCount(buildingData, item.Tag, selectedFeat) > trainedFeatCount;

            __instance.OnPreRefresh();
            __instance.RefreshNow();

            if (hasTrainedFeat)
            {
                __instance.MoveToNextLearnStep();
            }

            __instance.ResetWasClickedFlag();
        });

        return true;
    }

    [HarmonyPatch(typeof(CharacterStageProficiencySelectionPanel),
        nameof(CharacterStageProficiencySelectionPanel.OnProficiencyItemClicked))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnProficiencyItemClicked_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterStageProficiencySelectionPanel __instance,
            ProficiencyBaseItem item)
        {
            if (item is not FeatItem featItem ||
                featItem.CurrentPoolType != Feat ||
                featItem.GuiFeatDefinition?.FeatDefinition is not { } featDefinition)
            {
                return true;
            }

            if (!Main.Settings.EnableTabletopFeatRules2024)
            {
                return true;
            }

            var hero = __instance?.currentHero;
            var service = __instance?.CharacterBuildingService;

            if (!Tabletop2024Context.CanSelectFeatForCurrentPointPool(hero, featItem.StageTag, featDefinition, service))
            {
                Tabletop2024Context.ClearPendingFeatSelection(hero, featItem.StageTag);

                return false;
            }

            Tabletop2024Context.RememberPendingFeatSelection(
                hero,
                featItem.StageTag,
                featDefinition);

            return true;
        }
    }

    [HarmonyPatch(typeof(CharacterStageProficiencySelectionPanel),
        nameof(CharacterStageProficiencySelectionPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageProficiencySelectionPanel __instance)
        {
            //PATCH: support for skipping skill and tool proficiency picking if you picked all available, but still have points remaining
            var hero = __instance.currentHero;
            var service = ServiceRepository.GetService<ICharacterBuildingService>();
            var buildingData = hero?.GetHeroBuildingData();

            if (buildingData == null ||
                service == null)
            {
                return;
            }

            RefreshLearnStepTitles(__instance);

            var item = CurrentStepItem(__instance);

            if (!item)
            {
                return;
            }

            if (HasAvailableFeatGrantedMetamagicAutoChoice(buildingData, item, service) &&
                !item.autoLearnAvailable)
            {
                item.autoLearnAvailable = true;
                item.Refresh(LearnStepItem.Status.InProgress);
            }

            var needSkip = false;
            PointPool pool = null;

            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (item.PoolType)
            {
                case Skill:
                {
                    pool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);

                    if (pool == null)
                    {
                        return;
                    }

                    if (DatabaseRepository.GetDatabase<SkillDefinition>()
                        .All(s => service.IsSkillKnownOrTrained(buildingData, s)))
                    {
                        needSkip = true;
                    }

                    break;
                }
                case Tool:
                {
                    pool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);

                    if (pool == null)
                    {
                        return;
                    }

                    if (DatabaseRepository
                        //get all restricted tools
                        .GetDatabase<ToolTypeDefinition>()
                        //remove ones already known or trained this level
                        .Where(s =>
                            pool.RestrictedChoices == null ||
                            pool.RestrictedChoices.Count == 0 ||
                            pool.RestrictedChoices.Contains(s.Name))
                        .All(s => service.IsToolTypeKnownOrTrained(buildingData, s)))
                    {
                        needSkip = true;
                    }

                    break;
                }
            }

            if (needSkip)
            {
                item.ignoreAvailable = true;
                item.Refresh(LearnStepItem.Status.InProgress);

                return;
            }

            if (TryAutoTrainHumanOriginFeat(__instance, item))
            {
                return;
            }

            if (TryAutoTrainSingleOriginFeat(__instance, item))
            {
                return;
            }

            if (!Main.Settings.EnableTabletopFeatRules2024)
            {
                return;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageProficiencySelectionPanel),
        nameof(CharacterStageProficiencySelectionPanel.OnLearnAutoImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnLearnAutoImpl_Patch
    {
        private sealed class State
        {
            internal CharacterHeroBuildingData BuildingData;
            internal string Tag;
            internal int RemainingPointsBefore;
            internal int SelectedCountBefore;
            internal bool IsMetamagicAdeptStep;
        }

        [UsedImplicitly]
        public static bool Prefix(CharacterStageProficiencySelectionPanel __instance, Random rng, out object __state)
        {
            __state = null;

            //PATCH: support for skipping skill and tool proficiency picking if you picked all available, but still have points remaining
            var item = CurrentStepItem(__instance);

            if (!item)
            {
                return true;
            }

            var buildingData = __instance.currentHero?.GetHeroBuildingData();

            if (IsMetamagicAdeptLearnStep(item) &&
                buildingData != null &&
                __instance.CharacterBuildingService != null)
            {
                var pointPool = __instance.CharacterBuildingService.GetPointPoolOfTypeAndTag(
                    buildingData,
                    item.PoolType,
                    item.Tag);

                __state = new State
                {
                    BuildingData = buildingData,
                    Tag = item.Tag,
                    RemainingPointsBefore = pointPool?.remainingPoints ?? -1,
                    SelectedCountBefore = GetFeatGrantedMetamagicSelectedCount(buildingData, item.Tag),
                    IsMetamagicAdeptStep = true
                };
            }

            if (item.PoolType == Feat)
            {
                if (IsHumanOriginFeatStep(item))
                {
                    if (!TryTrainHumanOriginFeat(__instance, item, false) &&
                        Tabletop2024Context.IsHumanOriginSkilledSelected(__instance.currentHero))
                    {
                        Tabletop2024Context.SyncHumanOriginFeatPools(__instance.currentHero?.GetHeroBuildingData());
                        __instance.OnPreRefresh();
                        __instance.RefreshNow();
                    }

                    return false;
                }

                if (IsSingleOriginFeatStep(__instance, item, out _))
                {
                    TryTrainSingleOriginFeat(__instance, item, false);

                    return false;
                }

                if (!Main.Settings.EnableTabletopFeatRules2024)
                {
                    return true;
                }

                TryTrainVisibleFeat(__instance, item, rng ?? AutoFeatRandom);

                return false;
            }

            if (rng != null)
            {
                return true;
            }

            if (!item || !item.ignoreAvailable || (item.PoolType != Skill && item.PoolType != Tool))
            {
                return true;
            }

            var hero = __instance.currentHero;
            var heroBuildingCommandService = ServiceRepository.GetService<IHeroBuildingCommandService>();
            buildingData = hero?.GetHeroBuildingData();

            if (buildingData == null ||
                heroBuildingCommandService == null ||
                __instance.CharacterBuildingService == null)
            {
                return true;
            }

            heroBuildingCommandService.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
            {
                __instance.CharacterBuildingService
                    .GetPoolPointsOfTypeAndTag(buildingData, item.PoolType, item.Tag, out _, out _);
                __instance.OnPreRefresh();
                __instance.RefreshNow();
                __instance.MoveToNextLearnStep();
                __instance.ResetWasClickedFlag();
            });

            return false;
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageProficiencySelectionPanel __instance, object __state)
        {
            if (__state is not State { IsMetamagicAdeptStep: true } state)
            {
                return;
            }

            var item = CurrentStepItem(__instance);
            var buildingData = __instance.currentHero?.GetHeroBuildingData() ?? state.BuildingData;
            var service = __instance.CharacterBuildingService;

            if (!IsMetamagicAdeptLearnStep(item) ||
                buildingData == null ||
                service == null)
            {
                return;
            }

            var pointPool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);
            var selectedCount = GetFeatGrantedMetamagicSelectedCount(buildingData, item.Tag);

            if (pointPool == null ||
                pointPool.remainingPoints < state.RemainingPointsBefore ||
                selectedCount > state.SelectedCountBefore)
            {
                return;
            }

            var trained = 0;

            while (pointPool.remainingPoints > 0)
            {
                var option = GetAutoTrainableFeatGrantedMetamagicOptions(buildingData, item, pointPool)
                    .FirstOrDefault();

                if (option == null)
                {
                    break;
                }

                var remainingBefore = pointPool.remainingPoints;
                var selectedBefore = GetFeatGrantedMetamagicSelectedCount(buildingData, item.Tag);

                service.TrainMetamagicOption(buildingData, option, item.Tag, true);

                pointPool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);
                selectedCount = GetFeatGrantedMetamagicSelectedCount(buildingData, item.Tag);

                if (pointPool == null ||
                    (pointPool.remainingPoints >= remainingBefore && selectedCount <= selectedBefore))
                {
                    break;
                }

                trained++;
            }

            if (trained <= 0)
            {
                return;
            }

            __instance.OnPreRefresh();
            __instance.RefreshNow();

            pointPool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);

            if (pointPool is { remainingPoints: <= 0 })
            {
                __instance.MoveToNextLearnStep();
                __instance.ResetWasClickedFlag();
            }
        }
    }

    //PATCH: allow refreshing custom metamagic options to avoid requires restart when tweaking mod ui options
    [HarmonyPatch(typeof(CharacterStageProficiencySelectionPanel),
        nameof(CharacterStageProficiencySelectionPanel.EnterStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnterStage_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStageProficiencySelectionPanel __instance)
        {
            CampaignsContext.RefreshMetamagicOffering(__instance.metamagicSubPanel);
            AutoLearnedOriginFeatSteps.Clear();
            AutoTrainedHumanOriginFeatSteps.Clear();
            Tabletop2024Context.ClearPendingFeatSelections(__instance.currentHero);
            Tabletop2024Context.EnsureHumanOriginFeatStateMatchesRace(__instance.currentHero?.GetHeroBuildingData());
        }
    }
}
