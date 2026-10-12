using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationScreenNarrationPatcher
{
    [HarmonyPatch(typeof(GameLocationScreenNarration), nameof(GameLocationScreenNarration.OnBeginHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginHide_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationScreenNarration __instance)
        {
            NarrativeDirectionManagerPatcher.CancelSequenceSpeech(
                __instance.NarrativeDirectionService?.NarrativePlayer?.NarrativeSequence);
        }
    }

    [HarmonyPatch(typeof(GameLocationScreenNarration), nameof(GameLocationScreenNarration.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationScreenNarration __instance)
        {
            NarrativeDirectionManagerPatcher.OpenSequenceSpeech(
                __instance.NarrativeDirectionService?.NarrativePlayer?.NarrativeSequence);
        }
    }
}
