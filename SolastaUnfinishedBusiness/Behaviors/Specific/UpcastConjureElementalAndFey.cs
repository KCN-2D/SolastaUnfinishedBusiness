using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal static class UpcastConjureElementalAndFey
{
    private static readonly ICustomSubspellSelectionProvider Provider = new SelectionProvider();

    internal static ICustomSubspellSelectionProvider TryGetProvider(SpellDefinition masterSpell)
    {
        if (!Main.Settings.EnableUpcastConjureElementalAndFey)
        {
            return null;
        }

        return masterSpell.Name == DatabaseHelper.SpellDefinitions.ConjureElemental.Name ||
               masterSpell.Name == DatabaseHelper.SpellDefinitions.ConjureFey.Name
            ? Provider
            : null;
    }

    internal static List<SpellDefinition> GetSubspells(SpellDefinition masterSpell, int slotLevel)
    {
        var subspellsList = masterSpell.SubspellsList;

        if (subspellsList is not { Count: > 0 })
        {
            return subspellsList;
        }

        var subspellsGroupedAndFilteredByCr = subspellsList
            .Select(spell =>
                new
                {
                    SpellDefinition = spell,
                    spell.EffectDescription
                        .GetFirstFormOfType(EffectForm.EffectFormType.Summon)
                        .SummonForm
                        .MonsterDefinitionName
                })
            .Select(spell => new
            {
                spell.SpellDefinition,
                ChallengeRating =
                    DatabaseHelper.TryGetDefinition<MonsterDefinition>(
                        spell.MonsterDefinitionName,
                        out var monsterDefinition)
                        ? monsterDefinition.ChallengeRating
                        : int.MaxValue
            })
            .GroupBy(spell => spell.ChallengeRating)
            .Select(group => new
            {
                ChallengeRating = group.Key,
                SpellDefinitions = group
                    .Select(spell => spell.SpellDefinition)
                    .OrderBy(spell => spell.Name, System.StringComparer.Ordinal)
            })
            .Where(group => group.ChallengeRating <= slotLevel)
            .OrderByDescending(group => group.ChallengeRating);

        var allOrMostPowerful = Main.Settings.OnlyShowMostPowerfulUpcastConjuredElementalOrFey
            ? subspellsGroupedAndFilteredByCr.Take(1)
            : subspellsGroupedAndFilteredByCr;

        return allOrMostPowerful.SelectMany(group => group.SpellDefinitions).ToList();
    }

    private sealed class SelectionProvider : ICustomSubspellSelectionProvider
    {
        public ICustomSubspellSelectionSession CreateSession(
            SpellDefinition masterSpell,
            RulesetCharacter caster,
            RulesetSpellRepertoire repertoire,
            int slotLevel)
        {
            return new SelectionSession(GetSubspells(masterSpell, slotLevel));
        }
    }

    private sealed class SelectionSession(List<SpellDefinition> subspells) : ICustomSubspellSelectionSession
    {
        public List<SpellDefinition> GetSubspells()
        {
            return subspells;
        }

        public bool OnActivate(SubspellSelectionModal modal, int index)
        {
            if (index < 0 || index >= subspells.Count)
            {
                return true;
            }

            modal.spellCastEngaged?.Invoke(modal.spellRepertoire, subspells[index], modal.slotLevel);
            modal.Hide();

            return false;
        }
    }
}
