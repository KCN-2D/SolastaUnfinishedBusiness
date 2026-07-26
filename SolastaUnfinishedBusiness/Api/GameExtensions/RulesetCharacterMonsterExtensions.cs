using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

public static class RulesetCharacterMonsterExtensions
{
    public static RulesetAttackMode RefreshAttackMode(
        this RulesetCharacterMonster monster,
        ActionDefinitions.ActionType actionType,
        ItemDefinition itemDefinition,
        WeaponDescription weaponDescription,
        bool canAddAbilityDamageBonus,
        List<IAttackModificationProvider> attackModifiers,
        Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin,
        RulesetItem weapon = null)
    {
        return RefreshAttackMode(
            monster,
            actionType,
            itemDefinition,
            weaponDescription,
            true,
            canAddAbilityDamageBonus,
            EquipmentDefinitions.SlotTypeMainHand,
            attackModifiers,
            featuresOrigin,
            weapon);
    }

    internal static RulesetAttackMode RefreshAttackMode(
        this RulesetCharacterMonster monster,
        ActionDefinitions.ActionType actionType,
        ItemDefinition itemDefinition,
        WeaponDescription weaponDescription,
        bool freeOffHand,
        bool canAddAbilityDamageBonus,
        string slotName,
        List<IAttackModificationProvider> attackModifiers,
        Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin,
        RulesetItem weapon = null)
    {
        monster.TryGetShapeChangeOriginalHero(out var shapeChangedHero);
        var attackMode = RulesetAttackMode.AttackModesPool.Get();

        attackMode.Clear();
        attackMode.FreeOffHand = freeOffHand;
        attackMode.ActionType = actionType;
        attackMode.SourceDefinition = itemDefinition;
        attackMode.SlotName = slotName;
        attackMode.SourceObject = weapon;

        if (actionType == ActionDefinitions.ActionType.Main)
        {
            attackMode.AttacksNumber = monster.TryGetAttributeValue(AttributeDefinitions.AttacksNumber);
        }

        var weaponType = DatabaseRepository.GetDatabase<WeaponTypeDefinition>()
            .GetElement(weaponDescription.WeaponType);

        attackMode.AbilityScore = weaponType.WeaponProximity == AttackProximity.Melee
            ? AttributeDefinitions.Strength
            : AttributeDefinitions.Dexterity;

        var dexterity = monster.TryGetAttributeValue(AttributeDefinitions.Dexterity);
        var strength = monster.TryGetAttributeValue(AttributeDefinitions.Strength);

        if (weaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagFinesse))
        {
            if (dexterity > strength)
            {
                attackMode.AbilityScore = AttributeDefinitions.Dexterity;
            }
            else if (strength > dexterity)
            {
                attackMode.AbilityScore = AttributeDefinitions.Strength;
            }
        }

