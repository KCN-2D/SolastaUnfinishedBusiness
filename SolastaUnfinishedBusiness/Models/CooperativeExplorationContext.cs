using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Photon.Pun;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class CooperativeExplorationContext
{
    private const string PlayerProperty = "UB.Activity.Player.v1";
    private const string EpochProperty = "UB.Activity.Epoch.v1";
    private const string CommitProperty = "UB.Activity.Commit.v1";
    private const int MaxPayloadLength = 64 * 1024;
    private const int MaxActivities = 12;
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None, MaxDepth = 12,
        MissingMemberHandling = MissingMemberHandling.Error
    };
    private static readonly Dictionary<CharacterActionParams, RunningActivity> Running = new();
    private static readonly HashSet<string> Executed = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, (string Payload, string Epoch, PlayerState State)> PeerStates = new();
    private static string planJson;
    private static string planDigest;
    private static PlayerState local = new();
    private static Batch prepared;
    private static object location;
    private static string roomName;
    private static string sentBatch;
    private static float prepareDeadline;
    private static Action releaseClock;
    private static float nextRefresh;
    private static bool running;
    private static bool cancelled;
    private static int generation;
    private static string runningId;
    private static string cancelSent;
    private static int authorizedStage = -1;
    private static int sentStage = -1;
    private static int cancellationStage = int.MaxValue;
    private static ulong followLeader;
    private static TA.int3? leaderDestination;
    private static float nextFollow;
    private static bool issuingFollow;
    private static readonly Dictionary<ulong, (TA.int3 Position, float At)> FollowDestinations = new();
    internal static ulong FollowLeader => followLeader;
    [ThreadStatic] private static int advancingClock;

    internal static bool Online => MultiplayerSettingsContext.IsOnlineRoom;
    internal static bool IsHost => !Online || PhotonNetwork.IsMasterClient;
    internal static int LocalPlayer => Online ? PhotonNetwork.LocalPlayer.ActorNumber : 0;
    internal static bool Enabled => Main.Enabled && Main.Settings.EnableCoordinatedExplorationActivities;
    internal static bool Busy => running || prepared != null;
    internal static bool HasPanelActions => Busy ||
        Enabled && GetPlayers().Any(player => player.State.Activities.Count > 0) ||
        Main.Settings.EnableMultiplayerAutoFollow && (FollowLeader != 0 || FollowCandidates().Any());
    internal static bool FreezeClock => running && advancingClock == 0;
    internal enum ActivityFailure
    {
        None,
        MatchingPlayersRequired,
        UnsupportedCast,
        InvalidPlan
    }

    internal static ActivityFailure Failure { get; private set; }
    internal static string PlanDigest
    {
        get
        {
            var json = JsonConvert.SerializeObject(GetPlan(), Formatting.None);
            if (json == planJson) return planDigest;
            planJson = json;
            return planDigest = Digest(json);
        }
    }
    internal static bool Ready => local.Ready == PlanDigest;
    internal static bool CanStart => Enabled && !Busy && GetPlan().Count > 0 &&
        MultiplayerSettingsContext.AllPlayersMatch && GetPlayers().All(player => player.State.Ready == PlanDigest);

    internal sealed class Reservation
    {
        public ulong Actor { get; set; }
        public bool Power { get; set; }
        public string Definition { get; set; }
        public string Repertoire { get; set; }
        public string CastingClass { get; set; }
        public string Invocation { get; set; }
        public string Metamagic { get; set; }
        public int Action { get; set; }
        public int Level { get; set; }
        public int Duration { get; set; }
        public ulong[] Targets { get; set; } = [];
        public ulong Item { get; set; }
        public int[][] Positions { get; set; } = [];
        public int Parameter { get; set; }
        public int Parameter2 { get; set; }
        public bool[] Flags { get; set; } = [];
        public string Text { get; set; }
        public string Text2 { get; set; }
    }

    internal sealed class OwnedReservation
    {
        public int Owner { get; set; }
        public Reservation Activity { get; set; }
    }

    private sealed class PlayerState
    {
        public int Protocol { get; set; } = 1;
        public string Epoch { get; set; }
        public List<Reservation> Activities { get; set; } = [];
        public string Ready { get; set; }
        public string Ack { get; set; }
        public string Failure { get; set; }
        public string StageBatch { get; set; }
        public int Stage { get; set; }
    }

    private sealed class Batch
    {
        public string Id { get; set; }
        public string Epoch { get; set; }
        public string Digest { get; set; }
        public List<OwnedReservation> Activities { get; set; } = [];
    }

    private sealed class RunningActivity(Reservation reservation, CharacterActionParams parameters)
    {
        internal readonly Reservation Reservation = reservation;
        internal readonly CharacterActionParams Parameters = parameters;
        internal CharacterActionMagicEffect Action;
        internal bool Prepared;
        internal bool Started;
        internal bool Waiting;
        internal bool Released;
        internal bool Done;
        internal bool Failed;
    }

    internal static GameLocationCharacter Character(ulong guid) =>
        RulesetEntity.TryGetEntity<RulesetCharacter>(guid, out var actor) ? GameLocationCharacter.GetFromActor(actor) : null;

    internal static IEnumerable<GameLocationCharacter> ActivityCharacters() => ServiceRepository
        .GetService<IPlayerControllerService>()?.ActivePlayerController?.ControlledCharacters
        .Where(character => TargetingContext.IsPlayerPartyActor(character) && Owns(character, LocalPlayer) &&
                            !character.RulesetCharacter.IsDeadOrDyingOrUnconscious)
        ?? Enumerable.Empty<GameLocationCharacter>();

    internal static List<OwnedReservation> GetPlan() => GetPlayers()
        .SelectMany(player => player.State.Activities.Select(activity =>
            new OwnedReservation { Owner = player.Actor, Activity = activity }))
        .OrderBy(entry => entry.Owner).ThenBy(entry => entry.Activity.Actor).ToList();

    internal static IEnumerable<(int Actor, string Name, bool Ready)> PlayerStatuses() => GetPlayers()
        .Select(player => (player.Actor, player.Name, player.State.Ready == PlanDigest));

    private static List<(int Actor, string Name, PlayerState State)> GetPlayers()
    {
        if (!Online)
        {
            return [(0, string.Empty, local)];
        }

        var players = new List<(int, string, PlayerState)>();
        foreach (var player in PhotonNetwork.CurrentRoom.Players.Values.Where(player => !player.IsInactive)
                     .OrderBy(player => player.ActorNumber))
        {
            var state = local;
            if (!player.IsLocal)
            {
                var payload = player.CustomProperties[PlayerProperty] as string;
                if (!PeerStates.TryGetValue(player.ActorNumber, out var cached) ||
                    cached.Payload != payload || cached.Epoch != local.Epoch)
                {
                    state = Read<PlayerState>(payload);
                    if (state?.Protocol != 1 || state.Epoch != local.Epoch || state.Activities == null ||
                        state.Activities.Count > MaxActivities || state.Activities.Any(activity => !IsWellFormed(activity)))
                        state = new PlayerState();
                    PeerStates[player.ActorNumber] = (payload, local.Epoch, state);
                }
                else state = cached.State;
            }
            players.Add((player.ActorNumber, player.NickName ?? player.ActorNumber.ToString(), state));
        }
        return players;
    }

    internal static void Refresh(NetworkingManager manager, bool force = false)
    {
        if (!Main.Enabled || Main.IsApplicationQuitting || Gui.GameLocation == null || Gui.Battle != null)
        {
            if (location != null) Reset();
            return;
        }
        if (!force && Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.25f;

        var room = Online ? PhotonNetwork.CurrentRoom.Name : string.Empty;
        if (!ReferenceEquals(location, Gui.GameLocation) || roomName != room)
        {
            Reset();
            location = Gui.GameLocation;
            roomName = room;
            if (IsHost)
            {
                local.Epoch = Guid.NewGuid().ToString("N");
                if (Online) PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
                    { [EpochProperty] = local.Epoch, [CommitProperty] = null });
            }
        }

        if (Enabled || HasPanelActions)
        {
            CooperativeExplorationPanel.Refresh(manager);
        }
        else CooperativeExplorationPanel.Hide();
        if (!Enabled && !Busy) return;

        if (Online && PhotonNetwork.CurrentRoom.CustomProperties[EpochProperty] is string epoch &&
            epoch.Length == 32 && epoch != local.Epoch)
        {
            local = new PlayerState { Epoch = epoch };
            prepared = null;
            Publish();
        }
        if (local.Epoch == null) return;
        if (Online && PhotonNetwork.LocalPlayer.CustomProperties[PlayerProperty] == null) Publish();
        if (!Busy && local.Activities.RemoveAll(activity => Character(activity.Actor) is not { } actor ||
                actor.RulesetCharacter.IsDeadOrDyingOrUnconscious || !TargetingContext.IsPlayerPartyActor(actor) ||
                !Owns(actor, LocalPlayer)) > 0)
        {
            local.Ready = null;
            Publish();
        }

        if (Busy && (!Enabled || !MultiplayerSettingsContext.AllPlayersMatch) && local.Failure != prepared?.Id)
        {
            local.Failure = prepared?.Id;
            Publish();
            RequestCancel();
        }
        if (running && Online && IsHost)
        {
            var players = GetPlayers();
            if (players.Any(player => player.State.Failure == runningId)) RequestCancel();
            if (cancelSent != runningId && local.StageBatch == runningId && local.Stage > sentStage &&
                players.All(player => player.State.StageBatch == runningId && player.State.Stage >= local.Stage))
            {
                sentStage = local.Stage;
                SendControl(false, sentStage + 1);
            }
        }

        if (!running && Online)
        {
            if (prepared != null && Time.unscaledTime >= prepareDeadline) RequestCancel();
            var incoming = Read<Batch>(PhotonNetwork.CurrentRoom.CustomProperties[CommitProperty] as string);
            if (incoming == null)
            {
                prepared = null;
                local.Ack = null;
            }
            else if (incoming.Epoch == local.Epoch && !Executed.Contains(incoming.Id ?? string.Empty) &&
                     incoming.Id != prepared?.Id)
            {
                var plan = GetPlan();
                if (!MultiplayerSettingsContext.AllPlayersMatch || incoming.Digest != PlanDigest ||
                    local.Ready != incoming.Digest ||
                    JsonConvert.SerializeObject(incoming.Activities) != JsonConvert.SerializeObject(plan) ||
                    !ValidatePlan(incoming.Activities))
                {
                    local.Failure = incoming.Id;
                    Publish();
                    Failure = ActivityFailure.InvalidPlan;
                }
                else
                {
                    prepared = incoming;
                    prepareDeadline = Time.unscaledTime + 60;
                    local.Ack = incoming.Id;
                    local.Failure = null;
                    Publish();
                }
            }

            if (IsHost && incoming != null)
            {
                var players = GetPlayers();
                if (players.Any(player => player.State.Failure == incoming.Id))
                {
                    ClearCommit();
                }
                else if (prepared != null && sentBatch != prepared.Id &&
                         players.All(player => player.State.Ack == prepared.Id))
                {
                    SendBatch(manager, prepared);
                }
            }
        }
    }

    internal static void Reset()
    {
        generation++;
        cancelled = true;
        running = false;
        var release = releaseClock;
        releaseClock = null;
        release?.Invoke();
        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        foreach (var entry in Running.Values.ToArray())
        {
            if (entry.Done) continue;
            actionService?.StopCharacterActions(entry.Parameters.ActingCharacter, CharacterAction.InterruptionType.Invalid, true);
            entry.Parameters.RulesetEffect?.Terminate(false);
        }
        Running.Clear();
        running = false;
        runningId = null;
        cancelSent = null;
        prepared = null;
        local = new PlayerState();
        location = null;
        roomName = null;
        sentBatch = null;
        Failure = ActivityFailure.None;
        StopFollowing();
        CooperativeExplorationPanel.Reset();
        Executed.Clear();
        PeerStates.Clear();
        planJson = planDigest = null;
    }

    internal static void OnPartyChanged()
    {
        // Finish the already authorized time interval before cancelling. Resetting
        // one peer immediately can leave it on the previous campaign second.
        if (running) RequestCancel();
        else Reset();
    }

    internal static IEnumerable<GameLocationCharacter> FollowCandidates() => ServiceRepository
        .GetService<IGameLocationCharacterService>()?.PartyCharacters
        .Where(character => Online && !Owns(character, LocalPlayer) && !character.RulesetCharacter.IsDeadOrDyingOrUnconscious)
        ?? Enumerable.Empty<GameLocationCharacter>();

    internal static void Follow(ulong leader)
    {
        followLeader = leader;
        leaderDestination = null;
        FollowDestinations.Clear();
    }

    internal static void StopFollowing() => Follow(0);

    internal static void OnLocalMove(GameLocationCharacter character)
    {
        if (!issuingFollow && Owns(character, LocalPlayer)) StopFollowing();
    }

    internal static void OnMoveDestination(ulong leader, TA.int3 destination)
    {
        if (leader == followLeader) leaderDestination = destination;
    }

    internal static void UpdateFollow()
    {
        if (!Online || !Main.Settings.EnableMultiplayerAutoFollow || followLeader == 0 || Busy ||
            Gui.Battle != null || Gui.GameLocation == null || Time.unscaledTime < nextFollow) return;
        nextFollow = Time.unscaledTime + 0.75f;
        if (Gui.GuiService.GetScreen<CharacterInspectionScreen>()?.Visible == true) return;
        var leader = Character(followLeader);
        if (leader == null || Owns(leader, LocalPlayer) || leader.RulesetCharacter.IsDeadOrDyingOrUnconscious)
        {
            StopFollowing();
            return;
        }
        var destination = leaderDestination ?? leader.LocationPosition;
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
        var actions = ServiceRepository.GetService<IGameLocationActionService>();
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var claimed = new HashSet<TA.int3> { destination };
        foreach (var actor in characters.PartyCharacters.Where(character => Owns(character, LocalPlayer)))
        {
            if (actor.RulesetCharacter.IsDeadOrDyingOrUnconscious || actions.IsCharacterActing(actor) ||
                (actor.LocationPosition - destination).magnitude <= 3) continue;
            for (var index = 0; index < 9; index++)
            {
                var cell = destination + new TA.int3(index % 3 - 1, 0, index / 3 - 1);
                if (claimed.Contains(cell) || !positioning.CanPlaceCharacter(actor, cell, CellHelpers.PlacementMode.Station)) continue;
                claimed.Add(cell);
                var guid = actor.RulesetCharacter.Guid;
                if (FollowDestinations.TryGetValue(guid, out var previous) && previous.Position == cell &&
                    Time.unscaledTime - previous.At < 5) break;
                FollowDestinations[guid] = (cell, Time.unscaledTime);
                issuingFollow = true;
                try
                {
                    ServiceRepository.GetService<ICommandService>().MoveCharacter(actor, cell, actor.Orientation,
                        0f, actor.GetMoveStance(), null, false, true);
                }
                finally { issuingFollow = false; }
                break;
            }
        }
    }

    internal static int ItemTransferRange(int native) => Main.Enabled && Main.Settings.ExtendExplorationItemTransferRange &&
        Gui.GameLocation != null && Gui.Battle == null ? 24 : native;

    private static void Publish()
    {
        if (Online) PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable
            { [PlayerProperty] = JsonConvert.SerializeObject(local, Formatting.None) });
    }

    internal static int GetDuration(CharacterActionParams parameters)
    {
        if (parameters?.RulesetEffect is RulesetEffectPower power)
            return ComputeActivationTimeSeconds(power.PowerDefinition.ActivationTime);
        if (parameters?.RulesetEffect is not RulesetEffectSpell spell ||
            spell is RulesetEffectSpellWithOrigin { BypassCastingTime: true }) return 0;
        var custom = spell.SpellDefinition.GetFirstSubFeatureOfType<CustomSpellCastingTime>();
        return custom?.DurationSeconds ?? ComputeActivationTimeSeconds(
            spell.SlotLevel < 0 && spell.SpellRepertoire == null && spell.SpellDefinition.Ritual
                ? spell.SpellDefinition.RitualCastingTime : spell.SpellDefinition.ActivationTime);
    }

    internal static bool Intercept(CharacterActionParams parameters, CharacterAction.ActionExecutedHandler callback)
    {
        if ((!Enabled && !Busy) || Gui.Battle != null || Gui.GameLocation == null || Running.ContainsKey(parameters)) return false;
        if (parameters.ActionDefinition.Id == (ActionDefinitions.Id)ExtraActionId.CoordinateExploration) return false;
        if (!Busy && (GetDuration(parameters) <= 0 || !TargetingContext.IsPlayerPartyActor(parameters.ActingCharacter))) return false;

        if (Busy || !MultiplayerSettingsContext.AllPlayersMatch)
        {
            Failure = Busy ? ActivityFailure.None : ActivityFailure.MatchingPlayersRequired;
        }
        else if (parameters.RulesetEffect?.OriginItem != null ||
                 parameters.RulesetEffect is RulesetEffectSpellWithOrigin ||
                 !Owns(parameters.ActingCharacter, LocalPlayer))
        {
            Failure = ActivityFailure.UnsupportedCast;
        }
        else
        {
            SpellCastingResourceContext.PrepareNetworkAction(parameters);
            var activity = Capture(parameters);
            if (!IsWellFormed(activity))
            {
                Failure = ActivityFailure.UnsupportedCast;
            }
            else
            {
                local.Activities.RemoveAll(entry => entry.Actor == activity.Actor);
                local.Activities.Add(activity);
                local.Ready = null;
                Failure = ActivityFailure.None;
                Publish();
            }
        }

        // Rejected proposals also reopen the panel so their failure is visible.
        CooperativeExplorationPanel.Open();
        // UI effects are only proposals. Recreate them from current resources at commit.
        var action = CharacterAction.InstantiateAction(parameters);
        action?.Abort(CharacterAction.InterruptionType.Invalid);
        parameters.RulesetEffect?.Terminate(false);
        callback?.Invoke(action);
        return true;
    }

    internal static bool InterceptChain(CharacterActionChainParams chain, CharacterAction.ActionChainExecutedHandler callback)
    {
        if ((!Enabled && !Busy) || Gui.Battle != null || Gui.GameLocation == null) return false;
        var activities = chain.actionsParams.Where(parameters => GetDuration(parameters) > 0).ToArray();
        if (activities.Length == 0 && !Busy) return false;
        if (chain.actionsParams.Count == 1 && activities.Length == 1)
        {
            Intercept(activities[0], null);
        }
        else
        {
            Failure = Busy ? ActivityFailure.None : ActivityFailure.UnsupportedCast;
            foreach (var parameters in chain.actionsParams) parameters.RulesetEffect?.Terminate(false);
            CooperativeExplorationPanel.Open();
        }
        callback?.Invoke(false);
        return true;
    }

    private static Reservation Capture(CharacterActionParams parameters)
    {
        var spell = parameters.RulesetEffect as RulesetEffectSpell;
        var power = parameters.RulesetEffect as RulesetEffectPower;
        var repertoire = parameters.SpellRepertoire ?? spell?.SpellRepertoire;
        return new Reservation
        {
            Actor = parameters.ActingCharacter.RulesetCharacter.Guid,
            Definition = spell?.SpellDefinition.Name ?? power?.PowerDefinition.Name,
            Power = power != null, Repertoire = repertoire?.SpellCastingFeature.Name,
            CastingClass = repertoire?.SpellCastingClass?.Name, Metamagic = spell?.MetamagicOption?.Name,
            Invocation = spell?.RulesetInvocation?.InvocationDefinition.Name,
            Action = (int)parameters.ActionDefinition.Id, Level = spell?.SlotLevel ?? 0,
            Duration = GetDuration(parameters), Targets = parameters.TargetCharacters.Select(target => target?.RulesetCharacter?.Guid ?? 0).ToArray(),
            Item = parameters.TargetItem?.Guid ?? 0,
            Positions = parameters.Positions.Select(position => new[] { position.x, position.y, position.z }).ToArray(),
            Parameter = parameters.IntParameter, Parameter2 = parameters.IntParameter2,
            Flags = [parameters.BoolParameter, parameters.BoolParameter2, parameters.BoolParameter3, parameters.BoolParameter4, parameters.BoolParameter5],
            Text = parameters.StringParameter, Text2 = parameters.StringParameter2
        };
    }

    private static bool IsWellFormed(Reservation entry) => entry is { Actor: > 0, Duration: > 0 and <= 86400,
        Definition.Length: > 0 and <= 256, Targets.Length: <= MaxActivities, Positions.Length: <= MaxActivities,
        Flags.Length: 5 } && entry.Targets.All(target => target != 0) && entry.Positions.All(position => position?.Length == 3) &&
        entry.Level is >= -1 and <= 9 && (entry.Text?.Length ?? 0) <= 1024 && (entry.Text2?.Length ?? 0) <= 1024 &&
        (entry.Repertoire?.Length ?? 0) <= 256 && (entry.CastingClass?.Length ?? 0) <= 256 &&
        (entry.Metamagic?.Length ?? 0) <= 256 &&
        (entry.Invocation?.Length ?? 0) <= 256 &&
        (entry.Power ? entry.Action is (int)ActionDefinitions.Id.PowerMain or (int)ActionDefinitions.Id.PowerNoCost :
            entry.Action is (int)ActionDefinitions.Id.CastMain or (int)ActionDefinitions.Id.CastRitual or (int)ActionDefinitions.Id.CastNoCost ||
            !string.IsNullOrEmpty(entry.Invocation) && CustomActionIdContext.IsInvocationActionId((ActionDefinitions.Id)entry.Action));

    private static bool Owns(GameLocationCharacter character, int owner) => character != null &&
        (!Online || NetworkingDefinitions.IsCharacterControlledByPlayer(character.GetEffectControllerOrSelf(), owner));

    private static bool ValidatePlan(List<OwnedReservation> plan)
    {
        if (plan == null || plan.Count is 0 or > MaxActivities ||
            plan.Select(entry => entry.Activity?.Actor).Distinct().Count() != plan.Count) return false;
        var resting = ServiceRepository.GetService<IGameRestingService>();
        if (resting == null || resting.CampInProgress || resting.ScheduledCampActivities.Any(activity => activity.LocksCampUntilComplete)) return false;
        foreach (var entry in plan)
        {
            if (!IsWellFormed(entry.Activity) || !Owns(Character(entry.Activity.Actor), entry.Owner)) return false;
            CharacterActionParams parameters = null;
            try
            {
                parameters = Build(entry.Activity, true);
                if (parameters == null || GetDuration(parameters) != entry.Activity.Duration ||
                    !ValidateTargets(parameters)) return false;
            }
            catch (Exception) { return false; }
            finally { parameters?.RulesetEffect?.Terminate(false); }
        }
        return true;
    }

    private static bool ValidateTargets(CharacterActionParams parameters, bool beforeSpend = true)
    {
        var actor = parameters.ActingCharacter;
        var caster = actor?.RulesetCharacter;
        if (caster == null || Character(caster.Guid) != actor || caster.IsDeadOrDyingOrUnconscious || caster.IsIncapacitated ||
            !TargetingContext.ValidateActionTargets(parameters)) return false;
        var spell = parameters.RulesetEffect as RulesetEffectSpell;
        if (beforeSpend && spell != null && (!caster.AreSpellComponentsValid(spell.SpellDefinition,
                spell.RulesetInvocation?.InvocationDefinition.OverrideMaterialComponent == true) ||
            !SpellCastingValidation.IsValid(caster, parameters.SpellRepertoire, spell.SpellDefinition, spell, out _)))
            return false;
        var description = parameters.RulesetEffect.EffectDescription;
        if (description.RangeType == RangeType.Self) return true;
        var range = MetamagicContext.GetSpellRange(description, spell?.MetamagicOption);
        return parameters.TargetCharacters.All(target => target != null && actor.IsWithinRange(target, range)) &&
               parameters.Positions.All(position => DistanceCalculation.GetDistanceFromCharacter(actor, position) <= range);
    }

    private static CharacterActionParams Build(Reservation entry, bool delayRegistration)
    {
        var character = Character(entry.Actor);
        var caster = character?.RulesetCharacter;
        if (caster == null || caster.IsDeadOrDyingOrUnconscious) return null;
        var implementation = ServiceRepository.GetService<IRulesetImplementationService>();
        var parameters = new CharacterActionParams(character, (ActionDefinitions.Id)entry.Action)
        {
            IntParameter = entry.Parameter, IntParameter2 = entry.Parameter2,
            BoolParameter = entry.Flags[0], BoolParameter2 = entry.Flags[1], BoolParameter3 = entry.Flags[2],
            BoolParameter4 = entry.Flags[3], BoolParameter5 = entry.Flags[4],
            StringParameter = entry.Text, StringParameter2 = entry.Text2
        };
        foreach (var guid in entry.Targets)
        {
            var target = RulesetEntity.TryGetEntity<RulesetCharacter>(guid, out var targetActor)
                ? GameLocationCharacter.GetFromActor(targetActor) : null;
            if (target == null) return null;
            parameters.TargetCharacters.Add(target);
            parameters.ActionModifiers.Add(new ActionModifier());
        }
        foreach (var point in entry.Positions) parameters.Positions.Add(new TA.int3(point[0], point[1], point[2]));
        if (entry.Item != 0)
        {
            if (!RulesetEntity.TryGetEntity<RulesetItem>(entry.Item, out var item)) return null;
            parameters.TargetItem = item;
        }
        if (entry.Power)
        {
            var power = caster.UsablePowers.FirstOrDefault(value => value.PowerDefinition.Name == entry.Definition);
            if (power == null || !caster.CanUsePower(power.PowerDefinition)) return null;
            parameters.UsablePower = power;
            parameters.RulesetEffect = implementation.InstantiateEffectPower(caster, power, delayRegistration);
        }
        else
        {
            var spell = DatabaseRepository.GetDatabase<SpellDefinition>().GetElement(entry.Definition);
            if (spell == null) return null;
            if (!string.IsNullOrEmpty(entry.Invocation))
            {
                var invocation = caster.Invocations.FirstOrDefault(value => value.InvocationDefinition.Name == entry.Invocation);
                var grantedSpell = invocation?.InvocationDefinition.GrantedSpell;
                if (grantedSpell == null || !caster.CanCastInvocation(invocation)) return null;
                var index = grantedSpell == spell ? -1 : SpellsContext.GetSubspellLeaves(grantedSpell).IndexOf(spell);
                if (grantedSpell != spell && index < 0) return null;
                parameters.RulesetEffect = implementation.InstantiateEffectInvocation(caster, invocation, delayRegistration, index);
                if (parameters.RulesetEffect is not RulesetEffectSpell invoked || invoked.SpellDefinition != spell)
                {
                    parameters.RulesetEffect?.Terminate(false);
                    return null;
                }
                SpellCastingResourceContext.RestoreSelection(parameters);
                if (!SpellCastingResourceContext.IsSelectionAvailable(caster, invoked))
                {
                    invoked.Terminate(false);
                    return null;
                }
            }
            else if (entry.Level < 0)
            {
                var spells = new List<SpellDefinition>();
                caster.EnumerateAvailableRitualSpells(spells);
                if (!spells.Contains(spell)) return null;
                parameters.RulesetEffect = implementation.InstantiateEffectRitual(caster, spell, delayRegistration);
            }
            else
            {
                var repertoire = caster.SpellRepertoires.FirstOrDefault(value => value.SpellCastingFeature.Name == entry.Repertoire &&
                    value.SpellCastingClass?.Name == entry.CastingClass);
                if (repertoire == null) return null;
                parameters.SpellRepertoire = repertoire;
                parameters.RulesetEffect = implementation.InstantiateEffectSpell(caster, repertoire, spell, entry.Level, delayRegistration);
                SpellCastingResourceContext.RestoreSelection(parameters);
                if (!SpellCastingResourceContext.IsSelectionAvailable(caster, parameters.RulesetEffect as RulesetEffectSpell))
                {
                    parameters.RulesetEffect.Terminate(false);
                    return null;
                }
            }
            if (parameters.RulesetEffect is RulesetEffectSpell effect && !string.IsNullOrEmpty(entry.Metamagic))
                effect.MetamagicOption = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>().GetElement(entry.Metamagic);
        }
        return parameters;
    }

    internal static void Remove(ulong actor)
    {
        if (Busy) return;
        local.Activities.RemoveAll(entry => entry.Actor == actor);
        local.Ready = null;
        Publish();
    }

    internal static void ToggleReady()
    {
        if (Busy) return;
        local.Ready = Ready ? null : PlanDigest;
        Publish();
    }

    internal static void RequestStart()
    {
        if (!IsHost || !CanStart || !ValidatePlan(GetPlan()))
        {
            Failure = ActivityFailure.InvalidPlan;
            return;
        }
        var batch = new Batch { Id = Guid.NewGuid().ToString("N"), Epoch = local.Epoch, Digest = PlanDigest, Activities = GetPlan() };
        if (Online)
        {
            prepared = batch;
            prepareDeadline = Time.unscaledTime + 60;
            local.Ack = batch.Id;
            local.Failure = null;
            Publish();
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { [CommitProperty] = JsonConvert.SerializeObject(batch) });
        }
        else
        {
            prepared = batch;
            local.Ack = batch.Id;
            SendBatch(ServiceRepository.GetService<INetworkingService>() as NetworkingManager, batch);
        }
    }

    internal static void RequestCancel()
    {
        if (!Busy) return;
        if (!running)
        {
            local.Failure = prepared.Id;
            Publish();
            if (IsHost) ClearCommit();
            return;
        }
        if (!IsHost)
        {
            local.Failure = runningId;
            Publish();
            return;
        }
        if (cancelSent == runningId) return;
        cancelSent = runningId;
        SendControl(true, Math.Max(sentStage, authorizedStage) + 1);
    }

    private static void SendControl(bool cancel, int stage)
    {
        var actor = Character(prepared.Activities[0].Activity.Actor);
        if (actor == null) return;
        var parameters = new CharacterActionParams(actor, (ActionDefinitions.Id)ExtraActionId.CoordinateExploration)
            { BoolParameter = cancel, IntParameter = stage, StringParameter = runningId };
        if (Online) (ServiceRepository.GetService<INetworkingService>() as NetworkingManager)?.ExecuteInstantSingleAction(parameters);
        else ServiceRepository.GetService<IGameLocationActionService>().ExecuteInstantSingleAction(parameters);
    }

    private static void SendBatch(NetworkingManager manager, Batch batch)
    {
        var actor = Character(batch.Activities[0].Activity.Actor);
        if (actor == null || sentBatch == batch.Id) return;
        sentBatch = batch.Id;
        var parameters = new CharacterActionParams(actor, (ActionDefinitions.Id)ExtraActionId.CoordinateExploration)
            { StringParameter = JsonConvert.SerializeObject(batch) };
        if (Online) manager.ExecuteInstantSingleAction(parameters);
        else ServiceRepository.GetService<IGameLocationActionService>().ExecuteInstantSingleAction(parameters);
    }

    private static void ClearCommit()
    {
        prepared = null;
        local.Ack = null;
        local.Ready = null;
        if (Online && IsHost) PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { [CommitProperty] = null });
        Publish();
    }

    internal static bool IsApproved(CharacterActionParams parameters) => Running.ContainsKey(parameters);
    internal static bool BlockUnplanned(CharacterAction action) => Gui.Battle == null &&
        (Busy || Enabled && MultiplayerSettingsContext.AllPlayersMatch && TargetingContext.IsPlayerPartyActor(action.ActingCharacter)) &&
        GetDuration(action.ActionParams) > 0 && !IsApproved(action.ActionParams);

    internal static IEnumerator WaitBeforeSpend(CharacterActionMagicEffect action)
    {
        if (!Running.TryGetValue(action.ActionParams, out var entry)) yield break;
        if (!ValidateTargets(action.ActionParams))
        {
            action.ExecutionFailed = true;
            action.ActionParams.RulesetEffect.Terminate(false);
            yield break;
        }
        entry.Prepared = true;
        var version = generation;
        while (version == generation && running && !cancelled && !entry.Started) yield return null;
        if (!entry.Started || version != generation || cancelled)
        {
            action.ExecutionFailed = true;
            action.ActionParams.RulesetEffect.Terminate(false);
        }
    }

    internal static IEnumerator WaitForCasting(CharacterActionMagicEffect action)
    {
        if (!Running.TryGetValue(action.ActionParams, out var entry)) yield break;
        entry.Action = action;
        entry.Waiting = true;
        var version = generation;
        while (running && version == generation && !cancelled && !entry.Released && !action.Aborted) yield return null;
        if (version != generation || cancelled || action.Aborted || !ValidateTargets(action.ActionParams, false))
        {
            action.ExecutionFailed = true;
            action.ActionParams.RulesetEffect.Terminate(false);
        }
    }

    internal static IEnumerator ExecuteBatch(CharacterActionParams command)
    {
        if (command.BoolParameter)
        {
            if (runningId == command.StringParameter)
                cancellationStage = Math.Min(cancellationStage, command.IntParameter);
            yield break;
        }
        if (command.IntParameter > 0)
        {
            if (runningId == command.StringParameter && command.IntParameter <= MaxActivities + 5)
                authorizedStage = Math.Max(authorizedStage, command.IntParameter - 1);
            yield break;
        }
        var batch = Read<Batch>(command.StringParameter);
        if (batch?.Id == null || running || Executed.Contains(batch.Id) || prepared?.Id != batch.Id ||
            batch.Epoch != local.Epoch || batch.Digest != prepared.Digest ||
            JsonConvert.SerializeObject(batch.Activities) != JsonConvert.SerializeObject(prepared.Activities) ||
            Online && GetPlayers().Any(player => player.State.Ack != batch.Id)) yield break;

        Executed.Add(batch.Id);
        running = true;
        runningId = batch.Id;
        cancelled = false;
        authorizedStage = sentStage = -1;
        cancellationStage = int.MaxValue;
        local.StageBatch = null;
        var version = generation;
        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        RunningActivity[] entries = [];
        try
        {
            yield return WaitForLocal(() => ServiceRepository.GetService<IGameLocationCharacterService>().PartyCharacters
                .Concat(batch.Activities.Select(entry => Character(entry.Activity.Actor))).Where(character => character != null).Distinct()
                .All(character => !actionService.IsCharacterActing(character)), batch, version);
            if (cancelled || version != generation) yield break;
            if (!ValidatePlan(batch.Activities))
            {
                local.Failure = batch.Id;
                Publish();
                if (!Online) cancelled = true;
            }
            yield return WaitForPeers(batch, 0, version);
            if (cancelled || version != generation || local.Failure == batch.Id) yield break;
            foreach (var activity in batch.Activities)
            {
                var parameters = Build(activity.Activity, false);
                if (parameters == null) { cancelled = true; break; }
                Running.Add(parameters, new RunningActivity(activity.Activity, parameters));
            }
            entries = Running.Values.ToArray();
            if (cancelled)
            {
                local.Failure = batch.Id;
                Publish();
                yield break;
            }
            yield return WaitForPeers(batch, 1, version);
            if (cancelled || version != generation) yield break;
            // Native queues are per character. Each retains its own action lifecycle while
            // this ordered native command owns the common casting-time barrier and clock.
            foreach (var entry in entries)
            {
                actionService.ExecuteAction(entry.Parameters, action =>
                {
                    entry.Done = true;
                    entry.Failed = action.Aborted || action is CharacterActionMagicEffect { ExecutionFailed: true } || !entry.Waiting;
                }, false);
            }
            yield return WaitForLocal(() => entries.All(entry => entry.Prepared || entry.Done), batch, version);
            if (entries.Any(entry => entry.Failed))
            {
                local.Failure = batch.Id;
                Publish();
            }
            yield return WaitForPeers(batch, 2, version);
            if (cancelled || version != generation) yield break;
            foreach (var entry in entries) entry.Started = true;
            yield return WaitForLocal(() => entries.All(entry => entry.Waiting || entry.Done), batch, version);
            if (entries.Any(entry => entry.Failed))
            {
                local.Failure = batch.Id;
                Publish();
            }
            yield return WaitForPeers(batch, 3, version);
            if (cancelled || version != generation) yield break;

            var elapsed = 0;
            var stage = 3;
            foreach (var due in entries.Select(entry => entry.Reservation.Duration).Distinct().OrderBy(value => value))
            {
                if (cancelled || version != generation || Gui.Battle != null) yield break;
                yield return AdvanceInterval(due - elapsed, version);
                elapsed = due;
                if (version != generation) yield break;
                foreach (var entry in entries.Where(entry => entry.Reservation.Duration == due)) entry.Released = true;
                yield return WaitForLocal(() => entries.All(entry => !entry.Released || entry.Done), batch, version);
                if (version != generation) yield break;
                if (entries.Any(entry => entry.Failed))
                {
                    local.Failure = batch.Id;
                    Publish();
                }
                yield return WaitForPeers(batch, ++stage, version);
            }
        }
        finally
        {
            foreach (var entry in entries)
            {
                if (!entry.Done)
                {
                    actionService.StopCharacterActions(entry.Parameters.ActingCharacter, CharacterAction.InterruptionType.Invalid, true);
                    entry.Parameters.RulesetEffect?.Terminate(false);
                }
            }
            if (version == generation)
            {
                cancelled = true;
                Running.Clear();
                running = false;
                runningId = null;
                local.Activities.Clear();
                ClearCommit();
            }
        }
    }

    private static IEnumerator WaitForLocal(Func<bool> predicate, Batch batch, int version)
    {
        var deadline = Time.unscaledTime + 60;
        while (!predicate() && !cancelled && version == generation)
        {
            if (Time.unscaledTime > deadline)
            {
                local.Failure = batch.Id;
                Publish();
                if (!Online) cancelled = true;
                yield break;
            }
            yield return null;
        }
    }

    private static IEnumerator WaitForPeers(Batch batch, int stage, int version)
    {
        local.StageBatch = batch.Id;
        local.Stage = stage;
        Publish();
        if (!Online) authorizedStage = stage;
        yield return WaitForLocal(() => cancellationStage <= stage ||
            local.Failure != batch.Id && authorizedStage >= stage, batch, version);
        if (cancellationStage <= stage || local.Failure == batch.Id || authorizedStage < stage) cancelled = true;
    }

    private static IEnumerator AdvanceInterval(int seconds, int version)
    {
        var resting = ServiceRepository.GetService<IGameRestingService>();
        var clock = Gui.GameCampaign.GameTime;
        Action release = () =>
        {
            // Native camp iterators do not unsubscribe when an owner disposes them
            // during a location transition. Release only this interval's subscription.
            if (resting is GameRestingManager manager && manager.CampInProgress)
            {
                clock.RealTimeSecondTicked -= manager.UpdateCampSequence;
                manager.CampInProgress = false;
                manager.CampTerminated?.Invoke();
            }
        };
        releaseClock = release;
        try { yield return AdvanceClock(resting.ExecuteCampSequence(seconds), version); }
        finally
        {
            if (ReferenceEquals(releaseClock, release))
            {
                releaseClock = null;
                release();
            }
        }
    }

    private static IEnumerator AdvanceClock(IEnumerator sequence, int version)
    {
        try
        {
            // A requested cancellation takes effect at the next common time boundary.
            // Finishing the interval keeps every peer on the same campaign second.
            while (version == generation)
            {
                bool next;
                object current;
                advancingClock++;
                try { next = sequence.MoveNext(); current = next ? sequence.Current : null; }
                finally { advancingClock--; }
                if (!next) yield break;
                yield return current is IEnumerator nested ? AdvanceClock(nested, version) : current;
            }
        }
        finally { (sequence as IDisposable)?.Dispose(); }
    }

    private static T Read<T>(string payload) where T : class
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return null;
        try { return JsonConvert.DeserializeObject<T>(payload, JsonSettings); }
        catch (JsonException) { return null; }
    }

    private static string Digest(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }
}
