using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class SimulacrumInventoryUiPatcher
{
    private static readonly MethodInfo HeroGetter = AccessTools.PropertyGetter(
        typeof(GuiCharacter),
        nameof(GuiCharacter.RulesetCharacterHero));
    private static readonly MethodInfo SubjectGetter = AccessTools.Method(
        typeof(SimulacrumEquipmentPanel),
        nameof(SimulacrumEquipmentPanel.GetInventorySubject));
    private static readonly MethodInfo TransportGetter = AccessTools.Method(
        typeof(SimulacrumEquipmentPanel),
        nameof(SimulacrumEquipmentPanel.GetTransportHero));

    // These panels only use the Hero getter to reach members declared on
    // RulesetCharacter. Keep the actual Simulacrum as their inventory subject.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class InventorySubjectGetter_Patch
    {
        private static readonly IReadOnlyDictionary<MethodBase, int> Targets = BuildTargets();

        [UsedImplicitly]
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            return Targets.Keys;
        }

        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var codes = instructions.ToList();
            var replacements = 0;

            foreach (var instruction in codes.Where(instruction => instruction.Calls(HeroGetter)))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = SubjectGetter;
                replacements++;
            }

            if (!Targets.TryGetValue(original, out var expected) || replacements != expected)
            {
                throw new InvalidOperationException(
                    $"Simulacrum inventory subject patch expected " +
                    $"{(Targets.TryGetValue(original, out expected) ? expected : -1)} Hero getters in " +
                    $"{original.DeclaringType?.Name}.{original.Name}, found {replacements}.");
            }

            return codes;
        }

        private static IReadOnlyDictionary<MethodBase, int> BuildTargets()
        {
            return new Dictionary<MethodBase, int>
            {
                [RequireMethod(typeof(EquipmentLayoutPanel), nameof(EquipmentLayoutPanel.Bind),
                    [typeof(GuiCharacter)])] = 1,
                [RequireMethod(typeof(ContainerPanel), "OnReorderCb", Type.EmptyTypes)] = 2,
                [RequireMethod(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.Bind),
                    [
                        typeof(GuiCharacter),
                        typeof(bool),
                        typeof(WieldedConfigurationSelector.OnConfigurationSwitchedHandler)
                    ])] = 4,
                [RequireMethod(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.Unbind),
                    Type.EmptyTypes)] = 4,
                [RequireMethod(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.IsValidDrop),
                    [typeof(Vector3), typeof(string).MakeByRefType(), typeof(int).MakeByRefType()])] = 1,
                [RequireMethod(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.CollectSlots),
                    [typeof(List<InventorySlotBox>)])] = 4,
                [RequireMethod(typeof(InventoryShortcutsPanel),
                    nameof(InventoryShortcutsPanel.OnConfigurationSwitched),
                    [typeof(int), typeof(bool)])] = 2,
                [RequireMethod(typeof(InventoryShortcutsPanel), nameof(InventoryShortcutsPanel.Refresh),
                    Type.EmptyTypes)] = 2,
                [RequireMethod(typeof(InventoryShortcutsPanel),
                    nameof(InventoryShortcutsPanel.BindConfigurations),
                    Type.EmptyTypes)] = 3
            };
        }
    }

    // InventoryPanel and InventorySlotBox also invoke Hero-only command APIs.
    // Use the subject only for base-character inventory access and retain the
    // owner Hero solely as the native RPC transport where a Hero is required.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class MixedInventoryGetter_Patch
    {
        private static readonly MethodInfo ActualHeroGetter = AccessTools.Method(
            typeof(MixedInventoryGetter_Patch),
            nameof(GetActualHero));
        private static readonly IReadOnlyDictionary<MethodBase, HeroGetterRole[]> Targets = BuildTargets();

        [UsedImplicitly]
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            return Targets.Keys;
        }

        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var codes = instructions.ToList();
            var replacementIndex = 0;

            if (!Targets.TryGetValue(original, out var roles))
            {
                throw new InvalidOperationException(
                    $"Missing Simulacrum inventory getter plan for " +
                    $"{original.DeclaringType?.Name}.{original.Name}.");
            }

            for (var index = 0; index < codes.Count; index++)
            {
                if (!codes[index].Calls(HeroGetter))
                {
                    continue;
                }

                if (replacementIndex >= roles.Length)
                {
                    throw new InvalidOperationException(
                        $"Too many Hero getters in {original.DeclaringType?.Name}.{original.Name}.");
                }

                codes[index].opcode = OpCodes.Call;
                codes[index].operand = roles[replacementIndex++] switch
                {
                    HeroGetterRole.Subject => SubjectGetter,
                    HeroGetterRole.Transport => TransportGetter,
                    HeroGetterRole.ActualHero => ActualHeroGetter,
                    _ => throw new ArgumentOutOfRangeException()
                };
            }

            if (replacementIndex != roles.Length)
            {
                throw new InvalidOperationException(
                    $"Simulacrum inventory getter patch expected {roles.Length} Hero getters in " +
                    $"{original.DeclaringType?.Name}.{original.Name}, found {replacementIndex}.");
            }

            return codes;
        }

        private static RulesetCharacterHero GetActualHero(GuiCharacter guiCharacter)
        {
            return guiCharacter?.RulesetCharacter as RulesetCharacterHero;
        }

        private static IReadOnlyDictionary<MethodBase, HeroGetterRole[]> BuildTargets()
        {
            return new Dictionary<MethodBase, HeroGetterRole[]>
            {
                [RequireMethod(typeof(InventoryPanel), "OnPointerDown", [typeof(PointerEventData)])] =
                [
                    HeroGetterRole.Subject,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport
                ],
                [RequireMethod(typeof(InventoryPanel), "OnPointerUp", [typeof(PointerEventData)])] =
                    Repeat(HeroGetterRole.Transport, 2),
                [RequireMethod(typeof(InventoryPanel), "OnDoubleClick", Type.EmptyTypes)] =
                [
                    HeroGetterRole.Subject,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Subject,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Subject
                ],
                [RequireMethod(typeof(InventoryPanel), nameof(InventoryPanel.CancelDrag), Type.EmptyTypes)] =
                    Repeat(HeroGetterRole.Transport, 5),
                [RequireMethod(typeof(InventoryPanel), "StartDrag",
                    [typeof(RulesetItem), typeof(RulesetInventorySlot)])] = [HeroGetterRole.Subject],
                [RequireMethod(typeof(InventoryPanel), "StopDrag", [typeof(bool)])] =
                    Repeat(HeroGetterRole.Transport, 2),
                [RequireMethod(typeof(InventoryPanel), "RefreshValidDropTargets", Type.EmptyTypes)] =
                    Repeat(HeroGetterRole.Subject, 2),
                [RequireMethod(typeof(InventoryPanel), "StartInteraction", Type.EmptyTypes)] =
                    Repeat(HeroGetterRole.Transport, 2),
                [RequireMethod(typeof(InventoryPanel), "EndInteraction", Type.EmptyTypes)] =
                [
                    HeroGetterRole.Subject,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport,
                    HeroGetterRole.Transport
                ],
                [RequireMethod(typeof(InventoryPanel), nameof(InventoryPanel.RefreshSlotsList), Type.EmptyTypes)] =
                    [HeroGetterRole.Subject, HeroGetterRole.Transport],
                [RequireMethod(typeof(InventoryPanel), "SplitItemAndHandleStacks",
                    [
                        typeof(RulesetItem),
                        typeof(int),
                        typeof(int),
                        typeof(RulesetInventorySlot),
                        typeof(string),
                        typeof(int)
                    ])] = [HeroGetterRole.Transport],
                [RequireMethod(typeof(InventorySlotBox), "EquipItem", [typeof(RulesetItem), typeof(int)])] =
                    [HeroGetterRole.Transport],
                [RequireMethod(typeof(InventorySlotBox), nameof(InventorySlotBox.RefreshState),
                    [typeof(bool), typeof(RulesetItem)])] =
                    [HeroGetterRole.Subject, HeroGetterRole.ActualHero, HeroGetterRole.Subject],
                [RequireMethod(typeof(InventorySlotBox), "OnSwitchAmmunitionCb", [typeof(bool)])] =
                    [HeroGetterRole.Transport],
                [RequireMethod(typeof(WieldedConfigurationSelector), nameof(WieldedConfigurationSelector.Bind),
                    [
                        typeof(GuiCharacter),
                        typeof(int),
                        typeof(RulesetWieldedConfiguration),
                        typeof(WieldedConfigurationSelector.OnConfigurationSwitchedHandler),
                        typeof(bool),
                        typeof(bool),
                        typeof(bool),
                        typeof(RectTransform)
                    ])] =
                    [HeroGetterRole.Transport, HeroGetterRole.Subject, HeroGetterRole.Transport]
            };
        }

        private static HeroGetterRole[] Repeat(HeroGetterRole role, int count)
        {
            return Enumerable.Repeat(role, count).ToArray();
        }

        private enum HeroGetterRole
        {
            Subject,
            Transport,
            ActualHero
        }
    }

    // InventoryPanel has three supported native return-to-inventory paths which accept only
    // a Hero. The owner supplies only the transport identity, while this narrow
    // replacement keeps the real item destination on the Simulacrum.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class ReturnItemToInventorySubject_Patch
    {
        [UsedImplicitly]
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                RequireMethod("OnPointerDown", [typeof(PointerEventData)]),
                RequireMethod("OnPointerUp", [typeof(PointerEventData)]),
                RequireMethod(nameof(InventoryPanel.CancelDrag), Type.EmptyTypes)
            ];
        }

        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var codes = instructions.ToList();
            var grantItem = AccessTools.Method(
                typeof(IInventoryCommandService),
                nameof(IInventoryCommandService.GrantItem),
                [typeof(RulesetCharacterHero), typeof(RulesetItem), typeof(bool)]);
            var replacement = AccessTools.Method(
                typeof(SimulacrumEquipmentPanel),
                nameof(SimulacrumEquipmentPanel.ReturnItemToInventorySubject));
            var replacements = 0;

            foreach (var code in codes.Where(code => code.Calls(grantItem)))
            {
                code.opcode = OpCodes.Call;
                code.operand = replacement;
                replacements++;
            }

            if (replacements == 0)
            {
                throw new InvalidOperationException(
                    $"No GrantItem call was found in {original.DeclaringType?.Name}.{original.Name}.");
            }

            return codes;
        }

        private static MethodInfo RequireMethod(string name, Type[] parameters)
        {
            return SimulacrumInventoryUiPatcher.RequireMethod(
                typeof(InventoryPanel),
                name,
                parameters);
        }
    }

    // EndInteraction has four ReleaseItem paths. Only the two equipment-replacement
    // blocks first unequip the displaced item and equip its replacement.
    [HarmonyPatch(typeof(InventoryPanel), "EndInteraction")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class ReturnReleasedEquipmentToInventorySubject_Patch
    {
        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var codes = instructions.ToList();
            var releaseItem = AccessTools.Method(
                typeof(IInventoryCommandService),
                nameof(IInventoryCommandService.ReleaseItem),
                [typeof(RulesetCharacterHero), typeof(RulesetItem)]);
            var unequipItem = AccessTools.Method(
                typeof(InventorySlotBox),
                "UnequipItem",
                [typeof(bool)]);
            var equipItem = AccessTools.Method(
                typeof(InventorySlotBox),
                "EquipItem",
                [typeof(RulesetItem), typeof(int)]);
            var replacement = AccessTools.Method(
                typeof(SimulacrumEquipmentPanel),
                nameof(SimulacrumEquipmentPanel.ReturnReleasedItemToInventorySubject));
            var replacements = 0;

            for (var index = 0; index < codes.Count; index++)
            {
                if (!codes[index].Calls(releaseItem) ||
                    !PreviousBlockContainsEquipmentReplacement(
                        codes,
                        index,
                        unequipItem,
                        equipItem))
                {
                    continue;
                }

                codes[index].opcode = OpCodes.Call;
                codes[index].operand = replacement;
                replacements++;
            }

            if (replacements != 2)
            {
                throw new InvalidOperationException(
                    $"Simulacrum equipment return patch expected 2 replacement ReleaseItem calls in " +
                    $"{original.DeclaringType?.Name}.{original.Name}, found {replacements}.");
            }

            return codes;
        }

        private static bool PreviousBlockContainsEquipmentReplacement(
            IReadOnlyList<CodeInstruction> codes,
            int releaseIndex,
            MethodInfo unequipItem,
            MethodInfo equipItem)
        {
            var hasUnequip = false;
            var hasEquip = false;

            for (var index = releaseIndex - 1; index >= 0; index--)
            {
                var code = codes[index];

                hasUnequip |= code.Calls(unequipItem);
                hasEquip |= code.Calls(equipItem);

                if (code.labels.Count > 0 ||
                    code.opcode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch
                        or FlowControl.Return or FlowControl.Throw)
                {
                    break;
                }
            }

            return hasUnequip && hasEquip;
        }
    }

    private static MethodInfo RequireMethod(Type type, string name, Type[] parameters)
    {
        return AccessTools.DeclaredMethod(type, name, parameters) ??
               throw new MissingMethodException(type.FullName, name);
    }
}
