using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Displays;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;
using TA;
using static ActionDefinitions;
using static EquipmentDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private enum TabletopFeatCatalogKind
    {
        Standalone,
        GroupedRoot,
        GroupedChild,
        Container,
        Helper
    }

    private const string Charger2024Family = "Charger2024";
    private const string DualWielder2024Family = "DualWielder2024";
    private const string ElementalAdept2024Family = "ElementalAdept2024";
    private const string Alert2024FeatName = "FeatAlert2024";
    private const string Healer2024FeatName = "FeatHealer2024";
    private const string Lucky2024FeatName = "FeatLucky2024";
    private const string Lucky2024PoolPowerName = "PowerFeatLucky2024Pool";
    private const string SavageAttacker2024FeatName = "FeatSavageAttack2024";
    internal const string SavageAttacker2024SpecialFeatureName = "SavageAttacker2024";
    private const string FeyTeleport2024Family = "FeyTeleport";
    private const string FeyTouched2024Family = "FeyTouched2024";
    private const string FeyTouched2024ChoiceTag = "FeyTouched2024Choice";
    private const string FeyTouched2024FixedTag = "FeyTouched2024Fixed";
    private const string Grappler2024Family = "Grappler2024";
    private const string HeavyArmorMaster2024Family = "HeavyArmorMaster2024";
    private const string HeavyArmorMaster2024GroupFeatName = "FeatGroupHeavyArmorMaster2024";
    private const string LegacyHeavyArmorMaster2024SettingName = "FeatHeavyArmorMaster2024";
    private const string LightlyArmored2024Family = "LightlyArmored2024";
    private const string LegacyGreatWeaponMasterFeatName = "FeatCleavingAttack";
    private const string GreatWeaponMaster2024FeatName = "FeatGreatWeaponMaster2024";
    private const string LegacyGreatWeaponMaster2024SettingName = "FeatCleavingAttack2024";
    private const string GreatWeaponMaster2024FinishConditionName = "ConditionFeatGreatWeaponMaster2024Finish";
    private const string GreatWeaponMaster2024NotificationTag = "GreatWeaponMaster2024";
    private const string MagicInitiate2024Family = "FeatMagicInitiate";
    private const string MagicInitiate2024SpellTag = "MagicInitiate2024";
    private const string ModeratelyArmored2024Family = "ModeratelyArmored2024";
    private const string HeavilyArmored2024Family = "HeavilyArmored2024";
    private const string HeavilyArmored2024GroupFeatName = "FeatGroupHeavilyArmored2024";
    private const string LegacyHeavilyArmored2024SettingName = "FeatHeavilyArmored2024";
    private const string InspiringLeader2024Family = "InspiringLeader2024";
    private const string Resilient2024Family = "Resilient2024";
    private const string MageSlayer2024Family = "MageSlayer2024";
    private const string MediumArmorMaster2024Family = "MediumArmorMaster2024";
    private const string Poisoner2024Family = "Poisoner2024";
    private const string PolearmMaster2024Family = "PolearmMaster2024";
    private const string Sentinel2024Family = "Sentinel2024";
    private const string ShadowTouched2024Family = "ShadowTouched2024";
    private const string ShadowTouched2024ChoiceTag = "ShadowTouched2024Choice";
    private const string ShadowTouched2024FixedTag = "ShadowTouched2024Fixed";
    private const string LegacySharpshooterFeatName = "FeatDeadeye";
    private const string Sharpshooter2024FeatName = "FeatSharpshooter2024";
    private const string LegacySharpshooter2024SettingName = "FeatDeadeye2024";
    private const string SpeedyFamily = "Speedy";
    private const string SpellSniper2024Family = "SpellSniper2024";
    private const string SpellListFeatFeyTouched2024ChoiceName = "SpellListFeatFeyTouched2024Choice";
    private const string SpellListFeatFeyTouched2024MistyStepName = "SpellListFeatFeyTouched2024MistyStep";
    private const string SpellListFeatShadowTouched2024ChoiceName = "SpellListFeatShadowTouched2024Choice";
    private const string SpellListFeatShadowTouched2024InvisibilityName = "SpellListFeatShadowTouched2024Invisibility";
    private const string SpellListFeatSpellSniper2024Name = "SpellListFeatSpellSniper2024";
    private const int SpellSniper2024RangeIncreaseCells = 12;
    private const string WarCaster2024Family = "WarCaster2024";

    private static readonly List<TabletopFeat2024Profile> TabletopFeat2024Profiles = [];
    private static readonly Dictionary<string, bool> OriginalFeatHiddenStateByName = [];
    private static readonly Dictionary<string, FeatDefinition> IndependentTabletopFeatByCanonicalName = [];
    private static readonly Dictionary<string, FeatDefinition> DedicatedStandaloneHalfFeat2024ByCanonicalName = [];
    private static readonly Dictionary<string, string> CanonicalTabletopFeatNameByDefinitionName = [];
    private static readonly Dictionary<string, TabletopFeatCatalogKind> ManagedTabletopFeatKinds = [];
    private static readonly Dictionary<string, string> ManagedTabletopParentNameByDefinitionName = [];
    private static readonly Dictionary<string, HashSet<string>> ManagedTabletopChildNamesByParentName = [];
    private static readonly Dictionary<string, HashSet<string>> ManagedTabletopContainerNamesByCanonicalName = [];
    private static readonly Dictionary<string, HashSet<string>> ManagedSelectableRootNamesByContainerName = [];
    private static readonly Dictionary<string, string> PendingSelectedFeatNameByHeroAndTag = [];
    private static readonly HashSet<string> ManagedTabletopFeatNames = [];
    private static readonly HashSet<string> SelectableManagedTabletopRootNames = [];
    private static readonly HashSet<string> TabletopFeat2024Names = [];
    private static readonly HashSet<string> ManagedTabletopContainerGroupNames = [];
    private static readonly HashSet<string> SlotCastableTabletop2024FeatSpellTags =
    [
        MagicInitiate2024SpellTag,
        FeyTouched2024ChoiceTag,
        FeyTouched2024FixedTag,
        ShadowTouched2024ChoiceTag,
        ShadowTouched2024FixedTag
    ];
    private static readonly List<FeatureDefinitionCastSpell.SlotsByLevelDuplet> Touched2024FixedCastingSlots =
        Enumerable.Range(1, 20)
            .Select(level => new FeatureDefinitionCastSpell.SlotsByLevelDuplet
            {
                Slots = [0, 1],
                Level = level
            })
            .ToList();
    private static readonly HashSet<string> Dedicated2024StandaloneOverrideCanonicalNames =
    [
        "FeatAlert",
        "FeatHealer",
        "FeatLucky",
        "FeatSavageAttack",
        "FeatEldritchAdept",
        "FeatMetamagicAdept",
        "FeatTacticianAdept",
        "FeatSpearMastery",
        "FeatGiftOfTheChromaticDragon",
        "FeatWoodElfMagic",
        "FeatDarkElfMagic",
        "FeatBountifulLuck",
        "FeatBladeMastery",
        "FeatDragonWings",
        "FeatDungeonDelver",
        "FeatFellHanded"
    ];
    private static readonly Dictionary<string, (string Attribute, string Suffix)[]>
        Standalone2024HalfFeatAbilityOptionsByCanonicalName =
            new()
            {
                ["FeatEldritchAdept"] =
                [
                    (AttributeDefinitions.Intelligence, "Int"),
                    (AttributeDefinitions.Wisdom, "Wis"),
                    (AttributeDefinitions.Charisma, "Cha")
                ],
                ["FeatMetamagicAdept"] =
                [
                    (AttributeDefinitions.Intelligence, "Int"),
                    (AttributeDefinitions.Wisdom, "Wis"),
                    (AttributeDefinitions.Charisma, "Cha")
                ],
                ["FeatTacticianAdept"] =
                [
                    (AttributeDefinitions.Strength, "Str"),
                    (AttributeDefinitions.Dexterity, "Dex")
                ],
                ["FeatSpearMastery"] =
                [
                    (AttributeDefinitions.Strength, "Str"),
                    (AttributeDefinitions.Dexterity, "Dex")
                ],
                ["FeatGiftOfTheChromaticDragon"] =
                [
                    (AttributeDefinitions.Strength, "Str"),
                    (AttributeDefinitions.Constitution, "Con"),
                    (AttributeDefinitions.Charisma, "Cha")
                ],
                ["FeatWoodElfMagic"] =
                [
                    (AttributeDefinitions.Wisdom, "Wis")
                ],
                ["FeatDarkElfMagic"] =
                [
                    (AttributeDefinitions.Charisma, "Cha")
                ],
                ["FeatBountifulLuck"] =
                [
                    (AttributeDefinitions.Charisma, "Cha")
                ],
                ["FeatBladeMastery"] =
                [
                    (AttributeDefinitions.Strength, "Str"),
                    (AttributeDefinitions.Dexterity, "Dex")
                ],
                ["FeatDragonWings"] =
                [
                    (AttributeDefinitions.Constitution, "Con"),
                    (AttributeDefinitions.Charisma, "Cha")
                ],
                ["FeatDungeonDelver"] =
                [
                    (AttributeDefinitions.Dexterity, "Dex"),
                    (AttributeDefinitions.Wisdom, "Wis")
                ],
                ["FeatFellHanded"] =
                [
                    (AttributeDefinitions.Strength, "Str")
                ]
            };
    private static HashSet<string> LegacyFeatGroupEnabledSnapshot = [];
    private static bool HasLegacyFeatGroupEnabledSnapshot;
    private static bool LastAppliedTabletopFeatRules2024State;
    private static bool HasLastAppliedTabletopFeatRules2024State;
    private static readonly HashSet<string> TabletopFeatHelperGroupNames =
    [
        "FeatGroupTouchedMagic",
        "FeatGroupElementalTouch"
    ];
    private static readonly string[] TabletopFeatContainerGroupNames =
    [
        "FeatGroupArmor",
        "FeatGroupBodyResilience",
        "FeatGroupTools",
        "FeatGroupRaceBound",
        "FeatGroupSkills",
        "FeatGroupGeneralAdept",
        "FeatGroupPlaneMagic",
        "FeatGroupSpellCombat",
        "FeatGroupMeleeCombat",
        "FeatGroupRangedCombat",
        "FeatGroupSupportCombat",
        "FeatGroupDefenseCombat",
        "FeatGroupAgilityCombat",
        "FeatGroupTwoHandedCombat",
        "FeatGroupTwoWeaponCombat",
        "FeatGroupUnarmoredCombat"
    ];
    private static readonly List<FeatDefinition> ManagedStandaloneTabletopFeats = [];
    private static readonly HashSet<string> ExplicitManagedLegacyRootNames =
    [
        "Ambidextrous",
        "FeatAlert",
        "FeatCharger",
        "FeatCleavingAttack",
        "FeatDeadeye",
        "FeatDefensiveDuelist",
        "FeatDualWeaponDefense",
        "FeatGroupArmor",
        "FeatGroupCreed",
        "FeatGroupElementalAdept",
        "FeatGroupFeyTeleport",
        "FeatGroupMediumArmor",
        "FeatGroupShadowTouched",
        "FeatGroupSpellSniper",
        "FeatGroupTouchedMagic",
        "FeatHeavyArmorMaster",
        "FeatGrapplerStr",
        "FeatHealer",
        "FeatInspiringLeader",
        "FeatLucky",
        "FeatMageSlayer",
        "FeatMediumArmorMaster",
        "FeatMobile",
        "FeatPoisoner",
        "FeatPolearmExpert",
        "FeatRangedExpert",
        "FeatSavageAttack",
        "FeatSentinel",
        "FeatShieldTechniques",
        OtherFeats.FeatWarCaster,
        nameof(ForestRunner)
    ];

    private static readonly string[] ExplicitIndependentLegacyStandaloneRootNames =
    [
        "FeatAcrobat",
        "FeatAlert",
        "FeatArcanist",
        "FeatBladeMastery",
        "FeatBountifulLuck",
        "FeatDarkElfMagic",
        "FeatDragonWings",
        "FeatDungeonDelver",
        "FeatDwarvenFortitude",
        "FeatEldritchAdept",
        "FeatFellHanded",
        "FeatGiftOfTheChromaticDragon",
        "FeatHealer",
        "FeatInfernalConstitution",
        "FeatLucky",
        "FeatMenacing",
        "FeatMetamagicAdept",
        "FeatSavageAttack",
        "FeatSpearMastery",
        "FeatStealthy",
        "FeatTacticianAdept",
        "FeatTheologian",
        "FeatTough",
        "FeatWoodElfMagic"
    ];

    private static readonly string[] ExplicitIndependentLegacyGroupedRootNames =
    [
        "FeatGroupAthlete",
        "FeatGroupBalefulScion",
        "FeatGroupChef",
        "FeatGroupCrusher",
        "FeatGroupDragonFear",
        "FeatGroupDragonHide",
        "FeatGroupElvenAccuracy",
        "FeatGroupFadeAway",
        "FeatGroupFightingStyle",
        "FeatGroupFlamesOfPhlegethos",
        "FeatGroupGiftOfTheGemDragon",
        "FeatGroupGrudgeBearer",
        "FeatGroupOrcishAggression",
        "FeatGroupOrcishFury",
        "FeatGroupPiercer",
        "FeatGroupRevenantGreatSword",
        "FeatGroupSecondChance",
        "FeatGroupSlasher",
        "FeatGroupSquatNimbleness",
        "FeatGroupTelekinetic",
        "FeatGroupWeaponMaster",
        "FeatGroupWeaponMastery"
    ];
    private static readonly HashSet<string> ManagedIndependentHalfFeatAbilityPrerequisiteCanonicalRoots =
    [
        "FeatGroupAthlete"
    ];
    private static readonly HashSet<string> ManagedIndependentHalfFeatWithoutAbilityPrerequisiteCanonicalRoots =
    [
        "FeatGroupBalefulScion",
        "FeatGroupChef",
        "FeatGroupCrusher",
        "FeatGroupFeyTeleport",
        "FeatGroupGiftOfTheGemDragon",
        "FeatGroupPiercer",
        "FeatResilient",
        "FeatGroupSlasher",
        "FeatGroupTelekinetic",
        "FeatGroupWeaponMaster",
        "FeatGroupWeaponMastery"
    ];
    private static readonly HashSet<string> OptInOnlyManagedTabletopCanonicalNames =
    [
        "FeatAcrobat",
        "FeatArcanist",
        "FeatBladeMastery",
        "FeatDragonWings",
        "FeatFellHanded",
        "FeatGroupGrudgeBearer",
        "FeatGroupOrcishAggression",
        "FeatMenacing",
        "FeatSpearMastery",
        "FeatStealthy",
        "FeatTheologian"
    ];
    private static readonly HashSet<string> ExcludedManagedTabletopCanonicalNames =
    [
        "FeatGroupGeneralAdept",
        "FeatArcaneArcherAdept",
        "FeatEldritchVersatilityAdept",
        "FeatInfusionsAdept",
        "FeatMonkInitiate",
        "FeatSkilled"
    ];

    private static bool _tabletopFeats2024Loaded;
    private static FeatDefinition _featAlert2024;
    private static FeatDefinition _featGreatWeaponMaster2024;
    private static FeatDefinition _featCrossbowExpert2024;
    private static FeatDefinition _featSharpshooter2024;
    private static FeatDefinition _featDefensiveDuelist2024;
    private static FeatDefinition _featHealer2024;
    private static FeatDefinition _featLucky2024;
    private static FeatDefinition _featSavageAttack2024;
    private static FeatDefinition _featGrappler2024Dex;
    private static FeatDefinition _featGrappler2024Str;
    private static FeatDefinition _featGroupCharger2024;
    private static FeatDefinition _featGroupDualWielder2024;
    private static FeatDefinition _featGroupElementalAdept2024;
    private static FeatDefinition _featGroupFeyTeleport2024;
    private static FeatDefinition _featGroupFeyTouched2024;
    private static FeatDefinition _featGroupGrappler2024;
    private static FeatDefinition _featGroupHeavyArmorMaster2024;
    private static FeatDefinition _featGroupHeavilyArmored2024;
    private static FeatDefinition _featGroupInspiringLeader2024;
    private static FeatDefinition _featGroupLightlyArmored2024;
    private static FeatDefinition _featGroupMageSlayer2024;
    private static FeatDefinition _featGroupMagicInitiate2024;
    private static FeatDefinition _featGroupMediumArmorMaster2024;
    private static FeatDefinition _featGroupModeratelyArmored2024;
    private static FeatDefinition _featGroupPoisoner2024;
    private static FeatDefinition _featGroupPolearmMaster2024;
    private static FeatDefinition _featGroupResilient2024;
    private static FeatDefinition _featGroupSentinel2024;
    private static FeatDefinition _featGroupShadowTouched2024;
    private static FeatDefinition _featGroupSpeedy;
    private static FeatDefinition _featGroupSpellSniper2024;
    private static FeatDefinition _featGroupWarCaster2024;
    private static FeatDefinition _featShieldMaster2024;
    private static readonly Dictionary<string, FeatDefinition> MagicInitiate2024ByLegacyName = [];

    internal static void Load2024TabletopFeats()
    {
        if (_tabletopFeats2024Loaded)
        {
            return;
        }

        _tabletopFeats2024Loaded = true;
        var prerequisiteOverrideState = PushModFeatPrerequisiteOverride(true);

        try
        {
            BuildOriginFeats2024();
            BuildSpeedy();
            BuildGreatWeaponMaster2024();
            BuildSharpshooter2024();
            BuildWarCaster2024();
            BuildCrossbowExpert2024();
            BuildShieldMaster2024();
            BuildDualWielder2024();
            BuildCharger2024();
            BuildDefensiveDuelist2024();
            BuildGrappler2024();
            BuildElementalAdept2024();
            BuildInspiringLeader2024();
            BuildLightlyArmored2024();
            BuildMageSlayer2024();
            BuildModeratelyArmored2024();
            BuildMediumArmorMaster2024();
            BuildHeavyArmorMaster2024();
            BuildPoisoner2024();
            BuildPolearmMaster2024();
            BuildResilient2024();
            BuildSentinel2024();
            BuildHeavilyArmored2024();
            BuildFeyTeleport2024();
            BuildMagicInitiate2024();
            BuildFeyTouched2024();
            BuildShadowTouched2024();
            BuildSpellSniper2024();
            BuildStandaloneHalfAsi2024Feats();
            MigrateRenamedManagedTabletopFeatSettingNames();
            LoadIndependentTabletopFeatCatalog();
            LoadTabletopFeat2024Profiles();

            if (Main.Settings.EnableTabletopFeatRules2024)
            {
                LoadManagedTabletopContainerMappings();
                LegacyFeatGroupEnabledSnapshot = [.. Main.Settings.FeatGroupEnabled];
                HasLegacyFeatGroupEnabledSnapshot = true;
            }
            else
            {
                ClearManagedTabletopContainerMappingsForInactiveMode();
            }

            LastAppliedTabletopFeatRules2024State = Main.Settings.EnableTabletopFeatRules2024;
            HasLastAppliedTabletopFeatRules2024State = true;
        }
        finally
        {
            RestoreModFeatPrerequisiteOverride(prerequisiteOverrideState);
        }
    }

    internal static void SwitchTabletopFeatRules2024()
    {
        if (!_tabletopFeats2024Loaded)
        {
            return;
        }

        var use2024 = Main.Settings.EnableTabletopFeatRules2024;
        var previousUse2024 = HasLastAppliedTabletopFeatRules2024State && LastAppliedTabletopFeatRules2024State;

        if (use2024 && !previousUse2024)
        {
            LegacyFeatGroupEnabledSnapshot = [.. Main.Settings.FeatGroupEnabled];
            HasLegacyFeatGroupEnabledSnapshot = true;
        }

        if (use2024)
        {
            InitializeManagedCatalogSettings();
        }

        foreach (var profile in TabletopFeat2024Profiles)
        {
            profile.Apply(use2024);
        }

        if (use2024 &&
            TryGetDefinition<FeatDefinition>("FeatGroupShadowTouched", out var legacyShadowTouched))
        {
            SetFeatVisibility(legacyShadowTouched, false);
        }

        RefreshModeAwareOriginFeatDefinitions();
        ClearPendingFeatSelections();
        FeatsContext.ClearFeatSubPanel2024UiState();

        if (use2024)
        {
            RefreshManagedTabletopContainerMappings();
        }
        else
        {
            RestoreOriginalFeatHiddenStates();
            ClearManagedTabletopContainerMappingsForInactiveMode();

            if (previousUse2024 && HasLegacyFeatGroupEnabledSnapshot)
            {
                Main.Settings.FeatGroupEnabled.Clear();

                foreach (var groupName in LegacyFeatGroupEnabledSnapshot)
                {
                    Main.Settings.FeatGroupEnabled.TryAdd(groupName);
                }
            }
            else if (!Main.Settings.RepairedLegacyFeatGroupEnabledAfter2024ContainerBugV2)
            {
                RepairLegacyFeatGroupSettingsAfter2024BugV2();
            }

            FeatsContext.RefreshFeatVisibilityFromSettings();
            GuiWrapperContext.RecacheFeats();
#if DEBUG
            LogLegacyFeatVisibilityState();
#endif
        }

        LastAppliedTabletopFeatRules2024State = use2024;
        HasLastAppliedTabletopFeatRules2024State = true;
    }

    private static void InitializeManagedCatalogSettings()
    {
        foreach (var managedRoot in IndependentTabletopFeatByCanonicalName
                     .Where(entry => entry.Value != null && SelectableManagedTabletopRootNames.Contains(entry.Value.Name)))
        {
            var feat = managedRoot.Value;

            if (Main.Settings.TabletopFeats2024Initialized.Contains(feat.Name))
            {
                continue;
            }

            if (TryGetDefinition<FeatDefinition>(managedRoot.Key, out var legacyDefinition) &&
                !ShouldSuppressLegacyAutoEnable(managedRoot.Key) &&
                IsDefinitionEnabledBySettings(legacyDefinition))
            {
                EnableDefinitionBySettings(feat);
            }

            Main.Settings.TabletopFeats2024Initialized.TryAdd(feat.Name);
        }
    }

    internal static bool ShouldForceManagedFeatPrerequisites(FeatDefinition feat)
    {
        return feat != null &&
               Main.Settings.EnableTabletopFeatRules2024 &&
               IsManagedTabletopFeat(feat);
    }

    internal static (bool active, bool disableLevel, bool disableRace, bool disableCastSpell)
        PushModFeatPrerequisiteOverride(bool force)
    {
        if (!force)
        {
            return default;
        }

        var state = (
            active: true,
            disableLevel: Main.Settings.DisableLevelPrerequisitesOnModFeats,
            disableRace: Main.Settings.DisableRacePrerequisitesOnModFeats,
            disableCastSpell: Main.Settings.DisableCastSpellPreRequisitesOnModFeats);

        Main.Settings.DisableLevelPrerequisitesOnModFeats = false;
        Main.Settings.DisableRacePrerequisitesOnModFeats = false;
        Main.Settings.DisableCastSpellPreRequisitesOnModFeats = false;

        return state;
    }

    internal static void RestoreModFeatPrerequisiteOverride(
        (bool active, bool disableLevel, bool disableRace, bool disableCastSpell) state)
    {
        if (!state.active)
        {
            return;
        }

        Main.Settings.DisableLevelPrerequisitesOnModFeats = state.disableLevel;
        Main.Settings.DisableRacePrerequisitesOnModFeats = state.disableRace;
        Main.Settings.DisableCastSpellPreRequisitesOnModFeats = state.disableCastSpell;
    }

    private static void BuildOriginFeats2024()
    {
        _featAlert2024 = BuildAlert2024();
        _featHealer2024 = BuildHealer2024();
        _featLucky2024 = BuildLucky2024();
        _featSavageAttack2024 = BuildSavageAttack2024();

        foreach (var feat in new[] { _featAlert2024, _featHealer2024, _featLucky2024, _featSavageAttack2024 }
                     .Where(feat => feat != null))
        {
            SetFeatVisibility(feat, false);
            RegisterManagedTabletopFeats(true, feat);
        }
    }

    private static FeatDefinition BuildAlert2024()
    {
        var initiativeModifier = FeatureDefinitionAttributeModifierBuilder
            .Create("AttributeModifierFeatAlert2024Initiative")
            .SetGuiPresentationNoContent(true)
            .SetModifier(
                FeatureDefinitionAttributeModifier.AttributeModifierOperation.AddProficiencyBonus,
                AttributeDefinitions.Initiative)
            .AddToDB();
        var surpriseAffinity = FeatureDefinitionConditionAffinityBuilder
            .Create("ConditionAffinityFeatAlert2024Surprised")
            .SetGuiPresentationNoContent(true)
            .SetConditionAffinityType(ConditionAffinityType.Immunity)
            .SetConditionType(ConditionDefinitions.ConditionSurprised)
            .AddCustomSubFeatures(new OnCharacterBattleStartedAlert2024())
            .AddToDB();

        return FeatDefinitionBuilder
            .Create(Alert2024FeatName)
            .SetGuiPresentation("Feat/&FeatAlertTitle", "Feat/&FeatAlert2024Description", hidden: false)
            .SetFeatures(initiativeModifier, surpriseAffinity)
            .AddToDB();
    }

    private static FeatDefinition BuildHealer2024()
    {
        var conditionBattleMedic = ConditionDefinitionBuilder
            .Create("ConditionFeatHealer2024BattleMedic")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddCustomSubFeatures(new ModifyDiceRollHitDiceHealer2024BattleMedic())
            .AddToDB();
        var powerBattleMedic = FeatureDefinitionPowerBuilder
            .Create("PowerFeatHealer2024BattleMedic")
            .SetGuiPresentation(
                "Feature/&PowerFeatHealer2024BattleMedicTitle",
                "Feature/&PowerFeatHealer2024BattleMedicDescription",
                hidden: false)
            .SetUsesFixed(ActivationTime.Action)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Touch, 0, TargetType.IndividualsUnique)
                    .SetParticleEffectParameters(CureWounds)
                    .Build())
            .AddToDB();
        powerBattleMedic.AddCustomSubFeatures(
            new FilterTargetingCharacterHealer2024BattleMedic(),
            new PowerOrSpellFinishedByMeHealer2024BattleMedic(conditionBattleMedic, powerBattleMedic));

        var healingRerolls = FeatureDefinitionDieRollModifierBuilder
            .Create("DieRollModifierFeatHealer2024HealingRerolls")
            .SetGuiPresentationNoContent(true)
            .SetModifiers(RollContext.HealValueRoll, 1, 0, 2, "Feedback/&DivineHeartEmpoweredHealingReroll")
            .AddToDB();

        return FeatDefinitionBuilder
            .Create(Healer2024FeatName)
            .SetGuiPresentation("Feat/&FeatHealerTitle", "Feat/&FeatHealer2024Description", hidden: false)
            .SetFeatures(powerBattleMedic, healingRerolls)
            .AddToDB();
    }

    private static FeatDefinition BuildLucky2024()
    {
        var powerPool = FeatureDefinitionPowerBuilder
            .Create(Lucky2024PoolPowerName)
            .SetGuiPresentationNoContent(true)
            .SetUsesProficiencyBonus(ActivationTime.NoCost, RechargeRate.LongRest)
            .SetShowCasting(false)
            .AddToDB();
        powerPool.AddCustomSubFeatures(ModifyPowerVisibility.Hidden);

        var conditionAdvantage = ConditionDefinitionBuilder
            .Create("ConditionFeatLucky2024Advantage")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialInterruptions(ConditionInterruption.AnyBattleTurnEnd)
            .AddCustomSubFeatures(new ModifyDiceRollLucky2024Advantage())
            .AddToDB();
        var powerAdvantage = FeatureDefinitionPowerSharedPoolBuilder
            .Create("PowerFeatLucky2024Advantage")
            .SetGuiPresentation(
                "Feature/&PowerFeatLucky2024AdvantageTitle",
                "Feature/&PowerFeatLucky2024AdvantageDescription",
                hidden: false)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1, TurnOccurenceType.EndOfTurn)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(EffectFormBuilder.ConditionForm(conditionAdvantage, ConditionForm.ConditionOperation.Add))
                    .Build())
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .AddToDB();
        var feature = FeatureDefinitionBuilder
            .Create("FeatureFeatLucky2024")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new CustomBehaviorLucky2024(powerPool))
            .AddToDB();

        return FeatDefinitionBuilder
            .Create(Lucky2024FeatName)
            .SetGuiPresentation("Feat/&FeatLuckyTitle", "Feat/&FeatLucky2024Description", hidden: false)
            .SetFeatures(powerPool, powerAdvantage, feature)
            .AddToDB();
    }

    private static FeatDefinition BuildSavageAttack2024()
    {
        var feature = FeatureDefinitionBuilder
            .Create("FeatureFeatSavageAttack2024")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new SavageAttacker2024Marker())
            .AddToDB();

        return FeatDefinitionBuilder
            .Create(SavageAttacker2024FeatName)
            .SetGuiPresentation("Feat/&FeatSavageAttackTitle", "Feat/&FeatSavageAttack2024Description", hidden: false)
            .SetFeatures(feature)
            .AddToDB();
    }

    internal static bool CanApplySavageAttacker2024(RulesetCharacter rulesetCharacter, bool attackModeDamage)
    {
        return Main.Settings.EnableTabletopFeatRules2024 &&
               rulesetCharacter != null &&
               attackModeDamage &&
               rulesetCharacter.GetSubFeaturesByType<SavageAttacker2024Marker>().Count > 0 &&
               GameLocationCharacter.GetFromActor(rulesetCharacter)?.OnceInMyTurnIsValid(
                   SavageAttacker2024SpecialFeatureName) == true;
    }

    internal static void MarkSavageAttacker2024Used(RulesetCharacter rulesetCharacter)
    {
        GameLocationCharacter.GetFromActor(rulesetCharacter)?.UsedSpecialFeatures.TryAdd(
            SavageAttacker2024SpecialFeatureName,
            1);
    }

    private static void BuildStandaloneHalfAsi2024Feats()
    {
        DedicatedStandaloneHalfFeat2024ByCanonicalName.Clear();

        foreach (var overrideEntry in Standalone2024HalfFeatAbilityOptionsByCanonicalName)
        {
            if (!TryGetDefinition<FeatDefinition>(overrideEntry.Key, out var sourceFeat))
            {
                continue;
            }

            var replacement = BuildStandaloneHalfAsi2024Override(sourceFeat, overrideEntry.Key, overrideEntry.Value);

            if (replacement == null)
            {
                continue;
            }

            DedicatedStandaloneHalfFeat2024ByCanonicalName[overrideEntry.Key] = replacement;
            SetFeatVisibility(replacement, false);
            RegisterManagedTabletopFeats(true, replacement);
        }
    }

    private static FeatDefinition BuildStandaloneHalfAsi2024Override(
        FeatDefinition sourceFeat,
        string canonicalName,
        IReadOnlyList<(string Attribute, string Suffix)> abilityOptions)
    {
        if (sourceFeat == null || abilityOptions == null || abilityOptions.Count == 0)
        {
            return null;
        }

        var family = sourceFeat.HasFamilyTag ? sourceFeat.FamilyTag : canonicalName;
        var localizationRootName = abilityOptions.Count == 1
            ? BuildIndependentTabletopName(canonicalName)
            : BuildStandaloneHalfFeatGroupName(canonicalName);
        var groupTitle = Get2024HalfFeatGroupTitle(
            $"Feat/&{localizationRootName}Title",
            sourceFeat,
            sourceFeat.FormatTitle());
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey(localizationRootName),
            sourceFeat,
            sourceFeat.FormatDescription());

        if (abilityOptions.Count == 1)
        {
            var abilityOption = abilityOptions[0];

            return Build2024SingleHalfFeat(
                sourceFeat,
                BuildIndependentTabletopName(canonicalName),
                GetHalfFeatAttributeModifier(abilityOption.Attribute),
                family,
                abilityOption.Attribute,
                groupTitle,
                baseDescription,
                prerequisiteValue: null);
        }

        var feats = abilityOptions
            .Select(abilityOption => Build2024SingleHalfFeat(
                sourceFeat,
                BuildStandaloneHalfFeatVariantName(canonicalName, abilityOption.Suffix),
                GetHalfFeatAttributeModifier(abilityOption.Attribute),
                family,
                abilityOption.Attribute,
                Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(abilityOption.Attribute)),
                baseDescription,
                hideFromFeats: true,
                prerequisiteValue: null))
            .Where(feat => feat != null)
            .ToArray();

        return feats.Length == 0
            ? null
            : BuildManagedGroup(
                BuildStandaloneHalfFeatGroupName(canonicalName),
                family,
                groupTitle,
                baseDescription,
                feats);
    }

    private static string BuildStandaloneHalfFeatGroupName(string canonicalName)
    {
        return canonicalName.StartsWith("Feat", StringComparison.Ordinal)
            ? $"FeatGroup{canonicalName.Substring(4)}2024"
            : $"FeatGroup{canonicalName}2024";
    }

    private static string BuildStandaloneHalfFeatVariantName(string canonicalName, string suffix)
    {
        return canonicalName.EndsWith("2024", StringComparison.Ordinal)
            ? $"{canonicalName}{suffix}"
            : $"{canonicalName}2024{suffix}";
    }

    private static void BuildSpeedy()
    {
        const string name = "FeatSpeedy";

        var movementAffinityDash = FeatureDefinitionMovementAffinityBuilder
            .Create($"MovementAffinity{name}AfterDash")
            .SetGuiPresentationNoContent(true)
            .SetImmunities(difficultTerrainImmunity: true)
            .AddToDB();

        var conditionAfterDash = ConditionDefinitionBuilder
            .Create($"Condition{name}AfterDash")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(movementAffinityDash)
            .AddToDB();

        var featureDash = FeatureDefinitionBuilder
            .Create($"Feature{name}AfterDash")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new ActionFinishedByMeAfterDash(conditionAfterDash))
            .AddToDB();

        var movementAffinity = FeatureDefinitionMovementAffinityBuilder
            .Create($"MovementAffinity{name}")
            .SetGuiPresentationNoContent(true)
            .SetBaseSpeedAdditiveModifier(2)
            .AddToDB();

        var combatAffinity = FeatureDefinitionCombatAffinityBuilder
            .Create($"CombatAffinity{name}")
            .SetGuiPresentationNoContent(true)
            .SetAttackOfOpportunityOnMeAdvantage(AdvantageType.Disadvantage)
            .AddToDB();
        var groupTitle = Gui.Localize("Feat/&FeatGroupSpeedyTitle");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupSpeedy"),
            fallbackDescription: Gui.Localize("Feat/&FeatGroupSpeedyDescription"));
        var featSpeedyDex = BuildSpeedyVariant(
            $"{name}Dex",
            AttributeDefinitions.Dexterity,
            AttributeModifierCreed_Of_Misaye);
        var featSpeedyCon = BuildSpeedyVariant(
            $"{name}Con",
            AttributeDefinitions.Constitution,
            AttributeModifierCreed_Of_Arun);

        _featGroupSpeedy = GroupFeats.MakeGroup(
            "FeatGroupSpeedy",
            SpeedyFamily,
            featSpeedyDex,
            featSpeedyCon);
        SetFeatVisibility(_featGroupSpeedy, false);
        RegisterManagedTabletopFeats(true, _featGroupSpeedy);

        FeatDefinition BuildSpeedyVariant(
            string featName,
            string attribute,
            FeatureDefinitionAttributeModifier attributeModifier)
        {
            var feat = FeatDefinitionBuilder
                .Create(featName)
                .SetGuiPresentation(
                    Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute)),
                    BuildHalfFeatDescription(attribute, baseDescription),
                    hidden: false)
                .SetFeatures(attributeModifier, movementAffinity, combatAffinity, featureDash)
                .SetFeatFamily(SpeedyFamily)
                .AddCustomSubFeatures(FeatsContext.HideFromFeats.Marker)
                .AddToDB();

            OverrideMinimalAbilityPrerequisite(feat, attribute, 13);

            return feat;
        }
    }

    private static void BuildGreatWeaponMaster2024()
    {
        var sourceFeat = GetDefinition<FeatDefinition>(LegacyGreatWeaponMasterFeatName);
        var title = Get2024HalfFeatGroupTitle("Feat/&FeatGreatWeaponMaster2024Title", sourceFeat);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey(GreatWeaponMaster2024FeatName),
            sourceFeat);

        var conditionFinish = ConditionDefinitionBuilder
            .Create(GreatWeaponMaster2024FinishConditionName)
            .SetGuiPresentation(Category.Condition)
            .SetPossessive()
            .AddCustomSubFeatures(new AddExtraMainHandAttack(ActionType.Bonus))
            .AddToDB();

        var additionalDamage = FeatureDefinitionAdditionalDamageBuilder
            .Create("AdditionalDamageFeatGreatWeaponMaster2024")
            .SetGuiPresentationNoContent(true)
            .SetNotificationTag(GreatWeaponMaster2024NotificationTag)
            .SetDamageValueDetermination(AdditionalDamageValueDetermination.ProficiencyBonus)
            .SetFrequencyLimit(FeatureLimitedUsage.OnceInMyTurn)
            .SetRequiredProperty(RestrictedContextRequiredProperty.Weapon)
            .AddCustomSubFeatures(
                new ValidateContextInsteadOfRestrictedProperty((_, _, character, _, _, mode, _) =>
                    (OperationType.Set, IsValidGreatWeaponMasterAttack(character, mode))))
            .AddToDB();

        _featGreatWeaponMaster2024 = BuildDedicatedAbilityPrerequisiteHalfFeat(
            sourceDefinition: sourceFeat,
            name: GreatWeaponMaster2024FeatName,
            attributeModifier: AttributeModifierCreed_Of_Einar,
            family: sourceFeat.HasFamilyTag ? sourceFeat.FamilyTag : null,
            attribute: AttributeDefinitions.Strength,
            title: title,
            baseDescription: baseDescription,
            prerequisiteValue: 13,
            extraFeatures: [additionalDamage]);

        _featGreatWeaponMaster2024.AddCustomSubFeatures(new CustomBehaviorGreatWeaponMaster2024(conditionFinish));
        RegisterManagedTabletopFeats(true, _featGreatWeaponMaster2024);
    }

    private static void BuildSharpshooter2024()
    {
        var sourceFeat = GetDefinition<FeatDefinition>(LegacySharpshooterFeatName);
        var title = Get2024HalfFeatGroupTitle("Feat/&FeatSharpshooter2024Title", sourceFeat);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey(Sharpshooter2024FeatName),
            sourceFeat);
        var combatAffinity = FeatureDefinitionCombatAffinityBuilder
            .Create("CombatAffinityFeatSharpshooter2024")
            .SetGuiPresentationNoContent(true)
            .SetIgnoreCover()
            .AddCustomSubFeatures(
                new ValidateContextInsteadOfRestrictedProperty((_, _, _, _, _, mode, _) =>
                    (OperationType.Set, IsRangedAttackWithWeapon(mode))),
                new RemoveRangedAttackInMeleeDisadvantage(IsRangedWeaponAttackMode),
                new BumpWeaponWeaponAttackRangeToMax(IsRangedWeaponAttackMode))
            .AddToDB();

        _featSharpshooter2024 = BuildDedicatedAbilityPrerequisiteHalfFeat(
            sourceDefinition: sourceFeat,
            name: Sharpshooter2024FeatName,
            attributeModifier: AttributeModifierCreed_Of_Misaye,
            family: sourceFeat.HasFamilyTag ? sourceFeat.FamilyTag : null,
            attribute: AttributeDefinitions.Dexterity,
            title: title,
            baseDescription: baseDescription,
            prerequisiteValue: 13,
            extraFeatures: [combatAffinity]);
        RegisterManagedTabletopFeats(true, _featSharpshooter2024);
    }

    private static void BuildWarCaster2024()
    {
        var featWarCaster = GetDefinition<FeatDefinition>(OtherFeats.FeatWarCaster);
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupWarCaster2024Title", featWarCaster);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupWarCaster2024"),
            featWarCaster,
            Gui.Localize("Feat/&FeatGroupWarCaster2024Description"));

        var featWarCaster2024Int = Build2024HalfFeatVariant(
            featWarCaster,
            "FeatWarCaster2024Int",
            AttributeModifierCreed_Of_Pakri,
            WarCaster2024Family,
            AttributeDefinitions.Intelligence,
            groupTitle,
            baseDescription,
            prerequisiteValue: null);
        var featWarCaster2024Wis = Build2024HalfFeatVariant(
            featWarCaster,
            "FeatWarCaster2024Wis",
            AttributeModifierCreed_Of_Maraike,
            WarCaster2024Family,
            AttributeDefinitions.Wisdom,
            groupTitle,
            baseDescription,
            prerequisiteValue: null);
        var featWarCaster2024Cha = Build2024HalfFeatVariant(
            featWarCaster,
            "FeatWarCaster2024Cha",
            AttributeModifierCreed_Of_Solasta,
            WarCaster2024Family,
            AttributeDefinitions.Charisma,
            groupTitle,
            baseDescription,
            prerequisiteValue: null);

        _featGroupWarCaster2024 = GroupFeats.MakeGroup(
            "FeatGroupWarCaster2024",
            WarCaster2024Family,
            featWarCaster2024Int,
            featWarCaster2024Wis,
            featWarCaster2024Cha);
        SetFeatVisibility(_featGroupWarCaster2024, false);
        RegisterManagedTabletopFeats(true, _featGroupWarCaster2024);
    }

    private static void BuildCrossbowExpert2024()
    {
        var featRangedExpert = GetDefinition<FeatDefinition>("FeatRangedExpert");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatCrossbowExpert2024"),
            featRangedExpert,
            Gui.Localize("Feat/&FeatCrossbowExpert2024Description"));

        _featCrossbowExpert2024 = BuildSingleAbilityPrerequisiteHalfFeat(
            featRangedExpert,
            "FeatCrossbowExpert2024",
            AttributeModifierCreed_Of_Misaye,
            featRangedExpert.FamilyTag,
            AttributeDefinitions.Dexterity,
            Gui.Localize("Feat/&FeatCrossbowExpert2024Title"),
            baseDescription);
        RegisterManagedTabletopFeats(true, _featCrossbowExpert2024);
    }

    private static void BuildShieldMaster2024()
    {
        const string name = "FeatShieldMaster2024";

        var featShieldTechniques = GetDefinition<FeatDefinition>("FeatShieldTechniques");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey(name),
            featShieldTechniques,
            Gui.Localize($"Feat/&{name}Description"));

        var powerPool = FeatureDefinitionPowerBuilder
            .Create($"Power{name}")
            .SetGuiPresentationNoContent(true)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetShowCasting(false)
            .AddToDB();
        var powerPush = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{name}Push")
            .SetGuiPresentation(Gui.Localize("Action/&ShoveTitle"), Gui.EmptyContent, hidden: true)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 1, TargetType.IndividualsUnique)
                    .SetSavingThrowData(
                        false,
                        AttributeDefinitions.Strength,
                        false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency,
                        AttributeDefinitions.Strength,
                        8)
                    .SetEffectForms(EffectFormBuilder.Create()
                        .SetMotionForm(MotionForm.MotionType.PushFromOrigin, 1)
                        .Build())
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();
        var powerProne = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{name}Prone")
            .SetGuiPresentation(Gui.Localize("Rules/&ConditionProneTitle"), Gui.EmptyContent, hidden: true)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 1, TargetType.IndividualsUnique)
                    .SetSavingThrowData(
                        false,
                        AttributeDefinitions.Strength,
                        false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency,
                        AttributeDefinitions.Strength,
                        8)
                    .SetEffectForms(EffectFormBuilder.Create()
                        .SetMotionForm(MotionForm.MotionType.FallProne)
                        .Build())
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();
        var conditionNoDamage = ConditionDefinitionBuilder
            .Create("ConditionFeatShieldMaster2024InterposeNoDamage")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialInterruptions(ConditionInterruption.Damaged)
            .AddToDB();
        var powerInterpose = FeatureDefinitionPowerBuilder
            .Create($"Power{name}Interpose")
            .SetGuiPresentationNoContent(true)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();

        PowerBundle.RegisterPowerBundle(powerPool, true, powerPush, powerProne);

        var feature = FeatureDefinitionBuilder
            .Create($"Feature{name}")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new CustomBehaviorShieldMaster2024(powerPool, powerInterpose, conditionNoDamage))
            .AddToDB();

        _featShieldMaster2024 = BuildDedicated2024SingleHalfFeat(
            featShieldTechniques,
            name,
            AttributeModifierCreed_Of_Einar,
            featShieldTechniques.FamilyTag,
            AttributeDefinitions.Strength,
            Gui.Localize($"Feat/&{name}Title"),
            baseDescription,
            clearAbilityPrerequisite: true);

        _featShieldMaster2024.Features.TryAdd(powerPool);
        _featShieldMaster2024.Features.TryAdd(powerPush);
        _featShieldMaster2024.Features.TryAdd(powerProne);
        _featShieldMaster2024.Features.TryAdd(powerInterpose);
        _featShieldMaster2024.Features.TryAdd(feature);
        RegisterManagedTabletopFeats(true, _featShieldMaster2024);
    }

    private static void BuildDualWielder2024()
    {
        var featDualWeaponDefense = GetDefinition<FeatDefinition>("FeatDualWeaponDefense");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupDualWielder2024Title", featDualWeaponDefense);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupDualWielder2024"),
            featDualWeaponDefense,
            Gui.Localize("Feat/&FeatGroupDualWielder2024Description"));

        var featDualWielder2024Str = BuildAbilityPrerequisiteHalfFeatVariant(
            featDualWeaponDefense,
            "FeatDualWielder2024Str",
            AttributeModifierCreed_Of_Einar,
            DualWielder2024Family,
            AttributeDefinitions.Strength,
            groupTitle,
            baseDescription);
        var featDualWielder2024Dex = BuildAbilityPrerequisiteHalfFeatVariant(
            featDualWeaponDefense,
            "FeatDualWielder2024Dex",
            AttributeModifierCreed_Of_Misaye,
            DualWielder2024Family,
            AttributeDefinitions.Dexterity,
            groupTitle,
            baseDescription);

        _featGroupDualWielder2024 = GroupFeats.MakeGroup(
            "FeatGroupDualWielder2024",
            DualWielder2024Family,
            featDualWielder2024Str,
            featDualWielder2024Dex);
        SetFeatVisibility(_featGroupDualWielder2024, false);
        RegisterManagedTabletopFeats(true, _featGroupDualWielder2024);
    }

    private static void BuildCharger2024()
    {
        const string name = "FeatCharger2024";

        var featCharger = GetDefinition<FeatDefinition>("FeatCharger");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupCharger2024Title", featCharger);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupCharger2024"),
            featCharger,
            Gui.Localize("Feat/&FeatGroupCharger2024Description"));
        var movementAffinityAfterDash = FeatureDefinitionMovementAffinityBuilder
            .Create($"MovementAffinity{name}AfterDash")
            .SetGuiPresentationNoContent(true)
            .SetBaseSpeedAdditiveModifier(2)
            .AddToDB();
        var conditionAfterDash = ConditionDefinitionBuilder
            .Create($"Condition{name}AfterDash")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(movementAffinityAfterDash)
            .AddToDB();
        var featureAfterDash = FeatureDefinitionBuilder
            .Create($"Feature{name}AfterDash")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new ActionFinishedByMeAfterDash(conditionAfterDash))
            .AddToDB();
        var powerPool = FeatureDefinitionPowerBuilder
            .Create($"Power{name}")
            .SetGuiPresentationNoContent(true)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();
        var additionalDamage = FeatureDefinitionAdditionalDamageBuilder
            .Create($"AdditionalDamage{name}")
            .SetGuiPresentationNoContent(true)
            .SetNotificationTag(name)
            .SetDamageDice(DieType.D8, 1)
            .SetRequiredProperty(RestrictedContextRequiredProperty.Weapon)
            .SetAttackModeOnly()
            .SetImpactParticleReference(FeatureDefinitionPowers.PowerRoguishHoodlumDirtyFighting)
            .AddToDB();
        var conditionAdditionalDamage = ConditionDefinitionBuilder
            .Create($"Condition{name}AddDamage")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(additionalDamage)
            .SetSpecialInterruptions(ConditionInterruption.Attacks)
            .AddToDB();
        var powerAddDamage = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{name}AddDamage")
            .SetGuiPresentation(
                Gui.Localize("Feature/&PowerFeatChargerAddDamageTitle"),
                Gui.Localize("Feature/&PowerFeatChargerAddDamageDescription"),
                hidden: true)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(EffectFormBuilder.ConditionForm(conditionAdditionalDamage))
                    .Build())
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();
        var powerShove = FeatureDefinitionPowerSharedPoolBuilder
            .Create($"Power{name}Shove")
            .SetGuiPresentation(
                Gui.Localize("Feature/&PowerFeatChargerShoveTitle"),
                Gui.Localize("Feature/&PowerFeatChargerShoveDescription"),
                hidden: true)
            .SetSharedPool(ActivationTime.NoCost, powerPool)
            .SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();

        PowerBundle.RegisterPowerBundle(powerPool, true, powerAddDamage, powerShove);

        var feature = FeatureDefinitionBuilder
            .Create($"Feature{name}")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new CustomBehaviorCharger2024(powerPool, powerShove))
            .AddToDB();

        var featCharger2024Str = BuildDedicated2024HalfFeatVariant(
            featCharger,
            $"{name}Str",
            AttributeModifierCreed_Of_Einar,
            Charger2024Family,
            AttributeDefinitions.Strength,
            groupTitle,
            baseDescription,
            extraFeatures: [featureAfterDash, powerPool, powerAddDamage, powerShove, feature]);
        var featCharger2024Dex = BuildDedicated2024HalfFeatVariant(
            featCharger,
            $"{name}Dex",
            AttributeModifierCreed_Of_Misaye,
            Charger2024Family,
            AttributeDefinitions.Dexterity,
            groupTitle,
            baseDescription,
            extraFeatures: [featureAfterDash, powerPool, powerAddDamage, powerShove, feature]);

        _featGroupCharger2024 = GroupFeats.MakeGroup(
            "FeatGroupCharger2024",
            Charger2024Family,
            featCharger2024Str,
            featCharger2024Dex);
        SetFeatVisibility(_featGroupCharger2024, false);
        RegisterManagedTabletopFeats(true, _featGroupCharger2024);
    }

    private static void BuildDefensiveDuelist2024()
    {
        var featDefensiveDuelist = GetDefinition<FeatDefinition>("FeatDefensiveDuelist");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatDefensiveDuelist2024"),
            featDefensiveDuelist,
            Gui.Localize("Feat/&FeatDefensiveDuelist2024Description"));

        _featDefensiveDuelist2024 = BuildSingleAbilityPrerequisiteHalfFeat(
            featDefensiveDuelist,
            "FeatDefensiveDuelist2024",
            AttributeModifierCreed_Of_Misaye,
            featDefensiveDuelist.FamilyTag,
            AttributeDefinitions.Dexterity,
            Gui.Localize("Feat/&FeatDefensiveDuelist2024Title"),
            baseDescription);
        RegisterManagedTabletopFeats(true, _featDefensiveDuelist2024);
    }

    private static void BuildGrappler2024()
    {
        var featGrappler = GetDefinition<FeatDefinition>("FeatGrapplerStr");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupGrappler2024Title", featGrappler);
        var baseDescription = Get2024HalfFeatBaseDescription("Feat/&FeatGroupGrappler2024Description", featGrappler);

        _featGrappler2024Str = Build2024HalfFeatVariant(
            featGrappler,
            "FeatGrappler2024Str",
            AttributeModifierCreed_Of_Einar,
            Grappler2024Family,
            AttributeDefinitions.Strength,
            groupTitle,
            baseDescription);

        _featGrappler2024Dex = Build2024HalfFeatVariant(
            featGrappler,
            "FeatGrappler2024Dex",
            AttributeModifierCreed_Of_Misaye,
            Grappler2024Family,
            AttributeDefinitions.Dexterity,
            groupTitle,
            baseDescription);

        _featGroupGrappler2024 = GroupFeats.MakeGroup(
            "FeatGroupGrappler2024",
            Grappler2024Family,
            _featGrappler2024Str,
            _featGrappler2024Dex);
        SetFeatVisibility(_featGroupGrappler2024, false);
        RegisterManagedTabletopFeats(true, _featGroupGrappler2024);
    }

    private static void BuildElementalAdept2024()
    {
        var damageTypes = new[]
        {
            DamageTypeAcid, DamageTypeCold, DamageTypeFire, DamageTypeLightning, DamageTypePoison, DamageTypeThunder
        };
        var attributes = new[]
        {
            (AttributeDefinitions.Intelligence, AttributeModifierCreed_Of_Pakri),
            (AttributeDefinitions.Wisdom, AttributeModifierCreed_Of_Maraike),
            (AttributeDefinitions.Charisma, AttributeModifierCreed_Of_Solasta)
        };
        var groupTitle = Gui.Localize("Feat/&FeatGroupElementalAdept2024Title");
        var groupFeats = new List<FeatDefinition>();

        foreach (var damageType in damageTypes)
        {
            var feat = GetDefinition<FeatDefinition>($"FeatElementalAdept{damageType}");
            var damageTitle = Gui.Localize($"Rules/&{damageType}Title");
            var baseDescription = Gui.Format("Feat/&FeatElementalAdeptDescription", damageTitle);

            foreach (var (attribute, modifier) in attributes)
            {
                groupFeats.Add(
                    Build2024HalfFeatVariant(
                        feat,
                        $"FeatElementalAdept2024{damageType}{attribute}",
                        modifier,
                        ElementalAdept2024Family,
                        attribute,
                        groupTitle,
                        baseDescription,
                        prerequisiteValue: null,
                        explicitTitle: Gui.Format("Feat/&GeneralFeat2024VariantTitle2", groupTitle, damageTitle,
                            GetAttributeTitle(attribute))));
            }
        }

        _featGroupElementalAdept2024 = GroupFeats.MakeGroup(
            "FeatGroupElementalAdept2024",
            ElementalAdept2024Family,
            groupFeats);
        SetFeatVisibility(_featGroupElementalAdept2024, false);
        RegisterManagedTabletopFeats(true, _featGroupElementalAdept2024);
    }

    private static void BuildInspiringLeader2024()
    {
        var feat = GetDefinition<FeatDefinition>("FeatInspiringLeader");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupInspiringLeader2024Title", feat);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupInspiringLeader2024"),
            fallbackDescription: feat.FormatDescription());
        var legacyPower = GetDefinition<FeatureDefinitionPower>("PowerFeatInspiringLeader");
        var powerWis = FeatureDefinitionPowerBuilder
            .Create(legacyPower, "PowerFeatInspiringLeader2024Wis")
            .SetExplicitAbilityScore(AttributeDefinitions.Wisdom)
            .AddToDB();
        var powerCha = FeatureDefinitionPowerBuilder
            .Create(legacyPower, "PowerFeatInspiringLeader2024Cha")
            .SetExplicitAbilityScore(AttributeDefinitions.Charisma)
            .AddToDB();
        var featInspiringLeader2024Wis = BuildDedicated2024HalfFeatVariant(
            feat,
            "FeatInspiringLeader2024Wis",
            AttributeModifierCreed_Of_Maraike,
            InspiringLeader2024Family,
            AttributeDefinitions.Wisdom,
            groupTitle,
            baseDescription,
            extraFeatures: [powerWis]);
        var featInspiringLeader2024Cha = BuildDedicated2024HalfFeatVariant(
            feat,
            "FeatInspiringLeader2024Cha",
            AttributeModifierCreed_Of_Solasta,
            InspiringLeader2024Family,
            AttributeDefinitions.Charisma,
            groupTitle,
            baseDescription,
            extraFeatures: [powerCha]);

        _featGroupInspiringLeader2024 = GroupFeats.MakeGroup(
            "FeatGroupInspiringLeader2024",
            InspiringLeader2024Family,
            featInspiringLeader2024Wis,
            featInspiringLeader2024Cha);
        SetFeatVisibility(_featGroupInspiringLeader2024, false);
        RegisterManagedTabletopFeats(true, _featGroupInspiringLeader2024);
    }

    private static void BuildLightlyArmored2024()
    {
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupLightlyArmored2024Title");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupLightlyArmored2024"),
            fallbackDescription: Gui.Localize("Feat/&FeatGroupLightlyArmored2024Description"));
        var proficiency = FeatureDefinitionProficiencyBuilder
            .Create("ProficiencyFeatLightlyArmored2024")
            .SetGuiPresentationNoContent(true)
            .SetProficiencies(ProficiencyType.Armor, LightArmorCategory, ShieldCategory)
            .AddToDB();

        var featLightlyArmored2024Str = BuildArmorHalfFeat(
            "FeatLightlyArmored2024Str",
            Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(AttributeDefinitions.Strength)),
            AttributeDefinitions.Strength,
            baseDescription,
            AttributeModifierCreed_Of_Einar,
            LightlyArmored2024Family,
            proficiency,
            clearAbilityPrerequisite: true);
        var featLightlyArmored2024Dex = BuildArmorHalfFeat(
            "FeatLightlyArmored2024Dex",
            Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(AttributeDefinitions.Dexterity)),
            AttributeDefinitions.Dexterity,
            baseDescription,
            AttributeModifierCreed_Of_Misaye,
            LightlyArmored2024Family,
            proficiency,
            clearAbilityPrerequisite: true);

        _featGroupLightlyArmored2024 = GroupFeats.MakeGroup(
            "FeatGroupLightlyArmored2024",
            LightlyArmored2024Family,
            featLightlyArmored2024Str,
            featLightlyArmored2024Dex);
        SetFeatVisibility(_featGroupLightlyArmored2024, false);
        RegisterManagedTabletopFeats(true, _featGroupLightlyArmored2024);
    }

    private static void BuildMageSlayer2024()
    {
        var feat = GetDefinition<FeatDefinition>("FeatMageSlayer");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupMageSlayer2024Title", feat);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupMageSlayer2024"),
            feat,
            Gui.Localize("Feat/&FeatGroupMageSlayer2024Description"));
        var powerSaving = FeatureDefinitionPowerBuilder
            .Create("PowerFeatMageSlayer2024Saving")
            .SetGuiPresentationNoContent(true)
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.ShortRest)
            .SetShowCasting(false)
            .AddToDB();
        var condition = ConditionDefinitionBuilder
            .Create("ConditionFeatMageSlayer2024ConcentrationBreaker")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddFeatures(
                FeatureDefinitionMagicAffinityBuilder
                    .Create("MagicAffinityFeatMageSlayer2024ConcentrationBreaker")
                    .SetGuiPresentationNoContent(true)
                    .SetConcentrationModifiers(ConcentrationAffinity.Disadvantage)
                    .AddToDB())
            .SetSpecialInterruptions(ConditionInterruption.AnyBattleTurnEnd)
            .AddToDB();
        var feature = FeatureDefinitionBuilder
            .Create("FeatureFeatMageSlayer2024")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new OtherFeats.CustomBehaviorMageSlayer(powerSaving, condition))
            .AddToDB();

        _featGroupMageSlayer2024 = GroupFeats.MakeGroup(
            "FeatGroupMageSlayer2024",
            MageSlayer2024Family,
            BuildDedicated2024HalfFeatVariant(
                feat,
                "FeatMageSlayer2024Str",
                AttributeModifierCreed_Of_Einar,
                MageSlayer2024Family,
                AttributeDefinitions.Strength,
                groupTitle,
                baseDescription,
                extraFeatures: [powerSaving, feature]),
            BuildDedicated2024HalfFeatVariant(
                feat,
                "FeatMageSlayer2024Dex",
                AttributeModifierCreed_Of_Misaye,
                MageSlayer2024Family,
                AttributeDefinitions.Dexterity,
                groupTitle,
                baseDescription,
                extraFeatures: [powerSaving, feature]));
        SetFeatVisibility(_featGroupMageSlayer2024, false);
        RegisterManagedTabletopFeats(true, _featGroupMageSlayer2024);
    }

    private static void BuildModeratelyArmored2024()
    {
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupModeratelyArmored2024Title");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupModeratelyArmored2024"),
            fallbackDescription: Gui.Localize("Feat/&FeatGroupModeratelyArmored2024Description"));
        var proficiency = FeatureDefinitionProficiencyBuilder
            .Create("ProficiencyFeatModeratelyArmored2024")
            .SetGuiPresentationNoContent(true)
            .SetProficiencies(ProficiencyType.Armor, MediumArmorCategory)
            .AddToDB();

        var featModeratelyArmored2024Str = BuildArmorHalfFeat(
            "FeatModeratelyArmored2024Str",
            Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(AttributeDefinitions.Strength)),
            AttributeDefinitions.Strength,
            baseDescription,
            AttributeModifierCreed_Of_Einar,
            ModeratelyArmored2024Family,
            proficiency,
            LightArmorCategory,
            clearAbilityPrerequisite: true);
        var featModeratelyArmored2024Dex = BuildArmorHalfFeat(
            "FeatModeratelyArmored2024Dex",
            Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(AttributeDefinitions.Dexterity)),
            AttributeDefinitions.Dexterity,
            baseDescription,
            AttributeModifierCreed_Of_Misaye,
            ModeratelyArmored2024Family,
            proficiency,
            LightArmorCategory,
            clearAbilityPrerequisite: true);

        _featGroupModeratelyArmored2024 = GroupFeats.MakeGroup(
            "FeatGroupModeratelyArmored2024",
            ModeratelyArmored2024Family,
            featModeratelyArmored2024Str,
            featModeratelyArmored2024Dex);
        SetFeatVisibility(_featGroupModeratelyArmored2024, false);
        RegisterManagedTabletopFeats(true, _featGroupModeratelyArmored2024);
    }

    private static void BuildMediumArmorMaster2024()
    {
        var feat = GetDefinition<FeatDefinition>("FeatMediumArmorMaster");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupMediumArmorMaster2024Title", feat);
        var baseDescription = Get2024HalfFeatBaseDescription(null, feat);

        _featGroupMediumArmorMaster2024 = GroupFeats.MakeGroup(
            "FeatGroupMediumArmorMaster2024",
            MediumArmorMaster2024Family,
            Build2024HalfFeatVariant(
                feat,
                "FeatMediumArmorMaster2024Str",
                AttributeModifierCreed_Of_Einar,
                MediumArmorMaster2024Family,
                AttributeDefinitions.Strength,
                groupTitle,
                baseDescription,
                clearAbilityPrerequisite: true),
            Build2024HalfFeatVariant(
                feat,
                "FeatMediumArmorMaster2024Dex",
                AttributeModifierCreed_Of_Misaye,
                MediumArmorMaster2024Family,
                AttributeDefinitions.Dexterity,
                groupTitle,
                baseDescription,
                clearAbilityPrerequisite: true));
        SetFeatVisibility(_featGroupMediumArmorMaster2024, false);
        RegisterManagedTabletopFeats(true, _featGroupMediumArmorMaster2024);
    }

    private static void BuildHeavyArmorMaster2024()
    {
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatHeavyArmorMasterTitle");
        var groupDescription = Gui.Localize($"Feat/&{HeavyArmorMaster2024GroupFeatName}Description");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey(HeavyArmorMaster2024GroupFeatName),
            fallbackDescription: groupDescription);
        var reduceDamage = FeatureDefinitionReduceDamageBuilder
            .Create("ReduceDamageFeatHeavyArmorMaster2024")
            .SetGuiPresentationNoContent(true)
            .SetNotificationTag("HeavyArmorMaster2024")
            .SetAlwaysActiveReducedDamage((_, defender) =>
                defender?.RulesetCharacter?.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus) ?? 0,
                DamageTypeBludgeoning,
                DamageTypePiercing,
                DamageTypeSlashing)
            .AddCustomSubFeatures(ValidatorsCharacter.HasHeavyArmor, AttackOnlyReduceDamageMarker.Marker)
            .AddToDB();
        var featHeavyArmorMaster2024Str = BuildArmorHalfFeat(
            LegacyHeavyArmorMaster2024SettingName,
            Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(AttributeDefinitions.Strength)),
            AttributeDefinitions.Strength,
            baseDescription,
            AttributeModifierCreed_Of_Einar,
            HeavyArmorMaster2024Family,
            reduceDamage,
            HeavyArmorCategory,
            clearAbilityPrerequisite: true);
        var featHeavyArmorMaster2024Con = BuildArmorHalfFeat(
            "FeatHeavyArmorMaster2024Con",
            Gui.Format(
                "Feat/&GeneralFeat2024VariantTitle",
                groupTitle,
                GetAttributeTitle(AttributeDefinitions.Constitution)),
            AttributeDefinitions.Constitution,
            baseDescription,
            AttributeModifierCreed_Of_Arun,
            HeavyArmorMaster2024Family,
            reduceDamage,
            HeavyArmorCategory,
            clearAbilityPrerequisite: true);

        _featGroupHeavyArmorMaster2024 = BuildManagedGroup(
            HeavyArmorMaster2024GroupFeatName,
            HeavyArmorMaster2024Family,
            groupTitle,
            groupDescription,
            [featHeavyArmorMaster2024Str, featHeavyArmorMaster2024Con]);

        SetFeatVisibility(_featGroupHeavyArmorMaster2024, false);
        RegisterManagedTabletopFeats(true, _featGroupHeavyArmorMaster2024);
    }

    private static void BuildPoisoner2024()
    {
        var feat = GetDefinition<FeatDefinition>("FeatPoisoner");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupPoisoner2024Title", feat);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupPoisoner2024"),
            feat,
            Gui.Localize("Feat/&FeatGroupPoisoner2024Description"));

        _featGroupPoisoner2024 = GroupFeats.MakeGroup(
            "FeatGroupPoisoner2024",
            Poisoner2024Family,
            Build2024HalfFeatVariant(
                feat,
                "FeatPoisoner2024Dex",
                AttributeModifierCreed_Of_Misaye,
                Poisoner2024Family,
                AttributeDefinitions.Dexterity,
                groupTitle,
                baseDescription,
                prerequisiteValue: null,
                clearAbilityPrerequisite: true),
            Build2024HalfFeatVariant(
                feat,
                "FeatPoisoner2024Int",
                AttributeModifierCreed_Of_Pakri,
                Poisoner2024Family,
                AttributeDefinitions.Intelligence,
                groupTitle,
                baseDescription,
                prerequisiteValue: null,
                clearAbilityPrerequisite: true));
        SetFeatVisibility(_featGroupPoisoner2024, false);
        RegisterManagedTabletopFeats(true, _featGroupPoisoner2024);
    }

    private static void BuildPolearmMaster2024()
    {
        var feat = GetDefinition<FeatDefinition>("FeatPolearmExpert");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupPolearmMaster2024Title", feat);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupPolearmMaster2024"),
            feat,
            Gui.Localize("Feat/&FeatGroupPolearmMaster2024Description"));
        var featPolearmMaster2024Str = BuildAbilityPrerequisiteHalfFeatVariant(
            feat,
            "FeatPolearmMaster2024Str",
            AttributeModifierCreed_Of_Einar,
            PolearmMaster2024Family,
            AttributeDefinitions.Strength,
            groupTitle,
            baseDescription);
        var featPolearmMaster2024Dex = BuildAbilityPrerequisiteHalfFeatVariant(
            feat,
            "FeatPolearmMaster2024Dex",
            AttributeModifierCreed_Of_Misaye,
            PolearmMaster2024Family,
            AttributeDefinitions.Dexterity,
            groupTitle,
            baseDescription);

        _featGroupPolearmMaster2024 = GroupFeats.MakeGroup(
            "FeatGroupPolearmMaster2024",
            PolearmMaster2024Family,
            featPolearmMaster2024Str,
            featPolearmMaster2024Dex);
        SetFeatVisibility(_featGroupPolearmMaster2024, false);
        RegisterManagedTabletopFeats(true, _featGroupPolearmMaster2024);
    }

    private static void BuildSentinel2024()
    {
        var feat = GetDefinition<FeatDefinition>("FeatSentinel");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupSentinel2024Title", feat);
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupSentinel2024"),
            feat,
            Gui.Localize("Feat/&FeatGroupSentinel2024Description"));
        var featSentinel2024Str = BuildAbilityPrerequisiteHalfFeatVariant(
            feat,
            "FeatSentinel2024Str",
            AttributeModifierCreed_Of_Einar,
            Sentinel2024Family,
            AttributeDefinitions.Strength,
            groupTitle,
            baseDescription);
        var featSentinel2024Dex = BuildAbilityPrerequisiteHalfFeatVariant(
            feat,
            "FeatSentinel2024Dex",
            AttributeModifierCreed_Of_Misaye,
            Sentinel2024Family,
            AttributeDefinitions.Dexterity,
            groupTitle,
            baseDescription);

        _featGroupSentinel2024 = GroupFeats.MakeGroup(
            "FeatGroupSentinel2024",
            Sentinel2024Family,
            featSentinel2024Str,
            featSentinel2024Dex);
        SetFeatVisibility(_featGroupSentinel2024, false);
        RegisterManagedTabletopFeats(true, _featGroupSentinel2024);
    }

    private static void BuildResilient2024()
    {
        var featResilient2024Str =
            BuildResilientVariant(Creed_Of_Einar, "FeatResilient2024Str", AttributeDefinitions.Strength);
        var featResilient2024Dex =
            BuildResilientVariant(Creed_Of_Misaye, "FeatResilient2024Dex", AttributeDefinitions.Dexterity);
        var featResilient2024Con =
            BuildResilientVariant(Creed_Of_Arun, "FeatResilient2024Con", AttributeDefinitions.Constitution);
        var featResilient2024Int =
            BuildResilientVariant(Creed_Of_Pakri, "FeatResilient2024Int", AttributeDefinitions.Intelligence);
        var featResilient2024Wis =
            BuildResilientVariant(Creed_Of_Maraike, "FeatResilient2024Wis", AttributeDefinitions.Wisdom);
        var featResilient2024Cha =
            BuildResilientVariant(Creed_Of_Solasta, "FeatResilient2024Cha", AttributeDefinitions.Charisma);

        _featGroupResilient2024 = GroupFeats.MakeGroup(
            "FeatGroupResilient2024",
            Resilient2024Family,
            featResilient2024Str,
            featResilient2024Dex,
            featResilient2024Con,
            featResilient2024Int,
            featResilient2024Wis,
            featResilient2024Cha);
        SetFeatVisibility(_featGroupResilient2024, false);
        RegisterManagedTabletopFeats(true, _featGroupResilient2024);
    }

    private static void BuildHeavilyArmored2024()
    {
        var groupTitle = Get2024HalfFeatGroupTitle($"Feat/&{HeavilyArmored2024GroupFeatName}Title");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey(HeavilyArmored2024GroupFeatName),
            fallbackDescription: Gui.Localize($"Feat/&{HeavilyArmored2024GroupFeatName}Description"));
        var proficiency = FeatureDefinitionProficiencyBuilder
            .Create("ProficiencyFeatHeavilyArmored2024")
            .SetGuiPresentationNoContent(true)
            .SetProficiencies(ProficiencyType.Armor, HeavyArmorCategory)
            .AddToDB();

        var featHeavilyArmored2024Str = BuildArmorHalfFeat(
            "FeatHeavilyArmored2024Str",
            Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(AttributeDefinitions.Strength)),
            AttributeDefinitions.Strength,
            baseDescription,
            AttributeModifierCreed_Of_Einar,
            HeavilyArmored2024Family,
            proficiency,
            MediumArmorCategory,
            clearAbilityPrerequisite: true,
            hideFromFeats: true);
        var featHeavilyArmored2024Con = BuildArmorHalfFeat(
            "FeatHeavilyArmored2024Con",
            Gui.Format(
                "Feat/&GeneralFeat2024VariantTitle",
                groupTitle,
                GetAttributeTitle(AttributeDefinitions.Constitution)),
            AttributeDefinitions.Constitution,
            baseDescription,
            AttributeModifierCreed_Of_Arun,
            HeavilyArmored2024Family,
            proficiency,
            MediumArmorCategory,
            clearAbilityPrerequisite: true,
            hideFromFeats: true);

        _featGroupHeavilyArmored2024 = GroupFeats.MakeGroup(
            HeavilyArmored2024GroupFeatName,
            HeavilyArmored2024Family,
            featHeavilyArmored2024Str,
            featHeavilyArmored2024Con);
        SetFeatVisibility(_featGroupHeavilyArmored2024, false);
        RegisterManagedTabletopFeats(true, _featGroupHeavilyArmored2024);
    }

    private static void BuildFeyTeleport2024()
    {
        // Repo-wide stable race validator also includes Half-Elf variants.
        var validator = ValidatorsFeat.IsElfOfHalfElf;
        var legacyFeatInt = GetDefinition<FeatDefinition>("FeatFeyTeleportationInt");
        var legacyFeatCha = GetDefinition<FeatDefinition>("FeatFeyTeleportationCha");
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupFeyTeleportTitle");
        var groupDescription = Gui.Localize("Feat/&FeatGroupFeyTeleport2024Description");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupFeyTeleport2024"),
            fallbackDescription: groupDescription);
        var featFeyTeleportation2024Int = BuildValidatedCopied2024HalfFeatVariant(
            legacyFeatInt,
            "FeatFeyTeleportation2024Int",
            FeyTeleport2024Family,
            AttributeDefinitions.Intelligence,
            groupTitle,
            baseDescription,
            validator);
        var featFeyTeleportation2024Cha = BuildValidatedCopied2024HalfFeatVariant(
            legacyFeatCha,
            "FeatFeyTeleportation2024Cha",
            FeyTeleport2024Family,
            AttributeDefinitions.Charisma,
            groupTitle,
            baseDescription,
            validator);

        _featGroupFeyTeleport2024 = BuildManagedGroupWithValidator(
            "FeatGroupFeyTeleport2024",
            FeyTeleport2024Family,
            groupTitle,
            groupDescription,
            validator,
            [featFeyTeleportation2024Int, featFeyTeleportation2024Cha]);

        SetFeatVisibility(_featGroupFeyTeleport2024, false);
        RegisterManagedTabletopFeats(true, _featGroupFeyTeleport2024);
    }

    private static void BuildFeyTouched2024()
    {
        var choiceSpellList = SpellListDefinitionBuilder
            .Create(SpellListFeatFeyTouched2024ChoiceName)
            .SetGuiPresentationNoContent(true)
            .ClearSpells()
            .FinalizeSpells(true, 1)
            .AddToDB();
        var mistyStepSpellList = SpellListDefinitionBuilder
            .Create(SpellListFeatFeyTouched2024MistyStepName)
            .SetGuiPresentationNoContent(true)
            .ClearSpells()
            .SetSpellsAtLevel(2, MistyStep)
            .FinalizeSpells(false, 2)
            .AddToDB();
        var groupTitle = Gui.Localize("Feat/&FeatGroupFeyTouched2024Title");
        var groupDescription = Gui.Localize("Feat/&FeatGroupFeyTouched2024Description");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupFeyTouched2024"),
            fallbackDescription: groupDescription);
        var feats = new[]
        {
            BuildFeyTouched2024Variant(
                AttributeDefinitions.Intelligence,
                AttributeModifierCreed_Of_Pakri,
                groupTitle,
                baseDescription,
                choiceSpellList,
                mistyStepSpellList),
            BuildFeyTouched2024Variant(
                AttributeDefinitions.Wisdom,
                AttributeModifierCreed_Of_Maraike,
                groupTitle,
                baseDescription,
                choiceSpellList,
                mistyStepSpellList),
            BuildFeyTouched2024Variant(
                AttributeDefinitions.Charisma,
                AttributeModifierCreed_Of_Solasta,
                groupTitle,
                baseDescription,
                choiceSpellList,
                mistyStepSpellList)
        };

        RefreshFeyTouched2024SpellLists();

        _featGroupFeyTouched2024 = BuildManagedGroup(
            "FeatGroupFeyTouched2024",
            FeyTouched2024Family,
            groupTitle,
            groupDescription,
            feats);

        SetFeatVisibility(_featGroupFeyTouched2024, false);
        RegisterManagedTabletopFeats(true, _featGroupFeyTouched2024);
    }

    private static void BuildShadowTouched2024()
    {
        var choiceSpellList = SpellListDefinitionBuilder
            .Create(SpellListFeatShadowTouched2024ChoiceName)
            .SetGuiPresentationNoContent(true)
            .ClearSpells()
            // CharacterStageSpellSelectionPanel indexes spell lists by HasCantrips.
            // Keep this list cantrip-aware so level 1 spells stored at SpellsByLevel[1] stay visible.
            .FinalizeSpells(true, 1)
            .AddToDB();
        var invisibilitySpellList = SpellListDefinitionBuilder
            .Create(SpellListFeatShadowTouched2024InvisibilityName)
            .SetGuiPresentationNoContent(true)
            .ClearSpells()
            .SetSpellsAtLevel(2, Invisibility)
            .FinalizeSpells(false, 2)
            .AddToDB();
        var groupTitle = Gui.Localize("Feat/&FeatGroupShadowTouchedTitle");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupShadowTouched2024"),
            fallbackDescription: Gui.Localize("Feat/&FeatGroupShadowTouchedDescription"));
        var feats = new[]
        {
            BuildShadowTouched2024Variant(
                AttributeDefinitions.Intelligence,
                AttributeModifierCreed_Of_Pakri,
                groupTitle,
                baseDescription,
                choiceSpellList,
                invisibilitySpellList),
            BuildShadowTouched2024Variant(
                AttributeDefinitions.Wisdom,
                AttributeModifierCreed_Of_Maraike,
                groupTitle,
                baseDescription,
                choiceSpellList,
                invisibilitySpellList),
            BuildShadowTouched2024Variant(
                AttributeDefinitions.Charisma,
                AttributeModifierCreed_Of_Solasta,
                groupTitle,
                baseDescription,
                choiceSpellList,
                invisibilitySpellList)
        };

        RefreshShadowTouched2024SpellLists();

        _featGroupShadowTouched2024 = BuildManagedGroup(
            "FeatGroupShadowTouched2024",
            ShadowTouched2024Family,
            groupTitle,
            baseDescription,
            feats);

        SetFeatVisibility(_featGroupShadowTouched2024, false);
        RegisterManagedTabletopFeats(true, _featGroupShadowTouched2024);
    }

    private static void BuildSpellSniper2024()
    {
        var attributes = new[]
        {
            (AttributeDefinitions.Intelligence, AttributeModifierCreed_Of_Pakri),
            (AttributeDefinitions.Wisdom, AttributeModifierCreed_Of_Maraike),
            (AttributeDefinitions.Charisma, AttributeModifierCreed_Of_Solasta)
        };
        var spellList = SpellListDefinitionBuilder
            .Create(SpellListFeatSpellSniper2024Name)
            .SetGuiPresentationNoContent(true)
            .ClearSpells()
            .FinalizeSpells()
            .AddToDB();
        RefreshSpellSniper2024SpellList();
        var groupTitle = Get2024HalfFeatGroupTitle("Feat/&FeatGroupSpellSniper2024Title");
        var groupDescription = Gui.Localize("Feat/&FeatGroupSpellSniper2024Description");
        var baseDescription = Get2024HalfFeatBaseDescription(
            Get2024HalfFeatBaseDescriptionKey("FeatGroupSpellSniper2024"),
            fallbackDescription: groupDescription);
        var feats = attributes.Select(x => BuildSpellSniper2024Variant(x.Item1, x.Item2, groupTitle, baseDescription, spellList))
            .ToArray();

        _featGroupSpellSniper2024 = BuildManagedGroup(
            "FeatGroupSpellSniper2024",
            SpellSniper2024Family,
            groupTitle,
            groupDescription,
            feats);

        _featGroupSpellSniper2024.mustCastSpellsPrerequisite = true;

        SetFeatVisibility(_featGroupSpellSniper2024, false);
        RegisterManagedTabletopFeats(true, _featGroupSpellSniper2024);
    }

    internal static void RefreshFeatSpellSelectionLists2024()
    {
        RefreshFeyTouched2024SpellLists();
        RefreshSpellSniper2024SpellList();
        RefreshShadowTouched2024SpellLists();
    }

    internal static void RefreshSpellSniper2024SpellList()
    {
        if (!TryGetDefinition<SpellListDefinition>(SpellListFeatSpellSniper2024Name, out var spellList))
        {
            return;
        }

        var castSpellDefinitions = new[]
        {
            GetDefinition<FeatureDefinitionCastSpell>("CastSpellBard"),
            GetDefinition<FeatureDefinitionCastSpell>("CastSpellCleric"),
            GetDefinition<FeatureDefinitionCastSpell>("CastSpellDruid"),
            GetDefinition<FeatureDefinitionCastSpell>("CastSpellSorcerer"),
            GetDefinition<FeatureDefinitionCastSpell>("CastSpellWarlock"),
            GetDefinition<FeatureDefinitionCastSpell>("CastSpellWizard")
        };
        var cantrips = castSpellDefinitions
            .Where(x => x?.SpellListDefinition != null)
            .SelectMany(x => x.SpellListDefinition.SpellsByLevel)
            .SelectMany(x => x.Spells)
            .Where(IsSpellSniper2024SelectableCantrip)
            .Distinct()
            .OrderBy(x => x.FormatTitle())
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        UpdateSpellListSpells(spellList, 0, cantrips, hasCantrips: true, maxSpellLevel: 0);
    }

    private static void RefreshShadowTouched2024SpellLists()
    {
        RefreshTouched2024SpellLists(
            SpellListFeatShadowTouched2024ChoiceName,
            SpellListFeatShadowTouched2024InvisibilityName,
            Invisibility,
            IsShadowTouched2024SelectableSpell);
    }

    private static void RefreshFeyTouched2024SpellLists()
    {
        RefreshTouched2024SpellLists(
            SpellListFeatFeyTouched2024ChoiceName,
            SpellListFeatFeyTouched2024MistyStepName,
            MistyStep,
            IsFeyTouched2024SelectableSpell);
    }

    private static void RefreshTouched2024SpellLists(
        string choiceSpellListName,
        string fixedSpellListName,
        SpellDefinition fixedSpell,
        Func<SpellDefinition, bool> predicate)
    {
        if (TryGetDefinition<SpellListDefinition>(choiceSpellListName, out var choiceSpellList))
        {
            // CharacterStageSpellSelectionPanel indexes spell lists by HasCantrips.
            // Keep this list cantrip-aware so level 1 spells stored at SpellsByLevel[1] stay visible.
            UpdateSpellListSpells(
                choiceSpellList,
                1,
                CollectTouched2024SelectableSpells(predicate),
                hasCantrips: true,
                maxSpellLevel: 1);
        }

        if (TryGetDefinition<SpellListDefinition>(fixedSpellListName, out var fixedSpellList))
        {
            UpdateSpellListSpells(
                fixedSpellList,
                2,
                [fixedSpell],
                hasCantrips: false,
                maxSpellLevel: 2);
        }
    }

    private static void UpdateSpellListSpells(
        SpellListDefinition spellList,
        int spellLevel,
        IEnumerable<SpellDefinition> spells,
        bool hasCantrips,
        int maxSpellLevel)
    {
        if (!spellList)
        {
            return;
        }

        EnsureSpellListLevelsConfigured(spellList);

        foreach (var spellsByLevel in spellList.SpellsByLevel)
        {
            spellsByLevel.Spells.Clear();
        }

        spellList.SpellsByLevel[spellLevel].Spells.AddRange(
            spells
                .Where(x => x is { Implemented: true })
                .Distinct());
        spellList.hasCantrips = hasCantrips;
        spellList.maxSpellLevel = maxSpellLevel;
    }

    private static void EnsureSpellListLevelsConfigured(SpellListDefinition spellList)
    {
        if (!spellList)
        {
            return;
        }

        for (var level = 0; level < 10; level++)
        {
            if (spellList.SpellsByLevel.Count < level + 1)
            {
                spellList.SpellsByLevel.Add(new SpellListDefinition.SpellsByLevelDuplet
                {
                    Level = level,
                    Spells = []
                });
            }

            var spellsByLevel = spellList.SpellsByLevel[level];

            spellsByLevel.Level = level;
            spellsByLevel.Spells ??= [];
        }
    }

    private static bool IsShadowTouched2024SelectableSpell(SpellDefinition spell)
    {
        return IsSelectableFeatSpell2024(spell) &&
               spell.SpellLevel == 1 &&
               spell.SchoolOfMagic is SchoolIllusion or SchoolNecromancy;
    }

    private static bool IsFeyTouched2024SelectableSpell(SpellDefinition spell)
    {
        return IsSelectableFeatSpell2024(spell) &&
               spell.SpellLevel == 1 &&
               (spell.SchoolOfMagic is SchoolDivination ||
                spell.SchoolOfMagic == SchoolOfMagicDefinitions.SchoolEnchantment.Name);
    }

    private static bool IsSpellSniper2024SelectableCantrip(SpellDefinition spell)
    {
        return IsSelectableFeatSpell2024(spell) &&
               spell.SpellLevel == 0 &&
               (spell.EffectDescription?.RangeType is RangeType.MeleeHit or RangeType.RangeHit ||
                spell.HasSubFeatureOfType<AttackAfterMagicEffect>());
    }

    private static bool IsSelectableFeatSpell2024(SpellDefinition spell)
    {
        return spell is { Implemented: true, GuiPresentation.hidden: false } &&
               !SpellsContext.SpellsChildMaster.ContainsKey(spell);
    }

    internal static bool UsesDedicatedTouchedSpellSelectionList2024(
        FeatureDefinitionCastSpell spellFeature,
        SpellListDefinition spellListDefinition,
        string spellTag)
    {
        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            spellFeature == null ||
            spellListDefinition == null ||
            string.IsNullOrEmpty(spellTag))
        {
            return false;
        }

        return IsDedicatedTouchedSpellSelectionList2024(
                   spellFeature,
                   spellListDefinition,
                   spellTag,
                   SpellListFeatShadowTouched2024ChoiceName,
                   ShadowTouched2024ChoiceTag,
                   "CastSpellFeatShadowTouched2024") ||
               IsDedicatedTouchedSpellSelectionList2024(
                   spellFeature,
                   spellListDefinition,
                   spellTag,
                   SpellListFeatFeyTouched2024ChoiceName,
                   FeyTouched2024ChoiceTag,
                   "CastSpellFeatFeyTouched2024");
    }

    private static bool IsDedicatedTouchedSpellSelectionList2024(
        FeatureDefinitionCastSpell spellFeature,
        SpellListDefinition spellListDefinition,
        string spellTag,
        string spellListName,
        string choiceTag,
        string castSpellPrefix)
    {
        return spellListDefinition.Name == spellListName &&
               spellTag.EndsWith(choiceTag, StringComparison.Ordinal) &&
               spellFeature.Name.StartsWith(castSpellPrefix, StringComparison.Ordinal) &&
               spellFeature.Name.EndsWith("Choice", StringComparison.Ordinal);
    }

    internal static void AddTabletop2024FeatAutoPreparedSpells(
        RulesetCharacter character,
        RulesetSpellRepertoire spellRepertoire)
    {
        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            character is not RulesetCharacterHero hero ||
            !(spellRepertoire.SpellCastingClass || spellRepertoire.SpellCastingSubclass))
        {
            return;
        }

        var maxSpellLevel = spellRepertoire.MaxSpellLevelOfSpellCastingLevel;

        if (maxSpellLevel <= 0)
        {
            return;
        }

        foreach (var spell in EnumerateSlotCastableTabletop2024FeatSpells(hero)
                     .Where(spell => spell is { Implemented: true, GuiPresentation.hidden: false })
                     .Where(spell => !SpellsContext.SpellsChildMaster.ContainsKey(spell))
                     .Where(spell => spell.SpellLevel > 0 && spell.SpellLevel <= maxSpellLevel)
                     .Distinct())
        {
            if (!spellRepertoire.AutoPreparedSpells.Contains(spell))
            {
                spellRepertoire.AutoPreparedSpells.Add(spell);
            }
        }
    }

    private static IEnumerable<SpellDefinition> EnumerateSlotCastableTabletop2024FeatSpells(RulesetCharacterHero hero)
    {
        foreach (var repertoire in hero.SpellRepertoires)
        {
            var spellCastingFeature = repertoire.SpellCastingFeature;

            if (!spellCastingFeature)
            {
                continue;
            }

            var spellTag = spellCastingFeature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();

            if (!IsSlotCastableTabletop2024FeatSpellTag(spellTag, spellCastingFeature))
            {
                continue;
            }

            foreach (var spell in repertoire.KnownSpells)
            {
                yield return spell;
            }

            if (!(spellTag.ForceFixedList || spellCastingFeature.SpellKnowledge == SpellKnowledge.FixedList) ||
                !spellCastingFeature.SpellListDefinition)
            {
                continue;
            }

            foreach (var spell in spellCastingFeature.SpellListDefinition.SpellsByLevel
                         .Where(x => x.Level > 0)
                         .SelectMany(x => x.Spells))
            {
                yield return spell;
            }
        }
    }

    private static bool IsSlotCastableTabletop2024FeatSpellTag(
        FeatHelpers.SpellTag spellTag,
        FeatureDefinitionCastSpell spellCastingFeature)
    {
        if (spellTag == null)
        {
            return false;
        }

        if (SlotCastableTabletop2024FeatSpellTags.Contains(spellTag.Name))
        {
            return true;
        }

        // Compatibility path:
        // Earlier Magic Initiate 2024 definitions reused the old Initiate tag and cast spell feature names.
        return Main.Settings.EnableTabletopFeatRules2024 &&
               spellTag.Name == OtherFeats.FeatMagicInitiateTag &&
               spellCastingFeature.Name.StartsWith("CastSpellFeatMagicInitiate", StringComparison.Ordinal);
    }

    private static void BuildMagicInitiate2024()
    {
        MagicInitiate2024ByLegacyName.Clear();

        var magicInitiateFeats = new List<FeatDefinition>();
        var castSpells = new List<(FeatureDefinitionCastSpell Feature, ClassHolder ClassHolder)>
        {
            (GetDefinition<FeatureDefinitionCastSpell>("CastSpellBard"), ClassHolder.Bard),
            (GetDefinition<FeatureDefinitionCastSpell>("CastSpellCleric"), ClassHolder.Cleric),
            (GetDefinition<FeatureDefinitionCastSpell>("CastSpellDruid"), ClassHolder.Druid),
            (GetDefinition<FeatureDefinitionCastSpell>("CastSpellSorcerer"), ClassHolder.Sorcerer),
            (GetDefinition<FeatureDefinitionCastSpell>("CastSpellWarlock"), ClassHolder.Warlock),
            (GetDefinition<FeatureDefinitionCastSpell>("CastSpellWizard"), ClassHolder.Wizard)
        };
        var groupTitle = Gui.Localize("Feat/&FeatGroupMagicInitiateTitle");
        var groupDescription = Gui.Localize("Feat/&FeatGroupMagicInitiate2024Description");

        foreach (var (castSpell, classHolder) in castSpells)
        {
            var spellList = castSpell.SpellListDefinition;
            var className = spellList.Name.Replace("SpellList", "");
            var classDefinition = GetDefinition<CharacterClassDefinition>(className);
            var classTitle = classDefinition.FormatTitle();
            var legacyFeatName = $"FeatMagicInitiate{className}";
            var featName = $"{legacyFeatName}2024";
            var description = Gui.Format("Feat/&FeatMagicInitiate2024Description", classTitle);
            var featureDefinitionCastSpell = FeatureDefinitionCastSpellBuilder
                .Create(castSpell, $"CastSpell{featName}")
                .SetGuiPresentation(
                    Gui.Localize("Feature/&CastSpellFeatMagicInitiateTitle"),
                    description)
                .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
                .SetSpellKnowledge(SpellKnowledge.Selection)
                .SetSpellReadyness(SpellReadyness.AllKnown)
                .SetSlotsRecharge(RechargeRate.LongRest)
                .SetSlotsPerLevel(SharedSpellsContext.InitiateCastingSlots)
                .SetKnownCantrips(2, 1, FeatureDefinitionCastSpellBuilder.CasterProgression.Flat)
                .SetKnownSpells(1, FeatureDefinitionCastSpellBuilder.CasterProgression.Flat)
                .SetReplacedSpells(1, 0)
                .SetUniqueLevelSlots(false)
                .SetSpellList(spellList)
                .AddCustomSubFeatures(new FeatHelpers.SpellTag(MagicInitiate2024SpellTag), classHolder)
                .AddToDB();
            var cantripPool = FeatureDefinitionPointPoolBuilder
                .Create($"PointPool{featName}Cantrip")
                .SetGuiPresentationNoContent(true)
                .SetSpellOrCantripPool(
                    HeroDefinitions.PointsPoolType.Cantrip,
                    2,
                    spellList,
                    MagicInitiate2024SpellTag)
                .AddToDB();
            var spellPool = FeatureDefinitionPointPoolBuilder
                .Create($"PointPool{featName}Spell")
                .SetGuiPresentationNoContent(true)
                .SetSpellOrCantripPool(
                    HeroDefinitions.PointsPoolType.Spell,
                    1,
                    spellList,
                    MagicInitiate2024SpellTag,
                    1,
                    1)
                .AddToDB();
            var feat = FeatDefinitionBuilder
                .Create(featName)
                .SetGuiPresentation(
                    Gui.Format("Feat/&FeatMagicInitiateTitle", classTitle),
                    description,
                    hidden: false)
                .SetFeatures(featureDefinitionCastSpell, cantripPool, spellPool)
                .SetFeatFamily(MagicInitiate2024Family)
                .AddCustomSubFeatures(FeatsContext.HideFromFeats.Marker)
                .AddToDB();

            magicInitiateFeats.Add(feat);
            MagicInitiate2024ByLegacyName[legacyFeatName] = feat;
        }

        _featGroupMagicInitiate2024 = BuildManagedGroup(
            "FeatGroupMagicInitiate2024",
            MagicInitiate2024Family,
            groupTitle,
            groupDescription,
            magicInitiateFeats);

        SetFeatVisibility(_featGroupMagicInitiate2024, false);
        RegisterManagedTabletopFeats(true, _featGroupMagicInitiate2024);
    }

    private static FeatDefinition BuildShadowTouched2024Variant(
        string attribute,
        FeatureDefinitionAttributeModifier attributeModifier,
        string groupTitle,
        string baseDescription,
        SpellListDefinition choiceSpellList,
        SpellListDefinition invisibilitySpellList)
    {
        var name = $"FeatShadowTouched2024{attribute}";
        var title = Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute));
        var description = BuildHalfFeatDescription(attribute, baseDescription);
        var fixedCastSpell = FeatureDefinitionCastSpellBuilder
            .Create($"CastSpell{name}Fixed")
            .SetGuiPresentationNoContent(true)
            .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
            .SetSpellKnowledge(SpellKnowledge.FixedList)
            .SetSpellReadyness(SpellReadyness.AllKnown)
            .SetSlotsRecharge(RechargeRate.LongRest)
            .SetSlotsPerLevel(Touched2024FixedCastingSlots)
            .SetUniqueLevelSlots(false)
            .SetSpellList(invisibilitySpellList)
            .SetSpellCastingAbility(attribute)
            .AddCustomSubFeatures(new FeatHelpers.SpellTag(ShadowTouched2024FixedTag, forceFixedList: true))
            .AddToDB();
        var choiceCastSpell = FeatureDefinitionCastSpellBuilder
            .Create($"CastSpell{name}Choice")
            .SetGuiPresentationNoContent(true)
            .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
            .SetSpellKnowledge(SpellKnowledge.Selection)
            .SetSpellReadyness(SpellReadyness.AllKnown)
            .SetSlotsRecharge(RechargeRate.LongRest)
            .SetSlotsPerLevel(SharedSpellsContext.InitiateCastingSlots)
            .SetKnownSpells(1, FeatureDefinitionCastSpellBuilder.CasterProgression.Flat)
            .SetReplacedSpells(1, 0)
            .SetUniqueLevelSlots(false)
            .SetSpellList(choiceSpellList)
            .SetSpellCastingAbility(attribute)
            .AddCustomSubFeatures(new FeatHelpers.SpellTag(ShadowTouched2024ChoiceTag))
            .AddToDB();
        var pointPool = FeatureDefinitionPointPoolBuilder
            .Create($"PointPool{name}Spell")
            .SetGuiPresentationNoContent(true)
            .SetSpellOrCantripPool(
                HeroDefinitions.PointsPoolType.Spell,
                1,
                choiceSpellList,
                ShadowTouched2024ChoiceTag,
                1,
                1)
            .AddToDB();
        var feat = FeatDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(title, description, hidden: false)
            .SetFeatures(attributeModifier, fixedCastSpell, choiceCastSpell, pointPool)
            .SetFeatFamily(ShadowTouched2024Family)
            .AddCustomSubFeatures(FeatsContext.HideFromFeats.Marker)
            .AddToDB();

        feat.mustCastSpellsPrerequisite = false;
        ClearMinimalAbilityPrerequisite(feat);

        return feat;
    }

    private static void LoadIndependentTabletopFeatCatalog()
    {
        IndependentTabletopFeatByCanonicalName.Clear();
        CanonicalTabletopFeatNameByDefinitionName.Clear();
        ManagedTabletopFeatKinds.Clear();
        ManagedTabletopParentNameByDefinitionName.Clear();
        ManagedTabletopChildNamesByParentName.Clear();
        ManagedTabletopContainerNamesByCanonicalName.Clear();
        ManagedSelectableRootNamesByContainerName.Clear();
        ManagedTabletopContainerGroupNames.Clear();
        ManagedTabletopFeatNames.Clear();
        SelectableManagedTabletopRootNames.Clear();
        ManagedStandaloneTabletopFeats.Clear();
        TabletopFeat2024Names.Clear();

        foreach (var groupName in TabletopFeatHelperGroupNames)
        {
            ManagedTabletopFeatKinds[groupName] = TabletopFeatCatalogKind.Helper;
        }

        RegisterExplicitManagedCatalogEntries();
        RegisterExplicitIndependentLegacyRoots();
        PostConfigureManagedIndependentHalfFeats();
    }

    private static void RegisterExplicitManagedCatalogEntries()
    {
        RegisterManagedCatalogEntry("FeatAlert", _featAlert2024, true, true);
        RegisterManagedCatalogEntry("FeatHealer", _featHealer2024, true, true);
        RegisterManagedCatalogEntry("FeatLucky", _featLucky2024, true, true);
        RegisterManagedCatalogEntry("FeatSavageAttack", _featSavageAttack2024, true, true);
        RegisterManagedCatalogEntry("FeatMobile", _featGroupSpeedy, true);
        RegisterManagedCatalogEntry(OtherFeats.FeatWarCaster, _featGroupWarCaster2024, true);
        RegisterManagedCatalogEntry("FeatRangedExpert", _featCrossbowExpert2024, true, true);
        RegisterManagedCatalogEntry("FeatShieldTechniques", _featShieldMaster2024, true, true);
        RegisterManagedCatalogEntry("FeatDualWeaponDefense", _featGroupDualWielder2024, true);
        RegisterManagedCatalogEntry("FeatCharger", _featGroupCharger2024, true);
        RegisterManagedCatalogEntry("FeatDefensiveDuelist", _featDefensiveDuelist2024, true, true);
        RegisterManagedCatalogEntry("FeatGrapplerStr", _featGroupGrappler2024, true);
        RegisterManagedCanonicalAlias("FeatGrapplerStr", _featGrappler2024Str);
        RegisterManagedCanonicalAlias("FeatGrapplerStr", _featGrappler2024Dex);
        RegisterManagedCatalogEntry("FeatGroupElementalAdept", _featGroupElementalAdept2024, true);
        RegisterManagedCatalogEntry("FeatInspiringLeader", _featGroupInspiringLeader2024, true);
        RegisterManagedCatalogEntry("FeatLightlyArmored", _featGroupLightlyArmored2024, true);
        RegisterManagedCatalogEntry("FeatMageSlayer", _featGroupMageSlayer2024, true);
        RegisterManagedCatalogEntry("FeatModeratelyArmored", _featGroupModeratelyArmored2024, true);
        RegisterManagedCatalogEntry("FeatMediumArmorMaster", _featGroupMediumArmorMaster2024, true);
        RegisterManagedCatalogEntry("FeatHeavyArmorMaster", _featGroupHeavyArmorMaster2024, true);
        RegisterManagedCatalogEntry("FeatPoisoner", _featGroupPoisoner2024, true);
        RegisterManagedCatalogEntry("FeatPolearmExpert", _featGroupPolearmMaster2024, true);
        RegisterManagedCatalogEntry("FeatResilient", _featGroupResilient2024, true);
        RegisterManagedCatalogEntry("FeatSentinel", _featGroupSentinel2024, true);
        RegisterManagedCatalogEntry("FeatHeavilyArmored", _featGroupHeavilyArmored2024, true);
        RegisterManagedCatalogEntry("FeatGroupFeyTeleport", _featGroupFeyTeleport2024, true);
        RegisterManagedCatalogEntry("FeatGroupFeyTouched2024", _featGroupFeyTouched2024, true);
        RegisterManagedCatalogPair(
            "FeatGroupMagicInitiate",
            _featGroupMagicInitiate2024,
            TabletopFeatCatalogKind.GroupedRoot,
            null);
        foreach (var magicInitiate2024 in MagicInitiate2024ByLegacyName)
        {
            RegisterManagedCatalogPair(
                magicInitiate2024.Key,
                magicInitiate2024.Value,
                TabletopFeatCatalogKind.GroupedChild,
                _featGroupMagicInitiate2024);
        }
        RegisterManagedCatalogEntry("FeatGroupShadowTouched", _featGroupShadowTouched2024, true);
        RegisterManagedCatalogEntry("FeatGroupSpellSniper", _featGroupSpellSniper2024, true);
        RegisterManagedCatalogEntry(LegacyGreatWeaponMasterFeatName, _featGreatWeaponMaster2024, true, true);
        RegisterManagedCatalogEntry(LegacySharpshooterFeatName, _featSharpshooter2024, true, true);

        foreach (var dedicatedHalfFeat in DedicatedStandaloneHalfFeat2024ByCanonicalName)
        {
            RegisterManagedCatalogEntry(
                dedicatedHalfFeat.Key,
                dedicatedHalfFeat.Value,
                selectableRoot: true,
                standalone: dedicatedHalfFeat.Value.GetFirstSubFeatureOfType<IGroupedFeat>() == null);

            if (dedicatedHalfFeat.Value.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat)
            {
                continue;
            }

            foreach (var childFeat in groupedFeat.GetSubFeats(true))
            {
                RegisterManagedCanonicalAlias(dedicatedHalfFeat.Key, childFeat);
            }
        }
    }

    private static SpellDefinition[] CollectTouched2024SelectableSpells(Func<SpellDefinition, bool> predicate)
    {
        return SpellsContext.SpellLists.Values
            .Where(x => x != null)
            .SelectMany(x => x.SpellsByLevel)
            .SelectMany(x => x.Spells)
            .Where(predicate)
            .Distinct()
            .OrderBy(x => x.FormatTitle())
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void RegisterExplicitIndependentLegacyRoots()
    {
        foreach (var featName in ExplicitIndependentLegacyStandaloneRootNames)
        {
            if (Dedicated2024StandaloneOverrideCanonicalNames.Contains(featName))
            {
                continue;
            }

            BuildIndependentLegacyRoot(featName, standalone: true);
        }

        foreach (var featName in ExplicitIndependentLegacyGroupedRootNames)
        {
            BuildIndependentLegacyRoot(featName, standalone: false);
        }
    }

    private static FeatDefinition BuildIndependentLegacyRoot(string canonicalName, bool standalone)
    {
        if (string.IsNullOrEmpty(canonicalName) ||
            IsExcludedManagedTabletopCanonicalName(canonicalName) ||
            !TryGetDefinition<FeatDefinition>(canonicalName, out var legacyDefinition))
        {
            return null;
        }

        if (standalone || legacyDefinition.GetFirstSubFeatureOfType<IGroupedFeat>() == null)
        {
            var independentDefinition = BuildExplicitIndependentLeafDefinition(
                legacyDefinition,
                legacyDefinition,
                hideFromFeats: false,
                hidden: true);

            RegisterManagedCatalogPair(
                canonicalName,
                independentDefinition,
                TabletopFeatCatalogKind.Standalone,
                null);

            return independentDefinition;
        }

        return BuildIndependentLegacyGroupedDefinition(
            legacyDefinition,
            canonicalName,
            TabletopFeatCatalogKind.GroupedRoot,
            null);
    }

    private static FeatDefinition BuildIndependentLegacyGroupedDefinition(
        FeatDefinition legacyDefinition,
        string canonicalName,
        TabletopFeatCatalogKind kind,
        FeatDefinition parentDefinition)
    {
        if (legacyDefinition?.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat)
        {
            return null;
        }

        var rootName = BuildIndependentTabletopName(legacyDefinition.Name);
        var created = !TryGetDefinition<FeatDefinition>(rootName, out var independentRoot);

        independentRoot ??= BuildManagedGroup(
            rootName,
            legacyDefinition.HasFamilyTag ? legacyDefinition.FamilyTag : null,
            legacyDefinition.GuiPresentation?.Title,
            legacyDefinition.GuiPresentation?.Description,
            []);

        var childDefinitions = groupedFeat.GetSubFeats(true)
            .Select(subFeat => BuildIndependentLegacyGroupedChildDefinition(subFeat, legacyDefinition, independentRoot))
            .Where(definition => definition != null)
            .ToArray();

        independentRoot.SetSubFeatureOfType<GroupedFeat>(new GroupedFeat(childDefinitions));

        if (created)
        {
            CopyFeatCustomSubFeatures(legacyDefinition, independentRoot, false);
            MergeFeatPrerequisites(legacyDefinition, independentRoot, false);
        }

        RegisterManagedCatalogPair(
            canonicalName,
            independentRoot,
            kind,
            parentDefinition);

        return independentRoot;
    }

    private static FeatDefinition BuildIndependentLegacyGroupedChildDefinition(
        FeatDefinition legacyDefinition,
        FeatDefinition inheritedPrerequisiteDefinition,
        FeatDefinition parentDefinition)
    {
        if (legacyDefinition == null ||
            IsExcludedManagedTabletopCanonicalName(legacyDefinition.Name) ||
            TabletopFeatHelperGroupNames.Contains(legacyDefinition.Name))
        {
            return null;
        }

        if (legacyDefinition.GetFirstSubFeatureOfType<IGroupedFeat>() != null)
        {
            return BuildIndependentLegacyGroupedDefinition(
                legacyDefinition,
                legacyDefinition.Name,
                TabletopFeatCatalogKind.GroupedChild,
                parentDefinition);
        }

        var independentChild = BuildExplicitIndependentLeafDefinition(
            legacyDefinition,
            inheritedPrerequisiteDefinition,
            hideFromFeats: true,
            hidden: false);

        RegisterManagedCatalogPair(
            legacyDefinition.Name,
            independentChild,
            TabletopFeatCatalogKind.GroupedChild,
            parentDefinition);

        return independentChild;
    }

    private static FeatDefinition BuildExplicitIndependentLeafDefinition(
        FeatDefinition legacyDefinition,
        FeatDefinition inheritedPrerequisiteDefinition,
        bool hideFromFeats,
        bool hidden)
    {
        var name = BuildIndependentTabletopName(legacyDefinition.Name);

        return TryGetDefinition<FeatDefinition>(name, out var existingDefinition)
            ? existingDefinition
            : BuildExplicitIndependentFeat(
                legacyDefinition,
                name,
                family: legacyDefinition.HasFamilyTag ? legacyDefinition.FamilyTag : null,
                hideFromFeats: hideFromFeats,
                hidden: hidden,
                inheritedPrerequisiteDefinitions: inheritedPrerequisiteDefinition == null ||
                                                 inheritedPrerequisiteDefinition == legacyDefinition
                    ? null
                    : [inheritedPrerequisiteDefinition]);
    }

    private static void RegisterManagedCatalogPair(
        string canonicalName,
        FeatDefinition featDefinition,
        TabletopFeatCatalogKind kind,
        FeatDefinition parentDefinition)
    {
        if (featDefinition == null || string.IsNullOrEmpty(canonicalName))
        {
            return;
        }

        RegisterManagedTabletopFeats(true, featDefinition);
        IndependentTabletopFeatByCanonicalName[canonicalName] = featDefinition;
        CanonicalTabletopFeatNameByDefinitionName[featDefinition.Name] = canonicalName;
        ManagedTabletopFeatKinds[featDefinition.Name] = kind;

        if (parentDefinition == null)
        {
            ClearManagedTabletopParent(featDefinition.Name);
        }
        else
        {
            TrySetManagedTabletopParent(featDefinition, parentDefinition);
        }

        if (kind is TabletopFeatCatalogKind.GroupedRoot or TabletopFeatCatalogKind.Standalone)
        {
            SelectableManagedTabletopRootNames.Add(featDefinition.Name);
        }

        if (kind == TabletopFeatCatalogKind.Standalone)
        {
            ManagedStandaloneTabletopFeats.TryAdd(featDefinition);
        }
    }

    private static void RegisterManagedCatalogEntry(
        string canonicalName,
        FeatDefinition featDefinition,
        bool selectableRoot,
        bool standalone = false)
    {
        if (featDefinition == null || string.IsNullOrEmpty(canonicalName))
        {
            return;
        }

        RegisterManagedCatalogTree(featDefinition);
        IndependentTabletopFeatByCanonicalName[canonicalName] = featDefinition;
        CanonicalTabletopFeatNameByDefinitionName[featDefinition.Name] = canonicalName;
        ManagedTabletopFeatKinds[featDefinition.Name] = standalone
            ? TabletopFeatCatalogKind.Standalone
            : selectableRoot
                ? TabletopFeatCatalogKind.GroupedRoot
                : TabletopFeatCatalogKind.GroupedChild;

        if (selectableRoot)
        {
            SelectableManagedTabletopRootNames.Add(featDefinition.Name);
        }

        if (standalone)
        {
            ManagedStandaloneTabletopFeats.TryAdd(featDefinition);
        }
    }

    private static void RegisterManagedCanonicalAlias(string canonicalName, FeatDefinition featDefinition)
    {
        if (featDefinition == null || string.IsNullOrEmpty(canonicalName))
        {
            return;
        }

        CanonicalTabletopFeatNameByDefinitionName[featDefinition.Name] = canonicalName;
    }

    private static void RegisterManagedCatalogTree(FeatDefinition featDefinition, FeatDefinition parentDefinition = null)
    {
        if (featDefinition == null)
        {
            return;
        }

        RegisterManagedTabletopFeats(true, featDefinition);
        ManagedTabletopFeatKinds.TryAdd(
            featDefinition.Name,
            parentDefinition == null
                ? featDefinition.GetFirstSubFeatureOfType<IGroupedFeat>() != null
                    ? TabletopFeatCatalogKind.GroupedRoot
                    : TabletopFeatCatalogKind.Standalone
                : TabletopFeatCatalogKind.GroupedChild);

        if (parentDefinition == null)
        {
            ClearManagedTabletopParent(featDefinition.Name);
        }
        else
        {
            TrySetManagedTabletopParent(featDefinition, parentDefinition);
        }

        if (featDefinition.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat)
        {
            return;
        }

        foreach (var subFeat in groupedFeat.GetSubFeats(true))
        {
            RegisterManagedCatalogTree(subFeat, featDefinition);
        }
    }

    private static string BuildIndependentTabletopName(string name)
    {
        return name.EndsWith("2024", StringComparison.Ordinal) ? name : $"{name}2024";
    }

    private static void LoadTabletopFeat2024Profiles()
    {
        var featGroupArmor = GetDefinition<FeatDefinition>("FeatGroupArmor");
        var featGroupCreed = GetDefinition<FeatDefinition>("FeatGroupCreed");
        var featGroupMediumArmor = GetDefinition<FeatDefinition>("FeatGroupMediumArmor");
        var featGroupPlaneMagic = GetDefinition<FeatDefinition>("FeatGroupPlaneMagic");
        var featGroupRaceBound = GetDefinition<FeatDefinition>("FeatGroupRaceBound");
        var featGroupTouchedMagic = GetDefinition<FeatDefinition>("FeatGroupTouchedMagic");
        var featGroupElementalAdept = GetDefinition<FeatDefinition>("FeatGroupElementalAdept");
        var featMobile = GetDefinition<FeatDefinition>("FeatMobile");
        var featCharger = GetDefinition<FeatDefinition>("FeatCharger");
        var featCleavingAttack = GetDefinition<FeatDefinition>(LegacyGreatWeaponMasterFeatName);
        var featDeadeye = GetDefinition<FeatDefinition>(LegacySharpshooterFeatName);
        var featDefensiveDuelist = GetDefinition<FeatDefinition>("FeatDefensiveDuelist");
        var featDualWeaponDefense = GetDefinition<FeatDefinition>("FeatDualWeaponDefense");
        var featGrappler = GetDefinition<FeatDefinition>("FeatGrapplerStr");
        var featInspiringLeader = GetDefinition<FeatDefinition>("FeatInspiringLeader");
        var featMageSlayer = GetDefinition<FeatDefinition>("FeatMageSlayer");
        var featMediumArmorMaster = GetDefinition<FeatDefinition>("FeatMediumArmorMaster");
        var featHeavyArmorMaster = GetDefinition<FeatDefinition>("FeatHeavyArmorMaster");
        var featPoisoner = GetDefinition<FeatDefinition>("FeatPoisoner");
        var featPolearmExpert = GetDefinition<FeatDefinition>("FeatPolearmExpert");
        var featRangedExpert = GetDefinition<FeatDefinition>("FeatRangedExpert");
        var featSentinel = GetDefinition<FeatDefinition>("FeatSentinel");
        var featShieldTechniques = GetDefinition<FeatDefinition>("FeatShieldTechniques");
        var featGroupFeyTeleport = GetDefinition<FeatDefinition>("FeatGroupFeyTeleport");
        var featGroupSpellSniper = GetDefinition<FeatDefinition>("FeatGroupSpellSniper");
        var featWarCaster = GetDefinition<FeatDefinition>(OtherFeats.FeatWarCaster);

        TabletopFeat2024Profiles.Clear();
        TabletopFeat2024Profiles.AddRange(
        [
            new TabletopFeat2024Profile(
                _featAlert2024,
                [GetDefinition<FeatDefinition>("FeatAlert")],
                [GroupFeats.FeatGroupOrigin, GroupFeats.FeatGroupAgilityCombat]),
            new TabletopFeat2024Profile(
                _featHealer2024,
                [GetDefinition<FeatDefinition>("FeatHealer")],
                [GroupFeats.FeatGroupOrigin, GroupFeats.FeatGroupSupportCombat]),
            new TabletopFeat2024Profile(
                _featLucky2024,
                [GetDefinition<FeatDefinition>("FeatLucky")],
                [GroupFeats.FeatGroupOrigin, GroupFeats.FeatGroupBodyResilience, GroupFeats.FeatGroupSupportCombat]),
            new TabletopFeat2024Profile(
                _featSavageAttack2024,
                [GetDefinition<FeatDefinition>("FeatSavageAttack")],
                [GroupFeats.FeatGroupOrigin, GroupFeats.FeatGroupMeleeCombat]),
            new TabletopFeat2024Profile(
                _featGroupSpeedy,
                [ForestRunner, featMobile],
                [GroupFeats.FeatGroupAgilityCombat]),
            new TabletopFeat2024Profile(
                _featGroupWarCaster2024,
                [featWarCaster],
                [GroupFeats.FeatGroupSpellCombat]),
            new TabletopFeat2024Profile(
                _featCrossbowExpert2024,
                [featRangedExpert],
                [GroupFeats.FeatGroupRangedCombat]),
            new TabletopFeat2024Profile(
                _featGroupElementalAdept2024,
                [featGroupElementalAdept],
                [GroupFeats.FeatGroupSpellCombat]),
            new TabletopFeat2024Profile(
                _featGroupInspiringLeader2024,
                [featInspiringLeader],
                [GroupFeats.FeatGroupSupportCombat]),
            new TabletopFeat2024Profile(
                _featGroupLightlyArmored2024,
                [],
                [featGroupArmor]),
            new TabletopFeat2024Profile(
                _featGroupMageSlayer2024,
                [featMageSlayer],
                [GroupFeats.FeatGroupSupportCombat]),
            new TabletopFeat2024Profile(
                _featGroupModeratelyArmored2024,
                [featGroupMediumArmor],
                [featGroupArmor]),
            new TabletopFeat2024Profile(
                _featGroupMediumArmorMaster2024,
                [featMediumArmorMaster],
                [featGroupArmor]),
            new TabletopFeat2024Profile(
                _featGroupHeavyArmorMaster2024,
                [featHeavyArmorMaster],
                [featGroupArmor]),
            new TabletopFeat2024Profile(
                _featGroupPoisoner2024,
                [featPoisoner],
                [GroupFeats.FeatGroupSupportCombat, GroupFeats.FeatGroupTools]),
            new TabletopFeat2024Profile(
                _featGroupPolearmMaster2024,
                [featPolearmExpert],
                [GroupFeats.FeatGroupMeleeCombat, GroupFeats.FeatGroupTwoHandedCombat]),
            new TabletopFeat2024Profile(
                _featGroupResilient2024,
                [featGroupCreed],
                [GroupFeats.FeatGroupBodyResilience]),
            new TabletopFeat2024Profile(
                _featGroupSentinel2024,
                [featSentinel],
                [GroupFeats.FeatGroupSupportCombat]),
            new TabletopFeat2024Profile(
                _featGroupHeavilyArmored2024,
                [],
                [featGroupArmor]),
            new TabletopFeat2024Profile(
                _featGroupFeyTeleport2024,
                [featGroupFeyTeleport],
                [featGroupPlaneMagic, featGroupRaceBound]),
            new TabletopFeat2024Profile(
                _featGroupFeyTouched2024,
                [],
                [featGroupPlaneMagic]),
            new TabletopFeat2024Profile(
                _featGroupShadowTouched2024,
                [featGroupTouchedMagic],
                [featGroupPlaneMagic]),
            new TabletopFeat2024Profile(
                _featShieldMaster2024,
                [featShieldTechniques],
                [GroupFeats.FeatGroupDefenseCombat]),
            new TabletopFeat2024Profile(
                _featGroupSpellSniper2024,
                [featGroupSpellSniper],
                [GroupFeats.FeatGroupSpellCombat]),
            new TabletopFeat2024Profile(
                _featGroupDualWielder2024,
                [featDualWeaponDefense, Ambidextrous],
                [GroupFeats.FeatGroupDefenseCombat, GroupFeats.FeatGroupTwoWeaponCombat]),
            new TabletopFeat2024Profile(
                _featGroupCharger2024,
                [featCharger],
                [GroupFeats.FeatGroupMeleeCombat]),
            new TabletopFeat2024Profile(
                _featDefensiveDuelist2024,
                [featDefensiveDuelist],
                [GroupFeats.FeatGroupDefenseCombat, GroupFeats.FeatGroupMeleeCombat]),
            new TabletopFeat2024Profile(
                _featGreatWeaponMaster2024,
                [featCleavingAttack],
                [GroupFeats.FeatGroupMeleeCombat]),
            new TabletopFeat2024Profile(
                _featSharpshooter2024,
                [featDeadeye],
                [GroupFeats.FeatGroupRangedCombat]),
            new TabletopFeat2024Profile(
                _featGroupGrappler2024,
                [featGrappler],
                [GroupFeats.FeatGroupUnarmoredCombat])
        ]);

        foreach (var independentFeat in IndependentTabletopFeatByCanonicalName)
        {
            if (ExplicitManagedLegacyRootNames.Contains(independentFeat.Key) ||
                IsExcludedManagedTabletopCanonicalName(independentFeat.Key) ||
                !SelectableManagedTabletopRootNames.Contains(independentFeat.Value.Name) ||
                !TryGetDefinition<FeatDefinition>(independentFeat.Key, out var legacyFeat) ||
                legacyFeat == independentFeat.Value)
            {
                continue;
            }

            var targetGroups = GroupFeats.Groups
                .Where(group => group.GetFirstSubFeatureOfType<GroupedFeat>() is { } groupedFeat &&
                                groupedFeat.Feats.Contains(legacyFeat))
                .ToArray();

            TabletopFeat2024Profiles.Add(new TabletopFeat2024Profile(
                independentFeat.Value,
                [legacyFeat],
                targetGroups));
        }

        foreach (var name in Main.Settings.TabletopFeats2024Initialized
                     .Where(name => !SelectableManagedTabletopRootNames.Contains(name))
                     .ToArray())
        {
            Main.Settings.TabletopFeats2024Initialized.Remove(name);
        }

        CaptureOriginalFeatHiddenStates();
    }

    private static void CaptureOriginalFeatHiddenStates()
    {
        foreach (var feat in TabletopFeat2024Profiles
                     .SelectMany(profile => profile.LegacyFeats.Concat(profile.TargetGroups))
                     .Where(feat => feat != null)
                     .Append(TryGetDefinition<FeatDefinition>("FeatGroupShadowTouched", out var legacyShadowTouched)
                         ? legacyShadowTouched
                         : null)
                     .Where(feat => feat != null))
        {
            OriginalFeatHiddenStateByName.TryAdd(feat.Name, feat.GuiPresentation.hidden);
        }
    }

    private static void RestoreOriginalFeatHiddenStates()
    {
        foreach (var entry in OriginalFeatHiddenStateByName)
        {
            if (TryGetDefinition<FeatDefinition>(entry.Key, out var feat))
            {
                feat.GuiPresentation.hidden = entry.Value;
            }
        }
    }

    internal static void RefreshManagedTabletopContainerMappings()
    {
        LoadManagedTabletopContainerMappings();
        NormalizeTabletopContainerGroupVisibility();
        GuiWrapperContext.RecacheFeats();
    }

    internal static void ClearManagedTabletopContainerMappingsForInactiveMode()
    {
        foreach (var groupName in ManagedTabletopFeatKinds
                     .Where(entry => entry.Value == TabletopFeatCatalogKind.Container)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            ManagedTabletopFeatKinds.Remove(groupName);
        }

        ManagedTabletopContainerNamesByCanonicalName.Clear();
        ManagedSelectableRootNamesByContainerName.Clear();
        ManagedTabletopContainerGroupNames.Clear();
    }

    private static void LoadManagedTabletopContainerMappings()
    {
        foreach (var groupName in ManagedTabletopContainerGroupNames.ToArray())
        {
            if (ManagedTabletopFeatKinds.TryGetValue(groupName, out var kind) &&
                kind == TabletopFeatCatalogKind.Container)
            {
                ManagedTabletopFeatKinds.Remove(groupName);
            }
        }

        ManagedTabletopContainerNamesByCanonicalName.Clear();
        ManagedSelectableRootNamesByContainerName.Clear();
        ManagedTabletopContainerGroupNames.Clear();

        var groupDefinitions = TabletopFeatContainerGroupNames
            .Select(groupName => TryGetDefinition<FeatDefinition>(groupName, out var featDefinition)
                ? featDefinition
                : null)
            .Where(group => group != null)
            .Distinct()
            .ToList();

        if (ShouldShowOriginFeatContainer() &&
            TryGetDefinition<FeatDefinition>("FeatGroupOrigin", out var featGroupOrigin))
        {
            groupDefinitions.Add(featGroupOrigin);
        }

        foreach (var groupDefinition in groupDefinitions.Distinct())
        {
            ManagedTabletopContainerGroupNames.Add(groupDefinition.Name);
            ManagedTabletopFeatKinds[groupDefinition.Name] = TabletopFeatCatalogKind.Container;
        }

        foreach (var groupName in ManagedTabletopContainerGroupNames)
        {
            ManagedSelectableRootNamesByContainerName[groupName] = [];
        }

        var profileTargetGroupsByReplacementName = TabletopFeat2024Profiles
            .Where(profile => profile.Replacement != null)
            .ToLookup(profile => profile.ReplacementName, profile => profile.TargetGroups);

        foreach (var managedRoot in IndependentTabletopFeatByCanonicalName
                     .Where(entry => entry.Value != null && IsSelectableManagedTabletopRoot(entry.Value)))
        {
            var replacement = managedRoot.Value;
            var canonicalName = managedRoot.Key;
            var explicitTargetGroups = profileTargetGroupsByReplacementName[replacement.Name]
                .SelectMany(groups => groups)
                .Where(groupDefinition =>
                    groupDefinition != null && ManagedTabletopContainerGroupNames.Contains(groupDefinition.Name))
                .Distinct()
                .ToArray();

            var targetGroups = (ExplicitManagedLegacyRootNames.Contains(canonicalName) &&
                                explicitTargetGroups.Length > 0
                    ? explicitTargetGroups
                    : ResolveManagedContainerGroups(canonicalName).Concat(explicitTargetGroups))
                .Where(groupDefinition =>
                    groupDefinition != null && ManagedTabletopContainerGroupNames.Contains(groupDefinition.Name))
                .Distinct()
                .ToArray();

            foreach (var groupDefinition in targetGroups)
            {
                if (!ManagedTabletopContainerNamesByCanonicalName.TryGetValue(canonicalName, out var containerNames))
                {
                    containerNames = [];
                    ManagedTabletopContainerNamesByCanonicalName[canonicalName] = containerNames;
                }

                containerNames.Add(groupDefinition.Name);

                if (!ManagedSelectableRootNamesByContainerName.TryGetValue(groupDefinition.Name, out var rootNames))
                {
                    rootNames = [];
                    ManagedSelectableRootNamesByContainerName[groupDefinition.Name] = rootNames;
                }

                rootNames.Add(replacement.Name);
            }
        }
    }

    private static IEnumerable<FeatDefinition> ResolveManagedContainerGroups(string canonicalName)
    {
        if (string.IsNullOrEmpty(canonicalName) ||
            IsExcludedManagedTabletopCanonicalName(canonicalName) ||
            !TryGetDefinition<FeatDefinition>(canonicalName, out var legacyDefinition))
        {
            return [];
        }

        var groups = GroupFeats.Groups
            .Where(group => group != null && ManagedTabletopContainerGroupNames.Contains(group.Name))
            .Where(group => group.GetFirstSubFeatureOfType<GroupedFeat>() is { } groupedFeat &&
                            groupedFeat.GetSubFeats(true).Contains(legacyDefinition))
            .Where(group => group.Name != "FeatGroupOrigin" || !IsMagicInitiateLeafCanonicalName(canonicalName))
            .Distinct()
            .ToList();

        if (canonicalName == "FeatGroupMagicInitiate" &&
            ShouldShowOriginFeatContainer() &&
            TryGetDefinition<FeatDefinition>("FeatGroupOrigin", out var featGroupOrigin))
        {
            groups.Add(featGroupOrigin);
        }

        return groups
            .Distinct()
            .ToArray();
    }

    internal static bool IsVisibleInGameFeatSelection(FeatDefinition feat)
    {
        if (!IsAllowedInGameFeatSelectionByConfiguration(feat))
        {
            return false;
        }

        if (!Main.Settings.EnableTabletopFeatRules2024)
        {
            return !feat.GuiPresentation.Hidden;
        }

        return !IsNonSelectableTabletopGroup(feat) &&
               (!IsTabletopContainerGroup(feat) || HasAllowedGameFeatDescendant(feat));
    }

    internal static bool IsAllowedInGameFeatSelectionByConfiguration(FeatDefinition feat)
    {
        return feat != null &&
               !IsNonSelectableTabletopGroup(feat) &&
               IsVisibleBySettings(feat) &&
               (Main.Settings.EnableTabletopFeatRules2024
                    ? IsManagedTabletopFeat(feat) || IsTabletopContainerGroup(feat)
                    : !IsManagedTabletopFeat(feat));
    }

    internal static bool IsNonSelectableTabletopGroup(FeatDefinition feat)
    {
        if (feat == null || !Main.Settings.EnableTabletopFeatRules2024)
        {
            return false;
        }

        return ManagedTabletopFeatKinds.TryGetValue(feat.Name, out var kind)
            ? kind == TabletopFeatCatalogKind.Helper
            : TabletopFeatHelperGroupNames.Contains(feat.Name);
    }

    internal static bool IsSelectableManagedTabletopRoot(FeatDefinition feat)
    {
        return feat != null &&
               SelectableManagedTabletopRootNames.Contains(feat.Name) &&
               !ManagedTabletopParentNameByDefinitionName.ContainsKey(feat.Name);
    }

    internal static bool HasAllowedGameFeatDescendant(FeatDefinition feat)
    {
        if (feat == null || IsNonSelectableTabletopGroup(feat))
        {
            return false;
        }

        if (feat.GetFirstSubFeatureOfType<IGroupedFeat>() is not { })
        {
            return IsAllowedInGameFeatSelectionByConfiguration(feat);
        }

        return GetAllowedGameFeatChildren(feat).Any();
    }

    internal static bool IsTabletopContainerGroup(FeatDefinition feat)
    {
        return feat != null &&
               (ManagedTabletopFeatKinds.TryGetValue(feat.Name, out var kind)
                   ? kind == TabletopFeatCatalogKind.Container
                   : ManagedTabletopContainerGroupNames.Contains(feat.Name));
    }

    internal static IEnumerable<FeatDefinition> GetGameFeatSelectionCatalogRoots()
    {
        if (!Main.Settings.EnableTabletopFeatRules2024)
        {
            return [];
        }

        var containerGroups = ManagedTabletopContainerGroupNames
            .Select(name => TryGetDefinition<FeatDefinition>(name, out var feat) ? feat : null)
            .Where(feat => feat != null)
            .Distinct()
            .ToArray();

        var activeContainerNames = containerGroups
            .Where(group => group != null && HasAllowedGameFeatDescendant(group))
            .Select(group => group.Name)
            .ToHashSet();

        var managedRoots = IndependentTabletopFeatByCanonicalName.Values
            .Where(IsSelectableManagedTabletopRoot)
            .Where(feat =>
            {
                var canonicalName = GetCanonicalTabletopFeatName(feat.Name);

                return !ManagedTabletopContainerNamesByCanonicalName.TryGetValue(canonicalName, out var containerNames) ||
                       containerNames.All(containerName => !activeContainerNames.Contains(containerName));
            });

        return managedRoots
            .Concat(containerGroups.Where(group => activeContainerNames.Contains(group.Name)))
            .Where(feat => feat != null && !IsNonSelectableTabletopGroup(feat))
            .Distinct();
    }

    internal static IEnumerable<FeatDefinition> GetAllowedGameFeatChildren(FeatDefinition feat)
    {
        if (feat == null)
        {
            return [];
        }

        if (Main.Settings.EnableTabletopFeatRules2024)
        {
            if (IsNonSelectableTabletopGroup(feat))
            {
                return [];
            }

            if (IsTabletopContainerGroup(feat))
            {
                return ManagedSelectableRootNamesByContainerName.TryGetValue(feat.Name, out var rootNames)
                    ? rootNames
                        .Select(rootName => TryGetDefinition<FeatDefinition>(rootName, out var rootDefinition)
                            ? rootDefinition
                            : null)
                        .Where(child => child != null && IsVisibleInGameFeatSelection(child))
                        .Distinct()
                        .OrderBy(child => child.FormatTitle())
                        .ToArray()
                    : [];
            }

            var managedChildren = ManagedTabletopChildNamesByParentName.TryGetValue(feat.Name, out var childNames)
                ? childNames
                    .Select(childName => TryGetDefinition<FeatDefinition>(childName, out var childDefinition)
                        ? childDefinition
                        : null)
                    .Where(child => child != null &&
                                    !IsNonSelectableTabletopGroup(child) &&
                                    !IsTabletopContainerGroup(child) &&
                                    IsVisibleInGameFeatSelection(child))
                    .Distinct()
                    .OrderBy(child => child.FormatTitle())
                    .ToArray()
                : [];

            if (managedChildren.Length > 0)
            {
                return managedChildren;
            }

            return [];
        }

        if (feat.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeatLegacy)
        {
            return [];
        }

        return groupedFeatLegacy.GetSubFeats(true)
            .Where(HasAllowedGameFeatDescendant)
            .Distinct();
    }

    private static IEnumerable<FeatDefinition> EnumerateSelectableLeafFeats(FeatDefinition feat)
    {
        if (feat == null || IsNonSelectableTabletopGroup(feat))
        {
            yield break;
        }

        if (feat.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat)
        {
            yield return feat;
            yield break;
        }

        foreach (var subFeat in groupedFeat.GetSubFeats(true))
        {
            foreach (var leafFeat in EnumerateSelectableLeafFeats(subFeat))
            {
                yield return leafFeat;
            }
        }
    }

    private static IEnumerable<FeatDefinition> ResolveIndependentRestrictedChoices(string featName)
    {
        if (string.IsNullOrEmpty(featName))
        {
            return [];
        }

        var canonicalName = GetCanonicalTabletopFeatName(featName);

        return Main.Settings.EnableTabletopFeatRules2024 &&
               IndependentTabletopFeatByCanonicalName.TryGetValue(canonicalName, out var independentFeat)
            ? EnumerateSelectableLeafFeats(independentFeat).Distinct()
            : [];
    }

    internal static IReadOnlyCollection<string> GetModeAwareRestrictedChoiceNames(PointPool pointPool)
    {
        if (pointPool?.RestrictedChoices is not { Count: > 0 })
        {
            return [];
        }

        if (!Main.Settings.EnableTabletopFeatRules2024)
        {
            return pointPool.RestrictedChoices.ToArray();
        }

        return pointPool.RestrictedChoices
            .SelectMany(featName =>
            {
                var resolvedNames = ResolveIndependentRestrictedChoices(featName)
                    .Select(feat => feat.Name)
                    .Distinct()
                    .ToArray();

                return resolvedNames.Length > 0 ? resolvedNames : [featName];
            })
            .Distinct()
            .ToArray();
    }

    internal static void NormalizeModeAwareFeatPointPool(PointPool pointPool)
    {
        if (pointPool?.RestrictedChoices is not { Count: > 0 })
        {
            return;
        }

        var normalizedChoices = GetModeAwareRestrictedChoiceNames(pointPool)
            .Distinct()
            .ToList();

        if (normalizedChoices.Count == 0)
        {
            return;
        }

        pointPool.RestrictedChoices.Clear();
        pointPool.RestrictedChoices.AddRange(normalizedChoices);
    }

    internal static bool IsFeatMatchingPrerequisites(
        ICharacterBuildingService service,
        CharacterHeroBuildingData heroBuildingData,
        FeatDefinition feat,
        out bool isSameFamily)
    {
        isSameFamily = false;

        if (service == null || heroBuildingData == null || feat == null)
        {
            return false;
        }

        var prerequisiteOverrideState = PushModFeatPrerequisiteOverride(
            ShouldForceManagedFeatPrerequisites(feat));

        try
        {
            return Evaluate(feat, ref isSameFamily) && MatchesGroupedRootPrerequisites(feat, ref isSameFamily);
        }
        finally
        {
            RestoreModFeatPrerequisiteOverride(prerequisiteOverrideState);
        }

        bool Evaluate(FeatDefinition definition, ref bool sameFamily)
        {
            var matchesPrerequisites = service.IsFeatMatchingPrerequisites(heroBuildingData, definition, out var localSameFamily);
            sameFamily |= localSameFamily;

            if (definition is not FeatDefinitionWithPrerequisites featDefinitionWithPrerequisites ||
                featDefinitionWithPrerequisites.Validators.Count == 0)
            {
                return matchesPrerequisites;
            }

            var (result, _) = featDefinitionWithPrerequisites.Validate(
                featDefinitionWithPrerequisites,
                heroBuildingData.HeroCharacter);

            return matchesPrerequisites && result;
        }

        bool MatchesGroupedRootPrerequisites(FeatDefinition definition, ref bool sameFamily)
        {
            var currentName = definition.Name;
            var processedParents = new HashSet<string>();

            while (ManagedTabletopParentNameByDefinitionName.TryGetValue(currentName, out var parentName) &&
                   processedParents.Add(parentName))
            {
                if (TryGetDefinition<FeatDefinition>(parentName, out var parentDefinition) &&
                    !Evaluate(parentDefinition, ref sameFamily))
                {
                    return false;
                }

                currentName = parentName;
            }

            return true;
        }
    }

    internal static bool TryResolveTrainableModeAwareFeat(
        FeatDefinition feat,
        out FeatDefinition resolvedFeat)
    {
        resolvedFeat = null;

        if (feat == null)
        {
            return false;
        }

        if (!Main.Settings.EnableTabletopFeatRules2024)
        {
            resolvedFeat = feat;

            return true;
        }

        if (IsManagedTabletopFeat(feat))
        {
            if (!IsSelectableManagedTabletopFeatLeaf(feat))
            {
                return false;
            }

            resolvedFeat = feat;

            return true;
        }

        if (!TryResolveModeAwareFeatDefinition(feat.Name, out var modeAwareFeat) ||
            !IsManagedTabletopFeat(modeAwareFeat))
        {
            resolvedFeat = feat;

            return true;
        }

        if (!IsSelectableManagedTabletopFeatLeaf(modeAwareFeat))
        {
            return false;
        }

        resolvedFeat = modeAwareFeat;

        return true;
    }

    internal static bool TryPrepareIndependentFeatTraining(
        CharacterHeroBuildingData heroBuildingData,
        string tag,
        FeatDefinition feat,
        ICharacterBuildingService service)
    {
        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            heroBuildingData == null ||
            feat == null ||
            !IsManagedTabletopFeat(feat))
        {
            return true;
        }

        if (service?.GetPointPoolOfTypeAndTag(heroBuildingData, HeroDefinitions.PointsPoolType.Feat, tag) is not { } pointPool ||
            !IsSelectableManagedTabletopFeatLeaf(feat))
        {
            return false;
        }

        NormalizeModeAwareFeatPointPool(pointPool);

        FeatDefinition parentGroupedFeat = null;

        if (ManagedTabletopParentNameByDefinitionName.TryGetValue(feat.Name, out var parentName))
        {
            TryGetDefinition(parentName, out parentGroupedFeat);
        }

        if (!MatchesRestrictedChoice(feat, parentGroupedFeat, pointPool.RestrictedChoices?.ToHashSet() ?? []) ||
            !IsFeatMatchingPrerequisites(service, heroBuildingData, feat, out _))
        {
            return false;
        }

        return !service.IsFeatKnownOrTrained(heroBuildingData, feat) ||
               service.IsFeatSelectedForTraining(heroBuildingData, feat, tag);
    }

    internal static bool CanSelectFeatForCurrentPointPool(
        RulesetCharacterHero hero,
        string tag,
        FeatDefinition feat,
        ICharacterBuildingService service)
    {
        if (hero?.GetHeroBuildingData() is not { } heroBuildingData ||
            service?.GetPointPoolOfTypeAndTag(heroBuildingData, HeroDefinitions.PointsPoolType.Feat, tag)
            is not { } pointPool ||
            feat == null ||
            !IsAllowedInGameFeatSelectionByConfiguration(feat) ||
            !IsVisibleInGameFeatSelection(feat))
        {
            return false;
        }

        if (!TryResolveTrainableModeAwareFeat(feat, out var resolvedFeat) ||
            !IsSelectableTabletopFeatLeaf(resolvedFeat))
        {
            return false;
        }

        FeatDefinition parentGroupedFeat = null;

        if (ManagedTabletopParentNameByDefinitionName.TryGetValue(resolvedFeat.Name, out var parentName))
        {
            TryGetDefinition(parentName, out parentGroupedFeat);
        }

        var restrictedChoices = GetModeAwareRestrictedChoiceNames(pointPool).ToHashSet();

        if (!MatchesRestrictedChoice(resolvedFeat, parentGroupedFeat, restrictedChoices) ||
            !IsFeatMatchingPrerequisites(service, heroBuildingData, resolvedFeat, out _))
        {
            return false;
        }

        return !service.IsFeatKnownOrTrained(heroBuildingData, resolvedFeat) ||
               service.IsFeatSelectedForTraining(heroBuildingData, resolvedFeat, tag);
    }

    private static string BuildPendingFeatSelectionKey(RulesetCharacterHero hero, string tag)
    {
        return hero == null || string.IsNullOrEmpty(tag)
            ? null
            : $"{hero.Guid}:{tag}";
    }

    internal static void RememberPendingFeatSelection(RulesetCharacterHero hero, string tag, FeatDefinition feat)
    {
        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            hero == null ||
            string.IsNullOrEmpty(tag) ||
            feat == null ||
            !IsSelectableTabletopFeatLeaf(feat))
        {
            return;
        }

        var key = BuildPendingFeatSelectionKey(hero, tag);

        if (!string.IsNullOrEmpty(key))
        {
            PendingSelectedFeatNameByHeroAndTag[key] = feat.Name;
        }
    }

    internal static bool TryGetPendingFeatSelection(
        RulesetCharacterHero hero,
        string tag,
        out FeatDefinition feat)
    {
        feat = null;

        var key = BuildPendingFeatSelectionKey(hero, tag);

        return !string.IsNullOrEmpty(key) &&
               PendingSelectedFeatNameByHeroAndTag.TryGetValue(key, out var featName) &&
               TryGetDefinition(featName, out feat);
    }

    internal static void ClearPendingFeatSelection(RulesetCharacterHero hero, string tag)
    {
        var key = BuildPendingFeatSelectionKey(hero, tag);

        if (!string.IsNullOrEmpty(key))
        {
            PendingSelectedFeatNameByHeroAndTag.Remove(key);
        }
    }

    internal static void ClearPendingFeatSelections(RulesetCharacterHero hero)
    {
        if (hero == null)
        {
            return;
        }

        var prefix = $"{hero.Guid}:";

        foreach (var key in PendingSelectedFeatNameByHeroAndTag.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                     .ToArray())
        {
            PendingSelectedFeatNameByHeroAndTag.Remove(key);
        }
    }

    internal static void ClearPendingFeatSelections()
    {
        PendingSelectedFeatNameByHeroAndTag.Clear();
    }

    internal static void RepairLegacyFeatGroupSettingsAfter2024BugV2()
    {
        if (Main.Settings.RepairedLegacyFeatGroupEnabledAfter2024ContainerBugV2 ||
            Main.Settings.EnableTabletopFeatRules2024)
        {
            return;
        }

        var legacyContainerGroups = TabletopFeatContainerGroupNames
            .Select(groupName => FeatsContext.FeatGroups.FirstOrDefault(group => group.Name == groupName))
            .Where(group => group != null && !Is2024TabletopFeat(group))
            .Distinct()
            .ToList();

        if (legacyContainerGroups.Count == 0)
        {
            Main.Settings.RepairedLegacyFeatGroupEnabledAfter2024ContainerBugV2 = true;

            return;
        }

        var hasAnyEnabledLegacyContainerGroup = legacyContainerGroups
            .Any(group => Main.Settings.FeatGroupEnabled.Contains(group.Name));

        if (!hasAnyEnabledLegacyContainerGroup)
        {
            foreach (var group in legacyContainerGroups)
            {
                Main.Settings.FeatGroupEnabled.TryAdd(group.Name);
            }
        }

        Main.Settings.RepairedLegacyFeatGroupEnabledAfter2024ContainerBugV2 = true;
    }

    internal static IEnumerable<FeatDefinition> GetDisplayableManagedTabletopTrainedFeats(RulesetCharacterHero hero)
    {
        return GetDisplayableManagedTabletopTrainedFeats(hero, null);
    }

    internal static IEnumerable<FeatDefinition> GetDisplayableManagedTabletopTrainedFeats(
        RulesetCharacterHero hero,
        CharacterHeroBuildingData buildingData)
    {
        if (hero == null)
        {
            yield break;
        }

        var displayableFeats = new List<FeatDefinition>();

        foreach (var feat in hero.TrainedFeats ?? [])
        {
            TryAddDisplayableManagedTabletopFeat(displayableFeats, feat);
        }

        if (buildingData?.LevelupTrainedFeats != null)
        {
            foreach (var feat in buildingData.LevelupTrainedFeats.Values
                         .Where(feats => feats != null)
                         .SelectMany(feats => feats))
            {
                TryAddDisplayableManagedTabletopFeat(displayableFeats, feat);
            }
        }

        foreach (var feat in displayableFeats)
        {
            yield return feat;
        }
    }

    internal static bool HasTrainedOrSelectedDescendant(
        CharacterHeroBuildingData heroBuildingData,
        RulesetCharacterHero hero,
        string tag,
        FeatDefinition group)
    {
        return TryGetTrainedOrSelectedDescendant(heroBuildingData, hero, tag, group, out _);
    }

    internal static bool TryGetTrainedOrSelectedDescendant(
        CharacterHeroBuildingData heroBuildingData,
        RulesetCharacterHero hero,
        string tag,
        FeatDefinition group,
        out FeatDefinition selectedLeaf)
    {
        selectedLeaf = null;

        if (group == null)
        {
            return false;
        }

        var descendantLeaves = EnumerateFeatAndDescendantLeaves(group)
            .Distinct()
            .ToArray();

        if (descendantLeaves.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(tag) &&
            TryGetPendingFeatSelection(hero, tag, out var pendingFeat) &&
            TryMatchEquivalentLeaf(descendantLeaves, new[] { pendingFeat }, out selectedLeaf))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(tag) &&
            heroBuildingData?.LevelupTrainedFeats.TryGetValue(tag, out var trainedFeatsByTag) == true &&
            TryMatchEquivalentLeaf(descendantLeaves, trainedFeatsByTag, out selectedLeaf))
        {
            return true;
        }

        if (TryMatchEquivalentLeaf(descendantLeaves, hero?.TrainedFeats, out selectedLeaf))
        {
            return true;
        }

        return false;
    }

    internal static string GetCanonicalTabletopFeatName(string featName)
    {
        return !string.IsNullOrEmpty(featName) &&
               CanonicalTabletopFeatNameByDefinitionName.TryGetValue(featName, out var canonicalName)
            ? canonicalName
            : featName;
    }

    internal static bool ShouldShowOriginFeatContainer()
    {
        return Main.Settings.EnableTabletopFeatRules2024 && Main.Settings.EnableBackgroundASI;
    }

    internal static bool TrySetManagedTabletopParent(
        FeatDefinition childDefinition,
        FeatDefinition parentDefinition,
        bool allowOverride = false)
    {
        if (childDefinition == null || parentDefinition == null)
        {
            return false;
        }

        if (!ManagedTabletopParentNameByDefinitionName.TryGetValue(childDefinition.Name, out var existingParentName))
        {
            SetManagedTabletopParentName(childDefinition.Name, parentDefinition.Name);

            return true;
        }

        if (existingParentName == parentDefinition.Name)
        {
            return true;
        }

        if (!allowOverride)
        {
            return false;
        }

        SetManagedTabletopParentName(childDefinition.Name, parentDefinition.Name);

        return true;
    }

    private static void ClearManagedTabletopParent(string childName)
    {
        if (string.IsNullOrEmpty(childName) ||
            !ManagedTabletopParentNameByDefinitionName.TryGetValue(childName, out var parentName))
        {
            return;
        }

        ((IDictionary<string, string>)ManagedTabletopParentNameByDefinitionName).Remove(childName);

        if (string.IsNullOrEmpty(parentName) ||
            !ManagedTabletopChildNamesByParentName.TryGetValue(parentName, out var childNames))
        {
            return;
        }

        childNames.Remove(childName);

        if (childNames.Count == 0)
        {
            ManagedTabletopChildNamesByParentName.Remove(parentName);
        }
    }

    private static void SetManagedTabletopParentName(string childName, string parentName)
    {
        if (string.IsNullOrEmpty(childName) ||
            string.IsNullOrEmpty(parentName))
        {
            return;
        }

        ClearManagedTabletopParent(childName);
        ManagedTabletopParentNameByDefinitionName[childName] = parentName;

        if (!ManagedTabletopChildNamesByParentName.TryGetValue(parentName, out var childNames))
        {
            childNames = [];
            ManagedTabletopChildNamesByParentName[parentName] = childNames;
        }

        childNames.Add(childName);
    }

    private static bool IsExcludedManagedTabletopCanonicalName(string canonicalName)
    {
        return !string.IsNullOrEmpty(canonicalName) &&
               ExcludedManagedTabletopCanonicalNames.Contains(canonicalName);
    }

    private static bool IsOptInOnlyManagedTabletopCanonicalName(string canonicalName)
    {
        return !string.IsNullOrEmpty(canonicalName) &&
               OptInOnlyManagedTabletopCanonicalNames.Contains(canonicalName);
    }

    private static bool ShouldSuppressLegacyAutoEnable(string canonicalName)
    {
        return IsOptInOnlyManagedTabletopCanonicalName(canonicalName);
    }

    private static bool IsMagicInitiateLeafCanonicalName(string canonicalName)
    {
        return !string.IsNullOrEmpty(canonicalName) &&
               canonicalName.StartsWith("FeatMagicInitiate", StringComparison.Ordinal) &&
               canonicalName != "FeatGroupMagicInitiate";
    }

    internal static bool AreEquivalentTabletopFeatNames(string left, string right)
    {
        return !string.IsNullOrEmpty(left) &&
               !string.IsNullOrEmpty(right) &&
               GetCanonicalTabletopFeatName(left) == GetCanonicalTabletopFeatName(right);
    }

    internal static bool MatchesRestrictedChoice(
        FeatDefinition feat,
        FeatDefinition parentGroupedFeat,
        HashSet<string> restrictedChoices)
    {
        if (feat == null || restrictedChoices == null || restrictedChoices.Count == 0)
        {
            return true;
        }

        foreach (var restrictedChoiceName in EnumerateRestrictionAliasesForFeat(feat, parentGroupedFeat))
        {
            if (restrictedChoices.Contains(restrictedChoiceName))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateRestrictionAliasesForFeat(
        FeatDefinition feat,
        FeatDefinition parentGroupedFeat)
    {
        if (feat == null)
        {
            yield break;
        }

        var processedAliases = new HashSet<string>();
        var processedParentNames = new HashSet<string>();

        foreach (var featName in EnumerateRestrictionAliasesForName(feat.Name, processedAliases))
        {
            yield return featName;
        }

        var currentName = parentGroupedFeat?.Name;

        if (string.IsNullOrEmpty(currentName))
        {
            ManagedTabletopParentNameByDefinitionName.TryGetValue(feat.Name, out currentName);
        }

        while (!string.IsNullOrEmpty(currentName) &&
               processedParentNames.Add(currentName))
        {
            foreach (var featName in EnumerateRestrictionAliasesForName(currentName, processedAliases))
            {
                yield return featName;
            }

            ManagedTabletopParentNameByDefinitionName.TryGetValue(currentName, out currentName);
        }
    }

    private static IEnumerable<string> EnumerateRestrictionAliasesForName(
        string featName,
        HashSet<string> processedAliases)
    {
        if (string.IsNullOrEmpty(featName))
        {
            yield break;
        }

        if (processedAliases.Add(featName))
        {
            yield return featName;
        }

        var canonicalName = GetCanonicalTabletopFeatName(featName);

        if (!string.IsNullOrEmpty(canonicalName) && processedAliases.Add(canonicalName))
        {
            yield return canonicalName;
        }

        foreach (var containerName in EnumerateManagedContainerNamesForCanonical(canonicalName))
        {
            if (processedAliases.Add(containerName))
            {
                yield return containerName;
            }
        }
    }

    private static IEnumerable<string> EnumerateManagedContainerNamesForCanonical(string canonicalName)
    {
        if (string.IsNullOrEmpty(canonicalName) ||
            !ManagedTabletopContainerNamesByCanonicalName.TryGetValue(canonicalName, out var containerNames))
        {
            yield break;
        }

        foreach (var containerName in containerNames)
        {
            if (!string.IsNullOrEmpty(containerName))
            {
                yield return containerName;
            }
        }
    }

    private static IEnumerable<FeatDefinition> EnumerateFeatAndDescendantLeaves(FeatDefinition feat)
    {
        return EnumerateFeatAndDescendantLeaves(feat, new HashSet<string>());
    }

    private static IEnumerable<FeatDefinition> EnumerateFeatAndDescendantLeaves(
        FeatDefinition feat,
        HashSet<string> visitedNames)
    {
        if (feat == null ||
            !visitedNames.Add(feat.Name) ||
            IsNonSelectableTabletopGroup(feat))
        {
            yield break;
        }

        var children = GetAllowedGameFeatChildren(feat)
            .ToArray();

        if (children.Length == 0 &&
            feat.GetFirstSubFeatureOfType<IGroupedFeat>() is { } groupedFeat &&
            !IsTabletopContainerGroup(feat))
        {
            children = groupedFeat.GetSubFeats(true)
                .Where(IsVisibleInGameFeatSelection)
                .ToArray();
        }

        if (children.Length == 0)
        {
            if (!IsTabletopContainerGroup(feat) &&
                feat.GetFirstSubFeatureOfType<IGroupedFeat>() == null)
            {
                yield return feat;
            }

            yield break;
        }

        foreach (var child in children)
        {
            foreach (var leaf in EnumerateFeatAndDescendantLeaves(child, visitedNames))
            {
                yield return leaf;
            }
        }
    }

    private static bool TryMatchEquivalentLeaf(
        IEnumerable<FeatDefinition> descendantLeaves,
        IEnumerable<FeatDefinition> candidates,
        out FeatDefinition selectedLeaf)
    {
        selectedLeaf = null;

        if (descendantLeaves == null || candidates == null)
        {
            return false;
        }

        var candidateNames = candidates
            .Where(candidate => candidate != null)
            .Select(candidate => GetCanonicalTabletopFeatName(candidate.Name))
            .Where(candidateName => !string.IsNullOrEmpty(candidateName))
            .ToHashSet();

        if (candidateNames.Count == 0)
        {
            return false;
        }

        selectedLeaf = descendantLeaves.FirstOrDefault(leaf =>
            leaf != null &&
            candidateNames.Contains(GetCanonicalTabletopFeatName(leaf.Name)));

        return selectedLeaf != null;
    }

    internal static bool TryResolveModeAwareFeatDefinition(string featName, out FeatDefinition featDefinition)
    {
        featDefinition = null;

        if (string.IsNullOrEmpty(featName))
        {
            return false;
        }

        if (Main.Settings.EnableTabletopFeatRules2024 &&
            TryGetDefinition(featName, out featDefinition) &&
            IsManagedTabletopFeat(featDefinition))
        {
            return true;
        }

        var canonicalName = GetCanonicalTabletopFeatName(featName);

        if (Main.Settings.EnableTabletopFeatRules2024 &&
            IndependentTabletopFeatByCanonicalName.TryGetValue(canonicalName, out featDefinition))
        {
            return true;
        }

        if (Main.Settings.EnableTabletopFeatRules2024 &&
            (ManagedTabletopContainerGroupNames.Contains(canonicalName) ||
             TabletopFeatHelperGroupNames.Contains(canonicalName)))
        {
            return false;
        }

        return TryGetDefinition(featName, out featDefinition) ||
               (canonicalName != featName && TryGetDefinition(canonicalName, out featDefinition));
    }

    private static void NormalizeTabletopContainerGroupVisibility()
    {
        foreach (var groupName in ManagedTabletopContainerGroupNames)
        {
            if (!TryGetDefinition<FeatDefinition>(groupName, out var group))
            {
                continue;
            }

            var hasVisibleChildren = GetAllowedGameFeatChildren(group).Any();

            group.GuiPresentation.hidden = !hasVisibleChildren;
        }
    }

    private static FeatDefinition BuildArmorHalfFeat(
        string name,
        string title,
        string attribute,
        string baseDescription,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        FeatureDefinition proficiency,
        string prerequisiteArmorCategory = null,
        int? abilityPrerequisiteValue = null,
        bool clearAbilityPrerequisite = false,
        bool hideFromFeats = true)
    {
        var builder = FeatDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(title, BuildHalfFeatDescription(attribute, baseDescription), hidden: false)
            .SetFeatures(attributeModifier, proficiency)
            .SetFeatFamily(family);

        if (hideFromFeats)
        {
            builder.AddCustomSubFeatures(FeatsContext.HideFromFeats.Marker);
        }

        if (!string.IsNullOrEmpty(prerequisiteArmorCategory))
        {
            builder.SetArmorProficiencyPrerequisite(prerequisiteArmorCategory);
        }

        var feat = builder.AddToDB();

        if (clearAbilityPrerequisite)
        {
            ClearMinimalAbilityPrerequisite(feat);
        }
        else if (abilityPrerequisiteValue.HasValue)
        {
            OverrideMinimalAbilityPrerequisite(feat, attribute, abilityPrerequisiteValue.Value);
        }

        return feat;
    }

    private static FeatDefinition BuildResilientVariant(FeatDefinition featDefinition, string name, string attribute)
    {
        var attributeTitle = GetAttributeTitle(attribute);
        var title = Gui.Format("Feat/&GeneralFeat2024VariantTitle",
            Gui.Localize("Feat/&FeatGroupResilient2024Title"), attributeTitle);
        var description = BuildHalfFeatDescription(
            attribute,
            Gui.Format("Feat/&FeatGroupResilient2024VariantDescription", attributeTitle));

        var feat = BuildExplicitIndependentFeat(
            featDefinition,
            name,
            Resilient2024Family,
            title,
            description,
            hideFromFeats: true,
            hidden: false);

        ClearMinimalAbilityPrerequisite(feat);

        return feat;
    }

    private static FeatDefinition BuildHalfFeatVariant(
        FeatDefinition featDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string title,
        string description)
    {
        return BuildExplicitIndependentFeat(
            featDefinition,
            name,
            family,
            title,
            description,
            hideFromFeats: true,
            hidden: false,
            extraFeatures: [attributeModifier]);
    }

    private static FeatDefinition BuildAbilityPrerequisiteHalfFeatVariant(
        FeatDefinition sourceDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string attribute,
        string groupTitle,
        string baseDescription,
        int prerequisiteValue = 13)
    {
        return Build2024HalfFeatVariant(
            sourceDefinition,
            name,
            attributeModifier,
            family,
            attribute,
            groupTitle,
            baseDescription,
            prerequisiteValue);
    }

    private static FeatDefinition BuildDedicated2024HalfFeatVariant(
        FeatDefinition sourceDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string attribute,
        string groupTitle,
        string baseDescription,
        int? prerequisiteValue = 13,
        bool clearAbilityPrerequisite = false,
        string explicitTitle = null,
        params FeatureDefinition[] extraFeatures)
    {
        return BuildDedicated2024SingleHalfFeat(
            sourceDefinition,
            name,
            attributeModifier,
            family,
            attribute,
            explicitTitle ?? Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute)),
            baseDescription,
            hideFromFeats: true,
            prerequisiteValue: prerequisiteValue,
            clearAbilityPrerequisite: clearAbilityPrerequisite,
            extraFeatures: extraFeatures);
    }

    private static FeatDefinition BuildSingleAbilityPrerequisiteHalfFeat(
        FeatDefinition sourceDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string attribute,
        string title,
        string baseDescription,
        bool hideFromFeats = false,
        int? prerequisiteValue = 13,
        bool clearAbilityPrerequisite = false,
        params FeatureDefinition[] extraFeatures)
    {
        return Build2024SingleHalfFeat(
            sourceDefinition,
            name,
            attributeModifier,
            family,
            attribute,
            title,
            baseDescription,
            hideFromFeats,
            prerequisiteValue,
            clearAbilityPrerequisite,
            extraFeatures);
    }

    private static string BuildHalfFeatDescription(string attribute, string baseDescription)
    {
        var increaseDescription = Gui.Format("Feat/&GeneralFeat2024IncreaseDescription", GetAttributeTitle(attribute));

        return string.IsNullOrWhiteSpace(baseDescription)
            ? increaseDescription
            : $"{increaseDescription}\n{baseDescription}";
    }

    private static string Get2024HalfFeatBaseDescriptionKey(string definitionName)
    {
        if (string.IsNullOrEmpty(definitionName))
        {
            return null;
        }

        return definitionName.EndsWith("2024", StringComparison.Ordinal)
            ? $"Feat/&{definitionName}BaseDescription"
            : $"Feat/&{definitionName}2024BaseDescription";
    }

    private static string GetHalfFeatBaseDescription(string description, string fallbackDescription = null)
    {
        var sourceDescription = string.IsNullOrWhiteSpace(description) || description.Contains("/&")
            ? fallbackDescription
            : description;

        if (string.IsNullOrWhiteSpace(sourceDescription))
        {
            return string.Empty;
        }

        if (sourceDescription.Contains("/&"))
        {
            var localizedFallback = Gui.Localize(sourceDescription);

            if (!string.IsNullOrWhiteSpace(localizedFallback) &&
                !localizedFallback.Contains("/&"))
            {
                sourceDescription = localizedFallback;
            }
        }

        return sourceDescription.Trim();
    }

    private static string Get2024HalfFeatGroupTitle(
        string titleKey,
        FeatDefinition fallbackFeat = null,
        string fallbackTitle = null)
    {
        var localizedTitle = string.IsNullOrEmpty(titleKey) ? null : Gui.Localize(titleKey);

        if (!string.IsNullOrEmpty(localizedTitle) && !localizedTitle.Contains("/&"))
        {
            return localizedTitle;
        }

        if (!string.IsNullOrWhiteSpace(fallbackTitle))
        {
            return StripAttributeVariantSuffix(fallbackTitle);
        }

        return StripAttributeVariantSuffix(fallbackFeat?.FormatTitle());
    }

    private static string Get2024HalfFeatBaseDescription(
        string descriptionKey,
        FeatDefinition fallbackFeat = null,
        string fallbackDescription = null)
    {
        var localizedDescription = string.IsNullOrEmpty(descriptionKey) ? null : Gui.Localize(descriptionKey);

        return GetHalfFeatBaseDescription(localizedDescription, fallbackDescription ?? fallbackFeat?.FormatDescription());
    }

    private static FeatDefinition Build2024HalfFeatVariant(
        FeatDefinition sourceDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string attribute,
        string groupTitle,
        string baseDescription,
        int? prerequisiteValue = 13,
        bool clearAbilityPrerequisite = false,
        string explicitTitle = null,
        params FeatureDefinition[] extraFeatures)
    {
        var feat = BuildHalfFeatVariant(
            sourceDefinition,
            name,
            attributeModifier,
            family,
            explicitTitle ?? Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute)),
            BuildHalfFeatDescription(attribute, baseDescription));

        if (extraFeatures != null)
        {
            foreach (var feature in extraFeatures.Where(feature => feature != null && !feat.Features.Contains(feature)))
            {
                feat.Features.Add(feature);
            }
        }

        if (clearAbilityPrerequisite)
        {
            ClearMinimalAbilityPrerequisite(feat);
        }
        else if (prerequisiteValue.HasValue)
        {
            OverrideMinimalAbilityPrerequisite(feat, attribute, prerequisiteValue.Value);
        }

        return feat;
    }

    private static FeatDefinition Build2024SingleHalfFeat(
        FeatDefinition sourceDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string attribute,
        string title,
        string baseDescription,
        bool hideFromFeats = false,
        int? prerequisiteValue = 13,
        bool clearAbilityPrerequisite = false,
        params FeatureDefinition[] extraFeatures)
    {
        var features = new List<FeatureDefinition> { attributeModifier };

        if (extraFeatures != null)
        {
            features.AddRange(extraFeatures.Where(feature => feature != null));
        }

        var feat = BuildExplicitIndependentFeat(
            sourceDefinition,
            name,
            family,
            title,
            BuildHalfFeatDescription(attribute, baseDescription),
            hideFromFeats,
            hidden: false,
            extraFeatures: [.. features.Distinct()]);

        if (clearAbilityPrerequisite)
        {
            ClearMinimalAbilityPrerequisite(feat);
        }
        else if (prerequisiteValue.HasValue)
        {
            OverrideMinimalAbilityPrerequisite(feat, attribute, prerequisiteValue.Value);
        }

        return feat;
    }

    private static FeatDefinition BuildFeyTouched2024Variant(
        string attribute,
        FeatureDefinitionAttributeModifier attributeModifier,
        string groupTitle,
        string baseDescription,
        SpellListDefinition choiceSpellList,
        SpellListDefinition mistyStepSpellList)
    {
        var name = $"FeatFeyTouched2024{attribute}";
        var title = Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute));
        var description = BuildHalfFeatDescription(attribute, baseDescription);
        var fixedCastSpell = FeatureDefinitionCastSpellBuilder
            .Create($"CastSpell{name}Fixed")
            .SetGuiPresentationNoContent(true)
            .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
            .SetSpellKnowledge(SpellKnowledge.FixedList)
            .SetSpellReadyness(SpellReadyness.AllKnown)
            .SetSlotsRecharge(RechargeRate.LongRest)
            .SetSlotsPerLevel(Touched2024FixedCastingSlots)
            .SetUniqueLevelSlots(false)
            .SetSpellList(mistyStepSpellList)
            .SetSpellCastingAbility(attribute)
            .AddCustomSubFeatures(new FeatHelpers.SpellTag(FeyTouched2024FixedTag, forceFixedList: true))
            .AddToDB();
        var choiceCastSpell = FeatureDefinitionCastSpellBuilder
            .Create($"CastSpell{name}Choice")
            .SetGuiPresentationNoContent(true)
            .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
            .SetSpellKnowledge(SpellKnowledge.Selection)
            .SetSpellReadyness(SpellReadyness.AllKnown)
            .SetSlotsRecharge(RechargeRate.LongRest)
            .SetSlotsPerLevel(SharedSpellsContext.InitiateCastingSlots)
            .SetKnownSpells(1, FeatureDefinitionCastSpellBuilder.CasterProgression.Flat)
            .SetReplacedSpells(1, 0)
            .SetUniqueLevelSlots(false)
            .SetSpellList(choiceSpellList)
            .SetSpellCastingAbility(attribute)
            .AddCustomSubFeatures(new FeatHelpers.SpellTag(FeyTouched2024ChoiceTag))
            .AddToDB();
        var pointPool = FeatureDefinitionPointPoolBuilder
            .Create($"PointPool{name}Spell")
            .SetGuiPresentationNoContent(true)
            .SetSpellOrCantripPool(
                HeroDefinitions.PointsPoolType.Spell,
                1,
                choiceSpellList,
                FeyTouched2024ChoiceTag,
                1,
                1)
            .AddToDB();
        var feat = FeatDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(title, description, hidden: false)
            .SetFeatures(attributeModifier, fixedCastSpell, choiceCastSpell, pointPool)
            .SetFeatFamily(FeyTouched2024Family)
            .AddCustomSubFeatures(FeatsContext.HideFromFeats.Marker)
            .AddToDB();

        feat.mustCastSpellsPrerequisite = false;
        ClearMinimalAbilityPrerequisite(feat);

        return feat;
    }

    private static FeatDefinition BuildDedicated2024SingleHalfFeat(
        FeatDefinition sourceDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string attribute,
        string title,
        string baseDescription,
        bool hideFromFeats = false,
        int? prerequisiteValue = 13,
        bool clearAbilityPrerequisite = false,
        params FeatureDefinition[] extraFeatures)
    {
        if (!sourceDefinition)
        {
            return null;
        }

        var features = new List<FeatureDefinition> { attributeModifier };

        if (extraFeatures != null)
        {
            features.AddRange(extraFeatures.Where(feature => feature != null));
        }

        var featureArray = features.Distinct().ToArray();
        FeatDefinition featDefinition;

        if (sourceDefinition is FeatDefinitionWithPrerequisites featDefinitionWithPrerequisites)
        {
            featDefinition = FeatDefinitionWithPrerequisitesBuilder
                .Create(name)
                .SetGuiPresentation(title, BuildHalfFeatDescription(attribute, baseDescription), sourceDefinition, false)
                .SetFeatures(featureArray)
                .SetValidators(featDefinitionWithPrerequisites.Validators.Distinct().ToArray())
                .AddToDB();
        }
        else
        {
            featDefinition = FeatDefinitionBuilder
                .Create(name)
                .SetGuiPresentation(title, BuildHalfFeatDescription(attribute, baseDescription), sourceDefinition, false)
                .SetFeatures(featureArray)
                .AddToDB();
        }

        MergeFeatPrerequisites(sourceDefinition, featDefinition, false);

        if (clearAbilityPrerequisite)
        {
            ClearMinimalAbilityPrerequisite(featDefinition);
        }
        else if (prerequisiteValue.HasValue)
        {
            OverrideMinimalAbilityPrerequisite(featDefinition, attribute, prerequisiteValue.Value);
        }

        if (hideFromFeats)
        {
            featDefinition.AddCustomSubFeatures(FeatsContext.HideFromFeats.Marker);
        }

        if (string.IsNullOrEmpty(family))
        {
            featDefinition.hasFamilyTag = false;
            featDefinition.familyTag = string.Empty;
        }
        else
        {
            featDefinition.hasFamilyTag = true;
            featDefinition.familyTag = family;
        }

        return featDefinition;
    }

    private static FeatDefinition BuildValidatedCopied2024HalfFeatVariant(
        FeatDefinition sourceDefinition,
        string name,
        string family,
        string attribute,
        string groupTitle,
        string baseDescription,
        Func<FeatDefinitionWithPrerequisites, RulesetCharacterHero, (bool result, string output)> validator)
    {
        if (!sourceDefinition)
        {
            return null;
        }

        var validators = sourceDefinition is FeatDefinitionWithPrerequisites featDefinitionWithPrerequisites
            ? featDefinitionWithPrerequisites.Validators.Concat([validator]).Distinct().ToArray()
            : [validator];
        var featDefinition = FeatDefinitionWithPrerequisitesBuilder
            .Create(name)
            .SetGuiPresentation(
                Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute)),
                BuildHalfFeatDescription(attribute, baseDescription),
                sourceDefinition,
                false)
            .SetFeatures(sourceDefinition.Features.ToArray())
            .SetValidators(validators)
            .AddToDB();

        MergeFeatPrerequisites(sourceDefinition, featDefinition, false);
        CopyFeatCustomSubFeatures(sourceDefinition, featDefinition, true);
        featDefinition.hasFamilyTag = true;
        featDefinition.familyTag = family;

        return featDefinition;
    }

    private static void PostConfigureManagedIndependentHalfFeats()
    {
        foreach (var featName in ManagedTabletopParentNameByDefinitionName.Keys.ToArray())
        {
            if (!TryGetDefinition<FeatDefinition>(featName, out var featDefinition) ||
                featDefinition.GetFirstSubFeatureOfType<IGroupedFeat>() != null ||
                !ManagedTabletopFeatKinds.TryGetValue(featName, out var kind) ||
                kind != TabletopFeatCatalogKind.GroupedChild ||
                !TryGetHalfFeatAttribute(featDefinition, out var attribute))
            {
                continue;
            }

            var canonicalName = GetCanonicalTabletopFeatName(featName);

            if (string.IsNullOrEmpty(canonicalName) || canonicalName == featName ||
                DedicatedStandaloneHalfFeat2024ByCanonicalName.ContainsKey(canonicalName) ||
                !ManagedTabletopParentNameByDefinitionName.TryGetValue(featName, out var parentName) ||
                !TryGetDefinition<FeatDefinition>(parentName, out var parentDefinition))
            {
                continue;
            }

            var canonicalParentName = GetCanonicalTabletopFeatName(parentDefinition.Name);
            var requiresAbilityPrerequisite =
                ManagedIndependentHalfFeatAbilityPrerequisiteCanonicalRoots.Contains(canonicalParentName);
            var clearsAbilityPrerequisite =
                ManagedIndependentHalfFeatWithoutAbilityPrerequisiteCanonicalRoots.Contains(canonicalParentName);

            NormalizeManagedHalfFeatVariant(
                featName,
                attribute,
                parentDefinition.GuiPresentation?.Title,
                Get2024HalfFeatBaseDescriptionKey(canonicalParentName),
                canonicalParentName,
                requiresAbilityPrerequisite ? 13 : null,
                clearsAbilityPrerequisite);
        }

#if DEBUG
        ValidateManagedHalfFeatCoverage();
#endif
    }

    private static void NormalizeManagedHalfFeatVariant(
        string featName,
        string attribute,
        string groupTitleKey,
        string groupDescriptionKey,
        string fallbackCanonicalSourceName = null,
        int? prerequisiteValue = 13,
        bool clearAbilityPrerequisite = false)
    {
        if (!TryGetDefinition<FeatDefinition>(featName, out var featDefinition))
        {
            return;
        }

        TryGetDefinition<FeatDefinition>(fallbackCanonicalSourceName, out var fallbackSourceFeat);

        var groupTitle = Get2024HalfFeatGroupTitle(groupTitleKey, fallbackSourceFeat, featDefinition.GuiPresentation?.Title);
        var baseDescription = Get2024HalfFeatBaseDescription(
            groupDescriptionKey,
            fallbackSourceFeat,
            featDefinition.GuiPresentation?.Description);

        featDefinition.GuiPresentation.title =
            Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute));
        featDefinition.GuiPresentation.description = BuildHalfFeatDescription(attribute, baseDescription);
        featDefinition.GuiPresentation.hidden = false;

        if (clearAbilityPrerequisite)
        {
            ClearMinimalAbilityPrerequisite(featDefinition);
        }
        else if (prerequisiteValue.HasValue)
        {
            OverrideMinimalAbilityPrerequisite(featDefinition, attribute, prerequisiteValue.Value);
        }
    }

    private static bool TryGetHalfFeatAttribute(FeatDefinition featDefinition, out string attribute)
    {
        attribute = null;

        if (featDefinition == null)
        {
            return false;
        }

        foreach (var candidate in new[]
                 {
                     AttributeDefinitions.Strength,
                     AttributeDefinitions.Dexterity,
                     AttributeDefinitions.Constitution,
                     AttributeDefinitions.Intelligence,
                     AttributeDefinitions.Wisdom,
                     AttributeDefinitions.Charisma
                 })
        {
            if (!featDefinition.Features.Contains(GetHalfFeatAttributeModifier(candidate)))
            {
                continue;
            }

            attribute = candidate;

            break;
        }

        return attribute != null;
    }

