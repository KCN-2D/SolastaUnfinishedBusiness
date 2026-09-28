namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IModifyAttackAfterMagicEffect
{
    void ModifyAttack(RulesetEffect effect, RulesetCharacter caster, RulesetAttackMode attackMode);
}
