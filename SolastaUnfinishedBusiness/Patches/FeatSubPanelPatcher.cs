using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;
using static HeroDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FeatSubPanelPatcher
{
    [HarmonyPatch(typeof(FeatSubPanel), nameof(FeatSubPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] FeatSubPanel __instance)
        {
            _ = __instance;
        }

        [UsedImplicitly]
        public static void Postfix([NotNull] FeatSubPanel __instance)
        {
            FeatsContext.RefreshPassiveFeatDisplayPanel(__instance);
        }
    }

    //PATCH: enforces the feat selection panel to always display same-width columns
    [HarmonyPatch(typeof(FeatSubPanel), nameof(FeatSubPanel.SetState))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetState_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            [NotNull] FeatSubPanel __instance,
            bool active,
            string stageTag,
            string previousStageTag,
            PointsPoolType currentPoolType,
            [HarmonyArgument(4)] ref List<string> restrictedChoices)
        {
            if (FeatsContext.IsFeatSelectionCandidateContext(
                    __instance,
                    active,
                    stageTag,
                    currentPoolType))
            {
                if (Main.Settings.EnableTabletopFeatRules2024)
                {
                    FeatsContext.PrepareRelevantFeatsFor2024Selection(
                        __instance,
                        stageTag,
                        previousStageTag,
                        currentPoolType,
                        ref restrictedChoices);
                }
                else
                {
                    FeatsContext.UpdateRelevantFeatListForLegacySelection(__instance);
                    FeatsContext.SortFeats(__instance);
                    FeatsContext.UpdatePanelChildren(__instance);
                    FeatsContext.RebindPanelChildren(
                        __instance,
                        stageTag,
                        previousStageTag,
                        currentPoolType,
                        true);
                }

                return;
            }

            if (FeatsContext.IsCharacterBuildingFeatSummaryContext(
                    __instance,
                    active,
                    stageTag,
                    currentPoolType))
            {
                FeatsContext.MergeRelevantFeatsForCharacterBuildingSummary(
                    __instance,
                    stageTag,
                    previousStageTag,
                    currentPoolType);

                return;
            }
        }

        [UsedImplicitly]
        public static void Postfix(
            [NotNull] FeatSubPanel __instance,
            bool active,
            string stageTag,
            string previousStageTag,
            PointsPoolType currentPoolType)
        {
            _ = active;
            _ = stageTag;
            _ = previousStageTag;
            _ = currentPoolType;

            FeatsContext.RefreshPassiveFeatDisplayPanel(__instance);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var forceRebuildLayoutImmediateMethod = typeof(LayoutRebuilder)
                .GetMethod("ForceRebuildLayoutImmediate", BindingFlags.Static | BindingFlags.Public);
            var forceSameWidthMethod =
                new Action<RectTransform, bool, FeatSubPanel>(FeatsContext.ForceSameWidth).Method;

            return instructions.ReplaceCalls(forceRebuildLayoutImmediateMethod, "FeatSubPanel.SetState",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, forceSameWidthMethod));
        }
    }

    [HarmonyPatch(typeof(FeatSubPanel), "OnFeatItemHoverChanged")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnFeatItemHoverChanged_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(FeatSubPanel __instance, bool hover, ProficiencyBaseItem item)
        {
            _ = __instance;
            _ = hover;
            _ = item;

            return false;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }

#if DEBUG
            Main.Log($"Suppressed FeatSubPanel.OnFeatItemHoverChanged exception: {__exception}");
#endif

            return null;
        }
    }
}
