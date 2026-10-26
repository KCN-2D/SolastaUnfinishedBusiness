using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.CustomUI;

public class CustomInvocationSubPanel : MonoBehaviour
{
    private InvocationPoolTypeCustom Type { get; set; }

    private static bool HasEldritchAdeptFeat(RulesetCharacterHero hero)
    {
        if (hero == null)
        {
            return false;
        }

        if (hero.TrainedFeats.Any(OtherFeats.IsEldritchAdeptFeat))
        {
            return true;
        }

        return hero.GetHeroBuildingData()?.LevelupTrainedFeats?
            .SelectMany(entry => entry.Value ?? [])
            .Any(OtherFeats.IsEldritchAdeptFeat) == true;
    }

    private void Setup(InvocationPoolTypeCustom type)
    {
        Type = type;
        name = $"CustomSubPanel<{type.Name}>";

        var title = transform.Find("ProficiencySectionHeader/Title");

        if (title)
        {
            title.GetComponent<GuiLabel>().Text = type.PanelTitle;
        }
    }

    internal static List<string> CustomInvocationsProficiencies(RulesetCharacterHero hero, InvocationSubPanel panel)
    {
        var custom = panel.GetComponent<CustomInvocationSubPanel>();

        return GetAcquiredInvocations(hero)
            .Where(invocation => IsInvocationFromPanel(invocation, custom))
            .Select(invocation => invocation.Name)
            .ToList();
    }

    internal static List<string> OnlyStandardInvocationProficiencies(RulesetCharacterHero hero)
    {
        if (hero == null)
        {
            return [];
        }

        var selectedClass = LevelUpHelper.GetSelectedClass(hero);

        if (selectedClass != DatabaseHelper.CharacterClassDefinitions.Warlock
            && !HasEldritchAdeptFeat(hero))
        {
            return [];
        }

        return GetAcquiredInvocations(hero)
            .Where(invocation => invocation is not InvocationDefinitionCustom)
            .Select(invocation => invocation.Name)
            .ToList();
    }

    public static void UpdateRelevantInvocations(InvocationSubPanel panel)
    {
        PrepareRelevantInvocations(panel, null);
    }

    internal static void PrepareRelevantInvocations(InvocationSubPanel panel, RulesetCharacterHero hero)
    {
        var custom = panel.GetComponent<CustomInvocationSubPanel>();
        var inspection = IsPassiveInspection(panel, hero);
        IEnumerable<InvocationDefinition> source = inspection
            ? GetAcquiredInvocations(hero)
            : DatabaseRepository.GetDatabase<InvocationDefinition>();
        var invocations = source.Where(invocation => !invocation.GuiPresentation.Hidden &&
                !InvocationsContext.DisabledInvocations.Contains(invocation) &&
                IsInvocationFromPanel(invocation, custom) &&
                (!inspection || !custom || Main.Settings.EnableExtendedProficienciesPanelDisplay));

        var table = panel.Table;
        var relevantInvocations = panel.relevantInvocations;

        relevantInvocations.SetRange(invocations);

        // get missing children from pool
        while (table.childCount < relevantInvocations.Count)
        {
            Gui.GetPrefabFromPool(panel.ItemPrefab, table);
        }

        // release extra children to pool
        while (table.childCount > relevantInvocations.Count)
        {
            Gui.ReleaseInstanceToPool(table.GetChild(table.childCount - 1).gameObject);
        }
    }

    private static bool IsPassiveInspection(ProficienciesSubPanel panel, RulesetCharacterHero hero)
    {
        return hero != null && CharacterInspectionScreenEnhancement.IsInspectionProficiencyPanel(panel) &&
               !LevelUpHelper.IsLevelingUp(hero) &&
               ServiceRepository.GetService<ICharacterBuildingService>()?.CurrentLocalHeroCharacter == null;
    }

    private static IEnumerable<InvocationDefinition> GetAcquiredInvocations(RulesetCharacterHero hero)
    {
        if (hero == null)
        {
            yield break;
        }

        var database = DatabaseRepository.GetDatabase<InvocationDefinition>();
        HashSet<string> acquired = [];

        foreach (var name in hero.InvocationProficiencies)
        {
            if (acquired.Add(name) && database.TryGetElement(name, out var invocation))
            {
                yield return invocation;
            }
        }
    }

    private static bool IsInvocationFromPanel(InvocationDefinition invocation, CustomInvocationSubPanel custom)
    {
        return custom
            ? invocation is InvocationDefinitionCustom definition && definition.PoolType == custom.Type
            : invocation is not InvocationDefinitionCustom;
    }

    public static void AddCustomSubPanels(ProficienciesPanel panel, RulesetCharacterHero hero = null)
    {
        if (!panel.toggleGroup || !Main.Settings.EnableExtendedProficienciesPanelDisplay)
        {
            return;
        }

        var invocationsSubPanel = panel.subPanels[(int)ProficienciesPanel.ProficiencyType.Invocation];

        var inspection = CharacterInspectionScreenEnhancement.IsInspectionProficiencyPanel(invocationsSubPanel);
        if (inspection && hero == null)
        {
            return;
        }

        var pools = InvocationPoolTypeCustom.Pools.All.Where(pool => !pool.Hidden);
        if (inspection && IsPassiveInspection(invocationsSubPanel, hero))
        {
            var acquiredPools = GetAcquiredInvocations(hero)
                .Where(invocation => !invocation.GuiPresentation.Hidden &&
                                     !InvocationsContext.DisabledInvocations.Contains(invocation))
                .OfType<InvocationDefinitionCustom>().Select(invocation => invocation.PoolType).ToHashSet();
            pools = pools.Where(acquiredPools.Contains);
        }

        var invocationTransform = invocationsSubPanel.transform;
        var root = invocationTransform.parent;
        var siblingIndex = invocationTransform.GetSiblingIndex() + 1;

        var index = panel.subPanels.Length;
        var existing = panel.subPanels.OfType<InvocationSubPanel>()
            .Select(subPanel => subPanel.GetComponent<CustomInvocationSubPanel>())
            .Where(custom => custom).Select(custom => custom.Type).ToHashSet();
        var poolTypes = pools.Where(pool => !existing.Contains(pool)).ToArray();
        if (poolTypes.Length == 0)
        {
            return;
        }

        Array.Resize(ref panel.subPanels, index + poolTypes.Length);

        foreach (var pool in poolTypes)
        {
            var extra = Instantiate(invocationsSubPanel, root);

            // A setting can create these sections after the source panel was bound.
            // Cloned rows do not share the source's native pool ownership.
            for (var child = extra.Table.childCount - 1; child >= 0; child--)
            {
                DestroyImmediate(extra.Table.GetChild(child).gameObject);
            }

            extra.gameObject.AddComponent<CustomInvocationSubPanel>().Setup(pool);
            extra.transform.SetSiblingIndex(siblingIndex);
            panel.subPanels[index] = extra;

            var groupPrefab = panel.proficiencyTogglePrefab;
            var group = Gui.GetPrefabFromPool(groupPrefab, panel.toggleGroup);

            group.transform.SetSiblingIndex(siblingIndex);

            var proficiencyToggle = group.GetComponent<ProficiencyToggle>();

            proficiencyToggle.Bind(pool.PanelTitle, false, index, panel.ProficiencyToggleChanged);
            panel.subPanelsMap.Add(proficiencyToggle, extra);

            index++;
            siblingIndex++;
        }
    }
}
