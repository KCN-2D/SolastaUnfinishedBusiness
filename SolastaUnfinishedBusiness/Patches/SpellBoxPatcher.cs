using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellBoxPatcher
{
    private const string AutoPreparedFeatureTag = SpellPreparationContext.FeatureTag + "|";
    private const string AutoPreparedSpellSourceTitle = "Screen/&AutoPreparedSpellSourceTitle";
    private const string AutoPreparedSpellSourceDescription = "Screen/&AutoPreparedSpellSourceDescription";
    private const string AutoPreparedSpellSourceDescriptionFormat = "Screen/&AutoPreparedSpellSourceDescriptionFormat";
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
            if (TryResolveSpellSource(
                    __state,
                    __instance.GuiSpellDefinition?.SpellDefinition,
                    __instance.autoPrepared,
                    __instance.extraSpell,
                    out var title,
                    out var tooltipContent))
            {
                __instance.autoPreparedTitle.Text = title;
                __instance.autoPreparedTooltip.Content = tooltipContent;
            }

            RefreshSpellSourceVisibility(__instance);
            UiTextHelpers.KeepSpellBoxTextInside(__instance);
        }
    }

    private static bool TryResolveSpellSource(
        string tag,
        SpellDefinition spell,
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
                     TryResolveLocalizedSpellSource(tag, spell, extraSpell, out title, out tooltipContent) ||
                     TryResolveLocalizedSpellSource(tag, spell, !extraSpell, out title, out tooltipContent))
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
                tag, spell, out tooltipContent))
        {
            return true;
        }

        var descriptionKey = sourceKey + "Description";
        tooltipContent = TranslatorContext.HasTranslation(descriptionKey)
            ? Gui.Localize(descriptionKey)
            : Gui.Localize(AutoPreparedSpellSourceDescription);

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

    private static void ClearSpellSource(SpellBox spellBox)
    {
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
