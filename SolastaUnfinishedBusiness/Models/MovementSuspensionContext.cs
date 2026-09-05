using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine.AddressableAssets;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Models;

internal static class MovementSuspensionContext
{
    internal enum MovementKind
    {
        Flight,
        Levitation
    }

    private const string LegacyTrackerName = "ConditionFlightSuspendedConcentrationTracker";
    private static readonly Dictionary<ConditionDefinition, SuspendedMovement> ActiveConditions = new();
    private static readonly Dictionary<ConditionDefinition, SuspendedMovement> SuspendedConditions = new();
    private static ConditionDefinition _flightSuspended;
    private static ConditionDefinition _levitationSuspended;

    internal static void Load()
    {
        // Retain the old definitions so characters saved with flight suspended can still resume it.
        ConditionDefinitionBuilder.Create(LegacyTrackerName)
            .SetGuiPresentationNoContent()
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialDuration(DurationType.Round, 1)
            .AddToDB();

        _flightSuspended = BuildSuspendedCondition("Flight");
        _levitationSuspended = BuildSuspendedCondition("Levitate");

        BuildAction("FlightSuspend", ExtraActionId.FlightSuspend, MovementKind.Flight, false,
            Sprites.GetSprite("ActionFlightSuspend", Resources.ActionFlightSuspend, 80));
        BuildAction("FlightResume", ExtraActionId.FlightResume, MovementKind.Flight, true,
            Sprites.GetSprite("ActionFlightResume", Resources.ActionFlightResume, 80));
        BuildAction("LevitateSuspend", ExtraActionId.LevitateSuspend, MovementKind.Levitation, false,
            Sprites.GetSprite("ActionFlightSuspend", Resources.ActionFlightSuspend, 80));
        BuildAction("LevitateResume", ExtraActionId.LevitateResume, MovementKind.Levitation, true,
            SpellDefinitions.Levitate.GuiPresentation.SpriteReference);
    }

    internal static void LateLoad()
    {
        // Register both states on every client, irrespective of the current option values.
        // Reverse Gravity inherits ConditionFlying but does not grant voluntary flight.
        foreach (var definition in DatabaseRepository.GetDatabase<ConditionDefinition>().ToArray())
        {
            if (definition == ConditionDefinitions.ConditionLevitate)
            {
                Register(definition, MovementKind.Levitation, _levitationSuspended);
            }
            else if (definition.IsSubtypeOf(ConditionFlying) &&
                     definition.ConditionType == ConditionType.Beneficial &&
                     definition.Features.Any(IsFlightFeature))
            {
                Register(definition, MovementKind.Flight, _flightSuspended);
            }
        }
    }

    private static ConditionDefinition BuildSuspendedCondition(string name)
    {
        return ConditionDefinitionBuilder.Create($"Condition{name}Suspended")
            .SetGuiPresentation($"{name}Suspend", Category.Condition,
                Sprites.GetSprite("ConditionFlightSuspended", Resources.ConditionFlightSuspended, 32))
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();
    }

    private static bool IsFlightFeature(FeatureDefinition feature)
    {
        return feature is FeatureDefinitionMoveMode { MoveMode: MoveMode.Fly } or
            FeatureDefinitionMovementAffinity { CanFlyWithWalkSpeed: true };
    }

    private static void Register(ConditionDefinition original, MovementKind kind, ConditionDefinition parent)
    {
        if (ActiveConditions.ContainsKey(original))
        {
            return;
        }

        var suspended = ConditionDefinitionBuilder.Create(original, $"{original.Name}MovementSuspended")
            .SetGuiPresentation(parent.GuiPresentation)
            .SetParentCondition(parent)
            .SetFeatures(original.Features.Where(feature => !IsFlightFeature(feature) &&
                feature.Name != "MovementAffinityConditionLevitate" &&
                feature != FeatureDefinitionConditionAffinitys.ConditionAffinityProneImmunity).ToArray())
            .CopyParticleReferences(parent)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddCustomSubFeatures(original.GetCustomSubFeatures().ToArray())
            .AddToDB();

        suspended.ConditionTags.Remove("Verticality");
        var movement = new SuspendedMovement(original, suspended, kind);
        ActiveConditions.Add(original, movement);
        SuspendedConditions.Add(suspended, movement);
    }

