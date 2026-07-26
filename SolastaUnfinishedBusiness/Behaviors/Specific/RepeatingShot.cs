using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.GameExtensions;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

public class RepeatingShot
{
    private RepeatingShot()
    {
    }

    public static RepeatingShot Instance { get; } = new();

    internal static void ModifyTags(RulesetItem item, Dictionary<string, TagsDefinitions.Criticity> tags)
    {
        if (!HasRepeatingShot(item))
        {
            return;
        }

        tags.Remove(TagsDefinitions.WeaponTagLoading);
        tags.Remove(TagsDefinitions.WeaponTagAmmunition);
    }

    internal static bool HasRepeatingShot(RulesetItem item)
    {
        return item != null && item.HasSubFeatureOfType<RepeatingShot>();
    }

    internal static void IgnoreStandardAmmunition(
        RulesetCharacter character,
        RulesetAttackMode mode,
        ref string ammunitionType)
    {
        var currentAmmunitionSlot =
            character?.CharacterInventory?.GetCurrentAmmunitionSlot(ammunitionType);
        var ammunitionDefinition = currentAmmunitionSlot?.EquipedItem?.ItemDefinition;

        // Keep special ammunition that carries its own effect. Repeating Shot only
        // replaces ordinary ammunition supplied by the weapon.
        if (ammunitionDefinition?.AmmunitionDescription?.EffectDescription == null ||
            ammunitionDefinition.AmmunitionDescription.EffectDescription.FindFirstDamageForm() != null ||
            !HasRepeatingShot(mode?.sourceObject as RulesetItem))
        {
            return;
        }

        ammunitionType = string.Empty;
    }
}
