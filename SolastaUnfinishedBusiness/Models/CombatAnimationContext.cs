using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static class CombatAnimationContext
{
    internal const float MinSpeedMultiplier = 1.0f;
    internal const float DefaultSpeedMultiplier = 1.08f;
    internal const float MaxSpeedMultiplier = 1.20f;

    private const float MinEffectiveSpeedMultiplier = MinSpeedMultiplier + 0.0001f;

    private static readonly Dictionary<Animator, AnimatorState> AnimatorStates = [];

    private const int CivilianReactionRange = 12;
    private static readonly Dictionary<GraphicsCharacter, CivilianPose> CivilianPoses = [];
    private static readonly HashSet<GraphicsCharacter> InterruptedCivilians = [];
    private static readonly HashSet<GraphicsCharacter> PendingCivilianPoses = [];
    private static readonly Dictionary<RuntimeAnimatorController, bool> CivilianControllers = [];
    private static readonly MethodInfo HandleBodyAnimation =
        AccessTools.Method(typeof(GraphicsCharacter), "HandleBodyAnimation");

    private static readonly int[] CivilianBoolParameters =
    [
        AnimationDefinitions.IsInDialogId, AnimationDefinitions.DialogInterruptsId,
        AnimationDefinitions.CutAnimationForDialogId, AnimationDefinitions.DialogIsLoopingId,
        AnimationDefinitions.IsKeepingPostureId, AnimationDefinitions.IsInSpeakIn
    ];

    private static readonly int[] CivilianFloatParameters =
    [
        AnimationDefinitions.SpeakTypeId, AnimationDefinitions.SpeakVariationId,
        AnimationDefinitions.NextPostureTypeId, AnimationDefinitions.CurrentSpeakTypeId,
        AnimationDefinitions.CurrentSpeakVariationId, AnimationDefinitions.CurrentPostureId
    ];

    private static GameLocationBattle _civilianBattle;
    private static GraphicsCharacter _applyingCivilianPose;
    private static bool _civilianRefreshPending;
    private static bool _civilianLocationSuspended;
    private static bool _civilianPresentationBlocked;
    private static float _civilianRetryTime;

    internal static void StartCivilianReactions(GameLocationBattle battle)
    {
        StopCivilianReactions();
        _civilianBattle = battle;
        RequestCivilianRefresh();
    }

    internal static void StopCivilianReactions()
    {
        ClearCivilianPoses();
        _civilianBattle = null;
        _civilianRefreshPending = false;
        _civilianPresentationBlocked = false;
        InterruptedCivilians.Clear();
    }

    internal static void SuspendCivilianReactions()
    {
        StopCivilianReactions();
        _civilianLocationSuspended = true;
        CivilianControllers.Clear();
    }

    internal static void ResumeCivilianReactions()
    {
        _civilianLocationSuspended = false;
        StartCivilianReactions(Gui.Battle);
    }

    internal static void RequestCivilianRefresh()
    {
        if (Main.Settings.EnableCivilianBattleReactions)
        {
            _civilianRefreshPending = true;
        }
    }

    internal static void RefreshCivilianReactions()
    {
        if (!Main.Settings.EnableCivilianBattleReactions)
        {
            ClearCivilianPoses();
            InterruptedCivilians.Clear();
            return;
        }

        _civilianBattle ??= Gui.Battle;
        RequestCivilianRefresh();
        UpdateCivilianReactions(_civilianBattle);
    }

    internal static void InterruptCivilianReaction(GraphicsCharacter graphicsCharacter)
    {
        if (ReferenceEquals(graphicsCharacter, _applyingCivilianPose))
        {
            return;
        }

        var pending = PendingCivilianPoses.Remove(graphicsCharacter);

        if (CivilianPoses.TryGetValue(graphicsCharacter, out var pose))
        {
            CivilianPoses.Remove(graphicsCharacter);
            pose.Restore(graphicsCharacter);
            pending = true;
        }

        if (pending)
        {
            // A scripted action owns this NPC until the presentation sequence or battle ends.
            InterruptedCivilians.Add(graphicsCharacter);
        }
    }

    internal static void ForgetCivilianReaction(GraphicsCharacter graphicsCharacter)
    {
        InterruptCivilianReaction(graphicsCharacter);
        InterruptedCivilians.Remove(graphicsCharacter);
    }

    internal static void InterruptCivilianReaction(RulesetCharacter character)
    {
        if (character == null)
        {
            return;
        }

        foreach (var graphicsCharacter in CivilianPoses.Keys.ToArray())
        {
            if (graphicsCharacter && graphicsCharacter.RulesetCharacter == character)
            {
                InterruptCivilianReaction(graphicsCharacter);
            }
        }
    }

    internal static void UpdateCivilianReactions(GameLocationBattle battle)
    {
        if (_civilianLocationSuspended || battle == null || !ReferenceEquals(battle, _civilianBattle))
        {
            return;
        }

        if (!Main.Settings.EnableCivilianBattleReactions)
        {
            if (CivilianPoses.Count != 0 || PendingCivilianPoses.Count != 0 || InterruptedCivilians.Count != 0)
            {
                ClearCivilianPoses();
                InterruptedCivilians.Clear();
            }

            return;
        }

        var blocked = ServiceRepository.GetService<INarrativeDirectionService>()?.IsSequenceInProgress == true ||
                      ServiceRepository.GetService<IViewLocationContextualService>()?.IsActive == true;

        if (blocked != _civilianPresentationBlocked)
        {
            _civilianPresentationBlocked = blocked;
            ClearCivilianPoses();
            InterruptedCivilians.Clear();
            RequestCivilianRefresh();
        }

        if (blocked || !_civilianRefreshPending &&
            (PendingCivilianPoses.Count == 0 || Time.unscaledTime < _civilianRetryTime))
        {
            return;
        }

        var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (factory == null || positioning == null || characters == null)
        {
            return;
        }

        // The native getter rebuilds a shared list; read it once per refresh, not once per NPC.
        var contenders = battle.AllContenders;

        if (!_civilianRefreshPending)
        {
            // Only queued, transitioning models retry; the world list is not rescanned each frame.
            foreach (var graphicsCharacter in PendingCivilianPoses.ToArray())
            {
                PendingCivilianPoses.Remove(graphicsCharacter);

                if (IsCivilianCandidate(graphicsCharacter, contenders, characters) &&
                    IsNearBattle(graphicsCharacter, contenders, positioning))
                {
                    TryStartCivilianPose(graphicsCharacter);
                }
            }

            _civilianRetryTime = Time.unscaledTime + 0.25f;
            return;
        }

        _civilianRefreshPending = false;
        PendingCivilianPoses.Clear();

        // Refresh only after roster, movement, spawn, load, or option changes. No NPC AI runs here.
        var nearby = new HashSet<GraphicsCharacter>();

        foreach (var graphicsCharacter in factory.GraphicsCharacters)
        {
            if (!IsCivilianCandidate(graphicsCharacter, contenders, characters))
            {
                continue;
            }

            if (IsNearBattle(graphicsCharacter, contenders, positioning))
            {
                nearby.Add(graphicsCharacter);
            }
        }

        foreach (var entry in CivilianPoses.ToArray())
        {
            if (!nearby.Contains(entry.Key) || !entry.Value.IsCurrent(entry.Key))
            {
                CivilianPoses.Remove(entry.Key);
                entry.Value.Restore(entry.Key);
            }
        }

        foreach (var graphicsCharacter in nearby)
        {
            TryStartCivilianPose(graphicsCharacter);
        }

        _civilianRetryTime = Time.unscaledTime + 0.25f;
    }

    private static bool IsNearBattle(
        GraphicsCharacter graphicsCharacter, IReadOnlyList<GameLocationCharacter> contenders,
        IGameLocationPositioningService positioning)
    {
        var position = positioning.GetGridPositionFromWorldPosition(graphicsCharacter.transform);

        return contenders.Any(contender =>
            contender.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
            int3.Distance(position, contender.LocationPosition) <= CivilianReactionRange);
    }

    private static void TryStartCivilianPose(GraphicsCharacter graphicsCharacter)
    {
        if (CivilianPoses.ContainsKey(graphicsCharacter) || InterruptedCivilians.Contains(graphicsCharacter))
        {
            return;
        }

        var animator = graphicsCharacter.Animator;

        if (animator.IsInTransition(0))
        {
            PendingCivilianPoses.Add(graphicsCharacter);
            return;
        }

        var pose = new CivilianPose(animator);
        CivilianPoses.Add(graphicsCharacter, pose);
        _applyingCivilianPose = graphicsCharacter;

        try
        {
            HandleBodyAnimation.Invoke(graphicsCharacter,
            [
                new GameLocationCharacterDefinitions.SpeechStartedParameters
                {
                    SpeakAnimationType = AnimationDefinitions.SpeakType.Fear_Idle,
                    CutAnimation = true,
                    LoopAnimation = true
                }
            ]);
        }
        catch (Exception exception)
        {
            CivilianPoses.Remove(graphicsCharacter);
            pose.Restore(graphicsCharacter);
            CivilianControllers[animator.runtimeAnimatorController] = false;
            Main.Error(exception);
        }
        finally
        {
            _applyingCivilianPose = null;
        }
    }

    private static bool IsCivilianCandidate(
        GraphicsCharacter graphicsCharacter, IReadOnlyList<GameLocationCharacter> contenders,
        IGameLocationCharacterService characters)
    {
        if (!graphicsCharacter || !graphicsCharacter.gameObject.activeInHierarchy ||
            !graphicsCharacter.Animator || !graphicsCharacter.Animator.isActiveAndEnabled ||
            !graphicsCharacter.Animator.runtimeAnimatorController || graphicsCharacter.Animator.layerCount == 0 ||
            graphicsCharacter.IsInCombat ||
            graphicsCharacter.RulesetCharacter is not RulesetCharacterMonster
            {
                IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false, ConjuredByParty: false
            } monster)
        {
            return false;
        }

        if (!SupportsCivilianPose(graphicsCharacter.Animator))
        {
            return false;
        }

        if (graphicsCharacter.CharacterType == GraphicsCharacterDefinitions.CharacterType.Simple)
        {
            // The native spawn callback sets this after positioning and its initial animation.
            // SetupAfterAnimator can notify us while that asynchronous callback is still pending.
            return graphicsCharacter.Animator.GetBool(AnimationDefinitions.IsInDialogId);
        }

        if (!graphicsCharacter.IsBoundToGameCharacter ||
            GameLocationCharacter.GetFromActor(monster) is not { } character ||
            character.IsSummonedCreature || character.MovingToDestination ||
            ServiceRepository.GetService<IGameLocationActionService>()?.IsCharacterActing(character) == true ||
            contenders.Contains(character) || characters.PartyCharacters.Contains(character) ||
            characters.GuestCharacters.Contains(character) ||
            graphicsCharacter.Animator.GetBool(AnimationDefinitions.IsInDialogId) &&
            !CivilianPoses.ContainsKey(graphicsCharacter))
        {
            return false;
        }

        return monster.Side == RuleDefinitions.Side.Neutral ||
               monster.Side == RuleDefinitions.Side.Ally && character.BoundGadgetId != 0 && !character.HasComplexActions;
    }

    private static bool SupportsCivilianPose(Animator animator)
    {
        var controller = animator.runtimeAnimatorController;

        if (!controller || HandleBodyAnimation == null)
        {
            return false;
        }

        if (!CivilianControllers.TryGetValue(controller, out var supported))
        {
            var parameters = animator.parameters;
            supported = controller.animationClips.Any(clip => clip && clip.name == "Fear_Idle") &&
                        CivilianBoolParameters.All(id => parameters.Any(parameter =>
                            parameter.nameHash == id && parameter.type == AnimatorControllerParameterType.Bool)) &&
                        CivilianFloatParameters.All(id => parameters.Any(parameter =>
                            parameter.nameHash == id && parameter.type == AnimatorControllerParameterType.Float)) &&
                        parameters.Any(parameter => parameter.nameHash == AnimationDefinitions.SpeakAnimationStartId &&
                                                    parameter.type == AnimatorControllerParameterType.Trigger);
            CivilianControllers.Add(controller, supported);
        }

        return supported;
    }

    private static void ClearCivilianPoses()
    {
        foreach (var entry in CivilianPoses)
        {
            entry.Value.Restore(entry.Key);
        }

        CivilianPoses.Clear();
        PendingCivilianPoses.Clear();
    }

    private sealed class CivilianPose(Animator animator)
    {
        private readonly Animator _animator = animator;
        private readonly RuntimeAnimatorController _controller = animator.runtimeAnimatorController;
        private readonly bool[] _boolValues = CivilianBoolParameters.Select(animator.GetBool).ToArray();
        private readonly float[] _floatValues = CivilianFloatParameters.Select(animator.GetFloat).ToArray();
        private readonly AnimatorStateInfo _state = animator.GetCurrentAnimatorStateInfo(0);

        internal bool IsCurrent(GraphicsCharacter graphicsCharacter)
        {
            return graphicsCharacter && graphicsCharacter.Animator == _animator && _animator &&
                   _animator.runtimeAnimatorController == _controller;
        }

        internal void Restore(GraphicsCharacter graphicsCharacter)
        {
            if (!IsCurrent(graphicsCharacter))
            {
                return;
            }

            _animator.ResetTrigger(AnimationDefinitions.SpeakAnimationStartId);

            for (var i = 0; i < CivilianBoolParameters.Length; i++)
            {
                _animator.SetBool(CivilianBoolParameters[i], _boolValues[i]);
            }

            for (var i = 0; i < CivilianFloatParameters.Length; i++)
            {
                _animator.SetFloat(CivilianFloatParameters[i], _floatValues[i]);
            }

            if (_animator.isActiveAndEnabled && _animator.HasState(0, _state.fullPathHash))
            {
                _animator.Play(_state.fullPathHash, 0, _state.normalizedTime % 1);
            }
        }
    }

    internal static float ClampSpeedMultiplier(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? DefaultSpeedMultiplier
            : Math.Min(MaxSpeedMultiplier, Math.Max(MinSpeedMultiplier, value));
    }

    internal static IDisposable BeginActionScope(CharacterAction action)
    {
        if (CivilianPoses.Count != 0)
        {
            InterruptCivilianReaction(action?.ActingCharacter?.RulesetCharacter);
        }

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
        SuspendCivilianReactions();
        foreach (var entry in AnimatorStates.ToArray())
        {
            if (entry.Key)
            {
                entry.Key.speed = entry.Value.OriginalSpeed;
            }
        }

        AnimatorStates.Clear();
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
