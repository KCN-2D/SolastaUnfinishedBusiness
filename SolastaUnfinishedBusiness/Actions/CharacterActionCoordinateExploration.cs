using System.Collections;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

// Native action lookup uses the global namespace for mod action classes.
[UsedImplicitly]
#pragma warning disable CA1050
public sealed class CharacterActionCoordinateExploration(CharacterActionParams actionParams) : CharacterAction(actionParams)
#pragma warning restore CA1050
{
    public override IEnumerator ExecuteImpl() => CooperativeExplorationContext.ExecuteBatch(ActionParams);
}
