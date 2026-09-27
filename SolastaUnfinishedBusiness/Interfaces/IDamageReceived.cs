namespace SolastaUnfinishedBusiness.Interfaces;

// Damage after affinity and difficulty adjustments, before temporary hit points absorb it.
internal interface IDamageReceived
{
    void OnDamageReceived(RulesetCharacter character, int damage, string damageType, ulong sourceGuid);
}
