using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Behaviors;

internal sealed class SpellSelectionByLevel
{
    private readonly Dictionary<int, int> _spellCounts;
    private readonly Func<SpellDefinition, bool> _isSpellEligible;
    private readonly IReadOnlyCollection<SpellDefinition> _previousSpells;
    private readonly int _maxReplacements;

    internal SpellSelectionByLevel(params int[] spellLevels) : this(null, spellLevels)
    {
    }

    internal SpellSelectionByLevel(Func<SpellDefinition, bool> isSpellEligible, params int[] spellLevels)
    {
        _isSpellEligible = isSpellEligible;
        _spellCounts = spellLevels.GroupBy(level => level).ToDictionary(group => group.Key, group => group.Count());
        SpellCount = spellLevels.Length;
    }

    internal SpellSelectionByLevel(
        Func<SpellDefinition, bool> isSpellEligible,
        IReadOnlyCollection<SpellDefinition> previousSpells,
        int maxReplacements,
        params int[] spellLevels) : this(isSpellEligible, spellLevels)
    {
        _previousSpells = previousSpells;
        _maxReplacements = maxReplacements;
    }

    internal int SpellCount { get; }

    internal bool AllowsLevel(int spellLevel)
    {
        return _spellCounts.ContainsKey(spellLevel);
    }

    internal bool IsLevelFull(IReadOnlyCollection<SpellDefinition> selectedSpells, int spellLevel)
    {
        return !_spellCounts.TryGetValue(spellLevel, out var limit) ||
               selectedSpells.Count(spell => spell != null && spell.SpellLevel == spellLevel) >= limit;
    }

    internal bool CanSelectSpell(IReadOnlyCollection<SpellDefinition> selectedSpells, SpellDefinition spell)
    {
        // Previously saved choices must remain removable, including choices that no longer satisfy the limits.
        return spell != null &&
               (selectedSpells.Contains(spell) ||
                (IsSpellEligible(spell) && selectedSpells.Count < SpellCount &&
                 !IsLevelFull(selectedSpells, spell.SpellLevel) &&
                 IsWithinReplacementLimit(selectedSpells.Append(spell))));
    }

    internal bool IsSpellEligible(SpellDefinition spell)
    {
        return spell != null && (_isSpellEligible == null || _isSpellEligible(spell));
    }

    private bool IsWithinReplacementLimit(IEnumerable<SpellDefinition> selectedSpells)
    {
        return _previousSpells == null || selectedSpells.Except(_previousSpells).Count() <= _maxReplacements;
    }

    internal bool IsValidSelection(IReadOnlyCollection<SpellDefinition> selectedSpells)
    {
        return selectedSpells.Count == SpellCount &&
               selectedSpells.All(IsSpellEligible) &&
               IsWithinReplacementLimit(selectedSpells) &&
               selectedSpells.Distinct().Count() == SpellCount &&
               _spellCounts.All(limit =>
                   selectedSpells.Count(spell => spell.SpellLevel == limit.Key) == limit.Value);
    }
}
