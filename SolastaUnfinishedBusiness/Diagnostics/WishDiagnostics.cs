using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Diagnostics;

// Temporary Wish playtest diagnostics. Keep all formatting here so the
// diagnostics can be removed without changing Wish behavior.
internal static class WishDiagnostics
{
    private const string Prefix = "[UB-WISH-DIAG]";

    internal static IOnConditionAddedOrRemoved ConditionLifecycleMarker { get; } =
        new ConditionLifecycle();

    internal static void RecordRealityRevision(
        string mode,
        string rollKind,
        GameLocationCharacter helper,
        GameLocationCharacter observed,
        RuleDefinitions.RollOutcome originalOutcome,
        RuleDefinitions.RollOutcome finalOutcome)
    {
        try
        {
            Main.Info(
                $"{Prefix} reality-revision mode={mode} kind={rollKind} " +
                $"helper={helper?.RulesetCharacter?.Guid ?? 0} " +
                $"observed={observed?.RulesetCharacter?.Guid ?? 0} " +
                $"originalOutcome={originalOutcome} finalOutcome={finalOutcome}");
        }
        catch
        {
            // Diagnostics must never affect gameplay.
        }
    }

    private static void RecordConditionLifecycle(
        string operation,
        RulesetCharacter target,
        RulesetCondition rulesetCondition)
    {
        try
        {
            var definition = rulesetCondition?.ConditionDefinition;
            var spriteReference = definition?.GuiPresentation?.SpriteReference;
            var spriteGuid = string.IsNullOrEmpty(spriteReference?.AssetGUID)
                ? "<none>"
                : spriteReference.AssetGUID;

            Main.Info(
                $"{Prefix} condition-{operation} " +
                $"target={target?.Guid ?? 0} condition={definition?.Name ?? "<null>"} " +
                $"remainingRounds={rulesetCondition?.RemainingRounds ?? 0} spriteGuid={spriteGuid}");
        }
        catch
        {
            // Diagnostics must never affect gameplay.
        }
    }

    private sealed class ConditionLifecycle : IOnConditionAddedOrRemoved
    {
        public void OnConditionAdded(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            RecordConditionLifecycle("added", target, rulesetCondition);
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            RecordConditionLifecycle("removed", target, rulesetCondition);
        }
    }
}
