using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface ICustomSubspellSelectionProvider
{
    ICustomSubspellSelectionSession CreateSession(
        SpellDefinition masterSpell,
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        int slotLevel);
}

internal interface ICustomSubspellSelectionSession
{
    List<SpellDefinition> GetSubspells();

    bool OnActivate(SubspellSelectionModal modal, int index);
}
