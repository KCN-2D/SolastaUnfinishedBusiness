using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors;

internal static class PowerProvider
{
    // private static readonly Dictionary<(FeatureDefinitionPower, RulesetCharacter), RulesetUsablePower>
    //     UsablePowers = [];

    [NotNull]
    internal static RulesetUsablePower Get(FeatureDefinitionPower power, [CanBeNull] RulesetCharacter actor = null)
    {
        RulesetUsablePower result = null;

        if (actor != null)
        {
            result = actor.UsablePowers.FirstOrDefault(u => u.PowerDefinition == power);
        }

        if (result != null)
        {
            UpdateSharedPoolUses(actor, result);

            return result;
        }

        // if (UsablePowers.TryGetValue((power, actor), out var usablePower))
        // {
        //     result = usablePower;
        // }
        // else
        {
            result = new RulesetUsablePower(power, null, null);
            //UsablePowers.Add((power, actor), result);
        }

        //Update properties to match actor
        UpdateUses(actor, result);
        UpdateSaveDc(actor, result);
        UpdateSharedPoolUses(actor, result);

        return result;
    }

    internal static int EnsureGrantedPowers([CanBeNull] RulesetCharacter actor)
    {
        if (actor == null)
        {
            return 0;
        }

        // Use the same owned feature tree as native GrantPowers, including current
        // feat definitions. Bundle children that are not granted stay transient.
        var features = new List<FeatureDefinition>();
        actor.EnumerateFeaturesToBrowse<FeatureDefinitionPower>(features);
        var powers = features.OfType<FeatureDefinitionPower>().ToArray();
        var existing = actor.UsablePowers.Where(power => power?.PowerDefinition != null)
            .Select(power => power.PowerDefinition).ToHashSet();
        var overridden = powers.Select(power => power.OverriddenPower)
            .Where(power => power != null).ToHashSet();
        var added = new List<RulesetUsablePower>();

        foreach (var group in powers.GroupBy(power => power))
        {
            var power = group.Key;

            if (existing.Contains(power) || overridden.Contains(power))
            {
                continue;
            }

            actor.LookForFeatureOrigin(power, out var race, out var characterClass, out _);
            var usablePower = new RulesetUsablePower(power, race, characterClass);

            // Native grants add fixed uses when several owned features grant the
            // same power. Only the new instance may be initialized or recharged.
            usablePower.AddUses((group.Count() - 1) * power.FixedUsesPerRecharge);
            UpdateUses(actor, usablePower);
            actor.RefreshUsablePower(usablePower, actor.GetClassSpellRepertoire());
            UpdateSaveDc(actor, usablePower, characterClass);
            actor.UsablePowers.Add(usablePower);
            added.Add(usablePower);
        }

        // An owned pool may appear after its children in the feature tree. Resolve
        // shared uses only after all missing owners have been added.
        foreach (var usablePower in added)
        {
            UpdateSharedPoolUses(actor, usablePower);
        }

        return added.Count;
    }

    private static void UpdateUses(
        // ReSharper disable once SuggestBaseTypeForParameter
        RulesetCharacter actor,
        RulesetUsablePower usablePower)
    {
        if (actor == null)
        {
            return;
        }

        BindUsesAttribute(actor, usablePower);

        usablePower.Recharge();
    }

    internal static void BindUsesAttribute(
        RulesetCharacter actor,
        RulesetUsablePower usablePower)
    {
        var attributeName = GetUsesAttributeName(usablePower.powerDefinition);

        if (!string.IsNullOrEmpty(attributeName))
        {
            usablePower.UsesAttribute = actor.GetAttribute(attributeName);
        }
    }

    internal static string GetUsesAttributeName(FeatureDefinitionPower powerDefinition)
    {
        return powerDefinition?.RechargeRate switch
        {
            RechargeRate.ChannelDivinity => AttributeDefinitions.ChannelDivinityNumber,
            RechargeRate.HealingPool => AttributeDefinitions.HealingPool,
            RechargeRate.SorceryPoints => AttributeDefinitions.SorceryPoints,
            RechargeRate.KiPoints => AttributeDefinitions.KiPoints,
            RechargeRate.BardicInspiration => AttributeDefinitions.BardicInspirationNumber,
            _ => powerDefinition.UsesDetermination switch
            {
                UsesDetermination.AbilityBonusPlusFixed => powerDefinition.UsesAbilityScoreName,
                UsesDetermination.ProficiencyBonus => AttributeDefinitions.ProficiencyBonus,
                _ => null
            }
        };
    }

