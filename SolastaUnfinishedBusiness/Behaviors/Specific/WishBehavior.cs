using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Patches;
using TA;
using UnityEngine.AddressableAssets;
using static FeatureDefinitionAttributeModifier;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal sealed class WishBehavior : ICustomSubspellSelectionProvider
{
    private const int WishSlotLevel = 9;

    [ThreadStatic]
    private static bool _isApplyingIrreducibleDamage;

    private static readonly StoreCreatedItemInCasterInventory StoreCreatedItemMarker = new();

    private static readonly string[] DamageTypes =
    [
        DamageTypeAcid,
        DamageTypeBludgeoning,
        DamageTypeCold,
        DamageTypeFire,
        DamageTypeForce,
        DamageTypeLightning,
        DamageTypeNecrotic,
        DamageTypePiercing,
        DamageTypePoison,
        DamageTypePsychic,
        DamageTypeRadiant,
        DamageTypeSlashing,
        DamageTypeThunder
    ];

    private readonly SpellDefinition _back;
    private readonly Dictionary<int, SpellDefinition> _duplicateLevels = [];
    private readonly Dictionary<SpellDefinition, SpellDefinition> _immunityOptions = [];
    private readonly Dictionary<int, SpellDefinition> _immunityLevels = [];
    private readonly List<SpellDefinition> _realityRevisionOptions = [];
    private readonly List<SpellDefinition> _resistanceOptions = [];
    private readonly HashSet<SpellDefinition> _safeRoots;
    private readonly AlternateEffectFinished _stressOnFinished;
    private readonly AssetReferenceSprite _wishSprite;

    private WishBehavior(AssetReferenceSprite wishSprite)
    {
        _wishSprite = wishSprite;

        var roots = BuildSafeCatalog();

        _safeRoots = roots.ToHashSet();

        BuildStressDefinitions();
        _stressOnFinished = new AlternateEffectFinished(this);
        _back = BuildNavigationSpell("WishBack");

        for (var level = 0; level <= 8; level++)
        {
            _duplicateLevels[level] = BuildNavigationSpell($"WishDuplicateLevel{level}");
            _immunityLevels[level] = BuildNavigationSpell($"WishSpellImmunityLevel{level}");
        }

        DuplicateSpell = BuildNavigationSpell("WishDuplicateSpell");
        Resistance = BuildNavigationSpell("WishResistance");
        SpellImmunity = BuildNavigationSpell("WishSpellImmunity");
        ObjectCreation = BuildObjectCreation();
        InstantHealth = BuildInstantHealth();
        RealityRevision = BuildNavigationSpell("WishRealityRevision");

        BuildResistanceOptions();
        BuildSpellImmunityOptions(roots);
        BuildRealityRevisionOptions();

        TopLevelSpells =
        [
            DuplicateSpell,
            ObjectCreation,
            InstantHealth,
            Resistance,
            SpellImmunity,
            RealityRevision
        ];
    }

    internal SpellDefinition DuplicateSpell { get; }

    internal SpellDefinition InstantHealth { get; }

    internal SpellDefinition ObjectCreation { get; }

    internal SpellDefinition RealityRevision { get; }

    internal SpellDefinition Resistance { get; }

    internal SpellDefinition SpellImmunity { get; }

    internal SpellDefinition[] TopLevelSpells { get; }

    internal static bool IsApplyingIrreducibleDamage => _isApplyingIrreducibleDamage;

    internal static bool ShouldStoreCreatedItemInCasterInventory(BaseDefinition sourceDefinition)
    {
        return sourceDefinition?.HasSubFeatureOfType<StoreCreatedItemInCasterInventory>() == true;
    }

    internal IValidateSpellCasting CastingValidator { get; private set; }

    private ConditionDefinition ConditionWishLost { get; set; }

    private ConditionDefinition ConditionWishStrength { get; set; }

    private ConditionDefinition ConditionWishStress { get; set; }

    internal static WishBehavior Build(AssetReferenceSprite wishSprite)
    {
        return new WishBehavior(wishSprite);
    }

    public ICustomSubspellSelectionSession CreateSession(
        SpellDefinition masterSpell,
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        int slotLevel)
    {
        var activeRoots = SpellsContext
            .GetActiveSpells(8)
            .Where(_safeRoots.Contains)
            .OrderBy(spell => spell.SpellLevel)
            .ThenBy(spell => spell.Name, StringComparer.Ordinal)
            .ToList();

        return new SelectionSession(this, masterSpell, caster, repertoire, activeRoots);
    }

    private static List<SpellDefinition> BuildSafeCatalog()
    {
        var gamingPlatformService = ServiceRepository.GetService<IGamingPlatformService>();
        var publicSpells = SpellsContext.Spells
            .Where(spell => spell != null)
            .ToHashSet();
        var bundleChildren = DatabaseRepository
            .GetDatabase<SpellDefinition>()
            .Where(spell => spell.SubspellsList is { Count: > 0 })
            .SelectMany(spell => spell.SubspellsList)
            .Where(spell => spell != null)
            .ToHashSet();
        var states = new Dictionary<SpellDefinition, SpellTreeState>();

        return publicSpells
            .Where(spell =>
                spell is { Implemented: true, GuiPresentation.hidden: false, SpellLevel: >= 0 and <= 8 } &&
                spell.Name != Counterspell.Name &&
                spell.Name != "Wish" &&
                !bundleChildren.Contains(spell) &&
                IsSafeSpellTree(spell, gamingPlatformService, states))
            .OrderBy(spell => spell.SpellLevel)
            .ThenBy(spell => spell.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsSafeSpellTree(
        SpellDefinition spell,
        IGamingPlatformService gamingPlatformService,
        Dictionary<SpellDefinition, SpellTreeState> states)
    {
        if (spell == null)
        {
            return false;
        }

        if (states.TryGetValue(spell, out var state))
        {
            return state == SpellTreeState.Safe;
        }

        if (!spell.Implemented ||
            spell.SpellLevel is < 0 or > 8 ||
            !IsReplicableCastingTime(spell.ActivationTime) ||
            !IsContentPackAvailable(spell, gamingPlatformService) ||
            spell.Name == "Wish")
        {
            states[spell] = SpellTreeState.Unsafe;

            return false;
        }

        states[spell] = SpellTreeState.Visiting;

        var children = spell.SubspellsList;
        var valid = children is not { Count: > 0 }
            ? spell.EffectDescription != null
            : children.All(child => IsSafeSpellTree(child, gamingPlatformService, states));

        states[spell] = valid
            ? SpellTreeState.Safe
            : SpellTreeState.Unsafe;

        return valid;
    }

    private static bool IsContentPackAvailable(
        SpellDefinition spell,
        IGamingPlatformService gamingPlatformService)
    {
        return spell.ContentPack == CeContentPackContext.CeContentPack ||
               gamingPlatformService == null ||
               gamingPlatformService.IsContentPackAvailable(spell.ContentPack);
    }

    private static bool IsReplicableCastingTime(ActivationTime castingTime)
    {
        return castingTime is
            ActivationTime.Action or
            ActivationTime.BonusAction or
            ActivationTime.Minute1 or
            ActivationTime.Minute10 or
            ActivationTime.Hours1 or
            ActivationTime.Hours24;
    }

    private enum SpellTreeState
    {
        Visiting,
        Safe,
        Unsafe
    }

    private SpellDefinition BuildNavigationSpell(string name)
    {
        return SpellDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(Category.Spell, _wishSprite)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
            .SetSpellLevel(WishSlotLevel)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetSomaticComponent(false)
            .SetVerboseComponent(true)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Self)
                    .Build())
            .AddToDB();
    }

    private SpellDefinition BuildObjectCreation()
    {
        var item = ItemDefinitionBuilder
            .Create(ItemDefinitions._1000_GP_Diamond, "ItemWishCreatedTreasure")
            .SetGuiPresentation(Category.Item, ItemDefinitions._1000_GP_Diamond)
            .SetGold(25000)
            .HideFromDungeonEditor()
            .AddToDB();

        var spell = BuildAlternateSpell(
            "WishObjectCreation",
            EffectDescriptionBuilder
                .Create()
                .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Self)
                .SetEffectForms(
                    EffectFormBuilder
                        .Create()
                        .SetSummonItemForm(item, 1)
                        .Build())
                .SetParticleEffectParameters(DivineWord)
                .Build());

        spell.AddCustomSubFeatures(StoreCreatedItemMarker);

        return spell;
    }

    private sealed class StoreCreatedItemInCasterInventory
    {
    }

    private SpellDefinition BuildInstantHealth()
    {
        var restorationForm =
            GreaterRestoration.EffectDescription.GetFirstFormOfType(EffectForm.EffectFormType.Condition);
        var targeting = new InstantHealthTargeting();

        return SpellDefinitionBuilder
            .Create("WishInstantHealth")
            .SetGuiPresentation(Category.Spell, _wishSprite)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
            .SetSpellLevel(WishSlotLevel)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetSomaticComponent(false)
            .SetVerboseComponent(true)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Distance, 24, TargetType.IndividualsUnique, 21)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetHealingForm(
                                HealingComputation.Dice,
                                700,
                                DieType.D1,
                                0,
                                false,
                                HealingCap.MaximumHitPoints)
                            .Build(),
                        restorationForm)
                    .SetParticleEffectParameters(Heal)
                    .Build())
            .AddCustomSubFeatures(
                targeting,
                new AlternateEffectFinished(this, RestoreInstantHealthTargets))
            .AddToDB();
    }

    private void BuildRealityRevisionOptions()
    {
        var conditionAid = ConditionDefinitionBuilder
            .Create("ConditionWishRealityRevisionAid")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionBlessed)
            .SetPossessive()
            .AddToDB();
        var conditionHinder = ConditionDefinitionBuilder
            .Create("ConditionWishRealityRevisionHinder")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionCursed)
            .SetConditionType(ConditionType.Detrimental)
            .SetPossessive()
            .AddToDB();

        conditionAid.AddCustomSubFeatures(
            new RealityRevisionOneShot(conditionAid, RealityRevisionMode.Aid));
        conditionHinder.AddCustomSubFeatures(
            new RealityRevisionOneShot(conditionHinder, RealityRevisionMode.Hinder));

        var aid = BuildAlternateSpell(
            "WishRealityRevisionAid",
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Round, 1, TurnOccurenceType.EndOfTurn)
                .SetTargetingData(Side.Ally, RangeType.Distance, 24, TargetType.IndividualsUnique)
                .SetEffectForms(EffectFormBuilder.ConditionForm(conditionAid))
                .SetParticleEffectParameters(DivineWord)
                .Build());
        var hinder = BuildAlternateSpell(
            "WishRealityRevisionHinder",
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Round, 1, TurnOccurenceType.EndOfTurn)
                .SetTargetingData(Side.Enemy, RangeType.Distance, 24, TargetType.IndividualsUnique)
                .SetEffectForms(EffectFormBuilder.ConditionForm(conditionHinder))
                .SetParticleEffectParameters(DivineWord)
                .Build());

        _realityRevisionOptions.Add(aid);
        _realityRevisionOptions.Add(hinder);
    }

    private SpellDefinition BuildAlternateSpell(string name, EffectDescription effectDescription)
    {
        return SpellDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(Category.Spell, _wishSprite)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
            .SetSpellLevel(WishSlotLevel)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetSomaticComponent(false)
            .SetVerboseComponent(true)
            .SetEffectDescription(effectDescription)
            .AddCustomSubFeatures(_stressOnFinished)
            .AddToDB();
    }

    private void BuildResistanceOptions()
    {
        foreach (var damageType in DamageTypes)
        {
            var suffix = damageType.StartsWith("Damage", StringComparison.Ordinal)
                ? damageType.Substring("Damage".Length)
                : damageType;
            var affinity = FeatureDefinitionDamageAffinityBuilder
                .Create($"DamageAffinityWishResistance{suffix}")
                .SetGuiPresentationNoContent(true)
                .SetDamageType(damageType)
                .SetDamageAffinityType(DamageAffinityType.Resistance)
                .AddToDB();
            var condition = ConditionDefinitionBuilder
                .Create($"ConditionWishResistance{suffix}")
                .SetGuiPresentation(
                    $"Spell/&WishResistance{suffix}Title",
                    $"Spell/&WishResistance{suffix}Description",
                    ConditionDefinitions.ConditionProtectedInsideMagicCircle)
                .SetPossessive()
                .SetFeatures(affinity)
                .AddToDB();
            var option = BuildAlternateSpell(
                $"WishResistance{suffix}",
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Permanent)
                    .SetTargetingData(Side.All, RangeType.Distance, 24, TargetType.IndividualsUnique, 10)
                    .SetEffectForms(EffectFormBuilder.ConditionForm(condition))
                    .SetParticleEffectParameters(ProtectionFromEnergy)
                    .Build());

            _resistanceOptions.Add(option);
        }
    }

    private void BuildSpellImmunityOptions(IEnumerable<SpellDefinition> roots)
    {
        foreach (var root in roots)
        {
            var immuneSpells = FlattenSpellTree(root).ToArray();
            var affinity = FeatureDefinitionMagicAffinityBuilder
                .Create($"MagicAffinityWishSpellImmunity{root.Name}")
                .SetGuiPresentationNoContent(true)
                .SetSpellImmunities(immuneSpells)
                .AddToDB();
            var condition = ConditionDefinitionBuilder
                .Create($"ConditionWishSpellImmunity{root.Name}")
                .SetGuiPresentation(
                    "Condition/&ConditionWishSpellImmunityTitle",
                    root.GuiPresentation.Title,
                    ConditionDefinitions.ConditionShielded)
                .SetPossessive()
                .SetFeatures(affinity)
                .AddToDB();
            var option = SpellDefinitionBuilder
                .Create($"WishSpellImmunity{root.Name}")
                .SetGuiPresentation(
                    root.GuiPresentation.Title,
                    "Spell/&WishSpellImmunityChoiceDescription",
                    _wishSprite)
                .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
                .SetSpellLevel(WishSlotLevel)
                .SetCastingTime(ActivationTime.Action)
                .SetMaterialComponent(MaterialComponentType.None)
                .SetSomaticComponent(false)
                .SetVerboseComponent(true)
                .SetEffectDescription(
                    EffectDescriptionBuilder
                        .Create()
                        .SetDurationData(DurationType.Hour, 8)
                        .SetTargetingData(Side.All, RangeType.Distance, 24, TargetType.IndividualsUnique, 10)
                        .SetEffectForms(EffectFormBuilder.ConditionForm(condition))
                        .SetParticleEffectParameters(ProtectionFromEnergy)
                        .Build())
                .AddCustomSubFeatures(_stressOnFinished)
                .AddToDB();

            _immunityOptions[root] = option;
        }
    }

    private static IEnumerable<SpellDefinition> FlattenSpellTree(SpellDefinition root)
    {
        var result = new HashSet<SpellDefinition>();
        var pending = new Stack<SpellDefinition>();

        pending.Push(root);

        while (pending.Count > 0)
        {
            var spell = pending.Pop();

            if (spell == null || !result.Add(spell))
            {
                continue;
            }

            foreach (var child in spell.SubspellsList ?? [])
            {
                pending.Push(child);
            }
        }

        return result;
    }

    private void BuildStressDefinitions()
    {
        var strengthModifier = FeatureDefinitionAttributeModifierBuilder
            .Create("AttributeModifierWishStrength")
            .SetGuiPresentationNoContent(true)
            .SetModifier(AttributeModifierOperation.Set, AttributeDefinitions.Strength, 3)
            .AddToDB();

        ConditionWishStrength = ConditionDefinitionBuilder
            .Create("ConditionWishStrength")
            .SetGuiPresentation(
                Category.Condition,
                ConditionDefinitions.ConditionCursedByBestowCurseAttackRoll)
            .SetConditionType(ConditionType.Detrimental)
            .SetPossessive()
            .SetFeatures(strengthModifier)
            .AddToDB();

        ConditionWishStress = ConditionDefinitionBuilder
            .Create("ConditionWishStress")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionExhausted)
            .SetConditionType(ConditionType.Detrimental)
            .SetPossessive()
            .AddCustomSubFeatures(new StressDamageOnSpellCasted())
            .AddToDB();

        ConditionWishLost = ConditionDefinitionBuilder
            .Create("ConditionWishLost")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionCursed)
            .SetConditionType(ConditionType.Neutral)
            .SetPossessive()
            .AddToDB();

        CastingValidator = new WishCastingValidator(ConditionWishLost);
    }

    private void ApplyStress(RulesetCharacter caster)
    {
        if (!caster.HasConditionOfType(ConditionWishStress))
        {
            InflictConditionUnlessPresent(caster, ConditionWishStress, DurationType.UntilLongRest, 0);
        }

        var days = DeterministicRandom.Range(1, 5) + DeterministicRandom.Range(1, 5);
        var rounds = ComputeRoundsDuration(DurationType.Day, days);

        if (caster.TryGetConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect,
                ConditionWishStrength.Name,
                out var activeStrength))
        {
            activeStrength.RemainingRounds = Math.Max(activeStrength.RemainingRounds, rounds);
        }
        else
        {
            InflictConditionUnlessPresent(caster, ConditionWishStrength, DurationType.Day, days);
        }

        if (!caster.HasConditionOfType(ConditionWishLost) && DeterministicRandom.Range(0, 100) < 33)
        {
            InflictConditionUnlessPresent(caster, ConditionWishLost, DurationType.Permanent, 0);
        }
    }

    private static void InflictConditionUnlessPresent(
        RulesetCharacter caster,
        ConditionDefinition condition,
        DurationType durationType,
        int durationParameter)
    {
        if (caster.HasConditionOfType(condition))
        {
            return;
        }

        caster.InflictCondition(
            condition.Name,
            durationType,
            durationParameter,
            TurnOccurenceType.EndOfTurn,
            AttributeDefinitions.TagEffect,
            caster.Guid,
            caster.CurrentFaction.Name,
            WishSlotLevel,
            condition.Name,
            0,
            0,
            0);
    }

    private static void RestoreInstantHealthTargets(CharacterActionMagicEffect action)
    {
        var caster = action.ActingCharacter.RulesetCharacter;
        var targets = action.ActionParams.TargetCharacters
            .Select(target => target.RulesetCharacter)
            .Append(caster)
            .Where(target => target != null)
            .Distinct()
            .ToArray();
        var removableConditions = GreaterRestoration.EffectDescription
            .GetFirstFormOfType(EffectForm.EffectFormType.Condition)
            .ConditionForm
            .ConditionsList;

        foreach (var target in targets)
        {
            if (!SimulacrumBehavior.TryGetSnapshot(target, out _) && target.MissingHitPoints > 0)
            {
                target.ForceSetHealth(target.MissingHitPoints, true);
            }

            foreach (var activeCondition in target.AllConditions
                         .Where(active => removableConditions.Any(
                             removable => active.ConditionDefinition.IsSubtypeOf(removable.Name)))
                         .ToArray())
            {
                target.RemoveCondition(activeCondition);
            }
        }
    }

    private sealed class InstantHealthTargeting :
        IFilterTargetingCharacter,
        IPowerOrSpellInitiatedByMe
    {
        public bool EnforceFullSelection => false;

        public bool IsValid(CursorLocationSelectTarget cursor, GameLocationCharacter target)
        {
            var caster = cursor.ActionParams.ActingCharacter;

            return target == caster ||
                   cursor.ActionParams.TargetCharacters.Count(character => character != caster) < 20;
        }

        public IEnumerator OnPowerOrSpellInitiatedByMe(
            CharacterActionMagicEffect action,
            BaseDefinition baseDefinition)
        {
            var caster = action.ActingCharacter;
            var targets = action.ActionParams.TargetCharacters;
            var actionModifiers = action.ActionParams.ActionModifiers;

            for (var i = targets.Count - 1; i >= 0; i--)
            {
                if (targets[i] != caster)
                {
                    continue;
                }

                targets.RemoveAt(i);

                if (i < actionModifiers.Count)
                {
                    actionModifiers.RemoveAt(i);
                }
            }

            yield break;
        }
    }

    private sealed class RealityRevisionOneShot(
        ConditionDefinition condition,
        RealityRevisionMode mode)
        : ITryAlterOutcomeAttack, ITryAlterOutcomeAttributeCheck, ITryAlterOutcomeSavingThrow
    {
        public int HandlerPriority => -1;

        public IEnumerator OnTryAlterOutcomeAttack(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            ActionModifier attackModifier,
            RulesetAttackMode attackMode,
            RulesetEffect rulesetEffect)
        {
            if (action.AttackRoll <= 0)
            {
                yield break;
            }

            bool forceSuccess;

            if (helper == attacker)
            {
                forceSuccess = mode == RealityRevisionMode.Aid;
            }
            else if (helper == defender)
            {
                forceSuccess = mode == RealityRevisionMode.Hinder;
            }
            else
            {
                yield break;
            }

            var finalOutcome = forceSuccess ? RollOutcome.Success : RollOutcome.Failure;

            action.AttackSuccessDelta = forceSuccess ? 1 : -1;
            action.AttackRollOutcome = finalOutcome;

            Consume(helper);

            yield break;
        }

        public IEnumerator OnTryAlterAttributeCheck(
            GameLocationBattleManager battleManager,
            int rawRoll,
            AbilityCheckData abilityCheckData,
            GameLocationCharacter defender,
            GameLocationCharacter helper)
        {
            if (helper != defender || rawRoll <= 0)
            {
                yield break;
            }

            var forceSuccess = mode == RealityRevisionMode.Aid;
            var originalDelta = abilityCheckData.AbilityCheckSuccessDelta;
            var finalDelta = forceSuccess ? 1 : -1;
            var finalOutcome = forceSuccess ? RollOutcome.Success : RollOutcome.Failure;

            abilityCheckData.AbilityCheckRoll += finalDelta - originalDelta;
            abilityCheckData.AbilityCheckSuccessDelta = finalDelta;
            abilityCheckData.AbilityCheckRollOutcome = finalOutcome;

            Consume(helper);

            yield break;
        }

        public IEnumerator OnTryAlterOutcomeSavingThrow(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            SavingThrowData savingThrowData,
            bool hasHitVisual)
        {
            if (helper != defender)
            {
                yield break;
            }

            var rawRoll =
                savingThrowData.SaveOutcomeDelta -
                savingThrowData.SaveBonusAndRollModifier +
                savingThrowData.SaveDC;

            if (rawRoll <= 0)
            {
                yield break;
            }

            var forceSuccess = mode == RealityRevisionMode.Aid;
            var finalOutcome = forceSuccess ? RollOutcome.Success : RollOutcome.Failure;

            savingThrowData.SaveOutcomeDelta = forceSuccess ? 1 : -1;
            savingThrowData.SaveOutcome = finalOutcome;
            Consume(helper);

            yield break;
        }

        private void Consume(GameLocationCharacter helper)
        {
            if (helper.RulesetCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect,
                    condition.Name,
                    out var activeCondition))
            {
                helper.RulesetCharacter.RemoveCondition(activeCondition);
            }
        }
    }

    private enum RealityRevisionMode
    {
        Aid,
        Hinder
    }

    private sealed class AlternateEffectFinished(
        WishBehavior behavior,
        Action<CharacterActionMagicEffect> beforeStress = null) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(
            CharacterActionMagicEffect action,
            BaseDefinition baseDefinition)
        {
            if (action.Countered || action.ExecutionFailed)
            {
                yield break;
            }

            beforeStress?.Invoke(action);
            behavior.ApplyStress(action.ActingCharacter.RulesetCharacter);
        }
    }

    private sealed class StressDamageOnSpellCasted : IOnSpellCasted
    {
        public int Priority => 100;

        public IEnumerator OnSpellCasted(
            RulesetCharacter featureOwner,
            GameLocationCharacter caster,
            CharacterActionCastSpell castAction,
            RulesetEffectSpell selectEffectSpell,
            RulesetSpellRepertoire selectedRepertoire,
            SpellDefinition selectedSpellDefinition)
        {
            var stressLevel = selectEffectSpell == null
                ? selectedSpellDefinition.SpellLevel
                : RulesetEffectSpellWithOrigin.GetResourceSlotLevel(selectEffectSpell);

            if (featureOwner != caster.RulesetCharacter ||
                castAction.Countered ||
                castAction.ExecutionFailed ||
                stressLevel <= 0)
            {
                yield break;
            }

            var damage = 0;

            for (var i = 0; i < stressLevel; i++)
            {
                damage += DeterministicRandom.Range(1, 11);
            }

            var previousIrreducibleDamage = _isApplyingIrreducibleDamage;

            _isApplyingIrreducibleDamage = true;

            try
            {
                featureOwner.SustainDamage(
                    damage,
                    DamageTypeNecrotic,
                    false,
                    featureOwner.Guid,
                    null,
                    out _);
            }
            finally
            {
                _isApplyingIrreducibleDamage = previousIrreducibleDamage;
            }
        }
    }

    private sealed class WishCastingValidator(ConditionDefinition conditionWishLost) : IValidateSpellCasting
    {
        public bool CanCastSpell(
            SpellCastingValidationContext context,
            out string failure)
        {
            if (!context.Caster.HasConditionOfType(conditionWishLost))
            {
                failure = string.Empty;
                return true;
            }

            failure = "Failure/&WishLost";
            return false;
        }
    }

    private sealed class SelectionSession : ICustomSubspellSelectionSession
    {
        private readonly WishBehavior _behavior;
        private readonly List<SpellDefinition>[] _activeRootsByLevel = new List<SpellDefinition>[9];
        private readonly RulesetCharacter _caster;
        private readonly SpellDefinition _masterSpell;
        private readonly Stack<List<Choice>> _previousPages = [];
        private readonly RulesetSpellRepertoire _repertoire;
        private List<Choice> _choices;

        internal SelectionSession(
            WishBehavior behavior,
            SpellDefinition masterSpell,
            RulesetCharacter caster,
            RulesetSpellRepertoire repertoire,
            List<SpellDefinition> activeRoots)
        {
            _behavior = behavior;
            _masterSpell = masterSpell;
            _caster = caster;
            _repertoire = repertoire;

            for (var level = 0; level <= 8; level++)
            {
                _activeRootsByLevel[level] = activeRoots
                    .Where(spell => spell.SpellLevel == level)
                    .ToList();
            }

            _choices =
            [
                new Choice(behavior.DuplicateSpell, ShowDuplicateLevels),
                new Choice(behavior.ObjectCreation, (session, modal) =>
                    session.CastAlternate(modal, behavior.ObjectCreation)),
                new Choice(behavior.InstantHealth, (session, modal) =>
                    session.CastAlternate(modal, behavior.InstantHealth)),
                new Choice(behavior.Resistance, ShowResistanceOptions),
                new Choice(behavior.SpellImmunity, ShowImmunityLevels),
                new Choice(behavior.RealityRevision, ShowRealityRevisionOptions)
            ];
        }

        public List<SpellDefinition> GetSubspells()
        {
            return _choices.Select(choice => choice.DisplaySpell).ToList();
        }

        public bool OnActivate(SubspellSelectionModal modal, int index)
        {
            if (index < 0 || index >= _choices.Count)
            {
                return false;
            }

            _choices[index].Activate(this, modal);

            return false;
        }

        private static void ShowDuplicateLevels(SelectionSession session, SubspellSelectionModal modal)
        {
            session.PushPage(
                modal,
                session._behavior._duplicateLevels
                    .Where(pair => session._activeRootsByLevel[pair.Key]
                        .Any(root => SpellCastingValidation.IsValid(
                            session._caster,
                            session._repertoire,
                            root,
                            null,
                            out _,
                            true)))
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new Choice(
                        pair.Value,
                        (current, currentModal) => current.ShowDuplicateRoots(currentModal, pair.Key)))
                    .ToList());
        }

        private static void ShowResistanceOptions(SelectionSession session, SubspellSelectionModal modal)
        {
            session.PushPage(
                modal,
                session._behavior._resistanceOptions
                    .Select(option => new Choice(
                        option,
                        (current, currentModal) => current.CastAlternate(currentModal, option)))
                    .ToList());
        }

        private static void ShowImmunityLevels(SelectionSession session, SubspellSelectionModal modal)
        {
            session.PushPage(
                modal,
                session._behavior._immunityLevels
                    .Where(pair => session._activeRootsByLevel[pair.Key]
                        .Any(session._behavior._immunityOptions.ContainsKey))
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new Choice(
                        pair.Value,
                        (current, currentModal) => current.ShowImmunityOptions(currentModal, pair.Key)))
                    .ToList());
        }

        private static void ShowRealityRevisionOptions(SelectionSession session, SubspellSelectionModal modal)
        {
            session.PushPage(
                modal,
                session._behavior._realityRevisionOptions
                    .Select(option => new Choice(
                        option,
                        (current, currentModal) => current.CastAlternate(currentModal, option)))
                    .ToList());
        }

        private void ShowDuplicateRoots(SubspellSelectionModal modal, int level)
        {
            PushPage(
                modal,
                _activeRootsByLevel[level]
                    .Where(root => SpellCastingValidation.IsValid(
                        _caster,
                        _repertoire,
                        root,
                        null,
                        out _,
                        true))
                    .Select(root => new Choice(
                        root,
                        (session, currentModal) => session.SelectDuplicateRoot(currentModal, root)))
                    .ToList());
        }

        private void SelectDuplicateRoot(SubspellSelectionModal modal, SpellDefinition root)
        {
            SelectDuplicateSpell(modal, root);
        }

        private void SelectDuplicateSpell(
            SubspellSelectionModal modal,
            SpellDefinition spell)
        {
            var children = GetSubspellsForReplication(spell);

            if (children.Count == 0)
            {
                if (spell.SubspellsList is { Count: > 0 })
                {
                    PushPage(modal, []);
                    return;
                }

                CastReplication(modal, spell);
                return;
            }

            PushPage(
                modal,
                children
                    .Select(child => new Choice(
                        child,
                        (session, currentModal) =>
                            session.SelectDuplicateSpell(currentModal, child)))
                    .ToList());
        }

        private List<SpellDefinition> GetSubspellsForReplication(SpellDefinition spell)
        {
            var children = UpcastConjureElementalAndFey.TryGetProvider(spell) != null
                ? UpcastConjureElementalAndFey.GetSubspells(spell, WishSlotLevel)
                : spell.SubspellsList;
            var gamingPlatformService = ServiceRepository.GetService<IGamingPlatformService>();
            var states = new Dictionary<SpellDefinition, SpellTreeState>();

            return children?
                       .Where(child =>
                           IsSafeSpellTree(child, gamingPlatformService, states) &&
                           SpellCastingValidation.IsValid(
                               _caster,
                               _repertoire,
                               child,
                               null,
                               out _,
                               true))
                       .Distinct()
                       .OrderBy(child => child.Name, StringComparer.Ordinal)
                       .ToList()
                   ?? [];
        }

        private void ShowImmunityOptions(SubspellSelectionModal modal, int level)
        {
            PushPage(
                modal,
                _activeRootsByLevel[level]
                    .Where(_behavior._immunityOptions.ContainsKey)
                    .Select(root => _behavior._immunityOptions[root])
                    .Select(option => new Choice(
                        option,
                        (session, currentModal) => session.CastAlternate(currentModal, option)))
                    .ToList());
        }

        private void CastReplication(
            SubspellSelectionModal modal,
            SpellDefinition spell)
        {
            using (RulesetEffectSpellWithOrigin.UseOrigin(
                       _caster,
                       _repertoire,
                       spell,
                       WishSlotLevel,
                       _masterSpell,
                       WishSlotLevel,
                       true,
                       RulesetEffectSpellWithOrigin.OriginMode.WishSpellReplication))
            {
                modal.spellCastEngaged?.Invoke(_repertoire, spell, WishSlotLevel);
            }

            modal.Hide();
        }

        private void CastAlternate(SubspellSelectionModal modal, SpellDefinition spell)
        {
            using (RulesetEffectSpellWithOrigin.UseOrigin(
                       _caster,
                       _repertoire,
                       spell,
                       WishSlotLevel,
                       _masterSpell,
                       WishSlotLevel,
                       true,
                       RulesetEffectSpellWithOrigin.OriginMode.WishAlternateEffect))
            {
                modal.spellCastEngaged?.Invoke(_repertoire, spell, WishSlotLevel);
            }

            modal.Hide();
        }

        private void PushPage(SubspellSelectionModal modal, List<Choice> choices)
        {
            _previousPages.Push(_choices);
            _choices =
            [
                new Choice(_behavior._back, (session, currentModal) => session.PopPage(currentModal)),
                .. choices
            ];
            SubspellSelectionModalPatcher.Refresh(modal);
        }

        private void PopPage(SubspellSelectionModal modal)
        {
            if (_previousPages.Count == 0)
            {
                return;
            }

            _choices = _previousPages.Pop();
            SubspellSelectionModalPatcher.Refresh(modal);
        }

        private sealed class Choice(
            SpellDefinition displaySpell,
            Action<SelectionSession, SubspellSelectionModal> activate)
        {
            internal readonly Action<SelectionSession, SubspellSelectionModal> Activate = activate;
            internal readonly SpellDefinition DisplaySpell = displaySpell;
        }
    }
}