#if DEBUG
    private static void ValidateManagedHalfFeatCoverage()
    {
        foreach (var featDefinition in ManagedTabletopFeatNames
                     .Select(name => TryGetDefinition<FeatDefinition>(name, out var feat) ? feat : null)
                     .Where(feat => feat != null &&
                                    feat.GetFirstSubFeatureOfType<IGroupedFeat>() == null &&
                                    !IsNonSelectableTabletopGroup(feat) &&
                                    !IsTabletopContainerGroup(feat) &&
                                    TryGetHalfFeatAttribute(feat, out _)))
        {
            if (!TryGetHalfFeatAttribute(featDefinition, out var attribute))
            {
                continue;
            }

            var localizedDescription = Gui.Localize(featDefinition.GuiPresentation.Description);
            var hasResolvedDescription = !string.IsNullOrWhiteSpace(localizedDescription) &&
                                         !localizedDescription.Contains("/&");
            var requiresAbilityPrerequisite =
                ManagedTabletopParentNameByDefinitionName.TryGetValue(featDefinition.Name, out var parentName) &&
                ManagedIndependentHalfFeatAbilityPrerequisiteCanonicalRoots.Contains(GetCanonicalTabletopFeatName(parentName));
            var clearsAbilityPrerequisite =
                ManagedTabletopParentNameByDefinitionName.TryGetValue(featDefinition.Name, out parentName) &&
                ManagedIndependentHalfFeatWithoutAbilityPrerequisiteCanonicalRoots.Contains(
                    GetCanonicalTabletopFeatName(parentName));

            if (!hasResolvedDescription ||
                !featDefinition.minimalAbilityScorePrerequisite && requiresAbilityPrerequisite ||
                featDefinition.minimalAbilityScorePrerequisite && clearsAbilityPrerequisite)
            {
                Main.Log($"ValidateManagedHalfFeatCoverage: {featDefinition.Name} may require normalization.");
            }
        }
    }
