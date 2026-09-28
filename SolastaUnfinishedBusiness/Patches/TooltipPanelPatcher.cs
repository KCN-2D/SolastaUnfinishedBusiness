using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class TooltipPanelPatcher
{
    private const int TooltipForegroundSortingOrder = 31000;

    [ThreadStatic]
    private static RulesetCharacter _effectFormattingCharacter;

    internal static RulesetCharacter EffectFormattingCharacter => _effectFormattingCharacter;

    private static TooltipPanel ActiveTooltipPanel;
    private static readonly List<TooltipPanel> TooltipForegroundPanels = new();
    private static readonly Dictionary<TooltipPanel, TooltipForegroundState> TooltipForegroundStates = new();

    [HarmonyPatch(
        typeof(TooltipFeatureMonsterAttacksEnumerator),
        nameof(TooltipFeatureMonsterAttacksEnumerator.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureMonsterAttacksEnumeratorBind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            TooltipFeatureMonsterAttacksEnumerator __instance,
            ITooltip tooltip,
            RectTransform ___table,
            GameObject ___attackIterationPrefab)
        {
            if (tooltip?.DataProvider is not ILiveMonsterAttacksProvider provider)
            {
                return true;
            }

            var attackModes = provider.LiveAttackModes
                .Where(mode =>
                    mode is
                    {
                        SourceDefinition: not null,
                        EffectDescription: not null,
                        ActionType: ActionDefinitions.ActionType.Main or
                            ActionDefinitions.ActionType.Bonus
                    })
                .ToArray();

            if (attackModes.Length == 0)
            {
                __instance.gameObject.SetActive(false);

                return false;
            }

            __instance.gameObject.SetActive(true);

            while (___table.childCount < attackModes.Length)
            {
                Gui.GetPrefabFromPool(___attackIterationPrefab, ___table);
            }

            for (var i = 0; i < attackModes.Length; i++)
            {
                var child = ___table.GetChild(i);
                var attackModeBox = child.GetComponent<AttackModeBox>();

                child.gameObject.SetActive(true);
                attackModeBox.Unbind();
                attackModeBox.Bind(attackModes[i]);
            }

            for (var i = attackModes.Length; i < ___table.childCount; i++)
            {
                var child = ___table.GetChild(i);

                child.GetComponent<AttackModeBox>().Unbind();
                child.gameObject.SetActive(false);
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(___table);
            __instance.RectTransform.sizeDelta = new Vector2(
                __instance.RectTransform.sizeDelta.x,
                ___table.rect.height - ___table.anchoredPosition.y);

            return false;
        }
    }

    [HarmonyPatch(typeof(TooltipFeatureHeader), nameof(TooltipFeatureHeader.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureHeaderBind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(Image ___image)
        {
            Tooltips.ResetHeaderImage(___image);
        }

        [UsedImplicitly]
        public static void Postfix(
            TooltipFeatureHeader __instance,
            ITooltip tooltip,
            Image ___image,
            RectTransform ___mask)
        {
            Tooltips.FitConditionHeaderImage(__instance, tooltip);
            Tooltips.UpdateHeaderPortrait(tooltip, ___image, ___mask);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var width = AccessTools.PropertyGetter(typeof(Texture), nameof(Texture.width));
            var height = AccessTools.PropertyGetter(typeof(Texture), nameof(Texture.height));

            // Native cover sizing uses the entire texture, which distorts sprites cut from an atlas.
            // Replace both dimensions together, keeping the native mask, anchoring and cover branches.
            if (code.Count(instruction => instruction.Calls(width)) != 1 ||
                code.Count(instruction => instruction.Calls(height)) != 1)
            {
                Main.Error("Failed to apply transpiler patch [TooltipFeatureHeader.Bind]: " +
                           "expected one texture width and height calculation.");

                return code;
            }

            return code
                .ReplaceCalls(width, "TooltipFeatureHeader.Bind.SpriteWidth",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(Tooltips), nameof(Tooltips.GetHeaderSpriteWidth))))
                .ReplaceCalls(height, "TooltipFeatureHeader.Bind.SpriteHeight",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(Tooltips), nameof(Tooltips.GetHeaderSpriteHeight))));
        }
    }

    [HarmonyPatch(typeof(TooltipFeatureHeader), nameof(TooltipFeatureHeader.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureHeaderUnbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(Image ___image)
        {
            Tooltips.ResetHeaderImage(___image);
        }
    }

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

    [HarmonyPatch(typeof(TooltipFeatureEffectsEnumerator), nameof(TooltipFeatureEffectsEnumerator.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureEffectsEnumerator_Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ITooltip tooltip, out RulesetCharacter __state)
        {
            __state = _effectFormattingCharacter;
            _effectFormattingCharacter =
                Tooltips.ResolveCharacter(tooltip) ?? __state;
        }

        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(
            TooltipFeatureEffectsEnumerator __instance,
            RulesetCharacter __state)
        {
            try
            {
                Tooltips.ModifyWidth<TooltipFeatureEffectsEnumWidthMod, TooltipFeatureEffectsEnumerator>(__instance);
            }
            finally
            {
                _effectFormattingCharacter = __state;
            }

            // Measure the final width before native ComputeSize.
            TooltipEffectForms.RefreshLayout(__instance);
        }

        [UsedImplicitly]
        public static Exception Finalizer(
            Exception __exception,
            RulesetCharacter __state)
        {
            _effectFormattingCharacter = __state;

            return __exception;
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatConditionOperation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatConditionOperation_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            ConditionForm.ConditionOperation operation,
            List<ConditionDefinition> conditionsList,
            ref string description,
            ref string __result)
        {
            if (operation is not (ConditionForm.ConditionOperation.RemoveDetrimentalRandom or
                ConditionForm.ConditionOperation.RemoveDetrimentalAll or ConditionForm.ConditionOperation.AddRandom) ||
                conditionsList is { Count: > 0 })
            {
                return true;
            }

            // Native list formatting indexes the last entry even when no conditions are present.
            // An empty list has no effect to describe; do not alter the shared effect definition.
            description = string.Empty;
            __result = string.Empty;

            return false;
        }

        [UsedImplicitly]
        public static void Postfix(
            ConditionDefinition conditionDefinition,
            ref string description,
            ref string __result)
        {
            if (conditionDefinition?.UsesBardicInspirationDie() != true)
            {
                return;
            }

            // Definition-only surfaces have no character context. D6 is the Bardic Inspiration
            // feature's base die and is preferable to exposing an unresolved "{0}" placeholder.
            var die = _effectFormattingCharacter?.GetBardicInspirationDieValue() ??
                      RuleDefinitions.DieType.D6;
            var dieName = Gui.FormatDieTitle(die);

            if (die == RuleDefinitions.DieType.D1)
            {
                return;
            }

            __result = __result?.Replace("{0}", dieName);
            description = description?.Replace("{0}", dieName);
        }
    }

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

    [HarmonyPatch(typeof(TooltipFeatureSpellParameters), nameof(TooltipFeatureSpellParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureSpellParameters_Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ITooltip tooltip, out System.IDisposable __state)
        {
            __state = SpellCastingValidation.EnterTooltipRepertoire(tooltip);
        }

        [UsedImplicitly]
        public static void Postfix(
            TooltipFeatureSpellParameters __instance,
            ITooltip tooltip,
            Image ___verbalComponentMarker,
            Image ___somaticComponentMarker,
            Image ___materialComponentMarker,
            Image ___specificComponentMarker,
            Color ___validColor,
            Color ___invalidColor)
        {
            Tooltips.UpdateSpellCastingTime(__instance, tooltip);
            Tooltips.ModifyWidth<TooltipFeatureSpellParamsWidthModifier, TooltipFeatureSpellParameters>(__instance);
            Tooltips.RefreshAdaptiveSpellParameterTopRow(__instance);

            var bypassAllComponents =
                SpellCastingValidation.TooltipBypassesComponentsAndCastingTime(tooltip);
            var bypassMaterialComponent =
                SpellCastingValidation.TooltipBypassesMaterialComponent(tooltip);

            if (bypassAllComponents)
            {
                foreach (var marker in new[]
                         {
                             ___verbalComponentMarker,
                             ___somaticComponentMarker,
                             ___materialComponentMarker,
                             ___specificComponentMarker
                         })
                {
                    if (marker?.gameObject.activeSelf == true)
                    {
                        marker.color = ___validColor;
                    }
                }

            }
            else if (bypassMaterialComponent)
            {
                foreach (var marker in new[]
                         {
                             ___materialComponentMarker,
                             ___specificComponentMarker
                         })
                {
                    if (marker?.gameObject.activeSelf == true)
                    {
                        marker.color = ___validColor;
                    }
                }
            }

            var duplicate = tooltip?.Context switch
            {
                RulesetCharacterSimulacrum rulesetCharacter => rulesetCharacter,
                GameLocationCharacter locationCharacter =>
                    locationCharacter.RulesetCharacter as RulesetCharacterSimulacrum,
                GuiCharacter guiCharacter =>
                    guiCharacter.RulesetCharacter as RulesetCharacterSimulacrum,
                _ => null
            };

            if (!bypassAllComponents &&
                !bypassMaterialComponent &&
                duplicate != null &&
                tooltip.DataProvider is ISpellParametersProvider provider &&
                provider.SpellDefinition is { MaterialComponentType: not RuleDefinitions.MaterialComponentType.None }
                    spellDefinition &&
                SpellCastingValidation.TryGetTooltipRepertoire(tooltip, out var repertoire))
            {
                bool valid;

                // Use the same virtual runtime path as spell activation. A
                // RulesetCharacterSimulacrum inherits the monster override, and its
                // Harmony patch applies focus, stacked-cost, dynamic-tag, infusion,
                // and grapple-hand validation as one result.
                using (SpellCastingValidation.EnterSelectedRepertoire(repertoire))
                {
                    valid = duplicate.IsComponentMaterialValid(
                        spellDefinition,
                        out _);
                }

                var color = valid ? ___validColor : ___invalidColor;

                if (___materialComponentMarker?.gameObject.activeSelf == true)
                {
                    ___materialComponentMarker.color = color;
                }

                if (___specificComponentMarker?.gameObject.activeSelf == true)
                {
                    ___specificComponentMarker.color = color;
                }
            }

        }

        [UsedImplicitly]
        public static System.Exception Finalizer(
            System.Exception __exception,
            System.IDisposable __state)
        {
            __state?.Dispose();

            return __exception;
        }
    }

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
