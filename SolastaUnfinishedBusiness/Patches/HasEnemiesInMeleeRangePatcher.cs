using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using TA.AI;
using TA.AI.Considerations;

namespace SolastaUnfinishedBusiness.Patches;

public static class HasEnemiesInMeleeRangePatcher
{
    [HarmonyPatch(typeof(HasEnemiesInMeleeRange), nameof(HasEnemiesInMeleeRange.Score))]
    [UsedImplicitly]
    public static class Score_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            DecisionContext context,
            ConsiderationDescription consideration,
            DecisionParameters parameters,
            ScoringResult scoringResult)
        {
            Score(context, consideration, parameters, scoringResult);

            return false;
        }

        private static void Score(DecisionContext context, ConsiderationDescription consideration,
            DecisionParameters parameters, ScoringResult scoringResult)
        {
            var stringParameter = consideration.StringParameter;
            var remainingRequiredEnemies = consideration.IntParameter;
            var useContextPosition = consideration.BoolParameter;
            var requiresVisibility = consideration.BoolSecParameter;
            var requiresOpportunityAttack = consideration.BoolTerParameter;
            var defender = parameters.character.GameLocationCharacter;
            var battleService = parameters.situationalInformation.BattleService;

            if (defender?.RulesetCharacter == null || battleService == null)
            {
                scoringResult.Score = 0f;
                return;
            }

            var defenderPosition = useContextPosition
                ? context.position
                : defender.LocationPosition;

            foreach (var relevantEnemy in parameters.situationalInformation.RelevantEnemies)
            {
                if (relevantEnemy?.RulesetCharacter == null ||
                    !AiLocationDefinitions.IsRelevantTargetForCharacter(
                        defender,
                        relevantEnemy,
                        parameters.situationalInformation.HasRelevantPerceivedTarget) ||
                    (!string.IsNullOrEmpty(stringParameter) &&
                     !relevantEnemy.RulesetCharacter.HasConditionOfTypeOrSubType(stringParameter)))
                {
                    continue;
                }

                var isEnemyWithinMeleeReachRange = CombatAiContext.IsAdvancedCombatAiFlightEnabled
                    ? CombatAiContext.CanAttackInMeleeFromPosition(
                        relevantEnemy,
                        relevantEnemy.LocationPosition,
                        defender,
                        defenderPosition,
                        battleService)
                    : battleService.IsWithinXCells(
                        relevantEnemy,
                        relevantEnemy.LocationPosition,
                        defender,
                        defenderPosition,
                        relevantEnemy.FindActionAttackMode(ActionDefinitions.Id.AttackMain)?.reachRange ?? 1);

                if (isEnemyWithinMeleeReachRange)
                {
                    if (requiresVisibility)
                    {
                        isEnemyWithinMeleeReachRange = !useContextPosition
                            ? relevantEnemy.PerceivedFoes.Contains(defender)
                            : battleService.CanAttackerSeeCharacterFromPosition(
                                defenderPosition, relevantEnemy.LocationPosition,
                                defender, relevantEnemy);
                    }

                    if (requiresOpportunityAttack)
                    {
                        isEnemyWithinMeleeReachRange &=
                            battleService.IsValidAttackerForOpportunityAttackOnCharacter(relevantEnemy, defender);
                    }

                    if (isEnemyWithinMeleeReachRange)
                    {
                        remainingRequiredEnemies--;
                    }
                }

                if (remainingRequiredEnemies <= 0)
                {
                    break;
                }
            }

            scoringResult.Score = remainingRequiredEnemies <= 0 ? 1f : 0f;
        }
    }
}
