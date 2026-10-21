using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SolastaUnfinishedBusiness.Validators;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static class CivilianAnimationContext
{
    private const int CivilianBattleReactionRange = 12;
    private const float CivilianConversationCellSize = 4f;
    private static readonly Dictionary<GraphicsCharacter, CivilianPose> CivilianPoses = [];
    private static readonly HashSet<GraphicsCharacter> CulledCivilians = [];
    private static readonly Dictionary<Vector2Int, List<GraphicsCharacter>> CivilianConversationCells = [];
    private static readonly HashSet<GraphicsCharacter> InterruptedCivilians = [];
    private static readonly HashSet<GraphicsCharacter> PendingCivilianPoses = [];
    private static readonly Dictionary<GraphicsCharacter, RulesetCharacter> CivilianConversationPartners = [];
    private static readonly Dictionary<RuntimeAnimatorController, HashSet<AnimationDefinitions.SpeakType>>
        CivilianControllers = [];
    private static readonly Dictionary<RuntimeAnimatorController, bool> CivilianGameplayControllers = [];
    private static readonly List<Transform> CivilianSeats = [];
    private static readonly HashSet<Transform> CivilianBenchSeats = [];
    private static readonly Dictionary<Transform, CivilianSeatShape> CivilianSeatShapes = [];
    private static readonly List<GraphicsCharacter> NativeSeatedCivilians = [];
    private static readonly List<Transform> CivilianSocialSeats = [];
    private static readonly List<Transform> CivilianTables = [];
    private static readonly AnimationDefinitions.SpeakType[] CivilianTableSpeakTypes =
    [
        AnimationDefinitions.SpeakType.SitTableAttentive, AnimationDefinitions.SpeakType.SitTableBored,
        AnimationDefinitions.SpeakType.SitTableChill
    ];
    private static readonly AnimationDefinitions.SpeakType[] NativeSeatedSpeakTypes =
    [
        .. CivilianTableSpeakTypes,
        AnimationDefinitions.SpeakType.SitBored, AnimationDefinitions.SpeakType.SitBench,
        AnimationDefinitions.SpeakType.Council_Sit, AnimationDefinitions.SpeakType.Sit_Mourning,
        AnimationDefinitions.SpeakType.SitGroggy, AnimationDefinitions.SpeakType.SitFootOnTable,
        AnimationDefinitions.SpeakType.Orenetis_Sit_Throne_A, AnimationDefinitions.SpeakType.Orenetis_Sit_Throne_B,
        AnimationDefinitions.SpeakType.Orenetis_Sit_Throne_C, AnimationDefinitions.SpeakType.Orenetis_Sit_Throne_D
    ];
    private static readonly AnimationDefinitions.SpeakType[] CivilianSpeakTypes =
    [
        AnimationDefinitions.SpeakType.Fear_Idle, AnimationDefinitions.SpeakType.Neutral,
        AnimationDefinitions.SpeakType.ArmsCrossed,
        .. NativeSeatedSpeakTypes
    ];
    private static readonly MethodInfo HandleBodyAnimation =
        AccessTools.Method(typeof(GraphicsCharacter), "HandleBodyAnimation");
    private static readonly MethodInfo InitializeAnimatorBehaviours =
        AccessTools.Method(typeof(GraphicsCharacter), "InitializeAnimatorBehaviours");
    private static readonly MethodInfo InitializeLayers =
        AccessTools.Method(typeof(GraphicsCharacter), "InitializeLayers");
    private static readonly MethodInfo CheckWieldedItems =
        AccessTools.Method(typeof(GraphicsCharacter), "CheckWieldedItems");
    private static readonly AccessTools.FieldRef<GraphicsCharacter, TA.Coroutine> CivilianLookAtBlend =
        AccessTools.FieldRefAccess<GraphicsCharacter, TA.Coroutine>("layerWeightCoroutine");

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
        AnimationDefinitions.CurrentSpeakVariationId, AnimationDefinitions.CurrentPostureId,
        AnimationDefinitions.IsInCombatId
    ];

    private static GameLocationBattle _civilianBattle;
    private static GraphicsCharacter _applyingCivilianPose;
    private static bool _civilianRefreshPending;
    private static bool _civilianLocationSuspended = true;
    private static bool _civilianPresentationBlocked;
    private static bool _civilianAmbientRefreshInProgress;
    private static float _civilianRetryTime;
    private static float _civilianAmbientRefreshTime;
    private static WorldLocation _civilianSeatLocation;

    internal static void StartCivilianReactions(GameLocationBattle battle)
    {
        StopCivilianReactions();
        _civilianBattle = battle;
        RequestCivilianRefresh();
    }

    internal static void StopCivilianReactions()
    {
        ClearCivilianPoses();
        NativeSeatedCivilians.Clear();
        _civilianBattle = null;
        _civilianRefreshPending = false;
        _civilianPresentationBlocked = false;
        InterruptedCivilians.Clear();
        _civilianAmbientRefreshTime = 0;
    }

    internal static void SuspendCivilianReactions()
    {
        StopCivilianReactions();
        _civilianLocationSuspended = true;
        CivilianControllers.Clear();
        CivilianGameplayControllers.Clear();
        CivilianConversationPartners.Clear();
        CivilianConversationCells.Clear();
        CulledCivilians.Clear();
        ClearCivilianSeats();
    }

    internal static void ResumeCivilianReactions()
    {
        _civilianLocationSuspended = false;
        ClearCivilianSeats();
        StartCivilianReactions(Gui.Battle);
    }

    internal static void RequestCivilianRefresh()
    {
        if (Main.Settings.EnableCivilianBattleReactions || Main.Settings.EnableCivilianAmbientAnimations)
        {
            _civilianRefreshPending = true;
            _civilianAmbientRefreshTime = 0;
        }
    }

    internal static void RefreshCivilianReactions()
    {
        ClearCivilianPoses();
        InterruptedCivilians.Clear();
        _civilianBattle ??= Gui.Battle;
        RequestCivilianRefresh();
        UpdateCivilianReactions(_civilianBattle);
        UpdateAmbientCivilianAnimations();
    }

    internal static void PrepareCivilianPresentation(GraphicsCharacter graphicsCharacter, string methodName)
    {
        if (ReferenceEquals(graphicsCharacter, null))
        {
            return;
        }

        if (methodName is "SpeechEnded" or "ListenEnded")
        {
            return;
        }

        // Keep ambient posture through native speech/listening updates. A separate
        // authored body animation or dialogue boundary releases it before taking ownership.
        if (methodName is "SpeechStarted" or "ListenStarted" &&
            HasCurrentAmbientCivilianPose(graphicsCharacter) &&
            !IsCivilianPresentationBlocked())
        {
            return;
        }

        InterruptCivilianReaction(graphicsCharacter);
    }

    internal static void TrackCivilianConversation(GraphicsCharacter graphicsCharacter, string methodName, object[] args)
    {
        if (!graphicsCharacter)
        {
            return;
        }

        var partner = methodName switch
        {
            "SpeechStarted" when args[0] is GameLocationCharacterDefinitions.SpeechStartedParameters parameters =>
                parameters.LookAtCharacter?.RulesetCharacter,
            "ListenStarted" => (args[1] as GameLocationCharacter)?.RulesetCharacter,
            _ => null
        };

        if (methodName is "SpeechStarted" or "ListenStarted")
        {
            if (partner != null)
            {
                CivilianConversationPartners[graphicsCharacter] = partner;
            }
            else
            {
                CivilianConversationPartners.Remove(graphicsCharacter);
            }
        }
        else if (methodName is "SpeechEnded" or "ListenEnded")
        {
            CivilianConversationPartners.Remove(graphicsCharacter);
        }
    }

    internal static void InterruptCivilianReaction(GraphicsCharacter graphicsCharacter)
    {
        if (ReferenceEquals(graphicsCharacter, null) ||
            ReferenceEquals(graphicsCharacter, _applyingCivilianPose))
        {
            return;
        }

        var pending = PendingCivilianPoses.Remove(graphicsCharacter);

        if (ReleaseCivilianPose(graphicsCharacter))
        {
            pending = true;
        }

        if (pending && _civilianBattle != null)
        {
            // A scripted action owns this NPC until the presentation sequence or battle ends.
            InterruptedCivilians.Add(graphicsCharacter);
        }
    }

    internal static void ForgetCivilianReaction(GraphicsCharacter graphicsCharacter)
    {
        InterruptCivilianReaction(graphicsCharacter);
        InterruptedCivilians.Remove(graphicsCharacter);
        CivilianConversationPartners.Remove(graphicsCharacter);
        CulledCivilians.Remove(graphicsCharacter);
    }

    internal static void SetCivilianCullingVisibility(GraphicsCharacter graphicsCharacter, bool visible)
    {
        if (visible)
        {
            if (CulledCivilians.Remove(graphicsCharacter) &&
                !HasCurrentAmbientCivilianPose(graphicsCharacter))
            {
                // Existing ambient poses resume in the native Animator. Camera panning
                // must not turn their periodic environment refresh into a per-frame scan.
                RequestCivilianRefresh();
            }
        }
        else
        {
            CulledCivilians.Add(graphicsCharacter);
        }
    }

    private static bool HasCulledAmbientPose(GraphicsCharacter graphicsCharacter)
    {
        return CulledCivilians.Contains(graphicsCharacter) &&
               HasCurrentAmbientCivilianPose(graphicsCharacter);
    }

    internal static void RefreshCivilianEquipment(GraphicsCharacter graphicsCharacter)
    {
        if (!graphicsCharacter || ReferenceEquals(graphicsCharacter, _applyingCivilianPose))
        {
            return;
        }

        // Equipment refresh can replace weapon models without changing the actor or animator.
        // Release the old presentation, then select a posture for the new equipment.
        ReleaseCivilianPose(graphicsCharacter);
        PendingCivilianPoses.Remove(graphicsCharacter);
        RequestCivilianRefresh();
    }

    internal static void InterruptCivilianReaction(RulesetCharacter character)
    {
        if (character == null || CivilianPoses.Count == 0 && PendingCivilianPoses.Count == 0)
        {
            return;
        }

        foreach (var graphicsCharacter in CivilianPoses.Keys
                     .Concat(PendingCivilianPoses)
                     .Distinct()
                     .ToArray())
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

        var blocked = IsCivilianPresentationBlocked();

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
                    TryStartCivilianPose(graphicsCharacter, SelectCivilianBattlePosture(graphicsCharacter));
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

        ReleaseUnusedCivilianPoses(nearby);

        foreach (var graphicsCharacter in nearby)
        {
            TryStartCivilianPose(graphicsCharacter, SelectCivilianBattlePosture(graphicsCharacter));
        }

        _civilianRetryTime = Time.unscaledTime + 0.25f;
    }

    private static bool IsCivilianPresentationBlocked()
    {
        return ServiceRepository.GetService<INarrativeDirectionService>()?.IsSequenceInProgress == true ||
               ServiceRepository.GetService<IViewLocationContextualService>()?.IsActive == true;
    }

    internal static void UpdateAmbientCivilianAnimations()
    {
        if (_civilianLocationSuspended || Gui.Battle != null)
        {
            return;
        }

        if (!Main.Settings.EnableCivilianAmbientAnimations)
        {
            NativeSeatedCivilians.Clear();
            if (CivilianPoses.Count != 0)
            {
                ClearCivilianPoses();
            }

            return;
        }

        var location = ServiceRepository.GetService<IGameLocationService>();
        if (location?.LocationIsReady != true)
        {
            return;
        }

        var blocked = IsCivilianPresentationBlocked();

        if (blocked != _civilianPresentationBlocked)
        {
            _civilianPresentationBlocked = blocked;
            ClearCivilianPoses();
            InterruptedCivilians.Clear();
            _civilianAmbientRefreshTime = 0;
        }

        if (blocked || Time.unscaledTime < _civilianAmbientRefreshTime)
        {
            return;
        }

        _civilianAmbientRefreshTime = Time.unscaledTime + 2;
        var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
        if (factory == null || characters == null)
        {
            return;
        }

        var graphicsCharacters = factory.GraphicsCharacters.ToArray();
        RefreshCivilianSeats(location.WorldLocation);
        RefreshNativeSeatedCivilians(graphicsCharacters);
        var civilians = new HashSet<GraphicsCharacter>();
        var contenders = Array.Empty<GameLocationCharacter>();

        foreach (var graphicsCharacter in graphicsCharacters)
        {
            if (!IsCivilianCandidate(graphicsCharacter, contenders, characters,
                    AnimationDefinitions.SpeakType.Neutral))
            {
                continue;
            }

            if (!HasCurrentCivilianPose(graphicsCharacter) && !HasUnassignedCivilianPosture(graphicsCharacter.Animator))
            {
                continue;
            }

            civilians.Add(graphicsCharacter);
        }

        ReleaseUnusedCivilianPoses(civilians);

        RefreshCivilianConversationCells(graphicsCharacters, characters);
        _civilianAmbientRefreshInProgress = true;
        try
        {
            foreach (var graphicsCharacter in civilians.OrderBy(GetCivilianVariationSeed))
            {
                // Native camera culling temporarily disables simple actors. Keep their
                // owned presentation until visibility returns or an explicit action interrupts it.
                if (!graphicsCharacter.gameObject.activeInHierarchy && HasCulledAmbientPose(graphicsCharacter))
                {
                    continue;
                }

                var type = SelectAmbientCivilianPosture(graphicsCharacter);
                if (CivilianPoses.TryGetValue(graphicsCharacter, out var pose) && pose.Type != type)
                {
                    ReleaseCivilianPose(graphicsCharacter);
                }

                if (!CivilianPoses.ContainsKey(graphicsCharacter))
                {
                    TryStartCivilianPose(graphicsCharacter, type, true);
                }
            }
        }
        finally
        {
            _civilianAmbientRefreshInProgress = false;
        }
    }

    private static bool HasUnassignedCivilianPosture(Animator animator)
    {
        return !animator.GetBool(AnimationDefinitions.DialogIsLoopingId) &&
               !animator.GetBool(AnimationDefinitions.CutAnimationForDialogId) &&
               !animator.GetBool(AnimationDefinitions.IsKeepingPostureId) &&
               animator.GetFloat(AnimationDefinitions.SpeakTypeId) == (float)AnimationDefinitions.SpeakType.Neutral &&
               animator.GetFloat(AnimationDefinitions.NextPostureTypeId) == 0 &&
               animator.GetFloat(AnimationDefinitions.CurrentPostureId) == 0;
    }

    private static AnimationDefinitions.SpeakType SelectAmbientCivilianPosture(GraphicsCharacter graphicsCharacter)
    {
        var directedConversation = HasDirectedCivilianConversationPartner(graphicsCharacter);
        if (IsCivilianKeepingWatch(graphicsCharacter) && !directedConversation)
        {
            return AnimationDefinitions.SpeakType.None;
        }

        var seed = GetCivilianVariationSeed(graphicsCharacter);
        if (TryFindCivilianSeat(graphicsCharacter, out var seating))
        {
            return seating.Type;
        }

        foreach (var seat in CivilianSeats)
        {
            if (!seat || !seat.gameObject.activeInHierarchy || CivilianBenchSeats.Contains(seat) ||
                GetCivilianSeatShape(seat) != null)
            {
                continue;
            }

            var offset = graphicsCharacter.transform.position - seat.position;
            var facing = Vector3.Dot(graphicsCharacter.transform.forward, seat.forward);
            if (Math.Abs(offset.y) > 0.35f || offset.x * offset.x + offset.z * offset.z > 0.25f ||
                facing < 0.8f || !IsCivilianSeatAvailable(graphicsCharacter, seat,
                    graphicsCharacter.transform.position, false))
            {
                continue;
            }

            var type = HasCivilianTableInFront(graphicsCharacter.transform)
                ? CivilianTableSpeakTypes[(int)(seed % (ulong)CivilianTableSpeakTypes.Length)]
                : AnimationDefinitions.SpeakType.SitBored;
            if (SupportsCivilianPose(graphicsCharacter.Animator, type))
            {
                return type;
            }
        }

        // Neutral's looping native dialogue clip includes hand gestures even when
        // IsSpeaking is false. It belongs to a conversation, not an isolated idle.
        return directedConversation || HasCivilianConversationPartner(graphicsCharacter)
            ? AnimationDefinitions.SpeakType.Neutral
            : AnimationDefinitions.SpeakType.None;
    }

    private static bool TryFindCivilianSeat(GraphicsCharacter graphicsCharacter, out CivilianSeating seating)
    {
        seating = null;
        var character = graphicsCharacter.transform;
        var position = character.position;
        var forward = character.forward;
        if (CivilianPoses.TryGetValue(graphicsCharacter, out var current))
        {
            position = current.GetAuthoredPosition(character);
            forward = current.GetAuthoredForward(character);
        }

        var bestDistance = float.MaxValue;
        foreach (var seat in CivilianSeats)
        {
            if (!seat || !seat.gameObject.activeInHierarchy)
            {
                continue;
            }

            // The authored footprint indicates sitting intent; a passer-by beside
            // another chair must not be pulled into it.
            var offset = position - seat.position;
            offset.y = 0;
            var bench = CivilianBenchSeats.Contains(seat);
            if (Math.Abs(position.y - seat.position.y) > 0.35f || offset.sqrMagnitude > (bench ? 4f : 0.25f))
            {
                continue;
            }

            var shape = GetCivilianSeatShape(seat);
            if (shape == null)
            {
                continue;
            }

            var facing = shape.Mesh.forward;
            facing.y = 0;
            facing.Normalize();
            if (shape.IsBench && Vector3.Dot(facing, forward) < 0)
            {
                facing = -facing;
            }

            var center = shape.Mesh.TransformPoint(shape.Center);
            var side = Vector3.Cross(Vector3.up, facing);
            if (shape.IsBench && (Math.Abs(Vector3.Dot(position - center, side)) > shape.HalfWidth + 0.3f ||
                                  Math.Abs(Vector3.Dot(position - center, facing)) > 0.5f))
            {
                continue;
            }
            var sideOffset = shape.IsBench
                ? Mathf.Clamp(Vector3.Dot(position - center, side), -shape.HalfWidth, shape.HalfWidth)
                : 0f;
            var target = center + side * sideOffset;
            var floor = seat.position.y;
            var height = target.y - floor;
            target.y = position.y;
            var distance = (position - target).sqrMagnitude;
            if (height is < 0.2f or > 0.7f || distance > 0.49f || distance >= bestDistance ||
                !IsCivilianSeatAvailable(graphicsCharacter, seat, target, shape.IsBench))
            {
                continue;
            }

            var type = HasCivilianTableInFront(target, facing)
                ? CivilianTableSpeakTypes[(int)(GetCivilianVariationSeed(graphicsCharacter) %
                                              (ulong)CivilianTableSpeakTypes.Length)]
                : shape.IsBench ? AnimationDefinitions.SpeakType.SitBench : AnimationDefinitions.SpeakType.SitBored;
            if (!SupportsCivilianPose(graphicsCharacter.Animator, type))
            {
                continue;
            }

            bestDistance = distance;
            seating = new CivilianSeating(seat, target, Quaternion.LookRotation(facing), floor + height, floor,
                shape.IsBench, type);
        }

        return seating != null;
    }

    private static bool IsCivilianSeatAvailable(GraphicsCharacter character, Transform seat, Vector3 position,
        bool bench)
    {
        if (CivilianPoses.Any(entry => entry.Key != character &&
                (entry.Value.Seating is { } occupied
                    ? occupied.Seat == seat && (!bench || (occupied.Position - position).sqrMagnitude < 0.36f)
                    : entry.Key && entry.Value.IsAmbient && IsSeatedPosture((float)entry.Value.Type) &&
                      IsNearCivilianSeat(entry.Key.transform.position, position))))
        {
            return false;
        }

        // Scripted sitting postures remain native and never acquire our pose lease.
        // Reserve their space too; the snapshot is refreshed once per ambient update.
        return !NativeSeatedCivilians.Any(other => other && other != character &&
            other.gameObject.activeInHierarchy &&
            IsNearCivilianSeat(other.transform.position, position));
    }

    private static bool IsNearCivilianSeat(Vector3 occupant, Vector3 position)
    {
        return Math.Abs(occupant.y - position.y) < 0.35f &&
               new Vector2(occupant.x - position.x, occupant.z - position.z).sqrMagnitude < 0.36f;
    }

    private static void RefreshNativeSeatedCivilians(IEnumerable<GraphicsCharacter> characters)
    {
        NativeSeatedCivilians.Clear();
        foreach (var character in characters)
        {
            if (!character || !character.gameObject.activeInHierarchy || CivilianPoses.ContainsKey(character) ||
                !character.Animator || !character.Animator.runtimeAnimatorController)
            {
                continue;
            }

            var animator = character.Animator;
            SupportsCivilianPose(animator);
            if (!CivilianControllers.TryGetValue(animator.runtimeAnimatorController, out var supported) ||
                supported.Count == 0)
            {
                continue;
            }

            if (IsSeatedPosture(animator.GetFloat(AnimationDefinitions.CurrentPostureId)) ||
                IsSeatedPosture(animator.GetFloat(AnimationDefinitions.NextPostureTypeId)) ||
                IsSeatedPosture(animator.GetFloat(AnimationDefinitions.CurrentSpeakTypeId)) ||
                IsSeatedPosture(animator.GetFloat(AnimationDefinitions.SpeakTypeId)))
            {
                NativeSeatedCivilians.Add(character);
            }
        }
    }

    private static bool IsSeatedPosture(float type)
    {
        // Floor-sitting animations intentionally do not reserve a chair or bench.
        return NativeSeatedSpeakTypes.Any(posture => type == (float)posture);
    }

    private static CivilianSeatShape GetCivilianSeatShape(Transform seat)
    {
        if (CivilianSeatShapes.TryGetValue(seat, out var shape))
        {
            if (shape == null || shape.Mesh)
            {
                return shape;
            }

            CivilianSeatShapes.Remove(seat);
        }

        var meshes = seat.GetComponentsInChildren<MeshFilter>().Where(filter => filter.sharedMesh &&
            filter.TryGetComponent<MeshRenderer>(out var renderer) && renderer.enabled).ToArray();
        if (meshes.Length == 0)
        {
            return null;
        }

        // Compound decorated props and unusually shaped seating have no reliable
        // seat plane. Preserve them instead of guessing a position from total bounds.
        shape = null;
        if (meshes.Length == 1 && Vector3.Dot(meshes[0].transform.up, Vector3.up) > 0.98f)
        {
            var mesh = meshes[0];
            var bounds = mesh.sharedMesh.bounds;
            var scale = mesh.transform.lossyScale;
            var height = bounds.size.y * Math.Abs(scale.y);
            var width = bounds.size.x * Math.Abs(scale.x);
            var depth = bounds.size.z * Math.Abs(scale.z);
            var bench = CivilianBenchSeats.Contains(seat);
            if (width >= 0.25f && depth is >= 0.2f and <= 0.6f &&
                (bench ? height is >= 0.2f and <= 0.5f && width <= 3f
                    : height is >= 0.6f and <= 1.2f && width <= 0.75f))
            {
                // Backless benches end at the seat; a conventional chair's seat
                // is at the midpoint of its upright back and legs.
                var center = bounds.center;
                if (bench)
                {
                    center.y = bounds.max.y;
                }

                shape = new CivilianSeatShape(mesh.transform, center, Mathf.Max(0f, width / 2f - 0.2f), bench);
            }
        }

        CivilianSeatShapes.Add(seat, shape);
        return shape;
    }

    internal static void UpdateCivilianSeating(GraphicsCharacter character)
    {
        if (CivilianPoses.TryGetValue(character, out var pose) && pose.Seating != null)
        {
            if (!pose.UpdateSeating(character))
            {
                ReleaseCivilianPose(character);
            }
        }
    }

    internal static void ApplyCivilianSeatingFeet(GraphicsCharacter character)
    {
        if (CivilianPoses.TryGetValue(character, out var pose))
        {
            pose.ApplySeatingFeet(character);
        }
    }

    private static bool HasCivilianConversationPartner(GraphicsCharacter graphicsCharacter)
    {
        if (_civilianAmbientRefreshInProgress)
        {
            var cell = GetCivilianConversationCell(graphicsCharacter.transform.position);
            for (var x = cell.x - 1; x <= cell.x + 1; x++)
            {
                for (var z = cell.y - 1; z <= cell.y + 1; z++)
                {
                    if (CivilianConversationCells.TryGetValue(new Vector2Int(x, z), out var neighbors) &&
                        neighbors.Any(other => IsCivilianConversationPair(graphicsCharacter, other)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
        return factory?.GraphicsCharacters.Any(other => IsCivilianConversationCandidate(other, characters) &&
            IsCivilianConversationPair(graphicsCharacter, other)) == true;
    }

    private static Vector2Int GetCivilianConversationCell(Vector3 position)
    {
        return new Vector2Int(Mathf.FloorToInt(position.x / CivilianConversationCellSize),
            Mathf.FloorToInt(position.z / CivilianConversationCellSize));
    }

    private static void RefreshCivilianConversationCells(IEnumerable<GraphicsCharacter> graphicsCharacters,
        IGameLocationCharacterService characters)
    {
        foreach (var neighbors in CivilianConversationCells.Values)
        {
            neighbors.Clear();
        }

        foreach (var other in graphicsCharacters)
        {
            if (!IsCivilianConversationCandidate(other, characters))
            {
                continue;
            }

            var cell = GetCivilianConversationCell(other.transform.position);
            if (!CivilianConversationCells.TryGetValue(cell, out var neighbors))
            {
                neighbors = [];
                CivilianConversationCells.Add(cell, neighbors);
            }

            neighbors.Add(other);
        }
    }

    private static bool IsCivilianConversationCandidate(GraphicsCharacter other,
        IGameLocationCharacterService characters)
    {
        if (!other || !other.gameObject.activeInHierarchy && !CulledCivilians.Contains(other) ||
            other.RulesetCharacter is not RulesetCharacterMonster
            {
                IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false, ConjuredByParty: false
            } || IsCivilianKeepingWatch(other) || other.IsInCombat || !other.Animator ||
            other.Animator.GetFloat(AnimationDefinitions.CharacterSpeedId) > 0.01f)
        {
            return false;
        }

        var gameCharacter = GameLocationCharacter.GetFromActor(other.RulesetCharacter);
        return gameCharacter == null || characters?.PartyCharacters.Contains(gameCharacter) != true &&
            characters?.GuestCharacters.Contains(gameCharacter) != true;
    }

    private static bool IsCivilianConversationPair(GraphicsCharacter graphicsCharacter, GraphicsCharacter other)
    {
        if (other == graphicsCharacter)
        {
            return false;
        }

        var character = graphicsCharacter.transform;
        var offset = other.transform.position - character.position;
        if (Math.Abs(offset.y) > 0.75f)
        {
            return false;
        }

        offset.y = 0;
        if (offset.sqrMagnitude is < 0.16f or > 6.25f)
        {
            return false;
        }

        var direction = offset.normalized;
        return Vector3.Dot(character.forward, direction) > 0.5f &&
               Vector3.Dot(other.transform.forward, -direction) > 0.5f;
    }

    private static bool HasDirectedCivilianConversationPartner(GraphicsCharacter graphicsCharacter)
    {
        return CivilianConversationPartners.TryGetValue(graphicsCharacter, out var partner) &&
               !partner.IsDeadOrDyingOrUnconscious && !partner.IsRemovedFromTheGame;
    }

    private static bool IsCivilianKeepingWatch(GraphicsCharacter graphicsCharacter)
    {
        return graphicsCharacter.RulesetCharacter is RulesetCharacterMonster monster &&
               monster.MonsterDefinition?.DefaultBattleDecisionPackage?.Package?.WeightedDecisions
                   .Any(decision => decision.Decision?.ActivityType == "Intercept") == true;
    }

    private static bool IsCivilianNearSocialSeat(Transform character)
    {
        // Seating placement is also meaningful when no native seated pose fits the furniture.
        // A nearby table alone does not make a guard standing beside it off duty.
        foreach (var seat in CivilianSocialSeats)
        {
            if (!seat || !seat.gameObject.activeInHierarchy)
            {
                continue;
            }

            var offset = character.position - seat.position;
            if (Math.Abs(offset.y) <= 0.35f && offset.x * offset.x + offset.z * offset.z <= 0.25f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCivilianWeapon(ItemDefinition item)
    {
        return item && (item.IsWeapon && !ValidatorsWeapon.IsUnarmed(item) || ValidatorsWeapon.IsShield(item));
    }

    private static ItemDefinition GetCivilianWeapon(GraphicsCharacter graphicsCharacter)
    {
        var wielded = graphicsCharacter.WieldedRulesetItems;
        if (wielded != null)
        {
            if (wielded.TryGetValue(EquipmentDefinitions.SlotTypeMainHand, out var mainHandItem) &&
                IsCivilianWeapon(mainHandItem?.ItemDefinition))
            {
                return mainHandItem.ItemDefinition;
            }

            var item = wielded.Values.FirstOrDefault(value => IsCivilianWeapon(value?.ItemDefinition));
            if (item != null)
            {
                return item.ItemDefinition;
            }
        }

        if (graphicsCharacter.RulesetCharacter is not RulesetCharacterMonster monster)
        {
            return null;
        }

        var presentation = monster.HumanoidMonsterPresentationDefinition;
        if (presentation?.OverrideWieldedItems == true)
        {
            return IsCivilianWeapon(presentation.ItemDefinitionMainHand)
                ? presentation.ItemDefinitionMainHand
                : IsCivilianWeapon(presentation.ItemDefinitionOffHand) ? presentation.ItemDefinitionOffHand : null;
        }

        foreach (var iteration in monster.MonsterDefinition.AttackIterations)
        {
            var attack = iteration.MonsterAttackDefinition;
            if (IsCivilianWeapon(attack?.ItemDefinitionMainHand))
            {
                return attack.ItemDefinitionMainHand;
            }

            if (IsCivilianWeapon(attack?.ItemDefinitionOffHand))
            {
                return attack.ItemDefinitionOffHand;
            }
        }

        return null;
    }

    private static AnimationDefinitions.SpeakType SelectCivilianBattlePosture(GraphicsCharacter graphicsCharacter)
    {
        var weapon = GetCivilianWeapon(graphicsCharacter);
        if (weapon || IsCivilianKeepingWatch(graphicsCharacter))
        {
            return AnimationDefinitions.SpeakType.None;
        }

        // An actual spell repertoire distinguishes an experienced caster from a civilian.
        // Empty casting markers do not supply an authored combat stance.
        return graphicsCharacter.RulesetCharacter.SpellRepertoires.Any(repertoire =>
            repertoire.KnownCantrips.Count > 0 || repertoire.KnownSpells.Count > 0 ||
            repertoire.PreparedSpells.Count > 0 || repertoire.AutoPreparedSpells.Count > 0)
            ? AnimationDefinitions.SpeakType.None
            : AnimationDefinitions.SpeakType.Fear_Idle;
    }

    private static bool HasCivilianTableInFront(Transform character)
    {
        return HasCivilianTableInFront(character.position, character.forward);
    }

    private static bool HasCivilianTableInFront(Vector3 position, Vector3 forward)
    {
        foreach (var table in CivilianTables)
        {
            if (!table || !table.gameObject.activeInHierarchy)
            {
                continue;
            }

            var offset = table.position - position;
            if (Math.Abs(offset.y) <= 0.35f && offset.sqrMagnitude <= 2.25f &&
                Vector3.Dot(offset, forward) > 0.5f)
            {
                return true;
            }
        }

        return false;
    }

    private static ulong GetCivilianVariationSeed(GraphicsCharacter graphicsCharacter)
    {
        return graphicsCharacter.RulesetCharacter.Guid != 0
            ? graphicsCharacter.RulesetCharacter.Guid
            : unchecked((ulong)graphicsCharacter.GetInstanceID());
    }

    private static void ClearCivilianSeats()
    {
        CivilianSeats.Clear();
        CivilianBenchSeats.Clear();
        CivilianSeatShapes.Clear();
        CivilianSocialSeats.Clear();
        CivilianTables.Clear();
        _civilianSeatLocation = null;
    }

    private static void RefreshCivilianSeats(WorldLocation worldLocation)
    {
        if (ReferenceEquals(worldLocation, _civilianSeatLocation))
        {
            return;
        }

        ClearCivilianSeats();
        _civilianSeatLocation = worldLocation;
        if (!worldLocation || worldLocation.WorldSectors == null)
        {
            return;
        }

        // Furniture is identified by its native semantic title, including modded blueprints.
        // Chairs and backless benches can supply a sitting position from their
        // actual mesh. Decorated stools remain social cues, without a forced pose.
        var blueprints = DatabaseRepository.GetDatabase<PropBlueprint>()
            .Where(blueprint => IsCivilianSeatTitle(blueprint.GuiPresentation.Title) ||
                IsCivilianTableTitle(blueprint.GuiPresentation.Title))
            .ToDictionary(blueprint => blueprint.Name, blueprint => blueprint.GuiPresentation.Title);
        foreach (var sector in worldLocation.WorldSectors)
        {
            var environment = sector ? sector.transform.Find("Environment") : null;
            if (!environment)
            {
                continue;
            }

            foreach (var transform in environment.GetComponentsInChildren<Transform>(true))
            {
                if (!blueprints.TryGetValue(transform.name, out var title))
                {
                    continue;
                }

                if (IsCivilianTableTitle(title))
                {
                    CivilianTables.Add(transform);
                }
                else
                {
                    CivilianSocialSeats.Add(transform);
                    if (title is "Prop/&ChairTitle" or "Prop/&BenchTitle")
                    {
                        CivilianSeats.Add(transform);
                        if (title == "Prop/&BenchTitle")
                        {
                            CivilianBenchSeats.Add(transform);
                        }
                    }
                }
            }
        }
    }

    private static bool IsCivilianTableTitle(string title)
    {
        return title?.StartsWith("Prop/&Table", StringComparison.Ordinal) == true &&
               title.IndexOf("Destroyed", StringComparison.Ordinal) < 0;
    }

    private static bool IsCivilianSeatTitle(string title)
    {
        return title is "Prop/&ChairTitle" or "Prop/&BenchTitle" or "Prop/&StoolTitle";
    }

    private static bool IsNearBattle(
        GraphicsCharacter graphicsCharacter, IReadOnlyList<GameLocationCharacter> contenders,
        IGameLocationPositioningService positioning)
    {
        var position = positioning.GetGridPositionFromWorldPosition(graphicsCharacter.transform);

        return contenders.Any(contender =>
            contender.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
            int3.Distance(position, contender.LocationPosition) <= CivilianBattleReactionRange);
    }

    private static void TryStartCivilianPose(GraphicsCharacter graphicsCharacter,
        AnimationDefinitions.SpeakType type = AnimationDefinitions.SpeakType.Fear_Idle, bool ambient = false)
    {
        if (CivilianPoses.ContainsKey(graphicsCharacter) ||
            InterruptedCivilians.Contains(graphicsCharacter))
        {
            return;
        }

        var animator = graphicsCharacter.Animator;

        if (animator.IsInTransition(0))
        {
            if (!ambient)
            {
                PendingCivilianPoses.Add(graphicsCharacter);
            }
            return;
        }

        var pose = new CivilianPose(graphicsCharacter, type, ambient);
        CivilianPoses.Add(graphicsCharacter, pose);
        var previousApplyingPose = _applyingCivilianPose;
        _applyingCivilianPose = graphicsCharacter;

        try
        {
            if (ambient && type != AnimationDefinitions.SpeakType.None &&
                TryFindCivilianSeat(graphicsCharacter, out var seating) && seating.Type == type)
            {
                pose.SetSeating(graphicsCharacter, seating);
            }

            if (type == AnimationDefinitions.SpeakType.None)
            {
                // Lightweight civilian controllers have dialogue poses, but lack the native
                // weapon-specific idle graph. Borrow the already loaded gameplay controller.
                if (!pose.ApplyGameplayPosture(graphicsCharacter, !ambient))
                {
                    ReleaseCivilianPose(graphicsCharacter);
                    return;
                }
            }
            else
            {
                HandleBodyAnimation.Invoke(graphicsCharacter,
                [
                    new GameLocationCharacterDefinitions.SpeechStartedParameters
                    {
                        SpeakAnimationType = type,
                        SpeakAnimationVariation = ambient &&
                            AnimationDefinitions.SpeakTypeVariations.TryGetValue(type, out var count) && count > 0
                                ? (int)(GetCivilianVariationSeed(graphicsCharacter) % (ulong)count)
                                : 0,
                        CutAnimation = true,
                        LoopAnimation = true
                    }
                ]);
            }

            var carryWeapons = !ambient || type == AnimationDefinitions.SpeakType.None &&
                (IsCivilianKeepingWatch(graphicsCharacter) ||
                 !graphicsCharacter.HideWieldedItemsWhenPassive &&
                 !IsCivilianNearSocialSeat(graphicsCharacter.transform));
            pose.SetWieldedWeaponsVisible(graphicsCharacter, carryWeapons);
        }
        catch (Exception exception)
        {
            ReleaseCivilianPose(graphicsCharacter);
            // A single model's execution failure does not invalidate the shared controller's
            // proven clip/parameter support. Let the next presentation boundary retry this NPC.
            InterruptedCivilians.Add(graphicsCharacter);
            Main.Error(exception);
        }
        finally
        {
            _applyingCivilianPose = previousApplyingPose;
        }
    }

    private static bool IsCivilianCandidate(
        GraphicsCharacter graphicsCharacter, IReadOnlyList<GameLocationCharacter> contenders,
        IGameLocationCharacterService characters,
        AnimationDefinitions.SpeakType type = AnimationDefinitions.SpeakType.None)
    {
        if (!graphicsCharacter || !graphicsCharacter.Animator || !graphicsCharacter.Animator.enabled ||
            (!graphicsCharacter.gameObject.activeInHierarchy || !graphicsCharacter.Animator.isActiveAndEnabled) &&
            !HasCulledAmbientPose(graphicsCharacter) ||
            !graphicsCharacter.Animator.runtimeAnimatorController || graphicsCharacter.Animator.layerCount == 0 ||
            graphicsCharacter.IsInCombat && !HasCurrentCivilianPose(graphicsCharacter) ||
            graphicsCharacter.RulesetCharacter is not RulesetCharacterMonster
            {
                IsDeadOrDyingOrUnconscious: false, IsRemovedFromTheGame: false, ConjuredByParty: false
            } monster)
        {
            return false;
        }

        if (type == AnimationDefinitions.SpeakType.None)
        {
            type = SelectCivilianBattlePosture(graphicsCharacter);
        }

        if (type == AnimationDefinitions.SpeakType.None
                ? !HasCivilianGameplayController(graphicsCharacter) ||
                  !SupportsCivilianPose(graphicsCharacter.Animator, AnimationDefinitions.SpeakType.Neutral)
                : !SupportsCivilianPose(graphicsCharacter.Animator, type))
        {
            return false;
        }

        var gameCharacter = GameLocationCharacter.GetFromActor(monster);
        if (graphicsCharacter.Animator.GetFloat(AnimationDefinitions.CharacterSpeedId) > 0.01f ||
            monster.Side == RuleDefinitions.Side.Enemy ||
            gameCharacter?.Prone == true ||
            gameCharacter != null && (contenders.Contains(gameCharacter) ||
                                     characters.PartyCharacters.Contains(gameCharacter) ||
                                     characters.GuestCharacters.Contains(gameCharacter)))
        {
            return false;
        }

        if (graphicsCharacter.CharacterType == GraphicsCharacterDefinitions.CharacterType.Simple)
        {
            // The native spawn callback sets this after positioning and its initial animation.
            // SetupAfterAnimator can notify us while that asynchronous callback is still pending.
            return HasCurrentCivilianPose(graphicsCharacter) ||
                   graphicsCharacter.Animator.GetBool(AnimationDefinitions.IsInDialogId);
        }

        if (!graphicsCharacter.IsBoundToGameCharacter ||
            gameCharacter is not { } character ||
            character.IsSummonedCreature || character.MovingToDestination ||
            ServiceRepository.GetService<IGameLocationActionService>()?.IsCharacterActing(character) == true ||
            graphicsCharacter.Animator.GetBool(AnimationDefinitions.IsInDialogId) &&
            !HasCurrentCivilianPose(graphicsCharacter))
        {
            return false;
        }

        return monster.Side == RuleDefinitions.Side.Neutral ||
               monster.Side == RuleDefinitions.Side.Ally && character.BoundGadgetId != 0 && !character.HasComplexActions;
    }

    private static bool HasCivilianGameplayController(GraphicsCharacter graphicsCharacter)
    {
        var controller = graphicsCharacter.CurrentAnimatorController.Value;
        if (!controller)
        {
            return false;
        }

        if (CivilianGameplayControllers.TryGetValue(controller, out var compatible))
        {
            return compatible;
        }

        return controller.animationClips.Any(clip => clip &&
            clip.name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool SupportsCivilianGameplayController(Animator animator)
    {
        var controller = animator.runtimeAnimatorController;
        if (!CivilianGameplayControllers.TryGetValue(controller, out var compatible))
        {
            var parameters = animator.parameters;
            compatible = controller.animationClips.Any(clip => clip &&
                clip.name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0) &&
                parameters.Any(parameter => parameter.nameHash == AnimationDefinitions.IsInDialogId &&
                parameter.type == AnimatorControllerParameterType.Bool) &&
                new[] { AnimationDefinitions.IsInCombatId, AnimationDefinitions.CharacterSpeedId,
                    AnimationDefinitions.WeaponTypeId }.All(id => parameters.Any(parameter =>
                    parameter.nameHash == id && parameter.type == AnimatorControllerParameterType.Float));
            CivilianGameplayControllers.Add(controller, compatible);
        }

        return compatible;
    }

    private static bool SupportsCivilianPose(Animator animator,
        AnimationDefinitions.SpeakType type = AnimationDefinitions.SpeakType.Fear_Idle)
    {
        var controller = animator.runtimeAnimatorController;

        if (!controller || HandleBodyAnimation == null)
        {
            return false;
        }

        if (!CivilianControllers.TryGetValue(controller, out var supported))
        {
            var parameters = animator.parameters;
            var clips = controller.animationClips;
            var compatible = CivilianBoolParameters.All(id => parameters.Any(parameter =>
                            parameter.nameHash == id && parameter.type == AnimatorControllerParameterType.Bool)) &&
                        CivilianFloatParameters.All(id => parameters.Any(parameter =>
                            parameter.nameHash == id && parameter.type == AnimatorControllerParameterType.Float)) &&
                        parameters.Any(parameter => parameter.nameHash == AnimationDefinitions.CharacterSpeedId &&
                                                    parameter.type == AnimatorControllerParameterType.Float) &&
                        parameters.Any(parameter => parameter.nameHash == AnimationDefinitions.SpeakAnimationStartId &&
                                                    parameter.type == AnimatorControllerParameterType.Trigger);
            supported = compatible
                ? new HashSet<AnimationDefinitions.SpeakType>(CivilianSpeakTypes.Where(speakType =>
                    clips.Any(clip => clip &&
                        clip.name.IndexOf(speakType.ToString(), StringComparison.OrdinalIgnoreCase) >= 0)))
                : [];
            CivilianControllers.Add(controller, supported);
        }

        return supported.Contains(type);
    }

    private static void ClearCivilianPoses()
    {
        foreach (var graphicsCharacter in CivilianPoses.Keys.ToArray())
        {
            ReleaseCivilianPose(graphicsCharacter);
        }

        CivilianPoses.Clear();
        PendingCivilianPoses.Clear();
    }

    private static bool ReleaseCivilianPose(GraphicsCharacter graphicsCharacter)
    {
        if (!CivilianPoses.TryGetValue(graphicsCharacter, out var pose))
        {
            return false;
        }

        CivilianPoses.Remove(graphicsCharacter);
        pose.Restore(graphicsCharacter);

        return true;
    }

    private static void ReleaseUnusedCivilianPoses(HashSet<GraphicsCharacter> candidates)
    {
        foreach (var entry in CivilianPoses.ToArray())
        {
            if (!candidates.Contains(entry.Key) || !entry.Value.IsCurrent(entry.Key))
            {
                ReleaseCivilianPose(entry.Key);
            }
        }
    }

    private static bool HasCurrentCivilianPose(GraphicsCharacter graphicsCharacter)
    {
        return CivilianPoses.TryGetValue(graphicsCharacter, out var pose) && pose.IsCurrent(graphicsCharacter);
    }

    private static bool HasCurrentAmbientCivilianPose(GraphicsCharacter graphicsCharacter)
    {
        return CivilianPoses.TryGetValue(graphicsCharacter, out var pose) &&
               pose.IsAmbient && pose.IsCurrent(graphicsCharacter);
    }

    private sealed class CivilianPose
    {
        private readonly Animator _animator;
        private readonly RuntimeAnimatorController _controller;
        private RuntimeAnimatorController _appliedController;
        private readonly RulesetCharacter _character;
        private readonly bool[] _boolValues;
        private readonly float[] _floatValues;
        private readonly AnimatorStateInfo[] _states;
        private readonly float[] _layerWeights;
        private readonly bool _hasAbsoluteLookAtLayer;
        private readonly bool _keepAnimatorStateOnDisable;
        private AnimatorParameterValue[] _controllerParameters;
        private readonly Dictionary<GameObject, bool> _wieldedWeapons = [];
        private readonly Transform _parent;
        private readonly Vector3 _position;
        private readonly Quaternion _rotation;
        private readonly Vector3 _parentPosition;
        private readonly Quaternion _parentRotation;
        private Vector3 _seatedPosition;
        private Quaternion _seatedRotation;
        private bool _seatingHeightApplied;
        private bool _seatingHeightResolved;
        private bool _seatingIkAvailable;
        private readonly List<AnimatorClipInfo> _seatingClips = [];

        internal CivilianPose(GraphicsCharacter graphicsCharacter, AnimationDefinitions.SpeakType type, bool ambient)
        {
            _animator = graphicsCharacter.Animator;
            _controller = _animator.runtimeAnimatorController;
            _appliedController = _controller;
            _character = graphicsCharacter.RulesetCharacter;
            _parent = graphicsCharacter.transform.parent;
            _position = graphicsCharacter.transform.localPosition;
            _rotation = graphicsCharacter.transform.localRotation;
            _parentPosition = _parent ? _parent.position : Vector3.zero;
            _parentRotation = _parent ? _parent.rotation : Quaternion.identity;
            _boolValues = CivilianBoolParameters.Select(_animator.GetBool).ToArray();
            _floatValues = CivilianFloatParameters.Select(_animator.GetFloat).ToArray();
            _states = Enumerable.Range(0, _animator.layerCount).Select(_animator.GetCurrentAnimatorStateInfo).ToArray();
            _layerWeights = Enumerable.Range(0, _animator.layerCount).Select(_animator.GetLayerWeight).ToArray();
            _hasAbsoluteLookAtLayer = _animator.GetLayerIndex("LookAt_Head_Absolute") >= 0;
            Type = type;
            IsAmbient = ambient;
            _keepAnimatorStateOnDisable = _animator.keepAnimatorControllerStateOnDisable;
            if (ambient)
            {
                _animator.keepAnimatorControllerStateOnDisable = true;
            }
        }

        internal AnimationDefinitions.SpeakType Type { get; }
        internal bool IsAmbient { get; }
        internal CivilianSeating Seating { get; private set; }

        internal Vector3 GetAuthoredPosition(Transform character)
        {
            return Seating == null ? character.position : _parent ? _parent.TransformPoint(_position) : _position;
        }

        internal Vector3 GetAuthoredForward(Transform character)
        {
            return Seating == null ? character.forward : (_parent ? _parent.rotation : Quaternion.identity) *
                _rotation * Vector3.forward;
        }

        internal void SetSeating(GraphicsCharacter character, CivilianSeating seating)
        {
            Seating = seating;
            character.transform.SetPositionAndRotation(seating.Position, seating.Rotation);
            RecordSeatedTransform(character.transform);
        }

        private void RecordSeatedTransform(Transform character)
        {
            _seatedPosition = character.localPosition;
            _seatedRotation = character.localRotation;
        }

        private bool OwnsSeatedTransform(GraphicsCharacter character)
        {
            return character && character.RulesetCharacter == _character && character.transform.parent == _parent &&
                (character.transform.localPosition - _seatedPosition).sqrMagnitude < 0.000001f &&
                Quaternion.Angle(character.transform.localRotation, _seatedRotation) < 0.1f;
        }

        internal bool UpdateSeating(GraphicsCharacter character)
        {
            if (!IsCurrent(character) || !OwnsSeatedTransform(character) || !Seating.Seat ||
                !Seating.Seat.gameObject.activeInHierarchy ||
                (Seating.Seat.position - Seating.SeatPosition).sqrMagnitude > 0.000001f ||
                Quaternion.Angle(Seating.Seat.rotation, Seating.SeatRotation) > 0.1f ||
                _parent && ((_parent.position - _parentPosition).sqrMagnitude > 0.000001f ||
                            Quaternion.Angle(_parent.rotation, _parentRotation) > 0.1f))
            {
                return false;
            }

            if (_seatingHeightResolved || !_seatingIkAvailable || !_animator.isHuman || _animator.IsInTransition(0))
            {
                return true;
            }

            _animator.GetCurrentAnimatorClipInfo(0, _seatingClips);
            if (!_seatingClips.Any(clip => clip.weight > 0.95f && clip.clip &&
                    clip.clip.name.IndexOf(Type.ToString(), StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (!hips)
            {
                return true;
            }

            var target = Seating.Position;
            target.y = Seating.Height + 0.1f;
            var correction = target - hips.position;
            if (Math.Abs(correction.y) <= 0.3f && correction.x * correction.x + correction.z * correction.z <= 0.09f)
            {
                character.transform.position += correction;
                RecordSeatedTransform(character.transform);
                _seatingHeightApplied = true;
            }

            _seatingHeightResolved = true;
            return true;
        }

        internal void ApplySeatingFeet(GraphicsCharacter character)
        {
            if (Seating == null || !IsCurrent(character) || !OwnsSeatedTransform(character) || !_animator.isHuman)
            {
                return;
            }

            _seatingIkAvailable = true;
            if (!_seatingHeightApplied)
            {
                return;
            }

            ClampSeatedFoot(AvatarIKGoal.LeftFoot, HumanBodyBones.LeftFoot, _animator.leftFeetBottomHeight);
            ClampSeatedFoot(AvatarIKGoal.RightFoot, HumanBodyBones.RightFoot, _animator.rightFeetBottomHeight);
        }

        private void ClampSeatedFoot(AvatarIKGoal goal, HumanBodyBones bone, float clearance)
        {
            var foot = _animator.GetBoneTransform(bone);
            if (!foot)
            {
                return;
            }

            var position = foot.position;
            position.y = Mathf.Max(position.y, Seating.Floor + clearance);
            _animator.SetIKPosition(goal, position);
            _animator.SetIKPositionWeight(goal, 1f);
        }

        internal bool ApplyGameplayPosture(GraphicsCharacter graphicsCharacter, bool defensive)
        {
            var controller = graphicsCharacter.CurrentAnimatorController.Value;
            if (!controller)
            {
                throw new InvalidOperationException("Civilian gameplay animator is unavailable.");
            }

            if (controller != _controller)
            {
                _controllerParameters = CaptureParameters(_animator);
                var layerWeights = CaptureLayerWeights(_animator);
                _animator.runtimeAnimatorController = controller;
                _appliedController = controller;
                ApplyParameters(_animator, _controllerParameters);
                RefreshAnimatorBindings(graphicsCharacter, layerWeights);
            }

            if (!SupportsCivilianGameplayController(_animator))
            {
                return false;
            }

            _animator.ResetTrigger(AnimationDefinitions.SpeakAnimationStartId);
            _animator.SetBool(AnimationDefinitions.IsInDialogId, false);
            _animator.SetBool(AnimationDefinitions.DialogInterruptsId, false);
            _animator.SetBool(AnimationDefinitions.CutAnimationForDialogId, false);
            _animator.SetBool(AnimationDefinitions.DialogIsLoopingId, false);
            _animator.SetBool(AnimationDefinitions.IsKeepingPostureId, false);
            _animator.SetFloat(AnimationDefinitions.IsInCombatId, defensive ? 1 : 0);
            return true;
        }

        internal void SetWieldedWeaponsVisible(GraphicsCharacter graphicsCharacter, bool visible)
        {
            var items = graphicsCharacter.WieldedRulesetItems;
            var models = graphicsCharacter.WieldedInstantiatedItems;
            if (items == null || models == null)
            {
                return;
            }

            foreach (var item in items)
            {
                if (!IsCivilianWeapon(item.Value?.ItemDefinition) ||
                    !models.TryGetValue(item.Key, out var model) || !model || _wieldedWeapons.ContainsKey(model))
                {
                    continue;
                }

                // Preserve each original visibility; torches and musical instruments remain native.
                _wieldedWeapons.Add(model, model.activeSelf);
                model.SetActive(visible);
            }
        }

        internal bool IsCurrent(GraphicsCharacter graphicsCharacter)
        {
            return OwnsAnimator(graphicsCharacter) && _animator.runtimeAnimatorController == _appliedController;
        }

        private bool OwnsAnimator(GraphicsCharacter graphicsCharacter) =>
            graphicsCharacter && graphicsCharacter.Animator == _animator && _animator &&
            graphicsCharacter.RulesetCharacter == _character;

        internal void Restore(GraphicsCharacter graphicsCharacter)
        {
            if (IsAmbient && OwnsAnimator(graphicsCharacter) && _animator.keepAnimatorControllerStateOnDisable)
            {
                _animator.keepAnimatorControllerStateOnDisable = _keepAnimatorStateOnDisable;
            }

            if (Seating != null && graphicsCharacter && graphicsCharacter.RulesetCharacter == _character &&
                graphicsCharacter.transform.parent == _parent)
            {
                // Native movement or turning can replace either component independently.
                // Return only the component still owned by this presentation.
                if ((graphicsCharacter.transform.localPosition - _seatedPosition).sqrMagnitude < 0.000001f)
                {
                    graphicsCharacter.transform.localPosition = _position;
                }

                if (Quaternion.Angle(graphicsCharacter.transform.localRotation, _seatedRotation) < 0.1f)
                {
                    graphicsCharacter.transform.localRotation = _rotation;
                }
            }

            // A controller can change while the same actor still owns its weapon models.
            // Restore their visibility independently of the old controller's parameters.
            if (graphicsCharacter && graphicsCharacter.RulesetCharacter == _character &&
                graphicsCharacter.WieldedInstantiatedItems != null)
            {
                foreach (var weapon in _wieldedWeapons)
                {
                    if (weapon.Key && graphicsCharacter.WieldedInstantiatedItems.Values.Contains(weapon.Key))
                    {
                        weapon.Key.SetActive(weapon.Value);
                    }
                }
            }

            if (!IsCurrent(graphicsCharacter))
            {
                return;
            }

            if (_appliedController != _controller)
            {
                // Keep native speech/equipment updates made during the lease in common
                // parameters, while restoring values that only exist in the original graph.
                var currentParameters = CaptureParameters(_animator);
                var currentLayerWeights = CaptureLayerWeights(_animator);
                if (!_hasAbsoluteLookAtLayer)
                {
                    // A native blend reads its cached layer index on every step. Stop it
                    // before returning to a lightweight graph without that layer.
                    CivilianLookAtBlend(graphicsCharacter)?.Reset();
                    graphicsCharacter.SetEnableLookAt(false, false, false);
                }
                _animator.runtimeAnimatorController = _controller;
                ApplyParameters(_animator, _controllerParameters);
                ApplyParameters(_animator, currentParameters);
                for (var layer = 0; layer < _layerWeights.Length && layer < _animator.layerCount; layer++)
                {
                    _animator.SetLayerWeight(layer, _layerWeights[layer]);
                }
                RefreshAnimatorBindings(graphicsCharacter, currentLayerWeights);
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

            if (_animator.isActiveAndEnabled &&
                !_character.IsDeadOrDyingOrUnconscious &&
                GameLocationCharacter.GetFromActor(_character)?.Prone != true &&
                !graphicsCharacter.IsInCombat &&
                _animator.GetFloat(AnimationDefinitions.CharacterSpeedId) <= 0.01f)
            {
                for (var layer = 0; layer < _states.Length && layer < _animator.layerCount; layer++)
                {
                    var state = _states[layer];
                    if (_animator.HasState(layer, state.fullPathHash))
                    {
                        _animator.Play(state.fullPathHash, layer, state.normalizedTime % 1);
                    }
                }
            }
        }

        private void RefreshAnimatorBindings(GraphicsCharacter graphicsCharacter,
            Dictionary<string, float> layerWeights)
        {
            // Full and lightweight graphs use different layer indices. Native speech,
            // torch and hand updates must address the graph currently attached.
            InitializeLayers.Invoke(graphicsCharacter, null);
            CheckWieldedItems.Invoke(graphicsCharacter, null);
            for (var layer = 0; layer < _animator.layerCount; layer++)
            {
                if (layerWeights.TryGetValue(_animator.GetLayerName(layer), out var weight))
                {
                    _animator.SetLayerWeight(layer, weight);
                }
            }
            InitializeAnimatorBehaviours.Invoke(graphicsCharacter, null);
        }

        private static Dictionary<string, float> CaptureLayerWeights(Animator animator)
        {
            return Enumerable.Range(0, animator.layerCount)
                .ToDictionary(animator.GetLayerName, animator.GetLayerWeight);
        }

        private static AnimatorParameterValue[] CaptureParameters(Animator animator)
        {
            return animator.parameters
                .Where(parameter => parameter.type != AnimatorControllerParameterType.Trigger)
                .Select(parameter => new AnimatorParameterValue(animator, parameter)).ToArray();
        }

        private static void ApplyParameters(Animator animator, AnimatorParameterValue[] values)
        {
            var parameters = animator.parameters;
            foreach (var value in values)
            {
                if (parameters.Any(parameter => parameter.nameHash == value.Id && parameter.type == value.Type))
                {
                    value.Apply(animator);
                }
            }
        }
    }

    private sealed class CivilianSeatShape(Transform mesh, Vector3 center, float halfWidth, bool bench)
    {
        internal readonly Transform Mesh = mesh;
        internal readonly Vector3 Center = center;
        internal readonly float HalfWidth = halfWidth;
        internal readonly bool IsBench = bench;
    }

    private sealed class CivilianSeating(Transform seat, Vector3 position, Quaternion rotation, float height, float floor,
        bool bench, AnimationDefinitions.SpeakType type)
    {
        internal readonly Transform Seat = seat;
        internal readonly Vector3 SeatPosition = seat.position;
        internal readonly Quaternion SeatRotation = seat.rotation;
        internal readonly Vector3 Position = position;
        internal readonly Quaternion Rotation = rotation;
        internal readonly float Height = height;
        internal readonly float Floor = floor;
        internal readonly bool IsBench = bench;
        internal readonly AnimationDefinitions.SpeakType Type = type;
    }

    private readonly struct AnimatorParameterValue
    {
        internal readonly int Id;
        internal readonly AnimatorControllerParameterType Type;
        private readonly bool _bool;
        private readonly float _float;
        private readonly int _int;

        internal AnimatorParameterValue(Animator animator, AnimatorControllerParameter parameter)
        {
            Id = parameter.nameHash;
            Type = parameter.type;
            _bool = Type == AnimatorControllerParameterType.Bool && animator.GetBool(Id);
            _float = Type == AnimatorControllerParameterType.Float ? animator.GetFloat(Id) : 0;
            _int = Type == AnimatorControllerParameterType.Int ? animator.GetInteger(Id) : 0;
        }

        internal void Apply(Animator animator)
        {
            switch (Type)
            {
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(Id, _bool);
                    break;
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(Id, _float);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(Id, _int);
                    break;
            }
        }
    }

}
