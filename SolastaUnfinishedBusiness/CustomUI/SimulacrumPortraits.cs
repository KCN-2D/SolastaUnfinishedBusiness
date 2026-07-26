using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Spells;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class SimulacrumPortraits
{
    private static readonly Dictionary<ulong, PortraitState> States = [];
    private static readonly ConditionalWeakTable<RawImage, PortraitBinding> Bindings = new();

    internal static bool TryAssign(
        GuiCharacter guiCharacter,
        RawImage image,
        bool activePortrait = false)
    {
        if (!image ||
            guiCharacter?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
        {
            return false;
        }

        return TryAssign(duplicate, image, activePortrait);
    }

    internal static bool TryAssign(
        RulesetCharacterSimulacrum duplicate,
        RawImage image,
        bool activePortrait = false)
    {
        return TryAssign(
            duplicate,
            image,
            GetPortraitKind(image, activePortrait));
    }

    private static bool TryAssign(
        RulesetCharacterSimulacrum duplicate,
        RawImage image,
        PortraitKind kind)
    {
        if (duplicate == null || !image)
        {
            return false;
        }

        Detach(image);

        var state = GetOrCreateState(duplicate);

        Bindings.Add(
            image,
            new PortraitBinding(image, duplicate.Guid, state.Revision, kind));
        state.Images.Add(new WeakReference<RawImage>(image));
        var texture = state.GetTexture(kind);

        if (texture)
        {
            AssignTexturePreservingAspect(image, texture);
            RecordPortrait(
                duplicate,
                state,
                $"{kind.ToString().ToLowerInvariant()}-cached",
                image,
                texture);
        }
        else
        {
            var standardTexture = state.GetTexture(PortraitKind.Standard);
            var usesStandardFallback = kind == PortraitKind.Active && standardTexture;
            var fallbackTexture = usesStandardFallback
                ? standardTexture
                : GetFallbackTexture();

            if (fallbackTexture)
            {
                AssignTexturePreservingAspect(image, fallbackTexture);
                RecordPortrait(
                    duplicate,
                    state,
                    usesStandardFallback
                        ? "active-standard-fallback"
                        : "fallback",
                    image,
                    fallbackTexture);
            }
        }

        RequestPhoto(duplicate, state, kind);

        return true;
    }

    private static PortraitKind GetPortraitKind(RawImage image, bool activePortrait)
    {
        if (activePortrait)
        {
            return PortraitKind.Active;
        }

        return PortraitKind.Standard;
    }

    internal static void Release(RawImage image)
    {
        Detach(image);
    }

    internal static void MarkDirty(
        RulesetCharacterSimulacrum character,
        int visualRevision,
        string equipmentSignature)
    {
        if (character == null)
        {
            return;
        }

        var state = GetOrCreateState(character);

        if (state.VisualRevision == visualRevision &&
            string.Equals(
                state.EquipmentSignature,
                equipmentSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        PrepareRefresh(
            character,
            state,
            visualRevision,
            equipmentSignature);
        ReleaseNativePhotos(character);
    }

    internal static void Refresh(
        RulesetCharacterSimulacrum character,
        int visualRevision,
        string equipmentSignature)
    {
        if (character == null)
        {
            return;
        }

        var state = GetOrCreateState(character);

        if (state.VisualRevision != visualRevision ||
            !string.Equals(
                state.EquipmentSignature,
                equipmentSignature,
                StringComparison.Ordinal))
        {
            PrepareRefresh(
                character,
                state,
                visualRevision,
                equipmentSignature);
        }

        ReleaseNativePhotos(character);
        RequestPhoto(character, state, PortraitKind.Standard);
        RequestPhoto(character, state, PortraitKind.Active);
    }

    internal static void Invalidate(RulesetCharacterSimulacrum character)
    {
        if (character == null)
        {
            return;
        }

        var state = GetOrCreateState(character);

        SimulacrumBehavior.TryGetVisualRefreshState(
            character,
            out var requestedRevision,
            out _,
            out _,
            out var equipmentSignature);
        PrepareRefresh(
            character,
            state,
            requestedRevision,
            equipmentSignature);
        ReleaseNativePhotos(character);
        RequestPhoto(character, state, PortraitKind.Standard);
        RequestPhoto(character, state, PortraitKind.Active);
    }

    private static void PrepareRefresh(
        RulesetCharacterSimulacrum character,
        PortraitState state,
        int visualRevision,
        string equipmentSignature)
    {
        var hasCurrentTexture =
            state.HasCurrentTexture(PortraitKind.Standard) ||
            state.HasCurrentTexture(PortraitKind.Active);

        state.Revision++;

        if (hasCurrentTexture)
        {
            state.RetainTexturesForRefresh();
        }

        state.VisualRevision = visualRevision;
        state.EquipmentSignature = equipmentSignature;
        state.InFlight = false;
        state.ActiveInFlight = false;
        state.WaitingForGraphics = false;
        RebindLiveImages(state, character.Guid);
        AssignRetainedTexturesToLiveImages(state, character);
        RecordPortrait(character, state, "visual-dirty", null);
    }

    private static void ReleaseNativePhotos(RulesetCharacterSimulacrum character)
    {
        if (ServiceRepository.GetService<IGraphicsCharacterPhotoService>() is
            { } photoService)
        {
            photoService.ReleaseCharacterPhoto(character);
            photoService.ReleaseActiveCharacterPhoto(character);
        }
    }

    private static void AssignRetainedTexturesToLiveImages(
        PortraitState state,
        RulesetCharacterSimulacrum character)
    {
        var fallbackTexture = GetFallbackTexture();

        for (var index = state.Images.Count - 1; index >= 0; index--)
        {
            if (!state.Images[index].TryGetTarget(out var image) ||
                !image ||
                !Bindings.TryGetValue(image, out var binding) ||
                binding.CharacterGuid != character.Guid)
            {
                state.Images.RemoveAt(index);

                continue;
            }

            var texture = state.GetTexture(binding.Kind) ??
                          (binding.Kind == PortraitKind.Active
                              ? state.GetTexture(PortraitKind.Standard)
                              : null) ??
                          fallbackTexture;

            if (texture)
            {
                AssignTexturePreservingAspect(image, texture);
                RecordPortrait(
                    character,
                    state,
                    state.HasCurrentTexture(binding.Kind)
                        ? "invalidate-current"
                        : "invalidate-retained",
                    image,
                    texture);
            }
            else
            {
                image.texture = null;
                binding.SetLastAssignedTexture(null);
            }
        }
    }

    internal static void Remove(RulesetCharacterSimulacrum character)
    {
        if (character == null)
        {
            return;
        }

        if (States.TryGetValue(character.Guid, out var state) &&
            state.IsOwnedBy(character))
        {
            DetachAll(state, character.Guid);
            state.DestroyOwnedTextures();
            States.Remove(character.Guid);
        }

        if (ServiceRepository.GetService<IGraphicsCharacterPhotoService>() is
            { } photoService)
        {
            photoService.ReleaseCharacterPhoto(character);
            photoService.ReleaseActiveCharacterPhoto(character);
        }
    }

    private static PortraitState GetOrCreateState(RulesetCharacterSimulacrum character)
    {
        if (States.TryGetValue(character.Guid, out var state))
        {
            if (state.IsOwnedBy(character))
            {
                return state;
            }

            // Entity GUIDs can be reused when another save is loaded in the same process. Do not
            // let the previous Simulacrum's cached render or an old async completion bind to the
            // replacement character.
            DetachAll(state, character.Guid);
            state.DestroyOwnedTextures();
            States.Remove(character.Guid);
            SimulacrumDiagnostics.RecordPortrait(
                character,
                "state-reset-guid-reuse",
                null);
        }

        state = new PortraitState(character);
        States.Add(character.Guid, state);

        return state;
    }

    private static Texture GetFallbackTexture()
    {
        var spriteReference = SpellBuilders.Simulacrum?.GuiPresentation?.SpriteReference;

        return string.IsNullOrEmpty(spriteReference?.AssetGUID)
            ? null
            : Sprites.GetSpriteByGuid(spriteReference.AssetGUID)?.texture;
    }

    private static void RequestPhoto(
        RulesetCharacterSimulacrum character,
        PortraitState state,
        PortraitKind kind)
    {
        if (character.LifecycleState != SimulacrumLifecycleState.Ready ||
            (state.VisualRevision > 0 &&
             !SimulacrumBehavior.IsVisualRevisionReady(
                 character,
                 state.VisualRevision,
                 state.EquipmentSignature)) ||
            state.IsInFlight(kind) ||
            state.HasCurrentTexture(kind) ||
            !HasLiveBinding(state, character.Guid, kind) ||
            ServiceRepository.GetService<IGraphicsCharacterPhotoService>() is not
                { } photoService)
        {
            return;
        }

        if (!HasBoundGraphics(character))
        {
            ScheduleWhenGraphicsReady(character, state);

            return;
        }

        state.SetInFlight(kind, true);

        var requestedRevision = state.Revision;
        RecordPortrait(
            character,
            state,
            $"{kind.ToString().ToLowerInvariant()}-request",
            null);

        if (kind == PortraitKind.Active)
        {
            photoService.RequestActiveCharacterPhoto(
                character,
                texture => Complete(
                    character.Guid,
                    state,
                    requestedRevision,
                    texture,
                    kind));

            return;
        }

        photoService.RequestCharacterPhoto(
            character,
            texture => Complete(
                character.Guid,
                state,
                requestedRevision,
                texture,
                kind),
            false,
            256,
            384,
            default);
    }

    private static void ScheduleWhenGraphicsReady(
        RulesetCharacterSimulacrum character,
        PortraitState state)
    {
        if (state.WaitingForGraphics || !Gui.GameLocation)
        {
            return;
        }

        state.WaitingForGraphics = true;
        Gui.GameLocation.StartCoroutine(
            WaitForGraphics(character.Guid, state, state.Revision));
    }

    private static IEnumerator WaitForGraphics(
        ulong characterGuid,
        PortraitState expectedState,
        int revision)
    {
        var deadline = Time.realtimeSinceStartup + 30f;

        while (Time.realtimeSinceStartup < deadline &&
               States.TryGetValue(characterGuid, out var state) &&
               ReferenceEquals(state, expectedState) &&
               expectedState.Revision == revision &&
               EffectHelpers.GetCharacterByGuid(characterGuid) is
                   RulesetCharacterSimulacrum character &&
               expectedState.IsOwnedBy(character) &&
               character.LifecycleState == SimulacrumLifecycleState.Ready)
        {
            if (HasBoundGraphics(character))
            {
                expectedState.WaitingForGraphics = false;
                RequestPhoto(character, expectedState, PortraitKind.Standard);
                RequestPhoto(character, expectedState, PortraitKind.Active);

                yield break;
            }

            yield return null;
        }

        if (States.TryGetValue(characterGuid, out var current) &&
            ReferenceEquals(current, expectedState) &&
            expectedState.Revision == revision)
        {
            expectedState.WaitingForGraphics = false;
        }
    }

    private static bool HasBoundGraphics(RulesetCharacterSimulacrum character)
    {
        var locationCharacter = GameLocationCharacter.GetFromActor(character);
        var entityFactory = ServiceRepository.GetService<IWorldLocationEntityFactoryService>();

        return locationCharacter != null &&
               entityFactory != null &&
               entityFactory.TryFindWorldCharacter(locationCharacter, out var worldCharacter) &&
               worldCharacter?.GraphicsCharacter != null;
    }

    private static void Complete(
        ulong characterGuid,
        PortraitState expectedState,
        int revision,
        Texture texture,
        PortraitKind kind)
    {
        if (!States.TryGetValue(characterGuid, out var state) ||
            !ReferenceEquals(state, expectedState) ||
            state.Revision != revision)
        {
            return;
        }

        state.SetInFlight(kind, false);

        if (!texture)
        {
            return;
        }

        var character = EffectHelpers.GetCharacterByGuid(characterGuid) as
            RulesetCharacterSimulacrum;

        if (!state.IsOwnedBy(character))
        {
            return;
        }

        state.SetTexture(kind, texture, revision);

        var assignedImages = new HashSet<RawImage>();

        for (var index = state.Images.Count - 1; index >= 0; index--)
        {
            if (!state.Images[index].TryGetTarget(out var image) ||
                !image ||
                !Bindings.TryGetValue(image, out var binding) ||
                binding.CharacterGuid != characterGuid)
            {
                state.Images.RemoveAt(index);

                continue;
            }

            if (binding.Revision != revision ||
                (kind == PortraitKind.Active
                    ? binding.Kind != PortraitKind.Active
                    : binding.Kind == PortraitKind.Active) ||
                !assignedImages.Add(image))
            {
                continue;
            }

            // A pooled RawImage may already have been rebound by native UI code. Never replace a
            // texture that was not assigned by this binding.
            if (binding.LastAssignedTexture && image.texture != binding.LastAssignedTexture)
            {
                // Native UI already rebound this pooled RawImage. Preserve its
                // new texture, but always restore the layout and fitter state
                // changed by the previous Simulacrum binding.
                binding.Restore(image);
                Bindings.Remove(image);
                state.Images.RemoveAt(index);
                continue;
            }

            AssignTexturePreservingAspect(image, texture);
            binding.SetLastAssignedTexture(texture);
            RecordPortrait(
                character,
                state,
                $"{kind.ToString().ToLowerInvariant()}-complete",
                image,
                texture);
        }
    }

    private static void RecordPortrait(
        RulesetCharacterSimulacrum character,
        PortraitState state,
        string stage,
        RawImage image,
        Texture texture = null)
    {
        SimulacrumDiagnostics.RecordPortrait(
            character,
            stage,
            image,
            texture,
            state?.VisualRevision ?? -1,
            state?.Revision ?? -1,
            state?.EquipmentSignature);
    }

    private static bool HasLiveBinding(
        PortraitState state,
        ulong characterGuid,
        PortraitKind kind)
    {
        for (var index = state.Images.Count - 1; index >= 0; index--)
        {
            if (!state.Images[index].TryGetTarget(out var image) || !image)
            {
                state.Images.RemoveAt(index);

                continue;
            }

            if (Bindings.TryGetValue(image, out var binding) &&
                binding.CharacterGuid == characterGuid &&
                binding.Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    private static void RebindLiveImages(PortraitState state, ulong characterGuid)
    {
        var liveImages = new HashSet<RawImage>();

        for (var index = state.Images.Count - 1; index >= 0; index--)
        {
            if (!state.Images[index].TryGetTarget(out var image) ||
                !image ||
                !liveImages.Add(image))
            {
                state.Images.RemoveAt(index);

                continue;
            }

            if (Bindings.TryGetValue(image, out var binding) &&
                binding.CharacterGuid == characterGuid)
            {
                binding.Revision = state.Revision;
                continue;
            }

            Detach(image);
            Bindings.Add(
                image,
                new PortraitBinding(
                    image,
                    characterGuid,
                    state.Revision,
                    binding?.Kind ?? PortraitKind.Standard));
        }
    }

    private static void Detach(RawImage image)
    {
        if (!image)
        {
            return;
        }

        if (!Bindings.TryGetValue(image, out var binding))
        {
            return;
        }

        binding.Restore(image);
        Bindings.Remove(image);

        if (!States.TryGetValue(binding.CharacterGuid, out var state))
        {
            return;
        }

        for (var index = state.Images.Count - 1; index >= 0; index--)
        {
            if (!state.Images[index].TryGetTarget(out var trackedImage) ||
                !trackedImage ||
                ReferenceEquals(trackedImage, image))
            {
                state.Images.RemoveAt(index);
            }
        }
    }

    private static void DetachAll(PortraitState state, ulong characterGuid)
    {
        foreach (var reference in state.Images)
        {
            if (reference.TryGetTarget(out var image) &&
                image &&
                Bindings.TryGetValue(image, out var binding) &&
                binding.CharacterGuid == characterGuid)
            {
                binding.Restore(image);
                Bindings.Remove(image);
            }
        }

        state.Images.Clear();
    }

    private static void AssignTexturePreservingAspect(RawImage image, Texture texture)
    {
        if (!image || !texture)
        {
            return;
        }

        image.texture = texture;

        var surface = image.rectTransform.rect;

        if (surface.width > 0f &&
            surface.height > 0f &&
            texture.width > 0 &&
            texture.height > 0)
        {
            var textureAspect = (float)texture.width / texture.height;
            var surfaceAspect = surface.width / surface.height;

            if (surfaceAspect > textureAspect)
            {
                var visibleHeight = textureAspect / surfaceAspect;

                image.uvRect = new Rect(
                    0f,
                    (1f - visibleHeight) * 0.5f,
                    1f,
                    visibleHeight);
            }
            else
            {
                var visibleWidth = surfaceAspect / textureAspect;

                image.uvRect = new Rect(
                    (1f - visibleWidth) * 0.5f,
                    0f,
                    visibleWidth,
                    1f);
            }
        }
        else
        {
            image.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        if (Bindings.TryGetValue(image, out var binding))
        {
            binding.SetLastAssignedTexture(texture);
        }
    }

    private enum PortraitKind
    {
        Standard,
        Active
    }

    private sealed class PortraitState
    {
        private readonly WeakReference<RulesetCharacterSimulacrum> _owner;

        internal readonly List<WeakReference<RawImage>> Images = [];
        internal bool ActiveInFlight;
        internal Texture ActiveTexture;
        internal int ActiveTextureRevision = -1;
        internal string EquipmentSignature;
        internal bool InFlight;
        internal bool OwnsActiveTexture;
        internal bool OwnsTexture;
        internal bool WaitingForGraphics;
        internal int Revision;
        internal Texture Texture;
        internal int TextureRevision = -1;
        internal int VisualRevision = -1;

        internal PortraitState(RulesetCharacterSimulacrum owner)
        {
            _owner = new WeakReference<RulesetCharacterSimulacrum>(owner);
        }

        internal bool IsOwnedBy(RulesetCharacterSimulacrum character)
        {
            return character != null &&
                   _owner.TryGetTarget(out var owner) &&
                   ReferenceEquals(owner, character);
        }

        internal bool IsInFlight(PortraitKind kind)
        {
            return kind switch
            {
                PortraitKind.Active => ActiveInFlight,
                _ => InFlight
            };
        }

        internal Texture GetTexture(PortraitKind kind)
        {
            return kind switch
            {
                PortraitKind.Active => ActiveTexture,
                _ => Texture
            };
        }

        internal bool HasCurrentTexture(PortraitKind kind)
        {
            return kind switch
            {
                PortraitKind.Active => ActiveTexture && ActiveTextureRevision == Revision,
                _ => Texture && TextureRevision == Revision
            };
        }

        internal void SetInFlight(PortraitKind kind, bool value)
        {
            switch (kind)
            {
                case PortraitKind.Active:
                    ActiveInFlight = value;
                    break;
                default:
                    InFlight = value;
                    break;
            }
        }

        internal void SetTexture(PortraitKind kind, Texture texture, int revision)
        {
            switch (kind)
            {
                case PortraitKind.Active:
                    DestroyOwnedTexture(ref ActiveTexture, ref OwnsActiveTexture);
                    ActiveTexture = texture;
                    ActiveTextureRevision = revision;
                    break;
                default:
                    DestroyOwnedTexture(ref Texture, ref OwnsTexture);
                    Texture = texture;
                    TextureRevision = revision;
                    break;
            }
        }

        internal void RetainTexturesForRefresh()
        {
            var standardSource = Texture;
            var activeSource = ActiveTexture ? ActiveTexture : standardSource;
            var retainedStandard = CloneTexture(standardSource);
            var retainedActive = CloneTexture(activeSource);

            DestroyOwnedTextures();

            Texture = retainedStandard;
            TextureRevision = -1;
            OwnsTexture = retainedStandard != null;
            ActiveTexture = retainedActive;
            ActiveTextureRevision = -1;
            OwnsActiveTexture = retainedActive != null;
        }

        internal void DestroyOwnedTextures()
        {
            DestroyOwnedTexture(ref Texture, ref OwnsTexture);
            DestroyOwnedTexture(ref ActiveTexture, ref OwnsActiveTexture);
        }

        private static RenderTexture CloneTexture(Texture source)
        {
            if (!source || source.width <= 0 || source.height <= 0)
            {
                return null;
            }

            var copy = new RenderTexture(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32)
            {
                name = "SimulacrumPortraitRetained"
            };

            copy.Create();
            Graphics.Blit(source, copy);

            return copy;
        }

        private static void DestroyOwnedTexture(
            ref Texture texture,
            ref bool owned)
        {
            if (owned && texture)
            {
                UnityEngine.Object.Destroy(texture);
            }

            if (owned)
            {
                texture = null;
            }

            owned = false;
        }
    }

    private sealed class PortraitBinding
    {
        private readonly Texture _originalTexture;
        private readonly Rect _originalUvRect;

        internal PortraitBinding(
            RawImage image,
            ulong characterGuid,
            int revision,
            PortraitKind kind)
        {
            CharacterGuid = characterGuid;
            Revision = revision;
            Kind = kind;
            _originalTexture = image.texture;
            _originalUvRect = image.uvRect;
        }

        internal ulong CharacterGuid { get; }
        internal PortraitKind Kind { get; }
        internal Texture LastAssignedTexture { get; private set; }
        internal int Revision { get; set; }

        internal void SetLastAssignedTexture(Texture texture)
        {
            LastAssignedTexture = texture;
        }

        internal void Restore(RawImage image)
        {
            if (!image)
            {
                return;
            }

            if (!LastAssignedTexture || image.texture == LastAssignedTexture)
            {
                image.texture = _originalTexture;
            }

            // Native portrait requests can replace the pooled texture before this async binding
            // notices. The UV crop still belongs to the Simulacrum in that case and must always
            // be restored, otherwise the newly loaded hero is rendered with the old crop.
            image.uvRect = _originalUvRect;
        }
    }
}
