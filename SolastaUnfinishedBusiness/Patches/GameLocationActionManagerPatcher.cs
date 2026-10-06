using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using TA.AI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationActionManagerPatcher
{
    private static readonly ConditionalWeakTable<ReactionRequestGroup, Dictionary<ReactionRequest, int>>
        ReactionConfirmationOrder = new();

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ProcessReactionRequest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ProcessReactionRequest_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            GameLocationActionManager __instance,
            ReactionRequest reactionRequest,
            bool validated,
            out ReactionRequestGroup __state)
        {
            __state = null;
            if (!validated || reactionRequest.Processed || __instance.pendingReactionRequestGroups.Count == 0)
            {
                return;
            }

            var group = __instance.pendingReactionRequestGroups.Peek();
            if (group.Requests.Contains(reactionRequest))
            {
                __state = group;
            }
        }

        [UsedImplicitly]
        public static void Postfix(ReactionRequest reactionRequest, ReactionRequestGroup __state)
        {
            if (__state == null || !reactionRequest.Processed || !reactionRequest.Validated)
            {
                return;
            }

            // Native multiplayer confirmations arrive here through the ordered reaction RPC.
            // Record accepted requests, rather than a local UI clock or a character's last reaction.
            var order = ReactionConfirmationOrder.GetOrCreateValue(__state);
            if (!order.ContainsKey(reactionRequest))
            {
                order.Add(reactionRequest, order.Count);
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactToSpendSpellSlot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactToSpendSpellSlot_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: replace `SpendSpellSlot` reaction with custom one
            __instance.AddInterruptRequest(new ReactionRequestSpendSpellSlotExtended(reactionParams));

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactForOpportunityAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactForOpportunityAttack_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: replace `OpportunityAttack` reaction with warcaster one

            //replace only for player characters
            if (reactionParams.ActingCharacter.Side != Side.Ally)
            {
                return true;
            }

            __instance.AddInterruptRequest(new ReactionRequestWarcaster(reactionParams));

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactForReadiedAction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactForReadiedAction_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: mark this attack as not AoO, so Sentinel movement stop won't trigger
            reactionParams.AttackMode?.AddAttackTagAsNeeded(AttacksOfOpportunity.NotAoOTag);
            //PATCH: mark as a reaction, so Attack After Magic Effect won't check for attack validity, since it was already checked prior to triggering the reaction
            reactionParams.IsReactionEffect = true;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactToSpendPower))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactToSpendPower_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: replace `SpendPower` reaction for bundled powers or customized one for other powers
            if (reactionParams.RulesetEffect is not RulesetEffectPower powerEffect)
            {
                return true;
            }

            if (powerEffect.PowerDefinition.IsBundlePower())
            {
                __instance.AddInterruptRequest(new ReactionRequestSpendBundlePower(reactionParams)
                {
                    Resource = powerEffect.PowerDefinition.GetFirstSubFeatureOfType<ICustomReactionResource>()
                });
            }
            else
            {
                __instance.AddInterruptRequest(new ReactionRequestSpendPowerCustom(reactionParams));
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactToUsePower))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactToUsePower_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GameLocationActionManager __instance,
            CharacterActionParams reactionParams,
            string reactionName = "",
            GameLocationCharacter attacker = null)
        {
            //PATCH: replace `UsePower` reaction for customized one that allows better descriptions
            if (reactionParams.RulesetEffect is not RulesetEffectPower)
            {
                return true;
            }

            __instance.AddInterruptRequest(string.IsNullOrEmpty(reactionName)
                ? attacker == null
                    ? new ReactionRequestUsePowerCustom(reactionParams, (string)null)
                    : new ReactionRequestUsePowerCustom(reactionParams, attacker)
                : attacker == null
                    ? new ReactionRequestUsePowerCustom(reactionParams, reactionName)
                    : new ReactionRequestUsePowerCustom(reactionParams, reactionName, attacker));

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.CharacterDamageReceivedAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterDamageReceivedAsync_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(
            [NotNull] IEnumerator values,
            RulesetCharacter rulesetTarget,
            bool wasConscious,
            bool stillConscious,
            bool massiveDamage)
        {
            var characterGuid = SimulacrumBehavior.BeginDamageResolution(rulesetTarget);

            return TrackDamageResolution(
                values,
                rulesetTarget,
                wasConscious,
                stillConscious,
                massiveDamage,
                characterGuid);
        }

        private static IEnumerator TrackDamageResolution(
            IEnumerator values,
            RulesetCharacter rulesetTarget,
            bool wasConscious,
            bool stillConscious,
            bool massiveDamage,
            ulong characterGuid)
        {
            try
            {
                //PATCH: support for `DoNotTerminateWhileUnconscious`
                yield return RestrictEffectToNotTerminateWhileUnconscious.TerminateAllSpellsAndEffects(
                    values, rulesetTarget, wasConscious, stillConscious, massiveDamage);
            }
            finally
            {
                SimulacrumBehavior.EndDamageResolution(characterGuid);
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager),
        nameof(GameLocationActionManager.ExecuteReactionRequestGroupAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteReactionRequestGroupAsync_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(
            [NotNull] IEnumerator values,
            ReactionRequestGroup reactionRequestGroup)
        {
            if (ReactionConfirmationOrder.TryGetValue(reactionRequestGroup, out var order))
            {
                var originalOrder = new Dictionary<ReactionRequest, int>(reactionRequestGroup.Requests.Count);

                for (var i = 0; i < reactionRequestGroup.Requests.Count; i++)
                {
                    originalOrder[reactionRequestGroup.Requests[i]] = i;
                }

                reactionRequestGroup.Requests.Sort((a, b) =>
                {
                    var aOrder = order.TryGetValue(a, out var confirmedA) ? confirmedA : int.MaxValue;
                    var bOrder = order.TryGetValue(b, out var confirmedB) ? confirmedB : int.MaxValue;
                    var comparison = aOrder.CompareTo(bOrder);

                    return comparison != 0
                        ? comparison
                        : originalOrder[a].CompareTo(originalOrder[b]);
                });
            }

            try
            {
                while (values.MoveNext())
                {
                    yield return values.Current;
                }
            }
            finally
            {
                ReactionConfirmationOrder.Remove(reactionRequestGroup);
                (values as System.IDisposable)?.Dispose();
            }
        }
    }

    //PATCH: supports for unique request groups, except if an opportunity attack, otherwise battle crashes...
    // ...under advanced reaction scenarios when someone reacts in the middle of another reaction
    // i.e.: an attack before hit offers a reaction which forces an enemy to roll a save,
    // which an ally could change from success to failure, and only after that pop up a maneuver usage
    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.AddInterruptRequest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AddInterruptRequest_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, ReactionRequest reactionRequest)
        {
            // Apply the 2024 casting restrictions at the common queue boundary. This prevents blocked
            // reaction spells from opening a menu regardless of which request type created them.
            if (!MetamagicContext.CanQueueReactionSpell2024(reactionRequest.ReactionParams) ||
                !SpellSlotCastingLimit2024Context.CanQueueReactionSpell(reactionRequest.ReactionParams))
            {
                reactionRequest.ReactionParams.RulesetEffect?.Terminate(false);

                return false;
            }

            AddInterruptRequest(__instance, reactionRequest);

            return false;
        }

        // vanilla code except for BEGIN/END patch block
        private static void AddInterruptRequest(GameLocationActionManager __instance, ReactionRequest reactionRequest)
        {
            reactionRequest.AssignGuid(__instance.currentReactionGuid++);

            var pendingReactionRequestGroups = __instance.pendingReactionRequestGroups;

            if (pendingReactionRequestGroups.Count > 0 &&
                pendingReactionRequestGroups.Peek().ReactionDefinitionName == reactionRequest.DefinitionName)
            {
                var pendingReactionRequestGroup = pendingReactionRequestGroups.Peek();
                var isSameCharacter = false;

                foreach (var request in pendingReactionRequestGroup.Requests)
                {
                    if (request.Character != reactionRequest.Character)
                    {
                        continue;
                    }

                    isSameCharacter = true;
                    break;
                }

                if (!isSameCharacter)
                {
                    //BEGIN PATCH
                    // add a new unique request group to avoid reactions grouping and, enforce the desired sequence
                    // if not an opportunity attack, ready attack, cast reaction, or ready cast
                    if (reactionRequest.ReactionParams.ActionDefinition.Id is not (
                        ActionDefinitions.Id.AttackOpportunity or
                        ActionDefinitions.Id.AttackReadied or
                        ActionDefinitions.Id.CastReaction or
                        ActionDefinitions.Id.CastReadied))
                    {
                        pendingReactionRequestGroup = new ReactionRequestGroup(reactionRequest.DefinitionName);
                        pendingReactionRequestGroups.Push(pendingReactionRequestGroup);
                    }
                    //END PATCH

                    pendingReactionRequestGroup.Requests.Add(reactionRequest);
                }
            }
            else
            {
                var pendingReactionRequestGroup = new ReactionRequestGroup(reactionRequest.DefinitionName);

                pendingReactionRequestGroups.Push(pendingReactionRequestGroup);
                pendingReactionRequestGroup.Requests.Add(reactionRequest);
            }

            if (!reactionRequest.Automated)
            {
                __instance.ReactionTriggered?.Invoke(reactionRequest);
            }
            else
            {
                __instance.unstoppableCoroutines.Add(
                    __instance.DelayedProcessAutomatedReactionRequest(reactionRequest));
            }
        }
    }
}
