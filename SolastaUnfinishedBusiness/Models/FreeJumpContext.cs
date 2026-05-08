using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using TA;
using static ActionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class FreeJumpContext
{
    private const int FeetPerCell = 5;
    private const int PreferredMaxHorizontalCells = 6;
    private const int AbsoluteMaxHorizontalCells = 8;
    private const int MaxVerticalCells = 4;
    private const int MaxDownwardCells = MaxVerticalCells;
    private const int MaxCandidateCells = 64;
    private const int JumpSpellVerticalBonusCells = 1;
    private const int JumpSpellDcReduction = 5;
    private const string BonusActionMoveMarker = "UB:BonusActionFreeJump";
    private const string FlightSuspendedConditionName = "ConditionFlightSuspended";

    private static readonly Dictionary<ulong, ScopeData> ActiveScopes = [];
    private static readonly FieldInfo ChainEvaluatedField =
        AccessTools.Field(typeof(CharacterActionChainParams), "<Evaluated>k__BackingField");
    private static readonly FieldInfo ChainTotalCostField =
        AccessTools.Field(typeof(CharacterActionChainParams), "<TotalCost>k__BackingField");
    private static readonly FieldInfo ChainEvaluationInProgressField =
        AccessTools.Field(typeof(CharacterActionChainParams), "evaluationInProgress");
    private static readonly FieldInfo ChainCosmeticEvaluationField =
        AccessTools.Field(typeof(CharacterActionChainParams), "cosmeticEvaluation");
    private static readonly FieldInfo ChainIndexEvaluatedActionField =
        AccessTools.Field(typeof(CharacterActionChainParams), "indexEvaluatedAction");
    private static readonly FieldInfo ChainPathFirstMoveField =
        AccessTools.Field(typeof(CharacterActionChainParams), "pathFirstMove");
    private static readonly FieldInfo ChainPathFirstMoveFromPositionField =
        AccessTools.Field(typeof(CharacterActionChainParams), "pathFirstMoveFromPosition");

    private enum ScopeKind
    {
        Exploration,
        ExplorationMove,
        BonusActionCursor,
        BonusActionMove,
        AiMove,
        AiTurn
    }

    internal enum FreeJumpPreviewOutcome
    {
        NoCheck,
        AthleticsCheck,
        AutomaticFailure
    }

    private enum RejectionReason
    {
        SamePosition,
        OutOfRange,
        NoPositioningService,
        NoDestinationSector,
        BlockedTrajectory,
        CannotPlace,
        CannotStay,
        Dangerous,
        NoGridNode
    }

    internal readonly struct FreeJumpCheckPreview(
        FreeJumpPreviewOutcome outcome,
        int moveCost,
        int checkDc,
        int successChance,
        RuleDefinitions.AdvantageType affinity,
        string reason)
    {
        internal FreeJumpPreviewOutcome Outcome { get; } = outcome;
        internal int MoveCost { get; } = moveCost;
        internal bool RequiresAthleticsCheck { get; } = outcome == FreeJumpPreviewOutcome.AthleticsCheck;
        internal bool IsAutomaticFailure { get; } = outcome == FreeJumpPreviewOutcome.AutomaticFailure;
        internal int CheckDc { get; } = checkDc;
        internal int SuccessChance { get; } = successChance;
        internal RuleDefinitions.AdvantageType Affinity { get; } = affinity;
        internal string Reason { get; } = reason;
    }

    internal readonly struct FreeJumpCandidateInfo(FreeJumpCheckPreview preview, bool bypassesObstacle)
    {
        internal FreeJumpCheckPreview Preview { get; } = preview;
        internal bool BypassesObstacle { get; } = bypassesObstacle;
    }

    private readonly struct FreeJumpProfile(
        int maxHorizontalCells,
        int maxVerticalCells,
        int athleticsBonus,
        bool hasJumpSpell)
    {
        internal int MaxHorizontalCells { get; } = maxHorizontalCells;
        internal int MaxVerticalCells { get; } = maxVerticalCells;
        internal int AthleticsBonus { get; } = athleticsBonus;
        internal bool HasJumpSpell { get; } = hasJumpSpell;
    }

    private sealed class ScopeData(ScopeKind kind, GameLocationCharacter character, int3 startPosition,
        FreeJumpProfile profile, int3 targetPosition = default, bool hasTargetPosition = false)
    {
        internal ScopeKind Kind { get; } = kind;
        internal GameLocationCharacter Character { get; } = character;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 TargetPosition { get; } = targetPosition;
        internal bool HasTargetPosition { get; } = hasTargetPosition;
        internal FreeJumpProfile Profile { get; } = profile;
        internal HashSet<int3> Candidates { get; } = [];
        internal HashSet<int3> FailedTargets { get; set; } = [];
        internal bool BonusActionSpent { get; set; }
        internal bool JumpSignalReceived { get; set; }
        internal bool JumpLandingFailed { get; set; }
        internal int3 JumpSignalStart { get; set; }
        internal int3 JumpSignalFinish { get; set; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly ulong _guid;
        private readonly ScopeData _current;
        private readonly ScopeData _previous;
        private bool _disposed;

        internal Scope(ulong guid, ScopeData current, ScopeData previous)
        {
            _guid = guid;
            _current = current;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_previous == null)
            {
                ActiveScopes.Remove(_guid);
            }
            else
            {
                ActiveScopes[_guid] = _previous;
            }

            _disposed = true;
        }
    }

    internal static bool IsBonusActionFreeJump(Id actionId)
    {
        return actionId == (Id)ExtraActionId.BonusActionFreeJump;
    }

    internal static bool CanUseAction(GameLocationCharacter character)
    {
        return CanUseBattleFreeJump(character, true, out _);
    }

    internal static bool CanExecuteBonusActionMove(GameLocationCharacter character)
    {
        return CanUseBattleFreeJump(character, false, out _);
    }

    internal static ActionStatus GetExplorationToggleActionStatus(GameLocationCharacter character, ActionScope scope)
    {
        if (!Main.Settings.EnableBonusActionFreeJump || scope != ActionScope.Exploration)
        {
            return ActionStatus.Unavailable;
        }

        return CanShowExplorationFreeJumpToggle(character, out _)
            ? ActionStatus.Available
            : ActionStatus.Unavailable;
    }

    internal static ActionStatus GetActionStatus(GameLocationCharacter character, ActionScope scope,
        ActionStatus actionTypeStatus, bool ignoreMovePoints)
    {
        if (!Main.Settings.EnableBonusActionFreeJump || scope != ActionScope.Battle)
        {
            return ActionStatus.Unavailable;
        }

        if (actionTypeStatus == ActionStatus.Irrelevant)
        {
            actionTypeStatus = character.GetActionTypeStatus(ActionType.Bonus, scope, ignoreMovePoints);
        }

        if (actionTypeStatus != ActionStatus.Available)
        {
            return actionTypeStatus == ActionStatus.Spent ? ActionStatus.Unavailable : actionTypeStatus;
        }

        return CanUseAction(character) ? ActionStatus.Available : ActionStatus.Unavailable;
    }

    internal static void RefreshActionAvailability(
        List<Id> actions,
        GameLocationCharacter character,
        ActionScope panelScope,
        ActionType panelType,
        bool inBattle)
    {
        var actionId = (Id)ExtraActionId.BonusActionFreeJump;
        var explorationToggleId = (Id)ExtraActionId.ExplorationFreeJumpToggle;

        actions.Remove(actionId);
        actions.Remove(explorationToggleId);

        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            return;
        }

        if (panelScope == ActionScope.Exploration)
        {
            if (panelType is not (ActionType.None or ActionType.NoCost))
            {
                return;
            }

            RefreshExplorationToggleAvailability(actions, character, explorationToggleId);
            return;
        }

        if (panelScope != ActionScope.Battle || panelType != ActionType.Bonus)
        {
            return;
        }

        if (!inBattle)
        {
            return;
        }

        if (!CanUseBattleFreeJump(character, true, out _))
        {
            return;
        }

        var insertIndex = actions.IndexOf(Id.DashBonus);

        actions.Insert(insertIndex >= 0 ? insertIndex + 1 : actions.Count, actionId);
    }

    internal static bool TryActivateBattleSelection(CharacterActionPanel panel)
    {
        if (!IsBonusActionFreeJump(panel?.actionId ?? Id.NoAction))
        {
            return false;
        }

        var character = panel.GuiCharacter.GameLocationCharacter;

        if (!CanUseBattleFreeJump(character, true, out _))
        {
            return true;
        }

        var cursorService = ServiceRepository.GetService<ICursorService>();

        if (cursorService == null)
        {
            return true;
        }

        var cursor = cursorService.GetCursor<CursorLocationBattleFriendlyTurn>();

        cursorService.ActivateCursor<CursorLocationBattleFriendlyTurn>();
        cursor.constrainedMovementMode = (Id)ExtraActionId.BonusActionFreeJump;
        cursor.movementCap = -1;
        cursor.ComputeValidDestinations();
        cursor.RefreshVisibleDestinationsGrid();

        RefreshBattleSelectionCaption(cursor);

        return true;
    }

    internal static void CancelBattleSelectionIfActive(CharacterActionPanel panel, Id nextActionId)
    {
        if (panel == null || IsBonusActionFreeJump(nextActionId))
        {
            return;
        }

        var cursorService = ServiceRepository.GetService<ICursorService>();
        var cursor = cursorService?.GetCursor<CursorLocationBattleFriendlyTurn>();

        if (cursor == null || !IsBonusActionFreeJump(cursor.constrainedMovementMode))
        {
            return;
        }

        CancelBattleSelection(cursor);
    }

    internal static bool TryBuildBattleFreeJumpActionChain(CursorLocationBattleFriendlyTurn cursor, Id actionId)
    {
        if (!IsBonusActionFreeJump(actionId))
        {
            return false;
        }

        cursor.actionChainParams.Clear();

        if (!CanPrepareBattleFreeJumpMove(cursor, out var character, out var destination, out var reason))
        {
            CancelBattleSelection(cursor);
            return true;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (positioningService == null || characterService == null)
        {
            CancelBattleSelection(cursor);
            return true;
        }

        cursor.actionChainParams.ActingCharacter = character;

        characterService.FindGroupAndLeaderOfCharacter(character, cursor.charactersList, out var leader);
        positioningService.ComputeMovementPriorities(cursor.charactersList, leader);

        var orientation = positioningService.GetLocationOrientationFromTo(
            character.LocationPosition, destination, default);
        var moveParams = new CharacterActionParams(
            character, Id.TacticalMove, character.GetMoveStance(), destination, orientation)
        {
            BoolParameter3 = false,
            BoolParameter5 = false,
            StringParameter = BonusActionMoveMarker
        };

        cursor.actionChainParams.AddActionParams(moveParams);

        return true;
    }

    internal static bool TryCancelInvalidBattleFreeJumpAction(CursorLocationBattleFriendlyTurn cursor, Id actionId)
    {
        if (cursor == null ||
            !IsBonusActionFreeJump(actionId) ||
            !IsBonusActionFreeJump(cursor.constrainedMovementMode))
        {
            return false;
        }

        if (CanPrepareBattleFreeJumpMove(cursor, out _, out _, out _))
        {
            return false;
        }

        CancelBattleSelection(cursor);

        return true;
    }

    private static bool CanPrepareBattleFreeJumpMove(
        CursorLocationBattleFriendlyTurn cursor,
        out GameLocationCharacter character,
        out int3 destination,
        out string reason)
    {
        character = cursor?.actingCharacter;
        destination = cursor == null ? default : cursor.HoveredLocation;

        if (character == null)
        {
            reason = "no-character";
            return false;
        }

        if (!CanUseBattleFreeJump(character, true, out reason))
        {
            return false;
        }

        if (!CanReach(character, character.LocationPosition, destination))
        {
            reason = "cannot-reach";
            return false;
        }

        if (!CanAffordFreeJumpMove(character, character.LocationPosition, destination))
        {
            reason = "cannot-afford";
            return false;
        }

        if (ServiceRepository.GetService<IGameLocationPositioningService>() == null ||
            ServiceRepository.GetService<IGameLocationCharacterService>() == null)
        {
            reason = "no-service";
            return false;
        }

        reason = "available";
        return true;
    }

    private static void CancelBattleSelection(CursorLocationBattleFriendlyTurn cursor)
    {
        if (cursor == null)
        {
            return;
        }

        cursor.actionChainParams.Clear();
        cursor.validDestinations.Clear();
        cursor.allVisibleDestinations.Clear();
        cursor.constrainedMovementMode = Id.NoAction;
        cursor.movementCap = -1;
        cursor.RefreshVisibleDestinationsGrid();
        CursorLocation.CaptionLineDismissed();
    }

    internal static void MarkBonusActionMove(CharacterActionParams actionParams)
    {
        if (actionParams != null)
        {
            actionParams.StringParameter = BonusActionMoveMarker;
        }
    }

    internal static bool TrySeedBattleFreeJumpEvaluation(CharacterActionChainParams chainParams, bool cosmetic)
    {
        if (!TryGetMarkedBonusActionMove(
                chainParams,
                out var character,
                out var destination))
        {
            return false;
        }

        var start = character.MoveStepInProgress
            ? character.MoveStepDestination
            : character.LocationPosition;

        if (!TryBuildFreeJumpPathStep(
                character,
                start,
                destination,
                out var pathStep,
                out _))
        {
            SeedFreeJumpEvaluationFailure(chainParams);
            return true;
        }

        if (!TrySeedFreeJumpEvaluation(chainParams, start, pathStep, cosmetic))
        {
            return false;
        }

        return true;
    }

    internal static void RefreshBattleSelectionCaption(CursorLocationBattleFriendlyTurn cursor)
    {
        if (!Main.Settings.EnableBonusActionFreeJump ||
            cursor == null ||
            !IsBonusActionFreeJump(cursor.constrainedMovementMode))
        {
            return;
        }

        var character = cursor.actingCharacter;
        var destination = cursor.HoveredLocation;
        var captionContent = Gui.Localize("Action/&BonusActionFreeJumpDescription");

        if (character != null &&
            cursor.allVisibleDestinations.Contains(destination) &&
            CanReach(character, character.LocationPosition, destination) &&
            TryComputeFreeJumpPreview(character, character.LocationPosition, destination, true, out var preview))
        {
            captionContent = FormatFreeJumpPreview(preview);
        }

        CursorLocation.CaptionLineChanged(
            "Action/&BonusActionFreeJumpTitle",
            captionContent,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            true);
    }

    internal static IDisposable BeginExplorationPathfinding(GameLocationCharacter character)
    {
        if (!CanUseExplorationFreeJump(character))
        {
            return null;
        }

        return BeginScope(character, ScopeKind.Exploration);
    }

    private static IDisposable BeginExplorationMovePathfinding(CharacterActionMove action)
    {
        var character = action?.ActingCharacter;

        if (!CanUseExplorationFreeJump(character))
        {
            return null;
        }

        var destination = action.DestinationPosition;

        if (!CanReach(character, character.LocationPosition, destination))
        {
            DisableExplorationFreeJump(character);

            return null;
        }

        var scope = BeginScope(character, ScopeKind.ExplorationMove, destination, true);

        if (scope != null)
        {
            DisableExplorationFreeJump(character);
        }
        else
        {
            DisableExplorationFreeJump(character);
        }

        return scope;
    }

    private static IDisposable BeginAiMovePathfinding(CharacterActionMove action)
    {
        var character = action?.ActingCharacter;

        if (character == null ||
            !ActiveScopes.TryGetValue(character.Guid, out var aiScope) ||
            aiScope.Kind != ScopeKind.AiTurn)
        {
            return null;
        }

        var destination = action.DestinationPosition;

        if (!aiScope.Candidates.Contains(destination))
        {
            return null;
        }

        if (aiScope.FailedTargets.Contains(destination))
        {
            return null;
        }

        if (!CanUseAiFreeJump(character))
        {
            return null;
        }

        if (!CanAffordFreeJumpMove(character, character.LocationPosition, destination))
        {
            return null;
        }

        if (!TryGetAiCandidateInfo(character, character.LocationPosition, destination, out var candidateInfo) ||
            !CombatAiContext.TryEvaluateFreeJumpDestination(
                character,
                character.LocationPosition,
                destination,
                candidateInfo.Preview,
                candidateInfo.BypassesObstacle,
                out _))
        {
            return null;
        }

        return BeginScope(character, ScopeKind.AiMove, destination, true);
    }

    internal static IDisposable BeginBonusActionPathfinding(GameLocationCharacter character, Id constrainedMovementMode)
    {
        if (!IsBonusActionFreeJump(constrainedMovementMode))
        {
            return null;
        }

        if (!CanUseBattleFreeJump(character, true, out _))
        {
            return null;
        }

        return BeginScope(character, ScopeKind.BonusActionCursor);
    }

    internal static IDisposable BeginAiTurn(GameLocationCharacter character)
    {
        return CanUseAiFreeJump(character) ? BeginScope(character, ScopeKind.AiTurn) : null;
    }

    internal static IDisposable BeginMovePathfinding(CharacterActionMove action)
    {
        if (!Main.Settings.EnableBonusActionFreeJump || action?.ActingCharacter == null)
        {
            return null;
        }

        var character = action.ActingCharacter;
        var destination = action.DestinationPosition;

        if (IsMarkedBonusActionMove(action))
        {
            if (!CanUseBattleFreeJump(character, true, out _))
            {
                return null;
            }

            if (!CanReach(character, character.LocationPosition, destination))
            {
                return null;
            }

            if (!CanAffordFreeJumpMove(character, character.LocationPosition, destination))
            {
                return null;
            }

            return BeginScope(character, ScopeKind.BonusActionMove, destination, true);
        }

        return IsBattleInProgress()
            ? BeginAiMovePathfinding(action)
            : BeginExplorationMovePathfinding(action);
    }

    internal static IEnumerator WithMovePathfindingScope(IEnumerator values, CharacterActionMove action)
    {
        var disableExplorationToggle = ShouldDisableExplorationFreeJumpAfterMove(action);
        var scope = BeginMovePathfinding(action);

        if (IsMarkedBonusActionMove(action) && scope == null)
        {
            yield break;
        }

        try
        {
            while (values.MoveNext())
            {
                yield return values.Current;
            }
        }
        finally
        {
            TrySpendBonusActionAfterMove(action);
            if (disableExplorationToggle)
            {
                DisableExplorationFreeJump(action?.ActingCharacter);
            }
            scope?.Dispose();
        }
    }

    internal static void AddPathfindingNeighbours(
        PathfindingGridNode currentNode,
        List<PathfindingNeighbour> neighbours,
        List<int> moveModes)
    {
        if (!CanAddPathfindingNeighbours(currentNode, neighbours, moveModes))
        {
            return;
        }

        var currentPosition = currentNode.AbsolutePosition;

        foreach (var scope in ActiveScopes.Values)
        {
            if (scope.StartPosition != currentPosition)
            {
                continue;
            }

            if (!CanUseScope(scope))
            {
                continue;
            }

            AddPathfindingNeighbours(currentNode, neighbours, scope);

            return;
        }
    }

    internal static bool ApplyBonusActionDestinations(
        GameLocationCharacter character,
        Id constrainedMovementMode,
        List<GameLocationCharacterDefinitions.PathStep> validDestinations)
    {
        if (!IsBonusActionFreeJump(constrainedMovementMode) ||
            character == null ||
            validDestinations == null ||
            !ActiveScopes.TryGetValue(character.Guid, out var scope))
        {
            return false;
        }

        for (var i = validDestinations.Count - 1; i >= 0; i--)
        {
            var destination = validDestinations[i].position;

            if (!scope.Candidates.Contains(destination) ||
                !CanAffordFreeJumpMove(character, scope.StartPosition, destination))
            {
                validDestinations.RemoveAt(i);
            }
        }

        var existing = new HashSet<int3>();

        for (var i = 0; i < validDestinations.Count; i++)
        {
            existing.Add(validDestinations[i].position);
        }

        foreach (var destination in scope.Candidates)
        {
            if (!CanAffordFreeJumpMove(character, scope.StartPosition, destination))
            {
                continue;
            }

            if (!existing.Add(destination))
            {
                continue;
            }

            validDestinations.Add(BuildFreeJumpPathStep(scope, destination));
        }

        return true;
    }

    internal static bool CanReach(GameLocationCharacter character, int3 start, int3 destination)
    {
        return Main.Settings.EnableBonusActionFreeJump &&
               !IsFreeJumpSuppressedByFlight(character, out _) &&
               TryComputeProfile(character, out var profile) &&
               TryValidateCandidate(character, start, destination, profile, false, out _) &&
               CanAffordFreeJumpMove(character, start, destination);
    }

    internal static void TrySpendBonusActionAfterJump(
        GameLocationCharacter character,
        int3 start,
        int3 finish,
        bool landingFailed)
    {
        if (!Main.Settings.EnableBonusActionFreeJump ||
            character == null ||
            !ActiveScopes.TryGetValue(character.Guid, out var scope) ||
            !IsFreeJumpMoveScope(scope))
        {
            return;
        }

        scope.JumpSignalReceived = true;
        scope.JumpLandingFailed = landingFailed;
        scope.JumpSignalStart = start;
        scope.JumpSignalFinish = finish;

        if (landingFailed)
        {
            if (scope.Kind == ScopeKind.ExplorationMove)
            {
                DisableExplorationFreeJump(character);
            }

            return;
        }

        if (!IsBonusActionSpendingScope(scope) || scope.BonusActionSpent)
        {
            return;
        }

        if (start != scope.StartPosition)
        {
            return;
        }

        if (!IsScopeDestination(scope, finish))
        {
            return;
        }

        SpendBonusAction(character, scope, finish, "jump-finished");
    }

    private static void AddPathfindingNeighbours(
        PathfindingGridNode currentNode,
        List<PathfindingNeighbour> neighbours,
        ScopeData scope)
    {
        if (scope.HasTargetPosition && scope.Kind is
                (ScopeKind.BonusActionMove or ScopeKind.ExplorationMove or ScopeKind.AiMove))
        {
            ForceTargetFreeJumpNeighbour(currentNode, neighbours, scope);
            return;
        }

        EnumerateCandidatePositions(scope.StartPosition, scope.Profile, destination =>
        {
            if (scope.Candidates.Count >= MaxCandidateCells)
            {
                return false;
            }

            TryAddPathfindingNeighbour(currentNode, neighbours, scope, destination);

            return true;
        });
    }

    private static void ForceTargetFreeJumpNeighbour(
        PathfindingGridNode currentNode,
        List<PathfindingNeighbour> neighbours,
        ScopeData scope)
    {
        var destination = scope.TargetPosition;

        if (IsFreeJumpSuppressedByFlight(scope.Character, out _))
        {
            neighbours.Clear();
            return;
        }

        if (!TryBuildFreeJumpNeighbour(currentNode, scope, destination, out var neighbour, out _, out _))
        {
            neighbours.Clear();
            return;
        }

        neighbours.Clear();
        neighbours.Add(neighbour);

        scope.Candidates.Add(destination);

        if (IsBonusActionSpendingScope(scope))
        {
            SpendBonusAction(scope.Character, scope, destination, "target-edge");
        }
    }

    private static void TrySpendBonusActionAfterMove(CharacterActionMove action)
    {
        if (!Main.Settings.EnableBonusActionFreeJump || action?.ActingCharacter == null)
        {
            return;
        }

        var character = action.ActingCharacter;

        if (!ActiveScopes.TryGetValue(character.Guid, out var scope) ||
            !IsFreeJumpMoveScope(scope))
        {
            return;
        }

        var atTarget = IsScopeDestination(scope, character.LocationPosition);
        if (scope.Kind == ScopeKind.AiMove && !atTarget)
        {
            scope.FailedTargets.Add(scope.TargetPosition);
        }

        if (scope.Kind != ScopeKind.BonusActionMove || scope.BonusActionSpent)
        {
            return;
        }

        if (scope.JumpLandingFailed)
        {
            return;
        }

        if (!atTarget)
        {
            return;
        }

        if (!scope.JumpSignalReceived)
        {
            SpendBonusAction(character, scope, character.LocationPosition, "move-ended-no-jump-signal");
            return;
        }

        SpendBonusAction(character, scope, character.LocationPosition, "move-ended");
    }

    private static bool ShouldDisableExplorationFreeJumpAfterMove(CharacterActionMove action)
    {
        var character = action?.ActingCharacter;

        return Main.Settings.EnableBonusActionFreeJump &&
               character?.RulesetCharacter != null &&
               !IsBattleInProgress() &&
               IsExplorationFreeJumpEnabled(character);
    }

    private static void DisableExplorationFreeJump(GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;
        var actionId = (Id)ExtraActionId.ExplorationFreeJumpToggle;

        if (rulesetCharacter == null || !rulesetCharacter.IsToggleEnabled(actionId))
        {
            return;
        }

        rulesetCharacter.DisableToggle(actionId);
        rulesetCharacter.RefreshAttackModes();
        rulesetCharacter.CharacterRefreshed?.Invoke(rulesetCharacter);
    }

    private static bool IsBonusActionSpendingScope(ScopeData scope)
    {
        return scope.Kind is ScopeKind.BonusActionMove or ScopeKind.AiMove;
    }

    private static bool IsFreeJumpMoveScope(ScopeData scope)
    {
        return scope.Kind is ScopeKind.BonusActionMove or ScopeKind.ExplorationMove or ScopeKind.AiMove;
    }

    private static bool IsMarkedBonusActionMove(CharacterActionMove action)
    {
        return IsMarkedBonusActionMove(action?.ActionParams);
    }

    private static bool IsMarkedBonusActionMove(CharacterActionParams actionParams)
    {
        return actionParams?.StringParameter == BonusActionMoveMarker;
    }

    private static bool TryGetMarkedBonusActionMove(
        CharacterActionChainParams chainParams,
        out GameLocationCharacter character,
        out int3 destination)
    {
        character = null;
        destination = default;

        if (!Main.Settings.EnableBonusActionFreeJump || chainParams == null)
        {
            return false;
        }

        foreach (var actionParams in chainParams.GetActionsParams())
        {
            if (!IsMarkedBonusActionMove(actionParams) ||
                actionParams.ActionDefinition.Id != Id.TacticalMove ||
                actionParams.Positions.Count == 0)
            {
                continue;
            }

            character = actionParams.ActingCharacter ?? chainParams.ActingCharacter;
            destination = actionParams.Positions[0];

            return character != null;
        }

        return false;
    }

    private static bool TryBuildFreeJumpPathStep(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        out GameLocationCharacterDefinitions.PathStep pathStep,
        out string reason)
    {
        pathStep = default;

        if (!CanUseBattleFreeJump(character, true, out reason))
        {
            return false;
        }

        if (!TryComputeProfile(character, out var profile))
        {
            reason = "no-profile";
            return false;
        }

        if (!TryValidateCandidate(character, start, destination, profile, false, out var rejectionReason))
        {
            reason = rejectionReason.ToString();
            return false;
        }

        if (!CanAffordFreeJumpMove(character, start, destination))
        {
            reason = "cannot-afford";
            return false;
        }

        pathStep = BuildFreeJumpPathStep(start, destination);
        reason = "available";

        return true;
    }

    private static bool TrySeedFreeJumpEvaluation(
        CharacterActionChainParams chainParams,
        int3 start,
        GameLocationCharacterDefinitions.PathStep pathStep,
        bool cosmetic)
    {
        if (!CanAccessActionChainFields())
        {
            return false;
        }

        chainParams.StopEvaluation();
        chainParams.EvaluationSteps.Clear();

        var path = GetOrCreateFirstMovePath(chainParams);

        path.Clear();
        path.Add(pathStep);

        chainParams.EvaluationSteps.Add(
            new CharacterActionChainParams.EvaluationStep(Id.TacticalMove)
            {
                Position = pathStep.position,
                MoveFlags = pathStep.flags,
                Cost = pathStep.moveCost
            });

        ChainPathFirstMoveFromPositionField.SetValue(chainParams, start);
        ChainTotalCostField.SetValue(chainParams, pathStep.moveCost);
        ChainEvaluatedField.SetValue(chainParams, true);
        ChainEvaluationInProgressField.SetValue(chainParams, false);
        ChainCosmeticEvaluationField.SetValue(chainParams, cosmetic);
        ChainIndexEvaluatedActionField.SetValue(chainParams, -1);

        return true;
    }

    private static void SeedFreeJumpEvaluationFailure(CharacterActionChainParams chainParams)
    {
        if (!CanAccessActionChainFields())
        {
            return;
        }

        chainParams.StopEvaluation();
        chainParams.EvaluationSteps.Clear();
        GetOrCreateFirstMovePath(chainParams).Clear();

        ChainPathFirstMoveFromPositionField.SetValue(chainParams, int3.zero);
        ChainTotalCostField.SetValue(chainParams, 0);
        ChainEvaluatedField.SetValue(chainParams, true);
        ChainEvaluationInProgressField.SetValue(chainParams, false);
        ChainCosmeticEvaluationField.SetValue(chainParams, true);
        ChainIndexEvaluatedActionField.SetValue(chainParams, -1);
    }

    private static List<GameLocationCharacterDefinitions.PathStep> GetOrCreateFirstMovePath(
        CharacterActionChainParams chainParams)
    {
        var path = ChainPathFirstMoveField.GetValue(chainParams)
            as List<GameLocationCharacterDefinitions.PathStep>;

        if (path != null)
        {
            return path;
        }

        path = [];
        ChainPathFirstMoveField.SetValue(chainParams, path);

        return path;
    }

    private static bool CanAccessActionChainFields()
    {
        return ChainEvaluatedField != null &&
               ChainTotalCostField != null &&
               ChainEvaluationInProgressField != null &&
               ChainCosmeticEvaluationField != null &&
               ChainIndexEvaluatedActionField != null &&
               ChainPathFirstMoveField != null &&
               ChainPathFirstMoveFromPositionField != null;
    }

    private static bool IsScopeDestination(ScopeData scope, int3 destination)
    {
        return scope.HasTargetPosition
            ? scope.TargetPosition == destination
            : scope.Candidates.Contains(destination);
    }

    private static void SpendBonusAction(
        GameLocationCharacter character,
        ScopeData scope,
        int3 destination,
        string source)
    {
        if (scope.BonusActionSpent)
        {
            return;
        }

        if (character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available)
        {
            return;
        }

        character.SpendActionType(ActionType.Bonus);
        scope.BonusActionSpent = true;
    }

    private static void TryAddPathfindingNeighbour(
        PathfindingGridNode currentNode,
        List<PathfindingNeighbour> neighbours,
        ScopeData scope,
        int3 destination)
    {
        if (scope.Candidates.Contains(destination))
        {
            return;
        }

        if (scope.Kind == ScopeKind.AiTurn && scope.FailedTargets.Contains(destination))
        {
            return;
        }

        if (TryFindNeighbour(neighbours, destination, out var existingNeighbour))
        {
            if (!TryValidateCandidate(
                    scope.Character,
                    scope.StartPosition,
                    destination,
                    scope.Profile,
                    scope.Kind == ScopeKind.AiTurn,
                    out _,
                    out var bypassesObstacle))
            {
                return;
            }

            if (!CanUseAiCandidate(scope, destination, bypassesObstacle))
            {
                return;
            }

            if (ShouldTrackExistingNeighbour(scope, destination, existingNeighbour))
            {
                scope.Candidates.Add(destination);
            }

            return;
        }

        if (!TryBuildFreeJumpNeighbour(
                currentNode,
                scope,
                destination,
                out var neighbour,
                out _,
                out var bypassesObstacleForNeighbour))
        {
            return;
        }

        if (!CanUseAiCandidate(scope, destination, bypassesObstacleForNeighbour))
        {
            return;
        }

        neighbours.Add(neighbour);
        scope.Candidates.Add(destination);
    }

    private static bool TryBuildFreeJumpNeighbour(
        PathfindingGridNode currentNode,
        ScopeData scope,
        int3 destination,
        out PathfindingNeighbour neighbour,
        out RejectionReason reason,
        out bool bypassesObstacle)
    {
        neighbour = default;
        bypassesObstacle = false;

        if (!TryValidateCandidate(
                scope.Character,
                scope.StartPosition,
                destination,
                scope.Profile,
                scope.Kind == ScopeKind.AiTurn,
                out reason,
                out bypassesObstacle))
        {
            return false;
        }

        var delta = destination - scope.StartPosition;
        var targetNode = currentNode.GetGridNode(delta.x, delta.y, delta.z);

        if (targetNode == null)
        {
            reason = RejectionReason.NoGridNode;
            return false;
        }

        const PathfindingNode.InformationFlag flags = PathfindingNode.InformationFlag.Jump;
        const RuleDefinitions.MoveMode moveMode = RuleDefinitions.MoveMode.Walk;

        var moveCost = ComputeFreeJumpMovementCost(delta);
        var gCost = currentNode.GetGCost(currentNode, targetNode, moveCost);

        targetNode.UsedMoveMode = moveMode;
        neighbour = new PathfindingNeighbour(targetNode, gCost, (byte)moveCost, flags);
        reason = default;

        return true;
    }

    private static bool ShouldTrackExistingNeighbour(
        ScopeData scope,
        int3 destination,
        PathfindingNeighbour existingNeighbour)
    {
        var delta = destination - scope.StartPosition;

        if (delta.y != 0)
        {
            return true;
        }

        const PathfindingNode.InformationFlag movementFlags =
            PathfindingNode.InformationFlag.Jump |
            PathfindingNode.InformationFlag.Climbing |
            PathfindingNode.InformationFlag.EasyClimb |
            PathfindingNode.InformationFlag.Vault;

        return (existingNeighbour.flags & movementFlags) != 0;
    }

    private static bool CanUseAiCandidate(ScopeData scope, int3 destination, bool bypassesObstacle)
    {
        if (scope.Kind != ScopeKind.AiTurn)
        {
            return true;
        }

        if (!TryComputeFreeJumpPreview(
                scope.Character,
                scope.StartPosition,
                destination,
                true,
                scope.Profile,
                out var preview))
        {
            return false;
        }

        return CombatAiContext.TryEvaluateFreeJumpDestination(
            scope.Character,
            scope.StartPosition,
            destination,
            preview,
            bypassesObstacle,
            out _);
    }

    private static bool TryGetAiCandidateInfo(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        out FreeJumpCandidateInfo candidateInfo)
    {
        candidateInfo = default;

        if (!TryComputeProfile(character, out var profile))
        {
            return false;
        }

        return TryGetAiCandidateInfo(character, start, destination, profile, out candidateInfo);
    }

    private static bool TryGetAiCandidateInfo(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        FreeJumpProfile profile,
        out FreeJumpCandidateInfo candidateInfo)
    {
        candidateInfo = default;

        if (!TryValidateCandidate(character, start, destination, profile, true, out _, out var bypassesObstacle) ||
            !TryComputeFreeJumpPreview(character, start, destination, true, profile, out var preview))
        {
            return false;
        }

        candidateInfo = new FreeJumpCandidateInfo(preview, bypassesObstacle);

        return true;
    }

    private static bool TryValidateCandidate(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        FreeJumpProfile profile,
        bool avoidDangerousPosition,
        out RejectionReason reason)
    {
        return TryValidateCandidate(
            character,
            start,
            destination,
            profile,
            avoidDangerousPosition,
            out reason,
            out _);
    }

    private static bool TryValidateCandidate(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        FreeJumpProfile profile,
        bool avoidDangerousPosition,
        out RejectionReason reason,
        out bool bypassesObstacle)
    {
        bypassesObstacle = false;

        var delta = destination - start;
        var horizontalCells = Math.Max(Math.Abs(delta.x), Math.Abs(delta.z));

        if (destination == start)
        {
            reason = RejectionReason.SamePosition;
            return false;
        }

        if (delta.y < -MaxDownwardCells ||
            delta.y > profile.MaxVerticalCells ||
            horizontalCells > profile.MaxHorizontalCells)
        {
            reason = RejectionReason.OutOfRange;
            return false;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null)
        {
            reason = RejectionReason.NoPositioningService;
            return false;
        }

        if (!positioningService.TryGetSectorFromPosition(destination, out _))
        {
            reason = RejectionReason.NoDestinationSector;
            return false;
        }

        if (!TryValidateFreeJumpTrajectory(
                positioningService,
                start,
                destination,
                profile,
                out reason,
                out bypassesObstacle))
        {
            return false;
        }

        if (!CanLandAtCandidate(positioningService, character, destination, out reason))
        {
            return false;
        }

        if (avoidDangerousPosition && positioningService.IsDangerousPosition(character, destination))
        {
            reason = RejectionReason.Dangerous;
            return false;
        }

        reason = default;
        return true;
    }

    private static bool TryValidateFreeJumpTrajectory(
        IGameLocationPositioningService positioningService,
        int3 start,
        int3 destination,
        FreeJumpProfile profile,
        out RejectionReason reason,
        out bool bypassesObstacle)
    {
        bypassesObstacle = false;

        if (!positioningService.RaycastGrid(
                start,
                destination,
                CellFlags.Surface.MovementBlocker,
                CellFlags.Side.All))
        {
            reason = default;
            return true;
        }

        if (profile.MaxVerticalCells < 1)
        {
            reason = RejectionReason.BlockedTrajectory;
            return false;
        }

        var delta = destination - start;
        var clearance = Clamp(Math.Max(1, delta.y + 1), 1, profile.MaxVerticalCells);
        var lift = new int3(0, clearance, 0);
        var raisedStart = start + lift;
        var raisedDestination = destination + lift;

        if (!positioningService.TryGetSectorFromPosition(raisedStart, out _) ||
            !positioningService.TryGetSectorFromPosition(raisedDestination, out _) ||
            positioningService.RaycastGrid(
                start,
                raisedStart,
                CellFlags.Surface.MovementBlocker,
                CellFlags.Side.All) ||
            positioningService.RaycastGrid(
                raisedStart,
                raisedDestination,
                CellFlags.Surface.MovementBlocker,
                CellFlags.Side.All) ||
            positioningService.RaycastGrid(
                raisedDestination,
                destination,
                CellFlags.Surface.MovementBlocker,
                CellFlags.Side.All))
        {
            reason = RejectionReason.BlockedTrajectory;
            return false;
        }

        bypassesObstacle = true;
        reason = default;
        return true;
    }

    private static bool CanLandAtCandidate(
        IGameLocationPositioningService positioningService,
        GameLocationCharacter character,
        int3 destination,
        out RejectionReason reason)
    {
        if (!positioningService.CanPlaceCharacter(character, destination, CellHelpers.PlacementMode.Station))
        {
            reason = RejectionReason.CannotPlace;
            return false;
        }

        var strictStay = positioningService.CanCharacterStayAtPosition_Floor(
            character, destination, onlyCheckCellsWithRealGround: true);

        if (strictStay)
        {
            reason = default;
            return true;
        }

        var relaxedStay = positioningService.CanCharacterStayAtPosition_Floor(
            character, destination, onlyCheckCellsWithRealGround: false);

        if (!relaxedStay)
        {
            reason = RejectionReason.CannotStay;
            return false;
        }

        reason = default;
        return true;
    }

    private static bool TryFindNeighbour(
        List<PathfindingNeighbour> neighbours,
        int3 destination,
        out PathfindingNeighbour neighbour)
    {
        for (var i = 0; i < neighbours.Count; i++)
        {
            if (neighbours[i].node.AbsolutePosition == destination)
            {
                neighbour = neighbours[i];
                return true;
            }
        }

        neighbour = default;
        return false;
    }

    private static bool CanUseScope(ScopeData scope)
    {
        var character = scope.Character;

        return scope.Kind switch
        {
            ScopeKind.BonusActionCursor => CanUseAction(character),
            ScopeKind.BonusActionMove => CanExecuteBonusActionMove(character),
            ScopeKind.AiMove => CanUseAiFreeJump(character),
            ScopeKind.AiTurn => CanUseAiFreeJump(character),
            ScopeKind.Exploration => CanUseExplorationFreeJump(character),
            ScopeKind.ExplorationMove => CanShowExplorationFreeJumpToggle(character, out _),
            _ => false
        };
    }

    private static bool CanAffordFreeJumpMove(GameLocationCharacter character, int3 start, int3 destination)
    {
        return !IsBattleInProgress() ||
               character.RemainingTacticalMoves >= ComputeFreeJumpMovementCost(destination - start);
    }

    internal static bool IsActiveFreeJumpMove(GameLocationCharacter character, int3 start, int3 finish)
    {
        return Main.Settings.EnableBonusActionFreeJump &&
               character != null &&
               ActiveScopes.TryGetValue(character.Guid, out var scope) &&
               IsFreeJumpMoveScope(scope) &&
               start == scope.StartPosition &&
               IsScopeDestination(scope, finish);
    }

    internal static bool TryComputeFreeJumpPreview(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        bool applyFreeJumpBonuses,
        out FreeJumpCheckPreview preview)
    {
        preview = default;

        if (!TryComputeProfile(character, out var profile))
        {
            return false;
        }

        return TryComputeFreeJumpPreview(character, start, destination, applyFreeJumpBonuses, profile, out preview);
    }

    private static bool TryComputeFreeJumpPreview(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        bool applyFreeJumpBonuses,
        FreeJumpProfile profile,
        out FreeJumpCheckPreview preview)
    {
        var moveCost = ComputeFreeJumpMovementCost(destination - start);
        var requiresAthleticsCheck = TryComputeAthleticsCheck(
            character,
            start,
            destination,
            profile,
            applyFreeJumpBonuses,
            out var checkDc,
            out var affinity,
            out var reason);

        if (!requiresAthleticsCheck)
        {
            var outcome = CharacterActionMoveStepJump.AutomaticPenalty(character, start, destination)
                ? FreeJumpPreviewOutcome.AutomaticFailure
                : FreeJumpPreviewOutcome.NoCheck;
            var outcomeReason = outcome == FreeJumpPreviewOutcome.AutomaticFailure
                ? "automatic-penalty"
                : reason;

            preview = new FreeJumpCheckPreview(
                outcome,
                moveCost,
                0,
                0,
                RuleDefinitions.AdvantageType.None,
                outcomeReason);

            return true;
        }

        var successChance = ComputeSuccessChancePercent(checkDc, profile.AthleticsBonus, affinity);

        preview = new FreeJumpCheckPreview(
            FreeJumpPreviewOutcome.AthleticsCheck,
            moveCost,
            checkDc,
            successChance,
            affinity,
            reason);

        return true;
    }

    private static bool TryComputeAthleticsCheck(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        FreeJumpProfile profile,
        bool applyFreeJumpBonuses,
        out int checkDc,
        out RuleDefinitions.AdvantageType affinity,
        out string reason)
    {
        checkDc = 0;
        affinity = RuleDefinitions.AdvantageType.None;
        reason = "no-check-required";

        var rulesetCharacter = character?.RulesetCharacter;
        var usesArmorPenaltyRules = Main.Settings.ModifyJumpRulesForArmorAndEncumberance;
        var needsAthleticsCheck = CharacterActionMoveStepJump.NeedsAthleticsCheck(character, start, destination);
        var isWearingHeavy = rulesetCharacter?.IsWearingHeavyArmor() == true && usesArmorPenaltyRules;
        var isWearingMedium = rulesetCharacter?.IsWearingMediumArmor() == true && usesArmorPenaltyRules;

        if (!needsAthleticsCheck && !isWearingHeavy && !isWearingMedium)
        {
            return false;
        }

        var distance = Math.Max(1, (int)int3.Distance(start, destination));

        checkDc = usesArmorPenaltyRules ? distance * FeetPerCell : 15;
        affinity = isWearingHeavy
            ? RuleDefinitions.AdvantageType.Disadvantage
            : RuleDefinitions.AdvantageType.None;
        reason = needsAthleticsCheck
            ? "jump-rule"
            : isWearingHeavy
                ? "heavy-armor"
                : "medium-armor";

        if (applyFreeJumpBonuses && profile.HasJumpSpell)
        {
            checkDc = Math.Max(1, checkDc - JumpSpellDcReduction);
        }

        return true;
    }

    private static string FormatFreeJumpPreview(FreeJumpCheckPreview preview)
    {
        if (preview.IsAutomaticFailure)
        {
            return Gui.Format(
                "Action/&FreeJumpAutomaticFailureFormat",
                preview.MoveCost.ToString());
        }

        if (!preview.RequiresAthleticsCheck)
        {
            return Gui.Format(
                "Action/&FreeJumpMoveCostFormat",
                preview.MoveCost.ToString());
        }

        var parts = new List<string>
        {
            Gui.Format(
                "Action/&FreeJumpCheckFormat",
                preview.MoveCost.ToString(),
                preview.CheckDc.ToString())
        };

        var affinityText = GetAffinityText(preview.Affinity);

        parts.Add(string.IsNullOrEmpty(affinityText)
            ? Gui.Format(
                "Action/&FreeJumpSuccessChance",
                preview.SuccessChance.ToString())
            : Gui.Format(
                "Action/&FreeJumpSuccessChanceWithAffinity",
                affinityText,
                preview.SuccessChance.ToString()));

        return string.Join(" ", parts);
    }

    private static string GetAffinityText(RuleDefinitions.AdvantageType affinity)
    {
        if (affinity == RuleDefinitions.AdvantageType.Advantage)
        {
            return Gui.Localize("Action/&FreeJumpAdvantage");
        }

        if (affinity == RuleDefinitions.AdvantageType.Disadvantage)
        {
            return Gui.Localize("Action/&FreeJumpDisadvantage");
        }

        return string.Empty;
    }

    private static int ComputeSuccessChancePercent(
        int checkDc,
        int athleticsBonus,
        RuleDefinitions.AdvantageType affinity)
    {
        var minimumDie = checkDc - athleticsBonus;
        var successfulFaces = Math.Max(0, Math.Min(20, 21 - minimumDie));
        var success = successfulFaces / 20.0;

        if (affinity == RuleDefinitions.AdvantageType.Advantage)
        {
            var fail = 1.0 - success;
            success = 1.0 - fail * fail;
        }
        else if (affinity == RuleDefinitions.AdvantageType.Disadvantage)
        {
            success *= success;
        }

        return Math.Max(0, Math.Min(100, (int)Math.Round(success * 100, MidpointRounding.AwayFromZero)));
    }

    private static void RefreshExplorationToggleAvailability(
        List<Id> actions,
        GameLocationCharacter character,
        Id explorationToggleId)
    {
        if (!CanShowExplorationFreeJumpToggle(character, out _))
        {
            return;
        }

        var insertIndex = actions.IndexOf(Id.Cautious);

        actions.Insert(insertIndex >= 0 ? insertIndex + 1 : actions.Count, explorationToggleId);
    }

    private static bool CanUseExplorationFreeJump(GameLocationCharacter character)
    {
        return CanShowExplorationFreeJumpToggle(character, out _) &&
               IsExplorationFreeJumpEnabled(character);
    }

    private static bool CanShowExplorationFreeJumpToggle(GameLocationCharacter character, out string reason)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            reason = "setting-off";
            return false;
        }

        if (character?.RulesetCharacter == null)
        {
            reason = "no-character";
            return false;
        }

        if (!CanFreeJumpCharacterAct(character, out reason))
        {
            return false;
        }

        if (IsBattleInProgress())
        {
            reason = "battle";
            return false;
        }

        reason = "available";
        return true;
    }

    private static bool IsExplorationFreeJumpEnabled(GameLocationCharacter character)
    {
        return character?.RulesetCharacter?.IsToggleEnabled((Id)ExtraActionId.ExplorationFreeJumpToggle) == true;
    }

    private static bool CanAddPathfindingNeighbours(
        PathfindingGridNode currentNode,
        List<PathfindingNeighbour> neighbours,
        List<int> moveModes)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            return false;
        }

        if (currentNode == null)
        {
            return false;
        }

        if (neighbours == null)
        {
            return false;
        }

        if (moveModes == null)
        {
            return false;
        }

        if (ActiveScopes.Count == 0)
        {
            return false;
        }

        if (!moveModes.Contains((int)RuleDefinitions.MoveMode.Walk))
        {
            return false;
        }

        if ((currentNode.NodeState & PathfindingGridNode.State.Walkable) == 0)
        {
            return false;
        }

        return true;
    }

    private static void EnumerateCandidatePositions(int3 start, FreeJumpProfile profile, Func<int3, bool> handle)
    {
        for (var vertical = 1; vertical <= profile.MaxVerticalCells; vertical++)
        {
            if (!handle(new int3(start.x, start.y + vertical, start.z)))
            {
                return;
            }
        }

        for (var vertical = 1; vertical <= MaxDownwardCells; vertical++)
        {
            if (!handle(new int3(start.x, start.y - vertical, start.z)))
            {
                return;
            }
        }

        for (var distance = 1; distance <= profile.MaxHorizontalCells; distance++)
        {
            for (var x = -distance; x <= distance; x++)
            {
                for (var z = -distance; z <= distance; z++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != distance)
                    {
                        continue;
                    }

                    for (var y = 0; y <= profile.MaxVerticalCells; y++)
                    {
                        if (!handle(new int3(start.x + x, start.y + y, start.z + z)))
                        {
                            return;
                        }
                    }

                    for (var y = -1; y >= -MaxDownwardCells; y--)
                    {
                        if (!handle(new int3(start.x + x, start.y + y, start.z + z)))
                        {
                            return;
                        }
                    }
                }
            }
        }
    }

    private static bool CanUseBattleFreeJump(
        GameLocationCharacter character,
        bool requireBonusAction,
        out string reason)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            reason = "setting-off";
            return false;
        }

        if (character?.RulesetCharacter == null)
        {
            reason = "no-character";
            return false;
        }

        if (!CanFreeJumpCharacterAct(character, out reason))
        {
            return false;
        }

        if (!IsBattleInProgress())
        {
            reason = "not-battle";
            return false;
        }

        if (character.RemainingTacticalMoves <= 0)
        {
            reason = "no-move";
            return false;
        }

        var moveStatus = character.GetActionStatus(Id.TacticalMove, ActionScope.Battle);

        if (moveStatus != ActionStatus.Available)
        {
            reason = $"move-{moveStatus}";
            return false;
        }

        if (requireBonusAction)
        {
            var bonusStatus = character.GetActionTypeStatus(ActionType.Bonus);

            if (bonusStatus != ActionStatus.Available)
            {
                reason = $"bonus-{bonusStatus}";
                return false;
            }
        }

        reason = "available";
        return true;
    }

    private static bool CanFreeJumpCharacterAct(GameLocationCharacter character, out string reason)
    {
        if (TryGetUnableToActReason(character, out reason))
        {
            return false;
        }

        if (IsFreeJumpSuppressedByFlight(character, out reason))
        {
            return false;
        }

        reason = "available";
        return true;
    }

    private static bool IsFreeJumpSuppressedByFlight(GameLocationCharacter character, out string reason)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            reason = "no-character";
            return false;
        }

        if (rulesetCharacter.HasConditionOfType(FlightSuspendedConditionName))
        {
            reason = "flight-suspended";
            return false;
        }

        if (!rulesetCharacter.IsTouchingGround() ||
            rulesetCharacter.MoveModes.ContainsKey((int)RuleDefinitions.MoveMode.Fly))
        {
            reason = "flying";
            return true;
        }

        reason = "grounded";
        return false;
    }

    private static bool TryGetUnableToActReason(GameLocationCharacter character, out string reason)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            reason = "no-character";
            return true;
        }

        if (rulesetCharacter.IsDeadOrDyingOrUnconscious)
        {
            reason = "dead-or-unconscious";
            return true;
        }

        if (rulesetCharacter.IsIncapacitated)
        {
            reason = "incapacitated";
            return true;
        }

        reason = "available";
        return false;
    }

    private static IDisposable BeginScope(
        GameLocationCharacter character,
        ScopeKind kind,
        int3 targetPosition = default,
        bool hasTargetPosition = false)
    {
        if (IsFreeJumpSuppressedByFlight(character, out _))
        {
            return null;
        }

        if (!TryComputeProfile(character, out var profile))
        {
            return null;
        }

        ActiveScopes.TryGetValue(character.Guid, out var previous);

        var scope = new ScopeData(kind, character, character.LocationPosition, profile, targetPosition, hasTargetPosition);

        if (kind == ScopeKind.AiMove && previous?.Kind == ScopeKind.AiTurn)
        {
            scope.FailedTargets = previous.FailedTargets;
        }

        ActiveScopes[character.Guid] = scope;

        return new Scope(character.Guid, scope, previous);
    }

    private static bool TryComputeProfile(GameLocationCharacter character, out FreeJumpProfile profile)
    {
        profile = default;

        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            return false;
        }

        var strength = rulesetCharacter.TryGetAttributeValue(AttributeDefinitions.Strength);
        var strengthModifier = AttributeDefinitions.ComputeAbilityScoreModifier(strength);
        var athleticsBonus = rulesetCharacter.ComputeBaseAbilityCheckBonus(
            AttributeDefinitions.Strength, null, SkillDefinitions.Athletics);
        var athleticsTraining = Clamp(athleticsBonus - strengthModifier, 0, 12);
        var hasJumpSpell = rulesetCharacter.HasConditionOfType(ConditionJump);
        var vanillaMaxJump = Clamp(Math.Max(1, rulesetCharacter.maxJumpRange), 1, AbsoluteMaxHorizontalCells);
        var computedHorizontal = Math.Min(strength / FeetPerCell + athleticsTraining / 4,
            PreferredMaxHorizontalCells);
        var maxHorizontal = Clamp(Math.Max(vanillaMaxJump, computedHorizontal), 1, AbsoluteMaxHorizontalCells);
        var verticalFeet = 3 + Math.Max(0, strengthModifier) + athleticsTraining / 2;
        var maxVertical = Clamp(
            CeilDiv(verticalFeet, FeetPerCell) + (hasJumpSpell ? JumpSpellVerticalBonusCells : 0),
            1,
            MaxVerticalCells);

        profile = new FreeJumpProfile(maxHorizontal, maxVertical, athleticsBonus, hasJumpSpell);

        return true;
    }

    private static bool CanUseAiFreeJump(GameLocationCharacter character)
    {
        return CombatAiContext.CanUseFreeJumpForAi(character) &&
               CanUseAction(character) &&
               character.UsedTacticalMoves == 0 &&
               character.CanDecideToMoveByItself;
    }

    private static bool IsBattleInProgress()
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        return battleService is { IsBattleInProgress: true };
    }

    private static int ComputeFreeJumpMovementCost(int3 delta)
    {
        return Clamp(
            Math.Max(1, Math.Max(Math.Abs(delta.y), Math.Max(Math.Abs(delta.x), Math.Abs(delta.z)))),
            1,
            byte.MaxValue);
    }

    private static GameLocationCharacterDefinitions.PathStep BuildFreeJumpPathStep(ScopeData scope, int3 destination)
    {
        return BuildFreeJumpPathStep(scope.StartPosition, destination);
    }

    private static GameLocationCharacterDefinitions.PathStep BuildFreeJumpPathStep(int3 start, int3 destination)
    {
        return new GameLocationCharacterDefinitions.PathStep
        {
            position = destination,
            moveMode = RuleDefinitions.MoveMode.Walk,
            moveCost = ComputeFreeJumpMovementCost(destination - start),
            flags = PathfindingNode.InformationFlag.Jump
        };
    }

    private static int CeilDiv(int value, int divisor)
    {
        return (value + divisor - 1) / divisor;
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

}
