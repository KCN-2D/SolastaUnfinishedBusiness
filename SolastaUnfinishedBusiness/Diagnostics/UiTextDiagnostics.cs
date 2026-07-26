using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Diagnostics;

// Temporary UI playtest diagnostics. Layout probes are isolated here so they
// can be removed without changing the text-fitting behavior after verification.
internal static class UiTextDiagnostics
{
    private const string Prefix = "[UB-UI-DIAG]";
    private const int MaximumFailureKeys = 32;
    private const int MaximumStateKeys = 256;
    private static readonly Dictionary<string, string> LastStates = [];
    private static readonly HashSet<string> ReportedFailures = [];
    private static readonly Queue<string> StateOrder = new();

    internal static void ScheduleNavigationMenu(
        Button menuButton,
        TMP_Text label,
        string target)
    {
        TryRun(
            "navigation-menu-schedule",
            () => ScheduleNavigationMenuCore(menuButton, label, target));
    }

    private static void ScheduleNavigationMenuCore(
        Button menuButton,
        TMP_Text label,
        string target)
    {
        if (!menuButton)
        {
            return;
        }

        // Hidden exploration/battle panel instances are only prefab candidates, not
        // the UI the player can see. Do not let their layout masquerade as playtest
        // evidence for the active navigation menu.
        if (!menuButton.gameObject.activeInHierarchy ||
            label && !label.gameObject.activeInHierarchy)
        {
            return;
        }

        if (!label)
        {
            RecordNavigationMenu(menuButton, label, target, 0);

            return;
        }

        var probe = menuButton.GetComponent<NavigationMenuProbe>() ??
                    menuButton.gameObject.AddComponent<NavigationMenuProbe>();

        probe.Schedule(menuButton, label, target);
    }

    internal static void ScheduleActionCaptions(CharacterActionPanel actionPanel)
    {
        TryRun(
            "action-caption-schedule",
            () => ScheduleActionCaptionsCore(actionPanel));
    }

    private static void ScheduleActionCaptionsCore(CharacterActionPanel actionPanel)
    {
        if (!actionPanel || !actionPanel.gameObject.activeInHierarchy)
        {
            return;
        }

        var probe = actionPanel.GetComponent<ActionCaptionProbe>() ??
                    actionPanel.gameObject.AddComponent<ActionCaptionProbe>();

        probe.Schedule(actionPanel);
    }

    internal static void ScheduleActiveCharacterIdentity(
        ActiveCharacterPanel panel,
        GuiLabel classAndLevelLabel)
    {
        TryRun(
            "active-character-class-level-schedule",
            () => ScheduleActiveCharacterIdentityCore(panel, classAndLevelLabel));
    }

    private static void ScheduleActiveCharacterIdentityCore(
        ActiveCharacterPanel panel,
        GuiLabel classAndLevelLabel)
    {
        if (!panel || !panel.gameObject.activeInHierarchy)
        {
            return;
        }

        var probe = panel.GetComponent<ActiveCharacterIdentityProbe>() ??
                    panel.gameObject.AddComponent<ActiveCharacterIdentityProbe>();

        probe.Schedule(panel, classAndLevelLabel);
    }

