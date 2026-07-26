using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class WieldedConfigurationSelectorPatcher
{
    [HarmonyPatch(typeof(WieldedConfigurationSelector), nameof(WieldedConfigurationSelector.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: do not show warning sign over specialized monk weapons
            var baseIsMonkWeapon =
                typeof(WeaponDescription).GetMethod(nameof(WeaponDescription.IsMonkWeaponOrUnarmed));

            var customIsMonkWeapon =
                typeof(Bind_Patch).GetMethod(nameof(IsMonkWeaponOrUnarmed),
                    BindingFlags.Static | BindingFlags.NonPublic);

            return instructions.ReplaceCalls(baseIsMonkWeapon,
                "WieldedConfigurationSelector.Bind",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, customIsMonkWeapon));
        }

        private static bool IsMonkWeaponOrUnarmed(WeaponDescription description, GuiCharacter guiCharacter)
        {
            return guiCharacter.RulesetCharacter.IsMonkWeaponOrUnarmed(description);
        }

        [UsedImplicitly]
        public static void Postfix(WieldedConfigurationSelector __instance,
            GuiCharacter guiCharacter,
            int rank,
            RulesetWieldedConfiguration configuration)
        {
            var duplicate =
                guiCharacter?.RulesetCharacter as RulesetCharacterSimulacrum ??
                RulesetCharacterSimulacrum.FindBySlot(configuration?.MainHandSlot) ??
                RulesetCharacterSimulacrum.FindBySlot(configuration?.OffHandSlot);

            if (duplicate == null &&
                SimulacrumEquipmentPanel.TryGetActiveCharacter(
                    out var activeDuplicate) &&
                activeDuplicate.CharacterInventory.WieldedItemsConfigurations
                    .IndexOf(configuration) >= 0)
            {
                duplicate = activeDuplicate;
            }

            if (duplicate != null)
            {
                var inventory = duplicate.CharacterInventory;
                var currentRank = inventory.CurrentConfiguration;
                var isCurrentConfiguration =
                    rank == currentRank &&
                    currentRank >= 0 &&
                    currentRank < inventory.WieldedItemsConfigurations.Count &&
                    ReferenceEquals(
                        configuration,
                        inventory.WieldedItemsConfigurations[currentRank]);

                RefreshSimulacrumEquipmentWarnings(
                    __instance,
                    duplicate,
                    configuration,
                    rank,
                    currentRank,
                    isCurrentConfiguration);

                return;
            }

            var character = guiCharacter.RulesetCharacter;

            AddHandXbowWarning(
                __instance.mainHandWarning,
                configuration.MainHandSlot,
                character,
                configuration);
            AddHandXbowWarning(
                __instance.offHandWarning,
                configuration.OffHandSlot,
                character,
                configuration);
        }

        private static void RefreshSimulacrumEquipmentWarnings(
            WieldedConfigurationSelector selector,
            RulesetCharacterSimulacrum duplicate,
            RulesetWieldedConfiguration configuration,
            int configurationRank,
            int currentConfigurationRank,
            bool isCurrentConfiguration)
        {
            var mainHand = configuration?.MainHandSlot?.EquipedItem;
            var offHand = configuration?.OffHandSlot?.EquipedItem;
            var mainWeapon = mainHand?.ItemDefinition is { IsWeapon: true }
                ? mainHand.ItemDefinition.WeaponDescription
                : null;
            var offWeapon = offHand?.ItemDefinition is { IsWeapon: true }
                ? offHand.ItemDefinition.WeaponDescription
                : null;
            var hasWeaponPair = mainWeapon != null && offWeapon != null;
            var hasMeleeWeapon = hasWeaponPair &&
                                 (mainWeapon.WeaponTypeDefinition?.WeaponProximity ==
                                  RuleDefinitions.AttackProximity.Melee ||
                                  offWeapon.WeaponTypeDefinition?.WeaponProximity ==
                                  RuleDefinitions.AttackProximity.Melee);
            var mainLight = mainWeapon?.WeaponTags.Contains(
                TagsDefinitions.WeaponTagLight) == true;
            var offLight = offWeapon?.WeaponTags.Contains(
                TagsDefinitions.WeaponTagLight) == true;
            var canDualWieldNonLight =
                SimulacrumBehavior.SupportsNonLightDualWielding(duplicate);
            var pairPassesOffHandEquipmentRules =
                hasWeaponPair &&
                SimulacrumBehavior.CanUseOffHandWeaponAttack(
                    duplicate,
                    mainHand,
                    offHand);
            var matchingBonusModePresent =
                SimulacrumBehavior.TryGetBonusAttackModeForOffHand(
                    duplicate,
                    offHand,
                    out var matchingBonusMode);
            var matchingBonusModeInvalidByEquipment =
                matchingBonusModePresent &&
                CustomItemsContext.IsAttackModeInvalid(
                    duplicate,
                    matchingBonusMode);
            var bonusModeSuppressesLightWarning =
                isCurrentConfiguration &&
                matchingBonusModePresent &&
                !matchingBonusModeInvalidByEquipment;
            var suppressLightWeaponWarning =
                pairPassesOffHandEquipmentRules ||
                bonusModeSuppressesLightWarning;
            var computedLightWarningMain =
                hasMeleeWeapon && !suppressLightWeaponWarning && !mainLight;
            var computedLightWarningOff =
                hasMeleeWeapon && !suppressLightWeaponWarning && !offLight;

            SetLightWeaponWarning(
                selector.mainHandWarning,
                computedLightWarningMain);
            SetLightWeaponWarning(
                selector.offHandWarning,
                computedLightWarningOff);
            AddHandXbowWarning(
                selector.mainHandWarning,
                configuration.MainHandSlot,
                duplicate,
                configuration);
            AddHandXbowWarning(
                selector.offHandWarning,
                configuration.OffHandSlot,
                duplicate,
                configuration);

            SimulacrumDiagnostics.RecordDualWieldEligibility(
                duplicate,
                mainHand,
                offHand,
                mainLight,
                offLight,
                canDualWieldNonLight,
                pairPassesOffHandEquipmentRules,
                matchingBonusModePresent,
                matchingBonusModeInvalidByEquipment,
                bonusModeSuppressesLightWarning,
                configurationRank,
                currentConfigurationRank,
                isCurrentConfiguration,
                computedLightWarningMain,
                computedLightWarningOff,
                IsWarningActive(selector.mainHandWarning),
                GetWarningContent(selector.mainHandWarning),
                IsWarningActive(selector.offHandWarning),
                GetWarningContent(selector.offHandWarning));
        }

        private static bool IsWarningActive(Component warning)
        {
            return warning && warning.gameObject.activeSelf;
        }

        private static string GetWarningContent(Component warning)
        {
            return warning
                ? warning.GetComponent<GuiTooltip>()?.Content
                : null;
        }

        private static void SetLightWeaponWarning(Component warning, bool active)
        {
            if (!warning)
            {
                return;
            }

            const string warningContent =
                "Tooltip/&WeaponNotLightWarningDescription";
            var tooltip = warning.GetComponent<GuiTooltip>();
            var isLightWarning = tooltip?.Content == warningContent;

            if (active)
            {
                // Do not overwrite another, more specific equipment warning.
                if (!warning.gameObject.activeSelf || isLightWarning)
                {
                    if (tooltip != null)
                    {
                        tooltip.Content = warningContent;
                    }

                    warning.gameObject.SetActive(true);
                }

                return;
            }

            if (isLightWarning)
            {
                warning.gameObject.SetActive(false);
            }
        }

        private static void AddHandXbowWarning(
            Component warning,
            RulesetInventorySlot slot,
            RulesetCharacter character,
            RulesetWieldedConfiguration configuration)
        {
            if (!warning
                || warning.gameObject.activeSelf
                || !CustomItemsContext.IsHandCrossbowUseInvalid(slot.equipedItem, character,
                    configuration.MainHandSlot.EquipedItem, configuration.OffHandSlot.EquipedItem))
            {
                return;
            }

            warning.gameObject.SetActive(true);
            warning.GetComponent<GuiTooltip>().Content = "Tooltip/&NoFreeHandToLoadAmmoDescription";
        }
    }
}
