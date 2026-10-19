using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using SolastaUnfinishedBusiness.Api.Infrastructure;
using SolastaUnfinishedBusiness.Displays;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Models.TranslationServices;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness;

public sealed class Core;

public enum TagType
{
    QoL,
    T2014,
    T2024,
    Roleplay
}

[AttributeUsage(AttributeTargets.Property)]
public class Tag : Attribute
{
    public TagType Type { get; set; }
}

// Gameplay settings are shared by default; mark personal preferences at their declaration.
[AttributeUsage(AttributeTargets.Property)]
public sealed class MultiplayerLocalAttribute : Attribute;

[Serializable]
[XmlRoot(ElementName = "Settings")]
public class Settings : UnityModManager.ModSettings
{
    //
    // UI Saved State
    //

    [MultiplayerLocal] public int DisplayModMessage { get; set; }
    [MultiplayerLocal] public int EnableDiagsDump { get; set; }
    [MultiplayerLocal] public int SelectedTab { get; set; }

    //
    // SETTINGS UI TOGGLES
    //

    [MultiplayerLocal] public bool DisplayMultiplayerToggle { get; set; }
    [MultiplayerLocal] public bool DisplayTabletop2024 { get; set; }
    [MultiplayerLocal] public bool DisplayTabletopToggle { get; set; }
    [MultiplayerLocal] public bool DisplayRacesToggle { get; set; }
    [MultiplayerLocal] public bool DisplaySubracesToggle { get; set; }
    [MultiplayerLocal] public bool DisplayBackgroundsToggle { get; set; }
    [MultiplayerLocal] public bool DisplayFeatsToggle { get; set; }
    [MultiplayerLocal] public bool DisplayFeatGroupsToggle { get; set; }
    [MultiplayerLocal] public bool DisplayFightingStylesToggle { get; set; }
    [MultiplayerLocal] public bool DisplayInvocationsToggle { get; set; }
    [MultiplayerLocal] public bool DisplayMetamagicToggle { get; set; }
    [MultiplayerLocal] public bool DisplayItemsGeneralToggle { get; set; }
    [MultiplayerLocal] public bool DisplayCraftingToggle { get; set; }
    [MultiplayerLocal] public bool DisplayItemsToggle { get; set; }
    [MultiplayerLocal] public bool DisplayBackgroundsAndRacesGeneralToggle { get; set; }
    [MultiplayerLocal] public bool DisplayProficienciesGeneralToggle { get; set; }
    [MultiplayerLocal] public bool DisplaySpellsGeneralToggle { get; set; }
    [MultiplayerLocal] public bool DisplaySubClassesGeneralToggle { get; set; }
    [MultiplayerLocal] public SerializableDictionary<string, bool> DisplayKlassToggle { get; set; } = [];
    [MultiplayerLocal] public SerializableDictionary<string, bool> DisplaySpellListsToggle { get; set; } = [];

    //
    // SETTINGS HIDDEN ON UI
    //

    [MultiplayerLocal] public List<string> DefaultPartyHeroes { get; } = [];
    [MultiplayerLocal] public bool EnableCtrlClickOnlySwapsMainHand { get; set; } = true;
    [MultiplayerLocal] public bool EnableDisplaySorceryPointBoxSorcererOnly { get; set; } = true;
    [MultiplayerLocal] public bool EnableLevelUpFeaturesSelection { get; set; } = true;
    [MultiplayerLocal] public bool EnableSameWidthFeatSelection { get; set; } = true;
    [MultiplayerLocal] public bool EnableSameWidthInvocationSelection { get; set; } = true;
    [MultiplayerLocal] public bool EnableSortingFightingStyles { get; set; } = true;
    [MultiplayerLocal] public bool EnableSortingSubclasses { get; set; } = true;
    [MultiplayerLocal] public bool FixAsianLanguagesTextWrap { get; set; } = true;
    [MultiplayerLocal] public bool KeepCharactersPanelOpenAndHeroSelectedAfterLevelUp { get; set; } = true;
    [MultiplayerLocal] public bool DisableStreamlinedMultiLevelUp { get; set; } = true;

    //
    // Gameplay - General
    //

