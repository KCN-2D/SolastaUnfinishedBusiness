using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.Displays;
using SolastaUnfinishedBusiness.Interfaces;
using TA.AI;
using static SolastaUnfinishedBusiness.Spells.SpellBuilders;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellListDefinitions;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.MonsterDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class SpellsContext
{
    private const string InvisibleStalkerSubspellName = "ConjureElementalInvisibleStalker";

    internal static readonly Dictionary<SpellDefinition, SpellDefinition> SpellsChildMaster = [];
    internal static readonly Dictionary<SpellListDefinition, SpellListContext> SpellListContextTab = [];
    private static readonly HashSet<SpellDefinition> SpellsAvailableWithSpellLists2024Only = [];

    internal static readonly SpellListDefinition EmptySpellList = SpellListDefinitionBuilder
        .Create("SpellListEmpty")
        .SetGuiPresentationNoContent(true)
        .ClearSpells()
        .FinalizeSpells(false)
        .AddToDB();

    private static readonly DecisionPackageDefinition DecisionPackageRestrained =
        AiHelpers.BuildDecisionPackageBreakFree(ConditionRestrainedByEntangle.Name);

    // ReSharper disable once InconsistentNaming
    private static readonly SortedList<string, SpellListDefinition> spellLists = [];
    private static readonly Dictionary<SpellDefinition, List<SpellListDefinition>> SpellSpellListMap = [];
    private static readonly CharacterClassDefinition[] SpellSniperClasses = [Cleric, Druid, Sorcerer, Warlock, Wizard];

    internal static readonly SpellDefinition AirBlast = BuildAirBlast();
    internal static readonly SpellDefinition AshardalonStride = BuildAshardalonStride();
    internal static readonly SpellDefinition AuraOfLife = BuildAuraOfLife();
    internal static readonly SpellDefinition AuraOfVitality = BuildAuraOfVitality();
    internal static readonly SpellDefinition BanishingSmite = BuildBanishingSmite();
    internal static readonly SpellDefinition BindingIce = BuildBindingIce();
    internal static readonly SpellDefinition BladeWard = BuildBladeWard();
    internal static readonly SpellDefinition BlessingOfRime = BuildBlessingOfRime();
    internal static readonly SpellDefinition BlindingSmite = BuildBlindingSmite();
    internal static readonly SpellDefinition BurstOfRadiance = BuildBurstOfRadiance();
    internal static readonly SpellDefinition Command = BuildCommand();
    internal static readonly SpellDefinition CorruptingBolt = BuildCorruptingBolt();
    internal static readonly SpellDefinition CausticZap = BuildCausticZap();
    internal static readonly SpellDefinition ColorBurst = BuildColorBurst();
    internal static readonly SpellDefinition DivineWrath = BuildDivineWrath();
    internal static readonly SpellDefinition ElementalInfusion = BuildElementalInfusion();
    internal static readonly SpellDefinition ElementalWeapon = BuildElementalWeapon();
    internal static readonly SpellDefinition EarthTremor = BuildEarthTremor();
    internal static readonly SpellDefinition EnduringSting = BuildEnduringSting();
    internal static readonly SpellDefinition EnsnaringStrike = BuildEnsnaringStrike();
    internal static readonly SpellDefinition FarStep = BuildFarStep();
    internal static readonly SpellDefinition MaddeningDarkness = BuildMaddeningDarkness();
    internal static readonly SpellDefinition MantleOfThorns = BuildMantleOfThorns();
    internal static readonly SpellDefinition MirrorImage = BuildMirrorImage();
    internal static readonly SpellDefinition PetalStorm = BuildPetalStorm();
    internal static readonly SpellDefinition PowerWordHeal = BuildPowerWordHeal();
    internal static readonly SpellDefinition PowerWordKill = BuildPowerWordKill();
    internal static readonly SpellDefinition PsychicLance = BuildPsychicLance();
    internal static readonly SpellDefinition PsychicWhip = BuildPsychicWhip();
    internal static readonly SpellDefinition PulseWave = BuildPulseWave();
    internal static readonly SpellDefinition SearingSmite = BuildSearingSmite();
    internal static readonly SpellDefinition SilveryBarbs = BuildSilveryBarbs();
    internal static readonly SpellDefinition SorcerousBurst = BuildSorcerousBurst();
    internal static readonly SpellDefinition SonicBoom = BuildSonicBoom();
    internal static readonly SpellDefinition StaggeringSmite = BuildStaggeringSmite();
    internal static readonly SpellDefinition SteelWhirlwind = BuildSteelWhirlwind();
    internal static readonly SpellDefinition SunlightBlade = BuildSunlightBlade();
    internal static readonly SpellDefinition Telekinesis = BuildTelekinesis();
    internal static readonly SpellDefinition ThornyVines = BuildThornyVines();
    internal static readonly SpellDefinition ThunderousSmite = BuildThunderousSmite();
    internal static readonly SpellDefinition VitriolicSphere = BuildVitriolicSphere();
    internal static readonly SpellDefinition Web = BuildWeb();
    internal static readonly SpellDefinition Wrack = BuildWrack();
    internal static readonly SpellDefinition WrathfulSmite = BuildWrathfulSmite();

    private static SpellDefinition ConjureElementalInvisibleStalker { get; set; }
    internal static List<SpellDefinition> Spells { get; private set; } = [];

    [NotNull]
    internal static SortedList<string, SpellListDefinition> SpellLists
    {
        get
        {
            if (spellLists.Count != 0)
            {
                return spellLists;
            }

            // only this sub matters for spell selection. this might change if we add additional subs to mod
            var characterSubclass = CharacterSubclassDefinitions.TraditionLight;

            var title = characterSubclass.FormatTitle();

            var featureDefinitions = characterSubclass.FeatureUnlocks
                .Select(x => x.FeatureDefinition)
                .Where(x => x is FeatureDefinitionCastSpell or FeatureDefinitionMagicAffinity);

            foreach (var featureDefinition in featureDefinitions)
            {
                switch (featureDefinition)
                {
                    case FeatureDefinitionMagicAffinity featureDefinitionMagicAffinity
                        when featureDefinitionMagicAffinity.ExtendedSpellList &&
                             !spellLists.ContainsValue(featureDefinitionMagicAffinity.ExtendedSpellList):
                        spellLists.Add(title, featureDefinitionMagicAffinity.ExtendedSpellList);

                        foreach (var spell in featureDefinitionMagicAffinity.ExtendedSpellList.SpellsByLevel.SelectMany(
                                     x => x.Spells))
                        {
                            if (!SpellSpellListMap.TryGetValue(spell, out var value))
                            {
                                value = [];
                                SpellSpellListMap.Add(spell, value);
                            }

                            value.Add(featureDefinitionMagicAffinity.ExtendedSpellList);
                        }

                        break;
                    case FeatureDefinitionCastSpell featureDefinitionCastSpell
                        when featureDefinitionCastSpell.SpellListDefinition &&
                             !spellLists.ContainsValue(featureDefinitionCastSpell.SpellListDefinition):
                        spellLists.Add(title, featureDefinitionCastSpell.SpellListDefinition);

                        foreach (var spell in featureDefinitionCastSpell.SpellListDefinition.SpellsByLevel.SelectMany(
                                     x => x.Spells))
                        {
                            if (!SpellSpellListMap.TryGetValue(spell, out var value))
                            {
                                value = [];
                                SpellSpellListMap.Add(spell, value);
                            }

                            value.Add(featureDefinitionCastSpell.SpellListDefinition);
                        }

                        break;
                }
            }

            var dbCharacterClassDefinition = DatabaseRepository.GetDatabase<CharacterClassDefinition>();

            foreach (var characterClass in dbCharacterClassDefinition)
            {
                title = characterClass.FormatTitle();

                var featureDefinitionCastSpell = characterClass.FeatureUnlocks
                    .Select(x => x.FeatureDefinition)
                    .OfType<FeatureDefinitionCastSpell>()
                    .FirstOrDefault();

                // NOTE: don't use featureDefinitionCastSpell?. which bypasses Unity object lifetime check
                if (!featureDefinitionCastSpell
                    || !featureDefinitionCastSpell.SpellListDefinition
                    || spellLists.ContainsValue(featureDefinitionCastSpell.SpellListDefinition))
                {
                    continue;
                }

                spellLists.Add(title, featureDefinitionCastSpell.SpellListDefinition);

                foreach (var spell in featureDefinitionCastSpell.SpellListDefinition.SpellsByLevel.SelectMany(
                             x => x.Spells))
                {
                    if (!SpellSpellListMap.TryGetValue(spell, out var value))
                    {
                        value = [];
                        SpellSpellListMap.Add(spell, value);
                    }

                    value.Add(featureDefinitionCastSpell.SpellListDefinition);
                }
            }

            return spellLists;
        }
    }

    internal static bool IsAllSetSelected()
    {
        return SpellListContextTab.Values.All(spellListContext => spellListContext.IsAllSetSelected);
    }

    internal static bool IsSuggestedSetSelected()
    {
        return SpellListContextTab.Values.All(spellListContext => spellListContext.IsSuggestedSetSelected);
    }

    internal static bool IsTabletopSetSelected()
    {
        return SpellListContextTab.Values.All(spellListContext => spellListContext.IsTabletopSetSelected);
    }

    internal static void SelectAllSet(bool toggle)
    {
        foreach (var spellListContext in SpellListContextTab.Values)
        {
            spellListContext.SelectAllSetInternal(toggle);
        }
    }

    internal static void SelectSuggestedSet(bool toggle)
    {
        foreach (var spellListContext in SpellListContextTab.Values)
        {
            spellListContext.SelectSuggestedSetInternal(toggle);
        }
    }

    internal static void SelectTabletopSet(bool toggle)
    {
        foreach (var spellListContext in SpellListContextTab.Values)
        {
            spellListContext.SelectTabletopSetInternal(toggle);
        }
    }

    internal static void RecalculateDisplayedSpells()
    {
        foreach (var spellListContext in SpellListContextTab.Values)
        {
            spellListContext.CalculateDisplayedSpellsInternal();
        }
    }

    internal static void RecalculateAllSpells()
    {
        foreach (var spellListContext in SpellListContextTab.Values)
        {
            spellListContext.CalculateAllSpells();
            spellListContext.CalculateDisplayedSpellsInternal();
        }
    }

    internal static void ApplySpellList2024Change(
        SpellListDefinition spellList,
        SpellDefinition spell,
        bool included,
        bool enabled)
    {
        SpellListContextTab[spellList].ApplySpellList2024Change(spell, included, enabled);
    }

    internal static void SwitchSpellAvailabilityWithSpellLists2024(
        SpellDefinition spell,
        bool enabled)
    {
        foreach (var spellListContext in SpellListContextTab.Values)
        {
            spellListContext.SetConditionalSpellAvailability(spell, enabled);
        }
    }

    internal static void ApplySpellList2024Restrictions(bool enabled)
    {
        foreach (var spellListContext in SpellListContextTab.Values)
        {
            spellListContext.ApplySpellList2024Restrictions(enabled);
        }
    }

    private static bool IsUbOriginalSpell(SpellDefinition spellDefinition)
    {
        return spellDefinition.ContentPack == CeContentPackContext.CeContentPack &&
               !ModUi.TabletopDefinitionNames.Contains(spellDefinition.Name);
    }

    private static bool IsSpellList2024RestrictedSpell(SpellDefinition spellDefinition)
    {
        return Main.Settings.EnableSpellLists2024 && IsUbOriginalSpell(spellDefinition);
    }

    private static void NormalizeBaseSpellDurations()
    {
        // Shillelagh lasts one minute in both the 2014 and 2024 tabletop rules.
        Shillelagh.EffectDescription.DurationType = DurationType.Minute;
        Shillelagh.EffectDescription.DurationParameter = 1;
    }

    internal static void LateLoad()
    {
        NormalizeBaseSpellDurations();

        // init collections
        foreach (var spellList in SpellLists.Values)
        {
            var name = spellList.Name;

            SpellListContextTab.Add(spellList, new SpellListContext(spellList));

            Main.Settings.SpellListSpellEnabled.TryAdd(name, []);
            Main.Settings.DisplaySpellListsToggle.TryAdd(name, false);
            Main.Settings.SpellListSliderPosition.TryAdd(name, 4);
        }

        var spellListInventorClass = InventorClass.SpellList;

        // MUST COME BEFORE ANY MOD REGISTERED SPELL
        foreach (var kvp in SpellSpellListMap)
        {
            RegisterSpell(kvp.Key, kvp.Value.Count, kvp.Value.ToArray());
        }

        // cantrips
        RegisterSpell(AirBlast, 0, SpellListDruid, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BladeWard, 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildBoomingBlade(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(BurstOfRadiance, 0, SpellListCleric);
        RegisterSpell(BuildCreateBonfire(), 0, SpellListDruid, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(EnduringSting, 0, SpellListWizard);
        RegisterSpell(BuildIlluminatingSphere(), 0, SpellListBard, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildInfestation(), 0, SpellListDruid, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildLightningLure(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        //RegisterSpell(BuildMagicStone(), 0, SpellListDruid, SpellListWarlock, spellListInventorClass);
        RegisterSpell(BuildMindSpike(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildMinorLifesteal(), 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildPrimalSavagery(), 0, SpellListDruid);
        RegisterSpell(BuildResonatingStrike(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(SunlightBlade, 0, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(BuildSwordStorm(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        SpellsAvailableWithSpellLists2024Only.Add(SorcerousBurst);
        RegisterSpell(SorcerousBurst);
        RegisterSpell(BuildStarryWisp(), 0, SpellListBard, SpellListDruid);
        RegisterSpell(BuildTollTheDead(), 0, SpellListCleric, SpellListWarlock, SpellListWizard);
        RegisterSpell(ThornyVines, 0, SpellListDruid, spellListInventorClass);
        RegisterSpell(BuildThunderStrike(), 0, SpellListBard, SpellListDruid, SpellListSorcerer, SpellListWarlock,
            SpellListWizard, spellListInventorClass);
        RegisterSpell(Wrack, 0, SpellListCleric);

        // 1st level
        RegisterSpell(CausticZap, 0, SpellListSorcerer, SpellListWizard, spellListInventorClass);
        RegisterSpell(Command, 0, SpellListBard, SpellListPaladin, SpellListCleric);
        RegisterSpell(BuildChaosBolt(), 0, SpellListSorcerer);
        RegisterSpell(BuildChromaticOrb(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildDissonantWhispers(), 0, SpellListBard);
        RegisterSpell(EarthTremor, 0, SpellListBard, SpellListDruid, SpellListSorcerer, SpellListWizard);
        RegisterSpell(EnsnaringStrike, 0, SpellListRanger);
        RegisterSpell(ElementalInfusion, 0, SpellListDruid, SpellListRanger, SpellListSorcerer, SpellListWizard, spellListInventorClass);
        RegisterSpell(BuildFindFamiliar(), 0, SpellListWizard);
        RegisterSpell(BuildGiftOfAlacrity(), 0, SpellListWizard);
        RegisterSpell(BuildGoneWithTheWind(), 0, SpellListRanger);
        RegisterSpell(BuildIceBlade(), 0, SpellListDruid, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildMagnifyGravity(), 0, SpellListWizard);
        RegisterSpell(BuildMule(), 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildRadiantMotes(), 0, SpellListWizard, spellListInventorClass);
        RegisterSpell(BuildRayOfSickness(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildSanctuary(), 0, SpellListCleric, spellListInventorClass);
        RegisterSpell(SearingSmite, 0, SpellListPaladin, SpellListRanger);
        RegisterSpell(SilveryBarbs, 0, SpellListBard, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildSkinOfRetribution(), 0, SpellListWarlock);
        RegisterSpell(BuildSpikeBarrage(), 0, SpellListRanger);
        RegisterSpell(ThunderousSmite, 0, SpellListPaladin);
        RegisterSpell(BuildVileBrew(), 0, SpellListSorcerer, SpellListWizard, spellListInventorClass);
        RegisterSpell(BuildVoidGrasp(), 0, SpellListWarlock);
        RegisterSpell(BuildWitchBolt(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(WrathfulSmite, 0, SpellListPaladin);

        // 2nd level
        RegisterSpell(BuildAganazzarScorcher(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BindingIce, 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildBorrowedKnowledge(), 0, SpellListBard, SpellListCleric, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildCloudOfDaggers(), 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(ColorBurst, 0, SpellListSorcerer, SpellListWizard, spellListInventorClass);
        ConjureGoblinoids.contentPack = CeContentPackContext.CeContentPack;
        RegisterSpell(ConjureGoblinoids, 0, SpellListDruid, SpellListRanger);
        RegisterSpell(BuildKineticJaunt(), 0, SpellListBard, SpellListSorcerer, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(BuildDragonsBreath(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildNoxiousSpray(), 0, SpellListDruid, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(PetalStorm, 0, SpellListDruid);
        RegisterSpell(BuildProtectThreshold(), 0, SpellListCleric, SpellListDruid, SpellListPaladin);
        RegisterSpell(PsychicWhip, 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(MirrorImage, 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildShadowBlade(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildSnillocSnowballStorm(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(Web, 0, SpellListSorcerer, SpellListWizard, spellListInventorClass);
        RegisterSpell(BuildWitherAndBloom(), 0, SpellListDruid, SpellListSorcerer, SpellListWizard);

        // 3rd level
        RegisterSpell(BuildAdderFangs(), 0, SpellListDruid, SpellListRanger, SpellListSorcerer, SpellListWarlock);
        RegisterSpell(AshardalonStride, 0, SpellListRanger, SpellListSorcerer, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(AuraOfVitality, 0, SpellListCleric, SpellListPaladin);
        RegisterSpell(BlindingSmite, 0, SpellListPaladin);
        RegisterSpell(BuildBoomingStep(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(CorruptingBolt, 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildCrusadersMantle(), 0, SpellListPaladin);
        RegisterSpell(ElementalWeapon, 0, SpellListDruid, SpellListPaladin, SpellListRanger, spellListInventorClass);
        RegisterSpell(BuildHungerOfTheVoid(), 0, SpellListWarlock);
        RegisterSpell(PulseWave, 0, SpellListWizard);
        RegisterSpell(BuildFlameArrows(), 0, SpellListDruid, SpellListRanger, SpellListSorcerer, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(BuildLightningArrow(), 0, SpellListRanger);
        RegisterSpell(BuildIntellectFortress(), 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(BuildSpiritShroud(), 0, SpellListCleric, SpellListPaladin, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildVitalityTransfer(), 0, SpellListCleric, SpellListWizard);
        RegisterSpell(BuildWinterBreath(), 0, SpellListDruid, SpellListSorcerer, SpellListWizard);

        // 4th level
        RegisterSpell(BuildAuraOfPerseverance(), 0, SpellListCleric, SpellListPaladin);
        RegisterSpell(AuraOfLife, 0, SpellListCleric, SpellListPaladin);
        RegisterSpell(BlessingOfRime, 0, SpellListBard, SpellListDruid, SpellListRanger);
        RegisterSpell(BuildBrainBulwark(), 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(BuildElementalBane(), 0, SpellListDruid, SpellListWarlock, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(BuildFaithfulHound(), 0, SpellListWizard, spellListInventorClass);
        RegisterSpell(BuildForestGuardian(), 0, SpellListDruid, SpellListRanger);
        RegisterSpell(BuildGravitySinkhole(), 0, SpellListWizard);
        RegisterSpell(BuildIrresistiblePerformance(), 0, SpellListBard);
        RegisterSpell(PsychicLance, 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildPsionicBlast(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildSickeningRadiance(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(StaggeringSmite, 0, SpellListPaladin);
        RegisterSpell(VitriolicSphere, 0, SpellListSorcerer, SpellListWizard);

        //5th level
        RegisterSpell(BanishingSmite, 0, SpellListPaladin);
        RegisterSpell(BuildCircleOfMagicalNegation(), 0, SpellListPaladin);
        RegisterSpell(BuildDawn(), 0, SpellListCleric, SpellListWizard);
        RegisterSpell(DivineWrath, 0, SpellListPaladin);
        RegisterSpell(BuildEmpoweredKnowledge(), 0, SpellListBard, SpellListSorcerer, SpellListWizard,
            spellListInventorClass);
        RegisterSpell(FarStep, 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildHolyWeapon(), 0, SpellListCleric, SpellListPaladin);
        RegisterSpell(BuildIncineration(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(MantleOfThorns, 0, SpellListDruid);
        RegisterSpell(SteelWhirlwind, 0, SpellListRanger, SpellListWizard);
        RegisterSpell(SonicBoom, 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildSwiftQuiver(), 0, SpellListRanger);
        RegisterSpell(BuildSynapticStatic(), 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(Telekinesis, 0, SpellListSorcerer, SpellListWizard);

        // 6th level
        RegisterSpell(BuildFlashFreeze(), 0, SpellListDruid, SpellListSorcerer, SpellListWarlock);
        RegisterSpell(GravityFissure, 0, SpellListWizard);
        RegisterSpell(BuildHeroicInfusion(), 0, SpellListWizard);
        RegisterSpell(BuildMysticalCloak(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildPoisonWave(), 0, SpellListWizard);
        RegisterSpell(BuildFizbanPlatinumShield(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildRingOfBlades(), 0, SpellListWizard);
        RegisterSpell(BuildScatter(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildShelterFromEnergy(), 0, SpellListCleric, SpellListDruid, SpellListSorcerer, SpellListWizard);

        // 7th level
        RegisterSpell(BuildCrownOfStars(), 0, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildDraconicTransformation(), 0, SpellListDruid, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildRescueTheDying(), 0, SpellListCleric, SpellListDruid);
        RegisterSpell(BuildReverseGravity(), 0, SpellListDruid, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildSimulacrum(), 0, SpellListWizard);

        // 8th level
        RegisterSpell(BuildAbiDalzimHorridWilting(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(BuildGlibness(), 0, SpellListBard, SpellListWarlock);
        RegisterSpell(BuildMindBlank(), 0, SpellListBard, SpellListWizard);
        RegisterSpell(MaddeningDarkness, 0, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildSoulExpulsion(), 0, SpellListCleric, SpellListSorcerer, SpellListWizard);

        // 9th level
        RegisterSpell(BuildForesight(), 0, SpellListBard, SpellListDruid, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildInvulnerability(), 0, SpellListWizard);
        RegisterSpell(BuildMassHeal(), 0, SpellListCleric);
        RegisterSpell(BuildMeteorSwarmSingleTarget(), 0, SpellListSorcerer, SpellListWizard);
        RegisterSpell(PowerWordHeal, 0, SpellListBard, SpellListCleric);
        RegisterSpell(PowerWordKill, 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildPsychicScream(), 0, SpellListBard, SpellListSorcerer, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildTimeStop(), 0, SpellListWizard, SpellListSorcerer);
        RegisterSpell(BuildShapechange(), 0, SpellListDruid, SpellListWizard);
        RegisterSpell(BuildWeird(), 0, SpellListWarlock, SpellListWizard);
        RegisterSpell(BuildWish(), 0, SpellListSorcerer, SpellListWizard);

        Spells = [.. Spells.OrderBy(x => x.SpellLevel).ThenBy(x => x.FormatTitle())];

        foreach (var kvp in SpellListContextTab)
        {
            // caches which spells are toggleable per spell list
            var spellListContext = kvp.Value;

            spellListContext.CalculateAllSpells();
            spellListContext.CalculateDisplayedSpellsInternal();

            // settings paring
            var spellListName = kvp.Key.Name;

            foreach (var name in Main.Settings.SpellListSpellEnabled[spellListName]
                         .Where(name => Spells.All(x => x.Name != name))
                         .ToArray())
            {
                Main.Settings.SpellListSpellEnabled[spellListName].Remove(name);
            }
        }

        // cache spells with subs
        foreach (var parent in DatabaseRepository.GetDatabase<SpellDefinition>()
                     .Where(x => x.spellsBundle))
        {
            foreach (var child in parent.SubspellsList)
            {
                // tryAdd to avoid AtWill spells to mess up this collection
                SpellsChildMaster.TryAdd(child, parent);
            }
        }

        // bootstrap

        BuildConjureElementalInvisibleStalker();

        LoadAllowTargetingSelectionWhenCastingChainLightningSpell();


        SwitchAddBleedingToLesserRestoration();
        SwitchChangeSleetStormToCube();
        SwitchEnableUpcastConjureElementalAndFey();
        SwitchFilterOnHideousLaughter();
        SwitchAllowBladeCantripsToUseReach();
        SwitchHastedCasing();
        SwitchAllowTargetingSelectionWhenCastingChainLightningSpell();
        SwitchRecurringEffectOnEntangle();
        SwitchUseHeightOneCylinderEffect();
        SwitchDimensionDoorRange();
    }

    internal static HashSet<SpellDefinition> GetActiveSpells(int maximumSpellLevel)
    {
        return SpellLists.Values
            .SelectMany(spellList => spellList.SpellsByLevel)
            .Where(spellsByLevel => spellsByLevel.Level <= maximumSpellLevel)
            .SelectMany(spellsByLevel => spellsByLevel.Spells)
            .Where(spell => spell is { SpellLevel: >= 0 } && spell.SpellLevel <= maximumSpellLevel)
            .ToHashSet();
    }

    private static void RegisterSpell(
        SpellDefinition spellDefinition,
        int suggestedStartsAt = 0,
        params SpellListDefinition[] registeredSpellLists)
    {
        if (!Spells.TryAdd(spellDefinition))
        {
            return;
        }

        for (var i = 0; i < registeredSpellLists.Length; i++)
        {
            var spellList = registeredSpellLists[i];

            if (i < suggestedStartsAt)
            {
                SpellListContextTab[spellList].MinimumSpells.Add(spellDefinition);
            }
            else
            {
                SpellListContextTab[spellList].SuggestedSpells.Add(spellDefinition);
            }
        }

        foreach (var spellList in SpellLists.Values)
        {
            if (!Main.Settings.SpellListSpellEnabled.ContainsKey(spellList.Name))
            {
                continue;
            }

            if (SpellListContextTab[spellList].MinimumSpells.Contains(spellDefinition))
            {
                continue;
            }

            var enable = Main.Settings.SpellListSpellEnabled[spellList.Name].Contains(spellDefinition.Name);

            SpellListContextTab[spellList].Switch(spellDefinition, enable);
        }

        RefreshAuxiliarySpellLists(spellDefinition);
    }

    private static void RefreshAuxiliarySpellLists(SpellDefinition spellDefinition)
    {
        if (spellDefinition.contentPack != CeContentPackContext.CeContentPack)
        {
            return;
        }

        var isConditional = SpellsAvailableWithSpellLists2024Only.Contains(spellDefinition);
        var isAvailable = (!isConditional || Main.Settings.EnableSpellLists2024) &&
                          !IsSpellList2024RestrictedSpell(spellDefinition);
        var isActive = SpellLists.Values.Any(x => x.ContainsSpell(spellDefinition));

        if (isActive && isAvailable)
        {
            if (spellDefinition.SpellLevel == 0)
            {
                SpellListAllCantrips.AddSpell(spellDefinition);
            }

            SpellListAllSpells.AddSpell(spellDefinition);
        }
        else if (!isAvailable)
        {
            SpellListAllCantrips.RemoveSpell(spellDefinition);
            SpellListAllSpells.RemoveSpell(spellDefinition);
        }

        if (spellDefinition.SpellLevel != 0)
        {
            return;
        }

        foreach (var spellSniperClass in SpellSniperClasses)
        {
            if (!TryGetDefinition<SpellListDefinition>(
                    $"SpellListFeatSpellSniper{spellSniperClass.Name}", out var spellListSniper))
            {
                continue;
            }

            if (isActive && isAvailable)
            {
                spellListSniper.AddSpell(spellDefinition);
            }
            else if (!isAvailable)
            {
                spellListSniper.RemoveSpell(spellDefinition);
            }
        }
    }

    private static void LoadAllowTargetingSelectionWhenCastingChainLightningSpell()
    {
        ChainLightning.AddCustomSubFeatures(new FilterTargetingCharacterChainLightning());
    }

    internal static void SwitchAllowTargetingSelectionWhenCastingChainLightningSpell()
    {
        var spell = ChainLightning.EffectDescription;

        if (Main.Settings.AllowTargetingSelectionWhenCastingChainLightningSpell)
        {
            spell.targetType = TargetType.IndividualsUnique;
            spell.targetParameter = 4;
            spell.effectAdvancement.additionalTargetsPerIncrement = 1;
        }
        else
        {
            spell.targetType = TargetType.ArcFromIndividual;
            spell.targetParameter = 3;
            spell.effectAdvancement.additionalTargetsPerIncrement = 0;
        }
    }

    internal static void SwitchFilterOnHideousLaughter()
    {
        HideousLaughter.effectDescription.restrictedCreatureFamilies.Clear();

        if (!Main.Settings.RemoveHumanoidFilterOnHideousLaughter)
        {
            HideousLaughter.effectDescription.restrictedCreatureFamilies.Add(CharacterFamilyDefinitions.Humanoid.Name);
        }
    }

    internal static void SwitchRecurringEffectOnEntangle()
    {
        // Remove recurring effect on Entangle (as per SRD, any creature is only affected at cast time)
        if (Main.Settings.RemoveRecurringEffectOnEntangle)
        {
            Entangle.effectDescription.recurrentEffect = RecurrentEffect.OnActivation;
            Entangle.effectDescription.EffectForms[2].canSaveToCancel = false;
            ConditionRestrainedByEntangle.Features.Add(FeatureDefinitionActionAffinitys.ActionAffinityGrappled);
            ConditionRestrainedByEntangle.amountOrigin = ConditionDefinition.OriginOfAmount.Fixed;
            ConditionRestrainedByEntangle.baseAmount = (int)AiHelpers.BreakFreeType.DoStrengthCheckAgainstCasterDC;
            ConditionRestrainedByEntangle.addBehavior = true;
            ConditionRestrainedByEntangle.battlePackage = DecisionPackageRestrained;
        }
        else
        {
            Entangle.effectDescription.recurrentEffect =
                RecurrentEffect.OnActivation | RecurrentEffect.OnTurnEnd | RecurrentEffect.OnEnter;
            Entangle.effectDescription.EffectForms[2].canSaveToCancel = true;
            ConditionRestrainedByEntangle.Features.Remove(FeatureDefinitionActionAffinitys.ActionAffinityGrappled);
            ConditionRestrainedByEntangle.amountOrigin = ConditionDefinition.OriginOfAmount.None;
            ConditionRestrainedByEntangle.baseAmount = 0;
            ConditionRestrainedByEntangle.addBehavior = false;
            ConditionRestrainedByEntangle.battlePackage = null;
        }
    }

    internal static void SwitchChangeSleetStormToCube()
    {
        var sleetStormEffect = SleetStorm.EffectDescription;

        if (Main.Settings.ChangeSleetStormToCube)
        {
            // Set to Cube side 8, default height
            sleetStormEffect.targetType = TargetType.Cube;
            sleetStormEffect.targetParameter = 8;
            sleetStormEffect.targetParameter2 = 0;
        }
        else
        {
            // Restore to cylinder radius 4, height 3
            sleetStormEffect.targetType = TargetType.Cylinder;
            sleetStormEffect.targetParameter = 4;
            sleetStormEffect.targetParameter2 = 3;
        }
    }

    internal static void SwitchDimensionDoorRange()
    {
        DimensionDoor.EffectDescription.rangeParameter = Main.Settings.Use24CellsRangeWithDimensionDoor ? 24 : 12;
    }

    internal static void SwitchUseHeightOneCylinderEffect()
    {
        // always applicable
        ClearTargetParameter2ForTargetTypeCube();

        // Change SpikeGrowth to be height 1 round cylinder/sphere
        var spikeGrowthEffect = SpikeGrowth.EffectDescription;

        spikeGrowthEffect.targetParameter = 4;

        if (Main.Settings.UseHeightOneCylinderEffect)
        {
            // Set to Cylinder radius 4, height 1
            spikeGrowthEffect.targetType = TargetType.Cylinder;
            spikeGrowthEffect.targetParameter2 = 1;
        }
        else
        {
            // Restore default of Sphere radius 4
            spikeGrowthEffect.targetType = TargetType.Sphere;
            spikeGrowthEffect.targetParameter2 = 0;
        }

        // Spells with TargetType.Cube and defaults values of (tp, tp2)
        // Note that tp2 should be 0 for Cube and is ignored in game.
        // BlackTentacles: (4, 2)
        // Entangle: (4, 1)
        // FaerieFire: (4, 2)
        // FlamingSphere: (3, 2) <- a flaming sphere is a cube?
        // Grease: (2, 2)
        // HypnoticPattern: (6, 2)
        // Slow: (8, 2)
        // Thunderwave: (3, 2)

        // Change Black Tentacles, Entangle, Grease to be height 1 square cylinder/cube
        if (Main.Settings.UseHeightOneCylinderEffect)
        {
            // Setting height switches to square cylinder (if originally cube)
            SetHeight(BlackTentacles, 1);
            SetHeight(Entangle, 1);
            SetHeight(Grease, 1);
        }
        else
        {
            // Setting height to 0 restores original behaviour
            SetHeight(BlackTentacles, 0);
            SetHeight(Entangle, 0);
            SetHeight(Grease, 0);
        }

        return;

        static void SetHeight([NotNull] IMagicEffect spellDefinition, int height)
        {
            spellDefinition.EffectDescription.targetParameter2 = height;
        }

        static void ClearTargetParameter2ForTargetTypeCube()
        {
            foreach (var sd in DatabaseRepository
                         .GetDatabase<SpellDefinition>()
                         .Where(sd =>
                             sd.EffectDescription.TargetType is TargetType.Cube
                                 or TargetType.CubeWithOffset))
            {
                // TargetParameter2 is not used by TargetType.Cube but has random values assigned.
                // We are going to use it to create a square cylinder with height so set to zero for all spells with TargetType.Cube.
                sd.EffectDescription.targetParameter2 = 0;
            }
        }
    }

    internal static void SwitchAllowBladeCantripsToUseReach()
    {
        var db = DatabaseRepository.GetDatabase<SpellDefinition>();
        var cantrips = new List<string> { "BoomingBlade", "ResonatingStrike", "SunlightBlade" };

        foreach (var bladeCantrip in db.Where(x => cantrips.Contains(x.Name)))
        {
            var text = Main.Settings.AllowBladeCantripsToUseReach ? "Feedback/&WithinReach" : "Feedback/&Within5Ft";

            bladeCantrip.GuiPresentation.Description = Gui.Format($"Spell/&{bladeCantrip.Name}Description", text);
        }
    }

    internal static void SwitchHastedCasing()
    {
        var restrictedActions = FeatureDefinitionAdditionalActions.AdditionalActionHasted.RestrictedActions;
        if (Main.Settings.AllowHasteCasting)
        {
            restrictedActions.TryAdd(Id.CastMain);
        }
        else
        {
            restrictedActions.RemoveAll(id => id == Id.CastMain);
        }
    }

    internal static void SwitchAddBleedingToLesserRestoration()
    {
        var cf = LesserRestoration.EffectDescription.GetFirstFormOfType(EffectForm.EffectFormType.Condition);

        if (cf != null)
        {
            if (Main.Settings.AddBleedingToLesserRestoration)
            {
                cf.ConditionForm.ConditionsList.TryAdd(ConditionBleeding);
            }
            else
            {
                cf.ConditionForm.ConditionsList.Remove(ConditionBleeding);
            }
        }

        var cfg = GreaterRestoration.EffectDescription.GetFirstFormOfType(EffectForm.EffectFormType.Condition);

        if (cfg == null)
        {
            return;
        }

        // NOTE: using the same setting as for Lesser Restoration for compatibility
        if (Main.Settings.AddBleedingToLesserRestoration)
        {
            cfg.ConditionForm.ConditionsList.TryAdd(ConditionBleeding);
        }
        else
        {
            cfg.ConditionForm.ConditionsList.Remove(ConditionBleeding);
        }
    }

    /// <summary>
    ///     Allow conjurations to fully controlled party members instead of AI controlled.
    /// </summary>
    private static void BuildConjureElementalInvisibleStalker()
    {
        ConjureElementalInvisibleStalker = SpellDefinitionBuilder
            .Create(ConjureElementalFire, InvisibleStalkerSubspellName)
            .SetOrUpdateGuiPresentation("Spell/&ConjureElementalInvisibleStalkerTitle",
                "Spell/&ConjureElementalDescription")
            .AddToDB();

        var summonForm = ConjureElementalInvisibleStalker
            .EffectDescription.GetFirstFormOfType(EffectForm.EffectFormType.Summon)?.SummonForm;

        if (summonForm != null)
        {
            summonForm.monsterDefinitionName = InvisibleStalker.Name;
        }
    }


    internal static void SwitchEnableUpcastConjureElementalAndFey()
    {
        if (!Main.Settings.EnableUpcastConjureElementalAndFey)
        {
            ConjureElemental.SubspellsList.Remove(ConjureElementalInvisibleStalker);

            return;
        }

        ConfigureAdvancement(ConjureFey);
        ConfigureAdvancement(ConjureElemental);
        ConfigureAdvancement(ConjureMinorElementals);
        ConjureElemental.SubspellsList.Add(ConjureElementalInvisibleStalker);

        return;

        // Set advancement at spell level, not sub-spell
        static void ConfigureAdvancement([NotNull] IMagicEffect spell)
        {
            var advancement = spell.EffectDescription.EffectAdvancement;

            advancement.effectIncrementMethod = EffectIncrementMethod.PerAdditionalSlotLevel;
            advancement.additionalSpellLevelPerIncrement = 1;
        }
    }

    internal sealed class SpellListContext
    {
        internal SpellListContext(SpellListDefinition spellListDefinition)
        {
            SpellList = spellListDefinition;
            AllSpells = [];
            DisplayedSpells = [];
            DisplayedSuggestedSpells = [];
            DisplayedNonSuggestedSpells = [];
            DisplayedTabletopSpells = [];
            DisplayedNonTabletopSpells = [];
            MinimumSpells = [];
            SuggestedSpells = [];
            TabletopSpells = [];
            SpellList2024ExcludedSpells = [];
            SpellList2024States = [];
        }

        private List<string> SelectedSpells => Main.Settings.SpellListSpellEnabled[SpellList.Name];
        private SpellListDefinition SpellList { get; }
        private HashSet<SpellDefinition> AllSpells { get; }
        internal HashSet<SpellDefinition> DisplayedSpells { get; }
        private HashSet<SpellDefinition> DisplayedSuggestedSpells { get; }
        private HashSet<SpellDefinition> DisplayedNonSuggestedSpells { get; }
        private HashSet<SpellDefinition> DisplayedTabletopSpells { get; }
        private HashSet<SpellDefinition> DisplayedNonTabletopSpells { get; }
        internal HashSet<SpellDefinition> MinimumSpells { get; }
        internal HashSet<SpellDefinition> SuggestedSpells { get; }
        private HashSet<SpellDefinition> TabletopSpells { get; }
        private HashSet<SpellDefinition> SpellList2024ExcludedSpells { get; }
        private Dictionary<SpellDefinition, SpellList2024State> SpellList2024States { get; }


        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool IsAllSetSelected =>
            DisplayedSpells
                .Where(IsSpellToggleEnabled)
                .All(x => SelectedSpells.Contains(x.Name));

        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool IsSuggestedSetSelected =>
            DisplayedSuggestedSpells
                .Where(IsSpellToggleEnabled)
                .All(x => SelectedSpells.Contains(x.Name)) &&
            DisplayedNonSuggestedSpells
                .Where(IsSpellToggleEnabled)
                .All(x => !SelectedSpells.Contains(x.Name));

        // ReSharper disable once MemberHidesStaticFromOuterClass
        internal bool IsTabletopSetSelected =>
            DisplayedTabletopSpells
                .Where(IsSpellToggleEnabled)
                .All(x => SelectedSpells.Contains(x.Name)) &&
            DisplayedNonTabletopSpells
                .Where(IsSpellToggleEnabled)
                .All(x => !SelectedSpells.Contains(x.Name));

        internal bool IsSpellToggleEnabled(SpellDefinition spellDefinition)
        {
            return !IsRequiredSpellList2024Spell(spellDefinition) &&
                   !IsSpellList2024RestrictedSpell(spellDefinition);
        }

        internal bool? GetSpellToggleValueOverride(SpellDefinition spellDefinition)
        {
            if (IsRequiredSpellList2024Spell(spellDefinition))
            {
                return true;
            }

            return IsSpellList2024RestrictedSpell(spellDefinition) ? false : null;
        }

        internal void CalculateAllSpells()
        {
            AllSpells.Clear();
            TabletopSpells.Clear();

            var minSpellLevel = SpellList.HasCantrips ? 0 : 1;
            var maxSpellLevel = SpellList.MaxSpellLevel;

            foreach (var spell in Spells.Where(x =>
                         x.SpellLevel >= minSpellLevel &&
                         x.SpellLevel <= maxSpellLevel &&
                         (!MinimumSpells.Contains(x) || IsRequiredSpellList2024Spell(x))))
            {
                AllSpells.Add(spell);

                if (ModUi.TabletopDefinitionNames.Contains(spell.Name) &&
                    SuggestedSpells.Contains(spell))
                {
                    TabletopSpells.Add(spell);
                }
            }
        }

        internal void CalculateDisplayedSpellsInternal()
        {
            DisplayedSpells.Clear();
            DisplayedSuggestedSpells.Clear();
            DisplayedNonSuggestedSpells.Clear();
            DisplayedTabletopSpells.Clear();
            DisplayedNonTabletopSpells.Clear();

            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var spell in AllSpells)
            {
                if ((SpellsAvailableWithSpellLists2024Only.Contains(spell) &&
                     !Main.Settings.EnableSpellLists2024) ||
                    SpellList2024ExcludedSpells.Contains(spell))
                {
                    continue;
                }

                if (SpellsDisplay.SpellLevelFilter != -1 &&
                    spell.SpellLevel != SpellsDisplay.SpellLevelFilter)
                {
                    continue;
                }

                if (!Main.Settings.AllowDisplayingOfficialSpells &&
                    spell.ContentPack != CeContentPackContext.CeContentPack &&
                    !IsRequiredSpellList2024Spell(spell))
                {
                    continue;
                }

                if (!Main.Settings.AllowDisplayingNonSuggestedSpells &&
                    spell.ContentPack == CeContentPackContext.CeContentPack &&
                    !SuggestedSpells.Contains(spell))
                {
                    continue;
                }

                DisplayedSpells.Add(spell);

                if (SuggestedSpells.Contains(spell))
                {
                    DisplayedSuggestedSpells.Add(spell);
                }
                else
                {
                    DisplayedNonSuggestedSpells.Add(spell);
                }

                if (TabletopSpells.Contains(spell))
                {
                    DisplayedTabletopSpells.Add(spell);
                }
                else
                {
                    DisplayedNonTabletopSpells.Add(spell);
                }
            }
        }

        internal void SelectAllSetInternal(bool toggle)
        {
            foreach (var spell in DisplayedSpells.Where(IsSpellToggleEnabled))
            {
                Switch(spell, toggle);
            }
        }

        internal void SelectSuggestedSetInternal(bool toggle)
        {
            if (toggle)
            {
                SelectAllSetInternal(false);
            }

            foreach (var spell in DisplayedSpells.Intersect(SuggestedSpells).Where(IsSpellToggleEnabled))
            {
                Switch(spell, toggle);
            }
        }

        internal void SelectTabletopSetInternal(bool toggle)
        {
            if (toggle)
            {
                SelectAllSetInternal(false);
            }

            foreach (var spell in DisplayedSpells.Intersect(TabletopSpells).Where(IsSpellToggleEnabled))
            {
                Switch(spell, toggle);
            }
        }

        internal void Switch([NotNull] SpellDefinition spellDefinition, bool active)
        {
            if (!SpellList.HasCantrips && spellDefinition.SpellLevel == 0)
            {
                return;
            }

            // A 2024 exclusion belongs to this class list only. Keep the stored user toggle untouched so
            // disabling the 2024 list can restore the exact pre-switch state.
            if (SpellList2024ExcludedSpells.Contains(spellDefinition))
            {
                return;
            }

            if (IsRequiredSpellList2024Spell(spellDefinition))
            {
                return;
            }

            if (IsSpellList2024RestrictedSpell(spellDefinition))
            {
                return;
            }

            if (active &&
                SpellsAvailableWithSpellLists2024Only.Contains(spellDefinition) &&
                !Main.Settings.EnableSpellLists2024)
            {
                return;
            }

            SetSpellPresence(spellDefinition, active, true);
        }

        private bool IsRequiredSpellList2024Spell(SpellDefinition spellDefinition)
        {
            return MinimumSpells.Contains(spellDefinition) &&
                   SpellList2024States.TryGetValue(spellDefinition, out var state) &&
                   !state.Minimum;
        }

        internal void ApplySpellList2024Change(
            SpellDefinition spellDefinition,
            bool included,
            bool enabled)
        {
            if (!enabled)
            {
                if (!SpellList2024States.TryGetValue(spellDefinition, out var state))
                {
                    SpellList2024ExcludedSpells.Remove(spellDefinition);

                    return;
                }

                SpellList2024States.Remove(spellDefinition);
                var wasExcluded = SpellList2024ExcludedSpells.Remove(spellDefinition);

                SetCollectionMembership(MinimumSpells, spellDefinition, state.Minimum);
                SetCollectionMembership(SuggestedSpells, spellDefinition, state.Suggested);

                if (spellDefinition.ContentPack != CeContentPackContext.CeContentPack || wasExcluded)
                {
                    SetSpellPresence(spellDefinition, state.Active, false);
                }

                return;
            }

            if (!SpellList2024States.ContainsKey(spellDefinition))
            {
                SpellList2024States.Add(
                    spellDefinition,
                    new SpellList2024State(
                        MinimumSpells.Contains(spellDefinition),
                        SuggestedSpells.Contains(spellDefinition),
                        SpellList.ContainsSpell(spellDefinition)));
            }

            SetCollectionMembership(SpellList2024ExcludedSpells, spellDefinition, !included);

            if (spellDefinition.ContentPack == CeContentPackContext.CeContentPack)
            {
                SetCollectionMembership(SuggestedSpells, spellDefinition, included);

                if (!included)
                {
                    SetSpellPresence(spellDefinition, false, false);
                }
            }
            else
            {
                SetCollectionMembership(MinimumSpells, spellDefinition, included);
                SetSpellPresence(spellDefinition, included, false);
            }
        }

        internal void SetConditionalSpellAvailability(SpellDefinition spellDefinition, bool enabled)
        {
            if (!SpellList.HasCantrips && spellDefinition.SpellLevel == 0)
            {
                return;
            }

            SetSpellPresence(
                spellDefinition,
                enabled &&
                !IsSpellList2024RestrictedSpell(spellDefinition) &&
                SelectedSpells.Contains(spellDefinition.Name),
                false);
        }

        internal void ApplySpellList2024Restrictions(bool enabled)
        {
            foreach (var spellDefinition in AllSpells.Where(IsUbOriginalSpell))
            {
                var active = !enabled && SelectedSpells.Contains(spellDefinition.Name);

                if (SpellList.ContainsSpell(spellDefinition) != active)
                {
                    SetSpellPresence(spellDefinition, active, false);
                }
            }
        }

        private void SetSpellPresence(
            SpellDefinition spellDefinition,
            bool active,
            bool updateSettings)
        {
            var spellListName = SpellList.Name;
            var spellName = spellDefinition.Name;

            if (active)
            {
                SpellList.AddSpell(spellDefinition);

                // sync shock arcanist list with wizard
                if (SpellList == SpellListWizard && spellDefinition.SchoolOfMagic == SchoolEvocation)
                {
                    SpellListShockArcanist.AddSpell(spellDefinition);
                }

                if (updateSettings)
                {
                    Main.Settings.SpellListSpellEnabled[spellListName].TryAdd(spellName);
                }
            }
            else
            {
                SpellList.RemoveSpell(spellDefinition);

                // sync shock arcanist list with wizard
                if (SpellList == SpellListWizard && spellDefinition.SchoolOfMagic == SchoolEvocation)
                {
                    SpellListShockArcanist.RemoveSpell(spellDefinition);
                }

                if (updateSettings)
                {
                    Main.Settings.SpellListSpellEnabled[spellListName].Remove(spellName);
                }
            }

            if (SpellList == InventorClass.SpellList)
            {
                InventorClass.SwitchSpellStoringItemSubPower(spellDefinition, active);
            }

            RefreshAuxiliarySpellLists(spellDefinition);

            if (updateSettings && spellDefinition.SpellLevel <= 1)
            {
                Tabletop2024Context.RefreshFeatSpellSelectionLists2024();
            }
        }

        private static void SetCollectionMembership(
            HashSet<SpellDefinition> collection,
            SpellDefinition spellDefinition,
            bool active)
        {
            if (active)
            {
                collection.Add(spellDefinition);
            }
            else
            {
                collection.Remove(spellDefinition);
            }
        }

        private readonly struct SpellList2024State(
            bool minimum,
            bool suggested,
            bool active)
        {
            internal bool Minimum { get; } = minimum;
            internal bool Suggested { get; } = suggested;
            internal bool Active { get; } = active;
        }
    }

    private sealed class FilterTargetingCharacterChainLightning : IFilterTargetingCharacter
    {
        public bool EnforceFullSelection => false;

        public bool IsValid(CursorLocationSelectTarget __instance, GameLocationCharacter target)
        {
            var selectedTargets = __instance.SelectionService.SelectedTargets;

            if (selectedTargets.Count == 0)
            {
                return true;
            }

            var firstTarget = selectedTargets[0];

            var isValid = firstTarget.IsWithinRange(target, 6);

            if (!isValid)
            {
                __instance.actionModifier.FailureFlags.Add("Failure/&SecondTargetNotWithinRange");
            }

            return isValid;
        }
    }
}
