using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class FunctorInventoryLootPatcher
{
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class ExecuteMoveNext_Patch
    {
        [UsedImplicitly]
        private static MethodBase TargetMethod()
        {
            var iterator = typeof(FunctorInventoryLoot)
                .GetNestedTypes(BindingFlags.NonPublic)
                .Single(type => type.Name.Contains("<Execute>d__"));

            return AccessTools.Method(iterator, "MoveNext");
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(
                typeof(ExecuteMoveNext_Patch),
                nameof(ResolveLootTransportHero));
            var isCharacterControlled = AccessTools.Method(
                typeof(PlayerController),
                nameof(PlayerController.IsCharacterControlled),
                [typeof(GameLocationCharacter)]);
            var controlReplacement = AccessTools.Method(
                typeof(ExecuteMoveNext_Patch),
                nameof(IsLootCharacterControlled));
            var replacedHeroCast = 0;
            var replacedControlGate = 0;

            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Isinst &&
                    instruction.operand as System.Type == typeof(RulesetCharacterHero))
                {
                    replacedHeroCast++;
                    var replacementInstruction = new CodeInstruction(
                        OpCodes.Call,
                        replacement);

                    replacementInstruction.labels.AddRange(instruction.labels);
                    replacementInstruction.blocks.AddRange(instruction.blocks);
                    yield return replacementInstruction;
                    continue;
                }

                if ((instruction.opcode == OpCodes.Call ||
                     instruction.opcode == OpCodes.Callvirt) &&
                    Equals(instruction.operand, isCharacterControlled))
                {
                    replacedControlGate++;
                    var replacementInstruction = new CodeInstruction(
                        OpCodes.Call,
                        controlReplacement);

                    replacementInstruction.labels.AddRange(instruction.labels);
                    replacementInstruction.blocks.AddRange(instruction.blocks);
                    yield return replacementInstruction;
                    continue;
                }

                yield return instruction;
            }

            if (replacedHeroCast != 1 || replacedControlGate != 1)
            {
                throw new InvalidOperationException(
                    "Expected one FunctorInventoryLoot Hero cast and control gate, " +
                    $"replaced {replacedHeroCast} and {replacedControlGate}.");
            }
        }

        private static RulesetCharacterHero ResolveLootTransportHero(
            RulesetCharacter character)
        {
            var transport = character as RulesetCharacterHero;

            if (character is RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumBehavior.TryGetOwner(duplicate, out transport);
                SimulacrumDiagnostics.RecordLootGate(
                    duplicate,
                    "transport",
                    transport != null,
                    $"transport={transport?.Guid.ToString() ?? "<null>"}");
            }

            return transport;
        }

        private static bool IsLootCharacterControlled(
            PlayerController controller,
            GameLocationCharacter locationCharacter)
        {
            var nativeResult = controller?.IsCharacterControlled(locationCharacter) == true;

            if (nativeResult ||
                locationCharacter?.RulesetCharacter is not RulesetCharacterSimulacrum
                {
                    LifecycleState: SimulacrumLifecycleState.Ready
                } duplicate)
            {
                return nativeResult;
            }

            var ownerControlled = SimulacrumBehavior.TryGetOwner(duplicate, out var owner) &&
                                  GameLocationCharacter.GetFromActor(owner) is { } ownerLocation &&
                                  controller?.IsCharacterControlled(ownerLocation) == true;

            SimulacrumDiagnostics.RecordLootGate(
                duplicate,
                "controller",
                ownerControlled,
                $"native={nativeResult} owner={owner?.Guid.ToString() ?? "<null>"}");

            return ownerControlled;
        }
    }

    [HarmonyPatch(
        typeof(GameGadget),
        nameof(GameGadget.CanInteract),
        [typeof(GameLocationCharacter)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class GameGadgetCanInteract_Patch
    {
        [UsedImplicitly]
        private static void Postfix(
            GameGadget __instance,
            GameLocationCharacter __0,
            ref bool __result)
        {
            if (__0?.RulesetCharacter is not RulesetCharacterSimulacrum
                {
                    LifecycleState: SimulacrumLifecycleState.Ready
                } duplicate)
            {
                return;
            }

            var nativeResult = __result;
            var isLootGadget = __instance.HasFunctor(FunctorDefinitions.FunctorInventoryLoot);

            if (__instance.HasValidScope && isLootGadget)
            {
                __result = true;
            }

            SimulacrumDiagnostics.RecordLootGate(
                duplicate,
                "gadget",
                __result,
                $"native={nativeResult} validScope={__instance.HasValidScope} " +
                $"inventoryLoot={isLootGadget}");
        }
    }

    [HarmonyPatch(
        typeof(FunctorInventoryLoot),
        nameof(FunctorInventoryLoot.ExecuteFastLoot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class ExecuteFastLoot_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            GameLocationCharacter __0,
            RulesetContainer __1)
        {
            if (__0?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            var inventoryCommands = ServiceRepository.GetService<IInventoryCommandService>();
            var commandService = ServiceRepository.GetService<ICommandService>();

            if (inventoryCommands == null || commandService == null || __1 == null)
            {
                return false;
            }

            SimulacrumEquipmentPanel.SetExternalContainer(duplicate, __1);

            foreach (var slot in __1.InventorySlots
                         .Where(slot => slot?.EquipedItem != null)
                         .ToArray())
            {
                var item = slot.EquipedItem;

                if (item.ItemDefinition.IsWealthPile)
                {
                    var treasury = Gui.GameCampaign?.Party?.Treasury;
                    var gains = item.Gains;

                    if (!SimulacrumBehavior.TryGetOwner(duplicate, out var owner) ||
                        treasury?.CurrencyAmounts == null ||
                        gains == null ||
                        gains.Length > treasury.CurrencyAmounts.Length)
                    {
                        SimulacrumDiagnostics.RecordLootGate(
                            duplicate,
                            "fast-container-skip",
                            false,
                            $"item={item.ItemDefinition.Name} reason=invalid-wealth-destination");
                        continue;
                    }

                    var currencyBefore = treasury.CurrencyAmounts.ToArray();

                    inventoryCommands.UnequipItem(slot);
                    inventoryCommands.GrantItem(owner, item, false);
                    commandService.AcknowledgePreviousCommandLocally(() =>
                    {
                        var currencyAfter = treasury.CurrencyAmounts;
                        var gainsApplied = currencyAfter != null &&
                                           gains.Select((gain, index) =>
                                                   currencyAfter[index] >= currencyBefore[index] + gain)
                                               .All(applied => applied);
                        var sourceCleared = slot.EquipedItem != item;

                        SimulacrumDiagnostics.RecordLootGate(
                            duplicate,
                            "fast-container-result",
                            sourceCleared && gainsApplied,
                            $"item={item.ItemDefinition.Name} kind=wealth " +
                            $"sourceCleared={sourceCleared} gainsApplied={gainsApplied}");
                    });
                }
                else if (CanStoreInPersonalContainer(duplicate, item))
                {
                    inventoryCommands.UnequipItem(slot);

                    using (SimulacrumEquipmentPanel.BeginExternalContainerTransfer(
                               duplicate,
                               item))
                    {
                        inventoryCommands.AddContainerSubItem(
                            duplicate.CharacterInventory.PersonalContainer,
                            item,
                            default);
                    }

                    commandService.AcknowledgePreviousCommandLocally(() =>
                    {
                        var sourceCleared = slot.EquipedItem != item;
                        var destination = duplicate.CharacterInventory?.PersonalContainer;
                        var stored = destination != null &&
                                     (destination.FindSlotHoldingItem(item) != null ||
                                      destination.ItemHasBeenStacked(item));

                        SimulacrumDiagnostics.RecordLootGate(
                            duplicate,
                            "fast-container-result",
                            sourceCleared && stored,
                            $"item={item.ItemDefinition.Name} kind=item " +
                            $"sourceCleared={sourceCleared} stored={stored}");
                    });
                }
                else
                {
                    SimulacrumDiagnostics.RecordLootGate(
                        duplicate,
                        "fast-container-skip",
                        false,
                        $"item={item.ItemDefinition.Name} reason=cannot-store");
                }
            }

            SimulacrumDiagnostics.Write(
                "inventory",
                $"stage=fast-container-loot guid={duplicate.Guid}");

            return false;
        }

        private static bool CanStoreInPersonalContainer(
            RulesetCharacterSimulacrum duplicate,
            RulesetItem item)
        {
            return duplicate.CharacterInventory?.PersonalContainer != null &&
                   duplicate.CanCarryItem(item) &&
                   duplicate.CharacterInventory.CanStoreItem(item, null, out _);
        }
    }
}
