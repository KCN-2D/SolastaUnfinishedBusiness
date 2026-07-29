using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
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

    private const string ScavengerIntegratedHairPrefix =
        "Dwarf_Female_Muscular_Hair_A_LOD";
    private const string GnomeScavengerHoodHeadBoneName =
        "Bip001 Head";
    private const string GnomeScavengerHoodHeadProxyName =
        "UB_GnomeScavengerHoodHeadScale";
    private const float GnomeScavengerHoodScale = 1.2f;

    private static readonly ConditionalWeakTable<
        GraphicsCharacterMonster,
        InventoryAppearanceFinalizationState> InventoryAppearanceFinalizationStates = new();
    private static readonly ConditionalWeakTable<
        GraphicsCharacter,
        GnomeScavengerMutationState> GnomeScavengerMutationStates = new();

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
        out bool succeeded)
    {
        succeeded = false;

        if (graphicsCharacter == null ||
            !InventoryAppearanceFinalizationStates.TryGetValue(
                graphicsCharacter,
                out var state) ||
            state.Generation <= previousGeneration)
        {
            return false;
        }

        succeeded = state.Succeeded;

        return true;
    }

    internal static bool ApplySimulacrumWeaponStance(
        GraphicsCharacterMonster graphicsCharacter)
    {
        if (graphicsCharacter?.RulesetCharacterMonster is not
                RulesetCharacterSimulacrum duplicate ||
            !SimulacrumBehavior.CanUseHumanoidEquipment(duplicate))
        {
            return false;
        }

        var animationTag = AnimationDefinitions.WeaponTypes[0];
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

        return true;
    }

    private static bool TryGetGnomeHero(
        GraphicsCharacter graphicsCharacter,
        out RulesetCharacterHero hero)
    {
        hero = graphicsCharacter?.RulesetCharacter as RulesetCharacterHero;

        return hero != null &&
            hero.RaceDefinition ==
            SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterRaceDefinitions.Gnome;
    }

    private static bool IsScavengerOutfit(ItemDefinition itemDefinition)
    {
        return itemDefinition ==
               SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemDefinitions.ClothesScavenger_A ||
               itemDefinition ==
               SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemDefinitions.ClothesScavenger_B;
    }

    private static bool TryGetGnomeScavengerHero(
        GraphicsCharacter graphicsCharacter,
        out RulesetCharacterHero hero)
    {
        return TryGetGnomeHero(graphicsCharacter, out hero) &&
               IsScavengerOutfit(hero
                   .GetItemInSlot(EquipmentDefinitions.SlotTypeTorso)
                   ?.ItemDefinition);
    }

    private static SkinnedMeshRenderer[] GetBodyPartRenderers(
        SkinnedMeshRenderer[][] renderersBySlot,
        GraphicsCharacterDefinitions.BodyPart bodyPart)
    {
        var index = (int)bodyPart;

        return renderersBySlot != null &&
               renderersBySlot.Length > index &&
               renderersBySlot[index] != null
            ? renderersBySlot[index]
            : [];
    }

    private static void ApplyGnomeScavengerAppearance(
        GraphicsCharacter graphicsCharacter,
        RulesetCharacterHero hero)
    {
        var mutationState = GnomeScavengerMutationStates.GetValue(
            graphicsCharacter,
            _ => new GnomeScavengerMutationState());
        var hasSeparateHeadItem =
            hero.GetItemInSlot(EquipmentDefinitions.SlotTypeHead) != null;

        foreach (var renderer in GetBodyPartRenderers(
                     graphicsCharacter.ShapeSkinnedMeshRenderersPerBodySlot,
                     GraphicsCharacterDefinitions.BodyPart.Hair)
                 .Where(renderer => renderer))
        {
            if (!hasSeparateHeadItem && renderer.forceRenderingOff)
            {
                mutationState.Remember(renderer);
                renderer.forceRenderingOff = false;
            }
        }

        var hoodBodyPart = hero.Sex == RuleDefinitions.CreatureSex.Female
            ? GraphicsCharacterDefinitions.BodyPart.Body
            : GraphicsCharacterDefinitions.BodyPart.Helmet;

        foreach (var renderer in GetBodyPartRenderers(
                     graphicsCharacter.ArmorSkinnedMeshRenderersPerBodySlot,
                     hoodBodyPart)
                 .Where(renderer => renderer))
        {
            if (hero.Sex == RuleDefinitions.CreatureSex.Female &&
                IsScavengerIntegratedHair(renderer))
            {
                if (!renderer.forceRenderingOff)
                {
                    mutationState.Remember(renderer);
                    renderer.forceRenderingOff = true;
                }

                continue;
            }

            if (!IsScavengerHoodRenderer(renderer))
            {
                continue;
            }

            if (renderer.forceRenderingOff)
            {
                continue;
            }

            ScaleGnomeScavengerHood(renderer, mutationState);
        }
    }

    private static bool IsScavengerIntegratedHair(
        SkinnedMeshRenderer renderer)
    {
        return renderer?.name?.StartsWith(
                   ScavengerIntegratedHairPrefix,
                   StringComparison.Ordinal) == true ||
               renderer?.sharedMesh?.name?.StartsWith(
                   ScavengerIntegratedHairPrefix,
                   StringComparison.Ordinal) == true ||
               ((ContainsOrdinalIgnoreCase(renderer?.name, "_Hair_") ||
                 ContainsOrdinalIgnoreCase(renderer?.sharedMesh?.name, "_Hair_")) &&
                (renderer?.sharedMaterials ?? [])
                .Where(material => material)
                .Any(material =>
                    ContainsOrdinalIgnoreCase(
                        material.name,
                        "Scavenger_Hair")));
    }

    private static bool IsScavengerHoodRenderer(
        SkinnedMeshRenderer renderer)
    {
        if (!renderer || IsScavengerIntegratedHair(renderer))
        {
            return false;
        }

        return ContainsOrdinalIgnoreCase(renderer.name, "Scavenger") ||
               ContainsOrdinalIgnoreCase(
                   renderer.sharedMesh?.name,
                   "Scavenger") ||
               (renderer.sharedMaterials ?? [])
               .Where(material => material)
               .Any(material =>
                   ContainsOrdinalIgnoreCase(material.name, "Scavenger") &&
                   !ContainsOrdinalIgnoreCase(material.name, "Scavenger_Hair"));
    }

    private static bool ContainsOrdinalIgnoreCase(string value, string expected)
    {
        return !string.IsNullOrEmpty(value) &&
               value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ScaleGnomeScavengerHood(
        SkinnedMeshRenderer renderer,
        GnomeScavengerMutationState mutationState)
    {
        if (!renderer || !renderer.sharedMesh)
        {
            return;
        }

        var bones = renderer.bones;

        if (bones is not { Length: > 0 })
        {
            return;
        }

        var headBoneIndices = Enumerable.Range(0, bones.Length)
            .Where(index =>
                bones[index] &&
                (bones[index].name == GnomeScavengerHoodHeadBoneName ||
                 bones[index].name.EndsWith(" Head", StringComparison.Ordinal)))
            .ToArray();

        if (headBoneIndices.Length == 0)
        {
            return;
        }

        mutationState.Remember(renderer);

        foreach (var headBoneIndex in headBoneIndices)
        {
            var headBone = bones[headBoneIndex];
            var proxy = headBone.Find(GnomeScavengerHoodHeadProxyName);

            if (!proxy)
            {
                proxy = new GameObject(GnomeScavengerHoodHeadProxyName).transform;
                proxy.SetParent(headBone, false);
            }

            proxy.localPosition = Vector3.zero;
            proxy.localRotation = Quaternion.identity;
            proxy.localScale = Vector3.one * GnomeScavengerHoodScale;
            bones[headBoneIndex] = proxy;
        }

        renderer.bones = bones;

        var localBounds = renderer.localBounds;

        localBounds.extents = Vector3.Scale(
            localBounds.extents,
            Vector3.one * GnomeScavengerHoodScale);
        renderer.localBounds = localBounds;
    }

    private static void RestoreGnomeScavengerAppearance(
        GraphicsCharacter graphicsCharacter)
    {
        if (!graphicsCharacter ||
            !GnomeScavengerMutationStates.TryGetValue(
                graphicsCharacter,
                out var state))
        {
            return;
        }

        state.Restore();
        GnomeScavengerMutationStates.Remove(graphicsCharacter);
    }

    private sealed class GnomeScavengerMutationState
    {
        private readonly List<GnomeScavengerRendererMutation> mutations = [];

        internal void Remember(SkinnedMeshRenderer renderer)
        {
            if (!renderer ||
                mutations.Any(mutation =>
                    ReferenceEquals(mutation.Renderer, renderer)))
            {
                return;
            }

            mutations.Add(new GnomeScavengerRendererMutation(renderer));
        }

        internal void Restore()
        {
            foreach (var mutation in mutations)
            {
                mutation.Restore();
            }

            mutations.Clear();
        }
    }

    private sealed class GnomeScavengerRendererMutation
    {
        private readonly Transform[] originalBones;
        private readonly Bounds originalLocalBounds;
        private readonly bool originalForceRenderingOff;

        internal GnomeScavengerRendererMutation(
            SkinnedMeshRenderer renderer)
        {
            Renderer = renderer;
            originalBones = renderer.bones?.ToArray();
            originalLocalBounds = renderer.localBounds;
            originalForceRenderingOff = renderer.forceRenderingOff;
        }

        internal SkinnedMeshRenderer Renderer { get; }

        internal void Restore()
        {
            if (!Renderer)
            {
                return;
            }

            Renderer.forceRenderingOff = originalForceRenderingOff;
            Renderer.localBounds = originalLocalBounds;

            if (originalBones != null)
            {
                Renderer.bones = originalBones;
            }
        }
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
            return !ApplySimulacrumWeaponStance(__instance);
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
                    RulesetCharacterMonster: RulesetCharacterSimulacrum
                } graphicsCharacter)
            {
                ApplySimulacrumWeaponStance(graphicsCharacter);
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

                return false;
            }
            catch (Exception exception)
            {
                var rootException =
                    exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception;

                Trace.LogException(
                    new Exception(
                        "Failed to resolve a Simulacrum weapon animation.",
                        rootException));

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

                try
                {
                    succeeded = FinalizeInventoryAppearance(__instance, graphicsCharacter);
                }
                catch (Exception exception)
                {
                    Trace.LogException(
                        new Exception(
                            "Failed to finalize a Simulacrum inventory appearance.",
                            exception));
                }
                finally
                {
                    var state = InventoryAppearanceFinalizationStates.GetValue(
                        graphicsCharacter,
                        _ => new InventoryAppearanceFinalizationState());

                    state.Generation++;
                    state.Succeeded = succeeded;
                    nativeCallback?.Invoke();
                }
            };
        }
    }

    private static bool FinalizeInventoryAppearance(
        GraphicsCharacterFactoryManager graphicsFactory,
        GraphicsCharacterMonster graphicsCharacter)
    {
        var succeeded = true;

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
        return succeeded;

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

                succeeded = false;
                Trace.LogException(
                    new Exception(
                        $"Failed to finalize a Simulacrum inventory appearance ({stage}).",
                        rootException));
            }
        }
    }

    private sealed class InventoryAppearanceFinalizationState
    {
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

    [HarmonyPatch(
        typeof(GraphicsCharacterFactoryManager),
        "RefreshAllMorphotypeParameters",
        typeof(GraphicsCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FinalizeGnomeScavengerAppearance_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            [HarmonyArgument(0)] GraphicsCharacter graphicsCharacter)
        {
            try
            {
                RestoreGnomeScavengerAppearance(graphicsCharacter);
            }
            catch (Exception ex)
            {
                Trace.LogException(
                    new Exception("Failed to restore a Gnome Scavenger appearance.", ex));
            }
        }

        [UsedImplicitly]
        public static void Postfix(
            [HarmonyArgument(0)] GraphicsCharacter graphicsCharacter)
        {
            try
            {
                Apply(graphicsCharacter);
            }
            catch (Exception ex)
            {
                try
                {
                    RestoreGnomeScavengerAppearance(graphicsCharacter);
                }
                catch (Exception restoreException)
                {
                    Trace.LogException(
                        new Exception(
                            "Failed to restore a Gnome Scavenger appearance after an apply failure.",
                            restoreException));
                }

                Trace.LogException(
                    new Exception("Failed to apply a Gnome Scavenger appearance.", ex));
            }
        }

        private static void Apply(
            GraphicsCharacter graphicsCharacter)
        {
            if (!TryGetGnomeScavengerHero(graphicsCharacter, out var hero))
            {
                return;
            }

            ApplyGnomeScavengerAppearance(graphicsCharacter, hero);
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
