using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionUsePowerPatcher
{
    private static bool IgnoreInterruptionProcessForPowerFunction(CharacterActionUsePower __instance)
    {
        var isPowerFunction = __instance.ActionParams.RulesetEffect.Name.Contains("PowerFunction");

        if (isPowerFunction && Main.Settings.KeepInvisibilityWhenUsingItems)
        {
            return false;
        }

        var power = __instance.ActionParams.RulesetEffect.GetSourceDefinitionSafe();

        if (!power)
        {
            return true;
        }

        return !power.HasSubFeatureOfType<IIgnoreInvisibilityInterruptionCheck>();
    }

    [HarmonyPatch(typeof(CharacterActionUsePower), nameof(CharacterActionUsePower.CheckInterruptionBefore))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckInterruptionBefore_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] CharacterActionUsePower __instance)
        {
            //PATCH: ignores interruptions processing for certain powers so they won't interrupt invisibility
            return IgnoreInterruptionProcessForPowerFunction(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterActionUsePower), nameof(CharacterActionUsePower.CheckInterruptionAfter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckInterruptionAfter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] CharacterActionUsePower __instance)
        {
            //PATCH: ignores interruptions processing for certain powers so they won't interrupt invisibility
            return IgnoreInterruptionProcessForPowerFunction(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterActionUsePower), nameof(CharacterActionUsePower.GetAdvancementData))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetAdvancementData_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] CharacterActionUsePower __instance)
        {
            //PATCH: Calculate advancement data for `RulesetEffectPowerWithAdvancement`
            return RulesetEffectPowerWithAdvancement.GetAdvancementData(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterActionUsePower), nameof(CharacterActionUsePower.HandleEffectUniqueness))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class HandleEffectUniqueness_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] CharacterActionUsePower __instance)
        {
            //PATCH: terminates all matching spells and powers of same group
            ForceGlobalUniqueEffects.TerminateMatchingUniqueEffect(
                __instance.ActingCharacter.RulesetCharacter, __instance.actionParams.RulesetEffect);

            //PATCH: Support for limited power effect instances
            //terminates earliest power effect instances of same limit, if limit reached
            //used to limit Inventor's infusions
            ForceGlobalUniqueEffects.EnforceLimitedInstancePower(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterActionUsePower), nameof(CharacterActionUsePower.SpendMagicEffectUses))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpendMagicEffectUses_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] CharacterActionUsePower __instance)
        {
            TryRestoreMissingOriginItemFromPowerPoolDevice(__instance);

            //PATCH: Calculate extra charge usage for `RulesetEffectPowerWithAdvancement`
            if (__instance.actionParams.RulesetEffect.OriginItem == null ||
                __instance.actionParams.RulesetEffect is not RulesetEffectPowerWithAdvancement power)
            {
                return true;
            }

            CalculateExtraChargeUsage(__instance, power);

            return false;
        }

        private static void TryRestoreMissingOriginItemFromPowerPoolDevice(CharacterActionUsePower instance)
        {
            var character = instance.ActingCharacter.RulesetCharacter;

            if (!Global.IsMultiplayer ||
                character is not (RulesetCharacterHero or RulesetCharacterSimulacrum))
            {
                return;
            }

            var activePower = instance.activePower;
            var actionPower = instance.actionParams.RulesetEffect as RulesetEffectPower;

            if (activePower == null ||
                activePower.OriginItem != null && actionPower is not { OriginItem: null })
            {
                return;
            }

            var provider = activePower.PowerDefinition.GetFirstSubFeatureOfType<PowerPoolDevice>();

            if (provider == null)
            {
                return;
            }

            var device = provider.GetDevice(character);

            if (activePower.OriginItem == null)
            {
                activePower.originItem = device;
            }

            if (actionPower is { OriginItem: null })
            {
                actionPower.originItem = device;
            }
        }

        private static void CalculateExtraChargeUsage(
            CharacterActionUsePower __instance, RulesetEffectPowerWithAdvancement power)
        {
            var usableDevice = power.OriginItem;
            RulesetDeviceFunction usableFunction = null;

            foreach (var candidate in usableDevice.UsableFunctions)
            {
                var functionDescription = candidate.DeviceFunctionDescription;

                if (functionDescription.Type != DeviceFunctionDescription.FunctionType.Power ||
                    functionDescription.FeatureDefinitionPower != power.PowerDefinition)
                {
                    continue;
                }

                usableFunction = candidate;
                break;
            }

            if (usableFunction != null)
            {
                __instance.ActingCharacter.RulesetCharacter
                    .UseDeviceFunction(usableDevice, usableFunction, power.ExtraCharges);
            }

            ServiceRepository.GetService<IGameLocationActionService>()
                .ItemUsed?.Invoke(usableDevice.ItemDefinition.Name);
        }
    }

    //PATCH: allow check reactions on cast spell regardless of success / failure
    [HarmonyPatch(typeof(CharacterActionUsePower), nameof(CharacterActionUsePower.CounterEffectAction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CounterEffectAction_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            ref IEnumerator __result, CharacterActionUsePower __instance)
        {
            __result = SpellInterruptionContext.ResolvePowerCounterspell(__instance, __instance.ActionParams.TargetAction);

            return false;
        }
    }
}
