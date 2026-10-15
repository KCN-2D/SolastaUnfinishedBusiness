namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IModifyMagicEffectAttackModifier
{
    // Previews share the roll's effect provenance, but must not record or consume an executed attack.
    void ModifyMagicEffectAttackModifier(
        RulesetCharacter attacker,
        RulesetActor defender,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect,
        ActionModifier actionModifier,
        bool isPreview);
}
