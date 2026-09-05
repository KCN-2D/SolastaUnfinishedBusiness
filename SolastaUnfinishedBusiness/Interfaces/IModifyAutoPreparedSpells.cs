using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IModifyAutoPreparedSpells
{
    FeatureDefinition SourceFeature { get; }

    IEnumerable<SpellDefinition> GetAutoPreparedSpells(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire);
}
