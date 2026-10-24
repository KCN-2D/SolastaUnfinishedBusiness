using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using TA;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// This fixed adapter is compiled only into the temporary runtime verification mod.
public static class GameTestSaveSmokeProbe
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Action<string> log;
    private static CaseDefinition testCase;
    private static Harmony isolation;
    private static string ownedRoot;
    private static string characterDirectory;
    private static string builtInDirectory;
    private static string temporaryCharacterDirectory;
    private static string saveDirectory;
    private static string originalSaveStamp;
    private static string cloneStamp;
    private static Dictionary<string, string> originalCharacters;
    private static string originalCharacterDirectory;
    private static string originalBuiltInDirectory;
    private static bool isolationInstalled;
    private static int blockedSaves;
    private static bool outcomeRecorded;
    private static bool scenarioPassed;
    private static bool loadRequested;
    private static float deadline;
    private static GameLocationCharacter scenarioActor;
    private static string resultActorName;
    private static string resultActualPosition;
    private static int3 sourcePosition;
    private static int3 targetPosition;
    private static IActionObservation actionObservation;
    private static IActionOperation actionOperation;
    private static bool ownMinimized;

    public interface IActionObservation : IDisposable
    {
        string Adapter { get; }
        void Configure(string casePath, Action<string> log, Action<bool, string> check, Action<string, string> recordOperation);
        void Begin(GameLocationCharacter actor);
        IEnumerator Complete();
    }

    public interface IActionOperation : IDisposable
    {
        string Adapter { get; }
        string Kind { get; }
        void Configure(string casePath, Action<string> log, Action<bool, string> check, Action<string, string> recordOperation);
        IEnumerator Execute(GameLocationCharacter actor);
    }
    [Serializable, DataContract]
    public sealed class CaseDefinition
    {
        [DataMember] public int schemaVersion;
        [DataMember] public string id;
        [DataMember] public string adapter;
        [DataMember] public InitialState initialState;
        [DataMember] public Operation operation;
        [DataMember] public Expected expected;
        [DataMember] public Observation observation;
        [DataMember] public int timeoutSeconds;
    }

    [Serializable, DataContract]
    public sealed class InitialState
    {
        [DataMember] public string savePath;
        [DataMember] public string originalSavePath;
        [DataMember] public string saveSha256;
        [DataMember] public PowerSetup setup;
    }

    [DataContract]
    public sealed class PowerSetup
    {
        [DataMember] public string kind;
        [DataMember] public string actorSource;
        [DataMember] public int actorIndex;
        [DataMember] public string powerName;
        [DataMember] public int[] positionOffset;
        [DataMember] public int expectedGuestCount;
    }

    [Serializable, DataContract]
    public sealed class Operation
    {
        [DataMember] public string kind;
        [DataMember] public int actorIndex;
        [DataMember] public string actorSource;
        [DataMember] public int[] offset;
        [DataMember] public bool manualGate;
        [DataMember] public int manualGateTimeoutSeconds;
        [DataMember] public bool verifyFocusLoss;
    }

    [Serializable, DataContract]
    public sealed class Expected
    {
        [DataMember] public int loadedPartyCount;
        [DataMember] public bool requireUserLocation;
        [DataMember] public bool requireExploration;
    }

    [Serializable, DataContract]
    public sealed class Observation
    {
        [DataMember] public bool enabled;
    }

    [DataContract]
    private sealed class PowerSetupEvidence
    {
        [DataMember] public string actor;
        [DataMember] public string power;
        [DataMember] public string resourcePower;
        [DataMember] public string targetPosition;
        [DataMember] public int remainingBefore;
        [DataMember] public int remainingAfter;
        [DataMember] public int guestsBefore;
        [DataMember] public int guestsAfter;
        [DataMember] public bool completed;
    }

    [DataContract]
    private sealed class LoadingProgress
    {
        [DataMember] public string utc;
        [DataMember] public string phase;
        [DataMember] public int frame;
        [DataMember] public float realtime;
        [DataMember] public float gameTime;
        [DataMember] public float unscaledDeltaTime;
        [DataMember] public float timeScale;
        [DataMember] public bool focused;
        [DataMember] public bool runInBackground;
        [DataMember] public int targetFrameRate;
        [DataMember] public bool visible;
        [DataMember] public bool showing;
        [DataMember] public bool hiding;
        [DataMember] public bool readyToProceed;
        [DataMember] public bool startActive;
        [DataMember] public bool startInteractable;
        [DataMember] public bool startInvoked;
        [DataMember] public bool locationReady;
        [DataMember] public bool campaignAvailable;
        [DataMember] public bool postProcessAvailable;
        [DataMember] public bool postProcessReady;
    }

    [DataContract]
    private sealed class CaptureAttempt
    {
        [DataMember] public string utc;
        [DataMember] public string target;
        [DataMember] public int attempt;
        [DataMember] public int frame;
        [DataMember] public float elapsedSeconds;
        [DataMember] public int width;
        [DataMember] public int height;
        [DataMember] public int pixels;
        [DataMember] public int nonBlackPixels;
        [DataMember] public bool accepted;
        [DataMember(EmitDefaultValue = false)] public string storedPng;
        [DataMember(EmitDefaultValue = false)] public string sha256;
        [DataMember(EmitDefaultValue = false)] public int bytes;
    }

    [Serializable, DataContract]
    private sealed class ContinueSignal
    {
        [DataMember] public bool @continue = false;
    }

    [DataContract]
    private sealed class EvidenceRecord
    {
        [DataMember] public string utc;
        [DataMember] public string kind;
        [DataMember] public string phase;
        [DataMember] public string detail;
        [DataMember] public string status;
        [DataMember] public string actor;
        [DataMember] public string from;
        [DataMember] public string to;
        [DataMember] public string actual;
        [DataMember(EmitDefaultValue = false)] public int partyCount;
        [DataMember] public int blockedSaves;
        [DataMember] public int frame;
        [DataMember(EmitDefaultValue = false)] public int width;
        [DataMember(EmitDefaultValue = false)] public int height;
        [DataMember(EmitDefaultValue = false)] public string inputDelivery;
        [DataMember(EmitDefaultValue = false)] public string verificationScope;
    }

    public sealed class BlockedConditionException : Exception
    {
        public BlockedConditionException(string message) : base(message) { }
    }

    private sealed class ExpectedResultException : Exception
    {
        internal ExpectedResultException(string message) : base(message) { }
    }

    public static IEnumerator Run(Action<string> write) => RunScenario(write, null);

    public static IEnumerator RunScenario(Action<string> write, IActionObservation observation) =>
        RunScenario(write, observation, null);

    public static IEnumerator RunScenario(Action<string> write, IActionObservation observation, IActionOperation operation)
    {
        actionObservation = observation;
        actionOperation = operation;
        log = write ?? throw new ArgumentNullException(nameof(write));
        outcomeRecorded = false;
        scenarioPassed = false;
        loadRequested = false;
        deadline = 0;
        blockedSaves = 0;
        scenarioActor = null;
        ownMinimized = false;
        resultActorName = resultActualPosition = null;
        var stack = new Stack<IEnumerator>();
        stack.Push(RunCore());
        Exception failure = null;
        while (stack.Count > 0 && failure == null)
        {
            bool moved = false;
            object current = null;
            try
            {
                if (deadline > 0 && Time.realtimeSinceStartup > deadline)
                    throw new BlockedConditionException("The case deadline expired before the native scenario completed.");
                moved = stack.Peek().MoveNext();
                if (moved) current = stack.Peek().Current;
            }
            catch (Exception error) { failure = error; }
            if (failure != null) break;
            if (!moved)
            {
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception error) { failure = error; }
                continue;
            }
            if (current is IEnumerator nested) stack.Push(nested);
            else yield return current;
        }
        while (stack.Count > 0)
        {
            try { (stack.Pop() as IDisposable)?.Dispose(); }
            catch (Exception error) { if (failure == null) failure = error; }
        }
        try { RestoreOwnedWindow(); }
        catch (Exception error) { failure = error; }
        if (scenarioActor != null)
        {
            resultActorName = scenarioActor.Name;
            resultActualPosition = Position(scenarioActor.LocationPosition);
        }
        try { actionObservation?.Dispose(); }
        catch (Exception error) { failure = error; }
        try { actionOperation?.Dispose(); }
        catch (Exception error) { failure = error; }

        if (failure != null)
        {
            var status = failure is BlockedConditionException ? "BLOCKED" : failure is ExpectedResultException ? "FAIL" : "ERROR";
            RecordOutcome(status, failure.Message);
            log((status == "BLOCKED" ? "INCOMPLETE: " : "FAIL: ") + failure);
        }

        // Keep write isolation active until native game and session teardown both finish.
        var game = ServiceRepository.GetService<IGameService>();
        var session = ServiceRepository.GetService<ISessionService>();
        if (!loadRequested && (game?.Game != null || session?.Session != null))
        {
            RecordOutcome("ERROR", "An existing game or session is outside this case's ownership and was left untouched.");
            log("FAIL: no native teardown was requested for a game or session not created by this case.");
            yield break;
        }
        try { if (game?.Game != null) game.DestroyGame(); }
        catch (Exception error) { log("FAIL: native game teardown request: " + error); }
        var teardownDeadline = Time.realtimeSinceStartup + 45f;
        while (game?.Game != null && Time.realtimeSinceStartup < teardownDeadline) yield return null;
        if (game?.Game == null)
        {
            try { if (session?.Session != null) session.DestroySession(); }
            catch (Exception error) { log("FAIL: native session teardown request: " + error); }
        }
        while (session?.Session != null && Time.realtimeSinceStartup < teardownDeadline) yield return null;
        if (game?.Game != null || session?.Session != null)
        {
            RecordOutcome("ERROR", "Native teardown did not finish; write isolation remains active until process exit.");
            log("FAIL: native teardown did not finish; isolation remains active until the owned process exits.");
            yield break;
        }
        try
        {
            if (isolationInstalled)
            {
                isolation.UnpatchAll(isolation.Id);
                isolationInstalled = false;
                ServiceRepository.GetService<ICharacterPoolService>().EnumeratePool();
                Check(TacticalAdventuresApplication.GameCharactersDirectory == originalCharacterDirectory &&
                      TacticalAdventuresApplication.GameBuiltInCharactersDirectory == originalBuiltInDirectory,
                    "native character directory getters return to their original paths");
                var finalCharacters = StampCharacters(originalCharacterDirectory, originalBuiltInDirectory);
                Check(originalCharacters.Count == finalCharacters.Count && originalCharacters.All(entry =>
                          finalCharacters.ContainsKey(entry.Key) && finalCharacters[entry.Key] == entry.Value),
                    "all ordinary character files retain bytes, timestamps and attributes");
                Check(Stamp(testCase.initialState.originalSavePath) == originalSaveStamp,
                    "the original save retains bytes, timestamp and attributes after native teardown");
                Check(Stamp(testCase.initialState.savePath) == cloneStamp,
                    "the input save copy remains unchanged after the smoke action");
                Check(Directory.GetFiles(saveDirectory, "*", SearchOption.AllDirectories).Length == 0,
                    "the isolated save output directory contains no save or autosave output");
                Check(true, "native game and session teardown release the test before removing write isolation");
            }
        }
        catch (Exception error)
        {
            failure = error;
            RecordOutcome("ERROR", "Native test cleanup failed: " + error.Message);
            log("FAIL: native test cleanup: " + error);
        }
        if (failure == null && scenarioPassed)
            RecordOutcome("PASS", actionOperation == null
                ? "UB initialized, the isolated save loaded, one existing native move completed at the expected cell, and native test state was released."
                : "UB initialized, the isolated save loaded, the declared native operation completed, and native test state was released.");
        else if (!outcomeRecorded) RecordOutcome("ERROR", "The native scenario ended without a result.");
        RecordOperation("teardown", "Native game/session ended, write guards removed and original input stamps verified.");
    }

    private static IEnumerator RunCore()
    {
        ReadCase();
        deadline = Time.realtimeSinceStartup + testCase.timeoutSeconds;
        Check(Gui.Game == null && ServiceRepository.GetService<ISessionService>().Session == null,
            "the native smoke starts outside an existing game and session");
        Check(Hash(testCase.initialState.originalSavePath).Equals(testCase.initialState.saveSha256, StringComparison.OrdinalIgnoreCase) &&
              Hash(testCase.initialState.savePath).Equals(testCase.initialState.saveSha256, StringComparison.OrdinalIgnoreCase),
            "the original save and its isolated input copy match the declared SHA-256");
        originalSaveStamp = Stamp(testCase.initialState.originalSavePath);
        cloneStamp = Stamp(testCase.initialState.savePath);
        originalCharacterDirectory = TacticalAdventuresApplication.GameCharactersDirectory;
        originalBuiltInDirectory = TacticalAdventuresApplication.GameBuiltInCharactersDirectory;
        originalCharacters = StampCharacters(originalCharacterDirectory, originalBuiltInDirectory);
        InstallIsolation();
        yield return DismissStartupMessage();
        RecordOperation("load", "IGameSerializationService.LoadGameAsync loads only the declared input copy.");
        loadRequested = true;
        yield return ServiceRepository.GetService<IGameSerializationService>().LoadGameAsync(testCase.initialState.savePath);
        yield return WaitForLoadedMap();
        yield return DismissTutorial();
        var location = ServiceRepository.GetService<IGameLocationService>();
        var actors = ServiceRepository.GetService<IGameLocationCharacterService>();
        LogActors("party", actors.PartyCharacters);
        LogActors("guest", actors.GuestCharacters);
        if (testCase.expected.loadedPartyCount > 0)
            Check(actors.PartyCharacters.Count == testCase.expected.loadedPartyCount,
                "loaded party count matches the case: " + actors.PartyCharacters.Count);
        if (testCase.expected.requireUserLocation)
            Check(location.GameLocation.LocationDefinition.IsUserLocation,
                "the copied save restores an existing user location");
        if (testCase.expected.requireExploration)
            Check(ServiceRepository.GetService<IGameLocationBattleService>().Battle == null,
                "the copied save restores exploration without a battle");
        if (testCase.initialState.setup != null) yield return RunPowerSetup(testCase.initialState.setup);
        var actorSource = testCase.operation.actorSource == "guest" ? actors.GuestCharacters : actors.PartyCharacters;
        if (testCase.operation.actorIndex < 0 || testCase.operation.actorIndex >= actorSource.Count)
            throw new BlockedConditionException("The configured " + testCase.operation.actorSource + " actor index is absent from the loaded save.");
        scenarioActor = actorSource[testCase.operation.actorIndex];
        var selection = ServiceRepository.GetService<IGameLocationSelectionService>();
        selection.SelectCharacter(scenarioActor, true, false);
        yield return SettleFrames(8);
        sourcePosition = scenarioActor.LocationPosition;
        targetPosition = actionOperation == null
            ? sourcePosition + new int3(testCase.operation.offset[0], testCase.operation.offset[1], testCase.operation.offset[2])
            : sourcePosition;
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        if (actionOperation == null && !positioning.CanPlaceCharacter(scenarioActor, targetPosition, CellHelpers.PlacementMode.PlanningStation))
            throw new BlockedConditionException("The configured destination is not a legal native placement cell: " + targetPosition);
        Check(selection.SelectedCharacters.Contains(scenarioActor), "the native selection service selects the configured actor");
        yield return Capture("GameTest-Ready.png");
        WriteJson("GameTest-Ready.json", new EvidenceRecord
        {
            kind = "manual-gate", phase = "ready", status = "READY", utc = DateTime.UtcNow.ToString("O"),
            detail = "The copied save is loaded. Release the optional manual gate with continue.json; native screen and input checks have separate evidence.",
            actor = scenarioActor.Name, from = Position(sourcePosition), to = Position(targetPosition),
            partyCount = actors.PartyCharacters.Count, frame = Time.frameCount, width = Screen.width, height = Screen.height
        });
        if (testCase.operation.manualGate)
        {
            RecordOperation("manual-gate", "Waiting for the fixed continue.json {\"continue\":true} continuation flag.");
            var gateDeadline = Mathf.Min(deadline, Time.realtimeSinceStartup + testCase.operation.manualGateTimeoutSeconds);
            var signalPath = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "continue.json");
            while (!ContinueWasRequested(signalPath) && Time.realtimeSinceStartup < gateDeadline) yield return null;
            if (!ContinueWasRequested(signalPath))
                throw new BlockedConditionException("The manual gate was not released before its timeout.");
            RecordOperation("manual-continued", "The fixed continuation flag released the manual gate.");
        }
        yield return DismissTutorial();
        Check(scenarioActor.LocationPosition == sourcePosition,
            "the actor remains at the initial cell before the configured action");
        yield return Capture("GameTest-BeforeAction.png");
        actionObservation?.Begin(scenarioActor);
        try
        {
            if (testCase.operation.verifyFocusLoss)
            {
                ownMinimized = true;
                UBVerification.DesktopReader.PrepareCurrentWindowFocusLoss(Screen.width, Screen.height);
                yield return null;
                if (!Application.isFocused)
                    throw new BlockedConditionException("The owned render window did not report focus before the focus-loss check.");
                RecordOperation("focus-ready", "Windows activated the owned render window on the non-input desktop; Application.isFocused is true.");
                var focusStartFrame = Time.frameCount;
                if (!UBVerification.DesktopReader.MinimizeCurrentWindow(Screen.width, Screen.height))
                    throw new BlockedConditionException("The owned game window was already minimized before the focus-loss check.");
                RecordOperation("window-minimize", "SW_MINIMIZE lets Windows deactivate the owned render window in favor of its non-input desktop peer.");
                var focusDeadline = Mathf.Min(deadline, Time.realtimeSinceStartup + 3f);
                while ((Application.isFocused || !RuntimeVerificationHarness.ObservedFocusLossSince(focusStartFrame)) &&
                    Time.realtimeSinceStartup < focusDeadline) yield return null;
                if (Application.isFocused || !RuntimeVerificationHarness.ObservedFocusLossSince(focusStartFrame))
                    throw new BlockedConditionException("The owned game window did not report a real focus-loss callback and Application.isFocused=false within three seconds.");
                UBVerification.DesktopReader.ShowCurrentWindowWithoutActivation();
                yield return null;
                if (Application.isFocused || !UBVerification.DesktopReader.CurrentWindowIsDeactivated(Screen.width, Screen.height))
                    throw new BlockedConditionException("The visible render window did not remain deactivated after its non-activating display restore.");
                RecordOperation("focus-loss", "The actual focus callback and main-thread Application.isFocused value are false before the existing native action.");
            }
            if (actionOperation != null)
            {
                RecordOperation("action", "Declared native operation=" + actionOperation.Kind + "; actor=" + scenarioActor.Name);
                yield return actionOperation.Execute(scenarioActor);
                RecordOperation("action-completed", "The declared native operation completed.");
            }
            else
            {
                RecordOperation("action", "ICommandService.MoveCharacter actor=" + scenarioActor.Name + " from=" + Position(sourcePosition) + " to=" + Position(targetPosition));
                bool completed = false;
                bool aborted = true;
                ServiceRepository.GetService<ICommandService>().MoveCharacter(scenarioActor, targetPosition, scenarioActor.Orientation,
                    0f, ActionDefinitions.MoveStance.Run, wasAborted => { aborted = wasAborted; completed = true; }, false, true);
                var actionDeadline = Mathf.Min(deadline, Time.realtimeSinceStartup + 45f);
                var actions = ServiceRepository.GetService<IGameLocationActionService>();
                while ((!completed || actions.IsCharacterActing(scenarioActor)) && Time.realtimeSinceStartup < actionDeadline) yield return null;
                Check(completed && !actions.IsCharacterActing(scenarioActor), "the existing native move action reaches its completion callback");
                Check(!aborted, "the existing native move action completes without aborting");
                Check(scenarioActor.LocationPosition == targetPosition,
                    "the existing native move reaches the expected cell: actual=" + Position(scenarioActor.LocationPosition));
                RecordOperation("action-completed", "The existing native move completed at its expected cell.");
            }
        }
        finally { RestoreOwnedWindow(); }
        if (testCase.operation.verifyFocusLoss) yield return SettleFrames(6);
        if (actionObservation != null) yield return actionObservation.Complete();
        yield return SettleFrames(12);
        yield return Capture("GameTest-AfterAction.png");
        Check(Stamp(testCase.initialState.originalSavePath) == originalSaveStamp,
            "the original save is unchanged after the native action");
        scenarioPassed = true;
    }

    private static void ReadCase()
    {
        deadline = 0;
        var casePath = Environment.GetEnvironmentVariable("UB_GAME_TEST_CASE_PATH");
        if (string.IsNullOrEmpty(casePath) || !Path.IsPathRooted(casePath) || !File.Exists(casePath))
            throw new InvalidOperationException("UB_GAME_TEST_CASE_PATH must identify the archived case JSON file.");
        using (var input = File.OpenRead(casePath))
            testCase = (CaseDefinition)new DataContractJsonSerializer(typeof(CaseDefinition)).ReadObject(input);
        log("INFO: case parser=DataContractJsonSerializer; sha256=" + Hash(casePath) + "; id=" + testCase?.id +
            "; schema=" + testCase?.schemaVersion + "; operation=" + testCase?.operation?.kind +
            "; actorSource=" + testCase?.operation?.actorSource + "; actorIndex=" + testCase?.operation?.actorIndex +
            "; offset=" + (testCase?.operation?.offset == null ? "<null>" : string.Join(",", testCase.operation.offset)) +
            "; party=" + testCase?.expected?.loadedPartyCount + "; observation=" + testCase?.observation?.enabled);
        if (actionObservation != null && actionOperation != null && actionObservation.Adapter != actionOperation.Adapter)
            throw new InvalidOperationException("The operation and observation adapters must match.");
        if (testCase == null || testCase.schemaVersion != 1 || testCase.adapter != (actionOperation?.Adapter ?? actionObservation?.Adapter ?? "save-smoke") ||
            testCase.initialState == null || testCase.operation == null || testCase.expected == null ||
            testCase.operation.kind != (actionOperation?.Kind ?? "move") ||
            actionOperation == null && (testCase.operation.offset == null || testCase.operation.offset.Length != 3 ||
                testCase.operation.offset.All(value => value == 0) || testCase.operation.offset.Any(value => Math.Abs((long)value) > 3)) ||
            testCase.timeoutSeconds < 30 || testCase.timeoutSeconds > 1800 ||
            testCase.operation.manualGate && (testCase.operation.manualGateTimeoutSeconds < 1 || testCase.operation.manualGateTimeoutSeconds > testCase.timeoutSeconds))
            throw new InvalidOperationException("The archived case does not satisfy the fixed save-smoke schema.");
        if (string.IsNullOrEmpty(testCase.operation.actorSource)) testCase.operation.actorSource = "party";
        if (testCase.operation.actorSource != "party" && testCase.operation.actorSource != "guest")
            throw new InvalidOperationException("actorSource must be party or guest.");
        var setup = testCase.initialState.setup;
        if (setup != null)
        {
            if (string.IsNullOrEmpty(setup.actorSource)) setup.actorSource = "party";
            if (setup.kind != "use-power" || setup.actorSource != "party" && setup.actorSource != "guest" || setup.actorIndex < 0 ||
                string.IsNullOrWhiteSpace(setup.powerName) || setup.powerName.Length > 160 || setup.positionOffset == null ||
                setup.positionOffset.Length != 3 || setup.positionOffset.All(value => value == 0) ||
                setup.positionOffset.Any(value => Math.Abs((long)value) > 3) || setup.expectedGuestCount < 0 || setup.expectedGuestCount > 32)
                throw new InvalidOperationException("The finite initial use-power setup schema is invalid.");
        }
        if (testCase.observation == null || testCase.observation.enabled != (actionObservation != null))
            throw new InvalidOperationException("The archived observation selection does not match the fixed adapter.");
        actionObservation?.Configure(casePath, log, Check, RecordOperation);
        actionOperation?.Configure(casePath, log, Check, RecordOperation);
        var caseRoot = Path.GetDirectoryName(Path.GetFullPath(casePath));
        testCase.initialState.savePath = Path.GetFullPath(testCase.initialState.savePath);
        testCase.initialState.originalSavePath = Path.GetFullPath(testCase.initialState.originalSavePath);
        if (!Within(testCase.initialState.savePath, caseRoot) ||
            testCase.initialState.savePath.Equals(testCase.initialState.originalSavePath, StringComparison.OrdinalIgnoreCase) ||
            !testCase.initialState.savePath.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(testCase.initialState.originalSavePath) || !File.Exists(testCase.initialState.savePath))
            throw new InvalidOperationException("The save input must be an existing owned copy distinct from the original save.");
        ownedRoot = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "OwnedSaveSmoke");
        characterDirectory = Path.Combine(ownedRoot, "Characters");
        builtInDirectory = Path.Combine(ownedRoot, "BuiltInCharacters");
        temporaryCharacterDirectory = Path.Combine(ownedRoot, "TemporaryCharacters");
        saveDirectory = Path.Combine(ownedRoot, "SaveOutput");
        foreach (var directory in new[] { characterDirectory, builtInDirectory, temporaryCharacterDirectory, saveDirectory })
            Directory.CreateDirectory(directory);
        RecordOperation("case", "Fixed adapter=" + testCase.adapter + "; case=" + testCase.id);
    }

    private static void InstallIsolation()
    {
        isolation = new Harmony("UBRuntimeVerification.FixedSaveSmoke");
        isolationInstalled = true;
        Patch(AccessTools.PropertyGetter(typeof(TacticalAdventuresApplication), "GameCharactersDirectory"), nameof(CharacterPath), false);
        Patch(AccessTools.PropertyGetter(typeof(TacticalAdventuresApplication), "GameBuiltInCharactersDirectory"), nameof(BuiltInPath), false);
        Patch(AccessTools.PropertyGetter(typeof(TacticalAdventuresApplication), "GameCharactersTemporaryDirectory"), nameof(TemporaryPath), false);
        Patch(AccessTools.PropertyGetter(typeof(TacticalAdventuresApplication), "SaveGameDirectory"), nameof(SavePath), false);
        Patch(AccessTools.PropertyGetter(typeof(TacticalAdventuresApplication), "LostIronManSaveGameDirectory"), nameof(SavePath), false);
        Patch(AccessTools.Method(typeof(GameSerializationManager), "SaveGame", new[] { typeof(string), typeof(GameSerializationDefinitions.SaveType) }), nameof(PreventSave), true);
        Patch(AccessTools.PropertyGetter(typeof(GameSerializationManager), "WritingData"), nameof(PreventCloudWrite), true);
        Patch(AccessTools.PropertyGetter(typeof(GameSerializationManager), "DeletingData"), nameof(PreventCloudDelete), true);
        Patch(AccessTools.PropertyGetter(typeof(CharacterPoolManager), "WritingData"), nameof(PreventCloudWrite), true);
        Patch(AccessTools.PropertyGetter(typeof(CharacterPoolManager), "DeletingData"), nameof(PreventCloudDelete), true);
        Patch(AccessTools.Method(typeof(CharacterPoolManager), "SaveCharacter", new[] { typeof(RulesetCharacterHero), typeof(bool), typeof(bool) }), nameof(PreventCharacterSave), true);
        Patch(AccessTools.Method(typeof(CharacterPoolManager), "DeleteCharacter", new[] { typeof(string) }), nameof(PreventCharacterDelete), true);
        Patch(AccessTools.Method(typeof(TacticalAdventuresApplication), "ForceRecordUserPreferences", Type.EmptyTypes), nameof(PreventEvent), true);
        Patch(AccessTools.Method(typeof(NewAdventurePanel), "SendNewAdventureEventAndCheckAchievements", new[] { typeof(bool) }), nameof(PreventEvent), true);
        Patch(AccessTools.Method(typeof(CharacterEditionScreen), "SendCharacterEvent", new[] { typeof(RulesetCharacterHero) }), nameof(PreventEvent), true);
        Patch(AccessTools.Method(typeof(CharacterEditionScreen), "UnlockAchievementsIfRelevant", new[] { typeof(RulesetCharacterHero) }), nameof(PreventEvent), true);
        Check(TacticalAdventuresApplication.GameCharactersDirectory == characterDirectory &&
              TacticalAdventuresApplication.GameBuiltInCharactersDirectory == builtInDirectory &&
              TacticalAdventuresApplication.SaveGameDirectory == saveDirectory,
            "native save and character directories are isolated before loading the save copy");
    }

    private static void Patch(MethodBase method, string callback, bool prefix)
    {
        if (method == null) throw new MissingMethodException("Native isolation boundary was not resolved: " + callback);
        var patch = new HarmonyMethod(typeof(GameTestSaveSmokeProbe).GetMethod(callback, Flags))
            { priority = prefix ? Priority.First : Priority.Last };
        isolation.Patch(method, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
        log("INFO: resolved native isolation method " + method.DeclaringType.FullName + "." + method.Name);
    }

    private static void CharacterPath(ref string __result) => __result = characterDirectory;
    private static void BuiltInPath(ref string __result) => __result = builtInDirectory;
    private static void TemporaryPath(ref string __result) => __result = temporaryCharacterDirectory;
    private static void SavePath(ref string __result) => __result = saveDirectory;
    private static bool PreventSave() { blockedSaves++; return false; }
    private static bool PreventCharacterSave(ref string __result) { __result = null; blockedSaves++; return false; }
    private static bool PreventCharacterDelete() { blockedSaves++; return false; }
    private static bool PreventCloudWrite(ref CloudDataDefinitions.WritingDataHandler __result) { __result = null; return false; }
    private static bool PreventCloudDelete(ref CloudDataDefinitions.DeletingDataHandler __result) { __result = null; return false; }
    private static bool PreventEvent() => false;

    private static IEnumerator RunPowerSetup(PowerSetup setup)
    {
        if (ServiceRepository.GetService<IGameLocationBattleService>().Battle != null)
            throw new BlockedConditionException("Initial use-power setup requires exploration.");
        var actors = ServiceRepository.GetService<IGameLocationCharacterService>();
        var candidates = setup.actorSource == "guest" ? actors.GuestCharacters : actors.PartyCharacters;
        if (setup.actorIndex >= candidates.Count)
            throw new BlockedConditionException("The initial setup actor is absent from the copied save.");
        var actor = candidates[setup.actorIndex];
        var character = actor.RulesetCharacter;
        log("INFO: owned power definitions actor=" + actor.Name + "; count=" + character.UsablePowers.Count + "; " +
            string.Join("|", character.UsablePowers.Where(usable => usable.PowerDefinition != null).Select(usable => usable.PowerDefinition.Name)));
        log("INFO: owned summon power catalog actor=" + actor.Name + "; " + string.Join("|", character.UsablePowers
            .Where(usable => usable.PowerDefinition?.EffectDescription?.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Summon) == true)
            .Select(usable => usable.PowerDefinition.Name + "/remaining=" + character.GetRemainingUsesOfPower(usable))));
        var usablePower = character.UsablePowers.SingleOrDefault(usable => usable.PowerDefinition?.Name == setup.powerName);
        if (usablePower == null)
            throw new BlockedConditionException("The initial setup power is not already owned: " + setup.powerName);
        var power = usablePower.PowerDefinition;
        if (power.ActivationTime != RuleDefinitions.ActivationTime.Action)
            throw new BlockedConditionException("Initial use-power setup supports an owned main-action power.");
        var resource = usablePower;
        if (power.GetType().FullName == "FeatureDefinitionPowerSharedPool")
        {
            var poolGetter = power.GetType().GetMethod("GetUsagePoolPower", Flags, null, Type.EmptyTypes, null);
            if (poolGetter == null) throw new MissingMethodException("The known shared pool getter was not resolved.");
            var pool = (FeatureDefinitionPower)poolGetter.Invoke(power, null);
            resource = character.UsablePowers.SingleOrDefault(usable => usable.PowerDefinition == pool);
            if (resource == null) throw new BlockedConditionException("The setup power's resource pool is not already owned.");
        }
        var remainingBefore = character.GetRemainingUsesOfPower(resource);
        if (remainingBefore < Math.Max(1, power.CostPerUse))
            throw new BlockedConditionException("The owned setup power has insufficient remaining uses: " + setup.powerName);
        var validators = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "SolastaUnfinishedBusiness")
            .GetType("SolastaUnfinishedBusiness.Api.GameExtensions.RulesetCharacterExtensions", true);
        var canUse = validators.GetMethod("CanUsePower", Flags, null,
            new[] { typeof(RulesetCharacter), typeof(FeatureDefinitionPower), typeof(bool), typeof(bool) }, null);
        if (canUse == null) throw new MissingMethodException("The existing owned-power validator was not resolved.");
        if (!(bool)canUse.Invoke(null, new object[] { character, power, true, true }))
            throw new BlockedConditionException("The owned setup power is unavailable under its existing validators: " + setup.powerName);
        ServiceRepository.GetService<IGameLocationSelectionService>().SelectCharacter(actor, true, false);
        yield return DismissTutorial();
        var effect = ServiceRepository.GetService<IRulesetImplementationService>().InstantiateEffectPower(character, usablePower, false);
        var description = effect.EffectDescription;
        var summons = description.EffectForms.Where(form => form.FormType == EffectForm.EffectFormType.Summon).ToArray();
        if (description.TargetType != RuleDefinitions.TargetType.Position || description.RangeType != RuleDefinitions.RangeType.Distance || summons.Length != 1)
            throw new BlockedConditionException("Initial use-power setup supports one positional summon form.");
        var target = actor.LocationPosition + new int3(setup.positionOffset[0], setup.positionOffset[1], setup.positionOffset[2]);
        var monster = DatabaseRepository.GetDatabase<MonsterDefinition>().GetElement(summons[0].SummonForm.MonsterDefinitionName);
        if (monster == null) throw new MissingMemberException("The owned summon form's monster definition was not resolved.");
        var size = new RulesetActor.SizeParameters { minExtent = monster.SizeDefinition.MinExtent, maxExtent = monster.SizeDefinition.MaxExtent };
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var dummy = actors.DummyCharacter;
        var originalSize = dummy.RulesetCharacter.SizeParams;
        var legal = false;
        try
        {
            dummy.RulesetCharacter.SizeParams = size;
            legal = int3.Distance(actor.LocationPosition, target) <= description.RangeParameter &&
                ServiceRepository.GetService<IGameLocationVisibilityService>().IsCellPerceivedByCharacter(target, actor) &&
                positioning.CanPlaceCharacter_SpecificPerf(dummy, size, target, CellHelpers.PlacementMode.Spawn, null, null) &&
                positioning.CanCharacterStayAtPosition_Floor(dummy, target, false, false, false);
        }
        finally { dummy.RulesetCharacter.SizeParams = originalSize; }
        if (!legal) throw new BlockedConditionException("The configured setup summon cell is not legal under native range, visibility, placement and floor checks: " + target);
        var evidence = new PowerSetupEvidence
        {
            actor = actor.Name, power = power.Name, resourcePower = resource.PowerDefinition.Name,
            targetPosition = Position(target), remainingBefore = remainingBefore, guestsBefore = actors.GuestCharacters.Count
        };
        WriteJson("GameTest-Setup.json", evidence);
        RecordOperation("setup-use-power", "Native owned power=" + power.Name + "; actor=" + actor.Name + "; target=" + Position(target) +
            "; remaining=" + remainingBefore + "; expectedGuests=" + setup.expectedGuestCount);
        var parameters = new CharacterActionParams(actor, ActionDefinitions.Id.PowerMain) { RulesetEffect = effect, UsablePower = usablePower };
        parameters.Positions.Add(target);
        CharacterAction completed = null;
        ServiceRepository.GetService<ICommandService>().ExecuteAction(parameters.Clone(), action => completed = action, false);
        var until = Mathf.Min(deadline, Time.realtimeSinceStartup + 60f);
        var actions = ServiceRepository.GetService<IGameLocationActionService>();
        while ((completed == null || actions.IsCharacterActing(actor)) && Time.realtimeSinceStartup < until) yield return null;
        evidence.completed = completed != null && !actions.IsCharacterActing(actor);
        evidence.remainingAfter = character.GetRemainingUsesOfPower(resource);
        evidence.guestsAfter = actors.GuestCharacters.Count;
        WriteJson("GameTest-Setup.json", evidence);
        Check(evidence.completed, "the initial owned power completes through the native action service");
        var magic = completed as CharacterActionMagicEffect;
        var failedGetter = typeof(CharacterActionMagicEffect).GetProperty("ExecutionFailed", Flags);
        if (failedGetter == null) throw new MissingMemberException("The known native power execution outcome getter was not resolved.");
        Check(!completed.Aborted && magic != null && !magic.Countered && !(bool)failedGetter.GetValue(magic),
            "the native setup power completes without abort, counter or execution failure");
        Check(evidence.remainingAfter < evidence.remainingBefore,
            "the native setup power consumes its existing resource without a recharge: before=" + evidence.remainingBefore + "; after=" + evidence.remainingAfter);
        Check(evidence.guestsAfter == setup.expectedGuestCount,
            "the native setup leaves the expected guest count: " + evidence.guestsAfter);
        yield return DismissTutorial();
        yield return SettleFrames(8);
        LogActors("guest-after-setup", actors.GuestCharacters);
        yield return Capture("GameTest-AfterSetup.png");
    }

    private static IEnumerator WaitForLoadedMap()
    {
        var loading = Gui.GuiService.GetScreen<LoadingModal>();
        var end = Mathf.Min(deadline, Time.realtimeSinceStartup + 300f);
        var proceeded = false;
        var nextProgress = 0f;
        RecordOperation("load-map-wait", "Waiting for the native map readiness and loading-screen Start callback.");
        while (Time.realtimeSinceStartup < end)
        {
            if (Time.realtimeSinceStartup >= nextProgress)
            {
                RecordLoadingProgress(loading, "waiting-map", proceeded);
                nextProgress = Time.realtimeSinceStartup + 1f;
            }
            if (ServiceRepository.GetService<IGameLocationService>()?.LocationIsReady == true && Gui.GameCampaign != null) break;
            if (!proceeded && loading.Visible && !loading.Showing && loading.ReadyToProceed)
            {
                var start = (Button)typeof(LoadingModal).GetField("startButton", Flags).GetValue(loading);
                if (start && start.gameObject.activeInHierarchy && start.IsInteractable())
                {
                    yield return Click(start, "native loading-screen Start");
                    proceeded = true;
                }
            }
            yield return null;
        }
        RecordLoadingProgress(loading, "map-wait-ended", proceeded);
        if (ServiceRepository.GetService<IGameLocationService>()?.LocationIsReady != true || Gui.GameCampaign == null)
            throw new BlockedConditionException("The native save load did not reach a ready map before timeout.");
        Check(true, "the native serializer loads the isolated save into a ready real map");
        var uiDeadline = Mathf.Min(deadline, Time.realtimeSinceStartup + 20f);
        RecordOperation("load-ui-exit", "Waiting for the native loading modal to finish hiding.");
        while ((loading.Visible || loading.Showing || loading.Hiding) && Time.realtimeSinceStartup < uiDeadline)
        {
            if (Time.realtimeSinceStartup >= nextProgress)
            {
                RecordLoadingProgress(loading, "waiting-ui-hide", proceeded);
                nextProgress = Time.realtimeSinceStartup + 1f;
            }
            yield return null;
        }
        RecordLoadingProgress(loading, "ui-wait-ended", proceeded);
        if (loading.Visible || loading.Showing || loading.Hiding)
            throw new BlockedConditionException("The native loading modal did not finish leaving the map before timeout.");
        Check(true, "the native loading modal has completed its visible, showing and hiding lifecycle before the ready image");
        yield return SettleFrames(6);
    }

    private static void RecordLoadingProgress(LoadingModal loading, string phase, bool proceeded)
    {
        var start = (Button)typeof(LoadingModal).GetField("startButton", Flags).GetValue(loading);
        var postProcess = ServiceRepository.GetService<IGraphicsLocationPostProcessService>();
        var entry = new LoadingProgress
        {
            utc = DateTime.UtcNow.ToString("O"), phase = phase, frame = Time.frameCount,
            realtime = Time.realtimeSinceStartup, gameTime = Time.time, unscaledDeltaTime = Time.unscaledDeltaTime,
            timeScale = Time.timeScale, focused = Application.isFocused, runInBackground = Application.runInBackground,
            targetFrameRate = Application.targetFrameRate, visible = loading.Visible, showing = loading.Showing,
            hiding = loading.Hiding, readyToProceed = loading.ReadyToProceed,
            startActive = start && start.gameObject.activeInHierarchy,
            startInteractable = start && start.IsInteractable(), startInvoked = proceeded,
            locationReady = ServiceRepository.GetService<IGameLocationService>()?.LocationIsReady == true,
            campaignAvailable = Gui.GameCampaign != null,
            postProcessAvailable = postProcess != null, postProcessReady = postProcess?.IsReady == true
        };
        File.AppendAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "GameTest-Loading.jsonl"),
            SerializeJson(entry) + Environment.NewLine);
    }

    private static void LogActors(string source, IList<GameLocationCharacter> actors)
    {
        log("INFO: actor catalog source=" + source + "; count=" + actors.Count);
        for (var index = 0; index < actors.Count; index++)
        {
            var actor = actors[index];
            var monster = actor.RulesetCharacter as RulesetCharacterMonster;
            log("INFO: actor source=" + source + "; index=" + index + "; name=" + actor.Name +
                "; definition=" + (monster?.MonsterDefinition?.Name ?? actor.RulesetCharacter?.GetType().Name) +
                "; position=" + Position(actor.LocationPosition));
        }
    }

    private static IEnumerator DismissStartupMessage()
    {
        var modal = Gui.GuiService.GetScreen<MessageModal>();
        if (!modal.Visible) yield break;
        var end = Time.realtimeSinceStartup + 15f;
        while (modal.Showing && Time.realtimeSinceStartup < end) yield return null;
        var dismiss = modal.GetComponentsInChildren<Button>(false).FirstOrDefault(button =>
            button.gameObject.activeInHierarchy && button.IsInteractable() &&
            Enumerable.Range(0, button.onClick.GetPersistentEventCount()).Any(index =>
                button.onClick.GetPersistentMethodName(index) == "OnValidateCb" || button.onClick.GetPersistentMethodName(index) == "OnCancelCb"));
        if (!dismiss) throw new BlockedConditionException("The startup message has no known native dismissal button.");
        yield return Click(dismiss, "native startup message dismissal");
        while (modal.Visible && Time.realtimeSinceStartup < end) yield return null;
        if (modal.Visible) throw new BlockedConditionException("The native startup message did not close.");
    }

    private static IEnumerator DismissTutorial()
    {
        var modal = UnityEngine.Resources.FindObjectsOfTypeAll<TutorialModal>().SingleOrDefault(panel =>
            panel && panel.isActiveAndEnabled && (panel.Visible || panel.Showing || panel.Hiding));
        if (!modal) yield break;
        var end = Time.realtimeSinceStartup + 20f;
        while (modal && (modal.Visible || modal.Showing || modal.Hiding) && Time.realtimeSinceStartup < end)
        {
            if (modal.Showing || modal.Hiding || typeof(TutorialModal).GetField("transitionCoroutine", Flags).GetValue(modal) != null)
            { yield return null; continue; }
            var understood = (Button)typeof(TutorialModal).GetField("understoodButton", Flags).GetValue(modal);
            var next = (Button)typeof(TutorialModal).GetField("nextStepButton", Flags).GetValue(modal);
            var button = understood && understood.gameObject.activeInHierarchy ? understood : next;
            if (!button || !button.gameObject.activeInHierarchy || !button.IsInteractable())
            { yield return null; continue; }
            yield return Click(button, button == understood ? "native tutorial Understood" : "native tutorial Next");
        }
        if (modal && (modal.Visible || modal.Showing || modal.Hiding))
            throw new BlockedConditionException("The native tutorial did not finish before timeout.");
    }

    private static IEnumerator Click(Button button, string label)
    {
        var rect = button.GetComponent<RectTransform>();
        var canvas = button.GetComponentInParent<Canvas>();
        var point = RectTransformUtility.WorldToScreenPoint(canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null, rect.TransformPoint(rect.rect.center));
        if (!EventSystem.current) throw new BlockedConditionException("Native UI has no EventSystem for " + label);
        var pointer = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        Check(point.x >= 0 && point.y >= 0 && point.x <= Screen.width && point.y <= Screen.height &&
              hits.Count > 0 && (hits[0].gameObject == button.gameObject || hits[0].gameObject.transform.IsChildOf(button.transform)),
            label + " receives the top native EventSystem raycast");
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        RecordOperation("native-ui", label);
        yield return SettleFrames(4);
    }

    private static IEnumerator Capture(string name)
    {
        var started = Time.realtimeSinceStartup;
        var captureDeadline = Mathf.Min(deadline, started + 5f);
        var attempt = 0;
        while (Time.realtimeSinceStartup < captureDeadline)
        {
            yield return new WaitForEndOfFrame();
            if (Time.realtimeSinceStartup >= captureDeadline) break;
            var image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            var evidence = new CaptureAttempt
            {
                utc = DateTime.UtcNow.ToString("O"), target = name, attempt = ++attempt,
                frame = Time.frameCount, elapsedSeconds = Time.realtimeSinceStartup - started,
                width = Screen.width, height = Screen.height
            };
            try
            {
                image.ReadPixels(new Rect(0, 0, evidence.width, evidence.height), 0, 0);
                image.Apply();
                var pixels = image.GetPixels32();
                evidence.pixels = pixels.Length;
                foreach (var pixel in pixels)
                    if (pixel.r != 0 || pixel.g != 0 || pixel.b != 0) evidence.nonBlackPixels++;
                evidence.accepted = evidence.nonBlackPixels > 0;
                if (evidence.accepted || attempt == 1)
                {
                    evidence.storedPng = evidence.accepted ? name : Path.GetFileNameWithoutExtension(name) + "-RejectedBlack.png";
                    var path = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, evidence.storedPng);
                    var bytes = image.EncodeToPNG();
                    File.WriteAllBytes(path, bytes);
                    evidence.bytes = bytes.Length;
                    evidence.sha256 = Hash(path);
                }
            }
            finally { UnityEngine.Object.Destroy(image); }
            File.AppendAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "GameTest-Captures.jsonl"),
                SerializeJson(evidence) + Environment.NewLine);
            if (!evidence.accepted) continue;
            Check(evidence.bytes > 3000, "native framebuffer evidence saved: " + name + "; attempts=" + attempt);
            yield break;
        }
        throw new BlockedConditionException("The native framebuffer remained fully black before the capture deadline: " + name +
            "; attempts=" + attempt + "; maximumWaitSeconds=5.");
    }

    private static IEnumerator SettleFrames(int count)
    {
        for (var index = 0; index < count; index++) yield return null;
    }

    private static bool ContinueWasRequested(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using (var input = File.OpenRead(path))
                return ((ContinueSignal)new DataContractJsonSerializer(typeof(ContinueSignal)).ReadObject(input))?.@continue == true;
        }
        catch (IOException) { return false; }
        catch (SerializationException) { return false; }
    }

    private static void RecordOperation(string phase, string detail)
    {
        RuntimeVerificationHarness.RecordExecution(phase);
        var entry = new EvidenceRecord
        {
            kind = "operation", phase = phase, detail = detail, utc = DateTime.UtcNow.ToString("O"), frame = Time.frameCount,
            inputDelivery = phase == "native-ui" ? "UnityEngine.EventSystems.ExecuteEvents" :
                phase == "focus-ready" || phase == "window-minimize" || phase == "window-restore" ? "Win32.ShowWindow(game-owned)" : null
        };
        File.AppendAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "GameTest-Operations.jsonl"), SerializeJson(entry) + Environment.NewLine);
        log("INFO: " + phase + ": " + detail);
    }

    private static void RecordOutcome(string status, string reason)
    {
        outcomeRecorded = true;
        WriteJson("GameTest-Smoke.json", new EvidenceRecord
        {
            kind = "result", status = status, detail = reason, utc = DateTime.UtcNow.ToString("O"),
            actor = resultActorName, from = Position(sourcePosition), to = Position(targetPosition),
            actual = resultActualPosition, blockedSaves = blockedSaves, frame = Time.frameCount,
            verificationScope = testCase?.operation?.verifyFocusLoss == true ?
                "native-command-and-eventsystem; game-owned-window-state; host-input-unverified" :
                "native-command-and-eventsystem; host-input-unverified"
        });
    }

    private static void RestoreOwnedWindow()
    {
        if (!ownMinimized) return;
        UBVerification.DesktopReader.RestoreCurrentWindow();
        ownMinimized = false;
        RecordOperation("window-restore", "The owned game window uses SW_SHOWNOACTIVATE after the focus-loss check.");
    }

    private static void WriteJson(string name, object value) =>
        File.WriteAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, name), SerializeJson(value));

    private static string SerializeJson(object value)
    {
        using (var output = new MemoryStream())
        {
            new DataContractJsonSerializer(value.GetType()).WriteObject(output, value);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    private static void Check(bool condition, string description)
    {
        log((condition ? "PASS: " : "FAIL: ") + description);
        if (!condition)
        {
            RecordOutcome("FAIL", description);
            throw new ExpectedResultException(description);
        }
    }

    private static bool Within(string path, string parent) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Position(int3 value) =>
        value.x.ToString(CultureInfo.InvariantCulture) + "," + value.y.ToString(CultureInfo.InvariantCulture) + "," + value.z.ToString(CultureInfo.InvariantCulture);

    private static string Hash(string path)
    {
        using (var algorithm = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Length + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Attributes + "|" + Hash(path);
    }

    private static Dictionary<string, string> StampCharacters(params string[] directories) => directories
        .Where(Directory.Exists).SelectMany(directory => Directory.GetFiles(directory, "*.chr", SearchOption.AllDirectories))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, Stamp, StringComparer.OrdinalIgnoreCase);
}