    [MultiplayerLocal] public bool EnablePcgRandom { get; set; }
    [MultiplayerLocal] public bool EnableFastLocationLoads { get; set; }
    [MultiplayerLocal] public bool EnableCustomPortraits { get; set; } = true;
    [MultiplayerLocal] public bool DisableMultilineSpellOffering { get; set; }
    [MultiplayerLocal] public bool DisableUnofficialTranslations { get; set; } = true;
    [MultiplayerLocal] public bool FilterUnofficialTranslationsByLanguage { get; set; }
    [MultiplayerLocal] public List<string> EnabledUnofficialTranslationLanguages { get; } = [];
    [MultiplayerLocal] public bool WideScreenBattleUI { get; set; } = true;

    //
    // Gameplay - Rules
    //

    public bool EnableEpicPointsAndArray { get; set; }
    public bool ForcePointBuyAbilityScores { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableLevel20 { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableMulticlass { get; set; }
    public int MaxAllowedClasses { get; set; } = MulticlassContext.DefaultClasses;
    [MultiplayerLocal] public bool DisplayAllKnownSpellsDuringLevelUp { get; set; }
    [MultiplayerLocal] public bool DisplayPactSlotsOnSpellSelectionPanel { get; set; }
    public bool EnableMinInOutAttributes { get; set; }
    [MultiplayerLocal] public bool AlwaysSpendPactSlotsFirst { get; set; }
    [Tag(Type = TagType.QoL)] public bool EnableActionSwitching { get; set; }
    [Tag(Type = TagType.T2014)] public bool DontEndTurnAfterReady { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableProneAction { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableGrappleAction { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableHelpAction { get; set; }
    public bool EnableRespecAction { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableUnarmedMainAttackAction { get; set; }
    public bool EnableUnlimitedInventoryActions { get; set; }
    public bool AllowPartyFriendlyFire { get; set; }
    [Tag(Type = TagType.T2014)] public bool UseOfficialAdvantageDisadvantageRules { get; set; }
    [Tag(Type = TagType.T2014)] public bool PreventSavingThrowReactionsAfterLegendaryResistance { get; set; }
    public bool UseAlternateSpellPointsSystem { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSmiteSpells2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSpeciesBaseWalkSpeed2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSpeciesSpellcastingAbility2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableHalfOrcAdrenalineRush2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableHeavyWeaponAbilityRequirement2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableToolChecks2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool UseWeaponMasterySystem { get; set; }
    [Tag(Type = TagType.T2024)] public bool UseWeaponMasterySystemAddWeaponTag { get; set; }
    public bool UseWeaponMasteryMonkWayOfBlade { get; set; }
    public bool UseWeaponMasterySystemAddCleaveDamage { get; set; }
    public bool UseWeaponMasterySystemFlurryTriggersMastery { get; set; }
    public bool UseWeaponMasterySystemNickExtraAttackTriggersMastery { get; set; }
    public bool UseWeaponMasterySystemNickDualFlurry { get; set; }
    public bool UseWeaponMasterySystemPushSave { get; set; }
    [MultiplayerLocal] public bool WeaponMasterySystemCustomizeToggle { get; set; }
    public SerializableDictionary<string, Tabletop2024Context.MasteryProperty> WeaponMasteryCustom { get; set; } = [];
    public bool UseOfficialFlankingRules { get; set; }
    public bool UseMathFlankingRules { get; set; }
    public bool UseOfficialFlankingRulesAlsoForReach { get; set; }
    public bool UseOfficialFlankingRulesAlsoForRanged { get; set; }
    public bool UseOfficialFlankingRulesButAddAttackModifier { get; set; }
    [Tag(Type = TagType.T2014)] public bool UseOldOfficialFlankingRules { get; set; }
    public bool EnableInitiativeRollOnEveryRoundStart { get; set; }
    [Tag(Type = TagType.T2014)] public bool BlindedConditionDontAllowAttackOfOpportunity { get; set; }
    [Tag(Type = TagType.T2014)] public bool UseOfficialLightingObscurementAndVisionRules { get; set; }
    public bool EnableCeilingAwareTargetPerception { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableChanceToPerceiveCloseRange { get; set; }
    [Tag(Type = TagType.T2014)] public bool OfficialObscurementRulesInvisibleCreaturesCanBeTarget { get; set; }
    [Tag(Type = TagType.T2014)] public bool OfficialObscurementRulesCancelAdvDisPairs { get; set; }
    public bool OfficialObscurementRulesHeavilyObscuredAsProjectileBlocker { get; set; }
    public bool OfficialObscurementRulesMagicalDarknessAsProjectileBlocker { get; set; }
    [Tag(Type = TagType.T2014)] public bool OfficialObscurementRulesTweakMonsters { get; set; }
    [Tag(Type = TagType.T2014)] public bool StealthDoesNotBreakWithSubtle { get; set; }
    [Tag(Type = TagType.T2014)] public bool StealthBreaksWhenAttackHits { get; set; }
    [Tag(Type = TagType.T2014)] public bool StealthBreaksWhenAttackMisses { get; set; }
    public bool StealthBreaksWhenMoving { get; set; }
    [Tag(Type = TagType.T2014)] public bool StealthRollForBreak { get; set; }
    public bool StealthBreaksWhenCastingMaterial { get; set; }
    [Tag(Type = TagType.T2014)] public bool StealthBreaksWhenCastingVerbose { get; set; }
    public bool StealthBreaksWhenCastingSomatic { get; set; }
    [Tag(Type = TagType.T2014)] public bool AccountForAllDiceOnSavageAttack { get; set; }
    [Tag(Type = TagType.T2014)] public bool AddDexModifierToEnemiesInitiativeRoll { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnemiesAlwaysRollInitiative { get; set; }
    public bool AllowFlightSuspend { get; set; }
    public bool AllowLevitateSuspend { get; set; }
    public bool FlightSuspendWingedBoots { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnablePullPushOnVerticalDirection { get; set; }
    [Tag(Type = TagType.T2014)] public bool FullyControlConjurations { get; set; }
    public bool EnableHigherGroundRules { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSurprisedToEnforceDisadvantage { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableTeleportToRemoveRestrained { get; set; }
    public bool EnableCharactersOnFireToEmitLight { get; set; }
    [Tag(Type = TagType.T2014)] public bool ColdResistanceAlsoGrantsImmunityToChilledCondition { get; set; }
    [Tag(Type = TagType.T2014)] public bool ColdImmunityAlsoGrantsImmunityToChilledAndFrozenCondition { get; set; }
    public int SenseNormalVisionRangeMultiplier { get; set; } = 1;
    public int CriticalHitModeAllies { get; set; }
    public int CriticalHitModeEnemies { get; set; }
    public int CriticalHitModeNeutral { get; set; }

    #region "Roleplay Settings"

    [Tag(Type = TagType.Roleplay)] public bool EnableCivilianAmbientAnimations { get; set; }
    [Tag(Type = TagType.Roleplay)] public bool EnableCivilianBattleReactions { get; set; }
    [Tag(Type = TagType.Roleplay)] public bool EnableEnemyBattleTactics { get; set; }
    [Tag(Type = TagType.Roleplay)] public bool ModifyJumpRulesForArmorAndEncumberance { get; set; }
    [Tag(Type = TagType.Roleplay)] public bool EnableBonusActionFreeJump { get; set; }
    [Tag(Type = TagType.Roleplay)] public bool ModifyThrowingRulesForStrength { get; set; }

    [Tag(Type = TagType.Roleplay)] public bool EnableDamageOnFailedJumpCheck { get; set; }

    #endregion

    public bool EnableCriticalHitsMissesAt10 { get; set; }
    //
    // Gameplay - Campaigns
    //

    public bool NoExperienceOnLevelUp { get; set; }
    public bool OverrideMinMaxLevel { get; set; }
    public float FasterTimeModifier { get; set; } = CampaignsDisplay.DefaultFastTimeModifier;
    public int MultiplyTheExperienceGainedBy { get; set; } = 100;
    public int OverridePartySize { get; set; } = ToolsContext.GamePartySize;
    public bool AllowAllPlayersOnNarrativeSequences { get; set; }
    public bool AddPickPocketableLoot { get; set; }
    public bool PickPocketNonHostiles { get; set; }
    public bool EnemySpellcastersDropScribedSpellbooks { get; set; }
    [MultiplayerLocal] public bool AltOnlyHighlightItemsInPartyFieldOfView { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool EnableAdditionalIconsOnLevelMap { get; set; }
    [MultiplayerLocal] public bool HideExitsAndTeleportersGizmosIfNotDiscovered { get; set; }
    [MultiplayerLocal] public bool EnableOutOfCombatTargetingSightLines { get; set; }
    [MultiplayerLocal] public bool EnableLogDialoguesToConsole { get; set; }
    [MultiplayerLocal] public bool EnableSpeech { get; set; }
    [MultiplayerLocal] public bool EnableSpeechOnNpcs { get; set; }
    [MultiplayerLocal] public bool EnableSpeechOnInteractions { get; set; }
    [MultiplayerLocal] public bool EnableVoiceVoxSpeech { get; set; }
    [MultiplayerLocal] public bool ForceModSpeechOnNpcs { get; set; }
    [MultiplayerLocal] public int SpeechChoice { get; set; }
    [MultiplayerLocal] public SerializableDictionary<int, (string, float)> SpeechVoices { get; set; } = [];
    [MultiplayerLocal] public SerializableDictionary<int, float> SpeechVoicePitches { get; set; } = [];
    [MultiplayerLocal] public SerializableDictionary<int, float> SpeechVoiceIntonations { get; set; } = [];
    [MultiplayerLocal] public SerializableDictionary<string, int> SpeechVoiceGenderOverrides { get; set; } = [];
    [MultiplayerLocal] public SerializableDictionary<string, (string, float, float, float)> SpeechNpcVoiceOverrides
    {
        get;
        set;
    } = [];
    [MultiplayerLocal] public float SpeechVolume { get; set; }
    [MultiplayerLocal] public bool FixGameVolume { get; set; }
    public bool EnableHeroWithBestProficiencyToRollChoice { get; set; }
    [MultiplayerLocal] public bool MarkInvisibleTeleportersOnLevelMap { get; set; }
    public bool EnableAlternateVotingSystem { get; set; }
    public bool EnableSumD20OnAlternateVotingSystem { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool AllowMoreRealStateOnRestPanel { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool EnableStatsOnHeroTooltip { get; set; }
    [MultiplayerLocal] public bool EnableAdditionalBackstoryDisplay { get; set; }
    [MultiplayerLocal] public bool EnableExtendedProficienciesPanelDisplay { get; set; }
    [MultiplayerLocal] public bool HideMonsterHitPoints { get; set; }
    [MultiplayerLocal] public bool RemoveBugVisualModels { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool ShowButtonWithControlledMonsterInfo { get; set; }

    // Battle
    [MultiplayerLocal] public bool DontFollowCharacterInBattle { get; set; }
    [MultiplayerLocal] public bool EnableElevationCameraToStayAtPosition { get; set; }
    [MultiplayerLocal] public bool NeverMoveCameraOnEnemyTurn { get; set; }
    public bool EnableForceAllyMovementAllowsFlight { get; set; }
    public bool EnableTeleportAllowsMidAirPositions { get; set; }
    [MultiplayerLocal] public bool EnableCancelEditOnRightMouseClick { get; set; }
    [MultiplayerLocal] public int DontFollowMargin { get; set; } = 5;
    [MultiplayerLocal] public int GridSelectedColor { get; set; } = 1;
    [MultiplayerLocal] public int MovementGridWidthModifier { get; set; } = 100;
    [MultiplayerLocal] public int OutlineGridWidthModifier { get; set; } = 100;
    [MultiplayerLocal] public int OutlineGridWidthSpeed { get; set; } = 100;
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool EnableDistanceOnTooltip { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool ShowMotionFormPreview { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool EnableSmootherBattleAnimations { get; set; }
    [MultiplayerLocal] public float BattleActionAnimationSpeedMultiplier { get; set; } =
        CombatAnimationContext.DefaultSpeedMultiplier;
    [MultiplayerLocal] public int HighContrastTargetingAoeSelectedColor { get; set; }
    [MultiplayerLocal] public int HighContrastTargetingSingleSelectedColor { get; set; }

    // Formation
    [MultiplayerLocal] public int FormationGridSelectedSet { get; set; } = -1;

    [MultiplayerLocal] public int[][][] FormationGridSets { get; set; } =
    [
        [
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize],
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize]
        ],
        [
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize],
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize]
        ],
        [
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize],
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize]
        ],
        [
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize],
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize]
        ],
        [
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize],
            new int[CampaignsContext.GridSize], new int[CampaignsContext.GridSize]
        ]
    ];

    // Merchants
    [Tag(Type = TagType.QoL)] public bool ScaleMerchantPricesCorrectly { get; set; }
    public bool StockGorimStoreWithAllNonMagicalClothing { get; set; }
    public bool StockHugoStoreWithAdditionalFoci { get; set; }
    public bool StockGorimStoreWithAllNonMagicalInstruments { get; set; }
    public bool EnableAdditionalFociInDungeonMaker { get; set; }
    public bool RestockAntiquarians { get; set; }
    public bool RestockArcaneum { get; set; }
    public bool RestockCircleOfDanantar { get; set; }
    public bool RestockTowerOfKnowledge { get; set; }

    //
    // Gameplay - Items, Crafting & Merchants
    //

    public bool AllowAnyClassToUseArcaneShieldstaff { get; set; }
    public bool IdentifyAfterRest { get; set; }
    public bool IncreaseMaxAttunedItems { get; set; }
    public bool RemoveAttunementRequirements { get; set; }
    public bool AllowAnyClassToWearSylvanArmor { get; set; }
    public bool AllowClubsToBeThrown { get; set; }
    public bool AllMagicThrownReturn { get; set; }
    [Tag(Type = TagType.T2014)] public bool UseOfficialFoodRationsWeight { get; set; }
    [Tag(Type = TagType.QoL)] public bool ExcludeEquippedItemsFromCarriedWeight { get; set; }
    public bool MakeAllMagicStaveArcaneFoci { get; set; }
    [Tag(Type = TagType.T2014)] public bool FixRingOfRegenerationHealRate { get; set; }
    public bool IgnoreHandXbowFreeHandRequirements { get; set; }

    [MultiplayerLocal] public bool LegendaryTweaksToggle { get; set; }
    public SerializableDictionary<string, int> WeaponTweakedTypes { get; set; } = [];

    [Tag(Type = TagType.T2024)] public bool EnablePotionsBonusAction2024 { get; set; }

    [Tag(Type = TagType.T2024)] public bool EnablePoisonsBonusAction2024 { get; set; }
    public bool KeepInvisibilityWhenUsingItems { get; set; }
    [MultiplayerLocal] public bool AddCustomIconsToOfficialItems { get; set; }
    [MultiplayerLocal] public bool DisableAutoEquip { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool EnableInventoryFilteringAndSorting { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool EnableInventoryTaintNonProficientItemsRed { get; set; }
    [MultiplayerLocal] [Tag(Type = TagType.QoL)] public bool EnableInventoryTintKnownRecipesRed { get; set; }
    public bool EnableStackableAxesAndDaggers { get; set; }
    public bool EnableStackableArtItems { get; set; }
    public bool EnableVersatileAmmunitionSlots { get; set; }
    public bool EnableVersatileOffHandSlot { get; set; }
    public int SetBeltOfDwarvenKindBeardChances { get; set; } = 50;

    // Crafting
    [MultiplayerLocal] public bool ShowCraftingRecipeInDetailedTooltips { get; set; }
    [MultiplayerLocal] public bool ShowCraftedItemOnRecipeIcon { get; set; }
    [MultiplayerLocal] public bool SwapCraftedItemAndRecipeIcons { get; set; }
    public bool LearnAllScrollRecipes { get; set; }
    public int RecipeCost { get; set; } = 200;
    public int TotalCraftingTimeModifier { get; set; }
    public bool AddNewWeaponsAndRecipesToShops { get; set; }
    public bool NewWeaponsAndRecipesBaseInsteadOfPrimed { get; set; }
    public bool NewWeaponsAndRecipesSimplified { get; set; }
    public bool AddNewScrollsToShops { get; set; }
    public bool AddNewScrollsToTreasure { get; set; }
    public List<string> CraftingInStore { get; } = [];

    //
    // Interface - Dungeon Maker
    //

    [MultiplayerLocal] public bool EnableLoggingInvalidReferencesInUserCampaigns { get; set; }
    [MultiplayerLocal] public bool EnableSortingDungeonMakerAssets { get; set; }
    [MultiplayerLocal] public bool AllowGadgetsAndPropsToBePlacedAnywhere { get; set; }
    [MultiplayerLocal] public bool UnleashEnemyAsNpc { get; set; }
    [MultiplayerLocal] public bool AddNewWeaponsAndRecipesToEditor { get; set; }
    [MultiplayerLocal] public bool UnleashNpcAsEnemy { get; set; }
    [MultiplayerLocal] public bool EnableVariablePlaceholdersOnTexts { get; set; }
    [MultiplayerLocal] public bool EnableDungeonMakerModdedContent { get; set; }
    [MultiplayerLocal] public string SelectedLanguageCode { get; set; } = TranslatorContext.English;

    //
    // Translation Service Settings
    //
    [MultiplayerLocal] public TranslationServiceType SelectedTranslationService { get; set; } =
        TranslationServiceType.Google;

    [MultiplayerLocal] public bool GoogleLegacyMode { get; set; }

    [MultiplayerLocal] public string OpenAIEndpoint { get; set; } = OpenAITranslationService.DefaultEndpoint;
    [MultiplayerLocal] public string OpenAIModel { get; set; } = OpenAITranslationService.DefaultModel;
    [MultiplayerLocal] public float OpenAITemperature { get; set; } = OpenAITranslationService.DefaultTemperature;
    [MultiplayerLocal] public float OpenAITopP { get; set; } = OpenAITranslationService.DefaultTopP;
    [MultiplayerLocal] public int OpenAITopK { get; set; } = OpenAITranslationService.DefaultTopK;

    [MultiplayerLocal] public string OpenAISystemPrompt { get; set; } =
        OpenAITranslationService.FallbackSystemPrompt;

    /// <summary>
    ///     Number of concurrent translation tasks (1-10).
    /// </summary>
    [MultiplayerLocal] public int TranslationConcurrency { get; set; } = 1;

    //
    // Characters - Classes
    //

    public bool EnableBardScimitarSpecialization { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBardWeaponProficiency2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBardicInspiration2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBardCounterCharm2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBardExpertiseOneLevelBefore2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBardSuperiorInspiration2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBardWordsOfCreation2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBardMagicalSecrets2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool RemoveBardSongOfRest2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianBrutalStrike2024 { get; set; }
    public bool EnableBarbarianFightingStyle { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianInstinctivePounce2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianPersistentRage2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianPrimalKnowledge2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianReckless2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianDangerSense2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianIndomitableMight2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinRadiantStrikes2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianRage2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBarbarianRelentlessRage2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableClericBlessedStrikes2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableClericChannelDivinity2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableClericDivineOrder2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableClericDivineIntervention2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableClericSearUndead2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableClericToLearnDomainAtLevel3 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidArchDruid2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidToLearnCircleAtLevel3 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidElementalFury2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidMetalArmor2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidPrimalOrder2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidWeaponProficiency2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidWildResurgence2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableDruidWildshape2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableFighterIndomitableSaving2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableFighterSkillOptions2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableFighterSecondWind2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableFighterStudiedAttacks2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableFighterTacticalMaster2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableFighterTacticalProgression2024 { get; set; }
    public bool EnableMonkAbundantKi { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkWeaponProficiency2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkBodyAndMind2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkDeflectAttacks2024 { get; set; }
    public bool EnableMonkFightingStyle { get; set; }
    public bool EnableMonkHandwrapsOnGauntletSlot { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkEmpoweredStrikes2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkHeightenedFocus2024 { get; set; }
    public bool EnableMonkImprovedUnarmoredMovement { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkSelfRestoration2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkSuperiorDefense2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkUncannyMetabolism2024 { get; set; }
    public bool EnableMonkKatanaSpecialization { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkFocus2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkMartialArts2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMonkStunningStrike2024 { get; set; }
    public bool AddPaladinSmiteToggle { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinAbjureFoes2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinChannelDivinity2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinLayOnHands2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinRechargeLv20Feature { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinRestoringTouch2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinSmite2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinSpellCastingAtLevel1 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePaladinAnyFightingStyle2024 { get; set; }
    [MultiplayerLocal] public bool ShowChannelDivinityOnPortrait { get; set; }
    public bool AddHumanoidFavoredEnemyToRanger { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerDeftExplorer2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerExpertise2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerFavoredEnemy2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerNatureShroud2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerPreciseHunter2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerRelentlessHunter2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerRelentlessHunter2024AsNoConcentration { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerRoving2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerSpellCastingAtLevel1 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerTireless2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerAnyFightingStyle2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool RemoveRangerPrimevalAwareness2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerFeralSenses2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRangerFoeSlayers2024 { get; set; }
    public bool EnableRogueScimitarSpecialization { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRogueWeaponProficiency2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRogueCunningStrike2024 { get; set; }
    public bool EnableRogueFightingStyle { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRogueReliableTalent2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRogueSlipperyMind2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRogueSteadyAim2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRogueStrokeOfLuck2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableThiefUseMagicDevice2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableAssassinAssassinate2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableAssassinInfiltrationExpertise2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool RemoveRogueBlindSense2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererWeaponProficiency2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererArcaneApotheosis2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererInnateSorcery2024 { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableSorcererMagicalGuidance { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableSorcererQuickenedAction { get; set; }
    public bool HideQuickenedActionWhenMetamagicOff { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererSorcerousRestoration2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererMetamagic2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererDraconicBloodlineAC2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererDraconicBloodlineElementalAffinity2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererDraconicBloodlineResistance2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSorcererOrigin2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWarlockInvocationProgression2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWarlockMagicalCunningAndImprovedEldritchMaster2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWarlockToLearnPatronAtLevel3 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableAbjurerSpellBreaker2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWizardWeaponProficiency2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWizardMemorizeSpell2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWizardSpellMastery2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWizardToLearnScholarAtLevel2 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableWizardToLearnSchoolAtLevel3 { get; set; }
    public bool EnableSignatureSpellsRelearn { get; set; }

    //
    // Characters - Backgrounds, Races & Subraces
    //

    [Tag(Type = TagType.T2024)] public bool EnableBackgroundASI { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableBackgroundBonusFeats { get; set; }
    public bool AddOriginFeatsToAutoLearn { get; set; }
    public bool EnableFlexibleBackgrounds { get; set; }
    public bool EnableFlexibleRaces { get; set; }
    public bool ChangeDragonbornElementalBreathUsages { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableAlternateHuman { get; set; }
    [Tag(Type = TagType.T2014)] public bool UseOfficialSmallRacesDisWithHeavyWeapons { get; set; }
    public bool DisableSenseDarkVisionFromAllRaces { get; set; }
    public bool DisableSenseSuperiorDarkVisionFromAllRaces { get; set; }
    public bool AddDarknessPerceptiveToDarkRaces { get; set; }
    [Tag(Type = TagType.T2014)] public bool RaceLightSensitivityApplyOutdoorsOnly { get; set; }
    [MultiplayerLocal] public int RaceSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> RaceEnabled { get; } = [];
    [MultiplayerLocal] public int SubraceSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> SubraceEnabled { get; } = [];
    [MultiplayerLocal] public int BackgroundSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> BackgroundEnabled { get; } = [];

    //
    // Characters - Feats, Groups, Fighting Styles, Invocations and Metamagic
    //

    public bool DisableLevelPrerequisitesOnModFeats { get; set; }
    public bool DisableRacePrerequisitesOnModFeats { get; set; }
    public bool DisableCastSpellPreRequisitesOnModFeats { get; set; }
    public int TotalFeatsGrantedFirstLevel { get; set; }
    public bool EnablesAsiAndFeat { get; set; }
    [XmlElement("EnableGeneralFeats2024")]
    [Tag(Type = TagType.T2024)] public bool EnableTabletopFeatRules2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableEpicBoons2024 { get; set; } = true;
    [Obsolete("No longer used. Retained for backward compatibility only.")]
    [MultiplayerLocal] public bool HideCompletelyOriginalSolastaFeats { get; set; }
    public bool EnableFeatsAtEveryFourLevels { get; set; }
    public bool EnableFeatsAtEveryFourLevelsMiddle { get; set; }
    [Tag(Type = TagType.T2014)] public bool AccountForAllDiceOnFollowUpStrike { get; set; }
    public bool AllowCantripsTriggeringOnWarMagic { get; set; }
    [MultiplayerLocal] public int FeatSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> FeatEnabled { get; } = [];
    [XmlArray("GeneralFeats2024Initialized")]
    public List<string> TabletopFeats2024Initialized { get; } = [];
    [MultiplayerLocal] public int FeatGroupSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> FeatGroupEnabled { get; } = [];
    [Obsolete("No longer used. Retained for backward compatibility only.")]
    [MultiplayerLocal] public bool RepairedLegacyFeatGroupEnabledAfter2024ContainerBug { get; set; }
    public bool RepairedLegacyFeatGroupEnabledAfter2024ContainerBugV2 { get; set; }
    [MultiplayerLocal] public int FightingStyleSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> FightingStyleEnabled { get; } = [];
    [MultiplayerLocal] public int InvocationSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> InvocationEnabled { get; } = [];
    [MultiplayerLocal] public int MetamagicSliderPosition { get; set; } = ModUi.DontDisplayDescription;
    public List<string> MetamagicEnabled { get; } = [];

    //
    // Characters - Spells
    //

    public bool AllowBladeCantripsToUseReach { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnablePreparedSpellsTables2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableSpellLists2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableRitualOnAllCasters2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneSpellSlotPerTurn2024 { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndCounterspellSpell { get; set; }
    public bool QuickCastLightCantripOnWornItemsFirst { get; set; }
    [Tag(Type = TagType.T2014)] public bool IllusionSpellsAutomaticallyFailAgainstTrueSightInRange { get; set; }
    public bool AllowTargetingSelectionWhenCastingChainLightningSpell { get; set; }
    public bool RemoveHumanoidFilterOnHideousLaughter { get; set; }
    public bool AddBleedingToLesserRestoration { get; set; }
    [Tag(Type = TagType.T2014)] public bool BestowCurseNoConcentrationRequiredForSlotLevel5OrAbove { get; set; }
    [Tag(Type = TagType.T2014)] public bool RemoveRecurringEffectOnEntangle { get; set; }
    public bool EnableUpcastConjureElementalAndFey { get; set; }
    public bool OnlyShowMostPowerfulUpcastConjuredElementalOrFey { get; set; }
    public bool ChangeSleetStormToCube { get; set; }
    public bool UseHeightOneCylinderEffect { get; set; }
    [Tag(Type = TagType.T2014)] public bool FixEldritchBlastRange { get; set; }
    public bool Use24CellsRangeWithDimensionDoor { get; set; }
    public bool ModifyGravitySlam { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndBarkskinSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndChillTouchCantrip { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndBladeWardCantrip { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndDamagingSpellsUpgrade { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndHealingSpellsUpgrade { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndDivineFavorSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndGuidanceSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndHideousLaughterSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndHuntersMarkSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndLesserRestorationSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndMagicWeaponSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndPowerWordStunSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndSpareTheDyingSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndShillelaghSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndSpiderClimbSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndStoneSkinSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndTrueStrikeCantrip { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableOneDndWitchBoltSpell { get; set; }
    public bool SwapShineCantrip { get; set; }
    public bool AllowHasteCasting { get; set; }
    public bool AllowStackedMaterialComponent { get; set; }
    public bool EnableRelearnSpells { get; set; }
    [MultiplayerLocal] public bool AllowDisplayingOfficialSpells { get; set; }
    [MultiplayerLocal] public bool AllowDisplayingNonSuggestedSpells { get; set; }

    [MultiplayerLocal] public SerializableDictionary<string, int> SpellListSliderPosition { get; set; } = [];
    public SerializableDictionary<string, List<string>> SpellListSpellEnabled { get; set; } = [];

    //
    // Characters - Subclasses
    //

    public bool EnableStrictTabletopClassSelection { get; set; }
    public bool AllowAlliesToPerceiveRangerGloomStalkerInNaturalDarkness { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableBardHealingBalladOnLongRest { get; set; }
    [Tag(Type = TagType.T2014)] public bool EnableDivineHeartTabletopFeatures { get; set; }
    public bool EnableBg3AbjurationArcaneWard { get; set; }
    public bool EnableRogueStrSaving { get; set; }
    public bool RemoveSchoolRestrictionsFromShadowCaster { get; set; }
    public bool RemoveSchoolRestrictionsFromSpellBlade { get; set; }
    public int WildSurgeDieRollThreshold { get; set; } = 2;
    [Tag(Type = TagType.T2024)] public bool SwapAbjurationSavant { get; set; }
    [Tag(Type = TagType.T2024)] public bool SwapEvocationSavant { get; set; }
    [Tag(Type = TagType.T2024)] public bool SwapEvocationPotentCantripAndSculptSpell { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableEvocationPotentCantrip2024 { get; set; }
    public bool EvocationSculptSpellNoPerception { get; set; }
    [Tag(Type = TagType.T2024)] public bool EnableMartialChampion2024 { get; set; }
    [MultiplayerLocal] public SerializableDictionary<string, int> KlassListSliderPosition { get; set; } = [];
    public SerializableDictionary<string, List<string>> KlassListSubclassEnabled { get; set; } = [];

    //
    // Encounters - General
    //

    public bool EnableEnemiesControlledByPlayer { get; set; }
    public bool EnableHeroesControlledByComputer { get; set; }
    public bool EnableAdvancedCombatAI { get; set; }

    //
    // Debug
    //

    [MultiplayerLocal] public bool DebugDisableVerifyDefinitionNameIsNotInUse { get; set; }

#if DEBUG
    public int WildSurgeEffectDie { get; set; }
    [MultiplayerLocal] public bool DebugLogDefinitionCreation { get; set; }
    [MultiplayerLocal] public bool DebugLogFieldInitialization { get; set; }
    [MultiplayerLocal] public bool DebugLogVariantMisuse { get; set; }
#endif
}
