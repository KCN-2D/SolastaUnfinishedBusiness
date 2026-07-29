namespace SolastaUnfinishedBusiness.CustomUI;

public delegate string CustomSpellAdvancementTooltipDelegate(SpellDefinition spell);

public static class CustomSpellAdvancementTooltip
{
    public static CustomSpellAdvancementTooltipDelegate ExtraDie(RuleDefinitions.DieType type, int count = 1)
    {
        var format = count > 1
            ? GuiSpellDefinition.advancementGainAdditionalDiceMultipleFormat
            : GuiSpellDefinition.advancementGainAdditionalDiceSingleFormat;

        return _ => Gui.Format(format, $"{count:+0;-#} {Gui.GetDieSymbol(type)}");
    }

    internal static CustomSpellAdvancementTooltipDelegate FormattedQuantity(
        string formatKey,
        int count = 1)
    {
        return _ => Gui.Format(formatKey, count.ToString("+0;-#"));
    }
}
