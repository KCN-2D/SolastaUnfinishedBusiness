using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class BarbarianRage2024Context
{
    private static readonly RageBehavior Behavior = new();
    private static readonly ConditionDefinition Renewal = ConditionDefinitionBuilder
        .Create("ConditionBarbarianRageRenewal2024")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetFixedAmount(2)
        .AddToDB();

    private static readonly FeatureDefinitionPower ExtendRage = FeatureDefinitionPowerBuilder
        .Create("PowerBarbarianExtendRage2024")
        .SetGuiPresentation(Category.Feature,
            SolastaUnfinishedBusiness.Api.DatabaseHelper.ActionDefinitions.RageStart.GuiPresentation.SpriteReference)
        .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.AtWill)
        .SetShowCasting(false)
        .SetEffectDescription(EffectDescriptionBuilder.Create()
            .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self).Build())
        .AddCustomSubFeatures(new ValidatorsValidatePowerUse(character =>
            Main.Settings.EnableBarbarianRage2024 && character.HasConditionOfType(ConditionRagingNormal)))
        .AddToDB();

    private static readonly Dictionary<ConditionDefinition, (int Duration, ConditionInterruption[] Interruptions)>
        RageConditions = new();
    private static readonly Dictionary<FeatureDefinitionPower, int> RagePowerDurations = new();
    private static bool _loaded;

    internal static void Switch()
    {
        if (!_loaded)
        {
            ConditionRagingNormal.AddCustomSubFeatures(Behavior, AddUsablePowersFromCondition.Marker);
            FeatureDefinitionAdditionalDamages.AdditionalDamageConditionRaging.AddCustomSubFeatures(
                new ValidateContextInsteadOfRestrictedProperty((_, _, _, _, _, attackMode, _) =>
                    Main.Settings.EnableBarbarianRage2024
                        ? (OperationType.Set, attackMode?.AbilityScore == AttributeDefinitions.Strength &&
                                              (attackMode.SourceDefinition is ItemDefinition { IsWeapon: true } ||
                                               ValidatorsWeapon.IsUnarmed(attackMode)))
                        : (OperationType.Ignore, false)));
            ConditionRagingPersistent.AddCustomSubFeatures(Behavior);
            foreach (var condition in DatabaseRepository.GetDatabase<ConditionDefinition>().Where(condition =>
                         condition == ConditionRagingNormal || condition == ConditionRagingPersistent ||
                         condition.HasSpecialInterruptionOfType(ConditionInterruption.RageStop)))
            {
                RageConditions.Add(condition, (condition.DurationParameter, condition.SpecialInterruptions.ToArray()));
            }

            foreach (var power in DatabaseRepository.GetDatabase<FeatureDefinitionPower>().Where(power =>
                         power.EffectDescription.DurationType == DurationType.Minute &&
                         power.EffectDescription.DurationParameter == 1 &&
                         power.EffectDescription.EffectForms.GetAppliedConditionDefinitions().Any(condition =>
                             condition.HasSpecialInterruptionOfType(ConditionInterruption.RageStop))))
            {
                RagePowerDurations.Add(power, power.EffectDescription.DurationParameter);
            }

            _loaded = true;
        }

        var enabled = Main.Settings.EnableBarbarianRage2024;
        var persistent = Main.Settings.EnableBarbarianPersistentRage2024;
        var extended = enabled || persistent;

        foreach (var entry in RageConditions)
        {
            var condition = entry.Key;
            var original = entry.Value;
            condition.SpecialInterruptions.SetRange(original.Interruptions);
            var extend = condition == ConditionRagingNormal ? enabled : extended;

            // Dependent benefits end through RageStop. Separate inactivity/battle-end checks
            // would remove Frenzy and similar benefits before their owner's Rage ends.
            if (extend)
            {
                condition.SpecialInterruptions.Remove(ConditionInterruption.NoAttackOrDamagedInTurn);
                condition.SpecialInterruptions.Remove(ConditionInterruption.BattleEnd);
            }

            condition.durationParameter = extend && condition.SpecialDuration &&
                                          condition.DurationType == DurationType.Minute && original.Duration == 1
                ? 10
                : original.Duration;
        }

        foreach (var entry in RagePowerDurations)
        {
            entry.Key.EffectDescription.durationParameter = extended ? 10 : entry.Value;
        }

        ConditionRagingNormal.Features.Remove(ExtendRage);
        if (enabled)
        {
            ConditionRagingNormal.Features.Add(ExtendRage);
        }

        var description = enabled ? "Feature/&FeatureSetRageExtendedDescription" : "Action/&RageStartDescription";
        ConditionRagingNormal.GuiPresentation.description = description;
        FeatureDefinitionPowers.PowerBarbarianRageStart.GuiPresentation.description = description;
        var persistentDescription = (enabled, persistent) switch
        {
            (true, true) => "Action/&PersistentRageStartAll2024Description",
            (true, false) => "Action/&PersistentRageStartRage2024Description",
            (false, true) => "Action/&PersistentRageStartExtendedDescription",
            _ => "Action/&PersistentRageStartDescription"
        };
        ConditionRagingPersistent.GuiPresentation.description = persistentDescription;
        FeatureDefinitionPowers.PowerBarbarianPersistentRageStart.GuiPresentation.description = persistentDescription;

        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return;
        }

        foreach (var character in characterService.PartyCharacters)
        {
            UpdateCharacter(character.RulesetCharacter);
        }
    }

    internal static void UpdateCharacter(RulesetCharacter character)
    {
        if (Main.Settings.EnableBarbarianRage2024 && character.HasConditionOfType(ConditionRagingNormal))
        {
            if (character.UsablePowers.All(power => power.PowerDefinition != ExtendRage))
            {
                character.UsablePowers.Add(PowerProvider.Get(ExtendRage, character));
            }
        }
        else
        {
            character.UsablePowers.RemoveAll(power => power.PowerDefinition == ExtendRage);
            character.RemoveAllConditionsOfCategoryAndType(AttributeDefinitions.TagEffect, Renewal.Name);
        }
    }

    internal static void Renew(RulesetCharacter character, bool starting = false)
    {
        if (!Main.Settings.EnableBarbarianRage2024 ||
            !starting && Gui.Battle != null && Gui.Battle.ActiveContender?.RulesetCharacter != character ||
            !character.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                ConditionRagingNormal.Name, out var rage))
        {
            return;
        }

        if (!character.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect, Renewal.Name, out var renewal))
        {
            renewal = character.InflictCondition(Renewal.Name, DurationType.Minute, 10,
                TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect, character.Guid,
                character.CurrentFaction.Name, 1, Renewal.Name, 0, 0, 0);
        }

        // Persist the number of this character's turn endings, not a process-local battle counter.
        renewal.Amount = Gui.Battle?.ActiveContender?.RulesetCharacter == character ? 2 : 1;
        renewal.RemainingRounds = rage.RemainingRounds;
    }

    internal static void CheckTermination(RulesetCharacter character)
    {
        if (character == null)
        {
            return;
        }

        foreach (var rage in character.ConditionsByCategory.SelectMany(entry => entry.Value)
                     .Where(condition => condition.ConditionDefinition == ConditionRagingNormal ||
                                         condition.ConditionDefinition == ConditionRagingPersistent).ToArray())
        {
            var persistent = rage.ConditionDefinition == ConditionRagingPersistent;
            if (!(Main.Settings.EnableBarbarianRage2024 ||
                  persistent && Main.Settings.EnableBarbarianPersistentRage2024))
            {
                continue;
            }

            if (character.IsWearingHeavyArmor() ||
                (persistent ? character.IsDeadOrDyingOrUnconscious : character.IsIncapacitated))
            {
                character.RemoveCondition(rage);
            }
        }
    }

    private sealed class RageBehavior : IOnConditionAddedOrRemoved, ICharacterBeforeTurnEndListener,
        IPhysicalAttackFinishedByMe, IMagicEffectFinishedByMe, IOnCharacterEquipmentChanged
    {
        public void OnConditionAdded(RulesetCharacter target, RulesetCondition condition)
        {
            if (condition.ConditionDefinition == ConditionRagingNormal)
            {
                Renew(target, starting: true);
            }
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition condition)
        {
            if (condition.ConditionDefinition == ConditionRagingNormal)
            {
                target.RemoveAllConditionsOfCategoryAndType(AttributeDefinitions.TagEffect, Renewal.Name);
            }
        }

        public void OnCharacterBeforeTurnEnded(GameLocationCharacter character)
        {
            var ruleset = character.RulesetCharacter;
            CheckTermination(ruleset);
            if (!Main.Settings.EnableBarbarianRage2024 ||
                !ruleset.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                    ConditionRagingNormal.Name, out var rage))
            {
                return;
            }

            if (!ruleset.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect, Renewal.Name, out var renewal))
            {
                // An active Rage saved before this rule was enabled gets its next turn to renew.
                Renew(ruleset);
                return;
            }

            if (--renewal.Amount <= 0)
            {
                ruleset.RemoveCondition(rage);
            }
        }

        public IEnumerator OnPhysicalAttackFinishedByMe(GameLocationBattleManager battleManager,
            CharacterAction action, GameLocationCharacter attacker, GameLocationCharacter defender,
            RulesetAttackMode attackMode, RollOutcome rollOutcome, int damageAmount)
        {
            if (attacker.Side != defender.Side)
            {
                Renew(attacker.RulesetCharacter);
            }
            yield break;
        }

        public IEnumerator OnMagicEffectFinishedByMe(CharacterAction action, GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (action is CharacterActionMagicEffect magicEffect && (magicEffect.Countered || magicEffect.ExecutionFailed))
            {
                yield break;
            }

            if (action.ActionParams.RulesetEffect?.SourceDefinition == ExtendRage ||
                action is CharacterActionMagicEffect { AttackRoll: > 0 } &&
                targets.Any(target => target.Side != attacker.Side))
            {
                Renew(attacker.RulesetCharacter);
            }
            yield break;
        }

        public void OnCharacterEquipmentChanged(RulesetCharacter character) => CheckTermination(character);
    }
}
