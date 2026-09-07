using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SlotAdvancementBoxPatcher
{
    [HarmonyPatch(typeof(SlotAdvancementBox), nameof(SlotAdvancementBox.BindSlot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindSlot_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SlotAdvancementBox __instance)
        {
            SpellResourceSelectionPanel.RestoreRow(__instance);
        }
    }

    [HarmonyPatch(typeof(SlotAdvancementBox), nameof(SlotAdvancementBox.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SlotAdvancementBox __instance)
        {
            SpellResourceSelectionPanel.RestoreRow(__instance);
        }
    }
}
