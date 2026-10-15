using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class PosterScreenPatcher
{
    [HarmonyPatch(typeof(PosterScreen), nameof(PosterScreen.Show))]
    [UsedImplicitly]
    public static class Show_Patch
    {
        [UsedImplicitly]
        public static void Postfix(PosterScreen __instance)
        {
            GameLocationBanterManagerPatcher.RegisterInteractionPoster(__instance);
        }
    }

    [HarmonyPatch(typeof(PosterScreen), nameof(PosterScreen.OnUpdateHook))]
    [UsedImplicitly]
    public static class Update_Patch
    {
        [UsedImplicitly]
        public static void Prefix(PosterScreen __instance)
        {
            GameLocationBanterManagerPatcher.UpdateInteractionPanelSpeech(__instance);
        }
    }

    //PATCH: supports speech feature
    [HarmonyPatch(typeof(PosterScreen), nameof(PosterScreen.ProceedAndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ProceedAndHide_Patch
    {
        [UsedImplicitly]
        public static void Prefix(PosterScreen __instance)
        {
            GameLocationBanterManagerPatcher.EndInteractionPanelSpeech(__instance);
        }
    }

    [HarmonyPatch(typeof(PosterScreen), nameof(PosterScreen.OnBeginHide))]
    [UsedImplicitly]
    public static class Hide_Patch
    {
        [UsedImplicitly]
        public static void Prefix(PosterScreen __instance)
        {
            GameLocationBanterManagerPatcher.EndInteractionPanelSpeech(__instance);
        }
    }
}
