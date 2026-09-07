using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Subclasses;
using TA;
using UnityEngine;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.EffectProxyDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class SightPathContext
{
    internal enum Obscurement
    {
        None,
        MagicalDarkness,
        HeavilyObscured
    }

    // The caller checks physical line of sight and both endpoints separately. This
    // classifies intervening obscurement without treating every special sense alike.
    internal static Obscurement GetObscurement(GameLocationCharacter sensor, int3 targetCell)
    {
        if (sensor == null)
        {
            return Obscurement.HeavilyObscured;
        }

        var originCell = sensor.LocationPosition;

        if (originCell == targetCell)
        {
            return Obscurement.None;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null)
        {
            return Obscurement.HeavilyObscured;
        }

        if (!positioningService.IsSightImpaired(originCell, targetCell))
        {
            return Obscurement.None;
        }

        var origin = positioningService.GetWorldPositionFromGridPosition(originCell);
        var destination = positioningService.GetWorldPositionFromGridPosition(targetCell);
        var direction = destination - origin;
        var cells = new List<int3>();

        // Use the cell-list overload: native RaycastGridCellFlagsAll records the
        // previous cell in its hit even though it tested the next cell's flags.
        if (!positioningService.RaycastGridAll(new Ray(origin, direction.normalized), ref cells, direction.magnitude))
        {
            return Obscurement.HeavilyObscured;
        }

        var accessor = new GridAccessor(originCell);
        var result = Obscurement.None;

        foreach (var cell in cells)
        {
            if (cell == originCell || cell == targetCell ||
                (accessor.RuntimeFlags(cell) & CellFlags.Runtime.DynamicSightImpaired) == 0)
            {
                continue;
            }

            var cellObscurement = ClassifyCell(ref accessor, cell);

            // Fog still blocks truesight when a darkness effect occupies the same cell.
            if (cellObscurement == Obscurement.HeavilyObscured)
            {
                return cellObscurement;
            }

            result = cellObscurement;
        }

        return result;
    }

    private static Obscurement ClassifyCell(ref GridAccessor accessor, int3 cell)
    {
        if (!accessor.Occupants_TryGet(cell, out var occupants))
        {
            return Obscurement.HeavilyObscured;
        }

        var hasDarkness = false;

        foreach (var occupant in occupants)
        {
            if (occupant?.RulesetActor is not RulesetCharacterEffectProxy proxy)
            {
                continue;
            }

            if (proxy.EffectProxyDefinition == ProxyDarkness ||
                proxy.EffectProxyDefinition == WayOfShadow.EffectProxyDarkness)
            {
                hasDarkness = true;

                continue;
            }

            var forms = EffectHelpers.GetEffectByGuid(proxy.EffectGuid)?.EffectDescription?.EffectForms;

            // An unrelated proxy such as a light source does not turn darkness into
            // fog. Unknown sources remain opaque until their cause can be identified.
            if (forms == null)
            {
                return Obscurement.HeavilyObscured;
            }

            foreach (var form in forms)
            {
                if (form.FormType == EffectForm.EffectFormType.Topology &&
                    form.TopologyForm.ChangeType == TopologyForm.Type.SightImpaired)
                {
                    return Obscurement.HeavilyObscured;
                }
            }
        }

        return hasDarkness ? Obscurement.MagicalDarkness : Obscurement.HeavilyObscured;
    }
}
