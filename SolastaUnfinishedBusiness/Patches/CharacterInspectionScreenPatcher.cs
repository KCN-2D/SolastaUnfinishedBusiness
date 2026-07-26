using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterInspectionScreenPatcher
{
    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterInspectionScreen __instance,
            RulesetCharacterHero heroCharacter,
            ActionDefinitions.InventoryManagementMode __1,
            InventoryPanel.ItemSelectedHandler itemSelected,
            out bool __state)
        {
            __state = SimulacrumEquipmentPanel.TryBind(
                __instance,
                heroCharacter,
                __1,
                itemSelected);

            //PATCH: enable custom models renderer
            CustomModels.SwitchRenderer(true);

            //PATCH: sets the inspection context for MC heroes
            Global.InspectedHero = __state ? null : heroCharacter;
            CharacterInspectionScreenEnhancement.ResetInspectionState();

            //PATCH: gets more real state for the toggles on top (MULTICLASS)
            var transform = __instance.toggleGroup.transform;

            transform.position =
                new Vector3(__instance.characterPlate.transform.position.x / 2f, transform.position.y, 0);

            return !__state;
        }

        [UsedImplicitly]
        public static void Postfix(
            CharacterInspectionScreen __instance,
            RulesetCharacterHero heroCharacter,
            bool __state)
        {
            if (__state)
            {
                return;
            }

            // A Simulacrum inventory session temporarily rebinds these pooled boxes to
            // non-Hero attributes. CharacterInspectionScreen does not reliably rebind
            // the listing on every subsequent Hero inspection, so restore the actual
            // inspected Hero explicitly instead of leaving an empty pooled panel.
            CharacterStatsPanelPatcher.BindAbilityScores(
                __instance.abilityScoresListingPanel,
                heroCharacter);

            //PATCH: support display max spell points on inspection screen (SPELL_POINTS)
            SpellPointsContext.DisplayMaxSpellPointsOnInspectionScreen(__instance, heroCharacter);

            //PATCH: hide repertoires that have hidden spell casting feature
            for (var index = __instance.staticTogglesNumber; index < __instance.toggleGroup.transform.childCount; ++index)
            {
                var child = __instance.toggleGroup.transform.GetChild(index);
                var repertoireIndex = index - __instance.staticTogglesNumber;

                if (repertoireIndex < 0 || repertoireIndex >= heroCharacter.SpellRepertoires.Count)
                {
                    continue;
                }

                var repertoire = heroCharacter.SpellRepertoires[repertoireIndex];

                if (repertoire.SpellCastingFeature.GuiPresentation.Hidden)
                {
                    child.gameObject.SetActive(false);
                }
            }

        }

        [UsedImplicitly]
        public static System.Exception Finalizer(
            CharacterInspectionScreen __instance,
            System.Exception __exception)
        {
            if (__exception != null)
            {
                SimulacrumEquipmentPanel.HandleBindFailure(__instance);
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            //PATCH: disable custom models renderer
            CustomModels.SwitchRenderer(false);

            //PATCH: resets the inspection context for MC heroes
            Global.InspectedHero = null;
            CharacterInspectionScreenEnhancement.ResetInspectionState();

            return !SimulacrumEquipmentPanel.TryUnbind(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterInspectionScreen __instance)
        {
            SimulacrumEquipmentPanel.AfterBeginShow(__instance);

            if (!SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _) &&
                __instance.InspectedCharacter?.RulesetCharacter is
                    RulesetCharacterHero character)
            {
                SimulacrumDiagnostics.RecordInspectionPanels(
                    character,
                    "hero-begin-show-complete",
                    __instance);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            return !SimulacrumEquipmentPanel.TryRefresh(__instance);
        }
    }

    [HarmonyPatch(
        typeof(CharacterInspectionScreen),
        nameof(CharacterInspectionScreen.FilterInspectionPanels))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FilterInspectionPanels_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            if (!SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _))
            {
                return true;
            }

            // Bind already prepared the single supported panel. AfterBeginShow performs the first
            // native Show so the panel cannot skip its initialization merely because it was made
            // active while the parent screen was still entering OnBeginShow.
            return false;
        }
    }

    [HarmonyPatch(
        typeof(CharacterInspectionScreen),
        nameof(CharacterInspectionScreen.RefreshCharactersTable))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshCharactersTable_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            if (!SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _))
            {
                return true;
            }

            __instance.characterPlatesTable.gameObject.SetActive(false);

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.InventoryDragStarted))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InventoryDragStarted_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            return HandleSimulacrumInventoryDrag(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.InventoryDragStopped))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InventoryDragStopped_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            return HandleSimulacrumInventoryDrag(__instance);
        }
    }

    //PATCH: resets the inspection context for MC heroes otherwise we get class name bleeding on char pool
    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.DoClose))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DoClose_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterInspectionScreen __instance)
        {
            Global.InspectedHero = null;
            CharacterInspectionScreenEnhancement.ResetInspectionState();
        }
    }

    //PATCH: modify caption if unlimited inventory actions is enabled
    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.RefreshCaption))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshCaption_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            if (SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _))
            {
                __instance.screenCaption.gameObject.SetActive(true);
                __instance.screenCaption.Text =
                    Gui.Localize("Screen/&SimulacrumEquipmentTitle");
                __instance.screenCaption.TMP_Text.color =
                    __instance.inventoryActionAvailableColor;

                return false;
            }

            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch (__instance.inventoryManagementMode)
            {
                case ActionDefinitions.InventoryManagementMode.Battle:
                    __instance.screenCaption.gameObject.SetActive(true);
                    switch (Main.Settings.EnableUnlimitedInventoryActions)
                    {
                        case false when
                            __instance.InspectedCharacter.GameLocationCharacter.GetActionTypeStatus(ActionDefinitions
                                .ActionType
                                .FreeOnce) == ActionDefinitions.ActionStatus.Available:
                            __instance.screenCaption.Text =
                                Gui.Localize("Screen/&CharacterInspectionModeBattleAvailableTitle");
                            __instance.screenCaption.TMP_Text.color = __instance.inventoryActionAvailableColor;
                            return false; // Skip the original method
                        case true when
                            __instance.InspectedCharacter.GameLocationCharacter.GetActionTypeStatus(ActionDefinitions
                                .ActionType
                                .FreeOnce) == ActionDefinitions.ActionStatus.Available:
                            __instance.screenCaption.Text =
                                Gui.Localize("Screen/&CharacterInspectionModeBattleUnlimitedTitle");
                            __instance.screenCaption.TMP_Text.color = __instance.inventoryActionAvailableColor;
                            return false; // Skip the original method
                    }

                    __instance.screenCaption.Text = Gui.Localize("Screen/&CharacterInspectionModeBattleSpentTitle");
                    __instance.screenCaption.TMP_Text.color = __instance.inventoryActionSpentColor;
                    return false; // Skip the original method
                case ActionDefinitions.InventoryManagementMode.SelectItem:
                    __instance.screenCaption.gameObject.SetActive(true);
                    __instance.screenCaption.Text = __instance.itemSelectionType switch
                    {
                        ActionDefinitions.ItemSelectionType.Equiped
                            or ActionDefinitions.ItemSelectionType.EquippedNoLightSource => Gui.Localize(
                                "Screen/&CharacterInspectionModeSelectEquipedItemTitle"),
                        ActionDefinitions.ItemSelectionType.Carried => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectCarriedItemTitle"),
                        ActionDefinitions.ItemSelectionType.MagicalUnidentified => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectMagicalUnidentifiedItemTitle"),
                        ActionDefinitions.ItemSelectionType.Weapon => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectWeaponTitle"),
                        ActionDefinitions.ItemSelectionType.WeaponNonMagical => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectWeaponNonMagicalTitle"),
                        ActionDefinitions.ItemSelectionType.WieldedClubOrQuarterstaff => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectWieldedClubOrQuarterstaffTitle"),
                        ActionDefinitions.ItemSelectionType.Spellbook => Gui.Format(
                            "Screen/&CharacterInspectionModeSelectSpellbookTitle",
                            __instance.spellToScribe.SpellLevel.ToString()),
                        _ => __instance.screenCaption.Text
                    };

                    __instance.screenCaption.TMP_Text.color = __instance.inventoryActionAvailableColor;
                    return false; // Skip the original method
                default:
                    __instance.screenCaption.gameObject.SetActive(false);
                    return false; // Skip the original method
            }
        }
    }

    private static bool HandleSimulacrumInventoryDrag(CharacterInspectionScreen screen)
    {
        if (!SimulacrumEquipmentPanel.TryGetActiveCharacter(screen, out _))
        {
            return true;
        }

        ServiceRepository.GetService<ICommandService>()
            .AcknowledgePreviousCommandLocally(screen.OnInventoryDragCommandDone);

        return false;
    }
}
