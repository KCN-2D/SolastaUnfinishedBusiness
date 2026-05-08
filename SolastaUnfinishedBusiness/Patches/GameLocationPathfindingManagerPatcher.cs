using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
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
}
