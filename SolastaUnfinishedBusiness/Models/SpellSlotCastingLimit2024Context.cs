using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Models;

internal static class SpellSlotCastingLimit2024Context
{
    private const string FailureFlag = "Failure/&FailureFlagOneSpellSlotPerTurn2024";
    private static readonly ConditionalWeakTable<RulesetEffectSpell, SpellSlotPayment>
        SpellSlotPayments = new();

    private static readonly ConditionalWeakTable<RulesetCharacter, TurnUsage>
        TurnUsages = new();

    private static long _turnGeneration;

    internal static bool UsesLegacyBonusActionSpellRestriction =>
        !Main.Settings.EnableOneSpellSlotPerTurn2024;

    internal static void RemoveLegacyBonusActionSpellRestriction(ref bool cantripOnly)
    {
        if (!UsesLegacyBonusActionSpellRestriction)
        {
            cantripOnly = false;
        }
    }

    internal static bool CanCastSpell(
        SpellCastingValidationContext context,
        out string failure)
    {
        failure = string.Empty;

        if (context.BypassSpellSlotLimit ||
            !HasExpendedSpellSlotThisTurn(context.Caster) ||
            !WouldExpendSpellSlot(
                context.Caster,
                context.Repertoire,
                context.SpellDefinition,
                context.ActiveSpell))
        {
            return true;
        }

        failure = FailureFlag;
        return false;
    }

    internal static bool CanCastSpell(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        RulesetEffectSpell activeSpell)
    {
        var context = new SpellCastingValidationContext(
            caster,
            repertoire,
            spellDefinition,
            activeSpell,
            false,
            false,
            false);

        return CanCastSpell(context, out _);
    }

    internal static bool CanUseSpellSlotLevel(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        int slotLevel)
    {
        if (slotLevel <= 0 ||
            !HasExpendedSpellSlotThisTurn(caster) ||
            IsFreeUseRepertoire(repertoire))
        {
            return true;
        }

        return Level20Context.HasFreeWizardCast(
            caster,
            repertoire,
            spellDefinition,
            slotLevel);
    }

    internal static bool TryGetAvailableFreeUse(
        RulesetCharacter caster,
        SpellDefinition spellDefinition,
        out RulesetSpellRepertoire repertoire,
        out int slotLevel)
    {
        repertoire = null;
        slotLevel = 0;

        if (caster == null || spellDefinition == null)
        {
            return false;
        }

        foreach (var candidate in caster.SpellRepertoires.Where(IsFreeUseRepertoire))
        {
            if (!SpellCastingValidation.KnowsSpell(candidate, spellDefinition))
            {
                continue;
            }

            for (var level = Math.Max(1, spellDefinition.SpellLevel); level <= 9; level++)
            {
                if (!candidate.TryGetAvailableSlotLevel(
                        caster,
                        level,
                        spellDefinition,
                        out var isAvailable) ||
                    !isAvailable)
                {
                    continue;
                }

                repertoire = candidate;
                slotLevel = level;

                return true;
            }
        }

        return false;
    }

    internal static bool CanQueueReactionSpell(CharacterActionParams reactionParams)
    {
        if (reactionParams?.ActionDefinition?.Id != ActionDefinitions.Id.CastReaction ||
            reactionParams.RulesetEffect is not RulesetEffectSpell activeSpell)
        {
            return true;
        }

        var spellSlotMode = GetReactionSpellSlotMode(
            reactionParams,
            out var noSlotRepertoire,
            out var noSlotLevel);

        if (spellSlotMode == ReactionSpellSlotMode.Standard)
        {
            return true;
        }

        if (spellSlotMode == ReactionSpellSlotMode.Suppress)
        {
            return false;
        }

        activeSpell.spellRepertoire = noSlotRepertoire;
        activeSpell.SlotLevel = noSlotLevel;
        reactionParams.SpellRepertoire = noSlotRepertoire;
        reactionParams.IntParameter = noSlotLevel;

        return true;
    }

