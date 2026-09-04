using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class ReactionRequestSelectTarget : ReactionRequest
{
    internal const string Name = "ReactionSelectTarget";

    private readonly string _type;
    private int _selectedOption = -1;

    internal ReactionRequestSelectTarget(
        CharacterActionParams reactionParams,
        IEnumerable<GameLocationCharacter> candidates,
        string type)
        : base(Name, reactionParams)
    {
        _type = type;
        Candidates = candidates
            .Where(candidate => candidate != null)
            .GroupBy(candidate => candidate.Guid)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Guid)
            .ToList();

        BuildSuboptions();
    }

    internal IReadOnlyList<GameLocationCharacter> Candidates { get; }

    internal GameLocationCharacter SelectedTarget =>
        _selectedOption >= 0 &&
        _selectedOption < Candidates.Count &&
        IsCandidateValid(Candidates[_selectedOption])
            ? Candidates[_selectedOption]
            : null;

    public override int SelectedSubOption => _selectedOption;

    public override string SuboptionTag => _type;

    public override bool IsStillValid => Candidates.Any(IsCandidateValid);

    internal static bool IsCandidateValid(GameLocationCharacter candidate)
    {
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        return candidate?.RulesetCharacter is
                   { IsDeadOrDyingOrUnconscious: false } and not RulesetCharacterEffectProxy &&
               characterService?.ValidCharacters?.Contains(candidate) == true;
    }

    public override void SelectSubOption(int option)
    {
        var targetCharacters = ReactionParams.TargetCharacters;
        var actionModifiers = ReactionParams.ActionModifiers;

        targetCharacters.Clear();
        actionModifiers.Clear();
        _selectedOption = -1;

        if (option < 0 || option >= Candidates.Count || !IsCandidateValid(Candidates[option]))
        {
            return;
        }

        _selectedOption = option;
        ReactionParams.IntParameter = option;
        targetCharacters.Add(Candidates[option]);
        actionModifiers.Add(new ActionModifier());
    }

    public override string FormatTitle()
    {
        return Gui.Localize($"Reaction/&CustomReaction{_type}Title");
    }

    public override string FormatDescription()
    {
        return ReactionParams.StringParameter;
    }

    public override string FormatReactTitle()
    {
        return Gui.Localize($"Reaction/&CustomReaction{_type}ReactTitle");
    }

    public override string FormatReactDescription()
    {
        return Gui.Localize($"Reaction/&CustomReaction{_type}ReactDescription");
    }

    private void BuildSuboptions()
    {
        SubOptionsAvailability.Clear();
        ReactionParams.SpellRepertoire = new RulesetSpellRepertoire();

        for (var index = 0; index < Candidates.Count; index++)
        {
            SubOptionsAvailability.Add(index, IsCandidateValid(Candidates[index]));
        }

        foreach (var option in SubOptionsAvailability.Where(option => option.Value))
        {
            SelectSubOption(option.Key);
            break;
        }
    }
}
