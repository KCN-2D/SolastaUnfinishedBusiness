using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageBackgroundSelectionPanelPatcher
{
    private static readonly Action<CharacterStagePanel> RefreshStageStatus =
        AccessTools.MethodDelegate<Action<CharacterStagePanel>>(
            AccessTools.DeclaredMethod(typeof(CharacterStagePanel), nameof(CharacterStagePanel.Refresh)),
            virtualCall: false);

    [ThreadStatic]
    private static CharacterStageBackgroundSelectionPanel _personalityRefreshPanel;

    [HarmonyPatch(typeof(CharacterStageBackgroundSelectionPanel), nameof(CharacterStageBackgroundSelectionPanel.Refresh))]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterStageBackgroundSelectionPanel __instance, out bool __state)
        {
            __state = false;

            if (!ReferenceEquals(_personalityRefreshPanel, __instance))
            {
                return true;
            }

            _personalityRefreshPanel = null;
            __state = TryRefreshPersonality(__instance);

            return !__state;
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageBackgroundSelectionPanel __instance, bool __state)
        {
            if (__state)
            {
                return;
            }

            UiTextHelpers.ConfigureCharacterSelectionScroll(__instance.backgroundsTableSW);
            UiTextHelpers.ConfigureCharacterSelectionScroll(__instance.backgroundFeaturesScrollview);
        }
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class PersonalitySelection_Patch
    {
        [UsedImplicitly]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var panel = typeof(CharacterStageBackgroundSelectionPanel);

            return
            [
                AccessTools.DeclaredMethod(panel,
                    nameof(CharacterStageBackgroundSelectionPanel.OnBackgroundPersonalityFlagSelected)),
                AccessTools.DeclaredMethod(panel,
                    nameof(CharacterStageBackgroundSelectionPanel.OnAlignmentPersonalityFlagSelected)),
                AccessTools.DeclaredMethod(panel, nameof(CharacterStageBackgroundSelectionPanel.OnRandomBackgroundFlagsCb)),
                AccessTools.DeclaredMethod(panel, nameof(CharacterStageBackgroundSelectionPanel.OnRandomAlignmentFlagsCb)),
                AccessTools.DeclaredMethod(panel, nameof(CharacterStageBackgroundSelectionPanel.BackgroundFlagsSelectedHandler)),
                AccessTools.DeclaredMethod(panel, nameof(CharacterStageBackgroundSelectionPanel.AlignmentFlagsSelectedHandler))
            ];
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod) =>
            instructions.ReplaceCalls(AccessTools.DeclaredMethod(typeof(GuiPanel), nameof(GuiPanel.RefreshNow)),
                1, __originalMethod.Name,
                new CodeInstruction(OpCodes.Call,
                    new Action<CharacterStageBackgroundSelectionPanel>(RefreshPersonalityNow).Method));
    }

    private static void RefreshPersonalityNow(CharacterStageBackgroundSelectionPanel panel)
    {
        var previous = _personalityRefreshPanel;
        _personalityRefreshPanel = panel;

        try
        {
            // Keep the panel's navigation and selection lifecycle in RefreshNow.
            panel.RefreshNow();
        }
        finally
        {
            _personalityRefreshPanel = previous;
        }
    }

    private static bool TryRefreshPersonality(CharacterStageBackgroundSelectionPanel panel)
    {
        var hero = panel.currentHero;
        var map = panel.CommonData?.PersonalityMapPanel;
        var selected = panel.selectedBackground;

        if (panel.Dirty || !panel.Visible || hero == null || !map || !map.Dynamic || map.hero != hero ||
            selected < 0 || selected >= panel.compatibleBackgrounds.Count ||
            panel.compatibleBackgrounds[selected] != hero.BackgroundDefinition ||
            !panel.backgroundFlagsGroup.gameObject.activeSelf || !panel.alignmentFlagsGroup.gameObject.activeSelf ||
            !panel.selectedBackgroundPersonalityFlagsMap.TryGetValue(hero.BackgroundDefinition, out var backgroundFlags))
        {
            return false;
        }

        var alignmentFlags = panel.selectedAlignmentPersonalityFlags;

        RefreshSelectedFlags(panel.backgroundPersonalityFlagsTable, backgroundFlags, hero.BackgroundOptionalPersonalityFlags);
        RefreshSelectedFlags(panel.alignmentPersonalityFlagsTable, alignmentFlags, hero.AlignmentOptionaPersonalityFlags);

        panel.validateBackgroundFlagsButton.interactable = panel.personalityFlagsValidationGroup.CanValidate =
            backgroundFlags.Count == 2;
        panel.validateAlignmentFlagsButton.interactable = panel.alignmentFlagsValidationGroup.CanValidate =
            alignmentFlags.Count == 2;
        panel.RefreshValidationGroups();
        map.RefreshNow();
        RefreshStageStatus(panel);

        return true;
    }

    private static void RefreshSelectedFlags(Transform table, List<string> selectedFlags, List<string> heroFlags)
    {
        heroFlags.Clear();
        heroFlags.AddRange(selectedFlags);

        var canSelect = selectedFlags.Count < 2;

        for (var i = 0; i < table.childCount; i++)
        {
            var child = table.GetChild(i);

            if (!child.gameObject.activeSelf)
            {
                continue;
            }

            var flag = child.GetComponent<PersonalityFlagToggle>();
            var isOn = selectedFlags.Contains(flag.PersonalityFlagDefinition.Name);
            var interactable = isOn || canSelect;

            if (flag.Toggle.isOn != isOn || flag.Toggle.interactable != interactable)
            {
                flag.Refresh(isOn, interactable);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageBackgroundSelectionPanel),
        nameof(CharacterStageBackgroundSelectionPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterStageBackgroundSelectionPanel __instance)
        {
            //PATCH: avoids a restart when enabling / disabling backgrounds on the Mod UI panel
            __instance.compatibleBackgrounds.Clear();
            __instance.selectedBackgroundPersonalityFlagsMap.Clear();

            foreach (var key in
                     DatabaseRepository.GetDatabase<CharacterBackgroundDefinition>())
            {
                if (key.GuiPresentation.Hidden)
                {
                    continue;
                }

                __instance.compatibleBackgrounds.Add(key);
                __instance.selectedBackgroundPersonalityFlagsMap.Add(key,
                    key.OptionalPersonalityFlags.Count == 2
                        ? [..key.DefaultOptionalPersonalityFlags]
                        : []);
            }

            __instance.compatibleBackgrounds.Sort(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageBackgroundSelectionPanel),
        nameof(CharacterStageBackgroundSelectionPanel.OnFeatureChoiceChangedCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnFeatureChoiceChangedCb_Patch
    {
        [UsedImplicitly]
        public static void Prefix([CanBeNull] FeatureDescriptionItem __0)
        {
            FeatureDescriptionItemPatcher.TryPersistCharacterCreationFeatureSetDefaultSelection(__0);
        }

        [UsedImplicitly]
        public static void Postfix([CanBeNull] FeatureDescriptionItem __0)
        {
            FeatureDescriptionItemPatcher.RefreshSelectionFeatureDisplayLayout(__0);
        }
    }

    [HarmonyPatch(typeof(CharacterStageBackgroundSelectionPanel),
        nameof(CharacterStageBackgroundSelectionPanel.CanProceedToNextStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanProceedToNextStage_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageBackgroundSelectionPanel __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            __result = !Tabletop2024Context.HasDuplicateHumanOriginFeat(__instance.currentHero, out _);
        }
    }
}
