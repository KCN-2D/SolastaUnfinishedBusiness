using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using static HeroDefinitions.PointsPoolType;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageProficiencySelectionPanelPatcher
{
    // Keep auto-learned fixed background feat steps suppressed on revisit within the same proficiency stage.
    private static readonly HashSet<string> AutoLearnedOriginFeatSteps = [];
    private static readonly MethodInfo OnLearnAutoImplMethod =
        AccessTools.Method(typeof(CharacterStageProficiencySelectionPanel), nameof(CharacterStageProficiencySelectionPanel.OnLearnAutoImpl));
    private static bool _autoLearningOriginFeat;
    private static bool _autoTrainingHumanOriginFeat;

    private static LearnStepItem CurrentStepItem(CharacterStageProficiencySelectionPanel __instance)
    {
        var table = __instance.learnStepsTable;
        LearnStepItem item = null;

        for (var i = 0; i < table.childCount; i++)
        {
            var child = table.GetChild(i);

            if (!child.gameObject.activeSelf || i != __instance.currentLearnStep)
            {
                continue;
            }

            item = child.GetComponent<LearnStepItem>();
            break;
        }

        return item;
    }

    private static string BuildAutoLearnKey(CharacterStageProficiencySelectionPanel __instance, LearnStepItem item)
    {
        return $"{__instance.currentHero?.Guid}:{__instance.currentLearnStep}:{item.PoolType}:{item.Tag}";
    }

    private static bool ShouldAutoLearnOriginFeat(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item)
    {
        return !_autoLearningOriginFeat &&
               item &&
               item.PoolType == Feat &&
               Tabletop2024Context.IsBackgroundBonusFeatsEnabled() &&
               Tabletop2024Context.TryGetSingleOriginRestrictedFeat(
                   __instance.currentHero?.GetHeroBuildingData(),
                   item.Tag,
                   out _);
    }

    private static bool TryAutoTrainHumanOriginFeat(
        CharacterStageProficiencySelectionPanel __instance,
        LearnStepItem item)
    {
        if (_autoTrainingHumanOriginFeat ||
            !item ||
            item.PoolType != Feat ||
            !Tabletop2024Context.TryGetHumanOriginFeatToTrain(__instance.currentHero, item.Tag, out var feat) ||
            Tabletop2024Context.IsDuplicateHumanOriginFeatChoice(__instance.currentHero, item.Tag, feat.Name))
        {
            return false;
        }

        var hero = __instance.currentHero;
        var buildingData = hero.GetHeroBuildingData();
        var heroBuildingCommandService = ServiceRepository.GetService<IHeroBuildingCommandService>();

        heroBuildingCommandService.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
        {
            try
            {
                _autoTrainingHumanOriginFeat = true;

                if (!__instance.CharacterBuildingService.IsFeatSelectedForTraining(buildingData, feat, item.Tag))
                {
                    Tabletop2024Context.ClearHumanOriginFeatTraining(buildingData);
                    __instance.CharacterBuildingService.TrainFeat(buildingData, feat, item.Tag, true);
                }

                __instance.OnPreRefresh();
                __instance.RefreshNow();
                __instance.MoveToNextLearnStep();
                __instance.ResetWasClickedFlag();
            }
            finally
            {
                _autoTrainingHumanOriginFeat = false;
            }
        });

        return true;
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
            var item = CurrentStepItem(__instance);

            if (!item)
            {
                return;
            }

            var hero = __instance.currentHero;
            var buildingData = hero.GetHeroBuildingData();
            var service = ServiceRepository.GetService<ICharacterBuildingService>();
            var needSkip = false;
            var pool = service.GetPointPoolOfTypeAndTag(buildingData, item.PoolType, item.Tag);

            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (item.PoolType)
            {
                case Skill:
                {
                    if (DatabaseRepository.GetDatabase<SkillDefinition>()
                        .All(s => service.IsSkillKnownOrTrained(buildingData, s)))
                    {
                        needSkip = true;
                    }

                    break;
                }
                case Tool:
                {
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

            if (!ShouldAutoLearnOriginFeat(__instance, item))
            {
                return;
            }

            var autoLearnKey = BuildAutoLearnKey(__instance, item);

            if (!AutoLearnedOriginFeatSteps.Add(autoLearnKey) || OnLearnAutoImplMethod == null)
            {
                return;
            }

            try
            {
                _autoLearningOriginFeat = true;
                OnLearnAutoImplMethod.Invoke(__instance, [null]);
            }
            catch
            {
                AutoLearnedOriginFeatSteps.Remove(autoLearnKey);
                throw;
            }
            finally
            {
                _autoLearningOriginFeat = false;
            }
        }
    }

    [HarmonyPatch(typeof(LearnStepItem), nameof(LearnStepItem.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LearnStepItemBind_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] LearnStepItem __instance)
        {
            if (!Tabletop2024Context.TryGetHumanOriginFeatLearnStepTitle(
                    __instance.PoolType,
                    __instance.Tag,
                    out var title))
            {
                return;
            }

            __instance.headerLabelActive.Text = title;
            __instance.headerLabelInactive.Text = title;
        }
    }

    [HarmonyPatch(typeof(CharacterStageProficiencySelectionPanel),
        nameof(CharacterStageProficiencySelectionPanel.OnLearnAutoImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnLearnAutoImpl_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterStageProficiencySelectionPanel __instance, Random rng)
        {
            //PATCH: support for skipping skill and tool proficiency picking if you picked all available, but still have points remaining
            if (rng != null)
            {
                return true;
            }

            var item = CurrentStepItem(__instance);

            if (!item || !item.ignoreAvailable || (item.PoolType != Skill && item.PoolType != Tool))
            {
                return true;
            }

            var hero = __instance.currentHero;
            var buildingData = hero.GetHeroBuildingData();

            var heroBuildingCommandService = ServiceRepository.GetService<IHeroBuildingCommandService>();

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
            AutoLearnedOriginFeatSteps.Clear();
            CampaignsContext.RefreshMetamagicOffering(__instance.metamagicSubPanel);
        }
    }
}
