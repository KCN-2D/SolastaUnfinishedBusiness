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
