using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
            yield return handler.OnTryAlterOutcomeAttack(
                battleManager, action, attacker, defender, unit, actionModifier, attackMode, rulesetEffect);
        }
    }
}
