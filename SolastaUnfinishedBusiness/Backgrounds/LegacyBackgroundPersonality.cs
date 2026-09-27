using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Backgrounds;

// Definition-owned history for choices removed from character creation but still present in saves.
internal sealed class LegacyBackgroundPersonality(IEnumerable<PersonalityFlagOccurence> choices)
{
    private readonly Dictionary<string, int> _weights =
        choices.ToDictionary(choice => choice.PersonalityFlag, choice => choice.Weight);

    internal bool TryGetWeight(string flagName, out int weight)
    {
        return _weights.TryGetValue(flagName, out weight);
    }
}
