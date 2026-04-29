using System.Collections.Generic;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class SubpowerSelectionModalExtensions
{
    //Re-implements native method, but uses lust of powers instead of feature set
    internal static void Bind(
        this SubpowerSelectionModal instance,
        List<FeatureDefinitionPower> powers,
        RulesetCharacter caster,
        SubpowerSelectionModal.SubpowerEngagedHandler subpowerEngaged,
        RectTransform attachment)
    {
        var wasActive = instance.gameObject.activeSelf;
        var mainPanel = instance.mainPanel;

        instance.gameObject.SetActive(true);
        mainPanel.gameObject.SetActive(true);
        instance.caster = caster;
        instance.containerFeature = null; //Can lead to potential crash if TA suddenly starts using this
        instance.subpowerEngaged = subpowerEngaged;
        instance.subpowerCanceled = null;
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
                    if (instance.subpowerEngaged != null)
                    {
                        var usablePower = PowerProvider.Get(instance.powerDefinitions[index], instance.caster);
                        instance.subpowerEngaged(usablePower, index);
                    }

                    instance.Hide();
                });

                component.Button.enabled = valid;
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
            instance,
            mainPanel.RectTransform,
            attachment,
            instance.subpowersTable,
            new Vector3(70, -400, 0));

        instance.gameObject.SetActive(wasActive);
        mainPanel.gameObject.SetActive(wasActive);
    }
}
