using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class StockPanelPatcher
{
    [HarmonyPatch(typeof(StockPanel), nameof(StockPanel.Refresh))]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(StockPanel __instance) => !StockPanelLayout.IsResettingForShow(__instance);

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var displayed = AccessTools.Field(typeof(StockPanel), "displayedUnits");
            var count = AccessTools.PropertyGetter(typeof(List<GameStockUnit>), nameof(List<GameStockUnit>.Count));
            var price = AccessTools.Method(typeof(GameMerchant), nameof(GameMerchant.ComputePurchaseCostMultiplier));
            var priceIndex = code.FindIndex(instruction => instruction.Calls(price));
            var start = -1;
            for (var index = 1; index + 3 < code.Count; index++)
            {
                if (!code[index].LoadsField(displayed) || !code[index + 1].Calls(count)) continue;
                start = index;
                break;
            }
            if (priceIndex < 0 || priceIndex + 1 >= code.Count || code[priceIndex + 1].opcode != OpCodes.Stloc_0 ||
                start < 1 || code[start - 1].opcode != OpCodes.Ldarg_0 ||
                code[start + 2].opcode != OpCodes.Ldc_I4_0 ||
                code[start + 3].opcode.FlowControl != FlowControl.Cond_Branch)
            {
                Main.Error("StockPanel.Refresh native row rendering site differs.");
                return code;
            }

            // Keep native filtering (including known recipes and campaign rules).
            // The remaining native tail only builds the two stock layouts.
            code.RemoveRange(start, code.Count - start);
            code.Add(new CodeInstruction(OpCodes.Ldloc_0));
            code.Add(new CodeInstruction(OpCodes.Call, new Action<StockPanel, float>(StockPanelLayout.Refresh).Method));
            code.Add(new CodeInstruction(OpCodes.Ret));
            return code;
        }
    }

    [HarmonyPatch(typeof(StockPanel), nameof(StockPanel.SelectDefaultControlImpl))]
    [UsedImplicitly]
    public static class SelectDefaultControlImpl_Patch
    {
        [UsedImplicitly]
        private static void Prefix(StockPanel __instance) => StockPanelLayout.RefreshIfNeeded(__instance);
    }

    private static bool OwnsStockRows(StockPanel panel, Transform parent) => panel && parent &&
        (parent == panel.stockTable || parent == panel.stockGrid);

    private static void ReleaseUnboundStockRows(Transform parent, StockPanel panel)
    {
        if (OwnsStockRows(panel, parent) &&
            UiHelpers.RetainUnboundChildren<StockUnitLine>(parent, row => row.StockUnit == null)) return;

        Gui.ReleaseChildrenToPool(parent);
    }

    internal static Exception ReleaseStockRows(StockPanel panel, Exception originalException = null)
    {
        if (!panel) return originalException;
        originalException = UiHelpers.ReleasePooledChildren<StockUnitLine>(panel.stockTable,
            row => row.Unbind(), originalException);
        return UiHelpers.ReleasePooledChildren<StockUnitLine>(panel.stockGrid,
            row => row.Unbind(), originalException);
    }

    [HarmonyPatch(typeof(StockPanel), nameof(StockPanel.Unbind))]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.Method(typeof(Gui), nameof(Gui.ReleaseChildrenToPool)), 2,
                "StockPanel.Unbind.OwnedRows",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Action<Transform, StockPanel>(ReleaseUnboundStockRows).Method));

        [UsedImplicitly]
        private static Exception Finalizer(StockPanel __instance, Exception __exception) =>
            __exception == null ? null : ReleaseStockRows(__instance, __exception);
    }
}
