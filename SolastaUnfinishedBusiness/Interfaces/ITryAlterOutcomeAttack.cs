using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;

namespace SolastaUnfinishedBusiness.Interfaces;

public interface ITryAlterOutcomeAttack
{
    // using these priorities across the mod
    // -50 = Way of the Silhouette Shadowy Sanctuary
    // -10 = anything that changes attack rolls
    // non-negative priorities will only trigger if attack is success or critical success
    //   0 = Roguish Acrobat Heroic Uncanny Dodge
    //  10 = anything that adds resistance to damage
    //  20 = anything that reduces damage
    //  30 = anything that buff defender, debuff attacker or damages attacker
    public int HandlerPriority { get; }

    public IEnumerator OnTryAlterOutcomeAttack(
        GameLocationBattleManager instance,
        CharacterAction action,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        GameLocationCharacter helper,
        ActionModifier actionModifier,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect);
}

internal static class TryAlterOutcomeAttack
{
    private static readonly ConditionalWeakTable<CharacterAction, ForcedHit> ForcedHits = new();

    [ThreadStatic]
    private static AttackResolutionScope _attackResolutionScope;

    internal static void ForceHit(CharacterAction action, GameLocationCharacter defender, ActionModifier modifier)
    {
        ForcedHits.Remove(action);
        ForcedHits.Add(action, new ForcedHit(defender.RulesetActor, modifier, action.AttackRoll));
        action.AttackRollOutcome = RuleDefinitions.RollOutcome.Success;
        action.AttackSuccessDelta = 0;
    }

    internal static bool IsForcedHit(CharacterAction action, GameLocationCharacter defender, ActionModifier modifier)
    {
        return action.AttackRollOutcome == RuleDefinitions.RollOutcome.Success &&
               ForcedHits.TryGetValue(action, out var forcedHit) &&
               forcedHit.Matches(defender.RulesetActor, modifier, action.AttackRoll);
    }

    internal static IDisposable BeginAttackResolution(
        CharacterAction action, GameLocationCharacter defender, ActionModifier modifier)
    {
        var forceHit = IsForcedHit(action, defender, modifier);

        // Each target/beam consumes its own authorization, even when the native action
        // and modifier are reused for another attack against the same creature.
        ForcedHits.Remove(action);

        return new AttackResolutionScope(action.ActingCharacter.RulesetCharacter,
            defender.RulesetActor, modifier, action.AttackRoll, forceHit);
    }

    internal static bool ConsumeForcedHit(RulesetCharacter attacker, RulesetActor target,
        List<RuleDefinitions.TrendInfo> toHitTrends, int predefinedRoll, bool testMode)
    {
        return !testMode && _attackResolutionScope?.TryConsume(attacker, target, toHitTrends, predefinedRoll) == true;
    }

    private sealed class ForcedHit(RulesetActor target, ActionModifier modifier, int rawRoll)
    {
        internal bool Matches(RulesetActor candidateTarget, ActionModifier candidateModifier, int candidateRoll)
        {
            return ReferenceEquals(target, candidateTarget) && ReferenceEquals(modifier, candidateModifier) &&
                   rawRoll == candidateRoll;
        }
    }

    // The final native roll is synchronous. Do not keep this scope active across a yield:
    // unrelated reactions must never inherit another action's forced outcome.
    private sealed class AttackResolutionScope : IDisposable
    {
        private readonly AttackResolutionScope _previous = _attackResolutionScope;
        private readonly RulesetCharacter _attacker;
        private readonly RulesetActor _target;
        private readonly ActionModifier _modifier;
        private readonly int _rawRoll;
        private bool _available;
        private bool _disposed;

        internal AttackResolutionScope(RulesetCharacter attacker, RulesetActor target,
            ActionModifier modifier, int rawRoll, bool forceHit)
        {
            _attacker = attacker;
            _target = target;
            _modifier = modifier;
            _rawRoll = rawRoll;
            _available = forceHit;
            _attackResolutionScope = this;
        }

