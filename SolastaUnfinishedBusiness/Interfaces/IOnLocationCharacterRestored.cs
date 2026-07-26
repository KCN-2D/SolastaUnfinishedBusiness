namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IOnLocationCharacterRestored
{
    int Priority { get; }

    void OnLocationCharacterRestored(RulesetCharacter character);
}
