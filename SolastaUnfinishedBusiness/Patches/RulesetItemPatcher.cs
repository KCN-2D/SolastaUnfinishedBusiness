using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetItemPatcher
{
    private static void EnsureCustomStackCountAttribute(RulesetItem item)
    {
        ItemCraftingMerchantContext.EnsureCustomStackCountAttribute(item);
    }

    [HarmonyPatch(typeof(RulesetItem), nameof(RulesetItem.RegisterAttributes))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RegisterAttributes_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetItem __instance)
        {
            EnsureCustomStackCountAttribute(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetItem), nameof(RulesetItem.StackCount), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StackCount_Getter_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetItem __instance)
        {
            EnsureCustomStackCountAttribute(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetItem), nameof(RulesetItem.IncreaseStack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IncreaseStack_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetItem __instance)
        {
            EnsureCustomStackCountAttribute(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetItem), nameof(RulesetItem.SpendStack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpendStack_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetItem __instance)
        {
            EnsureCustomStackCountAttribute(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetItem), nameof(RulesetItem.FillTags))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillTags_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetItem __instance,
            Dictionary<string, TagsDefinitions.Criticity> tagsMap,
            object context)
        {
            var item = __instance.itemDefinition;

            //PATCH: add weapon mastery tags
            Tabletop2024Context.TryAddWeaponMasteryTags(context as RulesetCharacter, __instance, tagsMap);

            //PATCH: add custom tags from sub-features
            AddTagToWeapon.TryAddTags(context as RulesetCharacter, __instance, tagsMap);

            //PATCH: adds `Polearm` tag to appropriate weapons
            CustomWeaponsContext.AddPolearmWeaponTag(item, tagsMap);

            //PATCH: adds `Returning` tag to appropriate weapons
            ReturningWeapon.AddReturningWeaponTag(__instance, tagsMap);

            //PATCH: removes `Loading` and `Ammunition` tags from appropriate weapons
            RepeatingShot.ModifyTags(__instance, tagsMap);

            //PATCH: removes `Loading` tag for Oath of Demon Hunter crossbows
            OathOfDemonHunter.IgnoreCrossbowLoadingProperty.ModifyTags(__instance, context as RulesetCharacter, tagsMap);

            //PATCH: adds `Unfinished Business` tag to all CE items 
            CeContentPackContext.AddCeTag(item, tagsMap);
        }
    }
}
