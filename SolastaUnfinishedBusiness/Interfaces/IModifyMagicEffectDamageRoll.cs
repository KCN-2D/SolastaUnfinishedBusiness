using System.Collections.Generic;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Interfaces;

public interface IModifyMagicEffectDamageDice
{
    [UsedImplicitly]
    void ModifyDamageDice(
        RulesetCharacter rulesetCharacter,
        DamageForm damageForm,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        List<int> rolledValues);
}

public interface IOnMagicEffectDamageApplied
{
    [UsedImplicitly]
    void OnDamageApplied(
        RulesetCharacter rulesetCharacter,
        DamageForm damageForm,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        int damageReceived);
}

public interface IModifyMagicEffectDamageRoll
{
    [UsedImplicitly]
    public void ModifyDamageRoll(
        RulesetCharacter rulesetCharacter,
        DamageForm damageForm,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        bool criticalSuccess,
        int criticalHitMode,
        bool maximumDamage,
        float damageMultiplier,
        IReadOnlyList<int> actualRolledValues,
        List<int> rolledValues,
        bool canRerollDice,
        ref int damage);
}
