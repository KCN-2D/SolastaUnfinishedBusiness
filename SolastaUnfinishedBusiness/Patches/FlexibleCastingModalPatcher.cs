using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FlexibleCastingModalPatcher
{
    //PATCH: register on acting character if SHIFT is pressed on slots convertions
    [HarmonyPatch(typeof(FlexibleCastingModal), nameof(FlexibleCastingModal.OnConvertCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnConvertCb_Patch
    {
        [UsedImplicitly]
        public static void Prefix(FlexibleCastingModal __instance)
        {
            if (__instance.selectedSlotLevel < 0 || __instance.createSlotMode)
            {
                return;
            }

            var rulesetCaster = __instance.caster;
            var caster = GameLocationCharacter.GetFromActor(rulesetCaster);

            caster?.RegisterShiftState();
        }
    }

    [HarmonyPatch(typeof(FlexibleCastingModal), nameof(FlexibleCastingModal.CreateSorceryPoints))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CreateSorceryPoints_Patch
    {
        [UsedImplicitly]
        public static void Prefix(FlexibleCastingModal __instance, out RulesetSpellRepertoire __state)
        {
            __state = __instance.repertoire;
            // Native multiplayer transports this repertoire's definition name. Select the
            // payment owner before sending it, while keeping the modal's casting source.
            __instance.repertoire = SpellCastingResourceContext.SelectSlotPaymentRepertoire(
                __instance.caster, __state, __instance.selectedSlotLevel);
        }

        [UsedImplicitly]
        public static void Finalizer(FlexibleCastingModal __instance, RulesetSpellRepertoire __state)
        {
            __instance.repertoire = __state;
        }
    }
}
