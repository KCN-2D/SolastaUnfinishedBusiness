using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Models.TranslationServices;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Displays;

internal static class DungeonMakerDisplay
{
    private static bool _showTranslateServiceSettings;
    private static bool _showOpenAIAdvancedSettings;
    private static bool _showCategoryProgress;
    private static string _openAIApiKeyInput = string.Empty;
    private static bool _apiKeyInitialized;
    private static List<UserCampaign> _translationCampaignSource;
    private static TranslationCampaignRow[] _translationCampaignSnapshot = [];
    private static TranslationCampaignRow[] _translationCampaignRows = [];
    private static string _translationCatalogLanguage;
    private static int _translationCatalogStyle;
    private static float[] _translationColumnWidths = [1f, 1f, 1f];
    private static float[] _translationColumnMinimumWidths = [1f, 1f, 1f];
    private static readonly Dictionary<string, string> TranslationCaptions = new();
    private static readonly Dictionary<(string Name, bool Button), float> TranslationCaptionWidths = new();

    private sealed class TranslationCampaignRow(UserCampaign campaign)
    {
        internal readonly UserCampaign Campaign = campaign;
        internal readonly string Author = campaign?.Author ?? string.Empty;
        internal readonly string Title = campaign?.Title ?? string.Empty;
        internal readonly string TechnicalInfo = campaign?.TechnicalInfo ?? string.Empty;
        internal readonly string AuthorLabel = (campaign?.Author ?? string.Empty)
            .Substring(0, Math.Min(20, campaign?.Author?.Length ?? 0)).Bold().Orange();
        internal readonly string TitleLabel = (campaign?.Title ?? string.Empty).Bold().Italic();
        internal bool TaskPrepared;
        internal CampaignTranslationTask Task;
        internal NamedAction[] TaskControls;
        internal float[] TaskControlWidths;
        internal string ControlsTitle;
        internal bool ShowProgress;

        internal bool Matches(UserCampaign other)
        {
            return ReferenceEquals(Campaign, other) && Author == (other?.Author ?? string.Empty) &&
                   Title == (other?.Title ?? string.Empty) && TechnicalInfo == (other?.TechnicalInfo ?? string.Empty);
        }
    }

    private sealed class TranslationTaskSnapshot(CampaignTranslationTask task)
    {
        internal readonly CampaignTranslationStatus Status = task.Status;
        internal readonly bool HasFailedItems = task.FailedItems > 0;
        internal readonly float PercentageComplete = task.PercentageComplete;
    }

    private static readonly string[] MonsterDocumentationFamilies =
    [
        "Aberration", "Beast", "Celestial", "Construct", "Dragon", "Elemental",
        "Fey", "Fiend", "Giant", "Humanoid", "Monstrosity", "Undead"
    ];

    internal static void DisplayDungeonMaker()
    {
        var labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        var buttonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true };
        UI.Label();
        var selectedFamily = -1;
        var documentationLabels = MonsterDocumentationFamilies
            .Select(family => Gui.Localize($"CharacterFamily/&{family}Title").Bold().Khaki()).ToArray();
        UI.ActionSelectionGrid(ref selectedFamily, documentationLabels, 3,
            index => UpdateContext.OpenDocumentation($"Monsters/SolastaMonsters{MonsterDocumentationFamilies[index]}.md"),
            new GUIStyle(GUI.skin.button) { wordWrap = true, richText = true }, UI.ExpandWidth(true));

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&Basic"));
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&DungeonMakerBasicHelp"), labelStyle, UI.ExpandWidth(true));
        UI.Label();

        var toggle = Main.Settings.EnableLoggingInvalidReferencesInUserCampaigns;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableLoggingInvalidReferencesInUserCampaigns"), ref toggle))
        {
            Main.Settings.EnableLoggingInvalidReferencesInUserCampaigns = toggle;
        }

