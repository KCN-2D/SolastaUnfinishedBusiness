using JetBrains.Annotations;
using System.Collections.Generic;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class UnityImageExtensions
{
    internal static void ClearAddressableSprite([NotNull] this Image imageComponent)
    {
        var sprite = imageComponent.sprite;

        if (!sprite)
        {
            return;
        }

        imageComponent.ReleaseAddressableSprite(sprite);
        imageComponent.sprite = null;
    }

    internal static Sprite LoadAddressableSprite([CanBeNull] this Component owner, [NotNull] string assetPath)
    {
        var sprite = Gui.LoadAssetSync<Sprite>(assetPath);

        owner.TrackAddressableSprite(sprite);

        return sprite;
    }

    internal static Sprite LoadAddressableSprite(
        [CanBeNull] this Component owner,
        [NotNull] AssetReferenceSprite spriteReference)
    {
        var sprite = Gui.LoadAssetSync<Sprite>(spriteReference);

        owner.TrackAddressableSprite(sprite);

        return sprite;
    }

    internal static void ReleaseAddressableSprite([CanBeNull] this Component owner, [CanBeNull] Sprite sprite)
    {
        if (!owner || !sprite || Sprites.IsCustomSprite(sprite))
        {
            return;
        }

        var tracker = owner.GetComponent<AddressableSpriteTracker>();

        if (tracker == null || !tracker.Release(sprite))
        {
            return;
        }

        Gui.ReleaseAddressableAsset(sprite);
    }

    internal static void SetAddressableSprite(
        [NotNull] this Image imageComponent,
        [CanBeNull] AssetReferenceSprite spriteReference,
        bool changeActiveStatus = false)
    {
        imageComponent.ClearAddressableSprite();

        if (spriteReference != null && spriteReference.RuntimeKeyIsValid())
        {
            if (changeActiveStatus)
            {
                imageComponent.gameObject.SetActive(true);
            }

            imageComponent.sprite = imageComponent.LoadAddressableSprite(spriteReference);
        }
        else if (changeActiveStatus)
        {
            imageComponent.gameObject.SetActive(false);
        }
    }

    internal static void TransferAddressableSprite(
        [CanBeNull] this Component owner,
        [CanBeNull] Component newOwner,
        [CanBeNull] Sprite sprite)
    {
        if (!owner || !newOwner || owner == newOwner || !sprite || Sprites.IsCustomSprite(sprite))
        {
            return;
        }

        var tracker = owner.GetComponent<AddressableSpriteTracker>();

        if (tracker == null || !tracker.Release(sprite))
        {
            return;
        }

        newOwner.TrackAddressableSprite(sprite);
    }

    internal static void SetupSprite(
        [NotNull] this Image imageComponent,
        [CanBeNull] AssetReferenceSprite spriteReference,
        bool changeActiveStatus = false)
    {
        imageComponent.SetAddressableSprite(spriteReference, changeActiveStatus);
    }

    private static void TrackAddressableSprite([CanBeNull] this Component owner, [CanBeNull] Sprite sprite)
    {
        if (!owner || !sprite || Sprites.IsCustomSprite(sprite))
        {
            return;
        }

        var tracker = owner.GetComponent<AddressableSpriteTracker>() ??
                      owner.gameObject.AddComponent<AddressableSpriteTracker>();

        tracker.Track(sprite);
    }
}

internal sealed class AddressableSpriteTracker : MonoBehaviour
{
    private readonly HashSet<Sprite> _sprites = [];

    internal void Track([NotNull] Sprite sprite)
    {
        _sprites.Add(sprite);
    }

    internal bool Release([NotNull] Sprite sprite)
    {
        return _sprites.Remove(sprite);
    }
}
