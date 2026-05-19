using System.Collections.Generic;
using JetBrains.Annotations;
using TA;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal static class MovementTracker
{
    private static readonly Dictionary<ulong, (int3, int3)> StepMovementCache = [];
    private static readonly Dictionary<ulong, (int3, int3)> MoveEndMovementCache = [];

    internal static bool TryGetMovement(ulong guid, out (int3 from, int3 to) movement)
    {
        if (StepMovementCache.TryGetValue(guid, out movement) ||
            MoveEndMovementCache.TryGetValue(guid, out movement))
        {
            return true;
        }

        movement = (int3.invalid, int3.invalid);

        return false;
    }

    internal static bool TryConsumeMovement(ulong guid, out (int3 from, int3 to) movement)
    {
        if (StepMovementCache.TryGetValue(guid, out movement))
        {
            StepMovementCache.Remove(guid);

            return true;
        }

        if (MoveEndMovementCache.TryGetValue(guid, out movement))
        {
            MoveEndMovementCache.Remove(guid);

            return true;
        }

        movement = (int3.invalid, int3.invalid);

        return false;
    }

    internal static void ClearStepMovementCache()
    {
        StepMovementCache.Clear();
    }

    internal static void ClearMovement(GameLocationCharacter mover)
    {
        if (mover == null)
        {
            return;
        }

        StepMovementCache.Remove(mover.Guid);
        MoveEndMovementCache.Remove(mover.Guid);
    }

    internal static void RecordMovement([NotNull] GameLocationCharacter mover, int3 destination)
    {
        RecordMovement(mover, mover.LocationPosition, destination);
    }

    internal static void RecordMovement([NotNull] GameLocationCharacter mover, int3 source, int3 destination)
    {
        StepMovementCache.AddOrReplace(mover.Guid, (source, destination));
    }

    internal static void RecordMoveEndMovement([NotNull] GameLocationCharacter mover, int3 source, int3 destination)
    {
        MoveEndMovementCache.AddOrReplace(mover.Guid, (source, destination));
    }

    internal static void CleanMovementCache()
    {
        StepMovementCache.Clear();
        MoveEndMovementCache.Clear();
    }
}
