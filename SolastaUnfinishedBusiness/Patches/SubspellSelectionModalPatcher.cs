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
using SolastaUnfinishedBusiness.Diagnostics;
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
            ClearPooledTooltipBindings(modal, state);
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

    private static void ClearPooledTooltipBindings(
        SubspellSelectionModal modal,
        SessionState state)
    {
        var subspellItems = modal.subspellsTable.GetComponentsInChildren<SubspellItem>(true);

        foreach (var subspellItem in subspellItems)
        {
            SpellCastingValidation.BindTooltipRepertoire(subspellItem.tooltip, null);
        }

        SimulacrumDiagnostics.Write(
            "subspell-modal",
            $"stage=refresh-unbind page={state.PageRevision} master={state.MasterSpell.Name} " +
            $"items={subspellItems.Length}");
    }

    private static void RebindPooledTooltipBindings(
        SubspellSelectionModal modal,
        SessionState state)
    {
        var expectedDefinitions = state.Session.GetSubspells();
        var subspellItems = modal.subspellsTable.GetComponentsInChildren<SubspellItem>(true);

        foreach (var subspellItem in subspellItems)
        {
            if (!subspellItem.gameObject.activeInHierarchy)
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
                SimulacrumDiagnostics.Write(
                    "subspell-modal",
                    $"stage=binding-mismatch page={state.PageRevision} master={state.MasterSpell.Name} " +
                    $"index={index} expected={expectedDefinition.Name} actual={actualDefinitionName}");

                subspellItem.Bind(state.Caster, expectedDefinition, index, modal.OnActivate);
            }

            SpellCastingValidation.BindTooltipRepertoire(
                subspellItem.tooltip,
                state.Repertoire,
                state.BypassComponentsAndCastingTime);
        }

        SimulacrumDiagnostics.Write(
            "subspell-modal",
            $"stage=refresh-rebind page={state.PageRevision} master={state.MasterSpell.Name} " +
            $"items={subspellItems.Length} expected={expectedDefinitions.Count}");
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
            SimulacrumDiagnostics.RecordInvocationSubspellAvailability(
                state.Caster,
                state.Invocation.InvocationDefinition,
                spell,
                "modal-bind",
                available,
                subspellItem.Button.interactable,
                failure);

            if (!available && spell != null && !string.IsNullOrEmpty(failure))
            {
                subspellItem.tooltip.Content = Gui.FormatFailure(spell.Name, failure);
            }
        }

        Gui.InputService.RecomputeSelectableNavigation(true);
    }

    private static void ResetInvocationAvailability(SubspellSelectionModal modal)
    {
        foreach (var subspellItem in
                 modal.subspellsTable.GetComponentsInChildren<SubspellItem>(true))
        {
            subspellItem.Button.interactable = true;
            SpellCastingValidation.BindTooltipRepertoire(subspellItem.tooltip, null);
        }
    }

    [HarmonyPatch(typeof(InvocationSelectionPanel), nameof(InvocationSelectionPanel.OnInvocationSelected))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnInvocationSelected_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            InvocationSelectionPanel __instance,
            InvocationActivationBox invocationActivationBox)
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

            if (__instance.Caster?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate ||
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
                var failure = string.Empty;

                if (index < 0 ||
                    index >= subspells.Count ||
                    !invocationState.Caster.CanCastInvocationSpell(
                        invocationState.Invocation,
                        subspells[index],
                        out failure))
                {
                    var actualInteractable = __instance.subspellsTable
                        .GetComponentsInChildren<SubspellItem>(true)
                        .FirstOrDefault(item =>
                            item.gameObject.activeSelf &&
                            item.index == index)?
                        .Button.interactable ?? false;

                    SimulacrumDiagnostics.RecordInvocationSubspellAvailability(
                        invocationState.Caster,
                        invocationState.Invocation.InvocationDefinition,
                        index >= 0 && index < subspells.Count ? subspells[index] : null,
                        "click-blocked",
                        false,
                        actualInteractable,
                        failure);

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
        [UsedImplicitly]
        public static void Prefix(
            SubspellSelectionModal __instance,
            SpellDefinition masterSpell,
            RulesetCharacter caster,
            RulesetSpellRepertoire spellRepertoire,
            SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
            int slotLevel,
            RectTransform masterSpellBox)
        {
            ResetInvocationAvailability(__instance);

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
                provider is WishBehavior,
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
                __instance,
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
                ResetInvocationAvailability(__instance);
                InvocationSessions.Remove(__instance);

                return;
            }

            if (state.IsRefreshing)
            {
                return;
            }

            ResetInvocationAvailability(__instance);
            InvocationSessions.Remove(__instance);
            ClearPooledTooltipBindings(__instance, state);
            Sessions.Remove(__instance);

            SimulacrumDiagnostics.Write(
                "subspell-modal",
                $"stage=end-hide page={state.PageRevision} master={state.MasterSpell.Name}");
        }
    }

    [HarmonyPatch(typeof(SubspellSelectionModal), nameof(SubspellSelectionModal.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SubspellSelectionModal __instance)
        {
            if (!Sessions.TryGetValue(__instance, out var state))
            {
                ResetInvocationAvailability(__instance);
                InvocationSessions.Remove(__instance);

                return;
            }

            if (!state.IsRefreshing)
            {
                ResetInvocationAvailability(__instance);
                InvocationSessions.Remove(__instance);
                ClearPooledTooltipBindings(__instance, state);
                Sessions.Remove(__instance);
            }
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
        internal readonly SpellDefinition MasterSpell = masterSpell;
        internal readonly RectTransform MasterSpellBox = masterSpellBox;
        internal readonly RulesetSpellRepertoire Repertoire = repertoire;
        internal readonly ICustomSubspellSelectionSession Session = session;
        internal readonly int SlotLevel = slotLevel;
        internal readonly SpellsByLevelBox.SpellCastEngagedHandler SpellCastEngaged = spellCastEngaged;
        internal bool IsRefreshing;
        internal int PageRevision { get; private set; }

        internal void BeginRefresh()
        {
            IsRefreshing = true;
            PageRevision++;
        }

        internal void EndRefresh()
        {
            IsRefreshing = false;
        }
    }
}
