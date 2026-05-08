using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class PathfindingGridNodePatcher
{
    [HarmonyPatch(typeof(PathfindingGridNode), nameof(PathfindingGridNode.GetNeighbours))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetNeighbours_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            PathfindingGridNode __instance,
            List<PathfindingNeighbour> neighbours,
            List<int> moveModes)
        {
            FreeJumpContext.AddPathfindingNeighbours(__instance, neighbours, moveModes);
        }
    }
}
