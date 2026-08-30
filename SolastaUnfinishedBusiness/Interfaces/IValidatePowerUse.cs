namespace SolastaUnfinishedBusiness.Interfaces;

public interface IValidatePowerUse
{
    public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power);
}

public interface IValidatePowerUseWithFailure : IValidatePowerUse
{
    public bool CanUsePower(
        RulesetCharacter character,
        FeatureDefinitionPower power,
        out string failure);
}
