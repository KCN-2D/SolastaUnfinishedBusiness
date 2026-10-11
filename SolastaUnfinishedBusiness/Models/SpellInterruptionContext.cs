using System;
using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Interrupting a cast and ending an ongoing spell have different success events.
// Keep both results on their own action/effect so nested reactions cannot borrow a success.
internal static class SpellInterruptionContext
{
    private static readonly ConditionalWeakTable<CharacterAction, CounterspellResult> CounterspellResults = new();
    private static readonly ConditionalWeakTable<RulesetEffect, DispelResult> DispelResults = new();
    private static readonly ConditionalWeakTable<RulesetActor, ObserverStack> Observers = new();

    internal static CastScope Track(CharacterActionMagicEffect action)
    {
        return new CastScope(action, action.ActionParams.RulesetEffect);
    }

    internal static bool TryCounterSpell(CharacterAction action, CharacterAction targetAction)
    {
        if (action == null || action.Countered ||
            action is CharacterActionMagicEffect { ExecutionFailed: true } ||
            targetAction == null || targetAction.Countered)
        {
            return false;
        }

        targetAction.Countered = true;
        CounterspellResults.GetOrCreateValue(action).Succeeded = true;

        return true;
    }

    internal static bool HasCounteredSpell(CharacterAction action)
    {
        return action != null &&
               !action.Countered &&
               action is not CharacterActionMagicEffect { ExecutionFailed: true } &&
               CounterspellResults.TryGetValue(action, out var result) &&
               result.Succeeded;
    }

    // Power-based counters share the current Counterspell rules and per-action result tracking.
    internal static IEnumerator ResolvePowerCounterspell(CharacterAction counterAction, CharacterAction targetAction)
    {
        if (targetAction?.ActionParams?.RulesetEffect is not RulesetEffectSpell counteredSpell ||
            targetAction.Countered || counterAction?.ActionParams?.RulesetEffect is not RulesetEffectPower)
        {
            yield break;
        }

        if (Counterspell2024Context.ShouldResolve(counterAction, targetAction))
        {
            yield return Counterspell2024Context.Resolve(counterAction, targetAction);
            yield break;
        }

        var actingCharacter = counterAction.ActingCharacter;
        var rulesetCharacter = actingCharacter.RulesetCharacter;
        var actionParams = counterAction.ActionParams;
        var actionModifier = actionParams.ActionModifiers.FirstOrDefault() ?? new ActionModifier();
        var counteredSpellDefinition = RulesetEffectSpellWithOrigin.GetOriginSpell(counteredSpell);
        var slotLevel = RulesetEffectSpellWithOrigin.GetResourceSlotLevel(counteredSpell);

        foreach (var effectForm in actionParams.RulesetEffect.EffectDescription.EffectForms)
        {
            if (effectForm.FormType != EffectForm.EffectFormType.Counter ||
                effectForm.CounterForm.Type != CounterForm.CounterType.InterruptSpellcasting)
            {
                continue;
            }

            var counterForm = effectForm.CounterForm;
            if (counterForm.AutomaticSpellLevel >= slotLevel)
            {
                TryCounterSpell(counterAction, targetAction);
            }
            else if (counterForm.CheckBaseDC != 0)
            {
                var checkDC = counterForm.CheckBaseDC + slotLevel;

                rulesetCharacter
                    .EnumerateFeaturesToBrowse<FeatureDefinitionMagicAffinity>(rulesetCharacter.FeaturesToBrowse);

                foreach (var featureDefinition in rulesetCharacter.FeaturesToBrowse)
                {
                    var magicAffinity = (FeatureDefinitionMagicAffinity)featureDefinition;
                    if (magicAffinity.CounterspellAffinity == AdvantageType.None)
                    {
                        continue;
                    }

                    var trend = magicAffinity.CounterspellAffinity == AdvantageType.Advantage ? 1 : -1;
                    actionModifier.AbilityCheckAdvantageTrends.Add(new TrendInfo(
                        trend, FeatureSourceType.CharacterFeature, magicAffinity.Name, null));
                }

                if (counteredSpell.CounterAffinity != AdvantageType.None)
                {
                    var trend = counteredSpell.CounterAffinity == AdvantageType.Advantage ? 1 : -1;
                    actionModifier.AbilityCheckAdvantageTrends.Add(new TrendInfo(
                        trend, FeatureSourceType.CharacterFeature, counteredSpell.CounterAffinityOrigin, null));
                }

                var powerDefinition = actionParams.RulesetEffect.GetSourceDefinitionSafe();
                var classHolder = powerDefinition.GetFirstSubFeatureOfType<ClassHolder>();
                var repertoire = classHolder != null
                    ? rulesetCharacter.GetClassSpellRepertoire(classHolder.Class)
                    : rulesetCharacter.SpellRepertoires.FirstOrDefault(candidate =>
                        candidate.SpellCastingFeature?.SpellCastingOrigin
                            is FeatureDefinitionCastSpell.CastingOrigin.Class
                            or FeatureDefinitionCastSpell.CastingOrigin.Subclass);
                var castingAbility = powerDefinition
                    .HasSubFeatureOfType<Counterspell2024Context.CounterspellPowerOrigin>()
                    ? Counterspell2024Context.GetCounterspellPowerAbility(rulesetCharacter)
                    : repertoire?.SpellCastingAbility ?? AttributeDefinitions.Charisma;
                var proficiencyName = counterForm.AddProficiencyBonus ? "ForcedProficiency" : string.Empty;
                var abilityCheckData = new AbilityCheckData { Action = counterAction };

                yield return actingCharacter.RollAbilityCheckWithPrompt(
                    castingAbility, proficiencyName, checkDC, AdvantageType.None, actionModifier,
                    false, 0, abilityCheckData, true);
                yield return TryAlterOutcomeAttributeCheck.HandleITryAlterOutcomeAttributeCheck(
                    actingCharacter, abilityCheckData, abilityCheckData.CurrentRoll);

                counterAction.AbilityCheckRoll = abilityCheckData.AbilityCheckRoll;
                counterAction.AbilityCheckRollOutcome = abilityCheckData.AbilityCheckRollOutcome;
                counterAction.AbilityCheckSuccessDelta = abilityCheckData.AbilityCheckSuccessDelta;

                if (counterAction.AbilityCheckRollOutcome is RollOutcome.Success or RollOutcome.CriticalSuccess)
                {
                    TryCounterSpell(counterAction, targetAction);
                }
            }

            if (!HasCounteredSpell(counterAction))
            {
                continue;
            }

            rulesetCharacter.SpellCounter?.Invoke(
                rulesetCharacter, targetAction.ActingCharacter.RulesetCharacter,
                counteredSpellDefinition, targetAction.Countered, string.IsNullOrEmpty(counteredSpell.IdentifiedBy));
            yield break;
        }
    }

