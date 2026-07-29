using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class RenderMeshDeformerPatcher
{
    private const string RenderMeshDeformerTypeName = "MagicaCloth.RenderMeshDeformer";

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
            Mesh ___sharedMesh,
            Mesh ___mesh)
        {
            bool sharedMeshIsValid = ___sharedMesh;
            bool meshIsValid = ___mesh;

            if (sharedMeshIsValid && meshIsValid)
            {
                return true;
            }

            // During character-inspection scene teardown, Finish can still run
            // after a source mesh is destroyed. It dereferences both mesh fields
            // without checking Unity object validity.
            return false;
        }
    }
}
