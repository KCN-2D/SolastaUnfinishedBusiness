using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetConditionPatcher
{
    [HarmonyPatch(typeof(RulesetCondition), nameof(RulesetCondition.RemainingRounds), MethodType.Setter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    public static class RemainingRounds_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetCondition __instance, int value)
        {
            // Pause only the optional Winged Boots timer; removal and dispelling still use the native lifecycle.
            return value >= __instance.RemainingRounds || !MovementSuspensionContext.IsDurationPaused(__instance);
        }
    }
}
