using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class SpellResourceSelectionPanel
{
    private static readonly ConditionalWeakTable<SlotAdvancementPanel, PanelState> Panels = new();

    internal static bool ShowOptions(
        SlotAdvancementPanel panel,
        RulesetCharacter caster,
        SpellDefinition spell,
        RulesetSpellRepertoire preferredRepertoire,
        int index,
        SlotAdvancementBox.OnActivateHandler callback,
        RectTransform attachment = null)
    {
        if (!panel || caster == null || spell == null || callback == null)
        {
            return false;
        }

        var options = SpellCastingResourceContext.EnumerateResources(caster, spell, preferredRepertoire);

        // Preserve the native slot picker when this spell has no alternative free resource.
        if (!options.Any(option => option.IsFree))
        {
            return false;
        }

        var available = options.Where(option => option.IsAvailable(caster)).ToList();

        if (available.Count <= 1)
        {
            if (available.Count == 1)
            {
                Select(panel, caster, available[0], index, callback);
            }

            return true;
        }

        if (panel.Visible)
        {
            panel.Hide(true);
        }

        Restore(panel);
        var originalParent = panel.transform.parent;
        var originalSibling = panel.transform.GetSiblingIndex();
        var originalRect = new RectState(panel.RectTransform);
        var reparent = attachment && attachment.GetComponent<GuiScreen>();

        // Inventory shortcuts can open this picker above the still-visible inspection screen.
        if (reparent)
        {
            panel.transform.SetParent(attachment, true);
            panel.transform.SetAsLastSibling();
        }

        var wasActive = panel.gameObject.activeSelf;
        panel.gameObject.SetActive(true);

        // Native Bind reverses the row order. Keep real spell levels in every native row;
        // its callback is replaced below with the corresponding resource, not a level key.
        panel.Bind(spell, index, options.Select(option => option.SlotLevel).Reverse().ToList(), callback);
        var state = new PanelState(panel, reparent ? originalParent : null, originalSibling, originalRect);
        Panels.Add(panel, state);

        for (var rowIndex = 0; rowIndex < options.Count; rowIndex++)
        {
            var option = options[rowIndex];
            var row = panel.advancementBoxesTable.GetChild(rowIndex).GetComponent<SlotAdvancementBox>();

            row.OnActivate = (spellIndex, _) => Select(panel, caster, option, spellIndex, callback);
            var rowState = row.button.gameObject.AddComponent<ResourceRowState>();
            rowState.Apply(row, option.FormatChoiceTitle(caster), option.FormatDescription(caster),
                option.IsAvailable(caster), state.Width);
        }

        state.Apply();
        panel.gameObject.SetActive(wasActive);
        panel.Show(false);
        state.ObserveHostLifetime();

        if (attachment)
        {
            panel.RectTransform.position = attachment.position;
        }

        FloatingPanelBounds.ClampToScreenForNextFrames(panel, panel.RectTransform);

        if (Gui.GamepadActive)
        {
            for (var rowIndex = 0; rowIndex < panel.advancementBoxesTable.childCount; rowIndex++)
            {
                var row = panel.advancementBoxesTable.GetChild(rowIndex).GetComponent<SlotAdvancementBox>();

                if (row.gameObject.activeSelf && row.Button.IsInteractable())
                {
                    Gui.InputService.SelectCurrentSelectable(row.Button);
                    break;
                }
            }
        }

        return true;
    }

    private static void Select(
        SlotAdvancementPanel panel,
        RulesetCharacter caster,
        SpellCastingResourceContext.ResourceOption option,
        int index,
        SlotAdvancementBox.OnActivateHandler callback)
    {
        // A pending choice may have become unavailable before the player activates it.
        if (!option.IsAvailable(caster))
        {
            return;
        }

        if (panel.Visible)
        {
            panel.Hide(true);
        }

        using (SpellCastingResourceContext.BeginSelection(option))
        {
            callback(index, option.SlotLevel);
        }
    }

    internal static void Restore(SlotAdvancementPanel panel)
    {
        if (!Panels.TryGetValue(panel, out var state))
        {
            return;
        }

        Panels.Remove(panel);
        state.Restore();
    }

    internal static void RestoreRow(SlotAdvancementBox row)
    {
        if (!row.button.TryGetComponent<ResourceRowState>(out var state))
        {
            return;
        }

        state.Restore();
        Object.DestroyImmediate(state);
    }

    private sealed class PanelState
    {
        private readonly SlotAdvancementPanel _panel;
        private readonly Transform _originalParent;
        private readonly int _originalSibling;
        private readonly RectState _originalRect;
        private readonly RectState _panelRect;
        private readonly RectState _movingRect;
        private readonly RectState _tableRect;
        private readonly Transform _tableParent;
        private readonly int _tableSibling;
        private readonly Vector3 _animationStart;
        private readonly Vector3 _animationEnd;
        private readonly float _maximumHeight;
        private readonly float _overhead;
        private RectTransform _viewport;
        private ScrollRect _scroll;
        private HostedPanelLifetime _hostLifetime;

        internal PanelState(
            SlotAdvancementPanel panel, Transform originalParent, int originalSibling, RectState originalRect)
        {
            _panel = panel;
            _originalParent = originalParent;
            _originalSibling = originalSibling;
            _originalRect = originalRect;
            _panelRect = new RectState(panel.RectTransform);
            _movingRect = new RectState(panel.movingGroup);
            _tableRect = new RectState(panel.advancementBoxesTable);
            _tableParent = panel.advancementBoxesTable.parent;
            _tableSibling = panel.advancementBoxesTable.GetSiblingIndex();
            _animationStart = panel.movingPanelModifier.StartPosition;
            _animationEnd = panel.movingPanelModifier.EndPosition;
            _overhead = Mathf.Max(0f, panel.movingGroup.rect.height - panel.advancementBoxesTable.rect.height);
            var canvas = panel.GetComponentInParent<Canvas>();
            var canvasRect = canvas ? canvas.rootCanvas.GetComponent<RectTransform>() : null;
            var canvasWidth = canvasRect ? canvasRect.rect.width : Screen.width;
            var canvasHeight = canvasRect ? canvasRect.rect.height : Screen.height;
            Width = Mathf.Max(1f, Mathf.Min(300f, canvasWidth - 48f));
            _maximumHeight = Mathf.Max(1f, Mathf.Min(420f, canvasHeight * 0.6f) - _overhead);
        }

        internal float Width { get; }

        internal void ObserveHostLifetime()
        {
            if (_originalParent)
            {
                _hostLifetime = _panel.gameObject.AddComponent<HostedPanelLifetime>();
                _hostLifetime.Panel = _panel;
            }
        }

        internal void Apply()
        {
            var table = _panel.advancementBoxesTable;
            table.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Width);
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            var contentHeight = Mathf.Max(table.rect.height, LayoutUtility.GetPreferredHeight(table));
            var viewportHeight = Mathf.Min(contentHeight, _maximumHeight);
            var viewportObject = new GameObject("SpellResourcesViewport",
                typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect))
            {
                layer = table.gameObject.layer
            };

            _viewport = viewportObject.GetComponent<RectTransform>();
            _viewport.SetParent(_tableParent, false);
            _viewport.SetSiblingIndex(_tableSibling);
            _tableRect.CopyTo(_viewport);
            _viewport.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Width);
            _viewport.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, viewportHeight);
            viewportObject.GetComponent<Image>().color = Color.clear;

            table.SetParent(_viewport, false);
            table.anchorMin = new Vector2(0f, 1f);
            table.anchorMax = Vector2.one;
            table.pivot = new Vector2(0.5f, 1f);
            table.anchoredPosition = Vector2.zero;
            table.sizeDelta = new Vector2(0f, contentHeight);

            _scroll = viewportObject.GetComponent<ScrollRect>();
            _scroll.viewport = _viewport;
            _scroll.content = table;
            _scroll.horizontal = false;
            _scroll.vertical = contentHeight > viewportHeight;
            _scroll.inertia = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 36f;

            var height = viewportHeight + _overhead;
            _panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Width);
            _panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            _panel.movingGroup.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Width);
            _panel.movingGroup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            _panel.movingPanelModifier.StartPosition = new Vector3(0f, -height, 0f);
            _panel.movingPanelModifier.EndPosition = Vector3.zero;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel.RectTransform);
        }

        internal void Restore()
        {
            if (_hostLifetime)
            {
                _hostLifetime.Panel = null;
                Object.DestroyImmediate(_hostLifetime);
            }

            if (_viewport)
            {
                _scroll.content = null;
                _panel.advancementBoxesTable.SetParent(_tableParent, false);
                _panel.advancementBoxesTable.SetSiblingIndex(_tableSibling);
                Object.DestroyImmediate(_viewport.gameObject);
            }

            _tableRect.Restore();
            _movingRect.Restore();
            _panelRect.Restore();
            _panel.movingPanelModifier.StartPosition = _animationStart;
            _panel.movingPanelModifier.EndPosition = _animationEnd;

            if (_originalParent)
            {
                _panel.transform.SetParent(_originalParent, false);
                _panel.transform.SetSiblingIndex(_originalSibling);
                _originalRect.Restore();
            }
        }
    }

    private sealed class HostedPanelLifetime : MonoBehaviour
    {
        internal SlotAdvancementPanel Panel { get; set; }

        private void OnDisable()
        {
            var panel = Panel;

            if (!panel)
            {
                return;
            }

            // Closing the inspection screen also cancels its temporary child picker.
            if (panel.Visible)
            {
                panel.Hide(true);
            }

            SpellResourceSelectionPanel.Restore(panel);
        }
    }

    private sealed class ResourceRowState : MonoBehaviour, ISelectHandler
    {
        private SlotAdvancementBox _row;
        private RectState _rowRect;
        private RectState _labelRect;
        private RectState _labelContainerRect;
        private ContentSizeFitter _fitter;
        private bool _fitterEnabled;
        private float _fontSize;
        private bool _autoSizeContainer;
        private TMP_Text _text;
        private bool _wrapping;
        private bool _autoSizing;
        private TextOverflowModes _overflow;
        private int _maximumLines;
        private bool _interactable;
        private string _label;
        private string _tooltip;
        private LayoutElement _layout;
        private bool _createdLayout;
        private float _minimumWidth;
        private float _preferredWidth;
        private float _minimumHeight;
        private float _preferredHeight;

        internal void Apply(SlotAdvancementBox row, string title, string description, bool available, float width)
        {
            _row = row;
            _rowRect = new RectState(row.RectTransform);
            _labelContainerRect = new RectState(row.levelLabel.RectTransform);
            _text = row.levelLabel.TMP_Text;
            _labelRect = new RectState(_text.rectTransform);
            _fontSize = _text.fontSize;
            _autoSizeContainer = _text.autoSizeTextContainer;
            _fitter = _text.GetComponent<ContentSizeFitter>();
            _fitterEnabled = _fitter && _fitter.enabled;
            if (_fitter)
            {
                _fitter.enabled = false;
            }
            _wrapping = _text.enableWordWrapping;
            _autoSizing = _text.enableAutoSizing;
            _overflow = _text.overflowMode;
            _maximumLines = _text.maxVisibleLines;
            _interactable = row.button.interactable;
            _label = row.levelLabel.Text;
            _tooltip = row.tooltip.Content;
            _layout = row.GetComponent<LayoutElement>();
            _createdLayout = !_layout;
            _layout = _layout ? _layout : row.gameObject.AddComponent<LayoutElement>();
            _minimumWidth = _layout.minWidth;
            _preferredWidth = _layout.preferredWidth;
            _minimumHeight = _layout.minHeight;
            _preferredHeight = _layout.preferredHeight;

            row.levelLabel.Text = title;
            row.tooltip.Content = description;
            row.button.interactable = available;
            _text.enableWordWrapping = true;
            _text.enableAutoSizing = false;
            _text.fontSize = Mathf.Min(_fontSize, 22f);
            _text.autoSizeTextContainer = false;
            _text.maxVisibleLines = int.MaxValue;
            _text.overflowMode = TextOverflowModes.Overflow;
            var labelRect = row.levelLabel.RectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(12f, 6f);
            labelRect.offsetMax = new Vector2(-12f, -6f);
            if (labelRect != _text.rectTransform)
            {
                _text.rectTransform.anchorMin = Vector2.zero;
                _text.rectTransform.anchorMax = Vector2.one;
                _text.rectTransform.offsetMin = Vector2.zero;
                _text.rectTransform.offsetMax = Vector2.zero;
            }

            var height = Mathf.Max(30f,
                Mathf.Ceil(_text.GetPreferredValues(title, Mathf.Max(1f, width - 24f),
                    float.PositiveInfinity).y) + 12f);
            row.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            row.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            _layout.minWidth = _layout.preferredWidth = width;
            _layout.minHeight = _layout.preferredHeight = height;
        }

        public void OnSelect(BaseEventData eventData)
        {
            var scroll = GetComponentInParent<ScrollRect>();

            if (!scroll || !scroll.viewport || !scroll.content)
            {
                return;
            }

            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, _row.RectTransform);
            var viewport = scroll.viewport.rect;
            var position = scroll.content.anchoredPosition;

            if (bounds.max.y > viewport.yMax)
            {
                position.y -= bounds.max.y - viewport.yMax;
            }
            else if (bounds.min.y < viewport.yMin)
            {
                position.y += viewport.yMin - bounds.min.y;
            }

            position.y = Mathf.Clamp(position.y, 0f, Mathf.Max(0f, scroll.content.rect.height - viewport.height));
            scroll.StopMovement();
            scroll.content.anchoredPosition = position;
        }

        internal void Restore()
        {
            _row.OnActivate = null;
            _row.button.interactable = _interactable;
            _row.levelLabel.Text = _label;
            _row.tooltip.Content = _tooltip;
            _text.enableWordWrapping = _wrapping;
            _text.enableAutoSizing = _autoSizing;
            _text.maxVisibleLines = _maximumLines;
            _text.overflowMode = _overflow;
            _text.fontSize = _fontSize;
            _text.autoSizeTextContainer = _autoSizeContainer;
            if (_fitter)
            {
                _fitter.enabled = _fitterEnabled;
            }

            _labelRect.Restore();
            _labelContainerRect.Restore();
            _rowRect.Restore();

            if (_createdLayout)
            {
                Object.DestroyImmediate(_layout);
            }
            else
            {
                _layout.minWidth = _minimumWidth;
                _layout.preferredWidth = _preferredWidth;
                _layout.minHeight = _minimumHeight;
                _layout.preferredHeight = _preferredHeight;
            }
        }
    }

    private sealed class RectState(RectTransform rect)
    {
        private readonly Vector2 _anchorMin = rect.anchorMin;
        private readonly Vector2 _anchorMax = rect.anchorMax;
        private readonly Vector2 _pivot = rect.pivot;
        private readonly Vector2 _sizeDelta = rect.sizeDelta;
        private readonly Vector3 _position = rect.anchoredPosition3D;
        private readonly Vector3 _scale = rect.localScale;
        private readonly Quaternion _rotation = rect.localRotation;

        internal void CopyTo(RectTransform target)
        {
            target.anchorMin = _anchorMin;
            target.anchorMax = _anchorMax;
            target.pivot = _pivot;
            target.sizeDelta = _sizeDelta;
            target.anchoredPosition3D = _position;
            target.localScale = _scale;
            target.localRotation = _rotation;
        }

        internal void Restore()
        {
            CopyTo(rect);
        }
    }
}
