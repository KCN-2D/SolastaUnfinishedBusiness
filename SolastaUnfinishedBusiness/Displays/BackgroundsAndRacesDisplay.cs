using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Displays;

internal static class BackgroundsAndRacesDisplay
{
    private static bool _displayTabletop;

    private static void DisplayBackgroundsAndRacesGeneral()
    {
        var toggle = Main.Settings.DisplayBackgroundsAndRacesGeneralToggle;
        if (UI.DisclosureToggle(Gui.Localize("ModUi/&General"), ref toggle, 200))
        {
            Main.Settings.DisplayBackgroundsAndRacesGeneralToggle = toggle;
        }

        if (!Main.Settings.DisplayBackgroundsAndRacesGeneralToggle)
        {
            return;
        }

        UI.Label();

        toggle = Main.Settings.EnableFlexibleBackgrounds;
        if (DisplayDependentBackgroundOption(
                "ModUi/&EnableFlexibleBackgrounds", ref toggle,
                !Tabletop2024Context.IsBackgroundBonusFeatsEnabled(),
                "ModUi/&FlexibleBackgroundsDisabledByBackgroundBonusFeats"))
        {
            Main.Settings.EnableFlexibleBackgrounds = toggle;
            Tabletop2024Context.ApplyBackgroundOptions();
        }

        toggle = Main.Settings.EnableFlexibleRaces;
        if (DisplayDependentBackgroundOption(
                "ModUi/&EnableFlexibleRaces", ref toggle,
                !Main.Settings.EnableBackgroundASI,
                "ModUi/&FlexibleRacesDisabledByBackgroundASI"))
        {
            Main.Settings.EnableFlexibleRaces = toggle;
            Tabletop2024Context.ApplyBackgroundOptions();
        }

        toggle = Main.Settings.EnableBackgroundASI;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableBackgroundAbilityScoreIncreases"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableBackgroundASI = toggle;
            Tabletop2024Context.ApplyBackgroundOptions();
        }

        toggle = Main.Settings.EnableBackgroundBonusFeats;
        if (DisplayDependentBackgroundOption(
                "ModUi/&EnableBackgroundBonusFeats", ref toggle, Main.Settings.EnableBackgroundASI))
        {
            Main.Settings.EnableBackgroundBonusFeats = toggle;
            Tabletop2024Context.ApplyBackgroundOptions();
        }

        UI.Label();

