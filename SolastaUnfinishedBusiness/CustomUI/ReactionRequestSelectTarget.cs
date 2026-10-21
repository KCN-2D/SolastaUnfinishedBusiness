using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class ReactionRequestSelectTarget : ReactionRequest, IReactionRequestMultiSelection
{
    internal const string Name = "ReactionSelectTarget";

    private readonly string _type;
    private readonly Func<GameLocationCharacter, bool> _candidateValidator;
    private int _selectedOption = -1;

    internal ReactionRequestSelectTarget(
        CharacterActionParams reactionParams,
        IEnumerable<GameLocationCharacter> candidates,
        string type)
        : this(reactionParams, candidates, type, null)
    {
    }

    internal ReactionRequestSelectTarget(
        CharacterActionParams reactionParams,
        IEnumerable<GameLocationCharacter> candidates,
        string type,
        BaseDefinition effectDefinition,
        Func<GameLocationCharacter, bool> candidateValidator = null,
        bool isOffensive = false,
        int maximumTargets = 1,
        bool allowEmptySelection = false,
        bool isHarmfulInterruption = true,
        bool multipleSelection = false,
        bool preselectHostiles = false,
        bool isBeneficial = false)
        : base(Name, reactionParams)
    {
        _type = type;
        var validator = candidateValidator ?? (isOffensive
            ? candidate => IsCandidateValid(reactionParams.ActingCharacter, candidate)
            : IsCandidateValid);
        _candidateValidator = candidate => validator(candidate) &&
            (!isOffensive || TargetingContext.IsValidOffensiveTarget(reactionParams.ActingCharacter, candidate,
                effectDefinition)) &&
            (!isBeneficial || TargetingContext.IsBeneficialTargetAllowed(
                reactionParams.ActingCharacter, candidate));
        EffectDefinition = effectDefinition;
        IsOffensive = isOffensive;
        IsHarmfulInterruption = isOffensive && isHarmfulInterruption;
        IsBeneficial = isBeneficial;
        Candidates = candidates
            .Where(candidate => candidate != null)
            .GroupBy(candidate => candidate.Guid)
            .Select(group => group.First())
            .Where(_candidateValidator)
            .OrderBy(candidate => isOffensive && !reactionParams.ActingCharacter.IsOppositeSide(candidate.Side))
            .ThenBy(candidate => candidate.Guid)
            .ToList();

        if (multipleSelection || maximumTargets != 1 || allowEmptySelection)
        {
            Selection = new ReactionSelection(Candidates.Count, maximumTargets, allowEmptySelection ? 0 : 1,
                option => IsCandidateAvailable(Candidates[option]), selectionValid: options =>
                    !IsHarmfulInterruption || TargetingContext.IsHarmfulInterruptTargetsAllowed(
                        reactionParams.ActingCharacter, options.Select(option => Candidates[option]), effectDefinition));
            if (preselectHostiles)
            {
                for (var option = 0; option < Candidates.Count; option++)
                {
                    if (reactionParams.ActingCharacter.IsOppositeSide(Candidates[option].Side))
                    {
                        Selection.SetSelected(option, true);
                    }
                }
            }
        }

        BuildSuboptions();
    }

    internal IReadOnlyList<GameLocationCharacter> Candidates { get; }

    internal BaseDefinition EffectDefinition { get; }

    internal bool IsOffensive { get; }
    internal bool IsHarmfulInterruption { get; }
    internal bool IsBeneficial { get; }

    public ReactionSelection Selection { get; }
    public bool CanContinueSelection => Selection?.HasAvailableOptions == true;

    internal IReadOnlyList<GameLocationCharacter> SelectedTargets => Selection == null
        ? SelectedTarget == null ? [] : [SelectedTarget]
        : Selection.SelectedOptions.Select(option => Candidates[option]).ToArray();

    internal GameLocationCharacter SelectedTarget =>
        _selectedOption >= 0 &&
        _selectedOption < Candidates.Count &&
        IsCandidateAvailable(Candidates[_selectedOption])
            ? Candidates[_selectedOption]
            : null;

    public override int SelectedSubOption => _selectedOption;

    public override string SuboptionTag => _type;

    public override bool IsStillValid => Selection?.IsValid ?? SelectedTarget != null;

    internal bool IsCandidateAvailable(GameLocationCharacter candidate) =>
        ReactionRequestCallback.Validate(ReactionParams, () => _candidateValidator(candidate));

    internal static bool IsCandidateValid(GameLocationCharacter candidate)
    {
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        return candidate?.RulesetCharacter is
                   { IsDeadOrDyingOrUnconscious: false } and not RulesetCharacterEffectProxy &&
               characterService?.ValidCharacters?.Contains(candidate) == true;
    }

    internal static bool IsCandidateValid(GameLocationCharacter actor, GameLocationCharacter candidate) =>
        candidate?.RulesetCharacter is { IsDead: false } and not RulesetCharacterEffectProxy &&
        (TargetingContext.IsManualPartyActor(actor) || !candidate.RulesetCharacter.IsDeadOrDyingOrUnconscious) &&
        ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters?.Contains(candidate) == true;

    public override void SelectSubOption(int option)
    {
        if (Selection != null)
        {
            Selection.ApplyCommand(option);
            ReactionParams.TargetCharacters.Clear();
            ReactionParams.ActionModifiers.Clear();
            foreach (var selected in SelectedTargets)
            {
                ReactionParams.TargetCharacters.Add(selected);
                ReactionParams.ActionModifiers.Add(new ActionModifier());
            }
            return;
        }

        var targetCharacters = ReactionParams.TargetCharacters;
        var actionModifiers = ReactionParams.ActionModifiers;

        targetCharacters.Clear();
        actionModifiers.Clear();
        _selectedOption = -1;

        if (option < 0 || option >= Candidates.Count || !IsCandidateAvailable(Candidates[option]))
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
        var effectTitle = CustomTooltipProvider.FormatTitle(EffectDefinition);

        return CustomTooltipProvider.IsUnavailableContent(effectTitle)
            ? Gui.Localize($"Reaction/&CustomReaction{_type}Title")
            : effectTitle;
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
            SubOptionsAvailability.Add(index, IsCandidateAvailable(Candidates[index]));
        }

        if (Selection != null)
        {
            return;
        }

        foreach (var option in SubOptionsAvailability.Where(option => option.Value))
        {
            SelectSubOption(option.Key);
            break;
        }
    }
}

