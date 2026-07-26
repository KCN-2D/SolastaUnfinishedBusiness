using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class InventorySubjectResolver
{
    internal static bool TryResolve(
        RulesetContainer container,
        out RulesetCharacter character)
    {
        character = null;

        if (container == null)
        {
            return false;
        }

        if (RulesetCharacterSimulacrum.FindByContainer(container) is { } duplicate)
        {
            character = duplicate;

            return true;
        }

        character = Gui.GameCampaign?.Party?.CharactersList
            .Select(x => x.RulesetCharacter)
            .OfType<RulesetCharacterHero>()
            .FirstOrDefault(x => ReferenceEquals(x.CharacterInventory.PersonalContainer, container));

        return character != null;
    }
}
