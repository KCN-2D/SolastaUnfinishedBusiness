namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IOnEffectConditionTracked
{
    void OnConditionTracked(
        RulesetEffect effect, RulesetCharacter source, RulesetActor target, RulesetCondition condition);
}
