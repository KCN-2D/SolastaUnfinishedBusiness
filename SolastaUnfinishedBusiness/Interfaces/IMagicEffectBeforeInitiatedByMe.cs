using System.Collections;
using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IMagicEffectBeforeInitiatedByMe
{
    IEnumerator OnMagicEffectBeforeInitiatedByMe(
        CharacterAction action,
        RulesetEffect activeEffect,
        GameLocationCharacter attacker,
        List<GameLocationCharacter> targets);
}
