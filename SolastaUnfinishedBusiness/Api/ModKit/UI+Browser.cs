using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit.Utility;
using I2.Loc;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static partial class UI
{
    // Keep catalog preparation outside the hot IMGUI path and bound the number of controls.
    // Source changes are applied on Layout so input events retain that Layout's control tree.
    internal sealed class PagedList<T>(string controlName, int pageSize)
    {
        private IReadOnlyList<T> _source;
        private int _sourceCount = -1;
        private int _revision;
        private string _language;
        private T[] _items = [];
        private string[] _labels = [];
        private int[] _matches = [];
        private string _appliedSearch;
        private int _requestedPage;
        private int _appliedPage = -1;
        private string[] _pageLabels = [];

        internal string SearchText = string.Empty;
        internal int PageSize { get; } = Math.Max(1, pageSize);
        internal int MatchCount => _matches.Length;
        internal int PageCount => Math.Max(1, (MatchCount + PageSize - 1) / PageSize);
        internal int Page { get; private set; }
        internal int VisibleCount => _pageLabels.Length;

        internal void Refresh(IReadOnlyList<T> source, Func<T, string> title, int revision = 0)
        {
            var language = LocalizationManager.CurrentLanguageCode;
            if (ReferenceEquals(source, _source) && source.Count == _sourceCount &&
                revision == _revision && language == _language)
            {
                return;
            }

            if (_sourceCount >= 0 && Event.current?.type != EventType.Layout)
            {
                return;
            }

            _source = source;
            _sourceCount = source.Count;
            _revision = revision;
            _language = language;
            _items = source.ToArray();
            _labels = _items.Select(item => title(item) ?? string.Empty).ToArray();
            _appliedSearch = null;
            _requestedPage = 0;
        }

        private void PreparePage()
        {
            if (Event.current?.type != EventType.Layout && _appliedPage >= 0)
            {
                return;
            }

            var search = SearchText ?? string.Empty;
            if (_appliedSearch != search)
            {
                _appliedSearch = search;
                var terms = search.Split((char[])null,
                    StringSplitOptions.RemoveEmptyEntries);
                _matches = Enumerable.Range(0, _labels.Length).Where(index => terms.All(term =>
                    _labels[index].IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToArray();
                _requestedPage = 0;
                _appliedPage = -1;
            }

            Page = Math.Max(0, Math.Min(_requestedPage, PageCount - 1));
            if (_appliedPage == Page)
            {
                return;
            }

            _appliedPage = Page;
            _pageLabels = _matches.Skip(Page * PageSize).Take(PageSize).Select(index => _labels[index]).ToArray();
        }

        internal void DrawControls(GUIStyle labelStyle = null)
        {
            PreparePage();
            Label(Gui.Localize("ModUi/&SettingsListSearch"), labelStyle ?? GUI.skin.label, ExpandWidth(true));
            TextField(ref SearchText, controlName, MinWidth(0f), ExpandWidth(true));
            if (MatchCount == 0)
            {
                Label(Gui.Localize("ModUi/&SettingsListNoResults"), labelStyle ?? GUI.skin.label, ExpandWidth(true));
            }

            var enabled = GUI.enabled;
            using (HorizontalScope())
            {
                try
                {
                    GUI.enabled = enabled && Page > 0;
                    ActionButton(Gui.Localize("ModUi/&SettingsListPrevious"), () => _requestedPage = Page - 1,
                        AutoWidth());
                    GUI.enabled = enabled;
                    Label(Gui.Format("ModUi/&SettingsListPage", (Page + 1).ToString(), PageCount.ToString(),
                        MatchCount.ToString()), labelStyle ?? GUI.skin.label, MinWidth(0f), ExpandWidth(true));
                    GUI.enabled = enabled && Page + 1 < PageCount;
                    ActionButton(Gui.Localize("ModUi/&SettingsListNext"), () => _requestedPage = Page + 1,
                        AutoWidth());
                }
                finally
                {
                    GUI.enabled = enabled;
                }
            }
        }

        internal T GetVisibleItem(int index)
        {
            return _items[_matches[Page * PageSize + index]];
        }

        internal bool DrawSelection(ref T selected, GUIStyle style, int maximumColumns = 4)
        {
            var selection = -1;
            for (var index = 0; index < VisibleCount; index++)
            {
                if (EqualityComparer<T>.Default.Equals(GetVisibleItem(index), selected))
                {
                    selection = index;
                    break;
                }
            }

            if (!SelectionGrid(ref selection, _pageLabels, maximumColumns, style, maximumColumns,
                    ExpandWidth(true)))
            {
                return false;
            }

            selected = GetVisibleItem(selection);
            return true;
        }
    }

    public static class Browser<T, TItem, TDef> // for many things the item will be the definition
    {
        // ReSharper disable file StaticMemberInGenericType
        private static IEnumerable<TDef> _filteredDefinitions;
        private static readonly Dictionary<string, bool> DisclosureStates = new();
        private static string _prevCallerKey = "";
        private static string _searchText = "";
        private static int _searchLimit = 100;
        private static int _matchCount;
        private static bool _showAll;

        internal static void OnGUI(
            string callerKey, ref bool changed,
            T target,
            IEnumerable<TItem> current,
            IEnumerable<TDef> available,
            Func<TItem, TDef> definition,
            Func<TDef, string> searchAndSortKey,
            Func<TDef, string> title,
            Func<TDef, string> description = null,
            Func<TItem, string> value = null,
            Action<T, TItem, string> setValue = null,
            // The following functors take an target and item and will return an action to perform it if it can, null otherwise
            Func<T, TItem, Action> incrementValue = null,
            Func<T, TItem, Action> decrementValue = null,
            Func<T, TDef, Action> addItem = null,
            Func<T, TItem, Action> removeItem = null,
            // ReSharper disable once UnusedParameter.Global
            Func<T, TItem, Action> childrenOnGUI = null)
        {
            // ReSharper disable once NotAccessedVariable
            var searchChanged = false;
//            var refreshTree = false;
            if (callerKey != _prevCallerKey)
            {
                searchChanged = true;
                _showAll = false;
                DisclosureStates.Clear();
            }

            _prevCallerKey = callerKey;
            using (HorizontalScope())
            {
                100.Space();
                ActionTextField(ref _searchText, "searchText", null, () => { searchChanged = true; }, Width(320));
                25.Space();
                Label("Limit", ExpandWidth(false));
                ActionIntTextField(ref _searchLimit, "searchLimit", null, () => { searchChanged = true; }, Width(175));
                if (_searchLimit > 1000) { _searchLimit = 1000; }

                25.Space();
                searchChanged |= DisclosureToggle("Show All".Orange().Bold(), ref _showAll);
            }

            using (HorizontalScope())
            {
                Space(100);
                ActionButton("Search", () => { searchChanged = true; }, AutoWidth());
                Space(25);
                if (_matchCount > 0 && _searchText.Length > 0)
                {
                    var matchesText = "Matches: ".Green().Bold() + $"{_matchCount}".Orange().Bold();
                    if (_matchCount > _searchLimit) { matchesText += " => ".Cyan() + $"{_searchLimit}".Cyan().Bold(); }

                    Label(matchesText, ExpandWidth(false));
                }
            }

            var currentDict = current.ToDictionary(definition, c => c);
            List<TDef> definitions;
            if (_showAll)
            {
                UpdateSearchResults(_searchText, available, searchAndSortKey);
                definitions = [.._filteredDefinitions];
            }
            else
            {
                definitions = [.. currentDict.Keys];
            }

            var terms = _searchText.Split(' ').Select(s => s.ToLower()).ToHashSet();

            var sorted = definitions.OrderBy(title);
            _matchCount = 0;
            Div(100);
            foreach (var def in sorted)
            {
                var name = title(def);
                var nameLower = name.ToLower();
                if (name is not { Length: > 0 } ||
                    (_searchText.Length != 0 && !terms.All(term => nameLower.Contains(term))))
                {
                    continue;
                }

                currentDict.TryGetValue(def, out var item);
                OnRowGUI(callerKey, ref changed, target, def, item, title, description, value, setValue, incrementValue,
                    decrementValue,
                    addItem, removeItem);
            }
        }

        private static void OnRowGUI(
            string callerKey, ref bool changed,
            T target,
            TDef definition,
            TItem item,
            Func<TDef, string> title,
            Func<TDef, string> description = null,
            Func<TItem, string> value = null,
            // ReSharper disable once UnusedParameter.Local
            Action<T, TItem, string> setValue = null,
            Func<T, TItem, Action> incrementValue = null,
            Func<T, TItem, Action> decrementValue = null,
            Func<T, TDef, Action> addItem = null,
            Func<T, TItem, Action> removeItem = null,
            Func<T, TItem, Action> childrenOnGUI = null)
        {
            // var remWidth = UmmWidth;
            _matchCount++;
            using (HorizontalScope())
            {
                Space(100);
                // remWidth -= 100;
                var titleWidth = Mathf.Floor(UMMWidth / (IsWide ? 3f : 4f));
                var text = title(definition);
                var titleKey = $"{callerKey}-{text}";
                if (item != null)
                {
                    text = text.Cyan().Bold();
                }

                if (childrenOnGUI == null)
                {
                    Label(text, Width(titleWidth));
                    // remWidth -= titleWidth;
                }
                else
                {
                    DisclosureStates.TryGetValue(titleKey, out var show);
                    if (DisclosureToggle(text, ref show, titleWidth))
                    {
                        DisclosureStates[titleKey] = show;
                    }
                }

                Space(10);
                // remWidth -= 10;
                if (item != null && value?.Invoke(item) is { } stringValue)
                {
                    if (decrementValue?.Invoke(target, item) is { } decrementAction)
                    {
                        if (ActionButton("<", () => decrementAction(), 60.Width()))
                        {
                            changed = true;
                        }
                    }
                    else
                    {
                        63.Space();
                    }

                    Space(10f);
                    Label($"{stringValue}".Orange().Bold(), Width(100));
                    if (incrementValue?.Invoke(target, item) is { } incrementer)
                    {
                        if (ActionButton(">", incrementer, 60.Width()))
                        {
                            changed = true;
                        }
                    }
                    else
                    {
                        63.Space();
                    }
                }

                30.Space();
                if (addItem?.Invoke(target, definition) is { } add)
                {
                    if (ActionButton("Add".Localized(), add, 150.Width()))
                    {
                        changed = true;
                    }
                }

                Space(10);
                // remWidth -= 10;
                if (item != null && removeItem?.Invoke(target, item) is { } remove)
                {
                    if (ActionButton("Remove".Localized(), remove, 175.Width()))
                    {
                        changed = true;
                    }
                }

                // remWidth -= 178;
                Space(20);
                // remWidth -= 20;
                using (VerticalScope())
                {
                    if (description != null)
                    {
                        Label(description(definition).StripHTML().Green(), AutoWidth());
                    }
                }
            }

            Div(100);
        }

        [UsedImplicitly]
        public static void UpdateSearchResults(string searchTextParam, IEnumerable<TDef> definitions,
            Func<TDef, string> searchAndSortKey)
        {
            if (definitions == null)
            {
                return;
            }

            var terms = searchTextParam.Split(' ').Select(s => s.ToLower()).ToHashSet();
            var filtered = new List<TDef>();
            foreach (var def in definitions)
            {
                if (def.GetType().ToString().Contains(searchTextParam))
                {
                    filtered.Add(def);
                }
                else
                {
                    var name = searchAndSortKey(def).ToLower();
                    if (terms.All(term => name.Matches(term)))
                    {
                        filtered.Add(def);
                    }
                }
            }

            _matchCount = filtered.Count;
            _filteredDefinitions = filtered.OrderBy(searchAndSortKey).Take(_searchLimit).ToArray();
        }
    }
}
