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
