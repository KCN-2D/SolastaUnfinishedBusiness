using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Properties;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Builders.Features.AutoPreparedSpellsGroupBuilder;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public sealed class RangerFeyWanderer : AbstractSubclass
{
    private const string Name = "RangerFeyWanderer";

    public RangerFeyWanderer()
    {
        //
        // LEVEL 03
        //

        // Fey Wanderer Magic

        var autoPreparedSpells = FeatureDefinitionAutoPreparedSpellsBuilder
            .Create($"AutoPreparedSpells{Name}")
            .SetGuiPresentation(Category.Feature)
            .SetAutoTag("Ranger")
            .SetSpellcastingClass(CharacterClassDefinitions.Ranger)
            .SetPreparedSpellGroups(
                BuildSpellGroup(2, CharmPerson),
                BuildSpellGroup(5, MistyStep),
                BuildSpellGroup(9, DispelMagic),
                BuildSpellGroup(13, DimensionDoor),
                BuildSpellGroup(17, SpellsContext.SteelWhirlwind))
            .AddToDB();

        // Dreadful Strikes

        var conditionDreadfulStrikes = ConditionDefinitionBuilder
            .Create($"Condition{Name}DreadfulStrikes")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialDuration()
            .SetSpecialInterruptions(ConditionInterruption.AnyBattleTurnEnd)
            .AddToDB();

        var additionalDamageDreadfulStrikes = FeatureDefinitionAdditionalDamageBuilder
            .Create($"AdditionalDamage{Name}DreadfulStrikes")
            .SetGuiPresentation(Category.Feature)
            .SetNotificationTag("DreadfulStrikes")
            .SetDamageDice(DieType.D4, 1)
            .SetSpecificDamageType(DamageTypePsychic)
            .SetTargetCondition(conditionDreadfulStrikes, AdditionalDamageTriggerCondition.TargetDoesNotHaveCondition)
            .SetRequiredProperty(RestrictedContextRequiredProperty.Weapon)
            .SetAttackModeOnly()
            .SetConditionOperations(new ConditionOperationDescription
            {
                ConditionDefinition = conditionDreadfulStrikes,
                Operation = ConditionOperationDescription.ConditionOperation.Add
            })
            .SetImpactParticleReference(FeatureDefinitionPowers.Power_HornOfBlasting)
            .AddToDB();

        additionalDamageDreadfulStrikes.AddCustomSubFeatures(
            new ModifyAdditionalDamageDreadfulStrikes(additionalDamageDreadfulStrikes));

        // Otherworldly Glamour

        var pointPoolOtherworldlyGlamour = FeatureDefinitionPointPoolBuilder
            .Create($"PointPool{Name}OtherworldlyGlamour")
            .SetGuiPresentationNoContent(true)
            .SetPool(HeroDefinitions.PointsPoolType.Skill, 1)
            .RestrictChoices(
                SkillDefinitions.Deception,
                SkillDefinitions.Performance,
                SkillDefinitions.Persuasion)
            .AddToDB();

        var abilityCheckAffinityOtherworldlyGlamour = FeatureDefinitionAbilityCheckAffinityBuilder
            .Create($"AbilityCheckAffinity{Name}OtherworldlyGlamour")
            .SetGuiPresentation($"FeatureSet{Name}OtherworldlyGlamour", Category.Feature, Gui.NoLocalization)
            .BuildAndSetAffinityGroups(CharacterAbilityCheckAffinity.Advantage, AttributeDefinitions.Charisma)
            .AddToDB();

        var featureSetOtherworldlyGlamour = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}OtherworldlyGlamour")
            .SetGuiPresentation(Category.Feature)
            .SetFeatureSet(pointPoolOtherworldlyGlamour, abilityCheckAffinityOtherworldlyGlamour)
            .AddToDB();

        //
        // LEVEL 07
        //

        // Beguiling Twist

        var powerBeguilingTwist = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}BeguilingTwist")
            .SetGuiPresentation(Category.Feature)
            .SetUsesFixed(ActivationTime.NoCost)
            .AddToDB();

        var powerBeguilingTwistCharmed = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Name}BeguilingTwistCharmed")
            .SetGuiPresentation(Category.Feature, hidden: true)
            .SetSharedPool(ActivationTime.NoCost, powerBeguilingTwist)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 24, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Wisdom, false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates, TurnOccurenceType.EndOfTurn, true)
                            .SetConditionForm(ConditionDefinitions.ConditionCharmed,
                                ConditionForm.ConditionOperation.Add)
                            .Build())
                    .SetCasterEffectParameters(FeatureDefinitionPowers.PowerSorcererManaPainterDrain)
                    .Build())
            .AddToDB();

        var powerBeguilingTwistFrightened = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Name}BeguilingTwistFrightened")
            .SetGuiPresentation(Category.Feature, hidden: true)
            .SetSharedPool(ActivationTime.NoCost, powerBeguilingTwist)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 24, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Wisdom, false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates, TurnOccurenceType.EndOfTurn, true)
                            .SetConditionForm(ConditionDefinitions.ConditionFrightened,
                                ConditionForm.ConditionOperation.Add)
                            .Build())
                    .SetCasterEffectParameters(FeatureDefinitionPowers.PowerSorcererManaPainterDrain)
                    .Build())
            .AddToDB();

        powerBeguilingTwist.AddCustomSubFeatures(
            ModifyPowerVisibility.Hidden,
            new CustomBehaviorBeguilingTwist(powerBeguilingTwist,
                powerBeguilingTwistCharmed, powerBeguilingTwistFrightened));

        PowerBundle.RegisterPowerBundle(powerBeguilingTwist, false,
            powerBeguilingTwistCharmed, powerBeguilingTwistFrightened);

        //
        // LEVEL 11
        //

        // Fey Reinforcements

        var powerFeyReinforcements = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}FeyReinforcements")
            .SetGuiPresentation(Category.Feature, ConjureFey)
            .SetUsesFixed(ActivationTime.Action, RechargeRate.LongRest)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(ConjureFey)
                    .SetDurationData(DurationType.Minute, 1)
                    .Build())
            .AddToDB();

        //
        // LEVEL 15
        //

        // Misty Wanderer

        var powerMistyWanderer = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}MistyWanderer")
            .SetGuiPresentation(Category.Feature, MistyStep)
            .SetUsesAbilityBonus(ActivationTime.BonusAction, RechargeRate.LongRest, AttributeDefinitions.Wisdom)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(MistyStep)
                    .InviteOptionalAlly()
                    .Build())
            .AddCustomSubFeatures(new CustomBehaviorMistyWanderer())
            .AddToDB();

        MistyStep.AddCustomSubFeatures(new CustomBehaviorMistyStep());

        //
        // MAIN
        //

        Subclass = CharacterSubclassDefinitionBuilder
            .Create(Name)
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite(Name, Resources.RangerFeyWanderer, 256))
            .AddFeaturesAtLevel(3,
                autoPreparedSpells, additionalDamageDreadfulStrikes, featureSetOtherworldlyGlamour)
            .AddFeaturesAtLevel(7,
                powerBeguilingTwist, powerBeguilingTwistCharmed, powerBeguilingTwistFrightened)
            .AddFeaturesAtLevel(11, powerFeyReinforcements)
            .AddFeaturesAtLevel(15, powerMistyWanderer)
            .AddToDB();
    }

    internal override CharacterClassDefinition Klass => CharacterClassDefinitions.Ranger;

    internal override CharacterSubclassDefinition Subclass { get; }

    internal override FeatureDefinitionSubclassChoice SubclassChoice =>
        FeatureDefinitionSubclassChoices.SubclassChoiceRangerArchetypes;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    private sealed class ModifyAdditionalDamageDreadfulStrikes(
        FeatureDefinitionAdditionalDamage additionalDamageDreadfulStrikes) : IModifyAdditionalDamage
    {
        public void ModifyAdditionalDamage(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            FeatureDefinitionAdditionalDamage featureDefinitionAdditionalDamage,
            List<EffectForm> actualEffectForms,
            ref DamageForm damageForm)
        {
            if (featureDefinitionAdditionalDamage != additionalDamageDreadfulStrikes)
            {
                return;
            }

            var classLevel = attacker.RulesetCharacter.GetClassLevel(CharacterClassDefinitions.Ranger);

            if (classLevel < 11)
            {
                return;
            }

            damageForm.DieType = DieType.D6;
        }
    }

    private sealed class CustomBehaviorBeguilingTwist(
        FeatureDefinitionPower powerBeguilingTwist,
        FeatureDefinitionPower powerCharmed,
        FeatureDefinitionPower powerFrightened)
        : IRollSavingThrowInitiated, ITryAlterOutcomeSavingThrow
    {
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
            if (HasCharmedOrFrightened(effectForms))
            {
                advantageTrends.Add(
                    new TrendInfo(1, FeatureSourceType.Power, powerBeguilingTwist.Name, powerBeguilingTwist));
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
            bool TriggerStillValid() =>
                (savingThrowData.SaveOutcome is RollOutcome.Success or RollOutcome.CriticalSuccess) &&
                HasCharmedOrFrightened(savingThrowData.EffectDescription.EffectForms) && helper.CanReact() &&
                helper.RulesetCharacter.CanUsePower(powerBeguilingTwist, considerHaving: true) &&
                helper.IsWithinRange(defender, 24) && helper.CanSeeTarget(defender);

            bool CandidateValid(GameLocationCharacter target) => TriggerStillValid() && target != defender &&
                ReactionRequestSelectTarget.IsCandidateValid(helper, target) &&
                TargetingContext.IsValidOffensiveTarget(helper, target, powerBeguilingTwist) &&
                helper.IsWithinRange(target, 24) && helper.CanSeeTarget(target) &&
                (CanApplyCondition(target, ConditionDefinitions.ConditionCharmed) ||
                 CanApplyCondition(target, ConditionDefinitions.ConditionFrightened));

            if (!TriggerStillValid())
            {
                yield break;
            }

            var candidates = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters
                .Where(CandidateValid).Distinct().OrderBy(target => target.Guid).ToArray() ?? [];
            if (candidates.Length == 0)
            {
                yield break;
            }

            GameLocationCharacter selected;
            if (TargetingContext.IsManualPartyActor(helper))
            {
                selected = null;
                yield return helper.MyReactToSelectTarget(candidates, helper, "AdditionalEffectTarget",
                    Gui.Format("Reaction/&CustomReactionAdditionalEffectTargetDescription", powerBeguilingTwist.FormatTitle()),
                    target => selected = target, battleManager: battleManager, effectDefinition: powerBeguilingTwist,
                    candidateValidator: CandidateValid, isOffensive: true);
            }
            else
            {
                // Automated reactions keep hostile targets and prefer the original source when it is still useful.
                selected = candidates.FirstOrDefault(target => target == attacker) ?? candidates[0];
            }

            if (selected == null || !CandidateValid(selected))
            {
                yield break;
            }

            var rulesetHelper = helper.RulesetCharacter;
            var usablePower = PowerProvider.Get(powerBeguilingTwist, rulesetHelper);

            if (!TargetingContext.IsManualPartyActor(helper))
            {
                var power = CanApplyCondition(selected, ConditionDefinitions.ConditionCharmed)
                    ? powerCharmed : powerFrightened;
                var condition = power == powerCharmed
                    ? ConditionDefinitions.ConditionCharmed : ConditionDefinitions.ConditionFrightened;
                yield return helper.MyReactToUsePower(ActionDefinitions.Id.PowerReaction,
                    PowerProvider.Get(power, rulesetHelper), [selected], helper, powerBeguilingTwist.Name,
                    battleManager: battleManager,
                    reactionStillValid: () => CandidateValid(selected) && CanApplyCondition(selected, condition));
                yield break;
            }

            // any reaction within a saving flow must use the yielder as waiter
            yield return helper.MyReactToSpendPowerBundle(
                usablePower,
                [selected],
                helper,
                powerBeguilingTwist.Name,
                reactionValidated: ReactionValidated,
                battleManager: battleManager,
                reactionStillValid: () => CandidateValid(selected));

            yield break;

            void ReactionValidated(ReactionRequestSpendBundlePower reactionRequest)
            {
                helper.SpendActionType(ActionDefinitions.ActionType.Reaction);
            }

            bool CanApplyCondition(GameLocationCharacter target, ConditionDefinition condition) =>
                !target.RulesetCharacter.IsImmuneToCondition(condition.Name, helper.Guid, out _);
        }

        private static bool HasCharmedOrFrightened(List<EffectForm> effectForms)
        {
            return effectForms.GetAppliedConditionDefinitions().Any(condition =>
                condition.IsSubtypeOf(ConditionCharmed) || condition.IsSubtypeOf(ConditionFrightened));
        }
    }

    private sealed class CustomBehaviorMistyStep : IModifyEffectDescription, IFilterTargetingCharacter
    {
        public bool EnforceFullSelection => false;

        public bool IsValid(CursorLocationSelectTarget __instance, GameLocationCharacter target)
        {
            if (target.RulesetCharacter == null)
            {
                return false;
            }

            var isValid =
                target.RulesetCharacter is not RulesetCharacterEffectProxy &&
                __instance.ActionParams.ActingCharacter.IsWithinRange(target, 1);

            if (!isValid)
            {
                __instance.actionModifier.FailureFlags.Add("Failure/&MustBeWithin5ft");
            }

            return isValid;
        }

        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == MistyStep &&
                   character.GetSubclassLevel(CharacterClassDefinitions.Ranger, Name) >= 15;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            effectDescription.inviteOptionalAlly = true;

            return effectDescription;
        }
    }

    private sealed class CustomBehaviorMistyWanderer : IPowerOrSpellInitiatedByMe, IFilterTargetingCharacter
    {
        public bool EnforceFullSelection => false;

        public bool IsValid(CursorLocationSelectTarget __instance, GameLocationCharacter target)
        {
            if (target.RulesetCharacter == null)
            {
                return false;
            }

            var isValid =
                target.RulesetCharacter is not RulesetCharacterEffectProxy &&
                __instance.ActionParams.ActingCharacter.IsWithinRange(target, 1);

            if (!isValid)
            {
                __instance.actionModifier.FailureFlags.Add("Failure/&MustBeWithin5ft");
            }

            return isValid;
        }

        public IEnumerator OnPowerOrSpellInitiatedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            action.ActingCharacter.UsedBonusSpell = true;

            yield break;
        }
    }
}
