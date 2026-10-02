using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Patches;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemDefinitions;

namespace SolastaUnfinishedBusiness.Api.Helpers;

internal static class LevelUpHelper
{
    internal const string ExtraClassTag = "@Class";
    internal const string ExtraSubclassTag = "@Subclass";
    private const int AnySpellLevel = -1;

    // keeps a tab on all heroes leveling up
    private static readonly Dictionary<RulesetCharacterHero, LevelUpData> LevelUpTab = new();

    internal static void RegisterHero(
        [NotNull] RulesetCharacterHero rulesetCharacterHero,
        bool levelingUp)
    {
        //PATCH: enable custom models renderer
        CustomModels.SwitchRenderer(true);

        CharacterClassDefinition lastClass = null;
        CharacterSubclassDefinition lastSubclass = null;

        if (levelingUp)
        {
            lastClass = rulesetCharacterHero.ClassesHistory.Last();
            rulesetCharacterHero.ClassesAndSubclasses.TryGetValue(lastClass, out lastSubclass);
        }

        LevelUpTab.TryAdd(rulesetCharacterHero,
            new LevelUpData
            {
                Hero = rulesetCharacterHero,
                SelectedClass = lastClass,
                SelectedSubclass = lastSubclass,
                IsLevelingUp = levelingUp,
                InitialCharacterLevel = rulesetCharacterHero.ClassesHistory.Count,
                FeatSpellReplacements = levelingUp ? CaptureFeatSpellReplacements(rulesetCharacterHero) : []
            });

        // fixes max level and exp in case level 20 gets enabled after a campaign starts
        var characterLevelAttribute = rulesetCharacterHero.GetAttribute(AttributeDefinitions.CharacterLevel);

        characterLevelAttribute.MaxValue = Main.Settings.EnableLevel20
            ? Level20Context.ModMaxLevel
            : Level20Context.GameMaxLevel;
        characterLevelAttribute.Refresh();

        var experienceAttribute = rulesetCharacterHero.GetAttribute(AttributeDefinitions.Experience);

        experienceAttribute.MaxValue = Level20Context.ModMaxExperience;
        experienceAttribute.Refresh();
    }

    internal static void UnregisterHero([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        //PATCH: disable custom models renderer
        CustomModels.SwitchRenderer(false);

        LevelUpTab.Remove(rulesetCharacterHero);
    }

    [CanBeNull]
    internal static CharacterClassDefinition GetSelectedClass([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        return LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData)
            ? levelUpData.SelectedClass
            : null;
    }

    internal static void SetSelectedClass([NotNull] RulesetCharacterHero rulesetCharacterHero,
        CharacterClassDefinition characterClassDefinition)
    {
        if (!LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData))
        {
            return;
        }

        levelUpData.SelectedClass = characterClassDefinition;

        if (!characterClassDefinition)
        {
            levelUpData.SelectedSubclass = null;

            return;
        }

        var classesAndLevels = rulesetCharacterHero.ClassesAndLevels;

        rulesetCharacterHero.ClassesAndSubclasses.TryGetValue(levelUpData.SelectedClass, out var subclass);
        levelUpData.SelectedSubclass = subclass;

        levelUpData.RequiresDeity =
            (levelUpData.SelectedClass == Cleric && !classesAndLevels.ContainsKey(Cleric)) ||
            (levelUpData.SelectedClass == Paladin && !rulesetCharacterHero.DeityDefinition);

        levelUpData.GrantedItems = [];

        DatabaseHelper.TryGetDefinition<CharacterClassDefinition>(InventorClass.ClassName, out var inventorClass);

        void AddGrantedItemsIfRequired(bool required, params ItemDefinition[] items)
        {
            if (!required)
            {
                return;
            }

            levelUpData.GrantedItems.AddRange(items);
        }

        // Holy Symbol
        AddGrantedItemsIfRequired(
            (
                levelUpData.SelectedClass == Cleric ||
                levelUpData.SelectedClass == Paladin
            ) &&
            !(
                classesAndLevels.ContainsKey(Cleric) ||
                classesAndLevels.ContainsKey(Paladin)
            ),
            HolySymbolAmulet);

        // Component Pouch
        AddGrantedItemsIfRequired(
            (
                levelUpData.SelectedClass == Ranger ||
                levelUpData.SelectedClass == Sorcerer ||
                levelUpData.SelectedClass == Warlock ||
                levelUpData.SelectedClass == Wizard ||
                (inventorClass && levelUpData.SelectedClass == inventorClass)
            ) &&
            !(
                classesAndLevels.ContainsKey(Ranger) ||
                classesAndLevels.ContainsKey(Sorcerer) ||
                classesAndLevels.ContainsKey(Warlock) ||
                classesAndLevels.ContainsKey(Wizard) ||
                (inventorClass && classesAndLevels.ContainsKey(inventorClass))
            ),
            ComponentPouch);

        // Bardic Flute
        AddGrantedItemsIfRequired(
            levelUpData.SelectedClass == Bard && !classesAndLevels.ContainsKey(Bard),
            Flute);

        // Druidic Focus
        AddGrantedItemsIfRequired(
            levelUpData.SelectedClass == Druid && !classesAndLevels.ContainsKey(Druid),
            DruidicFocus);

        // Spellbook and Clothes Wizard
        AddGrantedItemsIfRequired(
            !classesAndLevels.ContainsKey(Wizard) && levelUpData.SelectedClass == Wizard,
            Spellbook,
            ClothesWizard);
    }

    [CanBeNull]
    internal static CharacterSubclassDefinition GetSelectedSubclass([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        return LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData)
            ? levelUpData.SelectedSubclass
            : null;
    }

    internal static void SetSelectedSubclass([NotNull] RulesetCharacterHero rulesetCharacterHero,
        CharacterSubclassDefinition characterSubclassDefinition)
    {
        if (!LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData))
        {
            return;
        }

