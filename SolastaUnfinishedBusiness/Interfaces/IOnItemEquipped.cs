namespace SolastaUnfinishedBusiness.Interfaces;

public interface IOnItemEquipped
{
    public void OnItemEquipped(RulesetCharacterHero hero);
}

// Runtime equipment behavior must also work for humanoid characters that are not
// RulesetCharacterHero. Keep IOnItemEquipped unchanged for external compatibility.
internal interface IOnCharacterEquipmentChanged
{
    void OnCharacterEquipmentChanged(RulesetCharacter character);
}
