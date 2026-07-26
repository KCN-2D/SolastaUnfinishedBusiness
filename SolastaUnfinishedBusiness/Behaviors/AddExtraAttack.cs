using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors;

internal enum AttackModeOrder
{
    Start,
    End
}

internal interface IAddExtraAttack
{
    // sort sub features [used on race claw attacks]
    public int Priority();
    public void TryAddExtraAttack(RulesetCharacter character);
}

internal abstract class AddExtraAttackBase(
    ActionDefinitions.ActionType actionType,
    params IsCharacterValidHandler[] validators) : IAddExtraAttack
{
    private const string ConditionFeatCleavingAttackFinish = "ConditionFeatCleavingAttackFinish";
    private const string ConditionFeatGreatWeaponMaster2024Finish = "ConditionFeatGreatWeaponMaster2024Finish";

    // private readonly List<string> additionalTags = new();
    protected readonly ActionDefinitions.ActionType ActionType = actionType;

    public void TryAddExtraAttack(RulesetCharacter character)
    {
        if (!character.IsValid(validators))
        {
            return;
        }

        var attackModes = character.AttackModes;

        var newAttacks = GetAttackModes(character);

        if (newAttacks == null || newAttacks.Count == 0)
        {
            return;
        }

        foreach (var attackMode in newAttacks)
        {
            var same = attackModes.FirstOrDefault(m => ModesEqual(attackMode, m));

            if (same != null)
            {
                //If same attack mode exists, ensure it has max amount of attacks
                same.attacksNumber = Math.Max(attackMode.attacksNumber, same.attacksNumber);
                //and dispose of newly created one
                RulesetAttackMode.AttackModesPool.Return(attackMode);
            }
            else
            {
                var order = GetOrder(character);

                switch (order)
                {
                    case AttackModeOrder.Start:
                        attackModes.Insert(0, attackMode);
                        break;
                    case AttackModeOrder.End:
                        attackModes.Add(attackMode);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(order.ToString());
                }
            }
        }
    }

    public virtual int Priority()
    {
        return 0;
    }

    internal static bool IsFreeOffhand(RulesetCharacter character)
    {
        return character.GetOffhandWeapon() == null;
    }

    protected abstract List<RulesetAttackMode> GetAttackModes(RulesetCharacter character);

    protected virtual AttackModeOrder GetOrder(RulesetCharacter character)
    {
        return AttackModeOrder.End;
    }

    protected static bool HasGreatWeaponMasterFollowUpCondition(RulesetCharacter character)
    {
        return character != null &&
               (character.HasConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect,
                    ConditionFeatCleavingAttackFinish) ||
                character.HasConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, ConditionFeatGreatWeaponMaster2024Finish));
    }

    protected static List<IAttackModificationProvider> GetAttackModifiers(
        RulesetCharacter character)
    {
        return character switch
        {
            RulesetCharacterHero hero => hero.attackModifiers,
            RulesetCharacterMonster monster => monster.attackModifiers,
            _ => null
        };
    }

    protected static bool SupportsInventoryAttackModes(RulesetCharacter character)
    {
        return character is RulesetCharacterHero or RulesetCharacterSimulacrum &&
               character.CharacterInventory?.InventorySlotsByName != null;
    }

    [CanBeNull]
    protected static RulesetItem GetEquippedItem(
        RulesetCharacter character,
        string slotName)
    {
        return SupportsInventoryAttackModes(character) &&
               character.CharacterInventory.InventorySlotsByName.TryGetValue(
                   slotName,
                   out var slot)
            ? slot?.EquipedItem
            : null;
    }

    //Copied from RulesetAttackMode.IsComparableForNetwork, but not checking for attack number
    private static bool ModesEqual([NotNull] RulesetAttackMode a, RulesetAttackMode b)
    {
        return a.actionType == b.actionType
               && a.sourceDefinition == b.sourceDefinition
               && a.sourceObject == b.sourceObject
               && a.slotName == b.slotName
               && a.ranged == b.ranged
               && a.thrown == b.thrown
               && a.reach == b.reach
               && a.reachRange == b.reachRange
               && a.closeRange == b.closeRange
               && a.maxRange == b.maxRange
               && a.toHitBonus == b.toHitBonus
               // && attacksNumber
               && a.useVersatileDamage == b.useVersatileDamage
               && a.freeOffHand == b.freeOffHand
               && a.automaticHit == b.automaticHit
               && a.afterChargeOnly == b.afterChargeOnly;
    }
}

