using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GraphicsCharacterFactoryManagerPatcher
{
    private static readonly MethodInfo GetAttackAnimationFromWeaponDefinition =
        AccessTools.Method(
            typeof(GraphicsCharacter),
            "GetAttackAnimationFromWeaponDefinition",
            [
                typeof(WeaponTypeDefinition),
                typeof(bool),
                typeof(ActionModifier),
                typeof(bool).MakeByRefType()
            ]);

    private static readonly MethodInfo RefreshAllMorphotypeParameters =
        AccessTools.Method(
            typeof(GraphicsCharacterFactoryManager),
            "RefreshAllMorphotypeParameters",
            [typeof(GraphicsCharacter)]);

    private static readonly ConditionalWeakTable<
        GraphicsCharacterMonster,
        InventoryAppearanceFinalizationState> InventoryAppearanceFinalizationStates = new();

    internal static int GetInventoryAppearanceFinalizationGeneration(
        GraphicsCharacterMonster graphicsCharacter)
    {
        return graphicsCharacter != null &&
               InventoryAppearanceFinalizationStates.TryGetValue(
                   graphicsCharacter,
                   out var state)
            ? state.Generation
            : 0;
    }

    internal static bool TryGetInventoryAppearanceFinalizationResult(
        GraphicsCharacterMonster graphicsCharacter,
        int previousGeneration,
        out bool succeeded,
        out string failedStages)
    {
        succeeded = false;
        failedStages = "<not-observed>";

        if (graphicsCharacter == null ||
            !InventoryAppearanceFinalizationStates.TryGetValue(
                graphicsCharacter,
                out var state) ||
            state.Generation <= previousGeneration)
        {
            return false;
        }

        succeeded = state.Succeeded;
        failedStages = state.FailedStages;

        return true;
    }

    internal static bool ApplySimulacrumWeaponStance(
        GraphicsCharacterMonster graphicsCharacter,
        string context)
    {
        if (graphicsCharacter?.RulesetCharacterMonster is not
            RulesetCharacterSimulacrum duplicate)
        {
            return false;
        }

        var animationTag = AnimationDefinitions.WeaponTypes[0];
        ItemDefinition itemDefinition = null;
        var oneHandedVersatile = false;
        var slotName = EquipmentDefinitions.SlotTypeMainHand;

        if (!TryGetEquippedWeapon(
                duplicate,
                slotName,
                out var equippedWeapon,
                out var weaponType))
        {
            slotName = EquipmentDefinitions.SlotTypeOffHand;
            TryGetEquippedWeapon(
                duplicate,
                slotName,
                out equippedWeapon,
                out weaponType);
        }

        if (equippedWeapon != null && weaponType != null)
        {
            itemDefinition = equippedWeapon;
            animationTag = weaponType.AnimationTag;
            oneHandedVersatile = IsVersatileWeaponUsedInOneHand(
                duplicate,
                equippedWeapon,
                slotName);

            if (oneHandedVersatile &&
                AnimationDefinitions.VersatileWeaponTypesTable.TryGetValue(
                    animationTag,
                    out var oneHandedAnimationTag))
            {
                animationTag = oneHandedAnimationTag;
            }
        }

        graphicsCharacter.WeaponType = animationTag;
        SimulacrumDiagnostics.RecordWeaponStance(
            duplicate,
            itemDefinition,
            animationTag,
            oneHandedVersatile,
            context,
            graphicsCharacter.UseGameplayController);

        return true;
    }

    private static bool TryGetEquippedWeapon(
        RulesetCharacterSimulacrum duplicate,
        string slotName,
        out ItemDefinition itemDefinition,
        out WeaponTypeDefinition weaponType)
    {
        itemDefinition = null;
        weaponType = null;

        if (duplicate?.CharacterInventory?.InventorySlotsByName.TryGetValue(
                slotName,
                out var slot) != true ||
            slot.EquipedItem?.ItemDefinition is not
            {
                IsWeapon: true,
                WeaponDescription: { WeaponTypeDefinition: { } equippedWeaponType }
            } equippedWeapon)
        {
            return false;
        }

        itemDefinition = equippedWeapon;
        weaponType = equippedWeaponType;

        return true;
    }

    [HarmonyPatch(typeof(GraphicsCharacterMonster), "CheckHasWeapon")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckHasWeapon_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GraphicsCharacterMonster __instance)
        {
            return !ApplySimulacrumWeaponStance(__instance, "check-has-weapon");
        }
    }

    [HarmonyPatch(typeof(CharacterViewport), "GraphicsCharacterCreated")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterViewportGraphicsCharacterCreated_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacter __0)
        {
            if (__0 is GraphicsCharacterMonster
                {
                    RulesetCharacterMonster: RulesetCharacterSimulacrum duplicate
                } graphicsCharacter)
            {
                ApplySimulacrumWeaponStance(graphicsCharacter, "inventory-viewport");
                SimulacrumDiagnostics.RecordGraphicsAppearance(
                    duplicate,
                    graphicsCharacter,
                    "inventory-viewport-created");
            }
        }
    }

    [HarmonyPatch(
        typeof(GraphicsCharacterMonster),
        nameof(GraphicsCharacterMonster.GetAttackAnimationData))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetAttackAnimationData_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GraphicsCharacterMonster __instance,
            [HarmonyArgument(0)] RulesetAttackMode attackMode,
            [HarmonyArgument(1)] ActionModifier attackModifier,
            [HarmonyArgument(2)] ref bool isThrown,
            [HarmonyArgument(3)] ref bool leftHand,
            [HarmonyArgument(4)] ref bool isMultiattack,
            [HarmonyArgument(5)] ref int randomMonkAttackId,
            ref string __result)
        {
            if (__instance?.RulesetCharacterMonster is not
                    RulesetCharacterSimulacrum duplicate ||
                attackMode?.SourceDefinition is not ItemDefinition
                {
                    IsWeapon: true,
                    WeaponDescription: { WeaponTypeDefinition: { } weaponType }
                })
            {
                return true;
            }

            try
            {
                var oneHandedVersatile = IsVersatileWeaponUsedInOneHand(
                    duplicate,
                    (ItemDefinition)attackMode.SourceDefinition,
                    attackMode.SlotName);
                object[] invocationArguments =
                [
                    weaponType,
                    oneHandedVersatile,
                    attackModifier,
                    isThrown
                ];

                __result = (string)GetAttackAnimationFromWeaponDefinition.Invoke(
                    __instance,
                    invocationArguments);
                isThrown = (bool)invocationArguments[3];
                leftHand = !weaponType.IsBow &&
                           attackMode.SlotName == EquipmentDefinitions.SlotTypeOffHand;
                isMultiattack = false;
                randomMonkAttackId = 0;
                SimulacrumDiagnostics.RecordAttackAnimation(
                    duplicate,
                    attackMode,
                    weaponType,
                    __result,
                    oneHandedVersatile);

                return false;
            }
            catch (Exception exception)
            {
                var rootException =
                    exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception;

                SimulacrumDiagnostics.RecordException(
                    "animation",
                    $"weapon-resolution:{attackMode.SourceDefinition?.Name ?? "<null>"}",
                    rootException);

                return true;
            }
        }
    }

    private static bool IsVersatileWeaponUsedInOneHand(
        RulesetCharacterSimulacrum duplicate,
        ItemDefinition itemDefinition,
        string slotName)
    {
        if (duplicate?.CharacterInventory == null ||
            itemDefinition?.WeaponDescription?.WeaponTags.Contains(
                TagsDefinitions.WeaponTagVersatile) != true)
        {
            return false;
        }

        var oppositeSlotName =
            slotName == EquipmentDefinitions.SlotTypeOffHand
                ? EquipmentDefinitions.SlotTypeMainHand
                : EquipmentDefinitions.SlotTypeOffHand;

        // A weapon in the off-hand slot is necessarily used one-handed, even when
        // the main hand is empty. A main-hand versatile weapon switches to its
        // one-handed animation only when the opposite hand is occupied.
        return slotName == EquipmentDefinitions.SlotTypeOffHand ||
               duplicate.CharacterInventory.InventorySlotsByName.TryGetValue(
                   oppositeSlotName,
                   out var oppositeSlot) &&
               oppositeSlot.EquipedItem != null;
    }

    [HarmonyPatch(
        typeof(GraphicsCharacterFactoryManager),
        nameof(GraphicsCharacterFactoryManager.InstantiateBodyPartsForMonsterAsync),
        typeof(GraphicsCharacterMonster),
        typeof(bool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateBodyPartsForMonsterAsync_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GraphicsCharacterFactoryManager __instance,
            GraphicsCharacterMonster __0,
            bool __1,
            ref IEnumerator __result)
        {
            if (__0?.RulesetCharacterMonster is not RulesetCharacterSimulacrum duplicate ||
                !SimulacrumBehavior.UsesInventoryAppearance(duplicate))
            {
                return true;
            }

            // Native body-part loading mutates the monster asset-prefix fields to the
            // selected morphotype-expanded names. Those expanded names are not valid
            // namespaces for a later inventory preview request. Restore the canonical
            // race/subrace/sex prefixes before every world or preview render.
            SimulacrumBehavior.PrepareInventoryAppearance(duplicate);

            SimulacrumDiagnostics.RecordAppearance(
                duplicate,
                "inventory-body-parts-requested",
                "inventory");
            __result = __instance.InstantiateBodyPartsFromInventoryAsync(__0, __1);

            return false;
        }
    }

    [HarmonyPatch(
        typeof(GraphicsCharacterFactoryManager),
        nameof(GraphicsCharacterFactoryManager.RefreshGraphicsCharacter),
        typeof(GraphicsCharacterMonster),
        typeof(Action))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshGraphicsCharacterMonster_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            GraphicsCharacterFactoryManager __instance,
            [HarmonyArgument(0)] GraphicsCharacterMonster graphicsCharacter,
            [HarmonyArgument(1)] ref Action refreshed)
        {
            if (graphicsCharacter?.RulesetCharacterMonster is not
                    RulesetCharacterSimulacrum duplicate ||
                !SimulacrumBehavior.UsesInventoryAppearance(duplicate))
            {
                return;
            }

            var nativeCallback = refreshed;

            refreshed = () =>
            {
                var succeeded = false;
                var failedStages = "finalization-wrapper";

                try
                {
                    (succeeded, failedStages) =
                        FinalizeInventoryAppearance(__instance, graphicsCharacter, duplicate);
                }
                catch (Exception exception)
                {
                    SimulacrumDiagnostics.RecordException(
                        "appearance-finalization",
                        "finalization-wrapper",
                        exception);
                }
                finally
                {
                    var state = InventoryAppearanceFinalizationStates.GetValue(
                        graphicsCharacter,
                        _ => new InventoryAppearanceFinalizationState());

                    state.Generation++;
                    state.Succeeded = succeeded;
                    state.FailedStages = failedStages;
                    nativeCallback?.Invoke();
                }
            };
        }
    }

    private static (bool Succeeded, string FailedStages) FinalizeInventoryAppearance(
        GraphicsCharacterFactoryManager graphicsFactory,
        GraphicsCharacterMonster graphicsCharacter,
        RulesetCharacterSimulacrum duplicate)
    {
        var failedStages = new List<string>();

        // The native monster refresh stops after ApplyLayer/RefreshRenderers, while its
        // initial humanoid creation also performs these three finalization steps. The
        // inventory body-part substitution above needs the same tail so newly created
        // hands and forearms receive the Simulacrum's morphotype colors.
        TryFinalize("update-lods", graphicsCharacter.UpdateLods);
        TryFinalize("setup-after-equipment", graphicsCharacter.SetupAfterEquipment);
        TryFinalize(
            "refresh-morphotypes",
            () =>
            {
                if (RefreshAllMorphotypeParameters == null)
                {
                    throw new MissingMethodException(
                        typeof(GraphicsCharacterFactoryManager).FullName,
                        "RefreshAllMorphotypeParameters");
                }

                RefreshAllMorphotypeParameters.Invoke(
                    graphicsFactory,
                    [graphicsCharacter]);
            });
        SimulacrumDiagnostics.RecordGraphicsAppearance(
            duplicate,
            graphicsCharacter,
            failedStages.Count == 0
                ? "inventory-refresh-finalized"
                : "inventory-refresh-partial-failed");

        return (
            failedStages.Count == 0,
            failedStages.Count == 0
                ? "<none>"
                : string.Join(",", failedStages));

        void TryFinalize(string stage, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                var rootException =
                    exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception;

                failedStages.Add(stage);
                SimulacrumDiagnostics.RecordException(
                    "appearance-finalization",
                    stage,
                    rootException);
            }
        }
    }

    private sealed class InventoryAppearanceFinalizationState
    {
        internal string FailedStages { get; set; } = "<not-observed>";
        internal int Generation { get; set; }
        internal bool Succeeded { get; set; }
    }

    [HarmonyPatch(typeof(GraphicsCharacterFactoryManager),
        nameof(GraphicsCharacterFactoryManager.InstantiateWieldedItemAsNeeded))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateWieldedItemAsNeeded_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacter graphicsCharacter,
            RulesetItem rulesetItem,
            string slotName)
        {
            //PATCH: Support for custom scaling of equipped items.
            //Used to scale reach weapons and hand crossbows
            var feature = rulesetItem.itemDefinition?.GetFirstSubFeatureOfType<CustomScale>()
                          ?? rulesetItem.itemDefinition?.WeaponDescription?.WeaponTypeDefinition
                              .GetFirstSubFeatureOfType<CustomScale>();

            if (feature == null)
            {
                return;
            }

            var flag = rulesetItem.ItemDefinition.IsArmor &&
                       rulesetItem.ItemDefinition.ArmorDescription.ArmorType == "ShieldType";

            AnimationDefinitions.BoneType boneType;

            if (rulesetItem.ItemDefinition.IsWeapon)
            {
                boneType = slotName != EquipmentDefinitions.SlotTypeOffHand
                    ? rulesetItem.ItemDefinition.WeaponDescription.WeaponTypeDefinition.IsAttachedToBone
                    : AnimationDefinitions.BoneType.Prop2;
            }
            else if (flag)
            {
                boneType = AnimationDefinitions.BoneType.Shield;
            }
            else if (slotName == EquipmentDefinitions.SlotTypeMainHand)
            {
                boneType = AnimationDefinitions.BoneType.Prop1;
            }
            else if (slotName == EquipmentDefinitions.SlotTypeOffHand)
            {
                boneType = AnimationDefinitions.BoneType.Prop2;
            }
            else
            {
                return;
            }


            var boneTransform = graphicsCharacter.GetBoneTransform(boneType);

            if (!boneTransform)
            {
                return;
            }

            var transform = boneTransform.Find(rulesetItem.Name);

            if (!transform)
            {
                return;
            }

            var scale = transform.localScale;

            scale.x *= feature.X;
            scale.y *= feature.Y;
            scale.z *= feature.Z;
            transform.localScale = scale;
        }
    }

