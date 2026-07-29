using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class LocalCommandManagerPatcher
{
    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.OpenMerchant))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OpenMerchant_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter merchantGameLocationCharacter)
        {
            var networkingService = ServiceRepository.GetService<INetworkingService>();

            if (networkingService?.IsMultiplayerGame == true)
            {
                return;
            }

            var merchant = merchantGameLocationCharacter?.Merchant;
            var merchantScreen = Gui.GuiService.GetScreen<GameMerchantScreen>();

            if (merchant == null ||
                merchantScreen == null ||
                !object.ReferenceEquals(merchantScreen.GameMerchant, merchant))
            {
                return;
            }

            var playerId = networkingService?.LocalPlayerNumber ?? -1;

            // OpenMerchant acquires a player lock only in multiplayer, while
            // GameMerchantScreen always releases one when it closes.
            if (!merchant.HasPlayerLock(playerId))
            {
                merchant.AddPlayerLock(playerId);
            }
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.ExamineItem),
        typeof(RulesetCharacterHero),
        typeof(RulesetItem))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExamineItem_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(RulesetItem __1)
        {
            if (__1 == null ||
                !ItemMenuModalPatcher.IsUnsupportedSimulacrumDocument(__1.ItemDefinition) ||
                !RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                    __1.BearerGuid,
                    out var duplicate))
            {
                return true;
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.DropItem),
        typeof(RulesetCharacterHero),
        typeof(RulesetItem))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DropItem_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(RulesetItem __1)
        {
            if (__1 == null ||
                !RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                    __1.BearerGuid,
                    out var duplicate))
            {
                return true;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                return false;
            }

            using (duplicate.BeginInventoryMutation())
            {
                duplicate.CharacterInventory.DropItem(__1);
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.StoreItem),
        typeof(RulesetCharacterHero),
        typeof(RulesetItem))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StoreItem_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            RulesetCharacterHero __0,
            RulesetItem __1,
            ref RulesetInventorySlot __result)
        {
            if (__1 == null)
            {
                return true;
            }

            RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                __1.BearerGuid,
                out var duplicate);

            if (duplicate == null)
            {
                return true;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                __result = null;

                return false;
            }

            using (duplicate.BeginInventoryMutation())
            {
                if (duplicate.CharacterInventory.StoreItem(
                    __1,
                    false,
                    null,
                    true,
                    out __result,
                    true))
                {
                    duplicate.AcceptItem(__1);
                }
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.DefineWieldedItemsConfiguration),
        typeof(RulesetCharacterHero),
        typeof(int),
        typeof(RulesetItem),
        typeof(string),
        typeof(bool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DefineWieldedItemsConfiguration_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            int __1,
            RulesetItem __2,
            string __3,
            bool __4)
        {
            if (__2 == null ||
                !RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                    __2.BearerGuid,
                    out var duplicate))
            {
                return true;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                return false;
            }

            using (duplicate.BeginInventoryMutation())
            {
                duplicate.CharacterInventory.DefineWieldedItemsConfiguration(
                    __1,
                    __2,
                    __3);

                if (__4)
                {
                    duplicate.RequestWieldedItemsConfigurationRefresh();
                }
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.ReleaseItem),
        typeof(RulesetCharacterHero),
        typeof(RulesetItem))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReleaseItem_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(RulesetItem __1)
        {
            if (__1 == null ||
                !RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                    __1.BearerGuid,
                    out var duplicate))
            {
                return true;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                return false;
            }

            using (duplicate.BeginInventoryMutation())
            {
                duplicate.CharacterInventory.ItemReleased?.Invoke(__1, true);
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.SwitchAmmunition),
        typeof(RulesetCharacterHero),
        typeof(string))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SwitchAmmunition_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(RulesetCharacterHero __0, string __1)
        {
            if (!SimulacrumEquipmentPanel.TryGetActiveCharacter(out var duplicate) ||
                !SimulacrumBehavior.TryGetOwner(duplicate, out var owner) ||
                owner.Guid != __0?.Guid)
            {
                return true;
            }

            using (duplicate.BeginInventoryMutation())
            {
                duplicate.CharacterInventory.SwitchAmmunitionSource(__1);
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.SplitItemAndHandleStacks),
        typeof(RulesetCharacterHero),
        typeof(RulesetItem),
        typeof(int),
        typeof(int),
        typeof(RulesetInventorySlot),
        typeof(string),
        typeof(int),
        typeof(bool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SplitItemAndHandleStacks_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            RulesetCharacterHero __0,
            RulesetItem __1,
            int __2,
            int __3,
            RulesetInventorySlot __4,
            string __5,
            int __6,
            bool __7,
            ref RulesetItem __result)
        {
            var destination = RulesetCharacterSimulacrum.FindBySlot(__4);
            var source = RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                __1?.BearerGuid ?? 0,
                out var sourceDuplicate)
                ? sourceDuplicate
                : null;
            var duplicate = destination ?? source;

            if (duplicate == null || __1?.ItemDefinition == null)
            {
                return true;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                __result = null;

                return false;
            }

            if (source != null && destination != null && source.Guid != destination.Guid)
            {
                __result = null;

                return false;
            }

            if (destination != null &&
                __1.BearerGuid != 0 &&
                __1.BearerGuid != destination.Guid)
            {
                __result = null;

                return false;
            }

            using (duplicate.BeginInventoryMutation())
            {
                var itemFactory = ServiceRepository.GetService<IRulesetItemFactoryService>();

                if (itemFactory == null)
                {
                    __result = null;

                    return false;
                }

                var splitItem = itemFactory.CreateStandardItem(__1.ItemDefinition, true, null);
                var sourceStack = __1.GetAttribute(AttributeDefinitions.ItemStackCount);

                sourceStack.BaseValue -= __2;
                sourceStack.Refresh();

                if (__3 >= 0)
                {
                    var destinationStack = splitItem.GetAttribute(AttributeDefinitions.ItemStackCount);

                    destinationStack.BaseValue = __3;
                    destinationStack.Refresh();
                }

                if (__4 != null)
                {
                    __4.EquipItem(splitItem, 1, true, false);
                }
                else if (!string.IsNullOrEmpty(__5) && __6 >= 0)
                {
                    duplicate.CharacterInventory.DefineWieldedItemsConfiguration(
                        __6,
                        splitItem,
                        __5);
                    duplicate.RequestWieldedItemsConfigurationRefresh();
                    duplicate.AcceptItem(splitItem);
                }
                else if (__7)
                {
                    duplicate.CharacterInventory.PersonalContainer.AddSubItem(splitItem);
                    duplicate.AcceptItem(splitItem);
                }

                __result = splitItem;
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.EquipItem),
        typeof(RulesetInventorySlot),
        typeof(RulesetItem),
        typeof(RulesetCharacterHero),
        typeof(int))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EquipItem_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            RulesetInventorySlot __0,
            RulesetItem __1,
            RulesetCharacterHero __2,
            out System.IDisposable __state)
        {
            __state = null;

            var slot = __0;
            var item = __1;
            var duplicate = RulesetCharacterSimulacrum.FindBySlot(slot);

            if (duplicate == null)
            {
                return true;
            }

            if (slot.Disabled ||
                !SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                Gui.GuiService.ShowAlert(
                    "Failure/&SimulacrumCannotEquipItem",
                    Gui.ColorFailure,
                    2.5f);

                return false;
            }

            if (item == null ||
                (item.BearerGuid != 0 && item.BearerGuid != duplicate.Guid))
            {
                return false;
            }

            __state = duplicate.BeginInventoryMutation();

            if (slot.EquipedItem is { } replacedItem && replacedItem != item)
            {
                if (!duplicate.CharacterInventory.StoreItem(
                        replacedItem,
                        true,
                        null,
                        true,
                        out _,
                        true))
                {
                    return false;
                }

                slot.UnequipItem(true, true);

                if (!duplicate.CharacterInventory.StoreItem(
                        replacedItem,
                        false,
                        null,
                        true,
                        out _,
                        true))
                {
                    if (slot.EquipedItem == null)
                    {
                        slot.EquipItem(replacedItem, -1, true, true);
                    }

                    duplicate.AcceptItem(replacedItem);

                    return false;
                }

                duplicate.AcceptItem(replacedItem);
            }

            return true;
        }

        [UsedImplicitly]
        public static void Postfix(ref System.IDisposable __state)
        {
            DisposeState(ref __state);
        }

        [UsedImplicitly]
        public static System.Exception Finalizer(
            System.Exception __exception,
            ref System.IDisposable __state)
        {
            DisposeState(ref __state);

            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.AddContainerSubItem),
        typeof(RulesetContainer),
        typeof(RulesetItem),
        typeof(TA.int3))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AddContainerSubItem_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            RulesetContainer __0,
            RulesetItem __1,
            out ContainerMutationState __state)
        {
            __state = null;

            if (RulesetCharacterSimulacrum.FindByContainer(__0) is { } containerDuplicate &&
                !SimulacrumBehavior.CanAccessHumanoidInventory(containerDuplicate))
            {
                return false;
            }

            if (!InventorySubjectResolver.TryResolve(__0, out var destination))
            {
                return true;
            }

            var duplicate = destination as RulesetCharacterSimulacrum;
            var sourceDuplicate = RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                __1?.BearerGuid ?? 0,
                out var source)
                ? source
                : null;

            if (duplicate == null)
            {
                duplicate = sourceDuplicate;
            }

            if (duplicate == null)
            {
                return true;
            }

            if (destination is RulesetCharacterSimulacrum destinationDuplicate)
            {
                if (__1 == null ||
                    (__1.BearerGuid != 0 &&
                     __1.BearerGuid != destinationDuplicate.Guid &&
                     !ProximityLootModalPatcher.IsPendingGroundPickup(
                         __1,
                         destinationDuplicate) &&
                     !SimulacrumEquipmentPanel.IsExternalContainerItem(
                         destinationDuplicate,
                         __1)))
                {
                    return false;
                }
            }
            else if (sourceDuplicate != null &&
                     destination.Guid != sourceDuplicate.Guid)
            {
                return false;
            }

            __state = new ContainerMutationState(
                destination,
                duplicate.BeginInventoryMutation());

            return true;
        }

        [UsedImplicitly]
        private static void Postfix(
            RulesetContainer __0,
            RulesetItem __1,
            ref ContainerMutationState __state)
        {
            var state = __state;

            __state = null;

            if (state != null)
            {
                __1.BearerGuid = state.Destination.Guid;
                __1.AttunedToCharacter = string.Empty;

                if (state.Destination is RulesetCharacterSimulacrum duplicate)
                {
                    duplicate.AcceptItem(__1);
                }
            }

            state?.Dispose();
        }

        [UsedImplicitly]
        private static System.Exception Finalizer(
            System.Exception __exception,
            ref ContainerMutationState __state)
        {
            var state = __state;

            __state = null;
            state?.Dispose();

            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.UnequipItem),
        typeof(RulesetInventorySlot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UnequipItem_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            RulesetInventorySlot __0,
            out System.IDisposable __state)
        {
            var duplicate = RulesetCharacterSimulacrum.FindBySlot(__0);

            if (duplicate != null &&
                !SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                __state = null;

                return false;
            }

            __state = duplicate?.BeginInventoryMutation();

            return true;
        }

        [UsedImplicitly]
        public static void Postfix(ref System.IDisposable __state)
        {
            DisposeState(ref __state);
        }

        [UsedImplicitly]
        public static System.Exception Finalizer(
            System.Exception __exception,
            ref System.IDisposable __state)
        {
            DisposeState(ref __state);

            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.AttuneItem),
        typeof(RulesetCharacterHero),
        typeof(RulesetItem),
        typeof(bool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AttuneItem_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetItem __1)
        {
            var item = __1;

            if (item == null ||
                !RulesetEntity.TryGetEntity<RulesetCharacterSimulacrum>(
                    item.BearerGuid,
                    out _))
            {
                return true;
            }

            item.AttunedToCharacter = string.Empty;
            Gui.GuiService.ShowAlert(
                "Failure/&SimulacrumCannotAttuneItems",
                Gui.ColorFailure,
                2.5f);

            return false;
        }
    }

    [HarmonyPatch(
        typeof(LocalCommandManager),
        nameof(LocalCommandManager.SwitchWeaponConfiguration),
        typeof(GameLocationCharacter),
        typeof(int))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SwitchWeaponConfigurationSimulacrum_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __0, int __1)
        {
            var character = __0;
            var configurationId = __1;

            if (character?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                return false;
            }

            var inventory = duplicate.CharacterInventory;

            if (configurationId >= 0 &&
                configurationId < inventory.WieldedItemsConfigurations.Count)
            {
                using (duplicate.BeginInventoryMutation())
                {
                    inventory.SwitchToWieldItemsOfConfiguration(configurationId);
                }
            }

            character.SpendActionType(ActionDefinitions.ActionType.FreeOnce);

            return false;
        }
    }

    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.ProcessReactionRequest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ProcessReactionRequest_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ReactionRequest reactionRequest, bool validated)
        {
            if (reactionRequest is not IReactionRequestWithCallbacks callbacks) { return; }

            if (validated)
            {
                callbacks.ReactionValidated?.Invoke(reactionRequest);
            }
            else
            {
                callbacks.ReactionNotValidated?.Invoke(reactionRequest);
            }
        }
    }

    [HarmonyPatch(typeof(LocalCommandManager), nameof(LocalCommandManager.TogglePermanentInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TogglePermanentInvocation_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter character, RulesetInvocation invocation)
        {
            var rulesetCharacter = character?.RulesetCharacter;

            if (rulesetCharacter == null || invocation == null)
            {
                return false;
            }

            invocation.Toggle();
            // PATCH BEGIN
            foreach (var toggledBehaviour in invocation.invocationDefinition.GrantedFeature
                         .GetAllSubFeaturesOfType<IOnInvocationToggled>())
            {
                toggledBehaviour.OnInvocationToggled(character, invocation);
            }

            // PATCH END
            rulesetCharacter.RefreshAll();

            return false;
        }
    }

    private static void DisposeState(ref System.IDisposable state)
    {
        var current = state;

        state = null;
        current?.Dispose();
    }

    private sealed class ContainerMutationState(
        RulesetCharacter destination,
        System.IDisposable mutationScope) : System.IDisposable
    {
        internal RulesetCharacter Destination { get; } = destination;

        public void Dispose()
        {
            mutationScope?.Dispose();
        }
    }
}
