using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using static FeatureDefinitionFeatureSet;
using static RuleDefinitions;

// ReSharper disable once CheckNamespace
internal enum SimulacrumLifecycleState
{
    Initializing,
    Ready,
    CleanupPending,
    Terminating
}

// ReSharper disable once CheckNamespace
internal sealed class RulesetCharacterSimulacrum :
    RulesetCharacterMonster,
    IProficiencyValidator
{
    private static readonly Factory CharacterFactory = new();

    private RulesetInventory _characterInventory;
    private bool _inventoryCallbacksBound;
    private bool _inventoryEvacuated;
    private bool _inventoryEvacuationInProgress;
    private bool _inventoryNormalizationInProgress;
    private int _inventoryMutationDepth;
    private int _inventoryRefreshSuppressionDepth;
    private bool _wieldedConfigurationsCurrent;
    private bool _wieldedConfigurationRefreshInProgress;
    private bool _discardPendingEquipmentRefresh;
    private DeityDefinition _deityDefinition;
    private bool _equipmentRefreshPending;
    private List<string> _skillExpertises = [];
    private bool _skillSnapshotCaptured;
    private string _voiceId = "MAL1";
    private readonly HashSet<RulesetSpellRepertoire> _deferredRepertoireRefreshes = [];
    private int _refreshAllDepth;
    private bool _refreshNotificationPending;

    internal SimulacrumLifecycleState LifecycleState { get; private set; } =
        SimulacrumLifecycleState.Initializing;

    internal bool UsesInventoryAppearanceSeed { get; private set; }
    internal bool PublishingRestoredState { get; set; }
    internal int RefreshAllDepth => _refreshAllDepth;
    internal IReadOnlyCollection<string> SkillExpertises => _skillExpertises;
    internal bool SkillSnapshotCaptured => _skillSnapshotCaptured;

    [UsedImplicitly]
    public RulesetCharacterSimulacrum()
    {
    }

    private RulesetCharacterSimulacrum(
        MonsterDefinition monsterDefinition,
        int experience,
        SpawnOverrides spawnOverrides,
        GadgetDefinitions.CreatureSex sex,
        RulesetCharacter originalFormCharacter,
        bool keepMentalAbilityScores,
        bool useMentalAbilityScores,
        bool useOriginalFormConstitution)
        : base(
            monsterDefinition,
            experience,
            spawnOverrides,
            sex,
            originalFormCharacter,
            keepMentalAbilityScores,
            useMentalAbilityScores,
            useOriginalFormConstitution)
    {
        EnsureInventory();
    }

    internal static IRulesetCharacterMonsterFactory FactoryMarker => CharacterFactory;

    public override RulesetInventory CharacterInventory
    {
        get => EnsureInventory();
        set
        {
            _characterInventory = value;
            _inventoryCallbacksBound = false;
            BindInventory();
        }
    }

    public override DeityDefinition DeityDefinition
    {
        get => _deityDefinition;
        set => _deityDefinition = value;
    }

    public override string VoiceID
    {
        get => string.IsNullOrEmpty(_voiceId) ? "MAL1" : _voiceId;
        set => _voiceId = string.IsNullOrEmpty(value) ? "MAL1" : value;
    }

    public override int GetSpellcastingLevel(RulesetSpellRepertoire spellRepertoire)
    {
        var spellcastingClass = spellRepertoire?.SpellCastingClass;

        if (!spellcastingClass && spellRepertoire?.SpellCastingSubclass)
        {
            spellcastingClass = LevelUpHelper.GetClassForSubclass(
                spellRepertoire.SpellCastingSubclass);
        }

        if (spellcastingClass)
        {
            var classLevel = this.GetClassLevel(spellcastingClass);

            if (classLevel > 0)
            {
                return classLevel;
            }
        }

        return base.GetSpellcastingLevel(spellRepertoire);
    }

    public override void EnumerateKnownLanguages(List<string> languages)
    {
        if (!SimulacrumBehavior.TryEnumerateKnownLanguages(this, languages))
        {
            base.EnumerateKnownLanguages(languages);
        }
    }

    public override int ComputeBaseAbilityCheckBonus(
        string abilityScoreName,
        List<TrendInfo> modifierTrends,
        string proficiencyName = null,
        bool doubleProficiency = false,
        bool checkInventory = true,
        bool checkFeatures = true)
    {
        var result = base.ComputeBaseAbilityCheckBonus(
            abilityScoreName,
            modifierTrends,
            proficiencyName,
            doubleProficiency,
            checkInventory,
            checkFeatures);

        if (string.IsNullOrEmpty(proficiencyName))
        {
            return result;
        }

        var snapshotRank = GetSnapshotSkillProficiencyRank(proficiencyName);
        var currentRank = checkFeatures
            ? GetSkillProficiencyRank(proficiencyName)
            : snapshotRank;

        if (doubleProficiency && currentRank > 0)
        {
            currentRank = 2;
        }

        var proficiencyDelta =
            (currentRank - snapshotRank) *
            TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);

        if (proficiencyDelta <= 0)
        {
            return result;
        }

        modifierTrends?.Add(
            new TrendInfo(
                proficiencyDelta,
                FeatureSourceType.Proficiency,
                string.Empty,
                null));

        return result + proficiencyDelta;
    }

    public override bool IsProficient(string proficiencyName)
    {
        return base.IsProficient(proficiencyName) ||
               GetSkillProficiencyRank(proficiencyName) > 0;
    }

    internal bool HasSkillProficiency(string skillName)
    {
        return GetSkillProficiencyRank(skillName) > 0;
    }

    internal bool HasSkillExpertise(string skillName)
    {
        return GetSkillProficiencyRank(skillName) >= 2;
    }

    internal bool BeginRefreshAllTransaction()
    {
        return _refreshAllDepth++ == 0;
    }

    internal bool DeferRefreshNotification()
    {
        if (_refreshAllDepth <= 0)
        {
            return false;
        }

        _refreshNotificationPending = true;

        return true;
    }

    internal bool DeferRepertoireRefresh(RulesetSpellRepertoire repertoire)
    {
        if (_refreshAllDepth <= 0 || repertoire == null)
        {
            return false;
        }

        _deferredRepertoireRefreshes.Add(repertoire);

        return true;
    }

    internal void PublishDeferredRepertoireRefreshes()
    {
        if (_refreshAllDepth != 0 || _deferredRepertoireRefreshes.Count == 0)
        {
            return;
        }

        var repertoires = _deferredRepertoireRefreshes.ToArray();

        _deferredRepertoireRefreshes.Clear();

        foreach (var repertoire in repertoires)
        {
            repertoire.RepertoireRefreshed?.Invoke(repertoire);
        }
    }

    internal void DiscardDeferredRepertoireRefreshes()
    {
        _deferredRepertoireRefreshes.Clear();
    }

    internal bool EndRefreshAllTransaction(out bool hadPendingNotification)
    {
        hadPendingNotification = _refreshNotificationPending;

        if (_refreshAllDepth <= 0)
        {
            _refreshAllDepth = 0;
            _refreshNotificationPending = false;

            return false;
        }

        _refreshAllDepth--;

        if (_refreshAllDepth != 0)
        {
            return false;
        }

        _refreshNotificationPending = false;

        return true;
    }

    public override bool IsProficientWithItem(ItemDefinition itemDefinition)
    {
        return SimulacrumBehavior.IsProficientWithItem(this, itemDefinition);
    }

    public override CharacterClassDefinition FindClassHoldingFeature(
        FeatureDefinition featureDefinition)
    {
        return SimulacrumBehavior.FindClassHoldingFeature(this, featureDefinition);
    }

    public override DieType GetBardicInspirationDieValue()
    {
        return (DieType)TryGetAttributeValue(AttributeDefinitions.BardicInspirationDie);
    }

    public override void EnumerateFeaturesToBrowse<T>(
        List<FeatureDefinition> featuresToBrowse,
        Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin)
    {
        featuresToBrowse.Clear();
        featuresOrigin?.Clear();

        // RulesetCharacterMonster normally appends OriginalFormCharacter's live
        // features for substitutes. A Simulacrum uses OriginalFormCharacter only
        // as its lifecycle owner, so its copied snapshot must remain the sole
        // source of character features.
        EnumerateFeaturesToBrowseHierarchicaly<T>(
            SimulacrumBehavior.GetCurrentlyActiveFeatures(this),
            featuresToBrowse,
            FeatureSourceType.MonsterFeature,
            featuresOrigin,
            this);

        GetAllConditions(AllConditionsForEnumeration, false);

        foreach (var condition in AllConditionsForEnumeration)
        {
            condition.ConditionDefinition.EnumerateFeaturesToBrowse<T>(
                featuresToBrowse,
                featuresOrigin,
                this);
        }

        SimulacrumBehavior.RebindFeatureOrigins(this, featuresOrigin);
    }

    public override IEnumerable<RulesetItemDevice> EnumerateAvailableDevices(
        bool includeContainer,
        bool ignoreActivationTimeChecks = false)
    {
        return CanUseHumanoidEquipment()
            ? this.EnumerateInventoryDevices(includeContainer, ignoreActivationTimeChecks)
            : Enumerable.Empty<RulesetItemDevice>();
    }

    public override RulesetItemDevice GetFirstAvailableDevice(bool includeContainer)
    {
        return EnumerateAvailableDevices(includeContainer).FirstOrDefault();
    }

    public override bool IsMatchingEquipementCondition(
        EquipmentDefinitions.EquipmentContext equipmentContext)
    {
        return CanUseHumanoidEquipment() &&
               CharacterInventory.IsMatchingEquipementCondition(equipmentContext);
    }

    public override string GetAmmunitionType(RulesetAttackMode mode)
    {
        if (mode?.SourceDefinition is not ItemDefinition itemDefinition ||
            itemDefinition.WeaponDescription is not { } weaponDescription ||
            !weaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagAmmunition))
        {
            return string.Empty;
        }

        return weaponDescription.AmmunitionType;
    }

    public override bool TryFindTargetWieldedItem(
        out RulesetItem targetItem,
        bool fallbackOnTorsoArmor = false)
    {
        targetItem = GetEquippedItem(EquipmentDefinitions.SlotTypeMainHand);

        if (targetItem != null)
        {
            return true;
        }

        targetItem = GetEquippedItem(EquipmentDefinitions.SlotTypeOffHand);

        if (targetItem != null)
        {
            return true;
        }

        if (fallbackOnTorsoArmor)
        {
            targetItem = GetEquippedItem(EquipmentDefinitions.SlotTypeTorso);
        }

        return targetItem != null;
    }

    public override bool WieldsLightSource()
    {
        var mainHand = GetEquippedItem(EquipmentDefinitions.SlotTypeMainHand);
        var offHand = GetEquippedItem(EquipmentDefinitions.SlotTypeOffHand);

        return mainHand?.RulesetLightSource != null ||
               offHand?.RulesetLightSource != null;
    }

    public override bool WieldsItemOfDefinition(ItemDefinition itemDefinition)
    {
        var mainHand = GetEquippedItem(EquipmentDefinitions.SlotTypeMainHand);

        if (mainHand != null && mainHand.ItemDefinition == itemDefinition)
        {
            return true;
        }

        var offHand = GetEquippedItem(EquipmentDefinitions.SlotTypeOffHand);

        return offHand != null && offHand.ItemDefinition == itemDefinition;
    }

    public override bool WieldsItemOfTag(string tag)
    {
        var mainHand = GetEquippedItem(EquipmentDefinitions.SlotTypeMainHand);

        if (mainHand != null &&
            ItemHasTag(
                mainHand,
                tag,
                null,
                mainHand.ItemDefinition.SlotsWhereActive.Contains(
                    EquipmentDefinitions.SlotTypeMainHand)))
        {
            return true;
        }

        var offHand = GetEquippedItem(EquipmentDefinitions.SlotTypeOffHand);

        return offHand != null &&
               ItemHasTag(
                   offHand,
                   tag,
                   null,
                   offHand.ItemDefinition.SlotsWhereActive.Contains(
                       EquipmentDefinitions.SlotTypeOffHand));
    }

    public override bool CarriesItemOfDefinition(ItemDefinition itemDefinition)
    {
        if (!CanUseHumanoidEquipment() || itemDefinition == null)
        {
            return false;
        }

        var items = new List<RulesetItem>();

        // Match the Hero route: include nested containers and all configuration
        // slots because carried-item predicates are independent of the active set.
        CharacterInventory.EnumerateAllItems(items, true, false);

        return items.Any(item => item?.ItemDefinition == itemDefinition);
    }

    public override bool CarriesItemOfTag(string tag)
    {
        if (!CanUseHumanoidEquipment() || string.IsNullOrEmpty(tag))
        {
            return false;
        }

        var items = new List<RulesetItem>();

        CharacterInventory.EnumerateAllItems(items, true, false);

        foreach (var item in items.Where(item => item?.ItemDefinition != null))
        {
            var slot = CharacterInventory.FindSlotHoldingItem(item);
            var slotName = slot?.SlotTypeDefinition?.Name;

            if (string.IsNullOrEmpty(slotName))
            {
                continue;
            }

            var active = item.ItemDefinition.SlotsWhereActive.Contains(slotName);

            if (ItemHasTag(item, tag, false, active))
            {
                return true;
            }
        }

        return false;
    }

    public override bool HasFreeHandSlot()
    {
        return CanUseHumanoidEquipment() &&
               (GetEquippedItem(EquipmentDefinitions.SlotTypeMainHand) == null ||
                GetEquippedItem(EquipmentDefinitions.SlotTypeOffHand) == null);
    }

    public override bool IsWearingArmor()
    {
        return TryGetArmorCategory(
                   EquipmentDefinitions.SlotTypeTorso,
                   out var armorCategory) &&
               armorCategory.IsPhysicalArmor;
    }

    public override bool IsWearingHeavyArmor()
    {
        return TryGetArmorCategory(
                   EquipmentDefinitions.SlotTypeTorso,
                   out var armorCategory) &&
               armorCategory.IsPhysicalArmor &&
               armorCategory.Name == EquipmentDefinitions.HeavyArmorCategory;
    }

    public override bool IsWearingShield()
    {
        return IsShieldInSlot(EquipmentDefinitions.SlotTypeOffHand);
    }

    public override bool IsWieldingRangedWeapon()
    {
        return TryGetEquippedWeapon(
                   EquipmentDefinitions.SlotTypeMainHand,
                   out var weaponDescription) &&
               (weaponDescription.WeaponTypeDefinition?.WeaponProximity ==
                AttackProximity.Range ||
                weaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagThrown));
    }

    public override bool IsWieldingBow()
    {
        return TryGetEquippedWeapon(
                   EquipmentDefinitions.SlotTypeMainHand,
                   out var weaponDescription) &&
               weaponDescription.WeaponTypeDefinition?.IsBow == true;
    }

    public override bool IsWieldingTwoHandedWeapon()
    {
        return TryGetEquippedWeapon(
                   EquipmentDefinitions.SlotTypeMainHand,
                   out var weaponDescription) &&
               weaponDescription.WeaponTags.Contains(TagsDefinitions.WeaponTagTwoHanded);
    }

    public override bool IsDualWieldingMeleeWeapons()
    {
        return TryGetEquippedWeapon(
                   EquipmentDefinitions.SlotTypeMainHand,
                   out var mainHand) &&
               mainHand.WeaponTypeDefinition?.WeaponProximity == AttackProximity.Melee &&
               TryGetEquippedWeapon(
                   EquipmentDefinitions.SlotTypeOffHand,
                   out var offHand) &&
               offHand.WeaponTypeDefinition?.WeaponProximity == AttackProximity.Melee;
    }

    public override bool IsWieldingMonkWeapon()
    {
        return IsEmptyOrMonkWeapon(EquipmentDefinitions.SlotTypeMainHand) &&
               IsEmptyOrMonkWeapon(EquipmentDefinitions.SlotTypeOffHand);
    }

    public override bool CanCarryItem(RulesetItem item)
    {
        return CanUseHumanoidEquipment() &&
               item != null &&
               CanCarryWeight(item.ComputeWeight());
    }

    public override bool CanCarryWeight(float additionalWeight)
    {
        if (!CanUseHumanoidEquipment())
        {
            return false;
        }

        ComputeEncumbranceThresholds(out _, out _, out var maxEncumbrance);

        return CharacterInventory.ComputeCarriedWeight() + additionalWeight <= maxEncumbrance;
    }

    public override bool CanEquipOrStoreItem(RulesetItem item)
    {
        return CanUseHumanoidEquipment() &&
               CanCarryItem(item) &&
               CharacterInventory.CanEquipOrStoreItem(item, null, false, out _);
    }

    public override bool GrantItem(
        RulesetItem equipmentItem,
        bool tryToEquip,
        bool bypassWeight = false,
        bool silent = false,
        bool autostack = true,
        bool feedbackOnCharacter = false)
    {
        if (!CanUseHumanoidEquipment() ||
            equipmentItem?.ItemDefinition == null)
        {
            return false;
        }

        if (equipmentItem.Guid == 0)
        {
            equipmentItem.Register(true);
        }

        var canCarry = CanCarryItem(equipmentItem);
        var granted = false;

        if (bypassWeight || canCarry)
        {
            using (BeginInventoryMutation())
            {
                if (tryToEquip || equipmentItem.ItemDefinition.ForceEquip)
                {
                    granted = TryEquipGrantedItem(equipmentItem);
                }

                if (!granted && equipmentItem.ItemDefinition.IsAmmunition)
                {
                    granted = TryStackAmmunition(equipmentItem);
                }

                if (!granted)
                {
                    granted = CharacterInventory.StoreItem(
                        equipmentItem,
                        false,
                        null,
                        true,
                        out _,
                        autostack);
                }

                if (granted)
                {
                    if (equipmentItem.Guid != 0)
                    {
                        AcceptItem(equipmentItem);
                    }
                }
            }
        }

        if (!silent && granted)
        {
            ItemGained?.Invoke(this, equipmentItem);
        }

        if (feedbackOnCharacter && (granted || !canCarry))
        {
            ItemOwnershipChanged?.Invoke(
                this,
                equipmentItem.ItemDefinition,
                equipmentItem.StackCount,
                true,
                canCarry
                    ? EquipmentDefinitions.ItemReceiveFailureType.None
                    : EquipmentDefinitions.ItemReceiveFailureType.CannotCarry);
        }

        return granted;
    }

    private bool TryEquipGrantedItem(RulesetItem item)
    {
        var definition = item.ItemDefinition;

        if (definition.IsWeapon &&
            IsProficientWithItem(definition) &&
            TryEquipGrantedWeapon(item))
        {
            return true;
        }

        if (definition is
            {
                IsArmor: true,
                ArmorDescription.IsBaseArmorClass: false
            } &&
            TryGetWieldedConfiguration(0, out var configuration) &&
            configuration.OffHandSlot?.EquipedItem == null)
        {
            return DefineGrantedWieldedItem(
                0,
                item,
                EquipmentDefinitions.SlotTypeOffHand);
        }

        return CharacterInventory.AutoEquipItem(
            item,
            false,
            null,
            out _);
    }

    private bool TryEquipGrantedWeapon(RulesetItem item)
    {
        var description = item.ItemDefinition.WeaponDescription;

        if (description?.WeaponTypeDefinition == null)
        {
            return false;
        }

        if (description.WeaponTypeDefinition.WeaponProximity != AttackProximity.Melee)
        {
            return TryGetWieldedConfiguration(1, out var rangedConfiguration) &&
                   rangedConfiguration.MainHandSlot?.EquipedItem == null &&
                   rangedConfiguration.OffHandSlot?.EquipedItem == null &&
                   DefineGrantedWieldedItem(
                       1,
                       item,
                       EquipmentDefinitions.SlotTypeMainHand);
        }

        if (!TryGetWieldedConfiguration(0, out var meleeConfiguration))
        {
            return false;
        }

        var mainHandItem = meleeConfiguration.MainHandSlot?.EquipedItem;
        var offHandItem = meleeConfiguration.OffHandSlot?.EquipedItem;
        var twoHanded = description.WeaponTags.Contains(
            TagsDefinitions.WeaponTagTwoHanded);

        if (mainHandItem == null &&
            (!twoHanded || offHandItem == null))
        {
            return DefineGrantedWieldedItem(
                0,
                item,
                EquipmentDefinitions.SlotTypeMainHand);
        }

        if (twoHanded ||
            offHandItem != null ||
            mainHandItem?.ItemDefinition?.WeaponDescription?.WeaponTags.Contains(
                TagsDefinitions.WeaponTagTwoHanded) == true)
        {
            return false;
        }

        return DefineGrantedWieldedItem(
            0,
            item,
            EquipmentDefinitions.SlotTypeOffHand);
    }

    private bool TryGetWieldedConfiguration(
        int index,
        out RulesetWieldedConfiguration configuration)
    {
        var configurations = CharacterInventory.WieldedItemsConfigurations;

        if (configurations == null ||
            index < 0 ||
            index >= configurations.Count)
        {
            configuration = null;

            return false;
        }

        configuration = configurations[index];

        return configuration != null;
    }

    private bool DefineGrantedWieldedItem(
        int configuration,
        RulesetItem item,
        string slotType)
    {
        CharacterInventory.DefineWieldedItemsConfiguration(
            configuration,
            item,
            slotType);
        RequestWieldedItemsConfigurationRefresh();

        return true;
    }

    private bool TryStackAmmunition(RulesetItem item)
    {
        if (!CharacterInventory.InventorySlotsByType.TryGetValue(
                EquipmentDefinitions.SlotTypeAmmunition,
                out var ammunitionSlots))
        {
            return false;
        }

        RulesetInventorySlot primarySlot = null;
        RulesetInventorySlot secondarySlot = null;

        foreach (var slot in ammunitionSlots)
        {
            if (slot.Name == CharacterInventory.PrimaryAmmunitionSlot)
            {
                primarySlot = slot;
            }
            else
            {
                secondarySlot = slot;
            }
        }

        return TryMergeAmmunitionStack(primarySlot, item) ||
               TryMergeAmmunitionStack(secondarySlot, item);
    }

    private static bool TryMergeAmmunitionStack(
        RulesetInventorySlot slot,
        RulesetItem item)
    {
        var equippedItem = slot?.EquipedItem;

        if (equippedItem?.ItemDefinition != item.ItemDefinition ||
            equippedItem.StackCount >= equippedItem.ItemDefinition.StackSize)
        {
            return false;
        }

        var capacity =
            equippedItem.ItemDefinition.StackSize - equippedItem.StackCount;

        if (item.StackCount > capacity)
        {
            equippedItem.IncreaseStack(capacity);
            item.SpendStack(capacity);

            return false;
        }

        equippedItem.IncreaseStack(item.StackCount);

        if (item.Guid != 0)
        {
            item.Unregister();
        }

        return true;
    }

    public override void LoseItem(
        RulesetItem itemToLose,
        bool silent = false)
    {
        if (itemToLose == null)
        {
            return;
        }

        RulesetItem removedItem = null;

        using (BeginInventoryMutation())
        {
            var slot = CharacterInventory
                .EnumerateAllSlots(true, false, false)
                .FirstOrDefault(candidate => candidate?.EquipedItem == itemToLose);

            if (slot != null)
            {
                removedItem = slot.EquipedItem;
                slot.UnequipItem(true, silent);
            }
        }

        if (!silent && removedItem != null)
        {
            ItemLost?.Invoke(this, removedItem);
        }
    }

    public override void LoseItem(
        ItemDefinition itemDefinition,
        bool allInstances,
        bool feedbackOnCharacter = false)
    {
        if (itemDefinition == null)
        {
            return;
        }

        foreach (var slot in CharacterInventory
                     .EnumerateAllSlots(true, false, false)
                     .Where(candidate =>
                         candidate?.EquipedItem?.ItemDefinition == itemDefinition)
                     .ToArray())
        {
            var removedItem = slot.EquipedItem;

            using (BeginInventoryMutation())
            {
                if (!allInstances && removedItem.StackCount > 1)
                {
                    removedItem.SpendStack(1);
                }
                else
                {
                    slot.UnequipItem(true, false);
                }
            }

            ItemLost?.Invoke(this, removedItem);

            if (feedbackOnCharacter)
            {
                ItemOwnershipChanged?.Invoke(
                    this,
                    removedItem.ItemDefinition,
                    1,
                    false,
                    EquipmentDefinitions.ItemReceiveFailureType.None);
            }

            if (!allInstances)
            {
                break;
            }
        }
    }

    public override void ComputeEncumbranceThresholds(
        out float encumberedThreshold,
        out float heavilyEncumberedThreshold,
        out float maxEncumbrance)
    {
        CharacterInventory.ComputeCarriedWeight();

        var size = SimulacrumBehavior.TryGetHumanoidIdentity(this, out var race, out _)
            ? race.SizeDefinition.CarryingSize
            : MonsterDefinition.SizeDefinition.CarryingSize;
        var carryingCapacityMultiplier = 1f;
        var additionalCarryingCapacity = 0f;

        EnumerateFeaturesToBrowse<FeatureDefinitionEquipmentAffinity>(FeaturesToBrowse, null);

        foreach (var affinity in FeaturesToBrowse.OfType<FeatureDefinitionEquipmentAffinity>())
        {
            carryingCapacityMultiplier *= affinity.CarryingCapacityMultiplier;
            additionalCarryingCapacity += affinity.AdditionalCarryingCapacity;
        }

        if (CharacterInventory.InventorySlotsByName.TryGetValue(
                EquipmentDefinitions.SlotTypeBack,
                out var backSlot) &&
            backSlot.EquipedItem?.ItemDefinition is
                { IsContainerItem: true } containerDefinition)
        {
            carryingCapacityMultiplier *=
                containerDefinition.ContainerItemDescription.WeightCapacityMultiplier;
        }

        EquipmentDefinitions.ComputeEncumbranceThresholds(
            size,
            TryGetAttributeValue(AttributeDefinitions.Strength),
            carryingCapacityMultiplier,
            additionalCarryingCapacity,
            out encumberedThreshold,
            out heavilyEncumberedThreshold,
            out maxEncumbrance);
    }

    public override bool CanCastInvocation(RulesetInvocation invocation)
    {
        return CanCastInvocation(invocation, out _);
    }

    internal bool CanCastInvocation(RulesetInvocation invocation, out string failure)
    {
        failure = string.Empty;
        var definition = invocation?.InvocationDefinition;

        if (definition == null)
        {
            return false;
        }

        if (definition.GetPower() is { } power)
        {
            return !invocation.Used && this.CanUsePower(power);
        }

        if (definition.IsPermanent())
        {
            return true;
        }

        if (definition.GrantedSpell is not { } spell ||
            invocation.Used ||
            !invocation.IsAvailable(this))
        {
            return false;
        }

        IEnumerable<SpellDefinition> candidateSpells = spell.SpellsBundle
            ? spell.SubspellsList
            : [spell];

        foreach (var candidateSpell in candidateSpells)
        {
            if (CanCastInvocationSpell(invocation, candidateSpell, out var candidateFailure))
            {
                return true;
            }

            if (string.IsNullOrEmpty(failure) && !string.IsNullOrEmpty(candidateFailure))
            {
                failure = candidateFailure;
            }
        }

        return false;
    }

    internal bool CanCastInvocationSpell(
        RulesetInvocation invocation,
        SpellDefinition spell,
        out string failure)
    {
        failure = string.Empty;

        if (invocation?.InvocationDefinition is not { GrantedSpell: { } grantedSpell } definition ||
            spell == null ||
            invocation.Used ||
            !invocation.IsAvailable(this) ||
            (spell != grantedSpell &&
             (!grantedSpell.SpellsBundle || !grantedSpell.SubspellsList.Contains(spell))))
        {
            return false;
        }

        var componentsValid =
            IsComponentVerbalValid(spell, out failure) &&
            IsComponentSomaticValid(spell, out failure);

        if (!componentsValid)
        {
            return false;
        }

        var repertoire =
            invocation.InvocationRepertoire ?? GetSpellRepertoireForInvocations();

        if (!definition.OverrideMaterialComponent)
        {
            using (SpellCastingValidation.EnterSelectedRepertoire(repertoire))
            {
                if (!IsComponentMaterialValid(spell, out failure))
                {
                    return false;
                }
            }
        }

        return SpellCastingValidation.IsValid(
            this,
            repertoire,
            spell,
            null,
            out failure,
            bypassMaterialComponent: definition.OverrideMaterialComponent);
    }

    public override bool CanCastAnyInvocation()
    {
        return Invocations.Any(CanCastInvocation);
    }

    public override bool CanCastAnySpellWithMetamagic()
    {
        return SimulacrumBehavior.EnumerateTrainedMetamagicOptions(this).Any();
    }

    public override void UseDeviceFunction(
        RulesetItemDevice usableDevice,
        RulesetDeviceFunction function,
        int additionalCharges)
    {
        if (usableDevice?.ItemDefinition == null ||
            function?.DeviceFunctionDescription == null)
        {
            return;
        }

        if (usableDevice.ItemDefinition.RequiresAttunement)
        {
            Gui.GuiService.ShowAlert(
                "Failure/&SimulacrumCannotUseAttunedItem",
                Gui.ColorFailure,
                2.5f);

            return;
        }

        base.UseDeviceFunction(usableDevice, function, additionalCharges);

        using (BeginInventoryMutation())
        {
            var deviceDescription = usableDevice.UsableDeviceDescription;

            switch (deviceDescription.Usage)
            {
                case EquipmentDefinitions.ItemUsage.Single:
                    if (usableDevice.ItemDefinition.CanBeStacked && usableDevice.StackCount > 1)
                    {
                        usableDevice.SpendStack(1);
                    }
                    else
                    {
                        CharacterInventory.DestroyItem(usableDevice);
                    }

                    break;
                case EquipmentDefinitions.ItemUsage.Charges:
                    if (usableDevice.SpendCharges(
                            function.DeviceFunctionDescription.UseAmount + additionalCharges) > 0)
                    {
                        break;
                    }

                    switch (deviceDescription.OutOfChargesConsequence)
                    {
                        case EquipmentDefinitions.ItemOutOfCharges.Destroy:
                            CharacterInventory.DestroyItem(usableDevice);
                            break;
                        case EquipmentDefinitions.ItemOutOfCharges.DestroyOnRoll1:
                            if (RuleDefinitions.RollDie(
                                    DieType.D8,
                                    AdvantageType.None,
                                    out _,
                                    out _) == 1)
                            {
                                CharacterInventory.DestroyItem(usableDevice);
                            }

                            break;
                    }

                    break;
                case EquipmentDefinitions.ItemUsage.ByFunction:
                    usableDevice.AccountFunctionUse(function);
                    break;
            }
        }
    }

    public override void Register(bool forceGuid)
    {
        base.Register(forceGuid);
        BindInventory();

        CharacterInventory.BearerGuid = Guid;
        CharacterInventory.EnumerateAllItems(Items, true, false);

        foreach (var item in Items)
        {
            NormalizeItem(item);
            item.Register(forceGuid);
        }

        if (CharacterInventory.PersonalContainer.Guid == 0)
        {
            CharacterInventory.PersonalContainer.Register(true);
        }

        if (forceGuid)
        {
            foreach (var slot in CharacterInventory.EnumerateAllSlots(true, false, false))
            {
                if (slot.Guid == 0)
                {
                    slot.Register(forceGuid);
                }
            }
        }

        Items.Clear();
    }

    public override void Unregister()
    {
        CharacterInventory.EnumerateAllItems(Items, true, false);

        foreach (var item in Items)
        {
            item.Unregister();
        }

        Items.Clear();
        base.Unregister();
    }

    [UsedImplicitly]
    public override void SerializeAttributes(
        IAttributesSerializer serializer,
        IVersionProvider versionProvider)
    {
        base.SerializeAttributes(serializer, versionProvider);
        _voiceId = serializer.SerializeAttribute("SimulacrumVoiceID", _voiceId);
    }

    [UsedImplicitly]
    public override void SerializeElements(
        IElementsSerializer serializer,
        IVersionProvider versionProvider)
    {
        base.SerializeElements(serializer, versionProvider);
        _characterInventory = serializer.SerializeElement(
            "SimulacrumCharacterInventory",
            _characterInventory);
    }

    [UsedImplicitly]
    public override void PostLoad()
    {
        if (PostLoaded)
        {
            return;
        }

        EnsureInventory().PostLoad();
        BindInventory();
        NormalizeInventoryItems();

        foreach (var repertoire in SpellRepertoires)
        {
            repertoire.CharacterInventory = CharacterInventory;
            repertoire.CharacterName = Name;
        }

        base.PostLoad();
        NormalizeInventory();

        foreach (var provider in this.GetSubFeaturesByType<IOnCharacterPostLoad>())
        {
            provider.OnCharacterPostLoad(this);
        }
    }

    internal bool OwnsSlot(RulesetInventorySlot slot)
    {
        return slot != null &&
               (CharacterInventory.InventorySlotsByName.Values.Contains(slot) ||
                CharacterInventory.PersonalContainer.InventorySlots.Contains(slot));
    }

    internal static RulesetCharacterSimulacrum FindBySlot(RulesetInventorySlot slot)
    {
        if (slot == null)
        {
            return null;
        }

        return ServiceRepository.GetService<IRulesetEntityService>()
            ?.RulesetEntities.Values
            .OfType<RulesetCharacterSimulacrum>()
            .FirstOrDefault(character => character.OwnsSlot(slot));
    }

    internal static RulesetCharacterSimulacrum FindByContainer(RulesetContainer container)
    {
        if (container == null)
        {
            return null;
        }

        return ServiceRepository.GetService<IRulesetEntityService>()
            ?.RulesetEntities.Values
            .OfType<RulesetCharacterSimulacrum>()
            .FirstOrDefault(character =>
                ReferenceEquals(character.CharacterInventory.PersonalContainer, container));
    }

    internal void AcceptItem(RulesetItem item)
    {
        NormalizeItem(item);
    }

    private RulesetItem GetEquippedItem(string slotName)
    {
        return CharacterInventory.InventorySlotsByName[slotName].EquipedItem;
    }

    private static bool ItemHasTag(
        RulesetItem item,
        string tag,
        object context,
        bool active)
    {
        var tags = new Dictionary<string, TagsDefinitions.Criticity>();

        item.FillTags(tags, context, active);

        return tags.ContainsKey(tag);
    }

    private bool TryGetArmorCategory(
        string slotName,
        out ArmorCategoryDefinition armorCategory)
    {
        armorCategory = null;

        if (GetEquippedItem(slotName)?.ItemDefinition is not
            {
                IsArmor: true
            } definition)
        {
            return false;
        }

        armorCategory = definition.ArmorDescription?
            .ArmorTypeDefinition?
            .ArmorCategoryDefinition;

        return armorCategory != null;
    }

    private bool TryGetEquippedWeapon(
        string slotName,
        out WeaponDescription weaponDescription)
    {
        weaponDescription = null;

        if (GetEquippedItem(slotName)?.ItemDefinition is not
            {
                IsWeapon: true,
                WeaponDescription: { } description
            })
        {
            return false;
        }

        weaponDescription = description;

        return true;
    }

    private bool IsEmptyOrMonkWeapon(string slotName)
    {
        if (GetEquippedItem(slotName)?.ItemDefinition is not
            {
                IsWeapon: true
            } definition)
        {
            return true;
        }

        return definition.WeaponDescription?.IsMonkWeaponOrUnarmed() == true;
    }

    private bool IsShieldInSlot(string slotName)
    {
        return GetEquippedItem(slotName)?.ItemDefinition is
               {
                   IsArmor: true
               } definition &&
               definition.ArmorDescription?
                   .ArmorTypeDefinition?
                   .ArmorCategory == EquipmentDefinitions.ShieldCategory;
    }

    internal bool CanUseHumanoidEquipment()
    {
        return SimulacrumBehavior.CanUseHumanoidEquipment(this);
    }

    internal void SetCreationAppearanceMode(bool usesInventoryAppearance)
    {
        UsesInventoryAppearanceSeed = usesInventoryAppearance;
    }

    internal void SetLifecycleState(SimulacrumLifecycleState state)
    {
        LifecycleState = state;

        if (state != SimulacrumLifecycleState.Ready)
        {
            _equipmentRefreshPending = false;
            _deferredRepertoireRefreshes.Clear();
        }
    }

    internal void RestoreSkillProficiencySnapshot(
        IReadOnlyList<string> skillNames,
        IReadOnlyList<int> skillRanks)
    {
        _skillExpertises ??= [];
        _skillExpertises.Clear();

        if (skillNames == null ||
            skillRanks == null ||
            skillNames.Count != skillRanks.Count ||
            skillRanks.Any(rank => rank is < 1 or > 2))
        {
            _skillSnapshotCaptured = false;

            return;
        }

        _skillExpertises.AddRange(
            skillNames
                .Where((name, index) =>
                    skillRanks[index] == 2 &&
                    !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal));

        _skillSnapshotCaptured = true;
    }

    internal int GetSnapshotSkillProficiencyRank(string skillName)
    {
        if (string.IsNullOrEmpty(skillName) ||
            !SkillProficiencies.ContainsKey(skillName))
        {
            return 0;
        }

        return _skillSnapshotCaptured &&
               _skillExpertises?.Contains(skillName) == true
            ? 2
            : 1;
    }

    internal int GetSkillProficiencyRank(string skillName)
    {
        var rank = GetSnapshotSkillProficiencyRank(skillName);

        if (string.IsNullOrEmpty(skillName))
        {
            return rank;
        }

        var featuresToBrowse = new List<FeatureDefinition>();
        var featuresOrigin = new Dictionary<FeatureDefinition, FeatureOrigin>();

        EnumerateFeaturesToBrowse<FeatureDefinitionProficiency>(
            featuresToBrowse,
            featuresOrigin);

        var matchingProficiencies = featuresToBrowse
            .OfType<FeatureDefinitionProficiency>()
            .Where(feature =>
                feature.Proficiencies.Contains(skillName) &&
                featuresOrigin.TryGetValue(feature, out var origin) &&
                IsRuntimeSkillProficiencyOrigin(origin.sourceType))
            .Distinct()
            .ToArray();

        if (matchingProficiencies.Any(feature =>
                feature.ProficiencyType == ProficiencyType.Skill))
        {
            rank = Math.Max(rank, 1);
        }

        if (matchingProficiencies.Any(feature =>
                feature.ProficiencyType == ProficiencyType.Expertise))
        {
            rank = 2;
        }

        foreach (var _ in matchingProficiencies.Where(feature =>
                     feature.ProficiencyType == ProficiencyType.SkillOrExpertise))
        {
            rank = rank > 0 ? 2 : 1;
        }

        return rank;
    }

    private static bool IsRuntimeSkillProficiencyOrigin(FeatureSourceType sourceType)
    {
        return sourceType is
            FeatureSourceType.Equipment or
            FeatureSourceType.Condition or
            FeatureSourceType.Spell or
            FeatureSourceType.Power or
            FeatureSourceType.Lighting or
            FeatureSourceType.Proximity or
            FeatureSourceType.EffectProxy or
            FeatureSourceType.TargetTag;
    }

    internal IDisposable BeginInventoryMutation(bool suppressRefresh = false)
    {
        _inventoryMutationDepth++;

        if (suppressRefresh)
        {
            _inventoryRefreshSuppressionDepth++;
            _discardPendingEquipmentRefresh = true;
        }

        return new InventoryMutationScope(this, suppressRefresh);
    }

    internal void RequestWieldedItemsConfigurationRefresh()
    {
        _wieldedConfigurationsCurrent = false;
        RequestEquipmentRefresh();
    }

    internal void NormalizeInventory()
    {
        if (_inventoryNormalizationInProgress)
        {
            return;
        }

        _inventoryNormalizationInProgress = true;

        try
        {
            using (BeginInventoryMutation())
            {
                var inventory = CharacterInventory;

                inventory.BearerGuid = Guid;
                inventory.ProficiencyValidator = this;
                NormalizeInventoryItems();

                var canUseHumanoidEquipment = CanUseHumanoidEquipment();

                foreach (var slot in inventory.InventorySlotsByName.Values)
                {
                    // Native configuration refresh restores two-handed and shadow-slot
                    // invariants after this temporary reset.
                    slot.Disabled = !canUseHumanoidEquipment;
                }

                _wieldedConfigurationsCurrent = false;
                RefreshWieldedItemsConfigurations();
            }
        }
        finally
        {
            _inventoryNormalizationInProgress = false;
        }
    }

    private void NormalizeInventoryItems()
    {
        CharacterInventory.EnumerateAllItems(Items, true, false);

        foreach (var item in Items)
        {
            NormalizeItem(item);
        }

        Items.Clear();
    }

    internal void EvacuateInventory(RulesetCharacter owner)
    {
        if (_inventoryEvacuated || _inventoryEvacuationInProgress)
        {
            return;
        }

        _inventoryEvacuationInProgress = true;

        try
        {
            using (BeginInventoryMutation(true))
            {
                var inventory = CharacterInventory;
                var slots = inventory.InventorySlotsByName.Values
                    .Concat(inventory.PersonalContainer.InventorySlots)
                    .Where(x => x?.EquipedItem != null)
                    .Distinct()
                    .ToArray();
                var items = slots
                    .Select(x => x.EquipedItem)
                    .Where(x => x != null)
                    .Distinct()
                    .ToList();

                foreach (var slot in slots)
                {
                    slot.UnequipItem(true, true);
                }

                if (items.Count == 0)
                {
                    _inventoryEvacuated = true;

                    return;
                }

                if (Gui.GameLocation &&
                    GameLocationCharacter.GetFromActor(this) is { } location &&
                    ServiceRepository.GetService<IGameLocationItemService>() is { } itemService)
                {
                    try
                    {
                        foreach (var item in items)
                        {
                            item.BearerGuid = 0;
                            item.AttunedToCharacter = string.Empty;
                        }

                        itemService.DropLoot(items, location.LocationPosition);
                        _inventoryEvacuated = true;

                        return;
                    }
                    catch (Exception ex)
                    {
                        Trace.LogException(new Exception(
                            "Unable to drop Simulacrum inventory; returning it to the party instead.",
                            ex));
                    }
                }

                var recipient = owner as RulesetCharacterHero ??
                                Gui.GameCampaign?.Party?.CharactersList
                                    .Select(character => character.RulesetCharacter)
                                    .OfType<RulesetCharacterHero>()
                                    .FirstOrDefault();

                if (recipient == null)
                {
                    Trace.LogWarning(
                        "Unable to return Simulacrum inventory because no party hero is available.");

                    return;
                }

                foreach (var item in items)
                {
                    item.BearerGuid = recipient.Guid;
                    item.AttunedToCharacter = string.Empty;

                    if (!recipient.GrantItem(item, false))
                    {
                        recipient.CharacterInventory.PersonalContainer.AddSubItem(item);
                    }
                }

                _inventoryEvacuated = true;
            }
        }
        finally
        {
            _inventoryEvacuationInProgress = false;
        }
    }

    private RulesetInventory EnsureInventory()
    {
        if (_characterInventory != null)
        {
            return _characterInventory;
        }

        _characterInventory = RulesetInventory.BuildCharacterInventory(
            SolastaUnfinishedBusiness.Api.DatabaseHelper.GetDefinition<InventoryDefinition>(
                "HumanoidInventory"));
        BindInventory();

        return _characterInventory;
    }

    private void BindInventory()
    {
        if (_characterInventory == null || _inventoryCallbacksBound)
        {
            return;
        }

        _inventoryCallbacksBound = true;
        _characterInventory.BearerGuid = Guid;
        _characterInventory.ProficiencyValidator = this;
        _characterInventory.ItemEquiped += OnItemEquipped;
        _characterInventory.ItemUnequiped += OnItemUnequipped;
        _characterInventory.ItemReleased += OnItemReleased;
        _characterInventory.ItemAltered += OnItemAltered;
    }

    private void OnItemEquipped(
        RulesetInventory inventory,
        RulesetInventorySlot slot,
        RulesetItem item)
    {
        NormalizeItem(item);
        _wieldedConfigurationsCurrent = false;
        RequestEquipmentRefresh();
    }

    private void OnItemUnequipped(
        RulesetInventory inventory,
        RulesetInventorySlot slot,
        RulesetItem item)
    {
        NormalizeItem(item);
        _wieldedConfigurationsCurrent = false;
        RequestEquipmentRefresh();
    }

    private void OnItemReleased(RulesetItem item, bool destroyed)
    {
        if (!destroyed)
        {
            NormalizeItem(item);
        }

        _wieldedConfigurationsCurrent = false;
        RequestEquipmentRefresh();
    }

    private void OnItemAltered(
        RulesetInventory inventory,
        RulesetInventorySlot slot,
        RulesetItem item)
    {
        // Native item-property and light-source application refresh the bearer
        // immediately after raising ItemAltered. Keep this callback limited to
        // ownership normalization so it also keeps native effect tracking enabled
        // without recursively rebuilding the Simulacrum during that refresh.
        NormalizeItem(item);
    }

    private void NormalizeItem(RulesetItem item)
    {
        if (item == null)
        {
            return;
        }

        item.BearerGuid = Guid;
        item.AttunedToCharacter = string.Empty;
    }

    private void RequestEquipmentRefresh()
    {
        if (LifecycleState != SimulacrumLifecycleState.Ready ||
            _inventoryEvacuationInProgress ||
            _inventoryRefreshSuppressionDepth > 0)
        {
            return;
        }

        if (_inventoryMutationDepth > 0)
        {
            _equipmentRefreshPending = true;

            return;
        }

        _equipmentRefreshPending = true;

        using (BeginInventoryMutation())
        {
            // EndInventoryMutation owns the single coherent configuration, rules,
            // graphics, and notification refresh for callbacks raised outside a
            // LocalCommandManager inventory transaction.
        }
    }

    private void EndInventoryMutation(bool suppressRefresh)
    {
        if (_inventoryMutationDepth <= 0)
        {
            return;
        }

        if (suppressRefresh && _inventoryRefreshSuppressionDepth > 0)
        {
            _inventoryRefreshSuppressionDepth--;
        }

        if (_inventoryMutationDepth > 1)
        {
            _inventoryMutationDepth--;

            return;
        }

        var refresh = _equipmentRefreshPending &&
                      !_discardPendingEquipmentRefresh &&
                      _inventoryRefreshSuppressionDepth == 0;

        try
        {
            if (refresh)
            {
                RefreshWieldedItemsConfigurations();
            }
        }
        finally
        {
            // Never leave the inventory in a permanently nested/deferred state when
            // native configuration reconciliation or one of its callbacks fails.
            _inventoryMutationDepth--;
            _equipmentRefreshPending = false;
            _discardPendingEquipmentRefresh = false;
        }

        if (refresh)
        {
            SimulacrumBehavior.RefreshEquipment(this);
            NotifyEquipmentChanged();
        }
    }

    private void RefreshWieldedItemsConfigurations()
    {
        if (_wieldedConfigurationsCurrent ||
            _wieldedConfigurationRefreshInProgress)
        {
            return;
        }

        _wieldedConfigurationRefreshInProgress = true;

        try
        {
            if (CanUseHumanoidEquipment())
            {
                CharacterInventory.RefreshWieldedItemsConfigurations();
            }

            // Events raised by the native refresh describe changes that it has
            // already reconciled; they must not schedule a second pass.
            _wieldedConfigurationsCurrent = true;
        }
        finally
        {
            _wieldedConfigurationRefreshInProgress = false;
        }
    }

    private void NotifyEquipmentChanged()
    {
        this.GetSubFeaturesByType<IOnCharacterEquipmentChanged>()
            .ForEach(handler => handler.OnCharacterEquipmentChanged(this));
    }

    private sealed class InventoryMutationScope(
        RulesetCharacterSimulacrum character,
        bool suppressRefresh) : IDisposable
    {
        private RulesetCharacterSimulacrum _character = character;

        public void Dispose()
        {
            var current = _character;

            if (current == null)
            {
                return;
            }

            _character = null;
            current.EndInventoryMutation(suppressRefresh);
        }
    }

    private sealed class Factory : IRulesetCharacterMonsterFactory
    {
        public RulesetCharacterMonster Create(
            MonsterDefinition monsterDefinition,
            int experience,
            SpawnOverrides spawnOverrides,
            GadgetDefinitions.CreatureSex sex,
            RulesetCharacter originalFormCharacter,
            bool keepMentalAbilityScores,
            bool useMentalAbilityScores,
            bool useOriginalFormConstitution)
        {
            return new RulesetCharacterSimulacrum(
                monsterDefinition,
                experience,
                spawnOverrides,
                sex,
                originalFormCharacter,
                keepMentalAbilityScores,
                useMentalAbilityScores,
                useOriginalFormConstitution);
        }
    }
}
