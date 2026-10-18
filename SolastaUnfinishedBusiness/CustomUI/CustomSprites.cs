using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Resources = SolastaUnfinishedBusiness.Properties.Resources;

namespace SolastaUnfinishedBusiness.CustomUI;

public static class Sprites
{
    private const string CustomSpriteNamePrefix = "_CE_";

    #region Spells

    internal static AssetReferenceSprite FarStep =>
        GetSprite("FarStep", Resources.FarStep, 128);

    #endregion

    #region Misc

    internal static AssetReferenceSprite ArcaneWardPoints =>
        GetSprite("ArcaneWardPointsIcon", Resources.ArcaneWardPoints, 64);

    internal static AssetReferenceSprite BardicDiceResourceIcon =>
        GetSprite("BardicDiceResourceIcon", Resources.BardicDiceResourceIcon, 64);

    internal static AssetReferenceSprite ChannelDivinityResourceIcon =>
        GetSprite("ChannelDivinityResourceIcon", Resources.ChannelDivinityResourceIcon, 64);

    internal static AssetReferenceSprite GambitResourceIcon =>
        GetSprite("GambitResourceIcon", Resources.GambitResourceIcon, 64);

    internal static AssetReferenceSprite EldritchVersatilityResourceIcon =>
        GetSprite("EldritchVersatilityResourceIcon", Resources.EldritchVersatilityResourceIcon, 64);

    internal static AssetReferenceSprite SorceryPointsResourceIcon =>
        GetSprite("SorceryPointsResourceIcon", Resources.SorceryPoints, 64);

    internal static AssetReferenceSprite LightEnergyCrossbowBolt =>
        GetSprite("LightEnergyCrossbowBolt", Resources.LightEnergyCrossbowBolt, 64);
    
    #endregion

    #region UI

    private static AssetReferenceSprite CheckOn =>
        GetSprite("CheckOn", Resources.checkOn, 12);

    internal static Texture CheckOnTexture => GetSpriteByGuid(CheckOn.AssetGUID)?.texture;

    private static AssetReferenceSprite CheckOff =>
        GetSprite("CheckOff", Resources.checkOff, 12);

    internal static Texture CheckOffTexture => GetSpriteByGuid(CheckOff.AssetGUID)?.texture;

    private static AssetReferenceSprite Expanded =>
        GetSprite("Expanded", Resources.expanded, 12);

    internal static Texture ExpandedTexture => GetSpriteByGuid(Expanded.AssetGUID)?.texture;

    private static AssetReferenceSprite Collapsed =>
        GetSprite("Collapsed", Resources.collapsed, 12);

    internal static Texture CollapsedTexture => GetSpriteByGuid(Collapsed.AssetGUID)?.texture;

    #endregion

    #region Actions

    internal static AssetReferenceSprite ActionInfuse =>
        GetSprite("ActionInfuse", Resources.ActionInfuse, 80);

    internal static AssetReferenceSprite ActionCrystalDefenseOff =>
        GetSprite("ActionCrystalDefenseOff", Resources.ActionCrystalDefenseOff, 80);

    internal static AssetReferenceSprite ActionCrystalDefenseOn =>
        GetSprite("ActionCrystalDefenseOn", Resources.ActionCrystalDefenseOn, 80);

    internal static AssetReferenceSprite ActionGambit =>
        GetSprite("ActionGambit", Resources.ActionGambit, 80);

    internal static AssetReferenceSprite ActionEldritchVersatility =>
        GetSprite("ActionEldritchVersatility", Resources.ActionEldritchVersatility, 80);

    #endregion

    #region Definition artwork

