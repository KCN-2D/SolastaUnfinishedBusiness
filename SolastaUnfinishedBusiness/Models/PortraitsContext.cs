using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Models;

public static class PortraitsContext
{
    private static readonly ConditionalWeakTable<CharacterViewport, Transform> ViewportFramingTargets = new();

    internal static void UnbindViewportForRebind(CharacterViewport viewport, bool unbindAll)
    {
        // Bind immediately reuses the same square target. Keep it within this viewport's
        // lifetime instead of returning and recreating a 2048-square GPU allocation.
        var texture = viewport.renderTexture;
        viewport.renderTexture = null;
        try
        {
            viewport.Unbind(unbindAll);
        }
        finally
        {
            viewport.renderTexture = texture;
        }
    }

    internal static void StabilizeViewportFraming(CharacterViewport viewport)
    {
        var character = viewport.GraphicsCharacter;
        var camera = viewport.virtualCamera;
        if (!character || !camera || viewport.mode == CharacterViewport.Mode.Bestiary ||
            character.CharacterType is not (GraphicsCharacterDefinitions.CharacterType.Inventory or
                GraphicsCharacterDefinitions.CharacterType.ActiveCharacter) || !camera.Follow ||
            camera.LookAt != camera.Follow)
        {
            ReleaseViewportFraming(viewport);
            return;
        }

        var source = camera.Follow;
        if (source.TryGetComponent<FollowIKTarget>(out var follow) && follow.FollowTransform)
        {
            // A slow-follow proxy may still be converging when a close-up starts.
            // Sample its actual bone before detaching the framing from animation.
            source = follow.FollowTransform;
        }

        if (!ViewportFramingTargets.TryGetValue(viewport, out var target) || !target)
        {
            ViewportFramingTargets.Remove(viewport);
            target = new GameObject("UB_CharacterViewportFraming").transform;
            ViewportFramingTargets.Add(viewport, target);
        }

        // Preserve each native close-up/body target when switching cameras, then keep
        // the framing attached to the actor root rather than its breathing/head bones.
        target.SetParent(character.transform, false);
        // Cinemachine's authored face offset uses the target's axes as well as its
        // position. Replacing those axes with the actor root shifts the close-up.
        target.SetPositionAndRotation(source.position, source.rotation);

        if (viewport.mode == CharacterViewport.Mode.CharacterCustomization &&
            viewport.target == CharacterViewport.Target.HeadSlowLerp &&
            camera.GetCinemachineComponent<Cinemachine.CinemachineTransposer>() is { } transposer)
        {
            // The bone is below the visible center of the face. Offset by a fraction
            // of the camera's view height so the adjustment scales with its lens.
            var halfViewHeight = Mathf.Abs(transposer.m_FollowOffset.z) *
                                 Mathf.Tan(camera.m_Lens.FieldOfView * 0.5f * Mathf.Deg2Rad);
            target.position += character.transform.up * (0.2f * halfViewHeight);
        }

        camera.Follow = target;
        camera.LookAt = target;
    }

    internal static void ReleaseFailedViewportBinding(CharacterViewport viewport)
    {
        try
        {
            viewport.Unbind(true);
        }
        finally
        {
            // Unbind can itself fail while services are shutting down. A retained target
            // still belongs to this viewport and must be returned in that path too.
            var texture = viewport.renderTexture;
            viewport.renderTexture = null;
            if (viewport.rawImage)
            {
                viewport.rawImage.texture = null;
            }

            if (texture)
            {
                texture.DiscardContents();
                texture.Release();
                RenderTexture.ReleaseTemporary(texture);
            }
        }
    }

    internal static void ReleaseViewportFraming(CharacterViewport viewport)
    {
        if (!ViewportFramingTargets.TryGetValue(viewport, out var target))
        {
            return;
        }

        ViewportFramingTargets.Remove(viewport);
        if (!target)
        {
            return;
        }

        var camera = viewport.virtualCamera;
        if (camera)
        {
            if (camera.Follow == target)
            {
                camera.Follow = null;
            }

            if (camera.LookAt == target)
            {
                camera.LookAt = null;
            }
        }

        UnityEngine.Object.Destroy(target.gameObject);
    }

    private const float SeamAngleThresholdDegrees = 5;
    private const float StablePoseToleranceDegrees = 0.1f;
    private const float SeamSampleFrames = 3;
    private const float MaximumFrameGapSeconds = 0.25f;
    private const float LayerWeightTolerance = 0.0001f;
    private const float SingleClipWeightTolerance = 0.999f;

