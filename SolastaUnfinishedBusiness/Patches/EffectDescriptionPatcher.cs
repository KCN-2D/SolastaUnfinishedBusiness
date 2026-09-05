using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class EffectDescriptionPatcher
{
    [HarmonyPatch(typeof(EffectDescription), nameof(EffectDescription.Copy))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Copy_Patch
    {
        [UsedImplicitly]
        public static void Postfix(EffectDescription __instance, EffectDescription reference)
        {
            // Native Copy omits this field, losing the explanation when modifiers clone an effect.
            __instance.specialFormsDescription = reference.specialFormsDescription;
        }
    }
}
