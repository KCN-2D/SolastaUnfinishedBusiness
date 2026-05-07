using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameTravelJournalEntryPatcher
{
    private static readonly AccessTools.FieldRef<GameTravelJournalEntry, string> LogLineRef =
        AccessTools.FieldRefAccess<GameTravelJournalEntry, string>("logLine");

    [HarmonyPatch(typeof(GameTravelJournalEntry), nameof(GameTravelJournalEntry.ComputeHeight))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeHeight_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameTravelJournalEntry __instance)
        {
            ref var logLine = ref LogLineRef(__instance);

            if (CampaignTranslationRuntimeRepairContext.TryRepairTravelJournalLine(logLine, out var repairedLogLine))
            {
                logLine = repairedLogLine;
            }
        }
    }
}

