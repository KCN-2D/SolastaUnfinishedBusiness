using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class RitualBoxPatcher
{
    [HarmonyPatch(typeof(RitualBox), nameof(RitualBox.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class Bind_Patch
    {
        [UsedImplicitly]
        private static void Prefix(
            RitualBox __instance,
            RulesetCharacter __0,
            SpellDefinition __1,
            out IDisposable __state)
        {
            __state = null;
            SpellCastingValidation.BindTooltipRepertoire(__instance.GuiTooltip, null);

            if (__0 is not RulesetCharacterSimulacrum duplicate)
            {
                return;
            }

            var repertoire = SimulacrumBehavior.ResolveRitualRepertoire(
                duplicate,
                __1);

            SpellCastingValidation.BindTooltipRepertoire(
                __instance.GuiTooltip,
                repertoire);
            __state = SpellCastingValidation.EnterSelectedRepertoire(repertoire);
            SimulacrumDiagnostics.RecordSpellActivation(
                "ritual-bind",
                duplicate,
                repertoire,
                __1);
        }

        [UsedImplicitly]
        private static void Postfix(
            RitualBox __instance,
            RulesetCharacter __0,
            SpellDefinition __1,
            CanvasGroup ___canvasGroup,
            Image ___image,
            Material ___unavailableMaterial)
        {
            if (__0 is not RulesetCharacterSimulacrum duplicate)
            {
                return;
            }

            var repertoire = SimulacrumBehavior.ResolveRitualRepertoire(duplicate, __1);

            if (SpellCastingValidation.IsValid(
                    duplicate,
                    repertoire,
                    __1,
                    null,
                    out _))
            {
                return;
            }

            ___canvasGroup.interactable = false;
            ___image.color = Color.grey;
            ___image.material = ___unavailableMaterial;
        }

        [UsedImplicitly]
        private static Exception Finalizer(
            Exception __exception,
            IDisposable __state)
        {
            __state?.Dispose();

            return __exception;
        }
    }
}
