using System.Collections;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class Counterspell2024Context
{
    internal static bool ShouldResolve(
        CharacterActionMagicEffect counterAction,
        CharacterAction targetAction)
    {
        return Main.Settings.EnableOneDndCounterspellSpell &&
               counterAction?.ActionParams?.RulesetEffect is RulesetEffectSpell counterspell &&
               RulesetEffectSpellWithOrigin.GetOriginSpell(counterspell) == Counterspell &&
               counterspell.EffectDescription.EffectForms.Any(
                   effectForm =>
                       effectForm.FormType == EffectForm.EffectFormType.Counter &&
                       effectForm.CounterForm.Type == CounterForm.CounterType.InterruptSpellcasting) &&
               targetAction?.ActionParams?.RulesetEffect is RulesetEffectSpell;
    }

    internal static IEnumerator Resolve(
        CharacterActionMagicEffect counterAction,
        CharacterAction targetAction)
    {
        var counterEffect = counterAction.ActionParams.RulesetEffect;
        var counterspeller = counterAction.ActingCharacter;
        var originalCaster = targetAction.ActingCharacter;
        var counteredSpell = (RulesetEffectSpell)targetAction.ActionParams.RulesetEffect;
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

        if (counterEffect == null ||
            counterspeller?.RulesetCharacter == null ||
            originalCaster?.RulesetCharacter == null ||
            implementationService == null)
        {
            yield break;
        }

        var actionModifier =
            counterAction.ActionParams.ActionModifiers.FirstOrDefault() ?? new ActionModifier();
        var sourceDefinition = counterEffect is RulesetEffectSpell counterspell
            ? RulesetEffectSpellWithOrigin.GetOriginSpell(counterspell)
            : counterEffect.GetSourceDefinitionSafe();
        var effectDescription = counterEffect.EffectDescription;
        var sourceType = sourceDefinition is SpellDefinition
            ? FeatureSourceType.Spell
            : FeatureSourceType.Power;
        var schoolOfMagic = sourceDefinition is SpellDefinition spellDefinition
            ? spellDefinition.SchoolOfMagic
            : string.Empty;
        var metamagic = (counterEffect as RulesetEffectSpell)?.MetamagicOption;

        bool RollSavingThrow(
            ActionModifier modifier,
            out RollOutcome outcome,
            out int outcomeDelta)
        {
            // The original caster now rolls, so old counterspell check affinities change sides.
            // Reapply them here because saving throw rerolls receive a new action modifier.
            ApplyCounterspellAffinities(counterspeller.RulesetCharacter, counteredSpell, modifier);

            return implementationService.TryRollSavingThrow(
                counterspeller.RulesetCharacter,
                counterspeller.Side,
                originalCaster.RulesetCharacter,
                modifier,
                false,
                true,
                AttributeDefinitions.Constitution,
                counterEffect.SaveDC,
                false,
                false,
                false,
                sourceType,
                effectDescription.EffectForms,
                null,
                null,
                sourceDefinition?.Name ?? string.Empty,
                sourceDefinition,
                schoolOfMagic,
                metamagic,
                out outcome,
                out outcomeDelta);
        }

        counterAction.RolledSaveThrow = RollSavingThrow(
            actionModifier,
            out var saveOutcome,
            out var saveOutcomeDelta);
        counterAction.SaveOutcome = saveOutcome;
        counterAction.SaveOutcomeDelta = saveOutcomeDelta;

        if (!counterAction.RolledSaveThrow)
        {
            yield break;
        }

        var rulesetOriginalCaster = originalCaster.RulesetCharacter;
        var savingThrowData = new SavingThrowData
        {
            SaveActionModifier = actionModifier,
            SaveOutcome = saveOutcome,
            SaveOutcomeDelta = saveOutcomeDelta,
            SaveDC = RulesetActorExtensions.SaveDC,
            SaveBonusAndRollModifier = RulesetActorExtensions.SaveBonusAndRollModifier,
            SavingThrowAbility = RulesetActorExtensions.SavingThrowAbility,
            SourceDefinition = sourceDefinition,
            EffectDescription = effectDescription,
            Title = counterAction.FormatTitle(),
            Action = null,
            RerollSavingThrow = RollSavingThrow
        };
        var battleManager = ServiceRepository.GetService<IGameLocationBattleService>()
            as GameLocationBattleManager;
        var hasBorrowedLuck =
            rulesetOriginalCaster.HasConditionOfTypeOrSubType(ConditionBorrowedLuck);

        yield return TryAlterOutcomeSavingThrow.Handler(
            battleManager,
            counterspeller,
            originalCaster,
            savingThrowData,
            hasBorrowedLuck,
            effectDescription);

        counterAction.SaveOutcome = savingThrowData.SaveOutcome;
        counterAction.SaveOutcomeDelta = savingThrowData.SaveOutcomeDelta;

        if (!savingThrowData.IsFailedSavingThrowOutcome())
        {
            yield break;
        }

        if (!SpellInterruptionContext.TryCounterSpell(counterAction, targetAction))
        {
            yield break;
        }

        SpellSlotCastingLimit2024Context.TryRefundPayment(counteredSpell);

        var rulesetCounterspeller = counterspeller.RulesetCharacter;

        if (rulesetCounterspeller.SpellCounter == null)
        {
            yield break;
        }

        var counteredSpellDefinition =
            RulesetEffectSpellWithOrigin.GetOriginSpell(counteredSpell);
        var unknown = string.IsNullOrEmpty(counteredSpell.IdentifiedBy);

        rulesetCounterspeller.SpellCounter(
            rulesetCounterspeller,
            rulesetOriginalCaster,
            counteredSpellDefinition,
            true,
            unknown);
    }

    private static void ApplyCounterspellAffinities(
        RulesetCharacter counterspeller,
        RulesetEffectSpell counteredSpell,
        ActionModifier actionModifier)
    {
        foreach (var magicAffinity in counterspeller.FeaturesByType<FeatureDefinitionMagicAffinity>().Distinct())
        {
            AddInvertedSavingThrowAffinity(
                actionModifier, magicAffinity.CounterspellAffinity, magicAffinity.Name);
        }

        // The effect retains the defensive affinity captured when its caster began casting.
        AddInvertedSavingThrowAffinity(
            actionModifier, counteredSpell.CounterAffinity, counteredSpell.CounterAffinityOrigin);
    }

    private static void AddInvertedSavingThrowAffinity(
        ActionModifier actionModifier,
        AdvantageType counterspellCheckAffinity,
        string sourceName)
    {
        var trend = counterspellCheckAffinity switch
        {
            AdvantageType.Advantage => -1,
            AdvantageType.Disadvantage => 1,
            _ => 0
        };

        if (trend != 0)
        {
            actionModifier.SavingThrowAdvantageTrends.Add(
                new TrendInfo(trend, FeatureSourceType.CharacterFeature, sourceName, null));
        }
    }
}
