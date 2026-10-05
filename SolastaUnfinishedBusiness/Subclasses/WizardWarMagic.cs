using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Properties;
using static ActionDefinitions;
using static FeatureDefinitionAttributeModifier;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public sealed class WizardWarMagic : AbstractSubclass
{
    private const string Name = "WarMagic";

    public WizardWarMagic()
    {
        // LEVEL 02

        // Arcane Deflection

        var featureArcaneDeflection = FeatureDefinitionBuilder
            .Create($"Feature{Name}ArcaneDeflection")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

        var conditionArcaneDeflectionArmor = ConditionDefinitionBuilder
            .Create($"Condition{Name}ArcaneDeflectionArmor")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialInterruptions(ExtraConditionInterruption.AfterWasAttacked)
            .SetFeatures(FeatureDefinitionAttributeModifierBuilder
                .Create($"AttributeModifier{Name}ArcaneDeflectionArmor")
                .SetGuiPresentationNoContent(true)
                .SetModifier(AttributeModifierOperation.Additive, AttributeDefinitions.ArmorClass, 2)
                .AddToDB())
            .AddToDB();

        var conditionArcaneDeflectionRestriction = ConditionDefinitionBuilder
            .Create($"Condition{Name}ArcaneDeflectionRestriction")
            .SetGuiPresentation(Category.Condition,
                Sprites.GetSprite("ConditionArcaneDeflectionCantripsOnly", Resources.ConditionArcaneDeflectionCantripsOnly, 128))
            .SetConditionType(ConditionType.Detrimental)
            .SetSpecialDuration()
            .AddToDB();

        conditionArcaneDeflectionRestriction.AddCustomSubFeatures(
            new SpellCastingLevelRestriction(0, "Failure/&FailureFlagWarMagicArcaneDeflectionCantripsOnly"));

        // Tactical Wit

        var attributeModifierTacticalWit = FeatureDefinitionAttributeModifierBuilder
            .Create($"AttributeModifier{Name}TacticalWit")
            .SetGuiPresentation(Category.Feature)
            .SetModifierAbilityScore(AttributeDefinitions.Initiative, AttributeDefinitions.Intelligence)
            .AddToDB();

        // LEVEL 06

        // Power Surge

        var conditionSurgeMark = ConditionDefinitionBuilder
            .Create($"Condition{Name}SurgeMark")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialInterruptions(
                ConditionInterruption.Attacks,
                ConditionInterruption.CastSpellExecuted,
                (ConditionInterruption)ExtraConditionInterruption.SpendPowerExecuted,
                ConditionInterruption.UsePowerExecuted)
            .AddToDB();

        var powerSurge = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}Surge")
            .SetGuiPresentation(Category.Feature)
            .SetUsesAbilityBonus(ActivationTime.NoCost, RechargeRate.LongRest, AttributeDefinitions.Intelligence)
            .DelegatedToAction()
            .AddToDB();

        _ = ActionDefinitionBuilder
            .Create(DatabaseHelper.ActionDefinitions.MetamagicToggle, "PowerSurgeToggle")
            .SetOrUpdateGuiPresentation(powerSurge.Name, Category.Feature)
            .RequiresAuthorization()
            .SetActionId(ExtraActionId.PowerSurgeToggle)
            .SetActivatedPower(powerSurge)
            .OverrideClassName("Toggle")
            .AddToDB();

        powerSurge.AddCustomSubFeatures(new CustomBehaviorPowerSurge(powerSurge, conditionSurgeMark));

        var actionAffinityPowerSurgeToggle = FeatureDefinitionActionAffinityBuilder
            .Create(FeatureDefinitionActionAffinitys.ActionAffinitySorcererMetamagicToggle,
                "ActionAffinityPowerSurgeToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((Id)ExtraActionId.PowerSurgeToggle)
            .AddToDB();

        var featureSetPowerSurge = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}PowerSurge")
            .SetGuiPresentation($"Power{Name}Surge", Category.Feature)
            .SetFeatureSet(powerSurge, actionAffinityPowerSurgeToggle)
            .AddToDB();

        // LEVEL 10

        // Durable Magic

        var featureDurableMagic = FeatureDefinitionAttributeModifierBuilder
            .Create($"Feature{Name}DurableMagic")
            .SetGuiPresentation(Category.Feature)
            .SetModifier(AttributeModifierOperation.Additive, AttributeDefinitions.ArmorClass, 2)
            .SetSituationalContext(ExtraSituationalContext.IsConcentratingOnSpell)
            .AddToDB();

        featureDurableMagic.AddCustomSubFeatures(new CustomBehaviorDurableMagic(featureDurableMagic));

        // LEVEL 14

        // Deflecting Shroud

        var powerDeflectionShroud = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}DeflectionShroud")
            .SetGuiPresentation(Category.Feature)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 12, TargetType.IndividualsUnique, 3)
                    .SetEffectForms(EffectFormBuilder.DamageForm(DamageTypeForce))
                    .SetImpactEffectParameters(SpellDefinitions.ArcaneSword)
                    .Build())
            .AddToDB();

        powerDeflectionShroud.AddCustomSubFeatures(
            ModifyPowerVisibility.Hidden,
            new UpgradeEffectDamageBonusBasedOnClassLevel(
                powerDeflectionShroud, CharacterClassDefinitions.Wizard, 0.5));
        featureArcaneDeflection.AddCustomSubFeatures(
            new CustomBehaviorArcaneDeflection(featureArcaneDeflection, powerDeflectionShroud,
                conditionArcaneDeflectionArmor, conditionArcaneDeflectionRestriction));

        Subclass = CharacterSubclassDefinitionBuilder
            .Create($"Wizard{Name}")
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite(Name, Resources.WizardWarMagic, 256))
            .AddFeaturesAtLevel(2, featureArcaneDeflection, attributeModifierTacticalWit)
            .AddFeaturesAtLevel(6, featureSetPowerSurge)
            .AddFeaturesAtLevel(10, featureDurableMagic)
            .AddFeaturesAtLevel(14, powerDeflectionShroud)
            .AddToDB();
    }

    internal override CharacterClassDefinition Klass => CharacterClassDefinitions.Wizard;

    internal override CharacterSubclassDefinition Subclass { get; }

    internal override FeatureDefinitionSubclassChoice SubclassChoice =>
        FeatureDefinitionSubclassChoices.SubclassChoiceWizardArcaneTraditions;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    private sealed class CustomBehaviorArcaneDeflection(
        FeatureDefinition featureArcaneDeflection,
        FeatureDefinitionPower powerDeflectionShroud,
        ConditionDefinition conditionArcaneDeflectionArmor,
        ConditionDefinition conditionArcaneDeflectionRestriction) : ITryAlterOutcomeAttack, ITryAlterOutcomeSavingThrow
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
            const int bonus = 2;

            if (action.AttackRollOutcome != RollOutcome.Success ||
                action.AttackSuccessDelta - bonus >= 0 ||
                helper != defender ||
                !defender.CanReact() ||
                !defender.CanPerceiveTarget(attacker))
            {
                yield break;
            }

            // any reaction within an attack flow must use the attacker as waiter
            yield return helper.MyReactToDoNothing(
                ExtraActionId.DoNothingReaction,
                attacker,
                "ArcaneDeflectionAttack",
                "CustomReactionArcaneDeflectionAttackDescription".Localized(Category.Reaction),
                ReactionValidated,
                battleManager: battleManager,
                target: helper, effectDefinition: featureArcaneDeflection);

            yield break;

            void ReactionValidated()
            {
                EffectHelpers.StartVisualEffect(
                    helper, helper, SpellDefinitions.Shield, EffectHelpers.EffectType.QuickCaster);

                helper.RulesetCharacter.InflictCondition(
                    conditionArcaneDeflectionArmor.Name, DurationType.Round, 0, TurnOccurenceType.EndOfTurn,
                    AttributeDefinitions.TagEffect, helper.Guid, helper.RulesetCharacter.CurrentFaction.Name,
                    1, featureArcaneDeflection.Name, 0, 0, 0);
                action.AttackSuccessDelta -= bonus;
                action.AttackRollOutcome = RollOutcome.Failure;
                helper.RulesetCharacter.LogCharacterUsedFeature(
                    featureArcaneDeflection,
                    "Feedback/&ArcaneDeflectionAttackRoll",
                    extra:
                    [
                        (ConsoleStyleDuplet.ParameterType.Positive, bonus.ToString()),
                        (ConsoleStyleDuplet.ParameterType.Negative, "Feedback/&RollAttackFailureTitle")
                    ]);

                ApplySpellRestriction(helper);
                HandleDeflectionShroud(helper);
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
            const int bonus = 4;

            if (!savingThrowData.IsFailedSavingThrowOutcome() ||
                savingThrowData.SaveOutcomeDelta + bonus < 0 ||
                helper != defender ||
                !helper.CanReact() ||
                (attacker != null && !helper.CanPerceiveTarget(attacker)))
            {
                yield break;
            }

            // any reaction within a saving flow must use the yielder as waiter
            yield return helper.MyReactToDoNothing(
                ExtraActionId.DoNothingReaction,
                helper,
                "ArcaneDeflectionSaving",
                "CustomReactionArcaneDeflectionSavingDescription".Formatted(
                    Category.Reaction, attacker?.Name ?? ReactionRequestCustom.EnvTitle, savingThrowData.Title),
                ReactionValidated,
                battleManager: battleManager,
                target: helper, effectDefinition: featureArcaneDeflection);

            yield break;

            void ReactionValidated()
            {
                EffectHelpers.StartVisualEffect(
                    helper, helper, SpellDefinitions.Shield, EffectHelpers.EffectType.QuickCaster);

                savingThrowData.SaveOutcomeDelta += bonus;
                savingThrowData.SaveOutcome = RollOutcome.Success;

                helper.RulesetCharacter.LogCharacterUsedFeature(
                    featureArcaneDeflection,
                    "Feedback/&ArcaneDeflectionSavingRoll",
                    extra:
                    [
                        (ConsoleStyleDuplet.ParameterType.Positive, bonus.ToString()),
                        (ConsoleStyleDuplet.ParameterType.Positive, "Feedback/&RollCheckSuccessTitle")
                    ]);

                ApplySpellRestriction(helper);
                HandleDeflectionShroud(helper);
            }
        }

        private void ApplySpellRestriction(GameLocationCharacter helper)
        {
            // A reaction on one's own turn must also survive that turn's end.
            var duration = Gui.Battle?.ActiveContender == helper ? 1 : 0;
            var rulesetHelper = helper.RulesetCharacter;

            rulesetHelper.InflictCondition(
                conditionArcaneDeflectionRestriction.Name, DurationType.Round, duration,
                TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect, helper.Guid,
                rulesetHelper.CurrentFaction.Name, 1, featureArcaneDeflection.Name, 0, 0, 0);
        }

        private void HandleDeflectionShroud(GameLocationCharacter helper)
        {
            var rulesetHelper = helper.RulesetCharacter;
            var classLevel = rulesetHelper.GetClassLevel(CharacterClassDefinitions.Wizard);

            if (classLevel < 14 || Gui.Battle == null)
            {
                return;
            }

            var usablePower = PowerProvider.Get(powerDeflectionShroud, rulesetHelper);
            var targets = Gui.Battle
                .GetContenders(helper, withinRange: 12)
                .OrderBy(x => DistanceCalculation.GetDistanceFromCharacters(helper, x))
                .Take(3)
                .ToArray();

            // deflection shroud is a use at will power
            helper.MyExecuteActionSpendPower(usablePower, targets);
        }
    }

    private sealed class CustomBehaviorPowerSurge(
        FeatureDefinitionPower powerSurge,
        ConditionDefinition conditionSurgeMark) : IMagicEffectFinishedByMe, IMagicEffectBeforeHitConfirmedOnEnemy
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
            if (rulesetEffect is not RulesetEffectSpell spellEffect ||
                !attacker.RulesetCharacter.IsSpellCastAsClassOrSubclassSpell(
                    spellEffect.SpellRepertoire, RulesetEffectSpellWithOrigin.GetOriginSpell(spellEffect),
                    CharacterClassDefinitions.Wizard, false)
                || defender.RulesetActor is not RulesetCharacter
                || !defender.IsOppositeSide(attacker.Side))
            {
                yield break;
            }

            HandlePowerSurge(attacker, defender, rulesetEffect, actualEffectForms);
        }

        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (action is not CharacterActionCastSpell actionCastSpell ||
                RulesetEffectSpellWithOrigin.GetOriginSpell(actionCastSpell.ActiveSpell) != SpellDefinitions.Counterspell ||
                !SpellInterruptionContext.HasCounteredSpell(action))
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;
            var usablePower = PowerProvider.Get(powerSurge, rulesetAttacker);

            usablePower.RepayUse();
        }

        private void HandlePowerSurge(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetEffect rulesetEffect,
            List<EffectForm> actualEffectForms)
        {
            var damageForm =
                actualEffectForms.FirstOrDefault(x => x.FormType == EffectForm.EffectFormType.Damage);

            if (damageForm == null)
            {
                return;
            }

            var rulesetAttacker = attacker.RulesetCharacter;
            var usablePower = PowerProvider.Get(powerSurge, rulesetAttacker);
            if (!IsAvailable())
            {
                return;
            }

            // Payment and target choice depend on the spell's actual received
            // damage, including damage remaining after a successful saving throw.
            OnHitEffectContext.TryQueueEffect(
                rulesetEffect, attacker, defender, powerSurge, IsAvailable,
                _ => CreateEffects(), AdditionalEffectTrigger.Damage, Consume,
                confirmReaction: true);

            return;

            bool IsAvailable()
            {
                return rulesetAttacker is { IsDeadOrDyingOrUnconscious: false } &&
                       attacker.OncePerTurnIsValid(powerSurge.Name) &&
                       rulesetAttacker.IsToggleEnabled((Id)ExtraActionId.PowerSurgeToggle) &&
                       rulesetAttacker.GetRemainingUsesOfPower(usablePower) > 0 &&
                       !rulesetAttacker.HasConditionOfCategoryAndType(
                           AttributeDefinitions.TagEffect, conditionSurgeMark.Name);
            }

            void Consume()
            {
                attacker.UsedSpecialFeatures.TryAdd(powerSurge.Name, 0);
                rulesetAttacker.UsePower(usablePower);
                rulesetAttacker.InflictCondition(
                    conditionSurgeMark.Name,
                    DurationType.Round,
                    0,
                    TurnOccurenceType.EndOfSourceTurn,
                    AttributeDefinitions.TagEffect,
                    rulesetAttacker.guid,
                    rulesetAttacker.CurrentFaction.Name,
                    1,
                    conditionSurgeMark.Name,
                    0,
                    0,
                    0);

            }

            List<EffectForm> CreateEffects()
            {
                var classLevel = rulesetAttacker.GetClassLevel(CharacterClassDefinitions.Wizard);
                var effectForm = EffectFormBuilder.DamageForm(DamageTypeForce, bonusDamage: classLevel / 2);

                effectForm.DamageForm.IgnoreSpellAdvancementDamageDice = true;

                return [effectForm];
            }
        }
    }

    private sealed class CustomBehaviorDurableMagic(
        FeatureDefinition featureDurableMagic) :
        IRollSavingThrowInitiated, IRollSavingCheckInitiated, IConditionalSavingThrowBonusProvider
    {
        public BaseDefinition SourceDefinition => featureDurableMagic;

        public int GetSavingThrowBonus(RulesetCharacter character, string abilityScoreName)
        {
            return character?.ConcentratedSpell != null ? 2 : 0;
        }

        public void OnRollSavingCheckInitiated(
            RulesetCharacter defender,
            int saveDC,
            string damageType,
            ref ActionModifier actionModifier,
            ref int modifier)
        {
            var bonus = GetSavingThrowBonus(defender, AttributeDefinitions.Constitution);

            if (bonus == 0)
            {
                return;
            }

            modifier += bonus;
            actionModifier.SavingThrowModifierTrends.Add(
                new TrendInfo(bonus, FeatureSourceType.CharacterFeature, featureDurableMagic.Name, featureDurableMagic));
        }

        public void OnSavingThrowInitiated(
            RulesetActor rulesetActorCaster,
            RulesetActor rulesetActorDefender,
            ref int saveBonus,
            ref string abilityScoreName,
            BaseDefinition sourceDefinition,
            List<TrendInfo> modifierTrends,
            List<TrendInfo> advantageTrends,
            ref int rollModifier,
            ref int saveDC,
            ref bool hasHitVisual,
            RollOutcome outcome,
            int outcomeDelta,
            List<EffectForm> effectForms)
        {
            var bonus = GetSavingThrowBonus(rulesetActorDefender as RulesetCharacter, abilityScoreName);

            if (bonus == 0)
            {
                return;
            }

            rollModifier += bonus;
            modifierTrends.Add(
                new TrendInfo(bonus, FeatureSourceType.CharacterFeature, featureDurableMagic.Name, featureDurableMagic));
        }
    }
}
