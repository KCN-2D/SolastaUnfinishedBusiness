using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.Infrastructure;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Spells;
using SolastaUnfinishedBusiness.Subclasses;
using TA;
using UnityEngine;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal sealed partial class SimulacrumBehavior
{
    internal static void RestoreInitializingSnapshot(RulesetCharacterMonster character)
    {
        if (character is RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Initializing
            } duplicate &&
            InitializingSnapshotSeeds.TryGetValue(duplicate, out var seed))
        {
            seed.ApplyInitialAttributes(duplicate);
        }
    }

    internal static void RestoreHitPointsAfterCompute(
        RulesetCharacter character,
        int currentHitPoints)
    {
        if (character is not RulesetCharacterSimulacrum duplicate)
        {
            return;
        }

        if (duplicate.LifecycleState == SimulacrumLifecycleState.Initializing)
        {
            if (InitializingSnapshotSeeds.TryGetValue(duplicate, out var seed))
            {
                seed.ApplyInitialAttributes(duplicate);

                return;
            }

            // Monster PostLoad refreshes the 1 HP construction shell before the loaded
            // snapshot is reapplied. Preserve the CurrentHitPoints read by RulesetActor.
            if (TryGetSnapshot(duplicate, out var loadedSnapshot) &&
                loadedSnapshot.IsCurrentSchema &&
                loadedSnapshot.HalfMaximumHitPoints > 0)
            {
                loadedSnapshot.RestoreHitPointsAfterCompute(
                    duplicate,
                    currentHitPoints);
            }

            return;
        }

        if (duplicate.LifecycleState != SimulacrumLifecycleState.Ready ||
            !TryGetSnapshot(duplicate, out var snapshot) ||
            !snapshot.IsCurrentSchema)
        {
            return;
        }

        snapshot.RestoreHitPointsAfterCompute(duplicate, currentHitPoints);
    }

    private readonly struct PersistentAttributeValue(
        string name,
        SmartAttributeDefinition definition,
        int value,
        int minValue,
        int maxValue)
    {
        internal string Name { get; } = name;
        internal SmartAttributeDefinition Definition { get; } = definition;
        internal int Value { get; } = value;
        internal int MinValue { get; } = minValue;
        internal int MaxValue { get; } = maxValue;
    }

    internal sealed class SimulacrumSnapshotSeed(
        ulong copiedCharacterGuid,
        SimulacrumAppearanceSeed appearance,
        IReadOnlyDictionary<string, PersistentAttributeSeed> attributes,
        IReadOnlyList<SourceFeatureState> features,
        IReadOnlyList<SpellRepertoireSeed> repertoires,
        IReadOnlyList<InvocationSeed> invocations,
        IReadOnlyList<AttackSeed> attacks,
        IReadOnlyList<SimulacrumSnapshotRulesetCondition.SourcePowerState> powers,
        IReadOnlyList<string> toggles,
        PersistentResourceSeed resources,
        IReadOnlyList<ClassLevelSeed> classes,
        IReadOnlyDictionary<CharacterClassDefinition, CharacterSubclassDefinition> subclasses,
        IReadOnlyList<string> knownLanguages,
        EquipmentEligibilitySeed equipmentEligibility,
        IReadOnlyList<MovementModeSeed> movementModes,
        IReadOnlyList<SenseModeSeed> senseModes,
        IReadOnlyList<SkillBonusSeed> skillBonuses,
        IReadOnlyList<SavingThrowSeed> savingThrows,
        IReadOnlyList<string> trainedFeatNames,
        IReadOnlyList<string> trainedFightingStyleNames,
        IReadOnlyList<string> trainedMetamagicOptionNames)
    {
        internal ulong CopiedCharacterGuid { get; } = copiedCharacterGuid;
        internal SimulacrumAppearanceSeed Appearance { get; } = appearance;
        internal IReadOnlyDictionary<string, PersistentAttributeSeed> Attributes { get; } = attributes;
        internal IReadOnlyList<SourceFeatureState> Features { get; } = features;
        internal IReadOnlyList<SpellRepertoireSeed> Repertoires { get; } = repertoires;
        internal IReadOnlyList<InvocationSeed> Invocations { get; } = invocations;
        internal IReadOnlyList<AttackSeed> Attacks { get; } = attacks;
        internal IReadOnlyList<SimulacrumSnapshotRulesetCondition.SourcePowerState> Powers { get; } = powers;
        internal IReadOnlyList<string> Toggles { get; } = toggles;
        internal PersistentResourceSeed Resources { get; } = resources;
        internal IReadOnlyList<ClassLevelSeed> Classes { get; } = classes;
        internal IReadOnlyDictionary<CharacterClassDefinition, CharacterSubclassDefinition> Subclasses { get; } =
            subclasses;
        internal IReadOnlyList<string> KnownLanguages { get; } = knownLanguages;
        internal EquipmentEligibilitySeed EquipmentEligibility { get; } = equipmentEligibility;
        internal IReadOnlyList<MovementModeSeed> MovementModes { get; } = movementModes;
        internal IReadOnlyList<SenseModeSeed> SenseModes { get; } = senseModes;
        internal IReadOnlyList<SkillBonusSeed> SkillBonuses { get; } = skillBonuses;
        internal IReadOnlyList<SavingThrowSeed> SavingThrows { get; } = savingThrows;
        internal IReadOnlyList<string> TrainedFeatNames { get; } = trainedFeatNames;
        internal IReadOnlyList<string> TrainedFightingStyleNames { get; } = trainedFightingStyleNames;
        internal IReadOnlyList<string> TrainedMetamagicOptionNames { get; } = trainedMetamagicOptionNames;
        internal int HalfMaximumHitPoints => Math.Max(
            1,
            GetAttributeValue(AttributeDefinitions.HitPoints, 1) / 2);

        internal int GetAttributeValue(string attributeName, int fallback = 0)
        {
            return Attributes.TryGetValue(attributeName, out var attribute)
                ? attribute.FinalValue
                : fallback;
        }

        internal void ApplyConstructionAttributes(RulesetCharacterSimulacrum duplicate)
        {
            ApplyPersistentAttributeValues(duplicate, CreatePersistentAttributeValues());
        }

        internal void ApplyInitialAttributes(RulesetCharacterSimulacrum duplicate)
        {
            ApplyPersistentAttributeValues(
                duplicate,
                CreatePersistentAttributeValues());

            duplicate.MoveModes.Clear();

            foreach (var movementMode in MovementModes)
            {
                duplicate.MoveModes[movementMode.Type] = movementMode.Speed;
            }

            duplicate.SenseModes.Clear();

            foreach (var senseMode in SenseModes)
            {
                duplicate.SenseModes.Add(new SenseMode(
                    (SenseMode.Type)senseMode.Type,
                    senseMode.Range,
                    senseMode.StealthBreakerRange));
            }

            duplicate.ForceSetHealth(HalfMaximumHitPoints, false);
            Resources.Apply(duplicate);
        }

        private PersistentAttributeValue[] CreatePersistentAttributeValues()
        {
            return Attributes.Values
                .Select(attribute => new PersistentAttributeValue(
                    attribute.Name,
                    attribute.Definition,
                    attribute.Name == AttributeDefinitions.HitPoints
                        ? HalfMaximumHitPoints
                        : attribute.FinalValue,
                    attribute.MinValue,
                    attribute.MaxValue))
                .ToArray();
        }
    }

    internal sealed class PersistentAttributeSeed(
        string name,
        SmartAttributeDefinition definition,
        int baseValue,
        int minValue,
        int maxValue,
        int finalValue)
    {
        internal string Name { get; } = name;
        internal SmartAttributeDefinition Definition { get; } = definition;
        internal int BaseValue { get; } = baseValue;
        internal int MinValue { get; } = minValue;
        internal int MaxValue { get; } = maxValue;
        internal int FinalValue { get; } = finalValue;
    }

    internal sealed class PersistentResourceSeed(RulesetCharacter source)
    {
        internal int UsedChannelDivinity { get; } = source.UsedChannelDivinity;
        internal int UsedHealingPool { get; } = source.UsedHealingPool;
        internal int UsedIndomitableResistances { get; } = source.UsedIndomitableResistances;
        internal int UsedSorceryPoints { get; } = source.UsedSorceryPoints;
        internal int UsedRagePoints { get; } = source.UsedRagePoints;
        internal int UsedKiPoints { get; } = source.UsedKiPoints;
        internal int UsedBindChain { get; } = source.UsedBindChain;
        internal int UsedBardicInspiration { get; } = source.UsedBardicInspiration;
        internal int UsedKnockOutImmunityPerLongRest { get; } = source.UsedKnockOutImmunityPerLongRest;

        internal void Apply(RulesetCharacter target)
        {
            target.UsedChannelDivinity = UsedChannelDivinity;
            target.UsedHealingPool = UsedHealingPool;
            target.UsedIndomitableResistances = UsedIndomitableResistances;
            target.UsedSorceryPoints = UsedSorceryPoints;
            target.UsedRagePoints = UsedRagePoints;
            target.UsedKiPoints = UsedKiPoints;
            target.UsedBindChain = UsedBindChain;
            target.UsedBardicInspiration = UsedBardicInspiration;
            target.UsedKnockOutImmunityPerLongRest = UsedKnockOutImmunityPerLongRest;
        }
    }

    internal sealed class ClassLevelSeed(
        CharacterClassDefinition classDefinition,
        int level)
    {
        internal CharacterClassDefinition ClassDefinition { get; } = classDefinition;
        internal int Level { get; } = level;
    }

    internal sealed class EquipmentEligibilitySeed(
        bool canEquipHumanoidItems,
        IReadOnlyList<string> armorCategories,
        IReadOnlyList<string> armorTypes,
        IReadOnlyList<string> weaponCategories,
        IReadOnlyList<string> weaponTypes)
    {
        internal bool CanEquipHumanoidItems { get; } = canEquipHumanoidItems;
        internal IReadOnlyList<string> ArmorCategories { get; } = armorCategories;
        internal IReadOnlyList<string> ArmorTypes { get; } = armorTypes;
        internal IReadOnlyList<string> WeaponCategories { get; } = weaponCategories;
        internal IReadOnlyList<string> WeaponTypes { get; } = weaponTypes;
    }

    internal sealed class MovementModeSeed(int type, int speed)
    {
        internal int Type { get; } = type;
        internal int Speed { get; } = speed;
    }

    internal sealed class SenseModeSeed(int type, int range, int stealthBreakerRange)
    {
        internal int Type { get; } = type;
        internal int Range { get; } = range;
        internal int StealthBreakerRange { get; } = stealthBreakerRange;
    }

    internal sealed class SkillBonusSeed(string name, int bonus, int proficiencyRank)
    {
        internal string Name { get; } = name;
        internal int Bonus { get; } = bonus;
        internal int ProficiencyRank { get; } = proficiencyRank;
    }

    internal sealed class SavingThrowSeed(string abilityScore, int proficiencyDelta)
    {
        internal string AbilityScore { get; } = abilityScore;
        internal int ProficiencyDelta { get; } = proficiencyDelta;
    }

    internal sealed class SpellRepertoireSeed
    {
        internal FeatureDefinitionCastSpell SpellCastingFeature { get; set; }
        internal CharacterClassDefinition SpellCastingClass { get; set; }
        internal CharacterSubclassDefinition SpellCastingSubclass { get; set; }
        internal CharacterRaceDefinition SpellCastingRace { get; set; }
        internal string SpellCastingAbility { get; set; }
        internal string AutoPreparedTag { get; set; }
        internal int MaxPreparedSpells { get; set; }
        internal int SpellCastingLevel { get; set; }
        internal int FormAbilityBonus { get; set; }
        internal int SaveDc { get; set; }
        internal int SpellAttackBonus { get; set; }
        internal MonsterDefinition SpellCastingMonster { get; set; }
        internal IReadOnlyList<SpellDefinition> KnownCantrips { get; set; }
        internal IReadOnlyList<SpellDefinition> KnownSpells { get; set; }
        internal IReadOnlyList<SpellDefinition> PreparedSpells { get; set; }
        internal IReadOnlyList<SpellDefinition> AutoPreparedSpells { get; set; }
        internal IReadOnlyList<TrendInfo> MagicAttackTrends { get; set; }
        internal IReadOnlyDictionary<int, int> SlotCapacities { get; set; }
        internal IReadOnlyDictionary<int, int> UsedSpellSlots { get; set; }
        internal IReadOnlyDictionary<int, int> UsedMysticArcanum { get; set; }
        internal IReadOnlyDictionary<int, int> LegacyAvailableSpellSlots { get; set; }
        internal IReadOnlyDictionary<string, IReadOnlyList<SpellDefinition>> ExtraSpellsByTag { get; set; }
    }

    internal sealed class InvocationSeed(
        InvocationDefinition definition,
        int repertoireIndex,
        bool active,
        bool used)
    {
        internal InvocationDefinition Definition { get; } = definition;
        internal int RepertoireIndex { get; } = repertoireIndex;
        internal bool Active { get; } = active;
        internal bool Used { get; } = used;
    }

    internal sealed class AttackSeed
    {
        internal string DefinitionKind { get; set; }
        internal string DefinitionName { get; set; }
        internal IReadOnlyList<string> Tags { get; set; }
        internal IReadOnlyList<AttackDamageSeed> DamageForms { get; set; }
        internal int ActionType { get; set; }
        internal string AbilityScore { get; set; }
        internal int AttacksNumber { get; set; }
        internal int ToHitBonus { get; set; }
        internal int CloseRange { get; set; }
        internal int MaxRange { get; set; }
        internal int ReachRange { get; set; }
        internal string SlotName { get; set; }
        internal bool Ranged { get; set; }
        internal bool Reach { get; set; }
        internal bool Thrown { get; set; }
        internal bool HasPriority { get; set; }
        internal bool UseVersatileDamage { get; set; }
    }

    internal sealed class AttackDamageSeed
    {
        internal string DamageType { get; set; }
        internal int DieType { get; set; }
        internal bool Versatile { get; set; }
        internal int VersatileDieType { get; set; }
        internal int DiceNumber { get; set; }
        internal int BonusDamage { get; set; }
    }

    internal sealed class SourceFeatureState(
        FeatureDefinition feature,
        FeatureOrigin origin)
    {
        internal FeatureDefinition Feature { get; } = feature;
        internal FeatureOrigin Origin { get; } = origin;
    }

    internal static bool TryGetSnapshot(
        RulesetActor character,
        out SimulacrumSnapshotRulesetCondition snapshot)
    {
        return SimulacrumSnapshotRulesetCondition.GetCustomConditionFromCharacter(character, out snapshot);
    }

    internal static bool TryReplaceInvocation(
        RulesetCharacterSimulacrum character,
        InvocationDefinition invocationToRemove,
        InvocationDefinition invocationToAdd)
    {
        if (character?.LifecycleState != SimulacrumLifecycleState.Ready ||
            invocationToRemove == null ||
            invocationToAdd == null ||
            invocationToRemove == invocationToAdd ||
            !TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema ||
            !snapshot.TryReplaceInvocation(
                character,
                invocationToRemove,
                invocationToAdd))
        {
            return false;
        }

        RefreshEquipment(character);

        return character.LifecycleState == SimulacrumLifecycleState.Ready;
    }

    internal static void RebindFeatureOrigins(
        RulesetCharacterSimulacrum character,
        Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
    {
        if (character == null ||
            featuresOrigin == null ||
            !TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema)
        {
            return;
        }

        snapshot.RebindFeatureOrigins(featuresOrigin);
    }

    internal static CharacterClassDefinition FindClassHoldingFeature(
        RulesetCharacterSimulacrum character,
        FeatureDefinition feature)
    {
        if (character == null || feature == null)
        {
            return null;
        }

        var classDefinition = feature
            .GetFirstSubFeatureOfType<ClassHolder>()?
            .Class;

        if (classDefinition && character.GetClassLevel(classDefinition) > 0)
        {
            return classDefinition;
        }

        if (feature is FeatureDefinitionPower)
        {
            classDefinition = character.UsablePowers
                .FirstOrDefault(usablePower =>
                    usablePower?.PowerDefinition == feature &&
                    usablePower.OriginClass != null)?
                .OriginClass;

            if (classDefinition && character.GetClassLevel(classDefinition) > 0)
            {
                return classDefinition;
            }
        }

        if (character.FeaturesOrigin == null ||
            !character.FeaturesOrigin.TryGetValue(feature, out var origin))
        {
            return null;
        }

        classDefinition = origin.source switch
        {
            CharacterClassDefinition characterClass => characterClass,
            CharacterSubclassDefinition characterSubclass =>
                LevelUpHelper.GetClassForSubclass(characterSubclass),
            _ => null
        };

        return classDefinition && character.GetClassLevel(classDefinition) > 0
            ? classDefinition
            : null;
    }

    internal static List<FeatureDefinition> GetCurrentlyActiveFeatures(
        RulesetCharacterSimulacrum character)
    {
        if (character == null)
        {
            return [];
        }

        if (!TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema)
        {
            return character.ActiveFeatures.ToList();
        }

        return character.ActiveFeatures
            .Where(feature => snapshot.IsFeatureCurrentlyActive(character, feature))
            .ToList();
    }

    internal static bool IsPowerCurrentlyActive(
        RulesetCharacterSimulacrum character,
        FeatureDefinitionPower power)
    {
        if (character == null || power == null)
        {
            return false;
        }

        return !TryGetSnapshot(character, out var snapshot) ||
               !snapshot.IsCurrentSchema ||
               snapshot.IsFeatureCurrentlyActive(character, power);
    }

    internal static bool TryGetDisplayName(
        RulesetCharacterSimulacrum character,
        out string displayName)
    {
        displayName = string.Empty;

        if (!TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema ||
            string.IsNullOrWhiteSpace(snapshot.DisplayName))
        {
            return false;
        }

        displayName = snapshot.DisplayName;

        return true;
    }

    internal static bool TryGetHumanoidIdentity(
        RulesetCharacterSimulacrum character,
        out CharacterRaceDefinition race,
        out CharacterRaceDefinition subRace)
    {
        race = null;
        subRace = null;

        if (!TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema ||
            snapshot.HumanoidRace == null)
        {
            return false;
        }

        race = snapshot.HumanoidRace;
        subRace = snapshot.HumanoidSubRace;

        return true;
    }

    internal static bool TryGetClassLevels(
        RulesetCharacterSimulacrum character,
        out IReadOnlyList<ClassLevelSeed> classLevels)
    {
        classLevels = null;

        if (!TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema)
        {
            return false;
        }

        classLevels = snapshot.GetClassLevels();

        return classLevels.Count > 0;
    }

    internal static IEnumerable<BaseDefinition> EnumerateBehaviorCarriers(
        RulesetCharacterSimulacrum character)
    {
        return TryGetSnapshot(character, out var snapshot) && snapshot.IsCurrentSchema
            ? snapshot.BehaviorCarriers
            : [];
    }

    internal static IEnumerable<FeatDefinition> EnumerateTrainedFeats(
        RulesetCharacter character)
    {
        if (character?.GetOriginalHero() is { } hero)
        {
            return (hero.TrainedFeats ?? []).Where(feat => feat != null);
        }

        return TryGetIdentitySnapshot(character, out var snapshot)
            ? snapshot.EnumerateTrainedFeats()
            : [];
    }

    internal static bool HasTrainedFeat(
        RulesetCharacter character,
        FeatDefinition feat)
    {
        return feat != null &&
               HasTrainedFeat(character, candidate =>
                   candidate == feat ||
                   string.Equals(candidate.Name, feat.Name, StringComparison.Ordinal));
    }

    internal static bool HasTrainedFeat(
        RulesetCharacter character,
        Func<FeatDefinition, bool> predicate)
    {
        return predicate != null &&
               EnumerateTrainedFeats(character).Any(predicate);
    }

    internal static IEnumerable<FightingStyleDefinition> EnumerateTrainedFightingStyles(
        RulesetCharacter character)
    {
        if (character?.GetOriginalHero() is { } hero)
        {
            return (hero.TrainedFightingStyles ?? []).Where(fightingStyle => fightingStyle != null);
        }

        return TryGetIdentitySnapshot(character, out var snapshot)
            ? snapshot.EnumerateTrainedFightingStyles()
            : [];
    }

    internal static bool HasTrainedFightingStyle(
        RulesetCharacter character,
        FightingStyleDefinition fightingStyle)
    {
        return fightingStyle != null &&
               EnumerateTrainedFightingStyles(character).Any(candidate =>
                   candidate == fightingStyle ||
                   string.Equals(candidate.Name, fightingStyle.Name, StringComparison.Ordinal));
    }

    internal static IEnumerable<MetamagicOptionDefinition> EnumerateTrainedMetamagicOptions(
        RulesetCharacter character)
    {
        if (character?.GetOriginalHero() is { } hero)
        {
            return (hero.TrainedMetamagicOptions ?? []).Where(metamagic => metamagic != null);
        }

        return TryGetIdentitySnapshot(character, out var snapshot)
            ? snapshot.EnumerateTrainedMetamagicOptions()
            : [];
    }

    internal static bool HasTrainedMetamagicOption(
        RulesetCharacter character,
        MetamagicOptionDefinition metamagicOption)
    {
        return metamagicOption != null &&
               EnumerateTrainedMetamagicOptions(character).Any(candidate =>
                   candidate == metamagicOption ||
                   string.Equals(candidate.Name, metamagicOption.Name, StringComparison.Ordinal));
    }

    internal static bool IsFightingStyleActive(
        RulesetCharacterSimulacrum character,
        FightingStyleDefinition fightingStyle)
    {
        if (fightingStyle == null ||
            fightingStyle.contentPack == CeContentPackContext.CeContentPack ||
            fightingStyle.Condition ==
            FightingStyleDefinition.TriggerCondition.RangedWeaponAttack)
        {
            // UB fighting styles own their runtime validators. Archery is also
            // intentionally kept active and validates the individual attack mode.
            return true;
        }

        TryGetActiveEquippedDefinition(
            character,
            EquipmentDefinitions.SlotTypeMainHand,
            out var mainHand);
        TryGetActiveEquippedDefinition(
            character,
            EquipmentDefinitions.SlotTypeOffHand,
            out var offHand);

        return fightingStyle.Condition switch
        {
            FightingStyleDefinition.TriggerCondition.WearingArmor =>
                character.IsWearingArmor(),
            FightingStyleDefinition.TriggerCondition.OneHandedMeleeWeapon =>
                IsMeleeWeapon(mainHand) &&
                !mainHand.WeaponDescription.WeaponTags.Contains(
                    TagsDefinitions.WeaponTagTwoHanded) &&
                !IsWeapon(offHand) &&
                (offHand != null ||
                 !mainHand.WeaponDescription.WeaponTags.Contains(
                     TagsDefinitions.WeaponTagVersatile)),
            FightingStyleDefinition.TriggerCondition.TwoHandedMeleeWeapon =>
                IsMeleeWeapon(mainHand) &&
                offHand == null &&
                (mainHand.WeaponDescription.WeaponTags.Contains(
                     TagsDefinitions.WeaponTagTwoHanded) ||
                 mainHand.WeaponDescription.WeaponTags.Contains(
                     TagsDefinitions.WeaponTagVersatile)),
            FightingStyleDefinition.TriggerCondition.ShieldEquiped =>
                IsShield(mainHand) || IsShield(offHand),
            FightingStyleDefinition.TriggerCondition.TwoMeleeWeaponsWielded =>
                IsMeleeWeapon(mainHand) && IsMeleeWeapon(offHand),
            _ => true
        };
    }

    private static bool TryGetActiveEquippedDefinition(
        RulesetCharacterSimulacrum character,
        string slotName,
        out ItemDefinition definition)
    {
        definition = null;

        if (character?.CharacterInventory == null ||
            !character.CharacterInventory.InventorySlotsByName.TryGetValue(
                slotName,
                out var slot) ||
            slot is { Disabled: true } or { ConfigSlot: true } ||
            slot.EquipedItem?.ItemDefinition is not { } itemDefinition)
        {
            return false;
        }

        var activeSlotName = slot.SlotTypeDefinition?.Name ?? slotName;

        if (!itemDefinition.SlotsWhereActive.Contains(activeSlotName))
        {
            return false;
        }

        definition = itemDefinition;

        return true;
    }

    private static bool IsWeapon(ItemDefinition definition)
    {
        return definition is { IsWeapon: true, WeaponDescription: not null };
    }

    private static bool IsMeleeWeapon(ItemDefinition definition)
    {
        return IsWeapon(definition) &&
               definition.WeaponDescription.WeaponTypeDefinition?.WeaponProximity ==
               AttackProximity.Melee;
    }

    private static bool IsShield(ItemDefinition definition)
    {
        return definition is { IsArmor: true, ArmorDescription: not null } &&
               !definition.ArmorDescription.IsBaseArmorClass;
    }

    private static bool TryGetIdentitySnapshot(
        RulesetCharacter character,
        out SimulacrumSnapshotRulesetCondition snapshot)
    {
        snapshot = null;
        var simulacrum = character as RulesetCharacterSimulacrum ??
                         character?.OriginalFormCharacter as RulesetCharacterSimulacrum;

        return simulacrum != null &&
               TryGetSnapshot(simulacrum, out snapshot) &&
               snapshot.IsCurrentSchema;
    }

    internal static bool TryGetPrimaryClass(
        RulesetCharacterSimulacrum character,
        out CharacterClassDefinition classDefinition)
    {
        classDefinition = null;

        if (!TryGetClassLevels(character, out var classLevels))
        {
            return false;
        }

        classDefinition = classLevels
            .OrderByDescending(entry => entry.Level)
            .ThenBy(entry => entry.ClassDefinition.Name, StringComparer.Ordinal)
            .Select(entry => entry.ClassDefinition)
            .FirstOrDefault();

        return classDefinition != null;
    }

    internal static bool TryGetPrimarySubclass(
        RulesetCharacterSimulacrum character,
        CharacterClassDefinition classDefinition,
        out CharacterSubclassDefinition subclassDefinition)
    {
        subclassDefinition = null;

        if (classDefinition == null)
        {
            return false;
        }

        if (TryGetSnapshot(character, out var snapshot) &&
            snapshot.IsCurrentSchema &&
            (subclassDefinition = snapshot.GetSubclass(classDefinition)) != null)
        {
            return true;
        }

        subclassDefinition = character?.SpellRepertoires
            .Select(repertoire => repertoire.SpellCastingSubclass)
            .FirstOrDefault(subclass =>
                subclass != null &&
                LevelUpHelper.GetClassForSubclass(subclass) == classDefinition);

        return subclassDefinition != null;
    }

    internal static bool TryEnumerateKnownLanguages(
        RulesetCharacterSimulacrum character,
        List<string> languages)
    {
        if (languages == null ||
            !TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema)
        {
            return false;
        }

        var previousCount = languages.Count;

        snapshot.EnumerateKnownLanguages(languages);

        return languages.Count > previousCount;
    }

    internal static bool IsProficientWithItem(
        RulesetCharacterSimulacrum character,
        ItemDefinition itemDefinition)
    {
        return TryGetSnapshot(character, out var snapshot) &&
               snapshot.IsCurrentSchema &&
               snapshot.IsProficientWithItem(itemDefinition);
    }

    internal static void RebuildAttackModes(RulesetCharacterMonster character)
    {
        if (character is RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Ready
            } &&
            TryGetSnapshot(character, out var snapshot))
        {
            snapshot.RebuildAttackModes(character);
        }
    }

    internal static bool TryGetUnlimitedCopiedAttackUses(
        RulesetCharacterMonster character,
        RulesetAttackMode attackMode,
        out int remainingUses)
    {
        remainingUses = 0;

        if (character is not RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Ready
            } ||
            attackMode?.SourceDefinition == null ||
            attackMode.SourceDefinition is MonsterAttackDefinition { LimitedUse: true } ||
            character.GetAttackModeRank(attackMode) >= 0 ||
            !character.AttackModes.Any(candidate =>
                candidate != null &&
                candidate.SourceDefinition == attackMode.SourceDefinition &&
                candidate.ActionType == attackMode.ActionType))
        {
            return false;
        }

        // RulesetCharacterMonster ranks attacks against MonsterDefinition.AttackIterations.
        // Simulacrum attack modes are rebuilt from its snapshot and intentionally do not mutate
        // the shared shell definition, so every ordinary copied attack otherwise resolves to
        // rank -1 and zero remaining uses. Native characters use -1 as the unlimited sentinel;
        // int.MaxValue leaks into the action panel as an apparent attack count.
        remainingUses = -1;

        return true;
    }

    internal sealed class SimulacrumSnapshotRulesetCondition :
        RulesetConditionCustom<SimulacrumSnapshotRulesetCondition>,
        IBindToRulesetConditionCustom
    {
        // Schema 17 snapshots created before the direct MonsterPresentation route already
        // contain a selected presentation. The source definition attribute is optional so
        // those valid snapshots remain loadable without a lossy schema migration.
        private const int CurrentSnapshotSchemaVersion = 17;
        private const int MaximumAttackCount = 64;
        private const int MaximumDamageFormsPerAttack = 16;
        private const int MaximumDamageFormCount = 256;
        private const int MaximumTagsPerAttack = 64;
        private const int MaximumTagCount = MaximumAttackCount * MaximumTagsPerAttack;
        private const int MaximumRepertoireCount = 16;
        private const int MaximumAutoPreparedSpellsPerRepertoire = 512;
        private const int MaximumAutoPreparedSpellCount = 1024;
        private const int MaximumSlotCapacitiesPerRepertoire = 16;
        private const int MaximumSlotCapacityCount = 256;
        private const int MaximumPowerCount = 256;
        private const int MaximumToggleTokenLength = 256;
        private const int MaximumInvocationCount = 256;
        private const int MaximumTrainedIdentityCount = 256;
        private const int MaximumFeatureCount = 1024;
        private const int MaximumAttributeCount = 512;
        private const int MaximumKnownLanguageCount = 64;
        private const int PersistentResourcePoolCount = 9;
        private const string BardicInspirationRecoveryFeatureName =
            "CampAffinityBardFontOfInspiration";
        private const string MonsterAttackKind = "MonsterAttack";
        private const string ItemAttackKind = "Item";
        private static readonly string[] RetainedAttributeModifierTagPrefixes =
        [
            AttributeDefinitions.TagAbilityScore,
            AttributeDefinitions.TagRace,
            AttributeDefinitions.TagClass,
            AttributeDefinitions.TagBackground,
            AttributeDefinitions.TagFightingStyle,
            AttributeDefinitions.TagSubclass,
            AttributeDefinitions.TagHealth,
            AttributeDefinitions.TagFeat,
            AttributeDefinitions.TagDifficultySetting,
            AttributeDefinitions.TagInvocation
        ];
        private static readonly string[] ExcludedAttributeModifierTagPrefixes =
        [
            AttributeDefinitions.TagEquipment,
            AttributeDefinitions.TagEncumbrance,
            AttributeDefinitions.TagCombat,
            AttributeDefinitions.TagEffect,
            AttributeDefinitions.TagStatus,
            AttributeDefinitions.TagLightSensitivity,
            AttributeDefinitions.TagGadget,
            AttributeDefinitions.TagDebug,
            AttributeDefinitions.TagDeath,
            AttributeDefinitions.TagConjure,
            AttributeDefinitions.TagDebugDynamic
        ];
        private static readonly HashSet<string> MissingFeatureOriginWarnings = [];

        static SimulacrumSnapshotRulesetCondition()
        {
            Category = SnapshotCategory;
            Marker = new SimulacrumSnapshotRulesetCondition();
        }

        internal static IBindToRulesetConditionCustom BindingMarker => Marker;

        internal static ConditionDefinition BindingCondition => BindingDefinition;

        internal ulong OwningEffectGuid { get; private set; }
        internal int ArmorClass { get; private set; }
        internal int Charisma { get; private set; }
        internal int CharacterLevel { get; private set; }
        internal int Constitution { get; private set; }
        internal int Dexterity { get; private set; }
        internal int HalfMaximumHitPoints { get; private set; }
        internal int Intelligence { get; private set; }
        internal int ProficiencyBonus { get; private set; }
        internal int Strength { get; private set; }
        internal int Wisdom { get; private set; }
        internal bool CanEquipHumanoidItems { get; private set; }
        internal bool IsCurrentSchema =>
            SnapshotSchemaVersion == CurrentSnapshotSchemaVersion;
        internal bool UsesInventoryAppearance =>
            SourceHumanoidPresentation?.Name?.StartsWith(
                "SimulacrumPresentation_",
                StringComparison.Ordinal) == true;
        internal IEnumerable<BaseDefinition> BehaviorCarriers => _behaviorCarriers;
        internal string DisplayName => SourceDisplayName;
        internal CharacterRaceDefinition HumanoidRace =>
            SourceHumanoidPresentation?.RaceDefinition;
        internal CharacterRaceDefinition HumanoidSubRace =>
            SourceHumanoidPresentation?.SubRaceDefinition;

        private enum AttributeModifierDisposition
        {
            Retain,
            Exclude
        }

        private sealed class SnapshotValidationException : Exception
        {
            internal SnapshotValidationException(string message)
                : base(message)
            {
            }
        }

        private string SourceDisplayName { get; set; } = string.Empty;
        private string SourceDeityName { get; set; } = string.Empty;
        private string SourceDefinitionPresentationName { get; set; } = string.Empty;
        private int SnapshotSchemaVersion { get; set; }

        private List<string> AttackAbilityScores { get; set; } = [];
        private List<int> AttackActionTypes { get; set; } = [];
        private List<int> AttackCloseRanges { get; set; } = [];
        private List<int> AttackDamageBonuses { get; set; } = [];
        private List<int> AttackDamageDiceNumbers { get; set; } = [];
        private List<int> AttackDamageDieTypes { get; set; } = [];
        private List<int> AttackDamageFormCounts { get; set; } = [];
        private List<string> AttackDamageTypes { get; set; } = [];
        private List<bool> AttackDamageVersatile { get; set; } = [];
        private List<int> AttackDamageVersatileDieTypes { get; set; } = [];
        private List<string> AttackDefinitionKinds { get; set; } = [];
        private List<string> AttackDefinitionNames { get; set; } = [];
        private List<bool> AttackHasPriority { get; set; } = [];
        private List<int> AttackMaxRanges { get; set; } = [];
        private List<int> AttackNumbers { get; set; } = [];
        private List<bool> AttackRanged { get; set; } = [];
        private List<bool> AttackReach { get; set; } = [];
        private List<int> AttackReachRanges { get; set; } = [];
        private List<string> AttackSlotNames { get; set; } = [];
        private List<int> AttackTagCounts { get; set; } = [];
        private List<string> AttackTags { get; set; } = [];
        private List<bool> AttackThrown { get; set; } = [];
        private List<int> AttackToHitBonuses { get; set; } = [];
        private List<bool> AttackUseVersatileDamage { get; set; } = [];
        private List<CharacterClassDefinition> ClassDefinitions { get; } = [];
        private List<int> ClassLevels { get; set; } = [];
        private Dictionary<string, string> ClassSubclassNames { get; set; } = [];
        private List<string> KnownLanguages { get; set; } = [];
        private List<FeatureDefinition> CopiedFeatures { get; } = [];
        private List<int> CopiedFeatureSourceTypes { get; set; } = [];
        private List<string> CopiedFeatureSourceNames { get; set; } = [];
        private List<string> CopiedFeatureSourceDefinitionNames { get; set; } = [];
        private List<string> CopiedFeatureSourceDefinitionTypes { get; set; } = [];
        private List<SmartAttributeDefinition> PersistentAttributeDefinitions { get; } = [];
        private List<string> PersistentAttributeNames { get; set; } = [];
        private List<int> PersistentAttributeMinValues { get; set; } = [];
        private List<int> PersistentAttributeMaxValues { get; set; } = [];
        private List<int> PersistentAttributeFinalValues { get; set; } = [];
        private List<int> MovementModeTypes { get; set; } = [];
        private List<int> MovementSpeeds { get; set; } = [];
        private List<int> PowerSaveDcs { get; set; } = [];
        private List<int> PowerSpentPoints { get; set; } = [];
        private List<int> PowerMaxUses { get; set; } = [];
        private List<int> PowerRemainingUses { get; set; } = [];
        private List<string> PowerOriginClassNames { get; set; } = [];
        private List<string> PowerOriginRaceNames { get; set; } = [];
        private List<string> PowerDefinitionNames { get; set; } = [];
        private List<string> ToggledPowerNames { get; set; } = [];
        private List<string> InvocationDefinitionNames { get; set; } = [];
        private List<int> InvocationRepertoireIndices { get; set; } = [];
        private List<bool> InvocationActiveStates { get; set; } = [];
        private List<bool> InvocationUsedStates { get; set; } = [];
        private List<string> TrainedFeatNames { get; set; } = [];
        private List<string> TrainedFightingStyleNames { get; set; } = [];
        private List<string> TrainedMetamagicOptionNames { get; set; } = [];
        private List<int> RepertoireAutoPreparedCounts { get; set; } = [];
        private List<SpellDefinition> RepertoireAutoPreparedSpells { get; } = [];
        private List<string> RepertoireAutoPreparedTags { get; set; } = [];
        private List<int> RepertoireFormAbilityBonuses { get; set; } = [];
        private List<int> RepertoireMaxPreparedSpells { get; set; } = [];
        private List<string> RepertoireMonsterNames { get; set; } = [];
        private List<int> RepertoireSaveDcs { get; set; } = [];
        private List<int> RepertoireSlotCapacityCounts { get; set; } = [];
        private List<int> RepertoireSlotCapacityLevels { get; set; } = [];
        private List<int> RepertoireSlotCapacityValues { get; set; } = [];
        private List<int> RepertoireSpellAttackBonuses { get; set; } = [];
        private List<int> RepertoireSpellCastingLevels { get; set; } = [];
        private List<string> SaveAbilityNames { get; set; } = [];
        private List<int> SaveProficiencyDeltas { get; set; } = [];
        private List<int> SenseRanges { get; set; } = [];
        private List<int> SenseStealthBreakerRanges { get; set; } = [];
        private List<int> SenseTypes { get; set; } = [];
        private HumanoidMonsterPresentationDefinition SourceHumanoidPresentation { get; set; }
        private MonsterPresentationDefinition SourceMonsterPresentation { get; set; }
        private List<int> SourceMorphotypeAdditionalCategories { get; set; } = [];
        private List<float> SourceMorphotypeAdditionalValues { get; set; } = [];
        private List<int> SourceMorphotypeCategories { get; set; } = [];
        private List<string> SourceMorphotypeValues { get; set; } = [];
        private int SourceSex { get; set; }
        private string SourceArmorAssetPrefix { get; set; } = string.Empty;
        private string SourceBeardShapeAssetPrefix { get; set; } = string.Empty;
        private string SourceBodyAssetPrefix { get; set; } = string.Empty;
        private List<int> SkillBonuses { get; set; } = [];
        private List<string> SkillNames { get; set; } = [];
        private List<int> SkillProficiencyRanks { get; set; } = [];
        private List<string> ArmorCategoryProficiencies { get; set; } = [];
        private List<string> ArmorTypeProficiencies { get; set; } = [];
        private List<string> WeaponCategoryProficiencies { get; set; } = [];
        private List<string> WeaponTypeProficiencies { get; set; } = [];
        private List<FeatureDefinition> RuntimeEquipmentFeatures { get; set; } = [];
        private readonly Dictionary<FeatureDefinition, FeatureOrigin> _restoredFeatureOrigins = [];
        private readonly Dictionary<FeatureDefinition, HashSet<FightingStyleDefinition>>
            _restoredFightingStyleOrigins = [];
        private readonly Dictionary<FeatureDefinition, HashSet<InvocationDefinition>>
            _restoredInvocationOrigins = [];
        private readonly HashSet<FeatureDefinition> _restoredUnconditionalFeatures = [];
        private readonly Dictionary<FeatureDefinition, FeatureOrigin> _runtimeEquipmentFeatureOrigins = [];
        private readonly HashSet<BaseDefinition> _behaviorCarriers = [];
        private string SourceFaceShapeAssetPrefix { get; set; } = string.Empty;
        private string SourceHairShapeAssetPrefix { get; set; } = string.Empty;
        private string SourceHelmetAssetPrefix { get; set; } = string.Empty;
        private string SourceHornsTailAssetPrefix { get; set; } = string.Empty;

        internal static void Bind(ConditionDefinition conditionDefinition)
        {
            BindingDefinition = conditionDefinition;
        }

        internal static bool TryCreateSeed(
            RulesetCharacter source,
            SimulacrumAppearanceSeed appearance,
            out SimulacrumSnapshotSeed seed,
            out string failure)
        {
            seed = null;
            failure = null;

            try
            {
                if (source == null ||
                    appearance == null ||
                    string.IsNullOrWhiteSpace(appearance.DisplayName) ||
                    appearance.HumanoidPresentation == null &&
                    appearance.MonsterPresentation == null &&
                    string.IsNullOrEmpty(appearance.DefinitionPresentationSourceName))
                {
                    failure = "required source values are missing";

                    return false;
                }

                var attributes = new Dictionary<string, PersistentAttributeSeed>(
                    StringComparer.Ordinal);
                var persistentAttributeValues = new Dictionary<string, int>(StringComparer.Ordinal);
                var capturesMonsterBaseline = CapturesMonsterBaseline(source);
                var summoningBaselineConditions =
                    CollectSummoningBaselineConditions(source);

                if (source.Attributes.Count > MaximumAttributeCount)
                {
                    failure = "source attribute limit was exceeded";

                    return false;
                }

                foreach (var pair in source.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var attributeName = pair.Key;
                    var sourceAttribute = pair.Value;

                    if (string.IsNullOrEmpty(attributeName) ||
                        sourceAttribute?.AttributeDefinition == null)
                    {
                        failure = "source attribute references are invalid";

                        return false;
                    }

                    var finalValue = GetPersistentAttributeValue(
                        source,
                        attributeName,
                        0,
                        persistentAttributeValues,
                        new HashSet<string>(StringComparer.Ordinal),
                        summoningBaselineConditions);

                    attributes.Add(
                        attributeName,
                        new PersistentAttributeSeed(
                            attributeName,
                            sourceAttribute.AttributeDefinition,
                            sourceAttribute.BaseValue,
                            sourceAttribute.MinValue,
                            sourceAttribute.MaxValue,
                            finalValue));
                }

                AddMissingMonsterSnapshotAttributes(source, attributes);

                if (attributes.Count > MaximumAttributeCount)
                {
                    failure = "source attribute limit was exceeded after monster defaults were added";

                    return false;
                }

                var missingAttributeNames = SnapshotAttributeNames
                    .Where(attributeName => !attributes.ContainsKey(attributeName))
                    .ToArray();

                if (missingAttributeNames.Length > 0)
                {
                    failure =
                        $"required source attributes are missing: {string.Join(", ", missingAttributeNames)}";

                    return false;
                }

                if (attributes.TryGetValue(
                        AttributeDefinitions.CharacterLevel,
                        out var characterLevelAttribute) &&
                    characterLevelAttribute.FinalValue <= 0)
                {
                    failure = "source character level is invalid";

                    return false;
                }

                var features = CollectSourceFeatureStates(
                    source,
                    capturesMonsterBaseline);
                var sourceRepertoires = source.SpellRepertoires
                    .Where(repertoire => repertoire?.SpellCastingFeature != null)
                    .ToArray();
                var sourceInvocations = source.Invocations
                    .Where(invocation => invocation?.invocationDefinition != null)
                    .ToArray();
                var sourceAttacks = source.AttackModes
                    .Where(attack =>
                        attack?.SourceDefinition != null &&
                        (source is RulesetCharacterMonster || attack.SourceObject is not RulesetItem) &&
                        attack.SourceDefinition is MonsterAttackDefinition or ItemDefinition)
                    .ToArray();

                if (features.Count > MaximumFeatureCount ||
                    sourceRepertoires.Length > MaximumRepertoireCount ||
                    sourceInvocations.Length > MaximumInvocationCount ||
                    sourceAttacks.Length > MaximumAttackCount)
                {
                    failure = "source feature, spell, invocation, or attack limit was exceeded";

                    return false;
                }

                if (features.Any(feature =>
                        feature.Feature == null ||
                        string.IsNullOrEmpty(feature.Feature.Name) ||
                        string.IsNullOrEmpty(feature.Origin.sourceName)) ||
                    sourceRepertoires.Any(repertoire => repertoire.SpellCastingFeature == null) ||
                    sourceInvocations.Any(invocation =>
                        string.IsNullOrEmpty(invocation.invocationDefinition.Name) ||
                        !TryGetDefinition<InvocationDefinition>(
                            invocation.invocationDefinition.Name,
                            out var definition) ||
                        definition != invocation.invocationDefinition))
                {
                    failure = "feature, repertoire, or invocation references are invalid";

                    return false;
                }

                var repertoires = CollectSpellRepertoires(
                    source,
                    sourceRepertoires,
                    attributes);
                var autoPreparedSpellCount = repertoires.Sum(repertoire =>
                    repertoire.AutoPreparedSpells.Count);
                var slotCapacityCount = repertoires.Sum(repertoire =>
                    repertoire.SlotCapacities.Count);

                if (repertoires.Any(repertoire =>
                        repertoire.AutoPreparedSpells.Count >
                        MaximumAutoPreparedSpellsPerRepertoire ||
                        repertoire.SlotCapacities.Count >
                        MaximumSlotCapacitiesPerRepertoire ||
                        string.IsNullOrEmpty(repertoire.SpellCastingAbility)) ||
                    autoPreparedSpellCount > MaximumAutoPreparedSpellCount ||
                    slotCapacityCount > MaximumSlotCapacityCount)
                {
                    failure = "spell repertoire data limit or reference is invalid";

                    return false;
                }

                var repertoireIndices = sourceRepertoires
                    .Select((repertoire, index) => (repertoire, index))
                    .ToDictionary(pair => pair.repertoire, pair => pair.index);
                var invocations = sourceInvocations
                    .Select(invocation => new InvocationSeed(
                        invocation.invocationDefinition,
                        invocation.invocationRepertoire != null &&
                        repertoireIndices.TryGetValue(invocation.invocationRepertoire, out var index)
                            ? index
                            : -1,
                        invocation.active,
                        invocation.used))
                    .ToArray();

                var damageFormCount = 0;
                var attackTagCount = 0;

                foreach (var attack in sourceAttacks)
                {
                    var definitionIsValid = attack.SourceDefinition switch
                    {
                        MonsterAttackDefinition monsterAttack =>
                            TryGetDefinition<MonsterAttackDefinition>(
                                monsterAttack.Name,
                                out var resolvedMonsterAttack) &&
                            resolvedMonsterAttack == monsterAttack,
                        ItemDefinition item =>
                            TryGetDefinition<ItemDefinition>(item.Name, out var resolvedItem) &&
                            resolvedItem == item,
                        _ => false
                    };
                    var damageForms = attack.EffectDescription?.EffectForms.Count(form =>
                        form.FormType == EffectForm.EffectFormType.Damage) ?? 0;

                    if (!definitionIsValid ||
                        damageForms > MaximumDamageFormsPerAttack ||
                        attack.AttackTags.Count > MaximumTagsPerAttack)
                    {
                        failure = "attack form or tag limit was exceeded";

                        return false;
                    }

                    damageFormCount = checked(damageFormCount + damageForms);
                    attackTagCount = checked(attackTagCount + attack.AttackTags.Count);
                }

                if (damageFormCount > MaximumDamageFormCount ||
                    attackTagCount > MaximumTagCount)
                {
                    failure = "attack data limit was exceeded";

                    return false;
                }

                var attacks = CollectAttackSeeds(
                    source,
                    sourceAttacks,
                    attributes,
                    summoningBaselineConditions);

                var powers = CollectSafeSourcePowers(source);
                var duplicatePowerName = powers
                    .Where(power => power.Definition != null)
                    .GroupBy(power => power.Definition.Name, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);

                if (powers.Count > MaximumPowerCount ||
                    powers.Any(power =>
                        power.Definition == null ||
                        string.IsNullOrEmpty(power.Definition.Name) ||
                        !TryGetDefinition<FeatureDefinitionPower>(
                            power.Definition.Name,
                            out var resolved) ||
                        resolved != power.Definition ||
                        power.EffectiveMaxUses < 0 ||
                        power.RemainingUses < 0 ||
                        power.RemainingUses > power.EffectiveMaxUses ||
                        power.SpentPoints < 0) ||
                    duplicatePowerName != null)
                {
                    failure = duplicatePowerName == null
                        ? "power definitions or resources are invalid"
                        : $"power definition name {duplicatePowerName.Key} is ambiguous";

                    return false;
                }

                var toggles = source.ToggledPowersOn
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(token => token, StringComparer.Ordinal)
                    .ToArray();

                if (toggles.Length > MaximumPowerCount ||
                    toggles.Any(token => token.Length > MaximumToggleTokenLength))
                {
                    failure = "toggle state is invalid";

                    return false;
                }

                var classes = CollectClassLevels(source);
                var subclasses = CollectSubclasses(source);
                var knownLanguages = CollectKnownLanguages(source);
                var equipmentEligibility = CollectEquipmentEligibility(source);
                var movementModes = CollectMovementModes(
                    source,
                    capturesMonsterBaseline,
                    summoningBaselineConditions);
                var senseModes = CollectSenseModes(
                    source,
                    capturesMonsterBaseline,
                    summoningBaselineConditions);
                var skillBonuses = CollectSkillBonuses(
                    source,
                    attributes,
                    summoningBaselineConditions);
                var savingThrows = CollectSavingThrows(
                    source,
                    summoningBaselineConditions);
                var sourceHero = source as RulesetCharacterHero;
                var trainedFeatNames = CollectTrainedDefinitionNames(
                    sourceHero?.TrainedFeats,
                    "feat");
                var trainedFightingStyleNames = CollectTrainedDefinitionNames(
                    sourceHero?.TrainedFightingStyles,
                    "fighting style");
                var trainedMetamagicOptionNames = CollectTrainedDefinitionNames(
                    sourceHero?.TrainedMetamagicOptions,
                    "metamagic option");

                seed = new SimulacrumSnapshotSeed(
                    source.Guid,
                    appearance,
                    attributes,
                    features,
                    repertoires,
                    invocations,
                    attacks,
                    powers,
                    toggles,
                    new PersistentResourceSeed(source),
                    classes,
                    subclasses,
                    knownLanguages,
                    equipmentEligibility,
                    movementModes,
                    senseModes,
                    skillBonuses,
                    savingThrows,
                    trainedFeatNames,
                    trainedFightingStyleNames,
                    trainedMetamagicOptionNames);

                return true;
            }
            catch (SnapshotValidationException ex)
            {
                failure = ex.Message;

                return false;
            }
            catch (Exception ex)
            {
                failure = null;
                Trace.LogException(new Exception(
                    "Error validating a Simulacrum source before summoning.",
                    ex));

                return false;
            }
        }

        private static IReadOnlyList<string> CollectTrainedDefinitionNames<TDefinition>(
            IEnumerable<TDefinition> definitions,
            string identityType)
            where TDefinition : BaseDefinition
        {
            var values = (definitions ?? Enumerable.Empty<TDefinition>()).ToArray();

            if (values.Length > MaximumTrainedIdentityCount ||
                values.Any(definition =>
                    definition == null ||
                    string.IsNullOrEmpty(definition.Name) ||
                    !TryGetDefinition<TDefinition>(definition.Name, out var resolved) ||
                    resolved != definition))
            {
                throw new SnapshotValidationException(
                    $"trained {identityType} references are invalid");
            }

            return values
                .Select(definition => definition.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        private bool HasValidSourcePresentation()
        {
            if (SourceHumanoidPresentation != null || SourceMonsterPresentation != null)
            {
                return true;
            }

            return !string.IsNullOrEmpty(SourceDefinitionPresentationName) &&
                   TryGetDefinition<MonsterDefinition>(
                       SourceDefinitionPresentationName,
                       out var sourceDefinition) &&
                   sourceDefinition.MonsterPresentation != null;
        }

        internal bool TryValidateSnapshot(out string failure)
        {
            failure = null;

            if (!IsCurrentSchema)
            {
                failure = "legacy schema";

                return false;
            }

            try
            {
                if (OwningEffectGuid == 0 ||
                    HalfMaximumHitPoints <= 0 ||
                    CharacterLevel <= 0 ||
                    string.IsNullOrWhiteSpace(SourceDisplayName) ||
                    (!string.IsNullOrEmpty(SourceDeityName) &&
                     !TryGetDefinition<DeityDefinition>(SourceDeityName, out _)) ||
                    !HasValidSourcePresentation())
                {
                    failure = "required snapshot values are missing";

                    return false;
                }

                var attackCount = AttackDefinitionNames.Count;

                if (attackCount > MaximumAttackCount ||
                    !AllCountsEqual(
                        attackCount,
                        AttackDefinitionKinds.Count,
                        AttackTagCounts.Count,
                        AttackDamageFormCounts.Count,
                        AttackActionTypes.Count,
                        AttackAbilityScores.Count,
                        AttackNumbers.Count,
                        AttackToHitBonuses.Count,
                        AttackCloseRanges.Count,
                        AttackMaxRanges.Count,
                        AttackReachRanges.Count,
                        AttackSlotNames.Count,
                        AttackRanged.Count,
                        AttackReach.Count,
                        AttackThrown.Count,
                        AttackHasPriority.Count,
                        AttackUseVersatileDamage.Count))
                {
                    failure = "attack list shape is invalid";

                    return false;
                }

                if (!TryValidateFlatCounts(
                        AttackTagCounts,
                        MaximumTagsPerAttack,
                        MaximumTagCount,
                        AttackTags.Count,
                        "attack tags",
                        out failure))
                {
                    return false;
                }

                if (!TryValidateFlatCounts(
                        AttackDamageFormCounts,
                        MaximumDamageFormsPerAttack,
                        MaximumDamageFormCount,
                        AttackDamageTypes.Count,
                        "damage forms",
                        out failure) ||
                    !AllCountsEqual(
                        AttackDamageTypes.Count,
                        AttackDamageDieTypes.Count,
                        AttackDamageVersatile.Count,
                        AttackDamageVersatileDieTypes.Count,
                        AttackDamageDiceNumbers.Count,
                        AttackDamageBonuses.Count))
                {
                    failure ??= "damage form list shape is invalid";

                    return false;
                }

                for (var index = 0; index < attackCount; index++)
                {
                    var sourceDefinition = ResolveAttackDefinition(index);

                    if (string.IsNullOrEmpty(AttackDefinitionNames[index]) ||
                        sourceDefinition == null ||
                        GetAttackEffectDescription(sourceDefinition) == null)
                    {
                        failure = $"attack definition {index} cannot be restored";

                        return false;
                    }
                }

                if (ClassDefinitions.Count > 32 ||
                    ClassDefinitions.Any(x => x == null) ||
                    ClassDefinitions.Count != ClassLevels.Count ||
                    ClassLevels.Any(level => level <= 0) ||
                    !TryValidateSubclasses(out failure) ||
                    KnownLanguages.Count > MaximumKnownLanguageCount ||
                    KnownLanguages.Any(string.IsNullOrEmpty) ||
                    KnownLanguages.Distinct(StringComparer.Ordinal).Count() != KnownLanguages.Count ||
                    MovementModeTypes.Count > 32 ||
                    MovementModeTypes.Count != MovementSpeeds.Count ||
                    SenseTypes.Count > 32 ||
                    !AllCountsEqual(
                        SenseTypes.Count,
                        SenseRanges.Count,
                        SenseStealthBreakerRanges.Count) ||
                    SkillNames.Count > 128 ||
                    !AllCountsEqual(
                        SkillNames.Count,
                        SkillBonuses.Count,
                        SkillProficiencyRanks.Count) ||
                    SkillNames.Any(string.IsNullOrEmpty) ||
                    SkillNames.Distinct(StringComparer.Ordinal).Count() !=
                    SkillNames.Count ||
                    SkillProficiencyRanks.Any(rank => rank is < 1 or > 2) ||
                    SaveAbilityNames.Count > 16 ||
                    SaveAbilityNames.Count != SaveProficiencyDeltas.Count)
                {
                    failure ??= "class, movement, sense, skill, or save list shape is invalid";

                    return false;
                }

                var attributeCount = PersistentAttributeNames.Count;

                if (attributeCount == 0 ||
                    attributeCount > MaximumAttributeCount ||
                    !AllCountsEqual(
                        attributeCount,
                        PersistentAttributeDefinitions.Count,
                        PersistentAttributeMinValues.Count,
                        PersistentAttributeMaxValues.Count,
                        PersistentAttributeFinalValues.Count) ||
                    PersistentAttributeDefinitions.Any(x => x == null) ||
                    PersistentAttributeNames.Any(string.IsNullOrEmpty) ||
                    PersistentAttributeNames.Distinct(StringComparer.Ordinal).Count() !=
                    attributeCount ||
                    SnapshotAttributeNames.Any(name => !PersistentAttributeNames.Contains(name)) ||
                    PersistentAttributeMinValues.Where((value, index) =>
                            value > PersistentAttributeMaxValues[index])
                        .Any())
                {
                    failure = "persistent attribute list shape is invalid";

                    return false;
                }

                if (SourceMorphotypeCategories.Count > 64 ||
                    SourceMorphotypeCategories.Count != SourceMorphotypeValues.Count ||
                    SourceMorphotypeAdditionalCategories.Count > 64 ||
                    SourceMorphotypeAdditionalCategories.Count !=
                    SourceMorphotypeAdditionalValues.Count)
                {
                    failure = "appearance list shape is invalid";

                    return false;
                }

                if (PowerDefinitionNames.Count > MaximumPowerCount ||
                    !AllCountsEqual(
                        PowerDefinitionNames.Count,
                        PowerSaveDcs.Count,
                        PowerSpentPoints.Count,
                        PowerMaxUses.Count,
                        PowerRemainingUses.Count,
                        PowerOriginClassNames.Count,
                        PowerOriginRaceNames.Count))
                {
                    failure = "power list shape is invalid";

                    return false;
                }

                if (PowerDefinitionNames.Any(string.IsNullOrEmpty) ||
                    PowerDefinitionNames.Distinct(StringComparer.Ordinal).Count() !=
                    PowerDefinitionNames.Count ||
                    PowerDefinitionNames.Any(x =>
                        !TryGetDefinition<FeatureDefinitionPower>(x, out _)) ||
                    PowerMaxUses.Any(x => x < 0) ||
                    PowerRemainingUses.Where((value, index) =>
                            value < 0 || value > PowerMaxUses[index])
                        .Any() ||
                    PowerSpentPoints.Any(x => x < 0) ||
                    ToggledPowerNames.Count > MaximumPowerCount ||
                    ToggledPowerNames.Any(string.IsNullOrEmpty) ||
                    ToggledPowerNames.Any(x => x.Length > MaximumToggleTokenLength) ||
                    ToggledPowerNames.Distinct(StringComparer.Ordinal).Count() !=
                    ToggledPowerNames.Count)
                {
                    failure = "power definitions or toggles are invalid";

                    return false;
                }

                if (InvocationDefinitionNames.Count > MaximumInvocationCount ||
                    !AllCountsEqual(
                        InvocationDefinitionNames.Count,
                        InvocationRepertoireIndices.Count,
                        InvocationActiveStates.Count,
                        InvocationUsedStates.Count) ||
                    InvocationRepertoireIndices.Any(index =>
                        index < -1 || index >= RepertoireAutoPreparedCounts.Count))
                {
                    failure = "invocation list shape is invalid";

                    return false;
                }

                if (InvocationDefinitionNames.Any(string.IsNullOrEmpty))
                {
                    failure = "invocation definitions are invalid";

                    return false;
                }

                if (InvocationDefinitionNames.Any(x =>
                        !TryGetDefinition<InvocationDefinition>(x, out _)))
                {
                    failure = "invocation definitions cannot be restored";

                    return false;
                }

                if (!TryValidateTrainedDefinitionNames<FeatDefinition>(
                        TrainedFeatNames,
                        "feat",
                        out failure) ||
                    !TryValidateTrainedDefinitionNames<FightingStyleDefinition>(
                        TrainedFightingStyleNames,
                        "fighting style",
                        out failure) ||
                    !TryValidateTrainedDefinitionNames<MetamagicOptionDefinition>(
                        TrainedMetamagicOptionNames,
                        "metamagic option",
                        out failure))
                {
                    return false;
                }

                var repertoireCount = RepertoireAutoPreparedCounts.Count;

                if (repertoireCount > MaximumRepertoireCount ||
                    !AllCountsEqual(
                        repertoireCount,
                        RepertoireAutoPreparedTags.Count,
                        RepertoireFormAbilityBonuses.Count,
                        RepertoireMaxPreparedSpells.Count,
                        RepertoireMonsterNames.Count,
                        RepertoireSaveDcs.Count,
                        RepertoireSlotCapacityCounts.Count,
                        RepertoireSpellAttackBonuses.Count,
                        RepertoireSpellCastingLevels.Count) ||
                    !TryValidateFlatCounts(
                        RepertoireAutoPreparedCounts,
                        MaximumAutoPreparedSpellsPerRepertoire,
                        MaximumAutoPreparedSpellCount,
                        RepertoireAutoPreparedSpells.Count,
                        "auto-prepared spells",
                        out failure) ||
                    !TryValidateFlatCounts(
                        RepertoireSlotCapacityCounts,
                        MaximumSlotCapacitiesPerRepertoire,
                        MaximumSlotCapacityCount,
                        RepertoireSlotCapacityLevels.Count,
                        "spell slot capacities",
                        out failure) ||
                    RepertoireSlotCapacityLevels.Count != RepertoireSlotCapacityValues.Count)
                {
                    failure ??= "spell repertoire list shape is invalid";

                    return false;
                }

                if (RepertoireAutoPreparedSpells.Any(x => x == null))
                {
                    failure = "auto-prepared spell references are invalid";

                    return false;
                }

                if (CopiedFeatures.Count > MaximumFeatureCount ||
                    !AllCountsEqual(
                        CopiedFeatures.Count,
                        CopiedFeatureSourceTypes.Count,
                        CopiedFeatureSourceNames.Count,
                        CopiedFeatureSourceDefinitionNames.Count,
                        CopiedFeatureSourceDefinitionTypes.Count) ||
                    RuntimeEquipmentFeatures.Count > MaximumFeatureCount ||
                    ArmorCategoryProficiencies.Count > 256 ||
                    ArmorTypeProficiencies.Count > 256 ||
                    WeaponCategoryProficiencies.Count > 256 ||
                    WeaponTypeProficiencies.Count > 256)
                {
                    failure = "feature or proficiency list exceeds the safe limit";

                    return false;
                }

                if (CopiedFeatures.Any(x => x == null) ||
                    CopiedFeatureSourceNames.Any(string.IsNullOrEmpty))
                {
                    failure = "copied feature references are invalid";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                failure = $"snapshot validation failed: {ex.GetType().Name}";
                Trace.LogException(new Exception(
                    "Error validating a Simulacrum snapshot.",
                    ex));

                return false;
            }
        }

        private static bool TryValidateTrainedDefinitionNames<TDefinition>(
            IReadOnlyCollection<string> names,
            string identityType,
            out string failure)
            where TDefinition : BaseDefinition
        {
            failure = null;

            if (names == null ||
                names.Count > MaximumTrainedIdentityCount ||
                names.Any(string.IsNullOrEmpty) ||
                names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            {
                failure = $"trained {identityType} list shape is invalid";

                return false;
            }

            if (names.Any(name => !TryGetDefinition<TDefinition>(name, out _)))
            {
                failure = $"trained {identityType} definitions cannot be restored";

                return false;
            }

            return true;
        }

        private bool TryValidateSubclasses(out string failure)
        {
            failure = null;

            if (ClassSubclassNames == null ||
                ClassSubclassNames.Count > ClassDefinitions.Count ||
                ClassSubclassNames.Any(pair =>
                    string.IsNullOrEmpty(pair.Key) ||
                    string.IsNullOrEmpty(pair.Value)))
            {
                failure = "class/subclass association shape is invalid";

                return false;
            }

            var subclassDatabase = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>();

            foreach (var pair in ClassSubclassNames)
            {
                var classDefinition = ClassDefinitions.FirstOrDefault(definition =>
                    definition?.Name == pair.Key);
                var subclassDefinition = subclassDatabase.GetElement(pair.Value);

                if (classDefinition == null ||
                    subclassDefinition == null ||
                    LevelUpHelper.GetClassForSubclass(subclassDefinition) != classDefinition)
                {
                    failure = $"class/subclass association {pair.Key}:{pair.Value} is invalid";

                    return false;
                }
            }

            return true;
        }

        public void ReplaceRulesetCondition(
            RulesetCondition originalRulesetCondition,
            out RulesetCondition replacedRulesetCondition)
        {
            replacedRulesetCondition = GetFromPoolAndCopyOriginalRulesetCondition(originalRulesetCondition);
        }

        internal void Capture(
            RulesetCharacter source,
            RulesetCharacterMonster duplicate,
            RulesetEffect activeEffect,
            SimulacrumSnapshotSeed seed)
        {
            if (duplicate is not RulesetCharacterSimulacrum simulacrum ||
                simulacrum.CharacterInventory == null ||
                seed == null ||
                seed.CopiedCharacterGuid != source.Guid)
            {
                throw new InvalidOperationException(
                    "Simulacrum shell does not own an independent inventory.");
            }

            SnapshotSchemaVersion = CurrentSnapshotSchemaVersion;
            OwningEffectGuid = activeEffect.Guid;
            SourceDisplayName = seed.Appearance.DisplayName;
            SourceDeityName = source.DeityDefinition?.Name ?? string.Empty;
            CharacterLevel = seed.GetAttributeValue(AttributeDefinitions.CharacterLevel, 1);
            Strength = seed.GetAttributeValue(AttributeDefinitions.Strength);
            Dexterity = seed.GetAttributeValue(AttributeDefinitions.Dexterity);
            Constitution = seed.GetAttributeValue(AttributeDefinitions.Constitution);
            Intelligence = seed.GetAttributeValue(AttributeDefinitions.Intelligence);
            Wisdom = seed.GetAttributeValue(AttributeDefinitions.Wisdom);
            Charisma = seed.GetAttributeValue(AttributeDefinitions.Charisma);
            ProficiencyBonus = seed.GetAttributeValue(AttributeDefinitions.ProficiencyBonus);
            ArmorClass = seed.GetAttributeValue(AttributeDefinitions.ArmorClass);
            HalfMaximumHitPoints = seed.HalfMaximumHitPoints;

            CapturePersistentAttributes(seed.Attributes);
            CaptureClasses(seed.Classes);
            CaptureSubclasses(seed.Subclasses);
            CaptureKnownLanguages(seed.KnownLanguages);
            CaptureTrainedIdentities(seed);
            CaptureEquipmentEligibility(seed.EquipmentEligibility);
            CaptureFeatures(seed.Features);
            CaptureMovementAndSenses(seed.MovementModes, seed.SenseModes);
            CaptureSkillAndSavingThrowBonuses(seed.SkillBonuses, seed.SavingThrows);
            CaptureAttacks(seed.Attacks);
            CaptureAppearance(seed.Appearance, duplicate);

            var sourcePowers = seed.Powers;

            EnsureRequiredAttributes(
                null,
                duplicate,
                sourcePowers.Select(x => x.Definition));
            CopySpellRepertoires(duplicate, seed);

            CaptureSpellRepertoires(duplicate);
            CaptureInvocations(seed.Invocations);
            RestoreInvocations(duplicate);
            CopyUsablePowers(duplicate, sourcePowers);

            duplicate.ToggledPowersOn.Clear();
            duplicate.ToggledPowersOn.AddRange(seed.Toggles);
            CaptureUsablePowers(sourcePowers, duplicate);
            seed.Resources.Apply(duplicate);

            if (!TryValidateSnapshot(out var validationFailure))
            {
                throw new InvalidOperationException(
                    $"Simulacrum snapshot is invalid: {validationFailure}.");
            }

            Reapply(
                simulacrum,
                HalfMaximumHitPoints,
                initializeRuntimeResourcesFromSnapshot: true);
            if (!ValidateRuntimeInvariants(simulacrum))
            {
                throw new InvalidOperationException(
                    "Simulacrum runtime invariants were not established.");
            }

            // Initial graphics binding is still in progress here. Appearance
            // was seeded before registration; portrait and graphics refreshes
            // start only after the snapshot becomes Ready.
        }

        internal bool IsProficientWithItem(ItemDefinition itemDefinition)
        {
            if (!CanEquipHumanoidItems || itemDefinition == null)
            {
                return false;
            }

            if (itemDefinition.IsWeapon)
            {
                var weaponType = itemDefinition.WeaponDescription?.WeaponTypeDefinition;

                return weaponType != null &&
                       (WeaponTypeProficiencies.Contains(weaponType.Name) ||
                        WeaponCategoryProficiencies.Contains(weaponType.WeaponCategory));
            }

            if (itemDefinition.IsArmor)
            {
                var armorType = itemDefinition.ArmorDescription?.ArmorTypeDefinition;

                return armorType != null &&
                       (!armorType.RequiresProficiency ||
                        ArmorTypeProficiencies.Contains(armorType.Name) ||
                        ArmorCategoryProficiencies.Contains(armorType.ArmorCategory));
            }

            return true;
        }

        internal int GetClassLevel(CharacterClassDefinition classDefinition)
        {
            var index = ClassDefinitions.IndexOf(classDefinition);

            return index < 0 || index >= ClassLevels.Count ? 0 : ClassLevels[index];
        }

        internal int GetSubclassLevel(
            CharacterClassDefinition classDefinition,
            string subclassName)
        {
            var classLevel = GetClassLevel(classDefinition);

            if (classLevel <= 0 || string.IsNullOrEmpty(subclassName))
            {
                return 0;
            }

            if (GetSubclass(classDefinition) is { } explicitOrRestoredSubclass &&
                explicitOrRestoredSubclass.Name == subclassName)
            {
                return classLevel;
            }

            var subclassTypeName = typeof(CharacterSubclassDefinition).FullName;
            var count = Math.Min(
                CopiedFeatureSourceNames.Count,
                Math.Min(
                    CopiedFeatureSourceDefinitionNames.Count,
                    CopiedFeatureSourceDefinitionTypes.Count));

            for (var index = 0; index < count; index++)
            {
                if (CopiedFeatureSourceDefinitionTypes[index] == subclassTypeName &&
                    (CopiedFeatureSourceDefinitionNames[index] == subclassName ||
                     CopiedFeatureSourceNames[index] == subclassName))
                {
                    return classLevel;
                }
            }

            return 0;
        }

        internal IReadOnlyList<ClassLevelSeed> GetClassLevels()
        {
            return ClassDefinitions
                .Take(Math.Min(ClassDefinitions.Count, ClassLevels.Count))
                .Select((definition, index) => new ClassLevelSeed(definition, ClassLevels[index]))
                .ToArray();
        }

        internal IEnumerable<FeatDefinition> EnumerateTrainedFeats()
        {
            return EnumerateTrainedDefinitions<FeatDefinition>(TrainedFeatNames);
        }

        internal IEnumerable<FightingStyleDefinition> EnumerateTrainedFightingStyles()
        {
            return EnumerateTrainedDefinitions<FightingStyleDefinition>(TrainedFightingStyleNames);
        }

        internal IEnumerable<MetamagicOptionDefinition> EnumerateTrainedMetamagicOptions()
        {
            return EnumerateTrainedDefinitions<MetamagicOptionDefinition>(TrainedMetamagicOptionNames);
        }

        private static IEnumerable<TDefinition> EnumerateTrainedDefinitions<TDefinition>(
            IEnumerable<string> names)
            where TDefinition : BaseDefinition
        {
            foreach (var name in names)
            {
                if (TryGetDefinition<TDefinition>(name, out var definition))
                {
                    yield return definition;
                }
            }
        }

        internal CharacterSubclassDefinition GetSubclass(CharacterClassDefinition classDefinition)
        {
            var database = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>();

            if (classDefinition != null &&
                ClassSubclassNames.TryGetValue(classDefinition.Name, out var subclassName) &&
                !string.IsNullOrEmpty(subclassName) &&
                database.GetElement(subclassName) is { } explicitSubclass &&
                LevelUpHelper.GetClassForSubclass(explicitSubclass) == classDefinition)
            {
                return explicitSubclass;
            }

            return null;
        }

        internal void EnumerateKnownLanguages(List<string> languages)
        {
            foreach (var language in KnownLanguages)
            {
                if (!languages.Contains(language))
                {
                    languages.Add(language);
                }
            }
        }

        internal void Reapply(RulesetCharacter target)
        {
            if (target is not RulesetCharacterSimulacrum duplicate ||
                 !IsCurrentSchema ||
                 HalfMaximumHitPoints <= 0)
            {
                return;
            }

            if (!TryValidateSnapshot(out var validationFailure) ||
                duplicate.SpellRepertoires.Count != RepertoireAutoPreparedCounts.Count)
            {
                throw new InvalidOperationException(
                    $"Simulacrum snapshot cannot be restored: " +
                    $"{validationFailure ?? "spell repertoire count is invalid"}.");
            }

            Reapply(
                duplicate,
                Math.Min(duplicate.CurrentHitPoints, HalfMaximumHitPoints));
        }

        private void Reapply(
            RulesetCharacterSimulacrum duplicate,
            int currentHitPoints,
            bool initializeRuntimeResourcesFromSnapshot = false)
        {
            EnsureRequiredAttributes(
                null,
                duplicate,
                duplicate.UsablePowers.Select(power => power.PowerDefinition));

            duplicate.ForcedName = SourceDisplayName;
            RestoreDeity(duplicate);
            RestoreAppearance(duplicate);
            RestoreCopiedFeaturesAndOrigins(duplicate);
            PrepareEquipmentFeatures(duplicate);
            SynchronizeConditionalFeatures(duplicate);
            RestorePersistentAttributes(duplicate);

            var runtimeState = new DuplicateRuntimeState(duplicate, currentHitPoints);

            UnbindSnapshotHandlers(duplicate);
            duplicate.RefreshAll();
            RestoreSnapshotValues(duplicate, runtimeState);
            // CopyUsablePowers is necessarily called before every copied max-use feature is
            // active. Preserve the authoritative snapshot values during initial construction;
            // subsequent refreshes preserve resources actually spent by the duplicate.
            runtimeState.Restore(
                duplicate,
                restorePowerResources: !initializeRuntimeResourcesFromSnapshot);
        }

        internal void RestoreSnapshotValues(
            RulesetCharacter target,
            DuplicateRuntimeState runtimeState = null)
        {
            if (target is not RulesetCharacterSimulacrum duplicate ||
                !IsCurrentSchema ||
                HalfMaximumHitPoints <= 0)
            {
                return;
            }

            if (!TryValidateSnapshot(out var validationFailure) ||
                duplicate.SpellRepertoires.Count != RepertoireAutoPreparedCounts.Count)
            {
                throw new InvalidOperationException(
                    $"Simulacrum snapshot values cannot be restored: " +
                    $"{validationFailure ?? "spell repertoire count is invalid"}.");
            }

            EnsureRequiredAttributes(
                null,
                duplicate,
                duplicate.UsablePowers.Select(power => power.PowerDefinition));
            duplicate.ForcedName = SourceDisplayName;
            RestoreDeity(duplicate);
            RestoreAppearance(duplicate);
            RestorePersistentAttributes(duplicate);

            // Runtime invocation state must be established before publishing its
            // feature graph. Otherwise RefreshAll resets permanent toggles and spent
            // invocations to their creation-time snapshot while their effects remain
            // unconditionally active.
            RestoreSpellRepertoires(duplicate);
            RestoreInvocations(duplicate);
            runtimeState?.RestoreInvocationStates(duplicate);

            RestoreCopiedFeaturesAndOrigins(duplicate);
            PrepareEquipmentFeatures(duplicate);
            SynchronizeConditionalFeatures(duplicate);
            ApplyEquipmentAttributeModifiers(duplicate);
            duplicate.RefreshAttributes();

            if (!CanEquipHumanoidItems)
            {
                RemoveSummoningBaselineAttributeModifiers(duplicate);
            }

            RestorePowerDefinitionsAndResources(duplicate);
            SynchronizeConditionalFeatures(duplicate);
            if (CanEquipHumanoidItems)
            {
                RefreshEncumbrance(duplicate);
            }

            duplicate.RefreshMoveModes();
            RestoreMovementAndSenses(duplicate);
            RestoreSkillAndSavingThrowBonuses(duplicate);
            RebuildAttackModes(duplicate);

            if (CanEquipHumanoidItems)
            {
                ApplyEquipmentArmorClass(duplicate);
            }
            else
            {
                ApplyCreatureArmorClass(duplicate);
            }
        }

        internal void RestoreHitPointsAfterCompute(
            RulesetCharacterSimulacrum duplicate,
            int currentHitPoints)
        {
            SetBaseAttribute(
                duplicate,
                AttributeDefinitions.HitPoints,
                HalfMaximumHitPoints);
            // ComputeHitPoints runs inside RefreshAll. ForceSetHealth raises
            // CharacterRefreshed immediately, which is unsafe while the inventory screen is in
            // the middle of equipping an item and its Hero-only panels are deliberately unbound.
            // RefreshAll's caller performs the one UI refresh after the snapshot is coherent.
            duplicate.CurrentHitPoints =
                Math.Min(Math.Max(0, currentHitPoints), HalfMaximumHitPoints);
        }

        private void RestoreCopiedFeaturesAndOrigins(
            RulesetCharacterSimulacrum duplicate)
        {
            EnsureBardicInspirationRecoveryFeature();

            _restoredFeatureOrigins.Clear();
            _restoredFightingStyleOrigins.Clear();
            _restoredInvocationOrigins.Clear();
            _restoredUnconditionalFeatures.Clear();
            _behaviorCarriers.Clear();

            foreach (var unsupportedFeature in CopiedFeatures
                         .Where(x => x != null && !IsSupportedForSnapshotProfile(x)))
            {
                duplicate.ActiveFeatures.Remove(unsupportedFeature);
                duplicate.FeaturesOrigin.Remove(unsupportedFeature);
            }

            for (var index = 0; index < CopiedFeatures.Count; index++)
            {
                var feature = CopiedFeatures[index];

                if (!IsSupportedForSnapshotProfile(feature))
                {
                    continue;
                }

                if (feature is FeatureDefinitionFeatureSet)
                {
                    _behaviorCarriers.Add(feature);

                    continue;
                }

                duplicate.ActiveFeatures.TryAdd(feature);
                var origin = new FeatureOrigin(
                    (FeatureSourceType)CopiedFeatureSourceTypes[index],
                    CopiedFeatureSourceNames[index],
                    ResolveFeatureSourceDefinition(index, feature),
                    feature.ParseSpecialFeatureTags());

                duplicate.FeaturesOrigin[feature] = origin;
                _restoredFeatureOrigins[feature] = origin;
                TrackFeatureActivationOrigin(duplicate, feature, origin);
            }

            // A Hero publishes only the leaves of fighting styles whose equipment
            // trigger happened to be true at refresh time. The snapshot must retain
            // every trained style and evaluate that parent trigger against the
            // duplicate's current equipment, otherwise Defense remains active while
            // naked and styles equipped later can never become active.
            foreach (var fightingStyle in EnumerateTrainedFightingStyles())
            {
                var origin = new FeatureOrigin(
                    FeatureSourceType.FightingStyle,
                    fightingStyle.Name,
                    fightingStyle,
                    null);

                foreach (var candidate in fightingStyle.Features
                             .Where(feature => feature != null)
                             .SelectMany(feature => EnumerateFeatureGraph(feature, origin)))
                {
                    var feature = candidate.Feature;

                    if (!IsSupportedForSnapshotProfile(feature))
                    {
                        continue;
                    }

                    if (feature is FeatureDefinitionFeatureSet)
                    {
                        _behaviorCarriers.Add(feature);

                        continue;
                    }

                    duplicate.ActiveFeatures.TryAdd(feature);
                    duplicate.FeaturesOrigin.TryAdd(feature, origin);
                    _restoredFeatureOrigins.TryAdd(feature, origin);
                    // Native feature browsing can publish a fighting-style leaf with its
                    // class origin, so the serialized origin alone cannot prove that the
                    // leaf is unconditional. The trained-style graph is authoritative for
                    // these leaves and must own their equipment trigger.
                    _restoredUnconditionalFeatures.Remove(feature);
                    TrackFeatureActivationOrigin(duplicate, feature, origin);
                }
            }
        }

        private bool IsSupportedForSnapshotProfile(FeatureDefinition feature)
        {
            return IsSupportedCopiedFeature(feature) &&
                   (CanEquipHumanoidItems ||
                    feature is not FeatureDefinitionAttributeModifier);
        }

        private static void AddMissingMonsterSnapshotAttributes(
            RulesetCharacter source,
            IDictionary<string, PersistentAttributeSeed> attributes)
        {
            if (source is not RulesetCharacterMonster monster)
            {
                return;
            }

            var monsterDefinition = monster.MonsterDefinition;
            var challengeRating = Math.Max(1, (int)Math.Ceiling(
                monsterDefinition?.ChallengeRating ?? 0));
            var attacksNumber = monster.AttackModes
                .Where(mode => mode?.ActionType == ActionType.Main)
                .Select(mode => Math.Max(1, mode.AttacksNumber))
                .DefaultIfEmpty(1)
                .Max();

            AddMissingMonsterSnapshotAttribute(
                attributes,
                AttributeDefinitions.CharacterLevel,
                Math.Max(1, monsterDefinition?.HitDice ?? 1),
                1);
            AddMissingMonsterSnapshotAttribute(
                attributes,
                AttributeDefinitions.ProficiencyBonus,
                Math.Min(9, 2 + (challengeRating - 1) / 4),
                0);
            AddMissingMonsterSnapshotAttribute(
                attributes,
                AttributeDefinitions.AttacksNumber,
                attacksNumber,
                1);
        }

        private static void AddMissingMonsterSnapshotAttribute(
            IDictionary<string, PersistentAttributeSeed> attributes,
            string attributeName,
            int value,
            int minValue)
        {
            if (attributes.ContainsKey(attributeName))
            {
                return;
            }

            var attributeDefinition = DatabaseRepository.GetDatabase<SmartAttributeDefinition>()
                .GetElement(attributeName);

            if (attributeDefinition == null)
            {
                return;
            }

            attributes.Add(
                attributeName,
                new PersistentAttributeSeed(
                    attributeName,
                    attributeDefinition,
                    value,
                    minValue,
                    1000,
                    value));
        }

        private void EnsureBardicInspirationRecoveryFeature()
        {
            var recoveryFeature =
                DatabaseRepository.GetDatabase<FeatureDefinitionCampAffinity>()
                    .GetElement(BardicInspirationRecoveryFeatureName, true);

            if (recoveryFeature == null ||
                CopiedFeatures.Contains(recoveryFeature) ||
                CopiedFeatures.Count >= MaximumFeatureCount ||
                !PowerDefinitionNames.Contains(
                    FeatureDefinitionPowers.PowerBardGiveBardicInspiration.Name))
            {
                return;
            }

            var bardIndex = ClassDefinitions.FindIndex(definition =>
                definition == CharacterClassDefinitions.Bard);

            if (bardIndex < 0 ||
                GetValue(ClassLevels, bardIndex, 0) < 5)
            {
                return;
            }

            AddCopiedFeature(
                recoveryFeature,
                new FeatureOrigin(
                    FeatureSourceType.CharacterFeature,
                    recoveryFeature.Name,
                    recoveryFeature,
                    recoveryFeature.ParseSpecialFeatureTags()));
        }

        private void TrackFeatureActivationOrigin(
            RulesetCharacterSimulacrum duplicate,
            FeatureDefinition feature,
            FeatureOrigin origin)
        {
            var invocation = origin.source as InvocationDefinition;

            if (invocation == null &&
                origin.sourceType == FeatureSourceType.Invocation)
            {
                TryGetDefinition(origin.sourceName, out invocation);
            }

            if (origin.sourceType == FeatureSourceType.Invocation ||
                invocation != null)
            {
                // Only permanent invocations use Active as an on/off switch. Spell
                // and power invocations keep their granted activation feature
                // available independently of this flag.
                if (invocation?.IsPermanent() != true)
                {
                    if (invocation != null)
                    {
                        _restoredUnconditionalFeatures.Add(feature);
                    }
                    else
                    {
                        _restoredInvocationOrigins.TryAdd(feature, []);
                    }

                    return;
                }

                if (!_restoredInvocationOrigins.TryGetValue(
                        feature,
                        out var invocations))
                {
                    invocations = [];
                    _restoredInvocationOrigins.Add(feature, invocations);
                }

                invocations.Add(invocation);

                // A feature can be shared by more than one invocation. Treat it as
                // active when any permanent invocation that grants it is active,
                // rather than binding it to whichever origin won snapshot dedup.
                foreach (var candidate in duplicate.Invocations
                             .Select(x => x?.InvocationDefinition)
                             .Where(x =>
                                 x?.IsPermanent() == true &&
                                 x.GrantedFeature != null &&
                                 EnumerateFeatureGraph(x.GrantedFeature, default)
                                     .Any(entry => entry.Feature == feature)))
                {
                    invocations.Add(candidate);
                }

                return;
            }

            var fightingStyle = origin.source as FightingStyleDefinition;

            if (fightingStyle == null &&
                origin.sourceType == FeatureSourceType.FightingStyle)
            {
                fightingStyle = EnumerateTrainedFightingStyles().FirstOrDefault(style =>
                    string.Equals(style.Name, origin.sourceName, StringComparison.Ordinal));
            }

            if (origin.sourceType == FeatureSourceType.FightingStyle ||
                fightingStyle != null)
            {
                if (!_restoredFightingStyleOrigins.TryGetValue(
                        feature,
                        out var fightingStyles))
                {
                    fightingStyles = [];
                    _restoredFightingStyleOrigins.Add(feature, fightingStyles);
                }

                if (fightingStyle != null)
                {
                    fightingStyles.Add(fightingStyle);
                }

                return;
            }

            _restoredUnconditionalFeatures.Add(feature);
        }

        private void SynchronizeConditionalFeatures(
            RulesetCharacterSimulacrum duplicate)
        {
            foreach (var feature in _restoredFightingStyleOrigins.Keys
                         .Concat(_restoredInvocationOrigins.Keys)
                         .Distinct()
                         .ToArray())
            {
                if (IsFeatureCurrentlyActive(duplicate, feature))
                {
                    duplicate.ActiveFeatures.TryAdd(feature);
                }
                else
                {
                    duplicate.ActiveFeatures.Remove(feature);
                }
            }
        }

        private void RestorePowerFeaturesAndOrigins(
            RulesetCharacterSimulacrum duplicate)
        {
            foreach (var usablePower in duplicate.UsablePowers
                         .Where(x => x?.PowerDefinition != null &&
                                     PowerDefinitionNames.Contains(x.PowerDefinition.Name)))
            {
                var power = usablePower.PowerDefinition;

                duplicate.ActiveFeatures.TryAdd(power);
                var origin = ResolvePowerFeatureOrigin(
                    duplicate,
                    usablePower);

                duplicate.FeaturesOrigin[power] = origin;
                _restoredFeatureOrigins[power] = origin;
                TrackFeatureActivationOrigin(duplicate, power, origin);
            }
        }

        internal void RebindFeatureOrigins(
            Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
        {
            foreach (var pair in _restoredFeatureOrigins)
            {
                if (featuresOrigin.ContainsKey(pair.Key))
                {
                    featuresOrigin[pair.Key] = pair.Value;
                }
            }

            foreach (var equipmentFeature in RuntimeEquipmentFeatures.Where(feature =>
                         feature != null))
            {
                if (!_runtimeEquipmentFeatureOrigins.TryGetValue(
                        equipmentFeature,
                        out var origin))
                {
                    continue;
                }

                foreach (var candidate in EnumerateFeatureGraph(
                             equipmentFeature,
                             origin))
                {
                    if (featuresOrigin.ContainsKey(candidate.Feature))
                    {
                        featuresOrigin[candidate.Feature] = origin;
                    }
                }
            }
        }

        private void RestorePowerDefinitionsAndResources(
            RulesetCharacterSimulacrum duplicate)
        {
            // Effective use limits can depend on copied power features and their origins.
            // Restore that graph before the resource values so RestoreRemainingUses does not
            // clamp an untouched source pool to the shell's provisional maximum.
            RestorePowerFeaturesAndOrigins(duplicate);
            RestoreUsablePowers(duplicate);
        }

        internal bool IsFeatureCurrentlyActive(
            RulesetCharacterSimulacrum duplicate,
            FeatureDefinition feature)
        {
            if (_restoredUnconditionalFeatures.Contains(feature))
            {
                return true;
            }

            var hasConditionalOrigin = false;

            if (_restoredFightingStyleOrigins.TryGetValue(
                    feature,
                    out var fightingStyles))
            {
                hasConditionalOrigin = true;

                if (fightingStyles.Any(fightingStyle =>
                        SimulacrumBehavior.IsFightingStyleActive(
                            duplicate,
                            fightingStyle)))
                {
                    return true;
                }
            }

            if (_restoredInvocationOrigins.TryGetValue(
                    feature,
                    out var invocationDefinitions))
            {
                hasConditionalOrigin = true;

                if (duplicate.Invocations.Any(invocation =>
                        invocation?.Active == true &&
                        invocationDefinitions.Contains(invocation.InvocationDefinition)))
                {
                    return true;
                }
            }

            return !hasConditionalOrigin;
        }

        private static FeatureOrigin ResolvePowerFeatureOrigin(
            RulesetCharacterSimulacrum duplicate,
            RulesetUsablePower usablePower)
        {
            var power = usablePower.PowerDefinition;
            var invocation = duplicate.Invocations.FirstOrDefault(x =>
                x?.InvocationDefinition?.GrantedFeature != null &&
                EnumerateFeatureGraph(
                        x.InvocationDefinition.GrantedFeature,
                        default)
                    .Any(candidate => candidate.Feature == power));

            if (invocation?.InvocationDefinition is { } invocationDefinition)
            {
                return new FeatureOrigin(
                    FeatureSourceType.Invocation,
                    invocationDefinition.Name,
                    invocationDefinition,
                    power.ParseSpecialFeatureTags());
            }

            BaseDefinition source = usablePower.originClass;

            source ??= usablePower.originRace;
            source ??= power;

            return new FeatureOrigin(
                FeatureSourceType.ExplicitFeature,
                source.Name,
                source,
                power.ParseSpecialFeatureTags());
        }

        private void RestorePersistentAttributes(RulesetCharacterSimulacrum duplicate)
        {
            foreach (var attribute in duplicate.Attributes.Values)
            {
                attribute.RemoveModifiersByTags(AttributeDefinitions.TagFeat);
            }

            var values = new PersistentAttributeValue[PersistentAttributeNames.Count];

            for (var index = 0; index < PersistentAttributeNames.Count; index++)
            {
                var attributeName = PersistentAttributeNames[index];
                var value = attributeName == AttributeDefinitions.HitPoints
                    ? HalfMaximumHitPoints
                    : PersistentAttributeFinalValues[index];

                values[index] = new PersistentAttributeValue(
                    attributeName,
                    PersistentAttributeDefinitions[index],
                    value,
                    PersistentAttributeMinValues[index],
                    PersistentAttributeMaxValues[index]);
            }

            ApplyPersistentAttributeValues(duplicate, values);
        }

        private BaseDefinition ResolveFeatureSourceDefinition(
            int index,
            FeatureDefinition fallback)
        {
            var definitionName = CopiedFeatureSourceDefinitionNames[index];
            var definitionTypeName = CopiedFeatureSourceDefinitionTypes[index];

            if (string.IsNullOrEmpty(definitionName) ||
                string.IsNullOrEmpty(definitionTypeName) ||
                AccessTools.TypeByName(definitionTypeName) is not { } definitionType ||
                !typeof(BaseDefinition).IsAssignableFrom(definitionType))
            {
                return fallback;
            }

            try
            {
                var getDatabase = typeof(DatabaseRepository)
                    .GetMethods(System.Reflection.BindingFlags.Static |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic)
                    .Single(method =>
                        method.Name == nameof(DatabaseRepository.GetDatabase) &&
                        method.IsGenericMethodDefinition &&
                        method.GetParameters().Length == 0)
                    .MakeGenericMethod(definitionType);
                var database = getDatabase.Invoke(null, null);
                var getElement = AccessTools.Method(
                    database?.GetType(),
                    "GetElement",
                    [typeof(string)]);

                return getElement?.Invoke(database, [definitionName]) as BaseDefinition ?? fallback;
            }
            catch (Exception ex)
            {
                var key = $"{definitionTypeName}:{definitionName}";

                if (MissingFeatureOriginWarnings.Add(key))
                {
                    Trace.LogWarning(
                        $"Unable to restore Simulacrum feature origin " +
                        $"'{key}': {ex.GetType().Name}.");
                }

                return fallback;
            }
        }

        internal void RebuildAttackModes(RulesetCharacterMonster duplicate)
        {
            if (!TryValidateSnapshot(out _))
            {
                return;
            }

            ReturnAttackModes(duplicate);

            for (var i = 0; i < AttackDefinitionNames.Count; i++)
            {
                var sourceDefinition = ResolveAttackDefinition(i);
                var effectDescription = GetAttackEffectDescription(sourceDefinition);

                if (sourceDefinition == null || effectDescription == null)
                {
                    continue;
                }

                var attackMode = RulesetAttackMode.AttackModesPool.Get();

                attackMode.Clear();
                attackMode.SourceDefinition = sourceDefinition;
                attackMode.ActionType = (ActionType)GetValue(AttackActionTypes, i);
                attackMode.AbilityScore = GetValue(AttackAbilityScores, i);
                attackMode.AttacksNumber = GetValue(AttackNumbers, i, 1);
                attackMode.ToHitBonus = GetValue(AttackToHitBonuses, i);
                attackMode.CloseRange = GetValue(AttackCloseRanges, i);
                attackMode.MaxRange = GetValue(AttackMaxRanges, i);
                attackMode.ReachRange = GetValue(AttackReachRanges, i);
                attackMode.SlotName = GetValue(AttackSlotNames, i);
                attackMode.Ranged = GetValue(AttackRanged, i);
                attackMode.Reach = GetValue(AttackReach, i);
                attackMode.Thrown = GetValue(AttackThrown, i);
                attackMode.HasPriority = GetValue(AttackHasPriority, i);
                attackMode.UseVersatileDamage = GetValue(AttackUseVersatileDamage, i);
                attackMode.EffectDescription = EffectDescriptionBuilder.Create(effectDescription).Build();
                RestoreAttackTags(attackMode, i);
                RestoreDamageForms(attackMode.EffectDescription, i);

                duplicate.AttackModes.Add(attackMode);
            }

            ApplyTransientAttackModifiers(duplicate);

            if (CanEquipHumanoidItems &&
                duplicate is RulesetCharacterSimulacrum simulacrum)
            {
                AddEquipmentAttackModes(
                    simulacrum,
                    out var attackModifiers,
                    out var featuresOrigin);
                CustomWeaponsContext.AddFollowUpStrikeAttackModes(
                    simulacrum,
                    attackModifiers,
                    featuresOrigin);
            }

            if (CanEquipHumanoidItems)
            {
                ApplyExtraAttacks(duplicate);
            }

            if (CanEquipHumanoidItems &&
                duplicate is RulesetCharacterSimulacrum simulacrumAfterExtras)
            {
                CustomWeaponsContext.TryAddMainActionUnarmedAttacks(
                    simulacrumAfterExtras);
                simulacrumAfterExtras.AttackModes.RemoveAll(mode =>
                    CustomItemsContext.IsAttackModeInvalid(
                        simulacrumAfterExtras,
                        mode));
                Tabletop2024Context.ModifyNickOffHandAttack(
                    simulacrumAfterExtras,
                    simulacrumAfterExtras.AttackModes);
            }
        }

        [UsedImplicitly]
        public override void SerializeAttributes(
            IAttributesSerializer serializer,
            IVersionProvider versionProvider)
        {
            base.SerializeAttributes(serializer, versionProvider);

            try
            {
                OwningEffectGuid = serializer.SerializeAttribute("OwningEffectGuid", OwningEffectGuid);
                SnapshotSchemaVersion =
                    serializer.SerializeAttribute("SnapshotSchemaVersion", SnapshotSchemaVersion);
                CanEquipHumanoidItems =
                    serializer.SerializeAttribute("CanEquipHumanoidItems", CanEquipHumanoidItems);
                SourceDisplayName = serializer.SerializeAttribute("SourceDisplayName", SourceDisplayName);
                SourceDeityName = serializer.SerializeAttribute("SourceDeityName", SourceDeityName);
                SourceDefinitionPresentationName = serializer.SerializeAttribute(
                    "SourceDefinitionPresentationName",
                    SourceDefinitionPresentationName);
                SourceBodyAssetPrefix =
                    serializer.SerializeAttribute("SourceBodyAssetPrefix", SourceBodyAssetPrefix);
                SourceArmorAssetPrefix =
                    serializer.SerializeAttribute("SourceArmorAssetPrefix", SourceArmorAssetPrefix);
                SourceHelmetAssetPrefix =
                    serializer.SerializeAttribute("SourceHelmetAssetPrefix", SourceHelmetAssetPrefix);
                SourceFaceShapeAssetPrefix =
                    serializer.SerializeAttribute("SourceFaceShapeAssetPrefix", SourceFaceShapeAssetPrefix);
                SourceBeardShapeAssetPrefix =
                    serializer.SerializeAttribute("SourceBeardShapeAssetPrefix", SourceBeardShapeAssetPrefix);
                SourceHairShapeAssetPrefix =
                    serializer.SerializeAttribute("SourceHairShapeAssetPrefix", SourceHairShapeAssetPrefix);
                SourceHornsTailAssetPrefix =
                    serializer.SerializeAttribute("SourceHornsTailAssetPrefix", SourceHornsTailAssetPrefix);
                SourceSex = serializer.SerializeAttribute("SourceSex", SourceSex);
                CharacterLevel = serializer.SerializeAttribute("CharacterLevel", CharacterLevel);
                Strength = serializer.SerializeAttribute("Strength", Strength);
                Dexterity = serializer.SerializeAttribute("Dexterity", Dexterity);
                Constitution = serializer.SerializeAttribute("Constitution", Constitution);
                Intelligence = serializer.SerializeAttribute("Intelligence", Intelligence);
                Wisdom = serializer.SerializeAttribute("Wisdom", Wisdom);
                Charisma = serializer.SerializeAttribute("Charisma", Charisma);
                ProficiencyBonus = serializer.SerializeAttribute("ProficiencyBonus", ProficiencyBonus);
                ArmorClass = serializer.SerializeAttribute("ArmorClass", ArmorClass);
                HalfMaximumHitPoints =
                    serializer.SerializeAttribute("HalfMaximumHitPoints", HalfMaximumHitPoints);
            }
            catch (Exception ex)
            {
                Trace.LogException(new Exception(
                    "Error serializing Simulacrum snapshot attributes: " + ex.Message,
                    ex));
            }
        }

        [UsedImplicitly]
        public override void SerializeElements(
            IElementsSerializer serializer,
            IVersionProvider versionProvider)
        {
            base.SerializeElements(serializer, versionProvider);

            try
            {
                BaseDefinition.SerializeDatabaseReferenceList(
                    serializer,
                    "CopiedFeatures",
                    "FeatureDefinition",
                    CopiedFeatures);
                BaseDefinition.SerializeDatabaseReferenceList(
                    serializer,
                    "RuntimeEquipmentFeatures",
                    "FeatureDefinition",
                    RuntimeEquipmentFeatures);
                BaseDefinition.SerializeDatabaseReferenceList(
                    serializer,
                    "ClassDefinitions",
                    "CharacterClassDefinition",
                    ClassDefinitions);
                BaseDefinition.SerializeDatabaseReferenceList(
                    serializer,
                    "RepertoireAutoPreparedSpells",
                    "SpellDefinition",
                    RepertoireAutoPreparedSpells);
                BaseDefinition.SerializeDatabaseReferenceList(
                    serializer,
                    "PersistentAttributeDefinitions",
                    "SmartAttributeDefinition",
                    PersistentAttributeDefinitions);
                SourceHumanoidPresentation = BaseDefinition.SerializeDatabaseReference(
                    serializer,
                    "SourceHumanoidPresentation",
                    SourceHumanoidPresentation);
                SourceMonsterPresentation = BaseDefinition.SerializeDatabaseReference(
                    serializer,
                    "SourceMonsterPresentation",
                    SourceMonsterPresentation);

                ClassLevels = serializer.SerializeElement("ClassLevels", ClassLevels);
                KnownLanguages = serializer.SerializeElement("KnownLanguages", KnownLanguages);
                CopiedFeatureSourceTypes = serializer.SerializeElement(
                    "CopiedFeatureSourceTypes",
                    CopiedFeatureSourceTypes);
                CopiedFeatureSourceNames = serializer.SerializeElement(
                    "CopiedFeatureSourceNames",
                    CopiedFeatureSourceNames);
                CopiedFeatureSourceDefinitionNames = serializer.SerializeElement(
                    "CopiedFeatureSourceDefinitionNames",
                    CopiedFeatureSourceDefinitionNames);
                CopiedFeatureSourceDefinitionTypes = serializer.SerializeElement(
                    "CopiedFeatureSourceDefinitionTypes",
                    CopiedFeatureSourceDefinitionTypes);
                PersistentAttributeNames = serializer.SerializeElement(
                    "PersistentAttributeNames",
                    PersistentAttributeNames);
                PersistentAttributeMinValues = serializer.SerializeElement(
                    "PersistentAttributeMinValues",
                    PersistentAttributeMinValues);
                PersistentAttributeMaxValues = serializer.SerializeElement(
                    "PersistentAttributeMaxValues",
                    PersistentAttributeMaxValues);
                PersistentAttributeFinalValues = serializer.SerializeElement(
                    "PersistentAttributeFinalValues",
                    PersistentAttributeFinalValues);
                MovementModeTypes =
                    serializer.SerializeElement("MovementModeTypes", MovementModeTypes);
                MovementSpeeds = serializer.SerializeElement("MovementSpeeds", MovementSpeeds);
                PowerDefinitionNames =
                    serializer.SerializeElement("PowerDefinitionNames", PowerDefinitionNames);
                PowerSaveDcs = serializer.SerializeElement("PowerSaveDcs", PowerSaveDcs);
                PowerSpentPoints =
                    serializer.SerializeElement("PowerSpentPoints", PowerSpentPoints);
                PowerMaxUses = serializer.SerializeElement("PowerMaxUses", PowerMaxUses);
                PowerRemainingUses = serializer.SerializeElement(
                    "PowerRemainingUses",
                    PowerRemainingUses);
                PowerOriginClassNames = serializer.SerializeElement(
                    "PowerOriginClassNames",
                    PowerOriginClassNames);
                PowerOriginRaceNames = serializer.SerializeElement(
                    "PowerOriginRaceNames",
                    PowerOriginRaceNames);
                ToggledPowerNames = serializer.SerializeElement(
                    "ToggledPowerNames",
                    ToggledPowerNames);
                InvocationDefinitionNames = serializer.SerializeElement(
                    "InvocationDefinitionNames",
                    InvocationDefinitionNames);
                InvocationRepertoireIndices = serializer.SerializeElement(
                    "InvocationRepertoireIndices",
                    InvocationRepertoireIndices);
                InvocationActiveStates = serializer.SerializeElement(
                    "InvocationActiveStates",
                    InvocationActiveStates);
                InvocationUsedStates = serializer.SerializeElement(
                    "InvocationUsedStates",
                    InvocationUsedStates);
                RepertoireAutoPreparedCounts = serializer.SerializeElement(
                    "RepertoireAutoPreparedCounts",
                    RepertoireAutoPreparedCounts);
                RepertoireAutoPreparedTags = serializer.SerializeElement(
                    "RepertoireAutoPreparedTags",
                    RepertoireAutoPreparedTags);
                RepertoireFormAbilityBonuses = serializer.SerializeElement(
                    "RepertoireFormAbilityBonuses",
                    RepertoireFormAbilityBonuses);
                RepertoireMaxPreparedSpells = serializer.SerializeElement(
                    "RepertoireMaxPreparedSpells",
                    RepertoireMaxPreparedSpells);
                RepertoireMonsterNames = serializer.SerializeElement(
                    "RepertoireMonsterNames",
                    RepertoireMonsterNames);
                RepertoireSaveDcs =
                    serializer.SerializeElement("RepertoireSaveDcs", RepertoireSaveDcs);
                RepertoireSlotCapacityCounts = serializer.SerializeElement(
                    "RepertoireSlotCapacityCounts",
                    RepertoireSlotCapacityCounts);
                RepertoireSlotCapacityLevels = serializer.SerializeElement(
                    "RepertoireSlotCapacityLevels",
                    RepertoireSlotCapacityLevels);
                RepertoireSlotCapacityValues = serializer.SerializeElement(
                    "RepertoireSlotCapacityValues",
                    RepertoireSlotCapacityValues);
                RepertoireSpellAttackBonuses = serializer.SerializeElement(
                    "RepertoireSpellAttackBonuses",
                    RepertoireSpellAttackBonuses);
                RepertoireSpellCastingLevels = serializer.SerializeElement(
                    "RepertoireSpellCastingLevels",
                    RepertoireSpellCastingLevels);
                SenseTypes = serializer.SerializeElement("SenseTypes", SenseTypes);
                SenseRanges = serializer.SerializeElement("SenseRanges", SenseRanges);
                SenseStealthBreakerRanges = serializer.SerializeElement(
                    "SenseStealthBreakerRanges",
                    SenseStealthBreakerRanges);
                SourceMorphotypeCategories = serializer.SerializeElement(
                    "SourceMorphotypeCategories",
                    SourceMorphotypeCategories);
                SourceMorphotypeValues = serializer.SerializeElement(
                    "SourceMorphotypeValues",
                    SourceMorphotypeValues);
                SourceMorphotypeAdditionalCategories = serializer.SerializeElement(
                    "SourceMorphotypeAdditionalCategories",
                    SourceMorphotypeAdditionalCategories);
                SourceMorphotypeAdditionalValues = serializer.SerializeElement(
                    "SourceMorphotypeAdditionalValues",
                    SourceMorphotypeAdditionalValues);
                SkillNames = serializer.SerializeElement("SkillNames", SkillNames);
                SkillBonuses = serializer.SerializeElement("SkillBonuses", SkillBonuses);
                ArmorCategoryProficiencies = serializer.SerializeElement(
                    "ArmorCategoryProficiencies",
                    ArmorCategoryProficiencies);
                ArmorTypeProficiencies = serializer.SerializeElement(
                    "ArmorTypeProficiencies",
                    ArmorTypeProficiencies);
                WeaponCategoryProficiencies = serializer.SerializeElement(
                    "WeaponCategoryProficiencies",
                    WeaponCategoryProficiencies);
                WeaponTypeProficiencies = serializer.SerializeElement(
                    "WeaponTypeProficiencies",
                    WeaponTypeProficiencies);
                SaveAbilityNames =
                    serializer.SerializeElement("SaveAbilityNames", SaveAbilityNames);
                SaveProficiencyDeltas =
                    serializer.SerializeElement("SaveProficiencyDeltas", SaveProficiencyDeltas);
                AttackDefinitionKinds =
                    serializer.SerializeElement("AttackDefinitionKinds", AttackDefinitionKinds);
                AttackDefinitionNames =
                    serializer.SerializeElement("AttackDefinitionNames", AttackDefinitionNames);
                AttackTagCounts = serializer.SerializeElement("AttackTagCounts", AttackTagCounts);
                AttackTags = serializer.SerializeElement("AttackTags", AttackTags);
                AttackDamageFormCounts =
                    serializer.SerializeElement("AttackDamageFormCounts", AttackDamageFormCounts);
                AttackDamageTypes =
                    serializer.SerializeElement("AttackDamageTypes", AttackDamageTypes);
                AttackDamageDieTypes =
                    serializer.SerializeElement("AttackDamageDieTypes", AttackDamageDieTypes);
                AttackDamageVersatile =
                    serializer.SerializeElement("AttackDamageVersatile", AttackDamageVersatile);
                AttackDamageVersatileDieTypes = serializer.SerializeElement(
                    "AttackDamageVersatileDieTypes",
                    AttackDamageVersatileDieTypes);
                AttackDamageDiceNumbers = serializer.SerializeElement(
                    "AttackDamageDiceNumbers",
                    AttackDamageDiceNumbers);
                AttackDamageBonuses =
                    serializer.SerializeElement("AttackDamageBonuses", AttackDamageBonuses);
                AttackActionTypes = serializer.SerializeElement("AttackActionTypes", AttackActionTypes);
                AttackAbilityScores =
                    serializer.SerializeElement("AttackAbilityScores", AttackAbilityScores);
                AttackNumbers = serializer.SerializeElement("AttackNumbers", AttackNumbers);
                AttackToHitBonuses =
                    serializer.SerializeElement("AttackToHitBonuses", AttackToHitBonuses);
                AttackCloseRanges = serializer.SerializeElement("AttackCloseRanges", AttackCloseRanges);
                AttackMaxRanges = serializer.SerializeElement("AttackMaxRanges", AttackMaxRanges);
                AttackReachRanges = serializer.SerializeElement("AttackReachRanges", AttackReachRanges);
                AttackSlotNames = serializer.SerializeElement("AttackSlotNames", AttackSlotNames);
                AttackRanged = serializer.SerializeElement("AttackRanged", AttackRanged);
                AttackReach = serializer.SerializeElement("AttackReach", AttackReach);
                AttackThrown = serializer.SerializeElement("AttackThrown", AttackThrown);
                AttackHasPriority = serializer.SerializeElement("AttackHasPriority", AttackHasPriority);
                AttackUseVersatileDamage =
                    serializer.SerializeElement("AttackUseVersatileDamage", AttackUseVersatileDamage);

                // Schema 9 and older streams end here. Decode schema 10's subclass map before
                // rejecting it through IsCurrentSchema, without shifting the enclosing condition.
                if (SnapshotSchemaVersion >= 10 &&
                    SnapshotSchemaVersion <= CurrentSnapshotSchemaVersion)
                {
                    ClassSubclassNames = serializer.SerializeElement<string, string>(
                        "ClassSubclassNames",
                        ClassSubclassNames);
                }

                // Decode the identity tail from every schema that carries it before
                // IsCurrentSchema rejects an older snapshot, preserving stream alignment.
                if (SnapshotSchemaVersion >= 11 &&
                    SnapshotSchemaVersion <= CurrentSnapshotSchemaVersion)
                {
                    TrainedFeatNames =
                        serializer.SerializeElement("TrainedFeatNames", TrainedFeatNames);
                    TrainedFightingStyleNames = serializer.SerializeElement(
                        "TrainedFightingStyleNames",
                        TrainedFightingStyleNames);
                    TrainedMetamagicOptionNames = serializer.SerializeElement(
                        "TrainedMetamagicOptionNames",
                        TrainedMetamagicOptionNames);
                }

                if (SnapshotSchemaVersion >= 14 &&
                    SnapshotSchemaVersion <= CurrentSnapshotSchemaVersion)
                {
                    SkillProficiencyRanks = serializer.SerializeElement(
                        "SkillProficiencyRanks",
                        SkillProficiencyRanks);
                }

                if (serializer.Mode == Serializer.SerializationMode.Read)
                {
                    CopiedFeatures.RemoveAll(x => x == null);
                    RuntimeEquipmentFeatures.RemoveAll(x => x == null);
                    RepertoireAutoPreparedSpells.RemoveAll(x => x == null);
                    ClassSubclassNames ??= [];
                    TrainedFeatNames ??= [];
                    TrainedFightingStyleNames ??= [];
                    TrainedMetamagicOptionNames ??= [];
                    SkillProficiencyRanks ??= [];
                }
            }
            catch (Exception ex)
            {
                Trace.LogException(new Exception(
                    "Error serializing Simulacrum snapshot elements: " + ex.Message,
                    ex));
            }
        }

        protected override void ClearCustomStates()
        {
            SnapshotSchemaVersion = 0;
            CanEquipHumanoidItems = false;
            OwningEffectGuid = 0;
            SourceDisplayName = string.Empty;
            SourceDeityName = string.Empty;
            SourceDefinitionPresentationName = string.Empty;
            SourceBodyAssetPrefix = string.Empty;
            SourceArmorAssetPrefix = string.Empty;
            SourceHelmetAssetPrefix = string.Empty;
            SourceFaceShapeAssetPrefix = string.Empty;
            SourceBeardShapeAssetPrefix = string.Empty;
            SourceHairShapeAssetPrefix = string.Empty;
            SourceHornsTailAssetPrefix = string.Empty;
            SourceSex = 0;
            SourceHumanoidPresentation = null;
            SourceMonsterPresentation = null;
            SourceMorphotypeCategories.Clear();
            SourceMorphotypeValues.Clear();
            SourceMorphotypeAdditionalCategories.Clear();
            SourceMorphotypeAdditionalValues.Clear();
            CharacterLevel = 0;
            Strength = 0;
            Dexterity = 0;
            Constitution = 0;
            Intelligence = 0;
            Wisdom = 0;
            Charisma = 0;
            ProficiencyBonus = 0;
            ArmorClass = 0;
            HalfMaximumHitPoints = 0;
            ClassDefinitions.Clear();
            ClassLevels.Clear();
            ClassSubclassNames.Clear();
            KnownLanguages.Clear();
            TrainedFeatNames.Clear();
            TrainedFightingStyleNames.Clear();
            TrainedMetamagicOptionNames.Clear();
            CopiedFeatures.Clear();
            CopiedFeatureSourceTypes.Clear();
            CopiedFeatureSourceNames.Clear();
            CopiedFeatureSourceDefinitionNames.Clear();
            CopiedFeatureSourceDefinitionTypes.Clear();
            PersistentAttributeDefinitions.Clear();
            PersistentAttributeNames.Clear();
            PersistentAttributeMinValues.Clear();
            PersistentAttributeMaxValues.Clear();
            PersistentAttributeFinalValues.Clear();
            MovementModeTypes.Clear();
            MovementSpeeds.Clear();
            PowerDefinitionNames.Clear();
            PowerSaveDcs.Clear();
            PowerSpentPoints.Clear();
            PowerMaxUses.Clear();
            PowerRemainingUses.Clear();
            PowerOriginClassNames.Clear();
            PowerOriginRaceNames.Clear();
            ToggledPowerNames.Clear();
            InvocationDefinitionNames.Clear();
            InvocationRepertoireIndices.Clear();
            InvocationActiveStates.Clear();
            InvocationUsedStates.Clear();
            RepertoireAutoPreparedCounts.Clear();
            RepertoireAutoPreparedSpells.Clear();
            RepertoireAutoPreparedTags.Clear();
            RepertoireFormAbilityBonuses.Clear();
            RepertoireMaxPreparedSpells.Clear();
            RepertoireMonsterNames.Clear();
            RepertoireSaveDcs.Clear();
            RepertoireSlotCapacityCounts.Clear();
            RepertoireSlotCapacityLevels.Clear();
            RepertoireSlotCapacityValues.Clear();
            RepertoireSpellAttackBonuses.Clear();
            RepertoireSpellCastingLevels.Clear();
            SenseTypes.Clear();
            SenseRanges.Clear();
            SenseStealthBreakerRanges.Clear();
            SkillNames.Clear();
            SkillBonuses.Clear();
            SkillProficiencyRanks.Clear();
            ArmorCategoryProficiencies.Clear();
            ArmorTypeProficiencies.Clear();
            WeaponCategoryProficiencies.Clear();
            WeaponTypeProficiencies.Clear();
            RuntimeEquipmentFeatures.Clear();
            _restoredFeatureOrigins.Clear();
            _restoredFightingStyleOrigins.Clear();
            _restoredInvocationOrigins.Clear();
            _restoredUnconditionalFeatures.Clear();
            _runtimeEquipmentFeatureOrigins.Clear();
            SaveAbilityNames.Clear();
            SaveProficiencyDeltas.Clear();
            AttackDefinitionKinds.Clear();
            AttackDefinitionNames.Clear();
            AttackTagCounts.Clear();
            AttackTags.Clear();
            AttackDamageFormCounts.Clear();
            AttackDamageTypes.Clear();
            AttackDamageDieTypes.Clear();
            AttackDamageVersatile.Clear();
            AttackDamageVersatileDieTypes.Clear();
            AttackDamageDiceNumbers.Clear();
            AttackDamageBonuses.Clear();
            AttackActionTypes.Clear();
            AttackAbilityScores.Clear();
            AttackNumbers.Clear();
            AttackToHitBonuses.Clear();
            AttackCloseRanges.Clear();
            AttackMaxRanges.Clear();
            AttackReachRanges.Clear();
            AttackSlotNames.Clear();
            AttackRanged.Clear();
            AttackReach.Clear();
            AttackThrown.Clear();
            AttackHasPriority.Clear();
            AttackUseVersatileDamage.Clear();
        }

        private void CapturePersistentAttributes(
            IReadOnlyDictionary<string, PersistentAttributeSeed> attributes)
        {
            PersistentAttributeDefinitions.Clear();
            PersistentAttributeNames.Clear();
            PersistentAttributeMinValues.Clear();
            PersistentAttributeMaxValues.Clear();
            PersistentAttributeFinalValues.Clear();

            foreach (var attribute in attributes.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                PersistentAttributeDefinitions.Add(attribute.Definition);
                PersistentAttributeNames.Add(attribute.Name);
                PersistentAttributeMinValues.Add(attribute.MinValue);
                PersistentAttributeMaxValues.Add(attribute.MaxValue);
                PersistentAttributeFinalValues.Add(attribute.FinalValue);
            }
        }

        private void CaptureClasses(IReadOnlyList<ClassLevelSeed> classes)
        {
            ClassDefinitions.Clear();
            ClassLevels.Clear();

            foreach (var classLevel in classes)
            {
                ClassDefinitions.Add(classLevel.ClassDefinition);
                ClassLevels.Add(classLevel.Level);
            }
        }

        private void CaptureSubclasses(
            IReadOnlyDictionary<CharacterClassDefinition, CharacterSubclassDefinition> subclasses)
        {
            ClassSubclassNames.Clear();

            foreach (var pair in subclasses
                         .Where(pair => pair.Key != null && pair.Value != null)
                         .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
            {
                ClassSubclassNames[pair.Key.Name] = pair.Value.Name;
            }
        }

        private void CaptureKnownLanguages(IReadOnlyList<string> knownLanguages)
        {
            KnownLanguages.Clear();
            KnownLanguages.AddRange(knownLanguages);
        }

        private void CaptureTrainedIdentities(SimulacrumSnapshotSeed seed)
        {
            TrainedFeatNames.Clear();
            TrainedFeatNames.AddRange(seed.TrainedFeatNames);
            TrainedFightingStyleNames.Clear();
            TrainedFightingStyleNames.AddRange(seed.TrainedFightingStyleNames);
            TrainedMetamagicOptionNames.Clear();
            TrainedMetamagicOptionNames.AddRange(seed.TrainedMetamagicOptionNames);
        }

        private static IReadOnlyList<ClassLevelSeed> CollectClassLevels(RulesetCharacter source)
        {
            return source is RulesetCharacterHero hero
                ? hero.ClassesAndLevels
                    .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal)
                    .Select(pair => new ClassLevelSeed(pair.Key, pair.Value))
                    .ToArray()
                : [];
        }

        private static IReadOnlyDictionary<CharacterClassDefinition, CharacterSubclassDefinition>
            CollectSubclasses(RulesetCharacter source)
        {
            return source is RulesetCharacterHero hero
                ? hero.ClassesAndSubclasses
                    .Where(pair => pair.Key != null && pair.Value != null)
                    .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<CharacterClassDefinition, CharacterSubclassDefinition>();
        }

        private static IReadOnlyList<string> CollectKnownLanguages(RulesetCharacter source)
        {
            var languages = new List<string>();

            source.EnumerateKnownLanguages(languages);

            return languages
                .Where(language => !string.IsNullOrEmpty(language))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(language => language, StringComparer.Ordinal)
                .ToArray();
        }

        private void CaptureEquipmentEligibility(EquipmentEligibilitySeed seed)
        {
            CanEquipHumanoidItems = seed.CanEquipHumanoidItems;
            ArmorCategoryProficiencies.Clear();
            ArmorTypeProficiencies.Clear();
            WeaponCategoryProficiencies.Clear();
            WeaponTypeProficiencies.Clear();
            ArmorCategoryProficiencies.AddRange(seed.ArmorCategories);
            ArmorTypeProficiencies.AddRange(seed.ArmorTypes);
            WeaponCategoryProficiencies.AddRange(seed.WeaponCategories);
            WeaponTypeProficiencies.AddRange(seed.WeaponTypes);
        }

        private static EquipmentEligibilitySeed CollectEquipmentEligibility(RulesetCharacter source)
        {
            if (source is not RulesetCharacterHero hero)
            {
                return new EquipmentEligibilitySeed(false, [], [], [], []);
            }

            return new EquipmentEligibilitySeed(
                true,
                hero.ArmorCategoryProficiencies.Distinct().OrderBy(x => x).ToArray(),
                hero.ArmorTypeProficiencies.Distinct().OrderBy(x => x).ToArray(),
                hero.WeaponCategoryProficiencies.Distinct().OrderBy(x => x).ToArray(),
                hero.WeaponTypeProficiencies.Distinct().OrderBy(x => x).ToArray());
        }

        private void PrepareEquipmentFeatures(RulesetCharacterSimulacrum duplicate)
        {
            duplicate.RemoveAllConditionsOfCategory(AttributeDefinitions.TagEquipment, false);

            foreach (var feature in RuntimeEquipmentFeatures.Where(x => x != null))
            {
                duplicate.ActiveFeatures.Remove(feature);
                duplicate.FeaturesOrigin.Remove(feature);
            }

            RuntimeEquipmentFeatures.Clear();
            _runtimeEquipmentFeatureOrigins.Clear();

            if (!CanEquipHumanoidItems)
            {
                return;
            }

            foreach (var item in EnumerateActiveEquipment(duplicate)
                         .Select(x => x.Item)
                         .Distinct()
                         .Where(CanUseItemProperties))
            {
                var ignoreStealthDisadvantage =
                    ArmorFeats.IsMediumArmorMasterStealthContextValid(
                        item.ItemDefinition,
                        duplicate);

                foreach (var property in item.ItemDefinition.StaticProperties.Where(x =>
                             IsStaticItemPropertyAvailable(item, x) &&
                             (!ignoreStealthDisadvantage ||
                              x.FeatureDefinition !=
                              FeatureDefinitionAbilityCheckAffinitys
                                  .AbilityCheckAffinityStealthDisadvantage)))
                {
                    switch (property.Type)
                    {
                        case ItemPropertyDescription.PropertyType.Feature:
                            AddRuntimeEquipmentFeature(
                                duplicate,
                                item,
                                property.FeatureDefinition);
                            break;
                        case ItemPropertyDescription.PropertyType.Condition:
                            AddRuntimeEquipmentCondition(
                                duplicate,
                                property.ConditionDefinition);
                            break;
                    }
                }

                foreach (var property in item.DynamicItemProperties)
                {
                    var feature = property?.FeatureDefinition;

                    // Item-scoped attack features and dynamic AC are consumed from the
                    // RulesetItem by the same paths as a hero. Publishing them globally would
                    // affect every weapon and make the source item count them a second time.
                    if (IsItemScopedAttackFeature(feature) ||
                        feature is FeatureDefinitionAttributeModifier
                        {
                            ModifiedAttribute: AttributeDefinitions.ArmorClass
                        })
                    {
                        continue;
                    }

                    AddRuntimeEquipmentFeature(duplicate, item, feature);
                }
            }
        }

        private static bool IsItemScopedAttackFeature(FeatureDefinition feature)
        {
            return feature is IAttackModificationProvider or IAdditionalDamageProvider ||
                   feature?.GetAllSubFeaturesOfType<IModifyWeaponAttackMode>().Any() == true;
        }

        private void AddRuntimeEquipmentFeature(
            RulesetCharacterSimulacrum duplicate,
            RulesetItem item,
            FeatureDefinition feature)
        {
            if (feature == null || duplicate.ActiveFeatures.Contains(feature))
            {
                return;
            }

            duplicate.ActiveFeatures.Add(feature);
            var origin = new FeatureOrigin(
                FeatureSourceType.Equipment,
                item.ItemDefinition.Name,
                item.ItemDefinition,
                feature.ParseSpecialFeatureTags());
            duplicate.FeaturesOrigin[feature] = origin;
            RuntimeEquipmentFeatures.Add(feature);
            _runtimeEquipmentFeatureOrigins[feature] = origin;

            if (feature is FeatureDefinitionConditionAffinity
                {
                    ConditionAffinityType: ConditionAffinityType.Immunity
                } conditionAffinity)
            {
                duplicate.HandleConditionImmunity([conditionAffinity]);
            }
        }

        private static void AddRuntimeEquipmentCondition(
            RulesetCharacterSimulacrum duplicate,
            ConditionDefinition condition)
        {
            if (condition == null ||
                (condition.SpecialInterruptions.Contains(ConditionInterruption.Damaged) &&
                 duplicate.LastReceivedDamageTypes.Count > 0))
            {
                return;
            }

            duplicate.AllCancellingConditions.Clear();
            condition.CollectCancellingConditions(duplicate.AllCancellingConditions);

            if (duplicate.AllCancellingConditions.Any(duplicate.HasConditionOfType))
            {
                return;
            }

            var activeCondition = RulesetCondition.CreateCondition(
                duplicate.Guid,
                condition,
                1,
                0,
                0,
                0);

            duplicate.AddConditionOfCategory(
                AttributeDefinitions.TagEquipment,
                activeCondition,
                false,
                true);
        }

        private static bool CanUseItemProperties(RulesetItem item)
        {
            return item?.ItemDefinition != null &&
                   !item.ItemDefinition.RequiresAttunement;
        }

        private static bool IsStaticItemPropertyActive(
            RulesetItem item,
            ItemPropertyDescription property)
        {
            return IsStaticItemPropertyAvailable(item, property) &&
                   property is
                   {
                       Type: ItemPropertyDescription.PropertyType.Feature,
                       FeatureDefinition: not null
                   };
        }

        private static bool IsStaticItemPropertyAvailable(
            RulesetItem item,
            ItemPropertyDescription property)
        {
            return property is { AppliesOnItemOnly: false } &&
                   (!item.ItemDefinition.RequiresIdentification ||
                    property.KnowledgeAffinity !=
                    EquipmentDefinitions.KnowledgeAffinity.InactiveAndHidden ||
                    item.Identified);
        }

        private void ApplyEquipmentAttributeModifiers(
            RulesetCharacterSimulacrum duplicate)
        {
            foreach (var attribute in duplicate.Attributes.Values)
            {
                attribute.RemoveModifiersByTags(AttributeDefinitions.TagEquipment);
                attribute.Refresh();
            }

            if (!CanEquipHumanoidItems)
            {
                return;
            }

            foreach (var item in EnumerateActiveEquipment(duplicate)
                         .Select(entry => entry.Item)
                         .Distinct()
                         .Where(CanUseItemProperties))
            {
                foreach (var attributeModifier in item.ItemDefinition.StaticProperties
                             .Where(property => IsStaticItemPropertyActive(item, property))
                             .Select(property => property.FeatureDefinition)
                             .OfType<FeatureDefinitionAttributeModifier>()
                             .Where(modifier =>
                                 modifier.ModifiedAttribute !=
                                 AttributeDefinitions.ArmorClass))
                {
                    ApplyEquipmentAttributeModifier(duplicate, attributeModifier);
                }
            }

            foreach (var attribute in duplicate.Attributes.Values)
            {
                RulesetAttributeModifier.SortAttributeModifiersList(
                    attribute.ActiveModifiers);
            }
        }

        private static void ApplyEquipmentAttributeModifier(
            RulesetCharacterSimulacrum duplicate,
            FeatureDefinitionAttributeModifier definition)
        {
            var modifierValue = definition.ModifierValue;

            switch (definition.ModifierOperation)
            {
                case FeatureDefinitionAttributeModifier.AttributeModifierOperation
                    .AddAbilityScoreBonus:
                    modifierValue = AttributeDefinitions.ComputeAbilityScoreModifier(
                        duplicate.TryGetAttributeValue(definition.ModifierAbilityScore));

                    if (definition.Minimum1 && modifierValue < 1)
                    {
                        modifierValue = 1;
                    }

                    break;
                case FeatureDefinitionAttributeModifier.AttributeModifierOperation
                    .AddProficiencyBonus:
                    modifierValue = GetEquipmentProficiencyBonus(
                        duplicate,
                        definition.UseBonusFromCaster);
                    break;
                case FeatureDefinitionAttributeModifier.AttributeModifierOperation
                    .AddHalfProficiencyBonus:
                    var proficiencyBonus = GetEquipmentProficiencyBonus(
                        duplicate,
                        definition.UseBonusFromCaster);
                    modifierValue = (proficiencyBonus + 1) / 2;
                    break;
            }

            var targetAttributes =
                definition.ModifiedAttribute == AttributeDefinitions.AllAbilityScores
                    ? AttributeDefinitions.AbilityScoreNames
                    : [definition.ModifiedAttribute];

            foreach (var targetAttribute in targetAttributes)
            {
                if (string.IsNullOrEmpty(targetAttribute) ||
                    !duplicate.TryGetAttribute(targetAttribute, out var attribute))
                {
                    continue;
                }

                attribute.AddModifier(
                    RulesetAttributeModifier.BuildAttributeModifier(
                        definition.ModifierOperation,
                        modifierValue,
                        AttributeDefinitions.TagEquipment,
                        string.Empty));
            }
        }

        private static int GetEquipmentProficiencyBonus(
            RulesetCharacterSimulacrum duplicate,
            bool useBonusFromCaster)
        {
            RulesetCharacter provider = duplicate;

            if (useBonusFromCaster &&
                ServiceRepository.GetService<IGameService>()?
                    .TryFindControllerFromKindredSpirit(duplicate, out provider) !=
                TryFindControllerFromKindredSpiritErrorType.Success)
            {
                return 0;
            }

            return provider?.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus) ?? 0;
        }

        private void ApplyCreatureArmorClass(RulesetCharacterSimulacrum duplicate)
        {
            if (!duplicate.TryGetAttribute(
                    AttributeDefinitions.ArmorClass,
                    out var armorClassAttribute))
            {
                return;
            }

            armorClassAttribute.ActiveModifiers.Clear();
            armorClassAttribute.ValueTrends.Clear();
            armorClassAttribute.BaseValue = ArmorClass;
            armorClassAttribute.Refresh();

            if (ServiceRepository.GetService<IRulesetImplementationService>() is
                { } implementationService)
            {
                ApplyConditionArmorClassFeatures(
                    duplicate,
                    implementationService,
                    armorClassAttribute,
                    CollectSummoningBaselineConditions(duplicate));
            }

            RulesetAttributeModifier.SortAttributeModifiersList(
                armorClassAttribute.ActiveModifiers);
            armorClassAttribute.Refresh(true);
            duplicate.SortArmorClassModifierTrends(armorClassAttribute);
            armorClassAttribute.Refresh();
        }

        private void ApplyEquipmentArmorClass(RulesetCharacterSimulacrum duplicate)
        {
            if (!duplicate.TryGetAttribute(
                    AttributeDefinitions.ArmorClass,
                    out var armorClassAttribute))
            {
                return;
            }

            var implementationService =
                ServiceRepository.GetService<IRulesetImplementationService>();
            var contextParams = new RulesetImplementationDefinitions.SituationalContextParams(
                SituationalContext.None,
                duplicate,
                null,
                0,
                null,
                false,
                null);
            var armorItems = EnumerateActiveEquipment(duplicate)
                .Select(entry => entry.Item)
                .Distinct()
                .Where(item => item?.ItemDefinition is { IsArmor: true })
                .Select(item => (Item: item, Armor: item.ItemDefinition.ArmorDescription))
                .Where(entry => entry.Armor != null)
                .ToArray();
            var baseArmor = armorItems
                .Where(entry => entry.Armor.IsBaseArmorClass)
                .OrderByDescending(entry => entry.Armor.ArmorClassValue)
                .FirstOrDefault();
            var baseArmorClass = baseArmor.Item == null
                ? 10
                : baseArmor.Armor.ArmorClassValue +
                  (CanUseItemProperties(baseArmor.Item)
                      ? baseArmor.Item.ComputeACEnhancement(contextParams)
                      : 0);
            var baseModifier = RulesetAttributeModifier.BuildAttributeModifier(
                FeatureDefinitionAttributeModifier.AttributeModifierOperation.Set,
                baseArmorClass,
                AttributeDefinitions.TagEquipment,
                string.Empty);

            armorClassAttribute.ActiveModifiers.Clear();
            armorClassAttribute.ValueTrends.Clear();
            armorClassAttribute.BaseValue = 0;
            armorClassAttribute.AddModifier(baseModifier);
            armorClassAttribute.ValueTrends.Add(new TrendInfo(
                baseArmorClass,
                baseArmor.Item == null ? FeatureSourceType.Base : FeatureSourceType.Equipment,
                baseArmor.Item?.ItemDefinition.Name ?? string.Empty,
                baseArmor.Item?.ItemDefinition,
                baseModifier)
            {
                additive = false
            });

            var dexterityBonus = AttributeDefinitions.ComputeAbilityScoreModifier(
                duplicate.TryGetAttributeValue(AttributeDefinitions.Dexterity));
            var maximumDexterityBonus = baseArmor.Item == null
                ? -1
                : ArmorFeats.IsMediumArmorMasterMaxDexContextValid(
                    baseArmor.Armor,
                    duplicate)
                    ? 3
                    : baseArmor.Armor.MaxDexterityBonus;

            if (maximumDexterityBonus >= 0)
            {
                dexterityBonus = Math.Min(dexterityBonus, maximumDexterityBonus);
            }

            if (armorItems.Any(entry =>
                    entry.Armor.ArmorTypeDefinition?.ArmorCategoryDefinition
                        ?.ForbidsNegativeDexterityBonus == true))
            {
                dexterityBonus = Math.Max(0, dexterityBonus);
            }

            var dexterityModifier = RulesetAttributeModifier.BuildAttributeModifier(
                FeatureDefinitionAttributeModifier.AttributeModifierOperation
                    .AddAbilityScoreBonus,
                dexterityBonus,
                AttributeDefinitions.TagAbilityScore,
                AttributeDefinitions.Dexterity);

            armorClassAttribute.AddModifier(dexterityModifier);
            armorClassAttribute.ValueTrends.Add(new TrendInfo(
                dexterityBonus,
                FeatureSourceType.AbilityScore,
                AttributeDefinitions.Dexterity,
                duplicate,
                dexterityModifier));

            var additiveArmorBonuses = armorItems
                .Where(entry => !entry.Armor.IsBaseArmorClass)
                .Select(entry => (
                    entry.Item,
                    Value: entry.Armor.ArmorClassValue +
                           (CanUseItemProperties(entry.Item)
                               ? entry.Item.ComputeACEnhancement(contextParams)
                               : 0)))
                .Where(entry => entry.Value != 0)
                .ToArray();
            var additiveArmorClass = additiveArmorBonuses.Sum(entry => entry.Value);

            if (additiveArmorClass != 0)
            {
                var additiveModifier = RulesetAttributeModifier.BuildAttributeModifier(
                    FeatureDefinitionAttributeModifier.AttributeModifierOperation.Additive,
                    additiveArmorClass,
                    AttributeDefinitions.TagEquipment,
                    string.Empty);

                armorClassAttribute.AddModifier(additiveModifier);

                foreach (var (item, value) in additiveArmorBonuses)
                {
                    armorClassAttribute.ValueTrends.Add(new TrendInfo(
                        value,
                        FeatureSourceType.Equipment,
                        item.ItemDefinition.Name,
                        item.ItemDefinition,
                        additiveModifier));
                }
            }

            ApplyOwnedArmorClassFeatures(
                duplicate,
                implementationService,
                armorClassAttribute);

            foreach (var item in armorItems
                         .Select(entry => entry.Item)
                         .Where(CanUseItemProperties))
            {
                var features = item.ItemDefinition.StaticProperties
                    .Where(property => IsStaticItemPropertyActive(item, property))
                    .Select(property => property.FeatureDefinition)
                    .SelectMany(feature => EnumerateFeatureGraph(feature, default))
                    .Select(candidate => candidate.Feature)
                    .OfType<FeatureDefinitionAttributeModifier>()
                    .Where(modifier =>
                        modifier.ModifiedAttribute == AttributeDefinitions.ArmorClass &&
                        modifier.ModifierOperation ==
                        FeatureDefinitionAttributeModifier.AttributeModifierOperation.Additive)
                    .Cast<FeatureDefinition>()
                    .ToList();

                if (features.Count == 0)
                {
                    continue;
                }

                duplicate.RefreshArmorClassInFeatures(
                    implementationService,
                    armorClassAttribute,
                    features,
                    AttributeDefinitions.TagEquipment,
                    FeatureSourceType.Equipment,
                    item.ItemDefinition.Name);
            }

            ApplyConditionArmorClassFeatures(
                duplicate,
                implementationService,
                armorClassAttribute);

            RulesetAttributeModifier.SortAttributeModifiersList(
                armorClassAttribute.ActiveModifiers);
            armorClassAttribute.Refresh(true);
            duplicate.SortArmorClassModifierTrends(armorClassAttribute);
            armorClassAttribute.Refresh();

            foreach (var modifier in duplicate.GetSubFeaturesByType<IModifyAC>().Distinct())
            {
                modifier.ModifyAC(
                    duplicate,
                    false,
                    false,
                    null,
                    armorClassAttribute);
            }

            RulesetAttributeModifier.SortAttributeModifiersList(
                armorClassAttribute.ActiveModifiers);
            armorClassAttribute.Refresh(true);
            duplicate.SortArmorClassModifierTrends(armorClassAttribute);
            armorClassAttribute.Refresh();
        }

        private void ApplyOwnedArmorClassFeatures(
            RulesetCharacterSimulacrum duplicate,
            IRulesetImplementationService implementationService,
            RulesetAttribute armorClassAttribute)
        {
            var applied = new HashSet<FeatureDefinition>();

            foreach (var root in duplicate.ActiveFeatures.Where(feature =>
                         feature != null &&
                         !RuntimeEquipmentFeatures.Contains(feature) &&
                         IsFeatureCurrentlyActive(duplicate, feature)))
            {
                var rootOrigin = duplicate.FeaturesOrigin.TryGetValue(root, out var origin)
                    ? origin
                    : CreateFallbackFeatureOrigin(duplicate, root);

                foreach (var candidate in EnumerateFeatureGraph(root, rootOrigin))
                {
                    if (candidate.Feature is not FeatureDefinitionAttributeModifier
                        {
                            ModifiedAttribute: AttributeDefinitions.ArmorClass
                        } armorClassFeature ||
                        !applied.Add(armorClassFeature))
                    {
                        continue;
                    }

                    var featureOrigin = duplicate.FeaturesOrigin.TryGetValue(
                        armorClassFeature,
                        out var specificOrigin)
                        ? specificOrigin
                        : candidate.Origin;

                    duplicate.RefreshArmorClassInFeatures(
                        implementationService,
                        armorClassAttribute,
                        [armorClassFeature],
                        GetArmorClassModifierTag(featureOrigin),
                        featureOrigin.sourceType,
                        featureOrigin.sourceName);
                }
            }
        }

        private static void ApplyConditionArmorClassFeatures(
            RulesetCharacterSimulacrum duplicate,
            IRulesetImplementationService implementationService,
            RulesetAttribute armorClassAttribute,
            IReadOnlyCollection<RulesetCondition> conditionsToSkip = null)
        {
            foreach (var pair in duplicate.ConditionsByCategory)
            {
                foreach (var condition in pair.Value.Where(condition =>
                             condition?.ConditionDefinition != null &&
                             !(conditionsToSkip?.Contains(condition) ?? false)))
                {
                    duplicate.RefreshArmorClassInFeatures(
                        implementationService,
                        armorClassAttribute,
                        condition.ConditionDefinition.Features,
                        pair.Key,
                        FeatureSourceType.Condition,
                        condition.ConditionDefinition.Name,
                        condition.Amount);
                }
            }
        }

        private static string GetArmorClassModifierTag(FeatureOrigin origin)
        {
            return origin.source switch
            {
                CharacterRaceDefinition => AttributeDefinitions.TagRace,
                CharacterClassDefinition => AttributeDefinitions.TagClass,
                CharacterSubclassDefinition => AttributeDefinitions.TagSubclass,
                FeatDefinition => AttributeDefinitions.TagFeat,
                FightingStyleDefinition => AttributeDefinitions.TagFightingStyle,
                InvocationDefinition => AttributeDefinitions.TagInvocation,
                ItemDefinition => AttributeDefinitions.TagEquipment,
                _ => origin.sourceType switch
                {
                    FeatureSourceType.Feat => AttributeDefinitions.TagFeat,
                    FeatureSourceType.FightingStyle =>
                        AttributeDefinitions.TagFightingStyle,
                    FeatureSourceType.Invocation => AttributeDefinitions.TagInvocation,
                    FeatureSourceType.Equipment => AttributeDefinitions.TagEquipment,
                    _ => AttributeDefinitions.TagClass
                }
            };
        }

        private static void AddEquipmentAttackModes(
            RulesetCharacterSimulacrum duplicate,
            out List<IAttackModificationProvider> attackModifiers,
            out Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
        {
            var featuresToBrowse = new List<FeatureDefinition>();

            featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            duplicate.EnumerateFeaturesToBrowse<IAttackModificationProvider>(
                featuresToBrowse,
                featuresOrigin);

            attackModifiers = featuresToBrowse
                .OfType<IAttackModificationProvider>()
                .ToList();

            var mainHandItem = GetActiveItem(
                duplicate,
                EquipmentDefinitions.SlotTypeMainHand);
            var offHandItem = GetActiveItem(
                duplicate,
                EquipmentDefinitions.SlotTypeOffHand);
            var mainHand =
                SimulacrumBehavior.IsProficientWeapon(duplicate, mainHandItem)
                ? mainHandItem
                : null;
            var offHand =
                SimulacrumBehavior.IsProficientWeapon(duplicate, offHandItem)
                ? offHandItem
                : null;

            if (mainHand != null)
            {
                AddEquipmentAttackMode(
                    duplicate,
                    EquipmentDefinitions.SlotTypeMainHand,
                    ActionType.Main,
                    offHandItem == null,
                    true,
                    attackModifiers,
                    featuresOrigin);
            }

            if (offHand != null)
            {
                var actionType = mainHand == null
                    ? ActionType.Main
                    : SimulacrumBehavior.CanUseOffHandWeaponAttack(
                        mainHand,
                        offHand,
                        attackModifiers)
                        ? ActionType.Bonus
                        : (ActionType?)null;

                if (actionType.HasValue)
                {
                    AddEquipmentAttackMode(
                        duplicate,
                        EquipmentDefinitions.SlotTypeOffHand,
                        actionType.Value,
                        false,
                        actionType.Value == ActionType.Main ||
                        attackModifiers.Any(modifier =>
                            modifier?.CanAddAbilityBonusToSecondary == true),
                        attackModifiers,
                        featuresOrigin);
                }
            }

            if (duplicate.AttackModes.All(mode =>
                    mode.ActionType != ActionType.Main))
            {
                AddUnarmedAttackMode(
                    duplicate,
                    offHandItem == null,
                    attackModifiers,
                    featuresOrigin);
            }
        }

        private static RulesetItem GetActiveItem(
            RulesetCharacterSimulacrum duplicate,
            string slotName)
        {
            return duplicate.CharacterInventory.InventorySlotsByName.TryGetValue(
                       slotName,
                       out var slot) &&
                   TryGetActiveEquipmentItem(slotName, slot, out var item)
                ? item
                : null;
        }

        private static void AddEquipmentAttackMode(
            RulesetCharacterSimulacrum duplicate,
            string slotName,
            ActionType actionType,
            bool freeOffHand,
            bool canAddAbilityDamageBonus,
            List<IAttackModificationProvider> attackModifiers,
            Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
        {
            if (!duplicate.CharacterInventory.InventorySlotsByName.TryGetValue(
                    slotName,
                    out var slot) ||
                !TryGetActiveEquipmentItem(slotName, slot, out var item) ||
                item.ItemDefinition is not { IsWeapon: true } itemDefinition)
            {
                return;
            }

            var attackMode = duplicate.RefreshAttackMode(
                actionType,
                itemDefinition,
                itemDefinition.WeaponDescription,
                freeOffHand,
                canAddAbilityDamageBonus,
                slotName,
                attackModifiers,
                featuresOrigin,
                item);

            if (attackMode == null)
            {
                return;
            }

            attackMode.SourceObject = item;
            attackMode.SlotName = slotName;
            attackMode.AttacksNumber = Math.Max(1, attackMode.AttacksNumber);

            if (itemDefinition.RequiresAttunement)
            {
                attackMode.AttackTags.Remove(TagsDefinitions.MagicalWeapon);
            }

            var weaponAttackModeModifiers =
                duplicate.GetSubFeaturesByType<IModifyWeaponAttackMode>();

            weaponAttackModeModifiers.AddRange(
                item.GetSubFeaturesByType<IModifyWeaponAttackMode>());

            foreach (var modifier in weaponAttackModeModifiers.Distinct())
            {
                modifier.ModifyWeaponAttackMode(
                    duplicate,
                    attackMode,
                    item,
                    canAddAbilityDamageBonus);
            }

            duplicate.AttackModes.Add(attackMode);
        }

        private static void AddUnarmedAttackMode(
            RulesetCharacterSimulacrum duplicate,
            bool freeOffHand,
            List<IAttackModificationProvider> attackModifiers,
            Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
        {
            var itemDefinition = ItemDefinitions.UnarmedStrikeBase;
            var weaponDescription = itemDefinition.WeaponDescription;
            RulesetItem weapon = null;

            CustomWeaponsContext.ModifyUnarmedAttackWithGauntlet(
                duplicate,
                ref itemDefinition,
                ref weaponDescription,
                ref weapon);

            var attackMode = duplicate.RefreshAttackMode(
                ActionType.Main,
                itemDefinition,
                weaponDescription,
                freeOffHand,
                true,
                EquipmentDefinitions.SlotTypeMainHand,
                attackModifiers,
                featuresOrigin,
                weapon);

            if (attackMode == null)
            {
                return;
            }

            attackMode.SourceObject = weapon;
            attackMode.SlotName = EquipmentDefinitions.SlotTypeMainHand;
            attackMode.AttacksNumber = Math.Max(1, attackMode.AttacksNumber);

            var weaponAttackModeModifiers =
                duplicate.GetSubFeaturesByType<IModifyWeaponAttackMode>();

            if (weapon != null)
            {
                weaponAttackModeModifiers.AddRange(
                    weapon.GetSubFeaturesByType<IModifyWeaponAttackMode>());
            }

            foreach (var modifier in weaponAttackModeModifiers.Distinct())
            {
                modifier.ModifyWeaponAttackMode(
                    duplicate,
                    attackMode,
                    weapon,
                    true);
            }

            duplicate.AttackModes.Add(attackMode);
        }

        private static IEnumerable<(string SlotName, RulesetItem Item)> EnumerateActiveEquipment(
            RulesetCharacterSimulacrum duplicate)
        {
            var inventory = duplicate.CharacterInventory;
            var seen = new HashSet<RulesetInventorySlot>();

            foreach (var slotName in new[]
                     {
                         EquipmentDefinitions.SlotTypeMainHand,
                         EquipmentDefinitions.SlotTypeOffHand,
                         EquipmentDefinitions.SlotTypeAmmunition
                     })
            {
                if (inventory.InventorySlotsByName.TryGetValue(slotName, out var slot) &&
                    TryGetActiveEquipmentItem(slotName, slot, out var item) &&
                    seen.Add(slot))
                {
                    yield return (slotName, item);
                }
            }

            foreach (var pair in inventory.InventorySlotsByType)
            {
                if (pair.Key == EquipmentDefinitions.SlotTypeMainHand ||
                    pair.Key == EquipmentDefinitions.SlotTypeOffHand ||
                    pair.Key == EquipmentDefinitions.SlotTypeAmmunition)
                {
                    continue;
                }

                foreach (var slot in pair.Value)
                {
                    if (TryGetActiveEquipmentItem(pair.Key, slot, out var item) &&
                        seen.Add(slot))
                    {
                        yield return (pair.Key, item);
                    }
                }
            }
        }

        private static bool TryGetActiveEquipmentItem(
            string slotName,
            RulesetInventorySlot slot,
            out RulesetItem item)
        {
            item = slot?.EquipedItem;

            if (item?.ItemDefinition == null ||
                slot.Disabled ||
                slot.ConfigSlot)
            {
                return false;
            }

            var activeSlotName = slot.SlotTypeDefinition?.Name ?? slotName;

            return item.ItemDefinition.SlotsWhereActive.Contains(activeSlotName);
        }

        private static bool ValidateRuntimeInvariants(
            RulesetCharacterSimulacrum duplicate)
        {
            if (duplicate.CharacterInventory?.BearerGuid != duplicate.Guid ||
                !duplicate.TryGetAttribute(AttributeDefinitions.CharacterLevel, out _) ||
                !duplicate.TryGetAttribute(AttributeDefinitions.ProficiencyBonus, out _))
            {
                return false;
            }

            duplicate.NormalizeInventory();

            return duplicate.SpellRepertoires.All(repertoire =>
                repertoire.CharacterInventory == duplicate.CharacterInventory &&
                repertoire.CharacterInventory.BearerGuid == duplicate.Guid);
        }

        private void CaptureFeatures(IReadOnlyList<SourceFeatureState> sourceFeatures)
        {
            CopiedFeatures.Clear();
            CopiedFeatureSourceTypes.Clear();
            CopiedFeatureSourceNames.Clear();
            CopiedFeatureSourceDefinitionNames.Clear();
            CopiedFeatureSourceDefinitionTypes.Clear();

            foreach (var sourceFeature in sourceFeatures)
            {
                AddCopiedFeature(sourceFeature.Feature, sourceFeature.Origin);
            }
        }

        private static bool CapturesMonsterBaseline(RulesetCharacter source)
        {
            return source is RulesetCharacterMonster;
        }

        internal static IReadOnlyCollection<RulesetCondition>
            CollectSummoningBaselineConditions(RulesetCharacter source)
        {
            if (source is not RulesetCharacterMonster monster ||
                monster.MonsterDefinition == null)
            {
                return [];
            }

            var creatureTags = monster.MonsterDefinition.CreatureTags;
            var affinityConditionsBySource = new Dictionary<
                ulong,
                HashSet<ConditionDefinition>>();
            var result = new HashSet<RulesetCondition>();

            foreach (var condition in source.ConditionsByCategory
                .SelectMany(pair => pair.Value)
                .Where(condition =>
                    condition?.ConditionDefinition != null &&
                    condition.SourceGuid != 0))
            {
                if (!affinityConditionsBySource.TryGetValue(
                        condition.SourceGuid,
                        out var affinityConditions))
                {
                    var conditionSource =
                        EffectHelpers.GetCharacterByGuid(condition.SourceGuid);

                    affinityConditions = conditionSource?
                        .FeaturesByType<FeatureDefinitionSummoningAffinity>()
                        .Where(affinity =>
                            affinity != null &&
                            creatureTags.Contains(affinity.RequiredMonsterTag))
                        .SelectMany(affinity => affinity.AddedConditions)
                        .Where(definition => definition != null)
                        .ToHashSet() ?? [];
                    affinityConditionsBySource.Add(
                        condition.SourceGuid,
                        affinityConditions);
                }

                if (affinityConditions.Contains(condition.ConditionDefinition))
                {
                    result.Add(condition);
                }
            }

            return result;
        }

        private static IReadOnlyList<SourceFeatureState> CollectSourceFeatureStates(
            RulesetCharacter source,
            bool capturesMonsterBaseline)
        {
            var captured = new HashSet<FeatureDefinition>();
            var result = new List<SourceFeatureState>();

            var featuresToBrowse = new List<FeatureDefinition>();
            var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            source.EnumerateFeaturesToBrowse<FeatureDefinition>(
                featuresToBrowse,
                featuresOrigin);

            foreach (var feature in featuresToBrowse.Where(feature =>
                         IsSupportedCopiedFeature(feature) &&
                         (!capturesMonsterBaseline ||
                          feature is not FeatureDefinitionAttributeModifier) &&
                         HasPersistentOrigin(featuresOrigin, feature)))
            {
                if (!captured.Add(feature))
                {
                    continue;
                }

                var origin = featuresOrigin.TryGetValue(feature, out var existingOrigin)
                    ? existingOrigin
                    : CreateFallbackFeatureOrigin(source, feature);

                result.Add(CreateSourceFeatureState(feature, origin));
            }

            // Native enumeration has the most precise class/race/subclass/feat origins. Only use
            // the persistent graph as a fallback for leaf features the engine does not expose in
            // the requested browse type.
            foreach (var candidate in EnumeratePersistentFeatureGraphWithOrigins(
                         source,
                         featuresOrigin))
            {
                if (IsSupportedCopiedFeature(candidate.Feature) &&
                    (!capturesMonsterBaseline ||
                     candidate.Feature is not FeatureDefinitionAttributeModifier) &&
                    captured.Add(candidate.Feature))
                {
                    result.Add(CreateSourceFeatureState(candidate.Feature, candidate.Origin));
                }
            }

            // Native and persistent enumeration carry the authoritative
            // class/race/subclass/feat origin. Add invocation-only graphs last so a
            // feature shared with an unconditional source is not incorrectly disabled
            // when a permanent invocation is toggled off.
            foreach (var invocation in source.Invocations.Where(x =>
                         x?.invocationDefinition?.GrantedFeature != null))
            {
                var definition = invocation.invocationDefinition;
                var origin = new FeatureOrigin(
                    FeatureSourceType.Invocation,
                    definition.Name,
                    definition,
                    definition.GrantedFeature.ParseSpecialFeatureTags());

                foreach (var candidate in EnumerateFeatureGraph(
                             definition.GrantedFeature,
                             origin))
                {
                    if (IsSupportedCopiedFeature(candidate.Feature) &&
                        (!capturesMonsterBaseline ||
                         candidate.Feature is not FeatureDefinitionAttributeModifier) &&
                        captured.Add(candidate.Feature))
                    {
                        result.Add(CreateSourceFeatureState(candidate.Feature, candidate.Origin));
                    }
                }
            }

            return result;
        }

        private static SourceFeatureState CreateSourceFeatureState(
            FeatureDefinition feature,
            FeatureOrigin origin)
        {
            if (string.IsNullOrEmpty(origin.sourceName))
            {
                origin = new FeatureOrigin(
                    origin.sourceType,
                    feature.Name,
                    origin.source ?? feature,
                    feature.ParseSpecialFeatureTags());
            }

            return new SourceFeatureState(feature, origin);
        }

        private void AddCopiedFeature(FeatureDefinition feature, FeatureOrigin origin)
        {
            CopiedFeatures.Add(feature);
            CopiedFeatureSourceTypes.Add((int)origin.sourceType);
            CopiedFeatureSourceNames.Add(
                string.IsNullOrEmpty(origin.sourceName) ? feature.Name : origin.sourceName);

            if (origin.source is BaseDefinition sourceDefinition)
            {
                CopiedFeatureSourceDefinitionNames.Add(sourceDefinition.Name);
                CopiedFeatureSourceDefinitionTypes.Add(sourceDefinition.GetType().FullName);
            }
            else
            {
                CopiedFeatureSourceDefinitionNames.Add(string.Empty);
                CopiedFeatureSourceDefinitionTypes.Add(string.Empty);
            }
        }

        private void CaptureMovementAndSenses(
            IReadOnlyList<MovementModeSeed> movementModes,
            IReadOnlyList<SenseModeSeed> senseModes)
        {
            MovementModeTypes.Clear();
            MovementSpeeds.Clear();
            SenseTypes.Clear();
            SenseRanges.Clear();
            SenseStealthBreakerRanges.Clear();

            foreach (var movementMode in movementModes)
            {
                MovementModeTypes.Add(movementMode.Type);
                MovementSpeeds.Add(movementMode.Speed);
            }

            foreach (var senseMode in senseModes)
            {
                SenseTypes.Add(senseMode.Type);
                SenseRanges.Add(senseMode.Range);
                SenseStealthBreakerRanges.Add(senseMode.StealthBreakerRange);
            }
        }

        private static IReadOnlyList<MovementModeSeed> CollectMovementModes(
            RulesetCharacter source,
            bool capturesMonsterBaseline,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            var featuresToBrowse = new List<FeatureDefinition>();
            var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            source.EnumerateFeaturesToBrowse<IMoveModesProvider>(
                featuresToBrowse,
                featuresOrigin);

            var persistentMoveModes = new Dictionary<int, int>();
            var providers = featuresToBrowse
                .Where(feature => HasPersistentOrigin(featuresOrigin, feature))
                .Concat(EnumerateMonsterBaselineFeatures(
                    source,
                    capturesMonsterBaseline,
                    summoningBaselineConditions))
                .OfType<IMoveModesProvider>()
                .Distinct();

            foreach (var provider in providers)
            {
                provider.RefreshMoveModes(persistentMoveModes);
            }

            return persistentMoveModes
                .OrderBy(pair => pair.Key)
                .Select(pair => new MovementModeSeed(pair.Key, pair.Value))
                .ToArray();
        }

        private static IReadOnlyList<SenseModeSeed> CollectSenseModes(
            RulesetCharacter source,
            bool capturesMonsterBaseline,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            var featuresToBrowse = new List<FeatureDefinition>();
            var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            source.EnumerateFeaturesToBrowse<ISenseModesProvider>(
                featuresToBrowse,
                featuresOrigin);

            var persistentSenseModes = new List<SenseMode>();
            var providers = featuresToBrowse
                .Where(feature => HasPersistentOrigin(featuresOrigin, feature))
                .Concat(EnumerateMonsterBaselineFeatures(
                    source,
                    capturesMonsterBaseline,
                    summoningBaselineConditions))
                .OfType<ISenseModesProvider>()
                .Distinct();

            foreach (var provider in providers)
            {
                provider.RefreshSenseModes(persistentSenseModes);
            }

            // Normal vision is also materialized directly on heroes and monsters. Merge that
            // stable runtime baseline with persistent providers so a generic provider cannot
            // reduce the source's actual sight or stealth-breaking range.
            var stableSenseModes = persistentSenseModes
                .Concat(source.SenseModes.Where(sourceMode =>
                    sourceMode.SenseType == SenseMode.Type.NormalVision))
                .GroupBy(mode => mode.SenseType)
                .Select(group => new SenseModeSeed(
                    (int)group.Key,
                    group.Max(mode => mode.SenseRange),
                    group.Max(mode => mode.StealthBreakerRange)))
                .OrderBy(mode => mode.Type)
                .ToArray();

            return stableSenseModes;
        }

        private static IEnumerable<FeatureDefinition> EnumerateMonsterBaselineFeatures(
            RulesetCharacter source,
            bool capturesMonsterBaseline,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            if (!capturesMonsterBaseline ||
                source is not RulesetCharacterMonster { MonsterDefinition: { } monsterDefinition })
            {
                return [];
            }

            var monsterFeatures = monsterDefinition.Features
                .Where(feature => feature != null)
                .SelectMany(feature =>
                    EnumerateFeatureGraph(
                        feature,
                        CreateFallbackFeatureOrigin(source, feature)))
                .Select(candidate => candidate.Feature);
            var summoningFeatures = (summoningBaselineConditions ?? [])
                .Where(condition => condition?.ConditionDefinition != null)
                .SelectMany(condition => condition.ConditionDefinition.Features)
                .Where(feature => feature != null)
                .SelectMany(feature =>
                    EnumerateFeatureGraph(
                        feature,
                        CreateFallbackFeatureOrigin(source, feature)))
                .Select(candidate => candidate.Feature);

            return monsterFeatures.Concat(summoningFeatures).Distinct();
        }

        private void CaptureSkillAndSavingThrowBonuses(
            IReadOnlyList<SkillBonusSeed> skillBonuses,
            IReadOnlyList<SavingThrowSeed> savingThrows)
        {
            SkillNames.Clear();
            SkillBonuses.Clear();
            SkillProficiencyRanks.Clear();
            SaveAbilityNames.Clear();
            SaveProficiencyDeltas.Clear();

            foreach (var skillBonus in skillBonuses)
            {
                SkillNames.Add(skillBonus.Name);
                SkillBonuses.Add(skillBonus.Bonus);
                SkillProficiencyRanks.Add(skillBonus.ProficiencyRank);
            }

            foreach (var savingThrow in savingThrows)
            {
                SaveAbilityNames.Add(savingThrow.AbilityScore);
                SaveProficiencyDeltas.Add(savingThrow.ProficiencyDelta);
            }
        }

        private static IReadOnlyList<SkillBonusSeed> CollectSkillBonuses(
            RulesetCharacter source,
            IReadOnlyDictionary<string, PersistentAttributeSeed> attributes,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            var result = new List<SkillBonusSeed>();
            var proficiencyRanks = CollectSkillProficiencyRanks(
                source,
                summoningBaselineConditions);
            var persistentProficiencyBonus =
                attributes.TryGetValue(
                    AttributeDefinitions.ProficiencyBonus,
                    out var proficiencyAttribute)
                    ? proficiencyAttribute.FinalValue
                    : source.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);

            foreach (var skill in DatabaseRepository.GetDatabase<SkillDefinition>()
                         .Where(x => proficiencyRanks.ContainsKey(x.Name))
                         .OrderBy(x => x.Name))
            {
                var trends = new List<TrendInfo>();
                var currentAbilityModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                    source.TryGetAttributeValue(skill.AbilityScore));
                var persistentAbilityModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                    attributes.TryGetValue(skill.AbilityScore, out var attribute)
                        ? attribute.FinalValue
                        : 0);
                var bonus = source.ComputeBaseAbilityCheckBonus(
                    skill.AbilityScore,
                    trends,
                    skill.Name);

                bonus -= trends.Where(trend =>
                        trend.sourceType != FeatureSourceType.Proficiency &&
                        IsSnapshotExcludedTrend(
                            trend,
                            summoningBaselineConditions))
                    .Sum(trend => trend.value);
                bonus -= currentAbilityModifier;
                bonus += persistentAbilityModifier;

                var proficiencyRank = proficiencyRanks[skill.Name];

                if (source is RulesetCharacterHero)
                {
                    // Hero proficiency trends always use the generic Proficiency origin,
                    // including temporary Expertise. Replace that live contribution with
                    // the rank derived only from the persistent feature graph.
                    bonus -= trends
                        .Where(trend =>
                            trend.sourceType == FeatureSourceType.Proficiency)
                        .Sum(trend => trend.value);
                    bonus += proficiencyRank * persistentProficiencyBonus;
                }
                else if (source is RulesetCharacterSimulacrum simulacrum)
                {
                    var runtimeRankDelta = Math.Max(
                        0,
                        simulacrum.GetSkillProficiencyRank(skill.Name) -
                        simulacrum.GetSnapshotSkillProficiencyRank(skill.Name));

                    bonus -= runtimeRankDelta *
                             source.TryGetAttributeValue(
                                 AttributeDefinitions.ProficiencyBonus);
                }

                result.Add(new SkillBonusSeed(skill.Name, bonus, proficiencyRank));
            }

            return result;
        }

        private static IReadOnlyDictionary<string, int> CollectSkillProficiencyRanks(
            RulesetCharacter source,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            var skillNames = DatabaseRepository.GetDatabase<SkillDefinition>()
                .Where(skill => skill != null)
                .Select(skill => skill.Name)
                .ToHashSet(StringComparer.Ordinal);
            var result = new Dictionary<string, int>(StringComparer.Ordinal);

            static void SetRank(
                IDictionary<string, int> ranks,
                string skillName,
                int rank)
            {
                if (!string.IsNullOrEmpty(skillName) &&
                    (!ranks.TryGetValue(skillName, out var currentRank) ||
                     currentRank < rank))
                {
                    ranks[skillName] = rank;
                }
            }

            switch (source)
            {
                case RulesetCharacterHero hero:
                    foreach (var skill in hero.TrainedSkills.Where(skill => skill != null))
                    {
                        SetRank(result, skill.Name, 1);
                    }

                    foreach (var skillName in hero.TrainedExpertises)
                    {
                        SetRank(result, skillName, 2);
                    }

                    break;
                case RulesetCharacterSimulacrum simulacrum:
                    foreach (var skillName in simulacrum.SkillProficiencies.Keys)
                    {
                        SetRank(
                            result,
                            skillName,
                            simulacrum.SkillSnapshotCaptured &&
                            simulacrum.SkillExpertises.Contains(skillName)
                                ? 2
                                : 1);
                    }

                    return result;
                case RulesetCharacterMonster monster:
                    foreach (var skillName in monster.SkillProficiencies.Keys)
                    {
                        SetRank(result, skillName, 1);
                    }

                    break;
            }

            var featuresToBrowse = new List<FeatureDefinition>();
            var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            source.EnumerateFeaturesToBrowse<FeatureDefinition>(
                featuresToBrowse,
                featuresOrigin);

            var proficiencyFeatures = featuresToBrowse
                .Where(feature =>
                    !featuresOrigin.TryGetValue(feature, out var origin) ||
                    !IsSnapshotExcludedOrigin(
                        origin,
                        summoningBaselineConditions))
                .Concat(EnumeratePersistentFeatureGraph(source, featuresOrigin))
                .OfType<FeatureDefinitionProficiency>()
                .Distinct()
                .ToArray();

            foreach (var proficiency in proficiencyFeatures.Where(feature =>
                         feature.ProficiencyType == ProficiencyType.Skill))
            {
                foreach (var skillName in proficiency.Proficiencies.Where(skillNames.Contains))
                {
                    SetRank(result, skillName, 1);
                }
            }

            foreach (var proficiency in proficiencyFeatures.Where(feature =>
                         feature.ProficiencyType == ProficiencyType.Expertise))
            {
                foreach (var skillName in proficiency.Proficiencies.Where(skillNames.Contains))
                {
                    SetRank(result, skillName, 2);
                }
            }

            foreach (var proficiency in proficiencyFeatures.Where(feature =>
                         feature.ProficiencyType == ProficiencyType.SkillOrExpertise))
            {
                foreach (var skillName in proficiency.Proficiencies.Where(skillNames.Contains))
                {
                    SetRank(
                        result,
                        skillName,
                        result.ContainsKey(skillName) ? 2 : 1);
                }
            }

            return result;
        }

        private static IReadOnlyList<SavingThrowSeed> CollectSavingThrows(
            RulesetCharacter source,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            var result = new List<SavingThrowSeed>();

            foreach (var abilityScore in AttributeDefinitions.AbilityScoreNames)
            {
                var trends = new List<TrendInfo>();
                var totalBonus = source.ComputeBaseSavingThrowBonus(abilityScore, trends);

                totalBonus -= trends.Where(trend =>
                        IsSnapshotExcludedTrend(
                            trend,
                            summoningBaselineConditions))
                    .Sum(trend => trend.value);
                var abilityModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                    source.TryGetAttributeValue(abilityScore));

                result.Add(new SavingThrowSeed(abilityScore, totalBonus - abilityModifier));
            }

            return result;
        }

        private void CaptureAttacks(IReadOnlyList<AttackSeed> attacks)
        {
            AttackDefinitionKinds.Clear();
            AttackDefinitionNames.Clear();
            AttackTagCounts.Clear();
            AttackTags.Clear();
            AttackDamageFormCounts.Clear();
            AttackDamageTypes.Clear();
            AttackDamageDieTypes.Clear();
            AttackDamageVersatile.Clear();
            AttackDamageVersatileDieTypes.Clear();
            AttackDamageDiceNumbers.Clear();
            AttackDamageBonuses.Clear();
            AttackActionTypes.Clear();
            AttackAbilityScores.Clear();
            AttackNumbers.Clear();
            AttackToHitBonuses.Clear();
            AttackCloseRanges.Clear();
            AttackMaxRanges.Clear();
            AttackReachRanges.Clear();
            AttackSlotNames.Clear();
            AttackRanged.Clear();
            AttackReach.Clear();
            AttackThrown.Clear();
            AttackHasPriority.Clear();
            AttackUseVersatileDamage.Clear();

            foreach (var attack in attacks)
            {
                AttackDefinitionKinds.Add(attack.DefinitionKind);
                AttackDefinitionNames.Add(attack.DefinitionName);
                AttackTagCounts.Add(attack.Tags.Count);
                AttackTags.AddRange(attack.Tags);
                AttackDamageFormCounts.Add(attack.DamageForms.Count);

                foreach (var damage in attack.DamageForms)
                {
                    AttackDamageTypes.Add(damage.DamageType);
                    AttackDamageDieTypes.Add(damage.DieType);
                    AttackDamageVersatile.Add(damage.Versatile);
                    AttackDamageVersatileDieTypes.Add(damage.VersatileDieType);
                    AttackDamageDiceNumbers.Add(damage.DiceNumber);
                    AttackDamageBonuses.Add(damage.BonusDamage);
                }

                AttackActionTypes.Add(attack.ActionType);
                AttackAbilityScores.Add(attack.AbilityScore);
                AttackNumbers.Add(attack.AttacksNumber);
                AttackToHitBonuses.Add(attack.ToHitBonus);
                AttackCloseRanges.Add(attack.CloseRange);
                AttackMaxRanges.Add(attack.MaxRange);
                AttackReachRanges.Add(attack.ReachRange);
                AttackSlotNames.Add(attack.SlotName);
                AttackRanged.Add(attack.Ranged);
                AttackReach.Add(attack.Reach);
                AttackThrown.Add(attack.Thrown);
                AttackHasPriority.Add(attack.HasPriority);
                AttackUseVersatileDamage.Add(attack.UseVersatileDamage);
            }
        }

        private static IReadOnlyList<AttackSeed> CollectAttackSeeds(
            RulesetCharacter source,
            IReadOnlyList<RulesetAttackMode> sourceAttacks,
            IReadOnlyDictionary<string, PersistentAttributeSeed> attributes,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            var result = new List<AttackSeed>();
            var transientAttackModifiers = GetAttackModifiers(
                source,
                true,
                summoningBaselineConditions);
            var persistentAttackModifiers = GetAttackModifiers(
                source,
                false,
                summoningBaselineConditions);

            foreach (var attackMode in sourceAttacks)
            {
                if (attackMode == null ||
                    source is RulesetCharacterHero &&
                    attackMode.SourceObject is RulesetItem)
                {
                    continue;
                }

                var kind = attackMode.SourceDefinition switch
                {
                    MonsterAttackDefinition => MonsterAttackKind,
                    ItemDefinition => ItemAttackKind,
                    _ => null
                };

                if (kind == null)
                {
                    continue;
                }

                var attackTags = attackMode.AttackTags.ToList();
                var validTransientModifiers = transientAttackModifiers
                    .Where(modifier => IsAttackModifierValid(source, attackMode, modifier))
                    .ToArray();
                var persistentAttackAbility = GetPersistentAttackAbility(attackMode, attributes);
                var persistentAttackAbilityModifier =
                    AttributeDefinitions.ComputeAbilityScoreModifier(
                        attributes.TryGetValue(persistentAttackAbility, out var attribute)
                            ? attribute.FinalValue
                            : 0);
                var attackAbilityTrends = attackMode.ToHitBonusTrends
                    .Where(trend => trend.sourceType == FeatureSourceType.AbilityScore)
                    .ToArray();
                var permanentlyMagical =
                    IsNaturallyMagical(attackMode.SourceDefinition) ||
                    persistentAttackModifiers.Any(modifier =>
                        modifier.MagicalWeapon &&
                        IsAttackModifierValid(source, attackMode, modifier));

                if (!permanentlyMagical &&
                    validTransientModifiers.Any(modifier => modifier.MagicalWeapon))
                {
                    attackTags.Remove(TagsDefinitions.MagicalWeapon);
                }

                var damageForms = attackMode.EffectDescription?.EffectForms
                                      .Where(form => form.FormType == EffectForm.EffectFormType.Damage)
                                      .Select(form => form.DamageForm)
                                      .Where(form => form != null)
                                      .ToArray()
                                  ?? [];
                var damageSeeds = new List<AttackDamageSeed>(damageForms.Length);

                for (var damageIndex = 0; damageIndex < damageForms.Length; damageIndex++)
                {
                    var damageForm = damageForms[damageIndex];
                    var abilityScoreBonus = damageForm.DamageBonusTrends
                        .Where(trend => trend.sourceType == FeatureSourceType.AbilityScore)
                        .Sum(trend => trend.value);

                    damageSeeds.Add(new AttackDamageSeed
                    {
                        DamageType = damageForm.DamageType,
                        DieType = (int)damageForm.DieType,
                        Versatile = damageForm.Versatile,
                        VersatileDieType = (int)damageForm.VersatileDieType,
                        DiceNumber = Math.Max(
                            0,
                            damageForm.DiceNumber -
                            (damageIndex == 0
                                ? validTransientModifiers.Sum(modifier => modifier.AdditionalDamageDice)
                                : 0)),
                        BonusDamage =
                            damageForm.BonusDamage -
                            damageForm.DamageBonusTrends
                                .Where(trend => IsSnapshotExcludedTrend(
                                    trend,
                                    summoningBaselineConditions))
                                .Sum(trend => trend.value) -
                            abilityScoreBonus +
                            (abilityScoreBonus == 0 ? 0 : persistentAttackAbilityModifier)
                    });
                }

                result.Add(new AttackSeed
                {
                    DefinitionKind = kind,
                    DefinitionName = attackMode.SourceDefinition.Name,
                    Tags = attackTags.ToArray(),
                    DamageForms = damageSeeds,
                    ActionType = (int)attackMode.ActionType,
                    AbilityScore = persistentAttackAbility,
                    AttacksNumber = Math.Max(
                        1,
                        attackMode.AttacksNumber -
                        validTransientModifiers
                            .Where(modifier => modifier.AdditionalMonsterAttack)
                            .Sum(modifier => modifier.AdditionalMonsterAttacksCount)),
                    ToHitBonus =
                        attackMode.ToHitBonus -
                        attackMode.ToHitBonusTrends
                            .Where(trend => IsSnapshotExcludedTrend(
                                trend,
                                summoningBaselineConditions))
                            .Sum(trend => trend.value) -
                        attackAbilityTrends.Sum(trend => trend.value) +
                        (attackAbilityTrends.Length == 0 ? 0 : persistentAttackAbilityModifier),
                    CloseRange = attackMode.CloseRange,
                    MaxRange = attackMode.MaxRange,
                    ReachRange = attackMode.ReachRange,
                    SlotName = attackMode.SlotName,
                    Ranged = attackMode.Ranged,
                    Reach = attackMode.Reach,
                    Thrown = attackMode.Thrown,
                    HasPriority = attackMode.HasPriority,
                    UseVersatileDamage = attackMode.UseVersatileDamage
                });
            }

            return result;
        }

        private static string GetPersistentAttackAbility(
            RulesetAttackMode attackMode,
            IReadOnlyDictionary<string, PersistentAttributeSeed> attributes)
        {
            if (attackMode.SourceDefinition is not ItemDefinition
                {
                    IsWeapon: true
                } item ||
                !item.WeaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagFinesse))
            {
                return attackMode.AbilityScore;
            }

            return attributes.TryGetValue(AttributeDefinitions.Dexterity, out var dexterity) &&
                   attributes.TryGetValue(AttributeDefinitions.Strength, out var strength) &&
                   dexterity.FinalValue > strength.FinalValue
                ? AttributeDefinitions.Dexterity
                : AttributeDefinitions.Strength;
        }

        private static IAttackModificationProvider[] GetAttackModifiers(
            RulesetCharacter source,
            bool excludedFromSnapshot,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions = null)
        {
            var featuresToBrowse = new List<FeatureDefinition>();
            var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            source.EnumerateFeaturesToBrowse<IAttackModificationProvider>(
                featuresToBrowse,
                featuresOrigin);

            return featuresToBrowse
                .Where(feature =>
                    featuresOrigin.TryGetValue(feature, out var origin) &&
                    IsSnapshotExcludedOrigin(
                        origin,
                        summoningBaselineConditions) ==
                    excludedFromSnapshot)
                .OfType<IAttackModificationProvider>()
                .ToArray();
        }

        private static bool IsAttackModifierValid(
            RulesetCharacter character,
            RulesetAttackMode attackMode,
            IAttackModificationProvider attackModifier)
        {
            if (attackModifier.TriggerCondition ==
                AttackModificationTriggerCondition.NotWearingArmorOrMageArmorOrShield &&
                (character.IsWearingArmor() ||
                 character.IsWearingShield() ||
                 character.HasConditionOfType(
                     ConditionDefinitions.ConditionMagicallyArmored)))
            {
                return false;
            }

            var service = ServiceRepository.GetService<IRulesetImplementationService>();

            return service == null ||
                   service.IsValidContextForRestrictedContextProvider(
                       attackModifier,
                       character,
                       attackMode.SourceDefinition as ItemDefinition,
                       attackMode.Ranged,
                       attackMode,
                       null);
        }

        private static bool IsNaturallyMagical(BaseDefinition attackDefinition)
        {
            return attackDefinition switch
            {
                ItemDefinition item => item.Magical,
                MonsterAttackDefinition attack => attack.Magical,
                _ => false
            };
        }

        private static void ApplyTransientAttackModifiers(RulesetCharacterMonster character)
        {
            var summoningBaselineConditions =
                CollectSummoningBaselineConditions(character);
            var summoningBaselineDefinitions = summoningBaselineConditions
                .Select(condition => condition.ConditionDefinition)
                .ToHashSet();
            var attackModifiers = GetAttackModifiers(
                character,
                true,
                summoningBaselineConditions);
            var conditionDefinitions = character.ConditionsByCategory
                .SelectMany(x => x.Value)
                .Select(x => x.ConditionDefinition)
                .Where(definition => !summoningBaselineDefinitions.Contains(definition))
                .Distinct()
                .ToArray();

            var customAttackModifiers = conditionDefinitions
                .SelectMany(x => x.GetAllSubFeaturesOfType<IModifyWeaponAttackMode>())
                .ToArray();

            foreach (var attackMode in character.AttackModes)
            {
                foreach (var attackModifier in attackModifiers.Where(x =>
                             IsAttackModifierValid(character, attackMode, x)))
                {
                    ApplyTransientAttackModifier(character, attackMode, attackModifier);
                }

                customAttackModifiers
                    .Do(x => x.ModifyWeaponAttackMode(character, attackMode, null, false));
            }
        }

        private static void ApplyExtraAttacks(RulesetCharacterMonster character)
        {
            var existingModes = character.AttackModes.ToHashSet();

            character.GetSubFeaturesByType<IAddExtraAttack>()
                .OrderBy(provider => provider.Priority())
                .Do(provider => provider.TryAddExtraAttack(character));

            foreach (var attackMode in character.AttackModes.Where(mode =>
                         !existingModes.Contains(mode)))
            {
                var item = attackMode.SourceObject as RulesetItem;
                var modifiers = character.GetSubFeaturesByType<IModifyWeaponAttackMode>();

                if (item != null)
                {
                    modifiers.AddRange(item.GetSubFeaturesByType<IModifyWeaponAttackMode>());
                }

                modifiers.Distinct().Do(modifier =>
                    modifier.ModifyWeaponAttackMode(
                        character,
                        attackMode,
                        item,
                        true));
            }
        }

        private static void ApplyTransientAttackModifier(
            RulesetCharacterMonster character,
            RulesetAttackMode attackMode,
            IAttackModificationProvider attackModifier)
        {
            var feature = attackModifier as FeatureDefinition;
            var damageForm = attackMode.EffectDescription?.FindFirstDamageForm();

            if (attackModifier.AttackRollModifierMethod != AttackModifierMethod.None ||
                attackModifier.AttackRollModifier != 0)
            {
                var modifier = RulesetCharacterMonsterExtensions.ComputeAttackRollModifier(
                    character,
                    attackModifier);

                attackMode.ToHitBonus += modifier;
                AddTransientTrend(character, feature, modifier, attackMode.ToHitBonusTrends);
            }

            if (damageForm != null &&
                attackModifier.DamageRollModifierMethod != AttackModifierMethod.None)
            {
                var modifier = RulesetCharacterMonsterExtensions.ComputeDamageRollModifier(
                    character,
                    attackModifier);

                damageForm.BonusDamage += modifier;
                AddTransientTrend(character, feature, modifier, damageForm.DamageBonusTrends);
            }

            if (damageForm != null && attackModifier.AdditionalDamageDice > 0)
            {
                damageForm.DiceNumber += attackModifier.AdditionalDamageDice;
            }

            if (attackModifier.MagicalWeapon)
            {
                attackMode.AddAttackTagAsNeeded(TagsDefinitions.MagicalWeapon);
            }

            if (attackModifier.AdditionalMonsterAttack)
            {
                attackMode.AttacksNumber += attackModifier.AdditionalMonsterAttacksCount;
            }

            if (attackModifier.AbilityScoreReplacement ==
                AbilityScoreReplacement.DexterityIfBetterThanStrength)
            {
                attackMode.AbilityScore =
                    character.TryGetAttributeValue(AttributeDefinitions.Dexterity) >=
                    character.TryGetAttributeValue(AttributeDefinitions.Strength)
                        ? AttributeDefinitions.Dexterity
                        : AttributeDefinitions.Strength;
            }

            if (damageForm != null &&
                attackModifier.DamageDieReplacement == DamageDieReplacement.FirstDamageForm)
            {
                damageForm.DieType = attackModifier.ReplacedDieType;

                if (damageForm.VersatileDieType < attackModifier.ReplacedDieType)
                {
                    damageForm.VersatileDieType = attackModifier.ReplacedDieType;
                }
            }

            foreach (var additionalEffectForm in attackModifier.AdditionalEffectForms ?? [])
            {
                attackMode.EffectDescription.EffectForms.Add(EffectForm.GetCopy(additionalEffectForm));
            }
        }

        private static void AddTransientTrend(
            RulesetCharacter character,
            FeatureDefinition feature,
            int value,
            List<TrendInfo> trends)
        {
            if (feature != null &&
                character.FeaturesOrigin.TryGetValue(feature, out var origin))
            {
                trends.Add(new TrendInfo(
                    value,
                    origin.sourceType,
                    origin.sourceName,
                    origin.source));
            }
        }

        private static bool IsSupportedCopiedFeature(FeatureDefinition feature)
        {
            // Most camp affinities describe the owner's rest schedule or food needs and must not
            // become creature features. Font of Inspiration is different: native ApplyRest uses
            // this semantic flag to decide whether Bardic Inspiration recovers on a short rest.
            if (feature is FeatureDefinitionCampAffinity campAffinity)
            {
                return campAffinity.Name == BardicInspirationRecoveryFeatureName;
            }

            if (feature is
                null or
                FeatureDefinitionAutoPreparedSpells or
                FeatureDefinitionBonusCantrips or
                FeatureDefinitionCastSpell or
                FeatureDefinitionCharacterPresentation or
                FeatureDefinitionCraftingAffinity or
                FeatureDefinitionFactionAffinity or
                FeatureDefinitionPower or
                FeatureDefinitionFactionChange or
                FeatureDefinitionFightingStyleChoice or
                FeatureDefinitionLoreExpertise or
                FeatureDefinitionPointPool or
                FeatureDefinitionRegeneration or
                FeatureDefinitionRestHealingModifier or
                FeatureDefinitionSchoolSavant or
                FeatureDefinitionSocialAffinity or
                FeatureDefinitionSubclassChoice)
            {
                return false;
            }

            // Summoning affinities are persistent capabilities of the owner, not
            // transient bonuses on an already summoned creature. Retain them so a
            // duplicate's summons receive the same native conditions as the source's.

            // Most unconditional numerical modifiers are baked into the persistent
            // attribute snapshot. Armor class is rebuilt from current equipment and
            // owned features, so retain every AC operation including ForceIfBetter.
            return feature is not FeatureDefinitionAttributeModifier attributeModifier ||
                   attributeModifier.ModifiedAttribute == AttributeDefinitions.ArmorClass ||
                   IsRuntimeAttributeModifier(attributeModifier);
        }

        private static bool IsRuntimeAttributeModifier(
            FeatureDefinitionAttributeModifier attributeModifier)
        {
            return attributeModifier?.SituationalContext != SituationalContext.None;
        }

        private static bool HasPersistentOrigin(
            IReadOnlyDictionary<FeatureDefinition, FeatureOrigin> featuresOrigin,
            FeatureDefinition feature)
        {
            return !featuresOrigin.TryGetValue(feature, out var origin) ||
                   !IsSnapshotExcludedOrigin(origin.sourceType);
        }

        private static IEnumerable<FeatureDefinition> EnumeratePersistentFeatureGraph(
            RulesetCharacter source,
            IReadOnlyDictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
        {
            return EnumeratePersistentFeatureGraphWithOrigins(source, featuresOrigin)
                .Select(x => x.Feature);
        }

        private static IEnumerable<FeatureOriginCandidate>
            EnumeratePersistentFeatureGraphWithOrigins(
                RulesetCharacter source,
                IReadOnlyDictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
        {
            var pending = new Stack<FeatureOriginCandidate>();
            var visited = new HashSet<FeatureDefinition>();
            var activeFeatures = source switch
            {
                RulesetCharacterHero hero => hero.ActiveFeatures.Values.SelectMany(x => x),
                RulesetCharacterMonster monster => monster.ActiveFeatures,
                _ => Enumerable.Empty<FeatureDefinition>()
            };

            foreach (var root in activeFeatures
                         .Where(x => x != null && HasPersistentOrigin(featuresOrigin, x)))
            {
                var origin = featuresOrigin.TryGetValue(root, out var existingOrigin)
                    ? existingOrigin
                    : CreateFallbackFeatureOrigin(source, root);

                pending.Push(new FeatureOriginCandidate(root, origin));
            }

            while (pending.Count > 0)
            {
                var candidate = pending.Pop();
                var feature = candidate.Feature;

                if (feature == null || !visited.Add(feature))
                {
                    continue;
                }

                yield return candidate;

                if (feature is not FeatureDefinitionFeatureSet featureSet)
                {
                    continue;
                }

                foreach (var child in featureSet.FeatureSet
                             .Where(x => x != null)
                             .Reverse())
                {
                    var origin = featuresOrigin.TryGetValue(child, out var existingOrigin)
                        ? existingOrigin
                        : candidate.Origin;

                    if (!IsSnapshotExcludedOrigin(origin.sourceType))
                    {
                        pending.Push(new FeatureOriginCandidate(child, origin));
                    }
                }
            }
        }

        private static IEnumerable<FeatureOriginCandidate> EnumerateFeatureGraph(
            FeatureDefinition root,
            FeatureOrigin origin)
        {
            var pending = new Stack<FeatureOriginCandidate>();
            var visited = new HashSet<FeatureDefinition>();

            pending.Push(new FeatureOriginCandidate(root, origin));

            while (pending.Count > 0)
            {
                var candidate = pending.Pop();

                if (candidate.Feature == null || !visited.Add(candidate.Feature))
                {
                    continue;
                }

                yield return candidate;

                if (candidate.Feature is not FeatureDefinitionFeatureSet featureSet)
                {
                    continue;
                }

                foreach (var child in featureSet.FeatureSet.Where(x => x != null).Reverse())
                {
                    pending.Push(new FeatureOriginCandidate(child, candidate.Origin));
                }
            }
        }

        private static FeatureOrigin CreateFallbackFeatureOrigin(
            RulesetCharacter source,
            FeatureDefinition feature)
        {
            return new FeatureOrigin(
                source is RulesetCharacterMonster
                    ? FeatureSourceType.MonsterFeature
                    : FeatureSourceType.CharacterFeature,
                feature.Name,
                feature,
                feature.ParseSpecialFeatureTags());
        }

        private static bool IsSnapshotExcludedOrigin(FeatureSourceType sourceType)
        {
            return sourceType == FeatureSourceType.Equipment ||
                   IsTransientOrigin(sourceType);
        }

        private static bool IsSnapshotExcludedOrigin(
            FeatureOrigin origin,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            return IsSnapshotExcludedOrigin(origin.sourceType) &&
                   !(summoningBaselineConditions is { Count: > 0 } &&
                     origin.sourceType == FeatureSourceType.Condition &&
                     IsSummoningBaselineCondition(
                         origin.source,
                         origin.sourceName,
                         summoningBaselineConditions));
        }

        private static bool IsSnapshotExcludedTrend(
            TrendInfo trend,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            return IsSnapshotExcludedOrigin(trend.sourceType) &&
                   !(summoningBaselineConditions is { Count: > 0 } &&
                     trend.sourceType == FeatureSourceType.Condition &&
                     IsSummoningBaselineCondition(
                         trend.source,
                         trend.sourceName,
                         summoningBaselineConditions));
        }

        private static bool IsSummoningBaselineCondition(
            object source,
            string sourceName,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            return source is ConditionDefinition condition &&
                   summoningBaselineConditions.Any(candidate =>
                       candidate.ConditionDefinition == condition) ||
                   !string.IsNullOrEmpty(sourceName) &&
                   summoningBaselineConditions.Any(candidate =>
                       string.Equals(
                           candidate.ConditionDefinition?.Name,
                           sourceName,
                           StringComparison.Ordinal));
        }

        private static bool IsTransientOrigin(FeatureSourceType sourceType)
        {
            return sourceType is
                FeatureSourceType.Condition or
                FeatureSourceType.Spell or
                FeatureSourceType.Power or
                FeatureSourceType.Lighting or
                FeatureSourceType.Proximity or
                FeatureSourceType.EffectProxy or
                FeatureSourceType.TargetTag;
        }

        private void CaptureAppearance(
            SimulacrumAppearanceSeed appearance,
            RulesetCharacterMonster duplicate)
        {
            SourceSex = (int)appearance.Sex;
            SourceBodyAssetPrefix = appearance.BodyAssetPrefix;
            SourceArmorAssetPrefix = appearance.ArmorAssetPrefix;
            SourceHelmetAssetPrefix = appearance.HelmetAssetPrefix;
            SourceFaceShapeAssetPrefix = appearance.FaceShapeAssetPrefix;
            SourceBeardShapeAssetPrefix = appearance.BeardShapeAssetPrefix;
            SourceHairShapeAssetPrefix = appearance.HairShapeAssetPrefix;
            SourceHornsTailAssetPrefix = appearance.HornsTailAssetPrefix;
            SourceHumanoidPresentation = appearance.HumanoidPresentation;
            SourceMonsterPresentation = appearance.MonsterPresentation;
            SourceDefinitionPresentationName = appearance.DefinitionPresentationSourceName;
            SourceMorphotypeCategories.Clear();
            SourceMorphotypeValues.Clear();
            SourceMorphotypeAdditionalCategories.Clear();
            SourceMorphotypeAdditionalValues.Clear();

            foreach (var morphotype in appearance.MorphotypeElements.OrderBy(x => x.Key))
            {
                SourceMorphotypeCategories.Add((int)morphotype.Key);
                SourceMorphotypeValues.Add(morphotype.Value);
            }

            foreach (var morphotype in appearance.MorphotypeAdditionalValues.OrderBy(x => x.Key))
            {
                SourceMorphotypeAdditionalCategories.Add((int)morphotype.Key);
                SourceMorphotypeAdditionalValues.Add(morphotype.Value);
            }

            duplicate.Sex = appearance.Sex;
            duplicate.ForcedName = SourceDisplayName;
            duplicate.MorphotypeElements = appearance.MorphotypeElements.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
            duplicate.MorphotypeElementAdditionalValues =
                appearance.MorphotypeAdditionalValues.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value);

            RestoreAppearance(duplicate);
        }

        private void RestoreDeity(RulesetCharacterSimulacrum duplicate)
        {
            duplicate.DeityDefinition =
                !string.IsNullOrEmpty(SourceDeityName) &&
                TryGetDefinition<DeityDefinition>(SourceDeityName, out var deity)
                    ? deity
                    : null;
        }

        private void RestoreAppearance(RulesetCharacterMonster duplicate)
        {
            duplicate.Sex = (CreatureSex)SourceSex;
            duplicate.MorphotypeElements.Clear();
            duplicate.MorphotypeElementAdditionalValues.Clear();

            for (var i = 0;
                 i < Math.Min(SourceMorphotypeCategories.Count, SourceMorphotypeValues.Count);
                 i++)
            {
                duplicate.MorphotypeElements[
                    (MorphotypeElementDefinition.ElementCategory)SourceMorphotypeCategories[i]] =
                    SourceMorphotypeValues[i];
            }

            for (var i = 0;
                 i < Math.Min(
                     SourceMorphotypeAdditionalCategories.Count,
                     SourceMorphotypeAdditionalValues.Count);
                 i++)
            {
                duplicate.MorphotypeElementAdditionalValues[
                    (MorphotypeElementDefinition.ElementCategory)
                    SourceMorphotypeAdditionalCategories[i]] =
                    SourceMorphotypeAdditionalValues[i];
            }

            var bodyAssetPrefix = SourceBodyAssetPrefix;
            var armorAssetPrefix = SourceArmorAssetPrefix;
            var helmetAssetPrefix = SourceHelmetAssetPrefix;
            var faceShapeAssetPrefix = SourceFaceShapeAssetPrefix;
            var beardShapeAssetPrefix = SourceBeardShapeAssetPrefix;
            var hairShapeAssetPrefix = SourceHairShapeAssetPrefix;
            var hornsTailAssetPrefix = SourceHornsTailAssetPrefix;

            if (UsesInventoryAppearance &&
                SourceHumanoidPresentation?.RaceDefinition != null)
            {
                var race = SourceHumanoidPresentation.RaceDefinition;
                var subRace = SourceHumanoidPresentation.SubRaceDefinition;
                var sex = SourceHumanoidPresentation.Sex;

                bodyAssetPrefix = GraphicsCharacterDefinitions.GetBodyAssetPrefix(race, subRace, sex);
                armorAssetPrefix = GraphicsCharacterDefinitions.GetArmorAssetPrefix(race, subRace, sex);
                helmetAssetPrefix = GraphicsCharacterDefinitions.GetHelmetAssetPrefix(race, subRace, sex);
                faceShapeAssetPrefix =
                    GraphicsCharacterDefinitions.GetFaceShapeAssetPrefix(race, subRace, sex);
                beardShapeAssetPrefix =
                    GraphicsCharacterDefinitions.GetBeardShapeAssetPrefix(race, subRace, sex);
                hairShapeAssetPrefix =
                    GraphicsCharacterDefinitions.GetHairShapeAssetPrefix(race, subRace, sex);
                hornsTailAssetPrefix =
                    GraphicsCharacterDefinitions.GetHornsTailAssetPrefix(race, subRace, sex);
            }

            MonsterBodyAssetPrefix(duplicate) = bodyAssetPrefix;
            MonsterArmorAssetPrefix(duplicate) = armorAssetPrefix;
            MonsterHelmetAssetPrefix(duplicate) = helmetAssetPrefix;
            MonsterFaceShapeAssetPrefix(duplicate) = faceShapeAssetPrefix;
            MonsterBeardShapeAssetPrefix(duplicate) = beardShapeAssetPrefix;
            MonsterHairShapeAssetPrefix(duplicate) = hairShapeAssetPrefix;
            MonsterHornsTailAssetPrefix(duplicate) = hornsTailAssetPrefix;

            if (SourceHumanoidPresentation != null || SourceMonsterPresentation != null)
            {
                HumanoidPresentation(duplicate) = SourceHumanoidPresentation;
                MonsterPresentation(duplicate) = SourceMonsterPresentation;
            }
        }

        internal void PrepareAppearance(RulesetCharacterMonster duplicate)
        {
            RestoreAppearance(duplicate);
        }

        private void EnsureRequiredAttributes(
            RulesetCharacter source,
            RulesetCharacterMonster duplicate,
            IEnumerable<FeatureDefinitionPower> powers)
        {
            if (source != null)
            {
                foreach (var attributeName in source.Attributes.Keys.ToArray())
                {
                    EnsureAttribute(duplicate, source, attributeName);
                }
            }

            EnsureAttribute(
                duplicate,
                source,
                AttributeDefinitions.CharacterLevel,
                CharacterLevel,
                1,
                1000);
            EnsureAttribute(
                duplicate,
                source,
                AttributeDefinitions.ProficiencyBonus,
                ProficiencyBonus,
                0,
                1000);

            foreach (var attributeModifier in CopiedFeatures
                         .Where(IsSupportedCopiedFeature)
                         .OfType<FeatureDefinitionAttributeModifier>()
                         .Where(x => !string.IsNullOrEmpty(x.ModifiedAttribute)))
            {
                EnsureAttribute(duplicate, source, attributeModifier.ModifiedAttribute);
            }

            foreach (var powerDefinition in (powers ?? Enumerable.Empty<FeatureDefinitionPower>())
                         .Where(definition => definition != null)
                         .Distinct())
            {
                var attributeName = PowerProvider.GetUsesAttributeName(powerDefinition);

                if (string.IsNullOrEmpty(attributeName))
                {
                    continue;
                }

                var costPerUse = Math.Max(1, powerDefinition.CostPerUse);
                var usablePower = duplicate.UsablePowers.FirstOrDefault(power =>
                                      power?.PowerDefinition == powerDefinition) ??
                                  PowerProvider.Get(powerDefinition, source ?? duplicate);
                var fallbackBaseValue = (int)Math.Min(
                    1000L,
                    Math.Max(
                        0L,
                        (long)PowerProvider.GetEffectiveMaxUses(
                            source ?? duplicate,
                            usablePower) * costPerUse));

                EnsureAttribute(
                    duplicate,
                    source,
                    attributeName,
                    fallbackBaseValue,
                    0,
                    1000);
            }
        }

        private static RulesetAttribute EnsureAttribute(
            RulesetCharacterMonster duplicate,
            RulesetCharacter source,
            string attributeName,
            int fallbackBaseValue = 0,
            int fallbackMinValue = 0,
            int fallbackMaxValue = 1000)
        {
            if (string.IsNullOrEmpty(attributeName))
            {
                return null;
            }

            RulesetAttribute sourceAttribute = null;

            source?.TryGetAttribute(attributeName, out sourceAttribute);

            if (duplicate.TryGetAttribute(attributeName, out var existingAttribute))
            {
                if (sourceAttribute != null)
                {
                    existingAttribute.BaseValue = sourceAttribute.BaseValue;
                    existingAttribute.MinValue = sourceAttribute.MinValue;
                    existingAttribute.MaxValue = sourceAttribute.MaxValue;
                    existingAttribute.Refresh();
                }

                return existingAttribute;
            }

            var attributeDefinition = sourceAttribute?.AttributeDefinition ??
                                      DatabaseRepository.GetDatabase<SmartAttributeDefinition>()
                                          .GetElement(attributeName);

            if (attributeDefinition == null)
            {
                throw new InvalidOperationException(
                    $"Cannot register missing Simulacrum attribute '{attributeName}'.");
            }

            var attribute = new RulesetAttribute
            {
                AttributeDefinition = attributeDefinition,
                BaseValue = sourceAttribute?.BaseValue ?? fallbackBaseValue,
                MinValue = sourceAttribute?.MinValue ?? fallbackMinValue,
                MaxValue = sourceAttribute?.MaxValue ?? fallbackMaxValue
            };

            duplicate.Attributes.Add(attributeName, attribute);
            attribute.Refresh();

            return attribute;
        }

        private static IReadOnlyList<SpellRepertoireSeed> CollectSpellRepertoires(
            RulesetCharacter source,
            IEnumerable<RulesetSpellRepertoire> sourceRepertoires,
            IReadOnlyDictionary<string, PersistentAttributeSeed> attributes)
        {
            var featuresToBrowse = new List<FeatureDefinition>();
            var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            source.EnumerateFeaturesToBrowse<ISpellCastingAffinityProvider>(
                featuresToBrowse,
                featuresOrigin);

            var excludedSpellAttackBonus = featuresToBrowse
                .Where(feature =>
                    feature is ISpellCastingAffinityProvider &&
                    featuresOrigin.TryGetValue(feature, out var origin) &&
                    IsSnapshotExcludedOrigin(origin.sourceType))
                .Cast<ISpellCastingAffinityProvider>()
                .Where(provider =>
                    provider.SpellAttackModifierType == SpellParamsModifierType.FlatValue)
                .Sum(provider => provider.SpellAttackModifier);
            var excludedSaveDcBonus = featuresToBrowse
                .Where(feature =>
                    feature is ISpellCastingAffinityProvider &&
                    featuresOrigin.TryGetValue(feature, out var origin) &&
                    IsSnapshotExcludedOrigin(origin.sourceType))
                .Cast<ISpellCastingAffinityProvider>()
                .Where(provider => provider.SaveDCModifierType == SpellParamsModifierType.FlatValue)
                .Sum(provider => provider.SaveDCModifier);
            var currentProficiencyBonus = source.TryGetAttributeValue(
                AttributeDefinitions.ProficiencyBonus);
            var persistentProficiencyBonus = attributes.TryGetValue(
                AttributeDefinitions.ProficiencyBonus,
                out var proficiencyBonus)
                ? proficiencyBonus.FinalValue
                : currentProficiencyBonus;
            var inventoryItems = new List<RulesetItem>();

            source.CharacterInventory?.EnumerateAllItems(inventoryItems, true, false);

            var scribedSpells = inventoryItems
                .OfType<RulesetItemSpellbook>()
                .SelectMany(spellbook => spellbook.ScribedSpells)
                .Where(IsAllowedSpell)
                .Distinct()
                .ToArray();

            return sourceRepertoires.Select(repertoire => new SpellRepertoireSeed
                {
                    FormAbilityBonus = GetPersistentSpellcastingAbilityBonus(
                        source,
                        repertoire,
                        attributes),
                    SaveDc = GetPersistentSpellcastingValue(
                        repertoire.saveDC,
                        repertoire,
                        source,
                        attributes,
                        currentProficiencyBonus,
                        persistentProficiencyBonus,
                        excludedSaveDcBonus),
                    SpellAttackBonus = GetPersistentSpellcastingValue(
                        repertoire.spellAttackBonus,
                        repertoire,
                        source,
                        attributes,
                        currentProficiencyBonus,
                        persistentProficiencyBonus,
                        excludedSpellAttackBonus),
                    SpellCastingFeature = repertoire.SpellCastingFeature,
                    SpellCastingClass = repertoire.SpellCastingClass,
                    SpellCastingSubclass = repertoire.SpellCastingSubclass,
                    SpellCastingRace = repertoire.SpellCastingRace,
                    SpellCastingAbility = repertoire.SpellCastingAbility,
                    AutoPreparedTag = repertoire.autoPreparedTag ?? string.Empty,
                    MaxPreparedSpells = repertoire.maxPreparedSpells,
                    SpellCastingLevel = repertoire.spellCastingLevel,
                    SpellCastingMonster = repertoire.spellCastingMonster,
                    KnownCantrips = repertoire.knownCantrips.Where(IsAllowedSpell).ToArray(),
                    // A Wizard's spellbook is a physical item and is intentionally not copied.
                    // Preserve its learned spell definitions in the independent repertoire so
                    // 2024 ritual casting can still use learned, unprepared ritual spells.
                    KnownSpells = repertoire.knownSpells
                        .Concat(repertoire.SpellCastingFeature?.SpellKnowledge ==
                                SpellKnowledge.Spellbook
                            ? scribedSpells
                            : Enumerable.Empty<SpellDefinition>())
                        .Where(IsAllowedSpell)
                        .Distinct()
                        .ToArray(),
                    PreparedSpells = repertoire.preparedSpells.Where(IsAllowedSpell).ToArray(),
                    AutoPreparedSpells = repertoire.autoPreparedSpells.Where(IsAllowedSpell).ToArray(),
                    MagicAttackTrends = repertoire.magicAttackTrends
                        .Where(trend => !IsSnapshotExcludedOrigin(trend.sourceType))
                        .Select(trend => new TrendInfo(
                            trend.value,
                            trend.sourceType,
                            trend.sourceName,
                            trend.source))
                        .ToArray(),
                    SlotCapacities = new Dictionary<int, int>(repertoire.spellsSlotCapacities),
                    UsedSpellSlots = new Dictionary<int, int>(repertoire.usedSpellsSlots),
                    UsedMysticArcanum = new Dictionary<int, int>(repertoire.usedMysticArcanum),
                    LegacyAvailableSpellSlots =
                        new Dictionary<int, int>(repertoire.legacyAvailableSpellsSlots),
                    ExtraSpellsByTag = repertoire.extraSpellsByTag.ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<SpellDefinition>)pair.Value
                            .Where(IsAllowedSpell)
                            .ToArray(),
                        StringComparer.Ordinal)
                })
                .ToArray();
        }

        private static int GetPersistentSpellcastingAbilityBonus(
            RulesetCharacter source,
            RulesetSpellRepertoire repertoire,
            IReadOnlyDictionary<string, PersistentAttributeSeed> attributes)
        {
            var ability = repertoire.SpellCastingAbility;
            var score = attributes.TryGetValue(ability, out var attribute)
                ? attribute.FinalValue
                : source.TryGetAttributeValue(ability);

            return AttributeDefinitions.ComputeAbilityScoreModifier(score);
        }

        private static int GetPersistentSpellcastingValue(
            int currentValue,
            RulesetSpellRepertoire repertoire,
            RulesetCharacter source,
            IReadOnlyDictionary<string, PersistentAttributeSeed> attributes,
            int currentProficiencyBonus,
            int persistentProficiencyBonus,
            int excludedAffinityBonus)
        {
            var result = currentValue - excludedAffinityBonus;

            if (repertoire.SpellCastingFeature.SpellcatingParametersComputation !=
                SpellcastingParametersComputation.Dynamic)
            {
                return result;
            }

            var currentAbilityBonus = AttributeDefinitions.ComputeAbilityScoreModifier(
                source.TryGetAttributeValue(repertoire.SpellCastingAbility));
            var persistentAbilityBonus = GetPersistentSpellcastingAbilityBonus(
                source,
                repertoire,
                attributes);

            return result - currentAbilityBonus + persistentAbilityBonus -
                   currentProficiencyBonus + persistentProficiencyBonus;
        }

        private static void CopySpellRepertoires(
            RulesetCharacter duplicate,
            SimulacrumSnapshotSeed seed)
        {
            duplicate.SpellRepertoires.Clear();
            foreach (var sourceRepertoire in seed.Repertoires)
            {
                var repertoire = new RulesetSpellRepertoire(
                    sourceRepertoire.SpellCastingFeature,
                    sourceRepertoire.SpellCastingClass,
                    sourceRepertoire.SpellCastingSubclass,
                    sourceRepertoire.SpellCastingRace,
                    duplicate.CharacterInventory,
                    duplicate.Name)
                {
                    autoPreparedTag = sourceRepertoire.AutoPreparedTag,
                    formAbilityBonus = sourceRepertoire.FormAbilityBonus,
                    maxPreparedSpells = sourceRepertoire.MaxPreparedSpells,
                    saveDC = sourceRepertoire.SaveDc,
                    spellAttackBonus = sourceRepertoire.SpellAttackBonus,
                    spellCastingLevel = sourceRepertoire.SpellCastingLevel,
                    spellCastingMonster = sourceRepertoire.SpellCastingMonster
                };

                repertoire.knownCantrips.AddRange(sourceRepertoire.KnownCantrips);
                repertoire.knownSpells.AddRange(sourceRepertoire.KnownSpells);
                repertoire.preparedSpells.AddRange(sourceRepertoire.PreparedSpells);
                repertoire.autoPreparedSpells.AddRange(sourceRepertoire.AutoPreparedSpells);
                repertoire.magicAttackTrends.AddRange(sourceRepertoire.MagicAttackTrends);

                CopyDictionary(sourceRepertoire.SlotCapacities, repertoire.spellsSlotCapacities);
                CopyDictionary(sourceRepertoire.UsedSpellSlots, repertoire.usedSpellsSlots);
                CopyDictionary(sourceRepertoire.UsedMysticArcanum, repertoire.usedMysticArcanum);
                CopyDictionary(
                    sourceRepertoire.LegacyAvailableSpellSlots,
                    repertoire.legacyAvailableSpellsSlots);

                foreach (var taggedSpells in sourceRepertoire.ExtraSpellsByTag)
                {
                    repertoire.extraSpellsByTag[taggedSpells.Key] = taggedSpells.Value.ToList();
                }

                duplicate.SpellRepertoires.Add(repertoire);
            }
        }

        private void CaptureSpellRepertoires(RulesetCharacter character)
        {
            RepertoireAutoPreparedCounts.Clear();
            RepertoireAutoPreparedSpells.Clear();
            RepertoireAutoPreparedTags.Clear();
            RepertoireFormAbilityBonuses.Clear();
            RepertoireMaxPreparedSpells.Clear();
            RepertoireMonsterNames.Clear();
            RepertoireSaveDcs.Clear();
            RepertoireSlotCapacityCounts.Clear();
            RepertoireSlotCapacityLevels.Clear();
            RepertoireSlotCapacityValues.Clear();
            RepertoireSpellAttackBonuses.Clear();
            RepertoireSpellCastingLevels.Clear();

            foreach (var repertoire in character.SpellRepertoires)
            {
                RepertoireSpellCastingLevels.Add(repertoire.spellCastingLevel);
                RepertoireMaxPreparedSpells.Add(repertoire.maxPreparedSpells);
                RepertoireSpellAttackBonuses.Add(repertoire.spellAttackBonus);
                RepertoireFormAbilityBonuses.Add(repertoire.formAbilityBonus);
                RepertoireSaveDcs.Add(repertoire.saveDC);
                RepertoireAutoPreparedTags.Add(repertoire.autoPreparedTag ?? string.Empty);
                RepertoireAutoPreparedCounts.Add(repertoire.autoPreparedSpells.Count);
                RepertoireAutoPreparedSpells.AddRange(repertoire.autoPreparedSpells);
                RepertoireMonsterNames.Add(repertoire.spellCastingMonster?.Name ?? string.Empty);

                var capacities = repertoire.spellsSlotCapacities
                    .OrderBy(x => x.Key)
                    .ToArray();

                RepertoireSlotCapacityCounts.Add(capacities.Length);

                foreach (var capacity in capacities)
                {
                    RepertoireSlotCapacityLevels.Add(capacity.Key);
                    RepertoireSlotCapacityValues.Add(capacity.Value);
                }
            }
        }

        private void RestoreSpellRepertoires(RulesetCharacter duplicate)
        {
            var autoPreparedOffset = 0;
            var slotCapacityOffset = 0;

            for (var i = 0; i < duplicate.SpellRepertoires.Count; i++)
            {
                var repertoire = duplicate.SpellRepertoires[i];

                repertoire.CharacterInventory = duplicate.CharacterInventory;
                repertoire.CharacterName = duplicate.Name;

                var autoPreparedCount = Math.Max(
                    0,
                    GetValue(RepertoireAutoPreparedCounts, i));
                var slotCapacityCount = Math.Max(
                    0,
                    GetValue(RepertoireSlotCapacityCounts, i));

                if (i < RepertoireSpellCastingLevels.Count)
                {
                    repertoire.spellCastingLevel =
                        GetValue(RepertoireSpellCastingLevels, i, repertoire.spellCastingLevel);
                    repertoire.maxPreparedSpells =
                        GetValue(RepertoireMaxPreparedSpells, i, repertoire.maxPreparedSpells);
                    repertoire.spellAttackBonus =
                        GetValue(RepertoireSpellAttackBonuses, i, repertoire.spellAttackBonus);
                    repertoire.formAbilityBonus =
                        GetValue(RepertoireFormAbilityBonuses, i, repertoire.formAbilityBonus);
                    repertoire.saveDC =
                        GetValue(RepertoireSaveDcs, i, repertoire.saveDC);
                    repertoire.autoPreparedTag =
                        GetValue(RepertoireAutoPreparedTags, i, repertoire.autoPreparedTag);

                    var monsterName = GetValue(RepertoireMonsterNames, i);

                    repertoire.spellCastingMonster =
                        !string.IsNullOrEmpty(monsterName) &&
                        TryGetDefinition<MonsterDefinition>(monsterName, out var monster)
                            ? monster
                            : null;

                    repertoire.autoPreparedSpells.Clear();

                    for (var j = autoPreparedOffset;
                         j < Math.Min(
                             autoPreparedOffset + autoPreparedCount,
                             RepertoireAutoPreparedSpells.Count);
                         j++)
                    {
                        var spell = RepertoireAutoPreparedSpells[j];

                        if (IsAllowedSpell(spell))
                        {
                            repertoire.autoPreparedSpells.Add(spell);
                        }
                    }

                    repertoire.spellsSlotCapacities.Clear();

                    for (var j = 0; j < slotCapacityCount; j++)
                    {
                        var flatIndex = slotCapacityOffset + j;

                        if (flatIndex >= RepertoireSlotCapacityLevels.Count ||
                            flatIndex >= RepertoireSlotCapacityValues.Count)
                        {
                            break;
                        }

                        repertoire.spellsSlotCapacities[
                            RepertoireSlotCapacityLevels[flatIndex]] =
                            RepertoireSlotCapacityValues[flatIndex];
                    }
                }

                autoPreparedOffset += autoPreparedCount;
                slotCapacityOffset += slotCapacityCount;
            }
        }

        private static List<SourcePowerState> CollectSafeSourcePowers(
            RulesetCharacter source)
        {
            var featuresToBrowse = new List<FeatureDefinition>();
            var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

            source.EnumerateFeaturesToBrowse<FeatureDefinition>(
                featuresToBrowse,
                featuresOrigin);

            var definitions = EnumeratePersistentFeatureGraph(source, featuresOrigin)
                .OfType<FeatureDefinitionPower>()
                .ToHashSet();

            foreach (var feature in featuresToBrowse.Where(x =>
                         x != null && HasPersistentOrigin(featuresOrigin, x)))
            {
                var origin = featuresOrigin.TryGetValue(feature, out var existingOrigin)
                    ? existingOrigin
                    : CreateFallbackFeatureOrigin(source, feature);

                foreach (var power in EnumerateFeatureGraph(feature, origin)
                             .Select(x => x.Feature)
                             .OfType<FeatureDefinitionPower>())
                {
                    definitions.Add(power);
                }
            }

            foreach (var invocation in source.Invocations.Where(x => x?.invocationDefinition != null))
            {
                var grantedFeature = invocation.invocationDefinition.GrantedFeature;

                if (grantedFeature == null)
                {
                    continue;
                }

                var origin = new FeatureOrigin(
                    FeatureSourceType.Invocation,
                    invocation.invocationDefinition.Name,
                    invocation.invocationDefinition,
                    grantedFeature.ParseSpecialFeatureTags());

                foreach (var power in EnumerateFeatureGraph(grantedFeature, origin)
                             .Select(x => x.Feature)
                             .OfType<FeatureDefinitionPower>())
                {
                    definitions.Add(power);
                }
            }

            // Runtime actions can temporarily materialize bundle children in UsablePowers.
            // Persistent class/race/feat/invocation powers are already derived from their
            // feature graph above; accepting origin-less runtime entries here would snapshot
            // interrupted Restoring Touch, Weapon Mastery, or borrowed-knowledge subpowers.
            if (source.UsablePowers.Any(x =>
                    x?.PowerDefinition == SpellPointsContext.PowerSpellPoints))
            {
                definitions.Add(SpellPointsContext.PowerSpellPoints);
            }

            var pending = new Queue<FeatureDefinitionPower>(definitions);

            while (pending.Count > 0)
            {
                var definition = pending.Dequeue();

                if (definition is FeatureDefinitionPowerSharedPool sharedPool)
                {
                    AddPowerDefinition(sharedPool.GetUsagePoolPower(), definitions, pending);
                }
            }

            return definitions
                .Where(x => x != null)
                .OrderBy(GetPowerDependencyOrder)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Select(definition =>
                {
                    var usablePower = source.UsablePowers.FirstOrDefault(x =>
                                          x?.PowerDefinition == definition) ??
                                      PowerProvider.Get(definition, source);

                    return new SourcePowerState(source, definition, usablePower);
                })
                .ToList();
        }

        private static void AddPowerDefinition(
            FeatureDefinitionPower definition,
            HashSet<FeatureDefinitionPower> definitions,
            Queue<FeatureDefinitionPower> pending)
        {
            if (definition != null && definitions.Add(definition))
            {
                pending.Enqueue(definition);
            }
        }

        private static int GetPowerDependencyOrder(FeatureDefinitionPower power)
        {
            if (power is FeatureDefinitionPowerSharedPool)
            {
                return 3;
            }

            if (power.GetBundle() != null)
            {
                return 0;
            }

            return PowerBundle.GetMasterPowersBySubPower(power).Count == 0 ? 1 : 2;
        }

        private static void CopyUsablePowers(
            RulesetCharacter duplicate,
            IEnumerable<SourcePowerState> sourcePowers)
        {
            duplicate.UsablePowers.Clear();

            foreach (var sourceState in sourcePowers)
            {
                var power = PowerProvider.Get(sourceState.Definition, duplicate);

                power.originClass = sourceState.OriginClass;
                power.originRace = sourceState.OriginRace;
                power.spentPoints = sourceState.SpentPoints;
                PowerProvider.UpdateSaveDc(duplicate, power, sourceState.OriginClass);
                PowerProvider.RestoreRemainingUses(
                    duplicate,
                    power,
                    sourceState.EffectiveMaxUses,
                    sourceState.RemainingUses);
                duplicate.UsablePowers.Add(power);
            }

            if (duplicate is not RulesetCharacterSimulacrum simulacrum)
            {
                return;
            }

            // Source powers replace the shell's runtime collection. Re-materialize powers that
            // belong to the shell itself (currently Dismiss Simulacrum) after the copy so they
            // cannot be lost when the snapshot is first applied or refreshed.
            foreach (var definition in simulacrum.MonsterDefinition.Features
                         .OfType<FeatureDefinitionPower>()
                         .Where(definition => duplicate.UsablePowers.All(
                             power => power.PowerDefinition != definition)))
            {
                duplicate.UsablePowers.Add(PowerProvider.Get(definition, duplicate));
            }
        }

        private void CaptureInvocations(IReadOnlyList<InvocationSeed> sourceInvocations)
        {
            InvocationDefinitionNames.Clear();
            InvocationRepertoireIndices.Clear();
            InvocationActiveStates.Clear();
            InvocationUsedStates.Clear();

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var invocation in sourceInvocations)
            {
                var key = $"{invocation.Definition.Name}|{invocation.RepertoireIndex}";

                if (!seen.Add(key))
                {
                    continue;
                }

                InvocationDefinitionNames.Add(invocation.Definition.Name);
                InvocationRepertoireIndices.Add(invocation.RepertoireIndex);
                InvocationActiveStates.Add(invocation.Active);
                InvocationUsedStates.Add(invocation.Used);
            }
        }

        internal bool TryReplaceInvocation(
            RulesetCharacterSimulacrum duplicate,
            InvocationDefinition invocationToRemove,
            InvocationDefinition invocationToAdd)
        {
            var invocationIndex = InvocationDefinitionNames.FindIndex(name =>
                string.Equals(
                    name,
                    invocationToRemove.Name,
                    StringComparison.Ordinal));

            if (invocationIndex < 0 ||
                invocationIndex >= InvocationRepertoireIndices.Count ||
                invocationIndex >= InvocationActiveStates.Count ||
                invocationIndex >= InvocationUsedStates.Count)
            {
                return false;
            }

            var repertoireIndex = InvocationRepertoireIndices[invocationIndex];

            if (InvocationDefinitionNames
                .Select((name, index) => (name, index))
                .Any(pair =>
                    pair.index != invocationIndex &&
                    string.Equals(
                        pair.name,
                        invocationToAdd.Name,
                        StringComparison.Ordinal) &&
                    GetValue(InvocationRepertoireIndices, pair.index, -1) ==
                    repertoireIndex))
            {
                return false;
            }

            var oldFeatureGraph = EnumerateFeatureGraph(
                    invocationToRemove.GrantedFeature,
                    default)
                .Select(candidate => candidate.Feature)
                .Where(feature => feature != null)
                .ToArray();
            var newFeatureGraph = EnumerateFeatureGraph(
                    invocationToAdd.GrantedFeature,
                    default)
                .Select(candidate => candidate.Feature)
                .Where(IsSupportedCopiedFeature)
                .ToArray();

            // This runtime replacement is used by Weapon Mastery retraining. Invocation powers
            // carry independent resource state, while skill proficiencies require the snapshot's
            // bonus/rank tuples to be rebuilt atomically. Never partially replace either through
            // this feature-only path.
            if (oldFeatureGraph.OfType<FeatureDefinitionPower>().Any() ||
                newFeatureGraph.OfType<FeatureDefinitionPower>().Any() ||
                oldFeatureGraph.OfType<FeatureDefinitionProficiency>().Any() ||
                newFeatureGraph.OfType<FeatureDefinitionProficiency>().Any())
            {
                return false;
            }

            var copiedFeatureIndices = Enumerable.Range(0, CopiedFeatures.Count)
                .Where(index =>
                    GetValue(CopiedFeatureSourceTypes, index, -1) ==
                    (int)FeatureSourceType.Invocation &&
                    (string.Equals(
                         GetValue(CopiedFeatureSourceNames, index, string.Empty),
                         invocationToRemove.Name,
                         StringComparison.Ordinal) ||
                     string.Equals(
                         GetValue(
                             CopiedFeatureSourceDefinitionNames,
                             index,
                             string.Empty),
                         invocationToRemove.Name,
                         StringComparison.Ordinal)))
                .OrderByDescending(index => index)
                .ToArray();
            var removedFeatures = copiedFeatureIndices
                .Select(index => CopiedFeatures[index])
                .Where(feature => feature != null)
                .ToArray();

            foreach (var index in copiedFeatureIndices)
            {
                CopiedFeatures.RemoveAt(index);
                CopiedFeatureSourceTypes.RemoveAt(index);
                CopiedFeatureSourceNames.RemoveAt(index);
                CopiedFeatureSourceDefinitionNames.RemoveAt(index);
                CopiedFeatureSourceDefinitionTypes.RemoveAt(index);
            }

            InvocationDefinitionNames[invocationIndex] = invocationToAdd.Name;

            var origin = new FeatureOrigin(
                FeatureSourceType.Invocation,
                invocationToAdd.Name,
                invocationToAdd,
                invocationToAdd.GrantedFeature?.ParseSpecialFeatureTags());

            foreach (var feature in newFeatureGraph.Where(feature =>
                         !CopiedFeatures.Contains(feature)))
            {
                AddCopiedFeature(feature, origin);
            }

            foreach (var feature in removedFeatures.Where(feature =>
                         !CopiedFeatures.Contains(feature)))
            {
                duplicate.ActiveFeatures.Remove(feature);
                duplicate.FeaturesOrigin.Remove(feature);
            }

            RestoreInvocations(duplicate);

            Trace.Log(
                "Simulacrum invocation replaced duplicate={0} removed={1} added={2}",
                duplicate.Guid,
                invocationToRemove.Name,
                invocationToAdd.Name);

            return true;
        }

        private void RestoreInvocations(RulesetCharacter duplicate)
        {
            var existingInvocations = duplicate.Invocations
                .Where(x => x?.invocationDefinition != null)
                .ToLookup(x => x.invocationDefinition.Name, StringComparer.Ordinal);
            var reusedInvocations = new HashSet<RulesetInvocation>();
            var restoredInvocations = new List<RulesetInvocation>();

            for (var i = 0; i < InvocationDefinitionNames.Count; i++)
            {
                if (!TryGetDefinition<InvocationDefinition>(
                        InvocationDefinitionNames[i],
                        out var definition))
                {
                    continue;
                }

                var repertoireIndex = GetValue(InvocationRepertoireIndices, i, -1);
                var repertoire = repertoireIndex >= 0 &&
                                 repertoireIndex < duplicate.SpellRepertoires.Count
                    ? duplicate.SpellRepertoires[repertoireIndex]
                    : null;
                var invocation = existingInvocations[definition.Name]
                    .FirstOrDefault(x =>
                        !reusedInvocations.Contains(x) &&
                        x.invocationRepertoire == repertoire);

                if (invocation == null)
                {
                    invocation = new RulesetInvocation(definition, repertoire);
                }
                else
                {
                    reusedInvocations.Add(invocation);
                    invocation.invocationRepertoire = repertoire;
                }

                invocation.active = GetValue(InvocationActiveStates, i);
                invocation.used = GetValue(InvocationUsedStates, i);

                restoredInvocations.Add(invocation);
            }

            duplicate.Invocations.Clear();
            duplicate.Invocations.AddRange(restoredInvocations);
        }

        private void CaptureUsablePowers(
            IEnumerable<SourcePowerState> sourcePowers,
            RulesetCharacter character)
        {
            PowerDefinitionNames.Clear();
            PowerSaveDcs.Clear();
            PowerSpentPoints.Clear();
            PowerMaxUses.Clear();
            PowerRemainingUses.Clear();
            PowerOriginClassNames.Clear();
            PowerOriginRaceNames.Clear();
            ToggledPowerNames = character.ToggledPowersOn
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var sourcePowerGroups = sourcePowers
                .Where(state => state?.Definition != null)
                .GroupBy(state => state.Definition.Name, StringComparer.Ordinal)
                .OrderBy(group => GetPowerDependencyOrder(group.First().Definition))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();

            if (sourcePowerGroups.Any(group => group.Count() != 1))
            {
                throw new InvalidOperationException(
                    "Simulacrum source powers contain duplicate definition names.");
            }

            foreach (var sourceState in sourcePowerGroups.Select(group => group.Single()))
            {
                PowerDefinitionNames.Add(sourceState.Definition.Name);
                PowerSaveDcs.Add(sourceState.SaveDc);
                PowerSpentPoints.Add(sourceState.SpentPoints);
                PowerMaxUses.Add(sourceState.EffectiveMaxUses);
                PowerRemainingUses.Add(sourceState.RemainingUses);
                PowerOriginClassNames.Add(sourceState.OriginClass?.Name ?? string.Empty);
                PowerOriginRaceNames.Add(sourceState.OriginRace?.Name ?? string.Empty);
            }

            var sourcePowerNames = new HashSet<string>(
                PowerDefinitionNames,
                StringComparer.Ordinal);
            var shellPowerGroups = character.UsablePowers
                .Where(power => power?.PowerDefinition != null)
                .Where(power => !sourcePowerNames.Contains(power.PowerDefinition.Name))
                .GroupBy(power => power.PowerDefinition.Name, StringComparer.Ordinal)
                .OrderBy(group => GetPowerDependencyOrder(group.First().PowerDefinition))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();

            if (shellPowerGroups.Any(group => group.Count() != 1))
            {
                throw new InvalidOperationException(
                    "Simulacrum shell powers contain duplicate definition names.");
            }

            // Source resources must be captured from the authoritative seed above. At this
            // point the shell has not restored every copied max-use modifier yet, so reading
            // source powers back from it would clamp Second Wind, Healing Light, and shared
            // pools to provisional monster values. Only shell-owned powers such as Dismiss
            // are captured from the runtime collection.
            foreach (var power in shellPowerGroups.Select(group => group.Single()))
            {
                PowerDefinitionNames.Add(power.PowerDefinition.Name);
                PowerSaveDcs.Add(power.saveDC);
                PowerSpentPoints.Add(power.spentPoints);
                var effectiveMaxUses = PowerProvider.GetEffectiveMaxUses(character, power);

                PowerMaxUses.Add(effectiveMaxUses);
                PowerRemainingUses.Add(Math.Min(
                    effectiveMaxUses,
                    Math.Max(0, power.remainingUses)));
                PowerOriginClassNames.Add(power.originClass?.Name ?? string.Empty);
                PowerOriginRaceNames.Add(power.originRace?.Name ?? string.Empty);
            }
        }

        private void RestoreUsablePowers(RulesetCharacter duplicate)
        {
            foreach (var power in duplicate.UsablePowers
                         .Where(x => x?.PowerDefinition != null)
                         .OrderBy(x => GetPowerDependencyOrder(x.PowerDefinition))
                         .ThenBy(x => x.PowerDefinition.Name, StringComparer.Ordinal))
            {
                PowerProvider.BindUsesAttribute(duplicate, power);

                var index = PowerDefinitionNames.IndexOf(power.PowerDefinition.Name);

                if (index < 0)
                {
                    continue;
                }

                power.saveDC = GetValue(PowerSaveDcs, index, power.saveDC);
                power.spentPoints = GetValue(PowerSpentPoints, index, power.spentPoints);
                power.originClass =
                    !string.IsNullOrEmpty(PowerOriginClassNames[index]) &&
                    TryGetDefinition<CharacterClassDefinition>(
                        PowerOriginClassNames[index],
                        out var originClass)
                        ? originClass
                        : null;
                power.originRace =
                    !string.IsNullOrEmpty(PowerOriginRaceNames[index]) &&
                    TryGetDefinition<CharacterRaceDefinition>(
                        PowerOriginRaceNames[index],
                        out var originRace)
                        ? originRace
                        : null;
                PowerProvider.RestoreRemainingUses(
                    duplicate,
                    power,
                    GetValue(PowerMaxUses, index),
                    GetValue(PowerRemainingUses, index));
            }

            duplicate.ToggledPowersOn.Clear();
            duplicate.ToggledPowersOn.AddRange(ToggledPowerNames);
        }

        internal sealed class SourcePowerState
        {
            internal SourcePowerState(
                RulesetCharacter source,
                FeatureDefinitionPower definition,
                RulesetUsablePower usablePower)
            {
                Definition = definition;
                EffectiveMaxUses = PowerProvider.GetEffectiveMaxUses(source, usablePower);
                RemainingUses = Math.Min(
                    EffectiveMaxUses,
                    Math.Max(0, usablePower.remainingUses));
                SaveDc = usablePower.saveDC;
                SpentPoints = Math.Max(0, usablePower.spentPoints);
                OriginClass = usablePower.originClass;
                OriginRace = usablePower.originRace;
            }

            internal FeatureDefinitionPower Definition { get; }
            internal int EffectiveMaxUses { get; }
            internal int RemainingUses { get; }
            internal int SaveDc { get; }
            internal int SpentPoints { get; }
            internal CharacterClassDefinition OriginClass { get; }
            internal CharacterRaceDefinition OriginRace { get; }
        }

        private sealed class FeatureOriginCandidate(
            FeatureDefinition feature,
            FeatureOrigin origin)
        {
            internal FeatureDefinition Feature { get; } = feature;
            internal FeatureOrigin Origin { get; } = origin;
        }

        internal sealed class DuplicateRuntimeState
        {
            private readonly int _currentHitPoints;
            private readonly int _usedBardicInspiration;
            private readonly int _usedBindChain;
            private readonly int _usedChannelDivinity;
            private readonly int _usedHealingPool;
            private readonly int _usedIndomitableResistances;
            private readonly int _usedKiPoints;
            private readonly int _usedKnockOutImmunityPerLongRest;
            private readonly int _usedRagePoints;
            private readonly int _usedSorceryPoints;
            private readonly InvocationRuntimeState[] _invocationStates;
            private readonly SpellResourceState[] _spellResources;
            private readonly PowerResourceState[] _powerResources;
            private readonly string[] _toggledPowersOn;

            internal DuplicateRuntimeState(RulesetCharacter duplicate, int currentHitPoints)
            {
                _currentHitPoints = currentHitPoints;
                _usedBardicInspiration = duplicate.UsedBardicInspiration;
                _usedBindChain = duplicate.UsedBindChain;
                _usedChannelDivinity = duplicate.UsedChannelDivinity;
                _usedHealingPool = duplicate.UsedHealingPool;
                _usedIndomitableResistances = duplicate.UsedIndomitableResistances;
                _usedKiPoints = duplicate.UsedKiPoints;
                _usedKnockOutImmunityPerLongRest = duplicate.UsedKnockOutImmunityPerLongRest;
                _usedRagePoints = duplicate.UsedRagePoints;
                _usedSorceryPoints = duplicate.UsedSorceryPoints;
                _invocationStates = duplicate.Invocations
                    .Where(invocation => invocation?.InvocationDefinition != null)
                    .Select(invocation => new InvocationRuntimeState(
                        invocation.InvocationDefinition.Name,
                        invocation.InvocationRepertoire == null
                            ? -1
                            : duplicate.SpellRepertoires.IndexOf(
                                invocation.InvocationRepertoire),
                        invocation.Active,
                        invocation.Used))
                    .ToArray();
                _spellResources = duplicate.SpellRepertoires
                    .Select(x => new SpellResourceState(x))
                    .ToArray();
                _powerResources = duplicate.UsablePowers
                    .Where(x => x?.PowerDefinition != null)
                    .Select(x => new PowerResourceState(duplicate, x))
                    .ToArray();
                _toggledPowersOn = duplicate.ToggledPowersOn
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }

            internal void Restore(
                RulesetCharacter duplicate,
                bool restorePowerResources = true)
            {
                RestoreInvocationStates(duplicate);

                foreach (var spellResource in _spellResources)
                {
                    spellResource.Restore();
                }

                if (restorePowerResources)
                {
                    foreach (var powerResource in _powerResources)
                    {
                        powerResource.Restore();
                    }
                }

                duplicate.ToggledPowersOn.Clear();
                duplicate.ToggledPowersOn.AddRange(_toggledPowersOn);

                duplicate.UsedBardicInspiration = _usedBardicInspiration;
                duplicate.UsedBindChain = _usedBindChain;
                duplicate.UsedChannelDivinity = _usedChannelDivinity;
                duplicate.UsedHealingPool = _usedHealingPool;
                duplicate.UsedIndomitableResistances = _usedIndomitableResistances;
                duplicate.UsedKiPoints = _usedKiPoints;
                duplicate.UsedKnockOutImmunityPerLongRest = _usedKnockOutImmunityPerLongRest;
                duplicate.UsedRagePoints = _usedRagePoints;
                duplicate.UsedSorceryPoints = _usedSorceryPoints;

                var maximumHitPoints = duplicate.TryGetAttributeValue(AttributeDefinitions.HitPoints);

                duplicate.CurrentHitPoints =
                    Math.Min(Math.Max(0, _currentHitPoints), maximumHitPoints);
            }

            internal void RestoreInvocationStates(RulesetCharacter duplicate)
            {
                if (duplicate == null || _invocationStates.Length == 0)
                {
                    return;
                }

                var unmatchedInvocations = duplicate.Invocations
                    .Where(invocation => invocation?.InvocationDefinition != null)
                    .ToList();

                foreach (var state in _invocationStates)
                {
                    var invocation = unmatchedInvocations.FirstOrDefault(candidate =>
                        state.Matches(duplicate, candidate));

                    if (invocation == null)
                    {
                        continue;
                    }

                    invocation.active = state.Active;
                    invocation.used = state.Used;
                    unmatchedInvocations.Remove(invocation);
                }
            }

            private sealed class InvocationRuntimeState(
                string definitionName,
                int repertoireIndex,
                bool active,
                bool used)
            {
                internal bool Active { get; } = active;
                internal bool Used { get; } = used;

                internal bool Matches(
                    RulesetCharacter duplicate,
                    RulesetInvocation invocation)
                {
                    if (!string.Equals(
                            invocation?.InvocationDefinition?.Name,
                            definitionName,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var candidateRepertoireIndex =
                        invocation.InvocationRepertoire == null
                            ? -1
                            : duplicate.SpellRepertoires.IndexOf(
                                invocation.InvocationRepertoire);

                    return candidateRepertoireIndex == repertoireIndex;
                }
            }

            private sealed class SpellResourceState
            {
                private readonly RulesetSpellRepertoire _repertoire;
                private readonly Dictionary<int, int> _usedMysticArcanum;
                private readonly Dictionary<int, int> _usedSpellSlots;

                internal SpellResourceState(RulesetSpellRepertoire repertoire)
                {
                    _repertoire = repertoire;
                    _usedMysticArcanum = new Dictionary<int, int>(repertoire.usedMysticArcanum);
                    _usedSpellSlots = new Dictionary<int, int>(repertoire.usedSpellsSlots);
                }

                internal void Restore()
                {
                    CopyDictionary(_usedMysticArcanum, _repertoire.usedMysticArcanum);
                    CopyDictionary(_usedSpellSlots, _repertoire.usedSpellsSlots);
                }
            }

            private sealed class PowerResourceState
            {
                private readonly RulesetCharacter _character;
                private readonly RulesetUsablePower _power;
                private readonly int _maxUses;
                private readonly int _remainingUses;
                private readonly int _saveDc;
                private readonly int _spentPoints;

                internal PowerResourceState(
                    RulesetCharacter character,
                    RulesetUsablePower power)
                {
                    _character = character;
                    _power = power;
                    _maxUses = PowerProvider.GetEffectiveMaxUses(character, power);
                    _remainingUses = power.remainingUses;
                    _saveDc = power.saveDC;
                    _spentPoints = power.spentPoints;
                }

                internal void Restore()
                {
                    _power.saveDC = _saveDc;
                    _power.spentPoints = _spentPoints;
                    PowerProvider.RestoreRemainingUses(
                        _character,
                        _power,
                        _maxUses,
                        _remainingUses);
                }
            }
        }

        private void RestoreAttackTags(RulesetAttackMode attackMode, int attackIndex)
        {
            var count = Math.Max(0, GetValue(AttackTagCounts, attackIndex));
            var start = GetFlatStart(AttackTagCounts, attackIndex);

            attackMode.AttackTags.Clear();

            for (var i = start; i < Math.Min(start + count, AttackTags.Count); i++)
            {
                attackMode.AttackTags.Add(AttackTags[i]);
            }
        }

        private void RestoreDamageForms(EffectDescription effectDescription, int attackIndex)
        {
            var count = Math.Max(0, GetValue(AttackDamageFormCounts, attackIndex));
            var start = GetFlatStart(AttackDamageFormCounts, attackIndex);
            var damageEffectForms = effectDescription.EffectForms
                .Where(x => x.FormType == EffectForm.EffectFormType.Damage)
                .ToList();

            foreach (var surplusForm in damageEffectForms.Skip(count))
            {
                effectDescription.EffectForms.Remove(surplusForm);
            }

            for (var i = 0; i < count; i++)
            {
                var flatIndex = start + i;
                EffectForm damageEffectForm;

                if (i < damageEffectForms.Count)
                {
                    damageEffectForm = damageEffectForms[i];
                }
                else
                {
                    damageEffectForm = EffectFormBuilder.DamageForm();
                    effectDescription.EffectForms.Add(damageEffectForm);
                }

                var damageForm = damageEffectForm.DamageForm;

                damageForm.DamageType = GetValue(AttackDamageTypes, flatIndex);
                damageForm.DieType =
                    (DieType)GetValue(AttackDamageDieTypes, flatIndex, (int)DieType.D1);
                damageForm.versatile = GetValue(AttackDamageVersatile, flatIndex);
                damageForm.VersatileDieType =
                    (DieType)GetValue(
                        AttackDamageVersatileDieTypes,
                        flatIndex,
                        (int)DieType.D1);
                damageForm.DiceNumber = GetValue(AttackDamageDiceNumbers, flatIndex);
                damageForm.BonusDamage = GetValue(AttackDamageBonuses, flatIndex);
            }
        }

        private static void RefreshEncumbrance(
            RulesetCharacterSimulacrum duplicate)
        {
            if (duplicate.CharacterInventory == null)
            {
                return;
            }

            var carriedWeight = duplicate.CharacterInventory.ComputeCarriedWeight();

            duplicate.ComputeEncumbranceThresholds(
                out var encumberedThreshold,
                out var heavilyEncumberedThreshold,
                out _);

            var movementFeatures = new List<FeatureDefinition>();

            duplicate.EnumerateFeaturesToBrowse<IMovementAffinityProvider>(
                movementFeatures,
                null);

            var movementProviders = movementFeatures
                .OfType<IMovementAffinityProvider>()
                .ToArray();
            var encumbranceImmune = movementProviders.Any(provider =>
                provider.EncumbranceImmunity);
            var heavyArmorImmune = movementProviders.Any(provider =>
                provider.HeavyArmorImmunity);
            var heavyArmorOverload = false;

            if (!heavyArmorImmune &&
                duplicate.CharacterInventory.InventorySlotsByName.TryGetValue(
                    EquipmentDefinitions.SlotTypeTorso,
                    out var torsoSlot) &&
                TryGetActiveEquipmentItem(
                    EquipmentDefinitions.SlotTypeTorso,
                    torsoSlot,
                    out var torsoArmor) &&
                torsoArmor.ItemDefinition.ArmorDescription is
                {
                    RequiresMinimalStrength: true
                } armorDescription &&
                armorDescription.ArmorTypeDefinition?.ArmorCategory ==
                EquipmentDefinitions.HeavyArmorCategory)
            {
                heavyArmorOverload =
                    duplicate.TryGetAttributeValue(AttributeDefinitions.Strength) <
                    armorDescription.MinimalStrength;
            }

            var settings = ServiceRepository.GetService<IGameSettingsService>();
            var usesVariantEncumbrance =
                settings == null ||
                string.Equals(
                    settings.EncumbranceRuleType,
                    "Variant",
                    StringComparison.Ordinal);
            var heavilyEncumbered =
                usesVariantEncumbrance &&
                !encumbranceImmune &&
                carriedWeight >= heavilyEncumberedThreshold;
            var encumbered =
                usesVariantEncumbrance &&
                !encumbranceImmune &&
                !heavilyEncumbered &&
                carriedWeight >= encumberedThreshold;

            SetEncumbranceCondition(
                duplicate,
                ConditionDefinitions.ConditionEncumbered,
                encumbered);
            SetEncumbranceCondition(
                duplicate,
                ConditionDefinitions.ConditionHeavilyEncumbered,
                heavilyEncumbered);
            SetEncumbranceCondition(
                duplicate,
                GetDefinition<ConditionDefinition>("ConditionHeavyArmorOverload"),
                heavyArmorOverload);

            if (!encumbered && !heavilyEncumbered && !heavyArmorOverload)
            {
                duplicate.RemoveConditionCategoryAsNeeded(
                    AttributeDefinitions.TagEncumbrance);
            }
        }

        private static void SetEncumbranceCondition(
            RulesetCharacterSimulacrum duplicate,
            ConditionDefinition conditionDefinition,
            bool active)
        {
            if (!active)
            {
                duplicate.RemoveAllConditionsOfCategoryAndType(
                    AttributeDefinitions.TagEncumbrance,
                    conditionDefinition.Name,
                    false);

                return;
            }

            if (duplicate.HasConditionOfCategoryAndType(
                    AttributeDefinitions.TagEncumbrance,
                    conditionDefinition.Name))
            {
                return;
            }

            duplicate.AddConditionCategoryAsNeeded(
                AttributeDefinitions.TagEncumbrance);
            duplicate.AddConditionOfCategory(
                AttributeDefinitions.TagEncumbrance,
                RulesetCondition.CreateCondition(
                    duplicate.Guid,
                    conditionDefinition,
                    1,
                    0,
                    0,
                    0),
                false,
                true);
        }

        private void RestoreMovementAndSenses(RulesetCharacterMonster duplicate)
        {
            var transientFeatures = new List<FeatureDefinition>();
            var featureOrigins = new Dictionary<FeatureDefinition, FeatureOrigin>();
            var summoningBaselineConditions =
                CollectSummoningBaselineConditions(duplicate);

            duplicate.EnumerateFeaturesToBrowse<FeatureDefinition>(
                transientFeatures,
                featureOrigins);

            var transientProviders = transientFeatures
                .Where(feature =>
                    featureOrigins.TryGetValue(feature, out var origin) &&
                    IsSnapshotExcludedOrigin(
                        origin,
                        summoningBaselineConditions))
                .Distinct()
                .ToArray();
            var restoredMoveModes = new Dictionary<int, int>();

            for (var i = 0; i < Math.Min(MovementModeTypes.Count, MovementSpeeds.Count); i++)
            {
                restoredMoveModes[MovementModeTypes[i]] = MovementSpeeds[i];
            }

            foreach (var provider in transientProviders.OfType<IMoveModesProvider>())
            {
                provider.RefreshMoveModes(restoredMoveModes);
            }

            duplicate.MoveModes.Clear();

            foreach (var pair in restoredMoveModes)
            {
                duplicate.MoveModes[pair.Key] = pair.Value;
            }

            var senseCount = Math.Min(
                SenseTypes.Count,
                Math.Min(SenseRanges.Count, SenseStealthBreakerRanges.Count));
            var restoredSenseModes = new List<SenseMode>();

            for (var i = 0; i < senseCount; i++)
            {
                restoredSenseModes.Add(new SenseMode(
                    (SenseMode.Type)SenseTypes[i],
                    SenseRanges[i],
                    SenseStealthBreakerRanges[i]));
            }

            foreach (var provider in transientProviders.OfType<ISenseModesProvider>())
            {
                provider.RefreshSenseModes(restoredSenseModes);
            }

            duplicate.SenseModes.Clear();
            duplicate.SenseModes.AddRange(restoredSenseModes);
        }

        private void RestoreSkillAndSavingThrowBonuses(RulesetCharacterMonster duplicate)
        {
            duplicate.SkillProficiencies.Clear();

            for (var i = 0; i < Math.Min(SkillNames.Count, SkillBonuses.Count); i++)
            {
                duplicate.SkillProficiencies[SkillNames[i]] = SkillBonuses[i];
            }

            if (duplicate is RulesetCharacterSimulacrum simulacrum)
            {
                simulacrum.RestoreSkillProficiencySnapshot(
                    SkillNames,
                    SkillProficiencyRanks);
            }

            duplicate.SavingThrowProficiencies.Clear();

            for (var i = 0;
                 i < Math.Min(SaveAbilityNames.Count, SaveProficiencyDeltas.Count);
                 i++)
            {
                var abilityScore = SaveAbilityNames[i];
                var abilityIndex = Array.IndexOf(
                    AttributeDefinitions.AbilityScoreNames,
                    abilityScore);
                var definitionAbilityModifier =
                    abilityIndex >= 0 &&
                    abilityIndex < duplicate.MonsterDefinition.AbilityScores.Length
                        ? AttributeDefinitions.ComputeAbilityScoreModifier(
                            duplicate.MonsterDefinition.AbilityScores[abilityIndex])
                        : 0;

                // Monster saving throws add the live ability modifier and then
                // subtract the MonsterDefinition's base ability modifier from
                // this dictionary value. Store only that base plus the captured
                // proficiency/feature delta; adding the live modifier here
                // would count it twice.
                duplicate.SavingThrowProficiencies[abilityScore] =
                    definitionAbilityModifier + SaveProficiencyDeltas[i];
            }
        }

        private static void ReturnAttackModes(RulesetCharacter duplicate)
        {
            foreach (var attackMode in duplicate.AttackModes)
            {
                attackMode.Return();
            }

            duplicate.AttackModes.Clear();
        }

        private BaseDefinition ResolveAttackDefinition(int index)
        {
            if (index >= AttackDefinitionKinds.Count || index >= AttackDefinitionNames.Count)
            {
                return null;
            }

            var name = AttackDefinitionNames[index];

            return AttackDefinitionKinds[index] switch
            {
                MonsterAttackKind when TryGetDefinition<MonsterAttackDefinition>(name, out var attack) => attack,
                ItemAttackKind when TryGetDefinition<ItemDefinition>(name, out var item) => item,
                _ => null
            };
        }

        private static EffectDescription GetAttackEffectDescription(BaseDefinition sourceDefinition)
        {
            return sourceDefinition switch
            {
                MonsterAttackDefinition attack => attack.EffectDescription,
                ItemDefinition { IsWeapon: true } item => item.WeaponDescription.EffectDescription,
                _ => null
            };
        }

        private static void SetBaseAttribute(
            RulesetCharacter character,
            string attributeName,
            int value)
        {
            if (character.TryGetAttribute(attributeName, out var attribute))
            {
                attribute.BaseValue = value;
                attribute.Refresh();
            }
        }

        private static int GetPersistentAttributeValue(
            RulesetCharacter character,
            string attributeName,
            int fallbackValue = 0,
            IDictionary<string, int> cache = null,
            ISet<string> visiting = null,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions = null)
        {
            if (cache?.TryGetValue(attributeName, out var cachedValue) == true)
            {
                return cachedValue;
            }

            if (!character.TryGetAttribute(attributeName, out var attribute))
            {
                return fallbackValue;
            }

            visiting ??= new HashSet<string>(StringComparer.Ordinal);

            if (!visiting.Add(attributeName))
            {
                throw new SnapshotValidationException(
                    $"Cyclic Simulacrum attribute dependency detected for '{attributeName}'.");
            }

            RulesetAttribute copy = null;
            var removedModifiers = new List<RulesetAttributeModifier>();

            try
            {
                var retainedSummoningModifiers =
                    SelectSummoningBaselineAttributeModifiers(
                        character,
                        attribute,
                        attributeName,
                        summoningBaselineConditions);
                var excludedModifiers = attribute.ActiveModifiers
                    .Where(modifier =>
                        !retainedSummoningModifiers.Contains(modifier) &&
                        ResolveAttributeModifierDisposition(
                                attribute,
                                modifier,
                                attributeName,
                                summoningBaselineConditions) ==
                            AttributeModifierDisposition.Exclude)
                    .ToHashSet();

                copy = attribute.GetCopy(null);

                for (var i = attribute.ActiveModifiers.Count - 1; i >= 0; i--)
                {
                    var modifier = attribute.ActiveModifiers[i];

                    if (!excludedModifiers.Contains(modifier))
                    {
                        if (!string.IsNullOrEmpty(modifier.SourceAbility) &&
                            modifier.SourceAbility != attributeName)
                        {
                            var currentAbilityModifier =
                                AttributeDefinitions.ComputeAbilityScoreModifier(
                                    character.TryGetAttributeValue(modifier.SourceAbility));
                            var persistentAbilityModifier =
                                AttributeDefinitions.ComputeAbilityScoreModifier(
                                    GetPersistentAttributeValue(
                                        character,
                                        modifier.SourceAbility,
                                        0,
                                        cache,
                                        visiting,
                                        summoningBaselineConditions));

                            copy.ActiveModifiers[i].Value +=
                                persistentAbilityModifier - currentAbilityModifier;
                        }

                        continue;
                    }

                    removedModifiers.Add(copy.ActiveModifiers[i]);
                    copy.ActiveModifiers.RemoveAt(i);
                }

                RulesetAttributeModifier.SortAttributeModifiersList(copy.ActiveModifiers);
                copy.Refresh();

                var result = copy.CurrentValue;

                if (cache != null)
                {
                    cache[attributeName] = result;
                }

                return result;
            }
            finally
            {
                visiting.Remove(attributeName);
                RulesetAttributeModifier.ReleaseAttributeModifiers(removedModifiers);

                if (copy != null)
                {
                    attribute.ReleaseCopy();
                }
            }
        }

        private static AttributeModifierDisposition ResolveAttributeModifierDisposition(
            RulesetAttribute attribute,
            RulesetAttributeModifier modifier,
            string attributeName,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            var matchingTrends = attribute.ValueTrends
                .Where(trend => ReferenceEquals(trend.attributeModifier, modifier))
                .ToArray();
            var origins = matchingTrends
                .Select(trend => trend.sourceType)
                .Distinct()
                .ToArray();

            if (origins.Length > 0)
            {
                var hasExcludedOrigin = matchingTrends.Any(trend =>
                    IsSnapshotExcludedTrend(
                        trend,
                        summoningBaselineConditions));
                var hasRetainedOrigin = matchingTrends.Any(trend =>
                    !IsSnapshotExcludedTrend(
                        trend,
                        summoningBaselineConditions));

                if (hasExcludedOrigin && hasRetainedOrigin)
                {
                    throw new SnapshotValidationException(
                        $"Conflicting Simulacrum attribute modifier origins for '{attributeName}' " +
                        $"({string.Join(",", origins.OrderBy(origin => origin))}).");
                }

                if (hasExcludedOrigin)
                {
                    return AttributeModifierDisposition.Exclude;
                }

                // Situational attribute features are restored as live
                // definitions. Exclude their currently active contribution
                // from the baked baseline so concentration and similar
                // predicates can turn them on and off normally.
                return matchingTrends.Any(trend =>
                        trend.source is FeatureDefinitionAttributeModifier attributeModifier &&
                        IsRuntimeAttributeModifier(attributeModifier))
                    ? AttributeModifierDisposition.Exclude
                    : AttributeModifierDisposition.Retain;
            }

            var tags = (modifier.Tags ?? [])
                .Where(tag => !string.IsNullOrEmpty(tag))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToArray();
            var hasRetainedTag = tags.Any(tag =>
                MatchesAttributeModifierTag(tag, RetainedAttributeModifierTagPrefixes));
            var hasExcludedTag = tags.Any(tag =>
                MatchesAttributeModifierTag(tag, ExcludedAttributeModifierTagPrefixes));

            if (hasRetainedTag && hasExcludedTag)
            {
                throw new SnapshotValidationException(
                    $"Conflicting Simulacrum attribute modifier tags for '{attributeName}' " +
                    $"({string.Join(",", tags)}).");
            }

            if (hasExcludedTag)
            {
                return AttributeModifierDisposition.Exclude;
            }

            return AttributeModifierDisposition.Retain;
        }

        private static IReadOnlyCollection<RulesetAttributeModifier>
            SelectSummoningBaselineAttributeModifiers(
            RulesetCharacter character,
            RulesetAttribute attribute,
            string attributeName,
            IReadOnlyCollection<RulesetCondition> summoningBaselineConditions)
        {
            if (character == null ||
                attribute == null ||
                string.IsNullOrEmpty(attributeName) ||
                summoningBaselineConditions is not { Count: > 0 })
            {
                return [];
            }

            var baselineConditionSet = summoningBaselineConditions.ToHashSet();
            var baselineConditionTags = character.ConditionsByCategory
                .Where(pair => pair.Value.Any(baselineConditionSet.Contains))
                .Select(pair => pair.Key)
                .Where(tag => !string.IsNullOrEmpty(tag))
                .ToHashSet(StringComparer.Ordinal);

            if (baselineConditionTags.Count == 0)
            {
                return [];
            }

            var expectedModifiers = summoningBaselineConditions
                .SelectMany(condition =>
                    condition.ConditionDefinition.Features
                        .Where(feature => feature != null)
                        .SelectMany(feature =>
                            EnumerateFeatureGraph(
                                feature,
                                new FeatureOrigin(
                                    FeatureSourceType.Condition,
                                    condition.ConditionDefinition.Name,
                                    condition.ConditionDefinition,
                                    null)))
                        .Select(candidate => candidate.Feature)
                        .OfType<FeatureDefinitionAttributeModifier>()
                        .Where(definition =>
                            definition.ModifierOperation ==
                            FeatureDefinitionAttributeModifier.AttributeModifierOperation
                                .AddConditionAmount &&
                            (definition.ModifiedAttribute == attributeName ||
                             definition.ModifiedAttribute ==
                             AttributeDefinitions.AllAbilityScores))
                        .Select(definition => (
                            ModifierOperation:
                            NormalizeConditionAttributeModifierOperation(
                                attributeName,
                                definition.ModifierOperation),
                            Amount: condition.Amount)))
                .GroupBy(candidate => candidate)
                .ToDictionary(group => group.Key, group => group.Count());
            var selected = new HashSet<RulesetAttributeModifier>();
            var candidates = attribute.ActiveModifiers
                .Where(modifier =>
                    (modifier.Tags ?? []).Any(baselineConditionTags.Contains))
                .ToArray();

            foreach (var expected in expectedModifiers)
            {
                var exactMatches = candidates
                    .Where(modifier =>
                        expected.Key.ModifierOperation == modifier.Operation &&
                        Math.Abs(modifier.Value - expected.Key.Amount) < 0.001f)
                    .ToArray();

                // A RulesetAttributeModifier does not retain its source condition.
                // Only bake the category match when it is one-to-one as a multiset;
                // otherwise a same-valued transient condition could be mistaken for
                // the summoning baseline.
                if (exactMatches.Length != expected.Value)
                {
                    continue;
                }

                selected.UnionWith(exactMatches);
            }

            return selected;
        }

        private static FeatureDefinitionAttributeModifier.AttributeModifierOperation
            NormalizeConditionAttributeModifierOperation(
                string attributeName,
                FeatureDefinitionAttributeModifier.AttributeModifierOperation operation)
        {
            // RefreshArmorClassInFeatures converts AddConditionAmount to an
            // additive runtime modifier after resolving the condition amount.
            // Other attributes retain the declared operation.
            return attributeName == AttributeDefinitions.ArmorClass &&
                   operation ==
                   FeatureDefinitionAttributeModifier.AttributeModifierOperation
                       .AddConditionAmount
                ? FeatureDefinitionAttributeModifier.AttributeModifierOperation.Additive
                : operation;
        }

        private static void RemoveSummoningBaselineAttributeModifiers(
            RulesetCharacterSimulacrum duplicate)
        {
            var summoningBaselineConditions =
                CollectSummoningBaselineConditions(duplicate);

            if (summoningBaselineConditions.Count == 0)
            {
                return;
            }

            foreach (var pair in duplicate.Attributes)
            {
                var removed = SelectSummoningBaselineAttributeModifiers(
                        duplicate,
                        pair.Value,
                        pair.Key,
                        summoningBaselineConditions)
                    .ToList();

                if (removed.Count == 0)
                {
                    continue;
                }

                foreach (var modifier in removed)
                {
                    pair.Value.ActiveModifiers.Remove(modifier);
                }

                var removedSet = removed.ToHashSet();

                pair.Value.ValueTrends.RemoveAll(trend =>
                    removedSet.Contains(trend.attributeModifier));
                RulesetAttributeModifier.ReleaseAttributeModifiers(removed);
                RulesetAttributeModifier.SortAttributeModifiersList(
                    pair.Value.ActiveModifiers);
                pair.Value.Refresh();
            }
        }

        private static bool MatchesAttributeModifierTag(
            string tag,
            IEnumerable<string> prefixes)
        {
            return prefixes.Any(prefix =>
                tag.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool IsAllowedSpell(SpellDefinition spell)
        {
            return spell != null && spell != _spellDefinition;
        }

        private static void CopyDictionary<TKey, TValue>(
            IReadOnlyDictionary<TKey, TValue> source,
            Dictionary<TKey, TValue> destination)
        {
            destination.Clear();

            foreach (var entry in source)
            {
                destination[entry.Key] = entry.Value;
            }
        }

        private static T GetValue<T>(IReadOnlyList<T> values, int index, T defaultValue = default)
        {
            return index >= 0 && index < values.Count ? values[index] : defaultValue;
        }

        private static int GetFlatStart(IReadOnlyList<int> counts, int index)
        {
            var start = 0;

            for (var i = 0; i < Math.Min(index, counts.Count); i++)
            {
                start = checked(start + Math.Max(0, counts[i]));
            }

            return start;
        }

        private static bool AllCountsEqual(int expected, params int[] counts)
        {
            return counts.All(count => count == expected);
        }

        private static bool TryValidateFlatCounts(
            IReadOnlyList<int> counts,
            int maximumPerEntry,
            int maximumTotal,
            int actualCount,
            string label,
            out string failure)
        {
            failure = null;
            var total = 0;

            foreach (var count in counts)
            {
                if (count < 0 || count > maximumPerEntry)
                {
                    failure = $"{label} count is outside the safe range";

                    return false;
                }

                try
                {
                    total = checked(total + count);
                }
                catch (OverflowException)
                {
                    failure = $"{label} count overflowed";

                    return false;
                }

                if (total > maximumTotal)
                {
                    failure = $"{label} count exceeds the safe limit";

                    return false;
                }
            }

            if (total == actualCount)
            {
                return true;
            }

            failure = $"{label} list shape is invalid";

            return false;
        }
    }
}
