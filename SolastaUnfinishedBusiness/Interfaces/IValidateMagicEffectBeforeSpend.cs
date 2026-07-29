using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IValidateMagicEffectBeforeSpend
{
    bool IsValid(
        CharacterActionMagicEffect action,
        GameLocationCharacter actingCharacter,
        IReadOnlyList<GameLocationCharacter> targets,
        out string failure);
}