    private static void BuildAction(string name, ExtraActionId id, MovementKind kind, bool resume,
        AssetReferenceSprite sprite)
    {
        var power = FeatureDefinitionPowerBuilder.Create($"Power{name}")
            .SetGuiPresentation(name, Category.Feature, sprite, 71)
            .SetUsesFixed(ActivationTime.NoCost)
            .DelegatedToAction()
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .UseQuickAnimations()
                .Build())
            .AddCustomSubFeatures(
                new ValidatorsValidatePowerUse(character => CanToggle(character, kind, resume)),
                new ToggleMovement(kind, resume))
            .AddToDB();

        ActionDefinitionBuilder.Create($"Action{name}")
            .SetGuiPresentation(name, Category.Action, sprite, 71)
            .SetActionId(id)
            .OverrideClassName("UsePower")
            .SetActionScope(ActionScope.All)
            .SetActionType(ActionType.NoCost)
            .SetFormType(ActionFormType.Small)
            .SetActivatedPower(power)
            .AddToDB();
    }

    internal static bool CanToggle(RulesetCharacter character, MovementKind kind, bool resume)
    {
        if (character == null || character.IsDeadOrDyingOrUnconscious || character.IsIncapacitated)
        {
            return false;
        }

        // Turning an option off must not strand a character whose effect is already suspended.
        if (!resume && (character is RulesetCharacterMonster ||
                        !(kind == MovementKind.Flight
                            ? Main.Settings.AllowFlightSuspend
                            : Main.Settings.AllowLevitateSuspend)))
        {
            return false;
        }

        return character.ConditionsByCategory.Values.SelectMany(conditions => conditions)
            .Any(condition => Matches(character, condition, kind, resume));
    }

    private static bool Matches(RulesetCharacter character, RulesetCondition condition, MovementKind kind, bool resume)
    {
        if (resume)
        {
            return (kind == MovementKind.Flight && condition.ConditionDefinition == _flightSuspended) ||
                   (SuspendedConditions.TryGetValue(condition.ConditionDefinition, out var suspended) &&
                    suspended.Kind == kind);
        }

        return condition.DurationType != DurationType.Permanent &&
               ActiveConditions.TryGetValue(condition.ConditionDefinition, out var active) && active.Kind == kind &&
               (condition.SourceGuid == character.Guid ||
                (RulesetEntity.TryGetEntity(condition.SourceGuid, out RulesetCharacter source) &&
                 source.Side == character.Side));
    }

    private static void Toggle(RulesetCharacter character, MovementKind kind, bool resume)
    {
        if (!CanToggle(character, kind, resume))
        {
            return;
        }

        foreach (var condition in character.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                     .Where(condition => Matches(character, condition, kind, resume)).ToArray())
        {
            if (condition.ConditionDefinition == _flightSuspended)
            {
                ResumeLegacyFlight(character, condition);
                continue;
            }

            var movement = (resume ? SuspendedConditions : ActiveConditions)[condition.ConditionDefinition];
            ChangeDefinition(character, condition, resume ? movement.Active : movement.Suspended);

            if (!resume && movement.Active.SubsequentOnRemoval != null)
            {
                // Winged Boots provide their normal gentle landing when flight is suspended as well.
                var landing = movement.Active.SubsequentOnRemoval;
                character.InflictCondition(landing.Name, landing.DurationType, landing.DurationParameter,
                    landing.TurnOccurence, AttributeDefinitions.TagEffect, condition.SourceGuid,
                    condition.SourceFactionName, condition.EffectLevel, condition.EffectDefinitionName,
                    landing.BaseAmount, condition.SourceAbilityBonus, condition.SourceProficiencyBonus);
            }
        }

        character.RefreshMoveModes();
        // Levitate normally lowers its target safely when removed. Do this before RefreshAll can start a normal fall.
        GameLocationCharacter.GetFromActor(character)?.CheckCharacterFooting(kind == MovementKind.Levitation && !resume);
        character.RefreshAll();
    }

    private static void ChangeDefinition(RulesetCharacter character, RulesetCondition condition,
        ConditionDefinition definition)
    {
        // Keep the instance, GUID, source, duration and all native effect subscriptions intact.
        // Removing/recreating a condition would end Levitate and lose cancellation/dispel tracking.
        character.ConditionRemovedForVisual?.Invoke(character, condition, false, true);
        condition.ConditionDefinition = definition;
        character.ConditionAdded?.Invoke(character, condition, true);
    }

    internal static void ResumeBeforeReapplication(RulesetActor actor, RulesetCondition incoming)
    {
        if (actor is not RulesetCharacter character ||
            !ActiveConditions.TryGetValue(incoming.ConditionDefinition, out var movement))
        {
            return;
        }

        // Let the native stacking/refresh rules see the original definition when the effect is reapplied.
        foreach (var condition in character.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                     .Where(condition => condition.ConditionDefinition == movement.Suspended).ToArray())
        {
            ChangeDefinition(character, condition, movement.Active);
        }
    }

    internal static string GetTrackingConditionName(BaseDefinition definition)
    {
        return definition is ConditionDefinition condition &&
               SuspendedConditions.TryGetValue(condition, out var movement)
            ? movement.Active.Name
            : definition.Name;
    }

    internal static bool IsControlledLevitationSuspended(RulesetCharacter controller)
    {
        return controller.ConcentratedSpell?.TrackedConditionGuids.Any(guid =>
            RulesetEntity.TryGetEntity(guid, out RulesetCondition condition) &&
            SuspendedConditions.TryGetValue(condition.ConditionDefinition, out var movement) &&
            movement.Kind == MovementKind.Levitation) == true;
    }

    internal static bool IsDurationPaused(RulesetCondition condition)
    {
        return Main.Settings.FlightSuspendWingedBoots && condition.ConditionDefinition != null &&
               SuspendedConditions.TryGetValue(condition.ConditionDefinition, out var movement) &&
               movement.Active.Name == "ConditionFlyingBootsWinged";
    }

    internal static bool IsDurationPaused(RulesetEffect effect)
    {
        return Main.Settings.FlightSuspendWingedBoots && effect.TrackedConditionGuids.Count > 0 &&
               effect.TrackedConditionGuids.All(guid =>
                   RulesetEntity.TryGetEntity(guid, out RulesetCondition condition) && IsDurationPaused(condition));
    }

    private static void ResumeLegacyFlight(RulesetCharacter character, RulesetCondition condition)
    {
        var movement = ActiveConditions.Values.FirstOrDefault(value => value.Active.Name == condition.EffectDefinitionName);
        var trackers = character.ConditionsByCategory.Values.SelectMany(conditions => conditions)
            .Where(candidate => candidate.ConditionDefinition.Name == LegacyTrackerName &&
                                candidate.SourceGuid == condition.SourceGuid).ToArray();
        RulesetEntity.TryGetEntity(condition.SourceGuid, out RulesetCharacter source);
        var effect = source?.FindEffectTrackingCondition(condition);

        if (movement == null || condition.RemainingRounds <= 0 || (trackers.Length > 0 && effect == null))
        {
            character.RemoveCondition(condition);
        }
        else
        {
            condition.effectDefinitionName = effect switch
            {
                RulesetEffectSpell spell => spell.SpellDefinition.Name,
                RulesetEffectPower power => power.PowerDefinition.Name,
                _ => movement.Active.Name
            };
            // Old suspended saves stored only the remaining rounds, including paused Winged Boots.
            condition.durationType = DurationType.Round;
            condition.durationParameter = condition.RemainingRounds;
            ChangeDefinition(character, condition, movement.Active);
        }

        foreach (var tracker in trackers)
        {
            character.RemoveCondition(tracker);
        }
    }

    private sealed class SuspendedMovement(ConditionDefinition active, ConditionDefinition suspended, MovementKind kind)
    {
        internal ConditionDefinition Active { get; } = active;
        internal ConditionDefinition Suspended { get; } = suspended;
        internal MovementKind Kind { get; } = kind;
    }

    private sealed class ToggleMovement(MovementKind kind, bool resume) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            Toggle(action.ActingCharacter.RulesetCharacter, kind, resume);
            yield break;
        }
    }
}
