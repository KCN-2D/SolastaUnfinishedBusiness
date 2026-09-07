using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static class VisibilityPerceptionContext
{
    private const float CeilingInset = 0.08f;

    internal static bool HasPerceptionLineOfSight(
        GameLocationVisibilityManager manager,
        Vector3 origin,
        Vector3 destination,
        int3 originCell,
        int3 destinationCell,
        GameLocationCharacter sensor)
    {
        if (TryRay(manager, origin, destination))
        {
            return true;
        }

        if (!Main.Settings.EnableCeilingAwareTargetPerception)
        {
            return false;
        }

        // Correct both endpoints before retrying. Lowering only the observer still
        // fails when the target's detection point is in the invalid layer above a roof.
        var finalOrigin = origin;
        var finalDestination = destination;
        var lowerOrigin = IsActualVisionOrigin(manager, sensor, origin) &&
                          TryLowerClippedPoint(manager, sensor, origin, originCell, out finalOrigin);
        var lowerDestination = TryLowerOccupiedTarget(manager, destination, destinationCell, out finalDestination);

        if (lowerOrigin || lowerDestination)
        {
            originCell = manager.gameLocationPositioningService.GetGridPositionFromWorldPosition(finalOrigin);
            destinationCell = manager.gameLocationPositioningService.GetGridPositionFromWorldPosition(finalDestination);

            // Only samples inside an occupied body column change. Intermediate walls
            // and floors must still pass the native ray test.
            if (TryRay(manager, finalOrigin, finalDestination))
            {
                return true;
            }
        }

        return TryAdaptedRay(manager, finalOrigin, finalDestination, originCell, destinationCell);
    }

    private static bool TryAdaptedRay(GameLocationVisibilityManager manager, Vector3 origin, Vector3 destination,
        int3 originCell, int3 destinationCell)
    {
        if (originCell.y <= destinationCell.y)
        {
            return false;
        }

        var adaptedOrigin = origin;
        manager.AdaptRayForVerticalityAndDiagonals(originCell, destinationCell, ref adaptedOrigin, true);

        // ComputeLineOfSight may already have applied this adjustment. Never
        // accumulate it into a neighbouring cell or move through a blocking face.
        return adaptedOrigin != origin &&
               manager.gameLocationPositioningService.GetGridPositionFromWorldPosition(adaptedOrigin) == originCell &&
               TryRay(manager, adaptedOrigin, destination);
    }

    private static bool TryLowerOccupiedTarget(GameLocationVisibilityManager manager, Vector3 destination,
        int3 destinationCell, out Vector3 loweredDestination)
    {
        loweredDestination = destination;
        var belowCell = new int3(destinationCell.x, destinationCell.y - 1, destinationCell.z);
        var accessor = new GridAccessor(manager.GameLocationService);

        // Without an explicit target identity, a valid upper cell might belong to a
        // different actor standing on the roof. Only repair invalid target samples;
        // never reinterpret a valid point above a floor as the occupant underneath it.
        if (accessor.GetCellId(destinationCell) != CellId.Invalid ||
            !accessor.Occupants_TryGet(belowCell, out var occupants))
        {
            return false;
        }

        foreach (var occupant in occupants)
        {
            if (occupant?.RulesetCharacter == null ||
                occupant.RulesetCharacter is RulesetCharacterEffectProxy)
            {
                continue;
            }

            if (TryLowerClippedPoint(manager, occupant, destination, destinationCell, out loweredDestination))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryLowerClippedPoint(GameLocationVisibilityManager manager, GameLocationCharacter character,
        Vector3 point, int3 pointCell, out Vector3 loweredPoint)
    {
        loweredPoint = point;
        var positioning = manager.gameLocationPositioningService;
        var center = positioning.GetWorldPositionFromGridPosition(pointCell);
        var belowCell = new int3(pointCell.x, pointCell.y - 1, pointCell.z);
        var belowCenter = positioning.GetWorldPositionFromGridPosition(belowCell);
        var cellHeight = center.y - belowCenter.y;
        var boundary = (center.y + belowCenter.y) * 0.5f;
        var minimum = character.LocationPosition + character.SizeParameters.minExtent;
        var maximum = character.LocationPosition + character.SizeParameters.maxExtent;

        // The sample must be just above a cell actually occupied by this body. This
        // admits native eye/detection heights (e.g. 3 + 1.116 / 1.2) but never lowers
        // an arbitrary point across a floor, into another column, or through a layer.
        if (cellHeight <= 0f || point.y < boundary || point.y - boundary > cellHeight * 0.5f ||
            belowCell.x < minimum.x || belowCell.x > maximum.x ||
            belowCell.y < minimum.y || belowCell.y > maximum.y ||
            belowCell.z < minimum.z || belowCell.z > maximum.z)
        {
            return false;
        }

        var accessor = new GridAccessor(manager.GameLocationService);
        var belowId = accessor.GetCellId(belowCell);
        if (belowId == CellId.Invalid)
        {
            return false;
        }

        var belowFlags = accessor.SightBlockerFlags(belowId);
        var pointId = accessor.GetCellId(pointCell);
        var pointFlags = pointId == CellId.Invalid ? CellFlags.Side.None : accessor.SightBlockerFlags(pointId);

        // A sparse roof can have an invalid upper cell without a Top/Bottom flag.
        // Requiring those flags alone would reject that case before testing a corrected ray.
        if ((belowFlags & CellFlags.Side.Override_AllSides) != 0 ||
            (pointId != CellId.Invalid && (belowFlags & CellFlags.Side.Top) == 0 &&
             (pointFlags & (CellFlags.Side.Bottom | CellFlags.Side.Override_AllSides)) == 0))
        {
            return false;
        }

        loweredPoint.y = boundary - Mathf.Clamp(cellHeight * CeilingInset, 0.03f, 0.15f);
        if (positioning.GetGridPositionFromWorldPosition(loweredPoint) == belowCell)
        {
            return true;
        }

        loweredPoint = point;
        return false;
    }

    private static bool IsActualVisionOrigin(GameLocationVisibilityManager manager, GameLocationCharacter sensor,
        Vector3 origin)
    {
        if (sensor?.RulesetCharacter == null)
        {
            return false;
        }

        var positioning = manager.gameLocationPositioningService;
        var actualEye = Vector3.zero;
        positioning.ComputeVisionCenterPosition(sensor, ref actualEye);
        var size = sensor.SizeParameters;
        var minimum = positioning.GetWorldPositionFromGridPosition(sensor.LocationPosition + size.minExtent);
        var maximum = positioning.GetWorldPositionFromGridPosition(sensor.LocationPosition + size.maxExtent);

        // Native verticality adjustment changes x/z only. A different eye height or a
        // point outside this actor's occupied footprint belongs to a hypothetical query.
        return Mathf.Abs(origin.y - actualEye.y) < 0.001f &&
               origin.x > minimum.x - 0.5f && origin.x < maximum.x + 0.5f &&
               origin.z > minimum.z - 0.5f && origin.z < maximum.z + 0.5f;
    }

    private static bool TryRay(GameLocationVisibilityManager manager, Vector3 origin, Vector3 destination)
    {
        return !manager.gameLocationPositioningService.RaycastGridSightBlocker(
            origin, destination, manager.GameLocationService);
    }
}
