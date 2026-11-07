using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharactersPanelPatcher
{
    private static CharacterPlateToggle GetSelectedPlate(CharactersPanel panel) =>
        panel.selectedPlate >= 0 && panel.selectedPlate < panel.characterPlates.Count
            ? panel.characterPlates[panel.selectedPlate]
            : null;

    private static Button RefreshCharacterEditButton(CharactersPanel panel, bool appearanceOnly)
    {
        var buttons = panel.transform.Find("ButtonsBar2");
        var footer = panel.GetComponent<CharacterFooterLayout>();
        var button = appearanceOnly ? footer?.AppearanceButton : footer?.RespecButton;

        if (!button)
        {
            // The footer owns placement after creating the action in its native second row.
            button = Object.Instantiate(panel.levelUpButton, buttons);
            button.name = appearanceOnly ? "AppearanceButton" : "RespecButton";
            button.transform.SetAsFirstSibling();
            var setup = button.GetComponent<GuiButtonSetup>();
            setup.DeactivateGamepadBindingButton();
            setup.enabled = false;
            var confirm = panel.inspectButton.GetComponent<GuiButtonSetup>().GamepadBindingButton;
            setup.GamepadBindingButton.ActionMapName = confirm.ActionMapName;
            setup.GamepadBindingButton.actionName = confirm.ActionName;
            // The native list map has named actions rather than a UI Submit action.
            // Keep native binding, glyph and enable/disable ownership for these actions too.
            setup.enabled = true;
            var navigation = button.GetComponent<GuiNavigationSetup>() ??
                             button.gameObject.AddComponent<GuiNavigationSetup>();
            navigation.gamepadNavigationMode = Navigation.Mode.Explicit;
            navigation.ForceNavigationMode(Navigation.Mode.Explicit);
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() =>
            {
                var selected = GetSelectedPlate(panel);
                var runner = ServiceRepository.GetService<IGameService>() as GameManager;

                if (!selected || !selected.Valid || !runner ||
                    !(appearanceOnly
                        ? ToolsContext.AppearanceEdition.CanStart(selected.Filename)
                        : ToolsContext.FunctorRespec.CanStartFromCharacterPool(selected.Filename)))
                {
                    return;
                }

                panel.ActionTaken?.Invoke(selected.Filename);
                // The menu is hidden during creation, so it cannot own the coroutine.
                runner.StartCoroutine(appearanceOnly
                    ? ToolsContext.AppearanceEdition.Start(selected.Filename, panel.OriginScreen)
                    : ToolsContext.FunctorRespec.StartFromCharacterPool(selected.Filename, panel.OriginScreen));
            });
        }

        button.gameObject.SetActive(true);
        button.transform.Find("Label").GetComponent<GuiLabel>().Text = appearanceOnly
            ? "Screen/&CharacterAppearanceTitle"
            : "RestActivity/&RestActivityRespecTitle";
        foreach (var tooltip in button.GetComponentsInChildren<GuiTooltip>(true))
        {
            tooltip.Content = appearanceOnly
                ? "Screen/&CharacterAppearanceDescription"
                : "RestActivity/&RestActivityRespecDescription";
        }

        var plate = GetSelectedPlate(panel);
        button.interactable = plate && plate.Valid && (appearanceOnly
            ? ToolsContext.AppearanceEdition.CanStart(plate.Filename)
            : ToolsContext.FunctorRespec.CanStartFromCharacterPool(plate.Filename));

        return button;
    }

    private static bool AllowFooterActionBinding(GamepadBindingButton binding,
        InputAction.CallbackContext context, bool performed)
    {
        // Unbind can clear the binding's button during another listener's dispatch.
        // The component hierarchy retains the footer owner for the entire input event.
        CharactersPanel panel = null;
        for (var parent = binding.transform; parent && !panel; parent = parent.parent)
        {
            panel = parent.GetComponent<CharactersPanel>();
        }
        var footer = panel ? panel.GetComponent<CharacterFooterLayout>() : null;
        if (!footer)
        {
            return true;
        }

        return footer.AllowBinding(binding, context, performed);
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class FooterActionBinding_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GamepadBindingButton), "OnButtonStarted");
            yield return AccessTools.Method(typeof(GamepadBindingButton), "OnButtonPerformed");
        }

        [UsedImplicitly]
        public static bool Prefix(GamepadBindingButton __instance, InputAction.CallbackContext __0,
            MethodBase __originalMethod) => AllowFooterActionBinding(__instance, __0,
            __originalMethod.Name == "OnButtonPerformed");
    }

    private sealed class CharacterFooterLayout : MonoBehaviour
    {
        private readonly List<RectTransform> rows = [];
        private CharactersPanel owner;
        private Button[] buttons;
        private float[] widths;
        private Vector2 firstPosition;
        private float rowHeight;
        private float rowGap;
        private float lastWidth;
        private int lastVisibility;
        private bool pending = true;
        private InputAction confirmAction;
        private double startedTime = double.NaN, performedTime = double.NaN;
        private int startedFrame = -1, performedFrame = -1;
        private Button startedTarget, performedTarget;

        internal Button RespecButton { get; private set; }
        internal Button AppearanceButton { get; private set; }

        internal bool AllowBinding(GamepadBindingButton binding, InputAction.CallbackContext context, bool performed)
        {
            var appearanceBinding = AppearanceButton.GetComponent<GuiButtonSetup>().GamepadBindingButton;
            var respecBinding = RespecButton.GetComponent<GuiButtonSetup>().GamepadBindingButton;
            var added = binding == appearanceBinding || binding == respecBinding;
            var input = Gui.InputService;
            if (input == null)
            {
                return !added;
            }

            var action = owner.inspectButton.GetComponent<GuiButtonSetup>().GamepadBindingButton.FindInputAction(input);
            if (context.action != action)
            {
                return !added;
            }

            if (confirmAction != action)
            {
                confirmAction = action;
                startedTime = performedTime = double.NaN;
                startedFrame = performedFrame = -1;
            }

            ref var time = ref (performed ? ref performedTime : ref startedTime);
            ref var frame = ref (performed ? ref performedFrame : ref startedFrame);
            ref var target = ref (performed ? ref performedTarget : ref startedTarget);
            // All subscribers share one routing receipt. The first click can hide this
            // panel and change selection (or cancel its map) before later subscribers run.
            if (time != context.time && !(frame == Time.frameCount && (!owner.Visible || owner.Hiding)))
            {
                time = context.time;
                frame = Time.frameCount;
                var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
                var focused = selected ? selected.GetComponentInParent<Button>() : null;
                target = Gui.GamepadActive && owner.Visible && !owner.Hiding && input.IsActivePanel(owner) &&
                         (focused == AppearanceButton || focused == RespecButton) ? focused : null;
            }

            // Other native shortcuts retain their existing dispatch. An added action
            // owns confirmation only for the event that began with its control focused.
            return added ? target && binding == target.GetComponent<GuiButtonSetup>().GamepadBindingButton : !target;
        }

        internal void Refresh(CharactersPanel panel, Button respec, Button appearance)
        {
            if (!owner)
            {
                owner = panel;
                RespecButton = respec;
                AppearanceButton = appearance;
                rows.Add((RectTransform)panel.transform.Find("ButtonsBar"));
                rows.Add((RectTransform)panel.transform.Find("ButtonsBar2"));
                firstPosition = rows[0].anchoredPosition;
                rowHeight = rows[0].rect.height;
                rowGap = Mathf.Max(0f, firstPosition.y - rows[1].anchoredPosition.y - rowHeight);
                buttons =
                [
                    panel.CancelButton, panel.inspectButton, panel.accessStoreButton, panel.newCharacterButton,
                    panel.levelUpButton, appearance, panel.characterCheckerButton, respec, panel.deleteButton
                ];
                widths = new float[buttons.Length];
            }

            Reflow(true);
        }

        private void OnEnable() => pending = true;

        private void OnRectTransformDimensionsChange() => pending = true;

        private void LateUpdate() => Reflow(pending);

        private void Reflow(bool force)
        {
            if (!owner || !owner.gameObject.activeInHierarchy)
            {
                return;
            }

            var panelRect = owner.GetComponent<RectTransform>();
            var left = panelRect.InverseTransformPoint(rows[0].TransformPoint(rows[0].rect.min)).x;
            var inset = Mathf.Max(0f, left - panelRect.rect.xMin);
            var availableWidth = panelRect.rect.xMax - left - inset;

            if (availableWidth <= 0f)
            {
                return;
            }

            var visibility = 0;
            var changed = force || Mathf.Abs(lastWidth - availableWidth) > 0.5f;

            for (var i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];

                // An unused extra row may be hidden while its native action becomes active again.
                if (!button || !button.gameObject.activeSelf)
                {
                    continue;
                }

                visibility |= 1 << i;
                var rect = button.GetComponent<RectTransform>();

                if (!button.gameObject.activeInHierarchy)
                {
                    rect.SetParent(rows[0], false);
                }

                if (force)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                }

                var width = Mathf.Max(LayoutUtility.GetMinWidth(rect), LayoutUtility.GetPreferredWidth(rect));
                changed |= Mathf.Abs(widths[i] - width) > 0.5f;
                widths[i] = width;
            }

            if (!changed && lastVisibility == visibility)
            {
                return;
            }

            pending = false;
            lastWidth = availableWidth;
            lastVisibility = visibility;
            var spacing = rows[0].GetComponent<HorizontalLayoutGroup>().spacing;
            var heights = new List<float> { rowHeight };
            var rowIndex = 0;
            var widthUsed = 0f;

            for (var i = 0; i < buttons.Length; i++)
            {
                if ((visibility & (1 << i)) == 0)
                {
                    continue;
                }

                if (widthUsed > 0f && widthUsed + spacing + widths[i] > availableWidth + 0.5f)
                {
                    rowIndex++;
                    widthUsed = 0f;
                    heights.Add(rowHeight);

                    if (rowIndex == rows.Count)
                    {
                        rows.Add(CreateRow(rows[1], rowIndex));
                    }
                }

                var rect = buttons[i].GetComponent<RectTransform>();
                rect.SetParent(rows[rowIndex], false);
                rect.SetAsLastSibling();
                heights[rowIndex] = Mathf.Max(heights[rowIndex], LayoutUtility.GetPreferredHeight(rect));
                widthUsed += (widthUsed > 0f ? spacing : 0f) + widths[i];
            }

            var y = firstPosition.y;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var used = i < heights.Count;

                row.gameObject.SetActive(used || i < 2);

                if (!used)
                {
                    continue;
                }

                row.anchoredPosition = new Vector2(firstPosition.x, y);
                row.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, heights[i]);
                LayoutRebuilder.ForceRebuildLayoutImmediate(row);

                if (i + 1 < heights.Count)
                {
                    y -= (heights[i] + heights[i + 1]) * 0.5f + rowGap;
                }
            }

            if (Gui.GamepadActive)
            {
                Gui.InputService?.RecomputeSelectableNavigation(owner.MainAxisOnlyNavigation,
                    owner.VerticalNavigationOnly, false);
            }
        }

        private static RectTransform CreateRow(RectTransform template, int index)
        {
            // Clone row geometry and layout only; cloning its children would duplicate native actions.
            var row = new GameObject($"ButtonsBar{index + 1}", typeof(RectTransform),
                typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
            row.SetParent(template.parent, false);
            row.anchorMin = template.anchorMin;
            row.anchorMax = template.anchorMax;
            row.pivot = template.pivot;
            row.sizeDelta = template.sizeDelta;
            var original = template.GetComponent<HorizontalLayoutGroup>();
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(original.padding.left, original.padding.right,
                original.padding.top, original.padding.bottom);
            layout.spacing = original.spacing;
            layout.childAlignment = original.childAlignment;
            layout.childControlWidth = original.childControlWidth;
            layout.childControlHeight = original.childControlHeight;
            layout.childForceExpandWidth = original.childForceExpandWidth;
            layout.childForceExpandHeight = original.childForceExpandHeight;
            var originalFitter = template.GetComponent<ContentSizeFitter>();
            var fitter = row.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = originalFitter.horizontalFit;
            fitter.verticalFit = originalFitter.verticalFit;

            return row;
        }
    }

    //PATCH: Keeps last level up hero selected
    [HarmonyPatch(typeof(CharactersPanel), nameof(CharactersPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharactersPanel __instance, out float __state)
        {
            // Remember the current scroll position
            __state = __instance.charactersScrollview.verticalNormalizedPosition;
        }

        [UsedImplicitly]
        public static void Postfix(CharactersPanel __instance, float __state)
        {
            var filename = Global.LastLevelUpHeroFilename;
            if (filename == null || !ServiceRepository.GetService<ICharacterPoolService>().Pool.ContainsKey(filename))
            {
                return;
            }

            var plate = __instance.characterPlates.Find(x =>
                string.Equals(x.Filename, filename, System.StringComparison.OrdinalIgnoreCase));
            if (!plate || !plate.gameObject.activeSelf)
            {
                // A saved copy can belong to a different source category than the original.
                __instance.characterFilteringGroup.allToggle.Toggle.isOn = true;
                plate = __instance.characterPlates.Find(x =>
                    string.Equals(x.Filename, filename, System.StringComparison.OrdinalIgnoreCase));
            }

            if (!plate || !plate.gameObject.activeSelf)
            {
                return;
            }

            __instance.OnSelectPlate(plate);
            Global.LastLevelUpHeroFilename = null;

            // Reset the scroll position because OnBeginShow sets it to 1.0f.
            // This keeps the selected character in view unless the panel is sorted by level
            // in which case the character will move.

            __instance.charactersScrollview.verticalNormalizedPosition = __state;
        }
    }

    //PATCH: add checkboxes to heroes plate to allow heroes pre-selection (DEFAULT_PARTY)
    [HarmonyPatch(typeof(CharactersPanel), nameof(CharactersPanel.EnumeratePlates))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumeratePlates_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharactersPanel __instance)
        {
            if (!SettingsContext.GuiModManagerInstance.EnablePartyToggles)
            {
                ToolsContext.Disable(__instance.charactersTable);

                return;
            }

            var max = Main.Settings.OverridePartySize;
            var characterPoolService = ServiceRepository.GetService<ICharacterPoolService>();

            Main.Settings.DefaultPartyHeroes.RemoveAll(x => !characterPoolService.ContainsCharacter(x));

            for (var i = 0; i < __instance.charactersTable.childCount; i++)
            {
                var character = __instance.charactersTable.GetChild(i);
                var checkBox = character.Find("DefaultHeroToggle") ?? ToolsContext.CreateHeroCheckbox(character);
                var tooltip = checkBox.Find("Background").GetComponentInChildren<GuiTooltip>();
                var checkBoxToggle = checkBox.GetComponentInChildren<Toggle>();

                tooltip.AnchorMode = TooltipDefinitions.AnchorMode.LEFT_CENTER;
                tooltip.Content = Gui.Format("ToolTip/&CheckBoxDefaultPartyTitle", max.ToString());

                checkBoxToggle.gameObject.SetActive(true);
                checkBoxToggle.onValueChanged = new Toggle.ToggleEvent();
                checkBoxToggle.isOn = Main.Settings.DefaultPartyHeroes.Contains(character.name);
                checkBoxToggle.onValueChanged.AddListener(delegate
                {
                    if (checkBoxToggle.isOn)
                    {
                        Main.Settings.DefaultPartyHeroes.Add(character.name);
                        ToolsContext.Rebase(character.parent.transform, max);
                    }
                    else
                    {
                        Main.Settings.DefaultPartyHeroes.Remove(character.name);
                    }
                });
            }

            ToolsContext.Rebase(__instance.charactersTable, max);
        }
    }

    //PATCH: enable the character checker button on main > characters
    [HarmonyPatch(typeof(CharactersPanel), nameof(CharactersPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharactersPanel __instance)
        {
            var checkerButton = __instance.characterCheckerButton;
            checkerButton.gameObject.SetActive(
                SettingsContext.GuiModManagerInstance.EnableCharacterChecker);

            checkerButton.transform.Find("Label").GetComponent<GuiLabel>().Text = "Screen/&CharacterCheckerTitle";
            foreach (var tooltip in checkerButton.GetComponentsInChildren<GuiTooltip>(true))
            {
                tooltip.Content = "Screen/&CharacterCheckerDescription";
            }

            var respec = RefreshCharacterEditButton(__instance, false);
            var appearance = RefreshCharacterEditButton(__instance, true);
            var footer = __instance.GetComponent<CharacterFooterLayout>() ??
                         __instance.gameObject.AddComponent<CharacterFooterLayout>();

            footer.Refresh(__instance, respec, appearance);
        }
    }
}