#if false
    [HarmonyPatch(typeof(GraphicsCharacterFactoryManager),
        nameof(GraphicsCharacterFactoryManager.CollectBodyPartsToLoadWherePossible_Morphotypes))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CollectBodyPartsToLoadWherePossible_Morphotypes_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacterFactoryManager __instance)
        {
            //PATCH: support for horns on all races
            var searchTermFemale = "_Female_" + MorphotypeElementDefinition.ElementCategory.Horns;
            var searchTermMale = "_Male_" + MorphotypeElementDefinition.ElementCategory.Horns;

            for (var i = 0; i < __instance.shapePartsToLoad.Length; i++)
            {
                var pos = __instance.shapePartsToLoad[i].IndexOf(searchTermFemale, StringComparison.InvariantCulture);

                if (pos > 0)
                {
                    var raceName = __instance.shapePartsToLoad[i].Substring(0, pos);
                    var newPartName = __instance.shapePartsToLoad[i].Replace(raceName, "Dragonborn");

                    __instance.shapePartsToLoad[i] = newPartName;
                }

                pos = __instance.shapePartsToLoad[i].IndexOf(searchTermMale, StringComparison.InvariantCulture);

                // ReSharper disable once InvertIf
                if (pos > 0)
                {
                    var raceName = __instance.shapePartsToLoad[i].Substring(0, pos);
                    var newPartName = __instance.shapePartsToLoad[i].Replace(raceName, "Dragonborn");

                    __instance.shapePartsToLoad[i] = newPartName;
                }
            }
        }
    }
#endif
}
