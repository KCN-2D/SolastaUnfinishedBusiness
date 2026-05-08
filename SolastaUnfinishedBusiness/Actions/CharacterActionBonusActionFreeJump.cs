using System.Collections;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using static ActionDefinitions;

//This should have default namespace so that it can be properly created by `CharacterActionPatcher`
// ReSharper disable once CheckNamespace
[UsedImplicitly]
#pragma warning disable CA1050
public class CharacterActionBonusActionFreeJump(CharacterActionParams actionParams) : CharacterAction(actionParams)
#pragma warning restore CA1050
{
    public override IEnumerator ExecuteImpl()
    {
        var destination = ActionParams.Positions.Count > 0
            ? ActionParams.Positions[0]
            : ActingCharacter.LocationPosition;

        if (!FreeJumpContext.CanExecuteBonusActionMove(ActingCharacter) ||
            !FreeJumpContext.CanReach(ActingCharacter, ActingCharacter.LocationPosition, destination))
        {
            yield break;
        }

        var moveParams = new CharacterActionParams(
            ActingCharacter,
            Id.TacticalMove,
            MoveStance.Run,
            destination,
            ActionParams.Orientation)
        {
            BoolParameter3 = false,
            BoolParameter5 = false
        };

        FreeJumpContext.MarkBonusActionMove(moveParams);
        ResultingActions.Add(new CharacterActionMove(moveParams));
    }
}
