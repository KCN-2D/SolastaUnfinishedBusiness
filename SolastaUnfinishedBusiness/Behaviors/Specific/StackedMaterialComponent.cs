using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

/// <summary>
///     Allow spells that require consumption of a material component (e.g. a gem of value >= 1000gp) use a stack
///     of lesser value components (e.g. 4 x 300gp diamonds).
///     This implementation combines stacks of the same item definition, but doesn't combine different types of items
///     that happen to share the same material tag.
/// </summary>
internal static class StackedMaterialComponent
{
    private sealed class ConsumptionEntry
    {
        internal ConsumptionEntry(RulesetItem rulesetItem, int count)
        {
            RulesetItem = rulesetItem;
            Count = count;
        }

        internal RulesetItem RulesetItem { get; }

        internal int Count { get; }
    }

    private sealed class ConsumptionPlan
    {
        internal ConsumptionPlan(
            ItemDefinition itemDefinition,
            List<ConsumptionEntry> entries,
            int itemCount,
            long totalCost)
        {
            ItemDefinition = itemDefinition;
            Entries = entries;
            ItemCount = itemCount;
            TotalCost = totalCost;
        }

        internal ItemDefinition ItemDefinition { get; }

        internal List<ConsumptionEntry> Entries { get; }

        internal int ItemCount { get; }

        internal long TotalCost { get; }
    }

    internal static void IsComponentMaterialValid(
        RulesetCharacter character,
        SpellDefinition spellDefinition,
        ref string failure,
        ref bool result)
    {
        if (!IsEnabled(spellDefinition))
        {
            return;
        }

        if (result)
        {
            return;
        }

        if (!TryBuildConsumptionPlan(character, spellDefinition, out _))
        {
            return;
        }

        result = true;
        failure = string.Empty;
    }

    // Modify original code to spend enough matching items to meet the component cost.
    internal static bool SpendSpellMaterialComponentAsNeeded(RulesetCharacter character, RulesetEffectSpell activeSpell)
    {
        var spell = activeSpell.SpellDefinition;

        if (!IsEnabled(spell))
        {
            return true;
        }

        if (spell.MaterialComponentType != RuleDefinitions.MaterialComponentType.Specific
            || !spell.SpecificMaterialComponentConsumed
            || string.IsNullOrEmpty(spell.SpecificMaterialComponentTag)
            || spell.SpecificMaterialComponentCostGp <= 0
            || character.CharacterInventory == null)
        {
            return false;
        }

        if (!TryBuildConsumptionPlan(character, spell, out var consumptionPlan))
        {
            // ReSharper disable once InvocationIsSkipped
            Main.Log("Didn't find item.");

            return false;
        }

        // ReSharper disable once InvocationIsSkipped
        Main.Log(
            $"Spending stacks={consumptionPlan.Entries.Count}, items={consumptionPlan.ItemCount}, " +
            $"cost={consumptionPlan.TotalCost}");

        var componentConsumed = character.SpellComponentConsumed;

        foreach (var entry in consumptionPlan.Entries)
        {
            if (componentConsumed != null)
            {
                for (var i = 0; i < entry.Count; i++)
                {
                    componentConsumed(character, spell, entry.RulesetItem);
                }
            }

            var rulesetItem = entry.RulesetItem;

            if (rulesetItem.ItemDefinition.CanBeStacked && entry.Count < rulesetItem.StackCount)
            {
                rulesetItem.SpendStack(entry.Count);
            }
            else
            {
                character.CharacterInventory.DestroyItem(rulesetItem);
            }
        }

        return false;
    }

    private static bool IsEnabled(SpellDefinition spellDefinition)
    {
        return Main.Settings.AllowStackedMaterialComponent ||
               spellDefinition.GetFirstSubFeatureOfType<IForceStackedMaterialComponent>() != null;
    }

    private static bool TryBuildConsumptionPlan(
        RulesetCharacter character,
        SpellDefinition spellDefinition,
        out ConsumptionPlan consumptionPlan)
    {
        consumptionPlan = null;

        if (spellDefinition.MaterialComponentType != RuleDefinitions.MaterialComponentType.Specific
            || string.IsNullOrEmpty(spellDefinition.SpecificMaterialComponentTag)
            || spellDefinition.SpecificMaterialComponentCostGp <= 0
            || character.CharacterInventory == null)
        {
            return false;
        }

        var items = new List<RulesetItem>();

        character.CharacterInventory.EnumerateAllItems(items);

        consumptionPlan = items
            .Where(item => item.StackCount > 0 &&
                           item.ItemDefinition.ItemTags.Contains(spellDefinition.SpecificMaterialComponentTag))
            .GroupBy(item => item.ItemDefinition)
            .Select(group => BuildConsumptionPlan(
                group.Key,
                group,
                spellDefinition.SpecificMaterialComponentCostGp))
            .Where(plan => plan != null)
            .OrderBy(plan => plan.TotalCost)
            .ThenBy(plan => plan.ItemCount)
            .ThenBy(plan => plan.ItemDefinition.Name, StringComparer.Ordinal)
            .ThenBy(plan => plan.Entries[0].RulesetItem.Guid)
            .FirstOrDefault();

        return consumptionPlan != null;
    }

    private static ConsumptionPlan BuildConsumptionPlan(
        ItemDefinition itemDefinition,
        IEnumerable<RulesetItem> stacks,
        int requiredCost)
    {
        // The base game spends Costs[1], while validation uses GetApproximateCostInGold. Use the latter consistently.
        var itemCost = EquipmentDefinitions.GetApproximateCostInGold(itemDefinition.Costs);

        if (itemCost <= 0)
        {
            return null;
        }

        var itemCount = (int)((requiredCost + (long)itemCost - 1) / itemCost);
        var orderedStacks = stacks
            .OrderBy(item => item.Guid)
            .ToList();

        if (orderedStacks.Sum(item => (long)item.StackCount) < itemCount)
        {
            return null;
        }

        var entries = new List<ConsumptionEntry>();
        var remaining = itemCount;

        foreach (var stack in orderedStacks)
        {
            var count = Math.Min(stack.StackCount, remaining);

            if (count <= 0)
            {
                continue;
            }

            entries.Add(new ConsumptionEntry(stack, count));
            remaining -= count;

            if (remaining == 0)
            {
                break;
            }
        }

        return new ConsumptionPlan(itemDefinition, entries, itemCount, itemCount * (long)itemCost);
    }
}
