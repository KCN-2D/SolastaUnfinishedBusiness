using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetImplementationManagerPatcher
{
    private static void EnumerateFeatureDefinitionSavingThrowAffinity(
        RulesetCharacter __instance,
        List<FeatureDefinition> featuresToBrowse,
        Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
    {
        __instance.EnumerateFeaturesToBrowse<ISavingThrowAffinityProvider>(featuresToBrowse, featuresOrigin);
        featuresToBrowse.RemoveAll(x =>
            !__instance.IsValid(x.GetAllSubFeaturesOfType<IsCharacterValidHandler>()));
    }

    [HarmonyPatch(typeof(RulesetImplementationManager),
        nameof(RulesetImplementationManager.InstantiateEffectInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateEffectInvocation_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetEffectSpell __result,
            RulesetInvocation invocation)
        {
            //PATCH: setup repertoire for spells cast through invocation
            __result.spellRepertoire ??= invocation.invocationRepertoire;
        }
    }

    //PATCH: allow different algorithms to calculate critical damage
    [HarmonyPatch(typeof(RulesetImplementationManager), nameof(RulesetImplementationManager.ApplyDamageForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyDamageForm_Patch
    {
        // add the sum of the max damage dice of your attack to your total bonus
        private static int RollDamageOption1(
            RulesetActor rulesetActor,
            DamageForm damageForm,
            int addDice,
            int additionalDamage,
            int damageRollReduction,
            float damageMultiplier,
            bool maximumDamage,
            bool useVersatileDamage,
            bool attackModeDamage,
            List<int> rolledValues,
            bool canRerollDice)
        {
            //crunchycrits
            var dieType = useVersatileDamage ? damageForm.VersatileDieType : damageForm.DieType;
            var diceMaxValue = DiceMaxValue[(int)damageForm.dieType];

            if (damageForm.OverrideWithBardicInspirationDie &&
                rulesetActor is RulesetCharacter character &&
                character.GetBardicInspirationDieValue() != DieType.D1)
            {
                dieType = character.GetBardicInspirationDieValue();
            }

            var totalDamage = rulesetActor.RollDiceAndSum(
                dieType,
                attackModeDamage
                    ? RollContext.AttackDamageValueRoll
                    : RollContext.MagicDamageValueRoll,
                damageForm.DiceNumber + addDice,
                rolledValues, canRerollDice, maximumDamage);

            // add additional dices equal with dice max value
            totalDamage += rolledValues.Count * diceMaxValue;
            rolledValues.AddRange(Enumerable.Repeat(diceMaxValue, rolledValues.Count));

            var finalDamage = Mathf.Clamp(totalDamage + damageForm.BonusDamage - damageRollReduction, 0, int.MaxValue);

            return Mathf.FloorToInt(damageMultiplier * (finalDamage + additionalDamage));
        }

        private static int RollDiceKeepRollingMaxAndSum(
            RulesetActor rulesetActor,
            DieType diceType,
            RollContext context,
            int diceNumber,
            ICollection<int> rolledValues = null,
            bool canRerollDice = true,
            string skill = "")
        {
            rulesetActor.EnumerateFeaturesToBrowse<IDieRollModificationProvider>(rulesetActor.featuresToBrowse);

            var maxDie = DiceMaxValue[(int)diceType];
            var total = 0;

            for (var index = 0; index < diceNumber; ++index)
            {
                var roll = maxDie;

                if (maxDie > 1)
                {
                    while (roll == maxDie)
                    {
                        roll = rulesetActor.RollDie(diceType, context, false, AdvantageType.None,
                            out _, out _, false, canRerollDice, skill);
                        rolledValues?.Add(roll);
                        total += roll;
                    }
                }
                else
                {
                    rolledValues?.Add(1);
                    total += 1;
                }
            }

            return total;
        }

        // keep re-rolling any dice that rolls it max value and add it to the total damage
        private static int RollDamageOption2(
            RulesetActor rulesetActor,
            DamageForm damageForm,
            int addDice,
            int additionalDamage,
            int damageRollReduction,
            float damageMultiplier,
            bool useVersatileDamage,
            bool attackModeDamage,
            ICollection<int> rolledValues,
            bool canRerollDice)
        {
            var diceType = useVersatileDamage ? damageForm.VersatileDieType : damageForm.DieType;

            if (damageForm.OverrideWithBardicInspirationDie &&
                rulesetActor is RulesetCharacter character &&
                character.GetBardicInspirationDieValue() != DieType.D1)
            {
                diceType = character.GetBardicInspirationDieValue();
            }

            var totalDamage = RollDiceKeepRollingMaxAndSum(rulesetActor,
                diceType,
                attackModeDamage
                    ? RollContext.AttackDamageValueRoll
                    : RollContext.MagicDamageValueRoll,
                (damageForm.DiceNumber + addDice) * 2, // 2 as it's a critical hit
                rolledValues, canRerollDice);

            return Mathf.FloorToInt(damageMultiplier *
                                    (Mathf.Clamp(totalDamage + damageForm.BonusDamage - damageRollReduction, 0,
                                         int.MaxValue) +
                                     additionalDamage));
        }

        // double your initial rolled results instead of rolling additional critical dice
        private static int RollDamageOption3(
            RulesetActor rulesetActor,
            DamageForm damageForm,
            int addDice,
            int additionalDamage,
            int damageRollReduction,
            float damageMultiplier,
            bool maximumDamage,
            bool useVersatileDamage,
            bool attackModeDamage,
            List<int> rolledValues,
            bool canRerollDice)
        {
            var diceType = useVersatileDamage ? damageForm.VersatileDieType : damageForm.DieType;

            if (damageForm.OverrideWithBardicInspirationDie &&
                rulesetActor is RulesetCharacter character &&
                character.GetBardicInspirationDieValue() != DieType.D1)
            {
                diceType = character.GetBardicInspirationDieValue();
            }

            // different from original game code we roll usual dices and multiply result by 2
            var totalDamage = rulesetActor.RollDiceAndSum(
                diceType,
                attackModeDamage
                    ? RollContext.AttackDamageValueRoll
                    : RollContext.MagicDamageValueRoll,
                damageForm.DiceNumber + addDice,
                rolledValues, canRerollDice, maximumDamage);

            // duplicates the rolled dices
            rolledValues.AddRange([.. rolledValues]);

            // doubles the rolled damage
            damageForm.bonusDamage *= 2;

            return Mathf.FloorToInt(
                damageMultiplier *
                ((2 * totalDamage) + damageForm.BonusDamage - damageRollReduction + additionalDamage));
        }

        private static int GetCriticalHitMode(RulesetActor rulesetActor, bool criticalSuccess)
        {
            if (!criticalSuccess)
            {
                return 0;
            }

            var criticalHitMode = rulesetActor.Side switch
            {
                Side.Ally => Main.Settings.CriticalHitModeAllies,
                Side.Enemy => Main.Settings.CriticalHitModeEnemies,
                Side.Neutral => Main.Settings.CriticalHitModeNeutral,
                _ => 0
            };

            //PATCH: supports Umbral Stalker shadow dance
            if (rulesetActor.HasConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, RoguishUmbralStalker.ConditionShadowDanceAdditionalDice.Name))
            {
                criticalHitMode = 2;
            }

            return criticalHitMode;
        }

        private static int RollDamageValue(
            RulesetActor rulesetActor,
            DamageForm damageForm,
            int addDice,
            bool criticalSuccess,
            int additionalDamage,
            int damageRollReduction,
            float damageMultiplier,
            bool maximumDamage,
            bool useVersatileDamage,
            bool attackModeDamage,
            List<int> rolledValues,
            bool canRerollDice,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            if (rulesetActor is not RulesetCharacter rulesetCharacter)
            {
                return rulesetActor.RollDamage(
                    damageForm, addDice, criticalSuccess, additionalDamage, damageRollReduction, damageMultiplier,
                    maximumDamage, useVersatileDamage, attackModeDamage, rolledValues, canRerollDice);
            }

            if (rulesetCharacter
                .GetSubFeaturesByType<IAllowRerollDice>()
                .Any(x => x.IsValid(rulesetActor, attackModeDamage, damageForm)))
            {
                canRerollDice = true;
            }

            int damage;

            if (!criticalSuccess)
            {
                damage = rulesetActor.RollDamage(
                    damageForm, addDice, false, additionalDamage, damageRollReduction, damageMultiplier,
                    maximumDamage, useVersatileDamage, attackModeDamage, rolledValues, canRerollDice);
            }
            else
            {
                //PATCH: supports different critical damage algorithms
                var criticalHitMode = GetCriticalHitMode(rulesetActor, true);

                damage = criticalHitMode switch
                {
                    1 => RollDamageOption1(
                        rulesetActor, damageForm, addDice, additionalDamage, damageRollReduction, damageMultiplier,
                        maximumDamage, useVersatileDamage, attackModeDamage, rolledValues, canRerollDice),
                    2 => RollDamageOption2(
                        rulesetActor, damageForm, addDice, additionalDamage, damageRollReduction, damageMultiplier,
                        useVersatileDamage, attackModeDamage, rolledValues, canRerollDice),
                    3 => RollDamageOption3(
                        rulesetActor, damageForm, addDice, additionalDamage, damageRollReduction, damageMultiplier,
                        maximumDamage, useVersatileDamage, attackModeDamage, rolledValues, canRerollDice),
                    _ => rulesetActor.RollDamage(
                        damageForm, addDice, true, additionalDamage, damageRollReduction, damageMultiplier,
                        maximumDamage, useVersatileDamage, attackModeDamage, rolledValues, canRerollDice)
                };
            }

            return damage;
        }

        private static int RollDamage(
            RulesetActor rulesetActor,
            DamageForm damageForm,
            int addDice,
            bool criticalSuccess,
            int additionalDamage,
            int damageRollReduction,
            float damageMultiplier,
            bool maximumDamage,
            bool useVersatileDamage,
            bool attackModeDamage,
            List<int> rolledValues,
            bool canRerollDice,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            var rulesetCharacter = rulesetActor as RulesetCharacter;
            int damage;

            if (rulesetCharacter != null &&
                Tabletop2024Context.CanApplySavageAttacker2024(rulesetCharacter, attackModeDamage))
            {
                var originalBonusDamage = damageForm.bonusDamage;
                var firstRolledValues = new List<int>();
                var secondRolledValues = new List<int>();
                var firstDamage = RollDamageValue(
                    rulesetActor,
                    damageForm,
                    addDice,
                    criticalSuccess,
                    additionalDamage,
                    damageRollReduction,
                    damageMultiplier,
                    maximumDamage,
                    useVersatileDamage,
                    attackModeDamage,
                    firstRolledValues,
                    canRerollDice,
                    formsParams);

                damageForm.bonusDamage = originalBonusDamage;

                var secondDamage = RollDamageValue(
                    rulesetActor,
                    damageForm,
                    addDice,
                    criticalSuccess,
                    additionalDamage,
                    damageRollReduction,
                    damageMultiplier,
                    maximumDamage,
                    useVersatileDamage,
                    attackModeDamage,
                    secondRolledValues,
                    canRerollDice,
                    formsParams);

                damageForm.bonusDamage = originalBonusDamage;
                Tabletop2024Context.MarkSavageAttacker2024Used(rulesetCharacter);
                rolledValues.Clear();

                if (secondDamage > firstDamage)
                {
                    damage = secondDamage;
                    rolledValues.AddRange(secondRolledValues);
                }
                else
                {
                    damage = firstDamage;
                    rolledValues.AddRange(firstRolledValues);
                }
            }
            else
            {
                damage = RollDamageValue(
                    rulesetActor,
                    damageForm,
                    addDice,
                    criticalSuccess,
                    additionalDamage,
                    damageRollReduction,
                    damageMultiplier,
                    maximumDamage,
                    useVersatileDamage,
                    attackModeDamage,
                    rolledValues,
                    canRerollDice,
                    formsParams);
            }

            var sourceCharacter = formsParams.sourceCharacter ?? rulesetCharacter;
            var sourceDefinition = formsParams.activeEffect?.SourceDefinition;

            if (sourceCharacter != null && sourceDefinition != null)
            {
                var criticalHitMode = GetCriticalHitMode(rulesetActor, criticalSuccess);

                // Modes 1 and 3 append synthetic critical values for display; "roll" triggers use real rolls only.
                IReadOnlyList<int> actualRolledValues =
                    criticalSuccess && criticalHitMode is 1 or 3
                        ? rolledValues.Take(rolledValues.Count / 2).ToList()
                        : [.. rolledValues];

                foreach (var handler in sourceDefinition.GetAllSubFeaturesOfType<IModifyMagicEffectDamageRoll>())
                {
                    handler.ModifyDamageRoll(
                        sourceCharacter,
                        damageForm,
                        formsParams,
                        criticalSuccess,
                        criticalHitMode,
                        maximumDamage,
                        damageMultiplier,
                        actualRolledValues,
                        rolledValues,
                        canRerollDice,
                        ref damage);
                }
            }

            //BUGFIX: don't allow damage roll reduction to spam across many damage forms
            if (formsParams.actionModifier != null && formsParams.actionModifier.DamageRollReduction != 0)
            {
                if (damage >= formsParams.actionModifier.DamageRollReduction)
                {
                    damage -= formsParams.actionModifier.DamageRollReduction;
                    formsParams.actionModifier.DamageRollReduction = 0;
                }
                else
                {
                    formsParams.actionModifier.DamageRollReduction -= damage;
                    damage = 0;
                }
            }

            //PATCH: supports Sorcerous Wild Magic spell bombardment
            SorcerousWildMagic.HandleSpellBombardment(rulesetCharacter, damageForm, rolledValues, ref damage);

            //PATCH: supports College of Audacity defensive whirl
            CollegeOfAudacity.HandleDefensiveWhirl(rulesetCharacter, damageForm, damage);

            return damage;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var damageRollReductionMethod = typeof(ActionModifier).GetMethod("get_DamageRollReduction");
            var rollDamageMethod = typeof(RulesetActor).GetMethod("RollDamage");
            var myRollDamageMethod =
                new Func<RulesetActor,
                    DamageForm, int, bool, int, int, float, bool, bool, bool, List<int>, bool,
                    RulesetImplementationDefinitions.ApplyFormsParams, int>(
                    RollDamage).Method;

            var getDiceOfRankMethod = typeof(DiceByRank).GetMethod("GetDiceOfRank");
            var myGetDiceOfRankMethod =
                new Func<int, List<DiceByRank>, bool, EffectForm, RulesetImplementationDefinitions.ApplyFormsParams,
                    int>(GetDiceOfRank).Method;
            var myDamageRollReductionMethod = new Func<ActionModifier, int>(GetDamageRollReduction).Method;

            return instructions
                .ReplaceCalls(rollDamageMethod,
                    "RulesetImplementationManager.RollDamage",
                    new CodeInstruction(OpCodes.Ldarg_2),
                    new CodeInstruction(OpCodes.Call, myRollDamageMethod))
                .ReplaceCalls(damageRollReductionMethod,
                    "RulesetImplementationManager.get_DamageRollReduction",
                    new CodeInstruction(OpCodes.Call, myDamageRollReductionMethod))
                .ReplaceCalls(getDiceOfRankMethod,
                    "RulesetImplementationManager.GetDiceOfRank",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_2),
                    new CodeInstruction(OpCodes.Call, myGetDiceOfRankMethod));
        }

        //BUGFIX: let damage roll reduction be handled above after damage is determined and before returned
        private static int GetDamageRollReduction(ActionModifier actionModifier)
        {
            return 0;
        }

        //PATCH: allows dice advancement to play nicely with MULTICLASS
        private static int GetDiceOfRank(
            int rank,
            List<DiceByRank> table,
            bool allowEmptyEntries,
            EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            if (formsParams.activeEffect?.SourceDefinition is not FeatureDefinitionPower featureDefinitionPower)
            {
                return DiceByRank.GetDiceOfRank(rank, table, allowEmptyEntries);
            }

            var character = formsParams.sourceCharacter;
            var klass = character.FindClassHoldingFeature(featureDefinitionPower);
            var klassLevel = character.GetClassLevel(klass);

            rank = effectForm.LevelType switch
            {
                LevelSourceType.ClassLevel => klassLevel,
                LevelSourceType.ClassLevelHalfUp => Mathf.CeilToInt(0.5f * klassLevel),
                LevelSourceType.EffectLevel => formsParams.activeEffect.EffectLevel,
                _ => rank
            };

            return DiceByRank.GetDiceOfRank(rank, table, allowEmptyEntries);
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManager), nameof(RulesetImplementationManager.ApplySummonForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplySummonForm_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            if (TryApplySimulacrumInventoryItem(effectForm, formsParams))
            {
                return false;
            }

            //PATCH: track item that is summoned to inventory
            //usually only items summoned to equipment slots are tracked
            //this code tracks item if it is single item summon and item is marked to be tracked
            //used to properly track items summoned by Inventor

            var summonForm = effectForm.SummonForm;

            if (summonForm.SummonType != SummonForm.Type.InventoryItem
                || !summonForm.ItemDefinition
                || summonForm.Number != 1
                || !summonForm.TrackItem
                || formsParams.targetType != TargetType.Self
                || formsParams.sourceCharacter is not RulesetCharacterHero)
            {
                return true;
            }

            var rulesetItem = ServiceRepository.GetService<IRulesetItemFactoryService>()
                .CreateStandardItem(summonForm.ItemDefinition);

            rulesetItem.SourceSummoningEffectGuid = formsParams.activeEffect.Guid;

            formsParams.sourceCharacter.GrantItem(rulesetItem, false);
            formsParams.activeEffect.TrackSummonedItem(rulesetItem);
            formsParams.sourceCharacter.RefreshAll();

            return false;
        }

        internal static bool TryApplySimulacrumInventoryItem(
            EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            var summonForm = effectForm?.SummonForm;

            if (summonForm?.SummonType != SummonForm.Type.InventoryItem ||
                !summonForm.ItemDefinition ||
                summonForm.Number <= 0 ||
                formsParams.sourceCharacter is not RulesetCharacterSimulacrum duplicate)
            {
                return false;
            }

            if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
            {
                return true;
            }

            if (formsParams.targetType == TargetType.FreeSlot &&
                summonForm.Number == 1)
            {
                ApplyInventoryItemToFreeSlot(summonForm, formsParams, duplicate);
            }
            else if (WishBehavior.ShouldStoreCreatedItemInCasterInventory(
                         formsParams.activeEffect?.SourceDefinition))
            {
                ApplyIndividualInventoryItems(summonForm, formsParams, duplicate);
            }
            else
            {
                ApplyStackedInventoryItems(summonForm, formsParams, duplicate);
            }

            return true;
        }

        private static void ApplyInventoryItemToFreeSlot(
            SummonForm summonForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            RulesetCharacterSimulacrum duplicate)
        {
            var activeEffect = formsParams.activeEffect;
            var inventory = duplicate.CharacterInventory;
            var slotTypes = activeEffect?.EffectDescription?.SlotTypes;

            using (duplicate.BeginInventoryMutation())
            {
                if (slotTypes == null ||
                    inventory?.WieldedItemsConfigurations is not { Count: > 0 } configurations)
                {
                    return;
                }

                var configurationIndex = inventory.CurrentConfiguration < 2
                    ? inventory.CurrentConfiguration
                    : 0;

                if (configurationIndex < 0 ||
                    configurationIndex >= configurations.Count)
                {
                    return;
                }

                foreach (var slotType in slotTypes)
                {
                    RulesetItem item = null;

                    if (slotType == EquipmentDefinitions.SlotTypeMainHand ||
                        slotType == EquipmentDefinitions.SlotTypeOffHand)
                    {
                        var configuration = configurations[configurationIndex];
                        var slot = slotType == EquipmentDefinitions.SlotTypeMainHand
                            ? configuration.MainHandSlot
                            : configuration.OffHandSlot;

                        if (slot?.EquipedItem != null)
                        {
                            continue;
                        }

                        item = CreateSummonedItem(summonForm.ItemDefinition);
                        inventory.DefineWieldedItemsConfiguration(
                            configurationIndex,
                            item,
                            slotType);
                    }
                    else if (inventory.InventorySlotsByName.TryGetValue(
                                 slotType,
                                 out var slot) &&
                             slot.EquipedItem == null)
                    {
                        item = CreateSummonedItem(summonForm.ItemDefinition);
                        slot.EquipItem(item, -1, true, false);
                    }

                    if (item == null)
                    {
                        continue;
                    }

                    duplicate.AcceptItem(item);
                    item.SourceSummoningEffectGuid = activeEffect.Guid;
                    activeEffect.TrackSummonedItem(item);

                    break;
                }
            }
        }

        private static void ApplyStackedInventoryItems(
            SummonForm summonForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            RulesetCharacterSimulacrum duplicate)
        {
            RulesetItem pendingItem = null;

            using (duplicate.BeginInventoryMutation())
            {
                if (!summonForm.ItemDefinition.CanBeStacked)
                {
                    for (var index = 0; index < summonForm.Number; index++)
                    {
                        GrantSummonedItem(
                            duplicate,
                            CreateSummonedItem(summonForm.ItemDefinition),
                            summonForm.TrackItem,
                            formsParams.activeEffect);
                    }

                    return;
                }

                for (var index = 0; index < summonForm.Number; index++)
                {
                    var createItem = pendingItem == null;

                    if (!createItem)
                    {
                        if (pendingItem.StackCount == summonForm.ItemDefinition.StackSize)
                        {
                            GrantSummonedItem(
                                duplicate,
                                pendingItem,
                                summonForm.TrackItem,
                                formsParams.activeEffect);
                            createItem = true;
                        }
                        else
                        {
                            pendingItem.IncreaseStack(1);
                        }
                    }

                    if (createItem)
                    {
                        pendingItem = CreateSummonedItem(summonForm.ItemDefinition);
                    }
                }

                if (pendingItem != null)
                {
                    GrantSummonedItem(
                        duplicate,
                        pendingItem,
                        summonForm.TrackItem,
                        formsParams.activeEffect);
                }
            }
        }

        private static void ApplyIndividualInventoryItems(
            SummonForm summonForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            RulesetCharacterSimulacrum duplicate)
        {
            var droppedItems = new List<RulesetItem>();

            using (duplicate.BeginInventoryMutation())
            {
                for (var index = 0; index < summonForm.Number; index++)
                {
                    var item = CreateSummonedItem(summonForm.ItemDefinition);

                    if (GrantSummonedItem(
                            duplicate,
                            item,
                            summonForm.TrackItem,
                            formsParams.activeEffect))
                    {
                        continue;
                    }

                    item.BearerGuid = 0;
                    droppedItems.Add(item);
                }
            }

            if (droppedItems.Count != 0 &&
                GameLocationCharacter.GetFromActor(duplicate) is { } location &&
                ServiceRepository.GetService<IGameLocationItemService>() is { } itemService)
            {
                itemService.DropLoot(droppedItems, location.LocationPosition);
            }
        }

        private static RulesetItem CreateSummonedItem(ItemDefinition itemDefinition)
        {
            return ServiceRepository.GetService<IRulesetItemFactoryService>()
                .CreateStandardItem(itemDefinition, true, null);
        }

        private static bool GrantSummonedItem(
            RulesetCharacterSimulacrum duplicate,
            RulesetItem item,
            bool trackItem,
            RulesetEffect activeEffect)
        {
            if (trackItem && activeEffect != null)
            {
                item.SourceSummoningEffectGuid = activeEffect.Guid;
            }

            // A tracked item must remain a distinct entity. Autostacking it into an
            // older summon would discard the GUID that the active effect must remove.
            if (!duplicate.GrantItem(item, false, autostack: !trackItem))
            {
                return false;
            }

            if (trackItem)
            {
                activeEffect?.TrackSummonedItem(item);
            }

            return true;
        }
    }


    [HarmonyPatch(typeof(RulesetImplementationManager), nameof(RulesetImplementationManager.TerminateEffect))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TerminateEffect_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetImplementationManager __instance, RulesetEffect activeEffect,
            bool showGraphics)
        {
            foreach (var handler in activeEffect
                         .GetSourceDefinitionSafe()
                         ?.GetAllSubFeaturesOfType<IOnBeforeEffectTerminated>()
                     ?? [])
            {
                handler.OnBeforeEffectTerminated(activeEffect);
            }

            //PATCH:
            // allows for extra careful tracking of summoned items
            // removes tracked items from any character, container or loot pile
            // used for Inventor's item summoning
            TrackItemsCarefully.Process(activeEffect);

            // vanilla code with enumeration protection
            TerminateEffect(__instance, activeEffect, showGraphics);

            //PATCH:
            // Call parts of the stuff `RulesetImplementationManagerLocation` does for `RulesetImplementationManagerCampaign`
            // This makes light and item effects correctly terminate when resting during world travel
            // The code is prettified decompiled code from `RulesetImplementationManagerLocation`
            TerminateLightAndItemEffectsOnWorldTravel(__instance, activeEffect);

            return false;
        }

        // original vanilla code
        private static void TerminateEffect(
            RulesetImplementationManager __instance, RulesetEffect activeEffect, bool showGraphics = true)
        {
            activeEffect.Terminated = true;
            __instance.ClearDamageFormsByIndex();

            if (activeEffect.TrackedConditionGuids.Count > 0)
            {
                __instance.conditionGuidsToProcess.AddRange(activeEffect.TrackedConditionGuids);

                foreach (var guid in __instance.conditionGuidsToProcess.ToArray())
                {
                    RulesetCondition rulesetCondition = null;
                    ref var local = ref rulesetCondition;

                    if (!RulesetEntity.TryGetEntity(guid, out local))
                    {
                        continue;
                    }

                    if (RulesetEntity.TryGetEntity(rulesetCondition.TargetGuid, out RulesetCharacter entity))
                    {
                        entity.ConditionRemoved -= activeEffect.ConditionRemoved;
                    }
                }

                foreach (var guid in __instance.conditionGuidsToProcess.ToArray())
                {
                    RulesetCondition rulesetCondition = null;
                    ref var local = ref rulesetCondition;

                    if (!RulesetEntity.TryGetEntity(guid, out local))
                    {
                        continue;
                    }

                    rulesetCondition.EndOfDurationReached = activeEffect.EndOfDurationReached;

                    if (!RulesetEntity.TryGetEntity(rulesetCondition.TargetGuid, out RulesetCharacter entity1))
                    {
                        continue;
                    }

                    entity1.RemoveCondition(rulesetCondition, showGraphics: showGraphics);

                    var entity2 = RulesetEntity.GetEntity<RulesetCharacter>(rulesetCondition.SourceGuid);
                    var conditionTrackingStopped = activeEffect.ConditionTrackingStopped;

                    conditionTrackingStopped?.Invoke(
                        entity2, entity1, activeEffect.EffectDescription, rulesetCondition, showGraphics);
                }

                activeEffect.TrackedConditionGuids.Clear();
                __instance.conditionGuidsToProcess.Clear();
            }

            if (activeEffect.TrackedSummonedItemGuids.Count <= 0)
            {
                return;
            }

            __instance.summonedItemGuidsToProcess.AddRange(activeEffect.TrackedSummonedItemGuids);

            foreach (var guid in __instance.summonedItemGuidsToProcess.ToArray())
            {
                RulesetItem rulesetItem = null;
                ref var local = ref rulesetItem;

                if (RulesetEntity.TryGetEntity(guid, out local))
                {
                    rulesetItem.ItemDestroyed -= activeEffect.ItemDestroyed;
                }
            }

            foreach (var guid in __instance.summonedItemGuidsToProcess.ToArray())
            {
                RulesetItem itemToLose = null;
                ref var local = ref itemToLose;

                if (!RulesetEntity.TryGetEntity(guid, out local) ||
                    !RulesetEntity.TryGetEntity(activeEffect.SourceGuid, out RulesetCharacter entity))
                {
                    continue;
                }

                entity.LoseItem(itemToLose);
                itemToLose.Unregister();
            }

            activeEffect.TrackedSummonedItemGuids.Clear();
            __instance.summonedItemGuidsToProcess.Clear();
        }

        [UsedImplicitly]
        private static void TerminateLightAndItemEffectsOnWorldTravel(
            RulesetImplementationManager __instance, RulesetEffect activeEffect)
        {
            if (__instance is not RulesetImplementationManagerCampaign)
            {
                return;
            }

            if (activeEffect is { TrackedLightSourceGuids.Count: > 0 })
            {
                var service = ServiceRepository.GetService<IGameLocationVisibilityService>();

                foreach (var trackedLightSourceGuid in activeEffect.TrackedLightSourceGuids.ToArray())
                {
                    RulesetLightSource rulesetLightSource = null;
                    ref var local = ref rulesetLightSource;

                    if (!RulesetEntity.TryGetEntity(trackedLightSourceGuid, out local) || rulesetLightSource == null)
                    {
                        continue;
                    }

                    rulesetLightSource.LightSourceExtinguished -= activeEffect.LightSourceExtinguished;

                    RulesetCharacter bearer;

                    if (rulesetLightSource.TargetItemGuid != 0UL &&
                        RulesetEntity.TryGetEntity(rulesetLightSource.TargetItemGuid, out RulesetItem rulesetItem))
                    {
                        if (RulesetEntity.TryGetEntity(rulesetItem.BearerGuid, out bearer) &&
                            bearer is { CharacterInventory: not null })
                        {
                            bearer.CharacterInventory.ItemAltered?.Invoke(bearer.CharacterInventory,
                                bearer.CharacterInventory.FindSlotHoldingItem(rulesetItem), rulesetItem);
                        }

                        var fromActor = GameLocationCharacter.GetFromActor(bearer);

                        if (fromActor != null)
                        {
                            service?.RemoveCharacterLightSource(fromActor, rulesetItem.RulesetLightSource);
                        }

                        rulesetItem.RulesetLightSource?.Unregister();
                        rulesetItem.RulesetLightSource = null;
                    }
                    else if (rulesetLightSource.TargetGuid != 0UL &&
                             RulesetEntity.TryGetEntity(rulesetLightSource.TargetGuid, out bearer))
                    {
                        var fromActor = GameLocationCharacter.GetFromActor(bearer);

                        if (fromActor != null)
                        {
                            service?.RemoveCharacterLightSource(fromActor, rulesetLightSource);
                        }

                        if (rulesetLightSource.UseSpecificLocationPosition)
                        {
                            if (bearer is RulesetCharacterEffectProxy proxy)
                            {
                                proxy.RemoveAdditionalPersonalLightSource(rulesetLightSource);
                            }
                        }
                        else if (bearer != null)
                        {
                            bearer.PersonalLightSource = null;
                        }
                    }
                }

                activeEffect.TrackedLightSourceGuids.Clear();
            }

            if (activeEffect is not { TrackedItemPropertyGuids.Count: > 0 })
            {
                return;
            }

            foreach (var itemPropertyGuid in activeEffect.TrackedItemPropertyGuids.ToArray())
            {
                RulesetItemProperty rulesetItemProperty = null;
                ref var local = ref rulesetItemProperty;

                if (!RulesetEntity.TryGetEntity(itemPropertyGuid, out local) || rulesetItemProperty == null)
                {
                    continue;
                }

                if (!RulesetEntity.TryGetEntity(rulesetItemProperty.TargetItemGuid,
                        out RulesetItem rulesetItem) || rulesetItem == null)
                {
                    continue;
                }

                rulesetItem.ItemPropertyRemoved -= activeEffect.ItemPropertyRemoved;
                rulesetItem.RemoveDynamicProperty(rulesetItemProperty);

                if (!RulesetEntity.TryGetEntity(rulesetItem.BearerGuid,
                        out RulesetCharacter rulesetItemBearer) || rulesetItemBearer == null)
                {
                    continue;
                }

                var characterInventory = rulesetItemBearer.CharacterInventory;

                characterInventory?.ItemAltered?.Invoke(characterInventory,
                    characterInventory.FindSlotHoldingItem(rulesetItem),
                    rulesetItem);

                rulesetItemBearer.RefreshAll();
            }
        }
    }

    [HarmonyPatch(
        typeof(RulesetImplementationManager),
        nameof(RulesetImplementationManager.ApplyAlterationForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyAlterationForm_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            var alteration = effectForm?.AlterationForm;

            if (formsParams.sourceCharacter is not
                    RulesetCharacterSimulacrum duplicate ||
                alteration?.AlterationType != AlterationForm.Type.AbilityScoreIncrease)
            {
                return true;
            }

            var attribute = duplicate.GetAttribute(
                alteration.AbilityScore,
                false);

            if (attribute == null)
            {
                Trace.LogWarning(
                    $"Unable to apply a Simulacrum ability score increase: " +
                    $"attribute '{alteration.AbilityScore}' was not found.");

                return false;
            }

            attribute.BaseValue += alteration.ValueIncrease;
            attribute.MaxValue += alteration.MaximumIncrease;
            attribute.MaxEditableValue += alteration.MaximumIncrease;
            attribute.Refresh(false);

            duplicate.AbilityScoreIncreased?.Invoke(
                duplicate,
                alteration.AbilityScore,
                alteration.ValueIncrease,
                alteration.MaximumIncrease);
            duplicate.RefreshAll();

            return false;
        }
    }

    //PATCH: handles Sorcerer wildshape scenarios / enforces sorcerer class level / correctly handle slots recovery scenarios
    [HarmonyPatch(typeof(RulesetImplementationManager), nameof(RulesetImplementationManager.ApplySpellSlotsForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplySpellSlotsForm_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            var resourceCharacter = formsParams.sourceCharacter is RulesetCharacterSimulacrum
                ? formsParams.sourceCharacter
                : formsParams.sourceCharacter?.GetOriginalHero();

            // this shouldn't happen so passing the problem back to original game code
            if (resourceCharacter == null)
            {
                return true;
            }

            var spellSlotsForm = effectForm.SpellSlotsForm;

            switch (spellSlotsForm.Type)
            {
                case SpellSlotsForm.EffectType.RecoverHalfLevelUp
                    when SharedSpellsContext.RecoverySlots.TryGetValue(formsParams.activeEffect.Name,
                        out var invokerClass) && invokerClass is CharacterClassDefinition characterClassDefinition:
                {
                    foreach (var spellRepertoire in resourceCharacter.SpellRepertoires)
                    {
                        var currentValue = 0;

                        if (spellRepertoire.SpellCastingClass == characterClassDefinition)
                        {
                            currentValue = resourceCharacter.GetClassLevel(
                                characterClassDefinition);
                        }
                        else if (spellRepertoire.SpellCastingSubclass)
                        {
                            var characterClass = LevelUpHelper.GetClassForSubclass(
                                spellRepertoire.SpellCastingSubclass);

                            if (characterClass == characterClassDefinition)
                            {
                                currentValue = resourceCharacter.GetClassLevel(
                                    characterClassDefinition);
                            }
                        }

                        if (currentValue <= 0)
                        {
                            continue;
                        }

                        if (ServiceRepository.GetService<IPlayerControllerService>()?
                                .ActivePlayerController?
                                .IsCharacterControlled(resourceCharacter) == false)
                        {
                            break;
                        }

                        var slotsCapital = (currentValue % 2) + (currentValue / 2);

                        Gui.GuiService.GetScreen<SlotRecoveryModal>().ShowSlotRecovery(
                            resourceCharacter,
                            formsParams.activeEffect.SourceDefinition.Name,
                            spellRepertoire,
                            slotsCapital,
                            spellSlotsForm.MaxSlotLevel);

                        break;
                    }

                    break;
                }
                //
                // handles Sorcerer and Wildshape scenarios slots / points scenarios
                //
                case SpellSlotsForm.EffectType.CreateSpellSlot or SpellSlotsForm.EffectType.CreateSorceryPoints:
                {
                    if (ServiceRepository.GetService<IPlayerControllerService>()?
                            .ActivePlayerController?
                            .IsCharacterControlled(resourceCharacter) == false)
                    {
                        break;
                    }

                    var spellRepertoire = resourceCharacter.SpellRepertoires.Find(
                        sr => sr.SpellCastingClass == Sorcerer);

                    if (spellRepertoire == null)
                    {
                        break;
                    }

                    Gui.GuiService.GetScreen<FlexibleCastingModal>().ShowFlexibleCasting(
                        resourceCharacter,
                        spellRepertoire,
                        spellSlotsForm.Type == SpellSlotsForm.EffectType.CreateSpellSlot);
                    break;
                }
                case SpellSlotsForm.EffectType.GainSorceryPoints:
                    resourceCharacter.GainSorceryPoints(spellSlotsForm.SorceryPointsGain);
                    break;
                case SpellSlotsForm.EffectType.RecovererSorceryHalfLevelUp:
                {
                    var currentValue = resourceCharacter.GetClassLevel(Sorcerer);
                    var sorceryPointsGain = (currentValue % 2) + (currentValue / 2);

                    resourceCharacter.GainSorceryPoints(sorceryPointsGain);
                    break;
                }
                case (SpellSlotsForm.EffectType)ExtraEffectType.RecoverSorceryHalfLevelDown:
                {
                    var currentValue = resourceCharacter.GetClassLevel(Sorcerer);
                    var sorceryPointsGain = currentValue / 2;

                    resourceCharacter.GainSorceryPoints(sorceryPointsGain);
                    break;
                }
                case SpellSlotsForm.EffectType.RechargePower
                    when formsParams.targetCharacter is RulesetCharacter targetCharacter:
                {
                    foreach (var usablePower in targetCharacter.UsablePowers.Where(usablePower =>
                                 usablePower.PowerDefinition == spellSlotsForm.PowerDefinition))
                    {
                        usablePower.Recharge();
                    }

                    break;
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManager),
        nameof(RulesetImplementationManager.IsValidContextForRestrictedContextProvider))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsValidContextForRestrictedContextProvider_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: support for shield counting as melee
            //replaces calls to ItemDefinition's isWeapon and WeaponDescription getter with custom ones that account for shield
            var weaponDescription = typeof(ItemDefinition).GetMethod("get_WeaponDescription");
            var isWeapon = typeof(ItemDefinition).GetMethod("get_IsWeapon");
            var customWeaponDescription =
                new Func<ItemDefinition, WeaponDescription>(ShieldAttack.EnhancedWeaponDescription).Method;

            //PATCH: support for AddTagToWeapon
            var weaponTags = typeof(WeaponDescription)
                .GetProperty(nameof(WeaponDescription.WeaponTags))
                ?.GetGetMethod();
            var customWeaponTags = new Func<
                WeaponDescription,
                RulesetCharacter,
                RulesetAttackMode,
                List<string>
            >(AddTagToWeapon.GetCustomWeaponTags).Method;
            var customIsWeapon = new Func<ItemDefinition, bool>(ShieldAttack.IsWeaponOrShield).Method;

            return instructions
                .ReplaceCalls(weaponDescription,
                    "RulesetImplementationManager.IsValidContextForRestrictedContextProvider.WeaponDescription",
                    new CodeInstruction(OpCodes.Call, customWeaponDescription))
                .ReplaceCalls(weaponTags,
                    "RulesetImplementationManager.IsValidContextForRestrictedContextProvider.WeaponTags",
                    new CodeInstruction(OpCodes.Ldarg_2),
                    new CodeInstruction(OpCodes.Ldarg, 5),
                    new CodeInstruction(OpCodes.Call, customWeaponTags))
                .ReplaceCalls(isWeapon,
                    "RulesetImplementationManager.IsValidContextForRestrictedContextProvider.IsWeapon",
                    new CodeInstruction(OpCodes.Call, customIsWeapon));
        }

        [UsedImplicitly]
        public static void Postfix(ref bool __result,
            IRestrictedContextProvider provider,
            RulesetCharacter character,
            ItemDefinition itemDefinition,
            bool rangedAttack,
            RulesetAttackMode attackMode,
            RulesetEffect rulesetEffect)
        {
            //PATCH: support for `IValidateContextInsteadOfRestrictedProperty` feature
            __result = RestrictedContextValidatorPatch.ModifyResult(__result, provider, character, itemDefinition,
                rangedAttack, attackMode, rulesetEffect);
        }
    }

    //PATCH: allow ISavingThrowAffinityProvider to be validated with IsCharacterValidHandler
    [HarmonyPatch(typeof(RulesetImplementationManager), nameof(RulesetImplementationManager.TryRollSavingThrow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TryRollSavingThrow_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var rollSavingThrowMethod = typeof(RulesetActor).GetMethod("RollSavingThrow");
            var myRollSavingThrowMethod = typeof(TryRollSavingThrow_Patch).GetMethod("RollSavingThrow");
            //PATCH: make ISpellCastingAffinityProvider from dynamic item properties apply to repertoires
            return instructions
                .ReplaceCalls(rollSavingThrowMethod,
                    "RulesetImplementationManager.TryRollSavingThrow.RollSavingThrow",
                    new CodeInstruction(OpCodes.Ldarg, 1),
                    new CodeInstruction(OpCodes.Ldarg, 13),
                    new CodeInstruction(OpCodes.Call, myRollSavingThrowMethod))
                .ReplaceEnumerateFeaturesToBrowse<ISavingThrowAffinityProvider>(
                    "RulesetImplementationManager.TryRollSavingThrow.EnumerateFeatureDefinitionSavingThrowAffinity",
                    EnumerateFeatureDefinitionSavingThrowAffinity);
        }

        [UsedImplicitly]
        public static void RollSavingThrow(
            RulesetCharacter __instance,
            int saveBonus,
            string abilityScoreName,
            BaseDefinition sourceDefinition,
            List<TrendInfo> modifierTrends,
            List<TrendInfo> advantageTrends,
            int rollModifier,
            int saveDC,
            bool hasHitVisual,
            ref RollOutcome outcome,
            ref int outcomeDelta,
            RulesetCharacter caster,
            List<EffectForm> effectForms)
        {
            __instance.MyRollSavingThrow(
                caster,
                saveBonus,
                abilityScoreName,
                sourceDefinition,
                modifierTrends,
                advantageTrends,
                rollModifier,
                saveDC,
                hasHitVisual,
                ref outcome,
                ref outcomeDelta,
                effectForms);
        }

        [UsedImplicitly]
        public static bool Prefix(
            RulesetCharacter caster,
            RulesetActor target,
            BaseDefinition sourceDefinition)
        {
            //PATCH: illusionary spells against creatures with True Sight should automatically save
            if (!Main.Settings.IllusionSpellsAutomaticallyFailAgainstTrueSightInRange ||
                target is not RulesetCharacter targetCharacter ||
                sourceDefinition is not
                    SpellDefinition { SchoolOfMagic: SchoolIllusion, EffectDescription.TargetSide: Side.Enemy } ||
                sourceDefinition == DatabaseHelper.SpellDefinitions.Silence)
            {
                return true;
            }

            var glCaster = GameLocationCharacter.GetFromActor(caster);
            var glTarget = GameLocationCharacter.GetFromActor(targetCharacter);

            if (glCaster == null || glTarget == null)
            {
                return true;
            }

            var senseMode = targetCharacter.SenseModes
                .FirstOrDefault(x => x.SenseType == SenseMode.Type.Truesight);

            return senseMode == null || !glTarget.IsWithinRange(glCaster, senseMode.SenseRange);
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManager), nameof(RulesetImplementationManager.ApplyConditionForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyConditionForm_Patch
    {
        private static readonly List<CodeInstruction> MatchPattern =
        [
            new(OpCodes.Ldarg_2),
            new(OpCodes.Ldfld, typeof(RulesetImplementationDefinitions.ApplyFormsParams).GetField("activeEffect")),
            new(OpCodes.Brtrue),
            new(OpCodes.Ldloc_1),
            new(OpCodes.Callvirt, typeof(ConditionDefinition).GetMethod("get_SpecialDuration")),
            new(OpCodes.Brfalse),
            new(OpCodes.Ldarg_1),
            new(OpCodes.Callvirt, typeof(OverrideSavingThrowInfo).GetMethod("get_OverrideSavingThrowInfo")),
            new(OpCodes.Brfalse_S)
        ];

        private static bool CompareInstructions(List<CodeInstruction> codes)
        {
            for (var i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != MatchPattern[i].opcode)
                {
                    return false;
                }

                if (MatchPattern[i].operand is null || codes[i].OperandIs(MatchPattern[i].operand))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        // delete the first check of 
        // if (formsParams.activeEffect == null && conditionDefinition.SpecialDuration && effectForm.OverrideSavingThrowInfo != null)
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var flag = false;

            var codes = new List<CodeInstruction>(instructions);

            for (var i = 0; i < codes.Count - MatchPattern.Count + 1; i++)
            {
                if (!CompareInstructions(codes.GetRange(i, MatchPattern.Count)))
                {
                    continue;
                }

                codes[i].opcode = OpCodes.Nop;
                codes.RemoveRange(i + 1, 2);
                flag = true;

                break;
            }

            if (!flag)
            {
                Main.Error("Failed to apply transpiler patch [RulesetImplementationManager.ApplyConditionForm]!");
            }

            return codes.AsEnumerable();
        }
    }
}
