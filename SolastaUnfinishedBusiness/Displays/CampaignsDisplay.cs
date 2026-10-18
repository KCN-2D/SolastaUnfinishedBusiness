using System;
using System.Linq;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Displays;

internal static class CampaignsDisplay
{
    internal const float DefaultFastTimeModifier = 1.5f;
    private static bool _selectedForSwap;
    private static int _selectedX, _selectedY;
    private static readonly string[] SetNames = ["1", "2", "3", "4", "5"];
    private static bool _showAllSpeechLanguages;
    private static string _selectedSpeechNpc;
    private sealed class SpeechVoicePicker(string controlName, bool npc)
    {
        internal readonly UI.PagedList<string> List = new(controlName, 24);
        private int _revision = -1;
        private string _language;
        private string _selection;
        private bool _allLanguages;
        private string[] _voices = [];

        internal void Refresh(string voice)
        {
            var language = I2.Loc.LocalizationManager.CurrentLanguageCode;
            var revision = SpeechContext.VoiceCatalogRevision;
            if (_revision == revision && _language == language &&
                (_selection == voice || Array.IndexOf(_voices, voice) >= 0) &&
                _allLanguages == _showAllSpeechLanguages)
            {
                _selection = voice;
                return;
            }

            if (_revision >= 0 && Event.current.type != EventType.Layout)
            {
                return;
            }

            _revision = revision;
            _language = language;
            _selection = voice;
            _allLanguages = _showAllSpeechLanguages;
            _voices = npc
                ? SpeechContext.GetSelectableNpcVoiceNames(_allLanguages, voice)
                : SpeechContext.GetSelectableVoiceNames(_allLanguages, voice);
            List.Refresh(_voices, value => npc && value == SpeechContext.DefaultNpcVoice
                ? Gui.Localize("ModUi/&SpeechNpcAutomatic")
                : SpeechContext.GetVoiceDisplayName(value), revision);
        }
    }

    private static readonly SpeechVoicePicker[] ActorVoicePickers = Enumerable.Range(0, SpeechContext.MaxHeroes + 1)
        .Select(index => new SpeechVoicePicker($"SpeechVoiceSearch{index}", false)).ToArray();
    private static readonly SpeechVoicePicker NpcVoicePicker = new("SpeechNpcVoiceSearch", true);
    private static readonly UI.PagedList<(string InternalName, string DisplayName)> NpcList = new("SpeechNpcSearch", 24);

