using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class InvocationSubPanelPatcher
{
    [HarmonyPatch(typeof(InvocationSubPanel), nameof(InvocationSubPanel.RuntimeLoaded))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RuntimeLoaded_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(InvocationSubPanel __instance)
        {
            if (!PermanentDefensesSubPanel.IsDefensesPanel(__instance) &&
                !CharacterInspectionScreenEnhancement.IsInspectionProficiencyPanel(__instance))
            {
                return true;
            }

            // An inspection has no candidates to choose until its hero is bound.
            // Selection panels retain the native complete catalog and row preparation.
            __instance.relevantInvocations.Clear();
            return false;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var count = typeof(List<InvocationDefinition>).GetProperty(nameof(List<InvocationDefinition>.Count))?.GetMethod;
            var initialCount = new Func<int, ProficienciesSubPanel, int>(
                CharacterInspectionScreenEnhancement.GetInitialProficiencyItemCount).Method;

            return instructions.ReplaceCalls(count, 1, "InvocationSubPanel.RuntimeLoaded",
                new CodeInstruction(OpCodes.Callvirt, count),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, initialCount));
        }
    }

    [HarmonyPatch(typeof(InvocationSubPanel), nameof(InvocationSubPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(InvocationSubPanel __instance, RulesetCharacterHero __0,
            ProficiencyBaseItem.OnItemClickedHandler __1)
        {
            if (PermanentDefensesSubPanel.TryBind(__instance, __0, __1))
            {
                return false;
            }

            //PATCH: support for custom invocations and separate sub-panels for them
            //filters only invocations that fit this sub-panel
            CustomInvocationSubPanel.PrepareRelevantInvocations(__instance, __0);

            // //PATCH: sorts the invocations panel by Title
            InvocationsContext.SortInvocations(__instance);
            return true;
        }
    }

    [HarmonyPatch(typeof(InvocationSubPanel), nameof(InvocationSubPanel.SetState))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetState_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(InvocationSubPanel __instance, bool active)
        {
            if (!PermanentDefensesSubPanel.IsDefensesPanel(__instance))
            {
                return true;
            }

            __instance.Selected = active;
            return false;
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var forceRebuildLayoutImmediateMethod = typeof(LayoutRebuilder)
                .GetMethod("ForceRebuildLayoutImmediate", BindingFlags.Static | BindingFlags.Public);
            var forceSameWidthMethod =
                new Action<RectTransform, bool, InvocationSubPanel>(InvocationsContext.ForceSameWidth).Method;

            var getInvocationProficiencies = typeof(RulesetCharacterHero).GetMethod("get_InvocationProficiencies");
            var customInvocationsProficiencies =
                new Func<RulesetCharacterHero, InvocationSubPanel, List<string>>(CustomInvocationSubPanel
                        .CustomInvocationsProficiencies)
                    .Method;

            return instructions
                //PATCH: enforces the invocations selection panel to always display same-width columns
                .ReplaceCalls(forceRebuildLayoutImmediateMethod, "InvocationSubPanel.SetState.1",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, forceSameWidthMethod))
                //PATCH: don't offer invocations unlearn on non Warlock classes (MULTICLASS)
                .ReplaceCalls(getInvocationProficiencies,
                    "InvocationSubPanel.SetState.2",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, customInvocationsProficiencies));
        }
    }
}
