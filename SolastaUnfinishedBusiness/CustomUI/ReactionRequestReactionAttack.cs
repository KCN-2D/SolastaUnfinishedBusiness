using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.CustomUI;

internal class ReactionRequestReactionAttack : ReactionRequest, IReactionRequestWithResource,
    IReactionRequestWithCallbacks
{
    private readonly string _ally;
    private readonly GuiCharacter _target;
    private readonly string _type;

    internal ReactionRequestReactionAttack(
        string type,
        CharacterActionParams reactionParams,
        System.Action<ReactionRequestReactionAttack> reactionValidated = null,
        System.Action<ReactionRequestReactionAttack> reactionNotValidated = null)
        : base(Name(type), reactionParams)
    {
        ReactionValidated = ReactionRequestCallback.Transform(reactionValidated);
        ReactionNotValidated = ReactionRequestCallback.Transform(reactionNotValidated);

        _type = type;
        _target = new GuiCharacter(reactionParams.TargetCharacters[0]);
        _ally = reactionParams.StringParameter;
    }

    public override bool IsStillValid
    {
        get
        {
            var targetCharacter = ReactionParams.TargetCharacters[0];

            return targetCharacter.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                   ServiceRepository.GetService<IGameLocationCharacterService>().ValidCharacters
                       .Contains(targetCharacter);
        }
    }

    public ICustomReactionResource Resource { get; set; }
    public System.Action<ReactionRequest> ReactionValidated { get; }
    public System.Action<ReactionRequest> ReactionNotValidated { get; }

    private static string Name(string type)
    {
        return $"ReactionAttack{type}";
    }

    public override string FormatTitle()
    {
        return Gui.Localize($"Reaction/&ReactionAttack{_type}Title");
    }

    public override string FormatDescription()
    {
        var format = $"Reaction/&ReactionAttack{_type}Description";

        return Gui.Format(format, _target.Name, _ally);
    }

    public override string FormatReactTitle()
    {
        var format = $"Reaction/&ReactionAttack{_type}ReactTitle";

        return Gui.Format(format, _target.Name, _ally);
    }

    public override string FormatReactDescription()
    {
        var format = $"Reaction/&ReactionAttack{_type}ReactDescription";

        return Gui.Format(format, _target.Name, _ally);
    }
}
