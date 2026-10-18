using System;
using System.Collections.Generic;
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
    private readonly HashSet<FeatureDefinitionPower> _powers = [];

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

        ConfigurePowers(originalFeature);
        ConfigurePowers(replacementFeature);
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

        foreach (var power in _powers)
        {
            SynchronizePower(hero, power, _characterClass, _race);
        }
    }

    internal static bool IsInactiveReplacement(RulesetCharacter character, FeatureDefinition feature)
    {
        var replacement = feature.GetFirstSubFeatureOfType<CharacterFeatureReplacement>();

        return replacement != null && !replacement.IsFeatureActive(character, feature);
    }

    private void ConfigurePowers(FeatureDefinition feature)
    {
        var pending = new Stack<FeatureDefinition>();
        var visited = new HashSet<FeatureDefinition>();
        pending.Push(feature);

        while (pending.Count > 0)
        {
            feature = pending.Pop();
            if (feature == null || !visited.Add(feature))
            {
                continue;
            }

            if (feature is FeatureDefinitionFeatureSet { Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Union } set)
            {
                foreach (var child in set.FeatureSet)
                {
                    pending.Push(child);
                }
            }

            if (feature is not FeatureDefinitionPower power || !_powers.Add(power))
            {
                continue;
            }

            var visibility = power.GetFirstSubFeatureOfType<ModifyPowerVisibility>() ?? ModifyPowerVisibility.Default;

            power.AddCustomSubFeatures(this,
                new ValidatorsValidatePowerUse(character => IsFeatureActive(character, power)));
            power.SetSubFeatureOfType<ModifyPowerVisibility>(new ModifyPowerVisibility(
                (character, definition, actionType) =>
                    IsFeatureActive(character, power) && visibility.IsVisible(character, definition, actionType),
                visibility.KeepsVisibleWhenUnavailable));
        }
    }

    internal static void SynchronizePower(
        RulesetCharacterHero hero,
        FeatureDefinitionPower power,
        CharacterClassDefinition originClass = null,
        CharacterRaceDefinition originRace = null)
    {
        var active = hero.HasAnyFeature(power);
        if (!active && power.RechargeRate == RuleDefinitions.RechargeRate.AtWill)
        {
            // At-will powers have no spent resource to retain when their last origin disappears.
            hero.UsablePowers.RemoveAll(usable => usable.PowerDefinition == power);
            return;
        }

        // Keep the same matching instance as native lookup, without discarding
        // consumption recorded on another serialized copy of the same power.
        var duplicates = hero.UsablePowers.Where(usable => usable.PowerDefinition == power).ToArray();
        var usablePower = duplicates.LastOrDefault() ?? hero.GetPowerFromDefinition(power);

        if (usablePower == null && active)
        {
            // GrantPowers clears and recharges every power; create only the missing active power.
            usablePower = PowerProvider.Get(power, hero);
            usablePower.OriginRace = originRace;
            usablePower.originClass = originClass;
            hero.UsablePowers.Add(usablePower);
        }
        else if (usablePower != null)
        {
            PowerProvider.BindUsesAttribute(hero, usablePower);
        }

        if (usablePower != null)
        {
            // Shared definitions can have more than one feature origin, but only one usable entry.
            hero.UsablePowers.RemoveAll(usable => usable.PowerDefinition == power && usable != usablePower);

            if (power.RechargeRate != RuleDefinitions.RechargeRate.AtWill && duplicates.Length > 0)
            {
                // Shared wrappers are recalculated from their root; their saved counts
                // are not independent consumption and can use different cost units.
                PowerProvider.RestoreRemainingUses(hero, usablePower,
                    PowerProvider.GetEffectiveMaxUses(hero, usablePower),
                    duplicates.Min(usable => usable.RemainingUses));
            }

            // A repaired root must also update already granted wrappers, without
            // spending/refunding a use or invoking the gameplay/HUD refresh path.
            foreach (var shared in hero.UsablePowers.Where(usable =>
                         usable.PowerDefinition is FeatureDefinitionPowerSharedPool pool &&
                         pool.GetUsagePoolPower() == power))
            {
                PowerProvider.RestoreRemainingUses(hero, shared, 0, 0);
            }
        }

        // Keep disabled resource-bearing instances serialized so toggling cannot restore spent uses.
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
        // A nested power may also come from another class, feat or feature set.
        // Only direct replacement roots are exclusively controlled by this option.
        return character != null && character.HasAnyFeature(feature) &&
               (feature != _originalFeature && feature != _replacementFeature ||
                feature == (_isEnabled() ? _replacementFeature : _originalFeature));
    }
}
