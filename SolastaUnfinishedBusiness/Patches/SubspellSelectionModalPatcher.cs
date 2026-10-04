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
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SubspellSelectionModalPatcher
{
    private static readonly Dictionary<SubspellSelectionModal, InvocationState> InvocationSessions = [];
    private static readonly Dictionary<SubspellSelectionModal, SessionState> Sessions = [];

    internal static bool HasNestedSubspells(SpellDefinition masterSpell)
    {
        return masterSpell?.SubspellsList.Any(spell => spell?.SpellsBundle == true) == true;
    }

    internal static SpellDefinition BuildBackNavigationSpell(string name)
    {
        return SpellDefinitionBuilder
            .Create(name)
            .SetGuiPresentation("Screen/&SubspellSelectionBackTitle", "Screen/&SubspellSelectionBackDescription")
            .SetSpellLevel(0)
            .SetCastingTime(ActivationTime.NoCost)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetSomaticComponent(false)
            .SetVerboseComponent(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Self)
                .Build())
            .AddCustomSubFeatures(new NavigationChoice())
            .AddToDB();
    }

    internal sealed class NavigationChoice
    {
    }

    private static bool IsNavigationChoice(SessionState state, SpellDefinition spell)
    {
        return spell?.HasSubFeatureOfType<NavigationChoice>() == true ||
               state.Session is HierarchicalSelectionSession hierarchy && hierarchy.IsNavigation(spell);
    }

    private static void BindNavigationTooltip(GuiTooltip tooltip, SpellDefinition spell)
    {
        SpellCastingValidation.BindTooltipRepertoire(tooltip, null);
        tooltip.TooltipClass = GuiManager.DefaultTooltipClass;
        tooltip.Content = spell.GuiPresentation.Description;
        tooltip.DataProvider = null;
        tooltip.Context = null;
    }

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

        Gui.InputService.RecomputeSelectableNavigation(true);
        var firstChoice = modal.subspellsTable.GetComponentsInChildren<SubspellItem>(true)
            .FirstOrDefault(item => item.gameObject.activeSelf && item.Button.IsInteractable());

        if (firstChoice)
        {
            Gui.InputService.SelectCurrentSelectable(firstChoice.Button);
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
            var navigation = IsNavigationChoice(state, expectedDefinition);
            var tooltip = subspellItem.tooltip;
            var matchesDefinition = tooltip.DataProvider is GuiSpellDefinition guiSpell &&
                                    guiSpell.SpellDefinition == expectedDefinition ||
                                    navigation && tooltip.TooltipClass == GuiManager.DefaultTooltipClass &&
                                    tooltip.Content == expectedDefinition.GuiPresentation.Description;

            if (expectedDefinition != null && !matchesDefinition)
            {
                subspellItem.Bind(state.Caster, expectedDefinition, index, modal.OnActivate);
            }

            SpellCastingValidation.BindTooltipRepertoire(
                subspellItem.tooltip,
                state.Repertoire,
                state.BypassComponentsAndCastingTime ||
                state.Session is ICustomSubspellSelectionAvailability { BypassComponentsAndCastingTime: true } ||
                IsNavigationChoice(state, expectedDefinition),
                state.BypassMaterialComponent);

            if (navigation)
            {
                // Page choices use the ordinary text tooltip, without spell-school or casting metadata.
                BindNavigationTooltip(tooltip, expectedDefinition);

                if (expectedDefinition?.HasSubFeatureOfType<NavigationChoice>() == true)
                {
                    subspellItem.Button.interactable = true;
                    continue;
                }
            }

            if (state.Session is ICustomSubspellSelectionAvailability availability && expectedDefinition != null)
            {
                var available = availability.IsAvailable(expectedDefinition, out var failure);
                subspellItem.Button.interactable = available;

                if (!available && !string.IsNullOrEmpty(failure))
                {
                    tooltip.Content = Gui.FormatFailure(
                        navigation ? expectedDefinition.GuiPresentation.Description : expectedDefinition.Name,
                        failure);
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
            var navigation = Sessions.TryGetValue(modal, out var sessionState) &&
                             IsNavigationChoice(sessionState, spell);

            SpellCastingValidation.BindTooltipRepertoire(
                subspellItem.tooltip,
                repertoire,
                bypassMaterialComponent:
                state.Invocation.InvocationDefinition.OverrideMaterialComponent);

            if (navigation)
            {
                BindNavigationTooltip(subspellItem.tooltip, spell);
            }

            var available = spell != null && IsInvocationChoiceAvailable(modal, state, spell, out failure);

            subspellItem.Button.interactable = available;

            if (!available && spell != null && !string.IsNullOrEmpty(failure))
            {
                subspellItem.tooltip.Content = Gui.FormatFailure(
                    navigation ? spell.GuiPresentation.Description : spell.Name,
                    failure);
            }
        }

        Gui.InputService.RecomputeSelectableNavigation(true);
    }

    private static bool IsInvocationChoiceAvailable(
        SubspellSelectionModal modal, InvocationState state, SpellDefinition spell, out string failure)
    {
        if (Sessions.TryGetValue(modal, out var sessionState) &&
            sessionState.Session is HierarchicalSelectionSession hierarchy)
        {
            return hierarchy.IsAvailable(spell, out failure);
        }

        failure = string.Empty;

        return state.Caster is RulesetCharacterSimulacrum duplicate &&
               duplicate.CanCastInvocationSpell(state.Invocation, spell, out failure);
    }

    private static void ResetPooledSelectionState(SubspellSelectionModal modal)
    {
        var selected = Gui.InputService.CurrentSelectedGameObject;

        if (selected && selected.transform.IsChildOf(modal.subspellsTable))
        {
            // A page refresh or close releases these rows; never retain their pooled buttons as focus.
            Gui.InputService.ClearCurrentSelectable();
        }

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

        var caster = panel.Caster?.RulesetCharacter;

        if (caster == null ||
            masterSpell == null ||
            !masterSpell.SpellsBundle ||
            !caster.Invocations.Contains(invocation) ||
            caster is not RulesetCharacterSimulacrum && !HasNestedSubspells(masterSpell))
        {
            return;
        }

        InvocationSessions[modal] = new InvocationState(
            panel,
            caster,
            invocation,
            masterSpell);
    }

    internal static bool TryGetHierarchicalInvocationSelection(
        InvocationSelectionPanel panel, SpellDefinition spell, out RulesetInvocation invocation, out int index)
    {
        invocation = null;
        index = -1;

        foreach (var state in InvocationSessions.Values)
        {
            if (state.Panel != panel || !HasNestedSubspells(state.MasterSpell))
            {
                continue;
            }

            index = SpellsContext.GetSubspellLeaves(state.MasterSpell).IndexOf(spell);

            if (index >= 0)
            {
                invocation = state.Invocation;
                return true;
            }
        }

        return false;
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
                    !IsInvocationChoiceAvailable(__instance, invocationState, subspells[index], out _))
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

            var session = provider?.CreateSession(masterSpell, caster, spellRepertoire, slotLevel);

            if (session == null && HasNestedSubspells(masterSpell))
            {
                session = new HierarchicalSelectionSession(masterSpell, caster, spellRepertoire,
                    InvocationSessions.TryGetValue(__instance, out var invocation) ? invocation.Invocation : null);
            }

            if (session == null)
            {
                Sessions.Remove(__instance);
                return;
            }

            Sessions[__instance] = new SessionState(
                session,
                provider?.BypassComponentsAndCastingTime == true,
                provider?.BypassMaterialComponent == true,
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
            ResetPooledSelectionState(__instance);
            InvocationSessions.Remove(__instance);
            Sessions.Remove(__instance);

            var masterSpell = rulesetDeviceFunction?.DeviceFunctionDescription?.SpellDefinition;
            var caster = guiCharacter?.RulesetCharacter;
            var provider = masterSpell?.GetFirstSubFeatureOfType<ICustomSubspellSelectionProvider>();

            if (caster == null || provider is not WishBehavior && !HasNestedSubspells(masterSpell))
            {
                return true;
            }

            SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged = (_, selectedSpell, _) =>
            {
                if (provider is not WishBehavior)
                {
                    var index = SpellsContext.GetSubspellLeaves(masterSpell).IndexOf(selectedSpell);

                    if (index >= 0)
                    {
                        deviceFunctionEngaged?.Invoke(guiCharacter, rulesetItemDevice, rulesetDeviceFunction, 0, index);
                    }

                    return;
                }

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

            if (Sessions.TryGetValue(__instance, out var state) &&
                state.Session is HierarchicalSelectionSession hierarchy)
            {
                hierarchy.IsDeviceSelection = true;
                RebindPooledTooltipBindings(__instance, state);
            }

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
        InvocationSelectionPanel panel,
        RulesetCharacter caster,
        RulesetInvocation invocation,
        SpellDefinition masterSpell)
    {
        internal readonly InvocationSelectionPanel Panel = panel;
        internal readonly RulesetCharacter Caster = caster;
        internal readonly RulesetInvocation Invocation = invocation;
        internal readonly SpellDefinition MasterSpell = masterSpell;
    }

    private sealed class HierarchicalSelectionSession(
        SpellDefinition masterSpell,
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        RulesetInvocation invocation) : ICustomSubspellSelectionSession, ICustomSubspellSelectionAvailability
    {
        private readonly SpellDefinition _back = GetDefinition<SpellDefinition>("WishBack");
        private readonly Stack<SpellDefinition> _previousPages = [];
        private readonly HashSet<SpellDefinition> _path = [masterSpell];
        private SpellDefinition _currentPage = masterSpell;

        internal bool IsDeviceSelection { get; set; }

        public bool BypassComponentsAndCastingTime => IsDeviceSelection;

        internal bool IsNavigation(SpellDefinition spell)
        {
            return spell == _back || spell?.SpellsBundle == true;
        }

        public List<SpellDefinition> GetSubspells()
        {
            var choices = _currentPage.SubspellsList
                .Where(spell => spell != null && !_path.Contains(spell)).Distinct().ToList();

            return _previousPages.Count > 0 ? [_back, .. choices] : choices;
        }

        public bool IsAvailable(SpellDefinition spell, out string failure)
        {
            failure = string.Empty;

            if (spell == _back)
            {
                return _previousPages.Count > 0;
            }

            if (spell == null || _path.Contains(spell))
            {
                return false;
            }

            if (!spell.SpellsBundle)
            {
                return IsLeafAvailable(spell, out failure);
            }

            foreach (var leaf in SpellsContext.GetSubspellLeaves(spell))
            {
                if (IsLeafAvailable(leaf, out var leafFailure))
                {
                    failure = string.Empty;
                    return true;
                }

                if (string.IsNullOrEmpty(failure))
                {
                    failure = leafFailure;
                }
            }

            return false;
        }

        private bool IsLeafAvailable(SpellDefinition spell, out string failure)
        {
            failure = string.Empty;

            if (caster == null || spell == null || spell.SpellsBundle)
            {
                return false;
            }

            if (invocation != null && caster is RulesetCharacterSimulacrum duplicate)
            {
                return duplicate.CanCastInvocationSpell(invocation, spell, out failure);
            }

            if (invocation != null && !caster.CanCastInvocation(invocation))
            {
                return false;
            }

            var bypassMaterial = invocation?.InvocationDefinition.OverrideMaterialComponent == true;

            if (!IsDeviceSelection)
            {
                using var source = SpellCastingValidation.EnterSelectedRepertoire(repertoire);

                if (!caster.IsComponentVerbalValid(spell, out failure) ||
                    !caster.IsComponentSomaticValid(spell, out failure) ||
                    !bypassMaterial && !caster.IsComponentMaterialValid(spell, out failure))
                {
                    return false;
                }
            }

            return SpellCastingValidation.IsValid(caster, repertoire, spell, null, out failure,
                bypassComponentsAndCastingTime: IsDeviceSelection,
                bypassMaterialComponent: bypassMaterial,
                bypassSpellSlotLimit: IsDeviceSelection || invocation?.InvocationDefinition.ConsumesSpellSlot == false);
        }

        public bool OnActivate(SubspellSelectionModal modal, int index)
        {
            var choices = GetSubspells();

            if (index < 0 || index >= choices.Count || !IsAvailable(choices[index], out _))
            {
                return false;
            }

            var spell = choices[index];

            if (spell == _back)
            {
                _path.Remove(_currentPage);
                _currentPage = _previousPages.Pop();
            }
            else if (spell.SpellsBundle)
            {
                _previousPages.Push(_currentPage);
                _path.Add(spell);
                _currentPage = spell;
            }
            else
            {
                modal.spellCastEngaged?.Invoke(modal.spellRepertoire, spell, modal.slotLevel);
                modal.Hide();
                return false;
            }

            Refresh(modal);
            return false;
        }
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
