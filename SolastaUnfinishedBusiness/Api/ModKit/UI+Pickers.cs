// Copyright < 2021 > Narria (github user Cabarius) - License: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit.Utility;
using UnityEngine;
using GL = UnityEngine.GUILayout;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static partial class UI
{
    private static float _lastSelectionLayoutWidth;

    private static bool DrawSelectionGrid(ref int selected, string[] texts, int maximumColumns,
        GUIStyle style, int maximumColumnsWhenWrapped, params GUILayoutOption[] options)
    {
        if (texts.Length == 0)
        {
            return false;
        }

        var previousSelection = selected;
        var titles = texts.Select((text, index) => index == previousSelection ? text.Orange().Bold() : text).ToArray();
        var drawStyle = new GUIStyle(style ?? GUI.skin.button) { wordWrap = true };

        using (VerticalScope(new[] { MinWidth(0f), ExpandWidth(true) }.Concat(options).ToArray()))
        {
            var horizontalMargins = drawStyle.margin.horizontal;
            var layout = MeasureResponsiveLayout(titles, maximumColumns, drawStyle,
                reservedWidth: horizontalMargins,
                maximumColumnsWhenWrapped: maximumColumnsWhenWrapped);
            _lastSelectionLayoutWidth = layout.LayoutWidth;
            // GUIGridSizer treats a zero minimum as unspecified and replaces it with the
            // unwrapped labels' width. A positive minimum lets the parent shrink after a
            // resize instead of feeding the previous column count back into its own width.
            var gridOptions = new[] { MinWidth(1f) }.Concat(options).ToArray();

            if (layout.LayoutWidth > 0f)
            {
                if (Event.current.type == EventType.Layout || layout.SelectionHeight <= 0f)
                {
                    var columns = layout.Columns;
                    var horizontalGap = Math.Max(drawStyle.margin.left, drawStyle.margin.right);
                    var gridWidth = layout.LayoutWidth - horizontalMargins;
                    var cellWidth = Mathf.Max(1f, (gridWidth - (columns - 1) * horizontalGap) / columns);
                    var rowHeight = titles.Max(title => drawStyle.CalcHeight(new GUIContent(title), cellWidth));
                    var rows = (texts.Length + columns - 1) / columns;
                    layout.SelectionHeight = rows * rowHeight +
                                             (rows - 1) * Math.Max(drawStyle.margin.top, drawStyle.margin.bottom);
                }

                // Unity's GUIGridSizer uses CalcSize even when wordWrap is enabled. Supply
                // the height measured for this Layout so every subsequent event uses the
                // same bounds and long titles remain visible.
                gridOptions = gridOptions.Concat(new[]
                    { MaxWidth(layout.LayoutWidth), Height(layout.SelectionHeight) }).ToArray();
            }

            selected = GL.SelectionGrid(selected, titles, layout.Columns, drawStyle, gridOptions);
        }

        return previousSelection != selected;
    }

    // UI for picking items from a collection
    [UsedImplicitly]
    public static void Toolbar(ref int value, string[] texts, params GUILayoutOption[] options)
    {
        value = GL.Toolbar(value, texts, options);
    }

    [UsedImplicitly]
    public static void Toolbar(ref int value, string[] texts, GUIStyle style, params GUILayoutOption[] options)
    {
        value = GL.Toolbar(value, texts, style, options);
    }

    [UsedImplicitly]
    public static bool SelectionGrid(ref int selected, string[] texts, int xCols, params GUILayoutOption[] options)
    {
        return DrawSelectionGrid(ref selected, texts, xCols, GUI.skin.button, 4, options);
    }

    [UsedImplicitly]
    public static bool SelectionGrid(string title, ref int selected, string[] texts, int xCols,
        params GUILayoutOption[] options)
    {
        var titleStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        var formattedTitle = title.Cyan();
        var titleWidth = Mathf.Min(300.Point(), titleStyle.CalcSize(new GUIContent(formattedTitle)).x);

        return SelectionGrid(formattedTitle, ref selected, texts, xCols, titleStyle, titleWidth, 0f, options);
    }

    [UsedImplicitly]
    public static bool SelectionGrid(string title, ref int selected, string[] texts, int xCols,
        GUIStyle titleStyle, float titleWidth, float gridWidth, params GUILayoutOption[] options)
    {
        titleStyle ??= GUI.skin.label;

        using (VerticalScope(MinWidth(0f), ExpandWidth(true)))
        {
            var layout = MeasureResponsiveLayout([], 1, titleStyle);
            var requiredGridWidth = gridWidth > 0f
                ? Mathf.Max(gridWidth, MeasureSelectionGridRowWidth(texts, xCols, GUI.skin.button))
                : 200.Point();
            var gridOptions = gridWidth > 0f
                ? new[] { Width(Mathf.Min(requiredGridWidth, Mathf.Max(1f, layout.LayoutWidth))) }.Concat(options).ToArray()
                : options;

            if (layout.LayoutWidth < titleWidth + 25f + requiredGridWidth)
            {
                Label(title, titleStyle, MinWidth(0f), ExpandWidth(true));
                return SelectionGrid(ref selected, texts, xCols, gridOptions);
            }

            using (HorizontalScope())
            {
                // A fixed title minimum feeds the wide row back into its own width
                // measurement after a resize, preventing the stacked layout below it.
                Label(title, titleStyle, MinWidth(0f), MaxWidth(titleWidth), ExpandWidth(true));
                Space(25f);
                var changed = SelectionGrid(ref selected, texts, xCols, gridOptions);
                if (gridWidth > 0f)
                {
                    GL.FlexibleSpace();
                }

                return changed;
            }
        }
    }

    private static float MeasureSelectionGridRowWidth(string[] texts, int maximumColumns, GUIStyle style)
    {
        if (texts.Length == 0)
        {
            return 0f;
        }

        var columns = Mathf.Clamp(maximumColumns <= 0 ? texts.Length : maximumColumns, 1, texts.Length);
        // Any option can become selected. Reserve its emphasized caption and the same
        // outer margins/inter-column gap used by DrawSelectionGrid's column calculation.
        var captionWidth = texts.Max(text => style.CalcSize(new GUIContent(text.Orange().Bold())).x);
        return Mathf.Ceil(columns * captionWidth +
                          (columns - 1) * Math.Max(style.margin.left, style.margin.right) + style.margin.horizontal);
    }

    [UsedImplicitly]
    public static bool SelectionGrid(ref int selected, string[] texts, int xCols, GUIStyle style,
        params GUILayoutOption[] options)
    {
        return SelectionGrid(ref selected, texts, xCols, style, 5, options);
    }

    [UsedImplicitly]
    public static bool SelectionGrid(ref int selected, string[] texts, int xCols, GUIStyle style,
        int maxColsIfNarrow, params GUILayoutOption[] options)
    {
        return DrawSelectionGrid(ref selected, texts, xCols, style, maxColsIfNarrow, options);
    }

    [UsedImplicitly]
    public static bool SelectionGrid<T>(ref int selected, T[] items, int xCols, params GUILayoutOption[] options)
    {
        return DrawSelectionGrid(ref selected, items.Select(item => $"{item}").ToArray(), xCols,
            GUI.skin.button, 4, options);
    }

    [UsedImplicitly]
    public static bool SelectionGrid<T>(ref int selected, T[] items, int xCols, GUIStyle style,
        params GUILayoutOption[] options)
    {
        return DrawSelectionGrid(ref selected, items.Select(item => $"{item}").ToArray(), xCols,
            style, 4, options);
    }

    [UsedImplicitly]
    public static bool SelectionGrid(ref int selected, [NotNull] string[] texts, int xCols, int maxColsIfNarrow = 4,
        params GUILayoutOption[] options)
    {
        return DrawSelectionGrid(ref selected, texts, xCols, GUI.skin.button, maxColsIfNarrow, options);
    }

    [UsedImplicitly]
    public static void ActionSelectionGrid(ref int selected, string[] texts, int xCols, Action<int> action,
        params GUILayoutOption[] options)
    {
        if (SelectionGrid(ref selected, texts, xCols, options))
        {
            action?.Invoke(selected);
        }
    }

    [UsedImplicitly]
    public static void ActionSelectionGrid(ref int selected, string[] texts, int xCols, Action<int> action,
        GUIStyle style, params GUILayoutOption[] options)
    {
        if (SelectionGrid(ref selected, texts, xCols, style, options))
        {
            action?.Invoke(selected);
        }
    }

    // EnumGrids

    [UsedImplicitly]
    public static void EnumGrid<TEnum>(Func<TEnum> get, Action<TEnum> set, int xCols, params GUILayoutOption[] options)
        where TEnum : struct
    {
        var value = get();
        var names = Enum.GetNames(typeof(TEnum));
        var index = Array.IndexOf(names, value.ToString());
        if (!SelectionGrid(ref index, names, xCols, options))
        {
            return;
        }

        if (Enum.TryParse(names[index], out TEnum newValue))
        {
            set(newValue);
        }
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(ref TEnum value, int xCols, Func<string, TEnum, string> titleFormatter = null,
        GUIStyle style = null, params GUILayoutOption[] options) where TEnum : struct
    {
        options = options.AddDefaults();
        var names = Enum.GetNames(typeof(TEnum));
        var formattedNames = names;
        var nameToEnum = value.NameToValueDictionary();
        if (titleFormatter != null)
        {
            formattedNames = names.Select(n => titleFormatter(n, nameToEnum[n])).ToArray();
        }

        var index = Array.IndexOf(names, value.ToString());

        if (style == null
                ? !SelectionGrid(ref index, formattedNames, xCols, options)
                : !SelectionGrid(ref index, formattedNames, xCols, style, options))
        {
            return false;
        }

        if (!Enum.TryParse(names[index], out TEnum newValue))
        {
            return false;
        }

        value = newValue;

        return true;
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(ref TEnum value, int xCols, Func<string, TEnum, string> titleFormatter = null,
        params GUILayoutOption[] options) where TEnum : struct
    {
        return EnumGrid(ref value, xCols, titleFormatter, null, options);
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(ref TEnum value, int xCols, params GUILayoutOption[] options)
        where TEnum : struct
    {
        return EnumGrid(ref value, xCols, null, options);
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(ref TEnum value, params GUILayoutOption[] options) where TEnum : struct
    {
        return EnumGrid(ref value, 0, null, options);
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(string title, ref TEnum value, int xCols, params GUILayoutOption[] options)
        where TEnum : struct
    {
        bool changed;

        using (HorizontalScope())
        {
            Label(title.Cyan(), Width(300f));
            Space(25f);
            changed = EnumGrid(ref value, xCols, null, options);
        }

        return changed;
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(string title, ref TEnum value, params GUILayoutOption[] options)
        where TEnum : struct
    {
        bool changed;

        using (HorizontalScope())
        {
            Label(title.Cyan(), Width(300f));
            Space(25f);
            changed = EnumGrid(ref value, 0, null, options);
        }

        return changed;
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(string title, ref TEnum value, int xCols, GUIStyle style = null,
        params GUILayoutOption[] options) where TEnum : struct
    {
        bool changed;

        using (HorizontalScope())
        {
            Label(title.Cyan(), Width(300f));
            Space(25f);
            changed = EnumGrid(ref value, xCols, null, style, options);
        }

        return changed;
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(string title, ref TEnum value, int xCols,
        Func<string, TEnum, string> titleFormatter = null, params GUILayoutOption[] options) where TEnum : struct
    {
        bool changed;

        using (HorizontalScope())
        {
            Label(title.Cyan(), Width(300f));
            Space(25f);
            changed = EnumGrid(ref value, xCols, titleFormatter, options);
        }

        return changed;
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(string title, ref TEnum value, int xCols,
        Func<string, TEnum, string> titleFormatter = null, GUIStyle style = null, params GUILayoutOption[] options)
        where TEnum : struct
    {
        bool changed;

        using (HorizontalScope())
        {
            Label(title.Cyan(), Width(300f));
            Space(25f);
            changed = EnumGrid(ref value, xCols, titleFormatter, style, options);
        }

        return changed;
    }

    [UsedImplicitly]
    public static bool EnumGrid<TEnum>(string title, Func<TEnum> get, Action<TEnum> set,
        params GUILayoutOption[] options) where TEnum : struct
    {
        bool changed;

        using (HorizontalScope())
        {
            Label(title.Cyan(), Width(300f));
            Space(25f);
            var value = get();
            changed = EnumGrid(ref value, 0, null, options);
            if (changed)
            {
                set(value);
            }
        }

        return changed;
    }

    // EnumerablePicker

    [UsedImplicitly]
    public static void EnumerablePicker<T>(
        string title,
        ref int selected,
        IEnumerable<T> range,
        int xCols,
        Func<T, string> titleFormatter = null,
        params GUILayoutOption[] options)
    {
        titleFormatter ??= a => $"{a}";

        var enumerable = range as T[] ?? range.ToArray();
        if (enumerable.Length > 0 && (selected < 0 || selected >= enumerable.Length))
        {
            selected = 0;
        }

        Label(title, AutoWidth());
        Space(25f);
        SelectionGrid(ref selected, enumerable.Select(titleFormatter).ToArray(), xCols, options);
    }

    [UsedImplicitly]
    public static NamedFunc<T> TypePicker<T>(string title, ref int selectedIndex, NamedFunc<T>[] items) where T : class
    {
        if (items.Length == 0)
        {
            return null;
        }

        selectedIndex = Mathf.Clamp(selectedIndex, 0, items.Length - 1);
        var titles = items.Select(item => item.Name).ToArray();
        if (title?.Length > 0) { Label(title); }

        SelectionGrid(ref selectedIndex, titles, 6);
        return items[selectedIndex];
    }

    // GridPicker

    [UsedImplicitly]
    private static bool GridPicker<T>(ref T selected,
        List<T> items,
        string unselectedTitle,
        Func<T, string> titleFunc,
        ref string searchText,
        int xCols,
        GUIStyle style,
        params GUILayoutOption[] options
    ) where T : class
    {
        options = options.AddDefaults();
        style ??= GUI.skin.button;

        var changed = false;
        if (searchText != null)
        {
            ActionTextField(
                ref searchText,
                "itemSearchText",
                _ => { changed = true; },
                () => { },
                xCols == 1 ? options : [Width(300f)]);
            if (searchText?.Length > 0)
            {
                var searchStr = searchText.ToLower();
#pragma warning disable CA1862
                items = items.Where(i => titleFunc(i).ToLower().Contains(searchStr)).ToList();
#pragma warning restore CA1862
            }
        }

        var selectedItemIndex = items.IndexOf(selected);

        if (items.Count > 0)
        {
            var titles = items.Select(titleFunc);
            var hasUnselectedTitle = unselectedTitle != null;
            if (hasUnselectedTitle)
            {
                titles = titles.Prepend(unselectedTitle);
                selectedItemIndex += 1;
            }

            var adjustedIndex = Math.Max(0, selectedItemIndex);
            if (adjustedIndex != selectedItemIndex)
            {
                selectedItemIndex = adjustedIndex;
                changed = true;
            }

            ActionSelectionGrid(
                ref selectedItemIndex,
                titles.ToArray(),
                xCols,
                _ => { changed = true; },
                style,
                options);
            if (hasUnselectedTitle)
            {
                selectedItemIndex -= 1;
            }

            selected = selectedItemIndex >= 0 ? items[selectedItemIndex] : null;
            //if (changed) Mod.Log($"sel index: {selectedItemIndex} sel: {selected}");
        }
        else
        {
            Label("No Items".Grey(), options);
        }

        return changed;
    }

    [UsedImplicitly]
    public static bool GridPicker<T>(ref T selected, List<T> items,
        string unselectedTitle,
        Func<T, string> titleFunc,
        ref string searchText,
        int xCols,
        params GUILayoutOption[] options
    ) where T : class
    {
        return GridPicker(ref selected, items, unselectedTitle, titleFunc, ref searchText, xCols, ButtonStyle,
            options);
    }

    [UsedImplicitly]
    public static bool GridPicker<T>(ref T selected, List<T> items,
        string unselectedTitle,
        Func<T, string> titleFunc,
        ref string searchText,
        params GUILayoutOption[] options
    ) where T : class
    {
        return GridPicker(ref selected, items, unselectedTitle, titleFunc, ref searchText, 6, ButtonStyle, options);
    }

    [UsedImplicitly]
    // VPicker
    public static bool VPicker<T>(
        string title,
        ref T selected, List<T> items,
        string unselectedTitle,
        Func<T, string> titleFunc,
        ref string searchText,
        Action extras,
        GUIStyle style,
        params GUILayoutOption[] options
    ) where T : class
    {
        // style ??= GUI.skin.button;

        if (title != null)
        {
            Label(title, options);
        }

        extras?.Invoke();
        Div();

        return GridPicker(ref selected, items, unselectedTitle, titleFunc, ref searchText, 1, options);
    }

    [UsedImplicitly]
    public static bool VPicker<T>(
        string title,
        ref T selected, List<T> items,
        string unselectedTitle,
        Func<T, string> titleFunc,
        ref string searchText,
        Action extras,
        params GUILayoutOption[] options
    ) where T : class
    {
        return VPicker(title, ref selected, items, unselectedTitle, titleFunc, ref searchText, extras, ButtonStyle,
            options);
    }

    [UsedImplicitly]
    public static bool VPicker<T>(
        string title,
        ref T selected, List<T> items,
        string unselectedTitle,
        Func<T, string> titleFunc,
        ref string searchText,
        params GUILayoutOption[] options
    ) where T : class
    {
        return VPicker(title, ref selected, items, unselectedTitle, titleFunc, ref searchText, () => { }, ButtonStyle,
            options);
    }
}
