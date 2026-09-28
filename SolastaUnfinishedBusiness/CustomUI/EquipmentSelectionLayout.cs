using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class EquipmentSelectionLayout
{
    internal static void Refresh(EquipmentRowGroup row)
    {
        // Rows are bound while the modal is inactive. Do not discover the owner from a child
        // through active-only ancestor searches, or first display can miss initialization.
        var layout = row.GetComponent<SeparatorLayout>() ?? row.gameObject.AddComponent<SeparatorLayout>();

        layout.Bind(row);
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
