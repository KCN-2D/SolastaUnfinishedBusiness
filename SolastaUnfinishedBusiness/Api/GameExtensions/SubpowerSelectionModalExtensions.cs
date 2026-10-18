using System.Collections.Generic;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class SubpowerSelectionModalExtensions
{
    private static readonly Dictionary<SubpowerSelectionModal, (Button.ButtonClickedEvent Event, UnityAction Close)>
        BackgroundDismissals = [];

    internal static void BindBackgroundDismissal(this SubpowerSelectionModal instance)
    {
        instance.UnbindBackgroundDismissal();

        var background = instance.transform.Find("Background")?.GetComponent<Button>();
        if (!background)
        {
            return;
        }

        var clicked = background.onClick;
        var missingCloseTarget = false;
        for (var i = 0; i < clicked.GetPersistentEventCount(); i++)
        {
            if (clicked.GetPersistentMethodName(i) != nameof(SubpowerSelectionModal.OnCloseCb))
            {
                continue;
            }

            var target = clicked.GetPersistentTarget(i);
            if (target == instance)
            {
                return;
            }

            missingCloseTarget |= !target;
        }

        if (!missingCloseTarget)
        {
            return;
        }

        // The native prefab's background has an OnCloseCb event with no target. Bind
        // only that missing connection, retaining native and other listeners unchanged.
        UnityAction close = () =>
        {
            if (instance && instance.Visible)
            {
                instance.OnCloseCb();
            }
        };
        clicked.AddListener(close);
        BackgroundDismissals.Add(instance, (clicked, close));
    }

    internal static void UnbindBackgroundDismissal(this SubpowerSelectionModal instance)
    {
        if (BackgroundDismissals.TryGetValue(instance, out var binding))
        {
            binding.Event.RemoveListener(binding.Close);
            BackgroundDismissals.Remove(instance);
        }
    }

    // Uses the native selector with a list of powers instead of a feature set.
    internal static void Bind(
        this SubpowerSelectionModal instance,
        List<FeatureDefinitionPower> powers,
        RulesetCharacter caster,
        SubpowerSelectionModal.SubpowerEngagedHandler subpowerEngaged,
        RectTransform attachment,
        SubpowerSelectionModal.SubpowerCanceledHandler subpowerCanceled = null)
    {
        var wasActive = instance.gameObject.activeSelf;
        var mainPanel = instance.mainPanel;
        var wasMainPanelActive = mainPanel.gameObject.activeSelf;
        FloatingPanelBounds.RestoreAttachmentList(mainPanel.RectTransform);

        instance.gameObject.SetActive(true);
        mainPanel.gameObject.SetActive(true);
        instance.caster = caster;
        instance.containerFeature = null;
        instance.subpowerEngaged = subpowerEngaged;
        instance.subpowerCanceled = subpowerCanceled;
        instance.powerDefinitions.Clear();
        instance.powerDefinitions.AddRange(powers);

        while (instance.subpowersTable.childCount < instance.powerDefinitions.Count)
        {
            Gui.GetPrefabFromPool(instance.subpowerItemPrefab, instance.subpowersTable);
        }

        for (var i = 0; i < instance.subpowersTable.childCount; ++i)
        {
            var child = instance.subpowersTable.GetChild(i);
            var component = child.GetComponent<SubpowerItem>();

            if (i < powers.Count)
            {
                child.gameObject.SetActive(true);

                var power = instance.powerDefinitions[i];
                var valid = caster.CanUsePower(power);

                component.Bind(caster, power, i, index =>
                {
                    // The handler may open another selector or hide the owning panel. Finish this
                    // selector first so its Unbind cannot clear the new selection's state.
                    var engaged = instance.subpowerEngaged;
                    if (engaged == null || instance.caster == null || index < 0 ||
                        index >= instance.powerDefinitions.Count)
                    {
                        return;
                    }

                    var usablePower = PowerProvider.Get(instance.powerDefinitions[index], instance.caster);
                    instance.Hide(true);
                    engaged(usablePower, index);
                });

                component.Button.enabled = true;
                component.Button.interactable = valid;
                component.GetComponent<GuiLabelHighlighter>().enabled = valid;
                component.powerTitle.TMP_Text.color = Color.gray;
            }
            else
            {
                child.gameObject.SetActive(false);
                component.Unbind();
            }
        }

        FloatingPanelBounds.ConfigureNearAttachmentList(
            mainPanel.RectTransform,
            attachment,
            instance.subpowersTable,
            new Vector3(70, -400, 0));
        instance.BindBackgroundDismissal();

        instance.gameObject.SetActive(wasActive);
        mainPanel.gameObject.SetActive(wasMainPanelActive);
    }
}
