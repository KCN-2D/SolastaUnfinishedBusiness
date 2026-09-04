using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetSpellRepertoirePatcher
{
    private static readonly string[] SpellSourceTitleFormats =
    [
        "Screen/&{0}ExtraSpellTitle",
        "Screen/&{0}SpellTitle",
        "Tag/&{0}SpellSpecialTagTitle",
        "Tag/&{0}CantripSpecialTagTitle",
        "Tag/&{0}CantripOrSpellSpecialTagTitle",
        "Feat/&Feat{0}Title",
        "Feat/&{0}Title",
        "FightingStyle/&{0}Title",
        "Feature/&{0}Title"
    ];

    private static IEnumerable<RulesetSpellRepertoire> EnumerateSharedSlotRepertoires(RulesetCharacter character)
    {
        return character?.SpellRepertoires
            .Where(x => x.UsesSharedSpellSlots())
            ?? Enumerable.Empty<RulesetSpellRepertoire>();
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.CanCastSpell))]
    [HarmonyPatch([typeof(SpellDefinition), typeof(bool)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanCastSpell_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            RulesetSpellRepertoire __instance,
            SpellDefinition spellDefinition,
            ref bool __result)
        {
            var caster = __instance.GetCaster();

            if (caster == null ||
                !RulesetEffectSpellWithOrigin.IsPendingOrigin(caster, spellDefinition))
            {
                return true;
            }

            __result = true;
            return false;
        }

        [UsedImplicitly]
        public static void Postfix(
            RulesetSpellRepertoire __instance,
            SpellDefinition spellDefinition,
            ref bool __result)
        {
            var caster = __instance.GetCaster();

            if (!__result)
            {
                return;
            }

            __result = caster == null ||
                       SpellCastingValidation.IsValid(
                           caster,
                           __instance,
                           spellDefinition,
                           null,
                           out _);
        }
    }

    private static bool FormatHeaderTitle(RulesetSpellRepertoire __instance, ref string __result)
    {
        if (TryFormatSpellTagSourceTitle(__instance, false, out var title, out _))
        {
            __result = title;

            return false;
        }

        if (__instance.SpellCastingClass
            || __instance.SpellCastingSubclass
            || __instance.SpellCastingRace)
        {
            return true;
        }

        __result = __instance.SpellCastingFeature.FormatTitle();

        return false;
    }

    private static bool FormatShortTitle(RulesetSpellRepertoire __instance, ref string __result)
    {
        if (TryFormatSpellTagSourceTitle(__instance, true, out var title, out _))
        {
            __result = title;

            return false;
        }

        return FormatHeaderTitle(__instance, ref __result);
    }

    private static bool TryFormatSpellTagSourceTitle(
        RulesetSpellRepertoire repertoire,
        bool shortTitle,
        out string title,
        out string source)
    {
        title = null;
        source = null;

        var spellCastingFeature = repertoire?.SpellCastingFeature;
        var spellTag = spellCastingFeature?
            .GetFirstSubFeatureOfType<FeatHelpers.SpellTag>()?.Name;
        var selectionTag = string.IsNullOrEmpty(spellTag)
            ? null
            : Tabletop2024Context.GetTabletop2024FeatSpellSelectionTag(spellTag);
        var sourceTag = string.IsNullOrEmpty(spellTag)
            ? null
            : Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(spellTag);

        if (string.IsNullOrEmpty(spellTag))
        {
            return false;
        }

        if (shortTitle)
        {
            if (Tabletop2024Context.TryGetMagicInitiate2024SpellSourceShortTitle(spellTag, out title) ||
                TryFormatClassHolderTitle(spellCastingFeature, out title))
            {
                source = "shortClass";

                return true;
            }
        }

        foreach (var candidateTag in EnumerateSpellSourceTitleTags(spellTag, selectionTag, sourceTag))
        {
            if (TryLocalizeSpellSourceTitle(candidateTag, out title, out source))
            {
                return true;
            }
        }

        if (TryFormatClassHolderTitle(spellCastingFeature, out title))
        {
            source = "classHolder";

            return true;
        }

        if (TryFormatSpellCastingFeatureTitle(spellCastingFeature, out title))
        {
            source = "featureTitle";

            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSpellSourceTitleTags(
        string spellTag,
        string selectionTag,
        string sourceTag)
    {
        return new[] { spellTag, selectionTag, sourceTag }
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal);
    }

    private static bool TryLocalizeSpellSourceTitle(string spellTag, out string title, out string source)
    {
        title = null;
        source = null;

        foreach (var format in SpellSourceTitleFormats)
        {
            var titleTerm = string.Format(format, spellTag);

            if (TryLocalizeSpellSourceTitleTerm(titleTerm, out title))
            {
                source = titleTerm;

                return true;
            }
        }

        return false;
    }

    private static bool TryLocalizeSpellSourceTitleTerm(string titleTerm, out string title)
    {
        title = null;

        if (string.IsNullOrEmpty(titleTerm) ||
            !TranslatorContext.HasTranslation(titleTerm))
        {
            return false;
        }

        var localizedTitle = Gui.Localize(titleTerm);

        if (!IsUsableSpellSourceTitle(localizedTitle, titleTerm))
        {
            return false;
        }

        title = localizedTitle;

        return true;
    }

    private static bool TryFormatClassHolderTitle(FeatureDefinitionCastSpell spellCastingFeature, out string title)
    {
        title = spellCastingFeature?
            .GetFirstSubFeatureOfType<ClassHolder>()?.Class?
            .FormatTitle();

        return IsUsableSpellSourceTitle(title);
    }

    private static bool TryFormatSpellCastingFeatureTitle(
        FeatureDefinitionCastSpell spellCastingFeature,
        out string title)
    {
        title = null;

        var titleTerm = spellCastingFeature?.GuiPresentation?.Title;

        if (string.IsNullOrEmpty(titleTerm) ||
            titleTerm == Gui.NoLocalization ||
            titleTerm == Gui.EmptyContent)
        {
            return false;
        }

        title = spellCastingFeature.FormatTitle();

        return IsUsableSpellSourceTitle(title, titleTerm);
    }

    private static bool IsUsableSpellSourceTitle(string title, string titleTerm = null)
    {
        return !string.IsNullOrWhiteSpace(title) &&
               title != titleTerm &&
               !title.Contains("/&") &&
               !title.Contains("{0}");
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.SpellCastingAbility), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpellCastingAbility_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetSpellRepertoire __instance, ref string __result)
        {
            if (Tabletop2024Context.TryGetMagicInitiate2024SpellcastingAbility(__instance, out var ability))
            {
                __result = ability;
            }
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.SaveDC), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SaveDC_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetSpellRepertoire __instance, ref int __result)
        {
            if (Tabletop2024Context.TryGetMagicInitiate2024SaveDC(__instance, out var saveDC))
            {
                __result = saveDC;
            }
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.SpellAttackBonus), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpellAttackBonus_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetSpellRepertoire __instance, ref int __result)
        {
            if (Tabletop2024Context.TryGetMagicInitiate2024SpellAttackBonus(
                    __instance,
                    out var spellAttackBonus))
            {
                __result = spellAttackBonus;
            }
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.ComputeSpellSlots))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeSpellSlots_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            RulesetSpellRepertoire __instance,
            out DeferredRepertoireRefresh __state)
        {
            __state = DeferredRepertoireRefresh.TryCreate(__instance);

            var spellCastingLevel = __instance?.SpellCastingLevel ?? 0;
            var slotsPerLevels = __instance?.SpellCastingFeature?.SlotsPerLevels;

            if (__instance == null ||
                spellCastingLevel > 0 && slotsPerLevels != null && spellCastingLevel <= slotsPerLevels.Count)
            {
                return true;
            }

            __instance.spellsSlotCapacities?.Clear();

            return false;
        }

        [UsedImplicitly]
        private static void Postfix(DeferredRepertoireRefresh __state)
        {
            __state?.Complete(true);
        }

        [UsedImplicitly]
        private static Exception Finalizer(
            Exception __exception,
            DeferredRepertoireRefresh __state)
        {
            __state?.Complete(__exception == null);

            return __exception;
        }

        private sealed class DeferredRepertoireRefresh
        {
            private readonly RulesetCharacterSimulacrum _character;
            private readonly RulesetSpellRepertoire _repertoire;
            private readonly RulesetSpellRepertoire.RepertoireRefreshedHandler _callback;
            private bool _completed;

            private DeferredRepertoireRefresh(
                RulesetCharacterSimulacrum character,
                RulesetSpellRepertoire repertoire,
                RulesetSpellRepertoire.RepertoireRefreshedHandler callback)
            {
                _character = character;
                _repertoire = repertoire;
                _callback = callback;
            }

            internal static DeferredRepertoireRefresh TryCreate(
                RulesetSpellRepertoire repertoire)
            {
                if (repertoire?.RepertoireRefreshed == null ||
                    repertoire.GetCaster() is not RulesetCharacterSimulacrum
                    {
                        LifecycleState: SimulacrumLifecycleState.Ready,
                        RefreshAllDepth: > 0
                    } character)
                {
                    return null;
                }

                var callback = repertoire.RepertoireRefreshed;

                repertoire.RepertoireRefreshed = null;

                return new DeferredRepertoireRefresh(character, repertoire, callback);
            }

            internal void Complete(bool publishAfterRestore)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _repertoire.RepertoireRefreshed = _callback;

                if (publishAfterRestore)
                {
                    _character.DeferRepertoireRefresh(_repertoire);
                }
            }
        }
    }

    //PATCH: Supports Wizard Mastery and Signature spell features
    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.MaxPreparedSpell), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class MaxPreparedSpell_Getter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetSpellRepertoire __instance, ref int __result)
        {
            var character = __instance.GetCaster();

            if (character == null)
            {
                return true;
            }

            if (Level20Context.WizardSpellMastery.IsPreparation(character, __instance, out var maxSpellMastery))
            {
                __result = maxSpellMastery;

                return false;
            }

            // ReSharper disable once InvertIf
            if (Level20Context.WizardSignatureSpells.IsPreparation(character, __instance, out var maxSignatureSpells))
            {
                __result = maxSignatureSpells;

                return false;
            }

            return true;
        }
    }

    //PATCH: handles all different scenarios of spell slots consumption (casts, smites, point buys)
    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.SpendSpellSlot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpendSpellSlot_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetSpellRepertoire __instance, int slotLevel)
        {
            SpendSpellSlot(__instance, slotLevel);

            return false;
        }

        private static void ConsumeSlot(RulesetSpellRepertoire repertoire, int slotLevel)
        {
            var usedSpellsSlots = repertoire.usedSpellsSlots;

            usedSpellsSlots.TryAdd(slotLevel, 0);
            usedSpellsSlots[slotLevel]++;
            repertoire.RepertoireRefreshed?.Invoke(repertoire);
        }

        private static void SpendSpellSlot(RulesetSpellRepertoire __instance, int slotLevel)
        {
            // cantrips don't have usage
            if (slotLevel == 0)
            {
                return;
            }

            var character = __instance.GetCaster();

            // vanilla behavior if a race or monster origin
            if (__instance.SpellCastingFeature.SpellCastingOrigin
                is FeatureDefinitionCastSpell.CastingOrigin.Race
                or FeatureDefinitionCastSpell.CastingOrigin.Monster)
            {
                ConsumeSlot(__instance, slotLevel);

                return;
            }

            if (character?.HasSubFeatureOfType<IUseIndependentSpellSlots>() == true)
            {
                SpendIndependentSpellSlot(character, __instance, slotLevel);

                return;
            }

            var warlockSpellRepertoire =
                SharedSpellsContext.GetWarlockSpellRepertoire(character);

            // handle single caster scenarios both alternate system and vanilla
            if (!SharedSpellsContext.IsMulticaster(character))
            {
                if (Main.Settings.UseAlternateSpellPointsSystem &&
                    warlockSpellRepertoire == null)
                {
                    SpellPointsContext.ConsumeSlotsAtLevelsPointsCannotCastAnymore(
                        character,
                        __instance,
                        slotLevel);
                }
                else
                {
                    ConsumeSlot(__instance, slotLevel);
                }

                return;
            }

            // handles MC non-Warlock
            if (warlockSpellRepertoire == null)
            {
                var consume = true;

                foreach (var spellRepertoire in EnumerateSharedSlotRepertoires(character))
                {
                    if (Main.Settings.UseAlternateSpellPointsSystem)
                    {
                        SpellPointsContext.ConsumeSlotsAtLevelsPointsCannotCastAnymore(
                            character, spellRepertoire, slotLevel, consume, true);

                        consume = false;
                    }
                    else
                    {
                        ConsumeSlot(spellRepertoire, slotLevel);
                    }
                }
            }
            // handles MC Warlock
            else
            {
                SpendMulticasterWarlockSlots(__instance, character, slotLevel);
            }
        }

        private static void SpendIndependentSpellSlot(
            RulesetCharacter character,
            RulesetSpellRepertoire activeRepertoire,
            int slotLevel)
        {
            var sharedRepertoires = EnumerateSharedSlotRepertoires(character).ToArray();

            if (sharedRepertoires.Length == 0)
            {
                ConsumeSlot(activeRepertoire, slotLevel);

                return;
            }

            var warlockRepertoire = sharedRepertoires.FirstOrDefault(
                x => x.SpellCastingClass == DatabaseHelper.CharacterClassDefinitions.Warlock);
            var spellPointPool = Main.Settings.UseAlternateSpellPointsSystem
                ? character.UsablePowers.FirstOrDefault(
                    x => x.PowerDefinition == SpellPointsContext.PowerSpellPoints)
                : null;

            if (sharedRepertoires.Length == 1)
            {
                if (spellPointPool == null || warlockRepertoire != null)
                {
                    ConsumeSlot(activeRepertoire, slotLevel);
                }
                else
                {
                    SpendIndependentSpellPoints(
                        sharedRepertoires,
                        spellPointPool,
                        slotLevel,
                        0,
                        0);
                }

                return;
            }

            if (warlockRepertoire == null ||
                !TryGetPactSlotState(
                    character,
                    activeRepertoire,
                    warlockRepertoire,
                    slotLevel,
                    out var pactSpellLevel,
                    out var pactMax,
                    out var sharedRemaining,
                    out var pactRemaining))
            {
                SpendIndependentSharedSlots(
                    sharedRepertoires,
                    spellPointPool,
                    slotLevel,
                    0,
                    0);

                return;
            }

            var gameLocationCharacter = GameLocationCharacter.GetFromActor(character);
            var wasShiftPressed = gameLocationCharacter.GetAndClearShiftState();
            var consumePactSlot = false;

            if (slotLevel >= pactSpellLevel && pactRemaining > 0)
            {
                if (slotLevel > pactSpellLevel || sharedRemaining == 0)
                {
                    consumePactSlot = true;
                }
                else if (slotLevel == pactSpellLevel)
                {
                    consumePactSlot = Main.Settings.AlwaysSpendPactSlotsFirst ||
                                      (activeRepertoire.SpellCastingClass !=
                                          DatabaseHelper.CharacterClassDefinitions.Warlock && wasShiftPressed) ||
                                      (activeRepertoire.SpellCastingClass ==
                                          DatabaseHelper.CharacterClassDefinitions.Warlock && !wasShiftPressed);
                }
            }

            if (consumePactSlot)
            {
                SpendIndependentPactSlot(sharedRepertoires, pactSpellLevel);

                return;
            }

            SpendIndependentSharedSlots(
                sharedRepertoires,
                spellPointPool,
                slotLevel,
                pactSpellLevel,
                pactMax);
        }

        private static void SpendIndependentSharedSlots(
            IReadOnlyList<RulesetSpellRepertoire> sharedRepertoires,
            RulesetUsablePower spellPointPool,
            int slotLevel,
            int pactSpellLevel,
            int pactMax)
        {
            if (spellPointPool != null)
            {
                SpendIndependentSpellPoints(
                    sharedRepertoires,
                    spellPointPool,
                    slotLevel,
                    pactSpellLevel,
                    pactMax);

                return;
            }

            foreach (var repertoire in sharedRepertoires)
            {
                ConsumeSlot(repertoire, slotLevel);
            }
        }

        private static void SpendIndependentSpellPoints(
            IReadOnlyList<RulesetSpellRepertoire> sharedRepertoires,
            RulesetUsablePower spellPointPool,
            int slotLevel,
            int pactSpellLevel,
            int pactMax)
        {
            var spellCost = slotLevel < SpellPointsContext.SpellCostByLevel.Count
                ? SpellPointsContext.SpellCostByLevel[slotLevel]
                : 0;

            spellPointPool.remainingUses = Math.Max(0, spellPointPool.remainingUses - spellCost);

            var remainingPoints = spellPointPool.RemainingUses;
            var sharedMaxSpellLevel = sharedRepertoires
                .SelectMany(x => x.spellsSlotCapacities)
                .Where(x =>
                    x.Key > 0 &&
                    x.Value - (x.Key <= pactSpellLevel ? pactMax : 0) > 0)
                .Select(x => x.Key)
                .DefaultIfEmpty(0)
                .Max();

            foreach (var repertoire in sharedRepertoires)
            {
                repertoire.usedSpellsSlots.TryGetValue(
                    SharedSpellsContext.PactMagicSlotsTab,
                    out var pactUsed);

                for (var level = 1; level <= sharedMaxSpellLevel; level++)
                {
                    repertoire.usedSpellsSlots.TryGetValue(level, out var totalUsed);

                    var pactUsedAtLevel = level <= pactSpellLevel ? pactUsed : 0;
                    var sharedUsed = Math.Max(0, totalUsed - pactUsedAtLevel);
                    var uniqueLevelAlreadyUsed = level > 5 &&
                                                 (level == slotLevel || sharedUsed > 0);
                    var cannotAffordLevel =
                        level >= SpellPointsContext.SpellCostByLevel.Count ||
                        remainingPoints < SpellPointsContext.SpellCostByLevel[level];

                    repertoire.usedSpellsSlots[level] =
                        pactUsedAtLevel + (uniqueLevelAlreadyUsed || cannotAffordLevel ? 1 : 0);
                }

                repertoire.RepertoireRefreshed?.Invoke(repertoire);
            }
        }

        private static void SpendIndependentPactSlot(
            IEnumerable<RulesetSpellRepertoire> sharedRepertoires,
            int pactSpellLevel)
        {
            foreach (var repertoire in sharedRepertoires)
            {
                for (var level = SharedSpellsContext.PactMagicSlotsTab;
                     level <= pactSpellLevel;
                     level++)
                {
                    if (level == 0)
                    {
                        continue;
                    }

                    repertoire.usedSpellsSlots.TryAdd(level, 0);
                    repertoire.usedSpellsSlots[level]++;
                }

                repertoire.RepertoireRefreshed?.Invoke(repertoire);
            }
        }

        private static bool TryGetPactSlotState(
            RulesetCharacter character,
            RulesetSpellRepertoire activeRepertoire,
            RulesetSpellRepertoire warlockRepertoire,
            int slotLevel,
            out int pactSpellLevel,
            out int pactMax,
            out int sharedRemaining,
            out int pactRemaining)
        {
            pactSpellLevel = GetPactSpellLevel(warlockRepertoire, out pactMax);
            sharedRemaining = 0;
            pactRemaining = 0;

            if (pactSpellLevel <= 0 || pactMax <= 0)
            {
                return false;
            }

            pactMax += character
                .FeaturesByType<FeatureDefinitionMagicAffinity>()
                .Where(x => x == DatabaseHelper.FeatureDefinitionMagicAffinitys
                    .MagicAffinityChitinousBoonAdditionalSpellSlot)
                .SelectMany(x => x.AdditionalSlots)
                .Sum(x => x.SlotsNumber);

            activeRepertoire.spellsSlotCapacities.TryGetValue(slotLevel, out var totalMax);
            activeRepertoire.usedSpellsSlots.TryGetValue(slotLevel, out var totalUsed);
            warlockRepertoire.usedSpellsSlots.TryGetValue(
                SharedSpellsContext.PactMagicSlotsTab,
                out var pactUsed);

            if (slotLevel <= pactSpellLevel)
            {
                pactRemaining = Math.Max(0, pactMax - pactUsed);
            }

            var pactAppliesAtLevel = slotLevel <= pactSpellLevel;
            var sharedMax = Math.Max(0, totalMax - (pactAppliesAtLevel ? pactMax : 0));
            var sharedUsed = Math.Max(0, totalUsed - (pactAppliesAtLevel ? pactUsed : 0));

            sharedRemaining = Math.Max(0, sharedMax - sharedUsed);

            return true;
        }

        private static int GetPactSpellLevel(
            RulesetSpellRepertoire warlockRepertoire,
            out int pactMax)
        {
            pactMax = 0;

            var spellCastingLevel = warlockRepertoire.SpellCastingLevel;
            var slotsPerLevels = warlockRepertoire.SpellCastingFeature.SlotsPerLevels;

            if (spellCastingLevel <= 0 ||
                slotsPerLevels == null ||
                spellCastingLevel > slotsPerLevels.Count)
            {
                return 0;
            }

            var slots = slotsPerLevels[spellCastingLevel - 1]?.Slots;

            if (slots == null)
            {
                return 0;
            }

            var pactSpellLevel = 0;

            for (var index = 0; index < slots.Count; index++)
            {
                if (slots[index] <= 0)
                {
                    continue;
                }

                pactSpellLevel = index + 1;
                pactMax = Math.Max(pactMax, slots[index]);
            }

            return pactSpellLevel;
        }

        private static void SpendWarlockSlots(
            RulesetSpellRepertoire rulesetSpellRepertoire,
            RulesetCharacter character)
        {
            var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);
            var usedSpellsSlots = rulesetSpellRepertoire.usedSpellsSlots;

            for (var i = SharedSpellsContext.PactMagicSlotsTab; i <= warlockSpellLevel; i++)
            {
                // don't mess with cantrips
                if (i == 0)
                {
                    continue;
                }

                usedSpellsSlots.TryAdd(i, 0);
                usedSpellsSlots[i]++;
            }

            rulesetSpellRepertoire.RepertoireRefreshed?.Invoke(rulesetSpellRepertoire);
        }

        private static void SpendMulticasterWarlockSlots(
            RulesetSpellRepertoire __instance,
            RulesetCharacter character,
            int slotLevel)
        {
            var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);
            __instance.GetSharedAndPactSlotNumbers(
                character,
                slotLevel,
                out var sharedRemainingSlots,
                out _,
                out var pactRemainingSlots,
                out _);

            var glc = GameLocationCharacter.GetFromActor(character);
            var wasShiftPressed = glc.GetAndClearShiftState();
            var consumePactSlot = false;

            if (slotLevel >= warlockSpellLevel && pactRemainingSlots > 0)
            {
                if (slotLevel > warlockSpellLevel || sharedRemainingSlots == 0)
                {
                    consumePactSlot = true;
                }
                else if (slotLevel == warlockSpellLevel)
                {
                    consumePactSlot = Main.Settings.AlwaysSpendPactSlotsFirst ||
                                      (__instance.SpellCastingClass !=
                                          DatabaseHelper.CharacterClassDefinitions.Warlock && wasShiftPressed) ||
                                      (__instance.SpellCastingClass ==
                                          DatabaseHelper.CharacterClassDefinitions.Warlock && !wasShiftPressed);
                }
            }

            // uses short rest slots across all non race repertoires
            if (consumePactSlot)
            {
                foreach (var spellRepertoire in EnumerateSharedSlotRepertoires(character))
                {
                    SpendWarlockSlots(spellRepertoire, character);
                }
            }

            // otherwise uses long rest slots across all non-race repertoires
            else
            {
                var consume = true;

                foreach (var spellRepertoire in EnumerateSharedSlotRepertoires(character))
                {
                    if (Main.Settings.UseAlternateSpellPointsSystem)
                    {
                        SpellPointsContext.ConsumeSlotsAtLevelsPointsCannotCastAnymore(
                            character,
                            spellRepertoire,
                            slotLevel,
                            consume,
                            true);

                        consume = false;
                    }
                    else
                    {
                        ConsumeSlot(spellRepertoire, slotLevel);
                    }
                }
            }
        }
    }

    //PATCH: handles all different scenarios to determine max spell level
    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.MaxSpellLevelOfSpellCastingLevel),
        MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class MaxSpellLevelOfSpellCastingLevel_Getter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetSpellRepertoire __instance, ref int __result)
        {
            var spellCastingFeature = __instance?.SpellCastingFeature;

            if (!spellCastingFeature)
            {
                return true;
            }

            var spellCastingLevel = __instance.SpellCastingLevel;
            var slotsPerLevels = spellCastingFeature.SlotsPerLevels;

            if (spellCastingLevel <= 0 ||
                slotsPerLevels == null ||
                spellCastingLevel > slotsPerLevels.Count ||
                IsInvalidSlotEntry(slotsPerLevels[spellCastingLevel - 1]))
            {
                __result = 0;

                return false;
            }

            if (spellCastingFeature.SpellCastingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Race
                or FeatureDefinitionCastSpell.CastingOrigin.Monster)
            {
                return true;
            }

            if (__instance.GetCaster()?.HasSubFeatureOfType<IUseIndependentSpellSlots>() == true)
            {
                __result = __instance.spellsSlotCapacities
                    .Where(x => x.Key > 0 && x.Value > 0)
                    .Select(x => x.Key)
                    .DefaultIfEmpty(0)
                    .Max();

                return false;
            }

            if (SharedSpellsContext.UseMaxSpellLevelOfSpellCastingLevelDefaultBehavior)
            {
                return true;
            }

            var character = __instance.GetCaster();

            if (!SharedSpellsContext.IsMulticaster(character))
            {
                return true;
            }

            var sharedSpellLevel = SharedSpellsContext.GetSharedSpellLevel(character);
            var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);

            __result = Math.Max(sharedSpellLevel, warlockSpellLevel);

            return false;
        }

        private static bool IsInvalidSlotEntry(FeatureDefinitionCastSpell.SlotsByLevelDuplet slotEntry)
        {
            return slotEntry?.Slots == null || slotEntry.Slots.Count == 0;
        }
    }

    //PATCH: handles Arcane Recovery granted spells on short rests
    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.RecoverMissingSlots))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RecoverMissingSlots_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetSpellRepertoire __instance, Dictionary<int, int> recoveredSlots)
        {
            if (__instance.SpellCastingFeature.SpellCastingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Race
                or FeatureDefinitionCastSpell.CastingOrigin.Monster)
            {
                return true;
            }

            var character = __instance.GetCaster();

            if (character == null)
            {
                return true;
            }

            if (!SharedSpellsContext.IsMulticaster(character))
            {
                return true;
            }

            foreach (var spellRepertoire in character.SpellRepertoires)
            {
                var usedSpellsSlots = spellRepertoire.usedSpellsSlots;

                foreach (var recoveredSlot in recoveredSlots)
                {
                    var key = recoveredSlot.Key;

                    if (usedSpellsSlots.TryGetValue(key, out var used) && used > 0)
                    {
                        usedSpellsSlots[key] = Mathf.Max(0, used - recoveredSlot.Value);
                    }
                }

                spellRepertoire.RepertoireRefreshed?.Invoke(spellRepertoire);
            }

            return false;
        }
    }

    //PATCH: only offers upcast Warlock pact at their correct slot level
    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.CanUpcastSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanUpcastSpell_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetSpellRepertoire __instance,
            SpellDefinition spellDefinition,
            List<int> availableSlotLevels,
            ref bool __result)
        {
            var character = __instance.GetCaster();

            foreach (var slotLevel in availableSlotLevels.ToArray())
            {
                if (SpellSlotCastingLimit2024Context.CanUseSpellSlotLevel(
                        character,
                        __instance,
                        spellDefinition,
                        slotLevel))
                {
                    continue;
                }

                availableSlotLevels.Remove(slotLevel);
            }

            if (availableSlotLevels.Count == 0)
            {
                __result = false;
            }

            if (__instance.SpellCastingFeature.SpellCastingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Race
                or FeatureDefinitionCastSpell.CastingOrigin.Monster)
            {
                return;
            }

            if (!SharedSpellsContext.IsMulticaster(character) ||
                SharedSpellsContext.GetWarlockSpellLevel(character) == 0)
            {
                return;
            }

            foreach (var slotLevel in availableSlotLevels.ToArray())
            {
                if (__instance.TryGetAvailableSlotLevel(
                        character,
                        slotLevel,
                        null,
                        out var isAvailable) &&
                    isAvailable)
                {
                    continue;
                }

                availableSlotLevels.Remove(slotLevel);
            }
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.FormatHeader))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatHeader_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetSpellRepertoire __instance, ref string __result)
        {
            //PATCH: prevent null pointer crashes if all origin sources are null
            return FormatHeaderTitle(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.FormatShortHeader))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatShortHeader_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetSpellRepertoire __instance, ref string __result)
        {
            //PATCH: prevent null pointer crashes if all origin sources are null
            return FormatShortTitle(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.GetLowestAvailableSlotLevel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetLowestAvailableSlotLevel_Patch
    {
        //PATCH: ensures MC Warlock will cast spells using a correct slot level (MULTICLASS)
        [UsedImplicitly]
        public static bool Prefix(RulesetSpellRepertoire __instance, ref int __result)
        {
            var character = __instance.GetCaster();

            // get off here if not multicaster
            if (!SharedSpellsContext.IsMulticaster(character))
            {
                return true;
            }

            var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);

            // get off here if it doesn't have any Warlock level
            if (warlockSpellLevel == 0)
            {
                return true;
            }

            var availableSlotLevels = new List<int>();
            var maxSpellLevel = Math.Max(
                SharedSpellsContext.GetSharedSpellLevel(character),
                warlockSpellLevel);

            for (var slotLevel = 1; slotLevel <= maxSpellLevel; slotLevel++)
            {
                if (__instance.TryGetAvailableSlotLevel(
                        character,
                        slotLevel,
                        null,
                        out var isAvailable) &&
                    isAvailable)
                {
                    availableSlotLevels.Add(slotLevel);
                }
            }

            __result = __instance.GetPreferredSlotLevel(character, availableSlotLevels);

            return false;
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.EnumerateExtraSpellsOfLevel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumerateExtraSpellsOfLevel_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetSpellRepertoire __instance,
            int spellLevel,
            Dictionary<SpellDefinition, string> extraSpells)
        {
            LevelUpHelper.AddSlotCastableExtraSpellsToExtraSpellsMap(__instance, spellLevel, extraSpells);
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.HasKnowledgeOfSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class HasKnowledgeOfSpell_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetSpellRepertoire __instance, ref bool __result,
            SpellDefinition consideredSpellDefinition)
        {
            if (__result)
            {
                return;
            }

            //PATCH: allow slot-castable spells granted by feats / extra features on regular class repertoires
            __result = LevelUpHelper.IsPreparedOrSlotCastableExtraSpellForRepertoire(
                __instance.GetCaster(),
                __instance,
                consideredSpellDefinition);
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.IsSpellReady))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsSpellReady_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetSpellRepertoire __instance,
            ref bool __result,
            SpellDefinition consideredSpellDefinition)
        {
            if (__result)
            {
                return;
            }

            //PATCH: allow slot-castable spells granted by feats / extra features on regular class repertoires
            __result = LevelUpHelper.IsPreparedOrSlotCastableExtraSpellForRepertoire(
                __instance.GetCaster(),
                __instance,
                consideredSpellDefinition);
        }
    }

    [HarmonyPatch(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.HasMissingSpellSlots))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class HasMissingSpellSlots_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetSpellRepertoire __instance, ref bool __result)
        {
            //PATCH: consider having missing Spell Slots if some Spell Points are spent - needed for Arcane Recovery to show on Short Rest
            if (__result) { return; }

            var caster = __instance.GetCaster();
            if (caster == null) { return; }

            if (caster.IsSpellPointsEnabled() && caster.GetMaxSpellPoints() > caster.GetRemainingSpellPoints())
            {
                __result = true;
            }
        }
    }
}
