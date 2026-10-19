using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Displays;

internal static class ProficienciesDisplay
{
    private static bool _displayTabletop;

    private static void DisplayProficienciesGeneral()
    {
        var toggle = Main.Settings.DisplayProficienciesGeneralToggle;
        if (UI.DisclosureToggle(Gui.Localize("ModUi/&General"), ref toggle))
        {
            Main.Settings.DisplayProficienciesGeneralToggle = toggle;
        }

        if (!Main.Settings.DisplayProficienciesGeneralToggle)
        {
            return;
        }

        UI.Label();

        toggle = Main.Settings.DisableLevelPrerequisitesOnModFeats;
        if (UI.Toggle(Gui.Localize("ModUi/&DisableClassPrerequisitesOnModFeats"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DisableLevelPrerequisitesOnModFeats = toggle;
        }

        toggle = Main.Settings.DisableRacePrerequisitesOnModFeats;
        if (UI.Toggle(Gui.Localize("ModUi/&DisableRacePrerequisitesOnModFeats"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DisableRacePrerequisitesOnModFeats = toggle;
        }

        toggle = Main.Settings.DisableCastSpellPreRequisitesOnModFeats;
        if (UI.Toggle(Gui.Localize("ModUi/&DisableCastSpellPreRequisitesOnModFeats"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DisableCastSpellPreRequisitesOnModFeats = toggle;
        }

        UI.Label();


        var intValue = Main.Settings.TotalFeatsGrantedFirstLevel;
        if (UI.Slider(Gui.Localize("ModUi/&TotalFeatsGrantedFirstLevel"), ref intValue,
                FeatsContext.MinInitialFeats, FeatsContext.MaxInitialFeats, 0, "",
                UI.AutoWidth()))
        {
            Main.Settings.TotalFeatsGrantedFirstLevel = intValue;
            FeatsContext.SwitchFirstLevelTotalFeats();
        }

        UI.Label();

        toggle = Main.Settings.EnablesAsiAndFeat;
        if (UI.Toggle(Gui.Localize("ModUi/&EnablesAsiAndFeat"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnablesAsiAndFeat = toggle;
            FeatsContext.SwitchAsiAndFeat();
        }

        toggle = Main.Settings.EnableTabletopFeatRules2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableTabletopFeatRules2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableTabletopFeatRules2024 = toggle;
            Tabletop2024Context.SwitchTabletopFeatRules2024();
        }

        if (Main.Settings.EnableTabletopFeatRules2024)
        {
            UI.Label(Gui.Localize("ModUi/&TabletopFeatRules2024Help"));
        }

        using (UI.HorizontalScope())
        {
            UI.Space(25f);
            using (UI.VerticalScope(UI.MinWidth(0f), UI.ExpandWidth(true)))
            {
                var guiEnabled = GUI.enabled;
                toggle = Main.Settings.EnableEpicBoons2024;
                try
                {
                    GUI.enabled = guiEnabled && Main.Settings.EnableTabletopFeatRules2024;
                    if (UI.Toggle(Gui.Localize("ModUi/&EnableEpicBoons2024"), ref toggle, UI.AutoWidth()))
                    {
                        Main.Settings.EnableEpicBoons2024 = toggle;
                        Tabletop2024Context.SwitchTabletopFeatRules2024();
                    }
                }
                finally
                {
                    GUI.enabled = guiEnabled;
                }

                UI.Label(Gui.Localize(Main.Settings.EnableTabletopFeatRules2024
                        ? "ModUi/&EpicBoons2024Help"
                        : "ModUi/&EpicBoons2024RequiresTabletopHelp"),
                    new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.AutoWidth());
            }
        }

        toggle = Main.Settings.EnableFeatsAtEveryFourLevels;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableFeatsAtEvenLevels"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableFeatsAtEveryFourLevels = toggle;
            FeatsContext.SwitchEveryFourLevelsFeats();
        }

        toggle = Main.Settings.EnableFeatsAtEveryFourLevelsMiddle;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableFeatsAtEvenLevelsMiddle"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableFeatsAtEveryFourLevelsMiddle = toggle;
            FeatsContext.SwitchEveryFourLevelsFeats(true);
        }

        UI.Label();

        toggle = Main.Settings.AccountForAllDiceOnFollowUpStrike;
        if (UI.Toggle(Gui.Localize("ModUi/&AccountForAllDiceOnFollowUpStrike"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AccountForAllDiceOnFollowUpStrike = toggle;
        }

        toggle = Main.Settings.AllowCantripsTriggeringOnWarMagic;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableCantripsTriggeringOnWarMagic"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AllowCantripsTriggeringOnWarMagic = toggle;
        }
    }

    internal static void DisplayProficiencies()
    {
        UI.Label();

        OtherHeaders();

        var displayToggle = Main.Settings.DisplayFeatGroupsToggle;
        var sliderPos = Main.Settings.FeatGroupSliderPosition;
        var isFeatGroupTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&FeatGroups"),
            FeatsContext.SwitchFeatGroup,
            FeatsContext.FeatGroups,
            Main.Settings.FeatGroupEnabled,
            ref displayToggle,
            ref sliderPos,
            headerRendering: () =>
            {
                UI.Label(Gui.Localize("ModUi/&FeatGroupsHelp"));
                UI.Label();
            },
            displaySelectTabletop: false,
            toggleEnabled: Tabletop2024Context.IsFeatToggleAvailableInCurrentMode,
            toggleValueOverride: Tabletop2024Context.GetFeatToggleValueOverrideInCurrentMode);
        Main.Settings.DisplayFeatGroupsToggle = displayToggle;
        Main.Settings.FeatGroupSliderPosition = sliderPos;

        displayToggle = Main.Settings.DisplayFeatsToggle;
        sliderPos = Main.Settings.FeatSliderPosition;
        System.Action featHeaderRendering = null;

        if (Main.Settings.EnableTabletopFeatRules2024)
        {
            featHeaderRendering = () =>
            {
                UI.Label(Gui.Localize("ModUi/&TabletopFeatRules2024ListHelp"));
                UI.Label();
            };
        }

        var isFeatTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&Feats"),
            FeatsContext.SwitchFeat,
            FeatsContext.Feats,
            Main.Settings.FeatEnabled,
            ref displayToggle,
            ref sliderPos,
            headerRendering: featHeaderRendering,
            toggleEnabled: Tabletop2024Context.IsFeatToggleAvailableInCurrentMode,
            toggleValueOverride: Tabletop2024Context.GetFeatToggleValueOverrideInCurrentMode);
        Main.Settings.DisplayFeatsToggle = displayToggle;
        Main.Settings.FeatSliderPosition = sliderPos;

        displayToggle = Main.Settings.DisplayFightingStylesToggle;
        sliderPos = Main.Settings.FightingStyleSliderPosition;
        var isFightingStyleTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&FightingStyles"),
            FightingStyleContext.Switch,
            FightingStyleContext.FightingStyles,
            Main.Settings.FightingStyleEnabled,
            ref displayToggle,
            ref sliderPos);
        Main.Settings.DisplayFightingStylesToggle = displayToggle;
        Main.Settings.FightingStyleSliderPosition = sliderPos;

        displayToggle = Main.Settings.DisplayInvocationsToggle;
        sliderPos = Main.Settings.InvocationSliderPosition;
        var isInvocationTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&Invocations"),
            InvocationsContext.SwitchInvocation,
            InvocationsContext.Invocations,
            Main.Settings.InvocationEnabled,
            ref displayToggle,
            ref sliderPos);
        Main.Settings.DisplayInvocationsToggle = displayToggle;
        Main.Settings.InvocationSliderPosition = sliderPos;

        displayToggle = Main.Settings.DisplayMetamagicToggle;
        sliderPos = Main.Settings.MetamagicSliderPosition;
        var isMetamagicTabletop = ModUi.DisplayDefinitions(
            Gui.Localize("ModUi/&Metamagic"),
            MetamagicContext.SwitchMetamagic,
            MetamagicContext.Metamagic,
            Main.Settings.MetamagicEnabled,
            ref displayToggle,
            ref sliderPos);
        Main.Settings.DisplayMetamagicToggle = displayToggle;
        Main.Settings.MetamagicSliderPosition = sliderPos;

        _displayTabletop = isFeatGroupTabletop && isFeatTabletop && isFightingStyleTabletop &&
                           isInvocationTabletop && isMetamagicTabletop;

        UI.Label();
    }

    private static void OtherHeaders()
    {
        ModUi.DisplayDocumentationLinks(
            ("ModUi/&DocsFeats", "Feats.md"),
            ("ModUi/&DocsFightingStyles", "FightingStyles.md"),
            ("ModUi/&DocsInvocations", "Invocations.md"),
            ("ModUi/&DocsMetamagic", "Metamagic.md"),
            ("ModUi/&DocsArcaneShots", "ArcaneShots.md"),
            ("ModUi/&DocsInfusions", "Infusions.md"),
            ("ModUi/&DocsManeuvers", "Maneuvers.md"),
            ("ModUi/&DocsVersatilities", "Versatilities.md"));

        UI.Label();

        DisplayProficienciesGeneral();

        UI.Label();

        var expandAllTitle = Gui.Localize("ModUi/&ExpandAll");
        var selectAllTitle = Gui.Localize("ModUi/&SelectAll");
        var tabletopTitle = Gui.Localize("ModUi/&SelectTabletop");
        NamedAction[] bulkActions =
        [
            new(expandAllTitle, () =>
            {
                var toggle =
                    Main.Settings.DisplayFeatsToggle &&
                    Main.Settings.DisplayFeatGroupsToggle &&
                    Main.Settings.DisplayFightingStylesToggle &&
                    Main.Settings.DisplayInvocationsToggle &&
                    Main.Settings.DisplayMetamagicToggle;

                if (UI.Toggle(expandAllTitle, ref toggle, UI.AutoWidth()))
                {
                    Main.Settings.DisplayFeatsToggle = toggle;
                    Main.Settings.DisplayFeatGroupsToggle = toggle;
                    Main.Settings.DisplayFightingStylesToggle = toggle;
                    Main.Settings.DisplayInvocationsToggle = toggle;
                    Main.Settings.DisplayMetamagicToggle = toggle;
                }
            }),
            new(selectAllTitle, () =>
            {
                var toggle =
                    ModUi.IsSelectionComplete(FeatsContext.Feats, Main.Settings.FeatEnabled,
                        Tabletop2024Context.IsFeatToggleAvailableInCurrentMode) &&
                    ModUi.IsSelectionComplete(FeatsContext.FeatGroups, Main.Settings.FeatGroupEnabled,
                        Tabletop2024Context.IsFeatToggleAvailableInCurrentMode) &&
                    ModUi.IsSelectionComplete(FightingStyleContext.FightingStyles, Main.Settings.FightingStyleEnabled) &&
                    ModUi.IsSelectionComplete(InvocationsContext.Invocations, Main.Settings.InvocationEnabled) &&
                    ModUi.IsSelectionComplete(MetamagicContext.Metamagic, Main.Settings.MetamagicEnabled);

                if (UI.Toggle(selectAllTitle, ref toggle, UI.AutoWidth()))
                {
                    foreach (var feat in FeatsContext.Feats)
                    {
                        if (!Tabletop2024Context.IsFeatToggleAvailableInCurrentMode(feat))
                        {
                            continue;
                        }

                        FeatsContext.SwitchFeat(feat, toggle);
                    }

                    foreach (var featGroup in FeatsContext.FeatGroups)
                    {
                        if (!Tabletop2024Context.IsFeatToggleAvailableInCurrentMode(featGroup))
                        {
                            continue;
                        }

                        FeatsContext.SwitchFeatGroup(featGroup, toggle);
                    }

                    foreach (var fightingStyles in FightingStyleContext.FightingStyles)
                    {
                        FightingStyleContext.Switch(fightingStyles, toggle);
                    }

                    foreach (var invocation in InvocationsContext.Invocations)
                    {
                        InvocationsContext.SwitchInvocation(invocation, toggle);
                    }

                    foreach (var metamagic in MetamagicContext.Metamagic)
                    {
                        MetamagicContext.SwitchMetamagic(metamagic, toggle);
                    }
                }
            }),
            new(tabletopTitle, () =>
            {
                var toggle = _displayTabletop;
                if (UI.Toggle(tabletopTitle, ref toggle, UI.AutoWidth()))
                {
                    ModUi.ApplyTabletopSelection(FeatsContext.Feats, FeatsContext.SwitchFeat, toggle,
                        Tabletop2024Context.IsFeatToggleAvailableInCurrentMode);
                    ModUi.ApplyTabletopSelection(FeatsContext.FeatGroups, FeatsContext.SwitchFeatGroup, toggle,
                        Tabletop2024Context.IsFeatToggleAvailableInCurrentMode);
                    ModUi.ApplyTabletopSelection(FightingStyleContext.FightingStyles, FightingStyleContext.Switch,
                        toggle);
                    ModUi.ApplyTabletopSelection(InvocationsContext.Invocations, InvocationsContext.SwitchInvocation,
                        toggle);
                    ModUi.ApplyTabletopSelection(MetamagicContext.Metamagic, MetamagicContext.SwitchMetamagic, toggle);
                }
            })
        ];
        UI.HStack(null, bulkActions.Length, GUI.skin.label,
            bulkActions.Select(action => UI.ToggleWidth(action.Name)).ToArray(), false, bulkActions);

        UI.Div();
    }
}
