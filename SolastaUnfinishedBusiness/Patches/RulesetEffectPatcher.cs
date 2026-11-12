using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetEffectPatcher
{
    [HarmonyPatch(typeof(RulesetEffect), nameof(RulesetEffect.Terminate))]
    [UsedImplicitly]
    public static class Terminate_Patch
    {
        private static void UnregisterIfRegistered(RulesetEntity entity)
        {
            // UI previews are created with delayed registration and have no entity ID.
            if (entity.Guid != 0) entity.Unregister();
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.Method(typeof(RulesetEntity), nameof(RulesetEntity.Unregister)),
                "RulesetEffect.Terminate", new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(Terminate_Patch), nameof(UnregisterIfRegistered))));
    }

    [HarmonyPatch(typeof(RulesetEffect), nameof(RulesetEffect.TrackCondition))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TrackCondition_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetEffect __instance, RulesetCharacter source, RulesetActor target, RulesetCondition condition)
        {
            foreach (var handler in __instance.GetSourceDefinitionSafe()
                         .GetAllSubFeaturesOfType<IOnEffectConditionTracked>())
            {
                handler.OnConditionTracked(__instance, source, target, condition);
            }
        }
    }

    [HarmonyPatch(typeof(RulesetEffect), nameof(RulesetEffect.RemainingRounds), MethodType.Setter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    public static class RemainingRounds_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetEffect __instance, int value)
        {
            // The owning power and its suspended condition must keep the same remaining duration.
            return value >= __instance.RemainingRounds || !MovementSuspensionContext.IsDurationPaused(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetEffect), nameof(RulesetEffect.ConditionRemoved))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    public static class ConditionRemoved_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // Native termination checks for other targets with the same condition name.
            // Active and suspended states must remain one spell for a twinned/multi-target effect.
            return instructions.ReplaceCalls(
                AccessTools.PropertyGetter(typeof(BaseDefinition), nameof(BaseDefinition.Name)),
                "RulesetEffect.ConditionRemoved",
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(MovementSuspensionContext),
                        nameof(MovementSuspensionContext.GetTrackingConditionName))));
        }
    }

    //PATCH: supports Oath of Ancients / Oath of Dread level 20 powers
    [HarmonyPatch(typeof(RulesetEffect), nameof(RulesetEffect.ConditionSaveRerollRequested))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    public static class ConditionSaveRerollRequested_Patch
    {
        [UsedImplicitly]
        private static void Prefix(RulesetEffect __instance, RulesetCondition __1,
            ref bool removeOnSuccess, out bool __state)
        {
            __state = __instance.TrackedConditionGuids.Contains(__1.Guid) &&
                      __1.ConditionDefinition.HasSubFeatureOfType<IConditionSavingThrowResolved>();
            if (__state) removeOnSuccess = false;
        }

        [UsedImplicitly]
        private static void Postfix(RulesetEffect __instance, RulesetActor __0,
            RulesetCondition __1, bool success, bool __state)
        {
            if (!__state) return;
            foreach (var handler in __1.ConditionDefinition
                         .GetAllSubFeaturesOfType<IConditionSavingThrowResolved>())
            {
                handler.OnConditionSavingThrowResolved(__instance, __0, __1, success);
            }
        }

        private static bool TryGetCasterAndEffectForms(
            RulesetEffect __instance,
            out RulesetCharacter rulesetCharacter,
            out List<EffectForm> effectForms)
        {
            switch (__instance)
            {
                case RulesetEffectSpell rulesetEffectSpell:
                {
                    rulesetCharacter = rulesetEffectSpell.Caster;
                    effectForms = rulesetEffectSpell.EffectDescription.EffectForms;

                    return true;
                }
                case RulesetEffectPower rulesetEffectPower:
                {
                    rulesetCharacter = rulesetEffectPower.User;
                    effectForms = rulesetEffectPower.EffectDescription.EffectForms;

                    return true;
                }
                default:
                    rulesetCharacter = null;
                    effectForms = null;

                    return false;
            }
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var rollSavingThrowMethod = typeof(RulesetActor).GetMethod("RollSavingThrow");
            var myRollSavingThrowMethod =
                typeof(ConditionSaveRerollRequested_Patch).GetMethod("RollSavingThrow");
            //PATCH: make ISpellCastingAffinityProvider from dynamic item properties apply to repertoires
            return instructions
                .ReplaceCalls(AccessTools.Method(typeof(RulesetActor), nameof(RulesetActor.IsAutomaticallyFailingSavingThrow)),
                    1, "RulesetEffect.ConditionSaveRerollRequested.IsAutomaticallyFailingSavingThrow",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MetamagicContext),
                        nameof(MetamagicContext.IsAutomaticallyFailingSpellSavingThrow))))
                .ReplaceCalls(rollSavingThrowMethod,
                    "RulesetEffect.ConditionSaveRerollRequested",
                    new CodeInstruction(OpCodes.Ldarg, 0),
                    new CodeInstruction(OpCodes.Call, myRollSavingThrowMethod));
        }

        [UsedImplicitly]
        public static void RollSavingThrow(
            RulesetCharacter __instance,
            int saveBonus,
            string abilityScoreName,
            BaseDefinition sourceDefinition,
            List<RuleDefinitions.TrendInfo> modifierTrends,
            List<RuleDefinitions.TrendInfo> advantageTrends,
            int rollModifier,
            int saveDC,
            bool hasHitVisual,
            ref RuleDefinitions.RollOutcome outcome,
            ref int outcomeDelta,
            RulesetEffect rulesetEffect)
        {
            if (!TryGetCasterAndEffectForms(rulesetEffect, out var caster, out var effectForms))
            {
                __instance.RollSavingThrow(
                    saveBonus, abilityScoreName, sourceDefinition, modifierTrends, advantageTrends,
                    rollModifier, saveDC, hasHitVisual, out outcome, out outcomeDelta);

                return;
            }

            MetamagicContext.ApplyHeightenedSavingThrow(rulesetEffect, __instance, advantageTrends);
            __instance.MyRollSavingThrow(
                caster,
                saveBonus,
                abilityScoreName,
                sourceDefinition,
                modifierTrends,
                advantageTrends,
                rollModifier,
                saveDC,
                hasHitVisual,
                ref outcome,
                ref outcomeDelta,
                effectForms);
            MetamagicContext.CompleteHeightenedSavingThrow(MetamagicContext.GetOriginatingSpell(rulesetEffect), __instance);
        }
    }

    [HarmonyPatch(typeof(RulesetEffect), nameof(RulesetEffect.TryRollSavingThrow))]
    [UsedImplicitly]
    public static class TryRollSavingThrow_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(RulesetEffect __instance, RulesetActor target,
            ref RuleDefinitions.RollOutcome saveOutcome, ref int saveOutcomeDelta,
            ref bool __result, out bool __state)
        {
            __state = false;
            foreach (var handler in __instance.GetSourceDefinitionSafe()
                         ?.GetAllSubFeaturesOfType<IAutomaticSavingThrowOutcome>() ?? [])
            {
                if (!handler.TryGetAutomaticOutcome(__instance, target, out var outcome)) continue;
                RulesetActorExtensions.RecordAutomaticSavingThrow(
                    __instance.EffectDescription.SavingThrowAbility, __instance.SaveDC);
                saveOutcome = outcome;
                saveOutcomeDelta = 0;
                __result = true;
                __state = true;
                return false;
            }
            return true;
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.ReplaceCalls(
                AccessTools.PropertyGetter(typeof(RulesetEffect), nameof(RulesetEffect.MetamagicOption)),
                1, "RulesetEffect.TryRollSavingThrow",
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Ldarg, 5),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MetamagicContext),
                    nameof(MetamagicContext.GetEffectSavingThrowMetamagic))));
        }

        [UsedImplicitly]
        private static void Postfix(RulesetEffect __instance, RulesetCharacter caster, RulesetActor target,
            List<EffectForm> actualEffectForms, bool __result, bool __state)
        {
            MetamagicContext.CompleteNativeHeightenedSavingThrow(__instance, caster, target, actualEffectForms, __result && !__state);
            if (__result && caster != null && target != caster &&
                GameLocationCharacter.GetFromActor(caster) is { } source &&
                GameLocationCharacter.GetFromActor(target) is { } defender && source.Side != defender.Side)
            {
                BarbarianRage2024Context.Renew(caster);
            }
        }
    }
}
