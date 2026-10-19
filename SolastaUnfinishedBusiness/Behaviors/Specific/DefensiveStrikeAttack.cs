using System.Collections;
using System;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses;
using static RuleDefinitions;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

//Old Attack of Opportunity before it became too narrow to use
internal static class DefensiveStrikeAttack
{
    internal static IEnumerator ProcessOnCharacterAttackFinished(
        GameLocationBattleManager battleManager,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        CharacterAction sourceAction = null)
    {
        if (Gui.Battle == null)
        {
            yield break;
        }

        var units = Gui.Battle.AllContenders.ToArray(); // avoid changing enumerator

        //Process other participants of the battle
        foreach (var unit in units)
        {
            if (unit.RulesetCharacter is not { IsDeadOrUnconscious: false } ||
                attacker == unit ||
                defender == unit)
            {
                continue;
            }

            yield return ActiveDefensiveStrike(unit, attacker, defender, battleManager, sourceAction);
        }
    }

    private static IEnumerator ActiveDefensiveStrike(
        [NotNull] GameLocationCharacter unit,
        [NotNull] GameLocationCharacter attacker,
        GameLocationCharacter defender,
        GameLocationBattleManager battleManager,
        CharacterAction sourceAction)
    {
        var unitCharacter = unit.RulesetCharacter;

        if (!TargetingContext.IsValidOffensiveTarget(unit, attacker) ||
            !TargetingContext.IsValidOffensiveTarget(defender, attacker) ||
            sourceAction is { AttackRollOutcome: not (RollOutcome.Success or RollOutcome.CriticalSuccess) } ||
            defender.IsOppositeSide(unit.Side) ||
            unit == defender ||
            !unitCharacter.HasSubFeatureOfType<DefensiveStrikeMarker>())
        {
            yield break;
        }

        //Is this unit able to react (not paralyzed, prone etc.)?
        if (!unit.CanReact(true))
        {
            yield break;
        }

        //Is defender able to use reaction (has reaction available and not paralyzed, prone etc.)?
        if (!defender.CanReact())
        {
            yield break;
        }

        //Can this unit see defender?
        if (!unit.CanSeeTarget(defender))
        {
            yield break;
        }

        //Does unit has enough Channel Divinity uses left?
        var maxUses = unitCharacter.TryGetAttributeValue(AttributeDefinitions.ChannelDivinityNumber);

        if (unitCharacter.UsedChannelDivinity >= maxUses)
        {
            yield break;
        }

        //Find defender's attack mode
        var (opportunityAttackMode, actionModifier) = defender.GetFirstMeleeModeThatCanAttack(attacker, battleManager);

        if (opportunityAttackMode?.EffectDescription.FindFirstDamageForm() == null || actionModifier == null)
        {
            yield break;
        }

        //Calculate bonus
        var charisma = unitCharacter.TryGetAttributeValue(AttributeDefinitions.Charisma);
        var bonus = Math.Max(1, AttributeDefinitions.ComputeAbilityScoreModifier(charisma));

        yield return unit.MyReactToDoNothing(
            ExtraActionId.DoNothingFree,
            attacker,
            OathOfAltruism.DefensiveStrike,
            $"CustomReaction{OathOfAltruism.DefensiveStrike}Description"
                .Formatted(Category.Reaction, defender.Name, attacker.Name, bonus),
            ReactionValidated,
            battleManager: battleManager,
            resource: ReactionResourceChannelDivinity.Instance,
            target: attacker,
            reactionStillValid: () => ReactionStillValid(false));

        yield break;

        void ReactionValidated()
        {
            if (!ReactionStillValid(false))
            {
                return;
            }

            // Recompute the chosen mode's modifier after the prompt instead of
            // retaining conditions or cover from the initial candidate check.
            var evaluation = new BattleDefinitions.AttackEvaluationParams();
            actionModifier = new ActionModifier();
            evaluation.FillForPhysicalReachAttack(defender, defender.LocationPosition, opportunityAttackMode,
                attacker, attacker.LocationPosition, actionModifier);
            if (!battleManager.CanAttack(evaluation))
            {
                return;
            }

            //spend resources
            unitCharacter.UsedChannelDivinity++;

            //create attack mode copy so we won't affect real one
            var attackMode = RulesetAttackMode.AttackModesPool.Get();

            attackMode.Copy(opportunityAttackMode);
            attackMode.actionType = ActionType.Reaction;
            attackMode.AddAttackTagAsNeeded(AttacksOfOpportunity.NotAoOTag);

            //Apply bonus to hit and damage of the attack mode
            attackMode.EffectDescription.FindFirstDamageForm().BonusDamage += bonus;
            attackMode.ToHitBonus += bonus;
            attackMode.ToHitBonusTrends.Add(
                new TrendInfo(bonus, FeatureSourceType.CharacterFeature, OathOfAltruism.DefensiveStrike, unit));

            //Execute attack
            var parameters = new CharacterActionParams(defender, Id.AttackOpportunity, attackMode,
                attacker, actionModifier);
            // The channel use has committed, while the granted attack still owns its
            // normal native reaction cost and must remain legal until execution.
            ReactionRequestCallback.SetValidation(parameters, () => ReactionStillValid(true));
            var returned = false;
            try
            {
                ServiceRepository.GetService<IGameLocationActionService>()
                    .ExecuteAction(parameters, ReleaseAttack, true);
            }
            catch
            {
                ReleaseAttack(null);
                throw;
            }

            return;

            void ReleaseAttack(CharacterAction _)
            {
                if (returned)
                {
                    return;
                }

                returned = true;
                ReactionRequestCallback.SetValidation(parameters, null);
                RulesetAttackMode.AttackModesPool.Return(attackMode);
            }
        }

        bool ReactionStillValid(bool channelCommitted)
        {
            if (unitCharacter is not { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } ||
                unit.IsCharging || unit.MoveStepInProgress ||
                defender.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } ||
                attacker.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } ||
                sourceAction is { Aborted: true } ||
                sourceAction is CharacterActionMagicEffect { ExecutionFailed: true } or
                    CharacterActionMagicEffect { Countered: true } ||
                sourceAction is { AttackRollOutcome: not (RollOutcome.Success or RollOutcome.CriticalSuccess) } ||
                defender.IsCharging || defender.MoveStepInProgress ||
                !defender.CanReact() ||
                !unit.CanSeeTarget(defender) || defender.IsOppositeSide(unit.Side) ||
                !TargetingContext.IsValidOffensiveTarget(unit, attacker) ||
                !TargetingContext.IsValidOffensiveTarget(defender, attacker) ||
                !unitCharacter.HasSubFeatureOfType<DefensiveStrikeMarker>() ||
                !channelCommitted && unitCharacter.UsedChannelDivinity >=
                unitCharacter.TryGetAttributeValue(AttributeDefinitions.ChannelDivinityNumber))
            {
                return false;
            }

            if (!defender.RulesetCharacter.AttackModes.Contains(opportunityAttackMode))
            {
                return false;
            }

            var evaluation = new BattleDefinitions.AttackEvaluationParams();
            evaluation.FillForPhysicalReachAttack(defender, defender.LocationPosition, opportunityAttackMode,
                attacker, attacker.LocationPosition, new ActionModifier());
            return battleManager.CanAttack(evaluation);
        }
    }
}

internal sealed class DefensiveStrikeMarker
{
    private DefensiveStrikeMarker()
    {
    }

    public static DefensiveStrikeMarker Mark { get; } = new();
}
