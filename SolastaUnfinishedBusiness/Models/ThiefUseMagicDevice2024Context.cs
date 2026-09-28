using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class ThiefUseMagicDevice2024Context
{
    internal static FeatureDefinitionMagicAffinity Feature =>
        DatabaseRepository.GetDatabase<FeatureDefinitionMagicAffinity>().GetElement("MagicAffinityUseMagicalItem");

    internal static bool IsEnabled(RulesetCharacter character)
    {
        return Main.Settings.EnableThiefUseMagicDevice2024 &&
               character != null && character.HasAnyFeature(Feature);
    }

    internal static bool CanUseScroll(RulesetCharacter character, SpellDefinition spell)
    {
        return IsEnabled(character) || character.IsSpellDefinitionOnAnySpellList(spell);
    }

    internal static int SpendCharges(RulesetItemDevice device, int charges, RulesetCharacterHero character)
    {
        // Roll only at the native consumption boundary. Virtual power-pool devices spend
        // class resources, and consumable scrolls never pass through this charged-item path.
        if (charges > 0 && device.RemainingCharges >= charges && device.ItemDefinition.Magical &&
            IsEnabled(character) && PowerPoolDevice.GetFromRulesetItem(character, device) == null &&
            RollDie(DieType.D6, AdvantageType.None, out _, out _) == 6)
        {
            character.LogCharacterUsedFeature(Feature);

            return device.RemainingCharges;
        }

        return device.SpendCharges(charges);
    }

    internal static bool IsScroll(RulesetEffectSpell spell)
    {
        return IsEnabled(spell?.Caster) &&
               spell.OriginItem?.UsableDeviceDescription.UsableDeviceTags.Contains("Scroll") == true;
    }

    internal static int GetSpellAttackBonus(RulesetCharacter character)
    {
        return character.TryGetProficiencyBonus() +
               AttributeDefinitions.ComputeAbilityScoreModifier(
                   character.TryGetAttributeValue(AttributeDefinitions.Intelligence));
    }

    internal static List<TrendInfo> GetSpellAttackTrends(RulesetCharacter character)
    {
        return
        [
            new TrendInfo(character.TryGetProficiencyBonus(), FeatureSourceType.Proficiency, string.Empty, null),
            new TrendInfo(
                AttributeDefinitions.ComputeAbilityScoreModifier(
                    character.TryGetAttributeValue(AttributeDefinitions.Intelligence)),
                FeatureSourceType.AbilityScore, AttributeDefinitions.Intelligence, null)
        ];
    }

    internal static IEnumerator CheckScrollExecutionFailure(IEnumerator original, CharacterActionCastSpell action)
    {
        while (original.MoveNext())
        {
            yield return original.Current;
        }

        var spell = action.ActiveSpell;
        if (action.ExecutionFailed || !IsScroll(spell))
        {
            yield break;
        }

        var spellDefinition = RulesetEffectSpellWithOrigin.GetOriginSpell(spell);

        if (spellDefinition.SpellLevel <= 1)
        {
            yield break;
        }

        var character = action.ActingCharacter;
        var modifier = action.ActionParams.ActionModifiers.FirstOrDefault() ?? new ActionModifier();
        var roll = character.RollAbilityCheckEx(
            AttributeDefinitions.Intelligence, SkillDefinitions.Arcana,
            10 + spellDefinition.SpellLevel, AdvantageType.None, modifier,
            false, 0, out var outcome, out var delta, out var rawRoll, true);
        var abilityCheck = new AbilityCheckData
        {
            AbilityCheckRoll = roll,
            AbilityCheckRollOutcome = outcome,
            AbilityCheckSuccessDelta = delta,
            AbilityCheckActionModifier = modifier,
            Action = action
        };

        yield return TryAlterOutcomeAttributeCheck.HandleITryAlterOutcomeAttributeCheck(
            character, abilityCheck, rawRoll);

        action.AbilityCheckRoll = abilityCheck.AbilityCheckRoll;
        action.AbilityCheckRollOutcome = abilityCheck.AbilityCheckRollOutcome;
        action.AbilityCheckSuccessDelta = abilityCheck.AbilityCheckSuccessDelta;
        action.ExecutionFailed = abilityCheck.AbilityCheckRollOutcome is RollOutcome.Failure or RollOutcome.CriticalFailure;

        if (action.ExecutionFailed)
        {
            character.RulesetCharacter.SpellcastingFailed?.Invoke(character.RulesetCharacter, spellDefinition);
        }
    }
}
