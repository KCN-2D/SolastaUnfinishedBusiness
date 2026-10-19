using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ReactionModalPatcher
{
    [HarmonyPatch(typeof(ReactionModal), nameof(ReactionModal.OnUpdateHook))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnUpdateHook_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ReactionModal __instance)
        {
            if (!__instance.Visible || __instance.Showing || __instance.Hiding)
            {
                return;
            }

            List<ReactionRequest> expired = null;

            foreach (var item in __instance.reactionItems)
            {
                if (!item || item.CurrentState != CharacterReactionItem.ReactionState.Pending ||
                    item.ReactionRequest is not ReactionRequestSelectTarget { Processed: false } request)
                {
                    continue;
                }

                // The native modal checks relevance only on user decisions/timeouts.
                // A target can change while another candidate remains selectable.
                item.CheckRelevance();
                if (item.CurrentState == CharacterReactionItem.ReactionState.Irrelevant)
                {
                    expired ??= [];
                    expired.Add(request);
                }
            }

            if (expired != null)
            {
                // Expiring an item only hides its buttons. Resolve the request through
                // the normal local/network command too, so its waiting group can finish.
                var commands = ServiceRepository.GetService<ICommandService>();
                foreach (var request in expired)
                {
                    if (!request.Processed)
                    {
                        commands?.ProcessReactionRequest(request, false, -1);
                    }
                }

                __instance.CheckPanelRelevance();
            }
        }
    }

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