    internal static bool HasDispelledSpell(RulesetEffect effect)
    {
        return effect != null &&
               DispelResults.TryGetValue(effect, out var result) &&
               result.Succeeded;
    }

    internal static IDisposable ObserveDispel(RulesetImplementationDefinitions.ApplyFormsParams formsParams)
    {
        return formsParams.activeEffect != null && formsParams.sourceCharacter != null
            ? new DispelObserver(formsParams.sourceCharacter, formsParams.activeEffect)
            : null;
    }

    private sealed class CounterspellResult
    {
        internal bool Succeeded { get; set; }
    }

    private sealed class DispelResult
    {
        internal bool Succeeded { get; set; }
    }

    private sealed class ObserverStack
    {
        internal DispelObserver Current { get; set; }
    }

    private sealed class DispelObserver : IDisposable
    {
        private readonly RulesetCharacter _source;
        private readonly RulesetEffect _effect;
        private readonly ObserverStack _stack;
        private readonly DispelObserver _previous;

        internal DispelObserver(RulesetCharacter source, RulesetEffect effect)
        {
            _source = source;
            _effect = effect;
            _stack = Observers.GetOrCreateValue(source);
            _previous = _stack.Current;
            _stack.Current = this;
            source.SpellDissipated += OnSpellDissipated;
        }

        private void OnSpellDissipated(RulesetActor source, RulesetActor target, SpellDefinition spell, bool success)
        {
            if (success && source == _source && spell != null && _stack.Current == this)
            {
                DispelResults.GetOrCreateValue(_effect).Succeeded = true;
            }
        }

        public void Dispose()
        {
            _source.SpellDissipated -= OnSpellDissipated;
            _stack.Current = _previous;

            if (_previous == null)
            {
                Observers.Remove(_source);
            }
        }
    }

    internal sealed class CastScope(CharacterActionMagicEffect action, RulesetEffect effect) : IDisposable
    {
        internal void Complete()
        {
            if (effect is not RulesetEffectSpell spell)
            {
                return;
            }

            var character = action.ActingCharacter.RulesetCharacter;

            if (character.GetSubFeaturesByType<IRefundSpellSlotOnFailure>().Any(policy =>
                    policy.IsEligible(character, spell) && policy.ShouldRefundSpellSlot(action)))
            {
                SpellSlotCastingLimit2024Context.TryRefundPayment(spell);
            }
        }

        public void Dispose()
        {
            // Abandoned actions only clean up; they must not manufacture an outcome or refund.
            if (effect is RulesetEffectSpell spell)
            {
                SpellSlotCastingLimit2024Context.ForgetPayment(spell);
            }

            CounterspellResults.Remove(action);
            DispelResults.Remove(effect);
        }
    }
}
