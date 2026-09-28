using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors;

// A registered definition carries the pair through native action serialization and save/load.
internal sealed class CombinedMetamagic(params MetamagicOptionDefinition[] options)
{
    internal IReadOnlyList<MetamagicOptionDefinition> Options { get; } = options;

    // Native methods test one kind of metamagic. Keep those branches intact while recognizing registered pairs.
    internal static IEnumerable<CodeInstruction> ReplaceTypeChecks(
        IEnumerable<CodeInstruction> instructions, MetamagicType expected, string context, bool adjustDistantRange = false)
    {
        var result = instructions.ReplaceCalls(
            AccessTools.PropertyGetter(typeof(MetamagicOptionDefinition), nameof(MetamagicOptionDefinition.Type)),
            context,
            new CodeInstruction(OpCodes.Ldc_I4, (int)expected),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CombinedMetamagic), nameof(ResolveType))));

        return adjustDistantRange
            ? result.ReplaceCalls(
                AccessTools.PropertyGetter(typeof(MetamagicOptionDefinition), nameof(MetamagicOptionDefinition.ParameterValue)),
                context,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CombinedMetamagic), nameof(GetDistantRange))))
            : result;
    }

    internal static MethodInfo GetIteratorMoveNext(Type owner, string method)
    {
        var iterator = owner.GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name.StartsWith($"<{method}>d__", StringComparison.Ordinal));
        return AccessTools.Method(iterator, "MoveNext");
    }

    internal static IEnumerable<MetamagicOptionDefinition> Enumerate(MetamagicOptionDefinition option)
    {
        return option?.GetFirstSubFeatureOfType<CombinedMetamagic>()?.Options ??
               (option == null ? Array.Empty<MetamagicOptionDefinition>() : new[] { option });
    }

    internal static bool Contains(MetamagicOptionDefinition option, string name)
    {
        return Enumerate(option).Any(candidate => candidate.Name == name);
    }

    internal static bool HasType(MetamagicOptionDefinition option, MetamagicType type)
    {
        return Enumerate(option).Any(candidate => candidate.Type == type);
    }

    internal static int GetCost(MetamagicOptionDefinition option, int spellLevel)
    {
        return Enumerate(option).Sum(candidate => GetSingleCost(candidate, spellLevel));
    }

    internal static int GetSingleCost(MetamagicOptionDefinition option, int spellLevel)
    {
        return option.CostMethod == MetamagicCostMethod.SpellLevel
            ? Math.Max(1, spellLevel)
            : Math.Max(0, option.SorceryPointsCost);
    }

    internal static int GetDistantRange(MetamagicOptionDefinition option)
    {
        return option.HasSubFeatureOfType<CombinedMetamagic>()
            ? Enumerate(option).First(candidate => candidate.Type == MetamagicType.DistantSpell).ParameterValue
            : option.ParameterValue;
    }

    internal static MetamagicType ResolveType(MetamagicOptionDefinition option, MetamagicType expected)
    {
        if (!option.HasSubFeatureOfType<CombinedMetamagic>())
        {
            return option.Type;
        }

        // A pair's default enum value is not a component. Never grant an absent option.
        return HasType(option, expected) ? expected : (MetamagicType)(-1);
    }
}
