using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors;

internal static class DamageReceivedContext
{
    [ThreadStatic]
    private static HashSet<ulong> _sharingEffects;

    [ThreadStatic]
    private static RulesetCharacter _sharedRecipient;

    [ThreadStatic]
    private static bool _skipDifficultyScaling;

    [ThreadStatic]
    private static SustainedDamageScope _sustainedDamageScope;

    internal static bool SkipDifficultyScaling => _skipDifficultyScaling;

    internal static SustainedDamageScope BeginSustainedDamage(RulesetCharacter character) => new(character);

    internal static void Notify(RulesetCharacter character, int damage, string damageType, ulong sourceGuid)
    {
        if (damage <= 0)
        {
            return;
        }

        var scope = _sustainedDamageScope;
        foreach (var handler in character.GetSubFeaturesByType<IDamageReceived>().Distinct().ToArray())
        {
            handler.OnDamageReceived(character, damage, damageType, sourceGuid);
        }

        scope?.Record(character, damage, damageType, sourceGuid);
    }

    internal sealed class SustainedDamageScope : IDisposable
    {
        private readonly RulesetCharacter _character;
        private readonly int _hitPointsBefore;
        private readonly SustainedDamageScope _previous;
        private readonly bool _previousScaling;
        private readonly List<(IReactToDamageReceived Handler, int Damage, string DamageType, ulong SourceGuid)>
            _notifications = [];
        private bool _completed;
        private bool _disposed;

        internal SustainedDamageScope(RulesetCharacter character)
        {
            _character = character;
            _hitPointsBefore = character.CurrentHitPoints;
            _previous = _sustainedDamageScope;
            _previousScaling = _skipDifficultyScaling;
            _sustainedDamageScope = this;
            _skipDifficultyScaling = ReferenceEquals(_sharedRecipient, character);
            if (_skipDifficultyScaling)
            {
                _sharedRecipient = null;
            }
        }

        internal void Record(RulesetCharacter character, int damage, string damageType, ulong sourceGuid)
        {
            if (_disposed || _character != character)
            {
                return;
            }
            foreach (var handler in character.GetSubFeaturesByType<IReactToDamageReceived>().Distinct().ToArray())
            {
                _notifications.Add((handler, damage, damageType, sourceGuid));
            }
        }

        internal void Complete()
        {
            if (_completed || _disposed)
            {
                return;
            }
            _completed = true;
            var hitPointsAfter = _character.CurrentHitPoints;
            GameLocationCharacterExtensions.QueueReactionsAfterApplication(_character, _notifications.Select(notification =>
                notification.Handler.ReactToDamageReceived(_character, notification.Damage, notification.DamageType,
                    notification.SourceGuid, _hitPointsBefore, hitPointsAfter)).ToArray());
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _sustainedDamageScope = _previous;
            _skipDifficultyScaling = _previousScaling;
        }
    }

    internal static void ShareDamage(
        RulesetEffect effect, RulesetCharacter target, int damage, string damageType, ulong sourceGuid)
    {
        if (damage <= 0 || !(_sharingEffects ??= []).Add(effect.Guid))
        {
            return;
        }

        var previousRecipient = _sharedRecipient;
        _sharedRecipient = target;

        try
        {
            // The recipient still uses its own affinities, temporary hit points and
            // concentration checks. Difficulty already adjusted the received amount.
            var damageForm = new DamageForm
            {
                DamageType = damageType,
                DieType = DieType.D1,
                DiceNumber = 0,
                BonusDamage = damage
            };
            RulesetEntity.TryGetEntity<RulesetCharacter>(sourceGuid, out var source);
            var formsParams = new RulesetImplementationDefinitions.ApplyFormsParams
            {
                sourceCharacter = source,
                targetCharacter = target,
                activeEffect = effect,
                position = GameLocationCharacter.GetFromActor(target)?.LocationPosition ?? default
            };

            RulesetActor.InflictDamage(
                damage,
                damageForm,
                damageType,
                formsParams,
                target,
                false,
                sourceGuid,
                false,
                [],
                new RollInfo(DieType.D1, [], damage),
                false,
                out _);
        }
        finally
        {
            _sharedRecipient = previousRecipient;
            _sharingEffects.Remove(effect.Guid);
        }
    }
}
