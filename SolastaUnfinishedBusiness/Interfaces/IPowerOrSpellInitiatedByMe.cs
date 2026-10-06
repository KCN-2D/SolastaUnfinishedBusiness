using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Interfaces;

public interface IPowerOrSpellInitiatedByMe
{
    [UsedImplicitly]
    IEnumerator OnPowerOrSpellInitiatedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition);
}

// Produces the affected creatures without committing conditions, resources, or animations.
public interface IPowerOrSpellTargetProvider
{
    List<GameLocationCharacter> GetTargetCharacters(CharacterActionMagicEffect action);
}
