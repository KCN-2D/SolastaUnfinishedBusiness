using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionMovePatcher
{
    [HarmonyPatch(typeof(CharacterActionMove), nameof(CharacterActionMove.ExecuteImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteImpl_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, CharacterActionMove __instance)
        {
            return FreeJumpContext.WithMovePathfindingScope(values, __instance);
        }
    }
}
