using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Patches;

public static class MetamagicSelectionPanelPatcher
{
    private const float CanvasMargin = 12f;
    private const float DefaultItemSpacing = 8f;

    [HarmonyPatch(typeof(MetamagicSelectionPanel), nameof(MetamagicSelectionPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshActions_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var rulesetCharacterGetter = AccessTools.PropertyGetter(
                typeof(GameLocationCharacter),
                nameof(GameLocationCharacter.RulesetCharacter));

            // PATCH: support replacement options and Simulacrum identity snapshots.
            return ReplaceMetamagicOption.PatchMetamagicGetterFromCharacter(
                instructions,
                "MetamagicSelectionPanel.Bind",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Callvirt, rulesetCharacterGetter));
        }

        [UsedImplicitly]
        public static void Postfix(
            MetamagicSelectionPanel __instance,
            RectTransform ___metamagicOptionsTable)
        {
            __instance.GetComponent<MetamagicSelectionLayoutState>()?.Restore();

            if (!___metamagicOptionsTable)
            {
                return;
            }

            var state = __instance.gameObject.AddComponent<MetamagicSelectionLayoutState>();

            state.Configure(__instance.RectTransform, ___metamagicOptionsTable);
        }
    }

    [HarmonyPatch(typeof(MetamagicSelectionPanel), nameof(MetamagicSelectionPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(MetamagicSelectionPanel __instance)
        {
            if (__instance.GetComponent<MetamagicSelectionLayoutState>() is not { } state)
            {
                return;
            }

            state.Apply();
            FloatingPanelBounds.ClampToScreen(__instance.RectTransform, true);
            FloatingPanelBounds.ClampToScreenForNextFrames(state, __instance.RectTransform, true);
        }
    }

    [HarmonyPatch(typeof(MetamagicSelectionPanel), nameof(MetamagicSelectionPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(MetamagicSelectionPanel __instance)
        {
            __instance.GetComponent<MetamagicSelectionLayoutState>()?.Restore();
        }
    }

    private sealed class MetamagicSelectionLayoutState : MonoBehaviour
    {
        private readonly List<ChildLayoutState> _childStates = [];
        private readonly List<LayoutGroupState> _layoutGroupStates = [];
        private RectTransform _panel;
        private RectTransform _table;
        private ContentSizeFitter _tableSizeFitter;
        private ContentSizeFitter _panelSizeFitter;
        private bool _tableSizeFitterEnabled;
        private bool _panelSizeFitterEnabled;
        private ContentSizeFitter.FitMode _tableHorizontalFit;
        private ContentSizeFitter.FitMode _tableVerticalFit;
        private ContentSizeFitter.FitMode _panelHorizontalFit;
        private ContentSizeFitter.FitMode _panelVerticalFit;
        private Vector2 _panelAnchorMin;
        private Vector2 _panelAnchorMax;
        private Vector2 _panelPivot;
        private Vector2 _panelAnchoredPosition;
        private Vector2 _panelSizeDelta;
        private Vector2 _panelRectSize;
        private Vector2 _tableAnchorMin;
        private Vector2 _tableAnchorMax;
        private Vector2 _tablePivot;
        private Vector2 _tableAnchoredPosition;
        private Vector2 _tableSizeDelta;
        private Vector2 _tableRectSize;
        private float _leftInset;
        private float _bottomInset;
        private RectOffset _layoutPadding;
        private Vector2 _itemSpacing;
        private Vector2 _cellSize;
        private int _activeItemCount;
        private bool _configured;

        internal void Configure(RectTransform panel, RectTransform table)
        {
            _panel = panel;
            _table = table;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);

            CaptureLayout();

            if (_activeItemCount == 0 || _cellSize.x <= 0f || _cellSize.y <= 0f)
            {
                return;
            }

            _configured = true;
            Apply();
        }

        internal void Apply()
        {
            if (!_configured || !_panel || !_table)
            {
                return;
            }

            var availableCanvasSize = GetAvailableCanvasSize();
            var horizontalChrome = _leftInset * 2f;
            var verticalChrome = Mathf.Max(0f, _panelRectSize.y - _tableRectSize.y);
            var maximumTableWidth = Mathf.Max(
                _cellSize.x + _layoutPadding.horizontal,
                availableCanvasSize.x - horizontalChrome);
            var maximumTableHeight = Mathf.Max(
                _cellSize.y + _layoutPadding.vertical,
                availableCanvasSize.y - verticalChrome);
            var maximumColumns = GetElementCapacity(
                maximumTableWidth,
                _layoutPadding.horizontal,
                _cellSize.x,
                _itemSpacing.x,
                _activeItemCount);
            var maximumRows = GetElementCapacity(
                maximumTableHeight,
                _layoutPadding.vertical,
                _cellSize.y,
                _itemSpacing.y,
                _activeItemCount);
            var minimumColumns = Mathf.CeilToInt(_activeItemCount / (float)maximumRows);
            var originalTableWidth = Mathf.Max(
                _cellSize.x + _layoutPadding.horizontal,
                Mathf.Min(_panelRectSize.x, availableCanvasSize.x) - horizontalChrome);
            var preferredColumns = GetElementCapacity(
                originalTableWidth,
                _layoutPadding.horizontal,
                _cellSize.x,
                _itemSpacing.x,
                _activeItemCount);
            var columns = minimumColumns <= maximumColumns
                ? Mathf.Clamp(preferredColumns, minimumColumns, maximumColumns)
                : maximumColumns;
            var rows = Mathf.CeilToInt(_activeItemCount / (float)columns);
            var tableWidth = _layoutPadding.horizontal + columns * _cellSize.x +
                             Mathf.Max(0, columns - 1) * _itemSpacing.x;
            var tableHeight = _layoutPadding.vertical + rows * _cellSize.y +
                              Mathf.Max(0, rows - 1) * _itemSpacing.y;
            var panelWidth = Mathf.Min(
                availableCanvasSize.x,
                Mathf.Max(
                    Mathf.Min(_panelRectSize.x, availableCanvasSize.x),
                    tableWidth + horizontalChrome));
            var panelHeight = _panelRectSize.y + Mathf.Max(0f, tableHeight - _tableRectSize.y);

            DisableOriginalLayout();

            ResizePanelKeepingBottom(panelWidth, panelHeight);
            PositionTableAboveOriginalBottom();
            _table.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, tableWidth);
            _table.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, tableHeight);
            PositionActiveChildren(columns);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
        }

        internal void Restore()
        {
            if (!_panel || !_table)
            {
                Object.DestroyImmediate(this);
                return;
            }

            RestoreSizeFitter(
                _tableSizeFitter,
                _tableSizeFitterEnabled,
                _tableHorizontalFit,
                _tableVerticalFit);
            RestoreSizeFitter(
                _panelSizeFitter,
                _panelSizeFitterEnabled,
                _panelHorizontalFit,
                _panelVerticalFit);

            RestoreRectTransform(
                _panel,
                _panelAnchorMin,
                _panelAnchorMax,
                _panelPivot,
                _panelAnchoredPosition,
                _panelSizeDelta);
            RestoreRectTransform(
                _table,
                _tableAnchorMin,
                _tableAnchorMax,
                _tablePivot,
                _tableAnchoredPosition,
                _tableSizeDelta);

            foreach (var childState in _childStates)
            {
                childState.Restore();
            }

            foreach (var layoutGroupState in _layoutGroupStates)
            {
                layoutGroupState.Restore();
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
            Object.DestroyImmediate(this);
        }

        private void CaptureLayout()
        {
            _tableSizeFitter = _table.GetComponent<ContentSizeFitter>();
            _panelSizeFitter = _panel.GetComponent<ContentSizeFitter>();

            _tableSizeFitterEnabled = _tableSizeFitter && _tableSizeFitter.enabled;
            _panelSizeFitterEnabled = _panelSizeFitter && _panelSizeFitter.enabled;

            CaptureSizeFitter(
                _tableSizeFitter,
                out _tableHorizontalFit,
                out _tableVerticalFit);
            CaptureSizeFitter(
                _panelSizeFitter,
                out _panelHorizontalFit,
                out _panelVerticalFit);

            _panelAnchorMin = _panel.anchorMin;
            _panelAnchorMax = _panel.anchorMax;
            _panelPivot = _panel.pivot;
            _panelAnchoredPosition = _panel.anchoredPosition;
            _panelSizeDelta = _panel.sizeDelta;
            _panelRectSize = _panel.rect.size;
            _tableAnchorMin = _table.anchorMin;
            _tableAnchorMax = _table.anchorMax;
            _tablePivot = _table.pivot;
            _tableAnchoredPosition = _table.anchoredPosition;
            _tableSizeDelta = _table.sizeDelta;
            _tableRectSize = _table.rect.size;

            CaptureLayoutGroups();
            CaptureChildren();
            CaptureItemSize();
            CaptureLayoutStyle();
            CaptureInsets();
        }

        private void CaptureLayoutGroups()
        {
            _layoutGroupStates.Clear();

            foreach (var layoutGroup in _table.GetComponents<LayoutGroup>())
            {
                _layoutGroupStates.Add(new LayoutGroupState(layoutGroup));
            }
        }

        private void CaptureChildren()
        {
            _childStates.Clear();
            _activeItemCount = 0;

            for (var index = 0; index < _table.childCount; index++)
            {
                if (_table.GetChild(index) is not RectTransform child)
                {
                    continue;
                }

                _childStates.Add(new ChildLayoutState(child));

                if (child.gameObject.activeSelf)
                {
                    _activeItemCount++;
                }
            }
        }

        private void CaptureItemSize()
        {
            _cellSize = Vector2.zero;

            foreach (var childState in _childStates)
            {
                if (!childState.IsActive)
                {
                    continue;
                }

                var size = childState.RectSize;

                _cellSize.x = Mathf.Max(
                    _cellSize.x,
                    size.x,
                    LayoutUtility.GetPreferredWidth(childState.RectTransform));
                _cellSize.y = Mathf.Max(
                    _cellSize.y,
                    size.y,
                    LayoutUtility.GetPreferredHeight(childState.RectTransform));
            }
        }

        private void CaptureLayoutStyle()
        {
            if (_table.GetComponent<GridLayoutGroup>() is { } gridLayout)
            {
                _layoutPadding = CopyPadding(gridLayout.padding);
                _itemSpacing = gridLayout.spacing;
                _cellSize.x = Mathf.Max(_cellSize.x, gridLayout.cellSize.x);
                _cellSize.y = Mathf.Max(_cellSize.y, gridLayout.cellSize.y);
                return;
            }

            if (_table.GetComponent<HorizontalLayoutGroup>() is { } horizontalLayout)
            {
                _layoutPadding = CopyPadding(horizontalLayout.padding);
                _itemSpacing = new Vector2(horizontalLayout.spacing, DefaultItemSpacing);
                return;
            }

            if (_table.GetComponent<VerticalLayoutGroup>() is { } verticalLayout)
            {
                _layoutPadding = CopyPadding(verticalLayout.padding);
                _itemSpacing = new Vector2(DefaultItemSpacing, verticalLayout.spacing);
                return;
            }

            _layoutPadding = new RectOffset();
            _itemSpacing = new Vector2(DefaultItemSpacing, DefaultItemSpacing);
        }

        private void CaptureInsets()
        {
            var corners = new Vector3[4];

            _table.GetWorldCorners(corners);

            var minimum = (Vector2)_panel.InverseTransformPoint(corners[0]);

            for (var index = 1; index < corners.Length; index++)
            {
                minimum = Vector2.Min(minimum, (Vector2)_panel.InverseTransformPoint(corners[index]));
            }

            _leftInset = Mathf.Max(0f, minimum.x - _panel.rect.xMin);
            _bottomInset = Mathf.Max(0f, minimum.y - _panel.rect.yMin);
        }

        private void DisableOriginalLayout()
        {
            foreach (var layoutGroupState in _layoutGroupStates)
            {
                layoutGroupState.Disable();
            }

            if (_tableSizeFitter)
            {
                _tableSizeFitter.enabled = false;
            }

            if (_panelSizeFitter)
            {
                _panelSizeFitter.enabled = false;
            }
        }

        private void PositionActiveChildren(int columns)
        {
            var activeIndex = 0;

            foreach (var childState in _childStates)
            {
                if (!childState.IsActive || !childState.RectTransform)
                {
                    continue;
                }

                var row = activeIndex / columns;
                var column = activeIndex % columns;
                var child = childState.RectTransform;

                child.anchorMin = new Vector2(0f, 1f);
                child.anchorMax = new Vector2(0f, 1f);
                child.pivot = new Vector2(0f, 1f);
                child.anchoredPosition = new Vector2(
                    _layoutPadding.left + column * (_cellSize.x + _itemSpacing.x),
                    -_layoutPadding.top - row * (_cellSize.y + _itemSpacing.y));
                child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _cellSize.x);
                child.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _cellSize.y);

                activeIndex++;
            }
        }

        private Vector2 GetAvailableCanvasSize()
        {
            var canvas = _panel.GetComponentInParent<Canvas>();

            if (!canvas)
            {
                return _panelRectSize;
            }

            var rootCanvas = canvas.rootCanvas ? canvas.rootCanvas : canvas;

            if (rootCanvas.transform is not RectTransform canvasRect ||
                canvasRect.rect.width <= 0f ||
                canvasRect.rect.height <= 0f)
            {
                return _panelRectSize;
            }

            var corners = new Vector3[4];

            canvasRect.GetWorldCorners(corners);

            var left = _panel.InverseTransformPoint(corners[0]).x;
            var right = _panel.InverseTransformPoint(corners[3]).x;
            var bottom = _panel.InverseTransformPoint(corners[0]).y;
            var top = _panel.InverseTransformPoint(corners[1]).y;
            var width = Mathf.Abs(right - left);
            var height = Mathf.Abs(top - bottom);
            var horizontalMarginScale = width / canvasRect.rect.width;
            var verticalMarginScale = height / canvasRect.rect.height;

            return new Vector2(
                Mathf.Max(1f, width - 2f * CanvasMargin * horizontalMarginScale),
                Mathf.Max(1f, height - 2f * CanvasMargin * verticalMarginScale));
        }

        private static int GetElementCapacity(
            float availableSize,
            int padding,
            float elementSize,
            float spacing,
            int elementCount)
        {
            var stride = Mathf.Max(1f, elementSize + spacing);
            var capacity = Mathf.FloorToInt((availableSize - padding + spacing) / stride);

            return Mathf.Clamp(capacity, 1, elementCount);
        }

        private void ResizePanelKeepingBottom(float width, float height)
        {
            var corners = new Vector3[4];

            _panel.GetWorldCorners(corners);

            var originalBottomCenter = (corners[0] + corners[3]) * 0.5f;

            _panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            _panel.GetWorldCorners(corners);

            var resizedBottomCenter = (corners[0] + corners[3]) * 0.5f;

            _panel.position += originalBottomCenter - resizedBottomCenter;
        }

        private void PositionTableAboveOriginalBottom()
        {
            _table.anchorMin = new Vector2(0.5f, 0f);
            _table.anchorMax = new Vector2(0.5f, 0f);
            _table.pivot = new Vector2(0.5f, 0f);
            _table.anchoredPosition = new Vector2(0f, _bottomInset);
        }

        private static void CaptureSizeFitter(
            ContentSizeFitter fitter,
            out ContentSizeFitter.FitMode horizontalFit,
            out ContentSizeFitter.FitMode verticalFit)
        {
            horizontalFit = fitter
                ? fitter.horizontalFit
                : ContentSizeFitter.FitMode.Unconstrained;
            verticalFit = fitter
                ? fitter.verticalFit
                : ContentSizeFitter.FitMode.Unconstrained;
        }

        private static void RestoreSizeFitter(
            ContentSizeFitter fitter,
            bool enabled,
            ContentSizeFitter.FitMode horizontalFit,
            ContentSizeFitter.FitMode verticalFit)
        {
            if (!fitter)
            {
                return;
            }

            fitter.horizontalFit = horizontalFit;
            fitter.verticalFit = verticalFit;
            fitter.enabled = enabled;
        }
    }

