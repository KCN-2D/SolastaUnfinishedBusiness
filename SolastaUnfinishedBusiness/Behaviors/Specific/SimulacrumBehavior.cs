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

internal sealed partial class SimulacrumBehavior :
    ICustomSummonFormHandler,
    ICustomSummonCharacterConstructionHandler,
    IFilterTargetingCharacter,
    IValidateMagicEffectBeforeSpend,
    IOnBeforeEffectTerminated
{
    internal const string RubyMaterialTag = "MaterialSimulacrumRuby";
    internal const int RepairCostPerHitPoint = 100;
    internal const string RepairPowerName = "PowerSimulacrumRepair";
    internal const string DismissPowerName = "PowerSimulacrumDismiss";

    private const string SnapshotCategory = "SimulacrumSnapshot";
    private const string FailureInvalidTarget = "Failure/&SimulacrumTargetMustBeFriendlyBeastOrHumanoid";
    private const string FailureDuplicateTarget = "Failure/&SimulacrumCannotDuplicateSimulacrum";
    private const string FailureDependentTarget =
        "Failure/&SimulacrumTargetDependsOnCurrentSimulacrum";
    private const string FailureCannotDuplicateTarget =
        "Failure/&SimulacrumTargetCannotBeDuplicated";
    private const string FailureCannotCast = "Failure/&SimulacrumCannotCastSimulacrum";
    private const string FailureNotFound = "Failure/&SimulacrumNotFound";
    private const string FailureRepairMaterials = "Failure/&SimulacrumInsufficientRepairMaterials";
    private const float VisualRefreshCallbackTimeoutSeconds = 5f;
    private const int VisualRefreshMaximumConsecutiveFailures = 6;
    private const int VisualRefreshMinimumRetryDelayFrames = 15;
    private const int VisualRefreshMaximumRetryDelayFrames = 120;

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, string> MonsterBodyAssetPrefix =
        AccessTools.FieldRefAccess<RulesetCharacterMonster, string>("bodyAssetPrefix");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, string> MonsterArmorAssetPrefix =
        AccessTools.FieldRefAccess<RulesetCharacterMonster, string>("armorAssetPrefix");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, string> MonsterHelmetAssetPrefix =
        AccessTools.FieldRefAccess<RulesetCharacterMonster, string>("helmetAssetPrefix");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, string> MonsterFaceShapeAssetPrefix =
        AccessTools.FieldRefAccess<RulesetCharacterMonster, string>("faceShapeAssetPrefix");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, string> MonsterBeardShapeAssetPrefix =
        AccessTools.FieldRefAccess<RulesetCharacterMonster, string>("beardShapeAssetPrefix");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, string> MonsterHairShapeAssetPrefix =
        AccessTools.FieldRefAccess<RulesetCharacterMonster, string>("hairShapeAssetPrefix");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, string> MonsterHornsTailAssetPrefix =
        AccessTools.FieldRefAccess<RulesetCharacterMonster, string>("hornsTailAssetPrefix");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, HumanoidMonsterPresentationDefinition>
        HumanoidPresentation =
            AccessTools.FieldRefAccess<RulesetCharacterMonster, HumanoidMonsterPresentationDefinition>(
                "humanoidMonsterPresentationDefinition");

    private static readonly AccessTools.FieldRef<RulesetCharacterMonster, MonsterPresentationDefinition>
        MonsterPresentation =
            AccessTools.FieldRefAccess<RulesetCharacterMonster, MonsterPresentationDefinition>(
                "monsterPresentationDefinition");

    private static readonly HashSet<string> SnapshotAttributeNames =
    [
        AttributeDefinitions.Strength,
        AttributeDefinitions.Dexterity,
        AttributeDefinitions.Constitution,
        AttributeDefinitions.Intelligence,
        AttributeDefinitions.Wisdom,
        AttributeDefinitions.Charisma,
        AttributeDefinitions.ArmorClass,
        AttributeDefinitions.HitPoints,
        AttributeDefinitions.CharacterLevel,
        AttributeDefinitions.ProficiencyBonus,
        AttributeDefinitions.AttacksNumber
    ];

    private static readonly RuntimeRestrictions RuntimeMarker = new();
    private static readonly ConditionalWeakTable<RulesetCharacterSimulacrum, SimulacrumSnapshotSeed>
        InitializingSnapshotSeeds = new();
    private static readonly ConditionalWeakTable<RulesetCharacterSimulacrum, VisualRefreshState>
        VisualRefreshStates = new();
    private static readonly Dictionary<ulong, bool> DeferredCleanupCharacters = [];
    private static readonly Dictionary<ulong, RuntimeCleanupRequest> RuntimeCleanupCharacters = [];
    private static IReadOnlyDictionary<string, HumanoidMonsterPresentationDefinition>
        _presentations = new Dictionary<string, HumanoidMonsterPresentationDefinition>();
    private readonly IReadOnlyDictionary<string, string> _shellsBySize;
    private readonly IReadOnlyDictionary<string, string> _definitionPresentationShells;
    private static ConditionDefinition _ownerCondition;
    private static SpellDefinition _spellDefinition;

    internal static FeatureDefinitionPower RepairPower { get; private set; }

    internal static ICustomRestPowerSelection RepairRestPowerSelectionMarker { get; } =
        new RepairRestPowerSelection();

    internal SimulacrumBehavior(
        IReadOnlyDictionary<string, string> shellsBySize,
        IReadOnlyDictionary<string, string> definitionPresentationShells)
    {
        _shellsBySize = shellsBySize ??
                        new Dictionary<string, string>();
        _definitionPresentationShells = definitionPresentationShells ??
                                        new Dictionary<string, string>();
    }

    internal static IBindToRulesetConditionCustom SnapshotBindingMarker =>
        SimulacrumSnapshotRulesetCondition.BindingMarker;

    internal static object RuntimeRestrictionsMarker => RuntimeMarker;

    internal static object OwnerReconciliationMarker { get; } = new OwnerReconciler();

    internal static IForceStackedMaterialComponent StackedMaterialRequirement { get; } =
        new ForceStackedMaterialComponent();

    public bool EnforceFullSelection => false;

    internal static string GetPresentationKey(
        CharacterRaceDefinition raceDefinition,
        CharacterRaceDefinition subRaceDefinition,
        CreatureSex sex)
    {
        return $"{raceDefinition?.Name ?? string.Empty}|" +
               $"{subRaceDefinition?.Name ?? string.Empty}|{sex}";
    }

    internal static void BindPresentations(
        IReadOnlyDictionary<string, HumanoidMonsterPresentationDefinition> presentations)
    {
        _presentations = presentations ??
                         new Dictionary<string, HumanoidMonsterPresentationDefinition>();
    }

    public bool TryPrepare(
        EffectForm effectForm,
        ref RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        out ICustomSummonInvocationContext invocationContext,
        out string failureFeedback)
    {
        invocationContext = null;
        failureFeedback = null;

        if (effectForm?.FormType != EffectForm.EffectFormType.Summon ||
            formsParams.activeEffect?.SourceDefinition != _spellDefinition)
        {
            return true;
        }

        try
        {
            if (GameLocationCharacter.GetFromActor(formsParams.targetCharacter) is not { } target ||
                formsParams.targetCharacter is not RulesetCharacter rulesetTarget ||
                rulesetTarget.SizeDefinition?.Name is not { } sizeName ||
                ServiceRepository.GetService<IGameLocationPositioningService>() is not { } positioningService ||
                ServiceRepository.GetService<IGameLocationPathfindingService>() is not { } pathfindingService)
            {
                Trace.LogWarning(
                    "Simulacrum placement could not be prepared because a required runtime value is missing.");
                failureFeedback = FailureCannotDuplicateTarget;

                return false;
            }

            if (!TryValidateCopyTarget(
                    formsParams.sourceCharacter,
                    rulesetTarget,
                    out failureFeedback))
            {
                return false;
            }

            if (!TryCreateInvocationContext(
                    rulesetTarget,
                    sizeName,
                    out var context,
                    out _))
            {
                failureFeedback = FailureCannotDuplicateTarget;

                return false;
            }

            var occupiedPositions = new List<int3>();

            foreach (var occupiedPosition in target.LocationPathfindBoundingBox.EnumerateAllPositionsWithin())
            {
                occupiedPositions.Add(occupiedPosition);
            }

            var foundPositions = new Dictionary<GameLocationCharacter, int3>();
            var coroutine = new TA.Coroutine();

            positioningService.ComputeUnstackedPlacementPositionsForCharacter(
                target,
                coroutine,
                pathfindingService,
                occupiedPositions,
                foundPositions);

            if (!foundPositions.TryGetValue(target, out var position))
            {
                failureFeedback = "Feedback/&NoRoomToConjureCreatureDescription";

                return false;
            }

            formsParams.position = position;
            invocationContext = context;

            return true;
        }
        catch (Exception ex)
        {
            Trace.LogException(new Exception("Error preparing Simulacrum summon.", ex));
            failureFeedback = FailureCannotDuplicateTarget;

            return false;
        }
    }

    public string GetMonsterDefinitionName(
        EffectForm effectForm,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        ICustomSummonInvocationContext invocationContext)
    {
        return (invocationContext as SimulacrumInvocationContext)?.ShellDefinitionName;
    }

    public void InitializeConstructionAttributes(
        RulesetCharacterMonster summonedCharacter,
        ICustomSummonInvocationContext invocationContext)
    {
        if (summonedCharacter is RulesetCharacterSimulacrum duplicate &&
            invocationContext is SimulacrumInvocationContext context)
        {
            context.SnapshotSeed.ApplyConstructionAttributes(duplicate);
        }
    }

    public void InitializeSummonedCharacter(
        RulesetCharacterMonster summonedCharacter,
        ICustomSummonInvocationContext invocationContext)
    {
        if (summonedCharacter is not RulesetCharacterSimulacrum duplicate ||
            invocationContext is not SimulacrumInvocationContext context)
        {
            return;
        }

        var appearance = context.SnapshotSeed.Appearance;

        appearance.ApplyTo(duplicate);

        InitializingSnapshotSeeds.Remove(duplicate);
        InitializingSnapshotSeeds.Add(duplicate, context.SnapshotSeed);
        context.SnapshotSeed.ApplyInitialAttributes(duplicate);
    }

    private bool TryCreateInvocationContext(
        RulesetCharacter source,
        string sizeName,
        out SimulacrumInvocationContext context,
        out string failure)
    {
        context = null;
        failure = null;

        if (!TryResolveShellDefinition(
                source,
                sizeName,
                out var shellDefinitionName,
                out var definitionPresentationSourceName,
                out failure))
        {
            return false;
        }

        var displayName = GetSourceDisplayName(source);
        var usesInventoryAppearance = source is RulesetCharacterHero;
        HumanoidMonsterPresentationDefinition humanoidPresentation;
        MonsterPresentationDefinition monsterPresentation;
        string bodyAssetPrefix;
        string armorAssetPrefix;
        string helmetAssetPrefix;
        string faceShapeAssetPrefix;
        string beardShapeAssetPrefix;
        string hairShapeAssetPrefix;
        string hornsTailAssetPrefix;

        if (source is RulesetCharacterHero hero)
        {
            // Existing saves receive 2024 ritual features at runtime. Reconcile immediately
            // before snapshot capture as well, so a Simulacrum cannot copy a stale feature graph
            // merely because its source has not opened an action panel since loading.
            Tabletop2024Context.SynchronizeRitualCastingFeatures(hero);

            if (!_presentations.TryGetValue(
                    GetPresentationKey(hero.RaceDefinition, hero.SubRaceDefinition, hero.Sex),
                    out humanoidPresentation))
            {
                failure = "a compatible humanoid presentation is unavailable";

                return false;
            }

            monsterPresentation = null;
            // Live hero properties can contain a morphotype-expanded asset name after a
            // graphics refresh (for example Human_Male_FaceShape_A). The native inventory
            // renderer appends the selected morphotype itself, so persisting that expanded
            // value produces invalid paths such as ..._FaceShape_A_FaceShape_A. Always seed
            // a hero-derived simulacrum with the canonical race/subrace/sex namespace.
            bodyAssetPrefix = GraphicsCharacterDefinitions.GetBodyAssetPrefix(
                hero.RaceDefinition,
                hero.SubRaceDefinition,
                hero.Sex);
            armorAssetPrefix = GraphicsCharacterDefinitions.GetArmorAssetPrefix(
                hero.RaceDefinition,
                hero.SubRaceDefinition,
                hero.Sex);
            helmetAssetPrefix = GraphicsCharacterDefinitions.GetHelmetAssetPrefix(
                hero.RaceDefinition,
                hero.SubRaceDefinition,
                hero.Sex);
            faceShapeAssetPrefix = GraphicsCharacterDefinitions.GetFaceShapeAssetPrefix(
                hero.RaceDefinition,
                hero.SubRaceDefinition,
                hero.Sex);
            beardShapeAssetPrefix = GraphicsCharacterDefinitions.GetBeardShapeAssetPrefix(
                hero.RaceDefinition,
                hero.SubRaceDefinition,
                hero.Sex);
            hairShapeAssetPrefix = GraphicsCharacterDefinitions.GetHairShapeAssetPrefix(
                hero.RaceDefinition,
                hero.SubRaceDefinition,
                hero.Sex);
            hornsTailAssetPrefix = GraphicsCharacterDefinitions.GetHornsTailAssetPrefix(
                hero.RaceDefinition,
                hero.SubRaceDefinition,
                hero.Sex);
        }
        else if (source is RulesetCharacterMonster monster)
        {
            humanoidPresentation = monster.HumanoidMonsterPresentationDefinition;
            monsterPresentation = monster.MonsterPresentationDefinition;
            bodyAssetPrefix = source.BodyAssetPrefix ?? string.Empty;
            armorAssetPrefix = source.ArmorAssetPrefix ?? string.Empty;
            helmetAssetPrefix = source.HelmetAssetPrefix ?? string.Empty;
            faceShapeAssetPrefix = source.FaceShapeAssetPrefix ?? string.Empty;
            beardShapeAssetPrefix = source.BeardShapeAssetPrefix ?? string.Empty;
            hairShapeAssetPrefix = source.HairShapeAssetPrefix ?? string.Empty;
            hornsTailAssetPrefix = source.HornsTailAssetPrefix ?? string.Empty;

        }
        else
        {
            failure = "the source character type is unsupported";

            return false;
        }

        var appearance = new SimulacrumAppearanceSeed(
            displayName,
            usesInventoryAppearance,
            source.Sex,
            source.VoiceID,
            bodyAssetPrefix,
            armorAssetPrefix,
            helmetAssetPrefix,
            faceShapeAssetPrefix,
            beardShapeAssetPrefix,
            hairShapeAssetPrefix,
            hornsTailAssetPrefix,
            humanoidPresentation,
            monsterPresentation,
            definitionPresentationSourceName,
            source.MorphotypeElements,
            source.MorphotypeElementAdditionalValues);

        if (!SimulacrumSnapshotRulesetCondition.TryCreateSeed(
                source,
                appearance,
                out var snapshotSeed,
                out failure))
        {
            return false;
        }

        context = new SimulacrumInvocationContext(shellDefinitionName, snapshotSeed);

        return true;
    }

    private static string GetSourceDisplayName(RulesetCharacter source)
    {
        var candidates = new[]
        {
            (source as RulesetCharacterHero)?.DisplayName,
            source?.Name,
            source?.ForcedName,
            source is RulesetCharacterMonster monster &&
            monster.MonsterDefinition?.GuiPresentation is { } presentation
                ? Gui.Localize(presentation.Title)
                : null,
            Gui.Localize("Spell/&SimulacrumTitle")
        };

        return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ??
               SpellBuilders.SimulacrumName;
    }

    private static void ApplyPersistentAttributeValues(
        RulesetCharacterSimulacrum duplicate,
        IReadOnlyList<PersistentAttributeValue> values)
    {
        foreach (var value in values)
        {
            if (duplicate.TryGetAttribute(value.Name, out _))
            {
                continue;
            }

            duplicate.Attributes.Add(
                value.Name,
                new RulesetAttribute
                {
                    AttributeDefinition = value.Definition
                });
        }

        foreach (var value in values)
        {
            var attribute = duplicate.Attributes[value.Name];

            attribute.AttributeDefinition = value.Definition;
            attribute.BaseValue = value.Value;
            attribute.MinValue = value.MinValue;
            attribute.MaxValue = Math.Max(value.MaxValue, value.Value);
        }

        var callbacks =
            new RulesetImplementationDefinitions.AttributeRefreshedHandler[values.Count];

        for (var index = 0; index < values.Count; index++)
        {
            var attribute = duplicate.Attributes[values[index].Name];
            var callback = attribute.AttributeRefreshed;

            callbacks[index] = callback;

            try
            {
                attribute.AttributeRefreshed = null;
                attribute.Refresh();
            }
            finally
            {
                attribute.AttributeRefreshed = callback;
            }
        }

        foreach (var callback in callbacks)
        {
            callback?.Invoke();
        }
    }

    internal static bool SupportsNonLightDualWielding(
        RulesetCharacter character)
    {
        if (character is RulesetCharacterHero hero)
        {
            return hero.CanDualWieldNonLight;
        }

        if (character is not RulesetCharacterSimulacrum duplicate)
        {
            return false;
        }

        var featuresToBrowse = new List<FeatureDefinition>();
        var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

        duplicate.EnumerateFeaturesToBrowse<IAttackModificationProvider>(
            featuresToBrowse,
            featuresOrigin);

        return SupportsNonLightDualWielding(
            featuresToBrowse.OfType<IAttackModificationProvider>());
    }

    internal static bool CanUseOffHandWeaponAttack(
        RulesetCharacterSimulacrum duplicate,
        RulesetItem mainHand,
        RulesetItem offHand)
    {
        if (!IsProficientWeapon(duplicate, mainHand) ||
            !IsProficientWeapon(duplicate, offHand))
        {
            return false;
        }

        return CanUseOffHandWeaponAttack(
            mainHand,
            offHand,
            SupportsNonLightDualWielding(duplicate));
    }

    internal static bool TryGetBonusAttackModeForOffHand(
        RulesetCharacterSimulacrum duplicate,
        RulesetItem offHand,
        out RulesetAttackMode attackMode)
    {
        attackMode = null;

        if (duplicate == null || offHand == null)
        {
            return false;
        }

        attackMode = duplicate.AttackModes.FirstOrDefault(mode =>
            mode?.ActionType == ActionType.Bonus &&
            mode.SourceObject is RulesetItem sourceItem &&
            (ReferenceEquals(sourceItem, offHand) ||
             sourceItem.Guid != 0 && sourceItem.Guid == offHand.Guid));

        return attackMode != null;
    }

    private static bool CanUseOffHandWeaponAttack(
        RulesetItem mainHand,
        RulesetItem offHand,
        IEnumerable<IAttackModificationProvider> attackModifiers)
    {
        return CanUseOffHandWeaponAttack(
            mainHand,
            offHand,
            SupportsNonLightDualWielding(attackModifiers));
    }

    private static bool CanUseOffHandWeaponAttack(
        RulesetItem mainHand,
        RulesetItem offHand,
        bool canDualWieldNonLight)
    {
        if (mainHand?.ItemDefinition?.WeaponDescription == null ||
            offHand?.ItemDefinition?.WeaponDescription == null)
        {
            return false;
        }

        return canDualWieldNonLight ||
               mainHand.ItemDefinition.WeaponDescription.WeaponTags.Contains(
                   TagsDefinitions.WeaponTagLight) &&
               offHand.ItemDefinition.WeaponDescription.WeaponTags.Contains(
                   TagsDefinitions.WeaponTagLight);
    }

    private static bool SupportsNonLightDualWielding(
        IEnumerable<IAttackModificationProvider> attackModifiers)
    {
        return attackModifiers.Any(modifier =>
            modifier?.CanDualWieldNonLight == true);
    }

    private static bool IsProficientWeapon(
        RulesetCharacterSimulacrum duplicate,
        RulesetItem item)
    {
        return duplicate != null &&
               item?.ItemDefinition is { IsWeapon: true } itemDefinition &&
               duplicate.IsProficientWithItem(itemDefinition);
    }

    private sealed class SimulacrumInvocationContext : ICustomSummonInvocationContext
    {
        internal SimulacrumInvocationContext(
            string shellDefinitionName,
            SimulacrumSnapshotSeed snapshotSeed)
        {
            ShellDefinitionName = shellDefinitionName;
            SnapshotSeed = snapshotSeed;
        }

        internal string ShellDefinitionName { get; }
        internal SimulacrumSnapshotSeed SnapshotSeed { get; }
    }

    internal static object CreateRepairPowerMarker()
    {
        return new RepairPowerBehavior();
    }

    internal static object CreateDismissPowerMarker()
    {
        return new DismissPowerBehavior();
    }

    internal static void BindPowers(FeatureDefinitionPower repairPower)
    {
        RepairPower = repairPower;
    }

    internal static void BindDefinitions(
        SpellDefinition spellDefinition,
        ConditionDefinition snapshotCondition,
        ConditionDefinition ownerCondition)
    {
        _spellDefinition = spellDefinition;
        _ownerCondition = ownerCondition;
        SimulacrumSnapshotRulesetCondition.Bind(snapshotCondition);
    }

    public bool IsValid(CursorLocationSelectTarget cursor, GameLocationCharacter target)
    {
        var owner = cursor?.ActionParams?.ActingCharacter?.RulesetCharacter;

        if (TryValidateCopyTarget(
                owner,
                target?.RulesetCharacter,
                out var failure))
        {
            return true;
        }

        cursor?.actionModifier?.FailureFlags.Add(failure);

        return false;
    }

    public bool IsValid(
        CharacterActionMagicEffect action,
        GameLocationCharacter actingCharacter,
        IReadOnlyList<GameLocationCharacter> targets,
        out string failure)
    {
        var owner = actingCharacter?.RulesetCharacter;
        var target = targets is { Count: 1 }
            ? targets[0]?.RulesetCharacter
            : null;

        if (!TryValidateCopyTarget(owner, target, out failure))
        {
            return false;
        }

        try
        {
            if (TryCreateInvocationContext(
                    target,
                    target.SizeDefinition?.Name,
                    out _,
                    out _))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Trace.LogException(new Exception(
                "Error validating a Simulacrum source before resource consumption. " +
                $"casterGuid={owner?.Guid ?? 0} targetGuid={target?.Guid ?? 0}.",
                ex));
        }

        failure = FailureCannotDuplicateTarget;

        return false;
    }

    public void AfterApply(
        EffectForm effectForm,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        ICustomSummonInvocationContext invocationContext)
    {
        if (effectForm?.FormType != EffectForm.EffectFormType.Summon ||
            formsParams.activeEffect?.SourceDefinition != _spellDefinition ||
            formsParams.sourceCharacter == null ||
            formsParams.targetCharacter is not RulesetCharacter copiedCharacter ||
            invocationContext is not SimulacrumInvocationContext context)
        {
            return;
        }

        try
        {
            CaptureAndApply(
                formsParams.sourceCharacter,
                copiedCharacter,
                formsParams.activeEffect,
                context.SnapshotSeed);
        }
        catch (Exception ex)
        {
            var summons = EffectHelpers
                .GetSummonedCreatures(formsParams.activeEffect)
                .OfType<RulesetCharacterSimulacrum>()
                .ToArray();

            Trace.LogException(new Exception("Error creating Simulacrum.", ex));

            foreach (var summon in summons)
            {
                QueueRuntimeCleanup(
                    summon,
                    formsParams.activeEffect,
                    formsParams.sourceCharacter);
            }

            if (summons.Length == 0)
            {
                formsParams.activeEffect.DoTerminate(formsParams.sourceCharacter);
            }
        }
    }

    internal static bool CanUseHumanoidEquipment(RulesetCharacterSimulacrum character)
    {
        return TryGetSnapshot(character, out var snapshot) &&
               snapshot.IsCurrentSchema &&
               snapshot.CanEquipHumanoidItems;
    }

    internal static bool CanAccessHumanoidInventory(RulesetCharacterSimulacrum character)
    {
        return character is { LifecycleState: SimulacrumLifecycleState.Ready } &&
               CanUseHumanoidEquipment(character);
    }

    internal static bool IsRepairActivity(AfterRestActionItem item)
    {
        return item?.RestActivityDefinition?.Functor == PowerBundleContext.UseCustomRestPowerFunctorName &&
               item.RestActivityDefinition.StringParameter == RepairPowerName;
    }

    internal static bool TryGetMaximumRepairHitPoints(RulesetCharacter owner, out int maximum)
    {
        var duplicate = FindOwnedSimulacrum(owner);

        maximum = duplicate == null || duplicate.IsDeadOrDyingOrUnconscious
            ? 0
            : Math.Min(
                duplicate.MissingHitPoints,
                GetAvailableGold() / RepairCostPerHitPoint);

        return maximum > 0;
    }

    internal static bool TryDismissSimulacrum(RulesetCharacterSimulacrum duplicate)
    {
        if (!TryGetDismissEffect(duplicate, out var activeEffect, out var owner))
        {
            return false;
        }

        QueueEffectForRuntimeCleanup(owner, activeEffect, [duplicate]);

        return true;
    }

    internal static bool TryRepair(
        RulesetCharacter owner,
        int requestedHitPoints,
        out int repairedHitPoints,
        out string failure)
    {
        repairedHitPoints = 0;
        failure = string.Empty;

        var duplicate = FindOwnedSimulacrum(owner);

        if (duplicate == null || duplicate.IsDeadOrDyingOrUnconscious)
        {
            failure = FailureNotFound;

            return false;
        }

        var party = Gui.GameCampaign?.Party;
        var availableGold = GetAvailableGold();
        var missingHitPoints = duplicate.MissingHitPoints;
        var affordableHitPoints = availableGold / RepairCostPerHitPoint;

        repairedHitPoints = Math.Min(
            Math.Min(Math.Max(0, requestedHitPoints), missingHitPoints),
            affordableHitPoints);

        if (repairedHitPoints <= 0)
        {
            failure = FailureRepairMaterials;

            return false;
        }

        party.SpendAmount(repairedHitPoints * RepairCostPerHitPoint, true, false);
        duplicate.ForceSetHealth(repairedHitPoints, true);

        return true;
    }

    private static int GetAvailableGold()
    {
        return Gui.GameCampaign?.Party is { } party
            ? EquipmentDefinitions.GetApproximateCostInGold(party.Treasury.CurrencyAmounts)
            : 0;
    }

    internal static bool CanCastSpellOfActionType(
        RulesetCharacterSimulacrum character,
        ActionType actionType,
        bool canOnlyUseCantrips)
    {
        if (character is not { LifecycleState: SimulacrumLifecycleState.Ready })
        {
            return false;
        }

        foreach (var repertoire in character.SpellRepertoires.Where(x => x != null))
        {
            if (repertoire.KnownCantrips.Any(spell =>
                    HasCastingActionType(spell, actionType)))
            {
                return true;
            }

            if (canOnlyUseCantrips)
            {
                continue;
            }

            var readySpells = repertoire.SpellCastingFeature?.SpellReadyness ==
                              SpellReadyness.Prepared
                ? repertoire.PreparedSpells
                : repertoire.KnownSpells;
            var candidates = readySpells
                .Concat(repertoire.AutoPreparedSpells)
                .Concat(repertoire.ExtraSpellsByTag.Values.SelectMany(x => x))
                .Where(x => x != null)
                .Distinct();

            if (candidates.Any(spell =>
                    HasCastingActionType(spell, actionType) &&
                    (spell.SpellLevel == 0 || repertoire.CanCastSpellOfLevel(spell.SpellLevel))))
            {
                return true;
            }
        }

        if (actionType != ActionType.Bonus)
        {
            return false;
        }

        if (!canOnlyUseCantrips && character.HasSmites())
        {
            return true;
        }

        return character.HasAnyFeature(PatronEldritchSurge.FeatureBlastReload);

        static bool HasCastingActionType(SpellDefinition spell, ActionType expectedActionType)
        {
            return spell != null &&
                   CastingTimeToActionDefinition.TryGetValue(
                       spell.ActivationTime,
                       out var spellActionType) &&
                   spellActionType == expectedActionType;
        }
    }

    internal static bool IsMaterialComponentValid(
        RulesetCharacterSimulacrum caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        out string failure)
    {
        failure = string.Empty;

        if (caster == null || spellDefinition == null)
        {
            return false;
        }

        var materialComponentSetting =
            ServiceRepository.GetService<IGameSettingsService>()?.MaterialComponent;

        if (materialComponentSetting == SettingDefinitions.MaterialComponentDisabled ||
            spellDefinition.MaterialComponentType == MaterialComponentType.None)
        {
            return true;
        }

        if (materialComponentSetting == SettingDefinitions.MaterialComponentBasic ||
            spellDefinition.MaterialComponentType == MaterialComponentType.Mundane)
        {
            return IsSpellFocusValid(caster, repertoire, spellDefinition, out failure);
        }

        if (spellDefinition.MaterialComponentType != MaterialComponentType.Specific)
        {
            return true;
        }

        var materialTag = spellDefinition.SpecificMaterialComponentTag;
        var materialCost = spellDefinition.SpecificMaterialComponentCostGp;

        if (string.IsNullOrEmpty(materialTag) || materialCost <= 0)
        {
            return true;
        }

        var materialItems = new List<RulesetItem>();

        caster.CharacterInventory?.EnumerateAllItems(materialItems, true, false);

        if (materialItems.Any(item =>
                item?.ItemDefinition != null &&
                item.ItemDefinition.ItemTags.Contains(materialTag) &&
                EquipmentDefinitions.GetApproximateCostInGold(
                    item.ItemDefinition.Costs) >= materialCost))
        {
            return true;
        }

        failure = Gui.Format(
            SpellAndPowersDefinitions.FailureFlagMaterialComponentMissingSpecific,
            Gui.FormatTag(materialTag),
            Gui.FormatCostGp(materialCost));

        return false;
    }

    private static bool IsSpellFocusValid(
        RulesetCharacterSimulacrum caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        out string failure)
    {
        failure = string.Empty;

        if (caster.CharacterInventory == null)
        {
            // Keep the native RulesetCharacter behavior for actors without an inventory.
            return true;
        }

        // A spell can be known through multiple repertoires with different focus
        // requirements. The action/tooltip validation context already resolves the
        // repertoire selected by the player; do not let another focus-free repertoire
        // waive the selected Paladin/Cleric/etc. route's material requirement.
        var relevantRepertoires = repertoire?.SpellCastingFeature != null
            ? [repertoire]
            : caster.SpellRepertoires
                .Where(candidate =>
                    candidate?.SpellCastingFeature != null &&
                    (candidate.HasKnowledgeOfSpell(spellDefinition) ||
                     candidate.PreparedSpells.Contains(spellDefinition)))
                .ToArray();

        if (relevantRepertoires.Any(candidate =>
                candidate.SpellCastingFeature.FocusType ==
                EquipmentDefinitions.FocusType.None))
        {
            return true;
        }

        var focusTypes = relevantRepertoires
            .Select(candidate => candidate.SpellCastingFeature.FocusType)
            .ToHashSet();
        var equippedItems = new List<RulesetItem>();

        caster.CharacterInventory.EnumerateAllItems(equippedItems, false, true);

        if (equippedItems.Any(item =>
                item?.ItemDefinition is
                {
                    IsFocusItem: true,
                    FocusItemDescription: { } focus
                } &&
                (focus.FocusType == EquipmentDefinitions.FocusType.Universal ||
                 focusTypes.Contains(focus.FocusType))))
        {
            return true;
        }

        caster.EnumerateFeaturesToBrowse<ISpellCastingAffinityProvider>(
            caster.FeaturesToBrowse,
            null);
        caster.CharacterInventory.InventorySlotsByName.TryGetValue(
            EquipmentDefinitions.SlotTypeMainHand,
            out var mainHandSlot);
        caster.CharacterInventory.InventorySlotsByName.TryGetValue(
            EquipmentDefinitions.SlotTypeOffHand,
            out var offHandSlot);
        var mainHand = mainHandSlot?.EquipedItem;
        var offHand = offHandSlot?.EquipedItem;

        if (caster.FeaturesToBrowse
            .OfType<ISpellCastingAffinityProvider>()
            .Any(provider =>
                provider.CanUseProficientWeaponAsFocus &&
                ((mainHand?.ItemDefinition is { IsWeapon: true } mainWeapon &&
                  caster.IsProficientWithItem(mainWeapon)) ||
                 (offHand?.ItemDefinition is { IsWeapon: true } offWeapon &&
                  caster.IsProficientWithItem(offWeapon)))))
        {
            return true;
        }

        if (spellDefinition.MaterialComponentType == MaterialComponentType.Mundane &&
            caster.DeityDefinition != null &&
            offHand?.ItemDefinition is { IsArmor: true } offHandDefinition &&
            offHandDefinition.ArmorDescription.ArmorTypeDefinition.ArmorCategory ==
            EquipmentDefinitions.ShieldCategory &&
            offHand.DeityMark == caster.DeityDefinition.Name)
        {
            return true;
        }

        failure = Gui.Localize(
            SpellAndPowersDefinitions.FailureFlagMaterialComponentMissingFocus);

        foreach (var candidate in relevantRepertoires)
        {
            failure += Gui.Format(
                " ({0})",
                Gui.Format(
                    $"Equipment/&ItemTypeSpellFocusSubtype" +
                    $"{candidate.SpellCastingFeature.FocusType}Title"));
        }

        return false;
    }

    internal static IEnumerable<KeyValuePair<SpellDefinition, RulesetSpellRepertoire>>
        EnumerateRitualSpellCandidates(
            RulesetCharacterSimulacrum duplicate,
            RitualCasting ritualCasting)
    {
        if (duplicate == null)
        {
            yield break;
        }

        foreach (var repertoire in duplicate.SpellRepertoires.Where(x =>
                     x?.SpellCastingFeature != null))
        {
            var maximumSpellLevel =
                SharedSpellsContext.MaxSpellLevelOfSpellCastingLevel(repertoire);
            IEnumerable<SpellDefinition> candidates;

            switch (ritualCasting)
            {
                case RitualCasting.PactTomeRitual:
                    candidates = repertoire.ExtraSpellsByTag
                        .Where(x => x.Key.Contains("PactTomeRitual"))
                        .SelectMany(x => x.Value);
                    break;
                case RitualCasting.Prepared:
                    candidates = repertoire.SpellCastingFeature.SpellReadyness ==
                                 SpellReadyness.Prepared
                        ? repertoire.PreparedSpells
                        : repertoire.KnownSpells;
                    break;
                case RitualCasting.Selection:
                    candidates = repertoire.KnownSpells;
                    break;
                case RitualCasting.Spellbook
                    when repertoire.SpellCastingFeature.SpellKnowledge ==
                         SpellKnowledge.Spellbook:
                    // A Simulacrum has no physical spellbook. Its independently copied KnownSpells
                    // are the scribed-spell source for 2024 Wizard ritual casting.
                    candidates = repertoire.KnownSpells;
                    break;
                case RitualCasting.Spellbook:
                    continue;
                default:
                    candidates = repertoire.KnownSpells
                        .Concat(repertoire.PreparedSpells)
                        .Concat(repertoire.AutoPreparedSpells);
                    break;
            }

            foreach (var spell in candidates
                         .Where(spell => spell is { Ritual: true } &&
                                         spell.SpellLevel <= maximumSpellLevel)
                         .Distinct())
            {
                yield return new KeyValuePair<SpellDefinition, RulesetSpellRepertoire>(
                    spell,
                    repertoire);
            }
        }
    }

    internal static RulesetSpellRepertoire ResolveRitualRepertoire(
        RulesetCharacterSimulacrum duplicate,
        SpellDefinition spellDefinition)
    {
        if (duplicate == null || spellDefinition == null)
        {
            return null;
        }

        var ritualCastings = duplicate.ActiveFeatures
            .OfType<FeatureDefinitionMagicAffinity>()
            .Select(x => x.RitualCasting)
            .Where(x => x != RitualCasting.None)
            .Distinct();

        return ritualCastings
            .SelectMany(ritualCasting =>
                EnumerateRitualSpellCandidates(duplicate, ritualCasting))
            .Where(candidate => candidate.Key == spellDefinition)
            .Select(candidate => candidate.Value)
            .Distinct()
            .OrderBy(repertoire => GetRitualRepertoirePriority(repertoire, spellDefinition))
            .ThenBy(repertoire => repertoire.SpellCastingFeature.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int GetRitualRepertoirePriority(
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition)
    {
        var feature = repertoire.SpellCastingFeature;
        var originPriority = feature.SpellCastingOrigin switch
        {
            FeatureDefinitionCastSpell.CastingOrigin.Class => 0,
            FeatureDefinitionCastSpell.CastingOrigin.Subclass => 1,
            FeatureDefinitionCastSpell.CastingOrigin.Race => 2,
            FeatureDefinitionCastSpell.CastingOrigin.Monster => 3,
            _ => 4
        };
        var sourcePriority = feature.SpellKnowledge == SpellKnowledge.Spellbook &&
                             repertoire.KnownSpells.Contains(spellDefinition)
            ? 0
            : feature.SpellReadyness == SpellReadyness.Prepared &&
              repertoire.PreparedSpells.Contains(spellDefinition)
                ? 1
                : repertoire.KnownSpells.Contains(spellDefinition)
                    ? 2
                    : repertoire.AutoPreparedSpells.Contains(spellDefinition)
                        ? 3
                        : 4;

        return originPriority * 10 + sourcePriority;
    }

    internal static void AdvanceTimedEffectsForRest(
        RulesetCharacterSimulacrum character,
        RestType restType,
        bool simulate = false)
    {
        if (character == null)
        {
            return;
        }

        if (simulate)
        {
            return;
        }

        if (RestDurationInSeconds.TryGetValue(restType, out var durationSeconds) &&
            durationSeconds > 0)
        {
            var rounds = (durationSeconds + 5) / 6;

            character.RefreshEffectsForRealTimeLapse(rounds);
        }

        // The duplicate does not itself rest. "Until rest" effects therefore remain, while
        // effects with an ordinary minute/hour duration expire because the elapsed camp or
        // travel time was applied above.
    }

    private bool CaptureAndApply(
        RulesetCharacter owner,
        RulesetCharacter copiedCharacter,
        RulesetEffect activeEffect,
        SimulacrumSnapshotSeed seed)
    {
        if (owner == null ||
            copiedCharacter == null ||
            activeEffect == null ||
            seed == null ||
            seed.CopiedCharacterGuid != copiedCharacter.Guid)
        {
            return false;
        }

        if (!TryValidateCopyTarget(
                owner,
                copiedCharacter,
                out _))
        {
            QueueEffectForRuntimeCleanup(owner, activeEffect);

            return false;
        }

        var summons = EffectHelpers
            .GetSummonedCreatures(activeEffect)
            .OfType<RulesetCharacterSimulacrum>()
            .ToArray();

        if (summons.Length != 1 ||
            summons[0].CurrentHitPoints <= 0 ||
            summons[0].IsDeadOrDyingOrUnconscious ||
            summons[0].MonsterDefinition?.Name?.StartsWith(
                "SimulacrumShell",
                StringComparison.Ordinal) != true ||
            GameLocationCharacter.GetFromActor(summons[0]) == null)
        {
            Trace.LogWarning("Simulacrum did not create exactly one live, placed shell.");
            QueueEffectForRuntimeCleanup(owner, activeEffect, summons);

            return false;
        }

        var duplicate = summons[0];
        var duplicateLocation = GameLocationCharacter.GetFromActor(duplicate);
        var copiedLocation = GameLocationCharacter.GetFromActor(copiedCharacter);

        if (duplicateLocation == null ||
            copiedLocation == null ||
            HasOverlappingFootprint(duplicateLocation, copiedLocation))
        {
            Trace.LogWarning("Simulacrum shell was not placed in a valid unoccupied position.");
            QueueRuntimeCleanup(duplicate, activeEffect, owner);

            return false;
        }

        if (TryGetSnapshot(duplicate, out var existingSnapshot))
        {
            if (existingSnapshot.SourceGuid == owner.Guid &&
                existingSnapshot.OwningEffectGuid == activeEffect.Guid &&
                duplicate.LifecycleState == SimulacrumLifecycleState.Ready)
            {
                return true;
            }

            QueueRuntimeCleanup(duplicate, activeEffect, owner);

            return false;
        }

        try
        {
            var snapshot = duplicate.InflictCondition(
                SimulacrumSnapshotRulesetCondition.BindingCondition.Name,
                DurationType.Permanent,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                owner.Guid,
                owner.CurrentFaction.Name,
                activeEffect.EffectLevel,
                _spellDefinition.Name,
                0,
                activeEffect.ComputeSourceAbilityBonus(owner),
                activeEffect.ComputeSourceProficiencyBonus(owner),
                0) as SimulacrumSnapshotRulesetCondition;

            if (snapshot == null)
            {
                QueueRuntimeCleanup(duplicate, activeEffect, owner);

                return false;
            }

            snapshot.Capture(copiedCharacter, duplicate, activeEffect, seed);
            activeEffect.TrackCondition(
                owner,
                owner.Guid,
                duplicate,
                duplicate.Guid,
                snapshot,
                AttributeDefinitions.TagEffect);

            AddOwnerCondition(owner, activeEffect);
            duplicate.SetLifecycleState(SimulacrumLifecycleState.Ready);
            InitializingSnapshotSeeds.Remove(duplicate);
            RefreshLocationState(duplicate, true);
            BindSnapshotHandlers(duplicate);
            PublishRestoredState(duplicate);
            SimulacrumPortraits.Invalidate(duplicate);
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogException(new Exception("Error applying Simulacrum snapshot.", ex));
            QueueRuntimeCleanup(duplicate, activeEffect, owner);

            return false;
        }
    }

    private bool TryValidateCopyTarget(
        RulesetCharacter owner,
        RulesetCharacter copiedCharacter,
        out string failure)
    {
        failure = null;

        if (owner == null ||
            copiedCharacter == null ||
            copiedCharacter.IsDeadOrDyingOrUnconscious ||
            copiedCharacter.Side != owner.Side ||
            copiedCharacter.CharacterFamily is not ("Beast" or "Humanoid"))
        {
            failure = FailureInvalidTarget;

            return false;
        }

        if (TryGetSnapshot(copiedCharacter, out _))
        {
            failure = FailureDuplicateTarget;

            return false;
        }

        if (TryFindOwnedSimulacrumDependency(owner, copiedCharacter))
        {
            failure = FailureDependentTarget;

            return false;
        }

        if (!TryResolveShellDefinition(
                copiedCharacter,
                copiedCharacter.SizeDefinition?.Name,
                out _,
                out _,
                out _))
        {
            failure = FailureCannotDuplicateTarget;

            return false;
        }

        return true;
    }

    private bool TryResolveShellDefinition(
        RulesetCharacter source,
        string sizeName,
        out string shellDefinitionName,
        out string definitionPresentationSourceName,
        out string failure)
    {
        shellDefinitionName = null;
        definitionPresentationSourceName = string.Empty;
        failure = null;

        if (source == null ||
            string.IsNullOrEmpty(sizeName) ||
            !_shellsBySize.TryGetValue(sizeName, out shellDefinitionName))
        {
            failure = "size-shell-unavailable";

            return false;
        }

        if (source is RulesetCharacterHero hero)
        {
            if (_presentations.ContainsKey(
                    GetPresentationKey(hero.RaceDefinition, hero.SubRaceDefinition, hero.Sex)))
            {
                return true;
            }

            failure = "humanoid-presentation-unavailable";

            return false;
        }

        if (source is not RulesetCharacterMonster monster)
        {
            failure = "source-character-type-unsupported";

            return false;
        }

        if (monster.HumanoidMonsterPresentationDefinition != null ||
            monster.MonsterPresentationDefinition != null)
        {
            return true;
        }

        definitionPresentationSourceName = monster.MonsterDefinition?.Name ?? string.Empty;

        if (monster.MonsterDefinition?.SizeDefinition?.Name == sizeName &&
            !string.IsNullOrEmpty(definitionPresentationSourceName) &&
            _definitionPresentationShells.TryGetValue(
                definitionPresentationSourceName,
                out shellDefinitionName))
        {
            return true;
        }

        definitionPresentationSourceName = string.Empty;

        failure = monster.MonsterDefinition?.SizeDefinition?.Name == sizeName
            ? "definition-monster-presentation-unavailable"
            : "definition-monster-presentation-size-mismatch";

        return false;
    }

    private static bool TryFindOwnedSimulacrumDependency(
        RulesetCharacter owner,
        RulesetCharacter target)
    {
        if (owner == null || target == null)
        {
            return false;
        }

        var visited = new HashSet<ulong>();
        var current = target;

        while (current != null && current.Guid != 0 && visited.Add(current.Guid))
        {
            var summoner = EffectHelpers.GetSummoner(current);

            if (summoner == null)
            {
                return false;
            }

            var simulacrum = summoner as RulesetCharacterSimulacrum ??
                             summoner.OriginalFormCharacter as RulesetCharacterSimulacrum;

            if (simulacrum != null &&
                TryGetSnapshot(simulacrum, out var snapshot) &&
                snapshot.SourceGuid == owner.Guid)
            {
                return true;
            }

            current = summoner;
        }

        return false;
    }

    private sealed class ForceStackedMaterialComponent : IForceStackedMaterialComponent
    {
    }

    private sealed class RepairRestPowerSelection : ICustomRestPowerSelection
    {
        public bool TryOpen(AfterRestActionItem item)
        {
            return SimulacrumRepairInput.TryOpen(item);
        }
    }

    private sealed class RepairPowerBehavior : IValidatePowerUse, IPowerOrSpellFinishedByMe
    {
        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            return FindOwnedSimulacrum(character) is
            {
                IsDeadOrDyingOrUnconscious: false,
                IsMissingHitPoints: true
            } &&
                   GetAvailableGold() >= RepairCostPerHitPoint;
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(
            CharacterActionMagicEffect action,
            BaseDefinition baseDefinition)
        {
            var owner = action.ActingCharacter.RulesetCharacter;

            if (!SimulacrumRepairInput.TryTakeConfirmedRequest(owner, out var requestedHitPoints))
            {
                RefundRepairPowerUse(action, owner);

                yield break;
            }

            if (!TryRepair(owner, requestedHitPoints, out _, out var failure))
            {
                RefundRepairPowerUse(action, owner);

                if (!string.IsNullOrEmpty(failure))
                {
                    Gui.GuiService.ShowAlert(failure, Gui.ColorFailure, 2.5f);
                }
            }
            else
            {
                SimulacrumRepairInput.MarkExecutionSuccessful(owner);
            }

            yield break;
        }

        private static void RefundRepairPowerUse(CharacterActionMagicEffect action, RulesetCharacter owner)
        {
            if (action.ActionParams.RulesetEffect is RulesetEffectPower { UsablePower: { } usablePower })
            {
                owner.RepayPowerUse(usablePower);
            }
        }
    }

    private sealed class DismissPowerBehavior : IValidatePowerUse, IPowerOrSpellFinishedByMe
    {
        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            if (character is not RulesetCharacterSimulacrum duplicate)
            {
                return false;
            }

            return TryGetDismissEffect(duplicate, out _, out _);
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(
            CharacterActionMagicEffect action,
            BaseDefinition baseDefinition)
        {
            if (action.ActingCharacter.RulesetCharacter is RulesetCharacterSimulacrum duplicate)
            {
                TryDismissSimulacrum(duplicate);
            }

            yield break;
        }
    }

    private sealed class RuntimeRestrictions :
        IClassLevelProvider,
        ISubclassLevelProvider,
        IOnConditionAddedOrRemoved,
        IOnCharacterPostLoad,
        IOnLocationCharacterRestored,
        IOnRefreshAttackModes,
        IOnReducedToZeroHp,
        IPreventRestRecovery,
        IUseIndependentSpellSlots,
        IUseOwnStatsWhenSummoned,
        IValidateSpellCasting
    {
        public int Priority => 0;

        public int GetClassLevel(
            RulesetCharacter character,
            CharacterClassDefinition classDefinition)
        {
            return TryGetSnapshot(character, out var snapshot)
                ? snapshot.GetClassLevel(classDefinition)
                : 0;
        }

        public int GetSubclassLevel(
            RulesetCharacter character,
            CharacterClassDefinition classDefinition,
            string subclassName)
        {
            if (!TryGetSnapshot(character, out var snapshot))
            {
                return 0;
            }

            var repertoireMatches = character.SpellRepertoires.Any(repertoire =>
                repertoire.SpellCastingSubclass &&
                repertoire.SpellCastingSubclass.Name == subclassName &&
                (!repertoire.SpellCastingClass || repertoire.SpellCastingClass == classDefinition));

            return repertoireMatches
                ? snapshot.GetClassLevel(classDefinition)
                : snapshot.GetSubclassLevel(classDefinition, subclassName);
        }

        public void OnConditionAdded(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            // The condition is inflicted before its serialized snapshot is
            // populated. Runtime handlers are bound only after a successful
            // capture or rehydration.
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            UnbindSnapshotHandlers(target);
        }

        public void OnCharacterPostLoad(RulesetCharacter character)
        {
            if (TryGetSnapshot(character, out var snapshot))
            {
                string validationFailure = null;

                if (character is not RulesetCharacterSimulacrum ||
                    !snapshot.IsCurrentSchema ||
                    !snapshot.TryValidateSnapshot(out validationFailure))
                {
                    Trace.LogWarning(
                        $"Deferring removal of an invalid Simulacrum snapshot: " +
                        $"{validationFailure ?? "legacy schema"}.");
                    QueueDeferredCleanup(character, true);

                    return;
                }

                try
                {
                    snapshot.Reapply(character);
                    var duplicate = (RulesetCharacterSimulacrum)character;

                    duplicate.SetLifecycleState(SimulacrumLifecycleState.Ready);
                    RefreshLocationState(duplicate);
                    BindSnapshotHandlers(duplicate);
                    SimulacrumPortraits.Invalidate(duplicate);
                }
                catch (Exception ex)
                {
                    Trace.LogException(new Exception(
                        "Error rehydrating a loaded Simulacrum.",
                        ex));
                    ((RulesetCharacterSimulacrum)character).SetLifecycleState(
                        SimulacrumLifecycleState.CleanupPending);
                    QueueDeferredCleanup(character, false);
                }
            }
        }

        public void OnLocationCharacterRestored(RulesetCharacter character)
        {
            Reconcile(character);
        }

        public void AfterRefreshAttackModes(RulesetCharacterMonster monster)
        {
            RebuildAttackModes(monster);
        }

        public IEnumerator HandleReducedToZeroHp(
            GameLocationCharacter attacker,
            GameLocationCharacter downedCreature,
            RulesetAttackMode attackMode,
            RulesetEffect activeEffect)
        {
            if (TryGetSnapshot(downedCreature.RulesetCharacter, out var snapshot))
            {
                if (!TerminateOwningEffect(downedCreature.RulesetCharacter, snapshot))
                {
                    DestroyOrphan(downedCreature.RulesetCharacter);
                }
            }

            yield break;
        }

        public bool PreventRestRecovery(RulesetCharacter character, RestType restType)
        {
            return TryGetSnapshot(character, out _);
        }

        public bool CanCastSpell(
            SpellCastingValidationContext context,
            out string failure)
        {
            failure = string.Empty;

            if (context.SpellDefinition == _spellDefinition)
            {
                failure = FailureCannotCast;

                return false;
            }

            if (context.Caster is not RulesetCharacterSimulacrum ||
                context.BypassComponentsAndCastingTime ||
                context.BypassMaterialComponent ||
                ServiceRepository.GetService<IGameSettingsService>()?.MaterialComponent ==
                 SettingDefinitions.MaterialComponentDisabled)
            {
                return true;
            }

            // Use the same virtual runtime route as the action box and tooltip.
            // The monster override patch first applies the Simulacrum's selected-
            // repertoire focus rules, then the shared stack, dynamic-tag, infusion,
            // and grapple-hand validators.
            bool valid;

            using (SpellCastingValidation.EnterSelectedRepertoire(context.Repertoire))
            {
                valid = context.Caster.IsComponentMaterialValid(
                    context.SpellDefinition,
                    out failure);
            }

            return valid;
        }
    }

}
