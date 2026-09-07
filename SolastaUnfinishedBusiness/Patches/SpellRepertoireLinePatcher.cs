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
                SpellSelectionContext.Resolve(__instance.spellRepertoire),
                __instance.actionType,
                spellDefinitions,
                __instance.relevantSpells);
            // Filter the display result; callers can pass the character's actual KnownCantrips list.
            __instance.relevantSpells.RemoveAll(spell =>
                spell.ActivationTime is ActivationTime.Reaction or ActivationTime.OnAttackHit);
            __instance.relevantSpells.Sort(__instance);
        }
    }
}
