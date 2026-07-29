using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Races;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSenses;

namespace SolastaUnfinishedBusiness.Models;

internal static class RacesContext
{
    private const int SpeciesBaseWalkSpeed2024 = 6;

    private static readonly FeatureDefinitionAbilityCheckAffinity AbilityCheckAffinityDarknessPerceptive =
        FeatureDefinitionAbilityCheckAffinityBuilder
            .Create("AbilityCheckAffinityDarknessPerceptive")
            .SetGuiPresentation(Category.Feature)
            .BuildAndSetAffinityGroups(CharacterAbilityCheckAffinity.Advantage,
                abilityProficiencyPairs: (AttributeDefinitions.Wisdom, SkillDefinitions.Perception))
            .AddCustomSubFeatures(ValidatorsCharacter.IsUnlitOrDarkness)
            .AddToDB();

    internal static Dictionary<CharacterRaceDefinition, float> RaceScaleMap { get; } = [];
    internal static HashSet<CharacterRaceDefinition> Races { get; private set; } = [];
    internal static HashSet<CharacterRaceDefinition> Subraces { get; private set; } = [];

    internal static void Load()
    {
        Morphotypes.Load();

        LoadRace(RaceBattlebornBuilder.RaceBattleborn);
        LoadRace(RaceBolgrifBuilder.RaceBolgrif);
        LoadRace(RaceFairyBuilder.RaceFairy);
        LoadRace(RaceImpBuilder.RaceImp);
        LoadRace(RaceKoboldBuilder.RaceKobold);
        LoadRace(RaceMalakhBuilder.RaceMalakh);
        LoadRace(RaceOligathBuilder.RaceOligath);
        LoadRace(RaceWendigoBuilder.RaceWendigo);
        LoadRace(RaceWildlingBuilder.RaceWildling);
        LoadRace(RaceWyrmkinBuilder.RaceWyrmkin);
        LoadRace(RaceLizardfolkBuilder.RaceLizardfolk);
        LoadRace(RaceOniBuilder.RaceOni);

        _ = RaceTieflingBuilder.RaceTiefling;

        LoadSubrace(RaceTieflingBuilder.RaceTieflingDevilTongue);
        LoadSubrace(RaceTieflingBuilder.RaceTieflingFeral);
        LoadSubrace(RaceTieflingBuilder.RaceTieflingMephistopheles);
        LoadSubrace(RaceTieflingBuilder.RaceTieflingZariel);

        _ = RaceHalfElfBuilder.RaceHalfElfVariant;

        LoadSubrace(RaceHalfElfBuilder.RaceHalfElfHighVariant);
        LoadSubrace(RaceHalfElfBuilder.RaceHalfElfSylvanVariant);
        LoadSubrace(RaceHalfElfBuilder.RaceHalfElfDarkVariant);

        LoadSubrace(SubraceDarkelfBuilder.SubraceDarkelf);
        LoadSubrace(SubraceGrayDwarfBuilder.SubraceGrayDwarf);
        LoadSubrace(SubraceIronbornDwarfBuilder.SubraceIronbornDwarf);
        LoadSubrace(SubraceObsidianDwarfBuilder.SubraceObsidianDwarf);
        LoadSubrace(SubraceShadarKaiBuilder.SubraceShadarKai);

        // sorting
        Races = Races.OrderBy(x => x.FormatTitle()).ToHashSet();
        Subraces = Subraces.OrderBy(x => x.FormatTitle()).ToHashSet();

        // settings paring
        foreach (var name in Main.Settings.RaceEnabled
                     .Where(name => Races.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.RaceEnabled.Remove(name);
        }

        foreach (var name in Main.Settings.SubraceEnabled
                     .Where(name => Subraces.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.SubraceEnabled.Remove(name);
        }

        DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
            .Do(x => x.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock));

        // final bootstrap
        LoadVision();
        SwitchDarknessPerceptive();
        SwitchDragonbornElementalBreathUsages();
        FlexibleBackgroundsContext.Load();
        FlexibleBackgroundsContext.SwitchFlexibleBackgrounds();
        FlexibleRacesContext.SwitchFlexibleRaces();
    }

    private static void LoadRace([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        Races.Add(characterRaceDefinition);
        UpdateRaceVisibility(characterRaceDefinition);
    }

    private static void LoadSubrace([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        Subraces.Add(characterRaceDefinition);
        UpdateSubraceVisibility(characterRaceDefinition);
    }

    private static void UpdateRaceVisibility([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        characterRaceDefinition.GuiPresentation.hidden =
            !Main.Settings.RaceEnabled.Contains(characterRaceDefinition.Name);

        characterRaceDefinition.SubRaces.ForEach(x => x.GuiPresentation.hidden =
            !Main.Settings.RaceEnabled.Contains(characterRaceDefinition.Name));
    }

    private static void UpdateSubraceVisibility([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        characterRaceDefinition.GuiPresentation.hidden =
            !Main.Settings.SubraceEnabled.Contains(characterRaceDefinition.Name);

        if (RaceHalfElfBuilder.RaceHalfElfVariant.SubRaces.Contains(characterRaceDefinition))
        {
            var hidden = RaceHalfElfBuilder.RaceHalfElfVariant.SubRaces.All(x => x.GuiPresentation.Hidden);

            RaceHalfElfBuilder.RaceHalfElfVariant.GuiPresentation.hidden = hidden;
        }
        else if (RaceTieflingBuilder.RaceTiefling.SubRaces.Contains(characterRaceDefinition))
        {
            var hidden = RaceTieflingBuilder.RaceTiefling.SubRaces.All(x => x.GuiPresentation.Hidden);

            RaceTieflingBuilder.RaceTiefling.GuiPresentation.hidden = hidden;
        }
    }

    internal static void Switch(CharacterRaceDefinition characterRaceDefinition, bool active)
    {
        var name = characterRaceDefinition.Name;

        if (active)
        {
            Main.Settings.RaceEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.RaceEnabled.Remove(name);
        }

        UpdateRaceVisibility(characterRaceDefinition);
    }

    internal static void SwitchSubrace(CharacterRaceDefinition characterRaceDefinition, bool active)
    {
        var name = characterRaceDefinition.Name;

        if (active)
        {
            Main.Settings.SubraceEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.SubraceEnabled.Remove(name);
        }

        UpdateSubraceVisibility(characterRaceDefinition);
    }

    private static void LoadVision()
    {
        if (Main.Settings.DisableSenseDarkVisionFromAllRaces)
        {
            foreach (var featureUnlocks in DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
                         .Select(crd => crd.FeatureUnlocks))
            {
                featureUnlocks.RemoveAll(x => x.FeatureDefinition == SenseDarkvision);
                // Half-orcs have a different darkvision.
                featureUnlocks.RemoveAll(x => x.FeatureDefinition == SenseDarkvision12);
            }
        }

        // ReSharper disable once InvertIf
        if (Main.Settings.DisableSenseSuperiorDarkVisionFromAllRaces)
        {
            foreach (var featureUnlocks in DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
                         .Select(crd => crd.FeatureUnlocks))
            {
                featureUnlocks.RemoveAll(x => x.FeatureDefinition == SenseSuperiorDarkvision);
            }
        }
    }

    internal static void SwitchDarknessPerceptive()
    {
        var races = new List<CharacterRaceDefinition>
        {
            RaceKoboldBuilder.SubraceDarkKobold,
            SubraceDarkelfBuilder.SubraceDarkelf,
            SubraceGrayDwarfBuilder.SubraceGrayDwarf
        };

        if (Main.Settings.AddDarknessPerceptiveToDarkRaces)
        {
            foreach (var characterRaceDefinition in races
                         .Where(a => !a.FeatureUnlocks.Exists(x =>
                             x.Level == 1 && x.FeatureDefinition == AbilityCheckAffinityDarknessPerceptive)))
            {
                characterRaceDefinition.FeatureUnlocks.Add(
                    new FeatureUnlockByLevel(AbilityCheckAffinityDarknessPerceptive, 1));
            }
        }
        else
        {
            foreach (var characterRaceDefinition in races
                         .Where(a => a.FeatureUnlocks.Exists(x =>
                             x.Level == 1 && x.FeatureDefinition == AbilityCheckAffinityDarknessPerceptive)))
            {
                characterRaceDefinition.FeatureUnlocks.RemoveAll(x =>
                    x.Level == 1 && x.FeatureDefinition == AbilityCheckAffinityDarknessPerceptive);
            }
        }
    }

    internal static void SwitchDragonbornElementalBreathUsages()
    {
        var powers = DatabaseRepository.GetDatabase<FeatureDefinitionPower>()
            .Where(x =>
                x.Name.StartsWith("PowerDragonbornBreathWeapon") ||
                x.Name == "PowerFeatDragonFear");

        foreach (var power in powers)
        {
            if (Main.Settings.ChangeDragonbornElementalBreathUsages)
            {
                power.usesAbilityScoreName = AttributeDefinitions.Constitution;
                power.usesDetermination = UsesDetermination.AbilityBonusPlusFixed;
                power.fixedUsesPerRecharge = 0;
            }
            else
            {
                power.usesAbilityScoreName = AttributeDefinitions.Charisma;
                power.usesDetermination = UsesDetermination.Fixed;
                power.fixedUsesPerRecharge = 1;
            }
        }
    }

    internal static void HandleSmallRaces(BattleDefinitions.AttackEvaluationParams evaluationParams)
    {
        if (!Main.Settings.UseOfficialSmallRacesDisWithHeavyWeapons)
        {
            return;
        }

        var rulesetCharacter = evaluationParams.attacker.RulesetCharacter;

        if (IsSmallSizeIdentity(rulesetCharacter) &&
            evaluationParams.attackMode is { SourceDefinition: ItemDefinition { IsWeapon: true } itemDefinition } &&
            ValidatorsWeapon.IsHeavyWeapon(itemDefinition))
        {
            evaluationParams.attackModifier.AttackAdvantageTrends.Add(
                new TrendInfo(-1, FeatureSourceType.Unknown, "Feedback/&SmallRace", null));
        }
    }

    internal static void ApplySpeciesBaseWalkSpeed(RulesetCharacter rulesetCharacter)
    {
        var walkMode = (int)MoveMode.Walk;

        if (!Main.Settings.EnableSpeciesBaseWalkSpeed2024 ||
            rulesetCharacter is not (RulesetCharacterHero or RulesetCharacterSimulacrum) ||
            !HasFiveCellBaseWalkSpeed(rulesetCharacter) ||
            !rulesetCharacter.MoveModes.TryGetValue(walkMode, out var currentSpeed) ||
            currentSpeed >= SpeciesBaseWalkSpeed2024)
        {
            return;
        }

        rulesetCharacter.MoveModes[walkMode] = SpeciesBaseWalkSpeed2024;
        SynchronizeWalkLinkedModes(rulesetCharacter, SpeciesBaseWalkSpeed2024);
    }

    internal static bool TryFormatSpeciesBaseWalkSpeedDescription(
        FeatureDefinition feature,
        out string description,
        out int displayedSpeed)
    {
        description = null;
        displayedSpeed = 0;

        if (feature is not FeatureDefinitionMoveMode
            {
                MoveMode: MoveMode.Walk,
                Speed: 5
            } moveMode)
        {
            return false;
        }

        displayedSpeed = Main.Settings.EnableSpeciesBaseWalkSpeed2024
            ? SpeciesBaseWalkSpeed2024
            : moveMode.Speed;
        description = Gui.Format(
            moveMode.GuiPresentation.Description,
            Gui.FormatDistance(displayedSpeed));

        return true;
    }

    internal static void RefreshSpeciesBaseWalkSpeeds()
    {
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return;
        }

        var rulesetCharacters = characterService.AllValidEntities
            .Select(x => x.RulesetActor)
            .Concat(characterService.PartyCharacters.Select(x => x.RulesetCharacter))
            .Concat(characterService.GuestCharacters.Select(x => x.RulesetCharacter))
            .OfType<RulesetCharacter>()
            .Where(x => x is RulesetCharacterHero or RulesetCharacterSimulacrum)
            .Distinct();

        foreach (var rulesetCharacter in rulesetCharacters)
        {
            rulesetCharacter.RefreshMoveModes();
        }
    }

    private static bool HasFiveCellBaseWalkSpeed(RulesetCharacter rulesetCharacter)
    {
        return TryGetRaceIdentity(rulesetCharacter, out var race, out var subRace) &&
               (DefinesFiveCellWalkSpeed(race) || DefinesFiveCellWalkSpeed(subRace));
    }

    private static bool DefinesFiveCellWalkSpeed(CharacterRaceDefinition race)
    {
        return race?.FeatureUnlocks.Any(x =>
            x.FeatureDefinition is FeatureDefinitionMoveMode moveMode &&
            moveMode.MoveMode == MoveMode.Walk &&
            moveMode.Speed == 5) == true;
    }

    private static bool IsSmallSizeIdentity(RulesetCharacter rulesetCharacter)
    {
        return TryGetRaceIdentity(rulesetCharacter, out var race, out _) &&
               race.SizeDefinition == DatabaseHelper.CharacterSizeDefinitions.Small;
    }

    private static void SynchronizeWalkLinkedModes(
        RulesetCharacter rulesetCharacter,
        int walkSpeed)
    {
        var implementationService =
            ServiceRepository.GetService<IRulesetImplementationService>();

        foreach (var feature in rulesetCharacter.GetMovementModifiers())
        {
            if (feature is not IMovementAffinityProvider provider ||
                !IsMovementAffinityActive(
                    rulesetCharacter,
                    feature,
                    provider,
                    implementationService))
            {
                continue;
            }

            if (provider.CanMoveOnWalls)
            {
                RaiseModeToWalkSpeed(rulesetCharacter, MoveMode.Climb, walkSpeed);
            }

            if (provider.CanFlyWithWalkSpeed)
            {
                RaiseModeToWalkSpeed(rulesetCharacter, MoveMode.Fly, walkSpeed);
            }
        }
    }

    private static bool IsMovementAffinityActive(
        RulesetCharacter rulesetCharacter,
        FeatureDefinition feature,
        IMovementAffinityProvider provider,
        IRulesetImplementationService implementationService)
    {
        if (provider.SituationalContext == SituationalContext.None)
        {
            return true;
        }

        if (implementationService == null)
        {
            return false;
        }

        var contextParams = new RulesetImplementationDefinitions.SituationalContextParams(
            provider.SituationalContext,
            rulesetCharacter,
            null,
            implementationService.FindSourceIdOfFeature(rulesetCharacter, feature),
            null,
            false,
            null);

        return implementationService.IsSituationalContextValid(contextParams);
    }

    private static void RaiseModeToWalkSpeed(
        RulesetCharacter rulesetCharacter,
        MoveMode moveMode,
        int walkSpeed)
    {
        var mode = (int)moveMode;

        if (!rulesetCharacter.MoveModes.TryGetValue(mode, out var currentSpeed) ||
            currentSpeed < walkSpeed)
        {
            rulesetCharacter.MoveModes[mode] = walkSpeed;
        }
    }

    private static bool TryGetRaceIdentity(
        RulesetCharacter rulesetCharacter,
        out CharacterRaceDefinition race,
        out CharacterRaceDefinition subRace)
    {
        if (rulesetCharacter is RulesetCharacterSimulacrum duplicate)
        {
            return SimulacrumBehavior.TryGetHumanoidIdentity(duplicate, out race, out subRace);
        }

        var hero = rulesetCharacter.GetOriginalHero();

        race = hero?.RaceDefinition;
        subRace = hero?.SubRaceDefinition;

        return race != null;
    }
}
