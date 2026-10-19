using System.Collections.Generic;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Interfaces;

// This should add to features
public interface ICustomLevelUpLogic
{
    // Use this to add the feature to the character
    [UsedImplicitly]
    public void ApplyFeature(RulesetCharacterHero hero, [UsedImplicitly] string tag);

    // Use this to remove the feature from the character. In particular this is used to allow level down functionality
    [UsedImplicitly]
    public void RemoveFeature(RulesetCharacterHero hero, [UsedImplicitly] string tag);
}

// Owned construction features expose the state rebuilt when a hero is recreated.
// External changes, such as a consumed tome, do not participate in this contract.
public interface ICustomLevelUpState
{
    IEnumerable<RulesetCondition> GetStateConditions(RulesetCharacterHero hero);

    (int BaseValue, int MaxValue, int MaxEditableValue) GetAttributeIncreases(
        RulesetCharacterHero hero, string attribute);
}
