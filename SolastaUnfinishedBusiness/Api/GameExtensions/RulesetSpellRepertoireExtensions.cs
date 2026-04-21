using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;

using System;
using System.Collections.Generic;
using SolastaUnfinishedBusiness.Models;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

public static class RulesetSpellRepertoireExtensions
{
    private static bool TryGetMulticasterWarlockSpellLevel(
        [CanBeNull] RulesetCharacterHero hero,
        out int warlockSpellLevel)
    {
        warlockSpellLevel = 0;

        if (hero == null ||
            !SharedSpellsContext.IsMulticaster(hero) ||
            SharedSpellsContext.GetWarlockSpellRepertoire(hero) == null)
        {
            return false;
        }

        warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(hero);

        return true;
    }

    public static RulesetCharacter GetCaster(this RulesetSpellRepertoire repertoire)
    {
        return EffectHelpers.GetCharacterByGuid(repertoire?.CharacterInventory?.BearerGuid ?? 0)
               ?? Global.InspectedHero;
    }

    [CanBeNull]
    public static CharacterClassDefinition GetCastingClass(this RulesetSpellRepertoire repertoire)
    {
        return repertoire.SpellCastingFeature.GetFirstSubFeatureOfType<ClassHolder>()?.Class
               ?? repertoire.SpellCastingClass;
    }

    public static bool AtLeastOneSpellSlotAvailable(this RulesetSpellRepertoire repertoire)
    {
        for (var spellLevel = 1;
             spellLevel <= repertoire.MaxSpellLevelOfSpellCastingLevel;
             spellLevel++)
        {
            repertoire.GetSlotsNumber(spellLevel, out var remaining, out _);

            if (remaining <= 0)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal static void GetSharedAndPactSlotNumbers(
        this RulesetSpellRepertoire repertoire,
        RulesetCharacterHero hero,
        int slotLevel,
        out int sharedRemaining,
        out int sharedMax,
        out int pactRemaining,
        out int pactMax)
    {
        repertoire.GetSlotsNumber(slotLevel, out var totalRemaining, out var totalMax);

        sharedRemaining = totalRemaining;
        sharedMax = totalMax;
        pactRemaining = 0;
        pactMax = 0;

        if (!TryGetMulticasterWarlockSpellLevel(hero, out var warlockSpellLevel))
        {
            return;
        }

        if (slotLevel > warlockSpellLevel)
        {
            return;
        }

        pactMax = SharedSpellsContext.GetWarlockMaxSlots(hero);

        var pactUsed = SharedSpellsContext.GetWarlockUsedSlots(hero);
        var totalUsed = totalMax - totalRemaining;
        var sharedUsed = Math.Max(0, totalUsed - pactUsed);

        pactRemaining = Math.Max(0, pactMax - pactUsed);
        sharedMax = Math.Max(0, totalMax - pactMax);
        sharedRemaining = Math.Max(0, sharedMax - sharedUsed);
    }

    internal static void GetDisplaySlotNumbers(
        this RulesetSpellRepertoire repertoire,
        RulesetCharacterHero hero,
        int slotLevel,
        out int remaining,
        out int max)
    {
        repertoire.GetSharedAndPactSlotNumbers(
            hero,
            slotLevel,
            out var sharedRemaining,
            out var sharedMax,
            out var pactRemaining,
            out var pactMax);

        remaining = sharedRemaining;
        max = sharedMax;

        if (!TryGetMulticasterWarlockSpellLevel(hero, out var warlockSpellLevel))
        {
            return;
        }

        if (slotLevel < warlockSpellLevel)
        {
            return;
        }

        remaining += pactRemaining;
        max += pactMax;
    }

    internal static bool TryGetAvailableSlotLevel(
        this RulesetSpellRepertoire repertoire,
        RulesetCharacterHero hero,
        int slotLevel,
        SpellDefinition spellDefinition,
        out bool isAvailable)
    {
        var warlockSpellLevel = hero == null ? 0 : SharedSpellsContext.GetWarlockSpellLevel(hero);
        var isSingleClassWarlock = hero != null &&
                                   !SharedSpellsContext.IsMulticaster(hero) &&
                                   warlockSpellLevel > 0;

        if (isSingleClassWarlock && slotLevel != warlockSpellLevel)
        {
            isAvailable = false;

            return false;
        }

        if (hero != null)
        {
            repertoire.GetDisplaySlotNumbers(hero, slotLevel, out var remaining, out var max);

            isAvailable = remaining > 0;

            if (spellDefinition != null)
            {
                if (hero.IsSpellPointsEnabled())
                {
                    isAvailable = SpellPointsContext.CanCastSpellOfLevel(hero, repertoire, slotLevel);
                }

                if (!isAvailable)
                {
                    isAvailable = Level20Context.HasFreeWizardCast(hero, repertoire, spellDefinition, slotLevel);
                }
            }

            return max > 0 || isAvailable;
        }

        repertoire.GetSlotsNumber(slotLevel, out var baseRemaining, out var baseMax);
        isAvailable = baseRemaining > 0;

        return baseMax > 0 || isAvailable;
    }

    internal static int GetPreferredSlotLevel(
        this RulesetSpellRepertoire repertoire,
        RulesetCharacterHero hero,
        IReadOnlyList<int> availableSlotLevels)
    {
        if (availableSlotLevels.Count == 0)
        {
            return 0;
        }

        if (hero == null || !SharedSpellsContext.IsMulticaster(hero))
        {
            return availableSlotLevels[0];
        }

        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(hero);

        if (warlockSpellLevel == 0)
        {
            return availableSlotLevels[0];
        }

        var shiftPressed = GameLocationCharacter.GetFromActor(hero)?.GetShiftState() == true;
        var preferPact = Main.Settings.AlwaysSpendPactSlotsFirst ||
                         (repertoire.SpellCastingClass != Warlock && shiftPressed) ||
                         (repertoire.SpellCastingClass == Warlock && !shiftPressed);

        if (preferPact)
        {
            for (var i = 0; i < availableSlotLevels.Count; i++)
            {
                if (availableSlotLevels[i] == warlockSpellLevel)
                {
                    return warlockSpellLevel;
                }
            }
        }

        return availableSlotLevels[0];
    }
}
