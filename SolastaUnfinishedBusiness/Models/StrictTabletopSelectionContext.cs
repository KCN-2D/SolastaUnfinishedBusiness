using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Classes;

namespace SolastaUnfinishedBusiness.Models;

internal static class StrictTabletopSelectionContext
{
    private static readonly HashSet<string> AllowedClassNames =
    [
        "Barbarian",
        "Bard",
        "Cleric",
        "Druid",
        "Fighter",
        "Monk",
        "Paladin",
        "Ranger",
        "Rogue",
        "Sorcerer",
        "Warlock",
        "Wizard",
        InventorClass.ClassName
    ];

    internal static bool IsEnabled => Main.Settings.EnableStrictTabletopClassSelection;

    internal static bool IsTabletopClassAllowed(CharacterClassDefinition klass)
    {
        return klass != null && AllowedClassNames.Contains(klass.Name);
    }

    internal static bool IsTabletopSubclassAllowed(CharacterSubclassDefinition subclass)
    {
        return subclass != null && IsTabletopSubclassNameAllowed(subclass.Name);
    }

    internal static bool IsTabletopSubclassNameAllowed(string name)
    {
        return !string.IsNullOrEmpty(name) && Displays.ModUi.TabletopDefinitionNames.Contains(name);
    }

    internal static bool IsClassAllowedForCurrentMode(CharacterClassDefinition klass)
    {
        return klass != null && (!IsEnabled || IsTabletopClassAllowed(klass));
    }

    internal static bool IsSubclassAllowedForCurrentMode(CharacterSubclassDefinition subclass)
    {
        return subclass != null && (!IsEnabled || IsTabletopSubclassAllowed(subclass));
    }

    internal static bool IsSubclassNameAllowedForCurrentMode(string name)
    {
        return !string.IsNullOrEmpty(name) && (!IsEnabled || IsTabletopSubclassNameAllowed(name));
    }

    internal static List<string> FilterSubclassNamesForCurrentMode(IEnumerable<string> subclassNames)
    {
        return subclassNames?.Where(IsSubclassNameAllowedForCurrentMode).ToList() ?? [];
    }

    internal static void FilterAndPreserveSelection<T>(
        List<T> items,
        ref int selectedIndex,
        Predicate<T> keepPredicate)
        where T : class
    {
        var selectedItem = selectedIndex >= 0 && selectedIndex < items.Count
            ? items[selectedIndex]
            : null;

        items.RemoveAll(item => item == null || !keepPredicate(item));

        if (items.Count == 0)
        {
            selectedIndex = -1;

            return;
        }

        selectedIndex = selectedItem != null ? items.IndexOf(selectedItem) : selectedIndex;

        if (selectedIndex < 0 || selectedIndex >= items.Count)
        {
            selectedIndex = 0;
        }
    }
}
