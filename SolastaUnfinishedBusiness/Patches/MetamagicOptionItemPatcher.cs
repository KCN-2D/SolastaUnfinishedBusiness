using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;

namespace SolastaUnfinishedBusiness.Patches;

[HarmonyPatch(typeof(MetamagicOptionItem), nameof(MetamagicOptionItem.Bind))]
[UsedImplicitly]
internal static class MetamagicOptionItemPatcher
{
    [UsedImplicitly]
    private static void Postfix(MetamagicOptionItem __instance, MetamagicOptionDefinition __0, string __2)
    {
        if (!__0.HasSubFeatureOfType<FormattedDefinitionText>())
        {
            return;
        }

        // The native item reads GuiPresentation directly, bypassing definition/wrapper formatters.
        __instance.titleLabel.Text = __0.FormatTitle();
        var description = __0.FormatDescription();
        __instance.tooltip.Content = string.IsNullOrEmpty(__2)
            ? description : Gui.FormatFailure(description, __2, true);
    }
}
