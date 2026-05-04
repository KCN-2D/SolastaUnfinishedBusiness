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

    private static void TryAddRulesetCharacter(ISet<RulesetCharacter> characters, GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter != null)
        {
            characters.Add(rulesetCharacter);
        }
    }

    private static List<Animator> Apply(ISet<RulesetCharacter> characters, float speedMultiplier)
    {
        var graphicsCharacters = ServiceRepository.GetService<IGraphicsCharacterFactoryService>()?.GraphicsCharacters;
        var animators = new List<Animator>();
        var appliedAnimators = new HashSet<Animator>();

        if (graphicsCharacters == null || characters.Count == 0)
        {
            return animators;
        }

        foreach (var graphicsCharacter in graphicsCharacters)
        {
            if (!graphicsCharacter || !characters.Contains(graphicsCharacter.RulesetCharacter))
            {
                continue;
            }

            TryApply(graphicsCharacter.Animator, speedMultiplier, animators, appliedAnimators);
            TryApply(graphicsCharacter.WeaponAnimator, speedMultiplier, animators, appliedAnimators);
        }

        return animators;
    }

    private static void TryApply(
        Animator animator,
        float speedMultiplier,
        List<Animator> animators,
        ISet<Animator> appliedAnimators)
    {
        if (!animator || !appliedAnimators.Add(animator))
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
