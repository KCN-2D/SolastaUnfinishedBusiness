using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SolastaUnfinishedBusiness.Behaviors;

// Resolve related definitions at display time so switching language never leaves cached text behind.
internal sealed class FormattedDefinitionText(
    Func<string> title = null,
    Func<string> description = null,
    Func<RulesetCharacter, string> characterDescription = null,
    Func<RulesetCondition, string> conditionDescription = null,
    object descriptionIdentity = null)
{
    private Declaration _declaration;

    private object DescriptionIdentity { get; } = descriptionIdentity;

    internal enum TextKind
    {
        Translation,
        DefinitionTitle,
        DefinitionDescription,
        Literal
    }

    internal sealed class Text
    {
        private Text(TextKind kind, string term, BaseDefinition definition, Text[] arguments, string value = null)
        {
            Kind = kind;
            Term = term;
            Definition = definition;
            Arguments = new ReadOnlyCollection<Text>((Text[])(arguments ?? throw new ArgumentNullException(nameof(arguments))).Clone());
            Value = value;
            Validate();
        }

        internal TextKind Kind { get; }

        internal string Term { get; }

        internal BaseDefinition Definition { get; }

        internal string Value { get; }

        internal IReadOnlyList<Text> Arguments { get; }

        internal static Text Translation(string term, params Text[] arguments) =>
            new(TextKind.Translation, term, null, arguments ?? throw new ArgumentNullException(nameof(arguments)));

        internal static Text DefinitionTitle(BaseDefinition definition) =>
            new(TextKind.DefinitionTitle, null, definition, []);

        internal static Text DefinitionDescription(BaseDefinition definition) =>
            new(TextKind.DefinitionDescription, null, definition, []);

        internal static Text Literal(string value) =>
            new(TextKind.Literal, null, null, [], value);

        internal void Validate()
        {
            if (Arguments == null || Arguments.Any(argument => argument == null))
            {
                throw new ArgumentException("Formatted text arguments must be present.");
            }

            switch (Kind)
            {
                case TextKind.Translation:
                    if (string.IsNullOrEmpty(Term) || Definition != null || Value != null)
                    {
                        throw new ArgumentException("Translated text requires a term and no definition or literal value.");
                    }

                    foreach (var argument in Arguments)
                    {
                        argument.Validate();
                    }

                    break;
                case TextKind.DefinitionTitle:
                case TextKind.DefinitionDescription:
                    if (Definition == null || Term != null || Arguments.Count != 0 || Value != null)
                    {
                        throw new ArgumentException("Definition text requires one definition and no term, arguments or literal value.");
                    }

                    break;
                case TextKind.Literal:
                    if (Value == null || Term != null || Definition != null || Arguments.Count != 0)
                    {
                        throw new ArgumentException("Literal text requires a value and no term, definition or arguments.");
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown formatted text kind.");
            }
        }

        internal string Resolve() => Kind switch
        {
            TextKind.Translation => Gui.Format(Term, Arguments.Select(argument => argument.Resolve()).ToArray()),
            TextKind.DefinitionTitle => Definition.FormatTitle(),
            TextKind.DefinitionDescription => Definition.FormatDescription(),
            TextKind.Literal => Value,
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown formatted text kind.")
        };
    }

    internal sealed class Declaration(Text title = null, Text description = null)
    {
        internal Text Title { get; } = title;

        internal Text Description { get; } = description;
    }

    internal static FormattedDefinitionText FromDeclaration(Declaration declaration)
    {
        if (declaration == null)
        {
            throw new ArgumentNullException(nameof(declaration));
        }

        declaration.Title?.Validate();
        declaration.Description?.Validate();

        return new FormattedDefinitionText(
            title: declaration.Title == null ? null : declaration.Title.Resolve,
            description: declaration.Description == null ? null : declaration.Description.Resolve)
        {
            _declaration = declaration
        };
    }

    internal bool TryGetDeclaration(out Declaration declaration)
    {
        declaration = _declaration;

        return declaration != null;
    }

    // Related wrappers can add a title or recharge line while still describing the same rules body.
    internal bool SharesDescriptionWith(FormattedDefinitionText other) => ReferenceEquals(this, other) ||
        DescriptionIdentity != null && DescriptionIdentity.Equals(other?.DescriptionIdentity);

    internal string FormatTitle(string original) => title?.Invoke() ?? original;

    internal string FormatDescription(string original) => description?.Invoke() ?? original;

    // Shared definition wrappers must never retain the character or condition from a previous tooltip.
    internal string FormatCharacterDescription(string original, RulesetCharacter character) =>
        character == null ? original : characterDescription?.Invoke(character) ?? original;

    internal string FormatConditionDescription(string original, RulesetCondition condition) =>
        condition == null ? original : conditionDescription?.Invoke(condition) ?? original;
}
