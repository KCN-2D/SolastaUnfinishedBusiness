using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using static FeatureDefinitionAttributeModifier;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetAttributeModifierPatcher
{
    private const string HalfProficiencyBonusRoundUpTag = "HalfProficiencyBonusRoundUp:";

    internal static int ComputeHalfProficiencyBonus(RulesetAttributeModifier modifier, int proficiencyBonus)
    {
        foreach (var tag in modifier.Tags)
        {
            if (tag.StartsWith(HalfProficiencyBonusRoundUpTag, StringComparison.Ordinal) &&
                int.TryParse(tag.Substring(HalfProficiencyBonusRoundUpTag.Length),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum))
            {
                return Math.Max(minimum, (proficiencyBonus + 1) / 2);
            }
        }

        return proficiencyBonus / 2;
    }

    [HarmonyPatch(typeof(RulesetAttributeModifier), nameof(RulesetAttributeModifier.BuildAttributeModifier))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildAttributeModifier_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetAttributeModifier __result,
            AttributeModifierOperation operationType,
            float modifierValue)
        {
            if (operationType !=
                (AttributeModifierOperation)ExtraAttributeModifierOperation.AddHalfProficiencyBonusRoundUp)
            {
                return;
            }

            // Keep native sorting and addition semantics; tags preserve the rounding and minimum on refresh/save.
            __result.operation = AttributeModifierOperation.AddHalfProficiencyBonus;
            __result.Tags.Add(HalfProficiencyBonusRoundUpTag + ((int)modifierValue).ToString(CultureInfo.InvariantCulture));
        }
    }

    [HarmonyPatch(typeof(RulesetAttributeModifier), nameof(RulesetAttributeModifier.CompareTo))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CompareTo_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetAttributeModifier __instance, ref int __result,
            RulesetAttributeModifier other)
        {
            //PATCH: fixes Critical Threshold SET operations to apply lowest value
            if (other == null)
            {
                return;
            }

            if (__instance.Operation != FeatureDefinitionAttributeModifier.AttributeModifierOperation.Set)
            {
                return;
            }

            var priorityCache = RulesetAttributeModifier.AttributeModifierOperationPriorityCache;
            if (priorityCache[(int)__instance.Operation] != priorityCache[(int)other.Operation])
            {
                return;
            }

            if (__instance.Tags.Contains("SetLowest"))
            {
                __result = -__instance.Value.CompareTo(other.Value);
            }
        }
    }
}
