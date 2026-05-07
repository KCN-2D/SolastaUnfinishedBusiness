using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameTravelSchedulerPatcher
{
    [HarmonyPatch(typeof(GameTravelScheduler), nameof(GameTravelScheduler.StartTravel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartTravel_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            CampaignTranslationRuntimeRepairContext.RepairTravelJournalSource();
        }
    }

    [HarmonyPatch(typeof(GameTravelScheduler), nameof(GameTravelScheduler.StartExploration))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartExploration_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            CampaignTranslationRuntimeRepairContext.RepairTravelJournalSource();
        }
    }
}

