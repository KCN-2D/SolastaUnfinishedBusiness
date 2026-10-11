using System;
using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;

// ReSharper disable once CheckNamespace
internal delegate int ReducedDamageHandler(
    GameLocationCharacter attacker,
    GameLocationCharacter defender);

internal sealed class FeatureDefinitionReduceDamage : FeatureDefinition
{
    public RuleDefinitions.AdditionalDamageTriggerCondition TriggerCondition { get; set; }
    public ReducedDamageHandler ReducedDamage { get; set; }
    public List<string> DamageTypes { get; set; }
    public string NotificationTag { get; set; }
    public CharacterClassDefinition SpellCastingClass { get; set; }

    // for powers that reduce based on a point count
    public FeatureDefinitionPower FeedbackPower { get; set; }

    public Action<RulesetImplementationDefinitions.ApplyFormsParams, int, string> DamageReduced { get; set; }
    public Func<RulesetImplementationDefinitions.ApplyFormsParams, string, bool> CanReduceDamage { get; set; }

    // for feedback powers caused by an external effect
    public ConditionDefinition SourceCondition { get; set; }

    public static int DamageReduction(
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        int damage,
        string damageType)
    {
        var defender = formsParams.targetCharacter;
        var reduction = 0;

        // The feature and this attack's defender-specific tags own eligibility,
        // including protection granted to an enemy by another enemy.
        if (defender == null)
        {
            return reduction;
        }

        foreach (var feature in defender.FeaturesByType<FeatureDefinitionReduceDamage>())
        {
            if (feature.DamageTypes != null
                && feature.DamageTypes.Count != 0
                && !feature.DamageTypes.Contains(damageType))
            {
                continue;
            }

            if (feature.CanReduceDamage?.Invoke(formsParams, damageType) == false)
            {
                continue;
            }

            var prefix = $"{feature.Name}:{defender.Guid}:";
            var k = formsParams.sourceTags?.FindIndex(x => x.StartsWith(prefix)) ?? -1;

            if (k < 0)
            {
                continue;
            }

            var tag = formsParams.sourceTags[k];
            var damageReduced = 0;

            formsParams.sourceTags.RemoveAt(k);

            try
            {
                var tmp = int.Parse(tag.Split(':')[2]);
                var tmp2 = tmp;

                if (reduction + tmp > damage)
                {
                    tmp = reduction + tmp - damage;
                    formsParams.sourceTags.Add(prefix + tmp);
                    reduction = damage;
                }
                else
                {
                    reduction += tmp;
                    tmp = 0;
                }

                damageReduced = tmp2 - tmp;

                // need double if check here to avoid issues with unity objects lifecycle
                if (feature.FeedbackPower)
                {
                    if (defender is RulesetCharacter sourceRulesetCharacter)
                    {
                        sourceRulesetCharacter = sourceRulesetCharacter.TryGetConditionOfCategoryAndType(
                            AttributeDefinitions.TagEffect,
                            feature.SourceCondition?.Name,
                            out var activeCondition)
                            ? EffectHelpers.GetCharacterByGuid(activeCondition.SourceGuid)
                            : sourceRulesetCharacter;

                        sourceRulesetCharacter.UpdateUsageForPower(feature.FeedbackPower, tmp2 - tmp);
                    }
                }
            }
            catch (Exception)
            {
                // ignored
            }

            if (damageReduced > 0)
            {
                feature.DamageReduced?.Invoke(formsParams, damageReduced, damageType);
            }
        }

        return reduction;
    }
}
