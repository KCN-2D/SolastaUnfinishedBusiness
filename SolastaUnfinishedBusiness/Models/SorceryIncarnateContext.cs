using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class SorceryIncarnateContext
{
    private static readonly Dictionary<(MetamagicOptionDefinition, MetamagicOptionDefinition),
        MetamagicOptionDefinition> Pairs = [];
    internal static void LateLoad()
    {
        if (Pairs.Count != 0)
        {
            return;
        }

        // Register every pair on all peers, independent of settings and characters. Native actions
        // and active spells serialize the definition, rather than process-local UI selection state.
        var options = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>()
            .Where(option => option.GetFirstSubFeatureOfType<ReplaceMetamagicOption>() is not { } replacement ||
                             replacement.RequiresSelection)
            .OrderBy(option => option.Name, StringComparer.Ordinal).ToArray();

        var replacementGroups = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>()
            .Where(option => option.HasSubFeatureOfType<ReplaceMetamagicOption>())
            .Select(option => option.GetFirstSubFeatureOfType<ReplaceMetamagicOption>().Options.Append(option).ToArray()).ToArray();

        for (var first = 0; first < options.Length; first++)
        {
            for (var second = first + 1; second < options.Length; second++)
            {
                var a = options[first];
                var b = options[second];
                // Alternative targeting modes of one learned option are not two metamagics.
                if (replacementGroups.Any(group => group.Contains(a) && group.Contains(b)))
                {
                    continue;
                }

                var pair = MetamagicOptionDefinitionBuilder
                    .Create($"MetamagicCombined{a.Name}_{b.Name}")
                    .SetGuiPresentation(a.GuiPresentation.Title, a.GuiPresentation.Description, hidden: true)
                    .SetCost()
                    .AddCustomSubFeatures(new CombinedMetamagic(a, b),
                        new FormattedDefinitionText(
                            () => $"{a.FormatTitle()} + {b.FormatTitle()}",
                            () => $"{a.FormatDescription()}\n\n{b.FormatDescription()}"))
                    .AddToDB();

                pair.AddCustomSubFeatures(a.GetCustomSubFeatures().Concat(b.GetCustomSubFeatures()).ToArray());
                Pairs.Add((a, b), pair);
                Pairs.Add((b, a), pair);
            }
        }
    }

    internal static bool CanCombine(RulesetCharacter caster)
    {
        return Main.Settings.EnableSorcererInnateSorcery2024 &&
               caster?.GetClassLevel(Api.DatabaseHelper.CharacterClassDefinitions.Sorcerer) >= 7 &&
               Tabletop2024Context.HasInnateSorceryCondition(caster);
    }

    internal static MetamagicOptionDefinition GetCombinedOption(
        MetamagicOptionDefinition first, MetamagicOptionDefinition second) =>
        Pairs.TryGetValue((first, second), out var pair) ? pair : null;

    internal static int GetPayableCost(
        RulesetCharacter caster, RulesetEffectSpell spell, MetamagicOptionDefinition option)
    {
        var cost = CombinedMetamagic.GetCost(option, spell.EffectLevel);
        return Tabletop2024Context.IsArcaneApotheosisValid(caster, spell)
            ? cost - CombinedMetamagic.Enumerate(option)
                .Select(candidate => CombinedMetamagic.GetSingleCost(candidate, spell.EffectLevel)).DefaultIfEmpty().Max()
            : cost;
    }

    internal static bool ValidatePair(
        RulesetEffectSpell spell, RulesetCharacter caster, MetamagicOptionDefinition option,
        out string failure, out int cost)
    {
        failure = string.Empty;
        cost = GetPayableCost(caster, spell, option);
        if (!CanCombine(caster))
        {
            return false;
        }

        var known = ReplaceMetamagicOption.GetOptions(caster);
        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        foreach (var component in CombinedMetamagic.Enumerate(option))
        {
            if (!known.Contains(component) ||
                !service.IsMetamagicOptionAvailable(spell, caster, component, out failure, out _))
            {
                return false;
            }
        }

        if (cost <= caster.RemainingSorceryPoints)
        {
            return true;
        }

        failure = "Failure/&FailureFlagInsufficientSorceryPoints";
        return false;
    }

    internal static bool Activate(
        RulesetCharacter caster, RulesetEffectSpell spell, MetamagicOptionDefinition option)
    {
        var apotheosis = Tabletop2024Context.IsArcaneApotheosisValid(caster, spell) &&
                         CombinedMetamagic.GetCost(option, spell.EffectLevel) > 0;

        if (!option.HasSubFeatureOfType<CombinedMetamagic>() && !apotheosis)
        {
            return true;
        }

        var cost = GetPayableCost(caster, spell, option);

        if (apotheosis)
        {
            // Claim the benefit before callbacks can initiate another spell on the same turn.
            Tabletop2024Context.MarkArcaneApotheosisUsed(caster);
        }

        if (cost > 0)
        {
            // Only genuinely spent points trigger on-spend effects and resource events.
            caster.SpendSorceryPoints(cost);
        }
        caster.MetamagicFeatures.Clear();

        foreach (var component in CombinedMetamagic.Enumerate(option))
        {
            if (component.ParameterMethod == MetamagicParameterMethod.BoundFeature)
            {
                caster.MetamagicFeatures.Add(component, component.BoundFeature);
            }

            if (component.Type == MetamagicType.ExtendedSpell && spell.RemainingRounds >= 10)
            {
                spell.RemainingRounds = Math.Min(component.ParameterValue, 2 * spell.RemainingRounds);
            }

            caster.MetamagicActivated?.Invoke(caster, spell, component);
        }

        return false;
    }
}
