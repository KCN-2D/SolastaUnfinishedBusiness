using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.Displays;
using SolastaUnfinishedBusiness.Subclasses;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSubclassChoices;

namespace SolastaUnfinishedBusiness.Models;

internal static class SubclassesContext
{
    internal static readonly SortedList<string, (string, CharacterClassDefinition)> Klasses = [];

    internal static readonly Dictionary<CharacterClassDefinition, KlassListContext> KlassListContextTab = [];

    private static Dictionary<CharacterSubclassDefinition, DeityDefinition> DeityChoiceList
    {
        get;
    } = [];

    private static Dictionary<CharacterSubclassDefinition, FeatureDefinitionSubclassChoice> SubclassesChoiceList
    {
        get;
    } = [];

    private static readonly HashSet<string> StrictClericDomainChoiceSubclasses = [];

    internal static void Load()
    {
        // kept for backward compatibility
        var wayOfTheWealAndWoe = new WayOfTheWealAndWoe();

        wayOfTheWealAndWoe.Subclass.GuiPresentation.hidden = true;

        RegisterClassesContext();

        foreach (var abstractSubClassInstance in typeof(AbstractSubclass)
                     .Assembly.GetTypes()
                     .Where(t => t.IsSubclassOf(typeof(AbstractSubclass)) && !t.IsAbstract)
                     .Select(t => (AbstractSubclass)Activator.CreateInstance(t)))
        {
            LoadSubclass(abstractSubClassInstance);
        }

        // settings paring
        var registeredSubclassNames = KlassListContextTab
            .SelectMany(x => x.Value.AllSubClasses)
            .Select(subclass => subclass.Name)
            .ToHashSet();

        foreach (var kvp in Main.Settings.KlassListSubclassEnabled)
        {
            kvp.Value.RemoveAll(name => !registeredSubclassNames.Contains(name));
        }

        DatabaseRepository.GetDatabase<CharacterSubclassDefinition>()
            .Do(x => x.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock));

        RefreshSubclassVisibility();

