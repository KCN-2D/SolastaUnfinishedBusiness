using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FeatureDefinitionPatcher
{
    [HarmonyPatch(typeof(FeatureDefinition), nameof(FeatureDefinition.ShouldRefreshCharacter), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ShouldRefreshCharacter_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(FeatureDefinition __instance, ref bool __result)
        {
            // The native race stage grants its displayed choices only when the chosen feature requests a refresh.
            if (Main.Settings.EnableSpeciesSpellcastingAbility2024 &&
                SpeciesSpellcastingContext.IsAbilityChoice(__instance))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(FeatureDefinition), nameof(FeatureDefinition.AllowsDuplicate), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AllowsDuplicate_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(FeatureDefinition __instance, ref bool __result)
        {
            if (__instance.HasSubFeatureOfType<AllowDuplicates>())
            {
                __result = true;
            }
        }
    }
}

internal class AllowDuplicates
{
    private AllowDuplicates()
    {
    }

    public static AllowDuplicates Mark { get; } = new();
}
