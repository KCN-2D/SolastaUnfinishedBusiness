using System;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Interfaces;

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
