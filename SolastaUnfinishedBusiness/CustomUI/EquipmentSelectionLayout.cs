using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.Helpers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class EquipmentSelectionLayout
{
    internal static void Refresh(CharacterStageWithEquipmentPanel panel)
    {
        var table = panel.equipmentTable;
        var layout = table.GetComponent<PreviewLayout>() ?? table.gameObject.AddComponent<PreviewLayout>();

        layout.Bind(panel);
    }

    internal static void Refresh(EquipmentRowGroup row)
    {
        // Rows are bound while the modal is inactive. Do not discover the owner from a child
        // through active-only ancestor searches, or first display can miss initialization.
        var layout = row.GetComponent<SeparatorLayout>() ?? row.gameObject.AddComponent<SeparatorLayout>();

        layout.Bind(row);
    }

    private sealed class PreviewLayout : MonoBehaviour
    {
        private const float ItemSpacing = 6f;
        private const float GroupSpacing = 8f;
        private readonly List<EquipmentLine> _lines = [];
        private CharacterStageWithEquipmentPanel _panel;
        private GridLayoutGroup _grid;
        private float _minimumHeight;
        private float _nameOffset;
        private int _columns;
        private int _lastSignature = int.MinValue;

        internal void Bind(CharacterStageWithEquipmentPanel panel)
        {
            if (!_panel)
            {
                _panel = panel;
                _grid = panel.equipmentTable.GetComponent<GridLayoutGroup>();

                if (_grid)
                {
                    var prefab = panel.equipmentLinePrefab.GetComponent<EquipmentLine>();

                    // Pooled rows may retain a changed label position. The unmodified
                    // prefab owns the native clearance between the category icon and name.
                    if (UiTextHelpers.TryGetDescendantLocalBounds(
                            prefab.itemNameLabel.RectTransform, prefab.RectTransform, out var nameBounds))
                    {
                        _nameOffset = nameBounds.xMin - prefab.RectTransform.rect.xMin;
                    }

                    _minimumHeight = _grid.cellSize.y;
                    _columns = _grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount
                        ? _grid.constraintCount
                        : Mathf.Max(1, Mathf.RoundToInt(panel.equipmentTable.rect.width / _grid.cellSize.x));
                }
            }

            _lastSignature = int.MinValue;
            Refresh();
        }

        private void OnEnable()
        {
            _lastSignature = int.MinValue;
        }

        private void LateUpdate()
        {
            Refresh();
        }

        private void Refresh()
        {
            if (!_panel || !_grid || _panel.equipmentTable.rect.width <= 0)
            {
                return;
            }

            var table = _panel.equipmentTable;
            _lines.Clear();

            // EnumerateSelectedEquipment owns the table's direct children. A prefab can
            // contain another EquipmentLine on its label; it is not a second grid cell.
            foreach (Transform child in table)
            {
                if (child.gameObject.activeSelf && child.GetComponent<EquipmentLine>() is { } line)
                {
                    _lines.Add(line);
                }
            }

            var signature = table.rect.width.GetHashCode();

            unchecked
            {
                signature = signature * 31 + _panel.RectTransform.rect.height.GetHashCode();
                signature = signature * 31 + _panel.CanChangeEquipment.GetHashCode();

                foreach (var line in _lines)
                {
                    var text = line.itemNameLabel.TMP_Text;

                    signature = signature * 31 + line.GetInstanceID();
                    signature = signature * 31 + (text.text?.GetHashCode() ?? 0);
                    signature = signature * 31 + (text.font ? text.font.GetInstanceID() : 0);
                    signature = signature * 31 + text.fontSize.GetHashCode();
                    signature = signature * 31 + text.fontStyle.GetHashCode();
                    signature = signature * 31 + text.characterSpacing.GetHashCode();
                    signature = signature * 31 + text.wordSpacing.GetHashCode();
                    signature = signature * 31 + text.margin.GetHashCode();
                    signature = signature * 31 + (line.quantityGroup && line.quantityGroup.gameObject.activeSelf
                        ? line.quantityGroup.rect.width.GetHashCode()
                        : 0);
                }
            }

            if (_lastSignature == signature)
            {
                return;
            }

            var columns = Mathf.Clamp(_columns, 1, Mathf.Max(1, _lines.Count));
            var width = Mathf.Max(1f,
                (table.rect.width - _grid.padding.horizontal - _grid.spacing.x * (columns - 1)) / columns);
            var height = _minimumHeight;

            foreach (var line in _lines)
            {
                var text = line.itemNameLabel.TMP_Text;
                var nameRect = text.rectTransform;

                // Native Bind places the badge after the label's unbounded preferred width.
                // Reserve it inside the cell before measuring wrapped text instead.
                var quantity = line.quantityGroup;
                var quantityWidth = quantity && quantity.gameObject.activeSelf ? quantity.rect.width : 0f;
                var textWidth = Mathf.Max(1f, width - _nameOffset - (quantityWidth > 0 ? quantityWidth + ItemSpacing : 0f));

                foreach (var fitter in line.itemNameLabel.GetComponentsInChildren<ContentSizeFitter>(true))
                {
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                }

                text.enableWordWrapping = true;
                var preferred = text.GetPreferredValues(text.text, textWidth, float.PositiveInfinity);

                if (float.IsNaN(preferred.y) || float.IsInfinity(preferred.y))
                {
                    continue;
                }

                var textHeight = Mathf.Max(_minimumHeight, Mathf.Ceil(preferred.y));

                nameRect.anchorMin = nameRect.anchorMax = new Vector2(0, 0.5f);
                nameRect.pivot = new Vector2(0, 0.5f);
                nameRect.anchoredPosition = new Vector2(_nameOffset, 0);
                nameRect.sizeDelta = new Vector2(textWidth, textHeight);
                height = Mathf.Max(height, textHeight);

                if (quantity && quantity.gameObject.activeSelf)
                {
                    quantity.anchorMin = quantity.anchorMax = new Vector2(1, 0.5f);
                    quantity.pivot = new Vector2(1, 0.5f);
                    quantity.anchoredPosition = Vector2.zero;
                }
            }

            _grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            _grid.constraintCount = columns;
            _grid.cellSize = new Vector2(width, height);
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);

            var rows = Mathf.CeilToInt((float)_lines.Count / columns);
            var tableHeight = _grid.padding.vertical + rows * height + Mathf.Max(0, rows - 1) * _grid.spacing.y;

            table.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, tableHeight);

            var groupHeight = _panel.equipmentHeader.RectTransform.rect.height + tableHeight + GroupSpacing;

            if (_panel.CanChangeEquipment)
            {
                groupHeight += ((RectTransform)_panel.changeEquipmentButton.transform).rect.height + GroupSpacing;
            }

            _panel.equipmentGroup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, groupHeight);

            if (_panel is CharacterStageClassSelectionPanel classes)
            {
                // The class feature scroll view shares the space above the bottom equipment block.
                var available = classes.RectTransform.rect.height - _panel.equipmentGroup.anchoredPosition.y -
                                groupHeight - 10f + classes.classFeaturesGroup.anchoredPosition.y;

                classes.classFeaturesGroup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(0, available));
            }

            LayoutRebuilder.MarkLayoutForRebuild(_panel.RectTransform);
            _lastSignature = signature;
        }
    }

    private sealed class SeparatorLayout : MonoBehaviour
    {
        private readonly List<(RectTransform Marker, TMP_Text Text)> _separators = [];
        private float _minimumWidth;
        private float _separatorClearance;
        private float _markerOffset;
        private float _originalSpacing;
        private EquipmentRowGroup _row;
        private HorizontalLayoutGroup _layout;
        private bool _refreshColumns;
        private int _childCount;
        private int _lastSignature = int.MinValue;

        internal void Bind(EquipmentRowGroup row)
        {
            if (!_row)
            {
                _row = row;
                _layout = row.columnsTable.GetComponent<HorizontalLayoutGroup>();

                if (_layout)
                {
                    // Read the unmodified prefab: pooled columns can already have a wider marker.
                    var marker = row.columnPrefab.GetComponent<EquipmentColumnGroup>().orMarker;

                    _minimumWidth = marker.rect.width;
                    _markerOffset = marker.anchoredPosition.x;
                    _originalSpacing = _layout.spacing;

                    // Keep the native clearance before the next radio button. Centering the marker
                    // in the gap would halve that clearance and move it toward the right-hand choice.
                    _separatorClearance = Mathf.Max(0, _originalSpacing - _minimumWidth);
                }
            }

            _refreshColumns = true;
        }

        private void OnEnable()
        {
            _refreshColumns = true;
        }

        private void OnDisable()
        {
            _separators.Clear();
        }

        private void OnRectTransformDimensionsChange()
        {
            _lastSignature = int.MinValue;
        }

        private void LateUpdate()
        {
            if (!_row || !_layout || _row.columnsTable.rect.width <= 0 ||
                Mathf.Approximately(_row.columnsTable.lossyScale.x, 0))
            {
                return;
            }

            if (_refreshColumns || _childCount != _row.columnsTable.childCount)
            {
                _refreshColumns = false;
                _separators.Clear();
                _childCount = _row.columnsTable.childCount;

                foreach (Transform child in _row.columnsTable)
                {
                    if (child.gameObject.activeSelf &&
                        child.GetComponent<EquipmentColumnGroup>() is { } column)
                    {
                        if (column.orMarker.gameObject.activeSelf &&
                            column.orMarker.GetComponentInChildren<GuiLabel>(true)?.TMP_Text is { } text)
                        {
                            _separators.Add((column.orMarker, text));
                        }
                    }
                }

                _lastSignature = int.MinValue;
            }

            var signature = 17;

            foreach (var separator in _separators)
            {
                var text = separator.Text;

                // Enforce presentation after native Awake/style application, including the first
                // activation of a previously inactive hierarchy. Never shorten or shrink the text.
                text.enableWordWrapping = false;

                unchecked
                {
                    signature = signature * 31 + (text.text?.GetHashCode() ?? 0);
                    signature = signature * 31 + (text.font ? text.font.GetInstanceID() : 0);
                    signature = signature * 31 + text.fontSize.GetHashCode();
                    signature = signature * 31 + text.fontStyle.GetHashCode();
                    signature = signature * 31 + text.characterSpacing.GetHashCode();
                    signature = signature * 31 + text.wordSpacing.GetHashCode();
                    signature = signature * 31 + text.margin.GetHashCode();
                }
            }

            if (_lastSignature != signature)
            {
                var width = _minimumWidth;

                foreach (var separator in _separators)
                {
                    var text = separator.Text;
                    var preferred = text.GetPreferredValues(text.text, float.PositiveInfinity, float.PositiveInfinity);

                    if (float.IsNaN(preferred.x) || float.IsInfinity(preferred.x))
                    {
                        return;
                    }

                    width = Mathf.Max(width, Mathf.Ceil(preferred.x));
                }

                _lastSignature = signature;
                _layout.spacing = Mathf.Max(_originalSpacing, width + _separatorClearance);

                foreach (var separator in _separators)
                {
                    var marker = separator.Marker;
                    var position = marker.anchoredPosition;

                    marker.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);

                    if (!Mathf.Approximately(position.x, _markerOffset))
                    {
                        position.x = _markerOffset;
                        marker.anchoredPosition = position;
                    }
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(_row.columnsTable);
            }
        }
    }
}