    // Each entry is one authored identity. Its saved aliases, upgrades and alternate costs share that artwork.
    private static readonly Dictionary<string, string[]> DefinitionSpriteFamilies = new(System.StringComparer.Ordinal)
    {
        ["CapabilityPowerMartialArcaneArcherSlowingArrow"] = ["PowerMartialArcaneArcherSlowingArrow"],
        ["CapabilityPowerMartialArcaneArcherShadowArrow"] = ["PowerMartialArcaneArcherShadowArrow"],
        ["CapabilityPowerMartialArcaneArcherInsightArrow"] = ["PowerMartialArcaneArcherInsightArrow"],
        ["CapabilityPowerMartialArcaneArcherGraspingArrow"] = ["PowerMartialArcaneArcherGraspingArrow"],
        ["CapabilityPowerMartialArcaneArcherEnfeeblingArrow"] = ["PowerMartialArcaneArcherEnfeeblingArrow"],
        ["CapabilityPowerMartialArcaneArcherBurstingArrow"] = ["PowerMartialArcaneArcherBurstingArrow"],
        ["CapabilityPowerMartialArcaneArcherBeguilingArrow"] = ["PowerMartialArcaneArcherBeguilingArrow"],
        ["CapabilityPowerMartialArcaneArcherBanishingArrow"] = ["PowerMartialArcaneArcherBanishingArrow"],
        ["CapabilityPowerPathOfTheWildMagicTeleport"] = ["PowerPathOfTheWildMagicTeleport"],
        ["CapabilityPowerInvocationBondOfTheTalisman"] = ["PowerInvocationBondOfTheTalisman"],
        ["CapabilityPowerInnovationArtilleristProtector"] = ["PowerInnovationArtilleristProtector"],
        ["CapabilityPowerInnovationArtilleristForceBallista"] = ["PowerInnovationArtilleristForceBallista"],
        ["CapabilityPowerInnovationArtilleristFlamethrower"] = ["PowerInnovationArtilleristFlamethrower"],
        ["CapabilityPowerOathOfDemonHunterRemasterHunterStep"] = ["PowerOathOfDemonHunterRemasterHunterStep"],
        ["CapabilityPowerRoguishShadowcasterShadowForm"] = ["PowerRoguishShadowcasterShadowForm"],
        ["CapabilityPowerInvocationInexorableHex"] = ["PowerInvocationInexorableHex"],
        ["CapabilityPowerFeatAcrobat"] = ["PowerFeatAcrobat"],
        ["CapabilityPaladinCapstoneRecharge"] = ["PowerRechargePowerHatredArdentHate", "PowerRechargePowerOathOfAncientsElderChampion", "PowerRechargePowerOathOfDevotionHolyNimbus", "PowerRechargePowerOathOfDreadAspectOfDread", "PowerRechargePowerOathOfJugementFinalJudgement", "PowerRechargePowerOathOfMotherlandFlamesOfMotherland", "PowerRechargePowerOathOfThunderStormHerald", "PowerRechargePowerOathOfTirmarInquisitorZeal"],
        ["CapabilityPowerOathOfAltruismTakeThePain"] = ["PowerOathOfAltruismTakeThePain"],
        ["CapabilityPowerDivineHeartDivineFount"] = ["PowerDivineHeartDivineFount"],
        ["CapabilityPowerRiftWalkerBlink"] = ["PowerRiftWalkerBlink"],
        ["CapabilityPowerFeatChefCookTreat"] = ["PowerFeatChefCookTreat", "PowerFeatChefTreat"],
        ["CapabilityPowerDivineHeartDivineRecovery"] = ["PowerDivineHeartDivineRecovery"],
        ["CapabilityPowerMalakhHealingTouch"] = ["PowerMalakhHealingTouch"],
        ["CapabilityPowerFeatChefCookMeal"] = ["PowerFeatChefCookMeal"],
        ["CapabilityPowerDarkKoboldGrovelCowerAndBeg"] = ["PowerDarkKoboldGrovelCowerAndBeg"],
        ["CapabilityPowerWizardAbjurationSpellResistance"] = ["PowerWizardAbjurationSpellResistance"],
        ["CapabilityPowerSpellShieldArcaneDeflection"] = ["PowerSpellShieldArcaneDeflection"],
        ["CapabilityPowerCollegeOfGutsArcaneDeflection"] = ["PowerCollegeOfGutsArcaneDeflection"],
        ["CapabilityPowerFeatOrcishAggression"] = ["PowerFeatOrcishAggression"],
        ["CapabilityPowerPathOfTheLightIlluminatingBurst"] = ["PowerPathOfTheLightIlluminatingBurst"],
        ["CapabilityPowerInnovationArtilleristEldritchCannonDetonate"] = ["PowerInnovationArtilleristEldritchCannonDetonate", "PowerInnovationArtilleristEldritchCannonDetonateSelf"],
        ["CapabilityPowerOathOfDreadAspectOfDread"] = ["PowerOathOfDreadAspectOfDread"],
        ["CapabilityPowerDomainOrderVoiceOfAuthorityCompelAttack"] = ["PowerDomainOrderVoiceOfAuthorityCompelAttack"],
        ["CapabilityPowerRoguishSwashbucklerElegantManeuver"] = ["PowerRoguishSwashbucklerElegantManeuver"],
        ["CapabilityPowerRoguishSwashbucklerPanache"] = ["PowerRoguishSwashbucklerPanache"],
        ["CapabilityPowerFeatDragonWings"] = ["PowerFeatDragonWings"],
        ["CapabilityPowerSorcerousRestoration"] = ["PowerSorcerousRestoration"],
        ["CapabilityPowerRangerWildMasterKillCommand"] = ["PowerRangerWildMasterKillCommand"],
        ["CapabilityPowerGrayDwarfStoneStrength"] = ["PowerGrayDwarfStoneStrength"],
        ["CapabilityPowerInvocationChillingHex"] = ["PowerInvocationChillingHex"],
        ["CapabilityPowerBladeDancerBladeDance"] = ["PowerBladeDancerBladeDance", "PowerBladeDancerDanceOfDefense", "PowerBladeDancerDanceOfVictory"],
        ["CapabilityPowerSharedPoolCollegeOfLifeHealingPool"] = ["PowerSharedPoolCollegeOfLifeHealingPool"],
        ["CapabilityPowerCircleOfTheCosmosChalice"] = ["PowerCircleOfTheCosmosChalice"],
        ["CapabilityPowerSorcerousWildMagicLightningStrike"] = ["PowerSorcerousWildMagicLightningStrike"],
        ["CapabilityPowerOathOfThunderBifrost"] = ["PowerOathOfThunderBifrost"],
        ["CapabilityPowerSorcerousPsionPsychokinesisFixed"] = ["PowerSorcerousPsionPsychokinesisFixed", "PowerSorcerousPsionPsychokinesisPoints"],
        ["CapabilityPowerSorcerousFieldManipulatorForcefulStepFixed"] = ["PowerSorcerousFieldManipulatorForcefulStepFixed", "PowerSorcerousFieldManipulatorForcefulStepPoints"],
        ["CapabilityPowerSoulBladeSummonPactWeapon"] = ["PowerSoulBladeSummonPactWeapon"],
        ["CapabilityPowerDomainOrderEmbodimentOfLaw"] = ["PowerDomainOrderEmbodimentOfLaw"],
        ["CapabilityPowerCollegeOfThespianCombatInspiration"] = ["PowerCollegeOfThespianCombatInspiration"],
        ["CapabilityPowerInfusionBloody"] = ["PowerInfusionBloody"],
        ["CapabilityPowerDivineHeartFavoredByTheGods"] = ["PowerDivineHeartFavoredByTheGods"],
        ["CapabilityPowerOathOfMotherlandFlamesOfMotherland"] = ["PowerOathOfMotherlandFlamesOfMotherland"],
        ["CapabilityPowerRoguishArcaneScoundrelEssenceTheft"] = ["PowerRoguishArcaneScoundrelEssenceTheft"],
        ["CapabilityPowerMartialWarlordStrategicReposition"] = ["PowerMartialWarlordStrategicReposition", "PowerMartialWarlordControlTheField"],
        ["CapabilityPowerOathOfDevotionHolyNimbus"] = ["PowerOathOfDevotionHolyNimbus"],
        ["CapabilityPowerWizardGraviturgistEventHorizon"] = ["PowerWizardGraviturgistEventHorizon"],
        ["CapabilityPowerSoulBladeSoulShield"] = ["PowerSoulBladeSoulShield"],
        ["CapabilityPowerRangerSkyWarriorGiftOfTheWind"] = ["PowerRangerSkyWarriorGiftOfTheWind"],
        ["CapabilityPowerRavenScionHeartSeekingShot"] = ["PowerRavenScionHeartSeekingShot"],
        ["CapabilityPowerAncientForestRooted"] = ["PowerAncientForestRooted"],
        ["CapabilityPowerInnovationVitriolistMixture"] = ["PowerInnovationVitriolistMixture"],
        ["CapabilityPowerHatredScornfulPrayer"] = ["PowerHatredScornfulPrayer", "PowerHatredScornfulPrayerFeature"],
        ["CapabilityPowerDomainOrderOrdersDemand"] = ["PowerDomainOrderOrdersDemand"],
        ["CapabilityPowerSharedPoolForestGuardianBarkWard"] = ["PowerSharedPoolForestGuardianBarkWard", "PowerSharedPoolForestGuardianImprovedBarkWard"],
        ["CapabilityPowerRangerWildMasterBeastCompanionPool"] = ["PowerRangerWildMasterBeastCompanionPool"],
        ["CapabilityPowerTeleportSummon"] = ["PowerTeleportSummon"],
        ["CapabilityPowerShadarKaiTeleport"] = ["PowerShadarKaiTeleport"],
        ["CapabilityPowerCircleOfTheLifeSeedOfLife"] = ["PowerCircleOfTheLifeSeedOfLife"],
        ["CapabilityPowerSorcerousFieldManipulatorDisplacement"] = ["PowerSorcerousFieldManipulatorDisplacement"],
        ["CapabilityPowerRangerFeyWandererMistyWanderer"] = ["PowerRangerFeyWandererMistyWanderer"],
        ["CapabilityPowerArchfeyMistyEscape"] = ["PowerArchfeyMistyEscape"],
        ["CapabilityPowerVanishSummon"] = ["PowerVanishSummon"],
        ["CapabilityPowerBolgrifInvisibility"] = ["PowerBolgrifInvisibility"],
        ["CapabilityPowerCelestialHealingLight"] = ["PowerCelestialHealingLight"],
        ["CapabilityPowerSorcerousSorrAkkathTouchOfDarknessFixed"] = ["PowerSorcerousSorrAkkathTouchOfDarknessFixed", "PowerSorcerousSorrAkkathTouchOfDarknessPoints"],
        ["CapabilityPowerInventorWeaponSteelDefenderCommand"] = ["PowerInventorWeaponSteelDefenderCommand"],
        ["CapabilityPowerInnovationArtilleristEldritchCannonCommand"] = ["PowerInnovationArtilleristEldritchCannonCommand"],
        ["CapabilityPowerCircleOfTheWildfireCommandSpirit"] = ["PowerCircleOfTheWildfireCommandSpirit"],
        ["CapabilityPowerWizardAbjurationArcaneWard"] = ["PowerWizardAbjurationArcaneWard"],
        ["CapabilityPowerWizardAbjurationRechargeArcaneWard"] = ["PowerWizardAbjurationRechargeArcaneWard"],
        ["CapabilityPowerOathOfJugementFinalJudgement"] = ["PowerOathOfJugementFinalJudgement"],
        ["CapabilityActionAffinitySpellShieldRangedDefense"] = ["ActionAffinitySpellShieldRangedDefense"],
        ["CapabilityPowerWizardGraviturgistViolentAttraction"] = ["PowerWizardGraviturgistViolentAttraction"],
        ["CapabilityPowerMalakhAngelicRadiance"] = ["PowerMalakhAngelicRadiance"],
        ["CapabilityPowerMalakhAngelicVisage"] = ["PowerMalakhAngelicVisage"],
        ["CapabilityPowerSorcererDraconicBloodlineAwePresence"] = ["PowerSorcererDraconicBloodlineAwePresence"],
        ["CapabilityPowerSorcererDraconicBloodlineFearPresence"] = ["PowerSorcererDraconicBloodlineFearPresence"],
        ["CapabilityPowerOathOfThunderDivineBolt"] = ["PowerOathOfThunderDivineBolt"],
        ["CapabilityPowerDomainFireSummonInferno"] = ["PowerDomainFireSummonInferno"],
        ["CapabilityPowerDomainColdSummonBlizzard"] = ["PowerDomainColdSummonBlizzard"],
        ["CapabilityPowerPathOfTheWildMagicBolt"] = ["PowerPathOfTheWildMagicBolt"],
        ["CapabilityPowerOathOfAncientsNaturesWrath"] = ["PowerOathOfAncientsNaturesWrath"],
        ["CapabilityPowerInvocationOneWithShadows"] = ["PowerInvocationOneWithShadows"],
        ["CapabilityPowerAncientForestRegrowth"] = ["PowerAncientForestRegrowth"],
        ["CapabilityPowerCircleOfTheNightWildShapeHealing"] = ["PowerCircleOfTheNightWildShapeHealing", "PowerCircleOfTheNightWildShapeImprovedHealing", "PowerCircleOfTheNightWildShapeSuperiorHealing", "PowerCircleOfTheNightWildShapeMasterfulHealing"],
        ["CapabilityPowerInvocationVexingHex"] = ["PowerInvocationVexingHex"],
        ["CapabilityPowerInvocationBurningHex"] = ["PowerInvocationBurningHex"],
        ["CapabilityPowerRiftWalkerRiftCloak"] = ["PowerRiftWalkerRiftCloak"],
        ["CapabilityPowerPathOfTheWildMagicSummon"] = ["PowerPathOfTheWildMagicSummon"],
        ["CapabilityPowerSorcererHauntedSoulPossession"] = ["PowerSorcererHauntedSoulPossession"],
        ["CapabilityPowerFeatDragonFear"] = ["PowerFeatDragonFear"],
        ["CapabilityPowerCollegeOfValianceHeroicInspiration"] = ["PowerCollegeOfValianceHeroicInspiration"],
        ["CapabilityPowerDomainSmithAdamantBenediction"] = ["PowerDomainSmithAdamantBenediction"],
        ["CapabilityPowerInnovationVivisectionistEmergencyCure"] = ["PowerInnovationVivisectionistEmergencyCure", "PowerInnovationVivisectionistMasterEmergencyCure"],
        ["CapabilityPowerInfusionMajorElemental"] = ["PowerInfusionMajorElemental", "PowerInfusionMinorElemental", "PowerInfusionMajorElementalDamageAcid", "PowerInfusionMajorElementalDamageCold", "PowerInfusionMajorElementalDamageFire", "PowerInfusionMajorElementalDamageLightning", "PowerInfusionMajorElementalDamageThunder", "PowerInfusionMinorElementalDamageAcid", "PowerInfusionMinorElementalDamageCold", "PowerInfusionMinorElementalDamageFire", "PowerInfusionMinorElementalDamageLightning", "PowerInfusionMinorElementalDamageThunder"],
        ["CapabilityPowerRiftWalkerRiftStrike"] = ["PowerRiftWalkerRiftStrike"],
        ["CapabilityPowerDomainSmithReinforceArmor1"] = ["PowerDomainSmithReinforceArmor1", "PowerDomainSmithReinforceArmor6", "PowerDomainSmithReinforceArmor11", "PowerDomainSmithReinforceArmor16"],
        ["CapabilityFeatureOathOfAltruismSpiritualShielding"] = ["FeatureOathOfAltruismSpiritualShielding"],
        ["CapabilityPowerMoonlitScionMidnightBlessing"] = ["PowerMoonlitScionMidnightBlessing"],
        ["CapabilityPowerOathOfAncientsTurnFaithless"] = ["PowerOathOfAncientsTurnFaithless"],
        ["CapabilityPowerWayOfTheDragonDragonFuryCold"] = ["PowerWayOfTheDragonDragonFuryCold"],
        ["CapabilityPowerWayOfTheDragonDragonFuryAcid"] = ["PowerWayOfTheDragonDragonFuryAcid"],
        ["CapabilityPowerWayOfTheDragonDragonFuryFire"] = ["PowerWayOfTheDragonDragonFuryFire"],
        ["CapabilityPowerWayOfTheDragonDragonFuryPoison"] = ["PowerWayOfTheDragonDragonFuryPoison"],
        ["CapabilityPowerWayOfTheDragonDragonFuryLightning"] = ["PowerWayOfTheDragonDragonFuryLightning"],
        ["CapabilityPowerInnovationArtilleristEldritchCannonRefund"] = ["PowerInnovationArtilleristEldritchCannonRefund"],
        ["CapabilityPowerArcanePrecision"] = ["PowerArcanePrecision", "PowerCharismaticPrecision", "PowerWisePrecision"],
        ["CapabilityPowerWayOfTheStormSoulTempestFury"] = ["PowerWayOfTheStormSoulTempestFury"],
        ["CapabilityPowerInnovationVitriolistRefundMixture"] = ["PowerInnovationVitriolistRefundMixture"],
        ["CapabilityPowerInnovationAlchemyRefundPool"] = ["PowerInnovationAlchemyRefundPool"],
        ["CapabilityPowerPathOfTheElementsElementalBurstWildfire"] = ["PowerPathOfTheElementsElementalBurstWildfire"],
        ["CapabilityPowerPathOfTheElementsElementalBurstBlizzard"] = ["PowerPathOfTheElementsElementalBurstBlizzard"],
        ["CapabilityPowerPathOfTheElementsElementalBurstStorm"] = ["PowerPathOfTheElementsElementalBurstStorm"],
        ["CapabilityPowerGiftOfTheChromaticDragonChromaticInfusion"] = ["PowerGiftOfTheChromaticDragonChromaticInfusion"],
        ["CapabilityPowerOathOfAltruismExaltedProtector"] = ["PowerOathOfAltruismExaltedProtector"],
        ["CapabilityPowerOathOfAltruismAuraOfTheGuardian"] = ["PowerOathOfAltruismAuraOfTheGuardian", "PowerOathOfAltruismAuraOfTheGuardian18", "GuardianAura"],
        ["CapabilityPowerOathOfThunderStormHerald"] = ["PowerOathOfThunderStormHerald"],
        ["CapabilityPowerCollegeOfValianceDishearteningPerformance"] = ["PowerCollegeOfValianceDishearteningPerformance"],
        ["CapabilityPowerWizardGraviturgistDensityDecrease"] = ["PowerWizardGraviturgistDensityDecrease"],
        ["CapabilityPowerWizardGraviturgistDensityIncrease"] = ["PowerWizardGraviturgistDensityIncrease"],
        ["CapabilityPowerRoyalKnightInspiringSurge"] = ["PowerRoyalKnightInspiringSurge", "PowerRoyalKnightSpiritedSurge"],
        ["CapabilityPowerDivineHeartEmpoweredHealing"] = ["PowerDivineHeartEmpoweredHealing"],
        ["CapabilityPowerWayOfTheDiscordanceChaosChanneling"] = ["PowerWayOfTheDiscordanceChaosChanneling", "PowerWayOfTheDiscordanceChaosChannelingPoints"],
        ["CapabilityPowerArcanistArcanePulse"] = ["PowerArcanistArcanePulse", "PowerArcanistArcanePulseUpgrade"],
        ["CapabilityPowerOathOfTirmarInquisitorZeal"] = ["PowerOathOfTirmarInquisitorZeal"],
        ["CapabilityPowerFeatDurable2024SpeedyRecovery"] = ["PowerFeatDurable2024SpeedyRecovery"],
        ["CapabilityPowerFeatMusician2024Performance"] = ["PowerFeatMusician2024Performance"],
        ["CapabilityPowerClericImprovedBlessedStrikes"] = ["PowerClericImprovedBlessedStrikes"],
        ["CapabilityPowerOathOfAncientsAuraWarding"] = ["PowerOathOfAncientsAuraWarding", "PowerOathOfAncientsAuraWarding18"],
        ["CapabilityPowerHatredHatefulGaze"] = ["PowerHatredHatefulGaze"],
        ["CapabilityPowerWayOfTheShadowCloakOfShadows"] = ["PowerWayOfTheShadowCloakOfShadows"],
        ["CapabilityPowerFeatSharpEye2024"] = ["PowerFeatSharpEye2024"],
        ["CapabilityPowerFeatArcaneOmens2024"] = ["PowerFeatArcaneOmens2024"],
        ["CapabilityPowerWarlockMagicalCunning"] = ["PowerWarlockMagicalCunning"],
        ["CapabilityPowerSorcererInnateSorcery"] = ["PowerSorcererInnateSorcery", "PowerSorcererSorceryIncarnate"],
        ["CapabilityPowerHalfOrcAdrenalineRush"] = ["PowerHalfOrcAdrenalineRush"],
        ["CapabilityPowerRangerNatureShroud"] = ["PowerRangerNatureShroud"],
        ["CapabilityPowerRoguishSlayerCloakOfShadows"] = ["PowerRoguishSlayerCloakOfShadows"],
    };

