using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using TA;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal sealed class WardingBondBehavior(ConditionDefinition sourceCondition) :
    IDamageReceived, IEffectCharacterChange, IOnEffectConditionTracked, IOnLocationCharacterRestored
{
    private readonly HashSet<RulesetEffect> _creatingSourceConditions = [];

    public int Priority => 0;

    internal static void Load()
    {
        if (WardingBond.HasSubFeatureOfType<WardingBondBehavior>())
        {
            return;
        }

        var sourceCondition = ConditionDefinitionBuilder
            .Create("ConditionWardingBondSource")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddCustomSubFeatures(AddUsablePowersFromCondition.Marker)
            .AddToDB();
        sourceCondition.terminateWhenRemoved = true;

        var dismiss = FeatureDefinitionPowerBuilder
            .Create("PowerWardingBondDismiss")
            .SetGuiPresentation(Category.Feature, WardingBond)
            .SetUsesFixed(ActivationTime.Action)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetEffectForms(EffectFormBuilder.ConditionForm(sourceCondition, ConditionForm.ConditionOperation.Remove))
                .Build())
            .AddToDB();
        sourceCondition.Features.Add(dismiss);

        var behavior = new WardingBondBehavior(sourceCondition);
        WardingBond.AddCustomSubFeatures(behavior);
        ConditionWardedByWardingBond.AddCustomSubFeatures(behavior);
        sourceCondition.AddCustomSubFeatures(behavior);

        // Complete the native spell in place; existing saves and scrolls keep their definitions.
        WardingBond.EffectDescription.targetExcludeCaster = true;
        ConditionWardedByWardingBond.terminateWhenRemoved = true;
        ConditionWardedByWardingBond.GuiPresentation.title = WardingBond.GuiPresentation.Title;
        ConditionWardedByWardingBond.GuiPresentation.description = WardingBond.GuiPresentation.Description;
        ConditionWardedByWardingBond.features = ConditionWardedByWardingBond.Features.Distinct().ToList();
    }

    public void OnDamageReceived(RulesetCharacter character, int damage, string damageType, ulong sourceGuid)
    {
        foreach (var effect in EffectCharacterChange.EnumerateEffectsInvolving(character)
                     .Where(IsBond).ToArray())
        {
            if (!HasWardedTarget(effect, character) || !Validate(effect))
            {
                continue;
            }

            var caster = EffectHelpers.GetCharacterByGuid(effect.SourceGuid);

            DamageReceivedContext.ShareDamage(effect, caster, damage, damageType, sourceGuid);
        }
    }

    public void OnCharacterChanged(RulesetEffect effect, RulesetCharacter character)
    {
        if (Validate(effect))
        {
            EnsureSourceCondition(effect, EffectHelpers.GetCharacterByGuid(effect.SourceGuid));
        }
    }

    public void OnConditionTracked(
        RulesetEffect effect, RulesetCharacter source, RulesetActor target, RulesetCondition condition)
    {
        if (condition.ConditionDefinition != ConditionWardedByWardingBond ||
            target is not RulesetCharacter character)
        {
            return;
        }

        // Casting on either endpoint ends the previous connection, including a bond
        // cast by somebody else. Act only once the new cast actually applied its condition.
        foreach (var previous in EffectCharacterChange.EnumerateEffectsInvolving(character)
                     .Where(previous => previous != effect && IsBond(previous)).ToArray())
        {
            previous.DoTerminate();
        }

        if (Validate(effect))
        {
            EnsureSourceCondition(effect, source);
        }
    }

    public void OnLocationCharacterRestored(RulesetCharacter character)
    {
        foreach (var effect in EffectCharacterChange.EnumerateEffectsInvolving(character)
                     .Where(IsBond).ToArray())
        {
            if (Validate(effect))
            {
                EnsureSourceCondition(effect, EffectHelpers.GetCharacterByGuid(effect.SourceGuid));
            }
        }
    }

    private bool IsBond(RulesetEffect effect)
    {
        return effect?.GetSourceDefinitionSafe()?.GetFirstSubFeatureOfType<WardingBondBehavior>() == this;
    }

    private static bool HasWardedTarget(RulesetEffect effect, RulesetCharacter character)
    {
        return character.ConditionsByCategory.Values.SelectMany(conditions => conditions)
            .Any(condition => condition.ConditionDefinition == ConditionWardedByWardingBond &&
                              effect.TrackedConditionGuids.Contains(condition.Guid));
    }

    private static bool Validate(RulesetEffect effect)
    {
        if (effect.Terminated || ServiceRepository.GetService<IGameSerializationService>()?.Loading == true)
        {
            return false;
        }

        var caster = EffectHelpers.GetCharacterByGuid(effect.SourceGuid);

        if (caster == null)
        {
            return false;
        }

        if (caster.CurrentHitPoints <= 0)
        {
            effect.DoTerminate(caster);
            return false;
        }

        var casterLocation = GameLocationCharacter.GetFromActor(caster);

        foreach (var conditionGuid in effect.TrackedConditionGuids.ToArray())
        {
            if (!RulesetEntity.TryGetEntity<RulesetCondition>(conditionGuid, out var condition) ||
                condition.ConditionDefinition != ConditionWardedByWardingBond ||
                EffectHelpers.GetCharacterByGuid(condition.TargetGuid) is not { } target)
            {
                continue;
            }

            var targetLocation = GameLocationCharacter.GetFromActor(target);

            // Missing location entities are normal while travelling/restoring a save.
            if (caster == target || casterLocation != null && targetLocation != null &&
                casterLocation.LocationPosition != int3.invalid && targetLocation.LocationPosition != int3.invalid &&
                !casterLocation.IsWithinRange(targetLocation, 12))
            {
                effect.DoTerminate(caster);
                return false;
            }
        }

        return true;
    }

    private void EnsureSourceCondition(RulesetEffect effect, RulesetCharacter source)
    {
        // Shape changing transfers the effect before its conditions. The tracked marker
        // already exists during that transition, even while it is still on the old form.
        if (source == null || effect.TrackedConditionGuids.Any(guid =>
                RulesetEntity.TryGetEntity<RulesetCondition>(guid, out var tracked) &&
                tracked.ConditionDefinition == sourceCondition) || !_creatingSourceConditions.Add(effect))
        {
            return;
        }

        // Inflicting the marker notifies active effects before TrackCondition completes.
        // Keep that notification from starting a second creation of the same marker.
        try
        {
            var condition = source.InflictCondition(
                sourceCondition.Name,
                DurationType.Round,
                effect.RemainingRounds,
                TurnOccurenceType.EndOfTurnNoPerceptionOfSource,
                AttributeDefinitions.TagEffect,
                source.Guid,
                source.CurrentFaction.Name,
                effect.EffectLevel,
                effect.SourceDefinition.Name,
                0,
                0,
                0);
            effect.TrackCondition(source, source.Guid, source, source.Guid, condition, AttributeDefinitions.TagEffect);
        }
        finally
        {
            _creatingSourceConditions.Remove(effect);
        }
    }
}
