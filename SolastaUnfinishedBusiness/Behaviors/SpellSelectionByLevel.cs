using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Behaviors;

internal sealed class SpellSelectionByLevel
{
    private readonly Dictionary<int, int> _spellCounts;

    internal SpellSelectionByLevel(params int[] spellLevels)
    {
        _spellCounts = spellLevels.GroupBy(level => level).ToDictionary(group => group.Key, group => group.Count());
        SpellCount = spellLevels.Length;
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
                (selectedSpells.Count < SpellCount && !IsLevelFull(selectedSpells, spell.SpellLevel)));
    }

    internal bool IsValidSelection(IReadOnlyCollection<SpellDefinition> selectedSpells)
    {
        return selectedSpells.Count == SpellCount &&
               selectedSpells.All(spell => spell != null) &&
               selectedSpells.Distinct().Count() == SpellCount &&
               _spellCounts.All(limit =>
                   selectedSpells.Count(spell => spell.SpellLevel == limit.Key) == limit.Value);
    }
}
