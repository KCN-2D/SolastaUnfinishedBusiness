using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Behaviors;

internal static class EffectCharacterChange
{
    internal static List<RulesetEffect> EnumerateEffectsInvolving(RulesetCharacter character)
    {
        if (character == null || ServiceRepository.GetService<IGameSerializationService>()?.Loading == true)
        {
            return [];
        }

        // Conditions retain their source across saves. Follow that ownership instead of
        // keeping a second registry of the characters participating in an effect.
        var sources = character.ConditionsByCategory.Values
            .SelectMany(conditions => conditions)
            .Select(condition => condition.SourceGuid)
            .Where(guid => guid != character.Guid)
            .Distinct()
            .Select(EffectHelpers.GetCharacterByGuid)
            .Where(source => source != null);

        return character.EnumerateActiveEffectsActivatedByMe()
            .Concat(sources.SelectMany(source => source.EnumerateActiveEffectsActivatedByMe())
                .Where(effect => effect.IsTrackingConditionOnCharacter(character)))
            .Where(effect => !effect.Terminated)
            .Distinct()
            .ToList();
    }

    internal static void Notify(RulesetCharacter character, IReadOnlyList<RulesetEffect> affectedEffects = null)
    {
        if (character == null || ServiceRepository.GetService<IGameSerializationService>()?.Loading == true)
        {
            return;
        }

        var effects = EnumerateEffectsInvolving(character);

        if (affectedEffects != null)
        {
            // A removed condition may have carried the last link to its source's effect.
            effects = effects.Concat(affectedEffects).Distinct().ToList();
        }

        // A handler may terminate an effect and remove its conditions while it runs.
        foreach (var effect in effects)
        {
            if (effect.Terminated)
            {
                continue;
            }

            foreach (var handler in effect.GetSourceDefinitionSafe().GetAllSubFeaturesOfType<IEffectCharacterChange>())
            {
                handler.OnCharacterChanged(effect, character);
            }
        }
    }
}
