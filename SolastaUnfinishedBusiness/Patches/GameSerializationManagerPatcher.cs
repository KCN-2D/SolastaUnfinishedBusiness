using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameSerializationManagerPatcher
{
    private static void UpdateHasSavedGames(GameSerializationManager manager)
    {
        //PATCH: update state of load buttons for SaveByLocation
        if (!SettingsContext.GuiModManagerInstance.EnableSaveByLocation || manager == null)
        {
            return;
        }

        manager.hasSavedGames = SaveByLocationContext.HasAnySaveGames();
    }

    private static IEnumerator UpdateHasSavedGamesAfter(GameSerializationManager manager, IEnumerator enumerator)
    {
        if (enumerator != null)
        {
            while (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }
        }

        UpdateHasSavedGames(manager);
    }

    [HarmonyPatch(typeof(GameSerializationManager), nameof(GameSerializationManager.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameSerializationManager __instance)
        {
            UpdateHasSavedGames(__instance);
        }
    }

    [HarmonyPatch(typeof(GameSerializationManager), nameof(GameSerializationManager.RefreshAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshAsync_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameSerializationManager __instance, ref IEnumerator __result)
        {
            if (!SettingsContext.GuiModManagerInstance.EnableSaveByLocation)
            {
                return;
            }

            __result = UpdateHasSavedGamesAfter(__instance, __result);
        }
    }
}
