using System;
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
        UpdateSharedPoolRemainingUses(actor, result);

        return result;
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

        if (actor != null)
        {
            BindUsesAttribute(actor, usablePower);
        }
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
            UpdateSharedPoolRemainingUses(actor, usablePower);

            return;
        }

        var effectiveMaxUses = GetEffectiveMaxUses(actor, usablePower);

        usablePower.remainingUses = Math.Min(
            Math.Min(Math.Max(0, capturedRemainingUses), Math.Max(0, capturedMaxUses)),
            effectiveMaxUses);
    }

    private static void UpdateSharedPoolRemainingUses(
        [CanBeNull] RulesetCharacter character,
        RulesetUsablePower usablePower)
    {
        if (TryGetSharedPoolUses(character, usablePower, out _, out var remainingUses))
        {
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
