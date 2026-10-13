using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static class CombatAnimationContext
{
    internal const float MinSpeedMultiplier = 1.0f;
    internal const float DefaultSpeedMultiplier = 1.08f;
    internal const float MaxSpeedMultiplier = 1.50f;

    private const float MinEffectiveSpeedMultiplier = MinSpeedMultiplier + 0.0001f;

    private static readonly Dictionary<Animator, AnimatorState> AnimatorStates = [];
    private static readonly Dictionary<Animator, MovementReactionState> MovementReactionStates = [];

    internal static float ClampSpeedMultiplier(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? DefaultSpeedMultiplier
            : Math.Min(MaxSpeedMultiplier, Math.Max(MinSpeedMultiplier, value));
    }

    internal static IDisposable BeginActionScope(CharacterAction action)
    {
        if (action?.ActingCharacter == null || IsMovementAction(action) ||
            Gui.Battle == null ||
            !Main.Settings.EnableSmootherBattleAnimations)
        {
            return EmptyScope.Instance;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService is not { IsBattleInProgress: true })
        {
            return EmptyScope.Instance;
        }

        var speedMultiplier = ClampSpeedMultiplier(Main.Settings.BattleActionAnimationSpeedMultiplier);

        if (speedMultiplier < MinEffectiveSpeedMultiplier)
        {
            return EmptyScope.Instance;
        }

        var characters = new HashSet<RulesetCharacter>();
        TryAddRulesetCharacter(characters, action.ActingCharacter);

        var targetCharacters = action.ActionParams?.TargetCharacters;

        if (targetCharacters != null)
        {
            foreach (var targetCharacter in targetCharacters)
            {
                TryAddRulesetCharacter(characters, targetCharacter);
            }
        }

        var animators = Apply(characters);

        return animators.Count == 0
            ? EmptyScope.Instance
            : new ActionScope(animators);
    }

    internal static void Unload()
    {
        foreach (var entry in AnimatorStates.ToArray())
        {
            RestoreAnimator(entry.Key, entry.Value);
        }

        AnimatorStates.Clear();

        foreach (var entry in MovementReactionStates.ToArray())
        {
            RestoreMovementSpeed(entry.Key, entry.Value);
        }

        MovementReactionStates.Clear();
    }

    internal static void RefreshActionAnimation(GraphicsCharacter graphicsCharacter)
    {
        if (!graphicsCharacter)
        {
            return;
        }

        if (AnimatorStates.Count != 0)
        {
            RefreshAnimator(graphicsCharacter.Animator);
            RefreshAnimator(graphicsCharacter.WeaponAnimator);
        }

        var animator = graphicsCharacter.Animator;

        if (MovementReactionStates.Count != 0 && animator &&
            MovementReactionStates.TryGetValue(animator, out var reaction) && reaction.IsCurrent(graphicsCharacter))
        {
            var speed = animator.GetFloat(AnimationDefinitions.CharacterSpeedId);

            if (IsMovementReactionBoundary(reaction.Character))
            {
                if (speed != 0f)
                {
                    reaction.RequestedSpeed = speed;
                }

                animator.SetFloat(AnimationDefinitions.CharacterSpeedId, 0f);
                reaction.IsPaused = true;
            }
            else
            {
                // A nested reaction may start a push, charge, jump, or other native
                // motion while the outer wait remains alive. Let that motion finish.
                RestoreMovementSpeed(animator, reaction);
                reaction.IsPaused = false;

                if (speed != 0f)
                {
                    reaction.RequestedSpeed = speed;
                }
            }
        }
    }

    internal static void ForgetActionAnimation(GraphicsCharacter graphicsCharacter)
    {
        if (!graphicsCharacter)
        {
            return;
        }

        ForgetAnimator(graphicsCharacter.Animator);
        ForgetAnimator(graphicsCharacter.WeaponAnimator);
        ForgetMovementReaction(graphicsCharacter.Animator);
    }

    internal static IDisposable BeginMovementReactionScope(GameLocationCharacter character)
    {
        var graphicsCharacters = ServiceRepository.GetService<IGraphicsCharacterFactoryService>()?.GraphicsCharacters;
        var leases = new List<MovementReactionLease>();
        var applied = new HashSet<Animator>();

        if (character?.RulesetCharacter == null || !IsMovementReactionBoundary(character) || graphicsCharacters == null)
        {
            return EmptyScope.Instance;
        }

        try
        {
            foreach (var graphicsCharacter in graphicsCharacters)
            {
                if (!graphicsCharacter || graphicsCharacter.RulesetCharacter != character.RulesetCharacter)
                {
                    continue;
                }

                var animator = graphicsCharacter.Animator;

                if (!animator || !animator.runtimeAnimatorController || !applied.Add(animator) ||
                    !animator.parameters.Any(parameter => parameter.nameHash == AnimationDefinitions.CharacterSpeedId &&
                                                          parameter.type == AnimatorControllerParameterType.Float))
                {
                    continue;
                }

                if (MovementReactionStates.TryGetValue(animator, out var state) && !state.IsCurrent(graphicsCharacter))
                {
                    ForgetMovementReaction(animator);
                    state = null;
                }

                if (state == null)
                {
                    state = new MovementReactionState(graphicsCharacter, animator, character);
                    MovementReactionStates.Add(animator, state);
                }
                else
                {
                    state.ReferenceCount++;
                }

                leases.Add(new MovementReactionLease(animator, state));
                animator.SetFloat(AnimationDefinitions.CharacterSpeedId, 0f);
                state.IsPaused = true;
            }
        }
        catch
        {
            foreach (var lease in leases)
            {
                ReleaseMovementReaction(lease);
            }

            throw;
        }

        return leases.Count == 0 ? EmptyScope.Instance : new MovementReactionScope(leases);
    }

    internal static float FilterMovementReactionSpeed(GraphicsCharacter graphicsCharacter, float requestedSpeed)
    {
        var animator = graphicsCharacter.Animator;

        if (!animator || !MovementReactionStates.TryGetValue(animator, out var state) ||
            !state.IsCurrent(graphicsCharacter))
        {
            return requestedSpeed;
        }

        // Preserve the latest native request: an interrupted move can request zero
        // while the reaction waits, and must remain stopped when the wait ends.
        state.RequestedSpeed = requestedSpeed;
        state.IsPaused = IsMovementReactionBoundary(state.Character);
        return state.IsPaused ? 0f : requestedSpeed;
    }

    private static bool IsMovementReactionBoundary(GameLocationCharacter character)
    {
        // Reactions between steps may pause the locomotion blend; an actual jump,
        // climb, fall, charge or forced-motion step must finish its native motion.
        return character is { MoveInProgress: true, MoveStepInProgress: false, Jumping: false, Climbing: false,
            Falling: false, IsCharging: false, IgnoreForPlacementDuringPushAnimation: false };
    }

    private static void ForgetMovementReaction(Animator animator)
    {
        if (ReferenceEquals(animator, null) || !MovementReactionStates.TryGetValue(animator, out var state))
        {
            return;
        }

        RestoreMovementSpeed(animator, state);
        MovementReactionStates.Remove(animator);
    }

    private static void RestoreMovementSpeed(Animator animator, MovementReactionState state)
    {
        if (animator && state.IsPaused && state.IsCurrent(state.GraphicsCharacter) &&
            animator.GetFloat(AnimationDefinitions.CharacterSpeedId) == 0f)
        {
            animator.SetFloat(AnimationDefinitions.CharacterSpeedId, state.RequestedSpeed);
        }
    }

    private static void ReleaseMovementReaction(MovementReactionLease lease)
    {
        if (!MovementReactionStates.TryGetValue(lease.Animator, out var state) || !ReferenceEquals(state, lease.State))
        {
            return;
        }

        if (--state.ReferenceCount > 0)
        {
            return;
        }

        RestoreMovementSpeed(lease.Animator, state);
        MovementReactionStates.Remove(lease.Animator);
    }

    internal static System.Collections.IEnumerator WaitForActionWarmup(CharacterAction action)
    {
        var duration = GameConfiguration.CharacterAnimation.WaitingTimeAfterMove;
        var elapsed = 0f;
        var animationElapsed = 0f;

        // This is only the settling interval before an attack trigger. Never shorten
        // animation-event, projectile-flight, impact, or reaction waits.
        while (animationElapsed < duration || elapsed < duration && IsActionAnimatorTransitioning(action))
        {
            yield return null;

            var delta = Time.deltaTime;
            elapsed += delta;
            animationElapsed += delta * GetOwnedActionSpeedMultiplier(action);
        }
    }

    private static bool IsMovementAction(CharacterAction action)
    {
        return action is CharacterActionMove or CharacterActionMoveStepBase or CharacterActionFreeFall or
            CharacterActionCharge or CharacterActionPushed or CharacterActionPushedCustom or CharacterActionLevitate;
    }

    private static bool HasSynchronizedMotion(GameLocationCharacter character)
    {
        return character == null || character.MoveInProgress || character.MoveStepInProgress ||
               character.MovingToDestination || character.Jumping || character.Climbing || character.Falling ||
               character.IsCharging || character.IgnoreForPlacementDuringPushAnimation;
    }

    private static float GetActionSpeedMultiplier(GameLocationCharacter character)
    {
        return Gui.Battle == null || !Main.Settings.EnableSmootherBattleAnimations ||
               HasSynchronizedMotion(character) ||
               ServiceRepository.GetService<IGameLocationBattleService>() is not { IsBattleInProgress: true }
            ? MinSpeedMultiplier
            : ClampSpeedMultiplier(Main.Settings.BattleActionAnimationSpeedMultiplier);
    }

    private static bool IsActionAnimatorTransitioning(CharacterAction action)
    {
        var animator = GetActionAnimator(action);

        return animator && animator.runtimeAnimatorController && animator.layerCount > 0 && animator.IsInTransition(0);
    }

    private static float GetOwnedActionSpeedMultiplier(CharacterAction action)
    {
        var animator = GetActionAnimator(action);

        if (!animator || !AnimatorStates.TryGetValue(animator, out var state))
        {
            return MinSpeedMultiplier;
        }

        RefreshAnimator(animator);

        return AnimatorStates.TryGetValue(animator, out var current) && ReferenceEquals(current, state) &&
               animator.runtimeAnimatorController && animator.layerCount > 0 && state.OriginalSpeed > 0
            ? Math.Max(MinSpeedMultiplier, state.AppliedSpeed / state.OriginalSpeed)
            : MinSpeedMultiplier;
    }

    private static Animator GetActionAnimator(CharacterAction action)
    {
        var character = action?.ActingCharacter;

        if (character == null)
        {
            return null;
        }

        foreach (var entry in AnimatorStates)
        {
            var state = entry.Value;

            if (state.Character == character && state.GraphicsCharacter &&
                entry.Key == state.GraphicsCharacter.Animator)
            {
                return entry.Key;
            }
        }

        return null;
    }

    internal static void RefreshMovementState(RulesetCharacter character)
    {
        if (GameLocationCharacter.GetFromActor(character) is { } location &&
            !character.MoveModes.ContainsKey((int)location.CurrentMoveMode))
        {
            location.CurrentMoveMode = location.DefaultMoveMode;
        }

        var graphicsCharacters = ServiceRepository.GetService<IGraphicsCharacterFactoryService>()?.GraphicsCharacters;

        if (graphicsCharacters == null)
        {
            return;
        }

        var flying = character.MoveModes.ContainsKey((int)RuleDefinitions.MoveMode.Fly) ||
                     character.HasConditionOfTypeOrSubType("ConditionFlying") ||
                     character.HasConditionOfTypeOrSubType("ConditionLevitate");

        foreach (var graphicsCharacter in graphicsCharacters)
        {
            if (!graphicsCharacter || graphicsCharacter.RulesetCharacter != character ||
                !graphicsCharacter.IsBoundToGameCharacter || !graphicsCharacter.Animator ||
                !graphicsCharacter.Animator.runtimeAnimatorController ||
                graphicsCharacter.flyingStatus == flying)
            {
                continue;
            }

            // Native condition animations only handle Flying/Levitate. Intrinsic movement can
            // also change on the same body, and must not leave its old flying pose active.
            graphicsCharacter.SetFlyingStatus(flying, false);
        }
    }

    private static void TryAddRulesetCharacter(ISet<RulesetCharacter> characters, GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter != null)
        {
            characters.Add(rulesetCharacter);
        }
    }

    private static List<AnimatorLease> Apply(ISet<RulesetCharacter> characters)
    {
        var graphicsCharacters = ServiceRepository.GetService<IGraphicsCharacterFactoryService>()?.GraphicsCharacters;
        var animators = new List<AnimatorLease>();
        var appliedAnimators = new HashSet<Animator>();

        if (graphicsCharacters == null || characters.Count == 0)
        {
            return animators;
        }

        try
        {
            foreach (var graphicsCharacter in graphicsCharacters)
            {
                if (!graphicsCharacter || !characters.Contains(graphicsCharacter.RulesetCharacter))
                {
                    continue;
                }

                var character = GameLocationCharacter.GetFromActor(graphicsCharacter.RulesetCharacter);

                TryApply(graphicsCharacter.Animator, graphicsCharacter, character, animators, appliedAnimators);
                TryApply(graphicsCharacter.WeaponAnimator, graphicsCharacter, character, animators, appliedAnimators);
            }
        }
        catch
        {
            foreach (var lease in animators)
            {
                Release(lease);
            }

            throw;
        }

        return animators;
    }

    private static void TryApply(
        Animator animator,
        GraphicsCharacter graphicsCharacter,
        GameLocationCharacter character,
        List<AnimatorLease> animators,
        ISet<Animator> appliedAnimators)
    {
        if (!animator || !appliedAnimators.Add(animator))
        {
            return;
        }

        if (AnimatorStates.TryGetValue(animator, out var state) && !state.IsCurrent(animator))
        {
            ForgetAnimator(animator);
            state = null;
        }

        if (state != null)
        {
            state.ReferenceCount++;
        }
        else
        {
            state = new AnimatorState(animator.speed, graphicsCharacter, character, animator);
            AnimatorStates.Add(animator, state);
        }

        animators.Add(new AnimatorLease(animator, state));
        RefreshAnimator(animator);
    }

    private static void RefreshAnimator(Animator animator)
    {
        if (!animator || !AnimatorStates.TryGetValue(animator, out var state))
        {
            return;
        }

        if (!state.IsCurrent(animator))
        {
            ForgetAnimator(animator);
            return;
        }

        if (animator.speed != state.AppliedSpeed)
        {
            // The native owner may pause or resume this animator during a nested action.
            // Treat that write as its new baseline instead of undoing the pause.
            state.OriginalSpeed = animator.speed;
        }

        state.AppliedSpeed = state.OriginalSpeed * GetActionSpeedMultiplier(state.Character);
        animator.speed = state.AppliedSpeed;
    }

    private static void ForgetAnimator(Animator animator)
    {
        if (ReferenceEquals(animator, null) || !AnimatorStates.TryGetValue(animator, out var state))
        {
            return;
        }

        RestoreAnimator(animator, state);
        AnimatorStates.Remove(animator);
    }

    private static void RestoreAnimator(Animator animator, AnimatorState state)
    {
        // Speed belongs to the Animator component. A controller replacement can end
        // the lease without changing the speed applied by it.
        if (state.OwnsAnimator(animator) && animator.speed == state.AppliedSpeed)
        {
            animator.speed = state.OriginalSpeed;
        }
    }

    private static void Release(AnimatorLease lease)
    {
        var animator = lease.Animator;

        if (!AnimatorStates.TryGetValue(animator, out var state) ||
            !ReferenceEquals(state, lease.State))
        {
            // Unload ends the previous ownership generation. A late action disposal must
            // not release a new action using the same pooled Animator afterwards.
            return;
        }

        state.ReferenceCount--;

        if (state.ReferenceCount > 0)
        {
            return;
        }

        RestoreAnimator(animator, state);
        AnimatorStates.Remove(animator);
    }

    private sealed class AnimatorState(
        float originalSpeed, GraphicsCharacter graphicsCharacter, GameLocationCharacter character, Animator animator)
    {
        internal readonly GraphicsCharacter GraphicsCharacter = graphicsCharacter;
        internal readonly GameLocationCharacter Character = character;
        private readonly RuntimeAnimatorController _controller = animator.runtimeAnimatorController;
        private readonly RulesetCharacter _character = graphicsCharacter.RulesetCharacter;
        internal float OriginalSpeed = originalSpeed;
        internal float AppliedSpeed = originalSpeed;
        internal int ReferenceCount = 1;

        internal bool IsCurrent(Animator animator)
        {
            return OwnsAnimator(animator) && animator.runtimeAnimatorController == _controller;
        }

        internal bool OwnsAnimator(Animator animator)
        {
            return animator && GraphicsCharacter && GraphicsCharacter.RulesetCharacter == _character &&
                   Character?.RulesetCharacter == _character &&
                   (GraphicsCharacter.Animator == animator || GraphicsCharacter.WeaponAnimator == animator);
        }
    }

    private readonly struct AnimatorLease(Animator animator, AnimatorState state)
    {
        internal Animator Animator { get; } = animator;
        internal AnimatorState State { get; } = state;
    }

    private sealed class ActionScope(List<AnimatorLease> animators) : IDisposable
    {
        private List<AnimatorLease> _animators = animators;

        public void Dispose()
        {
            if (_animators == null)
            {
                return;
            }

            var ownedAnimators = _animators;
            _animators = null;

            foreach (var lease in ownedAnimators)
            {
                Release(lease);
            }
        }
    }

    private sealed class MovementReactionState(
        GraphicsCharacter graphicsCharacter, Animator animator, GameLocationCharacter character)
    {
        internal readonly GraphicsCharacter GraphicsCharacter = graphicsCharacter;
        internal readonly GameLocationCharacter Character = character;
        private readonly Animator _animator = animator;
        private readonly RuntimeAnimatorController _controller = animator.runtimeAnimatorController;
        private readonly RulesetCharacter _character = graphicsCharacter.RulesetCharacter;
        internal float RequestedSpeed = animator.GetFloat(AnimationDefinitions.CharacterSpeedId);
        internal bool IsPaused;
        internal int ReferenceCount = 1;

        internal bool IsCurrent(GraphicsCharacter graphics)
        {
            return graphics && graphics == GraphicsCharacter && graphics.Animator == _animator && _animator &&
                   Character?.RulesetCharacter == _character &&
                   _animator.runtimeAnimatorController == _controller && graphics.RulesetCharacter == _character;
        }
    }

    private readonly struct MovementReactionLease(Animator animator, MovementReactionState state)
    {
        internal Animator Animator { get; } = animator;
        internal MovementReactionState State { get; } = state;
    }

    private sealed class MovementReactionScope(List<MovementReactionLease> leases) : IDisposable
    {
        private List<MovementReactionLease> _leases = leases;

        public void Dispose()
        {
            if (_leases == null)
            {
                return;
            }

            var ownedLeases = _leases;
            _leases = null;

            foreach (var lease in ownedLeases)
            {
                ReleaseMovementReaction(lease);
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();

        public void Dispose()
        {
        }
    }
}
