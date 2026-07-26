using System;

namespace SolastaUnfinishedBusiness.Builders;

internal sealed class HumanoidMonsterPresentationDefinitionBuilder
    : DefinitionBuilder<HumanoidMonsterPresentationDefinition, HumanoidMonsterPresentationDefinitionBuilder>
{
    internal HumanoidMonsterPresentationDefinitionBuilder SetCharacterAppearance(
        CharacterRaceDefinition raceDefinition,
        CharacterRaceDefinition subRaceDefinition,
        RuleDefinitions.CreatureSex sex,
        string defaultArmorDefinition)
    {
        Definition.raceDefinition = raceDefinition;
        Definition.subRaceDefinition = subRaceDefinition;
        Definition.sex = sex;
        Definition.armorDefinition = defaultArmorDefinition;
        Definition.helmetDefinition = string.Empty;
        Definition.tabardDefinition = string.Empty;
        Definition.overrideWieldedItems = false;
        Definition.itemDefinitionMainHand = null;
        Definition.itemDefinitionOffHand = null;

        return this;
    }

    private HumanoidMonsterPresentationDefinitionBuilder(string name, Guid namespaceGuid)
        : base(name, namespaceGuid)
    {
    }

    private HumanoidMonsterPresentationDefinitionBuilder(
        HumanoidMonsterPresentationDefinition original,
        string name,
        Guid namespaceGuid)
        : base(original, name, namespaceGuid)
    {
    }
}