    // Continued actions retain the original artwork of the ability they belong to.
    private static readonly Dictionary<string, string> SpellDerivedPowerSprites = new(System.StringComparer.Ordinal)
    {
        ["PowerWitchBolt"] = "WitchBolt",
        ["PowerSteelWhirlwindTeleport"] = "SteelWhirlwind",
        ["PowerStrikeWithTheWind"] = "StrikeWithTheWind",
    };

    private static readonly Dictionary<string, string> PowerDerivedPowerSprites = new(System.StringComparer.Ordinal)
    {
        ["PowerInnovationArtilleristEldritchCannonDismiss"] = "PowerInnovationArtilleristEldritchCannonActivate",
        ["PowerGambitCoordinatedAttackCommand"] = "PowerGambitCoordinatedAttackActivate",
    };

    internal static void ApplyDefinitionSprites()
    {
        var assigned = new Dictionary<FeatureDefinitionPower, AssetReferenceSprite>();

        foreach (var family in DefinitionSpriteFamilies)
        {
            var resourceName = family.Key;
            var bitmap = Resources.ResourceManager.GetObject(resourceName, Resources.Culture) as byte[] ??
                         throw new System.InvalidOperationException($"Missing definition artwork: {resourceName}");
            var sprite = GetPowerSprite(resourceName, bitmap);

            foreach (var powerName in family.Value)
            {
                if (DatabaseHelper.TryGetDefinition<FeatureDefinitionPower>(powerName, out var power))
                {
                    assigned[power] = sprite;
                }
            }
        }

        foreach (var source in SpellDerivedPowerSprites)
        {
            if (DatabaseHelper.TryGetDefinition<FeatureDefinitionPower>(source.Key, out var power) &&
                DatabaseHelper.TryGetDefinition<SpellDefinition>(source.Value, out var spell))
            {
                assigned[power] = spell.GuiPresentation.SpriteReference;
            }
        }

        foreach (var source in PowerDerivedPowerSprites)
        {
            if (DatabaseHelper.TryGetDefinition<FeatureDefinitionPower>(source.Key, out var power) &&
                DatabaseHelper.TryGetDefinition<FeatureDefinitionPower>(source.Value, out var parent))
            {
                assigned[power] = assigned.TryGetValue(parent, out var sprite)
                    ? sprite
                    : parent.GuiPresentation.SpriteReference;
            }
        }

        foreach (var assignment in assigned)
        {
            var power = assignment.Key;
            var sprite = assignment.Value;
            SetDefinitionSprite(power, sprite);

            // Bundled selections render generated spell mirrors, not their power definitions.
            var mirror = PowerBundle.GetSpell(power);
            if (mirror)
            {
                SetDefinitionSprite(mirror, sprite);
            }
        }

        // A power's selection tile represents the same ability, including custom invocation pools.
        foreach (var invocation in DatabaseRepository.GetDatabase<InvocationDefinition>())
        {
            if (invocation.GrantedFeature is FeatureDefinitionPower power &&
                assigned.TryGetValue(power, out var sprite))
            {
                SetDefinitionSprite(invocation, sprite);
            }
        }

        // A feature row can be the same ability's presentation, rather than a wider feature group.
        foreach (var featureSet in DatabaseRepository.GetDatabase<FeatureDefinitionFeatureSet>())
        {
            var presentation = featureSet.GuiPresentation;
            if (presentation.Title == null ||
                presentation.Title == Gui.EmptyContent ||
                presentation.Title.IndexOf("/&", System.StringComparison.Ordinal) <= 0 ||
                presentation.Description == null ||
                presentation.Description == Gui.EmptyContent ||
                presentation.Description.IndexOf("/&", System.StringComparison.Ordinal) <= 0)
            {
                continue;
            }

            var matchingSprites = featureSet.FeatureSet
                .OfType<FeatureDefinitionPower>()
                .Where(power => assigned.ContainsKey(power) &&
                                power.GuiPresentation.Title == presentation.Title &&
                                power.GuiPresentation.Description == presentation.Description)
                .Select(power => assigned[power])
                .ToArray();

            if (matchingSprites.Length == 0)
            {
                continue;
            }

            var sprite = matchingSprites[0];
            if (matchingSprites.Any(candidate =>
                    candidate?.AssetGUID != sprite?.AssetGUID ||
                    candidate?.SubObjectName != sprite?.SubObjectName))
            {
                continue;
            }

            SetDefinitionSprite(featureSet, sprite);
        }
    }

