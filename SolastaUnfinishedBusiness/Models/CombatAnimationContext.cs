using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace SolastaUnfinishedBusiness.Models;

internal static class CombatAnimationContext
{
    internal const float MinSpeedMultiplier = 1.0f;
    internal const float DefaultSpeedMultiplier = 1.08f;
    internal const float MaxSpeedMultiplier = 1.50f;

    private const float MinEffectiveSpeedMultiplier = MinSpeedMultiplier + 0.0001f;

    private static readonly Dictionary<Animator, AnimatorState> AnimatorStates = [];
    private static readonly Dictionary<Animator, MovementReactionState> MovementReactionStates = [];
    private static readonly Dictionary<GraphicsCharacter, EquipmentAttackState> EquipmentAttackStates = [];
    private static readonly HashSet<EquipmentPresentation> EquipmentPresentations = [];

    internal sealed class WeaponAnimation(string modelName, string attackBinding, string attackClipPrefix,
        string idleBinding, string nativeIdleClip, string slot, bool requiresFreeOtherHand)
    {
        internal string ModelName { get; } = modelName;
        internal string AttackBinding { get; } = attackBinding;
        internal string AttackClipPrefix { get; } = attackClipPrefix;
        internal string IdleBinding { get; } = idleBinding;
        internal string NativeIdleClip { get; } = nativeIdleClip;
        internal string IdleClipPrefix { get; private set; }
        internal string IdleVariantBinding { get; private set; }
        internal IReadOnlyList<string> IdleBindings { get; private set; } = Array.AsReadOnly(
            string.IsNullOrEmpty(idleBinding) ? Array.Empty<string>() : new[] { idleBinding });
        internal IReadOnlyDictionary<string, string> ClipPrefixes { get; private set; } =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        internal string ClipVariantBinding { get; private set; }
        internal IReadOnlyDictionary<string, string> AttackEntries { get; private set; } =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        internal string Slot { get; } = slot;
        internal bool RequiresFreeOtherHand { get; } = requiresFreeOtherHand;
        internal bool? OtherHandOccupied { get; private set; }
        internal bool? InCombat { get; private set; }
        internal GraphicsCharacterDefinitions.CharacterType? CharacterType { get; private set; }
        internal string IdlePoseGroup { get; private set; }
        internal bool? OtherHandSharesIdlePose { get; private set; }
        internal bool SupportsSecondaryIdle { get; private set; }

        private WeaponAnimation Copy() => new(ModelName, AttackBinding, AttackClipPrefix, IdleBinding,
            NativeIdleClip, Slot, RequiresFreeOtherHand)
        {
            OtherHandOccupied = OtherHandOccupied, InCombat = InCombat, CharacterType = CharacterType,
            IdlePoseGroup = IdlePoseGroup, OtherHandSharesIdlePose = OtherHandSharesIdlePose,
            SupportsSecondaryIdle = SupportsSecondaryIdle,
            IdleClipPrefix = IdleClipPrefix,
            IdleVariantBinding = IdleVariantBinding, IdleBindings = IdleBindings,
            ClipPrefixes = ClipPrefixes, ClipVariantBinding = ClipVariantBinding, AttackEntries = AttackEntries
        };

        internal WeaponAnimation WithOtherHandOccupied(bool occupied)
        {
            var copy = Copy();
            copy.OtherHandOccupied = occupied;
            return copy;
        }

        internal WeaponAnimation WithCombatState(bool inCombat)
        {
            var copy = Copy();
            copy.InCombat = inCombat;
            return copy;
        }

        internal WeaponAnimation WithCharacterType(GraphicsCharacterDefinitions.CharacterType characterType)
        {
            var copy = Copy();
            copy.CharacterType = characterType;
            return copy;
        }

        internal WeaponAnimation WithIdlePoseGroup(string group)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                throw new ArgumentException("An idle pose group must be named.", nameof(group));
            }
            var copy = Copy();
            copy.IdlePoseGroup = group;
            return copy;
        }

        internal WeaponAnimation WithOtherHandSharesIdlePose(bool sharesPose)
        {
            var copy = Copy();
            copy.OtherHandSharesIdlePose = sharesPose;
            return copy;
        }

        internal WeaponAnimation WithSecondaryIdleSupport(bool supported)
        {
            var copy = Copy();
            copy.SupportsSecondaryIdle = supported;
            return copy;
        }

        internal WeaponAnimation WithAttackEntries(IReadOnlyDictionary<string, string> entries)
        {
            var values = entries?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ??
                         new Dictionary<string, string>(StringComparer.Ordinal);
            if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            {
                throw new ArgumentException("Invalid equipment attack entries.", nameof(entries));
            }
            var copy = Copy();
            copy.AttackEntries = new ReadOnlyDictionary<string, string>(values);
            return copy;
        }

        internal WeaponAnimation WithIdleAnimation(string prefix, string variantBinding = null)
        {
            var copy = Copy();
            copy.IdleClipPrefix = prefix;
            copy.IdleVariantBinding = variantBinding;
            return copy;
        }

        internal WeaponAnimation WithIdleBindings(IEnumerable<string> bindings)
        {
            var values = bindings?.ToArray() ?? Array.Empty<string>();
            if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            {
                throw new ArgumentException("Invalid equipment idle bindings.", nameof(bindings));
            }
            var copy = Copy();
            copy.IdleBindings = Array.AsReadOnly(values);
            return copy;
        }

        internal WeaponAnimation WithClipAnimations(IReadOnlyDictionary<string, string> prefixes,
            string variantBinding = null)
        {
            var values = prefixes?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ??
                         new Dictionary<string, string>(StringComparer.Ordinal);
            if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            {
                throw new ArgumentException("Invalid equipment clip bindings.", nameof(prefixes));
            }
            var copy = Copy();
            copy.ClipPrefixes = new ReadOnlyDictionary<string, string>(values);
            copy.ClipVariantBinding = variantBinding;
            return copy;
        }
    }

    internal static void RefreshEquipmentPresentation(GraphicsCharacter character)
    {
        if (!character)
        {
            return;
        }

        var presentation = character.GetComponent<EquipmentPresentation>();
        if (presentation && presentation.IsEvaluating)
        {
            return;
        }
        var animation = GetEquippedWeaponAnimation(character);
        var attackAnimation = GetWeaponAnimation(character, animation);
        var attackEntry = GetEquipmentAttackEntry(character);
        var references = CustomModels.RequiresEquipmentPoseReferences(character);

        if (animation == null && attackAnimation == null && !references)
        {
            presentation?.Release();
            return;
        }

        presentation ??= character.gameObject.AddComponent<EquipmentPresentation>();
        presentation.Refresh(character, animation, attackAnimation, attackEntry, references);
    }

    internal static bool IsEquipmentControllerCurrent(GraphicsCharacter character)
    {
        if (!character || !character.Animator)
        {
            return false;
        }
        var current = character.Animator.runtimeAnimatorController;
        var presentation = character.GetComponent<EquipmentPresentation>();
        return current && (current == character.CurrentAnimatorController.Value ||
                           presentation && presentation.IsCurrent && current == presentation.Controller);
    }

    internal static RuntimeAnimatorController GetEquipmentReferenceController(GraphicsCharacter character)
    {
        var presentation = character ? character.GetComponent<EquipmentPresentation>() : null;
        return presentation && presentation.IsCurrent ? presentation.ReferenceController : null;
    }

    internal static void ReleaseEquipmentPresentation(GraphicsCharacter character)
    {
        if (character)
        {
            character.GetComponent<EquipmentPresentation>()?.Release();
        }
    }

    private static bool EquipmentCombatState(GraphicsCharacter character)
    {
        return character.IsInCombat;
    }

    private static string GetEquipmentAttackEntry(GraphicsCharacter character) =>
        EquipmentAttackStates.TryGetValue(character, out var action) && action.SameActor && action.IsCurrent
            ? action.EntryClipName
            : null;

    private static string CaptureEquipmentEntry(Animator animator)
    {
        if (!animator || !animator.isInitialized || !animator.runtimeAnimatorController || animator.layerCount == 0)
        {
            return null;
        }
        // Read once before the native attack changes the selector. Nested actions
        // keep their outer equipment entry rather than treating its attack as idle.
        var clips = animator.GetCurrentAnimatorClipInfo(0).AsEnumerable();
        if (animator.IsInTransition(0)) clips = clips.Concat(animator.GetNextAnimatorClipInfo(0));
        var sample = clips.Where(info => info.clip && info.weight > 0f)
            .OrderByDescending(info => info.clip.isLooping).ThenByDescending(info => info.weight).FirstOrDefault();
        return sample.clip ? sample.clip.name : null;
    }

    private static WeaponAnimation GetWeaponAnimation(GraphicsCharacter character) =>
        GetWeaponAnimation(character, GetEquippedWeaponAnimation(character));

    private static WeaponAnimation GetWeaponAnimation(GraphicsCharacter character, WeaponAnimation equipped) =>
        ResolveWeaponAnimation(character, true, equipped);

    private static WeaponAnimation GetEquippedWeaponAnimation(GraphicsCharacter character) => ResolveWeaponAnimation(character, false);

    private static WeaponAnimation ResolveWeaponAnimation(GraphicsCharacter character, bool activeAction,
        WeaponAnimation equipped = null)
    {
        string activeSlot = null;
        var inCombat = EquipmentCombatState(character);
        if (activeAction && EquipmentAttackStates.TryGetValue(character, out var attack) && attack.SameActor)
        {
            if (!attack.IsCurrent)
            {
                return null;
            }
            activeSlot = attack.Slot;
            inCombat = attack.InCombat;
        }
        if (activeAction && activeSlot == null)
        {
            if (equipped == null)
            {
                return null;
            }
            activeSlot = equipped.Slot;
        }
        var nativePrimaryIdle = false;
        for (var index = 0; index < 2; index++)
        {
            var slot = index == 0 ? EquipmentDefinitions.SlotTypeMainHand : EquipmentDefinitions.SlotTypeOffHand;
            if (activeSlot != null && slot != activeSlot ||
                !character.WieldedRulesetItems.TryGetValue(slot, out var item) ||
                item?.ItemDefinition is not { IsWeapon: true } definition ||
                activeAction && CustomModels.GetWieldedItemFeature<CustomModels.WieldedModel>(definition) != null &&
                !CustomModels.IsWieldedItemPresentationReady(character, slot))
            {
                continue;
            }
            var otherSlot = slot == EquipmentDefinitions.SlotTypeMainHand
                ? EquipmentDefinitions.SlotTypeOffHand
                : EquipmentDefinitions.SlotTypeMainHand;
            var occupied = character.WieldedRulesetItems.TryGetValue(otherSlot, out var other) && other != null;
            foreach (var animation in CustomModels.GetWieldedItemFeatures<WeaponAnimation>(definition))
            {
                if (MatchesEquipmentSelector(animation, slot, occupied, inCombat, character.CharacterType) &&
                    (!animation.OtherHandSharesIdlePose.HasValue ||
                     animation.OtherHandSharesIdlePose.Value ==
                     (occupied && SharesIdlePose(animation, other.ItemDefinition, otherSlot, inCombat,
                         character.CharacterType))) &&
                    (activeAction || !nativePrimaryIdle || animation.SupportsSecondaryIdle))
                {
                    return animation;
                }
            }
            if (!activeAction && index == 0)
            {
                // Only a secondary holding clip that preserves the native primary
                // pose may opt in here. An off-hand attack alone cannot own idle.
                nativePrimaryIdle = true;
            }
        }

        return null;
    }

    private static bool MatchesEquipmentSelector(WeaponAnimation animation, string slot, bool occupied,
        bool inCombat, GraphicsCharacterDefinitions.CharacterType characterType) =>
        animation.Slot == slot && (!animation.RequiresFreeOtherHand || !occupied) &&
        (!animation.OtherHandOccupied.HasValue || animation.OtherHandOccupied.Value == occupied) &&
        (!animation.InCombat.HasValue || animation.InCombat.Value == inCombat) &&
        (!animation.CharacterType.HasValue || animation.CharacterType.Value == characterType);

    private static bool SharesIdlePose(WeaponAnimation animation, ItemDefinition other, string otherSlot,
        bool inCombat, GraphicsCharacterDefinitions.CharacterType characterType)
    {
        if (animation.IdlePoseGroup == null)
        {
            return false;
        }
        // Holding compatibility belongs to the animation declaration. Different
        // equipment geometry can share a pose; identical geometry need not do so.
        foreach (var candidate in CustomModels.GetWieldedItemFeatures<WeaponAnimation>(other))
        {
            if (candidate.IdlePoseGroup == animation.IdlePoseGroup &&
                candidate.OtherHandSharesIdlePose != false &&
                MatchesEquipmentSelector(candidate, otherSlot, true, inCombat, characterType) &&
                candidate.IdleBindings.Count > 0 &&
                (!string.IsNullOrEmpty(candidate.IdleClipPrefix) || !string.IsNullOrEmpty(candidate.NativeIdleClip)))
            {
                return true;
            }
        }
        return false;
    }

    private static IDisposable BeginEquipmentAttack(CharacterAction action)
    {
        var mode = action?.ActionParams?.AttackMode;
        var actor = action?.ActingCharacter?.RulesetCharacter;
        var slot = mode?.SlotName;
        if (action is not CharacterActionAttack || actor == null ||
            slot != EquipmentDefinitions.SlotTypeMainHand && slot != EquipmentDefinitions.SlotTypeOffHand)
        {
            return EmptyScope.Instance;
        }
        var graphicsCharacters = ServiceRepository.GetService<IGraphicsCharacterFactoryService>()?.GraphicsCharacters;
        if (graphicsCharacters == null)
        {
            return EmptyScope.Instance;
        }
        var leases = new List<EquipmentAttackState>();
        try
        {
            foreach (var graphics in graphicsCharacters)
            {
                if (!graphics || !graphics.IsBoundToGameCharacter || graphics.RulesetCharacter != actor ||
                    !graphics.WieldedRulesetItems.TryGetValue(slot, out var item) || item == null ||
                    (mode.SourceObject is RulesetItem sourceItem
                        ? !ReferenceEquals(sourceItem, item)
                        : mode.SourceObject != null || item.ItemDefinition != mode.SourceDefinition) ||
                    !graphics.WieldedInstantiatedItems.TryGetValue(slot, out var model) || !model)
                {
                    continue;
                }
                EquipmentAttackStates.TryGetValue(graphics, out var previous);
                if (previous != null && !previous.SameActor)
                {
                    for (var stale = previous; stale != null; stale = stale.Previous)
                    {
                        stale.StopObservingNativeAttack();
                        stale.Active = false;
                    }
                    previous = null;
                }
                var state = new EquipmentAttackState(graphics, item, model, slot, previous);
                EquipmentAttackStates[graphics] = state;
                leases.Add(state);
                // Native AttackStart triggers GraphicsCharacter.WeaponAnimator,
                // whose factory cache otherwise names only the main-hand item.
                graphics.WeaponAnimator = state.WeaponAnimator;
                RefreshEquipmentPresentation(graphics);
                var presentation = graphics.GetComponent<EquipmentPresentation>();
                var authored = presentation && presentation.HasAuthoredAttack;
                state.ObserveNativeAttack(action.ActingCharacter, authored);
                state.ApplyAttackRoute(mode.Ranged && !mode.Thrown && authored);
            }
            return leases.Count == 0 ? EmptyScope.Instance : new EquipmentAttackScope(leases);
        }
        catch
        {
            for (var i = leases.Count - 1; i >= 0; i--)
            {
                ReleaseEquipmentAttack(leases[i], false);
            }
            throw;
        }
    }

    private static void ReleaseEquipmentAttack(EquipmentAttackState state, bool refresh = true)
    {
        state.StopObservingNativeAttack();
        if (!state.Active)
        {
            return;
        }
        state.Active = false;
        var graphics = state.Graphics;
        if (!EquipmentAttackStates.TryGetValue(graphics, out var current) || !ReferenceEquals(current, state))
        {
            return;
        }
        var restore = state.PreviousAnimator;
        var restoreShield = state.PreviousShield;
        var restoreWeaponType = state.PreviousWeaponType;
        var previous = state.Previous;
        while (previous != null && !previous.IsBound)
        {
            if (restore == previous.WeaponAnimator)
            {
                restore = previous.PreviousAnimator;
            }
            restoreShield = previous.UnwindShieldValue(restoreShield, state);
            restoreWeaponType = previous.UnwindWeaponTypeValue(restoreWeaponType, state);
            previous.Active = false;
            previous.StopObservingNativeAttack();
            previous = previous.Previous;
        }
        // This lease owns the selector, not the Animator's controller or rig.
        // Retain an external selector write and a pooled actor's new ownership.
        if (state.SameActor && graphics.WeaponAnimator == state.WeaponAnimator)
        {
            graphics.WeaponAnimator = restore ? restore : null;
        }
        state.RestoreAttackRoute(restoreShield);
        state.RestoreWeaponType(restoreWeaponType);
        if (previous != null)
        {
            EquipmentAttackStates[graphics] = previous;
        }
        else
        {
            EquipmentAttackStates.Remove(graphics);
        }
        if (refresh && state.SameActor)
        {
            RefreshEquipmentPresentation(graphics);
        }
    }

    private static RuntimeAnimatorController BaseController(RuntimeAnimatorController controller)
    {
        while (controller is AnimatorOverrideController overrides)
        {
            controller = overrides.runtimeAnimatorController;
        }

        return controller;
    }

    private static void SetEquipmentController(GraphicsCharacter character, Animator animator,
        RuntimeAnimatorController controller)
    {
        if (animator.runtimeAnimatorController == controller)
        {
            return;
        }

        // Only this owner's same-base swaps preserve the in-flight leases. External
        // controller replacements still end them through the ordinary identity checks.
        var sameBase = BaseController(animator.runtimeAnimatorController) == BaseController(controller);
        AnimatorStates.TryGetValue(animator, out var speed);
        MovementReactionStates.TryGetValue(animator, out var movement);
        var keepSpeed = sameBase && speed?.IsCurrent(animator) == true;
        var keepMovement = sameBase && movement?.IsCurrent(character) == true;
        animator.runtimeAnimatorController = controller;
        if (keepSpeed)
        {
            speed.FollowController(controller);
        }

        if (keepMovement)
        {
            movement.FollowController(controller);
        }
    }

    // One owner combines declared carry/ready/action clips. Native state,
    // event timing and the source controller's other overrides remain intact.
    private sealed class EquipmentPresentation : MonoBehaviour
    {
        private GraphicsCharacter _character;
        private RulesetCharacter _actor;
        private Animator _animator;
        private Avatar _avatar;
        private RuntimeAnimatorController _source;
        private AnimatorOverrideController _override;
        private readonly List<AnimationClip> _clips = [];
        // Equipment chooses all ordinary state clips. The actual action may select
        // a different hand/model for the attack binding without replacing that idle.
        private WeaponAnimation _animation, _attackAnimation;
        private string _attackEntry;
        private bool _normalEnabled;
        private RulesetItem _normalItem;
        private GameObject _normalRoot;
        private GraphicsCharacterDefinitions.CharacterType _type;
        private bool _references;
        private bool _attackApplied;
        private bool _evaluating;
        private AsyncOperationHandle<RuntimeAnimatorController> _load;
        private bool _ownsLoad;
        private string _key;
        internal RuntimeAnimatorController ReferenceController { get; private set; }
        internal RuntimeAnimatorController Controller => _override;
        internal bool IsEvaluating => _evaluating;
        internal bool HasAuthoredAttack => IsCurrent && _override && _attackApplied &&
                                           _animator.runtimeAnimatorController == _override;
        internal bool IsCurrent => _character && _animator && _character.Animator == _animator &&
                                   ReferenceEquals(_actor, _character.RulesetCharacter) &&
                                   _avatar == _animator.avatar && _source == _character.CurrentAnimatorController.Value &&
                                   _key == _character.CurrentAnimatorController.Key &&
                                   _type == _character.CharacterType &&
                                   (_animator.runtimeAnimatorController == _source ||
                                    _override && _animator.runtimeAnimatorController == _override);

        private bool CanUseNormalMotion(GraphicsCharacter character, WeaponAnimation animation)
        {
            if (animation == null || !character.WieldedRulesetItems.TryGetValue(animation.Slot, out var item) || item == null)
            {
                return false;
            }
            if (CustomModels.GetWieldedItemFeature<CustomModels.WieldedModel>(item.ItemDefinition) == null ||
                CustomModels.IsWieldedItemPresentationReady(character, animation.Slot))
            {
                return true;
            }

            // A previously validated equipment baseline survives native item hiding
            // while its actual item/root/rig remain owned. A visible/pending/unsupported model still
            // falls back; its declaration never gets replaced by the other slot.
            return IsCurrent && _normalEnabled && ReferenceEquals(_animation, animation) &&
                   ReferenceEquals(_normalItem, item) && _normalRoot && !_normalRoot.activeInHierarchy &&
                   character.WieldedInstantiatedItems.TryGetValue(animation.Slot, out var root) && root == _normalRoot;
        }

        private void CaptureNormalBinding()
        {
            _normalItem = null;
            _normalRoot = null;
            if (!_normalEnabled || _animation == null) return;
            _character.WieldedRulesetItems.TryGetValue(_animation.Slot, out _normalItem);
            _character.WieldedInstantiatedItems.TryGetValue(_animation.Slot, out _normalRoot);
        }

        internal void Refresh(GraphicsCharacter character, WeaponAnimation animation, WeaponAnimation attackAnimation, string attackEntry, bool references)
        {
            var animator = character.Animator;
            var key = character.CurrentAnimatorController.Key;

            if (!animator || !animator.isHuman || !animator.runtimeAnimatorController ||
                string.IsNullOrEmpty(key))
            {
                Release();
                return;
            }

            var current = animator.runtimeAnimatorController;
            var normalEnabled = CanUseNormalMotion(character, animation);

            if (_character && _animator == animator && _avatar == animator.avatar && _key == key &&
                _type == character.CharacterType &&
                ReferenceEquals(_actor, character.RulesetCharacter) &&
                _source == character.CurrentAnimatorController.Value &&
                (current == _override || !_override && current == _source))
            {
                if (_references == references && _normalEnabled == normalEnabled && ReferenceEquals(_animation, animation) &&
                    ReferenceEquals(_attackAnimation, attackAnimation) && _attackEntry == attackEntry)
                {
                    return;
                }

                // Pose eligibility can change while the same native reference is
                // still needed to evaluate it. Retain that reference and its load
                // handle when switching only the owned presentation overrides.
                _references = references;
                _animation = animation;
                _attackAnimation = attackAnimation;
                _attackEntry = attackEntry;
                _normalEnabled = normalEnabled;
                CaptureNormalBinding();
                if (ReferenceController)
                {
                    Apply(ReferenceController);
                }
                return;
            }

            Release();
            current = animator.runtimeAnimatorController;

            // A separate presentation owner may have replaced the native controller.
            // Do not wrap that owner's controller or restore over it on a later refresh.
            if (current != character.CurrentAnimatorController.Value)
            {
                return;
            }

            _character = character;
            _actor = character.RulesetCharacter;
            _animator = animator;
            _avatar = animator.avatar;
            _source = current;
            _key = key;
            _type = character.CharacterType;
            _references = references;
            _animation = animation;
            _attackAnimation = attackAnimation;
            _attackEntry = attackEntry;
            _normalEnabled = normalEnabled;
            CaptureNormalBinding();
            EquipmentPresentations.Add(this);
            if (!key.Contains("_Portrait_Controller"))
            {
                Apply(_source);
                return;
            }

            _load = Addressables.LoadAssetAsync<RuntimeAnimatorController>(
                key.Replace("_Portrait_Controller", "_Controller"));
            _ownsLoad = true;
            _load.Completed += Loaded;
        }

        private void Loaded(AsyncOperationHandle<RuntimeAnimatorController> operation)
        {
            if (!_ownsLoad || !operation.Equals(_load))
            {
                return;
            }

            if (operation.Status != AsyncOperationStatus.Succeeded || !operation.Result ||
                !_character || !_animator || _character.Animator != _animator ||
                _animator.avatar != _avatar || !ReferenceEquals(_actor, _character.RulesetCharacter) ||
                _character.CurrentAnimatorController.Key != _key ||
                _character.CharacterType != _type ||
                _references != CustomModels.RequiresEquipmentPoseReferences(_character) ||
                !ReferenceEquals(_animation, GetEquippedWeaponAnimation(_character)) ||
                !ReferenceEquals(_attackAnimation, GetWeaponAnimation(_character)) ||
                _attackEntry != GetEquipmentAttackEntry(_character) ||
                _animator.runtimeAnimatorController != _source ||
                _character.CurrentAnimatorController.Value != _source)
            {
                Release();
                return;
            }

            Apply(operation.Result);
        }

        private void Apply(RuntimeAnimatorController nativeController)
        {
            ReferenceController = nativeController;
            var normal = _normalEnabled ? _animation : null;
            if (normal == null && _attackAnimation == null)
            {
                // A declared holding pose borrows the matching native source
                // without replacing the live controller or any layer weights.
                ReleaseOverrides();
                return;
            }
            var authoredIdle = !string.IsNullOrEmpty(normal?.IdleClipPrefix);
            var idle = !authoredIdle && !string.IsNullOrEmpty(normal?.NativeIdleClip)
                ? nativeController.animationClips.FirstOrDefault(clip => clip && clip.name == normal.NativeIdleClip)
                : null;
            if (!authoredIdle && !string.IsNullOrEmpty(normal?.NativeIdleClip) && !idle)
            {
                Release();
                return;
            }

            _override ??= new AnimatorOverrideController(_source)
            {
                name = _source.name + "_Equipment"
            };
            var previousClipCount = _clips.Count;
            var attackApplied = false;
            var clips = new List<KeyValuePair<AnimationClip, AnimationClip>>(_override.overridesCount);

            if (_source is AnimatorOverrideController sourceOverrides)
            {
                sourceOverrides.GetOverrides(clips);
            }
            else
            {
                _override.GetOverrides(clips);
                // Rebuild from the native bindings, not this owner's previous
                // equipment. An unequipped slot must release its old replacements.
                for (var i = 0; i < clips.Count; i++)
                {
                    clips[i] = new KeyValuePair<AnimationClip, AnimationClip>(clips[i].Key, null);
                }
            }


            if (authoredIdle)
            {
                var variantBinding = normal.IdleVariantBinding ?? normal.IdleBinding;
                var native = ResolveNativeBinding(clips, nativeController, variantBinding);
                var authored = native ? ResolveAuthoredClip(normal.IdleClipPrefix, native, native, false) : null;
                if (!authored || authored.events.Length != 0)
                {
                    Main.Error("Missing or event-bearing authored equipment idle animation.");
                    Release();
                    return;
                }

                idle = Instantiate(authored);
                idle.name = authored.name;
                _clips.Add(idle);
            }

            var clipVariant = !string.IsNullOrEmpty(normal?.ClipVariantBinding)
                ? ResolveNativeBinding(clips, nativeController, normal.ClipVariantBinding)
                : null;
            if (!string.IsNullOrEmpty(normal?.ClipVariantBinding) && !clipVariant)
            {
                Release();
                return;
            }

            AnimationClip entryLoop = null;
            if (_attackEntry != null && _attackAnimation != null &&
                _attackAnimation.ClipPrefixes.Keys.Any(binding =>
                    ResolveNativeBinding(clips, nativeController, binding)?.isLooping == true))
            {
                var idleBindings = (normal?.IdleBindings ?? Array.Empty<string>()).Concat(_attackAnimation.IdleBindings);
                var declaredEntry = _attackAnimation.AttackEntries.ContainsKey(_attackEntry) ||
                                    idle && idle.name == _attackEntry ||
                                    _attackEntry == normal?.NativeIdleClip ||
                                    idleBindings.Any(binding =>
                                        ResolveNativeBinding(clips, nativeController, binding)?.name == _attackEntry) ||
                                    normal != null && normal.ClipPrefixes.Any(pair =>
                                    {
                                        var source = ResolveNativeBinding(clips, nativeController, pair.Key);
                                        return source && source.isLooping &&
                                               ResolveAuthoredClip(pair.Value, source, clipVariant, false, normal)?.name ==
                                               _attackEntry;
                                    });
                if (!declaredEntry)
                {
                    Main.Error("No declared equipment attack entry loop.");
                    Release();
                    return;
                }
                var models = new[] { normal?.ModelName, _attackAnimation.ModelName }
                    .Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal);
                var entries = nativeController.animationClips.Concat(models.SelectMany(CustomModels.GetAnimationClips))
                    .Where(clip => clip && clip.name == _attackEntry && clip.isHumanMotion && clip.isLooping)
                    .Distinct().ToArray();
                if (entries.Length != 1)
                {
                    Main.Error("No unique looping equipment attack entry.");
                    Release();
                    return;
                }
                entryLoop = entries[0];
            }

            var replaced = false;

            for (var i = 0; i < clips.Count; i++)
            {
                var key = clips[i].Key;
                AnimationClip replacement = null;
                if (_attackAnimation != null && key.name == _attackAnimation.AttackBinding)
                {
                    var prefix = _attackAnimation.AttackClipPrefix;
                    if (_attackEntry != null && _attackAnimation.AttackEntries.TryGetValue(_attackEntry, out var entryPrefix))
                    {
                        prefix = entryPrefix;
                    }
                    replacement = CreateTransientClip(clips[i].Value ? clips[i].Value : key,
                        prefix, declaration: _attackAnimation);
                    if (!replacement)
                    {
                        Release();
                        return;
                    }
                    attackApplied = true;
                }
                else if (entryLoop && _attackAnimation.ClipPrefixes.ContainsKey(key.name) &&
                         (clips[i].Value ? clips[i].Value : key).isLooping)
                {
                    // Native attack preparation changes the idle selector before its trigger.
                    // Keep this action's actual entry loop through preparation and return.
                    replacement = entryLoop;
                }
                else if (idle && normal.IdleBindings.Contains(key.name))
                {
                    replacement = idle;
                }
                else if (normal != null && normal.ClipPrefixes.TryGetValue(key.name, out var prefix))
                {
                    replacement = CreateTransientClip(clips[i].Value ? clips[i].Value : key, prefix, clipVariant);
                    if (!replacement)
                    {
                        Release();
                        return;
                    }
                }

                if (!replacement)
                {
                    continue;
                }

                clips[i] = new KeyValuePair<AnimationClip, AnimationClip>(key, replacement);
                replaced = true;
            }

            if (!replaced)
            {
                Release();
                return;
            }

            _override.ApplyOverrides(clips);
            _attackApplied = attackApplied;
            // Same-base override swaps retain native state/phase, parameters and layers.
            // Rebinding would reset the native class pose, hands and body deformation.
            SetEquipmentController(_character, _animator, _override);
            // The live controller kept the previous clips until every replacement
            // was resolved and committed together. Only then release those clones.
            for (var i = 0; i < previousClipCount; i++)
            {
                Destroy(_clips[i]);
            }
            _clips.RemoveRange(0, previousClipCount);
            EvaluateBindings();
        }

        private void EvaluateBindings()
        {
            if (_evaluating || !IsCurrent || !_animator.enabled || !_animator.isInitialized ||
                !_animator.gameObject.activeInHierarchy)
            {
                return;
            }

            // Changing bindings preserves the state machine but leaves its new
            // transform bindings unevaluated until the next animation pass.
            // Equipment can change after that pass, before the preview renders.
            _evaluating = true;
            try
            {
                _animator.Update(0f);
                if (IsCurrent)
                {
                    CustomModels.ReapplyEquipmentPoses(_character);
                }
            }
            finally
            {
                _evaluating = false;
            }
        }

        private static AnimationClip ResolveNativeBinding(
            List<KeyValuePair<AnimationClip, AnimationClip>> clips, RuntimeAnimatorController reference, string name)
        {
            var binding = clips.FirstOrDefault(pair => pair.Key && pair.Key.name == name);
            var native = binding.Value ? binding.Value : binding.Key;
            if (native)
            {
                return native;
            }
            var matches = reference.animationClips.Where(clip => clip && clip.name == name).Distinct().ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private AnimationClip CreateTransientClip(AnimationClip native, string prefix, AnimationClip variant = null,
            WeaponAnimation declaration = null)
        {
            var loop = native.isLooping;
            var authored = ResolveAuthoredClip(prefix, native, variant, !loop, declaration);
            if (!authored)
            {
                return null;
            }
            if (loop && (!authored.isLooping || authored.events.Length != 0))
            {
                Main.Error($"Invalid authored looping animation for {native.name}.");
                return null;
            }

            var result = Instantiate(authored);
            result.name = authored.name;
            // Every native payload (including audio) retains its original time.
            // The shared authored and native clips remain unchanged.
            if (!loop)
            {
                result.events = native.events;
            }
            _clips.Add(result);
            return result;
        }

        private AnimationClip ResolveAuthoredClip(string prefix, AnimationClip native, AnimationClip variant = null,
            bool requireNativeLength = true, WeaponAnimation declaration = null)
        {
            if (string.IsNullOrEmpty(prefix) || !native)
            {
                return null;
            }
            var name = variant
                ? prefix + Mathf.RoundToInt(variant.length * 60f).ToString(CultureInfo.InvariantCulture)
                : null;
            AnimationClip authored = null;
            foreach (var clip in CustomModels.GetAnimationClips((declaration ?? _animation).ModelName))
            {
                if (!clip || !clip.isHumanMotion ||
                    (name != null ? clip.name != name : !clip.name.StartsWith(prefix, StringComparison.Ordinal)) ||
                    requireNativeLength && Math.Abs(clip.length - native.length) > 0.0001f)
                {
                    continue;
                }

                if (authored)
                {
                    Main.Error($"Ambiguous authored animation for {native.name}, length {native.length}.");
                    return null;
                }

                authored = clip;
            }

            if (!authored)
            {
                Main.Error($"No authored Humanoid animation matches {native.name}, length {native.length}.");
                return null;
            }

            return authored;
        }

        private void ReleaseOverrides()
        {
            var evaluate = false;
            if (_animator && _source && _override && _animator.runtimeAnimatorController == _override)
            {
                // The native owner may already have rebound this pooled view to a new rig.
                // Only replace our own controller, and restore the latest native source.
                var native = _character && _character.Animator == _animator
                    ? _character.CurrentAnimatorController.Value
                    : null;
                evaluate = IsCurrent;
                SetEquipmentController(_character, _animator, native ? native : _source);
            }

            if (_override)
            {
                Destroy(_override);
            }

            foreach (var clip in _clips)
            {
                Destroy(clip);
            }

            _clips.Clear();
            _override = null;
            _attackApplied = false;
            if (evaluate)
            {
                EvaluateBindings();
            }
        }

        internal void Release()
        {
            ReleaseOverrides();

            if (_ownsLoad)
            {
                _ownsLoad = false;
                _load.Completed -= Loaded;
                Addressables.Release(_load);
            }

            _character = null;
            _actor = null;
            _animator = null;
            _avatar = null;
            _source = null;
            _animation = null;
            _attackAnimation = null;
            _attackEntry = null;
            _normalEnabled = false;
            _normalItem = null;
            _normalRoot = null;
            _references = false;
            ReferenceController = null;
            _key = null;
            EquipmentPresentations.Remove(this);
        }

        [UsedImplicitly]
        private void OnDisable() => Release();

        [UsedImplicitly]
        private void OnDestroy()
        {
            Release();
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
        var equipment = BeginEquipmentAttack(action);
        if (action?.ActingCharacter == null || IsMovementAction(action) ||
            Gui.Battle == null ||
            !Main.Settings.EnableSmootherBattleAnimations)
        {
            return equipment;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (battleService is not { IsBattleInProgress: true })
        {
            return equipment;
        }

        var speedMultiplier = ClampSpeedMultiplier(Main.Settings.BattleActionAnimationSpeedMultiplier);

        if (speedMultiplier < MinEffectiveSpeedMultiplier)
        {
            return equipment;
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

        try
        {
            var animators = Apply(characters);
            return animators.Count == 0 ? equipment : new ActionScope(animators, equipment);
        }
        catch
        {
            equipment.Dispose();
            throw;
        }
    }

    internal static void Unload()
    {
        foreach (var presentation in EquipmentPresentations.ToArray())
        {
            if (presentation)
            {
                presentation.Release();
            }
        }

        EquipmentPresentations.Clear();

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

        foreach (var state in EquipmentAttackStates.Values.ToArray())
        {
            for (var current = state; current != null; current = current.Previous)
            {
                ReleaseEquipmentAttack(current, false);
            }
        }
        EquipmentAttackStates.Clear();
    }

    internal static void RefreshActionAnimation(GraphicsCharacter graphicsCharacter)
    {
        if (!graphicsCharacter)
        {
            return;
        }

        CustomModels.UpdateWieldedItemPresentations(graphicsCharacter);
        RefreshEquipmentPresentation(graphicsCharacter);

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

        // Existing callers are native graphics shutdown and rig reset/rebind.
        // Release item-local geometry/poses before those bones are regenerated.
        CustomModels.ReleaseWieldedItemPresentations(graphicsCharacter);

        ForgetAnimator(graphicsCharacter.Animator);
        ForgetAnimator(graphicsCharacter.WeaponAnimator);
        foreach (var entry in AnimatorStates.Where(entry => entry.Value.GraphicsCharacter == graphicsCharacter).ToArray())
        {
            ForgetAnimator(entry.Key);
        }
        ForgetMovementReaction(graphicsCharacter.Animator);
        if (EquipmentAttackStates.TryGetValue(graphicsCharacter, out var attack))
        {
            while (attack != null)
            {
                var previous = attack.Previous;
                ReleaseEquipmentAttack(attack, false);
                attack = previous;
            }
        }
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
        private RuntimeAnimatorController _controller = animator.runtimeAnimatorController;
        private readonly RulesetCharacter _character = graphicsCharacter.RulesetCharacter;
        internal float OriginalSpeed = originalSpeed;
        internal float AppliedSpeed = originalSpeed;
        internal int ReferenceCount = 1;

        internal bool IsCurrent(Animator animator)
        {
            return OwnsAnimator(animator) && animator.runtimeAnimatorController == _controller;
        }

        internal void FollowController(RuntimeAnimatorController controller) => _controller = controller;

        internal bool OwnsAnimator(Animator animator)
        {
            if (!animator || !GraphicsCharacter || GraphicsCharacter.RulesetCharacter != _character ||
                Character?.RulesetCharacter != _character)
            {
                return false;
            }
            if (GraphicsCharacter.Animator == animator || GraphicsCharacter.WeaponAnimator == animator)
            {
                return true;
            }
            // An inner off-hand action can temporarily select a different weapon
            // Animator while an outer action still owns the first one's speed.
            foreach (var model in GraphicsCharacter.WieldedInstantiatedItems.Values)
            {
                if (model && animator.transform.IsChildOf(model.transform))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private readonly struct AnimatorLease(Animator animator, AnimatorState state)
    {
        internal Animator Animator { get; } = animator;
        internal AnimatorState State { get; } = state;
    }

    private sealed class EquipmentAttackState
    {
        internal readonly GraphicsCharacter Graphics;
        internal readonly string Slot;
        internal readonly string EntryClipName;
        internal readonly bool InCombat;
        internal readonly Animator WeaponAnimator, PreviousAnimator;
        internal readonly EquipmentAttackState Previous;
        internal bool Active = true;
        private readonly RulesetCharacter _actor;
        private readonly RulesetItem _item;
        private readonly GameObject _model;
        private readonly Animator _body;
        private readonly Avatar _avatar;
        private readonly RuntimeAnimatorController _source, _weaponController;
        private readonly string _key;
        internal readonly float PreviousShield;
        private readonly float _nativeShield;
        private readonly bool _hasShieldParameter;
        private float _appliedShield;
        private bool _ownsShield;
        internal readonly float PreviousWeaponType;
        private readonly bool _hasWeaponTypeParameter;
        private float _appliedWeaponType;
        private bool _ownsWeaponType;
        private GameLocationCharacter _nativeAttackActor;

        internal EquipmentAttackState(GraphicsCharacter graphics, RulesetItem item, GameObject model,
            string slot, EquipmentAttackState previous)
        {
            Graphics = graphics;
            Slot = slot;
            InCombat = EquipmentCombatState(graphics);
            Previous = previous;
            PreviousAnimator = graphics.WeaponAnimator;
            WeaponAnimator = CustomModels.GetWieldedWeaponAnimator(graphics, slot);
            _actor = graphics.RulesetCharacter;
            _item = item;
            _model = model;
            _body = graphics.Animator;
            EntryClipName = previous?.EntryClipName ?? CaptureEquipmentEntry(_body);
            _avatar = _body ? _body.avatar : null;
            _source = graphics.CurrentAnimatorController.Value;
            _key = graphics.CurrentAnimatorController.Key;
            _weaponController = WeaponAnimator ? WeaponAnimator.runtimeAnimatorController : null;
            _hasShieldParameter = _body && _body.parameters.Any(parameter =>
                parameter.nameHash == AnimationDefinitions.HasShieldId &&
                parameter.type == AnimatorControllerParameterType.Float);
            if (_hasShieldParameter)
            {
                PreviousShield = _body.GetFloat(AnimationDefinitions.HasShieldId);
                _nativeShield = previous?.OwnsShieldValue == true ? previous._nativeShield : PreviousShield;
            }
            _hasWeaponTypeParameter = _body && _body.parameters.Any(parameter =>
                parameter.nameHash == AnimationDefinitions.WeaponTypeId &&
                parameter.type == AnimatorControllerParameterType.Float);
            if (_hasWeaponTypeParameter)
            {
                PreviousWeaponType = _body.GetFloat(AnimationDefinitions.WeaponTypeId);
            }
        }

        internal bool SameActor => Graphics && ReferenceEquals(Graphics.RulesetCharacter, _actor) &&
                                   Graphics.Animator == _body;
        private bool SameBodyRig => SameActor && _body && _body.avatar == _avatar &&
                                      Graphics.CurrentAnimatorController.Value == _source &&
                                      Graphics.CurrentAnimatorController.Key == _key &&
                                      IsEquipmentControllerCurrent(Graphics);
        private bool OwnsShieldValue => _ownsShield && SameBodyRig &&
                                       _body.GetFloat(AnimationDefinitions.HasShieldId).Equals(_appliedShield);

        internal void ObserveNativeAttack(GameLocationCharacter actor, bool authored)
        {
            if (!authored || !_hasWeaponTypeParameter || actor == null)
            {
                return;
            }
            // GraphicsCharacter's existing native handler is already bound. Observe its
            // completed selector write without recomputing attack data or random choices.
            _nativeAttackActor = actor;
            actor.AttackStart += ObserveNativeAttackStart;
        }

        private void ObserveNativeAttackStart(GameLocationCharacter attacker, GameLocationCharacter target,
            RuleDefinitions.RollOutcome outcome, CharacterActionParams parameters, RulesetAttackMode mode,
            ActionModifier modifier)
        {
            if (attacker != _nativeAttackActor || !IsCurrent || mode?.SlotName != Slot ||
                !EquipmentAttackStates.TryGetValue(Graphics, out var current) || !ReferenceEquals(current, this) ||
                (mode.SourceObject is RulesetItem sourceItem
                    ? !ReferenceEquals(sourceItem, _item)
                    : mode.SourceObject != null || mode.SourceDefinition != _item.ItemDefinition))
            {
                return;
            }
            _appliedWeaponType = _body.GetFloat(AnimationDefinitions.WeaponTypeId);
            _ownsWeaponType = true;
        }

        internal void StopObservingNativeAttack()
        {
            if (_nativeAttackActor != null)
            {
                _nativeAttackActor.AttackStart -= ObserveNativeAttackStart;
                _nativeAttackActor = null;
            }
        }

        internal float UnwindWeaponTypeValue(float value, EquipmentAttackState current) =>
            _ownsWeaponType && _body == current._body && _avatar == current._avatar &&
            ReferenceEquals(_actor, current._actor) && _source == current._source && _key == current._key &&
            value.Equals(_appliedWeaponType) ? PreviousWeaponType : value;

        internal void RestoreWeaponType(float value)
        {
            if (_ownsWeaponType && SameBodyRig &&
                _body.GetFloat(AnimationDefinitions.WeaponTypeId).Equals(_appliedWeaponType))
            {
                _body.SetFloat(AnimationDefinitions.WeaponTypeId, value);
            }
            _ownsWeaponType = false;
        }

        internal void ApplyAttackRoute(bool authoredRanged)
        {
            if (!_hasShieldParameter || !SameBodyRig)
            {
                return;
            }
            // The native Normal Attack tree selects its shield-only melee branch
            // before reading WeaponType. Select the ranged branch only for this
            // action's ready authored shot; neither inventory nor rules change.
            // A nested melee action temporarily recovers the native shield route.
            var value = authoredRanged ? 0f : _nativeShield;
            if (_body.GetFloat(AnimationDefinitions.HasShieldId).Equals(value))
            {
                // A nested shot borrows the outer shot's value. It must still
                // unwind that lease if the outer scope is disposed first.
                _appliedShield = value;
                _ownsShield = Previous?.OwnsShieldValue == true;
                return;
            }
            _body.SetFloat(AnimationDefinitions.HasShieldId, value);
            _appliedShield = _body.GetFloat(AnimationDefinitions.HasShieldId);
            _ownsShield = _appliedShield.Equals(value);
        }

        internal float UnwindShieldValue(float value, EquipmentAttackState current) =>
            _ownsShield && _body == current._body && _avatar == current._avatar &&
            ReferenceEquals(_actor, current._actor) && _source == current._source && _key == current._key &&
            value.Equals(_appliedShield) ? PreviousShield : value;

        internal void RestoreAttackRoute(float value)
        {
            if (OwnsShieldValue)
            {
                _body.SetFloat(AnimationDefinitions.HasShieldId, value);
            }
            _ownsShield = false;
        }

        internal bool IsCurrent => IsBound && Graphics.WeaponAnimator == WeaponAnimator;
        internal bool IsBound => Active && SameActor && _body && _body.avatar == _avatar &&
                                   Graphics.CurrentAnimatorController.Value == _source &&
                                   Graphics.CurrentAnimatorController.Key == _key &&
                                   IsEquipmentControllerCurrent(Graphics) &&
                                   Graphics.WieldedRulesetItems.TryGetValue(Slot, out var item) &&
                                   ReferenceEquals(item, _item) &&
                                   Graphics.WieldedInstantiatedItems.TryGetValue(Slot, out var model) && model == _model &&
                                   (WeaponAnimator
                                       ? WeaponAnimator.runtimeAnimatorController == _weaponController
                                       : ReferenceEquals(WeaponAnimator, null));
    }

    private sealed class EquipmentAttackScope(List<EquipmentAttackState> states) : IDisposable
    {
        private List<EquipmentAttackState> _states = states;
        public void Dispose()
        {
            var owned = _states;
            _states = null;
            if (owned == null)
            {
                return;
            }
            for (var i = owned.Count - 1; i >= 0; i--)
            {
                ReleaseEquipmentAttack(owned[i]);
            }
        }
    }

    private sealed class ActionScope(List<AnimatorLease> animators, IDisposable equipment) : IDisposable
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

            try
            {
                foreach (var lease in ownedAnimators)
                {
                    Release(lease);
                }
            }
            finally
            {
                // Speed restoration still needs the selected native weapon.
                equipment.Dispose();
            }
        }
    }

    private sealed class MovementReactionState(
        GraphicsCharacter graphicsCharacter, Animator animator, GameLocationCharacter character)
    {
        internal readonly GraphicsCharacter GraphicsCharacter = graphicsCharacter;
        internal readonly GameLocationCharacter Character = character;
        private readonly Animator _animator = animator;
        private RuntimeAnimatorController _controller = animator.runtimeAnimatorController;
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

        internal void FollowController(RuntimeAnimatorController controller) => _controller = controller;
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
