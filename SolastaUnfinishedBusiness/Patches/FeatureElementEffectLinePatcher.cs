using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public class FeatureElementEffectLinePatcher
{
    private static void ClearTooltip(FeatureElementEffectLine effectLine)
    {
        var tooltip = effectLine.tooltip;

        if (tooltip)
        {
            tooltip.Content = string.Empty;
        }
    }

    [HarmonyPatch(typeof(FeatureElementEffectLine), nameof(FeatureElementEffectLine.Bind), typeof(string))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindSpecialDescription_Patch
    {
        [UsedImplicitly]
        public static void Prefix(FeatureElementEffectLine __instance)
        {
            ClearTooltip(__instance);
        }
    }

    //PATCH: clear tooltip on bind, so it does not show previous value if new effect has no trends
    [HarmonyPatch(typeof(FeatureElementEffectLine), nameof(FeatureElementEffectLine.Bind))]
    [HarmonyPatch([
            typeof(EffectForm), typeof(bool), typeof(int), typeof(bool), typeof(bool),
            typeof(Gui.VersatilityDisplay), typeof(int), typeof(string), typeof(RuleDefinitions.EffectApplication)
        ],
        [
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal
        ])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] FeatureElementEffectLine __instance)
        {
            ClearTooltip(__instance);
        }
    }
}
