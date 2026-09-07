using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Feats;
using static SolastaUnfinishedBusiness.Models.SpellCastingResourceContext;

namespace SolastaUnfinishedBusiness.Models;

// Display-only repertoires keep free Wizard casts in the same native columns as innate spells.
// They are never attached to a character or serialized; all casts resolve back to their real owner.
internal static class SpellSelectionContext
{
    private static readonly ConditionalWeakTable<RulesetSpellRepertoire, ResourceOption> Views = new();

    internal static bool TryGetOption(RulesetSpellRepertoire repertoire, out ResourceOption option)
    {
        option = null;
        return repertoire != null && Views.TryGetValue(repertoire, out option);
    }

    internal static RulesetSpellRepertoire Resolve(RulesetSpellRepertoire repertoire)
    {
        return TryGetOption(repertoire, out var option) ? option.Repertoire : repertoire;
    }

    internal static List<RulesetSpellRepertoire> GetRepertoires(RulesetCharacter caster)
    {
        var repertoires = caster.SpellRepertoires.Where(IsDisplayedRepertoire).ToList();
        var spells = repertoires.SelectMany(repertoire => repertoire.ExtraSpellsByTag
                .Where(entry => entry.Key is "SpellMastery" or "SignatureSpells")
                .SelectMany(entry => entry.Value)).Distinct().ToArray();

        foreach (var spell in spells)
        {
            foreach (var option in EnumerateResources(caster, spell).Where(option =>
                         option.Kind is ResourceKind.SpellMastery or ResourceKind.SignatureSpell))
            {
                repertoires.Add(CreateView(option));
            }
        }

        return repertoires;
    }

    internal static bool IsDisplayedRepertoire(RulesetSpellRepertoire repertoire)
    {
        var feature = repertoire?.SpellCastingFeature;
        if (feature?.SpellListDefinition == null || feature.SpellListDefinition == SpellsContext.EmptySpellList)
        {
            return false;
        }

        // Feat spellcasting features hide their redundant character-sheet description, while their
        // tagged repertoires still own selectable spells and uses. Invocation-only helpers stay hidden.
        return !feature.GuiPresentation.Hidden || feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null;
    }

    internal static RulesetSpellRepertoire CreateView(ResourceOption option)
    {
        var owner = option.Repertoire;
        var view = new RulesetSpellRepertoire
        {
            spellCastingFeature = owner.SpellCastingFeature,
            spellCastingClass = owner.SpellCastingClass,
            spellCastingSubclass = owner.SpellCastingSubclass,
            spellCastingRace = owner.SpellCastingRace,
            spellCastingLevel = owner.SpellCastingLevel,
            CharacterInventory = owner.CharacterInventory,
            CharacterName = owner.CharacterName,
            spellAttackBonus = owner.SpellAttackBonus,
            saveDC = owner.SaveDC
        };

        view.KnownSpells.Add(option.Spell);
        view.PreparedSpells.Add(option.Spell);
        Views.Add(view, option);
        return view;
    }

    internal static ResourceOption GetBaseSelection(
        RulesetCharacter caster, RulesetSpellRepertoire repertoire, SpellDefinition spell)
    {
        if (TryGetOption(repertoire, out var view))
        {
            return view;
        }

        var level = spell.SpellLevel;
        if (SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire) &&
            repertoire.SpellCastingFeature.CannotUpcast)
        {
            for (var candidate = level; candidate <= 9; candidate++)
            {
                repertoire.GetDisplaySlotNumbers(caster, candidate, out _, out var capacity);
                if (capacity <= 0)
                {
                    continue;
                }

                // Capacity, not remaining uses, identifies a fixed innate casting level.
                level = candidate;
                break;
            }
        }
        else if (repertoire.SpellCastingFeature.UniqueLevelSlots &&
                 (!repertoire.UsesSharedSpellSlots() || !SharedSpellsContext.IsMulticaster(caster)))
        {
            level = repertoire.GetLowestAvailableSlotLevel();
        }

        return GetSelection(repertoire, spell, level);
    }

    internal static ResourceOption GetSelection(
        RulesetSpellRepertoire repertoire, SpellDefinition spell, int level)
    {
        if (TryGetOption(repertoire, out var free))
        {
            return free;
        }

        var kind = SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire)
            ? ResourceKind.FreeRepertoire
            : ResourceKind.SpellSlot;
        return new ResourceOption(repertoire, spell, level, kind);
    }

    internal static void GetViewSlots(ResourceOption option, int level, out int remaining, out int maximum)
    {
        remaining = maximum = 0;
        if (option.SlotLevel != level)
        {
            return;
        }

        option.GetUses(option.Repertoire.GetCaster(), out remaining, out maximum);
        // Native counters need finite capacities. The native infinity indicator is bound separately.
        if (maximum < 0)
        {
            remaining = maximum = 1;
        }
    }
}
