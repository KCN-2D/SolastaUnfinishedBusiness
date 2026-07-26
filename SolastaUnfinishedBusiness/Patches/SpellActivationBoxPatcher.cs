using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellActivationBoxPatcher
{
    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.BindSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindSpell_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            SpellActivationBox __instance,
            RulesetSpellRepertoire __1,
            out IDisposable __state)
        {
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
            if (!__instance.globalValid ||
                SpellCastingValidation.IsValid(
                    __0,
                    __1,
                    __2,
                    null,
                    out _))
            {
                return;
            }

            __instance.globalValid = false;
            __instance.canvasGroup.interactable = false;
            __instance.image.color = Color.grey;
            __instance.image.material = __instance.unavailableMaterial;
            SimulacrumDiagnostics.RecordSpellActivation(
                "box-validation",
                __0,
                __1,
                __2,
                false,
                false);
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

            return featureDefinitionCastSpell.UniqueLevelSlots && !SharedSpellsContext.IsMulticaster(caster);
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
            var spellDefinition = spellActivationBox.GuiSpellDefinition?.SpellDefinition;
            var hasFreeWizardCast = Level20Context.HasFreeWizardCast(caster, repertoire, spellDefinition, spellLevel);

            if (caster.IsSpellPointsEnabled())
            {
                var canCastSpell = hasFreeWizardCast ||
                                   SpellPointsContext.CanCastSpellOfLevel(caster, repertoire, spellLevel);

                max = 1; // irrelevant
                remaining = canCastSpell ? 1 : 0;

                if (!canCastSpell)
                {
                    spellActivationBox.hasUpcast = false;
                }
            }
            else
            {
                var slotOwner = caster.GetOriginalHero() ?? caster;

                repertoire.GetDisplaySlotNumbers(slotOwner, spellLevel, out remaining, out max);

                if (remaining == 0 && hasFreeWizardCast)
                {
                    remaining = 1;
                    max = max == 0 ? 1 : max;
                }
            }

            SimulacrumDiagnostics.RecordSpellSlots(
                caster,
                "box-bind",
                repertoire,
                spellLevel,
                hasFreeWizardCast,
                spellDefinition,
                remaining,
                max);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var uniqueLevelSlotsMethod = typeof(FeatureDefinitionCastSpell).GetMethod("get_UniqueLevelSlots");
            var myUniqueLevelSlotsMethod =
                new Func<FeatureDefinitionCastSpell, RulesetCharacterHero, bool>(UniqueLevelSlots).Method;

            var getSlotsNumberMethod = typeof(RulesetSpellRepertoire).GetMethod("GetSlotsNumber");
            var myGetSlotsNumberMethod = typeof(BindSpell_Patch).GetMethod("MyGetSlotsNumber");

            return instructions
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
        public static void Prefix(SpellActivationBox __instance)
        {
            if (__instance.spellRepertoire == null)
            {
                return;
            }

            var rulesetCaster = __instance.tooltip.Context as RulesetCharacter
                                ?? __instance.spellRepertoire.GetCaster();
            var caster = GameLocationCharacter.GetFromActor(rulesetCaster);

            SimulacrumDiagnostics.RecordSpellActivation(
                "box-click",
                rulesetCaster,
                __instance.spellRepertoire,
                __instance.GuiSpellDefinition?.SpellDefinition,
                __instance.button?.interactable,
                __instance.globalValid);

            caster?.RegisterShiftState();
        }
    }
}
