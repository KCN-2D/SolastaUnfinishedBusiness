using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiBaseDefinitionWrapperPatcher
{
    [HarmonyPatch(typeof(GuiBaseDefinitionWrapper), nameof(GuiBaseDefinitionWrapper.Title), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Title_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiBaseDefinitionWrapper __instance, ref string __result)
        {
            var formattedText = __instance.BaseDefinition.GetFirstSubFeatureOfType<FormattedDefinitionText>();

            if (formattedText != null)
            {
                __result = formattedText.FormatTitle(__result);
            }
        }
    }

    [HarmonyPatch(typeof(GuiBaseDefinitionWrapper), nameof(GuiBaseDefinitionWrapper.Description), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Description_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiBaseDefinitionWrapper __instance, ref string __result)
        {
            var formattedText = __instance.BaseDefinition.GetFirstSubFeatureOfType<FormattedDefinitionText>();

            if (formattedText != null)
            {
                __result = formattedText.FormatDescription(__result);
            }
        }
    }
}