        internal bool TryConsume(RulesetCharacter attacker, RulesetActor target,
            List<RuleDefinitions.TrendInfo> toHitTrends, int rawRoll)
        {
            if (_disposed || !_available || !ReferenceEquals(_attacker, attacker) ||
                !ReferenceEquals(_target, target) ||
                !ReferenceEquals(_modifier.AttacktoHitTrends, toHitTrends) || _rawRoll != rawRoll)
            {
                return false;
            }

            _available = false;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _attackResolutionScope = _previous;
            _disposed = true;
        }
    }

    internal static int GetReplacementRollSuccessDelta(
        CharacterAction action,
        int newRoll,
        RulesetActor defender,
        ActionModifier actionModifier,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect)
    {
        // Native natural 1/20 results omit the numerical margin. Other critical thresholds
        // already have a margin, and subsequent reaction adjustments must be retained.
        var toHitBonus = attackMode?.ToHitBonus ?? rulesetEffect?.MagicAttackBonus;

        if (action.AttackRoll is 1 or 20 && action.AttackSuccessDelta == 0 && toHitBonus.HasValue)
        {
            return newRoll + toHitBonus.Value + actionModifier.AttackRollModifier -
                   defender.GetAttribute(AttributeDefinitions.ArmorClass).CurrentValue;
        }

        return action.AttackSuccessDelta + newRoll - action.AttackRoll;
    }

    private static readonly List<(ITryAlterOutcomeAttack, GameLocationCharacter)> Handlers = [];

    private static void CollectHandlers()
    {
        var locationCharacterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var contenders =
            Gui.Battle?.AllContenders ??
            locationCharacterService.PartyCharacters.Union(locationCharacterService.GuestCharacters);

        Handlers.Clear();

        foreach (var unit in contenders
                     .Where(u => u.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false })
                     .ToArray())
        {
            Handlers.AddRange(unit.RulesetCharacter.GetSubFeaturesByType<ITryAlterOutcomeAttack>()
                .Select(handler => (handler, unit)));

            Handlers.AddRange(unit.RulesetCharacter.GetUsableSpellSubFeaturesByType<ITryAlterOutcomeAttack>()
                .Select(handler => (handler, unit)));

            // supports metamagic use cases, including snapshotted Simulacrum identities
            Handlers.AddRange(SimulacrumBehavior
                .EnumerateTrainedMetamagicOptions(unit.RulesetCharacter)
                .SelectMany(metamagic => metamagic.GetAllSubFeaturesOfType<ITryAlterOutcomeAttack>())
                .Select(handler => (handler, unit)));
        }
    }

    internal static IEnumerable HandlerNegativePriority(
        GameLocationBattleManager battleManager,
        CharacterAction action,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        ActionModifier actionModifier,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect)
    {
        CollectHandlers();

        foreach (var (handler, unit) in Handlers
                     .Where(x => x.Item1.HandlerPriority < 0)
                     .OrderBy(x => x.Item1.HandlerPriority))
        {
            yield return handler.OnTryAlterOutcomeAttack(
                battleManager, action, attacker, defender, unit, actionModifier, attackMode, rulesetEffect);
        }
    }

    internal static IEnumerable HandlerNonNegativePriority(
        GameLocationBattleManager battleManager,
        CharacterAction action,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        ActionModifier actionModifier,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect)
    {
        foreach (var (handler, unit) in Handlers
                     .Where(x => x.Item1.HandlerPriority >= 0)
                     .OrderBy(x => x.Item1.HandlerPriority))
        {
            // A preceding hit reaction can still replace the roll (for example,
            // Silvery Barbs). Damage defenses must not spend resources on its miss.
            if (action.AttackRollOutcome is not (RuleDefinitions.RollOutcome.Success or
                    RuleDefinitions.RollOutcome.CriticalSuccess))
            {
                yield break;
            }

            yield return handler.OnTryAlterOutcomeAttack(
                battleManager, action, attacker, defender, unit, actionModifier, attackMode, rulesetEffect);
        }
    }
}
