using System.Collections;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IOnReducedToZeroHp
{
    IEnumerator HandleReducedToZeroHp(
        GameLocationCharacter attacker,
        GameLocationCharacter downedCreature,
        RulesetAttackMode attackMode,
        RulesetEffect activeEffect);
}
