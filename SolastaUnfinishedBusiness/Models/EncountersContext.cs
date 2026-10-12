using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Classes;
using TA;
using TA.AI;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.DecisionPackageDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class EncountersContext
{
    internal const int MaxEncounterCharacters = 16;

    private static readonly List<RulesetCharacterHero> Heroes = [];
    private static ICharacterPoolService _heroPoolService;
    private static object _heroPool;
    private static string[] _heroPoolKeys = [];
    private static RulesetCharacterHero.Snapshot[] _heroPoolSnapshots = [];
    private static string _heroLanguage;
    private static bool _heroesInitialized;
    internal static int HeroCatalogRevision { get; private set; }
    internal static int UnavailableHeroCount { get; private set; }

    private static readonly List<MonsterDefinition> Monsters = [];
    private static string _monsterLanguage;

    internal static readonly List<RulesetCharacter> EncounterCharacters = [];

    private static ulong EncounterId { get; set; } = 10000;

    internal static void AddToEncounter(RulesetCharacterHero hero)
    {
        if (EncounterCharacters.Count < MaxEncounterCharacters)
        {
            EncounterCharacters.Add(hero);
        }
    }

    internal static void AddToEncounter(MonsterDefinition monsterDefinition)
    {
        if (EncounterCharacters.Count < MaxEncounterCharacters)
        {
            EncounterCharacters.Add(new RulesetCharacterMonster(monsterDefinition, 0,
                new SpawnOverrides(), GadgetDefinitions.CreatureSex.Male));
        }
    }

    internal static void RemoveFromEncounter(int index)
    {
        if (index >= 0 && index < EncounterCharacters.Count)
        {
            EncounterCharacters.RemoveAt(index);
        }
    }

    [NotNull]
    internal static List<MonsterDefinition> GetMonsters()
    {
        if (Monsters.Count == 0)
        {
            var monsterDefinitionDatabase = DatabaseRepository.GetDatabase<MonsterDefinition>();
            Monsters.AddRange(monsterDefinitionDatabase.Where(x =>
                x.DungeonMakerPresence == MonsterDefinition.DungeonMaker.Monster));
            _monsterLanguage = null;
        }
        var language = I2.Loc.LocalizationManager.CurrentLanguageCode;
        if (_monsterLanguage != language)
        {
            // Translate once per catalog/language change instead of inside the sort comparer.
            var titles = Monsters.ToDictionary(monster => monster, monster => monster.FormatTitle());
            Monsters.Sort((a, b) => Math.Abs(a.ChallengeRating - b.ChallengeRating) < 0.001f
                ? string.Compare(titles[a], titles[b], StringComparison.CurrentCultureIgnoreCase)
                : a.ChallengeRating.CompareTo(b.ChallengeRating));
            _monsterLanguage = language;
        }

        return Monsters;
    }

    [NotNull]
    internal static List<RulesetCharacterHero> GetHeroes()
    {
        var characterPoolService = ServiceRepository.GetService<ICharacterPoolService>();

        if (!IsHeroCatalogCurrent(characterPoolService))
        {
            LoadHeroCatalog(characterPoolService);
        }

        var language = I2.Loc.LocalizationManager.CurrentLanguageCode ?? string.Empty;

        if (_heroLanguage != language)
        {
            Heroes.Sort((a, b) =>
            {
                var compareName = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return compareName == 0
                    ? string.Compare(a.SurName, b.SurName, StringComparison.CurrentCultureIgnoreCase)
                    : compareName;
            });
            _heroLanguage = language;
        }

        return Heroes;
    }

    private static bool IsHeroCatalogCurrent(ICharacterPoolService characterPoolService)
    {
        var pool = characterPoolService?.Pool;

        if (!_heroesInitialized || !ReferenceEquals(characterPoolService, _heroPoolService) ||
            !ReferenceEquals(pool, _heroPool) || (pool?.Count ?? 0) != _heroPoolKeys.Length)
        {
            return false;
        }

        if (pool == null)
        {
            return true;
        }

        var index = 0;

        foreach (var entry in pool)
        {
            if (!string.Equals(entry.Key, _heroPoolKeys[index], StringComparison.Ordinal) ||
                !ReferenceEquals(entry.Value, _heroPoolSnapshots[index]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static void LoadHeroCatalog(ICharacterPoolService characterPoolService)
    {
        var pool = characterPoolService?.Pool;
        _heroesInitialized = true;
        _heroPoolService = characterPoolService;
        _heroPool = pool;
        _heroPoolKeys = pool?.Keys.ToArray() ?? [];
        _heroPoolSnapshots = pool?.Values.ToArray() ?? [];
        Heroes.Clear();
        UnavailableHeroCount = 0;
        HeroCatalogRevision++;
        _heroLanguage = null;

        foreach (var filename in _heroPoolKeys)
        {
            // Pool keys already identify built-in and user files. Rebuilding a path
            // from the hero name incorrectly redirects built-in heroes to Characters.
            try
            {
                characterPoolService.LoadCharacter(filename, out var hero, out _);

                if (hero != null)
                {
                    Heroes.Add(hero);
                    continue;
                }

                Main.Info($"Cannot load encounter hero from '{filename}': no character was returned.");
            }
            catch (Exception ex)
            {
                // Native binary deserialization wraps failures in Exception, so an
                // IOException filter would miss corrupt files. Isolate each optional
                // catalog entry, as the native pool enumerator does, and cache failures.
                Main.Info($"Cannot load encounter hero from '{filename}': {ex}");
            }

            UnavailableHeroCount++;
        }
    }

    internal static void RefreshHeroes()
    {
        // Retry failed files only on demand or when the native pool changes, never
        // during every IMGUI event. The native pool and character files stay untouched.
        _heroesInitialized = false;
    }

    internal static void ConfirmStageEncounter()
    {
        if (Global.IsMultiplayer)
        {
            Gui.GuiService.ShowMessage(
                MessageModal.Severity.Informative1,
                "Message/&SpawnCustomEncounterTitle",
                "Message/&SpawnCustomEncounterErrorDescription",
                "Message/&MessageOkTitle", string.Empty,
                null,
                null);

            return;
        }

        if (!Gui.GameLocation)
        {
            return;
        }

        if (!Gui.GameLocation.LocationDefinition ||
            !Gui.GameLocation.LocationDefinition.IsUserLocation)
        {
            return;
        }

        var position = GetEncounterPosition();

        Gui.GuiService.ShowMessage(
            MessageModal.Severity.Attention2,
            "Message/&SpawnCustomEncounterTitle",
            Gui.Format("Message/&SpawnCustomEncounterDescription", position.x.ToString(),
                position.z.ToString()),
            "Message/&MessageYesTitle", "Message/&MessageNoTitle",
            () => StageEncounter(position),
            null);
    }

    private static int3 GetEncounterPosition()
    {
        var gameLocationService = ServiceRepository.GetService<IGameLocationService>();

        var x = (int)gameLocationService.GameLocation.LastCameraPosition.x;
        var z = (int)gameLocationService.GameLocation.LastCameraPosition.z;

        return new int3(x, 0, z);
    }

    private static DecisionPackageDefinition GetBestDecisionPackage(RulesetCharacter __instance)
    {
        var package = DefaultMeleeWithBackupRangeDecisions;

        switch (__instance)
        {
            case RulesetCharacterHero rulesetHero when rulesetHero.ClassesHistory.Contains(Cleric):
                package = ClericCombatDecisions;
                break;
            case RulesetCharacterHero rulesetHero when rulesetHero.ClassesHistory.Contains(Fighter):
                package = FighterCombatDecisions;
                break;
            case RulesetCharacterHero rulesetHero when rulesetHero.ClassesHistory.Contains(Paladin):
                package = PaladinCombatDecisions;
                break;
            case RulesetCharacterHero rulesetHero when rulesetHero.ClassesHistory.Contains(Rogue):
                package = RogueCombatDecisions;
                break;
            case RulesetCharacterHero rulesetHero when rulesetHero.ClassesHistory.Contains(Druid):
                package = CasterCombatDecisions;
                break;
            case RulesetCharacterHero rulesetHero:
            {
                if (rulesetHero.ClassesHistory.Contains(InventorClass.Class) ||
                    rulesetHero.ClassesHistory.Contains(Bard) ||
                    rulesetHero.ClassesHistory.Contains(Sorcerer) ||
                    rulesetHero.ClassesHistory.Contains(Warlock) ||
                    rulesetHero.ClassesHistory.Contains(Wizard))
                {
                    package = OffensiveCasterCombatDecisions;
                }

                break;
            }
            case RulesetCharacterMonster rulesetMonster:
            {
                if (rulesetMonster.MonsterDefinition.Features.Any(x => x is FeatureDefinitionCastSpell))
                {
                    package = DefaultSupportCasterWithBackupAttacksDecisions;
                }

                if (rulesetMonster.MonsterDefinition.AttackIterations.Any(x =>
                        x.MonsterAttackDefinition.Proximity == AttackProximity.Range))
                {
                    package = DefaultRangeWithBackupMeleeDecisions;
                }

                break;
            }
        }

        return package;
    }

    private static void StageEncounter(int3 position)
    {
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
        var positions = new List<int3>();
        var formationPositions = new List<int3>();
        var sizeList = new List<RulesetActor.SizeParameters>();
        var characters = new List<GameLocationCharacter>();

        for (var iy = 0; iy < 4; iy++)
        {
            for (var ix = 0; ix < 4; ix++)
            {
                formationPositions.Add(new int3(ix, 0, iy));
            }
        }

        foreach (var gameLocationCharacter in EncounterCharacters
                     .Select(character =>
                         characterService.CreateCharacter(
                             PlayerControllerManager.DmControllerId, character, Side.Enemy,
                             new GameLocationBehaviourPackage
                             {
                                 BattleStartBehavior =
                                     GameLocationBehaviourPackage.BattleStartBehaviorType.DoNotRaiseAlarm,
                                 DecisionPackageDefinition = GetBestDecisionPackage(character),
                                 EncounterId = EncounterId++,
                                 FormationDefinition = DatabaseHelper.FormationDefinitions.Column2
                             })))
        {
            gameLocationCharacter.CollectExistingLightSources(true);
            gameLocationCharacter.RefreshActionPerformances();
            gameLocationCharacter.RulesetCharacter.SetBaseFaction(DatabaseHelper.FactionDefinitions.HostileMonsters);
            characters.Add(gameLocationCharacter);
        }

        positioningService.ComputeFormationPlacementPositions(
            characters, position, LocationDefinitions.Orientation.North, formationPositions,
            CellHelpers.PlacementMode.Station, positions, sizeList, 25);

        for (var index = 0; index < positions.Count; index++)
        {
            positioningService.PlaceCharacter(
                characters[index], positions[index], LocationDefinitions.Orientation.North);
            characterService.RevealCharacter(characters[index]);
        }

        Heroes.Clear();
        _heroesInitialized = false;
        Monsters.Clear();
        EncounterCharacters.Clear();
    }
}