    private static void RestoreRectTransform(
        RectTransform rectTransform,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPosition,
        Vector2 sizeDelta)
    {
        if (!rectTransform)
        {
            return;
        }

        rectTransform.anchorMin = anchorMin;
        rectTransform.anchorMax = anchorMax;
        rectTransform.pivot = pivot;
        rectTransform.sizeDelta = sizeDelta;
        rectTransform.anchoredPosition = anchoredPosition;
    }

    private static RectOffset CopyPadding(RectOffset padding)
    {
        return padding is null
            ? new RectOffset()
            : new RectOffset(padding.left, padding.right, padding.top, padding.bottom);
    }

    private readonly struct LayoutGroupState
    {
        private readonly LayoutGroup _layoutGroup;
        private readonly bool _enabled;

        internal LayoutGroupState(LayoutGroup layoutGroup)
        {
            _layoutGroup = layoutGroup;
            _enabled = layoutGroup && layoutGroup.enabled;
        }

        internal void Disable()
        {
            if (_layoutGroup)
            {
                _layoutGroup.enabled = false;
            }
        }

        internal void Restore()
        {
            if (_layoutGroup)
            {
                _layoutGroup.enabled = _enabled;
            }
        }
    }

    private readonly struct ChildLayoutState
    {
        private readonly Vector2 _anchorMin;
        private readonly Vector2 _anchorMax;
        private readonly Vector2 _pivot;
        private readonly Vector2 _anchoredPosition;
        private readonly Vector2 _sizeDelta;
        private readonly RectTransform _rectTransform;

        internal ChildLayoutState(RectTransform rectTransform)
        {
            _rectTransform = rectTransform;
            _anchorMin = rectTransform.anchorMin;
            _anchorMax = rectTransform.anchorMax;
            _pivot = rectTransform.pivot;
            _anchoredPosition = rectTransform.anchoredPosition;
            _sizeDelta = rectTransform.sizeDelta;
            RectSize = rectTransform.rect.size;
            IsActive = rectTransform.gameObject.activeSelf;
        }

        internal bool IsActive { get; }

        internal RectTransform RectTransform => _rectTransform;

        internal Vector2 RectSize { get; }

        internal void Restore()
        {
            RestoreRectTransform(
                _rectTransform,
                _anchorMin,
                _anchorMax,
                _pivot,
                _anchoredPosition,
                _sizeDelta);
        }
    }
}
