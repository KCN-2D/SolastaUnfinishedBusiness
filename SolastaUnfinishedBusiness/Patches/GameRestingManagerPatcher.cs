using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameRestingManagerPatcher
{
    [HarmonyPatch(typeof(GameRestingManager), nameof(GameRestingManager.CheckFoodAvailability))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckFoodAvailability_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameRestingManager __instance, ref int missingCount, ref bool __result)
        {
            __result = SimulacrumBehavior.CheckPartyFoodAvailability(__instance.heroes, out missingCount);

            return false;
        }
    }

    [HarmonyPatch(typeof(GameRestingManager), nameof(GameRestingManager.ConsumeFood))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ConsumeFood_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameRestingManager __instance, ref int __result)
        {
            __result = SimulacrumBehavior.ConsumePartyFood(__instance.heroes, __instance);

            return false;
        }
    }
}
