using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Validators;
using TA;
using TA.AI;
using UnityEngine;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.DecisionPackageDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal enum CombatAiRole
{
    Melee,
    Ranged,
    SupportCaster,
    OffensiveCaster,
    Hybrid
}

internal enum CombatAiFamily
{
    Aberration,
    Beast,
    Celestial,
    Construct,
    Dragon,
    Elemental,
    Fey,
    Fiend,
    Giant,
    Humanoid,
    Monstrosity,
    Ooze,
    Plant,
    Undead,
    Other
}

internal enum CombatAiTemperament
{
    Neutral,
    Cautious,
    Disciplined,
    Opportunistic,
    Aggressive,
    Cunning,
    CunningAggressive,
    Relentless
}

internal enum CombatAiActionKind
{
    None,
    Melee,
    Ranged,
    Spell
}

internal enum CombatAiMovementGoalKind
{
    None,
    MoveToPreferredRange,
    SearchKnownTarget,
    AdvanceToMelee,
    MeleeSpacing,
    MaintainThreatAvoidance,
    BreakThreat,
    ClearLargeAllyPath
}

internal enum CombatAiMovementPolicyKind
{
    None,
    MeleePursuitPolicy,
    MeleeSpacingPolicy,
    FlyingMeleeSpacingPolicy,
    RangedLinePolicy,
    SpellLinePolicy,
    FlyingPursuitPolicy,
    SearchKnownTargetPolicy,
    DefensivePolicy
}

internal enum CombatAiResidualHostileActionResultKind
{
    Unavailable,
    Blocked,
    Executed,
    PolicyHeld
}

internal enum MeleeThreatSourceKind
{
    None,
    Local,
    Recent,
    RecentProxyInvalid
}

internal enum ConnectedFiringLineCompletionKind
{
    Unavailable,
    Pending,
    SettledReached,
    SettledPartial,
    FailedNoMovementStep,
    FailedNoAction
}

internal enum SearchKnownTargetCompletionKind
{
    Unavailable,
    Pending,
    SettledReached,
    SettledPartial,
    FailedNoMeaningfulMovement
}

internal enum CombatAiActionLinkedMoveContinuation
{
    ImmediateResidualAction,
    ReturnToVanillaDecision,
    ProgressOnlySearchMove,
    ReturnToCoordinatorAfterRouteMove
}

internal enum CombatAiFreeJumpEvaluationSource
{
    AiMovePathfinding,
    JumpCandidateEnumeration
}

internal enum CombatAiRouteMoveSourceKind
{
    Normal,
    BonusDash,
    FreeJump,
    JumpImmediateAttack,
    ConnectedFiringLine,
    SearchLostTarget
}

internal enum CombatAiPreMainRouteMoveStatus
{
    None,
    Executed,
    Blocked,
    Unavailable
}

internal readonly struct CombatAiProfile(
    CombatAiRole role,
    CombatAiFamily family,
    CombatAiTemperament temperament,
    bool hasFlight,
    bool hasRangedBackup,
    bool hasSpellcasting)
{
    internal CombatAiRole Role { get; } = role;
    internal CombatAiFamily Family { get; } = family;
    internal CombatAiTemperament Temperament { get; } = temperament;
    internal bool HasFlight { get; } = hasFlight;
    internal bool HasRangedBackup { get; } = hasRangedBackup;
    internal bool HasSpellcasting { get; } = hasSpellcasting;
    internal bool IsMeleeSpecialist => Role == CombatAiRole.Melee;
    internal bool PrefersDistance =>
        Role is CombatAiRole.Ranged or CombatAiRole.SupportCaster or CombatAiRole.OffensiveCaster ||
        Temperament is CombatAiTemperament.Cautious or CombatAiTemperament.Cunning or CombatAiTemperament.CunningAggressive;
    internal bool PrefersAggressivePursuit =>
        Role == CombatAiRole.Melee ||
        Temperament is CombatAiTemperament.Aggressive or CombatAiTemperament.Relentless;
    internal bool PrefersAerialCombat =>
        HasFlight &&
        (Role is CombatAiRole.Ranged or CombatAiRole.SupportCaster or CombatAiRole.OffensiveCaster ||
         Family is CombatAiFamily.Dragon or CombatAiFamily.Elemental ||
         Temperament == CombatAiTemperament.CunningAggressive);
}

internal readonly struct CombatAiSelfAssessment(
    bool isWounded,
    bool isBloodied,
    bool isCritical,
    bool isProne,
    bool isRestrained,
    bool hasSeriousCondition,
    bool isConcentrating)
{
    internal bool IsWounded { get; } = isWounded;
    internal bool IsBloodied { get; } = isBloodied;
    internal bool IsCritical { get; } = isCritical;
    internal bool IsProne { get; } = isProne;
    internal bool IsRestrained { get; } = isRestrained;
    internal bool HasSeriousCondition { get; } = hasSeriousCondition;
    internal bool IsConcentrating { get; } = isConcentrating;
}

internal static partial class CombatAiContext
{
    private const int ObservedCombatMemoryMaxRounds = 2;
    private const int ObservedCombatMemoryMaxTurns = 6;
    private const string AberrationFamilyName = "Aberration";
    private const string BeastFamilyName = "Beast";
    private const string CelestialFamilyName = "Celestial";
    private const string ConstructFamilyName = "Construct";
    private const string DragonFamilyName = "Dragon";
    private const string ElementalFamilyName = "Elemental";
    private const string FeyFamilyName = "Fey";
    private const string FiendFamilyName = "Fiend";
    private const string GiantFamilyName = "Giant";
    private const string HumanoidFamilyName = "Humanoid";
    private const string MonstrosityFamilyName = "Monstrosity";
    private const string OozeFamilyName = "Ooze";
    private const string PlantFamilyName = "Plant";
    private const string UndeadFamilyName = "Undead";
    private const int FreeJumpDefaultMinimumSuccessChance = 70;
    private const int FreeJumpImprovedPositionMinimumSuccessChance = 65;
    private const int FreeJumpEmergencyMinimumSuccessChance = 50;
    private const float FreeJumpMinimumBaselineScore = 0.20f;
    private const float FreeJumpMinimumPositioningScore = 0.12f;
    private const float FreeJumpMinimumActionEconomyScore = 0.05f;
    private const float FreeJumpAttackAccessScore = 0.45f;
    private const float FreeJumpThreatReductionScore = 0.20f;
    private const float FreeJumpCoverImprovementScore = 0.12f;
    private const float FreeJumpObstacleBypassScore = 0.20f;
    private const float FreeJumpHighGroundScore = 0.08f;
    private const int FreeJumpMinimumContactRouteSavings = 2;
    private const float MovementGoalProgressScore = 0.74f;
    private const float MovementGoalMeleeProgressScore = 0.82f;
    private const float MovementGoalRangedLineProgressScore = 1.00f;
    private const float MovementGoalSpellLineProgressScore = 1.08f;
    private const float MovementGoalPreferredRangeScore = 1.60f;
    private const float MovementGoalSearchScore = 0.70f;
    private const float MovementGoalSearchActionConnectedScore = 0.35f;
    private const float MovementGoalProgressMaximumScore = 1.20f;
    private const float MovementGoalSpellLineProgressMaximumScore = 1.22f;
    private const float MovementGoalRangedLineProgressMaximumScore = 1.30f;
    private const float MovementGoalRegressionTolerance = 0.5f;
    private const float StableTieBreakScoreScale = 0.0005f;
    private const float ThrownLikeRangedMaximumRange = 24f;
    private const float ReadyOpportunityDefaultTargetMove = 6f;
    private const float MinimumSpellRouteProgress = 2.5f;
    private const float MinimumRangedRouteProgress = 2.0f;
    private const int RecentMeleeThreatMaxRounds = 2;
    private const int RecentMeleeThreatMaxTurnStamps = 8;
    private const int ThreatAvoidanceMaxRounds = 3;
    private const int ThreatAvoidanceMaxTurnStamps = 10;
    private const float ThreatAvoidanceMinimumDistanceGain = 2.0f;
    private const float ThreatAvoidanceActualDistanceGain = 1.0f;
    private const float ThreatAvoidanceReturnTolerance = 1.5f;
    private const float ThreatAvoidanceThreatZoneDistance = 2.0f;
    private const float ThreatAvoidanceClearDistance = 4.0f;
    private const int GroundMeleeTargetContactRouteBudgetMultiplier = 8;
    private const int GroundMeleeTargetContactRouteMaxBudget = 48;
    private const int GroundMeleeRoutePathfindingPerTurnLimit = 16;
    private const int SearchLostTargetNextActionProbeLimit = 8;
    private const int GroundMeleeTargetContactReverseGoalLimit = 3;
    private const int MoveResultSettlingFrameLimit = 3;
    private const int JumpImmediateMoveResultSettlingFrameLimit = MoveResultSettlingFrameLimit;
    private const int SearchKnownTargetMoveResultSettlingFrameLimit = MoveResultSettlingFrameLimit;
    private const float CandidateScoreEpsilon = 0.000001f;
    private const int GroundMeleeAttackGoalProbeBand = 2;
    private const int MeleeSpacingAllyAdjacentGridSteps = 1;
    private const int MeleeSpacingRequiredGridGap = 2;
    private const int MeleeSpacingMaximumMoveCost = 1;
    private const float MeleeSpacingTargetPressureDistance = 2.0f;
    private const float ClearAllyCorridorRegressionTolerance = 1.5f;
    private const int FlyingMeleeCandidateHorizontalRadius = 2;
    private const int FlyingMeleeCandidateHeightBelowTarget = 1;
    private const int FlyingMeleeCandidateHeightAboveTarget = 3;
    private const int RepeatedRangedAttackThreshold = 2;
    private const int ForcedFiringPositionRepeatThreshold = 3;
    private const int RepeatedMeleeAlternativeThreshold = 3;
    private static readonly string[] CautiousFlags = ["Self-Preservation", "Pragmatism", "Cynicism"];
    private static readonly string[] DisciplinedFlags = ["Authority", "Lawfulness", "Helpfulness", "Friendliness"];
    private static readonly string[] OpportunisticFlags = ["Greed", "Selfishness"];

    private static readonly Dictionary<ulong, CombatAiProfile> ProfileCache = [];
    private static readonly Dictionary<ulong, string[]> PersonalityFlagsCache = [];
    private static readonly Dictionary<ulong, ObservedCombatMemory> ObservedCombatMemoryCache = [];
    private static readonly Dictionary<ulong, HashSet<AiMoveFailureKey>> AiMoveFailureCache = [];
    private static readonly Dictionary<ulong, AiMoveAttempt> PendingAiMoveAttemptCache = [];
    private static readonly Dictionary<ulong, PostMainClearAllyCorridorAttemptMemory>
        PostMainClearAllyCorridorAttemptCache = [];
    private static int ActionLinkedMoveTokenSeed;
    private static readonly Dictionary<ulong, ActionLinkedMoveMemory> ActionLinkedMoveCache = [];
    private static readonly Dictionary<ulong, ActionLinkedMoveSettlingMemory> ActionLinkedMoveSettlingCache = [];
    private static readonly Dictionary<ulong, CompletedAiMoveStepMemory> CompletedAiMoveStepCache = [];
    private static readonly Dictionary<ulong, RouteMoveCompletionClosedMemory> RouteMoveCompletionClosedCache = [];
    private static readonly Dictionary<ulong, LostTargetSearchAttemptMemory> LostTargetSearchAttemptCache = [];
    private static readonly Dictionary<ulong, PendingRouteMovementLockMemory> PendingRouteMovementLockCache = [];
    private static readonly Dictionary<ulong, PreMainRouteMoveAttempt> PreMainRouteMoveAttemptCache = [];
    private static readonly Dictionary<ulong, DisconnectedPositioningSealMemory> DisconnectedPositioningSealCache = [];
    private static readonly Dictionary<ulong, DisconnectedPositioningMovementLockMemory>
        DisconnectedPositioningMovementLockCache = [];
    private static readonly Dictionary<ulong, TerminalSealMemory> DisconnectedSearchMoveCompletionSealCache = [];
    private static readonly Dictionary<ulong, TerminalSealMemory> DisconnectedSearchNoRouteMovementSealCache = [];
    private static readonly Dictionary<ulong, HashSet<ulong>> PendingFallbackDodgeConditionCache = [];
    private static readonly Dictionary<ulong, FallbackDodgeConditionMemory> FallbackDodgeConditionCache = [];
    private static readonly Dictionary<ulong, RepeatedAttackActionMemory> RepeatAttackActionCache = [];
    private static readonly Dictionary<ulong, AiTurnMovementProgress> TurnMovementProgressCache = [];
    private static readonly Dictionary<ulong, CombatAiActionExecutionMemory> LastActionExecutionCache = [];
    private static readonly Dictionary<ulong, CombatAiActionExecutionMemory> LastMainActionExecutionCache = [];
    private static readonly Dictionary<ulong, int> TurnMainActionUseCountCache = [];
    private static readonly Dictionary<ulong, int> TurnBonusActionUseCountCache = [];
    private static readonly Dictionary<ulong, RecentMeleeThreatMemory> RecentMeleeThreatMemoryCache = [];
    private static readonly Dictionary<ulong, ThreatAvoidanceMemory> ThreatAvoidanceMemoryCache = [];
    private static readonly Dictionary<ulong, PendingResidualMainAction> PendingResidualMainActionCache = [];
    private static readonly Dictionary<ulong, BaselineFreeJumpAttemptMemory> BaselineFreeJumpAttemptCache = [];
    private static readonly Dictionary<AttackPositionKey, bool> MeleeAttackPositionCache = [];
    private static readonly Dictionary<RouteCandidateCacheKey, bool> ActionKindPositionCache = [];
    private static readonly Dictionary<RouteCandidateCacheKey, CoverEvaluationMemory> CoverEvaluationCache = [];
    private static readonly HashSet<ulong> JumpImmediateAttackReachableCache = [];
    private static readonly HashSet<ulong> GroundMeleeJumpRouteAvailableCache = [];
    private static readonly Dictionary<ulong, CurrentStateRouteBlockKind> CurrentStateRouteBlockCache = [];
    private static readonly Dictionary<ulong, GroundMeleeTargetContactRouteQuery> TargetContactRouteQueryCache = [];
    private static readonly Dictionary<ulong, GroundMeleeRouteFailureMemory> GroundMeleeRouteFailureCache = [];
    private static readonly Dictionary<ulong, GroundMeleeMoveSettlingMemory> GroundMeleeMoveSettlingCache = [];
    private static readonly Dictionary<ulong, GroundMeleePartialRouteMemory> GroundMeleePartialRouteCache = [];
    private static readonly Dictionary<ulong, ProxyThreatRouteAttemptMemory> ProxyThreatRouteAttemptCache = [];
    private static readonly Dictionary<ReachableRouteCacheKey, ReachableRouteDestinationMemory>
        ReachableRouteDestinationCache = [];
    private static readonly Dictionary<ActorTurnKey, int> ReachableRoutePathfindingCountCache = [];
    private static object CurrentBattleCacheKey;
    private static int CurrentBattleCacheRound = -1;
    private static bool? CurrentAdvancedCombatAiSetting;
    private static bool? CurrentBonusActionFreeJumpSetting;
    private static int ObservedCombatMemoryTurnStamp;

    private enum CombatAiMovementPlanReasonKind
    {
        None,
        BreakThreat,
        ImproveFiringPosition,
        ConnectFiringLine,
        ClearAllyCorridor,
        MeleeSpacing,
        MaintainThreatAvoidance,
        SearchKnownTarget,
        KnownTarget,
        PreferredMelee
    }

    private enum CurrentStateRouteBlockKind
    {
        None,
        RangedSeekDisconnected,
        SeekRegression,
        NoPostMoveAction
    }

    private enum ProxyThreatActivityState
    {
        Clear,
        Inactive,
        Active
    }

    private enum CombatAiExecutedActionKind
    {
        None,
        AttackMain,
        CastMain,
        Shove,
        Grapple,
        Ready,
        Dodge,
        BonusAction,
        TacticalMove
    }

    [Flags]
    private enum RouteMoveCompletionFlags
    {
        None = 0,
        NoMove = 1,
        LateCompletion = 2,
        RouteUnavailable = 4,
        NoConnectedRoute = 8,
        GroundMeleeNoMove = 16,
        GroundMeleePartial = 32
    }

    [Flags]
    private enum PreMainRouteMoveFlags
    {
        None = 0,
        VanillaOwned = 1,
        RequiresMainDash = 2,
        DisconnectedSeekFailure = 4
    }

    private readonly struct CurrentTerminalActionScan(
        bool hasValidatedAction,
        bool hasUsefulUtility)
    {
        internal bool HasValidatedAction { get; } = hasValidatedAction;
        internal bool HasUsefulUtility { get; } = hasUsefulUtility;
        internal bool BlocksReadyOrDodge => HasValidatedAction || HasUsefulUtility;
    }

    private readonly struct SearchKnownTargetProgress(
        int3 anchor,
        float startDistance,
        float currentDistance,
        float requiredProgress)
    {
        internal int3 Anchor { get; } = anchor;
        internal float StartDistance { get; } = startDistance;
        internal float CurrentDistance { get; } = currentDistance;
        internal float RequiredProgress { get; } = requiredProgress;
        internal float Progress => StartDistance - CurrentDistance;
        internal bool HasMeaningfulProgress => Progress + 0.01f >= RequiredProgress;
    }

    private readonly struct ConnectedFiringLineCompletionResult(
        ConnectedFiringLineCompletionKind kind,
        int3 actualDestination)
    {
        internal ConnectedFiringLineCompletionKind Kind { get; } = kind;
        internal int3 ActualDestination { get; } = actualDestination;
        internal bool IsComplete =>
            Kind is ConnectedFiringLineCompletionKind.SettledReached or
                ConnectedFiringLineCompletionKind.SettledPartial;
        internal bool Failed =>
            Kind is ConnectedFiringLineCompletionKind.FailedNoMovementStep or
                ConnectedFiringLineCompletionKind.FailedNoAction;
    }

    private readonly struct SearchKnownTargetCompletionResult(
        SearchKnownTargetCompletionKind kind,
        SearchKnownTargetProgress progress)
    {
        internal SearchKnownTargetCompletionKind Kind { get; } = kind;
        internal SearchKnownTargetProgress Progress { get; } = progress;
        internal bool IsComplete =>
            Kind is SearchKnownTargetCompletionKind.SettledReached or
                SearchKnownTargetCompletionKind.SettledPartial;
        internal bool Failed => Kind == SearchKnownTargetCompletionKind.FailedNoMeaningfulMovement;
    }

    private readonly struct ObservedCombatMemory(
        int3 lastKnownEnemyPosition,
        int round,
        int turnStamp)
    {
        internal int3 LastKnownEnemyPosition { get; } = lastKnownEnemyPosition;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private enum SearchLostTargetRouteCandidateQuality
    {
        Rejected,
        FiringLineProbe,
        TurnsImproved,
        SeverityImproved,
        Connected
    }

    private readonly struct RouteActionConnection(
        bool connected,
        bool rangeBlocked,
        bool canAttackBlocked,
        bool dashMainRejected)
    {
        internal bool Connected { get; } = connected;
        internal bool RangeBlocked { get; } = rangeBlocked;
        internal bool CanAttackBlocked { get; } = canAttackBlocked;
        internal bool DashMainRejected { get; } = dashMainRejected;
    }

    private readonly struct SearchLostTargetRouteCandidate(
        int3 position,
        float score,
        float progress,
        int turnsToAction,
        bool actionConnected,
        SearchLostTargetRouteCandidateQuality quality,
        bool forwardProgress,
        bool nextActionReachable,
        RouteActionConnection actionConnection,
        int moveCost)
    {
        internal int3 Position { get; } = position;
        internal float Score { get; } = score;
        internal float Progress { get; } = progress;
        internal int TurnsToAction { get; } = turnsToAction;
        internal bool ActionConnected { get; } = actionConnected;
        internal SearchLostTargetRouteCandidateQuality Quality { get; } = quality;
        internal bool BlockSeverityImproved => Quality == SearchLostTargetRouteCandidateQuality.SeverityImproved;
        internal bool TurnsToActionImproved => Quality == SearchLostTargetRouteCandidateQuality.TurnsImproved;
        internal bool FiringLineProbe => Quality == SearchLostTargetRouteCandidateQuality.FiringLineProbe;
        internal bool NextActionReachable { get; } = nextActionReachable;
        internal bool ForwardProgress { get; } = forwardProgress;
        internal RouteActionConnection ActionConnection { get; } = actionConnection;
        internal int MoveCost { get; } = moveCost;
    }

    private readonly struct ClearAllyCorridorCandidateResult(
        bool accepted,
        float score = 0f)
    {
        internal bool Accepted { get; } = accepted;
        internal float Score { get; } = score;
    }

    private readonly struct PendingResidualMainAction(
        Id actionId,
        int round,
        int turnStamp)
    {
        internal Id ActionId { get; } = actionId;
        private int Round { get; } = round;
        private int TurnStamp { get; } = turnStamp;

        internal bool Matches(Id candidateActionId, int round, int turnStamp)
        {
            return ActionId == candidateActionId && Round == round && TurnStamp == turnStamp;
        }
    }

    private readonly struct RouteMoveCompletionClosedMemory(
        CombatAiMovementGoalKind movementGoal,
        int3 startPosition,
        int3 expectedDestination,
        RouteMoveCompletionFlags flags,
        int round,
        int turnStamp)
    {
        internal CombatAiMovementGoalKind MovementGoal { get; } = movementGoal;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 ExpectedDestination { get; } = expectedDestination;
        internal RouteMoveCompletionFlags Flags { get; } = flags;
        internal bool IsNoMove => (Flags & RouteMoveCompletionFlags.NoMove) != 0;
        internal bool IsLateCompletion => (Flags & RouteMoveCompletionFlags.LateCompletion) != 0;
        internal bool IsRouteUnavailable => (Flags & RouteMoveCompletionFlags.RouteUnavailable) != 0;
        internal bool HasNoConnectedRoute => (Flags & RouteMoveCompletionFlags.NoConnectedRoute) != 0;
        internal bool IsGroundMeleeNoMove => (Flags & RouteMoveCompletionFlags.GroundMeleeNoMove) != 0;
        internal bool IsGroundMeleePartial => (Flags & RouteMoveCompletionFlags.GroundMeleePartial) != 0;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool Matches(CombatAiMovementGoalKind movementGoal, int3 startPosition, int3 expectedDestination,
            int round, int turnStamp)
        {
            return MovementGoal == movementGoal &&
                   StartPosition == startPosition &&
                   ExpectedDestination == expectedDestination &&
                   Round == round &&
                   TurnStamp == turnStamp;
        }
    }

    private readonly struct TerminalSealMemory(
        int round,
        int turnStamp)
    {
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool Matches(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }
    }

    private sealed class AiTurnMovementProgress
    {
        private readonly HashSet<int3> visitedPositions;
        private readonly List<AiAcceptedMoveCandidate> acceptedMoveCandidates = [];

        internal AiTurnMovementProgress(
            int3 startPosition,
            float bestDistanceToGoal,
            CombatAiTurnPlan turnPlan)
        {
            visitedPositions = [startPosition];
            StartPosition = startPosition;
            BestDistanceToGoal = bestDistanceToGoal;
            TargetGuid = turnPlan.MovementPlan.Target?.Guid ?? 0;
            TargetPosition = turnPlan.MovementPlan.TargetPosition;
            Goal = turnPlan.MovementPlan.Goal;
            Policy = turnPlan.MovementPlan.Policy;
            BeginEvaluation(startPosition, bestDistanceToGoal);
        }

        internal int3 StartPosition { get; }
        private ulong TargetGuid { get; }
        internal ulong PlannedTargetGuid => TargetGuid;
        private int3 TargetPosition { get; }
        private CombatAiMovementGoalKind Goal { get; }
        private CombatAiMovementPolicyKind Policy { get; }
        internal float BestDistanceToGoal { get; private set; }
        internal bool HadMeaningfulMovementCandidate { get; private set; }
        internal bool HadPreferredActionMovementCandidate { get; private set; }
        internal bool HadFreeJumpMovementCandidate { get; private set; }
        private int3 BestAcceptedPosition { get; set; }
        private float BestAcceptedScore { get; set; }
        private float BestAcceptedProgress { get; set; }
        private int BestAcceptedTurnsToAction { get; set; } = -1;
        private float BestPreferredActionScore { get; set; }
        private int BestPreferredActionTurnsToAction { get; set; } = -1;
        internal bool HasAcceptedMoveCandidate => acceptedMoveCandidates.Count > 0;
        internal bool HasImmediatePreferredActionMoveCandidate =>
            HadPreferredActionMovementCandidate && BestPreferredActionTurnsToAction == 0;
        internal int3 BestMoveCandidatePosition => BestAcceptedPosition;
        internal int BestMoveCandidateTurnsToAction => BestAcceptedTurnsToAction;
        internal int BestPreferredActionMoveTurnsToAction => BestPreferredActionTurnsToAction;

        internal bool Matches(CombatAiTurnPlan turnPlan)
        {
            return TargetGuid == (turnPlan.MovementPlan.Target?.Guid ?? 0) &&
                   TargetPosition == turnPlan.MovementPlan.TargetPosition &&
                   Goal == turnPlan.MovementPlan.Goal &&
                   Policy == turnPlan.MovementPlan.Policy;
        }

        internal void BeginEvaluation(int3 currentPosition, float distanceToGoal)
        {
            visitedPositions.Add(currentPosition);
            BestDistanceToGoal = Math.Min(BestDistanceToGoal, distanceToGoal);
            acceptedMoveCandidates.Clear();
            HadMeaningfulMovementCandidate = false;
            HadPreferredActionMovementCandidate = false;
            HadFreeJumpMovementCandidate = false;
            BestAcceptedPosition = currentPosition;
            BestAcceptedScore = float.MinValue;
            BestAcceptedProgress = 0f;
            BestAcceptedTurnsToAction = -1;
            BestPreferredActionScore = float.MinValue;
            BestPreferredActionTurnsToAction = -1;
        }

        internal bool TryGetBestAcceptedMoveCandidate(out AiAcceptedMoveCandidate candidate)
        {
            candidate = default;

            if (!HasAcceptedMoveCandidate)
            {
                return false;
            }

            candidate = new AiAcceptedMoveCandidate(
                BestAcceptedPosition,
                BestAcceptedScore,
                BestAcceptedProgress,
                BestAcceptedTurnsToAction);
            return true;
        }

        internal bool HasVisited(int3 position)
        {
            return visitedPositions.Contains(position);
        }

        internal void MarkVisited(int3 position, float distanceToGoal)
        {
            visitedPositions.Add(position);
            BestDistanceToGoal = Math.Min(BestDistanceToGoal, distanceToGoal);
        }

        internal void MarkMeaningfulMovementCandidate()
        {
            HadMeaningfulMovementCandidate = true;
        }

        internal void MarkPreferredActionMovementCandidate()
        {
            HadPreferredActionMovementCandidate = true;
            HadMeaningfulMovementCandidate = true;
        }

        internal void MarkFreeJumpMovementCandidate()
        {
            HadFreeJumpMovementCandidate = true;
            HadMeaningfulMovementCandidate = true;
        }

        internal void RecordAccepted(
            int3 position,
            float score,
            float progress,
            bool preferredActionCandidate = false,
            int turnsToAction = -1)
        {
            acceptedMoveCandidates.Add(new AiAcceptedMoveCandidate(position, score, progress, turnsToAction));

            if (preferredActionCandidate && score > BestPreferredActionScore + 0.000001f)
            {
                BestPreferredActionScore = score;
                BestPreferredActionTurnsToAction = turnsToAction;
            }

            if (score <= BestAcceptedScore + 0.000001f)
            {
                return;
            }

            BestAcceptedPosition = position;
            BestAcceptedScore = score;
            BestAcceptedProgress = progress;
            BestAcceptedTurnsToAction = turnsToAction;
        }

        internal IEnumerable<int3> EnumerateAcceptedMoveCandidatePositions()
        {
            var seenPositions = new HashSet<int3>();

            foreach (var candidate in acceptedMoveCandidates
                         .OrderByDescending(candidate => candidate.Score)
                         .ThenBy(candidate => candidate.Position.x)
                         .ThenBy(candidate => candidate.Position.y)
                         .ThenBy(candidate => candidate.Position.z))
            {
                if (!seenPositions.Add(candidate.Position))
                {
                    continue;
                }

                yield return candidate.Position;
            }
        }

        internal IEnumerable<AiAcceptedMoveCandidate> EnumerateAcceptedMoveCandidates()
        {
            var seenPositions = new HashSet<int3>();

            foreach (var candidate in acceptedMoveCandidates
                         .OrderByDescending(candidate => candidate.Score)
                         .ThenBy(candidate => candidate.TurnsToAction)
                         .ThenBy(candidate => candidate.Position.x)
                         .ThenBy(candidate => candidate.Position.y)
                         .ThenBy(candidate => candidate.Position.z))
            {
                if (!seenPositions.Add(candidate.Position))
                {
                    continue;
                }

                yield return candidate;
            }
        }

    }

    private readonly struct AiAcceptedMoveCandidate(
        int3 position,
        float score,
        float progress,
        int turnsToAction)
    {
        internal int3 Position { get; } = position;
        internal float Score { get; } = score;
        internal float Progress { get; } = progress;
        internal int TurnsToAction { get; } = turnsToAction;
    }

    private readonly struct BonusDashMeleeCandidate(
        GameLocationCharacter target,
        int3 destination)
    {
        internal GameLocationCharacter Target { get; } = target;
        internal int3 Destination { get; } = destination;
        internal bool IsAvailable => Target != null;
    }

    private readonly struct ConnectedFiringLineCandidate(
        int3 position,
        float score,
        CombatAiActionKind actionKind,
        int turnsToAction,
        int moveCost)
    {
        internal int3 Position { get; } = position;
        internal float Score { get; } = score;
        internal CombatAiActionKind ActionKind { get; } = actionKind;
        internal int TurnsToAction { get; } = turnsToAction;
        internal int MoveCost { get; } = moveCost;
    }

    private readonly struct GroundMeleeTargetContactRouteQuery(
        GroundMeleeTargetContactRouteMap map,
        bool complete,
        int3 bestGoal,
        int bestGoalMoveCost,
        IReadOnlyDictionary<int3, int> contactCostByPosition,
        IReadOnlyDictionary<int3, int3> contactGoalByPosition,
        ulong targetGuid,
        int3 targetPosition,
        int round,
        int turnStamp,
        bool isApproachRoute = false)
    {
        internal GroundMeleeTargetContactRouteMap Map { get; } = map;
        internal bool Complete { get; } = complete;
        internal int3 BestGoal { get; } = bestGoal;
        internal int BestGoalMoveCost { get; } = Math.Max(0, bestGoalMoveCost);
        internal IReadOnlyDictionary<int3, int> ContactCostByPosition { get; } = contactCostByPosition;
        internal IReadOnlyDictionary<int3, int3> ContactGoalByPosition { get; } = contactGoalByPosition;
        internal ulong TargetGuid { get; } = targetGuid;
        internal int3 TargetPosition { get; } = targetPosition;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal bool IsCompleteApproach { get; } = complete && isApproachRoute;

        internal bool Matches(
            GameLocationCharacter target,
            int3 expectedTargetPosition,
            int3 startPosition,
            int round,
            int turnStamp)
        {
            return target?.RulesetCharacter != null &&
                   TargetGuid == target.Guid &&
                   TargetPosition == expectedTargetPosition &&
                   Map.StartPosition == startPosition &&
                   Round == round &&
                   TurnStamp == turnStamp;
        }

        internal int GetMoveCost(int3 position)
        {
            return Map.GetMoveCost(position);
        }

        internal bool TryGetContactCost(int3 position, out int contactMoveCost, out int3 contactGoal)
        {
            contactMoveCost = 0;
            contactGoal = default;

            if (ContactCostByPosition == null ||
                !ContactCostByPosition.TryGetValue(position, out var cost))
            {
                return false;
            }

            contactMoveCost = Math.Max(0, cost);

            if (ContactGoalByPosition != null &&
                ContactGoalByPosition.TryGetValue(position, out var goal))
            {
                contactGoal = goal;
            }
            else
            {
                contactGoal = BestGoal;
            }

            return true;
        }
    }

    private readonly struct GroundMeleeTargetContactRouteMap(
        int3 startPosition,
        IReadOnlyList<int3> positions,
        IReadOnlyList<int3> contactPositions,
        IReadOnlyDictionary<int3, int> moveCostByPosition,
        int3 bestPosition,
        bool complete)
    {
        internal int3 StartPosition { get; } = startPosition;
        internal IReadOnlyList<int3> Positions { get; } = positions;
        internal IReadOnlyList<int3> ContactPositions { get; } = contactPositions;
        internal int3 BestPosition { get; } = bestPosition;
        internal bool Complete { get; } = complete;
        internal int NodeCount => Positions?.Count ?? 0;

        internal bool Contains(int3 position)
        {
            return moveCostByPosition?.ContainsKey(position) == true;
        }

        internal int GetMoveCost(int3 position)
        {
            return moveCostByPosition != null &&
                    moveCostByPosition.TryGetValue(position, out var moveCost)
                ? moveCost
                : 0;
        }
    }

    private readonly struct GroundMeleeRouteFailureMemory(
        ulong targetGuid,
        int3 startPosition,
        int3 failedDestination,
        int round,
        int turnStamp)
    {
        internal ulong TargetGuid { get; } = targetGuid;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 FailedDestination { get; } = failedDestination;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool Matches(GameLocationCharacter target, int3 start)
        {
            return target?.RulesetCharacter != null &&
                   target.Guid == TargetGuid &&
                   start == StartPosition;
        }

        internal bool Blocks(int3 candidate)
        {
            if (candidate == FailedDestination)
            {
                return true;
            }

            if (Math.Abs(candidate.y - FailedDestination.y) > 1)
            {
                return false;
            }

            var failedStepX = Math.Sign(FailedDestination.x - StartPosition.x);
            var failedStepZ = Math.Sign(FailedDestination.z - StartPosition.z);
            var candidateStepX = Math.Sign(candidate.x - StartPosition.x);
            var candidateStepZ = Math.Sign(candidate.z - StartPosition.z);

            return (failedStepX != 0 || failedStepZ != 0) &&
                   failedStepX == candidateStepX &&
                   failedStepZ == candidateStepZ &&
                   ComputeHorizontalGridStepDistance(candidate, FailedDestination) <= 2;
        }
    }

    private readonly struct ReachableRouteDestinationFacts(
        int moveCost,
        MoveMode moveMode,
        PathfindingNode.InformationFlag moveFlags)
    {
        internal int MoveCost { get; } = moveCost;
        internal MoveMode MoveMode { get; } = moveMode;
        internal PathfindingNode.InformationFlag MoveFlags { get; } = moveFlags;
    }

    private sealed class ReachableRouteDestinationMemory(
        int3 startPosition,
        int remainingMove,
        int round,
        int turnStamp,
        List<int3> positions,
        Dictionary<int3, ReachableRouteDestinationFacts> factsByPosition,
        bool walkOnly)
    {
        internal int3 StartPosition { get; } = startPosition;
        internal int RemainingMove { get; } = remainingMove;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal IReadOnlyList<int3> Positions { get; } = positions;
        internal bool WalkOnly { get; } = walkOnly;

        internal bool Matches(int3 start, int remaining, int round, int turnStamp, bool walkOnly)
        {
            return StartPosition == start &&
                   RemainingMove == remaining &&
                   Round == round &&
                   TurnStamp == turnStamp &&
                   WalkOnly == walkOnly;
        }

        internal bool Contains(int3 position)
        {
            return factsByPosition.ContainsKey(position);
        }

        internal int GetMoveCost(int3 position)
        {
            return factsByPosition.TryGetValue(position, out var facts)
                ? facts.MoveCost
                : ComputeForcedMoveCost(StartPosition, position);
        }

        internal MoveMode GetMoveMode(int3 position)
        {
            return factsByPosition.TryGetValue(position, out var facts)
                ? facts.MoveMode
                : MoveMode.Walk;
        }

        internal PathfindingNode.InformationFlag GetMoveFlags(int3 position)
        {
            return factsByPosition.TryGetValue(position, out var facts)
                ? facts.MoveFlags
                : default;
        }
    }

    private readonly struct CoverEvaluationMemory(bool hasCover, CoverType coverType)
    {
        internal bool HasCover { get; } = hasCover;
        internal CoverType CoverType { get; } = coverType;
    }

    private readonly struct FallbackDodgeConditionMemory(ulong conditionGuid, int round, int turnStamp)
    {
        internal ulong ConditionGuid { get; } = conditionGuid;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct DisconnectedPositioningMovementLockMemory(int round, int turnStamp)
    {
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool MatchesCurrentTurn(int currentRound, int currentTurnStamp)
        {
            return Round == currentRound && TurnStamp == currentTurnStamp;
        }
    }

    private readonly struct AiMoveFailureKey(int3 start, int3 target) : IEquatable<AiMoveFailureKey>
    {
        private int3 Start { get; } = start;
        private int3 Target { get; } = target;

        public bool Equals(AiMoveFailureKey other)
        {
            return Start == other.Start && Target == other.Target;
        }

        public override bool Equals(object obj)
        {
            return obj is AiMoveFailureKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Start.x;

                hash = (hash * 397) ^ Start.y;
                hash = (hash * 397) ^ Start.z;
                hash = (hash * 397) ^ Target.x;
                hash = (hash * 397) ^ Target.y;
                hash = (hash * 397) ^ Target.z;

                return hash;
            }
        }
    }

    private readonly struct AiMoveAttempt(int3 start, int3 target)
    {
        internal int3 Start { get; } = start;
        internal int3 Target { get; } = target;
    }

    private readonly struct PostMainClearAllyCorridorAttemptMemory(
        int3 start,
        int3 target,
        string allyGuid,
        ulong targetGuid,
        int round,
        int turnStamp,
        bool blocked)
    {
        internal int3 Start { get; } = start;
        internal int3 Target { get; } = target;
        internal string AllyGuid { get; } = string.IsNullOrEmpty(allyGuid) ? "none" : allyGuid;
        internal ulong TargetGuid { get; } = targetGuid;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal bool Blocked { get; } = blocked;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }

        internal PostMainClearAllyCorridorAttemptMemory WithBlocked()
        {
            return new PostMainClearAllyCorridorAttemptMemory(
                Start,
                Target,
                AllyGuid,
                TargetGuid,
                Round,
                TurnStamp,
                true);
        }
    }

    private readonly struct ActionLinkedMoveMemory(
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        CombatAiActionLinkedMoveContinuation continuation,
        CombatAiMovementGoalKind movementGoal,
        int3 startPosition,
        int3 expectedDestination,
        CombatAiRouteMoveSourceKind routeMoveSource,
        bool lockRemainingMovementAfterArrival,
        int round,
        int turnStamp,
        int moveToken = 0,
        bool searchRouteActionConnected = false,
        int routeContinuationCount = 0)
    {
        internal GameLocationCharacter Target { get; } = target;
        internal CombatAiActionKind ActionKind { get; } = actionKind;
        internal CombatAiActionLinkedMoveContinuation Continuation { get; } = continuation;
        internal CombatAiMovementGoalKind MovementGoal { get; } = movementGoal;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 ExpectedDestination { get; } = expectedDestination;
        internal CombatAiRouteMoveSourceKind RouteMoveSource { get; } = routeMoveSource;
        internal bool LockRemainingMovementAfterArrival { get; } = lockRemainingMovementAfterArrival;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal int MoveToken { get; } = moveToken;
        internal bool SearchRouteActionConnected { get; } = searchRouteActionConnected;
        internal int RouteContinuationCount { get; } = routeContinuationCount;
    }

    private static int CreateActionLinkedMoveToken()
    {
        unchecked
        {
            ActionLinkedMoveTokenSeed++;

            if (ActionLinkedMoveTokenSeed == 0)
            {
                ActionLinkedMoveTokenSeed++;
            }

            return ActionLinkedMoveTokenSeed;
        }
    }

    private static bool TryGetPendingActionLinkedMoveToken(
        GameLocationCharacter character,
        CombatAiRouteMoveSourceKind routeMoveSource,
        int3 destination,
        out int moveToken)
    {
        moveToken = 0;

        if (character?.RulesetCharacter == null ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
            pendingAction.RouteMoveSource != routeMoveSource ||
            pendingAction.ExpectedDestination != destination)
        {
            return false;
        }

        moveToken = pendingAction.MoveToken;
        return moveToken != 0;
    }

    private static bool IsCurrentActionLinkedMoveToken(
        GameLocationCharacter character,
        CombatAiRouteMoveSourceKind routeMoveSource,
        int moveToken,
        out ActionLinkedMoveMemory pendingAction)
    {
        pendingAction = default;

        if (character?.RulesetCharacter == null || moveToken == 0)
        {
            return false;
        }

        if (ActionLinkedMoveCache.TryGetValue(character.Guid, out pendingAction) &&
            pendingAction.MoveToken == moveToken &&
            pendingAction.RouteMoveSource == routeMoveSource)
        {
            return true;
        }

        if (ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var actionLinkedSettling) &&
            actionLinkedSettling.PendingAction.MoveToken == moveToken &&
            actionLinkedSettling.PendingAction.RouteMoveSource == routeMoveSource)
        {
            pendingAction = actionLinkedSettling.PendingAction;
            return true;
        }

        if (GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var settling) &&
            settling.PendingAction.MoveToken == moveToken &&
            settling.PendingAction.RouteMoveSource == routeMoveSource)
        {
            pendingAction = settling.PendingAction;
            return true;
        }

        pendingAction = default;
        return false;
    }

    private readonly struct CompletedAiMoveStepMemory(
        int3 from,
        int3 to,
        CombatAiRouteMoveSourceKind routeMoveSource,
        int moveToken,
        int round,
        int turnStamp)
    {
        internal int3 From { get; } = from;
        internal int3 To { get; } = to;
        internal CombatAiRouteMoveSourceKind RouteMoveSource { get; } = routeMoveSource;
        internal int MoveToken { get; } = moveToken;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool Matches(ActionLinkedMoveMemory pendingAction, int round, int turnStamp)
        {
            return RouteMoveSource == pendingAction.RouteMoveSource &&
                   MoveToken == pendingAction.MoveToken &&
                   Round == round &&
                   TurnStamp == turnStamp;
        }
    }

    private readonly struct GroundMeleeMoveSettlingMemory(
        ActionLinkedMoveMemory pendingAction,
        int round,
        int turnStamp)
    {
        internal ActionLinkedMoveMemory PendingAction { get; } = pendingAction;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }
    }

    private readonly struct ActionLinkedMoveSettlingMemory(
        ActionLinkedMoveMemory pendingAction,
        int3 resultStart,
        int3 resultTarget,
        int round,
        int turnStamp,
        bool callbackObserved = false)
    {
        internal ActionLinkedMoveMemory PendingAction { get; } = pendingAction;
        internal int3 ResultStart { get; } = resultStart;
        internal int3 ResultTarget { get; } = resultTarget;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal bool CallbackObserved { get; } = callbackObserved;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }

        internal ActionLinkedMoveSettlingMemory WithCallbackObserved()
        {
            return new ActionLinkedMoveSettlingMemory(
                PendingAction,
                ResultStart,
                ResultTarget,
                Round,
                TurnStamp,
                true);
        }
    }

    private readonly struct GroundMeleePartialRouteMemory(
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        int remainingTacticalMoves,
        int round,
        int turnStamp)
    {
        internal ActionLinkedMoveMemory PendingAction { get; } = pendingAction;
        internal int3 ActualDestination { get; } = actualDestination;
        internal int RemainingTacticalMoves { get; } = remainingTacticalMoves;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }
    }

    private readonly struct PendingRouteMovementLockMemory(
        CombatAiMovementGoalKind movementGoal,
        CombatAiActionLinkedMoveContinuation continuation,
        int3 startPosition,
        int3 expectedDestination,
        int round,
        int turnStamp)
    {
        internal CombatAiMovementGoalKind MovementGoal { get; } = movementGoal;
        internal CombatAiActionLinkedMoveContinuation Continuation { get; } = continuation;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 ExpectedDestination { get; } = expectedDestination;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct ProxyThreatRouteAttemptMemory(
        GameLocationCharacter source,
        int3 sourcePosition,
        int3 startPosition,
        int3 failedDestination,
        CombatAiRouteMoveSourceKind moveSource,
        int round,
        int turnStamp,
        bool noMove)
    {
        internal GameLocationCharacter Source { get; } = source;
        internal int3 SourcePosition { get; } = sourcePosition;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 FailedDestination { get; } = failedDestination;
        internal CombatAiRouteMoveSourceKind MoveSource { get; } = moveSource;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal bool NoMove { get; } = noMove;

        internal bool MatchesSource(RecentMeleeThreatMemory memory)
        {
            var sourceMatches = Source != null && memory.Source != null
                ? Source.Guid == memory.Source.Guid
                : SourcePosition == memory.SourcePosition;

            return sourceMatches;
        }
    }

    private readonly struct PreMainRouteMoveAttempt(
        CombatAiPreMainRouteMoveStatus status,
        CombatAiMovementGoalKind goal,
        int3 expectedDestination,
        PreMainRouteMoveFlags flags,
        int round,
        int turnStamp)
    {
        internal CombatAiPreMainRouteMoveStatus Status { get; } = status;
        internal CombatAiMovementGoalKind Goal { get; } = goal;
        internal int3 ExpectedDestination { get; } = expectedDestination;
        internal PreMainRouteMoveFlags Flags { get; } = flags;
        internal bool IsVanillaOwned => (Flags & PreMainRouteMoveFlags.VanillaOwned) != 0;
        internal bool RequiresMainDash => (Flags & PreMainRouteMoveFlags.RequiresMainDash) != 0;
        internal bool IsDisconnectedSeekFailure => (Flags & PreMainRouteMoveFlags.DisconnectedSeekFailure) != 0;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct DisconnectedPositioningSealMemory(
        CombatAiMovementGoalKind goal,
        CombatAiMovementPolicyKind policy,
        int3 startPosition,
        int round,
        int turnStamp)
    {
        internal CombatAiMovementGoalKind Goal { get; } = goal;
        internal CombatAiMovementPolicyKind Policy { get; } = policy;
        internal int3 StartPosition { get; } = startPosition;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }
    }

    private readonly struct RecentMeleeThreatMemory(
        GameLocationCharacter source,
        int3 sourcePosition,
        bool isEffectProxy,
        int round,
        int turnStamp)
    {
        internal GameLocationCharacter Source { get; } = source;
        internal int3 SourcePosition { get; } = sourcePosition;
        internal bool IsEffectProxy { get; } = isEffectProxy;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct ThreatAvoidanceMemory(
        GameLocationCharacter source,
        int3 sourcePosition,
        bool isEffectProxy,
        int3 startPosition,
        int3 safePosition,
        bool hasSafePosition,
        CombatAiMovementGoalKind movementGoal,
        bool handledThisTurn,
        int round,
        int turnStamp)
    {
        internal GameLocationCharacter Source { get; } = source;
        internal int3 SourcePosition { get; } = sourcePosition;
        internal bool IsEffectProxy { get; } = isEffectProxy;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 SafePosition { get; } = safePosition;
        internal bool HasSafePosition { get; } = hasSafePosition;
        internal CombatAiMovementGoalKind MovementGoal { get; } = movementGoal;
        internal bool HandledThisTurn { get; } = handledThisTurn;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct RepeatedAttackActionMemory(
        ulong targetGuid,
        string actionIdentity,
        CombatAiActionKind actionKind,
        int3 actorPosition,
        int3 targetPosition,
        int round,
        int turnStamp,
        int repeatCount)
    {
        internal ulong TargetGuid { get; } = targetGuid;
        internal string ActionIdentity { get; } = actionIdentity;
        internal CombatAiActionKind ActionKind { get; } = actionKind;
        internal int3 ActorPosition { get; } = actorPosition;
        internal int3 TargetPosition { get; } = targetPosition;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal int RepeatCount { get; } = repeatCount;
    }

    private readonly struct BaselineFreeJumpAttemptMemory(
        CombatAiPreMainRouteMoveStatus status,
        int3 start,
        int3 destination,
        int round,
        int turnStamp)
    {
        internal CombatAiPreMainRouteMoveStatus Status { get; } = status;
        internal int3 Start { get; } = start;
        internal int3 Destination { get; } = destination;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct LostTargetSearchAttemptMemory(
        int round,
        int turnStamp,
        int3 start,
        int3 anchor)
    {
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal int3 Start { get; } = start;
        internal int3 Anchor { get; } = anchor;

        internal bool Matches(int currentRound, int currentTurnStamp, int3 currentStart, int3 currentAnchor)
        {
            return Round == currentRound &&
                   TurnStamp == currentTurnStamp &&
                   Start == currentStart &&
                   Anchor == currentAnchor;
        }
    }

    private readonly struct EnemyEvaluation(
        GameLocationCharacter enemy,
        float distance,
        bool meleeReachable,
        bool unreachableFlyingForMelee,
        bool exposesActorToMeleeThreat,
        bool rangedAttackAvailableFromPosition,
        bool spellAttackAvailableFromPosition,
        CoverType rangedCoverType,
        bool enemyCanRangedAttackActorFromPosition,
        CoverType actorCoverFromEnemyRangedAttack,
        bool knownRangedOrCasterThreat,
        bool isWounded,
        bool isConcentrating,
        bool isApproachSource)
    {
        internal GameLocationCharacter Enemy { get; } = enemy;
        internal float Distance { get; } = distance;
        internal bool MeleeReachable { get; } = meleeReachable;
        internal bool UnreachableFlyingForMelee { get; } = unreachableFlyingForMelee;
        internal bool ExposesActorToMeleeThreat { get; } = exposesActorToMeleeThreat;
        internal bool RangedAttackAvailableFromPosition { get; } = rangedAttackAvailableFromPosition;
        internal bool SpellAttackAvailableFromPosition { get; } = spellAttackAvailableFromPosition;
        internal CoverType RangedCoverType { get; } = rangedCoverType;
        internal bool EnemyCanRangedAttackActorFromPosition { get; } = enemyCanRangedAttackActorFromPosition;
        internal CoverType ActorCoverFromEnemyRangedAttack { get; } = actorCoverFromEnemyRangedAttack;
        internal bool KnownRangedOrCasterThreat { get; } = knownRangedOrCasterThreat;
        internal bool ActorHasUsefulCover =>
            EnemyCanRangedAttackActorFromPosition && ActorCoverFromEnemyRangedAttack >= CoverType.Half;
        internal bool CanAttackFromPosition =>
            MeleeReachable || RangedAttackAvailableFromPosition || SpellAttackAvailableFromPosition;
        internal bool IsWounded { get; } = isWounded;
        internal bool IsConcentrating { get; } = isConcentrating;
        internal bool IsApproachSource { get; } = isApproachSource;
    }

    private readonly struct FreeJumpPositionFacts(
        bool hasPerceivedEnemy,
        float nearestEnemyDistance,
        int meleeThreatCount,
        int coveredRangedThreatCount,
        bool canAttack,
        bool canMeleeAttack,
        bool canRangedAttack,
        bool canSpellAttack)
    {
        internal bool HasPerceivedEnemy { get; } = hasPerceivedEnemy;
        internal float NearestEnemyDistance { get; } = nearestEnemyDistance;
        internal int MeleeThreatCount { get; } = meleeThreatCount;
        internal int CoveredRangedThreatCount { get; } = coveredRangedThreatCount;
        internal bool CanAttack { get; } = canAttack;
        internal bool CanMeleeAttack { get; } = canMeleeAttack;
        internal bool CanRangedAttack { get; } = canRangedAttack;
        internal bool CanSpellAttack { get; } = canSpellAttack;
    }

    private readonly struct CombatAiActionProbe(
        CombatAiActionKind preferredAction,
        CombatAiActionKind backupAction,
        GameLocationCharacter target,
        bool canUsePreferredAction,
        bool canUseBackupAction,
        bool hasAtWillHostileSpell,
        CombatAiCapabilityCatalog capabilityCatalog)
    {
        internal CombatAiActionKind PreferredAction { get; } = preferredAction;
        internal CombatAiActionKind BackupAction { get; } = backupAction;
        internal GameLocationCharacter Target { get; } = target;
        internal bool CanUsePreferredAction { get; } = canUsePreferredAction;
        internal bool CanUseBackupAction { get; } = canUseBackupAction;
        internal bool HasAtWillHostileSpell { get; } = hasAtWillHostileSpell;
        internal CombatAiCapabilityCatalog CapabilityCatalog { get; } = capabilityCatalog;
    }

    private readonly struct CombatAiCapabilityCatalog(
        bool hasMelee,
        bool hasThrownRanged,
        bool hasTrueRanged,
        float trueRangedMaximumRange,
        bool hasAtWillHostileSpell,
        int atWillHostileSpellCount,
        float atWillHostileSpellMaximumRange,
        bool hasFlight)
    {
        internal bool HasMelee { get; } = hasMelee;
        internal bool HasThrownRanged { get; } = hasThrownRanged;
        internal bool HasTrueRanged { get; } = hasTrueRanged;
        internal float TrueRangedMaximumRange { get; } = trueRangedMaximumRange;
        internal bool HasAtWillHostileSpell { get; } = hasAtWillHostileSpell;
        internal int AtWillHostileSpellCount { get; } = atWillHostileSpellCount;
        internal float AtWillHostileSpellMaximumRange { get; } = atWillHostileSpellMaximumRange;
        internal bool HasFlight { get; } = hasFlight;
        internal bool HasAnyRanged => HasThrownRanged || HasTrueRanged;
    }

    private readonly struct CombatAiSpellCapabilitySummary(
        int count,
        float maximumRange)
    {
        internal int Count { get; } = count;
        internal float MaximumRange { get; } = maximumRange;
    }

    private readonly struct CombatAiMovementPlan(
        CombatAiMovementGoalKind goal,
        CombatAiMovementPolicyKind policy,
        GameLocationCharacter target,
        int3 targetPosition,
        CombatAiMovementPlanReasonKind reasonKind = CombatAiMovementPlanReasonKind.None,
        ulong allyGuid = 0)
    {
        internal CombatAiMovementGoalKind Goal { get; } = goal;
        internal CombatAiMovementPolicyKind Policy { get; } = policy;
        internal GameLocationCharacter Target { get; } = target;
        internal int3 TargetPosition { get; } = targetPosition;
        internal CombatAiMovementPlanReasonKind ReasonKind { get; } = reasonKind;
        internal ulong AllyGuid { get; } = allyGuid;
        internal bool HasGoal => Goal != CombatAiMovementGoalKind.None;
    }

    private readonly struct CombatAiTurnPlan(
        CombatAiActionProbe actionProbe,
        CombatAiMovementPlan movementPlan,
        bool isAttackContinuation = false)
    {
        internal CombatAiActionProbe ActionProbe { get; } = actionProbe;
        internal CombatAiMovementPlan MovementPlan { get; } = movementPlan;
        internal bool IsAttackContinuation { get; } = isAttackContinuation;
    }

    private readonly struct CombatAiActionEconomySnapshot(
        ActionStatus mainActionType,
        ActionStatus attackMain,
        ActionStatus castMain,
        ActionStatus ready,
        ActionStatus dodge,
        ActionStatus tacticalMove,
        ActionStatus bonus,
        ActionStatus bonusFreeJump,
        ActionStatus noCostUtility,
        bool hasLastMainAction,
        bool hasLastAction,
        CombatAiExecutedActionKind lastMainTerminalAction,
        CombatAiExecutedActionKind lastTerminalAction,
        int mainUseCount,
        int bonusUseCount,
        bool mainAvailable,
        bool terminalMainAvailable,
        bool canAutoAct,
        bool isAiControlled)
    {
        internal ActionStatus MainActionType { get; } = mainActionType;
        internal ActionStatus AttackMain { get; } = attackMain;
        internal ActionStatus CastMain { get; } = castMain;
        internal ActionStatus Ready { get; } = ready;
        internal ActionStatus Dodge { get; } = dodge;
        internal ActionStatus TacticalMove { get; } = tacticalMove;
        internal ActionStatus Bonus { get; } = bonus;
        internal ActionStatus BonusFreeJump { get; } = bonusFreeJump;
        internal ActionStatus NoCostUtility { get; } = noCostUtility;
        internal bool CanAutoAct { get; } = canAutoAct;
        internal bool IsAiControlled { get; } = isAiControlled;
        internal bool HasLastMainAction { get; } = hasLastMainAction;
        internal bool HasLastAction { get; } = hasLastAction;
        internal CombatAiExecutedActionKind LastMainTerminalAction { get; } = lastMainTerminalAction;
        internal CombatAiExecutedActionKind LastTerminalAction { get; } = lastTerminalAction;
        internal int MainUseCount { get; } = mainUseCount;
        internal int BonusUseCount { get; } = bonusUseCount;
        internal bool MainAvailable { get; } = mainAvailable;
        internal bool TerminalMainAvailable { get; } = terminalMainAvailable;
        internal bool ReadyAvailable => TerminalMainAvailable && Ready == ActionStatus.Available;
        internal bool DodgeAvailable => TerminalMainAvailable && Dodge == ActionStatus.Available;
        internal CombatAiExecutedActionKind RecordedTerminalAction =>
            LastMainTerminalAction != CombatAiExecutedActionKind.None
                ? LastMainTerminalAction
                : LastTerminalAction;
    }

    private readonly struct CombatAiResidualHostileActionResult(
        CombatAiResidualHostileActionResultKind kind)
    {
        private CombatAiResidualHostileActionResultKind Kind { get; } = kind;
        internal bool Executed => Kind == CombatAiResidualHostileActionResultKind.Executed;
        internal bool PolicyHeld => Kind == CombatAiResidualHostileActionResultKind.PolicyHeld;
        internal bool IsBlocked => Kind == CombatAiResidualHostileActionResultKind.Blocked;
    }

    private readonly struct CombatAiActionExecutionMemory(
        Id actionId,
        ActionType actionType,
        ActionStatus mainBefore,
        int actionRank,
        int round,
        int turnStamp)
    {
        internal Id ActionId { get; } = actionId;
        internal ActionType ActionType { get; } = actionType;
        internal ActionStatus MainBefore { get; } = mainBefore;
        internal int ActionRank { get; } = actionRank;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal bool IsMainAction => ActionType == ActionType.Main;
    }

    private readonly struct AttackPositionKey(
        ulong attackerGuid,
        int3 attackerPosition,
        ulong targetGuid,
        int3 targetPosition,
        int actionStateSignature,
        int targetStateSignature) : IEquatable<AttackPositionKey>
    {
        private ulong AttackerGuid { get; } = attackerGuid;
        private int3 AttackerPosition { get; } = attackerPosition;
        private ulong TargetGuid { get; } = targetGuid;
        private int3 TargetPosition { get; } = targetPosition;
        private int ActionStateSignature { get; } = actionStateSignature;
        private int TargetStateSignature { get; } = targetStateSignature;

        public bool Equals(AttackPositionKey other)
        {
            return AttackerGuid == other.AttackerGuid &&
                   AttackerPosition == other.AttackerPosition &&
                   TargetGuid == other.TargetGuid &&
                   TargetPosition == other.TargetPosition &&
                   ActionStateSignature == other.ActionStateSignature &&
                   TargetStateSignature == other.TargetStateSignature;
        }

        public override bool Equals(object obj)
        {
            return obj is AttackPositionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)AttackerGuid;

                hash = (hash * 397) ^ AttackerPosition.x;
                hash = (hash * 397) ^ AttackerPosition.y;
                hash = (hash * 397) ^ AttackerPosition.z;
                hash = (hash * 397) ^ (int)TargetGuid;
                hash = (hash * 397) ^ TargetPosition.x;
                hash = (hash * 397) ^ TargetPosition.y;
                hash = (hash * 397) ^ TargetPosition.z;
                hash = (hash * 397) ^ ActionStateSignature;
                hash = (hash * 397) ^ TargetStateSignature;

                return hash;
            }
        }
    }

    internal static bool IsAdvancedCombatAiEnabled =>
        Main.Settings.EnableAdvancedCombatAI;

    internal static bool IsAdvancedCombatAiFlightEnabled =>
        IsAdvancedCombatAiEnabled;

    internal static bool IsAdvancedCombatAiPositioningEnabled =>
        IsAdvancedCombatAiEnabled;

    internal static bool IsAdvancedCombatAiActionEconomyEnabled =>
        IsAdvancedCombatAiEnabled;

    internal static bool IsAdvancedCombatAiProfilesEnabled =>
        IsAdvancedCombatAiEnabled;

    internal static bool ShouldOverrideEnemyProximityScore(
        ConsiderationDescription consideration,
        DecisionParameters parameters)
    {
        EnsureCombatAiRuntimeCache();

        if (IsAdvancedCombatAiEnabled)
        {
            return true;
        }

        var rulesetCharacter = parameters?.character?.GameLocationCharacter?.RulesetCharacter;

        return rulesetCharacter != null && GetApproachSourceGuid(rulesetCharacter, consideration?.StringParameter) != 0;
    }

    internal static void NotifyBattleStarted(GameLocationCharacter character)
    {
        EnsureCombatAiRuntimeCache();
    }

    internal static void ResetActionLedgerForTurn(GameLocationCharacter character)
    {
        ClearTurnState(character);
    }

    private readonly struct RouteCandidateCacheKey(
        ulong actorGuid,
        int3 actorPosition,
        ulong targetGuid,
        int3 targetPosition,
        CombatAiActionKind actionKind,
        int actionStateSignature,
        int targetStateSignature) : IEquatable<RouteCandidateCacheKey>
    {
        private ulong ActorGuid { get; } = actorGuid;
        private int3 ActorPosition { get; } = actorPosition;
        private ulong TargetGuid { get; } = targetGuid;
        private int3 TargetPosition { get; } = targetPosition;
        private CombatAiActionKind ActionKind { get; } = actionKind;
        private int ActionStateSignature { get; } = actionStateSignature;
        private int TargetStateSignature { get; } = targetStateSignature;

        public bool Equals(RouteCandidateCacheKey other)
        {
            return ActorGuid == other.ActorGuid &&
                   ActorPosition == other.ActorPosition &&
                   TargetGuid == other.TargetGuid &&
                   TargetPosition == other.TargetPosition &&
                   ActionKind == other.ActionKind &&
                   ActionStateSignature == other.ActionStateSignature &&
                   TargetStateSignature == other.TargetStateSignature;
        }

        public override bool Equals(object obj)
        {
            return obj is RouteCandidateCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)ActorGuid;

                hash = (hash * 397) ^ ActorPosition.x;
                hash = (hash * 397) ^ ActorPosition.y;
                hash = (hash * 397) ^ ActorPosition.z;
                hash = (hash * 397) ^ (int)TargetGuid;
                hash = (hash * 397) ^ TargetPosition.x;
                hash = (hash * 397) ^ TargetPosition.y;
                hash = (hash * 397) ^ TargetPosition.z;
                hash = (hash * 397) ^ (int)ActionKind;
                hash = (hash * 397) ^ ActionStateSignature;
                hash = (hash * 397) ^ TargetStateSignature;

                return hash;
            }
        }
    }

    private readonly struct ReachableRouteCacheKey(
        ulong actorGuid,
        int3 start,
        int remainingMove,
        int round,
        int turnStamp,
        bool walkOnly) : IEquatable<ReachableRouteCacheKey>
    {
        internal ulong ActorGuid { get; } = actorGuid;
        private int3 Start { get; } = start;
        private int RemainingMove { get; } = remainingMove;
        private int Round { get; } = round;
        private int TurnStamp { get; } = turnStamp;
        private bool WalkOnly { get; } = walkOnly;

        public bool Equals(ReachableRouteCacheKey other)
        {
            return ActorGuid == other.ActorGuid &&
                   Start == other.Start &&
                   RemainingMove == other.RemainingMove &&
                   Round == other.Round &&
                   TurnStamp == other.TurnStamp &&
                   WalkOnly == other.WalkOnly;
        }

        public override bool Equals(object obj)
        {
            return obj is ReachableRouteCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)ActorGuid;

                hash = (hash * 397) ^ Start.x;
                hash = (hash * 397) ^ Start.y;
                hash = (hash * 397) ^ Start.z;
                hash = (hash * 397) ^ RemainingMove;
                hash = (hash * 397) ^ Round;
                hash = (hash * 397) ^ TurnStamp;
                hash = (hash * 397) ^ WalkOnly.GetHashCode();

                return hash;
            }
        }
    }

    private readonly struct ActorTurnKey(
        ulong actorGuid,
        int round,
        int turnStamp) : IEquatable<ActorTurnKey>
    {
        internal ulong ActorGuid { get; } = actorGuid;
        private int Round { get; } = round;
        private int TurnStamp { get; } = turnStamp;

        public bool Equals(ActorTurnKey other)
        {
            return ActorGuid == other.ActorGuid && Round == other.Round && TurnStamp == other.TurnStamp;
        }

        public override bool Equals(object obj)
        {
            return obj is ActorTurnKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)ActorGuid;

                hash = (hash * 397) ^ Round;
                hash = (hash * 397) ^ TurnStamp;

                return hash;
            }
        }
    }

    internal static void PrimeTurnCache(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        EnsureCombatAiRuntimeCache();

        if (IsAdvancedCombatAiEnabled)
        {
            ObservedCombatMemoryTurnStamp++;
            InvalidateTurnPlanningCache(character);

            if (!IsAiControlledForCombat(character) ||
                !CanExecuteAutomaticCombatAction(character))
            {
                return;
            }

            var profile = BuildProfile(character);
            var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

            if (battleService != null)
            {
                var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

                PrimeTurnMovementProgress(character, turnPlan);
                PrimeGroundMeleeTargetContactRouteQuery(character, turnPlan);
            }
        }

    }

    internal static void ClearTurnCache(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        ClearTurnState(character);
        InvalidateTurnPlanningCache(character);

        if (character.RulesetCharacter != null)
        {
            PersonalityFlagsCache.Remove(character.RulesetCharacter.Guid);
        }
    }

    private static void InvalidateTurnPlanningCache(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        ProfileCache.Remove(character.Guid);
        MeleeAttackPositionCache.Clear();
        ActionKindPositionCache.Clear();
        CoverEvaluationCache.Clear();
        JumpImmediateAttackReachableCache.Remove(character.Guid);
        GroundMeleeJumpRouteAvailableCache.Remove(character.Guid);
        CurrentStateRouteBlockCache.Remove(character.Guid);
        TargetContactRouteQueryCache.Remove(character.Guid);
        TurnMovementProgressCache.Remove(character.Guid);
        ClearReachableRouteDestinationCache(character);
    }

    internal static void ClearBattleMemory(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        ClearTurnState(character);
        InvalidateTurnPlanningCache(character);
        ObservedCombatMemoryCache.Remove(character.Guid);
        PendingFallbackDodgeConditionCache.Remove(character.Guid);
        FallbackDodgeConditionCache.Remove(character.Guid);
        RecentMeleeThreatMemoryCache.Remove(character.Guid);
        ThreatAvoidanceMemoryCache.Remove(character.Guid);

        if (character.RulesetCharacter != null)
        {
            PersonalityFlagsCache.Remove(character.RulesetCharacter.Guid);
        }
    }

    private static void EnsureCombatAiRuntimeCache()
    {
        var battle = Gui.Battle;
        var round = GetCurrentBattleRound();
        var advancedCombatAiEnabled = Main.Settings.EnableAdvancedCombatAI;
        var bonusActionFreeJumpEnabled = Main.Settings.EnableBonusActionFreeJump;
        var resetRequired = false;

        if (battle == null)
        {
            if (CurrentBattleCacheKey != null)
            {
                resetRequired = true;
            }
        }
        else if (!ReferenceEquals(CurrentBattleCacheKey, battle))
        {
            resetRequired = true;
        }
        else if (CurrentBattleCacheRound >= 0 && round >= 0 && round < CurrentBattleCacheRound)
        {
            resetRequired = true;
        }
        else if (CurrentAdvancedCombatAiSetting.HasValue &&
                 (CurrentAdvancedCombatAiSetting.Value != advancedCombatAiEnabled ||
                  CurrentBonusActionFreeJumpSetting.GetValueOrDefault() != bonusActionFreeJumpEnabled))
        {
            resetRequired = true;
        }

        if (!resetRequired)
        {
            if (battle != null)
            {
                CurrentBattleCacheRound = round;
            }

            CurrentAdvancedCombatAiSetting = advancedCombatAiEnabled;
            CurrentBonusActionFreeJumpSetting = bonusActionFreeJumpEnabled;

            return;
        }

        ClearRuntimeCaches();
        CurrentBattleCacheKey = battle;
        CurrentBattleCacheRound = battle == null ? -1 : round;
        CurrentAdvancedCombatAiSetting = advancedCombatAiEnabled;
        CurrentBonusActionFreeJumpSetting = bonusActionFreeJumpEnabled;
    }

    private static void ClearRuntimeCaches()
    {
        ProfileCache.Clear();
        PersonalityFlagsCache.Clear();
        ObservedCombatMemoryCache.Clear();
        AiMoveFailureCache.Clear();
        PendingAiMoveAttemptCache.Clear();
        PostMainClearAllyCorridorAttemptCache.Clear();
        ActionLinkedMoveCache.Clear();
        ActionLinkedMoveSettlingCache.Clear();
        CompletedAiMoveStepCache.Clear();
        RouteMoveCompletionClosedCache.Clear();

        LostTargetSearchAttemptCache.Clear();
        PendingRouteMovementLockCache.Clear();
        ProxyThreatRouteAttemptCache.Clear();
        PreMainRouteMoveAttemptCache.Clear();
        DisconnectedPositioningSealCache.Clear();
        DisconnectedPositioningMovementLockCache.Clear();
        DisconnectedSearchMoveCompletionSealCache.Clear();
        DisconnectedSearchNoRouteMovementSealCache.Clear();
        PendingFallbackDodgeConditionCache.Clear();

        FallbackDodgeConditionCache.Clear();
        RepeatAttackActionCache.Clear();
        TurnMovementProgressCache.Clear();
        LastActionExecutionCache.Clear();
        LastMainActionExecutionCache.Clear();
        TurnMainActionUseCountCache.Clear();
        TurnBonusActionUseCountCache.Clear();

        RecentMeleeThreatMemoryCache.Clear();
        ThreatAvoidanceMemoryCache.Clear();
        PendingResidualMainActionCache.Clear();

        BaselineFreeJumpAttemptCache.Clear();
        MeleeAttackPositionCache.Clear();
        ActionKindPositionCache.Clear();
        CoverEvaluationCache.Clear();
        JumpImmediateAttackReachableCache.Clear();
        GroundMeleeJumpRouteAvailableCache.Clear();
        CurrentStateRouteBlockCache.Clear();
        TargetContactRouteQueryCache.Clear();
        GroundMeleeRouteFailureCache.Clear();
        GroundMeleeMoveSettlingCache.Clear();
        GroundMeleePartialRouteCache.Clear();

        ReachableRouteDestinationCache.Clear();
        ReachableRoutePathfindingCountCache.Clear();
        FreeJumpContext.ClearPendingAiFreeJumpCompletions();
        ObservedCombatMemoryTurnStamp = 0;
    }

    internal static IEnumerator HandleAiForcedMotionCompleted(CharacterAction action)
    {
        if (action?.ActingCharacter?.RulesetCharacter == null)
        {
            yield break;
        }

        ClearForcedMotionPositionDependentAiState(action.ActingCharacter);

        yield break;
    }

    private static bool ClearForcedMotionPositionDependentAiState(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character))
        {
            return false;
        }

        var guid = character.Guid;
        var groundMeleeRouteKey = GetGroundMeleeRouteMemoryKey(character);
        var removed = false;

        removed |= PendingAiMoveAttemptCache.Remove(guid);
        removed |= ActionLinkedMoveCache.Remove(guid);
        removed |= ActionLinkedMoveSettlingCache.Remove(guid);

        removed |= PendingRouteMovementLockCache.Remove(guid);
        removed |= RouteMoveCompletionClosedCache.Remove(guid);
        removed |= TurnMovementProgressCache.Remove(guid);
        removed |= ProxyThreatRouteAttemptCache.Remove(guid);
        removed |= PreMainRouteMoveAttemptCache.Remove(guid);
        removed |= DisconnectedPositioningMovementLockCache.Remove(guid);
        removed |= DisconnectedSearchMoveCompletionSealCache.Remove(guid);
        removed |= JumpImmediateAttackReachableCache.Remove(guid);
        removed |= GroundMeleeJumpRouteAvailableCache.Remove(guid);
        removed |= CurrentStateRouteBlockCache.Remove(guid);
        removed |= TargetContactRouteQueryCache.Remove(guid);
        removed |= GroundMeleeMoveSettlingCache.Remove(guid);
        removed |= GroundMeleePartialRouteCache.Remove(guid);

        removed |= GroundMeleeRouteFailureCache.Remove(groundMeleeRouteKey);
        removed |= FreeJumpContext.ClearAiMoveTargetState(character);

        ClearReachableRouteDestinationCache(character);

        if (removed)
        {
            MeleeAttackPositionCache.Clear();
            ActionKindPositionCache.Clear();
        }

        return removed;
    }

    private static bool HasAvailableAttackMainContinuation(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsActiveBattleContender(character) ||
            character.GetActionStatus(Id.AttackMain, ActionScope.Battle) != ActionStatus.Available ||
            character.GetActionAvailableIterations(Id.AttackMain) <= 0)
        {
            return false;
        }

        if (!TryGetMainAttackBudgetSnapshot(
                character,
                out var rank,
                out var usedMainAttacks,
                out var allowedMainAttacks,
                out _))
        {
            return HasAvailableAttackModeForMainAction(character);
        }

        if (TryGetCurrentTurnActionMemory(character, LastMainActionExecutionCache, out var lastMainAction) &&
            lastMainAction.ActionId == Id.AttackMain &&
            lastMainAction.ActionRank == rank)
        {
            usedMainAttacks = Math.Max(
                usedMainAttacks,
                GetActionUseCount(TurnMainActionUseCountCache, character));
        }

        return (allowedMainAttacks <= 0 || usedMainAttacks < allowedMainAttacks) &&
               HasAvailableAttackModeForMainAction(character);
    }

    private static bool HasRemainingAttackMainContinuation(GameLocationCharacter character)
    {
        return HasPendingAttackMainIteration(character) &&
               HasAvailableAttackMainContinuation(character);
    }

    private static bool HasPendingAttackMainIteration(GameLocationCharacter character)
    {
        return TryGetCurrentTurnActionMemory(character, LastMainActionExecutionCache, out var lastMainAction) &&
               lastMainAction.ActionId == Id.AttackMain &&
               character.GetActionStatus(Id.AttackMain, ActionScope.Battle) == ActionStatus.Available &&
               character.GetActionAvailableIterations(Id.AttackMain) > 0;
    }

    private static bool TryGetMainAttackBudgetSnapshot(
        GameLocationCharacter character,
        out int rank,
        out int usedMainAttacks,
        out int allowedMainAttacks,
        out int iterations)
    {
        rank = -1;
        usedMainAttacks = -1;
        allowedMainAttacks = -1;
        iterations = -1;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        rank = character.CurrentActionRankByType[ActionType.Main];
        usedMainAttacks = character.UsedMainAttacks;
        allowedMainAttacks = character.GetAllowedMainAttacksForRank(rank);
        iterations = character.GetActionAvailableIterations(Id.AttackMain);

        return allowedMainAttacks > 0 || iterations > 0;
    }

    private static bool TryClearInactiveCombatAiTurnOwnership(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null || IsActiveBattleContender(character))
        {
            return false;
        }

        if (MovementTracker.TryGetMovement(character.Guid, out _) ||
            HasPendingReactionRequests())
        {
            return false;
        }

        if (!HasPendingCombatAiTurnOwnershipState(character))
        {
            return false;
        }

        ClearTurnState(character);
        InvalidateTurnPlanningCache(character);

        return true;
    }

    private static void ClearTurnState(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        var guid = character.Guid;

        AiMoveFailureCache.Remove(guid);
        PendingAiMoveAttemptCache.Remove(guid);
        PostMainClearAllyCorridorAttemptCache.Remove(guid);
        ActionLinkedMoveCache.Remove(guid);
        ActionLinkedMoveSettlingCache.Remove(guid);
        CompletedAiMoveStepCache.Remove(guid);
        RouteMoveCompletionClosedCache.Remove(guid);
        LostTargetSearchAttemptCache.Remove(guid);
        PendingRouteMovementLockCache.Remove(guid);
        ProxyThreatRouteAttemptCache.Remove(guid);
        PreMainRouteMoveAttemptCache.Remove(guid);
        DisconnectedPositioningSealCache.Remove(guid);
        DisconnectedPositioningMovementLockCache.Remove(guid);
        DisconnectedSearchMoveCompletionSealCache.Remove(guid);
        DisconnectedSearchNoRouteMovementSealCache.Remove(guid);
        PendingFallbackDodgeConditionCache.Remove(guid);
        RepeatAttackActionCache.Remove(guid);
        TurnMovementProgressCache.Remove(guid);
        LastActionExecutionCache.Remove(guid);
        LastMainActionExecutionCache.Remove(guid);
        TurnMainActionUseCountCache.Remove(guid);
        TurnBonusActionUseCountCache.Remove(guid);
        PendingResidualMainActionCache.Remove(guid);
        BaselineFreeJumpAttemptCache.Remove(guid);
        JumpImmediateAttackReachableCache.Remove(guid);
        GroundMeleeJumpRouteAvailableCache.Remove(guid);
        CurrentStateRouteBlockCache.Remove(guid);
        TargetContactRouteQueryCache.Remove(guid);
        GroundMeleeRouteFailureCache.Remove(GetGroundMeleeRouteMemoryKey(character));
        GroundMeleeMoveSettlingCache.Remove(guid);
        GroundMeleePartialRouteCache.Remove(guid);
        ClearReachableRouteDestinationCache(character);
        FreeJumpContext.ClearAiMoveTargetState(character);
        MovementTracker.ClearMovement(character);
    }

    private static bool HasPendingCombatAiTurnOwnershipState(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var guid = character.Guid;

        return PendingAiMoveAttemptCache.ContainsKey(guid) ||
               ActionLinkedMoveCache.ContainsKey(guid) ||
               ActionLinkedMoveSettlingCache.ContainsKey(guid) ||
               GroundMeleeMoveSettlingCache.ContainsKey(guid) ||
               DisconnectedSearchMoveCompletionSealCache.ContainsKey(guid) ||
               DisconnectedSearchNoRouteMovementSealCache.ContainsKey(guid) ||
               PendingRouteMovementLockCache.ContainsKey(guid) ||
               RouteMoveCompletionClosedCache.ContainsKey(guid);
    }

    private static bool HasPendingReactionRequests()
    {
        if (ServiceRepository.GetService<IGameLocationActionService>() is not GameLocationActionManager actionManager ||
            actionManager.pendingReactionRequestGroups == null)
        {
            return false;
        }

        return actionManager.pendingReactionRequestGroups.Count > 0;
    }

    internal static void TryCompletePendingActionLinkedMove(CharacterAction action)
    {
        if (action?.ActionId is not Id.TacticalMove)
        {
            return;
        }

        if (!IsActiveBattleContender(action.ActingCharacter))
        {
            ClearPendingMoveOwnership(action.ActingCharacter);
            return;
        }

        TryCompleteActionLinkedMove(action.ActingCharacter);
    }

    internal static void OnAiTacticalMoveActionChainExecuted(
        GameLocationCharacter character,
        bool aborted,
        CombatAiRouteMoveSourceKind routeMoveSource,
        int moveToken = 0)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var pendingAction = default(ActionLinkedMoveMemory);

        if (moveToken != 0 &&
            !IsCurrentActionLinkedMoveToken(character, routeMoveSource, moveToken, out pendingAction))
        {
            return;
        }

        if (moveToken != 0 &&
            (!IsActiveBattleContender(character) ||
             pendingAction.Round != GetCurrentBattleRound() ||
             pendingAction.TurnStamp != Math.Max(1, ObservedCombatMemoryTurnStamp)))
        {
            return;
        }

        TryMarkActionLinkedMoveCallbackObserved(character, routeMoveSource, moveToken);

        if (routeMoveSource == CombatAiRouteMoveSourceKind.JumpImmediateAttack)
        {
            TryCompletePendingJumpImmediateAttackActionChainSettled(
                character,
                aborted,
                callbackObserved: true);

            return;
        }

        TryCompleteActionLinkedMove(
            character,
            allowSettlingCompletion: true,
            allowSettledNoMoveFinalization: true);

        if (TryResolveGroundMeleeMoveSettlingAfterActionChain(character, aborted))
        {
            return;
        }

    }

    internal static void NotifyAiMoveStepCompleted(
        GameLocationCharacter character,
        int3 from,
        int3 to)
    {
        if (character?.RulesetCharacter == null ||
            !IsAiControlledForCombat(character) ||
            from == int3.invalid ||
            to == int3.invalid ||
            from == to)
        {
            return;
        }

        if (!TryGetPendingActionLinkedMoveForCompletion(character, out var pendingAction, out _))
        {
            return;
        }

        CompletedAiMoveStepCache[character.Guid] = new CompletedAiMoveStepMemory(
            from,
            to,
            pendingAction.RouteMoveSource,
            pendingAction.MoveToken,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        TryCompleteConnectedFiringLineMovementStep(character);
        TryCompleteSearchKnownTargetMovementStep(character);
    }

    private static bool CanIssueAdditionalTacticalMove(GameLocationCharacter character)
    {
        return character?.RulesetCharacter != null &&
               character.RemainingTacticalMoves > 0 &&
               character.CanDecideToMoveByItself &&
               character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) == ActionStatus.Available;
    }

    private static ConnectedFiringLineCompletionResult TryCompleteConnectedFiringLineMovementStep(
        GameLocationCharacter character,
        bool includeSettling = false)
    {
        if (character?.RulesetCharacter == null)
        {
            return new ConnectedFiringLineCompletionResult(
                ConnectedFiringLineCompletionKind.Unavailable,
                character?.LocationPosition ?? default);
        }

        var hasPendingAction =
            TryGetLiveActionLinkedMoveForCompletion(character, out var pendingAction) ||
            includeSettling &&
            TryGetSettledActionLinkedMoveForCompletion(character, out pendingAction);

        if (!hasPendingAction || !IsConnectedFiringLineRoute(pendingAction))
        {
            return new ConnectedFiringLineCompletionResult(
                ConnectedFiringLineCompletionKind.Unavailable,
                character?.LocationPosition ?? default);
        }

        var actualDestination = character.LocationPosition;

        if (actualDestination == pendingAction.StartPosition)
        {
            return new ConnectedFiringLineCompletionResult(
                ConnectedFiringLineCompletionKind.Pending,
                actualDestination);
        }

        CloseConnectedFiringLineMoveResult(
            character,
            pendingAction,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination);

        return new ConnectedFiringLineCompletionResult(
            actualDestination == pendingAction.ExpectedDestination
                ? ConnectedFiringLineCompletionKind.SettledReached
                : ConnectedFiringLineCompletionKind.SettledPartial,
            actualDestination);
    }

    private static SearchKnownTargetCompletionResult TryCompleteSearchKnownTargetMovementStep(
        GameLocationCharacter character,
        bool includeSettling = false,
        bool allowProgressOnlyPartialContinuation = true)
    {
        if (character?.RulesetCharacter == null)
        {
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Unavailable,
                default);
        }

        var hasPendingAction =
            TryGetLiveActionLinkedMoveForCompletion(character, out var pendingAction) ||
            includeSettling &&
            TryGetSettledActionLinkedMoveForCompletion(character, out pendingAction);

        if (!hasPendingAction || !IsSearchKnownTargetRoute(pendingAction))
        {
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Unavailable,
                default);
        }

        if (ShouldWaitForActionLinkedMoveCallback(character, pendingAction))
        {
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Pending,
                ComputeSearchKnownTargetProgress(character, pendingAction, character.LocationPosition));
        }

        var actualDestination = character.LocationPosition;
        var progress = ComputeSearchKnownTargetProgress(character, pendingAction, actualDestination);

        if (actualDestination == pendingAction.StartPosition)
        {
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Pending,
                progress);
        }

        var hasMeaningfulProgress = progress.HasMeaningfulProgress;
        var hasValidatedAction = false;

        if (!hasMeaningfulProgress)
        {
            hasValidatedAction =
                pendingAction.SearchRouteActionConnected ||
                HasSearchKnownTargetValidatedActionAtDestination(character, pendingAction, actualDestination) ||
                HasSearchKnownTargetValidatedAction(character);
        }

        if (!hasMeaningfulProgress && !hasValidatedAction)
        {
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Pending,
                progress);
        }

        var result = actualDestination == pendingAction.ExpectedDestination
            ? SearchKnownTargetCompletionKind.SettledReached
            : SearchKnownTargetCompletionKind.SettledPartial;

        if (result == SearchKnownTargetCompletionKind.SettledPartial &&
            allowProgressOnlyPartialContinuation &&
            TryDeferProgressOnlySearchPartialMove(
                character,
                pendingAction,
                pendingAction.StartPosition,
                pendingAction.ExpectedDestination,
                actualDestination))
        {
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Pending,
                progress);
        }

        CloseSearchKnownTargetMoveResult(
            character,
            pendingAction,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            result);

        return new SearchKnownTargetCompletionResult(result, progress);
    }

    private static bool HasPendingSearchKnownTargetMovement(GameLocationCharacter character)
    {
        return character?.RulesetCharacter != null &&
               (TryGetPendingActionLinkedMoveForCompletion(character, out var pendingAction, out _) &&
                IsSearchKnownTargetRoute(pendingAction));
    }

    private static SearchKnownTargetProgress ComputeSearchKnownTargetProgress(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination)
    {
        var anchor = pendingAction.ExpectedDestination;

        if (TryGetSearchKnownTargetAnchor(
                character,
                pendingAction,
                out var foundAnchor,
                out _))
        {
            anchor = foundAnchor;
        }

        var startDistance = ComputeGridDistance(pendingAction.StartPosition, anchor);
        var actualDistance = ComputeGridDistance(actualDestination, anchor);
        var minimumProgress = ComputeMinimumSearchKnownTargetProgress(startDistance);

        return new SearchKnownTargetProgress(anchor, startDistance, actualDistance, minimumProgress);
    }

    private static float ComputeMinimumSearchKnownTargetProgress(float startDistance)
    {
        if (startDistance <= 1f)
        {
            return 0.75f;
        }

        return Math.Min(2f, Math.Max(0.75f, startDistance - 1f));
    }

    private static void RecordLostTargetSearchAttempt(
        GameLocationCharacter character,
        int round,
        int turnStamp,
        int3 start,
        int3 anchor)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        LostTargetSearchAttemptCache[character.Guid] = new LostTargetSearchAttemptMemory(
            round,
            turnStamp,
            start,
            anchor);
    }

    private static void RecordLostTargetSearchAttempt(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var anchor = pendingAction.ExpectedDestination;

        if (TryGetSearchKnownTargetAnchor(
                character,
                pendingAction,
                out var foundAnchor,
                out _))
        {
            anchor = foundAnchor;
        }

        RecordLostTargetSearchAttempt(
            character,
            pendingAction.Round,
            pendingAction.TurnStamp,
            pendingAction.StartPosition,
            anchor);
    }

    private static bool HasSearchKnownTargetValidatedAction(
        GameLocationCharacter character)
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);
        var currentActionScan = BuildCurrentTerminalActionScan(
            character,
            turnPlan.ActionProbe,
            battleService,
            BuildSelfAssessment(character));

        return currentActionScan.HasValidatedAction;
    }

    private static bool HasSearchKnownTargetValidatedActionAtDestination(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 destination)
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);
        var target = pendingAction.Target ?? turnPlan.ActionProbe.Target;

        if (target?.RulesetCharacter == null)
        {
            return false;
        }

        var preferred = turnPlan.ActionProbe.PreferredAction;
        var backup = turnPlan.ActionProbe.BackupAction;

        if (preferred != CombatAiActionKind.None &&
            CanUseActionKindAtPosition(character, destination, target, preferred, battleService))
        {
            return true;
        }

        if (backup != CombatAiActionKind.None &&
            CanUseActionKindAtPosition(character, destination, target, backup, battleService))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetSearchKnownTargetAnchor(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        out int3 anchor,
        out GameLocationCharacter target)
    {
        anchor = default;
        target = null;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (pendingAction.Target?.RulesetCharacter != null)
        {
            anchor = pendingAction.Target.LocationPosition;
            target = pendingAction.Target;
            return true;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var profile = BuildProfile(character);
        var actionProbe = battleService == null
            ? default
            : BuildCombatAiTurnPlan(character, profile, battleService).ActionProbe;

        return TryGetLostTargetSearchAnchor(character, profile, actionProbe, out anchor, out target);
    }

    private static ConnectedFiringLineCompletionKind GetConnectedFiringLineCompletionKindAtPosition(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 position)
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null ||
            pendingAction.Target?.RulesetCharacter == null ||
            pendingAction.ActionKind == CombatAiActionKind.None)
        {
            return ConnectedFiringLineCompletionKind.FailedNoAction;
        }

        if (!CanUseActionKindAtPosition(
                character,
                position,
                pendingAction.Target,
                pendingAction.ActionKind,
                battleService))
        {
            return ConnectedFiringLineCompletionKind.FailedNoAction;
        }

        return position == pendingAction.ExpectedDestination
            ? ConnectedFiringLineCompletionKind.SettledReached
            : ConnectedFiringLineCompletionKind.SettledPartial;
    }

    private static bool ShouldWaitForActionLinkedMoveCallback(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction)
    {
        if (character?.RulesetCharacter == null ||
            pendingAction.Continuation is not (
                CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove or
                CombatAiActionLinkedMoveContinuation.ProgressOnlySearchMove) ||
            !ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var settling) ||
            settling.PendingAction.MoveToken != pendingAction.MoveToken ||
            settling.PendingAction.RouteMoveSource != pendingAction.RouteMoveSource ||
            !settling.MatchesCurrentTurn(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp)))
        {
            return false;
        }

        return !settling.CallbackObserved && settling.ResultStart != settling.ResultTarget;
    }

    internal static bool IsFailedAiMoveTarget(GameLocationCharacter character, int3 start, int3 target)
    {
        return character != null &&
               AiMoveFailureCache.TryGetValue(character.Guid, out var failures) &&
               failures.Contains(new AiMoveFailureKey(start, target));
    }

    internal static bool IsRejectedAiMoveTarget(GameLocationCharacter character, int3 start, int3 target)
    {
        return IsFailedAiMoveTarget(character, start, target) || IsBacktrackingMove(character, start, target);
    }

    private static bool IsPendingAiMoveAttempt(GameLocationCharacter character, int3 start, int3 target)
    {
        return character != null &&
               PendingAiMoveAttemptCache.TryGetValue(character.Guid, out var pending) &&
               pending.Start == start &&
               pending.Target == target;
    }

    internal static bool ShouldCancelAiTacticalMove(
        CharacterActionMove action,
        int3 start,
        int3 target)
    {
        var character = action?.ActingCharacter;

        EnsureCombatAiRuntimeCache();

        if (!IsAiControlledForCombat(character))
        {
            return false;
        }

        // The vanilla AI owns ordinary movement. Combat AI may validate or reject only a
        // movement action it explicitly submitted and still owns in the live move cache.
        if (!ActionLinkedMoveCache.ContainsKey(character.Guid))
        {
            return false;
        }

        if (ShouldCancelDisconnectedSearchMovementSealMove(action, character))
        {
            return true;
        }

        if (TryGetSearchKnownTargetOwnedMoveGate(character, start, target, out var searchMoveAllowed))
        {
            if (searchMoveAllowed)
            {
                RecordAcceptedAiMoveAttempt(character, start, target);
                return false;
            }

            RecordAiMoveFailure(character, start, target);
            return true;
        }

        if (TryGetActiveDisconnectedPositioningSeal(character, out var seal))
        {
            bool blocked;

            if (target.x == 0 && target.y == 0 && target.z == 0)
            {
                blocked = ShouldBlockDisconnectedPositioningUnresolvedDestination(
                    character,
                    seal);
            }
            else
            {
                blocked = ShouldBlockDisconnectedPositioningDestination(
                    action,
                    character,
                    seal,
                    start,
                    target);
            }

            return blocked;
        }

        if (IsFailedAiMoveTarget(character, start, target))
        {
            return true;
        }

        if (IsPendingAiMoveAttempt(character, start, target))
        {
            RecordAiMoveFailure(character, start, target);
            return true;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService != null && HasOpportunityAttackRisk(character, start, target, battleService))
        {
            return true;
        }

        if (ShouldRejectProxyThreatReturnMove(character, start, target, out var shouldSealMovement))
        {
            RecordAiMoveFailure(character, start, target);

            if (shouldSealMovement)
            {
                ApplyPostThreatReturnSeal(character);
            }

            return true;
        }

        if (battleService != null && ShouldRejectTrafficBlockingMove(character, target, battleService))
        {
            return true;
        }

        if (IsBacktrackingMove(character, start, target))
        {
            return true;
        }

        RecordAcceptedAiMoveAttempt(character, start, target);

        return false;
    }

    private static bool ShouldCancelDisconnectedSearchMovementSealMove(
        CharacterActionMove action,
        GameLocationCharacter character)
    {
        return action?.ActionId == Id.TacticalMove &&
               character?.RulesetCharacter != null &&
               HasActiveDisconnectedSearchMoveCompletionSeal(character) &&
               !HasActiveProgressOnlySearchOwnedMove(character);
    }

    private static void RecordAcceptedAiMoveAttempt(GameLocationCharacter character, int3 start, int3 target)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PendingAiMoveAttemptCache[character.Guid] = new AiMoveAttempt(start, target);
    }

    private static bool TryValidateAiTacticalMoveIssue(
        GameLocationCharacter character,
        int3 destination,
        bool requireCanDecide = false)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            return false;
        }

        if (requireCanDecide && !character.CanDecideToMoveByItself)
        {
            return false;
        }

        if (character.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        var tacticalStatus = character.GetActionStatus(Id.TacticalMove, ActionScope.Battle);

        if (tacticalStatus != ActionStatus.Available)
        {
            return false;
        }

        if (!IsLegalAiRouteDestination(character, destination))
        {
            return false;
        }

        return true;
    }

    internal static bool TryGetActionLinkedMoveResultSettlingFrames(
        CharacterActionMove action,
        int3 start,
        int3 target,
        out int maxSettleFrames,
        out bool waitForExpectedDestination)
    {
        maxSettleFrames = 0;
        waitForExpectedDestination = false;

        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character))
        {
            return false;
        }

        var hasPendingAction = ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction);

        if (!hasPendingAction &&
            ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var actionLinkedSettling))
        {
            pendingAction = actionLinkedSettling.PendingAction;
            hasPendingAction = true;
        }

        if (!hasPendingAction)
        {
            hasPendingAction = TryGetPendingGroundMeleeMoveSettling(character, out pendingAction);
        }

        if (!hasPendingAction ||
            pendingAction.StartPosition != start ||
            pendingAction.ExpectedDestination != target)
        {
            return false;
        }

        if (IsGroundMeleePursuitTerminalRoute(pendingAction))
        {
            maxSettleFrames = MoveResultSettlingFrameLimit;
            return true;
        }

        if (IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            maxSettleFrames = JumpImmediateMoveResultSettlingFrameLimit;
            return true;
        }

        if (IsProgressOnlySearchRoute(pendingAction))
        {
            maxSettleFrames = SearchKnownTargetMoveResultSettlingFrameLimit;
            return true;
        }

        if (IsSearchKnownTargetRoute(pendingAction))
        {
            maxSettleFrames = SearchKnownTargetMoveResultSettlingFrameLimit;
            return true;
        }

        return false;
    }

    private static bool TryGetPendingGroundMeleeMoveSettling(
        GameLocationCharacter character,
        out ActionLinkedMoveMemory pendingAction)
    {
        pendingAction = default;

        if (character?.RulesetCharacter == null ||
            !GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var memory) ||
            !memory.MatchesCurrentTurn(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp)))
        {
            return false;
        }

        pendingAction = memory.PendingAction;
        return true;
    }

    private static void ClearPendingMoveOwnership(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PendingAiMoveAttemptCache.Remove(character.Guid);
        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
    }

    private static bool TryGetLiveActionLinkedMoveForCompletion(
        GameLocationCharacter character,
        out ActionLinkedMoveMemory pendingAction)
    {
        pendingAction = default;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (ActionLinkedMoveCache.TryGetValue(character.Guid, out pendingAction))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetSettledActionLinkedMoveForCompletion(
        GameLocationCharacter character,
        out ActionLinkedMoveMemory pendingAction)
    {
        pendingAction = default;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var settling))
        {
            pendingAction = settling.PendingAction;
            return true;
        }

        return false;
    }

    private static bool TryGetPendingActionLinkedMoveForCompletion(
        GameLocationCharacter character,
        out ActionLinkedMoveMemory pendingAction,
        out bool fromSettling)
    {
        fromSettling = false;

        if (TryGetLiveActionLinkedMoveForCompletion(character, out pendingAction))
        {
            return true;
        }

        if (TryGetSettledActionLinkedMoveForCompletion(character, out pendingAction))
        {
            fromSettling = true;
            return true;
        }

        pendingAction = default;
        return false;
    }

    private static bool TryDeferActionLinkedMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 target)
    {
        if (character?.RulesetCharacter == null || pendingAction.MoveToken == 0)
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        ActionLinkedMoveSettlingCache[character.Guid] = new ActionLinkedMoveSettlingMemory(
            pendingAction,
            start,
            target,
            round,
            turnStamp);
        ActionLinkedMoveCache.Remove(character.Guid);

        return true;
    }

    private static bool TryMarkActionLinkedMoveCallbackObserved(
        GameLocationCharacter character,
        CombatAiRouteMoveSourceKind routeMoveSource,
        int moveToken)
    {
        if (character?.RulesetCharacter == null ||
            moveToken == 0 ||
            !ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var memory) ||
            memory.PendingAction.MoveToken != moveToken ||
            memory.PendingAction.RouteMoveSource != routeMoveSource)
        {
            return false;
        }

        if (!memory.CallbackObserved)
        {
            ActionLinkedMoveSettlingCache[character.Guid] = memory.WithCallbackObserved();
        }

        return true;
    }

    internal static void RecordAiMoveResult(
        CharacterActionMove action,
        int3 start,
        int3 target,
        bool forceCloseNoMoveAfterSettling = false)
    {
        var character = action?.ActingCharacter;

        EnsureCombatAiRuntimeCache();

        if (!IsAiControlledForCombat(character))
        {
            return;
        }

        var hadPendingAttempt = PendingAiMoveAttemptCache.ContainsKey(character.Guid);
        var hadSettling = GroundMeleeMoveSettlingCache.ContainsKey(character.Guid);
        var hadActionLinkedSettling = ActionLinkedMoveSettlingCache.ContainsKey(character.Guid);
        var hadLinkedMove = ActionLinkedMoveCache.ContainsKey(character.Guid);

        if (!IsActiveBattleContender(character))
        {
            ClearPendingMoveOwnership(character);
            return;
        }

        if (start == target)
        {
            PendingAiMoveAttemptCache.Remove(character.Guid);
            return;
        }

        PendingAiMoveAttemptCache.Remove(character.Guid);

        if (character.LocationPosition != start &&
            TryGetBlockingCombatantAtPosition(character, character.LocationPosition, out var occupant))
        {
            ClearPendingMoveOwnership(character);
            return;
        }

        if (character.LocationPosition == start && start != target && !hadLinkedMove && !hadActionLinkedSettling)
        {
            RecordAiMoveFailure(character, start, target);
        }

        if (TryResolveJumpImmediateMoveSettlingAfterMoveResult(character))
        {
            return;
        }

        var hadPendingActionLinkedMove =
            ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction);

        if (hadPendingActionLinkedMove &&
            character.LocationPosition == start &&
            start != target &&
            TryDeferActionLinkedMoveResult(character, pendingAction, start, target))
        {
            return;
        }

        if (forceCloseNoMoveAfterSettling &&
            hadPendingActionLinkedMove &&
            IsGroundMeleeJumpImmediateAttackRoute(pendingAction) &&
            character.LocationPosition == start)
        {
            DeferJumpImmediateAttackMoveResult(character, pendingAction);
            return;
        }

        if (forceCloseNoMoveAfterSettling &&
            hadPendingActionLinkedMove &&
            IsGroundMeleePursuitTerminalRoute(pendingAction) &&
            character.LocationPosition == start)
        {
            ForceCloseNoMoveAfterMoveResult(
                character,
                pendingAction,
                start,
                target);
            return;
        }

        if (hadPendingActionLinkedMove &&
            IsGroundMeleeMoveSettlingRoute(pendingAction))
        {
            if (TryDeferGroundMeleeMoveSettling(character, pendingAction))
            {
                return;
            }

            if (TryFinalizeGroundMeleePursuitAtActualDestination(
                character,
                pendingAction,
                start,
                target))
            {
                return;
            }

            return;
        }

        if (hadPendingActionLinkedMove &&
            pendingAction.MoveToken != 0 &&
            pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove &&
            TryDeferActionLinkedMoveResult(character, pendingAction, start, target))
        {
            return;
        }

        if (hadPendingActionLinkedMove &&
            IsConnectedFiringLineRoute(pendingAction))
        {
            if (character.LocationPosition == start)
            {
                DeferConnectedFiringLineMoveResult(character, pendingAction, start, target);
                return;
            }

            CloseConnectedFiringLineMoveResult(
                character,
                pendingAction,
                start,
                target);
            return;
        }

        if (hadPendingActionLinkedMove &&
            IsSearchKnownTargetRoute(pendingAction))
        {
            if (character.LocationPosition == start)
            {
                return;
            }

            if (TryCompleteSearchKnownTargetMovementStep(character).IsComplete)
            {
                return;
            }

            DeferSearchKnownTargetMoveResult(character, pendingAction, start, target);
            return;
        }

        if (character.LocationPosition == target)
        {
            UpdateTurnMovementProgress(character);
            TryCompleteActionLinkedMove(character);
            return;
        }

        if (hadPendingActionLinkedMove &&
            TryFinalizeRouteMoveAtActualDestination(character, pendingAction, start, target))
        {
            return;
        }

        ApplyPendingRouteMovementState(character);
        ActionLinkedMoveCache.Remove(character.Guid);

        if (hadPendingActionLinkedMove)
        {
            CloseLateActionLinkedMoveCompletion(
                character,
                pendingAction,
                start,
                target);
            return;
        }

        RecordAiMoveFailure(character, start, target);
    }

    private static bool TryResolveJumpImmediateMoveSettlingAfterMoveResult(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var memory) ||
            !IsGroundMeleeJumpImmediateAttackRoute(memory.PendingAction))
        {
            return false;
        }

        if (TryDeferJumpImmediateAttackResolutionUntilStableBoundary(character, memory.PendingAction))
        {
            return true;
        }

        ResolveGroundMeleeMoveSettling(
            character,
            allowConnectedRouteValidation: true);

        return true;
    }

    private static void ForceCloseNoMoveAfterMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 target)
    {
        PendingAiMoveAttemptCache.Remove(character.Guid);
        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);

        RecordAiMoveFailure(character, start, target);
        RecordGroundMeleeRouteFailure(
            character,
            pendingAction.Target,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination);
        TurnMovementProgressCache.Remove(character.Guid);

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            RouteMoveCompletionFlags.NoMove | RouteMoveCompletionFlags.GroundMeleeNoMove,
            round,
            turnStamp);

    }

    private static void DeferConnectedFiringLineMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination)
    {
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            RouteMoveCompletionFlags.None,
            round,
            turnStamp);

    }

    private static void CloseConnectedFiringLineMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination)
    {
        var actualDestination = character.LocationPosition;
        var completionKind = actualDestination == start
            ? ConnectedFiringLineCompletionKind.FailedNoMovementStep
            : GetConnectedFiringLineCompletionKindAtPosition(character, pendingAction, actualDestination);
        var failed = completionKind is ConnectedFiringLineCompletionKind.FailedNoMovementStep or
            ConnectedFiringLineCompletionKind.FailedNoAction;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            RouteMoveCompletionFlags.None,
            round,
            turnStamp);

        if (failed && actualDestination == start)
        {
            RecordAiMoveFailure(character, start, expectedDestination);
        }
        else
        {
            UpdateTurnMovementProgress(character);
        }

    }

    private static void DeferSearchKnownTargetMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination)
    {
        var actualDestination = character.LocationPosition;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (TryDeferProgressOnlySearchPartialMove(
                character,
                pendingAction,
                start,
                expectedDestination,
                actualDestination))
        {
            return;
        }

        if (ShouldCloseProgressOnlySearchRouteWithoutTerminalOwnership(character, pendingAction, actualDestination))
        {
            CloseProgressOnlySearchRouteWithoutTerminalOwnership(
                character,
                pendingAction,
                start,
                expectedDestination);
            return;
        }

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            RouteMoveCompletionFlags.None,
            round,
            turnStamp);

    }

    private static void CloseSearchKnownTargetMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        SearchKnownTargetCompletionKind resultKind)
    {
        var actualDestination = character.LocationPosition;
        var failed = resultKind == SearchKnownTargetCompletionKind.FailedNoMeaningfulMovement;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (ShouldCloseProgressOnlySearchRouteWithoutTerminalOwnership(character, pendingAction, actualDestination))
        {
            CloseProgressOnlySearchRouteWithoutTerminalOwnership(
                character,
                pendingAction,
                start,
                expectedDestination);
            return;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);

        if (failed && actualDestination == start)
        {
            RecordLostTargetSearchAttempt(character, pendingAction);
        }

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            RouteMoveCompletionFlags.None,
            round,
            turnStamp);

        if (failed)
        {
            RecordAiMoveFailure(character, start, expectedDestination);

            if (actualDestination != start)
            {
                RecordAiMoveFailure(character, start, actualDestination);
            }

        }
        else
        {
            UpdateTurnMovementProgress(character);
        }

    }

    private static bool ShouldCloseProgressOnlySearchRouteWithoutTerminalOwnership(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination)
    {
        return character?.RulesetCharacter != null &&
               IsProgressOnlySearchRoute(pendingAction);
    }

    private static bool TryDeferProgressOnlySearchPartialMove(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        int3 actualDestination)
    {
        if (!CanContinueProgressOnlySearchPartialMove(
                character,
                pendingAction,
                start,
                expectedDestination,
                actualDestination))
        {
            return false;
        }

        var continuedAction = new ActionLinkedMoveMemory(
            pendingAction.Target,
            pendingAction.ActionKind,
            pendingAction.Continuation,
            pendingAction.MovementGoal,
            actualDestination,
            expectedDestination,
            pendingAction.RouteMoveSource,
            pendingAction.LockRemainingMovementAfterArrival,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            pendingAction.MoveToken,
            pendingAction.SearchRouteActionConnected);
        var callbackObserved =
            ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var settling) &&
            settling.PendingAction.MoveToken == pendingAction.MoveToken &&
            settling.PendingAction.RouteMoveSource == pendingAction.RouteMoveSource &&
            settling.CallbackObserved;

        ActionLinkedMoveSettlingCache[character.Guid] = new ActionLinkedMoveSettlingMemory(
            continuedAction,
            actualDestination,
            expectedDestination,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            callbackObserved);
        ActionLinkedMoveCache.Remove(character.Guid);
        PendingAiMoveAttemptCache.Remove(character.Guid);
        RouteMoveCompletionClosedCache.Remove(character.Guid);

        return true;
    }

    private static bool CanContinueProgressOnlySearchPartialMove(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        int3 actualDestination)
    {
        if (character?.RulesetCharacter == null ||
            !IsActiveBattleContender(character) ||
            !IsProgressOnlySearchRoute(pendingAction) ||
            actualDestination == start ||
            actualDestination == expectedDestination ||
            character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available ||
            !IsLegalAiRouteDestination(character, expectedDestination))
        {
            return false;
        }

        return pendingAction.Round == GetCurrentBattleRound() &&
               pendingAction.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool IsProgressOnlySearchRoute(ActionLinkedMoveMemory pendingAction)
    {
        return IsSearchKnownTargetRoute(pendingAction) &&
               pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ProgressOnlySearchMove &&
               !pendingAction.SearchRouteActionConnected;
    }

    private static void ClearSearchRouteWithoutTerminalOwnership(GameLocationCharacter character, bool markCompletionSeal)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        PendingAiMoveAttemptCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);

        if (markCompletionSeal)
        {
            MarkDisconnectedSearchMoveCompletionSeal(character);
        }
        else
        {
            DisconnectedSearchMoveCompletionSealCache.Remove(character.Guid);
        }

        DisconnectedSearchNoRouteMovementSealCache.Remove(character.Guid);
    }

    private static void CloseSearchNoRouteWithoutTerminalOwnership(
        GameLocationCharacter character,
        bool markNoRouteMovementSeal = false)
    {
        ClearSearchRouteWithoutTerminalOwnership(character, markCompletionSeal: false);

        if (markNoRouteMovementSeal)
        {
            MarkDisconnectedSearchNoRouteMovementSeal(character);
        }
    }

    private static void CloseProgressOnlySearchRouteWithoutTerminalOwnership(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var actualDestination = character.LocationPosition;
        var flags = RouteMoveCompletionFlags.NoConnectedRoute;

        if (actualDestination == start)
        {
            flags |= RouteMoveCompletionFlags.NoMove;
            RecordLostTargetSearchAttempt(character, pendingAction);
            RecordAiMoveFailure(character, start, expectedDestination);
        }
        else
        {
            UpdateTurnMovementProgress(character);
        }

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            flags,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        ClearSearchRouteWithoutTerminalOwnership(character, markCompletionSeal: true);
    }

    private static void MarkDisconnectedSearchMoveCompletionSeal(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        DisconnectedSearchMoveCompletionSealCache[character.Guid] =
            new TerminalSealMemory(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static void MarkDisconnectedSearchNoRouteMovementSeal(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        DisconnectedSearchNoRouteMovementSealCache[character.Guid] =
            new TerminalSealMemory(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool HasActiveDisconnectedSearchMoveCompletionSeal(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !DisconnectedSearchMoveCompletionSealCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        if (memory.Matches(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp)))
        {
            return true;
        }

        DisconnectedSearchMoveCompletionSealCache.Remove(character.Guid);

        return false;
    }

    private static bool HasActiveProgressOnlySearchOwnedMove(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction))
        {
            return IsCurrentProgressOnlySearchRoute(pendingAction);
        }

        if (ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var settling))
        {
            return IsCurrentProgressOnlySearchRoute(settling.PendingAction);
        }

        return false;
    }

    private static bool IsCurrentProgressOnlySearchRoute(ActionLinkedMoveMemory pendingAction)
    {
        return IsProgressOnlySearchRoute(pendingAction) &&
               pendingAction.Round == GetCurrentBattleRound() &&
               pendingAction.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static void CloseLateActionLinkedMoveCompletion(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination)
    {
        var actualDestination = character.LocationPosition;
        var noMove = actualDestination == start;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (noMove)
        {
            RecordAiMoveFailure(character, start, expectedDestination);

            if (IsGroundMeleePursuitTerminalRoute(pendingAction))
            {
                RecordGroundMeleeRouteFailure(
                    character,
                    pendingAction.Target,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination);
            }
        }
        else
        {
            UpdateTurnMovementProgress(character);
        }

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            RouteMoveCompletionFlags.LateCompletion |
            (noMove ? RouteMoveCompletionFlags.NoMove : RouteMoveCompletionFlags.None),
            round,
            turnStamp);
    }

    private static void RecordAiMoveFailure(GameLocationCharacter character, int3 start, int3 target)
    {
        if (character == null)
        {
            return;
        }

        if (!AiMoveFailureCache.TryGetValue(character.Guid, out var failures))
        {
            failures = [];
            AiMoveFailureCache[character.Guid] = failures;
        }

        failures.Add(new AiMoveFailureKey(start, target));
    }

    private static bool TryGetPostMainClearAllyCorridorAttempt(
        GameLocationCharacter actor,
        out PostMainClearAllyCorridorAttemptMemory memory)
    {
        memory = default;

        if (actor?.RulesetCharacter == null ||
            !PostMainClearAllyCorridorAttemptCache.TryGetValue(actor.Guid, out memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            return true;
        }

        PostMainClearAllyCorridorAttemptCache.Remove(actor.Guid);
        memory = default;

        return false;
    }

    private static bool TryBlockRepeatedPostMainClearAllyCorridorAttempt(GameLocationCharacter actor)
    {
        if (!TryGetPostMainClearAllyCorridorAttempt(actor, out var memory))
        {
            return false;
        }

        if (memory.Blocked)
        {
            return true;
        }

        PostMainClearAllyCorridorAttemptCache[actor.Guid] = memory.WithBlocked();

        return true;
    }

    private static void RecordPostMainClearAllyCorridorAttemptStarted(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        string allyGuid,
        ulong targetGuid)
    {
        if (actor?.RulesetCharacter == null)
        {
            return;
        }

        var memory = new PostMainClearAllyCorridorAttemptMemory(
            start,
            destination,
            allyGuid,
            targetGuid,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            false);

        PostMainClearAllyCorridorAttemptCache[actor.Guid] = memory;
    }

    private static void CompletePostMainClearAllyCorridorAttempt(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        string allyGuid,
        ulong targetGuid,
        bool aborted)
    {
        if (actor?.RulesetCharacter == null)
        {
            return;
        }

        if (!TryGetPostMainClearAllyCorridorAttempt(actor, out _))
        {
            PostMainClearAllyCorridorAttemptCache[actor.Guid] =
                new PostMainClearAllyCorridorAttemptMemory(
                    start,
                    destination,
                    allyGuid,
                    targetGuid,
                    GetCurrentBattleRound(),
                    Math.Max(1, ObservedCombatMemoryTurnStamp),
                    false);
        }

        if (aborted)
        {
            RecordAiMoveFailure(actor, start, destination);
        }

    }

    internal static void CleanupTrackedFallbackDodgeOnTurnStart(GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            !FallbackDodgeConditionCache.TryGetValue(character.Guid, out var memory))
        {
            return;
        }

        FallbackDodgeConditionCache.Remove(character.Guid);

        foreach (var conditions in rulesetCharacter.ConditionsByCategory.Values)
        {
            foreach (var condition in conditions.ToArray())
            {
                if (condition.Guid != memory.ConditionGuid)
                {
                    continue;
                }

                rulesetCharacter.RemoveCondition(condition);

                return;
            }
        }
    }

    internal static void Unload()
    {
        ClearRuntimeCaches();
        CurrentBattleCacheKey = null;
        CurrentBattleCacheRound = -1;
        CurrentAdvancedCombatAiSetting = null;
        CurrentBonusActionFreeJumpSetting = null;
    }

    private static bool CanExecuteAutomaticCombatAction(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (IsAdvancedCombatAiSuppressedByManualNpcControl(character))
        {
            return false;
        }

        var networkingService = ServiceRepository.GetService<INetworkingService>();

        return networkingService?.IsMultiplayerGame != true || networkingService.IsMasterClient;
    }

    internal static void RecordCombatAiActionExecution(
        GameLocationCharacter character,
        CharacterActionParams actionParams,
        ActionScope scope,
        int actionRank,
        ActionStatus mainBefore)
    {
        var actionDefinition = actionParams?.ActionDefinition;

        if (scope != ActionScope.Battle ||
            character?.RulesetCharacter == null ||
            actionDefinition == null ||
            !IsAiControlledForCombat(character))
        {
            return;
        }

        EnsureCombatAiRuntimeCache();

        var actionId = actionDefinition.Id;
        var actionType = actionDefinition.ActionType;
        TryClearAcceptedPendingResidualMainAction(character, actionId);

        var memory = new CombatAiActionExecutionMemory(
            actionId,
            actionType,
            mainBefore,
            actionRank,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        var isLedgerMainAction = IsMainActionId(memory.ActionId);

        if (isLedgerMainAction &&
            !IsActiveBattleContender(character))
        {
            return;
        }

        LastActionExecutionCache[character.Guid] = memory;

        if (isLedgerMainAction)
        {
            LastMainActionExecutionCache[character.Guid] = memory;
            IncrementActionUseCount(TurnMainActionUseCountCache, character);

            RecordRepeatedAttackActionExecution(character, actionParams, memory);
        }

        if (IsConnectedHostileActionExecution(character, actionParams))
        {
            DisconnectedPositioningSealCache.Remove(character.Guid);
            DisconnectedPositioningMovementLockCache.Remove(character.Guid);
            DisconnectedSearchMoveCompletionSealCache.Remove(character.Guid);
            DisconnectedSearchNoRouteMovementSealCache.Remove(character.Guid);
        }

        if (memory.ActionType == ActionType.Bonus)
        {
            IncrementActionUseCount(TurnBonusActionUseCountCache, character);
        }
    }

    private static bool IsConnectedHostileActionExecution(
        GameLocationCharacter character,
        CharacterActionParams actionParams)
    {
        var actionId = actionParams?.ActionDefinition?.Id;

        if (character?.RulesetCharacter == null ||
            actionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain or Id.CastBonus or Id.PowerBonus))
        {
            return false;
        }

        var target = actionParams.TargetCharacters.FirstOrDefault();

        if (target?.RulesetCharacter != null && target.Side != character.Side)
        {
            return true;
        }

        return actionId is Id.CastMain or Id.PowerMain or Id.CastBonus or Id.PowerBonus &&
               TryGetActionEffectDescription(actionParams, out var effectDescription) &&
               effectDescription.TargetSide == Side.Enemy;
    }

    private static void RecordRepeatedAttackActionExecution(
        GameLocationCharacter character,
        CharacterActionParams actionParams,
        CombatAiActionExecutionMemory memory)
    {
        if (!TryGetRepeatableAttackAction(
                character,
                actionParams,
                out var target,
                out var actionKind,
                out var actionIdentity))
        {
            return;
        }

        var repeat = 1;

        if (RepeatAttackActionCache.TryGetValue(character.Guid, out var previous) &&
            previous.TargetGuid == target.Guid &&
            previous.ActionIdentity == actionIdentity &&
            previous.ActionKind == actionKind &&
            previous.ActorPosition == character.LocationPosition &&
            previous.TargetPosition == target.LocationPosition)
        {
            repeat = previous.RepeatCount + 1;
        }

        RepeatAttackActionCache[character.Guid] = new RepeatedAttackActionMemory(
            target.Guid,
            actionIdentity,
            actionKind,
            character.LocationPosition,
            target.LocationPosition,
            memory.Round,
            memory.TurnStamp,
            repeat);

        var logThreshold = actionKind == CombatAiActionKind.Melee
            ? RepeatedMeleeAlternativeThreshold
            : RepeatedRangedAttackThreshold;

}

    private static bool TryGetRepeatableAttackAction(
        GameLocationCharacter character,
        CharacterActionParams actionParams,
        out GameLocationCharacter target,
        out CombatAiActionKind actionKind,
        out string actionIdentity)
    {
        target = null;
        actionKind = CombatAiActionKind.None;
        actionIdentity = null;

        var actionId = actionParams?.ActionDefinition?.Id;

        if (character?.RulesetCharacter == null || actionParams == null)
        {
            return false;
        }

        target = actionParams.TargetCharacters.FirstOrDefault();

        if (target?.RulesetCharacter == null || target.Side == character.Side)
        {
            return false;
        }

        if (actionId == Id.AttackMain)
        {
            var mode = actionParams.AttackMode;

            if (mode == null)
            {
                return false;
            }

            actionKind = ValidatorsWeapon.IsMelee(mode)
                ? CombatAiActionKind.Melee
                : CombatAiActionKind.Ranged;
            actionIdentity = GetAttackModeIdentity(mode);

            return true;
        }

        if (actionId != Id.CastMain)
        {
            return false;
        }

        var effect = actionParams.RulesetEffect ?? actionParams.activeEffect;
        var spellName = effect is RulesetEffectSpell spell
            ? spell.SpellDefinition?.Name
            : null;

        actionKind = CombatAiActionKind.Spell;
        actionIdentity = spellName;

        return true;
    }

    internal static void RecordAiBonusActionUse(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var memory = new CombatAiActionExecutionMemory(
            Id.TacticalMove,
            ActionType.Bonus,
            character.GetActionTypeStatus(ActionType.Main),
            character.CurrentActionRankByType[ActionType.Main],
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        LastActionExecutionCache[character.Guid] = memory;
        IncrementActionUseCount(TurnBonusActionUseCountCache, character);
    }

    private static void IncrementActionUseCount(Dictionary<ulong, int> cache, GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        cache[character.Guid] = cache.TryGetValue(character.Guid, out var count) ? count + 1 : 1;
    }

    internal static void RecordRecentMeleeThreat(
        GameLocationCharacter source,
        GameLocationCharacter defender,
        RulesetAttackMode attackMode,
        ActionType actionType)
    {
        if (!IsAdvancedCombatAiEnabled ||
            source?.RulesetCharacter == null ||
            defender?.RulesetCharacter == null ||
            source == defender ||
            source.Side == defender.Side ||
            attackMode == null ||
            attackMode.Ranged ||
            actionType is not (ActionType.Main or ActionType.Bonus or ActionType.Reaction))
        {
            return;
        }

        RecentMeleeThreatMemoryCache[defender.Guid] = new RecentMeleeThreatMemory(
            source,
            source.LocationPosition,
            source.RulesetCharacter is RulesetCharacterEffectProxy,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

    }

    internal static bool ShouldBlockInactiveAiBattleAction(CharacterAction action)
    {
        if (!IsAdvancedCombatAiEnabled)
        {
            return false;
        }

        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            !IsAiControlledForCombat(character) ||
            !CanExecuteAutomaticCombatAction(character) ||
            IsActiveBattleContender(character) ||
            action is CharacterActionMoveStepBase ||
            action.ActionType == ActionType.Reaction)
        {
            return false;
        }

        if (action.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain or
                Id.CastBonus or Id.PowerBonus or Id.DashBonus or Id.TacticalMove or
                Id.Ready or Id.Dodge))
        {
            return false;
        }

        TryClearInactiveCombatAiTurnOwnership(character);

        return true;
    }

    private static bool TryGetSearchKnownTargetOwnedMoveGate(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        out bool allowed)
    {
        allowed = false;

        if (character?.RulesetCharacter == null ||
            !TryGetPendingActionLinkedMoveForCompletion(character, out var pendingAction, out _) ||
            !IsSearchKnownTargetRoute(pendingAction))
        {
            return false;
        }

        if (destination.x == 0 && destination.y == 0 && destination.z == 0)
        {
            allowed = true;
            return true;
        }

        if (start != pendingAction.StartPosition)
        {
            return false;
        }

        if (destination == pendingAction.ExpectedDestination)
        {
            allowed = true;
            return true;
        }

        var progress = ComputeSearchKnownTargetProgress(character, pendingAction, destination);

        if (progress.HasMeaningfulProgress)
        {
            allowed = true;
            return true;
        }

        if (HasSearchKnownTargetValidatedActionAtDestination(
                character,
                pendingAction,
                destination))
        {
            allowed = true;
            return true;
        }

        return true;
    }

    private static bool ShouldBlockDisconnectedPositioningDestination(
        CharacterAction action,
        GameLocationCharacter character,
        DisconnectedPositioningSealMemory seal,
        int3 destination)
    {
        return ShouldBlockDisconnectedPositioningDestination(
            action,
            character,
            seal,
            character.LocationPosition,
            destination);
    }

    private static bool ShouldBlockDisconnectedPositioningDestination(
        CharacterAction action,
        GameLocationCharacter character,
        DisconnectedPositioningSealMemory seal,
        int3 start,
        int3 destination)
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            return false;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        if (HasValidatedPostMoveActionAtDestination(character, turnPlan, battleService, destination))
        {
            return false;
        }

        var currentTurnsToAction = EstimateTurnsToPreferredAction(character, turnPlan, start);
        var destinationTurnsToAction = EstimateTurnsToPreferredAction(character, turnPlan, destination);

        if (ShouldAllowDisconnectedRangedSeekImprovement(
                character,
                turnPlan,
                start,
                destination,
                currentTurnsToAction,
                destinationTurnsToAction))
        {
            return false;
        }

        RecordAiMoveFailure(character, start, destination);
        ApplyDisconnectedPositioningMovementLock(character, seal);

        return true;
    }

    private static bool ShouldBlockDisconnectedPositioningUnresolvedDestination(
        GameLocationCharacter character,
        DisconnectedPositioningSealMemory seal)
    {
        ApplyDisconnectedPositioningMovementLock(character, seal);

        return true;
    }

    private static bool ApplyDisconnectedPositioningMovementLock(
        GameLocationCharacter character,
        DisconnectedPositioningSealMemory seal)
    {
        if (character?.RulesetCharacter != null &&
            DisconnectedPositioningMovementLockCache.TryGetValue(character.Guid, out var existingLock) &&
            existingLock.MatchesCurrentTurn(seal.Round, seal.TurnStamp))
        {
            return false;
        }

        if (!CanApplyDisconnectedPositioningMovementLock(character, seal))
        {
            return false;
        }

        DisconnectedPositioningMovementLockCache[character.Guid] =
            new DisconnectedPositioningMovementLockMemory(seal.Round, seal.TurnStamp);
        ClearPendingMoveOwnership(character);

        return true;
    }

    private static bool CanApplyDisconnectedPositioningMovementLock(
        GameLocationCharacter character,
        DisconnectedPositioningSealMemory seal)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!CanExecuteAutomaticCombatAction(character))
        {
            return false;
        }

        if (!IsAiControlledForCombat(character))
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!seal.MatchesCurrentTurn(currentRound, currentStamp))
        {
            return false;
        }

        if (seal.Goal != CombatAiMovementGoalKind.MoveToPreferredRange)
        {
            return false;
        }

        if (seal.Policy is not (CombatAiMovementPolicyKind.RangedLinePolicy or CombatAiMovementPolicyKind.SpellLinePolicy))
        {
            return false;
        }

        var tacticalStatus = character.GetActionStatus(Id.TacticalMove, ActionScope.Battle);

        if (tacticalStatus != ActionStatus.Available)
        {
            return false;
        }

        if (character.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        return true;
    }

    private static void MarkPendingResidualMainAction(
        GameLocationCharacter character,
        Id actionId)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PendingResidualMainActionCache[character.Guid] = new PendingResidualMainAction(
            actionId,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool TryClearAcceptedPendingResidualMainAction(
        GameLocationCharacter character,
        Id actionId)
    {
        if (character?.RulesetCharacter == null ||
            !PendingResidualMainActionCache.TryGetValue(character.Guid, out var pending) ||
            !pending.Matches(
                actionId,
                GetCurrentBattleRound(),
                Math.Max(1, ObservedCombatMemoryTurnStamp)))
        {
            return false;
        }

        PendingResidualMainActionCache.Remove(character.Guid);
        return true;
    }

    private static bool IsMainActionId(Id actionId)
    {
        return actionId == Id.AttackMain ||
               actionId == Id.CastMain ||
               actionId == Id.PowerMain ||
               actionId == Id.DashMain ||
               actionId == Id.Ready ||
               actionId == Id.Dodge ||
               actionId == Id.Shove ||
               actionId == (Id)ExtraActionId.Grapple;
    }

    private static bool TryGetActionEffectDescription(
        CharacterActionParams actionParams,
        out EffectDescription effectDescription)
    {
        effectDescription = null;

        var effect = actionParams?.RulesetEffect ?? actionParams?.activeEffect;

        switch (effect)
        {
            case RulesetEffectSpell spell:
                effectDescription = spell.SpellDefinition?.EffectDescription;
                break;

            case RulesetEffectPower power:
                effectDescription = power.PowerDefinition?.EffectDescription;
                break;
        }

        return effectDescription != null;
    }

    private static bool IsAttackModeAvailableForMainAction(
        GameLocationCharacter character,
        RulesetAttackMode mode,
        out ActionStatus actionStatus)
    {
        actionStatus = ActionStatus.Unavailable;

        if (character?.RulesetCharacter == null || mode == null)
        {
            return false;
        }

        actionStatus = character.GetActionStatus(Id.AttackMain, ActionScope.Battle);

        if (actionStatus != ActionStatus.Available)
        {
            return false;
        }

        if (!TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction) ||
            committedMainAction.ActionId != Id.AttackMain ||
            HasUnspentMainActionRank(
                character,
                committedMainAction,
                character.GetActionTypeStatus(ActionType.Main)))
        {
            return true;
        }

        actionStatus = character.GetActionStatus(
            Id.AttackMain,
            ActionScope.Battle,
            optionalAttackMode: mode);

        return actionStatus == ActionStatus.Available;
    }

    private static bool HasAvailableAttackModeForMainAction(
        GameLocationCharacter character,
        CombatAiActionKind actionKind = CombatAiActionKind.None)
    {
        if (character?.RulesetCharacter == null ||
            actionKind is not (CombatAiActionKind.None or CombatAiActionKind.Melee or CombatAiActionKind.Ranged))
        {
            return false;
        }

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (mode == null ||
                actionKind == CombatAiActionKind.Melee && !IsMeleeAttackModeForAi(mode) ||
                actionKind == CombatAiActionKind.Ranged && !IsRangedAttackMode(mode) ||
                !IsAttackModeAvailableForMainAction(character, mode, out _))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool CanUseMainSpellActionForAi(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available ||
            character.GetActionStatus(Id.CastMain, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        return !TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction) ||
               HasUnspentMainActionRank(
                   character,
                   committedMainAction,
                   character.GetActionTypeStatus(ActionType.Main));
    }

    private static bool HasImprovingMeleePursuit(GameLocationCharacter character)
    {
        if (!CanIssueAdditionalTacticalMove(character))
        {
            return false;
        }

        if (!TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress))
        {
            return HasCurrentStateMeleePursuit(character);
        }

        var start = character.LocationPosition;

        foreach (var candidate in movementProgress.EnumerateAcceptedMoveCandidates())
        {
            if (candidate.Position == start ||
                IsFailedAiMoveTarget(character, start, candidate.Position))
            {
                continue;
            }

            return true;
        }

        if (!GroundMeleeJumpRouteAvailableCache.Contains(character.Guid))
        {
            return false;
        }

        return true;
    }

    private static bool HasCurrentStateMeleePursuit(GameLocationCharacter character)
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !CanIssueAdditionalTacticalMove(character))
        {
            return false;
        }

        var profile = BuildProfile(character);
        var capabilityCatalog = BuildCapabilityCatalog(character);

        if (!ShouldPreferMeleeAction(profile, capabilityCatalog))
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (IsRoutePathfindingUnsafePhase(character, round, turnStamp))
        {
            return false;
        }

        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        if (!IsGroundMeleePursuitPlan(turnPlan))
        {
            return false;
        }

        if (!TryGetGroundMeleeTargetContactRouteQuery(character, turnPlan, out var query) ||
            !query.Complete)
        {
            return false;
        }

        var start = character.LocationPosition;
        var startRemainingCost = query.TryGetContactCost(start, out var startContactCost, out _)
            ? startContactCost
            : query.BestGoalMoveCost;

        if (startRemainingCost <= 0)
        {
            return false;
        }

        return true;
    }

    private static string GetAttackModeIdentity(RulesetAttackMode mode)
    {
        return mode?.SourceDefinition?.Name ?? mode?.SourceObject?.GetType().Name;
    }

    private static ActionStatus GetAttackMainActionStatusForAi(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return ActionStatus.Unavailable;
        }

        var status = character.GetActionStatus(Id.AttackMain, ActionScope.Battle);

        if (status != ActionStatus.Available)
        {
            return status;
        }

        if (TryGetCommittedNonTerminalMainActionThisTurn(
                character,
                out var committedMainAction) &&
            committedMainAction.ActionId == Id.AttackMain &&
            !HasAvailableAttackMainContinuation(character))
        {
            return ActionStatus.Unavailable;
        }

        return status;
    }

    private static CombatAiExecutedActionKind GetTerminalActionKind(CombatAiActionExecutionMemory memory)
    {
        return memory.ActionId switch
        {
            Id.Ready => CombatAiExecutedActionKind.Ready,
            Id.Dodge => CombatAiExecutedActionKind.Dodge,
            _ => CombatAiExecutedActionKind.None
        };
    }

    private static CombatAiExecutedActionKind GetLastActionTerminalKind(
        GameLocationCharacter character,
        Dictionary<ulong, CombatAiActionExecutionMemory> cache)
    {
        return TryGetCurrentTurnActionMemory(character, cache, out var memory)
            ? GetTerminalActionKind(memory)
            : CombatAiExecutedActionKind.None;
    }

    private static int GetActionUseCount(Dictionary<ulong, int> cache, GameLocationCharacter character)
    {
        return character != null && cache.TryGetValue(character.Guid, out var count) ? count : 0;
    }

    private static bool IsActiveBattleContender(GameLocationCharacter character)
    {
        return character != null && Gui.Battle?.ActiveContender == character;
    }

    private static bool HasQueuedActionChain(GameLocationCharacter character)
    {
        return character?.RulesetCharacter != null &&
               ServiceRepository.GetService<IGameLocationActionService>() is GameLocationActionManager actionManager &&
               actionManager.actionChainByCharacter.TryGetValue(character, out var actionChainSlot) &&
               actionChainSlot?.actionQueue?.Count > 0;
    }

    private static bool HasAnyQueuedActionChain()
    {
        if (ServiceRepository.GetService<IGameLocationActionService>() is not
            GameLocationActionManager actionManager)
        {
            return false;
        }

        foreach (var slot in actionManager.actionChainByCharacter.Values)
        {
            if (slot?.actionQueue?.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCommittedMainActionThisTurn(GameLocationCharacter character)
    {
        return IsActionCommittedThisTurn(character, LastMainActionExecutionCache);
    }

    private static bool HasCommittedBonusActionThisTurn(GameLocationCharacter character)
    {
        if (character != null && TurnBonusActionUseCountCache.TryGetValue(character.Guid, out var count) && count > 0)
        {
            return true;
        }

        return IsActionCommittedThisTurn(character, LastActionExecutionCache, ActionType.Bonus);
    }

    private static bool IsActionCommittedThisTurn(
        GameLocationCharacter character,
        Dictionary<ulong, CombatAiActionExecutionMemory> cache,
        ActionType? actionType = null)
    {
        return character != null &&
               IsActiveBattleContender(character) &&
               cache.TryGetValue(character.Guid, out var memory) &&
               memory.Round == GetCurrentBattleRound() &&
               memory.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp) &&
               (!actionType.HasValue || memory.ActionType == actionType.Value);
    }

    private static bool TryGetCurrentTurnActionMemory(
        GameLocationCharacter character,
        Dictionary<ulong, CombatAiActionExecutionMemory> cache,
        out CombatAiActionExecutionMemory memory)
    {
        memory = default;

        return character != null &&
               cache.TryGetValue(character.Guid, out memory) &&
               memory.Round == GetCurrentBattleRound() &&
               memory.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool IsTerminalMainActionId(Id actionId)
    {
        return actionId is Id.Ready or Id.Dodge;
    }

    private static bool IsNonTerminalMainActionId(Id actionId)
    {
        return IsMainActionId(actionId) && !IsTerminalMainActionId(actionId);
    }

    private static bool TryGetCommittedNonTerminalMainActionThisTurn(
        GameLocationCharacter character,
        out CombatAiActionExecutionMemory memory)
    {
        if (TryGetCurrentTurnActionMemory(character, LastMainActionExecutionCache, out memory) &&
            IsNonTerminalMainActionId(memory.ActionId))
        {
            return true;
        }

        if (TryGetCurrentTurnActionMemory(character, LastActionExecutionCache, out memory) &&
            IsNonTerminalMainActionId(memory.ActionId))
        {
            return true;
        }

        memory = default;
        return false;
    }

    private static bool TryGetCompletedTerminalMainActionThisTurn(
        GameLocationCharacter character,
        out CombatAiActionExecutionMemory memory)
    {
        if (TryGetCurrentTurnActionMemory(character, LastMainActionExecutionCache, out memory) &&
            IsTerminalMainActionId(memory.ActionId))
        {
            return true;
        }

        if (TryGetCurrentTurnActionMemory(character, LastActionExecutionCache, out memory) &&
            IsTerminalMainActionId(memory.ActionId))
        {
            return true;
        }

        memory = default;
        return false;
    }

    private static CombatAiActionEconomySnapshot BuildActionEconomySnapshot(GameLocationCharacter character)
    {
        var canAutoAct = CanExecuteAutomaticCombatAction(character);
        var isAiControlled = IsAiControlledForCombat(character);
        var hasLastMainAction = character != null && LastMainActionExecutionCache.ContainsKey(character.Guid);
        var hasLastAction = character != null && LastActionExecutionCache.ContainsKey(character.Guid);
        var lastMainTerminalAction = GetLastActionTerminalKind(character, LastMainActionExecutionCache);
        var lastTerminalAction = GetLastActionTerminalKind(character, LastActionExecutionCache);
        var mainUseCount = GetActionUseCount(TurnMainActionUseCountCache, character);
        var bonusUseCount = GetActionUseCount(TurnBonusActionUseCountCache, character);

        if (character?.RulesetCharacter == null)
        {
            return new CombatAiActionEconomySnapshot(
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                ActionStatus.Unavailable,
                hasLastMainAction,
                hasLastAction,
                lastMainTerminalAction,
                lastTerminalAction,
                mainUseCount,
                bonusUseCount,
                false,
                false,
                canAutoAct,
                isAiControlled);
        }

        var mainStatus = character.GetActionTypeStatus(ActionType.Main);
        var bonusStatus = character.GetActionTypeStatus(ActionType.Bonus);
        var attackMainStatus = GetAttackMainActionStatusForAi(character);
        var mainAvailable = IsMainActionAvailableForAi(character, mainStatus, attackMainStatus);
        var terminalMainAvailable = IsTerminalMainActionAvailableForAi(character, mainStatus);
        var bonusFreeJumpStatus =
            bonusStatus == ActionStatus.Available &&
            CanUseFreeJumpForAi(character)
                ? ActionStatus.Available
                : ActionStatus.Unavailable;

        return new CombatAiActionEconomySnapshot(
            mainStatus,
            attackMainStatus,
            character.GetActionStatus(Id.CastMain, ActionScope.Battle),
            character.GetActionStatus(Id.Ready, ActionScope.Battle),
            character.GetActionStatus(Id.Dodge, ActionScope.Battle),
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle),
            bonusStatus,
            bonusFreeJumpStatus,
            character.GetActionTypeStatus(ActionType.NoCost),
            hasLastMainAction,
            hasLastAction,
            lastMainTerminalAction,
            lastTerminalAction,
            mainUseCount,
            bonusUseCount,
            mainAvailable,
            terminalMainAvailable,
            canAutoAct,
            isAiControlled);
    }

    private static bool IsMainActionAvailableForAi(
        GameLocationCharacter character,
        ActionStatus mainStatus,
        ActionStatus attackMainStatus)
    {
        if (character?.RulesetCharacter == null ||
            mainStatus != ActionStatus.Available &&
            attackMainStatus != ActionStatus.Available)
        {
            return false;
        }

        if (!TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction))
        {
            return true;
        }

        if (HasUnspentMainActionRank(character, committedMainAction, mainStatus))
        {
            return true;
        }

        if (attackMainStatus != ActionStatus.Available)
        {
            return false;
        }

        return committedMainAction.ActionId != Id.AttackMain ||
               HasAvailableAttackMainContinuation(character);
    }

    private static bool IsTerminalMainActionAvailableForAi(
        GameLocationCharacter character,
        ActionStatus mainStatus)
    {
        if (character?.RulesetCharacter == null || mainStatus != ActionStatus.Available)
        {
            return false;
        }

        if (TryGetCompletedTerminalMainActionThisTurn(character, out _))
        {
            return false;
        }

        return !TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction) ||
               HasUnspentMainActionRank(character, committedMainAction, mainStatus);
    }

    private static bool HasUnspentMainActionRank(
        GameLocationCharacter character,
        CombatAiActionExecutionMemory committedMainAction,
        ActionStatus mainStatus)
    {
        return character?.RulesetCharacter != null &&
               mainStatus == ActionStatus.Available &&
               character.CurrentActionRankByType[ActionType.Main] > committedMainAction.ActionRank;
    }

    private static bool CanSpendTerminalMainAction(
        GameLocationCharacter character,
        CombatAiActionEconomySnapshot actionEconomy)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            return false;
        }

        return actionEconomy.TerminalMainAvailable;
    }

    internal static bool IsAiControlledForCombat(GameLocationCharacter character)
    {
        return IsAdvancedCombatAiEnabled &&
               !IsAdvancedCombatAiSuppressedByManualNpcControl(character) &&
               IsAiControlledByGame(character);
    }

    internal static bool IsAdvancedCombatAiSuppressedByManualNpcControl(GameLocationCharacter character)
    {
        if (!Main.Settings.EnableEnemiesControlledByPlayer || character == null)
        {
            return false;
        }

        var controlledCharacters = Gui.ActivePlayerController?.ControlledCharacters;

        return controlledCharacters?.Contains(character) == true;
    }

    private static bool IsAiControlledByGame(GameLocationCharacter character)
    {
        if (character == null ||
            character.RulesetCharacter == null ||
            character.ControllerId != PlayerControllerManager.DmControllerId)
        {
            return false;
        }

        if (!Main.Settings.EnableEnemiesControlledByPlayer)
        {
            return true;
        }

        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return true;
        }

        var isPartyMember =
            characterService.PartyCharacters.Contains(character) ||
            characterService.GuestCharacters.Contains(character);

        return isPartyMember || character.Side != Side.Ally;
    }

    internal static CombatAiProfile BuildProfile(GameLocationCharacter character)
    {
        if (character == null)
        {
            return default;
        }

        if (ProfileCache.TryGetValue(character.Guid, out var cachedProfile))
        {
            return cachedProfile;
        }

        var rulesetCharacter = character.RulesetCharacter;
        var family = GetFamily(rulesetCharacter);
        var hasRangedBackup = HasRangedAttackModes(character);
        var hasSpellcasting = HasSpellcasting(rulesetCharacter);
        var hasFlight = !rulesetCharacter.IsTouchingGround() ||
                        rulesetCharacter.MoveModes.ContainsKey((int)MoveMode.Fly);

        var profile = new CombatAiProfile(
            GetRole(character, hasRangedBackup, hasSpellcasting),
            family,
            GetTemperament(rulesetCharacter, family),
            hasFlight,
            hasRangedBackup,
            hasSpellcasting);

        ProfileCache[character.Guid] = profile;

        return profile;
    }

    internal static bool CanAttackInMeleeFromPosition(
        GameLocationCharacter attacker,
        int3 attackerPosition,
        GameLocationCharacter target,
        int3 targetPosition,
        IGameLocationBattleService battleService)
    {
        if (attacker?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        var canUseCache = IsAdvancedCombatAiEnabled;
        var cacheKey = canUseCache
            ? new AttackPositionKey(
                attacker.Guid,
                attackerPosition,
                target.Guid,
                targetPosition,
                ComputeCombatAiActionStateSignature(attacker),
                ComputeCombatAiTargetStateSignature(target))
            : default;

        if (canUseCache && MeleeAttackPositionCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        foreach (var mode in attacker.RulesetCharacter.AttackModes)
        {
            if (mode == null ||
                !ValidatorsWeapon.IsMelee(mode) ||
                !IsAttackModeAvailableForMainAction(attacker, mode, out _))
            {
                continue;
            }

            if (!battleService.IsWithinXCells(attacker, attackerPosition, target, targetPosition, mode.reachRange))
            {
                continue;
            }

            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();

            attackParams.FillForPhysicalReachAttack(
                attacker, attackerPosition, mode, target, targetPosition, modifier);

            if (battleService.CanAttack(attackParams))
            {
                if (canUseCache)
                {
                    MeleeAttackPositionCache[cacheKey] = true;
                }

                return true;
            }
        }

        if (canUseCache)
        {
            MeleeAttackPositionCache[cacheKey] = false;
        }

        return false;
    }

    internal static bool HasUsableRangedAttackAgainstVisibleEnemies(
        GameLocationCharacter character,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        foreach (var enemy in GetKnownEnemyTargets(character))
        {
            if (enemy?.RulesetCharacter == null)
            {
                continue;
            }

            if (TryGetRangedAttackModifierFromPosition(
                    character,
                    character.LocationPosition,
                    enemy,
                    enemy.LocationPosition,
                    battleService,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasAnyUsableAttackAgainstVisibleEnemies(
        GameLocationCharacter character,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        return HasAnyUsableMeleeAttackAgainstVisibleEnemies(character, character.LocationPosition, battleService) ||
               HasUsableRangedAttackAgainstVisibleEnemies(character, battleService);
    }

    internal static bool HasAnyUsableMeleeAttackAgainstVisibleEnemies(
        GameLocationCharacter character,
        int3 position,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        foreach (var enemy in GetKnownEnemyTargets(character))
        {
            if (enemy?.RulesetCharacter == null)
            {
                continue;
            }

            if (CanAttackInMeleeFromPosition(character, position, enemy, enemy.LocationPosition, battleService))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool CanUseFreeJumpForAi(GameLocationCharacter character)
    {
        if (!Main.Settings.EnableBonusActionFreeJump ||
            IsAdvancedCombatAiSuppressedByManualNpcControl(character) ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsAiControlledByGame(character) ||
            character?.RulesetCharacter == null ||
            character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself ||
            HasActiveDisconnectedSearchMoveCompletionSeal(character) ||
            HasCommittedBonusActionThisTurn(character) ||
            character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var profile = BuildProfile(character);

        return !ShouldPreferFlightOverFreeJump(character, profile, battleService);
    }

    private static bool TryExecuteAiFreeJumpTacticalMove(
        GameLocationCharacter character,
        int3 destination,
        CombatAiRouteMoveSourceKind source,
        bool observeActionLinkedCompletion = true)
    {
        if (!IsActiveBattleContender(character))
        {
            return false;
        }

        if (HasCommittedBonusActionThisTurn(character) ||
            character?.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available)
        {
            return false;
        }

        if (!CanUseFreeJumpForAi(character))
        {
            return false;
        }

        if (!FreeJumpContext.TryPrepareAiFreeJumpMove(character, destination))
        {
            return false;
        }

        if (observeActionLinkedCompletion)
        {
            _ = TryGetPendingActionLinkedMoveToken(character, source, destination, out var moveToken);
            character.MyExecuteActionTacticalMove(
                destination,
                aborted => OnAiTacticalMoveActionChainExecuted(character, aborted, source, moveToken));
        }
        else
        {
            character.MyExecuteActionTacticalMove(destination);
        }

        return true;
    }

    internal static bool IsAiPlacementBlockedOnlyByNonOccupyingCombatProxy(
        GameLocationCharacter character,
        int3 destination)
    {
        if (character?.RulesetCharacter == null || Gui.Battle == null)
        {
            return false;
        }

        var hasProxyBlocker = false;

        var contenders = Gui.Battle.AllContenders.ToArray();

        foreach (var contender in contenders)
        {
            if (contender == character ||
                contender is not { destroying: false, destroyedBody: false, RulesetCharacter: not null } ||
                contender.LocationPosition != destination)
            {
                continue;
            }

            if (!IsNonOccupyingCombatProxyTarget(contender))
            {
                return false;
            }

            hasProxyBlocker = true;
        }

        return hasProxyBlocker;
    }

    private static bool IsNonOccupyingCombatProxyTarget(GameLocationCharacter character)
    {
        return character?.RulesetCharacter is RulesetCharacterEffectProxy;
    }

    internal static bool TryEvaluateFreeJumpDestination(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        FreeJumpContext.FreeJumpCheckPreview preview,
        bool bypassesObstacle,
        CombatAiFreeJumpEvaluationSource evaluationSource,
        out float score)
    {
        score = 0f;

        if (!CanUseFreeJumpForAi(actor) || preview.IsAutomaticFailure)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (battleService == null || positioningService == null)
        {
            return false;
        }

        var profile = BuildProfile(actor);

        if (ShouldPreferFlightOverFreeJump(actor, profile, battleService))
        {
            return false;
        }

        var self = BuildSelfAssessment(actor);
        var currentFacts = CollectFreeJumpPositionFacts(actor, start, battleService, positioningService);
        var destinationFacts = CollectFreeJumpPositionFacts(actor, destination, battleService, positioningService);

        if (HasOpportunityAttackRisk(actor, start, destination, battleService))
        {
            return false;
        }

        if (!IsAdvancedCombatAiEnabled)
        {
            return TryEvaluateBaselineFreeJumpDestination(
                actor,
                start,
                destination,
                preview,
                bypassesObstacle,
                profile,
                self,
                currentFacts,
                destinationFacts,
                battleService,
                out score);
        }

        // Advanced AI selects tactical free jumps explicitly. Letting every validated
        // jump enter vanilla pathfinding turns ordinary walking routes into bonus-action
        // jumps, including flat adjacent steps.
        if (evaluationSource == CombatAiFreeJumpEvaluationSource.AiMovePathfinding &&
            !FreeJumpContext.IsForcedAiFreeJumpTarget(actor, destination))
        {
            return false;
        }

        // Free-jump candidates are evaluated while vanilla pathfinding is enumerating neighbours.
        // Keep this branch local and cache-only; starting another destination search here corrupts
        // the shared pathfinding result.
        var turnPlan = BuildCombatAiTurnPlan(actor, profile, battleService);

        if (IsGroundMeleePursuitPlan(turnPlan))
        {
            var target = turnPlan.ActionProbe.Target;
            var attackConnected = target?.RulesetCharacter != null &&
                                  (CanUseActionKindAtPosition(
                                       actor,
                                       destination,
                                       target,
                                       turnPlan.ActionProbe.PreferredAction,
                                       battleService) ||
                                   CanUseActionKindAtPosition(
                                       actor,
                                       destination,
                                       target,
                                       turnPlan.ActionProbe.BackupAction,
                                       battleService));

            if (!attackConnected)
            {
                var startDistance = ComputeGroundMeleeRouteGoalDistance(
                    start,
                    turnPlan.MovementPlan.TargetPosition);
                var destinationDistance = ComputeGroundMeleeRouteGoalDistance(
                    destination,
                    turnPlan.MovementPlan.TargetPosition);
                var advancesTowardContact = destinationDistance + 0.01f < startDistance;
                var bypassesWithoutRegression = bypassesObstacle &&
                                                destinationDistance <=
                                                startDistance + MovementGoalRegressionTolerance;

                if (!advancesTowardContact && !bypassesWithoutRegression)
                {
                    return false;
                }

                GroundMeleeJumpRouteAvailableCache.Add(actor.Guid);
            }
        }

        if (TryComputeTurnPlanMovementScore(
                actor,
                profile,
                destination,
                battleService,
                turnPlan,
                out var movementGoalScore))
        {
            if (movementGoalScore <= 0f && turnPlan.MovementPlan.HasGoal)
            {
                var currentGoalDistance = ComputeGridDistance(start, turnPlan.MovementPlan.TargetPosition);
                var destinationGoalDistance = ComputeGridDistance(destination, turnPlan.MovementPlan.TargetPosition);

                if (!bypassesObstacle ||
                    destinationGoalDistance > currentGoalDistance + MovementGoalRegressionTolerance)
                {
                    return false;
                }

                movementGoalScore = MovementGoalProgressScore;
            }

            score = Math.Max(score, movementGoalScore);
        }

        var hasEnemyFacts = currentFacts.HasPerceivedEnemy || destinationFacts.HasPerceivedEnemy;
        var emergency = self.IsCritical || self.IsBloodied || self.HasSeriousCondition || self.IsConcentrating;
        var canGainAttack = !currentFacts.CanAttack && destinationFacts.CanAttack;
        var reducesMeleeThreat = destinationFacts.MeleeThreatCount < currentFacts.MeleeThreatCount;
        var reducesRecentMeleeThreat =
            TryGetCurrentOrRecentMeleeThreat(actor, start, out _, out var recentThreatPosition, out _) &&
            !WouldBeInCurrentOrRecentMeleeThreat(actor, destination, battleService) &&
            ComputeGridDistance(destination, recentThreatPosition) >
            ComputeGridDistance(start, recentThreatPosition) + 0.5f;
        var improvesCover = destinationFacts.CoveredRangedThreatCount > currentFacts.CoveredRangedThreatCount;
        var movesCloser = hasEnemyFacts &&
                          destinationFacts.NearestEnemyDistance + 0.5f < currentFacts.NearestEnemyDistance;
        var movesAway = hasEnemyFacts &&
                        destinationFacts.NearestEnemyDistance > currentFacts.NearestEnemyDistance + 1.0f;

        if (hasEnemyFacts)
        {
            if (canGainAttack)
            {
                score += FreeJumpAttackAccessScore;
            }

            if (reducesMeleeThreat || reducesRecentMeleeThreat)
            {
                score += Math.Max(1, currentFacts.MeleeThreatCount - destinationFacts.MeleeThreatCount) *
                         FreeJumpThreatReductionScore;

                if (emergency)
                {
                    score += 0.10f;
                }
            }

            if (improvesCover)
            {
                score += FreeJumpCoverImprovementScore;
            }

            if (profile.PrefersAggressivePursuit && movesCloser)
            {
                score += Mathf.Min(
                    0.22f,
                    (currentFacts.NearestEnemyDistance - destinationFacts.NearestEnemyDistance) * 0.05f);
            }

            if ((profile.PrefersDistance || emergency) && movesAway)
            {
                score += Mathf.Min(
                    0.18f,
                    (destinationFacts.NearestEnemyDistance - currentFacts.NearestEnemyDistance) * 0.04f);
            }

            if (destination.y > start.y && (destinationFacts.CanAttack || profile.PrefersDistance))
            {
                score += FreeJumpHighGroundScore;
            }

            if (bypassesObstacle &&
                (canGainAttack || reducesMeleeThreat || reducesRecentMeleeThreat || improvesCover ||
                 movesCloser || movesAway))
            {
                score += FreeJumpObstacleBypassScore;
            }
        }
        else if (TryGetLastKnownEnemyPosition(actor, out var lastKnownEnemyPosition))
        {
            score += ComputeApproachPositionScore(
                start,
                destination,
                lastKnownEnemyPosition,
                Math.Max(ComputeGridDistance(start, lastKnownEnemyPosition), 1f),
                0.20f);
        }

        if (!IsFreeJumpRiskAcceptable(preview, score, emergency, bypassesObstacle))
        {
            return false;
        }

        var minimumScore = FreeJumpMinimumPositioningScore;

        if (canGainAttack || !HasAnyUsefulHostileActionAgainstVisibleEnemies(actor, battleService))
        {
            minimumScore = Math.Min(minimumScore, FreeJumpMinimumActionEconomyScore);
        }

        return score >= minimumScore;
    }

    private static bool TryEvaluateBaselineFreeJumpDestination(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        FreeJumpContext.FreeJumpCheckPreview preview,
        bool bypassesObstacle,
        CombatAiProfile profile,
        CombatAiSelfAssessment self,
        FreeJumpPositionFacts currentFacts,
        FreeJumpPositionFacts destinationFacts,
        IGameLocationBattleService battleService,
        out float score)
    {
        score = 0f;

        var hasEnemyFacts = currentFacts.HasPerceivedEnemy || destinationFacts.HasPerceivedEnemy;
        var emergency = self.IsCritical || self.IsBloodied || self.HasSeriousCondition || self.IsConcentrating;
        var canGainAttack = !currentFacts.CanAttack && destinationFacts.CanAttack;
        var reducesMeleeThreat = destinationFacts.MeleeThreatCount < currentFacts.MeleeThreatCount;
        var reducesRecentMeleeThreat =
            TryGetCurrentOrRecentMeleeThreat(actor, start, out _, out var recentThreatPosition, out _) &&
            !WouldBeInCurrentOrRecentMeleeThreat(actor, destination, battleService) &&
            ComputeGridDistance(destination, recentThreatPosition) >
            ComputeGridDistance(start, recentThreatPosition) + 0.5f;
        var movesCloser = hasEnemyFacts &&
                          destinationFacts.NearestEnemyDistance + 0.5f < currentFacts.NearestEnemyDistance;

        if (!hasEnemyFacts && !reducesRecentMeleeThreat)
        {
            return false;
        }

        if (canGainAttack)
        {
            score += FreeJumpAttackAccessScore;
        }

        if (movesCloser)
        {
            var improvement = currentFacts.NearestEnemyDistance - destinationFacts.NearestEnemyDistance;

            score += Mathf.Min(0.28f, improvement * (profile.PrefersAggressivePursuit ? 0.06f : 0.04f));
        }

        if (reducesMeleeThreat || reducesRecentMeleeThreat)
        {
            score += Math.Max(1, currentFacts.MeleeThreatCount - destinationFacts.MeleeThreatCount) *
                     FreeJumpThreatReductionScore;

            if (emergency)
            {
                score += 0.10f;
            }
        }

        if (destination.y > start.y && destinationFacts.CanAttack)
        {
            score += FreeJumpHighGroundScore;
        }

        if (bypassesObstacle && (canGainAttack || movesCloser || reducesMeleeThreat || reducesRecentMeleeThreat))
        {
            score += FreeJumpObstacleBypassScore;
        }

        if (!IsFreeJumpRiskAcceptable(preview, score, emergency, bypassesObstacle))
        {
            return false;
        }

        var minimumScore = FreeJumpMinimumBaselineScore;

        if (canGainAttack)
        {
            minimumScore = Math.Min(minimumScore, FreeJumpMinimumActionEconomyScore);
        }
        else if (bypassesObstacle && (movesCloser || reducesMeleeThreat || reducesRecentMeleeThreat))
        {
            minimumScore = Math.Min(minimumScore, FreeJumpMinimumPositioningScore);
        }

        return score >= minimumScore;
    }

    private static FreeJumpPositionFacts CollectFreeJumpPositionFacts(
        GameLocationCharacter actor,
        int3 position,
        IGameLocationBattleService battleService,
        IGameLocationPositioningService positioningService)
    {
        var hasPerceivedEnemy = false;
        var nearestEnemyDistance = float.MaxValue;
        var meleeThreatCount = 0;
        var coveredRangedThreatCount = 0;
        var canMeleeAttack = false;
        var canRangedAttack = false;
        var canSpellAttack = false;

        foreach (var enemy in GetKnownEnemyTargets(actor))
        {
            if (enemy?.RulesetCharacter == null || enemy.Side == actor.Side)
            {
                continue;
            }

            hasPerceivedEnemy = true;

            var distance = positioningService.ComputeDistanceBetweenCharactersApproximatingSize(
                actor, position, enemy, enemy.LocationPosition);

            if (distance < nearestEnemyDistance)
            {
                nearestEnemyDistance = distance;
            }

            if (CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, position, battleService))
            {
                meleeThreatCount++;
            }

            if (CanAttackInMeleeFromPosition(actor, position, enemy, enemy.LocationPosition, battleService))
            {
                canMeleeAttack = true;
            }

            if (TryGetRangedAttackModifierFromPosition(
                    actor,
                    position,
                    enemy,
                    enemy.LocationPosition,
                    battleService,
                    out _))
            {
                canRangedAttack = true;
            }

            if (TryGetAtWillSpellAttackModifierFromPosition(
                    actor,
                    position,
                    enemy,
                    enemy.LocationPosition,
                    battleService,
                    out _))
            {
                canSpellAttack = true;
            }

            if (TryGetRangedAttackModifierFromPosition(
                    enemy,
                    enemy.LocationPosition,
                    actor,
                    position,
                    battleService,
                    out var enemyModifier) &&
                enemyModifier?.coverType >= CoverType.Half)
            {
                coveredRangedThreatCount++;
            }
        }

        return new FreeJumpPositionFacts(
            hasPerceivedEnemy,
            nearestEnemyDistance,
            meleeThreatCount,
            coveredRangedThreatCount,
            canMeleeAttack || canRangedAttack || canSpellAttack,
            canMeleeAttack,
            canRangedAttack,
            canSpellAttack);
    }

    private static bool IsFreeJumpRiskAcceptable(
        FreeJumpContext.FreeJumpCheckPreview preview,
        float score,
        bool emergency,
        bool bypassesObstacle)
    {
        if (preview.IsAutomaticFailure)
        {
            return false;
        }

        if (!preview.RequiresAthleticsCheck)
        {
            return true;
        }

        var minimumSuccessChance = FreeJumpDefaultMinimumSuccessChance;

        if (emergency && score >= FreeJumpMinimumActionEconomyScore)
        {
            minimumSuccessChance = FreeJumpEmergencyMinimumSuccessChance;
        }
        else if (bypassesObstacle || score >= FreeJumpMinimumPositioningScore)
        {
            minimumSuccessChance = FreeJumpImprovedPositionMinimumSuccessChance;
        }

        return preview.SuccessChance >= minimumSuccessChance;
    }

    private static bool ShouldPreferFlightOverFreeJump(
        GameLocationCharacter character,
        CombatAiProfile profile,
        IGameLocationBattleService battleService)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (!IsAdvancedCombatAiFlightEnabled ||
            rulesetCharacter == null ||
            rulesetCharacter.IsTouchingGround() ||
            !profile.HasFlight)
        {
            return false;
        }

        if (profile.PrefersAerialCombat)
        {
            return true;
        }

        return !rulesetCharacter.IsTouchingGround() &&
               battleService != null &&
               HasAnyUsableAttackAgainstVisibleEnemies(character, battleService);
    }

    internal static bool TryTerminateIncapacitatedAiTurn(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter is not { IsIncapacitated: true } ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsAiControlledByGame(character) ||
            !IsActiveBattleContender(character))
        {
            return false;
        }

        // Let the owning AI coroutine finish naturally after the current activity. Ending the
        // battle turn from inside an action chain can leave movement or reaction work attached
        // to the next contender.
        ClearTurnCache(character);

        return true;
    }

    internal static IEnumerator RunAdvancedCombatAiTurn(
        GameLocationCharacter character,
        IEnumerator vanillaTurn)
    {
        yield return PrepareAdvancedCombatAiTurnBeforeVanilla(character);

        if (TryTerminateIncapacitatedAiTurn(character) ||
            !IsActiveBattleContender(character))
        {
            yield break;
        }

        while (vanillaTurn.MoveNext())
        {
            yield return vanillaTurn.Current;

            if (TryTerminateIncapacitatedAiTurn(character) ||
                !IsActiveBattleContender(character))
            {
                yield break;
            }
        }

        if (TryTerminateIncapacitatedAiTurn(character))
        {
            yield break;
        }

        yield return CompleteAdvancedCombatAiTurnAtProcessBoundary(character);
    }

    private static IEnumerator PrepareAdvancedCombatAiTurnBeforeVanilla(
        GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsActiveBattleContender(character))
        {
            yield break;
        }

        EnsureCombatAiRuntimeCache();

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            yield break;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        var dashCandidate = EvaluateBonusDashMeleeEngagement(
            character,
            profile,
            turnPlan,
            battleService);

        if (dashCandidate.IsAvailable)
        {
            var bonusUseCountBefore = GetActionUseCount(TurnBonusActionUseCountCache, character);
            var remainingMovesBefore = character.RemainingTacticalMoves;

            if (TryExecuteBonusDash(character))
            {
                yield return WaitForCombatAiProcessAction(character);

                if (!IsActiveBattleContender(character))
                {
                    yield break;
                }

                var dashAccepted =
                    GetActionUseCount(TurnBonusActionUseCountCache, character) > bonusUseCountBefore ||
                    character.RemainingTacticalMoves > remainingMovesBefore;

                if (dashAccepted &&
                    TryExecuteBonusDashMeleeMove(
                        character,
                        dashCandidate,
                        battleService))
                {
                    yield return WaitForCombatAiProcessAction(character);

                    if (!IsActiveBattleContender(character))
                    {
                        yield break;
                    }

                }

                InvalidateTurnPlanningCache(character);
                profile = BuildProfile(character);
                turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);
            }
        }

        const int maximumPreMainMoveAttempts = 2;

        for (var attempt = 1; attempt <= maximumPreMainMoveAttempts; attempt++)
        {
            if (!ShouldUsePreMainRouteMove(character, turnPlan) ||
                turnPlan.ActionProbe.CanUsePreferredAction)
            {
                yield break;
            }

            var remainingMovesBefore = character.RemainingTacticalMoves;

            if (!TryStartResidualEngagementMove(
                    character,
                    battleService,
                    profile,
                    turnPlan,
                    out var start))
            {
                yield break;
            }
            yield return WaitForCombatAiProcessAction(character);

            if (!IsActiveBattleContender(character))
            {
                yield break;
            }

            UpdateTurnMovementProgress(character);

            var remainingMovesAfter = character.RemainingTacticalMoves;
            var returnedToStart = character.LocationPosition == start;

            if (!returnedToStart)
            {
                yield break;
            }

            var canReplan =
                attempt < maximumPreMainMoveAttempts &&
                remainingMovesAfter > 0 &&
                remainingMovesAfter < remainingMovesBefore &&
                BuildActionEconomySnapshot(character).MainAvailable;

            if (!canReplan)
            {
                yield break;
            }

            InvalidateTurnPlanningCache(character);
            profile = BuildProfile(character);
            turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

            if (!ShouldUsePreMainRouteMove(character, turnPlan) ||
                turnPlan.ActionProbe.CanUsePreferredAction)
            {
                yield break;
            }
        }
    }

    private static BonusDashMeleeCandidate EvaluateBonusDashMeleeEngagement(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            turnPlan.ActionProbe.PreferredAction != CombatAiActionKind.Melee ||
            !ShouldPreferMeleeAction(profile, turnPlan.ActionProbe.CapabilityCatalog))
        {
            return default;
        }

        if (turnPlan.ActionProbe.CanUsePreferredAction)
        {
            return default;
        }

        if (!BuildActionEconomySnapshot(character).MainAvailable ||
            character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available ||
            character.GetActionStatus(Id.DashBonus, ActionScope.Battle) != ActionStatus.Available ||
            HasCommittedBonusActionThisTurn(character))
        {
            return default;
        }

        var start = character.LocationPosition;
        var remainingMove = Math.Max(0, character.RemainingTacticalMoves);
        var dashMoveBudget = remainingMove + Math.Max(0, character.MaxTacticalMoves);

        if (TryFindJumpImmediateAttackCandidate(
                character,
                turnPlan,
                battleService,
                out _) ||
            dashMoveBudget <= remainingMove)
        {
            return default;
        }

        if (!TryGetReachableRouteDestinations(
                character,
                start,
                dashMoveBudget,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: true))
        {
            return default;
        }

        var enemies = GetKnownEnemyTargets(character)
            .Where(enemy => enemy?.RulesetCharacter != null && enemy.Side != character.Side)
            .OrderByDescending(enemy => enemy == turnPlan.ActionProbe.Target)
            .ThenBy(enemy => ComputeGridDistance(start, enemy.LocationPosition))
            .ThenBy(enemy => enemy.Guid)
            .ToArray();

        foreach (var position in reachableDestinations.Positions
                     .OrderBy(reachableDestinations.GetMoveCost)
                     .ThenBy(position => position.x)
                     .ThenBy(position => position.y)
                     .ThenBy(position => position.z))
        {
            var candidateMoveCost = reachableDestinations.GetMoveCost(position);

            if (candidateMoveCost > dashMoveBudget ||
                position == start ||
                !IsLegalAiRouteDestination(character, position) ||
                IsRejectedAiMoveTarget(character, start, position) ||
                HasForcedRouteOpportunityExposure(character, start, position, battleService))
            {
                continue;
            }

            if (candidateMoveCost <= remainingMove)
            {
                if (enemies.Any(enemy => CanUseActionKindAtPosition(
                        character,
                        position,
                        enemy,
                        CombatAiActionKind.Melee,
                        battleService)))
                {
                    return default;
                }

                continue;
            }

            foreach (var enemy in enemies)
            {
                if (!CanUseActionKindAtPosition(
                        character,
                        position,
                        enemy,
                        CombatAiActionKind.Melee,
                        battleService))
                {
                    continue;
                }

                return new BonusDashMeleeCandidate(enemy, position);
            }
        }

        return default;
    }

    private static bool TryExecuteBonusDash(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsActiveBattleContender(character) ||
            character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available ||
            character.GetActionStatus(Id.DashBonus, ActionScope.Battle) != ActionStatus.Available ||
            ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService)
        {
            return false;
        }

        actionService.ExecuteAction(new CharacterActionParams(character, Id.DashBonus), null, true);
        return true;
    }

    private static bool TryExecuteBonusDashMeleeMove(
        GameLocationCharacter character,
        BonusDashMeleeCandidate candidate,
        IGameLocationBattleService battleService)
    {
        var target = candidate.Target;
        var destination = candidate.Destination;

        if (character?.RulesetCharacter == null ||
            !candidate.IsAvailable ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsActiveBattleContender(character) ||
            !TryValidateAiTacticalMoveIssue(character, destination, requireCanDecide: true) ||
            !TryGetReachableRouteDestinations(
                character,
                character.LocationPosition,
                Math.Max(0, character.RemainingTacticalMoves),
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: true) ||
            !reachableDestinations.Contains(destination) ||
            !CanUseActionKindAtPosition(
                character,
                destination,
                target,
                CombatAiActionKind.Melee,
                battleService))
        {
            return false;
        }

        var moveToken = CreateActionLinkedMoveToken();
        var pendingMove = new ActionLinkedMoveMemory(
            target,
            CombatAiActionKind.Melee,
            CombatAiActionLinkedMoveContinuation.ImmediateResidualAction,
            CombatAiMovementGoalKind.AdvanceToMelee,
            character.LocationPosition,
            destination,
            CombatAiRouteMoveSourceKind.BonusDash,
            false,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            moveToken);

        ActionLinkedMoveCache[character.Guid] = pendingMove;

        CharacterAction.ActionChainExecutedHandler actionChainExecuted = aborted =>
            OnAiTacticalMoveActionChainExecuted(
                character,
                aborted,
                CombatAiRouteMoveSourceKind.BonusDash,
                moveToken);

        if (character.MyExecuteActionTacticalMove(destination, actionChainExecuted, null))
        {
            return true;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        return false;
    }

    private static IEnumerator CompleteAdvancedCombatAiTurnAtProcessBoundary(
        GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsActiveBattleContender(character))
        {
            yield break;
        }

        EnsureCombatAiRuntimeCache();

        if (!HasQueuedActionChain(character) &&
            !HasPendingReactionRequests() &&
            !character.MoveStepInProgress)
        {
            MovementTracker.ClearMovement(character);
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            yield break;
        }

        var hostileAttemptsRemaining = GetResidualHostileActionAttemptLimit(character);

        while (hostileAttemptsRemaining > 0 && IsActiveBattleContender(character))
        {
            var actionEconomy = BuildActionEconomySnapshot(character);

            if (!actionEconomy.MainAvailable)
            {
                break;
            }

            var mainUseCountBefore = GetActionUseCount(TurnMainActionUseCountCache, character);
            var availableIterationsBefore = character.GetActionAvailableIterations(Id.AttackMain);
            var usedMainAttacksBefore = character.UsedMainAttacks;
            var mainStatusBefore = character.GetActionTypeStatus(ActionType.Main);

            var hostileAction = TryUseCurrentPositionHostileMainAction(
                character,
                battleService);

            if (!hostileAction.Executed)
            {
                break;
            }

            hostileAttemptsRemaining--;
            yield return WaitForCombatAiProcessAction(character);

            if (!IsActiveBattleContender(character))
            {
                yield break;
            }

            if (HasResidualHostileActionProgress(
                    character,
                    mainUseCountBefore,
                    availableIterationsBefore,
                    usedMainAttacksBefore,
                    mainStatusBefore))
            {
                continue;
            }

            PendingResidualMainActionCache.Remove(character.Guid);
            break;
        }

        if (TryStartResidualEngagementMove(
                character,
                battleService,
                out _))
        {
            yield return WaitForCombatAiProcessAction(character);

            if (!IsActiveBattleContender(character))
            {
                yield break;
            }

            UpdateTurnMovementProgress(character);
        }

        if (TryStartPostMainClearAllyCorridorMove(character, battleService, out _))
        {
            yield return WaitForCombatAiProcessAction(character);

            if (!IsActiveBattleContender(character))
            {
                yield break;
            }

        }

        var residualEconomy = BuildActionEconomySnapshot(character);
        var remainingMovesBeforeBonus = character.RemainingTacticalMoves;

        if (TrySpendLeftoverBonusActionEconomy(character, residualEconomy, battleService))
        {
            yield return WaitForCombatAiProcessAction(character);

            if (!IsActiveBattleContender(character))
            {
                yield break;
            }

            if (character.RemainingTacticalMoves > remainingMovesBeforeBonus &&
                TryStartResidualEngagementMove(
                    character,
                    battleService,
                    out _))
            {
                yield return WaitForCombatAiProcessAction(character);

                if (!IsActiveBattleContender(character))
                {
                    yield break;
                }

                UpdateTurnMovementProgress(character);
            }
        }

        var profile = BuildProfile(character);
        var self = BuildSelfAssessment(character);
        var turnPlan = BuildTerminalActionPlan(character, profile);

        if (TryUseFallbackAtWillSelfBuff(
                character,
                self,
                turnPlan,
                clearTurnCacheAfterAction: false))
        {
            yield return WaitForCombatAiProcessAction(character);

            if (!IsActiveBattleContender(character))
            {
                yield break;
            }

        }

        while (hostileAttemptsRemaining > 0 && IsActiveBattleContender(character))
        {
            var actionEconomy = BuildActionEconomySnapshot(character);

            if (!actionEconomy.MainAvailable)
            {
                break;
            }

            var mainUseCountBefore = GetActionUseCount(TurnMainActionUseCountCache, character);
            var availableIterationsBefore = character.GetActionAvailableIterations(Id.AttackMain);
            var usedMainAttacksBefore = character.UsedMainAttacks;
            var mainStatusBefore = character.GetActionTypeStatus(ActionType.Main);
            var hostileAction = TryUseCurrentPositionHostileMainAction(
                character,
                battleService);

            if (!hostileAction.Executed)
            {
                break;
            }

            hostileAttemptsRemaining--;
            yield return WaitForCombatAiProcessAction(character);

            if (!IsActiveBattleContender(character))
            {
                yield break;
            }

            if (HasResidualHostileActionProgress(
                    character,
                    mainUseCountBefore,
                    availableIterationsBefore,
                    usedMainAttacksBefore,
                    mainStatusBefore))
            {
                continue;
            }

            PendingResidualMainActionCache.Remove(character.Guid);
            break;
        }

        var terminalEconomy = BuildActionEconomySnapshot(character);

        if (!CanSpendTerminalMainAction(character, terminalEconomy))
        {
            yield break;
        }

        profile = BuildProfile(character);
        turnPlan = BuildTerminalActionPlan(character, profile);
        self = BuildSelfAssessment(character);
        var terminalScan = BuildCurrentTerminalActionScan(
            character,
            turnPlan.ActionProbe,
            battleService,
            self);

        if (!CanUseTerminalReadyDodgeFallback(
                character,
                profile,
                turnPlan,
                battleService,
                terminalScan))
        {
            yield break;
        }

        if (TryUseFallbackReady(
                character,
                profile,
                turnPlan,
                battleService,
                clearTurnCacheAfterAction: false))
        {
            yield return WaitForCombatAiProcessAction(character);
            yield break;
        }

        if (!TryApplyFallbackDodge(character))
        {
            yield break;
        }

        yield return WaitForCombatAiProcessAction(character);
    }

    private static bool HasResidualHostileActionProgress(
        GameLocationCharacter character,
        int mainUseCountBefore,
        int availableIterationsBefore,
        int usedMainAttacksBefore,
        ActionStatus mainStatusBefore)
    {
        return character != null &&
               (GetActionUseCount(TurnMainActionUseCountCache, character) > mainUseCountBefore ||
                character.GetActionAvailableIterations(Id.AttackMain) < availableIterationsBefore ||
                character.UsedMainAttacks > usedMainAttacksBefore ||
                character.GetActionTypeStatus(ActionType.Main) != mainStatusBefore);
    }

    private static bool TryStartResidualEngagementMove(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        out int3 start,
        bool requireMainAction = true)
    {
        start = character?.LocationPosition ?? default;

        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        return TryStartResidualEngagementMove(
            character,
            battleService,
            profile,
            turnPlan,
            out start,
            requireMainAction);
    }

    private static bool TryStartResidualEngagementMove(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        out int3 start,
        bool requireMainAction = true)
    {
        start = character?.LocationPosition ?? default;
        var destination = default(int3);

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsActiveBattleContender(character) ||
            HasQueuedActionChain(character) ||
            HasPendingReactionRequests())
        {
            return false;
        }

        if (!turnPlan.MovementPlan.HasGoal ||
            turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.ClearLargeAllyPath)
        {
            return false;
        }

        if (requireMainAction && !BuildActionEconomySnapshot(character).MainAvailable)
        {
            return false;
        }

        if (character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available)
        {
            if (requireMainAction)
            {
                RecordPreMainRouteMoveAttempt(
                    character,
                    CombatAiPreMainRouteMoveStatus.Unavailable,
                    turnPlan.MovementPlan.Goal,
                    default);
            }

            return false;
        }

        PrimeTurnMovementProgress(character, turnPlan);

        if (IsGroundMeleePursuitPlan(turnPlan) &&
            TryFindJumpImmediateAttackCandidate(
                character,
                turnPlan,
                battleService,
                out var jumpDestination))
        {
            var jumpCost = FreeJumpContext.ComputeAiFreeJumpMovementCost(start, jumpDestination);
            var hasSafeWalkContact = TryGetSafeGroundMeleeWalkContactCost(
                character,
                profile,
                turnPlan,
                battleService,
                out var walkCost);
            var useJump = !hasSafeWalkContact ||
                          walkCost - jumpCost >= FreeJumpMinimumContactRouteSavings;

            if (useJump &&
                TryExecuteAiFreeJumpTacticalMove(
                    character,
                    jumpDestination,
                    CombatAiRouteMoveSourceKind.JumpImmediateAttack,
                    observeActionLinkedCompletion: false))
            {
                JumpImmediateAttackReachableCache.Remove(character.Guid);
                destination = jumpDestination;
                TurnMovementProgressCache.TryGetValue(character.Guid, out var jumpProgress);
                jumpProgress?.MarkFreeJumpMovementCandidate();

                if (requireMainAction)
                {
                    RecordPreMainRouteMoveAttempt(
                        character,
                        CombatAiPreMainRouteMoveStatus.Executed,
                        turnPlan.MovementPlan.Goal,
                        destination);
                }

                return true;
            }
        }

        JumpImmediateAttackReachableCache.Remove(character.Guid);

        var hasTerminalMain = BuildActionEconomySnapshot(character).MainAvailable;
        var routeContinuation = hasTerminalMain
            ? CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove
            : CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision;

        if (IsConnectedFiringLinePlan(turnPlan))
        {
            if (hasTerminalMain &&
                TryUseConnectedFiringLineRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    routeContinuation,
                    requireActionAfterMove: true,
                    routeMoveSource: CombatAiRouteMoveSourceKind.ConnectedFiringLine,
                    closeOnFailure: false,
                    out destination,
                    out _))
            {
                RecordPreMainRouteMoveIfRequired(
                    character,
                    turnPlan,
                    requireMainAction,
                    CombatAiPreMainRouteMoveStatus.Executed,
                    destination);
                return true;
            }

            if (TryUseLostTargetSearchRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    routeContinuation,
                    CombatAiRouteMoveSourceKind.SearchLostTarget,
                    out destination))
            {
                RecordPreMainRouteMoveIfRequired(
                    character,
                    turnPlan,
                    requireMainAction,
                    CombatAiPreMainRouteMoveStatus.Executed,
                    destination);
                return true;
            }

            RecordPreMainRouteMoveIfRequired(
                character,
                turnPlan,
                requireMainAction,
                CombatAiPreMainRouteMoveStatus.Blocked,
                default);
            return false;
        }

        if (IsSearchKnownTargetPlan(turnPlan))
        {
            if (TryUseLostTargetSearchRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    routeContinuation,
                    CombatAiRouteMoveSourceKind.SearchLostTarget,
                    out destination))
            {
                RecordPreMainRouteMoveIfRequired(
                    character,
                    turnPlan,
                    requireMainAction,
                    CombatAiPreMainRouteMoveStatus.Executed,
                    destination);
                return true;
            }

            RecordPreMainRouteMoveIfRequired(
                character,
                turnPlan,
                requireMainAction,
                CombatAiPreMainRouteMoveStatus.Blocked,
                default);
            return false;
        }

        if (!turnPlan.IsAttackContinuation)
        {
            PrimeGroundMeleeTargetContactRouteQuery(character, turnPlan);
        }

        var walkOnly = IsGroundMeleePursuitPlan(turnPlan);

        if (!TryGetReachableRouteDestinations(
                character,
                start,
                character.RemainingTacticalMoves,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: walkOnly))
        {
            RecordPreMainRouteMoveIfRequired(
                character,
                turnPlan,
                requireMainAction,
                CombatAiPreMainRouteMoveStatus.Blocked,
                default);

            return false;
        }

        foreach (var candidate in reachableDestinations.Positions)
        {
            _ = TryComputeTurnPlanMovementScore(
                character,
                profile,
                candidate,
                battleService,
                turnPlan,
                out _);
        }

        if (!TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress) ||
            !movementProgress.TryGetBestAcceptedMoveCandidate(out var bestCandidate) ||
            bestCandidate.Position == start ||
            !reachableDestinations.Contains(bestCandidate.Position) ||
            !TryValidateAiTacticalMoveIssue(character, bestCandidate.Position, requireCanDecide: true))
        {
            RecordPreMainRouteMoveIfRequired(
                character,
                turnPlan,
                requireMainAction,
                CombatAiPreMainRouteMoveStatus.Blocked,
                default);

            return false;
        }

        destination = bestCandidate.Position;
        FreeJumpContext.SuppressAiFreeJumpForNextMove(character, destination);
        character.MyExecuteActionTacticalMove(destination);
        RecordPreMainRouteMoveIfRequired(
            character,
            turnPlan,
            requireMainAction,
            CombatAiPreMainRouteMoveStatus.Executed,
            destination);

        return true;
    }

    private static void RecordPreMainRouteMoveIfRequired(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        bool required,
        CombatAiPreMainRouteMoveStatus status,
        int3 destination)
    {
        if (required)
        {
            RecordPreMainRouteMoveAttempt(
                character,
                status,
                turnPlan.MovementPlan.Goal,
                destination);
        }
    }

    private static int GetResidualHostileActionAttemptLimit(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return 0;
        }

        var rank = character.CurrentActionRankByType[ActionType.Main];
        var allowedAttacks = Math.Max(0, character.GetAllowedMainAttacksForRank(rank));
        var observedAttacks = Math.Max(0, character.UsedMainAttacks);

        if (TryGetCurrentTurnActionMemory(character, LastMainActionExecutionCache, out var lastMainAction) &&
            lastMainAction.ActionId == Id.AttackMain &&
            lastMainAction.ActionRank == rank)
        {
            observedAttacks = Math.Max(
                observedAttacks,
                GetActionUseCount(TurnMainActionUseCountCache, character));
        }

        var unusedAllowedAttacks = Math.Max(0, allowedAttacks - observedAttacks);
        var availableIterations = Math.Max(0, character.GetActionAvailableIterations(Id.AttackMain));

        return Math.Max(1, allowedAttacks > 0 ? unusedAllowedAttacks : availableIterations);
    }

    private static IEnumerator WaitForCombatAiProcessAction(GameLocationCharacter character)
    {
        // MyExecuteAction can publish its chain on the following frame. Keep the AI turn
        // coroutine alive until that publication boundary so the action cannot leak into
        // the next contender's turn.
        yield return null;

        while (IsActiveBattleContender(character) &&
               (HasAnyQueuedActionChain() ||
                HasPendingReactionRequests() ||
                character.MoveStepInProgress))
        {
            yield return null;
        }
    }

    private static bool TryStartPostMainClearAllyCorridorMove(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        out int3 destination)
    {
        destination = default;

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsActiveBattleContender(character) ||
            !HasCommittedMainActionThisTurn(character) ||
            BuildActionEconomySnapshot(character).MainAvailable ||
            HasAvailableAttackMainContinuation(character) ||
            character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available ||
            HasQueuedActionChain(character) ||
            HasPendingReactionRequests())
        {
            return false;
        }

        var profile = BuildProfile(character);

        return TryBuildPostMainClearAllyCorridorTurnPlan(
                   character,
                   profile,
                   battleService,
                   out var turnPlan) &&
               !TryBlockRepeatedPostMainClearAllyCorridorAttempt(character) &&
               TryUsePostMainClearAllyCorridorMove(
                   character,
                   turnPlan,
                   battleService,
                   out destination);
    }

    private static CombatAiResidualHostileActionResult TryUseCurrentPositionHostileMainAction(
        GameLocationCharacter character,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsActiveBattleContender(character))
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Unavailable);
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);
        var result = TryUseResidualSafeHostileAction(
            character,
            turnPlan.ActionProbe,
            battleService);

        if (result.Executed || result.PolicyHeld)
        {
            return result;
        }

        if (turnPlan.ActionProbe.CanUsePreferredAction ||
            turnPlan.ActionProbe.CanUseBackupAction)
        {
            return result;
        }

        var targets = GetCurrentHostileActionTargets(character, battleService)
            .Where(target => target?.RulesetCharacter != null && target.Side != character.Side)
            .OrderBy(target => CanAttackInMeleeFromPosition(
                character,
                character.LocationPosition,
                target,
                target.LocationPosition,
                battleService)
                ? 0
                : 1)
            .ThenBy(target => ComputeGridDistance(character.LocationPosition, target.LocationPosition))
            .ThenBy(target => target.Guid)
            .ToArray();

        foreach (var target in targets)
        {
            if (target?.RulesetCharacter == null || target.Side == character.Side)
            {
                continue;
            }

            if (TryUseCurrentHostilePowerMainAction(
                    character,
                    target,
                    battleService))
            {
                return new CombatAiResidualHostileActionResult(
                    CombatAiResidualHostileActionResultKind.Executed);
            }
        }

        return result;
    }

    private static bool TryUseCurrentHostilePowerMainAction(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            target.Side == character.Side ||
            battleService == null ||
            character.GetActionStatus(Id.PowerMain, ActionScope.Battle) != ActionStatus.Available ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available ||
            TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction) &&
            !HasUnspentMainActionRank(
                character,
                committedMainAction,
                character.GetActionTypeStatus(ActionType.Main)))
        {
            return false;
        }

        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

        if (actionService == null || implementationService == null)
        {
            return false;
        }

        var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);

        foreach (var usablePower in rulesetCharacter.UsablePowers)
        {
            var power = usablePower?.PowerDefinition;
            var effectDescription = power?.EffectDescription;

            if (power == null ||
                effectDescription == null ||
                power.ActivationTime != ActivationTime.Action ||
                effectDescription.TargetSide != Side.Enemy ||
                effectDescription.TargetType is not (TargetType.Individuals or TargetType.IndividualsUnique) ||
                !rulesetCharacter.CanUsePower(power, true, true) ||
                (effectDescription.RangeParameter > 0f && distance > effectDescription.RangeParameter + 0.5f))
            {
                continue;
            }

            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();

            attackParams.FillForMagic(
                character,
                character.LocationPosition,
                effectDescription,
                power.Name,
                target,
                target.LocationPosition,
                modifier);

            if (!battleService.CanAttack(attackParams))
            {
                continue;
            }

            var actionParams = new CharacterActionParams(character, Id.PowerMain)
            {
                ActionModifiers = { modifier },
                RulesetEffect = implementationService.InstantiateEffectPower(
                    rulesetCharacter,
                    usablePower,
                    false),
                UsablePower = usablePower,
                TargetCharacters = { target }
            };

            MarkPendingResidualMainAction(character, Id.PowerMain);
            return TryExecuteResidualHostileAction(
                actionService,
                actionParams);
        }

        return false;
    }

    private static CombatAiTurnPlan BuildTerminalActionPlan(
        GameLocationCharacter character,
        CombatAiProfile profile)
    {
        var capabilityCatalog = BuildCapabilityCatalog(character);
        var preferredAction = GetPreferredActionKind(character, profile, capabilityCatalog);
        var backupAction = GetBackupActionKind(character, preferredAction, capabilityCatalog);
        var actionProbe = new CombatAiActionProbe(
            preferredAction,
            backupAction,
            null,
            false,
            false,
            capabilityCatalog.HasAtWillHostileSpell,
            capabilityCatalog);
        var movementPlan = new CombatAiMovementPlan(
            CombatAiMovementGoalKind.None,
            CombatAiMovementPolicyKind.None,
            null,
            character?.LocationPosition ?? default);

        return new CombatAiTurnPlan(
            actionProbe,
            movementPlan,
            HasRemainingAttackMainContinuation(character));
    }

    internal static void TryUseBaselineFreeJumpRouteMove(GameLocationCharacter character)
    {
        EnsureCombatAiRuntimeCache();

        if (IsAdvancedCombatAiEnabled ||
            !Main.Settings.EnableBonusActionFreeJump ||
            !IsAiControlledByGame(character) ||
            !IsActiveBattleContender(character) ||
            character?.RulesetCharacter == null)
        {
            return;
        }

        if (BaselineFreeJumpAttemptCache.TryGetValue(character.Guid, out var previousAttempt) &&
            previousAttempt.Round == GetCurrentBattleRound() &&
            previousAttempt.Start == character.LocationPosition)
        {
            return;
        }

        var actionEconomy = BuildActionEconomySnapshot(character);

        if (actionEconomy.Bonus != ActionStatus.Available ||
            actionEconomy.TacticalMove != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0)
        {
            RecordBaselineFreeJumpAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Unavailable,
                default);
            return;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            RecordBaselineFreeJumpAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Unavailable,
                default);
            return;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        if (turnPlan.ActionProbe.CanUsePreferredAction || turnPlan.ActionProbe.CanUseBackupAction)
        {
            RecordBaselineFreeJumpAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Unavailable,
                default);
            return;
        }

        var bestDestination = default(int3);
        var bestScore = float.MinValue;
        var found = false;

        FreeJumpContext.TryEnumerateUsefulAiFreeJumpDestinations(
            character,
            (destination, score) =>
            {
                var actionAccessScore = 0f;

                if (turnPlan.ActionProbe.Target?.RulesetCharacter != null)
                {
                    if (CanUseActionKindAtPosition(
                            character,
                            destination,
                            turnPlan.ActionProbe.Target,
                            turnPlan.ActionProbe.PreferredAction,
                            battleService))
                    {
                        actionAccessScore = 0.30f;
                    }
                    else if (CanUseActionKindAtPosition(
                                 character,
                                 destination,
                                 turnPlan.ActionProbe.Target,
                                 turnPlan.ActionProbe.BackupAction,
                                 battleService))
                    {
                        actionAccessScore = 0.12f;
                    }
                }

                var candidateScore =
                    score +
                    actionAccessScore +
                    ComputeStableTieBreakScore(
                        character,
                        turnPlan,
                        destination,
                        turnPlan.ActionProbe.PreferredAction);

                if (found && candidateScore <= bestScore + 0.000001f)
                {
                    return true;
                }

                found = true;
                bestScore = candidateScore;
                bestDestination = destination;

                return true;
            });

        if (!found)
        {
            RecordBaselineFreeJumpAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Unavailable,
                default);
            return;
        }

        var moveToken = CreateActionLinkedMoveToken();
        ActionLinkedMoveCache[character.Guid] = new ActionLinkedMoveMemory(
            turnPlan.ActionProbe.Target,
            CombatAiActionKind.None,
            CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision,
            CombatAiMovementGoalKind.None,
            character.LocationPosition,
            bestDestination,
            CombatAiRouteMoveSourceKind.FreeJump,
            false,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            moveToken);

        if (!TryExecuteAiFreeJumpTacticalMove(
                character,
                bestDestination,
                CombatAiRouteMoveSourceKind.FreeJump))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            RecordBaselineFreeJumpAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Blocked,
                bestDestination);
            return;
        }

        RecordBaselineFreeJumpAttempt(
            character,
            CombatAiPreMainRouteMoveStatus.Executed,
            bestDestination);
    }

    private static void RecordBaselineFreeJumpAttempt(
        GameLocationCharacter character,
        CombatAiPreMainRouteMoveStatus status,
        int3 destination)
    {
        if (character == null)
        {
            return;
        }

        BaselineFreeJumpAttemptCache[character.Guid] = new BaselineFreeJumpAttemptMemory(
            status,
            character.LocationPosition,
            destination,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool ShouldUsePreMainRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (!turnPlan.MovementPlan.HasGoal)
        {
            return false;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat)
        {
            return true;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            return true;
        }

        if (IsGroundMeleePursuitPlan(turnPlan) &&
            character?.RulesetCharacter != null &&
            character.RemainingTacticalMoves > 0 &&
            character.CanDecideToMoveByItself &&
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) == ActionStatus.Available)
        {
            return true;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.AdvanceToMelee &&
            turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.FlyingPursuitPolicy &&
            TurnMovementProgressCache.TryGetValue(character.Guid, out var flyingMovementProgress) &&
            flyingMovementProgress.HasAcceptedMoveCandidate)
        {
            return true;
        }

        if (turnPlan.MovementPlan.Goal is not (CombatAiMovementGoalKind.MoveToPreferredRange or
                CombatAiMovementGoalKind.SearchKnownTarget))
        {
            return false;
        }

        if (IsSearchKnownTargetPlan(turnPlan))
        {
            return !turnPlan.ActionProbe.CanUsePreferredAction &&
                   !turnPlan.ActionProbe.CanUseBackupAction;
        }

        if (turnPlan.MovementPlan.Policy is not (CombatAiMovementPolicyKind.SpellLinePolicy or
                CombatAiMovementPolicyKind.RangedLinePolicy))
        {
            return false;
        }

        if (IsImproveFiringPositionPlan(turnPlan))
        {
            return true;
        }

        return !turnPlan.ActionProbe.CanUsePreferredAction &&
               !turnPlan.ActionProbe.CanUseBackupAction;
    }

    private static bool IsGroundMeleePursuitPlan(CombatAiTurnPlan turnPlan)
    {
        return turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.AdvanceToMelee &&
               turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.MeleePursuitPolicy &&
               turnPlan.ActionProbe.PreferredAction == CombatAiActionKind.Melee &&
               !turnPlan.ActionProbe.CanUsePreferredAction;
    }

    private static bool IsSearchKnownTargetPlan(CombatAiTurnPlan turnPlan)
    {
        return turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.SearchKnownTarget &&
               turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.SearchKnownTargetPolicy;
    }

    private static bool TryGetSameTurnNoMoveProxyThreatAttempt(
        GameLocationCharacter character,
        out ProxyThreatRouteAttemptMemory attempt)
    {
        attempt = default;

        if (character?.RulesetCharacter == null ||
            !ProxyThreatRouteAttemptCache.TryGetValue(character.Guid, out attempt) ||
            !attempt.NoMove)
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (currentRound == attempt.Round && currentTurnStamp == attempt.TurnStamp)
        {
            return true;
        }

        ProxyThreatRouteAttemptCache.Remove(character.Guid);
        attempt = default;

        return false;
    }

    private static bool TryUseConnectedFiringLineRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        CombatAiActionLinkedMoveContinuation continuation,
        bool requireActionAfterMove,
        CombatAiRouteMoveSourceKind routeMoveSource,
        bool closeOnFailure,
        out int3 destination,
        out bool closed)
    {
        destination = default;
        closed = false;

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsConnectedFiringLinePlan(turnPlan))
        {
            return false;
        }

        var start = character.LocationPosition;
        var remainingMove = Math.Max(0, character.RemainingTacticalMoves);

        if (!TryGetReachableRouteDestinations(
                character,
                start,
                remainingMove,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: false))
        {
            destination = start;
            if (closeOnFailure)
            {
                CloseFailedConnectedFiringLineRoute(
                    character,
                    turnPlan);
                closed = true;
            }

            return false;
        }

        _ = GetOrCreateTurnMovementProgress(character, turnPlan);
        var candidates = new List<ConnectedFiringLineCandidate>();

        foreach (var position in reachableDestinations.Positions)
        {
            var canPreferred = CanUseActionKindAtPosition(
                character,
                position,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.PreferredAction,
                battleService);
            var canBackup =
                !canPreferred &&
                turnPlan.ActionProbe.BackupAction != CombatAiActionKind.None &&
                CanUseActionKindAtPosition(
                    character,
                    position,
                    turnPlan.ActionProbe.Target,
                    turnPlan.ActionProbe.BackupAction,
                    battleService);

            if (!canPreferred && !canBackup)
            {
                continue;
            }

            if (!TryComputeImproveFiringPositionScore(
                    character,
                    position,
                    battleService,
                    turnPlan,
                    canPreferred,
                    out var score,
                    out var actionKind))
            {
                continue;
            }

            score += ComputeStableTieBreakScore(character, turnPlan, position, actionKind);
            candidates.Add(new ConnectedFiringLineCandidate(
                position,
                score,
                actionKind,
                EstimateTurnsToPreferredAction(character, turnPlan, position),
                reachableDestinations.GetMoveCost(position)));
        }

        var orderedCandidates = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.TurnsToAction < 0 ? int.MaxValue : candidate.TurnsToAction)
            .ThenBy(candidate => candidate.MoveCost)
            .ThenBy(candidate => candidate.Position.x)
            .ThenBy(candidate => candidate.Position.y)
            .ThenBy(candidate => candidate.Position.z)
            .ToArray();

        foreach (var candidate in orderedCandidates)
        {
            if (!TryExecutePreMainRouteMoveCandidate(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    start,
                    candidate.Position,
                    continuation,
                    requireActionAfterMove,
                    routeMoveSource,
                    out destination))
            {
                continue;
            }

            ClearDisconnectedPositioningSealForConnectedFiringLineRoute(character);
            return true;
        }

        destination = start;
        if (closeOnFailure)
        {
            CloseFailedConnectedFiringLineRoute(
                character,
                turnPlan);
            closed = true;
        }

        return false;
    }

    private static bool TryUseLostTargetSearchRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        CombatAiActionLinkedMoveContinuation continuation,
        CombatAiRouteMoveSourceKind routeMoveSource,
        out int3 destination)
    {
        destination = default;

        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        if (HasPendingSearchKnownTargetMovement(character))
        {
            return false;
        }

        if (!TryGetLostTargetSearchAnchor(
                character,
                profile,
                turnPlan.ActionProbe,
                out var anchor,
                out var searchTarget))
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var start = character.LocationPosition;
        if (LostTargetSearchAttemptCache.TryGetValue(character.Guid, out var previousAttempt) &&
            previousAttempt.Matches(round, turnStamp, start, anchor))
        {
            return false;
        }

        if (character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself)
        {
            return false;
        }

        var searchActionProbe = new CombatAiActionProbe(
            turnPlan.ActionProbe.PreferredAction,
            turnPlan.ActionProbe.BackupAction,
            searchTarget ?? turnPlan.ActionProbe.Target,
            turnPlan.ActionProbe.CanUsePreferredAction,
            turnPlan.ActionProbe.CanUseBackupAction,
            turnPlan.ActionProbe.HasAtWillHostileSpell,
            turnPlan.ActionProbe.CapabilityCatalog);
        var searchTurnPlan = new CombatAiTurnPlan(
            searchActionProbe,
            BuildSearchKnownTargetMovementPlan(searchTarget, anchor),
            turnPlan.IsAttackContinuation);
        var remainingMove = Math.Max(0, character.RemainingTacticalMoves);

        var currentDistance = ComputeGridDistance(start, anchor);
        var currentActionConnection = GetSearchKnownTargetActionConnection(
            character,
            searchActionProbe,
            battleService,
            start,
            start,
            searchTarget,
            remainingMove,
            remainingMove);
        var currentTurnsToAction = EstimateTurnsToPreferredAction(character, searchTurnPlan, start);
        var currentBlockSeverity = GetSearchRouteConnectionBlockSeverity(currentActionConnection);
        var candidates = new List<SearchLostTargetRouteCandidate>();
        var connectedCandidates = 0;
        var hasReachableDestinations = false;
        var meaningfulProgressCandidateCount = 0;
        bool? shouldSealNoRouteMovement = null;

        bool ShouldSealNoRouteMovementOnce()
        {
            if (!shouldSealNoRouteMovement.HasValue)
            {
                shouldSealNoRouteMovement = ShouldSealSearchNoRouteMovement(
                    character,
                    searchTurnPlan,
                    searchActionProbe,
                    battleService,
                    profile,
                    start,
                    searchTarget,
                    remainingMove);
            }

            return shouldSealNoRouteMovement.Value;
        }

        if (TryGetReachableRouteDestinations(
                character,
                start,
                remainingMove,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: false))
        {
            hasReachableDestinations = true;

            foreach (var position in reachableDestinations.Positions)
            {
                if (IsFailedAiMoveTarget(character, start, position) ||
                    IsBacktrackingMove(character, start, position))
                {
                    continue;
                }

                var candidateDistance = ComputeGridDistance(position, anchor);
                var progress = currentDistance - candidateDistance;
                var actionConnection = GetSearchKnownTargetActionConnection(
                    character,
                    searchActionProbe,
                    battleService,
                    start,
                    position,
                    searchTarget,
                    remainingMove,
                    remainingMove);
                var actionConnected = actionConnection.Connected;

                var turnsToAction = EstimateTurnsToPreferredAction(character, searchTurnPlan, position);
                var forwardProgress = progress > 0.01f;
                var candidateQuality = SearchLostTargetRouteCandidateQuality.Connected;

                if (!actionConnected)
                {
                    var candidateBlockSeverity = GetSearchRouteConnectionBlockSeverity(actionConnection);
                    var firingLineProbe = AllowsSearchLostTargetFiringLineProbe(
                        searchActionProbe,
                        currentActionConnection,
                        actionConnection,
                        currentBlockSeverity,
                        candidateBlockSeverity,
                        currentTurnsToAction,
                        turnsToAction);

                    if (!firingLineProbe &&
                        progress + 0.01f < ComputeMinimumMovementGoalProgress(
                            character,
                            CombatAiMovementGoalKind.SearchKnownTarget,
                            CombatAiMovementPolicyKind.SearchKnownTargetPolicy,
                            currentDistance))
                    {
                        continue;
                    }

                    candidateQuality = GetSearchLostTargetRouteCandidateQuality(
                        currentBlockSeverity,
                        currentTurnsToAction,
                        actionConnection,
                        candidateBlockSeverity,
                        turnsToAction,
                        firingLineProbe);

                    if (candidateQuality == SearchLostTargetRouteCandidateQuality.Rejected)
                    {
                        continue;
                    }

                    if (candidateQuality == SearchLostTargetRouteCandidateQuality.FiringLineProbe &&
                        progress + 0.01f < MinimumRangedRouteProgress)
                    {
                        continue;
                    }

                    if (IsMeaningfulSearchLostTargetProgressCandidate(
                            actionConnection,
                            currentBlockSeverity,
                            candidateBlockSeverity,
                            progress))
                    {
                        meaningfulProgressCandidateCount++;
                    }
                }

                var score =
                    MovementGoalSearchScore +
                    (actionConnected ? MovementGoalSearchActionConnectedScore : 0f) +
                    Mathf.Clamp01(progress / Math.Max(currentDistance, 1f)) * 0.14f +
                    Mathf.Clamp01(progress / Math.Max(remainingMove, 1f)) * 0.10f +
                    ComputeStableTieBreakScore(
                        character,
                        searchTurnPlan,
                        position,
                        searchTurnPlan.ActionProbe.PreferredAction);

                if (actionConnected)
                {
                    connectedCandidates++;
                }

                var candidate = new SearchLostTargetRouteCandidate(
                    position,
                    score,
                    progress,
                    turnsToAction,
                    actionConnected,
                    candidateQuality,
                    forwardProgress,
                    HasEstimatedSearchLostTargetNextActionReachability(actionConnection, turnsToAction),
                    actionConnection,
                    reachableDestinations.GetMoveCost(position));

                if (candidateQuality == SearchLostTargetRouteCandidateQuality.FiringLineProbe &&
                    !ShouldSealNoRouteMovementOnce())
                {
                    continue;
                }

                candidates.Add(candidate);
            }
        }

        if (!hasReachableDestinations)
        {
            RecordLostTargetSearchAttempt(character, round, turnStamp, start, anchor);
            CloseSearchNoRouteWithoutTerminalOwnership(
                character,
                ShouldSealNoRouteMovementOnce());
            return false;
        }

        var orderedCandidates = candidates
            .OrderByDescending(candidate => candidate.ActionConnected)
            .ThenByDescending(candidate => candidate.BlockSeverityImproved)
            .ThenByDescending(candidate => candidate.TurnsToActionImproved && candidate.NextActionReachable)
            .ThenBy(candidate => candidate.FiringLineProbe)
            .ThenByDescending(candidate => candidate.NextActionReachable)
            .ThenByDescending(candidate => candidate.ForwardProgress)
            .ThenBy(candidate => GetSearchRouteConnectionBlockSeverity(candidate.ActionConnection))
            .ThenBy(candidate => candidate.TurnsToAction < 0 ? int.MaxValue : candidate.TurnsToAction)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Progress)
            .ThenBy(candidate => candidate.MoveCost)
            .ThenBy(candidate => candidate.Position.x)
            .ThenBy(candidate => candidate.Position.y)
            .ThenBy(candidate => candidate.Position.z)
            .ToArray();

        var requireConnectedCandidate = connectedCandidates > 0;

        var nextActionProbeBudget = SearchLostTargetNextActionProbeLimit;

        foreach (var candidate in orderedCandidates)
        {
            if (requireConnectedCandidate && !candidate.ActionConnected)
            {
                break;
            }

            var nextActionReachable = candidate.NextActionReachable;

            if (!candidate.ActionConnected &&
                candidate.Quality is
                    SearchLostTargetRouteCandidateQuality.TurnsImproved or
                    SearchLostTargetRouteCandidateQuality.FiringLineProbe)
            {
                if (!nextActionReachable && nextActionProbeBudget > 0)
                {
                    nextActionReachable = CanReachSearchKnownTargetActionWithOneAdditionalMove(
                        character,
                        searchActionProbe,
                        battleService,
                        candidate.Position,
                        searchTarget,
                        ref nextActionProbeBudget);
                }

                if (!nextActionReachable)
                {
                    continue;
                }
            }

            var candidateContinuation =
                continuation == CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove &&
                !candidate.ActionConnected
                    ? CombatAiActionLinkedMoveContinuation.ProgressOnlySearchMove
                    : continuation;

            if (!TryExecuteSearchLostTargetRouteMoveCandidate(
                    character,
                    searchTurnPlan,
                    battleService,
                    profile,
                    start,
                    candidate.Position,
                    candidateContinuation,
                    routeMoveSource: routeMoveSource,
                    out destination,
                    searchRouteActionConnected: candidate.ActionConnected,
                    moveCost: candidate.MoveCost,
                    allowedMoveBudget: remainingMove))
            {
                continue;
            }

            return true;
        }

        RecordLostTargetSearchAttempt(character, round, turnStamp, start, anchor);
        if (connectedCandidates == 0 && meaningfulProgressCandidateCount > 0)
        {
            foreach (var candidate in orderedCandidates.Where(candidate => !candidate.ActionConnected && candidate.ForwardProgress))
            {
                if (!TryExecuteSearchLostTargetRouteMoveCandidate(
                        character,
                        searchTurnPlan,
                        battleService,
                        profile,
                        start,
                        candidate.Position,
                        CombatAiActionLinkedMoveContinuation.ProgressOnlySearchMove,
                        routeMoveSource: routeMoveSource,
                        out destination,
                        searchRouteActionConnected: false,
                        moveCost: candidate.MoveCost,
                        allowedMoveBudget: remainingMove))
                {
                    continue;
                }

                return true;
            }
        }

        if (meaningfulProgressCandidateCount > 0)
        {
            return false;
        }

        CloseSearchNoRouteWithoutTerminalOwnership(
            character,
            ShouldSealNoRouteMovementOnce());
        return false;
    }

    private static bool IsMeaningfulSearchLostTargetProgressCandidate(
        RouteActionConnection actionConnection,
        int currentBlockSeverity,
        int candidateBlockSeverity,
        float progress)
    {
        return !actionConnection.Connected &&
               !actionConnection.DashMainRejected &&
               currentBlockSeverity > 0 &&
               candidateBlockSeverity > 0 &&
               candidateBlockSeverity <= currentBlockSeverity &&
               progress + 0.01f >= MinimumRangedRouteProgress;
    }

    private static SearchLostTargetRouteCandidateQuality GetSearchLostTargetRouteCandidateQuality(
        int currentBlockSeverity,
        int currentTurnsToAction,
        RouteActionConnection candidateActionConnection,
        int candidateBlockSeverity,
        int candidateTurnsToAction,
        bool firingLineProbe)
    {
        if (candidateActionConnection.Connected)
        {
            return SearchLostTargetRouteCandidateQuality.Connected;
        }

        if (candidateActionConnection.DashMainRejected)
        {
            return SearchLostTargetRouteCandidateQuality.Rejected;
        }

        if (currentBlockSeverity <= 0 ||
            candidateBlockSeverity <= 0 ||
            candidateBlockSeverity > currentBlockSeverity)
        {
            return SearchLostTargetRouteCandidateQuality.Rejected;
        }

        if (candidateBlockSeverity < currentBlockSeverity)
        {
            return SearchLostTargetRouteCandidateQuality.SeverityImproved;
        }

        if (IsSearchRouteTurnsToActionImproved(currentTurnsToAction, candidateTurnsToAction))
        {
            return SearchLostTargetRouteCandidateQuality.TurnsImproved;
        }

        return firingLineProbe
            ? SearchLostTargetRouteCandidateQuality.FiringLineProbe
            : SearchLostTargetRouteCandidateQuality.Rejected;
    }

    private static bool AllowsSearchLostTargetFiringLineProbe(
        CombatAiActionProbe actionProbe,
        RouteActionConnection currentActionConnection,
        RouteActionConnection candidateActionConnection,
        int currentBlockSeverity,
        int candidateBlockSeverity,
        int currentTurnsToAction,
        int candidateTurnsToAction)
    {
        if (actionProbe.PreferredAction is not (CombatAiActionKind.Ranged or CombatAiActionKind.Spell) &&
            actionProbe.BackupAction is not (CombatAiActionKind.Ranged or CombatAiActionKind.Spell) &&
            !actionProbe.CapabilityCatalog.HasAnyRanged &&
            !actionProbe.CapabilityCatalog.HasAtWillHostileSpell)
        {
            return false;
        }

        if (!currentActionConnection.CanAttackBlocked ||
            currentActionConnection.RangeBlocked ||
            !candidateActionConnection.CanAttackBlocked ||
            candidateActionConnection.RangeBlocked ||
            currentBlockSeverity <= 0 ||
            candidateBlockSeverity <= 0 ||
            candidateBlockSeverity > currentBlockSeverity)
        {
            return false;
        }

        if (candidateBlockSeverity < currentBlockSeverity ||
            IsSearchRouteTurnsToActionImproved(currentTurnsToAction, candidateTurnsToAction))
        {
            return false;
        }

        return true;
    }

    private static bool HasEstimatedSearchLostTargetNextActionReachability(
        RouteActionConnection actionConnection,
        int turnsToAction)
    {
        if (actionConnection.Connected)
        {
            return true;
        }

        return turnsToAction is >= 0 and <= 1 &&
               !actionConnection.DashMainRejected &&
               !actionConnection.RangeBlocked &&
               !actionConnection.CanAttackBlocked;
    }

    private static bool CanReachSearchKnownTargetActionWithOneAdditionalMove(
        GameLocationCharacter character,
        CombatAiActionProbe searchActionProbe,
        IGameLocationBattleService battleService,
        int3 candidatePosition,
        GameLocationCharacter searchTarget,
        ref int remainingProbeBudget)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            searchTarget?.RulesetCharacter == null ||
            remainingProbeBudget <= 0)
        {
            return false;
        }

        var nextMoveBudget = Math.Max(0, character.MaxTacticalMoves);

        if (nextMoveBudget <= 0)
        {
            return false;
        }

        remainingProbeBudget--;

        if (!TryGetReachableRouteDestinations(
                character,
                candidatePosition,
                nextMoveBudget,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: false,
                ignoreTurnPathfindingLimit: true))
        {
            return false;
        }

        foreach (var position in reachableDestinations.Positions)
        {
            if (IsFailedAiMoveTarget(character, candidatePosition, position))
            {
                continue;
            }

            var connection = GetSearchKnownTargetActionConnection(
                character,
                searchActionProbe,
                battleService,
                candidatePosition,
                position,
                searchTarget,
                nextMoveBudget,
                nextMoveBudget);

            if (connection.Connected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSealSearchNoRouteMovement(
        GameLocationCharacter character,
        CombatAiTurnPlan searchTurnPlan,
        CombatAiActionProbe searchActionProbe,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        GameLocationCharacter searchTarget,
        int remainingMove)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            searchTarget?.RulesetCharacter == null)
        {
            return false;
        }

        return !CanBonusDashConnectSearchKnownTargetAction(
            character,
            searchTurnPlan,
            searchActionProbe,
            battleService,
            profile,
            start,
            searchTarget,
            remainingMove);
    }

    private static bool CanBonusDashConnectSearchKnownTargetAction(
        GameLocationCharacter character,
        CombatAiTurnPlan searchTurnPlan,
        CombatAiActionProbe searchActionProbe,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        GameLocationCharacter searchTarget,
        int remainingMove)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            searchTarget?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available ||
            character.GetActionStatus(Id.DashBonus, ActionScope.Battle) != ActionStatus.Available ||
            HasCommittedBonusActionThisTurn(character))
        {
            return false;
        }

        var dashMoveBudget = remainingMove + Math.Max(0, character.MaxTacticalMoves);

        if (dashMoveBudget <= remainingMove ||
            !TryGetReachableRouteDestinations(
                character,
                start,
                dashMoveBudget,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: false))
        {
            return false;
        }

        foreach (var position in reachableDestinations.Positions)
        {
            if (position == start ||
                IsFailedAiMoveTarget(character, start, position))
            {
                continue;
            }

            var moveCost = reachableDestinations.GetMoveCost(position);

            if (moveCost <= remainingMove ||
                moveCost > dashMoveBudget ||
                !IsLegalAiRouteDestination(character, position) ||
                HasForcedRouteOpportunityExposure(character, start, position, battleService) ||
                ShouldRejectTrafficBlockingMove(
                    character,
                    position,
                    battleService,
                    profile,
                    searchTurnPlan))
            {
                continue;
            }

            var connection = GetSearchKnownTargetActionConnection(
                character,
                searchActionProbe,
                battleService,
                start,
                position,
                searchTarget,
                dashMoveBudget,
                dashMoveBudget);

            if (connection.Connected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSearchRouteTurnsToActionImproved(
        int currentTurnsToAction,
        int candidateTurnsToAction)
    {
        return candidateTurnsToAction >= 0 &&
               (currentTurnsToAction < 0 || candidateTurnsToAction < currentTurnsToAction);
    }

    private static int GetSearchRouteConnectionBlockSeverity(RouteActionConnection connection)
    {
        if (connection.Connected)
        {
            return 0;
        }

        if (connection.DashMainRejected)
        {
            return 4;
        }

        var severity = 0;

        if (connection.RangeBlocked)
        {
            severity += 2;
        }

        if (connection.CanAttackBlocked)
        {
            severity += 1;
        }

        return severity;
    }

    private static bool TryValidateSearchLostTargetDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        int3 destination,
        int moveCost,
        int allowedMoveBudget)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            destination == character.LocationPosition ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        if (!character.CanDecideToMoveByItself)
        {
            return false;
        }

        if (!IsLegalAiRouteDestination(character, destination))
        {
            return false;
        }

        if (moveCost > allowedMoveBudget)
        {
            return false;
        }

        if (HasForcedRouteOpportunityExposure(character, start, destination, battleService))
        {
            return false;
        }

        if (ShouldRejectTrafficBlockingMove(
                character,
                destination,
                battleService,
                profile,
                turnPlan))
        {
            return false;
        }

        if (IsBacktrackingMove(character, start, destination))
        {
            return false;
        }

        if (IsFailedAiMoveTarget(character, start, destination))
        {
            return false;
        }

        if (turnPlan.ActionProbe.Target?.RulesetCharacter == null)
        {
            return false;
        }

        return true;
    }

    private static RouteActionConnection GetSearchKnownTargetActionConnection(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService,
        int3 start,
        int3 position,
        GameLocationCharacter searchTarget,
        int remainingMove,
        int allowedMoveBudget)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return new RouteActionConnection(false, false, true, false);
        }

        if (RequiresMainDashForSearchRoute(character, start, position, remainingMove, allowedMoveBudget))
        {
            return new RouteActionConnection(false, false, false, true);
        }

        var targets = GetSearchKnownTargetConnectionTargets(character, searchTarget);
        var actionKinds = GetSearchKnownTargetActionKinds(actionProbe);
        var hasFailure = false;
        var bestFailure = default(RouteActionConnection);

        if (targets.Length == 0 || actionKinds.Length == 0)
        {
            return new RouteActionConnection(false, false, true, false);
        }

        foreach (var target in targets)
        {
            if (target?.RulesetCharacter == null || target.Side == character.Side)
            {
                continue;
            }

            foreach (var actionKind in actionKinds)
            {
                var connection = GetSearchKnownTargetActionConnection(
                    character,
                    position,
                    target,
                    actionKind,
                    battleService);

                if (connection.Connected)
                {
                    return connection;
                }

                RecordSearchKnownTargetConnectionFailure(connection, ref hasFailure, ref bestFailure);
            }

            if (!actionKinds.Contains(CombatAiActionKind.Spell))
            {
                var spellConnection = GetSearchKnownTargetSpellConnection(
                    character,
                    position,
                    target,
                    battleService);

                if (spellConnection.Connected)
                {
                    return spellConnection;
                }

                RecordSearchKnownTargetConnectionFailure(spellConnection, ref hasFailure, ref bestFailure);
            }

            var powerConnection = GetSearchKnownTargetHostilePowerConnection(character, position, target, battleService);

            if (powerConnection.Connected)
            {
                return powerConnection;
            }

            RecordSearchKnownTargetConnectionFailure(powerConnection, ref hasFailure, ref bestFailure);
        }

        return hasFailure
            ? bestFailure
            : new RouteActionConnection(false, false, true, false);
    }

    private static void RecordSearchKnownTargetConnectionFailure(
        RouteActionConnection connection,
        ref bool hasFailure,
        ref RouteActionConnection bestFailure)
    {
        if (connection.Connected)
        {
            return;
        }

        if (!hasFailure ||
            GetSearchRouteConnectionBlockSeverity(connection) < GetSearchRouteConnectionBlockSeverity(bestFailure))
        {
            bestFailure = connection;
            hasFailure = true;
        }
    }

    private static bool RequiresMainDashForSearchRoute(
        GameLocationCharacter character,
        int3 start,
        int3 position,
        int remainingMove,
        int allowedMoveBudget)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var moveCost = ComputeForcedMoveCost(start, position);

        if (moveCost <= Math.Max(remainingMove, allowedMoveBudget))
        {
            return false;
        }

        return true;
    }

    private static RouteActionConnection GetSearchKnownTargetActionConnection(
        GameLocationCharacter character,
        int3 position,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || target?.RulesetCharacter == null)
        {
            return new RouteActionConnection(false, false, true, false);
        }

        return actionKind switch
        {
            CombatAiActionKind.Ranged => GetSearchKnownTargetRangedConnection(
                character,
                position,
                target,
                battleService),
            CombatAiActionKind.Spell => GetSearchKnownTargetSpellConnection(
                character,
                position,
                target,
                battleService),
            CombatAiActionKind.Melee => CanAttackInMeleeFromPosition(
                character,
                position,
                target,
                target.LocationPosition,
                battleService)
                    ? new RouteActionConnection(true, false, false, false)
                    : new RouteActionConnection(false, false, true, false),
            _ => new RouteActionConnection(false, false, true, false)
        };
    }

    private static RouteActionConnection GetSearchKnownTargetRangedConnection(
        GameLocationCharacter character,
        int3 position,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        var distance = ComputeGridDistance(position, target.LocationPosition);
        var hasInRangeMode = false;

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (mode == null || !IsRangedAttackMode(mode))
            {
                continue;
            }

            if (mode.MaxRange <= 0f || distance <= mode.MaxRange + 0.5f)
            {
                hasInRangeMode = true;
                break;
            }
        }

        if (!hasInRangeMode)
        {
            return new RouteActionConnection(false, true, false, false);
        }

        return TryGetRangedAttackModifierFromPosition(character, position, target, target.LocationPosition, battleService, out _)
            ? new RouteActionConnection(true, false, false, false)
            : new RouteActionConnection(false, false, true, false);
    }

    private static RouteActionConnection GetSearchKnownTargetSpellConnection(
        GameLocationCharacter character,
        int3 position,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (CanUseResidualHostileSpellAtPosition(character, position, target, battleService))
        {
            return new RouteActionConnection(true, false, false, false);
        }

        return HasHostileSpellInRangeFromPosition(character, position, target)
            ? new RouteActionConnection(false, false, true, false)
            : new RouteActionConnection(false, true, false, false);
    }

    private static bool HasHostileSpellInRangeFromPosition(
        GameLocationCharacter character,
        int3 position,
        GameLocationCharacter target)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null || target?.RulesetCharacter == null)
        {
            return false;
        }

        var distance = ComputeGridDistance(position, target.LocationPosition);
        var cantrips = new List<SpellDefinition>();

        rulesetCharacter.EnumerateReadyAttackCantrips(cantrips);

        foreach (var cantrip in cantrips)
        {
            var effectDescription = cantrip == null
                ? null
                : PowerBundle.ModifySpellEffect(cantrip, rulesetCharacter);

            if (effectDescription?.TargetSide == Side.Enemy &&
                effectDescription.TargetType is TargetType.Individuals or TargetType.IndividualsUnique &&
                (effectDescription.RangeParameter <= 0f || distance <= effectDescription.RangeParameter + 0.5f))
            {
                return true;
            }
        }

        foreach (var repertoire in rulesetCharacter.SpellRepertoires)
        {
            if (repertoire == null ||
                HasHostileSpellInRangeFromList(rulesetCharacter, repertoire, repertoire.PreparedSpells, distance) ||
                HasHostileSpellInRangeFromList(rulesetCharacter, repertoire, repertoire.AutoPreparedSpells, distance) ||
                HasHostileSpellInRangeFromList(rulesetCharacter, repertoire, repertoire.KnownSpells, distance))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHostileSpellInRangeFromList(
        RulesetCharacter rulesetCharacter,
        RulesetSpellRepertoire repertoire,
        IEnumerable<SpellDefinition> spells,
        float distance)
    {
        if (rulesetCharacter == null || repertoire == null || spells == null)
        {
            return false;
        }

        foreach (var spell in spells)
        {
            if (spell == null || !IsResidualHostileSpellReady(repertoire, spell))
            {
                continue;
            }

            var effectDescription = PowerBundle.ModifySpellEffect(spell, rulesetCharacter);

            if (effectDescription?.TargetSide == Side.Enemy &&
                effectDescription.TargetType is TargetType.Individuals or TargetType.IndividualsUnique &&
                (effectDescription.RangeParameter <= 0f || distance <= effectDescription.RangeParameter + 0.5f))
            {
                return true;
            }
        }

        return false;
    }

    private static RouteActionConnection GetSearchKnownTargetHostilePowerConnection(
        GameLocationCharacter character,
        int3 position,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null || target?.RulesetCharacter == null || battleService == null)
        {
            return new RouteActionConnection(false, false, true, false);
        }

        var hasInRangePower = false;
        var distance = ComputeGridDistance(position, target.LocationPosition);

        foreach (var usablePower in rulesetCharacter.UsablePowers)
        {
            var power = usablePower?.PowerDefinition;
            var effectDescription = power?.EffectDescription;

            if (power == null ||
                effectDescription == null ||
                power.ActivationTime != ActivationTime.Action ||
                effectDescription.TargetSide != Side.Enemy ||
                effectDescription.TargetType is not (TargetType.Individuals or TargetType.IndividualsUnique) ||
                !rulesetCharacter.CanUsePower(power, true, true))
            {
                continue;
            }

            if (effectDescription.RangeParameter > 0f && distance > effectDescription.RangeParameter + 0.5f)
            {
                continue;
            }

            hasInRangePower = true;
            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();

            attackParams.FillForMagic(
                character,
                position,
                effectDescription,
                power.Name,
                target,
                target.LocationPosition,
                modifier);

            if (battleService.CanAttack(attackParams))
            {
                return new RouteActionConnection(true, false, false, false);
            }
        }

        return hasInRangePower
            ? new RouteActionConnection(false, false, true, false)
            : new RouteActionConnection(false, true, false, false);
    }

    private static GameLocationCharacter[] GetSearchKnownTargetConnectionTargets(
        GameLocationCharacter character,
        GameLocationCharacter searchTarget)
    {
        if (character?.RulesetCharacter == null)
        {
            return Array.Empty<GameLocationCharacter>();
        }

        var targets = new List<GameLocationCharacter>();

        if (searchTarget?.RulesetCharacter != null)
        {
            AddKnownEnemyTargets(character, new[] { searchTarget }, targets);
        }

        AddKnownEnemyTargets(character, GetKnownEnemyTargets(character), targets);

        return targets.ToArray();
    }

    private static CombatAiActionKind[] GetSearchKnownTargetActionKinds(CombatAiActionProbe actionProbe)
    {
        var actionKinds = new List<CombatAiActionKind>();

        AddTerminalReprobeActionKind(actionKinds, actionProbe.PreferredAction);
        AddTerminalReprobeActionKind(actionKinds, actionProbe.BackupAction);

        if (actionProbe.CapabilityCatalog.HasAnyRanged)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Ranged);
        }

        if (actionProbe.HasAtWillHostileSpell || actionProbe.CapabilityCatalog.HasAtWillHostileSpell)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Spell);
        }

        if (actionProbe.CapabilityCatalog.HasMelee)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Melee);
        }

        return actionKinds.ToArray();
    }

    private static bool TryBuildGroundMeleeTargetContactRouteQuery(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 targetPosition,
        int3 start,
        out GroundMeleeTargetContactRouteQuery query)
    {
        query = default;

        if (character?.RulesetCharacter == null || target?.RulesetCharacter == null)
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var routeBudget = GetGroundMeleeTargetContactRouteSearchBudget(character);

        if (!TryBuildGroundMeleeTargetContactRouteMap(
                character,
                target,
                targetPosition,
                start,
                routeBudget,
                out var routeMap,
                ignoreTurnPathfindingLimit: true))
        {
            return false;
        }

        var startDistance = ComputeGroundMeleeRouteGoalDistance(start, targetPosition);
        var hasAttackContactGoal = routeMap.ContactPositions.Count > 0;
        var orderedGoals = hasAttackContactGoal
            ? routeMap.ContactPositions
                .OrderBy(routeMap.GetMoveCost)
                .ThenBy(position => position.x)
                .ThenBy(position => position.y)
                .ThenBy(position => position.z)
                .ToArray()
            : routeMap.Positions
                .Where(position => position != start)
                .Where(position => IsLegalAiRouteDestination(
                    character,
                    position,
                    allowActorCurrentPosition: false))
                .Where(position => ComputeGroundMeleeRouteGoalDistance(position, targetPosition) + 0.5f < startDistance)
                .OrderBy(position => ComputeGroundMeleeRouteGoalDistance(position, targetPosition))
                .ThenBy(routeMap.GetMoveCost)
                .ThenBy(position => position.x)
                .ThenBy(position => position.y)
                .ThenBy(position => position.z)
                .Take(GroundMeleeTargetContactReverseGoalLimit)
                .ToArray();

        if (orderedGoals.Length == 0)
        {
            query = new GroundMeleeTargetContactRouteQuery(
                routeMap,
                false,
                default,
                0,
                new Dictionary<int3, int>(),
                new Dictionary<int3, int3>(),
                target.Guid,
                targetPosition,
                round,
                turnStamp);
            TargetContactRouteQueryCache[character.Guid] = query;
            return false;
        }

        var bestGoal = orderedGoals[0];

        var bestGoalMoveCost = routeMap.GetMoveCost(bestGoal);
        var contactCostByPosition = new Dictionary<int3, int>();
        var contactGoalByPosition = new Dictionary<int3, int3>();
        var reverseGoals = 0;

        foreach (var goal in orderedGoals.Take(GroundMeleeTargetContactReverseGoalLimit))
        {
            contactCostByPosition[goal] = 0;
            contactGoalByPosition[goal] = goal;

            if (!TryBuildGroundMeleeTargetContactRouteMap(
                    character,
                    target,
                    targetPosition,
                    goal,
                    routeBudget,
                    out var reverseMap,
                    ignoreTurnPathfindingLimit: true))
            {
                continue;
            }

            reverseGoals++;

            foreach (var position in reverseMap.Positions)
            {
                var cost = reverseMap.GetMoveCost(position);

                if (contactCostByPosition.TryGetValue(position, out var existingCost) &&
                    existingCost <= cost)
                {
                    continue;
                }

                contactCostByPosition[position] = cost;
                contactGoalByPosition[position] = goal;
            }
        }

        query = new GroundMeleeTargetContactRouteQuery(
            routeMap,
            true,
            bestGoal,
            bestGoalMoveCost,
            contactCostByPosition,
            contactGoalByPosition,
            target.Guid,
            targetPosition,
            round,
            turnStamp,
            !hasAttackContactGoal);
        TargetContactRouteQueryCache[character.Guid] = query;
        return true;
    }

    private static bool TryGetGroundMeleeTargetContactRouteQuery(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        out GroundMeleeTargetContactRouteQuery query)
    {
        query = default;

        if (character?.RulesetCharacter == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null)
        {
            return false;
        }

        var target = turnPlan.ActionProbe.Target;
        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (TargetContactRouteQueryCache.TryGetValue(character.Guid, out var cached) &&
            cached.Matches(target, targetPosition, character.LocationPosition, round, turnStamp))
        {
            query = cached;
            return cached.Complete;
        }

        TargetContactRouteQueryCache.Remove(character.Guid);

        return TryBuildGroundMeleeTargetContactRouteQuery(
            character,
            target,
            targetPosition,
            character.LocationPosition,
            out query);
    }

    private static bool TryGetCachedGroundMeleeTargetContactRouteQuery(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        out GroundMeleeTargetContactRouteQuery query)
    {
        query = default;

        if (character?.RulesetCharacter == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            !TargetContactRouteQueryCache.TryGetValue(character.Guid, out query))
        {
            return false;
        }

        return query.Matches(
            turnPlan.ActionProbe.Target,
            turnPlan.MovementPlan.TargetPosition,
            character.LocationPosition,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool TryExecutePreMainRouteMoveCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        int3 candidateDestination,
        CombatAiActionLinkedMoveContinuation continuation,
        bool requireActionAfterMove,
        CombatAiRouteMoveSourceKind routeMoveSource,
        out int3 destination,
        int allowedMoveBudget = -1,
        bool searchRouteActionConnected = false)
    {
        destination = candidateDestination;

        if (!TryValidateForcedRouteDestination(
                character,
                turnPlan,
                battleService,
                profile,
                start,
                destination,
                requireActionAfterMove,
                allowedMoveBudget))
        {
            return false;
        }

        if (IsGroundMeleePursuitPlan(turnPlan))
        {
            var remainingMove = Math.Max(0, character?.RemainingTacticalMoves ?? 0);

            if (!TryGetCachedReachableRouteDestinations(
                    character,
                    start,
                    remainingMove,
                    walkOnly: true,
                    out var reachableDestinations) ||
                !reachableDestinations.Contains(destination))
            {
                return false;
            }
        }

        if (RequiresPostMoveActionConnectedPositioning(turnPlan) &&
            turnPlan.MovementPlan.Goal != CombatAiMovementGoalKind.BreakThreat &&
            !HasValidatedPostMoveActionAtDestination(character, turnPlan, battleService, destination))
        {
            if (HasAttackCapablePositioningDestination(character, turnPlan) ||
                IsImproveFiringPositionPlan(turnPlan))
            {
                CurrentStateRouteBlockCache[character.Guid] = CurrentStateRouteBlockKind.NoPostMoveAction;
                return false;
            }

            if (IsRangedCasterPreferredRangePlan(turnPlan))
            {
                CurrentStateRouteBlockCache[character.Guid] = CurrentStateRouteBlockKind.RangedSeekDisconnected;
                return false;
            }

            if (!IsRangedCasterPreferredRangePlan(turnPlan) &&
                !IsSeekRouteImprovementDestination(character, turnPlan, start, destination))
            {
                CurrentStateRouteBlockCache[character.Guid] = CurrentStateRouteBlockKind.SeekRegression;
                return false;
            }

            CurrentStateRouteBlockCache.Remove(character.Guid);
        }

        var lockRemainingMovement = ShouldLockRemainingMovementAfterRouteMove(turnPlan, continuation);

        var moveToken = CreateActionLinkedMoveToken();
        ActionLinkedMoveCache[character.Guid] = new ActionLinkedMoveMemory(
            turnPlan.ActionProbe.Target,
            CombatAiActionKind.None,
            continuation,
            turnPlan.MovementPlan.Goal,
            start,
            destination,
            routeMoveSource,
            lockRemainingMovement,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            moveToken,
            searchRouteActionConnected);

        if (IsSearchKnownTargetPlan(turnPlan) &&
            routeMoveSource == CombatAiRouteMoveSourceKind.SearchLostTarget)
        {
            ClearDisconnectedPositioningSealForSearchKnownTargetRoute(character);
        }

        if (lockRemainingMovement)
        {
            RecordPendingRouteMovementLock(character, turnPlan.MovementPlan.Goal, continuation, destination);
        }

        CharacterAction.ActionChainExecutedHandler actionChainExecuted = null;

        if (continuation is
            CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove or
            CombatAiActionLinkedMoveContinuation.ProgressOnlySearchMove)
        {
            actionChainExecuted = aborted =>
            {
                OnAiTacticalMoveActionChainExecuted(character, aborted, routeMoveSource, moveToken);
            };
        }

        if (!TryValidateAiTacticalMoveIssue(character, destination))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            PendingRouteMovementLockCache.Remove(character.Guid);
            return false;
        }

        if (routeMoveSource == CombatAiRouteMoveSourceKind.Normal)
        {
            FreeJumpContext.SuppressAiFreeJumpForNextMove(character, destination);
        }

        character.MyExecuteActionTacticalMove(destination, actionChainExecuted);

        return true;
    }

    private static bool TryExecuteSearchLostTargetRouteMoveCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        int3 candidateDestination,
        CombatAiActionLinkedMoveContinuation continuation,
        CombatAiRouteMoveSourceKind routeMoveSource,
        out int3 destination,
        bool searchRouteActionConnected,
        int moveCost,
        int allowedMoveBudget,
        int moveToken = 0)
    {
        destination = candidateDestination;

        if (!TryValidateSearchLostTargetDestination(
                character,
                turnPlan,
                battleService,
                profile,
                start,
                destination,
                moveCost,
                allowedMoveBudget))
        {
            return false;
        }

        moveToken = moveToken == 0 ? CreateActionLinkedMoveToken() : moveToken;

        CharacterAction.ActionChainExecutedHandler actionChainExecuted = null;

        if (continuation is
            CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove or
            CombatAiActionLinkedMoveContinuation.ProgressOnlySearchMove)
        {
            actionChainExecuted = aborted =>
            {
                OnAiTacticalMoveActionChainExecuted(character, aborted, routeMoveSource, moveToken);
            };
        }

        if (!TryValidateAiTacticalMoveIssue(character, destination))
        {
            return false;
        }

        ActionLinkedMoveCache[character.Guid] = new ActionLinkedMoveMemory(
            turnPlan.ActionProbe.Target,
            CombatAiActionKind.None,
            continuation,
            turnPlan.MovementPlan.Goal,
            start,
            destination,
            routeMoveSource,
            lockRemainingMovementAfterArrival: false,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            moveToken,
            searchRouteActionConnected);

        ClearDisconnectedPositioningSealForSearchKnownTargetRoute(character);

        character.MyExecuteActionTacticalMove(destination, actionChainExecuted);

        return true;
    }

    private static bool TryBuildGroundMeleeTargetContactRouteMap(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 targetPosition,
        int3 start,
        int routeBudget,
        out GroundMeleeTargetContactRouteMap routeMap,
        bool ignoreTurnPathfindingLimit = false)
    {
        routeMap = default;

        if (!TryGetReachableRouteDestinations(
                character,
                start,
                routeBudget,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: true,
                ignoreTurnPathfindingLimit: ignoreTurnPathfindingLimit))
        {
            return false;
        }

        var positions = reachableDestinations.Positions
            .OrderBy(reachableDestinations.GetMoveCost)
            .ThenBy(position => position.x)
            .ThenBy(position => position.y)
            .ThenBy(position => position.z)
            .ToArray();
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var moveCostByPosition = positions.ToDictionary(position => position, reachableDestinations.GetMoveCost);
        var contactPositions = positions
            .Where(position => IsGroundMeleeAttackContactGoal(character, target, position, battleService))
            .OrderBy(reachableDestinations.GetMoveCost)
            .ThenBy(position => position.x)
            .ThenBy(position => position.y)
            .ThenBy(position => position.z)
            .ToArray();
        var bestPosition = positions
            .OrderBy(position => ComputeGroundMeleeRouteGoalDistance(position, targetPosition))
            .ThenBy(reachableDestinations.GetMoveCost)
            .ThenBy(position => position.x)
            .ThenBy(position => position.y)
            .ThenBy(position => position.z)
            .DefaultIfEmpty(start)
            .First();

        routeMap = new GroundMeleeTargetContactRouteMap(
            start,
            positions,
            contactPositions,
            moveCostByPosition,
            bestPosition,
            contactPositions.Length > 0);

        return positions.Length > 0;
    }

    private static int GetGroundMeleeTargetContactRouteSearchBudget(GameLocationCharacter character)
    {
        var move = Math.Max(1, Math.Max(character?.MaxTacticalMoves ?? 0, character?.RemainingTacticalMoves ?? 0));

        return Math.Min(GroundMeleeTargetContactRouteMaxBudget, Math.Max(move, move * GroundMeleeTargetContactRouteBudgetMultiplier));
    }

    private static int EstimateGroundMeleeContactRouteTurns(
        GameLocationCharacter character,
        int3 firstStep,
        int3 contactPosition)
    {
        return EstimateGroundMeleeContactRouteTurns(character, ComputeForcedMoveCost(firstStep, contactPosition));
    }

    private static int EstimateGroundMeleeContactRouteTurns(GameLocationCharacter character, int moveCost)
    {
        var move = Math.Max(1, Math.Max(character?.MaxTacticalMoves ?? 0, character?.RemainingTacticalMoves ?? 0));
        var distance = Math.Max(0, moveCost);

        return 1 + Math.Max(0, (int)Math.Ceiling((float)distance / move));
    }

    private static void ClearGroundMeleeRouteFailureAfterProgress(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        GroundMeleeRouteFailureCache.Remove(GetGroundMeleeRouteMemoryKey(character));
    }

    private static void RecordGroundMeleeRouteFailure(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 startPosition,
        int3 failedDestination)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null)
        {
            return;
        }

        var memory = new GroundMeleeRouteFailureMemory(
            target.Guid,
            startPosition,
            failedDestination,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        GroundMeleeRouteFailureCache[GetGroundMeleeRouteMemoryKey(character)] = memory;
    }

    private static bool TryDeferGroundMeleeMoveSettling(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleeMoveSettlingRoute(pendingAction))
        {
            return false;
        }

        var isJumpImmediateAttack = IsGroundMeleeJumpImmediateAttackRoute(pendingAction);

        if (character.LocationPosition != pendingAction.StartPosition && !isJumpImmediateAttack)
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var memory = new GroundMeleeMoveSettlingMemory(pendingAction, round, turnStamp);

        GroundMeleeMoveSettlingCache[character.Guid] = memory;
        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);

        return true;
    }

    private static bool TryCompletePendingJumpImmediateAttackActionChainSettled(
        GameLocationCharacter character,
        bool aborted,
        bool callbackObserved = false)
    {
        if (character?.RulesetCharacter == null ||
            !TryGetPendingJumpImmediateAttackMove(character, out var pendingAction))
        {
            return false;
        }

        var actualDestination = character.LocationPosition;

        var target = pendingAction.Target;
        var expectedOrAdjacent =
            actualDestination == pendingAction.ExpectedDestination ||
            target?.RulesetCharacter != null &&
            ComputeHorizontalGridStepDistance(actualDestination, target.LocationPosition) <= 1;

        if (!IsActiveBattleContender(character))
        {
            return true;
        }

        if (actualDestination == pendingAction.StartPosition)
        {
            if (!HasObservedActionLinkedMoveCallback(character, pendingAction))
            {
                DeferJumpImmediateAttackMoveResult(character, pendingAction);
                return true;
            }

            CloseFailedJumpImmediateAttackRoute(
                character,
                pendingAction,
                actualDestination);
            return true;
        }

        if (!expectedOrAdjacent)
        {
            CloseFailedJumpImmediateAttackRoute(
                character,
                pendingAction,
                actualDestination);
            return true;
        }

        if (TryDeferJumpImmediateAttackResolutionUntilStableBoundary(
                character,
                pendingAction,
                callbackObserved))
        {
            return true;
        }

        return TryCompleteJumpImmediateAttackMove(character, pendingAction);
    }

    private static bool HasObservedActionLinkedMoveCallback(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction)
    {
        return character?.RulesetCharacter != null &&
               ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var memory) &&
               memory.CallbackObserved &&
               memory.PendingAction.MoveToken == pendingAction.MoveToken &&
               memory.PendingAction.RouteMoveSource == pendingAction.RouteMoveSource;
    }

    private static bool TryCompleteJumpImmediateAttackMove(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            return false;
        }

        PendingAiMoveAttemptCache.Remove(character.Guid);
        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);

        return true;
    }

    private static void DeferJumpImmediateAttackMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction)
    {
        if (TryDeferGroundMeleeMoveSettling(character, pendingAction))
        {
            return;
        }

        CloseFailedJumpImmediateAttackRoute(
            character,
            pendingAction,
            character.LocationPosition);
    }

    private static bool TryDeferJumpImmediateAttackResolutionUntilStableBoundary(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        bool callbackObserved = false)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!MovementTracker.TryGetMovement(character.Guid, out _) &&
            !HasPendingReactionRequests())
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        ActionLinkedMoveSettlingCache[character.Guid] = new ActionLinkedMoveSettlingMemory(
            pendingAction,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            round,
            turnStamp,
            callbackObserved);
        ActionLinkedMoveCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);

        return true;
    }

    private static void CloseFailedJumpImmediateAttackRoute(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var noMove = actualDestination == pendingAction.StartPosition;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        RecordAiMoveFailure(character, pendingAction.StartPosition, pendingAction.ExpectedDestination);
        RecordGroundMeleeRouteFailure(
            character,
            pendingAction.Target,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination);

        PendingAiMoveAttemptCache.Remove(character.Guid);
        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        GroundMeleePartialRouteCache.Remove(character.Guid);

        TurnMovementProgressCache.Remove(character.Guid);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            noMove
                ? RouteMoveCompletionFlags.NoMove | RouteMoveCompletionFlags.GroundMeleeNoMove
                : RouteMoveCompletionFlags.None,
            round,
            turnStamp);

    }

    private static bool TryGetPendingJumpImmediateAttackMove(
        GameLocationCharacter character,
        out ActionLinkedMoveMemory pendingAction)
    {
        pendingAction = default;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var settling) &&
            IsGroundMeleeJumpImmediateAttackRoute(settling.PendingAction))
        {
            pendingAction = settling.PendingAction;
            return true;
        }

        if (ActionLinkedMoveSettlingCache.TryGetValue(character.Guid, out var actionLinkedSettling) &&
            IsGroundMeleeJumpImmediateAttackRoute(actionLinkedSettling.PendingAction))
        {
            pendingAction = actionLinkedSettling.PendingAction;
            return true;
        }

        if (ActionLinkedMoveCache.TryGetValue(character.Guid, out pendingAction) &&
            IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            return true;
        }

        pendingAction = default;
        return false;
    }

    private static bool TryResolveGroundMeleeMoveSettlingAfterActionChain(
        GameLocationCharacter character,
        bool aborted)
    {
        if (character?.RulesetCharacter == null ||
            !GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        var pendingAction = memory.PendingAction;
        var actualDestination = character.LocationPosition;
        var noMove = actualDestination == pendingAction.StartPosition;

        if (!IsActiveBattleContender(character))
        {
            GroundMeleeMoveSettlingCache.Remove(character.Guid);
            return true;
        }

        ResolveGroundMeleeMoveSettling(
            character,
            allowConnectedRouteValidation: true);

        if (!noMove)
        {
            return true;
        }

        return true;
    }

    private static bool ResolveGroundMeleeMoveSettling(
        GameLocationCharacter character,
        bool allowConnectedRouteValidation = false)
    {
        if (character?.RulesetCharacter == null ||
            !GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        GroundMeleeMoveSettlingCache.Remove(character.Guid);

        var pendingAction = memory.PendingAction;
        var actualDestination = character.LocationPosition;
        var isJumpImmediate = IsGroundMeleeJumpImmediateAttackRoute(pendingAction);

        if (!IsActiveBattleContender(character))
        {
            return false;
        }

        if (actualDestination != pendingAction.StartPosition)
        {
            if (isJumpImmediate)
            {
                var resolved = TryResolveGroundMeleeJumpImmediateAttackAfterSettling(
                           character,
                           pendingAction,
                           actualDestination,
                           out var jumpSettlingHandled) &&
                       jumpSettlingHandled;

                return resolved;
            }

            if (!IsGroundMeleeActualDestinationRouteProgressValid(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination,
                    actualDestination,
                    allowConnectedRouteValidation))
            {
                RecordAiMoveFailure(character, pendingAction.StartPosition, pendingAction.ExpectedDestination);
                RecordGroundMeleeRouteFailure(
                    character,
                    pendingAction.Target,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination);
                TurnMovementProgressCache.Remove(character.Guid);

                RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
                    pendingAction.MovementGoal,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination,
                    RouteMoveCompletionFlags.None,
                    GetCurrentBattleRound(),
                    Math.Max(1, ObservedCombatMemoryTurnStamp));

                return false;
            }

            ClearGroundMeleeRouteFailureAfterProgress(character);
            RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
                pendingAction.MovementGoal,
                pendingAction.StartPosition,
                pendingAction.ExpectedDestination,
                RouteMoveCompletionFlags.GroundMeleePartial,
                GetCurrentBattleRound(),
                Math.Max(1, ObservedCombatMemoryTurnStamp));
            RecordGroundMeleePartialRouteProgress(
                character,
                pendingAction,
                actualDestination);

            return false;
        }

        if (isJumpImmediate)
        {
            CloseFailedJumpImmediateAttackRoute(
                character,
                pendingAction,
                actualDestination);
            return true;
        }

        RecordAiMoveFailure(character, pendingAction.StartPosition, pendingAction.ExpectedDestination);
        RecordGroundMeleeRouteFailure(
            character,
            pendingAction.Target,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination);
        TurnMovementProgressCache.Remove(character.Guid);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            RouteMoveCompletionFlags.NoMove | RouteMoveCompletionFlags.GroundMeleeNoMove,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        return false;
    }

    private static bool TryResolveGroundMeleeJumpImmediateAttackAfterSettling(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        out bool handled)
    {
        handled = false;

        if (character?.RulesetCharacter == null ||
            !IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            ClearPendingMoveOwnership(character);
            handled = true;
            return true;
        }

        var target = pendingAction.Target;

        if (target?.RulesetCharacter != null)
        {
            handled = TryCompleteJumpImmediateAttackMove(character, pendingAction);
            return handled;
        }

        CloseFailedJumpImmediateAttackRoute(character, pendingAction, actualDestination);
        handled = true;
        return true;
    }

    private static bool RequiresPostMoveActionConnectedPositioning(CombatAiTurnPlan turnPlan)
    {
        if (IsImproveFiringPositionPlan(turnPlan))
        {
            return true;
        }

        if (turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.FlyingPursuitPolicy)
        {
            return true;
        }

        return turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MoveToPreferredRange &&
               turnPlan.MovementPlan.Policy is CombatAiMovementPolicyKind.RangedLinePolicy
                   or CombatAiMovementPolicyKind.SpellLinePolicy;
    }

    private static void RecordDisconnectedPositioningSeal(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var seal = new DisconnectedPositioningSealMemory(
            turnPlan.MovementPlan.Goal,
            turnPlan.MovementPlan.Policy,
            character.LocationPosition,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        DisconnectedPositioningSealCache[character.Guid] = seal;
    }

    private static void RecordRejectedRangedCasterSeekSeal(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe)
    {
        if (character?.RulesetCharacter == null ||
            actionProbe.Target?.RulesetCharacter == null ||
            actionProbe.PreferredAction is not (CombatAiActionKind.Ranged or CombatAiActionKind.Spell) ||
            HasPendingSearchKnownTargetMovement(character))
        {
            return;
        }

        var policy = actionProbe.PreferredAction == CombatAiActionKind.Spell
            ? CombatAiMovementPolicyKind.SpellLinePolicy
            : CombatAiMovementPolicyKind.RangedLinePolicy;
        var seal = new DisconnectedPositioningSealMemory(
            CombatAiMovementGoalKind.MoveToPreferredRange,
            policy,
            character.LocationPosition,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        DisconnectedPositioningSealCache[character.Guid] = seal;
    }

    private static void ClearDisconnectedPositioningSealForConnectedFiringLineRoute(GameLocationCharacter character)
    {
        if (!TryGetActiveDisconnectedPositioningSeal(character, out _))
        {
            return;
        }

        DisconnectedPositioningSealCache.Remove(character.Guid);
        DisconnectedPositioningMovementLockCache.Remove(character.Guid);
    }

    private static void ClearDisconnectedPositioningSealForSearchKnownTargetRoute(GameLocationCharacter character)
    {
        if (!TryGetActiveDisconnectedPositioningSeal(character, out _))
        {
            return;
        }

        DisconnectedPositioningSealCache.Remove(character.Guid);
        DisconnectedPositioningMovementLockCache.Remove(character.Guid);
    }

    private static bool TryGetActiveDisconnectedPositioningSeal(
        GameLocationCharacter character,
        out DisconnectedPositioningSealMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !DisconnectedPositioningSealCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        if (memory.MatchesCurrentTurn(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp)))
        {
            return true;
        }

        DisconnectedPositioningSealCache.Remove(character.Guid);
        DisconnectedPositioningMovementLockCache.Remove(character.Guid);
        memory = default;

        return false;
    }

    private static bool HasDisconnectedPostMovePositioning(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (turnPlan.ActionProbe.CanUsePreferredAction || turnPlan.ActionProbe.CanUseBackupAction)
        {
            return false;
        }

        if (IsSearchKnownTargetPlan(turnPlan))
        {
            return false;
        }

        var hasDisconnectedPreMainAttempt =
            HasDisconnectedPostMovePositioningPreMainAttempt(character);

        if (!RequiresPostMoveActionConnectedPositioning(turnPlan) ||
            turnPlan.MovementPlan.Goal != CombatAiMovementGoalKind.MoveToPreferredRange)
        {
            if (!hasDisconnectedPreMainAttempt)
            {
                return false;
            }

            RecordDisconnectedPositioningSeal(
                character,
                turnPlan);
            return true;
        }

        if (HasAttackCapablePositioningDestination(character, turnPlan))
        {
            return false;
        }

        var currentTurnsToAction = EstimateTurnsToPreferredAction(character, turnPlan, character.LocationPosition);
        var routeBlockKind = CurrentStateRouteBlockCache.TryGetValue(character.Guid, out var cachedRouteBlockKind)
            ? cachedRouteBlockKind
            : CurrentStateRouteBlockKind.None;
        var hasMovementProgress =
            TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress);
        var bestTurnsToAction = hasMovementProgress
            ? movementProgress.BestMoveCandidateTurnsToAction
            : -1;

        if (currentTurnsToAction > 0 &&
            bestTurnsToAction >= 0 &&
            bestTurnsToAction < currentTurnsToAction)
        {
            return false;
        }

        var blockedByRoute = routeBlockKind != CurrentStateRouteBlockKind.None;
        var blockedByProgress =
            hasMovementProgress &&
            !movementProgress.HadPreferredActionMovementCandidate &&
            !movementProgress.HasAcceptedMoveCandidate;

        if (!blockedByRoute && !blockedByProgress && !hasDisconnectedPreMainAttempt)
        {
            return false;
        }

        RecordDisconnectedPositioningSeal(
            character,
            turnPlan);

        return true;
    }

    private static bool HasDisconnectedPostMovePositioningPreMainAttempt(
        GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PreMainRouteMoveAttemptCache.TryGetValue(character.Guid, out var attempt) ||
            attempt.Round != GetCurrentBattleRound() ||
            attempt.TurnStamp != Math.Max(1, ObservedCombatMemoryTurnStamp) ||
            attempt.Goal != CombatAiMovementGoalKind.MoveToPreferredRange ||
            attempt.Status == CombatAiPreMainRouteMoveStatus.Executed)
        {
            return false;
        }

        return attempt.IsDisconnectedSeekFailure;
    }

    private static bool RequiresPostMoveActionConnectedPositioning(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (!RequiresPostMoveActionConnectedPositioning(turnPlan))
        {
            return false;
        }

        if (character?.RulesetCharacter == null)
        {
            return true;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MoveToPreferredRange &&
            !HasAttackCapablePositioningDestination(character, turnPlan))
        {
            return false;
        }

        return true;
    }

    private static bool HasAttackCapablePositioningDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null ||
            !RequiresPostMoveActionConnectedPositioning(turnPlan))
        {
            return false;
        }

        return TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress) &&
               movementProgress.HadPreferredActionMovementCandidate;
    }

    private static bool HasValidatedPostMoveActionAtDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 destination)
    {
        var target = turnPlan.ActionProbe.Target;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        return (turnPlan.ActionProbe.PreferredAction != CombatAiActionKind.None &&
                CanUseActionKindAtPosition(
                    character,
                    destination,
                    target,
                    turnPlan.ActionProbe.PreferredAction,
                    battleService)) ||
               (turnPlan.ActionProbe.BackupAction != CombatAiActionKind.None &&
                CanUseActionKindAtPosition(
                    character,
                    destination,
                    target,
                    turnPlan.ActionProbe.BackupAction,
                    battleService));
    }

    private static bool IsSeekRouteImprovementDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        int3 destination)
    {
        if (character?.RulesetCharacter == null ||
            !turnPlan.MovementPlan.HasGoal ||
            destination == start)
        {
            return false;
        }

        var currentTurnsToAction = EstimateTurnsToPreferredAction(character, turnPlan, start);
        var destinationTurnsToAction = EstimateTurnsToPreferredAction(character, turnPlan, destination);

        if (currentTurnsToAction > 0 &&
            destinationTurnsToAction >= 0 &&
            destinationTurnsToAction < currentTurnsToAction)
        {
            return true;
        }

        if (currentTurnsToAction >= 0 &&
            destinationTurnsToAction >= 0 &&
            destinationTurnsToAction > currentTurnsToAction)
        {
            return false;
        }

        return false;
    }

    private static bool IsRangedCasterPreferredRangePlan(CombatAiTurnPlan turnPlan)
    {
        return turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MoveToPreferredRange &&
               turnPlan.MovementPlan.Policy is CombatAiMovementPolicyKind.RangedLinePolicy
                   or CombatAiMovementPolicyKind.SpellLinePolicy;
    }

    private static bool ShouldAllowDisconnectedRangedSeekImprovement(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        int3 destination,
        int currentTurnsToAction,
        int destinationTurnsToAction)
    {
        if (!IsRangedCasterPreferredRangePlan(turnPlan))
        {
            return IsSeekRouteImprovementDestination(character, turnPlan, start, destination);
        }

        if (TryGetCurrentOrRecentMeleeThreat(
                character,
                character?.LocationPosition ?? default,
                out _,
                out _,
                out _))
        {
            return IsSeekRouteImprovementDestination(character, turnPlan, start, destination);
        }

        if (currentTurnsToAction > 0 &&
            destinationTurnsToAction >= 0 &&
            destinationTurnsToAction < currentTurnsToAction)
        {
            return true;
        }

        if (currentTurnsToAction >= 0 &&
            destinationTurnsToAction >= 0 &&
            destinationTurnsToAction > currentTurnsToAction)
        {
            return false;
        }

        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var currentDistance = ComputeGridDistance(start, targetPosition);
        var destinationDistance = ComputeGridDistance(destination, targetPosition);
        var progress = currentDistance - destinationDistance;

        if (progress <= 0f)
        {
            return false;
        }

        var minimumProgress = ComputeMinimumMovementGoalProgress(
            character,
            turnPlan.MovementPlan.Goal,
            turnPlan.MovementPlan.Policy,
            currentDistance);

        if (TryGetMinimumLineRouteProgress(turnPlan.MovementPlan.Policy, out var minimumLineProgress))
        {
            minimumProgress = Math.Max(minimumProgress, minimumLineProgress);
        }

        return progress + 0.01f >= minimumProgress;
    }

    private static bool IsGroundMeleeActualDestinationRouteProgressValid(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 startPosition,
        int3 expectedDestination,
        int3 actualDestination,
        bool allowConnectedRouteValidation = false)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitTerminalRoute(pendingAction))
        {
            return true;
        }

        if (actualDestination == startPosition)
        {
            return false;
        }

        if (actualDestination == expectedDestination)
        {
            if (!IsLegalAiRouteDestination(
                    character,
                    actualDestination,
                    allowActorCurrentPosition: true))
            {
                return false;
            }

            return true;
        }

        var target = pendingAction.Target;

        if (target?.RulesetCharacter != null)
        {
            var targetPosition = target.LocationPosition;

            var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
            var actionKind = pendingAction.ActionKind == CombatAiActionKind.None
                ? CombatAiActionKind.Melee
                : pendingAction.ActionKind;

            if (battleService != null &&
                (CanUseActionKindAtPosition(character, actualDestination, target, actionKind, battleService) ||
                 IsGroundMeleeAttackContactGoal(character, target, actualDestination, battleService)))
            {
                return true;
            }

            if (TryValidateCachedGroundMeleeRouteProgress(
                    character,
                    target,
                    targetPosition,
                    startPosition,
                    actualDestination,
                    out var cachedRouteEvaluated))
            {
                return true;
            }

            if (cachedRouteEvaluated && !allowConnectedRouteValidation)
            {
                return false;
            }

            var startRouteQuery = default(GroundMeleeTargetContactRouteQuery);
            var hasStartContactRoute =
                allowConnectedRouteValidation &&
                TryBuildGroundMeleeTargetContactRouteQuery(
                    character,
                    target,
                    targetPosition,
                    startPosition,
                    out startRouteQuery);

            var startContactMoveCost = hasStartContactRoute &&
                                       startRouteQuery.TryGetContactCost(startPosition, out var startCost, out _)
                ? startCost
                : startRouteQuery.BestGoalMoveCost;

            if (hasStartContactRoute &&
                startRouteQuery.TryGetContactCost(actualDestination, out var actualContactMoveCost, out var contactGoal) &&
                actualContactMoveCost < startContactMoveCost)
            {
                return true;
            }

            if (allowConnectedRouteValidation)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryValidateCachedGroundMeleeRouteProgress(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 targetPosition,
        int3 startPosition,
        int3 actualDestination,
        out bool evaluated)
    {
        evaluated = false;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            !TargetContactRouteQueryCache.TryGetValue(character.Guid, out var cachedRouteQuery) ||
            !cachedRouteQuery.Matches(
                target,
                targetPosition,
                startPosition,
                GetCurrentBattleRound(),
                Math.Max(1, ObservedCombatMemoryTurnStamp)) ||
            !cachedRouteQuery.Complete)
        {
            return false;
        }

        evaluated = true;

        var startContactMoveCost = cachedRouteQuery.TryGetContactCost(startPosition, out var startCost, out _)
            ? startCost
            : cachedRouteQuery.BestGoalMoveCost;

        if (cachedRouteQuery.TryGetContactCost(actualDestination, out var actualContactMoveCost, out var contactGoal) &&
            actualContactMoveCost < startContactMoveCost)
        {
            return true;
        }

        return false;
    }

    private static bool ShouldRejectGroundMeleeRouteFailureCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        int3 candidatePosition)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitPlan(turnPlan) ||
            !GroundMeleeRouteFailureCache.TryGetValue(GetGroundMeleeRouteMemoryKey(character), out var failure))
        {
            return false;
        }

        if (!failure.Matches(turnPlan.ActionProbe.Target, start))
        {
            if (turnPlan.ActionProbe.Target?.RulesetCharacter != null &&
                failure.TargetGuid != turnPlan.ActionProbe.Target.Guid)
            {
                GroundMeleeRouteFailureCache.Remove(GetGroundMeleeRouteMemoryKey(character));
            }
            else if (turnPlan.ActionProbe.Target?.RulesetCharacter != null &&
                     failure.TargetGuid == turnPlan.ActionProbe.Target.Guid &&
                     start != failure.StartPosition)
            {
                GroundMeleeRouteFailureCache.Remove(GetGroundMeleeRouteMemoryKey(character));
            }

            return false;
        }

        if (!failure.Blocks(candidatePosition))
        {
            return false;
        }

        return true;
    }

    private static float ComputeGroundMeleeRouteGoalDistance(int3 position, int3 targetPosition)
    {
        var horizontalDistance = ComputeHorizontalGridStepDistance(position, targetPosition);
        var verticalPenalty = Math.Max(0, Math.Abs(position.y - targetPosition.y) - 1) * 0.25f;

        return horizontalDistance + verticalPenalty;
    }

    private static bool IsGroundMeleeRouteAdjacentContact(int3 position, int3 targetPosition)
    {
        return ComputeHorizontalGridStepDistance(position, targetPosition) <= 1;
    }

    private static bool IsGroundMeleeAttackContactGoal(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 position,
        IGameLocationBattleService battleService,
        bool allowStrictAdjacentFallback = false)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null)
        {
            return false;
        }

        if (!IsLegalAiRouteDestination(
                character,
                position,
                allowActorCurrentPosition: true))
        {
            return false;
        }

        if (battleService != null &&
            CanUseActionKindAtPosition(character, position, target, CombatAiActionKind.Melee, battleService))
        {
            return true;
        }

        return IsGroundMeleeStrictRouteContactGoal(
            character,
            target,
            position,
            battleService,
            allowStrictAdjacentFallback);
    }

    private static bool IsGroundMeleeStrictRouteContactGoal(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 position,
        IGameLocationBattleService battleService,
        bool allowStrictAdjacentFallback = false)
    {
        var maxReach = GetGroundMeleeMaximumReach(character);

        if (maxReach <= 0)
        {
            return false;
        }

        if (battleService != null)
        {
            foreach (var mode in character.RulesetCharacter.AttackModes)
            {
                if (mode == null || !ValidatorsWeapon.IsMelee(mode))
                {
                    continue;
                }

                if (battleService.IsWithinXCells(
                        character,
                        position,
                        target,
                        target.LocationPosition,
                        mode.reachRange))
                {
                    return true;
                }
            }
        }

        var routeReach = Math.Max(1, maxReach);
        var horizontalContact =
            ComputeHorizontalGridStepDistance(position, target.LocationPosition) <= routeReach;
        var verticalContact = Math.Abs(position.y - target.LocationPosition.y) <= routeReach;

        return horizontalContact &&
               verticalContact &&
               (allowStrictAdjacentFallback ||
                ComputeHorizontalGridStepDistance(position, target.LocationPosition) <= GroundMeleeAttackGoalProbeBand);
    }

    private static int GetGroundMeleeMaximumReach(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return 0;
        }

        var reach = 0;

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (mode == null || !ValidatorsWeapon.IsMelee(mode))
            {
                continue;
            }

            reach = Math.Max(reach, Math.Max(1, mode.reachRange));
        }

        return reach;
    }

    private static bool TryValidateForcedRouteDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        int3 destination,
        bool requireActionAfterMove,
        int allowedMoveBudget = -1)
    {
        if (destination == character.LocationPosition)
        {
            return false;
        }

        if (character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself)
        {
            return false;
        }

        if (!IsLegalAiRouteDestination(character, destination))
        {
            return false;
        }

        if (ComputeForcedMoveCost(start, destination) > GetAllowedRouteMoveBudget(character, allowedMoveBudget))
        {
            return false;
        }

        if (ShouldRequireReachableProxyThreatDestination(character, turnPlan) &&
            !TryValidateReachableProxyThreatDestination(character, start, destination))
        {
            return false;
        }

        if (turnPlan.MovementPlan.Goal is not (CombatAiMovementGoalKind.BreakThreat or
                CombatAiMovementGoalKind.MaintainThreatAvoidance) &&
            TryGetProxyMeleeThreat(character, out _) &&
            !TryValidateProxyThreatDistanceProgress(character, start, destination))
        {
            return false;
        }

        if (HasForcedRouteOpportunityExposure(character, start, destination, battleService))
        {
            return false;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            if (WouldLeaveCurrentReactionMeleeReach(
                    character,
                    start,
                    destination,
                    battleService))
            {
                return false;
            }

        }

        if (ShouldRejectTrafficBlockingMove(
                character,
                destination,
                battleService,
                profile,
                turnPlan))
        {
            return false;
        }

        if (IsBacktrackingMove(character, start, destination) || IsFailedAiMoveTarget(character, start, destination))
        {
            return false;
        }

        var breakThreatAttackConnected = IsBreakThreatDestinationAttackConnected(
            character,
            turnPlan,
            battleService,
            start,
            destination);
        var breakThreatDefensiveFallback =
            !breakThreatAttackConnected &&
            IsBreakThreatDefensiveFallbackDestination(
                character,
                turnPlan,
                battleService,
                start,
                destination);

        if (!breakThreatAttackConnected && !breakThreatDefensiveFallback)
        {
            return false;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat &&
            TryGetCurrentOrRecentMeleeThreat(
                character,
                start,
                out _,
                out var threatPosition,
                out _))
        {
            var startThreatDistance = ComputeGridDistance(start, threatPosition);
            var destinationThreatDistance = ComputeGridDistance(destination, threatPosition);
            var threatDistanceGain = destinationThreatDistance - startThreatDistance;

            if (destinationThreatDistance <= ThreatAvoidanceThreatZoneDistance ||
                threatDistanceGain < ThreatAvoidanceMinimumDistanceGain)
            {
                return false;
            }

        }

        if (requireActionAfterMove &&
            !breakThreatDefensiveFallback &&
            !CanUseActionKindAtPosition(
                character,
                destination,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.PreferredAction,
                battleService) &&
            !CanUseActionKindAtPosition(
                character,
                destination,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.BackupAction,
                battleService))
        {
            return false;
        }

        return true;
    }

    private static int GetAllowedRouteMoveBudget(GameLocationCharacter character, int allowedMoveBudget)
    {
        return allowedMoveBudget > 0
            ? allowedMoveBudget
            : Math.Max(0, character?.RemainingTacticalMoves ?? 0);
    }

    private static bool ShouldRequireReachableProxyThreatDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        return turnPlan.MovementPlan.Goal is (CombatAiMovementGoalKind.BreakThreat or
                   CombatAiMovementGoalKind.MaintainThreatAvoidance) &&
               TryGetProxyMeleeThreat(character, out _);
    }

    private static bool TryValidateReachableProxyThreatDestination(
        GameLocationCharacter character,
        int3 start,
        int3 destination)
    {
        var remainingMove = Math.Max(0, character?.RemainingTacticalMoves ?? 0);

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!TryGetReachableRouteDestinations(
                character,
                start,
                remainingMove,
                out var reachableDestinations,
                allowPathfinding: true))
        {
            return false;
        }

        if (!reachableDestinations.Contains(destination))
        {
            return false;
        }

        if (!TryValidateProxyThreatDistanceProgress(character, start, destination))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateProxyThreatDistanceProgress(
        GameLocationCharacter character,
        int3 start,
        int3 destination)
    {
        if (character?.RulesetCharacter == null ||
            !TryGetProxyMeleeThreat(character, out var proxyThreat))
        {
            return true;
        }

        var sourcePosition = proxyThreat.Source?.LocationPosition ?? proxyThreat.SourcePosition;
        var startDistance = ComputeGridDistance(start, sourcePosition);
        var destinationDistance = ComputeGridDistance(destination, sourcePosition);
        var distanceGain = destinationDistance - startDistance;
        var exitsThreatZone = startDistance <= ThreatAvoidanceThreatZoneDistance &&
                              destinationDistance > ThreatAvoidanceThreatZoneDistance;

        if (destinationDistance < startDistance ||
            !exitsThreatZone && distanceGain < ThreatAvoidanceMinimumDistanceGain)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetReachableRouteDestinations(
        GameLocationCharacter character,
        int3 start,
        int remainingMove,
        out ReachableRouteDestinationMemory reachableDestinations,
        bool allowPathfinding = false,
        bool walkOnly = false,
        bool ignoreTurnPathfindingLimit = false)
    {
        reachableDestinations = null;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (remainingMove <= 0)
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var cacheKey = new ReachableRouteCacheKey(
            character.Guid,
            start,
            remainingMove,
            round,
            turnStamp,
            walkOnly);

        if (!allowPathfinding)
        {
            return false;
        }

        if (IsRoutePathfindingUnsafePhase(character, round, turnStamp))
        {
            return false;
        }

        if (ReachableRouteDestinationCache.TryGetValue(cacheKey, out var cached) &&
            cached.Matches(start, remainingMove, round, turnStamp, walkOnly))
        {
            reachableDestinations = cached;
            return cached.Positions.Count > 0;
        }

        var pathfindingCountKey = new ActorTurnKey(character.Guid, round, turnStamp);
        var pathfindingCount = ReachableRoutePathfindingCountCache.TryGetValue(pathfindingCountKey, out var count)
            ? count
            : 0;

        if (!ignoreTurnPathfindingLimit &&
            pathfindingCount >= GroundMeleeRoutePathfindingPerTurnLimit)
        {
            return false;
        }

        var pathfindingService = ServiceRepository.GetService<IGameLocationPathfindingService>();

        if (pathfindingService == null)
        {
            return false;
        }

        using (FreeJumpContext.SuppressAiPathfindingFreeJump(character))
        {
            ReachableRoutePathfindingCountCache[pathfindingCountKey] = pathfindingCount + 1;
            pathfindingService
                .ComputeValidDestinationsAsync(character, start, remainingMove, 0, true, true)
                .ExecuteUntilDone();
        }

        var positions = new List<int3>();
        var factsByPosition = new Dictionary<int3, ReachableRouteDestinationFacts>();

        foreach (var destination in pathfindingService.ValidDestinations)
        {
            if ((walkOnly
                    ? !IsGroundRouteMoveMode(destination.moveMode)
                    : destination.moveMode is not (MoveMode.Walk or MoveMode.Fly)) ||
                destination.position == start ||
                destination.moveCost > remainingMove)
            {
                continue;
            }

            if (!factsByPosition.ContainsKey(destination.position))
            {
                positions.Add(destination.position);
                factsByPosition.Add(
                    destination.position,
                    new ReachableRouteDestinationFacts(
                        destination.moveCost,
                        destination.moveMode,
                        destination.flags));
            }
        }

        reachableDestinations = new ReachableRouteDestinationMemory(
            start,
            remainingMove,
            round,
            turnStamp,
            positions,
            factsByPosition,
            walkOnly);
        ReachableRouteDestinationCache[cacheKey] = reachableDestinations;

        return positions.Count > 0;
    }

    private static bool IsGroundRouteMoveMode(MoveMode moveMode)
    {
        return moveMode != MoveMode.Fly;
    }

    private static bool TryGetCachedReachableRouteDestinations(
        GameLocationCharacter character,
        int3 start,
        int remainingMove,
        bool walkOnly,
        out ReachableRouteDestinationMemory reachableDestinations)
    {
        reachableDestinations = null;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var cacheKey = new ReachableRouteCacheKey(
            character.Guid,
            start,
            remainingMove,
            round,
            turnStamp,
            walkOnly);

        return ReachableRouteDestinationCache.TryGetValue(cacheKey, out reachableDestinations) &&
               reachableDestinations.Matches(start, remainingMove, round, turnStamp, walkOnly);
    }

    private static void ClearReachableRouteDestinationCache(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        foreach (var key in ReachableRouteDestinationCache.Keys
                     .Where(key => key.ActorGuid == character.Guid)
                     .ToArray())
        {
            ReachableRouteDestinationCache.Remove(key);
        }

        foreach (var key in ReachableRoutePathfindingCountCache.Keys
                     .Where(key => key.ActorGuid == character.Guid)
                     .ToArray())
        {
            ReachableRoutePathfindingCountCache.Remove(key);
        }
    }

    private static bool IsRoutePathfindingUnsafePhase(
        GameLocationCharacter character,
        int round,
        int turnStamp)
    {
        if (character?.RulesetCharacter == null)
        {
            return true;
        }

        if (ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            ActionLinkedMoveSettlingCache.ContainsKey(character.Guid))
        {
            return true;
        }

        if (GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var settling) &&
            settling.Round == round &&
            settling.TurnStamp == turnStamp)
        {
            return true;
        }

        return false;
    }

    private static void RecordPreMainRouteMoveAttempt(
        GameLocationCharacter character,
        CombatAiPreMainRouteMoveStatus status,
        CombatAiMovementGoalKind goal,
        int3 expectedDestination,
        PreMainRouteMoveFlags flags = PreMainRouteMoveFlags.None)
    {
        if (character == null)
        {
            return;
        }

        PreMainRouteMoveAttemptCache[character.Guid] = new PreMainRouteMoveAttempt(
            status,
            goal,
            expectedDestination,
            flags,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool HasReachedExecutedPreMainRouteDestination(GameLocationCharacter character)
    {
        return character?.RulesetCharacter != null &&
               PreMainRouteMoveAttemptCache.TryGetValue(character.Guid, out var attempt) &&
               attempt.Status == CombatAiPreMainRouteMoveStatus.Executed &&
               attempt.ExpectedDestination == character.LocationPosition &&
               attempt.Round == GetCurrentBattleRound() &&
               attempt.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool ShouldLockRemainingMovementAfterRouteMove(
        CombatAiTurnPlan turnPlan,
        CombatAiActionLinkedMoveContinuation continuation)
    {
        if (continuation != CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision)
        {
            return false;
        }

        return turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat ||
               turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MaintainThreatAvoidance ||
               turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing ||
               IsImproveFiringPositionPlan(turnPlan);
    }

    private static void RecordPendingRouteMovementLock(
        GameLocationCharacter character,
        CombatAiMovementGoalKind movementGoal,
        CombatAiActionLinkedMoveContinuation continuation,
        int3 expectedDestination)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PendingRouteMovementLockCache[character.Guid] = new PendingRouteMovementLockMemory(
            movementGoal,
            continuation,
            character.LocationPosition,
            expectedDestination,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static void MarkRecentMeleeThreatHandledThisTurn(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        bool hasSafePosition)
    {
        if (character?.RulesetCharacter == null ||
            pendingAction.MovementGoal != CombatAiMovementGoalKind.BreakThreat ||
            actualDestination == pendingAction.StartPosition ||
            !RecentMeleeThreatMemoryCache.TryGetValue(character.Guid, out var memory))
        {
            return;
        }

        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
        var existingAvoidance = ThreatAvoidanceMemoryCache.TryGetValue(character.Guid, out var avoidance)
            ? avoidance
            : default;
        var safePosition = hasSafePosition
            ? actualDestination
            : existingAvoidance.HasSafePosition
                ? existingAvoidance.SafePosition
                : default;
        var safePositionKnown = hasSafePosition || existingAvoidance.HasSafePosition;

        ThreatAvoidanceMemoryCache[character.Guid] = new ThreatAvoidanceMemory(
            memory.Source,
            sourcePosition,
            memory.IsEffectProxy,
            pendingAction.StartPosition,
            safePosition,
            safePositionKnown,
            pendingAction.MovementGoal,
            true,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

    }

    private static bool TrySpendLeftoverBonusActionEconomy(
        GameLocationCharacter character,
        CombatAiActionEconomySnapshot actionEconomy,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            actionEconomy.Bonus != ActionStatus.Available ||
            character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available ||
            HasCommittedBonusActionThisTurn(character))
        {
            return false;
        }

        if (battleService == null)
        {
            return false;
        }

        return TryUseLeftoverBonusSpell(character, battleService, hostile: true) ||
               TryUseLeftoverBonusPower(character, battleService, hostile: true) ||
               TryUseLeftoverBonusSpell(character, battleService, hostile: false) ||
               TryUseLeftoverBonusPower(character, battleService, hostile: false);
    }

    private static bool TryUseLeftoverBonusSpell(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        bool hostile,
        bool selfOnlyUtility = false)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            character.GetActionStatus(Id.CastBonus, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        var targets = hostile
            ? GetCurrentHostileActionTargets(character, battleService)
            : GetLeftoverUtilityTargets(character, selfOnlyUtility);

        foreach (var target in targets)
        {
            if (!TryGetLeftoverBonusSpellFromPosition(
                    character,
                    target,
                    battleService,
                    hostile,
                    out var spell,
                    out var spellRepertoire,
                    out var modifier))
            {
                continue;
            }

            if (!TryExecuteLeftoverBonusSpell(character, target, spell, spellRepertoire, modifier))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryGetLeftoverBonusSpellFromPosition(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        bool hostile,
        out SpellDefinition spell,
        out RulesetSpellRepertoire spellRepertoire,
        out ActionModifier modifier)
    {
        spell = null;
        spellRepertoire = null;
        modifier = null;

        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null || target?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);
        var seenSpells = new HashSet<string>(StringComparer.Ordinal);

        foreach (var repertoire in rulesetCharacter.SpellRepertoires)
        {
            if (repertoire == null)
            {
                continue;
            }

            if (TryGetLeftoverBonusSpellFromSpellList(
                    character,
                    target,
                    battleService,
                    repertoire,
                    repertoire.PreparedSpells,
                    distance,
                    hostile,
                    seenSpells,
                    out spell,
                    out modifier) ||
                TryGetLeftoverBonusSpellFromSpellList(
                    character,
                    target,
                    battleService,
                    repertoire,
                    repertoire.AutoPreparedSpells,
                    distance,
                    hostile,
                    seenSpells,
                    out spell,
                    out modifier) ||
                TryGetLeftoverBonusSpellFromSpellList(
                    character,
                    target,
                    battleService,
                    repertoire,
                    repertoire.KnownSpells,
                    distance,
                    hostile,
                    seenSpells,
                    out spell,
                    out modifier))
            {
                spellRepertoire = repertoire;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetLeftoverBonusSpellFromSpellList(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        RulesetSpellRepertoire spellRepertoire,
        IEnumerable<SpellDefinition> spells,
        float distance,
        bool hostile,
        ISet<string> seenSpells,
        out SpellDefinition spell,
        out ActionModifier modifier)
    {
        spell = null;
        modifier = null;

        if (spells == null)
        {
            return false;
        }

        foreach (var candidate in spells)
        {
            if (candidate == null ||
                string.IsNullOrEmpty(candidate.Name) ||
                seenSpells.Contains(candidate.Name) ||
                !IsLeftoverBonusSpellCandidate(
                    character,
                    target,
                    spellRepertoire,
                    candidate,
                    distance,
                    hostile,
                    out var effectDescription))
            {
                continue;
            }

            seenSpells.Add(candidate.Name);
            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var actionModifier = new ActionModifier();

            attackParams.FillForMagic(
                character,
                character.LocationPosition,
                effectDescription,
                candidate.Name,
                target,
                target.LocationPosition,
                actionModifier);

            if (!battleService.CanAttack(attackParams))
            {
                continue;
            }

            spell = candidate;
            modifier = actionModifier;
            return true;
        }

        return false;
    }

    private static bool IsLeftoverBonusSpellCandidate(
        GameLocationCharacter character,
        GameLocationCharacter target,
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spell,
        float distance,
        bool hostile,
        out EffectDescription effectDescription)
    {
        effectDescription = null;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            spellRepertoire == null ||
            spell == null ||
            spell.ActivationTime != ActivationTime.BonusAction ||
            !IsSpellReadyForLeftoverBonus(character.RulesetCharacter, spellRepertoire, spell))
        {
            return false;
        }

        effectDescription = PowerBundle.ModifySpellEffect(spell, character.RulesetCharacter);

        if (!IsLeftoverBonusEffectCandidate(character, target, effectDescription, distance, hostile))
        {
            effectDescription = null;
            return false;
        }

        if (character.RulesetCharacter.ConcentratedSpell != null && spell.RequiresConcentration)
        {
            effectDescription = null;
            return false;
        }

        return true;
    }

    private static bool IsSpellReadyForLeftoverBonus(
        RulesetCharacter rulesetCharacter,
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spell)
    {
        if (rulesetCharacter == null || spellRepertoire == null || spell == null)
        {
            return false;
        }

        if (spell.SpellLevel <= 0)
        {
            return rulesetCharacter.CanCastCantrip(spell, out _);
        }

        return IsResidualHostileSpellReady(spellRepertoire, spell) &&
               spellRepertoire.CanCastSpell(spell, false) &&
               spellRepertoire.CanCastSpellOfLevel(spell.SpellLevel);
    }

    private static bool TryExecuteLeftoverBonusSpell(
        GameLocationCharacter character,
        GameLocationCharacter target,
        SpellDefinition spell,
        RulesetSpellRepertoire spellRepertoire,
        ActionModifier modifier)
    {
        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            spell == null ||
            spellRepertoire == null ||
            actionService == null ||
            implementationService == null)
        {
            return false;
        }

        var actionParams = new CharacterActionParams(character, Id.CastBonus)
        {
            ActionModifiers = { modifier ?? new ActionModifier() },
            IntParameter = spell.SpellLevel,
            StringParameter = spell.Name,
            RulesetEffect = implementationService.InstantiateEffectSpell(
                character.RulesetCharacter,
                spellRepertoire,
                spell,
                spell.SpellLevel,
                false),
            SpellRepertoire = spellRepertoire,
            TargetCharacters = { target }
        };

        actionService.ExecuteAction(actionParams, null, true);
        return true;
    }

    private static bool TryUseLeftoverBonusPower(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        bool hostile,
        bool selfOnlyUtility = false)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            character.GetActionStatus(Id.PowerBonus, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        var targets = hostile
            ? GetCurrentHostileActionTargets(character, battleService)
            : GetLeftoverUtilityTargets(character, selfOnlyUtility);

        foreach (var target in targets)
        {
            if (TryGetLeftoverBonusPowerFromPosition(
                    character,
                    target,
                    hostile,
                    out var usablePower) &&
                TryExecuteLeftoverBonusPower(character, target, usablePower))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetLeftoverBonusPowerFromPosition(
        GameLocationCharacter character,
        GameLocationCharacter target,
        bool hostile,
        out RulesetUsablePower usablePower)
    {
        usablePower = null;

        if (character?.RulesetCharacter == null || target?.RulesetCharacter == null)
        {
            return false;
        }

        foreach (var candidate in character.RulesetCharacter.UsablePowers)
        {
            var power = candidate?.PowerDefinition;
            var effectDescription = power?.EffectDescription;
            var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);

            if (power == null ||
                power.ActivationTime != ActivationTime.BonusAction ||
                !character.RulesetCharacter.CanUsePower(power, true, true) ||
                !IsLeftoverBonusEffectCandidate(character, target, effectDescription, distance, hostile))
            {
                continue;
            }

            usablePower = candidate;
            return true;
        }

        return false;
    }

    private static bool TryExecuteLeftoverBonusPower(
        GameLocationCharacter character,
        GameLocationCharacter target,
        RulesetUsablePower usablePower)
    {
        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            usablePower?.PowerDefinition == null ||
            actionService == null ||
            implementationService == null)
        {
            return false;
        }

        var actionParams = new CharacterActionParams(character, Id.PowerBonus)
        {
            ActionModifiers = { new ActionModifier() },
            RulesetEffect = implementationService.InstantiateEffectPower(
                character.RulesetCharacter,
                usablePower,
                false),
            UsablePower = usablePower,
            TargetCharacters = { target }
        };

        actionService.ExecuteAction(actionParams, null, true);
        return true;
    }

    private static bool IsLeftoverBonusEffectCandidate(
        GameLocationCharacter character,
        GameLocationCharacter target,
        EffectDescription effectDescription,
        float distance,
        bool hostile)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            effectDescription == null)
        {
            return false;
        }

        if (effectDescription.RangeParameter > 0 &&
            distance > effectDescription.RangeParameter + 0.5f)
        {
            return false;
        }

        if (hostile)
        {
            return target.Side != character.Side &&
                   effectDescription.TargetSide == Side.Enemy &&
                   effectDescription.TargetType is TargetType.Individuals or TargetType.IndividualsUnique;
        }

        if (target.Side != character.Side ||
            !IsSupportedLeftoverUtilityTarget(character, target, effectDescription))
        {
            return false;
        }

        var targetAssessment = BuildSelfAssessment(target);

        if (HasHealingForm(effectDescription))
        {
            return targetAssessment.IsBloodied ||
                   targetAssessment.IsCritical ||
                   targetAssessment.HasSeriousCondition;
        }

        return HasDefensiveBuffForm(effectDescription) &&
               !HasEquivalentActiveEffectOrCondition(target.RulesetCharacter, effectDescription) &&
               !IsLowValueSelfUtility(effectDescription);
    }

    private static bool IsSupportedLeftoverUtilityTarget(
        GameLocationCharacter character,
        GameLocationCharacter target,
        EffectDescription effectDescription)
    {
        return effectDescription.TargetType switch
        {
            TargetType.Self => target == character,
            TargetType.Individuals or TargetType.IndividualsUnique =>
                effectDescription.TargetSide == Side.Ally,
            _ => false
        };
    }

    private static bool HasHealingForm(EffectDescription effectDescription)
    {
        if (effectDescription?.EffectForms == null)
        {
            return false;
        }

        foreach (var effectForm in effectDescription.EffectForms)
        {
            if (effectForm.FormType == EffectForm.EffectFormType.Healing)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<GameLocationCharacter> GetLeftoverUtilityTargets(
        GameLocationCharacter character,
        bool selfOnly = false)
    {
        if (character?.RulesetCharacter == null)
        {
            yield break;
        }

        yield return character;

        if (selfOnly || Gui.Battle == null)
        {
            yield break;
        }

        foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), character.LocationPosition))
        {
            if (ally?.RulesetCharacter == null ||
                ally == character ||
                ally.Side != character.Side ||
                IsNonOccupyingCombatProxyTarget(ally))
            {
                continue;
            }

            yield return ally;
        }
    }

    internal static float ComputeEnemyProximityScore(
        DecisionContext context,
        ConsiderationDescription consideration,
        DecisionParameters parameters)
    {
        var character = parameters.character.GameLocationCharacter;
        EnsureCombatAiRuntimeCache();
        var rulesetCharacter = character.RulesetCharacter;
        var approachSourceGuid = GetApproachSourceGuid(rulesetCharacter, consideration.StringParameter);

        if (!IsAdvancedCombatAiEnabled ||
            approachSourceGuid != 0 ||
            ShouldUseBaselinePositionScoringAfterResolvedMain(character, consideration))
        {
            return ComputeBaselineEnemyProximityScore(context, consideration, parameters, approachSourceGuid);
        }

        var positionBased = IsPositionBasedScoring(consideration);
        var denominator = consideration.IntParameter > 0 ? consideration.IntParameter : 1;
        var floatParameter = consideration.FloatParameter;
        var position = consideration.BoolParameter ? context.position : character.LocationPosition;
        var profile = BuildProfile(character);

        UpdateObservedCombatMemory(character, parameters);

        var relevantEnemies = CollectRelevantEnemyTargets(character, parameters);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, parameters.situationalInformation.BattleService);

        if (relevantEnemies.Count == 0)
        {
            return IsAdvancedCombatAiPositioningEnabled && positionBased
                ? ComputeSearchOrRegroupScore(character, position, parameters, floatParameter, turnPlan)
                : 0f;
        }

        if (positionBased &&
            TryComputeTurnPlanMovementScore(character, profile, position, parameters, turnPlan, out var planScore))
        {
            return planScore;
        }

        var self = BuildSelfAssessment(character);
        var evaluations = CollectEnemyEvaluations(
            character,
            profile,
            position,
            parameters,
            approachSourceGuid,
            relevantEnemies);
        var reachableEnemyCount = 0;

        for (var i = 0; i < evaluations.Length; i++)
        {
            if (!evaluations[i].UnreachableFlyingForMelee)
            {
                reachableEnemyCount++;
            }
        }

        var numerator = 0.0f;

        for (var i = 0; i < evaluations.Length; i++)
        {
            var evaluation = evaluations[i];

            if (profile.IsMeleeSpecialist &&
                !profile.HasRangedBackup &&
                evaluation.UnreachableFlyingForMelee &&
                reachableEnemyCount > 0)
            {
                continue;
            }

            var distanceScore = evaluation.IsApproachSource
                ? Mathf.Lerp(
                    0.0f,
                    1f,
                    Mathf.Clamp(evaluation.Distance / Math.Max(floatParameter, 1f), 0.0f, 1f))
                : ComputeDistancePreferenceScore(profile, evaluation.Distance, floatParameter);

            if (!evaluation.IsApproachSource && evaluation.CanAttackFromPosition)
            {
                distanceScore = Math.Max(
                    distanceScore,
                    evaluation.RangedCoverType <= CoverType.Half ? 0.95f : 0.85f);
            }

            numerator += distanceScore * ComputeEnemyPriorityWeight(profile, evaluation);
        }

        var score = numerator / denominator;

        if (IsAdvancedCombatAiPositioningEnabled)
        {
            score += ComputePositionBias(profile, self, evaluations, floatParameter);

            if (positionBased)
            {
                score += ComputeFallbackApproachOrRegroupScore(
                    character,
                    profile,
                    position,
                    parameters,
                    evaluations,
                    floatParameter);
            }
        }

        return Mathf.Clamp01(score);
    }

    private static float ComputeBaselineEnemyProximityScore(
        DecisionContext context,
        ConsiderationDescription consideration,
        DecisionParameters parameters,
        ulong approachSourceGuid)
    {
        var character = parameters.character.GameLocationCharacter;
        var denominator = consideration.IntParameter > 0 ? consideration.IntParameter : 1;
        var scale = Math.Max(consideration.FloatParameter, 1f);
        var position = consideration.BoolParameter ? context.position : character.LocationPosition;
        var numerator = 0.0f;
        var hasRelevantPerceivedTarget = parameters.situationalInformation.HasRelevantPerceivedTarget;
        var positioningService = parameters.situationalInformation.PositioningService;

        foreach (var relevantEnemy in parameters.situationalInformation.RelevantEnemies)
        {
            if (relevantEnemy?.RulesetCharacter == null ||
                !AiLocationDefinitions.IsRelevantTargetForCharacter(character, relevantEnemy, hasRelevantPerceivedTarget))
            {
                continue;
            }

            var distance = positioningService.ComputeDistanceBetweenCharactersApproximatingSize(
                character, position, relevantEnemy, relevantEnemy.LocationPosition);

            numerator += relevantEnemy.Guid == approachSourceGuid
                ? Mathf.Lerp(0.0f, 1f, Mathf.Clamp(distance / scale, 0.0f, 1f))
                : Mathf.Lerp(1f, 0.0f, Mathf.Clamp(distance / scale, 0.0f, 1f));
        }

        return Mathf.Clamp01(numerator / denominator);
    }

    private static bool IsPositionBasedScoring(ConsiderationDescription consideration)
    {
        return consideration is { BoolParameter: true };
    }

    private static bool ShouldUseBaselinePositionScoringAfterResolvedMain(
        GameLocationCharacter character,
        ConsiderationDescription consideration)
    {
        if (!IsPositionBasedScoring(consideration) ||
            !TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction) ||
            committedMainAction.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain))
        {
            return false;
        }

        // Post-main movement is coordinated once after the vanilla AI process completes.
        // Reapplying the pre-main position policy here makes vanilla emit a new move chain
        // for each partial route before the remaining attack can be selected.
        return true;
    }

    private static ulong GetApproachSourceGuid(RulesetCharacter rulesetCharacter, string conditionName)
    {
        if (rulesetCharacter == null || string.IsNullOrEmpty(conditionName))
        {
            return 0;
        }

        foreach (var conditions in rulesetCharacter.ConditionsByCategory.Values)
        {
            foreach (var condition in conditions)
            {
                if (condition?.ConditionDefinition?.Name == conditionName)
                {
                    return condition.SourceGuid;
                }
            }
        }

        return 0;
    }

    private static void UpdateObservedCombatMemory(GameLocationCharacter actor, DecisionParameters parameters)
    {
        if (!IsAdvancedCombatAiPositioningEnabled ||
            actor?.RulesetCharacter == null ||
            parameters?.situationalInformation == null)
        {
            return;
        }

        var hasRelevantPerceivedTarget = parameters.situationalInformation.HasRelevantPerceivedTarget;
        var positioningService = parameters.situationalInformation.PositioningService;
        var bestDistance = float.MaxValue;
        var hasObservedEnemy = false;
        var lastKnownEnemyPosition = actor.LocationPosition;

        foreach (var enemy in GetKnownEnemyTargets(actor, parameters))
        {
            if (enemy?.RulesetCharacter == null ||
                !AiLocationDefinitions.IsRelevantTargetForCharacter(actor, enemy, hasRelevantPerceivedTarget))
            {
                continue;
            }

            var distance = positioningService.ComputeDistanceBetweenCharactersApproximatingSize(
                actor, actor.LocationPosition, enemy, enemy.LocationPosition);

            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            hasObservedEnemy = true;
            lastKnownEnemyPosition = enemy.LocationPosition;
        }

        if (!hasObservedEnemy)
        {
            return;
        }

        ObservedCombatMemoryCache[actor.Guid] = new ObservedCombatMemory(
            lastKnownEnemyPosition,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    internal static bool TryGetLastKnownEnemyPosition(GameLocationCharacter actor, out int3 position)
    {
        position = default;

        if (actor == null ||
            !ObservedCombatMemoryCache.TryGetValue(actor.Guid, out var memory) ||
            !IsObservedCombatMemoryFresh(memory))
        {
            return false;
        }

        position = memory.LastKnownEnemyPosition;

        return true;
    }

    private static GameLocationCharacter[] GetKnownEnemyTargets(
        GameLocationCharacter actor,
        DecisionParameters parameters = null)
    {
        if (actor?.RulesetCharacter == null)
        {
            return Array.Empty<GameLocationCharacter>();
        }

        var targets = new List<GameLocationCharacter>();

        AddKnownEnemyTargets(actor, SnapshotCharacters(actor.PerceivedFoes), targets);

        if (parameters?.situationalInformation?.RelevantEnemies != null)
        {
            AddKnownEnemyTargets(actor, parameters.situationalInformation.RelevantEnemies, targets);
        }

        if (Gui.Battle != null)
        {
            var contenders = Gui.Battle.AllContenders.ToArray();

            foreach (var ally in OrderCharactersForCombatAi(contenders, actor.LocationPosition))
            {
                if (ally == actor || ally?.RulesetCharacter == null || ally.Side != actor.Side)
                {
                    continue;
                }

                AddKnownEnemyTargets(actor, SnapshotCharacters(ally.PerceivedFoes), targets);
            }
        }

        return targets
            .OrderBy(target => ComputeGridDistance(actor.LocationPosition, target.LocationPosition))
            .ThenByDescending(target => ComputeTacticalTargetTieBreakPriority(actor, target))
            .ThenBy(target => target.LocationPosition.x)
            .ThenBy(target => target.LocationPosition.y)
            .ThenBy(target => target.LocationPosition.z)
            .ThenBy(GetStableCharacterSortName, StringComparer.Ordinal)
            .ThenBy(target => target.Guid)
            .ToArray();
    }

    private static void AddKnownEnemyTargets(
        GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> candidates,
        List<GameLocationCharacter> targets)
    {
        if (candidates == null)
        {
            return;
        }

        foreach (var target in candidates)
        {
            if (target?.RulesetCharacter == null ||
                target.Side == actor.Side ||
                IsNonOccupyingCombatProxyTarget(target) ||
                targets.Contains(target))
            {
                continue;
            }

            targets.Add(target);
        }
    }

    private static GameLocationCharacter[] GetCurrentHostileActionTargets(
        GameLocationCharacter actor,
        IGameLocationBattleService battleService,
        GameLocationCharacter priorityTarget = null)
    {
        if (actor?.RulesetCharacter == null)
        {
            return Array.Empty<GameLocationCharacter>();
        }

        var targets = new List<GameLocationCharacter>();

        if (priorityTarget?.RulesetCharacter != null)
        {
            AddKnownEnemyTargets(actor, new[] { priorityTarget }, targets);
        }

        AddKnownEnemyTargets(actor, GetKnownEnemyTargets(actor), targets);

        if (Gui.Battle == null || battleService == null)
        {
            return targets.ToArray();
        }

        var actionKinds = GetCurrentHostileTargetActionKinds(actor);

        foreach (var candidate in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), actor.LocationPosition))
        {
            if (candidate?.RulesetCharacter == null ||
                candidate == actor ||
                candidate.Side == actor.Side ||
                IsNonOccupyingCombatProxyTarget(candidate) ||
                targets.Contains(candidate))
            {
                continue;
            }

            if (CanUseCurrentHostileActionAgainst(
                    actor,
                    candidate,
                    battleService,
                    actionKinds) ||
                CanAttackInMeleeFromPosition(
                    candidate,
                    candidate.LocationPosition,
                    actor,
                    actor.LocationPosition,
                    battleService) ||
                ComputeGridDistance(actor.LocationPosition, candidate.LocationPosition) <= 1.5f)
            {
                targets.Add(candidate);
            }
        }

        return targets.ToArray();
    }

    private static CombatAiActionKind[] GetCurrentHostileTargetActionKinds(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return Array.Empty<CombatAiActionKind>();
        }

        var capabilityCatalog = BuildCapabilityCatalog(actor);
        var profile = BuildProfile(actor);
        var preferred = GetPreferredActionKind(actor, profile, capabilityCatalog);
        var backup = GetBackupActionKind(actor, preferred, capabilityCatalog);
        var actionKinds = new List<CombatAiActionKind>();

        AddTerminalReprobeActionKind(actionKinds, preferred);
        AddTerminalReprobeActionKind(actionKinds, backup);

        if (capabilityCatalog.HasAnyRanged)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Ranged);
        }

        if (capabilityCatalog.HasMelee)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Melee);
        }

        if (capabilityCatalog.HasAtWillHostileSpell)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Spell);
        }

        return actionKinds.ToArray();
    }

    private static bool CanUseCurrentHostileActionAgainst(
        GameLocationCharacter actor,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        IEnumerable<CombatAiActionKind> actionKinds)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            target.Side == actor.Side ||
            battleService == null)
        {
            return false;
        }

        foreach (var actionKind in actionKinds)
        {
            if (CanUseActionKindAtPosition(
                    actor,
                    actor.LocationPosition,
                    target,
                    actionKind,
                    battleService))
            {
                return true;
            }
        }

        return TryGetResidualHostileSpellFromPosition(
            actor,
            actor.LocationPosition,
            target,
            battleService,
            out _,
            out _,
            out _);
    }

    private static int ComputeTacticalTargetTieBreakPriority(
        GameLocationCharacter actor,
        GameLocationCharacter target)
    {
        if (actor?.RulesetCharacter == null || target?.RulesetCharacter == null)
        {
            return 0;
        }

        var targetSelf = BuildSelfAssessment(target);
        var priority = 0;

        if (targetSelf.IsConcentrating)
        {
            priority += 4;
        }

        if (targetSelf.IsCritical)
        {
            priority += 3;
        }
        else if (targetSelf.IsBloodied)
        {
            priority += 2;
        }

        if (targetSelf.HasSeriousCondition)
        {
            priority += 1;
        }

        if (HasDodgingCondition(target.RulesetCharacter))
        {
            priority -= 2;
        }

        if (TryGetRecentMeleeThreat(actor, out var threat) &&
            threat.Source == target)
        {
            priority += 2;
        }

        return priority;
    }

    private static GameLocationCharacter[] SnapshotCharacters(IEnumerable<GameLocationCharacter> characters)
    {
        if (characters == null)
        {
            return Array.Empty<GameLocationCharacter>();
        }

        try
        {
            return characters.ToArray();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<GameLocationCharacter>();
        }
    }

    private static float ComputeSearchOrRegroupScore(
        GameLocationCharacter actor,
        int3 position,
        DecisionParameters parameters,
        float floatParameter,
        CombatAiTurnPlan turnPlan)
    {
        if (actor?.RulesetCharacter == null)
        {
            return 0f;
        }

        if (TryComputeTurnPlanMovementScore(actor, BuildProfile(actor), position, parameters, turnPlan, out var planScore))
        {
            return planScore;
        }

        if (TryGetLastKnownEnemyPosition(actor, out var lastKnownEnemyPosition))
        {
            return ComputeApproachPositionScore(
                actor.LocationPosition,
                position,
                lastKnownEnemyPosition,
                floatParameter,
                0.65f);
        }

        return 0f;
    }

    private static float ComputeFallbackApproachOrRegroupScore(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        int3 position,
        DecisionParameters parameters,
        EnemyEvaluation[] evaluations,
        float floatParameter)
    {
        if (actor?.RulesetCharacter == null || evaluations.Length == 0)
        {
            return 0f;
        }

        for (var i = 0; i < evaluations.Length; i++)
        {
            if (evaluations[i].CanAttackFromPosition)
            {
                return 0f;
            }
        }

        if (TryGetVisibleApproachAnchor(actor, profile, position, evaluations, out var visibleEnemyPosition))
        {
            return ComputeApproachPositionScore(
                actor.LocationPosition,
                position,
                visibleEnemyPosition,
                floatParameter,
                0.55f);
        }

        if (TryGetLastKnownEnemyPosition(actor, out var lastKnownEnemyPosition))
        {
            return ComputeApproachPositionScore(
                actor.LocationPosition,
                position,
                lastKnownEnemyPosition,
                floatParameter,
                0.55f);
        }

        return 0f;
    }

    private static bool TryGetVisibleApproachAnchor(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        int3 candidatePosition,
        EnemyEvaluation[] evaluations,
        out int3 position)
    {
        position = default;

        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        var currentPosition = actor.LocationPosition;
        var bestScore = 0f;
        var hasAnchor = false;

        for (var i = 0; i < evaluations.Length; i++)
        {
            var evaluation = evaluations[i];
            var enemy = evaluation.Enemy;

            if (enemy?.RulesetCharacter == null || IsNonOccupyingCombatProxyTarget(enemy))
            {
                continue;
            }

            var currentDistance = ComputeGridDistance(currentPosition, enemy.LocationPosition);
            var candidateDistance = ComputeGridDistance(candidatePosition, enemy.LocationPosition);

            if (candidateDistance >= currentDistance)
            {
                continue;
            }

            var improvement = 1f - Mathf.Clamp01(candidateDistance / Math.Max(currentDistance, 1f));
            var score = ComputeEnemyPriorityWeight(profile, evaluation) + improvement;

            if (evaluation.IsApproachSource)
            {
                score += 0.25f;
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            position = enemy.LocationPosition;
            hasAnchor = true;
        }

        return hasAnchor;
    }

    private static float ComputeApproachPositionScore(
        int3 currentPosition,
        int3 candidatePosition,
        int3 targetPosition,
        float floatParameter,
        float maxScore)
    {
        var currentDistance = ComputeGridDistance(currentPosition, targetPosition);
        var candidateDistance = ComputeGridDistance(candidatePosition, targetPosition);

        if (candidateDistance >= currentDistance)
        {
            return 0f;
        }

        var scale = Math.Max(Math.Max(currentDistance, floatParameter), 1f);
        var score = 1f - Mathf.Clamp01(candidateDistance / scale);

        return Mathf.Clamp(score * maxScore, 0f, maxScore);
    }

    private static bool IsObservedCombatMemoryFresh(ObservedCombatMemory memory)
    {
        var currentRound = GetCurrentBattleRound();

        if (currentRound > 0 && memory.Round > 0)
        {
            return currentRound - memory.Round <= ObservedCombatMemoryMaxRounds;
        }

        return ObservedCombatMemoryTurnStamp - memory.TurnStamp <= ObservedCombatMemoryMaxTurns;
    }

    private static int GetCurrentBattleRound()
    {
        return Gui.Battle?.CurrentRound ?? 0;
    }

    private static void PrimeTurnMovementProgress(GameLocationCharacter character, CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null || !turnPlan.MovementPlan.HasGoal)
        {
            return;
        }

        var currentPosition = character.LocationPosition;
        var currentDistance = ComputeGridDistance(currentPosition, turnPlan.MovementPlan.TargetPosition);

        if (!TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress) ||
            !movementProgress.Matches(turnPlan))
        {
            movementProgress = new AiTurnMovementProgress(currentPosition, currentDistance, turnPlan);
            TurnMovementProgressCache[character.Guid] = movementProgress;
        }
        else
        {
            movementProgress.BeginEvaluation(currentPosition, currentDistance);
        }

        PrimeFlyingMeleeSupplementalMovementProgress(character, turnPlan, movementProgress);
    }

    private static void PrimeGroundMeleeTargetContactRouteQuery(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null || !IsGroundMeleePursuitPlan(turnPlan))
        {
            return;
        }

        if (TryGetCachedGroundMeleeTargetContactRouteQuery(character, turnPlan, out _))
        {
            return;
        }

        _ = TryBuildGroundMeleeTargetContactRouteQuery(
            character,
            turnPlan.ActionProbe.Target,
            turnPlan.MovementPlan.TargetPosition,
            character.LocationPosition,
            out _);
    }

    private static AiTurnMovementProgress GetOrCreateTurnMovementProgress(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character == null)
        {
            return null;
        }

        if (TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress) &&
            movementProgress.Matches(turnPlan))
        {
            return movementProgress;
        }

        if (!turnPlan.MovementPlan.HasGoal)
        {
            return null;
        }

        movementProgress = new AiTurnMovementProgress(
            character.LocationPosition,
            ComputeGridDistance(character.LocationPosition, turnPlan.MovementPlan.TargetPosition),
            turnPlan);
        TurnMovementProgressCache[character.Guid] = movementProgress;

        return movementProgress;
    }

    private static void UpdateTurnMovementProgress(GameLocationCharacter character)
    {
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (character?.RulesetCharacter == null || battleService == null)
        {
            return;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        if (!turnPlan.MovementPlan.HasGoal)
        {
            return;
        }

        var movementProgress = GetOrCreateTurnMovementProgress(character, turnPlan);

        movementProgress?.BeginEvaluation(
            character.LocationPosition,
            ComputeGridDistance(character.LocationPosition, turnPlan.MovementPlan.TargetPosition));
    }

    private static void PrimeFlyingMeleeSupplementalMovementProgress(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        AiTurnMovementProgress movementProgress)
    {
        if (!IsFlyingMeleeMovementPlan(turnPlan))
        {
            return;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var target = turnPlan.ActionProbe.Target;

        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            movementProgress == null ||
            !BuildProfile(actor).HasFlight)
        {
            return;
        }

        var start = actor.LocationPosition;
        var currentDistance = ComputeGridDistance(start, target.LocationPosition);

        foreach (var candidate in EnumerateFlyingMeleeCandidatePositions(actor, target))
        {
            if (candidate == start || movementProgress.HasVisited(candidate))
            {
                continue;
            }

            if (IsBlockingCombatantAtPosition(actor, candidate))
            {
                continue;
            }

            if (RequiresMainDashForForcedMove(actor, start, candidate))
            {
                continue;
            }

            if (HasForcedRouteOpportunityExposure(actor, start, candidate, battleService))
            {
                continue;
            }

            if (!CanUseActionKindAtPosition(actor, candidate, target, CombatAiActionKind.Melee, battleService))
            {
                continue;
            }

            if (ShouldRejectMeleeSpacingTrafficMove(actor, candidate, target, battleService))
            {
                continue;
            }

            var crowding = EvaluateMeleeAllyCrowding(actor, target, candidate);
            var clearsCrowding = crowding.AdjacentCount == 0 &&
                                 crowding.NearestHorizontalGap >= MeleeSpacingRequiredGridGap;

            if (!clearsCrowding &&
                turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.FlyingMeleeSpacingPolicy)
            {
                continue;
            }

            var progress = currentDistance - ComputeGridDistance(candidate, target.LocationPosition);
            var score =
                MovementGoalPreferredRangeScore +
                ComputeFlyingMeleeHeightScore(actor, candidate, target) +
                (clearsCrowding ? 0.32f : -0.18f) +
                Math.Min(0.10f, Math.Max(0f, progress) * 0.03f) +
                ComputeStableTieBreakScore(actor, turnPlan, candidate, CombatAiActionKind.Melee);

            movementProgress.MarkMeaningfulMovementCandidate();
            movementProgress.MarkPreferredActionMovementCandidate();
            movementProgress.RecordAccepted(candidate, score, progress, preferredActionCandidate: true, turnsToAction: 0);
        }
    }

    private static bool IsFlyingMeleeMovementPlan(CombatAiTurnPlan turnPlan)
    {
        return (turnPlan.MovementPlan.Policy is CombatAiMovementPolicyKind.FlyingPursuitPolicy
                   or CombatAiMovementPolicyKind.FlyingMeleeSpacingPolicy) &&
               turnPlan.ActionProbe.PreferredAction == CombatAiActionKind.Melee &&
               turnPlan.ActionProbe.Target?.RulesetCharacter != null;
    }

    private static IEnumerable<int3> EnumerateFlyingMeleeCandidatePositions(
        GameLocationCharacter actor,
        GameLocationCharacter target)
    {
        var start = actor.LocationPosition;
        var targetPosition = target.LocationPosition;
        var remainingMove = Math.Max(0, actor.RemainingTacticalMoves);
        var minY = Math.Max(targetPosition.y - FlyingMeleeCandidateHeightBelowTarget, start.y - remainingMove);
        var maxY = Math.Min(targetPosition.y + FlyingMeleeCandidateHeightAboveTarget, start.y + remainingMove);
        var seen = new HashSet<int3>();

        for (var y = maxY; y >= minY; y--)
        {
            for (var radius = 0; radius <= FlyingMeleeCandidateHorizontalRadius; radius++)
            {
                for (var x = -radius; x <= radius; x++)
                {
                    for (var z = -radius; z <= radius; z++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius)
                        {
                            continue;
                        }

                        var candidate = new int3(targetPosition.x + x, y, targetPosition.z + z);

                        if (seen.Add(candidate))
                        {
                            yield return candidate;
                        }
                    }
                }
            }
        }
    }

    private static bool IsLegalAiRouteDestination(
        GameLocationCharacter actor,
        int3 destination,
        bool allowActorCurrentPosition = false,
        bool allowNonOccupyingCombatProxy = true)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        if (destination == actor.LocationPosition)
        {
            if (allowActorCurrentPosition)
            {
                return true;
            }

            return false;
        }

        if (IsBlockingCombatantAtPosition(actor, destination))
        {
            return false;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null ||
            positioningService.CanPlaceCharacter(actor, destination, CellHelpers.PlacementMode.Station) ||
            (allowNonOccupyingCombatProxy &&
             IsAiPlacementBlockedOnlyByNonOccupyingCombatProxy(actor, destination)))
        {
            return true;
        }

        return false;
    }

    private static bool IsBlockingCombatantAtPosition(GameLocationCharacter actor, int3 position)
    {
        return TryGetBlockingCombatantAtPosition(actor, position, out _);
    }

    private static bool TryGetBlockingCombatantAtPosition(
        GameLocationCharacter actor,
        int3 position,
        out GameLocationCharacter occupant)
    {
        occupant = null;

        if (actor?.RulesetCharacter == null || Gui.Battle == null)
        {
            return false;
        }

        foreach (var contender in Gui.Battle.AllContenders.ToArray())
        {
            if (contender == actor ||
                contender is not { destroying: false, destroyedBody: false, RulesetCharacter: not null } ||
                contender.LocationPosition != position ||
                IsNonOccupyingCombatProxyTarget(contender))
            {
                continue;
            }

            occupant = contender;
            return true;
        }

        return false;
    }

    private static float ComputeGridDistance(int3 left, int3 right)
    {
        var deltaX = left.x - right.x;
        var deltaY = left.y - right.y;
        var deltaZ = left.z - right.z;

        return Mathf.Sqrt((deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ));
    }

    private static int ComputeForcedMoveCost(int3 start, int3 destination)
    {
        return Math.Max(
            Math.Abs(destination.x - start.x),
            Math.Max(
                Math.Abs(destination.y - start.y),
                Math.Abs(destination.z - start.z)));
    }

    private static int ComputeHorizontalGridStepDistance(int3 left, int3 right)
    {
        return Math.Max(
            Math.Abs(left.x - right.x),
            Math.Abs(left.z - right.z));
    }

    private static bool RequiresMainDashForForcedMove(
        GameLocationCharacter character,
        int3 start,
        int3 destination)
    {
        var requiredMove = ComputeForcedMoveCost(start, destination);
        var remainingMove = Math.Max(0, character?.RemainingTacticalMoves ?? 0);

        if (requiredMove <= remainingMove)
        {
            return false;
        }

        return true;
    }

    private static float ComputeStableTieBreakScore(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        int3 candidatePosition,
        CombatAiActionKind actionKind)
    {
        unchecked
        {
            var hash = 2166136261u;
            var startPosition = TurnMovementProgressCache.TryGetValue(actor?.Guid ?? 0, out var progress)
                ? progress.StartPosition
                : actor?.LocationPosition ?? default;
            var targetPosition = turnPlan.ActionProbe.Target?.LocationPosition ?? turnPlan.MovementPlan.TargetPosition;

            AddStablePositionHash(ref hash, startPosition);
            AddStablePositionHash(ref hash, targetPosition);
            AddStablePositionHash(ref hash, candidatePosition);
            AddStableHashComponent(ref hash, (uint)actionKind);
            AddStableHashComponent(ref hash, (uint)GetCurrentBattleRound());

            return (hash & 0x3ff) * (StableTieBreakScoreScale / 1023f);
        }
    }

    private static void AddStablePositionHash(ref uint hash, int3 position)
    {
        AddStableHashComponent(ref hash, (uint)position.x);
        AddStableHashComponent(ref hash, (uint)position.y);
        AddStableHashComponent(ref hash, (uint)position.z);
    }

    private static void AddStableHashComponent(ref uint hash, ulong value)
    {
        unchecked
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= 16777619u;
            }
        }
    }

    private static bool TryGetRouteCandidateCacheKey(
        GameLocationCharacter actor,
        int3 actorPosition,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        out RouteCandidateCacheKey cacheKey)
    {
        cacheKey = default;

        if (actor?.RulesetCharacter == null || target?.RulesetCharacter == null)
        {
            return false;
        }

        cacheKey = new RouteCandidateCacheKey(
            actor.Guid,
            actorPosition,
            target.Guid,
            target.LocationPosition,
            actionKind,
            ComputeCombatAiActionStateSignature(actor),
            ComputeCombatAiTargetStateSignature(target));

        return true;
    }

    private static int ComputeCombatAiActionStateSignature(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return 0;
        }

        unchecked
        {
            var hash = actor.CurrentActionRankByType[ActionType.Main];

            hash = (hash * 397) ^ (int)actor.GetActionTypeStatus(ActionType.Main);
            hash = (hash * 397) ^ (int)actor.GetActionStatus(Id.AttackMain, ActionScope.Battle);
            hash = (hash * 397) ^ (int)actor.GetActionStatus(Id.CastMain, ActionScope.Battle);
            hash = (hash * 397) ^ actor.GetActionAvailableIterations(Id.AttackMain);
            hash = (hash * 397) ^ actor.UsedMainAttacks;
            hash = (hash * 397) ^ GetActionUseCount(TurnMainActionUseCountCache, actor);

            if (TryGetCurrentTurnActionMemory(actor, LastMainActionExecutionCache, out var lastMainAction))
            {
                hash = (hash * 397) ^ (int)lastMainAction.ActionId;
                hash = (hash * 397) ^ lastMainAction.ActionRank;
            }

            return hash;
        }
    }

    private static int ComputeCombatAiTargetStateSignature(GameLocationCharacter target)
    {
        var rulesetTarget = target?.RulesetCharacter;

        if (rulesetTarget == null)
        {
            return 0;
        }

        unchecked
        {
            var hash = rulesetTarget.IsDeadOrDyingOrUnconscious ? 1 : 0;

            hash = (hash * 397) ^ (rulesetTarget.IsIncapacitated ? 1 : 0);
            hash = (hash * 397) ^ (rulesetTarget.IsTouchingGround() ? 1 : 0);

            return hash;
        }
    }

    private static bool TryGetRangedAttackModifierFromPosition(
        GameLocationCharacter attacker,
        int3 attackerPosition,
        GameLocationCharacter target,
        int3 targetPosition,
        IGameLocationBattleService battleService,
        out ActionModifier bestModifier)
    {
        bestModifier = null;

        if (attacker?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        var bestCover = CoverType.ThreeQuarter;
        var distance = ComputeGridDistance(attackerPosition, targetPosition);

        foreach (var mode in attacker.RulesetCharacter.AttackModes)
        {
            if (mode == null ||
                !IsRangedAttackMode(mode) ||
                !IsAttackModeAvailableForMainAction(attacker, mode, out _))
            {
                continue;
            }

            if (mode.MaxRange > 0f && distance > mode.MaxRange + 0.5f)
            {
                continue;
            }

            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();

            attackParams.FillForPhysicalRangeAttack(
                attacker, attackerPosition, mode, target, targetPosition, modifier);

            if (!battleService.CanAttack(attackParams))
            {
                continue;
            }

            if (bestModifier == null || modifier.coverType < bestCover)
            {
                bestModifier = modifier;
                bestCover = modifier.coverType;
            }

            if (modifier.coverType == CoverType.None)
            {
                return true;
            }
        }

        return bestModifier != null;
    }

    private static bool TryGetAtWillSpellAttackModifierFromPosition(
        GameLocationCharacter attacker,
        int3 attackerPosition,
        GameLocationCharacter target,
        int3 targetPosition,
        IGameLocationBattleService battleService,
        out ActionModifier bestModifier)
    {
        return TryGetAtWillSpellAttackFromPosition(
            attacker,
            attackerPosition,
            target,
            targetPosition,
            battleService,
            out _,
            out _,
            out bestModifier);
    }

    private static bool TryGetAtWillSpellAttackFromPosition(
        GameLocationCharacter attacker,
        int3 attackerPosition,
        GameLocationCharacter target,
        int3 targetPosition,
        IGameLocationBattleService battleService,
        out SpellDefinition spell,
        out RulesetSpellRepertoire spellRepertoire,
        out ActionModifier bestModifier)
    {
        bestModifier = null;
        spell = null;
        spellRepertoire = null;

        if (attacker?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            !CanUseMainSpellActionForAi(attacker))
        {
            return false;
        }

        var cantrips = new List<SpellDefinition>();
        var rulesetAttacker = attacker.RulesetCharacter;
        var distance = ComputeGridDistance(attackerPosition, targetPosition);

        rulesetAttacker.EnumerateReadyAttackCantrips(cantrips);

        foreach (var cantrip in OrderAtWillAttackCantrips(rulesetAttacker, cantrips))
        {
            if (cantrip == null ||
                cantrip.ActivationTime != ActivationTime.Action ||
                !rulesetAttacker.CanCastCantrip(cantrip, out var repertoire) ||
                cantrip.EffectDescription.TargetType is not (TargetType.Individuals or TargetType.IndividualsUnique))
            {
                continue;
            }

            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();
            var effectDescription = PowerBundle.ModifySpellEffect(cantrip, rulesetAttacker);

            if (effectDescription == null ||
                effectDescription.RangeParameter > 0 &&
                distance > effectDescription.RangeParameter + 0.5f)
            {
                continue;
            }

            attackParams.FillForMagic(
                attacker,
                attackerPosition,
                effectDescription,
                cantrip.Name,
                target,
                targetPosition,
                modifier);

            if (!battleService.CanAttack(attackParams))
            {
                continue;
            }

            bestModifier = modifier;
            spell = cantrip;
            spellRepertoire = repertoire;

            return true;
        }

        return false;
    }

    private static IEnumerable<GameLocationCharacter> OrderCharactersForCombatAi(
        IEnumerable<GameLocationCharacter> characters,
        int3 referencePosition)
    {
        if (characters == null)
        {
            return Array.Empty<GameLocationCharacter>();
        }

        return characters
            .Where(character => character?.RulesetCharacter != null)
            .OrderBy(character => ComputeGridDistance(referencePosition, character.LocationPosition))
            .ThenBy(character => character.LocationPosition.x)
            .ThenBy(character => character.LocationPosition.y)
            .ThenBy(character => character.LocationPosition.z)
            .ThenBy(GetStableCharacterSortName, StringComparer.Ordinal)
            .ThenBy(character => character.Guid)
            .ToArray();
    }

    private static string GetStableCharacterSortName(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return string.Empty;
        }

        var definitionName = character.RulesetCharacter is RulesetCharacterMonster monster
            ? monster.MonsterDefinition?.Name
            : null;

        return string.Join(
            "|",
            definitionName ?? string.Empty,
            character.RulesetCharacter.Name ?? string.Empty,
            character.Name ?? string.Empty);
    }

    private static CombatAiSpellCapabilitySummary BuildAtWillHostileSpellSummary(RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter == null)
        {
            return new CombatAiSpellCapabilitySummary(0, 0f);
        }

        var cantrips = new List<SpellDefinition>();
        var maximumRange = 0f;
        var count = 0;

        rulesetCharacter.EnumerateReadyAttackCantrips(cantrips);

        foreach (var cantrip in cantrips)
        {
            if (cantrip == null ||
                cantrip.ActivationTime != ActivationTime.Action ||
                cantrip.EffectDescription.TargetType is not (TargetType.Individuals or TargetType.IndividualsUnique))
            {
                continue;
            }

            if (!rulesetCharacter.CanCastCantrip(cantrip, out _))
            {
                continue;
            }

            count++;
            maximumRange = Math.Max(maximumRange, GetSpellRange(rulesetCharacter, cantrip));
        }

        return new CombatAiSpellCapabilitySummary(
            count,
            maximumRange);
    }

    private static IEnumerable<SpellDefinition> OrderAtWillAttackCantrips(
        RulesetCharacter rulesetCharacter,
        IEnumerable<SpellDefinition> cantrips)
    {
        return (cantrips ?? Array.Empty<SpellDefinition>())
            .Where(cantrip => cantrip != null)
            .OrderByDescending(cantrip => GetSpellRange(rulesetCharacter, cantrip))
            .ThenBy(cantrip => cantrip.Name ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private static float GetSpellRange(RulesetCharacter rulesetCharacter, SpellDefinition spell)
    {
        return PowerBundle.ModifySpellEffect(spell, rulesetCharacter)?.RangeParameter ?? 0f;
    }

    private static CombatAiTurnPlan BuildCombatAiTurnPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        IGameLocationBattleService battleService)
    {
        var actionProbe = BuildCombatAiActionProbe(actor, profile, battleService);
        var movementPlan = BuildCombatAiMovementPlan(actor, profile, actionProbe);

        return new CombatAiTurnPlan(
            actionProbe,
            movementPlan,
            HasRemainingAttackMainContinuation(actor));
    }

    private static bool TryBuildPostMainClearAllyCorridorTurnPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        IGameLocationBattleService battleService,
        out CombatAiTurnPlan turnPlan)
    {
        turnPlan = default;

        if (actor?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var actionProbe = BuildCombatAiActionProbe(actor, profile, battleService);
        var target = actionProbe.Target;

        if (target?.RulesetCharacter == null ||
            actionProbe.PreferredAction != CombatAiActionKind.Melee)
        {
            return false;
        }

        if (profile.HasFlight)
        {
            return false;
        }

        if (!IsBlockingLargeMeleeAllyCorridor(actor, target, battleService, out var corridorAllyGuid))
        {
            return false;
        }

        var movementPlan = new CombatAiMovementPlan(
            CombatAiMovementGoalKind.MeleeSpacing,
            IsFlyingMeleeSpacingActor(actor, actionProbe)
                ? CombatAiMovementPolicyKind.FlyingMeleeSpacingPolicy
                : CombatAiMovementPolicyKind.MeleeSpacingPolicy,
            target,
            target.LocationPosition,
            CombatAiMovementPlanReasonKind.ClearAllyCorridor,
            corridorAllyGuid);

        turnPlan = new CombatAiTurnPlan(
            actionProbe,
            movementPlan);

        return true;
    }

    private static bool TryUsePostMainClearAllyCorridorMove(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        out int3 destination)
    {
        destination = default;

        var target = turnPlan.ActionProbe.Target;

        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            !IsClearAllyCorridorMovementPlan(turnPlan))
        {
            return false;
        }

        var start = actor.LocationPosition;
        var currentCanMelee =
            CanUseActionKindAtPosition(actor, start, target, CombatAiActionKind.Melee, battleService);
        var bestScore = float.MinValue;
        var bestDestination = default(int3);

        foreach (var candidate in EnumeratePostMainClearAllyCorridorStepPositions(start))
        {
            var candidateResult = ValidatePostMainClearAllyCorridorCandidate(
                actor,
                turnPlan,
                battleService,
                candidate,
                currentCanMelee);

            if (!candidateResult.Accepted)
            {
                continue;
            }

            if (candidateResult.Score <= bestScore)
            {
                continue;
            }

            bestScore = candidateResult.Score;
            bestDestination = candidate;
        }

        if (bestScore <= float.MinValue)
        {
            return false;
        }

        var chainStart = start;
        var chainDestination = bestDestination;
        var allyGuid = turnPlan.MovementPlan.AllyGuid.ToString();
        var targetGuid = target.Guid;
        if (!actor.MyExecuteActionTacticalMove(
                chainDestination,
                aborted => CompletePostMainClearAllyCorridorAttempt(
                    actor,
                    chainStart,
                    chainDestination,
                    allyGuid,
                    targetGuid,
                    aborted),
                 null))
        {
            return false;
        }

        destination = chainDestination;
        RecordPostMainClearAllyCorridorAttemptStarted(actor, chainStart, chainDestination, allyGuid, targetGuid);

        return true;
    }

    private static IEnumerable<int3> EnumeratePostMainClearAllyCorridorStepPositions(int3 start)
    {
        for (var x = -1; x <= 1; x++)
        {
            for (var z = -1; z <= 1; z++)
            {
                if (x == 0 && z == 0)
                {
                    continue;
                }

                yield return new int3(start.x + x, start.y, start.z + z);
            }
        }
    }

    private static ClearAllyCorridorCandidateResult ValidatePostMainClearAllyCorridorCandidate(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 candidate,
        bool currentCanMelee)
    {
        var target = turnPlan.ActionProbe.Target;
        var start = actor.LocationPosition;

        if (candidate == start)
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        if (!IsLegalAiRouteDestination(
                actor,
                candidate,
                allowNonOccupyingCombatProxy: false))
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        if (IsRejectedAiMoveTarget(actor, start, candidate))
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        var moveCost = ComputeForcedMoveCost(start, candidate);
        var maximumMoveCost = Math.Min(Math.Max(0, actor.RemainingTacticalMoves), MeleeSpacingMaximumMoveCost);

        if (moveCost <= 0 || moveCost > maximumMoveCost)
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        if (WouldClearAllyCorridorTriggerOpportunityAttack(
                actor,
                start,
                candidate,
                target,
                battleService))
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        if (WouldClearAllyCorridorStillBlockTargetAlly(
                actor,
                candidate,
                target,
                battleService,
                turnPlan))
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        var candidateCanMelee =
            CanUseActionKindAtPosition(actor, candidate, target, CombatAiActionKind.Melee, battleService);

        if (currentCanMelee && !candidateCanMelee)
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        var currentDistance = ComputeGridDistance(start, target.LocationPosition);
        var candidateDistance = ComputeGridDistance(candidate, target.LocationPosition);

        if (candidateDistance > currentDistance + ClearAllyCorridorRegressionTolerance)
        {
            return new ClearAllyCorridorCandidateResult(false);
        }

        var trafficPenalty = 0f;

        if (ShouldRejectAllyFireLaneGate(actor, candidate, target, battleService))
        {
            trafficPenalty = 0.10f;
        }

        var score =
            MovementGoalPreferredRangeScore +
            ComputeStableTieBreakScore(actor, turnPlan, candidate, CombatAiActionKind.Melee) +
            (candidateCanMelee ? 0.15f : 0f) -
            (moveCost * 0.04f) -
            Math.Max(0f, candidateDistance - currentDistance) * 0.03f -
            trafficPenalty;
        return new ClearAllyCorridorCandidateResult(true, score);
    }

    private static CombatAiActionProbe BuildCombatAiActionProbe(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        IGameLocationBattleService battleService)
    {
        GameLocationCharacter target;
        var capabilityCatalog = BuildCapabilityCatalog(actor);
        var hasAtWillHostileSpell = capabilityCatalog.HasAtWillHostileSpell;
        var preferred = GetPreferredActionKind(actor, profile, capabilityCatalog);
        var backup = GetBackupActionKind(actor, preferred, capabilityCatalog);

        if (!CanAttemptActionKindThisTurn(actor, preferred) &&
            CanAttemptActionKindThisTurn(actor, backup))
        {
            (preferred, backup) = (backup, preferred);
        }

        SelectActionProbeTarget(
            actor,
            preferred,
            backup,
            battleService,
            out target,
            out var canPreferred,
            out var canBackup);
        var spellRange = capabilityCatalog.AtWillHostileSpellMaximumRange;
        var spellDistance = target == null ? 0f : ComputeGridDistance(actor.LocationPosition, target.LocationPosition);
        var spellRangeBlocked = preferred == CombatAiActionKind.Spell &&
                                spellRange > 0f &&
                                spellDistance > spellRange + 0.5f;
        var actionValidationMismatch = preferred == CombatAiActionKind.Spell &&
                                       canPreferred &&
                                       spellRangeBlocked;

        if (actionValidationMismatch)
        {
            canPreferred = false;
        }

        return new CombatAiActionProbe(
            preferred,
            backup,
            target,
            canPreferred,
            canBackup,
            hasAtWillHostileSpell,
            capabilityCatalog);
    }

    private static bool CanAttemptActionKindThisTurn(
        GameLocationCharacter actor,
        CombatAiActionKind actionKind)
    {
        return actionKind switch
        {
            CombatAiActionKind.Melee or CombatAiActionKind.Ranged =>
                HasAvailableAttackModeForMainAction(actor, actionKind),
            CombatAiActionKind.Spell => CanUseMainSpellActionForAi(actor),
            _ => false
        };
    }

    private static void SelectActionProbeTarget(
        GameLocationCharacter actor,
        CombatAiActionKind preferred,
        CombatAiActionKind backup,
        IGameLocationBattleService battleService,
        out GameLocationCharacter target,
        out bool canPreferred,
        out bool canBackup)
    {
        target = null;
        canPreferred = false;
        canBackup = false;

        if (actor?.RulesetCharacter == null || battleService == null)
        {
            return;
        }

        GameLocationCharacter firstTarget = null;
        var firstCanPreferred = false;
        var firstCanBackup = false;
        GameLocationCharacter preferredTarget = null;
        var preferredTargetCanBackup = false;
        GameLocationCharacter backupTarget = null;
        var backupTargetCanPreferred = false;
        GameLocationCharacter plannedTarget = null;
        var plannedTargetCanPreferred = false;
        var plannedTargetCanBackup = false;
        var plannedTargetGuid = TurnMovementProgressCache.TryGetValue(actor.Guid, out var movementProgress)
            ? movementProgress.PlannedTargetGuid
            : 0;

        foreach (var enemy in GetKnownEnemyTargets(actor))
        {
            if (enemy?.RulesetCharacter == null || enemy.Side == actor.Side)
            {
                continue;
            }

            var enemyCanPreferred = CanUseActionKindAtPosition(
                actor,
                actor.LocationPosition,
                enemy,
                preferred,
                battleService);
            var enemyCanBackup = CanUseActionKindAtPosition(
                actor,
                actor.LocationPosition,
                enemy,
                backup,
                battleService);

            firstTarget ??= enemy;

            if (firstTarget == enemy)
            {
                firstCanPreferred = enemyCanPreferred;
                firstCanBackup = enemyCanBackup;
            }

            if (plannedTargetGuid != 0 && enemy.Guid == plannedTargetGuid)
            {
                plannedTarget = enemy;
                plannedTargetCanPreferred = enemyCanPreferred;
                plannedTargetCanBackup = enemyCanBackup;
            }

            if (enemyCanPreferred)
            {
                if (preferredTarget == null)
                {
                    preferredTarget = enemy;
                    preferredTargetCanBackup = enemyCanBackup;
                }
            }

            if (enemyCanBackup)
            {
                if (backupTarget == null)
                {
                    backupTarget = enemy;
                    backupTargetCanPreferred = enemyCanPreferred;
                }
            }
        }

        if (preferredTarget != null)
        {
            target = preferredTarget;
            canPreferred = true;
            canBackup = preferredTargetCanBackup;
        }
        else if (backupTarget != null)
        {
            target = backupTarget;
            canPreferred = backupTargetCanPreferred;
            canBackup = true;
        }
        else if (plannedTarget != null)
        {
            target = plannedTarget;
            canPreferred = plannedTargetCanPreferred;
            canBackup = plannedTargetCanBackup;
        }
        else
        {
            target = firstTarget;
            canPreferred = firstCanPreferred;
            canBackup = firstCanBackup;
        }
    }

    private static CombatAiMovementPlan BuildCombatAiMovementPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiActionProbe actionProbe)
    {
        if (actor?.RulesetCharacter == null)
        {
            return default;
        }

        if (ShouldBuildBreakThreatMovementPlan(actor, profile, actionProbe, out var threat))
        {

            return new CombatAiMovementPlan(
                CombatAiMovementGoalKind.BreakThreat,
                CombatAiMovementPolicyKind.DefensivePolicy,
                threat,
                threat.LocationPosition,
                CombatAiMovementPlanReasonKind.BreakThreat);
        }

        if (actionProbe.CanUsePreferredAction)
        {
            return default;
        }

        if (ShouldBuildImproveFiringPositionMovementPlan(
                actor,
                profile,
                actionProbe,
                out var improveReasonKind))
        {
            return new CombatAiMovementPlan(
                CombatAiMovementGoalKind.MoveToPreferredRange,
                actionProbe.PreferredAction == CombatAiActionKind.Spell
                    ? CombatAiMovementPolicyKind.SpellLinePolicy
                    : CombatAiMovementPolicyKind.RangedLinePolicy,
                actionProbe.Target,
                actionProbe.Target.LocationPosition,
                improveReasonKind);
        }

        if (ShouldBuildMeleeSpacingMovementPlan(
                actor,
                profile,
                actionProbe,
                out var meleeSpacingReasonKind,
                out var meleeSpacingAllyGuid))
        {
            return new CombatAiMovementPlan(
                CombatAiMovementGoalKind.MeleeSpacing,
                IsFlyingMeleeSpacingActor(actor, actionProbe)
                    ? CombatAiMovementPolicyKind.FlyingMeleeSpacingPolicy
                    : CombatAiMovementPolicyKind.MeleeSpacingPolicy,
                actionProbe.Target,
                actionProbe.Target.LocationPosition,
                meleeSpacingReasonKind,
                meleeSpacingAllyGuid);
        }

        if (ShouldBuildMaintainThreatAvoidanceMovementPlan(actor, profile, actionProbe))
        {
            return new CombatAiMovementPlan(
                CombatAiMovementGoalKind.MaintainThreatAvoidance,
                actionProbe.PreferredAction == CombatAiActionKind.Spell
                    ? CombatAiMovementPolicyKind.SpellLinePolicy
                    : CombatAiMovementPolicyKind.RangedLinePolicy,
                actionProbe.Target,
                actionProbe.Target.LocationPosition,
                CombatAiMovementPlanReasonKind.MaintainThreatAvoidance);
        }

        if (actionProbe.Target != null)
        {
            if (actionProbe.PreferredAction is CombatAiActionKind.Ranged or CombatAiActionKind.Spell)
            {
                if (TryGetLostTargetSearchAnchor(
                    actor,
                    profile,
                    actionProbe,
                    out var rangedSearchAnchor,
                    out var rangedSearchTarget))
                {

                    return BuildSearchKnownTargetMovementPlan(
                        rangedSearchTarget,
                        rangedSearchAnchor);
                }

                RecordRejectedRangedCasterSeekSeal(actor, actionProbe);

                return default;
            }

            return actionProbe.PreferredAction switch
            {
                CombatAiActionKind.Melee => new CombatAiMovementPlan(
                    CombatAiMovementGoalKind.AdvanceToMelee,
                    actionProbe.CapabilityCatalog.HasFlight
                        ? CombatAiMovementPolicyKind.FlyingPursuitPolicy
                        : CombatAiMovementPolicyKind.MeleePursuitPolicy,
                    actionProbe.Target,
                    actionProbe.Target.LocationPosition,
                    CombatAiMovementPlanReasonKind.PreferredMelee),
                _ when profile.PrefersAggressivePursuit => new CombatAiMovementPlan(
                    CombatAiMovementGoalKind.SearchKnownTarget,
                    CombatAiMovementPolicyKind.SearchKnownTargetPolicy,
                    actionProbe.Target,
                    actionProbe.Target.LocationPosition,
                    CombatAiMovementPlanReasonKind.KnownTarget),
                _ => default
            };
        }

        if (TryGetLostTargetSearchAnchor(
                actor,
                profile,
                actionProbe,
                out var searchAnchor,
                out var searchTarget))
        {
            return BuildSearchKnownTargetMovementPlan(searchTarget, searchAnchor);
        }

        return default;
    }

    private static bool ShouldUseLostTargetSearch(
        CombatAiProfile profile,
        CombatAiActionProbe actionProbe)
    {
        return profile.PrefersDistance &&
               profile.Role is not (CombatAiRole.Melee or CombatAiRole.Hybrid) &&
               (actionProbe.PreferredAction is CombatAiActionKind.Ranged or CombatAiActionKind.Spell ||
                actionProbe.CapabilityCatalog.HasAnyRanged ||
                actionProbe.CapabilityCatalog.HasAtWillHostileSpell ||
                profile.HasSpellcasting);
    }

    private static bool TryGetLostTargetSearchAnchor(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiActionProbe actionProbe,
        out int3 anchor,
        out GameLocationCharacter target)
    {
        anchor = default;
        target = null;

        if (actor?.RulesetCharacter == null || !ShouldUseLostTargetSearch(profile, actionProbe))
        {
            return false;
        }

        if (actionProbe.Target?.RulesetCharacter != null)
        {
            anchor = actionProbe.Target.LocationPosition;
            target = actionProbe.Target;
            return true;
        }

        if (ObservedCombatMemoryCache.TryGetValue(actor.Guid, out var ownMemory) &&
            IsObservedCombatMemoryFresh(ownMemory))
        {
            anchor = ownMemory.LastKnownEnemyPosition;
            return true;
        }

        if (Gui.Battle != null)
        {
            var contenders = Gui.Battle.AllContenders.ToArray();
            var bestDistance = float.MaxValue;
            var hasAllyMemory = false;

            foreach (var ally in OrderCharactersForCombatAi(contenders, actor.LocationPosition))
            {
                if (ally == actor ||
                    ally?.RulesetCharacter == null ||
                    ally.Side != actor.Side ||
                    !ObservedCombatMemoryCache.TryGetValue(ally.Guid, out var allyMemory) ||
                    !IsObservedCombatMemoryFresh(allyMemory))
                {
                    continue;
                }

                var distance = ComputeGridDistance(actor.LocationPosition, allyMemory.LastKnownEnemyPosition);

                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                anchor = allyMemory.LastKnownEnemyPosition;
                hasAllyMemory = true;
            }

            if (hasAllyMemory)
            {
                return true;
            }
        }

        if (TryGetLastKnownEnemyPosition(actor, out var lastKnownEnemyPosition))
        {
            anchor = lastKnownEnemyPosition;
            return true;
        }

        return false;
    }

    private static CombatAiMovementPlan BuildSearchKnownTargetMovementPlan(
        GameLocationCharacter target,
        int3 anchor)
    {
        return new CombatAiMovementPlan(
            CombatAiMovementGoalKind.SearchKnownTarget,
            CombatAiMovementPolicyKind.SearchKnownTargetPolicy,
            target,
            anchor,
            CombatAiMovementPlanReasonKind.SearchKnownTarget);
    }

    private static bool ShouldBuildImproveFiringPositionMovementPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiActionProbe actionProbe,
        out CombatAiMovementPlanReasonKind reasonKind)
    {
        reasonKind = CombatAiMovementPlanReasonKind.None;

        if (actor?.RulesetCharacter == null ||
            actionProbe.Target?.RulesetCharacter == null ||
            !profile.PrefersDistance ||
            profile.Role is CombatAiRole.Melee or CombatAiRole.Hybrid ||
            actionProbe.PreferredAction is not (CombatAiActionKind.Ranged or CombatAiActionKind.Spell))
        {
            return false;
        }

        if (!actionProbe.CanUsePreferredAction)
        {
            if (TryGetCurrentOrRecentMeleeThreat(actor, actor.LocationPosition, out _, out _, out _))
            {
                return false;
            }

            reasonKind = CombatAiMovementPlanReasonKind.ConnectFiringLine;
            return true;
        }

        if (!TryGetRepeatedRangedAttackMemory(actor, actionProbe.Target, out var memory) ||
            memory.RepeatCount < RepeatedRangedAttackThreshold)
        {
            return false;
        }

        reasonKind = CombatAiMovementPlanReasonKind.ImproveFiringPosition;

        return true;
    }

    private static bool IsConnectedFiringLinePlan(CombatAiTurnPlan turnPlan)
    {
        return turnPlan.MovementPlan.ReasonKind == CombatAiMovementPlanReasonKind.ConnectFiringLine;
    }

    private static bool IsConnectedFiringLineRoute(ActionLinkedMoveMemory pendingAction)
    {
        return pendingAction.RouteMoveSource == CombatAiRouteMoveSourceKind.ConnectedFiringLine;
    }

    private static bool IsSearchKnownTargetRoute(ActionLinkedMoveMemory pendingAction)
    {
        return pendingAction.MovementGoal == CombatAiMovementGoalKind.SearchKnownTarget &&
               pendingAction.RouteMoveSource == CombatAiRouteMoveSourceKind.SearchLostTarget;
    }

    private static void CloseFailedConnectedFiringLineRoute(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null ||
            !IsConnectedFiringLinePlan(turnPlan))
        {
            return;
        }

        RecordDisconnectedPositioningSeal(
            character,
            turnPlan);
    }

    private static bool ShouldBuildMaintainThreatAvoidanceMovementPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiActionProbe actionProbe)
    {
        if (actor?.RulesetCharacter == null ||
            actionProbe.Target?.RulesetCharacter == null ||
            !profile.PrefersDistance ||
            profile.Role is not (CombatAiRole.Ranged or CombatAiRole.SupportCaster or CombatAiRole.OffensiveCaster) ||
            actionProbe.PreferredAction is not (CombatAiActionKind.Ranged or CombatAiActionKind.Spell) ||
            !TryGetThreatAvoidance(actor, out var memory))
        {
            return false;
        }

        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;

        if (memory.IsEffectProxy)
        {
            if (TryGetSameTurnNoMoveProxyThreatAttempt(actor, out _))
            {
                return false;
            }

            if (EvaluateProxyMeleeThreatState(actor, new RecentMeleeThreatMemory(
                    memory.Source,
                    sourcePosition,
                    true,
                    memory.Round,
                    memory.TurnStamp)) != ProxyThreatActivityState.Active)
            {
                return false;
            }
        }

        if (ComputeGridDistance(actor.LocationPosition, sourcePosition) > ThreatAvoidanceClearDistance &&
            (!memory.HasSafePosition ||
             ComputeGridDistance(memory.SafePosition, sourcePosition) > ThreatAvoidanceClearDistance))
        {
            return false;
        }

        return true;
    }

    private static bool ShouldBuildMeleeSpacingMovementPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiActionProbe actionProbe,
        out CombatAiMovementPlanReasonKind reasonKind,
        out ulong allyGuid)
    {
        reasonKind = CombatAiMovementPlanReasonKind.None;
        allyGuid = 0;

        if (actor?.RulesetCharacter == null ||
            actionProbe.Target?.RulesetCharacter == null ||
            actionProbe.PreferredAction != CombatAiActionKind.Melee)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var hasCrowding = HasMeleeAllyCrowding(actor, actionProbe.Target);
        var blocksLargeAllyCorridor = IsBlockingLargeMeleeAllyCorridor(
            actor,
            actionProbe.Target,
            battleService,
            out allyGuid);

        if ((!actionProbe.CanUsePreferredAction && !blocksLargeAllyCorridor) ||
            !hasCrowding && !blocksLargeAllyCorridor)
        {
            return false;
        }

        reasonKind = blocksLargeAllyCorridor
            ? CombatAiMovementPlanReasonKind.ClearAllyCorridor
            : CombatAiMovementPlanReasonKind.MeleeSpacing;

        return true;
    }

    private static bool IsClearAllyCorridorMovementPlan(CombatAiTurnPlan turnPlan)
    {
        return turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing &&
               turnPlan.MovementPlan.ReasonKind == CombatAiMovementPlanReasonKind.ClearAllyCorridor;
    }

    private static bool IsFlyingMeleeSpacingActor(GameLocationCharacter actor, CombatAiActionProbe actionProbe)
    {
        return actor?.RulesetCharacter != null &&
               actionProbe.PreferredAction == CombatAiActionKind.Melee &&
               (actionProbe.CapabilityCatalog.HasFlight || BuildProfile(actor).HasFlight);
    }

    private static bool TryGetRepeatedRangedAttackMemory(
        GameLocationCharacter actor,
        GameLocationCharacter target,
        out RepeatedAttackActionMemory memory)
    {
        memory = default;

        return actor?.RulesetCharacter != null &&
               target?.RulesetCharacter != null &&
               RepeatAttackActionCache.TryGetValue(actor.Guid, out memory) &&
               memory.TargetGuid == target.Guid &&
               memory.ActorPosition == actor.LocationPosition &&
               memory.TargetPosition == target.LocationPosition &&
               memory.ActionKind is CombatAiActionKind.Ranged or CombatAiActionKind.Spell;
    }

    private static bool HasMeleeAllyCrowding(GameLocationCharacter actor, GameLocationCharacter target)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            Gui.Battle == null)
        {
            return false;
        }

        foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), target.LocationPosition))
        {
            if (ally == actor ||
                ally?.RulesetCharacter == null ||
                ally.Side != actor.Side ||
                ComputeHorizontalGridStepDistance(ally.LocationPosition, actor.LocationPosition) >
                MeleeSpacingAllyAdjacentGridSteps ||
                ComputeHorizontalGridStepDistance(ally.LocationPosition, target.LocationPosition) >
                MeleeSpacingTargetPressureDistance)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool ShouldBuildBreakThreatMovementPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiActionProbe actionProbe,
        out GameLocationCharacter threat)
    {
        threat = null;

        if (actor?.RulesetCharacter == null ||
            !profile.PrefersDistance ||
            profile.Role is not (CombatAiRole.Ranged or CombatAiRole.SupportCaster or CombatAiRole.OffensiveCaster) ||
            actionProbe.PreferredAction is not (CombatAiActionKind.Ranged or CombatAiActionKind.Spell) ||
            !TryGetCurrentOrRecentMeleeThreat(
                actor,
                actor.LocationPosition,
                out threat,
                out var threatPosition,
                out var threatSource))
        {
            return false;
        }

        var isRecentThreat = threatSource == MeleeThreatSourceKind.Recent;
        var proxyThreatMemory = default(RecentMeleeThreatMemory);
        var isProxyMeleeThreat = isRecentThreat && TryGetProxyMeleeThreat(actor, out proxyThreatMemory);

        if (isProxyMeleeThreat)
        {
            if (TryGetSameTurnNoMoveProxyThreatAttempt(actor, out var previousProxyAttempt))
            {
                return false;
            }

            if (!ShouldEvaluateProxyMeleeThreatPreMainBreakThreat(
                    actor,
                    proxyThreatMemory))
            {
                return false;
            }

        }
        else if (isRecentThreat && actionProbe.CanUsePreferredAction)
        {
            if (!ShouldEvaluateRecentThreatPreMainBreakThreat(
                    actor,
                    threatPosition,
                    ServiceRepository.GetService<IGameLocationBattleService>()))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryGetProxyMeleeThreat(GameLocationCharacter actor, out RecentMeleeThreatMemory memory)
    {
        if (!TryGetRecentMeleeThreat(actor, out memory))
        {
            return false;
        }

        if (memory.IsEffectProxy || IsNonOccupyingCombatProxyTarget(memory.Source))
        {
            return true;
        }

        var source = memory.Source;

        if (source?.RulesetCharacter == null)
        {
            return false;
        }

        foreach (var target in GetKnownEnemyTargets(actor))
        {
            if (target == source)
            {
                return false;
            }
        }

        return true;
    }

    private static void ClearInactiveProxyThreatState(GameLocationCharacter actor, RecentMeleeThreatMemory memory)
    {
        if (actor?.RulesetCharacter == null)
        {
            return;
        }

        if (RecentMeleeThreatMemoryCache.TryGetValue(actor.Guid, out var recentMemory) &&
            (recentMemory.Source != null && memory.Source != null
                ? recentMemory.Source.Guid == memory.Source.Guid
                : recentMemory.SourcePosition == memory.SourcePosition))
        {
            RecentMeleeThreatMemoryCache.Remove(actor.Guid);
        }

        if (ThreatAvoidanceMemoryCache.TryGetValue(actor.Guid, out var avoidance) &&
            avoidance.IsEffectProxy &&
            (avoidance.Source != null && memory.Source != null
                ? avoidance.Source.Guid == memory.Source.Guid
                : avoidance.SourcePosition == memory.SourcePosition))
        {
            ThreatAvoidanceMemoryCache.Remove(actor.Guid);
        }

        if (ProxyThreatRouteAttemptCache.TryGetValue(actor.Guid, out var attempt) &&
            attempt.MatchesSource(memory))
        {
            ProxyThreatRouteAttemptCache.Remove(actor.Guid);
        }
    }

    private static ProxyThreatActivityState EvaluateProxyMeleeThreatState(
        GameLocationCharacter actor,
        RecentMeleeThreatMemory memory)
    {
        if (actor?.RulesetCharacter == null)
        {
            return ProxyThreatActivityState.Clear;
        }

        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
        var threatDistance = ComputeGridDistance(actor.LocationPosition, sourcePosition);

        if (threatDistance > ThreatAvoidanceClearDistance)
        {
            ClearInactiveProxyThreatState(actor, memory);
            return ProxyThreatActivityState.Clear;
        }

        if (threatDistance > ThreatAvoidanceThreatZoneDistance + 0.5f)
        {
            return ProxyThreatActivityState.Inactive;
        }

        return ProxyThreatActivityState.Active;
    }

    private static bool ShouldEvaluateProxyMeleeThreatPreMainBreakThreat(
        GameLocationCharacter actor,
        RecentMeleeThreatMemory memory)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        if (EvaluateProxyMeleeThreatState(actor, memory) != ProxyThreatActivityState.Active)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldEvaluateRecentThreatPreMainBreakThreat(
        GameLocationCharacter actor,
        int3 threatPosition,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        var threatDistance = ComputeGridDistance(actor.LocationPosition, threatPosition);
        var inThreatReach = battleService != null &&
                            WouldBeInCurrentOrRecentMeleeThreat(actor, actor.LocationPosition, battleService);

        if (!inThreatReach &&
            threatDistance > ThreatAvoidanceThreatZoneDistance + 0.5f)
        {
            return false;
        }

        return true;
    }

    private static GameLocationCharacter SelectPrimaryTarget(GameLocationCharacter actor)
    {
        return GetKnownEnemyTargets(actor).FirstOrDefault();
    }

    private static CombatAiActionKind GetPreferredActionKind(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiCapabilityCatalog capabilityCatalog)
    {
        if (capabilityCatalog.HasAtWillHostileSpell &&
            profile.Role is CombatAiRole.OffensiveCaster or CombatAiRole.SupportCaster)
        {
            return CombatAiActionKind.Spell;
        }

        if (ShouldPreferMeleeAction(profile, capabilityCatalog))
        {
            return CombatAiActionKind.Melee;
        }

        if (profile.Role == CombatAiRole.Ranged && capabilityCatalog.HasTrueRanged)
        {
            return CombatAiActionKind.Ranged;
        }

        if (capabilityCatalog.HasAtWillHostileSpell)
        {
            return CombatAiActionKind.Spell;
        }

        if (capabilityCatalog.HasMelee)
        {
            return CombatAiActionKind.Melee;
        }

        if (capabilityCatalog.HasTrueRanged || capabilityCatalog.HasThrownRanged)
        {
            return CombatAiActionKind.Ranged;
        }

        return CombatAiActionKind.None;
    }

    private static bool ShouldPreferMeleeAction(CombatAiProfile profile, CombatAiCapabilityCatalog capabilityCatalog)
    {
        if (!capabilityCatalog.HasMelee)
        {
            return false;
        }

        if (profile.Role is CombatAiRole.Melee or CombatAiRole.Hybrid)
        {
            return true;
        }

        if (!capabilityCatalog.HasTrueRanged)
        {
            return capabilityCatalog.HasThrownRanged;
        }

        return capabilityCatalog.HasThrownRanged &&
               capabilityCatalog.TrueRangedMaximumRange <= ThrownLikeRangedMaximumRange;
    }

    private static CombatAiActionKind GetBackupActionKind(
        GameLocationCharacter actor,
        CombatAiActionKind preferred,
        CombatAiCapabilityCatalog capabilityCatalog)
    {
        if (preferred != CombatAiActionKind.Ranged && capabilityCatalog.HasAnyRanged)
        {
            return CombatAiActionKind.Ranged;
        }

        if (preferred != CombatAiActionKind.Melee && capabilityCatalog.HasMelee)
        {
            return CombatAiActionKind.Melee;
        }

        return CombatAiActionKind.None;
    }

    private static bool CanUseActionKindAtPosition(
        GameLocationCharacter actor,
        int3 actorPosition,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService)
    {
        if (actionKind == CombatAiActionKind.None)
        {
            return false;
        }

        var cacheKey = default(RouteCandidateCacheKey);
        var canUseCache = IsAdvancedCombatAiEnabled &&
                          TryGetRouteCandidateCacheKey(actor, actorPosition, target, actionKind, out cacheKey);

        if (canUseCache && ActionKindPositionCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var canUseAction = actionKind switch
        {
            CombatAiActionKind.Melee => CanAttackInMeleeFromPosition(
                actor,
                actorPosition,
                target,
                target.LocationPosition,
                battleService),
            CombatAiActionKind.Ranged => TryGetRangedAttackModifierFromPosition(
                actor,
                actorPosition,
                target,
                target.LocationPosition,
                battleService,
                out _),
            CombatAiActionKind.Spell => TryGetAtWillSpellAttackModifierFromPosition(
                actor,
                actorPosition,
                target,
                target.LocationPosition,
                battleService,
                out _),
            _ => false
        };

        if (canUseCache)
        {
            ActionKindPositionCache[cacheKey] = canUseAction;
        }

        return canUseAction;
    }

    private static bool TryComputeTurnPlanMovementScore(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        int3 candidatePosition,
        DecisionParameters parameters,
        CombatAiTurnPlan turnPlan,
        out float score)
    {
        score = 0f;

        return parameters?.situationalInformation?.BattleService != null &&
               TryComputeTurnPlanMovementScore(
                   actor,
                   profile,
                   candidatePosition,
                   parameters.situationalInformation.BattleService,
                   turnPlan,
                   out score);
    }

    private static bool IsBreakThreatDestinationAttackConnected(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 start,
        int3 candidatePosition)
    {
        if (turnPlan.MovementPlan.Goal != CombatAiMovementGoalKind.BreakThreat)
        {
            return true;
        }

        if (actor?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        if (ComputeForcedMoveCost(start, candidatePosition) <= 1)
        {
            return false;
        }

        if (WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService))
        {
            return false;
        }

        var target = turnPlan.ActionProbe.Target;

        if (target?.RulesetCharacter == null)
        {
            return false;
        }

        var canUsePreferredAction = turnPlan.ActionProbe.PreferredAction != CombatAiActionKind.None &&
                                    CanUseActionKindAtPosition(
                                        actor,
                                        candidatePosition,
                                        target,
                                        turnPlan.ActionProbe.PreferredAction,
                                        battleService);
        var canUseBackupAction = turnPlan.ActionProbe.BackupAction != CombatAiActionKind.None &&
                                 CanUseActionKindAtPosition(
                                     actor,
                                     candidatePosition,
                                     target,
                                     turnPlan.ActionProbe.BackupAction,
                                     battleService);

        if (canUsePreferredAction || canUseBackupAction)
        {
            return true;
        }

        return false;
    }

    private static bool IsBreakThreatDefensiveFallbackDestination(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 start,
        int3 candidatePosition)
    {
        if (turnPlan.MovementPlan.Goal != CombatAiMovementGoalKind.BreakThreat)
        {
            return false;
        }

        var profile = BuildProfile(actor);

        if (!profile.PrefersDistance ||
            profile.Role is CombatAiRole.Melee or CombatAiRole.Hybrid ||
            actor?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        if (ComputeForcedMoveCost(start, candidatePosition) <= 1)
        {
            return false;
        }

        if (!TryGetCurrentOrRecentMeleeThreat(
                actor,
                start,
                out _,
                out var threatPosition,
                out _))
        {
            return false;
        }

        if (WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService))
        {
            return false;
        }

        var threatProgress = ComputeGridDistance(candidatePosition, threatPosition) -
                             ComputeGridDistance(start, threatPosition);

        if (threatProgress < ThreatAvoidanceMinimumDistanceGain)
        {
            return false;
        }

        return true;
    }

    private static bool TryComputeTurnPlanMovementScore(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        int3 candidatePosition,
        IGameLocationBattleService battleService,
        CombatAiTurnPlan turnPlan,
        out float score)
    {
        score = 0f;

        if (!turnPlan.MovementPlan.HasGoal ||
            candidatePosition == actor.LocationPosition ||
            battleService == null)
        {
            return false;
        }

        var movementProgress = GetOrCreateTurnMovementProgress(actor, turnPlan);

        if (IsFailedAiMoveTarget(actor, actor.LocationPosition, candidatePosition))
        {
            return true;
        }

        if (!IsLegalAiRouteDestination(actor, candidatePosition))
        {
            return true;
        }

        if (HasOpportunityAttackRisk(actor, actor.LocationPosition, candidatePosition, battleService))
        {
            return true;
        }

        if (ShouldRejectTrafficBlockingMove(
                actor,
                candidatePosition,
                battleService,
                profile,
                turnPlan))
        {
            return true;
        }

        if (ShouldRejectThreatAvoidanceReturnMove(actor, profile, turnPlan, candidatePosition))
        {
            return true;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat)
        {
            if (!TryGetCurrentOrRecentMeleeThreat(
                    actor,
                    actor.LocationPosition,
                    out _,
                    out var currentThreatPosition,
                    out _))
            {
                return true;
            }

            if (ShouldRequireReachableProxyThreatDestination(actor, turnPlan) &&
                !TryValidateProxyThreatDistanceProgress(
                    actor,
                    actor.LocationPosition,
                    candidatePosition))
            {
                return true;
            }

            if (WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService))
            {
                return true;
            }

            if (movementProgress != null && movementProgress.HasVisited(candidatePosition))
            {
                return true;
            }

            var breakThreatAttackConnected = IsBreakThreatDestinationAttackConnected(
                actor,
                turnPlan,
                battleService,
                actor.LocationPosition,
                candidatePosition);
            var breakThreatDefensiveFallback =
                !breakThreatAttackConnected &&
                IsBreakThreatDefensiveFallbackDestination(
                    actor,
                    turnPlan,
                    battleService,
                    actor.LocationPosition,
                    candidatePosition);

            if (!breakThreatAttackConnected && !breakThreatDefensiveFallback)
            {
                return true;
            }

            var threatProgress = ComputeGridDistance(candidatePosition, currentThreatPosition) -
                                 ComputeGridDistance(actor.LocationPosition, currentThreatPosition);

            var minimumThreatProgress = ThreatAvoidanceMinimumDistanceGain;

            if (threatProgress < minimumThreatProgress)
            {
                return true;
            }

            score = MovementGoalProgressScore +
                    Math.Min(0.35f, Math.Max(0.10f, threatProgress * 0.08f)) +
                    (breakThreatAttackConnected ? 0.12f : -0.18f) +
                    ComputeStableTieBreakScore(
                        actor,
                        turnPlan,
                        candidatePosition,
                        CombatAiActionKind.None);

            movementProgress?.MarkPreferredActionMovementCandidate();
            movementProgress?.RecordAccepted(candidatePosition, score, Math.Max(0f, threatProgress));

            return true;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MaintainThreatAvoidance)
        {
            if (ShouldRequireReachableProxyThreatDestination(actor, turnPlan) &&
                !TryValidateProxyThreatDistanceProgress(
                    actor,
                    actor.LocationPosition,
                    candidatePosition))
            {
                return true;
            }

            if (!TryComputeMaintainThreatAvoidanceScore(
                    actor,
                    candidatePosition,
                    battleService,
                    turnPlan,
                    out score))
            {
                return true;
            }

            score += ComputeStableTieBreakScore(
                actor,
                turnPlan,
                candidatePosition,
                turnPlan.ActionProbe.PreferredAction);
            movementProgress?.MarkMeaningfulMovementCandidate();
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                ComputeGridDistance(actor.LocationPosition, turnPlan.MovementPlan.TargetPosition) -
                ComputeGridDistance(candidatePosition, turnPlan.MovementPlan.TargetPosition),
                preferredActionCandidate: true,
                turnsToAction: 0);

            return true;
        }

        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var currentDistance = ComputeGridDistance(actor.LocationPosition, targetPosition);
        var candidateDistance = ComputeGridDistance(candidatePosition, targetPosition);
        var progress = currentDistance - candidateDistance;
        var currentTurnsToAction = EstimateTurnsToPreferredAction(actor, turnPlan, actor.LocationPosition);
        var candidateTurnsToAction = EstimateTurnsToPreferredAction(actor, turnPlan, candidatePosition);
        var shortensPreferredRoute =
            currentTurnsToAction > 0 &&
            candidateTurnsToAction >= 0 &&
            candidateTurnsToAction < currentTurnsToAction;

        var canPreferredFromCandidate =
            turnPlan.ActionProbe.Target != null &&
            CanUseActionKindAtPosition(
                actor,
                candidatePosition,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.PreferredAction,
                battleService);
        var canBackupFromCandidate =
            !canPreferredFromCandidate &&
            turnPlan.ActionProbe.Target != null &&
            turnPlan.ActionProbe.BackupAction != CombatAiActionKind.None &&
            CanUseActionKindAtPosition(
                actor,
                candidatePosition,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.BackupAction,
                battleService);

        if (IsSearchKnownTargetPlan(turnPlan) &&
            !canPreferredFromCandidate &&
            !canBackupFromCandidate)
        {
            var minimumSearchProgress = ComputeMinimumMovementGoalProgress(
                actor,
                turnPlan.MovementPlan.Goal,
                turnPlan.MovementPlan.Policy,
                currentDistance);

            if (progress + 0.01f < minimumSearchProgress)
            {
                return true;
            }
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            if (!TryComputeMeleeSpacingScore(
                    actor,
                    candidatePosition,
                    battleService,
                    turnPlan,
                    canPreferredFromCandidate,
                    out score))
            {
                return true;
            }

            score += ComputeStableTieBreakScore(
                actor,
                turnPlan,
                candidatePosition,
                CombatAiActionKind.Melee);
            movementProgress?.MarkMeaningfulMovementCandidate();
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                preferredActionCandidate: true,
                turnsToAction: 0);

            return true;
        }

        if (IsImproveFiringPositionPlan(turnPlan))
        {
            if (!TryComputeImproveFiringPositionScore(
                    actor,
                    candidatePosition,
                    battleService,
                    turnPlan,
                    canPreferredFromCandidate,
                    out score,
                    out var actionKind))
            {
                return true;
            }

            score += ComputeStableTieBreakScore(actor, turnPlan, candidatePosition, actionKind);
            movementProgress?.MarkMeaningfulMovementCandidate();
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                preferredActionCandidate: canPreferredFromCandidate,
                turnsToAction: candidateTurnsToAction);

            return true;
        }

        if (canPreferredFromCandidate)
        {
            movementProgress?.MarkPreferredActionMovementCandidate();
            score = MovementGoalPreferredRangeScore +
                    ComputePreferredActionPolicyBonus(turnPlan.MovementPlan.Policy) +
                    ComputeFlyingMeleePolicyHeightScore(actor, candidatePosition, turnPlan) +
                    ComputeStableTieBreakScore(
                actor,
                turnPlan,
                candidatePosition,
                turnPlan.ActionProbe.PreferredAction);
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                preferredActionCandidate: true,
                turnsToAction: candidateTurnsToAction);
            return true;
        }

        if (IsGroundMeleePursuitPlan(turnPlan))
        {
            if (TryComputeGroundMeleeContactRouteMovementScore(
                    actor,
                    turnPlan,
                    movementProgress,
                    candidatePosition,
                    out score))
            {
                return true;
            }

            if (TryGetCachedGroundMeleeTargetContactRouteQuery(actor, turnPlan, out var contactQuery) &&
                contactQuery.Complete)
            {
                return true;
            }

            if (canBackupFromCandidate)
            {
                var nonRegressingBackup =
                    shortensPreferredRoute ||
                    candidateDistance <= currentDistance + MovementGoalRegressionTolerance;

                if (!nonRegressingBackup)
                {
                    return true;
                }

                score = MovementGoalMeleeProgressScore +
                        0.12f +
                        Math.Min(0.20f, Math.Max(0f, progress) * 0.04f) +
                        ComputeStableTieBreakScore(
                            actor,
                            turnPlan,
                            candidatePosition,
                            turnPlan.ActionProbe.BackupAction);
                movementProgress?.MarkMeaningfulMovementCandidate();
                movementProgress?.RecordAccepted(
                    candidatePosition,
                    score,
                    progress,
                    turnsToAction: candidateTurnsToAction);
                return true;
            }
        }

        if (canBackupFromCandidate)
        {
            movementProgress?.MarkMeaningfulMovementCandidate();
            score = MovementGoalPreferredRangeScore +
                    ComputePreferredActionPolicyBonus(turnPlan.MovementPlan.Policy) +
                    ComputeFlyingMeleePolicyHeightScore(actor, candidatePosition, turnPlan) +
                    ComputeStableTieBreakScore(
                        actor,
                        turnPlan,
                        candidatePosition,
                        turnPlan.ActionProbe.BackupAction);
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                turnsToAction: candidateTurnsToAction);
            return true;
        }

        if (RequiresPostMoveActionConnectedPositioning(turnPlan) &&
            movementProgress?.HadPreferredActionMovementCandidate == true)
        {
            CurrentStateRouteBlockCache[actor.Guid] = CurrentStateRouteBlockKind.NoPostMoveAction;
            return true;
        }

        if (RequiresPostMoveActionConnectedPositioning(turnPlan) &&
            movementProgress?.HadPreferredActionMovementCandidate != true &&
            IsRangedCasterPreferredRangePlan(turnPlan))
        {
            if (!ShouldAllowDisconnectedRangedSeekImprovement(
                    actor,
                    turnPlan,
                    actor.LocationPosition,
                    candidatePosition,
                    currentTurnsToAction,
                    candidateTurnsToAction))
            {
                CurrentStateRouteBlockCache[actor.Guid] = CurrentStateRouteBlockKind.RangedSeekDisconnected;
                return true;
            }

            var seekProgress = Math.Max(0f, progress);
            var turnsImprovement =
                currentTurnsToAction > 0 &&
                candidateTurnsToAction >= 0 &&
                candidateTurnsToAction < currentTurnsToAction
                    ? currentTurnsToAction - candidateTurnsToAction
                    : 0;

            score = MovementGoalSearchScore +
                    Mathf.Clamp01(seekProgress / Math.Max(currentDistance, 1f)) * 0.20f +
                    Math.Min(0.35f, turnsImprovement * 0.15f) +
                    ComputeStableTieBreakScore(
                        actor,
                        turnPlan,
                        candidatePosition,
                        turnPlan.ActionProbe.PreferredAction);

            CurrentStateRouteBlockCache.Remove(actor.Guid);
            movementProgress?.MarkMeaningfulMovementCandidate();
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                turnsToAction: candidateTurnsToAction);
            return true;
        }

        if (RequiresPostMoveActionConnectedPositioning(turnPlan) &&
            movementProgress?.HadPreferredActionMovementCandidate != true &&
            IsSeekRouteImprovementDestination(actor, turnPlan, actor.LocationPosition, candidatePosition))
        {
            var seekProgress = Math.Max(0f, progress);
            var turnsImprovement =
                currentTurnsToAction > 0 &&
                candidateTurnsToAction >= 0 &&
                candidateTurnsToAction < currentTurnsToAction
                    ? currentTurnsToAction - candidateTurnsToAction
                    : 0;

            score = MovementGoalSearchScore +
                    Mathf.Clamp01(seekProgress / Math.Max(currentDistance, 1f)) * 0.20f +
                    Math.Min(0.35f, turnsImprovement * 0.15f) +
                    ComputeStableTieBreakScore(
                        actor,
                        turnPlan,
                        candidatePosition,
                        turnPlan.ActionProbe.PreferredAction);

            CurrentStateRouteBlockCache.Remove(actor.Guid);
            movementProgress?.MarkMeaningfulMovementCandidate();
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                turnsToAction: candidateTurnsToAction);
            return true;
        }

        if (RequiresPostMoveActionConnectedPositioning(turnPlan) &&
            movementProgress?.HadPreferredActionMovementCandidate != true)
        {
            CurrentStateRouteBlockCache[actor.Guid] = CurrentStateRouteBlockKind.SeekRegression;
            return true;
        }

        if (candidateDistance > currentDistance + MovementGoalRegressionTolerance ||
            progress <= 0f)
        {
            return true;
        }

        if (movementProgress != null &&
            (movementProgress.HasVisited(candidatePosition) ||
             candidateDistance > movementProgress.BestDistanceToGoal + MovementGoalRegressionTolerance))
        {
            return true;
        }

        var minimumProgress = ComputeMinimumMovementGoalProgress(
            actor,
            turnPlan.MovementPlan.Goal,
            turnPlan.MovementPlan.Policy,
            currentDistance);

        if (progress + 0.01f < minimumProgress &&
            !shortensPreferredRoute &&
            turnPlan.MovementPlan.Goal is not CombatAiMovementGoalKind.BreakThreat
                and not CombatAiMovementGoalKind.ClearLargeAllyPath)
        {
            return true;
        }

        if (!canPreferredFromCandidate &&
            turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MoveToPreferredRange &&
            TryGetMinimumLineRouteProgress(turnPlan.MovementPlan.Policy, out var minimumLineProgress) &&
            progress + 0.01f < minimumLineProgress)
        {
            return true;
        }

        var progressRatio = Mathf.Clamp01(progress / Math.Max(currentDistance, 1f));
        var moveUseRatio = Mathf.Clamp01(progress / Math.Max(actor.RemainingTacticalMoves, 1f));

        score = turnPlan.MovementPlan.Policy switch
        {
            CombatAiMovementPolicyKind.SpellLinePolicy =>
                MovementGoalSpellLineProgressScore + progressRatio * 0.18f + moveUseRatio * 0.28f,
            CombatAiMovementPolicyKind.RangedLinePolicy =>
                MovementGoalRangedLineProgressScore + progressRatio * 0.18f + moveUseRatio * 0.24f,
            CombatAiMovementPolicyKind.MeleePursuitPolicy or CombatAiMovementPolicyKind.FlyingPursuitPolicy =>
                MovementGoalMeleeProgressScore + progressRatio * 0.35f + moveUseRatio * 0.30f,
            CombatAiMovementPolicyKind.SearchKnownTargetPolicy =>
                MovementGoalSearchScore + progressRatio * 0.14f + moveUseRatio * 0.10f,
            _ => 0f
        };

        if (score > 0f)
        {
            if (shortensPreferredRoute)
            {
                score += Math.Min(0.30f, (currentTurnsToAction - candidateTurnsToAction) * 0.15f);
            }

            score += ComputePreferredRangeProximityBonus(turnPlan, candidateDistance);
            score += ComputeFlyingMeleePolicyHeightScore(actor, candidatePosition, turnPlan);

            if (turnPlan.MovementPlan.Policy is CombatAiMovementPolicyKind.SpellLinePolicy)
            {
                score = Math.Min(score, MovementGoalSpellLineProgressMaximumScore);
            }
            else if (turnPlan.MovementPlan.Policy is CombatAiMovementPolicyKind.RangedLinePolicy)
            {
                score = Math.Min(score, MovementGoalRangedLineProgressMaximumScore);
            }
            else if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MoveToPreferredRange)
            {
                score = Math.Min(score, MovementGoalProgressMaximumScore);
            }

            score += ComputeStableTieBreakScore(
                actor,
                turnPlan,
                candidatePosition,
                turnPlan.ActionProbe.PreferredAction);
            movementProgress?.MarkMeaningfulMovementCandidate();
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                turnsToAction: candidateTurnsToAction);
        }

        return score > 0f;
    }

    private static bool TryComputeGroundMeleeContactRouteMovementScore(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        AiTurnMovementProgress movementProgress,
        int3 candidatePosition,
        out float score)
    {
        score = 0f;

        if (!TryGetCachedGroundMeleeTargetContactRouteQuery(actor, turnPlan, out var query) ||
            !query.Complete)
        {
            return false;
        }

        var start = actor.LocationPosition;
        var startContactMoveCost = query.TryGetContactCost(start, out var startCost, out _)
            ? startCost
            : query.BestGoalMoveCost;

        if (movementProgress?.HasVisited(candidatePosition) == true)
        {
            return false;
        }

        if (!query.TryGetContactCost(candidatePosition, out var candidateContactMoveCost, out _))
        {
            return false;
        }

        var contactProgress = startContactMoveCost - candidateContactMoveCost;

        if (contactProgress <= 0)
        {
            return false;
        }

        var firstStepMoveCost = query.Map.Contains(candidatePosition)
            ? query.GetMoveCost(candidatePosition)
            : ComputeForcedMoveCost(start, candidatePosition);
        var totalPathCost = Math.Max(0, firstStepMoveCost) + candidateContactMoveCost;
        var routeTurns = EstimateGroundMeleeContactRouteTurns(actor, firstStepMoveCost + candidateContactMoveCost);

        score =
            MovementGoalMeleeProgressScore +
            Math.Min(0.70f, contactProgress * 0.08f) -
            Math.Min(0.40f, candidateContactMoveCost * 0.015f) -
            Math.Min(0.30f, totalPathCost * 0.01f) +
            Math.Min(0.10f, firstStepMoveCost * 0.005f) +
            ComputeStableTieBreakScore(actor, turnPlan, candidatePosition, CombatAiActionKind.Melee);

        movementProgress?.MarkMeaningfulMovementCandidate();
        movementProgress?.RecordAccepted(
            candidatePosition,
            score,
            contactProgress,
            turnsToAction: routeTurns);
        return true;
    }

    private static bool TryComputeMaintainThreatAvoidanceScore(
        GameLocationCharacter actor,
        int3 candidatePosition,
        IGameLocationBattleService battleService,
        CombatAiTurnPlan turnPlan,
        out float score)
    {
        score = 0f;

        if (actor?.RulesetCharacter == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            battleService == null ||
            !TryGetThreatAvoidance(actor, out var memory))
        {
            return false;
        }

        var canPreferredFromCandidate =
            turnPlan.ActionProbe.PreferredAction != CombatAiActionKind.None &&
            CanUseActionKindAtPosition(
                actor,
                candidatePosition,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.PreferredAction,
                battleService);
        var canBackupFromCandidate =
            turnPlan.ActionProbe.BackupAction != CombatAiActionKind.None &&
            CanUseActionKindAtPosition(
                actor,
                candidatePosition,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.BackupAction,
                battleService);

        if (!canPreferredFromCandidate && !canBackupFromCandidate)
        {
            return false;
        }

        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
        var currentThreatDistance = ComputeGridDistance(actor.LocationPosition, sourcePosition);
        var candidateThreatDistance = ComputeGridDistance(candidatePosition, sourcePosition);

        if (candidateThreatDistance <= ThreatAvoidanceThreatZoneDistance ||
            candidateThreatDistance + 0.5f < currentThreatDistance)
        {
            return false;
        }

        var currentTargetDistance = ComputeGridDistance(
            actor.LocationPosition,
            turnPlan.ActionProbe.Target.LocationPosition);
        var candidateTargetDistance = ComputeGridDistance(
            candidatePosition,
            turnPlan.ActionProbe.Target.LocationPosition);
        var keepsTargetPressure = candidateTargetDistance <= currentTargetDistance + 1.5f;
        var improvesThreatDistance = candidateThreatDistance > currentThreatDistance + 0.5f;
        var holdsSafePosition =
            memory.HasSafePosition &&
            ComputeGridDistance(candidatePosition, memory.SafePosition) <= ThreatAvoidanceReturnTolerance + 1.0f;

        if (!keepsTargetPressure || (!improvesThreatDistance && !holdsSafePosition))
        {
            return false;
        }

        score = MovementGoalProgressScore - 0.04f;

        if (improvesThreatDistance)
        {
            score += Math.Min(0.12f, (candidateThreatDistance - currentThreatDistance) * 0.04f);
        }

        if (holdsSafePosition)
        {
            score += 0.04f;
        }

        return true;
    }

    private static bool TryComputeMeleeSpacingScore(
        GameLocationCharacter actor,
        int3 candidatePosition,
        IGameLocationBattleService battleService,
        CombatAiTurnPlan turnPlan,
        bool canPreferredFromCandidate,
        out float score)
    {
        score = 0f;

        var target = turnPlan.ActionProbe.Target;
        var isClearAllyCorridorPlan =
            turnPlan.MovementPlan.ReasonKind == CombatAiMovementPlanReasonKind.ClearAllyCorridor;

        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            (!canPreferredFromCandidate && !isClearAllyCorridorPlan) ||
            candidatePosition == actor.LocationPosition)
        {
            return false;
        }

        if (ShouldRejectMeleeSpacingTrafficMove(actor, candidatePosition, target, battleService))
        {
            return false;
        }

        var isFlyingSpacing = turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.FlyingMeleeSpacingPolicy;
        var moveCost = ComputeForcedMoveCost(actor.LocationPosition, candidatePosition);

        if (!isFlyingSpacing && moveCost > MeleeSpacingMaximumMoveCost)
        {
            return false;
        }

        if (isFlyingSpacing && moveCost > Math.Max(actor.RemainingTacticalMoves, 0))
        {
            return false;
        }

        if (WouldLeaveCurrentReactionMeleeReach(
                actor,
                actor.LocationPosition,
                candidatePosition,
                battleService))
        {
            return false;
        }

        var currentCrowding = EvaluateMeleeAllyCrowding(actor, target, actor.LocationPosition);
        var candidateCrowding = EvaluateMeleeAllyCrowding(actor, target, candidatePosition);
        var blocksLargeAllyCorridor = IsBlockingLargeMeleeAllyCorridor(
            actor,
            target,
            battleService,
            out _);
        var clearsLargeAllyCorridor =
            blocksLargeAllyCorridor &&
            !WouldPositionBlockLargeMeleeAllyCorridor(
                actor,
                candidatePosition,
                target,
                battleService,
                out _);
        var clearsAllyAdjacency =
            currentCrowding.AdjacentCount > 0 &&
            candidateCrowding.AdjacentCount == 0 &&
            candidateCrowding.NearestHorizontalGap >= MeleeSpacingRequiredGridGap;

        if (!clearsAllyAdjacency && !clearsLargeAllyCorridor)
        {
            return false;
        }

        var currentDistance = ComputeGridDistance(actor.LocationPosition, target.LocationPosition);
        var candidateDistance = ComputeGridDistance(candidatePosition, target.LocationPosition);

        var regressionTolerance = isClearAllyCorridorPlan
            ? ClearAllyCorridorRegressionTolerance
            : MovementGoalRegressionTolerance;

        if (!isFlyingSpacing && candidateDistance > currentDistance + regressionTolerance)
        {
            return false;
        }

        score = MovementGoalPreferredRangeScore - 0.08f;

        if (clearsAllyAdjacency)
        {
            score += 0.18f + (currentCrowding.AdjacentCount * 0.04f);
        }

        if (clearsLargeAllyCorridor)
        {
            score += 0.22f;
        }

        if (currentCrowding.NearestHorizontalGap < int.MaxValue &&
            candidateCrowding.NearestHorizontalGap > currentCrowding.NearestHorizontalGap)
        {
            score += Math.Min(
                0.06f,
                (candidateCrowding.NearestHorizontalGap - currentCrowding.NearestHorizontalGap) * 0.02f);
        }

        if (isFlyingSpacing)
        {
            score += ComputeFlyingMeleeHeightScore(actor, candidatePosition, target);
        }

        return true;
    }

    private static float ComputeFlyingMeleeHeightScore(
        GameLocationCharacter actor,
        int3 candidatePosition,
        GameLocationCharacter target)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            !BuildProfile(actor).HasFlight)
        {
            return 0f;
        }

        var currentHeightDelta = actor.LocationPosition.y - target.LocationPosition.y;
        var candidateHeightDelta = candidatePosition.y - target.LocationPosition.y;
        var score = 0f;

        if (candidateHeightDelta > 0)
        {
            score += Math.Min(0.10f, candidateHeightDelta * 0.025f);
        }

        if (candidateHeightDelta > currentHeightDelta)
        {
            score += Math.Min(0.08f, (candidateHeightDelta - currentHeightDelta) * 0.025f);
        }

        if (candidateHeightDelta > 0 &&
            ComputeHorizontalGridStepDistance(candidatePosition, target.LocationPosition) <= 1)
        {
            score += 0.05f;
        }

        return score;
    }

    private static float ComputeFlyingMeleePolicyHeightScore(
        GameLocationCharacter actor,
        int3 candidatePosition,
        CombatAiTurnPlan turnPlan)
    {
        return (turnPlan.MovementPlan.Policy is CombatAiMovementPolicyKind.FlyingPursuitPolicy
                or CombatAiMovementPolicyKind.FlyingMeleeSpacingPolicy) &&
               turnPlan.ActionProbe.PreferredAction == CombatAiActionKind.Melee
            ? ComputeFlyingMeleeHeightScore(actor, candidatePosition, turnPlan.ActionProbe.Target)
            : 0f;
    }

    private static bool IsBlockingLargeMeleeAllyCorridor(
        GameLocationCharacter actor,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        out ulong allyGuid)
    {
        return WouldPositionBlockLargeMeleeAllyCorridor(
            actor,
            actor?.LocationPosition ?? default,
            target,
            battleService,
            out allyGuid);
    }

    private static bool WouldPositionBlockLargeMeleeAllyCorridor(
        GameLocationCharacter actor,
        int3 position,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        out ulong allyGuid)
    {
        allyGuid = 0;

        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null)
        {
            return false;
        }

        foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), target.LocationPosition))
        {
            if (ally == actor ||
                ally?.RulesetCharacter == null ||
                ally.Side != actor.Side ||
                !IsLargeMeleeAlly(ally) ||
                CanAttackInMeleeFromPosition(ally, ally.LocationPosition, target, target.LocationPosition, battleService))
            {
                continue;
            }

            var allyDistanceToTarget = ComputeGridDistance(ally.LocationPosition, target.LocationPosition);

            if (allyDistanceToTarget > Math.Max(ally.RemainingTacticalMoves, ally.MaxTacticalMoves) + 2.0f)
            {
                continue;
            }

            var blocksLine =
                ComputeDistanceToSegment2D(position, ally.LocationPosition, target.LocationPosition) <= 0.75f;
            var blocksContactSlot =
                IsLargeMeleeAllyTargetContactSlot(ally, position, target, battleService, allyDistanceToTarget);

            if (!blocksLine && !blocksContactSlot)
            {
                continue;
            }

            allyGuid = ally.Guid;
            return true;
        }

        return false;
    }

    private static bool WouldClearAllyCorridorStillBlockTargetAlly(
        GameLocationCharacter actor,
        int3 position,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        CombatAiTurnPlan turnPlan)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null)
        {
            return false;
        }

        var allyGuid = turnPlan.MovementPlan.AllyGuid;

        if (allyGuid == 0)
        {
            return false;
        }

        foreach (var ally in Gui.Battle.AllContenders)
        {
            if (ally == actor ||
                ally?.RulesetCharacter == null ||
                ally.Side != actor.Side ||
                ally.Guid != allyGuid ||
                !IsLargeMeleeAlly(ally) ||
                CanAttackInMeleeFromPosition(ally, ally.LocationPosition, target, target.LocationPosition, battleService))
            {
                continue;
            }

            var allyDistanceToTarget = ComputeGridDistance(ally.LocationPosition, target.LocationPosition);

            if (allyDistanceToTarget > Math.Max(ally.RemainingTacticalMoves, ally.MaxTacticalMoves) + 2.0f)
            {
                return false;
            }

            if (ComputeDistanceToSegment2D(position, ally.LocationPosition, target.LocationPosition) > 0.75f)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static bool IsLargeMeleeAllyTargetContactSlot(
        GameLocationCharacter ally,
        int3 position,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        float allyDistanceToTarget)
    {
        if (ally?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            position == target.LocationPosition)
        {
            return false;
        }

        if (ComputeGridDistance(ally.LocationPosition, position) > allyDistanceToTarget + 0.5f)
        {
            return false;
        }

        if (CanAttackInMeleeFromPosition(ally, position, target, target.LocationPosition, battleService))
        {
            return true;
        }

        return ComputeHorizontalGridStepDistance(position, target.LocationPosition) <= 1 &&
               ComputeGridDistance(position, target.LocationPosition) <= MeleeSpacingTargetPressureDistance;
    }

    private static bool ShouldRejectMeleeSpacingTrafficMove(
        GameLocationCharacter actor,
        int3 destination,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null)
        {
            return false;
        }

        if (ShouldRejectAllyFireLaneGate(actor, destination, target, battleService))
        {
            return true;
        }

        return WouldPositionBlockLargeMeleeAllyCorridor(actor, destination, target, battleService, out _);
    }

    private readonly struct MeleeAllyCrowdingFacts(
        int adjacentCount,
        int nearestHorizontalGap)
    {
        internal int AdjacentCount { get; } = adjacentCount;
        internal int NearestHorizontalGap { get; } = nearestHorizontalGap;
    }

    private static MeleeAllyCrowdingFacts EvaluateMeleeAllyCrowding(
        GameLocationCharacter actor,
        GameLocationCharacter target,
        int3 actorPosition)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            Gui.Battle == null)
        {
            return new MeleeAllyCrowdingFacts(0, int.MaxValue);
        }

        var count = 0;
        var nearest = int.MaxValue;

        foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), target.LocationPosition))
        {
            if (ally == actor ||
                ally?.RulesetCharacter == null ||
                ally.Side != actor.Side ||
                ComputeHorizontalGridStepDistance(ally.LocationPosition, target.LocationPosition) >
                MeleeSpacingTargetPressureDistance)
            {
                continue;
            }

            var horizontalGap = ComputeHorizontalGridStepDistance(ally.LocationPosition, actorPosition);

            nearest = Math.Min(nearest, horizontalGap);

            if (horizontalGap <= MeleeSpacingAllyAdjacentGridSteps)
            {
                count++;
            }
        }

        return new MeleeAllyCrowdingFacts(count, nearest);
    }

    private static bool IsImproveFiringPositionPlan(CombatAiTurnPlan turnPlan)
    {
        return turnPlan.MovementPlan.ReasonKind is
            CombatAiMovementPlanReasonKind.ImproveFiringPosition or
            CombatAiMovementPlanReasonKind.ConnectFiringLine;
    }

    private static bool TryGetMinimumLineRouteProgress(
        CombatAiMovementPolicyKind policy,
        out float minimumProgress)
    {
        switch (policy)
        {
            case CombatAiMovementPolicyKind.SpellLinePolicy:
                minimumProgress = MinimumSpellRouteProgress;
                return true;
            case CombatAiMovementPolicyKind.RangedLinePolicy:
                minimumProgress = MinimumRangedRouteProgress;
                return true;
            default:
                minimumProgress = 0f;
                return false;
        }
    }

    private static bool TryComputeImproveFiringPositionScore(
        GameLocationCharacter actor,
        int3 candidatePosition,
        IGameLocationBattleService battleService,
        CombatAiTurnPlan turnPlan,
        bool canPreferredFromCandidate,
        out float score,
        out CombatAiActionKind actionKind)
    {
        score = 0f;
        actionKind = CombatAiActionKind.None;

        var target = turnPlan.ActionProbe.Target;

        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        var canBackupFromCandidate =
            !canPreferredFromCandidate &&
            turnPlan.ActionProbe.BackupAction != CombatAiActionKind.None &&
            CanUseActionKindAtPosition(
                actor,
                candidatePosition,
                target,
                turnPlan.ActionProbe.BackupAction,
                battleService);
        var attackCapable = canPreferredFromCandidate || canBackupFromCandidate;
        var connectsFiringLine = IsConnectedFiringLinePlan(turnPlan);

        if (!attackCapable)
        {
            var currentTurnsToAction = EstimateTurnsToPreferredAction(
                actor,
                turnPlan,
                actor.LocationPosition);
            var candidateTurnsToAction = EstimateTurnsToPreferredAction(
                actor,
                turnPlan,
                candidatePosition);

            if (connectsFiringLine &&
                !WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService) &&
                ShouldAllowDisconnectedRangedSeekImprovement(
                    actor,
                    turnPlan,
                    actor.LocationPosition,
                    candidatePosition,
                    currentTurnsToAction,
                    candidateTurnsToAction))
            {
                var seekCurrentDistance = ComputeGridDistance(actor.LocationPosition, target.LocationPosition);
                var seekCandidateDistance = ComputeGridDistance(candidatePosition, target.LocationPosition);
                var progress = Math.Max(0f, seekCurrentDistance - seekCandidateDistance);
                var turnsImprovement =
                    currentTurnsToAction > 0 &&
                    candidateTurnsToAction >= 0 &&
                    candidateTurnsToAction < currentTurnsToAction
                        ? currentTurnsToAction - candidateTurnsToAction
                        : 0;

                actionKind = turnPlan.ActionProbe.PreferredAction;
                score = MovementGoalSearchScore +
                        Mathf.Clamp01(progress / Math.Max(seekCurrentDistance, 1f)) * 0.20f +
                        Math.Min(0.35f, turnsImprovement * 0.15f);
                return true;
            }

            return false;
        }

        actionKind = canPreferredFromCandidate
            ? turnPlan.ActionProbe.PreferredAction
            : turnPlan.ActionProbe.BackupAction;

        var currentPosition = actor.LocationPosition;
        var currentDistance = ComputeGridDistance(currentPosition, target.LocationPosition);
        var candidateDistance = ComputeGridDistance(candidatePosition, target.LocationPosition);
        var range = Math.Max(1f, GetPreferredActionRange(turnPlan.ActionProbe));
        var idealDistance = Math.Max(2f, range * 0.65f);
        var currentRangeError = Math.Abs(currentDistance - idealDistance);
        var candidateRangeError = Math.Abs(candidateDistance - idealDistance);
        var improvesRangeBand = candidateRangeError + 0.75f < currentRangeError;
        var gainsHighGround = candidatePosition.y > currentPosition.y;
        var hasCurrentCover = TryGetCoverFromPosition(
            actor,
            currentPosition,
            target,
            actionKind,
            battleService,
            out var currentCover);
        var hasCandidateCover = TryGetCoverFromPosition(
            actor,
            candidatePosition,
            target,
            actionKind,
            battleService,
            out var candidateCover);
        var improvesCover = hasCurrentCover && hasCandidateCover && candidateCover < currentCover;
        var reducesRecentThreat =
            TryGetCurrentOrRecentMeleeThreat(actor, currentPosition, out _, out var threatPosition, out _) &&
            !WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService) &&
            ComputeGridDistance(candidatePosition, threatPosition) >
            ComputeGridDistance(currentPosition, threatPosition) + 0.5f;
        var strictImprovement = connectsFiringLine ||
                                improvesRangeBand ||
                                gainsHighGround ||
                                improvesCover ||
                                reducesRecentThreat;
        var forcedReposition =
            TryGetRepeatedRangedAttackMemory(actor, target, out var repeatMemory) &&
            repeatMemory.RepeatCount >= ForcedFiringPositionRepeatThreshold;
        var rangeDoesNotRegress = candidateRangeError <= currentRangeError + 0.75f;
        var coverDoesNotRegress = !hasCurrentCover || !hasCandidateCover || candidateCover <= currentCover;
        var avoidsThreat = !WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService);
        var forcedSafeReposition =
            forcedReposition &&
            candidatePosition != currentPosition &&
            avoidsThreat;

        if (!strictImprovement && !forcedSafeReposition)
        {
            return false;
        }

        score = MovementGoalPreferredRangeScore + 0.05f;

        if (connectsFiringLine)
        {
            score += 0.18f;
        }

        if (improvesRangeBand)
        {
            score += Math.Min(0.16f, (currentRangeError - candidateRangeError) * 0.04f);
        }

        if (gainsHighGround)
        {
            score += 0.10f;
        }

        if (improvesCover)
        {
            score += 0.12f;
        }

        if (reducesRecentThreat)
        {
            score += 0.12f;
        }

        if (forcedSafeReposition && !strictImprovement)
        {
            score += 0.04f;
        }

        return true;
    }

    private static bool TryGetCoverFromPosition(
        GameLocationCharacter actor,
        int3 actorPosition,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService,
        out CoverType coverType)
    {
        coverType = CoverType.ThreeQuarter;

        var cacheKey = default(RouteCandidateCacheKey);
        var canUseCache = IsAdvancedCombatAiEnabled &&
                          TryGetRouteCandidateCacheKey(actor, actorPosition, target, actionKind, out cacheKey);

        if (canUseCache && CoverEvaluationCache.TryGetValue(cacheKey, out var cached))
        {
            coverType = cached.CoverType;
            return cached.HasCover;
        }

        ActionModifier modifier;
        var hasModifier = actionKind == CombatAiActionKind.Spell
            ? TryGetAtWillSpellAttackModifierFromPosition(
                actor,
                actorPosition,
                target,
                target.LocationPosition,
                battleService,
                out modifier)
            : TryGetRangedAttackModifierFromPosition(
                actor,
                actorPosition,
                target,
                target.LocationPosition,
                battleService,
                out modifier);

        if (!hasModifier)
        {
            if (canUseCache)
            {
                CoverEvaluationCache[cacheKey] = new CoverEvaluationMemory(false, coverType);
            }

            return false;
        }

        coverType = modifier?.coverType ?? CoverType.ThreeQuarter;

        if (canUseCache)
        {
            CoverEvaluationCache[cacheKey] = new CoverEvaluationMemory(true, coverType);
        }

        return true;
    }

    private static float ComputePreferredActionPolicyBonus(CombatAiMovementPolicyKind policy)
    {
        return policy switch
        {
            CombatAiMovementPolicyKind.SpellLinePolicy => 0.10f,
            CombatAiMovementPolicyKind.RangedLinePolicy => 0.08f,
            CombatAiMovementPolicyKind.MeleePursuitPolicy or CombatAiMovementPolicyKind.FlyingPursuitPolicy => 0.04f,
            _ => 0f
        };
    }

    private static float ComputePreferredRangeProximityBonus(CombatAiTurnPlan turnPlan, float candidateDistance)
    {
        if (turnPlan.MovementPlan.Policy is not (CombatAiMovementPolicyKind.SpellLinePolicy
                or CombatAiMovementPolicyKind.RangedLinePolicy))
        {
            return 0f;
        }

        var range = GetPreferredActionRange(turnPlan.ActionProbe);

        if (range <= 0f || candidateDistance > range + 0.5f)
        {
            return 0f;
        }

        return 0.12f;
    }

    private static float GetPreferredActionRange(CombatAiActionProbe actionProbe)
    {
        return actionProbe.PreferredAction switch
        {
            CombatAiActionKind.Spell => actionProbe.CapabilityCatalog.AtWillHostileSpellMaximumRange,
            CombatAiActionKind.Ranged => Math.Max(
                actionProbe.CapabilityCatalog.TrueRangedMaximumRange,
                ThrownLikeRangedMaximumRange),
            CombatAiActionKind.Melee => 1.5f,
            _ => 0f
        };
    }

    private static int EstimateTurnsToPreferredAction(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        int3 position)
    {
        if (actor?.RulesetCharacter == null ||
            !turnPlan.MovementPlan.HasGoal ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            turnPlan.ActionProbe.PreferredAction == CombatAiActionKind.None)
        {
            return -1;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService != null &&
            CanUseActionKindAtPosition(
                actor,
                position,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.PreferredAction,
                battleService))
        {
            return 0;
        }

        var preferredRange = Math.Max(1f, GetPreferredActionRange(turnPlan.ActionProbe));
        var distance = ComputeGridDistance(position, turnPlan.MovementPlan.TargetPosition);
        var missingDistance = Math.Max(0f, distance - preferredRange);
        var perRoundMove = Math.Max(1f, Math.Max(actor.MaxTacticalMoves, actor.RemainingTacticalMoves));

        return Math.Max(1, Mathf.CeilToInt(missingDistance / perRoundMove));
    }

    private static float ComputeMinimumMovementGoalProgress(
        GameLocationCharacter actor,
        CombatAiMovementGoalKind goal,
        CombatAiMovementPolicyKind policy,
        float currentDistance)
    {
        if (actor == null || currentDistance <= 1f)
        {
            return 0.75f;
        }

        var remainingMove = Math.Max(actor.RemainingTacticalMoves, 1f);

        if (policy is CombatAiMovementPolicyKind.MeleePursuitPolicy or CombatAiMovementPolicyKind.FlyingPursuitPolicy)
        {
            return 0f;
        }

        var desiredUse = policy switch
        {
            CombatAiMovementPolicyKind.SpellLinePolicy => 0.38f,
            CombatAiMovementPolicyKind.RangedLinePolicy => 0.34f,
            CombatAiMovementPolicyKind.SearchKnownTargetPolicy => 0.30f,
            _ => 0.25f
        };
        var lowerBound = policy == CombatAiMovementPolicyKind.SearchKnownTargetPolicy
            ? Math.Min(2f, Math.Max(0.75f, currentDistance - 1f))
            : 0.75f;

        return Math.Min(
            Math.Max(lowerBound, remainingMove * desiredUse),
            Math.Max(lowerBound, currentDistance - 1f));
    }

    private static List<GameLocationCharacter> CollectRelevantEnemyTargets(
        GameLocationCharacter actor,
        DecisionParameters parameters)
    {
        var relevantEnemies = new List<GameLocationCharacter>();
        var hasRelevantPerceivedTarget = parameters.situationalInformation.HasRelevantPerceivedTarget;

        foreach (var enemy in GetKnownEnemyTargets(actor, parameters))
        {
            if (enemy?.RulesetCharacter == null ||
                !AiLocationDefinitions.IsRelevantTargetForCharacter(actor, enemy, hasRelevantPerceivedTarget))
            {
                continue;
            }

            relevantEnemies.Add(enemy);
        }

        return relevantEnemies;
    }

    private static EnemyEvaluation[] CollectEnemyEvaluations(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        int3 position,
        DecisionParameters parameters,
        ulong approachSourceGuid,
        IReadOnlyList<GameLocationCharacter> relevantEnemies)
    {
        if (relevantEnemies == null || relevantEnemies.Count == 0)
        {
            return Array.Empty<EnemyEvaluation>();
        }

        var battleService = parameters.situationalInformation.BattleService;
        var positioningService = parameters.situationalInformation.PositioningService;
        var needFlightFacts = IsAdvancedCombatAiFlightEnabled &&
                              profile.IsMeleeSpecialist &&
                              !profile.HasRangedBackup &&
                              HasVisibleFlightContext(actor, relevantEnemies, position);
        var needThreatFacts = IsAdvancedCombatAiPositioningEnabled ||
                              (IsAdvancedCombatAiProfilesEnabled && profile.Family == CombatAiFamily.Celestial);
        var needRangedFacts = IsAdvancedCombatAiPositioningEnabled ||
                              profile.HasRangedBackup ||
                              profile.Role != CombatAiRole.Melee;
        var evaluations = new EnemyEvaluation[relevantEnemies.Count];

        for (var i = 0; i < relevantEnemies.Count; i++)
        {
            var enemy = relevantEnemies[i];
            var distance = positioningService.ComputeDistanceBetweenCharactersApproximatingSize(
                actor, position, enemy, enemy.LocationPosition);
            var meleeReachable = CanAttackInMeleeFromPosition(actor, position, enemy, enemy.LocationPosition, battleService);
            var unreachableFlyingForMelee = needFlightFacts &&
                                            HasFlightContext(actor, enemy, position) &&
                                            !meleeReachable;
            var exposesActorToMeleeThreat = needThreatFacts &&
                                            CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, position, battleService);
            var rangedAttackAvailableFromPosition = false;
            var spellAttackAvailableFromPosition = false;
            var rangedCoverType = CoverType.ThreeQuarter;
            var enemyCanRangedAttackActorFromPosition = false;
            var actorCoverFromEnemyRangedAttack = CoverType.None;

            if (needRangedFacts &&
                TryGetRangedAttackModifierFromPosition(
                    actor,
                    position,
                    enemy,
                    enemy.LocationPosition,
                    battleService,
                    out var modifier))
            {
                rangedAttackAvailableFromPosition = true;
                rangedCoverType = modifier?.coverType ?? CoverType.ThreeQuarter;
            }

            if (TryGetAtWillSpellAttackModifierFromPosition(
                    actor,
                    position,
                    enemy,
                    enemy.LocationPosition,
                    battleService,
                    out var spellModifier))
            {
                spellAttackAvailableFromPosition = true;
                rangedCoverType = spellModifier?.coverType ?? rangedCoverType;
            }

            if (needThreatFacts &&
                TryGetRangedAttackModifierFromPosition(
                    enemy,
                    enemy.LocationPosition,
                    actor,
                    position,
                    battleService,
                    out var enemyModifier))
            {
                enemyCanRangedAttackActorFromPosition = true;
                actorCoverFromEnemyRangedAttack = enemyModifier?.coverType ?? CoverType.None;
            }

            var rulesetEnemy = enemy.RulesetCharacter;
            var knownRangedOrCasterThreat =
                enemyCanRangedAttackActorFromPosition ||
                rulesetEnemy.ConcentratedSpell != null ||
                (needThreatFacts && HasSpellcasting(rulesetEnemy));

            var enemyWounded =
                TryGetHitPointState(rulesetEnemy, out var enemyCurrentHitPoints, out var enemyMissingHitPoints) &&
                enemyMissingHitPoints > enemyCurrentHitPoints;

            evaluations[i] = new EnemyEvaluation(
                enemy,
                distance,
                meleeReachable,
                unreachableFlyingForMelee,
                exposesActorToMeleeThreat,
                rangedAttackAvailableFromPosition,
                spellAttackAvailableFromPosition,
                rangedCoverType,
                enemyCanRangedAttackActorFromPosition,
                actorCoverFromEnemyRangedAttack,
                knownRangedOrCasterThreat,
                enemyWounded,
                rulesetEnemy.ConcentratedSpell != null,
                enemy.Guid == approachSourceGuid);
        }

        return evaluations;
    }

    private static float ComputeDistancePreferenceScore(CombatAiProfile profile, float distance, float floatParameter)
    {
        var scale = Math.Max(floatParameter, 1f);

        if (profile.PrefersAggressivePursuit && !profile.PrefersDistance)
        {
            return Mathf.Lerp(1f, 0f, Mathf.Clamp(distance / scale, 0f, 1f));
        }

        var preferredDistance = profile.PrefersAerialCombat ? scale : Math.Max(2f, scale * 0.65f);
        var deviation = Mathf.Abs(distance - preferredDistance);

        return 1f - Mathf.Clamp01(deviation / scale);
    }

    private static float ComputeEnemyPriorityWeight(
        CombatAiProfile profile,
        EnemyEvaluation evaluation)
    {
        if (!IsAdvancedCombatAiProfilesEnabled || evaluation.Enemy?.RulesetCharacter == null)
        {
            return 1f;
        }

        var weight = 1f;

        if (profile.Family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity)
        {
            if (evaluation.Distance <= 3f)
            {
                weight += 0.10f;
            }

            if (evaluation.IsWounded)
            {
                weight += 0.08f;
            }
        }

        if (profile.Temperament is CombatAiTemperament.Aggressive or CombatAiTemperament.Relentless &&
            evaluation.Distance <= 4f)
        {
            weight += 0.06f;
        }

        if (profile.Temperament is CombatAiTemperament.Disciplined
            or CombatAiTemperament.Cunning
            or CombatAiTemperament.CunningAggressive)
        {
            if (evaluation.IsConcentrating)
            {
                weight += 0.08f;
            }

            if (evaluation.IsWounded)
            {
                weight += 0.06f;
            }
        }

        switch (profile.Family)
        {
            case CombatAiFamily.Celestial:
                if (evaluation.ExposesActorToMeleeThreat)
                {
                    weight += 0.12f;
                }

                if (evaluation.Distance <= 3f)
                {
                    weight += 0.06f;
                }

                if (evaluation.IsConcentrating)
                {
                    weight += 0.04f;
                }

                break;
            case CombatAiFamily.Fey:
                if (evaluation.Distance is > 2f and <= 8f)
                {
                    weight += 0.05f;
                }

                if (evaluation.IsWounded)
                {
                    weight += 0.08f;
                }

                if (evaluation.ExposesActorToMeleeThreat)
                {
                    weight -= 0.03f;
                }

                break;
            case CombatAiFamily.Fiend:
                if (profile.PrefersDistance && evaluation.IsConcentrating)
                {
                    weight += 0.06f;
                }

                if (evaluation.IsWounded)
                {
                    weight += 0.08f;
                }

                break;
            case CombatAiFamily.Giant:
                if (evaluation.Distance <= 4f)
                {
                    weight += 0.18f;
                }

                if (evaluation.ExposesActorToMeleeThreat)
                {
                    weight += 0.10f;
                }

                break;
            case CombatAiFamily.Ooze:
            case CombatAiFamily.Plant:
                if (evaluation.Distance <= 2f)
                {
                    weight += 0.15f;
                }

                if (evaluation.ExposesActorToMeleeThreat)
                {
                    weight += 0.08f;
                }

                break;
        }

        return ClampPriorityWeight(weight);
    }

    private static float ComputePositionBias(
        CombatAiProfile profile,
        CombatAiSelfAssessment self,
        EnemyEvaluation[] evaluations,
        float floatParameter)
    {
        if (!IsAdvancedCombatAiPositioningEnabled || evaluations.Length == 0)
        {
            return 0f;
        }

        var bias = 0.0f;
        var exposedThreats = 0;
        var exposedKnownRangedOrCasterThreats = 0;
        var hasSafeRangedLine = false;
        var canAttackFromPosition = false;
        var coveredRangedThreats = 0;
        var nearestDistance = float.MaxValue;

        for (var i = 0; i < evaluations.Length; i++)
        {
            var evaluation = evaluations[i];

            if (evaluation.ExposesActorToMeleeThreat)
            {
                exposedThreats++;
            }

            if (evaluation.KnownRangedOrCasterThreat && !evaluation.ActorHasUsefulCover)
            {
                exposedKnownRangedOrCasterThreats++;
            }

            if (evaluation.RangedAttackAvailableFromPosition && evaluation.RangedCoverType <= CoverType.Half)
            {
                hasSafeRangedLine = true;
            }

            if (evaluation.CanAttackFromPosition)
            {
                canAttackFromPosition = true;
            }

            if (evaluation.ActorHasUsefulCover)
            {
                coveredRangedThreats++;
            }

            if (evaluation.Distance < nearestDistance)
            {
                nearestDistance = evaluation.Distance;
            }
        }

        if (profile.PrefersDistance)
        {
            var exposedPenalty = 0.10f;

            if (self.IsBloodied || self.HasSeriousCondition)
            {
                exposedPenalty += 0.04f;
            }

            if (profile.Family is CombatAiFamily.Celestial or CombatAiFamily.Fey)
            {
                exposedPenalty += 0.05f;
            }
            else if (profile.Family is CombatAiFamily.Giant or CombatAiFamily.Ooze or CombatAiFamily.Plant)
            {
                exposedPenalty = Math.Max(0.04f, exposedPenalty - 0.05f);
            }

            bias -= Mathf.Min(0.30f, exposedThreats * exposedPenalty);

            if (hasSafeRangedLine)
            {
                bias += profile.Family == CombatAiFamily.Fey ? 0.16f : 0.12f;
            }
        }
        else if (profile.Family is CombatAiFamily.Giant or CombatAiFamily.Ooze or CombatAiFamily.Plant)
        {
            bias -= Mathf.Min(0.08f, exposedThreats * 0.02f);
        }

        if (canAttackFromPosition)
        {
            var attackBias = profile.PrefersAggressivePursuit ? 0.20f : 0.12f;

            if (profile.Family == CombatAiFamily.Celestial)
            {
                attackBias -= 0.04f;
            }
            else if (profile.Family is CombatAiFamily.Giant or CombatAiFamily.Ooze or CombatAiFamily.Plant)
            {
                attackBias += 0.04f;
            }

            bias += attackBias;
        }

        if (profile.PrefersAggressivePursuit && nearestDistance < float.MaxValue)
        {
            var pursuitBias = 0.15f * (1f - Mathf.Clamp01(nearestDistance / Math.Max(floatParameter, 1f)));

            if (profile.Family is CombatAiFamily.Giant or CombatAiFamily.Ooze or CombatAiFamily.Plant)
            {
                pursuitBias += 0.05f;
            }

            bias += pursuitBias;
        }

        if (ShouldAvoidExposedAdvance(profile, self, canAttackFromPosition) &&
            exposedKnownRangedOrCasterThreats > 0)
        {
            var exposedAdvancePenalty = self.IsBloodied || self.HasSeriousCondition ? 0.18f : 0.12f;

            if (profile.Temperament is CombatAiTemperament.Cautious
                    or CombatAiTemperament.Cunning
                    or CombatAiTemperament.CunningAggressive)
            {
                exposedAdvancePenalty += 0.04f;
            }

            bias -= Mathf.Min(0.28f, exposedKnownRangedOrCasterThreats * exposedAdvancePenalty);
        }

        if (profile.PrefersAerialCombat && evaluations.Length > 1)
        {
            bias += 0.08f;
        }

        if (profile.Family == CombatAiFamily.Fey && hasSafeRangedLine && exposedThreats == 0)
        {
            bias += 0.06f;
        }

        if (profile.Family == CombatAiFamily.Celestial && exposedThreats == 0)
        {
            bias += 0.04f;
        }

        bias += ComputeCoverBias(profile, self, canAttackFromPosition, exposedThreats, coveredRangedThreats);

        return Mathf.Clamp(bias, -0.25f, 0.35f);
    }

    private static bool ShouldAvoidExposedAdvance(
        CombatAiProfile profile,
        CombatAiSelfAssessment self,
        bool canAttackFromPosition)
    {
        if (canAttackFromPosition ||
            profile.PrefersDistance ||
            profile.Role is not (CombatAiRole.Melee or CombatAiRole.Hybrid))
        {
            return false;
        }

        if (profile.Family is CombatAiFamily.Beast
                or CombatAiFamily.Monstrosity
                or CombatAiFamily.Ooze
                or CombatAiFamily.Plant ||
            profile.Temperament is CombatAiTemperament.Aggressive
                or CombatAiTemperament.Relentless)
        {
            return self.IsCritical;
        }

        return self.IsBloodied ||
               self.HasSeriousCondition ||
               profile.Temperament is CombatAiTemperament.Cautious
                   or CombatAiTemperament.Disciplined
                   or CombatAiTemperament.Cunning
                   or CombatAiTemperament.CunningAggressive
                   or CombatAiTemperament.Opportunistic;
    }

    private static float ComputeCoverBias(
        CombatAiProfile profile,
        CombatAiSelfAssessment self,
        bool canAttackFromPosition,
        int exposedThreats,
        int coveredRangedThreats)
    {
        if (!canAttackFromPosition || coveredRangedThreats == 0)
        {
            return 0f;
        }

        var coverBias = profile.PrefersDistance ? 0.04f : 0.0f;

        if (IsAdvancedCombatAiProfilesEnabled)
        {
            if (profile.Temperament is CombatAiTemperament.Cautious
                    or CombatAiTemperament.Cunning
                    or CombatAiTemperament.CunningAggressive ||
                profile.Family == CombatAiFamily.Fey)
            {
                coverBias += 0.04f;
            }

            if (profile.Family is CombatAiFamily.Giant or CombatAiFamily.Ooze or CombatAiFamily.Plant ||
                profile.Temperament == CombatAiTemperament.Relentless ||
                profile.IsMeleeSpecialist)
            {
                coverBias *= 0.5f;
            }
        }

        if (self.IsBloodied || self.HasSeriousCondition)
        {
            coverBias += 0.03f;
        }

        if (exposedThreats > 0 && !self.IsBloodied)
        {
            coverBias *= 0.75f;
        }

        return Mathf.Min(0.14f, coverBias + (coveredRangedThreats - 1) * 0.02f);
    }

    private static float ClampPriorityWeight(float weight)
    {
        return Mathf.Clamp(weight, 0.75f, 1.35f);
    }

    private static CombatAiSelfAssessment BuildSelfAssessment(GameLocationCharacter actor)
    {
        var rulesetCharacter = actor?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            return default;
        }

        var hasHitPoints = TryGetHitPointState(rulesetCharacter, out var currentHitPoints, out var missingHitPoints);
        var maxHitPoints = currentHitPoints + missingHitPoints;
        var isWounded = hasHitPoints && missingHitPoints > 0;
        var isBloodied = hasHitPoints && missingHitPoints >= currentHitPoints && isWounded;
        var isCritical = hasHitPoints && maxHitPoints > 0 && currentHitPoints > 0 && currentHitPoints * 4 <= maxHitPoints;
        var isProne = rulesetCharacter.HasConditionOfTypeOrSubType(ConditionProne);
        var isRestrained = rulesetCharacter.HasConditionOfTypeOrSubType(ConditionRestrained);
        var hasSeriousCondition =
            isProne ||
            isRestrained ||
            rulesetCharacter.HasConditionOfTypeOrSubType(ConditionBlinded) ||
            rulesetCharacter.HasConditionOfTypeOrSubType(ConditionFrightened) ||
            rulesetCharacter.HasConditionOfTypeOrSubType(ConditionParalyzed) ||
            rulesetCharacter.HasConditionOfTypeOrSubType(ConditionStunned) ||
            rulesetCharacter.HasConditionOfTypeOrSubType(ConditionIncapacitated);

        return new CombatAiSelfAssessment(
            isWounded,
            isBloodied,
            isCritical,
            isProne,
            isRestrained,
            hasSeriousCondition,
            rulesetCharacter.ConcentratedSpell != null);
    }

    private static bool TryGetHitPointState(
        RulesetCharacter rulesetCharacter,
        out int currentHitPoints,
        out int missingHitPoints)
    {
        currentHitPoints = 0;
        missingHitPoints = 0;

        if (rulesetCharacter == null ||
            !rulesetCharacter.TryGetAttribute(AttributeDefinitions.HitPoints, out _))
        {
            return false;
        }

        currentHitPoints = Math.Max(0, rulesetCharacter.CurrentHitPoints);
        missingHitPoints = Math.Max(0, rulesetCharacter.MissingHitPoints);

        return true;
    }

    private static bool HasDodgingCondition(RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter == null)
        {
            return false;
        }

        foreach (var conditions in rulesetCharacter.ConditionsByCategory.Values)
        {
            foreach (var condition in conditions)
            {
                if (condition.ConditionDefinition?.Name == DatabaseHelper.ConditionDefinitions.ConditionDodging.Name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasAnyUsefulHostileActionAgainstVisibleEnemies(
        GameLocationCharacter character,
        IGameLocationBattleService battleService)
    {
        if (HasAnyUsableAttackAgainstVisibleEnemies(character, battleService))
        {
            return true;
        }

        foreach (var enemy in GetKnownEnemyTargets(character))
        {
            if (TryGetAtWillSpellAttackModifierFromPosition(
                    character,
                    character.LocationPosition,
                    enemy,
                    enemy.LocationPosition,
                    battleService,
                    out _) ||
                TryGetResidualHostileSpellFromPosition(
                    character,
                    character.LocationPosition,
                    enemy,
                    battleService,
                    out _,
                    out _,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    private static CombatAiActionKind[] GetTerminalReprobeActionKinds(CombatAiActionProbe actionProbe)
    {
        var actionKinds = new List<CombatAiActionKind>();

        AddTerminalReprobeActionKind(actionKinds, actionProbe.PreferredAction);
        AddTerminalReprobeActionKind(actionKinds, actionProbe.BackupAction);

        if (actionProbe.CapabilityCatalog.HasAnyRanged)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Ranged);
        }

        if (actionProbe.CapabilityCatalog.HasMelee)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Melee);
        }

        if (actionProbe.HasAtWillHostileSpell || actionProbe.CapabilityCatalog.HasAtWillHostileSpell)
        {
            AddTerminalReprobeActionKind(actionKinds, CombatAiActionKind.Spell);
        }

        return actionKinds.ToArray();
    }

    private static void AddTerminalReprobeActionKind(
        ICollection<CombatAiActionKind> actionKinds,
        CombatAiActionKind actionKind)
    {
        if (actionKind != CombatAiActionKind.None && !actionKinds.Contains(actionKind))
        {
            actionKinds.Add(actionKind);
        }
    }

    private static bool HasCurrentValidatedHostileAction(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService,
        GameLocationCharacter priorityTarget = null)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var actionKinds = GetTerminalReprobeActionKinds(actionProbe);

        foreach (var target in GetCurrentHostileActionTargets(character, battleService, priorityTarget))
        {
            if (target?.RulesetCharacter == null || target.Side == character.Side)
            {
                continue;
            }

            if (TryGetResidualHostileSpellFromPosition(
                    character,
                    character.LocationPosition,
                    target,
                    battleService,
                    out _,
                    out _,
                    out _))
            {
                return true;
            }

            foreach (var actionKind in actionKinds)
            {
                if (!ValidateResidualMainAction(character, target, actionKind, battleService))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static CurrentTerminalActionScan BuildCurrentTerminalActionScan(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService,
        CombatAiSelfAssessment self,
        GameLocationCharacter priorityTarget = null)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return new CurrentTerminalActionScan(
                false,
                false);
        }

        if (HasCurrentValidatedHostileAction(
                character,
                actionProbe,
                battleService,
                priorityTarget))
        {
            return new CurrentTerminalActionScan(
                true,
                false);
        }

        var hasUsefulUtility = HasCurrentUsefulUtility(character, self);

        if (hasUsefulUtility)
        {
            return new CurrentTerminalActionScan(
                false,
                true);
        }

        return new CurrentTerminalActionScan(
            false,
            false);
    }

    private static bool HasCurrentUsefulUtility(
        GameLocationCharacter character,
        CombatAiSelfAssessment self)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            !(self.IsBloodied || self.IsCritical || self.HasSeriousCondition))
        {
            return false;
        }

        foreach (var usablePower in rulesetCharacter.UsablePowers)
        {
            if (!IsEligibleFallbackSelfBuff(usablePower, rulesetCharacter))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool ShouldReleaseRangedBackupAfterFailedMeleePursuit(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService,
        bool canContinueMoving)
    {
        if (character?.RulesetCharacter == null ||
            actionProbe.Target?.RulesetCharacter == null ||
            battleService == null ||
            actionProbe.PreferredAction != CombatAiActionKind.Melee ||
            actionProbe.BackupAction != CombatAiActionKind.Ranged ||
            !actionProbe.CanUseBackupAction)
        {
            return false;
        }

        if (canContinueMoving && !TryGetSameTurnClosedMeleePursuitRoute(character, out _))
        {
            return false;
        }

        if (!ValidateResidualMainAction(
                character,
                actionProbe.Target,
                CombatAiActionKind.Ranged,
                battleService))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetSameTurnClosedMeleePursuitRoute(
        GameLocationCharacter character,
        out RouteMoveCompletionClosedMemory closedRoute)
    {
        closedRoute = default;

        if (character?.RulesetCharacter == null ||
            !RouteMoveCompletionClosedCache.TryGetValue(character.Guid, out closedRoute) ||
            closedRoute.MovementGoal != CombatAiMovementGoalKind.AdvanceToMelee ||
            closedRoute.Round != GetCurrentBattleRound() ||
            closedRoute.TurnStamp != Math.Max(1, ObservedCombatMemoryTurnStamp))
        {
            return false;
        }

        return closedRoute.IsNoMove ||
               closedRoute.IsLateCompletion ||
               closedRoute.IsRouteUnavailable ||
               closedRoute.HasNoConnectedRoute ||
               closedRoute.IsGroundMeleePartial;
    }

    private static bool CanUseTerminalReadyDodgeFallback(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CurrentTerminalActionScan currentActionScan)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsActiveBattleContender(character))
        {
            return false;
        }

        if (currentActionScan.BlocksReadyOrDodge ||
            turnPlan.ActionProbe.CanUsePreferredAction ||
            turnPlan.ActionProbe.CanUseBackupAction)
        {
            return false;
        }

        if (HasQueuedActionChain(character) ||
            HasPendingReactionRequests() ||
            character.MoveStepInProgress ||
            MovementTracker.TryGetMovement(character.Guid, out _) ||
            ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            ActionLinkedMoveSettlingCache.ContainsKey(character.Guid) ||
            GroundMeleeMoveSettlingCache.ContainsKey(character.Guid))
        {
            return false;
        }

        MarkFallbackFreeJumpMovementCandidate(character, turnPlan);

        if (HasUnattemptedPreMainRouteMove(character, turnPlan))
        {
            return false;
        }

        return true;
    }

    private static bool TryUseFallbackReady(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        bool clearTurnCacheAfterAction = true)
    {
        var actionEconomy = BuildActionEconomySnapshot(character);

        if (!CanSpendTerminalMainAction(character, actionEconomy))
        {
            return false;
        }

        if (!TryGetFallbackReadyOpportunity(
                character,
                profile,
                turnPlan,
                battleService,
                out var readyActionType))
        {
            return false;
        }

        character.MyExecuteActionReady(readyActionType);

        if (clearTurnCacheAfterAction)
        {
            InvalidateTurnPlanningCache(character);
        }

        return true;
    }

    private static bool TryGetFallbackReadyOpportunity(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        out ReadyActionType readyActionType)
    {
        readyActionType = default;

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            character.GetActionStatus(Id.Ready, ActionScope.Battle) != ActionStatus.Available ||
            !HasRelevantPerceivedEnemies(character) ||
            !TryGetFallbackReadyActionType(character, profile, out readyActionType))
        {
            return false;
        }

        var target = turnPlan.ActionProbe.Target ?? SelectPrimaryTarget(character);

        if (target?.RulesetCharacter == null)
        {
            return false;
        }

        var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);
        var range = GetFallbackReadyRange(character, profile, readyActionType);
        var targetMove = Math.Max(ReadyOpportunityDefaultTargetMove, target.MaxTacticalMoves);
        var effectiveRange = range + targetMove;

        if (range <= 0f || distance > effectiveRange)
        {
            return false;
        }

        return true;
    }

    private static float GetFallbackReadyRange(
        GameLocationCharacter character,
        CombatAiProfile profile,
        ReadyActionType readyActionType)
    {
        switch (readyActionType)
        {
            case ReadyActionType.Cantrip when profile.HasSpellcasting:
            {
                var cantrips = new List<SpellDefinition>();
                var range = 0f;

                character.RulesetCharacter.EnumerateReadyAttackCantrips(cantrips);

                foreach (var cantrip in OrderAtWillAttackCantrips(character.RulesetCharacter, cantrips))
                {
                    if (cantrip == null ||
                        cantrip.ActivationTime != ActivationTime.Action ||
                        !character.RulesetCharacter.IsValidReadyCantrip(cantrip))
                    {
                        continue;
                    }

                    range = Math.Max(range, GetSpellRange(character.RulesetCharacter, cantrip));
                }

                return range;
            }
            case ReadyActionType.Ranged:
                return character.GetFirstRangedModeThatCanBeReadied()?.MaxRange ?? 0f;
            case ReadyActionType.Melee:
                return Math.Max(1f, character.RulesetCharacter.AttackModes
                    .Where(ValidatorsWeapon.IsMelee)
                    .Select(mode => mode.ReachRange)
                    .DefaultIfEmpty(1)
                    .Max());
            default:
                return 0f;
        }
    }

    private static bool TryGetFallbackReadyActionType(
        GameLocationCharacter character,
        CombatAiProfile profile,
        out ReadyActionType readyActionType)
    {
        readyActionType = default;

        if (profile.HasSpellcasting && HasEligibleFallbackReadyCantrip(character.RulesetCharacter))
        {
            readyActionType = ReadyActionType.Cantrip;

            return true;
        }

        if (character.GetFirstRangedModeThatCanBeReadied() != null)
        {
            readyActionType = ReadyActionType.Ranged;

            return true;
        }

        if (!profile.HasSpellcasting && HasReadiedMeleeAttackMode(character))
        {
            readyActionType = ReadyActionType.Melee;

            return true;
        }

        return false;
    }

    private static bool HasEligibleFallbackReadyCantrip(RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter == null)
        {
            return false;
        }

        var cantrips = new List<SpellDefinition>();

        rulesetCharacter.EnumerateReadyAttackCantrips(cantrips);

        for (var i = 0; i < cantrips.Count; i++)
        {
            var cantrip = cantrips[i];

            if (cantrip == null ||
                cantrip.ActivationTime != ActivationTime.Action ||
                !rulesetCharacter.IsValidReadyCantrip(cantrip))
            {
                continue;
            }

            var effectDescription = PowerBundle.ModifySpellEffect(cantrip, rulesetCharacter);

            if (effectDescription != null &&
                effectDescription.TargetType is TargetType.Individuals or TargetType.IndividualsUnique)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReadiedMeleeAttackMode(GameLocationCharacter character)
    {
        var mode = character.FindActionAttackMode(
            Id.AttackMain,
            true,
            true,
            true,
            ReadyActionType.Melee);

        return ValidatorsWeapon.IsMelee(mode);
    }

    private static bool HasRelevantPerceivedEnemies(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        foreach (var enemy in GetKnownEnemyTargets(actor))
        {
            if (enemy?.RulesetCharacter != null && enemy.Side != actor.Side)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryUseFallbackAtWillSelfBuff(
        GameLocationCharacter character,
        CombatAiSelfAssessment self,
        CombatAiTurnPlan turnPlan,
        bool clearTurnCacheAfterAction = true)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            !(self.IsBloodied || self.IsCritical || self.HasSeriousCondition))
        {
            return false;
        }

        if ((turnPlan.MovementPlan.HasGoal ||
             turnPlan.ActionProbe.HasAtWillHostileSpell ||
             turnPlan.ActionProbe.CanUsePreferredAction ||
             turnPlan.ActionProbe.CanUseBackupAction) &&
            !(self.IsCritical || self.HasSeriousCondition))
        {
            return false;
        }

        foreach (var usablePower in rulesetCharacter.UsablePowers)
        {
            if (!IsEligibleFallbackSelfBuff(usablePower, rulesetCharacter))
            {
                continue;
            }

            character.MyExecuteActionPowerNoCost(usablePower, character);

            if (clearTurnCacheAfterAction)
            {
                InvalidateTurnPlanningCache(character);
            }

            return true;
        }

        return false;
    }

    private static bool IsEligibleFallbackSelfBuff(
        RulesetUsablePower usablePower,
        RulesetCharacter actor)
    {
        var power = usablePower?.PowerDefinition;
        var effectDescription = power?.EffectDescription;

        if (power == null ||
            effectDescription == null ||
            power.RechargeRate != RechargeRate.AtWill ||
            power.ActivationTime != ActivationTime.NoCost ||
            power.DelegatedToAction ||
            power.GuiPresentation.Hidden ||
            effectDescription.TargetType != TargetType.Self ||
            effectDescription.TargetSide == Side.Enemy ||
            !actor.CanUsePower(power, true, true) ||
            HasEquivalentActiveEffectOrCondition(actor, effectDescription) ||
            IsLowValueSelfUtility(effectDescription) ||
            !HasDefensiveBuffForm(effectDescription))
        {
            return false;
        }

        return true;
    }

    private static bool IsLowValueSelfUtility(EffectDescription effectDescription)
    {
        var hasAddedCondition = false;

        foreach (var effectForm in effectDescription.EffectForms)
        {
            if (effectForm.FormType != EffectForm.EffectFormType.Condition ||
                effectForm.ConditionForm is not { Operation: ConditionForm.ConditionOperation.Add } conditionForm)
            {
                continue;
            }

            hasAddedCondition = true;

            if (!IsSenseOnlyCondition(conditionForm.ConditionDefinition))
            {
                return false;
            }
        }

        return hasAddedCondition;
    }

    private static bool IsSenseOnlyCondition(ConditionDefinition conditionDefinition)
    {
        return conditionDefinition?.Features != null &&
               conditionDefinition.Features.Count > 0 &&
               conditionDefinition.Features.All(feature => feature is FeatureDefinitionSense);
    }

    private static bool HasDefensiveBuffForm(EffectDescription effectDescription)
    {
        foreach (var effectForm in effectDescription.EffectForms)
        {
            if (effectForm.FormType == EffectForm.EffectFormType.Damage)
            {
                return false;
            }

            if (effectForm.FormType == EffectForm.EffectFormType.Condition &&
                effectForm.ConditionForm is { Operation: ConditionForm.ConditionOperation.Add })
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEquivalentActiveEffectOrCondition(
        RulesetCharacter actor,
        EffectDescription effectDescription)
    {
        foreach (var effectForm in effectDescription.EffectForms)
        {
            if (effectForm.FormType != EffectForm.EffectFormType.Condition ||
                effectForm.ConditionForm is not { Operation: ConditionForm.ConditionOperation.Add } conditionForm ||
                conditionForm.ConditionDefinition == null)
            {
                continue;
            }

            if (actor.HasConditionOfTypeOrSubType(conditionForm.ConditionDefinition.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnattemptedPreMainRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        return character != null &&
               ShouldUsePreMainRouteMove(character, turnPlan) &&
               !PreMainRouteMoveAttemptCache.ContainsKey(character.Guid);
    }

    private static void MarkFallbackFreeJumpMovementCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null || !turnPlan.MovementPlan.HasGoal)
        {
            return;
        }

        if (HasDisconnectedPostMovePositioning(
                character,
                turnPlan))
        {
            return;
        }

        if (character.RemainingTacticalMoves > 0 &&
            character.CanDecideToMoveByItself &&
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) == ActionStatus.Available)
        {
            return;
        }

        if (FreeJumpContext.HasUsefulAiFreeJumpDestination(character))
        {
            GetOrCreateTurnMovementProgress(character, turnPlan)?.MarkFreeJumpMovementCandidate();
        }
    }

    private static bool TryApplyFallbackDodge(
        GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            return false;
        }

        var actionEconomy = BuildActionEconomySnapshot(character);
        var dodgeStatus = character.GetActionStatus(Id.Dodge, ActionScope.Battle);

        if (!CanSpendTerminalMainAction(character, actionEconomy))
        {
            return false;
        }

        if (!actionEconomy.DodgeAvailable || dodgeStatus != ActionStatus.Available)
        {
            return false;
        }

        var rulesetCharacter = character.RulesetCharacter;
        var existingConditions = CollectDodgingConditionGuids(rulesetCharacter);

        PendingFallbackDodgeConditionCache[character.Guid] = existingConditions;
        character.MyExecuteActionDodge();

        return true;
    }

    internal static void NormalizeFallbackDodgeAfterAction(CharacterAction action)
    {
        var character = action?.ActingCharacter;
        var rulesetCharacter = character?.RulesetCharacter;

        if (action?.ActionId != Id.Dodge ||
            rulesetCharacter == null)
        {
            return;
        }

        var hasPendingCondition =
            PendingFallbackDodgeConditionCache.TryGetValue(character.Guid, out var existingConditions);

        if (!hasPendingCondition)
        {
            return;
        }

        PendingFallbackDodgeConditionCache.Remove(character.Guid);

        var condition = FindNewDodgingCondition(rulesetCharacter, existingConditions);

        if (condition == null)
        {
            return;
        }

        NormalizeFallbackDodgeCondition(character, condition);
    }

    private static void NormalizeFallbackDodgeCondition(GameLocationCharacter character, RulesetCondition condition)
    {
        FallbackDodgeConditionCache[character.Guid] = new FallbackDodgeConditionMemory(
            condition.Guid,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static HashSet<ulong> CollectDodgingConditionGuids(RulesetCharacter rulesetCharacter)
    {
        var guids = new HashSet<ulong>();

        if (rulesetCharacter == null)
        {
            return guids;
        }

        foreach (var conditions in rulesetCharacter.ConditionsByCategory.Values)
        {
            foreach (var condition in conditions)
            {
                if (condition.ConditionDefinition?.Name == DatabaseHelper.ConditionDefinitions.ConditionDodging.Name)
                {
                    guids.Add(condition.Guid);
                }
            }
        }

        return guids;
    }

    private static RulesetCondition FindNewDodgingCondition(
        RulesetCharacter rulesetCharacter,
        HashSet<ulong> existingConditions)
    {
        if (rulesetCharacter == null)
        {
            return null;
        }

        foreach (var conditions in rulesetCharacter.ConditionsByCategory.Values)
        {
            foreach (var condition in conditions)
            {
                if (condition.ConditionDefinition?.Name == DatabaseHelper.ConditionDefinitions.ConditionDodging.Name &&
                    (existingConditions == null || !existingConditions.Contains(condition.Guid)))
                {
                    return condition;
                }
            }
        }

        return null;
    }

    private static ulong GetGroundMeleeRouteMemoryKey(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter != null)
        {
            return character.RulesetCharacter.Guid;
        }

        return character?.Guid ?? 0UL;
    }

    private static CombatAiRole GetRole(
        GameLocationCharacter character,
        bool hasRangedBackup,
        bool hasSpellcasting)
    {
        var package = character.BehaviourPackage?.DecisionPackageDefinition;

        if (hasSpellcasting && character.RulesetCharacter is RulesetCharacterMonster)
        {
            return hasRangedBackup ? CombatAiRole.SupportCaster : CombatAiRole.OffensiveCaster;
        }

        if (package == DefaultRangeWithBackupMeleeDecisions)
        {
            return CombatAiRole.Ranged;
        }

        if (package == DefaultSupportCasterWithBackupAttacksDecisions ||
            package == ClericCombatDecisions)
        {
            return CombatAiRole.SupportCaster;
        }

        if (package == OffensiveCasterCombatDecisions ||
            package == CasterCombatDecisions)
        {
            return CombatAiRole.OffensiveCaster;
        }

        if (package == RogueCombatDecisions)
        {
            return CombatAiRole.Hybrid;
        }

        if (package == DefaultMeleeWithBackupRangeDecisions ||
            package == FighterCombatDecisions ||
            package == PaladinCombatDecisions)
        {
            return CombatAiRole.Melee;
        }

        var meleeModes = 0;

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (ValidatorsWeapon.IsMelee(mode))
            {
                meleeModes++;
            }
        }

        return (hasSpellcasting, meleeModes > 0, hasRangedBackup) switch
        {
            (true, false, false) => CombatAiRole.OffensiveCaster,
            (true, false, true) => CombatAiRole.SupportCaster,
            (false, true, false) => CombatAiRole.Melee,
            (false, false, true) => CombatAiRole.Ranged,
            _ => CombatAiRole.Hybrid
        };
    }

    private static CombatAiFamily GetFamily(RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter is not RulesetCharacterMonster rulesetMonster)
        {
            return CombatAiFamily.Humanoid;
        }

        return rulesetMonster.MonsterDefinition.CharacterFamily switch
        {
            AberrationFamilyName => CombatAiFamily.Aberration,
            BeastFamilyName => CombatAiFamily.Beast,
            CelestialFamilyName => CombatAiFamily.Celestial,
            ConstructFamilyName => CombatAiFamily.Construct,
            DragonFamilyName => CombatAiFamily.Dragon,
            ElementalFamilyName => CombatAiFamily.Elemental,
            FeyFamilyName => CombatAiFamily.Fey,
            FiendFamilyName => CombatAiFamily.Fiend,
            GiantFamilyName => CombatAiFamily.Giant,
            HumanoidFamilyName => CombatAiFamily.Humanoid,
            MonstrosityFamilyName => CombatAiFamily.Monstrosity,
            OozeFamilyName => CombatAiFamily.Ooze,
            PlantFamilyName => CombatAiFamily.Plant,
            UndeadFamilyName => CombatAiFamily.Undead,
            _ => CombatAiFamily.Other
        };
    }

    private static CombatAiTemperament GetTemperament(
        RulesetCharacter rulesetCharacter,
        CombatAiFamily family)
    {
        if (!IsAdvancedCombatAiProfilesEnabled)
        {
            return CombatAiTemperament.Neutral;
        }

        var flags = GetPersonalityFlags(rulesetCharacter);

        for (var i = 0; i < flags.Length; i++)
        {
            if (Array.IndexOf(CautiousFlags, flags[i]) >= 0)
            {
                return CombatAiTemperament.Cautious;
            }
        }

        for (var i = 0; i < flags.Length; i++)
        {
            if (Array.IndexOf(DisciplinedFlags, flags[i]) >= 0)
            {
                return CombatAiTemperament.Disciplined;
            }
        }

        for (var i = 0; i < flags.Length; i++)
        {
            if (Array.IndexOf(OpportunisticFlags, flags[i]) >= 0)
            {
                return CombatAiTemperament.Opportunistic;
            }
        }

        return family switch
        {
            CombatAiFamily.Humanoid => CombatAiTemperament.Disciplined,
            CombatAiFamily.Beast or CombatAiFamily.Monstrosity => CombatAiTemperament.Aggressive,
            CombatAiFamily.Aberration or CombatAiFamily.Fiend => CombatAiTemperament.Cunning,
            CombatAiFamily.Dragon or CombatAiFamily.Elemental => CombatAiTemperament.CunningAggressive,
            CombatAiFamily.Undead or CombatAiFamily.Construct or CombatAiFamily.Ooze or CombatAiFamily.Plant =>
                CombatAiTemperament.Relentless,
            CombatAiFamily.Celestial => CombatAiTemperament.Disciplined,
            CombatAiFamily.Fey => CombatAiTemperament.Opportunistic,
            CombatAiFamily.Giant => CombatAiTemperament.Aggressive,
            _ => CombatAiTemperament.Neutral
        };
    }

    private static string[] GetPersonalityFlags(RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter == null)
        {
            return Array.Empty<string>();
        }

        if (PersonalityFlagsCache.TryGetValue(rulesetCharacter.Guid, out var cachedFlags))
        {
            return cachedFlags;
        }

        var flags = Array.Empty<string>();

        if (rulesetCharacter is RulesetCharacterHero rulesetHero)
        {
            var collectedFlags = new List<string>();

            collectedFlags.AddRange(rulesetHero.BackgroundOptionalPersonalityFlags);
            collectedFlags.AddRange(rulesetHero.AlignmentOptionaPersonalityFlags);
            flags = collectedFlags.ToArray();
        }

        PersonalityFlagsCache[rulesetCharacter.Guid] = flags;

        return flags;
    }

    private static bool HasSpellcasting(RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter == null)
        {
            return false;
        }

        if (rulesetCharacter.CanCastSpells() || rulesetCharacter.SpellRepertoires.Count > 0)
        {
            return true;
        }

        if (rulesetCharacter is not RulesetCharacterMonster monster)
        {
            return false;
        }

        foreach (var feature in monster.MonsterDefinition.Features)
        {
            if (feature is FeatureDefinitionCastSpell)
            {
                return true;
            }
        }

        return false;
    }

    private static CombatAiCapabilityCatalog BuildCapabilityCatalog(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return new CombatAiCapabilityCatalog(
                false,
                false,
                false,
                0f,
                false,
                0,
                0f,
                false);
        }

        var attackModes = character.RulesetCharacter.AttackModes;
        var hasMelee = false;

        foreach (var mode in attackModes)
        {
            if (IsMeleeAttackModeForAi(mode))
            {
                hasMelee = true;
                break;
            }
        }

        var hasThrownRanged = false;
        var hasTrueRanged = false;
        var trueRangedMaximumRange = 0f;

        foreach (var mode in attackModes)
        {
            if (!IsRangedAttackMode(mode))
            {
                continue;
            }

            if (IsThrownLikeRangedAttackMode(mode, hasMelee))
            {
                hasThrownRanged = true;
            }
            else
            {
                hasTrueRanged = true;
                trueRangedMaximumRange = Math.Max(trueRangedMaximumRange, mode.MaxRange);
            }
        }

        var spellSummary = BuildAtWillHostileSpellSummary(character.RulesetCharacter);
        var hasAtWillHostileSpell = spellSummary.Count > 0;
        var hasFlight = !character.RulesetCharacter.IsTouchingGround() ||
                        character.RulesetCharacter.MoveModes.ContainsKey((int)MoveMode.Fly);

        return new CombatAiCapabilityCatalog(
            hasMelee,
            hasThrownRanged,
            hasTrueRanged,
            trueRangedMaximumRange,
            hasAtWillHostileSpell,
            spellSummary.Count,
            spellSummary.MaximumRange,
            hasFlight);
    }

    private static bool IsRangedAttackMode(RulesetAttackMode mode)
    {
        return mode is { Ranged: true } ||
               mode is { Thrown: true } ||
               mode?.SourceDefinition is MonsterAttackDefinition { proximity: AttackProximity.Range };
    }

    private static bool IsMeleeAttackModeForAi(RulesetAttackMode mode)
    {
        if (mode == null)
        {
            return false;
        }

        if (ValidatorsWeapon.IsMelee(mode))
        {
            return true;
        }

        if (mode.SourceDefinition is MonsterAttackDefinition { proximity: AttackProximity.Melee })
        {
            return true;
        }

        return !mode.Ranged &&
               !mode.Thrown &&
               mode.ReachRange > 0 &&
               mode.MaxRange <= Math.Max(mode.ReachRange, 1);
    }

    private static bool IsThrownLikeRangedAttackMode(RulesetAttackMode mode, bool actorHasMeleeMode)
    {
        if (mode is { Thrown: true })
        {
            return true;
        }

        return actorHasMeleeMode &&
               (mode?.Ranged == true ||
                mode?.SourceDefinition is MonsterAttackDefinition { proximity: AttackProximity.Range }) &&
               mode.MaxRange <= ThrownLikeRangedMaximumRange;
    }

    private static bool HasRangedAttackModes(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (IsRangedAttackMode(mode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOpportunityAttackRisk(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        IGameLocationBattleService battleService)
    {
        return WouldLeaveCurrentReactionMeleeReach(actor, start, destination, battleService);
    }

    private static bool WouldClearAllyCorridorTriggerOpportunityAttack(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null ||
            battleService == null ||
            actor.RulesetCharacter.HasConditionOfTypeOrSubType(
                DatabaseHelper.ConditionDefinitions.ConditionDisengaging.Name))
        {
            return false;
        }

        foreach (var enemy in GetKnownEnemyTargets(actor))
        {
            if (enemy?.RulesetCharacter == null ||
                enemy.GetActionTypeStatus(ActionType.Reaction) != ActionStatus.Available)
            {
                continue;
            }

            if (!enemy.CanPerformOpportunityAttackOnCharacter(
                    actor,
                    start,
                    destination,
                    out var attackMode,
                    out _,
                    true,
                    battleService))
            {
                continue;
            }

            var reach = attackMode?.ReachRange ?? 1;

            if (CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, destination, battleService))
            {
                continue;
            }

            var keepsTargetContact =
                target != null &&
                enemy == target &&
                ComputeGridDistance(destination, enemy.LocationPosition) <= MeleeSpacingTargetPressureDistance;

            if (keepsTargetContact)
            {
                continue;
            }

            if (battleService.IsWithinXCells(
                    enemy,
                    enemy.LocationPosition,
                    actor,
                    destination,
                    reach))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool WouldLeaveCurrentReactionMeleeReach(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null ||
            battleService == null ||
            actor.RulesetCharacter.HasConditionOfTypeOrSubType(
                DatabaseHelper.ConditionDefinitions.ConditionDisengaging.Name))
        {
            return false;
        }

        foreach (var enemy in GetKnownEnemyTargets(actor))
        {
            if (enemy?.RulesetCharacter == null ||
                enemy.GetActionTypeStatus(ActionType.Reaction) != ActionStatus.Available)
            {
                continue;
            }

            if (CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, start, battleService) &&
                !CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, destination, battleService))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasForcedRouteOpportunityExposure(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        if (HasOpportunityAttackRisk(actor, start, destination, battleService))
        {
            return true;
        }

        if (ComputeForcedMoveCost(start, destination) <= 1)
        {
            return false;
        }

        foreach (var enemy in GetKnownEnemyTargets(actor))
        {
            if (enemy?.RulesetCharacter == null ||
                enemy.GetActionTypeStatus(ActionType.Reaction) != ActionStatus.Available ||
                enemy.RulesetCharacter is RulesetCharacterEffectProxy)
            {
                continue;
            }

            if (CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, start, battleService) ||
                CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, destination, battleService))
            {
                continue;
            }

            foreach (var sample in EnumerateRouteExposureSamples(start, destination))
            {
                if (CanAttackInMeleeFromPosition(enemy, enemy.LocationPosition, actor, sample, battleService))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<int3> EnumerateRouteExposureSamples(int3 start, int3 destination)
    {
        var dx = destination.x - start.x;
        var dy = destination.y - start.y;
        var dz = destination.z - start.z;
        var steps = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));

        if (steps <= 1)
        {
            yield break;
        }

        for (var step = 1; step < steps; step++)
        {
            var t = (float)step / steps;

            yield return new int3(
                start.x + (int)Math.Round(dx * t),
                start.y + (int)Math.Round(dy * t),
                start.z + (int)Math.Round(dz * t));
        }
    }

    private static bool TryGetLocalMeleeThreat(
        GameLocationCharacter actor,
        int3 actorPosition,
        out GameLocationCharacter threat,
        out MeleeThreatSourceKind sourceKind)
    {
        threat = null;
        sourceKind = MeleeThreatSourceKind.None;

        if (actor?.RulesetCharacter == null || Gui.Battle == null)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            return false;
        }

        foreach (var candidate in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), actorPosition))
        {
            if (candidate?.RulesetCharacter == null ||
                candidate == actor ||
                candidate.Side == actor.Side)
            {
                continue;
            }

            if (!CanAttackInMeleeFromPosition(
                    candidate,
                    candidate.LocationPosition,
                    actor,
                    actorPosition,
                    battleService))
            {
                continue;
            }

            threat = candidate;
            sourceKind = MeleeThreatSourceKind.Local;

            return true;
        }

        return false;
    }

    private static bool TryGetCurrentOrRecentMeleeThreat(
        GameLocationCharacter actor,
        int3 actorPosition,
        out GameLocationCharacter threat,
        out int3 threatPosition,
        out MeleeThreatSourceKind sourceKind)
    {
        threatPosition = default;

        if (TryGetLocalMeleeThreat(actor, actorPosition, out threat, out sourceKind))
        {
            threatPosition = threat.LocationPosition;
            return true;
        }

        if (actorPosition == (actor?.LocationPosition ?? default) &&
            TryGetRecentMeleeThreat(actor, out var memory))
        {
            if (memory.IsEffectProxy &&
                EvaluateProxyMeleeThreatState(actor, memory) != ProxyThreatActivityState.Active)
            {
                threat = null;
                sourceKind = MeleeThreatSourceKind.RecentProxyInvalid;
                return false;
            }

            threat = memory.Source;
            threatPosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
            sourceKind = MeleeThreatSourceKind.Recent;
            return true;
        }

        threat = null;
        sourceKind = MeleeThreatSourceKind.None;

        return false;
    }

    private static bool TryGetRecentMeleeThreat(GameLocationCharacter actor, out RecentMeleeThreatMemory memory)
    {
        memory = default;

        if (actor?.RulesetCharacter == null ||
            !RecentMeleeThreatMemoryCache.TryGetValue(actor.Guid, out memory))
        {
            return false;
        }

        var source = memory.Source;
        var currentRound = GetCurrentBattleRound();
        var currentStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var expiredByRound =
            currentRound >= 0 &&
            memory.Round >= 0 &&
            currentRound - memory.Round > RecentMeleeThreatMaxRounds;
        var expiredByTurns =
            (currentRound < 0 || memory.Round < 0) &&
            currentStamp - memory.TurnStamp > RecentMeleeThreatMaxTurnStamps;

        if (source?.RulesetCharacter == null ||
            source.Side == actor.Side ||
            source.RulesetCharacter.IsDeadOrDyingOrUnconscious ||
            expiredByRound ||
            expiredByTurns)
        {
            RecentMeleeThreatMemoryCache.Remove(actor.Guid);
            memory = default;

            return false;
        }

        return true;
    }

    private static bool TryGetThreatAvoidance(GameLocationCharacter actor, out ThreatAvoidanceMemory memory)
    {
        memory = default;

        if (actor?.RulesetCharacter == null ||
            !ThreatAvoidanceMemoryCache.TryGetValue(actor.Guid, out memory))
        {
            return false;
        }

        var source = memory.Source;
        var currentRound = GetCurrentBattleRound();
        var currentStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var sourcePosition = source?.LocationPosition ?? memory.SourcePosition;
        var expiredByRound =
            currentRound >= 0 &&
            memory.Round >= 0 &&
            currentRound - memory.Round > ThreatAvoidanceMaxRounds;
        var expiredByTurns =
            (currentRound < 0 || memory.Round < 0) &&
            currentStamp - memory.TurnStamp > ThreatAvoidanceMaxTurnStamps;
        var sourceFarEnough =
            ComputeGridDistance(actor.LocationPosition, sourcePosition) > ThreatAvoidanceClearDistance &&
            ComputeGridDistance(memory.StartPosition, sourcePosition) > ThreatAvoidanceClearDistance &&
            (!memory.HasSafePosition ||
             ComputeGridDistance(memory.SafePosition, sourcePosition) > ThreatAvoidanceClearDistance);

        if (source?.RulesetCharacter == null ||
            source.Side == actor.Side ||
            source.RulesetCharacter.IsDeadOrDyingOrUnconscious ||
            expiredByRound ||
            expiredByTurns ||
            sourceFarEnough)
        {
            ThreatAvoidanceMemoryCache.Remove(actor.Guid);
            memory = default;

            return false;
        }

        return true;
    }

    private static bool UpdateThreatAvoidanceActualDestination(
        GameLocationCharacter actor,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 actualDestination)
    {
        if (actor?.RulesetCharacter == null ||
            !pendingAction.LockRemainingMovementAfterArrival)
        {
            return true;
        }

        if (!TryGetThreatAvoidance(actor, out var memory))
        {
            if (IsThreatRouteMovementLockGoal(pendingAction.MovementGoal))
            {

                return false;
            }

            return true;
        }

        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
        var beforeDistance = ComputeGridDistance(start, sourcePosition);
        var afterDistance = ComputeGridDistance(actualDestination, sourcePosition);
        var improvedDistance = afterDistance >= beforeDistance + ThreatAvoidanceActualDistanceGain;
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var actionConnected = HasThreatAvoidanceActionConnection(actor, pendingAction, actualDestination, battleService);
        var requiresActionConnection = pendingAction.MovementGoal != CombatAiMovementGoalKind.BreakThreat;
        var validSafePosition =
            improvedDistance &&
            (battleService == null ||
             !WouldBeInCurrentOrRecentMeleeThreat(actor, actualDestination, battleService)) &&
            (!requiresActionConnection || actionConnected);

        if (!validSafePosition)
        {

            return false;
        }

        ThreatAvoidanceMemoryCache[actor.Guid] = new ThreatAvoidanceMemory(
            memory.Source,
            sourcePosition,
            memory.IsEffectProxy,
            start,
            actualDestination,
            true,
            pendingAction.MovementGoal,
            memory.HandledThisTurn,
            memory.Round,
            memory.TurnStamp);

        return true;
    }

    private static bool HasThreatAvoidanceActionConnection(
        GameLocationCharacter actor,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        if (pendingAction.Target?.RulesetCharacter == null ||
            battleService == null)
        {
            return true;
        }

        if (pendingAction.ActionKind != CombatAiActionKind.None)
        {
            return CanUseActionKindAtPosition(
                actor,
                actualDestination,
                pendingAction.Target,
                pendingAction.ActionKind,
                battleService);
        }

        var profile = BuildProfile(actor);
        var turnPlan = BuildCombatAiTurnPlan(actor, profile, battleService);

        if (turnPlan.ActionProbe.Target?.RulesetCharacter == null)
        {
            return true;
        }

        return CanUseActionKindAtPosition(
                   actor,
                   actualDestination,
                   turnPlan.ActionProbe.Target,
                   turnPlan.ActionProbe.PreferredAction,
                   battleService) ||
               CanUseActionKindAtPosition(
                   actor,
                   actualDestination,
                   turnPlan.ActionProbe.Target,
                   turnPlan.ActionProbe.BackupAction,
                   battleService);
    }

    private static bool ShouldRejectThreatAvoidanceReturnMove(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        int3 candidatePosition)
    {
        if (!ShouldApplyThreatAvoidanceGate(profile, turnPlan) ||
            !TryGetThreatAvoidance(actor, out var memory))
        {
            return false;
        }

        var currentPosition = actor.LocationPosition;
        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
        var currentThreatDistance = ComputeGridDistance(currentPosition, sourcePosition);
        var candidateThreatDistance = ComputeGridDistance(candidatePosition, sourcePosition);

        if (candidateThreatDistance <= ThreatAvoidanceThreatZoneDistance)
        {
            return true;
        }

        if (candidateThreatDistance + 0.5f < currentThreatDistance)
        {
            return true;
        }

        if (ComputeGridDistance(candidatePosition, memory.StartPosition) <= ThreatAvoidanceReturnTolerance &&
            ComputeGridDistance(currentPosition, memory.StartPosition) > ThreatAvoidanceReturnTolerance)
        {
            return true;
        }

        if (memory.HasSafePosition &&
            ComputeGridDistance(currentPosition, memory.SafePosition) <= ThreatAvoidanceReturnTolerance &&
            ComputeGridDistance(candidatePosition, memory.StartPosition) <
            ComputeGridDistance(currentPosition, memory.StartPosition))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldRejectProxyThreatReturnMove(
        GameLocationCharacter actor,
        int3 startPosition,
        int3 targetPosition,
        out bool shouldSealMovement)
    {
        shouldSealMovement = false;

        if (actor?.RulesetCharacter == null ||
            targetPosition == startPosition ||
            !TryGetThreatAvoidance(actor, out var memory) ||
            !memory.IsEffectProxy)
        {
            return false;
        }

        var proxyMemory = new RecentMeleeThreatMemory(
            memory.Source,
            memory.SourcePosition,
            memory.IsEffectProxy,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        if (EvaluateProxyMeleeThreatState(actor, proxyMemory) == ProxyThreatActivityState.Clear)
        {
            return false;
        }

        var profile = BuildProfile(actor);

        if (!profile.PrefersDistance ||
            profile.Role is CombatAiRole.Melee or CombatAiRole.Hybrid)
        {
            return false;
        }

        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
        var currentThreatDistance = ComputeGridDistance(startPosition, sourcePosition);
        var targetThreatDistance = ComputeGridDistance(targetPosition, sourcePosition);
        var shouldReject =
            targetThreatDistance <= ThreatAvoidanceThreatZoneDistance ||
            targetThreatDistance + 0.5f < currentThreatDistance ||
            ComputeGridDistance(targetPosition, memory.StartPosition) <= ThreatAvoidanceReturnTolerance &&
            ComputeGridDistance(startPosition, memory.StartPosition) > ThreatAvoidanceReturnTolerance ||
            memory.HasSafePosition &&
            ComputeGridDistance(startPosition, memory.SafePosition) <= ThreatAvoidanceReturnTolerance &&
            ComputeGridDistance(targetPosition, sourcePosition) < ComputeGridDistance(startPosition, sourcePosition);

        if (!shouldReject)
        {
            return false;
        }

        var actionEconomy = BuildActionEconomySnapshot(actor);
        shouldSealMovement =
            actionEconomy.TacticalMove == ActionStatus.Available &&
            actor.RemainingTacticalMoves > 0;

        return true;
    }

    private static bool ApplyPostThreatReturnSeal(
        GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        if (actor.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        ClearPendingMoveOwnership(actor);

        return true;
    }

    private static bool ShouldApplyThreatAvoidanceGate(CombatAiProfile profile, CombatAiTurnPlan turnPlan)
    {
        if (!profile.PrefersDistance ||
            profile.Role is CombatAiRole.Melee or CombatAiRole.Hybrid)
        {
            return false;
        }

        return turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat ||
               turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MaintainThreatAvoidance ||
               IsImproveFiringPositionPlan(turnPlan) ||
               turnPlan.MovementPlan.Policy is CombatAiMovementPolicyKind.RangedLinePolicy or
                   CombatAiMovementPolicyKind.SpellLinePolicy or
                   CombatAiMovementPolicyKind.SearchKnownTargetPolicy;
    }

    private static bool WouldBeInCurrentOrRecentMeleeThreat(
        GameLocationCharacter actor,
        int3 actorPosition,
        IGameLocationBattleService battleService)
    {
        if (TryGetLocalMeleeThreat(actor, actorPosition, out _, out _))
        {
            return true;
        }

        if (!TryGetRecentMeleeThreat(actor, out var memory))
        {
            return false;
        }

        var source = memory.Source;
        var sourcePosition = source?.LocationPosition ?? memory.SourcePosition;

        if (source?.RulesetCharacter != null &&
            battleService != null &&
            CanAttackInMeleeFromPosition(source, sourcePosition, actor, actorPosition, battleService))
        {
            return true;
        }

        return ComputeGridDistance(actorPosition, sourcePosition) <= 1.5f;
    }

    private static bool ShouldRejectTrafficBlockingMove(
        GameLocationCharacter actor,
        int3 destination,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null ||
            destination == actor.LocationPosition)
        {
            return false;
        }

        var profile = BuildProfile(actor);

        var turnPlan = BuildCombatAiTurnPlan(actor, profile, battleService);

        return ShouldRejectTrafficBlockingMove(actor, destination, battleService, profile, turnPlan);
    }

    private static bool ShouldRejectTrafficBlockingMove(
        GameLocationCharacter actor,
        int3 destination,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan)
    {
        if (actor?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null ||
            destination == actor.LocationPosition)
        {
            return false;
        }

        var target = turnPlan.ActionProbe.Target ?? SelectPrimaryTarget(actor);
        var hasCurrentAction =
            turnPlan.ActionProbe.CanUsePreferredAction ||
            turnPlan.ActionProbe.CanUseBackupAction;
        var canEvaluateMeleeCorridor =
            IsGroundMeleePursuitPlan(turnPlan) ||
            turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing ||
            profile.IsMeleeSpecialist ||
            profile.PrefersAggressivePursuit;

        if (target?.RulesetCharacter == null ||
            !hasCurrentAction && !canEvaluateMeleeCorridor)
        {
            return false;
        }

        if (CanUseActionKindAtPosition(
                actor,
                destination,
                target,
                turnPlan.ActionProbe.PreferredAction,
                battleService))
        {
            return false;
        }

        if (ComputeGridDistance(destination, target.LocationPosition) > 2.25f &&
            !ShouldRejectAllyFireLaneGate(actor, destination, target, battleService))
        {
            return false;
        }

        if (ComputeGridDistance(destination, target.LocationPosition) > 2.25f)
        {
            return true;
        }

        foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), target.LocationPosition))
        {
            if (ally == actor ||
                ally?.RulesetCharacter == null ||
                ally.Side != actor.Side ||
                !IsLargeMeleeAlly(ally) ||
                CanAttackInMeleeFromPosition(ally, ally.LocationPosition, target, target.LocationPosition, battleService))
            {
                continue;
            }

            var allyDistanceToTarget = ComputeGridDistance(ally.LocationPosition, target.LocationPosition);

            if (allyDistanceToTarget > Math.Max(ally.RemainingTacticalMoves, ally.MaxTacticalMoves) + 2.0f)
            {
                continue;
            }

            if (ComputeDistanceToSegment2D(destination, ally.LocationPosition, target.LocationPosition) > 0.75f)
            {
                continue;
            }

            return true;
        }

        if (!ShouldRejectAllyFireLaneGate(actor, destination, target, battleService))
        {
            return false;
        }

        return true;
    }

    private static bool ShouldRejectAllyFireLaneGate(
        GameLocationCharacter actor,
        int3 destination,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null)
        {
            return false;
        }

        foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), target.LocationPosition))
        {
            if (ally == actor ||
                ally?.RulesetCharacter == null ||
                ally.Side != actor.Side ||
                ally.LocationPosition == destination ||
                ComputeGridDistance(ally.LocationPosition, target.LocationPosition) <= 1.5f)
            {
                continue;
            }

            var allyCapabilities = BuildCapabilityCatalog(ally);
            var allyHasCurrentShot =
                allyCapabilities.HasTrueRanged &&
                CanUseActionKindAtPosition(
                    ally,
                    ally.LocationPosition,
                    target,
                    CombatAiActionKind.Ranged,
                    battleService);

            var allyHasCurrentSpell =
                allyCapabilities.HasAtWillHostileSpell &&
                CanUseActionKindAtPosition(
                    ally,
                    ally.LocationPosition,
                    target,
                    CombatAiActionKind.Spell,
                    battleService);

            if (!allyHasCurrentShot && !allyHasCurrentSpell)
            {
                continue;
            }

            if (ComputeGridDistance(destination, target.LocationPosition) > 1.5f &&
                ComputeDistanceToSegment2D(destination, ally.LocationPosition, target.LocationPosition) > 0.75f)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static float ComputeDistanceToSegment2D(int3 point, int3 segmentStart, int3 segmentEnd)
    {
        var px = point.x;
        var pz = point.z;
        var ax = segmentStart.x;
        var az = segmentStart.z;
        var bx = segmentEnd.x;
        var bz = segmentEnd.z;
        var dx = bx - ax;
        var dz = bz - az;
        var lengthSquared = dx * dx + dz * dz;

        if (lengthSquared <= 0.001f)
        {
            var sx = px - ax;
            var sz = pz - az;

            return Mathf.Sqrt(sx * sx + sz * sz);
        }

        var t = Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / lengthSquared);
        var closestX = ax + t * dx;
        var closestZ = az + t * dz;
        var cx = px - closestX;
        var cz = pz - closestZ;

        return Mathf.Sqrt(cx * cx + cz * cz);
    }

    private static bool IsLargeMeleeAlly(GameLocationCharacter ally)
    {
        return ally?.RulesetCharacter?.SizeDefinition != null &&
               ally.RulesetCharacter.SizeDefinition.WieldingSize >=
               DatabaseHelper.CharacterSizeDefinitions.Medium.WieldingSize + 1 &&
               ally.RulesetCharacter.AttackModes.Any(ValidatorsWeapon.IsMelee);
    }

    private static bool IsBacktrackingMove(GameLocationCharacter actor, int3 start, int3 target)
    {
        if (actor == null ||
            !PendingAiMoveAttemptCache.TryGetValue(actor.Guid, out var pending))
        {
            return false;
        }

        return pending.Target == start && pending.Start == target;
    }

    private static bool TryGetSafeGroundMeleeWalkContactCost(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        out int walkCost)
    {
        walkCost = int.MaxValue;

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsGroundMeleePursuitPlan(turnPlan) ||
            !TryGetGroundMeleeTargetContactRouteQuery(character, turnPlan, out var query) ||
            !query.Complete ||
            query.IsCompleteApproach ||
            query.Map.ContactPositions == null)
        {
            return false;
        }

        var start = character.LocationPosition;
        var target = turnPlan.ActionProbe.Target;
        var remainingMove = Math.Max(0, character.RemainingTacticalMoves);

        foreach (var contactPosition in query.Map.ContactPositions)
        {
            var candidateMoveCost = query.GetMoveCost(contactPosition);

            if (candidateMoveCost > remainingMove ||
                !CanUseActionKindAtPosition(
                    character,
                    contactPosition,
                    target,
                    CombatAiActionKind.Melee,
                    battleService))
            {
                continue;
            }

            if (contactPosition != start &&
                (!IsLegalAiRouteDestination(character, contactPosition) ||
                 IsRejectedAiMoveTarget(character, start, contactPosition) ||
                 HasForcedRouteOpportunityExposure(character, start, contactPosition, battleService) ||
                 ShouldRejectTrafficBlockingMove(
                     character,
                     contactPosition,
                     battleService,
                     profile,
                     turnPlan)))
            {
                continue;
            }

            walkCost = Math.Min(walkCost, candidateMoveCost);
        }

        return walkCost < int.MaxValue;
    }

    private static bool TryFindJumpImmediateAttackCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        out int3 bestDestination)
    {
        bestDestination = default;

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0)
        {
            if (character != null)
            {
                JumpImmediateAttackReachableCache.Remove(character.Guid);
            }

            return false;
        }

        if (!CanUseFreeJumpForAi(character))
        {
            JumpImmediateAttackReachableCache.Remove(character.Guid);
            return false;
        }

        if (!IsGroundMeleePursuitPlan(turnPlan))
        {
            JumpImmediateAttackReachableCache.Remove(character.Guid);
            return false;
        }

        var start = character.LocationPosition;
        var target = turnPlan.ActionProbe.Target;
        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var selectedDestination = default(int3);
        var selectedScore = float.MinValue;
        var found = false;

        bool ConsiderDestination(int3 destination, float score)
        {
            var preferredAvailable = CanUseActionKindAtPosition(
                character,
                destination,
                target,
                CombatAiActionKind.Melee,
                battleService);
            var contactBandAvailable = IsGroundMeleeRouteAdjacentContact(destination, targetPosition);

            if (!preferredAvailable && !contactBandAvailable)
            {
                return true;
            }

            if (!IsLegalAiRouteDestination(character, destination))
            {
                return true;
            }

            if (ShouldRejectGroundMeleeRouteFailureCandidate(
                    character,
                    turnPlan,
                    start,
                    destination))
            {
                return true;
            }

            var actionScore =
                score +
                (preferredAvailable ? 1.50f : 1.00f) -
                (FreeJumpContext.ComputeAiFreeJumpMovementCost(start, destination) * 0.01f) +
                ComputeStableTieBreakScore(character, turnPlan, destination, CombatAiActionKind.Melee);

            if (!ShouldReplaceJumpImmediateAttackCandidate(
                    found,
                    actionScore,
                    selectedScore,
                    destination,
                    selectedDestination))
            {
                return true;
            }

            found = true;
            selectedScore = actionScore;
            selectedDestination = destination;
            return true;
        }

        TryFindAdjacentContactFreeJumpImmediateAttackCandidate(
            character,
            targetPosition,
            ConsiderDestination);

        if (!found)
        {
            FreeJumpContext.TryEnumerateImmediateAttackAiFreeJumpDestinations(
                character,
                ConsiderDestination);
        }

        if (!found)
        {
            JumpImmediateAttackReachableCache.Remove(character.Guid);
            return false;
        }

        bestDestination = selectedDestination;
        JumpImmediateAttackReachableCache.Add(character.Guid);
        return true;
    }

    private static bool ShouldReplaceJumpImmediateAttackCandidate(
        bool hasSelectedCandidate,
        float candidateScore,
        float selectedScore,
        int3 candidateDestination,
        int3 selectedDestination)
    {
        if (!hasSelectedCandidate)
        {
            return true;
        }

        if (candidateScore > selectedScore + CandidateScoreEpsilon)
        {
            return true;
        }

        if (candidateScore < selectedScore - CandidateScoreEpsilon)
        {
            return false;
        }

        return ComparePositionKey(candidateDestination, selectedDestination) < 0;
    }

    private static int ComparePositionKey(int3 lhs, int3 rhs)
    {
        var comparison = lhs.x.CompareTo(rhs.x);

        if (comparison != 0)
        {
            return comparison;
        }

        comparison = lhs.y.CompareTo(rhs.y);

        return comparison != 0 ? comparison : lhs.z.CompareTo(rhs.z);
    }

    private static bool TryFindAdjacentContactFreeJumpImmediateAttackCandidate(
        GameLocationCharacter character,
        int3 targetPosition,
        Func<int3, float, bool> considerCandidate)
    {
        if (considerCandidate == null)
        {
            return false;
        }

        return FreeJumpContext.TryEnumerateTargetContactAiFreeJumpDestinations(
            character,
            targetPosition,
            1,
            FreeJumpDefaultMinimumSuccessChance,
            (destination, _, score) => considerCandidate(destination, score));
    }

    private static void TryCompleteActionLinkedMove(
        GameLocationCharacter character,
        bool allowSettlingCompletion = false,
        bool allowSettledNoMoveFinalization = false)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var fromSettling = false;

        if (!TryGetLiveActionLinkedMoveForCompletion(character, out var pendingAction))
        {
            if (!allowSettlingCompletion ||
                !TryGetSettledActionLinkedMoveForCompletion(character, out pendingAction))
            {
                return;
            }

            fromSettling = true;
        }

        if (IsConnectedFiringLineRoute(pendingAction))
        {
            if (fromSettling &&
                character.LocationPosition == pendingAction.StartPosition &&
                allowSettledNoMoveFinalization)
            {
                CloseConnectedFiringLineMoveResult(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination);
                ActionLinkedMoveSettlingCache.Remove(character.Guid);
                return;
            }

            if (!TryCompleteConnectedFiringLineMovementStep(character, includeSettling: fromSettling).IsComplete)
            {
                DeferConnectedFiringLineMoveResult(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination);
            }

            return;
        }

        if (IsSearchKnownTargetRoute(pendingAction))
        {
            if (character.LocationPosition == pendingAction.StartPosition)
            {
                if (fromSettling && allowSettledNoMoveFinalization)
                {
                    CloseSearchKnownTargetMoveResult(
                        character,
                        pendingAction,
                        pendingAction.StartPosition,
                        pendingAction.ExpectedDestination,
                        SearchKnownTargetCompletionKind.FailedNoMeaningfulMovement);
                }

                return;
            }

            if (!TryCompleteSearchKnownTargetMovementStep(character, includeSettling: fromSettling).IsComplete)
            {
                DeferSearchKnownTargetMoveResult(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination);
            }

            return;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);

        if (!IsActiveBattleContender(character))
        {
            ClearTurnCache(character);
            return;
        }

        if (IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            if (TryDeferGroundMeleeMoveSettling(character, pendingAction))
            {
                return;
            }

            return;
        }

        if (character.LocationPosition != pendingAction.ExpectedDestination)
        {
            if (IsGroundMeleePursuitTerminalRoute(pendingAction))
            {
                if (fromSettling &&
                    character.LocationPosition == pendingAction.StartPosition &&
                    !allowSettledNoMoveFinalization)
                {
                    return;
                }

                if (!fromSettling && TryDeferGroundMeleeMoveSettling(character, pendingAction))
                {
                    return;
                }

                if (TryFinalizeGroundMeleePursuitAtActualDestination(
                        character,
                        pendingAction,
                        pendingAction.StartPosition,
                        pendingAction.ExpectedDestination))
                {
                    return;
                }

                return;
            }

            if (TryFinalizeRouteMoveAtActualDestination(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination))
            {
                return;
            }

            CloseLateActionLinkedMoveCompletion(
                character,
                pendingAction,
                pendingAction.StartPosition,
                pendingAction.ExpectedDestination);
            return;
        }

        if (IsGroundMeleePursuitTerminalRoute(pendingAction))
        {
            ClearGroundMeleeRouteFailureAfterProgress(character);
        }

        ApplyRouteMovementLockAfterArrival(character, pendingAction);

        if (pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ImmediateResidualAction)
        {
            InvalidateTurnPlanningCache(character);
            return;
        }

        if (pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision)
        {

            InvalidateTurnPlanningCache(character);
            return;
        }

        if (pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove)
        {

            InvalidateTurnPlanningCache(character);
            return;
        }

        InvalidateTurnPlanningCache(character);
    }

    private static void ApplyRouteMovementLockAfterArrival(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction)
    {
        if (!pendingAction.LockRemainingMovementAfterArrival)
        {
            return;
        }

        var actualDestination = character.LocationPosition;
        var actualSafe = IsRouteMovementLockActualDestinationValid(
            character,
            pendingAction,
            actualDestination);
        var partialThreatMove =
            !actualSafe &&
            CanApplyPartialThreatRouteMovementLock(
                pendingAction.MovementGoal,
                pendingAction.StartPosition,
                actualDestination);

        if (!actualSafe && !partialThreatMove)
        {
            PendingRouteMovementLockCache.Remove(character.Guid);
            return;
        }

        var safePositionUpdated = UpdateThreatAvoidanceActualDestination(
            character,
            pendingAction,
            pendingAction.StartPosition,
            actualDestination);
        PendingRouteMovementLockCache.Remove(character.Guid);
        ProxyThreatRouteAttemptCache.Remove(character.Guid);
        partialThreatMove =
            partialThreatMove ||
            (!safePositionUpdated &&
             CanApplyPartialThreatRouteMovementLock(
                 pendingAction.MovementGoal,
                 pendingAction.StartPosition,
                 actualDestination));

        if (!safePositionUpdated && !partialThreatMove)
        {
            return;
        }

        MarkRecentMeleeThreatHandledThisTurn(
            character,
            pendingAction,
            actualDestination,
            safePositionUpdated);
    }

    private static bool IsRouteMovementLockActualDestinationValid(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (pendingAction.MovementGoal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            return IsMeleeSpacingActualDestinationValid(
                character,
                pendingAction,
                actualDestination,
                battleService);
        }

        if (!IsThreatRouteMovementLockGoal(pendingAction.MovementGoal))
        {
            return true;
        }

        if (actualDestination == pendingAction.StartPosition)
        {
            return false;
        }

        if (battleService != null && WouldBeInCurrentOrRecentMeleeThreat(character, actualDestination, battleService))
        {
            return false;
        }

        if (!TryGetThreatAvoidance(character, out var avoidance))
        {
            return false;
        }

        var sourcePosition = avoidance.Source?.LocationPosition ?? avoidance.SourcePosition;
        var beforeDistance = ComputeGridDistance(avoidance.StartPosition, sourcePosition);
        var afterDistance = ComputeGridDistance(actualDestination, sourcePosition);

        if (afterDistance < beforeDistance + ThreatAvoidanceActualDistanceGain)
        {
            return false;
        }

        return true;
    }

    private static bool IsMeleeSpacingActualDestinationValid(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        IGameLocationBattleService battleService)
    {
        var target = pendingAction.Target;

        if (target?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        if (actualDestination == pendingAction.StartPosition)
        {
            return false;
        }

        var isFlyingSpacing = BuildProfile(character).HasFlight;
        var moveCost = ComputeForcedMoveCost(pendingAction.StartPosition, actualDestination);

        if (!isFlyingSpacing && moveCost > MeleeSpacingMaximumMoveCost)
        {
            return false;
        }

        if (isFlyingSpacing &&
            moveCost > Math.Max(character.MaxTacticalMoves, character.RemainingTacticalMoves))
        {
            return false;
        }

        if (!CanUseActionKindAtPosition(
                character,
                actualDestination,
                target,
                CombatAiActionKind.Melee,
                battleService))
        {
            return false;
        }

        if (WouldLeaveCurrentReactionMeleeReach(
                character,
                pendingAction.StartPosition,
                actualDestination,
                battleService))
        {
            return false;
        }

        if (ShouldRejectMeleeSpacingTrafficMove(character, actualDestination, target, battleService))
        {
            return false;
        }

        var crowdingBefore = EvaluateMeleeAllyCrowding(character, target, pendingAction.StartPosition);
        var crowdingAfter = EvaluateMeleeAllyCrowding(character, target, actualDestination);

        if (crowdingBefore.AdjacentCount <= 0)
        {
            return false;
        }

        if (crowdingAfter.AdjacentCount > 0 ||
            crowdingAfter.NearestHorizontalGap < MeleeSpacingRequiredGridGap)
        {
            return false;
        }

        return true;
    }

    private static bool TryFinalizeRouteMoveAtActualDestination(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination)
    {
        if (character?.RulesetCharacter == null ||
            !pendingAction.LockRemainingMovementAfterArrival)
        {
            return false;
        }

        var actualDestination = character.LocationPosition;

        if (actualDestination == start)
        {
            return TryRecoverNoMoveThreatRoute(
                character,
                pendingAction,
                start,
                expectedDestination);
        }

        var expectedMismatch = ComputeGridDistance(actualDestination, expectedDestination) > 2f;
        var partialThreatMove = CanApplyPartialThreatRouteMovementLock(
            pendingAction.MovementGoal,
            start,
            actualDestination);

        if (expectedMismatch && !partialThreatMove)
        {

            return false;
        }

        var actualSafe = IsRouteMovementLockActualDestinationValid(
            character,
            pendingAction,
            actualDestination);
        partialThreatMove = !actualSafe && partialThreatMove;

        if (!actualSafe && !partialThreatMove)
        {
            return false;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        var safePositionUpdated = UpdateThreatAvoidanceActualDestination(
            character,
            pendingAction,
            start,
            actualDestination);
        partialThreatMove =
            partialThreatMove ||
            (!safePositionUpdated &&
             CanApplyPartialThreatRouteMovementLock(pendingAction.MovementGoal, start, actualDestination));

        if (!safePositionUpdated && !partialThreatMove)
        {
            PendingRouteMovementLockCache.Remove(character.Guid);
            return false;
        }

        PendingRouteMovementLockCache.Remove(character.Guid);
        ProxyThreatRouteAttemptCache.Remove(character.Guid);
        MarkRecentMeleeThreatHandledThisTurn(
            character,
            pendingAction,
            actualDestination,
            safePositionUpdated);

        InvalidateTurnPlanningCache(character);

        return true;
    }

    private static bool TryFinalizeGroundMeleePursuitAtActualDestination(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 startPosition,
        int3 expectedDestination)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitTerminalRoute(pendingAction))
        {
            return false;
        }

        var actualDestination = character.LocationPosition;
        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (RouteMoveCompletionClosedCache.TryGetValue(character.Guid, out var closed) &&
            closed.Matches(
                pendingAction.MovementGoal,
                startPosition,
                expectedDestination,
                currentRound,
                currentTurnStamp))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            ActionLinkedMoveSettlingCache.Remove(character.Guid);
            PendingRouteMovementLockCache.Remove(character.Guid);
            return true;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        ActionLinkedMoveSettlingCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);

        if (actualDestination == startPosition)
        {
            RecordAiMoveFailure(character, startPosition, expectedDestination);
            RecordGroundMeleeRouteFailure(character, pendingAction.Target, startPosition, expectedDestination);
            TurnMovementProgressCache.Remove(character.Guid);
            CloseGroundMeleePursuitRouteCompletion(
                character,
                pendingAction,
                startPosition,
                expectedDestination,
                actualDestination,
                RouteMoveCompletionFlags.NoMove | RouteMoveCompletionFlags.GroundMeleeNoMove);

            return true;
        }

        if (!IsGroundMeleeActualDestinationRouteProgressValid(
                character,
                pendingAction,
                startPosition,
                expectedDestination,
                actualDestination))
        {
            RecordAiMoveFailure(character, startPosition, expectedDestination);
            RecordGroundMeleeRouteFailure(character, pendingAction.Target, startPosition, expectedDestination);
            TurnMovementProgressCache.Remove(character.Guid);
            CloseGroundMeleePursuitRouteCompletion(
                character,
                pendingAction,
                startPosition,
                expectedDestination,
                actualDestination,
                RouteMoveCompletionFlags.None);

            return true;
        }

        UpdateTurnMovementProgress(character);
        ClearGroundMeleeRouteFailureAfterProgress(character);
        CloseGroundMeleePursuitRouteCompletion(
            character,
            pendingAction,
            startPosition,
            expectedDestination,
            actualDestination,
            RouteMoveCompletionFlags.GroundMeleePartial);
        RecordGroundMeleePartialRouteProgress(
            character,
            pendingAction,
            actualDestination);

        return true;
    }

    private static bool IsGroundMeleePursuitTerminalRoute(ActionLinkedMoveMemory pendingAction)
    {
        return pendingAction.MovementGoal == CombatAiMovementGoalKind.AdvanceToMelee &&
               pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ReturnToCoordinatorAfterRouteMove &&
               pendingAction.RouteMoveSource is CombatAiRouteMoveSourceKind.Normal
                   or CombatAiRouteMoveSourceKind.BonusDash;
    }

    private static bool IsGroundMeleeJumpImmediateAttackRoute(ActionLinkedMoveMemory pendingAction)
    {
        return pendingAction.MovementGoal == CombatAiMovementGoalKind.AdvanceToMelee &&
               pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ImmediateResidualAction &&
               pendingAction.RouteMoveSource == CombatAiRouteMoveSourceKind.JumpImmediateAttack;
    }

    private static bool IsGroundMeleeMoveSettlingRoute(ActionLinkedMoveMemory pendingAction)
    {
        return IsGroundMeleePursuitTerminalRoute(pendingAction) ||
               IsGroundMeleeJumpImmediateAttackRoute(pendingAction);
    }

    private static void RecordGroundMeleePartialRouteProgress(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitTerminalRoute(pendingAction))
        {
            return;
        }

        GroundMeleePartialRouteCache[character.Guid] = new GroundMeleePartialRouteMemory(
            pendingAction,
            actualDestination,
            Math.Max(0, character.RemainingTacticalMoves),
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static void CloseGroundMeleePursuitRouteCompletion(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 startPosition,
        int3 expectedDestination,
        int3 actualDestination,
        RouteMoveCompletionFlags flags)
    {
        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            startPosition,
            expectedDestination,
            flags,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

    }

    private static void ApplyPendingRouteMovementState(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PendingRouteMovementLockCache.TryGetValue(character.Guid, out var pendingLock))
        {
            return;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (currentRound != pendingLock.Round ||
            currentTurnStamp != pendingLock.TurnStamp)
        {
            PendingRouteMovementLockCache.Remove(character.Guid);
            return;
        }

        if (pendingLock.MovementGoal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            PendingRouteMovementLockCache.Remove(character.Guid);
            return;
        }

        var actualDestination = character.LocationPosition;
        var isThreatRoute = IsThreatRouteMovementLockGoal(pendingLock.MovementGoal);
        var actualMoved = actualDestination != pendingLock.StartPosition;

        if (!actualMoved)
        {
            if (isThreatRoute)
            {
                PendingRouteMovementLockCache.Remove(character.Guid);

                return;
            }

            PendingRouteMovementLockCache.Remove(character.Guid);
            return;
        }

        if (!isThreatRoute &&
            ComputeGridDistance(actualDestination, pendingLock.ExpectedDestination) > 2f)
        {
            return;
        }

        PendingRouteMovementLockCache.Remove(character.Guid);
        ProxyThreatRouteAttemptCache.Remove(character.Guid);

        if (ThreatAvoidanceMemoryCache.TryGetValue(character.Guid, out var avoidance))
        {
            var sourcePosition = avoidance.Source?.LocationPosition ?? avoidance.SourcePosition;
            var beforeDistance = ComputeGridDistance(avoidance.StartPosition, sourcePosition);
            var afterDistance = ComputeGridDistance(actualDestination, sourcePosition);
            var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
            var validSafePosition =
                afterDistance >= beforeDistance + ThreatAvoidanceActualDistanceGain &&
                (battleService == null ||
                 !WouldBeInCurrentOrRecentMeleeThreat(character, actualDestination, battleService));
            ThreatAvoidanceMemoryCache[character.Guid] = new ThreatAvoidanceMemory(
                avoidance.Source,
                sourcePosition,
                avoidance.IsEffectProxy,
                avoidance.StartPosition,
                validSafePosition ? actualDestination : avoidance.SafePosition,
                validSafePosition || avoidance.HasSafePosition,
                pendingLock.MovementGoal,
                avoidance.HandledThisTurn,
                avoidance.Round,
                avoidance.TurnStamp);

        }
    }

    private static bool IsThreatRouteMovementLockGoal(CombatAiMovementGoalKind movementGoal)
    {
        return movementGoal is CombatAiMovementGoalKind.BreakThreat
            or CombatAiMovementGoalKind.MaintainThreatAvoidance;
    }

    private static bool TryRecoverNoMoveThreatRoute(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 startPosition,
        int3 failedDestination)
    {
        if (character?.RulesetCharacter == null ||
            !IsThreatRouteMovementLockGoal(pendingAction.MovementGoal))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (RouteMoveCompletionClosedCache.TryGetValue(character.Guid, out var closed) &&
            closed.Matches(pendingAction.MovementGoal, startPosition, failedDestination, currentRound, currentTurnStamp))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            PendingRouteMovementLockCache.Remove(character.Guid);
            return true;
        }

        RecordAiMoveFailure(character, startPosition, failedDestination);
        RecordProxyThreatRouteNoMove(character, pendingAction, startPosition, failedDestination);
        ActionLinkedMoveCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            startPosition,
            failedDestination,
            RouteMoveCompletionFlags.None,
            currentRound,
            currentTurnStamp);

        return true;
    }

    private static void RecordProxyThreatRouteNoMove(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 startPosition,
        int3 failedDestination)
    {
        if (character?.RulesetCharacter == null ||
            !TryGetProxyMeleeThreat(character, out var memory))
        {
            return;
        }

        var sourcePosition = memory.Source?.LocationPosition ?? memory.SourcePosition;
        var attempt = new ProxyThreatRouteAttemptMemory(
            memory.Source,
            sourcePosition,
            startPosition,
            failedDestination,
            pendingAction.RouteMoveSource,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            true);

        ProxyThreatRouteAttemptCache[character.Guid] = attempt;
        MarkProxyThreatAvoidancePending(character, pendingAction, memory, sourcePosition);
    }

    private static void MarkProxyThreatAvoidancePending(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        RecentMeleeThreatMemory memory,
        int3 sourcePosition)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        if (ThreatAvoidanceMemoryCache.TryGetValue(character.Guid, out var avoidance))
        {
            ThreatAvoidanceMemoryCache[character.Guid] = new ThreatAvoidanceMemory(
                avoidance.Source,
                sourcePosition,
                avoidance.IsEffectProxy,
                avoidance.StartPosition,
                avoidance.SafePosition,
                avoidance.HasSafePosition,
                pendingAction.MovementGoal,
                false,
                avoidance.Round,
                avoidance.TurnStamp);
        }
        else
        {
            ThreatAvoidanceMemoryCache[character.Guid] = new ThreatAvoidanceMemory(
                memory.Source,
                sourcePosition,
                memory.IsEffectProxy,
                pendingAction.StartPosition,
                default,
                false,
                pendingAction.MovementGoal,
                false,
                GetCurrentBattleRound(),
                Math.Max(1, ObservedCombatMemoryTurnStamp));
        }

    }

    private static bool CanApplyPartialThreatRouteMovementLock(
        CombatAiMovementGoalKind movementGoal,
        int3 startPosition,
        int3 actualDestination)
    {
        return IsThreatRouteMovementLockGoal(movementGoal) && actualDestination != startPosition;
    }

    private static bool TryExecuteResidualHostileAction(
        IGameLocationActionService actionService,
        CharacterActionParams actionParams)
    {
        if (actionService == null || actionParams == null)
        {
            return false;
        }

        actionService.ExecuteAction(
            actionParams,
            null,
            false);

        return true;
    }

    private static CombatAiResidualHostileActionResult TryUseResidualSafeHostileAction(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService)
    {
        return TryUseResidualSafeHostileAction(
            character,
            actionProbe,
            battleService,
            out _);
    }

    private static CombatAiResidualHostileActionResult TryUseResidualSafeHostileAction(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService,
        out bool policyHeld)
    {
        policyHeld = false;

        if (character?.RulesetCharacter == null || actionProbe.Target?.RulesetCharacter == null)
        {
            return new CombatAiResidualHostileActionResult(CombatAiResidualHostileActionResultKind.Unavailable);
        }

        if (ShouldPreferCurrentMeleeResidualAction(character, actionProbe, battleService) &&
            TryUseResidualSafeHostileAction(
                character,
                actionProbe.Target,
                CombatAiActionKind.Melee,
                battleService).Executed)
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Executed);
        }

        if (ShouldSuppressRangedBackupForMeleeRoute(character, actionProbe, battleService))
        {
            policyHeld = true;
            return new CombatAiResidualHostileActionResult(CombatAiResidualHostileActionResultKind.PolicyHeld);
        }

        var preferredFirst = actionProbe.CanUsePreferredAction
            ? actionProbe.PreferredAction
            : actionProbe.BackupAction;

        var preferredResult = TryUseResidualSafeHostileAction(
                character,
                actionProbe.Target,
                preferredFirst,
                battleService);

        if (preferredResult.Executed)
        {
            return preferredResult;
        }

        if (preferredFirst != actionProbe.BackupAction &&
            TryUseResidualSafeHostileAction(
                character,
                actionProbe.Target,
                actionProbe.BackupAction,
                battleService).Executed)
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Executed);
        }

        return preferredResult.IsBlocked
            ? preferredResult
            : new CombatAiResidualHostileActionResult(CombatAiResidualHostileActionResultKind.Unavailable);
    }

    private static bool ShouldSuppressRangedBackupForMeleeRoute(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            actionProbe.Target?.RulesetCharacter == null ||
            battleService == null ||
            actionProbe.PreferredAction != CombatAiActionKind.Melee ||
            actionProbe.BackupAction != CombatAiActionKind.Ranged ||
            !actionProbe.CanUseBackupAction)
        {
            return false;
        }

        var profile = BuildProfile(character);
        var capabilityCatalog = actionProbe.CapabilityCatalog;

        if (!ShouldPreferMeleeAction(profile, capabilityCatalog))
        {
            return false;
        }

        if (CanUseActionKindAtPosition(
                character,
                character.LocationPosition,
                actionProbe.Target,
                CombatAiActionKind.Melee,
                battleService))
        {
            return false;
        }

        if (HasReachedExecutedPreMainRouteDestination(character) &&
            CanUseActionKindAtPosition(
                character,
                character.LocationPosition,
                actionProbe.Target,
                CombatAiActionKind.Ranged,
                battleService))
        {
            return false;
        }

        var canContinueMoving = CanIssueAdditionalTacticalMove(character);

        if (ShouldReleaseRangedBackupAfterFailedMeleePursuit(
                character,
                actionProbe,
                battleService,
                canContinueMoving))
        {
            return false;
        }

        if (!canContinueMoving)
        {
            return false;
        }

        if (TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress) &&
            movementProgress.HasImmediatePreferredActionMoveCandidate)
        {
            return true;
        }

        if (HasImprovingMeleePursuit(character))
        {
            return true;
        }

        return JumpImmediateAttackReachableCache.Contains(character.Guid);
    }

    private static bool ShouldPreferCurrentMeleeResidualAction(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            actionProbe.Target?.RulesetCharacter == null ||
            battleService == null ||
            actionProbe.PreferredAction == CombatAiActionKind.Melee)
        {
            return false;
        }

        var profile = BuildProfile(character);
        var capabilityCatalog = actionProbe.CapabilityCatalog;

        return ShouldPreferMeleeAction(profile, capabilityCatalog) &&
               CanUseActionKindAtPosition(
                   character,
                   character.LocationPosition,
                   actionProbe.Target,
                   CombatAiActionKind.Melee,
                   battleService);
    }

    private static CombatAiResidualHostileActionResult TryUseResidualSafeHostileAction(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService)
    {
        if (!ValidateResidualMainAction(character, target, actionKind, battleService))
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Blocked);
        }

        return actionKind == CombatAiActionKind.Spell
            ? TryUseResidualHostileSpellAction(character, target, battleService)
            : TryUseResidualWeaponAttack(character, target, actionKind, battleService);
    }

    private static bool ValidateResidualMainAction(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (target?.RulesetCharacter == null || target.Side == character.Side)
        {
            return false;
        }

        var isWeaponAttack = actionKind is CombatAiActionKind.Melee or CombatAiActionKind.Ranged;

        if (TryGetCommittedNonTerminalMainActionThisTurn(
                character,
                out var committedMainAction) &&
            !HasUnspentMainActionRank(
                character,
                committedMainAction,
                character.GetActionTypeStatus(ActionType.Main)))
        {
            if (!isWeaponAttack ||
                committedMainAction.ActionId != Id.AttackMain ||
                !HasAvailableAttackMainContinuation(character))
            {
                return false;
            }
        }

        if (isWeaponAttack &&
            character.GetActionStatus(Id.AttackMain, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        if (!isWeaponAttack &&
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available)
        {
            return false;
        }

        if (battleService == null)
        {
            return false;
        }

        if (actionKind == CombatAiActionKind.None)
        {
            return false;
        }

        var canUseAtPosition = actionKind == CombatAiActionKind.Spell
            ? CanUseResidualHostileSpellAtPosition(
                character,
                character.LocationPosition,
                target,
                battleService)
            : CanUseActionKindAtPosition(character, character.LocationPosition, target, actionKind, battleService);

        if (!canUseAtPosition)
        {
            return false;
        }

        return true;
    }

    private static CombatAiResidualHostileActionResult TryUseResidualWeaponAttack(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService)
    {
        if (actionKind is not (CombatAiActionKind.Melee or CombatAiActionKind.Ranged))
        {
            return new CombatAiResidualHostileActionResult(CombatAiResidualHostileActionResultKind.Unavailable);
        }

        var attackMainStatus = character.GetActionStatus(Id.AttackMain, ActionScope.Battle);

        if (attackMainStatus != ActionStatus.Available)
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Blocked);
        }

        var actionService = ServiceRepository.GetService<IGameLocationActionService>();

        if (actionService == null)
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Blocked);
        }

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (mode == null ||
                (actionKind == CombatAiActionKind.Melee && !IsMeleeAttackModeForAi(mode)) ||
                (actionKind == CombatAiActionKind.Ranged && !IsRangedAttackMode(mode)) ||
                !IsAttackModeAvailableForMainAction(character, mode, out _))
            {
                continue;
            }

            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();

            if (actionKind == CombatAiActionKind.Melee)
            {
                attackParams.FillForPhysicalReachAttack(
                    character,
                    character.LocationPosition,
                    mode,
                    target,
                    target.LocationPosition,
                    modifier);
            }
            else
            {
                attackParams.FillForPhysicalRangeAttack(
                    character,
                    character.LocationPosition,
                    mode,
                    target,
                    target.LocationPosition,
                    modifier);
            }

            if (!battleService.CanAttack(attackParams))
            {
                continue;
            }

            MarkPendingResidualMainAction(character, Id.AttackMain);
            TryExecuteResidualHostileAction(
                actionService,
                new CharacterActionParams(character, Id.AttackMain, mode, target, modifier));

            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Executed);
        }

        return new CombatAiResidualHostileActionResult(
            CombatAiResidualHostileActionResultKind.Blocked);
    }

    private static bool CanUseResidualHostileSpellAtPosition(
        GameLocationCharacter character,
        int3 characterPosition,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        return TryGetAtWillSpellAttackModifierFromPosition(
                   character,
                   characterPosition,
                   target,
                   target.LocationPosition,
                   battleService,
                   out _) ||
               TryGetResidualHostileSpellFromPosition(
                   character,
                   characterPosition,
                   target,
                   battleService,
                   out _,
                   out _,
                   out _);
    }

    private static CombatAiResidualHostileActionResult TryUseResidualHostileSpellAction(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        var cantripResult = TryUseResidualCantripAttack(character, target, battleService);

        if (cantripResult.Executed)
        {
            return cantripResult;
        }

        var leveledResult = TryUseResidualLeveledHostileSpellAction(
            character,
            target,
            battleService,
            cantripResult);

        return leveledResult;
    }

    private static CombatAiResidualHostileActionResult TryUseResidualLeveledHostileSpellAction(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        CombatAiResidualHostileActionResult fallbackResult)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available ||
            !TryGetResidualHostileSpellFromPosition(
                character,
                character.LocationPosition,
                target,
                battleService,
                out var spell,
                out var spellRepertoire,
                out var modifier))
        {
            return fallbackResult.IsBlocked
                ? fallbackResult
                : new CombatAiResidualHostileActionResult(CombatAiResidualHostileActionResultKind.Unavailable);
        }

        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

        if (actionService == null || implementationService == null)
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Blocked);
        }

        var actionParams = new CharacterActionParams(character, Id.CastMain)
        {
            ActionModifiers = { modifier },
            IntParameter = spell.SpellLevel,
            StringParameter = spell.Name,
            RulesetEffect = implementationService.InstantiateEffectSpell(
                character.RulesetCharacter,
                spellRepertoire,
                spell,
                spell.SpellLevel,
                false),
            SpellRepertoire = spellRepertoire,
            TargetCharacters = { target }
        };

        MarkPendingResidualMainAction(character, Id.CastMain);
        TryExecuteResidualHostileAction(actionService, actionParams);

        return new CombatAiResidualHostileActionResult(
            CombatAiResidualHostileActionResultKind.Executed);
    }

    private static bool TryGetResidualHostileSpellFromPosition(
        GameLocationCharacter character,
        int3 characterPosition,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        out SpellDefinition spell,
        out RulesetSpellRepertoire spellRepertoire,
        out ActionModifier bestModifier)
    {
        spell = null;
        spellRepertoire = null;
        bestModifier = null;

        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            !CanUseMainSpellActionForAi(character))
        {
            return false;
        }

        var distance = ComputeGridDistance(characterPosition, target.LocationPosition);
        var seenSpells = new HashSet<string>(StringComparer.Ordinal);

        foreach (var repertoire in rulesetCharacter.SpellRepertoires)
        {
            if (repertoire == null)
            {
                continue;
            }

            if (TryGetResidualHostileSpellFromRepertoire(
                    character,
                    characterPosition,
                    target,
                    battleService,
                    repertoire,
                    distance,
                    seenSpells,
                    out spell,
                    out bestModifier))
            {
                spellRepertoire = repertoire;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetResidualHostileSpellFromRepertoire(
        GameLocationCharacter character,
        int3 characterPosition,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        RulesetSpellRepertoire spellRepertoire,
        float distance,
        ISet<string> seenSpells,
        out SpellDefinition spell,
        out ActionModifier bestModifier)
    {
        spell = null;
        bestModifier = null;

        return TryGetResidualHostileSpellFromSpellList(
                   character,
                   characterPosition,
                   target,
                   battleService,
                   spellRepertoire,
                   spellRepertoire.PreparedSpells,
                   distance,
                   seenSpells,
                   out spell,
                   out bestModifier) ||
               TryGetResidualHostileSpellFromSpellList(
                   character,
                   characterPosition,
                   target,
                   battleService,
                   spellRepertoire,
                   spellRepertoire.AutoPreparedSpells,
                   distance,
                   seenSpells,
                   out spell,
                   out bestModifier) ||
               TryGetResidualHostileSpellFromSpellList(
                   character,
                   characterPosition,
                   target,
                   battleService,
                   spellRepertoire,
                   spellRepertoire.KnownSpells,
                   distance,
                   seenSpells,
                   out spell,
                   out bestModifier);
    }

    private static bool TryGetResidualHostileSpellFromSpellList(
        GameLocationCharacter character,
        int3 characterPosition,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        RulesetSpellRepertoire spellRepertoire,
        IEnumerable<SpellDefinition> spells,
        float distance,
        ISet<string> seenSpells,
        out SpellDefinition spell,
        out ActionModifier bestModifier)
    {
        spell = null;
        bestModifier = null;

        if (spells == null)
        {
            return false;
        }

        foreach (var candidate in spells)
        {
            if (candidate == null ||
                string.IsNullOrEmpty(candidate.Name) ||
                seenSpells.Contains(candidate.Name) ||
                !IsResidualHostileSpellCandidate(character.RulesetCharacter, spellRepertoire, candidate, distance))
            {
                continue;
            }

            seenSpells.Add(candidate.Name);

            var effectDescription = PowerBundle.ModifySpellEffect(candidate, character.RulesetCharacter);

            if (effectDescription == null)
            {
                continue;
            }

            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();

            attackParams.FillForMagic(
                character,
                characterPosition,
                effectDescription,
                candidate.Name,
                target,
                target.LocationPosition,
                modifier);

            if (!battleService.CanAttack(attackParams))
            {
                continue;
            }

            spell = candidate;
            bestModifier = modifier;
            return true;
        }

        return false;
    }

    private static bool IsResidualHostileSpellCandidate(
        RulesetCharacter rulesetCharacter,
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spell,
        float distance)
    {
        if (rulesetCharacter == null ||
            spellRepertoire == null ||
            spell == null ||
            spell.SpellLevel <= 0 ||
            spell.ActivationTime != ActivationTime.Action ||
            !IsResidualHostileSpellReady(spellRepertoire, spell) ||
            !spellRepertoire.CanCastSpell(spell, false) ||
            !spellRepertoire.CanCastSpellOfLevel(spell.SpellLevel))
        {
            return false;
        }

        var effectDescription = PowerBundle.ModifySpellEffect(spell, rulesetCharacter);

        return effectDescription is
               {
                   TargetSide: Side.Enemy,
                   TargetType: TargetType.Individuals or TargetType.IndividualsUnique
               } &&
               (effectDescription.RangeParameter <= 0 ||
                distance <= effectDescription.RangeParameter + 0.5f);
    }

    private static bool IsResidualHostileSpellReady(
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spell)
    {
        if (spellRepertoire == null || spell == null)
        {
            return false;
        }

        if (spellRepertoire.IsSpellReady(spell) ||
            spellRepertoire.AutoPreparedSpells.Contains(spell))
        {
            return true;
        }

        return spellRepertoire.SpellCastingFeature?.SpellReadyness == SpellReadyness.Prepared
            ? spellRepertoire.PreparedSpells.Contains(spell)
            : spellRepertoire.KnownSpells.Contains(spell);
    }

    private static CombatAiResidualHostileActionResult TryUseResidualCantripAttack(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available ||
            !TryGetAtWillSpellAttackFromPosition(
                character,
                character.LocationPosition,
                target,
                target.LocationPosition,
                battleService,
                out var spell,
                out var spellRepertoire,
                out var modifier) ||
            spell == null ||
            spellRepertoire == null)
        {
            return new CombatAiResidualHostileActionResult(CombatAiResidualHostileActionResultKind.Unavailable);
        }

        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

        if (actionService == null || implementationService == null)
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Blocked);
        }

        var actionParams = new CharacterActionParams(character, Id.CastMain)
        {
            ActionModifiers = { modifier },
            IntParameter = spell.SpellLevel,
            StringParameter = spell.Name,
            RulesetEffect = implementationService.InstantiateEffectSpell(
                character.RulesetCharacter,
                spellRepertoire,
                spell,
                spell.SpellLevel,
                false),
            SpellRepertoire = spellRepertoire,
            TargetCharacters = { target }
        };

        MarkPendingResidualMainAction(character, Id.CastMain);
        TryExecuteResidualHostileAction(actionService, actionParams);

        return new CombatAiResidualHostileActionResult(
            CombatAiResidualHostileActionResultKind.Executed);
    }

    private static bool HasVisibleFlightContext(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        return HasVisibleFlightContext(actor, GetKnownEnemyTargets(actor), actor.LocationPosition);
    }

    private static bool HasVisibleFlightContext(
        GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> enemies,
        int3 actorPosition)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        if (!actor.RulesetCharacter.IsTouchingGround())
        {
            return true;
        }

        foreach (var enemy in enemies)
        {
            if (enemy?.RulesetCharacter == null)
            {
                continue;
            }

            if (HasFlightContext(actor, enemy, actorPosition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFlightContext(
        GameLocationCharacter actor,
        GameLocationCharacter enemy,
        int3 actorPosition)
    {
        return actor?.RulesetCharacter != null &&
               enemy?.RulesetCharacter != null &&
               (!actor.RulesetCharacter.IsTouchingGround() ||
                !enemy.RulesetCharacter.IsTouchingGround() ||
                enemy.LocationPosition.y > actorPosition.y);
    }
}