        // bootstrap
        SwitchSchoolRestrictionsFromShadowCaster();
        SwitchSchoolRestrictionsFromSpellBlade();
    }

    internal static void LateLoad()
    {
        CircleOfTheLife.LateLoad();
        CollegeOfLife.LateLoad();
        RangerSurvivalist.LateLoad();
        SorcerousFieldManipulator.LateLoad();
        WizardAbjuration.LateLoad();
        WizardDeadMaster.LateLoad();
        WizardEvocation.LateLoad();
    }

    private static void RegisterClassesContext()
    {
        foreach (var klass in DatabaseRepository.GetDatabase<CharacterClassDefinition>())
        {
            var klassName = klass.Name;
            var postfix = klassName == InventorClass.ClassName ? " \u00a9".Grey() : string.Empty;

            Klasses.Add(klass.FormatTitle() + postfix, (klassName, klass));
            KlassListContextTab.Add(klass, new KlassListContext(klass));
            Main.Settings.DisplayKlassToggle.TryAdd(klassName, false);
            Main.Settings.KlassListSliderPosition.TryAdd(klassName, 4);
            Main.Settings.KlassListSubclassEnabled.TryAdd(klassName, []);
        }
    }

    private static void LoadSubclass([NotNull] AbstractSubclass subclassBuilder)
    {
        var klass = subclassBuilder.Klass;
        var subclass = subclassBuilder.Subclass;

        if (subclassBuilder.SubclassChoice && !subclassBuilder.DeityDefinition)
        {
            SubclassesChoiceList.Add(subclass, subclassBuilder.SubclassChoice);
        }
        else if (!subclassBuilder.SubclassChoice && subclassBuilder.DeityDefinition)
        {
            DeityChoiceList.Add(subclass, subclassBuilder.DeityDefinition);
        }

        KlassListContextTab[klass].RegisterSubclass(subclass);
    }

    internal static bool IsAllSetSelected()
    {
        return KlassListContextTab.Values.All(subclassListContext => subclassListContext.IsAllSetSelected);
    }

    internal static bool IsTabletopSetSelected()
    {
        return KlassListContextTab.Values.All(subclassListContext => subclassListContext.IsTabletopSetSelected);
    }

    internal static void RefreshSubclassVisibility()
    {
        foreach (var subclassListContext in KlassListContextTab.Values)
        {
            subclassListContext.RefreshSubclassVisibilityInternal();
        }

        RefreshStrictClericDomainChoice();
    }

    internal static void SelectAllSet(bool toggle)
    {
        foreach (var subclassListContext in KlassListContextTab.Values)
        {
            subclassListContext.SelectAllSetInternal(toggle);
        }
    }

    internal static void SelectTabletopSet(bool toggle)
    {
        foreach (var subclassListContext in KlassListContextTab.Values)
        {
            subclassListContext.SelectTabletopSetInternal(toggle);
        }
    }

    private static bool IsClericDomainName(string subclassName)
    {
        return !string.IsNullOrEmpty(subclassName) && subclassName.StartsWith("Domain", StringComparison.Ordinal);
    }

    private static void RefreshStrictClericDomainChoice()
    {
        SubclassChoiceClericDivineDomains.filterByDeity = !StrictTabletopSelectionContext.IsEnabled;

        foreach (var subclassName in StrictClericDomainChoiceSubclasses)
        {
            SubclassChoiceClericDivineDomains.Subclasses.Remove(subclassName);
        }

        StrictClericDomainChoiceSubclasses.Clear();

        if (!StrictTabletopSelectionContext.IsEnabled)
        {
            return;
        }

        foreach (var subclassName in DatabaseRepository
                     .GetDatabase<DeityDefinition>()
                     .SelectMany(deity => deity.Subclasses)
                     .Where(IsClericDomainName)
                     .Where(StrictTabletopSelectionContext.IsSubclassNameAllowedForCurrentMode)
                     .Distinct())
        {
            if (SubclassChoiceClericDivineDomains.Subclasses.Contains(subclassName))
            {
                continue;
            }

            SubclassChoiceClericDivineDomains.Subclasses.Add(subclassName);
            StrictClericDomainChoiceSubclasses.Add(subclassName);
        }
    }

    internal static void SwitchSchoolRestrictionsFromShadowCaster()
    {
        if (Main.Settings.RemoveSchoolRestrictionsFromShadowCaster)
        {
            FeatureDefinitionCastSpells.CastSpellShadowcaster.RestrictedSchools.Clear();
        }
        else
        {
            FeatureDefinitionCastSpells.CastSpellShadowcaster.RestrictedSchools.SetRange(
                SchoolAbjuration,
                SchoolDivination,
                SchoolIllusion,
                SchoolNecromancy);
        }
    }

    internal static void SwitchSchoolRestrictionsFromSpellBlade()
    {
        if (Main.Settings.RemoveSchoolRestrictionsFromSpellBlade)
        {
            FeatureDefinitionCastSpells.CastSpellMartialSpellBlade.RestrictedSchools.Clear();
        }
        else
        {
            FeatureDefinitionCastSpells.CastSpellMartialSpellBlade.RestrictedSchools.SetRange(
                SchoolConjuration,
                //RuleDefinitions has wrong constant for Enchantment school
                SchoolOfMagicDefinitions.SchoolEnchantment.Name,
                SchoolEvocation,
                SchoolTransmutation);
        }
    }

    internal sealed class KlassListContext
    {
        internal KlassListContext(CharacterClassDefinition characterClassDefinition)
        {
            Klass = characterClassDefinition;
            AllSubClasses = [];
        }

        private List<string> SelectedSubclasses => Main.Settings.KlassListSubclassEnabled[Klass.Name];
        private CharacterClassDefinition Klass { get; }
        internal HashSet<CharacterSubclassDefinition> AllSubClasses { get; }
        private IEnumerable<CharacterSubclassDefinition> TabletopSubclasses =>
            AllSubClasses.Where(StrictTabletopSelectionContext.IsTabletopSubclassAllowed);
        private IEnumerable<CharacterSubclassDefinition> NonTabletopSubclasses =>
            AllSubClasses.Where(subclass => !StrictTabletopSelectionContext.IsTabletopSubclassAllowed(subclass));
        private IEnumerable<CharacterSubclassDefinition> AvailableSubclasses =>
            AllSubClasses.Where(IsSubclassAvailable);

        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool IsAllSetSelected =>
            AvailableSubclasses.All(IsSubclassEffectivelyEnabled);

        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool IsTabletopSetSelected =>
            TabletopSubclasses.All(IsSubclassEffectivelyEnabled) &&
            NonTabletopSubclasses.All(subclass => !IsSubclassEffectivelyEnabled(subclass));

        internal void SelectAllSetInternal(bool toggle)
        {
            foreach (var subclass in AllSubClasses)
            {
                Switch(subclass, toggle);
            }
        }

        internal void SelectTabletopSetInternal(bool toggle)
        {
            foreach (var subclass in AllSubClasses)
            {
                Switch(subclass, toggle && StrictTabletopSelectionContext.IsTabletopSubclassAllowed(subclass));
            }
        }

        internal void RegisterSubclass(CharacterSubclassDefinition characterSubclassDefinition)
        {
            AllSubClasses.Add(characterSubclassDefinition);
            UpdateSubclassVisibility(characterSubclassDefinition);
            UpdateClassVisibility();
        }

        internal void Switch(CharacterSubclassDefinition characterSubclassDefinition, bool active)
        {
            var klass = Klass.Name;
            var subclass = characterSubclassDefinition.Name;
            var subclassAllowed = IsSubclassAvailable(characterSubclassDefinition);

            if (!subclassAllowed)
            {
                UpdateSubclassVisibility(characterSubclassDefinition);
                UpdateClassVisibility();

                return;
            }

            if (active)
            {
                Main.Settings.KlassListSubclassEnabled[klass].TryAdd(subclass);
            }
            else
            {
                Main.Settings.KlassListSubclassEnabled[klass].Remove(subclass);
            }

            UpdateSubclassVisibility(characterSubclassDefinition);
            UpdateClassVisibility();
        }

        internal void RefreshSubclassVisibilityInternal()
        {
            foreach (var subclass in AllSubClasses)
            {
                UpdateSubclassVisibility(subclass);
            }

            UpdateClassVisibility();
        }

        private void UpdateSubclassVisibility([NotNull] CharacterSubclassDefinition characterSubclassDefinition)
        {
            var subclass = characterSubclassDefinition.Name;
            var isActive = IsSubclassEffectivelyEnabled(characterSubclassDefinition);

            if (SubclassesChoiceList.TryGetValue(characterSubclassDefinition, out var choiceList))
            {
                if (isActive)
                {
                    choiceList.Subclasses.TryAdd(subclass);
                }
                else
                {
                    choiceList.Subclasses.Remove(subclass);
                }
            }
            else if (DeityChoiceList.TryGetValue(characterSubclassDefinition, out var deityDefinition))
            {
                if (isActive)
                {
                    deityDefinition.Subclasses.TryAdd(subclass);
                }
                else
                {
                    deityDefinition.Subclasses.Remove(subclass);
                }
            }
        }

        private bool IsSubclassAvailable(CharacterSubclassDefinition characterSubclassDefinition)
        {
            return StrictTabletopSelectionContext.IsSubclassAllowedForCurrentMode(characterSubclassDefinition);
        }

        private bool IsSubclassEffectivelyEnabled(CharacterSubclassDefinition characterSubclassDefinition)
        {
            return SelectedSubclasses.Contains(characterSubclassDefinition.Name) &&
                   IsSubclassAvailable(characterSubclassDefinition);
        }

        private void UpdateClassVisibility()
        {
            if (Klass == InventorClass.Class)
            {
                InventorClass.Class.GuiPresentation.hidden = !AllSubClasses.Any(IsSubclassEffectivelyEnabled);
            }
        }
    }
}
