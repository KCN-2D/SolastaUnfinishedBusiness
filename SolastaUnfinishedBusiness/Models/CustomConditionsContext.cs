using System.Collections;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionCombatAffinitys;

namespace SolastaUnfinishedBusiness.Models;

/**
 * Place for generic conditions that may be reused between several features
 */
internal static class CustomConditionsContext
{
    internal static ConditionDefinition Distracted;

    internal static ConditionDefinition LightSensitivity;

    internal static ConditionDefinition StopMovement;

    internal static ConditionDefinition Taunted;

    private static ConditionDefinition _taunter;

    internal static void Load()
    {
        Distracted = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionTrueStrike, "ConditionDistractedByAlly")
            .SetOrUpdateGuiPresentation(Category.Condition)
            .SetSpecialInterruptions(ExtraConditionInterruption.AfterWasAttacked)
            .SetFeatures(
                FeatureDefinitionCombatAffinityBuilder
                    .Create("CombatAffinityDistractedByAlly")
                    .SetGuiPresentation("ConditionDistractedByAlly", Category.Condition, Gui.NoLocalization)
                    .SetAttackOnMeAdvantage(AdvantageType.Advantage)
                    .AddToDB())
            .AddToDB();

        MovementSuspensionContext.Load();

        LightSensitivity = BuildLightSensitivity();

