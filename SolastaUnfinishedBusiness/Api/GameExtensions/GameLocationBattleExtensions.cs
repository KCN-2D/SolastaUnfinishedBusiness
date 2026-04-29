using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class GameLocationBattleExtensions
{
    internal static void ProcessExtraAfterAttackConditionsMatchingInterruption(
        GameLocationCharacter actingCharacter, RulesetActor rulesetDefender)
    {
        //PATCH: allow condition interruption after target was attacked not by source
        if (!rulesetDefender.matchingInterruption)
        {
            rulesetDefender.matchingInterruption = true;
            rulesetDefender.matchingInterruptionConditions.Clear();

            foreach (var rulesetCondition in rulesetDefender.ConditionsByCategory
                         .SelectMany(x => x.Value)
                         .Where(rulesetCondition =>
                             rulesetCondition.ConditionDefinition.HasSpecialInterruptionOfType(
                                 (RuleDefinitions.ConditionInterruption)ExtraConditionInterruption
                                     .AfterWasAttackedNotBySource) &&
                             rulesetCondition.SourceGuid != actingCharacter.Guid))
            {
                rulesetDefender.matchingInterruptionConditions.Add(rulesetCondition);
            }

            for (var index = rulesetDefender.matchingInterruptionConditions.Count - 1; index >= 0; --index)
            {
                rulesetDefender.RemoveCondition(rulesetDefender.matchingInterruptionConditions[index]);
            }

            rulesetDefender.matchingInterruptionConditions.Clear();
            rulesetDefender.matchingInterruption = false;
        }

        //PATCH: Allows condition interruption after target was attacked
        rulesetDefender.ProcessConditionsMatchingInterruption(
            (RuleDefinitions.ConditionInterruption)ExtraConditionInterruption.AfterWasAttacked);
    }

    internal static List<GameLocationCharacter> GetContenders(this GameLocationBattle battle,
        GameLocationCharacter character,
        GameLocationCharacter perceiver = null,
        bool hasToPerceivePerceiver = false,
        bool isOppositeSide = true,
        bool excludeSelf = true,
        bool hasToPerceiveTarget = false,
        int withinRange = 0)
    {
        var contenders = new List<GameLocationCharacter>();

        foreach (var contender in battle.AllContenders)
        {
            if (contender.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } ||
                contender.IsCharging ||
                contender.MoveStepInProgress ||
                excludeSelf && contender == character)
            {
                continue;
            }

            if (character != null)
            {
                if (isOppositeSide)
                {
                    if (!contender.IsOppositeSide(character.Side))
                    {
                        continue;
                    }
                }
                else if (contender.Side != character.Side)
                {
                    continue;
                }

                if (withinRange > 0 && !character.IsWithinRange(contender, withinRange))
                {
                    continue;
                }

                var finalPerceiver = perceiver ?? character;

                if (hasToPerceiveTarget && !finalPerceiver.CanPerceiveTarget(contender))
                {
                    continue;
                }

                if (hasToPerceivePerceiver && !contender.CanPerceiveTarget(finalPerceiver))
                {
                    continue;
                }
            }

            contenders.Add(contender);
        }

        return contenders;
    }
}
