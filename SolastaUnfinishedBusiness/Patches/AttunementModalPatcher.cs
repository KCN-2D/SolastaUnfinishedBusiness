using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class AttunementModalPatcher
{
    [HarmonyPatch(typeof(AttunementModal), nameof(AttunementModal.FindAttunedCharacterOfItem))]
    [UsedImplicitly]
    public static class FindAttunedCharacterOfItem_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.PropertyGetter(typeof(GuiCharacter), nameof(GuiCharacter.Name)),
                1, "AttunementModal.FindAttunedCharacterOfItem.Identity",
                new CodeInstruction(OpCodes.Call,
                    new Func<GuiCharacter, string>(GuiCharacterExtensions.GetIdentityName).Method));
    }

    internal static void FitButtonCaption(GuiLabel label)
    {
        var slot = UiTextHelpers.FindAncestorComponent<AttunementSlotBox>(label);
        var button = UiTextHelpers.FindAncestorComponent<Button>(label);
        if (slot && button && (button == slot.removeAttunementButton || button == slot.attunementButton))
        {
            UiTextHelpers.FitButtonCaption(button, label.TMP_Text);
        }
    }

    private static void FitButtonCaptions(AttunementModal modal)
    {
        foreach (var label in modal.attunementSlotsTable.GetComponentsInChildren<GuiLabel>(true))
        {
            FitButtonCaption(label);
        }
        foreach (var label in modal.attunableItemsTable.GetComponentsInChildren<GuiLabel>(true))
        {
            FitButtonCaption(label);
        }
    }

    private static int GetLimit(AttunementModal modal)
    {
        return modal.AttuningCharacter.RulesetCharacterHero.GetAttunementLimit();
    }

    private static void RefreshAttunementCountLabel(AttunementModal modal)
    {
        var text = Gui.Format(AttunementModal.AttunementCountFormat,
            modal.attunedItems.Count.ToString(),
            GetLimit(modal).ToString());

        modal.attunementCountLabel.Text = text.Replace("\n", "");
    }

    private static void RefreshSlots(AttunementModal modal)
    {
        var hero = modal.AttuningCharacter.RulesetCharacterHero;
        hero.EnumerateAttunedItems(modal.heroes, modal.attunedItems);

        var count = Math.Max(GetLimit(modal), modal.attunedItems.Count);
        var table = modal.attunementSlotsTable;

        while (table.childCount < count)
        {
            Gui.GetPrefabFromPool(modal.attunementSlotPrefab, table);
        }

        for (var i = 0; i < table.childCount; i++)
        {
            table.GetChild(i).gameObject.SetActive(i < count);
        }
    }

    [HarmonyPatch(typeof(AttunementModal), nameof(AttunementModal.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix(AttunementModal __instance)
        {
            RefreshSlots(__instance);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var method = new Func<AttunementModal, int>(GetLimit).Method;
            var codes = instructions.ToList();
            var achievement = codes.FindIndex(code => code.opcode == OpCodes.Ldstr &&
                                                      code.operand is "ACH_ATTUNED");
            var achievementLimit = achievement >= 2 ? codes[achievement - 2] : null;

            return codes.ReplaceCode(x => x.opcode == OpCodes.Ldc_I4_3 && x != achievementLimit,
                -1, "AttunementModal.Refresh",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, method));
        }

        [UsedImplicitly]
        public static void Postfix(AttunementModal __instance)
        {
            var limit = GetLimit(__instance);
            if (__instance.attunedItems.Count > limit)
            {
                __instance.attunableItemsCanvasGroup.interactable = false;
                __instance.attunableItemsCanvasGroup.alpha = 0.3f;
                __instance.attunementLimitWarningLabel.gameObject.SetActive(true);
                __instance.attunementLimitWarningLabel.Text = Gui.Colorize(
                    Gui.Format(AttunementModal.AttunableItemsWarningFormat,
                        __instance.AttuningCharacter.Name, limit.ToString()), "EC6661");
            }

            RefreshAttunementCountLabel(__instance);
            FitButtonCaptions(__instance);
        }
    }

    [HarmonyPatch(typeof(AttunementModal), nameof(AttunementModal.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(AttunementModal __instance)
        {
            var label = __instance.attunementCountLabel;
            var rect = label.GetComponent<RectTransform>();

            label.TMP_Text.alignment = TextAlignmentOptions.Left;

            rect.sizeDelta = new Vector2(300, rect.sizeDelta.y);
            rect.localPosition = new Vector2(10, 225);
        }
    }
}
