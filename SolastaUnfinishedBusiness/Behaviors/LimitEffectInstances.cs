using SolastaUnfinishedBusiness.Interfaces;

using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;

namespace SolastaUnfinishedBusiness.Behaviors;

internal class LimitEffectInstances : ILimitEffectInstances, IUniqueEffectTerminationFilter
{
    private readonly GetEffectLimit _handler;
    private readonly bool _ownsUniqueness;

    internal LimitEffectInstances(string name, GetEffectLimit handler, bool ownsUniqueness = false)
    {
        _handler = handler;
        _ownsUniqueness = ownsUniqueness;
        Name = name;
    }

    public string Name { get; }

    public int GetLimit(RulesetCharacter character)
    {
        return _handler(character);
    }

    internal delegate int GetEffectLimit(RulesetCharacter character);

    public bool ShouldTerminateExistingEffect(
        RulesetCharacter character, RulesetEffect incoming, RulesetEffect existing)
    {
        // A source can let the group limit own uniqueness. Other sources retain their
        // normal same-definition and bundle replacement rules in addition to that limit.
        return !_ownsUniqueness ||
               existing.GetSourceDefinitionSafe()?.GetFirstSubFeatureOfType<ILimitEffectInstances>()?.Name != Name;
    }
}