        levelUpData.SelectedSubclass = characterSubclassDefinition;
    }

    [CanBeNull]
    private static RulesetSpellRepertoire GetSelectedClassOrSubclassRepertoire(
        [NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        return rulesetCharacterHero.SpellRepertoires.FirstOrDefault(x =>
            (x.SpellCastingClass && x.SpellCastingClass == GetSelectedClass(rulesetCharacterHero))
            || (x.SpellCastingSubclass &&
                x.SpellCastingSubclass == GetSelectedSubclass(rulesetCharacterHero)));
    }

    internal static void SetIsClassSelectionStage(RulesetCharacterHero rulesetCharacterHero, bool isClassSelectionStage)
    {
        if (rulesetCharacterHero == null || !LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData))
        {
            return;
        }

        levelUpData.IsClassSelectionStage = isClassSelectionStage;
    }

    internal static bool RequiresDeity([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        return LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData)
               && levelUpData.RequiresDeity;
    }

    internal static int GetSelectedClassLevel([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        var selectedClass = GetSelectedClass(rulesetCharacterHero);

        return Math.Max(1, rulesetCharacterHero.GetClassLevel(selectedClass));
    }

    internal static bool IsClassSelectionStage([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        return LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData) &&
               levelUpData.IsClassSelectionStage;
    }

    internal static bool IsLevelingUp([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        return LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData) && levelUpData.IsLevelingUp;
    }

    internal static bool IsMulticlass([NotNull] RulesetCharacterHero rulesetCharacterHero)
    {
        return LevelUpTab.TryGetValue(rulesetCharacterHero, out var levelUpData)
               && levelUpData.SelectedClass
               && (rulesetCharacterHero.ClassesAndLevels.Count > 1
                   || !rulesetCharacterHero.ClassesAndLevels.ContainsKey(levelUpData.SelectedClass));
    }

    internal static bool IsRepertoireFromSelectedClassSubclass(
        [NotNull] RulesetCharacterHero rulesetCharacterHero,
        [NotNull] RulesetSpellRepertoire rulesetSpellRepertoire)
    {
        var selectedClass = GetSelectedClass(rulesetCharacterHero);
        var selectedSubclass = GetSelectedSubclass(rulesetCharacterHero);

        return
            (rulesetSpellRepertoire.SpellCastingFeature.SpellCastingOrigin ==
             FeatureDefinitionCastSpell.CastingOrigin.Class
             && rulesetSpellRepertoire.SpellCastingClass == selectedClass) ||
            (rulesetSpellRepertoire.SpellCastingFeature.SpellCastingOrigin ==
             FeatureDefinitionCastSpell.CastingOrigin.Subclass
             && rulesetSpellRepertoire.SpellCastingSubclass == selectedSubclass);
    }

    [NotNull]
    private static HashSet<SpellDefinition> CacheAllowedAutoPreparedSpells(
        [NotNull] IEnumerable<FeatureDefinition> featureDefinitions)
    {
        var allowedAutoPreparedSpells = new List<SpellDefinition>();

        foreach (var featureDefinition in featureDefinitions)
        {
            switch (featureDefinition)
            {
                case FeatureDefinitionAutoPreparedSpells
                {
                    AutoPreparedSpellsGroups: not null
                } featureDefinitionAutoPreparedSpells:
                    allowedAutoPreparedSpells.AddRange(
                        featureDefinitionAutoPreparedSpells.AutoPreparedSpellsGroups.SelectMany(x => x.SpellsList));
                    break;
                case FeatureDefinitionFeatureSet { uniqueChoices: false } featureDefinitionFeatureSet:
                    allowedAutoPreparedSpells.AddRange(
                        CacheAllowedAutoPreparedSpells(featureDefinitionFeatureSet.FeatureSet));
                    break;
            }
        }

        return [.. allowedAutoPreparedSpells];
    }

    [NotNull]
    private static HashSet<SpellDefinition> CacheAllowedSpells(
        [NotNull] IEnumerable<FeatureDefinition> featureDefinitions)
    {
        var allowedSpells = new List<SpellDefinition>();

        foreach (var featureDefinition in featureDefinitions)
        {
            switch (featureDefinition)
            {
                case FeatureDefinitionFeatureSet { uniqueChoices: false } featureDefinitionFeatureSet:
                    allowedSpells.AddRange(
                        CacheAllowedSpells(featureDefinitionFeatureSet.FeatureSet));
                    break;

                case FeatureDefinitionCastSpell featureDefinitionCastSpell
                    when featureDefinitionCastSpell.SpellListDefinition:
                    allowedSpells.AddRange(
                        featureDefinitionCastSpell.SpellListDefinition.SpellsByLevel.SelectMany(x => x.Spells));
                    break;

                case FeatureDefinitionMagicAffinity featureDefinitionMagicAffinity
                    when featureDefinitionMagicAffinity.ExtendedSpellList:
                    allowedSpells.AddRange(
                        featureDefinitionMagicAffinity.ExtendedSpellList.SpellsByLevel.SelectMany(x => x.Spells));
                    break;

                case FeatureDefinitionBonusCantrips { BonusCantrips: not null } featureDefinitionBonusCantrips:
                    allowedSpells.AddRange(featureDefinitionBonusCantrips.BonusCantrips);
                    break;

                case FeatureDefinitionAutoPreparedSpells
                {
                    AutoPreparedSpellsGroups: not null
                } featureDefinitionAutoPreparedSpells:
                    allowedSpells.AddRange(
                        featureDefinitionAutoPreparedSpells.AutoPreparedSpellsGroups.SelectMany(x => x.SpellsList));
                    break;
            }
        }

        return allowedSpells.ToHashSet();
    }

    [NotNull]
    private static Dictionary<SpellDefinition, string> CacheOtherClassesKnownSpells([NotNull] RulesetCharacterHero hero)
    {
        var selectedRepertoire = GetSelectedClassOrSubclassRepertoire(hero);
        var knownSpells = new Dictionary<SpellDefinition, string>();

        foreach (var spellRepertoire in hero.SpellRepertoires
                     .Where(x => x != selectedRepertoire))
        {
            var maxSpellLevel = spellRepertoire.MaxSpellLevelOfSpellCastingLevel;
            var castingFeature = spellRepertoire.SpellCastingFeature;
            var tag = "Multiclass";

            if (spellRepertoire.spellCastingClass)
            {
                tag = $"{ExtraClassTag}|{spellRepertoire.spellCastingClass.Name}";
            }
            else if (spellRepertoire.spellCastingSubclass)
            {
                tag = $"{ExtraSubclassTag}|{spellRepertoire.spellCastingSubclass.Name}";
            }
            else if (spellRepertoire.spellCastingRace)
            {
                tag = "Race";
            }

            switch (castingFeature.spellKnowledge)
            {
                case SpellKnowledge.Selection:
                    knownSpells.TryAddRange(
                        spellRepertoire.AutoPreparedSpells.Where(x => x.SpellLevel <= maxSpellLevel), tag);
                    knownSpells.TryAddRange(spellRepertoire.KnownCantrips, tag);
                    knownSpells.TryAddRange(spellRepertoire.KnownSpells, tag);
                    break;
                case SpellKnowledge.Spellbook:
                    knownSpells.TryAddRange(
                        spellRepertoire.AutoPreparedSpells.Where(x => x.SpellLevel <= maxSpellLevel), tag);
                    knownSpells.TryAddRange(spellRepertoire.KnownCantrips, tag);
                    knownSpells.TryAddRange(spellRepertoire.KnownSpells, tag);
                    knownSpells.TryAddRange(spellRepertoire.EnumerateAvailableScribedSpells(), tag);
                    break;
                case SpellKnowledge.FixedList:
                case SpellKnowledge.WholeList:
                    knownSpells.TryAddRange(spellRepertoire.KnownCantrips, tag);
                    knownSpells.TryAddRange(
                        castingFeature.SpellListDefinition.SpellsByLevel.SelectMany(s => s.Spells)
                            .Where(x => x.SpellLevel > 0 && x.SpellLevel <= maxSpellLevel), tag);
                    break;
                default:
                    continue;
            }
        }

        return knownSpells;
    }

    internal static HashSet<SpellDefinition> GetAllowedSpells([NotNull] RulesetCharacterHero hero)
    {
        return !LevelUpTab.TryGetValue(hero, out var levelUpData)
            ? []
            : levelUpData.AllowedSpells;
    }

    internal static IEnumerable<SpellDefinition> GetAllowedAutoPreparedSpells([NotNull] RulesetCharacterHero hero)
    {
        return !LevelUpTab.TryGetValue(hero, out var levelUpData)
            ? []
            : levelUpData.AllowedAutoPreparedSpells;
    }

    internal static Dictionary<SpellDefinition, string> GetOtherClassesKnownSpells([NotNull] RulesetCharacterHero hero)
    {
        return !LevelUpTab.TryGetValue(hero, out var levelUpData)
            ? new Dictionary<SpellDefinition, string>()
            : levelUpData.OtherClassesKnownSpells;
    }

    private static bool IsSpellCastableWithRepertoireSlots(
        SpellDefinition spell,
        int maxSpellLevel,
        int spellLevel)
    {
        return spell is { Implemented: true, GuiPresentation.hidden: false, SpellLevel: > 0 } &&
               spell.SpellLevel <= maxSpellLevel &&
               (spellLevel == AnySpellLevel || spell.SpellLevel == spellLevel) &&
               !SpellsContext.SpellsChildMaster.ContainsKey(spell);
    }

    internal static IEnumerable<(SpellDefinition Spell, string DisplayTag)> EnumerateSlotCastableExtraSpellsForRepertoire(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        int spellLevel = AnySpellLevel)
    {
        if (character == null || !repertoire.UsesSharedSpellSlots())
        {
            yield break;
        }

        var maxSpellLevel = repertoire.MaxSpellLevelOfSpellCastingLevel;

        if (maxSpellLevel <= 0)
        {
            yield break;
        }

        HashSet<SpellDefinition> yielded = [];
        foreach (var feature in character.FeaturesByType<FeatureDefinitionAutoPreparedSpells>()
                     .Where(feature => feature.AutoPreparedSpellsGroups != null)
                     .OrderBy(feature => feature.Name, StringComparer.Ordinal)
                     .Where(feature => feature.HasSubFeatureOfType<RepertoireValidForAutoPreparedFeature>()))
        {
            foreach (var spell in SpellPreparationContext.EnumerateFeatureSpells(character, repertoire, feature)
                         .Where(spell => IsSpellCastableWithRepertoireSlots(spell, maxSpellLevel, spellLevel)))
            {
                if (yielded.Add(spell))
                {
                    yield return (spell, feature.AutoPreparedTag);
                }
            }
        }

        foreach (var (spell, displayTag) in EnumerateSlotCastableFeatRepertoireSpells(character, repertoire, spellLevel))
        {
            if (yielded.Add(spell))
            {
                yield return (spell, displayTag);
            }
        }
    }

    internal static IEnumerable<(SpellDefinition Spell, string DisplayTag)> EnumerateSlotCastableFeatRepertoireSpells(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        int spellLevel = AnySpellLevel)
    {
        if (character == null || !repertoire.UsesSharedSpellSlots())
        {
            yield break;
        }

        var maxSpellLevel = repertoire.MaxSpellLevelOfSpellCastingLevel;
        if (maxSpellLevel <= 0)
        {
            yield break;
        }

        HashSet<SpellDefinition> yielded = [];
        foreach (var entry in EnumerateSlotCastableFeatSpells(character)
                     .Where(entry => IsSpellCastableWithRepertoireSlots(entry.Spell, maxSpellLevel, spellLevel)))
        {
            // A class's independently learned copy keeps its own source. Only project the
            // selected feat grant here, using the same origin as tooltips and actual casting.
            if (SpellCastingResourceContext.ResolveCastingRepertoire(repertoire, entry.Spell, character) == entry.Repertoire &&
                yielded.Add(entry.Spell))
            {
                yield return (entry.Spell, entry.DisplayTag);
            }
        }
    }

    internal static IEnumerable<(RulesetSpellRepertoire Repertoire, SpellDefinition Spell, string DisplayTag)>
        EnumerateSlotCastableFeatSpells(RulesetCharacter character)
    {
        if (character == null)
        {
            yield break;
        }

        foreach (var repertoire in character.SpellRepertoires
                     .Where(repertoire => repertoire.SpellCastingFeature != null)
                     .OrderBy(repertoire => repertoire.SpellCastingFeature.Name, StringComparer.Ordinal))
        {
            var feature = repertoire.SpellCastingFeature;
            var tag = feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();
            if (tag == null ||
                !tag.AllowSlotCasting && !Tabletop2024Context.IsSlotCastableTabletop2024FeatSpellTag(tag))
            {
                continue;
            }

            var displayTag = Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(tag.Name);
            var fixedSpells = (tag.ForceFixedList || feature.SpellKnowledge == SpellKnowledge.FixedList) &&
                              feature.SpellListDefinition != null
                ? feature.SpellListDefinition.SpellsByLevel.SelectMany(level => level.Spells)
                : Enumerable.Empty<SpellDefinition>();
            foreach (var spell in repertoire.KnownSpells.Concat(repertoire.PreparedSpells).Concat(fixedSpells)
                         .Concat(repertoire.AutoPreparedSpells)
                         .Concat(repertoire.ExtraSpellsByTag.Values.SelectMany(spells => spells))
                         .Distinct().Where(spell => IsSpellCastableWithRepertoireSlots(spell, 9, AnySpellLevel))
                         .OrderBy(spell => spell.Name, StringComparer.Ordinal))
            {
                yield return (repertoire, spell, displayTag);
            }
        }
    }

    internal static void AddAutoPreparedSpellsToCommonBind(
        SpellsByLevelGroup group,
        RulesetCharacter caster,
        List<SpellDefinition> allSpells,
        List<SpellDefinition> autoPreparedSpells,
        Dictionary<SpellDefinition, string> tagBySpell,
        Dictionary<SpellDefinition, string> extraSpellsMap)
    {
        if (caster == null ||
            group is not { SpellRepertoire: not null, SpellLevel: > 0 } ||
            allSpells == null)
        {
            return;
        }

        var repertoire = group.SpellRepertoire;
        var selectedSpells = repertoire.ExtraSpellsByTag
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .SelectMany(entry => entry.Value.Select(spell => (Spell: spell, DisplayTag: entry.Key)))
            .Where(entry => entry.Spell.SpellLevel == group.SpellLevel)
            .ToArray();

        foreach (var (spell, displayTag) in SpellPreparationContext
                     .EnumerateAutoPreparedSpells(caster, repertoire)
                     .Where(entry => entry.Spell.SpellLevel == group.SpellLevel))
        {
            allSpells.TryAdd(spell);
            autoPreparedSpells.TryAdd(spell);
            tagBySpell[spell] = displayTag;
            extraSpellsMap[spell] = displayTag;
        }

        // Mastery, signature spells and other explicit repertoire choices retain their own source.
        foreach (var (spell, displayTag) in selectedSpells)
        {
            tagBySpell[spell] = displayTag;
            extraSpellsMap[spell] = displayTag;
        }
    }

    internal static void AddAutoPreparedSpellsToExtraSpellsMap(
        RulesetSpellRepertoire repertoire,
        int spellLevel,
        Dictionary<SpellDefinition, string> extraSpellsMap)
    {
        if (spellLevel <= 0 ||
            extraSpellsMap == null ||
            repertoire?.GetCaster() is not { } character)
        {
            return;
        }

        foreach (var (spell, displayTag) in SpellPreparationContext
                     .EnumerateAutoPreparedSpells(character, repertoire)
                     .Where(entry => entry.Spell.SpellLevel == spellLevel))
        {
            if (spell.ActivationTime is ActivationTime.Reaction or ActivationTime.OnAttackHit)
            {
                continue;
            }

            extraSpellsMap.TryAdd(spell, displayTag);
        }
    }

    internal static bool IsSlotCastableExtraSpellForRepertoire(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell)
    {
        return spell != null &&
               EnumerateSlotCastableExtraSpellsForRepertoire(character, repertoire, spell.SpellLevel)
                   .Any(entry => entry.Spell == spell);
    }

    internal static bool IsPreparedOrSlotCastableExtraSpellForRepertoire(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell)
    {
        if (spell == null)
        {
            return false;
        }

        var castingFeature = repertoire?.spellCastingFeature;
        var isPreparedSpellForWholeListCaster =
            castingFeature is
            {
                SpellKnowledge: SpellKnowledge.WholeList,
                SpellReadyness: SpellReadyness.Prepared
            } &&
            repertoire.PreparedSpells.Contains(spell);

        return isPreparedSpellForWholeListCaster ||
               IsSlotCastableExtraSpellForRepertoire(character, repertoire, spell);
    }

    internal static ActivationTime GetSpellActivationTime(ActionDefinitions.ActionType actionType)
    {
        return actionType switch
        {
            ActionDefinitions.ActionType.Bonus => ActivationTime.BonusAction,
            ActionDefinitions.ActionType.Main => ActivationTime.Action,
            ActionDefinitions.ActionType.Reaction => ActivationTime.Reaction,
            ActionDefinitions.ActionType.NoCost => ActivationTime.NoCost,
            _ => ActivationTime.Action
        };
    }

    internal static void EnumerateExtraSpells(
        Dictionary<SpellDefinition, string> extraSpells,
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire)
    {
        if (character == null || repertoire == null)
        {
            return;
        }

        foreach (var (spell, displayTag) in SpellPreparationContext.EnumerateAutoPreparedSpells(character, repertoire))
        {
            extraSpells.TryAdd(spell, displayTag);
        }

        // Pending level-up feats may not have been granted to the hero yet.
        if (character is not RulesetCharacterHero hero ||
            !hero.TryGetHeroBuildingData(out var data))
        {
            return;
        }

        var features = data.levelupTrainedFeats
            .SelectMany(entry => entry.Value)
            .SelectMany(feat => feat.Features)
            .OfType<FeatureDefinitionAutoPreparedSpells>()
            .OrderBy(feature => feature.Name, StringComparer.Ordinal);

        foreach (var feature in features)
        {
            foreach (var spell in SpellPreparationContext.EnumerateFeatureSpells(character, repertoire, feature))
            {
                extraSpells.TryAdd(spell, feature.AutoPreparedTag);
            }
        }
    }

    internal static void GrantItemsIfRequired([NotNull] RulesetCharacterHero hero)
    {
        if (!LevelUpTab.TryGetValue(hero, out var levelUpData) || !levelUpData.IsLevelingUp)
        {
            return;
        }

        foreach (var grantedItem in levelUpData.GrantedItems)
        {
            hero.GrantItem(grantedItem, false);
        }
    }

    internal static void RemoveItemsIfRequired([NotNull] RulesetCharacterHero hero)
    {
        if (!LevelUpTab.TryGetValue(hero, out var levelUpData) || !levelUpData.IsLevelingUp)
        {
            return;
        }

        foreach (var grantedItem in levelUpData.GrantedItems)
        {
            hero.LoseItem(grantedItem, false);
        }
    }

    internal static void GrantRaceFeatures(
        CharacterBuildingManager characterBuildingManager,
        RulesetCharacterHero hero)
    {
        var characterLevel = hero.ClassesHistory.Count;

        // game correctly handles level 1
        if (characterLevel <= 1)
        {
            return;
        }

        var raceDefinition = hero.RaceDefinition;
        var subRaceDefinition = hero.SubRaceDefinition;
        var grantedFeatures = new List<FeatureDefinition>();

        raceDefinition.FeatureUnlocks
            .Where(x => x.Level == characterLevel)
            .Do(x => grantedFeatures.Add(x.FeatureDefinition));

        if (subRaceDefinition)
        {
            subRaceDefinition.FeatureUnlocks
                .Where(x => x.Level == characterLevel)
                .Do(x => grantedFeatures.Add(x.FeatureDefinition));
        }

        characterBuildingManager.GrantFeatures(hero, grantedFeatures, $"02Race{characterLevel}", false);
    }

    internal static void GrantSpellsOrCantripsFromFeatCastSpell(
        CharacterBuildingManager characterBuildingManager,
        [NotNull] RulesetCharacterHero hero)
    {
        var heroBuildingData = hero.GetHeroBuildingData();

        foreach (var featureDefinitionCastSpell in heroBuildingData.LevelupTrainedFeats
                     .SelectMany(x => x.Value)
                     .SelectMany(x => x.Features)
                     .OfType<FeatureDefinitionCastSpell>())
        {
            var spellTag = featureDefinitionCastSpell.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();

            if (spellTag == null)
            {
                continue;
            }

            if (!CharacterBuildingManagerPatcher.TryResolveFeatGrantedPointPoolTags(
                    characterBuildingManager,
                    hero,
                    spellTag.Name,
                    out _,
                    out _,
                    out var finalTag))
            {
                continue;
            }

            // grant cantrips from selection or fixed list
            if (heroBuildingData.AcquiredCantrips.TryGetValue(finalTag, out var cantrips))
            {
                foreach (var cantrip in cantrips)
                {
                    hero.GrantCantrip(cantrip, featureDefinitionCastSpell);
                }
            }
            else if (featureDefinitionCastSpell.SpellKnowledge == SpellKnowledge.FixedList)
            {
                foreach (var spell in featureDefinitionCastSpell.SpellListDefinition.SpellsByLevel
                             .Where(x => x.Level == 0)
                             .SelectMany(x => x.Spells))
                {
                    hero.GrantCantrip(spell, featureDefinitionCastSpell);
                }
            }

            // grant spells from fixed list or selection
            if (spellTag.ForceFixedList || featureDefinitionCastSpell.SpellKnowledge == SpellKnowledge.FixedList)
            {
                foreach (var spell in featureDefinitionCastSpell.SpellListDefinition.SpellsByLevel
                             .Where(x => x.Level > 0)
                             .SelectMany(x => x.Spells))
                {
                    hero.GrantSpell(spell, featureDefinitionCastSpell);
                }
            }
            else if (heroBuildingData.AcquiredSpells.TryGetValue(finalTag, out var spells))
            {
                foreach (var spell in spells)
                {
                    hero.GrantSpell(spell, featureDefinitionCastSpell);
                }
            }
        }
    }


    private static List<FeatSpellReplacement> CaptureFeatSpellReplacements(RulesetCharacterHero hero)
    {
        var replacements = new List<FeatSpellReplacement>();
        var handledFeatures = new HashSet<FeatureDefinitionCastSpell>();

        foreach (var feat in hero.TrainedFeats)
        {
            var replacement = feat.GetFirstSubFeatureOfType<FeatHelpers.SpellReplacementOnLevelUp>();
            if (replacement is not { IsEnabled: true } ||
                replacement.CastingFeatures.Any(handledFeatures.Contains))
            {
                continue;
            }

            var repertoires = replacement.CastingFeatures.Select(feature =>
                hero.SpellRepertoires.FirstOrDefault(repertoire => repertoire.SpellCastingFeature == feature)).ToArray();
            if (repertoires.Any(repertoire => repertoire == null))
            {
                continue;
            }

            var selection = new FeatSpellReplacement(feat, repertoires);
            if (selection.PreviousSpells.Length == 0)
            {
                continue;
            }

            replacements.Add(selection);
            handledFeatures.UnionWith(replacement.CastingFeatures);
        }

        return replacements;
    }

    internal static void EnsureFeatSpellReplacementPools(
        CharacterBuildingManager manager,
        CharacterHeroBuildingData data)
    {
        if (data?.HeroCharacter == null ||
            !LevelUpTab.TryGetValue(data.HeroCharacter, out var levelUpData) ||
            !levelUpData.IsLevelingUp || data.HeroCharacter.ClassesHistory.Count <= levelUpData.InitialCharacterLevel)
        {
            return;
        }

        foreach (var replacement in levelUpData.FeatSpellReplacements)
        {
            var pools = data.PointPoolStacks[HeroDefinitions.PointsPoolType.CantripOrSpell].ActivePools;
            if (pools.ContainsKey(replacement.Tag))
            {
                continue;
            }

            manager.SetPointPool(data, HeroDefinitions.PointsPoolType.CantripOrSpell,
                replacement.Tag, replacement.PreviousSpells.Length);
            var pool = pools[replacement.Tag];
            pool.spellListOverride = replacement.DisplayFeature.SpellListDefinition;
            pool.minSpellLevel = replacement.PreviousSpells.Min(spell => spell.SpellLevel);
            pool.maxSpellLevel = replacement.PreviousSpells.Max(spell => spell.SpellLevel);
            replacement.Reset(data);
        }
    }

    internal static FeatSpellReplacement GetFeatSpellReplacement(CharacterHeroBuildingData data, string tag)
    {
        return data?.HeroCharacter != null && tag != null &&
               LevelUpTab.TryGetValue(data.HeroCharacter, out var levelUpData) && levelUpData.IsLevelingUp &&
               data.HeroCharacter.ClassesHistory.Count > levelUpData.InitialCharacterLevel
            ? levelUpData.FeatSpellReplacements.FirstOrDefault(replacement => replacement.Tag == tag)
            : null;
    }

    internal static bool AreFeatSpellReplacementsValid(CharacterHeroBuildingData data)
    {
        return data?.HeroCharacter == null || !LevelUpTab.TryGetValue(data.HeroCharacter, out var levelUpData) ||
               levelUpData.FeatSpellReplacements.All(replacement =>
                   !data.PointPoolStacks[HeroDefinitions.PointsPoolType.CantripOrSpell].ActivePools
                       .ContainsKey(replacement.Tag) || replacement.IsValid(data));
    }

    internal static void FinalizeFeatSpellReplacements(RulesetCharacterHero hero)
    {
        if (!LevelUpTab.TryGetValue(hero, out var levelUpData) || !levelUpData.IsLevelingUp ||
            hero.ClassesHistory.Count <= levelUpData.InitialCharacterLevel)
        {
            return;
        }

        var data = hero.GetHeroBuildingData();
        foreach (var replacement in levelUpData.FeatSpellReplacements.Where(replacement => replacement.IsValid(data)))
        {
            replacement.Apply(hero, data);
        }
    }

    internal sealed class FeatSpellReplacement
    {
        private readonly Dictionary<FeatureDefinitionCastSpell, SpellDefinition[]> _previousByFeature;
        private readonly SpellSelectionByLevel _selection;

        internal FeatSpellReplacement(FeatDefinition feat, RulesetSpellRepertoire[] repertoires)
        {
            Feat = feat;
            Tag = $"{AttributeDefinitions.TagClass}FeatSpellReplacement{feat.Name}";
            _previousByFeature = repertoires.ToDictionary(repertoire => repertoire.SpellCastingFeature,
                repertoire => repertoire.KnownCantrips.Concat(repertoire.KnownSpells).Distinct().ToArray());
            PreviousSpells = _previousByFeature.Values.SelectMany(spells => spells).ToArray();
            DisplayFeature = repertoires.OrderByDescending(repertoire =>
                repertoire.KnownSpells.Select(spell => spell.SpellLevel).DefaultIfEmpty(0).Max())
                .First().SpellCastingFeature;
            _selection = new SpellSelectionByLevel(IsEligible, PreviousSpells, 1,
                PreviousSpells.Select(spell => spell.SpellLevel).ToArray());
        }

        internal FeatDefinition Feat { get; }
        internal string Tag { get; }
        internal FeatureDefinitionCastSpell DisplayFeature { get; }
        internal SpellDefinition[] PreviousSpells { get; }

        internal string FormatSourceTitle()
        {
            var sourceClass = DisplayFeature.GetFirstSubFeatureOfType<ClassHolder>()?.Class;
            return sourceClass
                ? Gui.Format("Feat/&GeneralFeat2024VariantTitle", DisplayFeature.FormatTitle(), sourceClass.FormatTitle())
                : Feat.FormatTitle();
        }

        internal SpellDefinition[] GetSelected(CharacterHeroBuildingData data)
        {
            data.AcquiredCantrips.TryGetValue(Tag, out var cantrips);
            data.AcquiredSpells.TryGetValue(Tag, out var spells);
            return (cantrips ?? []).Concat(spells ?? []).ToArray();
        }

        internal bool IsEligible(SpellDefinition spell)
        {
            // Preserve saved choices when a content list is disabled; new choices must belong to the original list.
            return PreviousSpells.Contains(spell) || spell != null && _previousByFeature.Any(pair =>
                pair.Value.Any(previous => previous.SpellLevel == spell.SpellLevel) &&
                pair.Key.SpellListDefinition.SpellsByLevel.Any(level =>
                    level.Level == spell.SpellLevel && level.Spells.Contains(spell)));
        }

        internal bool CanSelect(CharacterHeroBuildingData data, SpellDefinition spell)
        {
            return _selection.CanSelectSpell(GetSelected(data), spell);
        }

        internal bool IsValid(CharacterHeroBuildingData data)
        {
            return _selection.IsValidSelection(GetSelected(data));
        }

        internal void Reset(CharacterHeroBuildingData data)
        {
            data.AcquiredCantrips[Tag] = PreviousSpells.Where(spell => spell.SpellLevel == 0).ToList();
            data.AcquiredSpells[Tag] = PreviousSpells.Where(spell => spell.SpellLevel > 0).ToList();
            if (data.PointPoolStacks[HeroDefinitions.PointsPoolType.CantripOrSpell].ActivePools
                .TryGetValue(Tag, out var pool))
            {
                pool.remainingPoints = 0;
            }
        }

        internal void Apply(RulesetCharacterHero hero, CharacterHeroBuildingData data)
        {
            var selected = GetSelected(data);
            var removed = PreviousSpells.Except(selected).ToArray();
            var added = selected.Except(PreviousSpells).ToArray();
            if (removed.Length != 1 || added.Length != 1)
            {
                return;
            }

            var feature = _previousByFeature.First(pair => pair.Value.Contains(removed[0])).Key;
            var repertoire = hero.SpellRepertoires.FirstOrDefault(entry => entry.SpellCastingFeature == feature);
            if (repertoire == null)
            {
                return;
            }

            // Only selected spell identities change. Slot usage and the other feat repertoires stay intact.
            repertoire.KnownCantrips.Remove(removed[0]);
            repertoire.KnownSpells.Remove(removed[0]);
            if (repertoire.PreparedSpells.Remove(removed[0]))
            {
                repertoire.PreparedSpells.TryAdd(added[0]);
            }

            if (added[0].SpellLevel == 0)
            {
                hero.GrantCantrip(added[0], feature);
            }
            else
            {
                hero.GrantSpell(added[0], feature);
            }
        }
    }


    internal static void SortHeroRepertoires(RulesetCharacterHero hero)
    {
        if (hero.SpellRepertoires.Count <= 1)
        {
            return;
        }

        static int GetGroup(RulesetSpellRepertoire repertoire)
        {
            var feature = repertoire?.SpellCastingFeature;
            if (feature?.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null)
            {
                return 2;
            }

            return feature?.SpellCastingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Race
                or FeatureDefinitionCastSpell.CastingOrigin.Monster ? 0 : 1;
        }

        hero.SpellRepertoires.Sort((a, b) =>
        {
            var comparison = GetGroup(a).CompareTo(GetGroup(b));
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = a.SaveDC.CompareTo(b.SaveDC);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.Compare(a.FormatHeader(), b.FormatHeader(), StringComparison.CurrentCultureIgnoreCase);
            return comparison != 0
                ? comparison
                : string.Compare(a.SpellCastingFeature?.Name, b.SpellCastingFeature?.Name, StringComparison.Ordinal);
        });
    }


    internal static void RecursiveGrantCustomFeatures(
        RulesetCharacterHero hero,
        string tag,
        [NotNull] List<FeatureDefinition> features)
    {
        foreach (var grantedFeature in features)
        {
            foreach (var customCode in grantedFeature.GetAllSubFeaturesOfType<ICustomLevelUpLogic>())
            {
                customCode.ApplyFeature(hero, tag);
            }

            switch (grantedFeature)
            {
                case FeatureDefinitionFeatureSet
                {
                    Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Union
                } featureDefinitionFeatureSet:
                    RecursiveGrantCustomFeatures(hero, tag, featureDefinitionFeatureSet.FeatureSet);
                    break;

                case FeatureDefinitionProficiency
                {
                    ProficiencyType: ProficiencyType.FightingStyle
                } featureDefinitionProficiency:
                    featureDefinitionProficiency.Proficiencies
                        .ForEach(prof =>
                            hero.TrainedFightingStyles
                                .Add(DatabaseHelper.GetDefinition<FightingStyleDefinition>(prof)));
                    break;
                case FeatureDefinitionProficiency
                {
                    ProficiencyType: ProficiencyType.Feat
                } featureDefinitionProficiency:
                    featureDefinitionProficiency.Proficiencies
                        .ForEach(prof =>
                            hero.TrainedFeats
                                .Add(DatabaseHelper.GetDefinition<FeatDefinition>(prof)));
                    break;
            }
        }
    }

    internal static void RecursiveRemoveCustomFeatures(
        RulesetCharacterHero hero,
        string tag,
        [NotNull] List<FeatureDefinition> features)
    {
        foreach (var grantedFeature in features)
        {
            foreach (var customCode in grantedFeature.GetAllSubFeaturesOfType<ICustomLevelUpLogic>())
            {
                customCode.RemoveFeature(hero, tag);
            }

            switch (grantedFeature)
            {
                case FeatureDefinitionFeatureSet
                {
                    Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Union
                } featureDefinitionFeatureSet:
                    // Fix a typo
                    RecursiveRemoveCustomFeatures(hero, tag, featureDefinitionFeatureSet.FeatureSet);
                    break;

                case FeatureDefinitionProficiency
                {
                    ProficiencyType: ProficiencyType.FightingStyle
                } featureDefinitionProficiency:
                    featureDefinitionProficiency.Proficiencies
                        .ForEach(prof =>
                            hero.TrainedFightingStyles
                                .Remove(DatabaseHelper.GetDefinition<FightingStyleDefinition>(prof)));
                    break;
                case FeatureDefinitionProficiency
                {
                    ProficiencyType: ProficiencyType.Feat
                } featureDefinitionProficiency:
                    featureDefinitionProficiency.Proficiencies
                        .ForEach(prof =>
                            hero.TrainedFeats
                                .Remove(DatabaseHelper.GetDefinition<FeatDefinition>(prof)));
                    break;
            }
        }
    }

    internal static void GrantCustomFeatures(RulesetCharacterHero hero)
    {
        var buildingData = hero.GetHeroBuildingData();
        var selectedClass = GetSelectedClass(hero);
        var selectedSubclass = GetSelectedSubclass(hero);
        var level = hero.ClassesHistory.Count(x => x == selectedClass);

        foreach (var kvp in buildingData.LevelupTrainedFeats)
        {
            foreach (var feat in kvp.Value)
            {
                foreach (var customCode in feat.GetAllSubFeaturesOfType<ICustomLevelUpLogic>())
                {
                    customCode.ApplyFeature(hero, kvp.Key);
                }

                RecursiveGrantCustomFeatures(hero, kvp.Key, feat.Features);
            }
        }

        foreach (var kvp in buildingData.LevelupTrainedInvocations)
        {
            foreach (var invocation in kvp.Value)
            {
                RecursiveGrantCustomFeatures(hero, kvp.Key, [invocation.grantedFeature]);
            }
        }

        var classTag = AttributeDefinitions.GetClassTag(selectedClass, level);

        if (hero.ActiveFeatures.TryGetValue(classTag, out var classFeatures))
        {
            RecursiveGrantCustomFeatures(hero, classTag, classFeatures);
        }

        if (!selectedSubclass)
        {
            return;
        }

        var subclassTag = AttributeDefinitions.GetSubclassTag(selectedClass, level, selectedSubclass);

        if (hero.ActiveFeatures.TryGetValue(subclassTag, out var subclassFeatures))
        {
            RecursiveGrantCustomFeatures(hero, classTag, subclassFeatures);
        }
    }

    internal static void EnumerateKnownAndAcquiredSpells(
        [NotNull] CharacterHeroBuildingData heroBuildingData,
        List<SpellDefinition> __result)
    {
        var hero = heroBuildingData.HeroCharacter;
        var isMulticlass = IsMulticlass(hero);

        if (!isMulticlass)
        {
            return;
        }

        if (Main.Settings.EnableRelearnSpells)
        {
            var otherClassesKnownSpells = GetOtherClassesKnownSpells(hero);

            __result.RemoveAll(x => otherClassesKnownSpells.ContainsKey(x));
        }
        else
        {
            var allowedSpells = GetAllowedSpells(hero);

            __result.RemoveAll(x => !allowedSpells.Contains(x));
        }
    }

    [NotNull]
    internal static CharacterClassDefinition GetClassForSubclass(CharacterSubclassDefinition subclass)
    {
        return DatabaseRepository.GetDatabase<CharacterClassDefinition>().FirstOrDefault(klass =>
        {
            return klass.FeatureUnlocks.Any(unlock =>
            {
                if (unlock.FeatureDefinition is FeatureDefinitionSubclassChoice subclassChoice)
                {
                    return subclassChoice.Subclasses.Contains(subclass.Name);
                }

                return false;
            });
        })!;
    }

    public static void GrantCustomFeaturesFromFeats(RulesetCharacterHero hero)
    {
        var data = hero.GetOrCreateHeroBuildingData();

        foreach (var pair in data.levelupTrainedFeats)
        {
            //Grant invocations from feat features
            var features = pair.Value.SelectMany(f => f.Features).ToArray();

            FeatureDefinitionGrantInvocations.GrantInvocations(hero, pair.Key, features);

            foreach (var castSpell in features.OfType<FeatureDefinitionCastSpell>())
            {
                hero.GrantSpellRepertoire(castSpell, null, null, null);
            }
        }
    }

    internal static void RebuildCharacterStageProficiencyPanel(bool levelingUp)
    {
        CharacterStagePanel characterStagePanel = null;

        if (levelingUp)
        {
            var screen = Gui.GuiService.GetScreen<CharacterLevelUpScreen>();

            if (screen && screen.Visible)
            {
                characterStagePanel = screen.CurrentStagePanel;
            }
        }
        else
        {
            var screen = Gui.GuiService.GetScreen<CharacterCreationScreen>();

            if (screen && screen.Visible)
            {
                characterStagePanel = screen.CurrentStagePanel;
            }
        }

        if (characterStagePanel is not CharacterStageProficiencySelectionPanel characterStageProficiencySelectionPanel)
        {
            return;
        }

        Gui.ReleaseChildrenToPool(characterStageProficiencySelectionPanel.learnStepsTable);
        characterStageProficiencySelectionPanel.CollectTags();
        characterStageProficiencySelectionPanel.BuildLearnSteps();
    }

    // keeps the multiclass level up context
    private sealed class LevelUpData
    {
        internal RulesetCharacterHero Hero;
        internal CharacterClassDefinition SelectedClass;
        internal CharacterSubclassDefinition SelectedSubclass;
        internal int InitialCharacterLevel;
        internal List<FeatSpellReplacement> FeatSpellReplacements = [];

        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool IsClassSelectionStage { get; set; }

        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool IsLevelingUp { get; set; }

        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool RequiresDeity { get; set; }
        internal HashSet<ItemDefinition> GrantedItems { get; set; } = [];

        private IEnumerable<FeatureDefinition> SelectedClassFeatures => Hero.ActiveFeatures
            .Where(x => x.Key.Contains(SelectedClass.Name))
            .SelectMany(x => x.Value);

        internal HashSet<SpellDefinition> AllowedSpells => CacheAllowedSpells(SelectedClassFeatures);

        internal HashSet<SpellDefinition> AllowedAutoPreparedSpells =>
            CacheAllowedAutoPreparedSpells(SelectedClassFeatures);

        internal Dictionary<SpellDefinition, string> OtherClassesKnownSpells => CacheOtherClassesKnownSpells(Hero);
    }
}
