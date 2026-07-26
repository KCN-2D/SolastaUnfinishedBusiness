namespace SolastaUnfinishedBusiness.Interfaces;

internal interface ISubclassLevelProvider
{
    int GetSubclassLevel(
        RulesetCharacter character,
        CharacterClassDefinition classDefinition,
        string subclassName);
}
