using System.Collections.Generic;
using JetBrains.Annotations;
using TA;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal static class MovementTracker
{
    private static readonly Dictionary<ulong, (int3, int3)> MovementCache = [];

    internal static bool TryGetMovement(ulong guid, out (int3 from, int3 to) movement)
    {
        if (MovementCache.TryGetValue(guid, out movement))
        {
            return true;
        }

        movement = (int3.invalid, int3.invalid);

        return false;
    }

    internal static bool TryConsumeMovement(ulong guid, out (int3 from, int3 to) movement)
    {
        if (!TryGetMovement(guid, out movement))
        {
            return false;
        }

        MovementCache.Remove(guid);

        return true;
    }

    internal static void RecordMovement([NotNull] GameLocationCharacter mover, int3 destination)
    {
        RecordMovement(mover, mover.LocationPosition, destination);
    }

    internal static void RecordMovement([NotNull] GameLocationCharacter mover, int3 source, int3 destination)
    {
        var movement = (source, destination);
        MovementCache.AddOrReplace(mover.Guid, movement);
    }

    internal static void CleanMovementCache()
    {
        MovementCache.Clear();
    }
}
