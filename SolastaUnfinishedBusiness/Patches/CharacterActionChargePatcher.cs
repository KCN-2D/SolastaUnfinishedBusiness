using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionChargePatcher
{
    //PATCH: supports Orcish Aggression
    [HarmonyPatch(typeof(CharacterActionCharge), nameof(CharacterActionCharge.ExecuteImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteImpl_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ref IEnumerator __result, CharacterActionCharge __instance)
        {
            if (!HasOrcishAggressionFeat(__instance.ActingCharacter.RulesetCharacter))
            {
                return true;
            }

            __result = RaceFeats.CustomBehaviorOrcishAggression.ExecuteImpl(__instance);

            return false;
        }

        private static bool HasOrcishAggressionFeat(RulesetCharacter character)
        {
            return Tabletop2024Context.HasEquivalentTrainedFeat(
                       character,
                       RaceFeats.FeatOrcishAggressionStr) ||
                   Tabletop2024Context.HasEquivalentTrainedFeat(
                       character,
                       RaceFeats.FeatOrcishAggressionCon);
        }
    }
}
