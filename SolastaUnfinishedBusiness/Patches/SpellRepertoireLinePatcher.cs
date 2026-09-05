using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellRepertoireLinePatcher
{
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
                UiTextHelpers.FitSideLabel(__instance.headerLabel);
            }
        }
    }

    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.FindAndSortRelevantSpells))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FindAndSortRelevantSpells_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] List<SpellDefinition> spellDefinitions)
        {
            //PATCH: hide reaction spells from spell panel
            spellDefinitions.RemoveAll(x => x.ActivationTime == ActivationTime.Reaction);
            //PATCH: hide smite spells from spell panel
            spellDefinitions.RemoveAll(x => x.ActivationTime == ActivationTime.OnAttackHit);
        }

        [UsedImplicitly]
        public static void Postfix([NotNull] List<SpellDefinition> spellDefinitions, SpellRepertoireLine __instance)
        {
            SpellActionTypeContext.QualifySpells(
                __instance.caster?.RulesetCharacter,
                __instance.spellRepertoire,
                __instance.actionType,
                spellDefinitions,
                __instance.relevantSpells);
            __instance.relevantSpells.Sort(__instance);
        }
    }
}
