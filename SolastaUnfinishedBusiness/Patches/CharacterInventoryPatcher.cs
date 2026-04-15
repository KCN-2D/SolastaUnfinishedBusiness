using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterInventoryPatcher
{
    [HarmonyPatch(typeof(RulesetInventory), nameof(RulesetInventory.ComputeCarriedWeight))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeCarriedWeight_Patch
    {
        private static readonly IEqualityComparer<RulesetItem> RulesetItemComparer = new RulesetItemReferenceComparer();

        [UsedImplicitly]
        public static void Postfix(RulesetInventory __instance, ref float __result)
        {
            if (!Main.Settings.ExcludeEquippedItemsFromCarriedWeight || __instance?.InventorySlotsByName == null)
            {
                return;
            }

            var equippedWeight = __instance.InventorySlotsByName.Values
                .Where(slot => slot is { EquipedItem: not null } && !slot.ConfigSlot)
                .Select(slot => slot.EquipedItem)
                .Distinct(RulesetItemComparer)
                .Sum(item => item.Weight);

            __result = Math.Max(0f, __result - equippedWeight);
        }

        private sealed class RulesetItemReferenceComparer : IEqualityComparer<RulesetItem>
        {
            public bool Equals(RulesetItem x, RulesetItem y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(RulesetItem obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