    internal static ReactionSpellSlotMode GetReactionSpellSlotMode(
        CharacterActionParams reactionParams,
        out RulesetSpellRepertoire noSlotRepertoire,
        out int noSlotLevel)
    {
        noSlotRepertoire = null;
        noSlotLevel = 0;

        if (reactionParams?.ActionDefinition?.Id != ActionDefinitions.Id.CastReaction ||
            reactionParams.RulesetEffect is not RulesetEffectSpell activeSpell ||
            !RequiresNoSlotReactionCast(
                reactionParams.ActingCharacter?.RulesetCharacter,
                activeSpell))
        {
            return ReactionSpellSlotMode.Standard;
        }

        return TryResolveNoSlotReactionCast(
            reactionParams.ActingCharacter.RulesetCharacter,
            RulesetEffectSpellWithOrigin.GetOriginSpell(activeSpell),
            out noSlotRepertoire,
            out noSlotLevel)
            ? ReactionSpellSlotMode.NoSlotOnly
            : ReactionSpellSlotMode.Suppress;
    }

    internal static bool RequiresNoSlotReactionCast(
        RulesetCharacter caster,
        RulesetEffectSpell activeSpell)
    {
        return HasExpendedSpellSlotThisTurn(caster) &&
               activeSpell != null &&
               activeSpell.OriginItem == null &&
               activeSpell.SlotLevel >= 0 &&
               RulesetEffectSpellWithOrigin.GetResourceSlotLevel(activeSpell) > 0 &&
               activeSpell.RulesetInvocation?.InvocationDefinition is not
               {
                   ConsumesSpellSlot: false
               };
    }

    internal static bool TryResolveNoSlotReactionCast(
        RulesetCharacter caster,
        SpellDefinition spellDefinition,
        out RulesetSpellRepertoire repertoire,
        out int slotLevel)
    {
        if (TryGetAvailableFreeUse(caster, spellDefinition, out repertoire, out slotLevel))
        {
            return true;
        }

        repertoire = null;
        var baseSpellLevel = spellDefinition?.SpellLevel ?? 0;
        slotLevel = baseSpellLevel;

        if (caster == null || spellDefinition == null || baseSpellLevel <= 0)
        {
            return false;
        }

        repertoire = caster.SpellRepertoires.FirstOrDefault(candidate =>
            !IsFreeUseRepertoire(candidate) &&
            SpellCastingValidation.KnowsSpell(candidate, spellDefinition) &&
            Level20Context.HasFreeWizardCast(
                caster,
                candidate,
                spellDefinition,
                baseSpellLevel));

        return repertoire != null;
    }

    internal static SpellSlotPaymentCapture BeginPayment(
        RulesetCharacter caster,
        RulesetEffectSpell activeSpell,
        RulesetSpellRepertoire spendingRepertoire)
    {
        if (!Main.Settings.EnableOneSpellSlotPerTurn2024 &&
            !Main.Settings.EnableOneDndCounterspellSpell &&
            !caster.GetSubFeaturesByType<IRefundSpellSlotOnFailure>()
                .Any(policy => policy.IsEligible(caster, activeSpell)))
        {
            return null;
        }

        if (caster == null ||
            activeSpell == null ||
            RulesetEffectSpellWithOrigin.GetResourceSlotLevel(activeSpell) <= 0 ||
            !UsesActualSpellSlots(spendingRepertoire))
        {
            return null;
        }

        return new SpellSlotPaymentCapture(caster, activeSpell, spendingRepertoire);
    }

    internal static bool TryRefundPayment(RulesetEffectSpell activeSpell)
    {
        if (activeSpell == null ||
            !SpellSlotPayments.TryGetValue(activeSpell, out var payment))
        {
            return false;
        }

        SpellSlotPayments.Remove(activeSpell);
        payment.Refund();

        return true;
    }

    internal static void ForgetPayment(RulesetEffectSpell activeSpell)
    {
        if (activeSpell != null)
        {
            SpellSlotPayments.Remove(activeSpell);
        }
    }

