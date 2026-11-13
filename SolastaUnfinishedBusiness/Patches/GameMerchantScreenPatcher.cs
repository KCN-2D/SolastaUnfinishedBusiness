using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameMerchantScreenPatcher
{
    [HarmonyPatch(typeof(GameMerchantScreen), nameof(GameMerchantScreen.OnBeginShow))]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.Method(typeof(StockPanel), nameof(StockPanel.Reset)),
                1, "GameMerchantScreen.OnBeginShow.StockReset",
                new CodeInstruction(OpCodes.Call, new Action<StockPanel>(StockPanelLayout.ResetForShow).Method));
    }
}
