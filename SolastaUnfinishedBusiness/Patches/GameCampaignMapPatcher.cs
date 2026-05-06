using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameCampaignMapPatcher
{
    [HarmonyPatch(typeof(GameCampaignMap), nameof(GameCampaignMap.Initialize))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Initialize_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameCampaignMap __instance)
        {
            CampaignTranslationRuntimeRepairContext.RepairCampaignMap(__instance);
        }
    }
}
