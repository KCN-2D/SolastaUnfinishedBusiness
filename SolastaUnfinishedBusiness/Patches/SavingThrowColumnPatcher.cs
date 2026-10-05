using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SavingThrowColumnPatcher
{
    //PATCH: allow ISavingThrowAffinityProvider to be validated with IsCharacterValidHandler
    [HarmonyPatch(typeof(SavingThrowColumn), nameof(SavingThrowColumn.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CBind_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var baseBonus = AccessTools.Method(typeof(RulesetActor), nameof(RulesetActor.ComputeBaseSavingThrowBonus));
            var displayBonus = AccessTools.Method(typeof(CBind_Patch), nameof(ComputeSavingThrowBonusForDisplay));

            return instructions.ReplaceEnumerateFeaturesToBrowse<ISavingThrowAffinityProvider>(
                    "SavingThrowColumn.Bind", EnumerateFeatureDefinitionSavingThrowAffinity)
                .ReplaceCalls(baseBonus, 1, "SavingThrowColumn.Bind conditional bonuses",
                    new CodeInstruction(OpCodes.Call, displayBonus));
        }

        private static int ComputeSavingThrowBonusForDisplay(
            RulesetActor actor,
            string abilityScoreName,
            List<RuleDefinitions.TrendInfo> modifierTrends)
        {
            var result = actor.ComputeBaseSavingThrowBonus(abilityScoreName, modifierTrends);

            if (actor is not RulesetCharacter character)
            {
                return result;
            }

            foreach (var provider in character.GetSubFeaturesByType<IConditionalSavingThrowBonusProvider>())
            {
                var bonus = provider.GetSavingThrowBonus(character, abilityScoreName);

                if (bonus == 0)
                {
                    continue;
                }

                var source = provider.SourceDefinition;

                result += bonus;
                modifierTrends.Add(new RuleDefinitions.TrendInfo(
                    bonus, RuleDefinitions.FeatureSourceType.CharacterFeature, source?.Name ?? string.Empty, source));
            }

            return result;
        }

        private static void EnumerateFeatureDefinitionSavingThrowAffinity(
            RulesetCharacter __instance,
            List<FeatureDefinition> featuresToBrowse,
            Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin> featuresOrigin)
        {
            __instance.EnumerateFeaturesToBrowse<ISavingThrowAffinityProvider>(featuresToBrowse,
                featuresOrigin);
            featuresToBrowse.RemoveAll(x =>
                !__instance.IsValid(x.GetAllSubFeaturesOfType<IsCharacterValidHandler>()));
        }
    }
}
