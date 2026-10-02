using System;

namespace SolastaUnfinishedBusiness.Builders;

internal sealed class MonsterPresentationDefinitionBuilder
    : DefinitionBuilder<MonsterPresentationDefinition, MonsterPresentationDefinitionBuilder>
{
    internal MonsterPresentationDefinitionBuilder SetModelScale(float scale)
    {
        Definition.modelScale = scale;
        return this;
    }

    private MonsterPresentationDefinitionBuilder(string name, Guid namespaceGuid)
        : base(name, namespaceGuid)
    {
    }

    private MonsterPresentationDefinitionBuilder(
        MonsterPresentationDefinition original,
        string name,
        Guid namespaceGuid)
        : base(original, name, namespaceGuid)
    {
    }
}
