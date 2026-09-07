using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
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
            activePortrait ? PortraitKind.Active : PortraitKind.Standard);
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
            }
        }

        RequestPhoto(duplicate, state, kind);

        return true;
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

        ReleaseNativePhotos(character);
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
            if (state.TryGetOwner(out var previousOwner))
            {
                ReleaseNativePhotos(previousOwner);
            }

            DetachAll(state, character.Guid);
            state.DestroyOwnedTextures();
            States.Remove(character.Guid);
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
        var requestVersion = state.GetRequestVersion(kind);

        PortraitsContext.RequestCompletedPhoto(
            photoService,
            character,
            kind == PortraitKind.Active,
            texture => Complete(character.Guid, state, requestedRevision, requestVersion, texture, kind));
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
        int requestVersion,
        Texture texture,
        PortraitKind kind)
    {
        if (!States.TryGetValue(characterGuid, out var state) ||
            !ReferenceEquals(state, expectedState) ||
            state.Revision != revision || !state.IsCurrentRequest(kind, requestVersion))
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

        var snapshot = state.SetTexture(kind, texture, revision);

        if (!snapshot)
        {
            return;
        }

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
                    : binding.Kind == PortraitKind.Active && state.GetTexture(PortraitKind.Active)) ||
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

            AssignTexturePreservingAspect(image, snapshot);
        }
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
        internal int ActiveRequestVersion;
        internal Texture ActiveTexture;
        internal int ActiveTextureRevision = -1;
        internal string EquipmentSignature;
        internal bool InFlight;
        internal int RequestVersion;
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

        internal bool TryGetOwner(out RulesetCharacterSimulacrum owner)
        {
            return _owner.TryGetTarget(out owner) && owner != null;
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
                    if (value)
                    {
                        ActiveRequestVersion++;
                    }

                    break;
                default:
                    InFlight = value;
                    if (value)
                    {
                        RequestVersion++;
                    }

                    break;
            }
        }

        internal int GetRequestVersion(PortraitKind kind)
        {
            return kind == PortraitKind.Active ? ActiveRequestVersion : RequestVersion;
        }

        internal bool IsCurrentRequest(PortraitKind kind, int requestVersion)
        {
            return IsInFlight(kind) && GetRequestVersion(kind) == requestVersion;
        }

        internal Texture SetTexture(PortraitKind kind, Texture texture, int revision)
        {
            // The photo service owns a temporary render target. Its cache can release and reuse
            // that same object for another character, so only retain an independently owned copy.
            var snapshot = CloneTexture(texture);

            if (!snapshot)
            {
                return null;
            }

            switch (kind)
            {
                case PortraitKind.Active:
                    DestroyOwnedTexture(ref ActiveTexture, ref OwnsActiveTexture);
                    ActiveTexture = snapshot;
                    OwnsActiveTexture = true;
                    ActiveTextureRevision = revision;
                    break;
                default:
                    DestroyOwnedTexture(ref Texture, ref OwnsTexture);
                    Texture = snapshot;
                    OwnsTexture = true;
                    TextureRevision = revision;
                    break;
            }

            return snapshot;
        }

        internal void RetainTexturesForRefresh()
        {
            // Completed snapshots are already owned: keep them until their replacements arrive.
            // An active surface using the standard fallback needs a separate retained copy before
            // the standard snapshot can be replaced and destroyed.
            if (!ActiveTexture && Texture)
            {
                ActiveTexture = CloneTexture(Texture);
                OwnsActiveTexture = ActiveTexture != null;
            }

            TextureRevision = -1;
            ActiveTextureRevision = -1;
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
                name = "SimulacrumPortraitSnapshot"
            };

            var completed = false;

            try
            {
                if (!copy.Create())
                {
                    Main.Error("Unable to allocate a Simulacrum portrait snapshot.");
                    return null;
                }

                Graphics.Blit(source, copy);
                completed = true;
                return copy;
            }
            catch (Exception exception)
            {
                Main.Error(exception);
                return null;
            }
            finally
            {
                if (!completed)
                {
                    UnityEngine.Object.Destroy(copy);
                }
            }
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
