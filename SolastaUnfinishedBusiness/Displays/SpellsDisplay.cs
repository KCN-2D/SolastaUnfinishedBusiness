using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Displays;

internal static class SpellsDisplay
{
    private const int ShowAll = -1;

    internal static int SpellLevelFilter { get; private set; } = ShowAll;

    private static void DisplaySpellsGeneral()
    {
        var toggle = Main.Settings.DisplaySpellsGeneralToggle;
        if (UI.DisclosureToggle(Gui.Localize("ModUi/&General"), ref toggle, 200))
        {
            Main.Settings.DisplaySpellsGeneralToggle = toggle;
        }

        if (!Main.Settings.DisplaySpellsGeneralToggle)
        {
            return;
        }

        UI.Label();

        toggle = Main.Settings.AllowBladeCantripsToUseReach;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowBladeCantripsToUseReach"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AllowBladeCantripsToUseReach = toggle;
            SpellsContext.SwitchAllowBladeCantripsToUseReach();
        }

        toggle = Main.Settings.QuickCastLightCantripOnWornItemsFirst;
        if (UI.Toggle(Gui.Localize("ModUi/&QuickCastLightCantripOnWornItemsFirst"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.QuickCastLightCantripOnWornItemsFirst = toggle;
        }

        UI.Label();

        toggle = Main.Settings.AddBleedingToLesserRestoration;
        if (UI.Toggle(Gui.Localize("ModUi/&AddBleedingToLesserRestoration"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AddBleedingToLesserRestoration = toggle;
            SpellsContext.SwitchAddBleedingToLesserRestoration();
        }

        toggle = Main.Settings.AllowTargetingSelectionWhenCastingChainLightningSpell;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowTargetingSelectionWhenCastingChainLightningSpell"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.AllowTargetingSelectionWhenCastingChainLightningSpell = toggle;
            SpellsContext.SwitchAllowTargetingSelectionWhenCastingChainLightningSpell();
        }

        toggle = Main.Settings.BestowCurseNoConcentrationRequiredForSlotLevel5OrAbove;
        if (UI.Toggle(Gui.Localize("ModUi/&BestowCurseNoConcentrationRequiredForSlotLevel5OrAbove"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.BestowCurseNoConcentrationRequiredForSlotLevel5OrAbove = toggle;
        }

        toggle = Main.Settings.EnableUpcastConjureElementalAndFey;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableUpcastConjureElementalAndFey"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableUpcastConjureElementalAndFey = toggle;
            Main.Settings.OnlyShowMostPowerfulUpcastConjuredElementalOrFey = false;
            SpellsContext.SwitchEnableUpcastConjureElementalAndFey();
        }

        if (Main.Settings.EnableUpcastConjureElementalAndFey)
        {
            toggle = Main.Settings.OnlyShowMostPowerfulUpcastConjuredElementalOrFey;
            if (UI.Toggle(Gui.Localize("ModUi/&OnlyShowMostPowerfulUpcastConjuredElementalOrFey"), ref toggle,
                    UI.AutoWidth()))
            {
                Main.Settings.OnlyShowMostPowerfulUpcastConjuredElementalOrFey = toggle;
            }
        }

        toggle = Main.Settings.IllusionSpellsAutomaticallyFailAgainstTrueSightInRange;
        if (UI.Toggle(Gui.Localize("ModUi/&IllusionSpellsAutomaticallyFailAgainstTrueSightInRange"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.IllusionSpellsAutomaticallyFailAgainstTrueSightInRange = toggle;
        }

        toggle = Main.Settings.RemoveRecurringEffectOnEntangle;
        if (UI.Toggle(Gui.Localize("ModUi/&RemoveRecurringEffectOnEntangle"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RemoveRecurringEffectOnEntangle = toggle;
            SpellsContext.SwitchRecurringEffectOnEntangle();
        }

        toggle = Main.Settings.RemoveHumanoidFilterOnHideousLaughter;
        if (UI.Toggle(Gui.Localize("ModUi/&RemoveHumanoidFilterOnHideousLaughter"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RemoveHumanoidFilterOnHideousLaughter = toggle;
            SpellsContext.SwitchFilterOnHideousLaughter();
        }

        UI.Label();

        toggle = Main.Settings.ChangeSleetStormToCube;
        if (UI.Toggle(Gui.Localize("ModUi/&ChangeSleetStormToCube"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ChangeSleetStormToCube = toggle;
            SpellsContext.SwitchChangeSleetStormToCube();
        }

        toggle = Main.Settings.UseHeightOneCylinderEffect;
        if (UI.Toggle(Gui.Localize("ModUi/&UseHeightOneCylinderEffect"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.UseHeightOneCylinderEffect = toggle;
            SpellsContext.SwitchUseHeightOneCylinderEffect();
        }

        toggle = Main.Settings.FixEldritchBlastRange;
        if (UI.Toggle(Gui.Localize("ModUi/&FixEldritchBlastRange"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.FixEldritchBlastRange = toggle;
            Tabletop2014Context.SwitchEldritchBlastRange();
        }

        toggle = Main.Settings.ModifyGravitySlam;
        if (UI.Toggle(Gui.Localize("ModUi/&ModifyGravitySlam"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ModifyGravitySlam = toggle && Main.Settings.EnablePullPushOnVerticalDirection;
            Tabletop2014Context.SwitchGravitySlam();
        }

        UI.Label();
        UI.Label();

        DisplaySpells2024();

        UI.Label();

        toggle = Main.Settings.AllowHasteCasting;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowHasteCasting"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AllowHasteCasting = toggle;
            SpellsContext.SwitchHastedCasing();
        }

        toggle = Main.Settings.AllowStackedMaterialComponent;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowStackedMaterialComponent"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AllowStackedMaterialComponent = toggle;
        }

        toggle = Main.Settings.EnableRelearnSpells;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableRelearnSpells"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableRelearnSpells = toggle;
        }

        toggle = Main.Settings.SwapShineCantrip;
        if (UI.Toggle(Gui.Localize("ModUI/&SwapShineCantrip"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.SwapShineCantrip = toggle;
            Tabletop2024Context.SwitchShineCantrip();
        }
    }

    internal static void DisplaySpells2024()
    {
        var toggle = Main.Settings.EnablePreparedSpellsTables2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnablePreparedSpellsTables2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnablePreparedSpellsTables2024 = toggle;
            Tabletop2024Context.SwitchOneDndPreparedSpellsTables();
        }

        toggle = Main.Settings.EnableRitualOnAllCasters2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableRitualOnAllCasters2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableRitualOnAllCasters2024 = toggle;
            Tabletop2024Context.SwitchOneDndSpellRitualOnAllCasters();
        }

        toggle = Main.Settings.EnableOneSpellSlotPerTurn2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableOneSpellSlotPerTurn2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneSpellSlotPerTurn2024 = toggle;
        }

        toggle = Main.Settings.EnableOneDndCounterspellSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndCounterspellSpell",
                    Counterspell),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndCounterspellSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellCounterspell();
        }

        if (Main.Settings.EnableOneDndCounterspellSpell)
        {
            UI.Label(Gui.Localize("ModUi/&Counterspell2024CompatibilityDescription"),
                new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.ExpandWidth(true));
        }

        UI.Label();

        toggle = Main.Settings.EnableOneDndBarkskinSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndBarkskinSpell",
                    Barkskin),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndBarkskinSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellBarkskin();
        }

        toggle = Main.Settings.EnableOneDndBladeWardCantrip;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndBladeWardCantrip",
                    SpellsContext.BladeWard),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndBladeWardCantrip = toggle;
            Tabletop2024Context.SwitchOneDndCantripBladeWard();
        }

        toggle = Main.Settings.EnableOneDndChillTouchCantrip;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndChillTouchCantrip",
                    ChillTouch),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndChillTouchCantrip = toggle;
            Tabletop2024Context.SwitchOneDndCantripChillTouch();
        }

        toggle = Main.Settings.EnableOneDndDamagingSpellsUpgrade;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndDamagingSpellsUpgrade",
                    ArcaneSword, CircleOfDeath, FlameStrike, IceStorm, PrismaticSpray, ViciousMockery),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndDamagingSpellsUpgrade = toggle;
            Tabletop2024Context.SwitchOneDndDamagingSpellsUpgrade();
        }

        toggle = Main.Settings.EnableOneDndHealingSpellsUpgrade;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndHealingSpellsUpgrade",
                    CureWounds, FalseLife, HealingWord, MassCureWounds, MassHealingWord),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndHealingSpellsUpgrade = toggle;
            Tabletop2024Context.SwitchOneDndHealingSpellsUpgrade();
        }

        toggle = Main.Settings.EnableOneDndDivineFavorSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndDivineFavorSpell",
                    DivineFavor),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndDivineFavorSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellDivineFavor();
        }

