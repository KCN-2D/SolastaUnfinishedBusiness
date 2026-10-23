using System;
using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Displays;

internal static class SubclassesDisplay
{
    private static void DisplaySubclassesGeneral()
    {
        var toggle = Main.Settings.DisplaySubClassesGeneralToggle;
        if (UI.DisclosureToggle(Gui.Localize("ModUi/&General"), ref toggle))
        {
            Main.Settings.DisplaySubClassesGeneralToggle = toggle;
        }

        if (!Main.Settings.DisplaySubClassesGeneralToggle)
        {
            return;
        }

        DisplaySubclassRules();
    }

    internal static void DisplaySubclassRules(bool tabletop2024Only = false)
    {
        bool toggle;

        if (!tabletop2024Only)
        {
            DisplayClassHeading("Bard");

            toggle = Main.Settings.EnableBardHealingBalladOnLongRest;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableBardHealingBalladOnLongRest"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableBardHealingBalladOnLongRest = toggle;
                Tabletop2014Context.SwitchBardHealingBalladOnLongRest();
            }
        }

        DisplayClassHeading("Inventor");

        toggle = Main.Settings.EnableArtilleristEldritchCannon2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableArtilleristEldritchCannon2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableArtilleristEldritchCannon2024 = toggle;
            Tabletop2024Context.SwitchArtilleristEldritchCannon();
        }

        toggle = Main.Settings.EnableArtilleristFortifiedPosition2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableArtilleristFortifiedPosition2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableArtilleristFortifiedPosition2024 = toggle;
            Tabletop2024Context.SwitchArtilleristFortifiedPosition();
        }

        DisplayClassHeading("Fighter");

        toggle = Main.Settings.EnableMartialChampion2024;
        if (UI.Toggle(Gui.Localize("ModUi/&SwapMartialChampion"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableMartialChampion2024 = toggle;
            Tabletop2024Context.SwitchMartialChampion();
        }

        if (!tabletop2024Only)
        {
            toggle = Main.Settings.RemoveSchoolRestrictionsFromSpellBlade;
            if (UI.Toggle(Gui.Localize("ModUi/&RemoveSchoolRestrictionsFromSpellBlade"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.RemoveSchoolRestrictionsFromSpellBlade = toggle;
                SubclassesContext.SwitchSchoolRestrictionsFromSpellBlade();
            }
        }

        if (!tabletop2024Only)
        {
            DisplayClassHeading("Monk");

            toggle = Main.Settings.UseWeaponMasteryMonkWayOfBlade;
            var masteryGuiEnabled = GUI.enabled;
            GUI.enabled = masteryGuiEnabled && Main.Settings.UseWeaponMasterySystem;
            if (UI.Toggle(Gui.Localize("ModUi/&UseWeaponMasteryMonkWayOfBlade"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.UseWeaponMasteryMonkWayOfBlade = toggle;
                Tabletop2024Context.SwitchWayOfBladeWeaponMastery();
            }
            GUI.enabled = masteryGuiEnabled;

            if (!Main.Settings.UseWeaponMasterySystem)
            {
                UI.Label(Gui.Localize("ModUi/&WayOfBladeWeaponMasteryRequiresSystemHelp"),
                    new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.ExpandWidth(true));
            }
        }

        if (!tabletop2024Only)
        {
            DisplayClassHeading("Ranger");

            toggle = Main.Settings.AllowAlliesToPerceiveRangerGloomStalkerInNaturalDarkness;
            if (UI.Toggle(Gui.Localize("ModUi/&AllowAlliesToPerceiveRangerGloomStalkerInNaturalDarkness"), ref toggle,
                    UI.AutoWidth()))
            {
                Main.Settings.AllowAlliesToPerceiveRangerGloomStalkerInNaturalDarkness = toggle;
            }
        }

        DisplayClassHeading("Rogue");

        toggle = Main.Settings.EnableAssassinAssassinate2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAssassinAssassinate2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAssassinAssassinate2024 = toggle;
            Tabletop2024Context.SwitchAssassinAssassinate();
        }

        toggle = Main.Settings.EnableAssassinInfiltrationExpertise2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAssassinInfiltrationExpertise2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAssassinInfiltrationExpertise2024 = toggle;
            Tabletop2024Context.SwitchAssassinInfiltrationExpertise();
        }

        toggle = Main.Settings.EnableThiefUseMagicDevice2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableThiefUseMagicDevice2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableThiefUseMagicDevice2024 = toggle;
            Tabletop2024Context.SwitchThiefUseMagicDevice();
        }

        if (!tabletop2024Only)
        {
            toggle = Main.Settings.EnableRogueStrSaving;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableRogueStrSaving"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableRogueStrSaving = toggle;
            }

            toggle = Main.Settings.RemoveSchoolRestrictionsFromShadowCaster;
            if (UI.Toggle(Gui.Localize("ModUi/&RemoveSchoolRestrictionsFromShadowCaster"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.RemoveSchoolRestrictionsFromShadowCaster = toggle;
                SubclassesContext.SwitchSchoolRestrictionsFromShadowCaster();
            }
        }

        DisplayClassHeading("Sorcerer");

        toggle = Main.Settings.EnableSorcererDraconicBloodlineAC2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSorcererDraconicBloodlineAC2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSorcererDraconicBloodlineAC2024 = toggle;
            Tabletop2024Context.SwitchSorcererDraconicBloodlineAC();
        }

        toggle = Main.Settings.EnableSorcererDraconicBloodlineElementalAffinity2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSorcererDraconicBloodlineElementalAffinity2024"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.EnableSorcererDraconicBloodlineElementalAffinity2024 = toggle;
            Tabletop2024Context.SwitchSorcererDraconicBloodlineElementalAffinity();
        }

        toggle = Main.Settings.EnableSorcererDraconicBloodlineResistance2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSorcererDraconicBloodlineResistance2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSorcererDraconicBloodlineResistance2024 = toggle;
            Tabletop2024Context.SwitchSorcererDraconicBloodlineResistance();
        }

        if (!tabletop2024Only)
        {
            toggle = Main.Settings.EnableDivineHeartTabletopFeatures;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableDivineHeartTabletopFeatures"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableDivineHeartTabletopFeatures = toggle;
                SorcerousDivineHeart.SwitchDivineHeartTabletopFeatures();
            }

            if (Main.Settings.EnableDivineHeartTabletopFeatures)
            {
                UI.Label(Gui.Localize("ModUi/&EnableDivineHeartTabletopFeaturesHelp"));
                UI.Label();
            }

            var intValue = Main.Settings.WildSurgeDieRollThreshold;
            if (UI.Slider(Gui.Localize("ModUi/&WildSurgeDieRollThreshold"), ref intValue, 1, 20,
                    2, string.Empty, UI.AutoWidth()))
            {
                Main.Settings.WildSurgeDieRollThreshold = intValue;
                SorcerousWildMagic.SwitchWildSurgeChanceDieThreshold();
            }

            UI.Label();
        }

        DisplayClassHeading("Wizard");

        if (!tabletop2024Only)
        {
            toggle = Main.Settings.EnableBg3AbjurationArcaneWard;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableBG3AbjurationArcaneWard"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableBg3AbjurationArcaneWard = toggle;
                WizardAbjuration.SwapAbjurationBaldurGate3Mode();
            }

            if (Main.Settings.EnableBg3AbjurationArcaneWard)
            {
                UI.Label(Gui.Localize("ModUi/&EnableBG3AbjurationArcaneWardHelp"));
                UI.Label();
            }
        }

        // Show the effective rule without overwriting the user's independent preference.
        toggle = WizardAbjuration.IsSpellBreakerEnabled;
        var wizardGuiEnabled = GUI.enabled;
        GUI.enabled = wizardGuiEnabled && !Main.Settings.EnableOneDndCounterspellSpell;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAbjurerSpellBreaker2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAbjurerSpellBreaker2024 = toggle;
            Tabletop2024Context.SwitchWizardAbjurerSpellBreaker();
        }
        GUI.enabled = wizardGuiEnabled;

        if (Main.Settings.EnableOneDndCounterspellSpell)
        {
            UI.Label(Gui.Localize("ModUi/&AbjurerSpellBreakerForcedByCounterspell2024"),
                new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.ExpandWidth(true));
        }

        toggle = Main.Settings.SwapAbjurationSavant;
        if (UI.Toggle(Gui.Localize("ModUi/&SwapAbjurationSavant"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.SwapAbjurationSavant = toggle;
            WizardAbjuration.SwapSavantAndSavant2024();
        }

        toggle = Main.Settings.SwapEvocationSavant;
        if (UI.Toggle(Gui.Localize("ModUi/&SwapEvocationSavant"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.SwapEvocationSavant = toggle;
            WizardEvocation.SwapSavantAndSavant2024();
        }

        toggle = Main.Settings.SwapEvocationPotentCantripAndSculptSpell;
        if (UI.Toggle(Gui.Localize("ModUi/&SwapEvocationPotentCantripAndSculptSpell"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.SwapEvocationPotentCantripAndSculptSpell = toggle;
            WizardEvocation.SwapEvocationPotentCantripAndSculptSpell();
        }

        toggle = Main.Settings.EnableEvocationPotentCantrip2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableEvocationPotentCantrip2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableEvocationPotentCantrip2024 = toggle;
            WizardEvocation.SwitchPotentCantrip2024();
        }

        if (!tabletop2024Only)
        {
            toggle = Main.Settings.EvocationSculptSpellNoPerception;
            if (UI.Toggle(Gui.Localize("ModUi/&EvocationSculptSpellNoPerception"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EvocationSculptSpellNoPerception = toggle;
            }
        }
    }

    private static void DisplayClassHeading(string className)
    {
        UI.Label();
        UI.Label("<color=#F0DAA0>" + Gui.Localize($"Class/&{className}Title") + ":</color>");
        UI.Label();
    }

    internal static void DisplaySubclasses()
    {
        UI.Label();

        ModUi.DisplayDocumentationLinks(("ModUi/&DocsSubclasses", "SubClasses.md"));

        UI.Label();

        DisplaySubclassesGeneral();

        UI.Label();

        var expandAllTitle = Gui.Localize("ModUi/&ExpandAll");
        var selectAllTitle = Gui.Localize("ModUi/&SelectAll");
        var tabletopTitle = Gui.Localize("ModUi/&SelectTabletop");
        NamedAction[] bulkActions =
        [
            new(expandAllTitle, () =>
            {
                var toggle = Main.Settings.DisplayKlassToggle.All(x => x.Value);
                if (UI.Toggle(expandAllTitle, ref toggle, UI.AutoWidth()))
                {
                    foreach (var key in Main.Settings.DisplayKlassToggle.Keys.ToHashSet())
                    {
                        Main.Settings.DisplayKlassToggle[key] = toggle;
                    }
                }
            }),
            new(selectAllTitle, () =>
            {
                var toggle = SubclassesContext.IsAllSetSelected();
                if (UI.Toggle(selectAllTitle, ref toggle, UI.AutoWidth()))
                {
                    SubclassesContext.SelectAllSet(toggle);
                }
            }),
            new(tabletopTitle, () =>
            {
                var toggle = SubclassesContext.IsTabletopSetSelected();
                if (UI.Toggle(tabletopTitle, ref toggle, UI.AutoWidth()))
                {
                    SubclassesContext.SelectTabletopSet(toggle);
                }
            })
        ];
        UI.HStack(null, bulkActions.Length, GUI.skin.label,
            bulkActions.Select(action => UI.ToggleWidth(action.Name)).ToArray(), false, bulkActions);

        UI.Div();

        foreach (var klassDefinition in SubclassesContext.KlassListContextTab.Keys
                     .OrderBy(klass => klass.FormatTitle(), StringComparer.CurrentCulture))
        {
            var klassName = klassDefinition.Name;
            var displayName = klassDefinition.FormatTitle();
            if (klassName == InventorClass.ClassName)
            {
                displayName += " \u00a9".Grey();
            }
            var subclassListContext = SubclassesContext.KlassListContextTab[klassDefinition];
            var displayToggle = Main.Settings.DisplayKlassToggle[klassName];
            var sliderPos = Main.Settings.KlassListSliderPosition[klassName];
            var subclassEnabled = Main.Settings.KlassListSubclassEnabled[klassName];

            ModUi.DisplayDefinitions(
                displayName.Khaki(),
                subclassListContext.Switch,
                subclassListContext.AllSubClasses,
                subclassEnabled,
                ref displayToggle,
                ref sliderPos,
                toggleEnabled: StrictTabletopSelectionContext.IsSubclassAllowedForCurrentMode,
                toggleValueOverride: subclass =>
                    !StrictTabletopSelectionContext.IsSubclassAllowedForCurrentMode(subclass)
                        ? false
                        : null);

            Main.Settings.DisplayKlassToggle[klassName] = displayToggle;
            Main.Settings.KlassListSliderPosition[klassName] = sliderPos;
        }

        UI.Label();
    }
}
