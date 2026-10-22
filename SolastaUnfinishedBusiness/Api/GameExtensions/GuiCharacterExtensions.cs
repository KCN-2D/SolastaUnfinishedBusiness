namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class GuiCharacterExtensions
{
    internal static string GetIdentityName(this GuiCharacter character) =>
        character.RulesetCharacterHero?.Name ?? character.Snapshot?.Name ?? character.Name;
}
