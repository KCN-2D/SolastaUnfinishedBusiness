using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SubspellSelectionModalPatcher
{
    private static readonly Dictionary<SubspellSelectionModal, InvocationState> InvocationSessions = [];
    private static readonly Dictionary<SubspellSelectionModal, SessionState> Sessions = [];

    internal static void Refresh(SubspellSelectionModal modal)
    {
        if (!Sessions.TryGetValue(modal, out var state))
        {
            return;
        }

        state.BeginRefresh();

        try
        {
            modal.Unbind();
            modal.Bind(
                state.MasterSpell,
                state.Caster,
                state.Repertoire,
                state.SpellCastEngaged,
                state.SlotLevel,
                state.MasterSpellBox);
        }
        finally
        {
            state.EndRefresh();
        }
    }

    private static void RebindPooledTooltipBindings(
        SubspellSelectionModal modal,
        SessionState state)
    {
        var expectedDefinitions = state.Session.GetSubspells();
        var subspellItems = modal.subspellsTable.GetComponentsInChildren<SubspellItem>(true);

        foreach (var subspellItem in subspellItems)
        {
            // Bind restores the modal's hidden state before this postfix runs.
            if (!subspellItem.gameObject.activeSelf)
            {
                SpellCastingValidation.BindTooltipRepertoire(subspellItem.tooltip, null);

                continue;
            }

            var index = subspellItem.index;
            var expectedDefinition = index >= 0 && index < expectedDefinitions.Count
                ? expectedDefinitions[index]
                : null;
            var actualDefinitionName = subspellItem.tooltip.Content;

            if (expectedDefinition != null &&
                !string.Equals(actualDefinitionName, expectedDefinition.Name, StringComparison.Ordinal))
            {
                subspellItem.Bind(state.Caster, expectedDefinition, index, modal.OnActivate);
            }

            SpellCastingValidation.BindTooltipRepertoire(
                subspellItem.tooltip,
                state.Repertoire,
                state.BypassComponentsAndCastingTime ||
                state.Session is ICustomSubspellSelectionAvailability { BypassComponentsAndCastingTime: true },
                state.BypassMaterialComponent);

            if (state.Session is ICustomSubspellSelectionAvailability availability && expectedDefinition != null)
            {
                var available = availability.IsAvailable(expectedDefinition, out var failure);
                subspellItem.Button.interactable = available;

                if (!available && !string.IsNullOrEmpty(failure))
                {
                    subspellItem.tooltip.Content = Gui.FormatFailure(expectedDefinition.Name, failure);
                }
            }
        }

    }

    private static List<SpellDefinition> GetSubspells(
        SpellDefinition masterSpell,
        int slotLevel,
        SubspellSelectionModal modal)
    {
        return Sessions.TryGetValue(modal, out var state)
            ? state.Session.GetSubspells()
            : masterSpell.SubspellsList;
    }

    private static void ApplyInvocationAvailability(SubspellSelectionModal modal)
    {
        if (!InvocationSessions.TryGetValue(modal, out var state))
        {
            return;
        }

        var subspells = GetSubspells(state.MasterSpell, modal.slotLevel, modal);
        var subspellItems = modal.subspellsTable.GetComponentsInChildren<SubspellItem>(true);
        var repertoire =
            state.Invocation.InvocationRepertoire ??
            state.Caster.GetSpellRepertoireForInvocations();

        foreach (var subspellItem in subspellItems)
        {
            if (!subspellItem.gameObject.activeSelf)
            {
                continue;
            }

            var index = subspellItem.index;
            var spell = index >= 0 && index < subspells.Count
                ? subspells[index]
                : null;
            var failure = string.Empty;

            SpellCastingValidation.BindTooltipRepertoire(
                subspellItem.tooltip,
                repertoire,
                bypassMaterialComponent:
                state.Invocation.InvocationDefinition.OverrideMaterialComponent);

            var available = spell != null &&
                            state.Caster.CanCastInvocationSpell(
                                state.Invocation,
                                spell,
                                out failure);

            subspellItem.Button.interactable = available;

            if (!available && spell != null && !string.IsNullOrEmpty(failure))
            {
                subspellItem.tooltip.Content = Gui.FormatFailure(spell.Name, failure);
            }
        }

        Gui.InputService.RecomputeSelectableNavigation(true);
    }

    private static void ResetPooledSelectionState(SubspellSelectionModal modal)
    {
        foreach (var subspellItem in
                 modal.subspellsTable.GetComponentsInChildren<SubspellItem>(true))
        {
            subspellItem.Button.interactable = true;
            SpellCastingValidation.BindTooltipRepertoire(subspellItem.tooltip, null);
        }
    }

    internal static void RecordInvocationSelection(
        InvocationSelectionPanel panel, InvocationActivationBox invocationActivationBox)
    {
        var modal = Gui.GuiService.GetScreen<SubspellSelectionModal>();

        if (!modal)
        {
            return;
        }

        InvocationSessions.Remove(modal);

        var invocation = invocationActivationBox
            ? invocationActivationBox.Invocation
            : null;
        var masterSpell = invocation?.InvocationDefinition?.GrantedSpell;

        if (panel.Caster?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate ||
            masterSpell == null ||
            !masterSpell.SpellsBundle ||
            !duplicate.Invocations.Contains(invocation))
        {
            return;
        }

        InvocationSessions[modal] = new InvocationState(
            duplicate,
            invocation,
            masterSpell);
    }

    [HarmonyPatch(typeof(SubspellSelectionModal), nameof(SubspellSelectionModal.OnActivate))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnActivate_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SubspellSelectionModal __instance, int index)
        {
            if (InvocationSessions.TryGetValue(__instance, out var invocationState))
            {
                var subspells = GetSubspells(
                    invocationState.MasterSpell,
                    __instance.slotLevel,
                    __instance);
                if (index < 0 ||
                    index >= subspells.Count ||
                    !invocationState.Caster.CanCastInvocationSpell(
                        invocationState.Invocation,
                        subspells[index],
                        out _))
                {
                    return false;
                }
            }

            return !Sessions.TryGetValue(__instance, out var state) ||
                   state.Session.OnActivate(__instance, index);
        }
    }

    [HarmonyPatch(typeof(SubspellSelectionModal), nameof(SubspellSelectionModal.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [HarmonyPatch([
        typeof(SpellDefinition), typeof(RulesetCharacter), typeof(RulesetSpellRepertoire),
        typeof(SpellsByLevelBox.SpellCastEngagedHandler), typeof(int), typeof(RectTransform)
    ])]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [HarmonyPriority(Priority.First)]
        [UsedImplicitly]
        public static void Prefix(
            SubspellSelectionModal __instance,
            SpellDefinition masterSpell,
            RulesetCharacter caster,
            ref RulesetSpellRepertoire spellRepertoire,
            ref SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
            ref int slotLevel,
            RectTransform masterSpellBox)
        {
            var selected = SpellCastingResourceContext.CurrentSelection;
            if (selected != null && SpellCastingResourceContext.IsSameSpell(selected.Spell, masterSpell))
            {
                // The child picker runs after this click returns. Its callback owns the captured choice.
                spellRepertoire = selected.Repertoire;
                slotLevel = selected.SlotLevel;
                var callback = spellCastEngaged;
                spellCastEngaged = (repertoire, spell, level) =>
                {
                    using var selection = SpellCastingResourceContext.BeginSelection(selected);
                    callback(repertoire, spell, level);
                };
            }

            FloatingPanelBounds.RestoreAttachmentList(__instance.mainPanel.RectTransform);
            ResetPooledSelectionState(__instance);

            if (InvocationSessions.TryGetValue(__instance, out var invocationState) &&
                (!ReferenceEquals(invocationState.Caster, caster) ||
                 invocationState.MasterSpell != masterSpell))
            {
                InvocationSessions.Remove(__instance);
            }

            if (Sessions.TryGetValue(__instance, out var existingState) && existingState.IsRefreshing)
            {
                return;
            }

            var provider = masterSpell.GetFirstSubFeatureOfType<ICustomSubspellSelectionProvider>() ??
                           UpcastConjureElementalAndFey.TryGetProvider(masterSpell);

            if (provider == null)
            {
                Sessions.Remove(__instance);
                return;
            }

            Sessions[__instance] = new SessionState(
                provider.CreateSession(masterSpell, caster, spellRepertoire, slotLevel),
                provider.BypassComponentsAndCastingTime,
                provider.BypassMaterialComponent,
                masterSpell,
                caster,
                spellRepertoire,
                spellCastEngaged,
                slotLevel,
                masterSpellBox);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var subspellsListMethod = typeof(SpellDefinition).GetMethod("get_SubspellsList");
            var getSpellList =
                new Func<SpellDefinition, int, SubspellSelectionModal, List<SpellDefinition>>(GetSubspells).Method;

            return instructions.ReplaceCalls(
                subspellsListMethod,
                "SubspellSelectionModal.Bind",
                new CodeInstruction(OpCodes.Ldarg, 5),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, getSpellList));
        }

        [UsedImplicitly]
        public static void Postfix(SubspellSelectionModal __instance, RectTransform masterSpellBox)
        {
            if (Sessions.TryGetValue(__instance, out var state))
            {
                RebindPooledTooltipBindings(__instance, state);
            }

            ApplyInvocationAvailability(__instance);

            FloatingPanelBounds.ConfigureNearAttachmentList(
                __instance.mainPanel.RectTransform,
                masterSpellBox,
                __instance.subspellsTable,
                new Vector3(70, -400, 0));
        }
    }

    [HarmonyPatch(typeof(SubspellSelectionModal), nameof(SubspellSelectionModal.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [HarmonyPatch([
        typeof(RulesetItemDevice), typeof(RulesetDeviceFunction), typeof(GuiCharacter),
        typeof(UsableDeviceFunctionBox.DeviceFunctionEngagedHandler), typeof(RectTransform)
    ])]
    [UsedImplicitly]
    public static class BindDevice_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            SubspellSelectionModal __instance,
            RulesetItemDevice rulesetItemDevice,
            RulesetDeviceFunction rulesetDeviceFunction,
            GuiCharacter guiCharacter,
            UsableDeviceFunctionBox.DeviceFunctionEngagedHandler deviceFunctionEngaged,
            RectTransform masterSpellBox)
        {
            FloatingPanelBounds.RestoreAttachmentList(__instance.mainPanel.RectTransform);

            var masterSpell = rulesetDeviceFunction?.DeviceFunctionDescription?.SpellDefinition;
            var caster = guiCharacter?.RulesetCharacter;
            var provider = masterSpell?.GetFirstSubFeatureOfType<ICustomSubspellSelectionProvider>();

            if (provider is not WishBehavior || caster == null)
            {
                return true;
            }

            SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged = (_, _, _) =>
            {
                using (RulesetEffectSpellWithOrigin.UseDeviceOrigin(
                           rulesetItemDevice,
                           rulesetDeviceFunction,
                           0,
                           0))
                {
                    deviceFunctionEngaged?.Invoke(
                        guiCharacter,
                        rulesetItemDevice,
                        rulesetDeviceFunction,
                        0,
                        0);
                }
            };

            __instance.Bind(
                masterSpell,
                caster,
                null,
                spellCastEngaged,
                masterSpell.SpellLevel,
                masterSpellBox);

            return false;
        }

        [UsedImplicitly]
        public static void Postfix(SubspellSelectionModal __instance, RectTransform masterSpellBox)
        {
            FloatingPanelBounds.ConfigureNearAttachmentList(
                __instance.mainPanel.RectTransform,
                masterSpellBox,
                __instance.subspellsTable,
                new Vector3(70, -400, 0));
        }
    }

    [HarmonyPatch(typeof(SubspellSelectionModal), nameof(SubspellSelectionModal.OnEndHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SubspellSelectionModal __instance)
        {
            if (!Sessions.TryGetValue(__instance, out var state))
            {
                ResetPooledSelectionState(__instance);
                InvocationSessions.Remove(__instance);

                return;
            }

            if (state.IsRefreshing)
            {
                return;
            }

            ResetPooledSelectionState(__instance);
            InvocationSessions.Remove(__instance);
            Sessions.Remove(__instance);
        }
    }

    [HarmonyPatch(typeof(SubspellSelectionModal), nameof(SubspellSelectionModal.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SubspellSelectionModal __instance)
        {
            // Native Unbind returns the children to the shared pool. Reset their state before they leave.
            FloatingPanelBounds.RestoreAttachmentList(__instance.mainPanel.RectTransform);
            ResetPooledSelectionState(__instance);

            if (Sessions.TryGetValue(__instance, out var state) && state.IsRefreshing)
            {
                return;
            }

            InvocationSessions.Remove(__instance);
            Sessions.Remove(__instance);
        }
    }

    private sealed class InvocationState(
        RulesetCharacterSimulacrum caster,
        RulesetInvocation invocation,
        SpellDefinition masterSpell)
    {
        internal readonly RulesetCharacterSimulacrum Caster = caster;
        internal readonly RulesetInvocation Invocation = invocation;
        internal readonly SpellDefinition MasterSpell = masterSpell;
    }

    private sealed class SessionState(
        ICustomSubspellSelectionSession session,
        bool bypassComponentsAndCastingTime,
        bool bypassMaterialComponent,
        SpellDefinition masterSpell,
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
        int slotLevel,
        RectTransform masterSpellBox)
    {
        internal readonly RulesetCharacter Caster = caster;
        internal readonly bool BypassComponentsAndCastingTime =
            bypassComponentsAndCastingTime;
        internal readonly bool BypassMaterialComponent = bypassMaterialComponent;
        internal readonly SpellDefinition MasterSpell = masterSpell;
        internal readonly RectTransform MasterSpellBox = masterSpellBox;
        internal readonly RulesetSpellRepertoire Repertoire = repertoire;
        internal readonly ICustomSubspellSelectionSession Session = session;
        internal readonly int SlotLevel = slotLevel;
        internal readonly SpellsByLevelBox.SpellCastEngagedHandler SpellCastEngaged = spellCastEngaged;
        internal bool IsRefreshing;

        internal void BeginRefresh()
        {
            IsRefreshing = true;
        }

        internal void EndRefresh()
        {
            IsRefreshing = false;
        }
    }
}
