using System;

namespace SolastaUnfinishedBusiness.Behaviors;

// Resolve related definitions at display time so switching language never leaves cached text behind.
internal sealed class FormattedDefinitionText(
    Func<string> title = null,
    Func<string> description = null,
    Func<RulesetCharacter, string> characterDescription = null,
    Func<RulesetCondition, string> conditionDescription = null)
{
    internal string FormatTitle(string original) => title?.Invoke() ?? original;

    internal string FormatDescription(string original) => description?.Invoke() ?? original;

    // Shared definition wrappers must never retain the character or condition from a previous tooltip.
    internal string FormatCharacterDescription(string original, RulesetCharacter character) =>
        character == null ? original : characterDescription?.Invoke(character) ?? original;

    internal string FormatConditionDescription(string original, RulesetCondition condition) =>
        condition == null ? original : conditionDescription?.Invoke(condition) ?? original;
}
