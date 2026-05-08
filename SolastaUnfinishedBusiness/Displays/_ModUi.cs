using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityModManagerNet;
using static SolastaUnfinishedBusiness.Displays.BackgroundsAndRacesDisplay;
using static SolastaUnfinishedBusiness.Displays.BlueprintDisplay;
using static SolastaUnfinishedBusiness.Displays.ClassesDisplay;
using static SolastaUnfinishedBusiness.Displays.CreditsDisplay;
using static SolastaUnfinishedBusiness.Displays.DungeonMakerDisplay;
using static SolastaUnfinishedBusiness.Displays.EffectsDisplay;
using static SolastaUnfinishedBusiness.Displays.EncountersDisplay;
using static SolastaUnfinishedBusiness.Displays.GameServicesDisplay;
using static SolastaUnfinishedBusiness.Displays.CampaignsDisplay;
using static SolastaUnfinishedBusiness.Displays.CraftingAndItems;
using static SolastaUnfinishedBusiness.Displays.ProficienciesDisplay;
using static SolastaUnfinishedBusiness.Displays.RulesDisplay;
using static SolastaUnfinishedBusiness.Displays.SpellsDisplay;
using static SolastaUnfinishedBusiness.Displays.SubclassesDisplay;
using static SolastaUnfinishedBusiness.Displays.ToolsDisplay;

namespace SolastaUnfinishedBusiness.Displays;

internal static class ModUi
{
    internal const int DontDisplayDescription = 4;
    internal const float PixelsPerColumn = 220;