        StopMovement = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionRestrained, "ConditionStopMovement")
            .SetOrUpdateGuiPresentation(Category.Condition)
            .SetFeatures(
                FeatureDefinitionMovementAffinitys.MovementAffinityConditionRestrained,
                FeatureDefinitionActionAffinitys.ActionAffinityConditionRestrained)
            .AddToDB();

        BuildTaunted();
    }

    private static ConditionDefinition BuildLightSensitivity()
    {
        var abilityCheckAffinityLightSensitivity = FeatureDefinitionAbilityCheckAffinityBuilder
            .Create("AbilityCheckAffinityLightSensitivity")
            .SetGuiPresentation("ConditionLightSensitivity", Category.Condition, Gui.NoLocalization)
            .BuildAndSetAffinityGroups(
                CharacterAbilityCheckAffinity.Disadvantage,
                abilityProficiencyPairs: (AttributeDefinitions.Wisdom, SkillDefinitions.Perception))
            .AddToDB();

        var combatAffinityDarkelfLightSensitivity = FeatureDefinitionCombatAffinityBuilder
            .Create(CombatAffinitySensitiveToLight, "CombatAffinityLightSensitivity")
            .SetGuiPresentation("ConditionLightSensitivity", Category.Condition, Gui.NoLocalization)
            .SetMyAttackAdvantage(AdvantageType.None)
            .SetMyAttackModifierSign(AttackModifierSign.Substract)
            .SetMyAttackModifierDie(DieType.D4)
            .AddToDB();

        var conditionLightSensitive = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionLightSensitive, "ConditionLightSensitivity")
            .SetOrUpdateGuiPresentation(Category.Condition)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetPossessive()
            .SetConditionType(ConditionType.Detrimental)
            .SetFeatures(abilityCheckAffinityLightSensitivity, combatAffinityDarkelfLightSensitivity)
            .AddToDB();

        return conditionLightSensitive;
    }


    private static void BuildTaunted()
    {
        _taunter = ConditionDefinitionBuilder
            .Create("ConditionTaunter")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddCustomSubFeatures(new ActionFinishedByMeTaunter())
            .AddToDB();

        var combatAffinityTaunted = FeatureDefinitionCombatAffinityBuilder
            .Create("CombatAffinityTaunted")
            .SetGuiPresentation("ConditionTaunted", Category.Condition, Gui.NoLocalization)
            .SetMyAttackAdvantage(AdvantageType.Disadvantage)
            .SetSituationalContext(ExtraSituationalContext.IsNotConditionSource)
            .AddToDB();

        Taunted = ConditionDefinitionBuilder
            .Create("ConditionTaunted")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionConfused)
            .SetConditionType(ConditionType.Detrimental)
            .SetConditionParticleReference(ConditionDefinitions.ConditionUnderDemonicInfluence)
            .SetFeatures(combatAffinityTaunted)
            .AddCustomSubFeatures(new CustomBehaviorTaunted())
            .AddToDB();

        combatAffinityTaunted.requiredCondition = Taunted;
    }

    private static int GetTauntedRange(string effectDescriptionName)
    {
        return effectDescriptionName switch
        {
            "PowerGambitGoadingReact" or "ActionAffinityMartialGuardianCompellingStrike" => 1,
            "PowerMartialGuardianGrandChallenge" => 6,
            _ => 18 // Thunder Gauntlet
        };
    }

    private sealed class ActionFinishedByMeTaunter : IActionFinishedByMe
    {
        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (Gui.Battle == null)
            {
                yield break;
            }

            if (action is not CharacterActionMove or CharacterActionDash or CharacterActionAttack)
            {
                yield break;
            }

            var actingCharacter = action.ActingCharacter;
            var targets = Gui.Battle.GetContenders(actingCharacter);

            foreach (var target in targets)
            {
                var rulesetTarget = target.RulesetActor;

                if (!rulesetTarget.TryGetConditionOfCategoryAndType(
                        AttributeDefinitions.TagEffect, Taunted.Name, out var activeCondition) ||
                    activeCondition.SourceGuid != actingCharacter.Guid)
                {
                    continue;
                }

                // ruleset amount carries the max range for the condition
                if (DistanceCalculation
                        .GetDistanceFromCharacter(target, actingCharacter.DestinationPosition) > activeCondition.Amount)
                {
                    target.RulesetCharacter.RemoveCondition(activeCondition);
                }
            }
        }
    }

    //
    // Taunted
    //

    private sealed class CustomBehaviorTaunted : IActionFinishedByMe, IOnConditionAddedOrRemoved
    {
        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (action is not CharacterActionMove or CharacterActionDash)
            {
                yield break;
            }

            var actingCharacter = action.ActingCharacter;
            var rulesetCharacter = actingCharacter.RulesetCharacter;

            // need ToArray to avoid enumerator issues with RemoveCondition
            foreach (var rulesetCondition in rulesetCharacter.ConditionsByCategory
                         .SelectMany(x => x.Value)
                         .Where(x => x.ConditionDefinition.Name == Taunted.Name)
                         .Select(a => new { a, rulesetCaster = EffectHelpers.GetCharacterByGuid(a.SourceGuid) })
                         .Where(t => t.rulesetCaster != null)
                         .Select(b => new { b, caster = GameLocationCharacter.GetFromActor(b.rulesetCaster) })
                         .Where(t =>
                             // ruleset amount carries the max range for the condition
                             t.caster != null && !t.caster.IsWithinRange(actingCharacter, t.b.a.Amount))
                         .Select(c => c.b.a)
                         .ToArray())
            {
                rulesetCharacter.RemoveCondition(rulesetCondition);
            }
        }

        public void OnConditionAdded(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            var sourceCharacter = EffectHelpers.GetCharacterByGuid(rulesetCondition.SourceGuid);
            var tauntedRange = GetTauntedRange(rulesetCondition.effectDefinitionName);

            // for some reason when conditions are refreshed the original effect name and amount aren't updating
            // also, when condition is applied from an item (gauntlet), the end occurence gets set to StartOfTurn
            // below is a hack for these particular scenarios
            if (target.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, Taunted.Name, out var activeCondition))
            {
                activeCondition.amount = tauntedRange;
                activeCondition.effectDefinitionName = rulesetCondition.effectDefinitionName;
                activeCondition.sourceGuid = rulesetCondition.sourceGuid;
                activeCondition.durationType = DurationType.Round;
                activeCondition.durationParameter = 1;
                activeCondition.EndOccurence = TurnOccurenceType.EndOfSourceTurn;
            }

            sourceCharacter?.InflictCondition(
                _taunter.Name,
                DurationType.Round,
                1,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                sourceCharacter.Guid,
                sourceCharacter.CurrentFaction.Name,
                1,
                _taunter.Name,
                tauntedRange,
                0,
                0);
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            var sourceCharacter = EffectHelpers.GetCharacterByGuid(rulesetCondition.SourceGuid);

            if (sourceCharacter == null)
            {
                return;
            }

            if (sourceCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, _taunter.Name, out var activeCondition))
            {
                sourceCharacter.RemoveCondition(activeCondition);
            }
        }
    }
}
