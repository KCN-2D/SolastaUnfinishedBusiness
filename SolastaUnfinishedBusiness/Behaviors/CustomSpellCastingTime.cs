namespace SolastaUnfinishedBusiness.Behaviors;

internal sealed class CustomSpellCastingTime
{
    internal CustomSpellCastingTime(int durationSeconds, string guiTerm)
    {
        DurationSeconds = durationSeconds;
        GuiTerm = guiTerm;
    }

    internal int DurationSeconds { get; }

    internal string GuiTerm { get; }
}
