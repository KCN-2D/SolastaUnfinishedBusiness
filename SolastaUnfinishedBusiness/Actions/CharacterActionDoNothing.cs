using System.Collections;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

//This should have default namespace so that it can be properly created by `CharacterActionPatcher`
// ReSharper disable once CheckNamespace
[UsedImplicitly]
#pragma warning disable CA1050
public class CharacterActionDoNothing(CharacterActionParams actionParams) : CharacterAction(actionParams)
#pragma warning restore CA1050
{
    private static readonly ConditionalWeakTable<CharacterActionParams, Func<IEnumerable<CharacterAction>>>
        FollowUps = new();

    internal static void SetResultingActions(CharacterActionParams parameters,
        Func<IEnumerable<CharacterAction>> actions)
    {
        FollowUps.Remove(parameters);
        if (actions != null) FollowUps.Add(parameters, actions);
    }

    public override IEnumerator ExecuteImpl()
    {
        if (FollowUps.TryGetValue(ActionParams, out var actions))
        {
            FollowUps.Remove(ActionParams);
            ResultingActions.AddRange(actions());
        }
        yield break;
    }
}
