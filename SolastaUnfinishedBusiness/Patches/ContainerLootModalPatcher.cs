using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class ContainerLootModalPatcher
{
    private static readonly ConditionalWeakTable<ContainerLootModal, LootSession> Sessions = new();

    [HarmonyPatch(
        typeof(ContainerLootModal),
        nameof(ContainerLootModal.ShowContainerLoot),
        typeof(GameLocationCharacter),
        typeof(RulesetContainer))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class ShowContainerLoot_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(
            ContainerLootModal __instance,
            ref GameLocationCharacter __0,
            RulesetContainer __1)
        {
            if (Sessions.TryGetValue(__instance, out var previous) && previous.TransferInProgress)
            {
                Gui.GuiService.ShowAlert(
                    "Failure/&SimulacrumInventoryUnavailable",
                    Gui.ColorFailure,
                    2.5f);

                return false;
            }

            if (Sessions.TryGetValue(__instance, out _))
            {
                RestoreNativePanels(__instance);
            }

            Sessions.Remove(__instance);

            if (__0?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                return false;
            }

            if (!SimulacrumBehavior.TryGetOwner(duplicate, out var owner) ||
                GameLocationCharacter.GetFromActor(owner) is not { } transport)
            {
                Gui.GuiService.ShowAlert(
                    "Failure/&SimulacrumOwnerNotFound",
                    Gui.ColorFailure,
                    2.5f);

                return false;
            }

            duplicate.NormalizeInventory();
            Sessions.Add(__instance, new LootSession(duplicate.Guid, owner.Guid, __1));
            __0 = transport;

            return true;
        }
    }

    [HarmonyPatch(typeof(ContainerLootModal), nameof(ContainerLootModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(ContainerLootModal __instance)
        {
            if (!Sessions.TryGetValue(__instance, out _))
            {
                return;
            }

            __instance.lootingHeroesTable.gameObject.SetActive(false);
            __instance.encumbrancePanel.gameObject.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(ContainerLootModal), nameof(ContainerLootModal.OnInventoryCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnInventoryCb_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(ContainerLootModal __instance)
        {
            if (!Sessions.TryGetValue(__instance, out var knownSession))
            {
                return true;
            }

            if (!TryGetSession(__instance, out var session, out var duplicate, out _))
            {
                AbortSession(__instance, knownSession);

                return false;
            }

            if (session.TransferInProgress)
            {
                Gui.GuiService.ShowAlert(
                    "Failure/&SimulacrumInventoryUnavailable",
                    Gui.ColorFailure,
                    2.5f);

                return false;
            }

            if (SimulacrumEquipmentPanel.TryOpenExternalContainer(
                    duplicate,
                    Gui.CurrentLocationScreen,
                    session.Container))
            {
                __instance.Hide(false);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(ContainerLootModal), nameof(ContainerLootModal.OnBeginHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnBeginHide_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(ContainerLootModal __instance)
        {
            if (!Sessions.TryGetValue(__instance, out var session))
            {
                return;
            }

            RestoreNativePanels(__instance);
            session.HideCompleted = true;

            if (!session.TransferInProgress)
            {
                Sessions.Remove(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(ContainerLootModal), nameof(ContainerLootModal.HandleItemLoot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class HandleItemLoot_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(
            ContainerLootModal __instance,
            RulesetItem __2,
            RulesetInventorySlot __3)
        {
            if (!Sessions.TryGetValue(__instance, out var knownSession))
            {
                return true;
            }

            if (!TryGetSession(__instance, out var session, out var duplicate, out _))
            {
                AbortSession(__instance, knownSession);

                return false;
            }

            BeginTransfer(
                __instance,
                session,
                duplicate,
                [( __3, __2 )],
                false);

            return false;
        }
    }

    [HarmonyPatch(typeof(ContainerLootModal), nameof(ContainerLootModal.OnLootAllCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnLootAllCb_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(ContainerLootModal __instance)
        {
            if (!Sessions.TryGetValue(__instance, out var knownSession))
            {
                return true;
            }

            if (!TryGetSession(__instance, out var session, out var duplicate, out _))
            {
                AbortSession(__instance, knownSession);

                return false;
            }

            var items = session.Container?.InventorySlots
                .Where(slot => slot?.EquipedItem != null)
                .Select(slot => (slot, slot.EquipedItem))
                .ToArray() ?? [];

            BeginTransfer(__instance, session, duplicate, items, true);

            return false;
        }
    }

    private static void BeginTransfer(
        ContainerLootModal modal,
        LootSession session,
        RulesetCharacterSimulacrum duplicate,
        IReadOnlyCollection<(RulesetInventorySlot Slot, RulesetItem Item)> items,
        bool closeWhenComplete)
    {
        if (!IsSessionActive(modal, session, duplicate))
        {
            AbortSession(modal, session);

            return;
        }

        if (session.TransferInProgress ||
            items.Count == 0 ||
            ServiceRepository.GetService<IInventoryCommandService>() is not { } inventoryCommands ||
            ServiceRepository.GetService<ICommandService>() is not { } commandService)
        {
            return;
        }

        var queue = new Queue<(RulesetInventorySlot Slot, RulesetItem Item)>(items);

        session.TransferInProgress = true;
        TransferNext(
            modal,
            session,
            duplicate,
            queue,
            inventoryCommands,
            commandService,
            closeWhenComplete);
    }

    private static void TransferNext(
        ContainerLootModal modal,
        LootSession session,
        RulesetCharacterSimulacrum duplicate,
        Queue<(RulesetInventorySlot Slot, RulesetItem Item)> queue,
        IInventoryCommandService inventoryCommands,
        ICommandService commandService,
        bool closeWhenComplete)
    {
        if (!IsSessionActive(modal, session, duplicate))
        {
            AbortSession(modal, session);

            return;
        }

        if (queue.Count == 0)
        {
            CompleteTransfer(modal, session, duplicate, closeWhenComplete);

            return;
        }

        var (slot, item) = queue.Dequeue();

        if (slot?.EquipedItem != item || item == null)
        {
            TransferNext(
                modal,
                session,
                duplicate,
                queue,
                inventoryCommands,
                commandService,
                closeWhenComplete);

            return;
        }

        if (!item.ItemDefinition.IsWealthPile && !duplicate.CanEquipOrStoreItem(item))
        {
            Gui.GuiService.ShowAlert(
                InventoryPanel.InventoryCannotLootAll,
                Gui.ColorFailure,
                2.5f);
            TransferNext(
                modal,
                session,
                duplicate,
                queue,
                inventoryCommands,
                commandService,
                closeWhenComplete);

            return;
        }

        inventoryCommands.UnequipItem(slot);
        commandService.AcknowledgePreviousCommandLocally(() =>
        {
            if (!IsSessionActive(modal, session, duplicate))
            {
                if (slot.EquipedItem != item)
                {
                    slot.EquipItem(item, -1, true, false);
                }

                AbortSession(modal, session);

                return;
            }

            if (item.ItemDefinition.IsWealthPile &&
                RulesetEntity.TryGetEntity<RulesetCharacterHero>(session.TransportGuid, out var owner))
            {
                inventoryCommands.GrantItem(owner, item, false);
            }
            else
            {
                using (SimulacrumEquipmentPanel.BeginExternalContainerTransfer(duplicate, item))
                {
                    inventoryCommands.AddContainerSubItem(
                        duplicate.CharacterInventory.PersonalContainer,
                        item,
                        default);
                }
            }

            commandService.AcknowledgePreviousCommandLocally(() => TransferNext(
                modal,
                session,
                duplicate,
                queue,
                inventoryCommands,
                commandService,
                closeWhenComplete));
        });
    }

    private static void CompleteTransfer(
        ContainerLootModal modal,
        LootSession session,
        RulesetCharacterSimulacrum duplicate,
        bool closeWhenComplete)
    {
        session.TransferInProgress = false;

        if (modal)
        {
            modal.SignalItemInteraction();

            if (closeWhenComplete)
            {
                RestoreNativePanels(modal);
                modal.Hide(false);
            }
            else if (modal.Visible)
            {
                modal.Refresh();
            }
        }

        if (session.HideCompleted)
        {
            Sessions.Remove(modal);
        }
    }

    private static bool IsSessionActive(
        ContainerLootModal modal,
        LootSession session,
        RulesetCharacterSimulacrum duplicate)
    {
        return modal != null &&
               session != null &&
               duplicate != null &&
               Sessions.TryGetValue(modal, out var current) &&
               ReferenceEquals(current, session) &&
               duplicate.Guid == session.SubjectGuid &&
               SimulacrumBehavior.CanAccessHumanoidInventory(duplicate);
    }

    private static void AbortSession(
        ContainerLootModal modal,
        LootSession session)
    {
        if (session != null)
        {
            session.TransferInProgress = false;
        }

        RestoreNativePanels(modal);
        Sessions.Remove(modal);

        if (modal && modal.Visible)
        {
            modal.Hide(false);
        }
    }

    private static void RestoreNativePanels(ContainerLootModal modal)
    {
        if (!modal)
        {
            return;
        }

        modal.lootingHeroesTable.gameObject.SetActive(true);
        modal.encumbrancePanel.gameObject.SetActive(true);
    }

    private static bool TryGetSession(
        ContainerLootModal modal,
        out LootSession session,
        out RulesetCharacterSimulacrum duplicate,
        out RulesetCharacterHero transport)
    {
        session = null;
        duplicate = null;
        transport = null;

        return modal != null &&
               Sessions.TryGetValue(modal, out session) &&
               RulesetEntity.TryGetEntity(session.SubjectGuid, out duplicate) &&
               RulesetEntity.TryGetEntity(session.TransportGuid, out transport) &&
               SimulacrumBehavior.CanAccessHumanoidInventory(duplicate);
    }

    private sealed class LootSession(
        ulong subjectGuid,
        ulong transportGuid,
        RulesetContainer container)
    {
        internal RulesetContainer Container { get; } = container;
        internal bool HideCompleted { get; set; }
        internal ulong SubjectGuid { get; } = subjectGuid;
        internal bool TransferInProgress { get; set; }
        internal ulong TransportGuid { get; } = transportGuid;
    }
}
