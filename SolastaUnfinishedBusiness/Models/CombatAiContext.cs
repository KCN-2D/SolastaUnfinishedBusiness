using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
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

internal static class CombatAiContext
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

    private static readonly string[] CautiousFlags = ["Self-Preservation", "Pragmatism", "Cynicism"];
    private static readonly string[] DisciplinedFlags = ["Authority", "Lawfulness", "Helpfulness", "Friendliness"];
    private static readonly string[] OpportunisticFlags = ["Greed", "Selfishness"];
    private static readonly Dictionary<ulong, CombatAiProfile> ProfileCache = [];
    private static readonly Dictionary<ulong, string[]> PersonalityFlagsCache = [];
    private static readonly Dictionary<ulong, ObservedCombatMemory> ObservedCombatMemoryCache = [];
    private static readonly Dictionary<AttackPositionKey, bool> MeleeAttackPositionCache = [];
    private static int ObservedCombatMemoryTurnStamp;

    private readonly struct ObservedCombatMemory(
        int3 lastKnownEnemyPosition,
        int round,
        int turnStamp)
    {
        internal int3 LastKnownEnemyPosition { get; } = lastKnownEnemyPosition;
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
        internal CoverType RangedCoverType { get; } = rangedCoverType;
        internal bool EnemyCanRangedAttackActorFromPosition { get; } = enemyCanRangedAttackActorFromPosition;
        internal CoverType ActorCoverFromEnemyRangedAttack { get; } = actorCoverFromEnemyRangedAttack;
        internal bool KnownRangedOrCasterThreat { get; } = knownRangedOrCasterThreat;
        internal bool ActorHasUsefulCover =>
            EnemyCanRangedAttackActorFromPosition && ActorCoverFromEnemyRangedAttack >= CoverType.Half;
        internal bool CanAttackFromPosition => MeleeReachable || RangedAttackAvailableFromPosition;
        internal bool IsWounded { get; } = isWounded;
        internal bool IsConcentrating { get; } = isConcentrating;
        internal bool IsApproachSource { get; } = isApproachSource;
    }

    private readonly struct FreeJumpPositionFacts(
        bool hasPerceivedEnemy,
        float nearestEnemyDistance,
        int meleeThreatCount,
        int coveredRangedThreatCount,
        bool canAttack)
    {
        internal bool HasPerceivedEnemy { get; } = hasPerceivedEnemy;
        internal float NearestEnemyDistance { get; } = nearestEnemyDistance;
        internal int MeleeThreatCount { get; } = meleeThreatCount;
        internal int CoveredRangedThreatCount { get; } = coveredRangedThreatCount;
        internal bool CanAttack { get; } = canAttack;
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
        IsAdvancedCombatAiEnabled && Main.Settings.EnableAdvancedCombatAIFlight;

    internal static bool IsAdvancedCombatAiPositioningEnabled =>
        IsAdvancedCombatAiEnabled && Main.Settings.EnableAdvancedCombatAIPositioning;

    internal static bool IsAdvancedCombatAiActionEconomyEnabled =>
        IsAdvancedCombatAiEnabled && Main.Settings.EnableAdvancedCombatAIActionEconomy;

    internal static bool IsAdvancedCombatAiProfilesEnabled =>
        IsAdvancedCombatAiEnabled && Main.Settings.EnableAdvancedCombatAIProfiles;

    internal static bool ShouldOverrideEnemyProximityScore(
        ConsiderationDescription consideration,
        DecisionParameters parameters)
    {
        if (IsAdvancedCombatAiEnabled)
        {
            return true;
        }

        var rulesetCharacter = parameters?.character?.GameLocationCharacter?.RulesetCharacter;

        return rulesetCharacter != null && GetApproachSourceGuid(rulesetCharacter, consideration?.StringParameter) != 0;
    }

    internal static void PrimeTurnCache(GameLocationCharacter character)
    {
        if (!IsAdvancedCombatAiEnabled || character?.RulesetCharacter == null)
        {
            return;
        }

        ObservedCombatMemoryTurnStamp++;
        MeleeAttackPositionCache.Clear();
        _ = BuildProfile(character);
    }

    internal static void ClearTurnCache(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        ProfileCache.Remove(character.Guid);
        MeleeAttackPositionCache.Clear();

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
    }

    internal static void Unload()
    {
        ProfileCache.Clear();
        PersonalityFlagsCache.Clear();
        ObservedCombatMemoryCache.Clear();
        MeleeAttackPositionCache.Clear();
        ObservedCombatMemoryTurnStamp = 0;
    }

    private static bool CanExecuteAutomaticCombatAction(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        var networkingService = ServiceRepository.GetService<INetworkingService>();

        return networkingService?.IsMultiplayerGame != true || networkingService.IsMasterClient;
    }

    internal static bool IsAiControlledForCombat(GameLocationCharacter character)
    {
        return IsAdvancedCombatAiEnabled && IsAiControlledByGame(character);
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
            if (mode == null || mode.Ranged || mode.Thrown)
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

        foreach (var enemy in character.PerceivedFoes)
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

        foreach (var enemy in character.PerceivedFoes)
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
        PrimeTurnCache(character);

        return true;
    }

    internal static bool CanUseFreeJumpForAi(GameLocationCharacter character)
    {
        if (!Main.Settings.EnableBonusActionFreeJump ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsAiControlledByGame(character) ||
            character?.RulesetCharacter == null ||
            character.UsedTacticalMoves != 0 ||
            !character.CanDecideToMoveByItself ||
            character.GetActionTypeStatus(ActionType.Bonus) != ActionStatus.Available ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var profile = BuildProfile(character);

        return !ShouldPreferFlightOverFreeJump(character, profile, battleService);
    }

    internal static bool TryEvaluateFreeJumpDestination(
        GameLocationCharacter actor,
        int3 start,
        int3 destination,
        FreeJumpContext.FreeJumpCheckPreview preview,
        bool bypassesObstacle,
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
        var hasEnemyFacts = currentFacts.HasPerceivedEnemy || destinationFacts.HasPerceivedEnemy;
        var emergency = self.IsCritical || self.IsBloodied || self.HasSeriousCondition || self.IsConcentrating;
        var canGainAttack = !currentFacts.CanAttack && destinationFacts.CanAttack;
        var reducesMeleeThreat = destinationFacts.MeleeThreatCount < currentFacts.MeleeThreatCount;
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

            if (reducesMeleeThreat)
            {
                score += (currentFacts.MeleeThreatCount - destinationFacts.MeleeThreatCount) *
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

            if (bypassesObstacle && (canGainAttack || reducesMeleeThreat || improvesCover || movesCloser || movesAway))
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
        else if (bypassesObstacle && (movesCloser || reducesMeleeThreat || improvesCover))
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

        foreach (var enemy in actor.PerceivedFoes)
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
            canMeleeAttack || canRangedAttack);
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
        if (!IsAdvancedCombatAiActionEconomyEnabled ||
            !CanExecuteAutomaticCombatAction(character) ||
            !IsAiControlledForCombat(character) ||
            character?.RulesetCharacter == null)
        {
            return false;
        }

        if (character.GetActionTypeStatus(ActionType.Main) != ActionStatus.Available)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null ||
            HasAnyUsefulHostileActionAgainstVisibleEnemies(character, battleService) ||
            !HasLeftoverActionCombatContext(character))
        {
            return false;
        }

        var profile = BuildProfile(character);
        var self = BuildSelfAssessment(character);
        var hasUsefulMovementOrPositioningFallback =
            HasUsefulMovementOrPositioningFallback(character, battleService, profile, self);

        if (hasUsefulMovementOrPositioningFallback)
        {
            return false;
        }

        if (TryUseFallbackReady(character, profile))
        {
            return true;
        }

        if (TryUseFallbackAtWillSelfBuff(character, profile, self))
        {
            return true;
        }

        if (character.GetActionStatus(Id.Dodge, ActionScope.Battle) != ActionStatus.Available ||
            !ShouldUseFallbackDodge(character, battleService))
        {
            return false;
        }

        character.MyExecuteActionDodge();

        return true;
    }

    internal static float ComputeEnemyProximityScore(
        DecisionContext context,
        ConsiderationDescription consideration,
        DecisionParameters parameters)
    {
        var character = parameters.character.GameLocationCharacter;
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

        if (evaluations.Length == 0)
        {
            return IsAdvancedCombatAiPositioningEnabled && IsPositionBasedScoring(consideration)
                ? ComputeSearchOrRegroupScore(character, position, parameters, floatParameter)
                : 0f;
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

        foreach (var enemy in parameters.situationalInformation.RelevantEnemies)
        {
            if (enemy?.RulesetCharacter == null ||
                !actor.PerceivedFoes.Contains(enemy) ||
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

        foreach (var ally in Gui.Battle.AllContenders)
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
        float floatParameter)
    {
        if (actor?.RulesetCharacter == null)
        {
            return 0f;
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

        return TryGetRegroupPosition(actor, parameters, out var regroupPosition)
            ? ComputeApproachPositionScore(actor.LocationPosition, position, regroupPosition, floatParameter, 0.30f)
            : 0f;
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

        return TryGetRegroupPosition(actor, parameters, out var regroupPosition)
            ? ComputeApproachPositionScore(actor.LocationPosition, position, regroupPosition, floatParameter, 0.30f)
            : 0f;
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

            if (enemy?.RulesetCharacter == null || !actor.PerceivedFoes.Contains(enemy))
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

    private static float ComputeGridDistance(int3 left, int3 right)
    {
        var deltaX = left.x - right.x;
        var deltaY = left.y - right.y;
        var deltaZ = left.z - right.z;

        return Mathf.Sqrt((deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ));
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

        foreach (var mode in attacker.RulesetCharacter.AttackModes)
        {
            if (mode == null || (!mode.Ranged && !mode.Thrown))
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

    private static EnemyEvaluation[] CollectEnemyEvaluations(
        GameLocationCharacter actor,
        CombatAiProfile profile,
        int3 position,
        DecisionParameters parameters,
        ulong approachSourceGuid)
    {
        var relevantEnemies = new List<GameLocationCharacter>();
        var hasRelevantPerceivedTarget = parameters.situationalInformation.HasRelevantPerceivedTarget;

        foreach (var enemy in parameters.situationalInformation.RelevantEnemies)
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

            evaluations[i] = new EnemyEvaluation(
                enemy,
                distance,
                meleeReachable,
                unreachableFlyingForMelee,
                exposesActorToMeleeThreat,
                rangedAttackAvailableFromPosition,
                rangedCoverType,
                enemyCanRangedAttackActorFromPosition,
                actorCoverFromEnemyRangedAttack,
                knownRangedOrCasterThreat,
                rulesetEnemy.MissingHitPoints > rulesetEnemy.CurrentHitPoints,
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

        var missingHitPoints = rulesetCharacter.MissingHitPoints;
        var currentHitPoints = rulesetCharacter.CurrentHitPoints;
        var maxHitPoints = currentHitPoints + missingHitPoints;
        var isWounded = missingHitPoints > 0;
        var isBloodied = missingHitPoints >= currentHitPoints && isWounded;
        var isCritical = maxHitPoints > 0 && currentHitPoints > 0 && currentHitPoints * 4 <= maxHitPoints;
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

    private static bool HasAnyUsefulHostileActionAgainstVisibleEnemies(
        GameLocationCharacter character,
        IGameLocationBattleService battleService)
    {
        return HasAnyUsableAttackAgainstVisibleEnemies(character, battleService);
    }

    private static bool HasLeftoverActionCombatContext(GameLocationCharacter character)
    {
        return HasRelevantPerceivedEnemies(character) ||
               TryGetLastKnownEnemyPosition(character, out _) ||
               TryGetRegroupPosition(character, out _);
    }

    private static bool TryUseFallbackReady(GameLocationCharacter character, CombatAiProfile profile)
    {
        if (character?.RulesetCharacter == null ||
            character.GetActionStatus(Id.Ready, ActionScope.Battle) != ActionStatus.Available ||
            !HasRelevantPerceivedEnemies(character) ||
            !TryGetFallbackReadyActionType(character, profile, out var readyActionType))
        {
            return false;
        }

        character.MyExecuteActionReady(readyActionType);
        ClearTurnCache(character);
        PrimeTurnCache(character);

        return true;
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

        return mode is { Ranged: false, Thrown: false };
    }

    private static bool HasRelevantPerceivedEnemies(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        foreach (var enemy in actor.PerceivedFoes)
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
        CombatAiSelfAssessment self)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null ||
            !(self.IsBloodied || self.HasSeriousCondition || profile.PrefersDistance || profile.HasSpellcasting))
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
            PrimeTurnCache(character);

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
        IGameLocationBattleService battleService)
    {
        if (character?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        return true;
    }

    private static bool HasUsefulMovementOrPositioningFallback(
        GameLocationCharacter character,
        IGameLocationBattleService battleService,
        CombatAiProfile profile,
        CombatAiSelfAssessment self)
    {
        if (character?.RulesetCharacter == null ||
            character.MaxTacticalMoves <= 0 ||
            character.UsedTacticalMoves >= character.MaxTacticalMoves ||
            character.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        if (FreeJumpContext.HasUsefulAiFreeJumpDestination(character))
        {
            return true;
        }

        var hasVisibleMeleeThreat = HasVisibleMeleeThreat(character, battleService);

        foreach (var enemy in character.PerceivedFoes)
        {
            if (enemy?.RulesetCharacter == null || enemy.Side == character.Side)
            {
                continue;
            }

            if (CanAttackInMeleeFromPosition(
                    character,
                    character.LocationPosition,
                    enemy,
                    enemy.LocationPosition,
                    battleService) ||
                TryGetRangedAttackModifierFromPosition(
                    character,
                    character.LocationPosition,
                    enemy,
                    enemy.LocationPosition,
                    battleService,
                    out _))
            {
                continue;
            }

            var distance = ComputeGridDistance(character.LocationPosition, enemy.LocationPosition);

            if ((profile.PrefersAggressivePursuit || !profile.PrefersDistance) && distance > 1.5f)
            {
                return true;
            }

            if (profile.PrefersDistance &&
                (hasVisibleMeleeThreat || self.IsBloodied || self.HasSeriousCondition || self.IsConcentrating))
            {
                return true;
            }
        }

        return TryGetLastKnownEnemyPosition(character, out _) ||
               TryGetRegroupPosition(character, out _);
    }

    private static bool HasVisibleMeleeThreat(
        GameLocationCharacter actor,
        IGameLocationBattleService battleService)
    {
        if (actor?.RulesetCharacter == null || battleService == null)
        {
            return false;
        }

        foreach (var enemy in actor.PerceivedFoes)
        {
            if (enemy?.RulesetCharacter == null || enemy.Side == actor.Side)
            {
                continue;
            }

            if (CanAttackInMeleeFromPosition(
                    enemy,
                    enemy.LocationPosition,
                    actor,
                    actor.LocationPosition,
                    battleService))
            {
                return true;
            }
        }

        return false;
    }

    private static CombatAiRole GetRole(
        GameLocationCharacter character,
        bool hasRangedBackup,
        bool hasSpellcasting)
    {
        var package = character.BehaviourPackage?.DecisionPackageDefinition;

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
            if (mode != null && !mode.Ranged && !mode.Thrown)
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
        if (rulesetCharacter is RulesetCharacterHero hero)
        {
            return hero.SpellRepertoires.Count > 0;
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

    private static bool HasRangedAttackModes(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null)
        {
            return false;
        }

        foreach (var mode in character.RulesetCharacter.AttackModes)
        {
            if (mode != null && (mode.Ranged || mode.Thrown))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVisibleFlightContext(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }

        return HasVisibleFlightContext(actor, actor.PerceivedFoes, actor.LocationPosition);
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
