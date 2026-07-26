using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using UnityEngine;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class InventoryShortcutsPanelPatcher
{
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
            bool force)
        {
            var duplicate = __instance.GuiCharacter?.RulesetCharacter as
                                RulesetCharacterSimulacrum ??
                            (SimulacrumEquipmentPanel.TryGetActiveCharacter(out var activeDuplicate)
                                ? activeDuplicate
                                : null);

            if (duplicate != null)
            {
                if (duplicate.LifecycleState != SimulacrumLifecycleState.Ready)
                {
                    return false;
                }

                var inventory = duplicate.CharacterInventory;

                if (rank < 0 || rank >= inventory.WieldedItemsConfigurations.Count)
                {
                    return false;
                }

                if (rank != inventory.CurrentConfiguration || force)
                {
                    if (GameLocationCharacter.GetFromActor(duplicate) is { } location &&
                        ServiceRepository.GetService<ICommandService>() is { } commandService)
                    {
                        commandService.SwitchWeaponConfiguration(location, rank);
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

            if (Main.Settings.EnableCtrlClickOnlySwapsMainHand && isCtrlPressed)
            {
                rank += 100;
            }

            return true;
        }

        [UsedImplicitly]
        public static void Postfix(InventoryShortcutsPanel __instance, int rank)
        {
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
            var duplicate = __instance.GuiCharacter?.RulesetCharacter as
                                RulesetCharacterSimulacrum ??
                            (SimulacrumEquipmentPanel.TryGetActiveCharacter(out var activeDuplicate)
                                ? activeDuplicate
                                : null);
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
            var locationCharacter = duplicate == null
                ? __instance.GuiCharacter.GameLocationCharacter
                : GameLocationCharacter.GetFromActor(duplicate);
            var selectors = EnsureDirectSelectors(
                __instance,
                wieldedItemsConfigurations.Count);

            if (duplicate != null)
            {
                SimulacrumDiagnostics.RecordInventoryShortcuts(
                    duplicate,
                    "configurations-bound",
                    wieldedItemsConfigurations.Count,
                    __instance.configurationsTable.childCount,
                    selectors.Count(x => x));
            }

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
                            ActionStatus.Spent && !__instance.ItemSelectionInProgress:
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

            SimulacrumDiagnostics.RecordInventoryShortcuts(
                duplicate,
                "collect-slots",
                configurationCount,
                __instance.configurationsTable.childCount,
                selectors.Count(x => x));

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