// The native local/network reaction command transports one integer. Checkbox changes use
// that same ordered command to update the pending request; only Confirm resolves it.
// This supports any practical candidate count without a 32-target bit-mask limit.
internal interface IReactionRequestMultiSelection
{
    ReactionSelection Selection { get; }
    bool CanContinueSelection { get; }
}

internal sealed class ReactionSelection(
    int optionCount,
    int maximumSelected,
    int minimumSelected,
    Func<int, bool> optionAvailable,
    Func<int, int> optionCost = null,
    Func<int> availableBudget = null,
    Func<IEnumerable<int>, bool> selectionValid = null)
{
    internal const int ConfirmCommand = -2;
    private readonly HashSet<int> _selected = [];
    private bool _selectionUpdate;
    private bool _validCommand = true;

    internal IEnumerable<int> SelectedOptions => _selected.OrderBy(option => option);
    internal int Count => _selected.Count;
    internal int Maximum => maximumSelected < 0 ? optionCount : Math.Min(maximumSelected, optionCount);
    internal int Cost => _selected.Sum(CostOf);
    internal int? Budget => availableBudget?.Invoke();
    internal bool HasAvailableOptions => Enumerable.Range(0, optionCount).Any(IsAvailable);
    internal bool IsGroupValid => selectionValid?.Invoke(SelectedOptions) ?? true;
    internal bool IsValid => _validCommand && Count >= minimumSelected && Count <= Maximum &&
                             _selected.All(IsAvailable) && Cost <= (availableBudget?.Invoke() ?? int.MaxValue) &&
                             IsGroupValid;

    internal bool IsSelected(int option) => _selected.Contains(option);
    internal bool IsAvailable(int option) => option >= 0 && option < optionCount && optionAvailable(option);
    internal int CostOf(int option) => Math.Max(0, optionCost?.Invoke(option) ?? 0);

    internal bool CanSelect(int option) => IsSelected(option) || IsAvailable(option) && Count < Maximum &&
        (long)Cost + CostOf(option) <= (availableBudget?.Invoke() ?? int.MaxValue);

    internal void SetSelected(int option, bool selected)
    {
        if (!selected)
        {
            _selected.Remove(option);
        }
        else if (CanSelect(option))
        {
            _selected.Add(option);
        }
    }

    internal static int EncodeUpdate(int option, bool selected) => checked(option * 2 + (selected ? 1 : 0));

    internal void ApplyCommand(int command)
    {
        _selectionUpdate = command >= 0;
        _validCommand = command == ConfirmCommand || _selectionUpdate && command / 2 < optionCount;
        if (_selectionUpdate)
        {
            SetSelected(command / 2, (command & 1) != 0);
        }
    }

    internal bool ConsumeUpdate()
    {
        var update = _selectionUpdate;
        _selectionUpdate = false;
        return update;
    }
}
