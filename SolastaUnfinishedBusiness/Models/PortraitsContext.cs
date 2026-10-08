using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Models;

public static class PortraitsContext
{
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
        GraphicsCharacterPhotoManager.PhotoParameters parameters = default)
    {
        var manager = service as GraphicsCharacterPhotoManager;
        var completed = false;

        void Respond(Texture texture)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            response?.Invoke(texture);
        }

        if (TryWaitForPhoto(manager, character, activePortrait, Respond))
        {
            return;
        }

        void NativeResponse(Texture texture)
        {
            // Native cache hits can return an unfinished render target. A newly rendered photo's
            // callback also runs before its pending flag is cleared; finish both at the render boundary.
            if (manager != null && manager.pendingRequests.Contains(texture as RenderTexture))
            {
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

        if (!completed)
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

        return request;
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
            while (values.MoveNext())
            {
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
        request.Respond(null);
    }

    internal static void CancelAllPhotoRequests(GraphicsCharacterPhotoManager manager)
    {
        if (!PhotoServices.TryGetValue(manager, out var state))
        {
            return;
        }

        PhotoServices.Remove(manager);

        foreach (var request in state.Requests.Values)
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
        internal readonly List<Action<Texture>> Responses = [];
        internal readonly List<Action<Texture>> NativeResponses = [];
        internal bool NativeResponseAllowed;
        internal GraphicsCharacterPhotoManager Manager { get; } = manager;
        internal RulesetCharacter Character { get; } = character;
        internal RenderTexture Texture { get; } = texture;
        internal bool Active { get; } = active;

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
