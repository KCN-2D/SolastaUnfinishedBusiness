using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static SolastaUnfinishedBusiness.Models.SpellCastingResourceContext;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

// Spell source labels and pooled slot counters describe the same casting resources.
internal static class SpellUseTooltips
{
    private static readonly ConditionalWeakTable<SlotStatusTable, List<Action>> Restorations = new();

    internal static bool TryFormatSourceDescription(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        string sourceTitle,
        bool alwaysPrepared,
        out string description)
    {
        description = null;
        if (caster == null || repertoire?.SpellCastingFeature == null || !spell)
        {
            return false;
        }

        if (spell.SpellLevel == 0)
        {
            description = Gui.Format("Screen/&FeatGrantedCantripDescriptionFormat", sourceTitle);
            return true;
        }

        var feature = repertoire.SpellCastingFeature;
        var spellTag = feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();
        var resources = EnumerateResources(caster, spell, repertoire)
            .Where(option => option.Kind == ResourceKind.FreeRepertoire && option.CastingRepertoire == repertoire)
            .ToArray();
        var slotCastable = LevelUpHelper.EnumerateSlotCastableFeatSpells(caster)
            .Any(entry => entry.Repertoire == repertoire && entry.Spell == spell);
        var paragraphs = new List<string>();

        foreach (var resource in resources)
        {
            resource.GetUses(caster, out _, out var maximum);
            var shared = maximum >= 0 && HasSharedUses(caster, repertoire, spell, resource.SlotLevel);

            // Keep the familiar concise description when the grant has the standard
            // one-use daily resource. Other grants are described from their own metadata.
            if (resources.Length == 1 && maximum == 1 && resource.SlotLevel == spell.SpellLevel &&
                alwaysPrepared && slotCastable && feature.SlotsRecharge == RuleDefinitions.RechargeRate.LongRest &&
                spellTag?.FreeCastingIgnoresComponents != true)
            {
                description = Gui.Localize(shared
                    ? "Screen/&FeatGrantedSpellSharedUseDescription"
                    : "Screen/&FeatGrantedSpellSingleUseDescription");
                return true;
            }

            var details = new List<string>
            {
                Gui.Format("Screen/&GrantedSpellFreeCastingDescriptionFormat", resource.SlotLevel.ToString())
            };
            if (maximum < 0)
            {
                details.Add(Gui.Localize("Screen/&GrantedSpellFreeUnlimitedDescription"));
            }
            else
            {
                details.Add(Gui.Format("Screen/&GrantedSpellFreeUsesDescriptionFormat", maximum.ToString()));
                var recharge = feature.SlotsRecharge switch
                {
                    RuleDefinitions.RechargeRate.LongRest => "Screen/&GrantedSpellUsesLongRestDescription",
                    RuleDefinitions.RechargeRate.ShortRest => "Screen/&GrantedSpellUsesShortRestDescription",
                    _ => null
                };
                if (recharge != null)
                {
                    details.Add(Gui.Localize(recharge));
                }

                if (shared)
                {
                    details.Add(Gui.Localize("Screen/&GrantedSpellSharedUsesDescription"));
                }
            }

            if (spellTag?.FreeCastingIgnoresComponents == true)
            {
                details.Add(Gui.Localize("Screen/&GrantedSpellFreeComponentsDescription"));
            }

            paragraphs.Add(details.Aggregate((text, next) =>
                text + (text.EndsWith("。", StringComparison.Ordinal) ? string.Empty : " ") + next));
        }

        if (alwaysPrepared)
        {
            paragraphs.Insert(0, Gui.Localize("Screen/&AutoPreparedSpellSourceDescription"));
        }

        if (slotCastable)
        {
            paragraphs.Add(Gui.Localize("Screen/&GrantedSpellSlotCastingDescription"));
        }

        if (paragraphs.Count == 0)
        {
            return false;
        }

        description = string.Join("\n\n", paragraphs);
        return true;
    }

    private static bool HasSharedUses(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        int slotLevel)
    {
        return CharacterInspectionScreenEnhancement.GetInspectionLearnedSpells(repertoire)
            .Where(candidate => candidate != spell && candidate.SpellLevel > 0)
            .Any(candidate => EnumerateResources(caster, candidate, repertoire).Any(option =>
                option.Kind == ResourceKind.FreeRepertoire && option.CastingRepertoire == repertoire &&
                option.SlotLevel == slotLevel));
    }

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
