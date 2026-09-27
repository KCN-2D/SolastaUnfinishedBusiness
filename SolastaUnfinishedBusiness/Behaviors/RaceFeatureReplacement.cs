using System;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Validators;

namespace SolastaUnfinishedBusiness.Behaviors;

internal sealed class RaceFeatureReplacement
{
    private readonly CharacterRaceDefinition _race;
    private readonly FeatureDefinition _originalFeature;
    private readonly FeatureDefinitionPower _replacementPower;
    private readonly Func<bool> _isEnabled;

    internal RaceFeatureReplacement(
        CharacterRaceDefinition race,
        FeatureDefinition originalFeature,
        FeatureDefinitionPower replacementPower,
        Func<bool> isEnabled)
    {
        _race = race;
        _originalFeature = originalFeature;
        _replacementPower = replacementPower;
        _isEnabled = isEnabled;

        replacementPower.AddCustomSubFeatures(
            this,
            new ValidatorsValidatePowerUse(IsReplacementActive),
            new ModifyPowerVisibility((character, power, actionType) =>
                IsReplacementActive(character) &&
                ModifyPowerVisibility.Default.IsVisible(character, power, actionType)));
    }

    internal void Apply()
    {
        var activeFeature = _isEnabled() ? _replacementPower : _originalFeature;
        var unlocks = _race.FeatureUnlocks;

        for (var i = 0; i < unlocks.Count; i++)
        {
            var unlock = unlocks[i];

            if ((unlock.FeatureDefinition == _originalFeature ||
                 unlock.FeatureDefinition == _replacementPower) &&
                unlock.FeatureDefinition != activeFeature)
            {
                // Replace the unlock rather than mutating a potentially shared cloned entry.
                unlocks[i] = new FeatureUnlockByLevel(activeFeature, unlock.Level);
            }
        }

        var entityService = ServiceRepository.GetService<IRulesetEntityService>();

        if (entityService == null)
        {
            return;
        }

        foreach (var hero in entityService.RulesetEntities.Values
                     .OfType<RulesetCharacterHero>()
                     .Where(MatchesRace)
                     .ToArray())
        {
            // The common RefreshAll prefix also handles new heroes and deserialized saves.
            hero.RefreshAll();
        }
    }

    internal void Synchronize(RulesetCharacterHero hero)
    {
        if (hero == null || !MatchesRace(hero) ||
            !hero.ActiveFeatures.TryGetValue(AttributeDefinitions.TagRace, out var raceFeatures) ||
            !raceFeatures.Any(feature => feature == _originalFeature || feature == _replacementPower))
        {
            return;
        }

        var enabled = _isEnabled();
        var activeFeature = enabled ? _replacementPower : _originalFeature;
        var inactiveFeature = enabled ? _originalFeature : _replacementPower;
        var featureIndex = raceFeatures.IndexOf(inactiveFeature);

        if (featureIndex >= 0)
        {
            raceFeatures[featureIndex] = activeFeature;
        }

        // Retain the first active entry, removing stale duplicates without touching other origins.
        var foundActive = false;
        raceFeatures.RemoveAll(feature =>
        {
            if (feature == inactiveFeature)
            {
                return true;
            }

            if (feature != activeFeature)
            {
                return false;
            }

            var duplicate = foundActive;
            foundActive = true;
            return duplicate;
        });

        var usablePower = hero.GetPowerFromDefinition(_replacementPower);

        if (usablePower == null && enabled)
        {
            // GrantPowers clears and recharges every power; create only the missing racial power.
            usablePower = PowerProvider.Get(_replacementPower, hero);
            usablePower.OriginRace = _race;
            hero.UsablePowers.Add(usablePower);
        }
        else if (usablePower != null)
        {
            PowerProvider.BindUsesAttribute(hero, usablePower);
        }

        // Keep the disabled instance serialized so toggling or saving cannot restore spent uses.
        // Its visibility and use validators require the replacement feature to remain active.
    }

    internal static bool IsInactiveReplacement(RulesetCharacter character, FeatureDefinition feature)
    {
        var replacement = feature.GetFirstSubFeatureOfType<RaceFeatureReplacement>();

        return replacement != null && !replacement.IsReplacementActive(character);
    }

    private bool MatchesRace(RulesetCharacterHero hero)
    {
        return hero.RaceDefinition == _race;
    }

    private bool IsReplacementActive(RulesetCharacter character)
    {
        return _isEnabled() && character != null && character.HasAnyFeature(_replacementPower);
    }
}
