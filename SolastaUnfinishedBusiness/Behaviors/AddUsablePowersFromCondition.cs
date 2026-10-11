using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Behaviors;

/**
 * Adds all powers from condition definition's feature list to character's usable powers when condition is applied
 * and removes them when condition is removed.
 */
public class AddUsablePowersFromCondition : IOnConditionAddedOrRemoved
{
    private static readonly FieldInfo UsablePowersField = AccessTools.Field(typeof(RulesetCharacter), "usablePowers");
    internal static AddUsablePowersFromCondition Marker { get; } = new();

    // Monster.UsablePowers exposes the legendary collection while resolving a
    // legendary action. A condition grants ordinary powers even at that time.
    internal static List<RulesetUsablePower> GetGrantedUsablePowers(RulesetCharacter target) =>
        (List<RulesetUsablePower>)UsablePowersField.GetValue(target);

    public void OnConditionAdded(RulesetCharacter target, RulesetCondition rulesetCondition)
    {
        //assuming mod won't have any case where same power is added twice to a condition
        var grantedPowers = GetGrantedUsablePowers(target);
        var powers = grantedPowers
            .Select(x => x.PowerDefinition)
            .ToArray(); // avoid changing enumerator

        foreach (var power in rulesetCondition.ConditionDefinition.Features
                     .OfType<FeatureDefinitionPower>()
                     .Where(x => !powers.Contains(x)))
        {
            grantedPowers.Add(PowerProvider.Get(power, target));
        }
    }

    public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition)
    {
        var powers = rulesetCondition.ConditionDefinition.Features
            .OfType<FeatureDefinitionPower>();

        var grantedPowers = GetGrantedUsablePowers(target);
        foreach (var usablePower in grantedPowers
                     .Where(x => powers.Contains(x.PowerDefinition))
                     .ToArray())
        {
            var effectPower = target.PowersUsedByMe
                .FirstOrDefault(x => x.UsablePower == usablePower);

            if (effectPower != null)
            {
                target.TerminatePower(effectPower);
            }

            grantedPowers.Remove(usablePower);
        }
    }
}
