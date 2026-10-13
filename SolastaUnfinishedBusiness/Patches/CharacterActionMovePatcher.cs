using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using TA;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionMovePatcher
{
    [HarmonyPatch(typeof(CharacterActionMoveStepBase), nameof(CharacterActionMoveStepBase.WaitForCanMove))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class WaitForCanMove_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterActionMoveStepBase __instance, int3 position)
        {
            if (__instance is CharacterActionMoveStepWalk || __instance.ActingCharacter is not { } mover)
            {
                // Walk steps select their exact mode in the existing step-start patch.
                return;
            }

            // Other steps can follow flight. Resolve the destination's path mode
            // before reactions, rather than retaining the preceding step's mode.
            var moveMode = mover.DefaultMoveMode;

            foreach (var step in __instance.MovePath)
            {
                if (step.position != position)
                {
                    continue;
                }

                moveMode = step.moveMode;
                break;
            }

            mover.CurrentMoveMode = moveMode;
        }
    }

    [HarmonyPatch(typeof(CharacterActionMove), nameof(CharacterActionMove.ExecuteImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteImpl_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, CharacterActionMove __instance)
        {
            return Process(values, __instance);
        }

        private static IEnumerator Process(IEnumerator values, CharacterActionMove action)
        {
            var character = action?.ActingCharacter;
            var start = character?.LocationPosition ?? default;
            var target = action?.DestinationPosition ?? default;
            var trackMoveResult = character != null && action != null;

            if (CombatAiContext.ShouldCancelAiTacticalMove(action, start, target))
            {
                (values as IDisposable)?.Dispose();
                yield break;
            }

            var wrapped = FreeJumpContext.WithMovePathfindingScope(values, action);

            var completed = false;

            try
            {
                while (wrapped.MoveNext())
                {
                    yield return wrapped.Current;
                }

                completed = true;
            }
            finally
            {
                try
                {
                    (wrapped as IDisposable)?.Dispose();
                }
                finally
                {
                    if (!completed && trackMoveResult)
                    {
                        try
                        {
                            CombatAiContext.RecordAiMoveResult(action, start, target);
                        }
                        finally
                        {
                            MovementTracker.ClearMovement(character);
                        }
                    }
                }
            }

            if (!trackMoveResult)
            {
                yield break;
            }

            var settleFrames = 0;
            var forceCloseNoMoveAfterSettling = false;

            if (CombatAiContext.TryGetActionLinkedMoveResultSettlingFrames(
                    action,
                    start,
                    target,
                    out var maxSettleFrames,
                    out var waitForExpectedDestination))
            {
                while (settleFrames < maxSettleFrames &&
                       (waitForExpectedDestination
                           ? character.LocationPosition != target
                           : character.LocationPosition == start))
                {
                    settleFrames++;
                    yield return null;
                }

                forceCloseNoMoveAfterSettling =
                    settleFrames >= maxSettleFrames &&
                    character.LocationPosition == start;
            }

            CombatAiContext.RecordAiMoveResult(
                action,
                start,
                target,
                forceCloseNoMoveAfterSettling);

            var extraAoOEvents = AttacksOfOpportunity.ProcessOnCharacterMoveEnd(character);
            using var extraAoOIterator = extraAoOEvents as IDisposable;

            while (extraAoOEvents.MoveNext())
            {
                yield return extraAoOEvents.Current;
            }
        }
    }
}
