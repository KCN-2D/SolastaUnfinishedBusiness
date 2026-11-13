namespace SolastaUnfinishedBusiness.Behaviors;

public delegate int ModifyPowerRechargeHandler(RulesetCharacter character, RulesetUsablePower usablePower, int maxUses);

// The supplied remaining uses already include the power's ordinary rest recovery.
public delegate int ModifyPowerRestRecoveryHandler(
    RulesetCharacter character, RulesetUsablePower usablePower, RuleDefinitions.RestType restType,
    int maxUses, int remainingUses);
