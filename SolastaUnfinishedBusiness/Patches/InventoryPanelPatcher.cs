using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class InventoryPanelPatcher
{
    private static readonly ConditionalWeakTable<EncumbrancePanel, RulesetCharacterSimulacrum>
        SimulacrumEncumbranceSubjects = new();
    private static readonly ConditionalWeakTable<InventoryPanel, SimulacrumLootControlsState>
        SimulacrumLootControls = new();

    //PATCH: Enable Inventory Filtering and Sorting
    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix(InventoryPanel __instance)
        {
            InventoryManagementContext.RefreshControlsVisibility();

            if (InventoryManagementContext.Enabled && __instance.MainContainerPanel)
            {
                InventoryManagementContext.Refresh(__instance.MainContainerPanel, true);
            }
        }
    }

    //PATCH: Enable Inventory Filtering and Sorting
    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            InventoryPanel __instance,
            GuiCharacter __0,
            InventoryManagementMode __1,
            RulesetContainer __2,
            InventoryPanel.ItemSelectedHandler __3,
            RectTransform __4)
        {
            var encumbrancePanel = __instance.encumbrancePanel;
            var treasuryPanel = __instance.treasuryPanel;

            SimulacrumEncumbranceSubjects.Remove(encumbrancePanel);

            if (__0?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumEncumbranceSubjects.Add(encumbrancePanel, duplicate);
                if (__2 == null)
                {
                    HideLootControls(__instance);
                }
                else
                {
                    RestoreLootControls(__instance);
                }

                BindSimulacrum(__instance, __0, duplicate, __1, __2, __3, __4);

                return false;
            }

            RestoreLootControls(__instance);
            treasuryPanel.gameObject.SetActive(true);

            // NOTE: don't use MainContainerPanel?. which bypasses Unity object lifetime check
            if (InventoryManagementContext.Enabled && __instance.MainContainerPanel)
            {
                InventoryManagementContext.BindInventory(__instance.MainContainerPanel);
            }

            return true;
        }

        [UsedImplicitly]
        public static void Postfix(InventoryPanel __instance)
        {
            var encumbrancePanel = __instance.encumbrancePanel;

            if (__instance.GuiCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                encumbrancePanel.RefreshNow();
            }
        }

        private static void BindSimulacrum(
            InventoryPanel panel,
            GuiCharacter guiCharacter,
            RulesetCharacterSimulacrum duplicate,
            InventoryManagementMode mode,
            RulesetContainer externalContainer,
            InventoryPanel.ItemSelectedHandler itemSelected,
            RectTransform characterPlatesTable)
        {
            panel.GuiCharacter = guiCharacter;
            RulesetCharacter.ItemDroppedOnGround += panel.OnItemDroppedOnGround;
            duplicate.CharacterRefreshed += panel.CharacterRefreshed;
            RulesetInventorySlot.ItemLostAttunement += panel.ItemLostAttunement;
            duplicate.CharacterInventory.ItemEquiped += panel.ItemEquiped;
            duplicate.CharacterInventory.ItemUnequiped += panel.ItemUnequiped;

            var playerController = ServiceRepository
                .GetService<IPlayerControllerService>()
                ?.ActivePlayerController;
            var locationCharacter = guiCharacter.GameLocationCharacter;
            var controlsDuplicate = locationCharacter != null
                ? playerController?.IsCharacterControlled(locationCharacter) ?? true
                : playerController?.IsCharacterControlled(duplicate) ?? true;
            var controlsOwner = SimulacrumBehavior.TryGetOwner(duplicate, out var owner) &&
                                (GameLocationCharacter.GetFromActor(owner) is { } ownerLocation
                                    ? playerController?.IsCharacterControlled(ownerLocation) ?? true
                                    : playerController?.IsCharacterControlled(owner) ?? true);

            panel.canManipulateInventory = playerController == null ||
                                           controlsDuplicate ||
                                           controlsOwner;
            duplicate.ComputeEncumbranceThresholds(
                out var lightThreshold,
                out var heavyThreshold,
                out var maximumWeight);
            SimulacrumDiagnostics.Write(
                "inventory",
                $"stage=authority guid={duplicate.Guid} canManipulate={panel.canManipulateInventory} " +
                $"controlsDuplicate={controlsDuplicate} controlsOwner={controlsOwner} " +
                $"carry={lightThreshold:0.###}/{heavyThreshold:0.###}/{maximumWeight:0.###}");

            if (panel.canManipulateInventory && Gui.GamepadActive &&
                mode == InventoryManagementMode.Merchant)
            {
                panel.canManipulateInventory = false;
            }

            if (ServiceRepository.GetService<IGameLocationItemService>() is { } itemService)
            {
                itemService.ItemCreated += panel.ItemCreated;
                itemService.ItemDestroyed += panel.ItemDestroyed;
            }

            panel.audioService = ServiceRepository.GetService<IAudioService>();
            SimulacrumDiagnostics.RecordInventory(duplicate, "before-character-viewport-bind");
            panel.characterViewport.Bind(
                GraphicsCharacterDefinitions.CharacterType.Inventory,
                duplicate,
                false);
            SimulacrumDiagnostics.RecordInventory(duplicate, "character-viewport-bound");
            Gui.GuiService.CharacterViewportBounding += panel.CharacterViewportBounding;
            Gui.GuiService.CharacterViewportUnbound += panel.CharacterViewportUnbound;
            BindSimulacrumTreasury(panel, duplicate);
            panel.encumbrancePanel.gameObject.SetActive(true);
            panel.encumbrancePanel.RefreshNow();

            if (panel.reorderPersonalContainerButton)
            {
                panel.reorderPersonalContainerButton.gameObject.SetActive(
                    panel.canManipulateInventory);
            }

            panel.ItemSelectionType = ItemSelectionType.Equiped;
            panel.InventoryManagementMode = mode;
            panel.inventoryShortcutsPanel.ItemSelectionInProgress =
                mode == InventoryManagementMode.SelectItem;
            panel.ItemSelected = itemSelected;
            SimulacrumDiagnostics.RecordInventory(duplicate, "before-equipment-layout-bind");
            panel.equipmentLayoutPanel.Bind(guiCharacter);
            SimulacrumDiagnostics.RecordInventory(duplicate, "equipment-layout-bound");
            SimulacrumDiagnostics.RecordInventory(duplicate, "before-shortcuts-bind");
            panel.inventoryShortcutsPanel.Bind(guiCharacter, false, null);
            SimulacrumDiagnostics.RecordInventory(duplicate, "shortcuts-bound");
            panel.externalContainer = externalContainer;
            InventoryPanelPatcher.RefreshSimulacrumPersonalContainer(
                panel,
                duplicate,
                "initial-bind");

            if (panel.externalContainerPanel && externalContainer != null)
            {
                SimulacrumDiagnostics.RecordInventory(
                    duplicate,
                    "before-external-container-bind");
                panel.externalContainerPanel.Bind(
                    externalContainer,
                    guiCharacter,
                    panel.DropAreaClicked,
                    panel.VisibleSlotsRefreshed);
                panel.externalContainerPanel.Show(false);

                if (externalContainer.Name == "Ground")
                {
                    panel.externalContainerPanel.containerLabel.Text =
                        Gui.Localize("Equipment/&ContainerGroundTitle");
                }

                SimulacrumEquipmentPanel.SetExternalContainer(duplicate, externalContainer);
                SimulacrumDiagnostics.RecordInventory(
                    duplicate,
                    "external-container-bound");
            }
            else if (panel.externalContainerPanel)
            {
                panel.externalContainerPanel.Unbind();
                panel.externalContainerPanel.Hide(true);
            }

            panel.characterPlatesTable = characterPlatesTable;
            SimulacrumDiagnostics.RecordInventory(duplicate, "before-stop-drag");
            panel.StopDrag(false);
            SimulacrumDiagnostics.RecordInventory(duplicate, "stop-drag-complete");
            Gui.GuiService.ResetOverlayCanvasSortingOrder();
            Gui.TooltipService.HideTooltip();

            if (InventoryManagementContext.Enabled && panel.MainContainerPanel)
            {
                SimulacrumDiagnostics.RecordInventory(
                    duplicate,
                    "before-management-context-bind");
                InventoryManagementContext.BindInventory(panel.MainContainerPanel);
                SimulacrumDiagnostics.RecordInventory(
                    duplicate,
                    "management-context-bound");
            }
        }

        private static void BindSimulacrumTreasury(
            InventoryPanel panel,
            RulesetCharacterSimulacrum duplicate)
        {
            var treasuryPanel = panel.treasuryPanel;

            treasuryPanel.Unbind();

            if (!SimulacrumBehavior.TryGetOwner(duplicate, out var ownerHero))
            {
                treasuryPanel.gameObject.SetActive(false);
                SimulacrumDiagnostics.RecordInventory(
                    duplicate,
                    "treasury-owner-unavailable",
                    panel.ParentScreen as CharacterInspectionScreen);

                return;
            }

            treasuryPanel.gameObject.SetActive(true);
            treasuryPanel.Bind(new GuiCharacter(ownerHero));
            SimulacrumDiagnostics.RecordInventory(
                duplicate,
                "treasury-bound",
                panel.ParentScreen as CharacterInspectionScreen);
        }
    }

    [HarmonyPatch(typeof(InventoryPanel), "RefreshPersonalContainer")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshPersonalContainer_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(InventoryPanel __instance)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is not
                RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            RefreshSimulacrumPersonalContainer(__instance, duplicate, "refresh");

            return false;
        }
    }

    private static void RefreshSimulacrumPersonalContainer(
        InventoryPanel panel,
        RulesetCharacterSimulacrum duplicate,
        string stage)
    {
        var personalContainer = duplicate.CharacterInventory.PersonalContainer;
        var personalContainerPanel = panel.personalContainerPanel;

        panel.personalContainer = personalContainer;

        if (personalContainerPanel.Container != personalContainer)
        {
            if (personalContainerPanel.Container != null)
            {
                personalContainerPanel.Unbind();
            }

            personalContainerPanel.CompareasonSlots = panel.CompareasonSlots;
            personalContainerPanel.Bind(
                personalContainer,
                panel.GuiCharacter,
                panel.DropAreaClicked,
                panel.VisibleSlotsRefreshed);
        }
        else
        {
            personalContainerPanel.RefreshNow();
        }

        personalContainerPanel.gameObject.SetActive(true);
        SimulacrumDiagnostics.RecordInventory(
            duplicate,
            $"personal-container-{stage}",
            panel.ParentScreen as CharacterInspectionScreen);
    }

    [HarmonyPatch(typeof(EncumbrancePanel), nameof(EncumbrancePanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EncumbranceBind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(EncumbrancePanel __instance)
        {
            if (!SimulacrumEncumbranceSubjects.TryGetValue(__instance, out _))
            {
                return true;
            }

            __instance.gameObject.SetActive(true);
            __instance.RefreshNow();

            return false;
        }
    }

    //PATCH: Enable Inventory Filtering and Sorting
    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(InventoryPanel __instance)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                duplicate.CharacterInventory.ItemEquiped -= __instance.ItemEquiped;
                duplicate.CharacterInventory.ItemUnequiped -= __instance.ItemUnequiped;
            }

            if (__instance.MainContainerPanel)
            {
                InventoryManagementContext.UnbindInventory(__instance.MainContainerPanel);
            }
        }

        [UsedImplicitly]
        public static void Postfix(InventoryPanel __instance)
        {
            // Keep the bindings alive while native Unbind tears down its child panels. A refresh
            // raised during teardown must still use the Simulacrum-safe paths.
            SimulacrumEncumbranceSubjects.Remove(__instance.encumbrancePanel);
            RestoreLootControls(__instance);
        }
    }

    [HarmonyPatch(typeof(InventoryPanel), "OnLootAllCb")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnLootAllCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(InventoryPanel __instance)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is not
                RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            var container = __instance.externalContainerPanel?.Container;
            var inventoryCommands = ServiceRepository.GetService<IInventoryCommandService>();

            if (container == null || inventoryCommands == null)
            {
                return false;
            }

            SimulacrumEquipmentPanel.SetExternalContainer(duplicate, container);
            var items = container.InventorySlots
                .Where(slot => slot?.EquipedItem != null)
                .Select(slot => (slot, slot.EquipedItem))
                .ToArray();

            foreach (var (slot, item) in items)
            {
                if (item.ItemDefinition.IsWealthPile)
                {
                    if (SimulacrumBehavior.TryGetOwner(duplicate, out var owner))
                    {
                        inventoryCommands.UnequipItem(slot);
                        inventoryCommands.GrantItem(owner, item, false);
                        continue;
                    }
                }

                if (!duplicate.CanCarryItem(item))
                {
                    Gui.GuiService.ShowAlert(
                        "Screen/&InventoryCannotLootAllDescription",
                        Gui.ColorFailure,
                        2.5f);
                    continue;
                }

                using (SimulacrumEquipmentPanel.BeginExternalContainerTransfer(
                           duplicate,
                           item))
                {
                    inventoryCommands.UnequipItem(slot);
                    inventoryCommands.AddContainerSubItem(
                        duplicate.CharacterInventory.PersonalContainer,
                        item,
                        __instance.GroundPosition);
                }
            }

            __instance.Refresh();
            SimulacrumDiagnostics.Write(
                "inventory",
                $"stage=external-container-loot-all guid={duplicate.Guid} items={items.Length}");

            return false;
        }
    }

    [HarmonyPatch(typeof(EncumbrancePanel), nameof(EncumbrancePanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EncumbranceRefresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(EncumbrancePanel __instance)
        {
            if (!SimulacrumEncumbranceSubjects.TryGetValue(
                    __instance,
                    out var duplicate))
            {
                return true;
            }

            duplicate.ComputeEncumbranceThresholds(
                out var lightThreshold,
                out var heavyThreshold,
                out var maximumWeight);
            var carriedWeight = duplicate.CharacterInventory.ComputeCarriedWeight();

            __instance.carriedWeightLabel.Text =
                Gui.FormatWeightRatio(carriedWeight, maximumWeight);
            __instance.lightThresholdLabel.Text = Gui.FormatWeight(lightThreshold);
            __instance.heavyThresholdLabel.Text = Gui.FormatWeight(heavyThreshold);
            __instance.previousRatio = __instance.fillGauge.fillAmount;
            __instance.targetRatio = Mathf.Clamp(
                maximumWeight <= 0 ? 0 : carriedWeight / maximumWeight,
                0f,
                1f);
            __instance.remainingAnimationTime = 0.5f;
            __instance.encumbranceNoneLabel.TMP_Text.color = carriedWeight < lightThreshold
                ? __instance.activeColor
                : __instance.inactiveColor;
            __instance.encumbranceLightLabel.TMP_Text.color =
                carriedWeight >= lightThreshold && carriedWeight < heavyThreshold
                    ? __instance.activeColor
                    : __instance.inactiveColor;
            __instance.encumbranceHeavyLabel.TMP_Text.color = carriedWeight >= heavyThreshold
                ? __instance.activeColor
                : __instance.inactiveColor;

            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.RefreshSlotsList))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshSlotsList_Patch
    {
        [UsedImplicitly]
        public static void Postfix(InventoryPanel __instance)
        {
            //PATCH: support for customized filtering of items for ItemProperty effect form
            CustomItemFilter.FilterItems(__instance);
        }
    }

    //PATCH: enable CTRL click-drag to bypass quest items checks on drop
    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.EndInteraction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EndInteraction_Patch
    {
        [UsedImplicitly]
        public static void Prefix(InventoryPanel __instance, out ItemDefinition __state)
        {
            __state = null;

            if (!SettingsContext.InputModManagerInstance.EnableCtrlClickDragToBypassQuestItemsOnDrop ||
                (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) ||
                __instance.DraggedItem?.ItemDefinition is not { } itemDefinition ||
                !itemDefinition.ItemTags.Remove(TagsDefinitions.ItemTagQuest))
            {
                return;
            }

            __state = itemDefinition;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, ItemDefinition __state)
        {
            if (__state != null)
            {
                __state.ItemTags.Add(TagsDefinitions.ItemTagQuest);
            }

            return __exception;
        }
    }

    //PATCH: enable unlimited inventory actions
    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.SpendInventoryActionAsNeeded))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpendInventoryActionAsNeeded_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(InventoryPanel __instance,
            RulesetInventorySlot newSlot,
            bool allowDifferentSlot,
            ref bool __result)
        {
            var locationCharacter =
                (SimulacrumEquipmentPanel.TryGetActiveCharacter(out var duplicate)
                    ? GameLocationCharacter.GetFromActor(duplicate)
                    : null) ??
                __instance.GuiCharacter.GameLocationCharacter;

            if ((newSlot == null || __instance.PreviousSlot == null ||
                 (newSlot != __instance.PreviousSlot && !allowDifferentSlot)) &&
                locationCharacter != null &&
                Gui.Battle != null &&
                __instance.InventoryManagementMode == InventoryManagementMode.Battle &&
                !Main.Settings.EnableUnlimitedInventoryActions &&
                locationCharacter.GetActionTypeStatus(ActionType.FreeOnce) ==
                ActionStatus.Available)
            {
                locationCharacter.SpendActionType(ActionType.FreeOnce);
                __result = true;

                return false;
            }

            __result = false;

            return false;
        }
    }

    private static void HideLootControls(InventoryPanel panel)
    {
        if (!SimulacrumLootControls.TryGetValue(panel, out _))
        {
            SimulacrumLootControls.Add(
                panel,
                new SimulacrumLootControlsState(
                    panel.lootAllButton && panel.lootAllButton.gameObject.activeSelf,
                    panel.lootAllInstructions && panel.lootAllInstructions.gameObject.activeSelf));
        }

        if (panel.lootAllButton)
        {
            panel.lootAllButton.gameObject.SetActive(false);
        }

        if (panel.lootAllInstructions)
        {
            panel.lootAllInstructions.gameObject.SetActive(false);
        }
    }

    private static void RestoreLootControls(InventoryPanel panel)
    {
        if (!SimulacrumLootControls.TryGetValue(panel, out var state))
        {
            return;
        }

        if (panel.lootAllButton)
        {
            panel.lootAllButton.gameObject.SetActive(state.ButtonActive);
        }

        if (panel.lootAllInstructions)
        {
            panel.lootAllInstructions.gameObject.SetActive(state.InstructionsActive);
        }

        SimulacrumLootControls.Remove(panel);
    }

    private sealed class SimulacrumLootControlsState(
        bool buttonActive,
        bool instructionsActive)
    {
        internal bool ButtonActive { get; } = buttonActive;
        internal bool InstructionsActive { get; } = instructionsActive;
    }
}
