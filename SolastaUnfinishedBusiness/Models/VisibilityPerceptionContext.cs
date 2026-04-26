using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static class VisibilityPerceptionContext
{
    private const float OriginMaxEpsilon = 0.15f;
    private const float DestinationMaxEpsilon = 0.15f;

    internal static bool HasPerceptionLineOfSight(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 originCell,
        int3 destinationCell)
    {
        if (TryRay(visibilityManager, origin, destination))
        {
            return true;
        }

        if (!Main.Settings.EnableCeilingAwareTargetPerception ||
            originCell.y <= destinationCell.y)
        {
            return false;
        }

        return TryCeilingAdjustedLineOfSight(
            visibilityManager,
            origin,
            destination,
            originCell,
            destinationCell);
    }

    private static bool TryCeilingAdjustedLineOfSight(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 originCell,
        int3 destinationCell)
    {
        var originEpsilon = ComputeCellHeightEpsilon(visibilityManager, originCell, OriginMaxEpsilon);
        var destinationEpsilon = ComputeCellHeightEpsilon(visibilityManager, destinationCell, DestinationMaxEpsilon);

        if (TryAdaptedOriginRay(visibilityManager, origin, destination, originCell, destinationCell) ||
            TryLoweredOriginRay(visibilityManager, origin, destination, originCell, originEpsilon) ||
            TryOffsetOriginRays(visibilityManager, origin, destination, originCell, originEpsilon) ||
            TryDestinationHeightRays(visibilityManager, origin, destination, destinationCell, destinationEpsilon))
        {
            return true;
        }

        var loweredOrigin = origin + Vector3.down * originEpsilon;
        var raisedDestination = destination + Vector3.up * destinationEpsilon;

        return IsSameGridCell(visibilityManager, loweredOrigin, originCell) &&
               IsSameGridCell(visibilityManager, raisedDestination, destinationCell) &&
               TryRay(visibilityManager, loweredOrigin, raisedDestination);
    }

    private static bool TryAdaptedOriginRay(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 originCell,
        int3 destinationCell)
    {
        var adaptedOrigin = origin;

        visibilityManager.AdaptRayForVerticalityAndDiagonals(
            originCell,
            destinationCell,
            ref adaptedOrigin,
            true);

        return TryOriginRay(visibilityManager, adaptedOrigin, destination, originCell);
    }

    private static bool TryLoweredOriginRay(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 originCell,
        float epsilon)
    {
        return TryOriginRay(
                   visibilityManager,
                   origin + Vector3.down * epsilon,
                   destination,
                   originCell) ||
               TryOriginRay(
                   visibilityManager,
                   origin + Vector3.down * epsilon * 2f,
                   destination,
                   originCell);
    }

    private static bool TryOffsetOriginRays(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 originCell,
        float epsilon)
    {
        var direction = destination - origin;
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        direction.Normalize();

        var side = new Vector3(-direction.z, 0f, direction.x);
        var loweredOrigin = origin + Vector3.down * epsilon;

        return TryOriginRay(visibilityManager, loweredOrigin + direction * epsilon, destination, originCell) ||
               TryOriginRay(visibilityManager, loweredOrigin - direction * epsilon, destination, originCell) ||
               TryOriginRay(visibilityManager, loweredOrigin + side * epsilon, destination, originCell) ||
               TryOriginRay(visibilityManager, loweredOrigin - side * epsilon, destination, originCell);
    }

    private static bool TryDestinationHeightRays(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 destinationCell,
        float epsilon)
    {
        return TryDestinationRay(
                   visibilityManager,
                   origin,
                   destination + Vector3.up * epsilon,
                   destinationCell) ||
               TryDestinationRay(
                   visibilityManager,
                   origin,
                   destination + Vector3.up * epsilon * 2f,
                   destinationCell);
    }

    private static bool TryOriginRay(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 originCell)
    {
        return IsSameGridCell(visibilityManager, origin, originCell) &&
               TryRay(visibilityManager, origin, destination);
    }

    private static bool TryDestinationRay(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination,
        int3 destinationCell)
    {
        return IsSameGridCell(visibilityManager, destination, destinationCell) &&
               TryRay(visibilityManager, origin, destination);
    }

    private static bool TryRay(
        GameLocationVisibilityManager visibilityManager,
        Vector3 origin,
        Vector3 destination)
    {
        return !visibilityManager.gameLocationPositioningService.RaycastGridSightBlocker(
            origin,
            destination,
            visibilityManager.GameLocationService);
    }

    private static bool IsSameGridCell(
        GameLocationVisibilityManager visibilityManager,
        Vector3 position,
        int3 cell)
    {
        return visibilityManager.gameLocationPositioningService.GetGridPositionFromWorldPosition(position) == cell;
    }

    private static float ComputeCellHeightEpsilon(
        GameLocationVisibilityManager visibilityManager,
        int3 cell,
        float maxEpsilon)
    {
        var positioningService = visibilityManager.gameLocationPositioningService;
        var origin = positioningService.GetWorldPositionFromGridPosition(cell);
        var upperCell = new int3(cell.x, cell.y + 1, cell.z);
        var upper = positioningService.GetWorldPositionFromGridPosition(upperCell);
        var cellHeight = Mathf.Abs(upper.y - origin.y);

        return Mathf.Clamp(cellHeight * 0.08f, 0.03f, maxEpsilon);
    }
}