    private static readonly ConditionalWeakTable<GraphicsCharacter, InventoryHandPose> InventoryHandPoses = new();

    internal static void StabilizeInventoryHands(
        GraphicsCharacter character,
        Transform rightIk,
        Transform leftIk,
        int rightClosedLayer,
        int leftClosedLayer)
    {
        // Inventory uses the portrait controller. Its looping idle can briefly emit a different
        // finger pose at the clip boundary before returning to the same relaxed pose.
        if (character.CharacterType != GraphicsCharacterDefinitions.CharacterType.Inventory)
        {
            return;
        }

        var animator = character.Animator;

        if (!character.gameObject.activeInHierarchy || !animator || !animator.enabled ||
            !animator.isHuman || !animator.avatar || !animator.avatar.isValid ||
            !animator.runtimeAnimatorController || animator.layerCount == 0 || animator.speed <= 0)
        {
            InventoryHandPoses.Remove(character);
            return;
        }

        var items = character.WieldedRulesetItems;

        if (items == null)
        {
            InventoryHandPoses.Remove(character);
            return;
        }

        items.TryGetValue(EquipmentDefinitions.SlotTypeMainHand, out var mainHand);
        items.TryGetValue(EquipmentDefinitions.SlotTypeOffHand, out var offHand);
        var twoHanded = UsesBothHands(mainHand) || UsesBothHands(offHand);
        var rightFree = mainHand == null && !twoHanded && !rightIk &&
                        !HasWeightedLayer(animator, rightClosedLayer) &&
                        !HasWeightedLayer(animator, character.TorchRightLayerIndex);
        var leftFree = offHand == null && !twoHanded && !leftIk &&
                       !HasWeightedLayer(animator, leftClosedLayer) &&
                       !HasWeightedLayer(animator, character.TorchLeftLayerIndex);

        if (!rightFree && !leftFree)
        {
            InventoryHandPoses.Remove(character);
            return;
        }

        InventoryHandPoses.GetValue(character, _ => new InventoryHandPose())
            .Update(character, animator, rightFree, leftFree);
    }

    private static bool HasWeightedLayer(Animator animator, int index)
    {
        return index >= 0 && index < animator.layerCount && animator.GetLayerWeight(index) > LayerWeightTolerance;
    }