    // New Power illustrations and their bundle mirrors share this wide format; glyphs keep their own views.
    internal static AssetReferenceSprite GetPowerSprite(string name, byte[] bitmap)
    {
        const int width = 256;
        const int height = 128;

        if (bitmap == null || bitmap.Length < 24 ||
            bitmap[0] != 137 || bitmap[1] != 80 || bitmap[2] != 78 || bitmap[3] != 71 ||
            bitmap[4] != 13 || bitmap[5] != 10 || bitmap[6] != 26 || bitmap[7] != 10 ||
            bitmap[12] != 73 || bitmap[13] != 72 || bitmap[14] != 68 || bitmap[15] != 82 ||
            ReadPngDimension(bitmap, 16) != width || ReadPngDimension(bitmap, 20) != height)
        {
            throw new System.InvalidOperationException($"Power artwork must be a {width}x{height} PNG: {name}");
        }

        return GetSprite(name, bitmap, width, height);
    }

    private static int ReadPngDimension(byte[] bitmap, int offset)
    {
        return (bitmap[offset] << 24) | (bitmap[offset + 1] << 16) |
               (bitmap[offset + 2] << 8) | bitmap[offset + 3];
    }

    private static void SetDefinitionSprite(BaseDefinition definition, AssetReferenceSprite sprite)
    {
        var presentation = definition.GuiPresentation;
        if (sprite?.RuntimeKeyIsValid() != true ||
            (presentation.SpriteReference?.AssetGUID == sprite.AssetGUID &&
             presentation.SpriteReference.SubObjectName == sprite.SubObjectName))
        {
            return;
        }

        // Some native definitions share a presentation. Changing artwork must not alter another identity.
        definition.GuiPresentation = new GuiPresentation(presentation)
        {
            spriteReference = sprite,
            // The native copy constructor omits this DLC presentation flag.
            usedInValleyDLC = presentation.usedInValleyDLC
        };
    }

