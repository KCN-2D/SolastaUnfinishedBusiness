namespace SolastaUnfinishedBusiness.Interfaces;

internal interface ICustomSummonInvocationContext
{
}

internal interface ICustomSummonCharacterConstructionHandler
{
    void InitializeConstructionAttributes(
        RulesetCharacterMonster summonedCharacter,
        ICustomSummonInvocationContext invocationContext);
}

internal interface ICustomSummonFormHandler
{
    bool TryPrepare(
        EffectForm effectForm,
        ref RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        out ICustomSummonInvocationContext invocationContext,
        out string failureFeedback);

    string GetMonsterDefinitionName(
        EffectForm effectForm,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        ICustomSummonInvocationContext invocationContext);

    void InitializeSummonedCharacter(
        RulesetCharacterMonster summonedCharacter,
        ICustomSummonInvocationContext invocationContext);

    void AfterApply(
        EffectForm effectForm,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        ICustomSummonInvocationContext invocationContext);
}
