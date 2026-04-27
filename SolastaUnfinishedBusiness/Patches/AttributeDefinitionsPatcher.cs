using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class AttributeDefinitionsPatcher
{
    [HarmonyPatch(typeof(AttributeDefinitions), nameof(AttributeDefinitions.ComputeCostToRaiseAbility))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeCostToRaiseAbility_Patch
    {
        [UsedImplicitly]
        public static void Postfix(int previousValue, ref int __result)
        {
            //PATCH: extends the cost buy table to enable `EpicPointsAndArray`
            if (!Main.Settings.EnableEpicPointsAndArray)
            {
                return;
            }

            __result = previousValue switch
            {
                15 or 16 => 3,
                17 or 18 => 4,
                _ => __result
            };
        }
    }
}
