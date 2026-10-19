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
        "BackgroundFarmer",
        "BackgroundMilitia",
        "BanishingSmite",
        "BattleFamiliar",
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
        "ThrownWeaponFighting",
        "FeatThrownWeaponFighting",
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
        "FeatGroupGiftOfTheMetallicDragon",
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
        "FeatGroupSkillExpert",
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
        "SilveryBarbs",
        "Simulacrum",
        "SkinOfRetribution",
        "SorcerousDivineHeart",
        "SorcerousWildMagic",
        "SnillocSnowballStorm",
        "SpellWeb",
        "SpikeBarrage",
        "SorcerousBurst",
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
        "Wish",
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

        var backgroundDb = DatabaseRepository.GetDatabase<CharacterBackgroundDefinition>();
        var raceDb = DatabaseRepository.GetDatabase<CharacterRaceDefinition>();
        var subclassDb = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>();
        var featDb = DatabaseRepository.GetDatabase<FeatDefinition>();
        var fightingStyleDb = DatabaseRepository.GetDatabase<FightingStyleDefinition>();
        var invocationDb = DatabaseRepository.GetDatabase<InvocationDefinition>();
        var metamagicOptionDb = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>();
        var spellDb = DatabaseRepository.GetDatabase<SpellDefinition>();

        foreach (var definitionName in TabletopDefinitionNames)
        {
            if (backgroundDb.TryGetElement(definitionName, out var background))
            {
                TabletopDefinitions.Add(background);
            }
            else if (raceDb.TryGetElement(definitionName, out var race))
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
        [NotNull] HashSet<string> processedDefinitions,
        bool bulkSelection = false)
    {
        // Container groups retain legacy references; classify the active replacement's provenance.
        if (bulkSelection && featDefinition != null &&
            Tabletop2024Context.TryResolveModeAwareFeatDefinition(featDefinition.Name, out var currentDefinition))
        {
            featDefinition = currentDefinition;
        }

        if (featDefinition == null || !processedDefinitions.Add(featDefinition.Name))
        {
            return false;
        }

        if (Tabletop2024Context.IsNonSelectableTabletopGroup(featDefinition) ||
            bulkSelection && (Tabletop2024Context.IsOptInOnlyManagedTabletopFeat(featDefinition) ||
                              Tabletop2024Context.IsEpicBoonDefinition(featDefinition) &&
                              !Tabletop2024Context.AreEpicBoonsEffectivelyEnabled()))
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

        return group.GetSubFeats(true)
            .Any(subFeat => HasTabletopFeatDescendants(subFeat, processedDefinitions, bulkSelection));
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
            FeatDefinition featDefinition => HasTabletopFeatDescendants(featDefinition, [], bulkSelection: true),
            not null => IsTabletopDefinition(definition),
            _ => false
        };
    }

    private static bool IsManualOnlyTabletopDefinition(BaseDefinition definition)
    {
        if (definition is not FeatDefinition feat)
        {
            return false;
        }

        if (Tabletop2024Context.TryResolveModeAwareFeatDefinition(feat.Name, out var currentDefinition))
        {
            feat = currentDefinition;
        }

        return Tabletop2024Context.IsOptInOnlyManagedTabletopFeat(feat);
    }

    internal static bool IsSelectionComplete<T>(
        IEnumerable<T> registeredDefinitions,
        IEnumerable<string> selectedDefinitions,
        Func<T, bool> toggleEnabled = null) where T : BaseDefinition
    {
        var enabledDefinitions = registeredDefinitions
            .Where(definition => toggleEnabled?.Invoke(definition) ?? true)
            .ToArray();
        var selectedNames = new HashSet<string>(selectedDefinitions);

        return enabledDefinitions.Length > 0 &&
               enabledDefinitions.All(definition => selectedNames.Contains(definition.Name));
    }

    internal static bool IsTabletopSelectionComplete<T>(
        IEnumerable<T> registeredDefinitions,
        IEnumerable<string> selectedDefinitions,
        Func<T, bool> toggleEnabled = null) where T : BaseDefinition
    {
        var enabledDefinitions = registeredDefinitions
            .Where(definition => toggleEnabled?.Invoke(definition) ?? true)
            .ToArray();
        var selectedNames = new HashSet<string>(selectedDefinitions);
        var tabletopDefinitions = enabledDefinitions.Where(IsBulkSelectableTabletopDefinition).ToArray();

        return tabletopDefinitions.Length > 0 &&
               tabletopDefinitions.All(definition => selectedNames.Contains(definition.Name)) &&
               !enabledDefinitions.Any(definition => IsManualOnlyTabletopDefinition(definition) &&
                                                     selectedNames.Contains(definition.Name));
    }

    internal static void ApplyTabletopSelection<T>(
        IEnumerable<T> registeredDefinitions,
        Action<T, bool> switchAction,
        bool selected,
        Func<T, bool> toggleEnabled = null,
        bool preserveOtherChoices = true) where T : BaseDefinition
    {
        // Snapshot both eligibility and desired values before a switch refreshes the feat catalog.
        // Checkboxes preserve unrelated choices; the general preset replaces the available set.
        // Selecting either set clears saved manual-only choices; individual toggles remain available.
        var changes = registeredDefinitions
            .Where(definition => toggleEnabled?.Invoke(definition) ?? true)
            .Select(definition => (Definition: definition,
                Tabletop: IsBulkSelectableTabletopDefinition(definition),
                ManualOnly: IsManualOnlyTabletopDefinition(definition)))
            .Where(change => !preserveOtherChoices || change.Tabletop || selected && change.ManualOnly)
            .ToArray();

        foreach (var change in changes)
        {
            switchAction(change.Definition, selected && change.Tabletop);
        }
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

    internal static void DisplayDocumentationLinks(params (string TitleKey, string FileName)[] links)
    {
        var style = new GUIStyle(GUI.skin.button) { wordWrap = true };
        var actions = links.Select(link =>
        {
            var title = Gui.Localize(link.TitleKey).Bold().Khaki();
            return new NamedAction(title, () => UI.ActionButton(title,
                () => UpdateContext.OpenDocumentation(link.FileName), style, UI.MinWidth(0f), UI.AutoWidth()));
        }).ToArray();
        UI.HStack(null, actions.Length, style, false, actions);
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
        [CanBeNull] NamedAction[] additionalActions = null,
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
        var selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
        var selectAll = IsSelectionComplete(enabledDefinitions, selectedDefinitionNames);
        var selectTabletop = IsTabletopSelectionComplete(enabledDefinitions, selectedDefinitionNames);

        UI.Label();

        var toggle = displayToggle;

        if (UI.DisclosureToggle($"{label}:", ref toggle))
        {
            displayToggle = toggle;
        }

        if (!displayToggle)
        {
            return selectTabletop;
        }

        UI.Label();

        headerRendering?.Invoke();

        var newSliderPosition = sliderPosition;
        var descriptionsTitle = Gui.Localize("ModUi/&ShowDescriptions");
        NamedAction[] bulkActions =
        [
            new(descriptionsTitle, () =>
            {
                var showDescriptions = newSliderPosition == 1;
                if (UI.Toggle(descriptionsTitle, ref showDescriptions, UI.AutoWidth()))
                {
                    newSliderPosition = showDescriptions ? 1 : 4;
                }
            })
        ];

        if (additionalActions != null)
        {
            bulkActions = bulkActions.Concat(additionalActions).ToArray();
        }
        else
        {
            var selectAllTitle = Gui.Localize("ModUi/&SelectAll");
            bulkActions = bulkActions.Append(new NamedAction(selectAllTitle, () =>
            {
                if (UI.Toggle(selectAllTitle, ref selectAll, UI.AutoWidth()))
                {
                    foreach (var registeredDefinition in enabledDefinitions)
                    {
                        switchAction.Invoke(registeredDefinition, selectAll);
                    }

                    selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
                }
            }, () => enabledDefinitions.Length > 0)).ToArray();

            if (displaySelectTabletop)
            {
                var selectTabletopTitle = Gui.Localize("ModUi/&SelectTabletop");
                bulkActions = bulkActions.Append(new NamedAction(selectTabletopTitle, () =>
                {
                    if (UI.Toggle(selectTabletopTitle, ref selectTabletop, UI.AutoWidth()))
                    {
                        ApplyTabletopSelection(enabledDefinitions, switchAction, selectTabletop);

                        selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
                    }
                }, () => enabledDefinitions.Length > 0)).ToArray();
            }
        }

        UI.HStack(null, bulkActions.Length, GUI.skin.label,
            bulkActions.Select(action => UI.ToggleWidth(action.Name)).ToArray(), false, bulkActions);
        sliderPosition = newSliderPosition;

        UI.Label();

        var definitions = registeredDefinitions.ToArray();
        var showDefinitionDescriptions = sliderPosition == 1;
        var maximumCaptionWidth = UnityModManager.UI.Scale(300);
        var definitionTitles = definitions.Select((definition, index) =>
        {
            var title = FormatDefinitionOptionTitle(definition);
            var alternateColor = showDefinitionDescriptions && index % 2 == 1;
            if (alternateColor)
            {
                title = title.Khaki();
            }
            else if (showDefinitionDescriptions)
            {
                title = title.White();
            }
            else if (IsTabletopDefinition(definition))
            {
                title = title.Color("#D89555").Bold() + " \u00a9".Grey();
            }
            else if (definition.ContentPack != CeContentPackContext.CeContentPack)
            {
                title = title.Orange() + " \u263c".Grey();
            }

            return title;
        }).ToArray();
        var captionWidths = definitionTitles.Select(title =>
            Mathf.Max(PixelsPerColumn, Mathf.Min(UI.ToggleWidth(title), maximumCaptionWidth))).ToArray();

        void DrawDefinition(int index)
        {
            var definition = definitions[index];
            var guiEnabled = GUI.enabled;
            try
            {
                GUI.enabled = guiEnabled && (toggleEnabled?.Invoke(definition) ?? true);
                var value = toggleValueOverride?.Invoke(definition) ?? selectedDefinitionNames.Contains(definition.Name);
                if (UI.Toggle(definitionTitles[index], ref value, UI.MinWidth(0f), UI.ExpandWidth(true)))
                {
                    switchAction.Invoke(definition, value);
                    selectedDefinitionNames = new HashSet<string>(selectedDefinitions);
                }
            }
            finally
            {
                GUI.enabled = guiEnabled;
            }
        }

        if (!showDefinitionDescriptions)
        {
            UI.AlignedGrid(sliderPosition, captionWidths.DefaultIfEmpty(PixelsPerColumn).Max(), definitions.Length,
                DrawDefinition);
        }
        else
        {
            var descriptionStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            for (var index = 0; index < definitions.Length; index++)
            {
                var definition = definitions[index];
                var description = useAlternateDescription
                    ? Gui.Localize($"ModUi/&{definition.Name}Description")
                    : definition.FormatDescription();
                description = index % 2 == 1 ? description.Khaki() : description.White();
                var titleWidth = captionWidths[index];
                var definitionIndex = index;
                UI.AlignedRow(1, [titleWidth, PixelsPerColumn * 3],
                    [Mathf.Min(titleWidth, UnityModManager.UI.Scale(150)), UnityModManager.UI.Scale(200)],
                    new NamedAction(definitionTitles[index], () => DrawDefinition(definitionIndex)),
                    new NamedAction(description, () => UI.Label(description, descriptionStyle,
                        UI.MinWidth(0f), UI.ExpandWidth(true))));
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
        var toggle = Main.Settings.EnableStrictTabletopClassSelection;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableStrictTabletopClassSelection"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableStrictTabletopClassSelection = toggle;
            SubclassesContext.RefreshSubclassVisibility();
        }

        if (Main.Settings.EnableStrictTabletopClassSelection)
        {
            UI.Label(Gui.Localize("ModUi/&EnableStrictTabletopClassSelectionHelp"),
                new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.ExpandWidth(true));
        }

        UI.Label();
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
