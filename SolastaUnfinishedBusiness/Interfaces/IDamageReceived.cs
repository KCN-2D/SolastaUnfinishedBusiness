using System.Collections;

namespace SolastaUnfinishedBusiness.Interfaces;

// Damage after affinity and difficulty adjustments, before temporary hit points absorb it.
internal interface IDamageReceived
{
    void OnDamageReceived(RulesetCharacter character, int damage, string damageType, ulong sourceGuid);
}

// Optional reactions run after the native damage application has completed.
internal interface IReactToDamageReceived
{
    IEnumerator ReactToDamageReceived(RulesetCharacter character, int damage, string damageType, ulong sourceGuid,
        int hitPointsBefore, int hitPointsAfter);
}

// Only an actual increase in current HP queues these optional reactions.
internal interface IReactToHealingReceived
{
    IEnumerator ReactToHealingReceived(RulesetCharacter character, int healing, ulong sourceGuid,
        RuleDefinitions.HealingCap cap, IHealingModificationProvider provider,
        GameLocationBattle battle);
}
