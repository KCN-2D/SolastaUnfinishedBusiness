using System;
using System.Collections.Generic;
using System.Linq;
using I2.Loc;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal interface IMenuPage
{
    string Name { get; }

    int Priority { get; }

    // ReSharper disable once UnusedParameter.Global
    void OnGUI(UnityModManager.ModEntry modEntry);
}

internal interface IMenuTopPage : IMenuPage;

internal interface IMenuSelectablePage : IMenuPage;

internal interface IMenuBottomPage : IMenuPage;

internal sealed class MenuManager
{
    private Exception _caughtException;
    private readonly List<IMenuBottomPage> _bottomPages = [];
    private readonly List<IMenuSelectablePage> _selectablePages = [];
    private readonly List<IMenuTopPage> _topPages = [];
    private string _tabLanguage;
    private string[] _tabTitles;

    internal void Enable(UnityModManager.ModEntry modEntry, Type[] types)
    {
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters ||
                !typeof(IMenuPage).IsAssignableFrom(type))
            {
                continue;
            }

            var page = (IMenuPage)Activator.CreateInstance(type, true);

            if (page is IMenuTopPage topPage)
            {
                _topPages.Add(topPage);
            }

            if (page is IMenuSelectablePage selectablePage)
            {
                _selectablePages.Add(selectablePage);
            }

            if (page is IMenuBottomPage bottomPage)
            {
                _bottomPages.Add(bottomPage);
            }
        }

        _topPages.Sort(ComparePriority);
        _selectablePages.Sort(ComparePriority);
        _bottomPages.Sort(ComparePriority);

        modEntry.OnGUI += OnGUI;
        modEntry.OnShowGUI += OnShowGUI;
    }

    internal void Unload(UnityModManager.ModEntry modEntry)
    {
        modEntry.OnGUI -= OnGUI;
        modEntry.OnShowGUI -= OnShowGUI;
        _topPages.Clear();
        _selectablePages.Clear();
        _bottomPages.Clear();
        _tabTitles = null;
        _caughtException = null;
    }

    private static int ComparePriority(IMenuPage first, IMenuPage second)
    {
        return first.Priority.CompareTo(second.Priority);
    }

    private static void OnShowGUI(UnityModManager.ModEntry _)
    {
        if (Main.Settings.EnableHeroesControlledByComputer)
        {
            PlayerControllerContext.RefreshGuiState();
        }
    }

    private void OnGUI(UnityModManager.ModEntry modEntry)
    {
        if (Main.SettingsRestartRequired)
        {
            var descriptionStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            UI.Label(Gui.Localize("ModUi/&SettingsImportPending"), descriptionStyle, UI.ExpandWidth(true));
            if (!string.IsNullOrEmpty(Main.SettingsOperationMessage))
            {
                UI.Label(Gui.Localize(Main.SettingsOperationMessage), descriptionStyle, UI.ExpandWidth(true));
            }
            UI.ActionButton(Gui.Localize("ModUi/&SettingsImportCancel"), Main.CancelSettingsImport);
            return;
        }

        try
        {
            if (_caughtException != null)
            {
                GUILayout.Label("ERROR".Red().Bold() +
                                $": caught exception {_caughtException}");

                if (GUILayout.Button("Reset".Orange().Bold(), GUILayout.ExpandWidth(false)))
                {
                    _caughtException = null;
                }

                return;
            }

            UI.UserHasHitReturn = Event.current.keyCode == KeyCode.Return;
            UI.FocusedControlName = GUI.GetNameOfFocusedControl();

            var hasPriorPage = false;
            DrawPages(_topPages, modEntry, ref hasPriorPage);

            if (_selectablePages.Count > 0)
            {
                DrawSelectedPage(modEntry);
                hasPriorPage = true;
            }

            DrawPages(_bottomPages, modEntry, ref hasPriorPage);
        }
        catch (Exception e)
        {
            _caughtException = e;
        }
    }

    private void DrawSelectedPage(UnityModManager.ModEntry modEntry)
    {
        var selected = Main.Settings.SelectedTab;

        if (selected < 0 || selected >= _selectablePages.Count)
        {
            selected = 0;
        }

        if (_selectablePages.Count > 1)
        {
            UI.TabBar(ref selected, GetTabTitles());
        }

        Main.Settings.SelectedTab = selected;

        using (UI.VerticalScope(GUI.skin.box))
        {
            _selectablePages[selected].OnGUI(modEntry);
        }
    }

    private string[] GetTabTitles()
    {
        var language = LocalizationManager.CurrentLanguageCode;

        if (_tabTitles == null || _tabLanguage != language)
        {
            _tabTitles = _selectablePages.Select(page => page.Name).ToArray();
            _tabLanguage = language;
        }

        return _tabTitles;
    }

    private static void DrawPages<T>(List<T> pages, UnityModManager.ModEntry modEntry, ref bool hasPriorPage)
        where T : IMenuPage
    {
        foreach (var page in pages)
        {
            if (hasPriorPage)
            {
                GUILayout.Space(10f);
            }

            page.OnGUI(modEntry);
            hasPriorPage = true;
        }
    }
}
