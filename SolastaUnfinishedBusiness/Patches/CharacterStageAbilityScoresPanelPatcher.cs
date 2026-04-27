using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageAbilityScoresPanelPatcher
{
    private const CharacterStageAbilityScoresPanel.AbilityScoreMethod PointBuyMethod =
        CharacterStageAbilityScoresPanel.AbilityScoreMethod.PointSystem;

    private static bool ShouldForcePointBuy(CharacterStageAbilityScoresPanel panel)
    {
        return Main.Settings.ForcePointBuyAbilityScores && panel != null && panel.creationMode;
    }

    private static void SetPointBuyMethod(CharacterStageAbilityScoresPanel panel)
    {
        panel.currentMethod = PointBuyMethod;
        panel.freeEdition = false;

        SetFreeEditionToggle(panel, false);
    }

    private static void SetFreeEditionToggle(CharacterStageAbilityScoresPanel panel, bool interactable)
    {
        if (panel.freeEditionToggle)
        {
            var settingToggle = panel.settingToggle;

            panel.settingToggle = true;
            panel.freeEditionToggle.isOn = false;
            panel.settingToggle = settingToggle;
            panel.freeEditionToggle.interactable = interactable;
        }
    }

    private static void RefreshAbilityScoreMethodUiState(CharacterStageAbilityScoresPanel panel)
    {
        if (ShouldForcePointBuy(panel))
        {
            ApplyPointBuyOnlyUiState(panel);
        }
        else
        {
            RestoreAbilityScoreMethodUiState(panel);
        }
    }

    private static void ApplyPointBuyOnlyUiState(CharacterStageAbilityScoresPanel panel)
    {
        if (panel == null)
        {
            return;
        }

        SetPointBuyMethod(panel);

        if (panel.scoreMethodTitle)
        {
            panel.scoreMethodTitle.gameObject.SetActive(false);
        }

        if (panel.scoreMethodToggle)
        {
            panel.scoreMethodToggle.gameObject.SetActive(false);

            if (panel.scoreMethodToggle.button)
            {
                panel.scoreMethodToggle.button.interactable = false;
            }
        }

        if (panel.rollsGroup)
        {
            panel.rollsGroup.gameObject.SetActive(false);
        }

        if (panel.pointsGroup)
        {
            panel.pointsGroup.gameObject.SetActive(true);
        }

        if (panel.scoreMethodValidationGroup)
        {
            panel.scoreMethodValidationGroup.CanValidate = false;
        }

        if (panel.diceRollsValidationGroup)
        {
            panel.diceRollsValidationGroup.CanValidate = false;
        }
    }

    private static void RestoreAbilityScoreMethodUiState(CharacterStageAbilityScoresPanel panel)
    {
        if (panel == null || !panel.creationMode)
        {
            return;
        }

        if (panel.scoreMethodTitle)
        {
            panel.scoreMethodTitle.gameObject.SetActive(true);
        }

        if (panel.scoreMethodToggle)
        {
            panel.scoreMethodToggle.gameObject.SetActive(true);

            if (panel.scoreMethodToggle.button)
            {
                panel.scoreMethodToggle.button.interactable = true;
            }
        }

        if (panel.freeEditionToggle)
        {
            panel.freeEditionToggle.interactable = true;
        }
    }

    private static void EnsureDiceRollValues(CharacterStageAbilityScoresPanel panel)
    {
        if (panel == null)
        {
            return;
        }

        var abilityCount = AttributeDefinitions.AbilityScoreNames.Length;

        panel.rollValues ??= [];

        while (panel.rollValues.Count < abilityCount)
        {
            panel.rollValues.Add(RuleDefinitions.RollDiceAndSumBest(4, RuleDefinitions.DieType.D6, 3));
        }

        panel.rollValues.Sort(panel.intDecreasingComparer);
    }

    private static void RemoveForcedValidationGroups(CharacterStageAbilityScoresPanel panel)
    {
        if (!ShouldForcePointBuy(panel))
        {
            return;
        }

        var validationGroups = panel.ValidationGroups;

        if (validationGroups == null || validationGroups.Count == 0)
        {
            return;
        }

        var activeGroup = panel.ValidationGroupIndex >= 0 && panel.ValidationGroupIndex < validationGroups.Count
            ? validationGroups[panel.ValidationGroupIndex]
            : null;

        validationGroups.Remove(panel.scoreMethodValidationGroup);
        validationGroups.Remove(panel.diceRollsValidationGroup);

        if (activeGroup != panel.scoreMethodValidationGroup && activeGroup != panel.diceRollsValidationGroup)
        {
            return;
        }

        var pointSystemIndex = validationGroups.IndexOf(panel.pointSystemValidationGroup);

        if (pointSystemIndex >= 0)
        {
            panel.ValidationGroupIndex = pointSystemIndex;
        }
    }

    [HarmonyPatch(typeof(CharacterStageAbilityScoresPanel), nameof(CharacterStageAbilityScoresPanel.InitStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InitStage_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageAbilityScoresPanel __instance)
        {
            if (!ShouldForcePointBuy(__instance))
            {
                return;
            }

            SetPointBuyMethod(__instance);
        }
    }

    //PATCH: extends the cost buy table to enable `EpicPointsAndArray`
    [HarmonyPatch(typeof(CharacterStageAbilityScoresPanel), nameof(CharacterStageAbilityScoresPanel.Reset))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Reset_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            if (!Main.Settings.EnableEpicPointsAndArray)
            {
                return instructions;
            }

            return instructions.ReplaceCode(
                instruction => instruction.opcode == OpCodes.Ldc_I4_S &&
                               instruction.operand.ToString() == RulesContext.GameBuyPoints.ToString(),
                -1, "CharacterStageAbilityScoresPanel.Reset",
                new CodeInstruction(OpCodes.Ldc_I4_S, RulesContext.ModBuyPoints));
        }

        [UsedImplicitly]
        public static void Prefix(CharacterStageAbilityScoresPanel __instance)
        {
            if (!ShouldForcePointBuy(__instance))
            {
                return;
            }

            SetPointBuyMethod(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageAbilityScoresPanel __instance)
        {
            RefreshAbilityScoreMethodUiState(__instance);
        }
    }

    //PATCH: extends the cost buy table to enable `EpicPointsAndArray`
    [HarmonyPatch(typeof(CharacterStageAbilityScoresPanel), nameof(CharacterStageAbilityScoresPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            if (!Main.Settings.EnableEpicPointsAndArray)
            {
                return instructions;
            }

            return instructions
                .ReplaceCode(instruction => instruction.opcode == OpCodes.Ldc_R4 &&
                                            instruction.operand.ToString() ==
                                            RulesContext.GameBuyPoints.ToString(),
                    -1, "CharacterStageAbilityScoresPanel.Refresh.1",
                    new CodeInstruction(OpCodes.Ldc_R4, 1f * RulesContext.ModBuyPoints))
                .ReplaceCode(instruction => instruction.opcode == OpCodes.Ldc_I4_S &&
                                            instruction.operand.ToString() ==
                                            RulesContext.GameBuyPoints.ToString(),
                    -1, "CharacterStageAbilityScoresPanel.Refresh.2",
                    new CodeInstruction(OpCodes.Ldc_I4_S, RulesContext.ModBuyPoints))
                .ReplaceCode(instruction => instruction.opcode == OpCodes.Ldc_I4_S &&
                                            instruction.operand.ToString() ==
                                            RulesContext.GameMaxAttribute.ToString(),
                    -1, "CharacterStageAbilityScoresPanel.Refresh.3",
                    new CodeInstruction(OpCodes.Ldc_I4_S, RulesContext.ModMaxAttribute));
        }

        [UsedImplicitly]
        public static void Prefix(CharacterStageAbilityScoresPanel __instance)
        {
            if (!ShouldForcePointBuy(__instance))
            {
                return;
            }

            SetPointBuyMethod(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageAbilityScoresPanel __instance)
        {
            RefreshAbilityScoreMethodUiState(__instance);
        }
    }

    [HarmonyPatch(
        typeof(CharacterStageAbilityScoresPanel),
        nameof(CharacterStageAbilityScoresPanel.OnMethodToggleChangedCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnMethodToggleChangedCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterStageAbilityScoresPanel __instance, bool on)
        {
            if (!ShouldForcePointBuy(__instance))
            {
                if (!on)
                {
                    EnsureDiceRollValues(__instance);
                }

                return true;
            }

            SetPointBuyMethod(__instance);
            __instance.Reset();
            __instance.RefreshNow();

            if (Gui.GamepadActive)
            {
                Gui.InputService?.ClearCurrentSelectable();
                __instance.SelectDefaultControl();
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(CharacterStageAbilityScoresPanel),
        nameof(CharacterStageAbilityScoresPanel.EnumerateValidationGroups))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumerateValidationGroups_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageAbilityScoresPanel __instance)
        {
            RemoveForcedValidationGroups(__instance);
        }
    }
}
