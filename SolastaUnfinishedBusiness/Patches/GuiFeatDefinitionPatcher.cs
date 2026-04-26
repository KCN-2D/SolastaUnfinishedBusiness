using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Validators;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiFeatDefinitionPatcher
{
    [HarmonyPatch(typeof(GuiFeatDefinition), nameof(GuiFeatDefinition.IsFeatMatchingPrerequisites))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsFeatMatchingPrerequisites_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            FeatDefinition feat,
            out (bool active, bool disableLevel, bool disableRace, bool disableCastSpell) __state)
        {
            __state = Tabletop2024Context.PushModFeatPrerequisiteOverride(
                Tabletop2024Context.ShouldForceManagedFeatPrerequisites(feat));
        }

        [UsedImplicitly]
        public static void Postfix(
            ref bool __result,
            FeatDefinition feat,
            RulesetCharacterHero hero,
            ref string prerequisiteOutput,
            (bool active, bool disableLevel, bool disableRace, bool disableCastSpell) __state)
        {
            ReplaceVanillaAbilityScorePrerequisiteTitle(feat, ref prerequisiteOutput);

            //PATCH: Enforces Feats With PreRequisites
            if (feat is not FeatDefinitionWithPrerequisites featDefinitionWithPrerequisites
                || featDefinitionWithPrerequisites.Validators.Count == 0)
            {
                Tabletop2024Context.RestoreModFeatPrerequisiteOverride(__state);
                return;
            }

            var (result, output) = featDefinitionWithPrerequisites.Validate(featDefinitionWithPrerequisites, hero);

            __result = __result && result;
            if (string.IsNullOrEmpty(output))
            {
                Tabletop2024Context.RestoreModFeatPrerequisiteOverride(__state);

                return;
            }

            if (!string.IsNullOrEmpty(prerequisiteOutput))
            {
                output = '\n' + output;
            }

            prerequisiteOutput += output;
            Tabletop2024Context.RestoreModFeatPrerequisiteOverride(__state);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: Replace call to RulesetCharacterHero.SpellRepertoires.Count with Count list of FeatureCastSpell
            //which are registered before feat selection at lvl 1
            return instructions
                .ReplaceCall(typeof(RulesetCharacter).GetMethod("get_SpellRepertoires"),
                    1,
                    1, "GuiFeatDefinition.IsFeatMatchingPrerequisites",
                    new CodeInstruction(OpCodes.Call,
                        new Func<RulesetCharacterHero, int>(CanCastSpells).Method))

                // PATCH: Remove asserts in DEBUG build
                .RemoveBoolAsserts();
        }

        private static int CanCastSpells([NotNull] RulesetCharacterHero hero)
        {
            return hero.FeaturesByType<FeatureDefinitionCastSpell>().Count;
        }

        private static void ReplaceVanillaAbilityScorePrerequisiteTitle(
            FeatDefinition feat,
            ref string prerequisiteOutput)
        {
            if (feat == null ||
                !feat.minimalAbilityScorePrerequisite ||
                string.IsNullOrEmpty(feat.minimalAbilityScoreName) ||
                string.IsNullOrEmpty(prerequisiteOutput))
            {
                return;
            }

            var shortTitle = LocalizeShortAbilityScoreTitle(feat.minimalAbilityScoreName);
            var longTitle = ValidatorsFeat.LocalizePrerequisiteAbilityScoreTitle(feat.minimalAbilityScoreName);

            if (shortTitle == longTitle)
            {
                return;
            }

            var value = feat.minimalAbilityScoreValue.ToString();
            var shortPrerequisite = Gui.Format("Tooltip/&FeatPrerequisiteAbilityScoreFormat", shortTitle, value);
            var longPrerequisite = Gui.Format("Tooltip/&FeatPrerequisiteAbilityScoreFormat", longTitle, value);

            prerequisiteOutput = prerequisiteOutput.Replace(shortPrerequisite, longPrerequisite);
        }

        private static string LocalizeShortAbilityScoreTitle(string abilityScoreName)
        {
            var title = Gui.Localize($"Attribute/&{abilityScoreName}Title");

            return string.IsNullOrEmpty(title) || title.Contains("/&")
                ? abilityScoreName
                : title;
        }
    }

    [HarmonyPatch(typeof(GuiFeatDefinition), nameof(GuiFeatDefinition.Subtitle), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Subtitle_Getter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GuiFeatDefinition __instance, ref string __result)
        {
            //PATCH: use 'Feat Group' as subtitle for feats that are feat groups
            if (__instance?.FeatDefinition == null ||
                __instance.FeatDefinition.GetFirstSubFeatureOfType<IGroupedFeat>() == null &&
                !Tabletop2024Context.IsTabletopContainerGroup(__instance.FeatDefinition))
            {
                return true;
            }

            __result = "Tooltip/&FeatGroupTitle";

            return false;
        }
    }

    [HarmonyPatch(typeof(GuiFeatDefinition), nameof(GuiFeatDefinition.Description), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Description_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiFeatDefinition __instance, ref string __result)
        {
            if (__instance?.FeatDefinition == null)
            {
                return;
            }

            if (FeatsContext.TryBuildFeatGroupContentsDescription(__instance.FeatDefinition, out var description))
            {
                __result = description;
            }
        }
    }
}
