using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiActiveConditionPatcher
{
    [HarmonyPatch(typeof(GuiActiveCondition), nameof(GuiActiveCondition.Description), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    public static class Description_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiActiveCondition __instance, ref string __result)
        {
            var condition = __instance.ActiveCondition;
            var formattedText = condition.ConditionDefinition.GetFirstSubFeatureOfType<FormattedDefinitionText>();

            if (formattedText != null)
            {
                __result = formattedText.FormatConditionDescription(__result, condition);
            }
        }
    }
}
