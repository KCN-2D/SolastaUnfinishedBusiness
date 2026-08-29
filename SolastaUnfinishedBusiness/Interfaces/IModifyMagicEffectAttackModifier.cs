namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IModifyMagicEffectAttackModifier
{
    void ModifyMagicEffectAttackModifier(
        RulesetCharacter attacker,
        RulesetActor defender,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect,
        ActionModifier actionModifier);
}
