using System.Collections.Generic;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Interfaces;

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
