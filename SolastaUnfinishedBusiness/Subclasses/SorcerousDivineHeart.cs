using System.Collections;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static FeatureDefinitionAttributeModifier;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionMoveModes;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public sealed class SorcerousDivineHeart : AbstractSubclass
{
    private const string Name = "DivineHeart";
    private const string OriginTag = "Origin";
    private const int FavoredByTheGodsMaxBonus = 8;

    private static FeatureDefinitionAttributeModifier AttributeModifierDivineHeartDivineFortitude;
    private static FeatureDefinitionFeatureSet FeatureSetDivineHeartOtherworldlyWings;
    private static FeatureDefinitionPower PowerDivineHeartFavoredByTheGods;
    private static FeatureDefinitionPower PowerDivineHeartPlanarPortal;
    private static CharacterSubclassDefinition SubclassDefinition;

    public SorcerousDivineHeart()
    {
        AttributeModifierDivineHeartDivineFortitude = BuildDivineFortitude();
        PowerDivineHeartFavoredByTheGods = BuildFavoredByTheGods();
        PowerDivineHeartPlanarPortal = BuildPlanarPortal();
        FeatureSetDivineHeartOtherworldlyWings = BuildOtherworldlyWings();

        SubclassDefinition = CharacterSubclassDefinitionBuilder
            .Create($"Sorcerous{Name}")
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite(Name, Resources.SorcererDivineHeart, 256))
            .AddFeaturesAtLevel(1,
                BuildDeityChoice(),
                BuildClericSpellsList())
            .AddFeaturesAtLevel(6,
                BuildEmpoweredHealing(),
                BuildDivineFount())
            .AddFeaturesAtLevel(18,
                BuildDivineRecovery())
            .AddToDB();

        SwitchDivineHeartTabletopFeatures();
    }

    internal override CharacterClassDefinition Klass => CharacterClassDefinitions.Sorcerer;

    internal override CharacterSubclassDefinition Subclass => SubclassDefinition;

    internal override FeatureDefinitionSubclassChoice SubclassChoice =>
        FeatureDefinitionSubclassChoices.SubclassChoiceSorcerousOrigin;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    internal static void SwitchDivineHeartTabletopFeatures()
    {
        if (SubclassDefinition == null)
        {
            return;
        }

        SubclassDefinition.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == AttributeModifierDivineHeartDivineFortitude ||
            x.FeatureDefinition == PowerDivineHeartFavoredByTheGods ||
            x.FeatureDefinition == PowerDivineHeartPlanarPortal ||
            x.FeatureDefinition == FeatureSetDivineHeartOtherworldlyWings);

        var initialOriginFeatureLevel = Main.Settings.EnableSorcererOrigin2024 ? 3 : 1;

        if (Main.Settings.EnableDivineHeartTabletopFeatures)
        {
            SubclassDefinition.FeatureUnlocks.AddRange(
            [
                new FeatureUnlockByLevel(PowerDivineHeartFavoredByTheGods, initialOriginFeatureLevel),
                new FeatureUnlockByLevel(FeatureSetDivineHeartOtherworldlyWings, 14)
            ]);
        }
        else
        {
            SubclassDefinition.FeatureUnlocks.AddRange(
            [
                new FeatureUnlockByLevel(AttributeModifierDivineHeartDivineFortitude, initialOriginFeatureLevel),
                new FeatureUnlockByLevel(PowerDivineHeartPlanarPortal, 14)
            ]);
        }

        SubclassDefinition.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private static FeatureDefinitionFeatureSet BuildDeityChoice()
    {
        return FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}DeityChoice")
            .SetGuiPresentation(Category.Feature)
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
            .AddFeatureSet(
                BuildDeityAutoPreparedSpells("Arun", ProtectionFromEvilGood),
                BuildDeityAutoPreparedSpells("Einar", InflictWounds),
                BuildDeityAutoPreparedSpells("Mariake", CureWounds),
                BuildDeityAutoPreparedSpells("Misaye", Bane),
                BuildDeityAutoPreparedSpells("Pakri", Bless))
            .AddToDB();
    }

    private static FeatureDefinitionAutoPreparedSpells BuildDeityAutoPreparedSpells(
        string deity,
        SpellDefinition spellDefinition)
    {
        return FeatureDefinitionAutoPreparedSpellsBuilder
            .Create($"AutoPreparedSpells{Name}{deity}")
            .SetGuiPresentation(Category.Feature)
            .SetAutoTag(OriginTag)
            .SetSpellcastingClass(CharacterClassDefinitions.Sorcerer)
            .AddPreparedSpellGroup(1, spellDefinition)
            .AddToDB();
    }

    private static FeatureDefinitionAttributeModifier BuildDivineFortitude()
    {
        return FeatureDefinitionAttributeModifierBuilder
            .Create($"AttributeModifier{Name}DivineFortitude")
            .SetGuiPresentation(Category.Feature)
            .SetModifier(AttributeModifierOperation.Additive, AttributeDefinitions.HitPointBonusPerLevel, 1)
            .AddToDB();
    }

    private static FeatureDefinitionPower BuildFavoredByTheGods()
    {
        var power = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}FavoredByTheGods")
            .SetGuiPresentation(Category.Feature, Guidance)
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.ShortRest)
            .SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();

        power.AddCustomSubFeatures(new CustomBehaviorFavoredByTheGods(power));

        return power;
    }

    private static FeatureDefinitionMagicAffinity BuildClericSpellsList()
    {
        return FeatureDefinitionMagicAffinityBuilder
            .Create($"MagicAffinity{Name}ClericSpellsList")
            .SetGuiPresentation(Category.Feature)
            .SetExtendedSpellList(SpellListDefinitions.SpellListCleric)
            .AddToDB();
    }

    private static FeatureDefinitionPower BuildEmpoweredHealing()
    {
        var dieRollModifierEmpoweredHealing = FeatureDefinitionDieRollModifierBuilder
            .Create($"DieRollModifier{Name}EmpoweredHealing")
            .SetGuiPresentationNoContent(true)
            .SetModifiers(RollContext.HealValueRoll, 1, 0, 2, "Feedback/&DivineHeartEmpoweredHealingReroll")
            .AddToDB();

        var conditionDivineHeartEmpoweredHealing = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionSorcererChildRiftDeflection, $"Condition{Name}EmpoweredHealing")
            .SetOrUpdateGuiPresentation(Category.Condition)
            .SetFeatures(dieRollModifierEmpoweredHealing)
            .AddToDB();

        return FeatureDefinitionPowerBuilder
            .Create($"Power{Name}EmpoweredHealing")
            .SetGuiPresentation(Category.Feature, HealingWord)
            .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.SorceryPoints, 1, 0)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(
                                conditionDivineHeartEmpoweredHealing,
                                ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddToDB();
    }

    private static FeatureDefinitionPower BuildDivineFount()
    {
        return FeatureDefinitionPowerBuilder
            .Create($"Power{Name}DivineFount")
            .SetGuiPresentation(Category.Feature, BeaconOfHope)
            .SetUsesAbilityBonus(
                ActivationTime.BonusAction, RechargeRate.LongRest, AttributeDefinitions.Charisma)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(PowerSorcererManaPainterDrain.EffectDescription.EffectForms[1])
                    .Build())
            .AddToDB();
    }

    private static FeatureDefinitionPower BuildPlanarPortal()
    {
        return FeatureDefinitionPowerBuilder
            .Create($"Power{Name}PlanarPortal")
            .SetGuiPresentation(Category.Feature, DimensionDoor)
            .SetUsesFixed(ActivationTime.Action)
            .SetEffectDescription(DimensionDoor.EffectDescription)
            .AddToDB();
    }

    private static FeatureDefinitionFeatureSet BuildOtherworldlyWings()
    {
        var conditionOtherworldlyWings = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionFlyingAdaptive, $"Condition{Name}OtherworldlyWings")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionFlyingAdaptive)
            .SetPossessive()
            .SetParentCondition(ConditionDefinitions.ConditionFlying)
            .SetFeatures(MoveModeFly6)
            .AddToDB();

        var powerOtherworldlyWings = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}OtherworldlyWings")
            .SetGuiPresentation(Category.Feature,
                Sprites.GetSprite("FlightSprout", Resources.PowerAngelicFormSprout, 256, 128))
            .SetUsesFixed(ActivationTime.BonusAction)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Permanent)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(
                                conditionOtherworldlyWings,
                                ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(new ValidatorsValidatePowerUse(
                ValidatorsCharacter.HasNoneOfConditions(ConditionFlyingAdaptive, conditionOtherworldlyWings.Name)))
            .AddToDB();

        var powerDismissOtherworldlyWings = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}DismissOtherworldlyWings")
            .SetGuiPresentation(Category.Feature,
                Sprites.GetSprite("FlightDismiss", Resources.PowerAngelicFormDismiss, 256, 128))
            .SetUsesFixed(ActivationTime.BonusAction)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(
                                conditionOtherworldlyWings,
                                ConditionForm.ConditionOperation.Remove)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(new ValidatorsValidatePowerUse(
                ValidatorsCharacter.HasAnyOfConditions(ConditionFlyingAdaptive, conditionOtherworldlyWings.Name)))
            .AddToDB();

        return FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}OtherworldlyWings")
            .SetGuiPresentation($"Power{Name}OtherworldlyWings", Category.Feature)
            .AddFeatureSet(powerOtherworldlyWings, powerDismissOtherworldlyWings)
            .AddToDB();
    }

    private static FeatureDefinitionPower BuildDivineRecovery()
    {
        return FeatureDefinitionPowerBuilder
            .Create($"Power{Name}DivineRecovery")
            .SetGuiPresentation(Category.Feature, Heal)
            .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.LongRest)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(Heal.EffectDescription)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .Build())
            .AddToDB();
    }

    private sealed class CustomBehaviorFavoredByTheGods(FeatureDefinitionPower power)
        : ITryAlterOutcomeAttack, ITryAlterOutcomeSavingThrow
    {
        public int HandlerPriority => -10;

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
            var rulesetHelper = helper.RulesetCharacter;
            var usablePower = PowerProvider.Get(power, rulesetHelper);

            if (helper != attacker ||
                action.AttackRollOutcome != RollOutcome.Failure ||
                action.AttackSuccessDelta + FavoredByTheGodsMaxBonus < 0 ||
                rulesetHelper.GetRemainingUsesOfPower(usablePower) <= 0)
            {
                yield break;
            }

            // any reaction within an attack flow must use the attacker as waiter
            yield return helper.MyReactToSpendPower(
                usablePower,
                attacker,
                "DivineHeartFavoredByTheGodsAttack",
                "SpendPowerDivineHeartFavoredByTheGodsAttackDescription"
                    .Formatted(Category.Reaction, defender.Name),
                ReactionValidated,
                battleManager);

            yield break;

            void ReactionValidated()
            {
                usablePower.Consume();

                var dieRoll = RollTwoD4(rulesetHelper);

                attackModifier.AttacktoHitTrends.Add(
                    new TrendInfo(dieRoll, FeatureSourceType.Power, power.Name, power)
                    {
                        dieType = DieType.D4, dieFlag = TrendInfoDieFlag.None
                    });

                action.AttackSuccessDelta += dieRoll;
                attackModifier.AttackRollModifier += dieRoll;
                action.AttackRollOutcome = action.AttackSuccessDelta >= 0 ? RollOutcome.Success : RollOutcome.Failure;

                rulesetHelper.LogCharacterActivatesAbility(
                    power.GuiPresentation.Title,
                    "Feedback/&DivineHeartFavoredByTheGodsAttackRoll",
                    tooltipContent: power.Name,
                    tooltipClass: "PowerDefinition",
                    extra:
                    [
                        (ConsoleStyleDuplet.ParameterType.AbilityInfo, "2d4"),
                        (action.AttackSuccessDelta >= 0
                            ? ConsoleStyleDuplet.ParameterType.Positive
                            : ConsoleStyleDuplet.ParameterType.Negative, dieRoll.ToString())
                    ]);
            }
        }

        public IEnumerator OnTryAlterOutcomeSavingThrow(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            SavingThrowData savingThrowData,
            bool hasHitVisual)
        {
            var rulesetHelper = helper.RulesetCharacter;
            var usablePower = PowerProvider.Get(power, rulesetHelper);

            if (helper != defender ||
                !savingThrowData.IsFailedSavingThrowOutcome() ||
                savingThrowData.SaveOutcomeDelta + FavoredByTheGodsMaxBonus < 0 ||
                rulesetHelper.GetRemainingUsesOfPower(usablePower) <= 0)
            {
                yield break;
            }

            // any reaction within a saving flow must use the yielder as waiter
            yield return helper.MyReactToSpendPower(
                usablePower,
                helper,
                "DivineHeartFavoredByTheGodsSaving",
                "SpendPowerDivineHeartFavoredByTheGodsSavingDescription"
                    .Formatted(Category.Reaction, defender.Name,
                        attacker?.Name ?? ReactionRequestCustom.EnvTitle, savingThrowData.Title),
                ReactionValidated,
                battleManager);

            yield break;

            void ReactionValidated()
            {
                usablePower.Consume();

                var dieRoll = RollTwoD4(rulesetHelper);

                savingThrowData.SaveOutcomeDelta += dieRoll;
                savingThrowData.SaveOutcome = savingThrowData.SaveOutcomeDelta >= 0
                    ? RollOutcome.Success
                    : RollOutcome.Failure;

                var saveActionModifier = savingThrowData.SaveActionModifier;

                if (saveActionModifier != null)
                {
                    saveActionModifier.SavingThrowModifierTrends.Add(
                        new TrendInfo(dieRoll, FeatureSourceType.Power, power.Name, power)
                        {
                            dieType = DieType.D4, dieFlag = TrendInfoDieFlag.None
                        });

                    saveActionModifier.SavingThrowModifier += dieRoll;
                }

                rulesetHelper.LogCharacterActivatesAbility(
                    power.GuiPresentation.Title,
                    "Feedback/&DivineHeartFavoredByTheGodsSavingRoll",
                    tooltipContent: power.Name,
                    tooltipClass: "PowerDefinition",
                    extra:
                    [
                        (ConsoleStyleDuplet.ParameterType.AbilityInfo, "2d4"),
                        (savingThrowData.SaveOutcomeDelta >= 0
                            ? ConsoleStyleDuplet.ParameterType.Positive
                            : ConsoleStyleDuplet.ParameterType.Negative, dieRoll.ToString())
                    ]);
            }
        }

        private static int RollTwoD4(RulesetCharacter rulesetCharacter)
        {
            return rulesetCharacter.RollDie(DieType.D4, RollContext.None, false, AdvantageType.None, out _, out _) +
                   rulesetCharacter.RollDie(DieType.D4, RollContext.None, false, AdvantageType.None, out _, out _);
        }
    }
}
