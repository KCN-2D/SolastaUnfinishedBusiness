using System;
using System.Diagnostics;
using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses;
using UnityEngine;
#if DEBUG
using UnityExplorer;
#endif

namespace SolastaUnfinishedBusiness.Displays;

internal static class ToolsDisplay
{
    private static string ExportFileName { get; set; } =
        ServiceRepository.GetService<INetworkingService>()?.GetUserName() ?? "Multiplayer";

#if DEBUG
    private static bool IsUnityExplorerEnabled { get; set; }
#endif

    internal static void DisplayGameplay()
    {
        DisplayGeneral();
        UI.Label();
        DisplayMultiplayer();

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&TableTopHelp1"));
        UI.Label(Gui.Localize("ModUi/&TableTopHelp2"));
        UI.Label();

        var buttonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true, fixedHeight = 0f };
        NamedAction Button(string key, Action action)
        {
            var title = Gui.Localize(key);
            return new NamedAction(title, () => UI.ActionButton(title, action, buttonStyle, UI.ExpandWidth(true)));
        }

        UI.HStack(null, 4, buttonStyle,
            Button("ModUi/&TableTopButton", SelectTabletopSet),
            Button("ModUi/&ModQoL", () => SwitchTabletop(TagType.QoL)),
            Button("ModUi/&TableTop2014", () => SwitchTabletop(TagType.T2014)),
            Button("ModUi/&TableTop2024", () => SwitchTabletop(TagType.T2024)));

        UI.Label();
        DisplayTabletop2014();
        UI.Label();
        DisplayTabletop2024();
        UI.Label();
    }

    private static void SwitchTabletop(TagType tagType)
    {
        var properties = typeof(Settings).GetProperties()
            .Where(prop =>
                prop.IsDefined(typeof(Tag), false) &&
                ((Tag[])prop.GetCustomAttributes(typeof(Tag), false)).Any(x => x.Type == tagType))
            .ToArray();

        foreach (var property in properties)
        {
            property.SetValue(Main.Settings, true);
        }

        if (tagType == TagType.T2024)
        {
            ApplyTabletop2024RuntimeSettings();
        }
    }

    private static void ApplyTabletop2024RuntimeSettings()
    {
        Tabletop2024Context.SwitchRangerSpellCastingAtOne();
        Tabletop2024Context.SwitchOneDndPreparedSpellsTables();
        Tabletop2024Context.SwitchOneDndSpellCounterspell();
        Tabletop2024Context.SwitchSpeciesBaseWalkSpeed();
        SpeciesSpellcastingContext.Switch();
        Tabletop2024Context.SwitchHalfOrcAdrenalineRush();
        Tabletop2024Context.SwitchRogueSteadyAim();
        Tabletop2024Context.SwitchAssassinAssassinate();
        Tabletop2024Context.SwitchAssassinInfiltrationExpertise();
    }

    private static void SelectTabletopSet()
    {
        foreach (var background in BackgroundsContext.Backgrounds)
        {
            BackgroundsContext.Switch(background, ModUi.IsTabletopDefinition(background));
        }

        foreach (var race in RacesContext.Races)
        {
            RacesContext.Switch(race, ModUi.IsTabletopDefinition(race));
        }

        foreach (var subrace in RacesContext.Subraces)
        {
            RacesContext.SwitchSubrace(subrace, ModUi.IsTabletopDefinition(subrace));
        }

        foreach (var feat in FeatsContext.Feats)
        {
            FeatsContext.SwitchFeat(
                feat,
                ModUi.IsTabletopDefinition(feat) &&
                Tabletop2024Context.IsFeatToggleAvailableInCurrentMode(feat));
        }

        foreach (var featGroup in FeatsContext.FeatGroups)
        {
            FeatsContext.SwitchFeatGroup(
                featGroup,
                ModUi.IsTabletopDefinition(featGroup) &&
                Tabletop2024Context.IsFeatToggleAvailableInCurrentMode(featGroup));
        }

        foreach (var fightingStyles in FightingStyleContext.FightingStyles)
        {
            FightingStyleContext.Switch(fightingStyles, ModUi.IsTabletopDefinition(fightingStyles));
        }

        foreach (var invocation in InvocationsContext.Invocations)
        {
            InvocationsContext.SwitchInvocation(invocation, ModUi.IsTabletopDefinition(invocation));
        }

        foreach (var metamagic in MetamagicContext.Metamagic)
        {
            MetamagicContext.SwitchMetamagic(metamagic, ModUi.IsTabletopDefinition(metamagic));
        }

        SpellsContext.SelectTabletopSet(true);
        SubclassesContext.SelectTabletopSet(true);
    }

