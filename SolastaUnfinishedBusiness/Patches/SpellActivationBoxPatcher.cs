using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellActivationBoxPatcher
{
    private static readonly ConditionalWeakTable<SpellActivationBox, SpellCastingResourceContext.ResourceOption> FreeBindings = new();

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.BindSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindSpell_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            SpellActivationBox __instance,
            ref RulesetSpellRepertoire __1,
            out IDisposable __state)
        {
            FreeBindings.Remove(__instance);
            if (SpellSelectionContext.TryGetOption(__1, out var option))
            {
                FreeBindings.Add(__instance, option);
                __1 = option.Repertoire;
            }

            SpellCastingValidation.BindTooltipRepertoire(__instance.tooltip, __1);
            __state = SpellCastingValidation.EnterSelectedRepertoire(__1);
        }

        [UsedImplicitly]
        public static void Postfix(
            SpellActivationBox __instance,
            RulesetCharacter __0,
            RulesetSpellRepertoire __1,
            SpellDefinition __2)
        {
            if (FreeBindings.TryGetValue(__instance, out _))
            {
                __instance.hasUpcast = false;
                __instance.upcastButton.gameObject.SetActive(false);
                __instance.closeAdvancementButton.gameObject.SetActive(false);
            }

            if (!__instance.globalValid ||
                SpellCastingValidation.IsValid(__0, __1, __2, null, out _))
            {
                return;
            }

            __instance.globalValid = false;
            __instance.canvasGroup.interactable = false;
            __instance.image.color = Color.grey;
            __instance.image.material = __instance.unavailableMaterial;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();

            return __exception;
        }

        private static bool UniqueLevelSlots(
            FeatureDefinitionCastSpell featureDefinitionCastSpell,
            RulesetCharacter character)
        {
            //PATCH: MC casters must use the standard slot picker so shared and pact slots can coexist
            var caster = character.GetOriginalHero() ?? character;

            return featureDefinitionCastSpell.UniqueLevelSlots &&
                   (!featureDefinitionCastSpell.UsesSharedSpellSlots() ||
                    !SharedSpellsContext.IsMulticaster(caster));
        }

        [UsedImplicitly]
        public static void MyGetSlotsNumber(
            RulesetSpellRepertoire repertoire,
            int spellLevel,
            out int remaining,
            out int max,
            RulesetCharacter caster,
            SpellActivationBox spellActivationBox)
        {
            if (FreeBindings.TryGetValue(spellActivationBox, out var free))
            {
                SpellSelectionContext.GetViewSlots(free, spellLevel, out remaining, out max);
                if (!free.IsAvailable(caster))
                {
                    remaining = 0;
                }

                return;
            }

            if (SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire) &&
                repertoire.SpellCastingFeature.CannotUpcast &&
                spellActivationBox.GuiSpellDefinition?.SpellDefinition is { } fixedSpell)
            {
                var fixedUse = SpellSelectionContext.GetBaseSelection(caster, repertoire, fixedSpell);
                fixedUse.GetUses(caster, out remaining, out max);
                return;
            }

            if (repertoire.UsesSharedSpellSlots() && caster.IsSpellPointsEnabled())
            {
                max = 1;
                remaining = SpellPointsContext.CanCastSpellOfLevel(caster, repertoire, spellLevel) ? 1 : 0;
            }
            else
            {
                repertoire.GetDisplaySlotNumbers(caster.GetOriginalHero() ?? caster, spellLevel, out remaining, out max);
            }

            // The column owns the payment choice. A paid spell never borrows a free
            // exemption from another column when deciding whether its button is enabled.
            if (remaining > 0 && !SpellSlotCastingLimit2024Context.CanUseSpellSlotLevel(
                    caster, repertoire, null, spellLevel))
            {
                remaining = 0;
                spellActivationBox.hasUpcast = false;
            }
        }

        private static int GetLowestAvailableSlotLevel(
            RulesetSpellRepertoire repertoire, RulesetCharacter caster, SpellActivationBox box)
        {
            var spell = box.GuiSpellDefinition?.SpellDefinition;
            if (spell == null || spell.SpellLevel == 0)
            {
                return repertoire.GetLowestAvailableSlotLevel();
            }

            var option = FreeBindings.TryGetValue(box, out var free)
                ? free
                : SpellSelectionContext.GetBaseSelection(caster, repertoire, spell);
            return option.IsAvailable(caster) ? option.SlotLevel : 0;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var uniqueLevelSlotsMethod = typeof(FeatureDefinitionCastSpell).GetMethod("get_UniqueLevelSlots");
            var myUniqueLevelSlotsMethod =
                new Func<FeatureDefinitionCastSpell, RulesetCharacterHero, bool>(UniqueLevelSlots).Method;

            var getLowestSlotMethod = AccessTools.Method(
                typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.GetLowestAvailableSlotLevel));
            var getOwnSlotMethod = new Func<RulesetSpellRepertoire, RulesetCharacter, SpellActivationBox, int>(
                GetLowestAvailableSlotLevel).Method;
            var getSlotsNumberMethod = typeof(RulesetSpellRepertoire).GetMethod("GetSlotsNumber");
            var myGetSlotsNumberMethod = typeof(BindSpell_Patch).GetMethod("MyGetSlotsNumber");
            var getActivationTimeMethod = typeof(SpellDefinition).GetMethod("get_ActivationTime");
            var getDisplayedActivationTimeMethod =
                new Func<SpellDefinition, SpellActivationBox, RuleDefinitions.ActivationTime>(
                    SpellActionTypeContext.GetDisplayedActivationTime).Method;

            return instructions
                .ReplaceCalls(getActivationTimeMethod, 2, "SpellActivationBox.BindSpell.ActivationTime",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, getDisplayedActivationTimeMethod))
                .ReplaceCalls(getLowestSlotMethod, "SpellActivationBox.BindSpell.GetLowestAvailableSlotLevel",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, getOwnSlotMethod))
                .ReplaceCalls(getSlotsNumberMethod, "SpellActivationBox.BindSpell.GetSlotsNumber",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, myGetSlotsNumberMethod))
                .ReplaceCalls(uniqueLevelSlotsMethod, "SpellActivationBox.BindSpell.UniqueLevelSlots",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, myUniqueLevelSlotsMethod));
        }

    }

    //PATCH: register on acting character if SHIFT is pressed on spell box activation
    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.OnActivateCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnActivateCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellActivationBox __instance)
        {
            if (__instance.spellRepertoire == null)
            {
                return true;
            }

            var rulesetCaster = __instance.tooltip.Context as RulesetCharacter
                                ?? __instance.spellRepertoire.GetCaster();
            var caster = GameLocationCharacter.GetFromActor(rulesetCaster);

            caster?.RegisterShiftState();

            return true;
        }
    }

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SpellActivationBox __instance)
        {
            FreeBindings.Remove(__instance);
        }
    }
}
