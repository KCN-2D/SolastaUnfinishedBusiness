using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
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
        public static void Postfix([NotNull] List<SpellDefinition> spellDefinitions, SpellRepertoireLine __instance)
        {
            SpellActionTypeContext.QualifySpells(
                __instance.caster?.RulesetCharacter,
                __instance.spellRepertoire,
                __instance.actionType,
                spellDefinitions,
                __instance.relevantSpells);
            // Filter the display result; callers can pass the character's actual KnownCantrips list.
            __instance.relevantSpells.RemoveAll(spell =>
                spell.ActivationTime is ActivationTime.Reaction or ActivationTime.OnAttackHit);
            ActionPanelContext.FilterFamiliarTouchSpells(__instance);
            __instance.relevantSpells.Sort(__instance);
        }
    }

}
