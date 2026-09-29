using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Interfaces;

public interface IBeforeD20Roll
{
    IEnumerator OnBeforeD20Roll(D20RollContext context, GameLocationCharacter waiter);
}

public sealed class D20RollContext(
    RulesetCharacter character,
    RollContext rollContext,
    string abilityScoreName = "",
    string proficiencyName = "",
    List<TrendInfo> advantageTrends = null,
    bool canGainAdvantage = true) : IDisposable
{
    private readonly HashSet<string> _approvedFeatures = [];
    private bool _disposed;
    private bool _hasApprovedFeature;

    public static D20RollContext Current { get; private set; }

    public RulesetCharacter Character { get; } = character;
    public RollContext RollContext { get; } = rollContext;
    public string AbilityScoreName { get; } = abilityScoreName;
    public string ProficiencyName { get; } = proficiencyName;

    // Another source of advantage cannot improve a roll even when disadvantage cancels it.
    public bool HasExistingAdvantage => advantageTrends?.Any(trend => trend.value > 0) == true;

    public bool CanGainAdvantage => !_disposed && canGainAdvantage && !_hasApprovedFeature &&
                                    !HasExistingAdvantage;

    public IEnumerator Prompt(GameLocationCharacter waiter)
    {
        if (Character == null || !CanGainAdvantage ||
            (RollContext == RollContext.SavingThrow &&
             Character.IsAutomaticallyFailingSavingThrow(AbilityScoreName)))
        {
            yield break;
        }

        foreach (var handler in Character.GetSubFeaturesByType<IBeforeD20Roll>().ToArray())
        {
            if (!CanGainAdvantage)
            {
                yield break;
            }

            yield return handler.OnBeforeD20Roll(this, waiter);
        }
    }

    public void Approve(string featureName)
    {
        if (CanGainAdvantage)
        {
            _approvedFeatures.Add(featureName);
            _hasApprovedFeature = true;
        }
    }

    public bool IsApproved(string featureName)
    {
        return !_disposed && _approvedFeatures.Contains(featureName);
    }

    public bool TryConsumeApproval(string featureName)
    {
        return !_disposed && _approvedFeatures.Remove(featureName);
    }

    public IDisposable Activate()
    {
        return new ActiveRollScope(this);
    }

    public void Dispose()
    {
        _disposed = true;
        _approvedFeatures.Clear();
    }

    private sealed class ActiveRollScope : IDisposable
    {
        private readonly D20RollContext _previous = Current;
        private bool _disposed;

        internal ActiveRollScope(D20RollContext context)
        {
            Current = context;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Current = _previous;
            _disposed = true;
        }
    }
}

// Resource-based choices run after passive modifiers so existing advantage never wastes a use.
public interface IModifyDiceRollWithResource : IModifyDiceRoll
{
}

public interface IModifyDiceRoll
{
    [UsedImplicitly]
    public void BeforeRoll(
        RollContext rollContext,
        RulesetCharacter rulesetCharacter,
        ref DieType dieType,
        ref AdvantageType advantageType);

    [UsedImplicitly]
    public void AfterRoll(
        DieType dieType,
        AdvantageType advantageType,
        RollContext rollContext,
        RulesetCharacter rulesetCharacter,
        ref int firstRoll,
        ref int secondRoll,
        ref int result);
}
