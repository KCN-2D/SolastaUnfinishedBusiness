namespace SolastaUnfinishedBusiness.Interfaces;

/**
 * Can influence results of calls to `RulesetImplementationManager.IsValidContextForRestrictedContextProvider`
 * Native callers may skip context validation when RestrictedProperty is `None`.
 * The additional-damage handler explicitly validates every restricted-context provider.
 * Note: for now supports only single modification per feature - only first `IValidateContextInsteadOfRestrictedProperty` would apply
 */
public enum OperationType
{
    Ignore,
    Set,
    Or,
    And
}

// can validate FeatureDefinitionAdditionalDamage, FeatureDefinitionAttackModifier, FeatureDefinitionAttributeModifier, FeatureDefinitionCombatAffinity, FeatureDefinitionPower
public interface IValidateContextInsteadOfRestrictedProperty
{
    public (OperationType, bool) ValidateContext(
        BaseDefinition definition,
        IRestrictedContextProvider provider,
        RulesetCharacter character,
        ItemDefinition itemDefinition,
        bool rangedAttack,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect);
}
