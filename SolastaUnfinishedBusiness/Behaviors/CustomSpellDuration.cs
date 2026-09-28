using System;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors;

// Separates the displayed rules duration from an internal condition used to resolve the spell.
internal sealed class CustomSpellDuration(DurationType durationType, int durationParameter, Func<bool> isValid)
{
    internal bool IsValid => isValid();

    internal string FormatDuration(bool requiresConcentration)
    {
        return (requiresConcentration ? "◉ " : string.Empty) + Gui.FormatDuration(durationType, durationParameter);
    }
}
