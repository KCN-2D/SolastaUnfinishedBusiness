using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using TA;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationPathfindingManagerPatcher
{
    [HarmonyPatch(typeof(GameLocationPathfindingManager), nameof(GameLocationPathfindingManager.ComputeValidDestinations))]
    [HarmonyPatch([typeof(GameLocationCharacter), typeof(bool), typeof(int)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeValidDestinations_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter character, ref IDisposable __state)
        {
            __state = FreeJumpContext.BeginExplorationPathfinding(character);
        }

        [UsedImplicitly]
        public static void Postfix(IDisposable __state)
        {
            __state?.Dispose();
        }
    }

    [HarmonyPatch(typeof(GameLocationPathfindingManager),
        nameof(GameLocationPathfindingManager.ComputeValidDestinationsAsync))]
    [HarmonyPatch([typeof(GameLocationCharacter), typeof(bool), typeof(int), typeof(int)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeValidDestinationsAsync_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, GameLocationCharacter character)
        {
            using (FreeJumpContext.BeginExplorationPathfinding(character))
            {
                while (values.MoveNext())
                {
                    yield return values.Current;
                }
            }
        }
    }

    [HarmonyPatch]
    [UsedImplicitly]
    private static class CaptureCompletedRoutes
    {
        [UsedImplicitly]
        private static MethodBase TargetMethod()
        {
            var iterator = typeof(PathfindingLocation)
                .GetNestedTypes(BindingFlags.NonPublic)
                .Single(type => type.Name.Contains("<ComputeValidDestinations>d__"));

            return AccessTools.Method(iterator, "MoveNext");
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // UpdateFromGame also resets sectors before and after closedSet.Clear. Only this
            // coroutine's direct ResetSector call belongs to completed-search cleanup.
            var code = instructions.ReplaceCalls(
                AccessTools.Method(typeof(PathfindingSector), nameof(PathfindingSector.ResetSector)),
                1,
                "AiPathfinding.CompletedRoutes",
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(CaptureCompletedRoutes), nameof(ResetCompletedSector)))).ToList();
            var busyField = AccessTools.Field(typeof(PathfindingLocation), "isComputingValidDestinations");
            var acquisitions = Enumerable.Range(1, code.Count - 1)
                .Where(index => code[index].StoresField(busyField) && code[index - 1].opcode == OpCodes.Ldc_I4_1)
                .ToArray();
            if (acquisitions.Length != 1)
            {
                throw new InvalidOperationException("Expected one native valid-destination query acquisition.");
            }

            // Preserve the location reference across its busy=true store. A rejected native
            // query must never acquire our cleanup ownership or clear somebody else's grid.
            var acquisition = acquisitions[0];
            code.Insert(acquisition + 1, new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(AiPathfindingRouteContext), nameof(AiPathfindingRouteContext.MarkQueryStarted))));
            code.Insert(acquisition - 1, new CodeInstruction(OpCodes.Dup)
                .MoveLabelsFrom(code[acquisition - 1]).MoveBlocksFrom(code[acquisition - 1]));
            return code;
        }

        private static void ResetCompletedSector(PathfindingSector sector, bool hardReset)
        {
            AiPathfindingRouteContext.CaptureCompletedRoutes(sector);
            sector.ResetSector(hardReset);
        }
    }

}
