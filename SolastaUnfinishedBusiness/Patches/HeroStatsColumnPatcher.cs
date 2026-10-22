using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class HeroStatsColumnPatcher
{
    [HarmonyPatch(typeof(HeroStatsColumn), nameof(HeroStatsColumn.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCallChecked(AccessTools.PropertyGetter(typeof(GuiCharacter), nameof(GuiCharacter.Name)),
                2, 2, "HeroStatsColumn.Bind.Identity",
                new CodeInstruction(OpCodes.Call,
                    new Func<GuiCharacter, string>(GuiCharacterExtensions.GetIdentityName).Method));

        //PATCH: allow heroes not in pool (RESPEC ed) to be exported
        [UsedImplicitly]
        public static void Postfix(HeroStatsColumn __instance, RectTransform tooltipAnchor)
        {
            if (__instance.BuiltIn ||
                __instance.updateFileButton.interactable)
            {
                return;
            }

            __instance.updateFileButton.gameObject.SetActive(true);
            __instance.updateFileButtonTooltip.Anchor = tooltipAnchor;
            __instance.updateFileButtonTooltip.AnchorMode = TooltipDefinitions.AnchorMode.LEFT_FREE;
            __instance.updateFileButton.interactable = true;
        }
    }
}
