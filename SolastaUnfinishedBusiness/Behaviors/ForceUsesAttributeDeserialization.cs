using SolastaUnfinishedBusiness.Api.GameExtensions;

namespace SolastaUnfinishedBusiness.Behaviors;

public class ForceUsesAttributeDeserialization
{
    private ForceUsesAttributeDeserialization()
    {
    }

    public static ForceUsesAttributeDeserialization Mark { get; } = new();

    internal static void Process(RulesetCharacter character)
    {
        if (character == null)
        {
            return;
        }

        var usablePowers = character.UsablePowers;

        if (usablePowers == null)
        {
            return;
        }

        foreach (var usablePower in usablePowers)
        {
            RestoreUsesAttribute(character, usablePower);
        }
    }

    private static void RestoreUsesAttribute(RulesetCharacter character, RulesetUsablePower usablePower)
    {
        var powerDefinition = usablePower?.PowerDefinition;

        if (powerDefinition == null ||
            string.IsNullOrEmpty(powerDefinition.UsesAbilityScoreName) ||
            !powerDefinition.HasSubFeatureOfType<ForceUsesAttributeDeserialization>())
        {
            return;
        }

        usablePower.UsesAttribute = character.GetAttribute(powerDefinition.UsesAbilityScoreName);
    }
}
