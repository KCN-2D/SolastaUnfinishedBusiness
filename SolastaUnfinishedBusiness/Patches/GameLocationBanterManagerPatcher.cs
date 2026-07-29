using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using TA;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationBanterManagerPatcher
{
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
        public static void Prefix(string line, GameLocationCharacter speaker)
        {
            SpeechContext.Speak(line, speaker);
        }
    }
}
