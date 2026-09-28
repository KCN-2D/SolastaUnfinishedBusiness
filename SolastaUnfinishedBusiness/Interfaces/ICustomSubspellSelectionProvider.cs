using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface ICustomSubspellSelectionProvider
{
    bool BypassComponentsAndCastingTime { get; }

    bool BypassMaterialComponent { get; }

    ICustomSubspellSelectionSession CreateSession(
        SpellDefinition masterSpell,
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        int slotLevel);
}

internal interface ICustomSubspellSelectionAvailability
{
    bool BypassComponentsAndCastingTime { get; }

    bool IsAvailable(SpellDefinition spell, out string failure);
}

internal interface ICustomSubspellSelectionSession
{
    List<SpellDefinition> GetSubspells();

    bool OnActivate(SubspellSelectionModal modal, int index);
}
