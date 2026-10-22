using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Behaviors;

/**
 * Adds all powers from condition definition's feature list to character's usable powers when condition is applied
 * and removes them when no remaining feature or condition grants the power.
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
        var grantedPowers = GetGrantedUsablePowers(target);
        var powers = new HashSet<FeatureDefinitionPower>(grantedPowers.Select(x => x.PowerDefinition));

        foreach (var power in rulesetCondition.ConditionDefinition.Features
                     .OfType<FeatureDefinitionPower>()
                     .Where(powers.Add))
        {
            grantedPowers.Add(PowerProvider.Get(power, target));
        }
    }

    public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition)
    {
        var retainedPowers = GetRemainingPowers(target, rulesetCondition);
        var powers = rulesetCondition.ConditionDefinition.Features
            .OfType<FeatureDefinitionPower>()
            .Where(power => !retainedPowers.Contains(power))
            .ToArray();

        var grantedPowers = GetGrantedUsablePowers(target);
        foreach (var usablePower in grantedPowers
                     .Where(x => powers.Contains(x.PowerDefinition))
                     .ToArray())
        {
            // TerminatePower refreshes the character. Revoke ownership first so
            // that refresh cannot automatically reactivate a permanent power.
            grantedPowers.Remove(usablePower);

            foreach (var effectPower in target.PowersUsedByMe
                         .Where(effect => effect.UsablePower == usablePower).ToArray())
            {
                target.TerminatePower(effectPower);
            }
        }
    }

    private static FeatureDefinitionPower[] GetRemainingPowers(
        RulesetCharacter target, RulesetCondition removedCondition)
    {
        // Instance removal updates its list before this callback; native type-based
        // removal updates it afterwards. Browse the same remaining owners in both cases.
        var category = target.ConditionsByCategory.Values
            .FirstOrDefault(conditions => conditions.Contains(removedCondition));
        var index = category?.IndexOf(removedCondition) ?? -1;
        if (index >= 0)
        {
            category.RemoveAt(index);
        }

        try
        {
            return target.FeaturesByType<FeatureDefinitionPower>().Distinct().ToArray();
        }
        finally
        {
            if (index >= 0)
            {
                category.Insert(index, removedCondition);
            }
        }
    }
}
