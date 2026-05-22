using System.Collections.Generic;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CursorLocationBattleFriendlyTurnPatcher
{
    [HarmonyPatch(typeof(CursorLocationBattleFriendlyTurn), nameof(CursorLocationBattleFriendlyTurn.IsValidAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsValidAttack_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: ReachMeleeTargeting
            var findBestActionDestinationMethod = typeof(CursorLocationBattleFriendlyTurn)
                .GetMethod("FindBestActionDestination", BindingFlags.Instance | BindingFlags.NonPublic);
            var method = typeof(ReachMeleeTargeting)
                .GetMethod("FindBestActionDestination", BindingFlags.Static | BindingFlags.NonPublic);

            return instructions.ReplaceCalls(findBestActionDestinationMethod,
                "CursorLocationBattleFriendlyTurn.IsValidAttack",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldloc_2),
                new CodeInstruction(OpCodes.Call, method));
        }
    }

    [HarmonyPatch(typeof(CursorLocationBattleFriendlyTurn), nameof(CursorLocationBattleFriendlyTurn.IsValidTarget))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsValidTarget_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] CursorLocationBattleFriendlyTurn __instance,
            GameLocationCharacter targetCharacter, out bool __result)
        {
            //BUGFIX: do not allow attacking effect proxies
            __result = targetCharacter is { RulesetActor: not RulesetCharacterEffectProxy }
                       && __instance is { Battle: not null, actingCharacter: not null }
                       && targetCharacter.Side == RuleDefinitions.GetOpposingSide(__instance.actingCharacter.Side);
        }
    }

    [HarmonyPatch(typeof(CursorLocationBattleFriendlyTurn), nameof(CursorLocationBattleFriendlyTurn.Initialize))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Initialize_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] CursorLocationBattleFriendlyTurn __instance)
        {
            //PATCH: 
            CursorMotionHelper.Initialize(__instance.chainHelperPrefab);
        }
    }

    [HarmonyPatch(typeof(CursorLocationBattleFriendlyTurn), nameof(CursorLocationBattleFriendlyTurn.ComputeValidDestinations))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeValidDestinations_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CursorLocationBattleFriendlyTurn __instance, ref IDisposable __state)
        {
            __state = FreeJumpContext.BeginCursorDestinationComputation(__instance);

            return __state != null;
        }

        [UsedImplicitly]
        public static void Postfix(CursorLocationBattleFriendlyTurn __instance, IDisposable __state)
        {
            if (__state == null)
            {
                return;
            }

            try
            {
                if (FreeJumpContext.ApplyBonusActionDestinations(
                        __instance.actingCharacter,
                        __instance.constrainedMovementMode,
                        __instance.validDestinations))
                {
                    __instance.RefreshVisibleDestinationsGrid();
                }
            }
            finally
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(CursorLocationBattleFriendlyTurn), "BuildActionChain")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildActionChain_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CursorLocationBattleFriendlyTurn __instance, Id actionId)
        {
            return !FreeJumpContext.TryBuildBattleFreeJumpActionChain(__instance, actionId);
        }
    }

    [HarmonyPatch(typeof(CursorLocationBattleFriendlyTurn), "ProcessAction")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ProcessAction_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CursorLocationBattleFriendlyTurn __instance, Id actionId)
        {
            return !FreeJumpContext.TryCancelInvalidBattleFreeJumpAction(__instance, actionId);
        }
    }

    [HarmonyPatch(typeof(CursorLocationBattleFriendlyTurn), nameof(CursorLocationBattleFriendlyTurn.RefreshHover))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshHover_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CursorLocationBattleFriendlyTurn __instance)
        {
            FreeJumpContext.RefreshBattleSelectionCaption(__instance);
        }
    }
}
