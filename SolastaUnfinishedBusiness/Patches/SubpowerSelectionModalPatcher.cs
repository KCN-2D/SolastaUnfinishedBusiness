using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SubpowerSelectionModalPatcher
{
    [HarmonyPatch(typeof(SubpowerSelectionModal), nameof(SubpowerSelectionModal.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SubpowerSelectionModal __instance)
        {
            FloatingPanelBounds.RestoreAttachmentList(__instance.mainPanel.RectTransform);
        }

        [UsedImplicitly]
        public static void Postfix(SubpowerSelectionModal __instance, RectTransform attachment)
        {
            FloatingPanelBounds.ConfigureNearAttachmentList(
                __instance.mainPanel.RectTransform, attachment, __instance.subpowersTable,
                new Vector3(70, -400, 0));
        }
    }

    [HarmonyPatch(typeof(SubpowerSelectionModal), nameof(SubpowerSelectionModal.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SubpowerSelectionModal __instance)
        {
            // Restore pooled controls while they still belong to this selection.
            FloatingPanelBounds.RestoreAttachmentList(__instance.mainPanel.RectTransform);
        }
    }
}
