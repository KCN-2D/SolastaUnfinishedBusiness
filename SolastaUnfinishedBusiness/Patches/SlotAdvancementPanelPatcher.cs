using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SlotAdvancementPanelPatcher
{
    [HarmonyPatch(typeof(SlotAdvancementPanel), nameof(SlotAdvancementPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SlotAdvancementPanel __instance)
        {
            SpellResourceSelectionPanel.Restore(__instance);
        }
    }

    [HarmonyPatch(typeof(SlotAdvancementPanel), nameof(SlotAdvancementPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SlotAdvancementPanel __instance)
        {
            SpellResourceSelectionPanel.Restore(__instance);
        }
    }
}
