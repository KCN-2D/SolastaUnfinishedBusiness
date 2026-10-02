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
    private static readonly ConditionalWeakTable<SpellActivationBox, SpellCastingResourceContext.ResourceOption> ResourceBindings = new();

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.BindSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindSpell_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            SpellActivationBox __instance,
            RulesetCharacter __0,
            ref RulesetSpellRepertoire __1,
            SpellDefinition __2,
            out IDisposable __state)
        {
            ResourceBindings.Remove(__instance);
            var source = SpellCastingResourceContext.ResolveCastingRepertoire(__1, __2, __0);
            if (SpellSelectionContext.TryGetOption(__1, out var option))
            {
                ResourceBindings.Add(__instance, option);
                if (option.IsFree)
                {
                    __1 = option.CastingRepertoire;
                }
            }
            else if (__1 != null && source != __1)
            {
                // The native class column owns the slots; the feat remains the spell's casting source.
                ResourceBindings.Add(__instance, SpellSelectionContext.GetBaseSelection(__0, __1, __2));
            }

            SpellCastingValidation.BindTooltipRepertoire(__instance.tooltip, source);
            __state = SpellCastingValidation.EnterSelectedRepertoire(source);
        }

        [UsedImplicitly]
        public static void Postfix(
            SpellActivationBox __instance,
            RulesetCharacter __0,
            RulesetSpellRepertoire __1,
            SpellDefinition __2)
        {
            if (ResourceBindings.TryGetValue(__instance, out var option) && option.IsFree)
            {
                __instance.hasUpcast = false;
                __instance.upcastButton.gameObject.SetActive(false);
                __instance.closeAdvancementButton.gameObject.SetActive(false);
            }

            var line = SpellActionTypeContext.GetRepertoireLine(__instance);
            var hasAvailableAction = Gui.Battle == null || line?.actionType != ActionDefinitions.ActionType.None ||
                                     SpellActionTypeContext.TryGetAvailableSpellAction(
                                         GameLocationCharacter.GetFromActor(__0), __1, __2,
                                         ActionDefinitions.ActionScope.Battle, out _);

            if (hasAvailableAction && (!__instance.globalValid ||
                SpellCastingValidation.IsValid(__0,
                    SpellCastingResourceContext.ResolveCastingRepertoire(__1, __2, __0), __2, null, out _)))
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
            if (ResourceBindings.TryGetValue(spellActivationBox, out var option))
            {
                SpellSelectionContext.GetViewSlots(option, spellLevel, out remaining, out max);
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

            var option = ResourceBindings.TryGetValue(box, out var free)
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
            ResourceBindings.Remove(__instance);
            SpellCastingValidation.BindTooltipRepertoire(__instance.tooltip, null);
        }
    }
}