    internal static void DisplayGameUi()
    {
        #region Campaign

        UI.Label();
        UI.Label();

        var toggle = Main.Settings.NoExperienceOnLevelUp;
        if (UI.Toggle(Gui.Localize("ModUi/&NoExperienceOnLevelUp"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.NoExperienceOnLevelUp = toggle;
        }

        toggle = Main.Settings.OverrideMinMaxLevel;
        if (UI.Toggle(Gui.Localize("ModUi/&OverrideMinMaxLevel"), ref toggle))
        {
            Main.Settings.OverrideMinMaxLevel = toggle;
        }

        UI.Label();

        var floatValue = Main.Settings.FasterTimeModifier;
        if (UI.Slider(Gui.Localize("ModUi/&FasterTimeModifier"), ref floatValue,
                DefaultFastTimeModifier, 10f, DefaultFastTimeModifier, 1, string.Empty, UI.AutoWidth()))
        {
            Main.Settings.FasterTimeModifier = floatValue;
        }

        var intValue = Main.Settings.MultiplyTheExperienceGainedBy;
        if (UI.Slider(Gui.Localize("ModUi/&MultiplyTheExperienceGainedBy"), ref intValue, 0, 200, 100, string.Empty,
                UI.AutoWidth()))
        {
            Main.Settings.MultiplyTheExperienceGainedBy = intValue;
        }

        intValue = Main.Settings.OverridePartySize;
        if (UI.Slider(Gui.Localize("ModUi/&OverridePartySize"), ref intValue,
                ToolsContext.MinPartySize, ToolsContext.MaxPartySize,
                ToolsContext.GamePartySize, string.Empty, UI.AutoWidth()))
        {
            Main.Settings.OverridePartySize = intValue;

            while (Main.Settings.DefaultPartyHeroes.Count > intValue)
            {
                Main.Settings.DefaultPartyHeroes.RemoveAt(Main.Settings.DefaultPartyHeroes.Count - 1);
            }
        }

        if (Main.Settings.OverridePartySize > ToolsContext.GamePartySize)
        {
            toggle = Main.Settings.AllowAllPlayersOnNarrativeSequences;
            if (UI.Toggle(Gui.Localize("ModUi/&AllowAllPlayersOnNarrativeSequences"), ref toggle))
            {
                Main.Settings.AllowAllPlayersOnNarrativeSequences = toggle;
            }
        }

        UI.Label();

        toggle = Main.Settings.AddPickPocketableLoot;
        if (UI.Toggle(Gui.Localize("ModUi/&AddPickPocketableLoot"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AddPickPocketableLoot = toggle;
            if (toggle)
            {
                PickPocketContext.Load();
            }
        }

        toggle = Main.Settings.PickPocketNonHostiles;
        if (UI.Toggle(Gui.Localize("ModUi/&PickPocketNonHostiles"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.PickPocketNonHostiles = toggle;
        }

        toggle = Main.Settings.EnemySpellcastersDropScribedSpellbooks;
        if (UI.Toggle(Gui.Localize("ModUi/&EnemySpellcastersDropScribedSpellbooks"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnemySpellcastersDropScribedSpellbooks = toggle;
        }

        UI.Label();

        toggle = Main.Settings.AltOnlyHighlightItemsInPartyFieldOfView;
        if (UI.Toggle(Gui.Localize("ModUi/&AltOnlyHighlightItemsInPartyFieldOfView"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AltOnlyHighlightItemsInPartyFieldOfView = toggle;
        }

        toggle = Main.Settings.EnableAdditionalIconsOnLevelMap;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAdditionalIconsOnLevelMap"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAdditionalIconsOnLevelMap = toggle;

            if (toggle)
            {
                Main.Settings.MarkInvisibleTeleportersOnLevelMap = false;
            }
        }

        if (Main.Settings.EnableAdditionalIconsOnLevelMap)
        {
            toggle = Main.Settings.MarkInvisibleTeleportersOnLevelMap;
            if (UI.Toggle(Gui.Localize("ModUi/&MarkInvisibleTeleportersOnLevelMap"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.MarkInvisibleTeleportersOnLevelMap = toggle;
            }
        }

        toggle = Main.Settings.HideExitsAndTeleportersGizmosIfNotDiscovered;
        if (UI.Toggle(Gui.Localize("ModUi/&HideExitAndTeleporterGizmosIfNotDiscovered"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.HideExitsAndTeleportersGizmosIfNotDiscovered = toggle;
        }

        toggle = Main.Settings.EnableOutOfCombatTargetingSightLines;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableOutOfCombatTargetingSightLines"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableOutOfCombatTargetingSightLines = toggle;
        }

        UI.Label();

        toggle = Main.Settings.EnableLogDialoguesToConsole;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableLogDialoguesToConsole"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableLogDialoguesToConsole = toggle;
        }

        DisplaySpeechSettings();

        toggle = Main.Settings.EnableHeroWithBestProficiencyToRollChoice;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableHeroWithBestProficiencyToRollChoice"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableHeroWithBestProficiencyToRollChoice = toggle;
        }

        toggle = Main.Settings.EnableAlternateVotingSystem;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAlternateVotingSystem"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAlternateVotingSystem = toggle;

            if (!toggle)
            {
                Main.Settings.EnableSumD20OnAlternateVotingSystem = false;
            }
        }

        if (Main.Settings.EnableAlternateVotingSystem)
        {
            toggle = Main.Settings.EnableSumD20OnAlternateVotingSystem;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableSumD20OnAlternateVotingSystem"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableSumD20OnAlternateVotingSystem = toggle;
            }
        }

        UI.Label();

        toggle = Main.Settings.AllowMoreRealStateOnRestPanel;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowMoreRealStateOnRestPanel"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.AllowMoreRealStateOnRestPanel = toggle;
        }

        toggle = Main.Settings.EnableStatsOnHeroTooltip;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableStatsOnHeroTooltip"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableStatsOnHeroTooltip = toggle;
        }

        toggle = Main.Settings.EnableAdditionalBackstoryDisplay;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAdditionalBackstoryDisplay"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAdditionalBackstoryDisplay = toggle;
        }

        toggle = Main.Settings.EnableExtendedProficienciesPanelDisplay;
        var pools = InvocationPoolTypeCustom.Pools.All.Where(p => !p.Hidden)
            .Select(p => Gui.Localize(p.PanelTitle))
            .OrderBy(p => p)
            .Join(delimiter: Gui.ListSeparator());
        if (UI.Toggle(Gui.Format("ModUi/&EnableExtendedProficienciesPanelDisplay", pools), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableExtendedProficienciesPanelDisplay = toggle;
        }

        UI.Label();
        UI.Label();

        toggle = Main.Settings.HideMonsterHitPoints;
        if (UI.Toggle(Gui.Localize("ModUi/&HideMonsterHitPoints"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.HideMonsterHitPoints = toggle;
        }

        toggle = Main.Settings.RemoveBugVisualModels;
        if (UI.Toggle(Gui.Localize("ModUi/&RemoveBugVisualModels"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RemoveBugVisualModels = toggle;
        }

        toggle = Main.Settings.ShowChannelDivinityOnPortrait;
        if (UI.Toggle(Gui.Localize("ModUi/&ShowChannelDivinityOnPortrait"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ShowChannelDivinityOnPortrait = toggle;
        }

        toggle = Main.Settings.ShowButtonWithControlledMonsterInfo;
        if (UI.Toggle(Gui.Localize("ModUi/&ShowButtonWithControlledMonsterInfo"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ShowButtonWithControlledMonsterInfo = toggle;
            if (!toggle)
            {
                CustomCharacterStatsPanel.MaybeInstance?.Unbind();
            }
        }

        #endregion

        #region Combat

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&Combat"));
        UI.Label();

        toggle = Main.Settings.DontFollowCharacterInBattle;
        if (UI.Toggle(Gui.Localize("ModUi/&DontFollowCharacterInBattle"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.DontFollowCharacterInBattle = toggle;
        }

        if (Main.Settings.DontFollowCharacterInBattle)
        {
            intValue = Main.Settings.DontFollowMargin;
            if (UI.Slider(Gui.Localize("ModUi/&DontFollowMargin"), ref intValue, 0, 20,
                    1, "%", UI.AutoWidth()))
            {
                Main.Settings.DontFollowMargin = intValue;
            }

            toggle = Main.Settings.EnableElevationCameraToStayAtPosition;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableElevationCameraToStayAtPosition"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableElevationCameraToStayAtPosition = toggle;
            }

            toggle = Main.Settings.NeverMoveCameraOnEnemyTurn;
            if (UI.Toggle(Gui.Localize("ModUi/&NeverMoveCameraOnEnemyTurn"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.NeverMoveCameraOnEnemyTurn = toggle;
            }

            toggle = Main.Settings.EnableForceAllyMovementAllowsFlight;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableAllyMovementAllowsFlight"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableForceAllyMovementAllowsFlight = toggle;
            }

            UI.Label();
        }

        toggle = Main.Settings.EnableCancelEditOnRightMouseClick;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableCancelEditOnRightMouseClick"), ref toggle))
        {
            Main.Settings.EnableCancelEditOnRightMouseClick = toggle;
        }

        toggle = Main.Settings.EnableDistanceOnTooltip;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableTooltipDistance"), ref toggle))
        {
            Main.Settings.EnableDistanceOnTooltip = toggle;
        }

        toggle = Main.Settings.ShowMotionFormPreview;
        if (UI.Toggle(Gui.Localize("ModUi/&ShowMotionFormPreview"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ShowMotionFormPreview = toggle;
        }

        toggle = Main.Settings.EnableSmootherBattleAnimations;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSmootherBattleAnimations"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSmootherBattleAnimations = toggle;
        }

        if (Main.Settings.EnableSmootherBattleAnimations)
        {
            floatValue = CombatAnimationContext.ClampSpeedMultiplier(
                Main.Settings.BattleActionAnimationSpeedMultiplier);
            Main.Settings.BattleActionAnimationSpeedMultiplier = floatValue;

            if (UI.Slider(Gui.Localize("ModUi/&BattleActionAnimationSpeedMultiplier"), ref floatValue,
                    CombatAnimationContext.MinSpeedMultiplier,
                    CombatAnimationContext.MaxSpeedMultiplier,
                    CombatAnimationContext.DefaultSpeedMultiplier, 2, "x", UI.AutoWidth()))
            {
                Main.Settings.BattleActionAnimationSpeedMultiplier =
                    CombatAnimationContext.ClampSpeedMultiplier(floatValue);
            }

            UI.Label(Gui.Localize("ModUi/&BattleActionAnimationSpeedMultiplierHelp"));
        }

        UI.Label();

        var colorButtonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true };
        var color = CampaignsContext.HighContrastColorStrings[Main.Settings.HighContrastTargetingAoeSelectedColor];
        var targetingAoeTitle = Gui.Localize("ModUi/&HighContrastTargetingAoeColor").Replace("$$$$$$", color);

        color = CampaignsContext.HighContrastColorStrings[Main.Settings.HighContrastTargetingSingleSelectedColor];
        var targetingSingleTitle = Gui.Localize("ModUi/&HighContrastTargetingSingleColor").Replace("$$$$$$", color);

        color = CampaignsContext.GridColorStrings[Main.Settings.GridSelectedColor];
        var gridTitle = Gui.Localize("ModUi/&GridSelectedColor").Replace("$$$$$$", color);

        UI.HStack(null, 3, colorButtonStyle, false,
            new NamedAction(targetingAoeTitle, () => UI.ActionButton(targetingAoeTitle, () =>
            {
                Main.Settings.HighContrastTargetingAoeSelectedColor =
                    (Main.Settings.HighContrastTargetingAoeSelectedColor + 1) % CampaignsContext.HighContrastColors.Length;
            }, colorButtonStyle, UI.AutoWidth())),
            new NamedAction(targetingSingleTitle, () => UI.ActionButton(targetingSingleTitle, () =>
            {
                Main.Settings.HighContrastTargetingSingleSelectedColor =
                    (Main.Settings.HighContrastTargetingSingleSelectedColor + 1) %
                    CampaignsContext.HighContrastColors.Length;
            }, colorButtonStyle, UI.AutoWidth())),
            new NamedAction(gridTitle, () => UI.ActionButton(gridTitle, () =>
            {
                Main.Settings.GridSelectedColor =
                    (Main.Settings.GridSelectedColor + 1) % CampaignsContext.GridColors.Length;
                CampaignsContext.UpdateMovementGrid();
            }, colorButtonStyle, UI.AutoWidth())));

        intValue = Main.Settings.MovementGridWidthModifier;
        if (UI.Slider(Gui.Localize("ModUi/&MovementGridWidthModifier"), ref intValue, 0, 200, 100, string.Empty,
                UI.AutoWidth()))
        {
            Main.Settings.MovementGridWidthModifier = intValue;
            CampaignsContext.UpdateMovementGrid();
        }

        intValue = Main.Settings.OutlineGridWidthModifier;
        if (UI.Slider(Gui.Localize("ModUi/&OutlineGridWidthModifier"), ref intValue, 0, 200, 100, string.Empty,
                UI.AutoWidth()))
        {
            Main.Settings.OutlineGridWidthModifier = intValue;
            CampaignsContext.UpdateMovementGrid();
        }

        intValue = Main.Settings.OutlineGridWidthSpeed;
        if (UI.Slider(Gui.Localize("ModUi/&OutlineGridWidthSpeed"), ref intValue, 0, 200, 100, string.Empty,
                UI.AutoWidth()))
        {
            Main.Settings.OutlineGridWidthSpeed = intValue;
            CampaignsContext.UpdateMovementGrid();
        }

        #endregion

        #region Factions

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&FactionRelations"));
        UI.Label();

        var flip = true;
        var gameCampaign = Gui.GameCampaign;
        var gameFactionService = ServiceRepository.GetService<IGameFactionService>();

        // NOTE: don't use gameCampaign?. which bypasses Unity object lifetime check
        if (gameCampaign)
        {
            if (gameFactionService != null)
            {
                foreach (var faction in gameFactionService.RegisteredFactions.Where(faction =>
                             !faction.BuiltIn &&
                             !faction.GuiPresentation.Hidden))
                {
                    var title = faction.FormatTitle();
                    title = flip ? title.Khaki() : title.White();

                    intValue = gameFactionService.FactionRelations[faction.Name];

                    if (UI.Slider("                              " + title, ref intValue, faction.MinRelationCap,
                            faction.MaxRelationCap, 0, "", UI.AutoWidth()))
                    {
                        SetFactionRelation(faction.Name, intValue);
                    }

                    flip = !flip;
                }
            }
            else
            {
                UI.Label(Gui.Localize("ModUi/&FactionHelp"));
            }
        }
        else
        {
            UI.Label(Gui.Localize("ModUi/&FactionHelp"));
        }

        #endregion

        #region Formation

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&Formation"));
        UI.Label();

        if (Global.IsMultiplayer)
        {
            UI.Label(Gui.Localize("ModUi/&FormationError"));
        }
        else
        {
            DisplayFormationGrid();
        }

        #endregion

        #region Merchants

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&Merchants"));
        UI.Label();

        toggle = Main.Settings.ScaleMerchantPricesCorrectly;
        if (UI.Toggle(Gui.Localize("ModUi/&ScaleMerchantPricesCorrectly"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ScaleMerchantPricesCorrectly = toggle;
        }

        toggle = Main.Settings.StockGorimStoreWithAllNonMagicalClothing;
        if (UI.Toggle(Gui.Localize("ModUi/&StockGorimStoreWithAllNonMagicalClothing"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.StockGorimStoreWithAllNonMagicalClothing = toggle;
        }

        toggle = Main.Settings.StockGorimStoreWithAllNonMagicalInstruments;
        if (UI.Toggle(Gui.Localize("ModUi/&StockGorimStoreWithAllNonMagicalInstruments"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.StockGorimStoreWithAllNonMagicalInstruments = toggle;
        }

        toggle = Main.Settings.StockHugoStoreWithAdditionalFoci;
        if (UI.Toggle(Gui.Localize("ModUi/&StockHugoStoreWithAdditionalFoci"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.StockHugoStoreWithAdditionalFoci = toggle;
            Main.Settings.EnableAdditionalFociInDungeonMaker = toggle;
            ItemCraftingMerchantContext.SwitchFociItems();
        }

        if (Main.Settings.StockHugoStoreWithAdditionalFoci)
        {
            toggle = Main.Settings.EnableAdditionalFociInDungeonMaker;
            if (UI.Toggle(Gui.Localize("ModUi/&EnableAdditionalItemsInDungeonMaker"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableAdditionalFociInDungeonMaker = toggle;
                ItemCraftingMerchantContext.SwitchFociItemsDungeonMaker();
            }
        }

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&RestockHelp"));
        UI.Label();

        toggle = Main.Settings.RestockAntiquarians;
        if (UI.Toggle(Gui.Localize("ModUi/&RestockAntiquarians"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RestockAntiquarians = toggle;
            ItemCraftingMerchantContext.SwitchRestockAntiquarian();
        }

        toggle = Main.Settings.RestockArcaneum;
        if (UI.Toggle(Gui.Localize("ModUi/&RestockArcaneum"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RestockArcaneum = toggle;
            ItemCraftingMerchantContext.SwitchRestockArcaneum();
        }

        toggle = Main.Settings.RestockCircleOfDanantar;
        if (UI.Toggle(Gui.Localize("ModUi/&RestockCircleOfDanantar"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RestockCircleOfDanantar = toggle;
            ItemCraftingMerchantContext.SwitchRestockCircleOfDanantar();
        }

        toggle = Main.Settings.RestockTowerOfKnowledge;
        // ReSharper disable once InvertIf
        if (UI.Toggle(Gui.Localize("ModUi/&RestockTowerOfKnowledge"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.RestockTowerOfKnowledge = toggle;
            ItemCraftingMerchantContext.SwitchRestockTowerOfKnowledge();
        }

        #endregion

        UI.Label();
    }

    private static void DisplaySpeechSettings()
    {
        var toggle = Main.Settings.EnableSpeech;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSpeech"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSpeech = toggle;
        }

        toggle = Main.Settings.EnableSpeechOnNpcs;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSpeechOnNpcs"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSpeechOnNpcs = toggle;
        }

        toggle = Main.Settings.EnableSpeechOnInteractions;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSpeechOnInteractions"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableSpeechOnInteractions = toggle;
        }

        if (!Main.Settings.EnableSpeech && !Main.Settings.EnableSpeechOnNpcs &&
            !Main.Settings.EnableSpeechOnInteractions)
        {
            return;
        }

        SpeechContext.EnsureVoiceDataInitialized();

        var labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        var buttonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true, richText = true };

        UI.Label();
        toggle = Main.Settings.EnableVoiceVoxSpeech;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableVoiceVoxSpeech"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableVoiceVoxSpeech = toggle;
            SpeechContext.RefreshAvailableVoices();
        }

        if (Main.Settings.EnableVoiceVoxSpeech)
        {
            UI.Label(Gui.Localize("ModUi/&SpeechVoiceVoxHelp"), labelStyle, UI.ExpandWidth(true));
            var status = SpeechContext.VoiceVoxRefreshInProgress
                ? Gui.Localize("ModUi/&SpeechVoiceVoxConnecting")
                : SpeechContext.VoiceVoxEngineAvailable
                    ? Gui.Format("ModUi/&SpeechVoiceVoxConnected", SpeechContext.AvailableVoiceVoxVoiceCount.ToString())
                    : Gui.Localize("ModUi/&SpeechVoiceVoxUnavailable");
            UI.Label(status, labelStyle, UI.ExpandWidth(true));
            UI.Label(SpeechContext.GetVoiceVoxSetupStatus(), labelStyle, UI.ExpandWidth(true));
            UI.Label(Gui.Localize("ModUi/&SpeechVoiceVoxInstallHelp"), labelStyle, UI.ExpandWidth(true));
            var setupLabel = SpeechContext.VoiceVoxSetupInProgress
                ? Gui.Localize("ModUi/&SpeechVoiceVoxCancel") : SpeechContext.GetVoiceVoxInstallLabel();
            var termsLabel = Gui.Localize("ModUi/&SpeechVoiceVoxTerms");
            UI.HStack(null, 2, buttonStyle,
                new NamedAction(setupLabel, () =>
                {
                    var wasEnabled = GUI.enabled;
                    try
                    {
                        GUI.enabled = wasEnabled &&
                                      (SpeechContext.CanSetUpVoiceVox || SpeechContext.VoiceVoxSetupInProgress);
                        UI.ActionButton(setupLabel, () =>
                        {
                            if (SpeechContext.VoiceVoxSetupInProgress)
                            {
                                SpeechContext.CancelVoiceVoxSetup();
                            }
                            else
                            {
                                SpeechContext.InstallVoiceVox();
                            }
                        }, buttonStyle, UI.ExpandWidth(true));
                    }
                    finally
                    {
                        GUI.enabled = wasEnabled;
                    }
                }),
                new NamedAction(termsLabel, () => UI.ActionButton(termsLabel,
                    () => Application.OpenURL("https://voicevox.hiroshiba.jp/"),
                    buttonStyle, UI.ExpandWidth(true))));
        }

        var refreshLabel = Gui.Localize("ModUi/&RefreshVoice");
        var downloadLabel = SpeechContext.VoicesDownloader.Shared.GetButtonLabel();
        var downloadActions = new[]
        {
            new NamedAction(refreshLabel, () => UI.ActionButton(refreshLabel,
                SpeechContext.RefreshAvailableVoices, buttonStyle, UI.ExpandWidth(true))),
            new NamedAction(downloadLabel, () => UI.ActionButton(downloadLabel,
                SpeechContext.VoicesDownloader.Shared.DownloadVoices, buttonStyle, UI.ExpandWidth(true)))
        }.Concat(SpeechContext.DownloadableLanguageProfiles.Select(profile =>
        {
            var text = SpeechContext.PiperPlusVoiceDownloader.Shared.GetButtonLabel(profile);
            return new NamedAction(text, () => UI.ActionButton(text,
                () => SpeechContext.PiperPlusVoiceDownloader.Shared.DownloadVoice(profile),
                buttonStyle, UI.ExpandWidth(true)));
        })).ToArray();
        UI.HStack(null, downloadActions.Length, buttonStyle, downloadActions);

        UI.Label(Gui.Localize("ModUi/&EnableSpeechAvailableVoiceHelp"), labelStyle, UI.ExpandWidth(true));
        UI.Label();

        toggle = Main.Settings.ForceModSpeechOnNpcs;
        var guiEnabled = GUI.enabled;
        GUI.enabled = guiEnabled && Main.Settings.EnableSpeechOnNpcs;
        if (UI.Toggle(Gui.Localize("ModUi/&ForceModSpeechOnNpcs"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.ForceModSpeechOnNpcs = toggle;
        }
        GUI.enabled = guiEnabled;

        UI.Label();
        UI.Toggle(Gui.Localize("ModUi/&SpeechShowAllLanguages"), ref _showAllSpeechLanguages, UI.AutoWidth());
        UI.Label(Gui.Localize("ModUi/&EnableSpeechActorHelp"), labelStyle, UI.ExpandWidth(true));
        var actor = Main.Settings.SpeechChoice;
        if (UI.SelectionGrid(ref actor, SpeechContext.Choices, SpeechContext.Choices.Length,
                buttonStyle, SpeechContext.Choices.Length, UI.ExpandWidth(true)))
        {
            Main.Settings.SpeechChoice = actor;
        }

        UI.Label(Gui.Localize("ModUi/&SpeechAutomaticHelp"), labelStyle, UI.ExpandWidth(true));
        UI.ActionButton(Gui.Localize("ModUi/&SpeechAssignAllAutomatic"), () =>
        {
            for (var index = 0; index <= SpeechContext.MaxHeroes; index++)
            {
                var (_, scale) = Main.Settings.SpeechVoices[index];
                Main.Settings.SpeechVoices[index] = (SpeechContext.DefaultNpcVoice, scale);
            }
            SpeechContext.UpdateAvailableVoices();
        }, buttonStyle, UI.ExpandWidth(true));

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&EnableSpeechVoiceHelp"), labelStyle, UI.ExpandWidth(true));

        var (voice, speed) = Main.Settings.SpeechVoices[actor];
        var previousVoice = voice;
        var pitch = Main.Settings.SpeechVoicePitches[actor];
        var intonation = Main.Settings.SpeechVoiceIntonations[actor];
        if (DisplaySpeechVoiceSettings(ref voice, ref speed, ref pitch, ref intonation,
                false, labelStyle, buttonStyle, actor))
        {
            Main.Settings.SpeechVoices[actor] = (voice, speed);
            Main.Settings.SpeechVoicePitches[actor] = pitch;
            Main.Settings.SpeechVoiceIntonations[actor] = intonation;
            if (voice != previousVoice)
            {
                SpeechContext.UpdateAvailableVoices();
            }
        }

        UI.ActionButton(Gui.Localize("ModUi/&SpeechPreview"), SpeechContext.SpeakQuote,
            buttonStyle, UI.AutoWidth());

        var volume = Main.Settings.SpeechVolume;
        if (UI.Slider(Gui.Localize("ModUi/&SpeechVolume"), ref volume,
                0f, 1f, 0.8f, 1, string.Empty, UI.AutoWidth()))
        {
            Main.Settings.SpeechVolume = volume;
        }

        if (Main.Settings.EnableSpeechOnNpcs)
        {
            DisplayNpcSpeechSettings(labelStyle, buttonStyle);
        }

        UI.Label();
    }

    private static bool DisplaySpeechVoiceSettings(ref string voice, ref float speed, ref float pitch,
        ref float intonation, bool npc, GUIStyle labelStyle, GUIStyle buttonStyle, int actor = -1)
    {
        var guiEnabled = GUI.enabled;
        var changed = false;
        var effectiveVoice = npc ? voice : SpeechContext.GetEffectiveSpeechVoice(actor);
        try
        {
            GUI.enabled = guiEnabled && (!npc || voice != SpeechContext.DefaultNpcVoice);
            changed |= UI.Slider(Gui.Localize("ModUi/&SpeechScale"), ref speed,
                0.5f, 2f, SpeechContext.DefaultScale, 2, string.Empty, UI.AutoWidth());
            if (UI.Slider(Gui.Localize("ModUi/&SpeechPitch"), ref pitch,
                    -6f, 6f, 0f, 1, string.Empty, UI.AutoWidth()))
            {
                pitch = Mathf.Round(pitch * 2f) / 2f;
                changed = true;
            }
            UI.Label(Gui.Localize("ModUi/&EnableSpeechPitchHelp"), labelStyle, UI.ExpandWidth(true));

            GUI.enabled = guiEnabled && SpeechContext.IsVoiceVoxVoice(effectiveVoice);
            changed |= UI.Slider(Gui.Localize("ModUi/&SpeechIntonation"), ref intonation,
                0f, 2f, 1f, 2, string.Empty, UI.AutoWidth());
        }
        finally
        {
            GUI.enabled = guiEnabled;
        }

        UI.Label(Gui.Localize(SpeechContext.IsVoiceVoxVoice(effectiveVoice)
                ? "ModUi/&SpeechIntonationHelp"
                : "ModUi/&SpeechIntonationUnavailable"), labelStyle, UI.ExpandWidth(true));
        var selectedLabel = npc && voice == SpeechContext.DefaultNpcVoice
            ? Gui.Localize("ModUi/&SpeechNpcAutomatic")
            : SpeechContext.GetVoiceDisplayName(voice);
        UI.Label(Gui.Format("ModUi/&SettingsListSelected", selectedLabel),
            labelStyle, UI.ExpandWidth(true));
        if (!npc && voice == SpeechContext.DefaultNpcVoice)
        {
            UI.Label(Gui.Format("ModUi/&SpeechAutomaticResolved", SpeechContext.GetVoiceDisplayName(effectiveVoice)),
                labelStyle, UI.ExpandWidth(true));
        }
        var picker = npc ? NpcVoicePicker : ActorVoicePickers[actor];
        picker.Refresh(voice);
        picker.List.DrawControls(labelStyle);
        if (picker.List.DrawSelection(ref voice, buttonStyle))
        {
            changed = true;
        }

        if (voice != SpeechContext.DefaultVoice && voice != SpeechContext.DefaultNpcVoice)
        {
            UI.Label(Gui.Localize("ModUi/&SpeechVoiceGender"), labelStyle, UI.ExpandWidth(true));
            var gender = SpeechContext.GetVoiceGenderOverride(voice);
            var genderLabels = new[]
            {
                Gui.Localize("ModUi/&SpeechVoiceGenderDefault"),
                Gui.Localize("ModUi/&SpeechVoiceGenderMale"),
                Gui.Localize("ModUi/&SpeechVoiceGenderFemale"),
                Gui.Localize("ModUi/&SpeechVoiceGenderUnknown")
            };
            if (UI.SelectionGrid(ref gender, genderLabels, genderLabels.Length, buttonStyle,
                    genderLabels.Length, UI.ExpandWidth(true)))
            {
                SpeechContext.SetVoiceGenderOverride(voice, gender);
            }
            UI.Label(Gui.Localize("ModUi/&SpeechVoiceGenderHelp"), labelStyle, UI.ExpandWidth(true));
        }

        return changed;
    }

    private static void DisplayNpcSpeechSettings(GUIStyle labelStyle, GUIStyle buttonStyle)
    {
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&SpeechNpcHelp"), labelStyle, UI.ExpandWidth(true));
        var npcs = SpeechContext.GetCurrentCampaignNpcs();
        if (npcs.Count == 0)
        {
            _selectedSpeechNpc = null;
            UI.Label(Gui.Localize("ModUi/&SpeechNpcLoadCampaign"), labelStyle, UI.ExpandWidth(true));
            return;
        }

        var selectedNpc = npcs.FirstOrDefault(npc => npc.InternalName == _selectedSpeechNpc);
        if (selectedNpc.InternalName == null)
        {
            selectedNpc = npcs[0];
        }
        UI.Label(Gui.Format("ModUi/&SettingsListSelected", selectedNpc.DisplayName),
            labelStyle, UI.ExpandWidth(true));
        NpcList.Refresh(npcs, npc => npc.DisplayName);
        NpcList.DrawControls(labelStyle);
        NpcList.DrawSelection(ref selectedNpc, buttonStyle);
        _selectedSpeechNpc = selectedNpc.InternalName;
        var (voice, speed, pitch, intonation) = SpeechContext.GetNpcSpeechSettings(_selectedSpeechNpc);
        if (DisplaySpeechVoiceSettings(ref voice, ref speed, ref pitch, ref intonation,
                true, labelStyle, buttonStyle))
        {
            SpeechContext.SetNpcSpeechSettings(_selectedSpeechNpc, voice, speed, pitch, intonation);
        }

        UI.ActionButton(Gui.Localize("ModUi/&SpeechPreview"),
            () => SpeechContext.SpeakNpcQuote(_selectedSpeechNpc), buttonStyle, UI.AutoWidth());
    }

    private static void SetFactionRelation(string name, int value)
    {
        var service = ServiceRepository.GetService<IGameFactionService>();

        service?.ExecuteFactionOperation(name, FactionDefinition.FactionOperation.Increase,
            value - service.FactionRelations[name], "",
            null /* this string and monster doesn't matter if we're using "SetValue" */);
    }

    private static void DisplayFormationGrid()
    {
        var selectedSet = Main.Settings.FormationGridSelectedSet;
        var resetAll = Gui.Localize("ModUi/&FormationResetAllSets");
        var resetSet = Gui.Localize("ModUi/&FormationResetThisSet");
        var resetWidth = Mathf.Max(110f, GUI.skin.button.CalcSize(new GUIContent(resetAll)).x,
            GUI.skin.button.CalcSize(new GUIContent(resetSet)).x);
        var cellWidth = Mathf.Max(30f, GUI.skin.button.CalcSize(new GUIContent("@".Bold())).x,
            GUI.skin.button.CalcSize(new GUIContent("..".Bold())).x);

        using (UI.HorizontalScope())
        {
            UI.ActionButton(resetAll, () =>
                {
                    _selectedForSwap = false;
                    CampaignsContext.ResetAllFormationGrids();
                },
                UI.Width(resetWidth));

            if (UI.SelectionGrid(ref selectedSet, SetNames, SetNames.Length, SetNames.Length,
                    UI.Width(UnityModManagerNet.UnityModManager.UI.Scale(165))))
            {
                _selectedForSwap = false;
                Main.Settings.FormationGridSelectedSet = selectedSet;
                CampaignsContext.FillDefinitionFromFormationGrid();
            }

            UI.Label(Gui.Localize("ModUi/&FormationHelp1"));
        }

        UI.Label();

        for (var y = 0; y < CampaignsContext.GridSize; y++)
        {
            using (UI.HorizontalScope())
            {
                // first line
                if (y == 0)
                {
                    UI.ActionButton(resetSet, () =>
                        {
                            _selectedForSwap = false;
                            CampaignsContext.ResetFormationGrid(Main.Settings.FormationGridSelectedSet);
                        },
                        UI.Width(resetWidth));
                }
                else
                {
                    UI.Label("", UI.Width(resetWidth));
                }

                for (var x = 0; x < CampaignsContext.GridSize; x++)
                {
                    var saveColor = GUI.color;
                    string label;

                    if (Main.Settings.FormationGridSets[selectedSet][y][x] == 1)
                    {
                        // yep 256 not 255 for a light contrast
                        GUI.color = new Color(0x1E / 256f, 0x81 / 256f, 0xB0 / 256f);
                        label = "@";
                    }
                    else
                    {
                        label = "..";
                    }

                    if (_selectedForSwap && _selectedX == x && _selectedY == y)
                    {
                        label = $"<b><color=red>{label}</color></b>";
                    }

                    UI.ActionButton(label, () =>
                    {
                        // ReSharper disable once InlineTemporaryVariable
                        // ReSharper disable once AccessToModifiedClosure
                        var localX = x;
                        // ReSharper disable once InlineTemporaryVariable
                        // ReSharper disable once AccessToModifiedClosure
                        var localY = y;

                        if (_selectedForSwap)
                        {
                            (Main.Settings.FormationGridSets[selectedSet][localY][localX],
                                Main.Settings.FormationGridSets[selectedSet][_selectedY][_selectedX]) = (
                                Main.Settings.FormationGridSets[selectedSet][_selectedY][_selectedX],
                                Main.Settings.FormationGridSets[selectedSet][localY][localX]);

                            CampaignsContext.FillDefinitionFromFormationGrid();

                            _selectedForSwap = false;
                        }
                        else
                        {
                            _selectedX = localX;
                            _selectedY = localY;
                            _selectedForSwap = true;
                        }
                    }, UI.Width(cellWidth));

                    GUI.color = saveColor;
                }

                // first line
                if (y <= 1)
                {
                    UI.Label(Gui.Localize("ModUi/&FormationHelp" + (y + 2)));
                }
            }
        }
    }
}
