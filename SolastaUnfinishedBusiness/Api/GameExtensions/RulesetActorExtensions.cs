using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Subclasses;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class RulesetActorExtensions
{
    #region Saving Throw Handlers

    private static void OnRollSavingThrowOath(
        RulesetCharacter caster,
        RulesetActor target,
        BaseDefinition sourceDefinition,
        string selfConditionName,
        ConditionDefinition conditionDefinitionEnemy)
    {
        if (caster == null ||
            caster.Side == target.Side ||
            !caster.HasConditionOfCategoryAndType(AttributeDefinitions.TagEffect, selfConditionName))
        {
            return;
        }

        if (sourceDefinition is not SpellDefinition { castingTime: ActivationTime.Action } &&
            sourceDefinition is not FeatureDefinitionPower { RechargeRate: RechargeRate.ChannelDivinity } &&
            !caster.ConditionsByCategory
                .SelectMany(x => x.Value)
                .Any(x => x.Name.Contains("Smite")))
        {
            return;
        }

        var gameLocationCaster = GameLocationCharacter.GetFromActor(caster);
        var gameLocationTarget = GameLocationCharacter.GetFromActor(target);

        if (gameLocationCaster == null ||
            gameLocationTarget == null ||
            !gameLocationCaster.IsWithinRange(gameLocationTarget, 2))
        {
            return;
        }

        target.InflictCondition(
            conditionDefinitionEnemy.Name,
            DurationType.Round,
            0,
            TurnOccurenceType.StartOfTurn,
            AttributeDefinitions.TagEffect,
            caster.guid,
            caster.CurrentFaction.Name,
            1,
            conditionDefinitionEnemy.Name,
            0,
            0,
            0);
    }

    // keep a tab on last SaveDC / SaveBonusAndRollModifier / SavingThrowAbility
    internal static int SaveDC { get; private set; }
    internal static int SaveBonusAndRollModifier { get; private set; }
    internal static string SavingThrowAbility { get; private set; }

    internal static void MyRollSavingThrow(
        this RulesetActor rulesetActorTarget,
        RulesetCharacter rulesetActorCaster,
        int saveBonus,
        string abilityScoreName,
        BaseDefinition sourceDefinition,
        List<TrendInfo> modifierTrends,
        List<TrendInfo> advantageTrends,
        int rollModifier,
        int saveDC,
        bool hasHitVisual,
        ref RollOutcome outcome,
        ref int outcomeDelta,
        List<EffectForm> effectForms)
    {
        //PATCH: supports Oath of Ancients / Oath of Dread
        OnRollSavingThrowOath(rulesetActorCaster, rulesetActorTarget, sourceDefinition,
            OathOfAncients.ConditionElderChampionName,
            OathOfAncients.ConditionElderChampionEnemy);
        OnRollSavingThrowOath(rulesetActorCaster, rulesetActorTarget, sourceDefinition,
            OathOfDread.ConditionAspectOfDreadName,
            OathOfDread.ConditionAspectOfDreadEnemy);

        var rulesetCharacterTarget = rulesetActorTarget as RulesetCharacter;

        if (rulesetCharacterTarget != null)
        {
            //PATCH: supports Path of The Savagery
            PathOfTheSavagery.OnRollSavingThrowFuriousDefense(rulesetCharacterTarget, ref abilityScoreName);

            //PATCH: supports `OnSavingThrowInitiated` interface
            foreach (var rollSavingThrowInitiated in rulesetCharacterTarget
                         .GetSubFeaturesByType<IRollSavingThrowInitiated>())
            {
                rollSavingThrowInitiated.OnSavingThrowInitiated(
                    rulesetActorCaster,
                    rulesetActorTarget,
                    ref saveBonus,
                    ref abilityScoreName,
                    sourceDefinition,
                    modifierTrends,
                    advantageTrends,
                    ref rollModifier,
                    ref saveDC,
                    ref hasHitVisual,
                    outcome,
                    outcomeDelta,
                    effectForms);
            }
        }

        // keep a tab on last SaveDC / SaveBonusAndRollModifier / SavingThrowAbility
        SaveDC = saveDC;
        SaveBonusAndRollModifier = saveBonus + rollModifier;
        SavingThrowAbility = abilityScoreName;

        var saveRoll = rulesetActorTarget.RollDie(
            DieType.D20, RollContext.SavingThrow, false, ComputeAdvantage(advantageTrends),
            out var firstRoll, out var secondRoll);

        var totalRoll = saveRoll + saveBonus + rollModifier;
        outcomeDelta = totalRoll - saveDC;
        outcome = totalRoll < saveDC ? RollOutcome.Failure : RollOutcome.Success;

        foreach (var modifierTrend in modifierTrends)
        {
            if (modifierTrend.dieFlag == TrendInfoDieFlag.None ||
                modifierTrend is not { value: > 0, dieType: > DieType.D1 })
            {
                continue;
            }

            var additionalSaveDieRolled = rulesetActorTarget.AdditionalSaveDieRolled;

            additionalSaveDieRolled?.Invoke(rulesetActorTarget, modifierTrend);
        }

        rulesetActorTarget.SaveRolled?.Invoke(rulesetActorTarget, abilityScoreName, sourceDefinition, outcome, saveDC,
            totalRoll,
            saveRoll, firstRoll, secondRoll, saveBonus + rollModifier, modifierTrends, advantageTrends, hasHitVisual);

        rulesetActorTarget.ProcessConditionsMatchingInterruption(ConditionInterruption.SavingThrow);

        //BEGIN PATCH
        if (rulesetCharacterTarget == null)
        {
            return;
        }

        //PATCH: supports `IRollSavingThrowFinished` interface
        foreach (var rollSavingThrowFinished in rulesetCharacterTarget.GetSubFeaturesByType<IRollSavingThrowFinished>())
        {
            rollSavingThrowFinished.OnSavingThrowFinished(
                rulesetActorCaster,
                rulesetActorTarget,
                saveBonus,
                abilityScoreName,
                sourceDefinition,
                modifierTrends,
                advantageTrends,
                rollModifier,
                saveDC,
                hasHitVisual,
                ref outcome,
                ref outcomeDelta,
                effectForms);
        }
        //END PATCH
    }

    #endregion

    internal static void ModifyAttributeAndMax(this RulesetActor hero, string attributeName, int amount)
    {
        var attribute = hero.GetAttribute(attributeName);

        attribute.BaseValue += amount;
        attribute.MaxValue += amount;
        attribute.MaxEditableValue += amount;
        attribute.Refresh();

        hero.AbilityScoreIncreased?.Invoke(hero, attributeName, amount, amount);
    }

    [NotNull]
    internal static List<T> FeaturesByType<T>([CanBeNull] this RulesetActor actor) where T : class
    {
        var list = new List<FeatureDefinition>();

        actor?.EnumerateFeaturesToBrowse<T>(list);

        // mainly because of Feature Sets granted as invocations (tabletop 2024)
        list.AddRange(list.OfType<FeatureDefinitionFeatureSet>().SelectMany(x => x.FeatureSet).ToArray());

        return list
            .OfType<T>()
            .ToList();
    }

    internal static bool HasFastBardicInspirationRecovery(this RulesetActor actor)
    {
        return actor.FeaturesByType<FeatureDefinitionCampAffinity>()
            .Any(feature => feature.FastBardicInspirationRecovery);
    }

    [NotNull]
    // ReSharper disable once ReturnTypeCanBeEnumerable.Local
    private static List<BaseDefinition> AllActiveDefinitions([CanBeNull] RulesetActor actor)
    {
        var list = FeaturesByType<BaseDefinition>(actor);

        RulesetCharacterHero hero = null;

        switch (actor)
        {
            case RulesetCharacterHero rulesetCharacterHero:
                hero = rulesetCharacterHero;
                break;
            case RulesetCharacterSimulacrum simulacrum:
                // Match the definition carriers a Hero exposes below. Feature-set
                // carriers are restored separately because their leaf features are
                // flattened by native enumeration.
                foreach (var definition in simulacrum.FeaturesOrigin.Values
                             .Select(origin => origin.source)
                             .OfType<BaseDefinition>()
                             .Concat(simulacrum.Invocations
                                 .Where(invocation => invocation?.InvocationDefinition != null)
                                 .Select(invocation =>
                                     (BaseDefinition)invocation.InvocationDefinition))
                             .Concat(SimulacrumBehavior
                                 .EnumerateTrainedFeats(simulacrum)
                                 .Cast<BaseDefinition>())
                             .Concat(SimulacrumBehavior
                                 .EnumerateTrainedFightingStyles(simulacrum)
                                 .Cast<BaseDefinition>())
                             .Where(definition => definition is
                                 FeatDefinition or
                                 InvocationDefinition or
                                 FightingStyleDefinition)
                             .Concat(SimulacrumBehavior.EnumerateBehaviorCarriers(simulacrum))
                             .Distinct())
                {
                    if (!list.Contains(definition))
                    {
                        list.Add(definition);
                    }
                }

                break;
            //WILDSHAPE: Original hero features
            case RulesetCharacterMonster { originalFormCharacter: RulesetCharacterHero rulesetCharacterHero }:
                hero = rulesetCharacterHero;
                list.AddRange(FeaturesByType<BaseDefinition>(hero)
                    .Where(f => !list.Contains(f)));
                break;
        }

        if (hero == null)
        {
            return list;
        }

        // metamagic are handled in other locations
        list.AddRange(hero.trainedFeats);
        list.AddRange(hero.trainedInvocations);
        list.AddRange(hero.trainedFightingStyles);

        return list;
    }

    internal static bool HasAnyFeature(this RulesetActor actor, params FeatureDefinition[] features)
    {
        return FeaturesByType<FeatureDefinition>(actor).Any(features.Contains);
    }

    internal static bool HasAnyFeature(this RulesetActor actor, params string[] featureNames)
    {
        return FeaturesByType<FeatureDefinition>(actor).Any(f => featureNames.Contains(f.Name));
    }

    [NotNull]
    public static IEnumerable<FeatureDefinition> FlattenFeatureList([NotNull] IEnumerable<FeatureDefinition> features)
    {
        return features.SelectMany(f =>
            f is FeatureDefinitionFeatureSet set
                ? FlattenFeatureList(set.FeatureSet)
                : [f]);
    }

    [NotNull]
    private static IEnumerable<T> EnumerateSubFeaturesByType<T>([CanBeNull] RulesetActor actor, params Type[] typesToSkip)
        where T : class
    {
        foreach (var subFeature in AllActiveDefinitions(actor)
                     .Where(feature => !typesToSkip.Contains(feature.GetType()))
                     .SelectMany(feature => feature.GetAllSubFeaturesOfType<T>()))
        {
            yield return subFeature;
        }

        if (actor == null)
        {
            yield break;
        }

        foreach (var subFeature in actor.ConditionsByCategory
                     .SelectMany(x => x.Value)
                     .SelectMany(x => x.ConditionDefinition.GetAllSubFeaturesOfType<T>()))
        {
            yield return subFeature;
        }
    }

    [NotNull]
    internal static List<T> GetSubFeaturesByType<T>([CanBeNull] this RulesetActor actor, params Type[] typesToSkip)
        where T : class
    {
        return EnumerateSubFeaturesByType<T>(actor, typesToSkip).ToList();
    }

    internal static bool HasSubFeatureOfType<T>([CanBeNull] this RulesetActor actor, params Type[] typesToSkip)
        where T : class
    {
        return EnumerateSubFeaturesByType<T>(actor, typesToSkip).Any();
    }

    internal static bool IsTouchingGround(this RulesetActor actor)
    {
        return !actor.HasConditionOfType(ConditionFlying) &&
               !(actor is RulesetCharacter other && other.MoveModes.ContainsKey((int)MoveMode.Fly));
    }

    internal static bool IsTemporarilyFlying(this RulesetActor actor)
    {
        return actor is RulesetCharacter character
               && character.HasTemporaryConditionOfType(ConditionFlying)
               && !character.HasConditionOfType(ConditionLevitate);
        /*
         * For future use, when can allow flying wildshape to temporarily walk
         *
        || (actor.HasConditionOfType(RuleDefinitions.ConditionWildShapeSubstituteForm)
                && actor is RulesetCharacterMonster monster
                && monster.MoveModes.ContainsKey((int)RuleDefinitions.MoveMode.Fly)
                && !actor.HasConditionOfType("ConditionFlightSuspended")

        );*/
    }

    internal static bool HasSuspendableFlightCondition(this RulesetActor actor)
    {
        return actor.IsTemporarilyFlying();
    }

    internal static bool HasAnyConditionOfType(this RulesetActor actor, params string[] conditions)
    {
        return actor is RulesetCharacter && conditions.Any(actor.HasConditionOfType);
    }

    internal static bool HasAnyConditionOfTypeOrSubType(this RulesetActor actor, params string[] conditions)
    {
        return actor is RulesetCharacter && conditions.Any(actor.HasConditionOfTypeOrSubType);
    }


    internal static void RemoveAllConditionsOfType(this RulesetActor actor, params string[] conditions)
    {
        var conditionsToRemove = actor.ConditionsByCategory
            .SelectMany(x => x.Value)
            .Where(x => conditions.Contains(x.ConditionDefinition.Name))
            .ToArray();

        foreach (var condition in conditionsToRemove)
        {
            actor.RemoveCondition(condition, false);
        }

        actor.RefreshAll();
    }

    internal static int TryGetProficiencyBonus(this RulesetActor actor)
    {
        if (actor == null ||
            !actor.TryGetAttribute(AttributeDefinitions.ProficiencyBonus, out var proficiencyBonusAttribute))
        {
            return 0;
        }

        if (!proficiencyBonusAttribute.upToDate)
        {
            proficiencyBonusAttribute.Refresh();
        }

        return proficiencyBonusAttribute.CurrentValue;
    }

    internal static int TryGetAbilityModifier(this RulesetActor actor, string ability)
    {
        return AttributeDefinitions.ComputeAbilityScoreModifier(actor.TryGetAttributeValue(ability));
    }

    internal static RollOutcome MakeSimpleSavingThrow(this RulesetActor actor, string saveAbility, int dc,
        BaseDefinition source,
        string schoolOfMagic = "", string damageType = "", string conditionType = "", string sourceFamily = "")
    {
        var modifier = new ActionModifier();
        var saveBonus = actor.ComputeBaseSavingThrowBonus(saveAbility, modifier.SavingThrowModifierTrends);
        actor.ComputeSavingThrowModifier(saveAbility, EffectForm.EffectFormType.Motion, source.Name, 
            schoolOfMagic, damageType, conditionType, sourceFamily, modifier, []);

        actor.RollSavingThrow(saveBonus, saveAbility, source, modifier.SavingThrowModifierTrends,
            modifier.SavingThrowAdvantageTrends, modifier.SavingThrowModifier, dc, false, out var outcome, out _);

        return outcome;
    }
    
    internal static bool ReceivesMaximizedHealing(this RulesetActor actor)
    {
        return actor.FeaturesByType<IHealingModificationProvider>()
            .Any(x => x.MaximizeReceivedHealing);
    }
}