    private static bool UsesBothHands(RulesetItem item)
    {
        return item?.ItemDefinition is { IsWeapon: true } definition &&
               definition.WeaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagTwoHanded);
    }

    private sealed class InventoryHandPose
    {
        private readonly List<AnimatorClipInfo> _clips = new(1);
        private readonly InventoryFingerPose _right = new(
        [
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal
        ]);
        private readonly InventoryFingerPose _left = new(
        [
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal
        ]);
        private Animator _animator;
        private Avatar _avatar;
        private RuntimeAnimatorController _controller;
        private RulesetCharacter _character;
        private AnimationClip _clip;
        private int _state;
        private int _frame = -1;
        private float _normalized;
        private float _time;
        private float _length;
        private float _speed;
        private float _multiplier;
        private float _animatorSpeed;
        private bool _initialized;

        internal void Update(GraphicsCharacter character, Animator animator, bool rightFree, bool leftFree)
        {
            if (_frame == Time.frameCount)
            {
                return;
            }

            var state = animator.GetCurrentAnimatorStateInfo(0);
            animator.GetCurrentAnimatorClipInfo(0, _clips);
            var valid = state.loop && state.length > 0 && state.speed > 0 && state.speedMultiplier > 0 &&
                        !float.IsNaN(state.normalizedTime) && !float.IsInfinity(state.normalizedTime) &&
                        !animator.IsInTransition(0) && _clips.Count == 1 &&
                        _clips[0].weight >= SingleClipWeightTolerance &&
                        _clips[0].clip && _clips[0].clip.isLooping && _clips[0].clip.frameRate > 0;
            var sameRig = _animator == animator && _avatar == animator.avatar &&
                          _controller == animator.runtimeAnimatorController &&
                          ReferenceEquals(_character, character.RulesetCharacter);

            if (!sameRig || (rightFree && _right.HasDestroyedBone) || (leftFree && _left.HasDestroyedBone))
            {
                _animator = animator;
                _avatar = animator.avatar;
                _controller = animator.runtimeAnimatorController;
                _character = character.RulesetCharacter;
                _right.Bind(animator);
                _left.Bind(animator);
                _initialized = false;
            }

            var clip = valid ? _clips[0].clip : null;
            var delta = state.normalizedTime - _normalized;
            var continuous = _initialized && sameRig && valid && _clip == clip &&
                             _state == state.fullPathHash && _length == state.length &&
                             _speed == state.speed && _multiplier == state.speedMultiplier &&
                             _animatorSpeed == animator.speed && _frame + 1 == Time.frameCount &&
                             Time.unscaledTime - _time <= MaximumFrameGapSeconds && delta > 0 &&
                             delta * state.length <= MaximumFrameGapSeconds;
            var loopBoundary = continuous && Mathf.Floor(state.normalizedTime) > Mathf.Floor(_normalized);
            var clipDelta = continuous ? delta * clip.length : 0;
            var window = valid ? SeamSampleFrames / clip.frameRate : 0;

            _right.Update(rightFree && valid, continuous, loopBoundary, clipDelta, window);
            _left.Update(leftFree && valid, continuous, loopBoundary, clipDelta, window);
            _initialized = valid;
            _clip = clip;
            _state = state.fullPathHash;
            _normalized = state.normalizedTime;
            _length = state.length;
            _speed = state.speed;
            _multiplier = state.speedMultiplier;
            _animatorSpeed = animator.speed;
            _frame = Time.frameCount;
            _time = Time.unscaledTime;
        }
    }

    private sealed class InventoryFingerPose(HumanBodyBones[] boneTypes)
    {
        private readonly Transform[] _bones = new Transform[boneTypes.Length];
        private readonly Quaternion[] _raw = new Quaternion[boneTypes.Length];
        private readonly Quaternion[] _stable = new Quaternion[boneTypes.Length];
        private readonly bool[] _affected = new bool[boneTypes.Length];
        private bool _initialized;
        private float _elapsed;

        internal bool HasDestroyedBone
        {
            get
            {
                for (var i = 0; i < _bones.Length; i++)
                {
                    if (!ReferenceEquals(_bones[i], null) && !_bones[i])
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        internal void Bind(Animator animator)
        {
            for (var i = 0; i < _bones.Length; i++)
            {
                _bones[i] = animator.GetBoneTransform(boneTypes[i]);
            }

            _initialized = false;
        }

        internal void Update(bool free, bool continuous, bool loopBoundary, float delta, float window)
        {
            if (!free)
            {
                if (_initialized)
                {
                    Array.Clear(_affected, 0, _affected.Length);
                }

                _initialized = false;
                _elapsed = 0;
                return;
            }

            if (!continuous || !_initialized)
            {
                for (var i = 0; i < _bones.Length; i++)
                {
                    _affected[i] = false;

                    if (_bones[i])
                    {
                        _raw[i] = _bones[i].localRotation;
                    }
                }

                _initialized = true;
                _elapsed = 0;
                return;
            }

            _elapsed += delta;

            if (loopBoundary)
            {
                _elapsed = 0;
            }

            for (var i = 0; i < _bones.Length; i++)
            {
                var bone = _bones[i];

                if (!bone)
                {
                    _affected[i] = false;
                    continue;
                }

                var raw = bone.localRotation;

                if (loopBoundary && Quaternion.Angle(_raw[i], raw) > SeamAngleThresholdDegrees)
                {
                    _stable[i] = _raw[i];
                    _affected[i] = true;
                }

                // Keep the native sample separate from the displayed correction. A later seam
                // must be detected from animation output, not from our previous corrected pose.
                _raw[i] = raw;

                if (!_affected[i])
                {
                    continue;
                }

                // Seam samples can briefly cross the stable pose before settling.
                if ((_elapsed >= window &&
                     Quaternion.Angle(_stable[i], raw) <= StablePoseToleranceDegrees) || _elapsed >= window * 2)
                {
                    _affected[i] = false;
                    continue;
                }

                // Defective opening samples normally return within two clip frames. If another
                // clip keeps moving, release smoothly to its live pose rather than snap at timeout.
                var blend = Mathf.Clamp01((_elapsed - window) / window);
                bone.localRotation = Quaternion.Slerp(_stable[i], raw, blend);
            }
        }
    }

    private static readonly ConditionalWeakTable<RawImage, PortraitRequest> Requests = new();

    internal static void BeginBinding(GuiCharacter character, RawImage image)
    {
        if (!image)
        {
            return;
        }

        Requests.Remove(image);
        Requests.Add(image, new PortraitRequest(character));
    }

    internal static void Release(RawImage image)
    {
        if (image)
        {
            Requests.Remove(image);
        }
    }

    internal static void RequestCharacterPortrait(
        IGraphicsCharacterPhotoService service,
        RulesetCharacter character,
        Action<Texture> response,
        bool useOnlyExisting,
        int width,
        int height,
        GraphicsCharacterPhotoManager.PhotoParameters parameters,
        RawImage image)
    {
        RequestCompletedPhoto(
            service,
            character,
            false,
            GuardResponse(image, response),
            useOnlyExisting,
            width,
            height,
            parameters);
    }

    internal static bool TryAssignModelPortrait(
        GuiCharacter character,
        RawImage image,
        RectTransform group,
        RectTransform back,
        Action response)
    {
        var monster = character?.RulesetCharacterMonster;
        if (!image || monster?.MonsterDefinition.GetFirstSubFeatureOfType<CustomModels.MonsterModel>() == null ||
            ServiceRepository.GetService<IGraphicsCharacterPhotoService>() is not { } service)
        {
            return false;
        }

        // Native monster plates use a definition sprite instead of photographing the live model.
        // Keep the native photo cache and binding guard used by the other character portraits.
        image.texture = null;
        image.gameObject.SetActive(false);

        if (group)
        {
            group.gameObject.SetActive(false);
        }

        if (back)
        {
            back.gameObject.SetActive(true);
        }

        RequestCharacterPortrait(service, monster, texture =>
        {
            image.texture = texture;
            CompletePortraitBinding(image, group, back, null, response);
        }, false, 256, 384, default, image);
        return true;
    }

    internal static void CompletePortraitBinding(
        RawImage image,
        RectTransform group,
        RectTransform back,
        Image alternateImage,
        Action response)
    {
        if (back)
        {
            back.gameObject.SetActive(false);
        }

        if (group)
        {
            group.gameObject.SetActive(true);
        }

        if (image)
        {
            image.gameObject.SetActive(true);
        }

        if (alternateImage)
        {
            alternateImage.gameObject.SetActive(false);
        }

        response?.Invoke();
    }

    internal static void RequestSnapshotPortrait(
        IGraphicsCharacterPhotoService service,
        RulesetCharacterHero.Snapshot snapshot,
        Action<Texture> response,
        Action error,
        RawImage image)
    {
        if (!image)
        {
            service.RequestCharacterPhoto(snapshot, response, error);
            return;
        }

        Requests.TryGetValue(image, out var request);
        service.RequestCharacterPhoto(snapshot, GuardResponse(image, response), () =>
        {
            if (IsCurrentRequest(image, request))
            {
                error?.Invoke();
            }
        });
    }

    internal static void RequestActivePortrait(
        IGraphicsCharacterPhotoService service,
        RulesetCharacter character,
        Action<Texture> response,
        RawImage image)
    {
        RequestCompletedPhoto(service, character, true, GuardResponse(image, response));
    }

    private static Action<Texture> GuardResponse(
        RawImage image,
        Action<Texture> response)
    {
        if (!image)
        {
            return response;
        }

        Requests.TryGetValue(image, out var request);

        return texture =>
        {
            // A pooled image can be rebound even to the same character before this callback runs.
            // Ownership belongs to this binding, not to the character name or the texture identity.
            if (!IsCurrentRequest(image, request))
            {
                return;
            }

            response?.Invoke(texture);

            // Native response handlers can trigger another Bind/Unbind themselves.
            if (IsCurrentRequest(image, request))
            {
                ChangePortrait(request.Character, image);
            }
        };
    }

    private static bool IsCurrentRequest(RawImage image, PortraitRequest request)
    {
        return image && request != null && Requests.TryGetValue(image, out var current) &&
               ReferenceEquals(current, request);
    }

    private static readonly ConditionalWeakTable<GraphicsCharacterPhotoManager, PhotoServiceState> PhotoServices = new();
    private static readonly ConditionalWeakTable<IEnumerator, PhotoRenderRequest> RenderRequests = new();

    internal static void RequestCompletedPhoto(
        IGraphicsCharacterPhotoService service,
        RulesetCharacter character,
        bool activePortrait,
        Action<Texture> response,
        bool useOnlyExisting = false,
        int width = 256,
        int height = 384,
        GraphicsCharacterPhotoManager.PhotoParameters parameters = default,
        bool forceRefresh = false)
    {
        var manager = service as GraphicsCharacterPhotoManager;
        var completed = false;
        var refreshStarted = false;
        var refreshCache = !useOnlyExisting && (forceRefresh ||
            (!activePortrait && character is RulesetCharacterHero && manager != null &&
             !manager.photoByRulesetCharacter.ContainsKey(character)));

        void Respond(Texture texture)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            response?.Invoke(texture);
        }

        void Refresh(Texture texture)
        {
            if (!texture)
            {
                Respond(null);
                return;
            }

            refreshCache = false;
            refreshStarted = true;
            if (activePortrait)
            {
                service.RefreshActiveCharacterPhoto(character, NativeResponse);
            }
            else
            {
                service.RefreshCharacterPhoto(character, parameters, NativeResponse);
            }

            if (!completed)
            {
                TryWaitForPhoto(manager, character, activePortrait, Respond);
            }
        }

        // A render already in progress may have captured data before an explicit replacement.
        var pendingResponse = forceRefresh && !useOnlyExisting ? (Action<Texture>)Refresh : Respond;
        if (TryWaitForPhoto(manager, character, activePortrait, pendingResponse))
        {
            return;
        }

        void NativeResponse(Texture texture)
        {
            // Native cache hits can return an unfinished render target. A newly rendered photo's
            // callback also runs before its pending flag is cleared; finish both at the render boundary.
            if (completed || (manager != null && manager.pendingRequests.Contains(texture as RenderTexture)))
            {
                return;
            }

            // Snapshot fallback matches names and ancestry, not the live hero's appearance.
            if (refreshCache)
            {
                Refresh(texture);
                return;
            }

            Respond(texture);
        }

        if (activePortrait)
        {
            service.RequestActiveCharacterPhoto(character, NativeResponse);
        }
        else
        {
            service.RequestCharacterPhoto(character, NativeResponse, useOnlyExisting, width, height, parameters);
        }

        if (!completed && !refreshStarted)
        {
            TryWaitForPhoto(manager, character, activePortrait, Respond);
        }
    }

    private static bool TryWaitForPhoto(
        GraphicsCharacterPhotoManager manager,
        RulesetCharacter character,
        bool activePortrait,
        Action<Texture> response)
    {
        if (manager == null || !PhotoServices.TryGetValue(manager, out var state) ||
            !state.Requests.TryGetValue((character, activePortrait), out var request))
        {
            return false;
        }

        request.Responses.Add(response);
        return true;
    }

    internal static PhotoRenderRequest BeginPhotoRequest(
        GraphicsCharacterPhotoManager manager,
        RulesetCharacter character,
        RenderTexture texture,
        bool activePortrait,
        ref Action<Texture> response)
    {
        var state = PhotoServices.GetValue(manager, _ => new PhotoServiceState());
        var key = (character, activePortrait);
        var request = new PhotoRenderRequest(manager, character, texture, activePortrait);

        if (state.Requests.TryGetValue(key, out var previous))
        {
            // A refresh replaces this character's render, but its bound UI still awaits a photo.
            request.Responses.AddRange(previous.Responses);
            request.NativeResponses.AddRange(previous.NativeResponses);
            previous.Responses.Clear();
            previous.NativeResponses.Clear();
        }

        if (response != null)
        {
            request.NativeResponses.Add(response);
        }

        // Keep native callers (including character snapshot saving) attached across refreshes.
        // The native active-photo guard still decides whether its original callbacks may run.
        response = _ => request.NativeResponseAllowed = true;
        state.Requests[key] = request;
        // The native factory can advance before this iterator's next coroutine tick.
        previous?.TryAbortObsoleteFactoryRequest();

        return request;
    }

    internal static int CreatePhotoGraphicsCharacterRequest(
        IGraphicsCharacterFactoryService factory,
        GraphicsCharacterDefinitions.CharacterType characterType,
        RulesetCharacter character,
        int layerIndex,
        Action<GraphicsCharacter> created,
        bool forceDestroyClothes,
        bool dontLoadWieldedItems,
        IEnumerator values)
    {
        var id = factory.CreateGraphicsCharacterRequest(characterType, character, layerIndex, created,
            forceDestroyClothes, dontLoadWieldedItems);
        if (RenderRequests.TryGetValue(values, out var request)) request.AttachFactory(factory, id);
        return id;
    }

    internal static IEnumerator ObservePhotoRequest(IEnumerator values, PhotoRenderRequest request)
    {
        RenderRequests.Add(values, request);
        return CompletePhotoRequest(values, request);
    }

    internal static bool IsPendingPhotoRequest(
        HashSet<RenderTexture> pending,
        RenderTexture texture,
        IEnumerator values)
    {
        return pending.Contains(texture) &&
               (!RenderRequests.TryGetValue(values, out var request) || request.IsCurrent());
    }

    private static IEnumerator CompletePhotoRequest(IEnumerator values, PhotoRenderRequest request)
    {
        var completed = false;

        try
        {
            // A newer request owns the transferred callbacks before native work begins.
            // Started requests retain the native abort and resource cleanup path.
            if (!request.IsCurrent()) yield break;
            while (true)
            {
                if (!request.IsCurrent() && request.TryAbortObsoleteFactoryRequest()) yield break;
                if (!values.MoveNext()) break;
                yield return values.Current;
            }

            completed = true;
        }
        finally
        {
            RenderRequests.Remove(values);
            try
            {
                (values as IDisposable)?.Dispose();
            }
            finally
            {
                if (request.IsCurrent())
                {
                    PhotoServices.GetValue(request.Manager, _ => new PhotoServiceState()).Requests
                        .Remove((request.Character, request.Active));
                    var photos = request.Active
                        ? request.Manager.activeCharacterPhotoByRulesetCharacter
                        : request.Manager.photoByRulesetCharacter;
                    var ownsPhoto = photos.TryGetValue(request.Character, out var photo) &&
                                    ReferenceEquals(photo, request.Texture);
                    var texture = completed && ownsPhoto && !request.Manager.pendingRequests.Contains(photo)
                        ? photo
                        : null;

                    // Native iterator disposal does not release an unfinished cached target.
                    // Release only this request's photo, before cancellation can request a replacement.
                    if (texture == null && ownsPhoto && request.Manager.pendingRequests.Contains(photo))
                    {
                        if (request.Active)
                        {
                            request.Manager.ReleaseActiveCharacterPhoto(request.Character);
                        }
                        else
                        {
                            request.Manager.ReleaseCharacterPhoto(request.Character);
                        }
                    }

                    request.Respond(texture);
                }
            }
        }
    }

    internal sealed class PhotoReleaseState(PhotoRenderRequest[] requests, RenderTexture[] activeTargets)
    {
        internal readonly PhotoRenderRequest[] Requests = requests;
        internal readonly RenderTexture[] ActiveTargets = activeTargets;
    }

    internal static PhotoReleaseState CaptureActivePhotoRelease(
        GraphicsCharacterPhotoManager manager, RulesetCharacter character)
    {
        var requests = Array.Empty<PhotoRenderRequest>();
        if (PhotoServices.TryGetValue(manager, out var state) &&
            state.Requests.TryGetValue((character, true), out var request))
        {
            requests = [request];
        }

        return new PhotoReleaseState(requests,
            manager.activeCharacterPhotoByRulesetCharacter.TryGetValue(character, out var target)
                ? [target]
                : Array.Empty<RenderTexture>());
    }

    internal static PhotoReleaseState CaptureAllPhotoRelease(GraphicsCharacterPhotoManager manager)
    {
        return new PhotoReleaseState(
            PhotoServices.TryGetValue(manager, out var state)
                ? state.Requests.Values.ToArray()
                : Array.Empty<PhotoRenderRequest>(),
            manager.activeCharacterPhotoByRulesetCharacter.Values.ToArray());
    }

    internal static void CompletePhotoRelease(GraphicsCharacterPhotoManager manager, PhotoReleaseState released)
    {
        if (ReferenceEquals(manager, null) || released == null) return;
        PhotoServices.TryGetValue(manager, out var state);

        // Native release removes the cache and regular pending marker, but leaves the
        // active marker. A pooled target can already belong to another current request.
        foreach (var target in released.ActiveTargets)
        {
            if (ReferenceEquals(target, null) || manager.pendingRequests.Contains(target) ||
                HasCachedPhotoTarget(manager, target)) continue;
            var currentOwner = false;
            if (state != null)
            {
                foreach (var request in state.Requests.Values)
                {
                    if (ReferenceEquals(request.Texture, target) &&
                        Array.IndexOf(released.Requests, request) < 0)
                    {
                        currentOwner = true;
                        break;
                    }
                }
            }

            if (!currentOwner) manager.pendingActivePhotoRequests.Remove(target);
        }

        if (state == null) return;
        var cancelled = new List<PhotoRenderRequest>(released.Requests.Length);
        foreach (var request in released.Requests)
        {
            var key = (request.Character, request.Active);
            var photos = request.Active
                ? manager.activeCharacterPhotoByRulesetCharacter
                : manager.photoByRulesetCharacter;
            if (!state.Requests.TryGetValue(key, out var current) || !ReferenceEquals(current, request) ||
                photos.TryGetValue(request.Character, out var target) && ReferenceEquals(target, request.Texture))
            {
                continue;
            }

            state.Requests.Remove(key);
            cancelled.Add(request);
        }

        // Detach the whole released batch before callbacks can reenter either photo mode.
        if (state.Requests.Count == 0) PhotoServices.Remove(manager);
        try
        {
            foreach (var request in cancelled) request.TryAbortObsoleteFactoryRequest();
        }
        finally
        {
            foreach (var request in cancelled) request.Respond(null);
        }
    }

    private static bool HasCachedPhotoTarget(GraphicsCharacterPhotoManager manager, RenderTexture target)
    {
        foreach (var cached in manager.photoByRulesetCharacter.Values)
            if (ReferenceEquals(cached, target)) return true;
        foreach (var cached in manager.activeCharacterPhotoByRulesetCharacter.Values)
            if (ReferenceEquals(cached, target)) return true;
        return false;
    }

    internal static void CancelPhotoRequest(
        GraphicsCharacterPhotoManager manager,
        RulesetCharacter character,
        bool activePortrait)
    {
        if (!PhotoServices.TryGetValue(manager, out var state) ||
            !state.Requests.TryGetValue((character, activePortrait), out var request))
        {
            return;
        }

        state.Requests.Remove((character, activePortrait));
        try
        {
            request.TryAbortObsoleteFactoryRequest();
        }
        finally
        {
            request.Respond(null);
        }
    }

    private sealed class PhotoServiceState
    {
        internal readonly Dictionary<(RulesetCharacter, bool), PhotoRenderRequest> Requests = new();
    }

    internal sealed class PhotoRenderRequest(
        GraphicsCharacterPhotoManager manager,
        RulesetCharacter character,
        RenderTexture texture,
        bool active)
    {
        private GraphicsCharacterFactoryManager _factory;
        private int _factoryRequest = -1;
        private bool _factoryStopped;
        internal readonly List<Action<Texture>> Responses = [];
        internal readonly List<Action<Texture>> NativeResponses = [];
        internal bool NativeResponseAllowed;
        internal GraphicsCharacterPhotoManager Manager { get; } = manager;
        internal RulesetCharacter Character { get; } = character;
        internal RenderTexture Texture { get; } = texture;
        internal bool Active { get; } = active;

        internal void AttachFactory(IGraphicsCharacterFactoryService factory, int requestId)
        {
            _factory = factory as GraphicsCharacterFactoryManager;
            _factoryRequest = requestId;
            _factoryStopped = false;
        }

        internal bool TryAbortObsoleteFactoryRequest()
        {
            if (IsCurrent()) return false;
            if (_factoryStopped) return true;
            if (ReferenceEquals(_factory, null) || _factoryRequest < 0) return false;
            var id = _factoryRequest;
            // Only this iterator's captured native factory request is eligible. An existing
            // abort owns its callbacks already; native AbortOrDestroy is not idempotent.
            if (!_factory || _factory.pendingAbortRequests.Contains(id) ||
                _factory.pendingDestroyedCallbacks.ContainsKey(id) ||
                !_factory.pendingRequests.Contains(id) && !_factory.characterPerRequestId.ContainsKey(id))
            {
                _factoryStopped = true;
                _factoryRequest = -1;
                return true;
            }
            if (_factory.characterPerRequestId.TryGetValue(id, out var graphics) &&
                (!graphics || !ReferenceEquals(graphics.RulesetCharacter, Character)))
            {
                _factoryStopped = true;
                _factoryRequest = -1;
                return true;
            }
            // Remember the terminal result after clearing the ID so a later iterator
            // tick exits instead of waiting for the aborted native creation callback.
            _factoryStopped = true;
            _factoryRequest = -1;
            _factory.AbortOrDestroyGraphicsCharacter(id, null, null);
            return true;
        }

        internal bool IsCurrent()
        {
            return PhotoServices.TryGetValue(Manager, out var state) &&
                   state.Requests.TryGetValue((Character, Active), out var current) &&
                   ReferenceEquals(current, this);
        }

        internal void Respond(Texture photo)
        {
            // A response can rebind a surface or request another photo. Complete this batch
            // before invoking callers, and do not strand the other surfaces if one fails.
            var responses = Responses.ToArray();
            var nativeResponses = NativeResponses.ToArray();
            Responses.Clear();
            NativeResponses.Clear();

            // Our subscribers need the completed cache even when another active portrait
            // suppressed its native callback. Take their snapshots before native callers
            // can release the cached target while updating a character snapshot or binding.
            Notify(responses, photo);

            if (NativeResponseAllowed && photo)
            {
                Notify(nativeResponses, photo);
            }
        }

        private static void Notify(Action<Texture>[] responses, Texture photo)
        {
            foreach (var response in responses)
            {
                try
                {
                    response(photo);
                }
                catch (Exception exception)
                {
                    Main.Error(exception);
                }
            }
        }
    }

    private sealed class PortraitRequest(GuiCharacter character)
    {
        internal GuiCharacter Character { get; } = character;
    }

    #region Custom Portraits Helpers

    private static readonly Dictionary<string, Texture2D> CustomHeroPortraits = new();
    private static readonly Dictionary<string, Texture2D> CustomMonsterPortraits = new();

    internal static readonly string PortraitsFolder = $"{Main.ModFolder}/Portraits";
    private static readonly string PreGenFolder = $"{PortraitsFolder}/PreGen";
    private static readonly string PersonalFolder = $"{PortraitsFolder}/Personal";
    private static readonly string MonstersFolder = $"{PortraitsFolder}/Monsters";

    internal static void EnsureFolderExists()
    {
        Main.EnsureFolderExists(PortraitsFolder);
        Main.EnsureFolderExists(PreGenFolder);
        Main.EnsureFolderExists(PersonalFolder);
        Main.EnsureFolderExists(MonstersFolder);
    }

    internal static bool HasCustomPortrait(RulesetCharacter rulesetCharacter)
    {
        return (rulesetCharacter is RulesetCharacterHero &&
                CustomHeroPortraits.ContainsKey(rulesetCharacter.Name)) ||
               (rulesetCharacter is RulesetCharacterMonster rulesetCharacterMonster &&
                CustomMonsterPortraits.ContainsKey(rulesetCharacterMonster.MonsterDefinition.Name));
    }

    internal static void ChangePortrait(GuiCharacter __instance, RawImage rawImage)
    {
        if (!Main.Settings.EnableCustomPortraits || ToolsContext.FunctorRespec.IsRespecing)
        {
            return;
        }

        if (__instance.RulesetCharacterMonster != null)
        {
            if (TryGetMonsterPortrait(__instance.RulesetCharacterMonster.MonsterDefinition.Name, rawImage,
                    out var texture))
            {
                rawImage.texture = texture;
            }
        }
        else if (__instance.BuiltIn)
        {
            if (TryGetPreGenHeroPortrait(__instance.Name, rawImage, out var texture))
            {
                rawImage.texture = texture;
            }
        }
        else
        {
            if (TryGetHeroPortrait(__instance.Name, rawImage, out var texture))
            {
                rawImage.texture = texture;
            }
        }
    }

    private static bool TryGetHeroPortrait(string name, RawImage original, out Texture2D texture)
    {
        var filename = $"{PersonalFolder}/{name}.png";

        return TryGetPortrait(CustomHeroPortraits, name, filename, original, out texture);
    }

    private static bool TryGetPreGenHeroPortrait(string name, RawImage original, out Texture2D texture)
    {
        var filename = $"{PreGenFolder}/{name}.png";

        return TryGetPortrait(CustomHeroPortraits, name, filename, original, out texture);
    }

    private static bool TryGetMonsterPortrait(string name, RawImage original, out Texture2D texture)
    {
        var filename = $"{MonstersFolder}/{name}.png";

        return TryGetPortrait(CustomMonsterPortraits, name, filename, original, out texture);
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static bool TryGetPortrait(
        Dictionary<string, Texture2D> dict, string name, string filename, RawImage original, out Texture2D texture)
    {
        if (original is not { texture: not null })
        {
            texture = null;
            return false;
        }

        if (dict.TryGetValue(name, out texture))
        {
            return true;
        }

        if (!File.Exists(filename))
        {
            return false;
        }

        var fileData = File.ReadAllBytes(filename);

        texture = new Texture2D(original.texture.width, original.texture.height, TextureFormat.ARGB32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
        };
        texture.LoadImage(fileData);
        dict.Add(name, texture);

        return true;
    }

    #endregion
}
