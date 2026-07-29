using System;
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