    internal static readonly HashSet<string> TabletopDefinitionNames =
    [
        "AbiDalzimHorridWilting",
        "AganazzarScorcher",
        "AshardalonStride",
        "AuraOfLife",
        "AuraOfPerseverance",
        "AuraOfVitality",
        "BanishingSmite",
        "BindingIce",
        "BladeWard",
        "BlessedWarrior",
        "BlindFighting",
        "BlindingSmite",
        "BoomingBlade",
        "BoomingStep",
        "BorrowedKnowledge",
        "BurstOfRadiance",
        "CircleLand",
        "ChromaticOrb",
        "CircleOfMagicalNegation",
        "CircleOfTheCosmos",
        "CircleOfTheNight",
        "CircleOfTheWildfire",
        "ChaosBolt",
        "CloudOfDaggers",
        "CollegeOfAudacity",
        "CollegeOfGuts",
        "CollegeOfLore",
        "CollegeOfValiance",
        "CommandSpell",
        "CreateBonfire",
        "CrownOfStars",
        "CrusadersMantle",
        "Dawn",
        "DissonantWhispers",
        "DivineWrath",
        "DomainLife",
        "DomainNature",
        "DomainOrder",
        "DomainTempest",
        "DomainSmith",
        "DraconicTransformation",
        "DruidicWarrior",
        "DragonsBreathSpell",
        "EarthTremor",
        "ElementalBane",
        "ElementalInfusion",
        "ElementalWeapon",
        "EmpoweredKnowledge",
        "EnduringSting",
        "EnsnaringStrike",
        "FaithfulHound",
        "FarStep",
        "FeatAcrobat",
        "FeatAlert",
        "FeatArcanist",
        "FeatBladeMastery",
        "FeatBlindFighting",
        "FeatBountifulLuck",
        "FeatCharger",
        "FeatCleavingAttack",
        "FeatGreatWeaponMaster2024",
        "FeatCrossbowExpert2024",
        "FeatDarkElfMagic",
        "FeatDeadeye",
        "FeatSharpshooter2024",
        "FeatDefensiveDuelist",
        "FeatDefensiveDuelist2024",
        "FeatDragonWings",
        "FeatDualWeaponDefense",
        "FeatDungeonDelver",
        "FeatDwarvenFortitude",
        "FeatEldritchAdept",
        "FeatFellHanded",
        "FeatGiftOfTheChromaticDragon",
        "FeatGroupGrappler2024",
        "FeatGroupGiftOfTheGemDragon",
        "FeatGroupAthlete",
        "FeatGroupBalefulScion",
        "FeatGroupChef",
        "FeatGroupCrusher",
        "FeatGroupDragonFear",
        "FeatGroupDragonHide",
        "FeatGroupElementalAdept2024",
        "FeatGroupElementalAdept",
        "FeatGroupElvenAccuracy",
        "FeatGroupFadeAway",
        "FeatGroupFightingStyle",
        "FeatGroupCharger2024",
        "FeatGroupDualWielder2024",
        "FeatGroupFlamesOfPhlegethos",
        "FeatGroupFeyTouched2024",
        "FeatFeyTouched2024Intelligence",
        "FeatFeyTouched2024Wisdom",
        "FeatFeyTouched2024Charisma",
        "FeatGroupFeyTeleport2024",
        "FeatFeyTeleportation2024Int",
        "FeatFeyTeleportation2024Cha",
        "FeatGroupGrudgeBearer",
        "FeatGroupHeavyArmorMaster2024",
        "FeatHeavyArmorMaster2024",
        "FeatHeavyArmorMaster2024Con",
        "FeatGroupInspiringLeader2024",
        "FeatGroupLightlyArmored2024",
        "FeatGroupMagicInitiate",
        "FeatGroupMageSlayer2024",
        "FeatGroupMediumArmor",
        "FeatGroupMediumArmorMaster2024",
        "FeatGroupModeratelyArmored2024",
        "FeatGroupOrcishAggression",
        "FeatGroupOrcishFury",
        "FeatGroupPiercer",
        "FeatGroupPoisoner2024",
        "FeatGroupPolearmMaster2024",
        "FeatGroupResilient2024",
        "FeatGroupRevenantGreatSword",
        "FeatGroupSecondChance",
        "FeatGroupShadowTouched",
        "FeatGroupShadowTouched2024",
        "FeatGroupSentinel2024",
        "FeatGroupSlasher",
        "FeatGroupSpeedy",
        "FeatGroupSpellSniper",
        "FeatGroupSpellSniper2024",
        "FeatGroupWarCaster2024",
        "FeatGroupSquatNimbleness",
        "FeatGroupTelekinetic",
        "FeatGroupFeyTeleport",
        "FeatGroupWeaponMaster",
        "FeatGroupWeaponMastery",
        "FeatHealer",
        "FeatHeavyArmorMaster",
        "FeatGroupHeavilyArmored2024",
        "FeatInfernalConstitution",
        "FeatInspiringLeader",
        "FeatLucky",
        "FeatMageSlayer",
        "FeatMediumArmorMaster",
        "FeatMenacing",
        "FeatMetamagicAdept",
        "FeatMobile",
        "FeatPoisoner",
        "FeatPolearmExpert",
        "FeatRangedExpert",
        "FeatRemarkableTechnique",
        "FeatSavageAttack",
        "FeatSentinel",
        "FeatShieldMaster2024",
        "FeatShieldTechniques",
        "FeatSkilled",
        "FeatSpearMastery",
        "FeatStealthy",
        "FeatTacticianAdept",
        "FeatTough",
        "FeatTheologian",
        "FeatWarCaster",
        "FeatWoodElfMagic",
        "FindFamiliar",
        "FizbanPlatinumShield",
        "FlameArrows",
        "Foresight",
        "ForestGuardian",
        "Glibness",
        "GiftOfAlacrity",
        "GravityFissure",
        "GravitySinkhole",
        "HeroicInfusion",
        "HolyWeapon",
        "HungerOfTheVoid",
        "IceBlade",
        "Incineration",
        "Infestation",
        "InnovationArmor",
        "InnovationArtillerist",
        "InnovationWeapon",
        "IntellectFortress",
        "Interception",
        "InvocationAbilitiesOfTheChainMaster",
        "InvocationAspectOfTheMoon",
        "InvocationBondOfTheTalisman",
        "InvocationBurningHex",
        "InvocationChillingHex",
        "InvocationDevouringBlade",
        "InvocationEldritchMind",
        "InvocationEldritchSmite",
        "InvocationGiftOfTheEverLivingOnes",
        "InvocationGiftOfTheProtectors",
        "InvocationGraspingBlast",
        "InvocationHinderingBlast",
        "InvocationImprovedPactWeapon",
        "InvocationInexorableHex",
        "InvocationPerniciousCloak",
        "InvocationShroudOfShadow",
        "InvocationSuperiorPactWeapon",
        "InvocationTombOfFrost",
        "InvocationTrickstersEscape",
        "InvocationUltimatePactWeapon",
        "InvocationUndyingServitude",
        "InvocationVexingHex",
        "Invulnerability",
        "KineticJaunt",
        "LightningArrow",
        "LightningLure",
        "MaddeningDarkness",
        "MagicStone",
        "MagnifyGravity",
        "MartialChampion",
        "MartialArcaneArcher",
        "MartialForceKnight",
        "MartialRoyalKnight",
        "MartialSpellShield",
        "MartialTactician",
        "MassHeal",
        "MetamagicSeekingSpell",
        "MetamagicTransmutedSpell",
        "MeteorSwarmSingleTarget",
        "MindBlank",
        "MindSpike",
        "MirrorImage",
        "MysticalCloak",
        "OathOfAncients",
        "OathOfDevotion",
        "PathBerserker",
        "PathOfTheBattlerager",
        "PathOfTheBeast",
        "PathOfTheRavager",
        "PathOfTheSpirits",
        "PathOfTheWildMagic",
        "PatronArchfey",
        "PatronCelestial",
        "PatronFiend",
        "PatronSoulBlade",
        "PowerWordHeal",
        "PowerWordKill",
        "PrimalSavagery",
        "PsychicLance",
        "PsychicScream",
        "PsychicWhip",
        "PulseWave",
        "RaceBattleborn",
        "RaceBolgrif",
        "RaceFairy",
        "RaceKobold",
        "RaceMalakh",
        "RaceOligath",
        "RaceDarkelf",
        // "RaceHalfElfVariant",
        "RaceHalfElfDark",
        "RaceHalfElfHigh",
        "RaceHalfElfSylvan",
        // "RaceTiefling",
        "RaceLizardfolk",
        "RaceShadarKai",
        "RaceTieflingDevilTongue",
        "RaceTieflingFeral",
        "RaceTieflingMephistopheles",
        "RaceTieflingZariel",
        "RangerFeyWanderer",
        "RangerGloomStalker",
        "RangerHunter",
        "RangerWildMaster",
        "RayOfSickness",
        "RemarkableTechnique",
        "ResonatingStrike",
        "ReverseGravity",
        "RoguishSlayer",
        "RoguishSwashbuckler",
        "RoguishThief",
        "Sanctuary",
        "Scatter",
        "SearingSmite",
        "ShadowBlade",
        "ShadowOfMoil",
        "Shapechange",
        "SickeningRadiance",
        "SkinOfRetribution",
        "SorcerousDivineHeart",
        "SorcerousWildMagic",
        "SnillocSnowballStorm",
        "SpellWeb",
        "SpikeBarrage",
        "SorcerousDraconicBloodline",
        "SpiritShroud",
        "StaggeringSmite",
        "StarryWisp",
        "SteelWhirlwind",
        "StrikeWithTheWind",
        "SwiftQuiver",
        "SwordStorm",
        "SynapticStatic",
        "Telekinesis",
        "ThornyVines",
        "ThunderousSmite",
        "ThunderStrike",
        "TimeStop",
        "TollTheDead",
        "TraditionOpenHand",
        "VileBrew",
        "VitalityTransfer",
        "VitriolicSphere",
        "VoidGrasp",
        "WardingBond",
        "WayOfTheShadow",
        "Weird",
        "Wendigo",
        "Wildling",
        "WitchBolt",
        "WitherAndBloom",
        "WizardAbjuration",
        "WizardBladeDancer",
        "WizardEvocation",
        "WizardGraviturgist",
        "WizardWarMagic",
        "WrathfulSmite"
    ];

