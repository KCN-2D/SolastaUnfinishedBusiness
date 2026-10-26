using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class InvocationItemPatcher
{
    [HarmonyPatch(typeof(InvocationItem), nameof(InvocationItem.ComputeTraining))]
    [UsedImplicitly]
    public static class ComputeTraining_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(InvocationItem __instance, ref bool __0, ref bool __1, ref bool __2)
        {
            if (!PermanentDefensesSubPanel.IsDefenseRow(__instance))
            {
                return true;
            }

            // Defensive rows use native static presentation without an invocation definition.
            __0 = false;
            __1 = false;
            __2 = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(InvocationItem), nameof(InvocationItem.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(InvocationItem __instance)
        {
            //PATCH: makes custom invocations have custom tooltips
            if (__instance.GuiInvocationDefinition
                    .InvocationDefinition is not InvocationDefinitionCustom custom)
            {
                return;
            }

            var tooltip = __instance.Tooltip;
            var hero = tooltip.Context as RulesetCharacter ?? Global.CurrentCharacter;

            if (hero == null)
            {
                return;
            }

            PowerBundle.ValidatePrerequisites(hero, custom, custom.Validators, out var requirements);

            var gui = new GuiPresentationBuilder(custom.GuiPresentation).Build();
            var item = custom.Item;
            var dataProvider = !item
                ? new CustomTooltipProvider(custom, gui)
                : new CustomItemTooltipProvider(custom, gui, item);

            dataProvider.SetPrerequisites(requirements);
            tooltip.TooltipClass = dataProvider.TooltipClass;
            tooltip.Content = CustomTooltipProvider.GetActivationContent(custom);
            tooltip.Context = hero;
            tooltip.DataProvider = dataProvider;
        }
    }
}
