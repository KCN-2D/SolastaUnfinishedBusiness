using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        public static void Postfix(SpellRepertoirePanel __instance)
        {
            //PATCH: filters how spells and slots are displayed on inspection (MULTICLASS)
            MulticlassGameUi.RebuildSlotsTable(__instance);

            RefreshMagicInitiate2024SpellcastingLabels(__instance);
            RefreshPreparedSpellsLabel(__instance);

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

    private static void RefreshMagicInitiate2024SpellcastingLabels(SpellRepertoirePanel panel)
    {
        var repertoire = panel.SpellRepertoire;

        if (Tabletop2024Context.TryGetMagicInitiate2024SpellcastingAbilityLabel(
                repertoire,
                out var abilityLabel))
        {
            SetLabelText(panel.abilityLabel, abilityLabel);
        }

        if (Tabletop2024Context.TryGetMagicInitiate2024SaveDC(repertoire, out var saveDC))
        {
            SetLabelText(panel.saveDCLabel, saveDC.ToString());
        }

        if (Tabletop2024Context.TryGetMagicInitiate2024SpellAttackBonus(
                repertoire,
                out var spellAttackBonus))
        {
            SetLabelText(panel.spellAttackBonusLabel, spellAttackBonus.ToString("+0;-#"));
        }
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

    //PATCH: Supports Wizard Mastery and Signature spell features
    //UI allows other spells to be selected so easier to prevent it here
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

            return !Tabletop2024Context.IsInvalidMemorizeSelectedSpell(__instance, rulesetCharacter, spellDefinition) &&
                   !WizardSpellMastery.IsInvalidSelectedSpell(rulesetCharacter, spellRepertoire, spellDefinition) &&
                   !WizardSignatureSpells.IsInvalidSelectedSpell(rulesetCharacter, spellRepertoire, spellDefinition);
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

        private static void RepaintPanel(
            SpellRepertoirePanel __instance,
            string title,
            bool showDesc, bool showAutoButton, bool showClearRevertButtons, string byPassInstruction = null)
        {
            var preparationPanelTransform = __instance.PreparationPanel.transform;
            var titleTransform = preparationPanelTransform.FindChildRecursive("Title");
            var descriptionTransform = preparationPanelTransform.FindChildRecursive("Description");
            var automateButtonTransform = preparationPanelTransform.FindChildRecursive("AutomateButton");
            var clearButtonTransform = preparationPanelTransform.FindChildRecursive("ClearButton");
            var revertButtonTransform = preparationPanelTransform.FindChildRecursive("RevertButton");
            var instructionTransform = preparationPanelTransform.FindChildRecursive("Instruction");

            titleTransform!.GetComponentInChildren<TextMeshProUGUI>().text = title;

            descriptionTransform!.gameObject.SetActive(showDesc);

            // not the best solution but this object is getting re-activated somewhere else so moving off-screen
            automateButtonTransform!.localPosition = showAutoButton
                ? new Vector3(-12.5f, -61)
                : new Vector3(-1000, -1000);

            clearButtonTransform!.gameObject.SetActive(showClearRevertButtons);
            revertButtonTransform!.gameObject.SetActive(showClearRevertButtons);

            if (byPassInstruction != null)
            {
                instructionTransform!.GetComponentInChildren<TextMeshProUGUI>().text = byPassInstruction;
            }
        }

        private static void RefreshInteractivePreparation(
            SpellsByLevelGroup spellsByLevelGroup,
            bool canSelectSpells,
            bool maxReached,
            List<SpellDefinition> preparedSpells,
            SpellRepertoirePanel spellRepertoirePanel)
        {
            var rulesetCharacter = spellRepertoirePanel.GuiCharacter.RulesetCharacter;
            var spellRepertoire = spellRepertoirePanel.SpellRepertoire;

            if (Tabletop2024Context.IsMemorizeSpellPreparation(rulesetCharacter, spellRepertoire))
            {
                RepaintPanel(
                    spellRepertoirePanel, Tabletop2024Context.FeatureMemorizeSpell.FormatTitle(),
                    false, false, false,
                    Gui.Localize("Screen/&PreparePanelMemorizeSpellSelect"));
            }
            else if (WizardSpellMastery.IsPreparation(rulesetCharacter, spellRepertoire, out _))
            {
                RepaintPanel(
                    spellRepertoirePanel, WizardSpellMastery.FeatureSpellMastery.FormatTitle(),
                    true, false, true);

                canSelectSpells = spellsByLevelGroup.SpellLevel is 1 or 2;
            }
            else if (WizardSignatureSpells.IsPreparation(rulesetCharacter, spellRepertoire, out _))
            {
                RepaintPanel(
                    spellRepertoirePanel, WizardSignatureSpells.PowerSignatureSpells.FormatTitle(),
                    Main.Settings.EnableSignatureSpellsRelearn, false, true);

                canSelectSpells = spellsByLevelGroup.SpellLevel is 3;
            }
            else
            {
                RepaintPanel(
                    spellRepertoirePanel,
                    Gui.Localize("Screen/&PreparePanelTitle"),
                    true, true, true);
            }

            spellsByLevelGroup.RefreshInteractivePreparation(canSelectSpells, maxReached, preparedSpells);
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnValidatePreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnValidatePreparationCb_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            return ReplacePreparedSpellCount(
                instructions,
                "SpellRepertoirePanel.OnValidatePreparationCb.PreparedSpellCount");
        }
    }
}
