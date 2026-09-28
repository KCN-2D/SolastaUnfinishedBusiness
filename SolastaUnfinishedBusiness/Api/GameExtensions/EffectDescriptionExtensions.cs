using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class EffectDescriptionExtensions
{
    // AddRandom uses ConditionsList, including null entries for a possible no-effect outcome.
    // Removal operations do not describe conditions that the effect can apply.
    public static IEnumerable<ConditionDefinition> GetAppliedConditionDefinitions(
        this IEnumerable<EffectForm> effectForms)
    {
        if (effectForms == null)
        {
            yield break;
        }

        foreach (var effectForm in effectForms)
        {
            if (effectForm?.FormType != EffectForm.EffectFormType.Condition ||
                effectForm.ConditionForm is not { } conditionForm)
            {
                continue;
            }

            switch (conditionForm.Operation)
            {
                case ConditionForm.ConditionOperation.Add when conditionForm.ConditionDefinition != null:
                    yield return conditionForm.ConditionDefinition;
                    break;
                case ConditionForm.ConditionOperation.AddRandom:
                    foreach (var condition in conditionForm.ConditionsList)
                    {
                        if (condition != null)
                        {
                            yield return condition;
                        }
                    }
                    break;
            }
        }
    }

    public static EffectForm WithSavingThrow(this EffectForm effect, EffectSavingThrowType savingThrowAffinity,
        TurnOccurenceType saveOccurence = TurnOccurenceType.EndOfTurn, bool canSaveToCancel = false)
    {
        effect.HasSavingThrow = true;
        effect.SavingThrowAffinity = savingThrowAffinity;
        effect.SaveOccurence = saveOccurence;
        effect.CanSaveToCancel = canSaveToCancel;
        return effect;
    }

    public static DamageForm FindFirstNonNegatedDamageFormOfType(this EffectDescription effect, bool canForceHalfDamage,
        List<string> types)
    {
        return effect?.effectForms
            .Where(x => x.FormType == EffectForm.EffectFormType.Damage &&
                        (x.SavingThrowAffinity != EffectSavingThrowType.Negates) | canForceHalfDamage &&
                        (types == null || types.Count == 0 || types.Contains(x.damageForm.damageType)))
            .Select(effectForm => effectForm.damageForm)
            .FirstOrDefault();
    }

    public static DamageForm FindFirstDamageFormOfType(this EffectDescription effect, List<string> types)
    {
        return effect?.effectForms
            .Where(x =>
                x.FormType == EffectForm.EffectFormType.Damage &&
                (types == null || types.Count == 0 || types.Contains(x.damageForm.damageType)))
            .Select(effectForm => effectForm.damageForm)
            .FirstOrDefault();
    }

    public static bool HasNotNegatedDamageForm(this EffectDescription effect, SavingThrowData savingThrowData,
        bool canForceHalfDamage, bool hasSpecialHalfDamage)
    {
        return effect.effectForms
            .Any(x => (x.FormType == EffectForm.EffectFormType.Damage
                       && savingThrowData.SaveOutcome is not RollOutcome.CriticalSuccess and not RollOutcome.Success) ||
                      x.SavingThrowAffinity switch
                      {
                          EffectSavingThrowType.Negates => canForceHalfDamage,
                          EffectSavingThrowType.HalfDamage => canForceHalfDamage || !hasSpecialHalfDamage,
                          _ => true
                      }
            );
    }
}
