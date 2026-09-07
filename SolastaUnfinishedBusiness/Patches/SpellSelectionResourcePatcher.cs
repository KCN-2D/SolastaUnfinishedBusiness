using System;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class SpellSelectionResourcePatcher
{
    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.GetSlotsNumber))]
    [UsedImplicitly]
    private static class ViewSlots
    {
        [HarmonyPriority(Priority.First)]
        [UsedImplicitly]
        private static bool Prefix(RulesetSpellRepertoire __instance, int __0, ref int __1, ref int __2)
        {
            if (!SpellSelectionContext.TryGetOption(__instance, out var option))
            {
                return true;
            }

            SpellSelectionContext.GetViewSlots(option, __0, out __1, out __2);
            return false;
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.MaxSpellLevelOfSpellCastingLevel),
        MethodType.Getter)]
    [UsedImplicitly]
    private static class ViewLevel
    {
        [HarmonyPriority(Priority.First)]
        [UsedImplicitly]
        private static bool Prefix(RulesetSpellRepertoire __instance, ref int __result)
        {
            if (!SpellSelectionContext.TryGetOption(__instance, out var option))
            {
                return true;
            }

            __result = option.SlotLevel;
            return false;
        }
    }

    [HarmonyPatch(typeof(SpellsByLevelBox), nameof(SpellsByLevelBox.OnActivateStandardBox))]
    [UsedImplicitly]
    private static class StandardCast
    {
        [UsedImplicitly]
        private static bool Prefix(SpellsByLevelBox __instance, int index, out IDisposable __state)
        {
            __state = null;
            if (!__instance.spellsByIndex.TryGetValue(index, out var spell) || spell.SpellLevel == 0 ||
                __instance.spellRepertoire.IsMysticArcanumSpell(spell))
            {
                return true;
            }

            var repertoire = __instance.spellRepertoire;
            var option = SpellSelectionContext.GetBaseSelection(__instance.caster, repertoire, spell);
            return Begin(__instance, option, out __state);
        }

        [UsedImplicitly]
        private static void Finalizer(IDisposable __state) => __state?.Dispose();
    }

    [HarmonyPatch(typeof(SpellsByLevelBox), nameof(SpellsByLevelBox.OnActivateAdvancedBox))]
    [UsedImplicitly]
    private static class AdvancedCast
    {
        [UsedImplicitly]
        private static bool Prefix(SpellsByLevelBox __instance, int index, int slotLevel, out IDisposable __state)
        {
            GameLocationCharacter.GetFromActor(__instance.caster)?.RegisterShiftState();
            __state = null;
            return !__instance.spellsByIndex.TryGetValue(index, out var spell) ||
                   Begin(__instance, SpellSelectionContext.GetSelection(__instance.spellRepertoire, spell, slotLevel),
                       out __state);
        }

        [UsedImplicitly]
        private static void Finalizer(IDisposable __state) => __state?.Dispose();
    }

    private static bool Begin(SpellsByLevelBox box, SpellCastingResourceContext.ResourceOption option, out IDisposable state)
    {
        state = null;
        if (!option.IsAvailable(box.caster))
        {
            return false;
        }

        state = SpellCastingResourceContext.BeginSelection(option);
        return true;
    }
}
