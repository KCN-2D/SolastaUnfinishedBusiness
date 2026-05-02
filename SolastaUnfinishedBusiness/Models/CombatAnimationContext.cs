using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static class CombatAnimationContext
{
    internal const float MinSpeedMultiplier = 1.0f;
    internal const float DefaultSpeedMultiplier = 1.08f;
    internal const float MaxSpeedMultiplier = 1.20f;

    private const float MinEffectiveSpeedMultiplier = MinSpeedMultiplier + 0.0001f;

    private static readonly Dictionary<Animator, AnimatorState> AnimatorStates = [];

    internal static float ClampSpeedMultiplier(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? DefaultSpeedMultiplier
            : Math.Min(MaxSpeedMultiplier, Math.Max(MinSpeedMultiplier, value));
    }

    internal static IDisposable BeginActionScope(CharacterAction action)
    {
        if (action?.ActingCharacter == null ||
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

        var characters = new List<GameLocationCharacter> { action.ActingCharacter };
        var targetCharacters = action.ActionParams?.TargetCharacters;

        if (targetCharacters != null)
        {
            foreach (var targetCharacter in targetCharacters)
            {
                if (targetCharacter != null && !characters.Contains(targetCharacter))
                {
                    characters.Add(targetCharacter);
                }
            }
        }

        var animators = Apply(characters, speedMultiplier);

        return animators.Count == 0
            ? EmptyScope.Instance
            : new ActionScope(animators);
    }

    internal static void Unload()
    {
        foreach (var entry in AnimatorStates.ToArray())
        {
            if (entry.Key)
            {
                entry.Key.speed = entry.Value.OriginalSpeed;
            }
        }

        AnimatorStates.Clear();
    }

    private static List<Animator> Apply(IEnumerable<GameLocationCharacter> characters, float speedMultiplier)
    {
        var graphicsCharacters = ServiceRepository.GetService<IGraphicsCharacterFactoryService>()?.GraphicsCharacters;
        var animators = new List<Animator>();

        if (graphicsCharacters == null)
        {
            return animators;
        }

        foreach (var character in characters)
        {
            var graphicsCharacter = FindGraphicsCharacter(graphicsCharacters, character);

            if (!graphicsCharacter)
            {
                continue;
            }

            TryApply(graphicsCharacter.Animator, speedMultiplier, animators);
            TryApply(graphicsCharacter.WeaponAnimator, speedMultiplier, animators);
        }

        return animators;
    }

    private static GraphicsCharacter FindGraphicsCharacter(
        IEnumerable<GraphicsCharacter> graphicsCharacters,
        GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            return null;
        }

        foreach (var graphicsCharacter in graphicsCharacters)
        {
            if (graphicsCharacter && graphicsCharacter.RulesetCharacter == rulesetCharacter)
            {
                return graphicsCharacter;
            }
        }

        return null;
    }

    private static void TryApply(Animator animator, float speedMultiplier, List<Animator> animators)
    {
        if (!animator || animators.Contains(animator))
        {
            return;
        }

        if (AnimatorStates.TryGetValue(animator, out var state))
        {
            state.ReferenceCount++;
        }
        else
        {
            state = new AnimatorState(animator.speed);
            AnimatorStates.Add(animator, state);
            animator.speed = state.OriginalSpeed * speedMultiplier;
        }

        animators.Add(animator);
    }

    private static void Release(Animator animator)
    {
        if (!AnimatorStates.TryGetValue(animator, out var state))
        {
            return;
        }

        state.ReferenceCount--;

        if (state.ReferenceCount > 0)
        {
            return;
        }

        if (animator)
        {
            animator.speed = state.OriginalSpeed;
        }

        AnimatorStates.Remove(animator);
    }

    private sealed class AnimatorState(float originalSpeed)
    {
        internal readonly float OriginalSpeed = originalSpeed;
        internal int ReferenceCount = 1;
    }

    private sealed class ActionScope(List<Animator> animators) : IDisposable
    {
        private List<Animator> _animators = animators;

        public void Dispose()
        {
            if (_animators == null)
            {
                return;
            }

            foreach (var animator in _animators)
            {
                Release(animator);
            }

            _animators = null;
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
