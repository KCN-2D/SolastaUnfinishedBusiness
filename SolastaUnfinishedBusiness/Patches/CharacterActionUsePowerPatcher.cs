using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

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

        return !__instance.ActionParams.RulesetEffect.SourceDefinition
            .HasSubFeatureOfType<IIgnoreInvisibilityInterruptionCheck>();
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
            if (!Global.IsMultiplayer ||
                instance.ActingCharacter.RulesetCharacter is not RulesetCharacterHero hero)
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

            var device = provider.GetDevice(hero);

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
            __result = Process(__instance);

            return false;
        }

        private static IEnumerator Process(CharacterActionUsePower actionUsePower)
        {
            if (actionUsePower.ActionParams.TargetAction == null)
            {
                yield break;
            }

            var actingCharacter = actionUsePower.ActingCharacter;
            var rulesetCharacter = actingCharacter.RulesetCharacter;
            var actionParams = actionUsePower.ActionParams;
            var actionModifier = actionParams.ActionModifiers[0];
            var targetAction = actionParams.TargetAction;
            var targetActionParams = targetAction.ActionParams;

            foreach (var effectForm in actionParams.RulesetEffect.EffectDescription.EffectForms)
            {
                if (effectForm.FormType != EffectForm.EffectFormType.Counter)
                {
                    continue;
                }

                var counterForm = effectForm.CounterForm;
                if (targetActionParams.RulesetEffect is not RulesetEffectSpell counteredSpell)
                {
                    continue;
                }

                var counteredSpellDefinition = counteredSpell.SpellDefinition;
                var slotLevel = counteredSpell.SlotLevel;

                if (counterForm.AutomaticSpellLevel >= slotLevel)
                {
                    targetAction.Countered = true;
                }
                else if (counterForm.CheckBaseDC != 0)
                {
                    var checkDC = counterForm.CheckBaseDC + slotLevel;

                    rulesetCharacter
                        .EnumerateFeaturesToBrowse<FeatureDefinitionMagicAffinity>(rulesetCharacter.FeaturesToBrowse);

                    foreach (var featureDefinition in rulesetCharacter.FeaturesToBrowse)
                    {
                        var definitionMagicAffinity = (FeatureDefinitionMagicAffinity)featureDefinition;

                        if (definitionMagicAffinity.CounterspellAffinity == AdvantageType.None)
                        {
                            continue;
                        }

                        var advTrend = definitionMagicAffinity.CounterspellAffinity == AdvantageType.Advantage
                            ? 1
                            : -1;

                        actionModifier.AbilityCheckAdvantageTrends.Add(new TrendInfo(
                            advTrend, FeatureSourceType.CharacterFeature, definitionMagicAffinity.Name, null));
                    }

                    if (counteredSpell.CounterAffinity != AdvantageType.None)
                    {
                        var advTrend = counteredSpell.CounterAffinity == AdvantageType.Advantage
                            ? 1
                            : -1;

                        actionModifier.AbilityCheckAdvantageTrends
                            .Add(new TrendInfo(advTrend,
                                FeatureSourceType.CharacterFeature,
                                counteredSpell.CounterAffinityOrigin, null));
                    }

                    var abilityScoreName = AttributeDefinitions.Charisma;

                    foreach (var spellRepertoire in rulesetCharacter.SpellRepertoires
                                 .Where(repertoire =>
                                     repertoire.SpellCastingFeature.SpellCastingOrigin
                                         is FeatureDefinitionCastSpell.CastingOrigin.Class
                                         or FeatureDefinitionCastSpell.CastingOrigin.Subclass))
                    {
                        abilityScoreName = spellRepertoire.SpellCastingFeature.SpellcastingAbility;

                        break;
                    }

                    var proficiencyName = string.Empty;

                    if (counterForm.AddProficiencyBonus)
                    {
                        proficiencyName = "ForcedProficiency";
                    }

                    var abilityCheckRoll = actingCharacter.RollAbilityCheckEx(
                        abilityScoreName,
                        proficiencyName,
                        checkDC,
                        AdvantageType.None,
                        actionModifier,
                        false,
                        0,
                        out var outcome,
                        out var successDelta,
                        out var rawRoll,
                        true);

                    var abilityCheckData = new AbilityCheckData
                    {
                        AbilityCheckRoll = abilityCheckRoll,
                        AbilityCheckRollOutcome = outcome,
                        AbilityCheckSuccessDelta = successDelta,
                        AbilityCheckActionModifier = actionModifier,
                        Action = actionUsePower
                    };

                    yield return TryAlterOutcomeAttributeCheck
                        .HandleITryAlterOutcomeAttributeCheck(actingCharacter, abilityCheckData, rawRoll);

                    actionUsePower.AbilityCheckRoll = abilityCheckData.AbilityCheckRoll;
                    actionUsePower.AbilityCheckRollOutcome = abilityCheckData.AbilityCheckRollOutcome;
                    actionUsePower.AbilityCheckSuccessDelta = abilityCheckData.AbilityCheckSuccessDelta;

                    if (actionUsePower.AbilityCheckRollOutcome == RollOutcome.Success)
                    {
                        targetAction.Countered = true;
                    }
                }

                if (!targetAction.Countered ||
                    rulesetCharacter.SpellCounter == null)
                {
                    continue;
                }

                var unknown = string.IsNullOrEmpty(counteredSpell.IdentifiedBy);

                rulesetCharacter.SpellCounter(
                    rulesetCharacter,
                    targetAction.ActingCharacter.RulesetCharacter,
                    counteredSpellDefinition,
                    targetAction.Countered,
                    unknown);
            }
        }
    }
}
