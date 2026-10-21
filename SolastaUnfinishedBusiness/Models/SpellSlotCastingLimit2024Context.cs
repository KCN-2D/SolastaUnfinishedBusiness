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
    private static readonly ConditionalWeakTable<RulesetCharacter, ExplorationUsage>
        ExplorationUsages = new();
    private static readonly ConditionalWeakTable<RulesetCharacter, List<SpellSlotPaymentCapture>>
        PendingPayments = new();

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

        var option = SpellCastingResourceContext.EnumerateResources(caster, spellDefinition)
            .FirstOrDefault(candidate => candidate.Kind == SpellCastingResourceContext.ResourceKind.FreeRepertoire &&
                                         candidate.IsAvailable(caster));
        repertoire = option?.CastingRepertoire;
        slotLevel = option?.SlotLevel ?? 0;
        return option != null;
    }

    internal static bool CanQueueReactionSpell(CharacterActionParams reactionParams)
    {
        if (reactionParams?.ActionDefinition?.Id != ActionDefinitions.Id.CastReaction ||
            reactionParams.RulesetEffect is not RulesetEffectSpell activeSpell)
        {
            return true;
        }

        if (SpellCastingResourceContext.HasExplicitSelection(activeSpell))
        {
            return CanCastSpell(
                reactionParams.ActingCharacter?.RulesetCharacter,
                activeSpell.SpellRepertoire,
                RulesetEffectSpellWithOrigin.GetOriginSpell(activeSpell),
                activeSpell);
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
        if (caster == null ||
            activeSpell == null ||
            RulesetEffectSpellWithOrigin.GetResourceSlotLevel(activeSpell) <= 0 ||
            !UsesActualSpellSlots(spendingRepertoire))
        {
            return null;
        }

        if (!Main.Settings.EnableOneSpellSlotPerTurn2024 &&
            !Main.Settings.EnableOneDndCounterspellSpell &&
            !caster.GetSubFeaturesByType<IRefundSpellSlotOnFailure>()
                .Any(policy => policy.IsEligible(caster, activeSpell)))
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

        if (!Main.Settings.EnableOneSpellSlotPerTurn2024 || usageOwner == null)
        {
            return false;
        }

        // Exploration has no combat turn, but a cast and its nested interruptions
        // are still one action. Do not reset that cast's budget before a self-reaction.
        if (ExplorationUsages.TryGetValue(usageOwner, out var exploration) &&
            exploration.Generation == _turnGeneration && exploration.Count > 0)
        {
            return true;
        }

        // Native resource refresh callbacks run before the spending method returns.
        // Observe actual deltas during that interval, without charging preserved or free casts.
        if (PendingPayments.TryGetValue(usageOwner, out var pending) &&
            pending.Any(payment => payment.HasCurrentPayment))
        {
            return true;
        }

        return ServiceRepository.GetService<IGameLocationBattleService>() is { IsBattleInProgress: true } &&
               TurnUsages.TryGetValue(usageOwner, out var usage) &&
               usage.Generation == _turnGeneration && usage.Count > 0;
    }

    internal static IDisposable BeginAction(CharacterAction action) =>
        ServiceRepository.GetService<IGameLocationBattleService>() is { IsBattleInProgress: true } ||
        GetTurnUsageOwner(action?.ActingCharacter?.RulesetCharacter) is not { } owner
            ? null
            : new ExplorationActionScope(owner);

    internal static void RecordSpellSlotExpenditure(RulesetSpellRepertoire repertoire, int slotLevel)
    {
        if (slotLevel <= 0 || repertoire == null || IsFreeUseRepertoire(repertoire) ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not
                { IsBattleInProgress: true, Battle: { } battle })
        {
            return;
        }

        var caster = repertoire.GetCaster() ?? battle.AllContenders.Select(contender => contender.RulesetCharacter)
            .FirstOrDefault(character => character?.SpellRepertoires.Contains(repertoire) == true);
        var usageOwner = GetTurnUsageOwner(caster);
        if (usageOwner == null)
        {
            return;
        }

        var usage = GetOrCreateCurrentTurnUsage(usageOwner);
        usage.ExpenditureCount++;
    }

    private static TurnUsage GetOrCreateCurrentTurnUsage(RulesetCharacter usageOwner)
    {
        var usage = TurnUsages.GetOrCreateValue(usageOwner);
        if (usage.Generation != _turnGeneration)
        {
            usage.Generation = _turnGeneration;
            usage.Count = 0;
            usage.ExpenditureCount = 0;
        }

        return usage;
    }

    private static int GetSpellSlotExpenditureCount(RulesetCharacter caster)
    {
        var usageOwner = GetTurnUsageOwner(caster);
        return usageOwner != null && TurnUsages.TryGetValue(usageOwner, out var usage) &&
               usage.Generation == _turnGeneration ? usage.ExpenditureCount : 0;
    }

    internal static bool WouldExpendSpellSlot(
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

        if (SpellCastingResourceContext.IsExplicitSlotSelection(activeSpell))
        {
            return true;
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

    internal static bool IsFreeUseRepertoire(RulesetSpellRepertoire repertoire)
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
        int spentSpellPoints,
        int expenditures,
        long paymentGeneration)
    {
        var usageOwner = GetTurnUsageOwner(caster);
        var markerIncremented = false;
        ExplorationUsage explorationUsage = null;

        if (Main.Settings.EnableOneSpellSlotPerTurn2024 && paymentGeneration == _turnGeneration)
        {
            if (ServiceRepository.GetService<IGameLocationBattleService>() is { IsBattleInProgress: true })
            {
                var usage = GetOrCreateCurrentTurnUsage(usageOwner);
                usage.Count++;
                markerIncremented = true;
            }

            if (ExplorationUsages.TryGetValue(usageOwner, out var exploration) &&
                exploration.Generation == _turnGeneration)
            {
                explorationUsage = exploration;
                explorationUsage.Count++;
            }
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
                explorationUsage,
                expenditures,
                paymentGeneration));
    }

    internal sealed class SpellSlotPaymentCapture : IDisposable
    {
        private readonly RulesetCharacter _caster;
        private readonly RulesetEffectSpell _activeSpell;
        private readonly Dictionary<RulesetSpellRepertoire, Dictionary<int, int>> _slotsBefore;
        private readonly RulesetUsablePower _spellPointPool;
        private readonly int _spellPointsBefore;
        private readonly int _expendituresBefore;
        private readonly RulesetCharacter _usageOwner;
        private readonly long _generation;
        private bool _completed;

        internal bool HasCurrentPayment => !_completed && _generation == _turnGeneration &&
            ((_spellPointPool != null && _spellPointPool.remainingUses < _spellPointsBefore) ||
             _slotsBefore.Any(entry => entry.Key.usedSpellsSlots.Any(slot =>
                 slot.Value > (entry.Value.TryGetValue(slot.Key, out var before) ? before : 0))));

        internal SpellSlotPaymentCapture(
            RulesetCharacter caster,
            RulesetEffectSpell activeSpell,
            RulesetSpellRepertoire spendingRepertoire)
        {
            _caster = caster;
            _activeSpell = activeSpell;
            _expendituresBefore = GetSpellSlotExpenditureCount(caster);
            _slotsBefore = caster.SpellRepertoires
                .Where(UsesActualSpellSlots)
                .Append(spendingRepertoire)
                .Distinct()
                .ToDictionary(
                    candidate => candidate,
                    candidate => new Dictionary<int, int>(candidate.usedSpellsSlots));
            _spellPointPool = caster.UsablePowers.FirstOrDefault(
                usablePower => usablePower.PowerDefinition == SpellPointsContext.PowerSpellPoints);
            _spellPointsBefore = _spellPointPool?.remainingUses ?? 0;
            _usageOwner = GetTurnUsageOwner(caster);
            _generation = _turnGeneration;
            PendingPayments.GetOrCreateValue(_usageOwner).Add(this);
        }

        internal void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (PendingPayments.TryGetValue(_usageOwner, out var pending))
            {
                pending.Remove(this);
                if (pending.Count == 0)
                {
                    PendingPayments.Remove(_usageOwner);
                }
            }
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
                spentSpellPoints,
                Math.Max(0, GetSpellSlotExpenditureCount(_caster) - _expendituresBefore),
                _generation);
        }

        public void Dispose()
        {
            Complete();
        }
    }

    private sealed class SpellSlotPayment(
        RulesetCharacter caster,
        RulesetCharacter usageOwner,
        Dictionary<RulesetSpellRepertoire, Dictionary<int, int>> slotDeltas,
        RulesetUsablePower spellPointPool,
        int spentSpellPoints,
        bool markerIncremented,
        ExplorationUsage explorationUsage,
        int expenditures,
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
            if (explorationUsage != null)
            {
                explorationUsage.Count = Math.Max(0, explorationUsage.Count - 1);
            }

            if (paymentGeneration == _turnGeneration &&
                usageOwner != null &&
                TurnUsages.TryGetValue(usageOwner, out var usage) &&
                usage.Generation == paymentGeneration)
            {
                if (markerIncremented)
                {
                    usage.Count = Math.Max(0, usage.Count - 1);
                }

                usage.ExpenditureCount = Math.Max(0, usage.ExpenditureCount - expenditures);
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
        internal int ExpenditureCount { get; set; }
        internal long Generation { get; set; }
    }

    private sealed class ExplorationUsage
    {
        internal int ActiveActions { get; set; }
        internal int Count { get; set; }
        internal long Generation { get; } = _turnGeneration;
    }

    private sealed class ExplorationActionScope : IDisposable
    {
        private readonly RulesetCharacter _owner;
        private readonly ExplorationUsage _usage;
        private bool _disposed;

        internal ExplorationActionScope(RulesetCharacter owner)
        {
            _owner = owner;
            if (!ExplorationUsages.TryGetValue(owner, out _usage) || _usage.Generation != _turnGeneration)
            {
                ExplorationUsages.Remove(owner);
                _usage = new ExplorationUsage();
                ExplorationUsages.Add(owner, _usage);
            }
            _usage.ActiveActions++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--_usage.ActiveActions == 0 &&
                ExplorationUsages.TryGetValue(_owner, out var current) && ReferenceEquals(current, _usage))
            {
                ExplorationUsages.Remove(_owner);
            }
        }
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
