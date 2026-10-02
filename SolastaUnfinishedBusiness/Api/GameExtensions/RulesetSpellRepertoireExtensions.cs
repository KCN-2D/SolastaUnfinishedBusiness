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
        RulesetSpellRepertoire repertoire,
        [CanBeNull] RulesetCharacter character,
        out int warlockSpellLevel)
    {
        warlockSpellLevel = 0;

        if (!repertoire.UsesSharedSpellSlots() ||
            character == null ||
            !SharedSpellsContext.IsMulticaster(character) ||
            SharedSpellsContext.GetWarlockSpellRepertoire(character) == null)
        {
            return false;
        }

        warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);

        return true;
    }

    public static RulesetCharacter GetCaster(this RulesetSpellRepertoire repertoire)
    {
        var caster = EffectHelpers.GetCharacterByGuid(
            repertoire?.CharacterInventory?.BearerGuid ?? 0);

        if (caster != null)
        {
            return caster;
        }

        return Global.InspectedHero?.SpellRepertoires.Contains(repertoire) == true
            ? Global.InspectedHero
            : null;
    }

    [CanBeNull]
    public static CharacterClassDefinition GetCastingClass(this RulesetSpellRepertoire repertoire)
    {
        return repertoire.SpellCastingFeature.GetFirstSubFeatureOfType<ClassHolder>()?.Class
               ?? repertoire.SpellCastingClass;
    }

    internal static bool UsesSharedSpellSlots(this FeatureDefinitionCastSpell spellCastingFeature)
    {
        return spellCastingFeature?.SpellCastingOrigin is
            FeatureDefinitionCastSpell.CastingOrigin.Class or
            FeatureDefinitionCastSpell.CastingOrigin.Subclass;
    }

    internal static bool UsesSharedSpellSlots(this RulesetSpellRepertoire repertoire)
    {
        return repertoire?.SpellCastingFeature.UsesSharedSpellSlots() == true;
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
        RulesetCharacter character,
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

        if (!TryGetMulticasterWarlockSpellLevel(repertoire, character, out var warlockSpellLevel))
        {
            return;
        }

        if (slotLevel > warlockSpellLevel)
        {
            return;
        }

        pactMax = SharedSpellsContext.GetWarlockMaxSlots(character);

        var pactUsed = SharedSpellsContext.GetWarlockUsedSlots(character);
        var totalUsed = totalMax - totalRemaining;
        var sharedUsed = Math.Max(0, totalUsed - pactUsed);

        pactRemaining = Math.Max(0, pactMax - pactUsed);
        sharedMax = Math.Max(0, totalMax - pactMax);
        sharedRemaining = Math.Max(0, sharedMax - sharedUsed);
    }

    internal static void GetDisplaySlotNumbers(
        this RulesetSpellRepertoire repertoire,
        RulesetCharacter character,
        int slotLevel,
        out int remaining,
        out int max)
    {
        if (SpellSelectionContext.TryGetOption(repertoire, out var option))
        {
            SpellSelectionContext.GetViewSlots(option, slotLevel, out remaining, out max);
            return;
        }

        repertoire.GetSharedAndPactSlotNumbers(
            character,
            slotLevel,
            out var sharedRemaining,
            out var sharedMax,
            out var pactRemaining,
            out var pactMax);

        remaining = sharedRemaining;
        max = sharedMax;

        if (!TryGetMulticasterWarlockSpellLevel(repertoire, character, out var warlockSpellLevel))
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
        RulesetCharacter character,
        int slotLevel,
        SpellDefinition spellDefinition,
        out bool isAvailable)
    {
        var usesSharedSpellSlots = repertoire.UsesSharedSpellSlots();
        var warlockSpellLevel =
            !usesSharedSpellSlots || character == null
                ? 0
                : SharedSpellsContext.GetWarlockSpellLevel(character);
        var isSingleClassWarlock = usesSharedSpellSlots &&
                                   character != null &&
                                   !SharedSpellsContext.IsMulticaster(character) &&
                                   warlockSpellLevel > 0;

        if (isSingleClassWarlock && slotLevel != warlockSpellLevel)
        {
            isAvailable = false;

            return false;
        }

        if (character != null)
        {
            repertoire.GetDisplaySlotNumbers(character, slotLevel, out var remaining, out var max);

            isAvailable = remaining > 0;

            if (spellDefinition != null)
            {
                if (usesSharedSpellSlots && character.IsSpellPointsEnabled())
                {
                    isAvailable = SpellPointsContext.CanCastSpellOfLevel(
                        character,
                        repertoire,
                        slotLevel);
                }

                if (!isAvailable)
                {
                    isAvailable = Level20Context.HasFreeWizardCast(
                        character,
                        repertoire,
                        spellDefinition,
                        slotLevel);
                }
            }

            return max > 0 || isAvailable;
        }

        repertoire.GetSlotsNumber(slotLevel, out var baseRemaining, out var baseMax);
        isAvailable = baseRemaining > 0;

        return baseMax > 0 || isAvailable;
    }

    internal static bool ShouldSpendPactSlot(
        this RulesetSpellRepertoire repertoire,
        int slotLevel,
        int pactLevel,
        int sharedRemaining,
        int pactRemaining,
        bool shiftPressed)
    {
        // Preserve native priority: usable pact level, then depleted shared slots or the same
        // class/Shift preference. Display queries observe Shift; spending consumes it once.
        return slotLevel >= pactLevel && pactRemaining > 0 &&
               (slotLevel > pactLevel || sharedRemaining == 0 || repertoire.PrefersPactSlots(shiftPressed));
    }

    internal static bool WouldSpendPactSlot(
        this RulesetSpellRepertoire repertoire, RulesetCharacter character, int slotLevel)
    {
        if (!repertoire.UsesSharedSpellSlots() || SharedSpellsContext.GetWarlockSpellRepertoire(character) == null)
        {
            return false;
        }

        // A single pact caster never spends points, including while its slots are depleted.
        if (!SharedSpellsContext.IsMulticaster(character))
        {
            return true;
        }

        repertoire.GetSharedAndPactSlotNumbers(character, slotLevel, out var sharedRemaining, out _,
            out var pactRemaining, out _);
        return repertoire.ShouldSpendPactSlot(slotLevel, SharedSpellsContext.GetWarlockSpellLevel(character),
            sharedRemaining, pactRemaining, GameLocationCharacter.GetFromActor(character)?.GetShiftState() == true);
    }

    private static bool PrefersPactSlots(this RulesetSpellRepertoire repertoire, bool shiftPressed)
    {
        return Main.Settings.AlwaysSpendPactSlotsFirst ||
               (repertoire.SpellCastingClass != Warlock && shiftPressed) ||
               (repertoire.SpellCastingClass == Warlock && !shiftPressed);
    }

    internal static int GetPreferredSlotLevel(
        this RulesetSpellRepertoire repertoire,
        RulesetCharacter character,
        IReadOnlyList<int> availableSlotLevels)
    {
        if (availableSlotLevels.Count == 0)
        {
            return 0;
        }

        if (!repertoire.UsesSharedSpellSlots() ||
            character == null ||
            !SharedSpellsContext.IsMulticaster(character))
        {
            return availableSlotLevels[0];
        }

        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);

        if (warlockSpellLevel == 0)
        {
            return availableSlotLevels[0];
        }

        var shiftPressed = GameLocationCharacter.GetFromActor(character)?.GetShiftState() == true;
        if (repertoire.PrefersPactSlots(shiftPressed))
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
