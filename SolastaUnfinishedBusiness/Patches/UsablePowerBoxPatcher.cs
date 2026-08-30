using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class UsablePowerBoxPatcher
{
    [HarmonyPatch(typeof(UsablePowerBox), nameof(UsablePowerBox.OnActivateCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnActivateCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(UsablePowerBox __instance)
        {
            if (!__instance.globalValid)
            {
                return true;
            }

            //PATCH: used by Power Bundles feature
            //if the activated power is a bundle, this tries to replace activation with sub-spell selector and
            //then activates bundled power according to selected subspell.
            //returns false and skips base method if it does
            return PowerBundle.PowerBoxActivated(__instance);
        }
    }

    [HarmonyPatch(typeof(UsablePowerBox), nameof(UsablePowerBox.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(UsablePowerBox __instance)
        {
            var powerDefinition = __instance.usablePower.PowerDefinition;
            var unavailable =
                ModifyPowerVisibility.ShouldKeepVisibleWhenUnavailable(powerDefinition) &&
                ValidatorsValidatePowerUse.IsPowerNotValid(__instance.activator, __instance.usablePower);

            //PATCH: sets current character as context for power tooltip, so it may update its properties based on user
            __instance.GuiTooltip.Context = __instance.activator;

            //PATCH: make reaction powers not active while keeping unavailable powers focusable for their tooltip
            if (powerDefinition.activationTime == ActivationTime.Reaction)
            {
                __instance.canvasGroup.interactable = false;
                __instance.RefreshLabel(false);
            }
            else
            {
                __instance.canvasGroup.interactable = true;
            }

            if (unavailable)
            {
                __instance.globalValid = false;
                __instance.RefreshLabel(false);
                __instance.image.material = __instance.unavailableMaterial;
                __instance.image.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            }

            UiTextHelpers.FitCardTitle(__instance.titleActiveLabel);
            UiTextHelpers.FitCardTitle(__instance.titleInactiveLabel);

            //PATCH: make power icons fit into box, instead of stretching
            var img = __instance.image;
            var aspect = img ? img.GetComponent<AspectRatioFitter>() : null;

            if (!aspect || !img || !img.sprite)
            {
                return;
            }

            var rect = img.sprite.rect;

            //Set aspect ratio to natural for the sprite, to remove stretching
            aspect.aspectRatio = rect.width / rect.height;
            //Set mode that would fill parent
            aspect.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        }
    }
}
