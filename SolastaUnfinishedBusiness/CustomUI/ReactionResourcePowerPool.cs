using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine.AddressableAssets;

namespace SolastaUnfinishedBusiness.CustomUI;

public class ReactionResourcePowerPool(FeatureDefinitionPower pool, AssetReferenceSprite icon = null) : ICustomReactionResource
{
    public AssetReferenceSprite Icon { get; } = icon ?? Sprites.PowerPoolResourceIcon;

    public string GetUses(RulesetCharacter character)
    {
        return character.GetRemainingPowerCharges(pool).ToString();
    }
}
