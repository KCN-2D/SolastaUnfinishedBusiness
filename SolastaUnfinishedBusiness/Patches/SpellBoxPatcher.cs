using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellBoxPatcher
{
    private const string AutoPreparedFeatureTag = SpellPreparationContext.FeatureTag + "|";
    private const string AutoPreparedSpellSourceTitle = "Screen/&AutoPreparedSpellSourceTitle";
    private const string AutoPreparedSpellSourceDescription = "Screen/&AutoPreparedSpellSourceDescription";
    private const string AutoPreparedSpellSourceDescriptionFormat = "Screen/&AutoPreparedSpellSourceDescriptionFormat";
    private const string FeatGrantedSpellSingleUseDescription = "Screen/&FeatGrantedSpellSingleUseDescription";
    private const string FeatGrantedSpellSharedUseDescription = "Screen/&FeatGrantedSpellSharedUseDescription";
    private const string ClassExtraSpellDescriptionFormat = "Screen/&ClassExtraSpellDescriptionFormat";
    private const string SubclassExtraSpellDescriptionFormat = "Screen/&SubclassClassExtraSpellDescriptionFormat";
    private const string MulticlassExtraSpellTitle = "Screen/&MulticlassExtraSpellTitle";
    private const string MulticlassExtraSpellDescription = "Screen/&MulticlassExtraSpellDescription";

    internal static string NormalizeSpellSourceTag(string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return tag;
        }

        tag = Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(tag);

        return tag == "DOMAIN" ? "Domain" : tag;
    }

    [HarmonyPatch(typeof(SpellBox), nameof(SpellBox.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            SpellBox __instance,
            ref bool autoPrepared,
            ref bool extraSpell,
            ref string tag,
            out string __state)
        {
            ClearSpellSource(__instance);
            tag = NormalizeSpellSourceTag(tag);
            __state = tag;

            if (IsMulticlassSpellSourceTag(tag))
            {
                // Other-class labels describe a learning source, not automatic preparation.
                autoPrepared = false;
                extraSpell = false;
            }
            else
            {
                // Preserve native selection rules independently of the label's available translations.
                autoPrepared |= !string.IsNullOrEmpty(tag);
            }

            // Resolve every source below; native key construction cannot interpret feature tags
            // and requests missing ExtraSpell keys for sources that only have Spell translations.
            tag = string.Empty;
        }

        [UsedImplicitly]
        public static void Postfix(SpellBox __instance, string __state)
        {
            var repertoire = GetSpellRepertoire(__instance, out var displayedRepertoire);
            var source = repertoire == null
                ? null
                : SpellCastingResourceContext.ResolveCastingRepertoire(repertoire, __instance.SpellDefinition, __instance.Caster);
            SpellCastingValidation.BindTooltipRepertoire(__instance.tooltip, source);

            if (TryResolveSpellSource(
                    __state,
                    __instance.GuiSpellDefinition?.SpellDefinition,
                    __instance.Caster,
                    source,
                    __instance.autoPrepared,
                    __instance.extraSpell,
                    out var title,
                    out var tooltipContent))
            {
                __instance.autoPreparedTitle.Text = CharacterInspectionScreenEnhancement.IsInspectionSpellSourceRedundant(
                    __instance.Caster, displayedRepertoire, source, __state, __instance.bindMode) ? string.Empty : title;
                __instance.autoPreparedTooltip.Content = tooltipContent;
            }

            RefreshSpellSourceVisibility(__instance);
            UiTextHelpers.KeepSpellBoxTextInside(__instance);
        }
    }

    private static bool TryResolveSpellSource(
        string tag,
        SpellDefinition spell,
        RulesetCharacter caster,
        RulesetSpellRepertoire castingSource,
        bool autoPrepared,
        bool extraSpell,
        out string title,
        out string tooltipContent)
    {
        title = string.Empty;
        tooltipContent = string.Empty;

        if (!string.IsNullOrEmpty(tag))
        {
            if (tag.StartsWith(AutoPreparedFeatureTag, StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetSourceDefinition<FeatureDefinition>(tag.Substring(AutoPreparedFeatureTag.Length),
                        out var feature))
                {
                    title = feature.FormatTitle();
                    tooltipContent = Gui.Format(AutoPreparedSpellSourceDescriptionFormat, title);

                    return true;
                }
            }
            else if (TryResolveMulticlassSpellSourceTag(tag, out title, out tooltipContent) ||
                     TryResolveLocalizedSpellSource(tag, spell, caster, castingSource, extraSpell,
                         out title, out tooltipContent) ||
                     TryResolveLocalizedSpellSource(tag, spell, caster, castingSource, !extraSpell,
                         out title, out tooltipContent))
            {
                return true;
            }
        }

        if (!autoPrepared && !extraSpell)
        {
            return false;
        }

        title = Gui.Localize(AutoPreparedSpellSourceTitle);
        tooltipContent = Gui.Localize(AutoPreparedSpellSourceDescription);

        return true;
    }

    private static bool TryResolveLocalizedSpellSource(
        string tag,
        SpellDefinition spell,
        RulesetCharacter caster,
        RulesetSpellRepertoire castingSource,
        bool extraSpell,
        out string title,
        out string tooltipContent)
    {
        var sourceKey = $"Screen/&{tag}{(extraSpell ? "ExtraSpell" : "Spell")}";
        var titleKey = sourceKey + "Title";

        title = string.Empty;
        tooltipContent = string.Empty;

        if (!TranslatorContext.HasTranslation(titleKey))
        {
            return false;
        }

        title = Gui.Localize(titleKey);

        if (Tabletop2024Context.TryGetTabletop2024FeatSpellSourceDescription(
                tag, spell, out tooltipContent) ||
            TryResolveFeatSpellSourceDescription(tag, spell, caster, castingSource, out tooltipContent))
        {
            return true;
        }

        var descriptionKey = sourceKey + "Description";
        tooltipContent = TranslatorContext.HasTranslation(descriptionKey)
            ? Gui.Localize(descriptionKey)
            : Gui.Localize(AutoPreparedSpellSourceDescription);

        return true;
    }

    private static bool TryResolveFeatSpellSourceDescription(
        string tag,
        SpellDefinition spell,
        RulesetCharacter caster,
        RulesetSpellRepertoire castingSource,
        out string description)
    {
        description = null;

        if (spell is not { SpellLevel: > 0 } || caster == null ||
            castingSource?.SpellCastingFeature is not
            {
                SpellReadyness: RuleDefinitions.SpellReadyness.AllKnown,
                SlotsRecharge: RuleDefinitions.RechargeRate.LongRest,
                UniqueLevelSlots: false
            } ||
            !SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(castingSource))
        {
            return false;
        }

        // Use the same grant and source tag as class spell projection. Unrelated racial,
        // subclass and Wizard sources retain their own descriptions and recharge rules.
        var grantedSpells = LevelUpHelper.EnumerateSlotCastableFeatSpells(caster)
            .Where(entry => entry.Repertoire == castingSource && entry.DisplayTag == tag &&
                            entry.Spell.SpellLevel == spell.SpellLevel)
            .Select(entry => entry.Spell)
            .Distinct()
            .ToArray();

        if (!grantedSpells.Contains(spell))
        {
            return false;
        }

        castingSource.GetSlotsNumber(spell.SpellLevel, out _, out var capacity);
        if (capacity != 1)
        {
            return false;
        }

        // Capacity describes the grant even after its free use is spent. Spells sharing
        // one repertoire-level pool must not each promise an independent free casting.
        description = Gui.Localize(grantedSpells.Length > 1
            ? FeatGrantedSpellSharedUseDescription
            : FeatGrantedSpellSingleUseDescription);
        return true;
    }

    private static bool IsMulticlassSpellSourceTag(string tag)
    {
        return !string.IsNullOrEmpty(tag) &&
               (tag.StartsWith(LevelUpHelper.ExtraClassTag + "|", StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith(LevelUpHelper.ExtraSubclassTag + "|", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveMulticlassSpellSourceTag(
        string tag,
        out string title,
        out string tooltipContent)
    {
        title = string.Empty;
        tooltipContent = string.Empty;

        if (!IsMulticlassSpellSourceTag(tag))
        {
            return false;
        }

        title = Gui.Localize(MulticlassExtraSpellTitle);
        tooltipContent = Gui.Localize(MulticlassExtraSpellDescription);

        var separator = tag.IndexOf('|');
        var type = tag.Substring(0, separator);
        var name = tag.Substring(separator + 1);

        if (type.Equals(LevelUpHelper.ExtraClassTag, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetSourceDefinition<CharacterClassDefinition>(name, out var classDef))
            {
                title = classDef.FormatTitle();
                tooltipContent = Gui.Format(ClassExtraSpellDescriptionFormat, title);
            }
        }
        else if (TryGetSourceDefinition<CharacterSubclassDefinition>(name, out var subclassDef))
        {
            title = subclassDef.FormatTitle();
            tooltipContent = Gui.Format(SubclassExtraSpellDescriptionFormat, title);
        }

        return true;
    }

    private static bool TryGetSourceDefinition<T>(string name, out T definition) where T : BaseDefinition
    {
        definition = null;

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (DatabaseHelper.TryGetDefinition(name, out definition))
        {
            return true;
        }

        foreach (var candidate in DatabaseRepository.GetDatabase<T>())
        {
            if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            definition = candidate;

            return true;
        }

        return false;
    }

    private static RulesetSpellRepertoire GetSpellRepertoire(SpellBox spellBox, out RulesetSpellRepertoire displayedRepertoire)
    {
        displayedRepertoire = null;

        // Pooled inspection and preparation groups can be inactive while native Bind fills their cards.
        for (var parent = spellBox.transform.parent; parent; parent = parent.parent)
        {
            if (parent.GetComponent<SpellsByLevelGroup>() is { } group)
            {
                displayedRepertoire = group.SpellRepertoire;

                return CharacterInspectionScreenEnhancement.GetInspectionSpellSource(
                    spellBox.Caster, displayedRepertoire, spellBox.SpellDefinition, spellBox.bindMode);
            }
        }

        return null;
    }

    private static void ClearSpellSource(SpellBox spellBox)
    {
        SpellCastingValidation.BindTooltipRepertoire(spellBox.tooltip, null);
        spellBox.autoPreparedTitle.Text = string.Empty;
        spellBox.autoPreparedTooltip.Content = string.Empty;
        RefreshSpellSourceVisibility(spellBox);
    }

    private static void RefreshSpellSourceVisibility(SpellBox spellBox)
    {
        var visible = !string.IsNullOrEmpty(spellBox.autoPreparedTitle.Text);

        spellBox.autoPreparedTitle.gameObject.SetActive(visible);
        spellBox.autoPreparedGroup.gameObject.SetActive(visible);
    }

    [HarmonyPatch(typeof(SpellBox), nameof(SpellBox.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SpellBox __instance)
        {
            ClearSpellSource(__instance);
        }
    }

    [HarmonyPatch(typeof(SpellBox), nameof(SpellBox.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SpellBox __instance)
        {
            RefreshSpellSourceVisibility(__instance);
            UiTextHelpers.KeepSpellBoxTextInside(__instance);
        }
    }
}
