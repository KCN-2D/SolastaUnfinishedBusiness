using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GraphicsCharacterPatcher
{
    [HarmonyPatch(typeof(GraphicsCharacter), "LateUpdate")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LateUpdate_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var keysGetter = AccessTools.PropertyGetter(typeof(AnimationCurve), nameof(AnimationCurve.keys));
            var lengthGetter = AccessTools.PropertyGetter(typeof(AnimationCurve), nameof(AnimationCurve.length));
            var timeGetter = AccessTools.PropertyGetter(typeof(Keyframe), nameof(Keyframe.time));
            var replacement = AccessTools.Method(typeof(LateUpdate_Patch), nameof(GetLastKeyframeTime));
            var match = -1;
            var matches = 0;
            var keysCalls = 0;

            for (var i = 0; i < codes.Count; i++)
            {
                if (codes[i].Calls(keysGetter))
                {
                    keysCalls++;
                }

                if (i + 7 >= codes.Count || codes[i].opcode != OpCodes.Ldloc_0 ||
                    !codes[i + 1].Calls(keysGetter) || codes[i + 2].opcode != OpCodes.Ldloc_0 ||
                    !codes[i + 3].Calls(lengthGetter) || codes[i + 4].opcode != OpCodes.Ldc_I4_1 ||
                    codes[i + 5].opcode != OpCodes.Sub || codes[i + 6].opcode != OpCodes.Ldelema ||
                    !Equals(codes[i + 6].operand, typeof(Keyframe)) || !codes[i + 7].Calls(timeGetter))
                {
                    continue;
                }

                match = i;
                matches++;
            }

            if (matches != 1 || keysCalls != 1)
            {
                Main.Error("Failed to apply GraphicsCharacter.LateUpdate curve key allocation patch.");
                return codes;
            }

            for (var i = match + 1; i <= match + 7; i++)
            {
                if (codes[i].labels.Count != 0 || codes[i].blocks.Count != 0)
                {
                    Main.Error("Unexpected control flow in GraphicsCharacter.LateUpdate curve keys.");
                    return codes;
                }
            }

            // Retain the original curve load and its control-flow metadata. The native keys
            // getter copies every Keyframe even though this frame only reads the final time.
            codes.RemoveRange(match + 1, 7);
            codes.Insert(match + 1, new CodeInstruction(OpCodes.Call, replacement));
            return codes;
        }

        private static float GetLastKeyframeTime(AnimationCurve curve)
        {
            var index = curve.length - 1;

            // Preserve native null/empty failures while avoiding the normal keys array copy.
            return index < 0 ? curve.keys[index].time : curve[index].time;
        }

        [UsedImplicitly]
        public static void Postfix(
            GraphicsCharacter __instance,
            Transform ___ikRightHand,
            Transform ___ikLeftHand,
            int ___rightHandClosedLayerIndex,
            int ___leftHandClosedLayerIndex)
        {
            PortraitsContext.StabilizeInventoryHands(
                __instance,
                ___ikRightHand,
                ___ikLeftHand,
                ___rightHandClosedLayerIndex,
                ___leftHandClosedLayerIndex);
        }
    }

    private static bool UseInstrumentAnimation(GraphicsCharacter graphics, ActionDefinitions.Id actionId)
    {
        if (!graphics.CanUseMusicalInstrumentWhenCasting) { return false; }

        return ActionDefinitions.IsSpellAction(actionId)
               || actionId is ActionDefinitions.Id.GrantBardicInspiration;
    }

    [HarmonyPatch(typeof(GraphicsCharacter), "CheckWieldedItem")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckWieldedItem_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var nativeGetter = AccessTools.PropertyGetter(
                typeof(RulesetCharacterHero),
                nameof(RulesetCharacterHero.CanDualWieldNonLight));
            var replacement = AccessTools.Method(
                typeof(CheckWieldedItem_Patch),
                nameof(SupportsNonLightDualWielding));
            var characterField = AccessTools.Field(
                typeof(GraphicsCharacter),
                "rulesetCharacter");
            var getterIndex = -1;
            var getterCalls = 0;

            for (var index = 0; index < codes.Count; index++)
            {
                if (codes[index].Calls(nativeGetter))
                {
                    getterIndex = index;
                    getterCalls++;
                }
            }

            if (getterCalls != 1 ||
                getterIndex < 7 ||
                codes[getterIndex - 7].opcode != OpCodes.Ldarg_0 ||
                codes[getterIndex - 6].opcode != OpCodes.Ldfld ||
                !Equals(codes[getterIndex - 6].operand, characterField) ||
                codes[getterIndex - 5].opcode != OpCodes.Isinst ||
                !Equals(codes[getterIndex - 5].operand, typeof(RulesetCharacterHero)) ||
                (codes[getterIndex - 4].opcode != OpCodes.Brfalse &&
                 codes[getterIndex - 4].opcode != OpCodes.Brfalse_S) ||
                codes[getterIndex - 3].opcode != OpCodes.Ldarg_0 ||
                codes[getterIndex - 2].opcode != OpCodes.Ldfld ||
                !Equals(codes[getterIndex - 2].operand, characterField) ||
                codes[getterIndex - 1].opcode != OpCodes.Isinst ||
                !Equals(codes[getterIndex - 1].operand, typeof(RulesetCharacterHero)))
            {
                Main.Error(
                    "Failed to apply GraphicsCharacter.CheckWieldedItem " +
                    "dual-wield Hero gate patch.");

                return codes;
            }

            // Both casts are the two halves of the same C# `hero != null
            // ? hero.CanDualWieldNonLight : false` expression: the first feeds
            // its null branch and the second is the getter receiver.
            codes[getterIndex - 5].operand = typeof(RulesetCharacter);
            codes[getterIndex - 1].operand = typeof(RulesetCharacter);
            codes[getterIndex].opcode = OpCodes.Call;
            codes[getterIndex].operand = replacement;

            return codes;
        }

        private static bool SupportsNonLightDualWielding(
            RulesetCharacter character)
        {
            return SimulacrumBehavior.SupportsNonLightDualWielding(character);
        }
    }

    [HarmonyPatch(typeof(GraphicsCharacter), nameof(GraphicsCharacter.CastingStart))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CastingStart_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GraphicsCharacter __instance, ref ActionDefinitions.MagicEffectCastData spellCastData)
        {
            //PATCH: fixes Bardic Inspiration animation using weapon instead of instrument
            if (UseInstrumentAnimation(__instance, spellCastData.ActionId))
            {
                __instance.SetWieldedItemsActive(false);
                __instance.SetWieldedMusicalInstrumentsActive(true);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(GraphicsCharacter), nameof(GraphicsCharacter.CastingEnd))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CastingEnd_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GraphicsCharacter __instance, ActionDefinitions.Id actionId)
        {
            //PATCH: fixes Bardic Inspiration animation using weapon instead of instrument
            if (UseInstrumentAnimation(__instance, actionId))
            {
                __instance.SetWieldedItemsActive(true);
                __instance.SetWieldedMusicalInstrumentsActive(false);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(GraphicsCharacter), nameof(GraphicsCharacter.ResetScale))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ResetScale_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacter __instance, ref float __result)
        {
            //PATCH: Allows custom races with different scales
            if (__instance.RulesetCharacter is not RulesetCharacterHero rulesetCharacterHero ||
                !RacesContext.RaceScaleMap.TryGetValue(rulesetCharacterHero.RaceDefinition, out var scale))
            {
                return;
            }

            __result *= scale;
            __instance.transform.localScale = new Vector3(__result, __result, __result);
        }
    }
}
