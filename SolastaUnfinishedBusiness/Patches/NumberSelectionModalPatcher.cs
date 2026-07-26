using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NumberSelectionModalPatcher
{
    [HarmonyPatch(typeof(NumberSelectionModal), nameof(NumberSelectionModal.OnValidateCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnValidateCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(NumberSelectionModal __instance, int ___currentValue)
        {
            return !SimulacrumRepairInput.TryConfirm(__instance, ___currentValue);
        }
    }

    [HarmonyPatch(typeof(NumberSelectionModal), nameof(NumberSelectionModal.OnCloseCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnCloseCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(NumberSelectionModal __instance)
        {
            return !SimulacrumRepairInput.TryCancel(__instance);
        }
    }

    [HarmonyPatch(typeof(NumberSelectionModal), nameof(NumberSelectionModal.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NumberSelectionModal __instance)
        {
            SimulacrumRepairInput.OnEndHide(__instance);
        }
    }
}
