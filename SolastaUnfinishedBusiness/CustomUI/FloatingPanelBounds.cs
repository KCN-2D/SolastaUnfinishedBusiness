using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class FloatingPanelBounds
{
    private const float DefaultColumnSpacing = 8f;
    private const float DefaultMargin = 12f;
    private const int DefaultReapplyFrames = 4;
    private const float TooltipCursorPadding = 32f;
    private const float TooltipScrollPixelsPerWheel = 72f;

    private static TooltipPanelBoundsController ActiveTooltipWheelCapture;
    private static readonly Vector3[] WorldCorners = new Vector3[4];

    private enum AttachmentSide
    {
        Above,
        Below
    }

    internal static void ClampToScreen(RectTransform rectTransform, bool rebuild = false, float margin = DefaultMargin)
    {
        if (!CanUseScreen(rectTransform))
        {
            return;
        }

        if (rebuild)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }

        if (!TryGetCanvasLocalBounds(rectTransform, out var bounds, out var canvasRect))
        {
            return;
        }

        ApplyCanvasLocalDelta(rectTransform, canvasRect, GetCanvasLocalDelta(bounds, canvasRect, margin));
    }

    internal static void ClampToScreenForNextFrames(
        MonoBehaviour owner,
        RectTransform rectTransform,
        bool rebuild = false,
        float margin = DefaultMargin,
        int frames = DefaultReapplyFrames)
    {
        if (!owner || !rectTransform)
        {
            return;
        }

        if (owner.isActiveAndEnabled)
        {
            owner.StartCoroutine(ClampToScreenForNextFramesCoroutine(rectTransform, rebuild, margin, frames));
        }
    }

    internal static void ConfigureNearAttachmentList(
        MonoBehaviour owner,
        RectTransform panel,
        RectTransform attachment,
        RectTransform table,
        Vector3 fallbackLocalPosition,
        float verticalOffset = 4f,
        float margin = DefaultMargin)
    {
        if (!panel)
        {
            return;
        }

        var controller = panel.GetComponent<FloatingPanelAttachmentController>() ??
                         panel.gameObject.AddComponent<FloatingPanelAttachmentController>();

        controller.Configure(panel, attachment, table, fallbackLocalPosition, verticalOffset, margin);

        if (owner && owner.isActiveAndEnabled)
        {
            owner.StartCoroutine(ApplyNearAttachmentListForNextFramesCoroutine(controller));
        }
    }

    internal static void ConfigureTooltipBounds(TooltipPanel tooltipPanel, float margin = DefaultMargin)
    {
        if (!tooltipPanel)
        {
            return;
        }

        var controller = tooltipPanel.GetComponent<TooltipPanelBoundsController>() ??
                         tooltipPanel.gameObject.AddComponent<TooltipPanelBoundsController>();

        controller.Configure(tooltipPanel, margin);
    }

    internal static void RestoreTooltipBounds(TooltipPanel tooltipPanel)
    {
        if (!tooltipPanel)
        {
            return;
        }

        var controller = tooltipPanel.GetComponent<TooltipPanelBoundsController>();

        if (!controller)
        {
            return;
        }

        controller.RestoreAndDestroy();
    }

    internal static bool ShouldSuppressBackgroundWheel(Component source)
    {
        var controller = ActiveTooltipWheelCapture;

        return controller && controller.CanCaptureWheel(source);
    }

    private static IEnumerator ApplyNearAttachmentListForNextFramesCoroutine(
        FloatingPanelAttachmentController controller)
    {
        controller.Apply();

        for (var i = 0; i < DefaultReapplyFrames; i++)
        {
            yield return null;
            controller.Apply();
        }
    }

    private static IEnumerator ClampToScreenForNextFramesCoroutine(
        RectTransform rectTransform,
        bool rebuild,
        float margin,
        int frames)
    {
        ClampToScreen(rectTransform, rebuild, margin);

        for (var i = 0; i < frames; i++)
        {
            yield return null;
            ClampToScreen(rectTransform, rebuild, margin);
        }
    }

    private static void PlaceNearAttachmentAndClamp(
        RectTransform panel,
        RectTransform attachment,
        RectTransform table,
        Vector3 fallbackLocalPosition,
        float verticalOffset,
        float margin)
    {
        if (!CanUseScreen(panel))
        {
            return;
        }

        RestoreListLayout(table);

        if (table)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(panel);

        if (!TryGetCanvasLocalBounds(panel, out _, out var canvasRect))
        {
            return;
        }

        var canvasBounds = GetInsetCanvasRect(canvasRect, margin);

        if (!attachment || !attachment.gameObject.activeInHierarchy ||
            !TryGetCanvasLocalBounds(attachment, canvasRect, out var attachmentBounds))
        {
            panel.localPosition = fallbackLocalPosition;
            FitListToAvailableHeight(table, panel, canvasBounds.height);
            ClampToScreen(panel, true, margin);
            return;
        }

        var topSpace = Mathf.Max(1f, canvasBounds.yMax - attachmentBounds.yMax - verticalOffset);
        var bottomSpace = Mathf.Max(1f, attachmentBounds.yMin - canvasBounds.yMin - verticalOffset);
        var selectedSide = bottomSpace >= topSpace ? AttachmentSide.Below : AttachmentSide.Above;
        var availableHeight = selectedSide == AttachmentSide.Below ? bottomSpace : topSpace;

        FitListToAvailableHeight(table, panel, availableHeight);
        AlignToAttachment(panel, attachmentBounds, selectedSide, verticalOffset, canvasRect);
        ClampToScreen(panel, false, margin);
    }

    private static void AlignToAttachment(
        RectTransform panel,
        Rect attachmentBounds,
        AttachmentSide side,
        float verticalOffset,
        RectTransform canvasRect)
    {
        if (!TryGetCanvasLocalBounds(panel, canvasRect, out var panelBounds))
        {
            return;
        }

        var desiredCenterX = attachmentBounds.center.x;
        var deltaX = desiredCenterX - panelBounds.center.x;
        var deltaY = side == AttachmentSide.Below
            ? attachmentBounds.yMin - verticalOffset - panelBounds.yMax
            : attachmentBounds.yMax + verticalOffset - panelBounds.yMin;

        ApplyCanvasLocalDelta(panel, canvasRect, new Vector2(deltaX, deltaY));
    }

    private static void FitListToAvailableHeight(
        RectTransform table,
        RectTransform panel,
        float availableHeight)
    {
        if (!table)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            return;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel);

        var activeCount = CountActiveChildren(table, out var firstActiveChild);

        if (activeCount <= 1 || !firstActiveChild)
        {
            RestoreListLayout(table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            return;
        }

        var itemHeight = GetPreferredHeight(firstActiveChild);
        var itemWidth = GetPreferredWidth(firstActiveChild);

        if (itemHeight <= 0f || itemWidth <= 0f)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            return;
        }

        var spacing = GetVerticalSpacing(table);
        var overhead = panel ? Mathf.Max(0f, panel.rect.height - table.rect.height) : 0f;
        var listHeight = Mathf.Max(itemHeight, availableHeight - overhead);
        var rowHeight = itemHeight + spacing;
        var maxRows = Mathf.Max(1, Mathf.FloorToInt((listHeight + spacing) / rowHeight));

        if (activeCount <= maxRows)
        {
            RestoreListLayout(table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            return;
        }

        ApplyGridLayout(table, activeCount, maxRows, itemWidth, itemHeight, spacing);
        LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
    }

    private static void ApplyGridLayout(
        RectTransform table,
        int activeCount,
        int maxRows,
        float itemWidth,
        float itemHeight,
        float spacing)
    {
        var state = table.GetComponent<FloatingPanelLayoutState>() ??
                    table.gameObject.AddComponent<FloatingPanelLayoutState>();

        state.Capture(table);
        state.DisableOriginalLayouts();

        var gridLayout = state.GridLayoutGroup;

        if (!gridLayout)
        {
            gridLayout = table.GetComponent<GridLayoutGroup>();

            if (!gridLayout)
            {
                gridLayout = table.gameObject.AddComponent<GridLayoutGroup>();
                state.AddedGridLayout = true;
            }

            state.GridLayoutGroup = gridLayout;
        }

        var rows = Mathf.Min(activeCount, maxRows);
        var columns = Mathf.CeilToInt(activeCount / (float)rows);
        var padding = state.VerticalLayoutGroup ? state.VerticalLayoutGroup.padding : new RectOffset();

        gridLayout.enabled = true;
        gridLayout.childAlignment = state.VerticalLayoutGroup
            ? state.VerticalLayoutGroup.childAlignment
            : TextAnchor.UpperLeft;
        gridLayout.constraint = GridLayoutGroup.Constraint.FixedRowCount;
        gridLayout.constraintCount = rows;
        gridLayout.startAxis = GridLayoutGroup.Axis.Vertical;
        gridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
        gridLayout.padding = padding;
        gridLayout.spacing = new Vector2(DefaultColumnSpacing, spacing);
        gridLayout.cellSize = new Vector2(itemWidth, itemHeight);

        var width = padding.horizontal + columns * itemWidth + Mathf.Max(0, columns - 1) * gridLayout.spacing.x;
        var height = padding.vertical + rows * itemHeight + Mathf.Max(0, rows - 1) * gridLayout.spacing.y;

        table.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        table.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
    }

    private static void RestoreListLayout(RectTransform table)
    {
        var state = table ? table.GetComponent<FloatingPanelLayoutState>() : null;

        if (!state)
        {
            return;
        }

        state.Restore(table);
    }

    private static bool CanUseScreen(RectTransform rectTransform)
    {
        return rectTransform &&
               rectTransform.gameObject.activeInHierarchy &&
               Screen.width > 0 &&
               Screen.height > 0;
    }

    private static bool TryGetRootCanvasRect(Component component, out RectTransform canvasRect)
    {
        canvasRect = null;

        if (!component)
        {
            return false;
        }

        var canvas = component.GetComponentInParent<Canvas>();

        if (!canvas)
        {
            return false;
        }

        canvasRect = (canvas.rootCanvas ? canvas.rootCanvas : canvas).transform as RectTransform;

        return canvasRect && canvasRect.gameObject.activeInHierarchy;
    }

    private static Camera GetCanvasCamera(RectTransform canvasRect)
    {
        var canvas = canvasRect ? canvasRect.GetComponent<Canvas>() : null;

        return canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
    }

    private static bool TryGetMouseCanvasPosition(RectTransform canvasRect, out Vector2 position)
    {
        position = default;

        return canvasRect &&
               RectTransformUtility.ScreenPointToLocalPointInRectangle(
                   canvasRect,
                   Input.mousePosition,
                   GetCanvasCamera(canvasRect),
                   out position);
    }

    private static bool TryGetCanvasLocalBounds(
        RectTransform rectTransform,
        out Rect bounds,
        out RectTransform canvasRect)
    {
        bounds = default;
        canvasRect = null;

        if (!CanUseScreen(rectTransform) || !TryGetRootCanvasRect(rectTransform, out canvasRect))
        {
            return false;
        }

        return TryGetCanvasLocalBounds(rectTransform, canvasRect, out bounds);
    }

    private static bool TryGetCanvasLocalBounds(
        RectTransform rectTransform,
        RectTransform canvasRect,
        out Rect bounds)
    {
        bounds = default;

        if (!CanUseScreen(rectTransform) || !canvasRect)
        {
            return false;
        }

        rectTransform.GetWorldCorners(WorldCorners);
        var min = (Vector2)canvasRect.InverseTransformPoint(WorldCorners[0]);
        var max = min;

        for (var i = 1; i < WorldCorners.Length; i++)
        {
            var point = (Vector2)canvasRect.InverseTransformPoint(WorldCorners[i]);

            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

        return true;
    }

    private static Rect GetInsetCanvasRect(RectTransform canvasRect, float margin)
    {
        var rect = canvasRect.rect;
        var horizontalMargin = Mathf.Min(margin, rect.width * 0.5f);
        var verticalMargin = Mathf.Min(margin, rect.height * 0.5f);

        return Rect.MinMaxRect(
            rect.xMin + horizontalMargin,
            rect.yMin + verticalMargin,
            rect.xMax - horizontalMargin,
            rect.yMax - verticalMargin);
    }

    private static Rect ClampRectToRect(Rect rect, Rect bounds)
    {
        var xMin = rect.xMin;
        var yMin = rect.yMin;

        if (rect.width > bounds.width)
        {
            xMin = bounds.xMin;
        }
        else if (rect.xMin < bounds.xMin)
        {
            xMin += bounds.xMin - rect.xMin;
        }
        else if (rect.xMax > bounds.xMax)
        {
            xMin += bounds.xMax - rect.xMax;
        }

        if (rect.height > bounds.height)
        {
            yMin = bounds.yMin;
        }
        else if (rect.yMin < bounds.yMin)
        {
            yMin += bounds.yMin - rect.yMin;
        }
        else if (rect.yMax > bounds.yMax)
        {
            yMin += bounds.yMax - rect.yMax;
        }

        return new Rect(xMin, yMin, rect.width, rect.height);
    }

    private static float GetOverlapArea(Rect a, Rect b)
    {
        var width = Mathf.Max(0f, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin));
        var height = Mathf.Max(0f, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));

        return width * height;
    }

    private static Vector2 GetCanvasLocalDelta(Rect bounds, RectTransform canvasRect, float margin)
    {
        var delta = Vector2.zero;
        var canvasBounds = GetInsetCanvasRect(canvasRect, margin);

        if (bounds.xMin < canvasBounds.xMin)
        {
            delta.x = canvasBounds.xMin - bounds.xMin;
        }
        else if (bounds.xMax > canvasBounds.xMax)
        {
            delta.x = canvasBounds.xMax - bounds.xMax;
        }

        if (bounds.yMin < canvasBounds.yMin)
        {
            delta.y = canvasBounds.yMin - bounds.yMin;
        }
        else if (bounds.yMax > canvasBounds.yMax)
        {
            delta.y = canvasBounds.yMax - bounds.yMax;
        }

        return delta;
    }

    private static void ApplyCanvasLocalDelta(RectTransform rectTransform, RectTransform canvasRect, Vector2 delta)
    {
        if (delta == Vector2.zero || !canvasRect)
        {
            return;
        }

        rectTransform.position += canvasRect.TransformVector(new Vector3(delta.x, delta.y, 0f));
    }

    private static int CountActiveChildren(RectTransform table, out RectTransform firstActiveChild)
    {
        var count = 0;

        firstActiveChild = null;

        for (var i = 0; i < table.childCount; i++)
        {
            if (table.GetChild(i) is not RectTransform child || !child.gameObject.activeSelf)
            {
                continue;
            }

            firstActiveChild ??= child;
            count++;
        }

        return count;
    }

    private static float GetPreferredHeight(RectTransform rectTransform)
    {
        return Mathf.Max(rectTransform.rect.height, rectTransform.sizeDelta.y, LayoutUtility.GetPreferredHeight(rectTransform));
    }

    private static float GetPreferredWidth(RectTransform rectTransform)
    {
        return Mathf.Max(rectTransform.rect.width, rectTransform.sizeDelta.x, LayoutUtility.GetPreferredWidth(rectTransform));
    }

    private static float GetVerticalSpacing(RectTransform table)
    {
        var verticalLayout = table.GetComponent<VerticalLayoutGroup>();

        if (verticalLayout)
        {
            return verticalLayout.spacing;
        }

        var gridLayout = table.GetComponent<GridLayoutGroup>();

        return gridLayout ? gridLayout.spacing.y : 0f;
    }

    private sealed class FloatingPanelAttachmentController : MonoBehaviour
    {
        private RectTransform _attachment;
        private Vector3 _fallbackLocalPosition;
        private float _margin;
        private RectTransform _panel;
        private RectTransform _table;
        private float _verticalOffset;

        internal void Configure(
            RectTransform panel,
            RectTransform attachment,
            RectTransform table,
            Vector3 fallbackLocalPosition,
            float verticalOffset,
            float margin)
        {
            _panel = panel;
            _attachment = attachment;
            _table = table;
            _fallbackLocalPosition = fallbackLocalPosition;
            _verticalOffset = verticalOffset;
            _margin = margin;

            Apply();
        }

        internal void Apply()
        {
            PlaceNearAttachmentAndClamp(
                _panel,
                _attachment,
                _table,
                _fallbackLocalPosition,
                _verticalOffset,
                _margin);
        }

        private void OnEnable()
        {
            StartCoroutine(ApplyNearAttachmentListForNextFramesCoroutine(this));
        }
    }

    private sealed class FloatingPanelLayoutState : MonoBehaviour
    {
        internal bool AddedGridLayout;
        internal ContentSizeFitter ContentSizeFitter;
        internal GridLayoutGroup GridLayoutGroup;
        internal bool HasOriginalLayout;
        internal Vector2 OriginalSizeDelta;
        internal bool WasContentSizeFitterEnabled;
        internal bool WasGridLayoutEnabled;
        internal bool WasHorizontalLayoutEnabled;
        internal bool WasVerticalLayoutEnabled;
        internal HorizontalLayoutGroup HorizontalLayoutGroup;
        internal VerticalLayoutGroup VerticalLayoutGroup;

        internal void Capture(RectTransform table)
        {
            if (HasOriginalLayout)
            {
                return;
            }

            VerticalLayoutGroup = table.GetComponent<VerticalLayoutGroup>();
            HorizontalLayoutGroup = table.GetComponent<HorizontalLayoutGroup>();
            ContentSizeFitter = table.GetComponent<ContentSizeFitter>();
            GridLayoutGroup = table.GetComponent<GridLayoutGroup>();
            WasVerticalLayoutEnabled = VerticalLayoutGroup && VerticalLayoutGroup.enabled;
            WasHorizontalLayoutEnabled = HorizontalLayoutGroup && HorizontalLayoutGroup.enabled;
            WasContentSizeFitterEnabled = ContentSizeFitter && ContentSizeFitter.enabled;
            WasGridLayoutEnabled = GridLayoutGroup && GridLayoutGroup.enabled;
            OriginalSizeDelta = table.sizeDelta;
            HasOriginalLayout = true;
        }

        internal void DisableOriginalLayouts()
        {
            if (VerticalLayoutGroup)
            {
                VerticalLayoutGroup.enabled = false;
            }

            if (HorizontalLayoutGroup)
            {
                HorizontalLayoutGroup.enabled = false;
            }

            if (ContentSizeFitter)
            {
                ContentSizeFitter.enabled = false;
            }
        }

        internal void Restore(RectTransform table)
        {
            if (AddedGridLayout && GridLayoutGroup)
            {
                Object.DestroyImmediate(GridLayoutGroup);
            }
            else if (GridLayoutGroup)
            {
                GridLayoutGroup.enabled = WasGridLayoutEnabled;
            }

            if (VerticalLayoutGroup)
            {
                VerticalLayoutGroup.enabled = WasVerticalLayoutEnabled;
            }

            if (HorizontalLayoutGroup)
            {
                HorizontalLayoutGroup.enabled = WasHorizontalLayoutEnabled;
            }

            if (ContentSizeFitter)
            {
                ContentSizeFitter.enabled = WasContentSizeFitterEnabled;
            }

            table.sizeDelta = OriginalSizeDelta;
            Object.DestroyImmediate(this);
        }
    }

    private sealed class TooltipPanelBoundsController : MonoBehaviour
    {
        private RectTransform _backgroundBlur;
        private Vector2 _backgroundBlurSizeDelta;
        private RectTransform _content;
        private Vector2 _contentAnchorMax;
        private Vector2 _contentAnchorMin;
        private Vector2 _contentAnchoredPosition;
        private Vector3 _contentLocalScale;
        private Transform _contentParent;
        private Vector2 _contentPivot;
        private Vector2 _contentSizeDelta;
        private RectTransform _frame;
        private Vector2 _frameSizeDelta;
        private bool _hasOriginalState;
        private bool _hasLockedBounds;
        private bool _hasTooltipAnchor;
        private Vector2 _lockedCanvasSize;
        private float _lockedContentHeight;
        private Rect _lockedPanelBounds;
        private Vector2 _lockedPanelSize;
        private float _margin = DefaultMargin;
        private RectMask2D _mask;
        private RectTransform _panel;
        private ContentSizeFitter _panelContentSizeFitter;
        private Vector2 _panelSizeDelta;
        private int _siblingIndex;
        private float _scrollOffset;
        private float _scrollRange;
        private TooltipPanel _tooltipPanel;
        private Vector2 _tooltipAnchorCanvasPosition;
        private bool _addedMask;
        private bool _wasMaskEnabled;
        private bool _wasPanelContentSizeFitterEnabled;

        internal void Configure(TooltipPanel tooltipPanel, float margin)
        {
            RestoreScrollState();

            _tooltipPanel = tooltipPanel;
            _panel = tooltipPanel.RectTransform;
            _content = tooltipPanel.featuresTable;
            _margin = margin;
            _hasLockedBounds = false;
            _hasTooltipAnchor = TryGetRootCanvasRect(_panel, out var canvasRect) &&
                                TryGetMouseCanvasPosition(canvasRect, out _tooltipAnchorCanvasPosition);
            _scrollOffset = 0f;
            _scrollRange = 0f;

            CaptureOriginalState();
            Apply();
            enabled = true;
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void OnDisable()
        {
            ReleaseWheelCapture();
        }

        private void OnDestroy()
        {
            ReleaseWheelCapture();
        }

        internal void RestoreAndDestroy()
        {
            RestoreScrollState();
            Object.DestroyImmediate(this);
        }

        private void CaptureOriginalState()
        {
            if (!_panel || !_content)
            {
                return;
            }

            _contentParent = _content.parent;
            _siblingIndex = _content.GetSiblingIndex();
            _contentAnchorMin = _content.anchorMin;
            _contentAnchorMax = _content.anchorMax;
            _contentPivot = _content.pivot;
            _contentAnchoredPosition = _content.anchoredPosition;
            _contentSizeDelta = _content.sizeDelta;
            _contentLocalScale = _content.localScale;

            _panelSizeDelta = _panel.sizeDelta;
            _panelContentSizeFitter = _panel.GetComponent<ContentSizeFitter>();
            _wasPanelContentSizeFitterEnabled = _panelContentSizeFitter && _panelContentSizeFitter.enabled;
            _mask = _panel.GetComponent<RectMask2D>();
            _wasMaskEnabled = _mask && _mask.enabled;
            _addedMask = false;

            _backgroundBlur = _tooltipPanel.transform.Find("BackgroundBlur")?.GetComponent<RectTransform>();
            _frame = _tooltipPanel.transform.Find("Frame")?.GetComponent<RectTransform>();

            if (_backgroundBlur)
            {
                _backgroundBlurSizeDelta = _backgroundBlur.sizeDelta;
            }

            if (_frame)
            {
                _frameSizeDelta = _frame.sizeDelta;
            }

            _hasOriginalState = true;
        }

        private void Apply()
        {
            if (!_tooltipPanel || !_panel || !_content || !CanUseScreen(_panel))
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);

            var contentHeight = Mathf.Max(GetPreferredHeight(_content), _content.rect.height, _content.sizeDelta.y);

            if (!TryGetCanvasLocalBounds(_panel, out var panelBounds, out var canvasRect))
            {
                return;
            }

            var maxHeight = Mathf.Max(1f, GetInsetCanvasRect(canvasRect, _margin).height);
            var naturalHeight = Mathf.Max(
                panelBounds.height,
                contentHeight,
                GetPreferredHeight(_panel),
                _panel.rect.height,
                _panel.sizeDelta.y);
            var isLong = naturalHeight > maxHeight;

            if (!isLong)
            {
                ReleaseWheelCapture();

                if (_mask && (_addedMask || _mask.enabled != _wasMaskEnabled))
                {
                    RestoreScrollState();
                    _scrollOffset = 0f;
                    _scrollRange = 0f;
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
                    TryGetCanvasLocalBounds(_panel, canvasRect, out panelBounds);
                }

                LockOrApplyPanelBounds(panelBounds, canvasRect, naturalHeight);
                return;
            }

            ApplyScrollState(maxHeight, naturalHeight);

            if (!TryGetCanvasLocalBounds(_panel, canvasRect, out panelBounds))
            {
                return;
            }

            LockOrApplyPanelBounds(panelBounds, canvasRect, naturalHeight);
            HandleScrollInput();
            ApplyScrollOffset();
        }

        private void ApplyScrollState(float maxHeight, float naturalHeight)
        {
            if (_panelContentSizeFitter)
            {
                _panelContentSizeFitter.enabled = false;
            }

            EnsureRootMask();

            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.localScale = Vector3.one;
            _content.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _panel.rect.width);
            _content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, naturalHeight);

            SetHeight(_panel, maxHeight);
            SetHeight(_backgroundBlur, maxHeight);
            SetHeight(_frame, maxHeight);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);

            _scrollRange = Mathf.Max(0f, naturalHeight - maxHeight);
            _scrollOffset = Mathf.Clamp(_scrollOffset, 0f, _scrollRange);

            if (_scrollRange > 0f)
            {
                ActiveTooltipWheelCapture = this;
            }
            else
            {
                ReleaseWheelCapture();
            }
        }

        private void LockOrApplyPanelBounds(Rect panelBounds, RectTransform canvasRect, float contentHeight)
        {
            var panelSize = panelBounds.size;
            var canvasSize = canvasRect.rect.size;
            var shouldRelock = !_hasLockedBounds ||
                               (_lockedPanelSize - panelSize).sqrMagnitude > 1f ||
                               (_lockedCanvasSize - canvasSize).sqrMagnitude > 1f ||
                               Mathf.Abs(_lockedContentHeight - contentHeight) > 1f;

            if (shouldRelock)
            {
                _lockedPanelBounds = GetPreferredTooltipBounds(panelBounds, canvasRect);
                _lockedPanelSize = panelSize;
                _lockedCanvasSize = canvasSize;
                _lockedContentHeight = contentHeight;
                _hasLockedBounds = true;
            }

            ApplyCanvasLocalDelta(_panel, canvasRect, _lockedPanelBounds.min - panelBounds.min);
        }

        private Rect GetPreferredTooltipBounds(Rect panelBounds, RectTransform canvasRect)
        {
            var canvasBounds = GetInsetCanvasRect(canvasRect, _margin);

            if (!TryGetMouseCanvasPosition(canvasRect, out var mousePosition))
            {
                if (!_hasTooltipAnchor)
                {
                    return ClampRectToRect(panelBounds, canvasBounds);
                }

                mousePosition = _tooltipAnchorCanvasPosition;
            }
            else if (_hasTooltipAnchor)
            {
                mousePosition = _tooltipAnchorCanvasPosition;
            }

            var width = panelBounds.width;
            var height = panelBounds.height;
            var cursorBounds = Rect.MinMaxRect(
                mousePosition.x - TooltipCursorPadding,
                mousePosition.y - TooltipCursorPadding,
                mousePosition.x + TooltipCursorPadding,
                mousePosition.y + TooltipCursorPadding);
            var candidates = new[]
            {
                new Rect(mousePosition.x + TooltipCursorPadding, mousePosition.y - height * 0.5f, width, height),
                new Rect(mousePosition.x - TooltipCursorPadding - width, mousePosition.y - height * 0.5f, width, height),
                new Rect(mousePosition.x - width * 0.5f, mousePosition.y - TooltipCursorPadding - height, width, height),
                new Rect(mousePosition.x - width * 0.5f, mousePosition.y + TooltipCursorPadding, width, height)
            };
            var bestScore = float.NegativeInfinity;
            var bestBounds = ClampRectToRect(panelBounds, canvasBounds);

            foreach (var candidate in candidates)
            {
                var clamped = ClampRectToRect(candidate, canvasBounds);
                var visibleArea = GetOverlapArea(candidate, canvasBounds);
                var cursorOverlap = GetOverlapArea(clamped, cursorBounds);
                var movePenalty = (clamped.center - candidate.center).sqrMagnitude * 0.001f;
                var score = visibleArea - cursorOverlap * 8f - movePenalty;

                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestBounds = clamped;
            }

            return bestBounds;
        }

        private void HandleScrollInput()
        {
            if (!_content || _scrollRange <= 0f)
            {
                return;
            }

            var wheel = Input.mouseScrollDelta.y;

            if (Mathf.Abs(wheel) <= 0.01f)
            {
                return;
            }

            _scrollOffset = Mathf.Clamp(
                _scrollOffset - wheel * TooltipScrollPixelsPerWheel,
                0f,
                _scrollRange);
        }

        private void ApplyScrollOffset()
        {
            if (!_content || _scrollRange <= 0f)
            {
                return;
            }

            _content.anchoredPosition = new Vector2(_contentAnchoredPosition.x, _scrollOffset);
        }

        private void EnsureRootMask()
        {
            if (!_mask)
            {
                _mask = _panel.gameObject.AddComponent<RectMask2D>();
                _addedMask = true;
            }

            _mask.enabled = true;
        }

        private void RestoreScrollState()
        {
            ReleaseWheelCapture();

            if (!_hasOriginalState)
            {
                RestoreMask();
                return;
            }

            if (_content && _contentParent)
            {
                if (_content.parent != _contentParent)
                {
                    _content.SetParent(_contentParent, false);
                }

                _content.SetSiblingIndex(_siblingIndex);
                _content.anchorMin = _contentAnchorMin;
                _content.anchorMax = _contentAnchorMax;
                _content.pivot = _contentPivot;
                _content.anchoredPosition = _contentAnchoredPosition;
                _content.sizeDelta = _contentSizeDelta;
                _content.localScale = _contentLocalScale;
            }

            if (_panel)
            {
                _panel.sizeDelta = _panelSizeDelta;
            }

            if (_panelContentSizeFitter)
            {
                _panelContentSizeFitter.enabled = _wasPanelContentSizeFitterEnabled;
            }

            if (_backgroundBlur)
            {
                _backgroundBlur.sizeDelta = _backgroundBlurSizeDelta;
            }

            if (_frame)
            {
                _frame.sizeDelta = _frameSizeDelta;
            }

            RestoreMask();
        }

        private void RestoreMask()
        {
            if (!_mask)
            {
                return;
            }

            if (_addedMask)
            {
                Object.DestroyImmediate(_mask);
                _mask = null;
            }
            else
            {
                _mask.enabled = _wasMaskEnabled;
            }

            _addedMask = false;
        }

        internal bool CanCaptureWheel(Component source)
        {
            if (!enabled || !_panel || !_panel.gameObject.activeInHierarchy || _scrollRange <= 0f)
            {
                return false;
            }

            if (!source || !source.transform)
            {
                return true;
            }

            return source.transform != _panel && !source.transform.IsChildOf(_panel);
        }

        private void ReleaseWheelCapture()
        {
            if (ActiveTooltipWheelCapture == this)
            {
                ActiveTooltipWheelCapture = null;
            }
        }

        private static void SetHeight(RectTransform rectTransform, float height)
        {
            if (!rectTransform)
            {
                return;
            }

            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }
    }
}
