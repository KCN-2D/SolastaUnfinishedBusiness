using System;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Validators;

namespace SolastaUnfinishedBusiness.Behaviors;

internal sealed class CharacterFeatureReplacement
{
    private readonly CharacterRaceDefinition _race;
    private readonly CharacterClassDefinition _characterClass;
    private readonly CharacterSubclassDefinition _subclass;
    private readonly int _featureLevel;
    private readonly FeatureDefinition _originalFeature;
    private readonly FeatureDefinition _replacementFeature;
    private readonly Func<bool> _isEnabled;

    internal CharacterFeatureReplacement(
        CharacterRaceDefinition race,
        FeatureDefinition originalFeature,
        FeatureDefinition replacementFeature,
        Func<bool> isEnabled)
        : this(originalFeature, replacementFeature, isEnabled)
    {
        _race = race;
    }

    internal CharacterFeatureReplacement(
        CharacterClassDefinition characterClass,
        CharacterSubclassDefinition subclass,
        int featureLevel,
        FeatureDefinition originalFeature,
        FeatureDefinition replacementFeature,
        Func<bool> isEnabled)
        : this(originalFeature, replacementFeature, isEnabled)
    {
        _characterClass = characterClass;
        _subclass = subclass;
        _featureLevel = featureLevel;
    }

    private CharacterFeatureReplacement(
        FeatureDefinition originalFeature,
        FeatureDefinition replacementFeature,
        Func<bool> isEnabled)
    {
        _originalFeature = originalFeature;
        _replacementFeature = replacementFeature;
        _isEnabled = isEnabled;

        ConfigurePower(originalFeature);
        ConfigurePower(replacementFeature);
    }

    internal void Apply()
    {
        var activeFeature = _isEnabled() ? _replacementFeature : _originalFeature;
        var unlocks = _race != null ? _race.FeatureUnlocks : _subclass.FeatureUnlocks;

        for (var i = 0; i < unlocks.Count; i++)
        {
            var unlock = unlocks[i];

            if ((unlock.FeatureDefinition == _originalFeature ||
                 unlock.FeatureDefinition == _replacementFeature) &&
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
                     .Where(MatchesOrigin)
                     .ToArray())
        {
            // The common RefreshAll prefix also handles new heroes and deserialized saves.
            hero.RefreshAll();
        }
    }

    internal void Synchronize(RulesetCharacterHero hero)
    {
        if (hero == null || !MatchesOrigin(hero))
        {
            return;
        }

        var tag = _race != null
            ? AttributeDefinitions.TagRace
            : AttributeDefinitions.GetSubclassTag(_characterClass, _featureLevel, _subclass);

        if (!hero.ActiveFeatures.TryGetValue(tag, out var features) ||
            !features.Any(feature => feature == _originalFeature || feature == _replacementFeature))
        {
            return;
        }

        var activeFeature = _isEnabled() ? _replacementFeature : _originalFeature;
        var inactiveFeature = activeFeature == _originalFeature ? _replacementFeature : _originalFeature;
        var featureIndex = features.IndexOf(inactiveFeature);

        if (featureIndex >= 0)
        {
            features[featureIndex] = activeFeature;
        }

        // Retain the first active entry, removing stale duplicates without touching other origins.
        var foundActive = false;
        features.RemoveAll(feature =>
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

        SynchronizePower(hero, _originalFeature, activeFeature);
        SynchronizePower(hero, _replacementFeature, activeFeature);
    }

    internal static bool IsInactiveReplacement(RulesetCharacter character, FeatureDefinition feature)
    {
        var replacement = feature.GetFirstSubFeatureOfType<CharacterFeatureReplacement>();

        return replacement != null && !replacement.IsFeatureActive(character, feature);
    }

    private void ConfigurePower(FeatureDefinition feature)
    {
        if (feature is not FeatureDefinitionPower power)
        {
            return;
        }

        var visibility = power.GetFirstSubFeatureOfType<ModifyPowerVisibility>() ?? ModifyPowerVisibility.Default;

        power.AddCustomSubFeatures(this,
            new ValidatorsValidatePowerUse(character => IsFeatureActive(character, power)));
        power.SetSubFeatureOfType<ModifyPowerVisibility>(new ModifyPowerVisibility(
            (character, definition, actionType) =>
                IsFeatureActive(character, power) && visibility.IsVisible(character, definition, actionType),
            visibility.KeepsVisibleWhenUnavailable));
    }

    private void SynchronizePower(
        RulesetCharacterHero hero,
        FeatureDefinition feature,
        FeatureDefinition activeFeature)
    {
        if (feature is not FeatureDefinitionPower power)
        {
            return;
        }

        var usablePower = hero.GetPowerFromDefinition(power);

        if (usablePower == null && feature == activeFeature)
        {
            // GrantPowers clears and recharges every power; create only the missing active power.
            usablePower = PowerProvider.Get(power, hero);
            usablePower.OriginRace = _race;
            usablePower.originClass = _characterClass;
            hero.UsablePowers.Add(usablePower);
        }
        else if (usablePower != null)
        {
            PowerProvider.BindUsesAttribute(hero, usablePower);
        }

        // Keep disabled instances serialized so toggling or saving cannot restore spent uses.
        // Their visibility and use validators require the corresponding feature to remain active.
    }

    private bool MatchesOrigin(RulesetCharacterHero hero)
    {
        return _race != null
            ? hero.RaceDefinition == _race
            : hero.ClassesAndSubclasses.TryGetValue(_characterClass, out var subclass) &&
              subclass == _subclass &&
              hero.ClassesAndLevels.TryGetValue(_characterClass, out var level) &&
              level >= _featureLevel;
    }

    private bool IsFeatureActive(RulesetCharacter character, FeatureDefinition feature)
    {
        return feature == (_isEnabled() ? _replacementFeature : _originalFeature) &&
               character != null && character.HasAnyFeature(feature);
    }
}
