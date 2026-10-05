using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;
using static FeatureDefinitionAttributeModifier;
using static MotionForm;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiPatcher
{
    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatCommandMapping))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatCommandMapping_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.ReplaceCalls(
                AccessTools.Method(typeof(Gui), nameof(Gui.Localize),
                    [typeof(string), typeof(bool), typeof(UnityEngine.GameObject), typeof(string)]),
                5,
                "Gui.FormatCommandMapping",
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FormatCommandMapping_Patch),
                    nameof(LocalizeMappingPart))));
        }

        private static string LocalizeMappingPart(string key, bool silent, UnityEngine.GameObject obj,
            string overrideLanguage)
        {
            var localized = Gui.Localize(key, silent, obj, overrideLanguage);

            // Native concatenation supplies only the preceding space. Do not require
            // invisible trailing whitespace in translation files to separate key names.
            return key == "InputBinding/&OrFormat" ? localized.Trim() + " " : localized;
        }
    }

    //PATCH: avoid too much missing translation messages during mod boot
    [HarmonyPatch(typeof(Gui), nameof(Gui.LocalizeImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalizeImpl_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            string key,
            ref string translation,
            ref Gui.LocalizationResult __result,
            ref bool silent)
        {
            if (key == GuiPresentationBuilder.EmptyString)
            {
                translation = string.Empty;
                __result = Gui.LocalizationResult.Success;

                return false;
            }

            if (!silent)
            {
                silent = !Main.Enabled;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatEffectRange))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatEffectRange_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string __result, RangeType rangeType, int rangeValue)
        {
            if (rangeValue > 1 && rangeType is RangeType.Touch or RangeType.MeleeHit)
            {
                __result += " " + Gui.FormatDistance(rangeValue);
            }
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatTemporaryHitPointsForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatTemporaryHitPointsForm_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            TemporaryHitPointsForm temporaryHitPointsForm,
            AddBonusMode addAbilityBonusMode,
            ref string __result)
        {
            var bonus = temporaryHitPointsForm.BonusHitPoints;
            var bonusFormat = addAbilityBonusMode switch
            {
                AddBonusMode.AbilityBonus => "Rules/&AbilityScoreBonusFormat",
                AddBonusMode.Proficiency => "Rules/&ProficiencyBonusFormat",
                AddBonusMode.DoubleProficiency => "Rules/&ProficiencyBonusDoubleFormat",
                _ => null
            };

            // Use the same source attribute as ApplyTemporaryHitPointsForm. Definition-only
            // tooltips retain the formula, without changing the shared effect definition.
            if (TooltipPanelPatcher.EffectFormattingCharacter is { } character &&
                addAbilityBonusMode is AddBonusMode.Proficiency or AddBonusMode.DoubleProficiency)
            {
                var multiplier = addAbilityBonusMode == AddBonusMode.DoubleProficiency ? 2 : 1;

                bonus += multiplier * character.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);
                bonusFormat = null;
            }

            var amount = temporaryHitPointsForm.DiceNumber > 0
                ? temporaryHitPointsForm.DiceNumber.ToString(CultureInfo.InvariantCulture) +
                  Gui.GetDieSymbol(temporaryHitPointsForm.DieType)
                : string.Empty;

            if (bonus != 0)
            {
                amount += bonus.ToString(string.IsNullOrEmpty(amount) ? "0;-#" : "+0;-#",
                    CultureInfo.InvariantCulture);
            }

            if (bonusFormat != null)
            {
                amount += Gui.Localize(bonusFormat);
            }

            amount = amount.TrimStart(' ', '+');
            __result = Gui.Format("Rules/&TemporaryHitPointsAmountFormat",
                string.IsNullOrEmpty(amount) ? "0" : amount);

            if (temporaryHitPointsForm.ApplyToSelf)
            {
                __result = Gui.Format("Rules/&ApplySelfFormat", __result);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatMotionForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatMotionForm_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string __result, MotionForm motionForm)
        {
            //PATCH: format extra motion types
            __result = (ExtraMotionType)motionForm.Type switch
            {
                ExtraMotionType.CustomSwap => Gui.Format("Rules/&MotionFormSwitchFormat",
                    Gui.FormatDistance(motionForm.Distance)),
                ExtraMotionType.PushDown => Gui.Format("Rules/&MotionFormPushDownFormat",
                    Gui.FormatDistance(motionForm.Distance)),
                (ExtraMotionType)MotionType.DragToOrigin
                    when motionForm.Distance == MotionContext.PullOntoCaster
                    => Gui.Localize("Rules/&MotionFormPullOnTop"),
                _ => __result
            };
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatSpellSlotsForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatSpellSlotsForm_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string __result, SpellSlotsForm spellSlotsForm)
        {
            //PATCH: format extra motion types
            __result = (ExtraEffectType)spellSlotsForm.Type switch
            {
                ExtraEffectType.RecoverSorceryHalfLevelDown => Gui.Localize(
                    "Rules/&SpellSlotFormRecoverSorceryHalfLevelDownFormat"),
                _ => __result
            };
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatCounterForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatCounterForm_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string __result, CounterForm counterForm)
        {
            var format = counterForm.Type switch
            {
                CounterForm.CounterType.DissipateSpells => "Rules/&CounterFormDissipateSpellsFormat",
                CounterForm.CounterType.InterruptSpellcasting => "Rules/&CounterFormInterruptSpellcastingFormat",
                _ => null
            };

            if (format == null)
            {
                return;
            }

            __result = Gui.Format(
                format,
                counterForm.AutomaticSpellLevel.ToString(CultureInfo.InvariantCulture),
                counterForm.CheckBaseDC.ToString(CultureInfo.InvariantCulture));
        }
    }

    //PATCH: always displays a sign on attribute modifiers
    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatTrendsList))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatTrendsList_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            out string __result,
            string output,
            List<TrendInfo> trends,
            bool ignoreZero)
        {
            foreach (var trend in trends
                         .Where(trend => trend.value != 0 || !ignoreZero))
            {
                if (!string.IsNullOrEmpty(output))
                {
                    output += "\n";
                }

                var fixAdditive =
                    trend.attributeModifier?.operation is AttributeModifierOperation.Additive or
                        AttributeModifierOperation.AddConditionAmount or
                        AttributeModifierOperation.AddProficiencyBonus or
                        AttributeModifierOperation.AddSurroundingEnemies or
                        AttributeModifierOperation.AddAbilityScoreBonus or
                        AttributeModifierOperation.AddHalfProficiencyBonus;

                var value = fixAdditive || trend.additive ? trend.value.ToString("+0;-#") : trend.value.ToString();

                output += Gui.Format("{0}: {1}", value, Gui.FormatTrendInfo(trend));
            }

            __result = output;

            return false;
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.LocalizeFeatTagTitle))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalizeFeatTagTitle_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(string tag, ref string __result)
        {
            if (!Tabletop2024Context.TryGetHumanOriginFeatTagTitle(tag, out var title))
            {
                return true;
            }

            __result = title;

            return false;
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.LocalizeMetamagicTagTitle))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalizeMetamagicTagTitle_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(string tag, ref string __result)
        {
            if (!string.Equals(tag, MetamagicContext.FeatMetamagicAdeptPointPoolTag, System.StringComparison.Ordinal))
            {
                return true;
            }

            __result = DatabaseHelper.FeatureDefinitionPointPools.PointPoolSorcererMetamagic.FormatTitle();

            return false;
        }
    }
}
