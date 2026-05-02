using System.Collections.Generic;
using JetBrains.Annotations;
using TA;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal static class MovementTracker
{
    private static readonly Dictionary<ulong, List<int3>> MovementCache = [];

    internal static bool TryGetMovement(ulong guid, out (int3, int3) movement)
    {
        if (TryGetMovementPath(guid, out var path))
        {
            movement = (path[0], path[path.Count - 1]);

            return true;
        }

        movement = (int3.invalid, int3.invalid);

        return false;
    }

    internal static bool TryGetMovementPath(ulong guid, out IReadOnlyList<int3> path)
    {
        if (MovementCache.TryGetValue(guid, out var movementPath) && movementPath.Count >= 2)
        {
            path = movementPath;

            return true;
        }

        path = [];

        return false;
    }

    internal static void RecordMovement([NotNull] GameLocationCharacter mover, int3 destination)
    {
        if (!MovementCache.TryGetValue(mover.Guid, out var movementPath))
        {
            movementPath = [mover.LocationPosition];
            MovementCache.Add(mover.Guid, movementPath);
        }
        else
        {
            AddPositionIfChanged(movementPath, mover.LocationPosition);
        }

        AddPositionIfChanged(movementPath, destination);
    }

    internal static void CleanMovementCache()
    {
        MovementCache.Clear();
    }

    private static void AddPositionIfChanged(List<int3> movementPath, int3 position)
    {
        if (movementPath.Count == 0 || !movementPath[movementPath.Count - 1].Equals(position))
        {
            movementPath.Add(position);
        }
    }
}
