using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class ReactionCharacterNameFormatter
{
    private const string QualifiedCreatureNameFormat = "Reaction/&QualifiedCreatureNameFormat";

    internal static string Format(GameLocationCharacter character)
    {
        return Format(character, []);
    }

    private static string Format(
        GameLocationCharacter character,
        HashSet<ulong> visited)
    {
        if (character?.RulesetCharacter is not { } rulesetCharacter)
        {
            return string.Empty;
        }

        var name = new GuiCharacter(character).Name;

        if (character.Guid == 0 || !visited.Add(character.Guid))
        {
            return name;
        }

        var simulacrum = rulesetCharacter as RulesetCharacterSimulacrum ??
                         rulesetCharacter.OriginalFormCharacter as RulesetCharacterSimulacrum;

        if (simulacrum != null &&
            SimulacrumBehavior.TryGetDisplayName(simulacrum, out var copiedName))
        {
            return Qualify(copiedName, Gui.Localize("Monster/&SimulacrumTitle"));
        }

        var summoner = rulesetCharacter.GetMySummoner();

        if (summoner == null || summoner.Guid == character.Guid)
        {
            return name;
        }

        return Qualify(Format(summoner, visited), name);
    }

    private static string Qualify(string qualifier, string name)
    {
        return string.IsNullOrWhiteSpace(qualifier) || string.IsNullOrWhiteSpace(name)
            ? name
            : Gui.Format(QualifiedCreatureNameFormat, qualifier, name);
    }
}
