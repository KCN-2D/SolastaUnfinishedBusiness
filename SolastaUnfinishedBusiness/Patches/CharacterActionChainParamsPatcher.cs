using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionChainParamsPatcher
{
    [HarmonyPatch(typeof(CharacterActionChainParams), nameof(CharacterActionChainParams.Evaluate))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Evaluate_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterActionChainParams __instance, bool cosmetic)
        {
            return !FreeJumpContext.TrySeedBattleFreeJumpEvaluation(__instance, cosmetic);
        }
    }
}
