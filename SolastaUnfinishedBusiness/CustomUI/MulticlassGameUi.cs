using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Patches;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class MulticlassGameUi
{
    private static readonly float[] FontSizes = [17f, 17f, 16f, 14.75f, 13.5f, 13.5f, 13.5f];

    private static Sprite _regularSlotSprite;

    private static Sprite _pactSlotSprite;

    private static Sprite RegularSlotSprite => _regularSlotSprite ??= Resources
        // ReSharper disable once Unity.UnknownResource
        .Load<GameObject>("Gui/Prefabs/Location/Magic/SlotStatus")
        .GetComponent<SlotStatus>().Available
        .GetComponent<Image>().sprite;

    private static Sprite PactSlotSprite => _pactSlotSprite ??= Resources
        // ReSharper disable once Unity.UnknownResource
        .Load<GameObject>("Gui/Prefabs/Location/Magic/SlotStatusWarlock")
        .GetComponent<SlotStatus>().Available
        .GetComponent<Image>().sprite;

    private static void SetSlotState([NotNull] SlotStatus component, bool? used)
    {
        component.Used.gameObject.SetActive(used == true);
        component.Available.gameObject.SetActive(used == false);
    }

    internal static float GetFontSize(int classesCount)
    {
        return FontSizes[classesCount % (MulticlassContext.MaxClasses + 1)];
    }

    private static void SetRegularSlotImage(Image img)
    {
        img.sprite = RegularSlotSprite;
    }

    private static void SetPactSlotImage(Image img)
    {
        img.sprite = PactSlotSprite;
    }

    internal static void SetupLevelUpClassSelectionStep(CharacterEditionScreen characterEditionScreen)
    {
        if (!Main.Settings.EnableMulticlass || characterEditionScreen is not CharacterLevelUpScreen)
        {
            return;
        }

        var characterCreationScreen = Gui.GuiService.GetScreen<CharacterCreationScreen>();
        var stagePanelPrefabs = characterCreationScreen.stagePanelPrefabs;
        var classSelectionPanel = Gui
            .GetPrefabFromPool(stagePanelPrefabs[1], characterEditionScreen.StagesPanelContainer)
            .GetComponent<CharacterStagePanel>();
        var deitySelectionPanel = Gui
            .GetPrefabFromPool(stagePanelPrefabs[2], characterEditionScreen.StagesPanelContainer)
            .GetComponent<CharacterStagePanel>();
        var newLevelUpSequence =
            new Dictionary<string, CharacterStagePanel> { { "ClassSelection", classSelectionPanel } };

        foreach (var stagePanel in characterEditionScreen.stagePanelsByName)
        {
            newLevelUpSequence.Add(stagePanel.Key, stagePanel.Value);

            if (stagePanel.Key == "LevelGains")
            {
                newLevelUpSequence.Add("DeitySelection", deitySelectionPanel);
            }
        }

        characterEditionScreen.stagePanelsByName = newLevelUpSequence;
    }

    internal static void RebuildSlotsTable(SpellRepertoirePanel __instance)
    {
        var spellRepertoire = __instance.SpellRepertoire;
        var character = __instance.GuiCharacter.RulesetCharacter;
        var isMulticaster = SharedSpellsContext.IsMulticaster(character);
        var sharedSpellLevel = SharedSpellsContext.GetSharedSpellLevel(character);
        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);
        var classSpellLevel = spellRepertoire.spellCastingRace
            ? spellRepertoire.MaxSpellLevelOfSpellCastingLevel
            : SharedSpellsContext.MaxSpellLevelOfSpellCastingLevel(spellRepertoire);

        SharedSpellsContext.FactorMysticArcanum(character, spellRepertoire, ref classSpellLevel);

        var slotLevel = !isMulticaster ? classSpellLevel : Math.Max(sharedSpellLevel, warlockSpellLevel);
        var accountForCantrips = spellRepertoire.KnownCantrips.Count > 0 ? 1 : 0;

        while (__instance.levelButtonsTable.childCount < classSpellLevel + accountForCantrips)
        {
            Gui.GetPrefabFromPool(__instance.levelButtonPrefab, __instance.levelButtonsTable);

            var index = __instance.levelButtonsTable.childCount - 1;
            var child = __instance.levelButtonsTable.GetChild(index);

            child.GetComponent<SpellLevelButton>().Bind(index, __instance.LevelSelected);
        }

        while (__instance.levelButtonsTable.childCount > classSpellLevel + accountForCantrips)
        {
            Gui.ReleaseInstanceToPool(
                __instance.levelButtonsTable.GetChild(__instance.levelButtonsTable.childCount - 1).gameObject);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.levelButtonsTable);

        // patches the panel to display higher level spell slots from shared slots table
        // but hide the spell panels if class level not there yet
        for (var i = 0; i < __instance.spellsByLevelTable.childCount; i++)
        {
            var spellsByLevel = __instance.spellsByLevelTable.GetChild(i);

            for (var j = 0; j < spellsByLevel.childCount; j++)
            {
                var transform = spellsByLevel.GetChild(j);

                if (transform.TryGetComponent(typeof(SlotStatusTable), out _))
                {
                    transform.gameObject.SetActive(i < slotLevel + accountForCantrips); // table header (with slots)
                }
                else
                {
                    transform.gameObject.SetActive(i < classSpellLevel + accountForCantrips); // table content
                }
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.spellsByLevelTable);
    }

    internal static void PaintPactSlots(
        [NotNull] RulesetCharacter character,
        int totalSlotsCount,
        int totalSlotsRemainingCount,
        int slotLevel,
        int spellsAtLevel,
        SlotStatusTable slotStatusTable,
        bool ignorePactSlots = false)
    {
        var rectTransform = slotStatusTable.table;
        var warlockSpellRepertoire = SharedSpellsContext.GetWarlockSpellRepertoire(character);
        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);

        var pactSlotsCount = 0;
        var pactSlotsRemainingCount = 0;
        var pactSlotsUsedCount = 0;

        if (warlockSpellRepertoire != null)
        {
            pactSlotsCount = SharedSpellsContext.GetWarlockMaxSlots(character);
            pactSlotsUsedCount = SharedSpellsContext.GetWarlockUsedSlots(character);
            pactSlotsRemainingCount = pactSlotsCount - pactSlotsUsedCount;
        }

        var spellSlotsCount = totalSlotsCount - pactSlotsCount;
        var spellSlotsRemainingCount = totalSlotsRemainingCount - pactSlotsRemainingCount;
        var spellSlotsUsedCount = spellSlotsCount - spellSlotsRemainingCount;

        for (var index = 0; index < rectTransform.childCount; ++index)
        {
            var component = rectTransform.GetChild(index).GetComponent<SlotStatus>();

            // only tweak levels that have both spell and pact slots
            if (slotLevel <= warlockSpellLevel)
            {
                // these are spell slots that display after pact ones
                if (index >= pactSlotsCount)
                {
                    var used = index >= pactSlotsCount + spellSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                // these are pact slots that should only display at their highest level
                else if (!ignorePactSlots && slotLevel == warlockSpellLevel)
                {
                    var used = index >= totalSlotsRemainingCount - spellSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                else
                {
                    SetSlotState(component, null);
                }
            }

            // paint spell slots white
            if (index >= pactSlotsCount || slotLevel > warlockSpellLevel)
            {
                //PATCH: support display cost on spell level blocks (SPELL_POINTS)
                if (character.IsSpellPointsEnabled())
                {
                    SpellPointsContext.DisplayCostOnSpellLevelBlocks(slotStatusTable, component, slotLevel,
                        spellsAtLevel);
                }
                else
                {
                    SetRegularSlotImage(component.Available.GetComponent<Image>());
                }
            }
            else
            {
                SetPactSlotImage(component.Available.GetComponent<Image>());
            }
        }

        string str;

        if (totalSlotsRemainingCount == 0)
        {
            str = "Screen/&SpellSlotsUsedAllDescription";
        }
        else if (totalSlotsRemainingCount == totalSlotsCount)
        {
            str = "Screen/&SpellSlotsUsedNoneDescription";
        }
        else if (pactSlotsRemainingCount == pactSlotsCount)
        {
            str = Gui.Format("Screen/&SpellSlotsUsedLongDescription", spellSlotsUsedCount.ToString());
        }
        else if (spellSlotsRemainingCount == spellSlotsCount)
        {
            str = Gui.Format("Screen/&SpellSlotsUsedShortDescription", pactSlotsUsedCount.ToString());
        }
        else
        {
            str = Gui.Format("Screen/&SpellSlotsUsedShortLongDescription", pactSlotsUsedCount.ToString(),
                spellSlotsUsedCount.ToString());
        }

        rectTransform.GetComponent<GuiTooltip>().Content = str;
    }

    // reaction and flexible panels should paint white slots first
    internal static void PaintPactSlotsAlternate(
        [NotNull] RulesetCharacter character,
        int totalSlotsCount,
        int totalSlotsRemainingCount,
        int slotLevel,
        [NotNull] RectTransform rectTransform)
    {
        var warlockSpellRepertoire =
            SharedSpellsContext.GetWarlockSpellRepertoire(character);
        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);
        var pactSlotsCount = 0;
        var pactSlotsRemainingCount = 0;

        if (warlockSpellRepertoire != null)
        {
            var pactSlotsUsedCount = SharedSpellsContext.GetWarlockUsedSlots(character);

            pactSlotsCount = SharedSpellsContext.GetWarlockMaxSlots(character);
            pactSlotsRemainingCount = pactSlotsCount - pactSlotsUsedCount;
        }

        var spellSlotsCount = totalSlotsCount - pactSlotsCount;

        for (var index = 0; index < rectTransform.childCount; ++index)
        {
            var component = rectTransform.GetChild(index).GetComponent<SlotStatus>();

            if (slotLevel <= warlockSpellLevel)
            {
                if (index < spellSlotsCount)
                {
                    var used = index >= totalSlotsRemainingCount - pactSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                else if (slotLevel == warlockSpellLevel)
                {
                    var used = index >= spellSlotsCount + pactSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                else
                {
                    SetSlotState(component, null);
                }
            }

            if (index >= spellSlotsCount && slotLevel <= warlockSpellLevel)
            {
                SetPactSlotImage(component.Available.GetComponent<Image>());
            }
            else
            {
                //PATCH: support alternate spell system to avoid displaying spell slots on selection (SPELL_POINTS)
                if (character.IsSpellPointsEnabled())
                {
                    SetSlotState(component, null);
                }
                else
                {
                    SetRegularSlotImage(component.Available.GetComponent<Image>());
                }
            }
        }
    }

    internal static void PaintSlotsWhite([NotNull] RectTransform rectTransform)
    {
        for (var index = 0; index < rectTransform.childCount; ++index)
        {
            var child = rectTransform.GetChild(index);
            var component = child.GetComponent<SlotStatus>();

            SetRegularSlotImage(component.Available.GetComponent<Image>());
        }
    }

    /**Adds available slot level options to optionsAvailability and returns index of pre-picked option, or -1*/
    internal static int AddAvailableSubLevels(
        Dictionary<int, bool> optionsAvailability,
        RulesetCharacter character,
        [NotNull] RulesetSpellRepertoire spellRepertoire, int minSpellLevel = 1, int maxSpellLevel = 0,
        SpellDefinition spellDefinition = null)
    {
        var availableSlotLevels = new List<int>();

        if (maxSpellLevel == 0)
        {
            maxSpellLevel = Math.Max(
                SharedSpellsContext.GetSharedSpellLevel(character),
                SharedSpellsContext.GetWarlockSpellLevel(character));
        }

        for (var level = minSpellLevel; level <= maxSpellLevel; ++level)
        {
            if (!spellRepertoire.TryGetAvailableSlotLevel(
                    character,
                    level,
                    spellDefinition,
                    out var isAvailable))
            {
                continue;
            }

            optionsAvailability.Add(level, isAvailable);

            if (isAvailable)
            {
                availableSlotLevels.Add(level);
            }
        }

        var selectedLevel = spellRepertoire.GetPreferredSlotLevel(character, availableSlotLevels);

        if (selectedLevel == 0)
        {
            return -1;
        }

        var option = 0;

        foreach (var slotLevel in optionsAvailability.Keys)
        {
            if (slotLevel == selectedLevel)
            {
                return option;
            }

            option++;
        }

        return -1;
    }

    [CanBeNull]
    internal static string GetAllClassesLabel([CanBeNull] GuiCharacter character, char separator)
    {
        var dbCharacterClassDefinition = DatabaseRepository.GetDatabase<CharacterClassDefinition>();
        var builder = new StringBuilder();
        var snapshot = character?.Snapshot;
        var hero = character?.RulesetCharacterHero;
        var duplicate = character?.RulesetCharacter as RulesetCharacterSimulacrum;

        if (duplicate != null &&
            SimulacrumBehavior.TryGetClassLevels(duplicate, out var duplicateClasses) &&
            duplicateClasses.Count > 0)
        {
            var classesCount = duplicateClasses.Count;

            if (classesCount == 1)
            {
                var classDefinition = duplicateClasses[0].ClassDefinition;

                builder
                    .Append(classDefinition.FormatTitle())
                    .Append(separator);

                if (SimulacrumBehavior.TryGetPrimarySubclass(
                        duplicate,
                        classDefinition,
                        out var subclassDefinition))
                {
                    builder
                        .Append(subclassDefinition.FormatTitle())
                        .Append(separator);
                }

                return builder.ToString().Remove(builder.Length - 1, 1);
            }

            var newLine = separator == '\n' || classesCount <= 4 ? 2 : 3;
            var index = 0;

            foreach (var classLevel in duplicateClasses
                         .OrderByDescending(entry => entry.Level)
                         .ThenBy(entry => entry.ClassDefinition.FormatTitle()))
            {
                builder
                    .Append(classLevel.ClassDefinition.FormatTitle())
                    .Append('/')
                    .Append(classLevel.Level);

                if (classesCount <= 3)
                {
                    builder.Append(separator);
                }
                else
                {
                    builder.Append(' ');
                    index++;

                    if (index % newLine == 0)
                    {
                        builder.Append('\n');
                    }
                }
            }
        }
        else if (snapshot != null && snapshot.Classes.Length > 1)
        {
            foreach (var className in snapshot.Classes)
            {
                var classTitle = dbCharacterClassDefinition.GetElement(className).FormatTitle();

                builder
                    .Append(classTitle)
                    .Append(separator);
            }
        }
        else if (hero != null && hero.ClassesAndLevels.Count > 1)
        {
            var i = 0;
            var classesCount = hero.ClassesAndLevels.Count;
            var newLine = separator == '\n' || classesCount <= 4 ? 2 : 3;
            var sortedClasses = hero.ClassesAndLevels
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key.FormatTitle());

            foreach (var kvp in sortedClasses)
            {
                builder
                    .Append(kvp.Key.FormatTitle())
                    .Append('/')
                    .Append(kvp.Value);

                if (classesCount <= 3)
                {
                    builder
                        .Append(separator);
                }
                else
                {
                    builder
                        .Append(' ');

                    i++;

                    if (i % newLine == 0)
                    {
                        builder
                            .Append('\n');
                    }
                }
            }
        }
        else
        {
            return null;
        }

        return builder.ToString().Remove(builder.Length - 1, 1);
    }

    [NotNull]
    internal static string GetAllClassesHitDiceLabel([NotNull] GuiCharacter character, out int dieTypeCount)
    {
        // Assert.IsNotNull(character, nameof(character));

        var builder = new StringBuilder();
        var hero = character.RulesetCharacterHero;
        var dieTypesCount = new Dictionary<DieType, int>();
        const char SEPARATOR = ' ';

        foreach (var characterClassDefinition in hero.ClassesAndLevels.Keys)
        {
            if (!dieTypesCount.ContainsKey(characterClassDefinition.HitDice))
            {
                dieTypesCount.Add(characterClassDefinition.HitDice, 0);
            }

            dieTypesCount[characterClassDefinition.HitDice] += hero.ClassesAndLevels[characterClassDefinition];
        }

        foreach (var dieType in dieTypesCount.Keys)
        {
            builder
                .Append(dieTypesCount[dieType])
                .Append(Gui.GetDieSymbol(dieType))
                .Append(SEPARATOR);
        }

        dieTypeCount = dieTypesCount.Count;

        return builder.Remove(builder.Length - 1, 1).ToString();
    }

    internal static void SpellsByLevelGroupBindLearning(
        [NotNull] SpellsByLevelGroup group,
        [NotNull] ICharacterBuildingService characterBuildingService,
        FeatureDefinitionCastSpell spellFeature,
        [NotNull] SpellListDefinition spellListDefinition,
        List<string> restrictedSchools,
        bool ritualOnly,
        int spellLevel,
        SpellBox.SpellBoxChangedHandler spellBoxChanged,
        List<SpellDefinition> knownSpells,
        List<SpellDefinition> unlearnedSpells,
        [NotNull] string spellTag,
        bool canAcquireSpells,
        bool unlearn,
        RectTransform tooltipAnchor,
        TooltipDefinitions.AnchorMode anchorMode,
        CharacterStageSpellSelectionPanel panel)
    {
        var localHeroCharacter = panel.currentHero;
        var heroBuildingData = localHeroCharacter.GetHeroBuildingData();
        var pointPool = GetCurrentPool(panel, characterBuildingService, heroBuildingData, spellFeature);
        var effectiveSpellListDefinition = pointPool?.spellListOverride ?? spellListDefinition;
        var effectiveRitualOnly = ritualOnly || pointPool?.ritualOnly == true;
        var useDedicatedTouchedSpellList =
            Tabletop2024Context.UsesDedicatedTouchedSpellSelectionList2024(
                spellFeature,
                effectiveSpellListDefinition,
                spellTag);

        group.extraSpellsMap.Clear();
        group.spellsTable.gameObject.SetActive(true);
        group.slotStatusTable.gameObject.SetActive(true);
        group.SpellLevel = spellLevel;

        if (spellFeature)
        {
            foreach (var spellRepertoire in localHeroCharacter.SpellRepertoires
                         .Where(spellRepertoire => spellRepertoire.SpellCastingFeature == spellFeature))
            {
                spellRepertoire.EnumerateExtraSpellsOfLevel(group.SpellLevel, group.extraSpellsMap);
                break;
            }
        }

        var allSpells = effectiveSpellListDefinition
            .SpellsByLevel[effectiveSpellListDefinition.HasCantrips ? spellLevel : spellLevel - 1]
            .Spells
            .Where(spell => restrictedSchools.Count == 0 || restrictedSchools.Contains(spell.SchoolOfMagic))
            .Where(spell => !effectiveRitualOnly || spell.Ritual)
            .ToList();

        if (!useDedicatedTouchedSpellList)
        {
            allSpells.AddRange(characterBuildingService
                .EnumerateKnownAndAcquiredSpells(heroBuildingData, string.Empty)
                .Where(s => s.SpellLevel == spellLevel && !allSpells.Contains(s))
                .Where(spell => !effectiveRitualOnly || spell.Ritual)
            );

            if (!spellTag.Contains(AttributeDefinitions.TagRace)) // this is a patch over original TA code
            {
                localHeroCharacter.EnumerateFeaturesToBrowse<FeatureDefinitionMagicAffinity>(group.features);

                foreach (var spell in from FeatureDefinitionMagicAffinity feature in @group.features
                         where feature.ExtendedSpellList
                         from spell in feature.ExtendedSpellList
                             .SpellsByLevel[effectiveSpellListDefinition.HasCantrips ? spellLevel : spellLevel - 1]
                             .Spells
                         where !allSpells.Contains(spell) && (!effectiveRitualOnly || spell.Ritual) &&
                               (restrictedSchools.Count == 0 || restrictedSchools.Contains(spell.SchoolOfMagic))
                         select spell)
                {
                    allSpells.Add(spell);
                }
            }
        }

        var tagBySpell = new Dictionary<SpellDefinition, string>();

        group.autoPreparedSpells.Clear();

        if (!useDedicatedTouchedSpellList && group.SpellLevel > 0)
        {
            localHeroCharacter
                .EnumerateFeaturesToBrowse<FeatureDefinitionAutoPreparedSpells>(group.features);

            foreach (var featureDefinitionAutoPreparedSpells in group.features
                         .OfType<FeatureDefinitionAutoPreparedSpells>())
            {
                var maxLevel =
                    LevelUpHelper.GetMaxAutoPrepSpellsLevel(localHeroCharacter, featureDefinitionAutoPreparedSpells);

                foreach (var spells in featureDefinitionAutoPreparedSpells.AutoPreparedSpellsGroups
                             .SelectMany(preparedSpellsGroup => preparedSpellsGroup.SpellsList
                                 .Where(spell => spell.SpellLevel <= maxLevel)
                                 .Where(spell => spell.SpellLevel == group.SpellLevel)))
                {
                    group.autoPreparedSpells.Add(spells);
                    tagBySpell.TryAdd(spells, featureDefinitionAutoPreparedSpells.AutoPreparedTag);
                }

                foreach (var autoPreparedSpell in group.autoPreparedSpells
                             .Where(spell => !allSpells.Contains(spell))
                             .Where(spell => !effectiveRitualOnly || spell.Ritual))
                {
                    allSpells.Add(autoPreparedSpell);
                }
            }
        }

        var service = ServiceRepository.GetService<IGamingPlatformService>();

        for (var index = allSpells.Count - 1; index >= 0; --index)
        {
            if (!service.IsContentPackAvailable(allSpells[index].ContentPack))
            {
                allSpells.RemoveAt(index);
            }
        }

        if (!useDedicatedTouchedSpellList &&
            !spellTag.Contains(AttributeDefinitions.TagRace)) // this is a patch over original TA code
        {
            CollectAllAutoPreparedSpells(group, localHeroCharacter, allSpells, group.autoPreparedSpells);

            FilterMulticlassBleeding(group, localHeroCharacter, allSpells, group.autoPreparedSpells, pointPool,
                group.extraSpellsMap);
        }

        if (!useDedicatedTouchedSpellList)
        {
            //Properly tag and not allow to pick spells that are auto-prepared from various features
            LevelUpHelper.EnumerateExtraSpells(group.extraSpellsMap, localHeroCharacter);

            // this is required to support when other caster is whole list
            var keys = group.extraSpellsMap.Keys
                .Where(x => x.SpellLevel == group.SpellLevel && !allSpells.Contains(x));

            group.autoPreparedSpells.AddRange(keys);
        }

        NormalizeSpellSourceTags(tagBySpell);
        NormalizeSpellSourceTags(group.extraSpellsMap);

        group.CommonBind(null, unlearn ? SpellBox.BindMode.Unlearn : SpellBox.BindMode.Learning, spellBoxChanged,
            allSpells, null, null, group.autoPreparedSpells, unlearnedSpells, tagBySpell,
            group.extraSpellsMap, tooltipAnchor, anchorMode);

        if (unlearn)
        {
            group.RefreshUnlearning(characterBuildingService, knownSpells, unlearnedSpells, [],
                spellTag,
                canAcquireSpells && spellLevel > 0);
        }
        else
        {
            group.RefreshLearning(characterBuildingService, knownSpells, unlearnedSpells, spellTag,
                canAcquireSpells);
        }

        group.slotStatusTable.Bind(null, spellLevel, false, null, false);
    }

    private static PointPool GetCurrentPool(
        CharacterStageSpellSelectionPanel panel,
        ICharacterBuildingService characterBuildingService,
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinitionCastSpell spellFeature)
    {
        var tag = string.Empty;

        for (var index = 0; index < panel.learnStepsTable.childCount; ++index)
        {
            var child = panel.learnStepsTable.GetChild(index);

            if (!child.gameObject.activeSelf)
            {
                continue;
            }

            var component = child.GetComponent<LearnStepItem>();
            var status = index != panel.currentLearnStep
                ? index != panel.currentLearnStep - 1
                    ? LearnStepItem.Status.Locked
                    : LearnStepItem.Status.Previous
                : LearnStepItem.Status.InProgress;

            if (status == LearnStepItem.Status.InProgress)
            {
                tag = component.Tag;
            }
        }

        HeroDefinitions.PointsPoolType poolType;

        if (panel.IsFinalStep)
        {
            tag = panel.allTags[panel.allTags.Count - 1];
            poolType = panel.GetPoolTypeOfIndex(panel.currentLearnStep - 1);
        }
        else
        {
            poolType = panel.GetPoolTypeOfIndex(panel.currentLearnStep);
        }

        return CharacterBuildingManagerPatcher.ResolveFeatGrantedSpellSelectionPointPool(
            characterBuildingService,
            heroBuildingData,
            poolType,
            tag,
            spellFeature);
    }

    private static void NormalizeSpellSourceTags(IDictionary<SpellDefinition, string> tagsBySpell)
    {
        foreach (var pair in tagsBySpell.ToArray())
        {
            var normalizedTag = SpellBoxPatcher.NormalizeSpellSourceTag(pair.Value);

            if (normalizedTag != pair.Value)
            {
                tagsBySpell[pair.Key] = normalizedTag;
            }
        }
    }

    private static void FilterMulticlassBleeding(
        [NotNull] SpellsByLevelGroup __instance,
        [NotNull] RulesetCharacterHero caster,
        [NotNull] List<SpellDefinition> allSpells,
        [NotNull] List<SpellDefinition> autoPreparedSpells,
        PointPool pointPool,
        IDictionary<SpellDefinition, string> extraSpellsMap)
    {
        var spellsOverriden = pointPool?.spellListOverride;
        var spellLevel = __instance.SpellLevel;

        // avoids auto prepared spells from other classes to bleed in
        var allowedAutoPreparedSpells = LevelUpHelper.GetAllowedAutoPreparedSpells(caster)
            .Where(x => x.SpellLevel == spellLevel);

        autoPreparedSpells.SetRange(allowedAutoPreparedSpells);

        //Select allowed spells - all spells if list is overriden by the pool, or all allowed spells of current level
        var allowedSpells = spellsOverriden
            ? [..allSpells]
            : LevelUpHelper.GetAllowedSpells(caster).Where(x => x.SpellLevel == spellLevel).ToArray();

        var otherClassesKnownSpells = LevelUpHelper.GetOtherClassesKnownSpells(caster)
            .Where(x => x.Key.SpellLevel == spellLevel).ToArray();

        allSpells.RemoveAll(x => !allowedSpells.Contains(x) && otherClassesKnownSpells.All(p => p.Key != x));

        foreach (var pair in otherClassesKnownSpells)
        {
            var spell = pair.Key;

            //Add multiclass tag to spells known from other classes
            if (!Main.Settings.EnableRelearnSpells)
            {
                extraSpellsMap.TryAdd(spell, pair.Value);
            }

            // displays known spells from other classes
            if (!Main.Settings.DisplayAllKnownSpellsDuringLevelUp)
            {
                continue;
            }

            allSpells.TryAdd(spell);

            //allow re-learning already known spells from other classes
            if (!Main.Settings.EnableRelearnSpells || !allowedSpells.Contains(spell))
            {
                autoPreparedSpells.TryAdd(spell);
            }
        }

        // remove spells bleed from other classes
        if (!Main.Settings.DisplayAllKnownSpellsDuringLevelUp)
        {
            allSpells.RemoveAll(x => !allowedSpells.Contains(x));
        }
    }

    private static void CollectAllAutoPreparedSpells(
        [NotNull] SpellsByLevelGroup __instance,
        // ReSharper disable once SuggestBaseTypeForParameter
        [NotNull] RulesetCharacterHero hero,
        [NotNull] List<SpellDefinition> allSpells,
        // ReSharper disable once SuggestBaseTypeForParameter
        [NotNull] List<SpellDefinition> auToPreparedSpells)
    {
        // Collect all the auto prepared spells.
        // Also filter the prepped spells by level this group is displaying.
        hero.EnumerateFeaturesToBrowse<FeatureDefinitionAutoPreparedSpells>(hero.FeaturesToBrowse);

        foreach (var featureDefinitionAutoPreparedSpells in hero.FeaturesToBrowse
                     .OfType<FeatureDefinitionAutoPreparedSpells>())
        {
            var maxLevel = LevelUpHelper.GetMaxAutoPrepSpellsLevel(hero, featureDefinitionAutoPreparedSpells);

            foreach (var spell in from preparedSpellsGroup in featureDefinitionAutoPreparedSpells
                         .AutoPreparedSpellsGroups
                     from spell in preparedSpellsGroup.SpellsList
                     let flag = !auToPreparedSpells.Contains(spell) && __instance.SpellLevel == spell.SpellLevel &&
                                spell.SpellLevel <= maxLevel
                     where flag
                     select spell)
            {
                auToPreparedSpells.Add(spell);

                // If a spell is not in all spells it won't be shown in the UI.
                // Add the auto prepared spells here to make sure the user sees them.
                allSpells.TryAdd(spell);
            }
        }
    }

    [CanBeNull]
    internal static string GetLevelAndExperienceTooltip([NotNull] GuiCharacter character)
    {
        var builder = new StringBuilder();
        var hero = character.RulesetCharacterHero;

        if (hero == null)
        {
            return null;
        }

        var characterLevelAttribute = hero.GetAttribute(AttributeDefinitions.CharacterLevel);
        var characterLevel = characterLevelAttribute.CurrentValue;
        var experience = hero.TryGetAttributeValue(AttributeDefinitions.Experience);

        if (characterLevel == characterLevelAttribute.MaxValue)
        {
            builder.Append(Gui.Format("Format/&LevelAndExperienceMaxedFormat", characterLevel.ToString("N0"),
                experience.ToString("N0")));
        }
        else
        {
            var num = Mathf.Max(0.0f, ExperienceThresholds[characterLevel] - experience);

            builder.Append(Gui.Format("Format/&LevelAndExperienceFormat", characterLevel.ToString("N0"),
                experience.ToString("N0"), num.ToString("N0"), (characterLevel + 1).ToString("N0")));
        }

        if (hero.ClassesAndLevels.Count <= 1)
        {
            return builder.ToString();
        }

        builder.Append('\n');

        for (var i = 0; i < hero.ClassesHistory.Count; i++)
        {
            var characterClassDefinition = hero.ClassesHistory[i];

            hero.ClassesAndSubclasses.TryGetValue(characterClassDefinition,
                out var characterSubclassDefinition);

            builder
                .AppendFormat("\n{0:00} - ", i + 1)
                .Append(characterClassDefinition.FormatTitle());

            // NOTE: don't use characterSubclassDefinition?. which bypasses Unity object lifetime check
            if (characterSubclassDefinition)
            {
                builder
                    .Append(' ')
                    .Append(characterSubclassDefinition.FormatTitle());
            }
        }

        return builder.ToString();
    }
}
