using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class FloatingPanelBounds
{
    private const float DefaultColumnSpacing = 8f;
    private const float DefaultMargin = 12f;
    private const int DefaultReapplyFrames = 4;

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

        var camera = GetCanvasCamera(rectTransform);

        if (!TryGetScreenBounds(rectTransform, camera, out var bounds))
        {
            return;
        }

        ApplyScreenDelta(rectTransform, camera, GetScreenDelta(bounds, margin));
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

    internal static void FitTooltipAndClamp(TooltipPanel tooltipPanel, float margin = DefaultMargin)
    {
        if (!tooltipPanel || !CanUseScreen(tooltipPanel.RectTransform))
        {
            return;
        }

        var rectTransform = tooltipPanel.RectTransform;
        var featuresTable = tooltipPanel.featuresTable;

        LayoutRebuilder.ForceRebuildLayoutImmediate(featuresTable);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);

        var camera = GetCanvasCamera(rectTransform);
        var maxHeight = Mathf.Max(1f, Screen.height - margin * 2f);

        if (!TryGetScreenBounds(rectTransform, camera, out var bounds))
        {
            return;
        }

        if (bounds.height <= maxHeight)
        {
            ClampToScreen(rectTransform, false, margin);
            return;
        }

        ApplyTooltipScroll(tooltipPanel, maxHeight);
        LayoutRebuilder.ForceRebuildLayoutImmediate(featuresTable);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        ClampToScreen(rectTransform, false, margin);
    }

    internal static void RestoreTooltipScroll(TooltipPanel tooltipPanel)
    {
        if (!tooltipPanel)
        {
            return;
        }

        var state = tooltipPanel.GetComponent<FloatingPanelTooltipScrollState>();

        if (!state)
        {
            return;
        }

        state.Restore();
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

        var camera = GetCanvasCamera(panel);

        RestoreListLayout(table);
        LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel);

        if (!attachment || !attachment.gameObject.activeInHierarchy ||
            !TryGetScreenBounds(attachment, camera, out var attachmentBounds))
        {
            panel.localPosition = fallbackLocalPosition;
            FitListToAvailableHeight(table, panel, Screen.height - margin * 2f);
            ClampToScreen(panel, true, margin);
            return;
        }

        var topSpace = Mathf.Max(1f, Screen.height - margin - attachmentBounds.yMax - verticalOffset);
        var bottomSpace = Mathf.Max(1f, attachmentBounds.yMin - margin - verticalOffset);
        var selectedSide = bottomSpace >= topSpace ? AttachmentSide.Below : AttachmentSide.Above;
        var availableHeight = selectedSide == AttachmentSide.Below ? bottomSpace : topSpace;

        FitListToAvailableHeight(table, panel, availableHeight);
        AlignToAttachment(panel, attachmentBounds, selectedSide, verticalOffset, camera);
        ClampToScreen(panel, false, margin);
    }

    private static void AlignToAttachment(
        RectTransform panel,
        Rect attachmentBounds,
        AttachmentSide side,
        float verticalOffset,
        Camera camera)
    {
        if (!TryGetScreenBounds(panel, camera, out var panelBounds))
        {
            return;
        }

        var desiredCenterX = attachmentBounds.center.x;
        var deltaX = desiredCenterX - panelBounds.center.x;
        var deltaY = side == AttachmentSide.Below
            ? attachmentBounds.yMin - verticalOffset - panelBounds.yMax
            : attachmentBounds.yMax + verticalOffset - panelBounds.yMin;

        ApplyScreenDelta(panel, camera, new Vector2(deltaX, deltaY));
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

    private static void ApplyTooltipScroll(TooltipPanel tooltipPanel, float maxHeight)
    {
        var panel = tooltipPanel.RectTransform;
        var content = tooltipPanel.featuresTable;

        if (!panel || !content)
        {
            return;
        }

        var state = tooltipPanel.GetComponent<FloatingPanelTooltipScrollState>() ??
                    tooltipPanel.gameObject.AddComponent<FloatingPanelTooltipScrollState>();

        state.Capture(tooltipPanel);
        state.DisablePanelSizeFitter();

        var scrollRect = state.ScrollRect;

        if (!scrollRect)
        {
            scrollRect = tooltipPanel.GetComponent<ScrollRect>();

            if (!scrollRect)
            {
                scrollRect = tooltipPanel.gameObject.AddComponent<ScrollRect>();
                state.AddedScrollRect = true;
            }

            state.ScrollRect = scrollRect;
        }

        var mask = state.RectMask;

        if (!mask)
        {
            mask = tooltipPanel.GetComponent<RectMask2D>();

            if (!mask)
            {
                mask = tooltipPanel.gameObject.AddComponent<RectMask2D>();
                state.AddedRectMask = true;
            }

            state.RectMask = mask;
        }

        panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, maxHeight);
        SetChildHeight(tooltipPanel.transform, "BackgroundBlur", maxHeight);
        SetChildHeight(tooltipPanel.transform, "Frame", maxHeight);

        scrollRect.content = content;
        scrollRect.viewport = panel;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 25f;
        scrollRect.inertia = true;
        scrollRect.normalizedPosition = Vector2.up;
    }

    private static void SetChildHeight(Transform parent, string path, float height)
    {
        var rectTransform = parent.Find(path)?.GetComponent<RectTransform>();

        if (!rectTransform)
        {
            return;
        }

        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
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

    private static Camera GetCanvasCamera(Component component)
    {
        var canvas = component.GetComponentInParent<Canvas>();

        return canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
    }

    private static bool TryGetScreenBounds(RectTransform rectTransform, Camera camera, out Rect bounds)
    {
        bounds = default;

        if (!CanUseScreen(rectTransform))
        {
            return false;
        }

        rectTransform.GetWorldCorners(WorldCorners);

        var min = RectTransformUtility.WorldToScreenPoint(camera, WorldCorners[0]);
        var max = min;

        for (var i = 1; i < WorldCorners.Length; i++)
        {
            var point = RectTransformUtility.WorldToScreenPoint(camera, WorldCorners[i]);

            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

        return true;
    }

    private static Vector2 GetScreenDelta(Rect bounds, float margin)
    {
        var delta = Vector2.zero;
        var maxX = Screen.width - margin;
        var maxY = Screen.height - margin;

        if (bounds.xMin < margin)
        {
            delta.x = margin - bounds.xMin;
        }
        else if (bounds.xMax > maxX)
        {
            delta.x = maxX - bounds.xMax;
        }

        if (bounds.yMin < margin)
        {
            delta.y = margin - bounds.yMin;
        }
        else if (bounds.yMax > maxY)
        {
            delta.y = maxY - bounds.yMax;
        }

        return delta;
    }

    private static void ApplyScreenDelta(RectTransform rectTransform, Camera camera, Vector2 delta)
    {
        if (delta == Vector2.zero || rectTransform.parent is not RectTransform parent)
        {
            return;
        }

        var currentScreenPosition = RectTransformUtility.WorldToScreenPoint(camera, rectTransform.position);
        var targetScreenPosition = currentScreenPosition + delta;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, currentScreenPosition, camera, out var currentLocalPosition) ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, targetScreenPosition, camera, out var targetLocalPosition))
        {
            return;
        }

        rectTransform.localPosition += (Vector3)(targetLocalPosition - currentLocalPosition);
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

    private sealed class FloatingPanelTooltipScrollState : MonoBehaviour
    {
        internal bool AddedRectMask;
        internal bool AddedScrollRect;
        internal Vector2 BackgroundBlurSizeDelta;
        internal Vector2 ContentAnchoredPosition;
        internal Vector2 ContentAnchorMax;
        internal Vector2 ContentAnchorMin;
        internal Vector2 ContentPivot;
        internal Vector2 ContentSizeDelta;
        internal Vector2 FrameSizeDelta;
        internal ContentSizeFitter PanelContentSizeFitter;
        internal RectMask2D RectMask;
        internal ScrollRect ScrollRect;
        internal bool WasPanelContentSizeFitterEnabled;
        private RectTransform _backgroundBlur;
        private RectTransform _content;
        private RectTransform _frame;
        private RectTransform _panel;
        private Vector2 _panelSizeDelta;

        internal void Capture(TooltipPanel tooltipPanel)
        {
            if (_panel)
            {
                return;
            }

            _panel = tooltipPanel.RectTransform;
            _content = tooltipPanel.featuresTable;
            _backgroundBlur = tooltipPanel.transform.Find("BackgroundBlur")?.GetComponent<RectTransform>();
            _frame = tooltipPanel.transform.Find("Frame")?.GetComponent<RectTransform>();
            PanelContentSizeFitter = _panel.GetComponent<ContentSizeFitter>();
            _panelSizeDelta = _panel.sizeDelta;
            WasPanelContentSizeFitterEnabled = PanelContentSizeFitter && PanelContentSizeFitter.enabled;

            if (_content)
            {
                ContentAnchorMin = _content.anchorMin;
                ContentAnchorMax = _content.anchorMax;
                ContentPivot = _content.pivot;
                ContentAnchoredPosition = _content.anchoredPosition;
                ContentSizeDelta = _content.sizeDelta;
            }

            if (_backgroundBlur)
            {
                BackgroundBlurSizeDelta = _backgroundBlur.sizeDelta;
            }

            if (_frame)
            {
                FrameSizeDelta = _frame.sizeDelta;
            }
        }

        internal void DisablePanelSizeFitter()
        {
            if (PanelContentSizeFitter)
            {
                PanelContentSizeFitter.enabled = false;
            }
        }

        internal void Restore()
        {
            if (AddedScrollRect && ScrollRect)
            {
                Object.DestroyImmediate(ScrollRect);
            }

            if (AddedRectMask && RectMask)
            {
                Object.DestroyImmediate(RectMask);
            }

            if (_panel)
            {
                _panel.sizeDelta = _panelSizeDelta;
            }

            if (PanelContentSizeFitter)
            {
                PanelContentSizeFitter.enabled = WasPanelContentSizeFitterEnabled;
            }

            if (_content)
            {
                _content.anchorMin = ContentAnchorMin;
                _content.anchorMax = ContentAnchorMax;
                _content.pivot = ContentPivot;
                _content.anchoredPosition = ContentAnchoredPosition;
                _content.sizeDelta = ContentSizeDelta;
            }

            if (_backgroundBlur)
            {
                _backgroundBlur.sizeDelta = BackgroundBlurSizeDelta;
            }

            if (_frame)
            {
                _frame.sizeDelta = FrameSizeDelta;
            }

            Object.DestroyImmediate(this);
        }
    }
}
