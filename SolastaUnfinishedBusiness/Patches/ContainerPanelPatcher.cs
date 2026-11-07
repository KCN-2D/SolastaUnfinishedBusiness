using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ContainerPanelPatcher
{
    private static bool OwnsInventorySlots(ContainerPanel panel, Transform parent)
    {
        if (!panel || !parent || parent != panel.slotsTable)
        {
            return false;
        }

        // Search the existing hierarchy even while the inventory is hidden.
        for (var ancestor = panel.transform; ancestor; ancestor = ancestor.parent)
        {
            var inventory = ancestor.GetComponent<InventoryPanel>();
            if (inventory)
            {
                return inventory.personalContainerPanel == panel || inventory.externalContainerPanel == panel;
            }
        }

        return false;
    }

    private static void ReleaseUnboundInventorySlots(Transform parent, ContainerPanel panel)
    {
        if (OwnsInventorySlots(panel, parent))
        {
            var unbound = true;
            foreach (Transform child in parent)
            {
                var slot = child.GetComponent<InventorySlotBox>();
                if (!slot || slot.InventorySlot != null || slot.GuiCharacter != null || slot.BindToGameItems)
                {
                    unbound = false;
                    break;
                }
            }

            if (unbound)
            {
                // Native Unbind has cleared subscriptions, duration, tooltips and
                // decorations. Native RefreshVisibleSlots rebuilds its scratch lists
                // from these inactive children and performs every normal state refresh.
                foreach (Transform child in parent)
                {
                    child.gameObject.SetActive(false);
                }

                return;
            }
        }

        Gui.ReleaseChildrenToPool(parent);
    }

    internal static Exception ReleaseInventorySlots(ContainerPanel panel, Exception originalException = null)
    {
        if (!OwnsInventorySlots(panel, panel ? panel.slotsTable : null))
        {
            return originalException;
        }

        foreach (Transform child in panel.slotsTable)
        {
            try
            {
                var slot = child.GetComponent<InventorySlotBox>();
                if (slot)
                {
                    slot.Unbind();
                }
            }
            catch (Exception error)
            {
                originalException = originalException == null ? error :
                    new AggregateException(originalException, error);
            }
        }

        try
        {
            Gui.ReleaseChildrenToPool(panel.slotsTable);
        }
        catch (Exception error)
        {
            originalException = originalException == null ? error :
                new AggregateException(originalException, error);
        }

        return originalException;
    }

    [HarmonyPatch(typeof(ContainerPanel), nameof(ContainerPanel.Unbind))]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.Method(typeof(Gui), nameof(Gui.ReleaseChildrenToPool)), 1,
                "ContainerPanel.Unbind.OwnedSlots",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Action<Transform, ContainerPanel>(ReleaseUnboundInventorySlots).Method));

        [UsedImplicitly]
        private static Exception Finalizer(ContainerPanel __instance, Exception __exception) =>
            __exception == null ? null : ReleaseInventorySlots(__instance, __exception);
    }

    private static IEnumerable<CodeInstruction> ReplaceSlotsGetter(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator, string context)
    {
        var oldMethod = typeof(RulesetContainer).GetProperty(nameof(RulesetContainer.InventorySlots))!.GetGetMethod();
        var newMethod =
            typeof(InventoryManagementContext).GetMethod(nameof(InventoryManagementContext.GetFilteredSlots));

        // The native visible-slot loop reads the same list twice per item. Resolve the
        // filtering controls once for this synchronous refresh, retaining the live list.
        var slots = generator.DeclareLocal(typeof(List<RulesetInventorySlot>));
        var result = new List<CodeInstruction>
        {
            new(OpCodes.Ldarg_0),
            new(OpCodes.Call, AccessTools.PropertyGetter(typeof(ContainerPanel), nameof(ContainerPanel.Container))),
            new(OpCodes.Ldarg_0),
            new(OpCodes.Call, newMethod),
            new(OpCodes.Stloc, slots)
        };
        result.AddRange(instructions.ReplaceCalls(oldMethod, context,
            new CodeInstruction(OpCodes.Pop),
            new CodeInstruction(OpCodes.Ldloc, slots)));
        return result;
    }

    //PATCH: Enable Inventory Filtering and Sorting
    [HarmonyPatch(typeof(ContainerPanel), nameof(ContainerPanel.RefreshVisibleSlots))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshVisibleSlots_Patch
    {
        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            var oldMethod = typeof(RectTransform).GetProperty(nameof(RectTransform.anchoredPosition))!.GetSetMethod();
            var newMethod = new Action<RectTransform, Vector2>(SetAnchor).Method;
            var getPrefab = new Func<GameObject, Transform, GameObject>(Gui.GetPrefabFromPool).Method;
            var prepareSlot = new Func<GameObject, Transform, GameObject>(PreparePooledSlot).Method;

            return ReplaceSlotsGetter(instructions, generator, "ContainerPanel.RefreshVisibleSlots.1")
                .ReplaceCall(getPrefab, 1, "ContainerPanel.RefreshVisibleSlots.PooledSlot",
                    new CodeInstruction(OpCodes.Call, prepareSlot))
                .ReplaceCall(oldMethod, 1, "ContainerPanel.RefreshVisibleSlots.2",
                    new CodeInstruction(OpCodes.Call, newMethod));
        }

        private static GameObject PreparePooledSlot(GameObject prefab, Transform parent)
        {
            var instance = Gui.GuiService.PrefabPoolManager.GetElement(prefab, false);
            var transform = instance.transform;
            transform.SetParent(parent, false);
            transform.localScale = Vector3.one;
            ((RectTransform)transform).anchoredPosition = Vector2.zero;
            var slot = instance.GetComponent<InventorySlotBox>();
            var inactiveUnbound = !instance.activeSelf && slot && slot.InventorySlot == null;
            if (inactiveUnbound)
            {
                // The native visible-slot pass clears new, unbound boxes before binding.
                // Do that while still inactive instead of enabling, disabling, then enabling.
                slot.Unbind();
            }

            // Keep the native classification path if another pool consumer retained a binding.
            if (!inactiveUnbound)
            {
                instance.SetActive(true);
            }

            return instance;
        }

        private static void SetAnchor(RectTransform instance, Vector2 c)
        {
            if (!InventoryManagementContext.Enabled)
            {
                instance.anchoredPosition = c;
            }
        }
    }

    //PATCH: Enable Inventory Filtering and Sorting
    [HarmonyPatch(typeof(ContainerPanel), nameof(ContainerPanel.ComputeTableHeight))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeTableHeight_Patch
    {
        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            return ReplaceSlotsGetter(instructions, generator, "ContainerPanel.ComputeTableHeight");
        }
    }

    [HarmonyPatch(typeof(ContainerPanel), nameof(ContainerPanel.ContainerContentModified))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ContainerContentModified_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(ContainerPanel __instance)
        {
            //PATCH: Enable Inventory Filtering and Sorting
            InventoryManagementContext.Refresh(__instance, true);
        }
    }

    [HarmonyPatch(typeof(ContainerPanel), nameof(ContainerPanel.ItemCreated))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ItemCreated_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(ContainerPanel __instance)
        {
            //PATCH: Enable Inventory Filtering and Sorting
            InventoryManagementContext.Refresh(__instance, true);
        }
    }

    [HarmonyPatch(typeof(ContainerPanel), nameof(ContainerPanel.ItemDestroyed))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ItemDestroyed_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(ContainerPanel __instance)
        {
            //PATCH: Enable Inventory Filtering and Sorting
            InventoryManagementContext.Refresh(__instance, true);
        }
    }

    [HarmonyPatch(typeof(ContainerPanel), nameof(ContainerPanel.ItemUnequiped))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ItemUnequiped_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(ContainerPanel __instance)
        {
            //PATCH: Enable Inventory Filtering and Sorting
            InventoryManagementContext.Refresh(__instance, true);
        }
    }
}
