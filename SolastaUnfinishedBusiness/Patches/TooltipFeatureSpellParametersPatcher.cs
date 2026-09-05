using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class TooltipFeatureSpellParametersPatcher
{
    [HarmonyPatch(typeof(TooltipFeatureSpellParameters), nameof(TooltipFeatureSpellParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureSpellParameters __instance, ITooltip tooltip)
        {
            if (tooltip is not GuiTooltip guiTooltip ||
                tooltip.DataProvider is not ISpellParametersProvider { SpellDefinition: { } spell })
            {
                return;
            }

            var spellBox = guiTooltip.GetComponentInParent<SpellActivationBox>();
            var character = tooltip.Context switch
            {
                RulesetCharacter caster => caster,
                GameLocationCharacter locationCharacter => locationCharacter.RulesetCharacter,
                GuiCharacter guiCharacter => guiCharacter.RulesetCharacter,
                _ => null
            };
            var line = SpellActionTypeContext.GetRepertoireLine(spellBox);

            if (spellBox == null || spellBox.tooltip != guiTooltip ||
                character == null || line?.caster?.RulesetCharacter != character ||
                !SpellCastingValidation.TryGetTooltipRepertoire(tooltip, out var repertoire) ||
                repertoire != spellBox.spellRepertoire)
            {
                return;
            }

            var activationTime = SpellActionTypeContext.GetDisplayedActivationTime(spell, spellBox);

            if (activationTime != spell.ActivationTime)
            {
                __instance.castingTimeLabel.Text = Gui.FormatActivationTime(activationTime);
            }
        }
    }
}
