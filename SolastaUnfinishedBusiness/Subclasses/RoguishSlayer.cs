using System.Collections;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public sealed class RoguishSlayer : AbstractSubclass
{
    internal const string Name = "RoguishSlayer";
    internal const string ConditionChainOfExecutionBeneficialName = $"Condition{Name}{ChainOfExecution}Beneficial";
    internal const int ChainOfExecutionLevel = 9;

    private const string Elimination = "Elimination";
    private const string ChainOfExecution = "ChainOfExecution";
    private const string CloakOfShadows = "CloakOfShadows";
    private const string ConditionChainOfExecutionDetrimentalName = $"Condition{Name}{ChainOfExecution}Detrimental";

    public RoguishSlayer()
    {
        // LEVEL 03

        // Elimination

        var featureElimination = FeatureDefinitionBuilder
            .Create($"Feature{Name}{Elimination}")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

        featureElimination.AddCustomSubFeatures(new CustomBehaviorElimination(featureElimination));
        OriginalElimination = featureElimination;

        var combatAffinityAssassinate2024 = FeatureDefinitionCombatAffinityBuilder
            .Create($"CombatAffinity{Name}Assassinate2024")
            .SetGuiPresentationNoContent(true)
            .SetInitiativeAffinity(AdvantageType.Advantage)
            .AddToDB();

        var featureSetAssassinate2024 = FeatureDefinitionFeatureSetBuilder
            .Create($"FeatureSet{Name}Assassinate2024")
            .SetGuiPresentation(Category.Feature)
            .SetFeatureSet(combatAffinityAssassinate2024)
            .AddToDB();

        combatAffinityAssassinate2024.AddCustomSubFeatures(new CustomBehaviorElimination(featureSetAssassinate2024, true));
        Assassinate2024 = featureSetAssassinate2024;

        // LEVEL 09

        // Chain of Execution

        _ = ConditionDefinitionBuilder
            .Create(ConditionChainOfExecutionBeneficialName)
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionBullsStrength)
            .SetPossessive()
            .AddToDB();

        _ = ConditionDefinitionBuilder
            .Create(ConditionChainOfExecutionDetrimentalName)
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionBleeding)
            .SetConditionType(ConditionType.Detrimental)
            .SetPossessive()
            .AddCustomSubFeatures(new OnConditionAddedOrRemovedChainOfExecution())
            .AddToDB();

        var featureChainOfExecution = FeatureDefinitionBuilder
            .Create($"Feature{Name}{ChainOfExecution}")
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(new OnReducedToZeroHpByMeChainOfExecution())
            .AddToDB();

        // LEVEL 13

        // Cloak of Shadows

        var powerCloakOfShadows = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}{CloakOfShadows}")
            .SetGuiPresentation(Category.Feature, SpellDefinitions.Invisibility)
            .SetUsesAbilityBonus(ActivationTime.BonusAction, RechargeRate.LongRest, AttributeDefinitions.Dexterity)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(SpellDefinitions.Invisibility.EffectDescription)
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetParticleEffectParameters(PowerSorakAssassinShadowMurder)
                    .Build())
            .AddToDB();

        // LEVEL 17

        // Fatal Strike

        var featureFatalStrike = FeatureDefinitionBuilder
            .Create($"Feature{Name}FatalStrike")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

        featureFatalStrike.AddCustomSubFeatures(new PhysicalAttackInitiatedByMeFatalStrike(featureFatalStrike));

        // MAIN

        Subclass = CharacterSubclassDefinitionBuilder
            .Create(Name)
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite(Name, Resources.RoguishSlayer, 256))
            .AddFeaturesAtLevel(3, featureElimination)
            .AddFeaturesAtLevel(9, featureChainOfExecution)
            .AddFeaturesAtLevel(13, powerCloakOfShadows)
            .AddFeaturesAtLevel(17, featureFatalStrike)
            .AddToDB();
    }

    internal static FeatureDefinition OriginalElimination { get; private set; }

    internal static FeatureDefinition Assassinate2024 { get; private set; }

    internal override CharacterClassDefinition Klass => CharacterClassDefinitions.Rogue;

    internal override CharacterSubclassDefinition Subclass { get; }

    internal override FeatureDefinitionSubclassChoice SubclassChoice =>
        FeatureDefinitionSubclassChoices.SubclassChoiceRogueRoguishArchetypes;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    internal static void ModifySneakAttackDamage(
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        RulesetAttackMode attackMode,
        ref DamageForm damageForm)
    {
        var rulesetAttacker = attacker.RulesetCharacter.GetFeatureOwnerOrSelf();
        var slayerLevels = rulesetAttacker?.GetSubclassLevel(CharacterClassDefinitions.Rogue, Name) ?? 0;

        if (slayerLevels < 3)
        {
            return;
        }

        // The caller has resolved an actual Sneak Attack provider. Its usage limit
        // also covers attacks outside the rogue's turn, including reactions.
        if (Main.Settings.EnableAssassinAssassinate2024 && Gui.Battle is { CurrentRound: 1 } battle &&
            battle.initiativeSortedContenders.Contains(attacker) &&
            battle.initiativeSortedContenders.Contains(defender) &&
            attackMode?.SourceDefinition is ItemDefinition { IsWeapon: true } &&
            !ValidatorsWeapon.IsUnarmed(attackMode))
        {
            damageForm.BonusDamage += slayerLevels;
        }

        if (Main.Settings.EnableAssassinInfiltrationExpertise2024 || slayerLevels < ChainOfExecutionLevel)
        {
            return;
        }

        InflictConditionChainOfExecution(rulesetAttacker, defender.RulesetCharacter);

        if (!rulesetAttacker.TryGetConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect, ConditionChainOfExecutionBeneficialName, out var activeCondition) ||
            activeCondition.SourceGuid != rulesetAttacker.Guid)
        {
            return;
        }

        var extraDice = slayerLevels switch
        {
            >= 17 => 5,
            >= 13 => 4,
            _ => 3
        };
        var newDiceNumber = damageForm.DiceNumber + extraDice;
        var title = Gui.Format("Feature/&FeatureRoguishSlayerChainOfExecutionTitle");
        var description = Gui.Format("Feature/&FeatureRoguishSlayerChainOfExecutionDescription");

        rulesetAttacker.LogCharacterActivatesAbility(
            title, "Feedback/&ChangeSneakDiceNumber",
            tooltipContent: description, indent: true,
            extra:
            [
                (ConsoleStyleDuplet.ParameterType.AbilityInfo, damageForm.DiceNumber.ToString()),
                (ConsoleStyleDuplet.ParameterType.Positive, newDiceNumber.ToString())
            ]);

        damageForm.DiceNumber = newDiceNumber;
        rulesetAttacker.RemoveCondition(activeCondition);
    }

    internal static void InflictConditionChainOfExecution(
        RulesetCharacter rulesetAttacker,
        RulesetCharacter rulesetDefender = null)
    {
        if (Main.Settings.EnableAssassinInfiltrationExpertise2024)
        {
            return;
        }

        var isBeneficial = rulesetDefender == null;

        var conditionName = isBeneficial
            ? ConditionChainOfExecutionBeneficialName
            : ConditionChainOfExecutionDetrimentalName;

        var rulesetCharacter = isBeneficial
            ? rulesetAttacker
            : rulesetDefender;

        if (isBeneficial && rulesetCharacter.HasConditionOfType(conditionName))
        {
            return;
        }

        var endOccurrence = isBeneficial
            ? TurnOccurenceType.EndOfTurn
            : TurnOccurenceType.StartOfTurn;

        rulesetCharacter.InflictCondition(
            conditionName,
            DurationType.Round,
            1,
            endOccurrence,
            AttributeDefinitions.TagEffect,
            rulesetAttacker.guid,
            rulesetAttacker.CurrentFaction.Name,
            1,
            conditionName,
            0,
            0,
            0);
    }

    //
    // Elimination
    //

    private sealed class CustomBehaviorElimination(FeatureDefinition featureDefinition, bool use2024Rules = false)
        : IModifyAttackActionModifier, IModifyAttackCriticalThreshold
    {
        private readonly TrendInfo _trendInfo =
            new(1, FeatureSourceType.CharacterFeature, featureDefinition.Name, featureDefinition);

        // Allow advantage if first round and higher initiative order vs defender
        public void OnAttackComputeModifier(
            RulesetCharacter myself,
            RulesetCharacter defender,
            BattleDefinitions.AttackProximity attackProximity,
            RulesetAttackMode attackMode,
            string effectName,
            ref ActionModifier attackModifier)
        {
            var battle = Gui.Battle;

            if (use2024Rules)
            {
                if (battle is not { CurrentRound: 1 })
                {
                    return;
                }

                var source = GameLocationCharacter.GetFromActor(myself);

                if (source == null || !battle.initiativeSortedContenders.Contains(source))
                {
                    return;
                }

                var target = GameLocationCharacter.GetFromActor(defender);
                var targetOrder = battle.initiativeSortedContenders.IndexOf(target);

                // Compare against the current normal turn, not the attacker's own
                // initiative: reactions can occur before or after either creature acts.
                if (targetOrder > battle.activeContenderIndex)
                {
                    attackModifier.AttackAdvantageTrends.Add(_trendInfo);
                }

                return;
            }

            // always grant advantage on battle round zero
            if (battle == null)
            {
                attackModifier.AttackAdvantageTrends.Add(_trendInfo);

                return;
            }

            if (battle.CurrentRound > 1)
            {
                return;
            }

            // battle round one from here
            var gameLocationAttacker = GameLocationCharacter.GetFromActor(myself);
            var gameLocationDefender = GameLocationCharacter.GetFromActor(defender);

            if (gameLocationAttacker == null || gameLocationDefender == null)
            {
                return;
            }

            var attackerAttackOrder = battle.initiativeSortedContenders.IndexOf(gameLocationAttacker);
            var defenderAttackOrder = battle.initiativeSortedContenders.IndexOf(gameLocationDefender);

            if (defenderAttackOrder >= 0 && attackerAttackOrder > defenderAttackOrder)
            {
                return;
            }

            attackModifier.AttackAdvantageTrends.Add(_trendInfo);
        }

        public int GetCriticalThreshold(
            int current, RulesetCharacter me, RulesetCharacter target, BaseDefinition attackMethod)
        {
            return !use2024Rules && target.HasConditionOfType(ConditionSurprised) ? 1 : current;
        }
    }

    //
    // Chain of Execution
    //

    private sealed class OnConditionAddedOrRemovedChainOfExecution : IOnConditionAddedOrRemoved
    {
        public void OnConditionAdded(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            // empty
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            if (target is not { IsDeadOrDyingOrUnconscious: true } ||
                !RulesetEntity.TryGetEntity<RulesetCharacter>(rulesetCondition.sourceGuid, out var rulesetCharacter))
            {
                return;
            }

            InflictConditionChainOfExecution(rulesetCharacter);
        }
    }

    private sealed class OnReducedToZeroHpByMeChainOfExecution : IOnReducedToZeroHpByMe
    {
        public IEnumerator HandleReducedToZeroHpByMe(
            GameLocationCharacter attacker,
            GameLocationCharacter downedCreature,
            RulesetAttackMode attackMode,
            RulesetEffect activeEffect)
        {
            InflictConditionChainOfExecution(attacker.RulesetCharacter);

            yield break;
        }
    }

    //
    // Fatal Strike
    //

    private sealed class PhysicalAttackInitiatedByMeFatalStrike(
        // ReSharper disable once SuggestBaseTypeForParameterInConstructor
        FeatureDefinition featureFatalStrike) : IPhysicalAttackInitiatedByMe
    {
        public IEnumerator OnPhysicalAttackInitiatedByMe(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier attackModifier,
            RulesetAttackMode attackMode)
        {
            var rulesetDefender = defender.RulesetActor;

            if (rulesetDefender is not { IsDeadOrDyingOrUnconscious: false } ||
                !rulesetDefender.HasConditionOfCategoryAndType(AttributeDefinitions.TagEffect, ConditionSurprised))
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;
            var modifierTrend = rulesetDefender.actionModifier.SavingThrowModifierTrends;
            var advantageTrends = rulesetDefender.actionModifier.SavingThrowAdvantageTrends;
            var attackerDexterityModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                rulesetAttacker.TryGetAttributeValue(AttributeDefinitions.Dexterity));
            var attackerProficiencyBonus =
                rulesetAttacker.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);
            var defenderConstitutionModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                rulesetDefender.TryGetAttributeValue(AttributeDefinitions.Constitution));

            rulesetDefender.RollSavingThrow(
                0, AttributeDefinitions.Constitution, featureFatalStrike,
                modifierTrend, advantageTrends, defenderConstitutionModifier,
                8 + attackerProficiencyBonus + attackerDexterityModifier, false, out var savingOutcome, out _);

            if (savingOutcome == RollOutcome.Success)
            {
                yield break;
            }

            attackModifier.AttackerDamageMultiplier *= 2;
        }
    }
}
