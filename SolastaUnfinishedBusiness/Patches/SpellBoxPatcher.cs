using System.Diagnostics.CodeAnalysis;
using System;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellBoxPatcher
{
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
            ref bool autoPrepared,
            ref bool extraSpell,
            ref string tag,
            out string __state)
        {
            __state = null;

            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            tag = NormalizeSpellSourceTag(tag);

            //PATCH: show actual class/subclass name in the multiclass tag during spell selection on level up
            if (IsMulticlassSpellSourceTag(tag))
            {
                //store original extra tag and reset both - actual texts would be handled on Postfix for this case
                __state = tag;
                autoPrepared = false;
                extraSpell = false;
                return;
            }

            //PATCH: if extra spell tag has no translation, but auto prepared translation for same tag exists - use that one.
            if (TranslatorContext.HasTranslation($"Screen/&{tag}ExtraSpellTitle")
                || !TranslatorContext.HasTranslation($"Screen/&{tag}SpellTitle"))
            {
                return;
            }

            autoPrepared = true;
            extraSpell = false;
        }

        [UsedImplicitly]
        public static void Postfix(SpellBox __instance, string __state)
        {
            //PATCH: show actual class/subclass name in the multiclass tag during spell selection on level up
            ApplyMulticlassExtraSpellTooltip(__instance, __state);
            UiTextHelpers.KeepSpellBoxTextInside(__instance);
        }

        private static void ApplyMulticlassExtraSpellTooltip(SpellBox spellBox, string extraTag)
        {
            if (!spellBox || string.IsNullOrEmpty(extraTag))
            {
                return;
            }

            var parts = extraTag.Split('|');

            if (parts.Length != 2)
            {
                return;
            }

            var type = parts[0];
            var name = parts[1];

            if (!TryResolveMulticlassSpellSourceTag(type, name, out var title, out var tooltipContent))
            {
                return;
            }

            spellBox.autoPreparedTitle.Text = title;
            spellBox.autoPreparedTitle.gameObject.SetActive(true);
            spellBox.autoPreparedTooltip.Content = tooltipContent;
        }
    }

    private static bool IsMulticlassSpellSourceTag(string tag)
    {
        return tag.StartsWith(LevelUpHelper.ExtraClassTag, StringComparison.OrdinalIgnoreCase)
               || tag.StartsWith(LevelUpHelper.ExtraSubclassTag, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveMulticlassSpellSourceTag(
        string type,
        string name,
        out string title,
        out string tooltipContent)
    {
        title = Gui.Localize(MulticlassExtraSpellTitle);
        tooltipContent = Gui.Localize(MulticlassExtraSpellDescription);

        if (type.Equals(LevelUpHelper.ExtraClassTag, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetClassDefinition(name, out var classDef))
            {
                title = classDef.FormatTitle();
                tooltipContent = Gui.Format(ClassExtraSpellDescriptionFormat, title);
            }

            return true;
        }

        if (!type.Equals(LevelUpHelper.ExtraSubclassTag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryGetSubclassDefinition(name, out var subDef))
        {
            title = subDef.FormatTitle();
            tooltipContent = Gui.Format(SubclassExtraSpellDescriptionFormat, title);
        }

        return true;
    }

    private static bool TryGetClassDefinition(string name, out CharacterClassDefinition definition)
    {
        if (DatabaseHelper.TryGetDefinition(name, out definition))
        {
            return true;
        }

        foreach (var candidate in DatabaseRepository.GetDatabase<CharacterClassDefinition>())
        {
            if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            definition = candidate;

            return true;
        }

        definition = null;

        return false;
    }

    private static bool TryGetSubclassDefinition(string name, out CharacterSubclassDefinition definition)
    {
        if (DatabaseHelper.TryGetDefinition(name, out definition))
        {
            return true;
        }

        foreach (var candidate in DatabaseRepository.GetDatabase<CharacterSubclassDefinition>())
        {
            if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            definition = candidate;

            return true;
        }

        definition = null;

        return false;
    }

    [HarmonyPatch(typeof(SpellBox), nameof(SpellBox.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SpellBox __instance)
        {
            UiTextHelpers.KeepSpellBoxTextInside(__instance);
        }
    }
}