        toggle = Main.Settings.EnableSortingDungeonMakerAssets;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableSortingDungeonMakerAssets"), ref toggle))
        {
            Main.Settings.EnableSortingDungeonMakerAssets = toggle;
        }

        toggle = Main.Settings.AllowGadgetsAndPropsToBePlacedAnywhere;
        if (UI.Toggle(Gui.Localize("ModUi/&AllowGadgetsAndPropsToBePlacedAnywhere"), ref toggle))
        {
            Main.Settings.AllowGadgetsAndPropsToBePlacedAnywhere = toggle;
        }

        toggle = Main.Settings.UnleashEnemyAsNpc;
        if (UI.Toggle(Gui.Localize("ModUi/&UnleashEnemyAsNpc"), ref toggle))
        {
            Main.Settings.UnleashEnemyAsNpc = toggle;
        }

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&Advanced"));
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&AdvancedHelp"), labelStyle, UI.ExpandWidth(true));
        UI.Label();

        toggle = Main.Settings.AddNewWeaponsAndRecipesToEditor;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAdditionalItemsInDungeonMaker"), ref toggle,
                UI.AutoWidth()))
        {
            Main.Settings.AddNewWeaponsAndRecipesToEditor = toggle;
        }

        toggle = Main.Settings.UnleashNpcAsEnemy;
        if (UI.Toggle(Gui.Localize("ModUi/&UnleashNpcAsEnemy"), ref toggle))
        {
            Main.Settings.UnleashNpcAsEnemy = toggle;
        }

        toggle = Main.Settings.EnableVariablePlaceholdersOnTexts;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableVariablePlaceholdersOnTexts"), ref toggle))
        {
            Main.Settings.EnableVariablePlaceholdersOnTexts = toggle;
        }

        toggle = Main.Settings.EnableDungeonMakerModdedContent;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableDungeonMakerModdedContent"), ref toggle))
        {
            Main.Settings.EnableDungeonMakerModdedContent = toggle;
        }

        UI.Label();

        UI.Label();
        UI.Label(Gui.Format("ModUi/&Translations"));
        UI.Label();
        
        var concurrency = Main.Settings.TranslationConcurrency;
        if (UI.Slider(Gui.Localize("ModUi/&TranslationConcurrency"), ref concurrency,
                1, CampaignTranslationExecutor.MaxTranslationConcurrency, 1, string.Empty, UI.AutoWidth()))
        {
            Main.Settings.TranslationConcurrency = concurrency;
        }
        
        var serviceIndex = (int)Main.Settings.SelectedTranslationService;
        if (UI.SelectionGrid(Gui.Localize("ModUi/&TranslationService"), ref serviceIndex,
                TranslationServiceFactory.ServiceNames, TranslationServiceFactory.ServiceNames.Length,
                UI.ExpandWidth(true)))
        {
            Main.Settings.SelectedTranslationService = (TranslationServiceType)serviceIndex;
        }

        switch (Main.Settings.SelectedTranslationService)
        {
            case TranslationServiceType.Google:
                DisplayGoogleSettings();
                break;
            case TranslationServiceType.OpenAI:
                DisplayOpenAISettings();
                break;
        }

        var translationService = TranslationServiceFactory.GetCurrentService();
        if (!translationService.IsConfigured())
        {
            //Show error that translation service is not configured
            UI.Label(Gui.Format("ModUi/&TranslationServiceNotConfigured", translationService.Name).Bold().Red(),
                labelStyle, UI.ExpandWidth(true));
            UI.Label();
            return;
        }

        UI.Label();


        var intValue = Array.IndexOf(TranslatorContext.AvailableLanguages, Main.Settings.SelectedLanguageCode);
        if (UI.SelectionGrid(Gui.Localize("ModUi/&TargetLanguage"), ref intValue,
                TranslatorContext.AvailableLanguages, TranslatorContext.AvailableLanguages.Length,
                UI.ExpandWidth(true)))
        {
            Main.Settings.SelectedLanguageCode = TranslatorContext.AvailableLanguages[intValue];
        }

        UI.Label();

        var rowMargin = new RectOffset(Math.Max(labelStyle.margin.left, buttonStyle.margin.left),
            Math.Max(labelStyle.margin.right, buttonStyle.margin.right),
            Math.Max(labelStyle.margin.top, buttonStyle.margin.top),
            Math.Max(labelStyle.margin.bottom, buttonStyle.margin.bottom));
        var rowLabelStyle = new GUIStyle(labelStyle) { margin = rowMargin };
        var rowButtonStyle = new GUIStyle(buttonStyle) { margin = rowMargin };
        var campaigns = ServiceRepository.GetService<IUserCampaignPoolService>()?.AllCampaigns;
        RefreshTranslationCatalog(campaigns, rowLabelStyle, rowButtonStyle);

        foreach (var row in _translationCampaignRows)
        {
            var userCampaign = row.Campaign;
            var campaignTitle = row.Title;
            var translateTitle = TranslationCaptions["Translate"];
            if (!row.TaskPrepared || Event.current.type == EventType.Layout)
            {
                row.TaskPrepared = true;
                row.Task = CampaignTranslationExecutor.GetTask(campaignTitle);
                // Background task updates must not mix one status' controls with another
                // status' widths or progress visibility in the same IMGUI Layout.
                var snapshot = row.Task == null ? null : new TranslationTaskSnapshot(row.Task);
                row.TaskControls = row.Task == null
                    ? null
                    : CreateTranslationTaskControls(row.Task, userCampaign, rowLabelStyle, rowButtonStyle, snapshot);
                row.TaskControlWidths = row.TaskControls?.Select((control, index) =>
                    index == 0 ? TranslationCaptionWidths[(GetTranslationStatusKey(snapshot), false)] :
                    TranslationCaptionWidths[(control.Name, true)]).ToArray();
                row.ControlsTitle = row.TaskControls == null
                    ? translateTitle
                    : string.Join(" ", row.TaskControls.Select(control => control.Name));
                row.ShowProgress = snapshot?.Status is CampaignTranslationStatus.Running or CampaignTranslationStatus.Paused;
            }

            UI.AlignedRow(1, _translationColumnWidths, _translationColumnMinimumWidths,
                new NamedAction(row.AuthorLabel, () => UI.Label(row.AuthorLabel, rowLabelStyle, UI.ExpandWidth(true))),
                new NamedAction(row.TitleLabel, () => UI.Label(row.TitleLabel, rowLabelStyle, UI.ExpandWidth(true))),
                new NamedAction(row.ControlsTitle, () =>
                {
                    if (row.TaskControls != null)
                    {
                        UI.AlignedActions(row.TaskControlWidths, row.TaskControls);
                    }
                    else
                    {
                        UI.AlignedActions([TranslationCaptionWidths[(translateTitle, true)]],
                            new NamedAction(translateTitle, () => UI.ActionButton(translateTitle,
                                () => CampaignTranslationExecutor.StartTranslation(
                                    Main.Settings.SelectedLanguageCode, campaignTitle, userCampaign),
                                rowButtonStyle, UI.AutoWidth())));
                    }
                }));

            if (row.ShowProgress)
            {
                DisplayTranslationProgress(row.Task);
            }
        }

        UI.Label();
    }

    private static void RefreshTranslationCatalog(List<UserCampaign> campaigns, GUIStyle labelStyle, GUIStyle buttonStyle)
    {
        if (_translationCatalogLanguage != null && Event.current.type != EventType.Layout)
        {
            return;
        }

        var sourceChanged = !ReferenceEquals(campaigns, _translationCampaignSource) ||
                            (campaigns?.Count ?? 0) != _translationCampaignSnapshot.Length;
        if (!sourceChanged && campaigns != null)
        {
            for (var index = 0; index < campaigns.Count; index++)
            {
                if (_translationCampaignSnapshot[index].Matches(campaigns[index]))
                {
                    continue;
                }

                sourceChanged = true;
                break;
            }
        }

        var language = I2.Loc.LocalizationManager.CurrentLanguageCode ?? string.Empty;
        var style = (GUI.skin.GetInstanceID(), labelStyle.font ?? GUI.skin.font, labelStyle.fontSize,
            labelStyle.fontStyle, labelStyle.margin.horizontal, labelStyle.margin.vertical, labelStyle.padding.horizontal,
            buttonStyle.font ?? GUI.skin.font, buttonStyle.fontSize, buttonStyle.fontStyle,
            buttonStyle.margin.horizontal, buttonStyle.margin.vertical, buttonStyle.padding.horizontal).GetHashCode();
        if (!sourceChanged && language == _translationCatalogLanguage && style == _translationCatalogStyle)
        {
            return;
        }

        if (sourceChanged)
        {
            _translationCampaignSource = campaigns;
            _translationCampaignSnapshot = campaigns?.Select(campaign => new TranslationCampaignRow(campaign)).ToArray() ?? [];
        }

        _translationCampaignRows = _translationCampaignSnapshot
            .Where(row => row.Campaign != null &&
                          !row.TechnicalInfo.StartsWith(CampaignTranslationExecutor.UbTranslationTag, StringComparison.Ordinal))
            .OrderBy(row => row.Author).ThenBy(row => row.Title).ToArray();
        _translationCatalogLanguage = language;
        _translationCatalogStyle = style;
        TranslationCaptions.Clear();
        TranslationCaptionWidths.Clear();
        foreach (var key in new[]
                 {
                     "Translate", "Pause", "Resume", "Cancel", "RetryFailed", "TranslationPaused",
                     "TranslationCompletedWithErrors", "TranslationCompleted", "TranslationCancelled", "TranslationFailed"
                 })
        {
            var text = Gui.Localize($"ModUi/&{key}");
            TranslationCaptions[key] = text;
            var button = !key.StartsWith("Translation", StringComparison.Ordinal);
            var captionStyle = button ? buttonStyle : labelStyle;
            var markup = captionStyle == labelStyle ? text.Bold() : text;
            // A status and button can have identical translated text but different
            // padding. Keep their measurements separate in every language.
            TranslationCaptionWidths[(key, button)] =
                captionStyle.CalcSize(new GUIContent(markup)).x + captionStyle.margin.horizontal;
            if (button)
            {
                TranslationCaptionWidths[(text, true)] = TranslationCaptionWidths[(key, true)];
            }
        }

        TranslationCaptionWidths[("Running", false)] = labelStyle.CalcSize(new GUIContent(1f.ToString("P0").Bold())).x +
                                                       labelStyle.margin.horizontal;
        TranslationCaptionWidths[("NotStarted", false)] = 0f;
        var actionWidth = new[]
        {
            TranslationCaptionWidths[("Translate", true)],
            TranslationCaptionWidths[("Running", false)] + TranslationCaptionWidths[("Pause", true)] +
            TranslationCaptionWidths[("Cancel", true)],
            TranslationCaptionWidths[("TranslationPaused", false)] + TranslationCaptionWidths[("Resume", true)] +
            TranslationCaptionWidths[("Cancel", true)],
            TranslationCaptionWidths[("TranslationCompletedWithErrors", false)] +
            TranslationCaptionWidths[("RetryFailed", true)],
            TranslationCaptionWidths[("TranslationCompleted", false)], TranslationCaptionWidths[("TranslationCancelled", false)],
            TranslationCaptionWidths[("TranslationFailed", false)] + TranslationCaptionWidths[("RetryFailed", true)]
        }.Max();
        var authorWidth = _translationCampaignRows.Length == 0 ? 0f : _translationCampaignRows.Max(row =>
            labelStyle.CalcSize(new GUIContent(row.AuthorLabel)).x + labelStyle.margin.horizontal);
        var titleWidth = _translationCampaignRows.Length == 0 ? 0f : _translationCampaignRows.Max(row =>
            labelStyle.CalcSize(new GUIContent(row.TitleLabel)).x + labelStyle.margin.horizontal);
        var readableWidth = labelStyle.CalcSize(new GUIContent("MMMMMMMM")).x + labelStyle.margin.horizontal;
        _translationColumnWidths = [authorWidth, titleWidth, actionWidth];
        _translationColumnMinimumWidths = [Mathf.Min(authorWidth, readableWidth), Mathf.Min(titleWidth, readableWidth), actionWidth];
    }

    private static string GetTranslationStatusKey(TranslationTaskSnapshot task)
    {
        return task.Status switch
        {
            CampaignTranslationStatus.Running => "Running",
            CampaignTranslationStatus.Paused => "TranslationPaused",
            CampaignTranslationStatus.Completed => task.HasFailedItems ? "TranslationCompletedWithErrors" : "TranslationCompleted",
            CampaignTranslationStatus.Cancelled => "TranslationCancelled",
            CampaignTranslationStatus.Failed => "TranslationFailed",
            _ => "NotStarted"
        };
    }

    private static NamedAction[] CreateTranslationTaskControls(CampaignTranslationTask task, UserCampaign userCampaign,
        GUIStyle labelStyle, GUIStyle buttonStyle, TranslationTaskSnapshot snapshot)
    {
        var status = snapshot.Status;
        var hasFailedItems = snapshot.HasFailedItems;
        var statusText = status switch
        {
            CampaignTranslationStatus.Running => $"{snapshot.PercentageComplete:P0}".Bold().Khaki(),
            CampaignTranslationStatus.Paused => TranslationCaptions["TranslationPaused"].Bold().Yellow(),
            CampaignTranslationStatus.Completed => hasFailedItems
                ? TranslationCaptions["TranslationCompletedWithErrors"].Bold().Orange()
                : TranslationCaptions["TranslationCompleted"].Bold().Green(),
            CampaignTranslationStatus.Cancelled => TranslationCaptions["TranslationCancelled"].Bold().Red(),
            CampaignTranslationStatus.Failed => TranslationCaptions["TranslationFailed"].Bold().Red(),
            _ => string.Empty
        };

        var actions = new List<NamedAction>
        {
            new(statusText, () => UI.Label(statusText, labelStyle, UI.MinWidth(0f), UI.ExpandWidth(true)))
        };

        switch (status)
        {
            case CampaignTranslationStatus.Running:
            {
                var pauseTitle = TranslationCaptions["Pause"];
                var cancelTitle = TranslationCaptions["Cancel"];
                actions.Add(new NamedAction(pauseTitle, () => UI.ActionButton(pauseTitle,
                    () => task.Pause(), buttonStyle, UI.AutoWidth())));
                actions.Add(new NamedAction(cancelTitle, () => UI.ActionButton(cancelTitle,
                    () => CampaignTranslationExecutor.CancelTask(task.CampaignTitle), buttonStyle, UI.AutoWidth())));
                break;
            }

            case CampaignTranslationStatus.Paused:
            {
                var resumeTitle = TranslationCaptions["Resume"];
                var cancelTitle = TranslationCaptions["Cancel"];
                actions.Add(new NamedAction(resumeTitle, () => UI.ActionButton(resumeTitle,
                    () => task.Resume(), buttonStyle, UI.AutoWidth())));
                actions.Add(new NamedAction(cancelTitle, () => UI.ActionButton(cancelTitle,
                    () => CampaignTranslationExecutor.CancelTask(task.CampaignTitle), buttonStyle, UI.AutoWidth())));
                break;
            }

            case CampaignTranslationStatus.Completed when hasFailedItems:
            {
                var retryTitle = TranslationCaptions["RetryFailed"];
                actions.Add(new NamedAction(retryTitle, () => UI.ActionButton(retryTitle,
                    () => CampaignTranslationExecutor.RetryFailedItems(task.CampaignTitle, userCampaign),
                    buttonStyle, UI.AutoWidth())));
                break;
            }
            
            case CampaignTranslationStatus.Failed:
            {
                var retryTitle = TranslationCaptions["RetryFailed"];
                actions.Add(new NamedAction(retryTitle, () => UI.ActionButton(retryTitle,
                    () =>
                    {
                        CampaignTranslationExecutor.CancelTask(task.CampaignTitle);
                        CampaignTranslationExecutor.StartTranslation(task.TargetLanguageCode, task.CampaignTitle, userCampaign);
                    }, buttonStyle, UI.AutoWidth())));
                break;
            }
        }

        return actions.ToArray();
    }

    private static bool _showTranslationPreview;

    private static string GetTranslationCategoryLabel(string category)
    {
        var key = $"ModUi/&TranslationCategory{category}";
        var label = Gui.Localize(key);
        return label == key ? category : label;
    }

    private static void DisplayTranslationProgress(CampaignTranslationTask task)
    {
        var labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        using (UI.HorizontalScope(UI.MinWidth(0f), UI.ExpandWidth(true)))
        {
            UI.Space(25f);

            using (UI.VerticalScope(UI.MinWidth(0f), UI.ExpandWidth(true)))
            {
                var overallProgress = task.PercentageComplete;
                UI.Label(
                    $"{Gui.Localize("ModUi/&TranslationProgress")}: {task.CompletedItems}/{task.TotalItems} ({overallProgress:P0})"
                        .Italic(),
                    labelStyle, UI.ExpandWidth(true));

                var inProgressItems = task.InProgressItems;
                UI.Label($"{Gui.Localize("ModUi/&TranslatingConcurrent")}: {inProgressItems.Count}".Yellow().Italic(),
                    labelStyle, UI.ExpandWidth(true));

                var itemsList = inProgressItems.Take(3).ToList();
                for (var i = 0; i < 3; i++)
                {
                    if (i < itemsList.Count)
                    {
                        var item = itemsList[i];
                        var currentText = item.SourceText.Length > 40
                            ? item.SourceText.Substring(0, 37) + "..."
                            : item.SourceText;
                        UI.Label($"  {GetTranslationCategoryLabel(item.Category)}  {currentText}".Grey().Italic(),
                            labelStyle, UI.ExpandWidth(true));
                    }
                    else
                    {
                        UI.Label(" ", labelStyle, UI.ExpandWidth(true));
                    }
                }

                if (inProgressItems.Count > 3)
                {
                    UI.Label(Gui.Format("ModUi/&TranslationMoreItems", (inProgressItems.Count - 3).ToString()).Grey().Italic(),
                        labelStyle, UI.ExpandWidth(true));
                }
                else
                {
                    // Placeholder when no extra items
                    UI.Label(" ", labelStyle, UI.ExpandWidth(true));
                }

                UI.DisclosureToggle(Gui.Localize("ModUi/&ShowCategoryProgress"), ref _showCategoryProgress, 0);

                if (_showCategoryProgress)
                {
                    foreach (var kvp in task.CategoryProgress)
                    {
                        var category = kvp.Key;
                        var progress = kvp.Value;
                        using (UI.HorizontalScope(UI.MinWidth(0f), UI.ExpandWidth(true)))
                        {
                            UI.Label(GetTranslationCategoryLabel(category), labelStyle, UI.MinWidth(0f), UI.ExpandWidth(true));
                            UI.Label($"{progress.CompletedItems}/{progress.TotalItems}", UI.AutoWidth());

                            if (progress.FailedItems > 0)
                            {
                                UI.Label(Gui.Format("ModUi/&TranslationFailedCount", progress.FailedItems.ToString()).Red(),
                                    labelStyle, UI.MinWidth(0f), UI.ExpandWidth(true));
                            }
                        }
                    }
                }

                // Translation preview
                UI.DisclosureToggle(Gui.Localize("ModUi/&ShowTranslationPreview"), ref _showTranslationPreview, 0);

                if (_showTranslationPreview)
                {
                    var recentTranslations = task.RecentTranslations;
                    
                    var boxStyle = GUI.skin.box;
                    GUILayout.BeginVertical(boxStyle, UI.MinWidth(0f), UI.MinHeight(150f), UI.ExpandWidth(true));
                    
                    if (recentTranslations.Count > 0)
                    {
                        // Show newest first (reverse order)
                        foreach (var preview in recentTranslations.Reverse())
                        {
                            var sourceText = preview.SourceText.Length > 70
                                ? preview.SourceText.Substring(0, 67) + "..."
                                : preview.SourceText;
                            var translatedText = preview.TranslatedText.Length > 70
                                ? preview.TranslatedText.Substring(0, 67) + "..."
                                : preview.TranslatedText;

                            // [Category] Source text in grey italic
                            GUILayout.Label($"[{preview.Category}] {sourceText}".Grey().Italic(), labelStyle, UI.ExpandWidth(true));
                            // Translated text
                            GUILayout.Label(translatedText, labelStyle, UI.ExpandWidth(true));
                            GUILayout.Space(4f);
                        }
                    }
                    else
                    {
                        GUILayout.Label("(No translations yet)".Grey().Italic(), labelStyle, UI.ExpandWidth(true));
                    }
                    
                    GUILayout.EndVertical();
                }
            }
        }
    }

    private static void DisplayGoogleSettings()
    {
        UI.DisclosureToggle(Gui.Localize("ModUi/&TranslationGoogleSettings"), ref _showTranslateServiceSettings, 0);

        if (!_showTranslateServiceSettings) { return; }

        var toggle = Main.Settings.GoogleLegacyMode;
        if (UI.Toggle(Gui.Localize("ModUi/&TranslationGoogleLegacyMode"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.GoogleLegacyMode = toggle;
        }
    }

    private static void DisplayOpenAISettings()
    {
        var labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        var buttonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true };
        var resetTitle = Gui.Localize("ModUi/&Reset");
        var resetWidth = buttonStyle.CalcSize(new GUIContent(resetTitle)).x + buttonStyle.margin.horizontal;

        if (!_apiKeyInitialized)
        {
            _openAIApiKeyInput = OpenAITranslationService.GetApiKey();
            _apiKeyInitialized = true;
        }

        UI.DisclosureToggle(Gui.Localize("ModUi/&OpenAISettings"), ref _showTranslateServiceSettings, 0);

        if (!_showTranslateServiceSettings)
        {
            return;
        }

        UI.Label();

        // Endpoint URL
        var endpointTitle = Gui.Localize("ModUi/&OpenAIEndpoint");
        UI.HStack(null, 3, labelStyle, [0f, 0f, resetWidth],
            new NamedAction(endpointTitle, () => UI.Label(endpointTitle, labelStyle, UI.ExpandWidth(true))),
            new NamedAction(string.Empty, () =>
            {
                var endpoint = Main.Settings.OpenAIEndpoint;
                UI.TextField(ref endpoint, null, UI.MinWidth(0f), UI.ExpandWidth(true));
                if (endpoint != Main.Settings.OpenAIEndpoint)
                {
                    Main.Settings.OpenAIEndpoint = endpoint;
                }
            }),
            new NamedAction(resetTitle, () => UI.ActionButton(resetTitle,
                () => Main.Settings.OpenAIEndpoint = OpenAITranslationService.DefaultEndpoint,
                buttonStyle, UI.AutoWidth())));

        // API Key (stored in PlayerPrefs)
        var apiKeyTitle = Gui.Localize("ModUi/&OpenAIApiKey");
        UI.HStack(null, 3, labelStyle, [0f, 0f, resetWidth],
            new NamedAction(apiKeyTitle, () => UI.Label(apiKeyTitle, labelStyle, UI.ExpandWidth(true))),
            new NamedAction(string.Empty, () =>
            {
                UI.TextField(ref _openAIApiKeyInput, null, UI.MinWidth(0f), UI.ExpandWidth(true));
                var currentApiKey = OpenAITranslationService.GetApiKey();
                if (_openAIApiKeyInput != currentApiKey)
                {
                    OpenAITranslationService.SetApiKey(_openAIApiKeyInput);
                }
            }),
            new NamedAction(resetTitle, () => UI.ActionButton(resetTitle, () =>
            {
                _openAIApiKeyInput = string.Empty;
                OpenAITranslationService.ClearApiKey();
            }, buttonStyle, UI.AutoWidth())));

        // Model
        var modelTitle = Gui.Localize("ModUi/&OpenAIModel");
        UI.HStack(null, 3, labelStyle, [0f, 0f, resetWidth],
            new NamedAction(modelTitle, () => UI.Label(modelTitle, labelStyle, UI.ExpandWidth(true))),
            new NamedAction(string.Empty, () =>
            {
                var model = Main.Settings.OpenAIModel;
                UI.TextField(ref model, null, UI.MinWidth(0f), UI.ExpandWidth(true));
                if (model != Main.Settings.OpenAIModel)
                {
                    Main.Settings.OpenAIModel = model;
                }
            }),
            new NamedAction(resetTitle, () => UI.ActionButton(resetTitle,
                () => Main.Settings.OpenAIModel = OpenAITranslationService.DefaultModel,
                buttonStyle, UI.AutoWidth())));

        UI.Label();

        // Advanced Settings
        UI.DisclosureToggle(Gui.Localize("ModUi/&OpenAIAdvancedSettings"), ref _showOpenAIAdvancedSettings, 0);

        if (!_showOpenAIAdvancedSettings)
        {
            UI.Label();
            return;
        }

        UI.Label();

        // Temperature
        var temperature = Main.Settings.OpenAITemperature;

        if (UI.Slider(Gui.Localize("ModUi/&OpenAITemperature"), ref temperature, 0f, 2f,
                OpenAITranslationService.DefaultTemperature, 2))
        {
            Main.Settings.OpenAITemperature = temperature;
        }

        // Top P
        var topP = Main.Settings.OpenAITopP;

        if (UI.Slider(Gui.Localize("ModUi/&OpenAITopP"), ref topP, 0f, 1f,
                OpenAITranslationService.DefaultTopP, 2))
        {
            Main.Settings.OpenAITopP = topP;
        }

        // Top K
        var topK = Main.Settings.OpenAITopK;

        if (UI.Slider(Gui.Localize("ModUi/&OpenAITopK"), ref topK, 0, 100,
                OpenAITranslationService.DefaultTopK))
        {
            Main.Settings.OpenAITopK = topK;
        }

        // System Prompt
        UI.Label();
        var promptTitle = Gui.Localize("ModUi/&OpenAISystemPrompt");
        UI.HStack(null, 2, labelStyle, [0f, resetWidth],
            new NamedAction(promptTitle, () => UI.Label(promptTitle, labelStyle, UI.ExpandWidth(true))),
            new NamedAction(resetTitle, () => UI.ActionButton(resetTitle,
                () => Main.Settings.OpenAISystemPrompt = OpenAITranslationService.GetLocalizedDefaultPrompt(),
                buttonStyle, UI.AutoWidth())));

        var systemPrompt = Main.Settings.OpenAISystemPrompt;
        systemPrompt = GUILayout.TextArea(systemPrompt, UI.Height(100f), UI.MinWidth(0f), UI.ExpandWidth(true));

        if (systemPrompt != Main.Settings.OpenAISystemPrompt)
        {
            Main.Settings.OpenAISystemPrompt = systemPrompt;
        }

        UI.Label();
        UI.Label(Gui.Localize("ModUi/&OpenAISystemPromptHelp"), labelStyle, UI.ExpandWidth(true));

        UI.Label();
    }
}