    internal static readonly HashSet<BaseDefinition> TabletopDefinitions = [];

    internal static void LoadTabletopDefinitions()
    {
        TabletopDefinitions.Clear();

        var raceDb = DatabaseRepository.GetDatabase<CharacterRaceDefinition>();
        var subclassDb = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>();
        var featDb = DatabaseRepository.GetDatabase<FeatDefinition>();
        var fightingStyleDb = DatabaseRepository.GetDatabase<FightingStyleDefinition>();
        var invocationDb = DatabaseRepository.GetDatabase<InvocationDefinition>();
        var metamagicOptionDb = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>();
        var spellDb = DatabaseRepository.GetDatabase<SpellDefinition>();

        foreach (var definitionName in TabletopDefinitionNames)
        {
            if (raceDb.TryGetElement(definitionName, out var race))
            {
                TabletopDefinitions.Add(race);
            }
            else if (subclassDb.TryGetElement(definitionName, out var subclass))
            {
                TabletopDefinitions.Add(subclass);
            }
            else if (featDb.TryGetElement(definitionName, out var feat))
            {
                TabletopDefinitions.Add(feat);
            }
            else if (fightingStyleDb.TryGetElement(definitionName, out var fightingStyle))
            {
                TabletopDefinitions.Add(fightingStyle);
            }
            else if (invocationDb.TryGetElement(definitionName, out var invocation))
            {
                TabletopDefinitions.Add(invocation);
            }
            else if (metamagicOptionDb.TryGetElement(definitionName, out var metamagicOption))
            {
                TabletopDefinitions.Add(metamagicOption);
            }
            else if (spellDb.TryGetElement(definitionName, out var spell))
            {
                TabletopDefinitions.Add(spell);
            }
        }
    }

