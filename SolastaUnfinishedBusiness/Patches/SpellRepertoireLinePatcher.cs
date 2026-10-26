using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellRepertoireLinePatcher
{
    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SpellRepertoireLine __instance, out IDisposable __state)
        {
            __state = CampaignsContext.BeginMultilineSpellSelectionLineRefresh(__instance);
        }

        [UsedImplicitly]
        public static void Finalizer(IDisposable __state) => __state?.Dispose();
    }

    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SpellRepertoireLine __instance)
        {
            CampaignsContext.RestoreMultilineSpellSelectionLine(__instance);
        }
    }

    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SpellRepertoireLine __instance)
        {
            if (__instance.showHeader)
            {
                if (SpellSelectionContext.TryGetOption(__instance.spellRepertoire, out var option))
                {
                    __instance.headerLabel.Text = option.SourceTitle;
                }

                UiTextHelpers.FitSideLabel(__instance.headerLabel);
            }

            CampaignsContext.RebindMultilineSpellSelectionLine(__instance);
        }
    }

    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.FindAndSortRelevantSpells))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FindAndSortRelevantSpells_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(List<SpellDefinition> spellDefinitions, SpellRepertoireLine __instance,
            out bool __state)
        {
            foreach (var spell in spellDefinitions)
            {
                if (spell.SpellLevel >= __instance.minSpellLevel && spell.SpellLevel <= __instance.maxSpellLevel)
                {
                    __state = true;
                    return true;
                }
            }

            // Native Bind prepares every level before applying this line's display range.
            // Empty off-range results also avoid allocating level boxes that stay hidden.
            __instance.relevantSpells.Clear();
            __state = false;
            return false;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(
                typeof(List<SpellDefinition>).GetMethod(nameof(List<SpellDefinition>.Sort),
                    [typeof(IComparer<SpellDefinition>)]),
                1, "SpellRepertoireLine.FindAndSortRelevantSpells.Sort",
                new CodeInstruction(OpCodes.Call,
                    new Action<List<SpellDefinition>, IComparer<SpellDefinition>>(UiTextHelpers.SortSpellDefinitions).Method));

        [UsedImplicitly]
        public static void Postfix([NotNull] List<SpellDefinition> spellDefinitions, SpellRepertoireLine __instance,
            bool __state)
        {
            if (!__state)
            {
                return;
            }

            var added = SpellActionTypeContext.QualifySpells(
                __instance.caster?.RulesetCharacter,
                __instance.spellRepertoire,
                __instance.actionType,
                spellDefinitions,
                __instance.relevantSpells);
            // Filter the display result; callers can pass the character's actual KnownCantrips list.
            __instance.relevantSpells.RemoveAll(spell =>
                spell.ActivationTime is ActivationTime.Reaction or ActivationTime.OnAttackHit);
            ActionPanelContext.FilterFamiliarTouchSpells(__instance);
            // Removing entries preserves the native order. Only newly qualified spells need sorting.
            if (added)
            {
                UiTextHelpers.SortSpellDefinitions(__instance.relevantSpells, __instance);
            }
        }
    }

    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.Compare))]
    [UsedImplicitly]
    private static class Compare_Patch
    {
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(typeof(Gui).GetMethod(nameof(Gui.Localize),
                    [typeof(string), typeof(bool), typeof(GameObject), typeof(string)]),
                2, "SpellRepertoireLine.Compare.Title",
                new CodeInstruction(OpCodes.Call,
                    new Func<string, bool, GameObject, string, string>(UiTextHelpers.LocalizeSpellSortTitle).Method));
    }
}
