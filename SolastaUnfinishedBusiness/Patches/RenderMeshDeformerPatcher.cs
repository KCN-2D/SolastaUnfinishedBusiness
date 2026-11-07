using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class RenderMeshDeformerPatcher
{
    private const string RenderMeshDeformerTypeName = "MagicaCloth.RenderMeshDeformer";

    [HarmonyPatch]
    [UsedImplicitly]
    internal static class RuntimeMeshLifetime_Patch
    {
        private static readonly HashSet<string> Verified = [];
        internal static bool Enabled { get; private set; }

        [UsedImplicitly]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName(RenderMeshDeformerTypeName);
            if (type == null) yield break;
            yield return AccessTools.DeclaredMethod(type, "OnInit");
            yield return AccessTools.DeclaredMethod(type, "Dispose");
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var codes = instructions.ToList();
            var replacements = new List<(CodeInstruction Code, string Helper)>();
            var clones = 0; var poses = 0; var destroys = 0;
            foreach (var code in codes)
            {
                if (code.operand is not MethodInfo method) continue;
                if (method.DeclaringType == typeof(UnityEngine.Object) && method.Name == nameof(UnityEngine.Object.Instantiate) &&
                    method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[] { typeof(Mesh) }) && method.GetParameters().Length == 1)
                {
                    clones++; replacements.Add((code, nameof(RacesContext.AcquireCharacterModelClothMesh)));
                }
                else if (method.DeclaringType == typeof(UnityEngine.Object) && method.Name == nameof(UnityEngine.Object.Destroy) &&
                         method.GetParameters().Length == 1)
                {
                    destroys++; replacements.Add((code, nameof(RacesContext.ReleaseCharacterModelClothMesh)));
                }
                else if (method.DeclaringType == typeof(Mesh) && method.Name == "set_bindposes")
                {
                    poses++; replacements.Add((code, nameof(RacesContext.SetCharacterModelClothBindPoses)));
                }
            }
            Verified.Remove(__originalMethod.Name);
            var valid = __originalMethod.Name == "OnInit" ? clones == 2 && poses == 1 && destroys == 1 : clones == 0 && poses == 0 && destroys == 1;
            if (!valid)
            {
                Enabled = false;
                Main.Error("Native cloth mesh lifetime calls changed: " + __originalMethod.Name);
                return codes;
            }
            foreach (var (code, helper) in replacements)
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(RacesContext), helper);
            }
            Verified.Add(__originalMethod.Name);
            Enabled = Verified.Contains("OnInit") && Verified.Contains("Dispose");
            return codes;
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class Finish_Patch
    {
        [UsedImplicitly]
        private static MethodBase TargetMethod()
        {
            var deformerType = AccessTools.TypeByName(RenderMeshDeformerTypeName);

            return deformerType == null
                ? null
                : AccessTools.DeclaredMethod(deformerType, "Finish", [typeof(int)]);
        }

        [UsedImplicitly]
        private static bool Prefix(
            object __instance, int __0,
            Mesh ___sharedMesh, Mesh ___mesh,
            object ___status, GameObject ___targetObject, MonoBehaviour ___parent,
            out CustomModels.SkinMeshSource.SourceGraph __state)
        {
            __state = null;
            bool sharedMeshIsValid = ___sharedMesh;
            bool meshIsValid = ___mesh;

            if (sharedMeshIsValid && meshIsValid && ___status != null && ___targetObject && ___parent)
            {
                __state = RacesContext.BeginCharacterModelNativeSurfaceFinish(__instance, __0);
                return true;
            }

            // Queued writes can outlive their component during replacement or
            // teardown. Native Finish dereferences its status before checking
            // the render target, even when both retained meshes are still alive.
            return false;
        }

        [UsedImplicitly]
        private static Exception Finalizer(object __instance, int __0,
            CustomModels.SkinMeshSource.SourceGraph __state, Exception __exception)
        {
            __state?.EndNativeSurfaceFinish(__instance, __0, __exception);
            return __exception;
        }
    }
}