    private static bool IsTabletopFeatDefinition([CanBeNull] FeatDefinition featDefinition)
    {
        return featDefinition != null && HasTabletopFeatDescendants(featDefinition, []);
    }

    private static bool HasTabletopFeatDescendants(
        [CanBeNull] FeatDefinition featDefinition,
        [NotNull] HashSet<string> processedDefinitions)
    {
        if (featDefinition == null || !processedDefinitions.Add(featDefinition.Name))
        {
            return false;
        }

        if (Tabletop2024Context.IsNonSelectableTabletopGroup(featDefinition))
        {
            return false;
        }

        if (TabletopDefinitions.Contains(featDefinition) || Tabletop2024Context.IsManagedTabletopFeat(featDefinition))
        {
            return true;
        }

        if (!Tabletop2024Context.IsTabletopContainerGroup(featDefinition) ||
            featDefinition.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } group)
        {
            return false;
        }

        return group.GetSubFeats(true).Any(subFeat => HasTabletopFeatDescendants(subFeat, processedDefinitions));
    }

    internal static bool IsTabletopDefinition(BaseDefinition definition)
    {
        return definition switch
        {
            FeatDefinition featDefinition => IsTabletopFeatDefinition(featDefinition),
            not null => TabletopDefinitions.Contains(definition),
            _ => false
        };
    }

    internal static bool IsBulkSelectableTabletopDefinition(BaseDefinition definition)
    {
        return definition switch
        {
            FeatDefinition featDefinition => IsTabletopDefinition(featDefinition) &&
                                            !Tabletop2024Context.IsOptInOnlyManagedTabletopFeat(featDefinition),
            not null => IsTabletopDefinition(definition),
            _ => false
        };
    }

    private static string FormatDefinitionOptionTitle(BaseDefinition definition)
    {
        var title = definition.FormatTitle();

        if (Tabletop2024Context.Is2024TabletopFeat(definition))
        {
            title = $"{title} [2024]";
        }

        return title;
    }

    internal static void DisplaySubMenu(ref int selectedPane, string title = null, params NamedAction[] actions)
    {
        if (!Main.Enabled)
        {
            return;
        }

        if (title != null)
        {
            UI.Div();
            UI.Label(title);
            UI.Space(7f);
        }

        UI.SubMenu(ref selectedPane, title != null, null, actions);
    }

    internal static bool DisplayDefinitions<T>(
        string label,
        Action<T, bool> switchAction,
        [NotNull] HashSet<T> registeredDefinitions,
        [NotNull] List<string> selectedDefinitions,
        ref bool displayToggle,
        ref int sliderPosition,
        bool useAlternateDescription = false,
        [CanBeNull] Action headerRendering = null,
        [CanBeNull] Action additionalRendering = null,
        bool displaySelectTabletop = true,
        [CanBeNull] Func<T, bool> toggleEnabled = null,
        [CanBeNull] Func<T, bool?> toggleValueOverride = null) where T : BaseDefinition
    {
        if (registeredDefinitions.Count == 0)
        {
            return false;
        }

        var enabledDefinitions = registeredDefinitions
            .Where(definition => toggleEnabled?.Invoke(definition) ?? true)
            .ToArray();
        var enabledTabletopDefinitions = enabledDefinitions
            .Where(IsBulkSelectableTabletopDefinition)
            .ToArray();
        var selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
        var selectAll =
            enabledDefinitions.Length > 0 &&
            enabledDefinitions.All(definition => selectedDefinitionNames.Contains(definition.Name));
        var selectTabletop =
            enabledTabletopDefinitions.Length > 0 &&
            enabledTabletopDefinitions.All(definition => selectedDefinitionNames.Contains(definition.Name));

        UI.Label();

        var toggle = displayToggle;

        if (UI.DisclosureToggle($"{label}:", ref toggle, 200))
        {
            displayToggle = toggle;
        }

        if (!displayToggle)
        {
            return selectTabletop;
        }

        UI.Label();

        headerRendering?.Invoke();

        using (UI.HorizontalScope())
        {
            toggle = sliderPosition == 1;

            if (UI.Toggle(Gui.Localize("ModUi/&ShowDescriptions"), ref toggle, UI.Width(PixelsPerColumn)))
            {
                sliderPosition = toggle ? 1 : 4;
            }

            if (additionalRendering != null)
            {
                additionalRendering.Invoke();
            }
            else
            {
                var guiEnabled = GUI.enabled;
                GUI.enabled = guiEnabled && enabledDefinitions.Length > 0;

                if (UI.Toggle(Gui.Localize("ModUi/&SelectAll"), ref selectAll, UI.Width(PixelsPerColumn)))
                {
                    foreach (var registeredDefinition in enabledDefinitions)
                    {
                        switchAction.Invoke(registeredDefinition, selectAll);
                    }

                    selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
                }

                if (displaySelectTabletop)
                {
                    if (UI.Toggle(Gui.Localize("ModUi/&SelectTabletop"), ref selectTabletop,
                            UI.Width(PixelsPerColumn)))
                    {
                        foreach (var registeredDefinition in enabledTabletopDefinitions)
                        {
                            switchAction.Invoke(registeredDefinition, selectTabletop);
                        }

                        selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
                    }
                }

                GUI.enabled = guiEnabled;
            }
        }

        // UI.Slider("slide left for description / right to collapse".white().bold().italic(), ref sliderPosition, 1, maxColumns, 1, "");

        UI.Label();

        var flip = false;
        var current = 0;
        var definitions = registeredDefinitions.ToArray();
        var count = definitions.Length;

        using (UI.VerticalScope())
        {
            while (current < count)
            {
                var columns = sliderPosition;

                using (UI.HorizontalScope())
                {
                    while (current < count && columns-- > 0)
                    {
                        var definition = definitions[current];
                        var title = FormatDefinitionOptionTitle(definition);
                        var isTabletop = IsTabletopDefinition(definition);
                        var isVanilla = definition.ContentPack != CeContentPackContext.CeContentPack;

                        if (flip)
                        {
                            title = title.Khaki();
                        }
                        else if (sliderPosition == 1)
                        {
                            title = title.White();
                        }
                        else if (isTabletop)
                        {
                            title = title.Color("#D89555").Bold() + " \u00a9".Grey(); // copyright symbol
                        }
                        else if (isVanilla)
                        {
                            title = title.Orange() + " \u263c".Grey(); // sun symbol
                        }

                        var isEnabled = toggleEnabled?.Invoke(definition) ?? true;
                        toggle = toggleValueOverride?.Invoke(definition) ?? selectedDefinitionNames.Contains(definition.Name);

                        var guiEnabled = GUI.enabled;
                        GUI.enabled = guiEnabled && isEnabled;

                        if (UI.Toggle(title, ref toggle, UI.Width(PixelsPerColumn)))
                        {
                            switchAction.Invoke(definition, toggle);
                            selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
                        }

                        GUI.enabled = guiEnabled;

                        if (sliderPosition == 1)
                        {
                            var description = useAlternateDescription
                                ? Gui.Localize($"ModUi/&{definition.Name}Description")
                                : definition.FormatDescription();

                            description = flip ? description.Khaki() : description.White();

                            UI.Label(description, UI.Width(PixelsPerColumn * 3));

                            flip = !flip;
                        }

                        current++;
                    }
                }
            }
        }

        return selectTabletop;
    }
}