internal sealed class AddExtraUnarmedAttack : AddExtraAttackBase
{
    internal AddExtraUnarmedAttack(
        ActionDefinitions.ActionType actionType,
        params IsCharacterValidHandler[] validators) : base(actionType, validators)
    {
        // Empty
    }

    protected override List<RulesetAttackMode> GetAttackModes([NotNull] RulesetCharacter character)
    {
        var hero = character as RulesetCharacterHero;
        var monster = character as RulesetCharacterMonster;

        if (hero == null && monster == null)
        {
            return null;
        }

        var originalHero = character is RulesetCharacterSimulacrum
            ? null
            : character.GetOriginalHero();
        var mainHand = character.GetMainWeapon();
        // although IsUnarmed can take null this is a special case: isUnarmedWeapon vs isUnarmed only
        var isUnarmedWeapon = mainHand != null && ValidatorsWeapon.IsUnarmed(mainHand);
        var strikeDefinition = isUnarmedWeapon
            ? mainHand.ItemDefinition
            : originalHero != null
                ? originalHero.UnarmedStrikeDefinition
                : DatabaseHelper.ItemDefinitions.UnarmedStrikeBase;

        var attackModifiers = hero?.attackModifiers ?? monster?.attackModifiers;

        var attackMode = character.TryRefreshAttackMode(
            ActionType,
            strikeDefinition,
            strikeDefinition.WeaponDescription,
            IsFreeOffhand(character),
            true,
            EquipmentDefinitions.SlotTypeMainHand,
            attackModifiers,
            character.FeaturesOrigin,
            isUnarmedWeapon ? mainHand : null
        );

        return [attackMode];
    }
}

internal sealed class AddExtraMainHandAttack : AddExtraAttackBase
{
    internal AddExtraMainHandAttack(
        ActionDefinitions.ActionType actionType,
        params IsCharacterValidHandler[] validators) : base(actionType, validators)
    {
    }

    protected override List<RulesetAttackMode> GetAttackModes([NotNull] RulesetCharacter character)
    {
        if (!SupportsInventoryAttackModes(character))
        {
            return null;
        }

        var mainHandItem = character.GetMainWeapon();

        // don't use ?? on Unity Objects as it bypasses the lifetime check on the underlying object
        var strikeDefinition = mainHandItem?.ItemDefinition;

        if (!strikeDefinition)
        {
            strikeDefinition = character is RulesetCharacterHero hero
                ? hero.UnarmedStrikeDefinition
                : DatabaseHelper.ItemDefinitions.UnarmedStrikeBase;
        }

        var attackMode = character.TryRefreshAttackMode(
            ActionType,
            strikeDefinition,
            strikeDefinition.WeaponDescription,
            IsFreeOffhand(character),
            true,
            EquipmentDefinitions.SlotTypeMainHand,
            GetAttackModifiers(character),
            character.FeaturesOrigin,
            mainHandItem
        );

        if (attackMode == null)
        {
            return null;
        }

        attackMode.AddAttackTagAsNeeded(UpgradeWeaponDice.AbortUpgradeWeaponDice);

        return [attackMode];
    }
}

internal sealed class AddExtraRangedAttack : AddExtraAttackBase
{
    private readonly IsWeaponValidHandler _weaponValidator;

    internal AddExtraRangedAttack(
        ActionDefinitions.ActionType actionType,
        IsWeaponValidHandler weaponValidator,
        params IsCharacterValidHandler[] validators) : base(actionType, validators)
    {
        _weaponValidator = weaponValidator;
    }

    protected override List<RulesetAttackMode> GetAttackModes([NotNull] RulesetCharacter character)
    {
        var item = GetEquippedItem(character, EquipmentDefinitions.SlotTypeMainHand);

        if (item == null || !_weaponValidator.Invoke(null, item, character))
        {
            return null;
        }

        var strikeDefinition = item.ItemDefinition;
        var attackMode = character.TryRefreshAttackMode(
            ActionType,
            strikeDefinition,
            strikeDefinition.WeaponDescription,
            IsFreeOffhand(character),
            true,
            EquipmentDefinitions.SlotTypeMainHand,
            GetAttackModifiers(character),
            character.FeaturesOrigin,
            item
        );

        if (attackMode == null)
        {
            return null;
        }

        attackMode.Reach = false;
        attackMode.Ranged = true;
        attackMode.Thrown = ValidatorsWeapon.HasAnyWeaponTag(item.ItemDefinition, TagsDefinitions.WeaponTagThrown);
        attackMode.AttackTags.Remove(TagsDefinitions.WeaponTagMelee);

        return [attackMode];
    }
}

