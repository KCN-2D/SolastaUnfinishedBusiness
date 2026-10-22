using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
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
using static RuleDefinitions;
using static ActionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Subclasses.CommonBuilders;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public sealed class InnovationWeapon : AbstractSubclass
{
    private const string SteelDefenderTag = "SteelDefender";
    private const string CommandSteelDefenderCondition = "ConditionInventorWeaponSteelDefenerCommand";
    private const string SummonSteelDefenderPower = "PowerInnovationWeaponSummonSteelDefender";
    private const string SummonAdvancedSteelDefenderPower = "PowerInnovationWeaponSummonAdvancedSteelDefender";
    private const string ArcaneJoltPower = "PowerInnovationWeaponArcaneJolt";
    private const string ArcaneJoltSprite = "InventorArcaneJolt";
    private const string ArcaneJoltUsedCondition = "ConditionInnovationWeaponArcaneJoltUsed";

    public InnovationWeapon()
    {
        var steelDefenderFeatureSet =
            BuildSteelDefenderFeatureSet(out var steelDefenderPower, out var steelDefenderMonster);

        Subclass = CharacterSubclassDefinitionBuilder
            .Create("InnovationWeapon")
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite("InventorWeapon", Resources.InventorWeapon, 256))
            .AddFeaturesAtLevel(3, BuildBattleReady(), BuildAutoPreparedSpells(), steelDefenderFeatureSet)
            .AddFeaturesAtLevel(5, AttributeModifierCasterFightingExtraAttack)
            .AddFeaturesAtLevel(9, BuildArcaneJolt())
            .AddFeaturesAtLevel(15, BuildImprovedDefenderFeatureSet(steelDefenderPower, steelDefenderMonster))
            .AddToDB();
    }

    internal override CharacterSubclassDefinition Subclass { get; }
    internal override CharacterClassDefinition Klass => InventorClass.Class;
    internal override FeatureDefinitionSubclassChoice SubclassChoice => InventorClass.SubclassChoice;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    private static FeatureDefinitionProficiency BuildBattleReady()
    {
        return FeatureDefinitionProficiencyBuilder
            .Create("ProficiencyInnovationWeaponBattleReady")
            .SetGuiPresentation(Category.Feature)
            .SetProficiencies(ProficiencyType.Weapon, EquipmentDefinitions.MartialWeaponCategory)
            .AddCustomSubFeatures(
                new CanUseAttribute(AttributeDefinitions.Intelligence, ValidatorsWeapon.IsMagical))
            .AddToDB();
    }

    private static FeatureDefinitionAutoPreparedSpells BuildAutoPreparedSpells()
    {
        return FeatureDefinitionAutoPreparedSpellsBuilder
            .Create("AutoPreparedSpellsInnovationWeapon")
            .SetGuiPresentation(Category.Feature)
            .SetSpellcastingClass(InventorClass.Class)
            .SetAutoTag("InventorWeaponsmith")
            .AddPreparedSpellGroup(3, Heroism, Shield)
            .AddPreparedSpellGroup(5, BrandingSmite, SpiritualWeapon)
            .AddPreparedSpellGroup(9, RemoveCurse, BeaconOfHope)
            .AddPreparedSpellGroup(13, FireShield, DeathWard)
            .AddPreparedSpellGroup(17, MassCureWounds, SpellsContext.Telekinesis)
            .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildSteelDefenderFeatureSet(
        out FeatureDefinitionPower steelDefenderPower,
        out MonsterDefinition monsterDefinition)
    {
        steelDefenderPower = BuildSteelDefenderPower(out monsterDefinition);
        var commandPower = BuildCommandSteelDefender(out var commandCondition);
        var command = new InventorClass.ConstructCommand(commandCondition, commandPower, SteelDefenderTag, true);
        commandCondition.AddCustomSubFeatures(command);
        commandPower.AddCustomSubFeatures(command);
        monsterDefinition.Features.OfType<FeatureDefinitionActionAffinity>()
            .Single(feature => feature.Name == "ActionAffinitySteelDefenderBasic").AddCustomSubFeatures(command);

        return FeatureDefinitionFeatureSetBuilder
            .Create("FeatureSetInnovationWeaponSteelDefender")
            .SetGuiPresentation(Category.Feature)
            .AddFeatureSet(
                steelDefenderPower,
                commandPower,
                BuildSteelDefenderShortRestRecovery(),
                BuildSteelDefenderAffinity())
            .AddToDB();
    }

    private static FeatureDefinitionPower BuildSteelDefenderShortRestRecovery()
    {
        const string NAME = "PowerInnovationWeaponSteelDefenderRecuperate";

        RestActivityDefinitionBuilder
            .Create("RestActivityInnovationWeaponSteelDefenderRecuperate")
            .SetGuiPresentation(NAME, Category.Feature)
            .SetRestData(
                RestDefinitions.RestStage.AfterRest,
                RestType.ShortRest,
                RestActivityDefinition.ActivityCondition.CanUsePower,
                PowerBundleContext.UseCustomRestPowerFunctorName,
                NAME)
            .AddToDB();

        var power = FeatureDefinitionPowerBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(
                ModifyPowerVisibility.Hidden,
                new ValidatorsValidatePowerUse(HasInjuredDefender),
                new ModifyRestPowerTitleHandler(GetRestPowerTitle),
                new TargetDefendingBlade())
            .SetUsesFixed(ActivationTime.Rest, RechargeRate.LongRest, 1, 0)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetHealingForm(HealingComputation.Dice, 0, DieType.D8, 1, false,
                                HealingCap.MaximumHitPoints)
                            .Build())
                    .Build())
            .AddToDB();

        power.AddCustomSubFeatures(
            HasModifiedUses.Marker,
            new ModifyPowerPoolAmount
            {
                PowerPool = power,
                Type = PowerPoolBonusCalculationType.ClassLevel,
                Attribute = InventorClass.ClassName
            });

        return power;
    }

    private static RulesetCharacter GetBladeDefender(RulesetCharacter character)
    {
        var bladeEffect = character.powersUsedByMe.Find(p =>
            p.sourceDefinition.Name is SummonSteelDefenderPower or SummonAdvancedSteelDefenderPower);

        if (bladeEffect == null)
        {
            return null;
        }

        var summons = EffectHelpers.GetSummonedCreatures(bladeEffect);

        return summons.Count == 0 ? null : summons[0];
    }

    private static bool HasInjuredDefender(RulesetCharacter character)
    {
        return GetBladeDefender(character) is { IsMissingHitPoints: true };
    }

    private static string GetRestPowerTitle(RulesetCharacter character)
    {
        var blade = GetBladeDefender(character);

        if (blade == null)
        {
            return string.Empty;
        }

        return Gui.Format("Feature/&PowerInnovationWeaponSteelDefenderRecuperateFormat",
            blade.CurrentHitPoints.ToString(),
            blade.TryGetAttributeValue(AttributeDefinitions.HitPoints).ToString());
    }

    private static FeatureDefinitionPower BuildSteelDefenderPower(out MonsterDefinition monsterDefinition)
    {
        monsterDefinition = BuildSteelDefenderMonster();

        return FeatureDefinitionPowerBuilder
            .Create(SummonSteelDefenderPower)
            .SetGuiPresentation(Category.Feature,
                Sprites.GetSprite("SteelDefenderPower", Resources.SteelDefenderPower, 256, 128))
            .SetUsesFixed(ActivationTime.Action, RechargeRate.LongRest)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Permanent)
                    .SetTargetingData(Side.Ally, RangeType.Distance, 3, TargetType.Position)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetSummonCreatureForm(1, monsterDefinition.Name)
                            .Build())
                    .SetParticleEffectParameters(ConjureElementalAir)
                    .Build())
            .SetUniqueInstance()
            .AddCustomSubFeatures(
                RestrictEffectToNotTerminateWhileUnconscious.Marker,
                SkipEffectRemovalOnLocationChange.Always)
            .AddToDB();
    }

    private static FeatureDefinitionPower BuildAdvancedSteelDefenderPower(
        FeatureDefinitionPower overridenPower,
        MonsterDefinition steelDefenderMonster)
    {
        var defender = BuildAdvancedSteelDefenderMonster(steelDefenderMonster);

        return FeatureDefinitionPowerBuilder
            .Create(SummonAdvancedSteelDefenderPower)
            .SetGuiPresentation(SummonSteelDefenderPower, Category.Feature,
                Sprites.GetSprite("SteelDefenderPower", Resources.SteelDefenderPower, 256, 128))
            .SetUsesFixed(ActivationTime.Action, RechargeRate.LongRest)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Permanent)
                    .SetTargetingData(Side.Ally, RangeType.Distance, 3, TargetType.Position)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetSummonCreatureForm(1, defender.Name)
                            .Build())
                    .SetParticleEffectParameters(ConjureElementalAir)
                    .Build())
            .SetUniqueInstance()
            .SetOverriddenPower(overridenPower)
            .AddCustomSubFeatures(
                RestrictEffectToNotTerminateWhileUnconscious.Marker,
                SkipEffectRemovalOnLocationChange.Always,
                ValidatorsValidatePowerUse.NotInCombat)
            .AddToDB();
    }

    private static FeatureDefinitionSummoningAffinity BuildSteelDefenderAffinity()
    {
        var hpBonus = FeatureDefinitionAttributeModifierBuilder
            .Create("AttributeModifierInnovationWeaponSummonSteelDefenderHP")
            .SetGuiPresentationNoContent()
            .SetAddConditionAmount(AttributeDefinitions.HitPoints)
            .AddToDB();

        var toHit = FeatureDefinitionAttackModifierBuilder
            .Create("AttackModifierInnovationWeaponSummonSteelDefenderToHit")
            .SetGuiPresentationNoContent()
            .SetAttackRollModifier(1, AttackModifierMethod.SourceConditionAmount)
            .AddToDB();

        var toDamage = FeatureDefinitionAttackModifierBuilder
            .Create("AttackModifierInnovationWeaponSummonSteelDefenderDamage")
            .SetGuiPresentationNoContent()
            .SetDamageRollModifier(1, AttackModifierMethod.SourceConditionAmount)
            .AddToDB();

        var savingThrows = FeatureDefinitionSavingThrowAffinityBuilder
            .Create("SavingThrowAffinityInnovationWeaponSummonSteelDefender")
            .SetGuiPresentationNoContent()
            .AddCustomSubFeatures(
                new AddPBToSummonCheck(1,
                    AttributeDefinitions.Dexterity,
                    AttributeDefinitions.Constitution))
            .AddToDB();

        var skills = FeatureDefinitionAbilityCheckAffinityBuilder
            .Create("AbilityCheckAffinityInnovationWeaponSummonSteelDefenderSkills")
            .SetGuiPresentationNoContent()
            .AddCustomSubFeatures(
                new AddPBToSummonCheck(1, SkillDefinitions.Athletics),
                new AddPBToSummonCheck(2, SkillDefinitions.Perception))
            .AddToDB();

        return FeatureDefinitionSummoningAffinityBuilder
            .Create("SummoningAffinityInnovationWeaponSummonSteelDefender")
            .SetGuiPresentationNoContent()
            .SetRequiredMonsterTag(SteelDefenderTag)
            .SetAddedConditions(
                //Generic bonuses
                ConditionDefinitionBuilder
                    .Create("ConditionInnovationWeaponSummonSteelDefenderGeneric")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetAmountOrigin(ConditionDefinition.OriginOfAmount.SourceSpellAttack)
                    .SetFeatures(savingThrows, skills)
                    .AddToDB(),
                //Bonuses from Inventor's spell attack
                ConditionDefinitionBuilder
                    .Create("ConditionInnovationWeaponSummonSteelDefenderSpellAttack")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetPossessive()
                    .SetAmountOrigin(ConditionDefinition.OriginOfAmount.SourceSpellAttack)
                    .SetFeatures(toHit)
                    .AddCustomSubFeatures(ClassHolder.Inventor)
                    .AddToDB(),
                //Bonuses from Inventor's Proficiency Bonus
                ConditionDefinitionBuilder
                    .Create("ConditionInnovationWeaponSummonSteelDefenderProficiencyBonus")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetAmountOrigin(ExtraOriginOfAmount.SourceProficiencyBonus)
                    .SetFeatures(toDamage)
                    .AddToDB(),
                //Bonuses from Inventor's level
                ConditionDefinitionBuilder
                    .Create("ConditionInnovationWeaponSummonSteelDefenderLevel")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetAmountOrigin(ExtraOriginOfAmount.SourceClassLevel, InventorClass.ClassName)
                    .SetFeatures(hpBonus, hpBonus, hpBonus, hpBonus, hpBonus) // 5 HP per level
                    .AddToDB(),
                //Bonuses from Inventor's INT
                ConditionDefinitionBuilder
                    .Create("ConditionInnovationWeaponSummonSteelDefenderIntelligence")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetAmountOrigin(ExtraOriginOfAmount.SourceAbilityBonus, AttributeDefinitions.Intelligence)
                    .SetFeatures(hpBonus) // 1 hp per INT mod
                    .AddToDB())
            .AddToDB();
    }

    private static MonsterDefinition BuildSteelDefenderMonster()
    {
        var monsterAttackSteelDefender = MonsterAttackDefinitionBuilder
            .Create("MonsterAttackSteelDefender")
            .SetGuiPresentation(Category.Item, GuiPresentationBuilder.EmptyString)
            .SetToHitBonus(0)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetDamageForm(DamageTypeForce, 1, DieType.D8)
                            .SetCreatedBy(false, false)
                            .Build())
                    .Build())
            .AddToDB();

        var monster = MonsterDefinitionBuilder
            .Create("InnovationWeaponSteelDefender")
            .SetGuiPresentation(Category.Monster, MonsterDefinitions.Golem_Iron.GuiPresentation.SpriteReference)
            .SetDungeonMakerPresence(MonsterDefinition.DungeonMaker.None)
            .SetAbilityScores(14, 12, 14, 4, 10, 6)
            .SetSkillScores(
                (SkillDefinitions.Athletics, 2), //has feature that adds summoner's PB
                (SkillDefinitions.Perception, 0)) //has feature that adds summoner's PB x2
            .SetSavingThrowScores(
                (AttributeDefinitions.Dexterity, 1), //has feature that adds summoner's PB
                (AttributeDefinitions.Constitution, 2)) //has feature that adds summoner's PB
            .SetStandardHitPoints(2)
            .SetHitPointsBonus(2) //doesn't seem to be used anywhere
            .SetHitDice(DieType.D8, 1) //TODO: setup to 1 die per inventor level
            .SetArmorClass(15, EquipmentDefinitions.EmptyMonsterArmor) //natural armor
            .SetAttackIterations((1, monsterAttackSteelDefender))
            .SetFeatures(
                FeatureDefinitionMoveModes.MoveModeMove8,
                FeatureDefinitionSenses.SenseDarkvision,
                FeatureDefinitionDamageAffinitys.DamageAffinityPoisonImmunity,
                FeatureDefinitionPowerBuilder
                    .Create("PowerInnovationWeaponSteelDefenderRepair")
                    .SetGuiPresentation(Category.Feature,
                        Sprites.GetSprite("SteelDefenderRepair", Resources.SteelDefenderRepair, 256, 128))
                    .SetUsesFixed(ActivationTime.Action, RechargeRate.LongRest, 1, 3)
                    // RAW this can heal any other Inventor construct, this version only heals self
                    .SetEffectDescription(
                        EffectDescriptionBuilder
                            .Create()
                            .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                            .SetEffectForms(
                                EffectFormBuilder
                                    .Create()
                                    .SetHealingForm(HealingComputation.Dice, 4, DieType.D8, 2, false,
                                        HealingCap.MaximumHitPoints)
                                    .Build())
                            .Build())
                    .AddToDB(),
                FeatureDefinitionConditionAffinityBuilder
                    .Create("ConditionAffinityInnovationWeaponSteelDefenderInitiative")
                    .SetGuiPresentationNoContent()
                    .SetConditionAffinityType(ConditionAffinityType.Immunity)
                    .SetConditionType(ConditionDefinitions.ConditionSurprised)
                    .AddCustomSubFeatures(ForceInitiativeToSummoner.Mark)
                    .AddToDB(),
                FeatureDefinitionPerceptionAffinityBuilder
                    .Create("PerceptionAffinitySteelDefender")
                    .SetGuiPresentationNoContent()
                    .CannotBeSurprised()
                    .AddToDB(),
                FeatureDefinitionActionAffinityBuilder
                    .Create("ActionAffinitySteelDefenderBasic")
                    .SetGuiPresentationNoContent()
                    .SetForbiddenActions(
                        Id.AttackMain, Id.AttackOff, Id.AttackFree, Id.AttackReadied, Id.Ready,
                        Id.PowerMain, Id.PowerBonus, Id.PowerNoCost, Id.SpendPower,
                        Id.Shove, Id.ShoveBonus, Id.ShoveFree)
                    .AddToDB(),
                FeatureDefinitionActionAffinitys.ActionAffinityFightingStyleProtection,
                FeatureDefinitionConditionAffinitys.ConditionAffinityCharmImmunity,
                FeatureDefinitionConditionAffinitys.ConditionAffinityExhaustionImmunity,
                FeatureDefinitionConditionAffinitys.ConditionAffinityPoisonImmunity,
                RulesContext.PowerTeleportSummon,
                RulesContext.PowerVanishSummon)
            .SetCreatureTags(SteelDefenderTag)
            .SetDefaultFaction(FactionDefinitions.Party)
            .SetFullyControlledWhenAllied(true)
            .SetHeight(6)
            .SetSizeDefinition(CharacterSizeDefinitions.Medium)
            .SetCharacterFamily(InventorClass.InventorConstructFamily)
            .SetAlignment(MonsterDefinitionBuilder.NeutralAlignment)
            .NoExperienceGain()
            .SetChallengeRating(0)
            .SetBestiaryEntry(BestiaryDefinitions.BestiaryEntry.None)
            .SetDungeonMakerPresence(MonsterDefinition.DungeonMaker.None)
            .SetMonsterPresentation(
                MonsterPresentationBuilder
                    .Create()
                    .SetPrefab(MonsterDefinitions.Golem_Iron.MonsterPresentation.malePrefabReference)
                    .SetModelScale(0.25f)
                    .SetHasMonsterPortraitBackground(true)
                    .SetCanGeneratePortrait(true)
                    .Build())
            .AddToDB();

        return monster;
    }

    private static MonsterDefinition BuildAdvancedSteelDefenderMonster(MonsterDefinition steelDefenderMonster)
    {
        var monster = MonsterDefinitionBuilder
            .Create(steelDefenderMonster, "InnovationWeaponAdvancedSteelDefender")
            .SetArmorClass(17, EquipmentDefinitions.EmptyMonsterArmor) //natural armor
            .AddToDB();

        return monster;
    }

    private static FeatureDefinitionPower BuildCommandSteelDefender(out ConditionDefinition condition)
    {
        condition = ConditionDefinitionBuilder
            .Create(CommandSteelDefenderCondition)
            .SetGuiPresentation(Category.Condition, Sprites.GetConditionSprite("ConditionSteelDefenderCommanded"))
            .SetConditionType(ConditionType.Beneficial)
            .SetPossessive()
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();

        var power = FeatureDefinitionPowerBuilder
            .Create("PowerInventorWeaponSteelDefenderCommand")
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
                            .SetConditionForm(condition, ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddToDB();

        power.AddCustomSubFeatures(new ShowInCombatWhenHasBlade());

        return power;
    }

    private static FeatureDefinitionPower BuildArcaneJolt()
    {
        var conditionUsed = ConditionDefinitionBuilder
            .Create(ArcaneJoltUsedCondition)
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialInterruptions(ConditionInterruption.AnyBattleTurnEnd, ConditionInterruption.BattleEnd)
            .AddToDB();
        var power = FeatureDefinitionPowerBuilder
            .Create(ArcaneJoltPower)
            .SetGuiPresentation(Category.Feature,
                Sprites.GetSprite(ArcaneJoltSprite, Resources.InventorArcaneJolt, 256, 128))
            .SetUsesAbilityBonus(ActivationTime.OnAttackHit, RechargeRate.LongRest, AttributeDefinitions.Intelligence)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 1, TargetType.IndividualsUnique)
                    .SetEffectForms(BuildArcaneJoltDamageForm())
                    .Build())
            .SetShowCasting(false)
            .DelegatedToAction()
            .AddCustomSubFeatures(ClassHolder.Inventor)
            .AddToDB();

        var damage = FeatureDefinitionPowerSharedPoolBuilder
            .Create(ArcaneJoltPower + "Damage")
            .SetGuiPresentation(Category.Feature, power)
            .SetSharedPool(ActivationTime.NoCost, power)
            .SetShowCasting(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Enemy, RangeType.MeleeHit, 1, TargetType.IndividualsUnique)
                .SetEffectForms(BuildArcaneJoltDamageForm())
                .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, ClassHolder.Inventor)
            .AddToDB();
        var healing = FeatureDefinitionPowerSharedPoolBuilder
            .Create(ArcaneJoltPower + "Healing")
            .SetGuiPresentation(Category.Feature, power)
            .SetSharedPool(ActivationTime.NoCost, power)
            .SetShowCasting(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.All, RangeType.Distance, 6, TargetType.IndividualsUnique)
                .SetEffectForms(EffectFormBuilder.Create()
                    .SetHealingForm(HealingComputation.Dice, 0, DieType.D6, 2, false, HealingCap.MaximumHitPoints)
                    .Build())
                .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, ClassHolder.Inventor,
                TargetingContext.BeneficialEffect.Marker,
                new ReactionRequestSpendBundlePower.EffectContext(targetsSelf: true))
            .AddToDB();
        var behavior = new PhysicalAttackBeforeHitConfirmedOnEnemyArcaneJolt(power, damage, healing, conditionUsed);
        var upgrade = new ModifyEffectDescriptionArcaneJolt();

        power.AddCustomSubFeatures(
            new ValidatorsValidatePowerUse(character => !character.HasConditionOfType(ArcaneJoltUsedCondition)),
            ModifyPowerVisibility.Default,
            behavior, upgrade);
        damage.AddCustomSubFeatures(upgrade);
        healing.AddCustomSubFeatures(upgrade);
        GetDefinition<ConditionDefinition>("ConditionInnovationWeaponSummonSteelDefenderGeneric")
            .AddCustomSubFeatures(behavior);
        PowerBundle.RegisterPowerBundle(power, false, damage, healing);

        return power;
    }

    private static EffectForm BuildArcaneJoltDamageForm(RulesetCharacter character = null)
    {
        return EffectFormBuilder
            .Create()
            .SetDamageForm(DamageTypeForce, character?.GetClassLevel(InventorClass.Class) >= 15 ? 4 : 2, DieType.D6)
            .Build();
    }

    private sealed class ModifyEffectDescriptionArcaneJolt : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return character.GetClassLevel(InventorClass.Class) >= 15;
        }

        public EffectDescription GetEffectDescription(BaseDefinition definition, EffectDescription effectDescription,
            RulesetCharacter character, RulesetEffect rulesetEffect)
        {
            foreach (var form in effectDescription.EffectForms)
            {
                if (form.FormType == EffectForm.EffectFormType.Damage) form.DamageForm.DiceNumber = 4;
                if (form.FormType == EffectForm.EffectFormType.Healing) form.HealingForm.DiceNumber = 4;
            }
            return effectDescription;
        }
    }

    private static FeatureDefinitionFeatureSet BuildImprovedDefenderFeatureSet(
        FeatureDefinitionPower steelDefenderPower, MonsterDefinition steelDefenderMonster)
    {
        return FeatureDefinitionFeatureSetBuilder
            .Create("FeatureSetInnovationWeaponImprovedDefender")
            .SetGuiPresentation(Category.Feature)
            .AddFeatureSet(BuildAdvancedSteelDefenderPower(steelDefenderPower, steelDefenderMonster))
            .AddToDB();
    }

    private class ShowInCombatWhenHasBlade : IValidatePowerUse
    {
        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower featureDefinitionPower)
        {
            return Gui.Battle != null && GetBladeDefender(character) != null;
        }
    }

    private sealed class PhysicalAttackBeforeHitConfirmedOnEnemyArcaneJolt(
        FeatureDefinitionPower powerArcaneJolt,
        FeatureDefinitionPower damagePower,
        FeatureDefinitionPower healingPower,
        ConditionDefinition conditionUsed)
        : IPhysicalAttackBeforeHitConfirmedOnEnemy
    {
        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier actionModifier,
            RulesetAttackMode attackMode,
            bool rangedAttack,
            AdvantageType advantageType,
            List<EffectForm> actualEffectForms,
            bool firstTarget,
            bool criticalHit)
        {
            var character = attacker.RulesetCharacter;
            var isDefender = character is RulesetCharacterMonster monster &&
                             monster.MonsterDefinition.CreatureTags.Contains(SteelDefenderTag);
            if (!isDefender &&
                (attackMode?.SourceDefinition is not ItemDefinition { IsWeapon: true } ||
                 !ValidatorsWeapon.IsMagical(attackMode, attackMode.SourceObject as RulesetItem, character)))
            {
                yield break;
            }
            var owner = isDefender ? character.GetMySummoner() : attacker;
            var rulesetOwner = owner?.RulesetCharacter;
            if (rulesetOwner == null || rulesetOwner.GetClassLevel(InventorClass.Class) < 9)
            {
                yield break;
            }

            bool ReactionStillValid() => Gui.Battle != null &&
                rulesetOwner is { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } &&
                TargetingContext.IsValidOffensiveReactionTarget(attacker, defender, powerArcaneJolt) &&
                defender.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                rulesetOwner.CanUsePower(powerArcaneJolt, true, true);

            bool HealingTargetValid(GameLocationCharacter candidate) => ReactionStillValid() &&
                ReactionRequestSelectTarget.IsCandidateValid(owner, candidate) &&
                candidate.RulesetCharacter is { IsDead: false, IsRemovedFromTheGame: false, IsMissingHitPoints: true } &&
                defender.IsWithinRange(candidate, 6) && owner.CanSeeTarget(candidate) &&
                TargetingContext.IsBeneficialTargetAllowed(owner, candidate);

            if (!ReactionStillValid())
            {
                yield break;
            }

            var usablePower = PowerProvider.Get(powerArcaneJolt, rulesetOwner);
            var temporary = new List<RulesetUsablePower>();
            foreach (var choice in new[] { damagePower, healingPower })
            {
                if (rulesetOwner.UsablePowers.Any(usable => usable.PowerDefinition == choice)) continue;
                var usable = PowerProvider.Get(choice, rulesetOwner);
                usable.originClass = InventorClass.Class;
                rulesetOwner.UsablePowers.Add(usable);
                temporary.Add(usable);
            }
            try
            {
                FeatureDefinitionPower selected = null;
                bool ModeAvailable(FeatureDefinitionPower choice) => choice != healingPower ||
                    ServiceRepository.GetService<IGameLocationCharacterService>().ValidCharacters.Any(HealingTargetValid);
                yield return owner.MyReactToSelectPowerBundle(usablePower, defender, "ArcaneJolt", 1,
                    () => rulesetOwner.GetRemainingPowerUses(powerArcaneJolt),
                    choices => selected = choices[0], ReactionStillValid, executeSelectedPowers: false,
                    powerFilter: ModeAvailable, waiter: attacker);

                if (selected == null || !ReactionStillValid()) yield break;
                var before = rulesetOwner.GetRemainingPowerUses(powerArcaneJolt);
                if (selected == damagePower)
                {
                    rulesetOwner.UsePower(usablePower);
                    if (rulesetOwner.GetRemainingPowerUses(powerArcaneJolt) != before - 1) yield break;
                    MarkUsed(rulesetOwner);
                    actualEffectForms.Add(BuildArcaneJoltDamageForm(rulesetOwner));
                    yield break;
                }

                var healingUsable = PowerProvider.Get(healingPower, rulesetOwner);
                var parameters = new CharacterActionParams(owner, Id.SpendPower)
                {
                    UsablePower = healingUsable,
                    RulesetEffect = ServiceRepository.GetService<IRulesetImplementationService>()
                        .InstantiateEffectPower(rulesetOwner, healingUsable, false)
                };
                try
                {
                    yield return owner.MyReactToSelectTarget(
                        ServiceRepository.GetService<IGameLocationCharacterService>().ValidCharacters.Where(HealingTargetValid),
                        attacker, "ArcaneJoltHealing", Gui.Localize("Reaction/&CustomReactionArcaneJoltHealingDescription"),
                        _ =>
                        {
                            if (rulesetOwner.GetRemainingPowerUses(powerArcaneJolt) == before - 1) MarkUsed(rulesetOwner);
                        }, battleManager: battleManager, effectDefinition: healingPower,
                        candidateValidator: HealingTargetValid, actionParams: parameters, isBeneficial: true);
                }
                finally
                {
                    if (!parameters.ReactionValidated) parameters.RulesetEffect.Terminate(false);
                }
            }
            finally
            {
                foreach (var usable in temporary) rulesetOwner.UsablePowers.Remove(usable);
            }
        }

        private void MarkUsed(RulesetCharacter owner)
        {
            owner.InflictCondition(conditionUsed.Name, DurationType.Round, 0, TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect, owner.Guid, owner.CurrentFaction.Name, 1, conditionUsed.Name, 0, 0, 0);
        }
    }

    private class TargetDefendingBlade : IRetargetCustomRestPower
    {
        public GameLocationCharacter GetTarget(RulesetCharacter character)
        {
            return GameLocationCharacter.GetFromActor(GetBladeDefender(character));
        }
    }
}