    #endregion

    #region CustomSprites

    private static readonly Dictionary<string, Sprite> SpritesByGuid = new();

    /// <summary>
    ///     Convert a bitmap stored as an embedded resource to a Sprite.
    /// </summary>
    [NotNull]
    internal static Sprite GetOrCreateSprite(
        string name,
        byte[] bitmap,
        int size)
    {
        return GetOrCreateSprite(name, bitmap, size, size);
    }

    [NotNull]
    private static Sprite GetOrCreateSprite(
        string name,
        byte[] bitmap,
        int sizeX,
        int sizeY)
    {
        var (id, guid) = GetSpriteId(name, sizeX, sizeY);

        if (SpritesByGuid.TryGetValue(guid, out var sprite))
        {
            return sprite;
        }

        // LoadImage recompresses PNGs when the destination starts in DXT5, damaging small glyphs and alpha edges.
        var texture = new Texture2D(sizeX, sizeY, TextureFormat.RGBA32, false);

        try
        {
            if (!texture.LoadImage(bitmap))
            {
                throw new System.InvalidOperationException($"Cannot decode sprite artwork: {name}");
            }

            // LoadImage replaces the texture dimensions. Use the complete artwork rather than cropping it
            // to the legacy cache dimensions; intentional regions are handled by GetSpriteFromAtlas.
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0, 0));
        }
        catch
        {
            Object.Destroy(texture);
            throw;
        }

        SpritesByGuid[guid] = sprite;
        sprite.name = id;

        return sprite;
    }

    [CanBeNull]
    internal static Sprite GetSpriteByGuid([NotNull] string guid)
    {
        return SpritesByGuid.TryGetValue(guid, out var sprite) ? sprite : null;
    }

    internal static void Unload()
    {
        var textures = new HashSet<Texture>();

        foreach (var sprite in SpritesByGuid.Values.Where(x => x))
        {
            if (sprite.texture)
            {
                textures.Add(sprite.texture);
            }

            Object.Destroy(sprite);
        }

        SpritesByGuid.Clear();

        foreach (var texture in textures.Where(x => x))
        {
            Object.Destroy(texture);
        }
    }

    /// <summary>
    ///     Create a unique Id to serve as id of a sprite in our internal cache and as the guid for AssetReference
    /// </summary>
    /// <param name="name"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    private static (string id, string guid) GetSpriteId(string name, int x, int y)
    {
        var id = $"{CustomSpriteNamePrefix}{name}[{x},{y}]";

        return (id, GetSpriteGuid(id));
    }

    internal static bool IsCustomSprite([CanBeNull] Sprite sprite)
    {
        return sprite && sprite.name.StartsWith(CustomSpriteNamePrefix, System.StringComparison.Ordinal);
    }

    [NotNull]
    private static string GetSpriteGuid([NotNull] string id)
    {
        return GuidHelper.Create(DefinitionBuilder.CeNamespaceGuid, id).ToString();
    }

    [NotNull]
    internal static AssetReferenceSprite GetSprite(
        string name,
        byte[] bitmap,
        int size)
    {
        return GetSprite(name, bitmap, size, size);
    }

    [NotNull]
    internal static AssetReferenceSprite GetSprite(
        string name,
        byte[] bitmap,
        int sizeX,
        int sizeY)
    {
        var sprite = GetOrCreateSprite(name, bitmap, sizeX, sizeY);

        return new AssetReferenceSprite(GetSpriteGuid(sprite.name));
    }

    // Atlas regions share the cached texture and participate in the same sprite unload lifecycle.
    internal static AssetReferenceSprite GetSpriteFromAtlas(
        string name, string atlasName, byte[] bitmap, Vector2Int atlasSize, Rect region)
    {
        var (id, guid) = GetSpriteId(name, (int)region.width, (int)region.height);

        if (!SpritesByGuid.ContainsKey(guid))
        {
            var atlas = GetOrCreateSprite(atlasName, bitmap, atlasSize.x, atlasSize.y);
            var sprite = Sprite.Create(atlas.texture, region, new Vector2(0.5f, 0.5f));

            sprite.name = id;
            SpritesByGuid[guid] = sprite;
        }

        return new AssetReferenceSprite(guid);
    }

    #endregion
}
