using System;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.CustomUI;

public interface IReactionRequestWithResource
{
    ICustomReactionResource Resource { get; }
}

public interface IReactionRequestWithCallbacks //<T> where T : ReactionRequest
{
    [CanBeNull] public Action<ReactionRequest> ReactionValidated { get; }

    [CanBeNull] public Action<ReactionRequest> ReactionNotValidated { get; }
}

public static class ReactionRequestCallback
{
    [CanBeNull]
    public static Action<ReactionRequest> Transform<T>([CanBeNull] Action<T> callback) where T : ReactionRequest
    {
        if (callback == null) { return null; }

        return request => callback((T)request);
    }
}

public class ReactionRequestCustom : ReactionRequest, IReactionRequestWithResource
{
    internal static readonly string EnvTitle = Gui.Localize("Screen/&EditorLocationEnvironmentTitle");
    private readonly string _type;

    internal ReactionRequestCustom(string type, CharacterActionParams reactionParams)
        : this(type, reactionParams, null, null)
    {
    }

    internal ReactionRequestCustom(string type, CharacterActionParams reactionParams,
        GameLocationCharacter target, BaseDefinition effectDefinition)
        : base(Name(type), reactionParams)
    {
        _type = type;
        Target = target;
        EffectDefinition = effectDefinition;
    }

    // UI context is independent of action targets, which a confirmation-only
    // request must not populate or change on behalf of its gameplay callback.
    internal GameLocationCharacter Target { get; }

    internal BaseDefinition EffectDefinition { get; }

    public ICustomReactionResource Resource { get; set; }

    private static string Name(string type)
    {
        return $"CustomReaction{type}";
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
}
