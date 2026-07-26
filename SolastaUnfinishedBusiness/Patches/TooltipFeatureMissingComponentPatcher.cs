using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]

public class TooltipFeatureMissingComponentPatcher
{
    [HarmonyPatch(typeof(TooltipFeatureMissingComponent), nameof(TooltipFeatureMissingComponent.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            TooltipFeatureMissingComponent __instance,
            ITooltip __0,
            out System.IDisposable __state)
        {
            __state = SpellCastingValidation.EnterTooltipRepertoire(__0);

            var bypass = SpellCastingValidation.TooltipBypassesMissingComponents(__0);

            __instance.gameObject.SetActive(!bypass);

            return !bypass;
        }

        [UsedImplicitly]
        public static void Postfix(ref TooltipFeatureMissingComponent __instance)
        {
            __instance.gameObject.GetComponentInChildren<GuiLabel>().Text = "Tooltip/&InvalidComponentRequirementTitle";
        }

        [UsedImplicitly]
        public static System.Exception Finalizer(
            System.Exception __exception,
            System.IDisposable __state)
        {
            __state?.Dispose();

            return __exception;
        }
    }
}
