using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiLabelPatcher
{
    private const float ConstrainedLabelMinFontScale = 0.58f;
    private const float ConstrainedLabelAbsoluteMinFontSize = 7f;
    private const string GameMenuTitleTerm = "Screen/&GameMenuTitle";
    private const float WidthTolerance = 0.5f;

    [HarmonyPatch(typeof(GuiLabel), nameof(GuiLabel.ApplyText))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyText_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiLabel __instance)
        {
            FitActionPanelTitle(__instance);

            if (__instance.GetComponentInParent<CharacterActionItemForm>() is { } actionForm &&
                actionForm.captionLabel?.tmpText == __instance.TMP_Text)
            {
                // Bind/Refresh can run before localization applies the final caption.
                // Refit after ApplyText so long localized action names are not ellipsized
                // using stale layout measurements.
                UiTextHelpers.FitActionItemCaption(actionForm);
            }
        }
    }

    internal static void FitActionPanelTitles(CharacterActionPanel panel)
    {
        foreach (var label in panel.GetComponentsInChildren<GuiLabel>(true))
        {
            FitActionPanelTitle(label);
        }
    }

    private static void FitActionPanelTitle(GuiLabel label)
    {
        if (!label || !label.TMP_Text || string.IsNullOrEmpty(label.TMP_Text.text) ||
            label.GetComponentInParent<CharacterActionPanel>() is not { } panel)
        {
            return;
        }

        // The native category title retains its prefab width when the action
        // table shrinks. Follow the title bar's current width before fitting.
        var rect = label.TMP_Text.rectTransform;
        var gamepadImage = panel.GamepadActionImage;
        if (!gamepadImage || rect.parent is not RectTransform titleBar ||
            !gamepadImage.transform.IsChildOf(titleBar) ||
            panel.characterActionsTable.RectTransform.IsChildOf(titleBar))
        {
            return;
        }

        // Category captions share their header with the serialized shortcut. Use
        // that structure so exploration and future categories need no key exceptions.
        var padding = 8f;
        var hasGamepadIcon = gamepadImage.isActiveAndEnabled;
        if (hasGamepadIcon)
        {
            var corners = new Vector3[4];
            gamepadImage.rectTransform.GetWorldCorners(corners);
            foreach (var corner in corners)
            {
                padding = Mathf.Max(padding, titleBar.InverseTransformPoint(corner).x - titleBar.rect.xMin + 8f);
            }
        }

        rect.anchorMin = new Vector2(0f, rect.anchorMin.y);
        rect.anchorMax = new Vector2(1f, rect.anchorMax.y);
        rect.offsetMin = new Vector2(padding, rect.offsetMin.y);
        rect.offsetMax = new Vector2(-8f, rect.offsetMax.y);
        var layout = panel.GetComponent<ActionPanelTitleLayoutState>() ??
                     panel.gameObject.AddComponent<ActionPanelTitleLayoutState>();
        layout.Fit(panel, label.TMP_Text, padding + 8f);
    }

    private sealed class ActionPanelTitleLayoutState : MonoBehaviour
    {
        private CharacterActionPanel _panel;
        private RectTransform _titleBar;
        private RectTransform _table;
        private LayoutElement _element;
        private ActionPanelTitleViewportLayoutState _viewportLayout;
        private float _titleHeight;
        private float _panelHeight;
        private Vector2 _tablePosition;
        private float _tableBottom;
        private float _minimumWidth;
        private float _preferredWidth;
        private float _minimumHeight;
        private float _preferredHeight;
        private string _measuredText;
        private TMP_FontAsset _font;
        private bool _compactSpacing;
        private Vector2 _minimumSize;
        private float _singleLineWidth;

        internal void Fit(CharacterActionPanel panel, TMP_Text text, float horizontalPadding)
        {
            if (!_panel)
            {
                _panel = panel;
                _titleBar = text.rectTransform.parent as RectTransform;
                _table = panel.characterActionsTable.RectTransform;
                _titleHeight = _titleBar.rect.height;
                _panelHeight = panel.RectTransform.rect.height;
                _tablePosition = _table.anchoredPosition;
                _tableBottom = GetTableBottom();
                _element = panel.GetComponent<LayoutElement>() ?? panel.gameObject.AddComponent<LayoutElement>();
                _minimumWidth = _element.minWidth;
                _preferredWidth = _element.preferredWidth;
                _minimumHeight = _element.minHeight;
                _preferredHeight = _element.preferredHeight;
                if (panel.GetComponentInParent<ScrollRect>() is { } scrollRect && scrollRect.viewport &&
                    panel.transform.IsChildOf(scrollRect.viewport))
                {
                    _viewportLayout = scrollRect.GetComponent<ActionPanelTitleViewportLayoutState>() ??
                                      scrollRect.gameObject.AddComponent<ActionPanelTitleViewportLayoutState>();
                }
            }

            var compactSpacing = Main.Settings.FixAsianLanguagesTextWrap;
            if (_measuredText != text.text || _font != text.font || _compactSpacing != compactSpacing)
            {
                _minimumSize = UiTextHelpers.GetReadableTitleMinimumSize(text, 0.85f, 12f, out _singleLineWidth);
                _measuredText = text.text;
                _font = text.font;
                _compactSpacing = compactSpacing;
            }

            var size = _minimumSize;
            var minimumWidth = size.x + horizontalPadding;
            var width = Mathf.Max(_table.rect.width, minimumWidth);
            _element.minWidth = Mathf.Max(_minimumWidth, minimumWidth);
            _element.preferredWidth = Mathf.Max(_preferredWidth, width);
            panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            SetHeight(_singleLineWidth > width - horizontalPadding + 0.5f ? Mathf.Max(_titleHeight, size.y) : _titleHeight);
            UiTextHelpers.FitReadableTitle(text, 0.85f, 12f);
            LayoutRebuilder.MarkLayoutForRebuild(panel.RectTransform);
        }

        private void SetHeight(float height)
        {
            var extraHeight = height - _titleHeight;
            _titleBar.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            // Keep action tiles above the caption and preserve their original dimensions.
            _panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _panelHeight + extraHeight);
            _table.anchoredPosition = _tablePosition;
            var movement = _tableBottom + extraHeight - GetTableBottom();
            _table.anchoredPosition += (Vector2)_table.parent.InverseTransformVector(
                _panel.RectTransform.TransformVector(0f, movement, 0f));
            _element.minHeight = Mathf.Max(_minimumHeight, _panelHeight + extraHeight);
            _element.preferredHeight = Mathf.Max(_preferredHeight, _panelHeight + extraHeight);
            _viewportLayout?.SetExtraHeight(this, extraHeight);
        }

        private float GetTableBottom()
        {
            return _panel.RectTransform.InverseTransformPoint(
                _table.TransformPoint(_table.rect.xMin, _table.rect.yMin, 0f)).y;
        }

        private void OnDisable()
        {
            if (!_panel || !_titleBar || !_table || !_element)
            {
                return;
            }

            SetHeight(_titleHeight);
            _element.minWidth = _minimumWidth;
            _element.preferredWidth = _preferredWidth;
            _element.minHeight = _minimumHeight;
            _element.preferredHeight = _preferredHeight;
            _panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _table.rect.width);
        }
    }

    private sealed class ActionPanelTitleViewportLayoutState : MonoBehaviour
    {
        private readonly Dictionary<ActionPanelTitleLayoutState, float> _extraHeights = new();
        private RectTransform _rect;
        private float _nativeHeight;
        private bool _initialized;

        internal void SetExtraHeight(ActionPanelTitleLayoutState owner, float extraHeight)
        {
            if (extraHeight > 0f && owner.gameObject.activeInHierarchy)
            {
                if (!_initialized)
                {
                    _rect = transform as RectTransform;
                    _nativeHeight = _rect.sizeDelta.y;
                    _initialized = true;
                }

                _extraHeights[owner] = extraHeight;
            }
            else
            {
                _extraHeights.Remove(owner);
            }

            if (!_initialized)
            {
                return;
            }

            // Categories share a horizontal scroll viewport. Grow it by the tallest
            // caption, preserving its anchors, mask, and reserved scrollbar space.
            var maximum = 0f;
            foreach (var height in _extraHeights.Values)
            {
                maximum = Mathf.Max(maximum, height);
            }

            var size = _rect.sizeDelta;
            size.y = _nativeHeight + maximum;
            _rect.sizeDelta = size;
            LayoutRebuilder.MarkLayoutForRebuild(_rect);
        }

        private void OnDisable()
        {
            if (_initialized && _rect)
            {
                var size = _rect.sizeDelta;
                size.y = _nativeHeight;
                _rect.sizeDelta = size;
            }

            _extraHeights.Clear();
            _initialized = false;
        }
    }

    [HarmonyPatch(typeof(TimeAndNavigationPanel), nameof(TimeAndNavigationPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TimeAndNavigationPanelOnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TimeAndNavigationPanel __instance)
        {
            RefreshGameMenuLayout(
                __instance,
                __instance.MenuButtonGamepad,
                true);
        }
    }

    [HarmonyPatch(typeof(TimeAndNavigationPanel), "HandleBindGamepad")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TimeAndNavigationPanelHandleBindGamepad_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TimeAndNavigationPanel __instance)
        {
            RefreshGameMenuLayout(
                __instance,
                __instance.MenuButtonGamepad,
                true);
        }
    }

    [HarmonyPatch(typeof(GameLocationBaseScreen), nameof(GameLocationBaseScreen.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationBaseScreenOnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationBaseScreen __instance)
        {
            // This is the keyboard-and-mouse button below the navigation compass.
            // TimeAndNavigationPanel.MenuButtonGamepad is a different object and is
            // inactive in the control scheme shown by that HUD.
            RefreshGameMenuLayout(
                __instance,
                __instance.menuButton,
                true);
        }
    }

    [HarmonyPatch(
        typeof(GameLocationBaseScreen),
        nameof(GameLocationBaseScreen.HandleInputControlSchemeChangedForShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationBaseScreenHandleInputControlSchemeChangedForShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationBaseScreen __instance)
        {
            RefreshGameMenuLayout(
                __instance,
                __instance.menuButton,
                true);
        }
    }

    private static void RefreshGameMenuLayout(
        Component owner,
        Button menuButton,
        bool applyImmediately)
    {
        if (!owner)
        {
            return;
        }

        var watcher = owner.GetComponent<NavigationMenuLayoutWatcher>() ??
                      owner.gameObject.AddComponent<NavigationMenuLayoutWatcher>();

        watcher.Bind(menuButton, applyImmediately);
    }

    private static TMP_Text FindGameMenuLabel(Button menuButton)
    {
        if (!menuButton)
        {
            return null;
        }

        var localizedTitle = Gui.Localize(GameMenuTitleTerm);
        TMP_Text inactiveFallback = null;

        foreach (var label in menuButton.GetComponentsInChildren<GuiLabel>(true))
        {
            if (!label?.TMP_Text)
            {
                continue;
            }

            if (string.Equals(label.Text, GameMenuTitleTerm, StringComparison.Ordinal) ||
                string.Equals(label.TMP_Text.text, localizedTitle, StringComparison.Ordinal))
            {
                if (label.gameObject.activeInHierarchy)
                {
                    return label.TMP_Text;
                }

                inactiveFallback ??= label.TMP_Text;
            }
        }

        foreach (var text in menuButton.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text && string.Equals(text.text, localizedTitle, StringComparison.Ordinal))
            {
                if (text.gameObject.activeInHierarchy)
                {
                    return text;
                }

                inactiveFallback ??= text;
            }
        }

        return inactiveFallback;
    }

    private static void ApplyGameMenuLayout(
        Button menuButton,
        TMP_Text label)
    {
        if (!menuButton)
        {
            return;
        }

        if (label)
        {
            // One of the two control-scheme-specific buttons can still be inactive
            // during OnBeginShow. Establish the fixed one-line contract independently
            // of the shared fitter, which intentionally skips inactive text.
            label.enableWordWrapping = false;
            label.maxVisibleLines = 1;
            label.autoSizeTextContainer = false;

            if (menuButton.transform is RectTransform buttonRect &&
                label.rectTransform is { } labelRect)
            {
                // The prefab uses the localized text's preferred width while active. Merely
                // assigning sizeDelta in LateUpdate is undone again by the layout pass before
                // rendering. Give this fixed-size button ownership of the horizontal layout.
                if (label.GetComponent<ContentSizeFitter>() is { } fitter)
                {
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                }

                var buttonWidth = GetWidthInParentSpace(buttonRect, labelRect.parent);

                if (labelRect.parent == buttonRect)
                {
                    var layoutElement = label.GetComponent<LayoutElement>() ??
                                        label.gameObject.AddComponent<LayoutElement>();

                    layoutElement.ignoreLayout = true;
                    // HandleBindGamepad runs before the first Canvas layout. Once the
                    // label is excluded from the parent HorizontalLayoutGroup, establish
                    // both axes explicitly instead of inheriting a zero-height serialized
                    // RectTransform.
                    labelRect.anchorMin = Vector2.zero;
                    labelRect.anchorMax = Vector2.one;
                    labelRect.offsetMin = Vector2.zero;
                    labelRect.offsetMax = Vector2.zero;
                }
                else if (buttonWidth > 0f &&
                         Mathf.Abs(labelRect.rect.width - buttonWidth) > WidthTolerance)
                {
                    labelRect.SetSizeWithCurrentAnchors(
                        RectTransform.Axis.Horizontal,
                        buttonWidth);
                }
            }

            var margin = label.margin;

            if (!Mathf.Approximately(margin.x, 0f) ||
                !Mathf.Approximately(margin.z, 0f))
            {
                label.margin = new Vector4(0f, margin.y, 0f, margin.w);
            }

            UiTextHelpers.FitConstrainedSingleLine(
                label,
                ConstrainedLabelMinFontScale,
                ConstrainedLabelAbsoluteMinFontSize);
        }
    }

    private static float GetWidthInParentSpace(
        RectTransform rectTransform,
        Transform targetParent)
    {
        if (!targetParent)
        {
            return rectTransform.rect.width;
        }

        var corners = new Vector3[4];

        rectTransform.GetWorldCorners(corners);

        var minimumX = float.PositiveInfinity;
        var maximumX = float.NegativeInfinity;

        foreach (var corner in corners)
        {
            var localX = targetParent.InverseTransformPoint(corner).x;

            minimumX = Mathf.Min(minimumX, localX);
            maximumX = Mathf.Max(maximumX, localX);
        }

        return maximumX - minimumX;
    }

    private sealed class NavigationMenuLayoutWatcher : MonoBehaviour
    {
        private const int MissingLabelRetryFrames = 30;

        private TMP_Text _label;
        private int _lastLayoutSignature = int.MinValue;
        private Button _menuButton;
        private int _nextLabelSearchFrame;

        internal void Bind(
            Button menuButton,
            bool applyImmediately)
        {
            BindMenuButton(menuButton);

            if (applyImmediately)
            {
                RefreshLayout();
            }
        }

        private void LateUpdate()
        {
            RefreshLayout();
        }

        private void OnDisable()
        {
            _lastLayoutSignature = int.MinValue;
        }

        private void BindMenuButton(Button menuButton)
        {
            if (_menuButton == menuButton)
            {
                return;
            }

            _menuButton = menuButton;
            _label = null;
            _lastLayoutSignature = int.MinValue;
            _nextLabelSearchFrame = 0;
        }

        private void RefreshLayout()
        {
            if ((!_label || !_label.gameObject.activeInHierarchy) &&
                Time.frameCount >= _nextLabelSearchFrame)
            {
                _label = FindGameMenuLabel(_menuButton);
                _nextLabelSearchFrame = Time.frameCount + MissingLabelRetryFrames;
            }

            var signature = ComputeLayoutSignature(_menuButton, _label);

            if (_lastLayoutSignature == signature)
            {
                return;
            }

            ApplyGameMenuLayout(_menuButton, _label);
            _lastLayoutSignature = ComputeLayoutSignature(_menuButton, _label);
        }

        private static int ComputeLayoutSignature(Button menuButton, TMP_Text label)
        {
            unchecked
            {
                var signature = 17;

                if (!menuButton)
                {
                    return signature;
                }

                signature = signature * 31 + menuButton.GetInstanceID();
                signature = signature * 31 + (menuButton.gameObject.activeInHierarchy ? 1 : 0);

                if (menuButton.transform is RectTransform buttonRect)
                {
                    AddRectTransform(ref signature, buttonRect);
                }

                if (!label)
                {
                    return signature;
                }

                signature = signature * 31 + label.GetInstanceID();
                signature = signature * 31 + (label.gameObject.activeInHierarchy ? 1 : 0);
                signature = signature * 31 + (label.text?.GetHashCode() ?? 0);
                signature = signature * 31 + (label.font ? label.font.GetInstanceID() : 0);
                signature = signature * 31 + (label.enableAutoSizing ? 1 : 0);
                signature = signature * 31 + (label.enableWordWrapping ? 1 : 0);
                signature = signature * 31 + label.maxVisibleLines;
                signature = signature * 31 + (int)label.overflowMode;
                signature = signature * 31 + Mathf.RoundToInt(label.fontSizeMin * 10f);
                signature = signature * 31 + Mathf.RoundToInt(label.fontSizeMax * 10f);
                signature = signature * 31 + Mathf.RoundToInt(label.margin.x * 10f);
                signature = signature * 31 + Mathf.RoundToInt(label.margin.y * 10f);
                signature = signature * 31 + Mathf.RoundToInt(label.margin.z * 10f);
                signature = signature * 31 + Mathf.RoundToInt(label.margin.w * 10f);

                if (label.GetComponent<ContentSizeFitter>() is { } fitter)
                {
                    signature = signature * 31 + (fitter.enabled ? 1 : 0);
                    signature = signature * 31 + (int)fitter.horizontalFit;
                    signature = signature * 31 + (int)fitter.verticalFit;
                }

                if (label.GetComponent<LayoutElement>() is { } layoutElement)
                {
                    signature = signature * 31 + (layoutElement.enabled ? 1 : 0);
                    signature = signature * 31 + (layoutElement.ignoreLayout ? 1 : 0);
                }

                AddRectTransform(ref signature, label.rectTransform);

                return signature;
            }
        }

        private static void AddRectTransform(ref int signature, RectTransform rectTransform)
        {
            var rect = rectTransform.rect;

            signature = signature * 31 + Mathf.RoundToInt(rect.width * 10f);
            signature = signature * 31 + Mathf.RoundToInt(rect.height * 10f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.anchorMin.x * 100f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.anchorMin.y * 100f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.anchorMax.x * 100f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.anchorMax.y * 100f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.offsetMin.x * 10f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.offsetMin.y * 10f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.offsetMax.x * 10f);
            signature = signature * 31 + Mathf.RoundToInt(rectTransform.offsetMax.y * 10f);
        }
    }
}
