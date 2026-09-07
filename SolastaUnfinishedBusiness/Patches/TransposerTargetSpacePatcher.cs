using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[HarmonyPatch(typeof(TransposerTargetSpace), "PostPipelineStageCallback")]
[UsedImplicitly]
internal static class TransposerTargetSpacePatcher
{
    [UsedImplicitly]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var lookRotation = new Func<Vector3, Vector3, Quaternion>(Quaternion.LookRotation).Method;
        var targetRotation = new Func<Vector3, Vector3, Quaternion>(GetTargetRotation).Method;
        return instructions.ReplaceCalls(lookRotation, "TransposerTargetSpace.PostPipelineStageCallback",
            new CodeInstruction(OpCodes.Call, targetRotation));
    }

    private static Quaternion GetTargetRotation(Vector3 forward, Vector3 upwards)
    {
        if (forward.sqrMagnitude > 0)
        {
            return Quaternion.LookRotation(forward, upwards);
        }

        // Self-target spell cameras have identical Follow and LookAt positions. Preserve Unity's
        // identity fallback without repeatedly invoking LookRotation with an undefined direction.
        return Quaternion.identity;
    }
}
