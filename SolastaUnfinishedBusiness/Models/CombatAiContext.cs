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

internal enum CombatAiMainActionBlockKind
{
    None,
    Other,
    NoActor,
    NoHostileTarget,
    NoBattleService,
    NoActionKind,
    MainAlreadySpent,
    MainUnavailable,
    AttackMainUnavailable,
    ActionNotUsableAtPosition,
    RangedWhileMeleeReachable,
    RangedWhileMeleePursuitAvailable,
    LowValueUtility,
    UtilityBlocked,
    RedundantUtility,
    DashDisconnectedPositioning
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

internal enum CombatAiMovementAvailability
{
    None,
    Available,
    Blocked,
    Spent
}

internal enum FreeJumpFallbackAvailability
{
    Unavailable,
    Available,
    Disabled
}

internal enum MissedMovementKind
{
    None,
    UnresolvedGoal,
    MissedMeaningfulMove
}

internal enum TerminalFallbackBlockKind
{
    None,
    HostileOrUtility,
    PolicyHeld
}

internal enum TerminalReprobeStatus
{
    Blocked,
    Executed,
    PolicyHeld
}

internal enum TerminalFallbackActionStatus
{
    Blocked,
    Executed,
    Scheduled
}

internal enum PostRouteTerminalStatus
{
    Blocked,
    Executed
}

internal enum PendingTerminalLaunchKind
{
    Ready,
    Dodge
}

internal enum TerminalDodgeCompletionKind
{
    NoPendingCondition,
    NoCondition,
    EngineRejected,
    Applied
}

internal enum TerminalReadyCompletionKind
{
    EngineRejected,
    Applied
}

internal enum ProxyThreatRouteMoveStatus
{
    Unavailable,
    Executed
}

internal enum ClearAllyCorridorCandidateBlockKind
{
    None,
    NoMovement,
    Placement,
    Retry,
    MoveCost,
    Opportunity,
    StillBlocks,
    Pressure,
    Traffic
}

internal enum CombatAiResidualHostileActionResultKind
{
    Unavailable,
    Blocked,
    Executed,
    PolicyHeld
}

internal enum RepeatedMeleeAlternativeResultKind
{
    None,
    Blocked,
    Executed
}

internal enum GroundMeleeJumpShortcutBlockKind
{
    None,
    BasicGate,
    FreeJumpUnavailable,
    AlreadyAtGoal,
    NoTargetContactQuery,
    NoIndependentShortcut,
    NoImprovingShortcut,
    ExecuteFailed
}

internal enum BreakThreatConnectionBlockKind
{
    None,
    NotBreakThreat,
    MissingActorOrBattleService,
    NearStart,
    ThreatRange,
    NoTarget,
    NoPostMoveAttack
}

internal enum BreakThreatDefensiveFallbackBlockKind
{
    None,
    NotBreakThreat,
    Policy,
    NearStart,
    NoThreat,
    ThreatRange,
    InsufficientGain
}

internal enum RouteActualDestinationValidationKind
{
    Valid,
    MissingActor,
    NoMovement,
    ThreatZone,
    NoAvoidanceMemory,
    InsufficientThreatDistanceGain,
    MissingTargetOrBattleService,
    SpacingMovedTooFar,
    FlyingSpacingMovedTooFar,
    NoMeleePressure,
    OpportunityExposure,
    Traffic,
    NoAdjacentAllyBefore,
    InsufficientHorizontalGap
}

internal enum AiRouteDestinationLegality
{
    Legal,
    MissingActor,
    NoProgress,
    Occupied,
    CannotPlace
}

internal enum MeleeThreatSourceKind
{
    None,
    Local,
    Recent,
    RecentProxyInvalid
}

internal enum TrafficBlockKind
{
    None,
    AllyFireLane,
    LargeAllyCorridor
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

internal enum CombatAiActionPathKind
{
    NoAction,
    AttackNow,
    AttackAfterMove,
    PursueForNextRound,
    BreakThreat,
    Ready,
    Dodge
}

internal enum CombatAiActionPathAvailability
{
    None,
    Available,
    Executed,
    Blocked,
    Spent
}

internal enum CombatAiActionLinkedMoveContinuation
{
    ImmediateResidualAction,
    ReturnToVanillaDecision,
    TerminalAfterRouteMove
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
    JumpImprovement,
    JumpFallback,
    GroundMeleeShortcut,
    GroundMeleeUnresolved,
    JumpImmediateAttack,
    ConnectedFiringLine,
    ConnectedFiringLineRecovery,
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
    private const string FlightSuspendedConditionName = "ConditionFlightSuspended";
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
    private const float MovementGoalProgressScore = 0.74f;
    private const float MovementGoalMeleeProgressScore = 0.82f;
    private const float MovementGoalRangedLineProgressScore = 1.00f;
    private const float MovementGoalSpellLineProgressScore = 1.08f;
    private const float MovementGoalPreferredRangeScore = 1.60f;
    private const float MovementGoalSearchScore = 0.70f;
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
    private const int ProxyThreatRecoveryCandidateLimit = 12;
    private const int GroundMeleePursuitCandidateLimit = 8;
    private const int GroundMeleeRouteProjectionScoreTurnCap = 8;
    private const int GroundMeleeTargetContactRouteBudgetMultiplier = 8;
    private const int GroundMeleeTargetContactRouteMaxBudget = 48;
    private const int GroundMeleeRoutePathfindingPerTurnLimit = 16;
    private const int GroundMeleeTargetContactRouteSeedLimit = 32;
    private const int GroundMeleeTargetContactRouteProofLimit = 10;
    private const int GroundMeleeTargetContactReverseGoalLimit = 3;
    private const int MoveResultSettlingFrameLimit = 3;
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
    private static readonly Dictionary<ulong, ActionLinkedMoveMemory> ActionLinkedMoveCache = [];
    private static readonly Dictionary<ulong, RouteMoveCompletionClosedMemory> RouteMoveCompletionClosedCache = [];
    private static readonly Dictionary<ulong, PendingRouteActionOnlyTerminalMemory> PendingRouteActionOnlyTerminalCache = [];
    private static readonly Dictionary<ulong, string> ConnectedFiringLineRecoveryAttemptCache = [];
    private static readonly Dictionary<ulong, string> LostTargetSearchAttemptCache = [];
    private static readonly Dictionary<ulong, PendingRouteMovementLockMemory> PendingRouteMovementLockCache = [];
    private static readonly Dictionary<ulong, ThreatRouteRecoveryMemory> ThreatRouteRecoveryCache = [];
    private static readonly Dictionary<ulong, PreMainRouteMoveAttempt> PreMainRouteMoveAttemptCache = [];
    private static readonly Dictionary<ulong, DisconnectedPositioningSealMemory> DisconnectedPositioningSealCache = [];
    private static readonly Dictionary<ulong, string> DisconnectedPositioningMovementLockCache = [];
    private static readonly Dictionary<ulong, HashSet<ulong>> PendingFallbackDodgeConditionCache = [];
    private static readonly Dictionary<ulong, PendingTerminalDodgeEndTurnMemory> PendingTerminalDodgeEndTurnCache = [];
    private static readonly Dictionary<ulong, PendingTerminalDodgeEndTurnMemory> PendingTerminalReadyEndTurnCache = [];
    private static readonly Dictionary<ulong, PendingAiProcessTerminalLaunchMemory> PendingAiProcessTerminalLaunchCache = [];
    private static readonly HashSet<ulong> PendingAiProcessTerminalLaunchAcceptedCache = [];
    private static readonly Dictionary<ulong, PendingAiProcessTurnRecoveryMemory> PendingAiProcessTurnRecoveryCache = [];
    private static readonly Dictionary<ulong, PendingTerminalDodgeEndTurnMemory> AiProcessTurnRecoveryConsumedCache = [];
    private static readonly Dictionary<ulong, PendingTerminalDodgeEndTurnMemory>
        PostRecoveryEndTurnMainActionSealCache = [];
    private static readonly Dictionary<ulong, PendingTerminalDodgeEndTurnMemory>
        PostRecoveryMainActionNormalizationCache = [];
    private static readonly HashSet<ulong> PendingTerminalActionEndTurnSuppressCache = [];
    private static readonly Dictionary<ulong, FallbackDodgeConditionMemory> FallbackDodgeConditionCache = [];
    private static readonly Dictionary<ulong, RepeatedEndStateMemory> RepeatTerminalActionCache = [];
    private static readonly Dictionary<ulong, RepeatedAttackActionMemory> RepeatAttackActionCache = [];
    private static readonly Dictionary<ulong, AiTurnMovementProgress> TurnMovementProgressCache = [];
    private static readonly Dictionary<ulong, CombatAiActionExecutionMemory> LastActionExecutionCache = [];
    private static readonly Dictionary<ulong, CombatAiActionExecutionMemory> LastMainActionExecutionCache = [];
    private static readonly Dictionary<ulong, int> TurnMainActionUseCountCache = [];
    private static readonly Dictionary<ulong, int> TurnBonusActionUseCountCache = [];
    private static readonly Dictionary<ulong, CombatAiActionEconomySnapshot> TurnStartActionEconomyCache = [];
    private static readonly Dictionary<ulong, RecentMeleeThreatMemory> RecentMeleeThreatMemoryCache = [];
    private static readonly Dictionary<ulong, ThreatAvoidanceMemory> ThreatAvoidanceMemoryCache = [];
    private static readonly Dictionary<ulong, TacticalSituationMemory> TacticalSituationMemoryCache = [];
    private static readonly Dictionary<ulong, PendingResidualMainAction> PendingResidualMainActionCache = [];
    private static readonly Dictionary<ulong, PendingUtilityTerminalContinuation> PendingUtilityTerminalContinuationCache = [];
    private static readonly Dictionary<ulong, UbResidualMainAttackCommitMemory> UbResidualMainAttackCommitCache = [];
    private static readonly Dictionary<ulong, RouteMoveDashBlockMemory> RouteMoveDashBlockCache = [];
    private static readonly Dictionary<ulong, BaselineFreeJumpAttemptMemory> BaselineFreeJumpAttemptCache = [];
    private static readonly Dictionary<AttackPositionKey, bool> MeleeAttackPositionCache = [];
    private static readonly Dictionary<AttackPositionKey, bool> SpellAttackPositionCache = [];
    private static readonly Dictionary<string, bool> ActionKindPositionCache = [];
    private static readonly Dictionary<string, CoverEvaluationMemory> CoverEvaluationCache = [];
    private static readonly HashSet<ulong> JumpImmediateAttackReachableCache = [];
    private static readonly HashSet<ulong> GroundMeleeJumpRouteAvailableCache = [];
    private static readonly Dictionary<ulong, CurrentStateRouteBlockKind> CurrentStateRouteBlockCache = [];
    private static readonly Dictionary<ulong, GroundMeleeTargetContactRouteQuery> TargetContactRouteQueryCache = [];
    private static readonly Dictionary<ulong, GroundMeleeRouteIntentMemory> GroundMeleeRouteIntentCache = [];
    private static readonly Dictionary<ulong, GroundMeleeRouteFailureMemory> GroundMeleeRouteFailureCache = [];
    private static readonly Dictionary<ulong, GroundMeleeDetourCandidateMemory> GroundMeleeDetourCandidateCache = [];
    private static readonly Dictionary<ulong, GroundMeleeMoveSettlingMemory> GroundMeleeMoveSettlingCache = [];
    private static readonly Dictionary<ulong, GroundMeleePartialRouteMemory> GroundMeleePartialRouteCache = [];
    private static readonly Dictionary<ulong, TerminalSealMemory> GroundMeleeNoMoveTerminalSealCache = [];
    private static readonly Dictionary<ulong, ProxyThreatRouteAttemptMemory> ProxyThreatRouteAttemptCache = [];
    private static readonly Dictionary<string, ReachableRouteDestinationMemory> ReachableRouteDestinationCache = [];
    private static readonly Dictionary<string, int> ReachableRoutePathfindingCountCache = [];
    private static object CurrentBattleCacheKey;
    private static int CurrentBattleCacheRound = -1;
    private static bool? CurrentAdvancedCombatAiSetting;
    private static bool? CurrentBonusActionFreeJumpSetting;
    private static int ObservedCombatMemoryTurnStamp;

    private enum CurrentTerminalActionAvailability
    {
        None,
        Candidate,
        Validated
    }

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

    private enum AiMoveRejectionKind
    {
        None,
        Accepted,
        LongRoute,
        Opportunity,
        Traffic,
        Regression,
        NoProgress
    }

    private enum ImproveFiringCandidateState
    {
        None,
        Accepted,
        Blocked
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
        bool hasHostileCandidate,
        bool hasUsefulUtility)
    {
        internal bool HasValidatedAction { get; } = hasValidatedAction;
        internal bool HasHostileCandidate { get; } = hasHostileCandidate;
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

    private readonly struct TerminalReprobeResult(
        TerminalReprobeStatus status,
        CombatAiActionKind actionKind = CombatAiActionKind.None)
    {
        internal TerminalReprobeStatus Status { get; } = status;
        internal CombatAiActionKind ActionKind { get; } = actionKind;
        internal bool Executed => Status == TerminalReprobeStatus.Executed;
        internal bool PolicyHeld => Status == TerminalReprobeStatus.PolicyHeld;
    }

    private readonly struct TerminalFallbackActionResult(
        TerminalFallbackActionStatus status)
    {
        internal TerminalFallbackActionStatus Status { get; } = status;
        internal bool Executed => Status is TerminalFallbackActionStatus.Executed or TerminalFallbackActionStatus.Scheduled;
    }

    private readonly struct PostRouteTerminalResult(
        PostRouteTerminalStatus status,
        CombatAiExecutedActionKind actionKind = CombatAiExecutedActionKind.None)
    {
        internal PostRouteTerminalStatus Status { get; } = status;
        internal CombatAiExecutedActionKind ActionKind { get; } = actionKind;
        internal bool Executed => Status == PostRouteTerminalStatus.Executed;
    }

    private readonly struct ProxyThreatRouteMoveResult(
        ProxyThreatRouteMoveStatus status,
        int3 destination = default)
    {
        internal ProxyThreatRouteMoveStatus Status { get; } = status;
        internal int3 Destination { get; } = destination;
        internal bool Executed => Status == ProxyThreatRouteMoveStatus.Executed;
    }

    private readonly struct ClearAllyCorridorCandidateResult(
        bool accepted,
        float score = 0f,
        bool hasTrafficSoftPenalty = false,
        ClearAllyCorridorCandidateBlockKind blockKind = ClearAllyCorridorCandidateBlockKind.None)
    {
        internal bool Accepted { get; } = accepted;
        internal float Score { get; } = score;
        internal bool HasTrafficSoftPenalty { get; } = hasTrafficSoftPenalty;
        internal ClearAllyCorridorCandidateBlockKind BlockKind { get; } = blockKind;
    }

    private readonly struct PendingResidualMainAction(
        Id actionId,
        string source,
        int round,
        int turnStamp)
    {
        internal Id ActionId { get; } = actionId;
        internal string Source { get; } = string.IsNullOrEmpty(source) ? "ub" : source;
        private int Round { get; } = round;
        private int TurnStamp { get; } = turnStamp;

        internal bool Matches(Id candidateActionId, int round, int turnStamp)
        {
            return ActionId == candidateActionId && Round == round && TurnStamp == turnStamp;
        }
    }

    private readonly struct UbResidualMainAttackCommitMemory(
        string source,
        int round,
        int turnStamp)
    {
        internal string Source { get; } = string.IsNullOrEmpty(source) ? "ub residual" : source;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }
    }

    private readonly struct PendingUtilityTerminalContinuation(
        Id actionId,
        int round,
        int turnStamp)
    {
        internal Id ActionId { get; } = actionId;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct RouteMoveDashBlockMemory(
        CombatAiMovementGoalKind movementGoal,
        int3 destination,
        int round,
        int turnStamp)
    {
        internal CombatAiMovementGoalKind MovementGoal { get; } = movementGoal;
        internal int3 Destination { get; } = destination;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
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

    private readonly struct UtilityActionValidation(
        bool isBlocked,
        CombatAiMainActionBlockKind blockKind = CombatAiMainActionBlockKind.None)
    {
        internal bool IsBlocked { get; } = isBlocked;
        internal CombatAiMainActionBlockKind BlockKind { get; } = blockKind;
    }

    private sealed class AiTurnMovementProgress
    {
        private readonly HashSet<string> visitedPositions;
        private readonly List<AiAcceptedMoveCandidate> acceptedMoveCandidates = [];
        private readonly List<AiAcceptedMoveCandidate> longRoutePursuitCandidates = [];

        internal AiTurnMovementProgress(int3 startPosition, float bestDistanceToGoal)
        {
            visitedPositions = [GetPositionKey(startPosition)];
            StartPosition = startPosition;
            BestDistanceToGoal = bestDistanceToGoal;
            BestAcceptedPosition = startPosition;
            BestPreferredActionPosition = startPosition;
        }

        internal int3 StartPosition { get; }
        internal float BestDistanceToGoal { get; private set; }
        internal bool HadMeaningfulMovementCandidate { get; private set; }
        internal bool HadPreferredActionMovementCandidate { get; private set; }
        internal bool HadFreeJumpMovementCandidate { get; private set; }
        private int AcceptedMoveGateCount { get; set; }
        private int OpportunityRejectedCount { get; set; }
        private int TrafficRejectedCount { get; set; }
        private int RegressionRejectedCount { get; set; }
        private int NoProgressRejectedCount { get; set; }
        private int ImproveAttackCapableCandidates { get; set; }
        private int ImproveSafeCandidates { get; set; }
        private int ImproveStrictImprovementCandidates { get; set; }
        private int ImproveDifferentSafeCellCandidates { get; set; }
        private int ImproveFreeJumpCandidates { get; set; }
        private int3 BestAcceptedPosition { get; set; }
        private float BestAcceptedScore { get; set; }
        private float BestAcceptedProgress { get; set; }
        private int BestAcceptedTurnsToAction { get; set; } = -1;
        private int3 BestPreferredActionPosition { get; set; }
        private float BestPreferredActionScore { get; set; }
        private float BestPreferredActionProgress { get; set; }
        private int BestPreferredActionTurnsToAction { get; set; } = -1;
        private ImproveFiringCandidateState ImproveFiringState { get; set; }
        internal bool HasAcceptedMoveCandidate => AcceptedMoveGateCount > 0;
        internal bool HasLongRoutePursuitCandidate => longRoutePursuitCandidates.Count > 0;
        internal bool HasImmediatePreferredActionMoveCandidate =>
            HadPreferredActionMovementCandidate && BestPreferredActionTurnsToAction == 0;
        internal int3 BestMoveCandidatePosition => BestAcceptedPosition;
        internal int3 BestLongRoutePursuitCandidatePosition =>
            HasLongRoutePursuitCandidate ? longRoutePursuitCandidates[0].Position : StartPosition;
        internal int BestMoveCandidateTurnsToAction => BestAcceptedTurnsToAction;
        internal int BestPreferredActionMoveTurnsToAction => BestPreferredActionTurnsToAction;
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

        internal bool TryGetBestLongRoutePursuitCandidate(out AiAcceptedMoveCandidate candidate)
        {
            candidate = default;

            if (!HasLongRoutePursuitCandidate)
            {
                return false;
            }

            candidate = longRoutePursuitCandidates[0];
            return true;
        }

        internal bool HasVisited(int3 position)
        {
            return visitedPositions.Contains(GetPositionKey(position));
        }

        internal void MarkVisited(int3 position, float distanceToGoal)
        {
            visitedPositions.Add(GetPositionKey(position));
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
            AcceptedMoveGateCount++;
            acceptedMoveCandidates.Add(new AiAcceptedMoveCandidate(position, score, progress, turnsToAction));

            if (preferredActionCandidate && score > BestPreferredActionScore + 0.000001f)
            {
                BestPreferredActionPosition = position;
                BestPreferredActionScore = score;
                BestPreferredActionProgress = progress;
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
            var seenPositions = new HashSet<string>();

            foreach (var candidate in acceptedMoveCandidates
                         .OrderByDescending(candidate => candidate.Score)
                         .ThenBy(candidate => candidate.Position.x)
                         .ThenBy(candidate => candidate.Position.y)
                         .ThenBy(candidate => candidate.Position.z))
            {
                if (!seenPositions.Add(GetPositionKey(candidate.Position)))
                {
                    continue;
                }

                yield return candidate.Position;
            }
        }

        internal IEnumerable<AiAcceptedMoveCandidate> EnumerateAcceptedMoveCandidates()
        {
            var seenPositions = new HashSet<string>();

            foreach (var candidate in acceptedMoveCandidates
                         .OrderByDescending(candidate => candidate.Score)
                         .ThenBy(candidate => candidate.TurnsToAction)
                         .ThenBy(candidate => candidate.Position.x)
                         .ThenBy(candidate => candidate.Position.y)
                         .ThenBy(candidate => candidate.Position.z))
            {
                if (!seenPositions.Add(GetPositionKey(candidate.Position)))
                {
                    continue;
                }

                yield return candidate;
            }
        }

        internal void RecordLongRoutePursuitCandidate(
            int3 position,
            float score,
            float progress,
            int turnsToAction)
        {
            if (longRoutePursuitCandidates.Any(candidate => candidate.Position == position))
            {
                return;
            }

            HadMeaningfulMovementCandidate = true;
            longRoutePursuitCandidates.Add(new AiAcceptedMoveCandidate(position, score, progress, turnsToAction));
            longRoutePursuitCandidates.Sort(CompareAcceptedMoveCandidates);

            if (longRoutePursuitCandidates.Count > GroundMeleePursuitCandidateLimit)
            {
                longRoutePursuitCandidates.RemoveRange(
                    GroundMeleePursuitCandidateLimit,
                    longRoutePursuitCandidates.Count - GroundMeleePursuitCandidateLimit);
            }
        }

        internal IEnumerable<int3> EnumerateLongRoutePursuitCandidatePositions()
        {
            foreach (var candidate in longRoutePursuitCandidates)
            {
                yield return candidate.Position;
            }
        }

        internal IEnumerable<AiAcceptedMoveCandidate> EnumerateLongRoutePursuitCandidates()
        {
            foreach (var candidate in longRoutePursuitCandidates)
            {
                yield return candidate;
            }
        }

        private static int CompareAcceptedMoveCandidates(AiAcceptedMoveCandidate left, AiAcceptedMoveCandidate right)
        {
            var comparison = right.Score.CompareTo(left.Score);

            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.TurnsToAction.CompareTo(right.TurnsToAction);

            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Position.x.CompareTo(right.Position.x);

            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Position.y.CompareTo(right.Position.y);

            return comparison != 0
                ? comparison
                : left.Position.z.CompareTo(right.Position.z);
        }

        internal void RecordRejected(AiMoveRejectionKind rejection)
        {
            switch (rejection)
            {
                case AiMoveRejectionKind.Opportunity:
                    OpportunityRejectedCount++;
                    break;
                case AiMoveRejectionKind.Traffic:
                    TrafficRejectedCount++;
                    break;
                case AiMoveRejectionKind.Regression:
                    RegressionRejectedCount++;
                    break;
                default:
                    NoProgressRejectedCount++;
                    break;
            }
        }

        internal void RecordImproveFiringCandidate(
            bool attackCapable,
            bool safe,
            bool strictImprovement,
            bool differentSafeCell,
            ImproveFiringCandidateState candidateState)
        {
            if (attackCapable)
            {
                ImproveAttackCapableCandidates++;
            }

            if (safe)
            {
                ImproveSafeCandidates++;
            }

            if (strictImprovement)
            {
                ImproveStrictImprovementCandidates++;
            }

            if (differentSafeCell)
            {
                ImproveDifferentSafeCellCandidates++;
            }

            if (candidateState == ImproveFiringCandidateState.None)
            {
                return;
            }

            if (candidateState == ImproveFiringCandidateState.Accepted)
            {
                ImproveFiringState = candidateState;
                return;
            }

            if (ImproveFiringState == ImproveFiringCandidateState.Accepted ||
                (!attackCapable && ImproveAttackCapableCandidates > 0))
            {
                return;
            }

            if (ImproveFiringState == ImproveFiringCandidateState.None || attackCapable)
            {
                ImproveFiringState = candidateState;
            }
        }

        internal void RecordImproveFiringFreeJumpCandidate()
        {
            ImproveFreeJumpCandidates++;
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

    private readonly struct GroundMeleeRouteProjection(
        int routeTurns,
        float startDistance,
        float firstStepDistance,
        float bestDistance,
        string goal)
    {
        internal int RouteTurns { get; } = routeTurns;
        internal float StartDistance { get; } = startDistance;
        internal float FirstStepDistance { get; } = firstStepDistance;
        internal float BestDistance { get; } = bestDistance;
        internal string Goal { get; } = goal;
        internal float Progress => StartDistance - BestDistance;
    }

    private readonly struct GroundMeleeConnectedRoute(
        GroundMeleeRouteProjection projection,
        int3 bestPosition,
        int connectedNodes,
        bool complete,
        bool frontierCapped,
        int firstStepMoveCost,
        int contactMoveCost)
    {
        internal GroundMeleeRouteProjection Projection { get; } = projection;
        internal int3 BestPosition { get; } = bestPosition;
        internal int ConnectedNodes { get; } = connectedNodes;
        internal bool Complete { get; } = complete;
        internal bool FrontierCapped { get; } = frontierCapped;
        internal int FirstStepMoveCost { get; } = Math.Max(0, firstStepMoveCost);
        internal int ContactMoveCost { get; } = Math.Max(0, contactMoveCost);
        internal int TotalPathCost => FirstStepMoveCost + ContactMoveCost;
        internal bool IsCompleteApproach => Complete && Projection.Goal == "approach";
    }

    private readonly struct GroundMeleeTargetContactRouteQuery(
        GroundMeleeTargetContactRouteMap map,
        bool complete,
        int3 bestGoal,
        int bestGoalMoveCost,
        IReadOnlyDictionary<string, int> contactCostByPosition,
        IReadOnlyDictionary<string, int3> contactGoalByPosition,
        bool isApproachRoute = false)
    {
        internal GroundMeleeTargetContactRouteMap Map { get; } = map;
        internal bool Complete { get; } = complete;
        internal int3 BestGoal { get; } = bestGoal;
        internal int BestGoalMoveCost { get; } = Math.Max(0, bestGoalMoveCost);
        internal IReadOnlyDictionary<string, int> ContactCostByPosition { get; } = contactCostByPosition;
        internal IReadOnlyDictionary<string, int3> ContactGoalByPosition { get; } = contactGoalByPosition;
        internal bool IsCompleteApproach { get; } = complete && isApproachRoute;

        internal int GetMoveCost(int3 position)
        {
            return Map.GetMoveCost(position);
        }

        internal bool TryGetContactCost(int3 position, out int contactMoveCost, out int3 contactGoal)
        {
            contactMoveCost = 0;
            contactGoal = default;

            if (ContactCostByPosition == null ||
                !ContactCostByPosition.TryGetValue(GetPositionKey(position), out var cost))
            {
                return false;
            }

            contactMoveCost = Math.Max(0, cost);

            if (ContactGoalByPosition != null &&
                ContactGoalByPosition.TryGetValue(GetPositionKey(position), out var goal))
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
        IReadOnlyDictionary<string, int> moveCostByPosition,
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
            return Positions != null && Positions.Any(candidate => candidate == position);
        }

        internal int GetMoveCost(int3 position)
        {
            return moveCostByPosition != null &&
                   moveCostByPosition.TryGetValue(GetPositionKey(position), out var moveCost)
                ? moveCost
                : 0;
        }
    }

    private readonly struct GroundMeleeTargetContactRouteCandidate(
        int3 position,
        int3 selectedDestination,
        int selectedMoveCost,
        int selectedContactMoveCost,
        int3 selectedContactGoal,
        IReadOnlyList<GameLocationCharacterDefinitions.PathStep> routePath,
        string source,
        int sourcePriority,
        float sourceScore,
        int sourceTurnsToAction,
        GroundMeleeConnectedRoute route)
    {
        internal int3 Position { get; } = position;
        internal int3 SelectedDestination { get; } = selectedDestination;
        internal int SelectedMoveCost { get; } = Math.Max(0, selectedMoveCost);
        internal int SelectedContactMoveCost { get; } = Math.Max(0, selectedContactMoveCost);
        internal int3 SelectedContactGoal { get; } = selectedContactGoal;
        internal IReadOnlyList<GameLocationCharacterDefinitions.PathStep> RoutePath { get; } = routePath ?? [];
        internal string Source { get; } = source;
        internal int SourcePriority { get; } = sourcePriority;
        internal float SourceScore { get; } = sourceScore;
        internal int SourceTurnsToAction { get; } = sourceTurnsToAction;
        internal GroundMeleeConnectedRoute Route { get; } = route;
        internal bool Complete => Route.Complete;
        internal bool Capped => !Route.Complete && Route.FrontierCapped;
    }

    private readonly struct GroundMeleeTargetContactRouteSeed(
        AiAcceptedMoveCandidate candidate,
        string source,
        int sourcePriority,
        int firstStepMoveCost)
    {
        internal AiAcceptedMoveCandidate Candidate { get; } = candidate;
        internal int3 Position => Candidate.Position;
        internal string Source { get; } = source;
        internal int SourcePriority { get; } = sourcePriority;
        internal int FirstStepMoveCost { get; } = Math.Max(0, firstStepMoveCost);
    }

    private readonly struct GroundMeleeRouteWaypointNode(
        int3 position,
        int moveCost,
        int contactMoveCost,
        int3 contactGoal,
        MoveMode moveMode,
        PathfindingNode.InformationFlag moveFlags)
    {
        internal int3 Position { get; } = position;
        internal int MoveCost { get; } = Math.Max(0, moveCost);
        internal int ContactMoveCost { get; } = Math.Max(0, contactMoveCost);
        internal int3 ContactGoal { get; } = contactGoal;
        internal MoveMode MoveMode { get; } = moveMode;
        internal PathfindingNode.InformationFlag MoveFlags { get; } = moveFlags;
    }

    private readonly struct GroundMeleeRouteIntentMemory(
        ulong targetGuid,
        int3 previousPosition,
        int3 lastPosition,
        float bestRouteDistance,
        int round,
        int turnStamp)
    {
        internal ulong TargetGuid { get; } = targetGuid;
        internal int3 PreviousPosition { get; } = previousPosition;
        internal int3 LastPosition { get; } = lastPosition;
        internal float BestRouteDistance { get; } = bestRouteDistance;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool Matches(GameLocationCharacter target)
        {
            return target?.RulesetCharacter != null &&
                   target.Guid == TargetGuid;
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

    private readonly struct GroundMeleeDetourCandidateMemory(
        ulong targetGuid,
        int3 startPosition,
        int3 destination,
        float score,
        float progress,
        int turnsToAction,
        int round,
        int turnStamp)
    {
        internal ulong TargetGuid { get; } = targetGuid;
        internal int3 StartPosition { get; } = startPosition;
        internal int3 Destination { get; } = destination;
        internal float Score { get; } = score;
        internal float Progress { get; } = progress;
        internal int TurnsToAction { get; } = turnsToAction;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool Matches(GameLocationCharacter target, int3 start)
        {
            return target?.RulesetCharacter != null &&
                   target.Guid == TargetGuid &&
                   start == StartPosition &&
                   Round == GetCurrentBattleRound() &&
                   TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
        }
    }

    private sealed class ReachableRouteDestinationMemory(
        int3 startPosition,
        int remainingMove,
        int round,
        int turnStamp,
        List<int3> positions,
        List<int> moveCosts,
        List<MoveMode> moveModes,
        List<PathfindingNode.InformationFlag> moveFlags,
        bool walkOnly)
    {
        private readonly HashSet<string> positionKeys = [..positions.Select(GetPositionKey)];
        private readonly Dictionary<string, int> moveCostByPosition = positions
            .Select((position, index) => new { position, index })
            .ToDictionary(
                item => GetPositionKey(item.position),
                item => item.index < moveCosts.Count ? moveCosts[item.index] : 0);
        private readonly Dictionary<string, MoveMode> moveModeByPosition = positions
            .Select((position, index) => new { position, index })
            .ToDictionary(
                item => GetPositionKey(item.position),
                item => item.index < moveModes.Count ? moveModes[item.index] : MoveMode.Walk);
        private readonly Dictionary<string, PathfindingNode.InformationFlag> moveFlagsByPosition = positions
            .Select((position, index) => new { position, index })
            .ToDictionary(
                item => GetPositionKey(item.position),
                item => item.index < moveFlags.Count ? moveFlags[item.index] : default);

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
            return positionKeys.Contains(GetPositionKey(position));
        }

        internal int GetMoveCost(int3 position)
        {
            return moveCostByPosition.TryGetValue(GetPositionKey(position), out var moveCost)
                ? moveCost
                : ComputeForcedMoveCost(StartPosition, position);
        }

        internal MoveMode GetMoveMode(int3 position)
        {
            return moveModeByPosition.TryGetValue(GetPositionKey(position), out var moveMode)
                ? moveMode
                : MoveMode.Walk;
        }

        internal PathfindingNode.InformationFlag GetMoveFlags(int3 position)
        {
            return moveFlagsByPosition.TryGetValue(GetPositionKey(position), out var flags)
                ? flags
                : default;
        }
    }

    private readonly struct ProxyThreatRecoveryCandidate(
        int3 position,
        float score,
        float threatGain,
        bool attackConnected)
    {
        internal int3 Position { get; } = position;
        internal float Score { get; } = score;
        internal float ThreatGain { get; } = threatGain;
        internal bool AttackConnected { get; } = attackConnected;
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

    private readonly struct PendingTerminalDodgeEndTurnMemory(int round, int turnStamp)
    {
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct PendingAiProcessTurnRecoveryMemory(int round, int turnStamp, Id actionId, string reason)
    {
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal Id ActionId { get; } = actionId;
        internal string Reason { get; } = string.IsNullOrEmpty(reason) ? "unknown" : reason;

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
        int turnStamp)
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
    }

    private readonly struct PendingRouteActionOnlyTerminalMemory(
        ActionLinkedMoveMemory pendingAction,
        int3 expectedDestination,
        int3 actualDestination,
        bool consumeAfterAbort,
        int round,
        int turnStamp)
    {
        internal ActionLinkedMoveMemory PendingAction { get; } = pendingAction;
        internal int3 ExpectedDestination { get; } = expectedDestination;
        internal int3 ActualDestination { get; } = actualDestination;
        internal bool ConsumeAfterAbort { get; } = consumeAfterAbort;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
        }
    }

    private readonly struct PendingAiProcessTerminalLaunchMemory(
        Id actionId,
        ReadyActionType readyActionType,
        PendingTerminalLaunchKind kind,
        int round,
        int turnStamp)
    {
        internal Id ActionId { get; } = actionId;
        internal ReadyActionType ReadyActionType { get; } = readyActionType;
        internal PendingTerminalLaunchKind Kind { get; } = kind;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool MatchesCurrentTurn(int round, int turnStamp)
        {
            return Round == round && TurnStamp == turnStamp;
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

    private readonly struct ThreatRouteRecoveryMemory(
        CombatAiMovementGoalKind movementGoal,
        int3 failedDestination,
        int round,
        int turnStamp)
    {
        internal CombatAiMovementGoalKind MovementGoal { get; } = movementGoal;
        internal int3 FailedDestination { get; } = failedDestination;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;

        internal bool Matches(CombatAiMovementGoalKind movementGoal, int round, int turnStamp)
        {
            return MovementGoal == movementGoal && Round == round && TurnStamp == turnStamp;
        }
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

        internal bool MatchesSourceAndStart(RecentMeleeThreatMemory memory, int3 startPosition)
        {
            return MatchesSource(memory) && StartPosition == startPosition;
        }

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

    private readonly struct TacticalSituationMemory(
        string signature,
        int round,
        int turnStamp)
    {
        internal string Signature { get; } = signature;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
    }

    private readonly struct RepeatedEndStateMemory(
        ulong targetGuid,
        CombatAiExecutedActionKind action,
        int3 actorPosition,
        int3 targetPosition,
        int round,
        int turnStamp,
        int repeatCount)
    {
        internal ulong TargetGuid { get; } = targetGuid;
        internal CombatAiExecutedActionKind Action { get; } = action;
        internal int3 ActorPosition { get; } = actorPosition;
        internal int3 TargetPosition { get; } = targetPosition;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal int RepeatCount { get; } = repeatCount;
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
        CombatAiMovementPlan movementPlan)
    {
        internal CombatAiActionProbe ActionProbe { get; } = actionProbe;
        internal CombatAiMovementPlan MovementPlan { get; } = movementPlan;
    }

    private readonly struct CombatAiMovementAvailabilityResult(
        CombatAiMovementAvailability availability,
        FreeJumpFallbackAvailability freeJumpAvailability = FreeJumpFallbackAvailability.Unavailable)
    {
        internal CombatAiMovementAvailability Availability { get; } = availability;
        internal FreeJumpFallbackAvailability FreeJumpAvailability { get; } = freeJumpAvailability;
        internal bool IsAvailable => Availability == CombatAiMovementAvailability.Available;
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
        internal bool MainAvailable => MainActionType == ActionStatus.Available || AttackMain == ActionStatus.Available;
        internal bool TerminalMainAvailable =>
            MainActionType == ActionStatus.Available &&
            MainUseCount == 0 &&
            !HasLastMainAction;
        internal bool ReadyAvailable => TerminalMainAvailable && Ready == ActionStatus.Available;
        internal bool DodgeAvailable => TerminalMainAvailable && Dodge == ActionStatus.Available;
        internal CombatAiExecutedActionKind RecordedTerminalAction =>
            LastMainTerminalAction != CombatAiExecutedActionKind.None
                ? LastMainTerminalAction
                : LastTerminalAction;
    }

    private readonly struct CombatAiMainActionValidation
    {
        internal CombatAiMainActionValidation(
            bool isValid,
            CombatAiMainActionBlockKind blockKind = CombatAiMainActionBlockKind.None,
            ActionStatus actionStatus = default)
        {
            IsValid = isValid;
            BlockKind = blockKind;
            ActionStatus = actionStatus;
        }

        internal bool IsValid { get; }
        internal CombatAiMainActionBlockKind BlockKind { get; }
        internal ActionStatus ActionStatus { get; }
    }

    private readonly struct CombatAiResidualHostileActionResult(
        CombatAiResidualHostileActionResultKind kind,
        CombatAiActionKind actionKind = CombatAiActionKind.None,
        Id actionId = Id.NoAction,
        CombatAiMainActionBlockKind blockKind = CombatAiMainActionBlockKind.None,
        ActionStatus actionStatus = default)
    {
        internal CombatAiResidualHostileActionResultKind Kind { get; } = kind;
        internal CombatAiActionKind ActionKind { get; } = actionKind;
        internal Id ActionId { get; } = actionId;
        internal CombatAiMainActionBlockKind BlockKind { get; } = blockKind;
        internal ActionStatus ActionStatus { get; } = actionStatus;
        internal bool Executed => Kind == CombatAiResidualHostileActionResultKind.Executed;
        internal bool PolicyHeld => Kind == CombatAiResidualHostileActionResultKind.PolicyHeld;
        internal bool IsBlocked => Kind == CombatAiResidualHostileActionResultKind.Blocked;
    }

    private readonly struct RepeatedMeleeAlternativeResult(
        RepeatedMeleeAlternativeResultKind kind,
        CombatAiExecutedActionKind actionKind = CombatAiExecutedActionKind.None,
        CombatAiMainActionBlockKind blockKind = CombatAiMainActionBlockKind.None,
        int repeatCount = 0)
    {
        internal RepeatedMeleeAlternativeResultKind Kind { get; } = kind;
        internal CombatAiExecutedActionKind ActionKind { get; } = actionKind;
        internal CombatAiMainActionBlockKind BlockKind { get; } = blockKind;
        internal int RepeatCount { get; } = repeatCount;
        internal bool Executed => Kind == RepeatedMeleeAlternativeResultKind.Executed;
    }

    private readonly struct GroundMeleeJumpShortcutResult(
        bool executed,
        GroundMeleeJumpShortcutBlockKind blockKind,
        int3 destination = default)
    {
        internal bool Executed { get; } = executed;
        internal GroundMeleeJumpShortcutBlockKind BlockKind { get; } = blockKind;
        internal int3 Destination { get; } = destination;
    }

    private readonly struct CombatAiActionExecutionMemory(
        Id actionId,
        ActionType actionType,
        ActionStatus mainBefore,
        int round,
        int turnStamp)
    {
        internal Id ActionId { get; } = actionId;
        internal ActionType ActionType { get; } = actionType;
        internal ActionStatus MainBefore { get; } = mainBefore;
        internal int Round { get; } = round;
        internal int TurnStamp { get; } = turnStamp;
        internal bool IsMainAction => ActionType == ActionType.Main;
    }

    private readonly struct CombatAiActionPathStatus(
        CombatAiActionPathKind kind,
        CombatAiActionPathAvailability availability,
        int turnsToPreferredAction = -1)
    {
        internal CombatAiActionPathKind Kind { get; } = kind;
        internal CombatAiActionPathAvailability Availability { get; } = availability;
        internal int TurnsToPreferredAction { get; } = turnsToPreferredAction;
    }

    private readonly struct AttackPositionKey(
        ulong attackerGuid,
        int3 attackerPosition,
        ulong targetGuid,
        int3 targetPosition) : IEquatable<AttackPositionKey>
    {
        private ulong AttackerGuid { get; } = attackerGuid;
        private int3 AttackerPosition { get; } = attackerPosition;
        private ulong TargetGuid { get; } = targetGuid;
        private int3 TargetPosition { get; } = targetPosition;

        public bool Equals(AttackPositionKey other)
        {
            return AttackerGuid == other.AttackerGuid &&
                   AttackerPosition == other.AttackerPosition &&
                   TargetGuid == other.TargetGuid &&
                   TargetPosition == other.TargetPosition;
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
        EnsureCombatAiRuntimeCache(parameters?.character?.GameLocationCharacter);

        if (IsAdvancedCombatAiEnabled)
        {
            return true;
        }

        var rulesetCharacter = parameters?.character?.GameLocationCharacter?.RulesetCharacter;

        return rulesetCharacter != null && GetApproachSourceGuid(rulesetCharacter, consideration?.StringParameter) != 0;
    }

    internal static void NotifyBattleStarted(GameLocationCharacter character)
    {
        EnsureCombatAiRuntimeCache(character);
    }

    internal static void ResetActionLedgerForTurn(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        LastActionExecutionCache.Remove(character.Guid);
        LastMainActionExecutionCache.Remove(character.Guid);
        TurnMainActionUseCountCache.Remove(character.Guid);
        TurnBonusActionUseCountCache.Remove(character.Guid);
        TurnStartActionEconomyCache.Remove(character.Guid);
        PreMainRouteMoveAttemptCache.Remove(character.Guid);
        DisconnectedPositioningSealCache.Remove(character.Guid);
        DisconnectedPositioningMovementLockCache.Remove(character.Guid);
        PostMainClearAllyCorridorAttemptCache.Remove(character.Guid);
        PendingUtilityTerminalContinuationCache.Remove(character.Guid);
        RouteMoveDashBlockCache.Remove(character.Guid);
        RouteMoveCompletionClosedCache.Remove(character.Guid);
        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
        ConnectedFiringLineRecoveryAttemptCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
        PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
        AiProcessTurnRecoveryConsumedCache.Remove(character.Guid);
        PostRecoveryEndTurnMainActionSealCache.Remove(character.Guid);
        PostRecoveryMainActionNormalizationCache.Remove(character.Guid);
        PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
    }

    private static void FailStalePendingTerminalActions(GameLocationCharacter character, string phase)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (PendingTerminalDodgeEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalDodge) &&
            (pendingTerminalDodge.Round != currentRound || pendingTerminalDodge.TurnStamp != currentTurnStamp))
        {
            var hasPendingCondition =
                PendingFallbackDodgeConditionCache.TryGetValue(character.Guid, out var existingConditions);
            var condition = hasPendingCondition
                ? FindNewDodgingCondition(character.RulesetCharacter, existingConditions)
                : null;

            PendingFallbackDodgeConditionCache.Remove(character.Guid);
            PendingTerminalDodgeEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);

            if (condition != null)
            {
                NormalizeFallbackDodgeCondition(character, condition);
                TryCompleteTerminalDodgeEndTurn(
                    character,
                    true,
                    pendingTerminalDodge,
                    TerminalDodgeCompletionKind.Applied);
            }
            else
            {
                var dodgeStatus = character.GetActionStatus(Id.Dodge, ActionScope.Battle);

            }
        }

        if (PendingTerminalReadyEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalReady) &&
            (pendingTerminalReady.Round != currentRound || pendingTerminalReady.TurnStamp != currentTurnStamp))
        {
            var readyStatus = character.GetActionStatus(Id.Ready, ActionScope.Battle);

            PendingTerminalReadyEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
            TryCompleteTerminalReadyEndTurn(
                character,
                pendingTerminalReady,
                TerminalReadyCompletionKind.EngineRejected);
        }
    }

    private static void FailStalePendingRouteActionOnlyTerminal(GameLocationCharacter character, string phase)
    {
        if (character?.RulesetCharacter == null ||
            !PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out var pendingTerminal))
        {
            return;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (pendingTerminal.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            return;
        }

        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
    }

    internal static void PrimeTurnCache(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        EnsureCombatAiRuntimeCache(character);

        if (IsAdvancedCombatAiEnabled)
        {
            ObservedCombatMemoryTurnStamp++;
            PostRecoveryEndTurnMainActionSealCache.Remove(character.Guid);
            PostRecoveryMainActionNormalizationCache.Remove(character.Guid);
            FailStalePendingTerminalActions(character, "turn-start");
            FailStalePendingRouteActionOnlyTerminal(character, "turn-start");
            TurnStartActionEconomyCache[character.Guid] = BuildActionEconomySnapshot(character);
            UpdateTacticalSituationMemory(character);
            MeleeAttackPositionCache.Clear();
            SpellAttackPositionCache.Clear();
            ActionKindPositionCache.Clear();
            CoverEvaluationCache.Clear();
            CurrentStateRouteBlockCache.Remove(character.Guid);
            GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
            GroundMeleePartialRouteCache.Remove(character.Guid);
            ClearReachableRouteDestinationCache(character);
            TurnMovementProgressCache.Remove(character.Guid);
            ResolveGroundMeleeMoveSettling(character, allowConnectedRouteValidation: true);
            var profile = BuildProfile(character);
            var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

            if (battleService != null && IsAiControlledForCombat(character))
            {
                PrimeTurnMovementProgress(character, BuildCombatAiTurnPlan(character, profile, battleService));
            }

        }

    }

    internal static void ClearTurnCache(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }


        ProfileCache.Remove(character.Guid);
        MeleeAttackPositionCache.Clear();
        SpellAttackPositionCache.Clear();
        ActionKindPositionCache.Clear();
        CoverEvaluationCache.Clear();
        JumpImmediateAttackReachableCache.Remove(character.Guid);
        GroundMeleeJumpRouteAvailableCache.Remove(character.Guid);
        CurrentStateRouteBlockCache.Remove(character.Guid);
        TargetContactRouteQueryCache.Remove(character.Guid);
        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
        ClearReachableRouteDestinationCache(character);
        PendingAiMoveAttemptCache.Remove(character.Guid);
        ThreatRouteRecoveryCache.Remove(character.Guid);
        TurnStartActionEconomyCache.Remove(character.Guid);
        PendingResidualMainActionCache.Remove(character.Guid);
        PendingUtilityTerminalContinuationCache.Remove(character.Guid);
        UbResidualMainAttackCommitCache.Remove(character.Guid);
        PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
        AiProcessTurnRecoveryConsumedCache.Remove(character.Guid);

        if (character.RulesetCharacter != null)
        {
            PersonalityFlagsCache.Remove(character.RulesetCharacter.Guid);
        }
    }

    internal static void ClearBattleMemory(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        ObservedCombatMemoryCache.Remove(character.Guid);
        AiMoveFailureCache.Remove(character.Guid);
        PendingAiMoveAttemptCache.Remove(character.Guid);
        PostMainClearAllyCorridorAttemptCache.Remove(character.Guid);
        ActionLinkedMoveCache.Remove(character.Guid);
        RouteMoveCompletionClosedCache.Remove(character.Guid);
        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
        ConnectedFiringLineRecoveryAttemptCache.Remove(character.Guid);
        LostTargetSearchAttemptCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        ThreatRouteRecoveryCache.Remove(character.Guid);
        ProxyThreatRouteAttemptCache.Remove(character.Guid);
        PreMainRouteMoveAttemptCache.Remove(character.Guid);
        DisconnectedPositioningSealCache.Remove(character.Guid);
        DisconnectedPositioningMovementLockCache.Remove(character.Guid);
        PendingFallbackDodgeConditionCache.Remove(character.Guid);
        PendingTerminalDodgeEndTurnCache.Remove(character.Guid);
        PendingTerminalReadyEndTurnCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
        PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
        AiProcessTurnRecoveryConsumedCache.Remove(character.Guid);
        PostRecoveryEndTurnMainActionSealCache.Remove(character.Guid);
        PostRecoveryMainActionNormalizationCache.Remove(character.Guid);
        PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
        FallbackDodgeConditionCache.Remove(character.Guid);
        RepeatTerminalActionCache.Remove(character.Guid);
        RepeatAttackActionCache.Remove(character.Guid);
        TurnMovementProgressCache.Remove(character.Guid);
        LastActionExecutionCache.Remove(character.Guid);
        LastMainActionExecutionCache.Remove(character.Guid);
        TurnMainActionUseCountCache.Remove(character.Guid);
        TurnBonusActionUseCountCache.Remove(character.Guid);
        TurnStartActionEconomyCache.Remove(character.Guid);
        RecentMeleeThreatMemoryCache.Remove(character.Guid);
        ThreatAvoidanceMemoryCache.Remove(character.Guid);
        TacticalSituationMemoryCache.Remove(character.Guid);
        PendingResidualMainActionCache.Remove(character.Guid);
        PendingUtilityTerminalContinuationCache.Remove(character.Guid);
        UbResidualMainAttackCommitCache.Remove(character.Guid);
        RouteMoveDashBlockCache.Remove(character.Guid);
        BaselineFreeJumpAttemptCache.Remove(character.Guid);
        JumpImmediateAttackReachableCache.Remove(character.Guid);
        GroundMeleeJumpRouteAvailableCache.Remove(character.Guid);
        CurrentStateRouteBlockCache.Remove(character.Guid);
        var groundMeleeRouteKey = GetGroundMeleeRouteMemoryKey(character);

        GroundMeleeRouteIntentCache.Remove(groundMeleeRouteKey);
        GroundMeleeRouteFailureCache.Remove(groundMeleeRouteKey);
        GroundMeleeDetourCandidateCache.Remove(groundMeleeRouteKey);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        GroundMeleePartialRouteCache.Remove(character.Guid);
        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
        ClearReachableRouteDestinationCache(character);
    }

    internal static void ClearAiMoveFailures(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        AiMoveFailureCache.Remove(character.Guid);
        PendingAiMoveAttemptCache.Remove(character.Guid);
        PostMainClearAllyCorridorAttemptCache.Remove(character.Guid);
        ActionLinkedMoveCache.Remove(character.Guid);
        RouteMoveCompletionClosedCache.Remove(character.Guid);
        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
        ConnectedFiringLineRecoveryAttemptCache.Remove(character.Guid);
        LostTargetSearchAttemptCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        ThreatRouteRecoveryCache.Remove(character.Guid);
        ProxyThreatRouteAttemptCache.Remove(character.Guid);
        PreMainRouteMoveAttemptCache.Remove(character.Guid);
        DisconnectedPositioningSealCache.Remove(character.Guid);
        DisconnectedPositioningMovementLockCache.Remove(character.Guid);
        JumpImmediateAttackReachableCache.Remove(character.Guid);
        GroundMeleeJumpRouteAvailableCache.Remove(character.Guid);
        CurrentStateRouteBlockCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        GroundMeleePartialRouteCache.Remove(character.Guid);
        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
        ClearReachableRouteDestinationCache(character);
        BaselineFreeJumpAttemptCache.Remove(character.Guid);
    }

    private static void EnsureCombatAiRuntimeCache(GameLocationCharacter character)
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
        _ = character;
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
        RouteMoveCompletionClosedCache.Clear();
        PendingRouteActionOnlyTerminalCache.Clear();
        ConnectedFiringLineRecoveryAttemptCache.Clear();
        LostTargetSearchAttemptCache.Clear();
        PendingRouteMovementLockCache.Clear();
        ThreatRouteRecoveryCache.Clear();
        ProxyThreatRouteAttemptCache.Clear();
        PreMainRouteMoveAttemptCache.Clear();
        DisconnectedPositioningSealCache.Clear();
        DisconnectedPositioningMovementLockCache.Clear();
        PendingFallbackDodgeConditionCache.Clear();
        PendingTerminalDodgeEndTurnCache.Clear();
        PendingTerminalReadyEndTurnCache.Clear();
        PendingAiProcessTerminalLaunchCache.Clear();
        PendingAiProcessTerminalLaunchAcceptedCache.Clear();
        PendingAiProcessTurnRecoveryCache.Clear();
        AiProcessTurnRecoveryConsumedCache.Clear();
        PostRecoveryEndTurnMainActionSealCache.Clear();
        PostRecoveryMainActionNormalizationCache.Clear();
        PendingTerminalActionEndTurnSuppressCache.Clear();
        FallbackDodgeConditionCache.Clear();
        RepeatTerminalActionCache.Clear();
        RepeatAttackActionCache.Clear();
        TurnMovementProgressCache.Clear();
        LastActionExecutionCache.Clear();
        LastMainActionExecutionCache.Clear();
        TurnMainActionUseCountCache.Clear();
        TurnBonusActionUseCountCache.Clear();
        TurnStartActionEconomyCache.Clear();
        RecentMeleeThreatMemoryCache.Clear();
        ThreatAvoidanceMemoryCache.Clear();
        TacticalSituationMemoryCache.Clear();
        PendingResidualMainActionCache.Clear();
        PendingUtilityTerminalContinuationCache.Clear();
        UbResidualMainAttackCommitCache.Clear();
        RouteMoveDashBlockCache.Clear();
        BaselineFreeJumpAttemptCache.Clear();
        MeleeAttackPositionCache.Clear();
        SpellAttackPositionCache.Clear();
        ActionKindPositionCache.Clear();
        CoverEvaluationCache.Clear();
        JumpImmediateAttackReachableCache.Clear();
        GroundMeleeJumpRouteAvailableCache.Clear();
        CurrentStateRouteBlockCache.Clear();
        TargetContactRouteQueryCache.Clear();
        GroundMeleeRouteIntentCache.Clear();
        GroundMeleeRouteFailureCache.Clear();
        GroundMeleeDetourCandidateCache.Clear();
        GroundMeleeMoveSettlingCache.Clear();
        GroundMeleePartialRouteCache.Clear();
        GroundMeleeNoMoveTerminalSealCache.Clear();
        ReachableRouteDestinationCache.Clear();
        ReachableRoutePathfindingCountCache.Clear();
        ObservedCombatMemoryTurnStamp = 0;
    }

    internal static bool HasPendingActionLinkedMove(GameLocationCharacter character)
    {
        return character != null && ActionLinkedMoveCache.ContainsKey(character.Guid);
    }

    private static int GetDictionaryValueOrDefault(Dictionary<ulong, int> dictionary, ulong key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : 0;
    }

    internal static IEnumerator HandleAiForcedMotionCompleted(CharacterAction action)
    {
        if (action?.ActingCharacter?.RulesetCharacter == null)
        {
            yield break;
        }

        TryCloseForcedMotionGroundMeleeTerminal(action.ActingCharacter);
        ScheduleAiProcessTurnRecovery(action.ActingCharacter, action.ActionId, "ForcedMotion");

        yield break;
    }

    private static bool TryCloseForcedMotionGroundMeleeTerminal(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character) ||
            !PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out var terminal) ||
            ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            GroundMeleeMoveSettlingCache.ContainsKey(character.Guid) ||
            !IsGroundMeleePursuitTerminalRoute(terminal.PendingAction) ||
            !HasClosedNoMoveGroundMeleeRoute(character) ||
            !IsTacticalMovementUnavailable(character))
        {
            return false;
        }

        var target = terminal.PendingAction.Target;
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (target?.RulesetCharacter == null ||
            battleService == null ||
            CanUseActionKindAtPosition(
                character,
                character.LocationPosition,
                target,
                CombatAiActionKind.Melee,
                battleService))
        {
            return false;
        }

        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
        GroundMeleePartialRouteCache.Remove(character.Guid);
        RouteMoveDashBlockCache.Remove(character.Guid);
        TurnMovementProgressCache.Remove(character.Guid);


        return true;
    }

    private static void ScheduleAiProcessTurnRecovery(
        GameLocationCharacter character,
        Id actionId,
        string reason)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsActiveBattleContender(character))
        {
            return;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        PendingAiProcessTurnRecoveryCache[character.Guid] =
            new PendingAiProcessTurnRecoveryMemory(currentRound, currentTurnStamp, actionId, reason);

    }

    private static bool TryGetCurrentPendingAiProcessTurnRecovery(
        GameLocationCharacter character,
        out PendingAiProcessTurnRecoveryMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !PendingAiProcessTurnRecoveryCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            return true;
        }

        PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
        memory = default;

        return false;
    }

    internal static bool TryConsumePendingAiProcessTurnRecoveryAtAiProcessBoundary(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PendingAiProcessTurnRecoveryCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }


        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
            return true;
        }

        if (!IsActiveBattleContender(character))
        {
            PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
            return true;
        }

        if (HasCurrentAiProcessTurnRecovery(character))
        {
            PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
            return true;
        }

        return TryRecoverAiProcessTurnProgression(character, memory.Reason);
    }

    internal static bool TryExitAiProcessAfterPostRecoveryEndTurn(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character) ||
            !HasCurrentPostRecoveryEndTurnMainActionSeal(character))
        {
            return false;
        }

        if (HasPendingReactionRequests())
        {
            return false;
        }

        return true;
    }

    internal static bool TryPrunePostRecoveryStartNextChainQueue(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character) ||
            !HasCurrentPostRecoveryEndTurnMainActionSeal(character))
        {
            return false;
        }

        if (HasPendingReactionRequests())
        {
            return false;
        }

        if (ServiceRepository.GetService<IGameLocationActionService>() is not GameLocationActionManager actionManager ||
            !actionManager.actionChainQueueByCharacter.TryGetValue(character, out var pendingChains) ||
            pendingChains == null ||
            pendingChains.Count <= 0)
        {
            return false;
        }

        var initialCount = pendingChains.Count;
        var retainedChains = new Queue<GameLocationActionManager.ActionChainSlot>(initialCount);
        var removed = 0;

        while (pendingChains.Count > 0)
        {
            var actionChainSlot = pendingChains.Dequeue();

            if (IsPostRecoveryStaleMainActionChain(character, actionChainSlot))
            {
                removed++;
                continue;
            }

            retainedChains.Enqueue(actionChainSlot);
        }

        while (retainedChains.Count > 0)
        {
            pendingChains.Enqueue(retainedChains.Dequeue());
        }

        if (removed <= 0)
        {
            return false;
        }

        return true;
    }

    internal static bool TrySuppressPostRecoveryRunNextChains(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character) ||
            !HasCurrentPostRecoveryEndTurnMainActionSeal(character))
        {
            return false;
        }

        if (HasPendingReactionRequests())
        {
            return false;
        }

        TryNormalizePostRecoveryCommittedMainAction(character);
        TryPrunePostRecoveryStartNextChainQueue(character);

        return true;
    }

    private static bool IsPostRecoveryStaleMainActionChain(
        GameLocationCharacter character,
        GameLocationActionManager.ActionChainSlot actionChainSlot)
    {
        var actionParams = actionChainSlot?.actionChainParams?.GetActionsParams();

        if (character?.RulesetCharacter == null || actionParams == null || actionParams.Count <= 0)
        {
            return false;
        }

        for (var i = 0; i < actionParams.Count; i++)
        {
            var actionParam = actionParams[i];
            var actionId = actionParam?.ActionDefinition?.Id;

            if (actionParam?.ActingCharacter != character ||
                actionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryRecoverAiProcessTurnProgression(
        GameLocationCharacter character,
        string reason,
        CharacterAction currentAction = null,
        bool ignoreSingleCurrentAction = false)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !CanExecuteAutomaticCombatAction(character))
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
            return false;
        }

        if (HasCurrentAiProcessTurnRecovery(character))
        {
            PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
            return false;
        }

        if (HasPendingReactionRequests())
        {
            return false;
        }

        if (HasBlockingPendingActionChain(
                character,
                currentAction,
                ignoreSingleCurrentAction))
        {
            return false;
        }

        if (HasPendingAiProcessTurnRecoveryCombatAiState(character))
        {
            return false;
        }

        if (reason == "ForcedMotion" &&
            !IsTacticalMovementUnavailable(character) &&
            !HasCommittedMainActionThisTurn(character))
        {
            PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
            return false;
        }

        TryNormalizePostRecoveryCommittedMainAction(character);

        PendingAiProcessTurnRecoveryCache.Remove(character.Guid);
        AiProcessTurnRecoveryConsumedCache[character.Guid] =
            new PendingTerminalDodgeEndTurnMemory(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp));

        var allowActionLinkedMove = ShouldAllowRecoveryActionLinkedMove(character, reason);

        if (allowActionLinkedMove && TrySpendRecoveryLeftoverActionEconomy(character, true))
        {
            return true;
        }

        if (TrySpendRecoveryLeftoverActionEconomy(character, false))
        {
            return true;
        }

        SealPostRecoveryEndTurnMainActions(character);
        character.EndBattleTurn(GetCurrentBattleRound());

        return true;
    }

    private static bool TrySpendRecoveryLeftoverActionEconomy(
        GameLocationCharacter character,
        bool allowActionLinkedMove)
    {
        if (!TrySpendLeftoverActionEconomy(character, allowActionLinkedMove, endTurnTerminal: true))
        {
            return false;
        }

        var hasPendingTerminalLaunch = HasPendingAiProcessTerminalLaunch(character);

        if (hasPendingTerminalLaunch)
        {
            TryConsumePendingAiProcessTerminalLaunch(character);
        }


        return true;
    }

    private static bool ShouldAllowRecoveryActionLinkedMove(GameLocationCharacter character, string reason)
    {
        return reason == "SearchLostTargetAbort" &&
               character?.RulesetCharacter != null &&
               character.RemainingTacticalMoves > 0 &&
               character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) == ActionStatus.Available;
    }

    private static bool TryNormalizePostRecoveryCommittedMainAction(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character) ||
            !CanExecuteAutomaticCombatAction(character) ||
            HasCurrentPostRecoveryMainActionNormalization(character))
        {
            return false;
        }

        if (!TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction, out _) ||
            committedMainAction.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain) ||
            !IsVanillaMainActionStillAvailable(character))
        {
            return false;
        }

        PostRecoveryMainActionNormalizationCache[character.Guid] =
            new PendingTerminalDodgeEndTurnMemory(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp));

        character.SpendActionType(ActionType.Main);


        return true;
    }

    private static bool HasCurrentPostRecoveryMainActionNormalization(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PostRecoveryMainActionNormalizationCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.Round == currentRound && memory.TurnStamp == currentTurnStamp)
        {
            return true;
        }

        PostRecoveryMainActionNormalizationCache.Remove(character.Guid);

        return false;
    }

    private static bool IsVanillaMainActionStillAvailable(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        return character.GetActionTypeStatus(ActionType.Main) == ActionStatus.Available ||
               character.GetActionStatus(Id.AttackMain, ActionScope.Battle) == ActionStatus.Available ||
               character.GetActionStatus(Id.CastMain, ActionScope.Battle) == ActionStatus.Available ||
               character.GetActionStatus(Id.PowerMain, ActionScope.Battle) == ActionStatus.Available;
    }

    private static void SealPostRecoveryEndTurnMainActions(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PostRecoveryEndTurnMainActionSealCache[character.Guid] =
            new PendingTerminalDodgeEndTurnMemory(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool HasCurrentAiProcessTurnRecovery(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !AiProcessTurnRecoveryConsumedCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.Round == currentRound && memory.TurnStamp == currentTurnStamp)
        {
            return true;
        }

        AiProcessTurnRecoveryConsumedCache.Remove(character.Guid);

        return false;
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

    private static bool HasBlockingPendingActionChain(
        GameLocationCharacter character,
        CharacterAction currentAction,
        bool ignoreSingleCurrentAction = false)
    {
        if (character?.RulesetCharacter == null ||
            ServiceRepository.GetService<IGameLocationActionService>() is not GameLocationActionManager actionManager ||
            !actionManager.actionChainByCharacter.TryGetValue(character, out var actionChainSlot) ||
            actionChainSlot?.actionQueue == null)
        {
            return false;
        }

        var count = actionChainSlot.actionQueue.Count;

        if (count <= 0)
        {
            return false;
        }

        var currentActionMatches = 0;

        for (var i = 0; i < actionChainSlot.actionQueue.Count; i++)
        {
            var pendingAction = actionChainSlot.actionQueue[i].action;

            if (pendingAction != null && ReferenceEquals(pendingAction, currentAction))
            {
                currentActionMatches++;
            }
        }

        if (count == 1 &&
            currentActionMatches == 1 &&
            (ignoreSingleCurrentAction ||
             currentAction is CharacterActionPushed or CharacterActionPushedCustom))
        {
            return false;
        }

        return true;
    }

    private static bool TryPrunePostRecoveryEndTurnCurrentChain(
        GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character) ||
            !HasCurrentPostRecoveryEndTurnMainActionSeal(character) ||
            ServiceRepository.GetService<IGameLocationActionService>() is not GameLocationActionManager actionManager ||
            !actionManager.actionChainByCharacter.TryGetValue(character, out var actionChainSlot) ||
            actionChainSlot?.actionQueue == null)
        {
            return false;
        }

        var removed = 0;
        var initialCount = actionChainSlot.actionQueue.Count;

        if (initialCount <= 0)
        {
            return false;
        }

        for (var i = initialCount - 1; i >= 0; i--)
        {
            var pendingAction = actionChainSlot.actionQueue[i].action;

            if (pendingAction?.ActingCharacter != character ||
                pendingAction.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain))
            {
                continue;
            }

            actionChainSlot.actionQueue.RemoveAt(i);
            removed++;
        }

        if (removed <= 0)
        {
            return false;
        }

        actionChainSlot.aborted = true;
        actionChainSlot.abortReason = CharacterAction.InterruptionType.Invalid;

        return true;
    }

    private static bool HasPendingAiProcessTurnRecoveryCombatAiState(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        return ActionLinkedMoveCache.ContainsKey(character.Guid) ||
               GroundMeleeMoveSettlingCache.ContainsKey(character.Guid) ||
               PendingRouteActionOnlyTerminalCache.ContainsKey(character.Guid) ||
               PendingTerminalDodgeEndTurnCache.ContainsKey(character.Guid) ||
               PendingTerminalReadyEndTurnCache.ContainsKey(character.Guid) ||
               PendingAiProcessTerminalLaunchCache.ContainsKey(character.Guid);
    }

    private static bool IsTacticalMovementUnavailable(GameLocationCharacter character)
    {
        return character.RemainingTacticalMoves <= 0 ||
               character.MaxTacticalMoves <= 0 ||
               character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available;
    }

    internal static void TryCompletePendingActionLinkedMove(CharacterAction action)
    {
        if (action?.ActionId is not Id.TacticalMove)
        {
            return;
        }

        TryCompleteActionLinkedMove(action.ActingCharacter);
    }

    internal static void OnAiTacticalMoveActionChainExecuted(
        GameLocationCharacter character,
        bool aborted,
        CombatAiRouteMoveSourceKind routeMoveSource)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }


        if (routeMoveSource == CombatAiRouteMoveSourceKind.JumpImmediateAttack)
        {
            TryCompletePendingJumpImmediateAttackActionChainSettled(
                character,
                aborted,
                "action-chain-settled");
            return;
        }

        if (TryResolveGroundMeleeMoveSettlingAfterActionChain(character, aborted))
        {
            TryConsumePendingRouteActionOnlyTerminal(character, "action-chain", aborted);
            return;
        }

        TryUseGroundMeleePartialRouteContinuationAfterActionChain(character, aborted);
        TryConsumePendingRouteActionOnlyTerminal(character, "action-chain", aborted);
    }

    internal static void NotifyAiMoveStepCompleted(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !IsAiControlledForCombat(character))
        {
            return;
        }

        TryCompleteConnectedFiringLineMovementStep(character, "move-step");
        TryCompleteSearchKnownTargetMovementStep(character, "move-step");
    }

    private static void SchedulePendingRouteActionOnlyTerminal(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 expectedDestination,
        int3 actualDestination,
        string phase,
        string result,
        bool consumeAfterAbort = false)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var memory = new PendingRouteActionOnlyTerminalMemory(
            pendingAction,
            expectedDestination,
            actualDestination,
            consumeAfterAbort,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        if (!IsActiveBattleContender(character))
        {
            return;
        }

        PendingRouteActionOnlyTerminalCache[character.Guid] = memory;
    }

    private static bool TryConsumePendingRouteActionOnlyTerminal(
        GameLocationCharacter character,
        string phase,
        bool aborted)
    {
        if (character?.RulesetCharacter == null ||
            !PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }


        if (IsConnectedFiringLineRoute(memory.PendingAction) &&
            TryHandlePendingConnectedFiringLineMovement(
                character,
                memory,
                phase,
                allowFinalFailure: false,
                out var connectedRouteClosed))
        {
            if (!connectedRouteClosed)
            {
                return true;
            }

            if (!PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out memory))
            {
                return true;
            }
        }

        if (IsSearchKnownTargetRoute(memory.PendingAction) &&
            TryHandlePendingSearchKnownTargetMovement(
                character,
                memory,
                phase,
                allowFinalFailure: false,
                out var searchRouteClosed))
        {
            if (!searchRouteClosed)
            {
                return true;
            }

            if (!PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out memory))
            {
                return true;
            }
        }

        if (aborted && !ShouldConsumeAbortedRouteActionOnlyTerminal(memory))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);

            if (IsSearchKnownTargetRoute(memory.PendingAction))
            {
                ScheduleAiProcessTurnRecovery(character, Id.TacticalMove, "SearchLostTargetAbort");
            }

            return true;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            GroundMeleeMoveSettlingCache.ContainsKey(character.Guid))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (!IsActiveBattleContender(character))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (IsConnectedFiringLineRoute(memory.PendingAction) &&
            TryConsumePendingConnectedFiringLineRouteTerminal(
                character,
                memory,
                phase,
                allowTerminalFallback: false))
        {
            return true;
        }


        return true;
    }

    internal static bool TryConsumePendingRouteTerminalAtAiProcessBoundary(
        GameLocationCharacter character,
        bool allowFinalFailure = false)
    {
        if (character?.RulesetCharacter == null ||
            !PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        const string phase = "ai-process";

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (IsConnectedFiringLineRoute(memory.PendingAction) &&
            TryHandlePendingConnectedFiringLineMovement(
                character,
                memory,
                phase,
                allowFinalFailure,
                out var connectedRouteClosed))
        {
            if (!connectedRouteClosed)
            {
                return true;
            }

            if (!PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out memory))
            {
                return true;
            }
        }

        if (IsSearchKnownTargetRoute(memory.PendingAction) &&
            TryHandlePendingSearchKnownTargetMovement(
                character,
                memory,
                phase,
                allowFinalFailure,
                out var searchRouteClosed))
        {
            if (!searchRouteClosed)
            {
                return true;
            }

            if (!PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out memory))
            {
                return true;
            }
        }

        if (ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            GroundMeleeMoveSettlingCache.ContainsKey(character.Guid))
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (HasCurrentPendingTerminalReadyOrDodge(character))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (IsConnectedFiringLineRoute(memory.PendingAction))
        {
            return TryConsumePendingConnectedFiringLineRouteTerminal(
                character,
                memory,
                phase,
                allowTerminalFallback: true);
        }

        var handled = TrySpendLeftoverActionEconomy(character, false, endTurnTerminal: true);
        var hasPendingTerminalLaunch = handled && HasPendingAiProcessTerminalLaunch(character);

        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);

        if (hasPendingTerminalLaunch)
        {
            TryConsumePendingAiProcessTerminalLaunch(character);
            return true;
        }


        return handled;
    }

    private static bool TryHandlePendingConnectedFiringLineMovement(
        GameLocationCharacter character,
        PendingRouteActionOnlyTerminalMemory memory,
        string phase,
        bool allowFinalFailure,
        out bool routeClosed)
    {
        routeClosed = false;

        if (character?.RulesetCharacter == null ||
            !IsConnectedFiringLineRoute(memory.PendingAction) ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
            !IsConnectedFiringLineRoute(pendingAction))
        {
            return false;
        }

        if (TryCompleteConnectedFiringLineMovementStep(character, phase).IsComplete)
        {
            routeClosed = true;
            return true;
        }

        if (allowFinalFailure)
        {
            if (character.LocationPosition == pendingAction.StartPosition)
            {
                CloseFailedConnectedFiringLineAwaitingMovement(
                    character,
                    pendingAction,
                    phase);
            }
            else
            {
                CloseConnectedFiringLineMoveResult(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination,
                    phase,
                    0);
            }

            routeClosed = true;
            return true;
        }

        return true;
    }

    private static ConnectedFiringLineCompletionResult TryCompleteConnectedFiringLineMovementStep(
        GameLocationCharacter character,
        string phase)
    {
        if (character?.RulesetCharacter == null ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
            !IsConnectedFiringLineRoute(pendingAction))
        {
            return new ConnectedFiringLineCompletionResult(
                ConnectedFiringLineCompletionKind.Unavailable,
                character?.LocationPosition ?? default);
        }

        var actualDestination = character.LocationPosition;

        if (actualDestination == pendingAction.StartPosition)
        {
            _ = phase;
            return new ConnectedFiringLineCompletionResult(
                ConnectedFiringLineCompletionKind.Pending,
                actualDestination);
        }

        CloseConnectedFiringLineMoveResult(
            character,
            pendingAction,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            phase,
            0);

        return new ConnectedFiringLineCompletionResult(
            actualDestination == pendingAction.ExpectedDestination
                ? ConnectedFiringLineCompletionKind.SettledReached
                : ConnectedFiringLineCompletionKind.SettledPartial,
            actualDestination);
    }

    private static bool TryHandlePendingSearchKnownTargetMovement(
        GameLocationCharacter character,
        PendingRouteActionOnlyTerminalMemory memory,
        string phase,
        bool allowFinalFailure,
        out bool routeClosed)
    {
        routeClosed = false;

        if (character?.RulesetCharacter == null ||
            !IsSearchKnownTargetRoute(memory.PendingAction) ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
            !IsSearchKnownTargetRoute(pendingAction))
        {
            return false;
        }

        if (TryCompleteSearchKnownTargetMovementStep(character, phase).IsComplete)
        {
            routeClosed = true;
            return true;
        }

        if (allowFinalFailure)
        {
            CloseSearchKnownTargetMoveResult(
                character,
                pendingAction,
                pendingAction.StartPosition,
                pendingAction.ExpectedDestination,
                phase,
                SearchKnownTargetCompletionKind.FailedNoMeaningfulMovement);

            routeClosed = true;
            return true;
        }

        return true;
    }

    private static SearchKnownTargetCompletionResult TryCompleteSearchKnownTargetMovementStep(
        GameLocationCharacter character,
        string phase)
    {
        if (character?.RulesetCharacter == null ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
            !IsSearchKnownTargetRoute(pendingAction))
        {
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Unavailable,
                default);
        }

        var actualDestination = character.LocationPosition;
        var progress = ComputeSearchKnownTargetProgress(character, pendingAction, actualDestination);

        if (actualDestination == pendingAction.StartPosition)
        {
            _ = phase;
            return new SearchKnownTargetCompletionResult(
                SearchKnownTargetCompletionKind.Pending,
                progress);
        }

        var hasMeaningfulProgress = progress.HasMeaningfulProgress;
        var hasValidatedAction = false;

        if (!hasMeaningfulProgress)
        {
            hasValidatedAction = HasSearchKnownTargetValidatedAction(character);
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

        CloseSearchKnownTargetMoveResult(
            character,
            pendingAction,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            phase,
            result);

        return new SearchKnownTargetCompletionResult(result, progress);
    }

    private static bool HasPendingSearchKnownTargetMovement(GameLocationCharacter character)
    {
        return character?.RulesetCharacter != null &&
               ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) &&
               IsSearchKnownTargetRoute(pendingAction);
    }

    private static bool HasMeaningfulSearchKnownTargetProgress(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination)
    {
        return character?.RulesetCharacter != null &&
               ComputeSearchKnownTargetProgress(character, pendingAction, actualDestination)
                   .HasMeaningfulProgress;
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

    private static string BuildLostTargetSearchAttemptKey(
        int round,
        int turnStamp,
        int3 start,
        int3 anchor)
    {
        return $"{round}:{turnStamp}:{FormatPosition(start)}:{FormatPosition(anchor)}";
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

        LostTargetSearchAttemptCache[character.Guid] =
            BuildLostTargetSearchAttemptKey(round, turnStamp, start, anchor);
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
            profile,
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
        var target = turnPlan.ActionProbe.Target ?? pendingAction.Target;

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

    private static void CloseFailedConnectedFiringLineAwaitingMovement(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        string phase)
    {
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        _ = phase;

        ActionLinkedMoveCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        RecordAiMoveFailure(character, pendingAction.StartPosition, pendingAction.ExpectedDestination);
        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            RouteMoveCompletionFlags.None,
            round,
            turnStamp);
    }

    private static bool TryConsumePendingConnectedFiringLineRouteTerminal(
        GameLocationCharacter character,
        PendingRouteActionOnlyTerminalMemory memory,
        string phase,
        bool allowTerminalFallback)
    {
        if (character?.RulesetCharacter == null ||
            !IsConnectedFiringLineRoute(memory.PendingAction))
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        if (TryUseTerminalReprobeHostileAction(character, turnPlan, battleService).Executed)
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (TryUseConnectedFiringLineRecovery(
                character,
                turnPlan,
                battleService,
                profile))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (TryUseLostTargetSearchRouteMove(
                character,
                turnPlan,
                battleService,
                profile,
                CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove,
                CombatAiRouteMoveSourceKind.SearchLostTarget,
                out _))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        HandleConnectedFiringLineCompletionFailure(
            character,
            turnPlan,
            memory);

        if (!allowTerminalFallback)
        {
            return true;
        }

        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
        var handled = TrySpendLeftoverActionEconomy(character, false, endTurnTerminal: true);
        var hasPendingTerminalLaunch = handled && HasPendingAiProcessTerminalLaunch(character);

        if (hasPendingTerminalLaunch)
        {
            TryConsumePendingAiProcessTerminalLaunch(character);
            return true;
        }


        return handled;
    }

    private static void HandleConnectedFiringLineCompletionFailure(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        PendingRouteActionOnlyTerminalMemory memory)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        if (IsConnectedFiringLinePlan(turnPlan))
        {
            RecordDisconnectedPositioningSeal(
                character,
                turnPlan);

            if (TryGetActiveDisconnectedPositioningSeal(character, out var seal))
            {
                ApplyDisconnectedPositioningMovementLock(
                    character,
                    seal,
                    "connected-firing-line");
            }
        }

    }

    internal static bool TryConsumePendingUtilityTerminalAtAiProcessBoundary(
        GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PendingUtilityTerminalContinuationCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.Round != currentRound || memory.TurnStamp != currentTurnStamp)
        {
            PendingUtilityTerminalContinuationCache.Remove(character.Guid);
            return true;
        }

        if (!IsActiveBattleContender(character))
        {
            PendingUtilityTerminalContinuationCache.Remove(character.Guid);
            return true;
        }

        if (ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            GroundMeleeMoveSettlingCache.ContainsKey(character.Guid))
        {
            return false;
        }

        var handled = TrySpendLeftoverActionEconomy(character, false, endTurnTerminal: true);
        var hasPendingTerminalLaunch = handled && HasPendingAiProcessTerminalLaunch(character);

        if (hasPendingTerminalLaunch)
        {
            TryConsumePendingAiProcessTerminalLaunch(character);
            return true;
        }


        return true;
    }

    private static bool TryConsumePendingRouteTerminalAtEndTurn(
        GameLocationCharacter character,
        out bool suppressEndTurn)
    {
        suppressEndTurn = false;

        if (character?.RulesetCharacter == null ||
            !PendingRouteActionOnlyTerminalCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }


        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            GroundMeleeMoveSettlingCache.ContainsKey(character.Guid))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        if (!IsActiveBattleContender(character))
        {
            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        PendingRouteActionOnlyTerminalCache.Remove(character.Guid);

        return true;
    }

    private static bool ShouldConsumeAbortedRouteActionOnlyTerminal(PendingRouteActionOnlyTerminalMemory memory)
    {
        return memory.ConsumeAfterAbort;
    }

    private static bool SchedulePendingAiProcessTerminalLaunch(
        GameLocationCharacter character,
        Id actionId,
        ReadyActionType readyActionType,
        PendingTerminalLaunchKind kind)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var memory = new PendingAiProcessTerminalLaunchMemory(actionId, readyActionType, kind, round, turnStamp);

        PendingAiProcessTerminalLaunchCache[character.Guid] = memory;

        return true;
    }

    private static bool HasPendingAiProcessTerminalLaunch(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PendingAiProcessTerminalLaunchCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        return memory.MatchesCurrentTurn(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool TryConsumePendingAiProcessTerminalLaunch(GameLocationCharacter character)
    {
        if (!HasPendingAiProcessTerminalLaunch(character))
        {
            return false;
        }

        TryBeginPendingAiProcessTerminalLaunch(character);
        return true;
    }

    private static void TryBeginPendingAiProcessTerminalLaunch(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PendingAiProcessTerminalLaunchCache.TryGetValue(character.Guid, out var memory))
        {
            return;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
            return;
        }

        if (!IsActiveBattleContender(character))
        {
            PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
            return;
        }

        if (ActionLinkedMoveCache.ContainsKey(character.Guid) ||
            GroundMeleeMoveSettlingCache.ContainsKey(character.Guid))
        {
            PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
            return;
        }

        var actionEconomy = BuildActionEconomySnapshot(character);

        if (!CanSpendTerminalMainAction(character, actionEconomy))
        {
            PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
            return;
        }


        if (memory.ActionId == Id.Dodge)
        {
            TryBeginPendingDodgeLaunch(character, memory, actionEconomy);
        }
        else
        {
            TryBeginPendingReadyLaunch(character, memory, actionEconomy);
        }
    }

    private static void TryBeginPendingDodgeLaunch(
        GameLocationCharacter character,
        PendingAiProcessTerminalLaunchMemory memory,
        CombatAiActionEconomySnapshot actionEconomy)
    {
        var dodgeStatus = character.GetActionStatus(Id.Dodge, ActionScope.Battle);

        if (!actionEconomy.DodgeAvailable || dodgeStatus != ActionStatus.Available)
        {
            PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
            return;
        }

        PendingFallbackDodgeConditionCache[character.Guid] =
            CollectDodgingConditionGuids(character.RulesetCharacter);
        PendingTerminalDodgeEndTurnCache[character.Guid] =
            new PendingTerminalDodgeEndTurnMemory(memory.Round, memory.TurnStamp);

        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
        character.MyExecuteActionDodge();

        if (!PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid))
        {
            PendingTerminalActionEndTurnSuppressCache.Add(character.Guid);
            return;
        }

        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);

    }

    private static void TryBeginPendingReadyLaunch(
        GameLocationCharacter character,
        PendingAiProcessTerminalLaunchMemory memory,
        CombatAiActionEconomySnapshot actionEconomy)
    {
        var readyStatus = character.GetActionStatus(Id.Ready, ActionScope.Battle);

        if (!actionEconomy.ReadyAvailable || readyStatus != ActionStatus.Available)
        {
            PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
            return;
        }

        PendingTerminalReadyEndTurnCache[character.Guid] =
            new PendingTerminalDodgeEndTurnMemory(memory.Round, memory.TurnStamp);

        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
        character.MyExecuteActionReady(memory.ReadyActionType);

        if (!PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid))
        {
            PendingTerminalActionEndTurnSuppressCache.Add(character.Guid);
            return;
        }

        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);

    }

    internal static void NotifyPendingTerminalActionAccepted(CharacterAction action)
    {
        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            action.ActionId is not (Id.Dodge or Id.Ready) ||
            !PendingAiProcessTerminalLaunchCache.TryGetValue(character.Guid, out var memory) ||
            memory.ActionId != action.ActionId ||
            !memory.MatchesCurrentTurn(GetCurrentBattleRound(), Math.Max(1, ObservedCombatMemoryTurnStamp)))
        {
            return;
        }

        PendingAiProcessTerminalLaunchAcceptedCache.Add(character.Guid);

    }

    private static bool TryGetActiveGroundMeleePartialRouteProgress(
        GameLocationCharacter character,
        out GroundMeleePartialRouteMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !GroundMeleePartialRouteCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            return true;
        }

        GroundMeleePartialRouteCache.Remove(character.Guid);
        memory = default;

        return false;
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

    internal static bool ShouldCancelAiTacticalMove(
        CharacterActionMove action,
        int3 start,
        int3 target)
    {
        var character = action?.ActingCharacter;

        EnsureCombatAiRuntimeCache(character);

        if (!IsAiControlledForCombat(character))
        {
            return false;
        }

        if (TryGetSearchKnownTargetOwnedMoveGate(character, start, target, out var searchMoveAllowed))
        {
            if (searchMoveAllowed)
            {
                return false;
            }

            RecordAiMoveFailure(character, start, target);
            return true;
        }

        if (TryGetActiveDisconnectedPositioningSeal(character, out var seal))
        {
            if (target.x == 0 && target.y == 0 && target.z == 0)
            {
                return ShouldBlockDisconnectedPositioningUnresolvedDestination(
                    action,
                    character,
                    seal,
                    start);
            }

            return ShouldBlockDisconnectedPositioningDestination(
                action,
                character,
                seal,
                start,
                target);
        }

        if (IsFailedAiMoveTarget(character, start, target))
        {
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
                ApplyPostThreatReturnSeal(character, start, target);
            }

            return true;
        }

        if (battleService != null && ShouldRejectTrafficBlockingMove(character, target, battleService, out _))
        {
            return true;
        }

        if (IsBacktrackingMove(character, start, target))
        {
            return true;
        }

        PendingAiMoveAttemptCache[character.Guid] = new AiMoveAttempt(start, target);

        return false;
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
            !IsAiControlledForCombat(character) ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
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

        return false;
    }

    internal static void RecordAiMoveResult(
        CharacterActionMove action,
        int3 start,
        int3 target,
        bool forceCloseNoMoveAfterSettling = false,
        int settleFrames = 0)
    {
        var character = action?.ActingCharacter;

        EnsureCombatAiRuntimeCache(character);

        if (!IsAiControlledForCombat(character))
        {
            return;
        }

        PendingAiMoveAttemptCache.Remove(character.Guid);
        var hadPendingActionLinkedMove =
            ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction);


        if (forceCloseNoMoveAfterSettling &&
            hadPendingActionLinkedMove &&
            IsGroundMeleePursuitTerminalRoute(pendingAction) &&
            character.LocationPosition == start)
        {
            ForceCloseNoMoveAfterMoveResult(
                character,
                pendingAction,
                start,
                target,
                Math.Max(0, settleFrames));
            return;
        }

        if (hadPendingActionLinkedMove &&
            IsGroundMeleeMoveSettlingRoute(pendingAction))
        {
            if (TryDeferGroundMeleeMoveSettling(character, pendingAction, "move-result"))
            {
                return;
            }

            if (TryFinalizeGroundMeleePursuitAtActualDestination(
                character,
                pendingAction,
                start,
                target,
                "move-result"))
            {
                return;
            }

            return;
        }

        if (hadPendingActionLinkedMove &&
            IsConnectedFiringLineRoute(pendingAction))
        {
            if (character.LocationPosition == start)
            {
                DeferConnectedFiringLineMoveResult(character, pendingAction, start, target, "move-result");
                return;
            }

            CloseConnectedFiringLineMoveResult(
                character,
                pendingAction,
                start,
                target,
                "move-result",
                Math.Max(0, settleFrames));
            return;
        }

        if (hadPendingActionLinkedMove &&
            IsSearchKnownTargetRoute(pendingAction))
        {
            if (TryCompleteSearchKnownTargetMovementStep(character, "move-result").IsComplete)
            {
                return;
            }

            DeferSearchKnownTargetMoveResult(character, pendingAction, start, target, "move-result");
            return;
        }

        if (character.LocationPosition == target)
        {
            UpdateTurnMovementProgress(character);
            if (hadPendingActionLinkedMove)
            {
            }

            TryCompleteActionLinkedMove(character);
            return;
        }

        if (hadPendingActionLinkedMove &&
            TryFinalizeRouteMoveAtActualDestination(character, pendingAction, start, target, "move-result"))
        {
            return;
        }

        TryApplyPendingRouteMovementLock(character, "move-result");
        ActionLinkedMoveCache.Remove(character.Guid);

        if (hadPendingActionLinkedMove)
        {
            CloseLateCompletionAndScheduleTerminal(
                character,
                pendingAction,
                start,
                target,
                "move-result");
            return;
        }

        RecordAiMoveFailure(character, start, target);
    }

    private static void ForceCloseNoMoveAfterMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 target,
        int settleFrames)
    {
        PendingAiMoveAttemptCache.Remove(character.Guid);
        ActionLinkedMoveCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);

        RecordAiMoveFailure(character, start, target);
        RecordGroundMeleeRouteFailure(
            character,
            pendingAction.Target,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination);
        RouteMoveDashBlockCache.Remove(character.Guid);
        TurnMovementProgressCache.Remove(character.Guid);

        var actualDestination = character.LocationPosition;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            RouteMoveCompletionFlags.NoMove | RouteMoveCompletionFlags.GroundMeleeNoMove,
            round,
            turnStamp);


        if (!IsActiveBattleContender(character))
        {
            return;
        }

        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            target,
            actualDestination,
            $"move-result-settled;settleFrames:{settleFrames}",
            "no-move",
            consumeAfterAbort: true);
    }

    private static void DeferConnectedFiringLineMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        string phase)
    {
        var actualDestination = character.LocationPosition;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            RouteMoveCompletionFlags.None,
            round,
            turnStamp);

        if (pendingAction.Continuation != CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove)
        {
            return;
        }

        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            expectedDestination,
            actualDestination,
            $"connected-firing-line-awaiting:{phase}",
            "deferred:connected firing line awaiting movement");
    }

    private static void CloseConnectedFiringLineMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        string phase,
        int settleFrames)
    {
        var actualDestination = character.LocationPosition;
        var completionKind = actualDestination == start
            ? ConnectedFiringLineCompletionKind.FailedNoMovementStep
            : GetConnectedFiringLineCompletionKindAtPosition(character, pendingAction, actualDestination);
        var failed = completionKind is ConnectedFiringLineCompletionKind.FailedNoMovementStep or
            ConnectedFiringLineCompletionKind.FailedNoAction;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        _ = settleFrames;

        ActionLinkedMoveCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
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


        if (!IsActiveBattleContender(character))
        {
            return;
        }

        if (pendingAction.Continuation != CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove)
        {
            return;
        }

        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            expectedDestination,
            actualDestination,
            $"connected-firing-line:{phase}",
            null);
    }

    private static void DeferSearchKnownTargetMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        string phase)
    {
        var actualDestination = character.LocationPosition;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            start,
            expectedDestination,
            RouteMoveCompletionFlags.None,
            round,
            turnStamp);

        if (pendingAction.Continuation != CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove)
        {
            return;
        }

        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            expectedDestination,
            actualDestination,
            $"search-awaiting:{phase}",
            "deferred:search movement awaiting step");
    }

    private static void CloseSearchKnownTargetMoveResult(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        string phase,
        SearchKnownTargetCompletionKind resultKind)
    {
        var actualDestination = character.LocationPosition;
        var failed = resultKind == SearchKnownTargetCompletionKind.FailedNoMeaningfulMovement;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var result = resultKind switch
        {
            SearchKnownTargetCompletionKind.SettledReached => "settled:search reached",
            SearchKnownTargetCompletionKind.SettledPartial => "settled:search partial",
            _ => "failed:no meaningful search movement"
        };

        ActionLinkedMoveCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);
        RecordLostTargetSearchAttempt(character, pendingAction);
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


        if (!IsActiveBattleContender(character))
        {
            return;
        }

        if (pendingAction.Continuation != CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove)
        {
            return;
        }

        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            expectedDestination,
            actualDestination,
            $"search:{phase}",
            result);
    }

    private static void CloseLateCompletionAndScheduleTerminal(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        string phase)
    {
        var actualDestination = character.LocationPosition;
        var noMove = actualDestination == start;
        var result = noMove ? "settled:no-move" : "settled:partial";
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


        if (!IsActiveBattleContender(character))
        {
            return;
        }

        if (pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove)
        {
            SchedulePendingRouteActionOnlyTerminal(
                character,
                pendingAction,
                expectedDestination,
                actualDestination,
                $"late-completion:{phase}",
                noMove ? "no-move" : "partial",
                consumeAfterAbort: noMove);
            return;
        }

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

    internal static void RecordFallbackReadyTriggered(
        GameLocationCharacter actor,
        GameLocationCharacter target)
    {
        // Kept as a stable patch hook; the compact turn plan does not need per-ready trigger state.
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

    internal static void RecordCombatAiActionExecution(CharacterAction action)
    {
        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null || !IsAiControlledForCombat(character))
        {
            return;
        }

        EnsureCombatAiRuntimeCache(character);

        var memory = new CombatAiActionExecutionMemory(
            action.ActionId,
            action.ActionType,
            character.GetActionTypeStatus(ActionType.Main),
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        var isPendingTerminalAction =
            IsPendingTerminalDodgeAction(action) ||
            IsPendingTerminalReadyAction(action);

        if ((memory.IsMainAction || IsMainActionId(memory.ActionId)) &&
            !IsActiveBattleContender(character) &&
            !isPendingTerminalAction)
        {
            return;
        }

        LastActionExecutionCache[character.Guid] = memory;

        if (memory.IsMainAction || IsMainActionId(memory.ActionId))
        {
            LastMainActionExecutionCache[character.Guid] = memory;
            IncrementActionUseCount(TurnMainActionUseCountCache, character);
            RecordRepeatedAttackActionExecution(action, memory);

            if (IsConnectedHostileActionExecution(action))
            {
                DisconnectedPositioningSealCache.Remove(character.Guid);
                DisconnectedPositioningMovementLockCache.Remove(character.Guid);
            }
        }

        if (memory.ActionType == ActionType.Bonus)
        {
            IncrementActionUseCount(TurnBonusActionUseCountCache, character);
        }
    }

    private static bool IsConnectedHostileActionExecution(CharacterAction action)
    {
        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            action.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain))
        {
            return false;
        }

        var target = action.ActionParams?.TargetCharacters.FirstOrDefault();

        if (target?.RulesetCharacter != null && target.Side != character.Side)
        {
            return true;
        }

        return action.ActionId is Id.CastMain or Id.PowerMain &&
               TryGetActionEffectDescription(action, out var effectDescription) &&
               effectDescription.TargetSide == Side.Enemy;
    }

    private static void RecordRepeatedAttackActionExecution(
        CharacterAction action,
        CombatAiActionExecutionMemory memory)
    {
        if (!TryGetRepeatableAttackAction(action, out var target, out var actionKind, out var actionIdentity))
        {
            return;
        }

        var character = action.ActingCharacter;
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

        if (repeat >= logThreshold)
        {
        }
    }

    private static bool TryGetRepeatableAttackAction(
        CharacterAction action,
        out GameLocationCharacter target,
        out CombatAiActionKind actionKind,
        out string actionIdentity)
    {
        target = null;
        actionKind = CombatAiActionKind.None;
        actionIdentity = null;

        var character = action?.ActingCharacter;
        var actionParams = action?.ActionParams;

        if (character?.RulesetCharacter == null || actionParams == null)
        {
            return false;
        }

        target = actionParams.TargetCharacters.FirstOrDefault();

        if (target?.RulesetCharacter == null || target.Side == character.Side)
        {
            return false;
        }

        if (action.ActionId == Id.AttackMain)
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

        if (action.ActionId != Id.CastMain)
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

    internal static bool ShouldBlockInvalidAiMainAction(
        CharacterAction action,
        out CombatAiMainActionBlockKind blockKind)
    {
        blockKind = CombatAiMainActionBlockKind.None;

        var character = action?.ActingCharacter;

        if (!IsAdvancedCombatAiEnabled ||
            character?.RulesetCharacter == null ||
            !IsAiControlledForCombat(character) ||
            !IsMainActionId(action.ActionId))
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            blockKind = CombatAiMainActionBlockKind.Other;

            if (TryNormalizePendingTerminalActionForInactiveContender(character))
            {
                return true;
            }

            return true;
        }

        if (ShouldBlockPostRecoveryEndTurnMainAction(action, out blockKind))
        {
            return true;
        }

        if (ShouldBlockMainActionDuringPendingTurnRecovery(action, out blockKind))
        {
            return true;
        }

        if (!TryConsumePendingResidualMainAction(action))
        {
            if (TryGetBlockedVanillaMainActionValidation(action, out var vanillaValidation))
            {
                blockKind = vanillaValidation.BlockKind == CombatAiMainActionBlockKind.None
                    ? CombatAiMainActionBlockKind.Other
                    : vanillaValidation.BlockKind;

                if (ShouldContinueTerminalAfterBlockedMainAction(action, vanillaValidation))
                {
                    MarkPendingUtilityTerminalContinuation(character, action.ActionId);
                }


                return true;
            }

            return false;
        }

        var validation = ValidateMainActionBeforeExecution(action);

        if (validation.IsValid)
        {
            return false;
        }

        blockKind = validation.BlockKind == CombatAiMainActionBlockKind.None
            ? CombatAiMainActionBlockKind.Other
            : validation.BlockKind;

        if (ShouldContinueTerminalAfterBlockedMainAction(action, validation))
        {
            MarkPendingUtilityTerminalContinuation(character, action.ActionId);
        }


        return true;
    }

    private static bool ShouldBlockPostRecoveryEndTurnMainAction(
        CharacterAction action,
        out CombatAiMainActionBlockKind blockKind)
    {
        blockKind = CombatAiMainActionBlockKind.None;

        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            action.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain) ||
            !HasCurrentPostRecoveryEndTurnMainActionSeal(character))
        {
            return false;
        }

        PendingResidualMainActionCache.Remove(character.Guid);
        PendingUtilityTerminalContinuationCache.Remove(character.Guid);
        TryNormalizePostRecoveryCommittedMainAction(character);
        TryPrunePostRecoveryEndTurnCurrentChain(character);
        blockKind = CombatAiMainActionBlockKind.MainAlreadySpent;


        return true;
    }

    private static bool HasCurrentPostRecoveryEndTurnMainActionSeal(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PostRecoveryEndTurnMainActionSealCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.Round == currentRound && memory.TurnStamp == currentTurnStamp)
        {
            return true;
        }

        PostRecoveryEndTurnMainActionSealCache.Remove(character.Guid);

        return false;
    }

    private static bool ShouldBlockMainActionDuringPendingTurnRecovery(
        CharacterAction action,
        out CombatAiMainActionBlockKind blockKind)
    {
        blockKind = CombatAiMainActionBlockKind.None;

        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            action.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain) ||
            !TryGetCurrentPendingAiProcessTurnRecovery(character, out var recovery))
        {
            return false;
        }

        var hasCommittedMainAction = TryGetCommittedNonTerminalMainActionThisTurn(
            character,
            out _,
            out var committedSource);
        var mainUseCount = GetActionUseCount(TurnMainActionUseCountCache, character);

        if (!hasCommittedMainAction && mainUseCount <= 0)
        {
            return false;
        }

        PendingResidualMainActionCache.Remove(character.Guid);
        blockKind = CombatAiMainActionBlockKind.MainAlreadySpent;


        return true;
    }

    internal static bool ShouldBlockDisconnectedAiMovementAction(CharacterAction action)
    {
        if (!IsAdvancedCombatAiEnabled)
        {
            return false;
        }

        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character) ||
            !TryGetActiveDisconnectedPositioningSeal(character, out var seal))
        {
            return false;
        }

        if (action is CharacterActionMoveStepWalk)
        {
            return false;
        }

        if (action is CharacterActionMove move)
        {
            if (TryGetSearchKnownTargetOwnedMoveGate(
                    character,
                    character.LocationPosition,
                    move.DestinationPosition,
                    out var searchMoveAllowed))
            {
                if (searchMoveAllowed)
                {
                    return false;
                }

                RecordAiMoveFailure(character, character.LocationPosition, move.DestinationPosition);
                return true;
            }

            return ShouldBlockDisconnectedPositioningDestination(
                action,
                character,
                seal,
                move.DestinationPosition);
        }

        if (action is CharacterActionMoveStepBase)
        {
            var destination = character.DestinationPosition;

            if (destination.x == 0 && destination.y == 0 && destination.z == 0)
            {
                return false;
            }

            if (TryGetSearchKnownTargetOwnedMoveGate(
                    character,
                    character.LocationPosition,
                    destination,
                    out var searchMoveAllowed))
            {
                if (searchMoveAllowed)
                {
                    return false;
                }

                RecordAiMoveFailure(character, character.LocationPosition, destination);
                return true;
            }

            return ShouldBlockDisconnectedPositioningDestination(
                action,
                character,
                seal,
                destination);
        }

        return false;
    }

    private static bool IsRouteOwnedSearchKnownTargetMove(GameLocationCharacter character, int3 destination)
    {
        return character?.RulesetCharacter != null &&
               ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) &&
               pendingAction.MovementGoal == CombatAiMovementGoalKind.SearchKnownTarget &&
               pendingAction.RouteMoveSource == CombatAiRouteMoveSourceKind.SearchLostTarget &&
               pendingAction.ExpectedDestination == destination;
    }

    private static bool TryGetSearchKnownTargetOwnedMoveGate(
        GameLocationCharacter character,
        int3 start,
        int3 destination,
        out bool allowed)
    {
        allowed = false;

        if (character?.RulesetCharacter == null ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
            !IsSearchKnownTargetRoute(pendingAction))
        {
            return false;
        }

        if (destination.x == 0 && destination.y == 0 && destination.z == 0)
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
        ApplyDisconnectedPositioningMovementLock(character, seal, "move-execute");

        return true;
    }

    private static bool ShouldBlockDisconnectedPositioningUnresolvedDestination(
        CharacterAction action,
        GameLocationCharacter character,
        DisconnectedPositioningSealMemory seal,
        int3 start)
    {
        _ = action;
        _ = start;
        ApplyDisconnectedPositioningMovementLock(character, seal, "move-execute:unresolved");

        return true;
    }

    private static bool ApplyDisconnectedPositioningMovementLock(
        GameLocationCharacter character,
        DisconnectedPositioningSealMemory seal,
        string origin)
    {
        var key = $"{seal.Round}:{seal.TurnStamp}";

        if (character?.RulesetCharacter != null &&
            DisconnectedPositioningMovementLockCache.TryGetValue(character.Guid, out var existingKey) &&
            existingKey.StartsWith(key + ";", StringComparison.Ordinal))
        {
            return false;
        }

        if (!CanApplyDisconnectedPositioningMovementLock(character, seal))
        {
            return false;
        }

        var remainingTacticalMoves = character.RemainingTacticalMoves;

        character.UsedTacticalMoves += remainingTacticalMoves;
        character.UsedTacticalMovesChanged?.Invoke(character);
        DisconnectedPositioningMovementLockCache[character.Guid] = $"{key};origin:{origin}";

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

    private static bool IsPendingTerminalDodgeAction(CharacterAction action)
    {
        return action?.ActionId == Id.Dodge &&
               TryGetPendingTerminalDodgeEndTurn(action.ActingCharacter, out _);
    }

    private static bool IsPendingTerminalReadyAction(CharacterAction action)
    {
        return action?.ActionId == Id.Ready &&
               TryGetPendingTerminalReadyEndTurn(action.ActingCharacter, out _);
    }

    private static bool TryGetPendingTerminalDodgeEndTurn(
        GameLocationCharacter character,
        out PendingTerminalDodgeEndTurnMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !PendingTerminalDodgeEndTurnCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        return memory.Round == GetCurrentBattleRound() &&
               memory.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool TryGetPendingTerminalReadyEndTurn(
        GameLocationCharacter character,
        out PendingTerminalDodgeEndTurnMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !PendingTerminalReadyEndTurnCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        return memory.Round == GetCurrentBattleRound() &&
               memory.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool TryGetBlockedVanillaMainActionValidation(
        CharacterAction action,
        out CombatAiMainActionValidation validation)
    {
        validation = default;

        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (action.ActionId == Id.AttackMain &&
            TryGetActiveUbResidualMainAttackCommit(character, out var ubResidualAttack))
        {
            validation = new CombatAiMainActionValidation(
                false);
            return true;
        }

        if (action.ActionId == Id.DashMain &&
            TryGetActiveRouteMoveDashBlock(character, out var dashBlock))
        {
            if (TryGetSearchKnownTargetDashContinuationValidation(
                    character,
                    dashBlock,
                    out var searchDashValidation))
            {

                if (searchDashValidation.IsValid)
                {
                    return false;
                }

                validation = searchDashValidation;
                return true;
            }

            validation = new CombatAiMainActionValidation(
                false);
            return true;
        }

        if (action.ActionId == Id.DashMain &&
            TryGetActiveDisconnectedPositioningSeal(character, out var disconnectedSeal))
        {
            if (character.LocationPosition != disconnectedSeal.StartPosition)
            {
            }

            validation = new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.DashDisconnectedPositioning);
            return true;
        }

        if (action.ActionId == Id.AttackMain)
        {
            validation = ValidateMainActionBeforeExecution(action);

            if (validation.IsValid)
            {
                return false;
            }

            if (!IsAttackMainUnavailableValidation(validation) &&
                !IsRangedWhileMeleeReachableValidation(validation))
            {
                return false;
            }

            return true;
        }

        if (action.ActionId is not (Id.CastMain or Id.PowerMain))
        {
            return false;
        }

        validation = ValidateMainActionBeforeExecution(action);

        if (validation.IsValid ||
            !IsUtilityActionBlockedByValidation(validation))
        {
            return false;
        }


        return true;
    }

    private static bool ShouldContinueTerminalAfterBlockedMainAction(
        CharacterAction action,
        CombatAiMainActionValidation validation)
    {
        if (action?.ActionId == Id.DashMain)
        {
            return validation.BlockKind != CombatAiMainActionBlockKind.DashDisconnectedPositioning;
        }

        return IsUtilityActionBlockedByValidation(validation);
    }

    private static bool IsUtilityActionBlockedByValidation(CombatAiMainActionValidation validation)
    {
        return validation.BlockKind is
            CombatAiMainActionBlockKind.LowValueUtility or
            CombatAiMainActionBlockKind.UtilityBlocked or
            CombatAiMainActionBlockKind.RedundantUtility;
    }

    private static bool IsRangedWhileMeleeReachableValidation(CombatAiMainActionValidation validation)
    {
        return validation.BlockKind is
            CombatAiMainActionBlockKind.RangedWhileMeleeReachable or
            CombatAiMainActionBlockKind.RangedWhileMeleePursuitAvailable;
    }

    private static bool IsAttackMainUnavailableValidation(CombatAiMainActionValidation validation)
    {
        return validation.BlockKind == CombatAiMainActionBlockKind.AttackMainUnavailable;
    }

    private static void MarkPendingResidualMainAction(
        GameLocationCharacter character,
        Id actionId,
        string source = null)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PendingResidualMainActionCache[character.Guid] = new PendingResidualMainAction(
            actionId,
            source,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static void MarkPendingUtilityTerminalContinuation(
        GameLocationCharacter character,
        Id actionId)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PendingUtilityTerminalContinuationCache[character.Guid] = new PendingUtilityTerminalContinuation(
            actionId,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

    }

    internal static void NotifyBlockedInvalidAiMainAction(
        CharacterAction action,
        CombatAiMainActionBlockKind blockKind)
    {
        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null)
        {
            return;
        }

        if (!ShouldClearBlockedInvalidMainCaches(blockKind))
        {
            return;
        }

        PendingResidualMainActionCache.Remove(character.Guid);
        PendingUtilityTerminalContinuationCache.Remove(character.Guid);

    }

    internal static bool TryCloseTurnAfterBlockedInvalidAiMainAction(
        CharacterAction action,
        CombatAiMainActionBlockKind blockKind)
    {
        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            !ShouldClearBlockedInvalidMainCaches(blockKind))
        {
            return false;
        }

        PendingResidualMainActionCache.Remove(character.Guid);
        PendingUtilityTerminalContinuationCache.Remove(character.Guid);

        if (blockKind != CombatAiMainActionBlockKind.MainAlreadySpent)
        {

            return false;
        }

        if (!TryGetCurrentPendingAiProcessTurnRecovery(character, out var recovery))
        {
            return false;
        }


        return TryRecoverAiProcessTurnProgression(
            character,
            recovery.Reason,
            action,
            ignoreSingleCurrentAction: true);
    }

    private static bool ShouldClearBlockedInvalidMainCaches(CombatAiMainActionBlockKind blockKind)
    {
        return blockKind is
            CombatAiMainActionBlockKind.MainAlreadySpent or
            CombatAiMainActionBlockKind.AttackMainUnavailable or
            CombatAiMainActionBlockKind.RangedWhileMeleeReachable or
            CombatAiMainActionBlockKind.RangedWhileMeleePursuitAvailable;
    }

    private static bool HasPendingUtilityTerminalContinuation(GameLocationCharacter character)
    {
        return character?.RulesetCharacter != null &&
               PendingUtilityTerminalContinuationCache.TryGetValue(character.Guid, out var pending) &&
               pending.Round == GetCurrentBattleRound() &&
               pending.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool TryConsumePendingUtilityTerminalContinuation(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PendingUtilityTerminalContinuationCache.TryGetValue(character.Guid, out var pending))
        {
            return false;
        }

        PendingUtilityTerminalContinuationCache.Remove(character.Guid);

        if (pending.Round != GetCurrentBattleRound() ||
            pending.TurnStamp != Math.Max(1, ObservedCombatMemoryTurnStamp))
        {
            return false;
        }

        return true;
    }

    private static bool TryConsumePendingResidualMainAction(CharacterAction action)
    {
        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null ||
            !PendingResidualMainActionCache.TryGetValue(character.Guid, out var pending))
        {
            return false;
        }

        PendingResidualMainActionCache.Remove(character.Guid);

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!pending.Matches(action.ActionId, round, turnStamp))
        {
            return false;
        }

        if (action.ActionId == Id.AttackMain)
        {
            UbResidualMainAttackCommitCache[character.Guid] = new UbResidualMainAttackCommitMemory(
                pending.Source,
                round,
                turnStamp);
        }

        return true;
    }

    private static bool TryGetActiveUbResidualMainAttackCommit(
        GameLocationCharacter character,
        out UbResidualMainAttackCommitMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !UbResidualMainAttackCommitCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (memory.MatchesCurrentTurn(round, turnStamp))
        {
            return true;
        }

        UbResidualMainAttackCommitCache.Remove(character.Guid);
        memory = default;
        return false;
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

    private static CombatAiMainActionValidation ValidateMainActionBeforeExecution(CharacterAction action)
    {
        var character = action?.ActingCharacter;
        var target = action?.ActionParams?.TargetCharacters.FirstOrDefault();

        if (character?.RulesetCharacter == null)
        {
            return new CombatAiMainActionValidation(true);
        }

        if (action.ActionId is Id.Ready or Id.Dodge)
        {
            return new CombatAiMainActionValidation(true);
        }

        if (action.ActionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain))
        {
            return new CombatAiMainActionValidation(true);
        }

        if (action.ActionId != Id.AttackMain &&
            HasCommittedMainActionThisTurn(character))
        {
            return new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.MainAlreadySpent);
        }

        if (action.ActionId != Id.AttackMain &&
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available)
        {
            return new CombatAiMainActionValidation(true);
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            return new CombatAiMainActionValidation(true);
        }

        if (target?.RulesetCharacter == null || target.Side == character.Side)
        {
            return ValidateNonHostileMainActionBeforeExecution(character, target, action, battleService);
        }

        return action.ActionId switch
        {
            Id.AttackMain => ValidateAttackMainBeforeExecution(character, target, action.ActionParams, battleService),
            Id.CastMain => ValidateCastMainBeforeExecution(character, target, action.ActionParams, battleService),
            Id.PowerMain => ValidatePowerMainBeforeExecution(character, target, action.ActionParams),
            _ => new CombatAiMainActionValidation(true)
        };
    }

    private static CombatAiMainActionValidation ValidateNonHostileMainActionBeforeExecution(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CharacterAction action,
        IGameLocationBattleService battleService)
    {
        if (action.ActionId is not (Id.CastMain or Id.PowerMain))
        {
            return new CombatAiMainActionValidation(true);
        }

        if (TryGetActionEffectDescription(action, out var actionEffectDescription) &&
            actionEffectDescription.TargetSide == Side.Enemy)
        {
            return new CombatAiMainActionValidation(true);
        }

        var utilityTarget = target ?? character;
        var utilityValidation = ValidateUtilityAction(character, utilityTarget, action);

        if (utilityValidation.IsBlocked)
        {
            return new CombatAiMainActionValidation(
                false,
                utilityValidation.BlockKind);
        }

        var targetAssessment = BuildSelfAssessment(utilityTarget);

        if (targetAssessment.IsBloodied ||
            targetAssessment.IsCritical ||
            targetAssessment.HasSeriousCondition)
        {
            return new CombatAiMainActionValidation(true);
        }

        return new CombatAiMainActionValidation(true);
    }

    private static UtilityActionValidation ValidateUtilityAction(
        GameLocationCharacter actor,
        GameLocationCharacter target,
        CharacterAction action)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            action?.ActionParams == null ||
            action.ActionId is not (Id.CastMain or Id.PowerMain) ||
            !TryGetActionEffectDescription(action, out var effectDescription))
        {
            return new UtilityActionValidation(false);
        }

        if (HasEquivalentActiveEffectOrCondition(target.RulesetCharacter, effectDescription))
        {
            return new UtilityActionValidation(
                true,
                CombatAiMainActionBlockKind.RedundantUtility);
        }

        if (HasEquivalentSenseUtility(target.RulesetCharacter, effectDescription))
        {
            return new UtilityActionValidation(
                true,
                CombatAiMainActionBlockKind.RedundantUtility);
        }

        if (IsLowValueSelfUtility(effectDescription) &&
            HasLeftoverActionCombatContext(actor))
        {
            return new UtilityActionValidation(
                true,
                CombatAiMainActionBlockKind.LowValueUtility);
        }

        return new UtilityActionValidation(false);
    }

    private static bool TryGetActionEffectDescription(CharacterAction action, out EffectDescription effectDescription)
    {
        effectDescription = null;

        var effect = action?.ActionParams?.RulesetEffect ?? action?.ActionParams?.activeEffect;

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

    private static CombatAiMainActionValidation ValidateAttackMainBeforeExecution(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CharacterActionParams actionParams,
        IGameLocationBattleService battleService)
    {
        var mode = actionParams?.AttackMode;

        if (mode == null)
        {
            return new CombatAiMainActionValidation(false);
        }

        var attackEvaluationParams = new BattleDefinitions.AttackEvaluationParams();
        var modifier = new ActionModifier();
        var attackMainStatus = character.GetActionStatus(Id.AttackMain, ActionScope.Battle);

        if (attackMainStatus != ActionStatus.Available)
        {
            return new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.AttackMainUnavailable);
        }

        if (!ValidatorsWeapon.IsMelee(mode) &&
            ShouldBlockRangedAttackBecauseMeleeReachable(character, target, battleService, out var blockKind))
        {
            return new CombatAiMainActionValidation(
                false,
                blockKind);
        }

        if (ValidatorsWeapon.IsMelee(mode))
        {
            attackEvaluationParams.FillForPhysicalReachAttack(
                character,
                character.LocationPosition,
                mode,
                target,
                target.LocationPosition,
                modifier);
        }
        else
        {
            attackEvaluationParams.FillForPhysicalRangeAttack(
                character,
                character.LocationPosition,
                mode,
                target,
                target.LocationPosition,
                modifier);
        }

        return battleService.CanAttack(attackEvaluationParams)
            ? new CombatAiMainActionValidation(true)
            : new CombatAiMainActionValidation(false);
    }

    private static bool ShouldBlockRangedAttackBecauseMeleeReachable(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService,
        out CombatAiMainActionBlockKind blockKind)
    {
        blockKind = CombatAiMainActionBlockKind.None;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        var capabilityCatalog = BuildCapabilityCatalog(character);
        var profile = BuildProfile(character);

        if (!ShouldPreferMeleeAction(profile, capabilityCatalog))
        {
            return false;
        }

        var attackMainStatus = character.GetActionStatus(Id.AttackMain, ActionScope.Battle);

        if (attackMainStatus != ActionStatus.Available)
        {
            return false;
        }

        if (CanUseActionKindAtPosition(
                character,
                character.LocationPosition,
                target,
                CombatAiActionKind.Melee,
                battleService))
        {
            blockKind = CombatAiMainActionBlockKind.RangedWhileMeleeReachable;
            return true;
        }

        if (!TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress))
        {
            return false;
        }

        foreach (var candidate in movementProgress.EnumerateAcceptedMoveCandidates())
        {
            if (candidate.TurnsToAction != 0 ||
                !CanUseActionKindAtPosition(
                    character,
                    candidate.Position,
                    target,
                    CombatAiActionKind.Melee,
                    battleService))
            {
                continue;
            }

            blockKind = CombatAiMainActionBlockKind.RangedWhileMeleeReachable;
            return true;
        }

        if (HasImprovingMeleePursuit(character))
        {
            blockKind = CombatAiMainActionBlockKind.RangedWhileMeleePursuitAvailable;
            return true;
        }

        return false;
    }

    private static bool HasImprovingMeleePursuit(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
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

        foreach (var candidate in movementProgress.EnumerateLongRoutePursuitCandidates())
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
            character.RemainingTacticalMoves <= 0)
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

    private static ActionStatus GetAttackMainActionStatusForAi(GameLocationCharacter character, ActionStatus mainStatus)
    {
        _ = mainStatus;

        if (character?.RulesetCharacter == null)
        {
            return ActionStatus.Unavailable;
        }

        return character.GetActionStatus(Id.AttackMain, ActionScope.Battle);
    }

    private static CombatAiMainActionValidation ValidateCastMainBeforeExecution(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CharacterActionParams actionParams,
        IGameLocationBattleService battleService)
    {
        var effect = actionParams?.RulesetEffect ?? actionParams?.activeEffect;

        if (effect is not RulesetEffectSpell spellEffect)
        {
            return new CombatAiMainActionValidation(true);
        }

        var spell = spellEffect.SpellDefinition;

        if (!IsAtWillAttackCantripForAi(character.RulesetCharacter, spell))
        {
            return new CombatAiMainActionValidation(true);
        }

        var attackParams = new BattleDefinitions.AttackEvaluationParams();
        var modifier = new ActionModifier();
        var effectDescription = PowerBundle.ModifySpellEffect(spell, character.RulesetCharacter);

        if (effectDescription == null)
        {
            return new CombatAiMainActionValidation(false);
        }

        attackParams.FillForMagic(
            character,
            character.LocationPosition,
            effectDescription,
            spell.Name,
            target,
            target.LocationPosition,
            modifier);

        if (!battleService.CanAttack(attackParams))
        {
            return new CombatAiMainActionValidation(false);
        }

        return new CombatAiMainActionValidation(true);
    }

    private static CombatAiMainActionValidation ValidatePowerMainBeforeExecution(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CharacterActionParams actionParams)
    {
        var effect = actionParams?.RulesetEffect ?? actionParams?.activeEffect;

        if (effect is not RulesetEffectPower powerEffect)
        {
            return new CombatAiMainActionValidation(true);
        }

        var power = powerEffect.PowerDefinition;
        var effectDescription = power?.EffectDescription;

        if (power == null ||
            power.RechargeRate != RechargeRate.AtWill ||
            effectDescription?.TargetSide != Side.Enemy)
        {
            return new CombatAiMainActionValidation(true);
        }

        return target?.RulesetCharacter != null && target.Side != character.Side
            ? new CombatAiMainActionValidation(true)
            : new CombatAiMainActionValidation(false);
    }

    private static bool IsAtWillAttackCantripForAi(RulesetCharacter rulesetCharacter, SpellDefinition spell)
    {
        if (rulesetCharacter == null || spell == null)
        {
            return false;
        }

        var cantrips = new List<SpellDefinition>();

        rulesetCharacter.EnumerateReadyAttackCantrips(cantrips);

        foreach (var cantrip in OrderAtWillAttackCantrips(rulesetCharacter, cantrips))
        {
            if (!string.Equals(cantrip?.Name, spell.Name, StringComparison.Ordinal) ||
                !rulesetCharacter.CanCastCantrip(cantrip, out _))
            {
                continue;
            }

            return true;
        }

        return false;
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
        out CombatAiActionExecutionMemory memory,
        out string source)
    {
        if (TryGetCurrentTurnActionMemory(character, LastMainActionExecutionCache, out memory) &&
            IsNonTerminalMainActionId(memory.ActionId))
        {
            source = "lastMain";
            return true;
        }

        if (TryGetCurrentTurnActionMemory(character, LastActionExecutionCache, out memory) &&
            IsNonTerminalMainActionId(memory.ActionId))
        {
            source = "lastAction";
            return true;
        }

        memory = default;
        source = "none";
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
                canAutoAct,
                isAiControlled);
        }

        var mainStatus = character.GetActionTypeStatus(ActionType.Main);
        var bonusStatus = character.GetActionTypeStatus(ActionType.Bonus);
        var attackMainStatus = GetAttackMainActionStatusForAi(character, mainStatus);
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
            canAutoAct,
            isAiControlled);
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

        var hasCurrentPendingReady = TryGetPendingTerminalReadyEndTurn(character, out _);
        var hasCurrentPendingDodge = TryGetPendingTerminalDodgeEndTurn(character, out _);

        if (hasCurrentPendingReady || hasCurrentPendingDodge)
        {
            return false;
        }

        if (TryGetCommittedNonTerminalMainActionThisTurn(character, out var committedMainAction, out var committedSource))
        {
            return false;
        }

        if (TryGetCompletedTerminalMainActionThisTurn(character, out var completedTerminalAction))
        {
            return false;
        }

        if (actionEconomy.MainActionType != ActionStatus.Available)
        {
            return false;
        }

        if (actionEconomy.MainUseCount > 0)
        {
            return false;
        }

        return true;
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
            ? new AttackPositionKey(attacker.Guid, attackerPosition, target.Guid, targetPosition)
            : default;

        if (canUseCache && MeleeAttackPositionCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        foreach (var mode in attacker.RulesetCharacter.AttackModes)
        {
            if (mode == null || !ValidatorsWeapon.IsMelee(mode))
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

    internal static bool IsUnreachableFlyingTargetForMelee(
        GameLocationCharacter actor,
        GameLocationCharacter enemy,
        int3 actorPosition,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null ||
            enemy?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        if (!HasFlightContext(actor, enemy, actorPosition))
        {
            return false;
        }

        return !CanAttackInMeleeFromPosition(actor, actorPosition, enemy, enemy.LocationPosition, battleService);
    }

    internal static bool TryAutoSuspendFlight(GameLocationCharacter character)
    {
        if (!IsAdvancedCombatAiFlightEnabled ||
            !Main.Settings.AllowFlightSuspend ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsAiControlledForCombat(character) ||
            character?.RulesetCharacter is not { } rulesetCharacter ||
            !rulesetCharacter.HasSuspendableFlightCondition())
        {
            return false;
        }

        var profile = BuildProfile(character);
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null ||
            profile.PrefersAerialCombat ||
            !HasVisibleFlightContext(character) ||
            HasAnyUsableAttackAgainstVisibleEnemies(character, battleService))
        {
            return false;
        }

        var powerFlightSuspend = DatabaseHelper.GetDefinition<FeatureDefinitionPower>("PowerFlightSuspend");
        var usablePower = PowerProvider.Get(powerFlightSuspend, rulesetCharacter);

        if (usablePower == null)
        {
            return false;
        }

        character.MyExecuteActionPowerNoCost(usablePower, character);
        ClearTurnCache(character);

        return true;
    }

    internal static bool TryAutoResumeFlight(GameLocationCharacter character)
    {
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
        CombatAiRouteMoveSourceKind source)
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

        character.MyExecuteActionTacticalMove(
            destination,
            aborted => OnAiTacticalMoveActionChainExecuted(character, aborted, source));

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

        var turnPlan = BuildCombatAiTurnPlan(actor, profile, battleService);
        var isGroundMeleePursuit = IsGroundMeleePursuitPlan(turnPlan);

        if (IsAdvancedCombatAiEnabled &&
            evaluationSource == CombatAiFreeJumpEvaluationSource.AiMovePathfinding &&
            !FreeJumpContext.IsForcedAiFreeJumpTarget(actor, destination) &&
            !isGroundMeleePursuit)
        {
            if (profile.IsMeleeSpecialist)
            {
            }

            return false;
        }

        if (isGroundMeleePursuit)
        {
            var target = turnPlan.ActionProbe.Target;
            var preferredAvailable =
                target?.RulesetCharacter != null &&
                CanUseActionKindAtPosition(
                    actor,
                    destination,
                    target,
                    turnPlan.ActionProbe.PreferredAction,
                    battleService);
            var backupAvailable =
                target?.RulesetCharacter != null &&
                !preferredAvailable &&
                CanUseActionKindAtPosition(
                    actor,
                    destination,
                    target,
                    turnPlan.ActionProbe.BackupAction,
                    battleService);

            if (!TryValidateGroundMeleeJumpRouteCandidate(
                    actor,
                    turnPlan,
                    start,
                    destination,
                    preferredAvailable || backupAvailable,
                    out var isGroundMeleeRouteImprovement))
            {
                return false;
            }

            if (isGroundMeleeRouteImprovement)
            {
                GroundMeleeJumpRouteAvailableCache.Add(actor.Guid);
            }
        }

        if (TryComputeTurnPlanMovementScore(actor, profile, destination, battleService, turnPlan, out var movementGoalScore))
        {
            if (movementGoalScore <= 0f && turnPlan.MovementPlan.HasGoal)
            {
                var currentGoalDistance = ComputeGridDistance(start, turnPlan.MovementPlan.TargetPosition);
                var destinationGoalDistance = ComputeGridDistance(destination, turnPlan.MovementPlan.TargetPosition);
                var bypassesTowardGoal = bypassesObstacle &&
                                         destinationGoalDistance <=
                                         currentGoalDistance + MovementGoalRegressionTolerance;

                if (!bypassesTowardGoal)
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
                var improvement = currentFacts.NearestEnemyDistance - destinationFacts.NearestEnemyDistance;

                score += Mathf.Min(0.22f, improvement * 0.05f);
            }

            if ((profile.PrefersDistance || emergency) && movesAway)
            {
                var improvement = destinationFacts.NearestEnemyDistance - currentFacts.NearestEnemyDistance;

                score += Mathf.Min(0.18f, improvement * 0.04f);
            }

            if (destination.y > start.y && (destinationFacts.CanAttack || profile.PrefersDistance))
            {
                score += FreeJumpHighGroundScore;
            }

            if (bypassesObstacle &&
                (canGainAttack || reducesMeleeThreat || reducesRecentMeleeThreat || improvesCover || movesCloser || movesAway))
            {
                score += FreeJumpObstacleBypassScore;
            }
        }
        else if (IsAdvancedCombatAiPositioningEnabled &&
                 TryGetLastKnownEnemyPosition(actor, out var lastKnownEnemyPosition))
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

        var minimumScore = IsAdvancedCombatAiPositioningEnabled || IsAdvancedCombatAiFlightEnabled
            ? FreeJumpMinimumPositioningScore
            : FreeJumpMinimumBaselineScore;

        if (canGainAttack)
        {
            minimumScore = Math.Min(minimumScore, FreeJumpMinimumActionEconomyScore);
        }
        else if (bypassesObstacle && (movesCloser || reducesMeleeThreat || reducesRecentMeleeThreat || improvesCover))
        {
            minimumScore = Math.Min(minimumScore, FreeJumpMinimumPositioningScore);
        }

        if (IsAdvancedCombatAiActionEconomyEnabled &&
            !HasAnyUsefulHostileActionAgainstVisibleEnemies(actor, battleService))
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
            rulesetCharacter.HasConditionOfType(FlightSuspendedConditionName) ||
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

    internal static bool TrySpendLeftoverActionEconomy(GameLocationCharacter character)
    {
        return TrySpendLeftoverActionEconomy(character, true);
    }

    internal static bool TrySpendLeftoverActionEconomyAtEndTurn(GameLocationCharacter character)
    {
        return TrySpendLeftoverActionEconomyAtEndTurn(character, out _);
    }

    internal static bool TrySpendLeftoverActionEconomyAtEndTurn(
        GameLocationCharacter character,
        out bool suppressEndTurn)
    {

        if (ResolveGroundMeleeMoveSettling(
                character,
                allowConnectedRouteValidation: false,
                allowTerminalAction: false))
        {
            suppressEndTurn = false;
            return true;
        }

        FailStalePendingTerminalActions(character, "end-turn");

        if (TryConsumePendingRouteTerminalAtEndTurn(character, out suppressEndTurn))
        {
            return true;
        }

        if (character != null &&
            PendingTerminalDodgeEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalDodge) &&
            pendingTerminalDodge.Round == GetCurrentBattleRound())
        {

            if (TryNormalizePendingTerminalDodgeAtEndTurn(
                    character,
                    pendingTerminalDodge,
                    out suppressEndTurn))
            {
                return true;
            }

            if (pendingTerminalDodge.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp) &&
                IsActiveBattleContender(character) &&
                !PendingTerminalActionEndTurnSuppressCache.Contains(character.Guid))
            {
                PendingTerminalActionEndTurnSuppressCache.Add(character.Guid);
                suppressEndTurn = true;
                return true;
            }

            FailPendingTerminalDodgeActionNotAccepted(character, pendingTerminalDodge);
            suppressEndTurn = false;
            return true;
        }

        if (character != null &&
            PendingTerminalReadyEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalReady) &&
            pendingTerminalReady.Round == GetCurrentBattleRound())
        {

            if (TryNormalizePendingTerminalReadyAtEndTurn(
                    character,
                    pendingTerminalReady,
                    out suppressEndTurn))
            {
                return true;
            }

            if (pendingTerminalReady.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp) &&
                IsActiveBattleContender(character) &&
                !PendingTerminalActionEndTurnSuppressCache.Contains(character.Guid))
            {
                PendingTerminalActionEndTurnSuppressCache.Add(character.Guid);
                suppressEndTurn = true;
                return true;
            }

            FailPendingTerminalReadyActionNotAccepted(character, pendingTerminalReady);
            suppressEndTurn = false;
            return true;
        }

        suppressEndTurn = false;

        if (TryCloseMissedAiProcessTerminalLaunchAtEndTurn(character))
        {
            return true;
        }

        if (TryConsumeSearchLostTargetRecoveryAtEndTurn(character, out suppressEndTurn))
        {
            return true;
        }

        return false;
    }

    private static bool TryConsumeSearchLostTargetRecoveryAtEndTurn(
        GameLocationCharacter character,
        out bool suppressEndTurn)
    {
        suppressEndTurn = false;

        if (character?.RulesetCharacter == null ||
            !TryGetCurrentPendingAiProcessTurnRecovery(character, out var recovery) ||
            recovery.Reason != "SearchLostTargetAbort")
        {
            return false;
        }

        PendingAiProcessTurnRecoveryCache.Remove(character.Guid);

        if (ShouldAllowRecoveryActionLinkedMove(character, recovery.Reason) &&
            TrySpendRecoveryLeftoverActionEconomy(character, true))
        {
            suppressEndTurn = true;

            return true;
        }

        if (TrySpendRecoveryLeftoverActionEconomy(character, false))
        {
            suppressEndTurn = true;

            return true;
        }

        return false;
    }

    private static bool TryCloseMissedAiProcessTerminalLaunchAtEndTurn(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PendingAiProcessTerminalLaunchCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }


        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
        PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);

        if (memory.ActionId == Id.Dodge)
        {
            PendingFallbackDodgeConditionCache.Remove(character.Guid);
            PendingTerminalDodgeEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
        }
        else if (memory.ActionId == Id.Ready)
        {
            PendingTerminalReadyEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
        }


        return true;
    }

    private static bool TryNormalizePendingTerminalDodgeAtEndTurn(
        GameLocationCharacter character,
        PendingTerminalDodgeEndTurnMemory pendingTerminalDodge,
        out bool suppressEndTurn)
    {
        suppressEndTurn = false;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var hasPendingCondition =
            PendingFallbackDodgeConditionCache.TryGetValue(character.Guid, out var existingConditions);
        var condition = hasPendingCondition
            ? FindNewDodgingCondition(character.RulesetCharacter, existingConditions)
            : null;

        if (condition != null)
        {
            PendingFallbackDodgeConditionCache.Remove(character.Guid);
            PendingTerminalDodgeEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
            NormalizeFallbackDodgeCondition(character, condition);
            return true;
        }

        var dodgeStatus = character.GetActionStatus(Id.Dodge, ActionScope.Battle);

        if (dodgeStatus != ActionStatus.Available)
        {
            if (pendingTerminalDodge.Round == GetCurrentBattleRound() &&
                pendingTerminalDodge.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp))
            {
                return false;
            }

            PendingFallbackDodgeConditionCache.Remove(character.Guid);
            PendingTerminalDodgeEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
            TryCompleteTerminalDodgeEndTurn(
                character,
                true,
                pendingTerminalDodge,
                TerminalDodgeCompletionKind.EngineRejected);
            return true;
        }

        return false;
    }

    private static bool TryNormalizePendingTerminalReadyAtEndTurn(
        GameLocationCharacter character,
        PendingTerminalDodgeEndTurnMemory pendingTerminalReady,
        out bool suppressEndTurn)
    {
        suppressEndTurn = false;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var readyStatus = character.GetActionStatus(Id.Ready, ActionScope.Battle);

        if (readyStatus != ActionStatus.Available)
        {
            if (pendingTerminalReady.Round == GetCurrentBattleRound() &&
                pendingTerminalReady.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp))
            {
                return false;
            }

            PendingTerminalReadyEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
            TryCompleteTerminalReadyEndTurn(
                character,
                pendingTerminalReady,
                TerminalReadyCompletionKind.EngineRejected);
            return true;
        }

        return false;
    }

    private static bool HasCurrentPendingTerminalReadyOrDodge(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        return PendingTerminalDodgeEndTurnCache.TryGetValue(character.Guid, out var pendingDodge) &&
               pendingDodge.Round == currentRound &&
               pendingDodge.TurnStamp == currentTurnStamp ||
               PendingTerminalReadyEndTurnCache.TryGetValue(character.Guid, out var pendingReady) &&
               pendingReady.Round == currentRound &&
               pendingReady.TurnStamp == currentTurnStamp;
    }

    private static bool FailPendingTerminalDodgeActionNotAccepted(
        GameLocationCharacter character,
        PendingTerminalDodgeEndTurnMemory pendingTerminalDodge)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var dodgeStatus = character.GetActionStatus(Id.Dodge, ActionScope.Battle);

        PendingFallbackDodgeConditionCache.Remove(character.Guid);
        PendingTerminalDodgeEndTurnCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
        PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
        TryCompleteTerminalDodgeEndTurn(
            character,
            true,
            pendingTerminalDodge,
            TerminalDodgeCompletionKind.EngineRejected);

        return true;
    }

    private static bool FailPendingTerminalReadyActionNotAccepted(
        GameLocationCharacter character,
        PendingTerminalDodgeEndTurnMemory pendingTerminalReady)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var readyStatus = character.GetActionStatus(Id.Ready, ActionScope.Battle);

        PendingTerminalReadyEndTurnCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
        PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
        TryCompleteTerminalReadyEndTurn(
            character,
            pendingTerminalReady,
            TerminalReadyCompletionKind.EngineRejected);

        return true;
    }

    private static bool TryNormalizePendingTerminalActionForInactiveContender(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null || IsActiveBattleContender(character))
        {
            return false;
        }

        if (PendingTerminalDodgeEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalDodge))
        {
            if (TryNormalizePendingTerminalDodgeAtEndTurn(
                    character,
                    pendingTerminalDodge,
                    out _))
            {
                return true;
            }

            return FailPendingTerminalDodgeActionNotAccepted(character, pendingTerminalDodge);
        }

        if (PendingTerminalReadyEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalReady))
        {
            if (TryNormalizePendingTerminalReadyAtEndTurn(
                    character,
                    pendingTerminalReady,
                    out _))
            {
                return true;
            }

            return FailPendingTerminalReadyActionNotAccepted(character, pendingTerminalReady);
        }

        return false;
    }

    internal static bool TryHandleAdvancedCombatAiBeforeEndTurn(
        GameLocationCharacter character,
        out bool suppressEndTurn)
    {
        suppressEndTurn = false;

        if (character?.RulesetCharacter == null ||
            !IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character))
        {
            return false;
        }

        EnsureCombatAiRuntimeCache(character);


        ResolveGroundMeleeMoveSettling(
            character,
            allowConnectedRouteValidation: false,
            allowTerminalAction: false);

        TrySpendLeftoverActionEconomyAtEndTurn(character, out suppressEndTurn);


        return true;
    }

    internal static void TryUsePostMainMovementAfterActionExecution(
        GameLocationCharacter character,
        Id actionId,
        ActionScope scope,
        int mainRankBefore,
        int mainAttacksBefore)
    {
        if (scope != ActionScope.Battle ||
            actionId is not (Id.AttackMain or Id.CastMain or Id.PowerMain))
        {
            return;
        }

        EnsureCombatAiRuntimeCache(character);

        if (!IsAiControlledForCombat(character))
        {
            return;
        }

        if (!IsActiveBattleContender(character))
        {
            return;
        }

        TryApplyPendingRouteMovementLock(character, "post-main");

        var actionEconomy = BuildActionEconomySnapshot(character);
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var profile = BuildProfile(character);
        var turnPlan = battleService == null
            ? default
            : BuildCombatAiTurnPlan(character, profile, battleService);
        var isClearAllyCorridor = battleService != null && IsClearAllyCorridorMovementPlan(turnPlan);
        var hasCommittedMainAction = HasCommittedMainActionThisTurn(character);

        if (!isClearAllyCorridor &&
            battleService != null &&
            hasCommittedMainAction &&
            TryBuildPostMainClearAllyCorridorTurnPlan(
                character,
                profile,
                battleService,
                out var corridorTurnPlan))
        {
            turnPlan = corridorTurnPlan;
            isClearAllyCorridor = true;
        }

        if (!hasCommittedMainAction)
        {
            return;
        }

        if (!isClearAllyCorridor)
        {
            return;
        }

        if (ShouldDeferPostMainClearAllyCorridorForRemainingAttack(
                character,
                actionId,
                mainRankBefore,
                mainAttacksBefore))
        {
            return;
        }

        if (TryBlockRepeatedPostMainClearAllyCorridorAttempt(character))
        {
            return;
        }

        if (battleService == null ||
            character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself ||
            actionEconomy.TacticalMove != ActionStatus.Available)
        {
            return;
        }

        if (TryUsePostMainClearAllyCorridorMove(
                character,
                turnPlan,
                battleService,
                out _))
        {
            return;
        }

    }

    private static bool ShouldDeferPostMainClearAllyCorridorForRemainingAttack(
        GameLocationCharacter character,
        Id actionId,
        int mainRankBefore,
        int mainAttacksBefore)
    {
        if (actionId != Id.AttackMain ||
            character?.RulesetCharacter == null)
        {
            return false;
        }

        var mainRank = character.CurrentActionRankByType[ActionType.Main];
        var usedMainAttacks = character.UsedMainAttacks;
        var allowedMainAttacks = character.GetAllowedMainAttacksForRank(mainRank);
        var attackMainStatus = character.GetActionStatus(Id.AttackMain, ActionScope.Battle);

        return attackMainStatus == ActionStatus.Available &&
               allowedMainAttacks > 0 &&
               usedMainAttacks > 0 &&
               usedMainAttacks < allowedMainAttacks;
    }

    internal static void TryUsePreMainRouteMove(GameLocationCharacter character)
    {
        EnsureCombatAiRuntimeCache(character);

        if (!IsAdvancedCombatAiEnabled ||
            !IsAiControlledForCombat(character) ||
            !IsActiveBattleContender(character))
        {
            return;
        }

        if (TryCloseGroundMeleeNoMoveTerminalSeal(character, "pre-main route"))
        {
            return;
        }

        var actionEconomy = BuildActionEconomySnapshot(character);

        if (!actionEconomy.MainAvailable)
        {
            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Unavailable,
                CombatAiMovementGoalKind.None,
                default);
            return;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null)
        {
            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Unavailable,
                CombatAiMovementGoalKind.None,
                default);
            return;
        }

        var profile = BuildProfile(character);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);

        if (PreMainRouteMoveAttemptCache.ContainsKey(character.Guid))
        {
            return;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.AdvanceToMelee &&
            turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.FlyingPursuitPolicy)
        {
            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Unavailable,
                turnPlan.MovementPlan.Goal,
                default,
                PreMainRouteMoveFlags.VanillaOwned);
            return;
        }

        if (!ShouldUsePreMainRouteMove(character, profile, turnPlan))
        {
            return;
        }

        var continuation = GetPreMainRouteMoveContinuation(character, turnPlan);
        var requireActionAfterMove = continuation == CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision;
        var requireActionAfterNormalMove = requireActionAfterMove && !CanUseProgressOnlyNormalRoute(turnPlan);
        var isFlyingMeleeRoute = IsFlyingMeleeMovementPlan(turnPlan);
        var skipFreeJump = turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing ||
                           isFlyingMeleeRoute;
        var jumpRouteSource = CombatAiRouteMoveSourceKind.JumpFallback;

        if (TryUseAdjacentContactFreeJumpImmediateAttack(
                character,
                turnPlan,
                battleService,
                skipFreeJump))
        {
            return;
        }

        if (ShouldEvaluateJumpImprovementBeforeNormalRoute(
                character,
                turnPlan,
                skipFreeJump) &&
            TryUseActionLinkedFreeJumpToResidualAction(
                character,
                turnPlan,
                battleService,
                continuation,
                requireActionAfterMove,
                requireJumpImprovement: true,
                routeMoveSource: CombatAiRouteMoveSourceKind.JumpImprovement))
        {
            var destination = ActionLinkedMoveCache.TryGetValue(character.Guid, out var pending)
                ? pending.ExpectedDestination
                : default;

            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Executed,
                turnPlan.MovementPlan.Goal,
                destination);
            return;
        }

        if (TryUseConnectedFiringLineRouteMove(
                character,
                turnPlan,
                battleService,
                profile,
                continuation,
                requireActionAfterNormalMove,
                CombatAiRouteMoveSourceKind.ConnectedFiringLine,
                true,
                out var connectedFiringLineDestination,
                out var connectedFiringLineClosed))
        {
            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Executed,
                turnPlan.MovementPlan.Goal,
                connectedFiringLineDestination);
            return;
        }

        if (connectedFiringLineClosed)
        {
            if (TryUseLostTargetSearchRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove,
                    CombatAiRouteMoveSourceKind.SearchLostTarget,
                    out var connectedSearchDestination))
            {
                RecordPreMainRouteMoveAttempt(
                    character,
                    CombatAiPreMainRouteMoveStatus.Executed,
                    CombatAiMovementGoalKind.SearchKnownTarget,
                    connectedSearchDestination);
                return;
            }

            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Blocked,
                turnPlan.MovementPlan.Goal,
                connectedFiringLineDestination,
                PreMainRouteMoveFlags.DisconnectedSeekFailure);
            return;
        }

        if (IsSearchKnownTargetPlan(turnPlan) &&
            TryUseLostTargetSearchRouteMove(
                character,
                turnPlan,
                battleService,
                profile,
                continuation,
                CombatAiRouteMoveSourceKind.SearchLostTarget,
                out var searchRouteDestination))
        {
            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Executed,
                turnPlan.MovementPlan.Goal,
                searchRouteDestination);
            return;
        }

        if (TryUseRecordedPreMainRouteMove(
                character,
                turnPlan,
                battleService,
                continuation,
                requireActionAfterNormalMove,
                out var preferredTacticalStatus,
                out var preferredTacticalDestination))
        {
            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Executed,
                turnPlan.MovementPlan.Goal,
                preferredTacticalDestination);
            return;
        }

        if (ShouldEvaluateJumpFallbackAfterNormalRouteFailure(
                character,
                turnPlan,
                skipFreeJump) &&
            TryUseActionLinkedFreeJumpToResidualAction(
                character,
                turnPlan,
                battleService,
                continuation,
                requireActionAfterMove,
                routeMoveSource: jumpRouteSource))
        {
            var destination = ActionLinkedMoveCache.TryGetValue(character.Guid, out var pending)
                ? pending.ExpectedDestination
                : default;

            RecordPreMainRouteMoveAttempt(
                character,
                CombatAiPreMainRouteMoveStatus.Executed,
                turnPlan.MovementPlan.Goal,
                destination);
            return;
        }

        var tacticalDestination = preferredTacticalDestination;
        var status = preferredTacticalStatus;

        if (IsGroundMeleePursuitPlan(turnPlan) &&
            status == CombatAiPreMainRouteMoveStatus.Blocked)
        {
            SealGroundMeleeNoMoveTerminal(character);
        }

        ScheduleConnectedFiringLineTerminalAfterFailedRoute(
            character,
            turnPlan,
            actionEconomy,
            status,
            tacticalDestination);

        RecordPreMainRouteMoveAttempt(
            character,
            status,
            turnPlan.MovementPlan.Goal,
            tacticalDestination,
            status != CombatAiPreMainRouteMoveStatus.Executed &&
            turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MoveToPreferredRange
                ? PreMainRouteMoveFlags.DisconnectedSeekFailure
                : PreMainRouteMoveFlags.None);
    }

    internal static void TryUseBaselineFreeJumpRouteMove(GameLocationCharacter character)
    {
        EnsureCombatAiRuntimeCache(character);

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
            Math.Max(1, ObservedCombatMemoryTurnStamp));

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

        MarkDashBlockedAfterRouteMove(character, CombatAiMovementGoalKind.None, bestDestination);
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
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan)
    {
        _ = character;
        _ = profile;

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

    private static bool CanUseProgressOnlyNormalRoute(CombatAiTurnPlan turnPlan)
    {
        if (IsImproveFiringPositionPlan(turnPlan) ||
            turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.FlyingPursuitPolicy)
        {
            return false;
        }

        return turnPlan.MovementPlan.Goal is CombatAiMovementGoalKind.SearchKnownTarget
            or CombatAiMovementGoalKind.AdvanceToMelee;
    }

    private static bool ShouldEvaluateJumpImmediateAttackBeforeNormalRoute(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        bool skipFreeJump)
    {
        return !skipFreeJump &&
               character?.RulesetCharacter != null &&
               IsGroundMeleePursuitPlan(turnPlan);
    }

    private static bool TryUseAdjacentContactFreeJumpImmediateAttack(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        bool skipFreeJump)
    {
        if (!ShouldEvaluateJumpImmediateAttackBeforeNormalRoute(
                character,
                turnPlan,
                skipFreeJump))
        {
            return false;
        }

        if (!TryUseActionLinkedFreeJumpToResidualAction(
                character,
                turnPlan,
                battleService,
                CombatAiActionLinkedMoveContinuation.ImmediateResidualAction,
                requireActionAfterMove: true,
                requireJumpImprovement: false,
                routeMoveSource: CombatAiRouteMoveSourceKind.JumpImmediateAttack,
                requireImmediateAttack: true))
        {
            if (character != null)
            {
                JumpImmediateAttackReachableCache.Remove(character.Guid);
            }

            return false;
        }

        var destination = ActionLinkedMoveCache.TryGetValue(character.Guid, out var pending)
            ? pending.ExpectedDestination
            : default;

        JumpImmediateAttackReachableCache.Add(character.Guid);
        RecordPreMainRouteMoveAttempt(
            character,
            CombatAiPreMainRouteMoveStatus.Executed,
            turnPlan.MovementPlan.Goal,
            destination);
        return true;
    }

    private static bool ShouldEvaluateJumpImprovementBeforeNormalRoute(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        bool skipFreeJump)
    {
        if (skipFreeJump ||
            character?.RulesetCharacter == null ||
            !CanUseJumpImprovementGoal(turnPlan))
        {
            return false;
        }

        if (ShouldBlockGenericFreeJumpForPositioning(
                turnPlan,
                CombatAiRouteMoveSourceKind.JumpImprovement))
        {
            return false;
        }

        if (IsGroundMeleePursuitPlan(turnPlan))
        {
            return false;
        }

        if (!TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress) ||
            !movementProgress.HasAcceptedMoveCandidate)
        {
            return false;
        }

        if (movementProgress.HasImmediatePreferredActionMoveCandidate)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldEvaluateJumpFallbackAfterNormalRouteFailure(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        bool skipFreeJump)
    {
        if (skipFreeJump ||
            character?.RulesetCharacter == null ||
            !turnPlan.MovementPlan.HasGoal)
        {
            return false;
        }

        if (ShouldBlockGenericFreeJumpForPositioning(
                turnPlan,
                CombatAiRouteMoveSourceKind.JumpFallback))
        {
            return false;
        }

        if (TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress) &&
            movementProgress.HasAcceptedMoveCandidate)
        {
            return false;
        }

        return true;
    }

    private static bool CanUseJumpImprovementGoal(CombatAiTurnPlan turnPlan)
    {
        if (turnPlan.MovementPlan.Goal is CombatAiMovementGoalKind.BreakThreat
            or CombatAiMovementGoalKind.MaintainThreatAvoidance
            or CombatAiMovementGoalKind.MeleeSpacing)
        {
            return false;
        }

        return turnPlan.MovementPlan.Goal is (CombatAiMovementGoalKind.MoveToPreferredRange
                   or CombatAiMovementGoalKind.SearchKnownTarget
                   or CombatAiMovementGoalKind.AdvanceToMelee) ||
               IsImproveFiringPositionPlan(turnPlan);
    }

    private static bool ShouldBlockGenericFreeJumpForPositioning(
        CombatAiTurnPlan turnPlan,
        CombatAiRouteMoveSourceKind routeMoveSource)
    {
        var isPositioningOrSeek =
            RequiresPostMoveActionConnectedPositioning(turnPlan) ||
            turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.SearchKnownTarget;

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat ||
            !isPositioningOrSeek ||
            routeMoveSource is not (CombatAiRouteMoveSourceKind.JumpImprovement
                or CombatAiRouteMoveSourceKind.JumpFallback
                or CombatAiRouteMoveSourceKind.FreeJump))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetRecentNoMoveProxyThreatAttempt(
        GameLocationCharacter character,
        int3 startPosition,
        out ProxyThreatRouteAttemptMemory attempt)
    {
        attempt = default;

        if (!TryGetSameTurnNoMoveProxyThreatAttempt(character, out attempt) ||
            !TryGetProxyMeleeThreat(character, out var memory) ||
            !attempt.MatchesSourceAndStart(memory, startPosition))
        {
            return false;
        }

        return true;
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

    private static CombatAiActionLinkedMoveContinuation GetPreMainRouteMoveContinuation(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat ||
            turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            return CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision;
        }

        return CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove;
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
                    turnPlan,
                    BuildActionEconomySnapshot(character),
                    destination);
                closed = true;
            }

            return false;
        }

        var movementProgress = GetOrCreateTurnMovementProgress(character, turnPlan);
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
                    movementProgress,
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
                    null,
                    requireSeededPath: false,
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
                turnPlan,
                BuildActionEconomySnapshot(character),
                destination);
            closed = true;
        }

        return false;
    }

    private static bool TryUseConnectedFiringLineRecovery(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsConnectedFiringLinePlan(turnPlan))
        {
            return false;
        }

        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var key = $"{round}:{turnStamp}";

        if (ConnectedFiringLineRecoveryAttemptCache.TryGetValue(character.Guid, out var previousKey) &&
            previousKey == key)
        {
            return false;
        }

        ConnectedFiringLineRecoveryAttemptCache[character.Guid] = key;

        if (character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        if (TryUseConnectedFiringLineRouteMove(
                character,
                turnPlan,
                battleService,
                profile,
                CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove,
                true,
                    CombatAiRouteMoveSourceKind.ConnectedFiringLineRecovery,
                false,
                out _,
                out _))
        {
            return true;
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
        var attemptKey = BuildLostTargetSearchAttemptKey(round, turnStamp, start, anchor);

        if (LostTargetSearchAttemptCache.TryGetValue(character.Guid, out var previousAttempt) &&
            previousAttempt == attemptKey)
        {
            return false;
        }

        if (character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0 ||
            !character.CanDecideToMoveByItself)
        {
            return false;
        }

        var searchTurnPlan = new CombatAiTurnPlan(
            turnPlan.ActionProbe,
            BuildSearchKnownTargetMovementPlan(searchTarget, anchor));
        var remainingMove = Math.Max(0, character.RemainingTacticalMoves);

        if (!TryGetReachableRouteDestinations(
                character,
                start,
                remainingMove,
                out var reachableDestinations,
                allowPathfinding: true,
                walkOnly: false))
        {
            RecordLostTargetSearchAttempt(character, round, turnStamp, start, anchor);
            return false;
        }

        var currentDistance = ComputeGridDistance(start, anchor);
        var candidates = new List<AiAcceptedMoveCandidate>();

        foreach (var position in reachableDestinations.Positions)
        {
            if (IsFailedAiMoveTarget(character, start, position) || IsBacktrackingMove(character, start, position))
            {
                continue;
            }

            var candidateDistance = ComputeGridDistance(position, anchor);
            var progress = currentDistance - candidateDistance;

            if (progress + 0.01f < ComputeMinimumMovementGoalProgress(
                    character,
                    CombatAiMovementGoalKind.SearchKnownTarget,
                    CombatAiMovementPolicyKind.SearchKnownTargetPolicy,
                    currentDistance))
            {
                continue;
            }

            var score =
                MovementGoalSearchScore +
                Mathf.Clamp01(progress / Math.Max(currentDistance, 1f)) * 0.14f +
                Mathf.Clamp01(progress / Math.Max(remainingMove, 1f)) * 0.10f +
                ComputeStableTieBreakScore(
                    character,
                    searchTurnPlan,
                    position,
                    searchTurnPlan.ActionProbe.PreferredAction);

            candidates.Add(new AiAcceptedMoveCandidate(
                position,
                score,
                progress,
                EstimateTurnsToPreferredAction(character, searchTurnPlan, position)));
        }

        var orderedCandidates = candidates
            .OrderByDescending(candidate => candidate.Progress)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => reachableDestinations.GetMoveCost(candidate.Position))
            .ThenBy(candidate => candidate.Position.x)
            .ThenBy(candidate => candidate.Position.y)
            .ThenBy(candidate => candidate.Position.z)
            .ToArray();

        foreach (var candidate in orderedCandidates)
        {
            if (!TryExecutePreMainRouteMoveCandidate(
                    character,
                    searchTurnPlan,
                    battleService,
                    profile,
                    start,
                    candidate.Position,
                    continuation,
                    requireActionAfterMove: false,
                    routeMoveSource: routeMoveSource,
                    routePath: null,
                    requireSeededPath: false,
                    out destination))
            {
                continue;
            }

            PendingRouteActionOnlyTerminalCache.Remove(character.Guid);
            return true;
        }

        RecordLostTargetSearchAttempt(character, round, turnStamp, start, anchor);
        return false;
    }

    private static bool TryUseRecordedPreMainRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiActionLinkedMoveContinuation continuation,
        bool requireActionAfterMove,
        out CombatAiPreMainRouteMoveStatus failedStatus,
        out int3 destination)
    {
        destination = default;
        failedStatus = CombatAiPreMainRouteMoveStatus.Unavailable;

        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var isGroundMeleePursuit = IsGroundMeleePursuitPlan(turnPlan);
        var hasMovementProgress = TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress);
        var hasAcceptedRouteCandidate = hasMovementProgress && movementProgress.HasAcceptedMoveCandidate;
        if (!isGroundMeleePursuit &&
            !hasAcceptedRouteCandidate &&
            !ShouldRequireReachableProxyThreatDestination(character, turnPlan))
        {
            return false;
        }

        var start = character.LocationPosition;
        var profile = BuildProfile(character);
        ReachableRouteDestinationMemory groundReachableDestinations = null;

        if (isGroundMeleePursuit)
        {
            RecordGroundMeleeRouteMemoryGate(character, turnPlan);

            var remainingMove = Math.Max(0, character.RemainingTacticalMoves);

            if (TryGetReachableRouteDestinations(
                    character,
                    start,
                    remainingMove,
                    out groundReachableDestinations,
                    allowPathfinding: true,
                    walkOnly: true))
            {
            }
            else
            {
                // Reachable sets are only a pre-main hint here; the move result is the source of truth.
            }
        }

        var candidateDestinations = hasAcceptedRouteCandidate
            ? movementProgress.EnumerateAcceptedMoveCandidatePositions().ToArray()
            : Array.Empty<int3>();
        var excludedDestinations = candidateDestinations;
        var rejectedCandidates = 0;
        var durableDetour = default(GroundMeleeDetourCandidateMemory);
        var durableDetourAvailable =
            isGroundMeleePursuit &&
            TryGetGroundMeleeDetourCandidate(
                character,
                turnPlan,
                start,
                out durableDetour);

        if (TryGetRecentNoMoveProxyThreatAttempt(character, start, out var previousProxyAttempt))
        {
            excludedDestinations = candidateDestinations
                .Append(previousProxyAttempt.FailedDestination)
                .ToArray();
            candidateDestinations = Array.Empty<int3>();
            rejectedCandidates = hasAcceptedRouteCandidate ? excludedDestinations.Length : 0;
            failedStatus = CombatAiPreMainRouteMoveStatus.Blocked;
        }

        if (isGroundMeleePursuit)
        {
            if (TryUseGroundMeleeOneRoundAttackRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    start,
                    candidateDestinations,
                    groundReachableDestinations,
                    continuation,
                    ref rejectedCandidates,
                    out destination))
            {
                return true;
            }

            var jumpShortcutResult = TryUseGroundMeleeJumpShortcutRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    start,
                    movementProgress,
                    groundReachableDestinations,
                    continuation);

            if (jumpShortcutResult.Executed)
            {
                destination = jumpShortcutResult.Destination;
                return true;
            }

            if (TryUseGroundMeleeTargetContactRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    start,
                    movementProgress,
                    durableDetourAvailable,
                    durableDetour,
                    groundReachableDestinations,
                    continuation,
                    requireActionAfterMove,
                    ref rejectedCandidates,
                    out destination))
            {
                return true;
            }

            return false;
        }

        foreach (var candidateDestination in candidateDestinations)
        {
            if (!TryExecutePreMainRouteMoveCandidate(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    start,
                    candidateDestination,
                    continuation,
                    requireActionAfterMove,
                    CombatAiRouteMoveSourceKind.Normal,
                    null,
                    requireSeededPath: false,
                    out destination))
            {
                rejectedCandidates++;
                continue;
            }

            return true;
        }

        var fallbackLockRemainingMovement = ShouldLockRemainingMovementAfterRouteMove(turnPlan, continuation);

        var proxyResult = TryUseReachableProxyThreatRouteMove(
            character,
            turnPlan,
            battleService,
            profile,
            start,
            excludedDestinations,
            turnPlan.ActionProbe.Target,
            CombatAiActionKind.None,
            continuation,
            turnPlan.MovementPlan.Goal,
            fallbackLockRemainingMovement,
            requireActionAfterMove,
            out destination);

        return proxyResult.Executed;
    }

    private static bool TryUseGroundMeleeOneRoundAttackRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        IEnumerable<int3> acceptedDestinations,
        ReachableRouteDestinationMemory reachableDestinations,
        CombatAiActionLinkedMoveContinuation continuation,
        ref int rejectedCandidates,
        out int3 destination)
    {
        destination = default;

        if (character?.RulesetCharacter == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            battleService == null ||
            reachableDestinations?.Positions == null)
        {
            return false;
        }

        var seen = new HashSet<string>();
        var target = turnPlan.ActionProbe.Target;
        var candidates = (acceptedDestinations ?? Enumerable.Empty<int3>())
            .Concat(reachableDestinations.Positions)
            .Where(position => position != start)
            .Where(position => seen.Add(GetPositionKey(position)))
            .Where(reachableDestinations.Contains)
            .Where(position => CanUseActionKindAtPosition(
                character,
                position,
                target,
                CombatAiActionKind.Melee,
                battleService))
            .OrderBy(reachableDestinations.GetMoveCost)
            .ThenBy(position => position.x)
            .ThenBy(position => position.y)
            .ThenBy(position => position.z)
            .ToArray();

        if (candidates.Length == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (ShouldRejectGroundMeleeRouteFailureCandidate(
                    character,
                    turnPlan,
                    start,
                    candidate))
            {
                rejectedCandidates++;
                continue;
            }

            if (!TryExecutePreMainRouteMoveCandidate(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    start,
                    candidate,
                    continuation,
                    true,
                    CombatAiRouteMoveSourceKind.Normal,
                    null,
                    requireSeededPath: false,
                    out destination))
            {
                rejectedCandidates++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryUseGroundMeleeTargetContactRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        AiTurnMovementProgress movementProgress,
        bool durableDetourAvailable,
        GroundMeleeDetourCandidateMemory durableDetour,
        ReachableRouteDestinationMemory reachableDestinations,
        CombatAiActionLinkedMoveContinuation continuation,
        bool requireActionAfterMove,
        ref int rejectedCandidates,
        out int3 destination)
    {
        destination = default;

        var candidates = BuildGroundMeleeTargetContactRouteCandidates(
            character,
            turnPlan,
            start,
            movementProgress,
            durableDetourAvailable,
            durableDetour,
            reachableDestinations);

        if (candidates.Count == 0)
        {
            return false;
        }

        var completeCandidates = candidates
            .Where(candidate => candidate.Complete)
            .OrderBy(candidate => IsOneRoundGroundMeleeAttackRouteCandidate(
                character,
                turnPlan,
                battleService,
                candidate)
                ? 0
                : 1)
            .ThenBy(candidate => candidate.Route.TotalPathCost)
            .ThenBy(candidate => candidate.SelectedContactMoveCost)
            .ThenByDescending(candidate => candidate.SelectedMoveCost)
            .ThenBy(candidate => candidate.SelectedDestination.x)
            .ThenBy(candidate => candidate.SelectedDestination.y)
            .ThenBy(candidate => candidate.SelectedDestination.z)
            .ThenBy(candidate => candidate.SourcePriority)
            .ToList();

        if (TryExecuteGroundMeleeTargetContactRouteCandidates(
                character,
                turnPlan,
                battleService,
                profile,
                start,
                completeCandidates,
                continuation,
                requireActionAfterMove,
                ref rejectedCandidates,
                out destination))
        {
            return true;
        }

        if (completeCandidates.Count > 0)
        {
            return false;
        }

        return false;
    }

    private static GroundMeleeJumpShortcutResult TryUseGroundMeleeJumpShortcutRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        AiTurnMovementProgress movementProgress,
        ReachableRouteDestinationMemory reachableDestinations,
        CombatAiActionLinkedMoveContinuation continuation)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsGroundMeleePursuitPlan(turnPlan))
        {
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.BasicGate);
        }

        if (!CanUseFreeJumpForAi(character))
        {
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.FreeJumpUnavailable);
        }

        if (!TryGetGroundMeleeTargetContactRouteQuery(character, turnPlan, out var query) ||
            !query.Complete)
        {
            var independentResult = TryUseGroundMeleeIndependentJumpShortcutRouteMove(
                    character,
                    turnPlan,
                    battleService,
                    start,
                    movementProgress,
                    continuation);

            if (independentResult.Executed)
            {
                return independentResult;
            }

            return new GroundMeleeJumpShortcutResult(
                false,
                independentResult.BlockKind == GroundMeleeJumpShortcutBlockKind.NoIndependentShortcut
                    ? GroundMeleeJumpShortcutBlockKind.NoTargetContactQuery
                    : independentResult.BlockKind);
        }

        var startRemainingCost = query.TryGetContactCost(start, out var startContactCost, out _)
            ? startContactCost
            : query.BestGoalMoveCost;

        if (startRemainingCost <= 0)
        {
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.AlreadyAtGoal);
        }

        var hasNormalBaseline = TryGetBestGroundMeleeNormalReachableCost(
            query,
            reachableDestinations,
            start,
            startRemainingCost,
            out var normalDestination,
            out var normalMoveCost,
            out var normalRemainingCost,
            out var normalTotalCost);
        var normalBaselineFailed =
            hasNormalBaseline &&
            (IsFailedAiMoveTarget(character, start, normalDestination) ||
             HasRecentNoMoveRouteCompletion(
                 character,
                 turnPlan.MovementPlan.Goal,
                 start));
        var found = false;
        var selectedDestination = default(int3);
        var selectedRemainingCost = 0;
        var selectedTotalCost = int.MaxValue;
        var selectedScore = float.MinValue;

        bool ConsiderDestination(int3 candidate, float score)
        {
            if (candidate == start ||
                !IsLegalAiRouteDestination(character, candidate, out _))
            {
                return true;
            }

            if (ShouldRejectGroundMeleeRouteFailureCandidate(
                    character,
                    turnPlan,
                    start,
                    candidate))
            {
                return true;
            }

            if (!query.TryGetContactCost(candidate, out var candidateRemainingCost, out _))
            {
                return true;
            }

            if (candidateRemainingCost >= startRemainingCost)
            {
                return true;
            }

            var jumpMoveCost = Math.Max(1, FreeJumpContext.ComputeAiFreeJumpMovementCost(start, candidate));
            var totalCost = jumpMoveCost + candidateRemainingCost;

            if (hasNormalBaseline &&
                !normalBaselineFailed &&
                (totalCost > normalTotalCost ||
                 totalCost == normalTotalCost && candidateRemainingCost >= normalRemainingCost))
            {
                return true;
            }

            var actionScore =
                (startRemainingCost - candidateRemainingCost) +
                (hasNormalBaseline ? normalTotalCost - totalCost : 1) +
                score * 0.01f +
                ComputeStableTieBreakScore(character, turnPlan, candidate, CombatAiActionKind.Melee);

            if (found &&
                (totalCost > selectedTotalCost ||
                 totalCost == selectedTotalCost && candidateRemainingCost > selectedRemainingCost ||
                 totalCost == selectedTotalCost &&
                 candidateRemainingCost == selectedRemainingCost &&
                 actionScore <= selectedScore + 0.000001f))
            {
                return true;
            }

            found = true;
            selectedDestination = candidate;
            selectedRemainingCost = candidateRemainingCost;
            selectedTotalCost = totalCost;
            selectedScore = actionScore;
            return true;
        }

        FreeJumpContext.TryEnumerateUsefulAiFreeJumpDestinations(
            character,
            ConsiderDestination);

        if (!found)
        {
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.NoImprovingShortcut);
        }

        var lockRemainingMovement = ShouldLockRemainingMovementAfterRouteMove(turnPlan, continuation);
        ActionLinkedMoveCache[character.Guid] = new ActionLinkedMoveMemory(
            turnPlan.ActionProbe.Target,
            CombatAiActionKind.None,
            continuation,
            turnPlan.MovementPlan.Goal,
            start,
            selectedDestination,
            CombatAiRouteMoveSourceKind.GroundMeleeShortcut,
            lockRemainingMovement,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        if (lockRemainingMovement)
        {
            RecordPendingRouteMovementLock(character, turnPlan.MovementPlan.Goal, continuation, selectedDestination);
        }

        if (!TryExecuteAiFreeJumpTacticalMove(
                character,
                selectedDestination,
                CombatAiRouteMoveSourceKind.GroundMeleeShortcut))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            PendingRouteMovementLockCache.Remove(character.Guid);
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.ExecuteFailed);
        }

        movementProgress?.MarkFreeJumpMovementCandidate();
        MarkDashBlockedAfterRouteMove(
            character,
            turnPlan.MovementPlan.Goal,
            selectedDestination);
        GroundMeleeJumpRouteAvailableCache.Add(character.Guid);
        return new GroundMeleeJumpShortcutResult(
            true,
            GroundMeleeJumpShortcutBlockKind.None,
            selectedDestination);
    }

    private static GroundMeleeJumpShortcutResult TryUseGroundMeleeIndependentJumpShortcutRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 start,
        AiTurnMovementProgress movementProgress,
        CombatAiActionLinkedMoveContinuation continuation)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            !CanUseFreeJumpForAi(character))
        {
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.FreeJumpUnavailable);
        }

        var target = turnPlan.ActionProbe.Target;
        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var startDistance = ComputeGroundMeleeRouteGoalDistance(start, targetPosition);
        var normalTurnsToAction = movementProgress?.BestPreferredActionMoveTurnsToAction ?? -1;

        if (normalTurnsToAction < 0)
        {
            normalTurnsToAction = movementProgress?.BestMoveCandidateTurnsToAction ?? -1;
        }

        var normalUnavailable =
            normalTurnsToAction < 0 ||
            HasRecentNoMoveRouteCompletion(
                character,
                turnPlan.MovementPlan.Goal,
                start);
        var found = false;
        var selectedDestination = default(int3);
        var selectedDistance = float.MaxValue;
        var selectedTotalPathCost = int.MaxValue;
        var selectedScore = float.MinValue;

        bool ConsiderDestination(
            int3 candidate,
            FreeJumpContext.FreeJumpCandidateInfo? candidateInfo,
            float score)
        {
            if (candidate == start ||
                !IsLegalAiRouteDestination(character, candidate, out _))
            {
                return true;
            }

            if (ShouldRejectGroundMeleeRouteFailureCandidate(
                    character,
                    turnPlan,
                    start,
                    candidate))
            {
                return true;
            }

            var candidateDistance = ComputeGroundMeleeRouteGoalDistance(candidate, targetPosition);
            var distanceGain = startDistance - candidateDistance;
            var canMelee = CanUseActionKindAtPosition(
                character,
                candidate,
                target,
                CombatAiActionKind.Melee,
                battleService);
            var isContact = IsGroundMeleeRouteAdjacentContact(candidate, targetPosition);
            var turnsToAction = canMelee
                ? 0
                : EstimateTurnsToPreferredAction(character, turnPlan, candidate);
            var improvesTurns =
                normalUnavailable &&
                turnsToAction >= 0 &&
                (normalTurnsToAction < 0 || turnsToAction < normalTurnsToAction);

            if (!canMelee &&
                !isContact &&
                distanceGain < 0.75f &&
                !improvesTurns)
            {
                return true;
            }

            var jumpMoveCost = candidateInfo?.Preview.MoveCost ??
                               FreeJumpContext.ComputeAiFreeJumpMovementCost(start, candidate);
            var approximateRemainingCost = Math.Max(0, (int)Math.Ceiling(candidateDistance));
            var totalPathCost = Math.Max(1, jumpMoveCost) + approximateRemainingCost;
            var actionScore =
                (canMelee ? 4.0f : isContact ? 2.0f : 0f) +
                (improvesTurns ? 1.5f : 0f) +
                distanceGain +
                score * 0.01f -
                jumpMoveCost * 0.01f +
                ComputeStableTieBreakScore(character, turnPlan, candidate, CombatAiActionKind.Melee);

            if (found &&
                (actionScore < selectedScore + 0.000001f ||
                 Math.Abs(actionScore - selectedScore) <= 0.000001f &&
                 (totalPathCost > selectedTotalPathCost ||
                  totalPathCost == selectedTotalPathCost && candidateDistance >= selectedDistance)))
            {
                return true;
            }

            found = true;
            selectedDestination = candidate;
            selectedDistance = candidateDistance;
            selectedTotalPathCost = totalPathCost;
            selectedScore = actionScore;
            return true;
        }

        FreeJumpContext.TryEnumerateTargetContactAiFreeJumpDestinations(
            character,
            targetPosition,
            1,
            FreeJumpDefaultMinimumSuccessChance,
            (candidate, candidateInfo, score) => ConsiderDestination(candidate, candidateInfo, score));

        FreeJumpContext.TryEnumerateUsefulAiFreeJumpDestinations(
            character,
            (candidate, score) => ConsiderDestination(candidate, null, score));

        if (!found)
        {
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.NoIndependentShortcut);
        }

        var lockRemainingMovement = ShouldLockRemainingMovementAfterRouteMove(turnPlan, continuation);
        ActionLinkedMoveCache[character.Guid] = new ActionLinkedMoveMemory(
            target,
            CombatAiActionKind.None,
            continuation,
            turnPlan.MovementPlan.Goal,
            start,
            selectedDestination,
            CombatAiRouteMoveSourceKind.GroundMeleeShortcut,
            lockRemainingMovement,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        if (lockRemainingMovement)
        {
            RecordPendingRouteMovementLock(character, turnPlan.MovementPlan.Goal, continuation, selectedDestination);
        }

        if (!TryExecuteAiFreeJumpTacticalMove(
                character,
                selectedDestination,
                CombatAiRouteMoveSourceKind.GroundMeleeShortcut))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            PendingRouteMovementLockCache.Remove(character.Guid);
            return new GroundMeleeJumpShortcutResult(false, GroundMeleeJumpShortcutBlockKind.ExecuteFailed);
        }

        movementProgress?.MarkFreeJumpMovementCandidate();
        MarkDashBlockedAfterRouteMove(
            character,
            turnPlan.MovementPlan.Goal,
            selectedDestination);
        GroundMeleeJumpRouteAvailableCache.Add(character.Guid);
        return new GroundMeleeJumpShortcutResult(
            true,
            GroundMeleeJumpShortcutBlockKind.None,
            selectedDestination);
    }

    private static bool TryGetBestGroundMeleeNormalReachableCost(
        GroundMeleeTargetContactRouteQuery query,
        ReachableRouteDestinationMemory reachableDestinations,
        int3 start,
        int startRemainingCost,
        out int3 destination,
        out int moveCost,
        out int remainingCost,
        out int totalCost)
    {
        destination = default;
        moveCost = 0;
        remainingCost = 0;
        totalCost = int.MaxValue;

        if (!query.Complete || reachableDestinations?.Positions == null)
        {
            return false;
        }

        var found = false;

        foreach (var position in reachableDestinations.Positions)
        {
            if (position == start ||
                !query.TryGetContactCost(position, out var contactCost, out _) ||
                contactCost >= startRemainingCost)
            {
                continue;
            }

            var candidateMoveCost = Math.Max(0, reachableDestinations.GetMoveCost(position));
            var candidateTotalCost = candidateMoveCost + contactCost;

            if (found &&
                (candidateTotalCost > totalCost ||
                 candidateTotalCost == totalCost && contactCost > remainingCost ||
                 candidateTotalCost == totalCost &&
                 contactCost == remainingCost &&
                 candidateMoveCost <= moveCost))
            {
                continue;
            }

            found = true;
            destination = position;
            moveCost = candidateMoveCost;
            remainingCost = contactCost;
            totalCost = candidateTotalCost;
        }

        return found;
    }

    private static bool HasRecentNoMoveRouteCompletion(
        GameLocationCharacter character,
        CombatAiMovementGoalKind movementGoal,
        int3 start)
    {
        if (character?.RulesetCharacter == null ||
            !RouteMoveCompletionClosedCache.TryGetValue(character.Guid, out var closed) ||
            closed.MovementGoal != movementGoal ||
            closed.StartPosition != start ||
            closed.Round != GetCurrentBattleRound() ||
            closed.TurnStamp != Math.Max(1, ObservedCombatMemoryTurnStamp) ||
            !closed.IsNoMove)
        {
            return false;
        }

        return true;
    }

    private static bool IsOneRoundGroundMeleeAttackRouteCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        GroundMeleeTargetContactRouteCandidate candidate)
    {
        return character?.RulesetCharacter != null &&
               turnPlan.ActionProbe.Target?.RulesetCharacter != null &&
               battleService != null &&
               candidate.Complete &&
               CanUseActionKindAtPosition(
                   character,
                   candidate.SelectedDestination,
                   turnPlan.ActionProbe.Target,
                   CombatAiActionKind.Melee,
                   battleService);
    }

    private static List<GroundMeleeTargetContactRouteCandidate> BuildGroundMeleeTargetContactRouteCandidates(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        AiTurnMovementProgress movementProgress,
        bool durableDetourAvailable,
        GroundMeleeDetourCandidateMemory durableDetour,
        ReachableRouteDestinationMemory reachableDestinations)
    {
        var candidates = new List<GroundMeleeTargetContactRouteCandidate>();
        var seeds = new List<GroundMeleeTargetContactRouteSeed>();
        var seen = new HashSet<string> { GetPositionKey(start) };
        var proofAttempts = 0;
        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var startDistance = ComputeGroundMeleeRouteGoalDistance(start, targetPosition);
        var hasTargetContactQuery = TryBuildGroundMeleeTargetContactRouteQuery(
            character,
            turnPlan.ActionProbe.Target,
            targetPosition,
            start,
            out var targetContactQuery);

        void AddSeed(AiAcceptedMoveCandidate seed, string source, int sourcePriority)
        {
            if (!IsLegalAiRouteDestination(character, seed.Position, out _))
            {
                return;
            }

            if (!seen.Add(GetPositionKey(seed.Position)))
            {
                return;
            }

            var firstStepMoveCost = GetGroundMeleeFirstStepMoveCost(start, seed.Position, reachableDestinations);

            seeds.Add(new GroundMeleeTargetContactRouteSeed(
                seed,
                source,
                sourcePriority,
                firstStepMoveCost));
        }

        void EvaluateSeed(GroundMeleeTargetContactRouteSeed seed)
        {
            if (ShouldRejectGroundMeleeRouteFailureCandidate(
                    character,
                    turnPlan,
                    start,
                    seed.Position))
            {
                return;
            }

            var selectedDestination = default(int3);
            var selectedMoveCost = 0;
            var selectedContactMoveCost = 0;
            var selectedContactGoal = default(int3);
            IReadOnlyList<GameLocationCharacterDefinitions.PathStep> routePath = [];
            var route = default(GroundMeleeConnectedRoute);
            var routeFound = false;

            if (hasTargetContactQuery)
            {
                routeFound = TryBuildGroundMeleeConnectedRouteFromTargetContactQuery(
                    character,
                    turnPlan,
                    start,
                    targetContactQuery,
                    seed,
                    reachableDestinations,
                    out selectedDestination,
                    out selectedMoveCost,
                    out selectedContactMoveCost,
                    out selectedContactGoal,
                    out routePath,
                    out route);
            }

            if (!routeFound && !hasTargetContactQuery)
            {
                routeFound = TryBuildGroundMeleeConnectedRouteFromFirstStep(
                    character,
                    turnPlan.ActionProbe.Target,
                    targetPosition,
                    seed.Position,
                    start,
                    seed.FirstStepMoveCost,
                    reachableDestinations,
                    turnPlan,
                    out selectedDestination,
                    out selectedMoveCost,
                    out selectedContactMoveCost,
                    out selectedContactGoal,
                    out routePath,
                    out route);
            }

            if (!routeFound)
            {
                return;
            }

            candidates.Add(new GroundMeleeTargetContactRouteCandidate(
                seed.Position,
                selectedDestination,
                selectedMoveCost,
                selectedContactMoveCost,
                selectedContactGoal,
                routePath,
                seed.Source,
                seed.SourcePriority,
                seed.Candidate.Score,
                seed.Candidate.TurnsToAction,
                route));
        }

        if (movementProgress != null)
        {
            foreach (var candidate in movementProgress.EnumerateAcceptedMoveCandidates())
            {
                AddSeed(candidate, "accepted", 1);
            }

            foreach (var candidate in movementProgress.EnumerateLongRoutePursuitCandidates())
            {
                AddSeed(candidate, "detour", 1);
            }
        }

        if (durableDetourAvailable)
        {
            AddSeed(
                new AiAcceptedMoveCandidate(
                    durableDetour.Destination,
                    durableDetour.Score,
                    durableDetour.Progress,
                    durableDetour.TurnsToAction),
                "durable-detour",
                1);
        }

        if (reachableDestinations?.Positions != null)
        {
            foreach (var position in reachableDestinations.Positions
                         .OrderBy(position => GetTargetContactSeedTotalCost(
                             targetContactQuery,
                             position,
                             GetGroundMeleeFirstStepMoveCost(start, position, reachableDestinations)))
                         .ThenBy(position => GetTargetContactSeedRemainingCost(targetContactQuery, position))
                         .ThenBy(position => GetGroundMeleeFirstStepMoveCost(start, position, reachableDestinations))
                         .ThenBy(position => position.x)
                         .ThenBy(position => position.y)
                         .ThenBy(position => position.z)
                         .Take(GroundMeleeTargetContactRouteSeedLimit))
            {
                var distance = ComputeGroundMeleeRouteGoalDistance(position, targetPosition);

                AddSeed(
                    new AiAcceptedMoveCandidate(
                        position,
                        startDistance - distance,
                        startDistance - distance,
                        -1),
                    "reachable",
                    2);
            }
        }

        foreach (var seed in seeds
                     .OrderBy(seed => GetTargetContactSeedTotalCost(
                         targetContactQuery,
                         seed.Position,
                         seed.FirstStepMoveCost))
                     .ThenBy(seed => GetTargetContactSeedRemainingCost(targetContactQuery, seed.Position))
                     .ThenBy(seed => seed.FirstStepMoveCost)
                     .ThenBy(seed => seed.Position.x)
                     .ThenBy(seed => seed.Position.y)
                     .ThenBy(seed => seed.Position.z)
                     .ThenBy(seed => seed.SourcePriority)
                     .Take(GroundMeleeTargetContactRouteSeedLimit))
        {
            if (proofAttempts >= GroundMeleeTargetContactRouteProofLimit)
            {
                break;
            }

            proofAttempts++;
            EvaluateSeed(seed);
        }

        return candidates;
    }

    private static bool TryBuildGroundMeleeTargetContactRouteQuery(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 targetPosition,
        int3 start,
        out GroundMeleeTargetContactRouteQuery query)
    {
        query = default;
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
                    out _,
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
                new Dictionary<string, int>(),
                new Dictionary<string, int3>());
            TargetContactRouteQueryCache[character.Guid] = query;
            return false;
        }

        var bestGoal = orderedGoals[0];

        var bestGoalMoveCost = routeMap.GetMoveCost(bestGoal);
        var contactCostByPosition = new Dictionary<string, int>();
        var contactGoalByPosition = new Dictionary<string, int3>();
        var reverseGoals = 0;

        foreach (var goal in orderedGoals.Take(GroundMeleeTargetContactReverseGoalLimit))
        {
            var goalKey = GetPositionKey(goal);
            contactCostByPosition[goalKey] = 0;
            contactGoalByPosition[goalKey] = goal;

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
                var key = GetPositionKey(position);
                var cost = reverseMap.GetMoveCost(position);

                if (contactCostByPosition.TryGetValue(key, out var existingCost) &&
                    existingCost <= cost)
                {
                    continue;
                }

                contactCostByPosition[key] = cost;
                contactGoalByPosition[key] = goal;
            }
        }

        query = new GroundMeleeTargetContactRouteQuery(
            routeMap,
            true,
            bestGoal,
            bestGoalMoveCost,
            contactCostByPosition,
            contactGoalByPosition,
            !hasAttackContactGoal);
        TargetContactRouteQueryCache[character.Guid] = query;
        return true;
    }

    private static int GetGroundMeleeFirstStepMoveCost(
        int3 start,
        int3 position,
        ReachableRouteDestinationMemory reachableDestinations)
    {
        return reachableDestinations?.Contains(position) == true
            ? Math.Max(0, reachableDestinations.GetMoveCost(position))
            : ComputeForcedMoveCost(start, position);
    }

    private static int GetTargetContactSeedRemainingCost(
        GroundMeleeTargetContactRouteQuery query,
        int3 position)
    {
        if (!query.Complete ||
            !query.TryGetContactCost(position, out var contactMoveCost, out _))
        {
            return int.MaxValue;
        }

        return contactMoveCost;
    }

    private static int GetTargetContactSeedTotalCost(
        GroundMeleeTargetContactRouteQuery query,
        int3 position,
        int firstStepMoveCost)
    {
        var remainingCost = GetTargetContactSeedRemainingCost(query, position);

        return remainingCost == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, firstStepMoveCost) + remainingCost;
    }

    private static bool TrySelectGroundMeleeTargetContactRouteDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        GroundMeleeTargetContactRouteQuery query,
        GroundMeleeTargetContactRouteSeed seed,
        int3 seedContactGoal,
        int startContactMoveCost,
        ReachableRouteDestinationMemory reachableDestinations,
        out int3 selectedDestination,
        out int selectedMoveCost,
        out int selectedContactMoveCost,
        out int3 selectedContactGoal,
        out IReadOnlyList<GameLocationCharacterDefinitions.PathStep> routePath)
    {
        selectedDestination = default;
        selectedMoveCost = 0;
        selectedContactMoveCost = 0;
        selectedContactGoal = default;
        routePath = [];

        if (!query.Complete)
        {
            return false;
        }

        IReadOnlyList<int3> positions = reachableDestinations?.Positions ?? [];
        var seen = new HashSet<string>();
        var nodes = new List<GroundMeleeRouteWaypointNode>();

        void Evaluate(int3 position)
        {
            if (position == start || !seen.Add(GetPositionKey(position)))
            {
                return;
            }

            if (!IsLegalAiRouteDestination(
                    character,
                    position,
                    out _,
                    allowActorCurrentPosition: false))
            {
                return;
            }

            if (ShouldRejectGroundMeleeRouteFailureCandidate(
                    character,
                    turnPlan,
                    start,
                    position))
            {
                return;
            }

            if (!query.TryGetContactCost(position, out var contactMoveCost, out var contactGoal) ||
                contactGoal != seedContactGoal)
            {
                return;
            }

            if (contactMoveCost >= startContactMoveCost)
            {
                return;
            }

            if (reachableDestinations?.Contains(position) != true)
            {
                return;
            }

            var moveCost = Math.Max(0, reachableDestinations.GetMoveCost(position));

            nodes.Add(new GroundMeleeRouteWaypointNode(
                position,
                moveCost,
                contactMoveCost,
                contactGoal,
                reachableDestinations.GetMoveMode(position),
                reachableDestinations.GetMoveFlags(position)));
        }

        foreach (var position in positions
                     .OrderBy(position => GetTargetContactSeedRemainingCost(query, position))
                     .ThenBy(position => reachableDestinations?.GetMoveCost(position) ?? seed.FirstStepMoveCost)
                     .ThenBy(position => position.x)
                     .ThenBy(position => position.y)
                     .ThenBy(position => position.z))
        {
            Evaluate(position);
        }

        Evaluate(seed.Position);

        if (nodes.Count == 0)
        {
            return false;
        }

        var selectedNode = nodes[0];
        var selectedSeedStep = false;

        foreach (var node in nodes)
        {
            if (node.Position != seed.Position)
            {
                continue;
            }

            selectedNode = node;
            selectedSeedStep = true;
            break;
        }

        if (!selectedSeedStep)
        {
            selectedNode = nodes
                .OrderBy(node => node.MoveCost + node.ContactMoveCost)
                .ThenBy(node => node.ContactMoveCost)
                .ThenByDescending(node => node.MoveCost)
                .ThenBy(node => node.Position.x)
                .ThenBy(node => node.Position.y)
                .ThenBy(node => node.Position.z)
                .First();
        }

        selectedDestination = selectedNode.Position;
        selectedMoveCost = selectedNode.MoveCost;
        selectedContactMoveCost = selectedNode.ContactMoveCost;
        selectedContactGoal = selectedNode.ContactGoal;
        routePath = [];
        return true;
    }

    private static IReadOnlyList<GameLocationCharacterDefinitions.PathStep> TryBuildGroundMeleeSingleStepRoutePath(
        int3 start,
        int3 destination,
        ReachableRouteDestinationMemory reachableDestinations)
    {
        if (reachableDestinations?.Contains(destination) != true ||
            destination == start)
        {
            return [];
        }

        return
        [
            new GameLocationCharacterDefinitions.PathStep
            {
                position = destination,
                moveMode = reachableDestinations.GetMoveMode(destination),
                moveCost = Math.Max(1, reachableDestinations.GetMoveCost(destination)),
                flags = reachableDestinations.GetMoveFlags(destination)
            }
        ];
    }

    private static bool TryBuildGroundMeleeConnectedRouteFromTargetContactQuery(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        GroundMeleeTargetContactRouteQuery query,
        GroundMeleeTargetContactRouteSeed seed,
        ReachableRouteDestinationMemory reachableDestinations,
        out int3 selectedDestination,
        out int selectedMoveCost,
        out int selectedContactMoveCost,
        out int3 selectedContactGoal,
        out IReadOnlyList<GameLocationCharacterDefinitions.PathStep> routePath,
        out GroundMeleeConnectedRoute connectedRoute)
    {
        selectedDestination = seed.Position;
        selectedMoveCost = seed.FirstStepMoveCost;
        selectedContactMoveCost = 0;
        selectedContactGoal = default;
        routePath = [];
        connectedRoute = default;

        if (!query.Complete ||
            !query.TryGetContactCost(seed.Position, out _, out var contactGoal))
        {
            return false;
        }

        var startContactCost = query.TryGetContactCost(start, out var startCost, out _)
            ? startCost
            : query.BestGoalMoveCost;

        if (!TrySelectGroundMeleeTargetContactRouteDestination(
                character,
                turnPlan,
                start,
                query,
                seed,
                contactGoal,
                startContactCost,
                reachableDestinations,
                out selectedDestination,
                out selectedMoveCost,
                out selectedContactMoveCost,
                out selectedContactGoal,
                out routePath))
        {
            return false;
        }

        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var startDistance = ComputeGroundMeleeRouteGoalDistance(start, targetPosition);
        var firstStepDistance = ComputeGroundMeleeRouteGoalDistance(seed.Position, targetPosition);
        var selectedDistance = ComputeGroundMeleeRouteGoalDistance(selectedDestination, targetPosition);
        var contactDistance = ComputeGroundMeleeRouteGoalDistance(selectedContactGoal, targetPosition);
        var routeTurns = EstimateGroundMeleeContactRouteTurns(character, selectedMoveCost + selectedContactMoveCost);
        var routeGoal = query.IsCompleteApproach
            ? "approach"
            : "contact";
        var projection = new GroundMeleeRouteProjection(
            routeTurns,
            startDistance,
            selectedDistance,
            contactDistance,
            routeGoal);

        connectedRoute = new GroundMeleeConnectedRoute(
            projection,
            selectedContactGoal,
            query.Map.NodeCount,
            true,
            false,
            selectedMoveCost,
            selectedContactMoveCost);
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

        if (TargetContactRouteQueryCache.TryGetValue(character.Guid, out var cached) &&
            cached.Map.StartPosition == character.LocationPosition)
        {
            query = cached;
            return cached.Complete;
        }

        return TryBuildGroundMeleeTargetContactRouteQuery(
            character,
            turnPlan.ActionProbe.Target,
            turnPlan.MovementPlan.TargetPosition,
            character.LocationPosition,
            out query);
    }

    private static bool TryExecuteGroundMeleeTargetContactRouteCandidates(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        IReadOnlyCollection<GroundMeleeTargetContactRouteCandidate> candidates,
        CombatAiActionLinkedMoveContinuation continuation,
        bool requireActionAfterMove,
        ref int rejectedCandidates,
        out int3 destination)
    {
        destination = default;

        foreach (var candidate in candidates)
        {
            if (!TryExecutePreMainRouteMoveCandidate(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    start,
                    candidate.SelectedDestination,
                    continuation,
                    requireActionAfterMove,
                    CombatAiRouteMoveSourceKind.Normal,
                    routePath: null,
                    requireSeededPath: false,
                    out destination))
            {
                rejectedCandidates++;
                continue;
            }

            return true;
        }

        return false;
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
        IReadOnlyList<GameLocationCharacterDefinitions.PathStep> routePath,
        bool requireSeededPath,
        out int3 destination)
    {
        destination = candidateDestination;

        if (!TryValidateForcedRouteDestination(
                character,
                turnPlan,
                battleService,
                profile,
                start,
                destination,
                requireActionAfterMove))
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

        if (requireSeededPath && routePath is not { Count: > 0 })
        {
            return false;
        }

        var lockRemainingMovement = ShouldLockRemainingMovementAfterRouteMove(turnPlan, continuation);

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
            Math.Max(1, ObservedCombatMemoryTurnStamp));

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

        if (continuation == CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove)
        {
            actionChainExecuted = aborted =>
                OnAiTacticalMoveActionChainExecuted(character, aborted, routeMoveSource);
        }

        if (routeMoveSource == CombatAiRouteMoveSourceKind.Normal)
        {
            FreeJumpContext.SuppressAiFreeJumpForNextMove(character, destination);
        }

        character.MyExecuteActionTacticalMove(destination, actionChainExecuted);

        MarkDashBlockedAfterRouteMove(character, turnPlan.MovementPlan.Goal, destination);

        return true;
    }

    private static List<AiAcceptedMoveCandidate> BuildReachableGroundMeleePursuitCandidates(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        ReachableRouteDestinationMemory reachableDestinations,
        IEnumerable<int3> excludedDestinations)
    {
        var candidates = new List<AiAcceptedMoveCandidate>();
        var remainingMove = Math.Max(0, character?.RemainingTacticalMoves ?? 0);
        var excludedKeys = new HashSet<string>(
            (excludedDestinations ?? Enumerable.Empty<int3>()).Select(GetPositionKey))
        {
            GetPositionKey(start)
        };

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            reachableDestinations == null ||
            reachableDestinations.Positions.Count == 0)
        {
            return candidates;
        }

        foreach (var candidatePosition in reachableDestinations.Positions)
        {
            if (excludedKeys.Contains(GetPositionKey(candidatePosition)))
            {
                continue;
            }

            if (RequiresMainDashForForcedMove(character, start, candidatePosition) ||
                IsFailedAiMoveTarget(character, start, candidatePosition) ||
                IsBacktrackingMove(character, start, candidatePosition))
            {
                continue;
            }

            if (HasForcedRouteOpportunityExposure(character, start, candidatePosition, battleService))
            {
                continue;
            }

            if (ShouldRejectTrafficBlockingMove(
                    character,
                    candidatePosition,
                    battleService,
                    profile,
                    turnPlan,
                    out _))
            {
                continue;
            }

            if (ShouldRejectGroundMeleeRouteIntentCandidate(
                    character,
                    turnPlan,
                    start,
                    candidatePosition))
            {
                continue;
            }

            if (ShouldRejectGroundMeleeRouteFailureCandidate(
                    character,
                    turnPlan,
                    start,
                    candidatePosition))
            {
                continue;
            }

            if (!TryProjectGroundMeleeRouteFromFirstStep(
                    character,
                    turnPlan,
                    candidatePosition,
                    start,
                    out var projection))
            {
                continue;
            }

            if (!IsGroundMeleeConnectedApproachProjection(
                    start,
                    candidatePosition,
                    turnPlan.MovementPlan.TargetPosition,
                    projection))
            {
                continue;
            }

            var moveCost = ComputeForcedMoveCost(start, candidatePosition);
            var routeTurnScore = Math.Max(
                0f,
                (GroundMeleeRouteProjectionScoreTurnCap - Math.Min(projection.RouteTurns, GroundMeleeRouteProjectionScoreTurnCap) + 1) * 0.18f);
            var routeProgressScore = Math.Max(0f, projection.Progress) * 0.12f;
            var firstStepRegressionPenalty =
                Math.Max(0f, projection.FirstStepDistance - projection.StartDistance) * 0.025f;
            var score = Math.Max(
                0.01f,
                MovementGoalMeleeProgressScore +
                routeTurnScore +
                routeProgressScore +
                (Mathf.Clamp01(moveCost / Math.Max(remainingMove, 1f)) * 0.18f) -
                firstStepRegressionPenalty +
                (projection.Goal == "contact" ? 0.18f : 0f) +
                ComputeStableTieBreakScore(character, turnPlan, candidatePosition, CombatAiActionKind.Melee));

            candidates.Add(new AiAcceptedMoveCandidate(
                candidatePosition,
                score,
                projection.Progress,
                projection.RouteTurns));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.TurnsToAction)
            .ThenBy(candidate => candidate.Position.x)
            .ThenBy(candidate => candidate.Position.y)
            .ThenBy(candidate => candidate.Position.z)
            .Take(GroundMeleePursuitCandidateLimit)
            .ToList();
    }

    private static bool TryProjectGroundMeleeRouteFromFirstStep(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 firstStep,
        int3 start,
        out GroundMeleeRouteProjection projection)
    {
        if (!TryBuildGroundMeleeConnectedRouteFromFirstStep(
                character,
                turnPlan,
                firstStep,
                start,
                out var connectedRoute))
        {
            projection = default;
            return false;
        }

        projection = connectedRoute.Projection;
        return true;
    }

    private static bool TryBuildGroundMeleeConnectedRouteFromFirstStep(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 firstStep,
        int3 start,
        out GroundMeleeConnectedRoute connectedRoute)
    {
        return TryBuildGroundMeleeConnectedRouteFromFirstStep(
            character,
            turnPlan.ActionProbe.Target,
            turnPlan.MovementPlan.TargetPosition,
            firstStep,
            start,
            ComputeForcedMoveCost(start, firstStep),
            null,
            turnPlan,
            out _,
            out _,
            out _,
            out _,
            out _,
            out connectedRoute);
    }

    private static bool TryBuildGroundMeleeConnectedRouteFromFirstStep(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 targetPosition,
        int3 firstStep,
        int3 start,
        int firstStepMoveCost,
        ReachableRouteDestinationMemory reachableDestinations,
        CombatAiTurnPlan turnPlan,
        out int3 selectedDestination,
        out int selectedMoveCost,
        out int selectedContactMoveCost,
        out int3 selectedContactGoal,
        out IReadOnlyList<GameLocationCharacterDefinitions.PathStep> routePath,
        out GroundMeleeConnectedRoute connectedRoute)
    {
        selectedDestination = firstStep;
        selectedMoveCost = Math.Max(0, firstStepMoveCost);
        selectedContactMoveCost = 0;
        selectedContactGoal = firstStep;
        routePath = TryBuildGroundMeleeSingleStepRoutePath(start, firstStep, reachableDestinations);
        connectedRoute = default;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null)
        {
            return false;
        }

        var startDistance = ComputeGroundMeleeRouteGoalDistance(start, targetPosition);
        var firstStepDistance = ComputeGroundMeleeRouteGoalDistance(firstStep, targetPosition);
        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (IsGroundMeleeAttackContactGoal(character, target, firstStep, battleService))
        {
            var contactProjection = new GroundMeleeRouteProjection(
                1,
                startDistance,
                firstStepDistance,
                firstStepDistance,
                "contact");
            connectedRoute = new GroundMeleeConnectedRoute(
                contactProjection,
                firstStep,
                1,
                true,
                false,
                firstStepMoveCost,
                0);
            return true;
        }

        var routeBudget = GetGroundMeleeTargetContactRouteSearchBudget(character);

        if (!TryBuildGroundMeleeTargetContactRouteMap(
                character,
                target,
                targetPosition,
                firstStep,
                routeBudget,
                out var routeMap))
        {
            return false;
        }

        if (routeMap.NodeCount <= 0)
        {
            return false;
        }

        var connectedNodes = routeMap.NodeCount;
        var contactPositions = routeMap.ContactPositions
            .OrderBy(routeMap.GetMoveCost)
            .ThenBy(position => position.x)
            .ThenBy(position => position.y)
            .ThenBy(position => position.z)
            .ToList();

        if (contactPositions.Count > 0)
        {
            var contactPosition = contactPositions[0];
            var contactDistance = ComputeGroundMeleeRouteGoalDistance(contactPosition, targetPosition);
            var contactMoveCost = routeMap.GetMoveCost(contactPosition);
            var contactRouteTurn = EstimateGroundMeleeContactRouteTurns(character, contactMoveCost);
            var contactProjection = new GroundMeleeRouteProjection(
                contactRouteTurn,
                startDistance,
                firstStepDistance,
                contactDistance,
                "contact");

            connectedRoute = new GroundMeleeConnectedRoute(
                contactProjection,
                contactPosition,
                connectedNodes,
                true,
                false,
                firstStepMoveCost,
                contactMoveCost);
            selectedDestination = firstStep;
            selectedMoveCost = firstStepMoveCost;
            selectedContactMoveCost = contactMoveCost;
            selectedContactGoal = contactPosition;
            return true;
        }

        var bestPosition = routeMap.BestPosition;
        var bestDistance = ComputeGroundMeleeRouteGoalDistance(bestPosition, targetPosition);
        var approachProgress = startDistance - bestDistance;

        if (bestPosition == firstStep ||
            approachProgress <= 0.5f ||
            !IsLegalAiRouteDestination(
                character,
                bestPosition,
                out _,
                allowActorCurrentPosition: true))
        {
            return false;
        }

        var approachMoveCost = routeMap.GetMoveCost(bestPosition);
        var approachRouteTurn = EstimateGroundMeleeContactRouteTurns(character, firstStepMoveCost + approachMoveCost);
        var approachProjection = new GroundMeleeRouteProjection(
            approachRouteTurn,
            startDistance,
            firstStepDistance,
            bestDistance,
            "approach");

        connectedRoute = new GroundMeleeConnectedRoute(
            approachProjection,
            bestPosition,
            connectedNodes,
            true,
            false,
            firstStepMoveCost,
            approachMoveCost);
        selectedDestination = firstStep;
        selectedMoveCost = firstStepMoveCost;
        selectedContactMoveCost = approachMoveCost;
        selectedContactGoal = bestPosition;
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
        var moveCostByPosition = positions.ToDictionary(GetPositionKey, reachableDestinations.GetMoveCost);
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

    private static bool ShouldRejectGroundMeleeRouteIntentCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        int3 candidatePosition)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitPlan(turnPlan) ||
            !TryGetGroundMeleeRouteIntent(character, turnPlan, out var intent))
        {
            return false;
        }


        if (candidatePosition == intent.PreviousPosition)
        {
            if (TryProjectGroundMeleeRouteFromFirstStep(
                    character,
                    turnPlan,
                    candidatePosition,
                    start,
                    out var previousPositionProjection) &&
                IsGroundMeleeConnectedApproachProjection(
                    start,
                    candidatePosition,
                    turnPlan.MovementPlan.TargetPosition,
                    previousPositionProjection))
            {
                return false;
            }

            return true;
        }

        var candidateDistance = ComputeGroundMeleeRouteGoalDistance(candidatePosition, turnPlan.MovementPlan.TargetPosition);

        if (candidateDistance > intent.BestRouteDistance + 2.0f &&
            (!TryProjectGroundMeleeRouteFromFirstStep(
                 character,
                 turnPlan,
                 candidatePosition,
                 start,
                 out var projection) ||
             !IsGroundMeleeConnectedApproachProjection(
                 start,
                 candidatePosition,
                 turnPlan.MovementPlan.TargetPosition,
                 projection)))
        {
            return true;
        }

        return false;
    }

    private static bool IsGroundMeleeRouteImmediateActionCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 candidatePosition)
    {
        var target = turnPlan.ActionProbe.Target;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null)
        {
            return false;
        }

        return CanUseActionKindAtPosition(
                   character,
                   candidatePosition,
                   target,
                   turnPlan.ActionProbe.PreferredAction,
                   battleService) ||
               CanUseActionKindAtPosition(
                   character,
                   candidatePosition,
                   target,
                   turnPlan.ActionProbe.BackupAction,
                   battleService);
    }

    private static bool TryGetGroundMeleeRouteIntent(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        out GroundMeleeRouteIntentMemory intent)
    {
        intent = default;

        if (character?.RulesetCharacter == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            !GroundMeleeRouteIntentCache.TryGetValue(GetGroundMeleeRouteMemoryKey(character), out intent))
        {
            return false;
        }

        if (intent.Matches(turnPlan.ActionProbe.Target))
        {
            return true;
        }

        GroundMeleeRouteIntentCache.Remove(GetGroundMeleeRouteMemoryKey(character));
        return false;
    }

    private static void RecordGroundMeleeRouteIntent(
        GameLocationCharacter character,
        GameLocationCharacter target,
        int3 previousPosition,
        int3 actualDestination)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            actualDestination == previousPosition)
        {
            return;
        }

        var routeDistance = ComputeGroundMeleeRouteGoalDistance(actualDestination, target.LocationPosition);

        var routeMemoryKey = GetGroundMeleeRouteMemoryKey(character);

        if (GroundMeleeRouteIntentCache.TryGetValue(routeMemoryKey, out var existing) &&
            existing.TargetGuid == target.Guid)
        {
            routeDistance = Math.Min(routeDistance, existing.BestRouteDistance);
        }

        GroundMeleeRouteIntentCache[routeMemoryKey] = new GroundMeleeRouteIntentMemory(
            target.Guid,
            previousPosition,
            actualDestination,
            routeDistance,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
        GroundMeleeRouteFailureCache.Remove(routeMemoryKey);
        GroundMeleeDetourCandidateCache.Remove(routeMemoryKey);
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
        ActionLinkedMoveMemory pendingAction,
        string phase)
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
        PendingRouteMovementLockCache.Remove(character.Guid);


        return true;
    }

    private static bool TryCompletePendingJumpImmediateAttackActionChainSettled(
        GameLocationCharacter character,
        bool aborted,
        string completionSource)
    {
        if (character?.RulesetCharacter == null ||
            !TryConsumePendingJumpImmediateAttackMove(character, out var pendingAction))
        {
            return false;
        }

        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);

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

        if (aborted && actualDestination == pendingAction.StartPosition)
        {
            CloseFailedJumpImmediateAttackRoute(
                character,
                pendingAction,
                actualDestination,
                "JumpImmediateAttackNoMove",
                completionSource);
            return true;
        }

        if (aborted && !expectedOrAdjacent)
        {
            CloseFailedJumpImmediateAttackRoute(
                character,
                pendingAction,
                actualDestination,
                "JumpImmediateAttackAborted",
                completionSource);
            return true;
        }

        if (actualDestination == pendingAction.StartPosition)
        {
            CloseFailedJumpImmediateAttackRoute(
                character,
                pendingAction,
                actualDestination,
                "JumpImmediateAttackNoMove",
                completionSource);
            return true;
        }

        if (!expectedOrAdjacent || aborted)
        {
            CloseFailedJumpImmediateAttackRoute(
                character,
                pendingAction,
                actualDestination,
                "JumpImmediateAttackAborted",
                completionSource);
            return true;
        }

        return TryResolveGroundMeleeJumpImmediateAttackAfterSettling(
                   character,
                   pendingAction,
                   actualDestination,
                   allowTerminalAction: true,
                   out var handled,
                   out _,
                   completionSource) &&
               handled;
    }

    private static void CloseFailedJumpImmediateAttackRoute(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        string reason,
        string completionSource)
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
        PendingRouteMovementLockCache.Remove(character.Guid);
        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        GroundMeleePartialRouteCache.Remove(character.Guid);
        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
        RouteMoveDashBlockCache.Remove(character.Guid);
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


        ScheduleAiProcessTurnRecovery(character, Id.TacticalMove, reason);
    }

    private static bool TryConsumePendingJumpImmediateAttackMove(
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
            GroundMeleeMoveSettlingCache.Remove(character.Guid);
            ActionLinkedMoveCache.Remove(character.Guid);
            return true;
        }

        if (ActionLinkedMoveCache.TryGetValue(character.Guid, out pendingAction) &&
            IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            return true;
        }

        pendingAction = default;
        return false;
    }

    private static bool TryUseGroundMeleePartialRouteContinuationAfterActionChain(
        GameLocationCharacter character,
        bool aborted)
    {
        if (character?.RulesetCharacter == null ||
            !GroundMeleePartialRouteCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!memory.MatchesCurrentTurn(currentRound, currentTurnStamp))
        {
            GroundMeleePartialRouteCache.Remove(character.Guid);
            return false;
        }

        if (aborted)
        {
            GroundMeleePartialRouteCache.Remove(character.Guid);
            return false;
        }

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
            GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
            return true;
        }

        ResolveGroundMeleeMoveSettling(
            character,
            allowConnectedRouteValidation: true,
            allowTerminalAction: false);

        if (!noMove)
        {
            return true;
        }

        return true;
    }

    private static bool ResolveGroundMeleeMoveSettling(
        GameLocationCharacter character,
        bool allowConnectedRouteValidation = false,
        bool allowTerminalAction = false)
    {
        if (character?.RulesetCharacter == null ||
            !GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        GroundMeleeMoveSettlingCache.Remove(character.Guid);
        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);

        var pendingAction = memory.PendingAction;
        var actualDestination = character.LocationPosition;

        if (!IsActiveBattleContender(character))
        {
            return false;
        }

        if (actualDestination != pendingAction.StartPosition)
        {
            if (IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
            {
                return TryResolveGroundMeleeJumpImmediateAttackAfterSettling(
                           character,
                           pendingAction,
                           actualDestination,
                           allowTerminalAction,
                           out var jumpSettlingHandled,
                           out _) &&
                       jumpSettlingHandled;
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
                RouteMoveDashBlockCache.Remove(character.Guid);
                TurnMovementProgressCache.Remove(character.Guid);

                RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
                    pendingAction.MovementGoal,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination,
                    RouteMoveCompletionFlags.None,
                    GetCurrentBattleRound(),
                    Math.Max(1, ObservedCombatMemoryTurnStamp));
                SchedulePendingRouteActionOnlyTerminal(
                    character,
                    pendingAction,
                    pendingAction.ExpectedDestination,
                    actualDestination,
                    string.Empty,
                    "route-regression",
                    consumeAfterAbort: true);
                return false;
            }

            RecordGroundMeleeRouteIntent(
                character,
                pendingAction.Target,
                pendingAction.StartPosition,
                actualDestination);
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

        RecordAiMoveFailure(character, pendingAction.StartPosition, pendingAction.ExpectedDestination);
        RecordGroundMeleeRouteFailure(
            character,
            pendingAction.Target,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination);
        RouteMoveDashBlockCache.Remove(character.Guid);
        TurnMovementProgressCache.Remove(character.Guid);

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            pendingAction.StartPosition,
            pendingAction.ExpectedDestination,
            RouteMoveCompletionFlags.NoMove | RouteMoveCompletionFlags.GroundMeleeNoMove,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            pendingAction.ExpectedDestination,
            actualDestination,
            string.Empty,
            "no-move",
            consumeAfterAbort: true);
        return false;
    }

    private static bool TryResolveGroundMeleeJumpImmediateAttackAfterSettling(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        bool allowTerminalAction,
        out bool handled,
        out bool actionStarted,
        string completionSource = "terminal")
    {
        handled = false;
        actionStarted = false;

        if (character?.RulesetCharacter == null ||
            !IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            return false;
        }

        if (!allowTerminalAction)
        {
            SealGroundMeleeNoMoveTerminal(character);
            handled = true;
            return true;
        }

        if (!IsActiveBattleContender(character))
        {
            SealGroundMeleeNoMoveTerminal(character);
            handled = true;
            return true;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var target = pendingAction.Target;

        if (battleService != null && target?.RulesetCharacter != null)
        {
            var attackResult = TryUseResidualSafeHostileAction(
                    character,
                    target,
                    CombatAiActionKind.Melee,
                    battleService);

            if (attackResult.Executed)
            {
                return RecordJumpImmediateAttackResolved(
                    character,
                    target,
                    pendingAction,
                    actualDestination,
                    completionSource,
                    out handled,
                    out actionStarted);
            }
        }

        SealGroundMeleeNoMoveTerminal(character);
        handled = true;
        return true;
    }

    private static bool RecordJumpImmediateAttackResolved(
        GameLocationCharacter character,
        GameLocationCharacter target,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        string completionSource,
        out bool handled,
        out bool actionStarted)
    {
        handled = false;
        actionStarted = false;

        if (character?.RulesetCharacter == null || target?.RulesetCharacter == null)
        {
            return false;
        }

        RecordGroundMeleeRouteIntent(
            character,
            target,
            pendingAction.StartPosition,
            actualDestination);
        handled = true;
        actionStarted = true;
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

    private static bool TryGetDisconnectedPositioningMovementLeak(
        GameLocationCharacter character,
        out DisconnectedPositioningSealMemory memory)
    {
        if (!TryGetActiveDisconnectedPositioningSeal(character, out memory))
        {
            return false;
        }

        return character.LocationPosition != memory.StartPosition;
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

        _ = currentTurnsToAction;
        _ = destinationTurnsToAction;
        return false;
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
                    out _,
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
        int3 startPosition,
        int3 actualDestination,
        out bool evaluated)
    {
        evaluated = false;

        if (character?.RulesetCharacter == null ||
            !TargetContactRouteQueryCache.TryGetValue(character.Guid, out var cachedRouteQuery) ||
            cachedRouteQuery.Map.StartPosition != startPosition ||
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

    private static void RecordGroundMeleeDetourCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 candidatePosition,
        float score,
        float progress,
        int turnsToAction)
    {
        var target = turnPlan.ActionProbe.Target;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            !IsGroundMeleePursuitPlan(turnPlan))
        {
            return;
        }

        var memory = new GroundMeleeDetourCandidateMemory(
            target.Guid,
            character.LocationPosition,
            candidatePosition,
            score,
            progress,
            turnsToAction,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        GroundMeleeDetourCandidateCache[GetGroundMeleeRouteMemoryKey(character)] = memory;
    }

    private static bool TryGetGroundMeleeDetourCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        out GroundMeleeDetourCandidateMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitPlan(turnPlan) ||
            !GroundMeleeDetourCandidateCache.TryGetValue(GetGroundMeleeRouteMemoryKey(character), out memory))
        {
            return false;
        }

        if (memory.Matches(turnPlan.ActionProbe.Target, start))
        {
            return true;
        }

        GroundMeleeDetourCandidateCache.Remove(GetGroundMeleeRouteMemoryKey(character));
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

    private static bool TryValidateGroundMeleeJumpRouteCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        int3 start,
        int3 candidatePosition,
        bool attackConnected,
        out bool routeImprovement)
    {
        routeImprovement = false;

        if (character?.RulesetCharacter == null || !IsGroundMeleePursuitPlan(turnPlan))
        {
            return true;
        }

        if (attackConnected)
        {
            return true;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (ShouldRejectGroundMeleeCurrentStateRouteCandidate(
                character,
                turnPlan,
                battleService,
                start,
                candidatePosition))
        {
            return false;
        }

        if (ShouldRejectGroundMeleeRouteIntentCandidate(
                character,
                turnPlan,
                start,
                candidatePosition))
        {
            return false;
        }

        if (ShouldRejectGroundMeleeRouteFailureCandidate(
                character,
                turnPlan,
                start,
                candidatePosition))
        {
            return false;
        }

        if (!attackConnected &&
            TryGetGroundMeleeRouteIntent(character, turnPlan, out var intent) &&
            ComputeHorizontalGridStepDistance(candidatePosition, intent.PreviousPosition) <= 1)
        {
            return false;
        }

        if (TryProjectGroundMeleeRouteFromFirstStep(
                character,
                turnPlan,
                candidatePosition,
                start,
                out var projection) &&
            IsGroundMeleeConnectedApproachProjection(
                start,
                candidatePosition,
                turnPlan.MovementPlan.TargetPosition,
                projection))
        {
            routeImprovement = true;
            return true;
        }

        return false;
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
                out _,
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

    private static bool ShouldRejectGroundMeleeCurrentStateRouteCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 start,
        int3 candidatePosition)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitPlan(turnPlan))
        {
            return false;
        }

        if (IsGroundMeleeRouteImmediateActionCandidate(
                character,
                turnPlan,
                battleService,
                candidatePosition))
        {
            return false;
        }

        var targetPosition = turnPlan.MovementPlan.TargetPosition;
        var startDistance = ComputeGroundMeleeRouteGoalDistance(start, targetPosition);
        var candidateDistance = ComputeGroundMeleeRouteGoalDistance(candidatePosition, targetPosition);

        if (IsGroundMeleeAttackContactGoal(
                character,
                turnPlan.ActionProbe.Target,
                candidatePosition,
                battleService))
        {
            return false;
        }

        if (TryProjectGroundMeleeRouteFromFirstStep(
                character,
                turnPlan,
                candidatePosition,
                start,
                out var projection))
        {
            if (projection.Goal is "contact" or "approach" &&
                IsGroundMeleeConnectedApproachProjection(
                    start,
                    candidatePosition,
                    targetPosition,
                    projection))
            {
                return false;
            }

            return true;
        }

        return true;
    }

    private static bool IsGroundMeleeConnectedApproachProjection(
        int3 start,
        int3 firstStep,
        int3 targetPosition,
        GroundMeleeRouteProjection projection)
    {
        if (projection.Goal == "contact")
        {
            return true;
        }

        if (projection.Goal == "approach")
        {
            return true;
        }

        if (projection.Goal is "capped" or "budget-capped")
        {
            return false;
        }

        if (projection.Goal != "incomplete")
        {
            return false;
        }

        return false;
    }

    private static void RecordGroundMeleeRouteMemoryGate(GameLocationCharacter character, CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null ||
            !IsGroundMeleePursuitPlan(turnPlan))
        {
            return;
        }

    }

    private static bool TryValidateForcedRouteDestination(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 start,
        int3 destination,
        bool requireActionAfterMove)
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

        if (!IsLegalAiRouteDestination(character, destination, out _))
        {
            return false;
        }

        if (RequiresMainDashForForcedMove(character, start, destination))
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
                turnPlan,
                out _))
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
            destination,
            out _);
        var breakThreatDefensiveFallback =
            !breakThreatAttackConnected &&
            IsBreakThreatDefensiveFallbackDestination(
                character,
                turnPlan,
                battleService,
                start,
                destination,
                out _);

        if (!breakThreatAttackConnected && !breakThreatDefensiveFallback)
        {
            return false;
        }

        if (breakThreatDefensiveFallback)
        {
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
        var cacheKey = GetReachableRouteDestinationCacheKey(
            character,
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

        var pathfindingCountKey = GetReachableRoutePathfindingCountKey(character, round, turnStamp);
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
        var moveCosts = new List<int>();
        var moveModes = new List<MoveMode>();
        var moveFlags = new List<PathfindingNode.InformationFlag>();
        var seen = new HashSet<string>();

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

            if (seen.Add(GetPositionKey(destination.position)))
            {
                positions.Add(destination.position);
                moveCosts.Add(destination.moveCost);
                moveModes.Add(destination.moveMode);
                moveFlags.Add(destination.flags);
            }
        }

        reachableDestinations = new ReachableRouteDestinationMemory(
            start,
            remainingMove,
            round,
            turnStamp,
            positions,
            moveCosts,
            moveModes,
            moveFlags,
            walkOnly);
        ReachableRouteDestinationCache[cacheKey] = reachableDestinations;

        return positions.Count > 0;
    }

    private static bool IsGroundRouteMoveMode(MoveMode moveMode)
    {
        return moveMode != MoveMode.Fly;
    }

    private static string GetReachableRouteDestinationCacheKey(
        GameLocationCharacter character,
        int3 start,
        int remainingMove,
        int round,
        int turnStamp,
        bool walkOnly)
    {
        return $"{character.Guid}:{GetPositionKey(start)}:{remainingMove}:{round}:{turnStamp}:{walkOnly}";
    }

    private static string GetReachableRoutePathfindingCountKey(
        GameLocationCharacter character,
        int round,
        int turnStamp)
    {
        return $"{character.Guid}:{round}:{turnStamp}";
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
        var cacheKey = GetReachableRouteDestinationCacheKey(
            character,
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

        var prefix = $"{character.Guid}:";

        foreach (var key in ReachableRouteDestinationCache.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                     .ToArray())
        {
            ReachableRouteDestinationCache.Remove(key);
        }

        foreach (var key in ReachableRoutePathfindingCountCache.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
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

        if (ActionLinkedMoveCache.ContainsKey(character.Guid))
        {
            return true;
        }

        if (GroundMeleeMoveSettlingCache.TryGetValue(character.Guid, out var settling) &&
            settling.Round == round &&
            settling.TurnStamp == turnStamp)
        {
            return true;
        }

        if (GroundMeleeNoMoveTerminalSealCache.TryGetValue(character.Guid, out var terminalSeal) &&
            terminalSeal.Matches(round, turnStamp))
        {
            return true;
        }

        if (RouteMoveCompletionClosedCache.TryGetValue(character.Guid, out var completion) &&
            completion.Round == round &&
            completion.TurnStamp == turnStamp)
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

    private static void MarkDashBlockedAfterRouteMove(
        GameLocationCharacter character,
        CombatAiMovementGoalKind movementGoal,
        int3 destination)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        RouteMoveDashBlockCache[character.Guid] = new RouteMoveDashBlockMemory(
            movementGoal,
            destination,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
    }

    private static bool TryGetActiveRouteMoveDashBlock(
        GameLocationCharacter character,
        out RouteMoveDashBlockMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !RouteMoveDashBlockCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        if (memory.Round == GetCurrentBattleRound() &&
            memory.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp))
        {
            return true;
        }

        RouteMoveDashBlockCache.Remove(character.Guid);
        memory = default;

        return false;
    }

    private static bool TryGetSearchKnownTargetDashContinuationValidation(
        GameLocationCharacter character,
        RouteMoveDashBlockMemory memory,
        out CombatAiMainActionValidation validation)
    {
        validation = default;

        if (character?.RulesetCharacter == null ||
            memory.MovementGoal != CombatAiMovementGoalKind.SearchKnownTarget ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction) ||
            !IsSearchKnownTargetRoute(pendingAction))
        {
            return false;
        }

        if (!TryGetSearchKnownTargetAnchor(
                character,
                pendingAction,
                out var anchor,
                out _))
        {
            validation = new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.ActionNotUsableAtPosition);
            return true;
        }

        var startDistance = ComputeGridDistance(pendingAction.StartPosition, anchor);
        var currentDistance = ComputeGridDistance(character.LocationPosition, anchor);

        if (currentDistance > startDistance + 0.1f)
        {
            validation = new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.ActionNotUsableAtPosition);
            return true;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService != null)
        {
            var profile = BuildProfile(character);
            var turnPlan = BuildCombatAiTurnPlan(character, profile, battleService);
            var currentTerminalScan = BuildCurrentTerminalActionScan(
                character,
                turnPlan.ActionProbe,
                battleService,
                profile,
                BuildSelfAssessment(character));

            if (currentTerminalScan.HasValidatedAction || currentTerminalScan.HasUsefulUtility)
            {
                validation = new CombatAiMainActionValidation(
                    false,
                    CombatAiMainActionBlockKind.ActionNotUsableAtPosition);
                return true;
            }
        }

        validation = new CombatAiMainActionValidation(
            true);
        return true;
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

    private static void SealGroundMeleeNoMoveTerminal(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        var memory = new TerminalSealMemory(
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        GroundMeleeNoMoveTerminalSealCache[character.Guid] = memory;

        var pendingAction = new ActionLinkedMoveMemory(
            null,
            CombatAiActionKind.Melee,
            CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove,
            CombatAiMovementGoalKind.AdvanceToMelee,
            character.LocationPosition,
            character.LocationPosition,
            CombatAiRouteMoveSourceKind.GroundMeleeUnresolved,
            false,
            memory.Round,
            memory.TurnStamp);

        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            character.LocationPosition,
            character.LocationPosition,
            "ground-melee-unresolved",
            null);
    }

    private static bool TryCloseGroundMeleeNoMoveTerminalSeal(
        GameLocationCharacter character,
        string phase,
        bool blockTerminal = true)
    {
        if (character?.RulesetCharacter == null ||
            !GroundMeleeNoMoveTerminalSealCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (!memory.Matches(currentRound, currentTurnStamp))
        {
            GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);

            return false;
        }

        _ = phase;
        _ = blockTerminal;

        return true;
    }

    private static bool TrySpendLeftoverActionEconomy(
        GameLocationCharacter character,
        bool allowActionLinkedMove,
        bool endTurnTerminal = false)
    {
        EnsureCombatAiRuntimeCache(character);
        var actionEconomy = BuildActionEconomySnapshot(character);
        TryConsumePendingUtilityTerminalContinuation(character);

        if (!IsAdvancedCombatAiActionEconomyEnabled ||
            !actionEconomy.CanAutoAct ||
            !actionEconomy.IsAiControlled ||
            character?.RulesetCharacter == null)
        {
            return false;
        }

        if (!IsActiveBattleContender(character))
        {
            TryNormalizePendingTerminalActionForInactiveContender(character);

            return false;
        }

        TryCloseGroundMeleeNoMoveTerminalSeal(character, "residual action", blockTerminal: false);

        if (!actionEconomy.MainAvailable &&
            !actionEconomy.ReadyAvailable &&
            !actionEconomy.DodgeAvailable)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null || !HasLeftoverActionCombatContext(character))
        {
            return false;
        }

        var profile = BuildProfile(character);
        var self = BuildSelfAssessment(character);
        var turnPlan = allowActionLinkedMove
            ? BuildCombatAiTurnPlan(character, profile, battleService)
            : BuildCombatAiActionOnlyTurnPlan(character, profile, battleService);
        var hostileActionFailed = false;
        var hostileActionPolicyHeld = false;

        var meleeAlternativeResult = TryUseRepeatedMeleeAlternativeAction(character, turnPlan, battleService);

        if (meleeAlternativeResult.Executed)
        {
            return true;
        }

        if (turnPlan.ActionProbe.CanUsePreferredAction ||
            (turnPlan.ActionProbe.CanUseBackupAction && !turnPlan.MovementPlan.HasGoal))
        {
            if (allowActionLinkedMove ||
                !ShouldReleaseRangedBackupAfterFailedMeleePursuit(
                    character,
                    turnPlan.ActionProbe,
                    battleService))
            {
                var attackResult = TryUseResidualSafeHostileAction(
                         character,
                         turnPlan.ActionProbe,
                         battleService,
                         out _);

                if (attackResult.Executed)
                {
                    return true;
                }

                if (attackResult.PolicyHeld)
                {
                    hostileActionPolicyHeld = true;
                }
                else
                {
                    hostileActionFailed = true;
                }
            }
        }

        var movementAvailability = GetMovementAvailability(character, turnPlan, forResidualAction: true);
        var movementDeferredForTerminal = false;

        if (allowActionLinkedMove &&
            movementAvailability.IsAvailable &&
            IsSearchKnownTargetPlan(turnPlan) &&
            TryUseLostTargetSearchRouteMove(
                character,
                turnPlan,
                battleService,
                profile,
                CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove,
                CombatAiRouteMoveSourceKind.SearchLostTarget,
                out _))
        {
            return true;
        }

        _ = TryGetMissedMovementState(character, turnPlan, out _, out _);

        if (turnPlan.ActionProbe.CanUseBackupAction &&
            movementAvailability.Availability is CombatAiMovementAvailability.Blocked or CombatAiMovementAvailability.Spent &&
            TryUseResidualSafeHostileAction(
                character,
                turnPlan.ActionProbe.Target,
                turnPlan.ActionProbe.BackupAction,
                battleService).Executed)
        {
            return true;
        }

        var terminalReprobeResult = TryUseTerminalReprobeHostileAction(character, turnPlan, battleService);

        if (terminalReprobeResult.Executed)
        {
            return true;
        }


        var currentTerminalScan = BuildCurrentTerminalActionScan(
            character,
            turnPlan.ActionProbe,
            battleService,
            profile,
            self);
        var hasTerminalPolicyHeld = HasTerminalPolicyHeld(hostileActionPolicyHeld, terminalReprobeResult.PolicyHeld);
        var hasTerminalFallbackBlock = TryGetTerminalFallbackBlock(
            currentTerminalScan,
            hasTerminalPolicyHeld,
            out _);
        var hasPendingSearchMovement = HasPendingSearchKnownTargetMovement(character);
        var hasSearchMovementAvailable =
            allowActionLinkedMove &&
            movementAvailability.IsAvailable &&
            IsSearchKnownTargetPlan(turnPlan);
        var hasDisconnectedMovementLeak = TryGetDisconnectedPositioningMovementLeak(
            character,
            out _);

        var hasGroundMeleePartialProgress = TryGetActiveGroundMeleePartialRouteProgress(
            character,
            out var partialRouteMemory);

        if (movementAvailability.IsAvailable &&
            turnPlan.MovementPlan.HasGoal &&
            !hasGroundMeleePartialProgress)
        {
            movementDeferredForTerminal = true;
        }

        if (IsGroundMeleePursuitPlan(turnPlan) &&
            turnPlan.MovementPlan.HasGoal &&
            !hasGroundMeleePartialProgress)
        {
            movementDeferredForTerminal = true;
        }

        var canSpendTerminalMain = CanSpendTerminalMainAction(
            character,
            actionEconomy);

        if (!hasGroundMeleePartialProgress &&
            canSpendTerminalMain &&
            endTurnTerminal &&
            !hasPendingSearchMovement &&
            !hasSearchMovementAvailable &&
            !hasTerminalFallbackBlock &&
            TryUseFallbackReady(
                      character,
                      profile,
                      turnPlan,
                      battleService,
                      currentTerminalScan,
                      endTurnTerminal))
        {
            RecordTerminalAction(character, turnPlan, CombatAiExecutedActionKind.Ready);
            return true;
        }

        if (TryUseFallbackAtWillSelfBuff(character, profile, self, turnPlan))
        {
            return true;
        }


        var shouldUseDodge =
            !hasPendingSearchMovement &&
            !hasSearchMovementAvailable &&
            !hasTerminalFallbackBlock &&
            !hasDisconnectedMovementLeak &&
            canSpendTerminalMain &&
            (ShouldUseFallbackDodge(character, battleService, turnPlan) ||
             hostileActionFailed ||
             hasTerminalPolicyHeld ||
             hasGroundMeleePartialProgress ||
             movementDeferredForTerminal);

        if (endTurnTerminal && actionEconomy.DodgeAvailable && shouldUseDodge)
        {
            if (TryApplyFallbackDodge(character, endTurnTerminal).Executed)
            {
                RecordTerminalAction(character, turnPlan, CombatAiExecutedActionKind.Dodge);

                return true;
            }

        }
        else if (hasDisconnectedMovementLeak)
        {
            return false;
        }

        return false;
    }

    internal static float ComputeEnemyProximityScore(
        DecisionContext context,
        ConsiderationDescription consideration,
        DecisionParameters parameters)
    {
        var character = parameters.character.GameLocationCharacter;
        EnsureCombatAiRuntimeCache(character);
        var rulesetCharacter = character.RulesetCharacter;
        var approachSourceGuid = GetApproachSourceGuid(rulesetCharacter, consideration.StringParameter);

        if (!IsAdvancedCombatAiEnabled)
        {
            return ComputeBaselineEnemyProximityScore(context, consideration, parameters, approachSourceGuid);
        }

        var denominator = consideration.IntParameter > 0 ? consideration.IntParameter : 1;
        var floatParameter = consideration.FloatParameter;
        var position = consideration.BoolParameter ? context.position : character.LocationPosition;
        var profile = BuildProfile(character);
        var self = BuildSelfAssessment(character);

        UpdateObservedCombatMemory(character, parameters);
        var evaluations = CollectEnemyEvaluations(character, profile, position, parameters, approachSourceGuid);
        var turnPlan = BuildCombatAiTurnPlan(character, profile, parameters.situationalInformation.BattleService);

        if (evaluations.Length == 0)
        {
            return IsAdvancedCombatAiPositioningEnabled && IsPositionBasedScoring(consideration)
                ? ComputeSearchOrRegroupScore(character, position, parameters, floatParameter, turnPlan)
                : 0f;
        }

        if (IsPositionBasedScoring(consideration) &&
            TryComputeTurnPlanMovementScore(character, profile, position, parameters, turnPlan, out var planScore))
        {
            return planScore;
        }

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
                ? Mathf.Lerp(0.0f, 1f, Mathf.Clamp(evaluation.Distance / Math.Max(floatParameter, 1f), 0.0f, 1f))
                : ComputeDistancePreferenceScore(profile, evaluation.Distance, floatParameter);

            if (!evaluation.IsApproachSource && evaluation.CanAttackFromPosition)
            {
                distanceScore = Math.Max(distanceScore, evaluation.RangedCoverType <= CoverType.Half ? 0.95f : 0.85f);
            }

            numerator += distanceScore * ComputeEnemyPriorityWeight(profile, evaluation);
        }

        var score = numerator / denominator;

        if (IsAdvancedCombatAiPositioningEnabled)
        {
            score += ComputePositionBias(profile, self, evaluations, floatParameter);

            if (IsPositionBasedScoring(consideration))
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

    private static bool TryGetRegroupPosition(
        GameLocationCharacter actor,
        DecisionParameters parameters,
        out int3 position)
    {
        position = default;

        if (actor?.RulesetCharacter == null || parameters?.situationalInformation == null)
        {
            return false;
        }

        var positioningService = parameters.situationalInformation.PositioningService;
        var bestDistance = float.MaxValue;
        var hasRegroup = false;

        foreach (var ally in parameters.situationalInformation.RelevantAllies)
        {
            if (ally == actor || ally?.RulesetCharacter == null)
            {
                continue;
            }

            var distance = positioningService.ComputeDistanceBetweenCharactersApproximatingSize(
                actor, actor.LocationPosition, ally, ally.LocationPosition);

            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            position = ally.LocationPosition;
            hasRegroup = true;
        }

        return hasRegroup;
    }

    private static bool TryGetRegroupPosition(GameLocationCharacter actor, out int3 position)
    {
        position = default;

        if (actor?.RulesetCharacter == null || Gui.Battle == null)
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        var hasRegroup = false;

        var contenders = Gui.Battle.AllContenders.ToArray();

        foreach (var ally in contenders)
        {
            if (ally == actor || ally?.RulesetCharacter == null || ally.Side != actor.Side)
            {
                continue;
            }

            var distance = ComputeGridDistance(actor.LocationPosition, ally.LocationPosition);

            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            position = ally.LocationPosition;
            hasRegroup = true;
        }

        return hasRegroup;
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

        var movementProgress = new AiTurnMovementProgress(
            character.LocationPosition,
            ComputeGridDistance(character.LocationPosition, turnPlan.MovementPlan.TargetPosition));
        TurnMovementProgressCache[character.Guid] = movementProgress;
        PrimeFlyingMeleeSupplementalMovementProgress(character, turnPlan, movementProgress);
    }

    private static AiTurnMovementProgress GetOrCreateTurnMovementProgress(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        if (character == null)
        {
            return null;
        }

        if (TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress))
        {
            return movementProgress;
        }

        if (!turnPlan.MovementPlan.HasGoal)
        {
            return null;
        }

        movementProgress = new AiTurnMovementProgress(
            character.LocationPosition,
            ComputeGridDistance(character.LocationPosition, turnPlan.MovementPlan.TargetPosition));
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

        movementProgress?.MarkVisited(
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
        var evaluated = 0;
        var attackCapable = 0;
        var safe = 0;
        var nonCrowded = 0;
        var accepted = 0;
        var bestPosition = start;
        var bestScore = float.MinValue;

        foreach (var candidate in EnumerateFlyingMeleeCandidatePositions(actor, target))
        {
            if (candidate == start || movementProgress.HasVisited(candidate))
            {
                continue;
            }

            evaluated++;

            if (IsBlockingCombatantAtPosition(actor, candidate))
            {
                movementProgress.RecordRejected(AiMoveRejectionKind.Traffic);
                continue;
            }

            if (RequiresMainDashForForcedMove(actor, start, candidate))
            {
                movementProgress.RecordRejected(AiMoveRejectionKind.NoProgress);
                continue;
            }

            if (HasForcedRouteOpportunityExposure(actor, start, candidate, battleService))
            {
                movementProgress.RecordRejected(AiMoveRejectionKind.Opportunity);
                continue;
            }

            if (!CanUseActionKindAtPosition(actor, candidate, target, CombatAiActionKind.Melee, battleService))
            {
                movementProgress.RecordRejected(AiMoveRejectionKind.NoProgress);
                continue;
            }

            attackCapable++;

            if (ShouldRejectMeleeSpacingTrafficMove(actor, candidate, target, battleService, out _))
            {
                movementProgress.RecordRejected(AiMoveRejectionKind.Traffic);
                continue;
            }

            safe++;
            var adjacentAllies = CountMeleeAllyCrowding(actor, target, candidate);
            var nearestAllyGap = GetNearestMeleeAllyCrowdingHorizontalGap(actor, target, candidate);
            var clearsCrowding = adjacentAllies == 0 && nearestAllyGap >= MeleeSpacingRequiredGridGap;

            if (clearsCrowding)
            {
                nonCrowded++;
            }
            else if (turnPlan.MovementPlan.Policy == CombatAiMovementPolicyKind.FlyingMeleeSpacingPolicy)
            {
                movementProgress.RecordRejected(AiMoveRejectionKind.NoProgress);
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
            accepted++;

            if (score > bestScore + 0.000001f)
            {
                bestScore = score;
                bestPosition = candidate;
            }
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
        var seen = new HashSet<string>();

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

                        if (seen.Add(GetPositionKey(candidate)))
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
        out AiRouteDestinationLegality legality,
        bool allowActorCurrentPosition = false,
        bool allowNonOccupyingCombatProxy = true)
    {
        legality = AiRouteDestinationLegality.Legal;

        if (actor?.RulesetCharacter == null)
        {
            legality = AiRouteDestinationLegality.MissingActor;
            return false;
        }

        if (destination == actor.LocationPosition)
        {
            if (allowActorCurrentPosition)
            {
                return true;
            }

            legality = AiRouteDestinationLegality.NoProgress;
            return false;
        }

        if (IsBlockingCombatantAtPosition(actor, destination))
        {
            legality = AiRouteDestinationLegality.Occupied;
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

        legality = AiRouteDestinationLegality.CannotPlace;
        return false;
    }

    private static bool IsBlockingCombatantAtPosition(GameLocationCharacter actor, int3 position)
    {
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

    private static string GetPositionKey(int3 position)
    {
        return $"{position.x}:{position.y}:{position.z}";
    }

    private static string GetRouteCandidateCacheKey(
        GameLocationCharacter actor,
        int3 actorPosition,
        GameLocationCharacter target,
        CombatAiActionKind actionKind)
    {
        return actor?.RulesetCharacter == null || target?.RulesetCharacter == null
            ? string.Empty
            : string.Join(
                "|",
                actor.Guid,
                GetPositionKey(actorPosition),
                target.Guid,
                GetPositionKey(target.LocationPosition),
                actionKind);
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
            if (mode == null || !IsRangedAttackMode(mode))
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
            battleService == null)
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

    private static bool HasAtWillHostileSpell(RulesetCharacter rulesetCharacter)
    {
        return BuildAtWillHostileSpellSummary(rulesetCharacter).Count > 0;
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

        foreach (var cantrip in OrderAtWillAttackCantrips(rulesetCharacter, cantrips))
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

        return new CombatAiTurnPlan(actionProbe, movementPlan);
    }

    private static CombatAiTurnPlan BuildCombatAiActionOnlyTurnPlan(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        IGameLocationBattleService battleService)
    {
        var actionProbe = BuildCombatAiActionProbe(actor, profile, battleService);

        return new CombatAiTurnPlan(actionProbe, default);
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
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.NoMovement);
        }

        if (!IsLegalAiRouteDestination(
                actor,
                candidate,
                out _,
                allowNonOccupyingCombatProxy: false))
        {
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.Placement);
        }

        if (IsRejectedAiMoveTarget(actor, start, candidate))
        {
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.Retry);
        }

        var moveCost = ComputeForcedMoveCost(start, candidate);
        var maximumMoveCost = Math.Min(Math.Max(0, actor.RemainingTacticalMoves), MeleeSpacingMaximumMoveCost);

        if (moveCost <= 0 || moveCost > maximumMoveCost)
        {
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.MoveCost);
        }

        if (WouldClearAllyCorridorTriggerOpportunityAttack(
                actor,
                start,
                candidate,
                target,
                battleService))
        {
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.Opportunity);
        }

        if (WouldClearAllyCorridorStillBlockTargetAlly(
                actor,
                candidate,
                target,
                battleService,
                turnPlan))
        {
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.StillBlocks);
        }

        var candidateCanMelee =
            CanUseActionKindAtPosition(actor, candidate, target, CombatAiActionKind.Melee, battleService);

        if (currentCanMelee && !candidateCanMelee)
        {
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.Pressure);
        }

        var currentDistance = ComputeGridDistance(start, target.LocationPosition);
        var candidateDistance = ComputeGridDistance(candidate, target.LocationPosition);

        if (candidateDistance > currentDistance + ClearAllyCorridorRegressionTolerance)
        {
            return new ClearAllyCorridorCandidateResult(false, blockKind: ClearAllyCorridorCandidateBlockKind.Pressure);
        }

        var trafficPenalty = 0f;
        var hasTrafficSoftPenalty = false;

        if (ShouldRejectAllyFireLaneGate(actor, candidate, target, battleService))
        {
            hasTrafficSoftPenalty = true;
            trafficPenalty = 0.10f;
        }

        var score =
            MovementGoalPreferredRangeScore +
            ComputeStableTieBreakScore(actor, turnPlan, candidate, CombatAiActionKind.Melee) +
            (candidateCanMelee ? 0.15f : 0f) -
            (moveCost * 0.04f) -
            Math.Max(0f, candidateDistance - currentDistance) * 0.03f -
            trafficPenalty;
        return new ClearAllyCorridorCandidateResult(true, score, hasTrafficSoftPenalty);
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
        return pendingAction.RouteMoveSource is CombatAiRouteMoveSourceKind.ConnectedFiringLine
            or CombatAiRouteMoveSourceKind.ConnectedFiringLineRecovery;
    }

    private static bool IsSearchKnownTargetRoute(ActionLinkedMoveMemory pendingAction)
    {
        return pendingAction.MovementGoal == CombatAiMovementGoalKind.SearchKnownTarget &&
               pendingAction.RouteMoveSource == CombatAiRouteMoveSourceKind.SearchLostTarget;
    }

    private static bool HasConnectedFiringLineRouteFailed(
        CombatAiPreMainRouteMoveStatus status)
    {
        return status != CombatAiPreMainRouteMoveStatus.Executed;
    }

    private static void ScheduleConnectedFiringLineTerminalAfterFailedRoute(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        CombatAiActionEconomySnapshot actionEconomy,
        CombatAiPreMainRouteMoveStatus status,
        int3 expectedDestination)
    {
        if (character?.RulesetCharacter == null ||
            !actionEconomy.MainAvailable ||
            !IsConnectedFiringLinePlan(turnPlan) ||
            !HasConnectedFiringLineRouteFailed(status))
        {
            return;
        }

        var destination = expectedDestination.Equals(default(int3))
            ? character.LocationPosition
            : expectedDestination;
        var pendingAction = new ActionLinkedMoveMemory(
            turnPlan.ActionProbe.Target,
            turnPlan.ActionProbe.PreferredAction,
            CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove,
            turnPlan.MovementPlan.Goal,
            character.LocationPosition,
            destination,
            CombatAiRouteMoveSourceKind.ConnectedFiringLine,
            false,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        SchedulePendingRouteActionOnlyTerminal(
            character,
            pendingAction,
            destination,
            character.LocationPosition,
            "connected-firing-line",
            string.Empty);
    }

    private static void CloseFailedConnectedFiringLineRoute(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        CombatAiActionEconomySnapshot actionEconomy,
        int3 expectedDestination)
    {
        if (character?.RulesetCharacter == null ||
            !IsConnectedFiringLinePlan(turnPlan))
        {
            return;
        }

        RecordDisconnectedPositioningSeal(
            character,
            turnPlan);


        ScheduleConnectedFiringLineTerminalAfterFailedRoute(
            character,
            turnPlan,
            actionEconomy,
            CombatAiPreMainRouteMoveStatus.Blocked,
            expectedDestination);
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
            if (TryGetSameTurnNoMoveProxyThreatAttempt(actor, out var previousProxyAttempt))
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

    private static bool TryGetRepeatedMeleeAttackMemory(
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
               memory.ActionKind == CombatAiActionKind.Melee;
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
                    proxyThreatMemory,
                    ServiceRepository.GetService<IGameLocationBattleService>()))
            {
                return false;
            }

        }
        else if (isRecentThreat && actionProbe.CanUsePreferredAction)
        {
            if (ShouldEvaluateRecentThreatPreMainBreakThreat(
                    actor,
                    threatPosition,
                    ServiceRepository.GetService<IGameLocationBattleService>()))
            {
            }
            else
            {
                return false;
            }
        }
        else if (isRecentThreat)
        {
        }

        _ = threatPosition;

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
        RecentMeleeThreatMemory memory,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        if (EvaluateProxyMeleeThreatState(actor, memory) != ProxyThreatActivityState.Active)
        {
            return false;
        }

        _ = battleService;
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

        var canUseCache = IsAdvancedCombatAiEnabled;
        var cacheKey = canUseCache
            ? GetRouteCandidateCacheKey(actor, actorPosition, target, actionKind)
            : string.Empty;

        if (canUseCache &&
            !string.IsNullOrEmpty(cacheKey) &&
            ActionKindPositionCache.TryGetValue(cacheKey, out var cached))
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

        if (canUseCache && !string.IsNullOrEmpty(cacheKey))
        {
            ActionKindPositionCache[cacheKey] = canUseAction;
        }

        return canUseAction;
    }

    private static bool TryRecordGroundMeleeLongRoutePursuitCandidate(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        AiTurnMovementProgress movementProgress,
        int3 candidatePosition,
        float currentDistance,
        float candidateDistance,
        float progress,
        int currentTurnsToAction,
        int candidateTurnsToAction)
    {
        if (actor?.RulesetCharacter == null || !IsGroundMeleePursuitPlan(turnPlan))
        {
            return false;
        }

        return false;
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
        int3 candidatePosition,
        out BreakThreatConnectionBlockKind blockKind)
    {
        blockKind = BreakThreatConnectionBlockKind.NotBreakThreat;

        if (turnPlan.MovementPlan.Goal != CombatAiMovementGoalKind.BreakThreat)
        {
            return true;
        }

        if (actor?.RulesetCharacter == null || battleService == null)
        {
            blockKind = BreakThreatConnectionBlockKind.MissingActorOrBattleService;
            return false;
        }

        if (ComputeForcedMoveCost(start, candidatePosition) <= 1)
        {
            blockKind = BreakThreatConnectionBlockKind.NearStart;
            return false;
        }

        if (WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService))
        {
            blockKind = BreakThreatConnectionBlockKind.ThreatRange;
            return false;
        }

        var target = turnPlan.ActionProbe.Target;

        if (target?.RulesetCharacter == null)
        {
            blockKind = BreakThreatConnectionBlockKind.NoTarget;
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
            blockKind = BreakThreatConnectionBlockKind.None;
            return true;
        }

        blockKind = BreakThreatConnectionBlockKind.NoPostMoveAttack;
        return false;
    }

    private static bool IsBreakThreatDefensiveFallbackDestination(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 start,
        int3 candidatePosition,
        out BreakThreatDefensiveFallbackBlockKind blockKind)
    {
        blockKind = BreakThreatDefensiveFallbackBlockKind.NotBreakThreat;

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
            blockKind = BreakThreatDefensiveFallbackBlockKind.Policy;
            return false;
        }

        if (ComputeForcedMoveCost(start, candidatePosition) <= 1)
        {
            blockKind = BreakThreatDefensiveFallbackBlockKind.NearStart;
            return false;
        }

        if (!TryGetCurrentOrRecentMeleeThreat(
                actor,
                start,
                out _,
                out var threatPosition,
                out _))
        {
            blockKind = BreakThreatDefensiveFallbackBlockKind.NoThreat;
            return false;
        }

        if (WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService))
        {
            blockKind = BreakThreatDefensiveFallbackBlockKind.ThreatRange;
            return false;
        }

        var threatProgress = ComputeGridDistance(candidatePosition, threatPosition) -
                             ComputeGridDistance(start, threatPosition);

        if (threatProgress < ThreatAvoidanceMinimumDistanceGain)
        {
            blockKind = BreakThreatDefensiveFallbackBlockKind.InsufficientGain;
            return false;
        }

        blockKind = BreakThreatDefensiveFallbackBlockKind.None;
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
            movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
            return true;
        }

        if (!IsLegalAiRouteDestination(actor, candidatePosition, out _))
        {
            movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
            return true;
        }

        if (HasOpportunityAttackRisk(actor, actor.LocationPosition, candidatePosition, battleService))
        {
            movementProgress?.RecordRejected(AiMoveRejectionKind.Opportunity);
            return true;
        }

        if (ShouldRejectTrafficBlockingMove(
                actor,
                candidatePosition,
                battleService,
                profile,
                turnPlan,
                out _))
        {
            movementProgress?.RecordRejected(AiMoveRejectionKind.Traffic);
            return true;
        }

        if (ShouldRejectThreatAvoidanceReturnMove(actor, profile, turnPlan, candidatePosition))
        {
            movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
                return true;
            }

            if (ShouldRequireReachableProxyThreatDestination(actor, turnPlan) &&
                !TryValidateReachableProxyThreatDestination(
                    actor,
                    actor.LocationPosition,
                    candidatePosition))
            {
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
                return true;
            }

            if (WouldBeInCurrentOrRecentMeleeThreat(actor, candidatePosition, battleService))
            {
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
                return true;
            }

            if (movementProgress != null && movementProgress.HasVisited(candidatePosition))
            {
                movementProgress.RecordRejected(AiMoveRejectionKind.Regression);
                return true;
            }

            var breakThreatAttackConnected = IsBreakThreatDestinationAttackConnected(
                actor,
                turnPlan,
                battleService,
                actor.LocationPosition,
                candidatePosition,
                out _);
            var breakThreatDefensiveFallback =
                !breakThreatAttackConnected &&
                IsBreakThreatDefensiveFallbackDestination(
                    actor,
                    turnPlan,
                    battleService,
                    actor.LocationPosition,
                    candidatePosition,
                    out _);

            if (!breakThreatAttackConnected && !breakThreatDefensiveFallback)
            {
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
                return true;
            }

            var threatProgress = ComputeGridDistance(candidatePosition, currentThreatPosition) -
                                 ComputeGridDistance(actor.LocationPosition, currentThreatPosition);

            var minimumThreatProgress = ThreatAvoidanceMinimumDistanceGain;

            if (threatProgress < minimumThreatProgress)
            {
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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

            if (breakThreatDefensiveFallback)
            {
            }

            movementProgress?.MarkPreferredActionMovementCandidate();
            movementProgress?.RecordAccepted(candidatePosition, score, Math.Max(0f, threatProgress));

            return true;
        }

        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MaintainThreatAvoidance)
        {
            if (ShouldRequireReachableProxyThreatDestination(actor, turnPlan) &&
                !TryValidateReachableProxyThreatDestination(
                    actor,
                    actor.LocationPosition,
                    candidatePosition))
            {
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
                return true;
            }

            if (!TryComputeMaintainThreatAvoidanceScore(
                    actor,
                    candidatePosition,
                    battleService,
                    turnPlan,
                    out score))
            {
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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
                    movementProgress,
                    out score,
                    out var actionKind))
            {
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
                return true;
            }

            score += ComputeStableTieBreakScore(actor, turnPlan, candidatePosition, actionKind);
            movementProgress?.MarkMeaningfulMovementCandidate();
            movementProgress?.RecordAccepted(
                candidatePosition,
                score,
                progress,
                preferredActionCandidate: true,
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

        if (canBackupFromCandidate && !IsGroundMeleePursuitPlan(turnPlan))
        {
            movementProgress?.MarkPreferredActionMovementCandidate();
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

            movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
            return true;
        }

        if (RequiresPostMoveActionConnectedPositioning(turnPlan) &&
            movementProgress?.HadPreferredActionMovementCandidate == true)
        {
            CurrentStateRouteBlockCache[actor.Guid] = CurrentStateRouteBlockKind.NoPostMoveAction;
            movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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
                movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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
            movementProgress?.RecordRejected(AiMoveRejectionKind.Regression);
            return true;
        }

        if (candidateDistance > currentDistance + MovementGoalRegressionTolerance ||
            progress <= 0f)
        {
            TryRecordGroundMeleeLongRoutePursuitCandidate(
                actor,
                turnPlan,
                movementProgress,
                candidatePosition,
                currentDistance,
                candidateDistance,
                progress,
                currentTurnsToAction,
                candidateTurnsToAction);
            movementProgress?.RecordRejected(AiMoveRejectionKind.Regression);
            return true;
        }

        if (movementProgress != null &&
            (movementProgress.HasVisited(candidatePosition) ||
             candidateDistance > movementProgress.BestDistanceToGoal + MovementGoalRegressionTolerance))
        {
            TryRecordGroundMeleeLongRoutePursuitCandidate(
                actor,
                turnPlan,
                movementProgress,
                candidatePosition,
                currentDistance,
                candidateDistance,
                progress,
                currentTurnsToAction,
                candidateTurnsToAction);
            movementProgress.RecordRejected(AiMoveRejectionKind.Regression);
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
            movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
            return true;
        }

        if (!canPreferredFromCandidate &&
            turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.MoveToPreferredRange &&
            TryGetMinimumLineRouteProgress(turnPlan.MovementPlan.Policy, out var minimumLineProgress) &&
            progress + 0.01f < minimumLineProgress)
        {
            movementProgress?.RecordRejected(AiMoveRejectionKind.NoProgress);
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

        if (!TryGetGroundMeleeTargetContactRouteQuery(actor, turnPlan, out var query) ||
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

        if (!query.TryGetContactCost(candidatePosition, out var candidateContactMoveCost, out var contactGoal))
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

        if (ShouldRejectMeleeSpacingTrafficMove(actor, candidatePosition, target, battleService, out _))
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

        var currentAdjacentAllyCount = CountMeleeAllyCrowding(actor, target, actor.LocationPosition);
        var candidateAdjacentAllyCount = CountMeleeAllyCrowding(actor, target, candidatePosition);
        var currentNearestAlly = GetNearestMeleeAllyCrowdingHorizontalGap(actor, target, actor.LocationPosition);
        var candidateNearestAlly = GetNearestMeleeAllyCrowdingHorizontalGap(actor, target, candidatePosition);
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
            currentAdjacentAllyCount > 0 &&
            candidateAdjacentAllyCount == 0 &&
            candidateNearestAlly >= MeleeSpacingRequiredGridGap;

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
            score += 0.18f + (currentAdjacentAllyCount * 0.04f);
        }

        if (clearsLargeAllyCorridor)
        {
            score += 0.22f;
        }

        if (currentNearestAlly < int.MaxValue &&
            candidateNearestAlly > currentNearestAlly)
        {
            score += Math.Min(0.06f, (candidateNearestAlly - currentNearestAlly) * 0.02f);
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
        IGameLocationBattleService battleService,
        out TrafficBlockKind blockKind)
    {
        blockKind = TrafficBlockKind.None;

        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null)
        {
            return false;
        }

        if (ShouldRejectAllyFireLaneGate(actor, destination, target, battleService))
        {
            blockKind = TrafficBlockKind.AllyFireLane;
            return true;
        }

        if (WouldPositionBlockLargeMeleeAllyCorridor(actor, destination, target, battleService, out _))
        {
            blockKind = TrafficBlockKind.LargeAllyCorridor;
            return true;
        }

        return false;
    }

    private static int CountMeleeAllyCrowding(
        GameLocationCharacter actor,
        GameLocationCharacter target,
        int3 actorPosition)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            Gui.Battle == null)
        {
            return 0;
        }

        var count = 0;

        foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), target.LocationPosition))
        {
            if (ally == actor ||
                ally?.RulesetCharacter == null ||
                ally.Side != actor.Side ||
                ComputeHorizontalGridStepDistance(ally.LocationPosition, actorPosition) >
                MeleeSpacingAllyAdjacentGridSteps ||
                ComputeHorizontalGridStepDistance(ally.LocationPosition, target.LocationPosition) >
                MeleeSpacingTargetPressureDistance)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private static int GetNearestMeleeAllyCrowdingHorizontalGap(
        GameLocationCharacter actor,
        GameLocationCharacter target,
        int3 actorPosition)
    {
        if (actor?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            Gui.Battle == null)
        {
            return int.MaxValue;
        }

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

            nearest = Math.Min(nearest, ComputeHorizontalGridStepDistance(ally.LocationPosition, actorPosition));
        }

        return nearest;
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
        AiTurnMovementProgress movementProgress,
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
            movementProgress?.RecordImproveFiringCandidate(
                false,
                false,
                false,
                false,
                ImproveFiringCandidateState.Blocked);
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

        if (!attackCapable)
        {
            movementProgress?.RecordImproveFiringCandidate(
                false,
                false,
                false,
                false,
                ImproveFiringCandidateState.Blocked);
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
        var connectsFiringLine = IsConnectedFiringLinePlan(turnPlan);
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
        var safeCandidate = attackCapable && avoidsThreat;
        var differentSafeCell = safeCandidate && candidatePosition != currentPosition;
        movementProgress?.RecordImproveFiringCandidate(
            attackCapable,
            safeCandidate,
            strictImprovement,
            differentSafeCell,
            strictImprovement || forcedSafeReposition
                ? ImproveFiringCandidateState.Accepted
                : ImproveFiringCandidateState.Blocked);

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

        var canUseCache = IsAdvancedCombatAiEnabled;
        var cacheKey = canUseCache
            ? GetRouteCandidateCacheKey(actor, actorPosition, target, actionKind) + "|cover"
            : string.Empty;

        if (canUseCache &&
            !string.IsNullOrEmpty(cacheKey) &&
            CoverEvaluationCache.TryGetValue(cacheKey, out var cached))
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
            if (canUseCache && !string.IsNullOrEmpty(cacheKey))
            {
                CoverEvaluationCache[cacheKey] = new CoverEvaluationMemory(false, coverType);
            }

            return false;
        }

        coverType = modifier?.coverType ?? CoverType.ThreeQuarter;

        if (canUseCache && !string.IsNullOrEmpty(cacheKey))
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

    private static EnemyEvaluation[] CollectEnemyEvaluations(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        int3 position,
        DecisionParameters parameters,
        ulong approachSourceGuid)
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

        if (relevantEnemies.Count == 0)
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

    private static void UpdateTacticalSituationMemory(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return;
        }

        var memory = BuildTacticalSituationMemory(actor);

        TacticalSituationMemoryCache[actor.Guid] = memory;
    }

    private static TacticalSituationMemory BuildTacticalSituationMemory(GameLocationCharacter actor)
    {
        var self = BuildSelfAssessment(actor);
        var target = SelectPrimaryTarget(actor);
        var targetSelf = BuildSelfAssessment(target);
        var alliesDowned = 0;
        var alliesCritical = 0;

        if (Gui.Battle != null)
        {
            foreach (var ally in OrderCharactersForCombatAi(Gui.Battle.AllContenders.ToArray(), actor.LocationPosition))
            {
                if (ally == actor || ally?.RulesetCharacter == null || ally.Side != actor.Side)
                {
                    continue;
                }

                if (ally.RulesetCharacter.IsDeadOrDyingOrUnconscious)
                {
                    alliesDowned++;
                    continue;
                }

                if (BuildSelfAssessment(ally).IsCritical)
                {
                    alliesCritical++;
                }
            }
        }

        var targetDodging = HasDodgingCondition(target?.RulesetCharacter);
        var recentThreat = TryGetRecentMeleeThreat(actor, out var recentThreatMemory)
            ? recentThreatMemory.Source?.Guid ?? 0
            : 0;
        var signature =
            $"self:{self.IsBloodied}:{self.IsCritical}:{self.HasSeriousCondition}:{self.IsConcentrating};" +
            $"ally:{alliesDowned}:{alliesCritical};" +
            $"target:{target?.Guid ?? 0}:{targetSelf.IsBloodied}:{targetSelf.IsCritical}:" +
            $"{targetSelf.HasSeriousCondition}:{targetSelf.IsConcentrating}:{targetDodging};" +
            $"threat:{recentThreat}";
        return new TacticalSituationMemory(
            signature,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));
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
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLeftoverActionCombatContext(GameLocationCharacter character)
    {
        return HasRelevantPerceivedEnemies(character) ||
               TryGetLastKnownEnemyPosition(character, out _) ||
               TryGetRegroupPosition(character, out _);
    }

    private static TerminalReprobeResult TryUseTerminalReprobeHostileAction(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return new TerminalReprobeResult(TerminalReprobeStatus.Blocked);
        }

        var target = turnPlan.ActionProbe.Target ?? SelectPrimaryTarget(character);

        if (target?.RulesetCharacter == null)
        {
            return new TerminalReprobeResult(TerminalReprobeStatus.Blocked);
        }

        ClearTerminalReprobeActionCaches();

        var preferred = turnPlan.ActionProbe.PreferredAction;
        var backup = turnPlan.ActionProbe.BackupAction;
        var backupPolicyHeld = false;

        if (preferred != CombatAiActionKind.None)
        {
            if (TryUseResidualSafeHostileAction(character, target, preferred, battleService).Executed)
            {
                return new TerminalReprobeResult(TerminalReprobeStatus.Executed, preferred);
            }
        }

        if (backup != CombatAiActionKind.None && backup != preferred)
        {
            if (TryUseTerminalRangedBackupAfterFailedMeleePursuit(
                character,
                turnPlan.ActionProbe,
                battleService))
            {
                return new TerminalReprobeResult(TerminalReprobeStatus.Executed, backup);
            }

            if (ShouldSuppressRangedBackupForMeleeRoute(
                    character,
                    turnPlan.ActionProbe,
                    battleService))
            {
                backupPolicyHeld = true;
                // The actor can still pursue or perform melee this turn; do not spend Main on a thrown fallback.
            }
            else if (TryUseResidualSafeHostileAction(character, target, backup, battleService).Executed)
            {
                return new TerminalReprobeResult(TerminalReprobeStatus.Executed, backup);
            }
        }

        if (turnPlan.ActionProbe.HasAtWillHostileSpell &&
            preferred != CombatAiActionKind.Spell &&
            backup != CombatAiActionKind.Spell)
        {
            if (TryUseResidualSafeHostileAction(
                    character,
                    target,
                    CombatAiActionKind.Spell,
                    battleService).Executed)
            {
                return new TerminalReprobeResult(TerminalReprobeStatus.Executed, CombatAiActionKind.Spell);
            }
        }

        var currentResult = TryUseAnyCurrentHostileAction(
                character,
                turnPlan.ActionProbe,
                battleService);

        if (currentResult.Executed)
        {
            return currentResult;
        }

        if (backupPolicyHeld || currentResult.PolicyHeld)
        {
            return new TerminalReprobeResult(TerminalReprobeStatus.PolicyHeld);
        }

        return new TerminalReprobeResult(TerminalReprobeStatus.Blocked);
    }

    private static TerminalReprobeResult TryUseAnyCurrentHostileAction(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return new TerminalReprobeResult(TerminalReprobeStatus.Blocked);
        }

        var actionKinds = GetTerminalReprobeActionKinds(actionProbe);
        var policyHeld = false;

        foreach (var target in GetKnownEnemyTargets(character))
        {
            if (target?.RulesetCharacter == null || target.Side == character.Side)
            {
                continue;
            }

            foreach (var actionKind in actionKinds)
            {
                var targetProbe = new CombatAiActionProbe(
                    actionProbe.PreferredAction,
                    actionProbe.BackupAction,
                    target,
                    actionKind == actionProbe.PreferredAction,
                    actionKind == actionProbe.BackupAction,
                    actionProbe.HasAtWillHostileSpell,
                    actionProbe.CapabilityCatalog);

                if (actionKind == CombatAiActionKind.Ranged &&
                    ShouldSuppressRangedBackupForMeleeRoute(
                        character,
                        targetProbe,
                        battleService))
                {
                    policyHeld = true;
                    continue;
                }

                var actionResult = TryUseResidualSafeHostileAction(
                        character,
                        target,
                        actionKind,
                        battleService);

                if (actionResult.Executed)
                {
                    return new TerminalReprobeResult(TerminalReprobeStatus.Executed, actionKind);
                }
            }
        }

        return new TerminalReprobeResult(
            policyHeld ? TerminalReprobeStatus.PolicyHeld : TerminalReprobeStatus.Blocked);
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
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        var actionKinds = GetTerminalReprobeActionKinds(actionProbe);

        foreach (var target in GetKnownEnemyTargets(character))
        {
            if (target?.RulesetCharacter == null || target.Side == character.Side)
            {
                continue;
            }

            foreach (var actionKind in actionKinds)
            {
                var validation = ValidateResidualMainAction(character, target, actionKind, battleService);

                if (!validation.IsValid)
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
        CombatAiProfile profile,
        CombatAiSelfAssessment self)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return new CurrentTerminalActionScan(
                false,
                false,
                false);
        }

        if (HasCurrentValidatedHostileAction(
                character,
                actionProbe,
                battleService))
        {
            return new CurrentTerminalActionScan(
                true,
                false,
                false);
        }

        var hasHostileCandidate = HasCurrentHostileActionCandidate(
            character,
            actionProbe,
            battleService);
        var hasUsefulUtility = HasCurrentUsefulUtility(character, profile, self);

        if (hasHostileCandidate)
        {
            return new CurrentTerminalActionScan(
                false,
                true,
                false);
        }

        if (hasUsefulUtility)
        {
            return new CurrentTerminalActionScan(
                false,
                false,
                true);
        }

        return new CurrentTerminalActionScan(
            false,
            false,
            false);
    }

    private static bool HasCurrentHostileActionCandidate(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available)
        {
            return false;
        }

        foreach (var target in GetKnownEnemyTargets(character))
        {
            if (target?.RulesetCharacter == null || target.Side == character.Side)
            {
                continue;
            }

            if (GetCurrentRangedMainActionAvailability(character, target, battleService) !=
                CurrentTerminalActionAvailability.None)
            {
                return true;
            }

            if (GetCurrentCantripMainActionAvailability(character, target, battleService) !=
                CurrentTerminalActionAvailability.None)
            {
                return true;
            }

            if (GetCurrentHostilePowerAvailability(character, target) != CurrentTerminalActionAvailability.None)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCurrentUsefulUtility(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiSelfAssessment self)
    {
        _ = profile;

        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            !(self.IsBloodied || self.IsCritical || self.HasSeriousCondition))
        {
            return false;
        }

        foreach (var usablePower in rulesetCharacter.UsablePowers)
        {
            if (!IsEligibleFallbackSelfBuff(usablePower, rulesetCharacter, self))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static CurrentTerminalActionAvailability GetCurrentRangedMainActionAvailability(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            character.GetActionStatus(Id.AttackMain, ActionScope.Battle) != ActionStatus.Available)
        {
            return CurrentTerminalActionAvailability.None;
        }

        var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);
        var inRangeCount = 0;

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (mode == null || !IsRangedAttackMode(mode))
            {
                continue;
            }

            if (distance <= mode.MaxRange + 0.5f)
            {
                inRangeCount++;
            }
        }

        var validation = ValidateResidualMainAction(
            character,
            target,
            CombatAiActionKind.Ranged,
            battleService);

        if (inRangeCount <= 0)
        {
            return CurrentTerminalActionAvailability.None;
        }

        if (!validation.IsValid)
        {
            return CurrentTerminalActionAvailability.Candidate;
        }

        return CurrentTerminalActionAvailability.Validated;
    }

    private static CurrentTerminalActionAvailability GetCurrentCantripMainActionAvailability(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available)
        {
            return CurrentTerminalActionAvailability.None;
        }

        var spellCapability = BuildAtWillHostileSpellSummary(character.RulesetCharacter);

        if (spellCapability.Count <= 0 || spellCapability.MaximumRange <= 0f)
        {
            return CurrentTerminalActionAvailability.None;
        }

        var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);
        var canAct = TryGetAtWillSpellAttackFromPosition(
            character,
            character.LocationPosition,
            target,
            target.LocationPosition,
            battleService,
            out _,
            out _,
            out _);

        if (distance > spellCapability.MaximumRange + 0.5f)
        {
            return CurrentTerminalActionAvailability.None;
        }

        if (!canAct)
        {
            return CurrentTerminalActionAvailability.Candidate;
        }

        return CurrentTerminalActionAvailability.Validated;
    }

    private static CurrentTerminalActionAvailability GetCurrentHostilePowerAvailability(
        GameLocationCharacter character,
        GameLocationCharacter target)
    {
        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available)
        {
            return CurrentTerminalActionAvailability.None;
        }

        var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);

        foreach (var usablePower in character.RulesetCharacter.UsablePowers)
        {
            var power = usablePower?.PowerDefinition;
            var effectDescription = power?.EffectDescription;

            if (power == null ||
                effectDescription == null ||
                power.ActivationTime != ActivationTime.Action ||
                effectDescription.TargetSide != Side.Enemy ||
                !character.RulesetCharacter.CanUsePower(power, true, true))
            {
                continue;
            }

            var range = effectDescription.RangeParameter;

            if (range > 0f && distance > range + 0.5f)
            {
                continue;
            }

            return CurrentTerminalActionAvailability.Candidate;
        }

        return CurrentTerminalActionAvailability.None;
    }

    private static bool TryUseTerminalRangedBackupAfterFailedMeleePursuit(
        GameLocationCharacter character,
        CombatAiActionProbe actionProbe,
        IGameLocationBattleService battleService)
    {
        if (!ShouldReleaseRangedBackupAfterFailedMeleePursuit(character, actionProbe, battleService))
        {
            return false;
        }

        if (!TryUseResidualSafeHostileAction(
                character,
                actionProbe.Target,
                CombatAiActionKind.Ranged,
                battleService).Executed)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldReleaseRangedBackupAfterFailedMeleePursuit(
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

        if (!TryGetSameTurnClosedMeleePursuitRoute(character, out _))
        {
            return false;
        }

        if (character.RemainingTacticalMoves > 0 &&
            HasImprovingMeleePursuit(character))
        {
            return false;
        }

        var validation = ValidateResidualMainAction(
            character,
            actionProbe.Target,
            CombatAiActionKind.Ranged,
            battleService);

        if (!validation.IsValid)
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
               closedRoute.HasNoConnectedRoute;
    }

    private static bool TryGetTerminalFallbackBlock(
        CurrentTerminalActionScan currentActionScan,
        bool hasTerminalPolicyHeld,
        out TerminalFallbackBlockKind blockKind)
    {
        blockKind = TerminalFallbackBlockKind.None;

        if (currentActionScan.BlocksReadyOrDodge)
        {
            blockKind = TerminalFallbackBlockKind.HostileOrUtility;
            return true;
        }

        if (hasTerminalPolicyHeld)
        {
            blockKind = TerminalFallbackBlockKind.PolicyHeld;
            return true;
        }

        return false;
    }

    private static bool HasTerminalPolicyHeld(
        bool hostileActionPolicyHeld,
        bool terminalReprobePolicyHeld)
    {
        return hostileActionPolicyHeld || terminalReprobePolicyHeld;
    }

    private static void ClearTerminalReprobeActionCaches()
    {
        MeleeAttackPositionCache.Clear();
        SpellAttackPositionCache.Clear();
        ActionKindPositionCache.Clear();
    }

    private static bool TryUseFallbackReady(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CurrentTerminalActionScan currentActionScan,
        bool endTurnTerminal = false)
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
                currentActionScan,
                out var readyActionType))
        {
            return false;
        }

        if (endTurnTerminal)
        {
            return SchedulePendingAiProcessTerminalLaunch(
                character,
                Id.Ready,
                readyActionType,
                PendingTerminalLaunchKind.Ready);
        }

        character.MyExecuteActionReady(readyActionType);
        ClearTurnCache(character);

        return true;
    }

    internal static void NormalizeFallbackReadyAfterAction(CharacterAction action)
    {
        var character = action?.ActingCharacter;

        if (action?.ActionId != Id.Ready ||
            character?.RulesetCharacter == null ||
            !PendingTerminalReadyEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalReady))
        {
            return;
        }

        PendingTerminalReadyEndTurnCache.Remove(character.Guid);
        PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
        TryCompleteTerminalReadyEndTurn(character, pendingTerminalReady, TerminalReadyCompletionKind.Applied);
    }

    internal static bool IsPendingTerminalReadyOrDodgeAction(CharacterAction action)
    {
        var character = action?.ActingCharacter;

        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        return action.ActionId == Id.Ready && TryGetPendingTerminalReadyEndTurn(character, out _) ||
               action.ActionId == Id.Dodge && TryGetPendingTerminalDodgeEndTurn(character, out _);
    }

    private static void TryCompleteTerminalReadyEndTurn(
        GameLocationCharacter character,
        PendingTerminalDodgeEndTurnMemory pendingTerminalReady,
        TerminalReadyCompletionKind result)
    {
        if (character?.RulesetCharacter == null)
        {
            return;
        }

        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
    }

    private static bool TryGetFallbackReadyOpportunity(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CurrentTerminalActionScan currentActionScan,
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

        if (turnPlan.ActionProbe.CanUsePreferredAction)
        {
            return false;
        }

        if (turnPlan.ActionProbe.CanUseBackupAction)
        {
            return false;
        }

        var movementAvailability = GetMovementAvailability(character, turnPlan, forResidualAction: true);

        if (movementAvailability.IsAvailable && IsSearchKnownTargetPlan(turnPlan))
        {
            return false;
        }

        if (movementAvailability.IsAvailable)
        {
            return false;
        }

        if (HasUnattemptedPreMainRouteMove(character, profile, turnPlan))
        {
            return false;
        }

        if (HasDeferredGroundMeleeCombatParticipationRoute(character))
        {
            return false;
        }

        if (HasClosedNoMoveGroundMeleeRoute(character))
        {
            return false;
        }

        if (HasSameTurnPartialGroundMeleeRoute(character, out var partialRoute))
        {
            return false;
        }

        if (TryGetSameTurnNoMoveProxyThreatAttempt(character, out var proxyThreatAttempt))
        {
            return false;
        }

        var hasMissedMovementRoute = TryGetMissedMovementState(
            character,
            turnPlan,
            out _,
            out _);

        if (hasMissedMovementRoute)
        {
            return false;
        }

        if (IsRepeatedTerminalAction(character, turnPlan, CombatAiExecutedActionKind.Ready))
        {
            return false;
        }

        var target = turnPlan.ActionProbe.Target ?? SelectPrimaryTarget(character);

        if (target?.RulesetCharacter == null)
        {
            return false;
        }

        if (currentActionScan.BlocksReadyOrDodge)
        {
            return false;
        }

        var distance = ComputeGridDistance(character.LocationPosition, target.LocationPosition);
        var range = GetFallbackReadyRange(character, profile, readyActionType);
        var targetMove = Math.Max(ReadyOpportunityDefaultTargetMove, target.MaxTacticalMoves);

        if (range <= 0f || distance > range)
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
        CombatAiProfile profile,
        CombatAiSelfAssessment self,
        CombatAiTurnPlan turnPlan)
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
            if (!IsEligibleFallbackSelfBuff(usablePower, rulesetCharacter, self))
            {
                continue;
            }

            character.MyExecuteActionPowerNoCost(usablePower, character);
            ClearTurnCache(character);

            return true;
        }

        return false;
    }

    private static bool IsEligibleFallbackSelfBuff(
        RulesetUsablePower usablePower,
        RulesetCharacter actor,
        CombatAiSelfAssessment self)
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
            !HasDefensiveSelfBuffForm(effectDescription))
        {
            return false;
        }

        if (self.IsConcentrating && CouldBreakConcentration(power))
        {
            return false;
        }

        return CanSpendActionForPower(power);
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

    private static bool HasDefensiveSelfBuffForm(EffectDescription effectDescription)
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

    private static bool HasEquivalentSenseUtility(
        RulesetCharacter actor,
        EffectDescription effectDescription)
    {
        if (actor == null || effectDescription == null)
        {
            return false;
        }

        foreach (var effectForm in effectDescription.EffectForms)
        {
            if (effectForm.FormType != EffectForm.EffectFormType.Condition ||
                effectForm.ConditionForm is not { Operation: ConditionForm.ConditionOperation.Add } conditionForm ||
                conditionForm.ConditionDefinition == null)
            {
                continue;
            }

            foreach (var feature in conditionForm.ConditionDefinition.Features)
            {
                if (feature is not FeatureDefinitionSense sense ||
                    !HasEquivalentSenseMode(actor, sense.senseType))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool HasEquivalentSenseMode(RulesetCharacter actor, SenseMode.Type grantedSense)
    {
        if (grantedSense == SenseMode.Type.None)
        {
            return false;
        }

        foreach (var senseMode in actor.SenseModes)
        {
            if (IsEquivalentOrBetterSenseMode(senseMode.SenseType, grantedSense))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEquivalentOrBetterSenseMode(SenseMode.Type existingSense, SenseMode.Type grantedSense)
    {
        if (existingSense == grantedSense)
        {
            return true;
        }

        return grantedSense switch
        {
            SenseMode.Type.Darkvision => existingSense is SenseMode.Type.SuperiorDarkvision or SenseMode.Type.Truesight,
            SenseMode.Type.SuperiorDarkvision => existingSense == SenseMode.Type.Truesight,
            SenseMode.Type.DetectInvisibility => existingSense == SenseMode.Type.Truesight,
            _ => false
        };
    }

    private static bool CouldBreakConcentration(FeatureDefinitionPower power)
    {
        return power.ActivationTime != ActivationTime.NoCost;
    }

    private static bool CanSpendActionForPower(FeatureDefinitionPower power)
    {
        return power.ActivationTime == ActivationTime.NoCost;
    }

    private static bool ShouldUseFallbackDodge(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        CombatAiTurnPlan turnPlan)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        if (GetMovementAvailability(character, turnPlan, forResidualAction: true).IsAvailable)
        {
            return false;
        }

        if (HasClosedNoMoveGroundMeleeRoute(character))
        {
            return false;
        }

        if (HasDeferredGroundMeleeCombatParticipationRoute(character))
        {
            return false;
        }

        var profile = BuildProfile(character);

        if (HasUnattemptedPreMainRouteMove(character, profile, turnPlan))
        {
            return false;
        }

        return !turnPlan.ActionProbe.CanUsePreferredAction &&
               !turnPlan.ActionProbe.CanUseBackupAction;
    }

    private static bool HasClosedNoMoveGroundMeleeRoute(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !RouteMoveCompletionClosedCache.TryGetValue(character.Guid, out var memory))
        {
            return false;
        }

        return memory.MovementGoal == CombatAiMovementGoalKind.AdvanceToMelee &&
               memory.IsGroundMeleeNoMove &&
               memory.Round == GetCurrentBattleRound() &&
               memory.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool HasSameTurnPartialGroundMeleeRoute(
        GameLocationCharacter character,
        out RouteMoveCompletionClosedMemory memory)
    {
        memory = default;

        if (character?.RulesetCharacter == null ||
            !RouteMoveCompletionClosedCache.TryGetValue(character.Guid, out memory))
        {
            return false;
        }

        return memory.MovementGoal == CombatAiMovementGoalKind.AdvanceToMelee &&
               memory.IsGroundMeleePartial &&
               memory.Round == GetCurrentBattleRound() &&
               memory.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp);
    }

    private static bool HasDeferredGroundMeleeCombatParticipationRoute(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !PreMainRouteMoveAttemptCache.TryGetValue(character.Guid, out var attempt))
        {
            return false;
        }

        return attempt.Goal == CombatAiMovementGoalKind.AdvanceToMelee &&
               attempt.Status != CombatAiPreMainRouteMoveStatus.Executed &&
               attempt.Round == GetCurrentBattleRound() &&
               attempt.TurnStamp == Math.Max(1, ObservedCombatMemoryTurnStamp) &&
               attempt.IsVanillaOwned;
    }

    private static bool HasUnattemptedPreMainRouteMove(
        GameLocationCharacter character,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan)
    {
        return character != null &&
               ShouldUsePreMainRouteMove(character, profile, turnPlan) &&
               !PreMainRouteMoveAttemptCache.ContainsKey(character.Guid);
    }

    private static bool HasPreMainRouteMoveAttempt(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan)
    {
        return character != null &&
               turnPlan.MovementPlan.HasGoal &&
               PreMainRouteMoveAttemptCache.ContainsKey(character.Guid);
    }

    private static bool TryGetMissedMovementState(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        out MissedMovementKind missedMovementKind,
        out CombatAiMovementAvailabilityResult availability)
    {
        missedMovementKind = MissedMovementKind.None;
        availability = default;

        if (character == null ||
            !turnPlan.MovementPlan.HasGoal ||
            turnPlan.ActionProbe.CanUsePreferredAction ||
            turnPlan.ActionProbe.CanUseBackupAction)
        {
            return false;
        }

        availability = GetMovementAvailability(character, turnPlan, forResidualAction: true);

        if (availability.IsAvailable)
        {
            return false;
        }

        if (!TurnMovementProgressCache.TryGetValue(character.Guid, out var movementProgress))
        {
            missedMovementKind = MissedMovementKind.UnresolvedGoal;

            return availability.Availability is CombatAiMovementAvailability.Spent;
        }

        if (!movementProgress.HadMeaningfulMovementCandidate)
        {
            missedMovementKind = MissedMovementKind.UnresolvedGoal;

            return availability.Availability is CombatAiMovementAvailability.Spent;
        }

        missedMovementKind = MissedMovementKind.MissedMeaningfulMove;

        return true;
    }

    private static CombatAiMovementAvailabilityResult GetMovementAvailability(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        bool forResidualAction = false)
    {
        if (character?.RulesetCharacter == null || !turnPlan.MovementPlan.HasGoal)
        {
            return new CombatAiMovementAvailabilityResult(CombatAiMovementAvailability.None);
        }

        var canUseResidualRouteMove = CanUseResidualRouteMovement(turnPlan);
        var freeJumpAvailability = GetFallbackFreeJumpAvailability(character);

        if (HasDisconnectedPostMovePositioning(
                character,
                turnPlan))
        {
            return new CombatAiMovementAvailabilityResult(
                CombatAiMovementAvailability.Blocked,
                freeJumpAvailability);
        }

        if (character.RemainingTacticalMoves > 0 &&
            character.CanDecideToMoveByItself &&
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) == ActionStatus.Available)
        {
            if (forResidualAction)
            {
                if (canUseResidualRouteMove)
                {
                    return new CombatAiMovementAvailabilityResult(
                        CombatAiMovementAvailability.Available,
                        freeJumpAvailability);
                }

                return new CombatAiMovementAvailabilityResult(
                    CombatAiMovementAvailability.Blocked,
                    freeJumpAvailability);
            }

            return new CombatAiMovementAvailabilityResult(
                CombatAiMovementAvailability.Available,
                freeJumpAvailability);
        }

        if (freeJumpAvailability == FreeJumpFallbackAvailability.Available)
        {
            GetOrCreateTurnMovementProgress(character, turnPlan)?.MarkFreeJumpMovementCandidate();
        }

        if (character.RemainingTacticalMoves <= 0 ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) == ActionStatus.Spent)
        {
            return new CombatAiMovementAvailabilityResult(
                CombatAiMovementAvailability.Spent,
                freeJumpAvailability);
        }

        return new CombatAiMovementAvailabilityResult(
            CombatAiMovementAvailability.Blocked,
            freeJumpAvailability);
    }

    private static FreeJumpFallbackAvailability GetFallbackFreeJumpAvailability(GameLocationCharacter character)
    {
        if (FreeJumpContext.HasUsefulAiFreeJumpDestination(character))
        {
            return FreeJumpFallbackAvailability.Available;
        }

        return Main.Settings.EnableBonusActionFreeJump
            ? FreeJumpFallbackAvailability.Unavailable
            : FreeJumpFallbackAvailability.Disabled;
    }

    private static bool CanUseResidualRouteMovement(CombatAiTurnPlan turnPlan)
    {
        _ = turnPlan;

        // EndTurn residual is not a safe phase to begin non action-linked movement.
        // Vanilla movement scoring or explicit action-linked movement must own those moves.
        return false;
    }

    private static TerminalFallbackActionResult TryApplyFallbackDodge(
        GameLocationCharacter character,
        bool endTurnTerminal = false)
    {
        if (character?.RulesetCharacter == null)
        {
            return new TerminalFallbackActionResult(TerminalFallbackActionStatus.Blocked);
        }

        if (!IsActiveBattleContender(character))
        {
            return new TerminalFallbackActionResult(TerminalFallbackActionStatus.Blocked);
        }

        var actionEconomy = BuildActionEconomySnapshot(character);
        var dodgeStatus = character.GetActionStatus(Id.Dodge, ActionScope.Battle);

        if (!CanSpendTerminalMainAction(character, actionEconomy))
        {
            return new TerminalFallbackActionResult(TerminalFallbackActionStatus.Blocked);
        }

        if (!actionEconomy.DodgeAvailable || dodgeStatus != ActionStatus.Available)
        {
            return new TerminalFallbackActionResult(TerminalFallbackActionStatus.Blocked);
        }

        if (endTurnTerminal)
        {
            return SchedulePendingAiProcessTerminalLaunch(
                character,
                Id.Dodge,
                default,
                PendingTerminalLaunchKind.Dodge)
                ? new TerminalFallbackActionResult(TerminalFallbackActionStatus.Scheduled)
                : new TerminalFallbackActionResult(TerminalFallbackActionStatus.Blocked);
        }

        var rulesetCharacter = character.RulesetCharacter;
        var existingConditions = CollectDodgingConditionGuids(rulesetCharacter);

        PendingFallbackDodgeConditionCache[character.Guid] = existingConditions;
        character.MyExecuteActionDodge();

        return new TerminalFallbackActionResult(TerminalFallbackActionStatus.Executed);
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
        var isTerminalEndTurnDodge =
            PendingTerminalDodgeEndTurnCache.TryGetValue(character.Guid, out var pendingTerminalDodge);

        if (!hasPendingCondition && !isTerminalEndTurnDodge)
        {
            return;
        }

        if (hasPendingCondition)
        {
            PendingFallbackDodgeConditionCache.Remove(character.Guid);
        }

        if (isTerminalEndTurnDodge)
        {
            PendingTerminalDodgeEndTurnCache.Remove(character.Guid);
            PendingTerminalActionEndTurnSuppressCache.Remove(character.Guid);
        }

        if (!hasPendingCondition)
        {
            TryCompleteTerminalDodgeEndTurn(
                character,
                isTerminalEndTurnDodge,
                pendingTerminalDodge,
                TerminalDodgeCompletionKind.NoPendingCondition);
            return;
        }

        var condition = FindNewDodgingCondition(rulesetCharacter, existingConditions);

        if (condition == null)
        {
            TryCompleteTerminalDodgeEndTurn(
                character,
                isTerminalEndTurnDodge,
                pendingTerminalDodge,
                TerminalDodgeCompletionKind.NoCondition);
            return;
        }

        NormalizeFallbackDodgeCondition(character, condition);
        TryCompleteTerminalDodgeEndTurn(
            character,
            isTerminalEndTurnDodge,
            pendingTerminalDodge,
            TerminalDodgeCompletionKind.Applied);
    }

    private static void TryCompleteTerminalDodgeEndTurn(
        GameLocationCharacter character,
        bool isTerminalEndTurnDodge,
        PendingTerminalDodgeEndTurnMemory pendingTerminalDodge,
        TerminalDodgeCompletionKind result)
    {
        if (!isTerminalEndTurnDodge || character?.RulesetCharacter == null)
        {
            return;
        }

        PendingAiProcessTerminalLaunchCache.Remove(character.Guid);
        PendingAiProcessTerminalLaunchAcceptedCache.Remove(character.Guid);
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

    private static bool IsRepeatedTerminalAction(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        CombatAiExecutedActionKind action)
    {
        if (actor == null ||
            action == CombatAiExecutedActionKind.None ||
            !RepeatTerminalActionCache.TryGetValue(actor.Guid, out var memory))
        {
            return false;
        }

        var target = turnPlan.ActionProbe.Target ?? turnPlan.MovementPlan.Target;
        var targetGuid = target?.Guid ?? 0;
        var targetPosition = target?.LocationPosition ?? turnPlan.MovementPlan.TargetPosition;

        return memory.Action == action &&
               memory.TargetGuid == targetGuid &&
               memory.ActorPosition == actor.LocationPosition &&
               memory.TargetPosition == targetPosition;
    }

    private static void RecordTerminalAction(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        CombatAiExecutedActionKind action)
    {
        if (actor == null || action == CombatAiExecutedActionKind.None)
        {
            return;
        }

        var target = turnPlan.ActionProbe.Target ?? turnPlan.MovementPlan.Target;
        var targetGuid = target?.Guid ?? 0;
        var targetPosition = target?.LocationPosition ?? turnPlan.MovementPlan.TargetPosition;
        var repeat = 1;

        if (RepeatTerminalActionCache.TryGetValue(actor.Guid, out var memory) &&
            memory.Action == action &&
            memory.TargetGuid == targetGuid &&
            memory.ActorPosition == actor.LocationPosition &&
            memory.TargetPosition == targetPosition)
        {
            repeat = memory.RepeatCount + 1;
        }

        RepeatTerminalActionCache[actor.Guid] = new RepeatedEndStateMemory(
            targetGuid,
            action,
            actor.LocationPosition,
            targetPosition,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp),
            repeat);
    }

    private static CombatAiActionPathStatus GetActionRouteStatus(
        GameLocationCharacter actor,
        CombatAiTurnPlan turnPlan,
        CombatAiMovementAvailabilityResult movementAvailability)
    {
        if (turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat)
        {
            var defensiveAvailability = movementAvailability.Availability switch
            {
                CombatAiMovementAvailability.Available => CombatAiActionPathAvailability.Available,
                CombatAiMovementAvailability.Blocked => CombatAiActionPathAvailability.Blocked,
                CombatAiMovementAvailability.Spent => CombatAiActionPathAvailability.Spent,
                _ => CombatAiActionPathAvailability.None
            };

            return new CombatAiActionPathStatus(
                CombatAiActionPathKind.BreakThreat,
                defensiveAvailability);
        }

        if (turnPlan.ActionProbe.CanUsePreferredAction ||
            (turnPlan.ActionProbe.CanUseBackupAction && !turnPlan.MovementPlan.HasGoal))
        {
            return new CombatAiActionPathStatus(
                CombatAiActionPathKind.AttackNow,
                CombatAiActionPathAvailability.Available);
        }

        if (!turnPlan.MovementPlan.HasGoal)
        {
            return new CombatAiActionPathStatus(
                CombatAiActionPathKind.NoAction,
                CombatAiActionPathAvailability.None);
        }

        var turnsToPreferredAction = EstimateTurnsToPreferredAction(
            actor,
            turnPlan,
            actor?.LocationPosition ?? turnPlan.MovementPlan.TargetPosition);
        var hasAttackCapablePositioningDestination =
            HasAttackCapablePositioningDestination(actor, turnPlan);
        var kind =
            turnPlan.ActionProbe.PreferredAction != CombatAiActionKind.None &&
            (hasAttackCapablePositioningDestination ||
             (!RequiresPostMoveActionConnectedPositioning(turnPlan) &&
              turnsToPreferredAction >= 0 &&
              turnsToPreferredAction <= 1))
                ? CombatAiActionPathKind.AttackAfterMove
                : CombatAiActionPathKind.PursueForNextRound;

        var availability = movementAvailability.Availability switch
        {
            CombatAiMovementAvailability.Available => CombatAiActionPathAvailability.Available,
            CombatAiMovementAvailability.Blocked => CombatAiActionPathAvailability.Blocked,
            CombatAiMovementAvailability.Spent => CombatAiActionPathAvailability.Spent,
            _ => CombatAiActionPathAvailability.None
        };

        return new CombatAiActionPathStatus(kind, availability, turnsToPreferredAction);
    }

    private static string FormatPosition(int3 position)
    {
        return $"{position.x},{position.y},{position.z}";
    }

    private static string FormatTurnsToAction(int turnsToAction)
    {
        return turnsToAction >= 0 ? turnsToAction.ToString() : "unknown";
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

        var attackModes = character.RulesetCharacter.AttackModes
            .Where(mode => mode != null)
            .ToArray();
        var hasMelee = attackModes.Any(IsMeleeAttackModeForAi);
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
        return BuildCapabilityCatalog(character).HasAnyRanged;
    }

    private static bool HasTrueRangedAttackModes(GameLocationCharacter character)
    {
        return BuildCapabilityCatalog(character).HasTrueRanged;
    }

    private static bool HasThrownAttackModes(GameLocationCharacter character)
    {
        return BuildCapabilityCatalog(character).HasThrownRanged;
    }

    private static bool HasMeleeAttackModes(GameLocationCharacter character)
    {
        return BuildCapabilityCatalog(character).HasMelee;
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
        int3 actualDestination,
        string phase)
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
        GameLocationCharacter actor,
        int3 startPosition,
        int3 targetPosition)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        var remainingTacticalMoves = actor.RemainingTacticalMoves;

        if (remainingTacticalMoves <= 0)
        {
            return false;
        }

        actor.UsedTacticalMoves += remainingTacticalMoves;
        actor.UsedTacticalMovesChanged?.Invoke(actor);

        _ = startPosition;
        _ = targetPosition;

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
        IGameLocationBattleService battleService,
        out TrafficBlockKind blockKind)
    {
        blockKind = TrafficBlockKind.None;

        if (actor?.RulesetCharacter == null ||
            battleService == null ||
            Gui.Battle == null ||
            destination == actor.LocationPosition)
        {
            return false;
        }

        var profile = BuildProfile(actor);

        var turnPlan = BuildCombatAiTurnPlan(actor, profile, battleService);

        return ShouldRejectTrafficBlockingMove(actor, destination, battleService, profile, turnPlan, out blockKind);
    }

    private static bool ShouldRejectTrafficBlockingMove(
        GameLocationCharacter actor,
        int3 destination,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        CombatAiTurnPlan turnPlan,
        out TrafficBlockKind blockKind)
    {
        blockKind = TrafficBlockKind.None;

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
            blockKind = TrafficBlockKind.AllyFireLane;
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

            blockKind = TrafficBlockKind.LargeAllyCorridor;
            return true;
        }

        if (!ShouldRejectAllyFireLaneGate(actor, destination, target, battleService))
        {
            return false;
        }

        blockKind = TrafficBlockKind.AllyFireLane;
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

    private static bool TryUseActionLinkedFreeJumpToResidualAction(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiActionLinkedMoveContinuation continuation =
            CombatAiActionLinkedMoveContinuation.ImmediateResidualAction,
        bool requireActionAfterMove = true,
        bool requireJumpImprovement = false,
        CombatAiRouteMoveSourceKind routeMoveSource = CombatAiRouteMoveSourceKind.FreeJump,
        bool requireImmediateAttack = false)
    {
        if (character?.RulesetCharacter == null ||
            battleService == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        if (!requireImmediateAttack &&
            ShouldBlockGenericFreeJumpForPositioning(turnPlan, routeMoveSource))
        {
            return false;
        }

        var bestDestination = default(int3);
        var bestAction = CombatAiActionKind.None;
        var groundMeleeJumpRouteAvailable = false;
        var foundCandidate = requireImmediateAttack
            ? TryFindJumpImmediateAttackCandidate(
                character,
                turnPlan,
                battleService,
                out bestDestination,
                out bestAction,
                out _)
            : TryFindActionLinkedFreeJumpCandidate(
                character,
                turnPlan,
                battleService,
                requireActionAfterMove,
                out bestDestination,
                out bestAction,
                out _,
                out groundMeleeJumpRouteAvailable,
                requireJumpImprovement);

        if (requireImmediateAttack)
        {
            if (foundCandidate)
            {
                JumpImmediateAttackReachableCache.Add(character.Guid);
            }
            else
            {
                JumpImmediateAttackReachableCache.Remove(character.Guid);
            }
        }
        else if (foundCandidate && groundMeleeJumpRouteAvailable)
        {
            GroundMeleeJumpRouteAvailableCache.Add(character.Guid);
        }

        if (!foundCandidate)
        {
            return false;
        }

        var lockRemainingMovement = ShouldLockRemainingMovementAfterRouteMove(turnPlan, continuation);

        ActionLinkedMoveCache[character.Guid] = new ActionLinkedMoveMemory(
            turnPlan.ActionProbe.Target,
            bestAction,
            continuation,
            turnPlan.MovementPlan.Goal,
            character.LocationPosition,
            bestDestination,
            routeMoveSource,
            lockRemainingMovement,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

        if (lockRemainingMovement)
        {
            RecordPendingRouteMovementLock(character, turnPlan.MovementPlan.Goal, continuation, bestDestination);
        }

        if (!TryExecuteAiFreeJumpTacticalMove(
                character,
                bestDestination,
                routeMoveSource))
        {
            ActionLinkedMoveCache.Remove(character.Guid);
            PendingRouteMovementLockCache.Remove(character.Guid);
            return false;
        }

        MarkDashBlockedAfterRouteMove(character, turnPlan.MovementPlan.Goal, bestDestination);

        return true;
    }

    private static bool TryFindJumpImmediateAttackCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        out int3 bestDestination,
        out CombatAiActionKind bestAction,
        out float bestScore)
    {
        bestDestination = default;
        bestAction = CombatAiActionKind.None;
        bestScore = float.MinValue;

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
        var selectedAction = CombatAiActionKind.None;
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

            if (!IsLegalAiRouteDestination(character, destination, out _))
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

            var candidateAction = CombatAiActionKind.Melee;
            var actionScore =
                score +
                (preferredAvailable ? 1.50f : 1.00f) -
                (FreeJumpContext.ComputeAiFreeJumpMovementCost(start, destination) * 0.01f) +
                ComputeStableTieBreakScore(character, turnPlan, destination, candidateAction);

            if (found && actionScore <= selectedScore + 0.000001f)
            {
                return true;
            }

            found = true;
            selectedScore = actionScore;
            selectedDestination = destination;
            selectedAction = candidateAction;
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
        bestAction = selectedAction;
        bestScore = selectedScore;
        JumpImmediateAttackReachableCache.Add(character.Guid);
        return true;
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

    private static bool TryFindActionLinkedFreeJumpCandidate(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        bool requireActionAfterMove,
        out int3 bestDestination,
        out CombatAiActionKind bestAction,
        out float bestScore,
        out bool selectedGroundMeleeRouteImprovement,
        bool requireJumpImprovement = false,
        bool requireImmediateAttack = false)
    {
        bestDestination = default;
        bestAction = CombatAiActionKind.None;
        bestScore = float.MinValue;
        selectedGroundMeleeRouteImprovement = false;

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            turnPlan.ActionProbe.Target?.RulesetCharacter == null ||
            character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available ||
            character.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        if (!CanUseFreeJumpForAi(character))
        {
            return false;
        }

        var found = false;
        var selectedDestination = default(int3);
        var selectedAction = CombatAiActionKind.None;
        var selectedScore = float.MinValue;
        var movementProgress = GetOrCreateTurnMovementProgress(character, turnPlan);
        var hasNormalRouteBaseline = movementProgress?.HasAcceptedMoveCandidate == true;
        var normalActionTurns = movementProgress?.BestPreferredActionMoveTurnsToAction ?? -1;
        var normalMoveTurns = movementProgress?.BestMoveCandidateTurnsToAction ?? -1;
        var normalTurnsToAction = normalActionTurns >= 0 ? normalActionTurns : normalMoveTurns;
        var profile = BuildProfile(character);
        var target = turnPlan.ActionProbe.Target;
        var preferredAction = turnPlan.ActionProbe.PreferredAction;
        var backupAction = turnPlan.ActionProbe.BackupAction;
        var start = character.LocationPosition;
        var isProxyThreatRoute = ShouldRequireReachableProxyThreatDestination(character, turnPlan);
        var isGroundMeleePursuit = IsGroundMeleePursuitPlan(turnPlan);
        var selectedGroundMeleeRouteImprovementLocal = false;

        bool HandleFreeJumpDestination(int3 destination, float score)
        {
            var preferredAvailable = CanUseActionKindAtPosition(
                character,
                destination,
                target,
                preferredAction,
                battleService);
            var backupAvailable = !preferredAvailable &&
                                  CanUseActionKindAtPosition(
                                      character,
                                      destination,
                                      target,
                                      backupAction,
                                      battleService);

            var routeScore = 0f;
            var improveFiringCandidate =
                IsImproveFiringPositionPlan(turnPlan) &&
                (preferredAvailable || backupAvailable);
            var contactBandAvailable =
                requireImmediateAttack &&
                isGroundMeleePursuit &&
                IsGroundMeleeRouteAdjacentContact(destination, turnPlan.MovementPlan.TargetPosition);
            var hasActionAfterJump = preferredAvailable || backupAvailable || contactBandAvailable;

            if (requireImmediateAttack && !hasActionAfterJump)
            {
                return true;
            }

            var groundMeleeRouteImprovement = false;

            if (isGroundMeleePursuit &&
                !TryValidateGroundMeleeJumpRouteCandidate(
                    character,
                    turnPlan,
                    start,
                    destination,
                    hasActionAfterJump,
                    out groundMeleeRouteImprovement))
            {
                return true;
            }

            if (requireJumpImprovement &&
                hasNormalRouteBaseline)
            {
                if (!hasActionAfterJump)
                {
                    return true;
                }

                if (normalTurnsToAction == 0)
                {
                    return true;
                }
            }

            if (improveFiringCandidate)
            {
                movementProgress?.RecordImproveFiringFreeJumpCandidate();
            }

            if (requireImmediateAttack && hasActionAfterJump)
            {
                var selectedImmediateAction = CombatAiActionKind.Melee;
                var immediateActionScore =
                    score +
                    1f +
                    (CanUseActionKindAtPosition(
                        character,
                        destination,
                        target,
                        CombatAiActionKind.Melee,
                        battleService)
                        ? 0.25f
                        : 0.15f) +
                    ComputeStableTieBreakScore(
                        character,
                        turnPlan,
                        destination,
                        selectedImmediateAction);

                if (found && immediateActionScore <= selectedScore + 0.000001f)
                {
                    return true;
                }

                found = true;
                selectedScore = immediateActionScore;
                selectedDestination = destination;
                selectedAction = selectedImmediateAction;
                return true;
            }

            var breakThreatAttackConnected = IsBreakThreatDestinationAttackConnected(
                character,
                turnPlan,
                battleService,
                start,
                destination,
                out _);
            var breakThreatDefensiveFallback =
                !breakThreatAttackConnected &&
                IsBreakThreatDefensiveFallbackDestination(
                    character,
                    turnPlan,
                    battleService,
                    start,
                    destination,
                    out _);

            if ((!requireActionAfterMove ||
                 turnPlan.MovementPlan.Goal == CombatAiMovementGoalKind.BreakThreat) &&
                (!TryComputeTurnPlanMovementScore(
                     character,
                     profile,
                     destination,
                     battleService,
                     turnPlan,
                     out routeScore) ||
                 routeScore <= 0f))
            {
                if (!isProxyThreatRoute || !breakThreatDefensiveFallback)
                {
                    return true;
                }

                routeScore = Math.Max(routeScore, 0.15f);
            }

            if (requireActionAfterMove &&
                !preferredAvailable &&
                !backupAvailable &&
                !breakThreatDefensiveFallback)
            {
                return true;
            }

            if (!breakThreatAttackConnected && !breakThreatDefensiveFallback)
            {
                return true;
            }

            var actionScore =
                score +
                routeScore +
                (preferredAvailable ? 0.25f : backupAvailable ? 0.05f : -0.12f) +
                ComputeStableTieBreakScore(
                    character,
                    turnPlan,
                    destination,
                    preferredAvailable ? preferredAction : backupAction);

            if (found && actionScore <= selectedScore + 0.000001f)
            {
                return true;
            }

            found = true;
            selectedScore = actionScore;
            selectedDestination = destination;
            selectedGroundMeleeRouteImprovementLocal = groundMeleeRouteImprovement;
            selectedAction = preferredAvailable
                ? preferredAction
                : backupAvailable
                    ? backupAction
                    : CombatAiActionKind.None;

            return true;
        }

        _ = requireImmediateAttack
            ? FreeJumpContext.TryEnumerateImmediateAttackAiFreeJumpDestinations(character, HandleFreeJumpDestination)
            : FreeJumpContext.TryEnumerateUsefulAiFreeJumpDestinations(character, HandleFreeJumpDestination);

        if (!found)
        {
            return false;
        }

        bestDestination = selectedDestination;
        bestAction = selectedAction;
        bestScore = selectedScore;
        selectedGroundMeleeRouteImprovement = selectedGroundMeleeRouteImprovementLocal;

        return true;
    }

    private static void TryCompleteActionLinkedMove(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null ||
            !ActionLinkedMoveCache.TryGetValue(character.Guid, out var pendingAction))
        {
            return;
        }

        if (IsConnectedFiringLineRoute(pendingAction))
        {
            if (!TryCompleteConnectedFiringLineMovementStep(character, "action-chain").IsComplete)
            {
                DeferConnectedFiringLineMoveResult(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination,
                    "action-chain");
            }

            return;
        }

        if (IsSearchKnownTargetRoute(pendingAction))
        {
            if (!TryCompleteSearchKnownTargetMovementStep(character, "action-chain").IsComplete)
            {
                DeferSearchKnownTargetMoveResult(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination,
                    "action-chain");
            }

            return;
        }

        ActionLinkedMoveCache.Remove(character.Guid);

        if (!IsActiveBattleContender(character))
        {
            ClearTurnCache(character);
            return;
        }

        if (IsGroundMeleeJumpImmediateAttackRoute(pendingAction))
        {
            if (TryDeferGroundMeleeMoveSettling(character, pendingAction, "late-completion"))
            {
                return;
            }

            return;
        }

        if (character.LocationPosition != pendingAction.ExpectedDestination)
        {
            if (IsGroundMeleePursuitTerminalRoute(pendingAction))
            {
                if (TryDeferGroundMeleeMoveSettling(character, pendingAction, "late-completion"))
                {
                    return;
                }

                if (TryFinalizeGroundMeleePursuitAtActualDestination(
                        character,
                        pendingAction,
                        pendingAction.StartPosition,
                        pendingAction.ExpectedDestination,
                        "late-completion"))
                {
                    return;
                }

                return;
            }

            if (TryFinalizeRouteMoveAtActualDestination(
                    character,
                    pendingAction,
                    pendingAction.StartPosition,
                    pendingAction.ExpectedDestination,
                    "late-completion"))
            {
                return;
            }

            CloseLateCompletionAndScheduleTerminal(
                character,
                pendingAction,
                pendingAction.StartPosition,
                pendingAction.ExpectedDestination,
                "action-chain");
            return;
        }

        if (IsGroundMeleePursuitTerminalRoute(pendingAction))
        {
            RecordGroundMeleeRouteIntent(
                character,
                pendingAction.Target,
                pendingAction.StartPosition,
                character.LocationPosition);
        }

        ApplyRouteMovementLockAfterArrival(character, pendingAction);

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision)
        {
            ClearTurnCache(character);
            return;
        }

        if (pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove)
        {
            if (TrySpendPostRouteTerminalAction(
                    character,
                    battleService,
                    allowReadyDodge: false).Executed)
            {
                ClearTurnCache(character);
                return;
            }

            ClearTurnCache(character);
            return;
        }

        if (battleService != null &&
            pendingAction.ActionKind != CombatAiActionKind.None &&
            pendingAction.Target?.RulesetCharacter != null &&
            TryUseResidualSafeHostileAction(
                character,
                pendingAction.Target,
                pendingAction.ActionKind,
                battleService).Executed)
        {
            ClearTurnCache(character);

            return;
        }

        if (TrySpendPostRouteTerminalAction(
                character,
                battleService,
                allowReadyDodge: false).Executed)
        {
            ClearTurnCache(character);
            return;
        }

        ClearTurnCache(character);
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
            actualDestination,
            out _);
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
            actualDestination,
            "arrival");
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

        ApplyRouteMovementLock(
            character,
            pendingAction.MovementGoal,
            pendingAction.Continuation,
            actualDestination,
            "arrival",
            safePositionUpdated,
            partialThreatMove && !safePositionUpdated);
        MarkRecentMeleeThreatHandledThisTurn(
            character,
            pendingAction,
            actualDestination,
            safePositionUpdated);
    }

    private static bool IsRouteMovementLockActualDestinationValid(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        out RouteActualDestinationValidationKind validationKind)
    {
        validationKind = RouteActualDestinationValidationKind.Valid;

        if (character?.RulesetCharacter == null)
        {
            validationKind = RouteActualDestinationValidationKind.MissingActor;
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (pendingAction.MovementGoal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            return IsMeleeSpacingActualDestinationValid(
                character,
                pendingAction,
                actualDestination,
                battleService,
                out validationKind);
        }

        if (!IsThreatRouteMovementLockGoal(pendingAction.MovementGoal))
        {
            return true;
        }

        if (actualDestination == pendingAction.StartPosition)
        {
            validationKind = RouteActualDestinationValidationKind.NoMovement;
            return false;
        }

        if (battleService != null && WouldBeInCurrentOrRecentMeleeThreat(character, actualDestination, battleService))
        {
            validationKind = RouteActualDestinationValidationKind.ThreatZone;
            return false;
        }

        if (!TryGetThreatAvoidance(character, out var avoidance))
        {
            validationKind = RouteActualDestinationValidationKind.NoAvoidanceMemory;
            return false;
        }

        var sourcePosition = avoidance.Source?.LocationPosition ?? avoidance.SourcePosition;
        var beforeDistance = ComputeGridDistance(avoidance.StartPosition, sourcePosition);
        var afterDistance = ComputeGridDistance(actualDestination, sourcePosition);

        if (afterDistance < beforeDistance + ThreatAvoidanceActualDistanceGain)
        {
            validationKind = RouteActualDestinationValidationKind.InsufficientThreatDistanceGain;
            return false;
        }

        return true;
    }

    private static bool IsMeleeSpacingActualDestinationValid(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 actualDestination,
        IGameLocationBattleService battleService,
        out RouteActualDestinationValidationKind validationKind)
    {
        validationKind = RouteActualDestinationValidationKind.Valid;

        var target = pendingAction.Target;

        if (target?.RulesetCharacter == null || battleService == null)
        {
            validationKind = RouteActualDestinationValidationKind.MissingTargetOrBattleService;
            return false;
        }

        if (actualDestination == pendingAction.StartPosition)
        {
            validationKind = RouteActualDestinationValidationKind.NoMovement;
            return false;
        }

        var isFlyingSpacing = BuildProfile(character).HasFlight;
        var moveCost = ComputeForcedMoveCost(pendingAction.StartPosition, actualDestination);

        if (!isFlyingSpacing && moveCost > MeleeSpacingMaximumMoveCost)
        {
            validationKind = RouteActualDestinationValidationKind.SpacingMovedTooFar;
            return false;
        }

        if (isFlyingSpacing &&
            moveCost > Math.Max(character.MaxTacticalMoves, character.RemainingTacticalMoves))
        {
            validationKind = RouteActualDestinationValidationKind.FlyingSpacingMovedTooFar;
            return false;
        }

        if (!CanUseActionKindAtPosition(
                character,
                actualDestination,
                target,
                CombatAiActionKind.Melee,
                battleService))
        {
            validationKind = RouteActualDestinationValidationKind.NoMeleePressure;
            return false;
        }

        if (WouldLeaveCurrentReactionMeleeReach(
                character,
                pendingAction.StartPosition,
                actualDestination,
                battleService))
        {
            validationKind = RouteActualDestinationValidationKind.OpportunityExposure;
            return false;
        }

        if (ShouldRejectMeleeSpacingTrafficMove(character, actualDestination, target, battleService, out _))
        {
            validationKind = RouteActualDestinationValidationKind.Traffic;
            return false;
        }

        var adjacentBefore = CountMeleeAllyCrowding(character, target, pendingAction.StartPosition);
        var adjacentAfter = CountMeleeAllyCrowding(character, target, actualDestination);
        var gridGapAfter = GetNearestMeleeAllyCrowdingHorizontalGap(character, target, actualDestination);

        if (adjacentBefore <= 0)
        {
            validationKind = RouteActualDestinationValidationKind.NoAdjacentAllyBefore;
            return false;
        }

        if (adjacentAfter > 0 || gridGapAfter < MeleeSpacingRequiredGridGap)
        {
            validationKind = RouteActualDestinationValidationKind.InsufficientHorizontalGap;
            return false;
        }

        return true;
    }

    private static bool TryFinalizeRouteMoveAtActualDestination(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 start,
        int3 expectedDestination,
        string phase)
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
                expectedDestination,
                phase);
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
            actualDestination,
            out _);
        partialThreatMove = !actualSafe && partialThreatMove;

        if (!actualSafe && !partialThreatMove)
        {
            return false;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        var safePositionUpdated = UpdateThreatAvoidanceActualDestination(
            character,
            pendingAction,
            start,
            actualDestination,
            phase);
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
        ApplyRouteMovementLock(
            character,
            pendingAction.MovementGoal,
            pendingAction.Continuation,
            actualDestination,
            phase,
            safePositionUpdated,
            partialThreatMove && !safePositionUpdated);
        MarkRecentMeleeThreatHandledThisTurn(
            character,
            pendingAction,
            actualDestination,
            safePositionUpdated);

        ClearTurnCache(character);

        return true;
    }

    private static bool TryFinalizeGroundMeleePursuitAtActualDestination(
        GameLocationCharacter character,
        ActionLinkedMoveMemory pendingAction,
        int3 startPosition,
        int3 expectedDestination,
        string phase)
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
            PendingRouteMovementLockCache.Remove(character.Guid);
            return true;
        }

        ActionLinkedMoveCache.Remove(character.Guid);
        PendingRouteMovementLockCache.Remove(character.Guid);

        if (actualDestination == startPosition)
        {
            RecordAiMoveFailure(character, startPosition, expectedDestination);
            RecordGroundMeleeRouteFailure(character, pendingAction.Target, startPosition, expectedDestination);
            RouteMoveDashBlockCache.Remove(character.Guid);
            TurnMovementProgressCache.Remove(character.Guid);
            CloseGroundMeleePursuitRouteCompletion(
                character,
                pendingAction,
                startPosition,
                expectedDestination,
                actualDestination,
                phase,
                "no-move");

            SchedulePendingRouteActionOnlyTerminal(
                character,
                pendingAction,
                expectedDestination,
                actualDestination,
                phase,
                "no-move",
                consumeAfterAbort: true);
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
            RouteMoveDashBlockCache.Remove(character.Guid);
            TurnMovementProgressCache.Remove(character.Guid);
            CloseGroundMeleePursuitRouteCompletion(
                character,
                pendingAction,
                startPosition,
                expectedDestination,
                actualDestination,
                phase,
                "route-regression");

            SchedulePendingRouteActionOnlyTerminal(
                character,
                pendingAction,
                expectedDestination,
                actualDestination,
                phase,
                "route-regression",
                consumeAfterAbort: true);
            return true;
        }

        GroundMeleeNoMoveTerminalSealCache.Remove(character.Guid);
        UpdateTurnMovementProgress(character);
        RecordGroundMeleeRouteIntent(
            character,
            pendingAction.Target,
            startPosition,
            actualDestination);
        CloseGroundMeleePursuitRouteCompletion(
            character,
            pendingAction,
            startPosition,
            expectedDestination,
            actualDestination,
            phase,
            "partial");
        RecordGroundMeleePartialRouteProgress(
            character,
            pendingAction,
            actualDestination);

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (TrySpendPostRouteTerminalAction(
                character,
                battleService,
                allowReadyDodge: false).Executed)
        {
            return true;
        }

        return true;
    }

    private static bool IsGroundMeleePursuitTerminalRoute(ActionLinkedMoveMemory pendingAction)
    {
        return pendingAction.MovementGoal == CombatAiMovementGoalKind.AdvanceToMelee &&
               pendingAction.Continuation == CombatAiActionLinkedMoveContinuation.TerminalAfterRouteMove &&
               pendingAction.RouteMoveSource is CombatAiRouteMoveSourceKind.Normal
                   or CombatAiRouteMoveSourceKind.BonusDash
                   or CombatAiRouteMoveSourceKind.GroundMeleeShortcut;
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
        string phase,
        string result)
    {
        var flags = result == "no-move"
            ? RouteMoveCompletionFlags.NoMove | RouteMoveCompletionFlags.GroundMeleeNoMove
            : result == "partial"
                ? RouteMoveCompletionFlags.GroundMeleePartial
                : RouteMoveCompletionFlags.None;

        RouteMoveCompletionClosedCache[character.Guid] = new RouteMoveCompletionClosedMemory(
            pendingAction.MovementGoal,
            startPosition,
            expectedDestination,
            flags,
            GetCurrentBattleRound(),
            Math.Max(1, ObservedCombatMemoryTurnStamp));

    }

    private static bool TryApplyPendingRouteMovementLock(GameLocationCharacter character, string phase)
    {
        if (character?.RulesetCharacter == null ||
            !PendingRouteMovementLockCache.TryGetValue(character.Guid, out var pendingLock))
        {
            return false;
        }

        var currentRound = GetCurrentBattleRound();
        var currentTurnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);

        if (currentRound != pendingLock.Round ||
            currentTurnStamp != pendingLock.TurnStamp)
        {
            PendingRouteMovementLockCache.Remove(character.Guid);
            return false;
        }

        if (pendingLock.MovementGoal == CombatAiMovementGoalKind.MeleeSpacing)
        {
            PendingRouteMovementLockCache.Remove(character.Guid);
            return false;
        }

        var actualDestination = character.LocationPosition;
        var isThreatRoute = IsThreatRouteMovementLockGoal(pendingLock.MovementGoal);
        var actualMoved = actualDestination != pendingLock.StartPosition;

        if (!actualMoved)
        {
            if (isThreatRoute)
            {
                PendingRouteMovementLockCache.Remove(character.Guid);


                return false;
            }

            PendingRouteMovementLockCache.Remove(character.Guid);
            return false;
        }

        if (!isThreatRoute &&
            ComputeGridDistance(actualDestination, pendingLock.ExpectedDestination) > 2f)
        {
            return false;
        }

        PendingRouteMovementLockCache.Remove(character.Guid);
        ProxyThreatRouteAttemptCache.Remove(character.Guid);

        var validSafePosition = !isThreatRoute;
        var canApplyMovementLock = !isThreatRoute;

        if (ThreatAvoidanceMemoryCache.TryGetValue(character.Guid, out var avoidance))
        {
            var sourcePosition = avoidance.Source?.LocationPosition ?? avoidance.SourcePosition;
            var beforeDistance = ComputeGridDistance(avoidance.StartPosition, sourcePosition);
            var afterDistance = ComputeGridDistance(actualDestination, sourcePosition);
            var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
            validSafePosition =
                afterDistance >= beforeDistance + ThreatAvoidanceActualDistanceGain &&
                (battleService == null ||
                 !WouldBeInCurrentOrRecentMeleeThreat(character, actualDestination, battleService));
            canApplyMovementLock = !isThreatRoute || actualMoved;

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
        else if (isThreatRoute)
        {
            canApplyMovementLock = actualMoved;
        }

        if (!canApplyMovementLock)
        {
            return false;
        }

        return ApplyRouteMovementLock(
            character,
            pendingLock.MovementGoal,
            CombatAiActionLinkedMoveContinuation.ReturnToVanillaDecision,
            actualDestination,
            phase,
            validSafePosition,
            isThreatRoute && !validSafePosition);
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
        int3 failedDestination,
        string phase)
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


        ThreatRouteRecoveryCache[character.Guid] = new ThreatRouteRecoveryMemory(
            pendingAction.MovementGoal,
            failedDestination,
            currentRound,
            currentTurnStamp);

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

    private static ProxyThreatRouteMoveResult TryUseReachableProxyThreatRouteMove(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        int3 startPosition,
        IEnumerable<int3> excludedDestinations,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        CombatAiActionLinkedMoveContinuation continuation,
        CombatAiMovementGoalKind movementGoal,
        bool lockRemainingMovement,
        bool requireActionAfterMove,
        out int3 destination)
    {
        destination = default;

        if (character?.RulesetCharacter == null ||
            battleService == null ||
            !IsThreatRouteMovementLockGoal(movementGoal) ||
            !TryGetProxyMeleeThreat(character, out var proxyThreat))
        {
            return new ProxyThreatRouteMoveResult(ProxyThreatRouteMoveStatus.Unavailable);
        }

        var currentPosition = character.LocationPosition;
        var sourcePosition = proxyThreat.Source?.LocationPosition ?? proxyThreat.SourcePosition;
        var currentThreatDistance = ComputeGridDistance(currentPosition, sourcePosition);
        var remainingMove = Math.Max(0, character.RemainingTacticalMoves);

        if (remainingMove <= 0 ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available ||
            !character.CanDecideToMoveByItself)
        {
            return new ProxyThreatRouteMoveResult(ProxyThreatRouteMoveStatus.Unavailable);
        }

        if (!TryGetReachableRouteDestinations(
                character,
                currentPosition,
                remainingMove,
                out var reachableDestinations,
                allowPathfinding: true))
        {
            return new ProxyThreatRouteMoveResult(ProxyThreatRouteMoveStatus.Unavailable);
        }

        var excludedKeys = new HashSet<string>(
            (excludedDestinations ?? Enumerable.Empty<int3>())
            .Select(GetPositionKey))
        {
            GetPositionKey(currentPosition),
            GetPositionKey(startPosition)
        };

        if (TryGetRecentNoMoveProxyThreatAttempt(character, currentPosition, out var previousProxyAttempt))
        {
            excludedKeys.Add(GetPositionKey(previousProxyAttempt.FailedDestination));
        }

        var candidates = BuildReachableProxyThreatRecoveryCandidates(
            character,
            turnPlan,
            battleService,
            currentPosition,
            sourcePosition,
            reachableDestinations,
            excludedKeys,
            target ?? turnPlan.ActionProbe.Target,
            remainingMove,
            currentThreatDistance);
        foreach (var candidate in candidates.Take(ProxyThreatRecoveryCandidateLimit))
        {
            if (!TryValidateForcedRouteDestination(
                    character,
                    turnPlan,
                    battleService,
                    profile,
                    currentPosition,
                    candidate.Position,
                    requireActionAfterMove))
            {
                continue;
            }

            destination = candidate.Position;
            ActionLinkedMoveCache[character.Guid] = new ActionLinkedMoveMemory(
                target,
                actionKind,
                continuation,
                movementGoal,
                currentPosition,
                candidate.Position,
                CombatAiRouteMoveSourceKind.Normal,
                lockRemainingMovement,
                GetCurrentBattleRound(),
                Math.Max(1, ObservedCombatMemoryTurnStamp));

            if (lockRemainingMovement)
            {
                RecordPendingRouteMovementLock(character, movementGoal, continuation, candidate.Position);
            }

            MarkDashBlockedAfterRouteMove(
                character,
                movementGoal,
                candidate.Position);
            FreeJumpContext.SuppressAiFreeJumpForNextMove(character, candidate.Position);
            character.MyExecuteActionTacticalMove(candidate.Position);

            return new ProxyThreatRouteMoveResult(ProxyThreatRouteMoveStatus.Executed, candidate.Position);
        }

        return new ProxyThreatRouteMoveResult(ProxyThreatRouteMoveStatus.Unavailable);
    }

    private static List<ProxyThreatRecoveryCandidate> BuildReachableProxyThreatRecoveryCandidates(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService,
        int3 currentPosition,
        int3 sourcePosition,
        ReachableRouteDestinationMemory reachableDestinations,
        HashSet<string> excludedKeys,
        GameLocationCharacter target,
        int remainingMove,
        float currentThreatDistance)
    {
        var candidates = new List<ProxyThreatRecoveryCandidate>();
        var preferredAction = turnPlan.ActionProbe.PreferredAction;
        var backupAction = turnPlan.ActionProbe.BackupAction;

        foreach (var candidatePosition in reachableDestinations.Positions)
        {
            if (candidatePosition == currentPosition ||
                excludedKeys.Contains(GetPositionKey(candidatePosition)))
            {
                continue;
            }

            if (RequiresMainDashForForcedMove(character, currentPosition, candidatePosition))
            {
                continue;
            }

            var candidateThreatDistance = ComputeGridDistance(candidatePosition, sourcePosition);
            var threatGain = candidateThreatDistance - currentThreatDistance;

            if (candidateThreatDistance <= ThreatAvoidanceThreatZoneDistance)
            {
                continue;
            }

            if (threatGain < ThreatAvoidanceMinimumDistanceGain)
            {
                continue;
            }

            var canUsePreferredAction =
                target?.RulesetCharacter != null &&
                preferredAction != CombatAiActionKind.None &&
                CanUseActionKindAtPosition(
                    character,
                    candidatePosition,
                    target,
                    preferredAction,
                    battleService);
            var canUseBackupAction =
                !canUsePreferredAction &&
                target?.RulesetCharacter != null &&
                backupAction != CombatAiActionKind.None &&
                CanUseActionKindAtPosition(
                    character,
                    candidatePosition,
                    target,
                    backupAction,
                    battleService);
            var connectsAttack = canUsePreferredAction || canUseBackupAction;
            var defensiveFallback =
                !connectsAttack &&
                IsBreakThreatDefensiveFallbackDestination(
                    character,
                    turnPlan,
                    battleService,
                    currentPosition,
                    candidatePosition,
                    out _);

            if (!connectsAttack && !defensiveFallback)
            {
                continue;
            }

            var score =
                threatGain +
                (connectsAttack ? 1.0f : 0.25f) -
                (ComputeForcedMoveCost(currentPosition, candidatePosition) * 0.03f) +
                ComputeStableTieBreakScore(
                    character,
                    turnPlan,
                    candidatePosition,
                    connectsAttack
                        ? canUsePreferredAction ? preferredAction : backupAction
                        : CombatAiActionKind.None);

            candidates.Add(new ProxyThreatRecoveryCandidate(
                candidatePosition,
                score,
                threatGain,
                connectsAttack));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Position.x)
            .ThenBy(candidate => candidate.Position.y)
            .ThenBy(candidate => candidate.Position.z)
            .ToList();
    }

    private static bool CanApplyPartialThreatRouteMovementLock(
        CombatAiMovementGoalKind movementGoal,
        int3 startPosition,
        int3 actualDestination)
    {
        return IsThreatRouteMovementLockGoal(movementGoal) && actualDestination != startPosition;
    }

    private static bool ApplyRouteMovementLock(
        GameLocationCharacter character,
        CombatAiMovementGoalKind movementGoal,
        CombatAiActionLinkedMoveContinuation continuation,
        int3 expectedDestination,
        string phase,
        bool safePosition = true,
        bool partial = false)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        if (character.RemainingTacticalMoves <= 0)
        {
            return false;
        }

        var remainingTacticalMoves = character.RemainingTacticalMoves;
        character.UsedTacticalMoves += remainingTacticalMoves;
        character.UsedTacticalMovesChanged?.Invoke(character);


        return true;
    }

    private static PostRouteTerminalResult TrySpendPostRouteTerminalAction(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        bool allowReadyDodge = true)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        var actionEconomy = BuildActionEconomySnapshot(character);

        if (!actionEconomy.MainAvailable &&
            !actionEconomy.ReadyAvailable &&
            !actionEconomy.DodgeAvailable)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        var profile = BuildProfile(character);
        var self = BuildSelfAssessment(character);
        var turnPlan = BuildCombatAiActionOnlyTurnPlan(character, profile, battleService);

        var terminalReprobeResult = TryUseTerminalReprobeHostileAction(character, turnPlan, battleService);

        if (terminalReprobeResult.Executed)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Executed, CombatAiExecutedActionKind.None);
        }

        var currentTerminalScan = BuildCurrentTerminalActionScan(
            character,
            turnPlan.ActionProbe,
            battleService,
            profile,
            self);
        var hasTerminalPolicyHeld = HasTerminalPolicyHeld(false, terminalReprobeResult.PolicyHeld);
        var hasTerminalFallbackBlock = TryGetTerminalFallbackBlock(
            currentTerminalScan,
            hasTerminalPolicyHeld,
            out _);
        var hasDisconnectedMovementLeak = TryGetDisconnectedPositioningMovementLeak(
            character,
            out _);
        var canSpendTerminalMain = CanSpendTerminalMainAction(
            character,
            actionEconomy);

        if (!allowReadyDodge)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        if (canSpendTerminalMain &&
            !hasTerminalFallbackBlock &&
            TryUseFallbackReady(
                     character,
                     profile,
                     turnPlan,
                     battleService,
                     currentTerminalScan))
        {
            RecordTerminalAction(character, turnPlan, CombatAiExecutedActionKind.Ready);
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Executed, CombatAiExecutedActionKind.Ready);
        }

        if (TryUseFallbackAtWillSelfBuff(character, profile, self, turnPlan))
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Executed);
        }

        if (hasTerminalFallbackBlock)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        if (!canSpendTerminalMain)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        if (hasDisconnectedMovementLeak)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        if (!actionEconomy.DodgeAvailable)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        var dodgeResult = TryApplyFallbackDodge(character);

        if (!dodgeResult.Executed)
        {
            return new PostRouteTerminalResult(PostRouteTerminalStatus.Blocked);
        }

        RecordTerminalAction(character, turnPlan, CombatAiExecutedActionKind.Dodge);

        return new PostRouteTerminalResult(PostRouteTerminalStatus.Executed, CombatAiExecutedActionKind.Dodge);
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
                CombatAiResidualHostileActionResultKind.Executed,
                CombatAiActionKind.Melee,
                Id.AttackMain);
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
                CombatAiResidualHostileActionResultKind.Executed,
                actionProbe.BackupAction,
                actionProbe.BackupAction == CombatAiActionKind.Spell ? Id.CastMain : Id.AttackMain);
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
            actionProbe.BackupAction != CombatAiActionKind.Ranged)
        {
            return false;
        }

        var profile = BuildProfile(character);
        var capabilityCatalog = BuildCapabilityCatalog(character);

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
            return true;
        }

        if (ShouldReleaseRangedBackupAfterFailedMeleePursuit(
                character,
                actionProbe,
                battleService))
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

        if (JumpImmediateAttackReachableCache.Contains(character.Guid))
        {
            return true;
        }

        return false;
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
        var capabilityCatalog = BuildCapabilityCatalog(character);

        return ShouldPreferMeleeAction(profile, capabilityCatalog) &&
               CanUseActionKindAtPosition(
                   character,
                   character.LocationPosition,
                   actionProbe.Target,
                   CombatAiActionKind.Melee,
                   battleService);
    }

    private static RepeatedMeleeAlternativeResult TryUseRepeatedMeleeAlternativeAction(
        GameLocationCharacter character,
        CombatAiTurnPlan turnPlan,
        IGameLocationBattleService battleService)
    {
        var target = turnPlan.ActionProbe.Target;

        if (character?.RulesetCharacter == null ||
            target?.RulesetCharacter == null ||
            battleService == null ||
            turnPlan.ActionProbe.PreferredAction != CombatAiActionKind.Melee ||
            !turnPlan.ActionProbe.CanUsePreferredAction)
        {
            return new RepeatedMeleeAlternativeResult(RepeatedMeleeAlternativeResultKind.None);
        }

        if (!TryGetRepeatedMeleeAttackMemory(character, target, out var memory) ||
            memory.RepeatCount < RepeatedMeleeAlternativeThreshold)
        {
            return new RepeatedMeleeAlternativeResult(RepeatedMeleeAlternativeResultKind.None);
        }

        var validation = ValidateResidualMainAction(character, target, CombatAiActionKind.Melee, battleService);

        if (!validation.IsValid)
        {
            return new RepeatedMeleeAlternativeResult(
                RepeatedMeleeAlternativeResultKind.Blocked,
                CombatAiExecutedActionKind.None,
                validation.BlockKind,
                memory.RepeatCount);
        }

        if (TryUseResidualShoveProne(character, target, battleService))
        {
            return new RepeatedMeleeAlternativeResult(
                RepeatedMeleeAlternativeResultKind.Executed,
                CombatAiExecutedActionKind.Shove,
                repeatCount: memory.RepeatCount);
        }

        if (TryUseResidualGrapple(character, target, battleService))
        {
            return new RepeatedMeleeAlternativeResult(
                RepeatedMeleeAlternativeResultKind.Executed,
                CombatAiExecutedActionKind.Grapple,
                repeatCount: memory.RepeatCount);
        }

        return new RepeatedMeleeAlternativeResult(
            RepeatedMeleeAlternativeResultKind.Blocked,
            repeatCount: memory.RepeatCount);
    }

    private static bool TryUseResidualShoveProne(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        if (target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionProne))
        {
            return false;
        }

        if (character.GetActionStatus(Id.Shove, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        if (!CanAttackInMeleeFromPosition(
                character,
                character.LocationPosition,
                target,
                target.LocationPosition,
                battleService))
        {
            return false;
        }

        if (!HasLikelyAthleticsContestSuccess(character, target))
        {
            return false;
        }

        var actionService = ServiceRepository.GetService<IGameLocationActionService>();

        if (actionService == null)
        {
            return false;
        }

        var actionParams = new CharacterActionParams(character, Id.Shove)
        {
            BoolParameter = true,
            TargetCharacters = { target }
        };

        MarkPendingResidualMainAction(character, Id.Shove);
        actionService.ExecuteAction(actionParams, null, true);

        return true;
    }

    private static bool TryUseResidualGrapple(
        GameLocationCharacter character,
        GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        var grappleActionId = (Id)ExtraActionId.Grapple;

        if (!Main.Settings.EnableGrappleAction)
        {
            return false;
        }

        if (target.IsGrappled())
        {
            return false;
        }

        if (character.GetActionStatus(grappleActionId, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        if (!ValidatorsCharacter.HasFreeHand(character.RulesetCharacter))
        {
            return false;
        }

        if (GrappleContext.CantGrapple(character.RulesetCharacter, target.RulesetCharacter))
        {
            return false;
        }

        if (!CanAttackInMeleeFromPosition(
                character,
                character.LocationPosition,
                target,
                target.LocationPosition,
                battleService))
        {
            return false;
        }

        if (!HasLikelyAthleticsContestSuccess(character, target))
        {
            return false;
        }

        var actionService = ServiceRepository.GetService<IGameLocationActionService>();

        if (actionService == null)
        {
            return false;
        }

        var actionParams = new CharacterActionParams(character, grappleActionId)
        {
            TargetCharacters = { target }
        };

        MarkPendingResidualMainAction(character, grappleActionId);
        actionService.ExecuteAction(actionParams, null, true);

        return true;
    }

    private static bool HasLikelyAthleticsContestSuccess(
        GameLocationCharacter actor,
        GameLocationCharacter target)
    {
        var actorBonus = ComputeAthleticsContestBonus(actor);
        var targetBonus = Math.Max(ComputeAthleticsContestBonus(target), ComputeAcrobaticsContestBonus(target));

        if (target?.RulesetCharacter?.IsIncapacitated == true)
        {
            return true;
        }

        return actorBonus >= targetBonus + 1;
    }

    private static int ComputeAthleticsContestBonus(GameLocationCharacter character)
    {
        return character?.RulesetCharacter?.ComputeBaseAbilityCheckBonus(
            AttributeDefinitions.Strength,
            null,
            SkillDefinitions.Athletics) ?? int.MinValue;
    }

    private static int ComputeAcrobaticsContestBonus(GameLocationCharacter character)
    {
        return character?.RulesetCharacter?.ComputeBaseAbilityCheckBonus(
            AttributeDefinitions.Dexterity,
            null,
            SkillDefinitions.Acrobatics) ?? int.MinValue;
    }

    private static CombatAiResidualHostileActionResult TryUseResidualSafeHostileAction(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService)
    {
        var validation = ValidateResidualMainAction(character, target, actionKind, battleService);

        if (!validation.IsValid)
        {
            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Blocked,
                actionKind,
                Id.NoAction,
                validation.BlockKind,
                validation.ActionStatus);
        }

        return actionKind == CombatAiActionKind.Spell
            ? TryUseResidualCantripAttack(character, target, battleService)
            : TryUseResidualWeaponAttack(character, target, actionKind, battleService);
    }

    private static CombatAiMainActionValidation ValidateResidualMainAction(
        GameLocationCharacter character,
        GameLocationCharacter target,
        CombatAiActionKind actionKind,
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null)
        {
            return new CombatAiMainActionValidation(false, CombatAiMainActionBlockKind.NoActor);
        }

        if (target?.RulesetCharacter == null || target.Side == character.Side)
        {
            return new CombatAiMainActionValidation(false, CombatAiMainActionBlockKind.NoHostileTarget);
        }

        var isWeaponAttack = actionKind is CombatAiActionKind.Melee or CombatAiActionKind.Ranged;

        if (TryGetCommittedNonTerminalMainActionThisTurn(
                character,
                out _,
                out _))
        {
            return new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.MainAlreadySpent);
        }

        if (isWeaponAttack)
        {
            var attackMainStatus = character.GetActionStatus(Id.AttackMain, ActionScope.Battle);

            if (attackMainStatus != ActionStatus.Available)
            {
                return new CombatAiMainActionValidation(
                    false,
                    CombatAiMainActionBlockKind.AttackMainUnavailable,
                    attackMainStatus);
            }
        }
        else if (character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available)
        {
            var mainStatus = character.GetActionTypeStatus(ActionType.Main);

            return new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.MainUnavailable,
                mainStatus);
        }

        if (battleService == null)
        {
            return new CombatAiMainActionValidation(false, CombatAiMainActionBlockKind.NoBattleService);
        }

        if (actionKind == CombatAiActionKind.None)
        {
            return new CombatAiMainActionValidation(false, CombatAiMainActionBlockKind.NoActionKind);
        }

        if (!CanUseActionKindAtPosition(character, character.LocationPosition, target, actionKind, battleService))
        {
            return new CombatAiMainActionValidation(
                false,
                CombatAiMainActionBlockKind.ActionNotUsableAtPosition);
        }

        return new CombatAiMainActionValidation(true);
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
                CombatAiResidualHostileActionResultKind.Blocked,
                actionKind,
                Id.AttackMain,
                CombatAiMainActionBlockKind.AttackMainUnavailable,
                attackMainStatus);
        }

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (mode == null ||
                (actionKind == CombatAiActionKind.Melee && !IsMeleeAttackModeForAi(mode)) ||
                (actionKind == CombatAiActionKind.Ranged && !IsRangedAttackMode(mode)))
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

            MarkPendingResidualMainAction(character, Id.AttackMain, $"residual:{actionKind}");
            character.MyExecuteActionAttack(Id.AttackMain, target, mode, modifier);

            return new CombatAiResidualHostileActionResult(
                CombatAiResidualHostileActionResultKind.Executed,
                actionKind,
                Id.AttackMain);
        }

        return new CombatAiResidualHostileActionResult(
            CombatAiResidualHostileActionResultKind.Blocked,
            actionKind,
            Id.AttackMain,
            CombatAiMainActionBlockKind.ActionNotUsableAtPosition);
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
                CombatAiResidualHostileActionResultKind.Blocked,
                CombatAiActionKind.Spell,
                Id.CastMain,
                CombatAiMainActionBlockKind.Other);
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

        MarkPendingResidualMainAction(character, Id.CastMain, "residual:Spell");
        actionService.ExecuteAction(actionParams, null, true);

        return new CombatAiResidualHostileActionResult(
            CombatAiResidualHostileActionResultKind.Executed,
            CombatAiActionKind.Spell,
            Id.CastMain);
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
