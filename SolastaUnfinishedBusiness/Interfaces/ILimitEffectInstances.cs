namespace SolastaUnfinishedBusiness.Interfaces;

public interface ILimitEffectInstances
{
    public string Name { get; }
    public int GetLimit(RulesetCharacter character);
}

internal interface IUniqueEffectTerminationFilter
{
    bool ShouldTerminateExistingEffect(RulesetCharacter character, RulesetEffect incoming, RulesetEffect existing);
}