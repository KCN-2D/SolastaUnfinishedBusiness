using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetCharacterMonsterPatcher
{
    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.PostLoad))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PostLoad_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetCharacterMonster __instance)
        {
            if (__instance is RulesetCharacterSimulacrum)
            {
                return;
            }

            __instance.GetSubFeaturesByType<IOnCharacterPostLoad>()
                .Do(provider => provider.OnCharacterPostLoad(__instance));
        }
    }

    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.FinalizeMonster))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FinalizeMonster_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetCharacterMonster __instance, bool keepMentalAbilityScores)
        {
            if (__instance is RulesetCharacterSimulacrum)
            {
                return;
            }

            //PATCH: Fixes AC calculation for MC shape-shifters and support for rage/ki/other stuff while shape-shifted
            MulticlassWildshapeContext.FinalizeMonster(__instance, keepMentalAbilityScores);

            //PATCH: supports Awaken the Beast Within feat
            ClassFeats.ActionFinishedByMeFeatAwakenTheBeastWithin.GrantTempHP(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.RefreshAll))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshAll_Patch
    {
        [UsedImplicitly]
        internal static void Prefix(
            RulesetCharacterMonster __instance,
            out SimulacrumBehavior.SimulacrumRefreshState __state)
        {
            __state = SimulacrumBehavior.CaptureRefreshState(__instance);

            //PATCH: clears cached customized spell effects
            PowerBundle.ClearSpellEffectCache(__instance);
        }

        [UsedImplicitly]
        internal static void Postfix(
            RulesetCharacterMonster __instance,
            SimulacrumBehavior.SimulacrumRefreshState __state)
        {
            // Native summon construction refreshes the shell before it is
            // registered. Keep the preflighted Simulacrum attributes and HP in
            // place so no transient 1 HP shell reaches the location layer.
            SimulacrumBehavior.RestoreInitializingSnapshot(__instance);
            SimulacrumBehavior.RestoreAfterRefresh(__instance, __state);

            //PATCH: allow power use validators to work on permanent (aura) powers
            __instance.UpdatePermanentPowersAsNeeded();
        }

        [UsedImplicitly]
        internal static Exception Finalizer(
            RulesetCharacterMonster __instance,
            SimulacrumBehavior.SimulacrumRefreshState __state,
            Exception __exception)
        {
            if (__exception != null)
            {
                SimulacrumBehavior.AbortRefreshAfterException(
                    __instance,
                    __state,
                    __exception);
            }

            return __exception;
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: support for rage/ki/other stuff while shape-shifted

            // refresh values of attribute modifiers before refreshing attributes
            var refreshAttributes =
                typeof(RulesetEntity).GetMethod("RefreshAttributes");
            var refreshAttributeModifiers =
                typeof(RulesetCharacter).GetMethod("RefreshAttributeModifierFromAbilityScore");
            var notifyCharacterRefreshed = AccessTools.Method(
                typeof(RulesetCharacter.CharacterRefreshedHandler),
                nameof(RulesetCharacter.CharacterRefreshedHandler.Invoke));
            var notifyAfterSnapshotRestore =
                AccessTools.Method(typeof(RefreshAll_Patch), nameof(NotifyAfterSnapshotRestore));
            var code = instructions
                .ReplaceCalls(refreshAttributes, "RulesetCharacterMonster.RefreshAll",
                    new CodeInstruction(OpCodes.Call, refreshAttributeModifiers),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Callvirt, refreshAttributes))
                .ToList(); // checked for Call vs CallVirtual
            var replacedNotifications = 0;

            foreach (var instruction in code.Where(x => x.Calls(notifyCharacterRefreshed)))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = notifyAfterSnapshotRestore;
                replacedNotifications++;
            }

            if (replacedNotifications != 1)
            {
                throw new InvalidOperationException(
                    "Expected one RulesetCharacterMonster.RefreshAll notification, " +
                    $"replaced {replacedNotifications}.");
            }

            return code;
        }

        private static void NotifyAfterSnapshotRestore(
            RulesetCharacter.CharacterRefreshedHandler handler,
            RulesetCharacter character)
        {
            // Ready Simulacra are restored by the Harmony Postfix. Publishing the
            // native notification here would expose the transient shell state
            // and force every subscriber to run twice.
            if (character is RulesetCharacterSimulacrum)
            {
                return;
            }

            handler(character);
        }
    }

    [HarmonyPatch(
        typeof(RulesetCharacterMonster),
        nameof(RulesetCharacterMonster.RefreshAttributeModifiersFromFeats))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshAttributeModifiersFromFeats_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetCharacterMonster __instance)
        {
            // A Simulacrum owns a copied feature snapshot. The native substitute path
            // would import the original hero's live feats and apply their modifiers again.
            return __instance is not RulesetCharacterSimulacrum;
        }
    }

    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.RefreshArmorClass))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshArmorClass_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: implements exclusivity for some AC modifiers
            // Makes sure various unarmored defense features don't stack with themselves and Dragon Resilience
            // Replaces calls to `RulesetAttributeModifier.SortAttributeModifiersList` with custom method
            // that removes inactive exclusive modifiers, and then calls `RulesetAttributeModifier.SortAttributeModifiersList`
            var sort = new Action<
                List<RulesetAttributeModifier>
            >(RulesetAttributeModifier.SortAttributeModifiersList).Method;

            var unstack = new Action<
                List<RulesetAttributeModifier>,
                RulesetCharacterMonster
            >(MulticlassWildshapeContext.ArmorClassStacking.ProcessWildShapeAc).Method;

            return instructions.ReplaceCalls(sort, "RulesetCharacterMonster.RefreshArmorClass",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, unstack));
        }

        [UsedImplicitly]
        public static void Postfix(
            RulesetCharacterMonster __instance,
            ref RulesetAttribute __result,
            bool callRefresh,
            bool dryRun,
            FeatureDefinition dryRunFeature)
        {
            foreach (var feature in __instance.GetSubFeaturesByType<IModifyAC>())
            {
                feature.ModifyAC(__instance, callRefresh, dryRun, dryRunFeature, __result);
            }

            RulesetAttributeModifier.SortAttributeModifiersList(__result.ActiveModifiers);
            __result.Refresh(true);
            __instance.SortArmorClassModifierTrends(__result);
            __result.Refresh();

            if (callRefresh && !dryRun)
            {
                if (!SimulacrumBehavior.ShouldDeferRefreshNotification(
                        __instance,
                        "armor-class"))
                {
                    __instance.CharacterRefreshed?.Invoke(__instance);
                }
            }
        }
    }

    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.ComputeBaseSavingThrowBonus))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeBaseSavingThrowBonus_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetCharacterMonster __instance, ref int __result,
            string abilityScoreName,
            List<TrendInfo> savingThrowModifierTrends)
        {
            //PATCH: allows `AddPBToSummonCheck` to add summoner's PB to the saving throws
            AddPBToSummonCheck.ModifyCheckBonus<ISavingThrowPerformanceProvider>(
                __instance, ref __result, abilityScoreName, savingThrowModifierTrends);
        }
    }


    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.ComputeBaseAbilityCheckBonus))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeBaseAbilityCheckBonus_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetCharacterMonster __instance, ref int __result,
            List<TrendInfo> abilityCheckModifierTrends,
            string proficiencyName)
        {
            //PATCH: allows `AddPBToSummonCheck` to add summoner's PB to the skill checks
            AddPBToSummonCheck.ModifyCheckBonus<IAbilityCheckPerformanceProvider>(
                __instance, ref __result, proficiencyName, abilityCheckModifierTrends);
        }
    }

    //PATCH: This is very similar to RulesetCharacterHero patch but it's here to support wildshape scenarios
    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.RefreshAttackModes))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshAttackModes_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ref bool callRefresh, out bool __state)
        {
            //save refresh flag, so it can be used in postfix
            __state = callRefresh;
            //reset refresh flag, so default code won't do refresh before postfix
            callRefresh = false;
        }

        [UsedImplicitly]
        public static void Postfix(RulesetCharacterMonster __instance, bool __state)
        {
            //PATCH: allow monk bonus unarmed attacks on wild-shaped characters
            MulticlassWildshapeContext.HandleExtraUnarmedAttacks(__instance);

            //PATCH: Allows adding extra attack modes
            __instance.GetSubFeaturesByType<IAddExtraAttack>()
                .OrderBy(provider => provider.Priority())
                .Do(provider => provider.TryAddExtraAttack(__instance));

            //PATCH: Allows changing damage and other stats of an attack mode
            __instance.AttackModes
                .ForEach(attackMode =>
                    __instance.GetSubFeaturesByType<IModifyWeaponAttackMode>()
                        .ForEach(provider => provider.ModifyWeaponAttackMode(__instance, attackMode, null, false)));

            //PATCH: allows persistent custom creature snapshots to restore the final, non-serialized attack modes
            __instance.GetSubFeaturesByType<IOnRefreshAttackModes>()
                .Do(provider => provider.AfterRefreshAttackModes(__instance));

            if (__instance is RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumDiagnostics.RecordShillelagh(duplicate, "attack-modes-refreshed");
            }

            //refresh character if needed after postfix
            if (__state)
            {
                if (!SimulacrumBehavior.ShouldDeferRefreshNotification(
                        __instance,
                        "attack-modes"))
                {
                    __instance.CharacterRefreshed?.Invoke(__instance);
                }
            }
        }
    }

    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.GetRemainingAttackUses))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetRemainingAttackUses_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetCharacterMonster __instance, ref int __result, RulesetAttackMode mode)
        {
            if (__result == 0 &&
                SimulacrumBehavior.TryGetUnlimitedCopiedAttackUses(
                    __instance,
                    mode,
                    out var simulacrumRemainingUses))
            {
                SimulacrumDiagnostics.RecordAttackUseFallback(
                    (RulesetCharacterSimulacrum)__instance,
                    mode,
                    __result,
                    simulacrumRemainingUses);
                __result = simulacrumRemainingUses;

                return;
            }

            //PATCH: allow monk bonus unarmed attacks on wild-shaped characters
            if (mode == null || !__instance.TryGetShapeChangeOriginalHero(out _))
            {
                return;
            }

            var attackModeRank = __instance.GetAttackModeRank(mode);

            if (attackModeRank == -1 && mode.ActionType == ActionDefinitions.ActionType.Bonus)
            {
                __result = -1;
            }
        }
    }

    [HarmonyPatch(typeof(RulesetCharacterMonster), nameof(RulesetCharacterMonster.AcknowledgeAttackUse))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AcknowledgeAttackUse_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            RulesetCharacterMonster __instance,
            RulesetAttackMode mode,
            ref AttackProximity proximity,
            bool hit,
            ref RulesetItem droppedItem,
            ref bool needToRefreshAttackModes,
            out bool __state)
        {
            __state = false;

            if (__instance is not RulesetCharacterSimulacrum duplicate ||
                mode == null)
            {
                return true;
            }

            CustomWeaponsContext.ProcessProducedFlameAttack(__instance, mode);
            proximity = ReturningWeapon.Process(__instance, mode, proximity);

            if (mode.SourceObject is RulesetItem sourceItem)
            {
                // RulesetCharacterMonster never accounts inventory-backed attacks.
                // Simulacra use Hero equipment, so preserve temporary item-property
                // counters before applying the Monster rank bookkeeping below.
                sourceItem.AccountAttack();

                if (hit)
                {
                    sourceItem.AccountHit();
                }

                __state = true;
            }

            if (!SimulacrumBehavior.TryGetUnlimitedCopiedAttackUses(
                    __instance,
                    mode,
                    out var remainingUses))
            {
                return true;
            }

            // Native monster bookkeeping ranks the mode against the shared shell
            // MonsterDefinition. Inventory-backed copied attacks deliberately are
            // not present there and are unlimited, so acknowledging them natively
            // only emits "Invalid mode rank" without consuming any real resource.
            SimulacrumDiagnostics.RecordAttackUseAcknowledgementSkipped(
                duplicate,
                mode,
                remainingUses);
            droppedItem = null;
            needToRefreshAttackModes = false;
            ProcessInventoryItemUse(
                duplicate,
                mode,
                proximity,
                hit,
                ref droppedItem,
                ref needToRefreshAttackModes);
            __state = false;

            return false;
        }

        [UsedImplicitly]
        public static void Postfix(
            RulesetCharacterMonster __instance,
            RulesetAttackMode mode,
            AttackProximity proximity,
            bool hit,
            ref RulesetItem droppedItem,
            ref bool needToRefreshAttackModes,
            bool __state)
        {
            if (__state && __instance is RulesetCharacterSimulacrum duplicate)
            {
                ProcessInventoryItemUse(
                    duplicate,
                    mode,
                    proximity,
                    hit,
                    ref droppedItem,
                    ref needToRefreshAttackModes);
            }
        }

        private static void ProcessInventoryItemUse(
            RulesetCharacterSimulacrum duplicate,
            RulesetAttackMode mode,
            AttackProximity proximity,
            bool hit,
            ref RulesetItem droppedItem,
            ref bool needToRefreshAttackModes)
        {
            var sourceItem = mode?.SourceObject as RulesetItem;
            var ammunitionType = string.Empty;
            var ammunitionBefore = -1;
            var ammunitionAfter = -1;

            if (sourceItem == null || proximity != AttackProximity.Range)
            {
                SimulacrumDiagnostics.RecordInventoryAttackUse(
                    duplicate,
                    mode,
                    sourceItem,
                    proximity,
                    hit,
                    droppedItem,
                    ammunitionType,
                    ammunitionBefore,
                    ammunitionAfter,
                    needToRefreshAttackModes);

                return;
            }

            try
            {
                using (duplicate.BeginInventoryMutation())
                {
                    if (mode.Thrown)
                    {
                        ProcessThrownItem(
                            duplicate,
                            mode,
                            ref droppedItem,
                            ref needToRefreshAttackModes);
                    }
                    else
                    {
                        ProcessAmmunition(
                            duplicate,
                            mode,
                            out ammunitionType,
                            out ammunitionBefore,
                            out ammunitionAfter);
                    }
                }
            }
            catch (Exception exception)
            {
                SimulacrumDiagnostics.RecordException(
                    "attack-use",
                    $"inventory:{mode.SourceDefinition?.Name ?? "<null>"}",
                    exception);
            }

            SimulacrumDiagnostics.RecordInventoryAttackUse(
                duplicate,
                mode,
                sourceItem,
                proximity,
                hit,
                droppedItem,
                ammunitionType,
                ammunitionBefore,
                ammunitionAfter,
                needToRefreshAttackModes);
        }

        private static void ProcessThrownItem(
            RulesetCharacterSimulacrum duplicate,
            RulesetAttackMode mode,
            ref RulesetItem droppedItem,
            ref bool needToRefreshAttackModes)
        {
            var inventory = duplicate.CharacterInventory;
            var configurations = inventory?.WieldedItemsConfigurations;
            var configurationIndex = inventory?.CurrentConfiguration ?? -1;

            if (configurations == null ||
                configurations.Count == 0 ||
                configurationIndex < 0 ||
                configurationIndex >= configurations.Count)
            {
                return;
            }

            if (configurationIndex == configurations.Count - 1)
            {
                configurationIndex =
                    configurations[configurationIndex].MainHandSlot.ShadowedSlot !=
                    configurations[0].MainHandSlot
                        ? 1
                        : 0;
            }

            if (configurationIndex < 0 ||
                configurationIndex >= configurations.Count)
            {
                return;
            }

            var configuration = configurations[configurationIndex];
            RulesetInventorySlot wieldedSlot = null;

            if (mode.SlotName == EquipmentDefinitions.SlotTypeMainHand)
            {
                wieldedSlot = configuration.MainHandSlot;
            }
            else if (mode.SlotName == EquipmentDefinitions.SlotTypeOffHand)
            {
                wieldedSlot = configuration.OffHandSlot;
            }

            if (wieldedSlot?.EquipedItem?.ItemDefinition != mode.SourceDefinition)
            {
                return;
            }

            droppedItem = wieldedSlot.EquipedItem;
            wieldedSlot.UnequipItem(true, false);

            if (inventory.InventorySlotsByType.TryGetValue(
                    mode.SlotName,
                    out var slotsByType) &&
                slotsByType.Count > 0)
            {
                slotsByType[0].UnequipItem(true, false);
            }

            var replacement = FindThrownItemReplacement(
                inventory.PersonalContainer,
                droppedItem);

            if (replacement != null)
            {
                inventory.DefineWieldedItemsConfiguration(
                    configurationIndex,
                    replacement,
                    mode.SlotName);
            }

            duplicate.RequestWieldedItemsConfigurationRefresh();
            needToRefreshAttackModes = true;
        }

        private static RulesetItem FindThrownItemReplacement(
            RulesetContainer personalContainer,
            RulesetItem droppedItem)
        {
            if (personalContainer == null || droppedItem?.ItemDefinition == null)
            {
                return null;
            }

            foreach (var slot in personalContainer.InventorySlots)
            {
                var candidate = slot?.EquipedItem;

                if (candidate?.ItemDefinition != droppedItem.ItemDefinition)
                {
                    continue;
                }

                if (slot.SlotTypeDefinition.CanStack &&
                    candidate.ItemDefinition.CanBeStacked &&
                    candidate.StackCount > 1)
                {
                    var replacement = ServiceRepository
                        .GetService<IRulesetItemFactoryService>()
                        ?.CreateStandardItem(candidate.ItemDefinition, true, null);

                    if (replacement != null)
                    {
                        candidate.SpendStack(1);
                    }

                    return replacement;
                }

                slot.UnequipItem(true, false);

                return candidate;
            }

            return null;
        }

        private static void ProcessAmmunition(
            RulesetCharacterSimulacrum duplicate,
            RulesetAttackMode mode,
            out string ammunitionType,
            out int ammunitionBefore,
            out int ammunitionAfter)
        {
            ammunitionType = duplicate.GetAmmunitionType(mode);
            ammunitionBefore = -1;
            ammunitionAfter = -1;

            if (string.IsNullOrEmpty(ammunitionType))
            {
                return;
            }

            var ammunitionSlot =
                duplicate.CharacterInventory.GetCurrentAmmunitionSlot(ammunitionType);
            var ammunition = ammunitionSlot?.EquipedItem;

            if (ammunition == null)
            {
                return;
            }

            ammunitionBefore = ammunition.StackCount;

            if (ammunition.StackCount > 1)
            {
                ammunition.SpendStack(1);
                ammunitionAfter = ammunition.StackCount;
            }
            else
            {
                ammunitionSlot.UnequipItem(true, false);
                ammunitionAfter = 0;
            }
        }
    }

}