    private static void RecordNavigationMenu(
        Button menuButton,
        TMP_Text label,
        string target,
        int frame)
    {
        if (!menuButton)
        {
            return;
        }

        var owner = GetNavigationMenuOwner(menuButton);
        var root = owner
            ? owner.transform
            : menuButton.transform.parent
                ? menuButton.transform.parent
                : menuButton.transform;
        var buttonPath = GetRelativePath(root, menuButton.transform);
        var buttonLayout = DescribeRectTransform("button", menuButton.transform as RectTransform);
        var buttonParent = menuButton.transform.parent;
        var buttonParentDetail = buttonParent
            ? $"buttonParentPath={GetRelativePath(root, buttonParent)} " +
              $"{DescribeRectTransform("buttonParent", buttonParent as RectTransform)} " +
              $"buttonParentDrivers={DescribeLayoutDrivers(buttonParent)}"
            : "buttonParent=<missing>";
        string labelDetail;
        string key;

        if (!label)
        {
            labelDetail = "label=<missing>";
            key = $"navigation-menu|{target}|{menuButton.GetInstanceID()}|missing";
        }
        else
        {
            var source = label.GetComponent<GuiLabel>()?.Text ?? label.text;
            var labelPath = GetRelativePath(root, label.transform);
            var labelLayout = DescribeRectTransform("label", label.rectTransform);
            var labelLayoutDrivers = DescribeLayoutDrivers(label);
            var labelParent = label.transform.parent;
            var labelParentDetail = labelParent
                ? $"labelParentPath={GetRelativePath(root, labelParent)} " +
                  $"{DescribeRectTransform("labelParent", labelParent as RectTransform)} " +
                  $"labelParentDrivers={DescribeLayoutDrivers(labelParent)}"
                : "labelParent=<missing>";
            var margin = label.margin;

            labelDetail =
                $"labelPath={labelPath} labelActive={label.gameObject.activeInHierarchy} " +
                $"source={Escape(source)} rendered={Escape(label.text)} {labelLayout} " +
                $"labelDrivers={labelLayoutDrivers} " +
                $"{labelParentDetail} " +
                $"labelMargin={margin.x:0.##},{margin.y:0.##},{margin.z:0.##},{margin.w:0.##} " +
                $"preferred={label.preferredWidth:0.##}x{label.preferredHeight:0.##} " +
                $"font={Escape(label.font?.name)} size={label.fontSize:0.##} " +
                $"min={label.fontSizeMin:0.##} max={label.fontSizeMax:0.##} " +
                $"auto={label.enableAutoSizing} wrap={label.enableWordWrapping} " +
                $"maxLines={label.maxVisibleLines} lines={label.textInfo?.lineCount ?? -1} " +
                $"overflow={label.overflowMode} overflowing={label.isTextOverflowing}";
            key = $"navigation-menu|{target}|{menuButton.GetInstanceID()}|{label.GetInstanceID()}";
        }

        var detail =
            $"surface=navigation-menu target={Escape(target)} frame={frame} " +
            $"owner={(owner ? owner.GetType().Name : "<none>")} " +
            $"buttonPath={buttonPath} " +
            $"buttonActive={menuButton.gameObject.activeInHierarchy} {buttonLayout} " +
            $"buttonDrivers={DescribeLayoutDrivers(menuButton)} " +
            $"{buttonParentDetail} " +
            $"{labelDetail}";

        RecordState(key, detail);
    }

    private static void RecordLabel(
        string surface,
        Transform root,
        GuiLabel label,
        int frame)
    {
        if (!label)
        {
            return;
        }

        RecordText(surface, root, label.TMP_Text, label.Text, frame);
    }

    private static void RecordText(
        string surface,
        Transform root,
        TMP_Text text,
        string sourceText,
        int frame)
    {
        if (!root ||
            !text ||
            !text.gameObject.activeInHierarchy)
        {
            return;
        }

        var rect = text.rectTransform.rect;
        var path = GetRelativePath(root, text.transform);
        var rendered = Escape(text.text);
        var source = Escape(sourceText);
        var detail =
            $"surface={surface} frame={frame} path={path} " +
            $"source={source} rendered={rendered} " +
            $"rect={rect.width:0.#}x{rect.height:0.#} " +
            $"preferred={text.preferredWidth:0.#}x{text.preferredHeight:0.#} " +
            $"font={text.fontSize:0.##} min={text.fontSizeMin:0.##} " +
            $"max={text.fontSizeMax:0.##} auto={text.enableAutoSizing} " +
            $"wrap={text.enableWordWrapping} lines={text.textInfo?.lineCount ?? -1} " +
            $"overflow={text.overflowMode} overflowing={text.isTextOverflowing}";
        var key = $"{surface}|{path}|{source}";

        RecordState(key, detail);
    }

