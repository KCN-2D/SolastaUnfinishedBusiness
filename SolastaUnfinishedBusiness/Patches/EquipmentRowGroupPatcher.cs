using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class EquipmentRowGroupPatcher
{
    [HarmonyPatch(typeof(CharacterStageWithEquipmentPanel),
        nameof(CharacterStageWithEquipmentPanel.EnumerateSelectedEquipment))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumerateSelectedEquipment_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageWithEquipmentPanel __instance)
        {
            EquipmentSelectionLayout.Refresh(__instance);
        }
    }

    [HarmonyPatch(typeof(EquipmentRowGroup), nameof(EquipmentRowGroup.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(EquipmentRowGroup __instance)
        {
            EquipmentSelectionLayout.Refresh(__instance);
        }
    }
}
