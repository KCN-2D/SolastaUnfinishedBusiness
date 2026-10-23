using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine;
using Resources = SolastaUnfinishedBusiness.Properties.Resources;
using static RuleDefinitions;
using static ActionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionConditionAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionDamageAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionMagicAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionMovementAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSenses;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.MonsterDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public sealed class InnovationArtillerist : AbstractSubclass
{
    private const string Name = "InnovationArtillerist";
    private const string CreatureTag = "EldritchCannon";
    private const string ConditionCommandCannon = $"Condition{Name}Command";
    private const string PowerSummonCannon = $"Power{Name}Summon";
    private const string EldritchCannon = "EldritchCannon";
    private const string ExplosiveCannon = "ExplosiveCannon";
    private const string FortifiedPosition = "FortifiedPosition";
    private const string Flamethrower = "Flamethrower";
    private const string ForceBallista = "ForceBallista";
    private const string Protector = "Protector";
    private const string ArcaneFirearm = "ArcaneFirearm";
    private const string SecondCannon = $"Condition{Name}SecondCannon";
    private const float CannonModelScale = 0.54f;
    private const float CannonBodyLift = 0.08f;
    private static FeatureDefinitionPower _eldritchCannonPool;
    private static FeatureDefinitionPower _explosiveCannonPool;
    private static FeatureDefinitionPower _fortifiedPositionPool;
    private static FeatureDefinitionFeatureSet _eldritchCannonFeatureSet;
    private static FeatureDefinitionFeatureSet _fortifiedPositionFeatureSet;
    private static FeatureDefinitionFeatureSet _arcaneFirearmFeatureSet;
    private static FeatureDefinitionAdditionalDamage _arcaneFirearmDamage;
    private static FeatureDefinitionPower[] _fortifiedSummons;
    private static ConditionDefinition _tinyCannonActivated;
    private static FeatureDefinition[] _cannonModeFeatures;
    private static FeatureDefinitionActionAffinity[] _tinyCannonModeFeatures;
    private static readonly Dictionary<MonsterDefinition, FeatureDefinition[]> CannonModes = [];
    private static readonly Dictionary<ConditionDefinition, FeatureDefinitionActionAffinity> TinyCannonModes = [];
    private static readonly Dictionary<BaseDefinition, (string Title, string Description)> CannonPresentations = [];
    private static readonly List<(FeatureDefinitionPower Pool, FeatureDefinitionPower[] Fixed,
        FeatureDefinitionPower[] Flexible)> CannonChoiceBundles = [];
    private static readonly List<ConditionDefinition> CannonTurnConditions = [];
    private static readonly string[] CannonTypes = [Flamethrower, ForceBallista, Protector];
    private static readonly List<FeatureDefinitionPower> SummonPowers = [];
    private static readonly List<EffectForm> CannonDamageForms = [];
    private static readonly ConditionDefinition SpellSlotCreation = ConditionDefinitionBuilder
        .Create($"Condition{Name}CannonSpellSlotCreation")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.Always)
        .AddToDB();
    private static readonly ConditionDefinition CannonCreationDebt = ConditionDefinitionBuilder
        .Create($"Condition{Name}CannonCreationDebt")
        .SetGuiPresentationNoContent(true)
        .SetAmountOrigin(ConditionDefinition.OriginOfAmount.Fixed)
        .SetSilent(Silent.Always)
        .AddToDB();

    private static readonly LimitEffectInstances CannonLimiter =
        new(CreatureTag, character => character.GetClassLevel(InventorClass.Class) < 15 ? 1 : 2, ownsUniqueness: true);

    // Cannon Features

    private static readonly FeatureDefinitionActionAffinity ActionAffinityEldritchCannon =
        FeatureDefinitionActionAffinityBuilder
            .Create($"ActionAffinity{Name}{EldritchCannon}")
            .SetGuiPresentationNoContent(true)
            .SetForbiddenActions(
                Id.AttackMain, Id.AttackOff, Id.AttackFree, Id.AttackReadied, Id.AttackOpportunity, Id.Ready,
                Id.Shove, Id.ShoveBonus, Id.ShoveFree,
                (Id)ExtraActionId.Grapple, (Id)ExtraActionId.GrappleBonus, (Id)ExtraActionId.GrappleNoCost)
            .AddToDB();

    private static readonly FeatureDefinitionActionAffinity ActionAffinityUncommandedCannon =
        FeatureDefinitionActionAffinityBuilder
            .Create($"ActionAffinity{Name}UncommandedCannon")
            .SetGuiPresentationNoContent(true)
            .SetForbiddenActions(
                Id.PowerMain, Id.PowerBonus, Id.PowerNoCost, Id.PowerReaction, Id.SpendPower,
                (Id)ExtraActionId.CannonFlamethrower, (Id)ExtraActionId.CannonForceBallista,
                (Id)ExtraActionId.CannonProtector)
            .AddToDB();

    private static readonly FeatureDefinitionConditionAffinity ConditionAffinityEldritchCannon =
        FeatureDefinitionConditionAffinityBuilder
            .Create($"ConditionAffinity{Name}{EldritchCannon}")
            .SetGuiPresentationNoContent(true)
            .SetConditionAffinityType(ConditionAffinityType.Immunity)
            .SetConditionType(DatabaseHelper.ConditionDefinitions.ConditionSurprised)
            .AddCustomSubFeatures(ForceInitiativeToSummoner.Mark)
            .AddToDB();

    private static readonly FeatureDefinitionMoveMode MoveModeEldritchCannon =
        FeatureDefinitionMoveModeBuilder
            .Create("MoveModeWalk3")
            .SetGuiPresentation(DatabaseHelper.FeatureDefinitionMoveModes.MoveModeMove12.GuiPresentation)
            .SetMode(MoveMode.Walk, 3)
            .AddToDB();

    public InnovationArtillerist()
    {
        #region COMMON

        var eldritchCannonSprite = Sprites.GetSprite(EldritchCannon, Resources.PowerEldritchCannon, 256, 128);

        // Cannon Powers

        var powerFlamethrower = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{Flamethrower}")
            .SetGuiPresentation(Category.Feature, BurningHands, hidden: true)
            .SetUsesFixed(ActivationTime.Action)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Cone, 3)
                    .ExcludeCaster()
                    .SetSavingThrowData(
                        false, AttributeDefinitions.Dexterity, false, EffectDifficultyClassComputation.FixedValue)
                    .SetParticleEffectParameters(BurningHands)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetDamageForm(DamageTypeFire, 2, DieType.D8)
                            .SetDiceAdvancement(LevelSourceType.ClassLevel, 0, 1, 6, 3)
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .Build(),
                        EffectFormBuilder
                            .Create()
                            .SetAlterationForm(AlterationForm.Type.LightUp)
                            .Build())
                    .Build())
            .DelegatedToAction()
            // required as added through condition
            .AddCustomSubFeatures(ClassHolder.Inventor)
            .AddToDB();

        CannonDamageForms.Add(powerFlamethrower.EffectDescription.EffectForms[0]);
        powerFlamethrower.AddCustomSubFeatures(new CannonPowerStatistics(powerFlamethrower),
            PowerBundle.UncachedEffectDescription.Marker);

        var powerForceBallista = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{ForceBallista}")
            .SetGuiPresentation(Category.Feature, EldritchBlast, hidden: true)
            .SetUsesFixed(ActivationTime.Action)
            .SetUseSpellAttack()
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.RangeHit, 24, TargetType.IndividualsUnique)
                    .SetParticleEffectParameters(EldritchBlast)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetDamageForm(DamageTypeForce, 2, DieType.D8)
                            .SetDiceAdvancement(LevelSourceType.ClassLevel, 0, 1, 6, 3)
                            .Build(),
                        EffectFormBuilder
                            .Create()
                            .SetMotionForm(MotionForm.MotionType.PushFromOrigin, 1)
                            .Build())
                    .Build())
            .DelegatedToAction()
            // required as added through condition
            .AddCustomSubFeatures(ClassHolder.Inventor)
            .AddToDB();

        CannonDamageForms.Add(powerForceBallista.EffectDescription.EffectForms[0]);
        powerForceBallista.AddCustomSubFeatures(new CannonPowerStatistics(powerForceBallista),
            PowerBundle.UncachedEffectDescription.Marker);

        var powerProtector = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{Protector}")
            .SetGuiPresentation(Category.Feature, MassCureWounds, hidden: true)
            .SetUsesFixed(ActivationTime.Action)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(MassCureWounds)
                    .SetDurationData(DurationType.UntilAnyRest)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Sphere, 2)
                    .SetParticleEffectParameters(MassCureWounds)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetTempHpForm(5, DieType.D8, 1)
                            .Build())
                    .Build())
            .DelegatedToAction()
            .AddToDB();
        powerProtector.AddCustomSubFeatures(new CannonPowerStatistics(powerProtector),
            PowerBundle.UncachedEffectDescription.Marker);

        // Action Affinities Medium Cannon

        var actionAffinityFlamethrower =
            BuildCannonActionAffinity(Flamethrower, ExtraActionId.CannonFlamethrower);

        var actionAffinityForceBallista =
            BuildCannonActionAffinity(ForceBallista, ExtraActionId.CannonForceBallista);

        var actionAffinityProtector =
            BuildCannonActionAffinity(Protector, ExtraActionId.CannonProtector);

        // Action Affinities Tiny Cannon

        var actionAffinityFlamethrowerTiny =
            BuildCannonActionAffinity($"{Flamethrower}Tiny", ExtraActionId.CannonFlamethrowerBonus);

        var actionAffinityForceBallistaTiny =
            BuildCannonActionAffinity($"{ForceBallista}Tiny", ExtraActionId.CannonForceBallistaBonus);

        var actionAffinityProtectorTiny =
            BuildCannonActionAffinity($"{Protector}Tiny", ExtraActionId.CannonProtectorBonus);

        _cannonModeFeatures =
        [
            powerFlamethrower, powerForceBallista, powerProtector,
            actionAffinityFlamethrower, actionAffinityForceBallista, actionAffinityProtector
        ];
        _tinyCannonModeFeatures =
            [actionAffinityFlamethrowerTiny, actionAffinityForceBallistaTiny, actionAffinityProtectorTiny];
        ActionAffinityEldritchCannon.AddCustomSubFeatures(new RestoreCannonModes());

        // Medium Cannon Conditions

        var conditionFlamethrower = ConditionDefinitionBuilder
            .Create($"Condition{Name}{Flamethrower}")
            .SetGuiPresentation($"Power{Name}{Flamethrower}", Category.Feature)
            .SetPossessive()
            .AllowMultipleInstances()
            .AddToDB();

        var conditionForceBallista = ConditionDefinitionBuilder
            .Create($"Condition{Name}{ForceBallista}")
            .SetGuiPresentation($"Power{Name}{ForceBallista}", Category.Feature)
            .SetPossessive()
            .AllowMultipleInstances()
            .AddToDB();

        var conditionProtector = ConditionDefinitionBuilder
            .Create($"Condition{Name}{Protector}")
            .SetGuiPresentation($"Power{Name}{Protector}", Category.Feature)
            .SetPossessive()
            .AllowMultipleInstances()
            .AddToDB();

        // Tiny Cannon Conditions

        var conditionFlamethrowerTiny = ConditionDefinitionBuilder
            .Create($"Condition{Name}{Flamethrower}Tiny")
            .SetGuiPresentation($"Power{Name}{Flamethrower}", Category.Feature)
            .SetPossessive()
            .SetFeatures(actionAffinityFlamethrowerTiny)
            .AddToDB();

        var conditionForceBallistaTiny = ConditionDefinitionBuilder
            .Create($"Condition{Name}{ForceBallista}Tiny")
            .SetGuiPresentation($"Power{Name}{ForceBallista}", Category.Feature)
            .SetPossessive()
            .SetFeatures(actionAffinityForceBallistaTiny)
            .AddToDB();

        var conditionProtectorTiny = ConditionDefinitionBuilder
            .Create($"Condition{Name}{Protector}Tiny")
            .SetGuiPresentation($"Power{Name}{Protector}", Category.Feature)
            .SetPossessive()
            .SetFeatures(actionAffinityProtectorTiny)
            .AddToDB();

        TinyCannonModes.Add(conditionFlamethrowerTiny, actionAffinityFlamethrowerTiny);
        TinyCannonModes.Add(conditionForceBallistaTiny, actionAffinityForceBallistaTiny);
        TinyCannonModes.Add(conditionProtectorTiny, actionAffinityProtectorTiny);
        foreach (var condition in new[] { conditionFlamethrower, conditionForceBallista, conditionProtector,
                     conditionFlamethrowerTiny, conditionForceBallistaTiny, conditionProtectorTiny })
        {
            CannonPresentations.Add(condition, (condition.GuiPresentation.Title, condition.GuiPresentation.Description));
        }

        // Medium Cannon Actions

        BuildCannonAction(
            Flamethrower, Flamethrower, ExtraActionId.CannonFlamethrower, ActionType.Main, powerFlamethrower);

        BuildCannonAction(
            ForceBallista, ForceBallista, ExtraActionId.CannonForceBallista, ActionType.Main, powerForceBallista);

        BuildCannonAction(
            Protector, Protector, ExtraActionId.CannonProtector, ActionType.Main, powerProtector);

        // Tiny Cannon Actions

        BuildCannonAction(
            $"{Flamethrower}Tiny", Flamethrower, ExtraActionId.CannonFlamethrowerBonus, ActionType.Bonus,
            powerFlamethrower);

        BuildCannonAction(
            $"{ForceBallista}Tiny", ForceBallista, ExtraActionId.CannonForceBallistaBonus, ActionType.Bonus,
            powerForceBallista);

        BuildCannonAction(
            $"{Protector}Tiny", Protector, ExtraActionId.CannonProtectorBonus, ActionType.Bonus, powerProtector);

        #endregion

        #region LEVEL 03

        // LEVEL 03

        // Auto Prepared Spell

        var autoPreparedSpells = FeatureDefinitionAutoPreparedSpellsBuilder
            .Create($"AutoPreparedSpells{Name}")
            .SetGuiPresentation(Category.Feature)
            .SetSpellcastingClass(InventorClass.Class)
            .SetAutoTag("InventorArtillerist")
            .AddPreparedSpellGroup(3, Shield, Thunderwave)
            .AddPreparedSpellGroup(5, ScorchingRay, Shatter)
            .AddPreparedSpellGroup(9, Fireball, WindWall)
            .AddPreparedSpellGroup(13, IceStorm, WallOfFire)
            .AddPreparedSpellGroup(17, ConeOfCold, SpellsContext.SonicBoom)
            .AddToDB();

        // Summoning Affinities

        var hpBonus = FeatureDefinitionAttributeModifierBuilder
            .Create($"AttributeModifier{Name}{EldritchCannon}HitPoints")
            .SetGuiPresentationNoContent(true)
            .SetAddConditionAmount(AttributeDefinitions.HitPoints)
            .AddToDB();
        var hpBaseAdjustment = FeatureDefinitionAttributeModifierBuilder
            .Create($"AttributeModifier{Name}{EldritchCannon}BaseHitPoints")
            .SetGuiPresentationNoContent(true)
            .SetModifier(FeatureDefinitionAttributeModifier.AttributeModifierOperation.Additive,
                AttributeDefinitions.HitPoints, -1)
            .AddToDB();

        var summoningAffinityEldritchCannon = FeatureDefinitionSummoningAffinityBuilder
            .Create($"SummoningAffinity{Name}{EldritchCannon}")
            .SetGuiPresentationNoContent(true)
            .SetRequiredMonsterTag(CreatureTag)
            .SetAddedConditions(
                ConditionDefinitionBuilder
                    .Create($"Condition{Name}{EldritchCannon}CopyCharacterLevel")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetAmountOrigin(ExtraOriginOfAmount.SourceCopyAttributeFromSummoner,
                        AttributeDefinitions.CharacterLevel)
                    .AddToDB(),
                ConditionDefinitionBuilder
                    .Create($"Condition{Name}{EldritchCannon}HitPoints")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetFeatures(hpBonus, hpBonus, hpBonus, hpBonus, hpBonus)
                    .SetAmountOrigin(ExtraOriginOfAmount.SourceClassLevel, InventorClass.ClassName)
                    .AddToDB(),
                ConditionDefinitionBuilder
                    .Create($"Condition{Name}{EldritchCannon}BaseHitPoints")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    // Dynamic condition amounts also replace additive modifier values.
                    // Apply this fixed adjustment separately, after the full HP bonus.
                    .SetFeatures(hpBaseAdjustment)
                    .AddToDB())
            .AddToDB();

        // Command Cannon

        var conditionEldritchCannonCommand = ConditionDefinitionBuilder
            .Create(ConditionCommandCannon)
            .SetGuiPresentation(Category.Condition, Sprites.GetConditionSprite("ConditionEldritchCannonCommanded"))
            .SetConditionType(ConditionType.Beneficial)
            .SetPossessive()
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();

        var powerEldritchCannonCommand = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{EldritchCannon}Command")
            .SetGuiPresentation(Category.Feature, Command)
            .SetUsesFixed(ActivationTime.BonusAction)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1, TurnOccurenceType.StartOfTurn)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(conditionEldritchCannonCommand, ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(ValidatorsValidatePowerUse.InCombat, new ValidatorsValidatePowerUse(character =>
                HasCannon(character) ||
                GetCannons(character).Count() > 1 && HasTinyCannon(character)))
            .AddToDB();

        var cannonCommand = new InventorClass.ConstructCommand(
            conditionEldritchCannonCommand, powerEldritchCannonCommand, CreatureTag, false, 12);
        conditionEldritchCannonCommand.AddCustomSubFeatures(cannonCommand);
        powerEldritchCannonCommand.AddCustomSubFeatures(cannonCommand);
        powerEldritchCannonCommand.AddCustomSubFeatures(new ValidatorsValidatePowerUse(character =>
            cannonCommand.HasCommandableConstruct(character) ||
            GetCannons(character).Count() > 1 && HasTinyCannon(character)));
        ActionAffinityUncommandedCannon.AddCustomSubFeatures(cannonCommand);

        // Dismiss Cannon

        var powerEldritchCannonDismiss = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{EldritchCannon}Dismiss")
            .SetGuiPresentation(Category.Feature, PowerSorcererManaPainterTap)
            .SetUsesFixed(ActivationTime.Action)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Distance, 12, TargetType.IndividualsUnique)
                    .SetRestrictedCreatureFamilies(InventorClass.InventorConstructFamily)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetCounterForm(CounterForm.CounterType.DismissCreature, 0, 0, false, false)
                            .Build())
                    .SetParticleEffectParameters(Counterspell)
                    .Build())
            .AddCustomSubFeatures(new ValidatorsValidatePowerUse(HasCannon), OwnCannonTargetFilter.Instance)
            .AddToDB();

        // Refund Cannon

        var powerEldritchCannonRefund = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{EldritchCannon}Refund")
            .SetGuiPresentation(Category.Feature, PowerDomainInsightForeknowledge)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetCasterEffectParameters(PowerMartialSpellbladeArcaneEscape)
                    .Build())
            .AddCustomSubFeatures(new CustomBehaviorRefundCannon())
            .AddToDB();

        // Eldritch Cannon

        var powerEldritchCannonPool = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{EldritchCannon}Activate")
            .SetGuiPresentation(Category.Feature, eldritchCannonSprite)
            .SetUsesFixed(ActivationTime.Action, RechargeRate.LongRest)
            // only for UI reasons
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Hour, 1)
                    .SetTargetingData(Side.All, RangeType.Distance, 1, TargetType.Position)
                    .Build())
            .AddToDB();
        _eldritchCannonPool = powerEldritchCannonPool;

        var powerFlamethrower03 =
            BuildFlamethrowerPower(powerEldritchCannonPool, conditionFlamethrower, 3,
                powerFlamethrower, actionAffinityFlamethrower);
        var powerForceBallista03 =
            BuildForceBallistaPower(powerEldritchCannonPool, conditionForceBallista, 3,
                powerForceBallista, actionAffinityForceBallista);
        var powerProtector03 =
            BuildProtectorPower(powerEldritchCannonPool, conditionProtector, 3,
                powerProtector, actionAffinityProtector);
        var powerTinyFlamethrower03 =
            BuildTinyFlamethrowerPower(powerEldritchCannonPool, conditionFlamethrowerTiny, 3);
        var powerTinyForceBallista03 =
            BuildTinyForceBallistaPower(powerEldritchCannonPool, conditionForceBallistaTiny, 3);
        var powerTinyProtector03 =
            BuildTinyProtectorPower(powerEldritchCannonPool, conditionProtectorTiny, 3);

        var featureSetEldritchCannon = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}{EldritchCannon}")
            .SetGuiPresentation(Category.Feature)
            .AddFeatureSet(
                summoningAffinityEldritchCannon,
                powerEldritchCannonCommand,
                powerEldritchCannonDismiss,
                powerEldritchCannonRefund,
                powerEldritchCannonPool,
                powerFlamethrower03,
                powerForceBallista03,
                powerProtector03,
                powerTinyFlamethrower03,
                powerTinyForceBallista03,
                powerTinyProtector03,
                powerFlamethrower,
                powerForceBallista,
                powerProtector)
            .AddToDB();
        _eldritchCannonFeatureSet = featureSetEldritchCannon;
        BuildCannonCreationBundle(powerEldritchCannonPool, featureSetEldritchCannon, 3,
            powerFlamethrower03, powerForceBallista03, powerProtector03,
            powerTinyFlamethrower03, powerTinyForceBallista03, powerTinyProtector03);

        #endregion

        #region LEVEL 05

        // LEVEL 05

        // Arcane Firearm

        const string ARCANE_FIREARM = $"FeatureSet{Name}{ArcaneFirearm}";

        var additionalDamageArcaneFirearm = FeatureDefinitionAdditionalDamageBuilder
            .Create($"AdditionalDamage{Name}{ArcaneFirearm}")
            .SetGuiPresentationNoContent(true)
            .SetNotificationTag(ArcaneFirearm)
            .SetDamageDice(DieType.D8, 1)
            .SetAdvancement(AdditionalDamageAdvancement.ClassLevel, 1, 1, 10, 5)
            .SetTriggerCondition(AdditionalDamageTriggerCondition.SpellDamagesTarget)
            // required as in a feature set
            .AddCustomSubFeatures(ClassHolder.Inventor,
                new ValidateContextInsteadOfRestrictedProperty(IsArcaneFirearmContextValid))
            .AddToDB();

        var featureSetArcaneFirearm = FeatureDefinitionFeatureSetBuilder
            .Create(ARCANE_FIREARM)
            .SetGuiPresentation(Category.Feature)
            .AddFeatureSet(
                additionalDamageArcaneFirearm,
                MagicAffinitySpellBladeIntoTheFray)
            .AddToDB();
        _arcaneFirearmFeatureSet = featureSetArcaneFirearm;
        _arcaneFirearmDamage = additionalDamageArcaneFirearm;

        #endregion

        #region LEVEL 09

        // Eldritch Detonation

        const string ELDRITCH_DETONATION = $"Power{Name}{EldritchCannon}Detonate";

        var powerDetonateSelf = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{EldritchCannon}DetonateSelf")
            .SetGuiPresentation(ELDRITCH_DETONATION, Category.Feature, Fireball)
            .SetUsesFixed(ActivationTime.Action)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Sphere, 4)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.FixedValue)
                    .SetParticleEffectParameters(Fireball)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypeForce, 3, DieType.D8)
                            .Build())
                    .Build())
            .AddToDB();

        powerDetonateSelf.AddCustomSubFeatures(
            new ValidatorsValidatePowerUse(HasTinyCannon));

        var tinyDetonations = new[] { conditionFlamethrowerTiny, conditionForceBallistaTiny, conditionProtectorTiny }
            .Select(condition =>
            {
                var detonation = new DetonateTinyCannon(condition);
                var power = FeatureDefinitionPowerBuilder
                    .Create(powerDetonateSelf, powerDetonateSelf.Name + condition.Name.Replace($"Condition{Name}", ""))
                    .SetGuiPresentation(condition.GuiPresentation.Title, powerDetonateSelf.GuiPresentation.Description,
                        Fireball, true)
                    .AddCustomSubFeatures(detonation, new ValidatorsValidatePowerUse(detonation.HasCannon))
                    .AddToDB();
                power.AddCustomSubFeatures(new CannonPowerStatistics(power));
                return power;
            }).ToArray();
        var flexibleTinyDetonation = new DetonateTinyCannon();
        var powerDetonateTiny = FeatureDefinitionPowerBuilder
            .Create(powerDetonateSelf, $"Power{Name}{EldritchCannon}DetonateTiny")
            .SetGuiPresentation(powerDetonateSelf.GuiPresentation)
            .AddCustomSubFeatures(flexibleTinyDetonation,
                new ValidatorsValidatePowerUse(flexibleTinyDetonation.HasCannon))
            .AddToDB();
        powerDetonateTiny.guiPresentation.hidden = true;
        powerDetonateTiny.AddCustomSubFeatures(new CannonPowerStatistics(powerDetonateTiny));
        PowerBundle.RegisterPowerBundle(powerDetonateSelf, false, tinyDetonations.Append(powerDetonateTiny));
        CannonChoiceBundles.Add((powerDetonateSelf, tinyDetonations, [powerDetonateTiny]));

        var powerDetonateCannon = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{EldritchCannon}DetonateCannon")
            .SetGuiPresentation(ELDRITCH_DETONATION, Category.Feature, hidden: true)
            .SetUsesFixed(ActivationTime.Action)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Distance, 12, TargetType.Sphere, 4)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.FixedValue)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypeForce, 3, DieType.D8)
                            .Build())
                    .SetParticleEffectParameters(Fireball)
                    .Build())
            .AddToDB();

        powerDetonateCannon.AddCustomSubFeatures(
            new PowerOrSpellFinishedByMeEldritchDetonationDismiss(),
            new CannonPowerStatistics(powerDetonateCannon));

        var powerDetonate = FeatureDefinitionPowerBuilder
            .Create(ELDRITCH_DETONATION)
            .SetGuiPresentation(Category.Feature, Fireball)
            .SetUsesFixed(ActivationTime.Action)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Distance, 12, TargetType.IndividualsUnique)
                    .SetRestrictedCreatureFamilies(InventorClass.InventorConstructFamily)
                    .Build())
            .AddCustomSubFeatures(
                new ValidatorsValidatePowerUse(HasCannon),
                OwnCannonTargetFilter.Instance,
                new PowerOrSpellFinishedByMeEldritchDetonationDetonate(powerDetonateCannon))
            .AddToDB();

        // Explosive Cannon

        var powerExplosiveCannonPool = FeatureDefinitionPowerBuilder
            .Create(powerEldritchCannonPool, $"Power{Name}{ExplosiveCannon}Activate")
            .SetOverriddenPower(powerEldritchCannonPool)
            .AddToDB();
        _explosiveCannonPool = powerExplosiveCannonPool;

        var powerFlamethrower09 =
            BuildFlamethrowerPower(powerExplosiveCannonPool, conditionFlamethrower, 9,
                powerFlamethrower, powerDetonateCannon, actionAffinityFlamethrower);
        var powerForceBallista09 =
            BuildForceBallistaPower(powerExplosiveCannonPool, conditionForceBallista, 9,
                powerForceBallista, powerDetonateCannon, actionAffinityForceBallista);
        var powerProtector09 =
            BuildProtectorPower(powerExplosiveCannonPool, conditionProtector, 9,
                powerProtector, powerDetonateCannon, actionAffinityProtector);
        var powerTinyFlamethrower09 =
            BuildTinyFlamethrowerPower(powerExplosiveCannonPool, conditionFlamethrowerTiny, 9);
        var powerTinyForceBallista09 =
            BuildTinyForceBallistaPower(powerExplosiveCannonPool, conditionForceBallistaTiny, 9);
        var powerTinyProtector09 =
            BuildTinyProtectorPower(powerExplosiveCannonPool, conditionProtectorTiny, 9);

        var featureSetExplosiveCannon = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}{ExplosiveCannon}")
            .SetGuiPresentation(Category.Feature)
            .AddFeatureSet(
                powerExplosiveCannonPool,
                powerDetonate,
                powerDetonateSelf,
                powerFlamethrower09,
                powerForceBallista09,
                powerProtector09,
                powerTinyFlamethrower09,
                powerTinyForceBallista09,
                powerTinyProtector09)
            .AddFeatureSet(tinyDetonations.Append(powerDetonateTiny).ToArray())
            .AddToDB();
        BuildCannonCreationBundle(powerExplosiveCannonPool, featureSetExplosiveCannon, 9,
            powerFlamethrower09, powerForceBallista09, powerProtector09,
            powerTinyFlamethrower09, powerTinyForceBallista09, powerTinyProtector09);

        #endregion

        #region LEVEL 15

        // Aura of Half-Cover

        var combatAffinityFortifiedPosition = FeatureDefinitionCombatAffinityBuilder
            .Create($"CombatAffinity{Name}{FortifiedPosition}")
            .SetGuiPresentation(powerEldritchCannonPool.Name, Category.Feature, "UI/&HasHalfCover")
            .SetPermanentCover(CoverType.Half)
            .AddToDB();

        var conditionFortifiedPosition = ConditionDefinitionBuilder
            .Create($"Condition{Name}{FortifiedPosition}")
            .SetGuiPresentation(Category.Condition, DatabaseHelper.ConditionDefinitions.ConditionBlessed)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(combatAffinityFortifiedPosition)
            .AddToDB();

        var powerFortifiedPosition = FeatureDefinitionPowerBuilder
            .Create(PowerPaladinAuraOfProtection, $"Power{Name}{FortifiedPosition}Aura")
            .SetGuiPresentationNoContent(true)
            .AddToDB();

        // keep it simple and ensure it'll follow any changes from Aura of Protection
        powerFortifiedPosition.EffectDescription.EffectForms[0] = EffectFormBuilder
            .Create()
            .SetConditionForm(conditionFortifiedPosition, ConditionForm.ConditionOperation.Add)
            .Build();

        var powerFortifiedPositionTiny = FeatureDefinitionPowerBuilder
            .Create(powerFortifiedPosition, $"Power{Name}{FortifiedPosition}AuraTiny")
            .SetGuiPresentationNoContent(true)
            .AddToDB();

        foreach (var aura in new[] { powerFortifiedPosition, powerFortifiedPositionTiny })
        {
            aura.EffectDescription.EffectForms.Add(EffectFormBuilder
                .Create()
                .SetConditionForm(conditionFortifiedPosition, ConditionForm.ConditionOperation.Add, true, true)
                .Build());
        }

        var conditionFlamethrowerTiny15 = ConditionDefinitionBuilder
            .Create(conditionFlamethrowerTiny, $"Condition{Name}{Flamethrower}Tiny15")
            .SetParentCondition(conditionFlamethrowerTiny)
            .AllowMultipleInstances()
            .AddFeatures(powerFortifiedPositionTiny)
            .AddCustomSubFeatures(AddUsablePowersFromCondition.Marker)
            .AddToDB();

        var conditionForceBallistaTiny15 = ConditionDefinitionBuilder
            .Create(conditionForceBallistaTiny, $"Condition{Name}{ForceBallista}Tiny15")
            .SetParentCondition(conditionForceBallistaTiny)
            .AllowMultipleInstances()
            .AddFeatures(powerFortifiedPositionTiny)
            .AddCustomSubFeatures(AddUsablePowersFromCondition.Marker)
            .AddToDB();

        var conditionProtectorTiny15 = ConditionDefinitionBuilder
            .Create(conditionProtectorTiny, $"Condition{Name}{Protector}Tiny15")
            .SetParentCondition(conditionProtectorTiny)
            .AllowMultipleInstances()
            .AddFeatures(powerFortifiedPositionTiny)
            .AddCustomSubFeatures(AddUsablePowersFromCondition.Marker)
            .AddToDB();

        TinyCannonModes.Add(conditionFlamethrowerTiny15, actionAffinityFlamethrowerTiny);
        TinyCannonModes.Add(conditionForceBallistaTiny15, actionAffinityForceBallistaTiny);
        TinyCannonModes.Add(conditionProtectorTiny15, actionAffinityProtectorTiny);
        foreach (var condition in new[] { conditionFlamethrowerTiny15, conditionForceBallistaTiny15,
                     conditionProtectorTiny15 })
        {
            CannonPresentations.Add(condition, (condition.GuiPresentation.Title, condition.GuiPresentation.Description));
        }

        // Fortified Position

        var powerFortifiedPositionPool = FeatureDefinitionPowerBuilder
            .Create(powerEldritchCannonPool, $"Power{Name}{FortifiedPosition}Activate")
            .SetUsesFixed(ActivationTime.Action, RechargeRate.LongRest, 1, 2)
            .SetOverriddenPower(powerExplosiveCannonPool)
            .AddToDB();
        _fortifiedPositionPool = powerFortifiedPositionPool;

        var powerFlamethrower15 =
            BuildFlamethrowerPower(powerFortifiedPositionPool, conditionFlamethrower, 15,
                powerFlamethrower, actionAffinityFlamethrower, powerFortifiedPosition);
        var powerForceBallista15 =
            BuildForceBallistaPower(powerFortifiedPositionPool, conditionForceBallista, 15,
                powerForceBallista, actionAffinityForceBallista, powerFortifiedPosition);
        var powerProtector15 =
            BuildProtectorPower(powerFortifiedPositionPool, conditionProtector, 15,
                powerProtector, actionAffinityProtector, powerFortifiedPosition);
        var powerTinyFlamethrower15 =
            BuildTinyFlamethrowerPower(powerFortifiedPositionPool, conditionFlamethrowerTiny15, 15);
        var powerTinyForceBallista15 =
            BuildTinyForceBallistaPower(powerFortifiedPositionPool, conditionForceBallistaTiny15, 15);
        var powerTinyProtector15 =
            BuildTinyProtectorPower(powerFortifiedPositionPool, conditionProtectorTiny15, 15);

        var featureSetFortifiedPosition = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}{FortifiedPosition}")
            .SetGuiPresentation(Category.Feature)
            .AddFeatureSet(
                powerFortifiedPositionPool,
                powerFlamethrower15,
                powerForceBallista15,
                powerProtector15,
                powerTinyFlamethrower15,
                powerTinyForceBallista15,
                powerTinyProtector15)
            .AddToDB();
        _fortifiedPositionFeatureSet = featureSetFortifiedPosition;
        BuildCannonCreationBundle(powerFortifiedPositionPool, featureSetFortifiedPosition, 15,
            powerFlamethrower15, powerForceBallista15, powerProtector15,
            powerTinyFlamethrower15, powerTinyForceBallista15, powerTinyProtector15);
        var fortifiedChoices = CannonChoiceBundles.Last();
        _fortifiedSummons = fortifiedChoices.Fixed.Concat(fortifiedChoices.Flexible).ToArray();
        BuildSecondCannonCreation(featureSetFortifiedPosition);
        BuildTinyCannonCommand(powerEldritchCannonCommand,
            [powerFlamethrower, powerForceBallista, powerProtector]);

        #endregion

        #region MAIN

        //
        // MAIN
        //

        Subclass = CharacterSubclassDefinitionBuilder
            .Create(Name)
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite(Name, Resources.InventorArtillerist, 256))
            .AddFeaturesAtLevel(3, autoPreparedSpells, featureSetEldritchCannon)
            .AddFeaturesAtLevel(5, featureSetArcaneFirearm)
            .AddFeaturesAtLevel(9, featureSetExplosiveCannon)
            .AddFeaturesAtLevel(15, featureSetFortifiedPosition)
            .AddToDB();

        #endregion
    }

    internal override CharacterSubclassDefinition Subclass { get; }
    internal override CharacterClassDefinition Klass => InventorClass.Class;
    internal override FeatureDefinitionSubclassChoice SubclassChoice => InventorClass.SubclassChoice;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    private static (OperationType, bool) IsArcaneFirearmContextValid(
        BaseDefinition definition,
        IRestrictedContextProvider provider,
        RulesetCharacter character,
        ItemDefinition itemDefinition,
        bool rangedAttack,
        RulesetAttackMode attackMode,
        RulesetEffect rulesetEffect)
    {
        if (character == null || rulesetEffect is not RulesetEffectSpell { OriginItem: null } spell)
        {
            return (OperationType.Set, false);
        }

        var repertoire = SpellSelectionContext.Resolve(spell.SpellRepertoire);
        if (repertoire?.SpellCastingClass != InventorClass.Class ||
            !character.SpellRepertoires.Contains(repertoire) || spell.Caster != character ||
            !character.FeaturesByType<ISpellCastingAffinityProvider>()
                .Any(affinity => affinity.CanUseProficientWeaponAsFocus))
        {
            return (OperationType.Set, false);
        }

        bool IsWeaponFocus(string slotName) =>
            character.CharacterInventory?.InventorySlotsByName.TryGetValue(slotName, out var slot) == true &&
            !slot.Disabled && !slot.ConfigSlot && slot.EquipedItem?.ItemDefinition is { IsWeapon: true } weapon &&
            character.IsProficientWithItem(weapon);

        return (OperationType.Set,
            IsWeaponFocus(EquipmentDefinitions.SlotTypeMainHand) ||
            IsWeaponFocus(EquipmentDefinitions.SlotTypeOffHand));
    }

    internal static void SwitchEldritchCannon2024()
    {
        if (_eldritchCannonFeatureSet == null)
        {
            return;
        }

        var enabled = Main.Settings.EnableArtilleristEldritchCannon2024;
        _eldritchCannonFeatureSet.GuiPresentation.description = enabled
            ? $"Feature/&FeatureSet{Name}{EldritchCannon}2024Description"
            : $"Feature/&FeatureSet{Name}{EldritchCannon}Description";
        foreach (var (pool, fixedPowers, flexiblePowers) in CannonChoiceBundles)
        {
            var selected = enabled ? flexiblePowers : fixedPowers;
            var bundle = pool.GetBundle();
            bundle.SubPowers.Clear();
            bundle.SubPowers.AddRange(selected);
            var spell = PowerBundle.GetSpell(pool);
            spell.SubspellsList.Clear();
            spell.SubspellsList.AddRange(selected.Select(PowerBundle.GetSpell));
        }
        foreach (var pair in CannonModes)
        {
            SetCannonModeFeatures(pair.Key.Features, pair.Value, _cannonModeFeatures, enabled);
        }
        foreach (var pair in TinyCannonModes)
        {
            SetCannonModeFeatures(pair.Key.Features, [pair.Value], _tinyCannonModeFeatures, enabled);
        }
        foreach (var pair in CannonPresentations)
        {
            var definition = pair.Key;
            var presentation = pair.Value;
            definition.GuiPresentation.title = enabled
                ? $"Feature/&FeatureSet{Name}{EldritchCannon}Title"
                : presentation.Title;
            definition.GuiPresentation.description = enabled && definition is ConditionDefinition
                ? $"Condition/&Condition{Name}{EldritchCannon}Description"
                : presentation.Description;
        }
        foreach (var character in Global.GetLoadedCharacters())
        {
            SynchronizeCannonModes(character, true);
        }
    }

    private static void SetCannonModeFeatures(List<FeatureDefinition> features,
        FeatureDefinition[] original, FeatureDefinition[] all, bool enabled)
    {
        features.RemoveAll(all.Contains);
        features.AddRange(enabled ? all : original);
    }

    private static void SynchronizeCannonModes(RulesetCharacter character, bool refresh = false)
    {
        if (character is RulesetCharacterMonster monster &&
            CannonModes.TryGetValue(monster.MonsterDefinition, out var original))
        {
            var selected = Main.Settings.EnableArtilleristEldritchCannon2024 ? _cannonModeFeatures : original;
            refresh |= !monster.activeFeatures.Where(_cannonModeFeatures.Contains).ToHashSet().SetEquals(selected);
            SetCannonModeFeatures(monster.activeFeatures, original, _cannonModeFeatures,
                Main.Settings.EnableArtilleristEldritchCannon2024);
            if (!monster.activeFeatures.Contains(ActionAffinityUncommandedCannon))
            {
                monster.activeFeatures.Add(ActionAffinityUncommandedCannon);
                refresh = true;
            }
            // Rebuild only this cannon's at-will activation powers. GrantPowers would
            // also recharge unrelated resources on an already loaded character.
            var active = monster.FeaturesByType<FeatureDefinitionPower>().ToHashSet();
            refresh |= monster.UsablePowers.RemoveAll(usable => _cannonModeFeatures.Contains(usable.PowerDefinition) &&
                !active.Contains(usable.PowerDefinition)) > 0;
            refresh |= PowerProvider.EnsureGrantedPowers(monster) > 0;
            if (refresh)
            {
                monster.RefreshAll();
            }
        }
        else if (character.HasAnyFeature(_eldritchCannonPool, _explosiveCannonPool, _fortifiedPositionPool))
        {
            if (PowerProvider.EnsureGrantedPowers(character) > 0 || refresh)
            {
                character.RefreshAll();
            }
        }
    }

    private sealed class RestoreCannonModes : IOnCharacterPostLoad
    {
        public void OnCharacterPostLoad(RulesetCharacter character) => SynchronizeCannonModes(character);
    }

    internal static void SwitchFortifiedPosition2024()
    {
        if (_fortifiedPositionPool == null)
        {
            return;
        }

        var enabled = Main.Settings.EnableArtilleristFortifiedPosition2024;
        foreach (var form in CannonDamageForms)
        {
            foreach (var advancement in form.DiceByLevelTable.Where(x => x.rank >= 15))
            {
                advancement.diceNumber = enabled ? 1 : 2;
            }
        }

        _fortifiedPositionPool.fixedUsesPerRecharge = enabled ? 1 : 2;
        _arcaneFirearmDamage.damageAdvancement = enabled
            ? AdditionalDamageAdvancement.None
            : AdditionalDamageAdvancement.ClassLevel;
        _arcaneFirearmFeatureSet.GuiPresentation.description = enabled
            ? $"Feature/&FeatureSet{Name}{ArcaneFirearm}2024Description"
            : $"Feature/&FeatureSet{Name}{ArcaneFirearm}Description";
        _fortifiedPositionFeatureSet.GuiPresentation.description = enabled
            ? $"Feature/&FeatureSet{Name}{FortifiedPosition}2024Description"
            : $"Feature/&FeatureSet{Name}{FortifiedPosition}Description";
        foreach (var character in Global.GetLoadedCharacters())
        {
            SynchronizeCannonPool(character);

            if (!enabled)
            {
                // Creation rules do not revoke a shared command already issued.
                RemoveConditions(character, SecondCannon);
            }
        }
    }

    private static void SynchronizeCannonPool(RulesetCharacter character)
    {
        character.GetAllConditions(character.AllConditionsForEnumeration);
        var debt = character.AllConditionsForEnumeration.FirstOrDefault(condition =>
            condition.ConditionDefinition == CannonCreationDebt)?.Amount ?? 0;
        var paidCreation = character.HasConditionOfType(SpellSlotCreation.Name) ? 1 : 0;
        foreach (var usable in character.UsablePowers.Where(x => x.PowerDefinition == _fortifiedPositionPool))
        {
            // A prepaid spell-slot creation is available separately from the free allowance.
            var used = Math.Max(0, usable.MaxUses - usable.RemainingUses + debt + paidCreation);
            usable.maxUses = _fortifiedPositionPool.FixedUsesPerRecharge;
            usable.remainingUses = Math.Max(0, usable.maxUses - used) + paidCreation;
            var newDebt = Math.Max(0, used - usable.maxUses);
            if (newDebt == debt)
            {
                continue;
            }

            RemoveConditions(character, CannonCreationDebt.Name);
            if (newDebt > 0)
            {
                character.InflictCondition(CannonCreationDebt.Name, DurationType.UntilLongRest, 0,
                    TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect, character.Guid,
                    character.CurrentFaction.Name, 0, CannonCreationDebt.Name, newDebt, 0, 0);
            }
        }
    }

    private static bool UsesFortifiedPosition2024(RulesetCharacter character) =>
        Main.Settings.EnableArtilleristFortifiedPosition2024 && character.GetClassLevel(InventorClass.Class) >= 15;

    private static IEnumerable<RulesetEffectPower> GetCannons(RulesetCharacter character) =>
        character.PowersUsedByMe.Where(effect => !effect.Terminated &&
            effect.PowerDefinition.GetFirstSubFeatureOfType<ILimitEffectInstances>()?.Name == CreatureTag);

    private static void RemoveConditions(RulesetCharacter character, string name)
    {
        character.GetAllConditions(character.AllConditionsForEnumeration);
        foreach (var condition in character.AllConditionsForEnumeration
                     .Where(condition => condition.ConditionDefinition.Name == name).ToArray())
        {
            character.RemoveCondition(condition);
        }
    }

    private static void SynchronizeTinyCannonTracking(RulesetCharacter character)
    {
        var cannons = GetTinyCannons(character).ToArray();
        character.GetAllConditions(character.AllConditionsForEnumeration);
        var markers = character.AllConditionsForEnumeration
            .Where(condition => condition.ConditionDefinition == _tinyCannonActivated).ToArray();
        var markerGuids = markers.Select(condition => condition.Guid).ToHashSet();
        foreach (var cannon in cannons)
        {
            // Native effect loading subscribes once for every tracked condition.
            // This actor needs one callback per cannon, including when its saved
            // activation marker and ownership condition are both restored.
            foreach (var duplicate in (character.ConditionRemoved?.GetInvocationList() ?? [])
                         .OfType<RulesetActor.ConditionRemovedHandler>()
                         .Where(handler => handler.Target == cannon).Skip(1))
            {
                character.ConditionRemoved -= duplicate;
            }
            foreach (var duplicate in (character.ConditionOccurenceReached?.GetInvocationList() ?? [])
                         .OfType<RulesetActor.ConditionOccurenceReachedHandler>()
                         .Where(handler => handler.Target == cannon).Skip(1))
            {
                character.ConditionOccurenceReached -= duplicate;
            }
            foreach (var duplicate in (character.ConditionSaveRerollRequested?.GetInvocationList() ?? [])
                         .OfType<RulesetActor.ConditionSaveRerollRequestedHandler>()
                         .Where(handler => handler.Target == cannon).Skip(1))
            {
                character.ConditionSaveRerollRequested -= duplicate;
            }
        }
        foreach (var marker in markers.Where(marker =>
                     !cannons.Any(cannon => cannon.TrackedConditionGuids.Contains(marker.Guid))))
        {
            var cannon = cannons.FirstOrDefault(candidate => GetTinyCannonType(candidate) == marker.Amount &&
                !candidate.TrackedConditionGuids.Any(markerGuids.Contains));
            cannon?.TrackedConditionGuids.Add(marker.Guid);
        }
    }

    private static void BuildSecondCannonCreation(FeatureDefinitionFeatureSet featureSet)
    {
        var continuation = ConditionDefinitionBuilder.Create(SecondCannon)
            .SetGuiPresentation(Category.Condition, Sprites.GetConditionSprite("ConditionEldritchCannonCommanded"))
            .SetConditionType(ConditionType.Beneficial)
            .SetAmountOrigin(ConditionDefinition.OriginOfAmount.Fixed)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();
        CannonTurnConditions.Add(continuation);
        var behavior = new CreateSecondCannon(continuation);
        continuation.AddCustomSubFeatures(behavior);
        featureSet.AddCustomSubFeatures(behavior);

        var pool = FeatureDefinitionPowerBuilder.Create($"Power{Name}CreateSecondCannon")
            .SetGuiPresentation(Category.Feature, _fortifiedPositionPool.GuiPresentation.SpriteReference)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetEffectDescription(EffectDescriptionBuilder.Create(_fortifiedPositionPool.EffectDescription).Build())
            .AddCustomSubFeatures(behavior)
            .AddToDB();
        featureSet.FeatureSet.Add(pool);

        var secondPowers = new List<FeatureDefinitionPower>();
        foreach (var original in SummonPowers)
        {
            original.AddCustomSubFeatures(behavior);
        }
        foreach (var original in _fortifiedSummons)
        {
            var second = FeatureDefinitionPowerBuilder.Create(original.Name + "Second")
                .SetGuiPresentation(original.GuiPresentation)
                .SetUsesFixed(ActivationTime.NoCost)
                .SetEffectDescription(EffectDescriptionBuilder.Create(original.EffectDescription).Build())
                .AddCustomSubFeatures(SkipEffectRemovalOnLocationChange.Always, CannonLimiter, behavior)
                .AddToDB();
            second.guiPresentation.hidden = true;
            secondPowers.Add(second);
            featureSet.FeatureSet.Add(second);
        }
        PowerBundle.RegisterPowerBundle(pool, false, secondPowers);
        var choices = CannonChoiceBundles.First(bundle => bundle.Pool == _fortifiedPositionPool);
        CannonChoiceBundles.Add((pool, secondPowers.Take(choices.Fixed.Length).ToArray(),
            secondPowers.Skip(choices.Fixed.Length).ToArray()));
    }

    private static void BuildCannonCreationBundle(FeatureDefinitionPower pool,
        FeatureDefinitionFeatureSet featureSet, int level, params FeatureDefinitionPower[] fixedPowers)
    {
        // Size is a creation choice. Activation mode remains a choice of the
        // physical cannon, including cannons created before this option changed.
        var flexible = new[] { (Size: "Small", Original: fixedPowers[0]),
            (Size: "Tiny", Original: fixedPowers[3]) }.Select(choice =>
        {
            var power = FeatureDefinitionPowerSharedPoolBuilder
                .Create(PowerSummonCannon + choice.Size + level)
                .SetGuiPresentation(PowerSummonCannon + choice.Size, Category.Feature,
                    pool.GuiPresentation.SpriteReference, hidden: true)
                .SetSharedPool(ActivationTime.Action, pool)
                .SetEffectDescription(EffectDescriptionBuilder.Create(choice.Original.EffectDescription).Build())
                .SetUniqueInstance()
                .AddCustomSubFeatures(SkipEffectRemovalOnLocationChange.Always, CannonLimiter)
                .AddToDB();
            SummonPowers.Add(power);
            featureSet.FeatureSet.Add(power);
            return (FeatureDefinitionPower)power;
        }).ToArray();
        PowerBundle.RegisterPowerBundle(pool, true, fixedPowers.Concat(flexible));
        CannonChoiceBundles.Add((pool, fixedPowers, flexible));
    }

    private static void BuildTinyCannonCommand(FeatureDefinitionPower command,
        FeatureDefinitionPower[] powers)
    {
        var activated = ConditionDefinitionBuilder.Create($"Condition{Name}TinyCannonActivated")
            .SetGuiPresentationNoContent(true)
            .SetSpecialDuration(DurationType.Round, 1, TurnOccurenceType.StartOfTurn)
            .SetAmountOrigin(ConditionDefinition.OriginOfAmount.Fixed)
            .SetSilent(Silent.Always)
            .AllowMultipleInstances()
            .AddToDB();
        _tinyCannonActivated = activated;
        var additionalActions = Enumerable.Range(1, 2).Select(index => FeatureDefinitionAdditionalActionBuilder
            .Create($"AdditionalAction{Name}TinyCannon{index}")
            .SetGuiPresentationNoContent(true)
            .SetActionType(ActionType.Bonus)
            .SetRestrictedActions((Id)ExtraActionId.CannonFlamethrowerBonus,
                (Id)ExtraActionId.CannonForceBallistaBonus, (Id)ExtraActionId.CannonProtectorBonus)
            .AddToDB()).ToArray();
        var commands = Enumerable.Range(1, 2).Select(count => ConditionDefinitionBuilder
            .Create($"Condition{Name}TinyCannonActions{count}")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.Always)
            .SetFeatures(additionalActions.Take(count))
            .AddToDB()).ToArray();
        CannonTurnConditions.AddRange(commands);
        command.AddCustomSubFeatures(new CommandTinyCannons(commands));
        for (var index = 0; index < powers.Length; index++)
        {
            var behavior = new ActivateTinyCannon(index + 1, activated);
            powers[index].AddCustomSubFeatures(behavior);
        }
    }

    private static int GetTinyCannonType(RulesetEffectPower cannon)
    {
        foreach (var guid in cannon.TrackedConditionGuids)
        {
            if (RulesetEntity.TryGetEntity<RulesetCondition>(guid, out var condition) &&
                condition.TargetGuid == cannon.User?.Guid)
            {
                var type = Array.FindIndex(CannonTypes, name =>
                    condition.ConditionDefinition.IsSubtypeOf($"Condition{Name}{name}Tiny")) + 1;
                if (type > 0)
                {
                    return type;
                }
            }
        }
        return 0;
    }

    private static IEnumerable<RulesetEffectPower> GetTinyCannons(RulesetCharacter character, int type = 0) =>
        GetCannons(character).Where(cannon => GetTinyCannonType(cannon) is var cannonType &&
            cannonType > 0 && (type == 0 || cannonType == type));

    private static bool HasCannon(RulesetCharacter character) =>
        GetCannons(character).SelectMany(EffectHelpers.GetSummonedCreatures).Any(cannon =>
            cannon is RulesetCharacterMonster monster && monster.MonsterDefinition.CreatureTags.Contains(CreatureTag));

    private static bool HasTinyCannon(RulesetCharacter character) => GetTinyCannons(character).Any();

    private static FeatureDefinitionActionAffinity BuildCannonActionAffinity(
        string actionName,
        ExtraActionId actionId)
    {
        return FeatureDefinitionActionAffinityBuilder
            .Create($"ActionAffinity{Name}{actionName}")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((Id)actionId)
            .AddToDB();
    }

    private static void BuildCannonAction(
        string actionName,
        string presentationName,
        ExtraActionId actionId,
        ActionType actionType,
        FeatureDefinitionPower activatedPower)
    {
        _ = ActionDefinitionBuilder
            .Create($"Action{Name}{actionName}")
            .SetGuiPresentation($"Power{Name}{presentationName}", Category.Feature,
                Sprites.GetActionSprite($"Action{EldritchCannon}{presentationName}"))
            .OverrideClassName("UsePower")
            .SetStealthBreakerBehavior(StealthBreakerBehavior.RollIfTargets)
            .RequiresAuthorization()
            .SetActionId(actionId)
            .SetActionType(actionType)
            .SetActivatedPower(activatedPower)
            .SetFormType(ActionFormType.Large)
            .AddToDB();
    }

    private static FeatureDefinitionPowerSharedPool BuildFlamethrowerPower(
        FeatureDefinitionPower sharedPoolPower,
        ConditionDefinition conditionFlamethrower,
        int level,
        params FeatureDefinition[] monsterAdditionalFeatures)
    {
        return BuildEldritchCannonPower(
            Flamethrower, sharedPoolPower, Fire_Spider, level, conditionFlamethrower, monsterAdditionalFeatures);
    }

    private static FeatureDefinitionPowerSharedPool BuildForceBallistaPower(
        FeatureDefinitionPower sharedPoolPower,
        ConditionDefinition conditionForceBallista,
        int level,
        params FeatureDefinition[] monsterAdditionalFeatures)
    {
        return BuildEldritchCannonPower(
            ForceBallista, sharedPoolPower, PhaseSpider, level, conditionForceBallista, monsterAdditionalFeatures);
    }

    private static FeatureDefinitionPowerSharedPool BuildProtectorPower(
        FeatureDefinitionPower sharedPoolPower,
        ConditionDefinition conditionProtector,
        int level,
        params FeatureDefinition[] monsterAdditionalFeatures)
    {
        return BuildEldritchCannonPower(
            Protector, sharedPoolPower, SpectralSpider, level, conditionProtector, monsterAdditionalFeatures);
    }

    private static FeatureDefinitionPowerSharedPool BuildEldritchCannonPower(
        string powerName,
        FeatureDefinitionPower sharedPoolPower,
        MonsterDefinition monsterDefinition,
        int level,
        ConditionDefinition conditionDefinition,
        params FeatureDefinition[] monsterAdditionalFeatures)
    {
        var name = PowerSummonCannon + powerName;
        var monster = BuildEldritchCannonMonster(powerName, monsterDefinition, level, monsterAdditionalFeatures);

        var power = FeatureDefinitionPowerSharedPoolBuilder
            .Create(name + level)
            .SetGuiPresentation($"Power{Name}{powerName}", Category.Feature, hidden: true)
            .SetSharedPool(ActivationTime.Action, sharedPoolPower)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Hour, 1)
                    .SetTargetingData(Side.Ally, RangeType.Distance, 1, TargetType.Position)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetSummonCreatureForm(1, monster.Name)
                            .Build(),
                        EffectFormBuilder.ConditionForm(
                            conditionDefinition,
                            ConditionForm.ConditionOperation.Add, true, true))
                    .SetParticleEffectParameters(ConjureGoblinoids)
                    .Build())
            .SetUniqueInstance()
            .AddCustomSubFeatures(SkipEffectRemovalOnLocationChange.Always, CannonLimiter)
            .AddToDB();

        SummonPowers.Add(power);
        return power;
    }

    private static MonsterDefinition BuildEldritchCannonMonster(
        string cannonName,
        MonsterDefinition monsterDefinition,
        int level,
        params FeatureDefinition[] monsterAdditionalFeatures)
    {
        var monsterName = $"{Name}{cannonName}{level}";
        var presentationName = $"Power{Name}{cannonName}";

        var monster = MonsterDefinitionBuilder
            .Create(monsterDefinition, monsterName)
            .SetOrUpdateGuiPresentation(presentationName, Category.Feature)
            .SetSizeDefinition(DatabaseHelper.CharacterSizeDefinitions.Small)
            .SetMonsterPresentation(
                MonsterPresentationBuilder.Create()
                    .SetPrefab(monsterDefinition.MonsterPresentation.malePrefabReference)
                    .SetModelScale(0.1f)
                    .SetHasMonsterPortraitBackground(true)
                    .SetCanGeneratePortrait(true)
                    .Build())
            .SetStandardHitPoints(1)
            .SetHeight(2)
            .NoExperienceGain()
            .SetArmorClass(18)
            .SetChallengeRating(0)
            .SetHitDice(DieType.D8, 1)
            .SetAbilityScores(10, 10, 10, 10, 10, 10)
            .SetDefaultFaction(DatabaseHelper.FactionDefinitions.Party)
            .SetCharacterFamily(InventorClass.InventorConstructFamily)
            .SetCreatureTags(CreatureTag)
            .SetBestiaryEntry(BestiaryDefinitions.BestiaryEntry.None)
            .SetFullyControlledWhenAllied(true)
            .SetDungeonMakerPresence(MonsterDefinition.DungeonMaker.None)
            .ClearAttackIterations()
            .SetFeatures(
                ActionAffinityEldritchCannon,
                ActionAffinityUncommandedCannon,
                ConditionAffinityEldritchCannon,
                MoveModeEldritchCannon,
                ConditionAffinityPoisonImmunity,
                DamageAffinityPoisonImmunity,
                DamageAffinityPsychicImmunity,
                SenseNormalVision,
                MovementAffinityNoSpecialMoves,
                MovementAffinitySpiderClimb)
            .AddFeatures(monsterAdditionalFeatures)
            .AddCustomSubFeatures(new CustomModels.MonsterModel(EldritchCannon, CannonModelScale, ConfigureCannonGraphics,
                graphics => graphics.GetComponentInChildren<CannonPresentation>()?.EffectOrigin,
                graphics => graphics.GetComponentInChildren<CannonPresentation>()?.Bones))
            .AddToDB();

        monster.guiPresentation.description = Gui.NoLocalization;
        CannonModes.Add(monster, monsterAdditionalFeatures.Where(_cannonModeFeatures.Contains).ToArray());
        CannonPresentations.Add(monster, (monster.GuiPresentation.Title, monster.GuiPresentation.Description));

        return monster;
    }

    private static void ConfigureCannonGraphics(GraphicsCharacterMonster graphics, Transform body)
    {
        var presentation = body.GetComponent<CannonPresentation>() ?? body.gameObject.AddComponent<CannonPresentation>();
        presentation.Bind(graphics);
        presentation.RefreshPose();
    }

    [DefaultExecutionOrder(1000)]
    private sealed class CannonPresentation : MonoBehaviour
    {
        private sealed class Leg(Transform foot, Vector3[] restPoints, Transform[] parts, Vector3 mount)
        {
            internal Transform Foot { get; } = foot;
            internal Vector3[] RestPoints { get; } = restPoints;
            internal Transform[] Parts { get; } = parts;
            internal Vector3 Mount { get; } = mount;
            internal float[] Lengths { get; } = Enumerable.Range(0, 3)
                .Select(index => Vector3.Distance(restPoints[index], restPoints[index + 1])).ToArray();
            internal Vector3[] Joints { get; } = new Vector3[4];
            internal Vector3 BracedFoot { get; set; } = restPoints[3];
        }

        private readonly List<Leg> _legs = [];
        private readonly HashSet<Material> _ownedMaterials = [];
        private readonly List<Texture2D> _ownedTextures = [];
        private readonly Dictionary<AnimationDefinitions.BoneType, (Transform Transform, Vector3 LocalPosition)> _bones = [];
        private GraphicsCharacterMonster _graphics;
        private Transform _barrel;
        private Transform _muzzle;
        private Vector3 _barrelOrigin;
        private Vector3 _aimVector;
        private bool _aimAlongDirection;
        private bool _aiming;
        private bool _shot;
        private float _recoilStarted = float.NegativeInfinity;
        private float _movementBlend;
        private float _idleTime;
        private bool _moving;
        private AnimatorCullingMode _originalCullingMode;
        private MeshRenderer[] _renderers;

        internal int RecoilCount { get; private set; }
        internal Transform Muzzle => _muzzle;
        internal Transform EffectOrigin => _aiming && _shot ? _muzzle : null;
        internal IReadOnlyDictionary<AnimationDefinitions.BoneType, (Transform Transform, Vector3 LocalPosition)> Bones => _bones;

        internal void Bind(GraphicsCharacterMonster graphics)
        {
            if (_graphics)
            {
                return;
            }
            _graphics = graphics;
            var bones = graphics.GetComponentsInChildren<Transform>(true);
            var hub = bones.Single(bone => bone.name == "Spider_b_Hub");
            // A graphics instance may bind during any idle or movement frame. Recover
            // the neutral rig from the skin instead of retaining that transient pose.
            var skin = graphics.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .First(renderer => renderer.bones.Contains(hub));
            var skinBones = skin.bones;
            var bindPoses = skin.sharedMesh.bindposes;
            var skinToGraphics = graphics.transform.worldToLocalMatrix * skin.transform.localToWorldMatrix;
            Matrix4x4 RestPose(Transform bone) =>
                skinToGraphics * bindPoses[Array.IndexOf(skinBones, bone)].inverse;
            var restHub = RestPose(hub).MultiplyPoint3x4(Vector3.zero);
            _barrel = CreatePart("EldritchCannonBarrel", "Barrel");
            _barrelOrigin = new Vector3(0, 0.525f, -0.035f);
            _barrel.localPosition = _barrelOrigin;
            _muzzle = new GameObject("Muzzle").transform;
            _muzzle.SetParent(_barrel, false);
            _muzzle.localPosition = new Vector3(0, 0, 0.55f);
            _bones.Add(AnimationDefinitions.BoneType.Prop1,
                (_muzzle, _barrelOrigin + _muzzle.localPosition + Vector3.up * (CannonBodyLift / CannonModelScale)));

            foreach (var side in new[] { "L", "R" })
            foreach (var number in new[] { 1, 4 })
            {
                var chain = Enumerable.Range(1, 4).Select(joint => bones.Single(bone =>
                    bone.name == $"Spider_b_{side}Leg{number}{joint}")).ToArray();
                var parts = new[]
                {
                    CreatePart("EldritchCannonLegUpper", $"{side}{number}Upper"),
                    CreatePart("EldritchCannonLegLower", $"{side}{number}Lower"),
                    CreatePart("EldritchCannonFoot", $"{side}{number}Foot")
                };
                var restPositions = chain.Select(bone => RestPose(bone).MultiplyPoint3x4(Vector3.zero)).ToArray();
                var mount = restPositions[0] - restHub;
                mount.y = 0;
                mount = mount.normalized * 0.24f;
                mount.y = 0.13f;
                var restPoints = restPositions.Select(NativePointOffset).ToArray();
                restPoints[0] = mount * CannonModelScale + Vector3.up * CannonBodyLift;
                var outward = new Vector3(restPoints[1].x, 0, restPoints[1].z).normalized;
                restPoints[1] += outward * 0.065f;
                _legs.Add(new Leg(chain[3], restPoints, parts, mount));
            }

            ConfigureMaterials();
            _renderers = GetComponentsInChildren<MeshRenderer>(true);
            _originalCullingMode = graphics.Animator.cullingMode;
            graphics.GraphicsCharacterEvent += OnAnimationEvent;
        }

        private Transform CreatePart(string model, string name)
        {
            var prefab = CustomModels.GetPrefabByGuid(CustomModels.GetBlenderModel(model).AssetGUID);
            var part = Instantiate(prefab, transform, false).transform;
            part.name = name;
            return part;
        }

        private void ConfigureMaterials()
        {
            var native = _graphics.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(renderer => renderer.sharedMaterials).First(material => material);
            var copies = new Dictionary<Material, Material>();
            foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.sharedMaterials = renderer.sharedMaterials.Select(source =>
                {
                    if (copies.TryGetValue(source, out var existing))
                    {
                        return existing;
                    }
                    var material = new Material(source) { shader = native.shader, shaderKeywords = native.shaderKeywords };
                    material.SetColor("_BaseColor", Color.white);
                    material.SetFloat("_ColorReplace", 0f);
                    material.EnableKeyword("_COLORREPLACE_OFF");
                    material.SetFloat("_MetallicFactor", 1f);
                    material.SetFloat("_GlossinessFactor", 1f);
                    material.SetFloat("_BumpMapScale", 1f);
                    material.SetFloat("_NewMaskPackingMethod", 0f);
                    if (source.GetTexture("_BumpMap"))
                    {
                        material.EnableKeyword("_NORMALMAP");
                    }
                    else
                    {
                        material.DisableKeyword("_NORMALMAP");
                    }
                    var metal = source.name.Contains("Iron") || source.name.Contains("Brass");
                    var mask = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
                    {
                        name = source.name + " Metallic Smoothness"
                    };
                    mask.SetPixel(0, 0, new Color(metal ? 0.85f : 0f, 0f, 0f, metal ? 0.32f : 0.12f));
                    mask.Apply();
                    _ownedTextures.Add(mask);
                    if (material.HasProperty("_MetallicGlossMap"))
                    {
                        material.SetTexture("_MetallicGlossMap", mask);
                        material.EnableKeyword("_METALLICGLOSSMAP");
                    }
                    if (source.name.Contains("Brass"))
                    {
                        material.SetColor("_BaseColor", new Color(0.85f, 0.65f, 0.3f));
                    }
                    else if (source.name.Contains("RedCloth"))
                    {
                        material.SetColor("_BaseColor", new Color(0.65f, 0.16f, 0.12f));
                    }
                    copies.Add(source, material);
                    _ownedMaterials.Add(material);
                    return material;
                }).ToArray();
            }
        }

        private Vector3 NativePointOffset(Vector3 point)
        {
            point *= _graphics.RulesetCharacterMonster.GetModelScale();
            point.x *= 1.3f;
            point.z *= 1.3f;
            return point;
        }

        private void PoseLeg(Leg leg)
        {
            var rotation = _graphics.transform.rotation;
            var origin = _graphics.transform.position;
            var footOffset = _aiming ? leg.BracedFoot : leg.RestPoints[3];
            if (!_aiming && _movementBlend > 0f)
            {
                var nativeFoot = NativePointOffset(_graphics.transform.InverseTransformPoint(leg.Foot.position));
                footOffset = Vector3.Lerp(footOffset, nativeFoot, _movementBlend);
            }
            var foot = origin + rotation * footOffset;
            var footDirection = rotation * (leg.RestPoints[3] - leg.RestPoints[2]).normalized;
            var root = transform.TransformPoint(leg.Mount);
            var ankleDirection = foot - footDirection * leg.Lengths[2] - root;
            if (ankleDirection.sqrMagnitude < 0.000001f)
            {
                ankleDirection = rotation * (leg.RestPoints[2] - leg.RestPoints[0]);
            }
            var distance = Mathf.Clamp(ankleDirection.magnitude,
                Mathf.Abs(leg.Lengths[0] - leg.Lengths[1]) + 0.00001f,
                leg.Lengths[0] + leg.Lengths[1] - 0.00001f);
            var axis = ankleDirection.normalized;
            var pole = Vector3.ProjectOnPlane(rotation * (leg.RestPoints[1] - leg.RestPoints[0]), axis);
            if (pole.sqrMagnitude < 0.000001f)
            {
                pole = Vector3.Cross(axis, _graphics.transform.forward);
            }
            if (pole.sqrMagnitude < 0.000001f)
            {
                pole = Vector3.Cross(axis, _graphics.transform.right);
            }
            var along = (leg.Lengths[0] * leg.Lengths[0] - leg.Lengths[1] * leg.Lengths[1] +
                         distance * distance) / (2f * distance);
            var height = Mathf.Sqrt(Mathf.Max(0f, leg.Lengths[0] * leg.Lengths[0] - along * along));
            leg.Joints[0] = root;
            leg.Joints[1] = root + axis * along + pole.normalized * height;
            leg.Joints[2] = root + axis * distance;
            leg.Joints[3] = leg.Joints[2] + footDirection * leg.Lengths[2];
            for (var index = 0; index < leg.Parts.Length; index++)
            {
                var start = leg.Joints[index];
                var direction = leg.Joints[index + 1] - start;
                var outward = Vector3.ProjectOnPlane((start + leg.Joints[index + 1]) * 0.5f - transform.position,
                    direction);
                if (outward.sqrMagnitude < 0.000001f)
                {
                    outward = Vector3.ProjectOnPlane(_graphics.transform.forward, direction);
                }
                if (outward.sqrMagnitude < 0.000001f)
                {
                    outward = Vector3.ProjectOnPlane(_graphics.transform.right, direction);
                }
                var part = leg.Parts[index];
                part.position = start;
                part.rotation = Quaternion.LookRotation(outward.normalized, direction.normalized);
                var parentScale = transform.lossyScale;
                const float width = 0.22f;
                part.localScale = new Vector3(width / Mathf.Abs(parentScale.x),
                    leg.Lengths[index] / Mathf.Abs(parentScale.y), width / Mathf.Abs(parentScale.z));
            }
        }

        private static float ServoAdjustment(float time, float start, float hold)
        {
            const float travel = 0.22f;
            var elapsed = time - start;
            if (elapsed < 0f || elapsed >= travel * 2f + hold)
            {
                return 0f;
            }
            if (elapsed < travel)
            {
                return Mathf.SmoothStep(0f, 1f, elapsed / travel);
            }
            return elapsed < travel + hold ? 1f :
                Mathf.SmoothStep(1f, 0f, (elapsed - travel - hold) / travel);
        }

        internal void RefreshPose()
        {
            if (!_graphics)
            {
                return;
            }
            // The native root owns travel and wall orientation. Its animal skeleton
            // supplies foot contacts only; it never drives the machine's carriage or firing pose.
            transform.position = _graphics.transform.position + _graphics.transform.up * CannonBodyLift;
            transform.rotation = _graphics.transform.rotation;
            foreach (var leg in _legs)
            {
                PoseLeg(leg);
            }

            Quaternion aim;
            if (_aiming)
            {
                var direction = transform.InverseTransformDirection(
                    _aimAlongDirection ? _aimVector : _aimVector - _barrel.position);
                var pitch = -Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg;
                var yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                aim = Quaternion.Euler(pitch, yaw, 0);
            }
            else
            {
                var cycle = _idleTime % 13f;
                var yaw = ServoAdjustment(cycle, 2f, 0.5f) * 3f - ServoAdjustment(cycle, 8.5f, 0.35f) * 2f;
                var pitch = ServoAdjustment(cycle, 5.5f, 0.25f) * 1.5f;
                aim = _moving ? Quaternion.identity : Quaternion.Euler(pitch, yaw, 0);
            }
            _barrel.localRotation = Quaternion.RotateTowards(_barrel.localRotation, aim, Time.deltaTime * 180f);
            var elapsed = Time.time - _recoilStarted;
            var recovery = elapsed is >= 0f and < 0.28f ? 1f - elapsed / 0.28f : 0f;
            var recoil = recovery * recovery * 0.09f;
            _barrel.localPosition = _barrelOrigin - _barrel.localRotation * Vector3.forward * recoil;
        }

        internal void AimAt(Vector3 target, bool shot) => BeginAim(target, shot, false);

        internal void AimAlong(Vector3 direction, bool shot) => BeginAim(direction, shot, true);

        private void BeginAim(Vector3 target, bool shot, bool alongDirection)
        {
            if (!_aiming)
            {
                foreach (var leg in _legs)
                {
                    leg.BracedFoot = Quaternion.Inverse(_graphics.transform.rotation) *
                        (leg.Joints[3] - _graphics.transform.position);
                }
            }
            _aimVector = target;
            _aimAlongDirection = alongDirection;
            _aiming = true;
            _shot = shot;
            RefreshPose();
        }

        internal void FinishAim()
        {
            _aiming = false;
            _shot = false;
        }

        private void OnAnimationEvent(Transform source, bool visible, AnimationEventParameters parameters,
            AnimationEventDynamicParameters dynamicParameters)
        {
            if (dynamicParameters.graphicsCharacter != _graphics)
            {
                return;
            }
            if (parameters.CharacterEvent == GameLocationCharacterEventSystem.Event.MagicEffectLaunchPoint && _shot)
            {
                _recoilStarted = Time.time;
                RecoilCount++;
            }
            else if (parameters.CharacterEvent == GameLocationCharacterEventSystem.Event.MagicEffectAnimationEnd)
            {
                FinishAim();
            }
        }

        [UsedImplicitly]
        private void Update()
        {
            if (!_graphics || !_graphics.Animator)
            {
                return;
            }
            // The visible rig uses rigid mesh parts, so its hidden source skin cannot
            // decide whether the animator is needed during a move or a visible shot.
            _moving = GameLocationCharacter.GetFromActor(_graphics.RulesetCharacterMonster) is
                { MoveInProgress: true } or { MoveStepInProgress: true } or { MovingToDestination: true } or
                { Jumping: true } or { Climbing: true } or { Falling: true } or { IsCharging: true } or
                { IgnoreForPlacementDuringPushAnimation: true };
            _movementBlend = Mathf.MoveTowards(_movementBlend, _moving && !_aiming ? 1f : 0f, Time.deltaTime * 8f);
            _idleTime = _moving || _aiming ? 0f : _idleTime + Time.deltaTime;
            var animate = _moving || _aiming;
            foreach (var renderer in _renderers)
            {
                if (animate || renderer.isVisible)
                {
                    animate = true;
                    break;
                }
            }
            _graphics.Animator.cullingMode = animate ? AnimatorCullingMode.AlwaysAnimate : _originalCullingMode;
        }

        [UsedImplicitly]
        private void LateUpdate() => RefreshPose();

        [UsedImplicitly]
        private void OnDestroy()
        {
            if (_graphics)
            {
                _graphics.GraphicsCharacterEvent -= OnAnimationEvent;
                if (_graphics.Animator)
                {
                    _graphics.Animator.cullingMode = _originalCullingMode;
                }
            }
            foreach (var material in _ownedMaterials.Where(material => material))
            {
                Destroy(material);
            }
            foreach (var texture in _ownedTextures.Where(texture => texture))
            {
                Destroy(texture);
            }
        }
    }

    private static FeatureDefinitionPowerSharedPool BuildTinyFlamethrowerPower(
        FeatureDefinitionPower sharedPoolPower,
        ConditionDefinition conditionFlamethrowerTiny,
        int level)
    {
        return BuildTinyEldritchCannonPower(Flamethrower, sharedPoolPower, level, conditionFlamethrowerTiny);
    }

    private static FeatureDefinitionPowerSharedPool BuildTinyForceBallistaPower(
        FeatureDefinitionPower sharedPoolPower,
        ConditionDefinition conditionForceBallistaTiny,
        int level)
    {
        return BuildTinyEldritchCannonPower(ForceBallista, sharedPoolPower, level, conditionForceBallistaTiny);
    }

    private static FeatureDefinitionPowerSharedPool BuildTinyProtectorPower(
        FeatureDefinitionPower sharedPoolPower,
        ConditionDefinition conditionProtectorTiny,
        int level)
    {
        return BuildTinyEldritchCannonPower(Protector, sharedPoolPower, level, conditionProtectorTiny);
    }

    private static FeatureDefinitionPowerSharedPool BuildTinyEldritchCannonPower(
        string powerName, FeatureDefinitionPower sharedPoolPower, int level, ConditionDefinition conditionDefinition)
    {
        var name = PowerSummonCannon + powerName + "Tiny";

        var power = FeatureDefinitionPowerSharedPoolBuilder
            .Create(name + level)
            .SetGuiPresentation(name, Category.Feature,
                $"Feature/&PowerInnovationArtillerist{powerName}Description", hidden: true)
            .SetSharedPool(ActivationTime.Action, sharedPoolPower)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Hour, 1)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(EffectFormBuilder.ConditionForm(conditionDefinition))
                    .SetParticleEffectParameters(ConjureGoblinoids)
                    .Build())
            .SetUniqueInstance()
            .AddCustomSubFeatures(
                SkipEffectRemovalOnLocationChange.Always,
                CannonLimiter)
            .AddToDB();

        SummonPowers.Add(power);
        return power;
    }

    // Cannon effect statistics

    private sealed class CannonPowerStatistics(FeatureDefinitionPower power)
        : IPowerOrSpellInitiatedByMe, IPowerOrSpellFinishedByMe, IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == power;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            effectDescription.fixedSavingThrowDifficultyClass = GetDC(character);
            var owner = character.GetClassFeatureStatsOwner();
            if (power.Name == $"Power{Name}{Protector}" && owner != null && UsesFortifiedPosition2024(owner))
            {
                var temporaryHitPoints = effectDescription.EffectForms
                    .First(form => form.FormType == EffectForm.EffectFormType.TemporaryHitPoints).TemporaryHitPointsForm;
                temporaryHitPoints.diceNumber = 2;
                temporaryHitPoints.bonusHitPoints = Math.Max(1, AttributeDefinitions.ComputeAbilityScoreModifier(
                    owner.TryGetAttributeValue(AttributeDefinitions.Intelligence)));
            }

            return effectDescription;
        }

        public IEnumerator OnPowerOrSpellInitiatedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            if (action.ActionParams.activeEffect is RulesetEffectPower rulesetEffectPower)
            {
                rulesetEffectPower.usablePower.saveDC = GetDC(action.ActingCharacter.RulesetCharacter);
            }

            var factory = ServiceRepository.GetService<IWorldLocationEntityFactoryService>();
            if (factory != null && factory.TryFindWorldCharacter(action.ActingCharacter, out var world) && world.GraphicsCharacter)
            {
                var presentation = world.GraphicsCharacter.GetComponentInChildren<CannonPresentation>();
                if (presentation)
                {
                    var point = world.transform.position + world.transform.forward * 5f;
                    var parameters = action.ActionParams;
                    var effect = parameters.RulesetEffect.EffectDescription;
                    var target = action.ActionParams.TargetCharacters.FirstOrDefault();
                    var shot = power.Name is $"Power{Name}{Flamethrower}" or $"Power{Name}{ForceBallista}";
                    if (effect.TargetType == TargetType.Cone)
                    {
                        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
                        var gridPoint = parameters.Positions.Count > 0 ? parameters.Positions[0] :
                            action.ActingCharacter.LocationPosition;
                        var impact = !parameters.HasMagneticTargeting &&
                                     IsUsingFloatingImpactPoint(effect.RangeType, effect.TargetType)
                            ? parameters.CursorHoveredPosition
                            : positioning.GetWorldPositionFromGridPosition(gridPoint);
                        var origin = Vector3.zero;
                        var direction = Vector3.zero;
                        ServiceRepository.GetService<IGameLocationTargetingService>().ComputeTargetingParameters(
                            impact, action.ActingCharacter, action.ActingCharacter.LocationPosition,
                            effect.ShapeType, effect.RangeType, ref origin, ref direction);
                        presentation.AimAlong(direction, shot);
                    }
                    else
                    {
                        if (target != null && factory.TryFindWorldCharacter(target, out var targetWorld))
                        {
                            point = targetWorld.DeterministicCenterTargetPosition;
                        }
                        presentation.AimAt(point, shot);
                    }
                }
            }

            yield break;
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            var factory = ServiceRepository.GetService<IWorldLocationEntityFactoryService>();
            if (factory != null && factory.TryFindWorldCharacter(action.ActingCharacter, out var world) && world.GraphicsCharacter)
            {
                world.GraphicsCharacter.GetComponentInChildren<CannonPresentation>()?.FinishAim();
            }

            yield break;
        }

        private static int GetDC(RulesetCharacter rulesetCharacter)
        {
            var repertoire = rulesetCharacter.GetClassFeatureStatsOwner()?.GetClassSpellRepertoire(InventorClass.Class);

            return repertoire?.SaveDC ?? 15;
        }
    }

    // Refund Cannon

    private class CustomBehaviorRefundCannon : IValidatePowerUse, IPowerOrSpellFinishedByMe
    {
        private static FeatureDefinitionPower GetPool(RulesetCharacter character)
        {
            var level = character.GetClassLevel(InventorClass.Class);
            return level >= 15 ? _fortifiedPositionPool : level >= 9 ? _explosiveCannonPool : _eldritchCannonPool;
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition power)
        {
            var rulesetCharacter = action.ActingCharacter.RulesetCharacter;
            var spellRepertoire = rulesetCharacter.GetClassSpellRepertoire(InventorClass.Class);
            var slotLevel = spellRepertoire?.GetLowestAvailableSlotLevel() ?? 0;
            if (slotLevel <= 0 || action.Countered || action.ExecutionFailed)
            {
                yield break;
            }

            spellRepertoire.SpendSpellSlot(slotLevel);
            rulesetCharacter.RepayPowerUse(PowerProvider.Get(GetPool(rulesetCharacter), rulesetCharacter));
            rulesetCharacter.InflictCondition(SpellSlotCreation.Name, DurationType.UntilLongRest, 0,
                TurnOccurenceType.StartOfTurn, AttributeDefinitions.TagEffect, rulesetCharacter.Guid,
                rulesetCharacter.CurrentFaction.Name, 1, SpellSlotCreation.Name, 0, 0, 0);
        }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower featureDefinitionPower)
        {
            var spellRepertoire = character.GetClassSpellRepertoire(InventorClass.Class);

            if (spellRepertoire == null)
            {
                return false;
            }

            return character.GetRemainingUsesOfPower(PowerProvider.Get(GetPool(character), character)) == 0 &&
                   spellRepertoire.GetLowestAvailableSlotLevel() > 0;
        }
    }

    // Eldritch Detonation

    private sealed class DetonateTinyCannon(ConditionDefinition condition = null) : IPowerOrSpellFinishedByMe
    {
        private RulesetEffectPower GetCannon(RulesetCharacter character) => GetCannons(character)
            .FirstOrDefault(effect => effect.EffectDescription.EffectForms.GetAppliedConditionDefinitions()
                .Any(definition => condition
                    ? definition.IsSubtypeOf(condition.Name)
                    : TinyCannonModes.Keys.Any(tiny => definition.IsSubtypeOf(tiny.Name))));

        internal bool HasCannon(RulesetCharacter character) => GetCannon(character) != null;

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition power)
        {
            var character = action.ActingCharacter.RulesetCharacter;
            GetCannon(character)?.DoTerminate(character);
            yield break;
        }
    }

    private sealed class PowerOrSpellFinishedByMeEldritchDetonationDetonate(
        FeatureDefinitionPower powerEldritchDetonation)
        : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition power)
        {
            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            var selectedTarget = action.ActionParams.TargetCharacters[0];
            var rulesetTarget = selectedTarget.RulesetCharacter;
            var usablePower = PowerProvider.Get(powerEldritchDetonation, rulesetTarget);
            var targets = characterService.AllValidEntities
                .Where(x =>
                    x != selectedTarget &&
                    x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                    x.IsWithinRange(selectedTarget, 4))
                .ToArray();

            // eldirith detonation is a use at will power
            selectedTarget.MyExecuteActionSpendPower(usablePower, targets);

            yield break;
        }
    }

    // can only dismiss the cannon after it fully detonates
    private sealed class PowerOrSpellFinishedByMeEldritchDetonationDismiss : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            var rulesetCharacter = action.ActingCharacter.RulesetCharacter;

            var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();

            var applyFormsParams = new RulesetImplementationDefinitions.ApplyFormsParams
            {
                sourceCharacter = rulesetCharacter, targetCharacter = rulesetCharacter
            };

            implementationService.ApplyEffectForms(
                [
                    new EffectForm
                    {
                        formType = EffectForm.EffectFormType.Counter,
                        counterForm = new CounterForm { type = CounterForm.CounterType.DismissCreature }
                    }
                ],
                applyFormsParams,
                [],
                out _,
                out _);

            yield break;
        }
    }

    private sealed class OwnCannonTargetFilter : IFilterTargetingCharacter
    {
        internal static OwnCannonTargetFilter Instance { get; } = new();

        public bool EnforceFullSelection => true;

        public bool IsValid(CursorLocationSelectTarget cursor, GameLocationCharacter target)
        {
            return target.RulesetCharacter is RulesetCharacterMonster monster &&
                   monster.MonsterDefinition.CreatureTags.Contains(CreatureTag) &&
                   EffectHelpers.GetSummoner(monster) == cursor.ActionParams.ActingCharacter.RulesetCharacter;
        }
    }

    private sealed class CreateSecondCannon(ConditionDefinition continuation)
        : IPowerOrSpellFinishedByMe, IActionFinishedByMe, IValidatePowerUse,
            ICharacterBattleStartedListener, ICharacterBattleEndedListener, IOnLocationCharacterRestored,
            ICharacterTurnStartListener, ICharacterBeforeTurnEndListener, IOnCharacterPostLoad
    {
        public int Priority => 0;

        public void OnLocationCharacterRestored(RulesetCharacter character)
        {
            SynchronizeTinyCannonTracking(character);
            OnCharacterPostLoad(character);
        }

        public void OnCharacterPostLoad(RulesetCharacter character)
        {
            SynchronizeCannonModes(character);
            SynchronizeCannonPool(character);
            RemoveConditions(character, SecondCannon);
        }

        public void OnCharacterBattleStarted(GameLocationCharacter character, bool surprise) =>
            ClearTurnConditions(character.RulesetCharacter);

        public void OnCharacterBattleEnded(GameLocationCharacter character) =>
            ClearTurnConditions(character.RulesetCharacter);

        public void OnCharacterTurnStarted(GameLocationCharacter character) =>
            ClearTurnConditions(character.RulesetCharacter);

        public void OnCharacterBeforeTurnEnded(GameLocationCharacter character) =>
            RemoveConditions(character.RulesetCharacter, SecondCannon);

        private static void ClearTurnConditions(RulesetCharacter character)
        {
            foreach (var condition in CannonTurnConditions)
            {
                RemoveConditions(character, condition.Name);
            }
        }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            if (SummonPowers.Contains(power))
            {
                return true;
            }
            var ready = GetContinuation(character);
            return UsesFortifiedPosition2024(character) && ready != null && GetCannons(character).Count() == 1 &&
                   (ready.Amount == 0 || HasSpellSlot(character));
        }

        private static bool HasSpellSlot(RulesetCharacter character) =>
            character.GetClassSpellRepertoire(InventorClass.Class)?.GetLowestAvailableSlotLevel() > 0;

        private static RulesetCondition GetContinuation(RulesetCharacter character)
        {
            character.GetAllConditions(character.AllConditionsForEnumeration);
            return character.AllConditionsForEnumeration.FirstOrDefault(condition =>
                condition.ConditionDefinition.Name == SecondCannon);
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            var character = action.ActingCharacter.RulesetCharacter;
            if (action.Countered || action.ExecutionFailed ||
                action.ActionParams.RulesetEffect is not RulesetEffectPower { Terminated: false } effect ||
                !GetCannons(character).Contains(effect))
            {
                yield break;
            }

            if (SummonPowers.Contains(definition))
            {
                var spellSlotPaid = character.HasConditionOfType(SpellSlotCreation.Name);
                RemoveConditions(character, SpellSlotCreation.Name);
                RemoveConditions(character, SecondCannon);
                if (UsesFortifiedPosition2024(character) && _fortifiedSummons.Contains(definition) &&
                    GetCannons(character).Count() == 1 && (!spellSlotPaid || HasSpellSlot(character)))
                {
                    character.InflictCondition(continuation.Name, DurationType.Permanent, 0,
                        TurnOccurenceType.StartOfTurn, AttributeDefinitions.TagEffect, character.Guid,
                        character.CurrentFaction.Name, 0, continuation.Name, spellSlotPaid ? 1 : 0, 0, 0);
                }
                yield break;
            }

            var ready = GetContinuation(character);
            if (!UsesFortifiedPosition2024(character) || ready == null || ready.Amount != 0 && !HasSpellSlot(character))
            {
                character.TerminatePower(effect);
                yield break;
            }

            if (ready.Amount != 0)
            {
                var repertoire = character.GetClassSpellRepertoire(InventorClass.Class);
                repertoire.SpendSpellSlot(repertoire.GetLowestAvailableSlotLevel());
            }
            RemoveConditions(character, SecondCannon);
        }

        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            var definition = action.ActionParams.RulesetEffect?.GetSourceDefinitionSafe();
            if (!SummonPowers.Contains(definition))
            {
                RemoveConditions(action.ActingCharacter.RulesetCharacter, SecondCannon);
            }
            yield break;
        }
    }

    private sealed class CommandTinyCannons(ConditionDefinition[] actions) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            var character = action.ActingCharacter.RulesetCharacter;
            var count = GetTinyCannons(character).Count();
            if (action.Countered || action.ExecutionFailed || count == 0 ||
                GetCannons(character).Count() < 2 || actions.Any(condition => character.HasConditionOfType(condition.Name)))
            {
                yield break;
            }
            var condition = actions[Math.Min(count, 2) - 1];
            character.InflictCondition(condition.Name, DurationType.Round, 1, TurnOccurenceType.StartOfTurn,
                AttributeDefinitions.TagEffect, character.Guid, character.CurrentFaction.Name, 1,
                condition.Name, 0, 0, 0);
        }
    }

    private sealed class ActivateTinyCannon(int type, ConditionDefinition activated)
        : IPowerOrSpellFinishedByMe, IValidatePowerUse
    {
        private RulesetEffectPower GetAvailableCannon(RulesetCharacter character)
        {
            character.GetAllConditions(character.AllConditionsForEnumeration);
            var used = character.AllConditionsForEnumeration
                .Where(condition => condition.ConditionDefinition == activated)
                .Select(condition => condition.Guid).ToHashSet();
            return GetTinyCannons(character, Main.Settings.EnableArtilleristEldritchCannon2024 ? 0 : type)
                .OrderBy(cannon => GetTinyCannonType(cannon) == type ? 0 : 1)
                .FirstOrDefault(cannon => !cannon.TrackedConditionGuids.Any(used.Contains));
        }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            if (Gui.Battle == null || !HasTinyCannon(character))
            {
                return true;
            }

            return GetAvailableCannon(character) != null &&
                   (GetCannons(character).Count() <= 1 || character.HasConditionOfType(ConditionCommandCannon));
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            var character = action.ActingCharacter.RulesetCharacter;
            if (Gui.Battle != null && !action.Countered && !action.ExecutionFailed &&
                GetAvailableCannon(character) is { } cannon)
            {
                var marker = character.InflictCondition(activated.Name, DurationType.Round, 1,
                    TurnOccurenceType.StartOfTurn,
                    AttributeDefinitions.TagEffect, character.Guid, character.CurrentFaction.Name,
                    0, activated.Name, GetTinyCannonType(cannon), 0, 0);
                // The creating effect owns this physical cannon and its activation.
                // Ending either of two identical cannons removes only its own marker.
                // Its existing ownership condition already subscribes this actor
                // to the native tracking callbacks. Re-subscribing every turn
                // would retain duplicate handlers after the marker expires.
                cannon.TrackedConditionGuids.Add(marker.Guid);
            }
            yield break;
        }
    }
}
