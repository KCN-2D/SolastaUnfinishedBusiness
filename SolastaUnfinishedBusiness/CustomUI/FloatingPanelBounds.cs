using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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
    private static readonly List<TooltipPanelBoundsEntry> ActiveTooltipPanelBounds = new();
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

    internal static void RestoreAttachmentList(RectTransform panel)
    {
        if (panel && panel.GetComponent<FloatingPanelAttachmentController>() is { } controller)
        {
            controller.Restore();
        }
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

    private static void FitListToAvailableArea(
        RectTransform table,
        RectTransform panel,
        Vector2 availableSize)
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
        var padding = table.GetComponent<LayoutGroup>()?.padding ?? new RectOffset();
        var overhead = panel ? Mathf.Max(0f, panel.rect.height - table.rect.height) : 0f;
        var listHeight = Mathf.Max(itemHeight, availableSize.y - overhead - padding.vertical);
        var rowHeight = itemHeight + spacing;
        var maxRows = Mathf.Max(1, Mathf.FloorToInt((listHeight + spacing) / rowHeight));

        if (activeCount <= maxRows)
        {
            RestoreListLayout(table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            return;
        }

        var horizontalOverhead = Mathf.Max(0f, panel.rect.width - table.rect.width);
        var maximumColumns = Mathf.Max(1, Mathf.FloorToInt(
            (availableSize.x - horizontalOverhead - padding.horizontal + DefaultColumnSpacing) /
            (itemWidth + DefaultColumnSpacing)));
        var columns = Mathf.Min(maximumColumns, Mathf.CeilToInt(activeCount / (float)maxRows));
        var rows = Mathf.CeilToInt(activeCount / (float)columns);
        var scrolls = rows > maxRows;

        // Reserve a visible scrollbar before deciding how many full-width buttons fit.
        if (scrolls)
        {
            maximumColumns = Mathf.Max(1, Mathf.FloorToInt(
                (availableSize.x - horizontalOverhead - padding.horizontal - FloatingPanelLayoutState.ScrollbarWidth +
                 DefaultColumnSpacing) / (itemWidth + DefaultColumnSpacing)));
            columns = Mathf.Min(columns, maximumColumns);
            rows = Mathf.CeilToInt(activeCount / (float)columns);
        }

        ApplyColumnLayout(table, activeCount, rows, itemWidth, itemHeight, spacing);
        if (scrolls)
        {
            table.GetComponent<FloatingPanelLayoutState>().ConfigureScroll(
                table, padding.vertical + maxRows * itemHeight + Mathf.Max(0, maxRows - 1) * spacing);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
    }

    private static void ApplyColumnLayout(
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

        var rows = Mathf.Min(activeCount, maxRows);
        var columns = Mathf.CeilToInt(activeCount / (float)rows);
        var padding = state.VerticalLayoutGroup ? state.VerticalLayoutGroup.padding : new RectOffset();
        var activeIndex = 0;

        for (var i = 0; i < table.childCount; i++)
        {
            if (table.GetChild(i) is not RectTransform child || !child.gameObject.activeSelf)
            {
                continue;
            }

            var column = activeIndex / rows;
            var row = activeIndex % rows;

            child.anchorMin = new Vector2(0f, 1f);
            child.anchorMax = new Vector2(0f, 1f);
            child.pivot = new Vector2(0f, 1f);
            child.anchoredPosition = new Vector2(
                padding.left + column * (itemWidth + DefaultColumnSpacing),
                -padding.top - row * (itemHeight + spacing));
            child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, itemWidth);
            child.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, itemHeight);

            activeIndex++;
        }

        var width = padding.horizontal + columns * itemWidth + Mathf.Max(0, columns - 1) * DefaultColumnSpacing;
        var height = padding.vertical + rows * itemHeight + Mathf.Max(0, rows - 1) * spacing;

        // The parent uses MinSize, not just the table's RectTransform dimensions.
        state.Width = width;
        state.Height = height;
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

    private static void RegisterTooltipBounds(
        TooltipPanelBoundsController controller,
        RectTransform canvasRect,
        Rect bounds)
    {
        if (!controller || !canvasRect)
        {
            return;
        }

        for (var i = ActiveTooltipPanelBounds.Count - 1; i >= 0; i--)
        {
            var entry = ActiveTooltipPanelBounds[i];

            if (!entry.Controller || !entry.CanvasRect)
            {
                ActiveTooltipPanelBounds.RemoveAt(i);
                continue;
            }

            if (entry.Controller == controller)
            {
                ActiveTooltipPanelBounds[i] = new TooltipPanelBoundsEntry(controller, canvasRect, bounds);
                return;
            }
        }

        ActiveTooltipPanelBounds.Add(new TooltipPanelBoundsEntry(controller, canvasRect, bounds));
    }

    private static void UnregisterTooltipBounds(TooltipPanelBoundsController controller)
    {
        for (var i = ActiveTooltipPanelBounds.Count - 1; i >= 0; i--)
        {
            var entry = ActiveTooltipPanelBounds[i];

            if (!entry.Controller || !entry.CanvasRect || entry.Controller == controller)
            {
                ActiveTooltipPanelBounds.RemoveAt(i);
            }
        }
    }

    private static float GetOtherTooltipOverlapArea(
        TooltipPanelBoundsController controller,
        RectTransform canvasRect,
        Rect bounds)
    {
        var overlapArea = 0f;

        for (var i = ActiveTooltipPanelBounds.Count - 1; i >= 0; i--)
        {
            var entry = ActiveTooltipPanelBounds[i];

            if (!entry.Controller || !entry.CanvasRect)
            {
                ActiveTooltipPanelBounds.RemoveAt(i);
                continue;
            }

            if (entry.Controller == controller || entry.CanvasRect != canvasRect)
            {
                continue;
            }

            overlapArea += GetOverlapArea(bounds, entry.Bounds);
        }

        return overlapArea;
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
        private bool _configured;
        private bool _layoutDirty;
        private float _margin;
        private RectTransform _panel;
        private RectTransform _table;
        private float _verticalOffset;
        private Vector2 _availableSize;
        private GameObject _selected;

        internal void Configure(
            RectTransform panel,
            RectTransform attachment,
            RectTransform table,
            Vector3 fallbackLocalPosition,
            float verticalOffset,
            float margin)
        {
            Restore();
            _panel = panel;
            _attachment = attachment;
            _table = table;
            _fallbackLocalPosition = fallbackLocalPosition;
            _verticalOffset = verticalOffset;
            _margin = margin;
            _configured = true;
            _layoutDirty = true;
            Apply();
        }

        internal void Restore()
        {
            _configured = false;
            _selected = null;
            RestoreListLayout(_table);
        }

        private void LateUpdate()
        {
            // Show modifiers and canvas scaling can run after Bind/OnEnable. Follow placement
            // until hidden, but rebuild the list only when its available area actually changes.
            Apply();
            RevealSelectedItem();
        }

        private void Apply()
        {
            if (!_configured || !CanUseScreen(_panel) ||
                !TryGetRootCanvasRect(_panel, out var canvas))
            {
                return;
            }

            var bounds = GetInsetCanvasRect(canvas, _margin);
            var attachmentBounds = default(Rect);
            var hasAttachment = _attachment && _attachment.gameObject.activeInHierarchy &&
                                TryGetCanvasLocalBounds(_attachment, canvas, out attachmentBounds);
            var topSpace = hasAttachment
                ? Mathf.Max(1f, bounds.yMax - attachmentBounds.yMax - _verticalOffset)
                : bounds.height;
            var bottomSpace = hasAttachment
                ? Mathf.Max(1f, attachmentBounds.yMin - bounds.yMin - _verticalOffset)
                : 0f;
            var side = bottomSpace >= topSpace ? AttachmentSide.Below : AttachmentSide.Above;
            var canvasSize = new Vector3(bounds.width, Mathf.Max(topSpace, bottomSpace), 0f);
            var localSize = _panel.InverseTransformVector(canvas.TransformVector(canvasSize));
            var available = new Vector2(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y));

            if (_layoutDirty || (_availableSize - available).sqrMagnitude > 0.01f)
            {
                RestoreListLayout(_table);
                FitListToAvailableArea(_table, _panel, available);
                _availableSize = available;
                _layoutDirty = false;
                _selected = null;
            }

            if (hasAttachment)
            {
                AlignToAttachment(_panel, attachmentBounds, side, _verticalOffset, canvas);
            }
            else if (_panel.localPosition != _fallbackLocalPosition)
            {
                _panel.localPosition = _fallbackLocalPosition;
            }

            ClampToScreen(_panel, false, _margin);
        }

        private void RevealSelectedItem()
        {
            var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == _selected)
            {
                return;
            }

            _selected = selected;
            var scroll = _table ? _table.GetComponentInParent<ScrollRect>() : null;
            if (!selected || !scroll || !selected.transform.IsChildOf(_table) ||
                selected.transform is not RectTransform selectedRect)
            {
                return;
            }

            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, selectedRect);
            var viewport = scroll.viewport.rect;
            var position = _table.anchoredPosition;
            if (bounds.max.y > viewport.yMax)
            {
                position.y -= bounds.max.y - viewport.yMax;
            }
            else if (bounds.min.y < viewport.yMin)
            {
                position.y += viewport.yMin - bounds.min.y;
            }

            position.y = Mathf.Clamp(position.y, 0f, Mathf.Max(0f, _table.rect.height - viewport.height));
            scroll.StopMovement();
            _table.anchoredPosition = position;
        }
    }

    private sealed class FloatingPanelLayoutState : MonoBehaviour, ILayoutElement
    {
        internal const float ScrollbarWidth = 16f;
        internal float Width = -1f;
        internal float Height = -1f;
        private RectTransform _scrollRoot;
        private Transform _tableParent;
        private int _tableSibling;
        private ChildLayoutState _tableState;

        public float minWidth => Width;
        public float preferredWidth => Width;
        public float flexibleWidth => -1f;
        public float minHeight => Height;
        public float preferredHeight => Height;
        public float flexibleHeight => -1f;
        public int layoutPriority => 10;
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() { }

        internal void ConfigureScroll(RectTransform table, float viewportHeight)
        {
            _tableParent = table.parent;
            _tableSibling = table.GetSiblingIndex();
            _tableState = new ChildLayoutState(table);
            _scrollRoot = CreateRect("SelectionScroll", _tableParent, table.gameObject.layer);
            _scrollRoot.SetSiblingIndex(_tableSibling);
            var layout = _scrollRoot.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = Width + ScrollbarWidth;
            layout.minHeight = layout.preferredHeight = viewportHeight;
            var viewport = CreateRect("Viewport", _scrollRoot, table.gameObject.layer);
            viewport.anchorMax = Vector2.one;
            viewport.offsetMax = new Vector2(-ScrollbarWidth, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();
            viewport.gameObject.AddComponent<Image>().color = Color.clear;
            var scroll = _scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = table;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.inertia = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 36f;
            table.SetParent(viewport, false);
            table.anchorMin = table.anchorMax = new Vector2(0f, 1f);
            table.pivot = new Vector2(0f, 1f);
            table.anchoredPosition = Vector2.zero;
            table.sizeDelta = new Vector2(Width, Height);

            var barRect = CreateRect("Scrollbar", _scrollRoot, table.gameObject.layer);
            barRect.anchorMin = new Vector2(1f, 0f);
            barRect.anchorMax = Vector2.one;
            barRect.offsetMin = new Vector2(-ScrollbarWidth + 4f, 0f);
            barRect.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.15f, 0.16f, 0.85f);
            var handle = CreateRect("Handle", barRect, table.gameObject.layer);
            handle.anchorMax = Vector2.one;
            var image = handle.gameObject.AddComponent<Image>();
            image.color = new Color(0.65f, 0.75f, 0.78f);
            var scrollbar = barRect.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = image;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalNormalizedPosition = 1f;
        }

        private static RectTransform CreateRect(string name, Transform parent, int layer)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.gameObject.layer = layer;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        internal ContentSizeFitter ContentSizeFitter;
        internal readonly List<ChildLayoutState> ChildStates = [];
        internal bool HasOriginalLayout;
        internal Vector2 OriginalSizeDelta;
        internal bool WasContentSizeFitterEnabled;
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
            WasVerticalLayoutEnabled = VerticalLayoutGroup && VerticalLayoutGroup.enabled;
            WasHorizontalLayoutEnabled = HorizontalLayoutGroup && HorizontalLayoutGroup.enabled;
            WasContentSizeFitterEnabled = ContentSizeFitter && ContentSizeFitter.enabled;
            OriginalSizeDelta = table.sizeDelta;

            for (var i = 0; i < table.childCount; i++)
            {
                if (table.GetChild(i) is RectTransform child && child.gameObject.activeSelf)
                {
                    ChildStates.Add(new ChildLayoutState(child));
                }
            }

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
            if (_scrollRoot)
            {
                _scrollRoot.GetComponent<ScrollRect>().content = null;
                table.SetParent(_tableParent, false);
                table.SetSiblingIndex(_tableSibling);
                _tableState.Restore();
                Object.DestroyImmediate(_scrollRoot.gameObject);
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

            foreach (var childState in ChildStates)
            {
                childState.Restore();
            }

            Object.DestroyImmediate(this);
        }
    }

    private readonly struct ChildLayoutState
    {
        private readonly Vector2 _anchorMax;
        private readonly Vector2 _anchorMin;
        private readonly Vector2 _anchoredPosition;
        private readonly Vector2 _pivot;
        private readonly RectTransform _rectTransform;
        private readonly Vector2 _sizeDelta;

        internal ChildLayoutState(RectTransform rectTransform)
        {
            _rectTransform = rectTransform;
            _anchorMin = rectTransform.anchorMin;
            _anchorMax = rectTransform.anchorMax;
            _pivot = rectTransform.pivot;
            _anchoredPosition = rectTransform.anchoredPosition;
            _sizeDelta = rectTransform.sizeDelta;
        }

        internal void Restore()
        {
            if (!_rectTransform)
            {
                return;
            }

            _rectTransform.anchorMin = _anchorMin;
            _rectTransform.anchorMax = _anchorMax;
            _rectTransform.pivot = _pivot;
            _rectTransform.anchoredPosition = _anchoredPosition;
            _rectTransform.sizeDelta = _sizeDelta;
        }
    }

    private readonly struct TooltipPanelBoundsEntry
    {
        internal readonly Rect Bounds;
        internal readonly RectTransform CanvasRect;
        internal readonly TooltipPanelBoundsController Controller;

        internal TooltipPanelBoundsEntry(
            TooltipPanelBoundsController controller,
            RectTransform canvasRect,
            Rect bounds)
        {
            Controller = controller;
            CanvasRect = canvasRect;
            Bounds = bounds;
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
        private bool _hasLayoutSignature;
        private bool _hasOriginalState;
        private bool _hasLockedBounds;
        private bool _hasTooltipAnchor;
        private Vector2 _lastCanvasSize;
        private Vector2 _lastContentRectSize;
        private Vector2 _lastContentSizeDelta;
        private bool _lastPanelActive;
        private Vector2 _lastPanelRectSize;
        private Vector2 _lastPanelSizeDelta;
        private Vector2 _lockedCanvasSize;
        private float _lockedContentHeight;
        private Rect _lockedPanelBounds;
        private Vector2 _lockedPanelSize;
        private bool _layoutDirty;
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
            UnregisterTooltipBounds(this);

            _tooltipPanel = tooltipPanel;
            _panel = tooltipPanel.RectTransform;
            _content = tooltipPanel.featuresTable;
            _margin = margin;
            _hasLockedBounds = false;
            _hasTooltipAnchor = TryGetRootCanvasRect(_panel, out var canvasRect) &&
                                TryGetMouseCanvasPosition(canvasRect, out _tooltipAnchorCanvasPosition);
            _scrollOffset = 0f;
            _scrollRange = 0f;
            _layoutDirty = true;
            _hasLayoutSignature = false;

            CaptureOriginalState();
            Apply();
            enabled = true;
        }

        private void LateUpdate()
        {
            if (!_layoutDirty && !HasLayoutSignatureChanged())
            {
                ApplyLockedBoundsWithoutRebuild();

                if (HandleScrollInput())
                {
                    ApplyScrollOffset();
                }

                return;
            }

            Apply();
        }

        private void OnDisable()
        {
            ReleaseWheelCapture();
            UnregisterTooltipBounds(this);
        }

        private void OnDestroy()
        {
            ReleaseWheelCapture();
            UnregisterTooltipBounds(this);
        }

        internal void RestoreAndDestroy()
        {
            RestoreScrollState();
            UnregisterTooltipBounds(this);
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
                UnregisterTooltipBounds(this);
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);

            var contentHeight = Mathf.Max(GetPreferredHeight(_content), _content.rect.height, _content.sizeDelta.y);

            if (!TryGetCanvasLocalBounds(_panel, out var panelBounds, out var canvasRect))
            {
                UnregisterTooltipBounds(this);
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
                RememberLayoutSignature(canvasRect);
                return;
            }

            ApplyScrollState(maxHeight, naturalHeight);

            if (!TryGetCanvasLocalBounds(_panel, canvasRect, out panelBounds))
            {
                UnregisterTooltipBounds(this);
                return;
            }

            LockOrApplyPanelBounds(panelBounds, canvasRect, naturalHeight);

            HandleScrollInput();
            ApplyScrollOffset();

            RememberLayoutSignature(canvasRect);
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
            RegisterTooltipBounds(this, canvasRect, _lockedPanelBounds);
        }

        private void ApplyLockedBoundsWithoutRebuild()
        {
            if (!_hasLockedBounds ||
                !_panel ||
                !TryGetCanvasLocalBounds(_panel, out var panelBounds, out var canvasRect))
            {
                UnregisterTooltipBounds(this);
                return;
            }

            ApplyCanvasLocalDelta(_panel, canvasRect, _lockedPanelBounds.min - panelBounds.min);
            RegisterTooltipBounds(this, canvasRect, _lockedPanelBounds);
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
            var bestScore = float.NegativeInfinity;
            var bestBounds = ClampRectToRect(panelBounds, canvasBounds);

            EvaluateTooltipCandidate(
                new Rect(mousePosition.x + TooltipCursorPadding, mousePosition.y - height * 0.5f, width, height),
                canvasBounds,
                cursorBounds,
                canvasRect,
                ref bestScore,
                ref bestBounds);
            EvaluateTooltipCandidate(
                new Rect(mousePosition.x - TooltipCursorPadding - width, mousePosition.y - height * 0.5f, width, height),
                canvasBounds,
                cursorBounds,
                canvasRect,
                ref bestScore,
                ref bestBounds);
            EvaluateTooltipCandidate(
                new Rect(mousePosition.x - width * 0.5f, mousePosition.y - TooltipCursorPadding - height, width, height),
                canvasBounds,
                cursorBounds,
                canvasRect,
                ref bestScore,
                ref bestBounds);
            EvaluateTooltipCandidate(
                new Rect(mousePosition.x - width * 0.5f, mousePosition.y + TooltipCursorPadding, width, height),
                canvasBounds,
                cursorBounds,
                canvasRect,
                ref bestScore,
                ref bestBounds);

            return bestBounds;
        }

        private void EvaluateTooltipCandidate(
            Rect candidate,
            Rect canvasBounds,
            Rect cursorBounds,
            RectTransform canvasRect,
            ref float bestScore,
            ref Rect bestBounds)
        {
            var clamped = ClampRectToRect(candidate, canvasBounds);
            var visibleArea = GetOverlapArea(candidate, canvasBounds);
            var cursorOverlap = GetOverlapArea(clamped, cursorBounds);
            var otherTooltipOverlap = GetOtherTooltipOverlapArea(this, canvasRect, clamped);
            var movePenalty = (clamped.center - candidate.center).sqrMagnitude * 0.001f;
            var score = visibleArea - cursorOverlap * 8f - otherTooltipOverlap * 12f - movePenalty;

            if (score <= bestScore)
            {
                return;
            }

            bestScore = score;
            bestBounds = clamped;
        }

        private bool HasLayoutSignatureChanged()
        {
            if (!_panel || !_content || !TryGetRootCanvasRect(_panel, out var canvasRect))
            {
                return true;
            }

            var panelActive = _panel.gameObject.activeInHierarchy;

            return !_hasLayoutSignature ||
                   _lastPanelActive != panelActive ||
                   (_lastCanvasSize - canvasRect.rect.size).sqrMagnitude > 1f ||
                   (_lastPanelRectSize - _panel.rect.size).sqrMagnitude > 1f ||
                   (_lastPanelSizeDelta - _panel.sizeDelta).sqrMagnitude > 1f ||
                   (_lastContentRectSize - _content.rect.size).sqrMagnitude > 1f ||
                   (_lastContentSizeDelta - _content.sizeDelta).sqrMagnitude > 1f;
        }

        private void RememberLayoutSignature(RectTransform canvasRect)
        {
            if (!_panel || !_content || !canvasRect)
            {
                return;
            }

            _lastPanelActive = _panel.gameObject.activeInHierarchy;
            _lastCanvasSize = canvasRect.rect.size;
            _lastPanelRectSize = _panel.rect.size;
            _lastPanelSizeDelta = _panel.sizeDelta;
            _lastContentRectSize = _content.rect.size;
            _lastContentSizeDelta = _content.sizeDelta;
            _hasLayoutSignature = true;
            _layoutDirty = false;
        }

        private bool HandleScrollInput()
        {
            if (!_content || _scrollRange <= 0f)
            {
                return false;
            }

            var wheel = Input.mouseScrollDelta.y;

            if (Mathf.Abs(wheel) <= 0.01f)
            {
                return false;
            }

            _scrollOffset = Mathf.Clamp(
                _scrollOffset - wheel * TooltipScrollPixelsPerWheel,
                0f,
                _scrollRange);

            return true;
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
