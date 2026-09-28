using System;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;

namespace SolastaUnfinishedBusiness.Behaviors;

internal sealed class AbilityCheckAbilityReplacement(
    string abilityScore,
    Func<RulesetCharacter, bool> isValid,
    params string[] skills)
{
    internal static string Resolve(RulesetCharacter character, string abilityScoreName, string proficiencyName)
    {
        if (character == null)
        {
            return abilityScoreName;
        }

        // Optional ability substitutions retain the original ability when its modifier is higher.
        foreach (var replacement in character.GetSubFeaturesByType<AbilityCheckAbilityReplacement>())
        {
            if (replacement.Applies(character, proficiencyName) &&
                character.TryGetAttributeValue(replacement.AbilityScore) >=
                character.TryGetAttributeValue(abilityScoreName))
            {
                abilityScoreName = replacement.AbilityScore;
            }
        }

        return abilityScoreName;
    }

    private string AbilityScore => abilityScore;

    private bool Applies(RulesetCharacter character, string proficiencyName)
    {
        return skills.Contains(proficiencyName) && isValid(character);
    }
}
