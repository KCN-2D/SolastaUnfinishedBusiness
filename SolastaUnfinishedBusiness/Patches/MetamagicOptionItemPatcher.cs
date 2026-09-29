using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[HarmonyPatch(typeof(MetamagicOptionItem), nameof(MetamagicOptionItem.Bind))]
[UsedImplicitly]
internal static class MetamagicOptionItemPatcher
{
    [UsedImplicitly]
    private static void Prefix(
        MetamagicOptionDefinition __0, ref bool __1, ref string __2,
        MetamagicOptionItem.MetamagicOptionSelectedHandler __4)
    {
        if (__0 != MetamagicContext.GetFirstSelection(
                __4?.Target as MetamagicSelectionPanel))
        {
            return;
        }

        __1 = false;
        __2 = "Failure/&FailureFlagMetamagicAlreadySelected";
    }

    [UsedImplicitly]
    private static void Postfix(
        MetamagicOptionItem __instance, MetamagicOptionDefinition __0, string __2,
        MetamagicOptionItem.MetamagicOptionSelectedHandler __4)
    {
        if (__0 == MetamagicContext.GetFirstSelection(
                __4?.Target as MetamagicSelectionPanel))
        {
            __instance.titleLabel.Text = Gui.Format("Screen/&MetamagicSelectedFormat", __0.FormatTitle());
            return;
        }

        if (!__0.HasSubFeatureOfType<FormattedDefinitionText>())
        {
            return;
        }

        // The native item reads GuiPresentation directly, bypassing definition/wrapper formatters.
        var family = MetamagicContext.GetReplacementSelection(__4?.Target as MetamagicSelectionPanel)
            ?.GetFirstSubFeatureOfType<ReplaceMetamagicOption>();
        var choice = family == null ? null : CombinedMetamagic.Enumerate(__0)
            .FirstOrDefault(family.Options.Contains);
        __instance.titleLabel.Text = choice?.GuiPresentation.Title ?? __0.FormatTitle();
        var description = __0.FormatDescription();
        __instance.tooltip.Content = string.IsNullOrEmpty(__2)
            ? description : Gui.FormatFailure(description, __2, true);
    }
}
