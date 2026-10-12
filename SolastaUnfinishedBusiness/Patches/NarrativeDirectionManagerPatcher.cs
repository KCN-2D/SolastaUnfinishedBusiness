using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeDirectionManagerPatcher
{
    private sealed class SpeechSequenceLifecycle
    {
        internal bool Closed;
        internal readonly HashSet<NarrativeStateBaseSpeech> Owners = [];
    }

    // These identities are transient, main-thread narrative states, never saved campaign data.
    private static readonly ConditionalWeakTable<NarrativeSequence, SpeechSequenceLifecycle> SpeechSequences = new();

    internal static bool RegisterSpeechState(NarrativeStateBaseSpeech state)
    {
        var sequence = state.NarrativeSequence;
        if (sequence == null || state.IsSkipped)
        {
            return false;
        }

        var lifecycle = SpeechSequences.GetValue(sequence, _ => new SpeechSequenceLifecycle());
        if (lifecycle.Closed)
        {
            return false;
        }

        lifecycle.Owners.Add(state);
        return true;
    }

    internal static void OpenSequenceSpeech(NarrativeSequence sequence)
    {
        if (sequence != null)
        {
            SpeechSequences.GetValue(sequence, _ => new SpeechSequenceLifecycle()).Closed = false;
        }
    }

    internal static void CancelSequenceSpeech(NarrativeSequence sequence)
    {
        if (sequence == null)
        {
            return;
        }

        var lifecycle = SpeechSequences.GetValue(sequence, _ => new SpeechSequenceLifecycle());
        lifecycle.Closed = true;
        foreach (var owner in lifecycle.Owners)
        {
            SpeechContext.CancelSpeech(owner);
        }

        lifecycle.Owners.Clear();
    }

    private static void EndStateSpeech(NarrativeStateBaseSpeech state)
    {
        SpeechContext.CancelSpeech(state);
        if (state.NarrativeSequence != null && SpeechSequences.TryGetValue(state.NarrativeSequence, out var lifecycle))
        {
            lifecycle.Owners.Remove(state);
        }
    }

    private static float GetEffectiveSpeechDuration(NarrativeStateBaseSpeech state)
    {
        // Hold the vanilla subtitle/StopSpeaking notification as well as automatic
        // advancement while this exact line is queued, being synthesized or playing.
        return !state.IsSkipped && SpeechContext.HasPendingSpeech(state)
            ? float.PositiveInfinity
            : state.speechDuration;
    }

    [HarmonyPatch(typeof(NarrativeState), nameof(NarrativeState.CanMoveToNextState), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanMoveToNextState_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeState __instance, ref bool __result)
        {
            if (__result && __instance is NarrativeStateBaseSpeech && !__instance.IsSkipped &&
                SpeechContext.HasPendingSpeech(__instance))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(NarrativeState), nameof(NarrativeState.IsSkipped), MethodType.Setter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsSkipped_Setter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeState __instance, bool value)
        {
            if (value && __instance is NarrativeStateBaseSpeech speechState)
            {
                EndStateSpeech(speechState);
            }
        }
    }

    [HarmonyPatch(typeof(NarrativeStateBaseSpeech), nameof(NarrativeStateBaseSpeech.Update))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpeechUpdate_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var duration = AccessTools.Field(typeof(NarrativeStateBaseSpeech),
                nameof(NarrativeStateBaseSpeech.speechDuration));
            if (code.Count(instruction => instruction.LoadsField(duration)) != 1)
            {
                Main.Error("Failed to apply transpiler patch [NarrativeStateBaseSpeech.Update]: " +
                           "expected one speech finish duration check.");
                return code;
            }

            var effectiveDuration = AccessTools.Method(typeof(NarrativeDirectionManagerPatcher),
                nameof(GetEffectiveSpeechDuration));
            foreach (var instruction in code)
            {
                // Only the finish check loads this field. The initial animation receives
                // the unchanged SpeechDuration property rather than an infinite duration.
                if (instruction.LoadsField(duration))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = effectiveDuration;
                }
            }

            return code;
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpeechStateLifecycle_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NarrativeStateBaseSpeech), nameof(NarrativeStateBaseSpeech.Begin));
            yield return AccessTools.Method(typeof(NarrativeStateBaseSpeech), nameof(NarrativeStateBaseSpeech.End));
        }

        [UsedImplicitly]
        public static void Prefix(NarrativeStateBaseSpeech __instance)
        {
            EndStateSpeech(__instance);
        }
    }

    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.PlayNarrativeSequence))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PlayNarrativeSequence_Patch
    {
        [UsedImplicitly]
        public static void Prefix(NarrativeSequence narrativeSequence)
        {
            OpenSequenceSpeech(narrativeSequence);
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SequenceSpeechEnded_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NarrativeDirectionManager),
                nameof(NarrativeDirectionManager.AbortNarrativeSequence));
            yield return AccessTools.Method(typeof(NarrativeDirectionManager),
                nameof(NarrativeDirectionManager.ConcludeNarrativeSequence));
            yield return AccessTools.Method(typeof(NarrativeDirectionManager),
                nameof(NarrativeDirectionManager.UnbindServices));
        }

        [UsedImplicitly]
        public static void Prefix(NarrativeDirectionManager __instance)
        {
            CancelSequenceSpeech(__instance.NarrativePlayer?.NarrativeSequence);
        }
    }

    [HarmonyPatch(typeof(NarrativePlayer), nameof(NarrativePlayer.Shutdown))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PlayerShutdown_Patch
    {
        [UsedImplicitly]
        public static void Prefix(NarrativePlayer __instance)
        {
            CancelSequenceSpeech(__instance.NarrativeSequence);
        }
    }

    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.PrepareDialogSequence))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PrepareDialogSequence_Patch
    {
        [UsedImplicitly]
        public static void Prefix(List<GameLocationCharacter> involvedGameCharacters)
        {
            //PATCH: Don't offer controlled conjurations on dialogue sequences (FullyControlConjurations)
            if (Main.Settings.FullyControlConjurations)
            {
                involvedGameCharacters.RemoveAll(
                    x => x.RulesetCharacter is RulesetCharacterMonster rulesetCharacterMonster
                         && Tabletop2014Context.ConjuredMonsters.Contains(rulesetCharacterMonster
                             .MonsterDefinition));
            }

            //PATCH: Only offer the first 4 players on dialogue sequences (PARTYSIZE)
            if (Main.Settings.OverridePartySize <= ToolsContext.GamePartySize
                || Main.Settings.AllowAllPlayersOnNarrativeSequences)
            {
                return;
            }

            {
                var party = Gui.GameCampaign.Party.CharactersList
                    .Select(x => x.RulesetCharacter)
                    .ToList();

                involvedGameCharacters.RemoveAll(
                    x => party.IndexOf(x.RulesetCharacter) >= ToolsContext.GamePartySize);
            }
        }
    }

    //PATCH: EnableLogDialoguesToConsole
    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.StartDialogSequence))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartDialogSequence_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            if (!Main.Settings.EnableLogDialoguesToConsole)
            {
                return;
            }

            var screen = Gui.GuiService.GetScreen<GuiConsoleScreen>();

            screen.Show();
        }
    }
}
