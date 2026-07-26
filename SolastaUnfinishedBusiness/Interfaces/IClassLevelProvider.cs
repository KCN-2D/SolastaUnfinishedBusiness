namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IClassLevelProvider
{
    int GetClassLevel(RulesetCharacter character, CharacterClassDefinition classDefinition);
}
