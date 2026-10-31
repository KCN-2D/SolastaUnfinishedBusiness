using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using Resources = SolastaUnfinishedBusiness.Properties.Resources;
using SolastaUnfinishedBusiness.Validators;
using TA;
using UnityEngine;
using UnityEngine.AddressableAssets;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.DecisionPackageDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Models;

[AttributeUsage(AttributeTargets.Field)]
internal sealed class EnemyBattleCapabilityAttribute(int tier, ActivationTime activationTime, int rechargeMinimumRoll) : Attribute
{
    internal int Tier { get; } = tier;
    internal ActivationTime ActivationTime { get; } = activationTime;
    internal int MaxCharges => 1;
    internal int RechargeMinimumRoll { get; } = rechargeMinimumRoll;
    // Native 5–6 powers enter the d6 branch; the shared success predicate
    // also accepts 4 for capabilities with the lower threshold.
    internal RechargeRate RechargeNativeEnum => RechargeMinimumRoll == 6 ? RechargeRate.D6_6 : RechargeRate.D6_56;
    internal string RechargeRoll => RechargeMinimumRoll == 6 ? "6" : $"{RechargeMinimumRoll}–6";
}

internal enum EnemyBattleAffix
{
    // Values identify stable bits in the serialized battle ledger.
    [EnemyBattleCapability(1, ActivationTime.Reaction, 6)]
    ArcaneWard = 0,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 6)]
    Spellbreaker = 1,
    [EnemyBattleCapability(1, ActivationTime.Reaction, 4)]
    Parry = 2,
    [EnemyBattleCapability(1, ActivationTime.Reaction, 4)]
    Bodyguard = 3,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 5)]
    Reprisal = 4,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    Withdrawal = 5,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    HuntingRush = 6,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    Resolve = 7,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    Feint = 8,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 6)]
    Dread = 9,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    Command = 10,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    Restoration = 11,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 5)]
    ElementalRebuke = 12,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 5)]
    ArcaneHindrance = 13,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    PhaseStep = 14,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 5)]
    DrivingShove = 15,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    PackCall = 16,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    PredatoryLunge = 17,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 6)]
    FeyVeil = 18,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 6)]
    FiendMiasma = 19,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    ThoughtLock = 20,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 6)]
    ConstructBrace = 21,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    WingBuffet = 22,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    PrimordialRepulsion = 23,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    ViscousSnare = 24,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 6)]
    RootSnare = 25,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    WitheringMark = 26,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    PurifyingTouch = 27,
    [EnemyBattleCapability(1, ActivationTime.Reaction, 4)]
    PsychicBacklash = 28,
    [EnemyBattleCapability(3, ActivationTime.BonusAction, 6)]
    BlindingEmbers = 29,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 5)]
    StoneTremor = 30,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 5)]
    CausticRetort = 31,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    LightningSurge = 32,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 6)]
    ThunderousRoar = 33,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 6)]
    ForcePulse = 34,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    FocusedVolley = 35,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    ElementalLance = 36,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 5)]
    GraveLeech = 37,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    RadiantSmite = 38,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    GuardedStance = 39,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 5)]
    SecondWind = 40,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 5)]
    Rally = 41,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    SteadyingWord = 42,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    PressingAttack = 43,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    UnsettlingCry = 44,
    [EnemyBattleCapability(3, ActivationTime.Reaction, 6)]
    LegendaryResolve = 45,
    [EnemyBattleCapability(3, ActivationTime.BonusAction, 6)]
    HeroicRally = 46,
    [EnemyBattleCapability(3, ActivationTime.BonusAction, 6)]
    CrushingBlow = 47,
    [EnemyBattleCapability(3, ActivationTime.BonusAction, 6)]
    RadiantBolt = 48,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 5)]
    ArcaneRetort = 49,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 4)]
    ImprovisedThrow = 50,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    PiercingJab = 51,
    [EnemyBattleCapability(3, ActivationTime.Reaction, 6)]
    LastingSurge = 52,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 6)]
    QuickenedCantrip = 53,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 5)]
    ChargingRush = 54,
    [EnemyBattleCapability(1, ActivationTime.Reaction, 5)]
    ElementalAbsorption = 55,
    [EnemyBattleCapability(2, ActivationTime.Reaction, 5)]
    ReachIntercept = 56,
    [EnemyBattleCapability(1, ActivationTime.BonusAction, 5)]
    WeaponCoating = 57,
    [EnemyBattleCapability(2, ActivationTime.BonusAction, 6)]
    SpellFlight = 59,
    [EnemyBattleCapability(3, ActivationTime.Reaction, 6)]
    ArmorRend = 60,
    [EnemyBattleCapability(3, ActivationTime.BonusAction, 6)]
    SweepingVolley = 61,
    [EnemyBattleCapability(3, ActivationTime.Reaction, 6)]
    CoordinatedAdvance = 62
}

// This partial owns opt-in enemy capabilities and their battle lifetime. The other
// part of the context owns tactical evaluation and the native AI turn coordinator.
internal static partial class CombatAiContext
{
    // Reserve both halves of the serialized amount for capability selection and
    // consumption, so adding a capability does not move existing saved bits.
    private const int EnemyBattleSpentShift = 16;
    private const int EnemyBattleUsageBits = 2;
    private const int EnemyBattleUsageMask = (1 << EnemyBattleUsageBits) - 1;
    private static readonly Dictionary<EnemyBattleAffix,
        (ConditionDefinition Condition, FeatureDefinitionPower Power)> EnemyBattleAffixes = [];
    private static readonly Dictionary<FeatureDefinitionPower, EnemyBattleAffix> EnemyBattlePowerKinds = [];
    private static readonly HashSet<ConditionDefinition> EnemyBattleGrantConditions = [];
    private static readonly HashSet<FeatureDefinitionCastSpell> EnemyBattleCastingFeatures = [];
    private static readonly Dictionary<ulong, RulesetCharacter> EnemyBattleRecipients = [];
    private static readonly HashSet<FeatureDefinition> EnemyBattleGrantedFeatures = [];
    private static readonly FieldInfo EnemyBattleOrdinaryAttackModesField =
        AccessTools.Field(typeof(RulesetCharacter), "attackModes");
    private static readonly FieldInfo EnemyBattleOrdinaryMoveModesField =
        AccessTools.Field(typeof(RulesetCharacter), "moveModes");
    private static readonly FieldInfo EnemyBattleOrdinarySpellRepertoiresField =
        AccessTools.Field(typeof(RulesetCharacter), "spellRepertoires");
    private static ConditionDefinition _enemyBattleLedger;
    private static ConditionDefinition _enemyBattleUsageLedger;
    private static readonly List<(ConditionDefinition Selection, ConditionDefinition Usage)> EnemyBattleLedgerBanks = [];
    private static HashSet<string> _enemyBattleWingedBodyPrefabs;
    private static MonsterAttackDefinition _enemyBattleImprovisedThrow;
    private static SpellDefinition _enemyBattleFlightSpell;
    private static readonly Dictionary<EnemyBattleAffix, SpellDefinition> EnemyBattleFreeSpells = [];
    private static readonly Dictionary<EnemyBattleAffix, FeatureDefinitionCastSpell> EnemyBattleFreeCasting = [];
    private static readonly ConditionalWeakTable<RulesetSpellRepertoire, EnemyBattleCastingSource> EnemyBattleCastingSources = new();
    private static GameLocationBattle _enemyTacticsBattle;
    private static readonly ConditionalWeakTable<RulesetCharacter, EnemyBattleAreaSnapshot> EnemyBattleAreaPending = new();
    private static readonly ConditionalWeakTable<RulesetEffect, EnemyBattleAreaSnapshot> EnemyBattleAreaEffects = new();
    private static readonly ConditionalWeakTable<CharacterActionMagicEffect,
        Dictionary<ulong, List<EnemyBattleAttackResult>>> EnemyBattleMagicAttackOutcomes = new();
    private static readonly ConditionalWeakTable<CharacterAction, Dictionary<ulong, int>> EnemyBattleAttackDamage = new();
    private static readonly ConditionalWeakTable<CharacterAction, Dictionary<ulong, int>> EnemyBattleLatestAttackDamage = new();
    private static readonly ConditionalWeakTable<RulesetCharacter, EnemyBattleOnHitBonusContext> EnemyBattleOnHitBonusPending = new();
    private static readonly ConditionalWeakTable<List<EffectForm>, HashSet<ulong>> EnemyBattleOnHitBonusHits = new();
    private static readonly ConditionalWeakTable<RulesetCharacter, SavingThrowData> EnemyBattlePendingSavingThrows = new();

    private sealed class EnemyBattlePendingScope<TKey, TValue> : IDisposable
        where TKey : class where TValue : class
    {
        private readonly ConditionalWeakTable<TKey, TValue> _table;
        private readonly TKey _key;
        private readonly TValue _value;

        internal EnemyBattlePendingScope(ConditionalWeakTable<TKey, TValue> table, TKey key, TValue value)
        {
            _table = table;
            _key = key;
            _value = value;
            table.Remove(key);
            table.Add(key, value);
        }

        public void Dispose()
        {
            if (_table.TryGetValue(_key, out var current) && ReferenceEquals(current, _value))
            {
                _table.Remove(_key);
            }
        }
    }

    private sealed class EnemyBattleAttackResult(RollOutcome outcome)
    {
        internal RollOutcome Outcome { get; } = outcome;
        internal int DamageAmount { get; set; }
    }

    private sealed class EnemyBattleCastingSource(RulesetSpellRepertoire repertoire, string innateAbility)
    {
        internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
        internal string InnateAbility { get; } = innateAbility;
    }

    internal static bool TryGetEnemyBattleSpellCastingAbility(RulesetSpellRepertoire repertoire, out string ability)
    {
        ability = null;
        if (!IsEnemyBattleGrantedSpellRepertoire(repertoire) ||
            !EnemyBattleCastingSources.TryGetValue(repertoire, out var source))
        {
            return false;
        }
        ability = source.Repertoire?.SpellCastingAbility ?? source.InnateAbility;
        return true;
    }

    private sealed class EnemyBattleAreaSnapshot(EnemyBattleAffix kind, GameLocationCharacter[] targets, int diceNumber)
    {
        internal EnemyBattleAffix Kind { get; } = kind;
        internal GameLocationCharacter[] Targets { get; } = targets;
        internal int DiceNumber { get; } = diceNumber;
    }

    private static FeatureDefinitionPower GetEnemyBattleCapabilityPower(FeatureDefinitionPower power) =>
        power is FeatureDefinitionPowerSharedPool shared ? shared.GetUsagePoolPower() : power;

    private static bool TryGetEnemyBattleCapabilityKind(FeatureDefinitionPower power, out EnemyBattleAffix kind)
    {
        kind = default;
        return power != null && EnemyBattlePowerKinds.TryGetValue(power, out kind);
    }

    internal static int GetEnemyBattleRechargeMinimumRoll(FeatureDefinitionPower power) =>
        TryGetEnemyBattleCapabilityKind(GetEnemyBattleCapabilityPower(power), out var kind)
            ? GetEnemyBattleCapabilityMetadata(kind).RechargeMinimumRoll : 0;

    private static float GetEnemyBattleChallengeRating(RulesetCharacter actor)
    {
        if (actor is not RulesetCharacterMonster monster)
        {
            return 0;
        }

        var definition = monster.MonsterDefinition;
        // Campaign authors can set CR to zero to suppress experience rewards.
        // Use the native template only for capability selection; retain the campaign's CR and XP.
        return definition.IsUserContent && definition.ChallengeRating == 0
            ? DungeonMakerContext.GetUserMonsterReference(definition)?.ChallengeRating ?? 0
            : definition.ChallengeRating;
    }

    // Selection, power generation, charge restoration and UI share one fixed definition.
    private static readonly Dictionary<EnemyBattleAffix, EnemyBattleCapabilityAttribute> EnemyBattleCapabilityMetadata =
        Enum.GetValues(typeof(EnemyBattleAffix)).Cast<EnemyBattleAffix>().ToDictionary(kind => kind, kind =>
        {
            var metadata = typeof(EnemyBattleAffix).GetField(kind.ToString())?
                .GetCustomAttributes(typeof(EnemyBattleCapabilityAttribute), false)
                .Cast<EnemyBattleCapabilityAttribute>().SingleOrDefault();
            if (metadata == null || metadata.Tier is < 1 or > 3 ||
                metadata.ActivationTime is not (ActivationTime.BonusAction or ActivationTime.Reaction) ||
                metadata.RechargeMinimumRoll is < 4 or > 6)
            {
                throw new InvalidOperationException($"Enemy battle capability {kind} requires a tier, a bonus action or reaction, and a d6 recharge threshold from 4 to 6.");
            }
            return metadata;
        });

    private static EnemyBattleCapabilityAttribute GetEnemyBattleCapabilityMetadata(EnemyBattleAffix kind) =>
        EnemyBattleCapabilityMetadata[kind];

    private enum EnemyBattleCapabilityRole { Offense, Support, Defense }

    [Flags]
    private enum EnemyBattleCapabilityTraits
    {
        None = 0,
        Control = 1 << 0,
        AttackPreparation = 1 << 1,
        NativeBonusAttack = 1 << 2,
        RangedDamage = 1 << 3,
        RangedSpellAttack = 1 << 4,
        AreaDamage = 1 << 5,
        WeaponCoating = 1 << 6,
        SpellCasting = 1 << 7,
        UtilityTargetFamily = 1 << 8,
        FamilyPower = 1 << 9,
        MeleeCombatant = 1 << 10,
        PhysicalCombatant = 1 << 11,
    }

    private sealed class EnemyBattleCapabilityDefinition(EnemyBattleAffix kind,
        EnemyBattleCapabilityRole role, EnemyBattleCapabilityTraits traits,
        int utilityOrder = -1, string nativeAttackTag = null, int nativeAttackTagOrder = -1)
    {
        internal EnemyBattleAffix Kind { get; } = kind;
        internal EnemyBattleCapabilityAttribute Metadata { get; } = GetEnemyBattleCapabilityMetadata(kind);
        internal EnemyBattleCapabilityRole Role { get; } = role;
        internal EnemyBattleCapabilityTraits Traits { get; } = traits;
        internal int UtilityOrder { get; } = utilityOrder;
        internal string NativeAttackTag { get; } = nativeAttackTag;
        internal int NativeAttackTagOrder { get; } = nativeAttackTagOrder;
    }

    // Definition traits are shared by selection, generation and native action routing.
    // The enum and its metadata remain the stable serialized identity and resource contract.
    private static readonly Dictionary<EnemyBattleAffix, EnemyBattleCapabilityDefinition> EnemyBattleCatalog =
        BuildEnemyBattleCatalog();

    private static Dictionary<EnemyBattleAffix, EnemyBattleCapabilityDefinition> BuildEnemyBattleCatalog()
    {
        EnemyBattleCapabilityDefinition[] definitions =
        [
            new(EnemyBattleAffix.ArcaneWard, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.SpellCasting),
            new(EnemyBattleAffix.Spellbreaker, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.SpellCasting),
            new(EnemyBattleAffix.Parry, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.Bodyguard, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.Reprisal, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.Withdrawal, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.PhysicalCombatant),
            new(EnemyBattleAffix.HuntingRush, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.Resolve, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.None),
            new(EnemyBattleAffix.Feint, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.AttackPreparation | EnemyBattleCapabilityTraits.PhysicalCombatant, utilityOrder: 27),
            new(EnemyBattleAffix.Dread, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control, utilityOrder: 25),
            new(EnemyBattleAffix.Command, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.AttackPreparation, utilityOrder: 7),
            new(EnemyBattleAffix.Restoration, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.None, utilityOrder: 2),
            new(EnemyBattleAffix.ElementalRebuke, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.None),
            new(EnemyBattleAffix.ArcaneHindrance, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control, utilityOrder: 24),
            new(EnemyBattleAffix.PhaseStep, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.SpellCasting),
            new(EnemyBattleAffix.DrivingShove, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.PackCall, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.AttackPreparation | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 6),
            new(EnemyBattleAffix.PredatoryLunge, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.NativeBonusAttack | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant, nativeAttackTag: EnemyBattlePredatoryTag, nativeAttackTagOrder: 0),
            new(EnemyBattleAffix.FeyVeil, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 3),
            new(EnemyBattleAffix.FiendMiasma, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 11),
            new(EnemyBattleAffix.ThoughtLock, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 8),
            new(EnemyBattleAffix.ConstructBrace, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.PhysicalCombatant, utilityOrder: 4),
            new(EnemyBattleAffix.WingBuffet, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 22),
            new(EnemyBattleAffix.PrimordialRepulsion, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 23),
            new(EnemyBattleAffix.ViscousSnare, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.NativeBonusAttack | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant, utilityOrder: 19, nativeAttackTag: EnemyBattleViscousTag, nativeAttackTagOrder: 1),
            new(EnemyBattleAffix.RootSnare, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 10),
            new(EnemyBattleAffix.WitheringMark, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 18),
            new(EnemyBattleAffix.PurifyingTouch, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 0),
            new(EnemyBattleAffix.PsychicBacklash, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.BlindingEmbers, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 17),
            new(EnemyBattleAffix.StoneTremor, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 21),
            new(EnemyBattleAffix.CausticRetort, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.LightningSurge, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 9),
            new(EnemyBattleAffix.ThunderousRoar, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.AreaDamage | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.ForcePulse, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.AreaDamage | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.FocusedVolley, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.NativeBonusAttack | EnemyBattleCapabilityTraits.RangedDamage | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.PhysicalCombatant, utilityOrder: 13, nativeAttackTag: EnemyBattleVolleyTag, nativeAttackTagOrder: 2),
            new(EnemyBattleAffix.ElementalLance, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.RangedDamage | EnemyBattleCapabilityTraits.RangedSpellAttack | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 14),
            new(EnemyBattleAffix.GraveLeech, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.RadiantSmite, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.GuardedStance, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 32),
            new(EnemyBattleAffix.SecondWind, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 1),
            new(EnemyBattleAffix.Rally, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 29),
            new(EnemyBattleAffix.SteadyingWord, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 30),
            new(EnemyBattleAffix.PressingAttack, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.AttackPreparation | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.PhysicalCombatant, utilityOrder: 28),
            new(EnemyBattleAffix.UnsettlingCry, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 31),
            new(EnemyBattleAffix.LegendaryResolve, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.HeroicRally, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 5),
            new(EnemyBattleAffix.CrushingBlow, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.RadiantBolt, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.RangedDamage | EnemyBattleCapabilityTraits.RangedSpellAttack | EnemyBattleCapabilityTraits.SpellCasting | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower, utilityOrder: 15),
            new(EnemyBattleAffix.ArcaneRetort, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.ImprovisedThrow, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.NativeBonusAttack | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.PhysicalCombatant, utilityOrder: 16, nativeAttackTag: EnemyBattleImprovisedThrowTag, nativeAttackTagOrder: 3),
            new(EnemyBattleAffix.PiercingJab, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.NativeBonusAttack | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant, utilityOrder: 20, nativeAttackTag: EnemyBattlePiercingTag, nativeAttackTagOrder: 4),
            new(EnemyBattleAffix.LastingSurge, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.QuickenedCantrip, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.SpellCasting | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.ChargingRush, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.Control | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.ElementalAbsorption, EnemyBattleCapabilityRole.Defense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.SpellCasting | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.ReachIntercept, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant),
            new(EnemyBattleAffix.WeaponCoating, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.AttackPreparation | EnemyBattleCapabilityTraits.WeaponCoating | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.PhysicalCombatant, utilityOrder: 26),
            new(EnemyBattleAffix.SpellFlight, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.SpellCasting | EnemyBattleCapabilityTraits.FamilyPower),
            new(EnemyBattleAffix.ArmorRend, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.MeleeCombatant, nativeAttackTag: EnemyBattleWhirlwindTag, nativeAttackTagOrder: 6),
            new(EnemyBattleAffix.SweepingVolley, EnemyBattleCapabilityRole.Offense, EnemyBattleCapabilityTraits.NativeBonusAttack | EnemyBattleCapabilityTraits.RangedDamage | EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower | EnemyBattleCapabilityTraits.PhysicalCombatant, utilityOrder: 12, nativeAttackTag: EnemyBattleSweepingVolleyTag, nativeAttackTagOrder: 5),
            new(EnemyBattleAffix.CoordinatedAdvance, EnemyBattleCapabilityRole.Support, EnemyBattleCapabilityTraits.UtilityTargetFamily | EnemyBattleCapabilityTraits.FamilyPower),
        ];
        var catalog = definitions.ToDictionary(definition => definition.Kind);
        if (catalog.Count != EnemyBattleCapabilityMetadata.Count ||
            EnemyBattleCapabilityMetadata.Keys.Any(kind => !catalog.ContainsKey(kind)))
        {
            throw new InvalidOperationException("Every enemy battle capability requires one catalog definition.");
        }
        return catalog;
    }

    private static bool HasEnemyBattleCapabilityTrait(EnemyBattleAffix kind, EnemyBattleCapabilityTraits trait) =>
        EnemyBattleCatalog.TryGetValue(kind, out var definition) && (definition.Traits & trait) != 0;

    private static FeatureDefinitionPowerBuilder CreateEnemyBattlePowerBuilder(EnemyBattleAffix kind, string name,
        AssetReferenceSprite sprite = null)
    {
        var metadata = EnemyBattleCatalog[kind].Metadata;
        return FeatureDefinitionPowerBuilder.Create(name)
            .SetGuiPresentation($"EnemyBattle{kind}", Category.Feature, sprite ?? GetEnemyBattleStatusSprite(kind))
            .SetUsesFixed(metadata.ActivationTime, metadata.RechargeNativeEnum, usesPerRecharge: metadata.MaxCharges);
    }

    private static int GetEnemyBattleCapabilityTier(EnemyBattleAffix kind) => GetEnemyBattleCapabilityMetadata(kind).Tier;

    private static string GetEnemyBattleTitleTerm(EnemyBattleAffix kind, bool granted = false)
    {
        var grantTerm = $"Feature/&EnemyBattle{kind}GrantTitle";
        return granted && TranslatorContext.HasTranslation(grantTerm) ? grantTerm : $"Feature/&EnemyBattle{kind}Title";
    }

    private static int GetEnemyBattleSlotBudget(RulesetCharacter actor)
    {
        var cr = GetEnemyBattleChallengeRating(actor);
        return cr <= 0 ? 0 : cr < 5 ? 1 : cr < 9 ? 2 : cr < 13 ? 3 : cr < 17 ? 4 : 5;
    }

    private static bool MeetsEnemyBattleTierRequirement(RulesetCharacter actor, EnemyBattleAffix kind) =>
        GetEnemyBattleChallengeRating(actor) > 0 && GetEnemyBattleChallengeRating(actor) >=
        (GetEnemyBattleCapabilityTier(kind) switch { 2 => 5, 3 => 11, _ => 0 });

    private static FormattedDefinitionText BuildEnemyBattleFormattedDescription(EnemyBattleAffix kind,
        bool includeRecharge = false, bool activeEffect = false) => new(
        title: () => Gui.Localize(activeEffect ? $"Feature/&EnemyBattle{kind}EffectTitle" :
            GetEnemyBattleTitleTerm(kind, includeRecharge)),
        description: () => FormatEnemyBattleDescription(kind, null, includeRecharge, activeEffect),
        characterDescription: actor => FormatEnemyBattleDescription(kind, actor, includeRecharge, activeEffect),
        conditionDescription: condition =>
        {
            RulesetEntity.TryGetEntity<RulesetCharacter>(condition.SourceGuid, out var source);
            return FormatEnemyBattleDescription(kind, source, includeRecharge, activeEffect);
        }, descriptionIdentity: activeEffect ? (object)(kind, true) : kind);

    private static string FormatEnemyBattleDescription(EnemyBattleAffix kind, RulesetCharacter actor,
        bool includeRecharge = false, bool activeEffect = false)
    {
        actor = actor as RulesetCharacterMonster;
        var hasDamage = kind == EnemyBattleAffix.ElementalRebuke || GetEnemyBattleDamageType(kind) != null;
        var dice = !hasDamage || actor == null ? 1 : IsEnemyBattleAreaDamage(kind)
            ? GetEnemyBattleAreaDamageDiceNumber(actor, kind) : GetEnemyBattleCapabilityDamageDiceNumber(actor, kind);
        var amount = IsEnemyBattleWeaponCoating(kind) ? $"{(actor == null ? 1 : GetEnemyBattleCapabilityDamageDiceNumber(actor, kind))}d4" :
            kind == EnemyBattleAffix.SweepingVolley ? FormatEnemyBattleSweepingVolleyDamage(actor) :
            kind == EnemyBattleAffix.ArmorRend ? FormatEnemyBattleWhirlwindDamage(actor) :
            kind is EnemyBattleAffix.ImprovisedThrow or EnemyBattleAffix.PiercingJab ? (dice == 0 ? "0" : "1d4") :
            kind == EnemyBattleAffix.ArcaneRetort ? (actor?.TryGetProficiencyBonus() ?? 2).ToString() :
            kind == EnemyBattleAffix.PressingAttack ? GetEnemyBattlePressingAttackDamageBonus(actor).ToString() :
            kind == EnemyBattleAffix.SecondWind ? $"1d6+{actor?.TryGetProficiencyBonus() ?? 2}" :
            kind == EnemyBattleAffix.Rally ? $"1d4+{actor?.TryGetProficiencyBonus() ?? 2}" :
            kind == EnemyBattleAffix.HeroicRally ? $"2d6+{actor?.TryGetProficiencyBonus() ?? 2}" :
            kind == EnemyBattleAffix.Restoration
            ? $"{GetEnemyBattleChallengeTier(actor)}d6+{actor?.TryGetProficiencyBonus() ?? 2}"
            : dice == 0 ? "0" : $"{dice}d6";
        var suffix = activeEffect ? "EffectDescription" : hasDamage && dice == 0 &&
            kind is not (EnemyBattleAffix.ElementalRebuke or EnemyBattleAffix.GraveLeech or EnemyBattleAffix.RadiantSmite or EnemyBattleAffix.CrushingBlow) &&
            !IsEnemyBattleAreaDamage(kind) && !IsEnemyBattleRangedDamage(kind) && kind is not (EnemyBattleAffix.ImprovisedThrow or EnemyBattleAffix.PiercingJab)
            ? "NoDamageDescription" : "Description";
        var body = Gui.Format($"Feature/&EnemyBattle{kind}{suffix}", string.Empty, amount,
            Gui.FormatDamageType(GetEnemyBattleEffectiveDamageType(actor, kind) ?? DamageTypeFire, true),
            Gui.FormatDistance(GetEnemyBattlePowerRange(actor, kind)));
        return includeRecharge
            ? Gui.Format("Feature/&EnemyBattleRechargeFormat", GetEnemyBattleCapabilityMetadata(kind).RechargeRoll) + "\n" + body
            : body;
    }

    internal static void LoadEnemyBattleTactics()
    {
        if (_enemyBattleLedger != null)
        {
            return;
        }

        EnemyBattleFreeSpells.Add(EnemyBattleAffix.PhaseStep, MistyStep);
        EnemyBattleFreeSpells.Add(EnemyBattleAffix.ArcaneWard, Shield);
        EnemyBattleFreeSpells.Add(EnemyBattleAffix.Spellbreaker, Counterspell);
        EnemyBattleFreeSpells.Add(EnemyBattleAffix.ElementalAbsorption, SpellsContext.ElementalInfusion);
        EnemyBattleFreeSpells.Add(EnemyBattleAffix.RadiantBolt, GuidingBolt);

        // Selection and current charge survive a save and an OFF/ON cycle. No ordinary
        // stat, damage multiplier, or unlimited additional action is attached.
        _enemyBattleLedger = ConditionDefinitionBuilder.Create("ConditionEnemyBattleTacticsLedger")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.Always)
            .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
            .AddCustomSubFeatures(EnemyBattleLifecycle.Instance)
            .AddToDB();

        // Keep the stable two-bit layout while recording ready/discharged state.
        // The original selection/exhaustion amount remains compatible with older saves.
        _enemyBattleUsageLedger = ConditionDefinitionBuilder.Create("ConditionEnemyBattleTacticsUsageLedger")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.Always)
            .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
            .AddToDB();

        EnemyBattleLedgerBanks.Add((_enemyBattleLedger, _enemyBattleUsageLedger));
        var kinds = Enum.GetValues(typeof(EnemyBattleAffix)).Cast<EnemyBattleAffix>().OrderBy(kind => (int)kind).ToArray();
        var bankCount = (int)kinds.Last() / EnemyBattleSpentShift + 1;
        for (var bank = 1; bank < bankCount; bank++)
        {
            ConditionDefinition BuildLedger(string name) => ConditionDefinitionBuilder.Create(name)
                .SetGuiPresentationNoContent(true).SetSilent(Silent.Always)
                .SetSpecialInterruptions(ConditionInterruption.BattleEnd).AddToDB();
            EnemyBattleLedgerBanks.Add((BuildLedger($"ConditionEnemyBattleTacticsLedgerBank{bank}"),
                BuildLedger($"ConditionEnemyBattleTacticsUsageLedgerBank{bank}")));
        }

        foreach (var kind in kinds)
        {
            if ((int)kind < 0 ||
                GetEnemyBattleCapabilityMetadata(kind).MaxCharges > EnemyBattleUsageMask)
            {
                throw new InvalidOperationException("The enemy battle capability ledgers need more storage bits.");
            }

            var name = $"EnemyBattle{kind}";
            var power = kind == EnemyBattleAffix.LegendaryResolve
                ? BuildEnemyBattleLegendaryResolvePower($"Power{name}")
                : GetEnemyBattleCapabilityMetadata(kind).ActivationTime == ActivationTime.BonusAction
                ? BuildEnemyBattleBonusPower(kind, $"Power{name}")
                : BuildEnemyBattleReactionPower(kind, $"Power{name}");
            if (kind is EnemyBattleAffix.Reprisal or EnemyBattleAffix.DrivingShove || IsEnemyBattleNativeBonusAttack(kind))
            {
                // Delegated native attacks/shoves use these markers only as a capability/resource.
                // Reject direct power selection without changing their native action or reaction gates.
                power.AddCustomSubFeatures(new ValidatorsValidatePowerUse(_ => false));
            }
            var formattedText = BuildEnemyBattleFormattedDescription(kind);
            power.AddCustomSubFeatures(EnemyBattlePowerTooltipModifier.Instance, formattedText);
            var condition = ConditionDefinitionBuilder.Create($"Condition{name}")
                // The shared formatter supplies one contextual body for both the power and its grant condition.
                .SetGuiPresentation(GetEnemyBattleTitleTerm(kind, granted: true), $"Feature/&EnemyBattle{kind}Description",
                    power.GuiPresentation.SpriteReference)
                .SetConditionType(ConditionType.Beneficial)
                .SetPossessive()
                .SetSilent(Silent.WhenRefreshedOrRemoved)
                .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
                .SetFeatures(power)
                .AddCustomSubFeatures(AddUsablePowersFromCondition.Marker, Tooltips.HideHeaderImage.Instance,
                    BuildEnemyBattleFormattedDescription(kind, true))
                .AddToDB();

            if (kind == EnemyBattleAffix.DrivingShove)
            {
                // The native shove action owns its contested check, animations, movement and action cost.
                condition.Features.Add(FeatureDefinitionActionAffinityBuilder.Create($"ActionAffinity{name}")
                    .SetGuiPresentationNoContent(true)
                    .SetAuthorizedActions(Id.ShoveBonus)
                    .AddToDB());
            }
            if (IsEnemyBattleNativeBonusAttack(kind))
            {
                condition.Features.Add(FeatureDefinitionActionAffinityBuilder.Create($"ActionAffinity{name}")
                    .SetGuiPresentationNoContent(true).SetAuthorizedActions(Id.AttackOff).AddToDB());
                condition.AddCustomSubFeatures(EnemyBattleNativeAttack.Instance);
                power.AddCustomSubFeatures(ModifyPowerVisibility.Hidden);
            }

            if (IsEnemyBattleSpellCastingCapability(kind))
            {
                condition.AddCustomSubFeatures(new EnemyBattleSpellCasting(kind, power));
            }

            if (EnemyBattleFreeSpells.TryGetValue(kind, out var freeSpell))
            {
                if (kind != EnemyBattleAffix.Spellbreaker)
                {
                    freeSpell = SpellDefinitionBuilder.Create(freeSpell, $"Spell{name}")
                        .SetGuiPresentation(freeSpell.GuiPresentation)
                        .SetCastingTime(GetEnemyBattleCapabilityMetadata(kind).ActivationTime)
                        .SetEffectDescription(power.EffectDescription)
                        .AddCustomSubFeatures(RequireSpellCastingActionProvider.Marker).AddToDB();
                    EnemyBattleFreeSpells[kind] = freeSpell;
                }
                if (kind == EnemyBattleAffix.ElementalAbsorption)
                {
                    freeSpell.AddCustomSubFeatures(
                        power.GetFirstSubFeatureOfType<EnemyBattleElementalAbsorption>().EffectModifier,
                        PowerBundle.UncachedEffectDescription.Marker);
                }
                var list = SpellListDefinitionBuilder.Create($"SpellList{name}")
                    .SetGuiPresentationNoContent(true).ClearSpells()
                    .SetSpellsAtLevel(freeSpell.SpellLevel, freeSpell)
                    .FinalizeSpells(false, freeSpell.SpellLevel).AddToDB();
                var capacity = GetEnemyBattleCapabilityMetadata(kind).MaxCharges;
                var slots = Enumerable.Range(1, 20).Select(level =>
                    new FeatureDefinitionCastSpell.SlotsByLevelDuplet
                    {
                        Level = level,
                        Slots = Enumerable.Range(1, 9).Select(slot => slot == freeSpell.SpellLevel
                            ? capacity : 0).ToList()
                    }).ToList();
                var casting = FeatureDefinitionCastSpellBuilder.Create($"CastSpell{name}")
                    .SetGuiPresentation(name, Category.Feature, power.GuiPresentation.SpriteReference)
                    .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Monster)
                    // Fixed-spell capacity requires a valid native monster casting level.
                    .SetSpellCastingLevel(1)
                    .SetSpellCastingAbility(AttributeDefinitions.Wisdom)
                    .SetSpellKnowledge(SpellKnowledge.FixedList).SetSpellReadyness(SpellReadyness.AllKnown)
                    .SetSpellList(list).SetSlotsPerLevel(slots).SetSlotsRecharge(RechargeRate.None)
                    .AddCustomSubFeatures(new FeatHelpers.SpellTag(name), RequireSpellCastingActionProvider.Marker)
                    .AddToDB();
                EnemyBattleFreeCasting.Add(kind, casting);
                EnemyBattleCastingFeatures.Add(casting);
                EnemyBattleGrantedFeatures.Add(casting);
                power.AddCustomSubFeatures(ModifyPowerVisibility.Hidden);
            }

            // Active riders need the same source-aware formatting as their grants.
            // Keep specialized variant providers and unrelated native condition bodies.
            foreach (var activeCondition in power.EffectDescription.EffectForms
                         .Where(form => form.FormType == EffectForm.EffectFormType.Condition &&
                             form.ConditionForm.Operation == ConditionForm.ConditionOperation.Add)
                         .Select(form => form.ConditionForm.ConditionDefinition).Distinct())
            {
                if (activeCondition != null &&
                    activeCondition.GuiPresentation.Description == $"Feature/&EnemyBattle{kind}EffectDescription" &&
                    activeCondition.GetFirstSubFeatureOfType<FormattedDefinitionText>() == null)
                {
                    activeCondition.AddCustomSubFeatures(BuildEnemyBattleFormattedDescription(kind, activeEffect: true));
                }
            }

            EnemyBattleAffixes.Add(kind, (condition, power));
            EnemyBattlePowerKinds.Add(power, kind);
            EnemyBattleGrantConditions.Add(condition);
        }

        // Native-ability checks must not reject a capability because of its own grant or active rider.
        foreach (var entry in EnemyBattleAffixes.Values)
        {
            EnemyBattleGrantedFeatures.UnionWith(GetEnemyBattleConditionFeatures(entry.Condition));
            EnemyBattleGrantedFeatures.UnionWith(GetEnemyBattleAddedConditionFeatures(entry.Power.EffectDescription));
        }
    }

    internal static void StartEnemyBattleTactics(GameLocationBattle battle)
    {
        if (_enemyTacticsBattle != battle)
        {
            StopEnemyBattleTactics();
            _enemyTacticsBattle = battle;
        }

        RefreshEnemyBattleTactics();
    }

    internal static void RefreshEnemyBattleTactics()
    {
        var battle = _enemyTacticsBattle ?? Gui.Battle;

        if (battle == null || _enemyBattleLedger == null)
        {
            return;
        }

        _enemyTacticsBattle = battle;

        // Direct condition grants must run in the same deterministic order on all
        // peers. Only subsequent action issuance is restricted to the master.
        var enemies = battle.EnemyContenders.ToArray();

        foreach (var character in enemies)
        {
            ReconcileEnemyBattleTactics(character, battle);
        }

        foreach (var actor in EnemyBattleRecipients.Values.ToArray())
        {
            var location = GameLocationCharacter.GetFromActor(actor);

            if (Main.Settings.EnableEnemyBattleTactics && IsEnemyBattleRecipient(location, battle))
            {
                continue;
            }

            CaptureEnemyBattleCharges(actor);
            RemoveEnemyBattleCapabilities(actor, removeLedger: false);
        }
    }

    private static void ReconcileEnemyBattleTactics(GameLocationCharacter character, GameLocationBattle battle)
    {
        var actor = character?.RulesetCharacter;

        if (actor == null)
        {
            return;
        }

        RemoveEnemyBattleOwnedRiders(actor, EnemyBattleAffix.QuickenedCantrip);

        var ledger = FindEnemyBattleCondition(actor, _enemyBattleLedger);

        if (ledger != null)
        {
            EnemyBattleRecipients[actor.Guid] = actor;
        }

        if (!Main.Settings.EnableEnemyBattleTactics || !IsEnemyBattleRecipient(character, battle))
        {
            CaptureEnemyBattleCharges(actor);
            RemoveEnemyBattleCapabilities(actor, removeLedger: false);
            return;
        }

        if (ledger == null)
        {
            var selection = SelectEnemyBattleAffixes(character);

            if (selection.Count == 0)
            {
                return;
            }

            ledger = InflictEnemyBattleCondition(actor, _enemyBattleLedger);
            // Bank zero is also the initialization sentinel when only newer capabilities were selected.
            ledger.Amount = 0;
            foreach (var kind in selection)
            {
                var definition = EnemyBattleLedgerBanks[(int)kind / EnemyBattleSpentShift].Selection;
                var bankLedger = FindEnemyBattleCondition(actor, definition) ?? InflictEnemyBattleCondition(actor, definition);
                bankLedger.Amount |= 1 << ((int)kind % EnemyBattleSpentShift);
            }
            EnemyBattleRecipients[actor.Guid] = actor;
        }

        var effectiveSelection = GetEnemyBattleEffectiveSelection(character);
        var normalPowers = AddUsablePowersFromCondition.GetGrantedUsablePowers(actor);
        var nativeModesChanged = false;
        foreach (var entry in EnemyBattleAffixes)
        {
            var bank = EnemyBattleLedgerBanks[(int)entry.Key / EnemyBattleSpentShift];
            var selectionLedger = FindEnemyBattleCondition(actor, bank.Selection);
            var flag = 1 << ((int)entry.Key % EnemyBattleSpentShift);

            if (selectionLedger == null || (selectionLedger.Amount & flag) == 0 || !effectiveSelection.Contains(entry.Key))
            {
                // Preserve selection and charge when a saved capability no longer fits this body.
                // Removing its grant never re-rolls that enemy's battle selection or refills its pool.
                foreach (var usedPower in normalPowers.Where(item => item.PowerDefinition == entry.Value.Power &&
                             selectionLedger != null && (selectionLedger.Amount & flag) != 0).ToArray())
                {
                    WriteEnemyBattleChargeState(actor, usedPower);
                }
                foreach (var effect in actor.PowersUsedByMe.Where(effect => effect.PowerDefinition == entry.Value.Power).ToArray())
                {
                    actor.TerminatePower(effect);
                }
                RemoveEnemyBattleOwnedRiders(actor, entry.Key);
                if (FindEnemyBattleCondition(actor, entry.Value.Condition) is { } incompatibleGrant)
                {
                    actor.RemoveCondition(incompatibleGrant);
                    nativeModesChanged |= IsEnemyBattleNativeBonusAttack(entry.Key);
                }
                normalPowers.RemoveAll(item => item.PowerDefinition == entry.Value.Power);
                continue;
            }

            var existingPower = normalPowers.FirstOrDefault(item => item.PowerDefinition == entry.Value.Power);

            if (FindEnemyBattleCondition(actor, entry.Value.Condition) == null)
            {
                InflictEnemyBattleCondition(actor, entry.Value.Condition);
                nativeModesChanged |= IsEnemyBattleNativeBonusAttack(entry.Key);
            }

            // PostLoad does not invoke the condition-added callback. Preserve any
            // existing native power instance and its serialized remaining uses.
            var usable = normalPowers.FirstOrDefault(item => item.PowerDefinition == entry.Value.Power);

            if (usable == null)
            {
                usable = GetEnemyBattleUsablePower(entry.Value.Power, actor);
                normalPowers.Add(usable);
            }

            var metadata = GetEnemyBattleCapabilityMetadata(entry.Key);
            // A native power's current charge wins after a roll or repayment. When
            // restoring a removed grant, only the serialized exhaustion bit matters;
            // a partially spent old pool still had a usable charge.
            var remaining = existingPower != null ? (usable.RemainingUses > 0 ? metadata.MaxCharges : 0) :
                GetEnemyBattleStoredCharge(entry.Key, selectionLedger, metadata);
            usable.maxUses = metadata.MaxCharges;
            PowerProvider.RestoreRemainingUses(actor, usable, metadata.MaxCharges, remaining);
            WriteEnemyBattleChargeState(actor, usable);
        }
        RefreshEnemyBattleSpellGrants(actor);
        if (nativeModesChanged)
        {
            actor.RefreshAttackModes();
        }
    }

    private static RulesetCondition InflictEnemyBattleCondition(RulesetCharacter actor, ConditionDefinition definition)
    {
        return actor.InflictCondition(definition.Name, DurationType.Irrelevant, 1,
            TurnOccurenceType.StartOfTurn, AttributeDefinitions.TagEffect, actor.Guid,
            actor.CurrentFaction.Name, 1, definition.Name, 0, 0, 0);
    }

    private static RulesetCondition FindEnemyBattleCondition(RulesetCharacter actor, ConditionDefinition definition)
    {
        return definition == null ? null : actor.ConditionsByCategory.Values
            .SelectMany(conditions => conditions)
            .FirstOrDefault(condition => condition.ConditionDefinition == definition);
    }

    internal static void RecordEnemyBattlePowerUse(RulesetCharacter actor, RulesetUsablePower usablePower)
    {
        if (WriteEnemyBattleChargeState(actor, usablePower))
        {
            RefreshEnemyBattleSpellGrants(actor);
        }
    }

    private static bool WriteEnemyBattleChargeState(RulesetCharacter actor, RulesetUsablePower usablePower)
    {
        if (_enemyBattleLedger == null ||
            !TryGetEnemyBattleCapabilityKind(usablePower?.PowerDefinition, out var kind))
        {
            return false;
        }

        var bank = EnemyBattleLedgerBanks[(int)kind / EnemyBattleSpentShift];
        var ledger = FindEnemyBattleCondition(actor, bank.Selection);

        if (ledger != null)
        {
            var usageLedger = FindEnemyBattleCondition(actor, bank.Usage) ??
                              InflictEnemyBattleCondition(actor, bank.Usage);
            // Consumption, repayment and native recharge all report the current
            // charge, rather than cumulative uses during the battle.
            var spent = usablePower.RemainingUses > 0 ? 0 : 1;
            var slot = (int)kind % EnemyBattleSpentShift;
            var shift = slot * EnemyBattleUsageBits;
            usageLedger.Amount = (usageLedger.Amount & ~(EnemyBattleUsageMask << shift)) | (spent << shift);
            var exhausted = 1 << (slot + EnemyBattleSpentShift);
            ledger.Amount = spent != 0 ? ledger.Amount | exhausted : ledger.Amount & ~exhausted;
        }

        return true;
    }

    private static int GetEnemyBattleStoredCharge(EnemyBattleAffix kind, RulesetCondition ledger,
        EnemyBattleCapabilityAttribute metadata)
    {
        var slot = (int)kind % EnemyBattleSpentShift;
        return (ledger.Amount & (1 << (slot + EnemyBattleSpentShift))) != 0 ? 0 : metadata.MaxCharges;
    }

    private static void CaptureEnemyBattleCharges(RulesetCharacter actor)
    {
        var recorded = false;
        foreach (var usable in AddUsablePowersFromCondition.GetGrantedUsablePowers(actor))
        {
            recorded |= WriteEnemyBattleChargeState(actor, usable);
        }
        if (recorded)
        {
            RefreshEnemyBattleSpellGrants(actor);
        }
    }

    private static void RemoveEnemyBattleCapabilities(RulesetCharacter actor, bool removeLedger)
    {
        if (EnemyBattleWhirlwinds.TryGetValue(actor, out var whirlwind))
        {
            EndEnemyBattleWhirlwind(whirlwind);
        }
        EnemyBattleRetort.Clear(actor);
        EnemyBattleAreaPending.Remove(actor);
        foreach (var kind in EnemyBattleAffixes.Keys)
        {
            RemoveEnemyBattleOwnedRiders(actor, kind);
        }
        var hadNativeMode = EnemyBattleNativeAttackTargets.Remove(actor) ||
                            GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => TryGetEnemyBattleNativeAttackKind(mode, out _));
        // End source-owned effects first, including conditions on other contenders.
        // Native termination follows tracked condition GUIDs and preserves unrelated effects.
        foreach (var effect in actor.PowersUsedByMe
                     .Where(effect => EnemyBattlePowerKinds.ContainsKey(effect.PowerDefinition)).ToArray())
        {
            actor.TerminatePower(effect);
        }
        foreach (var spell in actor.SpellsCastByMe.Where(spell =>
                     IsEnemyBattleGrantedSpellRepertoire(spell.SpellRepertoire) ||
                     spell.SpellDefinition == _enemyBattleFlightSpell).ToArray())
        {
            actor.TerminateSpell(spell);
        }

        foreach (var conditions in actor.ConditionsByCategory.Values.ToArray())
        {
            foreach (var condition in conditions.ToArray())
            {
                if (EnemyBattleGrantConditions.Contains(condition.ConditionDefinition) ||
                    removeLedger && EnemyBattleLedgerBanks.Any(bank =>
                        condition.ConditionDefinition == bank.Selection || condition.ConditionDefinition == bank.Usage))
                {
                    actor.RemoveCondition(condition);
                }
            }
        }
        RefreshEnemyBattleSpellGrants(actor);
        if (hadNativeMode)
        {
            actor.RefreshAttackModes();
        }
    }

    private static void RemoveEnemyBattleOwnedRiders(RulesetCharacter actor, EnemyBattleAffix kind)
    {
        if (EnemyBattleAffixes.TryGetValue(kind, out var entry))
        {
            var definitions = (entry.Power.GetBundle()?.SubPowers ?? []).Append(entry.Power);
            var riders = new HashSet<ConditionDefinition>(definitions.SelectMany(power => power.EffectDescription.EffectForms)
                .Where(form => form.FormType == EffectForm.EffectFormType.Condition &&
                    form.ConditionForm.Operation == ConditionForm.ConditionOperation.Add)
                .Select(form => form.ConditionForm.ConditionDefinition)
                .Where(condition => condition != null && condition.Name.Contains("EnemyBattle")));
            var recipients = (Gui.Battle ?? _enemyTacticsBattle)?.AllContenders.Select(character => character.RulesetCharacter)
                .Where(character => character != null).Append(actor).Distinct() ?? new[] { actor };
            foreach (var recipient in recipients)
            {
                foreach (var ownedCondition in recipient.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                             .Where(condition => condition.SourceGuid == actor.Guid && riders.Contains(condition.ConditionDefinition)).ToArray())
                {
                    recipient.RemoveCondition(ownedCondition);
                }
            }
        }
    }

    internal static void StopEnemyBattleTactics()
    {
        foreach (var actor in EnemyBattleRecipients.Values.ToArray())
        {
            RemoveEnemyBattleCapabilities(actor, removeLedger: true);
        }

        EnemyBattleRecipients.Clear();
        _enemyTacticsBattle = null;
    }

    internal static void RestoreEnemyBattleTactics()
    {
        var battle = Gui.Battle;
        _enemyTacticsBattle = battle;

        if (battle != null)
        {
            RefreshEnemyBattleTactics();
            return;
        }

        // A save outside combat must not retain a capability from a former fight.
        var service = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (service != null)
        {
            foreach (var location in service.ValidCharacters)
            {
                if (location.RulesetCharacter != null)
                {
                    RemoveEnemyBattleCapabilities(location.RulesetCharacter, removeLedger: true);
                }
            }
        }
    }

    private static bool IsEnemyBattleRecipient(GameLocationCharacter character, GameLocationBattle battle)
    {
        if (battle == null || character == null || PlayerControllerContext.GetBattleSide(character) != Side.Enemy ||
            character.IsSummonedCreature || character.RulesetCharacter is not RulesetCharacterMonster
            { IsDeadOrDying: false, IsRemovedFromTheGame: false, ConjuredByParty: false } monster ||
            monster.OriginalFormCharacter != null || !battle.EnemyContenders.Contains(character))
        {
            return false;
        }

        var service = ServiceRepository.GetService<IGameLocationCharacterService>();

        return service == null ||
               !service.PartyCharacters.Contains(character) && !service.GuestCharacters.Contains(character);
    }

    internal static bool CanUseEnemyBattlePower(GameLocationCharacter actor, EnemyBattleAffix kind,
        out RulesetUsablePower usablePower)
    {
        usablePower = null;

        if (!Main.Settings.EnableEnemyBattleTactics ||
            !IsEnemyBattleRecipient(actor, Gui.Battle ?? _enemyTacticsBattle) ||
            actor.RulesetCharacter.IsDeadOrDyingOrUnconscious ||
            !EnemyBattleAffixes.TryGetValue(kind, out var entry) ||
            !GetEnemyBattleEffectiveSelection(actor).Contains(kind) ||
            FindEnemyBattleCondition(actor.RulesetCharacter, entry.Condition) == null)
        {
            return false;
        }

        usablePower = AddUsablePowersFromCondition.GetGrantedUsablePowers(actor.RulesetCharacter)
            .FirstOrDefault(item => item.PowerDefinition == entry.Power);
        if (usablePower == null || usablePower.RemainingUses <= 0)
        {
            return false;
        }

        PowerProvider.UpdateSaveDc(actor.RulesetCharacter, usablePower);
        return true;
    }

    private sealed class EnemyBattlePowerTooltipModifier : PowerTooltipModifier
    {
        internal static readonly EnemyBattlePowerTooltipModifier Instance = new();

        protected override void Apply(TooltipFeaturePowerParameters parameters, GuiPowerDefinition definition,
            RulesetCharacter character)
        {
            if (!TryGetEnemyBattleCapabilityKind(GetEnemyBattleCapabilityPower(definition.PowerDefinition), out var kind))
            {
                return;
            }
            parameters.rechargeLabel.Text = Gui.Format("Feature/&EnemyBattleRechargeFormat",
                GetEnemyBattleCapabilityMetadata(kind).RechargeRoll);
            var duration = GetEnemyBattleImmediateRoundDuration(definition.PowerDefinition.EffectDescription);
            if (duration != null)
            {
                parameters.durationLabel.Text = duration;
            }
            if (parameters.GetComponentInParent<TooltipPanel>() is not { } panel)
            {
                return;
            }
            var nativeReach = kind is EnemyBattleAffix.Reprisal or EnemyBattleAffix.PredatoryLunge or
                EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.PiercingJab or EnemyBattleAffix.RadiantSmite or EnemyBattleAffix.CrushingBlow;
            foreach (var magicParameters in panel.GetComponentsInChildren<TooltipFeatureBaseMagicParameters>(true))
            {
                if (nativeReach)
                {
                    // Before selection, a tooltip cannot choose an attacker's native melee mode.
                    magicParameters.rangeLabel.Text = Gui.Localize("Feature/&EnemyBattleMeleeReach");
                }
            }
        }
    }

    private static string GetEnemyBattleImmediateRoundDuration(EffectDescription effect)
    {
        if (effect.DurationType != DurationType.Round || effect.DurationParameter != 0)
        {
            return null;
        }
        var key = effect.EndOfEffect switch
        {
            TurnOccurenceType.StartOfTurn => "Rules/&DurationUntilStartOfTargetTurnFormat",
            TurnOccurenceType.EndOfTurn => "Rules/&DurationUntilEndOfTargetTurnFormat",
            TurnOccurenceType.EndOfSourceTurn => "Rules/&DurationUntilEndOfSourceTurnFormat",
            (TurnOccurenceType)ExtraTurnOccurenceType.StartOfSourceTurn => "Rules/&DurationUntilStartOfSourceTurnFormat",
            _ => null
        };
        return key == null ? null : Gui.Localize(key);
    }

    private static List<EnemyBattleAffix> SelectEnemyBattleAffixes(GameLocationCharacter character)
    {
        var monster = (RulesetCharacterMonster)character.RulesetCharacter;
        var cr = GetEnemyBattleChallengeRating(monster);
        var budget = cr < 1 && !HasEnemyBattleMinorCapabilityRoll(character.Guid, cr) ? 0 : GetEnemyBattleSlotBudget(monster);

        if (budget == 0)
        {
            return [];
        }

        var candidates = GetEnemyBattleCandidates(character, out var profile);
        return ChooseEnemyBattleAffixes(character, profile, candidates, budget);
    }

    private static List<EnemyBattleAffix> GetEnemyBattleEffectiveSelection(GameLocationCharacter character)
    {
        if (character?.RulesetCharacter is not RulesetCharacterMonster monster ||
            FindEnemyBattleCondition(monster, _enemyBattleLedger) == null)
        {
            return [];
        }
        var candidates = GetEnemyBattleCandidates(character, out var profile);
        candidates.RemoveAll(kind =>
        {
            var bank = EnemyBattleLedgerBanks[(int)kind / EnemyBattleSpentShift];
            var saved = FindEnemyBattleCondition(monster, bank.Selection);
            return saved == null || (saved.Amount & (1 << ((int)kind % EnemyBattleSpentShift))) == 0;
        });
        // Saved selection and consumption remain intact; only the legal effective subset is granted.
        return ChooseEnemyBattleAffixes(character, profile, candidates, GetEnemyBattleSlotBudget(monster));
    }

    private static List<FeatureDefinition> GetEnemyBattleNativeFeatures(RulesetCharacterMonster monster)
    {
        var native = new HashSet<FeatureDefinition>(monster.MonsterDefinition.Features.SelectMany(GetEnemyBattleNestedFeatures));
        return monster.FeaturesByType<FeatureDefinition>()
            .Where(feature => !EnemyBattleGrantedFeatures.Contains(feature) || native.Contains(feature)).ToList();
    }

    // Body identity and ordinary grants use the normal collections. Native getters
    // expose separate legendary action data only while that action is executing.
    private static List<RulesetAttackMode> GetEnemyBattleOrdinaryAttackModes(RulesetCharacter actor) =>
        actor == null ? [] : (List<RulesetAttackMode>)EnemyBattleOrdinaryAttackModesField.GetValue(actor);

    private static Dictionary<int, int> GetEnemyBattleOrdinaryMoveModes(RulesetCharacter actor) =>
        actor == null ? [] : (Dictionary<int, int>)EnemyBattleOrdinaryMoveModesField.GetValue(actor);

    private static List<RulesetSpellRepertoire> GetEnemyBattleOrdinarySpellRepertoires(RulesetCharacter actor) =>
        actor == null ? [] : (List<RulesetSpellRepertoire>)EnemyBattleOrdinarySpellRepertoiresField.GetValue(actor);

    private static RulesetUsablePower GetEnemyBattleUsablePower(FeatureDefinitionPower power, RulesetCharacter actor) =>
        AddUsablePowersFromCondition.GetGrantedUsablePowers(actor).FirstOrDefault(usable => usable.PowerDefinition == power) ??
        PowerProvider.Get(power, actor);

    // Facts belong to one synchronous eligibility evaluation. They never survive a
    // native refresh, an action dispatch or a yielded reaction.
    private sealed class EnemyBattleNativeFacts(RulesetCharacter actor)
    {
        private List<FeatureDefinition> _features;
        private FeatureDefinitionPower[] _candidatePowers;
        private FeatureDefinitionPower[] _currentPowers;
        private SpellDefinition[] _spells;
        private RulesetAttackMode[] _attackModes;
        private EffectDescription[] _candidateEffects;
        private EffectDescription[] _nativeEffects;
        private CombatAiRole? _nativeCombatRole;
        private readonly Dictionary<EnemyBattleAffix, EnemyBattleRangedSource> _rangedSources = [];

        internal RulesetCharacter Actor { get; } = actor;
        internal List<FeatureDefinition> Features => _features ??= Actor is RulesetCharacterMonster monster
            ? GetEnemyBattleNativeFeatures(monster) : Actor?.FeaturesByType<FeatureDefinition>().ToList() ?? [];
        internal FeatureDefinitionPower[] CandidatePowers => _candidatePowers ??= Features.OfType<FeatureDefinitionPower>()
            .Concat(AddUsablePowersFromCondition.GetGrantedUsablePowers(Actor).Select(item => item.PowerDefinition))
            .Where(power => !EnemyBattlePowerKinds.ContainsKey(power)).Distinct().ToArray();
        internal FeatureDefinitionPower[] CurrentPowers => _currentPowers ??= Actor == null ? [] :
            Actor.FeaturesByType<FeatureDefinitionPower>()
                .Concat(AddUsablePowersFromCondition.GetGrantedUsablePowers(Actor).Select(item => item.PowerDefinition))
                .Where(power => !EnemyBattlePowerKinds.ContainsKey(power)).Distinct().ToArray();
        internal SpellDefinition[] KnownSpells => _spells ??= Actor == null ? [] : GetEnemyBattleKnownSpells(Actor).ToArray();
        internal RulesetAttackMode[] AttackModes => _attackModes ??=
            GetEnemyBattleOrdinaryAttackModes(Actor).Where(mode => !TryGetEnemyBattleNativeAttackKind(mode, out _)).ToArray();
        internal CombatAiRole NativeCombatRole
        {
            get
            {
                _nativeCombatRole ??= GetEnemyBattleNativeCombatRole(this);
                return _nativeCombatRole.Value;
            }
        }
        internal bool IsMeleeCombatant => NativeCombatRole is CombatAiRole.Melee or CombatAiRole.Hybrid &&
            (AttackModes.Any(IsEnemyBattleNativeMeleeAttack) || KnownSpells.Any(spell =>
                spell.SpellLevel == 0 && spell.ActivationTime == ActivationTime.Action && IsEnemyBattleMeleeCantrip(spell)));
        internal bool IsPhysicalCombatant => NativeCombatRole is CombatAiRole.Melee or CombatAiRole.Ranged or CombatAiRole.Hybrid &&
            AttackModes.Any(mode => mode.ActionType == ActionType.Main && mode.EffectDescription.HasDamageForm());

        // Candidate and native queries historically use distinct feature sets and
        // effect orders. Keep both contracts while sharing the underlying snapshot.
        internal EffectDescription[] CandidateEffects => _candidateEffects ??= CandidatePowers.Select(power => power.EffectDescription)
            .Concat(KnownSpells.Select(spell => spell.EffectDescription))
            .Concat(AttackModes.Select(mode => mode.EffectDescription)).ToArray();
        internal EffectDescription[] NativeEffects => _nativeEffects ??= AttackModes.Select(mode => mode.EffectDescription)
            .Concat(CurrentPowers.Select(power => power.EffectDescription))
            .Concat(KnownSpells.Where(spell => spell != _enemyBattleFlightSpell).Select(spell => spell.EffectDescription)).ToArray();

        internal bool HasDamage(string damage) => NativeEffects.Any(effect => effect.EffectForms.Any(form =>
            form.FormType == EffectForm.EffectFormType.Damage && form.DamageForm.DamageType == damage));

        // Read the effects this creature can cause. Conditions it has suffered,
        // immunities and a high ability score are not an offensive mental gift.
        internal bool HasMentalInfluence => HasDamage(DamageTypePsychic) || NativeEffects.Any(effect =>
            HasEnemyBattleAddedCondition(effect, ConditionDefinitions.ConditionCharmed) ||
            HasEnemyBattleAddedCondition(effect, ConditionDefinitions.ConditionFrightened) ||
            HasEnemyBattleAddedCondition(effect, ConditionDefinitions.ConditionConfused) ||
            HasEnemyBattleAddedCondition(effect, GetDefinition<ConditionDefinition>("ConditionMocked")) ||
            HasEnemyBattleAddedCondition(effect, ConditionDefinitions.ConditionMindDominatedByCaster));

        internal bool HasLifeDrain => NativeEffects.Any(effect => effect.EffectForms.Any(form =>
                form.FormType == EffectForm.EffectFormType.Damage && form.DamageForm.DamageType == DamageTypeNecrotic &&
                form.DamageForm.HealFromInflictedDamage != HealFromInflictedDamage.Never) ||
            effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                form.DamageForm.DamageType == DamageTypeNecrotic) &&
            (effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Healing) ||
             HasEnemyBattleAddedCondition(effect, ConditionDefinitions.ConditionLifeDrained)));

        internal bool HasRadiantSmite => Actor.FeaturesByType<FeatureDefinitionAdditionalDamage>().Any(damage =>
            damage.SpecificDamageType == DamageTypeRadiant &&
            (damage.TriggerCondition == AdditionalDamageTriggerCondition.SpendSpellSlot || damage.AttackModeOnly)) ||
        KnownSpells.Any(spell =>
            (spell.ActivationTime is ActivationTime.OnAttackHit or ActivationTime.OnAttackHitMeleeAuto or ActivationTime.OnAttackHitAuto &&
             spell.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                 form.DamageForm.DamageType == DamageTypeRadiant)) ||
            (spell.ActivationTime is ActivationTime.BonusAction or ActivationTime.OnAttackHit or
                 ActivationTime.OnAttackHitMeleeAuto or ActivationTime.OnAttackHitAuto &&
             GetEnemyBattleAddedConditionFeatures(spell.EffectDescription).OfType<FeatureDefinitionAdditionalDamage>()
                 .Any(damage => damage.SpecificDamageType == DamageTypeRadiant)));

        internal EnemyBattleRangedSource GetRangedSource(EnemyBattleAffix kind)
        {
            if (!_rangedSources.TryGetValue(kind, out var source))
            {
                source = GetEnemyBattleRangedSource(Actor, kind);
                _rangedSources.Add(kind, source);
            }
            return source;
        }
    }

    private static bool IsEnemyBattleNativeMeleeAttack(RulesetAttackMode mode) =>
        mode is { ActionType: ActionType.Main, Ranged: false, Thrown: false } &&
        !IsRangedAttackMode(mode) && !TryGetEnemyBattleNativeAttackKind(mode, out _);

    private static CombatAiRole GetEnemyBattleNativeCombatRole(EnemyBattleNativeFacts facts)
    {
        var actor = facts.Actor;
        var mainAttacks = facts.AttackModes.Where(mode => mode.ActionType == ActionType.Main &&
            mode.SourceDefinition is not MonsterAttackDefinition { LimitedUse: true }).ToArray();
        var melee = mainAttacks.Any(IsEnemyBattleNativeMeleeAttack);
        var ranged = mainAttacks.Any(IsRangedAttackMode);
        var casting = HasSpellcasting(actor);
        var package = (actor as RulesetCharacterMonster)?.MonsterDefinition.DefaultBattleDecisionPackage;

        // Use the same native decision-package categories as the tactical role
        // profile. Their preferred action still requires a real native source.
        // Native martial packages also preserve melee spellcasters; a backup
        // dagger or touch cantrip cannot turn a caster package into a frontliner.
        if (melee && (package == DefaultMeleeWithBackupRangeDecisions ||
                      package == FighterCombatDecisions || package == PaladinCombatDecisions))
        {
            return CombatAiRole.Melee;
        }
        if (ranged && package == DefaultRangeWithBackupMeleeDecisions)
        {
            return CombatAiRole.Ranged;
        }
        if ((melee || ranged) && package == RogueCombatDecisions)
        {
            return CombatAiRole.Hybrid;
        }
        if (casting && (package == DefaultSupportCasterWithBackupAttacksDecisions || package == ClericCombatDecisions))
        {
            return CombatAiRole.SupportCaster;
        }
        if (casting && (package == OffensiveCasterCombatDecisions || package == CasterCombatDecisions))
        {
            return CombatAiRole.OffensiveCaster;
        }

        // Unknown/custom packages keep a source-based fallback. Compare complete
        // native at-will routines, including attack counts and cantrip scaling,
        // instead of inferring a role from an ability score or an arbitrary cutoff.
        // Granted tactical attacks and free spells never establish this identity.
        var meleeDamage = mainAttacks.Where(IsEnemyBattleNativeMeleeAttack).Select(mode =>
            GetEnemyBattleEffectAverageDamage(mode.EffectDescription) * Math.Max(1, mode.AttacksNumber)).DefaultIfEmpty().Max();
        var rangedDamage = mainAttacks.Where(IsRangedAttackMode).Select(mode =>
            GetEnemyBattleEffectAverageDamage(mode.EffectDescription) * Math.Max(1, mode.AttacksNumber)).DefaultIfEmpty().Max();
        var magicDamage = GetEnemyBattleOrdinarySpellRepertoires(actor).Where(repertoire => !IsEnemyBattleGrantedSpellRepertoire(repertoire))
            .SelectMany(repertoire => GetEnemyBattleKnownSpells(repertoire)
                .Where(spell => spell.SpellLevel == 0 && spell.ActivationTime == ActivationTime.Action)
                .Select(spell => GetEnemyBattleEffectAverageDamage(spell.EffectDescription, repertoire.SpellCastingLevel)))
            .Concat(facts.CandidatePowers.Where(power => power.ActivationTime == ActivationTime.Action &&
                    power.RechargeRate == RechargeRate.AtWill)
                .Select(power => GetEnemyBattleEffectAverageDamage(power.EffectDescription))).DefaultIfEmpty().Max();
        if (casting && magicDamage > Math.Max(meleeDamage, rangedDamage))
        {
            return CombatAiRole.OffensiveCaster;
        }
        return (melee, ranged) switch
        {
            (true, false) => CombatAiRole.Melee,
            (false, true) => CombatAiRole.Ranged,
            (true, true) when meleeDamage > rangedDamage => CombatAiRole.Melee,
            (true, true) when rangedDamage > meleeDamage => CombatAiRole.Ranged,
            (true, true) => CombatAiRole.Hybrid,
            _ => casting ? CombatAiRole.OffensiveCaster : CombatAiRole.Hybrid
        };
    }

    private static List<EnemyBattleAffix> GetEnemyBattleCandidates(GameLocationCharacter character,
        out CombatAiProfile profile)
    {
        var monster = (RulesetCharacterMonster)character.RulesetCharacter;
        var cr = GetEnemyBattleChallengeRating(monster);
        var facts = new EnemyBattleNativeFacts(monster);
        profile = BuildEnemyBattleProfileFromFacts(character, facts);
        var features = facts.Features;
        var powers = facts.CandidatePowers;
        var spells = facts.KnownSpells;
        var effects = facts.CandidateEffects;
        var candidates = new List<EnemyBattleAffix>();
        var intelligent = monster.TryGetAttributeValue(AttributeDefinitions.Intelligence) >= 8;
        var trained = intelligent && profile.Family is CombatAiFamily.Humanoid or CombatAiFamily.Giant or
            CombatAiFamily.Celestial or CombatAiFamily.Undead or CombatAiFamily.Fey or CombatAiFamily.Fiend;
        var hasMelee = facts.AttackModes.Any(mode => !mode.Ranged && mode.ActionType == ActionType.Main);
        var hasWeapon = facts.AttackModes.Any(IsEnemyBattleWeaponAttack);
        var hasMeleeWeapon = facts.AttackModes.Any(mode => !mode.Ranged && IsEnemyBattleWeaponAttack(mode));
        var caster = profile.HasSpellcasting && intelligent;
        var hasDefensiveReaction = HasExistingEnemyBattleDefense(powers);

        if (!hasDefensiveReaction && !effects.Any(effect => effect.TargetSide == Side.Ally &&
                GetEnemyBattleAddedConditionFeatures(effect).OfType<FeatureDefinitionAttributeModifier>()
                    .Any(modifier => modifier.ModifiedAttribute == AttributeDefinitions.ArmorClass)))
        {
            candidates.Add(EnemyBattleAffix.GuardedStance);
        }
        if (profile.Family is not (CombatAiFamily.Undead or CombatAiFamily.Construct) &&
            !features.OfType<FeatureDefinitionHealingModifier>().Any(modifier => modifier.CannotGainHitPoints) &&
            !effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Healing)))
        {
            candidates.Add(EnemyBattleAffix.SecondWind);
        }
        if (intelligent && !effects.Any(effect => effect.TargetSide == Side.Ally && effect.TargetType != TargetType.Self &&
                effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.TemporaryHitPoints)))
        {
            candidates.Add(EnemyBattleAffix.Rally);
        }
        if (intelligent && !effects.Any(effect => effect.TargetSide == Side.Ally && effect.TargetType != TargetType.Self &&
                GetEnemyBattleAddedConditionFeatures(effect).OfType<FeatureDefinitionSavingThrowAffinity>().Any()))
        {
            candidates.Add(EnemyBattleAffix.SteadyingWord);
        }
        if (facts.AttackModes.Any(mode => mode.ActionType == ActionType.Main &&
                !TryGetEnemyBattleNativeAttackKind(mode, out _)) && !hasWeapon)
        {
            candidates.Add(EnemyBattleAffix.PressingAttack);
        }
        if (profile.Family is not (CombatAiFamily.Ooze or CombatAiFamily.Plant or CombatAiFamily.Construct) &&
            !effects.Any(effect => GetEnemyBattleAddedConditionFeatures(effect).OfType<FeatureDefinitionCombatAffinity>()
                .Any(affinity => affinity.MyAttackAdvantage == AdvantageType.Disadvantage)))
        {
            candidates.Add(EnemyBattleAffix.UnsettlingCry);
        }

        if (HasEnemyBattleAiCantrip(monster))
        {
            candidates.Add(EnemyBattleAffix.QuickenedCantrip);
        }
        if (HasEnemyBattleSpellFlightSource(monster))
        {
            candidates.Add(EnemyBattleAffix.SpellFlight);
        }

        if (caster && HasEnemyBattleThirdLevelSpells(monster) &&
            !HasEnemyBattleSpell(monster, Counterspell) && !powers.Any(IsEnemyBattleCounterPower))
        {
            candidates.Add(EnemyBattleAffix.Spellbreaker);
        }

        if (caster && HasEnemyBattleSpellLevel(monster, 1) &&
            !HasEnemyBattleSpell(monster, Shield) && !hasDefensiveReaction)
        {
            candidates.Add(EnemyBattleAffix.ArcaneWard);
        }

        if (profile.PrefersDistance &&
            monster.TryGetAttributeValue(AttributeDefinitions.Dexterity) >= 12 &&
            profile.Family is CombatAiFamily.Humanoid or CombatAiFamily.Fey or CombatAiFamily.Fiend or CombatAiFamily.Undead &&
            !HasEnemyBattleBonusAction(features, powers, Id.DisengageBonus, ConditionDefinitions.ConditionDisengaging))
        {
            candidates.Add(EnemyBattleAffix.Withdrawal);
        }

        if (trained && hasMeleeWeapon && !hasDefensiveReaction)
        {
            candidates.Add(EnemyBattleAffix.Parry);
        }

        if (hasMelee && !hasWeapon && cr >= 3 &&
            profile.Family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity or CombatAiFamily.Dragon or
                CombatAiFamily.Fiend or CombatAiFamily.Aberration or CombatAiFamily.Undead or CombatAiFamily.Fey or
                CombatAiFamily.Plant &&
            !HasExistingEnemyBattleReprisal(features, powers, spells))
        {
            candidates.Add(EnemyBattleAffix.Reprisal);
        }

        if ((trained && intelligent && hasWeapon || profile.Family == CombatAiFamily.Construct && hasMelee) && cr >= 4 &&
            (monster.TryGetAttributeValue(AttributeDefinitions.ArmorClass) >= 16 ||
             profile.Family == CombatAiFamily.Celestial) &&
            !HasExistingEnemyBattleReprisal(features, powers, spells))
        {
            candidates.Add(EnemyBattleAffix.Bodyguard);
        }

        if (hasMelee && profile.PrefersAggressivePursuit &&
            profile.Family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity or CombatAiFamily.Dragon or
                 CombatAiFamily.Giant or CombatAiFamily.Elemental or CombatAiFamily.Fey or CombatAiFamily.Undead or
                 CombatAiFamily.Plant &&
            !HasEnemyBattleBonusAction(features, powers, Id.DashBonus,
                ConditionDefinitions.ConditionDashing, ConditionDefinitions.ConditionDashingBonus))
        {
            candidates.Add(EnemyBattleAffix.HuntingRush);
        }

        var immuneFear = HasEnemyBattleConditionImmunity(features, ConditionDefinitions.ConditionFrightened);
        if (cr >= 3 && !immuneFear &&
            (profile.Family is CombatAiFamily.Undead or CombatAiFamily.Construct or CombatAiFamily.Giant ||
             profile.Temperament == CombatAiTemperament.Disciplined))
        {
            candidates.Add(EnemyBattleAffix.Resolve);
        }

        if (trained && hasWeapon &&
            !HasEnemyBattleAttackAdvantage(features, effects, Side.Ally))
        {
            candidates.Add(EnemyBattleAffix.Feint);
        }

        if (cr >= 3 && monster.TryGetAttributeValue(AttributeDefinitions.Charisma) >= 12 &&
            profile.Family is CombatAiFamily.Dragon or CombatAiFamily.Fiend or CombatAiFamily.Undead or
                CombatAiFamily.Aberration or CombatAiFamily.Monstrosity &&
            !effects.Any(effect => HasEnemyBattleAddedCondition(effect, ConditionDefinitions.ConditionFrightened)))
        {
            candidates.Add(EnemyBattleAffix.Dread);
        }

        if (cr >= 4 && trained && hasWeapon &&
            monster.TryGetAttributeValue(AttributeDefinitions.Intelligence) >= 12 &&
            monster.TryGetAttributeValue(AttributeDefinitions.Charisma) >= 12 &&
            !HasEnemyBattleAttackAdvantage([], effects, Side.Ally, requireOtherTarget: true))
        {
            candidates.Add(EnemyBattleAffix.Command);
        }

        if (cr >= 3 && (profile.Family == CombatAiFamily.Celestial || caster &&
                GetEnemyBattleOrdinarySpellRepertoires(monster).Any(repertoire => !IsEnemyBattleGrantedSpellRepertoire(repertoire) && repertoire.SpellCastingAbility == AttributeDefinitions.Wisdom)) &&
            !effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Healing)))
        {
            candidates.Add(EnemyBattleAffix.Restoration);
        }

        if (cr >= 3 && GetEnemyBattleNativeElement(monster) != null && GetEnemyBattleDamageDiceNumber(monster) > 0 &&
            profile.Family is CombatAiFamily.Elemental or CombatAiFamily.Dragon or CombatAiFamily.Fiend or
                CombatAiFamily.Monstrosity or CombatAiFamily.Undead or CombatAiFamily.Aberration or CombatAiFamily.Celestial &&
            !HasExistingEnemyBattleReprisal(features, powers, spells))
        {
            candidates.Add(EnemyBattleAffix.ElementalRebuke);
        }

        if (caster && cr >= 5 && HasEnemyBattleSpellLevel(monster, 2) && !features.OfType<FeatureDefinitionMagicAffinity>()
                .Any(affinity => affinity.ConcentrationAffinity == ConcentrationAffinity.Disadvantage) &&
            !effects.Any(effect => effect.TargetSide == Side.Enemy &&
                GetEnemyBattleAddedConditionFeatures(effect).OfType<FeatureDefinitionMagicAffinity>()
                    .Any(affinity => affinity.ConcentrationAffinity == ConcentrationAffinity.Disadvantage)))
        {
            candidates.Add(EnemyBattleAffix.ArcaneHindrance);
        }

        if ((caster && HasEnemyBattleSpellLevel(monster, 2) || profile.Family == CombatAiFamily.Fey && intelligent) &&
            cr >= 3 && !effects.Any(effect => effect.EffectForms.Any(form =>
                form.FormType == EffectForm.EffectFormType.Motion &&
                form.MotionForm.Type == MotionForm.MotionType.TeleportToDestination)))
        {
            candidates.Add(EnemyBattleAffix.PhaseStep);
        }

        var strongWarrior = (trained || profile.Family == CombatAiFamily.Giant) && hasMeleeWeapon &&
                            monster.TryGetAttributeValue(AttributeDefinitions.Strength) >= 16 &&
                            monster.TryGetAttributeValue(AttributeDefinitions.Strength) >
                            monster.TryGetAttributeValue(AttributeDefinitions.Dexterity);
        if (strongWarrior && !features.OfType<FeatureDefinitionActionAffinity>().Any(affinity =>
                affinity.AuthorizedActions.Any(action => action is Id.ShoveBonus or Id.ShoveFree)) &&
            !powers.Any(power => power.ActivationTime == ActivationTime.BonusAction &&
                power.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Motion &&
                    form.MotionForm.Type is MotionForm.MotionType.PushFromOrigin or MotionForm.MotionType.DragToOrigin)))
        {
            candidates.Add(EnemyBattleAffix.DrivingShove);
        }

        var naturalMelee = facts.AttackModes.Any(mode => mode.ActionType == ActionType.Main && !mode.Ranged &&
            !IsEnemyBattleWeaponAttack(mode));
        var family = profile.Family;
        void AddIdentity(EnemyBattleAffix kind, bool eligible)
        {
            if (eligible)
            {
                candidates.Add(kind);
            }
        }
        bool HasCondition(ConditionDefinition condition) => effects.Any(effect => HasEnemyBattleAddedCondition(effect, condition));
        var nativeConditions = effects.SelectMany(GetEnemyBattleAddedConditionFeatures).ToArray();
        var nativeSlow = nativeConditions.OfType<FeatureDefinitionMovementAffinity>()
            .Any(affinity => affinity.BaseSpeedMultiplicativeModifier < 1);
        var nativeReactionDenial = nativeConditions.OfType<FeatureDefinitionActionAffinity>()
            .Any(affinity => !affinity.AllowedActionTypes[(int)ActionType.Reaction]);
        var nativeBlind = HasCondition(ConditionDefinitions.ConditionBlinded);
        var nativePoison = HasCondition(ConditionDefinitions.ConditionPoisoned);
        var nativeProne = effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Motion &&
            form.MotionForm.Type == MotionForm.MotionType.FallProne));
        var nativePush = effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Motion &&
            form.MotionForm.Type is MotionForm.MotionType.PushFromOrigin or MotionForm.MotionType.DragToOrigin));
        var nativeInvisible = HasCondition(ConditionDefinitions.ConditionInvisibleBase) ||
            nativeConditions.OfType<FeatureDefinitionCombatAffinity>().Any(affinity =>
                affinity.AttackOnMeAdvantage == AdvantageType.Disadvantage);
        var psychic = facts.HasDamage(DamageTypePsychic);
        var poisonous = effects.Any(effect => effect.EffectForms.Any(form =>
            form.FormType == EffectForm.EffectFormType.Damage && form.DamageForm.DamageType == DamageTypePoison));
        var cold = facts.HasDamage(DamageTypeCold);
        var fire = facts.HasDamage(DamageTypeFire);
        var necrotic = facts.HasDamage(DamageTypeNecrotic);
        var hasFlight = profile.HasFlight;
        var ethereal = family == CombatAiFamily.Undead && necrotic &&
                       HasEnemyBattleConditionImmunity(features, ConditionDefinitions.ConditionProne) &&
                       HasEnemyBattleConditionImmunity(features, ConditionDefinitions.ConditionRestrained);
        var heavy = monster.TryGetAttributeValue(AttributeDefinitions.Strength) >= 18 &&
                    monster.SizeDefinition.WieldingSize >= GetDefinition<CharacterSizeDefinition>("Large").WieldingSize;

        AddIdentity(EnemyBattleAffix.PackCall, naturalMelee && family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity &&
            !HasEnemyBattleAttackAdvantage(features, effects, Side.Ally));
        AddIdentity(EnemyBattleAffix.PredatoryLunge, naturalMelee && !ethereal && cr >= 3 &&
            family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity or CombatAiFamily.Fiend or CombatAiFamily.Undead &&
            !facts.AttackModes.Any(mode => mode.ActionType == ActionType.Bonus) &&
            !features.OfType<FeatureDefinitionAdditionalAction>().Any() &&
            !features.OfType<FeatureDefinitionActionAffinity>().Any(affinity => affinity.AuthorizedActions.Contains(Id.AttackOff)));
        AddIdentity(EnemyBattleAffix.FeyVeil, !nativeInvisible && cr >= 3 &&
            (family == CombatAiFamily.Fey || ethereal));
        AddIdentity(EnemyBattleAffix.FiendMiasma, !nativePoison && cr >= 3 &&
            (family == CombatAiFamily.Fiend && (poisonous ||
                 monster.MonsterDefinition.Alignment == "ChaoticEvil" && facts.HasDamage(DamageTypePoison)) ||
             family == CombatAiFamily.Plant && poisonous));
        AddIdentity(EnemyBattleAffix.ThoughtLock, family == CombatAiFamily.Aberration && !nativeReactionDenial && cr >= 3 &&
            (monster.TryGetAttributeValue(AttributeDefinitions.Intelligence) >= 12 || psychic));
        AddIdentity(EnemyBattleAffix.ConstructBrace, family == CombatAiFamily.Construct &&
            monster.TryGetAttributeValue(AttributeDefinitions.ArmorClass) >= 16 &&
            !HasCondition(ConditionDefinitions.ConditionDodging));
        AddIdentity(EnemyBattleAffix.WingBuffet, hasFlight && HasEnemyBattleWingedBody(monster) && !nativePush && !nativeProne);
        AddIdentity(EnemyBattleAffix.PrimordialRepulsion, family == CombatAiFamily.Elemental && hasFlight && !nativePush);
        AddIdentity(EnemyBattleAffix.ViscousSnare, !nativeSlow && GetEnemyBattleNativeMeleeSlashingMode(monster) != null);
        AddIdentity(EnemyBattleAffix.PiercingJab, GetEnemyBattleNativeMeleeDamageMode(monster, DamageTypePiercing) != null &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.PiercingJab) > 0);
        AddIdentity(EnemyBattleAffix.RootSnare, cr >= 3 && !HasCondition(ConditionDefinitions.ConditionRestrained) &&
            !nativeConditions.OfType<FeatureDefinitionMovementAffinity>().Any(affinity => affinity.BaseSpeedMultiplicativeModifier <= 0) &&
            (family == CombatAiFamily.Plant || family == CombatAiFamily.Fey &&
                GetEnemyBattleOrdinarySpellRepertoires(monster).Any(repertoire => !IsEnemyBattleGrantedSpellRepertoire(repertoire) && repertoire.SpellCastingAbility == AttributeDefinitions.Wisdom)));
        AddIdentity(EnemyBattleAffix.WitheringMark, family == CombatAiFamily.Undead && (necrotic || cold) && cr >= 3 &&
            !nativeConditions.OfType<FeatureDefinitionHealingModifier>().Any(modifier => modifier.CannotGainHitPoints));
        AddIdentity(EnemyBattleAffix.PurifyingTouch, family == CombatAiFamily.Celestial && cr >= 3 &&
            !effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Condition &&
                form.ConditionForm.Operation == ConditionForm.ConditionOperation.RemoveDetrimentalAll)));
        AddIdentity(EnemyBattleAffix.PsychicBacklash, family == CombatAiFamily.Aberration && cr >= 3 &&
            (psychic || monster.TryGetAttributeValue(AttributeDefinitions.Intelligence) >= 12) &&
            !HasExistingEnemyBattleReprisal(features, powers, spells));
        AddIdentity(EnemyBattleAffix.BlindingEmbers, family == CombatAiFamily.Elemental && fire && !nativeBlind && cr >= 3);
        AddIdentity(EnemyBattleAffix.StoneTremor, !nativeProne && heavy && !hasFlight &&
            family is CombatAiFamily.Giant or CombatAiFamily.Construct or CombatAiFamily.Elemental);
        AddIdentity(EnemyBattleAffix.CausticRetort, family == CombatAiFamily.Ooze && !nativeSlow &&
            !HasExistingEnemyBattleReprisal(features, powers, spells));
        AddIdentity(EnemyBattleAffix.LightningSurge, cr >= 3 && family != CombatAiFamily.Humanoid &&
            !nativeReactionDenial && (facts.HasDamage(DamageTypeLightning) ||
                effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                    form.DamageForm.DamageType == DamageTypeLightning))));
        var hasNativeRetaliation = HasExistingEnemyBattleReprisal(features, powers, spells);
        AddIdentity(EnemyBattleAffix.ThunderousRoar, cr >= 3 && naturalMelee && !ethereal &&
            !hasNativeRetaliation && GetEnemyBattleDamageDiceNumber(monster) > 0 &&
            (family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity or CombatAiFamily.Dragon or
                 CombatAiFamily.Giant or CombatAiFamily.Fiend or CombatAiFamily.Celestial ||
             intelligent && family is CombatAiFamily.Undead or CombatAiFamily.Humanoid ||
             effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                 form.DamageForm.DamageType == DamageTypeThunder))));
        AddIdentity(EnemyBattleAffix.ForcePulse, cr >= 3 && !hasNativeRetaliation &&
            GetEnemyBattleDamageDiceNumber(monster) > 0 &&
            (facts.HasDamage(DamageTypeForce) ||
             effects.Any(effect => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                 form.DamageForm.DamageType == DamageTypeForce)) ||
             caster && cr >= 5 && HasEnemyBattleSpellLevel(monster, 2)));
        var nativeBonusRanged = HasEnemyBattleNativeBonusRanged(monster);
        AddIdentity(EnemyBattleAffix.FocusedVolley, !nativeBonusRanged &&
            facts.GetRangedSource(EnemyBattleAffix.FocusedVolley) != null &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.FocusedVolley) > 0);
        AddIdentity(EnemyBattleAffix.ElementalLance, !nativeBonusRanged && cr >= 5 &&
            facts.GetRangedSource(EnemyBattleAffix.ElementalLance) != null &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.ElementalLance) > 0);
        AddIdentity(EnemyBattleAffix.RadiantBolt, !HasEnemyBattleNativeBonusRanged(monster, EnemyBattleAffix.RadiantBolt) &&
            facts.GetRangedSource(EnemyBattleAffix.RadiantBolt) != null);
        AddIdentity(EnemyBattleAffix.ArcaneRetort, !hasNativeRetaliation &&
            !features.OfType<FeatureDefinitionReduceDamage>().Any());
        AddIdentity(EnemyBattleAffix.ImprovisedThrow, IsEnemyBattleImprovisedThrowBody(monster) &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.ImprovisedThrow) > 0);
        AddIdentity(EnemyBattleAffix.GraveLeech,
            family is CombatAiFamily.Undead or CombatAiFamily.Fiend or CombatAiFamily.Aberration &&
            facts.HasDamage(DamageTypeNecrotic) &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.GraveLeech) > 0 &&
            !facts.HasLifeDrain);
        AddIdentity(EnemyBattleAffix.RadiantSmite, hasMelee &&
            (family == CombatAiFamily.Celestial || facts.HasDamage(DamageTypeRadiant)) &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.RadiantSmite) > 0 &&
            !facts.HasRadiantSmite);

        AddIdentity(EnemyBattleAffix.LegendaryResolve, !HasEnemyBattleNativeLegendaryResistance(monster));
        AddIdentity(EnemyBattleAffix.HeroicRally, intelligent &&
            monster.TryGetAttributeValue(AttributeDefinitions.Charisma) >= 12 &&
            family is not (CombatAiFamily.Ooze or CombatAiFamily.Plant or CombatAiFamily.Construct) &&
            !effects.Any(effect => effect.TargetSide == Side.Ally && effect.TargetType != TargetType.Self &&
                (effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.TemporaryHitPoints) ||
                 GetEnemyBattleAddedConditionFeatures(effect).OfType<FeatureDefinitionSavingThrowAffinity>().Any())));
        AddIdentity(EnemyBattleAffix.CrushingBlow,
            monster.TryGetAttributeValue(AttributeDefinitions.Strength) >= 16 && !nativeProne &&
            facts.AttackModes.Any(IsEnemyBattleCrushingAttack) &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.CrushingBlow) > 0 &&
            !HasEnemyBattleNativeBonusMeleeSmite(monster));
        AddIdentity(EnemyBattleAffix.LastingSurge, IsEnemyBattleLastingSurgeCompatible(monster));
        AddIdentity(EnemyBattleAffix.ChargingRush, IsEnemyBattleChargingBody(monster));
        AddIdentity(EnemyBattleAffix.WeaponCoating, IsEnemyBattleCoatingBody(monster));
        AddIdentity(EnemyBattleAffix.ArmorRend, IsEnemyBattleArmorRendBody(monster));
        AddIdentity(EnemyBattleAffix.SweepingVolley, facts.GetRangedSource(EnemyBattleAffix.SweepingVolley) != null &&
            GetEnemyBattleCapabilityDamageDiceNumber(monster, EnemyBattleAffix.SweepingVolley) > 0);
        AddIdentity(EnemyBattleAffix.CoordinatedAdvance, IsEnemyBattleCommandingBody(monster));

        AddIdentity(EnemyBattleAffix.ElementalAbsorption, caster && HasEnemyBattleSpellLevel(monster, 1) &&
            !HasEnemyBattleSpell(monster, SpellsContext.ElementalInfusion));
        AddIdentity(EnemyBattleAffix.ReachIntercept, (trained && hasMelee ||
            GetEnemyBattleActionCantrips(monster).Any(IsEnemyBattleMeleeCantrip)) &&
            !features.Any(feature => feature.GetAllSubFeaturesOfType<CanMakeAoOOnReachEntered>().Count != 0));

        // Fresh selection and saved grants use the same body and native-source contract.
        candidates.RemoveAll(kind => !IsEnemyBattleCapabilityCompatibleFromFacts(facts, kind) ||
            !HasEnemyBattlePotentialSupportRecipient(character, kind));
        return candidates;
    }

    private static bool HasEnemyBattlePotentialSupportRecipient(GameLocationCharacter source, EnemyBattleAffix kind)
    {
        var effect = EnemyBattleAffixes[kind].Power.EffectDescription;
        // These delegated actions protect or free another contender despite their self marker.
        var requiresAlly = kind is EnemyBattleAffix.Bodyguard or EnemyBattleAffix.DrivingShove or EnemyBattleAffix.PackCall;
        if (!requiresAlly && (effect.TargetSide != Side.Ally || effect.TargetType != TargetType.IndividualsUnique))
        {
            return true;
        }

        // Check the whole encounter rather than perception or temporary wounds/buffs.
        // A support grant needs a body that can benefit, even when the source fights alone.
        return ((_enemyTacticsBattle ?? Gui.Battle)?.EnemyContenders ?? []).Concat(new[] { source }).Distinct().Any(target =>
            target?.RulesetCharacter is { IsDeadOrDying: false, IsRemovedFromTheGame: false } &&
            PlayerControllerContext.GetBattleSide(target) == PlayerControllerContext.GetBattleSide(source) &&
            (!(effect.TargetExcludeCaster || requiresAlly) || target != source) &&
            IsEnemyBattleSupportBodyEligible(source, kind, target));
    }

    private static bool IsEnemyBattleSupportBodyEligible(GameLocationCharacter source, EnemyBattleAffix kind,
        GameLocationCharacter target)
    {
        var actor = target.RulesetCharacter;
        if (EnemyBattleAffixes[kind].Power.EffectDescription.ImmuneCreatureFamilies.Contains(actor.CharacterFamily))
        {
            return false;
        }
        if (kind == EnemyBattleAffix.Restoration)
        {
            return actor is not RulesetCharacterMonster body || !body.MonsterDefinition.Features
                .SelectMany(GetEnemyBattleNestedFeatures).OfType<FeatureDefinitionHealingModifier>()
                .Any(modifier => modifier.CannotGainHitPoints);
        }
        if (kind == EnemyBattleAffix.PackCall)
        {
            return GetFamily(source.RulesetCharacter) == GetFamily(actor) &&
                GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Main);
        }
        if (kind == EnemyBattleAffix.DrivingShove)
        {
            return BuildEnemyBattleProfile(target).PrefersDistance || HasSpellcasting(actor);
        }
        if (kind == EnemyBattleAffix.Command)
        {
            return GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Main) ||
                GetEnemyBattleKnownSpells(actor).Any(spell => spell.EffectDescription.RangeType is RangeType.MeleeHit or RangeType.RangeHit);
        }
        if (kind == EnemyBattleAffix.CoordinatedAdvance)
        {
            return GetEnemyBattleOrdinaryAttackModes(actor).Any(IsEnemyBattleCoordinatedAttackMode);
        }
        if (kind == EnemyBattleAffix.PurifyingTouch && actor is RulesetCharacterMonster monster)
        {
            var features = monster.MonsterDefinition.Features.SelectMany(GetEnemyBattleNestedFeatures).ToList();
            return new[] { ConditionDefinitions.ConditionCharmed, ConditionDefinitions.ConditionFrightened,
                    ConditionDefinitions.ConditionPoisoned }.Any(condition => !HasEnemyBattleConditionImmunity(features, condition));
        }
        return true;
    }

    private static List<EnemyBattleAffix> ChooseEnemyBattleAffixes(GameLocationCharacter character,
        CombatAiProfile profile, List<EnemyBattleAffix> candidates, int budget)
    {
        var monster = (RulesetCharacterMonster)character.RulesetCharacter;
        var caster = profile.HasSpellcasting;
        // Creature identity and combat role are preferences. No tier or identity ability is mandatory.
        var offense = candidates.Where(kind => EnemyBattleCatalog[kind].Role == EnemyBattleCapabilityRole.Offense).ToArray();
        var support = candidates.Where(kind => EnemyBattleCatalog[kind].Role == EnemyBattleCapabilityRole.Support).ToArray();
        var defense = candidates.Except(offense).Except(support).ToArray();
        var lanes = support.Any(kind => kind is EnemyBattleAffix.Command or EnemyBattleAffix.Restoration)
            ? new[] { support, offense, defense }
            : caster && support.Contains(EnemyBattleAffix.PhaseStep)
                ? new[] { offense, support, defense }
            : profile.PrefersDistance && support.Contains(EnemyBattleAffix.Withdrawal)
                ? new[] { support, offense, defense }
            : profile.Family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity or CombatAiFamily.Elemental or CombatAiFamily.Dragon
                ? new[] { offense, support, defense }
                : new[] { offense, defense, support };
        var selected = new List<EnemyBattleAffix>();
        var weights = candidates.ToDictionary(kind => kind, kind =>
        {
            var laneWeight = lanes.Length - Array.FindIndex(lanes, lane => lane.Contains(kind));
            var preferred = kind is EnemyBattleAffix.DrivingShove or EnemyBattleAffix.ElementalRebuke or
                                EnemyBattleAffix.ArcaneHindrance or EnemyBattleAffix.Feint or
                                EnemyBattleAffix.Command or EnemyBattleAffix.Restoration ||
                            kind == EnemyBattleAffix.Dread && monster.TryGetAttributeValue(AttributeDefinitions.Charisma) >= 16 ||
                            kind == EnemyBattleAffix.Resolve && profile.Family is CombatAiFamily.Undead or CombatAiFamily.Construct ||
                            kind == EnemyBattleAffix.Bodyguard && monster.TryGetAttributeValue(AttributeDefinitions.ArmorClass) >= 18;
            return laneWeight * (preferred || GetEnemyBattleIdentityPriority(kind, profile) == 0 ? 2 : 1);
        });
        var remainingSlots = budget;
        while (remainingSlots > 0)
        {
            var next = candidates.Where(kind => !selected.Contains(kind) &&
                GetEnemyBattleCapabilityTier(kind) <= remainingSlots &&
                !selected.Any(other => AreEnemyBattleCapabilitiesRedundant(kind, other)))
                .OrderBy(kind => GetEnemyBattleSelectionScore(character.Guid, kind,
                    weights[kind] * (selected.Any(IsEnemyBattleBonus) && !IsEnemyBattleBonus(kind) ? 2 : 1) *
                    (selected.Any(IsEnemyBattleControl) && !IsEnemyBattleControl(kind) ? 2 : 1)))
                .ThenBy(kind => (int)kind)
                .Select(kind => (EnemyBattleAffix?)kind).FirstOrDefault();
            if (!next.HasValue)
            {
                break;
            }
            selected.Add(next.Value);
            remainingSlots -= GetEnemyBattleCapabilityTier(next.Value);
        }

        return selected;
    }

    private static bool AreEnemyBattleCapabilitiesRedundant(EnemyBattleAffix first, EnemyBattleAffix second) =>
        first is EnemyBattleAffix.ChargingRush or EnemyBattleAffix.HuntingRush &&
        second is EnemyBattleAffix.ChargingRush or EnemyBattleAffix.HuntingRush;

    private static double GetEnemyBattleSelectionScore(ulong guid, EnemyBattleAffix kind, int weight)
    {
        // An exponential race gives every eligible candidate a positive chance,
        // while deterministic, separate salts preserve peer/reload consistency
        // and leave the low-CR grant probability's existing trial unchanged.
        const ulong salt = 0xA24BAED4963EE407UL;
        var uniform = ((MixEnemyBattleIdentity(guid ^ salt, kind) >> 11) + 1d) / 9007199254740992d;
        return -System.Math.Log(uniform) / weight;
    }

    private static bool IsEnemyBattleBonus(EnemyBattleAffix kind) =>
        GetEnemyBattleCapabilityMetadata(kind).ActivationTime == ActivationTime.BonusAction;

    private static bool HasEnemyBattleMinorCapabilityRoll(ulong guid, float challengeRating)
    {
        // Saved ledgers bypass selection entirely. A new low-CR actor receives one
        // fixed uniform trial, identical on peers and after an uninitialized reload.
        return challengeRating > 0 && (MixEnemyBattleIdentity(guid, EnemyBattleAffix.GraveLeech) >> 11) /
            9007199254740992d < challengeRating;
    }

    private static bool IsEnemyBattleControl(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.Control);

    private static int GetEnemyBattleIdentityPriority(EnemyBattleAffix kind, CombatAiProfile profile) => kind switch
    {
        EnemyBattleAffix.RootSnare when profile.Family == CombatAiFamily.Plant => 0,
        EnemyBattleAffix.ViscousSnare when profile.Family == CombatAiFamily.Ooze => 0,
        EnemyBattleAffix.CausticRetort or EnemyBattleAffix.PurifyingTouch or EnemyBattleAffix.WingBuffet or
            EnemyBattleAffix.BlindingEmbers or EnemyBattleAffix.ConstructBrace or EnemyBattleAffix.FeyVeil => 0,
        EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.PsychicBacklash or EnemyBattleAffix.FiendMiasma or
            EnemyBattleAffix.LightningSurge => 0,
        EnemyBattleAffix.ThunderousRoar or EnemyBattleAffix.ForcePulse => 0,
        EnemyBattleAffix.PredatoryLunge when profile.IsMeleeSpecialist => 0,
        _ => 1
    };

    private static ulong MixEnemyBattleIdentity(ulong guid, EnemyBattleAffix kind)
    {
        unchecked
        {
            var mixed = guid ^ (((ulong)kind + 1) * 0x9E3779B97F4A7C15UL);
            mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
            mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;
            return mixed ^ (mixed >> 31);
        }
    }

    private static IEnumerable<EffectDescription> GetEnemyBattleNativeEffects(RulesetCharacter actor) =>
        new EnemyBattleNativeFacts(actor).NativeEffects;

    private static bool HasEnemyBattleNativeDamage(RulesetCharacter actor, string damage) =>
        new EnemyBattleNativeFacts(actor).HasDamage(damage);

    private static bool HasEnemyBattleNativeLifeDrain(RulesetCharacter actor) =>
        new EnemyBattleNativeFacts(actor).HasLifeDrain;

    private static bool HasEnemyBattleNativeRadiantSmite(RulesetCharacter actor) =>
        new EnemyBattleNativeFacts(actor).HasRadiantSmite;

    private static bool HasEnemyBattleNativeBonusAttackSource(RulesetCharacter actor, RulesetAttackMode source) =>
        GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Bonus && !TryGetEnemyBattleNativeAttackKind(mode, out _) &&
            mode.SourceDefinition == source.SourceDefinition && mode.Ranged == source.Ranged);

    private static bool IsEnemyBattleImprovisedThrowBody(RulesetCharacter actor)
    {
        if (actor is not RulesetCharacterMonster monster ||
            !GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Main && !mode.Ranged &&
                !TryGetEnemyBattleNativeAttackKind(mode, out _) && mode.EffectDescription.EffectForms.Any(form =>
                    form.FormType == EffectForm.EffectFormType.Damage && form.DamageForm.DamageType is
                        DamageTypeBludgeoning or DamageTypePiercing or DamageTypeSlashing)) ||
            GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.Ranged && !TryGetEnemyBattleNativeAttackKind(mode, out _)) ||
            GetEnemyBattleNativeEffects(actor).Any(effect => effect.HasDamageForm() &&
                (effect.RangeType == RangeType.RangeHit || effect.RangeType == RangeType.Distance && effect.RangeParameter > 1)))
        {
            return false;
        }
        var family = GetFamily(actor);
        if (family is CombatAiFamily.Humanoid or CombatAiFamily.Giant)
        {
            return true;
        }
        if (family != CombatAiFamily.Construct)
        {
            return false;
        }
        // A construct needs a native hand/weapon posture, rather than merely a melee bite or collision.
        var slamTitle = GetDefinition<MonsterAttackDefinition>("Attack_Golem_Clay_Slam").GuiPresentation.Title;
        return monster.MonsterDefinition.MonsterPresentation.useHumanoidMonsterPresentationName ||
            GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Main && !mode.Ranged &&
                !TryGetEnemyBattleNativeAttackKind(mode, out _) &&
                (IsEnemyBattleWeaponAttack(mode) || mode.SourceDefinition is MonsterAttackDefinition attack &&
                    attack.GuiPresentation.Title == slamTitle));
    }

    private static bool IsEnemyBattleCrushingAttack(RulesetAttackMode mode) =>
        mode is { ActionType: ActionType.Main, Ranged: false, Thrown: false } &&
        !TryGetEnemyBattleNativeAttackKind(mode, out _) &&
        mode.EffectDescription.EffectForms.FirstOrDefault(form => form.FormType == EffectForm.EffectFormType.Damage)?
            .DamageForm.DamageType is DamageTypeBludgeoning or DamageTypePiercing or DamageTypeSlashing;

    private static bool HasEnemyBattleNativeBonusMeleeSmite(RulesetCharacter actor) =>
        HasEnemyBattleNativeRadiantSmite(actor) ||
        GetEnemyBattleKnownSpells(actor).Any(spell =>
            spell.ActivationTime is ActivationTime.OnAttackHit or ActivationTime.OnAttackHitMeleeAuto or ActivationTime.OnAttackHitAuto &&
            spell.EffectDescription.HasDamageForm()) ||
        GetEnemyBattleNativeFeatures((RulesetCharacterMonster)actor).OfType<FeatureDefinitionPower>()
            .Any(power => power.ActivationTime == ActivationTime.BonusAction &&
                power.EffectDescription.RangeType is RangeType.Touch or RangeType.MeleeHit &&
                power.EffectDescription.HasDamageForm());

    private static RulesetAttackMode GetEnemyBattleNativeMeleeSlashingMode(RulesetCharacter actor) =>
        GetEnemyBattleNativeMeleeDamageMode(actor, DamageTypeSlashing);

    private static RulesetAttackMode GetEnemyBattleNativeMeleeDamageMode(RulesetCharacter actor, string damageType) =>
        GetEnemyBattleOrdinaryAttackModes(actor).Where(mode => mode.ActionType == ActionType.Main && !mode.Ranged && !mode.Thrown &&
                !HasEnemyBattleNativeBonusAttackSource(actor, mode) && mode.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                    form.DamageForm.DamageType == damageType))
            .OrderByDescending(mode => mode.ReachRange).FirstOrDefault();

    private static bool HasEnemyBattleWingedBody(RulesetCharacter actor)
    {
        if (actor is not RulesetCharacterMonster monster)
        {
            return false;
        }
        if (_enemyBattleWingedBodyPrefabs == null)
        {
            var wingAttack = GetDefinition<FeatureDefinitionPower>("PowerDragonWingAttack");
            _enemyBattleWingedBodyPrefabs = new HashSet<string>(DatabaseRepository.GetDatabase<MonsterDefinition>()
                .Where(definition => definition.Features.SelectMany(GetEnemyBattleNestedFeatures).OfType<FeatureDefinitionPower>()
                    .Concat(definition.LegendaryActionOptions.Select(action => action.FeatureDefinitionPower))
                    .Any(power => power != null && (power == wingAttack ||
                        power.GuiPresentation.Title == wingAttack.GuiPresentation.Title)))
                .SelectMany(definition => GetEnemyBattleBodyPrefabGuids(definition.MonsterPresentation))
                .Where(guid => !string.IsNullOrEmpty(guid)));
        }
        return GetEnemyBattleBodyPrefabGuids(monster.MonsterDefinition.MonsterPresentation)
            .Append(monster.MonsterPresentationDefinition?.PrefabReference?.AssetGUID)
            .Any(guid => guid != null && _enemyBattleWingedBodyPrefabs.Contains(guid));
    }

    private static IEnumerable<string> GetEnemyBattleBodyPrefabGuids(MonsterPresentation presentation)
    {
        yield return presentation.malePrefabReference?.AssetGUID;
        yield return presentation.femalePrefabReference?.AssetGUID;
        foreach (var variant in presentation.monsterPresentationDefinitions ?? [])
        {
            yield return variant?.PrefabReference?.AssetGUID;
        }
    }

    private static bool IsEnemyBattleCapabilityCompatible(RulesetCharacter actor, EnemyBattleAffix kind) =>
        IsEnemyBattleCapabilityCompatibleFromFacts(new EnemyBattleNativeFacts(actor), kind);

    private static bool IsEnemyBattleCapabilityCompatibleFromFacts(EnemyBattleNativeFacts facts, EnemyBattleAffix kind)
    {
        var actor = facts.Actor;
        if (actor is not RulesetCharacterMonster monster || !MeetsEnemyBattleTierRequirement(actor, kind))
        {
            return false;
        }
        if (HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.MeleeCombatant) && !facts.IsMeleeCombatant ||
            HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.PhysicalCombatant) && !facts.IsPhysicalCombatant)
        {
            return false;
        }
        var family = GetFamily(actor);
        var naturalMelee = facts.AttackModes.Any(mode => mode.ActionType == ActionType.Main && !mode.Ranged &&
            !IsEnemyBattleWeaponAttack(mode));
        var flight = GetEnemyBattleOrdinaryMoveModes(actor).ContainsKey((int)MoveMode.Fly);
        var ethereal = family == CombatAiFamily.Undead &&
            HasEnemyBattleConditionImmunity(actor.FeaturesByType<FeatureDefinition>(), ConditionDefinitions.ConditionProne) &&
            HasEnemyBattleConditionImmunity(actor.FeaturesByType<FeatureDefinition>(), ConditionDefinitions.ConditionRestrained);
        return kind switch
        {
            EnemyBattleAffix.QuickenedCantrip => HasEnemyBattleAiCantrip(actor),
            EnemyBattleAffix.SpellFlight => HasEnemyBattleSpellFlightSource(actor),
            EnemyBattleAffix.ChargingRush => IsEnemyBattleChargingBody(monster),
            EnemyBattleAffix.WeaponCoating => IsEnemyBattleCoatingBody(monster),
            EnemyBattleAffix.ArmorRend => IsEnemyBattleArmorRendBody(monster),
            EnemyBattleAffix.SweepingVolley => facts.GetRangedSource(kind) != null &&
                GetEnemyBattleCapabilityDamageDiceNumber(actor, kind) > 0,
            EnemyBattleAffix.CoordinatedAdvance => IsEnemyBattleCommandingBody(monster),
            EnemyBattleAffix.ElementalAbsorption => HasSpellcasting(actor) && HasEnemyBattleSpellLevel(actor, 1) &&
                !HasEnemyBattleSpell(monster, SpellsContext.ElementalInfusion),
            EnemyBattleAffix.ReachIntercept => (facts.AttackModes.Any(IsEnemyBattleNativeMeleeAttack) ||
                facts.KnownSpells.Any(spell => spell.SpellLevel == 0 && spell.ActivationTime == ActivationTime.Action &&
                    IsEnemyBattleMeleeCantrip(spell))) &&
                !facts.Features.Any(feature =>
                    feature.GetAllSubFeaturesOfType<CanMakeAoOOnReachEntered>().Count != 0),
            EnemyBattleAffix.LegendaryResolve => !HasEnemyBattleNativeLegendaryResistance(monster),
            EnemyBattleAffix.ImprovisedThrow => IsEnemyBattleImprovisedThrowBody(actor) &&
                GetEnemyBattleCapabilityDamageDiceNumber(actor, kind) > 0,
            EnemyBattleAffix.ViscousSnare => GetEnemyBattleNativeMeleeSlashingMode(actor) != null,
            EnemyBattleAffix.PiercingJab => GetEnemyBattleNativeMeleeDamageMode(actor, DamageTypePiercing) != null &&
                GetEnemyBattleCapabilityDamageDiceNumber(actor, kind) > 0,
            EnemyBattleAffix.WingBuffet => flight && HasEnemyBattleWingedBody(actor),
            EnemyBattleAffix.FocusedVolley or EnemyBattleAffix.ElementalLance or EnemyBattleAffix.RadiantBolt => facts.GetRangedSource(kind) != null &&
                !HasEnemyBattleNativeBonusRanged(actor, kind) && GetEnemyBattleCapabilityDamageDiceNumber(actor, kind) > 0,
            EnemyBattleAffix.ElementalRebuke => GetEnemyBattleNativeElement(actor) != null && GetEnemyBattleDamageDiceNumber(actor) > 0,
            EnemyBattleAffix.GraveLeech => family is CombatAiFamily.Undead or CombatAiFamily.Fiend or CombatAiFamily.Aberration &&
                facts.HasDamage(DamageTypeNecrotic) && !facts.HasLifeDrain &&
                GetEnemyBattleCapabilityDamageDiceNumber(actor, kind) > 0,
            EnemyBattleAffix.RadiantSmite => facts.AttackModes.Any(mode => mode.ActionType == ActionType.Main && !mode.Ranged && !mode.Thrown) &&
                (family == CombatAiFamily.Celestial || facts.HasDamage(DamageTypeRadiant)) &&
                !facts.HasRadiantSmite && GetEnemyBattleCapabilityDamageDiceNumber(actor, kind) > 0,
            EnemyBattleAffix.FiendMiasma => family is CombatAiFamily.Fiend or CombatAiFamily.Plant &&
                facts.HasDamage(DamageTypePoison),
            EnemyBattleAffix.WitheringMark => family == CombatAiFamily.Undead &&
                (facts.HasDamage(DamageTypeNecrotic) || facts.HasDamage(DamageTypeCold)),
            EnemyBattleAffix.BlindingEmbers => family == CombatAiFamily.Elemental && facts.HasDamage(DamageTypeFire),
            EnemyBattleAffix.CausticRetort => family == CombatAiFamily.Ooze && facts.HasDamage(DamageTypeAcid),
            EnemyBattleAffix.LightningSurge => family != CombatAiFamily.Humanoid && facts.HasDamage(DamageTypeLightning),
            EnemyBattleAffix.PackCall => naturalMelee && family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity,
            EnemyBattleAffix.PredatoryLunge => naturalMelee && !ethereal &&
                !facts.AttackModes.Any(mode => mode.ActionType == ActionType.Bonus && !TryGetEnemyBattleNativeAttackKind(mode, out _)) &&
                family is CombatAiFamily.Beast or CombatAiFamily.Monstrosity or CombatAiFamily.Fiend or CombatAiFamily.Undead,
            EnemyBattleAffix.FeyVeil => family == CombatAiFamily.Fey ||
                family == CombatAiFamily.Undead && ethereal && facts.HasDamage(DamageTypeNecrotic),
            EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.PsychicBacklash => family == CombatAiFamily.Aberration &&
                facts.HasMentalInfluence,
            EnemyBattleAffix.UnsettlingCry => facts.HasMentalInfluence,
            EnemyBattleAffix.ArcaneRetort => HasSpellcasting(actor) || facts.HasDamage(DamageTypeForce),
            EnemyBattleAffix.ConstructBrace => family == CombatAiFamily.Construct,
            EnemyBattleAffix.PrimordialRepulsion => family == CombatAiFamily.Elemental && flight,
            EnemyBattleAffix.PurifyingTouch => family == CombatAiFamily.Celestial,
            EnemyBattleAffix.StoneTremor => !flight && actor.TryGetAttributeValue(AttributeDefinitions.Strength) >= 18 &&
                monster.SizeDefinition.WieldingSize >= GetDefinition<CharacterSizeDefinition>("Large").WieldingSize &&
                family is CombatAiFamily.Giant or CombatAiFamily.Construct or CombatAiFamily.Elemental,
            EnemyBattleAffix.ThunderousRoar => naturalMelee && !ethereal &&
                facts.HasDamage(DamageTypeThunder) && GetEnemyBattleDamageDiceNumber(actor) > 0,
            EnemyBattleAffix.ForcePulse => GetEnemyBattleDamageDiceNumber(actor) > 0 &&
                (facts.HasDamage(DamageTypeForce) || HasSpellcasting(actor) &&
                    actor.TryGetAttributeValue(AttributeDefinitions.Intelligence) >= 8 &&
                    GetEnemyBattleChallengeRating(actor) >= 5 && HasEnemyBattleSpellLevel(actor, 2)),
            EnemyBattleAffix.LastingSurge => IsEnemyBattleLastingSurgeCompatible(monster),
            _ => true
        };
    }

    private static bool HasEnemyBattleNativeLegendaryResistance(RulesetCharacterMonster actor) =>
        actor.MonsterDefinition is { LegendaryCreature: true, MaxLegendaryResistances: > 0 } ||
        GetEnemyBattleNativeFeatures(actor).OfType<FeatureDefinitionPower>().Any(power =>
            power.Name.IndexOf("LegendaryResistance", StringComparison.OrdinalIgnoreCase) >= 0);

    private static bool IsEnemyBattleLastingSurgeCompatible(RulesetCharacterMonster actor) =>
        HasSpellcasting(actor) && HasEnemyBattleSpellLevel(actor, 3) &&
        actor.TryGetAttributeValue(AttributeDefinitions.Dexterity) >= 12 &&
        GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Main &&
            !TryGetEnemyBattleNativeAttackKind(mode, out _) && mode.EffectDescription.HasDamageForm()) &&
        !GetEnemyBattleNativeFeatures(actor).OfType<FeatureDefinitionAdditionalAction>()
            .Concat(GetEnemyBattleNativeEffects(actor).SelectMany(GetEnemyBattleAddedConditionFeatures)
                .OfType<FeatureDefinitionAdditionalAction>())
            .Any(feature => feature.ActionType == ActionType.Main);

    private static bool HasEnemyBattleAcceleration(RulesetCharacter actor) =>
        actor.ConditionsByCategory.Values.SelectMany(conditions => conditions)
            .Any(condition => condition.ConditionDefinition.Name == "ConditionEnemyBattleLastingSurgeEffect" ||
                GetEnemyBattleConditionFeatures(condition.ConditionDefinition)
                    .Contains(FeatureDefinitionAdditionalActions.AdditionalActionHasted));

    private static int GetEnemyBattlePowerRange(RulesetCharacter actor, EnemyBattleAffix kind) =>
        kind == EnemyBattleAffix.ImprovisedThrow ? 12 :
        kind is EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.PiercingJab ? System.Math.Max(1,
            GetEnemyBattleNativeMeleeDamageMode(actor, GetEnemyBattleDamageType(kind))?.ReachRange ?? 1) :
        IsEnemyBattleRangedDamage(kind) ? GetEnemyBattleRangedSource(actor, kind)?.Range ??
            (kind == EnemyBattleAffix.RadiantBolt ? GuidingBolt.EffectDescription.RangeParameter : 12) :
            EnemyBattleAffixes[kind].Power.EffectDescription.RangeParameter;

    private static bool IsEnemyBattleRangedDamage(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.RangedDamage);

    private static bool IsEnemyBattleRangedSpellAttack(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.RangedSpellAttack);

    private static RulesetSpellRepertoire GetEnemyBattleRadiantRepertoire(RulesetCharacter actor)
    {
        HashSet<FeatureDefinitionCastSpell> nativeRadiantCasting = actor is RulesetCharacterMonster monster &&
            (GetFamily(actor) == CombatAiFamily.Celestial || HasEnemyBattleNativeDamage(actor, DamageTypeRadiant))
            ? new HashSet<FeatureDefinitionCastSpell>(monster.MonsterDefinition.Features
                .SelectMany(GetEnemyBattleNestedFeatures).OfType<FeatureDefinitionCastSpell>()) : [];
        return GetEnemyBattleOrdinarySpellRepertoires(actor).Where(repertoire => repertoire.SpellCastingFeature != null &&
            !IsEnemyBattleGrantedSpellRepertoire(repertoire) &&
            (repertoire.SpellCastingClass == CharacterClassDefinitions.Cleric ||
             repertoire.SpellCastingClass == CharacterClassDefinitions.Paladin ||
             repertoire.SpellCastingFeature.SpellListDefinition == SpellListDefinitions.SpellListCleric ||
             repertoire.SpellCastingFeature.SpellListDefinition == SpellListDefinitions.SpellListPaladin ||
             nativeRadiantCasting.Contains(repertoire.SpellCastingFeature) ||
             GetEnemyBattleKnownSpells(repertoire).Any(spell => spell.EffectDescription.EffectForms.Any(form =>
                 form.FormType == EffectForm.EffectFormType.Damage && form.DamageForm.DamageType == DamageTypeRadiant))))
            .OrderByDescending(repertoire => repertoire.SpellAttackBonus)
            .ThenByDescending(repertoire => repertoire.SaveDC).FirstOrDefault();
    }

    private static RulesetSpellRepertoire GetEnemyBattlePowerRepertoire(RulesetCharacter actor,
        FeatureDefinitionPower power) => actor.FindClassHoldingFeature(power) is { } sourceClass
        ? GetEnemyBattleOrdinarySpellRepertoires(actor).FirstOrDefault(repertoire => repertoire.SpellCastingClass == sourceClass)
        : null;

    private static bool IsEnemyBattleNativeRangedMagic(EffectDescription description, int minimumRange) =>
        description.RangeType is RangeType.Distance or RangeType.RangeHit && description.RangeParameter >= minimumRange;

    private static bool HasEnemyBattleNativeBonusRanged(RulesetCharacter actor, EnemyBattleAffix? kind = null)
    {
        bool MatchingEffect(EffectDescription effect) => kind == EnemyBattleAffix.RadiantBolt
            ? effect.NeedsToRollDie() && IsEnemyBattleNativeRangedMagic(effect, 4) &&
              effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                  form.DamageForm.DamageType == DamageTypeRadiant) &&
              HasEnemyBattleAddedCondition(effect, GetDefinition<ConditionDefinition>("ConditionTargetedByGuidingBolt"))
            : IsEnemyBattleNativeRangedMagic(effect, 4) && effect.HasDamageForm();
        return GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Bonus && mode.Ranged &&
                !TryGetEnemyBattleNativeAttackKind(mode, out _) &&
                (kind != EnemyBattleAffix.RadiantBolt || MatchingEffect(mode.EffectDescription))) ||
            actor.FeaturesByType<FeatureDefinitionPower>()
                .Concat(AddUsablePowersFromCondition.GetGrantedUsablePowers(actor).Select(usable => usable.PowerDefinition))
                .Where(power => !EnemyBattlePowerKinds.ContainsKey(power))
                .Any(power => power.ActivationTime == ActivationTime.BonusAction && MatchingEffect(power.EffectDescription)) ||
            GetEnemyBattleKnownSpells(actor).Any(spell => spell.ActivationTime == ActivationTime.BonusAction &&
                MatchingEffect(spell.EffectDescription));
    }

    private sealed class EnemyBattleRangedSource(EffectDescription description, int range, string damageType,
        string ability, RulesetAttackMode attackMode = null, FeatureDefinitionPower sourcePower = null,
        RulesetSpellRepertoire repertoire = null)
    {
        internal EffectDescription Description { get; } = description;
        internal int Range { get; } = range;
        internal string DamageType { get; } = damageType;
        internal string Ability { get; } = ability;
        internal RulesetAttackMode AttackMode { get; } = attackMode;
        internal FeatureDefinitionPower SourcePower { get; } = sourcePower;
        internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
    }

    private static EnemyBattleRangedSource GetEnemyBattleRangedSource(RulesetCharacter actor, EnemyBattleAffix kind)
    {
        if (actor is not RulesetCharacterMonster || !IsEnemyBattleRangedDamage(kind))
        {
            return null;
        }
        if (kind == EnemyBattleAffix.SweepingVolley)
        {
            var mode = GetEnemyBattleOrdinaryAttackModes(actor).Where(mode => mode is { ActionType: ActionType.Main, Ranged: true, Thrown: false } &&
                    IsEnemyBattleWeaponAttack(mode) && !TryGetEnemyBattleNativeAttackKind(mode, out _) &&
                    ((RulesetCharacterMonster)actor).GetAttackModeRank(mode) >= 0 &&
                    mode.EffectDescription.FindFirstDamageForm()?.DamageType is DamageTypeBludgeoning or DamageTypePiercing or DamageTypeSlashing)
                .OrderByDescending(mode => GetEnemyBattleEffectAverageDamage(mode.EffectDescription))
                .ThenByDescending(mode => mode.MaxRange).ThenBy(mode => mode.SourceDefinition.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            return mode == null ? null : new EnemyBattleRangedSource(mode.EffectDescription, (int)mode.MaxRange,
                mode.EffectDescription.FindFirstDamageForm().DamageType, AttributeDefinitions.Dexterity, mode);
        }
        if (kind == EnemyBattleAffix.RadiantBolt)
        {
            var repertoire = GetEnemyBattleRadiantRepertoire(actor);
            if (repertoire != null)
            {
                return new EnemyBattleRangedSource(GuidingBolt.EffectDescription,
                    GuidingBolt.EffectDescription.RangeParameter, DamageTypeRadiant,
                    repertoire.SpellCastingFeature.SpellcastingAbility, repertoire: repertoire);
            }
            var sourcePower = actor.FeaturesByType<FeatureDefinitionPower>()
                .Concat(AddUsablePowersFromCondition.GetGrantedUsablePowers(actor).Select(usable => usable.PowerDefinition))
                .Where(power => !EnemyBattlePowerKinds.ContainsKey(power) &&
                    power.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                        form.DamageForm.DamageType == DamageTypeRadiant))
                .OrderByDescending(power => GetEnemyBattleEffectAverageDamage(power.EffectDescription))
                .ThenBy(power => power.Name, StringComparer.Ordinal).FirstOrDefault();
            if (sourcePower != null)
            {
                repertoire = GetEnemyBattlePowerRepertoire(actor, sourcePower);
                var native = sourcePower.EffectDescription;
                var ability = repertoire?.SpellCastingFeature?.SpellcastingAbility ??
                    (!native.NeedsToRollDie() && native.HasSavingThrow &&
                     native.DifficultyClassComputation == EffectDifficultyClassComputation.AbilityScoreAndProficiency &&
                     !string.IsNullOrEmpty(native.SavingThrowDifficultyAbility)
                        ? native.SavingThrowDifficultyAbility : actor.GetAbilityScoreOfPower(sourcePower));
                return new EnemyBattleRangedSource(GuidingBolt.EffectDescription,
                    GuidingBolt.EffectDescription.RangeParameter, DamageTypeRadiant, ability,
                    sourcePower: sourcePower, repertoire: repertoire);
            }
            // Innate celestial and radiant bodies need no ordinary spellcasting repertoire.
            return GetFamily(actor) == CombatAiFamily.Celestial || HasEnemyBattleNativeDamage(actor, DamageTypeRadiant)
                ? new EnemyBattleRangedSource(GuidingBolt.EffectDescription,
                    GuidingBolt.EffectDescription.RangeParameter, DamageTypeRadiant, AttributeDefinitions.Wisdom)
                : null;
        }
        bool MatchingType(string damageType) => kind == EnemyBattleAffix.FocusedVolley
            ? damageType is DamageTypeBludgeoning or DamageTypePiercing or DamageTypeSlashing
            : damageType is DamageTypeAcid or DamageTypeCold or DamageTypeFire or DamageTypeLightning or DamageTypePoison or DamageTypeThunder;
        var sources = new List<EnemyBattleRangedSource>();
        // Physical sources retain their native attack animation and projectile through FocusedVolley.
        // The lance uses an actual magic power or spell's casting and effect data.
        foreach (var mode in GetEnemyBattleOrdinaryAttackModes(actor).Where(mode => kind == EnemyBattleAffix.FocusedVolley &&
                     mode.ActionType == ActionType.Main && mode.Ranged && mode.MaxRange >= 8))
        {
            var damage = mode.EffectDescription.EffectForms.FirstOrDefault(form => form.FormType == EffectForm.EffectFormType.Damage &&
                MatchingType(form.DamageForm.DamageType));
            if (damage != null && GetEnemyBattleEffectAverageDamage(mode.EffectDescription) > 0)
            {
                sources.Add(new EnemyBattleRangedSource(mode.EffectDescription, (int)mode.MaxRange,
                    damage.DamageForm.DamageType, kind == EnemyBattleAffix.FocusedVolley ?
                        AttributeDefinitions.Dexterity : AttributeDefinitions.Constitution, mode));
            }
        }
        if (kind == EnemyBattleAffix.ElementalLance)
        {
            var nativePowers = actor.FeaturesByType<FeatureDefinitionPower>()
                .Where(power => power.ActivationTime == ActivationTime.Action && !EnemyBattlePowerKinds.ContainsKey(power) &&
                    IsEnemyBattleNativeRangedMagic(power.EffectDescription, 8) &&
                    power.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
                        MatchingType(form.DamageForm.DamageType)))
                .Select(power =>
                {
                    var repertoire = power.AttackHitComputation == (PowerAttackHitComputation)ExtraPowerAttackHitComputation.SpellAttack ||
                                     !power.EffectDescription.NeedsToRollDie() && power.EffectDescription.HasSavingThrow &&
                                     power.EffectDescription.DifficultyClassComputation == EffectDifficultyClassComputation.SpellCastingFeature
                        ? GetEnemyBattlePowerRepertoire(actor, power) : null;
                    return (Description: power.EffectDescription, Power: power, Repertoire: repertoire,
                        Ability: repertoire?.SpellCastingFeature?.SpellcastingAbility ?? actor.GetAbilityScoreOfPower(power));
                });
            var nativeSpells = GetEnemyBattleOrdinarySpellRepertoires(actor).Where(rep => !IsEnemyBattleGrantedSpellRepertoire(rep))
                .SelectMany(repertoire => GetEnemyBattleKnownSpells(repertoire)
                .Where(spell => spell.ActivationTime == ActivationTime.Action)
                .Select(spell => (Description: spell.EffectDescription, Power: (FeatureDefinitionPower)null,
                    Repertoire: repertoire, Ability: repertoire.SpellCastingFeature?.SpellcastingAbility ?? AttributeDefinitions.Constitution)));
            foreach (var (description, sourcePower, repertoire, ability) in nativePowers.Concat(nativeSpells).Where(source =>
                         IsEnemyBattleNativeRangedMagic(source.Description, 8)))
            {
                var damage = description.EffectForms.FirstOrDefault(form => form.FormType == EffectForm.EffectFormType.Damage &&
                    MatchingType(form.DamageForm.DamageType));
                if (damage != null && GetEnemyBattleEffectAverageDamage(description) > 0)
                {
                    sources.Add(new EnemyBattleRangedSource(description, description.RangeParameter,
                        damage.DamageForm.DamageType, !description.NeedsToRollDie() && description.HasSavingThrow &&
                            description.DifficultyClassComputation == EffectDifficultyClassComputation.AbilityScoreAndProficiency &&
                            !string.IsNullOrEmpty(description.SavingThrowDifficultyAbility)
                            ? description.SavingThrowDifficultyAbility : ability, sourcePower: sourcePower, repertoire: repertoire));
                }
            }
        }
        return sources.OrderByDescending(source => GetEnemyBattleEffectAverageDamage(source.Description))
            .ThenByDescending(source => source.Range).ThenBy(source => source.DamageType, StringComparer.Ordinal).FirstOrDefault();
    }

    internal static bool TryGetEnemyBattleMagicAttackAbility(RulesetCharacter actor, FeatureDefinitionPower power,
        out string ability)
    {
        ability = null;
        if (!TryGetEnemyBattleCapabilityKind(power, out var kind) || !IsEnemyBattleRangedSpellAttack(kind) ||
            GetEnemyBattleRangedSource(actor, kind) is not { } source)
        {
            return false;
        }
        ability = source.Ability;
        return true;
    }

    internal static bool TryGetEnemyBattleMagicAttackInfo(RulesetEffectPower effect, out int bonus,
        out List<TrendInfo> trends)
    {
        bonus = 0;
        trends = null;
        var actor = effect.User;
        if (!TryGetEnemyBattleCapabilityKind(effect.PowerDefinition, out var kind) || !IsEnemyBattleRangedSpellAttack(kind))
        {
            return false;
        }
        trends = [];
        var source = GetEnemyBattleRangedSource(actor, kind);
        if (source == null)
        {
            return true;
        }
        var ability = source.Ability;
        var repertoire = source.Repertoire;
        var native = source.SourcePower;
        if (repertoire == null && native?.AttackHitComputation ==
            (PowerAttackHitComputation)ExtraPowerAttackHitComputation.SpellAttack)
        {
            repertoire = GetEnemyBattlePowerRepertoire(actor, native);
        }
        if (repertoire != null)
        {
            bonus = repertoire.SpellAttackBonus;
            trends = repertoire.MagicAttackTrends;
            return true;
        }
        if (native?.EffectDescription.NeedsToRollDie() == true && native.AttackHitComputation == PowerAttackHitComputation.Fixed)
        {
            bonus = native.FixedAttackHit;
            trends.Add(new TrendInfo(bonus, FeatureSourceType.AbilityScore, ability, null));
            return true;
        }
        var nativeAttack = native?.EffectDescription.NeedsToRollDie() == true;
        if (!nativeAttack || native.AbilityScoreBonusToAttack)
        {
            var modifier = AttributeDefinitions.ComputeAbilityScoreModifier(actor.TryGetAttributeValue(ability));
            bonus += modifier;
            trends.Add(new TrendInfo(modifier, FeatureSourceType.AbilityScore, ability, null));
        }
        if (!nativeAttack || native.ProficiencyBonusToAttack)
        {
            var proficiency = actor.TryGetProficiencyBonus();
            bonus += proficiency;
            trends.Add(new TrendInfo(proficiency, FeatureSourceType.Proficiency, string.Empty, null));
        }
        return true;
    }

    private static string GetEnemyBattleEffectiveDamageType(RulesetCharacter actor, EnemyBattleAffix kind) =>
        IsEnemyBattleRangedDamage(kind) ? GetEnemyBattleRangedSource(actor, kind)?.DamageType ?? GetEnemyBattleDamageType(kind) :
            kind == EnemyBattleAffix.ArmorRend ? GetEnemyBattleArmorRendSource(actor)?.EffectDescription.FindFirstDamageForm().DamageType ?? DamageTypeBludgeoning :
            kind == EnemyBattleAffix.ElementalRebuke ? (actor == null ? null : GetEnemyBattleNativeElement(actor)) ?? DamageTypeFire :
                GetEnemyBattleDamageType(kind);

    private static IEnumerable<SpellDefinition> GetEnemyBattleKnownSpells(RulesetCharacter actor)
    {
        return GetEnemyBattleOrdinarySpellRepertoires(actor).Where(rep => !IsEnemyBattleGrantedSpellRepertoire(rep))
            .SelectMany(GetEnemyBattleKnownSpells).Distinct();
    }

    private static IEnumerable<SpellDefinition> GetEnemyBattleKnownSpells(RulesetSpellRepertoire repertoire) =>
        repertoire.KnownCantrips.Concat(repertoire.KnownSpells).Concat(repertoire.PreparedSpells)
            .Concat(repertoire.AutoPreparedSpells).Concat(repertoire.ExtraSpellsByTag.Values.SelectMany(spells => spells))
            .Distinct();

    private static IEnumerable<FeatureDefinition> GetEnemyBattleAddedConditionFeatures(EffectDescription effect)
    {
        return effect.EffectForms.Where(form => form.FormType == EffectForm.EffectFormType.Condition &&
                form.ConditionForm is { Operation: ConditionForm.ConditionOperation.Add, ConditionDefinition: not null })
            .SelectMany(form => GetEnemyBattleConditionFeatures(form.ConditionForm.ConditionDefinition));
    }

    private static IEnumerable<FeatureDefinition> GetEnemyBattleConditionFeatures(ConditionDefinition condition)
    {
        foreach (var feature in condition.Features)
        {
            foreach (var nested in GetEnemyBattleNestedFeatures(feature))
            {
                yield return nested;
            }
        }

        if (condition.parentCondition != null)
        {
            foreach (var feature in GetEnemyBattleConditionFeatures(condition.parentCondition))
            {
                yield return feature;
            }
        }
    }

    private static IEnumerable<FeatureDefinition> GetEnemyBattleNestedFeatures(FeatureDefinition feature)
    {
        yield return feature;
        if (feature is not FeatureDefinitionFeatureSet set)
        {
            yield break;
        }
        foreach (var child in set.FeatureSet.SelectMany(GetEnemyBattleNestedFeatures))
        {
            yield return child;
        }
    }

    private static bool HasEnemyBattleAddedCondition(EffectDescription effect, ConditionDefinition condition)
    {
        return effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Condition &&
            form.ConditionForm is { Operation: ConditionForm.ConditionOperation.Add, ConditionDefinition: not null } added &&
            added.ConditionDefinition.IsSubtypeOf(condition.Name));
    }

    private static bool HasEnemyBattleAttackAdvantage(IEnumerable<FeatureDefinition> features,
        IEnumerable<EffectDescription> effects, Side side, bool requireOtherTarget = false)
    {
        return features.OfType<FeatureDefinitionCombatAffinity>().Any(affinity =>
                   affinity.MyAttackAdvantage == AdvantageType.Advantage) ||
               effects.Any(effect => effect.TargetSide == side && (!requireOtherTarget || effect.TargetType != TargetType.Self) &&
                   GetEnemyBattleAddedConditionFeatures(effect).OfType<FeatureDefinitionCombatAffinity>()
                       .Any(affinity => affinity.MyAttackAdvantage == AdvantageType.Advantage));
    }

    private static string GetEnemyBattleNativeElement(RulesetCharacter actor)
    {
        string[] elements = [DamageTypeAcid, DamageTypeCold, DamageTypeFire, DamageTypeLightning,
            DamageTypeThunder, DamageTypePoison, DamageTypeNecrotic, DamageTypeRadiant, DamageTypePsychic];
        return GetEnemyBattleNativeEffects(actor).SelectMany(effect => effect.EffectForms)
            .Where(form => form.FormType == EffectForm.EffectFormType.Damage && elements.Contains(form.DamageForm.DamageType))
            .OrderByDescending(form => form.DamageForm.DiceNumber * DiceMaxValue[(int)form.DamageForm.DieType] +
                                       form.DamageForm.BonusDamage)
            .ThenBy(form => form.DamageForm.DamageType, StringComparer.Ordinal)
            .Select(form => form.DamageForm.DamageType).FirstOrDefault();
    }

    private static bool HasEnemyBattleSpell(RulesetCharacter character, SpellDefinition spell)
    {
        return GetEnemyBattleOrdinarySpellRepertoires(character).Where(rep => !IsEnemyBattleGrantedSpellRepertoire(rep))
            .Any(repertoire => repertoire.HasKnowledgeOfSpell(spell) ||
            repertoire.KnownSpells.Contains(spell) || repertoire.PreparedSpells.Contains(spell) ||
            repertoire.AutoPreparedSpells.Contains(spell) ||
            repertoire.ExtraSpellsByTag.Values.Any(list => list.Contains(spell)));
    }

    private static CombatAiProfile BuildEnemyBattleProfile(GameLocationCharacter character) =>
        BuildEnemyBattleProfileFromFacts(character, new EnemyBattleNativeFacts(character.RulesetCharacter));

    private static CombatAiProfile BuildEnemyBattleProfileFromFacts(GameLocationCharacter character,
        EnemyBattleNativeFacts facts)
    {
        var actor = character.RulesetCharacter;
        var family = GetFamily(actor);
        var ranged = facts.AttackModes.Any(IsRangedAttackMode);
        var spellcasting = HasSpellcasting(actor);
        var temperament = actor is RulesetCharacterMonster monster &&
                          monster.MonsterDefinition.Alignment?.StartsWith("Lawful", StringComparison.OrdinalIgnoreCase) == true
            ? CombatAiTemperament.Disciplined
            : GetTemperament(actor, family, independentOfAiSetting: true);
        return new CombatAiProfile(facts.NativeCombatRole, family,
            temperament,
            !actor.IsTouchingGround() || GetEnemyBattleOrdinaryMoveModes(actor).ContainsKey((int)MoveMode.Fly), ranged, spellcasting);
    }

    private static bool HasEnemyBattleThirdLevelSpells(RulesetCharacter character)
        => HasEnemyBattleSpellLevel(character, 3);

    private static bool HasEnemyBattleSpellLevel(RulesetCharacter character, int level)
    {
        return GetEnemyBattleOrdinarySpellRepertoires(character).Where(rep => !IsEnemyBattleGrantedSpellRepertoire(rep))
            .Any(repertoire => repertoire.MaxSpellLevelOfSpellCastingLevel >= level ||
            repertoire.KnownSpells.Concat(repertoire.PreparedSpells).Concat(repertoire.AutoPreparedSpells)
                .Concat(repertoire.ExtraSpellsByTag.Values.SelectMany(list => list))
                .Any(spell => spell.SpellLevel >= level));
    }

    private static bool IsEnemyBattleCounterPower(FeatureDefinitionPower power)
    {
        return power.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Counter &&
            form.CounterForm.Type == CounterForm.CounterType.InterruptSpellcasting);
    }

    private static bool HasExistingEnemyBattleDefense(IEnumerable<FeatureDefinitionPower> powers)
    {
        return powers.Any(power => power.ActivationTime is ActivationTime.Reaction or ActivationTime.NoCost &&
            power.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Condition &&
                form.ConditionForm.Operation == ConditionForm.ConditionOperation.Add &&
                form.ConditionForm.ConditionDefinition != null &&
                form.ConditionForm.ConditionDefinition.Features.Any(feature =>
                    feature is FeatureDefinitionReduceDamage || feature is FeatureDefinitionAttributeModifier modifier &&
                    modifier.ModifiedAttribute == AttributeDefinitions.ArmorClass)));
    }

    private static bool HasEnemyBattleConditionImmunity(IEnumerable<FeatureDefinition> features,
        ConditionDefinition condition)
    {
        return features.OfType<FeatureDefinitionConditionAffinity>().Any(affinity =>
            affinity.ConditionAffinityType == ConditionAffinityType.Immunity && affinity.ConditionType == condition.Name);
    }

    private static bool HasExistingEnemyBattleReprisal(IEnumerable<FeatureDefinition> features,
        IEnumerable<FeatureDefinitionPower> powers, IEnumerable<SpellDefinition> spells)
    {
        return features.Any(feature => feature.GetAllSubFeaturesOfType<IPhysicalAttackFinishedOnMe>().Count != 0 ||
            feature.GetAllSubFeaturesOfType<ITryAlterOutcomeAttack>().Count != 0) ||
            powers.Any(power => power.ActivationTime == ActivationTime.Reaction &&
                power.EffectDescription.HasDamageForm()) ||
            spells.Any(spell => spell.ActivationTime == ActivationTime.Reaction &&
                spell.EffectDescription.HasDamageForm());
    }

    private static bool HasEnemyBattleBonusAction(IEnumerable<FeatureDefinition> features,
        IEnumerable<FeatureDefinitionPower> powers, Id action, params ConditionDefinition[] conditions)
    {
        return features.OfType<FeatureDefinitionActionAffinity>().Any(affinity => affinity.AuthorizedActions.Contains(action)) ||
            powers.Any(power => power.ActivationTime == ActivationTime.BonusAction &&
                power.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Condition &&
                    form.ConditionForm.Operation == ConditionForm.ConditionOperation.Add &&
                    form.ConditionForm.ConditionDefinition != null &&
                    conditions.Any(condition => form.ConditionForm.ConditionDefinition.IsSubtypeOf(condition.Name))));
    }

    private static EffectDescription BuildEnemyBattleMeleeMarkerDescription()
    {
        return EffectDescriptionBuilder.Create()
            .SetTargetingData(Side.Enemy, RangeType.Touch, 1, TargetType.IndividualsUnique, 1)
            .SetDurationData(DurationType.Instantaneous)
            .Build();
    }

    private static bool IsEnemyBattleWeaponCoating(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.WeaponCoating);

    private static bool IsEnemyBattleCoatingBody(RulesetCharacter actor) =>
        actor.TryGetAttributeValue(AttributeDefinitions.Intelligence) >= 8 &&
        GetEnemyBattleCapabilityDamageDiceNumber(actor, EnemyBattleAffix.WeaponCoating) > 0 &&
        GetFamily(actor) is CombatAiFamily.Humanoid or CombatAiFamily.Giant or CombatAiFamily.Fey or
            CombatAiFamily.Fiend or CombatAiFamily.Undead or CombatAiFamily.Celestial &&
        GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Main && IsEnemyBattleWeaponAttack(mode) &&
            mode.EffectDescription.HasDamageForm()) &&
        !GetEnemyBattleOrdinaryAttackModes(actor).Where(IsEnemyBattleWeaponAttack).Any(mode => mode.EffectDescription.EffectForms.Any(form =>
            form.FormType == EffectForm.EffectFormType.Damage && form.DamageForm.DamageType is DamageTypePoison or DamageTypeAcid)) &&
        !GetEnemyBattleNativeFeatures((RulesetCharacterMonster)actor).OfType<FeatureDefinitionAdditionalDamage>()
            .Any(damage => damage.SpecificDamageType is DamageTypePoison or DamageTypeAcid);

    private static bool IsEnemyBattleCommandingBody(RulesetCharacterMonster actor) =>
        actor.TryGetAttributeValue(AttributeDefinitions.Intelligence) >= 12 &&
        actor.TryGetAttributeValue(AttributeDefinitions.Charisma) >= 12 &&
        GetFamily(actor) is not (CombatAiFamily.Ooze or CombatAiFamily.Plant or CombatAiFamily.Construct);

    private static FeatureDefinitionPower BuildEnemyBattleWeaponCoatingPower(string name)
    {
        const EnemyBattleAffix kind = EnemyBattleAffix.WeaponCoating;
        var metadata = GetEnemyBattleCapabilityMetadata(kind);
        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetShowCasting(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Round, 0, TurnOccurenceType.StartOfTurn).Build())
            .AddToDB();
        var choices = new List<FeatureDefinitionPower>();
        foreach (var element in new[] { DamageTypePoison, DamageTypeAcid })
        {
            var variant = element == DamageTypePoison ? "PoisonCoating" : "AcidCoating";
            var choiceKey = element == DamageTypePoison ? "WeaponCoatingPoisonChoice" : "WeaponCoatingAcidChoice";
            var choiceSprite = GetEnemyBattleSprite($"ConditionEnemyBattle{variant}Effect");
            var condition = ConditionDefinitionBuilder.Create($"ConditionEnemyBattle{variant}Effect")
                .SetGuiPresentation($"Feature/&EnemyBattle{variant}EffectTitle", $"Feature/&EnemyBattle{variant}EffectDescription",
                    choiceSprite)
                .SetConditionType(ConditionType.Beneficial).SetPossessive()
                .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
                .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance, new FormattedDefinitionText(
                    description: () => Gui.Format($"Feature/&EnemyBattle{variant}EffectDescription", string.Empty, "1d4"),
                    characterDescription: actor => Gui.Format($"Feature/&EnemyBattle{variant}EffectDescription", string.Empty,
                        $"{GetEnemyBattleCapabilityDamageDiceNumber(actor, kind)}d4"),
                    conditionDescription: active => Gui.Format($"Feature/&EnemyBattle{variant}EffectDescription", string.Empty,
                        $"{GetEnemyBattleCoatingDiceNumber(active)}d4"))).AddToDB();
            var choice = FeatureDefinitionPowerSharedPoolBuilder.Create($"PowerEnemyBattle{variant}")
                .SetGuiPresentation($"EnemyBattle{choiceKey}", Category.Feature, choiceSprite)
                .SetSharedPool(metadata.ActivationTime, power)
                .SetShowCasting(false)
                .SetEffectDescription(EffectDescriptionBuilder.Create(power.EffectDescription)
                    .SetEffectForms(EffectFormBuilder.ConditionForm(condition)).Build())
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance,
                    new ValidatorsValidatePowerUse(actor =>
                        CanUseEnemyBattlePower(GameLocationCharacter.GetFromActor(actor), kind, out _) &&
                        !HasEnemyBattleWeaponCoating(actor)),
                    new FormattedDefinitionText(
                        title: () => Gui.Localize($"Feature/&EnemyBattle{choiceKey}Title"),
                        description: () => Gui.Format($"Feature/&EnemyBattle{choiceKey}Description", string.Empty, "1d4"),
                        characterDescription: actor => Gui.Format($"Feature/&EnemyBattle{choiceKey}Description", string.Empty,
                            $"{GetEnemyBattleCapabilityDamageDiceNumber(actor, kind)}d4")))
                .AddToDB();
            choice.AddCustomSubFeatures(EnemyBattlePowerTooltipModifier.Instance, new EnemyBattleUtilityPower(kind, choice));
            condition.AddCustomSubFeatures(new EnemyBattlePreparedAttack(kind, condition, choice, element));
            choices.Add(choice);
        }
        PowerBundle.RegisterPowerBundle(power, true, choices);
        return power;
    }

    private static bool HasEnemyBattleWeaponCoating(RulesetCharacter actor) =>
        EnemyBattleAffixes.TryGetValue(EnemyBattleAffix.WeaponCoating, out var entry) &&
        entry.Power.GetBundle().SubPowers.Any(choice =>
            choice.EffectDescription.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Condition &&
                actor.HasConditionOfType(form.ConditionForm.ConditionDefinition.Name)));

    private static int GetEnemyBattleCoatingDiceNumber(RulesetCondition active, RulesetCharacter actor = null)
    {
        if (active.Amount > 0)
        {
            return active.Amount;
        }
        // Saved coatings predating the snapshot amount retain their current bounded damage.
        if (actor == null)
        {
            RulesetEntity.TryGetEntity(active.SourceGuid, out actor);
        }
        return actor == null ? 1 : GetEnemyBattleCapabilityDamageDiceNumber(actor, EnemyBattleAffix.WeaponCoating);
    }

    private static FeatureDefinitionPower GetEnemyBattleWeaponCoatingChoice(GameLocationCharacter actor) =>
        GetEnemyBattleWeaponCoatingChoice(actor, null);

    private static FeatureDefinitionPower GetEnemyBattleWeaponCoatingChoice(GameLocationCharacter actor,
        GameLocationCharacter attackTarget)
    {
        if (!EnemyBattleAffixes.TryGetValue(EnemyBattleAffix.WeaponCoating, out var entry) ||
            HasEnemyBattleWeaponCoating(actor.RulesetCharacter) ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            return null;
        }
        var targets = SnapshotCharacters(actor.PerceivedFoes).Where(foe => foe?.RulesetCharacter is
                { IsDeadOrDyingOrUnconscious: false } && actor.IsOppositeSide(foe.Side) &&
                (attackTarget == null || foe == attackTarget) &&
                HasUsefulEnemyBattlePhysicalAttack(actor, foe, battleService, false, weaponOnly: true))
            .OrderBy(foe => ComputeGridDistance(actor.LocationPosition, foe.LocationPosition))
            .ThenBy(foe => foe.RulesetCharacter.CurrentHitPoints).ThenBy(foe => foe.Guid).ToArray();
        if (targets.Length == 0)
        {
            return null;
        }
        double Score(FeatureDefinitionPower choice)
        {
            var element = choice.Name.EndsWith("PoisonCoating", StringComparison.Ordinal) ? DamageTypePoison : DamageTypeAcid;
            return targets.Max(target =>
            {
                var affinities = target.RulesetCharacter.FeaturesByType<IDamageAffinityProvider>()
                    .Where(affinity => affinity.DamageType == element).Select(affinity => affinity.DamageAffinityType).ToArray();
                return affinities.Contains(DamageAffinityType.Immunity) ? 0 :
                    (affinities.Contains(DamageAffinityType.Resistance) ? 0.5 : 1) *
                    (affinities.Contains(DamageAffinityType.Vulnerability) ? 2 : 1);
            });
        }
        return entry.Power.GetBundle().SubPowers.Where(choice => Score(choice) > 0)
            .OrderByDescending(Score).ThenBy(choice => choice.Name, StringComparer.Ordinal).FirstOrDefault();
    }

    private static bool IsEnemyBattleChargingBody(RulesetCharacter actor) =>
        !GetEnemyBattleOrdinaryMoveModes(actor).ContainsKey((int)MoveMode.Fly) &&
        actor.TryGetAttributeValue(AttributeDefinitions.Strength) >= 16 &&
        GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.ActionType == ActionType.Main && !mode.Ranged && !mode.Thrown &&
            mode.EffectDescription.HasDamageForm()) &&
        GetEnemyBattleCapabilityDamageDiceNumber(actor, EnemyBattleAffix.ChargingRush) > 0 &&
        !GetEnemyBattleOrdinaryAttackModes(actor).Any(mode => mode.SourceDefinition is MonsterAttackDefinition { AfterChargeOnly: true }) &&
        !GetEnemyBattleNativeFeatures((RulesetCharacterMonster)actor).OfType<FeatureDefinitionActionAffinity>()
            .Any(affinity => affinity.AuthorizedActions.Contains(Id.DashBonus));

    private const string EnemyBattleWhirlwindTag = "EnemyBattleWhirlwindReprisal";
    private static readonly ConditionalWeakTable<RulesetCharacter, EnemyBattleArmorRendContext>
        EnemyBattleWhirlwinds = new();

    private static RulesetAttackMode GetEnemyBattleArmorRendSource(RulesetCharacter actor) =>
        GetEnemyBattleOrdinaryAttackModes(actor).Where(mode => IsEnemyBattleCrushingAttack(mode) &&
                mode.SourceDefinition is not MonsterAttackDefinition { AfterChargeOnly: true } &&
                mode.EffectDescription.FindFirstDamageForm().DamageType is DamageTypeSlashing or DamageTypeBludgeoning)
            .OrderByDescending(mode => mode.ReachRange)
            .ThenByDescending(mode => GetEnemyBattleEffectAverageDamage(mode.EffectDescription)).FirstOrDefault();

    private static bool IsEnemyBattleArmorRendBody(RulesetCharacterMonster monster) =>
        monster.TryGetAttributeValue(AttributeDefinitions.Strength) >= 14 &&
        GetEnemyBattleArmorRendSource(monster) != null && GetEnemyBattleWhirlwindTargetLimit(monster) > 0;

    private static int GetEnemyBattleWhirlwindTargetLimit(RulesetCharacter actor) => actor == null ? 3 :
        Math.Min(3, (int)Math.Floor(GetEnemyBattleNativeMainDamage(actor) * 0.75d / 3.5d));

    private static int GetEnemyBattleWhirlwindDiceNumber(RulesetCharacter actor, int count) => actor == null ? 1 :
        Math.Min(GetEnemyBattleChallengeTier(actor),
            (int)Math.Floor(GetEnemyBattleNativeMainDamage(actor) * 0.75d / (3.5d * Math.Max(1, count))));

    private static string FormatEnemyBattleWhirlwindDamage(RulesetCharacter actor)
    {
        var maximum = GetEnemyBattleWhirlwindDiceNumber(actor, 1);
        var minimum = GetEnemyBattleWhirlwindDiceNumber(actor, Math.Max(1, GetEnemyBattleWhirlwindTargetLimit(actor)));
        return minimum == maximum ? $"{minimum}d6" : $"{minimum}d6–{maximum}d6";
    }

    private static EffectDescription BuildEnemyBattleWhirlwindDescription(RulesetCharacter actor, int count)
    {
        var mode = GetEnemyBattleArmorRendSource(actor);
        var saveDc = actor == null ? 10 : 8 + actor.TryGetProficiencyBonus() +
            AttributeDefinitions.ComputeAbilityScoreModifier(actor.TryGetAttributeValue(AttributeDefinitions.Strength));
        return EffectDescriptionBuilder.Create()
            .SetTargetingData(Side.Enemy, RangeType.Distance, Math.Max(1, mode?.ReachRange ?? 1),
                TargetType.IndividualsUnique, Math.Max(1, count))
            .SetDurationData(DurationType.Instantaneous)
            .SetSavingThrowData(false, AttributeDefinitions.Strength, false,
                EffectDifficultyClassComputation.FixedValue, AttributeDefinitions.Strength, saveDc)
            .SetEffectForms(
                EffectFormBuilder.Create().SetDamageForm(mode?.EffectDescription.FindFirstDamageForm().DamageType ??
                    DamageTypeBludgeoning, GetEnemyBattleWhirlwindDiceNumber(actor, count), DieType.D6).Build(),
                EffectFormBuilder.Create().SetMotionForm(MotionForm.MotionType.FallProne, 0)
                    .HasSavingThrow(EffectSavingThrowType.Negates).Build()).Build();
    }

    private static FeatureDefinitionPower BuildEnemyBattleArmorRendPower(string name)
    {
        const EnemyBattleAffix kind = EnemyBattleAffix.ArmorRend;
        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetShowCasting(false).SetEffectDescription(BuildEnemyBattleWhirlwindDescription(null, 3))
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance,
                PowerBundle.UncachedEffectDescription.Marker, new ValidatorsValidatePowerUse(_ => false)).AddToDB();
        power.AddCustomSubFeatures(new EnemyBattleArmorRend(power));
        return power;
    }

    private sealed class EnemyBattleArmorRendContext(CharacterAction trigger, GameLocationCharacter actor,
        GameLocationCharacter attacker, RulesetAttackMode nativeMode, GameLocationBattleManager battleManager,
        RulesetUsablePower usable, GameLocationCharacter[] targets)
    {
        internal CharacterAction Trigger { get; } = trigger;
        internal GameLocationCharacter Actor { get; } = actor;
        internal GameLocationCharacter Attacker { get; } = attacker;
        internal RulesetAttackMode SourceMode { get; } = nativeMode.Clone();
        internal EnemyBattleNativeModeSignature Signature { get; } = new(nativeMode);
        internal GameLocationBattleManager BattleManager { get; } = battleManager;
        internal RulesetUsablePower Usable { get; } = usable;
        internal GameLocationCharacter[] Targets { get; } = targets;
        internal int3[] Positions { get; } = targets.Select(target => target.LocationPosition).ToArray();
        internal int3 ActorPosition { get; } = actor.LocationPosition;
        internal GameLocationBattle Battle { get; } = Gui.Battle;
        internal int Round { get; } = GetCurrentBattleRound();
        internal int Turn { get; } = ObservedCombatMemoryTurnStamp;
        internal int Dice { get; } = GetEnemyBattleWhirlwindDiceNumber(actor.RulesetCharacter, targets.Length);
        internal int Index { get; set; }
        internal bool Accepted { get; set; }
        internal bool Consumed { get; set; }
        internal CharacterActionParams Parameters { get; set; }
    }

    private static bool IsCurrentEnemyBattleWhirlwind(EnemyBattleArmorRendContext context)
    {
        var actor = context.Actor;
        if (!Main.Settings.EnableEnemyBattleTactics || context.Battle == null ||
            !ReferenceEquals(context.Battle, Gui.Battle) || context.Round != GetCurrentBattleRound() ||
            context.Turn != ObservedCombatMemoryTurnStamp ||
            !ReferenceEquals(ServiceRepository.GetService<IGameLocationBattleService>(), context.BattleManager) ||
            context.Trigger is not CharacterActionAttack { Aborted: false, Countered: false } ||
            actor?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false,
                IsRemovedFromTheGame: false } source || actor.IsMyTurn() || actor.IsCharging || actor.MoveStepInProgress ||
            !EnemyBattleWhirlwinds.TryGetValue(source, out var registered) || registered != context ||
            actor.LocationPosition != context.ActorPosition ||
            !context.Accepted && actor.GetActionTypeStatus(ActionType.Reaction, ActionScope.Battle) != ActionStatus.Available ||
            !source.AttackModes.Any(mode => IsEnemyBattleCrushingAttack(mode) && context.Signature.Matches(mode)) ||
            !GetEnemyBattleEffectiveSelection(actor).Contains(EnemyBattleAffix.ArmorRend) ||
            !EnemyBattleAffixes.TryGetValue(EnemyBattleAffix.ArmorRend, out var entry) ||
            FindEnemyBattleCondition(source, entry.Condition) == null ||
            context.Usable.PowerDefinition != entry.Power ||
            !AddUsablePowersFromCondition.GetGrantedUsablePowers(source).Contains(context.Usable) ||
            !context.Consumed && context.Usable.RemainingUses <= 0)
        {
            return false;
        }
        return true;
    }

    private static bool TryGetEnemyBattleWhirlwindModifier(EnemyBattleArmorRendContext context, int index,
        out ActionModifier modifier)
    {
        modifier = null;
        if (!IsCurrentEnemyBattleWhirlwind(context) || index < 0 || index >= context.Targets.Length)
        {
            return false;
        }
        var actor = context.Actor;
        var target = context.Targets[index];
        if (target?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false } ||
            !context.Battle.AllContenders.Contains(target) || !actor.IsOppositeSide(target.Side) ||
            IsNonOccupyingCombatProxyTarget(target) || !actor.CanSeeTarget(target) ||
            target.LocationPosition != context.Positions[index])
        {
            return false;
        }
        // The untagged native source avoids recursing through the delegated attack validator.
        return TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, target,
            target.LocationPosition, context.SourceMode, context.BattleManager, out modifier);
    }

    private static bool IsValidEnemyBattleWhirlwindAction(CharacterActionAttack action) =>
        action?.ActingCharacter?.RulesetCharacter is { } source &&
        EnemyBattleWhirlwinds.TryGetValue(source, out var context) && context.Accepted &&
        action.ActionId == Id.AttackFree && action.ActionType == ActionType.NoCost &&
        ReferenceEquals(action.ActionParams, context.Parameters) &&
        context.Index >= 0 && context.Index < context.Targets.Length && action.ActionParams.TargetCharacters.Count == 1 &&
        action.ActionParams.TargetCharacters[0] == context.Targets[context.Index] &&
        TryGetEnemyBattleWhirlwindModifier(context, context.Index, out _);

    private static void EndEnemyBattleWhirlwind(EnemyBattleArmorRendContext context)
    {
        if (EnemyBattleWhirlwinds.TryGetValue(context.Actor.RulesetCharacter, out var current) && current == context)
        {
            EnemyBattleWhirlwinds.Remove(context.Actor.RulesetCharacter);
            context.SourceMode.Return();
        }
    }

    private static bool TryExecuteEnemyBattleWhirlwind(EnemyBattleArmorRendContext context)
    {
        if (ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService)
        {
            return false;
        }
        while (context.Index < context.Targets.Length)
        {
            if (!TryGetEnemyBattleWhirlwindModifier(context, context.Index, out var modifier))
            {
                context.Index++;
                continue;
            }
            var mode = context.SourceMode.Clone();
            mode.actionType = ActionType.NoCost;
            mode.attacksNumber = 1;
            mode.AddAttackTagAsNeeded(AttacksOfOpportunity.NotAoOTag);
            mode.AddAttackTagAsNeeded(EnemyBattleWhirlwindTag);
            var description = BuildEnemyBattleWhirlwindDescription(context.Actor.RulesetCharacter, context.Targets.Length);
            description.FindFirstDamageForm().DiceNumber = context.Dice;
            mode.EffectDescription.Copy(description);
            var parameters = new CharacterActionParams(context.Actor, Id.AttackFree, mode,
                context.Targets[context.Index], modifier);
            context.Parameters = parameters;
            try
            {
                actionService.ExecuteAction(parameters, _ =>
                {
                    mode.Return();
                    // Covers invalid/aborted actions that never reach the normal completion hook.
                    if (ReferenceEquals(context.Parameters, parameters))
                    {
                        EndEnemyBattleWhirlwind(context);
                    }
                }, true);
                return true;
            }
            catch
            {
                mode.Return();
                EndEnemyBattleWhirlwind(context);
                throw;
            }
        }
        return false;
    }

    private sealed class EnemyBattleArmorRend(FeatureDefinitionPower power) : IPhysicalAttackFinishedOnMe,
        IPhysicalAttackInitiatedByMe, IActionFinishedByMe, IModifyEffectDescription
    {
        private static readonly ConditionalWeakTable<CharacterAction, HashSet<ulong>> Triggered = new();

        internal bool IsReactionUseful(ReactionRequest request) =>
            request is ReactionRequestCustom custom && custom.EffectDefinition == power &&
            request.Character?.RulesetCharacter is { } source && EnemyBattleWhirlwinds.TryGetValue(source, out var context) &&
            !context.Accepted && TryGetEnemyBattleWhirlwindModifier(context, 0, out _);

        public bool IsValid(BaseDefinition definition, RulesetCharacter actor, EffectDescription description) =>
            definition == power;

        public EffectDescription GetEffectDescription(BaseDefinition definition, EffectDescription description,
            RulesetCharacter actor, RulesetEffect effect) =>
            BuildEnemyBattleWhirlwindDescription(actor, EnemyBattleWhirlwinds.TryGetValue(actor, out var context) ?
                context.Targets.Length : Math.Max(1, GetEnemyBattleWhirlwindTargetLimit(actor)));

        public IEnumerator OnPhysicalAttackFinishedOnMe(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, RulesetAttackMode attackMode,
            RollOutcome outcome, int damageAmount)
        {
            if (action is not CharacterActionAttack { Aborted: false, Countered: false } ||
                attackMode is not { Ranged: false, Thrown: false } ||
                attackMode.AttackTags.Contains(EnemyBattleWhirlwindTag) ||
                defender?.RulesetCharacter == null || attacker?.RulesetCharacter == null || defender.IsMyTurn() ||
                !defender.CanReact() || !defender.IsOppositeSide(attacker.Side) ||
                EnemyBattleWhirlwinds.TryGetValue(defender.RulesetCharacter, out _) ||
                !CanUseEnemyBattlePower(defender, EnemyBattleAffix.ArmorRend, out var usable) ||
                GetEnemyBattleArmorRendSource(defender.RulesetCharacter) is not { } nativeMode ||
                !Triggered.GetOrCreateValue(action).Add(defender.Guid))
            {
                yield break;
            }
            bool CanSweep(GameLocationCharacter target)
            {
                if (target?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false } ||
                    !defender.IsOppositeSide(target.Side) || !defender.CanSeeTarget(target) ||
                    IsNonOccupyingCombatProxyTarget(target))
                {
                    return false;
                }
                return TryGetPhysicalAttackModifierFromPosition(defender, defender.LocationPosition,
                    target, target.LocationPosition, nativeMode, battleManager, out _);
            }
            if (Gui.Battle == null || !CanSweep(attacker))
            {
                yield break;
            }
            var targets = new[] { attacker }.Concat(Gui.Battle.AllContenders.Where(target => target != attacker && CanSweep(target))
                    .OrderBy(target => ComputeGridDistance(defender.LocationPosition, target.LocationPosition)).ThenBy(target => target.Guid))
                .Distinct().Take(GetEnemyBattleWhirlwindTargetLimit(defender.RulesetCharacter)).ToArray();
            var context = new EnemyBattleArmorRendContext(action, defender, attacker, nativeMode, battleManager, usable, targets);
            EnemyBattleWhirlwinds.Add(defender.RulesetCharacter, context);
            var queued = false;
            try
            {
                yield return defender.MyReactToDoNothing(ExtraActionId.DoNothingReaction, attacker,
                    "EnemyBattleArmorRend", FormatEnemyBattleDescription(EnemyBattleAffix.ArmorRend, defender.RulesetCharacter),
                    reactionValidated: () => context.Accepted = true, battleManager: battleManager,
                    resource: new ReactionResourcePowerPool(power),
                    target: attacker, effectDefinition: power,
                    reactionStillValid: () => TryGetEnemyBattleWhirlwindModifier(context, 0, out _));
                if (context.Accepted)
                {
                    queued = TryExecuteEnemyBattleWhirlwind(context);
                }
            }
            finally
            {
                if (!queued)
                {
                    EndEnemyBattleWhirlwind(context);
                }
            }
        }

        public IEnumerator OnPhysicalAttackInitiatedByMe(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier, RulesetAttackMode attackMode)
        {
            if (attackMode.AttackTags.Contains(EnemyBattleWhirlwindTag) && action is CharacterActionAttack attack &&
                IsValidEnemyBattleWhirlwindAction(attack) && EnemyBattleWhirlwinds.TryGetValue(attacker.RulesetCharacter, out var context) &&
                !context.Consumed)
            {
                context.Consumed = true;
                attacker.RulesetCharacter.UsePower(context.Usable);
                attacker.RulesetCharacter.LogCharacterUsedFeature(power);
            }
            yield break;
        }

        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (action.ActingCharacter?.RulesetCharacter is { } source && EnemyBattleWhirlwinds.TryGetValue(source, out var context) &&
                ReferenceEquals(context.Parameters, action.ActionParams))
            {
                context.Index++;
                if (action.Aborted || action.Countered || !context.Consumed || !TryExecuteEnemyBattleWhirlwind(context))
                {
                    EndEnemyBattleWhirlwind(context);
                }
            }
            yield break;
        }
    }

    private static FeatureDefinitionPower BuildEnemyBattlePreparedAttackPower(EnemyBattleAffix kind, string name)
    {
        var condition = ConditionDefinitionBuilder.Create($"ConditionEnemyBattle{kind}Effect")
            .SetGuiPresentation($"Feature/&EnemyBattle{kind}EffectTitle", $"Feature/&EnemyBattle{kind}EffectDescription",
                GetEnemyBattleEffectSprite(kind))
            .SetConditionType(ConditionType.Beneficial).SetPossessive()
            .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
            .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance,
                BuildEnemyBattleFormattedDescription(kind, activeEffect: true)).AddToDB();
        var forms = new List<EffectForm> { EffectFormBuilder.ConditionForm(condition) };
        forms.Add(EffectFormBuilder.ConditionForm(ConditionDefinitions.ConditionDashingBonus));
        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetShowCasting(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Round, 0, TurnOccurenceType.EndOfTurn)
                .SetEffectForms(forms.ToArray()).Build())
            .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance,
                new ValidatorsValidatePowerUse(actor =>
                    !actor.HasConditionOfType(condition.Name) &&
                    (actor.IsTouchingGround() &&
                        !actor.HasConditionOfTypeOrSubType(ConditionDefinitions.ConditionDashingBonus.Name))))
            .AddToDB();
        condition.AddCustomSubFeatures(new EnemyBattlePreparedAttack(kind, condition, power));
        return power;
    }

    private sealed class EnemyBattleChargeMovement
    {
        internal int3 Direction;
        internal int3 LastDestination;
        internal int Steps;
    }

    private sealed class EnemyBattlePreparedAttack(EnemyBattleAffix kind, ConditionDefinition condition,
        FeatureDefinitionPower power, string coatingElement = null) : IPhysicalAttackBeforeHitConfirmedOnEnemy,
        IMoveStepStarted, IOnConditionAddedOrRemoved
    {
        private readonly ConditionalWeakTable<RulesetCharacter, EnemyBattleChargeMovement> _movement = new();

        public void OnConditionAdded(RulesetCharacter actor, RulesetCondition added)
        {
            _movement.Remove(actor);
            if (IsEnemyBattleWeaponCoating(kind))
            {
                added.Amount = GetEnemyBattleCapabilityDamageDiceNumber(actor, kind);
            }
        }
        public void OnConditionRemoved(RulesetCharacter actor, RulesetCondition removed) => _movement.Remove(actor);

        public void MoveStepStarted(GameLocationCharacter mover, int3 source, int3 destination)
        {
            if (kind != EnemyBattleAffix.ChargingRush || !mover.IsMyTurn())
            {
                return;
            }
            var movement = _movement.GetOrCreateValue(mover.RulesetCharacter);
            var delta = destination - source;
            var direction = new int3(System.Math.Sign(delta.x), 0, System.Math.Sign(delta.z));
            if (delta.y != 0 || direction == int3.zero || System.Math.Abs(delta.x) > 1 || System.Math.Abs(delta.z) > 1)
            {
                movement.Steps = 0;
            }
            else
            {
                movement.Steps = movement.Direction == direction && movement.LastDestination == source ? movement.Steps + 1 : 1;
            }
            movement.Direction = direction;
            // A cancelled step cannot qualify: the hit hook also checks the actual position.
            movement.LastDestination = destination;
        }

        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier,
            RulesetAttackMode attackMode, bool rangedAttack, AdvantageType advantageType,
            List<EffectForm> actualEffectForms, bool firstTarget, bool criticalHit)
        {
            var actor = attacker?.RulesetCharacter;
            if (actor == null || actualEffectForms == null || attackMode == null ||
                !Main.Settings.EnableEnemyBattleTactics || !IsEnemyBattleRecipient(attacker, Gui.Battle ?? _enemyTacticsBattle) ||
                FindEnemyBattleCondition(actor, EnemyBattleAffixes[kind].Condition) == null ||
                FindEnemyBattleCondition(actor, condition) is not { } active ||
                IsLimitedEnemyBattleNativeAttack(attackMode))
            {
                yield break;
            }
            if (kind == EnemyBattleAffix.ChargingRush)
            {
                if (rangedAttack || attackMode.Thrown || !attacker.IsMyTurn() || !actor.IsTouchingGround() ||
                    defender == null || !_movement.TryGetValue(actor, out var movement) || movement.Steps < 2 ||
                    movement.LastDestination != attacker.LocationPosition)
                {
                    yield break;
                }
                var direction = movement.Direction;
                var aligned = false;
                foreach (var cell in defender.LocationBattleBoundingBox.EnumerateAllPositionsWithin())
                {
                    var delta = cell - attacker.LocationPosition;
                    if (delta.y == 0 && delta.x * direction.z == delta.z * direction.x &&
                        delta.x * direction.x + delta.z * direction.z > 0)
                    {
                        aligned = true;
                        break;
                    }
                }
                if (!aligned)
                {
                    yield break;
                }
                actualEffectForms.Add(EffectFormBuilder.DamageForm(DamageTypeBludgeoning,
                    GetEnemyBattleCapabilityDamageDiceNumber(actor, kind), DieType.D6));
                actualEffectForms.Add(BuildEnemyBattleProneForm(actor, power));
            }
            else
            {
                if (!IsEnemyBattleWeaponAttack(attackMode))
                {
                    yield break;
                }
                actualEffectForms.Add(EffectFormBuilder.DamageForm(coatingElement,
                    GetEnemyBattleCoatingDiceNumber(active, actor), DieType.D4));
            }
            // Spend the prepared effect at a confirmed hit, so a miss retains the coating/charge.
            actor.RemoveCondition(active);
            actor.LogCharacterUsedFeature(power);
        }
    }

    private static EffectForm BuildEnemyBattleProneForm(RulesetCharacter actor, FeatureDefinitionPower power)
    {
        var prone = EffectFormBuilder.Create().SetMotionForm(MotionForm.MotionType.FallProne).Build();
        prone.SavingThrowAffinity = EffectSavingThrowType.Negates;
        prone.OverrideSavingThrowInfo = new OverrideSavingThrowInfo(AttributeDefinitions.Strength,
            8 + actor.TryGetProficiencyBonus() + AttributeDefinitions.ComputeAbilityScoreModifier(
                actor.TryGetAttributeValue(AttributeDefinitions.Strength)), power.Name, FeatureSourceType.CharacterFeature);
        return prone;
    }

    private static FeatureDefinitionPower BuildEnemyBattleBonusPower(EnemyBattleAffix kind, string name)
    {
        if (IsEnemyBattleSpellCastingCapability(kind))
        {
            return BuildEnemyBattleSpellCastingPower(kind, name);
        }
        if (IsEnemyBattleWeaponCoating(kind))
        {
            return BuildEnemyBattleWeaponCoatingPower(name);
        }
        if (kind == EnemyBattleAffix.ChargingRush)
        {
            return BuildEnemyBattlePreparedAttackPower(kind, name);
        }
        if (kind == EnemyBattleAffix.ImprovisedThrow)
        {
            var donor = GetDefinition<MonsterAttackDefinition>("Attack_Ape_Toss_Rock");
            _enemyBattleImprovisedThrow = MonsterAttackDefinitionBuilder.Create(donor, "AttackEnemyBattleImprovisedThrow")
                .SetGuiPresentation($"EnemyBattle{kind}", Category.Feature, GetEnemyBattleStatusSprite(kind))
                .SetEffectDescription(EffectDescriptionBuilder.Create(donor.EffectDescription)
                    .SetDurationData(DurationType.Instantaneous).ClearEffectAdvancements()
                    .SetEffectForms(EffectFormBuilder.DamageForm(DamageTypeBludgeoning, 1, DieType.D4)).Build())
                .SetToHitBonus(0).AddToDB();
            _enemyBattleImprovisedThrow.actionType = ActionType.Bonus;
            _enemyBattleImprovisedThrow.closeRange = 4;
            _enemyBattleImprovisedThrow.maxRange = 12;
            return CreateEnemyBattlePowerBuilder(kind, name)
                .SetShowCasting(false)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 12, TargetType.IndividualsUnique, 1)
                    .SetDurationData(DurationType.Instantaneous)
                    .SetEffectForms(EffectFormBuilder.DamageForm(DamageTypeBludgeoning, 1, DieType.D4)).Build())
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance).AddToDB();
        }
        if (kind is EnemyBattleAffix.CrushingBlow or EnemyBattleAffix.PiercingJab)
        {
            return BuildEnemyBattleFamilyPower(kind, name);
        }
        if (kind is EnemyBattleAffix.GuardedStance or EnemyBattleAffix.SecondWind or EnemyBattleAffix.Rally or
            EnemyBattleAffix.SteadyingWord or EnemyBattleAffix.PressingAttack or EnemyBattleAffix.UnsettlingCry or
            EnemyBattleAffix.HeroicRally)
        {
            return BuildEnemyBattleUtilityPower(kind, name);
        }
        if (HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.FamilyPower))
        {
            return BuildEnemyBattleFamilyPower(kind, name);
        }
        if (kind == EnemyBattleAffix.DrivingShove)
        {
            // This marker describes the granted native action; it is never executed as an empty power.
            return CreateEnemyBattlePowerBuilder(kind, name)
                .SetShowCasting(false)
                .SetEffectDescription(BuildEnemyBattleMeleeMarkerDescription())
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance)
                .AddToDB();
        }

        if (kind is EnemyBattleAffix.Feint or EnemyBattleAffix.Dread or EnemyBattleAffix.Command or
            EnemyBattleAffix.Restoration or EnemyBattleAffix.ArcaneHindrance)
        {
            return BuildEnemyBattleUtilityPower(kind, name);
        }

        var reference = kind switch
        {
            EnemyBattleAffix.Withdrawal => FeatureDefinitionPowers.PowerMonkStepOftheWindDisengage,
            EnemyBattleAffix.HuntingRush => FeatureDefinitionPowers.PowerMonkStepOfTheWindDash,
            EnemyBattleAffix.Resolve => FeatureDefinitionPowers.PowerMonkPatientDefense,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var cleansingMarker = kind == EnemyBattleAffix.Resolve ? BuildEnemyBattleCleanseMarker(kind) : null;
        var effect = kind == EnemyBattleAffix.Resolve
            ? EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Instantaneous)
                .SetEffectForms(EffectFormBuilder.ConditionForm(cleansingMarker))
                .SetParticleEffectParameters(reference)
                .SetAnimationMagicEffect(reference.EffectDescription.AnimationMagicEffect)
                .Build()
            : EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Round, 0, TurnOccurenceType.EndOfTurn)
                .SetEffectForms(EffectFormBuilder.ConditionForm(kind == EnemyBattleAffix.Withdrawal
                    ? ConditionDefinitions.ConditionDisengaging
                    : ConditionDefinitions.ConditionDashingBonus))
                .SetParticleEffectParameters(reference)
                .SetAnimationMagicEffect(reference.EffectDescription.AnimationMagicEffect)
                .Build();

        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetEffectDescription(effect)
            .SetShowCasting(true)
            .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance)
            .AddToDB();

        if (kind == EnemyBattleAffix.Resolve)
        {
            power.AddCustomSubFeatures(new ValidatorsValidatePowerUse(HasEnemyBattleResolveAffliction),
                new EnemyBattleCleanse(power, cleansingMarker, ConditionDefinitions.ConditionFrightened));
        }

        return power;
    }

    private static FeatureDefinitionPower BuildEnemyBattleLegendaryResolvePower(string name)
    {
        var power = CreateEnemyBattlePowerBuilder(EnemyBattleAffix.LegendaryResolve, name)
            .SetShowCasting(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Instantaneous).Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance,
                new ValidatorsValidatePowerUse(_ => false))
            .AddToDB();
        power.AddCustomSubFeatures(new EnemyBattleSavingThrowReaction(power));
        return power;
    }

    private static bool IsValidEnemyBattleSavingThrowReaction(GameLocationCharacter actor, SavingThrowData savingThrowData) =>
        savingThrowData?.IsFailedSavingThrowOutcome() == true && IsEnemyBattleReactionReady(actor) &&
        CanUseEnemyBattlePower(actor, EnemyBattleAffix.LegendaryResolve, out _);

    private sealed class EnemyBattleSavingThrowReaction(FeatureDefinitionPower power) : ITryAlterOutcomeSavingThrow
    {
        public IEnumerator OnTryAlterOutcomeSavingThrow(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, GameLocationCharacter helper,
            SavingThrowData savingThrowData, bool hasHitVisual)
        {
            if (helper != defender || defender?.CanReact() != true ||
                !IsValidEnemyBattleSavingThrowReaction(defender, savingThrowData) ||
                !CanUseEnemyBattlePower(defender, EnemyBattleAffix.LegendaryResolve, out var usable))
            {
                yield break;
            }

            var character = defender.RulesetCharacter;
            EnemyBattlePendingSavingThrows.Remove(character);
            EnemyBattlePendingSavingThrows.Add(character, savingThrowData);
            try
            {
                yield return defender.MyReactToDoNothing(ExtraActionId.DoNothingReaction, defender,
                    "EnemyBattleLegendaryResolve", FormatEnemyBattleDescription(EnemyBattleAffix.LegendaryResolve, character),
                    ReactionValidated, battleManager: battleManager,
                    resource: new ReactionResourcePowerPool(power),
                    target: defender, effectDefinition: power);
            }
            finally
            {
                EnemyBattlePendingSavingThrows.Remove(character);
            }

            yield break;

            void ReactionValidated()
            {
                // The native reaction action has already paid its action cost.
                if (!savingThrowData.IsFailedSavingThrowOutcome() ||
                    !CanUseEnemyBattlePower(defender, EnemyBattleAffix.LegendaryResolve, out var current) || current != usable)
                {
                    return;
                }

                character.UsePower(usable);
                savingThrowData.SaveOutcome = RollOutcome.Success;
                savingThrowData.SaveOutcomeDelta = 0;
                savingThrowData.UpdateActionSaveOutcome();
                character.LogCharacterUsedFeature(power);
            }
        }
    }

    private static FeatureDefinitionPower BuildEnemyBattleUtilityPower(EnemyBattleAffix kind, string name)
    {
        var animationReference = kind switch
        {
            EnemyBattleAffix.Feint or EnemyBattleAffix.GuardedStance or EnemyBattleAffix.PressingAttack =>
                FeatureDefinitionPowers.PowerFighterActionSurge.EffectDescription,
            EnemyBattleAffix.Command or EnemyBattleAffix.Rally or EnemyBattleAffix.HeroicRally =>
                FeatureDefinitionPowers.PowerKnightLeadership.EffectDescription,
            EnemyBattleAffix.Dread => FeatureDefinitionPowers.PowerDragonFrightfulPresence.EffectDescription,
            EnemyBattleAffix.Restoration or EnemyBattleAffix.SecondWind => HealingWord.EffectDescription,
            EnemyBattleAffix.SteadyingWord => Bless.EffectDescription,
            EnemyBattleAffix.UnsettlingCry => ViciousMockery.EffectDescription,
            EnemyBattleAffix.ArcaneHindrance => Bane.EffectDescription,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var self = kind is EnemyBattleAffix.Feint or EnemyBattleAffix.GuardedStance or
            EnemyBattleAffix.SecondWind or EnemyBattleAffix.PressingAttack;
        var currentTurn = kind is EnemyBattleAffix.Feint or EnemyBattleAffix.PressingAttack;
        var harmful = kind is EnemyBattleAffix.Dread or EnemyBattleAffix.ArcaneHindrance or EnemyBattleAffix.UnsettlingCry;
        var effect = EffectDescriptionBuilder.Create()
            .SetAnimationMagicEffect(animationReference.AnimationMagicEffect)
            .SetTargetingData(harmful ? Side.Enemy : Side.Ally,
                self ? RangeType.Self : RangeType.Distance,
                self ? 0 : kind == EnemyBattleAffix.Command ? 12 : kind == EnemyBattleAffix.Dread ? 3 : 6,
                self ? TargetType.Self : TargetType.IndividualsUnique,
                kind == EnemyBattleAffix.HeroicRally ? 3 : 1)
            .SetDurationData(DurationType.Round, currentTurn ? 0 : 1,
                currentTurn ? TurnOccurenceType.EndOfTurn :
                    (TurnOccurenceType)ExtraTurnOccurenceType.StartOfSourceTurn);
        var sprite = GetEnemyBattleStatusSprite(kind);

        if (kind is EnemyBattleAffix.Restoration or EnemyBattleAffix.SecondWind)
        {
            effect.AddImmuneCreatureFamilies(CharacterFamilyDefinitions.Undead, CharacterFamilyDefinitions.Construct)
                .SetDurationData(DurationType.Instantaneous)
                .SetEffectForms(EffectFormBuilder.Create()
                    .SetHealingForm(HealingComputation.Dice, 2, DieType.D6, 1, false, HealingCap.MaximumHitPoints)
                    .Build())
                .SetParticleEffectParameters(HealingWord);
        }
        else if (kind == EnemyBattleAffix.Rally)
        {
            effect.SetEffectForms(EffectFormBuilder.Create().SetTempHpForm(2, DieType.D4, 1).Build())
                .SetParticleEffectParameters(FeatureDefinitionPowers.PowerKnightLeadership);
        }
        else
        {
            var conditionName = $"ConditionEnemyBattle{kind}Effect";
            var parent = kind == EnemyBattleAffix.Dread ? ConditionDefinitions.ConditionFrightened :
                kind == EnemyBattleAffix.UnsettlingCry ? GetDefinition<ConditionDefinition>("ConditionMocked") : null;
            var condition = parent != null
                ? ConditionDefinitionBuilder.Create(parent, conditionName)
                    .SetParentCondition(parent)
                    .SetFeatures()
                : ConditionDefinitionBuilder.Create(conditionName);
            condition.SetGuiPresentation($"Feature/&EnemyBattle{kind}EffectTitle",
                    $"Feature/&EnemyBattle{kind}EffectDescription", GetEnemyBattleEffectSprite(kind))
                .SetConditionType(harmful ? ConditionType.Detrimental : ConditionType.Beneficial)
                .SetPossessive()
                .AddSpecialInterruptions(ConditionInterruption.BattleEnd)
                .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance);

            if (kind == EnemyBattleAffix.Feint)
            {
                condition.AddCustomSubFeatures(new EnemyBattleFeint($"ConditionEnemyBattle{kind}Effect"))
                    .SetSpecialInterruptions(ExtraConditionInterruption.AttacksWithWeaponOrUnarmed)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.PressingAttack)
            {
                condition.AddCustomSubFeatures(new EnemyBattleDamageBonus(conditionName))
                    .SetSpecialInterruptions(ExtraConditionInterruption.AttacksWithWeaponOrUnarmed)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.GuardedStance)
            {
                condition.SetFeatures(FeatureDefinitionAttributeModifierBuilder.Create($"AttributeModifier{name}")
                        .SetGuiPresentationNoContent(true)
                        .SetModifier(FeatureDefinitionAttributeModifier.AttributeModifierOperation.Additive,
                            AttributeDefinitions.ArmorClass, 2).AddToDB())
                    .SetSpecialInterruptions(ExtraConditionInterruption.AfterWasAttacked)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.SteadyingWord)
            {
                effect.ExcludeCaster();
                condition.SetFeatures(FeatureDefinitionSavingThrowAffinityBuilder.Create($"SavingThrowAffinity{name}")
                        .SetGuiPresentationNoContent(true)
                        .SetModifiers(FeatureDefinitionSavingThrowAffinity.ModifierType.AddDice, DieType.D4, 1, false,
                            AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution,
                            AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma)
                        .AddToDB())
                    .SetSpecialInterruptions(ConditionInterruption.SavingThrow)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.HeroicRally)
            {
                condition.SetFeatures(FeatureDefinitionSavingThrowAffinityBuilder.Create($"SavingThrowAffinity{name}")
                        .SetGuiPresentationNoContent(true)
                        .SetAffinities(CharacterSavingThrowAffinity.Advantage, false,
                            AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution,
                            AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma)
                        .AddToDB())
                    .SetSpecialInterruptions(ConditionInterruption.SavingThrow)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.UnsettlingCry)
            {
                condition.SetSpecialInterruptions(ConditionInterruption.Attacks)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.Command)
            {
                condition.SetFeatures(FeatureDefinitionCombatAffinityBuilder.Create($"CombatAffinity{name}")
                        .SetGuiPresentationNoContent(true)
                        .SetMyAttackAdvantage(AdvantageType.Advantage)
                        .AddToDB())
                    .SetSpecialInterruptions(ConditionInterruption.Attacks)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.ArcaneHindrance)
            {
                condition.SetFeatures(FeatureDefinitionMagicAffinityBuilder.Create($"MagicAffinity{name}")
                    .SetGuiPresentationNoContent(true)
                    .SetConcentrationModifiers(ConcentrationAffinity.Disadvantage)
                    .AddToDB());
            }

            var appliedCondition = condition.AddToDB();
            // Utility effects own their duration, including conditions copied from native templates.
            appliedCondition.specialDuration = false;
            var form = EffectFormBuilder.Create()
                .SetConditionForm(appliedCondition, ConditionForm.ConditionOperation.Add);
            if (harmful)
            {
                form.HasSavingThrow(EffectSavingThrowType.Negates);
                effect.SetSavingThrowData(false,
                    kind == EnemyBattleAffix.Dread ? AttributeDefinitions.Charisma : AttributeDefinitions.Wisdom, false,
                    kind == EnemyBattleAffix.ArcaneHindrance ? EffectDifficultyClassComputation.SpellCastingFeature :
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency,
                    AttributeDefinitions.Charisma);
            }

            effect.SetEffectForms(form.Build());
            if (kind == EnemyBattleAffix.HeroicRally)
            {
                effect.SetEffectForms(form.Build(), EffectFormBuilder.Create().SetTempHpForm(2, DieType.D6, 2).Build());
            }
            if (kind is EnemyBattleAffix.Feint or EnemyBattleAffix.GuardedStance or EnemyBattleAffix.PressingAttack)
            {
                effect.SetParticleEffectParameters(FeatureDefinitionPowers.PowerFighterActionSurge);
            }
            else if (kind is EnemyBattleAffix.Command or EnemyBattleAffix.HeroicRally)
            {
                effect.SetParticleEffectParameters(FeatureDefinitionPowers.PowerKnightLeadership);
            }
            else
            {
                effect.SetParticleEffectParameters(kind == EnemyBattleAffix.Dread ? SpellDefinitions.Fear :
                    kind == EnemyBattleAffix.SteadyingWord ? Bless : kind == EnemyBattleAffix.UnsettlingCry ? ViciousMockery : Bane);
            }
        }

        var power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
            .SetShowCasting(true)
            .SetEffectDescription(effect.Build())
            .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance)
            .AddToDB();
        power.AddCustomSubFeatures(new EnemyBattleUtilityPower(kind, power));
        if (kind is EnemyBattleAffix.Restoration or EnemyBattleAffix.SecondWind or EnemyBattleAffix.Rally or EnemyBattleAffix.HeroicRally)
        {
            power.AddCustomSubFeatures(new EnemyBattleFamilyAnimation(kind, power));
        }
        return power;
    }

    private static FeatureDefinitionPower BuildEnemyBattleFamilyPower(EnemyBattleAffix kind, string name)
    {
        var sprite = GetEnemyBattleStatusSprite(kind);
        var metadata = GetEnemyBattleCapabilityMetadata(kind);
        if (kind is EnemyBattleAffix.RadiantSmite or EnemyBattleAffix.CrushingBlow)
        {
            var smite = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                .SetShowCasting(false).SetEffectDescription(BuildEnemyBattleMeleeMarkerDescription())
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance,
                    new ValidatorsValidatePowerUse(_ => false)).AddToDB();
            smite.AddCustomSubFeatures(new EnemyBattleOnHitBonus(smite, kind));
            return smite;
        }
        if (kind == EnemyBattleAffix.PredatoryLunge)
        {
            return CreateEnemyBattlePowerBuilder(kind, name, sprite)
                .SetShowCasting(false).SetEffectDescription(BuildEnemyBattleMeleeMarkerDescription())
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance).AddToDB();
        }

        var self = kind is EnemyBattleAffix.FeyVeil or EnemyBattleAffix.ConstructBrace;
        var ally = kind is EnemyBattleAffix.PackCall or EnemyBattleAffix.PurifyingTouch;
        var area = IsEnemyBattleAreaDamage(kind);
        var ranged = IsEnemyBattleRangedDamage(kind);
        var range = ranged ? 12 :
            kind is EnemyBattleAffix.PackCall or EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.PsychicBacklash ? 6 :
            kind is EnemyBattleAffix.WingBuffet or EnemyBattleAffix.StoneTremor or EnemyBattleAffix.CausticRetort ? 2 :
            kind is EnemyBattleAffix.PurifyingTouch or EnemyBattleAffix.ViscousSnare ? 1 :
            kind == EnemyBattleAffix.GraveLeech ? 2 : 3;
        var donor = kind switch
        {
            EnemyBattleAffix.PackCall => FeatureDefinitionPowers.PowerKnightLeadership.EffectDescription,
            EnemyBattleAffix.FeyVeil => Invisibility.EffectDescription,
            EnemyBattleAffix.FiendMiasma => GetDefinition<FeatureDefinitionPower>("PowerHezrouPoisonBolt").EffectDescription,
            EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.PsychicBacklash => ViciousMockery.EffectDescription,
            EnemyBattleAffix.ConstructBrace => GetDefinition<FeatureDefinitionPower>("PowerStoneGolemSlow").EffectDescription,
            EnemyBattleAffix.WingBuffet => GetDefinition<FeatureDefinitionPower>("PowerDragonWingAttack").EffectDescription,
            EnemyBattleAffix.PrimordialRepulsion or EnemyBattleAffix.StoneTremor => Thunderwave.EffectDescription,
            EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.PiercingJab => new EffectDescription { effectParticleParameters = new EffectParticleParameters() },
            EnemyBattleAffix.CausticRetort => Grease.EffectDescription,
            EnemyBattleAffix.RootSnare => Entangle.EffectDescription,
            EnemyBattleAffix.WitheringMark => RayOfFrost.EffectDescription,
            EnemyBattleAffix.PurifyingTouch => LesserRestoration.EffectDescription,
            EnemyBattleAffix.BlindingEmbers => FireBolt.EffectDescription,
            EnemyBattleAffix.LightningSurge => LightningBolt.EffectDescription,
            EnemyBattleAffix.ThunderousRoar => Thunderwave.EffectDescription,
            EnemyBattleAffix.ForcePulse => MagicMissile.EffectDescription,
            EnemyBattleAffix.FocusedVolley or EnemyBattleAffix.SweepingVolley =>
                new EffectDescription { effectParticleParameters = new EffectParticleParameters() },
            EnemyBattleAffix.ElementalLance => FireBolt.EffectDescription,
            EnemyBattleAffix.GraveLeech => ChillTouch.EffectDescription,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var effect = EffectDescriptionBuilder.Create()
            .SetTargetingData(self || ally ? Side.Ally : Side.Enemy, self ? RangeType.Self :
                IsEnemyBattleRangedSpellAttack(kind) ? RangeType.RangeHit : RangeType.Distance,
                self ? 0 : range, self ? TargetType.Self : TargetType.IndividualsUnique,
                kind == EnemyBattleAffix.PackCall ? 2 : 1)
            .SetDurationData(DurationType.Round, 1, (TurnOccurenceType)ExtraTurnOccurenceType.StartOfSourceTurn)
            .SetAnimationMagicEffect(donor.AnimationMagicEffect).SetParticleEffectParameters(donor.EffectParticleParameters);

        ConditionDefinition cleansingMarker = null;
        if (area)
        {
            effect.SetTargetingData(Side.Enemy, RangeType.Self, 0, TargetType.Sphere, 1)
                .ExcludeCaster().SetDurationData(DurationType.Instantaneous)
                .SetEffectForms(EffectFormBuilder.Create().SetDamageForm(GetEnemyBattleDamageType(kind), 1, DieType.D6)
                    .HasSavingThrow(EffectSavingThrowType.HalfDamage).Build());
            if (kind == EnemyBattleAffix.ForcePulse)
            {
                effect.SetCasterEffectParameters(Thunderwave);
            }
        }
        else if (ranged)
        {
            effect.SetDurationData(DurationType.Instantaneous).SetEffectForms();
        }
        else if (kind == EnemyBattleAffix.GraveLeech)
        {
            effect.SetDurationData(DurationType.Instantaneous).SetEffectForms();
        }
        else if (kind == EnemyBattleAffix.PurifyingTouch)
        {
            cleansingMarker = BuildEnemyBattleCleanseMarker(kind);
            effect.SetDurationData(DurationType.Instantaneous).SetEffectForms(EffectFormBuilder.ConditionForm(cleansingMarker));
        }
        else if (kind is EnemyBattleAffix.WingBuffet or EnemyBattleAffix.PrimordialRepulsion or EnemyBattleAffix.StoneTremor)
        {
            effect.SetDurationData(DurationType.Instantaneous)
                .SetEffectForms(EffectFormBuilder.Create()
                    .SetMotionForm(kind == EnemyBattleAffix.StoneTremor ? MotionForm.MotionType.FallProne :
                        MotionForm.MotionType.PushFromOrigin, kind == EnemyBattleAffix.StoneTremor ? 0 : 1)
                    .HasSavingThrow(EffectSavingThrowType.Negates).Build());
        }
        else
        {
            var conditionName = $"ConditionEnemyBattle{kind}Effect";
            var parent = kind switch
            {
                EnemyBattleAffix.FeyVeil => ConditionDefinitions.ConditionInvisible,
                EnemyBattleAffix.FiendMiasma => ConditionDefinitions.ConditionPoisoned,
                EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.LightningSurge => ConditionDefinitions.ConditionShocked,
                EnemyBattleAffix.ConstructBrace => ConditionDefinitions.ConditionDodging,
                EnemyBattleAffix.RootSnare => ConditionDefinitions.ConditionRestrained,
                EnemyBattleAffix.PsychicBacklash or EnemyBattleAffix.PiercingJab => GetDefinition<ConditionDefinition>("ConditionMocked"),
                EnemyBattleAffix.BlindingEmbers => ConditionDefinitions.ConditionBlinded,
                _ => null
            };
            var condition = parent == null ? ConditionDefinitionBuilder.Create(conditionName) :
                ConditionDefinitionBuilder.Create(parent, conditionName).SetParentCondition(parent).SetFeatures();
            condition.SetGuiPresentation($"Feature/&EnemyBattle{kind}EffectTitle", $"Feature/&EnemyBattle{kind}EffectDescription",
                    GetEnemyBattleEffectSprite(kind))
                .SetConditionType(self || ally ? ConditionType.Beneficial : ConditionType.Detrimental)
                .SetSpecialDuration(DurationType.Round, 1, (TurnOccurenceType)ExtraTurnOccurenceType.StartOfSourceTurn)
                .SetPossessive().AddSpecialInterruptions(ConditionInterruption.BattleEnd)
                .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance);
            if (kind == EnemyBattleAffix.PackCall)
            {
                condition.AddCustomSubFeatures(new EnemyBattleFeint($"ConditionEnemyBattle{kind}Effect"))
                    .SetSpecialInterruptions(ExtraConditionInterruption.AttacksWithWeaponOrUnarmed)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind == EnemyBattleAffix.PiercingJab)
            {
                condition.SetSpecialInterruptions(ConditionInterruption.Attacks)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd);
            }
            else if (kind is EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.CausticRetort)
            {
                condition.SetFeatures(FeatureDefinitionMovementAffinityBuilder.Create($"MovementAffinity{name}")
                    .SetGuiPresentationNoContent(true).SetBaseSpeedMultiplicativeModifier(0.5f).AddToDB());
            }
            else if (kind == EnemyBattleAffix.WitheringMark)
            {
                condition.SetFeatures(FeatureDefinitionHealingModifiers.HealingModifierChilledByTouch);
            }
            var form = EffectFormBuilder.Create().SetConditionForm(condition.AddToDB(), ConditionForm.ConditionOperation.Add);
            if (!self && !ally && kind != EnemyBattleAffix.PiercingJab)
            {
                form.HasSavingThrow(EffectSavingThrowType.Negates);
            }
            effect.SetEffectForms(form.Build());
        }
        if (!area && GetEnemyBattleDamageType(kind) is { } damageType)
        {
            var riderForms = effect.Build().EffectForms.ToArray();
            var damage = EffectFormBuilder.Create().SetDamageForm(damageType, 1,
                kind == EnemyBattleAffix.PiercingJab ? DieType.D4 : DieType.D6);
            if (!ranged && kind != EnemyBattleAffix.PiercingJab)
            {
                damage.HasSavingThrow(EffectSavingThrowType.HalfDamage);
            }
            effect.SetEffectForms(new[] { damage.Build() }.Concat(riderForms).ToArray());
        }
        if (!self && !ally && !ranged && kind != EnemyBattleAffix.PiercingJab)
        {
            var save = kind is EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.PsychicBacklash ? AttributeDefinitions.Intelligence :
                kind is EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.CausticRetort or EnemyBattleAffix.WingBuffet or
                    EnemyBattleAffix.LightningSurge or EnemyBattleAffix.ForcePulse ?
                    AttributeDefinitions.Dexterity :
                kind is EnemyBattleAffix.RootSnare or EnemyBattleAffix.PrimordialRepulsion or EnemyBattleAffix.StoneTremor ?
                    AttributeDefinitions.Strength : AttributeDefinitions.Constitution;
            var dc = kind is EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.PsychicBacklash ? AttributeDefinitions.Intelligence :
                kind is EnemyBattleAffix.WingBuffet or EnemyBattleAffix.RootSnare or EnemyBattleAffix.StoneTremor ?
                    AttributeDefinitions.Strength : AttributeDefinitions.Constitution;
            effect.SetSavingThrowData(false, save, false, EffectDifficultyClassComputation.AbilityScoreAndProficiency, dc);
        }
        var reaction = metadata.ActivationTime == ActivationTime.Reaction;
        var powerBuilder = CreateEnemyBattlePowerBuilder(kind, name, sprite)
            .SetShowCasting(kind != EnemyBattleAffix.PiercingJab).SetEffectDescription(effect.Build());
        if (IsEnemyBattleRangedSpellAttack(kind))
        {
            powerBuilder.SetBonusToAttack(true, true, AttributeDefinitions.Constitution);
        }
        var power = powerBuilder.AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance).AddToDB();
        if (kind == EnemyBattleAffix.PurifyingTouch)
        {
            power.AddCustomSubFeatures(new EnemyBattleCleanse(power, cleansingMarker,
                ConditionDefinitions.ConditionCharmed, ConditionDefinitions.ConditionFrightened, ConditionDefinitions.ConditionPoisoned));
        }
        if (kind == EnemyBattleAffix.FeyVeil)
        {
            power.AddCustomSubFeatures(IgnoreInvisibilityInterruptionCheck.Marker);
        }
        if (reaction)
        {
            power.AddCustomSubFeatures(ModifyPowerVisibility.Hidden, new EnemyBattleReactionPower(kind, power));
            if (GetEnemyBattleDamageType(kind) != null)
            {
                power.AddCustomSubFeatures(new EnemyBattleFamilyAnimation(kind, power));
            }
            if (area)
            {
                power.AddCustomSubFeatures(PowerBundle.UncachedEffectDescription.Marker);
            }
            if (kind == EnemyBattleAffix.GraveLeech)
            {
                power.AddCustomSubFeatures(new EnemyBattleGraveLeech(power));
            }
        }
        else
        {
            power.AddCustomSubFeatures(new EnemyBattleUtilityPower(kind, power), new EnemyBattleFamilyAnimation(kind, power));
        }
        return power;
    }

    private static string GetEnemyBattleDamageType(EnemyBattleAffix kind) => kind switch
    {
        EnemyBattleAffix.FiendMiasma => DamageTypePoison,
        EnemyBattleAffix.WitheringMark => DamageTypeCold,
        EnemyBattleAffix.BlindingEmbers => DamageTypeFire,
        EnemyBattleAffix.ThoughtLock => DamageTypePsychic,
        EnemyBattleAffix.LightningSurge => DamageTypeLightning,
        EnemyBattleAffix.CausticRetort => DamageTypeAcid,
        EnemyBattleAffix.ThunderousRoar => DamageTypeThunder,
        EnemyBattleAffix.ForcePulse => DamageTypeForce,
        EnemyBattleAffix.RadiantBolt => DamageTypeRadiant,
        EnemyBattleAffix.WingBuffet or EnemyBattleAffix.ImprovisedThrow or EnemyBattleAffix.ChargingRush or EnemyBattleAffix.ArmorRend => DamageTypeBludgeoning,
        EnemyBattleAffix.ViscousSnare => DamageTypeSlashing,
        EnemyBattleAffix.PiercingJab => DamageTypePiercing,
        EnemyBattleAffix.FocusedVolley or EnemyBattleAffix.SweepingVolley => DamageTypePiercing,
        EnemyBattleAffix.ElementalLance => DamageTypeFire,
        EnemyBattleAffix.GraveLeech => DamageTypeNecrotic,
        EnemyBattleAffix.RadiantSmite => DamageTypeRadiant,
        EnemyBattleAffix.CrushingBlow => DamageTypeBludgeoning,
        _ => null
    };

    private static int GetEnemyBattleChallengeTier(RulesetCharacter actor) => GetEnemyBattleChallengeRating(actor) switch
    {
        >= 17 => 4,
        >= 11 => 3,
        >= 5 => 2,
        _ => 1
    };

    private static int GetEnemyBattleDamageDiceNumber(RulesetCharacter actor, DieType dieType = DieType.D6)
    {
        return GetEnemyBattleDamageDiceNumberForTargetCount(actor, 1, dieType);
    }

    private static int GetEnemyBattlePressingAttackDamageBonus(RulesetCharacter actor) =>
        2 * GetEnemyBattleChallengeTier(actor);

    private static int GetEnemyBattleCapabilityDamageDiceNumber(RulesetCharacter actor, EnemyBattleAffix kind)
    {
        if (kind == EnemyBattleAffix.ArmorRend)
        {
            return GetEnemyBattleWhirlwindDiceNumber(actor, actor != null && EnemyBattleWhirlwinds.TryGetValue(actor, out var context)
                ? context.Targets.Length : Math.Max(1, GetEnemyBattleWhirlwindTargetLimit(actor)));
        }
        if (IsEnemyBattleWeaponCoating(kind))
        {
            return GetEnemyBattleDamageDiceNumber(actor, DieType.D4);
        }
        if (kind == EnemyBattleAffix.ChargingRush)
        {
            return System.Math.Min(1, GetEnemyBattleDamageDiceNumber(actor));
        }
        if (kind == EnemyBattleAffix.RadiantBolt)
        {
            return GuidingBolt.EffectDescription.FindFirstDamageForm().DiceNumber;
        }
        if (kind == EnemyBattleAffix.SweepingVolley)
        {
            var count = actor != null && EnemyBattleNativeAttackTargets.TryGetValue(actor, out var pending) && pending.Kind == kind
                ? Math.Max(1, pending.Targets.Length) : 3;
            return GetEnemyBattleSweepingVolleyDiceNumber(actor, count);
        }
        if (kind is EnemyBattleAffix.ImprovisedThrow or EnemyBattleAffix.PiercingJab)
        {
            return GetEnemyBattleNativeMainDamage(actor) >= 5d ? 1 : 0;
        }
        if (GetEnemyBattleCapabilityTier(kind) == 3)
        {
            // A three-slot damage/control ability has a larger burst, still bounded by its native Main action.
            return System.Math.Min(GetEnemyBattleChallengeTier(actor),
                (int)System.Math.Floor(GetEnemyBattleNativeMainDamage(actor) * 0.75d / 3.5d));
        }
        if (kind == EnemyBattleAffix.GraveLeech)
        {
            // Damage plus at most half as much healing is 5.25 mean per d6.
            // Both together stay within half the native Main damage budget.
            return System.Math.Min(GetEnemyBattleChallengeTier(actor),
                (int)System.Math.Floor(GetEnemyBattleNativeMainDamage(actor) / 10.5d));
        }
        var dice = GetEnemyBattleDamageDiceNumber(actor);
        return IsEnemyBattleRangedDamage(kind) ? System.Math.Min(dice, (GetEnemyBattleChallengeTier(actor) + 1) / 2) : dice;
    }

    private static int GetEnemyBattleDamageDiceNumberForTargetCount(RulesetCharacter actor, int targetCount,
        DieType dieType = DieType.D6)
    {
        targetCount = System.Math.Max(1, targetCount);
        var nativeMainDamage = GetEnemyBattleNativeMainDamage(actor);
        // Additional action damage stays at most half a native main action's raw average.
        // No accuracy, vulnerability or critical-hit assumptions enter this budget.
        // The same total budget is shared by all actual area targets, never multiplied by their number.
        return nativeMainDamage <= 0 ? 0 : System.Math.Min(GetEnemyBattleChallengeTier(actor),
            (int)System.Math.Floor(nativeMainDamage / ((DiceMaxValue[(int)dieType] + 1d) * targetCount)));
    }

    private static int GetEnemyBattleSweepingVolleyDiceNumber(RulesetCharacter actor, int targetCount) =>
        actor == null ? 1 : Math.Min(GetEnemyBattleChallengeTier(actor),
            (int)Math.Floor(GetEnemyBattleNativeMainDamage(actor) * 0.75d / (3.5d * Math.Max(1, targetCount))));

    private static string FormatEnemyBattleSweepingVolleyDamage(RulesetCharacter actor)
    {
        var minimum = GetEnemyBattleSweepingVolleyDiceNumber(actor, 3);
        var maximum = GetEnemyBattleSweepingVolleyDiceNumber(actor, 1);
        return minimum == maximum ? $"{minimum}d6" : $"{minimum}d6–{maximum}d6";
    }

    private static bool IsEnemyBattleAreaDamage(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.AreaDamage);

    private static GameLocationCharacter[] GetEnemyBattleAreaTargets(GameLocationCharacter actor, EnemyBattleAffix kind)
    {
        if (actor?.RulesetCharacter == null || !IsEnemyBattleAreaDamage(kind) ||
            (Gui.Battle ?? _enemyTacticsBattle) is not { } battle || !EnemyBattleAffixes.TryGetValue(kind, out var entry))
        {
            return [];
        }
        var targeting = ServiceRepository.GetService<IGameLocationTargetingService>();
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        if (targeting == null || positioning == null)
        {
            return [];
        }
        var description = entry.Power.EffectDescription;
        var center = positioning.GetWorldPositionFromGridPosition(actor.LocationPosition);
        var origin = new Vector3();
        var direction = new Vector3();
        targeting.ComputeTargetingParameters(center, actor, actor.LocationPosition, description.ShapeType,
            description.RangeType, ref origin, ref direction);
        var affected = new List<GameLocationCharacter>();
        var radius = GetEnemyBattleAreaBroadphaseRadius(actor);
        targeting.ComputeTargetsOfAreaOfEffect(origin, direction, center, description.ShapeType, actor.Side,
            description, radius, description.TargetParameter2, affected, false, actor,
            new List<int3>(), groundOnly: description.AffectOnlyGround);
        return affected.Where(target => battle.AllContenders.Contains(target))
            .Where(target => target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                actor.IsOppositeSide(target.Side) && actor.IsWithinRange(target, 1) && actor.CanSeeTarget(target) &&
                HasUsefulEnemyBattleDamage(target.RulesetCharacter, entry.Power.EffectDescription))
            .Distinct().OrderBy(target => target.Guid).ToArray();
    }

    private static int GetEnemyBattleAreaBroadphaseRadius(GameLocationCharacter actor)
    {
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        if (actor == null || positioning == null)
        {
            return 1;
        }
        var center = new Vector3();
        positioning.ComputeCastingCenterPosition(actor, ref center);
        var extent = 0f;
        foreach (var cell in actor.LocationBattleBoundingBox.EnumerateAllPositionsWithin())
        {
            extent = Mathf.Max(extent, Vector3.Distance(center, positioning.GetWorldPositionFromGridPosition(cell)));
        }
        // The native sphere discovers the whole body and its neighboring cell
        // boxes. The shared native one-cell body-distance filter is authoritative.
        return Mathf.CeilToInt(extent) + 2;
    }

    private static EnemyBattleAreaSnapshot GetEnemyBattleAreaSnapshot(RulesetCharacter actor, EnemyBattleAffix kind)
    {
        if (actor == null)
        {
            return new EnemyBattleAreaSnapshot(kind, [], 1);
        }
        if (EnemyBattleAreaPending.TryGetValue(actor, out var snapshot) && snapshot.Kind == kind)
        {
            return snapshot;
        }
        var targets = GetEnemyBattleAreaTargets(GameLocationCharacter.GetFromActor(actor), kind);
        return new EnemyBattleAreaSnapshot(kind, targets, GetEnemyBattleDamageDiceNumberForTargetCount(actor, targets.Length));
    }

    private static int GetEnemyBattleAreaDamageDiceNumber(RulesetCharacter actor, EnemyBattleAffix kind,
        RulesetEffect effect = null) => effect == null ? GetEnemyBattleAreaSnapshot(actor, kind).DiceNumber :
        EnemyBattleAreaEffects.GetValue(effect, _ => GetEnemyBattleAreaSnapshot(actor, kind)).DiceNumber;

    private static EnemyBattleAreaSnapshot GetEnemyBattleAreaEffectSnapshot(RulesetCharacter actor,
        EnemyBattleAffix kind, RulesetEffect effect) =>
        EnemyBattleAreaEffects.GetValue(effect, _ => GetEnemyBattleAreaSnapshot(actor, kind));

    private static bool IsUsefulEnemyBattleAreaDamage(GameLocationCharacter actor, EnemyBattleAffix kind)
    {
        if (actor?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } ||
            kind == EnemyBattleAffix.ThunderousRoar &&
            actor.RulesetCharacter.HasConditionOfTypeOrSubType("ConditionSilenced"))
        {
            return false;
        }
        var snapshot = GetEnemyBattleAreaSnapshot(actor.RulesetCharacter, kind);
        return snapshot.Targets.Length > 0 && snapshot.DiceNumber > 0;
    }

    private static double GetEnemyBattleNativeMainDamage(RulesetCharacter actor)
    {
        // Limited breaths, daily powers and leveled spells cannot establish the
        // budget for an extra attack repeated alongside the body's ordinary turn.
        var attackDamage = GetEnemyBattleOrdinaryAttackModes(actor).Where(mode => mode.ActionType == ActionType.Main &&
                mode.SourceDefinition is not MonsterAttackDefinition { LimitedUse: true })
            .Select(mode => GetEnemyBattleEffectAverageDamage(mode.EffectDescription) * System.Math.Max(1, mode.AttacksNumber))
            .DefaultIfEmpty().Max();
        var powerDamage = actor.FeaturesByType<FeatureDefinitionPower>()
            .Concat(AddUsablePowersFromCondition.GetGrantedUsablePowers(actor).Select(usable => usable.PowerDefinition))
            .Where(power => power.ActivationTime == ActivationTime.Action && power.RechargeRate == RechargeRate.AtWill &&
                !EnemyBattlePowerKinds.ContainsKey(power))
            .Select(power => GetEnemyBattleEffectAverageDamage(power.EffectDescription)).DefaultIfEmpty().Max();
        var spellDamage = GetEnemyBattleOrdinarySpellRepertoires(actor).SelectMany(repertoire => repertoire.KnownCantrips.Concat(repertoire.KnownSpells)
                .Concat(repertoire.PreparedSpells).Concat(repertoire.AutoPreparedSpells)
                .Concat(repertoire.ExtraSpellsByTag.Values.SelectMany(spells => spells)).Distinct()
                .Where(spell => spell.ActivationTime == ActivationTime.Action && spell.SpellLevel == 0)
                .Select(spell => GetEnemyBattleEffectAverageDamage(spell.EffectDescription, repertoire.SpellCastingLevel)))
            .DefaultIfEmpty().Max();
        return System.Math.Max(attackDamage, System.Math.Max(powerDamage, spellDamage));
    }

    private static double GetEnemyBattleEffectAverageDamage(EffectDescription effect, int casterLevel = 0)
    {
        if (effect?.EffectForms == null)
        {
            return 0;
        }
        var additionalDice = casterLevel > 0 && effect.HasCasterLevelAdvancement
            ? effect.EffectAdvancement.ComputeAdditionalDiceByCasterLevel(casterLevel) : 0;
        return effect.EffectForms.Where(form => form.FormType == EffectForm.EffectFormType.Damage)
            .Sum(form => System.Math.Max(0, form.DamageForm.BonusDamage +
                (form.DamageForm.DiceNumber + (form.DamageForm.IgnoreSpellAdvancementDamageDice ? 0 : additionalDice)) *
                (DiceMaxValue[(int)form.DamageForm.DieType] + 1) / 2d));
    }

    private sealed class EnemyBattleLifecycle : IOnReducedToZeroHp, ICharacterTurnStartListener
    {
        internal static readonly EnemyBattleLifecycle Instance = new();

        public void OnCharacterTurnStarted(GameLocationCharacter locationCharacter)
        {
            if (!Main.Settings.EnableEnemyBattleTactics ||
                !IsEnemyBattleRecipient(locationCharacter, Gui.Battle ?? _enemyTacticsBattle))
            {
                return;
            }
            var actor = locationCharacter.RulesetCharacter;
            // Native RechargePowersForTurnStart has already resolved each discharged
            // power's d6. Persist that result before a later grant reconciliation.
            CaptureEnemyBattleCharges(actor);
            if (GetEnemyBattleEffectiveSelection(locationCharacter).Any(IsEnemyBattleNativeBonusAttack))
            {
                actor.RefreshAttackModes();
            }
        }

        public IEnumerator HandleReducedToZeroHp(GameLocationCharacter attacker, GameLocationCharacter downedCreature,
            RulesetAttackMode attackMode, RulesetEffect activeEffect)
        {
            if (downedCreature?.RulesetCharacter is { } actor)
            {
                EnemyBattleRetort.ResolvePendingReflection(downedCreature);
                CaptureEnemyBattleCharges(actor);
                RemoveEnemyBattleCapabilities(actor, removeLedger: false);
            }
            yield break;
        }
    }

    private static ConditionDefinition BuildEnemyBattleCleanseMarker(EnemyBattleAffix kind) =>
        ConditionDefinitionBuilder.Create($"ConditionEnemyBattle{kind}Effect")
            .SetGuiPresentationNoContent(true).SetSilent(Silent.Always)
            .SetSpecialDuration(DurationType.Round, 1, (TurnOccurenceType)ExtraTurnOccurenceType.StartOfSourceTurn)
            .AddSpecialInterruptions(ConditionInterruption.BattleEnd).AddToDB();

    private static IEnumerable<RulesetCondition> GetEnemyBattlePurifiableConditions(RulesetCharacter actor,
        params ConditionDefinition[] families) =>
        actor.ConditionsByCategory.Where(category => category.Key is AttributeDefinitions.TagEffect or AttributeDefinitions.TagGadget)
            .SelectMany(category => category.Value).Where(condition =>
                condition.DurationType is not (DurationType.Irrelevant or DurationType.Permanent or DurationType.Instantaneous) &&
                condition.ConditionDefinition.ConditionType == ConditionType.Detrimental &&
                families.Any(family => condition.ConditionDefinition.IsSubtypeOf(family.Name)));

    private sealed class EnemyBattleCleanse(FeatureDefinitionPower power, ConditionDefinition marker,
        params ConditionDefinition[] families) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            if (definition == power && !action.Countered && !action.ExecutionFailed)
            {
                foreach (var target in action.ActionParams.TargetCharacters)
                {
                    var actor = target.RulesetCharacter;
                    var markers = actor.ConditionsByCategory.Values.SelectMany(conditions => conditions).Where(condition =>
                        condition.SourceGuid == action.ActingCharacter.Guid &&
                        condition.ConditionDefinition == marker).ToArray();
                    if (markers.Length == 0)
                    {
                        continue;
                    }
                    foreach (var detrimental in GetEnemyBattlePurifiableConditions(actor, families).ToArray())
                    {
                        actor.RemoveCondition(detrimental);
                    }
                    foreach (var ownedMarker in markers)
                    {
                        actor.RemoveCondition(ownedMarker);
                    }
                }
            }
            yield break;
        }
    }

    private sealed class EnemyBattleFamilyAnimation(EnemyBattleAffix kind, FeatureDefinitionPower power) : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription description) =>
            definition == power && character is RulesetCharacterMonster;

        public EffectDescription GetEffectDescription(BaseDefinition definition, EffectDescription description,
            RulesetCharacter character, RulesetEffect rulesetEffect)
        {
            var builder = EffectDescriptionBuilder.Create(description);
            var nativeAttacks = GetEnemyBattleOrdinaryAttackModes(character);
            if (kind is EnemyBattleAffix.Restoration or EnemyBattleAffix.SecondWind)
            {
                builder.SetEffectForms(EffectFormBuilder.Create().SetHealingForm(HealingComputation.Dice,
                    character.TryGetProficiencyBonus(), DieType.D6,
                    kind == EnemyBattleAffix.SecondWind ? 1 : GetEnemyBattleChallengeTier(character), false,
                    HealingCap.MaximumHitPoints).Build());
            }
            else if (kind == EnemyBattleAffix.Rally)
            {
                builder.SetEffectForms(EffectFormBuilder.Create()
                    .SetTempHpForm(character.TryGetProficiencyBonus(), DieType.D4, 1).Build());
            }
            else if (kind == EnemyBattleAffix.HeroicRally)
            {
                builder.SetEffectForms(description.EffectForms.Where(form =>
                        form.FormType != EffectForm.EffectFormType.TemporaryHitPoints)
                    .Concat(new[] { EffectFormBuilder.Create().SetTempHpForm(character.TryGetProficiencyBonus(), DieType.D6, 2).Build() })
                    .ToArray());
            }
            else if (GetEnemyBattleEffectiveDamageType(character, kind) is { } damageType)
            {
                var dice = IsEnemyBattleAreaDamage(kind) ? GetEnemyBattleAreaDamageDiceNumber(character, kind, rulesetEffect) :
                    GetEnemyBattleCapabilityDamageDiceNumber(character, kind);
                var forms = description.EffectForms.Where(form => form.FormType != EffectForm.EffectFormType.Damage);
                var damage = EffectFormBuilder.Create().SetDamageForm(damageType, dice,
                    kind == EnemyBattleAffix.PiercingJab ? DieType.D4 : DieType.D6);
                if (!IsEnemyBattleRangedDamage(kind) && kind != EnemyBattleAffix.PiercingJab)
                {
                    damage.HasSavingThrow(EffectSavingThrowType.HalfDamage);
                }
                builder.SetEffectForms(dice > 0 ? new[] { damage.Build() }.Concat(forms).ToArray() : forms.ToArray());
            }
            if (kind is EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.PiercingJab)
            {
                var slash = GetEnemyBattleNativeMeleeDamageMode(character, GetEnemyBattleDamageType(kind));
                if (slash != null)
                {
                    builder.SetTargetingData(Side.Enemy, RangeType.Distance, GetEnemyBattlePowerRange(character, kind),
                            TargetType.IndividualsUnique, 1)
                        .SetAnimationMagicEffect(slash.EffectDescription.AnimationMagicEffect)
                        .SetParticleEffectParameters(slash.EffectDescription.EffectParticleParameters);
                }
            }
            if (IsEnemyBattleAreaDamage(kind))
            {
                builder.SetTargetingData(Side.Enemy, RangeType.Self, 0, TargetType.Sphere,
                    GetEnemyBattleAreaBroadphaseRadius(GameLocationCharacter.GetFromActor(character))).ExcludeCaster();
            }
            if (IsEnemyBattleRangedDamage(kind) && GetEnemyBattleRangedSource(character, kind) is { } rangedSource)
            {
                builder.SetTargetingData(Side.Enemy, IsEnemyBattleRangedSpellAttack(kind) ? RangeType.RangeHit : RangeType.Distance,
                        rangedSource.Range, TargetType.IndividualsUnique, 1)
                    .SetAnimationMagicEffect(rangedSource.Description.AnimationMagicEffect)
                    .SetParticleEffectParameters(rangedSource.Description.EffectParticleParameters);
            }
            if (kind == EnemyBattleAffix.PackCall && nativeAttacks.Any(mode => !IsEnemyBattleWeaponAttack(mode)))
            {
                builder.SetAnimationMagicEffect(nativeAttacks.First(mode => !IsEnemyBattleWeaponAttack(mode))
                    .EffectDescription.AnimationMagicEffect);
            }
            if (kind is EnemyBattleAffix.LightningSurge or EnemyBattleAffix.ForcePulse)
            {
                // Preserve the body's own matching elemental attack/cast pose while retaining the new particles.
                var nativeDamageType = GetEnemyBattleDamageType(kind);
                var nativeAttack = nativeAttacks.Select(mode => mode.EffectDescription)
                    .Concat(character.FeaturesByType<FeatureDefinitionPower>()
                        .Where(native => !EnemyBattlePowerKinds.ContainsKey(native))
                        .Select(native => native.EffectDescription))
                    .Concat(GetEnemyBattleKnownSpells(character).Select(spell => spell.EffectDescription))
                    .FirstOrDefault(native => native.EffectForms.Any(form =>
                        form.FormType == EffectForm.EffectFormType.Damage && form.DamageForm.DamageType == nativeDamageType));
                if (nativeAttack != null)
                {
                    builder.SetAnimationMagicEffect(nativeAttack.AnimationMagicEffect);
                }
            }
            if (kind == EnemyBattleAffix.ThunderousRoar)
            {
                var nativeRoar = character.FeaturesByType<FeatureDefinitionPower>()
                    .Where(native => !EnemyBattlePowerKinds.ContainsKey(native))
                    .Select(native => native.EffectDescription)
                    .FirstOrDefault(native => native.TargetType == TargetType.Sphere &&
                        HasEnemyBattleAddedCondition(native, ConditionDefinitions.ConditionFrightened));
                var naturalAttack = nativeAttacks.FirstOrDefault(mode => !mode.Ranged && !IsEnemyBattleWeaponAttack(mode));
                if (nativeRoar != null || naturalAttack != null)
                {
                    builder.SetAnimationMagicEffect((nativeRoar ?? naturalAttack.EffectDescription).AnimationMagicEffect);
                }
            }
            return builder.Build();
        }
    }

    private sealed class EnemyBattleFeint(string conditionName) : IModifyAttackActionModifier
    {
        public void OnAttackComputeModifier(RulesetCharacter attacker, RulesetCharacter defender,
            BattleDefinitions.AttackProximity attackProximity, RulesetAttackMode attackMode, string effectName,
            ref ActionModifier attackModifier)
        {
            if (attackProximity is BattleDefinitions.AttackProximity.PhysicalReach or BattleDefinitions.AttackProximity.PhysicalRange)
            {
                attackModifier.AttackAdvantageTrends.Add(
                    new TrendInfo(1, FeatureSourceType.Condition, conditionName, null));
            }
        }
    }

    private sealed class EnemyBattleDamageBonus(string conditionName) : IPhysicalAttackDamageBonus
    {
        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier actionModifier,
            RulesetAttackMode attackMode, bool rangedAttack, AdvantageType advantageType,
            List<EffectForm> actualEffectForms, bool firstTarget, bool criticalHit)
        {
            if (attackMode != null && actualEffectForms != null && firstTarget &&
                EffectForm.GetFirstDamageForm(actualEffectForms) is { } damage &&
                !damage.DamageBonusTrends.Any(trend => trend.sourceName == conditionName))
            {
                // Modify this hit's copy only, once on its primary damage roll. Native attack
                // interruption consumes the condition after a hit or miss, as with Feint.
                var bonus = GetEnemyBattlePressingAttackDamageBonus(attacker.RulesetCharacter);
                damage.BonusDamage += bonus;
                damage.DamageBonusTrends.Add(new TrendInfo(bonus, FeatureSourceType.Condition, conditionName, null));
            }
            yield break;
        }
    }

    private sealed class EnemyBattleUtilityPower(EnemyBattleAffix kind, FeatureDefinitionPower power)
        : IValidatePowerUse, IFilterTargetingCharacter, IFilterRulesetEffectTargets,
            IValidateMagicEffectBeforeSpend, IMagicEffectBeforeHitConfirmedOnEnemy
    {
        public bool EnforceFullSelection => false;

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower definition)
        {
            var actor = GameLocationCharacter.GetFromActor(character);
            return definition == power && CanUseEnemyBattlePower(actor, kind, out _) &&
                   TryGetEnemyBattleUtilityTarget(actor, kind, out _, forAi: false);
        }

        public bool IsValid(CursorLocationSelectTarget cursor, GameLocationCharacter target)
        {
            if (IsValidEnemyBattleUtilityTarget(cursor.ActionParams.ActingCharacter, kind, target, forAi: false))
            {
                return true;
            }
            cursor.actionModifier.FailureFlags.Add("Failure/&EnemyBattleTargetNotUseful");
            return false;
        }

        public bool CanAffectTarget(RulesetEffect rulesetEffect, GameLocationCharacter caster, GameLocationCharacter target) =>
            rulesetEffect.GetSourceDefinitionSafe() != power ||
            IsValidEnemyBattleUtilityTarget(caster, kind, target, forAi: false);

        public bool IsValid(CharacterActionMagicEffect action, GameLocationCharacter actingCharacter,
            IReadOnlyList<GameLocationCharacter> targets, out string failure)
        {
            failure = "Failure/&EnemyBattleTargetNotUseful";
            if (!CanUseEnemyBattlePower(actingCharacter, kind, out _) || targets.Count == 0 ||
                targets.Count > power.EffectDescription.TargetParameter ||
                targets.Distinct().Count() != targets.Count ||
                targets.Any(target => !IsValidEnemyBattleUtilityTarget(actingCharacter, kind, target, forAi: false)))
            {
                return false;
            }
            failure = null;
            return true;
        }

        public IEnumerator OnMagicEffectBeforeHitConfirmedOnEnemy(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier actionModifier,
            RulesetEffect rulesetEffect, List<EffectForm> actualEffectForms, bool firstTarget, bool criticalHit)
        {
            if (kind is EnemyBattleAffix.WingBuffet or EnemyBattleAffix.PrimordialRepulsion &&
                rulesetEffect.GetSourceDefinitionSafe() == power && !IsUsefulEnemyBattlePush(attacker, defender, forAi: false))
            {
                // Damage can remain useful when the rider would push onto a hazard, blocked floor or unsafe height.
                actualEffectForms.RemoveAll(form => form.FormType == EffectForm.EffectFormType.Motion &&
                    form.MotionForm.Type == MotionForm.MotionType.PushFromOrigin);
            }
            yield break;
        }
    }

    internal static bool IsEnemyBattleTacticsPower(IMagicEffect magicEffect)
    {
        if (magicEffect is not FeatureDefinitionPower power)
        {
            return false;
        }

        return TryGetEnemyBattleCapabilityKind(GetEnemyBattleCapabilityPower(power), out _);
    }

    private static bool HasEnemyBattleResolveAffliction(RulesetCharacter actor)
    {
        if (actor == null)
        {
            return false;
        }

        return GetEnemyBattlePurifiableConditions(actor, ConditionDefinitions.ConditionFrightened).Any();
    }

    private static bool IsEnemyBattleSpellCastingCapability(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.SpellCasting);

    private static bool IsEnemyBattleGrantedSpellRepertoire(RulesetSpellRepertoire repertoire) =>
        repertoire?.SpellCastingFeature != null && EnemyBattleCastingFeatures.Contains(repertoire.SpellCastingFeature);

    private static IEnumerable<SpellDefinition> GetEnemyBattleActionCantrips(RulesetCharacter actor) =>
        GetEnemyBattleOrdinarySpellRepertoires(actor).SelectMany(repertoire => repertoire.EnumerateReadyCantrips())
            .Where(spell => spell.ActivationTime == ActivationTime.Action).Distinct();

    private static bool IsEnemyBattleAiCantripEffect(EffectDescription effect) =>
        effect.HasDamageForm() && effect.TargetSide == Side.Enemy &&
        effect.TargetType is TargetType.Individuals or TargetType.IndividualsUnique;

    private static bool HasEnemyBattleAiCantrip(RulesetCharacter actor) =>
        GetEnemyBattleActionCantrips(actor).Any(spell =>
            IsEnemyBattleAiCantripEffect(PowerBundle.ModifySpellEffect(spell, actor)));

    private static IEnumerable<RulesetSpellRepertoire> GetEnemyBattleNativeSpellRepertoires(RulesetCharacter actor)
    {
        if (actor is not RulesetCharacterMonster monster)
        {
            return [];
        }
        var nativeCasting = new HashSet<FeatureDefinitionCastSpell>(GetEnemyBattleNativeFeatures(monster)
            .OfType<FeatureDefinitionCastSpell>());
        return GetEnemyBattleOrdinarySpellRepertoires(actor).Where(repertoire => repertoire?.SpellCastingFeature != null &&
                nativeCasting.Contains(repertoire.SpellCastingFeature) &&
                !SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire))
            .OrderBy(repertoire => repertoire.SpellCastingFeature.Name, StringComparer.Ordinal);
    }

    private static bool HasEnemyBattleSpellFlightSource(RulesetCharacter actor) =>
        actor is RulesetCharacterMonster monster &&
        // Current MoveModes also contains temporary flight, including this
        // capability's own spell. Only intrinsic flight makes the grant redundant.
        !monster.MonsterDefinition.Features.SelectMany(GetEnemyBattleNestedFeatures)
            .OfType<FeatureDefinitionMoveMode>().Any(feature => feature.MoveMode == MoveMode.Fly) &&
        GetEnemyBattleNativeSpellRepertoires(actor).Any(repertoire => HasEnemyBattleSpellSlotCapacity(repertoire, 3));

    private static bool HasEnemyBattleSpellSlotCapacity(RulesetSpellRepertoire repertoire, int minimumLevel)
    {
        for (var level = minimumLevel; level <= 9; level++)
        {
            repertoire.GetSlotsNumber(level, out _, out var maximum);
            if (maximum > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void RefreshEnemyBattleSpellGrants(RulesetCharacter actor)
    {
        var repertoires = GetEnemyBattleOrdinarySpellRepertoires(actor);
        foreach (var grant in EnemyBattleFreeCasting)
        {
            var freeEntry = EnemyBattleAffixes[grant.Key];
            var own = repertoires.Where(rep => rep.SpellCastingFeature == grant.Value).ToArray();
            if (!Main.Settings.EnableEnemyBattleTactics || FindEnemyBattleCondition(actor, freeEntry.Condition) == null)
            {
                foreach (var repertoire in own)
                {
                    repertoires.Remove(repertoire);
                }
                continue;
            }
            var source = (grant.Key == EnemyBattleAffix.RadiantBolt ? GetEnemyBattleRadiantRepertoire(actor) : null) ??
                repertoires.Where(rep => rep.SpellCastingFeature != null && !IsEnemyBattleGrantedSpellRepertoire(rep))
                .OrderByDescending(rep => rep.SaveDC).ThenBy(rep => rep.SpellCastingFeature.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            var repertoireToKeep = own.FirstOrDefault();
            var changed = repertoireToKeep == null || own.Length > 1;
            foreach (var duplicate in own.Skip(1))
            {
                repertoires.Remove(duplicate);
            }
            if (repertoireToKeep == null)
            {
                repertoireToKeep = new RulesetSpellRepertoire
                {
                    spellCastingFeature = grant.Value, CharacterInventory = actor.CharacterInventory,
                    CharacterName = actor.Name, spellCastingMonster = (actor as RulesetCharacterMonster)?.MonsterDefinition
                };
                repertoires.Add(repertoireToKeep);
            }
            var level = EnemyBattleFreeSpells[grant.Key].SpellLevel;
            var innateAbility = grant.Key == EnemyBattleAffix.RadiantBolt
                ? GetEnemyBattleRangedSource(actor, grant.Key)?.Ability ?? AttributeDefinitions.Wisdom : AttributeDefinitions.Wisdom;
            if (!EnemyBattleCastingSources.TryGetValue(repertoireToKeep, out var previousSource) ||
                previousSource.Repertoire != source || previousSource.InnateAbility != innateAbility)
            {
                EnemyBattleCastingSources.Remove(repertoireToKeep);
                EnemyBattleCastingSources.Add(repertoireToKeep, new EnemyBattleCastingSource(source, innateAbility));
                changed = true;
            }
            var abilityBonus = source?.FormAbilityBonus ?? AttributeDefinitions.ComputeAbilityScoreModifier(
                actor.TryGetAttributeValue(innateAbility));
            var saveDc = source?.SaveDC ?? 8 + actor.TryGetProficiencyBonus() + abilityBonus;
            var spellAttackBonus = source?.SpellAttackBonus ?? actor.TryGetProficiencyBonus() + abilityBonus;
            var castingLevel = grant.Value.SpellCastingLevel;
            if (repertoireToKeep.FormAbilityBonus != abilityBonus || repertoireToKeep.SaveDC != saveDc ||
                repertoireToKeep.SpellAttackBonus != spellAttackBonus || repertoireToKeep.SpellCastingLevel != castingLevel)
            {
                repertoireToKeep.formAbilityBonus = abilityBonus;
                repertoireToKeep.saveDC = saveDc;
                repertoireToKeep.spellAttackBonus = spellAttackBonus;
                repertoireToKeep.spellCastingLevel = castingLevel;
                changed = true;
            }
            if (repertoireToKeep.KnownSpells.Count != 1 || repertoireToKeep.KnownSpells[0] != EnemyBattleFreeSpells[grant.Key])
            {
                repertoireToKeep.KnownSpells.Clear();
                repertoireToKeep.KnownSpells.Add(EnemyBattleFreeSpells[grant.Key]);
                changed = true;
            }
            var capacity = GetEnemyBattleCapabilityMetadata(grant.Key).MaxCharges;
            var used = GetEnemyBattleUsablePower(freeEntry.Power, actor).RemainingUses > 0 ? 0 : capacity;
            if (repertoireToKeep.spellsSlotCapacities.Count != 1 ||
                !repertoireToKeep.spellsSlotCapacities.TryGetValue(level, out var oldCapacity) || oldCapacity != capacity ||
                repertoireToKeep.usedSpellsSlots.Count != 1 ||
                !repertoireToKeep.usedSpellsSlots.TryGetValue(level, out var oldUsed) || oldUsed != used)
            {
                repertoireToKeep.spellsSlotCapacities.Clear();
                repertoireToKeep.spellsSlotCapacities[level] = capacity;
                repertoireToKeep.usedSpellsSlots.Clear();
                repertoireToKeep.usedSpellsSlots[level] = used;
                changed = true;
            }
            if (changed)
            {
                repertoireToKeep.RepertoireRefreshed?.Invoke(repertoireToKeep);
            }
        }

        if (_enemyBattleFlightSpell == null || !EnemyBattleAffixes.TryGetValue(EnemyBattleAffix.SpellFlight, out var entry))
        {
            return;
        }
        var hasGrant = Main.Settings.EnableEnemyBattleTactics && FindEnemyBattleCondition(actor, entry.Condition) != null;
        var nativeRepertoires = new HashSet<RulesetSpellRepertoire>(GetEnemyBattleNativeSpellRepertoires(actor));
        foreach (var repertoire in GetEnemyBattleOrdinarySpellRepertoires(actor))
        {
            var tag = entry.Power.Name;
            var shouldGrant = hasGrant && nativeRepertoires.Contains(repertoire) &&
                              HasEnemyBattleSpellSlotCapacity(repertoire, 3);
            var hasOwnTag = repertoire.ExtraSpellsByTag.TryGetValue(tag, out var granted);
            if (shouldGrant && (!hasOwnTag || granted.Count != 1 || granted[0] != _enemyBattleFlightSpell))
            {
                // This tag belongs only to the temporary capability; original readiness is untouched.
                repertoire.ExtraSpellsByTag[tag] = [_enemyBattleFlightSpell];
                repertoire.RepertoireRefreshed?.Invoke(repertoire);
            }
            else if (!shouldGrant && hasOwnTag)
            {
                repertoire.ExtraSpellsByTag.Remove(tag);
                repertoire.RepertoireRefreshed?.Invoke(repertoire);
            }
        }
    }

    private static FeatureDefinitionPower BuildEnemyBattleSpellCastingPower(EnemyBattleAffix kind, string name)
    {
        EffectDescription effect;
        if (kind == EnemyBattleAffix.QuickenedCantrip)
        {
            // Keep the saved definition resolvable while retiring its former casting restriction.
            var legacyRestriction = ConditionDefinitionBuilder.Create("ConditionEnemyBattleQuickenedCantripEffect")
                .SetGuiPresentationNoContent(true).SetSilent(Silent.Always)
                .SetConditionType(ConditionType.Beneficial).SetPossessive()
                .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
                .AddToDB();
            effect = EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Round, 0, TurnOccurenceType.EndOfTurn)
                .SetEffectForms(EffectFormBuilder.ConditionForm(legacyRestriction)).Build();
        }
        else if (kind == EnemyBattleAffix.SpellFlight)
        {
            // A self-only spell variant preserves ordinary concentration, components,
            // counterspell and slot payment without changing anyone's native Fly definition.
            _enemyBattleFlightSpell = SpellDefinitionBuilder.Create(Fly, "SpellEnemyBattleFlight")
                .SetGuiPresentation(Fly.GuiPresentation.Title, "Feature/&EnemyBattleSpellFlightDescription",
                    Fly.GuiPresentation.SpriteReference)
                .SetCastingTime(ActivationTime.BonusAction)
                .SetEffectDescription(EffectDescriptionBuilder.Create(Fly)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .ClearEffectAdvancements().Build())
                .AddCustomSubFeatures(RequireSpellCastingActionProvider.Marker).AddToDB();
            effect = _enemyBattleFlightSpell.EffectDescription;
        }
        else
        {
            effect = EffectDescriptionBuilder.Create(EnemyBattleFreeSpells[kind]).ClearEffectAdvancements().Build();
        }
        var castingPower = CreateEnemyBattlePowerBuilder(kind, name)
            .SetShowCasting(false).SetEffectDescription(effect)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, new ValidatorsValidatePowerUse(_ => false),
                Tooltips.HideHeaderImage.Instance).AddToDB();
        if (kind == EnemyBattleAffix.RadiantBolt)
        {
            castingPower.AddCustomSubFeatures(new EnemyBattleUtilityPower(kind, castingPower));
        }
        return castingPower;
    }

    private sealed class EnemyBattleSpellCasting(EnemyBattleAffix kind, FeatureDefinitionPower power)
        : ISpellCastingActionProvider, IValidateSpellCasting
    {
        public bool IsAllowed(RulesetCharacter character, RulesetSpellRepertoire repertoire,
            SpellDefinition spell, ActionType actionType)
        {
            if (SpellSelectionContext.TryGetOption(repertoire, out var option))
            {
                if (kind == EnemyBattleAffix.SpellFlight && option.IsFree)
                {
                    return false;
                }
                repertoire = option.Repertoire;
            }
            var actor = GameLocationCharacter.GetFromActor(character);
            var reaction = GetEnemyBattleCapabilityMetadata(kind).ActivationTime == ActivationTime.Reaction;
            if (actionType != (reaction ? ActionType.Reaction : ActionType.Bonus) ||
                !reaction && actor?.IsMyTurn() != true ||
                !CanUseEnemyBattlePower(actor, kind, out _) || repertoire == null ||
                !character.SpellRepertoires.Contains(repertoire))
            {
                return false;
            }
            if (EnemyBattleFreeSpells.TryGetValue(kind, out var freeSpell))
            {
                return freeSpell == spell && repertoire.SpellCastingFeature == EnemyBattleFreeCasting[kind] &&
                    (power.GetFirstSubFeatureOfType<EnemyBattleElementalAbsorption>() is not { } absorption ||
                     absorption.CanUsePower(character, power));
            }
            return kind == EnemyBattleAffix.QuickenedCantrip
                ? spell is { SpellLevel: 0, ActivationTime: ActivationTime.Action } &&
                  repertoire.EnumerateReadyCantrips().Any(known => known == spell ||
                      spell.SpellsBundle && SpellsContext.GetSubspellLeaves(spell).Contains(known))
                : spell == _enemyBattleFlightSpell &&
                  GetEnemyBattleNativeSpellRepertoires(character).Contains(repertoire) &&
                  HasEnemyBattleSpellSlotCapacity(repertoire, 3);
        }

        public bool CanCastSpellAsAction(CharacterActionMagicEffect action, RulesetEffectSpell spell, out string failure)
        {
            var character = action.ActingCharacter.RulesetCharacter;
            var repertoire = SpellCastingResourceContext.GetResourceRepertoire(spell) ?? spell.SpellRepertoire;
            if (!IsAllowed(character, repertoire, RulesetEffectSpellWithOrigin.GetOriginSpell(spell), action.ActionType))
            {
                failure = "Failure/&FailureFlagNoPowerUses";
                return false;
            }
            if (kind == EnemyBattleAffix.SpellFlight &&
                (spell.SlotLevel < 3 || spell.OriginItem != null ||
                 spell.RulesetInvocation?.InvocationDefinition is { ConsumesSpellSlot: false } ||
                 SpellCastingResourceContext.TryGetSelectionKind(spell, out var resource) &&
                 resource != SpellCastingResourceContext.ResourceKind.SpellSlot ||
                 action.ActionParams.TargetCharacters.Count > 1 ||
                 action.ActionParams.TargetCharacters.Any(target => target != action.ActingCharacter) ||
                 !repertoire.TryGetAvailableSlotLevel(character, spell.SlotLevel, null, out var available) || !available))
            {
                failure = "Failure/&EnemyBattleTargetNotUseful";
                return false;
            }
            if (EnemyBattleFreeSpells.ContainsKey(kind) &&
                (spell.SlotLevel != EnemyBattleFreeSpells[kind].SpellLevel || spell.OriginItem != null ||
                 SpellCastingResourceContext.TryGetSelectionKind(spell, out var freeResource) &&
                 freeResource != SpellCastingResourceContext.ResourceKind.FreeRepertoire ||
                 kind == EnemyBattleAffix.Spellbreaker && !IsUsefulEnemyCounterspell(action.ActingCharacter,
                     action.ActionParams.TargetAction) ||
                 kind == EnemyBattleAffix.ElementalAbsorption &&
                 !power.GetFirstSubFeatureOfType<EnemyBattleElementalAbsorption>()
                     .IsValid(action, action.ActingCharacter, action.ActionParams.TargetCharacters, out failure)))
            {
                failure = "Failure/&EnemyBattleTargetNotUseful";
                return false;
            }
            failure = string.Empty;
            return true;
        }

        public bool CanCastSpell(SpellCastingValidationContext context, out string failure)
        {
            failure = "Failure/&FailureFlagNoPowerUses";
            if (EnemyBattleFreeCasting.TryGetValue(kind, out var casting) && context.Repertoire?.SpellCastingFeature == casting &&
                !IsAllowed(context.Caster, context.Repertoire, context.SpellDefinition,
                    GetEnemyBattleCapabilityMetadata(kind).ActivationTime == ActivationTime.Reaction ? ActionType.Reaction : ActionType.Bonus))
            {
                return false;
            }
            failure = string.Empty;
            return true;
        }

        public void OnSpellCastCommitted(CharacterActionMagicEffect action, RulesetEffectSpell spell)
        {
            var character = action.ActingCharacter.RulesetCharacter;
            character.UsePower(GetEnemyBattleUsablePower(power, character));
            character.LogCharacterUsedFeature(power);
        }
    }

    private static bool TryExecuteEnemyBattleSpellAction(GameLocationCharacter actor, EnemyBattleAffix kind)
    {
        if (!CanStartEnemyBattleBonusAction(actor) || !CanUseEnemyBattlePower(actor, kind, out _) ||
            actor.GetActionStatus(Id.CastBonus, ActionScope.Battle) != ActionStatus.Available ||
            ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService ||
            ServiceRepository.GetService<IRulesetImplementationService>() is not { } implementationService ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            return false;
        }
        var character = actor.RulesetCharacter;
        var provider = EnemyBattleAffixes[kind].Condition.GetFirstSubFeatureOfType<EnemyBattleSpellCasting>();
        var targets = kind == EnemyBattleAffix.SpellFlight ? new[] { actor } : SnapshotCharacters(actor.PerceivedFoes)
            .Where(target => target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                             actor.IsOppositeSide(target.Side) && actor.CanSeeTarget(target))
            .OrderBy(target => ComputeGridDistance(actor.LocationPosition, target.LocationPosition))
            .ThenBy(target => target.Guid).ToArray();
        if (kind == EnemyBattleAffix.SpellFlight &&
            (character.ConcentratedSpell != null || character.MoveModes.ContainsKey((int)MoveMode.Fly) ||
             !SnapshotCharacters(actor.PerceivedFoes).Any(target => target?.RulesetCharacter is
                 { IsDeadOrDyingOrUnconscious: false } && actor.IsOppositeSide(target.Side))))
        {
            return false;
        }
        foreach (var repertoire in kind == EnemyBattleAffix.SpellFlight
                     ? GetEnemyBattleNativeSpellRepertoires(character) : character.SpellRepertoires)
        {
            IEnumerable<SpellDefinition> spells = kind == EnemyBattleAffix.SpellFlight ? new[] { _enemyBattleFlightSpell } :
                repertoire.EnumerateReadyCantrips().Where(spell => spell.ActivationTime == ActivationTime.Action)
                    .OrderBy(spell => spell.Name, StringComparer.Ordinal);
            foreach (var spell in spells)
            {
                if (!provider.IsAllowed(character, repertoire, spell, ActionType.Bonus))
                {
                    continue;
                }
                using var selection = SpellCastingValidation.EnterSelectedRepertoire(repertoire);
                if (!character.AreSpellComponentsValid(spell) ||
                    !SpellCastingValidation.IsValid(character, repertoire, spell, null, out _))
                {
                    continue;
                }
                var level = 0;
                if (kind == EnemyBattleAffix.SpellFlight)
                {
                    for (var candidateLevel = 3; candidateLevel <= 9; candidateLevel++)
                    {
                        if (repertoire.TryGetAvailableSlotLevel(character, candidateLevel, null, out var available) &&
                            available && SpellSlotCastingLimit2024Context.CanUseSpellSlotLevel(character, repertoire,
                                null, candidateLevel))
                        {
                            level = candidateLevel;
                            break;
                        }
                    }
                    if (level == 0)
                    {
                        continue;
                    }
                }
                var effect = PowerBundle.ModifySpellEffect(spell, character);
                if (kind == EnemyBattleAffix.QuickenedCantrip &&
                    !IsEnemyBattleAiCantripEffect(effect))
                {
                    continue;
                }
                foreach (var target in targets)
                {
                    var modifier = new ActionModifier();
                    if (kind == EnemyBattleAffix.QuickenedCantrip)
                    {
                        if (!HasUsefulEnemyBattleCantripEffect(actor, target, effect))
                        {
                            continue;
                        }
                        if (!TryGetMagicAttackModifierFromPosition(actor, actor.LocationPosition, target,
                                target.LocationPosition, effect, spell.Name, battleService, out modifier))
                        {
                            continue;
                        }
                    }
                    var parameters = new CharacterActionParams(actor, Id.CastBonus)
                    {
                        SpellRepertoire = repertoire, IntParameter = level, StringParameter = spell.Name,
                        RulesetEffect = implementationService.InstantiateEffectSpell(character, repertoire, spell, level, false),
                        TargetCharacters = { target }, ActionModifiers = { modifier }
                    };
                    if (kind == EnemyBattleAffix.SpellFlight)
                    {
                        SpellCastingResourceContext.ApplySelection(parameters,
                            new SpellCastingResourceContext.ResourceOption(repertoire, spell, level,
                                SpellCastingResourceContext.ResourceKind.SpellSlot));
                    }
                    DispatchCombatAiAction(actionService, parameters, false);
                    return true;
                }
            }
        }
        return false;
    }

    // The trigger keeps its attack/spell context; native casting owns components,
    // counterspell, action economy and the free repertoire's resource counter.
    internal static bool TryConvertEnemyBattleSpellAction(CharacterActionParams parameters)
    {
        var power = parameters.UsablePower?.PowerDefinition ??
                    (parameters.RulesetEffect as RulesetEffectPower)?.PowerDefinition;
        if (!TryGetEnemyBattleCapabilityKind(power, out var kind) ||
            !EnemyBattleFreeSpells.TryGetValue(kind, out var spell))
        {
            return false;
        }
        var character = parameters.ActingCharacter.RulesetCharacter;
        var repertoire = character.SpellRepertoires.FirstOrDefault(rep =>
            rep.SpellCastingFeature == EnemyBattleFreeCasting[kind]);
        if (repertoire == null || ServiceRepository.GetService<IRulesetImplementationService>() is not { } implementation ||
            ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService)
        {
            return false;
        }
        var id = GetEnemyBattleCapabilityMetadata(kind).ActivationTime == ActivationTime.Reaction
            ? Id.CastReaction : Id.CastBonus;
        parameters.RulesetEffect?.Terminate(false);
        parameters.ActionDefinition = actionService.AllActionDefinitions[id];
        parameters.SpellRepertoire = repertoire;
        parameters.StringParameter = spell.Name;
        parameters.IntParameter = spell.SpellLevel;
        parameters.UsablePower = GetEnemyBattleUsablePower(power, character);
        parameters.RulesetEffect = implementation.InstantiateEffectSpell(character, repertoire, spell, spell.SpellLevel, false);
        SpellCastingResourceContext.ApplySelection(parameters,
            new SpellCastingResourceContext.ResourceOption(repertoire, spell, spell.SpellLevel,
                SpellCastingResourceContext.ResourceKind.FreeRepertoire));
        return true;
    }

    private static bool CanStartEnemyBattleBonusAction(GameLocationCharacter actor) =>
        Main.Settings.EnableEnemyBattleTactics && actor?.Side == Side.Enemy && CanIssueAutomaticTurnAction(actor) &&
        actor.GetActionTypeStatus(ActionType.Bonus) == ActionStatus.Available && !HasCommittedBonusActionThisTurn(actor);

    private static bool TryExecuteEnemyBattleBonusPower(
        GameLocationCharacter actor, EnemyBattleAffix kind, out RulesetUsablePower usablePower,
        GameLocationCharacter target = null, int3? position = null) =>
        TryExecuteEnemyBattleBonusPower(actor, kind, out usablePower, target, position, null);

    private static bool TryExecuteEnemyBattleBonusPower(
        GameLocationCharacter actor, EnemyBattleAffix kind, out RulesetUsablePower usablePower,
        GameLocationCharacter target, int3? position, GameLocationCharacter attackTarget)
    {
        usablePower = null;
        if (IsEnemyBattleNativeBonusAttack(kind))
        {
            return TryExecuteEnemyBattleNativeBonusAttack(actor, kind, target, out usablePower);
        }

        if (!CanStartEnemyBattleBonusAction(actor) ||
            actor.GetActionStatus(Id.PowerBonus, ActionScope.Battle) != ActionStatus.Available ||
            !CanUseEnemyBattlePower(actor, kind, out usablePower) ||
            !EnemyBattleFreeSpells.ContainsKey(kind) && !actor.RulesetCharacter.CanUsePower(usablePower.PowerDefinition, true, true) ||
            target != null && usablePower.PowerDefinition.GetFirstSubFeatureOfType<EnemyBattleUtilityPower>() != null &&
                // A pending weapon or spell attack has already established the usefulness
                // of self Command, including leveled spells with no at-will alternative.
                !IsValidEnemyBattleUtilityTarget(actor, kind, target,
                    forAi: attackTarget == null || kind != EnemyBattleAffix.Command || target != actor) ||
            ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService ||
            ServiceRepository.GetService<IRulesetImplementationService>() is not { } implementationService)
        {
            return false;
        }

        if (kind == EnemyBattleAffix.WeaponCoating)
        {
            var choice = GetEnemyBattleWeaponCoatingChoice(actor, attackTarget);
            if (choice == null)
            {
                return false;
            }
            usablePower = GetEnemyBattleUsablePower(choice, actor.RulesetCharacter);
        }
        var actionParams = new CharacterActionParams(actor, Id.PowerBonus)
        {
            ActionModifiers = { new ActionModifier() },
            RulesetEffect = implementationService.InstantiateEffectPower(actor.RulesetCharacter, usablePower, false),
            UsablePower = usablePower
        };

        if (position is { } destination)
        {
            // Native self-teleport uses the source when there is no character target.
            actionParams.Positions.Add(destination);
        }
        else
        {
            actionParams.TargetCharacters.Add(target ?? actor);
            var effect = usablePower.PowerDefinition.EffectDescription;
            if (effect.TargetSide == Side.Ally && effect.TargetType == TargetType.IndividualsUnique && effect.TargetParameter > 1)
            {
                foreach (var ally in ((_enemyTacticsBattle ?? Gui.Battle)?.AllContenders ?? []).Where(ally =>
                             !actionParams.TargetCharacters.Contains(ally) &&
                             IsValidEnemyBattleUtilityTarget(actor, kind, ally, forAi: true))
                             .OrderBy(ally => ally.RulesetCharacter.TemporaryHitPoints).ThenBy(ally => ally.Guid)
                             .Take(effect.TargetParameter - actionParams.TargetCharacters.Count))
                {
                    actionParams.TargetCharacters.Add(ally);
                    actionParams.ActionModifiers.Add(new ActionModifier());
                }
            }
        }

        if (EnemyBattleFreeSpells.ContainsKey(kind) && !TryConvertEnemyBattleSpellAction(actionParams))
        {
            ReleaseUndispatchedCombatAiAction(actionParams);
            return false;
        }
        DispatchCombatAiAction(actionService, actionParams, false);
        return true;
    }

    private static bool CanContinueEnemyBattleBonusMove(GameLocationCharacter actor, RulesetUsablePower dispatchedPower) =>
        CanIssueAutomaticTurnAction(actor) && actor.Side == Side.Enemy && actor.CanDecideToMoveByItself &&
        actor.RulesetCharacter.HasPower(dispatchedPower.PowerDefinition);

    internal static IEnumerator RunEnemyBattlePreparationBeforeAttack(CharacterActionParams attack)
    {
        var actor = attack?.ActingCharacter;
        var mode = attack?.AttackMode;
        var spell = (attack?.RulesetEffect ?? attack?.activeEffect) as RulesetEffectSpell;
        var physical = attack?.ActionDefinition?.Id == Id.AttackMain && mode != null;
        var magic = attack?.ActionDefinition?.Id == Id.CastMain &&
                    spell?.EffectDescription.RangeType is RangeType.MeleeHit or RangeType.RangeHit;
        if ((!physical && !magic) ||
            attack.TargetCharacters.FirstOrDefault() is not { } target ||
            !CanStartEnemyBattleBonusAction(actor) ||
            (physical ? !HasAvailableAttackMainContinuation(actor) : !CanUseMainSpellActionForAi(actor)) ||
            ShouldReserveEnemyBattleOnHitBonus(actor) ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            yield break;
        }

        ActionModifier modifier;
        if (magic)
        {
            if (!TryGetMagicAttackModifierFromPosition(actor, actor.LocationPosition, target,
                    target.LocationPosition, spell.EffectDescription, spell.SourceDefinition.Name, battleService, out modifier))
            {
                yield break;
            }
        }
        else if (!TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, target,
                     target.LocationPosition, mode, battleService, out modifier))
        {
            yield break;
        }

        // The native activity has selected a live attack but has not queued it yet.
        // Revisit preparation after movement without enqueueing a bonus from inside
        // the attack's own action chain, which would wait on itself.
        foreach (var kind in EnemyBattleUtilityPriority.Where(IsEnemyBattleAttackPreparation))
        {
            if (magic && kind != EnemyBattleAffix.Command ||
                kind is EnemyBattleAffix.Feint or EnemyBattleAffix.Command or EnemyBattleAffix.PackCall &&
                !IsEnemyBattleAttackAdvantageUseful(modifier.AttackAdvantageTrends) ||
                IsEnemyBattleWeaponCoating(kind) && !IsEnemyBattleWeaponAttack(mode) ||
                kind == EnemyBattleAffix.PressingAttack &&
                (EffectForm.GetFirstDamageForm(mode.EffectDescription.EffectForms) is not { } damage ||
                 HasEnemyBattleDamageImmunity(target.RulesetCharacter, damage.DamageType)) ||
                !CanUseEnemyBattlePower(actor, kind, out _) ||
                !magic && (!TryGetEnemyBattleUtilityTarget(actor, kind, out var recipient) || recipient != actor) ||
                !TryExecuteEnemyBattleBonusPower(actor, kind, out _, actor, null, target))
            {
                continue;
            }
            yield return WaitForCombatAiProcessAction(actor);
            if (magic)
            {
                RefreshEnemyBattleSpellAttackModifiers(attack, spell, battleService);
            }
            InvalidateTurnPlanningCache(actor);
            yield break;
        }
    }

    private static void RefreshEnemyBattleSpellAttackModifiers(CharacterActionParams attack,
        RulesetEffectSpell spell, IGameLocationBattleService battleService)
    {
        // CastMagic computes modifiers before it enters ExecuteAndWait, unlike a
        // physical attack. Re-evaluate each pending target after preparation using
        // a fresh modifier so affinity bonuses and cover are not added twice.
        var actor = attack.ActingCharacter;
        for (var index = 0; index < attack.TargetCharacters.Count && index < attack.ActionModifiers.Count; index++)
        {
            var target = attack.TargetCharacters[index];
            var pending = attack.ActionModifiers[index];
            if (target?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } || pending == null)
            {
                continue;
            }
            if (!TryGetMagicAttackModifierFromPosition(actor, actor.LocationPosition, target,
                    target.LocationPosition, spell.EffectDescription, spell.SourceDefinition.Name, battleService, out var refreshed))
            {
                continue;
            }

            // Preserve the pending modifier and its unrelated save/ability state.
            pending.AttackRollModifier = refreshed.AttackRollModifier;
            pending.AttacktoHitTrends.Clear();
            pending.AttacktoHitTrends.AddRange(refreshed.AttacktoHitTrends);
            pending.AttackAdvantageTrends.Clear();
            pending.AttackAdvantageTrends.AddRange(refreshed.AttackAdvantageTrends);
            pending.IgnoreAdvantage = refreshed.IgnoreAdvantage;
            pending.AttackerDamageMultiplier = refreshed.AttackerDamageMultiplier;
            pending.DamageRollReduction = refreshed.DamageRollReduction;
            pending.Proximity = refreshed.Proximity;
            pending.CoverType = refreshed.CoverType;
            pending.SavingThrowDexterityFromCoverModifier = refreshed.SavingThrowDexterityFromCoverModifier;
        }
    }

    private static IEnumerator RunEnemyBattleBonusActionBeforeVanilla(GameLocationCharacter actor)
    {
        if (TryExecuteEnemyBattleSpellAction(actor, EnemyBattleAffix.SpellFlight))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
            yield break;
        }
        if (!CanStartEnemyBattleBonusAction(actor))
        {
            yield break;
        }

        if (CanUseEnemyBattlePower(actor, EnemyBattleAffix.Resolve, out _) &&
            HasEnemyBattleResolveAffliction(actor.RulesetCharacter) &&
            TryExecuteEnemyBattleBonusPower(actor, EnemyBattleAffix.Resolve, out _))
        {
            yield return WaitForCombatAiProcessAction(actor);
            yield break;
        }

        if (CanUseEnemyBattlePower(actor, EnemyBattleAffix.PhaseStep, out _))
        {
            var destination = int3.invalid;
            yield return ComputeEnemyBattlePhaseStepDestination(actor, position => destination = position);
            if (destination != int3.invalid &&
                TryExecuteEnemyBattleBonusPower(actor, EnemyBattleAffix.PhaseStep, out _, position: destination))
            {
                yield return WaitForCombatAiProcessAction(actor);
                InvalidateTurnPlanningCache(actor);
                yield break;
            }
        }

        var reserveOnHitAttack = ShouldReserveEnemyBattleOnHitBonus(actor);
        if (!reserveOnHitAttack && TryExecuteEnemyBattleShove(actor))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
            yield break;
        }

        var reserveOnHitBonus = reserveOnHitAttack ||
            CanUseEnemyBattlePower(actor, EnemyBattleAffix.PredatoryLunge, out _) &&
            ServiceRepository.GetService<IGameLocationBattleService>() is { } lungeBattle &&
            SnapshotCharacters(actor.PerceivedFoes).Any(foe => foe?.RulesetCharacter is
                { IsDeadOrDyingOrUnconscious: false } && actor.IsOppositeSide(foe.Side) &&
                HasUsefulEnemyBattlePhysicalAttack(actor, foe, lungeBattle, false, meleeOnly: true));
        if (!reserveOnHitBonus && TryExecuteEnemyBattleUtilityBonusPower(actor))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
            yield break;
        }

        var hasWithdrawal = CanUseEnemyBattlePower(actor, EnemyBattleAffix.Withdrawal, out _);
        var rushKind = CanUseEnemyBattlePower(actor, EnemyBattleAffix.ChargingRush, out _)
            ? EnemyBattleAffix.ChargingRush : EnemyBattleAffix.HuntingRush;
        var hasHuntingRush = CanUseEnemyBattlePower(actor, rushKind, out _);

        if (reserveOnHitBonus || (!hasWithdrawal && !hasHuntingRush) || !actor.CanDecideToMoveByItself ||
            actor.RemainingTacticalMoves <= 0 || !actor.RulesetCharacter.IsTouchingGround() ||
            actor.GetActionStatus(Id.TacticalMove, ActionScope.Battle) != ActionStatus.Available)
        {
            yield break;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var enemies = SnapshotCharacters(actor.PerceivedFoes)
            .Where(enemy => enemy?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                            enemy.Side != actor.Side && !IsNonOccupyingCombatProxyTarget(enemy))
            .OrderBy(enemy => ComputeGridDistance(actor.LocationPosition, enemy.LocationPosition))
            .ThenBy(enemy => enemy.Guid)
            .ToArray();

        if (battleService == null || enemies.Length == 0)
        {
            yield break;
        }

        EnsureCombatAiRuntimeCache();
        var start = actor.LocationPosition;
        var remainingMove = actor.RemainingTacticalMoves;
        var round = GetCurrentBattleRound();
        var turnStamp = Math.Max(1, ObservedCombatMemoryTurnStamp);
        var planningBattle = Gui.Battle;

        bool IsCurrent() => CanStartEnemyBattleBonusAction(actor) && ReferenceEquals(Gui.Battle, planningBattle) &&
                            IsRoutePlanningStateCurrent(actor, start, remainingMove, round, turnStamp);

        if (hasWithdrawal && !actor.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionDefinitions.ConditionDisengaging.Name))
        {
            var threats = enemies.Where(enemy =>
                    enemy.RulesetCharacter.IsIncapacitated == false &&
                    enemy.GetActionTypeStatus(ActionType.Reaction) == ActionStatus.Available &&
                    IsEnemyBattleMeleeThreatAt(actor, enemy, start, battleService))
                .ToArray();

            if (threats.Length > 0 && HasAvailableAttackModeForMainAction(actor, CombatAiActionKind.Ranged))
            {
                yield return ComputeReachableRouteDestinations(actor, start, remainingMove, walkOnly: true,
                    validateTurn: true, queryIsCurrent: IsCurrent);

                if (!IsCurrent())
                {
                    yield break;
                }

                if (TryGetCachedReachableRouteDestinations(actor, start, remainingMove, true, out var withdrawalRoutes))
                {
                    var destination = int3.invalid;
                    AiWorkBudget.Resume();

                    foreach (var position in withdrawalRoutes.Positions.OrderBy(withdrawalRoutes.GetMoveCost)
                                 .ThenBy(position => position.x).ThenBy(position => position.y).ThenBy(position => position.z))
                    {
                        if (AiWorkBudget.ShouldYield())
                        {
                            yield return null;
                            if (!IsCurrent())
                            {
                                yield break;
                            }
                            AiWorkBudget.Resume();
                        }

                        if (IsEnemyBattleWithdrawalDestination(actor, start, position, enemies, threats,
                                withdrawalRoutes, battleService))
                        {
                            destination = position;
                            break;
                        }
                    }

                    if (destination != int3.invalid && IsCurrent() &&
                        TryExecuteEnemyBattleBonusPower(actor, EnemyBattleAffix.Withdrawal, out var withdrawal))
                    {
                        yield return WaitForCombatAiProcessAction(actor);

                        if (CanContinueEnemyBattleBonusMove(actor, withdrawal) && actor.LocationPosition == start &&
                            actor.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionDefinitions.ConditionDisengaging.Name) &&
                            IsEnemyBattleWithdrawalDestination(actor, start, destination, enemies, threats,
                                withdrawalRoutes, battleService, requireOpportunityRisk: false) &&
                            TryValidateAiTacticalMoveIssue(actor, destination, requireCanDecide: true) &&
                            actor.MyExecuteActionTacticalMove(destination, null, null))
                        {
                            yield return WaitForCombatAiProcessAction(actor);
                            InvalidateTurnPlanningCache(actor);
                        }
                        yield break;
                    }
                }
            }
        }

        if (!hasHuntingRush || !IsCurrent())
        {
            yield break;
        }

        var profile = BuildEnemyBattleProfile(actor);
        var turnPlan = BuildCombatAiTurnPlan(actor, profile, battleService);
        var dashMoveBudget = remainingMove + Math.Max(0, actor.MaxTacticalMoves);

        if (turnPlan.ActionProbe.PreferredAction != CombatAiActionKind.Melee ||
            turnPlan.ActionProbe.CanUsePreferredAction || dashMoveBudget <= remainingMove)
        {
            yield break;
        }

        yield return ComputeReachableRouteDestinations(actor, start, dashMoveBudget, walkOnly: true,
            validateTurn: true, queryIsCurrent: IsCurrent);

        if (!IsCurrent())
        {
            yield break;
        }

        var rush = EvaluateBonusDashMeleeEngagement(actor, profile, turnPlan, battleService,
            requireDashAction: false, eligibleTargets: enemies);

        if (!rush.IsAvailable ||
            !TryGetCachedReachableRouteDestinations(actor, start, dashMoveBudget, true, out var rushRoutes) ||
            !TryExecuteEnemyBattleBonusPower(actor, rushKind, out var huntingRush))
        {
            yield break;
        }

        yield return WaitForCombatAiProcessAction(actor);

        if (CanContinueEnemyBattleBonusMove(actor, huntingRush) && actor.LocationPosition == start &&
            actor.RemainingTacticalMoves > remainingMove &&
            rush.Target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
            actor.PerceivedFoes.Contains(rush.Target) &&
            rushRoutes.Contains(rush.Destination) && rushRoutes.GetMoveCost(rush.Destination) <= actor.RemainingTacticalMoves &&
            !HasForcedRouteOpportunityExposure(actor, start, rush.Destination, battleService) &&
            TryValidateAiTacticalMoveIssue(actor, rush.Destination, requireCanDecide: true) &&
            CanUseActionKindAtPosition(actor, rush.Destination, rush.Target, CombatAiActionKind.Melee, battleService) &&
            actor.MyExecuteActionTacticalMove(rush.Destination, null, null))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
        }
    }

    private static IEnumerator RunEnemyBattleBonusActionAfterVanilla(GameLocationCharacter actor)
    {
        if (TryExecuteEnemyBattleSpellAction(actor, EnemyBattleAffix.QuickenedCantrip))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
            yield break;
        }
        if (TryExecuteEnemyBattlePredatoryLunge(actor))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
            yield break;
        }
        // A reach-one warrior preserves its main attack, then uses an unspent bonus to cover an ally.
        // This capability also works when the separate advanced AI option is disabled.
        if (TryExecuteEnemyBattleShove(actor))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
            yield break;
        }
        if (TryExecuteEnemyBattleUtilityBonusPower(actor))
        {
            yield return WaitForCombatAiProcessAction(actor);
            InvalidateTurnPlanningCache(actor);
            yield break;
        }
    }

    private static bool TryExecuteEnemyBattleUtilityBonusPower(GameLocationCharacter actor)
    {
        foreach (var kind in EnemyBattleUtilityPriority)
        {
            if (CanUseEnemyBattlePower(actor, kind, out _) && TryGetEnemyBattleUtilityTarget(actor, kind, out var target) &&
                (target != actor || !IsEnemyBattleAttackPreparation(kind)) &&
                TryExecuteEnemyBattleBonusPower(actor, kind, out _, target))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly EnemyBattleAffix[] EnemyBattleUtilityPriority = EnemyBattleCatalog.Values
        .Where(definition => definition.UtilityOrder >= 0).OrderBy(definition => definition.UtilityOrder)
        .Select(definition => definition.Kind).ToArray();

    private static readonly EnemyBattleCapabilityDefinition[] EnemyBattleNativeAttackDefinitions = EnemyBattleCatalog.Values
        .Where(definition => definition.NativeAttackTagOrder >= 0).OrderBy(definition => definition.NativeAttackTagOrder).ToArray();

    private static bool IsEnemyBattleAttackPreparation(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.AttackPreparation);

    private const string EnemyBattlePredatoryTag = "EnemyBattlePredatoryLunge";
    private const string EnemyBattleViscousTag = "EnemyBattleViscousSnare";
    private const string EnemyBattleVolleyTag = "EnemyBattleFocusedVolley";
    private const string EnemyBattleSweepingVolleyTag = "EnemyBattleSweepingVolley";
    private const string EnemyBattleImprovisedThrowTag = "EnemyBattleImprovisedThrow";
    private const string EnemyBattlePiercingTag = "EnemyBattlePiercingJab";

    private static bool IsEnemyBattleNativeBonusAttack(EnemyBattleAffix kind) =>
        HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.NativeBonusAttack);

    internal static bool IsLimitedEnemyBattleNativeAttack(RulesetAttackMode mode) =>
        TryGetEnemyBattleNativeAttackKind(mode, out var kind) && kind != EnemyBattleAffix.PredatoryLunge;

    internal static bool UsesEnemyBattleNativeAttackAmmunition(RulesetAttackMode mode) =>
        TryGetEnemyBattleNativeAttackKind(mode, out var kind) && kind == EnemyBattleAffix.SweepingVolley;

    internal static void PrepareEnemyBattleNativeAttackAction(CharacterActionAttack action)
    {
        var parameters = action?.ActionParams;
        var mode = parameters?.AttackMode;
        if (!UsesEnemyBattleNativeAttackAmmunition(mode) ||
            action.ActingCharacter?.RulesetCharacter?.AttackModes.Contains(mode) != true)
        {
            return;
        }
        // Native ammunition exhaustion can refresh and return the actor's modes before this attack finishes.
        var owned = mode.Clone();
        parameters.AttackMode = owned;
        parameters.ActionExecuted += _ => owned.Return();
    }

    internal static bool TryGetEnemyBattleRechargingAttackAvailability(RulesetCharacterMonster actor, RulesetAttackMode mode,
        out int remainingAttacks, bool allowUntaggedSource = true)
    {
        remainingAttacks = 0;
        if (actor == null || mode == null || mode.ActionType is not (ActionType.Bonus or ActionType.NoCost))
        {
            return false;
        }
        bool IsCapabilityMode(RulesetAttackMode candidate) =>
            TryGetEnemyBattleNativeAttackKind(candidate, out var candidateKind) && IsEnemyBattleNativeBonusAttack(candidateKind) &&
            (candidate.ActionType == ActionType.Bonus ||
             candidateKind == EnemyBattleAffix.SweepingVolley && candidate.ActionType == ActionType.NoCost);

        // An untagged utility precheck only belongs to a new attack definition;
        // unrelated native bonus attacks that share a main source retain native ammunition.
        var capabilityMode = IsCapabilityMode(mode) ? mode :
            allowUntaggedSource && mode.SourceDefinition is MonsterAttackDefinition && actor.GetAttackModeRank(mode) < 0
                ? actor.AttackModes.FirstOrDefault(candidate => candidate.SourceDefinition == mode.SourceDefinition && IsCapabilityMode(candidate))
                : null;
        if (!TryGetEnemyBattleNativeAttackKind(capabilityMode, out var kind) || !IsEnemyBattleNativeBonusAttack(kind))
        {
            return false;
        }
        if (CanUseEnemyBattlePower(GameLocationCharacter.GetFromActor(actor), kind, out var usable))
        {
            // The attack can be initiated once while its rechargeable charge is ready.
            remainingAttacks = usable.RemainingUses > 0 ? 1 : 0;
        }
        else if (kind == EnemyBattleAffix.SweepingVolley && EnemyBattleNativeAttackTargets.TryGetValue(actor, out var pending) &&
                 pending.Kind == kind && pending.Issued && pending.Consumed && pending.TargetIndex > 0 &&
                 IsCurrentEnemyBattleNativeAttackTarget(GameLocationCharacter.GetFromActor(actor), pending))
        {
            remainingAttacks = 1;
        }
        return true;
    }

    private static string GetEnemyBattleNativeAttackTag(EnemyBattleAffix kind) =>
        EnemyBattleCatalog.TryGetValue(kind, out var definition) ? definition.NativeAttackTag : null;

    private static bool TryGetEnemyBattleNativeAttackKind(RulesetAttackMode mode, out EnemyBattleAffix kind)
    {
        kind = default;
        if (mode == null)
        {
            return false;
        }
        foreach (var definition in EnemyBattleNativeAttackDefinitions)
        {
            if (!mode.AttackTags.Contains(definition.NativeAttackTag))
            {
                continue;
            }
            kind = definition.Kind;
            return true;
        }
        return false;
    }

    private sealed class EnemyBattleNativeAttackTarget
    {
        internal EnemyBattleAffix Kind;
        internal GameLocationCharacter Target;
        internal GameLocationCharacter[] Targets = [];
        internal int TargetIndex;
        internal GameLocationBattle Battle;
        internal int Round;
        internal int Turn;
        internal bool Issued;
        internal bool Consumed;
    }

    private static readonly ConditionalWeakTable<RulesetCharacter, EnemyBattleNativeAttackTarget> EnemyBattleNativeAttackTargets = new();

    private static bool IsCurrentEnemyBattleNativeAttackTarget(GameLocationCharacter actor, EnemyBattleNativeAttackTarget pending) =>
        Main.Settings.EnableEnemyBattleTactics &&
        actor?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } source &&
        pending.Target?.RulesetCharacter is { } victim &&
        (pending.Kind == EnemyBattleAffix.SweepingVolley || !victim.IsDeadOrDyingOrUnconscious) &&
        ReferenceEquals(pending.Battle, Gui.Battle) && pending.Round == GetCurrentBattleRound() &&
        pending.Turn == ObservedCombatMemoryTurnStamp && actor.IsMyTurn() && actor.IsOppositeSide(pending.Target.Side) &&
        (pending.Kind != EnemyBattleAffix.PredatoryLunge ||
            victim.CurrentHitPoints <= (victim.CurrentHitPoints + victim.MissingHitPoints) / 2) &&
        GetEnemyBattleEffectiveSelection(actor).Contains(pending.Kind) &&
        (CanUseEnemyBattlePower(actor, pending.Kind, out _) || pending.Issued && pending.Consumed &&
            FindEnemyBattleCondition(source, EnemyBattleAffixes[pending.Kind].Condition) != null);

    internal static bool IsValidEnemyBattleAttackTarget(BattleDefinitions.AttackEvaluationParams attack)
    {
        if (attack.attackMode?.AttackTags.Contains(EnemyBattleWhirlwindTag) == true)
        {
            return attack.attacker?.RulesetCharacter is { } source && EnemyBattleWhirlwinds.TryGetValue(source, out var whirlwind) &&
                   whirlwind.Accepted && whirlwind.Index >= 0 && whirlwind.Index < whirlwind.Targets.Length &&
                   whirlwind.Parameters?.AttackMode == attack.attackMode &&
                   whirlwind.Targets[whirlwind.Index] == attack.defender &&
                   TryGetEnemyBattleWhirlwindModifier(whirlwind, whirlwind.Index, out _);
        }
        if (!TryGetEnemyBattleNativeAttackKind(attack.attackMode, out var kind))
        {
            return true;
        }
        var actor = attack.attacker;
        var target = attack.defender;
        if (actor?.RulesetCharacter == null || target?.RulesetCharacter is not
                { IsDeadOrDying: false, IsRemovedFromTheGame: false } ||
            !actor.IsMyTurn() || !actor.IsOppositeSide(target.Side) ||
            !actor.CanSeeTarget(target) || !GetEnemyBattleEffectiveSelection(actor).Contains(kind))
        {
            return false;
        }
        var exists = EnemyBattleNativeAttackTargets.TryGetValue(actor.RulesetCharacter, out var pending);
        var hasPending = exists && IsCurrentEnemyBattleNativeAttackTarget(actor, pending);
        if (UsesEnemyBattleNativeAttackAmmunition(attack.attackMode) &&
            actor.RulesetCharacter is RulesetCharacterMonster monster && monster.GetRemainingAttackUses(attack.attackMode) == 0)
        {
            return false;
        }
        var continuation = kind == EnemyBattleAffix.SweepingVolley && hasPending && pending.Issued && pending.Consumed &&
                           pending.TargetIndex > 0 && pending.TargetIndex < pending.Targets.Length &&
                           pending.Targets[pending.TargetIndex] == target;
        if (attack.attackMode.ActionType != ActionType.Bonus &&
            !(continuation && attack.attackMode.ActionType == ActionType.NoCost))
        {
            return false;
        }
        if (exists && pending.Issued && !hasPending || hasPending && (pending.Kind != kind || pending.Target != target))
        {
            return false;
        }
        if (!hasPending && kind == EnemyBattleAffix.PredatoryLunge ||
            !(hasPending && pending.Issued) &&
            (actor.GetActionTypeStatus(ActionType.Bonus, ActionScope.Battle) != ActionStatus.Available ||
             HasCommittedBonusActionThisTurn(actor) || !CanUseEnemyBattlePower(actor, kind, out _)))
        {
            return false;
        }
        if (kind == EnemyBattleAffix.PredatoryLunge)
        {
            return !attack.attackMode.Ranged && !IsEnemyBattleWeaponAttack(attack.attackMode);
        }
        return IsValidEnemyBattleUtilityTarget(actor, kind, target, forAi: false);
    }

    internal static bool IsValidEnemyBattleNativeAttackAction(CharacterActionAttack action)
    {
        if (action?.ActionParams?.AttackMode?.AttackTags.Contains(EnemyBattleWhirlwindTag) == true)
        {
            return IsValidEnemyBattleWhirlwindAction(action);
        }
        if (action?.ActionParams?.AttackMode?.AttackTags.Contains(EnemyBattleCoordinatedAdvanceTag) == true)
        {
            return EnemyBattleCoordinatedAttacks.TryGetValue(action.ActionParams.AttackMode, out var coordinated) &&
                   coordinated.Accepted && coordinated.Command.Consumed &&
                   action.ActionId == Id.AttackOpportunity && action.ActionType == ActionType.Reaction &&
                   action.ActingCharacter == coordinated.Ally && action.ActionParams.TargetCharacters.Count == 1 &&
                   action.ActionParams.TargetCharacters[0] == coordinated.Command.Defender &&
                   IsCurrentEnemyBattleCoordinatedAttack(coordinated, executing: true);
        }
        if (!TryGetEnemyBattleNativeAttackKind(action?.ActionParams?.AttackMode, out var kind))
        {
            return true;
        }
        var parameters = action.ActionParams;
        var continuation = kind == EnemyBattleAffix.SweepingVolley &&
            EnemyBattleNativeAttackTargets.TryGetValue(action.ActingCharacter.RulesetCharacter, out var pending) &&
            pending.Kind == kind && pending.Issued && pending.Consumed && pending.TargetIndex > 0 &&
            pending.TargetIndex < pending.Targets.Length && parameters.TargetCharacters.Count == 1 &&
            parameters.TargetCharacters[0] == pending.Targets[pending.TargetIndex];
        if (action.ActionId != (continuation ? Id.AttackFree : Id.AttackOff))
        {
            return false;
        }
        if (parameters.TargetCharacters.Count != 1 ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            return false;
        }
        // The shared native evaluation also runs the delegated target validator through CanAttack.
        return TryGetPhysicalAttackModifierFromPosition(action.ActingCharacter,
            action.ActingCharacter.LocationPosition, parameters.TargetCharacters[0],
            parameters.TargetCharacters[0].LocationPosition, parameters.AttackMode, battleService, out _);
    }

    private static EnemyBattleNativeAttackTarget SetEnemyBattleNativeAttackTarget(GameLocationCharacter actor,
        EnemyBattleAffix kind, GameLocationCharacter target)
    {
        var pending = new EnemyBattleNativeAttackTarget
        {
            Kind = kind, Target = target, Battle = Gui.Battle, Round = GetCurrentBattleRound(),
            Turn = ObservedCombatMemoryTurnStamp
        };
        if (kind == EnemyBattleAffix.SweepingVolley)
        {
            // Freeze distinct targets before the first attack; deaths or movement never add replacement shots.
            pending.Targets = new[] { target }.Concat(SnapshotCharacters(actor.PerceivedFoes)
                    .Where(foe => foe != target && IsValidEnemyBattleUtilityTarget(actor, kind, foe, forAi: true))
                    .OrderBy(foe => ComputeGridDistance(actor.LocationPosition, foe.LocationPosition)).ThenBy(foe => foe.Guid))
                .Distinct().Take(3).ToArray();
        }
        EnemyBattleNativeAttackTargets.Remove(actor.RulesetCharacter);
        EnemyBattleNativeAttackTargets.Add(actor.RulesetCharacter, pending);
        return pending;
    }

    private static bool TryExecuteEnemyBattlePredatoryLunge(GameLocationCharacter actor)
    {
        return actor?.RulesetCharacter != null && EnemyBattleNativeAttackTargets.TryGetValue(actor.RulesetCharacter, out var pending) &&
               pending.Kind == EnemyBattleAffix.PredatoryLunge &&
               TryExecuteEnemyBattleNativeBonusAttack(actor, pending.Kind, pending.Target, out _);
    }

    private static bool TryExecuteEnemyBattleNativeBonusAttack(GameLocationCharacter actor, EnemyBattleAffix kind,
        GameLocationCharacter target, out RulesetUsablePower usablePower)
    {
        usablePower = null;
        if (!CanStartEnemyBattleBonusAction(actor) || !CanUseEnemyBattlePower(actor, kind, out usablePower) || target == null ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService ||
            ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService)
        {
            return false;
        }
        EnemyBattleNativeAttackTargets.TryGetValue(actor.RulesetCharacter, out var pending);
        if (pending != null && IsCurrentEnemyBattleNativeAttackTarget(actor, pending) && pending.Issued ||
            kind == EnemyBattleAffix.PredatoryLunge && (pending == null || pending.Kind != kind ||
                pending.Target != target || !IsCurrentEnemyBattleNativeAttackTarget(actor, pending)) ||
            kind != EnemyBattleAffix.PredatoryLunge && !IsValidEnemyBattleUtilityTarget(actor, kind, target, forAi: true))
        {
            return false;
        }
        pending = SetEnemyBattleNativeAttackTarget(actor, kind, target);
        actor.RulesetCharacter.RefreshAttackModes();
        foreach (var mode in actor.RulesetCharacter.AttackModes.Where(mode => mode.ActionType == ActionType.Bonus &&
                     mode.AttackTags.Contains(GetEnemyBattleNativeAttackTag(kind))))
        {
            if (actor.GetActionStatus(Id.AttackOff, ActionScope.Battle, optionalAttackMode: mode) != ActionStatus.Available)
            {
                continue;
            }
            if (!TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, target,
                    target.LocationPosition, mode, battleService, out var modifier))
            {
                continue;
            }
            pending.Issued = true;
            actionService.ExecuteAction(new CharacterActionParams(actor, Id.AttackOff, mode, target, modifier), null, false);
            return true;
        }
        EnemyBattleNativeAttackTargets.Remove(actor.RulesetCharacter);
        actor.RulesetCharacter.RefreshAttackModes();
        return false;
    }

    private static bool TryContinueEnemyBattleSweepingVolley(CharacterAction action, EnemyBattleNativeAttackTarget pending)
    {
        var actor = action.ActingCharacter;
        if (action.Aborted || action.Countered || !pending.Consumed || !IsCurrentEnemyBattleNativeAttackTarget(actor, pending) ||
            action.ActionParams.TargetCharacters.Count != 1 || action.ActionParams.TargetCharacters[0] != pending.Target ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService ||
            ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService)
        {
            return false;
        }
        while (++pending.TargetIndex < pending.Targets.Length)
        {
            pending.Target = pending.Targets[pending.TargetIndex];
            if (!IsValidEnemyBattleUtilityTarget(actor, pending.Kind, pending.Target, forAi: true))
            {
                continue;
            }
            if (!TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, pending.Target,
                    pending.Target.LocationPosition, action.ActionParams.AttackMode, battleService, out var modifier))
            {
                continue;
            }
            var mode = action.ActionParams.AttackMode.Clone();
            actionService.ExecuteAction(new CharacterActionParams(actor, Id.AttackFree, mode,
                pending.Target, modifier), _ => mode.Return(), true);
            return true;
        }
        return false;
    }

    private static void RestrictEnemyBattleNativeAttackMode(RulesetCharacter character, RulesetAttackMode mode, EnemyBattleAffix kind)
    {
        if (kind is EnemyBattleAffix.PredatoryLunge or EnemyBattleAffix.ArmorRend)
        {
            return;
        }
        var forms = new List<EffectForm>();
        var dice = GetEnemyBattleCapabilityDamageDiceNumber(character, kind);
        if (dice > 0)
        {
            // Brand-new damage forms contain neither the weapon's ability bonus nor its displayed bonus trends.
            forms.Add(EffectFormBuilder.Create().SetDamageForm(GetEnemyBattleEffectiveDamageType(character, kind), dice,
                kind is EnemyBattleAffix.ImprovisedThrow or EnemyBattleAffix.PiercingJab ? DieType.D4 : DieType.D6).Build());
        }
        if (kind is EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.PiercingJab &&
            GetEnemyBattleRiderCondition(kind) is { } condition)
        {
            var rider = EffectFormBuilder.Create().SetConditionForm(condition, ConditionForm.ConditionOperation.Add);
            if (kind == EnemyBattleAffix.ViscousSnare)
            {
                rider.HasSavingThrow(EffectSavingThrowType.Negates);
            }
            forms.Add(rider.Build());
        }
        var builder = EffectDescriptionBuilder.Create(mode.EffectDescription).ClearEffectAdvancements()
            .SetEffectForms(forms.ToArray()).SetDurationData(DurationType.Round, 1,
                (TurnOccurenceType)ExtraTurnOccurenceType.StartOfSourceTurn);
        if (kind == EnemyBattleAffix.ViscousSnare)
        {
            builder.SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Constitution);
        }
        else
        {
            var range = GetEnemyBattlePowerRange(character, kind);
            mode.closeRange = System.Math.Min(mode.CloseRange, range);
            mode.maxRange = System.Math.Min(mode.MaxRange, range);
            builder.Build().hasSavingThrow = false;
        }
        if (kind == EnemyBattleAffix.ImprovisedThrow)
        {
            var strength = AttributeDefinitions.ComputeAbilityScoreModifier(
                character.TryGetAttributeValue(AttributeDefinitions.Strength));
            var proficiency = character.TryGetProficiencyBonus();
            mode.abilityScore = AttributeDefinitions.Strength;
            mode.toHitBonus = strength + proficiency;
            mode.ToHitBonusTrends.Clear();
            mode.ToHitBonusTrends.Add(new TrendInfo(strength, FeatureSourceType.AbilityScore, AttributeDefinitions.Strength, null));
            mode.ToHitBonusTrends.Add(new TrendInfo(proficiency, FeatureSourceType.Proficiency, string.Empty, null));
        }
        mode.EffectDescription.Copy(builder.Build());
    }

    private static RulesetAttackMode BuildEnemyBattleImprovisedThrowMode(RulesetCharacter character) =>
        character is RulesetCharacterMonster monster && _enemyBattleImprovisedThrow != null
            ? monster.BuildAttackMode(_enemyBattleImprovisedThrow) : null;

    internal static void RestrictEnemyBattleNativeAttackEffects(RulesetCharacter actor, RulesetAttackMode mode,
        List<EffectForm> actualEffectForms)
    {
        if (!TryGetEnemyBattleNativeAttackKind(mode, out var kind) || kind == EnemyBattleAffix.PredatoryLunge)
        {
            return;
        }
        // Normalize immediately before native hit-confirmed processing; defender hooks must retain their later changes.
        // Attacker additions are rejected at their common entry points, before they spend any separate resource.
        RestrictEnemyBattleNativeAttackMode(actor, mode, kind);
        actualEffectForms.Clear();
        actualEffectForms.AddRange(mode.EffectDescription.EffectForms);
    }

    private sealed class EnemyBattleNativeAttack() : AddExtraAttackBase(ActionType.Bonus), IPhysicalAttackInitiatedByMe,
        IPhysicalAttackFinishedByMe, IActionFinishedByMe, IOnRefreshAttackModes
    {
        internal static readonly EnemyBattleNativeAttack Instance = new();

        protected override List<RulesetAttackMode> GetAttackModes(RulesetCharacter character)
        {
            var actor = GameLocationCharacter.GetFromActor(character);
            if (actor == null || !actor.IsMyTurn() || !Main.Settings.EnableEnemyBattleTactics)
            {
                return [];
            }
            var hasPending = EnemyBattleNativeAttackTargets.TryGetValue(character, out var pending) &&
                             IsCurrentEnemyBattleNativeAttackTarget(actor, pending);
            if (hasPending && pending.Consumed && pending.Kind != EnemyBattleAffix.SweepingVolley)
            {
                return [];
            }
            var kinds = hasPending ? new[] { pending.Kind } : new[]
                { EnemyBattleAffix.ViscousSnare, EnemyBattleAffix.FocusedVolley, EnemyBattleAffix.ImprovisedThrow,
                    EnemyBattleAffix.PiercingJab, EnemyBattleAffix.SweepingVolley };
            var attacks = new List<RulesetAttackMode>();
            foreach (var kind in kinds.Where(kind => CanUseEnemyBattlePower(actor, kind, out _)))
            {
                if (kind == EnemyBattleAffix.ImprovisedThrow)
                {
                    var throwMode = BuildEnemyBattleImprovisedThrowMode(character);
                    if (throwMode != null)
                    {
                        throwMode.AddAttackTagAsNeeded(GetEnemyBattleNativeAttackTag(kind));
                        RestrictEnemyBattleNativeAttackMode(character, throwMode, kind);
                        attacks.Add(throwMode);
                    }
                    continue;
                }
                var sources = kind == EnemyBattleAffix.PredatoryLunge
                    ? GetEnemyBattleOrdinaryAttackModes(character).Where(mode => mode.ActionType == ActionType.Main && !mode.Ranged && !IsEnemyBattleWeaponAttack(mode))
                    : new[] { kind is EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.PiercingJab ?
                        GetEnemyBattleNativeMeleeDamageMode(character, GetEnemyBattleDamageType(kind)) :
                        GetEnemyBattleRangedSource(character, kind)?.AttackMode };
                foreach (var source in sources.Where(source => source != null).ToArray())
                {
                    var bonus = source.Clone();
                    bonus.actionType = ActionType.Bonus;
                    bonus.attacksNumber = 1;
                    bonus.AddAttackTagAsNeeded(GetEnemyBattleNativeAttackTag(kind));
                    RestrictEnemyBattleNativeAttackMode(character, bonus, kind);
                    attacks.Add(bonus);
                }
            }
            return attacks;
        }

        public void AfterRefreshAttackModes(RulesetCharacterMonster monster)
        {
            // Run after native weapon modifiers so they cannot reintroduce the source attack's bonuses or riders.
            foreach (var mode in monster.AttackModes)
            {
                if (TryGetEnemyBattleNativeAttackKind(mode, out var kind))
                {
                    RestrictEnemyBattleNativeAttackMode(monster, mode, kind);
                }
            }
        }

        public IEnumerator OnPhysicalAttackInitiatedByMe(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier, RulesetAttackMode attackMode)
        {
            if (TryGetEnemyBattleNativeAttackKind(attackMode, out var kind) && kind != EnemyBattleAffix.ArmorRend && attacker?.RulesetCharacter != null &&
                action is CharacterActionAttack nativeAction && IsValidEnemyBattleNativeAttackAction(nativeAction) &&
                CanUseEnemyBattlePower(attacker, kind, out var usable))
            {
                EnemyBattleNativeAttackTargets.TryGetValue(attacker.RulesetCharacter, out var pending);
                if (pending == null || !IsCurrentEnemyBattleNativeAttackTarget(attacker, pending))
                {
                    pending = SetEnemyBattleNativeAttackTarget(attacker, kind, defender);
                    RestrictEnemyBattleNativeAttackMode(attacker.RulesetCharacter, attackMode, kind);
                }
                if (!pending.Consumed)
                {
                    // The native attack owns the bonus action and animation; commit its battle resource only at initiation.
                    pending.Issued = true;
                    pending.Consumed = true;
                    attacker.RulesetCharacter.UsePower(usable);
                }
            }
            yield break;
        }

        public IEnumerator OnPhysicalAttackFinishedByMe(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, RulesetAttackMode attackMode,
            RollOutcome outcome, int damageAmount)
        {
            if (attacker?.RulesetCharacter == null || defender?.RulesetCharacter == null ||
                TryGetEnemyBattleNativeAttackKind(attackMode, out _))
            {
                yield break;
            }
            if (action?.ActionParams == null || action.ActionType != ActionType.Main || action.Countered ||
                attackMode is not { ActionType: ActionType.Main, Ranged: false } || IsEnemyBattleWeaponAttack(attackMode) ||
                outcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
                damageAmount <= 0 && GetEnemyBattleLatestAttackDamage(action, defender.RulesetCharacter) <= 0 ||
                !attacker.IsMyTurn() || !attacker.IsOppositeSide(defender.Side) ||
                defender.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } victim ||
                victim.CurrentHitPoints > (victim.CurrentHitPoints + victim.MissingHitPoints) / 2 ||
                !CanUseEnemyBattlePower(attacker, EnemyBattleAffix.PredatoryLunge, out _))
            {
                yield break;
            }
            if (!EnemyBattleNativeAttackTargets.TryGetValue(attacker.RulesetCharacter, out var pending) ||
                !pending.Issued || !IsCurrentEnemyBattleNativeAttackTarget(attacker, pending))
            {
                SetEnemyBattleNativeAttackTarget(attacker, EnemyBattleAffix.PredatoryLunge, defender);
            }
        }

        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (TryGetEnemyBattleNativeAttackKind(action.ActionParams?.AttackMode, out var kind) && kind != EnemyBattleAffix.ArmorRend &&
                action.ActingCharacter?.RulesetCharacter is { } actor)
            {
                if (kind == EnemyBattleAffix.SweepingVolley && EnemyBattleNativeAttackTargets.TryGetValue(actor, out var pending) &&
                    pending.Kind == kind &&
                    (action.ActionParams.TargetCharacters.FirstOrDefault() != pending.Target ||
                     TryContinueEnemyBattleSweepingVolley(action, pending)))
                {
                    yield break;
                }
                EnemyBattleNativeAttackTargets.Remove(actor);
                actor.RefreshAttackModes();
            }
            yield break;
        }
    }

    private static bool TryExecuteEnemyBattleShove(GameLocationCharacter actor)
    {
        if (!TryGetEnemyBattleShoveTarget(actor, out var target, out var destination) ||
            !CanStartEnemyBattleBonusAction(actor) ||
            actor.GetActionStatus(Id.ShoveBonus, ActionScope.Battle) != ActionStatus.Available ||
            ServiceRepository.GetService<IGameLocationActionService>() is not { } actionService)
        {
            return false;
        }
        actionService.ExecuteAction(new CharacterActionParams(actor, Id.ShoveBonus)
        {
            TargetCharacters = { target },
            ActionModifiers = { new ActionModifier() },
            Positions = { destination },
            BoolParameter = false
        }, null, false);
        return true;
    }

    private static readonly ConditionalWeakTable<CharacterAction, RulesetUsablePower> EnemyBattleShoveUses = new();

    internal static bool CanUseEnemyBattleShove(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null)
        {
            return false;
        }
        return !EnemyBattleAffixes.TryGetValue(EnemyBattleAffix.DrivingShove, out var entry) ||
            FindEnemyBattleCondition(actor.RulesetCharacter, entry.Condition) == null ||
            HasOtherEnemyBattleShoveGrant(actor.RulesetCharacter, entry.Condition) ||
            CanUseEnemyBattlePower(actor, EnemyBattleAffix.DrivingShove, out _);
    }

    private static bool HasOtherEnemyBattleShoveGrant(RulesetCharacter character, ConditionDefinition condition) =>
        character.FeaturesByType<FeatureDefinitionActionAffinity>().Any(affinity =>
            !condition.Features.Contains(affinity) && affinity.AuthorizedActions.Contains(Id.ShoveBonus));

    internal static bool TryBeginEnemyBattleShove(CharacterActionShove action)
    {
        if (action?.ActionId != Id.ShoveBonus ||
            !EnemyBattleAffixes.TryGetValue(EnemyBattleAffix.DrivingShove, out var entry) ||
            action.ActingCharacter?.RulesetCharacter is not { } character ||
            FindEnemyBattleCondition(character, entry.Condition) == null ||
            HasOtherEnemyBattleShoveGrant(character, entry.Condition))
        {
            return true;
        }
        if (EnemyBattleShoveUses.TryGetValue(action, out _))
        {
            return true;
        }
        var actor = action.ActingCharacter;
        if (action.Countered || character.IsIncapacitated || !actor.IsMyTurn() ||
            actor.GetActionTypeStatus(ActionType.Bonus, ActionScope.Battle) != ActionStatus.Available ||
            HasCommittedBonusActionThisTurn(actor) ||
            !CanUseEnemyBattlePower(actor, EnemyBattleAffix.DrivingShove, out var usable) ||
            action.ActionParams.TargetCharacters.Count != 1 ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            return false;
        }
        var target = action.ActionParams.TargetCharacters[0];
        if (target?.RulesetCharacter is not { IsDeadOrDying: false, IsRemovedFromTheGame: false } ||
            !actor.IsOppositeSide(target.Side) || !actor.CanSeeTarget(target) ||
            CharacterActionShove.ComputeTargetValidityState(actor, target, battleService) !=
                CharacterActionShove.TargetValidityState.Valid)
        {
            return false;
        }
        // The native shove still pays its bonus action and owns its contested check and movement.
        // Both a successful and a failed attempt spend one battle use, committed only once at initiation.
        EnemyBattleShoveUses.Add(action, usable);
        character.UsePower(usable);
        return true;
    }

    private static bool IsSafeEnemyBattleDisplacementDestination(GameLocationCharacter actor, int3 destination)
    {
        // Extra AI safeguards are stricter than ordinary player-controlled teleport/shove targeting.
        // Never use a non-occupying proxy exception for the character that will actually be moved.
        return actor?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
               destination != int3.invalid && destination != actor.LocationPosition &&
               destination.y == actor.LocationPosition.y &&
               ServiceRepository.GetService<IGameLocationPositioningService>() is { } positioning &&
               IsLegalAiRouteDestination(actor, destination, allowNonOccupyingCombatProxy: false) &&
               positioning.CanPlaceStandingCharacter(actor, destination, CellHelpers.PlacementMode.Station) &&
               positioning.CanCharacterStayAtPosition_Floor(actor, destination, groundOnly: true,
                   onlyCheckCellsWithRealGround: true, needsFullSurfaceSupport: true) &&
               !positioning.IsDangerousPosition(actor, destination);
    }

    private static IEnumerator ComputeEnemyBattlePhaseStepDestination(GameLocationCharacter actor, Action<int3> completed)
    {
        completed(int3.invalid);
        if (!CanStartEnemyBattleBonusAction(actor) ||
            !CanUseEnemyBattlePower(actor, EnemyBattleAffix.PhaseStep, out _) ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService ||
            ServiceRepository.GetService<IGameLocationVisibilityService>() is not { } visibility)
        {
            yield break;
        }

        var start = actor.LocationPosition;
        var battle = Gui.Battle;
        var round = GetCurrentBattleRound();
        var enemies = SnapshotCharacters(actor.PerceivedFoes)
            .Where(enemy => enemy?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                            actor.IsOppositeSide(enemy.Side) && !IsNonOccupyingCombatProxyTarget(enemy))
            .OrderBy(enemy => enemy.Guid).ToArray();
        if (enemies.Length == 0)
        {
            yield break;
        }

        bool CanAttackFrom(int3 position) => actor.GetActionTypeStatus(ActionType.Main) == ActionStatus.Available &&
            enemies.Any(enemy => actor.PerceivedFoes.Contains(enemy) &&
                (CanUseActionKindAtPosition(actor, position, enemy, CombatAiActionKind.Spell, battleService) ||
                 CanUseActionKindAtPosition(actor, position, enemy, CombatAiActionKind.Ranged, battleService)));
        bool IsCurrent() => CanStartEnemyBattleBonusAction(actor) && ReferenceEquals(Gui.Battle, battle) &&
                            actor.LocationPosition == start && GetCurrentBattleRound() == round;

        var grapplers = actor.IsGrappled()
            ? enemies.Where(enemy => GrappleContext.GetGrappledActor(enemy.RulesetCharacter, out var grappled, out _) &&
                                     grappled == actor.RulesetCharacter).ToArray()
            : [];
        var restrained = grapplers.Length > 0;
        var threatened = enemies.Any(enemy => IsEnemyBattleMeleeThreatAt(actor, enemy, start, battleService));
        var canAttackNow = CanAttackFrom(start);
        if (!restrained && !threatened && canAttackNow)
        {
            yield break;
        }

        var box = new BoxInt(start, int3.zero, int3.zero);
        box.Inflate(6, 0, 6);
        var positions = new List<int3>();
        foreach (var position in box.EnumerateAllPositionsWithin())
        {
            positions.Add(position);
        }
        AiWorkBudget.Resume();
        foreach (var position in positions.OrderBy(position => int3.Distance(start, position))
                     .ThenBy(position => position.x).ThenBy(position => position.z))
        {
            if (AiWorkBudget.ShouldYield())
            {
                yield return null;
                if (!IsCurrent())
                {
                    yield break;
                }
                AiWorkBudget.Resume();
            }

            if (int3.Distance(start, position) > 6 ||
                !IsSafeEnemyBattleDisplacementDestination(actor, position) ||
                !visibility.IsCellPerceivedByCharacter(position, actor) ||
                enemies.Any(enemy => IsEnemyBattleMeleeThreatAt(actor, enemy, position, battleService)) ||
                grapplers.Any(grappler => battleService.IsWithinXCells(grappler, grappler.LocationPosition,
                    actor, position, 2)) ||
                !restrained && !CanAttackFrom(position))
            {
                continue;
            }

            // Moving clear of a grappler is resolved by the existing native motion/grapple validation.
            // Other restraint conditions keep their own native duration and area rules.
            if (IsCurrent())
            {
                completed(position);
            }
            yield break;
        }
    }

    private static bool TryGetEnemyBattleShoveTarget(GameLocationCharacter actor,
        out GameLocationCharacter target, out int3 destination, bool preserveMainAttack = true)
    {
        target = null;
        destination = int3.invalid;
        if (!CanStartEnemyBattleBonusAction(actor) ||
            !CanUseEnemyBattlePower(actor, EnemyBattleAffix.DrivingShove, out _) ||
            actor.GetActionStatus(Id.ShoveBonus, ActionScope.Battle) != ActionStatus.Available ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService ||
            ServiceRepository.GetService<IGameLocationEnvironmentService>() is not { } environment ||
            ServiceRepository.GetService<IGameLocationPositioningService>() is not { } positioning)
        {
            return false;
        }

        var allies = SnapshotCharacters(actor.PerceivedAllies).Where(ally => ally != actor &&
                ally?.RulesetCharacter is { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } &&
                !actor.IsOppositeSide(ally.Side) && Gui.Battle.AllContenders.Contains(ally))
            .Where(ally => BuildEnemyBattleProfile(ally) is var profile &&
                           (profile.PrefersDistance || profile.HasSpellcasting))
            .ToArray();

        foreach (var foe in SnapshotCharacters(actor.PerceivedFoes).Where(foe => foe != null).OrderBy(foe => foe.Guid))
        {
            if (foe?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } ||
                !actor.IsOppositeSide(foe.Side) || IsNonOccupyingCombatProxyTarget(foe) ||
                !actor.CanSeeTarget(foe) ||
                CharacterActionShove.ComputeTargetValidityState(actor, foe, battleService) !=
                CharacterActionShove.TargetValidityState.Valid ||
                !environment.ComputePushDestination(actor, foe, 1, false, positioning, out var position, out _) ||
                !IsSafeEnemyBattleDisplacementDestination(foe, position))
            {
                continue;
            }

            // Preserve a useful ordinary melee attack instead of spending the bonus to lose its target.
            if (preserveMainAttack && actor.GetActionTypeStatus(ActionType.Main) == ActionStatus.Available &&
                IsEnemyBattleMeleeThreatAt(foe, actor, foe.LocationPosition, battleService) &&
                !IsEnemyBattleMeleeThreatAt(foe, actor, position, battleService))
            {
                continue;
            }

            if (!allies.Any(ally => IsEnemyBattleMeleeThreatAt(ally, foe, ally.LocationPosition, battleService) &&
                    !battleService.IsWithinXCells(foe, position, ally, ally.LocationPosition,
                        GetEnemyBattleOrdinaryAttackModes(foe.RulesetCharacter).Where(IsMeleeAttackModeForAi)
                            .Select(mode => mode.ReachRange).DefaultIfEmpty(1).Max())))
            {
                continue;
            }

            target = foe;
            destination = position;
            return true;
        }
        return false;
    }

    private static bool IsEnemyBattleMeleeThreatAt(
        GameLocationCharacter actor, GameLocationCharacter enemy, int3 position, IGameLocationBattleService battleService)
    {
        if (enemy?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false })
        {
            return false;
        }

        // A foe's already-spent main action does not remove its reach or next-turn threat.
        foreach (var mode in GetEnemyBattleOrdinaryAttackModes(enemy.RulesetCharacter))
        {
            if (mode == null || !IsMeleeAttackModeForAi(mode) ||
                !battleService.IsWithinXCells(enemy, enemy.LocationPosition, actor, position, mode.ReachRange))
            {
                continue;
            }

            var attack = new BattleDefinitions.AttackEvaluationParams();
            attack.FillForPhysicalReachAttack(enemy, enemy.LocationPosition, mode, actor, position, new ActionModifier());
            if (battleService.CanAttack(attack))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEnemyBattleWithdrawalDestination(
        GameLocationCharacter actor, int3 start, int3 destination, GameLocationCharacter[] enemies,
        GameLocationCharacter[] threats, ReachableRouteDestinationMemory routes, IGameLocationBattleService battleService,
        bool requireOpportunityRisk = true)
    {
        const PathfindingNode.InformationFlag riskyMovement = PathfindingNode.InformationFlag.Drop |
            PathfindingNode.InformationFlag.DropStart | PathfindingNode.InformationFlag.Jump |
            PathfindingNode.InformationFlag.Climbing | PathfindingNode.InformationFlag.Vault;

        return destination != start && destination.y == start.y && routes.Contains(destination) &&
               routes.GetMoveCost(destination) <= actor.RemainingTacticalMoves &&
               (routes.GetMoveFlags(destination) & riskyMovement) == 0 &&
               IsLegalAiRouteDestination(actor, destination) && !IsRejectedAiMoveTarget(actor, start, destination) &&
               !enemies.Any(enemy => IsEnemyBattleMeleeThreatAt(actor, enemy, destination, battleService)) &&
               (!requireOpportunityRisk || threats.Any(enemy => enemy.GetActionTypeStatus(ActionType.Reaction) == ActionStatus.Available &&
                   enemy.CanPerformOpportunityAttackOnCharacter(actor, start, destination, out _, out _, true, battleService))) &&
               enemies.Any(enemy => enemy.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                   actor.PerceivedFoes.Contains(enemy) && TryGetRangedAttackModifierFromPosition(actor, destination,
                       enemy, enemy.LocationPosition, battleService, out _));
    }

    private const string EnemyBattleReprisalTag = "EnemyBattleReprisal";

    private static bool TryGetEnemyBattleUtilityTarget(GameLocationCharacter actor, EnemyBattleAffix kind,
        out GameLocationCharacter target, bool forAi = true)
    {
        target = null;
        if (actor?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } ||
            !EnemyBattleAffixes.TryGetValue(kind, out var entry) ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            return false;
        }

        var foes = SnapshotCharacters(actor.PerceivedFoes)
            .Where(foe => foe?.RulesetCharacter is { IsDeadOrDying: false, IsRemovedFromTheGame: false } &&
                (!forAi || !foe.RulesetCharacter.IsDeadOrDyingOrUnconscious) &&
                actor.IsOppositeSide(foe.Side) && !IsNonOccupyingCombatProxyTarget(foe))
            .OrderBy(foe => ComputeGridDistance(actor.LocationPosition, foe.LocationPosition))
            .ThenBy(foe => foe.Guid).ToArray();
        if (forAi && foes.Length == 0 && kind is not (EnemyBattleAffix.Restoration or EnemyBattleAffix.PurifyingTouch or EnemyBattleAffix.SecondWind))
        {
            return false;
        }

        if (kind == EnemyBattleAffix.Feint)
        {
            // Keep the bonus for a safe post-attack shove that frees a threatened ally.
            // The native turn may change positions; dispatch always validates the live board again.
            if ((!forAi || !TryGetEnemyBattleShoveTarget(actor, out _, out _, preserveMainAttack: false)) &&
                IsValidEnemyBattleUtilityTarget(actor, kind, actor, forAi))
            {
                target = actor;
            }
            return target != null;
        }
        if (entry.Power.EffectDescription.TargetType == TargetType.Self)
        {
            target = IsValidEnemyBattleUtilityTarget(actor, kind, actor, forAi) ? actor : null;
            return target != null;
        }

        var range = entry.Power.EffectDescription.RangeParameter;
        if (kind is EnemyBattleAffix.Restoration or EnemyBattleAffix.Command or EnemyBattleAffix.PackCall or
            EnemyBattleAffix.PurifyingTouch or EnemyBattleAffix.Rally or EnemyBattleAffix.SteadyingWord or EnemyBattleAffix.HeroicRally)
        {
            var allies = (_enemyTacticsBattle ?? Gui.Battle)?.EnemyContenders
                .Where(ally => (ally != actor || !entry.Power.EffectDescription.TargetExcludeCaster) &&
                    ally?.RulesetCharacter is { IsDeadOrDying: false, IsRemovedFromTheGame: false } &&
                    (!forAi || !ally.RulesetCharacter.IsDeadOrDyingOrUnconscious) &&
                    (!forAi || kind == EnemyBattleAffix.Restoration || !ally.RulesetCharacter.IsIncapacitated) &&
                    ally.Side == actor.Side && actor.IsWithinRange(ally, range) && actor.CanSeeTarget(ally));
            if (allies == null)
            {
                return false;
            }

            target = kind == EnemyBattleAffix.Restoration
                ? allies.Where(ally => IsValidEnemyBattleUtilityTarget(actor, kind, ally, forAi))
                    .OrderBy(ally => (double)ally.RulesetCharacter.CurrentHitPoints /
                                    Math.Max(1, ally.RulesetCharacter.CurrentHitPoints + ally.RulesetCharacter.MissingHitPoints))
                    .ThenBy(ally => ally.Guid).FirstOrDefault()
                : allies.Where(ally => IsValidEnemyBattleUtilityTarget(actor, kind, ally, forAi))
                    .OrderBy(ally => ally.Guid).FirstOrDefault();
            return target != null;
        }

        var validFoes = foes.Where(foe => IsValidEnemyBattleUtilityTarget(actor, kind, foe, forAi));
        target = GetEnemyBattleDamageType(kind) != null
            ? validFoes.OrderByDescending(foe => IsUsefulEnemyBattleDamageRider(actor, kind, foe)).FirstOrDefault()
            : validFoes.FirstOrDefault();
        return target != null;
    }

    private static bool IsValidEnemyBattleUtilityTarget(GameLocationCharacter actor, EnemyBattleAffix kind,
        GameLocationCharacter target, bool forAi)
    {
        if (actor?.RulesetCharacter == null || target?.RulesetCharacter is not
                { IsDeadOrDying: false, IsRemovedFromTheGame: false } ||
            forAi && kind != EnemyBattleAffix.Restoration && target.RulesetCharacter.IsIncapacitated ||
            !EnemyBattleAffixes.TryGetValue(kind, out var entry) ||
            !GetEnemyBattleEffectiveSelection(actor).Contains(kind) ||
            !actor.IsWithinRange(target, GetEnemyBattlePowerRange(actor.RulesetCharacter, kind)) || !actor.CanSeeTarget(target))
        {
            return false;
        }

        if (entry.Power.EffectDescription.TargetSide == Side.Ally &&
            !IsEnemyBattleSupportBodyEligible(actor, kind, target))
        {
            return false;
        }

        var effectiveDamageType = GetEnemyBattleEffectiveDamageType(actor.RulesetCharacter, kind);
        var hasDamage = effectiveDamageType != null && GetEnemyBattleCapabilityDamageDiceNumber(actor.RulesetCharacter, kind) > 0;
        if (kind != EnemyBattleAffix.RadiantBolt && (forAi || !hasDamage) &&
            HasEquivalentActiveEffectOrCondition(target.RulesetCharacter, entry.Power.EffectDescription))
        {
            return false;
        }

        if (kind == EnemyBattleAffix.Restoration)
        {
            var wounded = target.RulesetCharacter;
            return target.Side == actor.Side && !wounded.IsDeadOrDyingOrUnconscious && wounded.IsMissingHitPoints &&
                   (!forAi || wounded.CurrentHitPoints <= (wounded.CurrentHitPoints + wounded.MissingHitPoints) / 2 &&
                       wounded.MissingHitPoints >= 6) &&
                   !entry.Power.EffectDescription.ImmuneCreatureFamilies.Contains(wounded.CharacterFamily) &&
                   !wounded.FeaturesByType<IHealingModificationProvider>().Any(provider => provider.CannotGainHitPoints);
        }

        if (kind == EnemyBattleAffix.SecondWind)
        {
            var wounded = actor.RulesetCharacter;
            return target == actor && wounded.IsMissingHitPoints &&
                   (!forAi || wounded.CurrentHitPoints <= (wounded.CurrentHitPoints + wounded.MissingHitPoints) / 2 &&
                       wounded.MissingHitPoints >= 4) &&
                   !entry.Power.EffectDescription.ImmuneCreatureFamilies.Contains(wounded.CharacterFamily) &&
                   !wounded.FeaturesByType<IHealingModificationProvider>().Any(provider => provider.CannotGainHitPoints);
        }
        if (kind is EnemyBattleAffix.SteadyingWord or EnemyBattleAffix.UnsettlingCry or EnemyBattleAffix.HeroicRally &&
            (actor.RulesetCharacter.HasConditionOfTypeOrSubType("ConditionSilenced") ||
             target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionDefinitions.ConditionDeafened.Name)))
        {
            return false;
        }
        if (kind == EnemyBattleAffix.HeroicRally)
        {
            return target.Side == actor.Side && (!forAi || SnapshotCharacters(actor.PerceivedFoes).Any(foe =>
                foe?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } && target.IsOppositeSide(foe.Side) &&
                (target.RulesetCharacter.TemporaryHitPoints < actor.RulesetCharacter.TryGetProficiencyBonus() + 7 &&
                 HasUsefulEnemyBattleAttack(foe, target, ServiceRepository.GetService<IGameLocationBattleService>()) ||
                 GetEnemyBattleNativeEffects(foe.RulesetCharacter).Any(effect => effect.HasSavingThrow))));
        }
        if (kind is EnemyBattleAffix.Rally or EnemyBattleAffix.SteadyingWord)
        {
            return (target != actor || kind == EnemyBattleAffix.Rally) && target.Side == actor.Side &&
                   (kind != EnemyBattleAffix.Rally || !forAi ||
                    target.RulesetCharacter.TemporaryHitPoints < actor.RulesetCharacter.TryGetProficiencyBonus() + 2) &&
                   (!forAi || SnapshotCharacters(actor.PerceivedFoes).Any(foe => foe?.RulesetCharacter is
                       { IsDeadOrDyingOrUnconscious: false } && target.IsOppositeSide(foe.Side) &&
                       (kind == EnemyBattleAffix.Rally ? HasUsefulEnemyBattleAttack(foe, target,
                           ServiceRepository.GetService<IGameLocationBattleService>()) :
                        GetEnemyBattleNativeEffects(foe.RulesetCharacter).Any(effect => effect.HasSavingThrow))));
        }

        if (kind == EnemyBattleAffix.PurifyingTouch)
        {
            return actor.Side == target.Side && GetEnemyBattlePurifiableConditions(target.RulesetCharacter,
                ConditionDefinitions.ConditionCharmed, ConditionDefinitions.ConditionFrightened, ConditionDefinitions.ConditionPoisoned).Any();
        }

        if (ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            return false;
        }

        if (IsEnemyBattleWeaponCoating(kind))
        {
            return target == actor && !HasEnemyBattleWeaponCoating(actor.RulesetCharacter) &&
                (!forAi || HasAvailableAttackMainContinuation(actor) &&
                    GetEnemyBattleWeaponCoatingChoice(actor) != null);
        }
        if (kind == EnemyBattleAffix.GuardedStance)
        {
            return target == actor && (!forAi || SnapshotCharacters(actor.PerceivedFoes).Any(foe =>
                foe?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } &&
                actor.IsOppositeSide(foe.Side) && HasUsefulEnemyBattleAttack(foe, actor, battleService)));
        }
        if (kind == EnemyBattleAffix.PressingAttack)
        {
            return target == actor && (!forAi || HasAvailableAttackMainContinuation(actor) &&
                SnapshotCharacters(actor.PerceivedFoes).Any(foe => foe?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                    actor.IsOppositeSide(foe.Side) && HasUsefulEnemyBattlePhysicalAttack(actor, foe, battleService, false,
                        requireAvailableAttack: true)));
        }
        if (kind == EnemyBattleAffix.UnsettlingCry)
        {
            return actor.IsOppositeSide(target.Side) &&
                   (!forAi || IsEnemyBattleAttackHindranceUseful(target, actor, battleService));
        }

        if (kind == EnemyBattleAffix.Command)
        {
            return target.Side == actor.Side && (!forAi || SnapshotCharacters(actor.PerceivedFoes)
                .Any(foe => foe?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                    target.IsOppositeSide(foe.Side) &&
                    ((target != actor || HasAvailableAttackMainContinuation(actor)) &&
                     HasUsefulEnemyBattlePhysicalAttack(target, foe, battleService, requireNoAdvantage: true,
                         requireAvailableAttack: target == actor) ||
                     (target != actor || CanUseMainSpellActionForAi(actor)) &&
                     TryGetAtWillSpellAttackModifierFromPosition(target, target.LocationPosition, foe,
                         foe.LocationPosition, battleService, out var modifier, requireAttackRoll: true) &&
                     IsEnemyBattleAttackAdvantageUseful(modifier.AttackAdvantageTrends))));
        }
        if (kind == EnemyBattleAffix.PackCall)
        {
            return target.Side == actor.Side && GetFamily(actor.RulesetCharacter) == GetFamily(target.RulesetCharacter) &&
                   (!forAi || (target != actor || HasAvailableAttackMainContinuation(actor)) &&
                   SnapshotCharacters(actor.PerceivedFoes).Any(foe => foe?.RulesetCharacter is
                       { IsDeadOrDyingOrUnconscious: false } && target.IsOppositeSide(foe.Side) &&
                       HasUsefulEnemyBattlePhysicalAttack(target, foe, battleService, requireNoAdvantage: true,
                           requireAvailableAttack: target == actor)));
        }
        if (kind is EnemyBattleAffix.FeyVeil or EnemyBattleAffix.ConstructBrace)
        {
            if (target != actor || actor.RulesetCharacter.HasConditionOfTypeOrSubType(kind == EnemyBattleAffix.FeyVeil ?
                    ConditionDefinitions.ConditionInvisibleBase.Name : ConditionDefinitions.ConditionDodging.Name))
            {
                return false;
            }
            if (!forAi)
            {
                return true;
            }
            var foes = SnapshotCharacters(actor.PerceivedFoes).Where(foe => foe?.RulesetCharacter is
                { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } && actor.IsOppositeSide(foe.Side) &&
                (kind != EnemyBattleAffix.ConstructBrace || actor.CanSeeTarget(foe)));
            var affinityCondition = kind == EnemyBattleAffix.FeyVeil
                ? GetEnemyBattleRiderCondition(kind)
                : null;
            return foes.Any(foe => kind == EnemyBattleAffix.FeyVeil &&
                    (HasAvailableAttackMainContinuation(actor) &&
                     HasUsefulEnemyBattlePhysicalAttack(actor, foe, battleService, true,
                         requireAvailableAttack: true, affinityCondition: affinityCondition) ||
                     TryGetAtWillSpellAttackModifierFromPosition(actor, actor.LocationPosition, foe, foe.LocationPosition,
                         battleService, out var modifier, requireAttackRoll: true) &&
                     IsEnemyBattleConditionAffinityUseful(actor, foe, null, modifier, affinityCondition, false)) ||
                (kind == EnemyBattleAffix.FeyVeil
                    ? IsEnemyBattleAttackHindranceUseful(foe, actor, battleService, affinityCondition)
                    : HasUsefulEnemyBattleAttack(foe, actor, battleService)));
        }

        if (kind == EnemyBattleAffix.Feint)
        {
            return target == actor && (!forAi || HasAvailableAttackMainContinuation(actor) &&
                   SnapshotCharacters(actor.PerceivedFoes).Any(foe => foe?.RulesetCharacter is
                       { IsDeadOrDyingOrUnconscious: false } && actor.IsOppositeSide(foe.Side) &&
                       HasUsefulEnemyBattlePhysicalAttack(actor, foe, battleService, requireNoAdvantage: true,
                           requireAvailableAttack: true)));
        }

        var usefulDamage = hasDamage && (!forAi ||
            !target.RulesetCharacter.FeaturesByType<IDamageAffinityProvider>().Any(affinity =>
                affinity.DamageType == effectiveDamageType && affinity.DamageAffinityType == DamageAffinityType.Immunity));
        var riderCondition = GetEnemyBattleRiderCondition(kind);
        if (!actor.IsOppositeSide(target.Side) || !usefulDamage &&
            riderCondition != null && target.RulesetCharacter.IsImmuneToCondition(riderCondition.Name, actor.Guid, out _))
        {
            return false;
        }

        if (HasEnemyBattleCapabilityTrait(kind, EnemyBattleCapabilityTraits.UtilityTargetFamily))
        {
            return kind switch
            {
                EnemyBattleAffix.FiendMiasma => usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target),
                EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.LightningSurge =>
                    usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target),
                EnemyBattleAffix.WingBuffet => usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target),
                EnemyBattleAffix.PrimordialRepulsion => IsUsefulEnemyBattlePush(actor, target, forAi),
                EnemyBattleAffix.ViscousSnare or EnemyBattleAffix.PiercingJab =>
                    IsEnemyBattleNativeMeleeDamageTarget(actor, target, battleService, GetEnemyBattleDamageType(kind)) &&
                    (usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target)),
                EnemyBattleAffix.FocusedVolley or EnemyBattleAffix.ElementalLance or EnemyBattleAffix.SweepingVolley => usefulDamage &&
                    IsEnemyBattleNativeRangedTarget(actor, kind, target, battleService),
                EnemyBattleAffix.RadiantBolt => (usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target)) &&
                    IsEnemyBattleNativeRangedTarget(actor, kind, target, battleService),
                EnemyBattleAffix.ImprovisedThrow => usefulDamage && IsEnemyBattleImprovisedThrowTarget(actor, target, battleService),
                EnemyBattleAffix.CausticRetort => usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target),
                EnemyBattleAffix.RootSnare => !target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionRestrained) &&
                    !target.RulesetCharacter.IsImmuneToCondition(ConditionRestrained, actor.Guid, out _) &&
                    target.RulesetCharacter.IsTouchingGround() && (!forAi || target.CanDecideToMoveByItself),
                EnemyBattleAffix.WitheringMark => usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target),
                EnemyBattleAffix.PsychicBacklash => !forAi || IsEnemyBattleAttackHindranceUseful(target, actor, battleService),
                EnemyBattleAffix.BlindingEmbers => usefulDamage || IsUsefulEnemyBattleDamageRider(actor, kind, target),
                EnemyBattleAffix.StoneTremor => target.RulesetCharacter.IsTouchingGround() &&
                    !target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionProne) &&
                    !target.RulesetCharacter.IsImmuneToCondition(ConditionProne, actor.Guid, out _) &&
                    target.RulesetCharacter.SizeDefinition.WieldingSize <= actor.RulesetCharacter.SizeDefinition.WieldingSize + 1 &&
                    (!forAi || HasUsefulEnemyBattlePhysicalAttack(actor, target, battleService, true, meleeOnly: true)),
                _ => false
            };
        }
        return kind == EnemyBattleAffix.Dread
            ? !target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionFrightened) &&
              (!forAi || GetEnemyBattleOrdinaryAttackModes(target.RulesetCharacter).Any(mode => mode.ActionType == ActionType.Main) ||
               GetEnemyBattleKnownSpells(target.RulesetCharacter).Any(spell => spell.EffectDescription.HasDamageForm()))
            : kind == EnemyBattleAffix.ArcaneHindrance && target.RulesetCharacter.ConcentratedSpell != null &&
              !target.RulesetCharacter.FeaturesByType<ISpellCastingAffinityProvider>().Any(affinity =>
                  affinity.ConcentrationAffinity is ConcentrationAffinity.Disadvantage or ConcentrationAffinity.CannotConcentrate) &&
              (!forAi || HasEnemyBattleConcentrationPressure(actor, target, battleService));
    }

    private static bool IsEnemyBattleNativeMeleeDamageTarget(GameLocationCharacter actor, GameLocationCharacter target,
        IGameLocationBattleService battleService, string damageType)
    {
        var mode = GetEnemyBattleNativeMeleeDamageMode(actor.RulesetCharacter, damageType);
        if (mode == null)
        {
            return false;
        }
        return TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, target,
            target.LocationPosition, mode, battleService, out _);
    }

    private static bool IsEnemyBattleNativeRangedTarget(GameLocationCharacter actor, EnemyBattleAffix kind,
        GameLocationCharacter target, IGameLocationBattleService battleService)
    {
        if (GetEnemyBattleRangedSource(actor.RulesetCharacter, kind) is not { } source)
        {
            return false;
        }
        if (source.AttackMode == null)
        {
            return true;
        }
        return TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, target,
            target.LocationPosition, source.AttackMode, battleService, out _);
    }

    private static bool IsEnemyBattleImprovisedThrowTarget(GameLocationCharacter actor, GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        var mode = BuildEnemyBattleImprovisedThrowMode(actor.RulesetCharacter);
        if (mode == null)
        {
            return false;
        }
        try
        {
            RestrictEnemyBattleNativeAttackMode(actor.RulesetCharacter, mode, EnemyBattleAffix.ImprovisedThrow);
            return TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, target,
                target.LocationPosition, mode, battleService, out _);
        }
        finally
        {
            RulesetAttackMode.AttackModesPool.Return(mode);
        }
    }

    private static bool HasUsefulEnemyBattlePhysicalAttack(GameLocationCharacter actor, GameLocationCharacter foe,
        IGameLocationBattleService battleService, bool requireNoAdvantage, bool meleeOnly = false, bool weaponOnly = false,
        bool requireAvailableAttack = false, ConditionDefinition affinityCondition = null)
    {
        if (actor?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } ||
            foe?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false })
        {
            return false;
        }

        var attackModes = requireAvailableAttack
            ? actor.RulesetCharacter.AttackModes
            : GetEnemyBattleOrdinaryAttackModes(actor.RulesetCharacter);
        foreach (var mode in attackModes.Where(mode => mode.ActionType == ActionType.Main && (!meleeOnly || !IsRangedAttackMode(mode)) &&
                     (!weaponOnly || IsEnemyBattleWeaponAttack(mode)) &&
                     (!requireAvailableAttack ||
                      IsAttackModeAvailableForMainAction(actor, mode, out _) &&
                      actor.GetActionStatus(Id.AttackMain, ActionScope.Battle, optionalAttackMode: mode) == ActionStatus.Available &&
                      (actor.RulesetCharacter is not RulesetCharacterMonster monster || monster.GetRemainingAttackUses(mode) != 0))))
        {
            if (TryGetPhysicalAttackModifierFromPosition(actor, actor.LocationPosition, foe,
                    foe.LocationPosition, mode, battleService, out var modifier) &&
                (!requireNoAdvantage || (affinityCondition == null
                    ? IsEnemyBattleAttackAdvantageUseful(modifier.AttackAdvantageTrends)
                    : IsEnemyBattleConditionAffinityUseful(actor, foe, mode, modifier, affinityCondition, false))))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasUsefulEnemyBattleAttack(GameLocationCharacter attacker, GameLocationCharacter defender,
        IGameLocationBattleService battleService) =>
        HasUsefulEnemyBattlePhysicalAttack(attacker, defender, battleService, false) ||
        TryGetAtWillSpellAttackModifierFromPosition(attacker, attacker.LocationPosition, defender,
            defender.LocationPosition, battleService, out _);

    private static bool HasUsefulEnemyBattleDamage(RulesetCharacter target, EffectDescription effect) =>
        effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage &&
            (form.DamageForm.DiceNumber > 0 || form.DamageForm.BonusDamage > 0) &&
            !target.FeaturesByType<IDamageAffinityProvider>().Any(affinity =>
                affinity.DamageType == form.DamageForm.DamageType && affinity.DamageAffinityType == DamageAffinityType.Immunity));

    private static bool HasUsefulEnemyBattleCantripEffect(GameLocationCharacter actor, GameLocationCharacter target,
        EffectDescription effect) =>
        HasUsefulEnemyBattleDamage(target.RulesetCharacter, effect) || effect.EffectForms.Any(form => form.FormType switch
        {
            EffectForm.EffectFormType.Condition => form.ConditionForm.Operation == ConditionForm.ConditionOperation.Add &&
                form.ConditionForm.ConditionDefinition is { } condition &&
                !target.RulesetCharacter.HasConditionOfTypeOrSubType(condition.Name) &&
                !target.RulesetCharacter.IsImmuneToCondition(condition.Name, actor.Guid, out _),
            EffectForm.EffectFormType.Motion => form.MotionForm.Type switch
            {
                MotionForm.MotionType.PushFromOrigin => IsUsefulEnemyBattlePush(actor, target),
                MotionForm.MotionType.FallProne => target.RulesetCharacter.IsTouchingGround() &&
                    !target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionProne) &&
                    !target.RulesetCharacter.IsImmuneToCondition(ConditionProne, actor.Guid, out _),
                _ => false
            },
            _ => false
        });

    private static bool IsUsefulEnemyBattleDamageRider(GameLocationCharacter actor, EnemyBattleAffix kind,
        GameLocationCharacter target)
    {
        var riderCondition = GetEnemyBattleRiderCondition(kind);
        if (riderCondition != null && target.RulesetCharacter.IsImmuneToCondition(riderCondition.Name, actor.Guid, out _))
        {
            return false;
        }
        return kind switch
        {
            EnemyBattleAffix.FiendMiasma => !target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionPoisoned) &&
                !target.RulesetCharacter.IsImmuneToCondition(ConditionPoisoned, actor.Guid, out _) &&
                ServiceRepository.GetService<IGameLocationBattleService>() is { } poisonBattle &&
                IsEnemyBattleAttackHindranceUseful(target, actor, poisonBattle),
            EnemyBattleAffix.WitheringMark => target.RulesetCharacter.IsMissingHitPoints &&
                GetFamily(target.RulesetCharacter) is not (CombatAiFamily.Undead or CombatAiFamily.Construct) &&
                !target.RulesetCharacter.FeaturesByType<IHealingModificationProvider>().Any(provider => provider.CannotGainHitPoints),
            EnemyBattleAffix.BlindingEmbers => !target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionBlinded) &&
                !target.RulesetCharacter.IsImmuneToCondition(ConditionBlinded, actor.Guid, out _) &&
                ServiceRepository.GetService<IGameLocationBattleService>() is { } battleService &&
                HasUsefulEnemyBattleAttack(target, actor, battleService),
            EnemyBattleAffix.ThoughtLock or EnemyBattleAffix.LightningSurge => target.CanReact() &&
                !target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionDefinitions.ConditionShocked.Name) &&
                !target.RulesetCharacter.IsImmuneToCondition(ConditionDefinitions.ConditionShocked.Name, actor.Guid, out _),
            EnemyBattleAffix.CausticRetort => IsEnemyBattleMovementHindranceUseful(target),
            EnemyBattleAffix.ViscousSnare => IsEnemyBattleMovementHindranceUseful(target),
            EnemyBattleAffix.PiercingJab => !target.RulesetCharacter.HasConditionOfTypeOrSubType("ConditionMocked") &&
                ServiceRepository.GetService<IGameLocationBattleService>() is { } piercingBattle &&
                IsEnemyBattleAttackHindranceUseful(target, actor, piercingBattle),
            EnemyBattleAffix.RadiantBolt => IsEnemyBattleGuidingAdvantageUseful(actor, target),
            EnemyBattleAffix.WingBuffet => IsUsefulEnemyBattlePush(actor, target),
            _ => false
        };
    }

    private static ConditionDefinition GetEnemyBattleRiderCondition(EnemyBattleAffix kind) =>
        EnemyBattleAffixes.TryGetValue(kind, out var entry) ? entry.Power.EffectDescription.EffectForms
            .FirstOrDefault(form => form.FormType == EffectForm.EffectFormType.Condition &&
                form.ConditionForm.Operation == ConditionForm.ConditionOperation.Add)?.ConditionForm.ConditionDefinition : null;

    private static bool IsEnemyBattleGuidingAdvantageUseful(GameLocationCharacter actor, GameLocationCharacter target)
    {
        if (target.RulesetCharacter.HasConditionOfTypeOrSubType("ConditionTargetedByGuidingBolt") ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService)
        {
            return false;
        }
        var allies = ((_enemyTacticsBattle ?? Gui.Battle)?.EnemyContenders ?? []).Concat(new[] { actor }).Distinct();
        return allies.Any(ally => ally?.RulesetCharacter is { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } &&
            ally.Side == actor.Side &&
            (HasUsefulEnemyBattlePhysicalAttack(ally, target, battleService, requireNoAdvantage: true) ||
             TryGetAtWillSpellAttackModifierFromPosition(ally, ally.LocationPosition, target, target.LocationPosition,
                 battleService, out var cantripModifier, requireAttackRoll: true) && IsEnemyBattleAttackAdvantageUseful(cantripModifier.AttackAdvantageTrends) ||
             TryGetResidualHostileSpellFromPosition(ally, ally.LocationPosition, target, battleService,
                 out var spell, out _, out var spellModifier) && spell.EffectDescription.NeedsToRollDie() &&
             IsEnemyBattleAttackAdvantageUseful(spellModifier.AttackAdvantageTrends)));
    }

    private static bool IsEnemyBattleMovementHindranceUseful(GameLocationCharacter target)
    {
        return target?.RulesetCharacter is { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } actor &&
               target.CanDecideToMoveByItself && GetEnemyBattleOrdinaryMoveModes(actor).Values.Any(speed => speed > 0) &&
               !actor.IsImmuneToCondition(ConditionDefinitions.ConditionSlowed.Name, 0, out _) &&
               !actor.FeaturesByType<FeatureDefinitionMovementAffinity>().Any(affinity =>
                   affinity.BaseSpeedMultiplicativeModifier < 1) &&
               !actor.HasConditionOfTypeOrSubType(ConditionRestrained) &&
               !actor.HasConditionOfTypeOrSubType(ConditionGrappled);
    }

    private static bool IsEnemyBattleAttackHindranceUseful(GameLocationCharacter attacker, GameLocationCharacter defender,
        IGameLocationBattleService battleService, ConditionDefinition affinityCondition = null)
    {
        if (attacker?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false })
        {
            return false;
        }
        foreach (var mode in GetEnemyBattleOrdinaryAttackModes(attacker.RulesetCharacter).Where(mode => mode.ActionType == ActionType.Main))
        {
            if (TryGetPhysicalAttackModifierFromPosition(attacker, attacker.LocationPosition, defender,
                    defender.LocationPosition, mode, battleService, out var modifier) && (affinityCondition == null
                    ? IsEnemyBattleAttackDisadvantageUseful(modifier.AttackAdvantageTrends)
                    : IsEnemyBattleConditionAffinityUseful(attacker, defender, mode, modifier, affinityCondition, true)))
            {
                return true;
            }
        }
        return TryGetAtWillSpellAttackModifierFromPosition(attacker, attacker.LocationPosition, defender,
                   defender.LocationPosition, battleService, out var spellModifier, requireAttackRoll: true) &&
               (affinityCondition == null
                   ? IsEnemyBattleAttackDisadvantageUseful(spellModifier.AttackAdvantageTrends)
                   : IsEnemyBattleConditionAffinityUseful(attacker, defender, null, spellModifier, affinityCondition, true));
    }

    private static bool IsEnemyBattleConditionAffinityUseful(GameLocationCharacter attacker,
        GameLocationCharacter defender, RulesetAttackMode mode, ActionModifier modifier,
        ConditionDefinition condition, bool defensive)
    {
        if (condition == null ||
            ServiceRepository.GetService<IRulesetImplementationService>() is not { } implementation ||
            ServiceRepository.GetService<IGameLocationPositioningService>() is not { } positioning)
        {
            return false;
        }

        // Forecast only on a copy. Native affinity evaluation retains its family,
        // attack filter and sense-range rules without briefly applying a real condition.
        var predicted = new ActionModifier();
        predicted.AttackAdvantageTrends.AddRange(modifier.AttackAdvantageTrends);
        var source = defensive ? defender : attacker;
        var distance = positioning.ComputeDistanceBetweenCharactersApproximatingSize(attacker,
            attacker.LocationPosition, defender, defender.LocationPosition);
        foreach (var affinity in GetEnemyBattleConditionFeatures(condition).OfType<ICombatAffinityProvider>().Distinct())
        {
            var context = new RulesetImplementationDefinitions.SituationalContextParams(
                affinity.SituationalContext, attacker.RulesetCharacter, defender.RulesetCharacter,
                source.Guid, affinity.RequiredCondition, modifier.Proximity == AttackProximity.Range, null);
            if (!implementation.IsSituationalContextValid(context))
            {
                continue;
            }
            var origin = new FeatureOrigin(FeatureSourceType.Condition, condition.Name, condition, string.Empty);
            if (defensive)
            {
                affinity.ComputeDefenseModifier(defender.RulesetCharacter, attacker.RulesetCharacter,
                    0, false, predicted, origin, distance);
            }
            else
            {
                affinity.ComputeAttackModifier(attacker.RulesetCharacter, defender.RulesetCharacter,
                    mode, predicted, origin, 0, distance);
            }
        }

        var before = GetEnemyBattleAdvantageRank(modifier.AttackAdvantageTrends);
        var after = GetEnemyBattleAdvantageRank(predicted.AttackAdvantageTrends);
        return defensive ? after < before : after > before;
    }

    private static int GetEnemyBattleAdvantageRank(List<TrendInfo> trends) => ComputeAdvantage(trends) switch
    {
        AdvantageType.Advantage => 2,
        AdvantageType.None => 1,
        _ => 0
    };

    private static bool IsEnemyBattleAdvantageChangeUseful(List<TrendInfo> trends, int change)
    {
        var predicted = new List<TrendInfo>(trends) { new(change, FeatureSourceType.Condition, "EnemyBattle", null) };
        return Math.Sign(GetEnemyBattleAdvantageRank(predicted) - GetEnemyBattleAdvantageRank(trends)) == change;
    }

    private static bool IsEnemyBattleAttackDisadvantageUseful(List<TrendInfo> trends) =>
        IsEnemyBattleAdvantageChangeUseful(trends, -1);

    private static bool IsUsefulEnemyBattlePush(GameLocationCharacter actor, GameLocationCharacter target, bool forAi = true)
    {
        if (ServiceRepository.GetService<IGameLocationEnvironmentService>() is not { } environment ||
            ServiceRepository.GetService<IGameLocationPositioningService>() is not { } positioning ||
            ServiceRepository.GetService<IGameLocationBattleService>() is not { } battleService ||
            !environment.ComputePushDestination(actor, target, 1, false, positioning, out var destination, out _) ||
            !IsSafeEnemyBattleDisplacementDestination(target, destination))
        {
            return false;
        }
        if (forAi && actor.GetActionTypeStatus(ActionType.Main, ActionScope.Battle) == ActionStatus.Available &&
            BuildEnemyBattleProfile(actor).IsMeleeSpecialist &&
            !actor.RulesetCharacter.AttackModes.Any(mode => !mode.Ranged &&
                battleService.IsWithinXCells(actor, actor.LocationPosition, target, destination, mode.ReachRange)))
        {
            return false;
        }
        return !forAi || IsEnemyBattleMeleeThreatAt(actor, target, actor.LocationPosition, battleService) ||
               (_enemyTacticsBattle ?? Gui.Battle)?.EnemyContenders.Any(ally => ally != actor && ally.Side == actor.Side &&
                   ally.RulesetCharacter is { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } &&
                   IsEnemyBattleMeleeThreatAt(ally, target, ally.LocationPosition, battleService)) == true;
    }

    private static bool IsEnemyBattleAttackAdvantageUseful(List<TrendInfo> trends) =>
        IsEnemyBattleAdvantageChangeUseful(trends, 1);

    private static bool HasEnemyBattleConcentrationPressure(GameLocationCharacter actor, GameLocationCharacter target,
        IGameLocationBattleService battleService)
    {
        var allies = (_enemyTacticsBattle ?? Gui.Battle)?.EnemyContenders;
        return allies != null && allies.Any(ally => ally?.RulesetCharacter is
                { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } && ally.Side == actor.Side &&
            (HasUsefulEnemyBattlePhysicalAttack(ally, target, battleService, requireNoAdvantage: false) ||
             ally == actor &&
             (TryGetAtWillSpellAttackFromPosition(actor, actor.LocationPosition, target, target.LocationPosition,
                  battleService, out var cantrip, out _, out _) && cantrip.EffectDescription.HasDamageForm() ||
              TryGetResidualHostileSpellFromPosition(actor, actor.LocationPosition, target, battleService,
                  out var spell, out _, out _) && spell.EffectDescription.HasDamageForm())));
    }

    private static AssetReferenceSprite GetEnemyBattleStatusSprite(EnemyBattleAffix kind) =>
        GetEnemyBattleSprite($"ConditionEnemyBattle{kind}");

    private static AssetReferenceSprite GetEnemyBattleEffectSprite(EnemyBattleAffix kind) =>
        GetEnemyBattleSprite($"ConditionEnemyBattle{kind}Effect");

    private static AssetReferenceSprite GetEnemyBattleSprite(string name)
    {
        // Portrait conditions use a 32px glyph, matching native status icons.
        var bitmap = Resources.ResourceManager.GetObject(name) as byte[] ??
                     throw new InvalidOperationException($"Missing enemy battle capability sprite: {name}");
        return Sprites.GetSprite(name, bitmap, 32);
    }

    private static FeatureDefinitionPower BuildEnemyBattleRetortPower(string name)
    {
        const EnemyBattleAffix kind = EnemyBattleAffix.ArcaneRetort;
        var condition = ConditionDefinitionBuilder.Create("ConditionEnemyBattleArcaneRetortEffect")
            .SetGuiPresentation("Feature/&EnemyBattleArcaneRetortEffectTitle",
                "Feature/&EnemyBattleArcaneRetortEffectDescription", GetEnemyBattleEffectSprite(kind))
            .SetConditionType(ConditionType.Beneficial).SetPossessive()
            .SetAmountOrigin(ExtraOriginOfAmount.SourceProficiencyBonus)
            .SetSpecialDuration(DurationType.Round, 0, TurnOccurenceType.StartOfTurn)
            .SetSpecialInterruptions(ExtraConditionInterruption.AfterWasAttacked)
            .AddSpecialInterruptions(ConditionInterruption.BattleEnd)
            .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance)
            .AddFeatures(FeatureDefinitionReduceDamageBuilder.Create("ReduceDamageEnemyBattleArcaneRetort")
                .SetGuiPresentationNoContent(true)
                .SetAlwaysActiveReducedDamage((_, defender) => EnemyBattleRetort.GetRemainingAbsorption(defender))
                .SetDamageReductionFilter(EnemyBattleRetort.CanAbsorbDamage)
                .SetDamageReducedFeedback(EnemyBattleRetort.OnDamageReduced)
                .AddToDB()).AddToDB();
        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetEffectDescription(EffectDescriptionBuilder.Create(Shield)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Round, 0, TurnOccurenceType.StartOfTurn)
                .ClearEffectAdvancements().SetEffectForms(EffectFormBuilder.ConditionForm(condition)).Build())
            .SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance).AddToDB();
        power.AddCustomSubFeatures(new EnemyBattleRetort(power, condition));
        return power;
    }

    private sealed class EnemyBattleRetortContext(CharacterAction action, GameLocationCharacter attacker,
        GameLocationCharacter defender, ActionModifier modifier, RulesetAttackMode mode, RulesetEffect effect)
    {
        internal CharacterAction Action { get; } = action;
        internal GameLocationCharacter Attacker { get; } = attacker;
        internal GameLocationCharacter Defender { get; } = defender;
        internal ActionModifier Modifier { get; } = modifier;
        internal RulesetAttackMode Mode { get; } = mode;
        internal RulesetEffect Effect { get; } = effect;
        internal int Absorbed { get; set; }
    }

    private sealed class EnemyBattleRetort(FeatureDefinitionPower power, ConditionDefinition condition)
        : ITryAlterOutcomeAttack, IPhysicalAttackFinishedOnMe, IMagicEffectFinishedOnMe,
            IValidatePowerUse, IValidateMagicEffectBeforeSpend
    {
        private static readonly ConditionalWeakTable<RulesetCharacter, EnemyBattleRetortContext> Pending = new();

        internal static void Clear(RulesetCharacter actor) => Pending.Remove(actor);

        internal static void ResolvePendingReflection(GameLocationCharacter defender)
        {
            if (Pending.TryGetValue(defender.RulesetCharacter, out var context))
            {
                EnemyBattleAffixes[EnemyBattleAffix.ArcaneRetort].Power.GetFirstSubFeatureOfType<EnemyBattleRetort>()
                    .Finish(context.Action, defender);
            }
        }

        // AC-changing reactions run first; a miss must not spend this defense.
        public int HandlerPriority => 20;

        private static bool IsValidPending(EnemyBattleRetortContext context) =>
            context.Action is { Countered: false } && context.Action is not CharacterActionMagicEffect { ExecutionFailed: true } &&
            context.Defender?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
            context.Attacker?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
            context.Defender.IsOppositeSide(context.Attacker.Side) && context.Defender.CanSeeTarget(context.Attacker) &&
            (context.Mode?.Ranged == true || context.Mode == null &&
                context.Effect?.EffectDescription is { RangeType: RangeType.RangeHit }) &&
            (context.Mode?.EffectDescription ?? context.Effect?.EffectDescription) is { } damage &&
            HasUsefulEnemyBattleDamage(context.Defender.RulesetCharacter, damage) &&
            TryGetEnemyBattleAttackOutcome(context.Action, context.Attacker, context.Defender, context.Modifier,
                context.Mode, context.Effect, out _, out _, out _);

        internal static int GetRemainingAbsorption(GameLocationCharacter defender) =>
            defender?.RulesetCharacter is { } actor && Pending.TryGetValue(actor, out var context)
                ? System.Math.Max(0, actor.TryGetProficiencyBonus() - context.Absorbed) : 0;

        internal static bool CanAbsorbDamage(RulesetImplementationDefinitions.ApplyFormsParams forms, string damageType) =>
            forms.targetCharacter is RulesetCharacter defender && Pending.TryGetValue(defender, out var context) &&
            forms.sourceCharacter == context.Attacker.RulesetCharacter &&
            !HasEnemyBattleDamageImmunity(defender, damageType);

        internal static void OnDamageReduced(RulesetImplementationDefinitions.ApplyFormsParams forms, int amount, string damageType)
        {
            if (forms.targetCharacter is RulesetCharacter defender && Pending.TryGetValue(defender, out var context))
            {
                context.Absorbed += amount;
            }
        }

        public bool CanUsePower(RulesetCharacter actor, FeatureDefinitionPower definition) =>
            definition == power && Pending.TryGetValue(actor, out var context) && IsValidPending(context) &&
            CanUseEnemyBattlePower(context.Defender, EnemyBattleAffix.ArcaneRetort, out _) &&
            !actor.HasConditionOfType(condition.Name);

        public bool IsValid(CharacterActionMagicEffect action, GameLocationCharacter actor,
            IReadOnlyList<GameLocationCharacter> targets, out string failure)
        {
            failure = "Failure/&EnemyBattleTargetNotUseful";
            if (targets.Count != 1 || targets[0] != actor || !CanUsePower(actor.RulesetCharacter, power))
            {
                return false;
            }
            failure = null;
            return true;
        }

        public IEnumerator OnTryAlterOutcomeAttack(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, GameLocationCharacter helper,
            ActionModifier modifier, RulesetAttackMode attackMode, RulesetEffect effect)
        {
            if (helper != defender || defender?.RulesetCharacter == null || !defender.CanReact() ||
                !CanUseEnemyBattlePower(defender, EnemyBattleAffix.ArcaneRetort, out var usable) ||
                defender.RulesetCharacter.HasConditionOfType(condition.Name))
            {
                yield break;
            }
            var context = new EnemyBattleRetortContext(action, attacker, defender, modifier, attackMode, effect);
            if (!IsValidPending(context))
            {
                yield break;
            }
            Pending.Remove(defender.RulesetCharacter);
            Pending.Add(defender.RulesetCharacter, context);
            var activated = false;
            try
            {
                yield return defender.MyReactToUsePower(Id.PowerReaction, usable, [defender], attacker, "UsePower",
                    reactionValidated: () => activated = defender.RulesetCharacter.HasConditionOfType(condition.Name),
                    battleManager: battleManager);
            }
            finally
            {
                if (!activated)
                {
                    Pending.Remove(defender.RulesetCharacter);
                }
            }
        }

        private void Finish(CharacterAction action, GameLocationCharacter defender)
        {
            if (defender?.RulesetCharacter is not { } actor || !Pending.TryGetValue(actor, out var context) ||
                context.Action != action)
            {
                return;
            }
            Pending.Remove(actor);
            foreach (var applied in actor.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                         .Where(applied => applied.ConditionDefinition == condition).ToArray())
            {
                actor.RemoveCondition(applied);
            }
            if (context.Absorbed <= 0 || action.Countered || action is CharacterActionMagicEffect { ExecutionFailed: true } ||
                context.Attacker.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } target)
            {
                return;
            }
            var damage = new DamageForm { DamageType = DamageTypeForce, DieType = DieType.D1,
                DiceNumber = 0, BonusDamage = context.Absorbed };
            var forms = new RulesetImplementationDefinitions.ApplyFormsParams
            {
                sourceCharacter = actor, targetCharacter = target, position = context.Attacker.LocationPosition
            };
            // The reaction already paid for both effects. Reflection is damage,
            // so it cannot recursively trigger attack-only retaliation.
            RulesetActor.InflictDamage(context.Absorbed, damage, DamageTypeForce, forms, target, false,
                actor.Guid, false, [], new RollInfo(DieType.D1, [], context.Absorbed), false, out _);
            EffectHelpers.StartVisualEffect(defender, context.Attacker, EldritchBlast, EffectHelpers.EffectType.Impact);
        }

        public IEnumerator OnPhysicalAttackFinishedOnMe(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, RulesetAttackMode attackMode,
            RollOutcome outcome, int damageAmount)
        {
            Finish(action, defender);
            yield break;
        }

        public IEnumerator OnMagicEffectFinishedOnMe(CharacterAction action, GameLocationCharacter attacker,
            GameLocationCharacter defender, List<GameLocationCharacter> targets)
        {
            Finish(action, defender);
            yield break;
        }
    }

    private static FeatureDefinitionPower BuildEnemyBattleElementalAbsorptionPower(string name)
    {
        const EnemyBattleAffix kind = EnemyBattleAffix.ElementalAbsorption;
        var spell = SpellsContext.ElementalInfusion;
        var conditions = new Dictionary<string, ConditionDefinition>();
        foreach (var element in new[] { DamageTypeAcid, DamageTypeCold, DamageTypeFire, DamageTypeLightning, DamageTypeThunder })
        {
            var suffix = element.Substring(6);
            var originalDamage = GetDefinition<ConditionDefinition>($"ConditionElementalInfusion{suffix}Damage");
            var originalResistance = GetDefinition<ConditionDefinition>($"ConditionElementalInfusion{suffix}Resistance");
            FormattedDefinitionText Text(string titleKey, string bodyKey) => new(
                title: () => Gui.Format(titleKey, Gui.FormatDamageType(element, true)),
                description: () => Gui.Format(bodyKey, Gui.FormatDamageType(element, true)));
            var damage = ConditionDefinitionBuilder.Create(originalDamage, $"ConditionEnemyBattleElementalAbsorption{suffix}Damage")
                .SetGuiPresentation("Feature/&EnemyBattleElementalAbsorptionDamageTitle",
                    "Feature/&EnemyBattleElementalAbsorptionDamageDescription", GetEnemyBattleEffectSprite(kind))
                .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
                .AddCustomSubFeatures(Text("Feature/&EnemyBattleElementalAbsorptionDamageTitle",
                    "Feature/&EnemyBattleElementalAbsorptionDamageDescription"), Tooltips.HideHeaderImage.Instance)
                .AddToDB();
            damage.AddCustomSubFeatures(new EnemyBattleAbsorbedElement(damage));
            var resistance = ConditionDefinitionBuilder.Create(originalResistance, $"ConditionEnemyBattleElementalAbsorption{suffix}Resistance")
                .SetGuiPresentation("Feature/&EnemyBattleElementalAbsorptionResistanceTitle",
                    "Feature/&EnemyBattleElementalAbsorptionResistanceDescription", GetEnemyBattleEffectSprite(kind))
                // The stored energy becomes an attack rider on the next own turn, after the resistance expires.
                .SetFeatures(originalResistance.Features.Where(feature => feature is not FeatureDefinitionAdditionalDamage))
                .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
                .AddCustomSubFeatures(new EnemyBattleAbsorbedElement(damage),
                    Text("Feature/&EnemyBattleElementalAbsorptionResistanceTitle",
                        "Feature/&EnemyBattleElementalAbsorptionResistanceDescription"), Tooltips.HideHeaderImage.Instance)
                .AddToDB();
            conditions.Add(element, resistance);
        }
        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetReactionContext(ReactionTriggerContext.None)
            .SetEffectDescription(EffectDescriptionBuilder.Create(spell)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Round, 0, TurnOccurenceType.StartOfTurn)
                .ClearEffectAdvancements().Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance).AddToDB();
        var absorption = new EnemyBattleElementalAbsorption(power, conditions);
        power.AddCustomSubFeatures(absorption, absorption.EffectModifier,
            PowerBundle.UncachedEffectDescription.Marker);
        return power;
    }

    private sealed class EnemyBattleAbsorbedElement(ConditionDefinition damageCondition)
        : IOnConditionAddedOrRemoved, IPhysicalAttackFinishedByMe, IMagicEffectFinishedByMe
    {
        public void OnConditionAdded(RulesetCharacter target, RulesetCondition condition) { }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition condition)
        {
            var actor = GameLocationCharacter.GetFromActor(target);
            if (condition.ConditionDefinition == damageCondition || !condition.EndOfDurationReached || actor?.IsMyTurn() != true || Gui.Battle == null ||
                target.IsDeadOrDyingOrUnconscious)
            {
                return;
            }
            target.InflictCondition(damageCondition.Name, DurationType.Round, 0, TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect, target.Guid, target.CurrentFaction.Name, 1,
                damageCondition.Name, 1, 0, 0);
        }

        public IEnumerator OnPhysicalAttackFinishedByMe(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, RulesetAttackMode attackMode,
            RollOutcome outcome, int damageAmount)
        {
            if (attacker.IsMyTurn() && attackMode is { Ranged: false, Thrown: false } &&
                outcome is RollOutcome.Success or RollOutcome.CriticalSuccess)
            {
                attacker.RulesetCharacter.RemoveAllConditionsOfCategoryAndType(AttributeDefinitions.TagEffect, damageCondition.Name);
            }
            yield break;
        }

        public IEnumerator OnMagicEffectFinishedByMe(CharacterAction action, GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (attacker.IsMyTurn() && action is CharacterActionMagicEffect { Countered: false, ExecutionFailed: false } &&
                action.AttackRollOutcome is RollOutcome.Success or RollOutcome.CriticalSuccess &&
                action.ActionParams.RulesetEffect.EffectDescription.RangeType == RangeType.MeleeHit)
            {
                attacker.RulesetCharacter.RemoveAllConditionsOfCategoryAndType(AttributeDefinitions.TagEffect, damageCondition.Name);
            }
            yield break;
        }
    }

    private sealed class EnemyBattleElementalAbsorption(FeatureDefinitionPower power,
        Dictionary<string, ConditionDefinition> conditions)
        : IPhysicalAttackBeforeHitConfirmedOnMe, IMagicEffectBeforeHitConfirmedOnMe,
            IValidatePowerUse, IValidateMagicEffectBeforeSpend
    {
        private static readonly ConditionalWeakTable<RulesetCharacter, List<string>> Pending = new();
        internal IModifyEffectDescription EffectModifier { get; } = new ElementalEffectDescription(power, conditions);

        private static bool HasUsefulElement(RulesetCharacter actor, string element) =>
            !actor.FeaturesByType<IDamageAffinityProvider>().Any(affinity => affinity.DamageType == element &&
                affinity.DamageAffinityType is DamageAffinityType.Resistance or DamageAffinityType.Immunity);

        public bool CanUsePower(RulesetCharacter actor, FeatureDefinitionPower definition) =>
            definition == power && Pending.TryGetValue(actor, out var elements) && elements.Any(element => HasUsefulElement(actor, element)) &&
            actor.AreSpellComponentsValid(SpellsContext.ElementalInfusion) &&
            CanUseEnemyBattlePower(GameLocationCharacter.GetFromActor(actor), EnemyBattleAffix.ElementalAbsorption, out _);

        public bool IsValid(CharacterActionMagicEffect action, GameLocationCharacter actor,
            IReadOnlyList<GameLocationCharacter> targets, out string failure)
        {
            failure = "Failure/&EnemyBattleTargetNotUseful";
            return targets.Count == 1 && targets[0] == actor && CanUsePower(actor.RulesetCharacter, power);
        }

        private sealed class ElementalEffectDescription(FeatureDefinitionPower power,
            Dictionary<string, ConditionDefinition> conditions) : IModifyEffectDescription
        {
            private static readonly ConditionalWeakTable<RulesetEffect, List<string>> ActiveElements = new();

            public bool IsValid(BaseDefinition definition, RulesetCharacter actor, EffectDescription description) =>
                definition == power || definition == EnemyBattleFreeSpells[EnemyBattleAffix.ElementalAbsorption];

            public EffectDescription GetEffectDescription(BaseDefinition definition, EffectDescription description,
                RulesetCharacter actor, RulesetEffect effect)
            {
                if (effect == null || !ActiveElements.TryGetValue(effect, out var elements))
                {
                    // Usefulness is checked before commitment. Rechecking after resistance is applied
                    // would erase the spell's forms and make native IsOnGoing terminate its own conditions.
                    elements = effect?.TrackedConditionGuids.Count > 0
                        ? actor.ConditionsByCategory.Values.SelectMany(value => value)
                            .Where(condition => effect.TrackedConditionGuids.Contains(condition.Guid))
                            .SelectMany(condition => conditions.Where(entry => entry.Value == condition.ConditionDefinition)
                                .Select(entry => entry.Key)).Distinct().ToList()
                        : Pending.TryGetValue(actor, out var pending) ? pending.ToList() : [];

                    // Restored spells recover their elements from their own tracked conditions.
                    // The weak snapshot survives the reaction's pending scope without retaining the actor.
                    if (effect != null && elements.Count > 0)
                    {
                        ActiveElements.Add(effect, elements);
                    }
                }

                return EffectDescriptionBuilder.Create(description)
                    .SetEffectForms(elements.Select(element => EffectFormBuilder.ConditionForm(conditions[element])).ToArray())
                    .Build();
            }
        }

        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnMe(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier,
            RulesetAttackMode mode, bool rangedAttack, AdvantageType advantageType, List<EffectForm> forms,
            bool firstTarget, bool criticalHit) => React(battleManager, attacker, defender, forms);

        public IEnumerator OnMagicEffectBeforeHitConfirmedOnMe(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier,
            RulesetEffect effect, List<EffectForm> forms, bool firstTarget, bool criticalHit) =>
            React(battleManager, attacker, defender, forms);

        private IEnumerator React(GameLocationBattleManager battleManager, GameLocationCharacter attacker,
            GameLocationCharacter defender, IEnumerable<EffectForm> forms)
        {
            if (defender?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } actor || !defender.CanReact() ||
                !actor.AreSpellComponentsValid(SpellsContext.ElementalInfusion) ||
                !CanUseEnemyBattlePower(defender, EnemyBattleAffix.ElementalAbsorption, out var usable))
            {
                yield break;
            }
            var elements = forms.Where(form => form.FormType == EffectForm.EffectFormType.Damage &&
                    conditions.ContainsKey(form.DamageForm.DamageType) && HasUsefulElement(actor, form.DamageForm.DamageType))
                .Select(form => form.DamageForm.DamageType).Distinct().ToList();
            if (elements.Count == 0)
            {
                yield break;
            }
            using (new EnemyBattlePendingScope<RulesetCharacter, List<string>>(Pending, actor, elements))
            {
                yield return defender.MyReactToUsePower(Id.PowerReaction, usable, [defender], attacker ?? defender,
                    "UsePower", battleManager: battleManager);
            }
        }
    }

    private static FeatureDefinitionPower BuildEnemyBattleReachInterceptPower(string name)
    {
        const EnemyBattleAffix kind = EnemyBattleAffix.ReachIntercept;
        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetShowCasting(false).SetEffectDescription(BuildEnemyBattleMeleeMarkerDescription())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance,
                new ValidatorsValidatePowerUse(_ => false)).AddToDB();
        power.AddCustomSubFeatures(new EnemyBattleReachIntercept(power));
        return power;
    }

    private static bool IsEnemyBattleMeleeCantrip(SpellDefinition spell) =>
        spell.EffectDescription.RangeType is RangeType.MeleeHit or RangeType.Touch &&
        spell.EffectDescription.TargetType is TargetType.Individuals or TargetType.IndividualsUnique &&
        spell.EffectDescription.IsSingleTarget && spell.EffectDescription.TargetSide == Side.Enemy;

    private static List<SpellDefinition> GetEnemyBattleReachCantrips(GameLocationCharacter attacker,
        GameLocationCharacter defender, GameLocationBattleManager battleManager)
    {
        var actor = attacker.RulesetCharacter;
        if (attacker.GetActionStatus(Id.CastReaction, ActionScope.Battle) != ActionStatus.Available)
        {
            return [];
        }
        return GetEnemyBattleActionCantrips(actor).Where(IsEnemyBattleMeleeCantrip).Where(spell =>
        {
            if (!actor.CanCastCantrip(spell, out var repertoire))
            {
                return false;
            }
            using var scope = SpellCastingValidation.EnterSelectedRepertoire(repertoire);
            if (!actor.AreSpellComponentsValid(spell) || !SpellCastingValidation.IsValid(actor, repertoire, spell, null, out _))
            {
                return false;
            }
            var attack = new BattleDefinitions.AttackEvaluationParams();
            attack.FillForMagic(attacker, attacker.LocationPosition, spell.EffectDescription, spell.Name,
                defender, defender.LocationPosition, new ActionModifier());
            return MetamagicContext.CanAttackWithReactionMetamagic(battleManager, attack, true);
        }).ToList();
    }

    private sealed class EnemyBattleReachIntercept : CanMakeAoOOnReachEntered, ISpellCastingActionProvider
    {
        private readonly FeatureDefinitionPower _power;

        internal EnemyBattleReachIntercept(FeatureDefinitionPower power)
        {
            _power = power;
            Name = "EnemyBattleReachIntercept";
            AccountAoOImmunity = true;
            ValidateAttacker = actor => CanUseEnemyBattlePower(GameLocationCharacter.GetFromActor(actor), EnemyBattleAffix.ReachIntercept, out _);
            WeaponValidator = (mode, _, _) => IsEnemyBattleNativeMeleeAttack(mode);
        }

        public bool IsAllowed(RulesetCharacter actor, RulesetSpellRepertoire repertoire, SpellDefinition spell,
            ActionType actionType) => actionType == ActionType.Reaction && spell.SpellLevel == 0 &&
            spell.ActivationTime == ActivationTime.Action && IsEnemyBattleMeleeCantrip(spell) &&
            repertoire?.EnumerateReadyCantrips().Contains(spell) == true &&
            CanUseEnemyBattlePower(GameLocationCharacter.GetFromActor(actor), EnemyBattleAffix.ReachIntercept, out _);

        public bool CanCastSpellAsAction(CharacterActionMagicEffect action, RulesetEffectSpell spell, out string failure)
        {
            failure = "Failure/&EnemyBattleTargetNotUseful";
            if (action.ActionParams.UsablePower?.PowerDefinition != _power ||
                !IsAllowed(action.ActingCharacter.RulesetCharacter, spell.SpellRepertoire, spell.SpellDefinition, action.ActionType))
            {
                return false;
            }
            failure = string.Empty;
            return true;
        }

        public void OnSpellCastCommitted(CharacterActionMagicEffect action, RulesetEffectSpell spell) =>
            action.ActingCharacter.RulesetCharacter.UsePower(action.ActionParams.UsablePower);

        protected override bool IsValidAttackerForOpportunityAttack(GameLocationCharacter attacker,
            GameLocationCharacter mover, GameLocationBattleManager battleManager)
        {
            if (base.IsValidAttackerForOpportunityAttack(attacker, mover, battleManager))
            {
                return true;
            }
            // Native opportunity-attack availability requires a physical mode. A granted melee
            // cantrip instead uses the native casting action gate while preserving the other OA rules.
            var actor = attacker.RulesetCharacter;
            var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
            if (actor.IsDeadOrDyingOrUnconscious || actor.HasConditionOfType(ConditionDefinitions.ConditionProne.Name) ||
                mover.ChosenLegendaryAction?.NoOpportunityAttack == true || positioning == null ||
                attacker.GetActionTypeStatus(ActionType.Reaction, ActionScope.Battle) != ActionStatus.Available ||
                attacker.GetActionStatus(Id.CastReaction, ActionScope.Battle) != ActionStatus.Available)
            {
                return false;
            }
            var distance = positioning.ComputeDistanceBetweenCharactersApproximatingSize(attacker,
                attacker.LocationPosition, mover, mover.LocationPosition);
            return mover.RulesetCharacter.IsSubjectToAttackOfOpportunity(actor, distance);
        }

        private bool IsChoiceValid(ReactionRequestWarcaster request, int choice)
        {
            var parameters = request.ReactionParams;
            var attacker = parameters.ActingCharacter;
            var target = parameters.TargetCharacters.FirstOrDefault();
            var battleManager = ServiceRepository.GetService<IGameLocationBattleService>() as GameLocationBattleManager;
            if (attacker?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } ||
                target?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } || battleManager == null ||
                !attacker.IsOppositeSide(target.Side) || !attacker.CanSeeTarget(target) ||
                !CanUseEnemyBattlePower(attacker, EnemyBattleAffix.ReachIntercept, out _) ||
                !IsValidAttackerForOpportunityAttack(attacker, target, battleManager))
            {
                return false;
            }
            if (choice > 0)
            {
                return request.TryGetSpellChoice(choice, out var spell, out _) &&
                    GetEnemyBattleReachCantrips(attacker, target, battleManager).Contains(spell);
            }
            var mode = parameters.AttackMode;
            if (choice != 0 || mode == null || parameters.BoolParameter4 ||
                !battleManager.IsValidAttackerForOpportunityAttackOnCharacter(attacker, target))
            {
                return false;
            }
            var attack = new BattleDefinitions.AttackEvaluationParams();
            attack.FillForPhysicalReachAttack(attacker, attacker.LocationPosition, mode, target,
                target.LocationPosition, new ActionModifier());
            return battleManager.CanAttack(attack) || !mode.Ranged && mode.ReachRange <= 1 &&
                DistanceCalculation.GetDistanceFromCharacters(attacker, target, target.LocationPosition) <= mode.ReachRange;
        }

        protected override bool CanPerformReactionAttack(GameLocationCharacter attacker, GameLocationCharacter mover,
            (int3 from, int3 to)? movement, GameLocationBattleManager battleManager, bool allowRange,
            out RulesetAttackMode mode, out ActionModifier modifier)
        {
            mode = null;
            modifier = null;
            if (!movement.HasValue || !attacker.CanSeeTarget(mover, movement.Value.to) ||
                !IsValidAttackerForOpportunityAttack(attacker, mover, battleManager))
            {
                return false;
            }
            if (attacker.CanPerformOpportunityAttackOnReachEntered(mover, movement.Value.from, movement.Value.to,
                    out mode, out modifier, true, battleManager, WeaponValidator))
            {
                return true;
            }
            var from = DistanceCalculation.GetDistanceFromCharacters(attacker, mover, movement.Value.from);
            var to = DistanceCalculation.GetDistanceFromCharacters(attacker, mover, movement.Value.to);
            if (!GetEnemyBattleReachCantrips(attacker, mover, battleManager).Any(spell =>
                    from > spell.EffectDescription.RangeParameter && to <= spell.EffectDescription.RangeParameter))
            {
                return false;
            }
            modifier = new ActionModifier();
            return true;
        }

        protected override ReactionRequest MakeReactionRequest(GameLocationCharacter attacker, GameLocationCharacter defender,
            RulesetAttackMode mode, ActionModifier modifier)
        {
            var usable = AddUsablePowersFromCondition.GetGrantedUsablePowers(attacker.RulesetCharacter)
                .First(item => item.PowerDefinition == _power);
            var parameters = new CharacterActionParams(attacker, Id.AttackOpportunity)
            {
                AttackMode = mode, UsablePower = usable, StringParameter2 = Name, BoolParameter4 = mode == null, IsReactionEffect = true
            };
            parameters.TargetCharacters.Add(defender);
            parameters.ActionModifiers.Add(modifier);
            var battleManager = ServiceRepository.GetService<IGameLocationBattleService>() as GameLocationBattleManager;
            return new ReactionRequestWarcaster(Name, parameters, GetEnemyBattleReachCantrips(attacker, defender, battleManager),
                reactionValidated: request =>
                {
                    if (request.ReactionParams.RulesetEffect is not RulesetEffectSpell)
                    {
                        attacker.RulesetCharacter.UsePower(usable);
                    }
                }, validateChoice: IsChoiceValid)
            {
                Resource = new ReactionResourcePowerPool(_power)
            };
        }
    }

    private const string EnemyBattleCoordinatedAdvanceTag = "EnemyBattleCoordinatedAdvance";
    // A request's own pending entry must not make its final legality predicate reject itself.
    private static bool IsEnemyBattleReactionReady(GameLocationCharacter actor) =>
        actor?.RulesetCharacter is { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } &&
        !actor.IsCharging && !actor.MoveStepInProgress &&
        actor.GetActionTypeStatus(ActionType.Reaction, ActionScope.Battle) == ActionStatus.Available;
    private static readonly ConditionalWeakTable<RulesetAttackMode, EnemyBattleCoordinatedAttack>
        EnemyBattleCoordinatedAttacks = new();

    private static bool IsEnemyBattleCoordinatedAttackMode(RulesetAttackMode mode) =>
        mode is { ActionType: ActionType.Main } && mode.EffectDescription.HasDamageForm() &&
        mode.SourceDefinition is not MonsterAttackDefinition { AfterChargeOnly: true } &&
        !TryGetEnemyBattleNativeAttackKind(mode, out _) &&
        !mode.AttackTags.Contains(EnemyBattleCoordinatedAdvanceTag) &&
        (IsEnemyBattleWeaponAttack(mode) || mode.SourceDefinition is MonsterAttackDefinition);

    private sealed class EnemyBattleNativeModeSignature(RulesetAttackMode mode)
    {
        private readonly BaseDefinition _definition = mode.SourceDefinition;
        private readonly object _source = mode.SourceObject;
        private readonly int _toHit = mode.ToHitBonus;
        private readonly string _ability = mode.AbilityScore;
        private readonly int _reach = mode.ReachRange;
        private readonly float _closeRange = mode.CloseRange;
        private readonly float _maxRange = mode.MaxRange;
        private readonly bool _ranged = mode.Ranged;
        private readonly bool _thrown = mode.Thrown;
        private readonly string[] _tags = mode.AttackTags.ToArray();
        private readonly (string Type, int Number, DieType Die, int Bonus)[] _damage =
            GetEnemyBattleAttackDamageSignature(mode);

        internal bool Matches(RulesetAttackMode current) =>
            current != null && current.SourceDefinition == _definition && current.SourceObject == _source &&
            current.ToHitBonus == _toHit && current.AbilityScore == _ability && current.ReachRange == _reach &&
            current.CloseRange == _closeRange && current.MaxRange == _maxRange &&
            current.Ranged == _ranged && current.Thrown == _thrown &&
            current.AttackTags.Where(tag => tag != AttacksOfOpportunity.NotAoOTag &&
                tag != EnemyBattleCoordinatedAdvanceTag).SequenceEqual(_tags.Where(tag =>
                tag != AttacksOfOpportunity.NotAoOTag && tag != EnemyBattleCoordinatedAdvanceTag)) &&
            GetEnemyBattleAttackDamageSignature(current).SequenceEqual(_damage);
    }

    private sealed class EnemyBattleCoordinatedCommandContext(CharacterAction attack,
        GameLocationCharacter attacker, GameLocationCharacter defender, GameLocationCharacter commander,
        RulesetAttackMode mode, GameLocationBattleManager battleManager, RulesetUsablePower usablePower)
    {
        internal CharacterAction Attack { get; } = attack;
        internal GameLocationCharacter Attacker { get; } = attacker;
        internal GameLocationCharacter Defender { get; } = defender;
        internal GameLocationCharacter Commander { get; } = commander;
        internal RulesetAttackMode Mode { get; } = mode;
        internal EnemyBattleNativeModeSignature Signature { get; } = new(mode);
        internal GameLocationBattleManager BattleManager { get; } = battleManager;
        internal RulesetUsablePower UsablePower { get; } = usablePower;
        internal GameLocationBattle Battle { get; } = Gui.Battle;
        internal int Round { get; } = GetCurrentBattleRound();
        internal int Turn { get; } = ObservedCombatMemoryTurnStamp;
        internal int3 AttackerPosition { get; } = attacker.LocationPosition;
        internal int3 DefenderPosition { get; } = defender.LocationPosition;
        internal int3 CommanderPosition { get; } = commander.LocationPosition;
        internal bool CommanderAccepted { get; set; }
        internal bool Consumed { get; set; }
        internal HashSet<ulong> UsedAllies { get; } = [];
    }

    private sealed class EnemyBattleCoordinatedAttack(EnemyBattleCoordinatedCommandContext command,
        GameLocationCharacter ally, RulesetAttackMode nativeMode, RulesetAttackMode reactionMode)
    {
        internal EnemyBattleCoordinatedCommandContext Command { get; } = command;
        internal GameLocationCharacter Ally { get; } = ally;
        internal RulesetAttackMode ReactionMode { get; set; } = reactionMode;
        internal EnemyBattleNativeModeSignature Signature { get; } = new(nativeMode);
        internal int3 AllyPosition { get; } = ally.LocationPosition;
        internal bool Accepted { get; set; }
    }

    private static bool IsCurrentEnemyBattleCoordinatedCommand(EnemyBattleCoordinatedCommandContext context)
    {
        var commander = context.Commander;
        var attacker = context.Attacker;
        var defender = context.Defender;
        if (!Main.Settings.EnableEnemyBattleTactics || context.Battle == null ||
            !ReferenceEquals(context.Battle, Gui.Battle) || context.Round != GetCurrentBattleRound() ||
            context.Turn != ObservedCombatMemoryTurnStamp ||
            !ReferenceEquals(ServiceRepository.GetService<IGameLocationBattleService>(), context.BattleManager) ||
            context.Attack is not CharacterActionAttack { Aborted: false, Countered: false, ActionType: ActionType.Main } ||
            context.Attack.ActingCharacter != attacker || context.Attack.ActionParams.AttackMode != context.Mode ||
            !context.Attack.ActionParams.TargetCharacters.Contains(defender) ||
            context.Attack.AttackRollOutcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
            !IsEnemyBattleCoordinatedAttackMode(context.Mode) || !context.Signature.Matches(context.Mode) ||
            commander?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } source ||
            attacker?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false } ||
            defender?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false } ||
            commander == attacker || commander.IsMyTurn() || commander.Side != attacker.Side ||
            !commander.IsOppositeSide(defender.Side) || IsNonOccupyingCombatProxyTarget(defender) ||
            commander.LocationPosition != context.CommanderPosition || attacker.LocationPosition != context.AttackerPosition ||
            defender.LocationPosition != context.DefenderPosition || !commander.IsWithinRange(attacker, 6) ||
            !commander.CanSeeTarget(attacker) || !commander.CanSeeTarget(defender) ||
            source.HasConditionOfTypeOrSubType("ConditionSilenced") ||
            commander.IsCharging || commander.MoveStepInProgress ||
            !context.CommanderAccepted && !IsEnemyBattleReactionReady(commander) ||
            !GetEnemyBattleEffectiveSelection(commander).Contains(EnemyBattleAffix.CoordinatedAdvance) ||
            !EnemyBattleAffixes.TryGetValue(EnemyBattleAffix.CoordinatedAdvance, out var entry) ||
            FindEnemyBattleCondition(source, entry.Condition) == null ||
            context.UsablePower.PowerDefinition != entry.Power ||
            !AddUsablePowersFromCondition.GetGrantedUsablePowers(source).Contains(context.UsablePower) ||
            !context.Consumed && context.UsablePower.RemainingUses <= 0)
        {
            return false;
        }
        return true;
    }

    private static bool TryGetEnemyBattleCoordinatedAttackModifier(EnemyBattleCoordinatedCommandContext context,
        GameLocationCharacter ally, RulesetAttackMode mode, out ActionModifier modifier, bool requireReaction = true)
    {
        modifier = null;
        var commander = context.Commander;
        var defender = context.Defender;
        if (!IsCurrentEnemyBattleCoordinatedCommand(context) || ally == commander ||
            ally?.RulesetCharacter is not { IsIncapacitated: false, IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false } actor ||
            ally.Side != commander.Side || !commander.IsWithinRange(ally, 6) || !commander.CanSeeTarget(ally) ||
            !ally.CanSeeTarget(defender) || actor.HasConditionOfTypeOrSubType(ConditionDefinitions.ConditionDeafened.Name) ||
            ally.IsCharging || ally.MoveStepInProgress ||
            requireReaction && !IsEnemyBattleReactionReady(ally) || !IsEnemyBattleCoordinatedAttackMode(mode) ||
            actor is RulesetCharacterMonster monster && monster.GetRemainingAttackUses(mode) == 0)
        {
            return false;
        }
        return TryGetPhysicalAttackModifierFromPosition(ally, ally.LocationPosition, defender,
            defender.LocationPosition, mode, context.BattleManager, out modifier);
    }

    private static RulesetAttackMode GetEnemyBattleCoordinatedAttackMode(EnemyBattleCoordinatedCommandContext context,
        GameLocationCharacter ally, out ActionModifier modifier)
    {
        modifier = null;
        foreach (var mode in ally.RulesetCharacter.AttackModes.Where(IsEnemyBattleCoordinatedAttackMode)
                     .OrderByDescending(mode => GetEnemyBattleEffectAverageDamage(mode.EffectDescription)))
        {
            if (TryGetEnemyBattleCoordinatedAttackModifier(context, ally, mode, out modifier))
            {
                return mode;
            }
        }
        return null;
    }

    private static bool IsCurrentEnemyBattleCoordinatedAttack(EnemyBattleCoordinatedAttack attack, bool executing = false)
    {
        if (attack.Ally.LocationPosition != attack.AllyPosition || !attack.Signature.Matches(attack.ReactionMode) ||
            attack.ReactionMode.ActionType != ActionType.Reaction ||
            executing && (!attack.Accepted || !attack.Ally.ReactionEngaged) ||
            !attack.Accepted && attack.Command.UsedAllies.Contains(attack.Ally.Guid))
        {
            return false;
        }
        var nativeMode = attack.Ally.RulesetCharacter.AttackModes.FirstOrDefault(mode =>
            IsEnemyBattleCoordinatedAttackMode(mode) && attack.Signature.Matches(mode));
        return nativeMode != null && TryGetEnemyBattleCoordinatedAttackModifier(attack.Command,
            attack.Ally, nativeMode, out _, requireReaction: !executing);
    }

    private static FeatureDefinitionPower BuildEnemyBattleCoordinatedAdvancePower(string name)
    {
        const EnemyBattleAffix kind = EnemyBattleAffix.CoordinatedAdvance;
        var power = CreateEnemyBattlePowerBuilder(kind, name)
            .SetShowCasting(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Distance, 6, TargetType.IndividualsUnique, 2)
                .ExcludeCaster().SetDurationData(DurationType.Instantaneous).Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance,
                new ValidatorsValidatePowerUse(_ => false)).AddToDB();
        power.AddCustomSubFeatures(new EnemyBattleCoordinatedAdvance(power));
        return power;
    }

    private sealed class EnemyBattleCoordinatedAdvance(FeatureDefinitionPower power) : IPhysicalAttackFinishedByMeOrAlly
    {
        public IEnumerator OnPhysicalAttackFinishedByMeOrAlly(GameLocationBattleManager battleManager,
            CharacterAction action, GameLocationCharacter attacker, GameLocationCharacter defender,
            GameLocationCharacter helper, RulesetAttackMode attackMode, RollOutcome rollOutcome, int damageAmount)
        {
            if (action is not CharacterActionAttack { ActionType: ActionType.Main, Aborted: false, Countered: false } ||
                !IsEnemyBattleCoordinatedAttackMode(attackMode) || attacker == helper ||
                helper?.RulesetCharacter == null || attacker?.RulesetCharacter == null || defender?.RulesetCharacter == null ||
                !helper.CanReact() ||
                rollOutcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
                !CanUseEnemyBattlePower(helper, EnemyBattleAffix.CoordinatedAdvance, out var usable))
            {
                yield break;
            }
            var context = new EnemyBattleCoordinatedCommandContext(action, attacker, defender, helper,
                attackMode, battleManager, usable);
            if (!IsCurrentEnemyBattleCoordinatedCommand(context))
            {
                yield break;
            }
            var allies = context.Battle.GetContenders(helper, isOppositeSide: false, excludeSelf: true)
                .Where(ally => ally?.RulesetCharacter != null && ally.CanReact() && GetEnemyBattleCoordinatedAttackMode(context, ally, out _) != null)
                .OrderByDescending(ally => ally == attacker).ThenBy(ally => ally.Guid).ToArray();
            if (allies.Length == 0)
            {
                yield break;
            }
            bool CanCommand() => IsCurrentEnemyBattleCoordinatedCommand(context) &&
                allies.Any(ally => ally.CanReact() && GetEnemyBattleCoordinatedAttackMode(context, ally, out _) != null);
            yield return helper.MyReactToDoNothing(ExtraActionId.DoNothingReaction, attacker,
                "EnemyBattleCoordinatedAdvance", Gui.Localize(power.GuiPresentation.Description),
                reactionValidated: () => context.CommanderAccepted = true, battleManager: battleManager,
                resource: new ReactionResourcePowerPool(power),
                target: defender, effectDefinition: power, reactionStillValid: CanCommand);
            if (!context.CommanderAccepted || !IsCurrentEnemyBattleCoordinatedCommand(context))
            {
                yield break;
            }
            foreach (var ally in allies)
            {
                if (context.UsedAllies.Count >= 2 || !IsCurrentEnemyBattleCoordinatedCommand(context))
                {
                    yield break;
                }
                if (!ally.CanReact())
                {
                    continue;
                }
                var nativeMode = GetEnemyBattleCoordinatedAttackMode(context, ally, out var modifier);
                if (nativeMode == null)
                {
                    continue;
                }
                var reactionMode = nativeMode.Clone();
                reactionMode.actionType = ActionType.Reaction;
                reactionMode.AddAttackTagAsNeeded(AttacksOfOpportunity.NotAoOTag);
                reactionMode.AddAttackTagAsNeeded(EnemyBattleCoordinatedAdvanceTag);
                var pending = new EnemyBattleCoordinatedAttack(context, ally, nativeMode, reactionMode);
                try
                {
                    yield return ally.MyReactForOpportunityAttack(defender, attacker, reactionMode, modifier,
                        "EnemyBattleCoordinatedAdvance", reactionValidated: () =>
                        {
                            if (pending.Accepted)
                            {
                                return;
                            }
                            pending.Accepted = true;
                            context.UsedAllies.Add(ally.Guid);
                            if (!context.Consumed)
                            {
                                context.Consumed = true;
                                helper.RulesetCharacter.UsePower(usable);
                                helper.RulesetCharacter.LogCharacterUsedFeature(power);
                            }
                        }, battleManager: battleManager, reactionStillValid: () => IsCurrentEnemyBattleCoordinatedAttack(pending),
                        attackModePrepared: preparedMode =>
                        {
                            pending.ReactionMode = preparedMode;
                            EnemyBattleCoordinatedAttacks.Add(preparedMode, pending);
                        });
                }
                finally
                {
                    EnemyBattleCoordinatedAttacks.Remove(pending.ReactionMode);
                    reactionMode.Return();
                }
            }
        }
    }

    private static FeatureDefinitionPower BuildEnemyBattleReactionPower(EnemyBattleAffix kind, string name)
    {
        if (kind == EnemyBattleAffix.ArmorRend)
        {
            return BuildEnemyBattleArmorRendPower(name);
        }
        if (kind == EnemyBattleAffix.CoordinatedAdvance)
        {
            return BuildEnemyBattleCoordinatedAdvancePower(name);
        }
        if (kind == EnemyBattleAffix.ArcaneRetort)
        {
            return BuildEnemyBattleRetortPower(name);
        }
        if (kind == EnemyBattleAffix.ElementalAbsorption)
        {
            return BuildEnemyBattleElementalAbsorptionPower(name);
        }
        if (kind == EnemyBattleAffix.ReachIntercept)
        {
            return BuildEnemyBattleReachInterceptPower(name);
        }
        if (kind is EnemyBattleAffix.PsychicBacklash or EnemyBattleAffix.CausticRetort or EnemyBattleAffix.GraveLeech || IsEnemyBattleAreaDamage(kind))
        {
            return BuildEnemyBattleFamilyPower(kind, name);
        }
        var effectName = $"Condition{name}Effect";
        var sprite = GetEnemyBattleStatusSprite(kind);
        FeatureDefinitionPower power;

        switch (kind)
        {
            case EnemyBattleAffix.LastingSurge:
            {
                var acceleration = ConditionDefinitionBuilder.Create("ConditionEnemyBattleLastingSurgeEffect")
                    .SetGuiPresentation("Feature/&EnemyBattleLastingSurgeEffectTitle",
                        "Feature/&EnemyBattleLastingSurgeEffectDescription", GetEnemyBattleEffectSprite(kind))
                    .SetConditionType(ConditionType.Beneficial).SetPossessive()
                    .SetFeatures(ConditionDefinitions.ConditionHasted.Features)
                    .SetSpecialInterruptions(ConditionInterruption.BattleEnd)
                    .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance)
                    .AddToDB();
                power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                    .SetReactionContext(ReactionTriggerContext.None)
                    .SetEffectDescription(EffectDescriptionBuilder.Create(Haste)
                        .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                        // Zero remaining rounds expire at the next matching source-turn end,
                        // independently of whether a global round ends before that turn.
                        .SetDurationData(DurationType.Round, 0, TurnOccurenceType.EndOfSourceTurn)
                        .SetEffectForms(EffectFormBuilder.ConditionForm(acceleration)).Build())
                    .AddToDB();
                power.AddCustomSubFeatures(new EnemyBattleReactionPower(kind, power, acceleration));
                break;
            }
            case EnemyBattleAffix.ArcaneWard:
            {
                var shield = ConditionDefinitionBuilder
                    .Create(ConditionDefinitions.ConditionShielded, effectName)
                    .SetGuiPresentation($"Feature/&EnemyBattle{kind}EffectTitle", $"Feature/&EnemyBattle{kind}EffectDescription", GetEnemyBattleEffectSprite(kind))
                    .SetPossessive()
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd)
                    .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance)
                    .AddToDB();
                power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                    .SetEffectDescription(EffectDescriptionBuilder.Create(Shield)
                        .SetEffectForms(EffectFormBuilder.ConditionForm(shield))
                        .Build())
                    .AddToDB();
                power.AddCustomSubFeatures(new EnemyBattleReactionPower(kind, power, shield));
                break;
            }
            case EnemyBattleAffix.Spellbreaker:
                power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                    .SetEffectDescription(EffectDescriptionBuilder.Create(Counterspell)
                        .ClearEffectAdvancements()
                        .Build())
                    .AddToDB();
                power.AddCustomSubFeatures(
                    Counterspell2024Context.CounterspellPowerOrigin.Instance,
                    new EnemyBattleReactionPower(kind, power));
                break;
            case EnemyBattleAffix.Parry:
            {
                var parry = ConditionDefinitionBuilder.Create(effectName)
                    .SetGuiPresentation($"Feature/&EnemyBattle{kind}EffectTitle", $"Feature/&EnemyBattle{kind}EffectDescription", GetEnemyBattleEffectSprite(kind))
                    .SetPossessive()
                    .SetAmountOrigin(ExtraOriginOfAmount.SourceProficiencyBonus)
                    .SetFeatures(FeatureDefinitionAttributeModifierBuilder.Create($"AttributeModifier{name}")
                        .SetGuiPresentationNoContent(true)
                        .SetAddConditionAmount(AttributeDefinitions.ArmorClass)
                        .AddToDB())
                    .SetSpecialInterruptions(ExtraConditionInterruption.AfterWasAttacked)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd)
                    .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance)
                    .AddToDB();
                power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                    .SetEffectDescription(EffectDescriptionBuilder.Create()
                        .SetDurationData(DurationType.Round, 0, TurnOccurenceType.StartOfTurn)
                        .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                        .SetEffectForms(EffectFormBuilder.ConditionForm(parry))
                        .SetParticleEffectParameters(FeatureDefinitionPowers.PowerFighterActionSurge)
                        .SetAnimationMagicEffect(FeatureDefinitionPowers.PowerFighterActionSurge.EffectDescription
                            .AnimationMagicEffect)
                        .Build())
                    .SetShowCasting(true)
                    .AddToDB();
                power.AddCustomSubFeatures(new EnemyBattleReactionPower(kind, power, parry));
                break;
            }
            case EnemyBattleAffix.Bodyguard:
            {
                var protection = ConditionDefinitionBuilder.Create(effectName)
                    .SetGuiPresentation($"Feature/&EnemyBattle{kind}EffectTitle", $"Feature/&EnemyBattle{kind}EffectDescription", GetEnemyBattleEffectSprite(kind))
                    .SetPossessive()
                    .SetAmountOrigin(ExtraOriginOfAmount.SourceProficiencyBonus)
                    .SetSpecialInterruptions(ExtraConditionInterruption.AfterWasAttacked)
                    .AddSpecialInterruptions(ConditionInterruption.BattleEnd)
                    .AddCustomSubFeatures(Tooltips.HideHeaderImage.Instance)
                    .AddFeatures(FeatureDefinitionReduceDamageBuilder.Create($"ReduceDamage{name}")
                        .SetGuiPresentationNoContent(true)
                        .SetAlwaysActiveReducedDamage((_, defender) =>
                        {
                            var condition = defender.RulesetActor.ConditionsByCategory.Values
                                .SelectMany(conditions => conditions)
                                .FirstOrDefault(condition => condition.ConditionDefinition.Name == effectName);
                            return condition == null ? 0 : 2 + condition.Amount;
                        })
                        .AddToDB())
                    .AddToDB();
                power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                    .SetEffectDescription(EffectDescriptionBuilder.Create()
                        .SetDurationData(DurationType.Round, 0, TurnOccurenceType.StartOfTurn)
                        .SetTargetingData(Side.Ally, RangeType.Distance, 1, TargetType.IndividualsUnique, 1)
                        .SetEffectForms(EffectFormBuilder.ConditionForm(protection))
                        .SetParticleEffectParameters(FeatureDefinitionPowers.PowerKnightLeadership)
                        .SetAnimationMagicEffect(FeatureDefinitionPowers.PowerKnightLeadership.EffectDescription
                            .AnimationMagicEffect)
                        .Build())
                    .SetShowCasting(true)
                    .AddToDB();
                power.AddCustomSubFeatures(new EnemyBattleReactionPower(kind, power, protection));
                break;
            }
            case EnemyBattleAffix.Reprisal:
                power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                    .SetShowCasting(false)
                    .SetEffectDescription(BuildEnemyBattleMeleeMarkerDescription())
                    .AddToDB();
                power.AddCustomSubFeatures(new EnemyBattleReactionPower(kind, power));
                break;
            case EnemyBattleAffix.ElementalRebuke:
                power = CreateEnemyBattlePowerBuilder(kind, name, sprite)
                    .SetShowCasting(true)
                    .SetEffectDescription(EffectDescriptionBuilder.Create()
                        .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique, 1)
                        .SetDurationData(DurationType.Instantaneous)
                        .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                            EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Constitution)
                        .SetEffectForms(EffectFormBuilder.Create().SetDamageForm(DamageTypeFire, 1, DieType.D6)
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage).Build())
                        .SetAnimationMagicEffect(HellishRebuke.EffectDescription.AnimationMagicEffect)
                        .SetCasterEffectParameters(HellishRebuke)
                        .SetImpactEffectParameters(HellishRebuke)
                        .Build())
                    .AddToDB();
                power.AddCustomSubFeatures(new EnemyBattleReactionPower(kind, power));
                break;
            default:
                throw new System.ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        power.AddCustomSubFeatures(ModifyPowerVisibility.Hidden, Tooltips.HideHeaderImage.Instance);
        return power;
    }

    private static bool IsEnemyBattleWeaponAttack(RulesetAttackMode mode)
    {
        return mode != null &&
               (mode.SourceObject is RulesetItem { ItemDefinition.IsWeapon: true } ||
                mode.SourceDefinition is ItemDefinition { IsWeapon: true } ||
                mode.SourceDefinition is MonsterAttackDefinition monsterAttack &&
                (monsterAttack.ItemDefinitionMainHand is { IsWeapon: true } ||
                 monsterAttack.ItemDefinitionOffHand is { IsWeapon: true }));
    }

    // Called before the native targeted-spell coroutine, which scans spell repertoires rather than powers.
    internal static IEnumerator HandleEnemyBattleSpellTargeted(
        GameLocationBattleManager battleManager,
        GameLocationCharacter caster,
        RulesetEffectSpell spell,
        GameLocationCharacter defender)
    {
        if (caster == null || defender == null || spell == null ||
            !caster.IsOppositeSide(defender.Side) || !defender.CanReact() ||
            defender.RulesetActor.IsImmuneToSpell(RulesetEffectSpellWithOrigin.GetOriginSpell(spell)) ||
            !CanUseEnemyBattlePower(defender, EnemyBattleAffix.ArcaneWard, out var usablePower))
        {
            yield break;
        }

        var marker = usablePower.PowerDefinition.GetFirstSubFeatureOfType<EnemyBattleReactionPower>();
        var incomingSpell = RulesetEffectSpellWithOrigin.GetOriginSpell(spell);
        var protectsAgainstSpell = marker?.EffectCondition?.Features
            .OfType<FeatureDefinitionMagicAffinity>()
            .Any(affinity => affinity.SpellImmunities.Contains(incomingSpell.Name)) == true;

        if (!protectsAgainstSpell || !defender.RulesetCharacter.AreSpellComponentsValid(Shield))
        {
            yield break;
        }

        yield return defender.MyReactToUsePower(
            Id.PowerReaction, usablePower, [defender], caster, "UsePower", battleManager: battleManager);
    }

    // This veto runs only for native AI-controlled reaction decisions. Manual NPC control keeps its prompts.
    internal static bool IsEnemyBattleOnHitBonusRequest(ReactionRequest request) =>
        request is ReactionRequestCustom custom &&
        custom.EffectDefinition?.GetFirstSubFeatureOfType<EnemyBattleOnHitBonus>() != null;

    internal static bool IsEnemyBattleReactionUseful(ReactionRequest request)
    {
        var parameters = request?.ReactionParams;
        var actor = parameters?.ActingCharacter;
        if (request is ReactionRequestCustom whirlwindRequest &&
            whirlwindRequest.EffectDefinition?.GetFirstSubFeatureOfType<EnemyBattleArmorRend>() is { } armorRend)
        {
            return armorRend.IsReactionUseful(request);
        }
        if (request is ReactionRequestCustom savingThrowRequest &&
            savingThrowRequest.EffectDefinition?.GetFirstSubFeatureOfType<EnemyBattleSavingThrowReaction>() != null)
        {
            return actor?.RulesetCharacter != null && savingThrowRequest.Target == actor &&
                   EnemyBattlePendingSavingThrows.TryGetValue(actor.RulesetCharacter, out var savingThrowData) &&
                   IsValidEnemyBattleSavingThrowReaction(actor, savingThrowData);
        }
        if (IsEnemyBattleOnHitBonusRequest(request))
        {
            return actor?.RulesetCharacter != null &&
                   EnemyBattleOnHitBonusPending.TryGetValue(actor.RulesetCharacter, out var pending) &&
                   request is ReactionRequestCustom custom && custom.Target == pending.Defender &&
                   IsValidEnemyBattleOnHitBonus(pending) &&
                   !HasEnemyBattleDamageImmunity(pending.Defender.RulesetCharacter, pending.Kind == EnemyBattleAffix.CrushingBlow
                       ? pending.HitDamageForm.DamageForm.DamageType : DamageTypeRadiant);
        }
        var intercept = parameters?.UsablePower?.PowerDefinition.GetFirstSubFeatureOfType<EnemyBattleReachIntercept>();
        if (intercept != null)
        {
            return CanUseEnemyBattlePower(actor, EnemyBattleAffix.ReachIntercept, out _) &&
                   parameters.TargetCharacters.FirstOrDefault()?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                   IsEnemyBattleReactionReady(actor);
        }
        var power = parameters?.UsablePower?.PowerDefinition ??
                    (parameters?.RulesetEffect as RulesetEffectPower)?.PowerDefinition;
        if (power?.GetFirstSubFeatureOfType<EnemyBattleElementalAbsorption>() is { } absorption)
        {
            return actor?.RulesetCharacter != null && absorption.CanUsePower(actor.RulesetCharacter, power) &&
                   IsEnemyBattleReactionReady(actor);
        }
        var marker = power?.GetFirstSubFeatureOfType<EnemyBattleReactionPower>();

        if (marker == null)
        {
            if (parameters?.AttackMode?.AttackTags.Contains(EnemyBattleReprisalTag) != true)
            {
                return true;
            }

            return CanUseEnemyBattlePower(actor, EnemyBattleAffix.Reprisal, out _) &&
                   parameters.TargetCharacters.FirstOrDefault()?.RulesetCharacter is
                       { IsDeadOrDyingOrUnconscious: false } &&
                   IsEnemyBattleReactionReady(actor);
        }

        if (!CanUseEnemyBattlePower(actor, marker.Kind, out _) ||
            !IsEnemyBattleReactionReady(actor))
        {
            return false;
        }

        if (marker.Kind == EnemyBattleAffix.Spellbreaker)
        {
            return IsUsefulEnemyCounterspell(actor, parameters.TargetAction);
        }
        if (marker.Kind == EnemyBattleAffix.LastingSurge)
        {
            return !actor.IsMyTurn() && !HasEnemyBattleAcceleration(actor.RulesetCharacter) &&
                   parameters.TargetCharacters.Count == 1 && parameters.TargetCharacters[0] == actor;
        }

        if (marker.Kind == EnemyBattleAffix.ElementalRebuke)
        {
            return IsUsefulEnemyElementalRebuke(actor, parameters.TargetCharacters.FirstOrDefault());
        }
        if (marker.Kind == EnemyBattleAffix.GraveLeech)
        {
            return IsUsefulEnemyBattleGraveLeech(actor, parameters.TargetCharacters.FirstOrDefault(), forAi: true);
        }
        if (IsEnemyBattleAreaDamage(marker.Kind))
        {
            return IsUsefulEnemyBattleAreaDamage(actor, marker.Kind);
        }
        if (marker.Kind is EnemyBattleAffix.PsychicBacklash or EnemyBattleAffix.CausticRetort)
        {
            return IsValidEnemyBattleUtilityTarget(actor, marker.Kind, parameters.TargetCharacters.FirstOrDefault(), forAi: true);
        }

        if (marker.EffectCondition != null && actor.RulesetCharacter.HasConditionOfType(marker.EffectCondition.Name))
        {
            return false;
        }

        if (marker.Kind == EnemyBattleAffix.ArcaneWard)
        {
            return actor.RulesetCharacter.AreSpellComponentsValid(Shield);
        }

        var target = parameters.TargetCharacters.FirstOrDefault();
        return target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
               !actor.IsOppositeSide(target.Side) &&
               (marker.Kind != EnemyBattleAffix.Bodyguard || target != actor && actor.IsWithinRange(target, 1) &&
                   !HasEnemyBattleAttackDamageReduction(target.RulesetCharacter));
    }

    private static bool IsUsefulEnemyCounterspell(GameLocationCharacter actor, CharacterAction targetAction)
    {
        var caster = targetAction?.ActingCharacter;
        if (targetAction == null || targetAction.Countered ||
            targetAction.ActionParams?.RulesetEffect is not RulesetEffectSpell spell ||
            actor == null || caster?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } ||
            !actor.IsOppositeSide(caster.Side) || !actor.CanSeeTarget(caster) ||
            RulesetEffectSpellWithOrigin.GetOriginSpell(spell)?.SpellLevel is not > 0 ||
            CombinedMetamagic.HasType(spell.MetamagicOption, MetamagicType.SubtleSpell) ||
            !actor.RulesetCharacter.AreSpellComponentsValid(Counterspell) ||
            !actor.IsWithinRange(caster, Counterspell.EffectDescription.RangeParameter) ||
            caster.RulesetActor.IsImmuneToSpellLevel(caster.LocationPosition, actor.LocationPosition,
                Counterspell.SpellLevel, out _))
        {
            return false;
        }

        // A recharging counter protects against damaging/control/summoning magic or meaningful healing,
        // rather than spending its only reaction on light, identification or other incidental utilities.
        return spell.EffectDescription.EffectForms.Any(form => form.FormType is
                   EffectForm.EffectFormType.Damage or EffectForm.EffectFormType.Healing or
                   EffectForm.EffectFormType.Summon or EffectForm.EffectFormType.Motion ||
               form.FormType == EffectForm.EffectFormType.Condition &&
               form.ConditionForm.Operation == ConditionForm.ConditionOperation.Add &&
               form.ConditionForm.ConditionDefinition is { ConditionType: ConditionType.Detrimental });
    }

    private static bool IsUsefulEnemyElementalRebuke(GameLocationCharacter actor, GameLocationCharacter target)
    {
        if (actor?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } || actor.IsMyTurn())
        {
            return false;
        }

        var element = GetEnemyBattleNativeElement(actor.RulesetCharacter);
        return element != null && GetEnemyBattleDamageDiceNumber(actor.RulesetCharacter) > 0 &&
               target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
               actor.IsOppositeSide(target.Side) &&
               actor.IsWithinRange(target,
                   EnemyBattleAffixes[EnemyBattleAffix.ElementalRebuke].Power.EffectDescription.RangeParameter) &&
               actor.CanSeeTarget(target) &&
               !target.RulesetCharacter.FeaturesByType<IDamageAffinityProvider>().Any(affinity =>
                   affinity.DamageType == element && affinity.DamageAffinityType == DamageAffinityType.Immunity);
    }

    private static bool HasEnemyBattleDamageImmunity(RulesetCharacter character, string damageType) =>
        character.FeaturesByType<IDamageAffinityProvider>().Any(affinity =>
            affinity.DamageType == damageType && affinity.DamageAffinityType == DamageAffinityType.Immunity);

    private static bool IsUsefulEnemyBattleGraveLeech(GameLocationCharacter actor, GameLocationCharacter target,
        bool forAi) =>
        actor?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } && !actor.IsMyTurn() &&
        target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } && actor.IsOppositeSide(target.Side) &&
        actor.IsWithinRange(target, 2) && actor.CanSeeTarget(target) &&
        GetEnemyBattleCapabilityDamageDiceNumber(actor.RulesetCharacter, EnemyBattleAffix.GraveLeech) > 0 &&
        (!forAi || !HasEnemyBattleDamageImmunity(target.RulesetCharacter, DamageTypeNecrotic));

    internal static bool ShouldReserveEnemyBattleOnHitBonus(GameLocationCharacter actor) =>
        actor?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } && actor.IsMyTurn() &&
        GetAttackMainActionStatusForAi(actor) == ActionStatus.Available &&
        actor.GetActionTypeStatus(ActionType.Bonus, ActionScope.Battle) == ActionStatus.Available &&
        !HasCommittedBonusActionThisTurn(actor) &&
        ServiceRepository.GetService<IGameLocationBattleService>() is { } battleService &&
        new[] { EnemyBattleAffix.RadiantSmite, EnemyBattleAffix.CrushingBlow }.Any(kind =>
            CanUseEnemyBattlePower(actor, kind, out _) && SnapshotCharacters(actor.PerceivedFoes).Any(target =>
                target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } && actor.IsOppositeSide(target.Side) &&
                actor.RulesetCharacter.AttackModes.Any(mode => mode.ActionType == ActionType.Main && !mode.Ranged && !mode.Thrown &&
                    (kind != EnemyBattleAffix.CrushingBlow || IsEnemyBattleCrushingAttack(mode)) &&
                    !HasEnemyBattleDamageImmunity(target.RulesetCharacter, kind == EnemyBattleAffix.CrushingBlow
                        ? mode.EffectDescription.EffectForms.First(form => form.FormType == EffectForm.EffectFormType.Damage).DamageForm.DamageType
                        : DamageTypeRadiant)) &&
                HasUsefulEnemyBattlePhysicalAttack(actor, target, battleService, false, meleeOnly: true)));

    private sealed class EnemyBattleOnHitBonusContext(
        GameLocationCharacter attacker, GameLocationCharacter defender, RulesetAttackMode attackMode,
        List<EffectForm> forms, GameLocationBattleManager battleManager, EnemyBattleAffix kind = EnemyBattleAffix.RadiantSmite)
    {
        internal EnemyBattleAffix Kind { get; } = kind;
        internal GameLocationCharacter Attacker { get; } = attacker;
        internal GameLocationCharacter Defender { get; } = defender;
        internal RulesetAttackMode AttackMode { get; } = attackMode;
        internal BaseDefinition SourceDefinition { get; } = attackMode.SourceDefinition;
        internal object SourceObject { get; } = attackMode.SourceObject;
        internal ActionType ModeActionType { get; } = attackMode.ActionType;
        internal int ReachRange { get; } = attackMode.ReachRange;
        internal int ToHitBonus { get; } = attackMode.ToHitBonus;
        internal (string Type, int Number, DieType Die, int Bonus)[] NativeDamage { get; } =
            GetEnemyBattleAttackDamageSignature(attackMode);
        internal int3 AttackerPosition { get; } = attacker.LocationPosition;
        internal int3 DefenderPosition { get; } = defender?.LocationPosition ?? int3.invalid;
        internal EffectForm ModeDamageForm { get; } = attackMode.EffectDescription.EffectForms
            .FirstOrDefault(form => form.FormType == EffectForm.EffectFormType.Damage);
        internal List<EffectForm> Forms { get; } = forms;
        internal EffectForm HitDamageForm { get; } = forms.FirstOrDefault(form => form.FormType == EffectForm.EffectFormType.Damage);
        internal GameLocationBattleManager BattleManager { get; } = battleManager;
        internal GameLocationBattle Battle { get; } = Gui.Battle;
        internal int Round { get; } = GetCurrentBattleRound();
        internal int Turn { get; } = ObservedCombatMemoryTurnStamp;
        internal bool Consumed { get; set; }
    }

    private static (string Type, int Number, DieType Die, int Bonus)[] GetEnemyBattleAttackDamageSignature(RulesetAttackMode mode) =>
        mode.EffectDescription.EffectForms.Where(form => form.FormType == EffectForm.EffectFormType.Damage)
            .Select(form => (form.DamageForm.DamageType, form.DamageForm.DiceNumber,
                form.DamageForm.DieType, form.DamageForm.BonusDamage)).ToArray();

    private static bool IsValidEnemyBattleOnHitBonus(EnemyBattleOnHitBonusContext context)
    {
        var actor = context.Attacker;
        var target = context.Defender;
        var mode = context.AttackMode;
        if (context.Consumed || actor?.RulesetCharacter is not
                { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } ||
            target?.RulesetCharacter is not { IsDeadOrDying: false, IsRemovedFromTheGame: false } ||
            !ReferenceEquals(context.Battle, Gui.Battle) || context.Round != GetCurrentBattleRound() ||
            context.Turn != ObservedCombatMemoryTurnStamp || !actor.IsMyTurn() || !actor.IsOppositeSide(target.Side) ||
            mode is not { Ranged: false, Thrown: false } || mode.ActionType == ActionType.Bonus ||
            TryGetEnemyBattleNativeAttackKind(mode, out _) ||
            mode.SourceDefinition != context.SourceDefinition || mode.SourceObject != context.SourceObject ||
            mode.ActionType != context.ModeActionType || mode.ReachRange != context.ReachRange || mode.ToHitBonus != context.ToHitBonus ||
            actor.LocationPosition != context.AttackerPosition || target.LocationPosition != context.DefenderPosition ||
            !GetEnemyBattleAttackDamageSignature(mode).SequenceEqual(context.NativeDamage) ||
            !actor.RulesetCharacter.AttackModes.Any(current =>
                current.SourceDefinition == context.SourceDefinition && current.SourceObject == context.SourceObject &&
                (current.ActionType == context.ModeActionType ||
                 context.ModeActionType == ActionType.Reaction && current.ActionType == ActionType.Main) &&
                !current.Ranged && !current.Thrown &&
                current.ReachRange == context.ReachRange && current.ToHitBonus == context.ToHitBonus &&
                !TryGetEnemyBattleNativeAttackKind(current, out _) &&
                GetEnemyBattleAttackDamageSignature(current).SequenceEqual(context.NativeDamage)) ||
            context.ModeDamageForm == null || !mode.EffectDescription.EffectForms.Contains(context.ModeDamageForm) ||
            context.HitDamageForm == null || !context.Forms.Contains(context.HitDamageForm) ||
            context.Kind == EnemyBattleAffix.CrushingBlow && context.HitDamageForm.DamageForm.DamageType is not
                (DamageTypeBludgeoning or DamageTypePiercing or DamageTypeSlashing) ||
            actor.GetActionTypeStatus(ActionType.Bonus, ActionScope.Battle) != ActionStatus.Available ||
            HasCommittedBonusActionThisTurn(actor) ||
            !CanUseEnemyBattlePower(actor, context.Kind, out _))
        {
            return false;
        }
        var evaluation = new BattleDefinitions.AttackEvaluationParams();
        evaluation.FillForPhysicalReachAttack(actor, actor.LocationPosition, mode, target, target.LocationPosition,
            new ActionModifier());
        return context.BattleManager.CanAttack(evaluation);
    }

    private sealed class EnemyBattleOnHitBonus(FeatureDefinitionPower power, EnemyBattleAffix kind) : IPhysicalAttackBeforeHitConfirmedOnEnemy
    {
        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier,
            RulesetAttackMode attackMode, bool rangedAttack, AdvantageType advantageType,
            List<EffectForm> actualEffectForms, bool firstTarget, bool criticalHit)
        {
            if (rangedAttack || attackMode == null || actualEffectForms == null || battleManager == null ||
                attacker?.RulesetCharacter == null || EnemyBattleOnHitBonusPending.TryGetValue(attacker.RulesetCharacter, out _))
            {
                yield break;
            }
            var context = new EnemyBattleOnHitBonusContext(attacker, defender, attackMode, actualEffectForms, battleManager, kind);
            if (!IsValidEnemyBattleOnHitBonus(context) ||
                !EnemyBattleOnHitBonusHits.GetOrCreateValue(actualEffectForms).Add(attacker.Guid))
            {
                yield break;
            }
            using (new EnemyBattlePendingScope<RulesetCharacter, EnemyBattleOnHitBonusContext>(
                       EnemyBattleOnHitBonusPending, attacker.RulesetCharacter, context))
            {
                yield return attacker.MyReactToDoNothing(ExtraActionId.DoNothingFree, attacker,
                    $"EnemyBattle{kind}", FormatEnemyBattleDescription(kind, attacker.RulesetCharacter),
                    ReactionValidated, battleManager: battleManager,
                    resource: new ReactionResourcePowerPool(power),
                    target: defender, effectDefinition: power);
            }
            yield break;

            void ReactionValidated()
            {
                if (!IsValidEnemyBattleOnHitBonus(context) ||
                    !CanUseEnemyBattlePower(attacker, kind, out var usable))
                {
                    return;
                }
                context.Consumed = true;
                attacker.SpendActionType(ActionType.Bonus);
                attacker.RulesetCharacter.UsePower(usable);
                actualEffectForms.Add(EffectFormBuilder.DamageForm(kind == EnemyBattleAffix.CrushingBlow
                        ? context.HitDamageForm.DamageForm.DamageType : DamageTypeRadiant,
                    GetEnemyBattleCapabilityDamageDiceNumber(attacker.RulesetCharacter, kind), DieType.D6));
                if (kind == EnemyBattleAffix.CrushingBlow)
                {
                    actualEffectForms.Add(BuildEnemyBattleProneForm(attacker.RulesetCharacter, power));
                }
                attacker.RulesetCharacter.LogCharacterUsedFeature(power);
                EffectHelpers.StartVisualEffect(attacker, defender,
                    kind == EnemyBattleAffix.CrushingBlow ? FeatureDefinitionPowers.PowerFighterActionSurge : SacredFlame,
                    EffectHelpers.EffectType.Effect);
            }
        }
    }

    private static bool TryGetEnemyBattleAttackOutcome(
        CharacterAction action, GameLocationCharacter attacker, GameLocationCharacter defender,
        ActionModifier modifier, RulesetAttackMode attackMode, RulesetEffect effect,
        out RollOutcome outcome, out int successDelta, out bool automaticHit)
    {
        outcome = action?.AttackRollOutcome ?? RollOutcome.Failure;
        successDelta = 0;
        automaticHit = false;
        var attackingCharacter = attacker?.RulesetCharacter;
        var defendingCharacter = defender?.RulesetCharacter;
        if (action == null || attackingCharacter == null || defendingCharacter == null)
        {
            return false;
        }

        // An explicit hit changes the result without replacing its original die.
        // AC defenses cannot evade it, while damage defenses still apply to that hit.
        automaticHit = attackMode?.AutomaticHit == true ||
                       TryAlterOutcomeAttack.IsForcedHit(action, defender, modifier);

        if (!automaticHit)
        {
            if (action.AttackRoll <= 0 || attackMode == null && effect == null ||
                !defendingCharacter.TryGetAttribute(AttributeDefinitions.ArmorClass, out var armorClass) ||
                armorClass == null)
            {
                return false;
            }

            // Earlier reactions may have changed AC while the action still caches its initial outcome.
            var evaluation = attackingCharacter.ComputeAttackOutcome(defendingCharacter,
                attackMode?.SourceDefinition ?? effect?.GetEffectSource(), action.AttackRoll,
                attackMode?.ToHitBonus ?? effect.MagicAttackBonus, modifier?.AttackRollModifier ?? 0);
            outcome = evaluation.Outcome;
            successDelta = evaluation.SuccessDelta;
            automaticHit = evaluation.IsAutomaticHit;
        }

        return outcome is RollOutcome.Success or RollOutcome.CriticalSuccess;
    }

    internal static void RecordEnemyBattleMagicAttackOutcome(
        CharacterActionMagicEffect action, GameLocationCharacter target, RollOutcome outcome)
    {
        if (action?.ActionParams?.RulesetEffect?.EffectDescription.NeedsToRollDie() != true || target == null)
        {
            return;
        }

        var outcomes = EnemyBattleMagicAttackOutcomes.GetOrCreateValue(action);
        if (!outcomes.TryGetValue(target.Guid, out var targetOutcomes))
        {
            targetOutcomes = [];
            outcomes.Add(target.Guid, targetOutcomes);
        }
        targetOutcomes.Add(new EnemyBattleAttackResult(outcome));
    }

    internal static void RecordEnemyBattleAttackDamage(CharacterAction action, RulesetCharacter target, int effectiveDamage)
    {
        if (action == null || target == null)
        {
            return;
        }
        effectiveDamage = System.Math.Max(0, effectiveDamage);
        var damage = EnemyBattleAttackDamage.GetOrCreateValue(action);
        damage.TryGetValue(target.Guid, out var previous);
        damage[target.Guid] = previous + effectiveDamage;
        EnemyBattleLatestAttackDamage.GetOrCreateValue(action)[target.Guid] = effectiveDamage;
        if (action is CharacterActionMagicEffect magicAction &&
            EnemyBattleMagicAttackOutcomes.TryGetValue(magicAction, out var outcomes) &&
            outcomes.TryGetValue(target.Guid, out var targetOutcomes) && targetOutcomes.Count > 0)
        {
            targetOutcomes[targetOutcomes.Count - 1].DamageAmount += effectiveDamage;
        }
    }

    internal static int GetEnemyBattleAttackDamage(CharacterAction action, RulesetCharacter target) =>
        action != null && target != null && EnemyBattleAttackDamage.TryGetValue(action, out var damage) &&
        damage.TryGetValue(target.Guid, out var amount) ? amount : 0;

    private static int GetEnemyBattleLatestAttackDamage(CharacterAction action, RulesetCharacter target) =>
        action != null && target != null && EnemyBattleLatestAttackDamage.TryGetValue(action, out var damage) &&
        damage.TryGetValue(target.Guid, out var amount) ? amount : 0;

    private sealed class EnemyBattleGraveLeech(FeatureDefinitionPower power) : IPowerOrSpellFinishedByMe
    {
        private static readonly ConditionalWeakTable<CharacterAction, HashSet<ulong>> HealedSources = new();

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            if (definition == power && !action.Countered && !action.ExecutionFailed &&
                action.ActingCharacter?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } source &&
                HealedSources.GetOrCreateValue(action).Add(source.Guid))
            {
                var damage = action.ActionParams.TargetCharacters.Distinct()
                    .Sum(target => GetEnemyBattleAttackDamage(action, target.RulesetCharacter));
                var healing = System.Math.Min(source.MissingHitPoints, damage / 2);
                if (healing > 0)
                {
                    source.ReceiveHealing(healing, true, source.Guid);
                }
            }
            yield break;
        }
    }

    private sealed class EnemyBattleReactionPower(
        EnemyBattleAffix kind,
        FeatureDefinitionPower power,
        ConditionDefinition effectCondition = null) : ITryAlterOutcomeAttack, IPhysicalAttackFinishedOnMe,
        IMagicEffectFinishedOnMe,
        IValidatePowerUse, IModifyEffectDescription, IFilterRulesetEffectTargets, IValidateMagicEffectBeforeSpend
    {
        private static readonly ConditionalWeakTable<CharacterAction, HashSet<ulong>> ElementalReactionTargets = new();
        private static readonly ConditionalWeakTable<CharacterAction, HashSet<ulong>> ReprisalReactionTargets = new();

        internal EnemyBattleAffix Kind { get; } = kind;
        internal ConditionDefinition EffectCondition { get; } = effectCondition;
        public int HandlerPriority => Kind == EnemyBattleAffix.Bodyguard ? 20 : -10;

        public bool CanAffectTarget(RulesetEffect rulesetEffect, GameLocationCharacter caster, GameLocationCharacter target)
        {
            if (Kind == EnemyBattleAffix.GraveLeech && rulesetEffect.GetSourceDefinitionSafe() == power)
            {
                return IsUsefulEnemyBattleGraveLeech(caster, target, forAi: false);
            }
            if (!IsEnemyBattleAreaDamage(Kind) || rulesetEffect.GetSourceDefinitionSafe() != power)
            {
                return true;
            }
            if (caster?.RulesetCharacter == null)
            {
                return false;
            }
            var snapshot = GetEnemyBattleAreaEffectSnapshot(caster.RulesetCharacter, Kind, rulesetEffect);
            return snapshot.Targets.Contains(target) && GetEnemyBattleAreaTargets(caster, Kind).Contains(target);
        }

        public bool IsValid(CharacterActionMagicEffect action, GameLocationCharacter actingCharacter,
            IReadOnlyList<GameLocationCharacter> targets, out string failure)
        {
            failure = string.Empty;
            if (Kind == EnemyBattleAffix.LastingSurge && action.ActionParams.RulesetEffect.GetSourceDefinitionSafe() == power)
            {
                return targets.Count == 1 && targets[0] == actingCharacter &&
                       !actingCharacter.IsMyTurn() && !HasEnemyBattleAcceleration(actingCharacter.RulesetCharacter) &&
                       CanUseEnemyBattlePower(actingCharacter, Kind, out _);
            }
            if (Kind == EnemyBattleAffix.GraveLeech && action.ActionParams.RulesetEffect.GetSourceDefinitionSafe() == power)
            {
                return targets.Count == 1 && IsUsefulEnemyBattleGraveLeech(actingCharacter, targets[0], forAi: false) &&
                       CanUseEnemyBattlePower(actingCharacter, Kind, out _);
            }
            if (!IsEnemyBattleAreaDamage(Kind) || action.ActionParams.RulesetEffect.GetSourceDefinitionSafe() != power)
            {
                return true;
            }
            var snapshot = GetEnemyBattleAreaEffectSnapshot(actingCharacter.RulesetCharacter, Kind,
                action.ActionParams.RulesetEffect);
            return targets.Count > 0 && snapshot.DiceNumber > 0 &&
                   IsUsefulEnemyBattleAreaDamage(actingCharacter, Kind) &&
                   CanUseEnemyBattlePower(actingCharacter, Kind, out _);
        }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower definition)
        {
            return definition == power &&
                   CanUseEnemyBattlePower(GameLocationCharacter.GetFromActor(character), Kind, out _) &&
                   (!IsEnemyBattleAreaDamage(Kind) || IsUsefulEnemyBattleAreaDamage(GameLocationCharacter.GetFromActor(character), Kind)) &&
                    (Kind != EnemyBattleAffix.LastingSurge ||
                        !GameLocationCharacter.GetFromActor(character).IsMyTurn() && !HasEnemyBattleAcceleration(character)) &&
                   (EffectCondition == null || !character.HasConditionOfType(EffectCondition.Name));
        }

        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription description)
        {
            return Kind == EnemyBattleAffix.ElementalRebuke && definition == power &&
                   character is RulesetCharacterMonster;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition, EffectDescription description, RulesetCharacter character, RulesetEffect effect)
        {
            var element = GetEnemyBattleNativeElement(character) ?? DamageTypeFire;
            var particles = element switch
            {
                DamageTypeAcid => AcidSplash,
                DamageTypeCold => RayOfFrost,
                DamageTypeLightning => LightningBolt,
                DamageTypeThunder => Thunderwave,
                DamageTypePoison => PoisonSpray,
                DamageTypeNecrotic => ChillTouch,
                DamageTypeRadiant => SacredFlame,
                DamageTypePsychic => ViciousMockery,
                _ => HellishRebuke
            };
            return EffectDescriptionBuilder.Create(description)
                .SetEffectForms(EffectFormBuilder.Create().SetDamageForm(element, GetEnemyBattleDamageDiceNumber(character), DieType.D6)
                    .HasSavingThrow(EffectSavingThrowType.HalfDamage).Build())
                .SetCasterEffectParameters(particles)
                .SetImpactEffectParameters(particles).Build();
        }

        public IEnumerator OnTryAlterOutcomeAttack(
            GameLocationBattleManager battleManager, CharacterAction action, GameLocationCharacter attacker,
            GameLocationCharacter defender, GameLocationCharacter helper, ActionModifier modifier,
            RulesetAttackMode attackMode, RulesetEffect effect)
        {
            if (Kind is not (EnemyBattleAffix.ArcaneWard or EnemyBattleAffix.Parry or EnemyBattleAffix.Bodyguard) ||
                attacker == null || defender == null || helper == null ||
                !attacker.IsOppositeSide(defender.Side) || !helper.CanReact() ||
                !CanUseEnemyBattlePower(helper, Kind, out var usablePower) ||
                action.AttackRollOutcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
                !TryGetEnemyBattleAttackOutcome(action, attacker, defender, modifier, attackMode, effect,
                    out var currentOutcome, out var successDelta, out var automaticHit))
            {
                yield break;
            }

            if (Kind == EnemyBattleAffix.Bodyguard)
            {
                var damage = attackMode?.EffectDescription ?? effect?.EffectDescription;
                if (helper == defender || helper.IsOppositeSide(defender.Side) ||
                    !helper.IsWithinRange(defender, 1) || !helper.CanSeeTarget(attacker) ||
                    defender.RulesetCharacter.HasConditionOfType(EffectCondition.Name) ||
                    HasEnemyBattleAttackDamageReduction(defender.RulesetCharacter) ||
                    damage?.HasDamageForm() != true)
                {
                    yield break;
                }
            }
            else
            {
                if (helper != defender || currentOutcome != RollOutcome.Success || automaticHit ||
                    defender.RulesetCharacter.HasConditionOfType(EffectCondition.Name))
                {
                    yield break;
                }

                var bonus = Kind == EnemyBattleAffix.Parry ? helper.RulesetCharacter.TryGetProficiencyBonus() : 5;
                if (successDelta >= bonus ||
                    Kind == EnemyBattleAffix.ArcaneWard && !helper.RulesetCharacter.AreSpellComponentsValid(Shield) ||
                    Kind == EnemyBattleAffix.Parry &&
                    (attackMode == null || attackMode.Ranged || effect != null ||
                     !GetEnemyBattleOrdinaryAttackModes(helper.RulesetCharacter).Any(mode => !mode.Ranged &&
                         IsEnemyBattleWeaponAttack(mode))))
                {
                    yield break;
                }
            }

            yield return helper.MyReactToUsePower(
                Id.PowerReaction, usablePower, [defender], attacker, "UsePower", battleManager: battleManager);
        }

        public IEnumerator OnPhysicalAttackFinishedOnMe(
            GameLocationBattleManager battleManager, CharacterAction action, GameLocationCharacter attacker,
            GameLocationCharacter defender, RulesetAttackMode attackMode, RollOutcome outcome, int damageAmount)
        {
            if (Kind is EnemyBattleAffix.ElementalRebuke or EnemyBattleAffix.PsychicBacklash or EnemyBattleAffix.CausticRetort or
                    EnemyBattleAffix.GraveLeech or EnemyBattleAffix.LastingSurge ||
                IsEnemyBattleAreaDamage(Kind))
            {
                if (attackMode == null || Kind == EnemyBattleAffix.CausticRetort && attackMode.Ranged ||
                    outcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
                    damageAmount <= 0 && GetEnemyBattleLatestAttackDamage(action, defender?.RulesetCharacter) <= 0)
                {
                    yield break;
                }

                yield return ReactToEnemyElementalAttack(battleManager, action, attacker, defender, outcome);
                yield break;
            }

            if (Kind != EnemyBattleAffix.Reprisal || attackMode == null || attackMode.Ranged ||
                outcome is not (RollOutcome.Failure or RollOutcome.CriticalFailure))
            {
                yield break;
            }

            yield return ReactToEnemyMissedMeleeAttack(battleManager, action, attacker, defender);
        }

        private IEnumerator ReactToEnemyMissedMeleeAttack(
            GameLocationBattleManager battleManager, CharacterAction action, GameLocationCharacter attacker,
            GameLocationCharacter defender)
        {
            if (action?.ActionParams == null || action.Countered ||
                attacker == null || defender == null ||
                !attacker.IsOppositeSide(defender.Side) || defender.IsMyTurn() ||
                attacker.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } ||
                !defender.CanReact() || !defender.CanSeeTarget(attacker) ||
                !CanUseEnemyBattlePower(defender, Kind, out var usablePower))
            {
                yield break;
            }

            // Monster natural attacks have a MonsterAttackDefinition source, not an equipped RulesetItem.
            RulesetAttackMode mode = null;
            ActionModifier modifier = null;
            foreach (var candidate in defender.RulesetCharacter.AttackModes.Where(candidate => !candidate.Ranged))
            {
                var candidateModifier = new ActionModifier();
                var evaluation = new BattleDefinitions.AttackEvaluationParams();
                evaluation.FillForPhysicalReachAttack(defender, defender.LocationPosition, candidate,
                    attacker, attacker.LocationPosition, candidateModifier);
                if (!battleManager.CanAttack(evaluation))
                {
                    continue;
                }
                mode = candidate;
                modifier = candidateModifier;
                break;
            }

            if (mode == null || !ReprisalReactionTargets.GetOrCreateValue(action).Add(defender.Guid))
            {
                yield break;
            }

            var reactionMode = mode.Clone();
            reactionMode.actionType = ActionType.Reaction;
            reactionMode.AddAttackTagAsNeeded(AttacksOfOpportunity.NotAoOTag);
            reactionMode.AddAttackTagAsNeeded(EnemyBattleReprisalTag);
            try
            {
                yield return defender.MyReactForOpportunityAttack(
                    attacker, attacker, reactionMode, modifier, "EnemyBattleReprisal",
                    reactionValidated: () => defender.RulesetCharacter.UsePower(usablePower),
                    battleManager: battleManager,
                    resource: new ReactionResourcePowerPool(power));
            }
            finally
            {
                reactionMode.Return();
            }
        }

        public IEnumerator OnMagicEffectFinishedOnMe(
            CharacterAction action, GameLocationCharacter attacker, GameLocationCharacter defender,
            List<GameLocationCharacter> targets)
        {
            if (Kind is not (EnemyBattleAffix.Reprisal or EnemyBattleAffix.CausticRetort or
                    EnemyBattleAffix.ElementalRebuke or EnemyBattleAffix.PsychicBacklash or EnemyBattleAffix.GraveLeech or
                    EnemyBattleAffix.LastingSurge) && !IsEnemyBattleAreaDamage(Kind) ||
                action is not CharacterActionMagicEffect { Countered: false, ExecutionFailed: false } magicAction ||
                action.ActionParams?.RulesetEffect?.EffectDescription.NeedsToRollDie() != true ||
                ServiceRepository.GetService<IGameLocationBattleService>() is not GameLocationBattleManager battleManager)
            {
                yield break;
            }

            if (Kind is (EnemyBattleAffix.Reprisal or EnemyBattleAffix.CausticRetort) &&
                action.ActionParams.RulesetEffect.EffectDescription.RangeType != RangeType.MeleeHit)
            {
                yield break;
            }
            if (defender == null || !EnemyBattleMagicAttackOutcomes.TryGetValue(magicAction, out var outcomes) ||
                !outcomes.TryGetValue(defender.Guid, out var targetOutcomes))
            {
                yield break;
            }

            // Every ray retains its target's final outcome; the action's last roll may belong to another target.
            var occurrence = 0;
            foreach (var target in action.ActionParams.TargetCharacters)
            {
                if (target != defender)
                {
                    continue;
                }
                var targetOccurrence = occurrence++;
                if (targetOccurrence >= targetOutcomes.Count)
                {
                    continue;
                }
                var result = targetOutcomes[targetOccurrence];
                var outcome = result.Outcome;
                if (Kind == EnemyBattleAffix.Reprisal && outcome is (RollOutcome.Failure or RollOutcome.CriticalFailure))
                {
                    yield return ReactToEnemyMissedMeleeAttack(battleManager, action, attacker, defender);
                    yield break;
                }
                if (Kind != EnemyBattleAffix.Reprisal && result.DamageAmount > 0 && outcome is (RollOutcome.Success or RollOutcome.CriticalSuccess))
                {
                    yield return ReactToEnemyElementalAttack(battleManager, action, attacker, defender, outcome);
                    yield break;
                }
            }
        }

        private IEnumerator ReactToEnemyElementalAttack(
            GameLocationBattleManager battleManager, CharacterAction action, GameLocationCharacter attacker,
            GameLocationCharacter defender, RollOutcome outcome)
        {
            if (action?.ActionParams == null ||
                action.Countered || outcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
                defender?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } || defender.IsMyTurn() ||
                attacker == null || !defender.IsOppositeSide(attacker.Side) ||
                (IsEnemyBattleAreaDamage(Kind) ? !IsUsefulEnemyBattleAreaDamage(defender, Kind) :
                    Kind == EnemyBattleAffix.LastingSurge ? HasEnemyBattleAcceleration(defender.RulesetCharacter) :
                    Kind == EnemyBattleAffix.ElementalRebuke ? !IsUsefulEnemyElementalRebuke(defender, attacker) :
                    Kind == EnemyBattleAffix.GraveLeech ? !IsUsefulEnemyBattleGraveLeech(defender, attacker, forAi: false) :
                    !IsValidEnemyBattleUtilityTarget(defender, Kind, attacker, forAi: true)) || !defender.CanReact() ||
                !CanUseEnemyBattlePower(defender, Kind, out var rebuke) ||
                !ElementalReactionTargets.GetOrCreateValue(action).Add(defender.Guid))
            {
                yield break;
            }

            if (!IsEnemyBattleAreaDamage(Kind))
            {
                yield return defender.MyReactToUsePower(
                    Id.PowerReaction, rebuke, [Kind == EnemyBattleAffix.LastingSurge ? defender : attacker], attacker,
                    "UsePower", battleManager: battleManager);
                yield break;
            }
            var snapshot = GetEnemyBattleAreaSnapshot(defender.RulesetCharacter, Kind);
            using (new EnemyBattlePendingScope<RulesetCharacter, EnemyBattleAreaSnapshot>(
                       EnemyBattleAreaPending, defender.RulesetCharacter, snapshot))
            {
                yield return defender.MyReactToUsePower(Id.PowerReaction, rebuke, snapshot.Targets.ToList(),
                    attacker, "UsePower", battleManager: battleManager);
            }
        }
    }

    private static bool HasEnemyBattleAttackDamageReduction(RulesetCharacter defender)
    {
        // Another guard may already have reduced this attack while this handler waited for reactions.
        return defender.ConditionsByCategory.Values.SelectMany(conditions => conditions)
            .Any(condition => condition.ConditionDefinition.Features.OfType<FeatureDefinitionReduceDamage>().Any());
    }

}
