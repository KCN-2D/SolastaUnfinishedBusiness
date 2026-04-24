using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

public static class MetaMagicSubPanelPatcher
{
    [HarmonyPatch(typeof(MetaMagicSubPanel), nameof(MetaMagicSubPanel.SetState))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshActions_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            MetaMagicSubPanel __instance,
            bool __0,
            HeroDefinitions.PointsPoolType __3,
            List<string> __4)
        {
            if (!__0 ||
                __3 != HeroDefinitions.PointsPoolType.Metamagic)
            {
                return;
            }

            CampaignsContext.SyncAndBindMetamagicOffering(__instance, __4);
        }
    }
}