    internal static int GetEffectiveMaxUses(
        RulesetCharacter actor,
        RulesetUsablePower usablePower)
    {
        if (usablePower?.PowerDefinition == null)
        {
            return 0;
        }

        BindUsesAttribute(actor, usablePower);

        if (usablePower.PowerDefinition is FeatureDefinitionPowerSharedPool)
        {
            return TryGetSharedPoolUses(actor, usablePower, out var maxUses, out _)
                ? maxUses
                : 0;
        }

        return Math.Max(
            0,
            actor?.GetMaxUsesOfPower(usablePower) ?? usablePower.MaxUses);
    }

    // Definitions can change independently of serialized maxUses; only repair invalid lower bounds.
    internal static void EnsureIndependentFixedPowerUseMinimums([CanBeNull] RulesetCharacter actor)
    {
        if (actor == null)
        {
            return;
        }

        foreach (var usablePower in actor.UsablePowers)
        {
            var power = usablePower?.PowerDefinition;

            if (power is FeatureDefinitionPowerSharedPool ||
                !UsesIndependentFixedPool(power) ||
                usablePower.maxUses >= power.FixedUsesPerRecharge)
            {
                continue;
            }

            usablePower.maxUses = power.FixedUsesPerRecharge;
            usablePower.remainingUses = Math.Min(
                Math.Max(0, usablePower.remainingUses),
                GetEffectiveMaxUses(actor, usablePower));
        }
    }

    internal static bool UsesIndependentResource([CanBeNull] FeatureDefinitionPower power)
    {
        return power is not null and not FeatureDefinitionPowerSharedPool && power.RechargeRate is
            RechargeRate.OneMinute or RechargeRate.ShortRest or RechargeRate.LongRest or
            RechargeRate.Dawn or RechargeRate.D6_6 or RechargeRate.None or
            RechargeRate.D6_56 or RechargeRate.TurnStart;
    }

    private static bool UsesIndependentFixedPool([CanBeNull] FeatureDefinitionPower power)
    {
        return UsesIndependentResource(power) && power is
        {
            UsesDetermination: UsesDetermination.Fixed,
            FixedUsesPerRecharge: > 0,
            CostPerUse: > 0
        };
    }

    internal static void RestoreRemainingUses(
        RulesetCharacter actor,
        RulesetUsablePower usablePower,
        int capturedMaxUses,
        int capturedRemainingUses)
    {
        if (usablePower?.PowerDefinition == null)
        {
            return;
        }

        if (usablePower.PowerDefinition is FeatureDefinitionPowerSharedPool)
        {
            // Shared powers mirror their root pool and never own an independent
            // resource count.
            UpdateSharedPoolUses(actor, usablePower);

            return;
        }

        var effectiveMaxUses = GetEffectiveMaxUses(actor, usablePower);

        usablePower.remainingUses = Math.Min(
            Math.Min(Math.Max(0, capturedRemainingUses), Math.Max(0, capturedMaxUses)),
            effectiveMaxUses);
    }

    private static void UpdateSharedPoolUses(
        [CanBeNull] RulesetCharacter character,
        RulesetUsablePower usablePower)
    {
        if (TryGetSharedPoolUses(character, usablePower, out var maxUses, out var remainingUses))
        {
            usablePower.maxUses = maxUses;
            usablePower.remainingUses = remainingUses;
        }
    }

    private static bool TryGetSharedPoolUses(
        [CanBeNull] RulesetCharacter character,
        RulesetUsablePower usablePower,
        out int maxUses,
        out int remainingUses)
    {
        maxUses = 0;
        remainingUses = 0;

        if (character == null ||
            usablePower?.PowerDefinition is not FeatureDefinitionPowerSharedPool)
        {
            return false;
        }

        var pool = PowerBundle.GetPoolPower(usablePower, character);

        if (pool == null || pool == usablePower)
        {
            return false;
        }

        var powerCost = Math.Max(1, usablePower.PowerDefinition.CostPerUse);

        maxUses = Math.Max(0, character.GetMaxUsesOfPower(pool) / powerCost);
        remainingUses = Math.Min(maxUses, Math.Max(0, pool.RemainingUses / powerCost));

        return true;
    }

    internal static void UpdateSaveDc(
        [CanBeNull] RulesetCharacter actor,
        [NotNull] RulesetUsablePower usablePower,
        CharacterClassDefinition classDefinition = null)
    {
        var power = usablePower.PowerDefinition;
        var effectDescription = power.EffectDescription;

        if (actor == null ||
            !effectDescription.HasSavingThrow)
        {
            return;
        }

        if (!classDefinition)
        {
            classDefinition = actor.FindClassHoldingFeature(power);
        }

        usablePower.saveDC =
            EffectHelpers.CalculateSaveDc(actor, effectDescription, classDefinition, usablePower.saveDC);
    }
}
