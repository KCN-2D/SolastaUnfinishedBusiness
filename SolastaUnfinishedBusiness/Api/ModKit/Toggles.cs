using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static partial class UI
{
    private static readonly GUIContent LabelContent = new();
    private static readonly GUIContent CheckOn = new(Sprites.CheckOnTexture);
    private static readonly GUIContent CheckOff = new(Sprites.CheckOffTexture);
    private static readonly GUIContent DisclosureOn = new(Sprites.ExpandedTexture);
    private static readonly GUIContent DisclosureOff = new(Sprites.CollapsedTexture);
    private static readonly GUIContent DisclosureEmpty = new(string.Empty);

    private static readonly int SButtonHint = "MyGUI.Button".GetHashCode();

    private static GUIContent SetLabelContent(string text)
    {
        LabelContent.text = text;
        LabelContent.image = null;
        LabelContent.tooltip = null;

        return LabelContent;
    }

    internal static bool Toggle(
        Rect rect,
        GUIContent label,
        bool value,
        bool isEmpty,
        GUIContent on,
        GUIContent off,
        GUIStyle stateStyle,
        GUIStyle labelStyle)
    {
        var controlID = GUIUtility.GetControlID(SButtonHint, FocusType.Passive, rect);
        var result = false;

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (Event.current.GetTypeForControl(controlID))
        {
            case EventType.MouseDown:
                if (GUI.enabled && rect.Contains(Event.current.mousePosition))
                {
                    GUIUtility.hotControl = controlID;
                    Event.current.Use();
                }

                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl == controlID)
                {
                    Event.current.Use();
                }

                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl == controlID)
                {
                    GUIUtility.hotControl = 0;

                    if (rect.Contains(Event.current.mousePosition))
                    {
                        result = true;
                        Event.current.Use();
                    }
                }

                break;

            case EventType.KeyDown:
                if (GUIUtility.hotControl == controlID)
                {
                    if (Event.current.keyCode == KeyCode.Escape)
                    {
                        GUIUtility.hotControl = 0;
                        Event.current.Use();
                    }
                }

                break;

            case EventType.Repaint:
            {
                var rightAlign =
                        stateStyle.alignment is TextAnchor.MiddleRight or TextAnchor.UpperRight or TextAnchor.LowerRight
                    ;
                // stateStyle.alignment determines position of state element
                var state = isEmpty ? DisclosureEmpty : value ? on : off;
                var stateSize =
                    stateStyle.CalcSize(value
                        ? on
                        : off); // don't use the empty content to calculate size so titles line up in lists
                var x = rightAlign ? rect.xMax - stateSize.x : rect.x;

                Rect stateRect = new(x, rect.y, stateSize.x, stateSize.y);

                // Use the assigned space for every line, including the clickable area.
                // A natural one-line width can otherwise expand the whole settings page.
                x = rightAlign ? rect.x : stateRect.xMax + 5;
                var labelRight = rightAlign ? stateRect.x - 5 : rect.xMax;
                Rect labelRect = new(x, rect.y, Mathf.Max(0f, labelRight - x), rect.height);

                stateStyle.Draw(stateRect, state, controlID);
                labelStyle.Draw(labelRect, label, controlID);
            }
                break;
        }

        return result;
    }

    // Button Control
    internal static bool Toggle(
        GUIContent label,
        bool value,
        GUIContent on,
        GUIContent off,
        GUIStyle stateStyle,
        GUIStyle labelStyle,
        bool isEmpty = false,
        params GUILayoutOption[] options)
    {
        var state = value ? on : off;
        var stateSize = stateStyle.CalcSize(state);
        var drawStyle = new GUIStyle(labelStyle) { wordWrap = true, fixedHeight = 0f };
        var layoutStyle = new GUIStyle(drawStyle);
        var padding = labelStyle.padding;
        var stateWidth = Mathf.CeilToInt(stateSize.x) + 5;
        var stateHeightPadding = Mathf.CeilToInt(
            Mathf.Max(0f, stateSize.y - drawStyle.CalcHeight(GUIContent.none, float.MaxValue)));
        var rightAlign = stateStyle.alignment is
            TextAnchor.MiddleRight or TextAnchor.UpperRight or TextAnchor.LowerRight;
        layoutStyle.padding = new RectOffset(
            padding.left + (rightAlign ? 0 : stateWidth),
            padding.right + (rightAlign ? stateWidth : 0), padding.top, padding.bottom + stateHeightPadding);

        // GUILayout calculates the wrapped height after assigning the available width.
        // Keep its style separate from painting: GUILayout retains it until CalcHeight.
        // MinHeight can force GUIWordWrapSizer's max height too, disabling word wrapping.
        var rect = GUILayoutUtility.GetRect(label, layoutStyle, options);

        return Toggle(rect, label, value, isEmpty, on, off, stateStyle, drawStyle);
    }

    // Disclosure Toggles
    private static bool DisclosureToggle(string label, bool value, bool isEmpty = false,
        params GUILayoutOption[] options)
    {
        return Toggle(SetLabelContent(label), value, DisclosureOn, DisclosureOff, GUI.skin.box, GUI.skin.label, isEmpty,
            options);
    }

    // CheckBox
    private static bool CheckBox(string label, bool value, bool isEmpty, GUIStyle style,
        params GUILayoutOption[] options)
    {
        return Toggle(SetLabelContent(label), value, CheckOn, CheckOff, GUI.skin.box, style, isEmpty, options);
    }
}
