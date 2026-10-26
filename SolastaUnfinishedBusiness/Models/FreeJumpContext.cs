using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
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

    private static readonly Dictionary<ulong, ScopeData> ActiveScopes = [];
    private static readonly Dictionary<ulong, int> AiPathfindingSuppressionDepths = [];
    private static readonly Dictionary<ulong, int3> SuppressedAiMoveTargets = [];
    private static readonly Dictionary<ulong, int3> ForcedAiFreeJumpTargets = [];
    private static readonly Dictionary<ulong, PendingAiFreeJumpCompletion> PendingAiFreeJumpCompletions = [];
    private static readonly HashSet<CursorLocationBattleFriendlyTurn> CursorDestinationComputations = [];
    private static readonly Dictionary<CursorLocationExplorationCharacter, ExplorationJumpPreview>
        ExplorationPreviews = [];
    private static object ScopeOwnershipToken = new();
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

    private enum AiFreeJumpBonusSpendSkipReason
    {
        None,
        AlreadySpent,
        LandingFailed,
        InvalidPosition,
        NoMove,
        StartMismatch,
        NotAtFinish,
        BonusUnavailable
    }

    internal readonly struct FreeJumpCheckPreview(
        FreeJumpPreviewOutcome outcome,
        int moveCost,
        int checkDc,
        int successChance,
        RuleDefinitions.AdvantageType affinity)
    {
        internal FreeJumpPreviewOutcome Outcome { get; } = outcome;
        internal int MoveCost { get; } = moveCost;
        internal bool RequiresAthleticsCheck { get; } = outcome == FreeJumpPreviewOutcome.AthleticsCheck;
        internal bool IsAutomaticFailure { get; } = outcome == FreeJumpPreviewOutcome.AutomaticFailure;
        internal int CheckDc { get; } = checkDc;
        internal int SuccessChance { get; } = successChance;
        internal RuleDefinitions.AdvantageType Affinity { get; } = affinity;
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

    private sealed class ExplorationJumpPreview : IDisposable
    {
        private readonly CursorLocationExplorationCharacter _cursor;
        private readonly IGameLocationPositioningService _positioning;

        internal ExplorationJumpPreview(CursorLocationExplorationCharacter cursor, GameLocationCharacter character,
            IGameLocationPositioningService positioning)
        {
            _cursor = cursor;
            Character = character;
            _positioning = positioning;
            Chain = new CharacterActionChainParams(character);
            _positioning.TopologyChanged += TopologyChanged;
        }

        internal GameLocationCharacter Character { get; }
        internal CharacterActionChainParams Chain { get; }
        internal List<int3> Destinations { get; } = [];
        internal int3 StartPosition { get; set; }
        internal int PlaneHeight { get; set; }
        internal FreeJumpProfile Profile { get; set; }
        internal bool GridDirty { get; set; } = true;
        internal bool HasHover { get; set; }
        internal int3 PreviousHover { get; set; }
        internal bool PreviousValidity { get; set; }
        internal bool CaptionVisible { get; set; }

        private void TopologyChanged(List<GameSector> sectors, List<TopologyForm.Type> forms)
        {
            GridDirty = true;
            HasHover = false;

            if (_cursor && _cursor.Active)
            {
                _cursor.RefreshHover();
            }
        }

        public void Dispose()
        {
            _positioning.TopologyChanged -= TopologyChanged;
            Chain.Clear();
        }
    }

    private readonly struct PendingAiFreeJumpCompletion(int3 startPosition, int3 targetPosition)
    {
        internal int3 StartPosition { get; } = startPosition;
        internal int3 TargetPosition { get; } = targetPosition;
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
        internal ScopeData PreviousScope { get; set; }
        internal bool IsDisposed { get; set; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly ulong _guid;
        private readonly ScopeData _current;
        private bool _disposed;

        internal Scope(ulong guid, ScopeData current, ScopeData previous)
        {
            _guid = guid;
            _current = current;
            _current.PreviousScope = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current.IsDisposed = true;

            if (!ActiveScopes.TryGetValue(_guid, out var active) || !ReferenceEquals(active, _current))
            {
                return;
            }

            // Cancellation can dispose an outer iterator while a nested move is still active.
            // Restore only live predecessors, and never replace a newer scope's state.
            var previous = _current.PreviousScope;
            while (previous?.IsDisposed == true)
            {
                previous = previous.PreviousScope;
            }

            if (previous == null)
            {
                ActiveScopes.Remove(_guid);
            }
            else
            {
                ActiveScopes[_guid] = previous;
            }

            if (_current.Kind is ScopeKind.AiMove or ScopeKind.AiTurn)
            {
                ForcedAiFreeJumpTargets.Remove(_guid);
            }
        }
    }

    private sealed class SuppressAiPathfindingScope : IDisposable
    {
        private readonly ulong _guid;
        private readonly object _ownershipToken = ScopeOwnershipToken;
        private bool _disposed;

        internal SuppressAiPathfindingScope(ulong guid)
        {
            _guid = guid;
            AiPathfindingSuppressionDepths.TryGetValue(_guid, out var depth);
            AiPathfindingSuppressionDepths[_guid] = depth + 1;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!ReferenceEquals(_ownershipToken, ScopeOwnershipToken) ||
                !AiPathfindingSuppressionDepths.TryGetValue(_guid, out var depth))
            {
                return;
            }

            if (depth <= 1)
            {
                AiPathfindingSuppressionDepths.Remove(_guid);
            }
            else
            {
                AiPathfindingSuppressionDepths[_guid] = depth - 1;
            }
        }
    }

    private sealed class CursorDestinationComputationScope : IDisposable
    {
        private readonly CursorLocationBattleFriendlyTurn _cursor;
        private readonly IDisposable _pathfindingScope;
        private readonly object _ownershipToken;
        private bool _disposed;

        internal CursorDestinationComputationScope(
            CursorLocationBattleFriendlyTurn cursor,
            IDisposable pathfindingScope,
            object ownershipToken)
        {
            _cursor = cursor;
            _pathfindingScope = pathfindingScope;
            _ownershipToken = ownershipToken;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _pathfindingScope?.Dispose();
            }
            finally
            {
                if (ReferenceEquals(_ownershipToken, ScopeOwnershipToken))
                {
                    CursorDestinationComputations.Remove(_cursor);
                }
            }
        }
    }

    internal static bool IsBonusActionFreeJump(Id actionId)
    {
        return actionId == (Id)ExtraActionId.BonusActionFreeJump;
    }

    internal static bool CanUseAction(GameLocationCharacter character)
    {
        return CanUseBattleFreeJump(character, true);
    }

    internal static bool CanExecuteBonusActionMove(GameLocationCharacter character)
    {
        return CanUseBattleFreeJump(character, false);
    }

    internal static ActionStatus GetExplorationToggleActionStatus(GameLocationCharacter character, ActionScope scope)
    {
        if (!Main.Settings.EnableBonusActionFreeJump || scope != ActionScope.Exploration)
        {
            return ActionStatus.Unavailable;
        }

        return CanShowExplorationFreeJumpToggle(character)
            ? ActionStatus.Available
            : ActionStatus.Unavailable;
    }

    internal static bool HasAiMoveState(GameLocationCharacter character)
    {
        return character != null &&
               (SuppressedAiMoveTargets.ContainsKey(character.Guid) ||
                ForcedAiFreeJumpTargets.ContainsKey(character.Guid));
    }

    internal static bool ClearAiMoveTargetState(GameLocationCharacter character)
    {
        if (character == null)
        {
            return false;
        }

        return SuppressedAiMoveTargets.Remove(character.Guid) |
               ForcedAiFreeJumpTargets.Remove(character.Guid) |
               PendingAiFreeJumpCompletions.Remove(character.Guid);
    }

    internal static void ClearPendingAiFreeJumpCompletions()
    {
        PendingAiFreeJumpCompletions.Clear();
    }

    internal static void Unload()
    {
        // Suspended iterators may dispose after a reload has acquired new guards.
        ScopeOwnershipToken = new object();
        ActiveScopes.Clear();
        AiPathfindingSuppressionDepths.Clear();
        SuppressedAiMoveTargets.Clear();
        ForcedAiFreeJumpTargets.Clear();
        PendingAiFreeJumpCompletions.Clear();
        CursorDestinationComputations.Clear();
        foreach (var cursor in new List<CursorLocationExplorationCharacter>(ExplorationPreviews.Keys))
        {
            ClearExplorationPreview(cursor);
        }
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

        if (!CanUseBattleFreeJump(character, true))
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

        if (!CanUseBattleFreeJump(character, true))
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
        TryComputeValidDestinations(cursor, refreshVisibleDestinationsGrid: true);

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

        if (!CanPrepareBattleFreeJumpMove(cursor, out var character, out var destination))
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

        if (CanPrepareBattleFreeJumpMove(cursor, out _, out _))
        {
            return false;
        }

        CancelBattleSelection(cursor);

        return true;
    }

    private static bool CanPrepareBattleFreeJumpMove(
        CursorLocationBattleFriendlyTurn cursor,
        out GameLocationCharacter character,
        out int3 destination)
    {
        character = cursor?.actingCharacter;
        destination = cursor == null ? default : cursor.HoveredLocation;

        if (character == null)
        {
            return false;
        }

        if (!CanUseBattleFreeJump(character, true))
        {
            return false;
        }

        if (!CanReach(character, character.LocationPosition, destination))
        {
            return false;
        }

        if (!CanAffordFreeJumpMove(character, character.LocationPosition, destination))
        {
            return false;
        }

        if (ServiceRepository.GetService<IGameLocationPositioningService>() == null ||
            ServiceRepository.GetService<IGameLocationCharacterService>() == null)
        {
            return false;
        }

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
                out var pathStep))
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

    internal static bool TryRefreshExplorationMovementHelper(CursorLocationExplorationCharacter cursor)
    {
        if (!TryGetExplorationJumpCharacter(cursor, out var character))
        {
            ClearExplorationPreview(cursor);
            return false;
        }

        if (!HasExplorationWorldHover(cursor) && !ShouldSuspendExplorationFeedback(cursor))
        {
            ClearExplorationPreview(cursor);
            return false;
        }

        var preview = GetExplorationPreview(cursor, character);

        if (preview == null)
        {
            return false;
        }

        if (ShouldSuspendExplorationFeedback(cursor))
        {
            SuspendExplorationFeedback(cursor, preview);
            return true;
        }

        RefreshExplorationDestinations(cursor, preview);
        cursor.movementHelper.HoveredPosition = cursor.HoveredPosition;
        cursor.movementHelper.RefreshHover(true);
        cursor.movementHelperDisplayed = true;
        return true;
    }

    internal static bool OwnsExplorationChainHelper(CursorLocationExplorationCharacter cursor)
    {
        // The exploration cursor normally clears this helper for a grounded character.
        // Our hover postfix supplies its one-step jump path after native picking completes.
        return TryGetExplorationJumpCharacter(cursor, out _) &&
               (HasExplorationWorldHover(cursor) || ShouldSuspendExplorationFeedback(cursor));
    }

    internal static bool CanUseExplorationPlanarSelection(CursorLocationExplorationCharacter cursor)
    {
        return TryGetExplorationJumpCharacter(cursor, out _);
    }

    internal static void RefreshExplorationSelection(CursorLocationExplorationCharacter cursor)
    {
        if (!TryGetExplorationJumpCharacter(cursor, out var character))
        {
            ClearExplorationPreview(cursor);
            return;
        }

        if (!HasExplorationWorldHover(cursor) && !ShouldSuspendExplorationFeedback(cursor))
        {
            ClearExplorationPreview(cursor);
            return;
        }

        var preview = GetExplorationPreview(cursor, character);

        if (preview == null)
        {
            return;
        }

        if (ShouldSuspendExplorationFeedback(cursor))
        {
            SuspendExplorationFeedback(cursor, preview);
            return;
        }

        RefreshExplorationDestinations(cursor, preview);
        var destination = cursor.HoveredLocation;
        var valid = TryValidateCandidate(character, preview.StartPosition, destination, preview.Profile, false, out _);
        ApplyExplorationDestination(cursor, character, destination, valid);

        // Occupants may move without changing the floor topology. Keep the hovered cell
        // authoritative even when the rest of this plane's landing grid is reusable.
        var listed = preview.Destinations.Contains(destination);
        if (valid != listed)
        {
            if (valid)
            {
                preview.Destinations.Add(destination);
            }
            else
            {
                preview.Destinations.Remove(destination);
            }

            cursor.movementHelper.RefreshValidDestinations(preview.Destinations);
        }

        if (!preview.HasHover || preview.PreviousHover != destination || preview.PreviousValidity != valid)
        {
            cursor.chainHelper.Clear(true);
            if (valid && TrySeedFreeJumpEvaluation(preview.Chain, preview.StartPosition,
                    BuildFreeJumpPathStep(preview.StartPosition, destination), true))
            {
                cursor.chainHelper.RefreshHoverBattle(preview.Chain, character, destination, null, true, false);
            }
            else
            {
                cursor.chainHelper.RefreshHoverExploration(character, destination, false);
            }

            cursor.chainHelperDisplayed = true;
            preview.PreviousHover = destination;
            preview.PreviousValidity = valid;
            preview.HasHover = true;
        }

        var caption = Gui.Localize("Action/&FreeJumpChooseDestinationDescription");
        if (valid && TryComputeFreeJumpPreview(character, preview.StartPosition, destination, true,
                preview.Profile, out var check))
        {
            caption = FormatFreeJumpPreview(check);
        }

        CursorLocation.CaptionLineChanged?.Invoke("Action/&BonusActionFreeJumpTitle", caption,
            string.Empty, string.Empty, string.Empty, string.Empty, false, true);
        preview.CaptionVisible = true;
    }

    internal static bool TryPrepareExplorationClick(CursorLocationExplorationCharacter cursor, out bool valid)
    {
        valid = false;
        if (!TryGetExplorationJumpCharacter(cursor, out _))
        {
            return false;
        }

        if (ShouldSuspendExplorationFeedback(cursor))
        {
            return true;
        }

        if (!HasExplorationWorldHover(cursor))
        {
            return false;
        }

        // Revalidate against the current position and occupancy rather than accepting
        // native formation placement's nearby replacement for the chosen landing cell.
        RefreshExplorationSelection(cursor);
        valid = cursor.movePositions.Count == 1;
        return true;
    }

    internal static void CancelExplorationSelection(CharacterActionPanel panel)
    {
        if (panel?.ActionScope != ActionScope.Exploration ||
            ServiceRepository.GetService<ICursorService>()?.CurrentCursor is not CursorLocationExplorationCharacter
                { Active: true } cursor)
        {
            return;
        }

        var character = panel.GuiCharacter?.GameLocationCharacter;
        if (character == null || cursor.SelectionService?.SelectedCharacters is not { Count: > 0 } selected ||
            selected[0] != character || !IsExplorationFreeJumpEnabled(character))
        {
            return;
        }

        DisableExplorationFreeJump(character);
        ClearExplorationPreview(cursor);
    }

    internal static void ClearExplorationPreview(CursorLocationExplorationCharacter cursor)
    {
        if (ReferenceEquals(cursor, null) || !ExplorationPreviews.TryGetValue(cursor, out var preview))
        {
            return;
        }

        ExplorationPreviews.Remove(cursor);
        preview.Dispose();

        if (cursor)
        {
            cursor.movementHelper?.Clear();
            cursor.chainHelper?.Deactivate();
            cursor.chainHelper?.Clear(true);
            cursor.groundEchoHelper?.gameObject.SetActive(false);
            cursor.movementHelperDisplayed = false;
            cursor.chainHelperDisplayed = false;
        }

        if (preview.CaptionVisible)
        {
            CursorLocation.CaptionLineDismissed?.Invoke();
        }
    }

    private static bool TryGetExplorationJumpCharacter(CursorLocationExplorationCharacter cursor,
        out GameLocationCharacter character)
    {
        character = null;
        if (cursor is not { Active: true } ||
            ServiceRepository.GetService<ICursorService>()?.CurrentCursor != cursor ||
            cursor.SelectionService?.SelectedCharacters is not { Count: > 0 } selected)
        {
            return false;
        }

        character = selected[0];
        return CanUseExplorationFreeJump(character);
    }

    private static bool HasExplorationWorldHover(CursorLocationExplorationCharacter cursor)
    {
        return !cursor.HoveredGUI && !cursor.OutOfScreen && cursor.HoveredWorldGadget == null &&
               cursor.SelectionService.HoveredCharacters.Count == 0;
    }

    private static bool ShouldSuspendExplorationFeedback(CursorLocationExplorationCharacter cursor)
    {
        return cursor.HoveredGUI || cursor.OutOfScreen;
    }

    private static void SuspendExplorationFeedback(CursorLocationExplorationCharacter cursor,
        ExplorationJumpPreview preview)
    {
        // Keep the selection and its caption while the pointer reaches its controls.
        // World feedback is rebuilt when the pointer returns to a landing cell.
        if (cursor.movementHelperDisplayed)
        {
            cursor.movementHelper.Clear();
            cursor.movementHelperDisplayed = false;
        }

        if (cursor.chainHelperDisplayed)
        {
            cursor.chainHelper.Clear(true);
            cursor.chainHelperDisplayed = false;
        }

        cursor.groundEchoHelper.gameObject.SetActive(false);
        cursor.movePositions.Clear();
        preview.GridDirty = true;
        preview.HasHover = false;
    }

    private static ExplorationJumpPreview GetExplorationPreview(CursorLocationExplorationCharacter cursor,
        GameLocationCharacter character)
    {
        if (!ExplorationPreviews.TryGetValue(cursor, out var preview) || preview.Character != character)
        {
            ClearExplorationPreview(cursor);
            var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
            if (positioning == null)
            {
                return null;
            }

            preview = new ExplorationJumpPreview(cursor, character, positioning);
            ExplorationPreviews[cursor] = preview;
            cursor.chainHelper.Activate(character.RulesetCharacter);
        }

        return preview;
    }

    private static void RefreshExplorationDestinations(CursorLocationExplorationCharacter cursor,
        ExplorationJumpPreview preview)
    {
        if (!TryComputeProfile(preview.Character, out var profile))
        {
            return;
        }

        var start = preview.Character.LocationPosition;
        var height = cursor.HoveredLocation.y;
        var changed = preview.StartPosition != start || preview.PlaneHeight != height ||
                      preview.Profile.MaxHorizontalCells != profile.MaxHorizontalCells ||
                      preview.Profile.MaxVerticalCells != profile.MaxVerticalCells;
        preview.Profile = profile;
        if (!changed && !preview.GridDirty)
        {
            return;
        }

        preview.StartPosition = start;
        preview.PlaneHeight = height;
        preview.HasHover = false;
        preview.GridDirty = false;
        preview.Destinations.Clear();
        for (var x = -profile.MaxHorizontalCells; x <= profile.MaxHorizontalCells; x++)
        {
            for (var z = -profile.MaxHorizontalCells; z <= profile.MaxHorizontalCells; z++)
            {
                var destination = new int3(start.x + x, height, start.z + z);
                if (TryValidateCandidate(preview.Character, start, destination, profile, false, out _))
                {
                    preview.Destinations.Add(destination);
                }
            }
        }

        cursor.movementHelper.RefreshValidDestinations(preview.Destinations);
    }

    private static void ApplyExplorationDestination(CursorLocationExplorationCharacter cursor,
        GameLocationCharacter character, int3 destination, bool valid)
    {
        cursor.filteredCharacters.Clear();
        cursor.filteredCharacters.Add(character);
        cursor.movePositions.Clear();
        cursor.targetGadget = null;
        cursor.targetCharacter = null;
        cursor.targetAwaken = false;
        cursor.targetLoot = false;
        if (valid)
        {
            cursor.movePositions.Add(destination);
            cursor.groundEchoHelper.ConfigureDestination(destination, cursor.targetOrientation, character, 0);
        }

        cursor.groundEchoHelper.gameObject.SetActive(valid);
        cursor.SetMousePointerBitmap(valid ? CursorDefinitions.CursorType.Destination : CursorDefinitions.CursorType.Invalid);
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

        DisableExplorationFreeJump(character);

        return scope;
    }

    private static IDisposable BeginAiMovePathfinding(CharacterActionMove action)
    {
        var character = action?.ActingCharacter;

        if (character == null)
        {
            return null;
        }

        var destination = action.DestinationPosition;
        var forcedTarget =
            ForcedAiFreeJumpTargets.TryGetValue(character.Guid, out var forcedDestination) &&
            forcedDestination == destination;

        if (!ActiveScopes.TryGetValue(character.Guid, out var aiScope) ||
            aiScope.Kind != ScopeKind.AiTurn)
        {
            if (!forcedTarget)
            {
                return null;
            }

            if (!TryComputeProfile(character, out var profile))
            {
                ForcedAiFreeJumpTargets.Remove(character.Guid);
                return null;
            }

            aiScope = new ScopeData(ScopeKind.AiTurn, character, character.LocationPosition, profile);
        }

        if (SuppressedAiMoveTargets.TryGetValue(character.Guid, out var suppressedDestination) &&
            suppressedDestination == destination)
        {
            SuppressedAiMoveTargets.Remove(character.Guid);
            return null;
        }

        if (forcedTarget)
        {
            aiScope.Candidates.Add(destination);
        }

        if (!aiScope.Candidates.Contains(destination))
        {
            return null;
        }

        if (aiScope.FailedTargets.Contains(destination))
        {
            return null;
        }

        if (CombatAiContext.IsRejectedAiMoveTarget(character, aiScope.StartPosition, destination))
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
                CombatAiFreeJumpEvaluationSource.AiMovePathfinding,
                out _))
        {
            if (forcedTarget)
            {
                ForcedAiFreeJumpTargets.Remove(character.Guid);
            }

            return null;
        }

        if (forcedTarget)
        {
            ForcedAiFreeJumpTargets.Remove(character.Guid);
        }

        RegisterPendingAiFreeJumpCompletion(character, character.LocationPosition, destination);

        return BeginScope(character, ScopeKind.AiMove, destination, true);
    }

    internal static IDisposable BeginBonusActionPathfinding(GameLocationCharacter character, Id constrainedMovementMode)
    {
        if (!IsBonusActionFreeJump(constrainedMovementMode))
        {
            return null;
        }

        if (!CanUseBattleFreeJump(character, true))
        {
            return null;
        }

        return BeginScope(character, ScopeKind.BonusActionCursor);
    }

    internal static IDisposable BeginCursorDestinationComputation(CursorLocationBattleFriendlyTurn cursor)
    {
        if (cursor == null)
        {
            return null;
        }

        var ownershipToken = ScopeOwnershipToken;

        if (!CursorDestinationComputations.Add(cursor))
        {
            cursor.dirty = true;
            return null;
        }

        try
        {
            return new CursorDestinationComputationScope(
                cursor,
                BeginBonusActionPathfinding(cursor.actingCharacter, cursor.constrainedMovementMode),
                ownershipToken);
        }
        catch
        {
            if (ReferenceEquals(ownershipToken, ScopeOwnershipToken))
            {
                CursorDestinationComputations.Remove(cursor);
            }

            throw;
        }
    }

    internal static bool TryComputeValidDestinations(
        CursorLocationBattleFriendlyTurn cursor,
        bool refreshVisibleDestinationsGrid = false)
    {
        if (cursor == null)
        {
            return false;
        }

        if (CursorDestinationComputations.Contains(cursor))
        {
            cursor.dirty = true;
            return false;
        }

        cursor.ComputeValidDestinations();

        if (refreshVisibleDestinationsGrid)
        {
            cursor.RefreshVisibleDestinationsGrid();
        }

        return true;
    }

    internal static IDisposable BeginAiTurn(GameLocationCharacter character)
    {
        return CanUseAiFreeJump(character) ? BeginScope(character, ScopeKind.AiTurn) : null;
    }

    internal static IDisposable SuppressAiPathfindingFreeJump(GameLocationCharacter character)
    {
        return character?.RulesetCharacter == null
            ? null
            : new SuppressAiPathfindingScope(character.Guid);
    }

    internal static bool IsForcedAiFreeJumpTarget(GameLocationCharacter character, int3 destination)
    {
        return character?.RulesetCharacter != null &&
               ForcedAiFreeJumpTargets.TryGetValue(character.Guid, out var forcedDestination) &&
               forcedDestination == destination;
    }

    internal static bool TryPrepareAiFreeJumpMove(GameLocationCharacter character, int3 destination)
    {
        if (!CanUseAiFreeJump(character))
        {
            return false;
        }

        var hasAiTurnScope =
            ActiveScopes.TryGetValue(character.Guid, out var scope) &&
            scope.Kind == ScopeKind.AiTurn;

        if (ActiveScopes.TryGetValue(character.Guid, out var activeScope) &&
            activeScope.Kind != ScopeKind.AiTurn)
        {
            return false;
        }

        if (!CanAffordFreeJumpMove(character, character.LocationPosition, destination))
        {
            return false;
        }

        if (!TryGetAiCandidateInfo(character, character.LocationPosition, destination, out _))
        {
            return false;
        }

        if (hasAiTurnScope)
        {
            scope.Candidates.Add(destination);
            scope.FailedTargets.Remove(destination);
        }

        RegisterPendingAiFreeJumpCompletion(character, character.LocationPosition, destination);
        ForcedAiFreeJumpTargets[character.Guid] = destination;
        return true;
    }

    internal static void SuppressAiFreeJumpForNextMove(GameLocationCharacter character, int3 destination)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        SuppressedAiMoveTargets[character.Guid] = destination;
    }

    internal static bool HasUsefulAiFreeJumpDestination(GameLocationCharacter character)
    {
        return TryEnumerateUsefulAiFreeJumpDestinations(character, (_, _) => false);
    }

    internal static bool TryEnumerateUsefulAiFreeJumpDestinations(
        GameLocationCharacter character,
        Func<int3, float, bool> handle)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            return false;
        }

        if (handle == null ||
            !CanUseAiFreeJump(character) ||
            !TryComputeProfile(character, out var profile))
        {
            return false;
        }

        var start = character.LocationPosition;
        var hasAcceptedCandidate = false;

        EnumerateCandidatePositions(start, profile, destination =>
        {
            if (!TryGetEligibleAiFreeJumpCandidate(character, start, destination, profile, null, out var candidateInfo) ||
                !CombatAiContext.TryEvaluateFreeJumpDestination(
                    character,
                    start,
                    destination,
                    candidateInfo.Preview,
                    candidateInfo.BypassesObstacle,
                    CombatAiFreeJumpEvaluationSource.JumpCandidateEnumeration,
                    out var score))
            {
                return true;
            }

            hasAcceptedCandidate = true;

            return handle(destination, score);
        });

        return hasAcceptedCandidate;
    }

    internal static bool TryEnumerateAiFreeJumpDestinations(
        GameLocationCharacter character,
        int minimumSuccessChance,
        Func<int3, FreeJumpCandidateInfo, float, bool> handle)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            return false;
        }

        if (handle == null ||
            !CanUseAiFreeJump(character) ||
            !TryComputeProfile(character, out var profile))
        {
            return false;
        }

        var start = character.LocationPosition;
        var hasAcceptedCandidate = false;

        EnumerateCandidatePositions(start, profile, destination =>
        {
            if (!TryGetEligibleAiFreeJumpCandidate(
                    character, start, destination, profile, minimumSuccessChance, out var candidateInfo))
            {
                return true;
            }

            hasAcceptedCandidate = true;

            var score =
                1.0f +
                GetEffectiveAiFreeJumpSuccessChance(candidateInfo.Preview) / 1000f -
                candidateInfo.Preview.MoveCost * 0.01f +
                (candidateInfo.BypassesObstacle ? 0.05f : 0f);

            return handle(destination, candidateInfo, score);
        });

        return hasAcceptedCandidate;
    }

    internal static bool TryEnumerateTargetContactAiFreeJumpDestinations(
        GameLocationCharacter character,
        int3 targetPosition,
        int contactBand,
        int minimumSuccessChance,
        Func<int3, FreeJumpCandidateInfo, float, bool> handle)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            return false;
        }

        if (handle == null ||
            !CanUseAiFreeJump(character) ||
            !TryComputeProfile(character, out var profile))
        {
            return false;
        }

        var start = character.LocationPosition;
        var maxHorizontalTargetDistance = profile.MaxHorizontalCells + Math.Max(1, contactBand);
        var targetHorizontalDistance = Math.Max(Math.Abs(start.x - targetPosition.x), Math.Abs(start.z - targetPosition.z));

        if (targetHorizontalDistance > maxHorizontalTargetDistance)
        {
            return false;
        }

        var seen = new HashSet<int3>();
        var verticalOffsets = BuildTargetContactVerticalOffsets(profile);
        var hasAcceptedCandidate = false;
        var contactRadius = Math.Max(1, contactBand);
        var stopped = false;

        for (var ring = 1; ring <= contactRadius && !stopped; ring++)
        {
            for (var x = -ring; x <= ring && !stopped; x++)
            {
                for (var z = -ring; z <= ring && !stopped; z++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring)
                    {
                        continue;
                    }

                    foreach (var yOffset in verticalOffsets)
                    {
                        var destination = new int3(targetPosition.x + x, targetPosition.y + yOffset, targetPosition.z + z);

                        if (!seen.Add(destination))
                        {
                            continue;
                        }

                        if (!TryGetEligibleAiFreeJumpCandidate(
                                character, start, destination, profile, minimumSuccessChance, out var candidateInfo))
                        {
                            continue;
                        }

                        hasAcceptedCandidate = true;

                        var score =
                            2.0f +
                            GetEffectiveAiFreeJumpSuccessChance(candidateInfo.Preview) / 1000f -
                            candidateInfo.Preview.MoveCost * 0.01f -
                            ring * 0.02f +
                            (candidateInfo.BypassesObstacle ? 0.05f : 0f);

                        if (!handle(destination, candidateInfo, score))
                        {
                            stopped = true;
                            break;
                        }
                    }
                }
            }
        }

        return hasAcceptedCandidate;
    }

    private static bool TryGetEligibleAiFreeJumpCandidate(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        FreeJumpProfile profile,
        int? minimumSuccessChance,
        out FreeJumpCandidateInfo candidateInfo)
    {
        candidateInfo = default;

        return !CombatAiContext.IsFailedAiMoveTarget(character, start, destination) &&
               !CombatAiContext.IsRejectedAiMoveTarget(character, start, destination) &&
               CanAffordFreeJumpMove(character, start, destination) &&
               TryGetAiCandidateInfo(character, start, destination, profile, out candidateInfo) &&
               (!minimumSuccessChance.HasValue ||
                HasSufficientAiFreeJumpSuccessChance(candidateInfo.Preview, minimumSuccessChance.Value));
    }

    private static bool HasSufficientAiFreeJumpSuccessChance(
        FreeJumpCheckPreview preview,
        int minimumSuccessChance)
    {
        return !preview.IsAutomaticFailure &&
               (!preview.RequiresAthleticsCheck ||
                GetEffectiveAiFreeJumpSuccessChance(preview) >= minimumSuccessChance);
    }

    private static int GetEffectiveAiFreeJumpSuccessChance(FreeJumpCheckPreview preview)
    {
        // A check-free jump stores zero as its chance. Both eligibility and ranking
        // must treat it as certain, instead of preferring a jump that can fail.
        return preview.IsAutomaticFailure ? 0 : preview.RequiresAthleticsCheck ? preview.SuccessChance : 100;
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
            if (!CanUseBattleFreeJump(character, true))
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
        var disableExplorationToggle = false;
        var executeMove = false;
        IDisposable scope = null;

        try
        {
            disableExplorationToggle = ShouldDisableExplorationFreeJumpAfterMove(action);
            scope = BeginMovePathfinding(action);

            if (IsMarkedBonusActionMove(action) && scope == null)
            {
                yield break;
            }

            executeMove = true;

            while (values.MoveNext())
            {
                yield return values.Current;
            }
        }
        finally
        {
            try
            {
                (values as IDisposable)?.Dispose();
            }
            finally
            {
                try
                {
                    if (executeMove)
                    {
                        TrySpendBonusActionAfterMove(action);
                    }
                }
                finally
                {
                    try
                    {
                        if (executeMove && disableExplorationToggle)
                        {
                            DisableExplorationFreeJump(action?.ActingCharacter);
                        }
                    }
                    finally
                    {
                        scope?.Dispose();
                    }
                }
            }
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
            if (scope.StartPosition != currentPosition ||
                AiPathfindingSuppressionDepths.ContainsKey(scope.Character.Guid))
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
               !IsFreeJumpSuppressedByFlight(character) &&
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

        if (scope.Kind == ScopeKind.AiMove)
        {
            TrySpendAiBonusActionAfterCompletedMove(character, start, finish);
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

        SpendBonusAction(character, scope);
    }

    internal static void TrySpendAiBonusActionAfterCompletedMove(
        GameLocationCharacter character,
        int3 start,
        int3 finish)
    {
        if (!Main.Settings.EnableBonusActionFreeJump || character == null)
        {
            return;
        }

        var guid = character.Guid;
        var hasPending = PendingAiFreeJumpCompletions.TryGetValue(guid, out var pending);
        var hasAiMoveScope =
            ActiveScopes.TryGetValue(guid, out var scope) &&
            scope.Kind == ScopeKind.AiMove;

        if (!hasPending && !hasAiMoveScope)
        {
            return;
        }

        var expectedStart = hasPending ? pending.StartPosition : scope.StartPosition;
        var bonusStatus = character.GetActionTypeStatus(ActionType.Bonus);
        var skipReason = GetAiFreeJumpBonusSpendSkipReason(
            character,
            scope,
            hasAiMoveScope,
            start,
            finish,
            expectedStart,
            bonusStatus);

        if (skipReason == AiFreeJumpBonusSpendSkipReason.None &&
            (hasPending ? finish != pending.TargetPosition : !IsScopeDestination(scope, finish)))
        {
            // Native walking steps can complete before a prepared jump's landing.
            // A completed intermediate step must not spend the jump's bonus action.
            skipReason = AiFreeJumpBonusSpendSkipReason.NotAtFinish;
        }

        if (skipReason != AiFreeJumpBonusSpendSkipReason.None)
        {
            if (hasPending && skipReason != AiFreeJumpBonusSpendSkipReason.NotAtFinish)
            {
                PendingAiFreeJumpCompletions.Remove(guid);
            }

            return;
        }

        var spent = hasAiMoveScope
            ? SpendBonusAction(character, scope)
            : SpendAiBonusActionWithoutScope(character);

        if (!spent)
        {
            if (hasPending)
            {
                PendingAiFreeJumpCompletions.Remove(guid);
            }

            return;
        }

        PendingAiFreeJumpCompletions.Remove(guid);
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

        if (IsFreeJumpSuppressedByFlight(scope.Character))
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
            SpendBonusAction(scope.Character, scope);
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

        if (scope.Kind == ScopeKind.AiMove)
        {
            if (!atTarget || scope.BonusActionSpent || scope.JumpLandingFailed)
            {
                return;
            }

            TrySpendAiBonusActionAfterCompletedMove(
                character,
                scope.StartPosition,
                character.LocationPosition);

            return;
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

        SpendBonusAction(character, scope);
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
        rulesetCharacter.CharacterRefreshed?.Invoke(rulesetCharacter);
    }

    private static bool IsBonusActionSpendingScope(ScopeData scope)
    {
        return scope.Kind == ScopeKind.BonusActionMove;
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
        out GameLocationCharacterDefinitions.PathStep pathStep)
    {
        pathStep = default;

        if (!CanUseBattleFreeJump(character, true))
        {
            return false;
        }

        if (!TryComputeProfile(character, out var profile))
        {
            return false;
        }

        if (!TryValidateCandidate(character, start, destination, profile, false, out _))
        {
            return false;
        }

        if (!CanAffordFreeJumpMove(character, start, destination))
        {
            return false;
        }

        pathStep = BuildFreeJumpPathStep(start, destination);

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

    private static void RegisterPendingAiFreeJumpCompletion(
        GameLocationCharacter character,
        int3 startPosition,
        int3 targetPosition)
    {
        if (character?.RulesetCharacter == null ||
            startPosition == int3.invalid ||
            targetPosition == int3.invalid ||
            startPosition == targetPosition)
        {
            return;
        }

        PendingAiFreeJumpCompletions[character.Guid] =
            new PendingAiFreeJumpCompletion(startPosition, targetPosition);
    }

    private static AiFreeJumpBonusSpendSkipReason GetAiFreeJumpBonusSpendSkipReason(
        GameLocationCharacter character,
        ScopeData scope,
        bool hasAiMoveScope,
        int3 start,
        int3 finish,
        int3 expectedStart,
        ActionStatus bonusStatus)
    {
        if (hasAiMoveScope && scope.BonusActionSpent)
        {
            return AiFreeJumpBonusSpendSkipReason.AlreadySpent;
        }

        if (hasAiMoveScope && scope.JumpLandingFailed)
        {
            return AiFreeJumpBonusSpendSkipReason.LandingFailed;
        }

        if (start == int3.invalid || finish == int3.invalid)
        {
            return AiFreeJumpBonusSpendSkipReason.InvalidPosition;
        }

        if (start == finish)
        {
            return AiFreeJumpBonusSpendSkipReason.NoMove;
        }

        if (start != expectedStart)
        {
            return AiFreeJumpBonusSpendSkipReason.StartMismatch;
        }

        if (character.LocationPosition != finish)
        {
            return AiFreeJumpBonusSpendSkipReason.NotAtFinish;
        }

        return bonusStatus == ActionStatus.Available
            ? AiFreeJumpBonusSpendSkipReason.None
            : AiFreeJumpBonusSpendSkipReason.BonusUnavailable;
    }

    private static bool SpendBonusAction(
        GameLocationCharacter character,
        ScopeData scope)
    {
        if (scope.BonusActionSpent)
        {
            return false;
        }

        if (character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available)
        {
            return false;
        }

        character.SpendActionType(ActionType.Bonus);
        scope.BonusActionSpent = true;

        if (scope.Kind == ScopeKind.AiMove)
        {
            CombatAiContext.RecordAiBonusActionUse(character);
        }

        return true;
    }

    private static bool SpendAiBonusActionWithoutScope(GameLocationCharacter character)
    {
        if (character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available)
        {
            return false;
        }

        character.SpendActionType(ActionType.Bonus);
        CombatAiContext.RecordAiBonusActionUse(character);

        return true;
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

        if (scope.Kind == ScopeKind.AiTurn &&
            CombatAiContext.IsFailedAiMoveTarget(scope.Character, scope.StartPosition, destination))
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

        if (CombatAiContext.IsRejectedAiMoveTarget(scope.Character, scope.StartPosition, destination))
        {
            return false;
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
            CombatAiFreeJumpEvaluationSource.AiMovePathfinding,
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

        if (!CanLandAtCandidate(
                positioningService,
                character,
                destination,
                allowNonOccupyingCombatProxy: avoidDangerousPosition,
                out reason))
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
        bool allowNonOccupyingCombatProxy,
        out RejectionReason reason)
    {
        if (!positioningService.CanPlaceCharacter(character, destination, CellHelpers.PlacementMode.Station) &&
            (!allowNonOccupyingCombatProxy ||
             !CombatAiContext.IsAiPlacementBlockedOnlyByNonOccupyingCombatProxy(character, destination)))
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
            ScopeKind.ExplorationMove => CanShowExplorationFreeJumpToggle(character),
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
            out var affinity);

        if (!requiresAthleticsCheck)
        {
            var outcome = CharacterActionMoveStepJump.AutomaticPenalty(character, start, destination)
                ? FreeJumpPreviewOutcome.AutomaticFailure
                : FreeJumpPreviewOutcome.NoCheck;

            preview = new FreeJumpCheckPreview(
                outcome,
                moveCost,
                0,
                0,
                RuleDefinitions.AdvantageType.None);

            return true;
        }

        var successChance = ComputeSuccessChancePercent(checkDc, profile.AthleticsBonus, affinity);

        preview = new FreeJumpCheckPreview(
            FreeJumpPreviewOutcome.AthleticsCheck,
            moveCost,
            checkDc,
            successChance,
            affinity);

        return true;
    }

    private static bool TryComputeAthleticsCheck(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        FreeJumpProfile profile,
        bool applyFreeJumpBonuses,
        out int checkDc,
        out RuleDefinitions.AdvantageType affinity)
    {
        checkDc = 0;
        affinity = RuleDefinitions.AdvantageType.None;

        var rulesetCharacter = character?.RulesetCharacter;
        var usesArmorPenaltyRules = Main.Settings.ModifyJumpRulesForArmorAndEncumberance;
        var needsAthleticsCheck = CharacterActionMoveStepJump.NeedsAthleticsCheck(character, start, destination);
        var isWearingHeavy = rulesetCharacter?.IsWearingHeavyArmor() == true && usesArmorPenaltyRules;

        if (!needsAthleticsCheck)
        {
            return false;
        }

        var distance = ComputeFreeJumpMovementCost(destination - start);

        checkDc = usesArmorPenaltyRules ? distance * FeetPerCell : 15;
        affinity = isWearingHeavy
            ? RuleDefinitions.AdvantageType.Disadvantage
            : RuleDefinitions.AdvantageType.None;

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
        if (!CanShowExplorationFreeJumpToggle(character))
        {
            return;
        }

        var insertIndex = actions.IndexOf(Id.Cautious);

        actions.Insert(insertIndex >= 0 ? insertIndex + 1 : actions.Count, explorationToggleId);
    }

    private static bool CanUseExplorationFreeJump(GameLocationCharacter character)
    {
        return CanShowExplorationFreeJumpToggle(character) &&
               IsExplorationFreeJumpEnabled(character);
    }

    private static bool CanShowExplorationFreeJumpToggle(GameLocationCharacter character)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            return false;
        }

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!CanFreeJumpCharacterAct(character))
        {
            return false;
        }

        if (IsBattleInProgress())
        {
            return false;
        }

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

        var hasUnsuppressedScope = false;

        foreach (var scope in ActiveScopes.Values)
        {
            if (!AiPathfindingSuppressionDepths.ContainsKey(scope.Character.Guid))
            {
                hasUnsuppressedScope = true;
                break;
            }
        }

        if (!hasUnsuppressedScope)
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

    private static List<int> BuildTargetContactVerticalOffsets(FreeJumpProfile profile)
    {
        var offsets = new List<int> { 0 };
        var maxOffset = Math.Max(profile.MaxVerticalCells, MaxDownwardCells);

        for (var offset = 1; offset <= maxOffset; offset++)
        {
            if (offset <= profile.MaxVerticalCells)
            {
                offsets.Add(offset);
            }

            if (offset <= MaxDownwardCells)
            {
                offsets.Add(-offset);
            }
        }

        return offsets;
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
        bool requireBonusAction)
    {
        if (!Main.Settings.EnableBonusActionFreeJump)
        {
            return false;
        }

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!CanFreeJumpCharacterAct(character))
        {
            return false;
        }

        if (!IsBattleInProgress())
        {
            return false;
        }

        if (character.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        var moveStatus = character.GetActionStatus(Id.TacticalMove, ActionScope.Battle);

        if (moveStatus != ActionStatus.Available)
        {
            return false;
        }

        if (requireBonusAction)
        {
            var bonusStatus = character.GetActionTypeStatus(ActionType.Bonus);

            if (bonusStatus != ActionStatus.Available)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanFreeJumpCharacterAct(GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            !rulesetCharacter.TryGetAttribute(AttributeDefinitions.Strength, out _) ||
            rulesetCharacter is RulesetCharacterEffectProxy proxy &&
            proxy.EffectProxyDefinition?.CanMove != true)
        {
            return false;
        }

        if (IsUnableToAct(character))
        {
            return false;
        }

        if (IsFreeJumpSuppressedByFlight(character))
        {
            return false;
        }

        return true;
    }

    private static bool IsFreeJumpSuppressedByFlight(GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            return false;
        }

        return !rulesetCharacter.IsTouchingGround();
    }

    private static bool IsUnableToAct(GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            return true;
        }

        if (rulesetCharacter.IsDeadOrDyingOrUnconscious)
        {
            return true;
        }

        if (rulesetCharacter.IsIncapacitated)
        {
            return true;
        }

        return false;
    }

    private static IDisposable BeginScope(
        GameLocationCharacter character,
        ScopeKind kind,
        int3 targetPosition = default,
        bool hasTargetPosition = false)
    {
        if (IsFreeJumpSuppressedByFlight(character))
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

        if (!rulesetCharacter.TryGetAttribute(
                AttributeDefinitions.Strength,
                out var strengthAttribute))
        {
            return false;
        }

        var strength = strengthAttribute.CurrentValue;
        var strengthModifier = AttributeDefinitions.ComputeAbilityScoreModifier(strength);
        var athleticsBonus = rulesetCharacter.ComputeBaseAbilityCheckBonus(
            AttributeDefinitions.Strength, null, SkillDefinitions.Athletics);
        var athleticsTraining = Clamp(athleticsBonus - strengthModifier, 0, 12);
        var hasJumpSpell = rulesetCharacter.HasConditionOfType(ConditionJump);
        var additionalJumpCells = 0;
        var enhancedJump = false;
        foreach (var movementModifier in rulesetCharacter.GetMovementModifiers())
        {
            if (movementModifier is IMovementAffinityProvider affinity)
            {
                additionalJumpCells += affinity.AdditionalJumpCells;
                enhancedJump |= affinity.EnhancedJump;
            }
        }

        // Native maxJumpRange already includes bonuses and its own cap. Read the native
        // base before that cap so every movement-affinity bonus is applied exactly once.
        var movement = GameConfiguration.Movement;
        var nativeBase = movement.GetJumpDistanceAndType(
            strength, rulesetCharacter.IsProficient(SkillDefinitions.Athletics)).Item1;
        var computedHorizontal = Math.Min(strength / FeetPerCell + athleticsTraining / 4,
            PreferredMaxHorizontalCells);
        var rawHorizontal = Math.Max(Math.Max(nativeBase, computedHorizontal) + additionalJumpCells,
            enhancedJump ? movement.MaxJumpDistance : 1);
        var maxHorizontal = Clamp(MotionContext.ApplyJumpEncumbrance(rulesetCharacter,
            Clamp(rawHorizontal, 1, AbsoluteMaxHorizontalCells)), 1, AbsoluteMaxHorizontalCells);
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
            MotionContext.ComputeJumpDistance(delta),
            1,
            byte.MaxValue);
    }

    internal static int ComputeAiFreeJumpMovementCost(int3 start, int3 destination)
    {
        return ComputeFreeJumpMovementCost(destination - start);
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
