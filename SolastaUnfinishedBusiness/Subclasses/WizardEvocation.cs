using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionActionAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public sealed class WizardEvocation : AbstractSubclass
{
    private const string Name = "WizardEvocation";
    internal const string SpellTag = "Evoker";

    //
    // these lists contain all evocation spells that do damage in a non-vanilla way so they also get bonus
    //

    private static readonly HashSet<string> CantripsAdditionalDamages = new(StringComparer.Ordinal)
    {
        "AdditionalDamageBoomingBlade",
        "AdditionalDamageResonatingStrike", // Green-Flame Blade
        "AdditionalDamageSunlightBlade"
    };

    private static readonly Dictionary<string, string> BladeCantripAdditionalDamageBySpell = new(StringComparer.Ordinal)
    {
        { "BoomingBlade", "AdditionalDamageBoomingBlade" },
        { "ResonatingStrike", "AdditionalDamageResonatingStrike" },
        { "SunlightBlade", "AdditionalDamageSunlightBlade" }
    };

    private static readonly HashSet<string> SpellsAdditionalDamages = new(StringComparer.Ordinal)
    {
        "AdditionalDamageBanishingSmite",
        "AdditionalDamageBlindingSmite",
        "AdditionalDamageHolyWeapon",
        "AdditionalDamageSearingSmite",
        "AdditionalDamageStaggeringSmite",
        "AdditionalDamageWrathfulSmite"
    };

    private static readonly HashSet<string> SpellsPowerDamages = new(StringComparer.Ordinal)
    {
        "PowerCrownOfStars",
        "PowerHolyWeapon",
        "PowerThunderousSmite"
    };

    private static readonly FeatureDefinition FeatureSculptSpells = FeatureDefinitionBuilder
        .Create($"Feature{Name}SculptSpells")
        .SetGuiPresentation(Category.Feature)
        .AddCustomSubFeatures(new FilterRulesetEffectTargetsSculptSpells())
        .AddToDB();

    private static readonly FeatureDefinitionMagicAffinity MagicAffinityPotentCantrip =
        FeatureDefinitionMagicAffinityBuilder
            .Create($"MagicAffinity{Name}PotentCantrip")
            .SetGuiPresentation(Category.Feature)
            .SetCastingModifiers(halfDamageCantrips: true)
            .AddCustomSubFeatures(new CustomBehaviorPotentCantrips())
            .AddToDB();

    private static readonly FeatureDefinitionMagicAffinity MagicAffinitySavant = FeatureDefinitionMagicAffinityBuilder
        .Create($"MagicAffinity{Name}Savant")
        .SetGuiPresentation(Category.Feature)
        .SetSpellLearnAndPrepModifiers(
            0.5f, 0.5f, 0, AdvantageType.None, PreparedSpellsModifier.None)
        .AddCustomSubFeatures(new ModifyScribeCostAndDurationEvocationSavant())
        .AddToDB();

    private static readonly SpellListDefinition SpellListEvoker = SpellListDefinitionBuilder
        .Create(SpellListDefinitions.SpellListWizard, $"SpellList{Name}")
        .AddToDB();

    // no spell tag here as this work correctly with vanilla
    private static readonly FeatureDefinitionPointPool MagicAffinitySavant2024 = FeatureDefinitionPointPoolBuilder
        .Create($"MagicAffinity{Name}Savant2024")
        .SetGuiPresentation(Category.Feature)
        .SetSpellOrCantripPool(HeroDefinitions.PointsPoolType.Spell, 2, SpellListEvoker)
        .AddToDB();

    // need spell tag here to get this offered on level up and
    // let custom behavior at CharacterBuildingManager.FinalizeCharacter grant the spell
    private static readonly FeatureDefinitionPointPool MagicAffinitySavant2024Progression =
        FeatureDefinitionPointPoolBuilder
            .Create($"MagicAffinity{Name}Savant2024Progression")
            .SetGuiPresentationNoContent(true)
            .SetSpellOrCantripPool(HeroDefinitions.PointsPoolType.Spell, 1, SpellListEvoker, SpellTag)
            .AddToDB();

    private static CharacterSubclassDefinition _subclass;

    public WizardEvocation()
    {
        // LEVEL 02

        // Evocation Savant

        // Sculpt Spells

        // LEVEL 06

        // Potent Cantrip

        // LEVEL 10

        // Empowered Evocation

        var featureEmpoweredEvocation = FeatureDefinitionBuilder
            .Create($"Feature{Name}EmpoweredEvocation")
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(new MagicEffectBeforeHitConfirmedOnEnemyEmpoweredEvocation())
            .AddToDB();

        // LEVEL 14

        // Over Channel

        var conditionOverChannel = ConditionDefinitionBuilder
            .Create($"Condition{Name}OverChannel")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AllowMultipleInstances()
            .AddToDB();

        var conditionMaxDamage = ConditionDefinitionBuilder
            .Create($"Condition{Name}OverChannelMaxDamage")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddCustomSubFeatures(new ForceMaxDamageTypeDependentOverChannel())
            .AddToDB();

        var actionAffinityOverChannelToggle = FeatureDefinitionActionAffinityBuilder
            .Create(ActionAffinitySorcererMetamagicToggle, "ActionAffinityOverChannelToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((ActionDefinitions.Id)ExtraActionId.OverChannelToggle)
            .AddToDB();

        actionAffinityOverChannelToggle.AddCustomSubFeatures(
            new CustomBehaviorOverChannel(actionAffinityOverChannelToggle, conditionOverChannel, conditionMaxDamage));

        var featureSetOverChannel = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}OverChannel")
            .SetGuiPresentation(Category.Feature)
            .SetFeatureSet(actionAffinityOverChannelToggle)
            .AddToDB();

        //
        // Main
        //

        Subclass = CharacterSubclassDefinitionBuilder
            .Create(Name)
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite(Name, Resources.WizardEvocation, 256))
            .AddFeaturesAtLevel(2, MagicAffinitySavant, FeatureSculptSpells)
            .AddFeaturesAtLevel(6, MagicAffinityPotentCantrip)
            .AddFeaturesAtLevel(10, featureEmpoweredEvocation)
            .AddFeaturesAtLevel(14, featureSetOverChannel)
            .AddToDB();

        _subclass = Subclass;
    }

    internal override CharacterClassDefinition Klass => CharacterClassDefinitions.Wizard;

    internal override CharacterSubclassDefinition Subclass { get; }

    internal override FeatureDefinitionSubclassChoice SubclassChoice =>
        FeatureDefinitionSubclassChoices.SubclassChoiceWizardArcaneTraditions;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    internal static void LateLoad()
    {
        SwapSavantAndSavant2024();
        SwitchPotentCantrip2024();

        SpellListEvoker.SpellsByLevel.SetRange(
            SpellListDefinitions.SpellListWizard.SpellsByLevel
                .Select(spellByLevel => new SpellListDefinition.SpellsByLevelDuplet
                {
                    Level = spellByLevel.Level,
                    Spells = [.. spellByLevel.Spells.Where(x => x.SchoolOfMagic == SchoolEvocation)]
                }));
    }

    internal static void SwapEvocationPotentCantripAndSculptSpell()
    {
        var level = Main.Settings.EnableWizardToLearnSchoolAtLevel3 ? 3 : 2;
        var featureUnlockSculptSpell = _subclass.FeatureUnlocks.FirstOrDefault(x =>
            x.FeatureDefinition == FeatureSculptSpells);
        var featureUnlockMagicAffinityPotentCantrip = _subclass.FeatureUnlocks.FirstOrDefault(x =>
            x.FeatureDefinition == MagicAffinityPotentCantrip);

        if (Main.Settings.SwapEvocationPotentCantripAndSculptSpell)
        {
            featureUnlockSculptSpell!.level = 6;
            featureUnlockMagicAffinityPotentCantrip!.level = level;
        }
        else
        {
            featureUnlockSculptSpell!.level = level;
            featureUnlockMagicAffinityPotentCantrip!.level = 6;
        }

        _subclass.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchPotentCantrip2024()
    {
        MagicAffinityPotentCantrip.GuiPresentation.Description =
            Main.Settings.EnableEvocationPotentCantrip2024
                ? "Feature/&MagicAffinityWizardEvocationPotentCantrip2024Description"
                : "Feature/&MagicAffinityWizardEvocationPotentCantripDescription";
    }

    internal static void SwapSavantAndSavant2024()
    {
        var level = Main.Settings.EnableWizardToLearnSchoolAtLevel3 ? 3 : 2;

        _subclass.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == MagicAffinitySavant ||
            x.FeatureDefinition == MagicAffinitySavant2024 ||
            x.FeatureDefinition == MagicAffinitySavant2024Progression);

        if (Main.Settings.SwapEvocationSavant)
        {
            _subclass.FeatureUnlocks.Add(new FeatureUnlockByLevel(MagicAffinitySavant2024, level));

            for (var i = 5; i <= 20; i += 2)
            {
                _subclass.FeatureUnlocks.Add(new FeatureUnlockByLevel(MagicAffinitySavant2024Progression, i));
            }
        }
        else
        {
            _subclass.FeatureUnlocks.Add(new FeatureUnlockByLevel(MagicAffinitySavant, level));
        }

        _subclass.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    //
    // Evocation Savant
    //

    private sealed class ModifyScribeCostAndDurationEvocationSavant : IModifyScribeCostAndDuration
    {
        public void ModifyScribeCostMultiplier(
            RulesetCharacter character, SpellDefinition spellDefinition, ref float costMultiplier)
        {
            if (spellDefinition.SchoolOfMagic != SchoolEvocation)
            {
                costMultiplier = 1;
            }
        }

        public void ModifyScribeDurationMultiplier(
            RulesetCharacter character, SpellDefinition spellDefinition, ref float durationMultiplier)
        {
            if (spellDefinition.SchoolOfMagic != SchoolEvocation)
            {
                durationMultiplier = 1;
            }
        }
    }

    //
    // Sculpt Spells
    //

    private sealed class FilterRulesetEffectTargetsSculptSpells : IFilterRulesetEffectTargets
    {
        public bool CanAffectTarget(RulesetEffect rulesetEffect, GameLocationCharacter caster,
            GameLocationCharacter target)
        {
            if (rulesetEffect.SourceDefinition is not SpellDefinition { SchoolOfMagic: SchoolEvocation } spell ||
                spell.EffectDescription.TargetSide == Side.Ally ||
                spell.EffectDescription.TargetType == TargetType.Self)
            {
                return true;
            }

            if (target.IsOppositeSide(caster.Side)
                || (!Main.Settings.EvocationSculptSpellNoPerception && !caster.CanPerceiveTarget(target)))
            {
                return true;
            }

            return false;
        }
    }

    //
    // Potent Cantrips
    //

    private sealed class CustomBehaviorPotentCantrips
        : IMagicEffectBeforeHitConfirmedOnEnemy, IModifyAdditionalDamage, ITryAlterOutcomeAttack,
            IMagicEffectFinishedByMe, IPhysicalAttackFinishedByMe
    {
        private readonly Dictionary<RulesetEffect, bool> _halfDamageOnMissByEffect = [];

        private readonly Dictionary<(ulong Attacker, ulong Defender), BladeCantripMissDamage>
            _pendingBladeCantripMissDamages = [];

        public int HandlerPriority => -1;

        public IEnumerator OnMagicEffectBeforeHitConfirmedOnEnemy(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier actionModifier,
            RulesetEffect rulesetEffect,
            List<EffectForm> actualEffectForms,
            bool firstTarget,
            bool criticalHit)
        {
            if (Main.Settings.EnableEvocationPotentCantrip2024)
            {
                yield break;
            }

            var isCantrip = rulesetEffect.SourceDefinition is SpellDefinition { SpellLevel: 0 };

            if (!isCantrip ||
                !rulesetEffect.EffectDescription.NeedsToRollDie() ||
                (!firstTarget &&
                 rulesetEffect.EffectDescription.TargetType is TargetType.Individuals or TargetType.IndividualsUnique))
            {
                yield break;
            }

            var effectForm = actualEffectForms
                .FirstOrDefault(x => x.FormType == EffectForm.EffectFormType.Damage);

            if (effectForm == null)
            {
                yield break;
            }

            var pb = attacker.RulesetCharacter.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);

            effectForm.DamageForm.BonusDamage += pb;
        }

        // handle special blade cantrips use cases
        public void ModifyAdditionalDamage(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            FeatureDefinitionAdditionalDamage featureDefinitionAdditionalDamage,
            List<EffectForm> actualEffectForms,
            ref DamageForm damageForm)
        {
            if (Main.Settings.EnableEvocationPotentCantrip2024 ||
                !CantripsAdditionalDamages.Contains(featureDefinitionAdditionalDamage.Name))
            {
                return;
            }

            var pb = attacker.RulesetCharacter.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);

            damageForm.BonusDamage += pb;
        }

        public IEnumerator OnTryAlterOutcomeAttack(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            ActionModifier actionModifier,
            RulesetAttackMode attackMode,
            RulesetEffect rulesetEffect)
        {
            if (!Main.Settings.EnableEvocationPotentCantrip2024 ||
                helper != attacker ||
                action.AttackRollOutcome is not (RollOutcome.Failure or RollOutcome.CriticalFailure) ||
                rulesetEffect?.SourceDefinition is not SpellDefinition { SpellLevel: 0 } ||
                !rulesetEffect.EffectDescription.NeedsToRollDie() ||
                !rulesetEffect.EffectDescription.HasFormOfType(EffectForm.EffectFormType.Damage))
            {
                yield break;
            }

            if (!_halfDamageOnMissByEffect.ContainsKey(rulesetEffect))
            {
                _halfDamageOnMissByEffect.Add(rulesetEffect, rulesetEffect.EffectDescription.halfDamageOnAMiss);
            }

            rulesetEffect.EffectDescription.halfDamageOnAMiss = true;
        }

        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            var rulesetEffect = action.ActionParams.RulesetEffect;

            RestoreHalfDamageOnMiss(rulesetEffect);

            if (!Main.Settings.EnableEvocationPotentCantrip2024 ||
                rulesetEffect?.SourceDefinition is not SpellDefinition { SpellLevel: 0 } spellDefinition ||
                !spellDefinition.HasSubFeatureOfType<AttackAfterMagicEffect>() ||
                !BladeCantripAdditionalDamageBySpell.TryGetValue(spellDefinition.Name, out var additionalDamageName) ||
                !TryBuildBladeCantripMissDamage(attacker, additionalDamageName, out var bladeMissDamage))
            {
                yield break;
            }

            foreach (var target in targets.Where(x => x.RulesetCharacter != null))
            {
                _pendingBladeCantripMissDamages[GetBladeCantripMissDamageKey(attacker, target)] = bladeMissDamage;
            }
        }

        public IEnumerator OnPhysicalAttackFinishedByMe(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            RollOutcome rollOutcome,
            int damageAmount)
        {
            if (!Main.Settings.EnableEvocationPotentCantrip2024 ||
                attackMode == null ||
                !attackMode.AttackTags.Contains(AttackAfterMagicEffect.AttackAfterMagicEffectTag) ||
                defender.RulesetCharacter == null)
            {
                yield break;
            }

            var key = GetBladeCantripMissDamageKey(attacker, defender);

            if (!_pendingBladeCantripMissDamages.TryGetValue(key, out var bladeMissDamage))
            {
                yield break;
            }

            _pendingBladeCantripMissDamages.Remove(key);

            if (rollOutcome is not (RollOutcome.Failure or RollOutcome.CriticalFailure) ||
                bladeMissDamage.DiceNumber <= 0)
            {
                yield break;
            }

            ApplyBladeCantripMissDamage(attacker, defender, bladeMissDamage);
        }

        private void RestoreHalfDamageOnMiss(RulesetEffect rulesetEffect)
        {
            if (rulesetEffect == null ||
                !_halfDamageOnMissByEffect.TryGetValue(rulesetEffect, out var halfDamageOnMiss))
            {
                return;
            }

            rulesetEffect.EffectDescription.halfDamageOnAMiss = halfDamageOnMiss;
            _halfDamageOnMissByEffect.Remove(rulesetEffect);
        }

        private static (ulong Attacker, ulong Defender) GetBladeCantripMissDamageKey(
            GameLocationCharacter attacker,
            GameLocationCharacter defender)
        {
            return (attacker.RulesetCharacter.Guid, defender.RulesetCharacter.Guid);
        }

        private static bool TryBuildBladeCantripMissDamage(
            GameLocationCharacter attacker,
            string additionalDamageName,
            out BladeCantripMissDamage bladeMissDamage)
        {
            bladeMissDamage = default;

            if (!DatabaseRepository.GetDatabase<FeatureDefinitionAdditionalDamage>()
                    .TryGetElement(additionalDamageName, out var additionalDamage))
            {
                return false;
            }

            var diceNumber = additionalDamage.DamageDiceNumber;

            if ((ExtraAdditionalDamageAdvancement)additionalDamage.DamageAdvancement ==
                ExtraAdditionalDamageAdvancement.CharacterLevel)
            {
                diceNumber = additionalDamage.GetDiceOfRank(
                    attacker.RulesetCharacter.TryGetAttributeValue(AttributeDefinitions.CharacterLevel));
            }

            bladeMissDamage = new BladeCantripMissDamage(
                additionalDamage.SpecificDamageType,
                additionalDamage.DamageDieType,
                diceNumber);

            return true;
        }

        private static void ApplyBladeCantripMissDamage(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            BladeCantripMissDamage bladeMissDamage)
        {
            var rulesetAttacker = attacker.RulesetCharacter;
            var rulesetDefender = defender.RulesetCharacter;
            var effectForm = EffectFormBuilder.DamageForm(
                bladeMissDamage.DamageType, bladeMissDamage.DiceNumber, bladeMissDamage.DieType);
            var damageForm = effectForm.DamageForm;
            var rolledValues = new List<int>();
            var damage = rulesetAttacker.RollDamage(
                damageForm,
                0,
                false,
                0,
                0,
                0.5f,
                false,
                false,
                false,
                rolledValues,
                true);

            if (damage <= 0)
            {
                return;
            }

            var applyFormsParams = new RulesetImplementationDefinitions.ApplyFormsParams
            {
                sourceCharacter = rulesetAttacker,
                targetCharacter = rulesetDefender,
                position = defender.LocationPosition
            };

            RulesetActor.InflictDamage(
                damage,
                damageForm,
                damageForm.DamageType,
                applyFormsParams,
                rulesetDefender,
                false,
                attacker.Guid,
                false,
                [],
                new RollInfo(damageForm.DieType, rolledValues, 0),
                false,
                out _);
        }

        private readonly struct BladeCantripMissDamage(
            string damageType,
            DieType dieType,
            int diceNumber)
        {
            internal string DamageType { get; } = damageType;
            internal DieType DieType { get; } = dieType;
            internal int DiceNumber { get; } = diceNumber;
        }
    }

    //
    // Empowered Evocation
    //

    private sealed class MagicEffectBeforeHitConfirmedOnEnemyEmpoweredEvocation
        : IMagicEffectBeforeHitConfirmedOnEnemy, IModifyAdditionalDamage
    {
        public IEnumerator OnMagicEffectBeforeHitConfirmedOnEnemy(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier actionModifier,
            RulesetEffect rulesetEffect,
            List<EffectForm> actualEffectForms,
            bool firstTarget,
            bool criticalHit)
        {
            var isSpell = rulesetEffect.SourceDefinition is SpellDefinition;

            switch (isSpell)
            {
                case false when !SpellsPowerDamages.Contains(rulesetEffect.SourceDefinition.Name):
                case true when rulesetEffect.SchoolOfMagic != SchoolEvocation:
                case true when !firstTarget &&
                               rulesetEffect.EffectDescription.TargetType
                                   is TargetType.Individuals
                                   or TargetType.IndividualsUnique:
                    yield break;
            }

            var effectForm = actualEffectForms
                .FirstOrDefault(x => x.FormType == EffectForm.EffectFormType.Damage);

            if (effectForm == null)
            {
                yield break;
            }

            var intelligenceModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                attacker.RulesetCharacter.TryGetAttributeValue(AttributeDefinitions.Intelligence));

            effectForm.DamageForm.BonusDamage += Math.Max(1, intelligenceModifier);
        }

        // handle special blade cantrips and other spells additional damages use cases
        public void ModifyAdditionalDamage(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            FeatureDefinitionAdditionalDamage featureDefinitionAdditionalDamage,
            List<EffectForm> actualEffectForms,
            ref DamageForm damageForm)
        {
            var featureName = featureDefinitionAdditionalDamage.Name;

            if (!CantripsAdditionalDamages.Contains(featureName) && !SpellsAdditionalDamages.Contains(featureName))
            {
                return;
            }

            var intelligenceModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                attacker.RulesetCharacter.TryGetAttributeValue(AttributeDefinitions.Intelligence));

            damageForm.BonusDamage += Math.Max(1, intelligenceModifier);
        }
    }

    //
    // Over Channel
    //

    private sealed class ForceMaxDamageTypeDependentOverChannel : IForceMaxDamageTypeDependent
    {
        public bool IsValid(RulesetActor rulesetActor, DamageForm damageForm)
        {
            return true;
        }
    }

    private sealed class CustomBehaviorOverChannel(
        FeatureDefinition featureOverChannel,
        ConditionDefinition conditionOverChannel,
        ConditionDefinition conditionOverChannelMaxDamage)
        : IMagicEffectBeforeHitConfirmedOnEnemy, IMagicEffectFinishedByMe
    {
        public IEnumerator OnMagicEffectBeforeHitConfirmedOnEnemy(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier actionModifier,
            RulesetEffect rulesetEffect,
            List<EffectForm> actualEffectForms,
            bool firstTarget,
            bool criticalHit)
        {
            var rulesetAttacker = attacker.RulesetCharacter;

            // only spells between 1st and 5th levels
            if (!firstTarget ||
                !rulesetAttacker.IsToggleEnabled((ActionDefinitions.Id)ExtraActionId.OverChannelToggle) ||
                rulesetEffect.SourceDefinition is not SpellDefinition spellDefinition ||
                spellDefinition.SpellLevel == 0 ||
                spellDefinition.SpellLevel > 5)
            {
                yield break;
            }

            // allow max spell damage on this attack
            EffectHelpers.StartVisualEffect(
                attacker, attacker, PowerFighterActionSurge, EffectHelpers.EffectType.Caster);
            rulesetAttacker.LogCharacterUsedFeature(featureOverChannel);
            rulesetAttacker.InflictCondition(
                conditionOverChannelMaxDamage.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetAttacker.guid,
                rulesetAttacker.CurrentFaction.Name,
                1,
                conditionOverChannelMaxDamage.Name,
                0,
                0,
                0);
        }

        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action, GameLocationCharacter attacker, List<GameLocationCharacter> targets)
        {
            var rulesetAttacker = attacker.RulesetCharacter;

            if (!rulesetAttacker.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, conditionOverChannelMaxDamage.Name, out var actionCondition))
            {
                yield break;
            }

            rulesetAttacker.RemoveCondition(actionCondition);

            // add one instance of over channel
            rulesetAttacker.InflictCondition(
                conditionOverChannel.Name,
                DurationType.UntilLongRest,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetAttacker.guid,
                rulesetAttacker.CurrentFaction.Name,
                1,
                conditionOverChannel.Name,
                0,
                0,
                0);

            var overChannelInstancesCount =
                rulesetAttacker.AllConditions.Count(x => x.ConditionDefinition == conditionOverChannel);

            // first time used so no self damage
            if (overChannelInstancesCount <= 1)
            {
                yield break;
            }

            const DieType DIE_TYPE = DieType.D12;

            var rulesetEffect = action.ActionParams.RulesetEffect;
            var diceNumber = overChannelInstancesCount * rulesetEffect.EffectLevel;
            var rolls = new List<int>();
            var damage = rulesetAttacker.RollDiceAndSum(DIE_TYPE, RollContext.None, diceNumber, rolls, false);

            EffectHelpers.StartVisualEffect(
                attacker, attacker, PowerPatronFiendDarkOnesOwnLuck, EffectHelpers.EffectType.Effect);
            rulesetAttacker.SustainDamage(
                damage, DamageTypeNecrotic, false, rulesetAttacker.Guid,
                new RollInfo(DIE_TYPE, rolls, 0), out _);
        }
    }
}
