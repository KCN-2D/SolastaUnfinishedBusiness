using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IRulesetCharacterMonsterFactory
{
    RulesetCharacterMonster Create(
        MonsterDefinition monsterDefinition,
        int experience,
        SpawnOverrides spawnOverrides,
        GadgetDefinitions.CreatureSex sex,
        RulesetCharacter originalFormCharacter,
        bool keepMentalAbilityScores,
        bool useMentalAbilityScores,
        bool useOriginalFormConstitution);
}
