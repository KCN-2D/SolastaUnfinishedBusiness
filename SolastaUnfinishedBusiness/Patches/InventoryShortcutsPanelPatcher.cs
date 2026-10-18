using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class InventoryShortcutsPanelPatcher
{
    private static RulesetCharacterSimulacrum GetInventoryDuplicate(InventoryShortcutsPanel panel) =>
        panel.GuiCharacter?.RulesetCharacter as RulesetCharacterSimulacrum ??
        (SimulacrumEquipmentPanel.TryGetActiveCharacter(out var duplicate) ? duplicate : null);

    private static GameLocationCharacter GetInventoryLocationCharacter(
        InventoryShortcutsPanel panel, RulesetCharacterSimulacrum duplicate) =>
        duplicate == null ? panel.GuiCharacter?.GameLocationCharacter : GameLocationCharacter.GetFromActor(duplicate);

    private static bool PrepareConfigurationSwitch(GameLocationCharacter character, int configuration,
        ICommandService commands, out Action<bool> complete)
    {
        complete = null;

        if (Gui.Battle == null || Main.Settings.EnableUnlimitedInventoryActions ||
            !Tabletop2024Context.HasDualWielder2024QuickDraw(character) ||
            character.GetActionTypeStatus(ActionType.FreeOnce) == ActionStatus.Available)
        {
            return true;
        }

        if (commands == null ||
            !Tabletop2024Context.TryBeginDualWielder2024QuickDraw(character, configuration, out var finish))
        {
            return false;
        }

        // Keep the real inventory actor, including a Simulacrum, through acknowledgement.
        complete = succeeded =>
        {
            finish(succeeded);

            if (succeeded)
            {
                character.ActionsRefreshed?.Invoke(character);
            }
        };
        return true;
    }

    private static List<WieldedConfigurationSelector> GetDirectSelectors(
        RectTransform configurationsTable)
    {
        var selectors = new List<WieldedConfigurationSelector>();

        if (!configurationsTable)
        {
            return selectors;
        }

        for (var index = 0; index < configurationsTable.childCount; index++)
        {
            selectors.Add(
                configurationsTable.GetChild(index)
                    .GetComponent<WieldedConfigurationSelector>());
        }

        return selectors;
    }

    private static List<WieldedConfigurationSelector> EnsureDirectSelectors(
        InventoryShortcutsPanel panel,
        int requiredCount)
    {
        while (panel.configurationsTable.childCount < requiredCount)
        {
            Gui.GetPrefabFromPool(
                panel.wieldedConfigurationButtonPrefab,
                panel.configurationsTable);
        }

        var selectors = GetDirectSelectors(panel.configurationsTable);

        for (var index = 0; index < requiredCount; index++)
        {
            if (selectors[index])
            {
                continue;
            }

            throw new MissingComponentException(
                $"{nameof(WieldedConfigurationSelector)} is missing from direct inventory " +
                $"configuration child {index}.");
        }

        return selectors;
    }

    [HarmonyPatch(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.OnConfigurationSwitched))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnConfigurationSwitched_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            InventoryShortcutsPanel __instance,
            ref int rank,
            bool force,
            out Action<bool> __state)
        {
            __state = null;
            var duplicate = GetInventoryDuplicate(__instance);
            var inventory = duplicate?.CharacterInventory ??
                            __instance.GuiCharacter?.RulesetCharacterHero?.CharacterInventory;
            var configuration = rank >= 100 ? rank - 100 : rank;

            if (duplicate is { LifecycleState: not SimulacrumLifecycleState.Ready } || inventory == null ||
                configuration < 0 || configuration >= inventory.WieldedItemsConfigurations.Count)
            {
                return false;
            }

            var locationCharacter = GetInventoryLocationCharacter(__instance, duplicate);
            var commands = ServiceRepository.GetService<ICommandService>();

            if (Gui.Battle != null &&
                (duplicate != null && (locationCharacter == null || commands == null) ||
                 locationCharacter != null &&
                 (locationCharacter.HasForcedActionOrManipulation() ||
                  ServiceRepository.GetService<IPlayerControllerService>()?.ActivePlayerController
                      ?.IsCharacterControlled(locationCharacter) == false)))
            {
                return false;
            }

            if (!PrepareConfigurationSwitch(locationCharacter, configuration, commands, out __state))
            {
                return false;
            }

            if (duplicate != null)
            {
                rank = configuration;
                if (rank != inventory.CurrentConfiguration || force)
                {
                    if (locationCharacter != null && commands != null)
                    {
                        commands.SwitchWeaponConfiguration(locationCharacter, rank);
                    }
                    else
                    {
                        using (duplicate.BeginInventoryMutation())
                        {
                            inventory.SwitchToWieldItemsOfConfiguration(rank);
                        }
                    }
                }

                var selectors = GetDirectSelectors(__instance.configurationsTable);

                for (var index = 0;
                     index < inventory.WieldedItemsConfigurations.Count && index < selectors.Count;
                     index++)
                {
                    if (selectors[index])
                    {
                        selectors[index].Selected = index == rank;
                    }
                }

                return false;
            }

            var isCtrlPressed = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (Main.Settings.EnableCtrlClickOnlySwapsMainHand && isCtrlPressed && rank < 100)
            {
                rank += 100;
            }

            return true;
        }

        [UsedImplicitly]
        public static void Postfix(InventoryShortcutsPanel __instance, int rank, Action<bool> __state)
        {
            if (__state != null)
            {
                // The ordinary FreeOnce is already spent: native SpendActionType cannot advance
                // its exhausted rank. Reserve the feat's separate use until the command is acknowledged.
                var commands = ServiceRepository.GetService<ICommandService>();

                if (commands == null)
                {
                    __state(false);
                }
                else
                {
                    commands.AcknowledgePreviousCommandLocally(() => __state(true));
                }
            }

            if (rank < 100)
            {
                return;
            }

            rank -= 100;

            var itemsConfigurations = __instance.GuiCharacter.RulesetCharacterHero.CharacterInventory
                .WieldedItemsConfigurations;

            var selectors = GetDirectSelectors(__instance.configurationsTable);

            for (var index = 0; index < itemsConfigurations.Count && index < selectors.Count; ++index)
            {
                if (selectors[index])
                {
                    selectors[index].Selected = index == rank;
                }
            }
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, Action<bool> __state)
        {
            if (__exception != null)
            {
                __state?.Invoke(false);
            }

            return __exception;
        }
    }

    //PATCH: QuickCastLightCantripOnWornItemsFirst
    [HarmonyPatch(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.OnCastLightCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class OnCastLightCb_Patch
    {
        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var oldMethod = typeof(RulesetCharacter).GetMethod(nameof(RulesetCharacter.TryFindTargetWieldedItem));
            var newMethod = typeof(OnCastLightCb_Patch).GetMethod(nameof(MyTryFindTargetWieldedItem));

            return instructions.ReplaceCall(oldMethod, 1, "InventoryShortcutsPanel.OnCastLightCb",
                new CodeInstruction(OpCodes.Call, newMethod));
        }

        public static bool MyTryFindTargetWieldedItem([NotNull] RulesetCharacter rulesetCharacter,
            out RulesetItem targetItem, bool fallbackOnTorsoArmor = false)
        {
            if (!Main.Settings.QuickCastLightCantripOnWornItemsFirst ||
                rulesetCharacter is not (RulesetCharacterHero or RulesetCharacterSimulacrum) ||
                rulesetCharacter.CharacterInventory?.InventorySlotsByName is not { } slots)
            {
                return rulesetCharacter.TryFindTargetWieldedItem(out targetItem, fallbackOnTorsoArmor);
            }

            targetItem =
                (slots.TryGetValue(EquipmentDefinitions.SlotTypeHead, out var head)
                    ? head?.EquipedItem
                    : null) ??
                (slots.TryGetValue(EquipmentDefinitions.SlotTypeNeck, out var neck)
                    ? neck?.EquipedItem
                    : null) ??
                (slots.TryGetValue(EquipmentDefinitions.SlotTypeTorso, out var torso)
                    ? torso?.EquipedItem
                    : null);

            return targetItem != null ||
                   rulesetCharacter.TryFindTargetWieldedItem(
                       out targetItem,
                       fallbackOnTorsoArmor);
        }
    }

    //PATCH: enable unlimited inventory actions
    [HarmonyPatch(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.BindConfigurations))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindConfigurationsPatch
    {
        [UsedImplicitly]
        public static bool Prefix(InventoryShortcutsPanel __instance)
        {
            var duplicate = GetInventoryDuplicate(__instance);
            var owner = SimulacrumEquipmentPanel.GetTransportHero(__instance.GuiCharacter);

            if (duplicate == null && owner == null)
            {
                return false;
            }

            if (duplicate is { LifecycleState: not SimulacrumLifecycleState.Ready })
            {
                return false;
            }

            var inventory = duplicate?.CharacterInventory ?? owner.CharacterInventory;
            var wieldedItemsConfigurations = inventory.WieldedItemsConfigurations;
            var locationCharacter = GetInventoryLocationCharacter(__instance, duplicate);
            var selectors = EnsureDirectSelectors(
                __instance,
                wieldedItemsConfigurations.Count);

            for (var i = 0; i < wieldedItemsConfigurations.Count; i++)
            {
                var component = selectors[i];

                component.gameObject.SetActive(true);
                component.Bind(__instance.GuiCharacter, i, wieldedItemsConfigurations[i],
                    __instance.OnConfigurationSwitched,
                    i == inventory.CurrentConfiguration,
                    __instance.inMainHud,
                    __instance.forceRefresh,
                    __instance.tooltipAnchor);

                var flag = false;

                if (locationCharacter != null)
                {
                    var service = ServiceRepository.GetService<IPlayerControllerService>();
                    var flag2 = service?.ActivePlayerController?.IsCharacterControlled(locationCharacter);
                    flag = flag2 ?? true;
                }
                else if (duplicate != null)
                {
                    var service = ServiceRepository.GetService<IPlayerControllerService>();
                    var flag2 = service?.ActivePlayerController?.IsCharacterControlled(duplicate);
                    flag = flag2 ?? true;
                }
                else if (__instance.GuiCharacter.GameCampaignCharacter != null)
                {
                    var service2 = ServiceRepository.GetService<IPlayerControllerService>();
                    var flag3 = service2?.ActivePlayerController?.IsCharacterControlled(__instance.GuiCharacter
                        .RulesetCharacter);
                    flag = flag3 ?? true;
                }

                if (!flag)
                {
                    component.Interactable = false;
                    component.Tooltip.Content = component.TooltipContent;
                }
                else if (locationCharacter != null &&
                         locationCharacter.HasForcedActionOrManipulation())
                {
                    component.Interactable = false;
                    component.Tooltip.Content = component.TooltipContent;
                }
                else
                {
                    switch (Main.Settings.EnableUnlimitedInventoryActions)
                    {
                        case false when
                            Gui.Battle != null &&
                            locationCharacter != null &&
                            locationCharacter.GetActionTypeStatus(ActionType.FreeOnce) ==
                            ActionStatus.Spent && !__instance.ItemSelectionInProgress &&
                            !Tabletop2024Context.CanUseDualWielder2024QuickDraw(locationCharacter):
                            component.Tooltip.Content = Gui.FormatFailure(component.TooltipContent,
                                "Failure/&FailureFlagFreeOnceActionSpent");
                            component.Interactable = false;
                            break;
                        case false when
                            Gui.Battle != null &&
                            locationCharacter != null &&
                            locationCharacter.GetActionTypeStatus(ActionType.FreeOnce) ==
                            ActionStatus.Unavailable && !__instance.ItemSelectionInProgress:
                            component.Tooltip.Content = Gui.FormatFailure(component.TooltipContent,
                                "Failure/&FailureFlagFreeOnceActionUnavailable");
                            component.Interactable = false;
                            break;
                        case true when
                            locationCharacter != null:
                            locationCharacter.RefundActionUse(ActionType.FreeOnce);

                            component.Interactable = true;
                            component.Tooltip.Content = component.TooltipContent;
                            break;
                        default:
                            component.Interactable = true;
                            component.Tooltip.Content = component.TooltipContent;
                            break;
                    }
                }
            }

            for (var j = wieldedItemsConfigurations.Count; j < selectors.Count; j++)
            {
                selectors[j].gameObject.SetActive(false);
            }

            return false;
        }
    }

    [HarmonyPatch(
        typeof(InventoryShortcutsPanel),
        nameof(InventoryShortcutsPanel.CollectSlots))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CollectSlots_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            InventoryShortcutsPanel __instance,
            List<InventorySlotBox> __0)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is not
                RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            var inventory = duplicate.CharacterInventory;
            var configurationCount = inventory.WieldedItemsConfigurations.Count;
            var selectors = GetDirectSelectors(__instance.configurationsTable);

            AddConfigurationSlots(inventory.CurrentConfiguration);

            for (var index = 0; index < configurationCount; index++)
            {
                if (index != inventory.CurrentConfiguration)
                {
                    AddConfigurationSlots(index);
                }
            }

            return false;

            void AddConfigurationSlots(int index)
            {
                if (index < 0 || index >= selectors.Count || !selectors[index])
                {
                    return;
                }

                var selector = selectors[index];

                if (selector.MainHandSlotBox?.InventorySlot is { ProxySlot: false })
                {
                    __0.TryAdd(selector.MainHandSlotBox);
                }

                if (selector.OffHandSlotBox?.InventorySlot is { ProxySlot: false })
                {
                    __0.TryAdd(selector.OffHandSlotBox);
                }
            }
        }
    }
}
