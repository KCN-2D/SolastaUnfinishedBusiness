using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;

namespace SolastaUnfinishedBusiness.Models;

internal static class SpellPreparationContext
{
    internal const string FeatureTag = "AutoPreparedFeature";

    internal static IEnumerable<SpellDefinition> EnumerateFeatureSpells(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        FeatureDefinitionAutoPreparedSpells feature)
    {
        if (character == null || repertoire == null || feature?.AutoPreparedSpellsGroups == null)
        {
            yield break;
        }

        var matcher = feature.GetFirstSubFeatureOfType<RepertoireValidForAutoPreparedFeature>();
        var spellcastingClass = repertoire.SpellCastingClass;

        if (!spellcastingClass && repertoire.SpellCastingSubclass)
        {
            spellcastingClass = LevelUpHelper.GetClassForSubclass(repertoire.SpellCastingSubclass);
        }

        if (matcher != null
                ? !matcher(repertoire, character)
                : feature.SpellcastingClass != spellcastingClass)
        {
            yield break;
        }

        var classLevel = character.GetSpellcastingLevel(repertoire);

        foreach (var spell in feature.AutoPreparedSpellsGroups
                     .Where(group => group.ClassLevel <= classLevel)
                     .SelectMany(group => group.SpellsList)
                     .Where(spell => spell != null)
                     .Distinct()
                     .OrderBy(spell => spell.Name, StringComparer.Ordinal))
        {
            yield return spell;
        }
    }

    internal static IEnumerable<(SpellDefinition Spell, string DisplayTag)> EnumerateAutoPreparedSpells(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire)
    {
        if (character == null || repertoire == null)
        {
            yield break;
        }

        // A repertoire can have several preparation sources. Keep their ownership
        // independent of the order in which features or UI panels are visited.
        var featureSpells = character.FeaturesByType<FeatureDefinitionAutoPreparedSpells>()
            .SelectMany(feature => EnumerateFeatureSpells(character, repertoire, feature)
                .Select(spell => (Spell: spell, DisplayTag: feature.AutoPreparedTag, SourceName: feature.Name)));

        var customSpells = character.GetSubFeaturesByType<IModifyAutoPreparedSpells>()
            .Where(modifier => modifier.SourceFeature != null)
            .SelectMany(modifier => modifier.GetAutoPreparedSpells(character, repertoire)
                .Where(spell => spell != null)
                .Select(spell => (
                    Spell: spell,
                    DisplayTag: $"{FeatureTag}|{modifier.SourceFeature.Name}",
                    SourceName: modifier.SourceFeature.Name)));

        HashSet<SpellDefinition> yielded = [];

        foreach (var entry in featureSpells.Concat(customSpells)
                     .OrderBy(entry => entry.SourceName, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Spell.Name, StringComparer.Ordinal))
        {
            if (yielded.Add(entry.Spell))
            {
                yield return (entry.Spell, entry.DisplayTag ?? string.Empty);
            }
        }

        // Feature-based preparation was resolved above. Only add spells from feat repertoires
        // here, avoiding a second traversal and re-evaluation of the same feature graph.
        foreach (var entry in LevelUpHelper.EnumerateSlotCastableFeatRepertoireSpells(character, repertoire))
        {
            if (yielded.Add(entry.Spell))
            {
                yield return entry;
            }
        }
    }
}
