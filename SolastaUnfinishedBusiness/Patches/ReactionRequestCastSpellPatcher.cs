using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

//PATCH: removes low-level sub-option for spell reactions if caster is not-multiclass warlock (MULTICLASS)
[UsedImplicitly]
public static class ReactionRequestCastSpellPatcher
{
    [HarmonyPatch(typeof(ReactionRequestCastSpell), nameof(ReactionRequestCastSpell.BuildSlotSubOptions))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildSlotSubOptions_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ReactionRequestCastSpell __instance)
        {
            if (__instance.ReactionParams.RulesetEffect is not RulesetEffectSpell rulesetEffectSpell)
            {
                return;
            }

            var repertoire = ResolveReactionSpellRepertoire(__instance, rulesetEffectSpell);

            if (repertoire != null)
            {
                rulesetEffectSpell.spellRepertoire = repertoire;
                __instance.ReactionParams.SpellRepertoire = repertoire;
            }
        }

        [UsedImplicitly]
        public static void Postfix(ReactionRequestCastSpell __instance)
        {
            if (__instance.Character.RulesetCharacter is not RulesetCharacterHero hero
                || (SharedSpellsContext.GetWarlockSpellRepertoire(hero) != null
                    && !SharedSpellsContext.IsMulticaster(hero)))
            {
                return;
            }

            var optionsAvailability = __instance.SubOptionsAvailability;
            var reactionParams = __instance.ReactionParams;
            var repertoire = reactionParams.SpellRepertoire
                             ?? (reactionParams.RulesetEffect as RulesetEffectSpell)?.SpellRepertoire;

            if (repertoire == null)
            {
                return;
            }

            optionsAvailability.Clear();

            if (__instance.ReactionParams.RulesetEffect is not RulesetEffectSpell rulesetEffectSpell)
            {
                return;
            }

            var spellLevel = rulesetEffectSpell.SpellDefinition.SpellLevel;
            var selected = TryBuildFeatReactionSlotOptions(
                __instance,
                hero,
                rulesetEffectSpell,
                optionsAvailability,
                out var mergedSelected)
                ? mergedSelected
                : MulticlassGameUi.AddAvailableSubLevels(
                    optionsAvailability,
                    hero,
                    repertoire,
                    spellLevel,
                    0,
                    rulesetEffectSpell.SpellDefinition);

            if (selected >= 0)
            {
                __instance.SelectSubOption(selected);
            }
        }
    }

    [HarmonyPatch(typeof(ReactionRequestCastSpell), nameof(ReactionRequestCastSpell.SelectSubOption))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SelectSubOption_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ReactionRequestCastSpell __instance, int option)
        {
            //this should always be false
            if (__instance.ReactionParams.RulesetEffect is not RulesetEffectSpell spellEffect)
            {
                return true;
            }

            if (__instance.Character.RulesetCharacter is not RulesetCharacterHero hero
                || (SharedSpellsContext.GetWarlockSpellRepertoire(hero) != null
                    && !SharedSpellsContext.IsMulticaster(hero)))
            {
                return true;
            }

            spellEffect.SlotLevel = __instance.SubOptionsAvailability.Keys.ToArray()[option];

            if (TryApplyFeatReactionRepertoireForSlot(
                    __instance,
                    hero,
                    spellEffect,
                    spellEffect.SlotLevel))
            {
                return false;
            }

            var repertoire = __instance.ReactionParams.SpellRepertoire ?? spellEffect.SpellRepertoire;

            if (repertoire != null)
            {
                spellEffect.spellRepertoire = repertoire;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(ReactionRequestCastSpell), nameof(ReactionRequestCastSpell.SelectedSubOption),
        MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SelectedSubOption_Getter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ReactionRequestCastSpell __instance, ref int __result)
        {
            //this should always be false
            if (__instance.ReactionParams.RulesetEffect is not RulesetEffectSpell spellEffect)
            {
                return true;
            }

            if (__instance.Character.RulesetCharacter is not RulesetCharacterHero hero
                || (SharedSpellsContext.GetWarlockSpellRepertoire(hero) != null
                    && !SharedSpellsContext.IsMulticaster(hero)))
            {
                return true;
            }

            __result = Array.IndexOf([.. __instance.SubOptionsAvailability.Keys], spellEffect.SlotLevel);

            return false;
        }
    }

    private static RulesetSpellRepertoire ResolveReactionSpellRepertoire(
        ReactionRequestCastSpell request,
        RulesetEffectSpell effect)
    {
        if (request?.Character?.RulesetCharacter is not RulesetCharacterHero hero ||
            effect?.SpellDefinition == null)
        {
            return effect?.SpellRepertoire;
        }

        var spell = effect.SpellDefinition;
        var current = request.ReactionParams.SpellRepertoire ?? effect.SpellRepertoire;
        var knownRepertoires = hero.SpellRepertoires
            .Where(repertoire => repertoire.KnownSpells.Contains(spell))
            .ToArray();
        var featReactionRepertoire =
            IsFeatGrantedReactionRepertoire(current)
                ? current
                : knownRepertoires.FirstOrDefault(IsFeatGrantedReactionRepertoire);

        if (featReactionRepertoire == null)
        {
            // collateral case retained: race/feat repertoires can own the reaction spell
            return current ?? knownRepertoires.FirstOrDefault();
        }

        if (HasAvailableSlotLevel(hero, featReactionRepertoire, spell, spell.SpellLevel))
        {
            return featReactionRepertoire;
        }

        var classRepertoire = hero.SpellRepertoires
            .Where(repertoire => !IsRaceOrMonsterRepertoire(repertoire))
            .FirstOrDefault(repertoire =>
                LevelUpHelper.IsSlotCastableExtraSpellForRepertoire(hero, repertoire, spell) &&
                HasAvailableSlotLevel(hero, repertoire, spell, spell.SpellLevel));

        if (classRepertoire != null)
        {
            return classRepertoire;
        }

        return featReactionRepertoire;
    }

    private static bool TryBuildFeatReactionSlotOptions(
        ReactionRequestCastSpell request,
        RulesetCharacterHero hero,
        RulesetEffectSpell effect,
        Dictionary<int, bool> optionsAvailability,
        out int selected)
    {
        selected = -1;

        if (!TryGetFeatReactionRepertoire(request, effect, hero, out var featRepertoire))
        {
            return false;
        }

        var spell = effect.SpellDefinition;
        var minSpellLevel = spell.SpellLevel;
        var maxSpellLevel = Math.Max(
            minSpellLevel,
            Math.Max(
                SharedSpellsContext.GetSharedSpellLevel(hero),
                SharedSpellsContext.GetWarlockSpellLevel(hero)));
        var selectedLevel = 0;

        optionsAvailability.Clear();

        for (var level = minSpellLevel; level <= maxSpellLevel; level++)
        {
            var freeUseAvailable =
                level == minSpellLevel &&
                HasAvailableSlotLevel(hero, featRepertoire, spell, level);
            var hasClassOption = TryGetClassSlotRepertoireForLevel(
                hero,
                spell,
                level,
                requireAvailable: false,
                out _,
                out var classSlotAvailable);
            var isAvailable = freeUseAvailable || classSlotAvailable;

            if (!freeUseAvailable && !hasClassOption)
            {
                continue;
            }

            optionsAvailability[level] = isAvailable;

            if (selectedLevel == 0 && isAvailable)
            {
                selectedLevel = level;
            }
        }

        if (optionsAvailability.Count == 0)
        {
            return false;
        }

        if (selectedLevel > 0)
        {
            selected = 0;

            foreach (var slotLevel in optionsAvailability.Keys)
            {
                if (slotLevel == selectedLevel)
                {
                    break;
                }

                selected++;
            }
        }

        return true;
    }

    internal static bool TryGetFeatReactionDisplayRepertoire(
        ReactionRequestCastSpell request,
        int slotLevel,
        out RulesetSpellRepertoire repertoire)
    {
        repertoire = null;

        if (request?.Character?.RulesetCharacter is not RulesetCharacterHero hero ||
            request.ReactionParams.RulesetEffect is not RulesetEffectSpell effect ||
            !TryGetFeatReactionRepertoire(request, effect, hero, out var featRepertoire))
        {
            return false;
        }

        var spell = effect.SpellDefinition;

        if (slotLevel == spell.SpellLevel &&
            HasAvailableSlotLevel(hero, featRepertoire, spell, slotLevel))
        {
            repertoire = featRepertoire;
            return true;
        }

        if (TryGetClassSlotRepertoireForLevel(
                hero,
                spell,
                slotLevel,
                requireAvailable: false,
                out var classRepertoire,
                out _))
        {
            repertoire = classRepertoire;
            return true;
        }

        repertoire = featRepertoire;

        return true;
    }

    private static bool TryApplyFeatReactionRepertoireForSlot(
        ReactionRequestCastSpell request,
        RulesetCharacterHero hero,
        RulesetEffectSpell effect,
        int slotLevel)
    {
        if (!TryGetFeatReactionRepertoire(request, effect, hero, out var featRepertoire))
        {
            return false;
        }

        var spell = effect.SpellDefinition;
        var spellLevel = spell.SpellLevel;
        var freeUseAvailable =
            slotLevel == spellLevel &&
            HasAvailableSlotLevel(hero, featRepertoire, spell, slotLevel);

        if (freeUseAvailable)
        {
            SetReactionSpellRepertoire(request, effect, featRepertoire);
            return true;
        }

        if (TryGetClassSlotRepertoireForLevel(
                hero,
                spell,
                slotLevel,
                requireAvailable: true,
                out var classRepertoire,
                out _))
        {
            SetReactionSpellRepertoire(request, effect, classRepertoire);
            return true;
        }

        SetReactionSpellRepertoire(request, effect, featRepertoire);

        return true;
    }

    private static bool TryGetFeatReactionRepertoire(
        ReactionRequestCastSpell request,
        RulesetEffectSpell effect,
        RulesetCharacterHero hero,
        out RulesetSpellRepertoire featRepertoire)
    {
        featRepertoire = null;

        if (request == null ||
            effect?.SpellDefinition == null ||
            hero == null)
        {
            return false;
        }

        var spell = effect.SpellDefinition;
        var current = request.ReactionParams.SpellRepertoire ?? effect.SpellRepertoire;

        featRepertoire = IsFeatGrantedReactionRepertoire(current)
            ? current
            : hero.SpellRepertoires
                .Where(repertoire => repertoire.KnownSpells.Contains(spell))
                .FirstOrDefault(IsFeatGrantedReactionRepertoire);

        return featRepertoire != null;
    }

    private static bool TryGetClassSlotRepertoireForLevel(
        RulesetCharacterHero hero,
        SpellDefinition spell,
        int slotLevel,
        bool requireAvailable,
        out RulesetSpellRepertoire repertoire,
        out bool isAvailable)
    {
        repertoire = null;
        isAvailable = false;

        foreach (var candidate in hero.SpellRepertoires.Where(repertoire => !IsRaceOrMonsterRepertoire(repertoire)))
        {
            if (!LevelUpHelper.IsSlotCastableExtraSpellForRepertoire(hero, candidate, spell) ||
                !candidate.TryGetAvailableSlotLevel(hero, slotLevel, spell, out var available))
            {
                continue;
            }

            if (requireAvailable && !available)
            {
                continue;
            }

            repertoire = candidate;
            isAvailable = available;

            if (available)
            {
                return true;
            }
        }

        return repertoire != null;
    }

    private static void SetReactionSpellRepertoire(
        ReactionRequestCastSpell request,
        RulesetEffectSpell effect,
        RulesetSpellRepertoire repertoire)
    {
        effect.spellRepertoire = repertoire;
        request.ReactionParams.SpellRepertoire = repertoire;
    }

    private static bool HasAvailableSlotLevel(
        RulesetCharacterHero hero,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        int slotLevel)
    {
        return repertoire != null &&
               repertoire.TryGetAvailableSlotLevel(hero, slotLevel, spell, out var isAvailable) &&
               isAvailable;
    }

    private static bool IsFeatGrantedReactionRepertoire(RulesetSpellRepertoire repertoire)
    {
        var castSpell = repertoire?.SpellCastingFeature;

        return castSpell != null &&
               castSpell.SpellCastingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Race
                   or FeatureDefinitionCastSpell.CastingOrigin.Monster &&
               castSpell.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null;
    }

    private static bool IsRaceOrMonsterRepertoire(RulesetSpellRepertoire repertoire)
    {
        return repertoire?.SpellCastingFeature?.SpellCastingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Race
            or FeatureDefinitionCastSpell.CastingOrigin.Monster;
    }
}