internal sealed class AddPolearmFollowUpAttack : AddExtraAttackBase
{
    private readonly WeaponTypeDefinition _weaponTypeDefinition;

    internal AddPolearmFollowUpAttack(WeaponTypeDefinition weaponTypeDefinition) : base(
        ActionDefinitions.ActionType.Bonus,
        ValidatorsCharacter.HasUsedWeaponType(weaponTypeDefinition),
        ValidatorsCharacter.HasMainHandWeaponType(weaponTypeDefinition))
    {
        _weaponTypeDefinition = weaponTypeDefinition;
    }

    protected override List<RulesetAttackMode> GetAttackModes([NotNull] RulesetCharacter character)
    {
        var item = GetEquippedItem(character, EquipmentDefinitions.SlotTypeMainHand);

        if (item == null ||
            !ValidatorsWeapon.IsWeaponType(item, _weaponTypeDefinition))
        {
            return null;
        }

        var strikeDefinition = item.ItemDefinition;
        var attackMode = character.TryRefreshAttackMode(
            ActionType,
            strikeDefinition,
            strikeDefinition.WeaponDescription,
            IsFreeOffhand(character),
            true,
            EquipmentDefinitions.SlotTypeMainHand,
            GetAttackModifiers(character),
            character.FeaturesOrigin,
            item
        );

        if (attackMode == null)
        {
            return null;
        }

        var effectDamageForm = attackMode.EffectDescription.EffectForms
            .FirstOrDefault(x => x.FormType == EffectForm.EffectFormType.Damage);

        if (effectDamageForm == null ||
            // ensures PAM interacts well with GWM
            HasGreatWeaponMasterFollowUpCondition(character))
        {
            return [attackMode];
        }

        effectDamageForm.DamageForm.DamageType = DamageTypeBludgeoning;
        effectDamageForm.DamageForm.DieType = DieType.D4;
        effectDamageForm.DamageForm.DiceNumber = 1;
        effectDamageForm.DamageForm.versatile = false;
        effectDamageForm.DamageForm.versatileDieType = effectDamageForm.DamageForm.DieType;
        attackMode.AddAttackTagAsNeeded(UpgradeWeaponDice.AbortUpgradeWeaponDice);

        return [attackMode];
    }
}

internal sealed class AddWhirlWindFollowUpAttack : AddExtraAttackBase
{
    private readonly WeaponTypeDefinition _weaponTypeDefinition;

    internal AddWhirlWindFollowUpAttack(WeaponTypeDefinition weaponTypeDefinition) : base(
        ActionDefinitions.ActionType.Bonus,
        ValidatorsCharacter.HasUsedWeaponType(weaponTypeDefinition),
        ValidatorsCharacter.HasMainHandWeaponType(weaponTypeDefinition))
    {
        _weaponTypeDefinition = weaponTypeDefinition;
    }

    protected override List<RulesetAttackMode> GetAttackModes([NotNull] RulesetCharacter character)
    {
        var item = GetEquippedItem(character, EquipmentDefinitions.SlotTypeMainHand);

        if (item == null ||
            !ValidatorsWeapon.IsWeaponType(item, _weaponTypeDefinition))
        {
            return null;
        }

        var strikeDefinition = item.ItemDefinition;
        var attackMode = character.TryRefreshAttackMode(
            ActionType,
            strikeDefinition,
            strikeDefinition.WeaponDescription,
            IsFreeOffhand(character),
            true,
            EquipmentDefinitions.SlotTypeMainHand,
            GetAttackModifiers(character),
            character.FeaturesOrigin,
            item
        );

        if (attackMode == null)
        {
            return null;
        }

        var effectDamageForm = attackMode.EffectDescription.EffectForms
            .FirstOrDefault(x => x.FormType == EffectForm.EffectFormType.Damage);

        if (effectDamageForm == null ||
            // ensures WhirlWind interacts well with GWM
            HasGreatWeaponMasterFollowUpCondition(character))
        {
            return [attackMode];
        }

        effectDamageForm.DamageForm.DieType = DieType.D4;
        effectDamageForm.DamageForm.DiceNumber = 1;
        effectDamageForm.DamageForm.versatile = false;
        effectDamageForm.DamageForm.versatileDieType = effectDamageForm.DamageForm.DieType;
        attackMode.AddAttackTagAsNeeded(UpgradeWeaponDice.AbortUpgradeWeaponDice);

        return [attackMode];
    }
}

