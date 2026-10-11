using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class DamageFormPatcher
{
    [HarmonyPatch(typeof(DamageForm), nameof(DamageForm.Copy))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Copy_Patch
    {
        [UsedImplicitly]
        public static void Prefix(DamageForm __instance, DamageForm __0)
        {
            // Native Copy omits derived trends, so pooled destinations must start
            // empty just like fresh forms. Self-copy preserves the existing state.
            if (!ReferenceEquals(__instance, __0))
            {
                __instance.DamageBonusTrends.Clear();
            }
        }
    }
}
