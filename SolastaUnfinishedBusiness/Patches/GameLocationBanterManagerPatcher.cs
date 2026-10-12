using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using TA;
using UnityEngine;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationBanterManagerPatcher
{
    private sealed class BanterSpeechLifecycle
    {
        internal bool Closed;
        internal readonly HashSet<BanterSpeechOwner> Owners = [];
    }

    private sealed class BanterSpeechOwner(BanterSpeechLifecycle lifecycle, GameLocationCharacter speaker = null)
    {
        internal readonly BanterSpeechLifecycle Lifecycle = lifecycle;
        internal readonly GameLocationCharacter Speaker = speaker;
        internal readonly List<FeedbackLabel> FeedbackLabels = [];
        internal readonly List<GuiModifier> Modifiers = [];
        internal bool Ended;
    }

    private static readonly ConditionalWeakTable<GameLocationBanterManager, BanterSpeechLifecycle> BanterSpeech = new();
    private static readonly Dictionary<GameLocationCharacter, BanterSpeechOwner> CharacterSpeech = [];
    private static readonly Dictionary<FeedbackLabel, BanterSpeechOwner> FeedbackSpeech = [];
    private static readonly Dictionary<GuiModifier, BanterSpeechOwner> FeedbackModifiers = [];
    private static BanterSpeechOwner _displayingConversation;

    private static void EndBanterSpeech(BanterSpeechOwner owner, bool closeFeedback = true)
    {
        if (owner == null || owner.Ended)
        {
            return;
        }

        owner.Ended = true;
        SpeechContext.CancelSpeech(owner);
        owner.Lifecycle.Owners.Remove(owner);
        if (owner.Speaker != null && CharacterSpeech.TryGetValue(owner.Speaker, out var current) && current == owner)
        {
            CharacterSpeech.Remove(owner.Speaker);
        }

        foreach (var label in owner.FeedbackLabels)
        {
            if (!FeedbackSpeech.TryGetValue(label, out var currentFeedback) || currentFeedback != owner)
            {
                continue;
            }

            FeedbackSpeech.Remove(label);
            if (!label || !label.textLabel)
            {
                continue;
            }

            if (closeFeedback)
            {
                label.textLabel.StopAllModifiers();
            }
        }

        foreach (var modifier in owner.Modifiers)
        {
            if (FeedbackModifiers.TryGetValue(modifier, out var currentModifier) && currentModifier == owner)
            {
                FeedbackModifiers.Remove(modifier);
            }
        }

        owner.FeedbackLabels.Clear();
        owner.Modifiers.Clear();
    }

    internal static void EndCharacterBanterSpeech(GameLocationCharacter character)
    {
        if (character != null && CharacterSpeech.TryGetValue(character, out var owner))
        {
            EndBanterSpeech(owner);
        }
    }

    internal static void HoldCharacterBanterLabel(CharacterLabel label)
    {
        var character = label.GuiCharacter?.GameLocationCharacter;
        if (character == null || !CharacterSpeech.TryGetValue(character, out var owner))
        {
            return;
        }

        if (!SpeechContext.HasPendingSpeech(owner))
        {
            EndBanterSpeech(owner);
            return;
        }

        // Keep a finite native timer and let its normal fade resume after speech.
        label.remainingDialogLineTime = Mathf.Max(label.remainingDialogLineTime, Gui.SystemDeltaTime + 0.1f);
    }

    internal static void RegisterBanterFeedback(FeedbackLabel label)
    {
        var owner = _displayingConversation;
        if (FeedbackSpeech.TryGetValue(label, out var previous))
        {
            // The pooled label has already been rebound; do not stop its new modifiers.
            EndBanterSpeech(previous, false);
        }

        if (owner == null || owner.Ended || !label.textLabel)
        {
            return;
        }

        owner.FeedbackLabels.Add(label);
        FeedbackSpeech[label] = owner;
        foreach (var modifier in label.textLabel.GuiModifiers)
        {
            if (modifier)
            {
                owner.Modifiers.Add(modifier);
                FeedbackModifiers[modifier] = owner;
            }
        }
    }

    internal static bool HoldBanterFeedbackModifier(GuiModifier modifier)
    {
        // Allow the native fade-in first. Freeze only owned dialogue labels, never
        // combat feedback, and preserve finite animation durations for pool reuse.
        return FeedbackModifiers.TryGetValue(modifier, out var owner) && !owner.Ended &&
               modifier.Running && modifier.CurrentTime >= modifier.Duration * 0.5f &&
               SpeechContext.HasPendingSpeech(owner);
    }

    internal static void EndCurrentBanterSpeech()
    {
        if (ServiceRepository.GetService<IGameLocationBanterService>() is GameLocationBanterManager manager)
        {
            EndManagerBanterSpeech(manager);
        }
    }

    internal static void OpenCurrentBanterSpeech()
    {
        if (ServiceRepository.GetService<IGameLocationBanterService>() is GameLocationBanterManager manager)
        {
            ResetManagerBanterSpeech(manager);
        }
    }

    private static void ResetManagerBanterSpeech(GameLocationBanterManager manager)
    {
        // Rebinding a manager or reopening its screen starts a new lifecycle.
        // Suspended old iterators retain the closed lifecycle and cannot revive it.
        EndManagerBanterSpeech(manager);
        BanterSpeech.Remove(manager);
    }

    private static void EndManagerBanterSpeech(GameLocationBanterManager manager)
    {
        if (!BanterSpeech.TryGetValue(manager, out var lifecycle))
        {
            return;
        }

        lifecycle.Closed = true;
        foreach (var owner in lifecycle.Owners.ToArray())
        {
            EndBanterSpeech(owner);
        }
    }

    private static IEnumerator TrackBanterConversation(GameLocationBanterManager manager,
        List<ComputedBanterLine> lines, IEnumerator native)
    {
        var lifecycle = BanterSpeech.GetValue(manager, _ => new BanterSpeechLifecycle());
        BanterSpeechOwner owner = null;
        var index = 0;
        try
        {
            while (!lifecycle.Closed)
            {
                owner = new BanterSpeechOwner(lifecycle);
                lifecycle.Owners.Add(owner);
                var previous = _displayingConversation;
                _displayingConversation = owner;
                bool moved;
                try
                {
                    moved = native.MoveNext();
                }
                finally
                {
                    _displayingConversation = previous;
                }

                if (!moved)
                {
                    break;
                }

                // The native iterator displays exactly one ComputedBanterLine per yield.
                var submitted = !owner.Ended && !lifecycle.Closed && index < lines.Count &&
                                SpeechContext.SpeakBanterConversationLine(
                    lines[index].line, lines[index].speaker, owner);
                index++;
                if (!submitted)
                {
                    EndBanterSpeech(owner, false);
                }

                yield return native.Current;
                while (!lifecycle.Closed && !owner.Ended && SpeechContext.HasPendingSpeech(owner))
                {
                    yield return null;
                }

                // Release the native finite fade after normal playback. Explicit
                // cancellation/disposal closes its feedback immediately instead.
                EndBanterSpeech(owner, false);
                owner = null;
            }
        }
        finally
        {
            EndBanterSpeech(owner);
            (native as IDisposable)?.Dispose();
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TriggerIncantation_Patch
    {
        private static readonly AccessTools.FieldRef<GameLocationBanterManager, CoroutineList>
            CoroutineListRef =
                AccessTools.FieldRefAccess<GameLocationBanterManager, CoroutineList>("coroutineList");

        private static readonly MethodInfo PlayIncantationAsync = AccessTools.DeclaredMethod(
            typeof(GameLocationBanterManager),
            "PlayIncantationAsync",
            [
                typeof(GameLocationCharacter),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(bool)
            ]);

        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            return AccessTools.DeclaredMethod(
                typeof(GameLocationBanterManager),
                "TriggerIncantation",
                [typeof(ActionDefinitions.MagicEffectCastData).MakeByRefType()]);
        }

        [UsedImplicitly]
        public static bool Prefix(
            GameLocationBanterManager __instance,
            ref ActionDefinitions.MagicEffectCastData __0)
        {
            if (__0.Caster?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            if (!TryPrepareIncantation(
                    duplicate,
                    ref __0,
                    out var seme1,
                    out var seme2,
                    out var seme3,
                    out var onlyPlaySeme3,
                    out var reason))
            {
                return ShouldUseNativeFallback(reason);
            }

            IEnumerator coroutine;

            try
            {
                coroutine = PlayIncantationAsync.Invoke(
                    __instance,
                    [__0.Caster, seme1, seme2, seme3, onlyPlaySeme3]) as IEnumerator;
            }
            catch (Exception ex)
            {
                Trace.LogException(new Exception(
                    "Unable to start a Simulacrum incantation.",
                    ex));

                return true;
            }

            if (coroutine == null)
            {
                Trace.LogWarning(
                    "Unable to play a Simulacrum incantation because no playback coroutine was created.");

                return true;
            }

            try
            {
                CoroutineListRef(__instance).Add(
                    TrackIncantation(coroutine),
                    false);
            }
            catch (Exception ex)
            {
                Trace.LogException(new Exception(
                    "Unable to queue a Simulacrum incantation.",
                    ex));

                return true;
            }

            return false;
        }

        private static IEnumerator TrackIncantation(IEnumerator coroutine)
        {
            while (true)
            {
                bool moveNext;

                try
                {
                    moveNext = coroutine.MoveNext();
                }
                catch (Exception ex)
                {
                    Trace.LogException(new Exception(
                        "Simulacrum incantation playback failed.",
                        ex));

                    throw;
                }

                if (!moveNext)
                {
                    break;
                }

                yield return coroutine.Current;
            }
        }

        private static bool ShouldUseNativeFallback(string reason)
        {
            return reason is "spell-missing" or "voice-line-missing";
        }

        private static bool TryPrepareIncantation(
            RulesetCharacterSimulacrum duplicate,
            ref ActionDefinitions.MagicEffectCastData castData,
            out string seme1,
            out string seme2,
            out string seme3,
            out bool onlyPlaySeme3,
            out string reason)
        {
            seme1 = null;
            seme2 = null;
            seme3 = null;
            onlyPlaySeme3 = false;
            reason = null;

            if (ServiceRepository.GetService<IGameSettingsService>() is
                { VocalSpellIncantation: false })
            {
                reason = "setting-disabled";
                return false;
            }

            if (ServiceRepository.GetService<INarrativeDirectionService>() is
                { IsSequenceInProgress: true })
            {
                reason = "narrative-sequence";
                return false;
            }

            if (castData.Caster == null ||
                castData.Caster.IsSpeaking ||
                castData.Caster.Stealthy)
            {
                reason = castData.Caster == null
                    ? "caster-missing"
                    : castData.Caster.IsSpeaking
                        ? "already-speaking"
                        : "stealthy";
                return false;
            }

            if (!DatabaseRepository.GetDatabase<SpellDefinition>()
                    .TryGetElement(castData.Source, out var spell) &&
                !RulesetEffectSpellWithOrigin.TryGetVocalOrigin(
                    duplicate,
                    castData.Source,
                    out spell,
                    out _,
                    out _))
            {
                reason = "spell-missing";
                return false;
            }

            var vocalSpell = ResolveVocalSpell(duplicate, spell);

            if (!vocalSpell.VerboseComponent)
            {
                reason = "no-verbal-component";
                return false;
            }

            if (duplicate.IsSpeechImpaired())
            {
                reason = "speech-impaired";
                return false;
            }

            if (duplicate.IsUsingMusicalInstrumentWhenCasting &&
                duplicate.CharacterInventory?.GetEquipedMusicalInstrument() is { } instrument)
            {
                var instrumentType = DatabaseRepository
                    .GetDatabase<MusicalInstrumentTypeDefinition>()
                    .GetElement(
                        instrument.ItemDefinition.MusicalInstrumentDescription.MusicalInstrumentType);

                if (!instrumentType.IsCompatibleWithSpellVocalization)
                {
                    reason = "instrument-incompatible";
                    return false;
                }
            }

            if (castData.Subtle)
            {
                reason = "subtle";
                return false;
            }

            var repertoire = SpellCastingValidation.ResolveRepertoire(
                duplicate,
                null,
                spell);
            var characterClass = ResolveVocalClass(duplicate, repertoire);
            var semeClass = ResolveVocalSemeClass(duplicate, characterClass);
            seme1 = GameConfiguration.Banter.GetVoiceLineIdBySemeClass(semeClass);
            seme2 = GameConfiguration.Banter.GetVoiceLineIdBySemeSchool(vocalSpell.SchoolOfMagic);
            seme3 = GameConfiguration.Banter.GetVoiceLineIdBySemeType(vocalSpell.VocalSpellSemeType);

            // Wish's static options have no independent vocal metadata. If an
            // originating spell did not resolve a voice line, preserve the
            // selected spell as a final metadata fallback.
            if (!ReferenceEquals(vocalSpell, spell))
            {
                if (string.IsNullOrEmpty(seme2))
                {
                    seme2 = GameConfiguration.Banter.GetVoiceLineIdBySemeSchool(spell.SchoolOfMagic);
                }

                if (string.IsNullOrEmpty(seme3))
                {
                    seme3 = GameConfiguration.Banter.GetVoiceLineIdBySemeType(spell.VocalSpellSemeType);
                }
            }

            if (string.IsNullOrWhiteSpace(seme1) ||
                string.IsNullOrEmpty(seme2) ||
                string.IsNullOrEmpty(seme3))
            {
                if (string.IsNullOrWhiteSpace(seme1))
                {
                    seme1 = GameConfiguration.Banter.GetVoiceLineIdBySemeClass(
                        VocalSpellSemeClass.Arcana);
                }

                if (string.IsNullOrEmpty(seme2))
                {
                    seme2 = GameConfiguration.Banter.GetVoiceLineIdBySemeSchool(
                        SchoolOfMagicDefinitions.SchoolEvocation.Name);
                }

                if (string.IsNullOrEmpty(seme3))
                {
                    seme3 = GameConfiguration.Banter.GetVoiceLineIdBySemeType(
                        VocalSpellSemeType.Buff);
                }

                if (string.IsNullOrWhiteSpace(seme1) ||
                    string.IsNullOrEmpty(seme2) ||
                    string.IsNullOrEmpty(seme3))
                {
                    reason = "voice-line-missing";
                    return false;
                }
            }

            onlyPlaySeme3 =
                ServiceRepository.GetService<IGameLocationBattleService>() is
                    { IsBattleInProgress: true } &&
                castData.IsQuickSpell;

            return true;
        }

        private static VocalSpellSemeClass ResolveVocalSemeClass(
            RulesetCharacterSimulacrum duplicate,
            CharacterClassDefinition characterClass)
        {
            if (characterClass?.VocalSpellSemeClass is { } classSeme &&
                classSeme != VocalSpellSemeClass.None)
            {
                return classSeme;
            }

            if (duplicate.DeityDefinition?.VocalSpellSemeClass is { } deitySeme &&
                deitySeme != VocalSpellSemeClass.None)
            {
                return deitySeme;
            }

            // Feat/race repertoires have no spellcasting class. Arcana is the
            // stable native voice family used when no source-specific family
            // exists; it keeps verbal components audible without inventing a
            // class or bypassing stealth/subtle restrictions.
            return VocalSpellSemeClass.Arcana;
        }

        private static SpellDefinition ResolveVocalSpell(
            RulesetCharacterSimulacrum duplicate,
            SpellDefinition spell)
        {
            if (RulesetEffectSpellWithOrigin.TryGetVocalOrigin(
                    duplicate,
                    spell,
                    out var originatingSpell,
                    out var mode) &&
                mode != RulesetEffectSpellWithOrigin.OriginMode.None)
            {
                return originatingSpell;
            }

            if (SpellsContext.SpellsChildMaster.TryGetValue(spell, out var masterSpell) &&
                masterSpell.Name == "Wish")
            {
                return masterSpell;
            }

            return spell;
        }

        private static CharacterClassDefinition ResolveVocalClass(
            RulesetCharacterSimulacrum duplicate,
            RulesetSpellRepertoire repertoire)
        {
            var characterClass = repertoire?.SpellCastingClass;

            if (!characterClass && repertoire?.SpellCastingSubclass)
            {
                characterClass = LevelUpHelper.GetClassForSubclass(
                    repertoire.SpellCastingSubclass);
            }

            if (characterClass?.VocalSpellSemeClass != VocalSpellSemeClass.None)
            {
                return characterClass;
            }

            return DatabaseRepository
                .GetDatabase<CharacterClassDefinition>()
                .Where(candidate =>
                    candidate != null &&
                    candidate.VocalSpellSemeClass != VocalSpellSemeClass.None &&
                    duplicate.GetClassLevel(candidate) > 0)
                .OrderByDescending(candidate => duplicate.GetClassLevel(candidate))
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillVoiceLineInformation_Patch
    {
        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(NarrativeDefinitions))
                .Single(method =>
                    method.Name == nameof(NarrativeDefinitions.FillVoiceLineInformation) &&
                    method.GetParameters().Length == 9);
        }

        [UsedImplicitly]
        public static void Prefix(
            RulesetCharacter rulesetCharacter,
            ref bool isPlayer)
        {
            if (rulesetCharacter is RulesetCharacterSimulacrum)
            {
                // PlayVoiceSeme classifies every RulesetCharacterMonster as a non-player
                // speaker. That omits the voice suffix from spell seme event names, so a
                // fully controlled Simulacrum queues the coroutine but produces no audio.
                // It uses the ordinary player voice bank (the monster base fallback is MAL1).
                isPlayer = true;
            }
        }
    }

    // remove banter for forced off stealth from options on Mod UI
    [HarmonyPatch(typeof(GameLocationBanterManager), nameof(GameLocationBanterManager.StealthMayBeBrokenByAction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class HandleInput_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return CharacterActionPatcher.ApplyStealthBreakerBehavior_Patch.ShouldBanter;
        }
    }

    //PATCH: supports speech feature
    [HarmonyPatch(typeof(GameLocationBanterManager), nameof(GameLocationBanterManager.ForceBanterLine))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ForceBanterLine_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationBanterManager __instance, string line, GameLocationCharacter speaker)
        {
            if (!__instance.IsValidSpeaker(speaker))
            {
                return;
            }

            EndCharacterBanterSpeech(speaker);
            var lifecycle = BanterSpeech.GetValue(__instance, _ => new BanterSpeechLifecycle());
            if (lifecycle.Closed)
            {
                return;
            }

            var owner = new BanterSpeechOwner(lifecycle, speaker);
            if (SpeechContext.SpeakBanter(line, speaker, owner))
            {
                lifecycle.Owners.Add(owner);
                CharacterSpeech[speaker] = owner;
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationBanterManager), nameof(GameLocationBanterManager.PlayBanterConversation))]
    [UsedImplicitly]
    public static class PlayBanterConversation_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationBanterManager __instance, List<ComputedBanterLine> banterLines,
            ref IEnumerator __result)
        {
            if (__result != null && banterLines != null && Gui.Game?.CampaignDefinition?.IsUserCampaign == true)
            {
                __result = TrackBanterConversation(__instance, banterLines, __result);
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationBanterManager), nameof(GameLocationBanterManager.UnbindCharacter))]
    [UsedImplicitly]
    public static class UnbindCharacterSpeech_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter character)
        {
            EndCharacterBanterSpeech(character);
        }
    }

    [HarmonyPatch(typeof(GameLocationBanterManager), nameof(GameLocationBanterManager.UnbindServices))]
    [UsedImplicitly]
    public static class UnbindBanterSpeech_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationBanterManager __instance)
        {
            EndManagerBanterSpeech(__instance);
        }
    }

    [HarmonyPatch(typeof(GameLocationBanterManager), nameof(GameLocationBanterManager.BindServices))]
    [UsedImplicitly]
    public static class BindBanterSpeech_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationBanterManager __instance)
        {
            ResetManagerBanterSpeech(__instance);
        }
    }
}
