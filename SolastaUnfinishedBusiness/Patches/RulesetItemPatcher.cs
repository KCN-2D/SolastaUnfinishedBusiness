using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetItemPatcher
{
    internal static IEnumerable<CodeInstruction> ReplaceStackingDefinitionComparison(
        IEnumerable<CodeInstruction> instructions, string context, bool sourceFromArgument = false)
    {
        var codes = instructions.ToList();
        var equality = AccessTools.Method(typeof(BaseDefinition), "op_Equality");
        var definition = AccessTools.PropertyGetter(typeof(RulesetItem), nameof(RulesetItem.ItemDefinition));
        var matches = codes.Select((code, index) => (code, index))
            .Where(pair => pair.code.Calls(equality)).Select(pair => pair.index).ToArray();

        if (matches.Length != 1)
        {
            Main.Error($"{context}: expected one item definition comparison, found {matches.Length}");
            return codes;
        }

        var match = matches[0];
        var getters = Enumerable.Range(System.Math.Max(0, match - 5), System.Math.Min(5, match))
            .Where(index => codes[index].Calls(definition)).ToArray();
        if (getters.Length != (sourceFromArgument ? 1 : 2))
        {
            Main.Error($"{context}: unexpected item definition comparison operands");
            return codes;
        }

        foreach (var getter in getters)
        {
            // Retain native branch and exception metadata while passing each item instead of its definition.
            codes[getter].opcode = OpCodes.Nop;
            codes[getter].operand = null;
        }

        if (sourceFromArgument)
        {
            codes[match - 1].opcode = OpCodes.Ldarg_1;
            codes[match - 1].operand = null;
        }

        codes[match].operand = AccessTools.Method(typeof(ItemCraftingMerchantContext),
            nameof(ItemCraftingMerchantContext.CanMergeItemsOrMatchNativeDefinitions));
        return codes;
    }

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

    [HarmonyPatch(typeof(RulesetItem), nameof(RulesetItem.CanStillBeStacked), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanStillBeStacked_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetItem __instance, ref bool __result)
        {
            __result &= ItemCraftingMerchantContext.IsCustomStackingAllowed(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetItemFactoryManager), nameof(RulesetItemFactoryManager.CreateItemsFromLootPack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CreateItemsFromLootPack_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.ReplaceCalls(
                AccessTools.PropertyGetter(typeof(ItemDefinition), nameof(ItemDefinition.CanBeStacked)),
                2, "RulesetItemFactoryManager.CreateItemsFromLootPack.CustomStacking",
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(CreateItemsFromLootPack_Patch), nameof(CanCreateStack))));
        }

        private static bool CanCreateStack(ItemDefinition definition)
        {
            return definition.CanBeStacked && ItemCraftingMerchantContext.IsCustomStackingAllowed(definition);
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
