using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterViewportPatcher
{
    private static bool PreservesUserRotation(CharacterViewport viewport) =>
        viewport.CanBeRotated && viewport.mode != CharacterViewport.Mode.Bestiary;

    [HarmonyPatch(typeof(CharacterViewport), nameof(CharacterViewport.ReturnsToInitialPosition), MethodType.Setter)]
    [UsedImplicitly]
    public static class ReturnsToInitialPosition_Setter_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterViewport __instance, ref bool __0)
        {
            if (PreservesUserRotation(__instance))
            {
                __0 = false;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterViewport), nameof(CharacterViewport.Bind))]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.ReplaceCalls(
                AccessTools.Method(typeof(CharacterViewport), nameof(CharacterViewport.Unbind)),
                "CharacterViewport.Bind.Unbind",
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(PortraitsContext), nameof(PortraitsContext.UnbindViewportForRebind))));
        }

        [UsedImplicitly]
        public static void Postfix(CharacterViewport __instance)
        {
            // A reused render target still contains the previous actor until the factory completes.
            __instance.rawImage.enabled = __instance.GraphicsCharacter != null;
        }

        [UsedImplicitly]
        public static Exception Finalizer(CharacterViewport __instance, Exception __exception)
        {
            if (__exception != null)
            {
                try
                {
                    PortraitsContext.ReleaseFailedViewportBinding(__instance);
                }
                catch (Exception cleanupException)
                {
                    Main.Error($"Unable to release failed character viewport binding: {cleanupException}");
                }
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(CharacterViewport), nameof(CharacterViewport.Unbind))]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterViewport __instance)
        {
            PortraitsContext.ReleaseViewportFraming(__instance);
            __instance.backToCenterCoroutine.Reset();
            __instance.dragArmed = false;
            __instance.rotationIntensity = 0;
            if (__instance.rawImage)
            {
                __instance.rawImage.enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterViewport), nameof(CharacterViewport.GraphicsCharacterCreated))]
    [UsedImplicitly]
    public static class GraphicsCharacterCreated_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterViewport __instance, GraphicsCharacter __0)
        {
            __instance.rawImage.enabled = true;
            if (__0 is GraphicsCharacterMonster
                {
                    RulesetCharacterMonster: RulesetCharacterSimulacrum
                } graphicsCharacter)
            {
                GraphicsCharacterFactoryManagerPatcher.ApplySimulacrumWeaponStance(graphicsCharacter);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterViewport), nameof(CharacterViewport.SetFollowAndLookAt))]
    [UsedImplicitly]
    public static class SetFollowAndLookAt_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterViewport __instance)
        {
            PortraitsContext.StabilizeViewportFraming(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterViewport), nameof(CharacterViewport.OnEndDrag))]
    [UsedImplicitly]
    public static class OnEndDrag_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterViewport __instance)
        {
            if (PreservesUserRotation(__instance))
            {
                __instance.ReturnsToInitialPosition = false;
            }
        }
    }
}
