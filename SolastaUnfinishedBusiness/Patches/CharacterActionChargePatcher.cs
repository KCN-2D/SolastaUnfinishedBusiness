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
            if (__instance.ActingCharacter.RulesetCharacter.GetOriginalHero() is not { } hero ||
                !HasOrcishAggressionFeat(hero))
            {
                return true;
            }

            __result = RaceFeats.CustomBehaviorOrcishAggression.ExecuteImpl(__instance);

            return false;
        }

        private static bool HasOrcishAggressionFeat(RulesetCharacterHero hero)
        {
            if (hero?.TrainedFeats == null)
            {
                return false;
            }

            foreach (var feat in hero.TrainedFeats)
            {
                if (feat == null)
                {
                    continue;
                }

                if (feat == RaceFeats.FeatOrcishAggressionStr ||
                    feat == RaceFeats.FeatOrcishAggressionCon)
                {
                    return true;
                }

                if (RaceFeats.FeatOrcishAggressionStr != null &&
                    Tabletop2024Context.AreEquivalentTabletopFeatNames(
                        feat.Name,
                        RaceFeats.FeatOrcishAggressionStr.Name))
                {
                    return true;
                }

                if (RaceFeats.FeatOrcishAggressionCon != null &&
                    Tabletop2024Context.AreEquivalentTabletopFeatNames(
                        feat.Name,
                        RaceFeats.FeatOrcishAggressionCon.Name))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
