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
    internal static string NormalizeSpellSourceTag(string tag)
    {
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
            if (tag.StartsWith(LevelUpHelper.ExtraClassTag, StringComparison.Ordinal)
                || tag.StartsWith(LevelUpHelper.ExtraSubclassTag, StringComparison.Ordinal))
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

            const string CLASS_FORMAT = "Screen/&ClassExtraSpellDescriptionFormat";
            const string SUBCLASS_FORMAT = "Screen/&SubclassClassExtraSpellDescriptionFormat";

            //__instance.autoPreparedTitle.Text = "Screen/&MulticlassExtraSpellTitle";

            switch (type)
            {
                case LevelUpHelper.ExtraClassTag when
                    DatabaseHelper.TryGetDefinition<CharacterClassDefinition>(name, out var classDef):
                    name = classDef.FormatTitle();
                    spellBox.autoPreparedTooltip.Content = Gui.Format(CLASS_FORMAT, name);
                    break;

                case LevelUpHelper.ExtraSubclassTag when
                    DatabaseHelper.TryGetDefinition<CharacterSubclassDefinition>(name, out var subDef):
                    name = subDef.FormatTitle();
                    spellBox.autoPreparedTooltip.Content = Gui.Format(SUBCLASS_FORMAT, name);
                    break;
            }
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
            UiTextHelpers.KeepSpellBoxTextInside(__instance);
        }
    }
}
