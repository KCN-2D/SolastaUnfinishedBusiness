using System.Collections;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;

//This should have default namespace so that it can be properly created by `CharacterActionPatcher`
// ReSharper disable once CheckNamespace
[UsedImplicitly]
#pragma warning disable CA1050
public class CharacterActionExplorationFreeJumpToggle(CharacterActionParams actionParams) : CharacterAction(actionParams)
#pragma warning restore CA1050
{
    public override IEnumerator ExecuteImpl()
    {
        var rulesetCharacter = ActingCharacter.RulesetCharacter;

        if (rulesetCharacter.IsToggleEnabled(ActionId))
        {
            rulesetCharacter.DisableToggle(ActionId);
        }
        else
        {
            rulesetCharacter.EnableToggle(ActionId);
        }

        rulesetCharacter.CharacterRefreshed?.Invoke(rulesetCharacter);

        if (ServiceRepository.GetService<ICursorService>()?.CurrentCursor is CursorLocationExplorationCharacter
            { Active: true } cursor)
        {
            // The toggle changes movement feedback, not the character's attack modes.
            // Refresh it immediately for keyboard activation with a stationary pointer too.
            cursor.RefreshHover();
        }

        yield break;
    }
}
