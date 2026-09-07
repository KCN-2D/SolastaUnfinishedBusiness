using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static SolastaUnfinishedBusiness.Models.SpellCastingResourceContext;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

// Slot counters are pooled across class, innate and free Wizard casting columns.
internal static class SpellUseTooltips
{
    private static readonly ConditionalWeakTable<SlotStatusTable, List<Action>> Restorations = new();

    internal static void Bind(SlotStatusTable table, RulesetSpellRepertoire repertoire, int level)
    {
        if (level <= 0 || repertoire == null)
        {
            return;
        }

        if (!SpellSelectionContext.TryGetOption(repertoire, out var option))
        {
            if (!SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire))
            {
                return;
            }

            // Uses belong to this repertoire and level; the column can contain several spells.
            option = new ResourceOption(repertoire, null, level, ResourceKind.FreeRepertoire);
        }

        var caster = option.Repertoire.GetCaster();
        if (!option.IsFree || caster == null)
        {
            return;
        }

        var restorations = Restorations.GetOrCreateValue(table);
        var description = option.FormatDescription(caster);
        Apply(table.table);
        Apply(table.slotsText);
        Apply(table.infinitySymbol);

        if (table.slotsTitle)
        {
            var originalTitle = table.slotsTitle.Text;
            restorations.Add(() =>
            {
                if (table.slotsTitle)
                {
                    table.slotsTitle.Text = originalTitle;
                }
            });
            table.slotsTitle.Text = "Screen/&SpellFreeUsesTitle";
        }

        return;

        void Apply(Component component)
        {
            if (!component)
            {
                return;
            }

            var tooltip = component.GetComponent<GuiTooltip>();
            if (tooltip)
            {
                var content = tooltip.Content;
                var tooltipClass = tooltip.TooltipClass;
                var disabled = tooltip.Disabled;
                var context = tooltip.Context;
                var provider = tooltip.DataProvider;
                restorations.Add(() =>
                {
                    if (tooltip)
                    {
                        tooltip.Content = content;
                        tooltip.TooltipClass = tooltipClass;
                        tooltip.Disabled = disabled;
                        tooltip.Context = context;
                        tooltip.DataProvider = provider;
                    }
                });
            }
            else
            {
                tooltip = component.gameObject.AddComponent<GuiTooltip>();
                tooltip.AnchorMode = TooltipDefinitions.AnchorMode.LEFT_CENTER;
                restorations.Add(() =>
                {
                    if (tooltip)
                    {
                        Object.DestroyImmediate(tooltip);
                    }
                });
            }

            tooltip.TooltipClass = GuiManager.DefaultTooltipClass;
            tooltip.Content = description;
            tooltip.Disabled = false;
            tooltip.Context = null;
            tooltip.DataProvider = null;
        }
    }

    internal static void Restore(SlotStatusTable table)
    {
        if (!Restorations.TryGetValue(table, out var restorations))
        {
            return;
        }

        foreach (var restore in restorations)
        {
            restore();
        }

        Restorations.Remove(table);
    }
}