#endif

    private static string StripAttributeVariantSuffix(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var trimmedTitle = title.Trim();
        var suffixIndex = System.Math.Max(trimmedTitle.LastIndexOf('['), trimmedTitle.LastIndexOf('［'));

        return suffixIndex > 0 && (trimmedTitle.EndsWith("]") || trimmedTitle.EndsWith("］"))
            ? trimmedTitle.Substring(0, suffixIndex).TrimEnd()
            : trimmedTitle;
    }

    private static string GetAttributeTitle(string attribute)
    {
        var title = Gui.Localize($"Attribute/&{attribute}TitleLong");

        return string.IsNullOrEmpty(title) || title.Contains("/&")
            ? Gui.Localize($"Attribute/&{attribute}Title")
            : title;
    }

    private static FeatureDefinitionAttributeModifier GetHalfFeatAttributeModifier(string attribute)
    {
        return attribute switch
        {
            AttributeDefinitions.Strength => AttributeModifierCreed_Of_Einar,
            AttributeDefinitions.Dexterity => AttributeModifierCreed_Of_Misaye,
            AttributeDefinitions.Constitution => AttributeModifierCreed_Of_Arun,
            AttributeDefinitions.Intelligence => AttributeModifierCreed_Of_Pakri,
            AttributeDefinitions.Wisdom => AttributeModifierCreed_Of_Maraike,
            AttributeDefinitions.Charisma => AttributeModifierCreed_Of_Solasta,
            _ => null
        };
    }

    private static FeatDefinition BuildDedicatedAbilityPrerequisiteHalfFeat(
        FeatDefinition sourceDefinition,
        string name,
        FeatureDefinitionAttributeModifier attributeModifier,
        string family,
        string attribute,
        string title,
        string baseDescription,
        int prerequisiteValue = 13,
        params FeatureDefinition[] extraFeatures)
    {
        if (!sourceDefinition)
        {
            return null;
        }

        var features = new List<FeatureDefinition> { attributeModifier };

        if (extraFeatures != null)
        {
            features.AddRange(extraFeatures.Where(feature => feature != null));
        }

        var featureArray = features.Distinct().ToArray();
        FeatDefinition featDefinition;

        if (sourceDefinition is FeatDefinitionWithPrerequisites featDefinitionWithPrerequisites)
        {
            featDefinition = FeatDefinitionWithPrerequisitesBuilder
                .Create(name)
                .SetGuiPresentation(title, BuildHalfFeatDescription(attribute, baseDescription), sourceDefinition, false)
                .SetFeatures(featureArray)
                .SetValidators(featDefinitionWithPrerequisites.Validators.Distinct().ToArray())
                .AddToDB();
        }
        else
        {
            featDefinition = FeatDefinitionBuilder
                .Create(name)
                .SetGuiPresentation(title, BuildHalfFeatDescription(attribute, baseDescription), sourceDefinition, false)
                .SetFeatures(featureArray)
                .AddToDB();
        }

        MergeFeatPrerequisites(sourceDefinition, featDefinition, false);
        OverrideMinimalAbilityPrerequisite(featDefinition, attribute, prerequisiteValue);

        if (string.IsNullOrEmpty(family))
        {
            featDefinition.hasFamilyTag = false;
            featDefinition.familyTag = string.Empty;
        }
        else
        {
            featDefinition.hasFamilyTag = true;
            featDefinition.familyTag = family;
        }

        return featDefinition;
    }

    private static FeatDefinition BuildExplicitIndependentFeat(
        FeatDefinition sourceDefinition,
        string name,
        string family = null,
        bool hideFromFeats = false,
        bool hidden = true,
        IEnumerable<FeatDefinition> inheritedPrerequisiteDefinitions = null,
        params FeatureDefinition[] extraFeatures)
    {
        return BuildExplicitIndependentFeat(
            sourceDefinition,
            name,
            family,
            sourceDefinition?.GuiPresentation?.Title,
            sourceDefinition?.GuiPresentation?.Description,
            hideFromFeats,
            hidden,
            false,
            inheritedPrerequisiteDefinitions,
            extraFeatures);
    }

    private static FeatDefinition BuildExplicitIndependentFeat(
        FeatDefinition sourceDefinition,
        string name,
        string family,
        string title,
        string description,
        bool hideFromFeats = false,
        bool hidden = true,
        bool clearMustCastSpellsPrerequisite = false,
        IEnumerable<FeatDefinition> inheritedPrerequisiteDefinitions = null,
        params FeatureDefinition[] extraFeatures)
    {
        if (!sourceDefinition)
        {
            return null;
        }

        var features = sourceDefinition.Features
            .Concat(extraFeatures ?? [])
            .Where(feature => feature != null)
            .Distinct()
            .ToArray();

        var prerequisiteSources = new List<FeatDefinition> { sourceDefinition };

        if (inheritedPrerequisiteDefinitions != null)
        {
            prerequisiteSources.AddRange(inheritedPrerequisiteDefinitions.Where(definition => definition != null));
        }

        prerequisiteSources = prerequisiteSources
            .Distinct()
            .ToList();

        FeatDefinition featDefinition;

        if (prerequisiteSources.OfType<FeatDefinitionWithPrerequisites>().Any())
        {
            var validators = prerequisiteSources
                .OfType<FeatDefinitionWithPrerequisites>()
                .SelectMany(definition => definition.Validators)
                .Where(validator => !clearMustCastSpellsPrerequisite ||
                                    !IsSpellcastingPrerequisiteValidator(validator))
                .Distinct()
                .ToArray();

            featDefinition = FeatDefinitionWithPrerequisitesBuilder
                .Create(name)
                .SetGuiPresentation(title, description, sourceDefinition, hidden)
                .SetFeatures(features)
                .SetValidators(validators)
                .AddToDB();
        }
        else
        {
            featDefinition = FeatDefinitionBuilder
                .Create(name)
                .SetGuiPresentation(title, description, sourceDefinition, hidden)
                .SetFeatures(features)
                .AddToDB();
        }

        foreach (var prerequisiteSource in prerequisiteSources)
        {
            MergeFeatPrerequisites(prerequisiteSource, featDefinition, clearMustCastSpellsPrerequisite);
        }

        if (clearMustCastSpellsPrerequisite)
        {
            featDefinition.mustCastSpellsPrerequisite = false;

            if (featDefinition is FeatDefinitionWithPrerequisites featDefinitionWithPrerequisites)
            {
                featDefinitionWithPrerequisites.Validators.RemoveAll(IsSpellcastingPrerequisiteValidator);
            }
        }

        CopyFeatCustomSubFeatures(sourceDefinition, featDefinition, hideFromFeats);

        var resolvedFamily = family ?? (sourceDefinition.HasFamilyTag ? sourceDefinition.FamilyTag : null);

        if (string.IsNullOrEmpty(resolvedFamily))
        {
            featDefinition.hasFamilyTag = false;
            featDefinition.familyTag = string.Empty;
        }
        else
        {
            featDefinition.hasFamilyTag = true;
            featDefinition.familyTag = resolvedFamily;
        }

        return featDefinition;
    }

    private static void MigrateRenamedManagedTabletopFeatSettingNames()
    {
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatEnabled,
            LegacyGreatWeaponMaster2024SettingName,
            GreatWeaponMaster2024FeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatEnabled,
            LegacySharpshooter2024SettingName,
            Sharpshooter2024FeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatEnabled,
            LegacyHeavilyArmored2024SettingName,
            HeavilyArmored2024GroupFeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatEnabled,
            LegacyHeavyArmorMaster2024SettingName,
            HeavyArmorMaster2024GroupFeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatGroupEnabled,
            LegacyGreatWeaponMaster2024SettingName,
            GreatWeaponMaster2024FeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatGroupEnabled,
            LegacySharpshooter2024SettingName,
            Sharpshooter2024FeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatGroupEnabled,
            LegacyHeavilyArmored2024SettingName,
            HeavilyArmored2024GroupFeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.FeatGroupEnabled,
            LegacyHeavyArmorMaster2024SettingName,
            HeavyArmorMaster2024GroupFeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.TabletopFeats2024Initialized,
            LegacyGreatWeaponMaster2024SettingName,
            GreatWeaponMaster2024FeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.TabletopFeats2024Initialized,
            LegacySharpshooter2024SettingName,
            Sharpshooter2024FeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.TabletopFeats2024Initialized,
            LegacyHeavilyArmored2024SettingName,
            HeavilyArmored2024GroupFeatName);
        MigrateRenamedManagedTabletopFeatSettingName(
            Main.Settings.TabletopFeats2024Initialized,
            LegacyHeavyArmorMaster2024SettingName,
            HeavyArmorMaster2024GroupFeatName);
    }

    private static void MigrateRenamedManagedTabletopFeatSettingName(
        List<string> settingNames,
        string legacyName,
        string currentName)
    {
        if (settingNames == null || string.IsNullOrEmpty(legacyName) || string.IsNullOrEmpty(currentName))
        {
            return;
        }

        if (settingNames.Remove(legacyName) && !settingNames.Contains(currentName))
        {
            settingNames.Add(currentName);
        }
    }

    private static void MergeFeatPrerequisites(
        FeatDefinition sourceDefinition,
        FeatDefinition featDefinition,
        bool clearMustCastSpellsPrerequisite)
    {
        if (!sourceDefinition || !featDefinition)
        {
            return;
        }

        if (sourceDefinition.minimalAbilityScorePrerequisite &&
            (!featDefinition.minimalAbilityScorePrerequisite ||
             sourceDefinition.minimalAbilityScoreValue > featDefinition.minimalAbilityScoreValue))
        {
            featDefinition.minimalAbilityScorePrerequisite = true;
            featDefinition.minimalAbilityScoreName = sourceDefinition.minimalAbilityScoreName;
            featDefinition.minimalAbilityScoreValue = sourceDefinition.minimalAbilityScoreValue;
        }

        featDefinition.mustCastSpellsPrerequisite |= !clearMustCastSpellsPrerequisite &&
                                                     sourceDefinition.mustCastSpellsPrerequisite;

        foreach (var requiredFeat in sourceDefinition.knownFeatsPrerequisite)
        {
            featDefinition.knownFeatsPrerequisite.TryAdd(requiredFeat);
        }

        if (sourceDefinition.armorProficiencyPrerequisite)
        {
            featDefinition.armorProficiencyPrerequisite = true;

            if (string.IsNullOrEmpty(featDefinition.armorProficiencyCategory))
            {
                featDefinition.armorProficiencyCategory = sourceDefinition.armorProficiencyCategory;
            }
        }
    }

    private static bool IsSpellcastingPrerequisiteValidator(
        Func<FeatDefinitionWithPrerequisites, RulesetCharacterHero, (bool result, string output)> validator)
    {
        if (validator == null)
        {
            return false;
        }

        return ContainsSpellcastingPrerequisiteToken(validator.Method?.Name) ||
               ContainsSpellcastingPrerequisiteToken(validator.Method?.DeclaringType?.Name) ||
               ContainsSpellcastingPrerequisiteToken(validator.Target?.GetType().Name);
    }

    private static bool ContainsSpellcastingPrerequisiteToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.IndexOf("spell", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("cast", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("pact", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void OverrideMinimalAbilityPrerequisite(
        FeatDefinition featDefinition,
        string attribute,
        int value)
    {
        if (!featDefinition)
        {
            return;
        }

        featDefinition.minimalAbilityScorePrerequisite = true;
        featDefinition.minimalAbilityScoreName = attribute;
        featDefinition.minimalAbilityScoreValue = value;
    }

    private static void ClearMinimalAbilityPrerequisite(FeatDefinition featDefinition)
    {
        if (!featDefinition)
        {
            return;
        }

        featDefinition.minimalAbilityScorePrerequisite = false;
        featDefinition.minimalAbilityScoreName = string.Empty;
        featDefinition.minimalAbilityScoreValue = 0;
    }

    private static void CopyFeatCustomSubFeatures(
        FeatDefinition sourceDefinition,
        FeatDefinition featDefinition,
        bool hideFromFeats)
    {
        if (!sourceDefinition || !featDefinition)
        {
            return;
        }

        foreach (var subFeature in sourceDefinition.GetCustomSubFeatures()
                     .Where(subFeature => subFeature is not GroupedFeat)
                     .Where(subFeature => subFeature is not FeatsContext.HideFromFeats))
        {
            featDefinition.AddCustomSubFeatures(subFeature);
        }

        if (hideFromFeats)
        {
            featDefinition.AddCustomSubFeatures(FeatsContext.HideFromFeats.Marker);
        }
    }

    private static FeatDefinition BuildManagedGroup(
        string name,
        string family,
        string title,
        string description,
        IEnumerable<FeatDefinition> feats)
    {
        var group = FeatDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(title, description, hidden: true)
            .AddCustomSubFeatures(new GroupedFeat(feats))
            .SetFeatFamily(family)
            .SetFeatures()
            .AddToDB();

        GroupFeats.Groups.Add(group);

        return group;
    }

    private static FeatDefinition BuildManagedGroupWithValidator(
        string name,
        string family,
        string title,
        string description,
        Func<FeatDefinitionWithPrerequisites, RulesetCharacterHero, (bool result, string output)> validator,
        IEnumerable<FeatDefinition> feats)
    {
        var group = FeatDefinitionWithPrerequisitesBuilder
            .Create(name)
            .SetGuiPresentation(title, description, hidden: true)
            .AddCustomSubFeatures(new GroupedFeat(feats))
            .SetFeatFamily(family)
            .SetFeatures()
            .SetValidators(validator)
            .AddToDB();

        GroupFeats.Groups.Add(group);

        return group;
    }

    private static FeatDefinition BuildSpellSniper2024Variant(
        string attribute,
        FeatureDefinitionAttributeModifier attributeModifier,
        string groupTitle,
        string groupDescription,
        SpellListDefinition spellList)
    {
        var name = $"FeatSpellSniper2024{attribute}";
        var title = Gui.Format("Feat/&GeneralFeat2024VariantTitle", groupTitle, GetAttributeTitle(attribute));
        var description = BuildHalfFeatDescription(attribute, groupDescription);
        var castSpell = FeatureDefinitionCastSpellBuilder
            .Create($"CastSpell{name}")
            .SetGuiPresentationNoContent(true)
            .SetFocusType(FocusType.None)
            .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
            .SetSpellKnowledge(SpellKnowledge.Selection)
            .SetSpellReadyness(SpellReadyness.AllKnown)
            .SetSlotsRecharge(RechargeRate.LongRest)
            .SetSlotsPerLevel(SharedSpellsContext.RaceEmptyCastingSlots)
            .SetKnownCantrips(1, 1, FeatureDefinitionCastSpellBuilder.CasterProgression.Flat)
            .SetKnownSpells(0, FeatureDefinitionCastSpellBuilder.CasterProgression.Flat)
            .SetReplacedSpells(1, 0)
            .SetUniqueLevelSlots(false)
            .SetSpellList(spellList)
            .SetSpellCastingAbility(attribute)
            .AddCustomSubFeatures(new FeatHelpers.SpellTag(OtherFeats.FeatSpellSniperTag))
            .AddToDB();
        var combatAffinity = FeatureDefinitionCombatAffinityBuilder
            .Create($"CombatAffinity{name}")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(new ValidateContextInsteadOfRestrictedProperty((_, _, _, _, _, mode, _) =>
                (OperationType.Set,
                    mode.sourceDefinition is SpellDefinition &&
                    mode.EffectDescription.RangeType == RangeType.RangeHit)))
            .SetIgnoreCover()
            .AddToDB();
        var pointPool = FeatureDefinitionPointPoolBuilder
            .Create($"PointPool{name}Cantrip")
            .SetGuiPresentationNoContent(true)
            .SetSpellOrCantripPool(HeroDefinitions.PointsPoolType.Cantrip, 1, spellList, OtherFeats.FeatSpellSniperTag)
            .AddToDB();

        return FeatDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(title, description, hidden: false)
            .SetFeatures(attributeModifier, combatAffinity, castSpell, pointPool)
            .SetFeatFamily(SpellSniper2024Family)
            .SetMustCastSpellsPrerequisite()
            .AddCustomSubFeatures(new ModifyEffectDescriptionSpellSniper2024(), FeatsContext.HideFromFeats.Marker)
            .AddToDB();
    }

    private static void RegisterManagedTabletopFeats(bool is2024, params FeatDefinition[] feats)
    {
        foreach (var feat in feats)
        {
            if (feat != null)
            {
                ManagedTabletopFeatNames.Add(feat.Name);

                if (is2024)
                {
                    TabletopFeat2024Names.Add(feat.Name);
                }
            }
        }
    }

    internal static IEnumerable<FeatDefinition> GetManagedStandaloneTabletopFeats()
    {
        return ManagedStandaloneTabletopFeats;
    }

    internal static bool IsFeatToggleAvailableInCurrentMode(FeatDefinition feat)
    {
        if (feat == null)
        {
            return false;
        }

        return Main.Settings.EnableTabletopFeatRules2024
            ? IsManagedTabletopFeat(feat) || IsTabletopContainerGroup(feat)
            : !IsManagedTabletopFeat(feat);
    }

    internal static bool? GetFeatToggleValueOverrideInCurrentMode(FeatDefinition feat)
    {
        return IsFeatToggleAvailableInCurrentMode(feat) ? null : false;
    }

    internal static bool Is2024TabletopFeat(BaseDefinition definition)
    {
        return definition is FeatDefinition featDefinition &&
               TabletopFeat2024Names.Contains(featDefinition.Name);
    }

    internal static bool IsManagedTabletopFeat(BaseDefinition definition)
    {
        return definition is FeatDefinition featDefinition &&
               ManagedTabletopFeatNames.Contains(featDefinition.Name);
    }

    internal static bool IsOptInOnlyManagedTabletopFeat(FeatDefinition feat)
    {
        return feat != null &&
               IsManagedTabletopFeat(feat) &&
               IsOptInOnlyManagedTabletopCanonicalName(GetCanonicalTabletopFeatName(feat.Name));
    }

    internal static bool HasEquivalentTrainedFeat(RulesetCharacterHero hero, FeatDefinition feat)
    {
        if (hero?.TrainedFeats == null || feat == null)
        {
            return false;
        }

        foreach (var trainedFeat in hero.TrainedFeats)
        {
            if (trainedFeat == null)
            {
                continue;
            }

            if (trainedFeat == feat || trainedFeat.Name == feat.Name)
            {
                return true;
            }

            if (Main.Settings.EnableTabletopFeatRules2024 &&
                AreEquivalentTabletopFeatNames(trainedFeat.Name, feat.Name))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSelectableTabletopFeatLeaf(FeatDefinition feat)
    {
        return feat != null &&
               !IsNonSelectableTabletopGroup(feat) &&
               !IsTabletopContainerGroup(feat) &&
               feat.GetFirstSubFeatureOfType<IGroupedFeat>() == null;
    }

    internal static bool IsSelectableManagedTabletopFeatLeaf(FeatDefinition feat)
    {
        return IsManagedTabletopFeat(feat) &&
               IsSelectableTabletopFeatLeaf(feat);
    }

    internal static bool IsDisplayableManagedTabletopLeaf(FeatDefinition feat)
    {
        return IsSelectableManagedTabletopFeatLeaf(feat) &&
               GetCanonicalTabletopFeatName(feat.Name) != "FeatSkilled";
    }

    private static bool TryAddDisplayableManagedTabletopFeat(
        ICollection<FeatDefinition> feats,
        FeatDefinition feat)
    {
        if (feats == null ||
            !IsDisplayableManagedTabletopLeaf(feat))
        {
            return false;
        }

        feat.GuiPresentation.hidden = false;

        if (feats.Any(existingFeat =>
                existingFeat &&
                AreEquivalentTabletopFeatNames(existingFeat.Name, feat.Name)))
        {
            return false;
        }

        feats.Add(feat);

        return true;
    }

    private static bool IsValidGreatWeaponMasterAttack(RulesetCharacter character, RulesetAttackMode attackMode)
    {
        if (attackMode?.SourceDefinition is not ItemDefinition itemDefinition)
        {
            return false;
        }

        return itemDefinition.WeaponDescription != null &&
               ValidatorsWeapon.HasAnyWeaponTag(itemDefinition, TagsDefinitions.WeaponTagHeavy) &&
               attackMode.ActionType == ActionType.Main &&
               GameLocationCharacter.GetFromActor(character)?.IsMyTurn() == true;
    }

    private static bool IsMeleeAttackRollMode(RulesetAttackMode attackMode)
    {
        return attackMode != null &&
               ValidatorsWeapon.IsMeleeOrUnarmed(attackMode);
    }

    private static bool IsMeleeWeaponAttackMode(RulesetAttackMode attackMode)
    {
        return attackMode?.SourceObject is RulesetItem rulesetItem &&
               ValidatorsWeapon.IsMelee(null, rulesetItem, null);
    }

    private static bool IsRangedAttackWithWeapon(RulesetAttackMode attackMode)
    {
        return attackMode?.SourceDefinition is ItemDefinition itemDefinition &&
               itemDefinition.WeaponDescription != null &&
               (attackMode.Ranged || attackMode.Thrown);
    }

    private static bool IsRangedWeaponAttackMode(
        RulesetAttackMode attackMode,
        RulesetItem _,
        RulesetCharacter __)
    {
        return attackMode?.SourceDefinition is ItemDefinition itemDefinition &&
               itemDefinition.WeaponDescription?.WeaponTypeDefinition?.WeaponProximity == AttackProximity.Range;
    }

    private static bool CanUseCharger2024Shove(GameLocationCharacter attacker, GameLocationCharacter defender)
    {
        return attacker?.RulesetCharacter?.SizeDefinition != null &&
               defender?.RulesetCharacter?.SizeDefinition != null &&
               defender.RulesetCharacter.SizeDefinition.WieldingSize <=
               attacker.RulesetCharacter.SizeDefinition.WieldingSize + 1;
    }

    private static bool HasShieldMaster2024InterposeDamage(SavingThrowData savingThrowData)
    {
        return savingThrowData?.EffectDescription?.EffectForms?.Exists(
            effectForm => effectForm.FormType == EffectForm.EffectFormType.Damage) == true;
    }

    private static bool IsVisibleBySettings(FeatDefinition feat)
    {
        if (feat == null)
        {
            return false;
        }

        if (TryGetVisibilityFromSettings(feat, out var visible) && !visible)
        {
            return false;
        }

        var canonicalDefinition = default(FeatDefinition);
        var canonicalName = GetCanonicalTabletopFeatName(feat.Name);

        if (!string.IsNullOrEmpty(canonicalName) &&
            canonicalName != feat.Name &&
            TryGetDefinition<FeatDefinition>(canonicalName, out canonicalDefinition))
        {
            if (FightingStyleContext.HideFightingStyle(canonicalDefinition))
            {
                return false;
            }
        }
        else if (FightingStyleContext.HideFightingStyle(feat))
        {
            return false;
        }

        var currentName = feat.Name;
        var processedParents = new HashSet<string>();

        while (ManagedTabletopParentNameByDefinitionName.TryGetValue(currentName, out var parentName) &&
               processedParents.Add(parentName))
        {
            if (TryGetDefinition<FeatDefinition>(parentName, out var parentDefinition) &&
                TryGetVisibilityFromSettings(parentDefinition, out visible) &&
                !visible)
            {
                return false;
            }

            currentName = parentName;
        }

        if (Main.Settings.EnableTabletopFeatRules2024 &&
            IsManagedTabletopFeat(feat))
        {
            return true;
        }

        if (canonicalDefinition != null &&
            TryGetVisibilityFromSettings(canonicalDefinition, out visible) &&
            !visible)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetVisibilityFromSettings(FeatDefinition feat, out bool visible)
    {
        visible = true;

        if (feat == null)
        {
            visible = false;

            return true;
        }

        if (FeatsContext.Feats.Contains(feat))
        {
            visible = Main.Settings.FeatEnabled.Contains(feat.Name);

            return true;
        }

        if (FeatsContext.FeatGroups.Contains(feat))
        {
            visible = Main.Settings.FeatGroupEnabled.Contains(feat.Name);

            return true;
        }

        return false;
    }

    private static void SetFeatVisibility(FeatDefinition feat, bool visible)
    {
        if (feat == null)
        {
            return;
        }

        feat.GuiPresentation.hidden = !visible;
    }

#if DEBUG
    private static void LogLegacyFeatVisibilityState()
    {
        var visibleGroups = FeatsContext.FeatGroups.Count(feat => feat is { GuiPresentation.hidden: false });
        var visibleFeats = FeatsContext.Feats.Count(feat => feat is { GuiPresentation.hidden: false });
        var visibleHideFromFeats = FeatsContext.Feats.Count(feat =>
            feat is { GuiPresentation.hidden: false } &&
            feat.HasSubFeatureOfType<FeatsContext.HideFromFeats>());
        var visibleManagedFeats = FeatsContext.Feats
            .Concat(FeatsContext.FeatGroups)
            .Where(feat => feat != null)
            .Distinct()
            .Count(feat => IsManagedTabletopFeat(feat) && !feat.GuiPresentation.hidden);

        Main.Log(
            $"SwitchTabletopFeatRules2024(false): enabled={Main.Settings.EnableTabletopFeatRules2024}, " +
            $"containers={ManagedTabletopContainerGroupNames.Count}, visibleGroups={visibleGroups}, " +
            $"visibleFeats={visibleFeats}, visibleHideFromFeats={visibleHideFromFeats}, " +
            $"visibleManagedFeats={visibleManagedFeats}.");
    }
#endif

    private static bool IsDefinitionEnabledBySettings(FeatDefinition feat)
    {
        return TryGetVisibilityFromSettings(feat, out var visible) && visible;
    }

    private static void EnableDefinitionBySettings(FeatDefinition feat)
    {
        if (feat == null)
        {
            return;
        }

        if (FeatsContext.Feats.Contains(feat))
        {
            Main.Settings.FeatEnabled.TryAdd(feat.Name);
        }

        if (FeatsContext.FeatGroups.Contains(feat))
        {
            Main.Settings.FeatGroupEnabled.TryAdd(feat.Name);
        }
    }

    internal sealed class AttackOnlyReduceDamageMarker
    {
        internal static readonly AttackOnlyReduceDamageMarker Marker = new();
    }

    private sealed class TabletopFeat2024Profile(
        FeatDefinition replacement,
        IReadOnlyList<FeatDefinition> legacyFeats,
        IReadOnlyList<FeatDefinition> targetGroups)
    {
        internal string ReplacementName => replacement?.Name;
        internal FeatDefinition Replacement => replacement;
        internal IReadOnlyList<FeatDefinition> LegacyFeats => legacyFeats;
        internal IReadOnlyList<FeatDefinition> TargetGroups => targetGroups;

        internal void Apply(bool use2024)
        {
            InitializeToggle();

            var replacementVisible = use2024 && Tabletop2024Context.IsVisibleBySettings(replacement);

            foreach (var legacyFeat in legacyFeats)
            {
                if (use2024)
                {
                    Tabletop2024Context.SetFeatVisibility(legacyFeat, false);
                }
            }

            Tabletop2024Context.SetFeatVisibility(replacement, replacementVisible);
        }

        private void InitializeToggle()
        {
            if (replacement == null ||
                Main.Settings.TabletopFeats2024Initialized.Contains(replacement.Name))
            {
                return;
            }

            if (!Tabletop2024Context.ShouldSuppressLegacyAutoEnable(
                    Tabletop2024Context.GetCanonicalTabletopFeatName(replacement.Name)) &&
                legacyFeats.Any(IsDefinitionEnabledBySettings))
            {
                EnableDefinitionBySettings(replacement);
            }

            Main.Settings.TabletopFeats2024Initialized.TryAdd(replacement.Name);
        }
    }

    private sealed class ModifyEffectDescriptionSpellSniper2024 : IModifyEffectDescription
    {
        public bool IsValid(
            BaseDefinition definition,
            RulesetCharacter character,
            EffectDescription effectDescription)
        {
            return definition is SpellDefinition &&
                   effectDescription.rangeType == RangeType.RangeHit &&
                   effectDescription.rangeParameter >= 2;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            effectDescription.rangeParameter += SpellSniper2024RangeIncreaseCells;

            return effectDescription;
        }
    }

    private sealed class ActionFinishedByMeAfterDash(ConditionDefinition conditionAfterDash) : IActionFinishedByMe
    {
        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (action is not CharacterActionDash ||
                action.ActingCharacter?.IsMyTurn() != true)
            {
                yield break;
            }

            var rulesetCharacter = action.ActingCharacter.RulesetCharacter;

            if (rulesetCharacter.HasConditionOfType(conditionAfterDash.Name))
            {
                yield break;
            }

            rulesetCharacter.InflictCondition(
                conditionAfterDash.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetCharacter.guid,
                rulesetCharacter.CurrentFaction.Name,
                1,
                conditionAfterDash.Name,
                0,
                0,
                0);
        }
    }

    private sealed class CustomBehaviorCharger2024(
        FeatureDefinitionPower powerPool,
        FeatureDefinitionPower powerShove)
        : IPhysicalAttackBeforeHitConfirmedOnEnemy, IMoveStepStarted
    {
        private const string DirX = "DirectionXCharger2024";
        private const string DirY = "DirectionYCharger2024";
        private const string DirZ = "DirectionZCharger2024";
        private const string StraightLine = "StraightLineCharger2024";

        private static readonly EffectForm ShoveForm = EffectFormBuilder
            .Create()
            .SetMotionForm(MotionForm.MotionType.PushFromOrigin, 2)
            .Build();

        public void MoveStepStarted(GameLocationCharacter mover, int3 source, int3 destination)
        {
            InitDirections(mover);

            var previousDirectionX = mover.UsedSpecialFeatures[DirX];
            var previousDirectionY = mover.UsedSpecialFeatures[DirY];
            var previousDirectionZ = mover.UsedSpecialFeatures[DirZ];

            var directionX = Math.Sign(source.x - destination.x);
            var directionY = Math.Sign(source.y - destination.y);
            var directionZ = Math.Sign(source.z - destination.z);

            mover.UsedSpecialFeatures[DirX] = directionX;
            mover.UsedSpecialFeatures[DirY] = directionY;
            mover.UsedSpecialFeatures[DirZ] = directionZ;
            mover.UsedSpecialFeatures[StraightLine] =
                previousDirectionX == directionX &&
                previousDirectionY == directionY &&
                previousDirectionZ == directionZ
                    ? mover.UsedSpecialFeatures[StraightLine] + 1
                    : 1;
        }

        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier actionModifier,
            RulesetAttackMode attackMode,
            bool rangedAttack,
            AdvantageType advantageType,
            List<EffectForm> actualEffectForms,
            bool firstTarget,
            bool criticalHit)
        {
            if (attacker?.IsMyTurn() != true ||
                attackMode?.ActionType != ActionType.Main ||
                !IsMeleeAttackRollMode(attackMode) ||
                !attacker.OnceInMyTurnIsValid(powerPool.Name))
            {
                yield break;
            }

            var attackerPosition = attacker.LocationPosition;
            var defenderPosition = defender.LocationPosition;
            var attackDirectionX = Math.Sign(attackerPosition.x - defenderPosition.x);
            var attackDirectionY = Math.Sign(attackerPosition.y - defenderPosition.y);
            var attackDirectionZ = Math.Sign(attackerPosition.z - defenderPosition.z);

            InitDirections(attacker);

            if (attackDirectionX != attacker.UsedSpecialFeatures[DirX] ||
                attackDirectionY != attacker.UsedSpecialFeatures[DirY] ||
                attackDirectionZ != attacker.UsedSpecialFeatures[DirZ] ||
                attacker.UsedSpecialFeatures[StraightLine] < 2)
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;
            var usablePower = PowerProvider.Get(powerPool, rulesetAttacker);
            var usableShove = PowerProvider.Get(powerShove, rulesetAttacker);
            var restoreShove = false;

            if (usableShove != null &&
                !CanUseCharger2024Shove(attacker, defender) &&
                rulesetAttacker.UsablePowers.Contains(usableShove))
            {
                rulesetAttacker.UsablePowers.Remove(usableShove);
                restoreShove = true;
            }

            yield return attacker.MyReactToSpendPowerBundle(
                usablePower,
                [defender],
                attacker,
                "PowerFeatCharger",
                reactionValidated: ReactionValidated,
                battleManager: battleManager);

            if (restoreShove && !rulesetAttacker.UsablePowers.Contains(usableShove))
            {
                rulesetAttacker.UsablePowers.Add(usableShove);
            }

            yield break;

            void ReactionValidated(ReactionRequestSpendBundlePower reactionRequest)
            {
                attacker.UsedSpecialFeatures.TryAdd(powerPool.Name, 1);

                var subPowers = powerPool.GetBundle()?.SubPowers;

                if (subPowers == null ||
                    reactionRequest.SelectedSubOption < 0 ||
                    reactionRequest.SelectedSubOption >= subPowers.Count ||
                    subPowers[reactionRequest.SelectedSubOption].Name != powerShove.Name)
                {
                    return;
                }

                actualEffectForms.Add(ShoveForm);
            }
        }

        private static void InitDirections(GameLocationCharacter mover)
        {
            mover.UsedSpecialFeatures.TryAdd(DirX, 0);
            mover.UsedSpecialFeatures.TryAdd(DirY, 0);
            mover.UsedSpecialFeatures.TryAdd(DirZ, 0);
            mover.UsedSpecialFeatures.TryAdd(StraightLine, 0);
        }
    }

    private sealed class CustomBehaviorGreatWeaponMaster2024(ConditionDefinition conditionFinish)
        : IOnReducedToZeroHpByMe, IPhysicalAttackFinishedByMe
    {
        public IEnumerator HandleReducedToZeroHpByMe(
            GameLocationCharacter attacker,
            GameLocationCharacter downedCreature,
            RulesetAttackMode attackMode,
            RulesetEffect activeEffect)
        {
            if (attacker?.IsMyTurn() != true || !ValidateBonusAttack(attackMode))
            {
                yield break;
            }

            InflictCondition(attacker.RulesetCharacter);
        }

        public IEnumerator OnPhysicalAttackFinishedByMe(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            RollOutcome rollOutcome,
            int damageAmount)
        {
            if (attacker?.IsMyTurn() != true ||
                rollOutcome != RollOutcome.CriticalSuccess ||
                !ValidateBonusAttack(attackMode))
            {
                yield break;
            }

            InflictCondition(attacker.RulesetCharacter);
        }

        private void InflictCondition(RulesetCharacter rulesetCharacter)
        {
            rulesetCharacter.InflictCondition(
                conditionFinish.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetCharacter.guid,
                rulesetCharacter.CurrentFaction.Name,
                1,
                conditionFinish.Name,
                0,
                0,
                0);
        }

        private static bool ValidateBonusAttack(RulesetAttackMode attackMode)
        {
            return IsMeleeWeaponAttackMode(attackMode);
        }
    }

    private sealed class OnCharacterBattleStartedAlert2024 : ICharacterBattleStartedListener
    {
        public void OnCharacterBattleStarted(GameLocationCharacter locationCharacter, bool surprise)
        {
            var rulesetCharacter = locationCharacter?.RulesetCharacter;

            if (!surprise ||
                rulesetCharacter == null ||
                !rulesetCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagCombat,
                    ConditionSurprised,
                    out var activeCondition))
            {
                return;
            }

            rulesetCharacter.RemoveCondition(activeCondition);
        }
    }

    private sealed class ModifyDiceRollHitDiceHealer2024BattleMedic : IModifyDiceRollHitDice
    {
        private const string ConditionName = "ConditionFeatHealer2024BattleMedic";

        public void BeforeRoll(
            RulesetCharacterHero __instance,
            ref DieType die,
            ref int modifier,
            ref AdvantageType advantageType,
            ref bool healKindred,
            ref bool isBonus)
        {
            if (__instance.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect,
                    ConditionName,
                    out var activeCondition))
            {
                modifier = activeCondition.amount;
            }
        }
    }

    private sealed class FilterTargetingCharacterHealer2024BattleMedic : IFilterTargetingCharacter
    {
        public bool EnforceFullSelection => false;

        public bool IsValid(CursorLocationSelectTarget __instance, GameLocationCharacter target)
        {
            return target?.RulesetCharacter?.GetOriginalHero()?.RemainingHitDiceCount() > 0;
        }
    }

    private sealed class PowerOrSpellFinishedByMeHealer2024BattleMedic(
        ConditionDefinition conditionBattleMedic,
        FeatureDefinitionPower powerBattleMedic) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            if (action.Countered ||
                action.ExecutionFailed ||
                action.ActionParams.TargetCharacters.Count == 0)
            {
                yield break;
            }

            var healer = action.ActingCharacter;
            var target = action.ActionParams.TargetCharacters[0];
            var rulesetHealer = healer?.RulesetCharacter;
            var rulesetTarget = target?.RulesetCharacter?.GetOriginalHero();

            if (rulesetHealer == null ||
                rulesetTarget == null ||
                rulesetTarget.RemainingHitDiceCount() == 0)
            {
                yield break;
            }

            var proficiencyBonus = rulesetHealer.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);

            rulesetTarget.InflictCondition(
                conditionBattleMedic.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetHealer.guid,
                rulesetHealer.CurrentFaction.Name,
                1,
                conditionBattleMedic.Name,
                proficiencyBonus,
                0,
                0);

            EffectHelpers.StartVisualEffect(healer, target, CureWounds, EffectHelpers.EffectType.Effect);

            try
            {
                rulesetTarget.HitDieRolled += HitDieRolled;
                rulesetTarget.RollHitDie();
            }
            finally
            {
                rulesetTarget.HitDieRolled -= HitDieRolled;

                if (rulesetTarget.TryGetConditionOfCategoryAndType(
                        AttributeDefinitions.TagEffect,
                        conditionBattleMedic.Name,
                        out var activeCondition))
                {
                    rulesetTarget.RemoveCondition(activeCondition);
                }
            }

            yield break;

            void HitDieRolled(
                RulesetCharacter character,
                DieType dieType,
                int value,
                AdvantageType advantageType,
                int roll1,
                int roll2,
                int modifier,
                bool isBonus)
            {
                const string baseLine = "Feedback/&FeatHealer2024BattleMedicHitDieRolled";

                character.ShowDieRoll(
                    dieType,
                    roll1,
                    roll2,
                    advantage: advantageType,
                    title: powerBattleMedic.GuiPresentation.Title);

                character.LogCharacterActivatesAbility(
                    Gui.NoLocalization,
                    baseLine,
                    true,
                    extra:
                    [
                        (ConsoleStyleDuplet.ParameterType.AbilityInfo, Gui.FormatDieTitle(dieType)),
                        (ConsoleStyleDuplet.ParameterType.Positive, $"{value - modifier}+{modifier}"),
                        (ConsoleStyleDuplet.ParameterType.Positive, $"{value}")
                    ]);
            }
        }
    }

    private sealed class ModifyDiceRollLucky2024Advantage : IModifyDiceRoll
    {
        private const string ConditionName = "ConditionFeatLucky2024Advantage";

        public void BeforeRoll(
            RollContext rollContext,
            RulesetCharacter rulesetCharacter,
            ref DieType dieType,
            ref AdvantageType advantageType)
        {
            if (!IsValid(rollContext, rulesetCharacter))
            {
                return;
            }

            advantageType = advantageType == AdvantageType.Disadvantage
                ? AdvantageType.None
                : AdvantageType.Advantage;
        }

        public void AfterRoll(
            DieType dieType,
            AdvantageType advantageType,
            RollContext rollContext,
            RulesetCharacter rulesetCharacter,
            ref int firstRoll,
            ref int secondRoll,
            ref int result)
        {
            if (!IsValid(rollContext, rulesetCharacter) ||
                !rulesetCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect,
                    ConditionName,
                    out var activeCondition))
            {
                return;
            }

            rulesetCharacter.RemoveCondition(activeCondition);
        }

        private static bool IsValid(RollContext rollContext, RulesetCharacter rulesetCharacter)
        {
            return rollContext is RollContext.AttackRoll or RollContext.AbilityCheck or RollContext.SavingThrow &&
                   rulesetCharacter != null &&
                   rulesetCharacter.HasConditionOfCategoryAndType(AttributeDefinitions.TagEffect, ConditionName);
        }
    }

    private sealed class CustomBehaviorLucky2024(FeatureDefinitionPower powerPool)
        : IPhysicalAttackInitiatedOnMe, IMagicEffectAttackInitiatedOnMe
    {
        public IEnumerator OnPhysicalAttackInitiatedOnMe(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier attackModifier,
            RulesetAttackMode attackMode)
        {
            yield return TryUseLucky2024OnIncomingAttack(attacker, defender, attackModifier, battleManager);
        }

        public IEnumerator OnMagicEffectAttackInitiatedOnMe(
            CharacterActionMagicEffect action,
            RulesetEffect activeEffect,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier attackModifier,
            bool firstTarget,
            bool checkMagicalAttackDamage)
        {
            yield return TryUseLucky2024OnIncomingAttack(attacker, defender, attackModifier, null);
        }

        private IEnumerator TryUseLucky2024OnIncomingAttack(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier attackModifier,
            GameLocationBattleManager battleManager)
        {
            var rulesetDefender = defender?.RulesetCharacter;
            var usablePower = rulesetDefender == null ? null : PowerProvider.Get(powerPool, rulesetDefender);

            if (attacker == null ||
                defender == null ||
                attackModifier == null ||
                rulesetDefender is not { IsDeadOrDyingOrUnconscious: false } ||
                !defender.CanReact() ||
                usablePower == null ||
                rulesetDefender.GetRemainingUsesOfPower(usablePower) == 0)
            {
                yield break;
            }

            yield return defender.MyReactToSpendPower(
                usablePower,
                attacker,
                "Lucky2024Disadvantage",
                "UseLucky2024DisadvantageDescription".Formatted(Category.Reaction, attacker.Name),
                ReactionValidated,
                battleManager);

            yield break;

            void ReactionValidated()
            {
                usablePower.Consume();
                attackModifier.AttackAdvantageTrends.Add(
                    new TrendInfo(-1, FeatureSourceType.Power, powerPool.Name, powerPool));
            }
        }
    }

    internal sealed class SavageAttacker2024Marker
    {
    }

    private sealed class CustomBehaviorShieldMaster2024(
        FeatureDefinitionPower powerPool,
        FeatureDefinitionPower powerInterpose,
        ConditionDefinition conditionNoDamage)
        : IPhysicalAttackFinishedByMe, ITryAlterOutcomeSavingThrow
    {
        private const string ShieldBashSpecialFeatureName = "FeatureFeatShieldMaster2024ShieldBash";

        public IEnumerator OnPhysicalAttackFinishedByMe(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            RollOutcome rollOutcome,
            int damageAmount)
        {
            if (attacker?.IsMyTurn() != true ||
                attackMode?.ActionType != ActionType.Main ||
                rollOutcome is RollOutcome.Failure or RollOutcome.CriticalFailure ||
                !IsMeleeWeaponAttackMode(attackMode) ||
                !attacker.IsWithinRange(defender, 1) ||
                !attacker.OnceInMyTurnIsValid(ShieldBashSpecialFeatureName))
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;

            if (!rulesetAttacker.IsWearingShield())
            {
                yield break;
            }

            var usablePower = PowerProvider.Get(powerPool, rulesetAttacker);

            yield return attacker.MyReactToSpendPowerBundle(
                usablePower,
                [defender],
                attacker,
                powerPool.Name,
                reactionValidated: _ => attacker.UsedSpecialFeatures.TryAdd(ShieldBashSpecialFeatureName, 1),
                battleManager: battleManager);
        }

        public IEnumerator OnTryAlterOutcomeSavingThrow(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            SavingThrowData savingThrowData,
            bool hasHitVisual)
        {
            var rulesetDefender = defender?.RulesetCharacter;

            if (helper != defender ||
                defender?.CanReact() != true ||
                savingThrowData.SaveOutcome != RollOutcome.Success ||
                savingThrowData.SavingThrowAbility != AttributeDefinitions.Dexterity ||
                rulesetDefender is not { IsDeadOrDyingOrUnconscious: false } ||
                !rulesetDefender.IsWearingShield() ||
                !HasShieldMaster2024InterposeDamage(savingThrowData))
            {
                yield break;
            }

            var usablePower = PowerProvider.Get(powerInterpose, rulesetDefender);

            yield return defender.MyReactToSpendPower(
                usablePower,
                defender,
                "ShieldMaster2024",
                reactionValidated: ReactionValidated,
                battleManager: battleManager);

            yield break;

            void ReactionValidated()
            {
                defender.SpendActionType(ActionType.Reaction);

                rulesetDefender.InflictCondition(
                    conditionNoDamage.Name,
                    DurationType.Round,
                    0,
                    TurnOccurenceType.EndOfTurn,
                    AttributeDefinitions.TagEffect,
                    rulesetDefender.guid,
                    rulesetDefender.CurrentFaction.Name,
                    1,
                    conditionNoDamage.Name,
                    0,
                    0,
                    0);
            }
        }
    }
}