        toggle = Main.Settings.ChangeDragonbornElementalBreathUsages;
        if (UI.Toggle(Gui.Localize("ModUi/&ChangeDragonbornElementalBreathUsages"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ChangeDragonbornElementalBreathUsages = toggle;
            RacesContext.SwitchDragonbornElementalBreathUsages();
        }

        DisplayAlternateHumanOption();

        toggle = Main.Settings.UseOfficialSmallRacesDisWithHeavyWeapons;
        if (UI.Toggle(Gui.Localize("ModUi/&UseOfficialSmallRacesDisWithHeavyWeapons"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.UseOfficialSmallRacesDisWithHeavyWeapons = toggle;
        }

        UI.Label();

        toggle = Main.Settings.DisableSenseDarkVisionFromAllRaces;
        if (UI.Toggle(Gui.Localize("ModUi/&DisableSenseDarkVisionFromAllRaces"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DisableSenseDarkVisionFromAllRaces = toggle;
        }

        toggle = Main.Settings.DisableSenseSuperiorDarkVisionFromAllRaces;
        if (UI.Toggle(Gui.Localize("ModUi/&DisableSenseSuperiorDarkVisionFromAllRaces"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DisableSenseSuperiorDarkVisionFromAllRaces = toggle;
        }

        UI.Label();

        toggle = Main.Settings.AddDarknessPerceptiveToDarkRaces;
        if (UI.Toggle(Gui.Localize("ModUi/&AddDarknessPerceptiveToDarkRaces"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AddDarknessPerceptiveToDarkRaces = toggle;
            RacesContext.SwitchDarknessPerceptive();
        }

        UI.Label();

        toggle = Main.Settings.RaceLightSensitivityApplyOutdoorsOnly;
        if (UI.Toggle(Gui.Localize("ModUi/&RaceLightSensitivityApplyOutdoorsOnly"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RaceLightSensitivityApplyOutdoorsOnly = toggle;
        }

        UI.Label();
    }

    internal static void DisplayAlternateHumanOption()
    {
        var toggle = Main.Settings.EnableAlternateHuman;

        if (DisplayDependentBackgroundOption(
                "ModUi/&EnableAlternateHuman", ref toggle, !Main.Settings.EnableBackgroundASI,
                "ModUi/&EnableAlternateHumanForcedByBackgroundASI"))
        {
            Main.Settings.EnableAlternateHuman = toggle;
            Tabletop2024Context.ApplyBackgroundOptions();
        }
    }

    private static bool DisplayDependentBackgroundOption(
        string titleKey, ref bool toggle, bool available, string dependencyKey = null)
    {
        var guiEnabled = GUI.enabled;
        bool changed;

        try
        {
            GUI.enabled = guiEnabled && available;
            changed = UI.Toggle(Gui.Localize(titleKey), ref toggle, UI.AutoWidth());
        }
        finally
        {
            GUI.enabled = guiEnabled;
        }

        if (!available && dependencyKey != null)
        {
            UI.Label(Gui.Localize(dependencyKey), UI.AutoWidth());
        }

        return changed;
    }

    internal static void DisplayBackgroundsAndRaces()
    {
        UI.Label();

        using (UI.HorizontalScope())
        {
            UI.ActionButton(Gui.Localize("ModUi/&DocsBackgrounds").Bold().Khaki(),
                () => UpdateContext.OpenDocumentation("Backgrounds.md"), UI.Width(189f));
            UI.ActionButton(Gui.Localize("ModUi/&DocsRaces").Bold().Khaki(),
                () => UpdateContext.OpenDocumentation("Races.md"), UI.Width(189f));
            UI.ActionButton(Gui.Localize("ModUi/&DocsSubraces").Bold().Khaki(),
                () => UpdateContext.OpenDocumentation("Subraces.md"), UI.Width(189f));
        }

        UI.Label();
        DisplayBackgroundsAndRacesGeneral();
        UI.Label();

        using (UI.HorizontalScope())
        {
            var toggle =
                Main.Settings.DisplayBackgroundsToggle &&
                Main.Settings.DisplayRacesToggle &&
                Main.Settings.DisplaySubracesToggle;

            if (UI.Toggle(Gui.Localize("ModUi/&ExpandAll"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
            {
                Main.Settings.DisplayBackgroundsToggle = toggle;
                Main.Settings.DisplayRacesToggle = toggle;
                Main.Settings.DisplaySubracesToggle = toggle;
            }

            toggle =
                BackgroundsContext.Backgrounds.Count == Main.Settings.BackgroundEnabled.Count &&
                RacesContext.Races.Count == Main.Settings.RaceEnabled.Count &&
                RacesContext.Subraces.Count == Main.Settings.SubraceEnabled.Count;

            if (UI.Toggle(Gui.Localize("ModUi/&SelectAll"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
            {
                foreach (var background in BackgroundsContext.Backgrounds)
                {
                    BackgroundsContext.Switch(background, toggle);
                }

                foreach (var race in RacesContext.Races)
                {
                    RacesContext.Switch(race, toggle);
                }

                foreach (var subrace in RacesContext.Subraces)
                {
                    RacesContext.SwitchSubrace(subrace, toggle);
                }
            }

            toggle = _displayTabletop;
            if (UI.Toggle(Gui.Localize("ModUi/&SelectTabletop"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
            {
                foreach (var background in BackgroundsContext.Backgrounds)
                {
                    BackgroundsContext.Switch(background, toggle && ModUi.IsTabletopDefinition(background));
                }

                foreach (var race in RacesContext.Races)
                {
                    RacesContext.Switch(race, toggle && ModUi.IsTabletopDefinition(race));
                }

                foreach (var subrace in RacesContext.Subraces)
                {
                    RacesContext.SwitchSubrace(subrace, toggle && ModUi.IsTabletopDefinition(subrace));
                }
            }
        }

        UI.Div();

        var displayToggle = Main.Settings.DisplayBackgroundsToggle;
        var sliderPos = Main.Settings.BackgroundSliderPosition;
        var isBackgroundTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&Backgrounds"),
            BackgroundsContext.Switch,
            BackgroundsContext.Backgrounds,
            Main.Settings.BackgroundEnabled,
            ref displayToggle,
            ref sliderPos);
        Main.Settings.DisplayBackgroundsToggle = displayToggle;
        Main.Settings.BackgroundSliderPosition = sliderPos;

        displayToggle = Main.Settings.DisplayRacesToggle;
        sliderPos = Main.Settings.RaceSliderPosition;
        var isRaceTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&Races"),
            RacesContext.Switch,
            RacesContext.Races,
            Main.Settings.RaceEnabled,
            ref displayToggle,
            ref sliderPos);
        Main.Settings.DisplayRacesToggle = displayToggle;
        Main.Settings.RaceSliderPosition = sliderPos;

        displayToggle = Main.Settings.DisplaySubracesToggle;
        sliderPos = Main.Settings.SubraceSliderPosition;
        var isSubraceTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&Subraces"),
            RacesContext.SwitchSubrace,
            RacesContext.Subraces,
            Main.Settings.SubraceEnabled,
            ref displayToggle,
            ref sliderPos);
        Main.Settings.DisplaySubracesToggle = displayToggle;
        Main.Settings.SubraceSliderPosition = sliderPos;

        _displayTabletop = isBackgroundTabletop && isRaceTabletop && isSubraceTabletop;

        UI.Label();
    }
}