    internal static void StartTurn(GameLocationCharacter character)
    {
        if (ServiceRepository.GetService<IGameLocationBattleService>() is
                not { IsBattleInProgress: true, Battle: { } battle } ||
            !ReferenceEquals(battle.ActiveContender, character))
        {
            return;
        }

        _turnGeneration++;
    }

    internal static bool HasExpendedSpellSlotThisTurn(RulesetCharacter caster)
    {
        var usageOwner = GetTurnUsageOwner(caster);

        if (!Main.Settings.EnableOneSpellSlotPerTurn2024 ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not
            { IsBattleInProgress: true } ||
            usageOwner == null ||
            !TurnUsages.TryGetValue(usageOwner, out var usage))
        {
            return false;
        }

        return usage.Generation == _turnGeneration && usage.Count > 0;
    }

    private static bool WouldExpendSpellSlot(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        RulesetEffectSpell activeSpell)
    {
        if (activeSpell?.OriginItem != null ||
            activeSpell?.SlotLevel < 0 ||
            activeSpell?.RulesetInvocation?.InvocationDefinition is
            { ConsumesSpellSlot: false })
        {
            return false;
        }

        var slotLevel = activeSpell == null
            ? spellDefinition?.SpellLevel ?? 0
            : RulesetEffectSpellWithOrigin.GetResourceSlotLevel(activeSpell);

        if (slotLevel <= 0)
        {
            return false;
        }

        if (!IsFreeUseRepertoire(repertoire))
        {
            return !Level20Context.HasFreeWizardCast(
                caster,
                repertoire,
                spellDefinition,
                slotLevel);
        }

        if (activeSpell == null)
        {
            return false;
        }

        // A feat reaction can start on its free-use repertoire and later redirect
        // to a class repertoire when that free use is exhausted. Recheck here so
        // a stale effect cannot bypass the per-turn slot limit.
        return !repertoire.TryGetAvailableSlotLevel(
            caster,
            slotLevel,
            spellDefinition,
            out var freeUseAvailable) ||
               !freeUseAvailable;
    }

    private static bool UsesActualSpellSlots(RulesetSpellRepertoire repertoire)
    {
        return repertoire?.SpellCastingFeature != null &&
               !IsFreeUseRepertoire(repertoire);
    }

    private static bool IsFreeUseRepertoire(RulesetSpellRepertoire repertoire)
    {
        var castSpell = repertoire?.SpellCastingFeature;

        if (castSpell == null)
        {
            return false;
        }

        return castSpell.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Race ||
               castSpell.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Monster &&
               castSpell.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null;
    }

    private static void RegisterPayment(
        RulesetCharacter caster,
        RulesetEffectSpell activeSpell,
        Dictionary<RulesetSpellRepertoire, Dictionary<int, int>> slotDeltas,
        RulesetUsablePower spellPointPool,
        int spentSpellPoints)
    {
        var usageOwner = GetTurnUsageOwner(caster);
        var markerIncremented = false;

        if (Main.Settings.EnableOneSpellSlotPerTurn2024 &&
            ServiceRepository.GetService<IGameLocationBattleService>() is
            { IsBattleInProgress: true })
        {
            var usage = TurnUsages.GetOrCreateValue(usageOwner);

            if (usage.Generation != _turnGeneration)
            {
                usage.Generation = _turnGeneration;
                usage.Count = 0;
            }

            usage.Count++;
            markerIncremented = true;
        }

        SpellSlotPayments.Remove(activeSpell);
        SpellSlotPayments.Add(
            activeSpell,
            new SpellSlotPayment(
                caster,
                usageOwner,
                slotDeltas,
                spellPointPool,
                spentSpellPoints,
                markerIncremented,
                _turnGeneration));
    }

    internal sealed class SpellSlotPaymentCapture
    {
        private readonly RulesetCharacter _caster;
        private readonly RulesetEffectSpell _activeSpell;
        private readonly Dictionary<RulesetSpellRepertoire, Dictionary<int, int>> _slotsBefore;
        private readonly RulesetUsablePower _spellPointPool;
        private readonly int _spellPointsBefore;

