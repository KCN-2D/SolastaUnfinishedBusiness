using System;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Builders;

internal delegate bool IsInvocationValidHandler(RulesetCharacter character, InvocationDefinition invocation);

[UsedImplicitly]
internal class CustomInvocationDefinitionBuilder
    : InvocationDefinitionBuilder<InvocationDefinitionCustom, CustomInvocationDefinitionBuilder>
{
    internal CustomInvocationDefinitionBuilder(string name, Guid namespaceGuid) : base(name, namespaceGuid)
    {
    }

    internal CustomInvocationDefinitionBuilder(InvocationDefinitionCustom original, string name,
        Guid namespaceGuid)
        : base(original, name, namespaceGuid)
    {
    }

    internal CustomInvocationDefinitionBuilder SetPoolType(InvocationPoolTypeCustom poolType)
    {
        Definition.PoolType = poolType;
        return this;
    }

    internal CustomInvocationDefinitionBuilder AddValidators(params IValidateDefinitionPreRequisites.Validate[] validators)
    {
        Definition.CustomValidators.AddRange(validators);
        return this;
    }
}
