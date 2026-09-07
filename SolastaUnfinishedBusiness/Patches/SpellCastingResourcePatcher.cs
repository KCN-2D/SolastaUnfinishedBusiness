using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellCastingResourcePatcher
{
    [HarmonyPatch(typeof(RulesetCharacter), "EnumerateUsableSpells")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumerateUsableSpells_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetCharacter __instance)
        {
            SpellCastingResourceContext.AddFreeWizardUsableSpells(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterActionPanel), nameof(CharacterActionPanel.SpellcastEngaged))]
    [UsedImplicitly]
    internal static class SpellcastEngaged_Patch
    {
        [UsedImplicitly]
        private static void Prefix(ref RulesetSpellRepertoire __0, SpellDefinition __1, ref int __2)
        {
            var option = SpellCastingResourceContext.CurrentSelection;
            if (option != null && SpellCastingResourceContext.IsSameSpell(option.Spell, __1))
            {
                __0 = option.Repertoire;
                __2 = option.SlotLevel;
            }
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // Bind before native concentration confirmation or immediate self-target execution.
            return instructions.ReplaceCalls(
                AccessTools.PropertySetter(typeof(CharacterActionParams), nameof(CharacterActionParams.RulesetEffect)),
                1, "CharacterActionPanel.SpellcastEngaged.CastingSelection",
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(SpellCastingResourcePatcher), nameof(SetSelectedEffect))));
        }
    }

    internal static void SetSelectedEffect(CharacterActionParams parameters, RulesetEffect effect)
    {
        parameters.RulesetEffect = effect;
        if (effect is not RulesetEffectSpell spell || !SpellCastingResourceContext.SupportsSelection(spell))
        {
            return;
        }

        var selected = SpellCastingResourceContext.CurrentSelection;
        if (selected != null && SpellCastingResourceContext.IsSameSpell(selected.Spell, spell.SpellDefinition))
        {
            SpellCastingResourceContext.ApplySelection(parameters, selected);
        }
        else
        {
            // A cancelled cast may leave the panel's reusable parameters carrying an old choice.
            SpellCastingResourceContext.ClearSelectionMarker(parameters);
        }
    }

    [HarmonyPatch(typeof(SubspellSelectionModal), nameof(SubspellSelectionModal.Bind),
        [typeof(SpellDefinition), typeof(RulesetCharacter), typeof(RulesetSpellRepertoire),
            typeof(SpellsByLevelBox.SpellCastEngagedHandler), typeof(int), typeof(RectTransform)])]
    [UsedImplicitly]
    internal static class SubspellBind_Patch
    {
        [HarmonyPriority(Priority.First)]
        [UsedImplicitly]
        private static void Prefix(SpellDefinition masterSpell,
            ref RulesetSpellRepertoire __2,
            ref SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
            ref int __4)
        {
            var selected = SpellCastingResourceContext.CurrentSelection;
            if (selected == null || !SpellCastingResourceContext.IsSameSpell(selected.Spell, masterSpell))
            {
                return;
            }

            // The child picker runs after the original click has returned. Capture the choice in
            // its callback rather than retaining global UI state across frames or cancellation.
            __2 = selected.Repertoire;
            __4 = selected.SlotLevel;
            var callback = spellCastEngaged;
            spellCastEngaged = (repertoire, spell, level) =>
            {
                using var selection = SpellCastingResourceContext.BeginSelection(selected);
                callback(repertoire, spell, level);
            };
        }
    }

    [HarmonyPatch(typeof(CharacterActionCastSpell), MethodType.Constructor, [typeof(CharacterActionParams)])]
    [UsedImplicitly]
    internal static class CastSpellConstructor_Patch
    {
        [UsedImplicitly]
        private static void Postfix(CharacterActionParams __0)
        {
            // Reconstruct explicit payment after the native action/effect network deserialization.
            SpellCastingResourceContext.RestoreSelection(__0);
        }
    }
}
