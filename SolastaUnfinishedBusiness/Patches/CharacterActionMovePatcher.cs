using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionMovePatcher
{
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
                if (!completed && trackMoveResult)
                {
                    CombatAiContext.RecordAiMoveResult(action, start, target);
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
                forceCloseNoMoveAfterSettling,
                settleFrames);
        }
    }
}
