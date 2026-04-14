using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
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

internal static class CombatAiContext
{
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

    private static readonly string[] CautiousFlags = ["Self-Preservation", "Pragmatism", "Cynicism"];
    private static readonly string[] DisciplinedFlags = ["Authority", "Lawfulness", "Helpfulness", "Friendliness"];
    private static readonly string[] OpportunisticFlags = ["Greed", "Selfishness"];
    private static readonly Dictionary<ulong, CombatAiProfile> ProfileCache = [];
    private static readonly Dictionary<ulong, string[]> PersonalityFlagsCache = [];

    private readonly struct EnemyEvaluation(
        GameLocationCharacter enemy,
        float distance,
        bool meleeReachable,
        bool unreachableFlyingForMelee,
        bool exposesActorToMeleeThreat,
        bool rangedAttackAvailableFromPosition,
        CoverType rangedCoverType,
        bool isWounded,
        bool isCaster,
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
        internal bool IsWounded { get; } = isWounded;
        internal bool IsCaster { get; } = isCaster;
        internal bool IsConcentrating { get; } = isConcentrating;
        internal bool IsApproachSource { get; } = isApproachSource;
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

    internal static void PrimeTurnCache(GameLocationCharacter character)
    {
        if (!IsAdvancedCombatAiEnabled || character?.RulesetCharacter == null)
        {
            return;
        }

        _ = BuildProfile(character);
    }

    internal static void ClearTurnCache(GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        ProfileCache.Remove(character.Guid);

        if (character.RulesetCharacter != null)
        {
            PersonalityFlagsCache.Remove(character.RulesetCharacter.Guid);
        }
    }

    internal static bool IsAiControlledForCombat(GameLocationCharacter character)
    {
        if (!IsAdvancedCombatAiEnabled ||
            character == null ||
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

        foreach (var mode in attacker.RulesetCharacter.AttackModes)
        {
            if (mode == null || mode.Ranged || mode.Thrown)
            {
                continue;
            }

            var attackParams = new BattleDefinitions.AttackEvaluationParams();
            var modifier = new ActionModifier();

            attackParams.FillForPhysicalReachAttack(
                attacker, attackerPosition, mode, target, targetPosition, modifier);

            if (battleService.CanAttack(attackParams))
            {
                return true;
            }
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

    internal static bool TryAutoDodgeFlyingStalemate(GameLocationCharacter character)
    {
        if (!IsAdvancedCombatAiActionEconomyEnabled ||
            !IsAiControlledForCombat(character) ||
            character?.RulesetCharacter == null)
        {
            return false;
        }

        if (character.GetActionStatus(Id.Dodge, ActionScope.Battle) != ActionStatus.Available)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService == null || HasAnyUsableAttackAgainstVisibleEnemies(character, battleService))
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
        var denominator = consideration.IntParameter > 0 ? consideration.IntParameter : 1;
        var floatParameter = consideration.FloatParameter;
        var position = consideration.BoolParameter ? context.position : character.LocationPosition;
        var profile = BuildProfile(character);
        var rulesetCharacter = character.RulesetCharacter;

        var approachSourceGuid = rulesetCharacter.ConditionsByCategory
            .SelectMany(x => x.Value)
            .FirstOrDefault(x =>
                x.ConditionDefinition.Name == consideration.StringParameter)?.SourceGuid ?? 0;

        var evaluations = CollectEnemyEvaluations(character, profile, position, parameters, approachSourceGuid);

        if (evaluations.Length == 0)
        {
            return 0f;
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
                ? Mathf.Lerp(0.0f, 1f, Mathf.Clamp(evaluation.Distance / floatParameter, 0.0f, 1f))
                : ComputeDistancePreferenceScore(profile, evaluation.Distance, floatParameter);

            numerator += distanceScore * ComputeEnemyPriorityWeight(profile, evaluation);
        }

        var score = numerator / denominator;

        if (IsAdvancedCombatAiPositioningEnabled)
        {
            score += ComputePositionBias(profile, evaluations, floatParameter);
        }

        return Mathf.Clamp01(score);
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

            var rulesetEnemy = enemy.RulesetCharacter;

            evaluations[i] = new EnemyEvaluation(
                enemy,
                distance,
                meleeReachable,
                unreachableFlyingForMelee,
                exposesActorToMeleeThreat,
                rangedAttackAvailableFromPosition,
                rangedCoverType,
                rulesetEnemy.MissingHitPoints > rulesetEnemy.CurrentHitPoints,
                HasSpellcasting(rulesetEnemy),
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
                weight += 0.15f;
            }
        }

        if (profile.Temperament is CombatAiTemperament.Disciplined
            or CombatAiTemperament.Cunning
            or CombatAiTemperament.CunningAggressive)
        {
            if (evaluation.IsCaster)
            {
                weight += 0.15f;
            }

            if (evaluation.IsConcentrating)
            {
                weight += 0.15f;
            }

            if (evaluation.IsWounded)
            {
                weight += 0.10f;
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
                    weight += 0.08f;
                }

                if (evaluation.IsCaster)
                {
                    weight -= 0.05f;
                }

                if (evaluation.IsConcentrating)
                {
                    weight -= 0.03f;
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
                if (profile.PrefersDistance && (evaluation.IsCaster || evaluation.IsConcentrating))
                {
                    weight += 0.10f;
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

        return weight;
    }

    private static float ComputePositionBias(
        CombatAiProfile profile,
        EnemyEvaluation[] evaluations,
        float floatParameter)
    {
        if (!IsAdvancedCombatAiPositioningEnabled || evaluations.Length == 0)
        {
            return 0f;
        }

        var bias = 0.0f;
        var exposedThreats = 0;
        var hasSafeRangedLine = false;
        var canAttackFromPosition = false;
        var nearestDistance = float.MaxValue;

        for (var i = 0; i < evaluations.Length; i++)
        {
            var evaluation = evaluations[i];

            if (evaluation.ExposesActorToMeleeThreat)
            {
                exposedThreats++;
            }

            if (evaluation.RangedAttackAvailableFromPosition && evaluation.RangedCoverType <= CoverType.Half)
            {
                hasSafeRangedLine = true;
            }

            if (evaluation.MeleeReachable || evaluation.RangedAttackAvailableFromPosition)
            {
                canAttackFromPosition = true;
            }

            if (evaluation.Distance < nearestDistance)
            {
                nearestDistance = evaluation.Distance;
            }
        }

        if (profile.PrefersDistance)
        {
            var exposedPenalty = 0.10f;

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

        return bias;
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
        return rulesetCharacter switch
        {
            RulesetCharacterHero hero => hero.SpellRepertoires.Count > 0,
            RulesetCharacterMonster monster => monster.MonsterDefinition.Features.Any(x => x is FeatureDefinitionCastSpell),
            _ => false
        };
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
