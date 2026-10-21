using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Subclasses;
using SolastaUnfinishedBusiness.Validators;
using TA;
using UnityEngine.AddressableAssets;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionDieRollModifiers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionActionAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionMovementAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionProficiencys;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly ModifyAttunementLimit ThiefAttunementLimit =
        new(0, 4, () => Main.Settings.EnableThiefUseMagicDevice2024);

    internal static void SwitchThiefUseMagicDevice()
    {
        var feature = ThiefUseMagicDevice2024Context.Feature;

        if (!feature.GetAllSubFeaturesOfType<ModifyAttunementLimit>().Contains(ThiefAttunementLimit))
        {
            feature.AddCustomSubFeatures(ThiefAttunementLimit);
        }

        feature.ignoreClassRestrictionsOnMagicalItems = !Main.Settings.EnableThiefUseMagicDevice2024;
        feature.GuiPresentation.description = Main.Settings.EnableThiefUseMagicDevice2024
            ? "Feature/&MagicAffinityUseMagicalItem2024Description"
            : "Feature/&MagicAffinityUseMagicalItemDescription";
    }

    internal static void SwitchRogueStrokeOfLuck()
    {
        var feature = DatabaseRepository.GetDatabase<FeatureDefinitionPower>()
            .GetElement("PowerRogueStrokeOfLuck");

        feature.GuiPresentation.description = Main.Settings.EnableRogueStrokeOfLuck2024
            ? "Feature/&PowerRogueStrokeOfLuck2024Description"
            : "Feature/&PowerRogueStrokeOfLuckDescription";
    }

    private const string FeatSteadyAim = "FeatSteadyAim";

    private static readonly FeatureDefinitionPower PowerFeatSteadyAim = FeatureDefinitionPowerBuilder
        .Create($"Power{FeatSteadyAim}")
        .SetGuiPresentation(Category.Feature, Sprites.GetSprite(FeatSteadyAim, Resources.PowerSteadyAim, 256, 128))
        .SetUsesFixed(ActivationTime.BonusAction)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Round)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetEffectForms(
                    EffectFormBuilder
                        .Create()
                        .SetConditionForm(
                            ConditionDefinitionBuilder
                                .Create($"Condition{FeatSteadyAim}Advantage")
                                .SetGuiPresentation(Category.Condition,
                                    ConditionGuided)
                                .SetPossessive()
                                .SetSilent(Silent.WhenAddedOrRemoved)
                                .SetSpecialInterruptions(ConditionInterruption.Attacks)
                                .AddFeatures(
                                    FeatureDefinitionCombatAffinityBuilder
                                        .Create($"CombatAffinity{FeatSteadyAim}")
                                        .SetGuiPresentation($"Power{FeatSteadyAim}", Category.Feature,
                                            Gui.NoLocalization)
                                        .SetMyAttackAdvantage(AdvantageType.Advantage)
                                        .AddToDB())
                                .AddToDB(),
                            ConditionForm.ConditionOperation.Add)
                        .Build(),
                    EffectFormBuilder
                        .Create()
                        .SetConditionForm(
                            ConditionDefinitionBuilder
                                .Create($"Condition{FeatSteadyAim}Restrained")
                                .SetGuiPresentation(Category.Condition)
                                .SetConditionType(ConditionType.Detrimental)
                                .SetSilent(Silent.WhenAddedOrRemoved)
                                .AddFeatures(MovementAffinityConditionRestrained)
                                .AddToDB(),
                            ConditionForm.ConditionOperation.Add)
                        .Build())
                .SetParticleEffectParameters(PowerFunctionWandFearCommand)
                .SetImpactEffectParameters(new AssetReference())
                .Build())
        .AddCustomSubFeatures(
            new ValidatorsValidatePowerUse(c => GameLocationCharacter.GetFromActor(c) is { UsedTacticalMoves: 0 }))
        .AddToDB();

    private static CharacterFeatureReplacement _assassinAssassinateReplacement;
    private static CharacterFeatureReplacement _assassinInfiltrationExpertiseReplacement;
    private static FeatureDefinitionFeatureSet _assassinInfiltrationExpertise2024;

    private static void LoadAssassinFeatures()
    {
        var assassin = GetDefinition<CharacterSubclassDefinition>(RoguishSlayer.Name);
        var masterfulMimicry = FeatureDefinitionProficiencyBuilder
            .Create($"Proficiency{RoguishSlayer.Name}MasterfulMimicry2024")
            .SetGuiPresentation(Category.Feature)
            .SetProficiencies(ProficiencyType.SkillOrExpertise, SkillDefinitions.Deception)
            .AddToDB();
        var rovingAim = FeatureDefinitionBuilder
            .Create($"Feature{RoguishSlayer.Name}RovingAim2024")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

        _assassinInfiltrationExpertise2024 = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{RoguishSlayer.Name}InfiltrationExpertise2024")
            .SetGuiPresentation(Category.Feature)
            .SetFeatureSet(masterfulMimicry, rovingAim, PowerFeatSteadyAim)
            .AddToDB();

        _assassinAssassinateReplacement = new CharacterFeatureReplacement(
            Rogue, assassin, 3, RoguishSlayer.OriginalElimination, RoguishSlayer.Assassinate2024,
            () => Main.Settings.EnableAssassinAssassinate2024);
        _assassinInfiltrationExpertiseReplacement = new CharacterFeatureReplacement(
            Rogue, assassin, 9,
            GetDefinition<FeatureDefinition>($"Feature{RoguishSlayer.Name}ChainOfExecution"),
            _assassinInfiltrationExpertise2024,
            () => Main.Settings.EnableAssassinInfiltrationExpertise2024);

        PowerFeatSteadyAim.AddCustomSubFeatures(
            PowerBundle.UncachedEffectDescription.Marker,
            new ModifyEffectDescriptionRovingAim(),
            new FormattedDefinitionText(characterDescription: character => IsAssassinRovingAimEnabled(character)
                ? Gui.Localize("Feature/&PowerFeatSteadyAimRovingAimDescription")
                : null));
        UpdateAssassinSteadyAimSupply();
    }

    internal static void SwitchAssassinAssassinate()
    {
        _assassinAssassinateReplacement?.Apply();
    }

    internal static void SwitchAssassinInfiltrationExpertise()
    {
        UpdateAssassinSteadyAimSupply();
        _assassinInfiltrationExpertiseReplacement?.Apply();
    }

    private static void UpdateAssassinSteadyAimSupply()
    {
        if (_assassinInfiltrationExpertise2024 == null)
        {
            return;
        }

        // Supply the same power from one origin: the class at level 3, or this
        // subclass at level 9 when the optional class feature is disabled.
        var features = _assassinInfiltrationExpertise2024.FeatureSet;
        features.RemoveAll(feature => feature == PowerFeatSteadyAim);
        if (Main.Settings.EnableAssassinInfiltrationExpertise2024 && !Main.Settings.EnableRogueSteadyAim2024)
        {
            features.Add(PowerFeatSteadyAim);
        }
    }

    internal static void SynchronizeAssassinFeatures(RulesetCharacterHero hero)
    {
        if (hero == null)
        {
            return;
        }

        if (hero.ClassesAndLevels.TryGetValue(Rogue, out var rogueLevel) && rogueLevel >= 3)
        {
            var tag = AttributeDefinitions.GetClassTag(Rogue, 3);
            if (!hero.ActiveFeatures.TryGetValue(tag, out var features) && Main.Settings.EnableRogueSteadyAim2024)
            {
                features = [];
                hero.ActiveFeatures[tag] = features;
            }

            if (features != null)
            {
                features.RemoveAll(feature => feature == PowerFeatSteadyAim);
                if (Main.Settings.EnableRogueSteadyAim2024)
                {
                    features.Add(PowerFeatSteadyAim);
                }
            }
        }

        _assassinAssassinateReplacement?.Synchronize(hero);
        _assassinInfiltrationExpertiseReplacement?.Synchronize(hero);

        if (rogueLevel >= 3)
        {
            CharacterFeatureReplacement.SynchronizePower(hero, PowerFeatSteadyAim, Rogue);
        }
    }

    private static bool IsAssassinRovingAimEnabled(RulesetCharacter character)
    {
        return character != null && Main.Settings.EnableAssassinInfiltrationExpertise2024 &&
               character.GetSubclassLevel(Rogue, RoguishSlayer.Name) >= 9;
    }

    private sealed class ModifyEffectDescriptionRovingAim : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == PowerFeatSteadyAim && IsAssassinRovingAimEnabled(character);
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            var restrained = GetDefinition<ConditionDefinition>($"Condition{FeatSteadyAim}Restrained");
            effectDescription.EffectForms.RemoveAll(form =>
                form.FormType == EffectForm.EffectFormType.Condition &&
                form.ConditionForm.ConditionDefinition == restrained);
            return effectDescription;
        }
    }

    internal static readonly ConditionDefinition ConditionReduceSneakDice = ConditionDefinitionBuilder
        .Create("ConditionReduceSneakDice")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetConditionType(ConditionType.Detrimental)
        .SetAmountOrigin(ConditionDefinition.OriginOfAmount.Fixed)
        .AllowMultipleInstances()
        .SetSpecialInterruptions(ConditionInterruption.Attacks)
        .AddToDB();

    private static FeatureDefinitionFeatureSet _featureSetRogueCunningStrike;
    private static FeatureDefinition _featureRogueImprovedCunningStrike;
    private static FeatureDefinitionFeatureSet _featureSetRogueDeviousStrike;

    private static readonly ConditionDefinition ConditionWithdrawn = ConditionDefinitionBuilder
        .Create(ConditionDefinitions.ConditionDisengaging, "ConditionWithdrawn")
        .SetSilent(Silent.None)
        .SetParentCondition(ConditionDefinitions.ConditionDisengaging)
        .SetFeatures()
        .SetFixedAmount(0)
        .AddCustomSubFeatures(new ActionFinishedByWithdraw())
        .AddToDB();

    private static void LoadRogueCunningStrike()
    {
        const string Cunning = "RogueCunningStrike";
        const string Devious = "RogueDeviousStrike";

        var powerPool = FeatureDefinitionPowerBuilder
            .Create($"Power{Cunning}")
            .SetGuiPresentation(Category.Feature)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.Individuals)
                    .Build())
            .AddToDB();

        // Disarm

        var combatAffinityDisarmed = FeatureDefinitionCombatAffinityBuilder
            .Create($"CombatAffinity{Cunning}Disarmed")
            .SetGuiPresentation($"Condition{Cunning}Disarmed", Category.Condition, Gui.NoLocalization)
            .SetMyAttackAdvantage(AdvantageType.Disadvantage)
            .AddToDB();

        var conditionDisarmed = ConditionDefinitionBuilder
            .Create($"Condition{Cunning}Disarmed")
            .SetGuiPresentation(Category.Condition, Gui.EmptyContent, ConditionBaned)
            .SetConditionType(ConditionType.Detrimental)
            .AddFeatures(combatAffinityDisarmed)
            .AddToDB();

        var powerDisarm = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Cunning}Disarm")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Dexterity, 8)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .SetConditionForm(conditionDisarmed, ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, PowerUsesSneakDiceTooltipModifier.Instance)
            .AddToDB();

        // Poison

        var powerPoison = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Cunning}Poison")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Constitution, false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Dexterity, 8)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates, TurnOccurenceType.EndOfTurn, true)
                            .SetConditionForm(
                                ConditionDefinitions.ConditionPoisoned, ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, PowerUsesSneakDiceTooltipModifier.Instance)
            .AddToDB();

        // Trip

        var powerTrip = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Cunning}Trip")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Dexterity, 8)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .SetMotionForm(MotionForm.MotionType.FallProne)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, PowerUsesSneakDiceTooltipModifier.Instance)
            .AddToDB();

        // Withdraw

        var powerWithdraw = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Cunning}Withdraw")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round)
                    .SetTargetingData(Side.Ally, RangeType.Distance, 12, TargetType.Position)
                    .Build())
            .AddCustomSubFeatures(
                ModifyPowerVisibility.Hidden,
                PowerUsesSneakDiceTooltipModifier.Instance,
                new ReactionRequestSpendBundlePower.EffectContext(targetsSelf: true),
                new CustomBehaviorFilterTargetingPositionHalfMove())
            .AddToDB();

        //
        // DEVIOUS STRIKES - LEVEL 14
        //

        // Dazed

        var actionAffinityDazedOnlyMovement = FeatureDefinitionActionAffinityBuilder
            .Create($"ActionAffinity{Devious}DazedOnlyMovement")
            .SetGuiPresentationNoContent(true)
            .SetAllowedActionTypes(false, false, freeOnce: false, reaction: false, noCost: false)
            .AddToDB();

        var conditionDazedOnlyMovement = ConditionDefinitionBuilder
            .Create($"Condition{Devious}DazedOnlyMovement")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetConditionType(ConditionType.Detrimental)
            .AddFeatures(actionAffinityDazedOnlyMovement)
            .AddToDB();

        var actionAffinityDazed = FeatureDefinitionActionAffinityBuilder
            .Create($"ActionAffinity{Devious}Dazed")
            .SetGuiPresentationNoContent(true)
            .SetAllowedActionTypes(reaction: false, bonus: false)
            .AddToDB();

        var conditionDazed = ConditionDefinitionBuilder
            .Create(ConditionDazzled, $"Condition{Devious}Dazed")
            .SetGuiPresentation(Category.Condition, ConditionDazzled)
            .SetConditionType(ConditionType.Detrimental)
            .SetFeatures(actionAffinityDazed)
            .AddCustomSubFeatures(new ActionFinishedByMeDazed(conditionDazedOnlyMovement))
            .AddToDB();

        var powerDaze = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Devious}Daze")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.NoCost, powerPool, 2)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Constitution, false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Dexterity, 8)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .SetConditionForm(conditionDazed, ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, PowerUsesSneakDiceTooltipModifier.Instance)
            .AddToDB();

        // Knock Out

        var conditionKnockOut = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionIncapacitated, $"Condition{Devious}KnockOut")
            .SetGuiPresentation(Category.Condition, Gui.EmptyContent, ConditionDefinitions.ConditionAsleep)
            .SetParentCondition(ConditionDefinitions.ConditionIncapacitated)
            .SetFeatures()
            .SetSpecialInterruptions(ConditionInterruption.Damaged)
            .AddToDB();

        var powerKnockOut = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Devious}KnockOut")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.NoCost, powerPool, 5)
            .SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, PowerUsesSneakDiceTooltipModifier.Instance)
            .AddToDB();

        var powerKnockOutApply = FeatureDefinitionPowerBuilder
            .Create($"Power{Devious}KnockOutApply")
            .SetGuiPresentation($"Power{Devious}KnockOut", Category.Feature, hidden: true)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Constitution, false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Dexterity, 8)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates, TurnOccurenceType.EndOfTurn, true)
                            .SetConditionForm(conditionKnockOut, ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddToDB();

        powerKnockOut.AddCustomSubFeatures(
            new ReactionRequestSpendBundlePower.EffectContext(powerKnockOutApply));

        // Obscure

        var powerObscure = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{Devious}Obscure")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.NoCost, powerPool, 3)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Dexterity, 8)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .SetConditionForm(ConditionDefinitions.ConditionBlinded,
                                ConditionForm.ConditionOperation.Add)
                            .Build())
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden, PowerUsesSneakDiceTooltipModifier.Instance)
            .AddToDB();

        // MAIN

        powerPool.AddCustomSubFeatures(
            ModifyPowerVisibility.Hidden,
            new CustomBehaviorCunningStrike(powerPool, powerKnockOut, powerKnockOutApply, powerWithdraw));

        PowerBundle.RegisterPowerBundle(powerPool, true,
            powerDisarm, powerPoison, powerTrip, powerWithdraw, powerDaze, powerKnockOut, powerObscure);

        var actionAffinityToggle = FeatureDefinitionActionAffinityBuilder
            .Create(ActionAffinitySorcererMetamagicToggle, "ActionAffinityCunningStrikeToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((Id)ExtraActionId.CunningStrikeToggle)
            .AddToDB();

        _featureSetRogueCunningStrike = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Cunning}")
            .SetGuiPresentation($"Power{Cunning}", Category.Feature)
            .SetFeatureSet(powerPool, actionAffinityToggle, powerDisarm, powerPoison, powerTrip, powerWithdraw)
            .AddToDB();

        _featureRogueImprovedCunningStrike = FeatureDefinitionBuilder
            .Create($"FeatureImproved{Cunning}")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

        _featureSetRogueDeviousStrike = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Devious}")
            .SetGuiPresentation($"Power{Devious}", Category.Feature)
            .SetFeatureSet(powerDaze, powerKnockOut, powerObscure)
            .AddToDB();
    }

    internal static void SwitchRogueReliableTalent()
    {
        Rogue.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == DieRollModifierRogueReliableTalent);

        Rogue.FeatureUnlocks.Add(Main.Settings.EnableRogueReliableTalent2024
            ? new FeatureUnlockByLevel(DieRollModifierRogueReliableTalent, 7)
            : new FeatureUnlockByLevel(DieRollModifierRogueReliableTalent, 11));

        Rogue.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchRogueSlipperyMind()
    {
        ProficiencyRogueSlipperyMind.Proficiencies.Remove(AttributeDefinitions.Charisma);

        if (Main.Settings.EnableRogueSlipperyMind2024)
        {
            ProficiencyRogueSlipperyMind.Proficiencies.Add(AttributeDefinitions.Charisma);
            ProficiencyRogueSlipperyMind.GuiPresentation.description = "Feature/&RogueSlipperyMindExtendedDescription";
        }
        else
        {
            ProficiencyRogueSlipperyMind.GuiPresentation.description = "Feature/&RogueSlipperyMindDescription";
        }
    }

    internal static void SwitchRogueSteadyAim()
    {
        Rogue.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == PowerFeatSteadyAim);

        if (Main.Settings.EnableRogueSteadyAim2024)
        {
            Rogue.FeatureUnlocks.Add(new FeatureUnlockByLevel(PowerFeatSteadyAim, 3));
        }

        Rogue.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        UpdateAssassinSteadyAimSupply();

        var heroes = ServiceRepository.GetService<IRulesetEntityService>()?.RulesetEntities.Values
            .OfType<RulesetCharacterHero>()
            .Where(hero => hero.ClassesAndLevels.TryGetValue(Rogue, out var level) && level >= 3)
            .ToArray();
        if (heroes != null)
        {
            foreach (var hero in heroes)
            {
                hero.RefreshAll();
            }
        }
    }

    internal static void SwitchRogueBlindSense()
    {
        Rogue.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == FeatureDefinitionSenses.SenseRogueBlindsense);

        if (!Main.Settings.RemoveRogueBlindSense2024)
        {
            Rogue.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureDefinitionSenses.SenseRogueBlindsense, 14));
        }

        Rogue.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static bool IsSneakAttackValid(
        ActionModifier attackModifier,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        RulesetAttackMode attackMode)
    {
        var additionalDamageDatabase = DatabaseRepository.GetDatabase<FeatureDefinitionAdditionalDamage>();
        // All granted Sneak Attack variants share the same native notification family.
        // Weapon eligibility uses their own providers, including subclass/custom contexts.
        if (attackMode == null || attacker.UsedSpecialFeatures.Keys.Any(name =>
                additionalDamageDatabase.TryGetElement(name, out var used) &&
                used.NotificationTag == TagsDefinitions.AdditionalDamageSneakAttackTag) ||
            !GetSneakAttackProviders(attacker, defender, attackMode).Any())
        {
            return false;
        }

        var advantageType = ComputeAdvantage(attackModifier.AttackAdvantageTrends);

        return advantageType switch
        {
            AdvantageType.Advantage => true,
            AdvantageType.Disadvantage => false,
            _ =>
                // it's an attack with a nearby enemy (standard sneak attack)
                ServiceRepository.GetService<IGameLocationBattleService>()
                    .IsConsciousCharacterOfSideNextToCharacter(defender, attacker.Side, attacker) ||
                // it's a Duelist and target is dueling with him
                RoguishDuelist.TargetIsDuelingWithRoguishDuelist(attacker, defender, advantageType) ||
                // it's a Swashbuckler and meets 1v1 conditions
                RoguishSwashbuckler.IsRakishAudacity1v1Valid(attacker, defender, advantageType) ||
                // it's an Umbral Stalker and source or target are in dim light or darkness
                RoguishUmbralStalker.SourceOrTargetAreNotBright(attacker, defender, advantageType)
        };
    }

    private static IEnumerable<FeatureDefinitionAdditionalDamage> GetSneakAttackProviders(
        GameLocationCharacter attacker, GameLocationCharacter defender, RulesetAttackMode attackMode)
    {
        var features = new List<FeatureDefinition>();
        attacker.RulesetCharacter.EnumerateFeaturesToBrowse<IAdditionalDamageProvider>(features);
        var implementation = ServiceRepository.GetService<IRulesetImplementationService>();
        var item = (attackMode.SourceObject as RulesetItem)?.ItemDefinition ??
                   attackMode.SourceDefinition as ItemDefinition;
        return features.OfType<FeatureDefinitionAdditionalDamage>().Where(provider =>
            provider.NotificationTag == TagsDefinitions.AdditionalDamageSneakAttackTag &&
            (provider.TargetSide != Side.Enemy ||
             TargetingContext.IsValidOffensiveTarget(attacker, defender, provider)) &&
            (provider.TargetSide != Side.Ally || !attacker.IsOppositeSide(defender.Side)) &&
            implementation?.IsValidContextForRestrictedContextProvider(provider,
                attacker.RulesetCharacter, item, attackMode.Ranged, attackMode, null) == true);
    }

    internal static void SwitchRogueCunningStrike()
    {
        Rogue.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == _featureSetRogueCunningStrike ||
            x.FeatureDefinition == _featureRogueImprovedCunningStrike ||
            x.FeatureDefinition == _featureSetRogueDeviousStrike);

        if (Main.Settings.EnableRogueCunningStrike2024)
        {
            Rogue.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(_featureSetRogueCunningStrike, 5),
                new FeatureUnlockByLevel(_featureRogueImprovedCunningStrike, 11),
                new FeatureUnlockByLevel(_featureSetRogueDeviousStrike, 14));
        }

        Rogue.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private sealed class CustomBehaviorCunningStrike(
        FeatureDefinitionPower powerRogueCunningStrike,
        FeatureDefinitionPower powerKnockOut,
        FeatureDefinitionPower powerKnockOutApply,
        FeatureDefinitionPower powerWithdraw)
        : IPhysicalAttackBeforeHitConfirmedOnEnemy, IPhysicalAttackFinishedByMe
    {
        private readonly ConditionalWeakTable<RulesetAttackMode,
            Dictionary<(ulong Attacker, ulong Defender), List<FeatureDefinitionPower>>> _selections = new();

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
            var selections = _selections.GetOrCreateValue(attackMode);
            var selectionKey = (attacker.Guid, defender.Guid);
            selections.Remove(selectionKey);

            var rulesetAttacker = attacker.RulesetCharacter;

            if (!rulesetAttacker.IsToggleEnabled((Id)ExtraActionId.CunningStrikeToggle) ||
                !IsSneakAttackValid(actionModifier, attacker, defender, attackMode))
            {
                yield break;
            }

            var maximumEffects = rulesetAttacker.GetClassLevel(Rogue) >= 11 ? 2 : 1;
            var usablePower = PowerProvider.Get(powerRogueCunningStrike, rulesetAttacker);
            var battle = Gui.Battle;
            bool StillValid() => Gui.Battle == battle &&
                rulesetAttacker is { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } &&
                rulesetAttacker.IsToggleEnabled((Id)ExtraActionId.CunningStrikeToggle) &&
                ReactionRequestSelectTarget.IsCandidateValid(attacker, defender) &&
                IsSneakAttackValid(actionModifier, attacker, defender, attackMode);
            int AvailableDice()
            {
                var reductions = new List<RulesetCondition>();
                rulesetAttacker.GetAllConditionsOfType(reductions, ConditionReduceSneakDice.Name);
                return System.Math.Max(0, (rulesetAttacker.GetClassLevel(Rogue) + 1) / 2 -
                    reductions.Sum(condition => condition.Amount));
            }

            yield return attacker.MyReactToSelectPowerBundle(usablePower, defender,
                powerRogueCunningStrike.Name, maximumEffects, AvailableDice, SelectionApplied, StillValid);
            yield break;

            void SelectionApplied(IReadOnlyList<FeatureDefinitionPower> selected)
            {
                if (!selections.TryGetValue(selectionKey, out var chosen))
                {
                    chosen = [];
                    selections[selectionKey] = chosen;
                }
                chosen.AddRange(selected);
                // Allocate the accepted effects once. Saving against an effect does not
                // refund the Sneak Attack dice sacrificed for that effect.
                rulesetAttacker.InflictCondition(
                    ConditionReduceSneakDice.Name,
                    DurationType.Round,
                    0,
                    TurnOccurenceType.EndOfTurn,
                    AttributeDefinitions.TagEffect,
                    rulesetAttacker.guid,
                    rulesetAttacker.CurrentFaction.Name,
                    1,
                    ConditionReduceSneakDice.Name,
                    selected.Sum(power => power.CostPerUse),
                    0,
                    0);
            }
        }

        // handle Knock Out exception which should apply condition after attack
        public IEnumerator OnPhysicalAttackFinishedByMe(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            RollOutcome rollOutcome,
            int damageAmount)
        {
            if (!_selections.TryGetValue(attackMode, out var selections) ||
                !selections.TryGetValue((attacker.Guid, defender.Guid), out var selectedPowers))
            {
                yield break;
            }
            // Remove before yielding to a follow-up reaction from the same attacker.
            selections.Remove((attacker.Guid, defender.Guid));
            foreach (var selectedPower in selectedPowers)
            {
                if (selectedPower == powerKnockOut)
                {
                    yield return HandleKnockOut(attacker, defender);
                }
                else if (selectedPower == powerWithdraw)
                {
                    yield return HandleWithdraw(action, attacker);
                }
            }

        }

        private IEnumerator HandleWithdraw(CharacterAction action, GameLocationCharacter attacker)
        {
            yield return CampaignsContext.SelectPosition(action, powerWithdraw);

            var position = action.ActionParams.Positions[0];

            if (attacker.LocationPosition == position)
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;

            rulesetAttacker.InflictCondition(
                ConditionWithdrawn.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                // all disengaging in game is set under TagCombat (why?)
                AttributeDefinitions.TagCombat,
                rulesetAttacker.Guid,
                rulesetAttacker.CurrentFaction.Name,
                1,
                ConditionWithdrawn.Name,
                attacker.UsedTacticalMoves,
                0,
                0);

            var distance = (int)int3.Distance(attacker.LocationPosition, position);

            attacker.UsedTacticalMoves -= distance;
            attacker.UsedTacticalMovesChanged?.Invoke(attacker);

            var actionParams = new CharacterActionParams(
                attacker, Id.TacticalMove, MoveStance.Run, position, LocationDefinitions.Orientation.North)
            {
                BoolParameter3 = false, BoolParameter5 = false
            };

            action.ResultingActions.Add(new CharacterActionMove(actionParams));
        }

        private IEnumerator HandleKnockOut(GameLocationCharacter attacker, GameLocationCharacter defender)
        {
            var rulesetDefender = defender.RulesetActor;

            if (rulesetDefender is not { IsDeadOrDyingOrUnconscious: false })
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;
            var usablePower = PowerProvider.Get(powerKnockOutApply, rulesetAttacker);

            attacker.MyExecuteActionSpendPower(usablePower, defender);
        }
    }

    private sealed class ActionFinishedByMeDazed(
        // ReSharper disable once SuggestBaseTypeForParameterInConstructor
        ConditionDefinition conditionDazedOnlyMovement) : IActionFinishedByMe
    {
        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (action is not (CharacterActionMove or CharacterActionDash))
            {
                yield break;
            }

            var rulesetCharacter = action.ActingCharacter.RulesetCharacter;

            rulesetCharacter.InflictCondition(
                conditionDazedOnlyMovement.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetCharacter.guid,
                rulesetCharacter.CurrentFaction.Name,
                1,
                conditionDazedOnlyMovement.Name,
                0,
                0,
                0);
        }
    }

    private sealed class ActionFinishedByWithdraw : IActionFinishedByMe
    {
        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            var actingCharacter = action.ActingCharacter;

            if (action is not CharacterActionMoveStepBase || actingCharacter.MovingToDestination)
            {
                yield break;
            }

            var rulesetCharacter = actingCharacter.RulesetCharacter;

            if (!rulesetCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagCombat, ConditionWithdrawn.Name, out var activeCondition))
            {
                yield break;
            }

            actingCharacter.UsedTacticalMoves = activeCondition.Amount;
            actingCharacter.UsedTacticalMovesChanged?.Invoke(actingCharacter);
            rulesetCharacter.RemoveCondition(activeCondition);
        }
    }
}
