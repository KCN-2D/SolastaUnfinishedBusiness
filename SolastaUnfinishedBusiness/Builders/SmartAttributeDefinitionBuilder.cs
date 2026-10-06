using System;

namespace SolastaUnfinishedBusiness.Builders;

internal sealed class SmartAttributeDefinitionBuilder
    : DefinitionBuilder<SmartAttributeDefinition, SmartAttributeDefinitionBuilder>
{
    private SmartAttributeDefinitionBuilder(string name, Guid namespaceGuid) : base(name, namespaceGuid)
    {
    }

    private SmartAttributeDefinitionBuilder(SmartAttributeDefinition original, string name, Guid namespaceGuid)
        : base(original, name, namespaceGuid)
    {
    }
}
