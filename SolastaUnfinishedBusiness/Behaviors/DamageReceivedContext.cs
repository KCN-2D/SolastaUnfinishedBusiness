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

    internal static bool SkipDifficultyScaling => _skipDifficultyScaling;

    internal static bool BeginSustainedDamage(RulesetCharacter character)
    {
        var previous = _skipDifficultyScaling;
        _skipDifficultyScaling = ReferenceEquals(_sharedRecipient, character);

        if (_skipDifficultyScaling)
        {
            _sharedRecipient = null;
        }

        return previous;
    }

    internal static void EndSustainedDamage(bool previous)
    {
        _skipDifficultyScaling = previous;
    }

    internal static void Notify(RulesetCharacter character, int damage, string damageType, ulong sourceGuid)
    {
        if (damage <= 0)
        {
            return;
        }

        foreach (var handler in character.GetSubFeaturesByType<IDamageReceived>().Distinct().ToArray())
        {
            handler.OnDamageReceived(character, damage, damageType, sourceGuid);
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
