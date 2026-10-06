using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ReactionModalPatcher
{
    //TODO: Create a FeatureBuilder with Validators to create a generic check here
    [HarmonyPatch(typeof(ReactionModal), nameof(ReactionModal.ReactionTriggered))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactionTriggered_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ReactionModal __instance, ReactionRequest request)
        {
            // Native ReactionTriggered writes its full description after binding the new item.
            // Apply the interruption context after that final native write too.
            foreach (var item in __instance.reactionItems)
            {
                if (item && item.ReactionRequest == request)
                {
                    item.RefreshReactionDescription();
                    break;
                }
            }
        }

        [UsedImplicitly]
        public static bool Prefix(ReactionRequest request)
        {
            // wildshape heroes should not be able to cast spells
            var rulesetCharacter = request.Character.RulesetCharacter;

            if (!rulesetCharacter.IsSubstitute
                || request is not (ReactionRequestCastSpell or ReactionRequestCastFallPreventionSpell
                    or ReactionRequestCastImmunityToSpell))
            {
                return true;
            }

            ServiceRepository.GetService<IGameLocationActionService>().ProcessReactionRequest(request, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(ReactionModal), nameof(ReactionModal.OnReact))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnReact_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ReactionModal __instance, CharacterReactionItem item)
        {
            var request = item.ReactionRequest;
            var suboption = request.SubOptionsAvailability.Count > 1 ? item.GetSelectedSubItem() : -1;

            if (MetamagicContext.TrySelectReactionMetamagic(
                    request, __instance, suboption, () => RegisterReactionConfirmation(request.Character)))
            {
                __instance.ClearReactionTargetPreview();
                return false;
            }

            RegisterReactionConfirmation(request.Character);
            return true;
        }

        private static void RegisterReactionConfirmation(GameLocationCharacter caster)
        {
            //PATCH: register on acting character if SHIFT is pressed on reaction confirmations
            caster.RegisterShiftState();
        }
    }

    [HarmonyPatch(typeof(ReactionModal), nameof(ReactionModal.GaugeCoroutine))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GaugeCoroutine_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, ReactionModal __instance) =>
            MetamagicContext.PauseReactionTimer(values, __instance);
    }

    [HarmonyPatch(typeof(ReactionModal), nameof(ReactionModal.OnBeginHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginHide_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ReactionModal __instance) => __instance.ClearReactionTargetPreview();
    }

    [HarmonyPatch(typeof(ReactionModal), nameof(ReactionModal.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ReactionModal __instance) => MetamagicContext.CancelReactionSelection(__instance);
    }
}
