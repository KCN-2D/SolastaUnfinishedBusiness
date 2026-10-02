using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellsByLevelGroupPatcher
{
    [HarmonyPatch(typeof(SpellsByLevelGroup), nameof(SpellsByLevelGroup.BindInspectionOrPreparation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindInspectionOrPreparation_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            RulesetCharacter caster,
            ref RulesetSpellRepertoire spellRepertoire,
            ref FeatureDefinitionCastSpell spellcastingFeature,
            int spellLevel,
            SpellBox.BindMode bindMode)
        {
            CharacterInspectionScreenEnhancement.SelectInspectionLevelSource(
                caster, spellLevel, bindMode, ref spellRepertoire, ref spellcastingFeature);
        }
    }

    [HarmonyPatch(typeof(SpellsByLevelGroup), nameof(SpellsByLevelGroup.CommonBind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CommonBind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            SpellsByLevelGroup __instance,
            RulesetCharacter caster,
            SpellBox.BindMode bindMode,
            List<SpellDefinition> allSpells,
            ref List<SpellDefinition> trainedSpells,
            ref List<SpellDefinition> autoPreparedSpells,
            ref Dictionary<SpellDefinition, string> tagBySpell,
            ref Dictionary<SpellDefinition, string> extraSpellsMap)
        {
            if (caster == null || __instance.SpellRepertoire == null || allSpells == null)
            {
                return;
            }

            // Keep allSpells shared: the caller uses its sorted indices to refresh the bound boxes.
            // Copy preparation state and source maps so rendering cannot change the repertoire.
            autoPreparedSpells = autoPreparedSpells == null ? [] : [..autoPreparedSpells];
            tagBySpell = tagBySpell == null ? [] : new Dictionary<SpellDefinition, string>(tagBySpell);
            extraSpellsMap = extraSpellsMap == null ? [] : new Dictionary<SpellDefinition, string>(extraSpellsMap);

            LevelUpHelper.AddAutoPreparedSpellsToCommonBind(
                __instance,
                caster,
                allSpells,
                autoPreparedSpells,
                tagBySpell,
                extraSpellsMap);

            CharacterInspectionScreenEnhancement.AddInspectionLearnedSpells(
                __instance, caster, bindMode, allSpells, ref trainedSpells, tagBySpell);
        }
    }
}
