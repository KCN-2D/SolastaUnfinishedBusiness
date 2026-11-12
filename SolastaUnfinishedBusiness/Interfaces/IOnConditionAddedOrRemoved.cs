namespace SolastaUnfinishedBusiness.Interfaces;

public interface IOnConditionAddedOrRemoved
{
    public void OnConditionAdded(RulesetCharacter target, RulesetCondition rulesetCondition);
    public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition);
}

// A repeated condition save can advance a sequence instead of ending on the first success.
internal interface IConditionSavingThrowResolved
{
    void OnConditionSavingThrowResolved(RulesetEffect effect, RulesetActor target,
        RulesetCondition condition, bool success);
}
