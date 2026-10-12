using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class MultiplayerSetupPanelPatcher
{
    //PATCH: set flag that prevents hero auto assignment under MP (DEFAULT_PARTY)
    [HarmonyPatch(typeof(MultiplayerSetupPanel), nameof(MultiplayerSetupPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            Global.IsSettingUpMultiplayer = true;
        }

        [UsedImplicitly]
        public static void Postfix(MultiplayerSetupPanel __instance)
        {
            UiTextHelpers.FitMultiplayerRoomColumns(__instance.joinGameSubpanel);
        }
    }

    [HarmonyPatch(typeof(MultiplayerSetupPanel), nameof(MultiplayerSetupPanel.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            Global.IsSettingUpMultiplayer = false;
        }
    }

    [HarmonyPatch(typeof(JoinGameSubpanel), nameof(JoinGameSubpanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(JoinGameSubpanel __instance)
        {
            UiTextHelpers.FitMultiplayerRoomColumns(__instance);
        }
    }
}
