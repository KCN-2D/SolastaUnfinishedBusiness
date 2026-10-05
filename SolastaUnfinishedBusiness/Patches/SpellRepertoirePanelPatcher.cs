using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using static SolastaUnfinishedBusiness.Models.Level20Context;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellRepertoirePanelPatcher
{
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SpellRepertoirePanel __instance)
        {
            MulticlassGameUi.RestoreSpellPreparationLayout(__instance);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.ReplaceCalls(
                AccessTools.PropertyGetter(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.MaxSpellLevelOfSpellCastingLevel)),
                "SpellRepertoirePanel.Bind.InspectionSpellLevel",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_S, 4),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CharacterInspectionScreenEnhancement),
                    nameof(CharacterInspectionScreenEnhancement.GetInspectionSpellLevelMaximum))));
        }

        [UsedImplicitly]
        public static void Postfix(SpellRepertoirePanel __instance)
        {
            //PATCH: filters how spells and slots are displayed on inspection (MULTICLASS)
            MulticlassGameUi.RebuildSlotsTable(__instance);

            RefreshSpellcastingLabels(__instance);
            RefreshPreparedSpellsLabel(__instance);

            var sources = CharacterInspectionScreenEnhancement.GetInspectionSpellRepertoires(
                __instance.GuiCharacter.RulesetCharacter, __instance.SpellRepertoire, __instance.BindMode);
            if (sources.Length > 1 && __instance.knownSpellsBox.gameObject.activeSelf)
            {
                __instance.knownSpellsLabel.Text = sources.SelectMany(CharacterInspectionScreenEnhancement.GetInspectionLearnedSpells)
                    .Where(spell => spell.SpellLevel > 0).Distinct().Count().ToString();
            }

            //PATCH: displays sorcery point box for sorcerers only
            if (!Main.Settings.EnableDisplaySorceryPointBoxSorcererOnly)
            {
                return;
            }

            if (__instance.SpellRepertoire.SpellCastingClass != DatabaseHelper.CharacterClassDefinitions.Sorcerer)
            {
                __instance.sorceryPointsBox.gameObject.SetActive(false);
            }
        }
    }

    private static void RefreshSpellcastingLabels(SpellRepertoirePanel panel)
    {
        var repertoire = panel.SpellRepertoire;

        if (repertoire == null)
        {
            return;
        }

        // Inspection can present several grants from one feat; each spell retains its own casting source.
        var sources = CharacterInspectionScreenEnhancement.GetInspectionSpellRepertoires(
            panel.GuiCharacter.RulesetCharacter, repertoire, panel.BindMode);
        var attributes = DatabaseRepository.GetDatabase<SmartAttributeDefinition>();
        SetLabelText(panel.abilityLabel, string.Join(" / ", sources.Select(source =>
            attributes.GetElement(source.SpellCastingAbility).FormatTitle()).Distinct()));
        SetLabelText(panel.saveDCLabel, string.Join(" / ", sources.Select(source => source.SaveDC.ToString()).Distinct()));
        SetLabelText(panel.spellAttackBonusLabel, string.Join(" / ", sources.Select(source =>
            source.SpellAttackBonus.ToString("+0;-#")).Distinct()));
    }

    private static void SetLabelText(GuiLabel label, string text)
    {
        if (!label || label.Text == text)
        {
            return;
        }

        label.Text = text;
    }

    private static void RefreshPreparedSpellsLabel(SpellRepertoirePanel panel)
    {
        var repertoire = panel.SpellRepertoire;

        if (repertoire == null ||
            repertoire.SpellCastingFeature.SpellReadyness != RuleDefinitions.SpellReadyness.Prepared ||
            !panel.preparedSpellsBox.gameObject.activeSelf)
        {
            return;
        }

        panel.preparedSpellsLabel.Text = Gui.FormatCurrentOverMax(
            CountManualPreparedSpells(repertoire, repertoire.PreparedSpells),
            repertoire.MaxPreparedSpell,
            0,
            null);
    }

    private static int CountManualPreparedSpells(
        RulesetSpellRepertoire repertoire,
        List<SpellDefinition> preparedSpells)
    {
        if (preparedSpells == null || preparedSpells.Count == 0)
        {
            return 0;
        }

        var autoPreparedSpells = repertoire?.AutoPreparedSpells;

        if (autoPreparedSpells == null || autoPreparedSpells.Count == 0)
        {
            return preparedSpells.Count;
        }

        var count = 0;

        foreach (var spell in preparedSpells)
        {
            if (spell != null && !autoPreparedSpells.Contains(spell))
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<CodeInstruction> ReplacePreparedSpellCount(
        IEnumerable<CodeInstruction> instructions,
        string patchContext)
    {
        var code = new List<CodeInstruction>(instructions);
        var preparedSpellsField = AccessTools.Field(
            typeof(SpellRepertoirePanel),
            nameof(SpellRepertoirePanel.preparedSpells));
        var countMethod = typeof(List<SpellDefinition>).GetProperty(nameof(List<SpellDefinition>.Count))!
            .GetGetMethod();
        var getSpellRepertoireMethod =
            AccessTools.PropertyGetter(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.SpellRepertoire));
        var getAutoPreparedSpellsMethod =
            AccessTools.PropertyGetter(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.AutoPreparedSpells));
        var countManualPreparedSpellsMethod =
            new Func<RulesetSpellRepertoire, List<SpellDefinition>, int>(CountManualPreparedSpells).Method;

        for (var index = 0; index <= code.Count - 8; index++)
        {
            if (code[index].opcode != OpCodes.Ldarg_0 ||
                !code[index + 1].LoadsField(preparedSpellsField) ||
                !code[index + 2].Calls(countMethod) ||
                code[index + 3].opcode != OpCodes.Ldarg_0 ||
                !code[index + 4].Calls(getSpellRepertoireMethod) ||
                !code[index + 5].Calls(getAutoPreparedSpellsMethod) ||
                !code[index + 6].Calls(countMethod) ||
                code[index + 7].opcode != OpCodes.Sub)
            {
                continue;
            }

            var replacement = new List<CodeInstruction>
            {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Call, getSpellRepertoireMethod),
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldfld, preparedSpellsField),
                new(OpCodes.Call, countManualPreparedSpellsMethod)
            };

            replacement[0].labels.AddRange(code[index].labels);
            replacement[0].blocks.AddRange(code[index].blocks);

            code.RemoveRange(index, 8);
            code.InsertRange(index, replacement);

            return code;
        }

        Main.Error($"Failed to apply transpiler patch [{patchContext}]!");

        return code;
    }

    private static bool CanValidateWizardSpellSelection(SpellRepertoirePanel panel)
    {
        var selection = GetWizardExtraSpellSelection(panel.GuiCharacter.RulesetCharacter, panel.SpellRepertoire);

        return selection == null ||
               selection.IsValidSelection(GetWizardSelectedSpells(panel.SpellRepertoire, panel.preparedSpells));
    }

    //PATCH: Apply the same spell-level limits to mouse and gamepad selection.
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnSpellSelectedForPreparation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnSpellSelectedForPreparation_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance, SpellBox spellBox)
        {
            var rulesetCharacter = __instance.GuiCharacter.RulesetCharacter;
            var spellRepertoire = __instance.SpellRepertoire;
            var spellDefinition = spellBox.SpellDefinition;

            var selection = GetWizardExtraSpellSelection(rulesetCharacter, spellRepertoire);

            return !Tabletop2024Context.IsInvalidMemorizeSelectedSpell(__instance, rulesetCharacter, spellDefinition) &&
                   (selection == null ||
                    selection.CanSelectSpell(GetWizardSelectedSpells(spellRepertoire, __instance.preparedSpells),
                        spellDefinition));
        }
    }

    //PATCH: Supports Wizard Mastery and Signature spell features
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.RefreshPreparation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshPreparation_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var refreshInteractivePreparationMethod =
                typeof(SpellsByLevelGroup).GetMethod("RefreshInteractivePreparation");
            var myRefreshInteractivePreparationMethod =
                new Action<SpellsByLevelGroup, bool, bool, List<SpellDefinition>, SpellRepertoirePanel>(
                    RefreshInteractivePreparation).Method;

            return ReplacePreparedSpellCount(
                    instructions,
                    "SpellRepertoirePanel.RefreshPreparation.PreparedSpellCount")
                .ReplaceCalls(
                    refreshInteractivePreparationMethod,
                    "SpellRepertoirePanel.RefreshPreparation",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, myRefreshInteractivePreparationMethod));
        }

        [UsedImplicitly]
        public static void Postfix(SpellRepertoirePanel __instance)
        {
            var character = __instance.GuiCharacter.RulesetCharacter;
            var repertoire = __instance.SpellRepertoire;
            var selection = GetWizardExtraSpellSelection(character, repertoire);
            var isMemorizeSpell = Tabletop2024Context.IsMemorizeSpellPreparation(character, repertoire);

            if (isMemorizeSpell)
            {
                RepaintPanel(
                    __instance, Tabletop2024Context.FeatureMemorizeSpell.FormatTitle(),
                    false, false, false,
                    Gui.Localize("Screen/&PreparePanelMemorizeSpellSelect"));
            }
            else if (WizardSpellMastery.IsPreparation(character, repertoire, out _))
            {
                RepaintPanel(
                    __instance, WizardSpellMastery.FeatureSpellMastery.FormatTitle(),
                    true, false, true,
                    Gui.Localize(Main.Settings.EnableWizardSpellMastery2024
                        ? "Screen/&PreparePanelSpellMastery2024Select"
                        : "Screen/&PreparePanelSpellMasterySelect"));
            }
            else if (WizardSignatureSpells.IsPreparation(character, repertoire, out _))
            {
                RepaintPanel(
                    __instance, WizardSignatureSpells.PowerSignatureSpells.FormatTitle(),
                    Main.Settings.EnableSignatureSpellsRelearn, false, true,
                    Gui.Localize("Screen/&PreparePanelSignatureSpellsSelect"));
            }
            else
            {
                RepaintPanel(__instance, Gui.Localize("Screen/&PreparePanelTitle"), true, true, true);
            }

            if (selection != null)
            {
                __instance.validatePreparationButton.interactable = CanValidateWizardSpellSelection(__instance);
            }

            // Native navigation was computed before these feature-specific controls were updated.
            if (Gui.GamepadActive && (selection != null || isMemorizeSpell))
            {
                Gui.InputService.RecomputeSelectableNavigation(false, false, false);
                __instance.SelectDefaultControl();
            }
        }

        private static void RepaintPanel(
            SpellRepertoirePanel __instance,
            string title,
            bool showDesc, bool showAutoButton, bool showClearRevertButtons, string byPassInstruction = null)
        {
            MulticlassGameUi.CaptureSpellPreparationLayout(__instance);
            var preparationPanelTransform = __instance.PreparationPanel.transform;
            var titleTransform = preparationPanelTransform.FindChildRecursive("Title");
            var descriptionTransform = preparationPanelTransform.FindChildRecursive("Description");
            var automateButtonTransform = preparationPanelTransform.FindChildRecursive("AutomateButton");
            var clearButtonTransform = preparationPanelTransform.FindChildRecursive("ClearButton");
            var revertButtonTransform = preparationPanelTransform.FindChildRecursive("RevertButton");
            var instructionTransform = preparationPanelTransform.FindChildRecursive("Instruction");

            titleTransform!.GetComponentInChildren<TextMeshProUGUI>().text = title;

            descriptionTransform!.gameObject.SetActive(showDesc);

            var gamepadActive = Gui.GamepadActive;

            automateButtonTransform!.gameObject.SetActive(showAutoButton && !gamepadActive);
            __instance.automatePreparationButtonGamepad.gameObject.SetActive(showAutoButton && gamepadActive);
            clearButtonTransform!.gameObject.SetActive(showClearRevertButtons && !gamepadActive);
            revertButtonTransform!.gameObject.SetActive(showClearRevertButtons && !gamepadActive);
            __instance.clearPreparationButtonGamepad.gameObject.SetActive(showClearRevertButtons && gamepadActive);

            if (byPassInstruction != null)
            {
                instructionTransform!.GetComponentInChildren<TextMeshProUGUI>().text = byPassInstruction;
            }

            MulticlassGameUi.RefreshSpellPreparationLayout(__instance);
        }

        private static void RefreshInteractivePreparation(
            SpellsByLevelGroup spellsByLevelGroup,
            bool canSelectSpells,
            bool maxReached,
            List<SpellDefinition> preparedSpells,
            SpellRepertoirePanel spellRepertoirePanel)
        {
            var selection = GetWizardExtraSpellSelection(
                spellRepertoirePanel.GuiCharacter.RulesetCharacter, spellRepertoirePanel.SpellRepertoire);

            if (selection != null)
            {
                var selectedSpells = GetWizardSelectedSpells(spellRepertoirePanel.SpellRepertoire, preparedSpells);
                var spellLevel = spellsByLevelGroup.SpellLevel;

                canSelectSpells = selection.AllowsLevel(spellLevel) ||
                                  selectedSpells.Any(spell => spell != null && spell.SpellLevel == spellLevel);
                maxReached = selectedSpells.Length >= selection.SpellCount ||
                             selection.IsLevelFull(selectedSpells, spellLevel);
            }

            spellsByLevelGroup.RefreshInteractivePreparation(canSelectSpells, maxReached, preparedSpells);

            if (selection == null)
            {
                return;
            }

            var selected = GetWizardSelectedSpells(spellRepertoirePanel.SpellRepertoire, preparedSpells);

            foreach (var spellBox in spellsByLevelGroup.spellsTable.GetComponentsInChildren<SpellBox>())
            {
                if (!selection.CanSelectSpell(selected, spellBox.SpellDefinition))
                {
                    spellBox.RefreshPreparation(false, preparedSpells.Contains(spellBox.SpellDefinition), false);
                }
            }
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SpellRepertoirePanel __instance)
        {
            MulticlassGameUi.RestoreSpellPreparationLayout(__instance);
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnValidatePreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnValidatePreparationCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            return CanValidateWizardSpellSelection(__instance);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            return ReplacePreparedSpellCount(
                instructions,
                "SpellRepertoirePanel.OnValidatePreparationCb.PreparedSpellCount");
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.DoValidate))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DoValidate_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            return CanValidateWizardSpellSelection(__instance);
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnAutomatePreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnAutomatePreparationCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            var character = __instance.GuiCharacter.RulesetCharacter;
            var repertoire = __instance.SpellRepertoire;

            return GetWizardExtraSpellSelection(character, repertoire) == null &&
                   !Tabletop2024Context.IsMemorizeSpellPreparation(character, repertoire);
        }
    }
}
