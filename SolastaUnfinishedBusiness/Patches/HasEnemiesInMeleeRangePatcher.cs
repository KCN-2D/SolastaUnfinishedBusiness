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
            var num = consideration.IntParameter;
            var boolParameter = consideration.BoolParameter;
            var boolSecParameter = consideration.BoolSecParameter;
            var boolTerParameter = consideration.BoolTerParameter;
            var defenderPosition = boolParameter
                ? context.position
                : parameters.character.GameLocationCharacter.LocationPosition;
            foreach (var relevantEnemy in parameters.situationalInformation.RelevantEnemies)
            {
                if (!AiLocationDefinitions.IsRelevantTargetForCharacter(parameters.character.GameLocationCharacter,
                        relevantEnemy, parameters.situationalInformation.HasRelevantPerceivedTarget) ||
                    (!string.IsNullOrEmpty(stringParameter) && relevantEnemy.RulesetCharacter != null &&
                     !relevantEnemy.RulesetCharacter.HasConditionOfTypeOrSubType(stringParameter)))
                {
                    continue;
                }

                var isEnemyWithinMeleeReachRange = CombatAiContext.IsAdvancedCombatAiFlightEnabled
                    ? CombatAiContext.CanAttackInMeleeFromPosition(
                        relevantEnemy,
                        relevantEnemy.LocationPosition,
                        parameters.character.GameLocationCharacter,
                        defenderPosition,
                        parameters.situationalInformation.BattleService)
                    : parameters.situationalInformation.BattleService.IsWithinXCells(
                        relevantEnemy,
                        relevantEnemy.LocationPosition,
                        parameters.character.GameLocationCharacter,
                        defenderPosition,
                        relevantEnemy.FindActionAttackMode(ActionDefinitions.Id.AttackMain)?.reachRange ?? 1);

                if (isEnemyWithinMeleeReachRange)
                {
                    if (boolSecParameter)
                    {
                        isEnemyWithinMeleeReachRange = !boolParameter
                            ? relevantEnemy.PerceivedFoes.Contains(parameters.character.GameLocationCharacter)
                            : parameters.situationalInformation.BattleService.CanAttackerSeeCharacterFromPosition(
                                defenderPosition, relevantEnemy.LocationPosition,
                                parameters.character.GameLocationCharacter, relevantEnemy);
                    }

                    if (boolTerParameter)
                    {
                        isEnemyWithinMeleeReachRange &= parameters.situationalInformation.BattleService
                            .IsValidAttackerForOpportunityAttackOnCharacter(relevantEnemy,
                                parameters.character.GameLocationCharacter);
                    }

                    if (isEnemyWithinMeleeReachRange)
                    {
                        num--;
                    }
                }

                if (num <= 0)
                {
                    break;
                }
            }

            scoringResult.Score = num <= 0 ? 1f : 0f;
        }
    }
}
