using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Diagnostics;

// Temporary runtime probes for the Simulacrum playtest. Keep these patches in one file so they
// can be removed together after the user confirms the runtime fixes.
// Bind original arguments by position so a diagnostic probe cannot block mod startup because of
// an implementation parameter-name difference in the game assembly.
[UsedImplicitly]
internal static class SimulacrumDiagnosticsPatcher
{
    [HarmonyPatch(
        typeof(GameLocationCharacter),
        nameof(GameLocationCharacter.GetActionStatus))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class GameLocationCharacter_GetActionStatus_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(
            GameLocationCharacter __instance,
            ActionDefinitions.Id __0,
            ActionDefinitions.ActionScope __1,
            ActionDefinitions.ActionStatus __2,
            RulesetAttackMode __3,
            ActionDefinitions.ActionStatus __result)
        {
            var character = __instance?.RulesetCharacter;

            if (character is RulesetCharacterSimulacrum ||
                (__0 == ActionDefinitions.Id.CastRitual &&
                 character?.SpellRepertoires.Any(repertoire =>
                     repertoire?.SpellCastingClass == Warlock) == true))
            {
                SimulacrumDiagnostics.RecordActionStatus(
                    character,
                    __0,
                    __1,
                    __2,
                    __3,
                    __result);
            }
        }
    }

    [HarmonyPatch(
        typeof(RulesetCharacterHero),
        nameof(RulesetCharacterHero.EnumerateUsableRitualSpells))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class RulesetCharacterHero_EnumerateUsableRitualSpells_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(
            RulesetCharacterHero __instance,
            RuleDefinitions.RitualCasting __0,
            List<SpellDefinition> __1)
        {
            SimulacrumDiagnostics.RecordRitualSelection(
                __instance,
                __0,
                __1);
        }
    }

    [HarmonyPatch(
        typeof(CharacterActionPanel),
        nameof(CharacterActionPanel.RefreshActionPerformances))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class CharacterActionPanel_RefreshActionPerformances_Patch
    {
        [UsedImplicitly]
        internal static void Postfix(CharacterActionPanel __instance)
        {
            if (__instance?.GuiCharacter?.RulesetCharacter is
                    RulesetCharacterSimulacrum duplicate &&
                __instance.GuiCharacter.GameLocationCharacter is { } locationCharacter)
            {
                SimulacrumDiagnostics.RecordActionState(
                    duplicate,
                    locationCharacter,
                    $"panel-{__instance.ActionScope}-{__instance.ActionType}");
            }
        }
    }

    [HarmonyPatch(typeof(CharacterActionPanel), nameof(CharacterActionPanel.OnActivateAction))]
    [HarmonyPatch([typeof(ActionDefinitions.Id), typeof(GuiCharacterAction)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class CharacterActionPanel_OnActivateAction_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(
            CharacterActionPanel __instance,
            ActionDefinitions.Id __0,
            GuiCharacterAction __1)
        {
            if (__instance?.GuiCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumDiagnostics.RecordActionActivation(
                    duplicate,
                    "panel-activated",
                    __0,
                    __instance.actionParams?.ActionDefinition ?? __1?.ActionDefinition,
                    __1?.Status ?? ActionDefinitions.ActionStatus.Irrelevant,
                    __instance.actionParams?.UsablePower);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterActionPanel), nameof(CharacterActionPanel.ActionStarted))]
    [HarmonyPatch([typeof(CharacterAction)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class CharacterActionPanel_ActionStarted_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(CharacterAction __0)
        {
            if (__0?.ActingCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumDiagnostics.RecordActionActivation(
                    duplicate,
                    "action-started",
                    __0.ActionId,
                    __0.ActionDefinition,
                    ActionDefinitions.ActionStatus.Available,
                    __0.ActionParams?.UsablePower,
                    __0.GetType().Name);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterActionPanel), nameof(CharacterActionPanel.ActionExecuted))]
    [HarmonyPatch([typeof(CharacterAction)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class CharacterActionPanel_ActionExecuted_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(CharacterAction __0)
        {
            if (__0?.ActingCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumDiagnostics.RecordActionActivation(
                    duplicate,
                    "action-executed",
                    __0.ActionId,
                    __0.ActionDefinition,
                    ActionDefinitions.ActionStatus.Available,
                    __0.ActionParams?.UsablePower,
                    __0.GetType().Name);
            }
        }
    }

    [HarmonyPatch(
        typeof(GameLocationVisibilityManager),
        nameof(GameLocationVisibilityManager.UpdatePerception))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class GameLocationVisibilityManager_UpdatePerception_Patch
    {
        [UsedImplicitly]
        internal static void Postfix()
        {
            if (ServiceRepository.GetService<IGameLocationCharacterService>() is not
                { } characterService)
            {
                return;
            }

            foreach (var locationCharacter in characterService.ValidCharacters)
            {
                if (locationCharacter?.RulesetCharacter is
                    RulesetCharacterSimulacrum duplicate)
                {
                    SimulacrumDiagnostics.RecordPerceptionState(
                        duplicate,
                        locationCharacter);
                }
            }
        }
    }

    [HarmonyPatch(
        typeof(GameLocationVisibilityManager),
        nameof(GameLocationVisibilityManager.IsCharacterPerceivedByCharacter))]
    [HarmonyPatch([typeof(GameLocationCharacter), typeof(GameLocationCharacter)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class GameLocationVisibilityManager_IsCharacterPerceivedByCharacter_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(
            GameLocationCharacter __0,
            GameLocationCharacter __1,
            bool __result)
        {
            if (!__result &&
                __1?.RulesetCharacter is RulesetCharacterSimulacrum
                {
                    LifecycleState: SimulacrumLifecycleState.Ready
                } duplicate)
            {
                SimulacrumDiagnostics.RecordFailedPerception(duplicate, __1, __0);
            }
        }
    }

    [HarmonyPatch(typeof(RulesetCharacter), nameof(RulesetCharacter.CanSenseTarget))]
    [HarmonyPatch([typeof(RulesetCharacter)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class RulesetCharacter_CanSenseTarget_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(
            RulesetCharacter __instance,
            RulesetCharacter __0,
            bool __result)
        {
            if (__instance is RulesetCharacterSimulacrum
                {
                    LifecycleState: SimulacrumLifecycleState.Ready
                } duplicate)
            {
                SimulacrumDiagnostics.RecordSenseValidation(duplicate, __0, __result);
            }
        }
    }

    [HarmonyPatch(
        typeof(RulesetActor),
        nameof(RulesetActor.CurrentHitPoints),
        MethodType.Setter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class CurrentHitPoints_Setter_Patch
    {
        [UsedImplicitly]
        internal static void Prefix(
            RulesetActor __instance,
            int __0,
            out int __state)
        {
            __state = __instance is RulesetCharacterSimulacrum
                ? __instance.CurrentHitPoints
                : -1;
        }

        [UsedImplicitly]
        internal static void Postfix(
            RulesetActor __instance,
            int __0,
            int __state)
        {
            if (__state >= 0 && __instance is RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumDiagnostics.RecordHealthMutation(
                    duplicate,
                    __state,
                    __0,
                    duplicate.CurrentHitPoints);
            }
        }
    }
}
