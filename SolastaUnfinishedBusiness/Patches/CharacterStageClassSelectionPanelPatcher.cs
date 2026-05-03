using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageClassSelectionPanelPatcher
{
    private static void RefreshClassContentLayout(CharacterStageClassSelectionPanel panel)
    {
        if (!panel)
        {
            return;
        }

        var changed = false;

        foreach (var item in panel.GetComponentsInChildren<FeatureDescriptionItem>(false))
        {
            changed |= FeatureDescriptionItemPatcher.RefreshSelectionFeatureDisplayLayout(item);
        }

        if (!changed || panel.transform is not RectTransform rectTransform)
        {
            return;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
    }

    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel), nameof(CharacterStageClassSelectionPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        private static void ApplyStrictCompatibleClassesFilter([NotNull] CharacterStageClassSelectionPanel __instance)
        {
            StrictTabletopSelectionContext.FilterAndPreserveSelection(
                __instance.compatibleClasses,
                ref __instance.selectedClass,
                StrictTabletopSelectionContext.IsClassAllowedForCurrentMode);
        }

        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterStageClassSelectionPanel __instance)
        {
            //PATCH: avoids a restart when enabling / disabling classes on the Mod UI panel
            var visibleClasses = DatabaseRepository.GetDatabase<CharacterClassDefinition>()
                .Where(x => !x.GuiPresentation.Hidden)
                .OrderBy(x => x.FormatTitle());

            __instance.compatibleClasses.SetRange(visibleClasses);
            ApplyStrictCompatibleClassesFilter(__instance);

            if (!LevelUpHelper.IsLevelingUp(__instance.currentHero))
            {
                return;
            }

            //PATCH: mark we started selecting classes (MULTICLASS)
            LevelUpHelper.SetIsClassSelectionStage(__instance.currentHero, true);

            //PATCH: apply in/out logic (MULTICLASS)
            MulticlassInOutRulesHelper.EnumerateHeroAllowedClassDefinitions(
                __instance.currentHero,
                __instance.compatibleClasses,
                out __instance.selectedClass);
            ApplyStrictCompatibleClassesFilter(__instance);

            //PATCH: refresh the panel (MULTICLASS)
            var commonData = __instance.CommonData;

            // NOTE: don't use AttackModesPanel?. which bypasses Unity object lifetime check
            if (commonData.AttackModesPanel)
            {
                commonData.AttackModesPanel.RefreshNow();
            }

            // NOTE: don't use PersonalityMapPanel?. which bypasses Unity object lifetime check
            if (commonData.PersonalityMapPanel)
            {
                commonData.PersonalityMapPanel.RefreshNow();
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel),
        nameof(CharacterStageClassSelectionPanel.RefreshCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshCharacter_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStageClassSelectionPanel __instance)
        {
            var hero = __instance.currentHero;

            //PATCH: avoid Druid Primal Order to break level up with the cantrip pool it gets
            CharacterBuildingManagerPatcher.RemoveCantripPointPool(
                hero,
                CharacterBuildingManagerPatcher.BuildClassExtraSpellPoolName(Druid, 1, "PrimalOrder"));

            //PATCH: avoid Cleric Thaumaturge to bleed into another class spell selection after going back
            CharacterBuildingManagerPatcher.RemoveCantripPointPool(
                hero,
                CharacterBuildingManagerPatcher.BuildClassExtraSpellPoolName(
                    Cleric,
                    1,
                    Tabletop2024Context.ClericThaumaturgeExtraSpellsTag));
        }
    }

    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel),
        nameof(CharacterStageClassSelectionPanel.FillClassFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillClassFeatures_Patch
    {
        //PATCH: hides the features list for already acquired classes (MULTICLASS)
        private static int Level(
            [NotNull] FeatureUnlockByLevel featureUnlockByLevel,
            [NotNull] RulesetCharacterHero hero)
        {
            var isLevelingUp = LevelUpHelper.IsLevelingUp(hero);
            var selectedClass = LevelUpHelper.GetSelectedClass(hero);

            if (!isLevelingUp)
            {
                return featureUnlockByLevel.Level;
            }

            var levels = 0;

            if (selectedClass
                && hero.ClassesAndLevels.TryGetValue(selectedClass, out levels)
                && featureUnlockByLevel.Level != levels + 1)
            {
                return int.MaxValue;
            }

            if (levels == 0)
            {
                return featureUnlockByLevel.Level;
            }

            return featureUnlockByLevel.Level - 1;
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var levelMethod = typeof(FeatureUnlockByLevel).GetMethod("get_Level");
            var myLevelMethod = new Func<FeatureUnlockByLevel, RulesetCharacterHero, int>(Level).Method;
            var currentHeroField =
                typeof(CharacterStageClassSelectionPanel).GetField("currentHero",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return instructions.ReplaceCalls(levelMethod, "CharacterStageClassSelectionPanel.FillClassFeatures",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, currentHeroField),
                new CodeInstruction(OpCodes.Call, myLevelMethod));
        }
    }

    //PATCH: hides the equipment panel group (MULTICLASS)
    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel), nameof(CharacterStageClassSelectionPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        private static bool SetActive([NotNull] RulesetCharacterHero currentHero)
        {
            return !LevelUpHelper.IsLevelingUp(currentHero);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var setActiveMethod = typeof(GameObject).GetMethod("SetActive");
            var mySetActiveMethod = new Func<RulesetCharacterHero, bool>(SetActive).Method;
            var currentHeroField =
                typeof(CharacterStageClassSelectionPanel).GetField("currentHero",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return instructions.ReplaceCall(setActiveMethod,
                4, "CharacterStageClassSelectionPanel.Refresh",
                new CodeInstruction(OpCodes.Pop),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, currentHeroField),
                new CodeInstruction(OpCodes.Call, mySetActiveMethod),
                new CodeInstruction(OpCodes.Call, setActiveMethod)); // checked for Call vs CallVirtual
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageClassSelectionPanel __instance)
        {
            RefreshClassContentLayout(__instance);
        }
    }
}