[UsedImplicitly]
internal sealed class GameplayViewer : IMenuSelectablePage
{
    private int _gamePlaySelectedPane;
    public string Name => Gui.Localize("ModUi/&Gameplay");

    public int Priority => 100;

    public void OnGUI(UnityModManager.ModEntry modEntry)
    {
        ModUi.DisplaySubMenu(ref _gamePlaySelectedPane, Name,
            new NamedAction(Gui.Localize("ModUi/&GeneralMenu"), DisplayGameplay),
            new NamedAction(Gui.Localize("ModUi/&Rules"), DisplayRules),
            new NamedAction(Gui.Localize("ModUi/&Campaigns"), DisplayGameUi),
            new NamedAction(Gui.Localize("ModUi/&CraftingItems"), DisplayCraftingAndItems),
            new NamedAction(Gui.Localize("ModUi/&DungeonMaker"), DisplayDungeonMaker),
            new NamedAction(Gui.Localize("ModUI/&RoleplaySettings"), RoleplayDisplay.DisplayRoleplaySettings));
    }
}

[UsedImplicitly]
internal sealed class CharacterViewer : IMenuSelectablePage
{
    private int _characterSelectedPane;
    public string Name => Gui.Localize("ModUi/&Character");

    public int Priority => 200;

    public void OnGUI(UnityModManager.ModEntry modEntry)
    {
        ModUi.DisplaySubMenu(ref _characterSelectedPane, Name,
            new NamedAction(Gui.Localize("ModUi/&BackgroundsAndRaces"),
                DisplayBackgroundsAndRaces),
            new NamedAction(Gui.Localize("ModUi/&Classes"),
                DisplayClasses),
            new NamedAction(Gui.Localize("Screen/&FeatureListingProficienciesTitle"),
                DisplayProficiencies),
            new NamedAction(Gui.Localize("ModUi/&SpellsMenu"),
                DisplaySpells),
            new NamedAction(Gui.Localize("ModUi/&Subclasses"),
                DisplaySubclasses));
    }
}

