using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using TA.AI;
using TA.AI.Considerations;

namespace SolastaUnfinishedBusiness.Patches.Considerations;

[UsedImplicitly]
public static class InfluenceEnemyProximityPatcher
{
    //PATCH: allows this influence to be reverted if enemy has StringParameter condition name
    //used on Command Spell, approach command
    [HarmonyPatch(typeof(InfluenceEnemyProximity), nameof(InfluenceEnemyProximity.Score))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
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

        // mainly vanilla code except for BEGIN/END blocks
        private static void Score(
            DecisionContext context,
            ConsiderationDescription consideration,
            DecisionParameters parameters,
            ScoringResult scoringResult)
        {
            scoringResult.Score = CombatAiContext.ComputeEnemyProximityScore(context, consideration, parameters);
        }
    }
}
