namespace SolastaUnfinishedBusiness.CustomUI;

public class ReactionRequestUsePowerCustom : ReactionRequestUsePower
{
    public override bool IsStillValid => base.IsStillValid && ReactionRequestCallback.IsValid(ReactionParams);

    public ReactionRequestUsePowerCustom(
        CharacterActionParams reactionParams,
        string name = "UsePower")
        : base(reactionParams, name)
    {
        guiCharacter = new GuiCharacter(Character);
    }

    public ReactionRequestUsePowerCustom(
        CharacterActionParams reactionParams,
        GameLocationCharacter attacker = null)
        : base(reactionParams, "UsePower")
    {
        guiCharacter = new GuiCharacter(Character);
        guiAttacker = new GuiCharacter(attacker);
    }

    public ReactionRequestUsePowerCustom(
        CharacterActionParams reactionParams,
        string name = "UsePower",
        GameLocationCharacter attacker = null)
        : base(reactionParams, name)
    {
        guiCharacter = new GuiCharacter(Character);
        guiAttacker = new GuiCharacter(attacker);
    }

    public override string FormatDescription()
    {
        return string.IsNullOrEmpty(reactionParams.StringParameter2)
            ? base.FormatDescription()
            : reactionParams.StringParameter2;
    }
}