        attackMode.Ranged = weaponType.WeaponProximity == AttackProximity.Range;
        attackMode.Thrown = weaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagThrown);
        attackMode.Reach = weaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagReach);
        attackMode.ReachRange = attackMode.Reach ? weaponDescription.ReachRange : 1;

        if (attackMode.Ranged || attackMode.Thrown)
        {
            attackMode.CloseRange = weaponDescription.CloseRange;
            attackMode.MaxRange = weaponDescription.MaxRange;
        }

        if (monster.IsProficientWithItem(itemDefinition) ||
            shapeChangedHero?.IsProficientWithItem(itemDefinition) == true)
        {
            var currentValue = monster.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);

            attackMode.ToHitBonus += currentValue;
            attackMode.ToHitBonusTrends.Add(new TrendInfo(currentValue,
                FeatureSourceType.Proficiency, string.Empty, null));
        }

        var canUseItemProperties = CanUseItemProperties(monster, weapon);

        if (canUseItemProperties)
        {
            var enhancement = GetItemAttackRollModifier(
                monster,
                weapon,
                attackModifiers,
                featuresOrigin);
            var sourceName = itemDefinition.RequiresIdentification && !weapon.Identified
                ? string.Empty
                : weapon.Name;

            attackMode.ToHitBonus += enhancement;
            attackMode.ToHitBonusTrends.Add(new TrendInfo(
                enhancement,
                FeatureSourceType.Equipment,
                sourceName,
                weapon));
        }

        attackMode.EffectDescription.Copy(weaponDescription.EffectDescription);
        attackMode.UseVersatileDamage =
            freeOffHand &&
            weaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagVersatile);

        foreach (var itemTag in itemDefinition.ItemTags)
        {
            attackMode.AddAttackTagAsNeeded(itemTag);
        }

        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        var itemAttackModifiers = GetItemAttackModifiers(
            monster,
            weapon,
            canUseItemProperties);

        foreach (var attackModifier in attackModifiers)
        {
            if (attackModifier == null)
            {
                Trace.LogException(new Exception("[Tactical - Invisible for players] attackModifier is null"));
            }
            else if (service.IsValidContextForRestrictedContextProvider(
                         attackModifier, monster, itemDefinition, attackMode.Ranged, attackMode, null))
            {
                if (attackModifier.MagicalWeapon)
                {
                    attackMode.AddAttackTagAsNeeded(TagsDefinitions.MagicalWeapon);
                }

                var attackRollModifier = ComputeAttackRollModifier(
                    monster,
                    attackModifier);

                attackMode.ToHitBonus += attackRollModifier;

                var key = attackModifier as FeatureDefinition;

                if (key && featuresOrigin.TryGetValue(key, out var value))
                {
                    attackMode.ToHitBonusTrends.Add(new TrendInfo(attackRollModifier, value.sourceType,
                        featuresOrigin[key].sourceName, featuresOrigin[key].source));
                }

                if (attackModifier.AbilityScoreReplacement == AbilityScoreReplacement.DexterityIfBetterThanStrength)
                {
                    if (dexterity >= strength)
                    {
                        attackMode.AbilityScore = AttributeDefinitions.Dexterity;
                    }
                    else if (strength > dexterity)
                    {
                        attackMode.AbilityScore = AttributeDefinitions.Strength;
                    }
                }
                else if (attackModifier.AbilityScoreReplacement ==
                         AbilityScoreReplacement.SpellcastingAbility &&
                         TryGetSpellcastingAbility(
                             monster,
                             weapon,
                             key,
                             out var spellcastingAbility))
                {
                    attackMode.AbilityScore = spellcastingAbility;
                }

                if (attackModifier.DamageDieReplacement != DamageDieReplacement.None)
                {
                    var firstDamageForm = attackMode.EffectDescription.FindFirstDamageForm();

                    if (firstDamageForm != null)
                    {
                        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                        switch (attackModifier.DamageDieReplacement)
                        {
                            case DamageDieReplacement.FirstDamageForm:
                                firstDamageForm.DieType = attackModifier.ReplacedDieType;
                                if (firstDamageForm.VersatileDieType < attackModifier.ReplacedDieType)
                                {
                                    firstDamageForm.VersatileDieType = attackModifier.ReplacedDieType;
                                }

                                break;
                            case DamageDieReplacement.DieTypeByRankIfBetterThanNatural:
                                UpgradeAttackModeDieTypeWithAttackModifierByCharacterLevel(
                                    monster,
                                    attackMode,
                                    attackModifier);
                                break;
                        }
                    }
                }

                if (attackModifier.AdditionalEffectForms == null)
                {
                    continue;
                }

                foreach (var additionalEffectForm in attackModifier.AdditionalEffectForms)
                {
                    attackMode.EffectDescription.EffectForms.Add(EffectForm.GetCopy(additionalEffectForm));
                }
            }
        }

        ApplyItemAbilityScoreReplacement(
            monster,
            attackMode,
            weapon,
            itemAttackModifiers.Where(modifier =>
                !IsItemModifierPublishedAsEquipmentFeature(
                    modifier,
                    attackModifiers,
                    featuresOrigin)),
            dexterity,
            strength);

        var abilityScoreModifier =
            AttributeDefinitions.ComputeAbilityScoreModifier(monster.TryGetAttributeValue(attackMode.AbilityScore));

        attackMode.ToHitBonus += abilityScoreModifier;
        attackMode.ToHitBonusTrends.Add(new TrendInfo(abilityScoreModifier,
            FeatureSourceType.AbilityScore, attackMode.AbilityScore, null));

        var firstDamageForm1 = attackMode.EffectDescription.FindFirstDamageForm();

        if (firstDamageForm1 == null)
        {
            return attackMode;
        }

        firstDamageForm1.DamageBonusTrends.Clear();

        if (canAddAbilityDamageBonus)
        {
            firstDamageForm1.BonusDamage += abilityScoreModifier;
            firstDamageForm1.DamageBonusTrends.Add(new TrendInfo(abilityScoreModifier,
                FeatureSourceType.AbilityScore, attackMode.AbilityScore, null));
        }

        if (canUseItemProperties)
        {
            var enhancement = GetItemDamageRollModifier(
                monster,
                weapon,
                attackModifiers,
                featuresOrigin);
            var sourceName = itemDefinition.RequiresIdentification && !weapon.Identified
                ? string.Empty
                : weapon.Name;

            firstDamageForm1.BonusDamage += enhancement;
            firstDamageForm1.DamageBonusTrends.Add(new TrendInfo(
                enhancement,
                FeatureSourceType.Equipment,
                sourceName,
                weapon));
        }

        foreach (var attackModifier in attackModifiers)
        {
            if (attackModifier == null)
            {
                Trace.LogException(new Exception("[Tactical - Invisible for players] attackModifier is null"));
            }
            else if (service.IsValidContextForRestrictedContextProvider(
                         attackModifier, monster, itemDefinition, attackMode.Ranged, attackMode, null)
                     && attackModifier.DamageRollModifierMethod != AttackModifierMethod.None)
            {
                var num = ComputeDamageRollModifier(monster, attackModifier);

                firstDamageForm1.BonusDamage += num;

                var key = attackModifier as FeatureDefinition;

                if (key && featuresOrigin.TryGetValue(key, out var value))
                {
                    firstDamageForm1.DamageBonusTrends.Add(new TrendInfo(num, value.sourceType,
                        featuresOrigin[key].sourceName, featuresOrigin[key].source));
                }
            }
        }

        ApplyItemScopedAttackModifiers(attackMode, weapon, itemAttackModifiers);

        return attackMode;
    }

    internal static int ComputeAttackRollModifier(
        RulesetCharacter character,
        IAttackModificationProvider provider)
    {
        return GetAttackModifierValue(
            character,
            null,
            provider as FeatureDefinition,
            provider.AttackRollModifierMethod,
            provider.AttackRollModifier,
            provider.AttackRollAbilityScore,
            provider.AttackRollUseCasterBonus);
    }

    internal static int ComputeDamageRollModifier(
        RulesetCharacter character,
        IAttackModificationProvider provider)
    {
        return GetAttackModifierValue(
            character,
            null,
            provider as FeatureDefinition,
            provider.DamageRollModifierMethod,
            provider.DamageRollModifier,
            provider.DamageRollAbilityScore,
            provider.DamageRollUseCasterBonus);
    }

    private static int GetAttackModifierValue(
        RulesetCharacter character,
        RulesetItem weapon,
        FeatureDefinition feature,
        AttackModifierMethod method,
        int flatValue,
        string abilityScore,
        bool useCasterBonus)
    {
        var source = GetAttackModifierSource(
            character,
            weapon,
            feature,
            useCasterBonus);

        return method switch
        {
            AttackModifierMethod.SourceConditionAmount =>
                feature == null
                    ? 0
                    : character.FindFirstConditionHoldingFeature(feature)?.Amount ?? 0,
            AttackModifierMethod.AddAbilityScoreBonus
                when !string.IsNullOrEmpty(abilityScore) =>
                flatValue +
                AttributeDefinitions.ComputeAbilityScoreModifier(
                    source.TryGetAttributeValue(abilityScore)),
            AttackModifierMethod.AddProficiencyBonus =>
                flatValue +
                source.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus),
            AttackModifierMethod.None or
                AttackModifierMethod.FlatValue or
                AttackModifierMethod.AddAbilityScoreBonus => flatValue,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
        };
    }

    private static RulesetCharacter GetAttackModifierSource(
        RulesetCharacter character,
        RulesetItem weapon,
        FeatureDefinition feature,
        bool useCasterBonus)
    {
        if (!useCasterBonus || feature == null)
        {
            return character;
        }

        var sourceEffectGuid = weapon?.DynamicItemProperties
            .FirstOrDefault(property =>
                property?.FeatureDefinition == feature &&
                property.SourceEffectGuid != 0)
            ?.SourceEffectGuid;
        var sourceEffect = sourceEffectGuid.HasValue
            ? EffectHelpers.GetEffectByGuid(sourceEffectGuid.Value)
            : null;
        var source = sourceEffect == null
            ? null
            : EffectHelpers.GetCharacterByGuid(sourceEffect.SourceGuid);

        if (source != null)
        {
            return source;
        }

        var condition = character.FindFirstConditionHoldingFeature(feature);

        return condition == null
            ? character
            : EffectHelpers.GetCharacterByGuid(condition.SourceGuid) ?? character;
    }

    private static int GetItemAttackRollModifier(
        RulesetCharacterMonster monster,
        RulesetItem weapon,
        IReadOnlyCollection<IAttackModificationProvider> characterModifiers,
        IReadOnlyDictionary<FeatureDefinition, FeatureOrigin> featureOrigins)
    {
        return GetItemModifier(
            monster,
            weapon,
            characterModifiers,
            featureOrigins,
            provider => provider.AttackRollModifierMethod == AttackModifierMethod.None
                ? 0
                : GetAttackModifierValue(
                    monster,
                    weapon,
                    provider as FeatureDefinition,
                    provider.AttackRollModifierMethod,
                    provider.AttackRollModifier,
                    provider.AttackRollAbilityScore,
                    provider.AttackRollUseCasterBonus));
    }

    private static int GetItemDamageRollModifier(
        RulesetCharacterMonster monster,
        RulesetItem weapon,
        IReadOnlyCollection<IAttackModificationProvider> characterModifiers,
        IReadOnlyDictionary<FeatureDefinition, FeatureOrigin> featureOrigins)
    {
        return GetItemModifier(
            monster,
            weapon,
            characterModifiers,
            featureOrigins,
            provider => provider.DamageRollModifierMethod == AttackModifierMethod.None
                ? 0
                : GetAttackModifierValue(
                    monster,
                    weapon,
                    provider as FeatureDefinition,
                    provider.DamageRollModifierMethod,
                    provider.DamageRollModifier,
                    provider.DamageRollAbilityScore,
                    provider.DamageRollUseCasterBonus));
    }

    private static int GetItemModifier(
        RulesetCharacterMonster monster,
        RulesetItem weapon,
        IReadOnlyCollection<IAttackModificationProvider> characterModifiers,
        IReadOnlyDictionary<FeatureDefinition, FeatureOrigin> featureOrigins,
        Func<IAttackModificationProvider, int> getValue)
    {
        if (monster == null || weapon?.ItemDefinition == null)
        {
            return 0;
        }

        var identified =
            weapon.KnowledgeLevel >= EquipmentDefinitions.ItemKnowledge.Identified;
        var providers = weapon.ItemDefinition.StaticProperties
            .Where(property =>
                property?.FeatureDefinition is IAttackModificationProvider &&
                (identified ||
                 property.KnowledgeAffinity !=
                 EquipmentDefinitions.KnowledgeAffinity.InactiveAndHidden))
            .Select(property =>
                property.FeatureDefinition as IAttackModificationProvider)
            .Concat(
                weapon.DynamicItemProperties
                    .Select(property =>
                        property?.FeatureDefinition as IAttackModificationProvider)
                    .Where(provider => provider != null));

        return providers
            .Where(provider =>
                !IsItemModifierPublishedAsEquipmentFeature(
                    provider,
                    characterModifiers,
                    featureOrigins))
            .Sum(getValue);
    }

    private static bool IsItemModifierPublishedAsEquipmentFeature(
        IAttackModificationProvider provider,
        IReadOnlyCollection<IAttackModificationProvider> characterModifiers,
        IReadOnlyDictionary<FeatureDefinition, FeatureOrigin> featureOrigins)
    {
        return provider is FeatureDefinition feature &&
               characterModifiers.Contains(provider) &&
               featureOrigins.TryGetValue(feature, out var origin) &&
               origin.sourceType == FeatureSourceType.Equipment;
    }

    private static bool CanUseItemProperties(
        RulesetCharacterMonster monster,
        RulesetItem weapon)
    {
        if (weapon?.ItemDefinition == null ||
            monster is RulesetCharacterSimulacrum &&
            weapon.ItemDefinition.RequiresAttunement)
        {
            return false;
        }

        return !weapon.ItemDefinition.RequiresAttunement ||
               weapon.AttunedToCharacter == monster.Name;
    }

    private static IAttackModificationProvider[] GetItemAttackModifiers(
        RulesetCharacterMonster monster,
        RulesetItem weapon,
        bool canUseItemProperties)
    {
        if (!canUseItemProperties)
        {
            return [];
        }

        var features = new List<FeatureDefinition>();

        weapon.EnumerateFeaturesToBrowse<IAttackModificationProvider>(
            features,
            monster.Name);

        return features
            .OfType<IAttackModificationProvider>()
            .ToArray();
    }

    private static void ApplyItemAbilityScoreReplacement(
        RulesetCharacterMonster monster,
        RulesetAttackMode attackMode,
        RulesetItem weapon,
        IEnumerable<IAttackModificationProvider> itemModifiers,
        int dexterity,
        int strength)
    {
        foreach (var itemModifier in itemModifiers)
        {
            switch (itemModifier.AbilityScoreReplacement)
            {
                case AbilityScoreReplacement.DexterityIfBetterThanStrength:
                    attackMode.AbilityScore = dexterity >= strength
                        ? AttributeDefinitions.Dexterity
                        : AttributeDefinitions.Strength;
                    break;
                case AbilityScoreReplacement.SpellcastingAbility
                    when TryGetSpellcastingAbility(
                        monster,
                        weapon,
                        itemModifier as FeatureDefinition,
                        out var spellcastingAbility):
                    attackMode.AbilityScore = spellcastingAbility;
                    break;
            }
        }
    }

    private static void ApplyItemScopedAttackModifiers(
        RulesetAttackMode attackMode,
        RulesetItem weapon,
        IReadOnlyCollection<IAttackModificationProvider> itemModifiers)
    {
        if (weapon == null)
        {
            return;
        }

        var damageForm = attackMode.EffectDescription?.FindFirstDamageForm();
        var additionalDamage = itemModifiers.FirstOrDefault(modifier =>
            modifier.AdditionalDamageDice > 0);

        if (damageForm != null && additionalDamage != null)
        {
            damageForm.DiceNumber += additionalDamage.AdditionalDamageDice;
        }

        var dieReplacement = itemModifiers.FirstOrDefault(modifier =>
            modifier.DamageDieReplacement == DamageDieReplacement.FirstDamageForm);

        if (damageForm != null && dieReplacement != null)
        {
            damageForm.DieType = dieReplacement.ReplacedDieType;

            if (damageForm.VersatileDieType < dieReplacement.ReplacedDieType)
            {
                damageForm.VersatileDieType = dieReplacement.ReplacedDieType;
            }
        }

        if (weapon.ItemDefinition.Magical ||
            itemModifiers.Any(modifier => modifier.MagicalWeapon))
        {
            attackMode.AddAttackTagAsNeeded(TagsDefinitions.MagicalWeapon);
        }
    }

    private static bool TryGetSpellcastingAbility(
        RulesetCharacterMonster monster,
        RulesetItem weapon,
        FeatureDefinition attackModifier,
        out string ability)
    {
        ability = null;

        var sourceEffectGuid = weapon?.DynamicItemProperties
            .FirstOrDefault(property =>
                property?.FeatureDefinition == attackModifier &&
                property.SourceEffectGuid != 0)
            ?.SourceEffectGuid;
        var activeSpell = sourceEffectGuid.HasValue
            ? EffectHelpers.GetEffectByGuid(sourceEffectGuid.Value) as RulesetEffectSpell
            : null;
        var repertoire = SpellCastingValidation.ResolveRepertoire(
            monster,
            activeSpell?.SpellRepertoire,
            activeSpell?.SpellDefinition,
            activeSpell);

        if (repertoire != null &&
            !string.IsNullOrEmpty(repertoire.SpellCastingAbility))
        {
            ability = repertoire.SpellCastingAbility;

            return true;
        }

        ability = monster.SpellRepertoires
            .Where(candidate =>
                candidate != null &&
                !string.IsNullOrEmpty(candidate.SpellCastingAbility))
            .OrderByDescending(candidate =>
                monster.TryGetAttributeValue(candidate.SpellCastingAbility))
            .ThenBy(candidate => candidate.SpellCastingFeature?.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.SpellCastingAbility)
            .FirstOrDefault();

        return !string.IsNullOrEmpty(ability);
    }

    private static void UpgradeAttackModeDieTypeWithAttackModifierByCharacterLevel(
        RulesetCharacter character,
        RulesetAttackMode attackMode,
        IAttackModificationProvider attackModifier)
    {
        var rank = (attackModifier as FeatureDefinition)
                       ?.GetFirstSubFeatureOfType<IModifyProviderRank>()
                       ?.GetRank(character)
                   ?? character.GetClassLevel(Monk);
        var damageForm = attackMode.EffectDescription.FindFirstDamageForm();

        if (rank <= 0 || damageForm == null)
        {
            return;
        }

        var dieType = attackModifier.GetDieTypeOfRank(rank);

        if (attackMode.UseVersatileDamage)
        {
            if (dieType > damageForm.VersatileDieType)
            {
                damageForm.VersatileDieType = dieType;
            }
        }
        else if (dieType > damageForm.DieType)
        {
            damageForm.DieType = dieType;
        }

        if (damageForm.VersatileDieType < damageForm.DieType)
        {
            damageForm.VersatileDieType = damageForm.DieType;
        }
    }
}
