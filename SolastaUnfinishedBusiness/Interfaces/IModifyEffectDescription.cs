namespace SolastaUnfinishedBusiness.Interfaces;

public interface IModifyEffectDescription
{
    public bool IsValid(
        BaseDefinition definition,
        RulesetCharacter character,
        EffectDescription effectDescription);

    public EffectDescription GetEffectDescription(
        BaseDefinition definition,
        EffectDescription effectDescription,
        RulesetCharacter character,
        RulesetEffect rulesetEffect);
}

// Only modifiers with this contract follow a spell into a power granted by its conditions.
// Targeting and range modifiers must remain confined to the original spell.
public interface IModifySpellDerivedEffectDescription : IModifyEffectDescription
{
}
