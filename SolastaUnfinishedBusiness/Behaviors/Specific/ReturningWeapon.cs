using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Validators;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal class ReturningWeapon
{
    private const string ActivateReturningFormat = "Feedback/&ReturningWeaponActivates";
    private const string TagReturningWeapon = "ReturningWeapon";

    internal static readonly ReturningWeapon AlwaysValid = new(ValidatorsWeapon.AlwaysValid);
    private readonly IsWeaponValidHandler _isWeaponValidHandler;

    internal ReturningWeapon(IsWeaponValidHandler isWeaponValidHandler)
    {
        _isWeaponValidHandler = isWeaponValidHandler;
    }

    internal static RuleDefinitions.AttackProximity Process(
        RulesetCharacter character,
        RulesetAttackMode mode,
        RuleDefinitions.AttackProximity proximity)
    {
        if (character?.CharacterInventory == null ||
            mode == null ||
            proximity != RuleDefinitions.AttackProximity.Range ||
            !mode.Thrown)
        {
            return proximity;
        }

        var inventory = character.CharacterInventory;
        var num = inventory.CurrentConfiguration;
        var configurations = inventory.WieldedItemsConfigurations;

        if (configurations == null ||
            configurations.Count == 0 ||
            num < 0 ||
            num >= configurations.Count)
        {
            return proximity;
        }

        if (num == configurations.Count - 1)
        {
            num = configurations[num].MainHandSlot.ShadowedSlot != configurations[0].MainHandSlot
                ? 1
                : 0;

            if (num >= configurations.Count)
            {
                return proximity;
            }
        }

        var itemCfg = configurations[num];

        RulesetItem droppedItem = null;

        if (mode.SlotName == EquipmentDefinitions.SlotTypeMainHand &&
            itemCfg.MainHandSlot.EquipedItem != null &&
            itemCfg.MainHandSlot.EquipedItem.ItemDefinition == mode.SourceDefinition)
        {
            droppedItem = itemCfg.MainHandSlot.EquipedItem;
        }
        else if (mode.SlotName == EquipmentDefinitions.SlotTypeOffHand &&
                 itemCfg.OffHandSlot.EquipedItem != null &&
                 itemCfg.OffHandSlot.EquipedItem.ItemDefinition == mode.SourceDefinition)
        {
            droppedItem = itemCfg.OffHandSlot.EquipedItem;
        }

        if (droppedItem == null)
        {
            return proximity;
        }

        var isWeaponValid = droppedItem.GetSubFeaturesByType<ReturningWeapon>().Aggregate(
            false,
            (current, returningWeapon) =>
                current || returningWeapon._isWeaponValidHandler(mode, null, character));

        isWeaponValid = character.GetSubFeaturesByType<ReturningWeapon>().Aggregate(
            isWeaponValid,
            (current, returningWeapon) =>
                current || returningWeapon._isWeaponValidHandler(mode, null, character));

        if (!isWeaponValid)
        {
            return proximity;
        }

        proximity = RuleDefinitions.AttackProximity.Melee;

        character.LogCharacterActivatesAbility(droppedItem.ItemDefinition.GuiPresentation.Title,
            ActivateReturningFormat, tooltipClass: "ItemDefinition", tooltipContent: droppedItem.Name);

        return proximity;
    }

    internal static void AddReturningWeaponTag(RulesetItem item, Dictionary<string, TagsDefinitions.Criticity> tags)
    {
        if (item.HasSubFeatureOfType<ReturningWeapon>())
        {
            tags.TryAdd(TagReturningWeapon, TagsDefinitions.Criticity.Normal);
        }
    }
}