#if DEBUG
    private static void EnableUnityExplorerUi()
    {
        IsUnityExplorerEnabled = true;

        try
        {
            ExplorerStandalone.CreateInstance();
        }
        catch
        {
            // ignored
        }
    }
#endif

    private static void DisplayGeneral()
    {
        UI.Label();

#if DEBUG
        var size = IsUnityExplorerEnabled ? 195f : 145f;
#else
        // ReSharper disable once ConvertToConstant.Local
        var size = 195f;
#endif

        var width = UI.Width(size);

        using (UI.HorizontalScope())
        {
            if (UpdateContext.InProgress)
            {
                UI.ActionButton($"{UpdateContext.Progress}%", () => { }, width);
            }
            else
            {
                UI.ActionButton(Gui.Localize("ModUi/&Update"), () => UpdateContext.UpdateMod(), width);
            }

            UI.ActionButton(Gui.Localize("ModUi/&Rollback"), UpdateContext.DisplayRollbackMessage, width);
            UI.ActionButton(Gui.Localize("ModUi/&Changelog"), UpdateContext.OpenChangeLog, width);

#if DEBUG
            if (!IsUnityExplorerEnabled)
            {
                UI.ActionButton(Gui.Localize("ModUi/&UnityExplorer"), EnableUnityExplorerUi, UI.Width(145f));
            }
#endif
        }

        UI.Label();

        var toggle = Main.Settings.EnablePcgRandom;
        if (UI.Toggle(Gui.Localize("ModUi/&EnablePcgRandom"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnablePcgRandom = toggle;
        }

        if (Global.IsMultiplayer)
        {
            UI.Label(Gui.Localize("ModUi/&PcgRandomMultiplayerHelp"),
                new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.ExpandWidth(true));
        }

        toggle = Main.Settings.EnableFastLocationLoads;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableFastLocationLoads"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableFastLocationLoads = toggle;
            LoadingContext.ApplySettings();
        }

        UI.Label(Gui.Localize("ModUi/&EnableFastLocationLoadsHelp"),
            new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.ExpandWidth(true));
        UI.Label();

        toggle = Main.Settings.EnableCustomPortraits;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableCustomPortraits"), ref toggle))
        {
            Main.Settings.EnableCustomPortraits = toggle;
        }

        if (Main.Settings.EnableCustomPortraits)
        {
            UI.Label();

            UI.ActionButton(Gui.Localize("ModUi/&PortraitsOpenFolder"), () =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = PortraitsContext.PortraitsFolder, UseShellExecute = true, Verb = "open"
                });
            }, UI.Width(292f));

            UI.Label();
            UI.Label(Gui.Localize("ModUi/&EnableCustomPortraitsHelp"));
        }

        UI.Label();

        toggle = Main.Settings.DisableMultilineSpellOffering;
        if (UI.Toggle(Gui.Localize("ModUi/&DisableMultilineSpellOffering"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DisableMultilineSpellOffering = toggle;
        }

        toggle = Main.Settings.DisableUnofficialTranslations;
        if (UI.Toggle(Gui.Localize("ModUi/&DisableUnofficialTranslations"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DisableUnofficialTranslations = toggle;
        }

        if (!Main.Settings.DisableUnofficialTranslations)
        {
            toggle = Main.Settings.FilterUnofficialTranslationsByLanguage;
            if (UI.Toggle(
                    Gui.Localize("ModUi/&FilterUnofficialTranslationsByLanguage"),
                    ref toggle,
                    UI.AutoWidth()))
            {
                Main.Settings.FilterUnofficialTranslationsByLanguage = toggle;
            }

            UI.Label(Gui.Localize("ModUi/&FilterUnofficialTranslationsByLanguageHelp"));

            if (Main.Settings.FilterUnofficialTranslationsByLanguage)
            {
                DisplayUnofficialTranslationLanguages();
            }
        }

        toggle = Main.Settings.WideScreenBattleUI;
        if (UI.Toggle(Gui.Localize("ModUi/&WideScreenBattleUI"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.WideScreenBattleUI = toggle;
        }

        if (!Gui.GameCampaign)
        {
            return;
        }

        UI.Label();

        var gameCampaign = Gui.GameCampaign;

        if (!gameCampaign)
        {
            return;
        }

        UI.Label();

        using (UI.HorizontalScope())
        {
            UI.Label(Gui.Localize("ModUi/&IncreaseGameTimeBy"), UI.Width(300f));
            foreach (var hours in new[] { 1, 6, 12, 24 })
            {
                UI.ActionButton(Gui.Format("ModUi/&GameTimeHours", hours.ToString()),
                    () => gameCampaign.UpdateTime(60 * 60 * hours), UI.AutoWidth());
            }
        }
    }

    private static void DisplayUnofficialTranslationLanguages()
    {
        var languages = TranslatorContext.GetAvailableUnofficialLanguages();

        if (languages.Count == 0)
        {
            UI.Label(Gui.Localize("ModUi/&NoUnofficialTranslationsFound"));

            return;
        }

        foreach (var language in languages)
        {
            var toggle = IsUnofficialTranslationLanguageEnabled(language.Code);

            if (UI.Toggle($"{language.Text} ({language.Code})", ref toggle, UI.AutoWidth()))
            {
                SetUnofficialTranslationLanguageEnabled(language.Code, toggle);
            }
        }
    }

    private static bool IsUnofficialTranslationLanguageEnabled(string code)
    {
        return Main.Settings.EnabledUnofficialTranslationLanguages
            .Any(x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetUnofficialTranslationLanguageEnabled(string code, bool enabled)
    {
        var existing = Main.Settings.EnabledUnofficialTranslationLanguages
            .FirstOrDefault(x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

        if (enabled)
        {
            if (existing == null)
            {
                Main.Settings.EnabledUnofficialTranslationLanguages.Add(code);
            }
        }
        else if (existing != null)
        {
            Main.Settings.EnabledUnofficialTranslationLanguages.Remove(existing);
        }
    }

    private static void DisplayMultiplayer()
    {
        var toggle = Main.Settings.DisplayMultiplayerToggle;
        if (UI.DisclosureToggle(Gui.Localize("ModUi/&Multiplayer"), ref toggle))
        {
            Main.Settings.DisplayMultiplayerToggle = toggle;
        }

        if (!Main.Settings.DisplayMultiplayerToggle && !MultiplayerSettingsContext.IsOnlineRoom)
        {
            return;
        }

        MultiplayerSettingsContext.Refresh();
        var descriptionStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        UI.Label(Gui.Localize("ModUi/&MultiplayerSettingsHelp"), descriptionStyle, UI.ExpandWidth(true));
        UI.Label(MultiplayerSettingsContext.GetStatus(), descriptionStyle, UI.ExpandWidth(true));
        var guiEnabled = GUI.enabled;
        GUI.enabled = guiEnabled && MultiplayerSettingsContext.HasHostProfile;
        UI.ActionButton(Gui.Localize("ModUi/&MultiplayerSettingsSaveHost"),
            () => { MultiplayerSettingsContext.TrySaveHostSettings(); });
        GUI.enabled = guiEnabled;
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&SettingsHelp"), descriptionStyle, UI.ExpandWidth(true));
        if (!string.IsNullOrEmpty(Main.SettingsOperationMessage))
        {
            UI.Label(Gui.Localize(Main.SettingsOperationMessage), descriptionStyle, UI.ExpandWidth(true));
        }
        UI.Label();

        using (UI.HorizontalScope())
        {
            UI.ActionButton(Gui.Localize("ModUi/&SettingsExport"), () =>
            {
                Main.SaveSettings(ExportFileName);
            }, UI.Width(144f));

            UI.ActionButton(Gui.Localize("ModUi/&SettingsRemove"), () =>
            {
                Main.RemoveSettings(ExportFileName);
            }, UI.Width(144f));

            var text = ExportFileName;

            UI.ActionTextField(ref text, string.Empty, s => { ExportFileName = s; }, null, UI.Width(144f));
        }

        using (UI.HorizontalScope())
        {
            UI.ActionButton(Gui.Localize("ModUi/&SettingsRefresh"), Main.LoadSettingFilenames, UI.Width(144f));
            UI.ActionButton(Gui.Localize("ModUi/&SettingsOpenFolder"), () =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Main.SettingsFolder, UseShellExecute = true, Verb = "open"
                });
            }, UI.Width(292f));
        }

        if (Main.SettingsFiles.Length == 0)
        {
            return;
        }

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&SettingsLoad"));
        if (!Main.CanImportSettings)
        {
            UI.Label(Gui.Localize("ModUi/&SettingsImportUnavailable"), descriptionStyle, UI.ExpandWidth(true));
        }
        UI.Label();

        GUI.enabled = guiEnabled && Main.CanImportSettings;
        var intValue = -1;
        var profileStyle = new GUIStyle(GUI.skin.button) { wordWrap = true };
        if (UI.SelectionGrid(ref intValue, Main.SettingsFiles, 1, profileStyle, UI.ExpandWidth(true)))
        {
            Main.LoadSettings(Main.SettingsFiles[intValue]);
        }
        GUI.enabled = guiEnabled;
    }

    private static void DisplayTabletop2024()
    {
        var toggle = Main.Settings.DisplayTabletop2024;
        if (UI.DisclosureToggle(Gui.Localize("ModUi/&OneDnd"), ref toggle))
        {
            Main.Settings.DisplayTabletop2024 = toggle;
        }

        if (!Main.Settings.DisplayTabletop2024)
        {
            return;
        }

        UI.Label();

        UI.Label("<color=#F0DAA0>" + Gui.Localize("ModUi/&DocsRaces") + ":</color>");
        UI.Label();

        toggle = Main.Settings.EnableSpeciesBaseWalkSpeed2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSpeciesBaseWalkSpeed2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSpeciesBaseWalkSpeed2024 = toggle;
            Tabletop2024Context.SwitchSpeciesBaseWalkSpeed();
        }

        toggle = Main.Settings.EnableSpeciesSpellcastingAbility2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSpeciesSpellcastingAbility2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSpeciesSpellcastingAbility2024 = toggle;
            SpeciesSpellcastingContext.Switch();
        }

        toggle = Main.Settings.EnableHalfOrcAdrenalineRush2024;
        if (UI.Toggle(Gui.Format("ModUi/&EnableHalfOrcAdrenalineRush2024",
                    DatabaseHelper.CharacterRaceDefinitions.HalfOrc.FormatTitle(),
                    DatabaseHelper.FeatureDefinitionAdditionalDamages.AdditionalDamageHalfOrcSavageAttacks.FormatTitle(),
                    Tabletop2024Context.PowerHalfOrcAdrenalineRush.FormatTitle()), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableHalfOrcAdrenalineRush2024 = toggle;
            Tabletop2024Context.SwitchHalfOrcAdrenalineRush();
        }

        UI.Label();
        UI.Label("<color=#F0DAA0>" + Gui.Localize("ModUi/&Rules") + ":</color>");
        UI.Label();

        RulesDisplay.DisplayToolCheckRules2024();

        toggle = Main.Settings.EnablePoisonsBonusAction2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnablePoisonsBonusAction2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnablePoisonsBonusAction2024 = toggle;
            Tabletop2024Context.SwitchPoisonsBonusAction();
        }

        toggle = Main.Settings.EnablePotionsBonusAction2024;
        if (UI.Toggle(Gui.Localize("ModUi/&EnablePotionsBonusAction2024"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnablePotionsBonusAction2024 = toggle;
            Tabletop2024Context.SwitchPotionsBonusAction();
        }

        toggle = Main.Settings.EnableSurprisedToEnforceDisadvantage;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSurprisedToEnforceDisadvantage"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSurprisedToEnforceDisadvantage = toggle;
            Tabletop2024Context.SwitchSurprisedEnforceDisadvantage();
        }

        RulesDisplay.DisplayWeaponMasteryRules2024();

        UI.Label();
        UI.Label("<color=#F0DAA0>" + Gui.Localize("ModUi/&DocsClasses") + ":</color>");
        ClassesDisplay.DisplayClassRules(true);

        UI.Label();
        UI.Label("<color=#F0DAA0>" + Gui.Localize("ModUi/&DocsSpells") + ":</color>");
        UI.Label();

        SpellsDisplay.DisplaySpells2024();

        UI.Label();
        UI.Label("<color=#F0DAA0>" + Gui.Localize("ModUi/&DocsSubclasses") + ":</color>");
        UI.Label();

        SubclassesDisplay.DisplaySubclassRules(true);
    }

    private static void DisplayTabletop2014()
    {
        var toggle = Main.Settings.DisplayTabletopToggle;
        if (UI.DisclosureToggle(Gui.Localize("ModUi/&Tabletop"), ref toggle))
        {
            Main.Settings.DisplayTabletopToggle = toggle;
        }

        if (!Main.Settings.DisplayTabletopToggle)
        {
            return;
        }

        UI.Label();

        toggle = Main.Settings.EnableLevel20;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableLevel20"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableLevel20 = toggle;
        }

        toggle = Main.Settings.EnableMulticlass;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableMulticlass"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableMulticlass = toggle;
            Main.Settings.MaxAllowedClasses = MulticlassContext.DefaultClasses;
            Main.Settings.EnableMinInOutAttributes = true;
            Main.Settings.DisplayAllKnownSpellsDuringLevelUp = true;
            Main.Settings.DisplayPactSlotsOnSpellSelectionPanel = true;
            Main.Settings.AlwaysSpendPactSlotsFirst = true;
        }

        if (Main.Settings.EnableMulticlass)
        {
            UI.Label();

            var intValue = Main.Settings.MaxAllowedClasses;
            if (UI.Slider(Gui.Localize("ModUi/&MaxAllowedClasses"), ref intValue,
                    MulticlassContext.MinClasses, MulticlassContext.MaxClasses, MulticlassContext.DefaultClasses,
                    "", UI.AutoWidth()))
            {
                //For some reason Math.Clamp is not available
                Main.Settings.MaxAllowedClasses = intValue switch
                {
                    < MulticlassContext.MinClasses => MulticlassContext.MinClasses,
                    > MulticlassContext.MaxClasses => MulticlassContext.MaxClasses,
                    _ => intValue
                };
            }

            UI.Label();

            toggle = Main.Settings.DisplayAllKnownSpellsDuringLevelUp;
            if (UI.Toggle(Gui.Localize("ModUi/&DisplayAllKnownSpellsDuringLevelUp"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.DisplayAllKnownSpellsDuringLevelUp = toggle;
            }

            toggle = Main.Settings.DisplayPactSlotsOnSpellSelectionPanel;
            if (UI.Toggle(Gui.Localize("ModUi/&DisplayPactSlotsOnSpellSelectionPanel"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.DisplayPactSlotsOnSpellSelectionPanel = toggle;
            }

            toggle = Main.Settings.EnableMinInOutAttributes;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableMinInOutAttributes"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableMinInOutAttributes = toggle;
            }

            toggle = Main.Settings.AlwaysSpendPactSlotsFirst;
            if (UI.Toggle(Gui.Localize("ModUi/&AlwaysSpendPactSlotsFirst"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.AlwaysSpendPactSlotsFirst = toggle;
            }

            if (!Main.Settings.AlwaysSpendPactSlotsFirst)
            {
                UI.Label();
                UI.Label(Gui.Localize("ModUi/&MulticlassKeyHelp"));    
            }
        }

        UI.Label();

        toggle = Main.Settings.DontEndTurnAfterReady;
        if (UI.Toggle(Gui.Localize("ModUi/&DontEndTurnAfterReady"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DontEndTurnAfterReady = toggle;
        }

        UI.Label();

        toggle = Main.Settings.EnableProneAction;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableProneAction"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableProneAction = toggle;
            Tabletop2014Context.SwitchProneAction();
        }

        toggle = Main.Settings.EnableGrappleAction;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableGrappleAction"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableGrappleAction = toggle;
            Tabletop2014Context.SwitchGrappleAction();
        }

        toggle = Main.Settings.EnableHelpAction;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableHelpAction"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableHelpAction = toggle;
            Tabletop2014Context.SwitchHelpPower();
        }

        toggle = Main.Settings.EnableUnarmedMainAttackAction;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableUnarmedMainAttackAction"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.EnableUnarmedMainAttackAction = toggle;
        }

        UI.Label();

        toggle = Main.Settings.UseOfficialAdvantageDisadvantageRules;
        if (UI.Toggle(Gui.Localize("ModUi/&UseOfficialAdvantageDisadvantageRules"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.UseOfficialAdvantageDisadvantageRules = toggle;
            Main.Settings.UseOfficialFlankingRulesAlsoForRanged = false;
        }

        UI.Label();

        toggle = Main.Settings.BlindedConditionDontAllowAttackOfOpportunity;
        if (UI.Toggle(Gui.Localize("ModUi/&BlindedConditionDontAllowAttackOfOpportunity"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.BlindedConditionDontAllowAttackOfOpportunity = toggle;
        }

        toggle = Main.Settings.UseOfficialLightingObscurementAndVisionRules;
        if (UI.Toggle(Gui.Localize("ModUi/&UseOfficialObscurementRules"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.UseOfficialLightingObscurementAndVisionRules = toggle;
            Main.Settings.OfficialObscurementRulesInvisibleCreaturesCanBeTarget = toggle;
            Main.Settings.OfficialObscurementRulesCancelAdvDisPairs = toggle;
            Main.Settings.OfficialObscurementRulesHeavilyObscuredAsProjectileBlocker = false;
            Main.Settings.OfficialObscurementRulesMagicalDarknessAsProjectileBlocker = false;
            Main.Settings.EnableChanceToPerceiveCloseRange = false;
            Main.Settings.OfficialObscurementRulesTweakMonsters = toggle;
            LightingAndObscurementContext.SwitchOfficialObscurementRules();
        }

        toggle = Main.Settings.EnableCeilingAwareTargetPerception;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableCeilingAwareTargetPerception"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableCeilingAwareTargetPerception = toggle;
        }

        UI.Label(Gui.Localize("ModUi/&EnableCeilingAwareTargetPerceptionHelp"));

        if (Main.Settings.UseOfficialLightingObscurementAndVisionRules)
        {
            UI.Label(Gui.Localize("ModUi/&UseOfficialObscurementRulesHelp"));

            toggle = Main.Settings.OfficialObscurementRulesInvisibleCreaturesCanBeTarget;
            if (UI.Toggle(Gui.Localize("ModUi/&OfficialObscurementRulesInvisibleCreaturesCanBeTarget"), ref toggle,
                    UI.AutoWidth()))
            {
                Main.Settings.OfficialObscurementRulesInvisibleCreaturesCanBeTarget = toggle;
            }

            toggle = Main.Settings.OfficialObscurementRulesCancelAdvDisPairs;
            if (UI.Toggle(Gui.Localize("ModUi/&OfficialObscurementRulesCancelAdvDisPairs"), ref toggle,
                    UI.AutoWidth()))
            {
                Main.Settings.OfficialObscurementRulesCancelAdvDisPairs = toggle;
            }

            toggle = Main.Settings.OfficialObscurementRulesTweakMonsters;
            if (UI.Toggle(Gui.Localize("ModUi/&OfficialObscurementRulesTweakMonsters"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.OfficialObscurementRulesTweakMonsters = toggle;
                LightingAndObscurementContext.SwitchMonstersOnObscurementRules();
            }

            toggle = Main.Settings.EnableChanceToPerceiveCloseRange;
            if (UI.Toggle(Gui.Localize("ModUI/&EnableChanceToPerceiveCloseRange"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableChanceToPerceiveCloseRange = toggle;
            }

            if (Main.Settings.OfficialObscurementRulesTweakMonsters)
            {
                UI.Label(Gui.Localize("ModUi/&OfficialObscurementRulesTweakMonstersHelp"));
            }
        }

        UI.Label();

        toggle = Main.Settings.StealthDoesNotBreakWithSubtle;
        if (UI.Toggle(Gui.Localize("ModUi/&StealthDoesNotBreakWithSubtle"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.StealthDoesNotBreakWithSubtle = toggle;
        }

        UI.Label();

        toggle = Main.Settings.StealthBreaksWhenAttackHits;
        if (UI.Toggle(Gui.Localize("ModUi/&StealthBreaksWhenAttackHits"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.StealthBreaksWhenAttackHits = toggle;
        }

        toggle = Main.Settings.StealthBreaksWhenAttackMisses;
        if (UI.Toggle(Gui.Localize("ModUi/&StealthBreaksWhenAttackMisses"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.StealthBreaksWhenAttackMisses = toggle;
        }

        toggle = Main.Settings.StealthBreaksWhenCastingVerbose;
        if (UI.Toggle(Gui.Localize("ModUi/&StealthBreaksWhenCastingVerbose"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.StealthBreaksWhenCastingVerbose = toggle;
        }

        toggle = Main.Settings.StealthBreaksWhenMoving;
        if (UI.Toggle(Gui.Localize("ModUi/&StealthBreaksWhenMoving"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.StealthBreaksWhenMoving = toggle;
        }

        UI.Label();

        toggle = Main.Settings.StealthRollForBreak;
        if (UI.Toggle(Gui.Localize("ModUi/&StealthRollForBreak"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.StealthRollForBreak = toggle;
        }

        UI.Label();

        toggle = Main.Settings.AccountForAllDiceOnSavageAttack;
        if (UI.Toggle(Gui.Localize("ModUi/&AccountForAllDiceOnSavageAttack"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AccountForAllDiceOnSavageAttack = toggle;
        }

        toggle = Main.Settings.AddDexModifierToEnemiesInitiativeRoll;
        if (UI.Toggle(Gui.Localize("ModUi/&AddDexModifierToEnemiesInitiativeRoll"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AddDexModifierToEnemiesInitiativeRoll = toggle;
            Main.Settings.EnemiesAlwaysRollInitiative = toggle;
        }

        if (Main.Settings.AddDexModifierToEnemiesInitiativeRoll)
        {
            toggle = Main.Settings.EnemiesAlwaysRollInitiative;
            if (UI.Toggle(Gui.Localize("ModUi/&EnemiesAlwaysRollInitiative"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnemiesAlwaysRollInitiative = toggle;
            }
        }

        UI.Label();

        toggle = Main.Settings.EnablePullPushOnVerticalDirection;
        if (UI.Toggle(Gui.Localize("ModUi/&EnablePullPushOnVerticalDirection"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnablePullPushOnVerticalDirection = toggle;
            if (!toggle)
            {
                Main.Settings.ModifyGravitySlam = false;
                Tabletop2014Context.SwitchGravitySlam();
            }
        }

        toggle = Main.Settings.FullyControlConjurations;
        if (UI.Toggle(Gui.Localize("ModUi/&FullyControlConjurations"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.FullyControlConjurations = toggle;
            Tabletop2014Context.SwitchFullyControlConjurations();
        }

        UI.Label();

        toggle = Main.Settings.EnableTeleportToRemoveRestrained;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableTeleportToRemoveRestrained"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableTeleportToRemoveRestrained = toggle;
        }

        UI.Label();

        toggle = Main.Settings.ColdResistanceAlsoGrantsImmunityToChilledCondition;
        if (UI.Toggle(Gui.Localize("ModUi/&ColdResistanceAlsoGrantsImmunityToChilledCondition"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.ColdResistanceAlsoGrantsImmunityToChilledCondition = toggle;
            Tabletop2014Context.SwitchColdResistanceAndImmunityAlsoGrantsWeatherImmunity();
        }

        toggle = Main.Settings.ColdImmunityAlsoGrantsImmunityToChilledAndFrozenCondition;
        if (UI.Toggle(Gui.Localize("ModUi/&ColdImmunityAlsoGrantsImmunityToChilledAndFrozenCondition"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.ColdImmunityAlsoGrantsImmunityToChilledAndFrozenCondition = toggle;
            Tabletop2014Context.SwitchColdResistanceAndImmunityAlsoGrantsWeatherImmunity();
        }

        UI.Label();

        toggle = Main.Settings.UseOfficialFoodRationsWeight;
        if (UI.Toggle(Gui.Localize("ModUi/&UseOfficialFoodRationsWeight"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.UseOfficialFoodRationsWeight = toggle;
            Tabletop2014Context.SwitchOfficialFoodRationsWeight();
        }

        toggle = Main.Settings.FixRingOfRegenerationHealRate;
        // ReSharper disable once InvertIf
        if (UI.Toggle(Gui.Localize("ModUi/&FixRingOfRegenerationHealRate"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.FixRingOfRegenerationHealRate = toggle;
            Tabletop2014Context.SwitchRingOfRegenerationHealRate();
        }

        UI.Label();

        BackgroundsAndRacesDisplay.DisplayAlternateHumanOption();

        toggle = Main.Settings.RaceLightSensitivityApplyOutdoorsOnly;
        if (UI.Toggle(Gui.Localize("ModUi/&RaceLightSensitivityApplyOutdoorsOnly"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RaceLightSensitivityApplyOutdoorsOnly = toggle;
        }

        toggle = Main.Settings.UseOfficialSmallRacesDisWithHeavyWeapons;
        if (UI.Toggle(Gui.Localize("ModUi/&UseOfficialSmallRacesDisWithHeavyWeapons"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.UseOfficialSmallRacesDisWithHeavyWeapons = toggle;
        }

        UI.Label();

        toggle = Main.Settings.EnableSorcererMagicalGuidance;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSorcererMagicalGuidance"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSorcererMagicalGuidance = toggle;
            ClassesContext.SwitchSorcererMagicalGuidance();
        }

        toggle = Main.Settings.EnableSorcererQuickenedAction;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSorcererQuickenedAction"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSorcererQuickenedAction = toggle;
        }

        if (Main.Settings.EnableSorcererQuickenedAction)
        {
            toggle = Main.Settings.HideQuickenedActionWhenMetamagicOff;
            if (UI.Toggle(Gui.Localize("ModUi/&HideQuickenedActionWhenMetamagicOff"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.HideQuickenedActionWhenMetamagicOff = toggle;
            }
        }

        UI.Label();

        toggle = Main.Settings.AccountForAllDiceOnFollowUpStrike;
        if (UI.Toggle(Gui.Localize("ModUi/&AccountForAllDiceOnFollowUpStrike"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AccountForAllDiceOnFollowUpStrike = toggle;
        }

        UI.Label();

        toggle = Main.Settings.IllusionSpellsAutomaticallyFailAgainstTrueSightInRange;
        if (UI.Toggle(Gui.Localize("ModUi/&IllusionSpellsAutomaticallyFailAgainstTrueSightInRange"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.IllusionSpellsAutomaticallyFailAgainstTrueSightInRange = toggle;
        }

        toggle = Main.Settings.BestowCurseNoConcentrationRequiredForSlotLevel5OrAbove;
        if (UI.Toggle(Gui.Localize("ModUi/&BestowCurseNoConcentrationRequiredForSlotLevel5OrAbove"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.BestowCurseNoConcentrationRequiredForSlotLevel5OrAbove = toggle;
        }

        toggle = Main.Settings.RemoveRecurringEffectOnEntangle;
        if (UI.Toggle(Gui.Localize("ModUi/&RemoveRecurringEffectOnEntangle"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RemoveRecurringEffectOnEntangle = toggle;
            SpellsContext.SwitchRecurringEffectOnEntangle();
        }

        toggle = Main.Settings.FixEldritchBlastRange;
        if (UI.Toggle(Gui.Localize("ModUi/&FixEldritchBlastRange"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.FixEldritchBlastRange = toggle;
            Tabletop2014Context.SwitchEldritchBlastRange();
        }

        toggle = Main.Settings.Use24CellsRangeWithDimensionDoor;
        if (UI.Toggle(Gui.Localize("ModUi/&Use24CellsRangeWithDimensionDoor"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.Use24CellsRangeWithDimensionDoor = toggle;
            SpellsContext.SwitchDimensionDoorRange();
        }

        UI.Label();

        toggle = Main.Settings.EnableBardHealingBalladOnLongRest;
        // ReSharper disable once InvertIf
        if (UI.Toggle(Gui.Localize("ModUi/&EnableBardHealingBalladOnLongRest"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableBardHealingBalladOnLongRest = toggle;
            Tabletop2014Context.SwitchBardHealingBalladOnLongRest();
        }
    }
}