        internal SpellSlotPaymentCapture(
            RulesetCharacter caster,
            RulesetEffectSpell activeSpell,
            RulesetSpellRepertoire spendingRepertoire)
        {
            _caster = caster;
            _activeSpell = activeSpell;
            _slotsBefore = caster.SpellRepertoires
                .Where(candidate => candidate != null)
                .Append(spendingRepertoire)
                .Distinct()
                .ToDictionary(
                    candidate => candidate,
                    candidate => new Dictionary<int, int>(candidate.usedSpellsSlots));
            _spellPointPool = caster.UsablePowers.FirstOrDefault(
                usablePower => usablePower.PowerDefinition == SpellPointsContext.PowerSpellPoints);
            _spellPointsBefore = _spellPointPool?.remainingUses ?? 0;
        }

        internal void Complete()
        {
            var slotDeltas = new Dictionary<RulesetSpellRepertoire, Dictionary<int, int>>();
            var hasConsumedSlot = false;

            foreach (var repertoireEntry in _slotsBefore)
            {
                var repertoire = repertoireEntry.Key;
                var before = repertoireEntry.Value;
                var deltas = before.Keys
                    .Concat(repertoire.usedSpellsSlots.Keys)
                    .Distinct()
                    .Select(level =>
                    {
                        before.TryGetValue(level, out var beforeValue);
                        repertoire.usedSpellsSlots.TryGetValue(level, out var afterValue);

                        return (level, delta: afterValue - beforeValue);
                    })
                    .Where(entry => entry.delta != 0)
                    .ToDictionary(entry => entry.level, entry => entry.delta);

                if (deltas.Count == 0)
                {
                    continue;
                }

                hasConsumedSlot |= deltas.Values.Any(delta => delta > 0);
                slotDeltas.Add(repertoire, deltas);
            }

            var spentSpellPoints = _spellPointPool == null
                ? 0
                : Math.Max(0, _spellPointsBefore - _spellPointPool.remainingUses);

            if (!hasConsumedSlot && spentSpellPoints == 0)
            {
                return;
            }

            RegisterPayment(
                _caster,
                _activeSpell,
                slotDeltas,
                _spellPointPool,
                spentSpellPoints);
        }
    }

    private sealed class SpellSlotPayment(
        RulesetCharacter caster,
        RulesetCharacter usageOwner,
        Dictionary<RulesetSpellRepertoire, Dictionary<int, int>> slotDeltas,
        RulesetUsablePower spellPointPool,
        int spentSpellPoints,
        bool markerIncremented,
        long paymentGeneration)
    {
        internal void Refund()
        {
            foreach (var repertoireEntry in slotDeltas)
            {
                var repertoire = repertoireEntry.Key;

                foreach (var deltaEntry in repertoireEntry.Value)
                {
                    var level = deltaEntry.Key;
                    var delta = deltaEntry.Value;
                    repertoire.usedSpellsSlots.TryGetValue(level, out var current);
                    repertoire.usedSpellsSlots[level] = Math.Max(0, current - delta);
                }
            }

            // Publish the refund only after slots and the turn expenditure agree.
            // UI refresh callbacks can immediately ask whether another slot may be spent.
            if (markerIncremented &&
                paymentGeneration == _turnGeneration &&
                usageOwner != null &&
                TurnUsages.TryGetValue(usageOwner, out var usage) &&
                usage.Generation == paymentGeneration)
            {
                usage.Count = Math.Max(0, usage.Count - 1);
            }

            if (spellPointPool != null && spentSpellPoints > 0)
            {
                caster.AddSpellPoints(spentSpellPoints);
            }

            foreach (var repertoire in slotDeltas.Keys)
            {
                repertoire.RepertoireRefreshed?.Invoke(repertoire);
            }
        }
    }

    private sealed class TurnUsage
    {
        internal int Count { get; set; }
        internal long Generation { get; set; }
    }

    internal enum ReactionSpellSlotMode
    {
        Standard,
        NoSlotOnly,
        Suppress
    }

    private static RulesetCharacter GetTurnUsageOwner(RulesetCharacter caster)
    {
        return caster?.GetFeatureOwnerOrSelf() ?? caster;
    }
}