        toggle = Main.Settings.EnableSmiteSpells2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSmiteSpells2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSmiteSpells2024 = toggle;
            SmiteSpells2024Context.SwitchSmiteSpells();
        }

        if (Main.Settings.EnableSmiteSpells2024)
        {
            toggle = Main.Settings.AddPaladinSmiteToggle;
            if (UI.Toggle(" + " + Gui.Localize("ModUi/&AddPaladinSmiteToggle"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.AddPaladinSmiteToggle = toggle;
                Global.RefreshControlledCharacter();
            }
        }

        toggle = Main.Settings.EnableOneDndGuidanceSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndGuidanceSpell",
                    Guidance),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndGuidanceSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellGuidance();
        }

        toggle = Main.Settings.EnableOneDndHideousLaughterSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndHideousLaughterSpell",
                    HideousLaughter),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndHideousLaughterSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellHideousLaughter();
        }

        toggle = Main.Settings.EnableOneDndHuntersMarkSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndHuntersMarkSpell",
                    HuntersMark),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndHuntersMarkSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellHuntersMark();
        }

        toggle = Main.Settings.EnableOneDndLesserRestorationSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndLesserRestorationSpell",
                    LesserRestoration),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndLesserRestorationSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellLesserRestoration();
        }

        toggle = Main.Settings.EnableOneDndMagicWeaponSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndMagicWeaponSpell",
                    MagicWeapon),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndMagicWeaponSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellMagicWeapon();
        }

        toggle = Main.Settings.EnableOneDndPowerWordStunSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndPowerWordStunSpell",
                    PowerWordStun),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndPowerWordStunSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellPowerWordStun();
        }

        toggle = Main.Settings.EnableOneDndSpareTheDyingSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndSpareTheDyingSpell",
                    SpareTheDying),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndSpareTheDyingSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellSpareTheDying();
        }

        toggle = Main.Settings.EnableOneDndSpiderClimbSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndSpiderClimbSpell",
                    SpiderClimb),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndSpiderClimbSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellSpiderClimb();
        }

        toggle = Main.Settings.EnableOneDndStoneSkinSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndStoneSkinSpell",
                    Stoneskin),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndStoneSkinSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellStoneSkin();
        }

        toggle = Main.Settings.EnableOneDndWitchBoltSpell;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndWitchBoltSpell",
                    DatabaseHelper.GetDefinition<SpellDefinition>("WitchBolt")),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndWitchBoltSpell = toggle;
            Tabletop2024Context.SwitchOneDndSpellWitchBolt();
        }

        toggle = Main.Settings.EnableOneDndTrueStrikeCantrip;
        if (UI.Toggle(
                FormatSpellRuleSetting("ModUi/&EnableOneDndTrueStrikeCantrip",
                    TrueStrike),
                ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOneDndTrueStrikeCantrip = toggle;
        }
    }

    private static string FormatSpellRuleSetting(string settingKey, params SpellDefinition[] spells)
    {
        return Gui.Format(settingKey, spells.Select(spell => spell.FormatTitle()).ToArray());
    }

    internal static void DisplaySpells()
    {
        UI.Label();

        UI.ActionButton(Gui.Localize("ModUi/&DocsSpells").Bold().Khaki(),
            () => UpdateContext.OpenDocumentation("Spells.md"), UI.Width(189f));

        UI.Label();

        DisplaySpellsGeneral();

        UI.Label();

        var intValue = SpellLevelFilter;
        // ReSharper disable once InvertIf
        if (UI.Slider(Gui.Localize("ModUi/&SpellLevelFilter"), ref intValue, ShowAll, 9, ShowAll))
        {
            SpellLevelFilter = intValue;
            SpellsContext.RecalculateDisplayedSpells();
        }

        UI.Label();

        var toggle = Main.Settings.EnableSpellLists2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSpellLists2024"), ref toggle,
                UI.Width(ModUi.PixelsPerColumn)))
        {
            Main.Settings.EnableSpellLists2024 = toggle;
            Tabletop2024Context.SwitchSpellLists2024();
        }

        if (Main.Settings.EnableSpellLists2024)
        {
            UI.Label(Gui.Localize("ModUi/&SpellLists2024ListHelp"));
            UI.Label();
        }

        toggle = Main.Settings.AllowDisplayingOfficialSpells;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowDisplayingOfficialSpells"), ref toggle,
                UI.Width(ModUi.PixelsPerColumn)))
        {
            Main.Settings.AllowDisplayingOfficialSpells = toggle;
            SpellsContext.RecalculateDisplayedSpells();
        }

        toggle = Main.Settings.AllowDisplayingNonSuggestedSpells;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowDisplayingNonSuggestedSpells"), ref toggle,
                UI.Width(ModUi.PixelsPerColumn)))
        {
            Main.Settings.AllowDisplayingNonSuggestedSpells = toggle;
            SpellsContext.RecalculateDisplayedSpells();
        }

        toggle = Main.Settings.AddNewScrollsToShops;
        if (UI.Toggle(Gui.Localize("ModUi/&AddNewScrollsToShops"), ref toggle,
                UI.Width(ModUi.PixelsPerColumn)))
        {
            Main.Settings.AddNewScrollsToShops = toggle;
        }

        toggle = Main.Settings.AddNewScrollsToTreasure;
        if (UI.Toggle(Gui.Localize("ModUi/&AddNewScrollsToTreasure"), ref toggle,
                UI.Width(ModUi.PixelsPerColumn)))
        {
            Main.Settings.AddNewScrollsToTreasure = toggle;
        }

        UI.Label();

        using (UI.HorizontalScope())
        {
            var displaySpellListsToggle = Main.Settings.DisplaySpellListsToggle.All(x => x.Value);

            toggle = displaySpellListsToggle;
            if (UI.Toggle(Gui.Localize("ModUi/&ExpandAll"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
            {
                foreach (var key in Main.Settings.DisplaySpellListsToggle.Keys.ToHashSet())
                {
                    Main.Settings.DisplaySpellListsToggle[key] = toggle;
                }
            }

            toggle = SpellsContext.IsSuggestedSetSelected();
            if (UI.Toggle(Gui.Localize("ModUi/&SelectSuggested"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
            {
                SpellsContext.SelectSuggestedSet(toggle);
            }

            toggle = SpellsContext.IsTabletopSetSelected();
            if (UI.Toggle(Gui.Localize("ModUi/&SelectTabletop"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
            {
                SpellsContext.SelectTabletopSet(toggle);
            }

            if (displaySpellListsToggle)
            {
                toggle = SpellsContext.IsAllSetSelected();
                if (UI.Toggle(Gui.Localize("ModUi/&SelectDisplayed"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
                {
                    SpellsContext.SelectAllSet(toggle);
                }
            }
        }

        UI.Div();

        foreach (var kvp in SpellsContext.SpellLists)
        {
            var spellListDefinition = kvp.Value;
            var spellListContext = SpellsContext.SpellListContextTab[spellListDefinition];
            var name = spellListDefinition.name;
            var displayToggle = Main.Settings.DisplaySpellListsToggle[name];
            var sliderPos = Main.Settings.SpellListSliderPosition[name];
            var spellEnabled = Main.Settings.SpellListSpellEnabled[name];
            var allowedSpells = spellListContext.DisplayedSpells;

            ModUi.DisplayDefinitions(
                kvp.Key.Khaki(),
                spellListContext.Switch,
                allowedSpells,
                spellEnabled,
                ref displayToggle,
                ref sliderPos,
                additionalRendering: AdditionalRendering,
                toggleEnabled: spellListContext.IsSpellToggleEnabled,
                toggleValueOverride: spellListContext.GetSpellToggleValueOverride);

            Main.Settings.DisplaySpellListsToggle[name] = displayToggle;
            Main.Settings.SpellListSliderPosition[name] = sliderPos;

            continue;

            void AdditionalRendering()
            {
                toggle = spellListContext.IsSuggestedSetSelected;
                if (UI.Toggle(Gui.Localize("ModUi/&SelectSuggested"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
                {
                    spellListContext.SelectSuggestedSetInternal(toggle);
                }

                toggle = spellListContext.IsTabletopSetSelected;
                if (UI.Toggle(Gui.Localize("ModUi/&SelectTabletop"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
                {
                    spellListContext.SelectTabletopSetInternal(toggle);
                }

                toggle = spellListContext.IsAllSetSelected;
                if (UI.Toggle(Gui.Localize("ModUi/&SelectDisplayed"), ref toggle, UI.Width(ModUi.PixelsPerColumn)))
                {
                    spellListContext.SelectAllSetInternal(toggle);
                }
            }
        }

        UI.Label();
    }
}
