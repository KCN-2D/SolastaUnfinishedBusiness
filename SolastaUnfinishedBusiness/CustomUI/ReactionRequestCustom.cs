using System;
using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<CharacterActionParams, Func<bool>> Validations = new();
    private static readonly ConditionalWeakTable<CharacterActionParams, ExecutionScope> Executions = new();
    private static readonly ConditionalWeakTable<CharacterActionParams, object> Interruptions = new();
    private static readonly ConditionalWeakTable<CharacterActionParams, EffectContext> EffectContexts = new();

    // A resource-spending action can alter another creature's roll without
    // delivering its pool power to that creature. Keep presentation separate.
    internal sealed class EffectContext(GameLocationCharacter target, BaseDefinition definition, bool? beneficial)
    {
        internal GameLocationCharacter Target { get; } = target;
        internal BaseDefinition Definition { get; } = definition;
        internal bool? Beneficial { get; } = beneficial;
    }

    internal static void SetEffectContext(CharacterActionParams parameters, GameLocationCharacter target,
        BaseDefinition definition, bool? beneficial)
    {
        EffectContexts.Remove(parameters);
        if (target != null) EffectContexts.Add(parameters, new EffectContext(target, definition, beneficial));
    }

    internal static EffectContext GetEffectContext(CharacterActionParams parameters) =>
        parameters != null && EffectContexts.TryGetValue(parameters, out var context) ? context : null;

    [ThreadStatic] private static CharacterActionParams _validating;

    internal static CharacterActionParams Validating => _validating;

    internal static void TrackInterruption(CharacterActionParams parameters)
    {
        if (parameters != null) Interruptions.GetOrCreateValue(parameters);
    }

    internal static bool IsInterruption(CharacterActionParams parameters) =>
        parameters != null && Interruptions.TryGetValue(parameters, out _);

    internal static bool Validate(CharacterActionParams parameters, Func<bool> validation)
    {
        var previous = _validating;
        try
        {
            _validating = parameters;
            return validation?.Invoke() ?? true;
        }
        finally
        {
            _validating = previous;
        }
    }

    internal static IDisposable BeginExecution(CharacterAction action) => new ExecutionScope(action);

    internal static bool IsExecuting(CharacterActionParams parameters) =>
        parameters != null && Executions.TryGetValue(parameters, out _);

    // Native Execute engages the reaction before its implementation validates targets.
    // Read availability with only this action's own engagement/rank changes removed.
    internal static bool IsReactionAvailableDuringExecution(GameLocationCharacter actor, out bool available)
    {
        available = false;
        if (_validating?.ActingCharacter != actor ||
            !Executions.TryGetValue(_validating, out var execution) ||
            _validating.ActionDefinition.ActionType != ActionDefinitions.ActionType.Reaction)
        {
            return false;
        }

        var engaged = actor.ReactionEngaged;
        var rank = actor.currentActionRankByType[ActionDefinitions.ActionType.Reaction];
        try
        {
            actor.ReactionEngaged = execution.Engaged;
            actor.currentActionRankByType[ActionDefinitions.ActionType.Reaction] = execution.Rank;
            available = actor.GetActionTypeStatus(ActionDefinitions.ActionType.Reaction) ==
                        ActionDefinitions.ActionStatus.Available;
            return true;
        }
        finally
        {
            actor.ReactionEngaged = engaged;
            actor.currentActionRankByType[ActionDefinitions.ActionType.Reaction] = rank;
        }
    }

    private sealed class ExecutionScope : IDisposable
    {
        private readonly CharacterActionParams _parameters;
        private readonly ExecutionScope _previous;
        internal readonly bool Engaged;
        internal readonly int Rank;

        internal ExecutionScope(CharacterAction action)
        {
            _parameters = action.ActionParams;
            var actor = action.ActingCharacter;
            Engaged = actor.ReactionEngaged;
            Rank = actor.currentActionRankByType[ActionDefinitions.ActionType.Reaction];
            Executions.TryGetValue(_parameters, out _previous);
            Executions.Remove(_parameters);
            Executions.Add(_parameters, this);
        }

        public void Dispose()
        {
            Executions.Remove(_parameters);
            if (_previous != null) Executions.Add(_parameters, _previous);
        }
    }

    internal static void SetValidation(CharacterActionParams parameters, Func<bool> validation)
    {
        Validations.Remove(parameters);
        if (validation != null)
        {
            Validations.Add(parameters, validation);
        }
    }

    internal static bool IsValid(CharacterActionParams parameters) =>
        parameters == null || !Validations.TryGetValue(parameters, out var validation) ||
        Validate(parameters, validation);

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
    private readonly Func<bool> _reactionStillValid;

    internal ReactionRequestCustom(string type, CharacterActionParams reactionParams)
        : this(type, reactionParams, null, null)
    {
    }

    internal ReactionRequestCustom(string type, CharacterActionParams reactionParams,
        GameLocationCharacter target, BaseDefinition effectDefinition, Func<bool> reactionStillValid = null)
        : base(Name(type), reactionParams)
    {
        _type = type;
        Target = target;
        EffectDefinition = effectDefinition;
        _reactionStillValid = reactionStillValid;
    }

    // UI context is independent of action targets, which a confirmation-only
    // request must not populate or change on behalf of its gameplay callback.
    internal GameLocationCharacter Target { get; }

    internal BaseDefinition EffectDefinition { get; }

    public override bool IsStillValid => base.IsStillValid &&
                                        ReactionRequestCallback.Validate(ReactionParams, _reactionStillValid);

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