internal sealed class AddBonusShieldAttack : AddExtraAttackBase
{
    internal AddBonusShieldAttack() : base(ActionDefinitions.ActionType.Bonus)
    {
        // Empty
    }

    [CanBeNull]
    protected override List<RulesetAttackMode> GetAttackModes([NotNull] RulesetCharacter character)
    {
        if (!SupportsInventoryAttackModes(character))
        {
            return null;
        }

        var offHandItem = character.GetOffhandWeapon();

        if (offHandItem == null ||
            !ValidatorsWeapon.IsShield(offHandItem.ItemDefinition))
        {
            return null;
        }

        var acModifier = offHandItem.ItemDefinition.StaticProperties
            .Where(x => x.Type == ItemPropertyDescription.PropertyType.Feature)
            .Select(x => x.FeatureDefinition)
            .OfType<FeatureDefinitionAttributeModifier>()
            .Where(x => x.ModifiedAttribute == AttributeDefinitions.ArmorClass)
            .Select(x => x.ModifierValue)
            .AddItem(0)
            .Max();
        var attackMode = character.TryRefreshAttackMode(
            ActionDefinitions.ActionType.Bonus,
            offHandItem.ItemDefinition,
            ShieldStrike.ShieldWeaponDescription,
            IsFreeOffhand(character),
            true,
            EquipmentDefinitions.SlotTypeOffHand,
            GetAttackModifiers(character),
            character.FeaturesOrigin,
            offHandItem);

        if (attackMode == null)
        {
            return null;
        }

        var damageForm = attackMode.EffectDescription.FindFirstDamageForm();

        if (damageForm != null)
        {
            var duelingTrend = damageForm.DamageBonusTrends.FirstOrDefault(x => x.sourceName == "Dueling");

            if (duelingTrend.sourceName == "Dueling")
            {
                damageForm.BonusDamage -= 2;
                // ReSharper disable once UsageOfDefaultStructEquality
                damageForm.DamageBonusTrends.Remove(duelingTrend);
            }

            if (acModifier > 0)
            {
                var magicalTrend = new TrendInfo(acModifier,
                    FeatureSourceType.Equipment, offHandItem.ItemDefinition.Name, offHandItem.ItemDefinition);

                attackMode.ToHitBonus += acModifier;
                attackMode.ToHitBonusTrends.Add(magicalTrend);
                damageForm.BonusDamage += acModifier;
                damageForm.DamageBonusTrends.Add(magicalTrend);
            }
        }

        if (offHandItem.ItemDefinition.Magical)
        {
            attackMode.AddAttackTagAsNeeded(TagsDefinitions.MagicalWeapon);
        }

        return [attackMode];
    }
}

internal sealed class AddBonusTorchAttack : AddExtraAttackBase
{
    private readonly FeatureDefinitionPower _torchPower;

    internal AddBonusTorchAttack(FeatureDefinitionPower torchPower) : base(ActionDefinitions.ActionType.Bonus)
    {
        _torchPower = torchPower;
    }

    protected override List<RulesetAttackMode> GetAttackModes([NotNull] RulesetCharacter character)
    {
        var item = GetEquippedItem(character, EquipmentDefinitions.SlotTypeOffHand);

        if (item == null || !ValidatorsCharacter.HasLightSourceOffHand(character))
        {
            return null;
        }

        var strikeDefinition = item.ItemDefinition;
        var attackMode = character.TryRefreshAttackMode(
            ActionType,
            strikeDefinition,
            strikeDefinition.WeaponDescription,
            IsFreeOffhand(character),
            true,
            EquipmentDefinitions.SlotTypeOffHand,
            GetAttackModifiers(character),
            character.FeaturesOrigin,
            item
        );

        if (attackMode == null)
        {
            return null;
        }

        attackMode.Reach = false;
        attackMode.Ranged = false;
        attackMode.Thrown = false;
        attackMode.AutomaticHit = true;
        attackMode.EffectDescription.Clear();
        attackMode.EffectDescription.Copy(_torchPower.EffectDescription);

        var proficiencyBonus = character.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);
        var dexterity = character.TryGetAttributeValue(AttributeDefinitions.Dexterity);

        attackMode.EffectDescription.fixedSavingThrowDifficultyClass =
            8 + proficiencyBonus + AttributeDefinitions.ComputeAbilityScoreModifier(dexterity);

        return [attackMode];
    }
}
