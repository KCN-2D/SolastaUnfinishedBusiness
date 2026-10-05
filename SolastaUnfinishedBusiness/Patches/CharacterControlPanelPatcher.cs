using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterControlPanelPatcher
{
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ActionPanelRefreshed_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CharacterControlPanelBattle),
                nameof(CharacterControlPanelBattle.ActionPanelRefreshed));
            yield return AccessTools.Method(typeof(CharacterControlPanelExploration),
                nameof(CharacterControlPanelExploration.ActionPanelRefreshed));
        }

        [UsedImplicitly]
        public static void Postfix(CharacterControlPanel __instance)
        {
            // Native callbacks finalize the gamepad shortcut visibility after each
            // action panel's RefreshActions. Refit against that final reserved area.
            foreach (var panel in __instance.GetComponentsInChildren<CharacterActionPanel>(true))
            {
                GuiLabelPatcher.FitActionPanelTitles(panel);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterControlPanel), nameof(CharacterControlPanel.OnInspectCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnInspectCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterControlPanel __instance)
        {
            return !SimulacrumEquipmentPanel.TryOpen(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterControlPanel), nameof(CharacterControlPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterControlPanel __instance)
        {
            if (Main.Settings.WideScreenBattleUI)
            {
                float aspectRatio = UiHelpers.GetAspectRatio();
                if (aspectRatio > 1.778f)
                {
                    // The Overlay Canvas may use a higher internal resolution than the actual window size. 
                    //Using it as a reference ensures this works even for unconventional small but wide window sizes.
                    float expanded = UiHelpers.GetOverlayCanvasSize().x - 210;
                    __instance.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, expanded);
                }                
            }
        }
    }
}
