using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.ItemCrafting;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class ProximityLootModalPatcher
{
    private const string LootMoneyButton = "LootMoneyButton";
    private const string LootIngredientsButton = "LootIngredientsButton";
    private const string LootScrollsButton = "LootScrollsButton";
    private static readonly ConditionalWeakTable<ProximityLootModal, LootSession> Sessions = new();
    private static readonly HashSet<(ulong ItemGuid, ulong CharacterGuid)> PendingGroundPickups = [];

    internal static bool IsPendingGroundPickup(
        RulesetItem item,
        RulesetCharacterSimulacrum destination)
    {
        return item != null &&
               destination != null &&
               PendingGroundPickups.Contains((item.Guid, destination.Guid));
    }

    [HarmonyPatch(
        typeof(ProximityLootModal),
        nameof(ProximityLootModal.ShowProximityLoot),
        typeof(GameLocationCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class ShowProximityLoot_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(
            ProximityLootModal __instance,
            ref GameLocationCharacter __0)
        {
            if (Sessions.TryGetValue(__instance, out var activeSession) &&
                (activeSession.PendingCommands > 0 || activeSession.LootAllInProgress))
            {
                Gui.GuiService.ShowAlert(
                    "Failure/&SimulacrumInventoryUnavailable",
                    Gui.ColorFailure,
                    2.5f);

                return false;
            }

            Sessions.Remove(__instance);

            if (__0?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            SolastaUnfinishedBusiness.Behaviors.Specific.SimulacrumBehavior.TryGetOwner(
                duplicate,
                out var owner);

            var host = GameLocationCharacter.GetFromActor(owner) ??
                       ServiceRepository.GetService<IGameLocationCharacterService>()
                           ?.PartyCharacters
                           .FirstOrDefault(x => x?.RulesetCharacter is RulesetCharacterHero);

            if (host?.RulesetCharacter is not RulesetCharacterHero)
            {
                Gui.GuiService.ShowAlert(
                    "Failure/&SimulacrumInventoryUnavailable",
                    Gui.ColorFailure,
                    2.5f);

                return false;
            }

            Sessions.Add(__instance, new LootSession(duplicate.Guid));
            __0 = host;

            SimulacrumDiagnostics.Write(
                "loot",
                $"stage=open guid={duplicate.Guid} transport={host.RulesetCharacter.Guid}");

            return true;
        }
    }

    [HarmonyPatch(
        typeof(ProximityLootModal),
        nameof(ProximityLootModal.CharacterSelectionChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class CharacterSelectionChanged_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(ProximityLootModal __instance)
        {
            if (!TryGetLootingCharacter(__instance, out var duplicate))
            {
                return true;
            }

            var locationCharacter = GameLocationCharacter.GetFromActor(duplicate);
            var itemService = ServiceRepository.GetService<IGameLocationItemService>();

            __instance.itemsMap.Clear();

            if (locationCharacter != null && itemService != null)
            {
                itemService.EnumerateGroundItemsAroundCharacter(
                    locationCharacter,
                    5,
                    __instance.itemsMap);
            }

            __instance.BuildSlots();
            duplicate.ComputeEncumbranceThresholds(out _, out _, out var maximumWeight);
            SimulacrumDiagnostics.Write(
                "loot",
                $"stage=selection guid={duplicate.Guid} location={locationCharacter?.LocationPosition} " +
                $"items={__instance.itemsMap.Count} maxCarry={maximumWeight:0.###}");

            return false;
        }
    }

    [HarmonyPatch(typeof(ProximityLootModal), nameof(ProximityLootModal.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnEndHide_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(ProximityLootModal __instance)
        {
            __instance.lootingHeroesTable.gameObject.SetActive(true);
            __instance.encumbrancePanel.gameObject.SetActive(true);

            if (!Sessions.TryGetValue(__instance, out var session))
            {
                return;
            }

            session.HideCompleted = true;

            if (session.PendingCommands == 0 && !session.LootAllInProgress)
            {
                Sessions.Remove(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(LootEnumerationModal), nameof(LootEnumerationModal.TryToLootSlotBox))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class TryToLootSlotBox_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(
            LootEnumerationModal __instance,
            InventorySlotBox __0,
            ref bool __result)
        {
            if (__instance is not ProximityLootModal modal ||
                !TryGetLootingCharacter(modal, out var duplicate))
            {
                return true;
            }

            var item = __0?.InventorySlot?.EquipedItem;
            var canCarry = item != null && duplicate.CanCarryItem(item);
            var canEquipOrStore = item != null && duplicate.CanEquipOrStoreItem(item);
            var canDispatch = CanDispatchLootCommand();
            var hasGroundPosition = item != null && __instance.itemsMap.ContainsKey(item);

            SimulacrumDiagnostics.RecordLootEligibility(
                duplicate,
                item,
                canCarry,
                canEquipOrStore,
                canDispatch,
                hasGroundPosition);

            if (item == null ||
                !canEquipOrStore ||
                !canDispatch)
            {
                __result = false;

                return false;
            }

            if (!hasGroundPosition ||
                !__instance.itemsMap.TryGetValue(item, out var position))
            {
                __result = false;

                return false;
            }

            __instance.itemsMap.Remove(item);
            __instance.OnItemLooted(item, position);

            __0.UnequipItem(true);
            __result = true;

            return false;
        }
    }

    [HarmonyPatch(
        typeof(ProximityLootModal),
        nameof(ProximityLootModal.OnItemLooted),
        typeof(RulesetItem),
        typeof(TA.int3))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnItemLooted_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(
            ProximityLootModal __instance,
            RulesetItem __0,
            TA.int3 __1)
        {
            if (!TryGetLootingCharacter(__instance, out _) ||
                __0?.ItemDefinition?.IsWealthPile == true)
            {
                return true;
            }

            var inventoryCommands = ServiceRepository.GetService<IInventoryCommandService>();
            var commandService = ServiceRepository.GetService<ICommandService>();
            var itemService = ServiceRepository.GetService<IGameLocationItemService>();

            if (inventoryCommands == null || commandService == null || itemService == null)
            {
                Gui.GuiService.ShowAlert(
                    "Failure/&SimulacrumInventoryUnavailable",
                    Gui.ColorFailure,
                    2.5f);

                return false;
            }

            if (!Sessions.TryGetValue(__instance, out var session))
            {
                return false;
            }

            session.PendingCommands++;

            SimulacrumDiagnostics.Write(
                "loot",
                $"stage=ground-remove guid={session.SubjectGuid} item={__0.ItemDefinition.Name} " +
                $"pending={session.PendingCommands}");

            inventoryCommands.LootItemAtPosition(__0, __1);
            commandService.AcknowledgePreviousCommandLocally(() =>
            {
                if (itemService.EnumerateGroundItems(__1).Contains(__0))
                {
                    SimulacrumDiagnostics.Write(
                        "loot",
                        $"stage=ground-remove-failed guid={session.SubjectGuid} " +
                        $"item={__0.ItemDefinition.Name}");
                    CompletePendingCommand(__instance, session);

                    return;
                }

                if (RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                        session.SubjectGuid,
                        out var currentDuplicate) &&
                    SimulacrumBehavior.TryGetSnapshot(currentDuplicate, out var snapshot) &&
                    snapshot.IsCurrentSchema)
                {
                    SimulacrumDiagnostics.Write(
                        "loot",
                        $"stage=store-request guid={session.SubjectGuid} item={__0.ItemDefinition.Name}");
                    PendingGroundPickups.Add((__0.Guid, currentDuplicate.Guid));

                    try
                    {
                        inventoryCommands.AddContainerSubItem(
                            currentDuplicate.CharacterInventory.PersonalContainer,
                            __0,
                            __1);
                    }
                    finally
                    {
                        PendingGroundPickups.Remove((__0.Guid, currentDuplicate.Guid));
                    }
                }
                else
                {
                    SimulacrumDiagnostics.Write(
                        "loot",
                        $"stage=restore-ground guid={session.SubjectGuid} item={__0.ItemDefinition.Name}");
                    inventoryCommands.CreateItemAtPosition(__0, __1);
                }

                commandService.AcknowledgePreviousCommandLocally(() =>
                {
                    if (RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                            session.SubjectGuid,
                            out var currentDuplicate) &&
                        !ContainsItem(currentDuplicate.CharacterInventory, __0) &&
                        !itemService.EnumerateGroundItems(__1).Contains(__0))
                    {
                        SimulacrumDiagnostics.Write(
                            "loot",
                            $"stage=store-failed-restore guid={session.SubjectGuid} " +
                            $"item={__0.ItemDefinition.Name}");
                        inventoryCommands.CreateItemAtPosition(__0, __1);
                        commandService.AcknowledgePreviousCommandLocally(
                            () => CompletePendingCommand(__instance, session));

                        return;
                    }

                    CompletePendingCommand(__instance, session);
                });
            });
            Gui.TooltipService.HideTooltip();

            return false;
        }
    }

    [HarmonyPatch(typeof(LootEnumerationModal), nameof(LootEnumerationModal.OnLootAllCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnLootAllCb_Patch
    {
        [UsedImplicitly]
        internal static void Prefix(LootEnumerationModal __instance)
        {
            if (__instance is ProximityLootModal modal &&
                TryGetLootingCharacter(modal, out var duplicate) &&
                !__instance.hasLootAllButtonBeenClicked &&
                __instance.itemsToLootCache.Count == 0)
            {
                BeginLootAll(modal);
                duplicate.CharacterInventory.PersonalContainer.ClearStackedItems();
            }
        }
    }

    [HarmonyPatch(
        typeof(LootEnumerationModal),
        nameof(LootEnumerationModal.CheckForPreviousLootAllCommandResult),
        typeof(RulesetCharacterHero))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class CheckForPreviousLootAllCommandResult_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(LootEnumerationModal __instance)
        {
            if (__instance is not ProximityLootModal modal ||
                !TryGetLootSession(modal, out var session, out var duplicate))
            {
                return true;
            }

            if (session.PendingCommands > 0)
            {
                session.LootAllResultPending = true;

                return false;
            }

            FinalizeLootAll(modal, session, duplicate);

            return false;
        }
    }

    private static bool OnlyMoney(ItemDefinition item)
    {
        return item.IsWealthPile;
    }

    private static bool OnlyLightIngredients(ItemDefinition item)
    {
        return item.ItemTags.Contains(TagsDefinitions.ItemTagIngredient) && item.weight <= 1;
    }

    private static bool OnlyScrolls(ItemDefinition item)
    {
        return ScrollsData.IsScrollItem(item);
    }

    private static bool SlotMatches(RulesetInventorySlot slot, Func<ItemDefinition, bool> filter)
    {
        var item = slot.EquipedItem;
        return item != null && filter.Invoke(item.ItemDefinition);
    }

    private static void UpdateCustomButtons(LootEnumerationModal modal)
    {
        if (modal is not ProximityLootModal) { return; }

        var parent = modal.lootAllButton.transform.parent;

        var button = parent.Find(LootMoneyButton);
        if (button != null)
        {
            button.gameObject.SetActive(modal.groundSlots.Any(s => SlotMatches(s, OnlyMoney)));
        }

        button = parent.Find(LootIngredientsButton);
        if (button != null)
        {
            button.gameObject.SetActive(modal.groundSlots.Any(s => SlotMatches(s, OnlyLightIngredients)));
        }

        button = parent.Find(LootScrollsButton);
        if (button != null)
        {
            button.gameObject.SetActive(modal.groundSlots.Any(s => SlotMatches(s, OnlyScrolls)));
        }
    }

    [HarmonyPatch(typeof(ProximityLootModal), nameof(ProximityLootModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        internal static void Prefix([NotNull] ProximityLootModal __instance)
        {
            InitCustomButtons(__instance);
            UpdateCustomButtons(__instance);

            var simulacrumSession = TryGetLootingCharacter(__instance, out _);

            __instance.lootingHeroesTable.gameObject.SetActive(!simulacrumSession);
            __instance.encumbrancePanel.gameObject.SetActive(!simulacrumSession);
        }

        private static void InitCustomButtons(ProximityLootModal modal)
        {
            var parent = modal.lootAllButton.transform.parent;

            parent.GetComponent<HorizontalLayoutGroup>().spacing = 10;

            foreach (var element in parent.Find("CloseButton").GetComponents<LayoutElement>())
            {
                if (element.minWidth > 100) { element.minWidth = 100; }
            }

            if (parent.Find(LootMoneyButton) != null) { return; }

            var prefab = modal.lootAllButton.gameObject;

            //Loot all money
            var asset = Object.Instantiate(prefab, parent, false);
            var button = asset.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => OnLootCb(modal, OnlyMoney));
            asset.transform.Find("Label").GetComponent<GuiLabel>().Text = "UI/&LootAllGoldTitle";
            asset.transform.Find("Background").GetComponent<GuiTooltip>().Content = "UI/&LootAllGoldTooltip";
            foreach (var element in asset.GetComponents<LayoutElement>())
            {
                if (element.minWidth > 100) { element.minWidth = 100; }
            }

            asset.name = LootMoneyButton;

            //Loot all ingredients
            asset = Object.Instantiate(prefab, parent, false);
            button = asset.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => OnLootCb(modal, OnlyLightIngredients));
            asset.transform.Find("Label").GetComponent<GuiLabel>().Text = "UI/&LootAllIngredientsTitle";
            asset.transform.Find("Background").GetComponent<GuiTooltip>().Content = "UI/&LootAllIngredientsTooltip";
            foreach (var element in asset.GetComponents<LayoutElement>())
            {
                if (element.minWidth > 100) { element.minWidth = 100; }
            }

            asset.name = LootIngredientsButton;

            //Loot all ingredients
            asset = Object.Instantiate(prefab, parent, false);
            button = asset.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => OnLootCb(modal, OnlyScrolls));
            asset.transform.Find("Label").GetComponent<GuiLabel>().Text = "UI/&LootAllScrollsTitle";
            asset.transform.Find("Background").GetComponent<GuiTooltip>().Content = "UI/&LootAllScrollsTooltip";
            foreach (var element in asset.GetComponents<LayoutElement>())
            {
                if (element.minWidth > 100) { element.minWidth = 100; }
            }

            asset.name = LootScrollsButton;
        }

        private static void OnLootCb(LootEnumerationModal modal, Func<ItemDefinition, bool> filter)
        {
            if (modal.hasLootAllButtonBeenClicked || modal.itemsToLootCache.Count > 0) { return; }

            modal.hasLootAllButtonBeenClicked = true;
            var proximityModal = modal as ProximityLootModal;

            if (proximityModal != null)
            {
                BeginLootAll(proximityModal);
            }

            var lootingHero = modal.LootingHero.RulesetCharacterHero;
            var lootingInventory = proximityModal != null &&
                                   TryGetLootingCharacter(proximityModal, out var duplicate)
                ? duplicate.CharacterInventory
                : lootingHero.CharacterInventory;

            lootingInventory.PersonalContainer.ClearStackedItems();
            var fullSuccess = true;
            var hasLooted = false;
            modal.itemsToLootCache.Clear();

            for (var index = 0; index < modal.groundSlots.Count; ++index)
            {
                var item = modal.groundSlots[index].EquipedItem;
                if (item == null) { continue; }

                var definition = item.ItemDefinition;
                if (!filter.Invoke(definition)) { continue; }

                modal.itemsToLootCache.Add(item);
                if (!modal.TryToLootSlotBox(modal.slotsTable.GetChild(index).GetComponent<InventorySlotBox>()))
                {
                    fullSuccess = false;
                }
                else if (!hasLooted)
                {
                    hasLooted = true;
                }
            }

            ServiceRepository.GetService<ICommandService>()
                .AcknowledgePreviousCommandLocally(() => modal.CheckForPreviousLootAllCommandResult(lootingHero));

            if (!fullSuccess)
            {
                Gui.GuiService.ShowAlert(InventoryPanel.InventoryCannotLootAll, Gui.ColorBrokenWhite);
            }

            if (hasLooted)
            {
                modal.SignalItemInteraction();
            }

            modal.CharacterSelectionChanged();
        }
    }

    [HarmonyPatch(typeof(LootEnumerationModal), nameof(LootEnumerationModal.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class Refresh_Patch
    {
        [UsedImplicitly]
        internal static void Prefix([NotNull] LootEnumerationModal __instance)
        {
            UpdateCustomButtons(__instance);
        }

        [UsedImplicitly]
        internal static void Postfix([NotNull] LootEnumerationModal __instance)
        {
            if (__instance is not ProximityLootModal modal ||
                !TryGetLootingCharacter(modal, out var duplicate) ||
                !__instance.lootAllButton)
            {
                return;
            }

            __instance.lootAllButton.interactable = __instance.itemsMap.Keys.Any(
                duplicate.CanEquipOrStoreItem);
        }
    }

    private static bool TryGetLootingCharacter(
        ProximityLootModal modal,
        out RulesetCharacterSimulacrum duplicate)
    {
        return TryGetLootSession(modal, out _, out duplicate);
    }

    private static bool TryGetLootSession(
        ProximityLootModal modal,
        out LootSession session,
        out RulesetCharacterSimulacrum duplicate)
    {
        session = null;
        duplicate = null;

        return modal != null &&
               Sessions.TryGetValue(modal, out session) &&
               RulesetEntity.TryGetEntity(session.SubjectGuid, out duplicate);
    }

    private static void BeginLootAll(ProximityLootModal modal)
    {
        if (Sessions.TryGetValue(modal, out var session))
        {
            session.LootAllInProgress = true;
            session.LootAllResultPending = false;
        }
    }

    private static void CompletePendingCommand(
        ProximityLootModal modal,
        LootSession expectedSession)
    {
        if (!TryGetLootSession(modal, out var session, out var duplicate) ||
            !ReferenceEquals(session, expectedSession))
        {
            return;
        }

        if (session.PendingCommands > 0)
        {
            session.PendingCommands--;
        }

        SimulacrumDiagnostics.Write(
            "loot",
            $"stage=command-complete guid={session.SubjectGuid} pending={session.PendingCommands}");

        if (session.PendingCommands == 0 && session.LootAllResultPending)
        {
            FinalizeLootAll(modal, session, duplicate);

            return;
        }

        if (session.PendingCommands == 0 &&
            session.HideCompleted &&
            !session.LootAllInProgress)
        {
            Sessions.Remove(modal);
        }
    }

    private static void FinalizeLootAll(
        ProximityLootModal modal,
        LootSession session,
        RulesetCharacterSimulacrum duplicate)
    {
        var inventory = duplicate.CharacterInventory;
        var inventoryItems = inventory
            .EnumerateAllSlots(true, false, false)
            .Select(x => x.EquipedItem)
            .Where(x => x != null)
            .ToHashSet();
        var fullSuccess = modal.itemsToLootCache.All(item =>
            item.ItemDefinition.IsWealthPile ||
            inventoryItems.Contains(item) ||
            inventory.PersonalContainer.ItemHasBeenStacked(item));

        if (!fullSuccess)
        {
            Gui.GuiService.ShowAlert(
                InventoryPanel.InventoryCannotLootAll,
                Gui.ColorBrokenWhite,
                2.5f);
        }

        if (modal.Visible)
        {
            modal.RefreshNow();
        }

        modal.itemsToLootCache.Clear();
        modal.lootingCharacterItemsCache.Clear();
        modal.hasLootAllButtonBeenClicked = false;
        inventory.PersonalContainer.ClearStackedItems();
        session.LootAllInProgress = false;
        session.LootAllResultPending = false;

        if (session.HideCompleted)
        {
            Sessions.Remove(modal);
        }
    }

    private static bool CanDispatchLootCommand()
    {
        var available = ServiceRepository.GetService<IInventoryCommandService>() != null &&
                        ServiceRepository.GetService<ICommandService>() != null &&
                        ServiceRepository.GetService<IGameLocationItemService>() != null;

        if (!available)
        {
            Gui.GuiService.ShowAlert(
                "Failure/&SimulacrumInventoryUnavailable",
                Gui.ColorFailure,
                2.5f);
        }

        return available;
    }

    private static bool ContainsItem(RulesetInventory inventory, RulesetItem item)
    {
        return inventory != null &&
               item != null &&
               (inventory.EnumerateAllSlots(true, false, false)
                    .Any(slot => slot.EquipedItem == item) ||
                inventory.PersonalContainer.ItemHasBeenStacked(item));
    }

    private sealed class LootSession(ulong subjectGuid)
    {
        internal ulong SubjectGuid { get; } = subjectGuid;
        internal int PendingCommands { get; set; }
        internal bool HideCompleted { get; set; }
        internal bool LootAllInProgress { get; set; }
        internal bool LootAllResultPending { get; set; }
    }
}