[UsedImplicitly]
internal sealed class EncountersViewer : IMenuSelectablePage
{
    private int _encountersSelectedPane;
    public string Name => Gui.Localize("ModUi/&Encounters");

    public int Priority => 300;

    public void OnGUI(UnityModManager.ModEntry modEntry)
    {
        ModUi.DisplaySubMenu(ref _encountersSelectedPane, Name,
            new NamedAction(Gui.Localize("ModUi/&GeneralMenu"), DisplayEncountersGeneral),
            new NamedAction(Gui.Localize("ModUi/&Bestiary"), DisplayBestiary),
            new NamedAction(Gui.Localize("ModUi/&CharactersPool"), DisplayNpcs));
    }
}

[UsedImplicitly]
internal sealed class CreditsAndDiagnosticsViewer : IMenuSelectablePage
{
    private int _creditsSelectedPane;
    public string Name => Gui.Localize("ModUi/&CreditsAndDiagnostics");

    public int Priority => 999;

    public void OnGUI(UnityModManager.ModEntry modEntry)
    {
        ModUi.DisplaySubMenu(ref _creditsSelectedPane, null,
            new NamedAction(Gui.Localize("ModUi/&Credits"), DisplayCredits),
            new NamedAction(Gui.Localize("ModUi/&Blueprints"), DisplayBlueprints),
            new NamedAction(Gui.Localize("ModUi/&Effects"), DisplayEffects),
            new NamedAction(Gui.Localize("PartyEditor".Localized()), PartyEditor.OnGUI),
            new NamedAction(Gui.Localize("ModUi/&Services"), DisplayGameServices));
    }
}
