using UnityEngine.UI;
using System;
using HarmonyLib;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

public static class InvocationSelectionPanelExtensions
{
    private static readonly AccessTools.FieldRef<InvocationSelectionPanel, UnityEngine.GameObject>
        InvocationPrefab =
            AccessTools.FieldRefAccess<InvocationSelectionPanel, UnityEngine.GameObject>("invocationPrefab");
    private static readonly System.Reflection.MethodInfo InvocationSelectedMethod =
        AccessTools.DeclaredMethod(
            typeof(InvocationSelectionPanel),
            "OnInvocationSelected",
            [typeof(InvocationActivationBox)]);
    private static readonly System.Reflection.MethodInfo PermanentInvocationToggledMethod =
        AccessTools.DeclaredMethod(
            typeof(InvocationSelectionPanel),
            "OnPermanentInvocationToggled",
            [typeof(InvocationActivationBox)]);

    //Custom bind that acknowledges bonus action invocations
    public static void CustomBind(
        this InvocationSelectionPanel invocationPanel,
        GameLocationCharacter caster,
        InvocationSelectionPanel.InvocationSelectedHandler selected,
        InvocationSelectionPanel.InvocationCancelledHandler canceled,
        CharacterActionPanel actionPanel)
    {
        if (caster?.RulesetCharacter is RulesetCharacterSimulacrum)
        {
            BindSimulacrum(invocationPanel, caster, selected, canceled);
        }
        else
        {
            invocationPanel.Bind(caster, selected, canceled);
        }

        var table = invocationPanel.invocationsTable;
        var invocations = caster.RulesetCharacter.Invocations;
        var actionId = actionPanel.actionId;
        var action = ServiceRepository.GetService<IGameLocationActionService>().AllActionDefinitions[actionId];

        for (var i = 0; i < table.childCount; i++)
        {
            var box = table.GetChild(i).GetComponent<InvocationActivationBox>();
            var active = i < invocations.Count && box.gameObject.activeSelf;

            if (active)
            {
                var invocationDefinition = box.Invocation.invocationDefinition;

                //strict id checks when in battle
                if (actionPanel.ActionScope == ActionScope.Battle)
                {
                    active = actionId == invocationDefinition.GetActionId();
                }
                //allow all invocations that match main action id
                else
                {
                    active = actionId == invocationDefinition.GetMainActionId();
                }
            }

            box.gameObject.SetActive(active);
        }

        var child = invocationPanel.transform.Find("Header/InvocationLabel");

        if (child)
        {
            var label = child.GetComponent<GuiLabel>();

            if (label)
            {
                label.Text = actionId is Id.CastInvocation or (Id)ExtraActionId.CastInvocationBonus
                    ? "Feature/&PointPoolWarlockInvocationInitialTitle"
                    : action.GuiPresentation.Title;
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        Gui.InputService.RecomputeSelectableNavigation(true);
    }

    private static void BindSimulacrum(
        InvocationSelectionPanel panel,
        GameLocationCharacter caster,
        InvocationSelectionPanel.InvocationSelectedHandler selected,
        InvocationSelectionPanel.InvocationCancelledHandler canceled)
    {
        if (InvocationSelectedMethod == null || PermanentInvocationToggledMethod == null)
        {
            throw new MissingMethodException(
                typeof(InvocationSelectionPanel).FullName,
                "Simulacrum invocation callbacks");
        }

        panel.Caster = caster;
        panel.InvocationSelected = selected;
        panel.InvocationCanceled = canceled;

        var invocations = caster.RulesetCharacter.Invocations;
        var table = panel.invocationsTable;
        var prefab = InvocationPrefab(panel);
        var engaged = (InvocationActivationBox.InvocationEngagedHandler)Delegate.CreateDelegate(
            typeof(InvocationActivationBox.InvocationEngagedHandler),
            panel,
            InvocationSelectedMethod);
        var toggled = (InvocationActivationBox.PermanentInvocationToggledHandler)Delegate.CreateDelegate(
            typeof(InvocationActivationBox.PermanentInvocationToggledHandler),
            panel,
            PermanentInvocationToggledMethod);

        while (table.childCount < invocations.Count)
        {
            Gui.GetPrefabFromPool(prefab, table);
        }

        for (var index = 0; index < table.childCount; index++)
        {
            var box = table.GetChild(index).GetComponent<InvocationActivationBox>();

            if (index < invocations.Count)
            {
                box.gameObject.SetActive(true);
                box.Bind(invocations[index], engaged, toggled, caster.RulesetCharacter);
            }
            else
            {
                box.Unbind();
                box.gameObject.SetActive(false);
            }
        }
    }
}
