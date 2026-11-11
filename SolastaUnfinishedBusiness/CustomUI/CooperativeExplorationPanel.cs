using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.ModKit.Utility;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class CooperativeExplorationPanel : MonoBehaviour
{
    private static readonly Color Parchment = new(0.91f, 0.84f, 0.69f);
    private static readonly Color Muted = new(0.72f, 0.73f, 0.71f);
    private static CooperativeExplorationPanel instance;
    private readonly Dictionary<BaseDefinition, Sprite> icons = new();
    private RectTransform surface;
    private RectTransform root;
    private RectTransform rows;
    private Button openButton;
    private Button readyButton;
    private Button startButton;
    private Button cancelButton;
    private Button buttonTemplate;
    private Image cardBackground;
    private Image cardFrame;
    private TextMeshProUGUI status;
    private TextMeshProUGUI hint;
    private TextMeshProUGUI notice;
    private TMP_FontAsset font;
    private string signature;
    private string languageTitle;
    private bool opened;

    private static string Text(string suffix) => Gui.Localize("UI/&Cooperative" + suffix);

    internal static void Refresh(NetworkingManager owner)
    {
        if (!owner) return;
        if (!instance) instance = owner.gameObject.GetComponent<CooperativeExplorationPanel>() ??
                                  owner.gameObject.AddComponent<CooperativeExplorationPanel>();
        instance.RefreshView();
    }

    internal static void Open()
    {
        if (!instance) Refresh(ServiceRepository.GetService<INetworkingService>() as NetworkingManager);
        if (!instance) return;
        instance.opened = true;
        instance.signature = null;
        instance.RefreshView();
    }

    internal static void Hide()
    {
        if (!instance) return;
        if (instance.root) instance.root.gameObject.SetActive(false);
        if (instance.openButton) instance.openButton.gameObject.SetActive(false);
    }

    internal static void Reset()
    {
        Hide();
        if (!instance) return;
        instance.opened = false;
        instance.signature = null;
        instance.ReleaseIcons();
    }

    private void RefreshView()
    {
        var screen = Gui.GuiService?.GetScreen<GameLocationScreenExploration>();
        if (!screen || !screen.Visible || screen.CanvasGroup && !screen.CanvasGroup.interactable ||
            Gui.GuiService is GuiManager manager && manager.screensByType.Values.Any(panel => panel is ActionSubPanel && panel.Visible) ||
            screen.CharacterControlPanel?.ActionPanels?.Any(panel => panel.Visible && !panel.IsDefaultCursorActive) == true)
        { Hide(); return; }
        var title = Text("Open");
        if (root && languageTitle != title)
        {
            surface.gameObject.SetActive(false);
            Object.Destroy(surface.gameObject);
            root = null;
        }
        if (!root) Build(screen);
        if (!root) return;
        openButton.gameObject.SetActive(!opened);
        root.gameObject.SetActive(opened);
        Position(screen);
        if (!opened) return;

        var plan = CooperativeExplorationContext.GetPlan();
        var characters = CooperativeExplorationContext.ActivityCharacters().ToArray();
        var players = CooperativeExplorationContext.PlayerStatuses().ToArray();
        var coordinated = CooperativeExplorationContext.Enabled;
        var matching = MultiplayerSettingsContext.AllPlayersMatch;
        var next = CooperativeExplorationContext.PlanDigest + string.Join(";", players.Select(player => player.Ready)) +
                   CooperativeExplorationContext.Busy + CooperativeExplorationContext.Failure +
                   CooperativeExplorationContext.FollowLeader + Main.Settings.EnableMultiplayerAutoFollow +
                   coordinated + matching + title + string.Join(",", characters.Select(character => character.RulesetCharacter.Guid));
        if (signature == next) return;
        signature = next;
        foreach (Transform row in rows) { row.gameObject.SetActive(false); Object.Destroy(row.gameObject); }

        var seconds = plan.Count == 0 ? 0 : plan.Max(entry => entry.Activity.Duration);
        status.text = coordinated && seconds > 0
            ? string.Format(Text("Duration"), Duration(seconds)) : string.Empty;
        status.gameObject.SetActive(status.text.Length > 0);
        var failure = !coordinated ? CooperativeExplorationContext.ActivityFailure.None : !matching
            ? CooperativeExplorationContext.ActivityFailure.MatchingPlayersRequired : CooperativeExplorationContext.Failure;
        notice.text = failure == CooperativeExplorationContext.ActivityFailure.None
            ? string.Empty : Gui.Localize("Failure/&Cooperative" + failure);
        notice.gameObject.SetActive(notice.text.Length > 0);
        hint.text = CooperativeExplorationContext.Busy ? Text("Busy") : notice.text.Length > 0
            ? string.Empty : !coordinated ? Text("FollowPrompt") :
            plan.Count == 0 ? Text("AdventureHelp") : string.Empty;
        hint.gameObject.SetActive(hint.text.Length > 0);
        if (coordinated && CooperativeExplorationContext.Online)
            foreach (var player in players)
                Label(rows, player.Name + " · " + Text(player.Ready ? "Ready" : "NotReady"), 26, 18, Muted);
        if (coordinated)
        {
            foreach (var character in characters)
                Activity(character, plan.FirstOrDefault(entry => entry.Activity.Actor == character.RulesetCharacter.Guid));
            foreach (var entry in plan.Where(entry => characters.All(character => character.RulesetCharacter.Guid != entry.Activity.Actor)))
                Activity(CooperativeExplorationContext.Character(entry.Activity.Actor), entry);
        }
        if (Main.Settings.EnableMultiplayerAutoFollow)
        {
            foreach (var leader in CooperativeExplorationContext.FollowCandidates())
            {
                var guid = leader.RulesetCharacter.Guid;
                Button(rows, string.Format(Text("Follow"), leader.RulesetCharacter.Name),
                    () => CooperativeExplorationContext.Follow(guid));
            }
            if (CooperativeExplorationContext.FollowLeader != 0)
                Button(rows, Text("StopFollow"), CooperativeExplorationContext.StopFollowing);
        }
        readyButton.gameObject.SetActive(coordinated);
        readyButton.interactable = !CooperativeExplorationContext.Busy && matching;
        SetButtonText(readyButton, Text(CooperativeExplorationContext.Ready ? "UndoReady" : "SetReady"));
        startButton.gameObject.SetActive(coordinated && CooperativeExplorationContext.IsHost);
        startButton.interactable = CooperativeExplorationContext.CanStart;
        cancelButton.gameObject.SetActive(coordinated && CooperativeExplorationContext.Busy);
    }

    private static string Duration(int seconds) => seconds % 60 == 0
        ? Gui.FormatDuration(RuleDefinitions.DurationType.Minute, seconds / 60).StripHTML()
        : string.Format(Text("Time"), seconds / 60, seconds % 60);

    private void Activity(GameLocationCharacter actor, CooperativeExplorationContext.OwnedReservation entry)
    {
        var activity = entry?.Activity;
        BaseDefinition definition = activity == null ? null : activity.Power
            ? DatabaseRepository.GetDatabase<FeatureDefinitionPower>().GetElement(activity.Definition)
            : DatabaseRepository.GetDatabase<SpellDefinition>().GetElement(activity.Definition);
        var card = Rect("Activity", rows);
        CopyImage(card, cardBackground, true).color = new Color(0.18f, 0.22f, 0.24f, 0.96f);
        Frame(card, cardFrame);
        var layout = card.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 12, 12);
        layout.spacing = 14;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        card.gameObject.AddComponent<LayoutElement>().minHeight = activity == null ? 82 : 110;
        if (definition && definition.GuiPresentation.SpriteReference?.RuntimeKeyIsValid() == true)
        {
            if (!icons.TryGetValue(definition, out var sprite))
                icons[definition] = sprite = Gui.LoadAssetSync<Sprite>(definition.GuiPresentation.SpriteReference);
            var icon = Rect("ActionIcon", card);
            var image = icon.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            var size = icon.gameObject.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = 64;
            size.minHeight = size.preferredHeight = 64;
        }
        var body = Rect("Details", card);
        var bodyLayout = body.gameObject.AddComponent<VerticalLayoutGroup>();
        bodyLayout.spacing = 4;
        bodyLayout.childControlWidth = bodyLayout.childControlHeight = true;
        bodyLayout.childForceExpandWidth = true;
        bodyLayout.childForceExpandHeight = false;
        body.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
        Label(body, actor?.RulesetCharacter.Name ?? activity?.Actor.ToString(), 25, 22, Parchment);
        if (activity != null)
            Label(body, (definition ? Gui.Localize(definition.GuiPresentation.Title) : activity.Definition) +
                        " · " + Duration(activity.Duration), 26, 20, Color.white);
        var targets = (activity?.Targets ?? []).Where(guid => guid != activity.Actor).Select(CooperativeExplorationContext.Character)
            .Where(target => target != null).Select(target => target.RulesetCharacter.Name).ToArray();
        if (targets.Length > 0)
            Label(body, string.Format(Text("Targets"), string.Join(", ", targets)), 24, 18, Muted);
        var canChoose = actor != null && CooperativeExplorationContext.ActivityCharacters().Contains(actor);
        if ((canChoose || entry?.Owner == CooperativeExplorationContext.LocalPlayer) && !CooperativeExplorationContext.Busy)
        {
            var controls = Rect("ActionControls", body);
            var controlsLayout = controls.gameObject.AddComponent<HorizontalLayoutGroup>();
            controlsLayout.childControlWidth = controlsLayout.childControlHeight = true;
            controlsLayout.childForceExpandWidth = activity != null;
            controlsLayout.childForceExpandHeight = false;
            controlsLayout.spacing = 10;
            if (canChoose)
            {
                var choose = Button(controls, Text("ChooseActivity"), () => ChooseActivity(actor));
                choose.name = "ChooseActivity_" + actor.RulesetCharacter.Guid;
                choose.GetComponent<LayoutElement>().preferredWidth = 220;
            }
            if (activity != null)
            {
                var remove = Button(controls, Text("Remove"), () => CooperativeExplorationContext.Remove(activity.Actor));
                remove.name = "RemoveActivity_" + activity.Actor;
            }
        }
    }

    private void ChooseActivity(GameLocationCharacter actor)
    {
        if (CooperativeExplorationContext.Busy || !CooperativeExplorationContext.ActivityCharacters().Contains(actor)) return;
        if (CooperativeExplorationContext.Ready) CooperativeExplorationContext.ToggleReady();
        Gui.GuiService.GetScreen<GameLocationScreenExploration>().CharacterControlPanel.RestoreDefaultCursor();
        var selection = ServiceRepository.GetService<IGameLocationSelectionService>();
        selection.SelectCharacter(actor, true, false);
        if (selection.SelectedCharacters.Count != 1 || selection.SelectedCharacters[0] != actor) return;
        opened = false;
        Hide();
    }

    private void Build(GameLocationScreenExploration screen)
    {
        var message = Gui.GuiService.GetScreen<MessageModal>();
        buttonTemplate = message.validateButton;
        var power = Gui.GuiService.GetScreen<PowerSelectionPanel>().usablePowerPrefab.GetComponent<UsablePowerBox>();
        cardBackground = power.background;
        cardFrame = power.frame;
        font = buttonTemplate.GetComponentInChildren<TextMeshProUGUI>(true).font;
        languageTitle = Text("Open");
        surface = Rect("UB_CooperativeExplorationHud", screen.transform);
        surface.anchorMin = Vector2.zero;
        surface.anchorMax = Vector2.one;
        surface.offsetMin = surface.offsetMax = Vector2.zero;
        var canvas = surface.gameObject.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = screen.GetComponentInParent<Canvas>().sortingOrder + 1;
        surface.gameObject.AddComponent<GraphicRaycaster>();
        openButton = Button(surface, languageTitle, () => { opened = true; signature = null; }, 38);
        var openLabel = openButton.GetComponentInChildren<TextMeshProUGUI>(true);
        openButton.GetComponent<LayoutElement>().preferredWidth = Mathf.Max(190,
            openLabel.GetPreferredValues(languageTitle, float.PositiveInfinity, float.PositiveInfinity).x + 24);
        var openRect = (RectTransform)openButton.transform;
        openRect.anchorMin = openRect.anchorMax = openRect.pivot = new Vector2(0.5f, 0.5f);

        root = Rect("UB_CooperativeExploration", surface);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
        root.gameObject.AddComponent<Image>().color = new Color(0.012f, 0.018f, 0.022f, 0.99f);
        Frame(root, cardFrame);
        var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(20, 20, 16, 16);
        layout.spacing = 10;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        var heading = Rect("Heading", root);
        var headingLayout = heading.gameObject.AddComponent<HorizontalLayoutGroup>();
        headingLayout.childControlWidth = headingLayout.childControlHeight = true;
        headingLayout.childForceExpandWidth = false;
        headingLayout.childForceExpandHeight = false;
        var title = Label(heading, languageTitle, 34, 26, Parchment);
        title.GetComponent<LayoutElement>().flexibleWidth = 1;
        var close = Button(heading, Text("Close"), () => opened = false, 40);
        close.GetComponent<LayoutElement>().preferredWidth = 90;
        status = Label(root, string.Empty, 30, 20, Parchment);
        hint = Label(root, string.Empty, 32, 19, Muted);
        notice = Label(root, string.Empty, 32, 19, Parchment);
        notice.name = "SystemNotice";
        var viewport = Rect("Viewport", root);
        viewport.gameObject.AddComponent<RectMask2D>();
        viewport.gameObject.AddComponent<Image>().color = Color.clear;
        viewport.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1;
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.scrollSensitivity = 48;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.viewport = viewport;
        rows = Rect("Activities", viewport);
        rows.anchorMin = new Vector2(0, 1);
        rows.anchorMax = new Vector2(1, 1);
        rows.pivot = new Vector2(0.5f, 1);
        rows.sizeDelta = Vector2.zero;
        var rowsLayout = rows.gameObject.AddComponent<VerticalLayoutGroup>();
        rowsLayout.spacing = 12;
        rowsLayout.childControlWidth = rowsLayout.childControlHeight = true;
        rowsLayout.childForceExpandWidth = true;
        rowsLayout.childForceExpandHeight = false;
        rows.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = rows;
        var footer = Rect("Footer", root);
        var footerLayout = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
        footerLayout.spacing = 12;
        footerLayout.childControlWidth = footerLayout.childControlHeight = true;
        footerLayout.childForceExpandWidth = true;
        footerLayout.childForceExpandHeight = false;
        readyButton = Button(footer, Text("SetReady"), CooperativeExplorationContext.ToggleReady);
        startButton = Button(footer, Text("Start"), CooperativeExplorationContext.RequestStart);
        cancelButton = Button(root, Text("Cancel"), CooperativeExplorationContext.RequestCancel);
        signature = null;
    }

    private void Position(GameLocationScreenExploration screen)
    {
        var obstacles = new List<RectTransform>();
        var console = Gui.GuiService.GetScreen<GuiConsoleScreen>();
        if (console.Visible && console.viewport) obstacles.Add((RectTransform)console.viewport.parent);
        if (screen.partyControlPanel)
        {
            obstacles.Add(screen.partyControlPanel.partyPlatesTable);
            obstacles.Add(screen.partyControlPanel.guestPlatesTable);
        }
        if (screen.CharacterControlPanel)
        {
            obstacles.AddRange(screen.CharacterControlPanel.ActionPanels.Where(panel => panel.Visible)
                .Select(panel => panel.characterActionsTable.GetComponent<RectTransform>()));
            if (screen.CharacterControlPanel.ActiveCharacterPanel)
                obstacles.Add(screen.CharacterControlPanel.ActiveCharacterPanel.RectTransform);
        }
        var canvas = surface.GetComponent<Canvas>().rootCanvas.GetComponent<RectTransform>();
        if (opened)
            FloatingPanelBounds.PlaceInAvailableArea(root, obstacles, new Vector2(580, 510), canvas.rect.center + new Vector2(-60, 40), 18);
        else
            FloatingPanelBounds.PlaceInAvailableArea((RectTransform)openButton.transform, obstacles,
                new Vector2(openButton.GetComponent<LayoutElement>().preferredWidth, 38),
                new Vector2(canvas.rect.center.x + 320, canvas.rect.yMin + 230), 18);
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static Image CopyImage(RectTransform target, Image source, bool hit)
    {
        var image = target.gameObject.AddComponent<Image>();
        image.sprite = source.sprite;
        image.type = source.type;
        image.color = source.color;
        image.material = source.material;
        image.raycastTarget = hit;
        return image;
    }

    private static void Frame(RectTransform parent, Image source)
    {
        var rect = Rect("Frame", parent);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var image = CopyImage(rect, source, false);
        image.color = Parchment;
        image.fillCenter = false;
    }

    private TextMeshProUGUI Label(Transform parent, string text, float height, float size, Color color)
    {
        var rect = Rect("Label", parent);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = size;
        label.fontStyle = FontStyles.Normal;
        label.color = color;
        label.richText = false;
        label.enableWordWrapping = true;
        label.raycastTarget = false;
        label.text = text;
        rect.gameObject.AddComponent<LayoutElement>().minHeight = height;
        return label;
    }

    private Button Button(Transform parent, string text, Action action, float height = 48)
    {
        var button = Object.Instantiate(buttonTemplate, parent, false);
        button.name = "Button";
        button.gameObject.SetActive(true);
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => { action(); signature = null; });
        var setup = button.GetComponent<GuiButtonSetup>();
        if (setup)
        {
            setup.DeactivateGamepadBindingButton();
            setup.enabled = false;
            if (setup.BasicImage) setup.BasicImage.gameObject.SetActive(false);
        }
        var element = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
        element.ignoreLayout = false;
        element.layoutPriority = 100;
        var fitter = button.GetComponent<ContentSizeFitter>();
        if (fitter) fitter.enabled = false;
        var nativeLayout = button.GetComponent<LayoutGroup>();
        if (nativeLayout) nativeLayout.enabled = false;
        element.minHeight = element.preferredHeight = height;
        element.minWidth = 80;
        element.preferredWidth = 160;
        SetButtonText(button, text);
        var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        var labelRect = (RectTransform)label.transform;
        labelRect.SetParent(button.transform, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(10, 3);
        labelRect.offsetMax = new Vector2(-10, -3);
        label.margin = Vector4.zero;
        label.lineSpacing = label.paragraphSpacing = 0;
        label.enableWordWrapping = true;
        label.enableAutoSizing = true;
        label.fontSize = 20;
        label.fontSizeMin = 16;
        label.fontSizeMax = 20;
        var textWidth = label.GetPreferredValues(text, float.PositiveInfinity, float.PositiveInfinity).x *
                        label.fontSizeMin / label.fontSizeMax;
        element.minWidth = Mathf.Clamp(Mathf.Ceil(textWidth) + 24, 80, 200);
        return button;
    }

    private static void SetButtonText(Button button, string text) => button.GetComponentInChildren<GuiLabel>(true).Text = text;

    private void ReleaseIcons()
    {
        foreach (var icon in icons.Values) if (icon) Gui.ReleaseAddressableAsset(icon);
        icons.Clear();
    }

    private void OnDestroy()
    {
        if (surface) Object.Destroy(surface.gameObject);
        ReleaseIcons();
        if (instance == this) instance = null;
    }
}
