using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ActiveConditionItemPatcher
{
    [HarmonyPatch(typeof(ActiveConditionItem), nameof(ActiveConditionItem.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(Image ___image)
        {
            // Condition slots have a fixed size; preserve the shape of non-square status symbols.
            if (___image)
            {
                ___image.preserveAspect = true;
            }
        }
    }
}
