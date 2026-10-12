// Copyright < 2021 > Narria (github user Cabarius) - License: MIT

using System;
using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using GL = UnityEngine.GUILayout;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static partial class UI
{
    private static float _contentWidth;
    private static GUIStyle _alignedRowStyle;

    private static GUIStyle AlignedRowStyle => _alignedRowStyle ??= new GUIStyle();

    // ReSharper disable once InconsistentNaming
    private static float UMMWidth => _contentWidth > 0f ? _contentWidth : ScreenWidthInGui;
    private static bool IsWide => UMMWidth >= 1920.Point();

    private static float ScreenWidthInGui => Mathf.Abs(
        GUIUtility.ScreenToGUIPoint(new Vector2(Screen.width, 0f)).x -
        GUIUtility.ScreenToGUIPoint(Vector2.zero).x);

    private sealed class ResponsiveLayoutState
    {
        public float MeasuredWidth;
        public float LayoutWidth;
        public float SelectionHeight;
        public int Columns = 1;

        public ResponsiveLayoutState()
        {
        }
    }

    private static ResponsiveLayoutState MeasureResponsiveLayout(string[] labels, int maximumColumns,
        GUIStyle style, float reservedWidth = 0f, int maximumColumnsWhenWrapped = int.MaxValue,
        float minimumCellWidth = 0f, float[] preferredCellWidths = null)
    {
        var controlId = GUIUtility.GetControlID(nameof(ResponsiveLayoutState).GetHashCode(), FocusType.Passive);
        var state = (ResponsiveLayoutState)GUIUtility.GetStateObject(typeof(ResponsiveLayoutState), controlId);
        var rect = GUILayoutUtility.GetRect(0f, ScreenWidthInGui, 0f, 0f, ExpandWidth(true));

        // Repaint supplies the parent's assigned width. Keep this Layout's column count for
        // every input/Repaint event, so a resize cannot invalidate the current GUILayout tree.
        if (Event.current.type == EventType.Layout)
        {
            state.LayoutWidth = state.MeasuredWidth;
            var availableWidth = Mathf.Max(0f, state.LayoutWidth - reservedWidth);
            state.Columns = preferredCellWidths == null
                ? CalculateResponsiveColumns(labels, maximumColumns, availableWidth, style,
                    maximumColumnsWhenWrapped, minimumCellWidth)
                : CalculateResponsiveColumns(preferredCellWidths, maximumColumns, availableWidth);
        }
        else if (Event.current.type == EventType.Repaint && rect.width > 0f)
        {
            state.MeasuredWidth = rect.width;
        }

        return state;
    }

    private static int CalculateResponsiveColumns(string[] labels, int maximumColumns, float availableWidth,
        GUIStyle style, int maximumColumnsWhenWrapped = int.MaxValue, float minimumCellWidth = 0f)
    {
        var requested = Mathf.Clamp(maximumColumns <= 0 ? labels.Length : maximumColumns, 1,
            Math.Max(1, labels.Length));
        if (availableWidth <= 0f)
        {
            return 1;
        }

        var cellWidth = minimumCellWidth;
        foreach (var label in labels)
        {
            var content = new GUIContent(label ?? string.Empty);
            var preferredWidth = style.CalcSize(content).x;

            if (style.wordWrap)
            {
                // Let long localized names wrap within a readable cell instead of making
                // every item require the longest name's unwrapped width.
                style.CalcMinMaxWidth(content, out var minimumWordWidth, out _);
                preferredWidth = Mathf.Max(minimumWordWidth, Mathf.Min(preferredWidth, 300.Point()));
            }

            cellWidth = Mathf.Max(cellWidth, preferredWidth);
        }

        var gap = Math.Max(style.margin.left, style.margin.right);
        var columns = Mathf.Clamp(Mathf.FloorToInt((availableWidth + gap) / Mathf.Max(1f, cellWidth + gap)),
            1, requested);

        return columns < requested ? Math.Min(columns, Math.Max(1, maximumColumnsWhenWrapped)) : columns;
    }

    private static int CalculateResponsiveColumns(float[] preferredWidths, int maximumColumns, float availableWidth)
    {
        var requested = Mathf.Clamp(maximumColumns <= 0 ? preferredWidths.Length : maximumColumns, 1,
            Math.Max(1, preferredWidths.Length));
        if (availableWidth <= 0f)
        {
            return 1;
        }

        // Action rows contain captions and compound controls of different widths. Unlike
        // an equal-cell selection grid, they only need the sum of each row's natural widths.
        for (var columns = requested; columns > 1; columns--)
        {
            var fits = true;
            for (var first = 0; first < preferredWidths.Length; first += columns)
            {
                var rowWidth = 0f;
                for (var column = first; column < Math.Min(first + columns, preferredWidths.Length); column++)
                {
                    rowWidth += preferredWidths[column];
                }

                if (rowWidth <= availableWidth)
                {
                    continue;
                }

                fits = false;
                break;
            }

            if (fits)
            {
                return columns;
            }
        }

        return 1;
    }

    public static int ResponsiveColumns(string[] labels, int maximumColumns, GUIStyle style = null,
        float reservedWidth = 0f)
    {
        return MeasureResponsiveLayout(labels ?? [], maximumColumns, style ?? GUI.skin.button, reservedWidth).Columns;
    }

    /*** UI Builders
     *
     * This is a simple UI framework that simulates the style of SwiftUI.
     *
     * Usage - these are intended to be called from any OnGUI render path used in your mod
     *
     * Elements will be defined like this
            UI.Section("Cheap Tricks", () =>
            {
                UI.HStack("Combat", 4,
                    () => { UI.ActionButton("Rest All", () => { CheatsCombat.RestAll(); }); },
                    () => { UI.ActionButton("Empowered", () => { CheatsCombat.Empowered(""); }); },
                    () => { UI.ActionButton("Full Buff Please", () => { CheatsCombat.RestAll(); }); },
                    () => { UI.ActionButton("Remove Death's Door", () => { CheatsCombat.Empowered(""); }); },
                    () => { UI.ActionButton("Kill All Enemies", () => { CheatsCombat.KillAll(); }); },
                    () => { UI.ActionButton("Summon Zoo", () => { CheatsCombat.SpawnInspectedEnemiesUnderCursor(""); }); }
                 );
                UI.Space(10);
                UI.HStack("Common", 4,
                    () => { UI.ActionButton("Change Weather", () => { CheatsCommon.ChangeWeather(""); }); },
                    () => { UI.ActionButton("Set Perception to 40", () => { CheatsCommon.StatPerception(); }); }
                 );
                UI.Space(10);
                UI.HStack("Unlocks", 4,
                    () => { UI.ActionButton("Give All Items", () => { CheatsUnlock.CreateAllItems(""); }); }
                 );
            });
    */

    private static void If(bool value, params Action[] actions)
    {
        if (!value)
        {
            return;
        }

        foreach (var action in actions)
        {
            action();
        }
    }

    public static void HStack(string title = null, int stride = 0, params Action[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        var labels = new string[actions.Length];
        stride = MeasureResponsiveLayout(labels, stride, GUI.skin.button, title == null ? 0f : 153f,
            minimumCellWidth: 200.Point()).Columns;
        DrawHStack(title, stride, actions);
    }

    public static void HStack(string title, int stride, params NamedAction[] actions)
    {
        HStack(title, stride, GUI.skin.button, actions);
    }

    public static void HStack(string title, int stride, GUIStyle style, params NamedAction[] actions)
    {
        HStack(title, stride, style, null, actions);
    }

    public static void HStack(string title, int stride, GUIStyle style, float[] preferredWidths,
        params NamedAction[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        var labels = new string[actions.Length];
        var reservedWidth = title == null ? 0f : 153f;
        style ??= GUI.skin.button;
        var widths = new float[actions.Length];
        for (var index = 0; index < actions.Length; index++)
        {
            var label = labels[index] = actions[index].Name;
            // A compound control or a mixed-style row can supply its measured natural
            // width. Other cells retain the caption-based measurement.
            if (preferredWidths != null && index < preferredWidths.Length && preferredWidths[index] > 0f)
            {
                widths[index] = preferredWidths[index];
                continue;
            }

            widths[index] = string.IsNullOrEmpty(label)
                ? 200.Point()
                : Mathf.Max(1f, style.CalcSize(new GUIContent(label)).x + style.margin.horizontal);
        }
        var layout = MeasureResponsiveLayout(labels, stride, style, reservedWidth, preferredCellWidths: widths);
        DrawHStack(title, layout.Columns, null, Mathf.Max(0f, layout.LayoutWidth - reservedWidth), widths, actions);
    }

    private static void DrawNamedAction(NamedAction action)
    {
        var guiEnabled = GUI.enabled;

        try
        {
            GUI.enabled = guiEnabled && action.CanPerform();
            action.Action();
        }
        finally
        {
            GUI.enabled = guiEnabled;
        }
    }

    // A list can supply the same column measurements for every row. Fixed columns
    // retain their preferred widths while the caption column uses the remaining space.
    public static void AlignedRow(int flexibleColumn, float[] preferredWidths, float[] minimumWidths,
        params NamedAction[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        var layout = MeasureResponsiveLayout([], actions.Length, GUI.skin.label,
            preferredCellWidths: minimumWidths);

        for (var first = 0; first < actions.Length; first += layout.Columns)
        {
            var count = Math.Min(layout.Columns, actions.Length - first);
            var widths = new float[count];
            var minimumWidth = 0f;
            var fixedGrowth = 0f;
            var flexible = flexibleColumn >= first && flexibleColumn < first + count
                ? flexibleColumn - first
                : count - 1;

            for (var column = 0; column < count; column++)
            {
                widths[column] = minimumWidths[first + column];
                minimumWidth += widths[column];
                if (column != flexible)
                {
                    fixedGrowth += Mathf.Max(0f, preferredWidths[first + column] - widths[column]);
                }
            }

            var availableWidth = Mathf.Max(1f, layout.LayoutWidth);
            if (minimumWidth > availableWidth)
            {
                for (var column = 0; column < count; column++)
                {
                    widths[column] *= availableWidth / minimumWidth;
                }
            }
            else
            {
                var remaining = availableWidth - minimumWidth;
                var fixedShare = fixedGrowth > 0f ? Mathf.Min(1f, remaining / fixedGrowth) : 0f;
                for (var column = 0; column < count; column++)
                {
                    if (column != flexible)
                    {
                        widths[column] += Mathf.Max(0f, preferredWidths[first + column] - widths[column]) * fixedShare;
                    }
                }

                widths[flexible] += remaining - fixedGrowth * fixedShare;
            }

            using (HorizontalScope(AlignedRowStyle, MinWidth(0f), ExpandWidth(true)))
            {
                for (var column = 0; column < count; column++)
                {
                    // An explicit zero-margin style keeps child margins inside the
                    // assigned cell. Only cap its width: using the previous Repaint's
                    // width as a minimum can force the scroll parent wider on resize.
                    using (VerticalScope(AlignedRowStyle, Width(widths[column]), MinWidth(0f)))
                    {
                        DrawNamedAction(actions[first + column]);
                    }
                }
            }
        }
    }

    public static void AlignedActions(float[] preferredWidths, params NamedAction[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        var layout = MeasureResponsiveLayout([], actions.Length, GUI.skin.button,
            preferredCellWidths: preferredWidths);
        for (var first = 0; first < actions.Length; first += layout.Columns)
        {
            using (HorizontalScope(AlignedRowStyle, MinWidth(0f), ExpandWidth(true)))
            {
                GL.FlexibleSpace();
                for (var index = first; index < Math.Min(first + layout.Columns, actions.Length); index++)
                {
                    // GUILayout applies the controls' actual margins once. Additional
                    // proportional cell groups can otherwise wrap a caption that fits.
                    DrawNamedAction(actions[index]);
                }
            }
        }
    }

    private static void DrawHStack(string title, int stride, Action[] actions, float availableWidth = 0f,
        float[] preferredWidths = null, NamedAction[] namedActions = null)
    {
        var actionCount = namedActions?.Length ?? actions.Length;
        for (var first = 0; first < actionCount; first += stride)
        {
            var hasTitle = title != null;

            using (HorizontalScope())
            {
                if (hasTitle)
                {
                    if (first == 0)
                    {
                        Label(title.Bold(), Width(150f));
                    }
                    else
                    {
                        Space(153f);
                    }
                }

                var end = Math.Min(first + stride, actionCount);
                var preferredRowWidthSum = 0d;
                if (preferredWidths != null)
                {
                    for (var index = first; index < end; index++)
                    {
                        preferredRowWidthSum += preferredWidths[index];
                    }
                }
                var preferredRowWidth = (float)preferredRowWidthSum;

                for (var index = first; index < end; index++)
                {
                    if (availableWidth <= 0f || preferredRowWidth <= 0f)
                    {
                        DrawHStackAction(index, actions, namedActions);
                        continue;
                    }

                    // A nested group can otherwise claim the entire row's stretchable width,
                    // squeezing sibling captions despite the chosen column count. Distribute
                    // the row in proportion to its captions' natural widths so each fits on one
                    // line whenever their total fits. A zero minimum lets the parent shrink.
                    var maximumCellWidth = Mathf.Max(1f,
                        Mathf.Floor(availableWidth * preferredWidths[index] / preferredRowWidth));
                    using (VerticalScope(MinWidth(0f), MaxWidth(maximumCellWidth), ExpandWidth(true)))
                    {
                        DrawHStackAction(index, actions, namedActions);
                    }
                }
            }
        }
    }

    private static void DrawHStackAction(int index, Action[] actions, NamedAction[] namedActions)
    {
        if (namedActions != null)
        {
            DrawNamedAction(namedActions[index]);
        }
        else
        {
            actions[index]();
        }
    }

    public static void TabBar(ref int selected, Action header = null, params NamedAction[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        if (selected < 0 || selected >= actions.Length)
        {
            selected = 0;
        }

        var sel = selected;
        var titles = actions.Select((a, i) =>
            i == sel ? a.Name.Orange().Bold() : a.Name);
        var enumerable = titles as string[] ?? titles.ToArray();

        SelectionGrid(ref selected, enumerable.ToArray(), enumerable.Length, 6, ExpandWidth(true));
        if (_lastSelectionLayoutWidth > 0f)
        {
            _contentWidth = _lastSelectionLayoutWidth;
        }
        GL.BeginVertical("box");
        header?.Invoke();
        actions[selected].Action();
        GL.EndVertical();
    }

    public static void SubMenu(ref int selected, bool div = true, Action header = null, params NamedAction[] actions)
    {
        if (actions.Length == 0)
        {
            return;
        }

        if (selected < 0 || selected >= actions.Length)
        {
            selected = 0;
        }

        var sel = selected;
        var titles = actions.Select((a, i) =>
            i == sel ? a.Name.Orange().Bold() : a.Name);
        var enumerable = titles as string[] ?? titles.ToArray();
        using (HorizontalScope(GUI.skin.scrollView))
        {
            using (VerticalScope())
            {
                SelectionGrid(ref selected, enumerable.ToArray(), 0, SubmenuButtonStyle, AutoWidth());
                if (div)
                {
                    DivLast(20);
                    Space((float)-25);
                }

                header?.Invoke();
                actions[selected].Action();
            }
        }
    }
}