    private static void RecordState(string key, string detail)
    {
        var knownState = LastStates.TryGetValue(key, out var previous);

        if (knownState && previous == detail)
        {
            return;
        }

        if (!knownState)
        {
            if (LastStates.Count >= MaximumStateKeys &&
                StateOrder.Count > 0)
            {
                LastStates.Remove(StateOrder.Dequeue());
            }

            StateOrder.Enqueue(key);
        }

        LastStates[key] = detail;
        Main.Info($"{Prefix} {detail}");
    }

    private static void TryRun(string area, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            RecordFailure(area, exception);
        }
    }

    private static void RecordFailure(string area, Exception exception)
    {
        var key =
            $"{area}|{exception?.GetType().FullName ?? "<null>"}|" +
            $"{exception?.Message ?? string.Empty}";

        if (ReportedFailures.Count >= MaximumFailureKeys ||
            !ReportedFailures.Add(key))
        {
            return;
        }

        try
        {
            Main.Info(
                $"{Prefix} surface=diagnostic-failure area={Escape(area)} " +
                $"exception={exception?.GetType().FullName ?? "<null>"} " +
                $"message={Escape(exception?.Message)}");
        }
        catch
        {
            // Diagnostics must never affect gameplay.
        }
    }

    private static string DescribeLayoutDrivers(Component component)
    {
        var fitter = component.GetComponent<ContentSizeFitter>();
        var layoutElement = component.GetComponent<LayoutElement>();
        var layoutGroup = component.GetComponent<LayoutGroup>();
        var fitterSummary = fitter
            ? $"{fitter.enabled}:{fitter.horizontalFit}/{fitter.verticalFit}"
            : "<none>";
        var layoutElementSummary = layoutElement
            ? $"{layoutElement.enabled}:ignore={layoutElement.ignoreLayout}:" +
              $"min={layoutElement.minWidth:0.##}:preferred={layoutElement.preferredWidth:0.##}"
            : "<none>";
        var layoutGroupSummary = layoutGroup
            ? $"{layoutGroup.GetType().Name}:{layoutGroup.enabled}"
            : "<none>";

        return
            $"fitter={fitterSummary}:layoutElement={layoutElementSummary}:" +
            $"layoutGroup={layoutGroupSummary}";
    }

    private static Component GetNavigationMenuOwner(Button menuButton)
    {
        var navigationPanel = menuButton.GetComponentInParent<TimeAndNavigationPanel>();

        if (navigationPanel)
        {
            return navigationPanel;
        }

        return menuButton.GetComponentInParent<GameLocationBaseScreen>();
    }

    private static string DescribeRectTransform(string prefix, RectTransform rectTransform)
    {
        if (!rectTransform)
        {
            return $"{prefix}Rect=<missing>";
        }

        var rect = rectTransform.rect;
        var anchorMin = rectTransform.anchorMin;
        var anchorMax = rectTransform.anchorMax;
        var offsetMin = rectTransform.offsetMin;
        var offsetMax = rectTransform.offsetMax;

        return
            $"{prefix}Rect={rect.width:0.##}x{rect.height:0.##} " +
            $"{prefix}Anchors={anchorMin.x:0.###},{anchorMin.y:0.###}-" +
            $"{anchorMax.x:0.###},{anchorMax.y:0.###} " +
            $"{prefix}Offsets={offsetMin.x:0.##},{offsetMin.y:0.##}-" +
            $"{offsetMax.x:0.##},{offsetMax.y:0.##}";
    }

    private static string GetRelativePath(Transform root, Transform leaf)
    {
        var parts = new Stack<string>();

        for (var current = leaf;
             current && current != root.parent;
             current = current.parent)
        {
            parts.Push($"{current.name}[{current.GetSiblingIndex()}]");

            if (current == root)
            {
                break;
            }
        }

        return string.Join("/", parts);
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private sealed class NavigationMenuProbe : MonoBehaviour
    {
        private TMP_Text _label;
        private Button _menuButton;
        private Coroutine _probe;
        private string _target;

        internal void Schedule(
            Button menuButton,
            TMP_Text label,
            string target)
        {
            _menuButton = menuButton;
            _label = label;
            _target = target;

            if (_probe != null)
            {
                return;
            }

            _probe = StartCoroutine(ProbeAfterLayout());
        }

        private IEnumerator ProbeAfterLayout()
        {
            for (var frame = 0; frame < 2; frame++)
            {
                yield return null;
            }

            // Capture the state after Unity's layout and canvas rebuilds, matching the frame that
            // was actually presented rather than the value observed earlier in Update.
            yield return new WaitForEndOfFrame();
            TryRun(
                "navigation-menu-record",
                () => RecordNavigationMenu(_menuButton, _label, _target, 2));

            _probe = null;
        }

        private void OnDisable()
        {
            if (_probe == null)
            {
                return;
            }

            StopCoroutine(_probe);
            _probe = null;
        }
    }

    private sealed class ActionCaptionProbe : MonoBehaviour
    {
        private CharacterActionPanel _actionPanel;
        private Coroutine _probe;

        internal void Schedule(CharacterActionPanel actionPanel)
        {
            _actionPanel = actionPanel;

            if (_probe != null)
            {
                StopCoroutine(_probe);
            }

            _probe = StartCoroutine(ProbeAfterLayout());
        }

        private IEnumerator ProbeAfterLayout()
        {
            for (var frame = 0; frame < 2; frame++)
            {
                yield return null;
            }

            TryRun(
                "action-caption-record",
                () =>
                {
                    if (!_actionPanel ||
                        !_actionPanel.gameObject.activeInHierarchy)
                    {
                        return;
                    }

                    foreach (var item in _actionPanel.actionItems.Where(item =>
                                 item &&
                                 item.gameObject.activeInHierarchy &&
                                 item.currentItemForm &&
                                 item.currentItemForm.captionLabel))
                    {
                        var text = item.currentItemForm.captionLabel.TMP_Text;

                        if (!text ||
                            !text.isTextOverflowing &&
                            (text.textInfo?.lineCount ?? 0) <= Math.Max(1, text.maxVisibleLines))
                        {
                            continue;
                        }

                        RecordLabel(
                            "action-caption",
                            _actionPanel.transform,
                            item.currentItemForm.captionLabel,
                            2);
                    }
                });

            _probe = null;
        }

        private void OnDisable()
        {
            if (_probe == null)
            {
                return;
            }

            StopCoroutine(_probe);
            _probe = null;
        }
    }

    private sealed class ActiveCharacterIdentityProbe : MonoBehaviour
    {
        private GuiLabel _classAndLevelLabel;
        private ActiveCharacterPanel _panel;
        private Coroutine _probe;

        internal void Schedule(
            ActiveCharacterPanel panel,
            GuiLabel classAndLevelLabel)
        {
            _panel = panel;
            _classAndLevelLabel = classAndLevelLabel;

            if (_probe == null)
            {
                _probe = StartCoroutine(ProbeAfterLayout());
            }
        }

        private IEnumerator ProbeAfterLayout()
        {
            for (var frame = 0; frame < 2; frame++)
            {
                yield return null;
            }

            yield return new WaitForEndOfFrame();

            TryRun(
                "active-character-class-level-record",
                () =>
                {
                    if (_panel && _panel.gameObject.activeInHierarchy)
                    {
                        RecordLabel(
                            "active-character-class-level",
                            _panel.transform,
                            _classAndLevelLabel,
                            2);
                    }
                });

            _probe = null;
        }

        private void OnDisable()
        {
            if (_probe == null)
            {
                return;
            }

            StopCoroutine(_probe);
            _probe = null;
        }
    }
}
