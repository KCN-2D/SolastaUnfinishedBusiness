using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class TooltipPanelPatcher
{
    private const int TooltipForegroundSortingOrder = 31000;

    private static TooltipPanel ActiveTooltipPanel;
    private static readonly List<TooltipPanel> TooltipForegroundPanels = new();
    private static readonly Dictionary<TooltipPanel, TooltipForegroundState> TooltipForegroundStates = new();

    [HarmonyPatch(typeof(TooltipPanel), nameof(TooltipPanel.SetupFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetupFeatures_Patch
    {
        [UsedImplicitly]
        public static void Prefix(TooltipPanel __instance, ref TooltipDefinitions.Scope scope)
        {
            FloatingPanelBounds.RestoreTooltipBounds(__instance);

            //PATCH: swaps holding ALT behavior for tooltips
            if (!SettingsContext.GuiModManagerInstance.InvertTooltipBehavior)
            {
                return;
            }

            scope = scope switch
            {
                TooltipDefinitions.Scope.Simplified => TooltipDefinitions.Scope.Detailed,
                TooltipDefinitions.Scope.Detailed => TooltipDefinitions.Scope.Simplified,
                _ => scope
            };
        }

        [UsedImplicitly]
        public static void Postfix(TooltipPanel __instance)
        {
            Tooltips.ModifyWidth<TooltipPanelWidthModifier, TooltipPanel>(__instance);
            FloatingPanelBounds.ConfigureTooltipBounds(__instance);
        }
    }

    [HarmonyPatch(typeof(TooltipPanel), nameof(TooltipPanel.ShowContent))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ShowContent_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipPanel __instance)
        {
            ActiveTooltipPanel = __instance;
            ApplyTooltipForeground(__instance);
        }
    }

    [HarmonyPatch(typeof(TooltipPanel), nameof(TooltipPanel.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipPanel __instance)
        {
            if (ActiveTooltipPanel == __instance)
            {
                ActiveTooltipPanel = null;
            }

            RestoreTooltipForeground(__instance);
            FloatingPanelBounds.RestoreTooltipBounds(__instance);
        }
    }

    [HarmonyPatch(typeof(ScrollRect), nameof(ScrollRect.OnScroll))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ScrollRect_OnScroll_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ScrollRect __instance)
        {
            return !FloatingPanelBounds.ShouldSuppressBackgroundWheel(__instance);
        }
    }

    [HarmonyPatch(typeof(GuiDropdown), "CreateDropdownList")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GuiDropdown_CreateDropdownList_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            ApplyTooltipForeground(ActiveTooltipPanel);
        }
    }

    [HarmonyPatch(typeof(GuiManualScroll), nameof(GuiManualScroll.ScrollPerformed))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GuiManualScroll_ScrollPerformed_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GuiManualScroll __instance)
        {
            return !FloatingPanelBounds.ShouldSuppressBackgroundWheel(__instance);
        }
    }

    [HarmonyPatch(typeof(ScrollRectAutoScroll), "InputScroll")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ScrollRectAutoScroll_InputScroll_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ScrollRectAutoScroll __instance)
        {
            return !FloatingPanelBounds.ShouldSuppressBackgroundWheel(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeature), nameof(TooltipFeature.Setup))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Setup_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeature __instance)
        {
            Tooltips.ModifyWidth(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureEffectsEnumerator), nameof(TooltipFeatureEffectsEnumerator.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureEffectsEnumerator_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureEffectsEnumerator __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureEffectsEnumWidthMod, TooltipFeatureEffectsEnumerator>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureSubSpellsEnumerator), nameof(TooltipFeatureSubSpellsEnumerator.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureSubSpellsEnumerator_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureSubSpellsEnumerator __instance)
        {
            Tooltips.ModifyWidth<TooltipSubSpellEnumWidthModifier, TooltipFeatureSubSpellsEnumerator>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureSpellParameters), nameof(TooltipFeatureSpellParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureSpellParameters_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureSpellParameters __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureSpellParamsWidthModifier, TooltipFeatureSpellParameters>(__instance);
            Tooltips.RefreshAdaptiveSpellParameterTopRow(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureBaseMagicParameters), nameof(TooltipFeatureBaseMagicParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureBaseMagicParameters_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureBaseMagicParameters __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureBaseMagicParamsWidthModifier, TooltipFeatureBaseMagicParameters>(
                __instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureTagsEnumerator), nameof(TooltipFeatureTagsEnumerator.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureTagsEnumerator_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureTagsEnumerator __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureTagsEnumWidthModifier, TooltipFeatureTagsEnumerator>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureSpellAdvancement), nameof(TooltipFeatureSpellAdvancement.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureSpellAdvancement_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureSpellAdvancement __instance)
        {
            Tooltips.NormalizeSpellAdvancement(__instance);
            Tooltips.ModifyWidth<TooltipFeatureSpellAdvancementWidthMod, TooltipFeatureSpellAdvancement>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureDeviceParameters), nameof(TooltipFeatureDeviceParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureDeviceParameters_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureDeviceParameters __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureDeviceParametersWidthMod, TooltipFeatureDeviceParameters>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureItemPropertiesEnumerator), nameof(TooltipFeatureItemPropertiesEnumerator.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureItemPropertiesEnumerator_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureItemPropertiesEnumerator __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureItemPropertiesEnumWidthMod, TooltipFeatureItemPropertiesEnumerator>(
                __instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureDeviceFunctionsEnumerator),
        nameof(TooltipFeatureDeviceFunctionsEnumerator.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureDeviceFunctionsEnumerator_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureDeviceFunctionsEnumerator __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureDeviceFunctionsEnumWidthMod, TooltipFeatureDeviceFunctionsEnumerator>(
                __instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureItemStats), nameof(TooltipFeatureItemStats.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureItemStats_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureItemStats __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureItemStatsWidthMod, TooltipFeatureItemStats>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureWeaponParameters), nameof(TooltipFeatureWeaponParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureWeaponParameters_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureWeaponParameters __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureWeaponParametersWidthMod, TooltipFeatureWeaponParameters>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureArmorParameters), nameof(TooltipFeatureArmorParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureArmorParameters_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureArmorParameters __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureArmorParamsWidthMod, TooltipFeatureArmorParameters>(__instance);
        }
    }

    //TODO: move to separate file
    [HarmonyPatch(typeof(TooltipFeatureLightSourceParameters), nameof(TooltipFeatureLightSourceParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureLightSourceParameters_Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureLightSourceParameters __instance)
        {
            Tooltips.ModifyWidth<TooltipFeatureLightSourceParamsWidthMod, TooltipFeatureLightSourceParameters>(
                __instance);
        }
    }

    private static void ApplyTooltipForeground(TooltipPanel tooltipPanel)
    {
        if (!tooltipPanel)
        {
            return;
        }

        RemoveInvalidTooltipForegroundPanels();

        if (TooltipForegroundStates.TryGetValue(tooltipPanel, out var state) && !state.Canvas)
        {
            TooltipForegroundStates.Remove(tooltipPanel);
        }

        if (!TooltipForegroundStates.TryGetValue(tooltipPanel, out state))
        {
            var canvas = tooltipPanel.GetComponent<Canvas>();
            var addedCanvas = !canvas;

            if (addedCanvas)
            {
                canvas = tooltipPanel.gameObject.AddComponent<Canvas>();
            }

            state = new TooltipForegroundState(canvas, addedCanvas);
            TooltipForegroundStates[tooltipPanel] = state;
        }

        if (!state.Canvas)
        {
            return;
        }

        TooltipForegroundPanels.Remove(tooltipPanel);
        TooltipForegroundPanels.Add(tooltipPanel);

        ApplyTooltipForegroundOrders();
    }

    private static void RestoreTooltipForeground(TooltipPanel tooltipPanel)
    {
        if (!tooltipPanel || !TooltipForegroundStates.TryGetValue(tooltipPanel, out var state))
        {
            return;
        }

        TooltipForegroundPanels.Remove(tooltipPanel);
        TooltipForegroundStates.Remove(tooltipPanel);
        state.Restore();

        ApplyTooltipForegroundOrders();
    }

    private static void RemoveInvalidTooltipForegroundPanels()
    {
        for (var i = TooltipForegroundPanels.Count - 1; i >= 0; i--)
        {
            var tooltipPanel = TooltipForegroundPanels[i];

            if (!tooltipPanel ||
                !tooltipPanel.gameObject.activeInHierarchy ||
                !TooltipForegroundStates.TryGetValue(tooltipPanel, out var state) ||
                !state.Canvas)
            {
                TooltipForegroundPanels.RemoveAt(i);
            }
        }
    }

    private static void ApplyTooltipForegroundOrders()
    {
        RemoveInvalidTooltipForegroundPanels();

        for (var i = 0; i < TooltipForegroundPanels.Count; i++)
        {
            var tooltipPanel = TooltipForegroundPanels[i];

            if (!TooltipForegroundStates.TryGetValue(tooltipPanel, out var state) || !state.Canvas)
            {
                continue;
            }

            state.Canvas.overrideSorting = true;
            state.Canvas.sortingOrder = TooltipForegroundSortingOrder + i;
        }
    }

    private sealed class TooltipForegroundState
    {
        private readonly bool _addedCanvas;
        private readonly bool _overrideSorting;
        private readonly int _sortingLayerId;
        private readonly int _sortingOrder;

        internal TooltipForegroundState(Canvas canvas, bool addedCanvas)
        {
            Canvas = canvas;
            _addedCanvas = addedCanvas;

            if (!canvas)
            {
                return;
            }

            _overrideSorting = canvas.overrideSorting;
            _sortingLayerId = canvas.sortingLayerID;
            _sortingOrder = canvas.sortingOrder;
        }

        internal Canvas Canvas { get; }

        internal void Restore()
        {
            if (!Canvas)
            {
                return;
            }

            if (_addedCanvas)
            {
                UnityEngine.Object.DestroyImmediate(Canvas);
                return;
            }

            Canvas.overrideSorting = _overrideSorting;
            Canvas.sortingLayerID = _sortingLayerId;
            Canvas.sortingOrder = _sortingOrder;
        }
    }
}

//TODO: move to separate file
[HarmonyPatch(typeof(TooltipFeaturePowerParameters), nameof(TooltipFeaturePowerParameters.Bind))]
[SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
[UsedImplicitly]
public static class TooltipFeaturePowerParameters_Bind_Patch
{
    [UsedImplicitly]
    public static void Postfix(TooltipFeaturePowerParameters __instance)
    {
        Tooltips.ModifyWidth<TooltipFeaturePowerParamsWidthMod, TooltipFeaturePowerParameters>(__instance);
        Tooltips.RefreshAdaptivePowerParameterTopRow(__instance);
    }
}

//TODO: move to separate file
[HarmonyPatch(typeof(TooltipFeaturePrerequisites), nameof(TooltipFeaturePrerequisites.Bind))]
[SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
[UsedImplicitly]
public static class TooltipFeaturePrerequisites_Bind_Patch
{
    [UsedImplicitly]
    public static void Postfix(TooltipFeaturePrerequisites __instance)
    {
        Tooltips.ModifyWidth<TooltipFeaturePrerequisitesWidthMod, TooltipFeaturePrerequisites>(__instance);
    }
}
