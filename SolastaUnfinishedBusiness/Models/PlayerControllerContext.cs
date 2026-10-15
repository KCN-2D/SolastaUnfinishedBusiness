using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using TA.AI;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.DecisionPackageDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class PlayerControllerContext
{
    private static readonly Dictionary<GameLocationCharacter, int> ControllersChoices = [];

    private static readonly Dictionary<GameLocationCharacter, HeroControllerState> OriginalHeroControllers = [];

    private static readonly Dictionary<GameLocationCharacter, Side> OriginalSides = [];

    private static readonly Dictionary<RulesetActor, Side> OriginalProxySides = [];

    private static readonly FieldInfo ActorSideField = AccessTools.Field(typeof(RulesetActor), "side");

    private static readonly List<GameLocationCharacter> OriginalControlledCharacters = [];

    private static PlayerController ControllerAtTurnStart { get; set; }

    private static int SerializationDepth { get; set; }

    internal static bool CanControlCharacters =>
        !Global.IsMultiplayer && ServiceRepository.GetService<INetworkingService>()?.InOnlineRoom != true;

    // Keep a manually controlled turn coherent after its option is switched off;
    // Stop restores the turn's state before the next turn reads the saved option.
    internal static bool AreEnemiesControlledByPlayer =>
        CanControlCharacters && (Main.Settings.EnableEnemiesControlledByPlayer || OriginalSides.Count > 0);

    internal static bool AreEnemySidesFlipped => SerializationDepth == 0 && OriginalSides.Count > 0;

    // Manual enemy turns change the displayed side. Battle-owned grants still belong
    // to the contender's original side, including while that side is being serialized.
    internal static Side GetBattleSide(GameLocationCharacter character) =>
        character == null ? Side.Neutral : OriginalSides.TryGetValue(character, out var side) ? side : character.Side;

    // Index 0 is "Human"; it is never read because human control keeps the player controller id.
    private static readonly DecisionPackageDefinition[] AiDecisionPackages =
    [
        DefaultMeleeWithBackupRangeDecisions,
        DefaultMeleeWithBackupRangeDecisions,
        DefaultRangeWithBackupMeleeDecisions,
        DefaultSupportCasterWithBackupAttacksDecisions,
        ClericCombatDecisions,
        FighterCombatDecisions,
        CasterCombatDecisions,
        RogueCombatDecisions
    ];

    internal static string[] Controllers =>
    [
        Gui.Localize("ModUi/&ControllerHuman"),
        Gui.Localize("ModUi/&ControllerMelee"),
        Gui.Localize("ModUi/&ControllerRanged"),
        Gui.Localize("ModUi/&ControllerSupportCaster"),
        Gui.Localize("Class/&ClericTitle"),
        Gui.Localize("Class/&FighterTitle"),
        Gui.Localize("Class/&WizardTitle"),
        Gui.Localize("Class/&RogueTitle")
    ];

    // ReSharper disable once InconsistentNaming
    private static int[] playerCharactersChoices { get; set; }

    internal static List<GameLocationCharacter> PlayerCharacters { get; } = [];

    internal static int[] PlayerCharactersChoices
    {
        get => playerCharactersChoices;

        set
        {
            var count = PlayerCharacters.Count;
            playerCharactersChoices = new int[count];

            for (var i = 0; i < count; i++)
            {
                var playerCharacter = PlayerCharacters[i];

                var choice = NormalizeControllerChoice(value != null && i < value.Length ? value[i] : 0);

                playerCharactersChoices[i] = choice;
                ControllersChoices[playerCharacter] = choice;
            }
        }
    }

    private static int NormalizeControllerChoice(int choice)
    {
        return choice >= 0 && choice < AiDecisionPackages.Length ? choice : 0;
    }

    internal static void RefreshGuiState()
    {
        if (!Gui.GameCampaign)
        {
            return;
        }

        var controllersChoicesCopy = ControllersChoices.ToDictionary(x => x.Key, x => x.Value);
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return;
        }

        ControllersChoices.Clear();
        PlayerCharacters.Clear();
        PlayerCharacters.AddRange(characterService.PartyCharacters.Concat(characterService.GuestCharacters).Distinct());
        PlayerCharacters.ForEach(x =>
            ControllersChoices.Add(x, controllersChoicesCopy.TryGetValue(x, out var choice) ? choice : 0));
        playerCharactersChoices = PlayerCharacters.Select(character => ControllersChoices[character]).ToArray();
    }

    private static void UpdatePartyControllerIds()
    {
        var activePlayerController = Gui.ActivePlayerController;

        if (activePlayerController == null)
        {
            return;
        }

        foreach (var playerCharacter in PlayerCharacters)
        {
            var choice = NormalizeControllerChoice(ControllersChoices.TryGetValue(playerCharacter, out var selected)
                ? selected
                : 0);

            if (choice == 0)
            {
                continue;
            }

            var decisionPackage = AiDecisionPackages[choice];
            CaptureControllerState(activePlayerController);
            OriginalHeroControllers[playerCharacter] = new HeroControllerState(playerCharacter);
            playerCharacter.ControllerId = PlayerControllerManager.DmControllerId;

            playerCharacter.BehaviourPackage ??= new GameLocationBehaviourPackage
            {
                BattleStartBehavior =
                    GameLocationBehaviourPackage.BattleStartBehaviorType.DoNotRaiseAlarm
            };

            playerCharacter.BehaviourPackage.DecisionPackageDefinition = decisionPackage;
        }

        if (OriginalHeroControllers.Count > 0)
        {
            activePlayerController.DirtyControlledCharacters();
        }
    }

    internal static void Start([NotNull] GameLocationBattle battle)
    {
        RestoreControllerState();

        if (!CanControlCharacters || battle?.ActiveContender == null)
        {
            return;
        }

        var activeContender = battle.ActiveContender;
        var enemies = battle.EnemyContenders;
        var players = battle.PlayerContenders;

        if (Main.Settings.EnableHeroesControlledByComputer && players.Contains(activeContender))
        {
            RefreshGuiState();
            UpdatePartyControllerIds();
        }

        var activePlayerController = Gui.ActivePlayerController;

        if (!AreEnemiesControlledByPlayer || !enemies.Contains(activeContender) || activePlayerController == null)
        {
            return;
        }

        CaptureControllerState(activePlayerController);

        foreach (var contender in enemies.Concat(players).Distinct())
        {
            OriginalSides[contender] = contender.Side;

            if (contender.RulesetCharacter == null)
            {
                continue;
            }

            foreach (var proxy in contender.RulesetCharacter.ControlledEffectProxies)
            {
                OriginalProxySides[proxy] = proxy.Side;
            }
        }

        enemies.ForEach(x => x.ChangeSide(Side.Ally));
        players.ForEach(x => x.ChangeSide(Side.Enemy));
        var controlledCharacters = activePlayerController.ControlledCharacters;
        controlledCharacters.Clear();
        controlledCharacters.AddRange(enemies.Distinct());
    }

    internal static void Stop()
    {
        RestoreControllerState();
    }

    internal static IDisposable SuspendControlForSerialization()
    {
        return OriginalHeroControllers.Count == 0 && OriginalSides.Count == 0
            ? null
            : new ControllerSerializationScope();
    }

    internal static void Unload()
    {
        RestoreControllerState();
        ControllersChoices.Clear();
        PlayerCharacters.Clear();
        playerCharactersChoices = [];
    }

    private static void CaptureControllerState(PlayerController controller)
    {
        if (ControllerAtTurnStart != null)
        {
            return;
        }

        ControllerAtTurnStart = controller;
        OriginalControlledCharacters.AddRange(controller.ControlledCharacters);
    }

    private static void RestoreControllerState()
    {
        RestoreHeroControllers(OriginalHeroControllers);

        OriginalHeroControllers.Clear();

        foreach (var entry in OriginalSides)
        {
            entry.Key.ChangeSide(entry.Value);
        }

        OriginalSides.Clear();

        RestoreSerializedSides(OriginalProxySides);
        OriginalProxySides.Clear();

        if (ControllerAtTurnStart != null)
        {
            var controlledCharacters = ControllerAtTurnStart.ControlledCharacters;
            controlledCharacters.Clear();
            controlledCharacters.AddRange(OriginalControlledCharacters);
            ControllerAtTurnStart.DirtyControlledCharacters();
        }

        OriginalControlledCharacters.Clear();
        ControllerAtTurnStart = null;
    }

    private static void RestoreHeroControllers(IEnumerable<KeyValuePair<GameLocationCharacter, HeroControllerState>> states)
    {
        foreach (var entry in states)
        {
            entry.Value.Restore(entry.Key);
        }
    }

    private static void RestoreSerializedSides(IEnumerable<KeyValuePair<RulesetActor, Side>> states)
    {
        foreach (var entry in states)
        {
            // Native ChangeSide propagates to proxies and recalculates hit points.
            // Serialization must only select the stored side, without those gameplay effects.
            ActorSideField.SetValue(entry.Key, entry.Value);
        }
    }

    private readonly struct HeroControllerState
    {
        private readonly int controllerId;
        private readonly GameLocationBehaviourPackage behaviourPackage;
        private readonly DecisionPackageDefinition decisionPackage;

        internal HeroControllerState(GameLocationCharacter character)
        {
            controllerId = character.ControllerId;
            behaviourPackage = character.BehaviourPackage;
            decisionPackage = behaviourPackage?.DecisionPackageDefinition;
        }

        internal void Restore(GameLocationCharacter character)
        {
            character.ControllerId = controllerId;
            character.BehaviourPackage = behaviourPackage;

            if (behaviourPackage != null)
            {
                behaviourPackage.DecisionPackageDefinition = decisionPackage;
            }
        }
    }

    private sealed class ControllerSerializationScope : IDisposable
    {
        private readonly Dictionary<GameLocationCharacter, HeroControllerState> heroStates;

        private readonly Dictionary<RulesetActor, Side> actorSides;

        private bool disposed;

        internal ControllerSerializationScope()
        {
            heroStates = OriginalHeroControllers.Keys.ToDictionary(character => character,
                character => new HeroControllerState(character));

            var originalActorSides = new Dictionary<RulesetActor, Side>();

            foreach (var entry in OriginalSides)
            {
                originalActorSides[entry.Key.RulesetActor] = entry.Value;

                if (entry.Key.RulesetCharacter == null)
                {
                    continue;
                }

                foreach (var proxy in entry.Key.RulesetCharacter.ControlledEffectProxies)
                {
                    // A proxy created during this turn inherits its controller's temporary side.
                    originalActorSides[proxy] = OriginalProxySides.TryGetValue(proxy, out var side) ? side : entry.Value;
                }
            }

            foreach (var entry in OriginalProxySides)
            {
                originalActorSides[entry.Key] = entry.Value;
            }

            actorSides = originalActorSides.Keys.ToDictionary(actor => actor, actor => actor.Side);
            SerializationDepth++;

            try
            {
                RestoreHeroControllers(OriginalHeroControllers);
                RestoreSerializedSides(originalActorSides);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                RestoreHeroControllers(heroStates);
            }
            finally
            {
                try
                {
                    RestoreSerializedSides(actorSides);
                }
                finally
                {
                    SerializationDepth--;
                }
            }
        }
    }
}
