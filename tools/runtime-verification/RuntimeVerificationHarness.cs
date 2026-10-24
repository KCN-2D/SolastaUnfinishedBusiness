using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using UnityEngine;
using UnityModManagerNet;

// Compiled only into the temporary verification mod; never included in UB.
public sealed class RuntimeVerificationHarness : MonoBehaviour
{
    private static readonly object LogLock = new object();
    private int passed;
    private int failed;
    private int incomplete;
    private bool finished;
    private bool backgroundOverride;
    private bool previousRunInBackground;
    private static RuntimeVerificationHarness current;
    private float nextExecutionSample;
    private string executionPhase = "startup";
    private bool executionObservationFailed;
    private int processId;
    private uint mainThreadId;
    private long focusCallbackSequence;
    private long lastFocusLossSequence = -1;
    private long focusLossBoundarySequence = -1;
    private readonly List<Action> startupCleanups = new List<Action>();

    internal static void AssertMainThread()
    {
        if (ReferenceEquals(current, null) ||
            UBVerification.DesktopReader.CurrentNativeThreadId() != current.mainThreadId)
        {
            throw new InvalidOperationException("Presentation observation requires the installed game main thread.");
        }
    }

    public static bool Load(UnityModManager.ModEntry entry)
    {
        var host = new GameObject("UB Runtime Verification");
        DontDestroyOnLoad(host);
        host.AddComponent<RuntimeVerificationHarness>();
        return true;
    }

    private void Awake()
    {
        Application.logMessageReceived += OnUnityLog;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        current = this;
        using (var process = System.Diagnostics.Process.GetCurrentProcess()) processId = process.Id;
        mainThreadId = UBVerification.DesktopReader.CurrentNativeThreadId();
        if (RuntimeVerificationConfiguration.RequireOtherAppForeground && !RecordDesktopReceipt())
        {
            Finish();
            return;
        }
        if (RuntimeVerificationConfiguration.RunInBackground)
        {
            previousRunInBackground = Application.runInBackground;
            backgroundOverride = true;
            Application.runInBackground = true;
            Log("INFO: Background execution enabled; previous=" + previousRunInBackground +
                "; actual=" + Application.runInBackground + "; focused=" + Application.isFocused);
        }
        WriteExecution("awake");
        InstallSelectedStartupHooks();
        StartCoroutine(Run());
    }

    private void InstallSelectedStartupHooks()
    {
        foreach (var typeName in RuntimeVerificationConfiguration.ProbeTypes)
        {
            try
            {
                var type = typeof(RuntimeVerificationHarness).Assembly.GetType(typeName, true);
                var installer = type.GetMethod("InstallStartupHooks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(Action<string>) }, null);
                if (installer == null) continue;
                var cleanup = type.GetMethod("RemoveStartupHooks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (cleanup == null || installer.ReturnType != typeof(void) || cleanup.ReturnType != typeof(void))
                    throw new InvalidOperationException("Startup installer requires void InstallStartupHooks(Action<string>) and void RemoveStartupHooks().");
                startupCleanups.Add(() => cleanup.Invoke(null, null));
                installer.Invoke(null, new object[] { new Action<string>(Log) });
            }
            catch (Exception ex) { Log("FAIL: Startup hook installation for " + typeName + ": " + Unwrap(ex)); }
        }
    }

    private void RemoveSelectedStartupHooks()
    {
        var cleanups = startupCleanups.ToArray();
        startupCleanups.Clear();
        for (var index = cleanups.Length - 1; index >= 0; index--)
            try { cleanups[index](); }
            catch (Exception ex) { Log("FAIL: Startup hook cleanup: " + Unwrap(ex)); }
    }

    private IEnumerator Run()
    {
        var deadline = Time.realtimeSinceStartup + RuntimeVerificationConfiguration.TimeoutSeconds;
        while (!IsUbEnabled())
        {
            if (Time.realtimeSinceStartup >= deadline)
            {
                Log("INCOMPLETE: UB did not finish initialization before the deadline.");
                Finish();
                yield break;
            }

            yield return null;
        }

        if (!RecordAssemblyReceipt())
        {
            Finish();
            yield break;
        }

        // Let the native GUI finish its normal post-initialization work.
        yield return new WaitForSecondsRealtime(2f);
        foreach (var typeName in RuntimeVerificationConfiguration.ProbeTypes)
        {
            var checksBefore = passed + failed + incomplete;
            Log("INFO: Starting " + typeName);
            IEnumerator routine = null;
            try
            {
                var type = typeof(RuntimeVerificationHarness).Assembly.GetType(typeName, true);
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static, null, new[] { typeof(Action<string>) }, null);
                if (method == null || (method.ReturnType != typeof(void) &&
                    !typeof(IEnumerator).IsAssignableFrom(method.ReturnType)))
                {
                    throw new InvalidOperationException(typeName +
                        " must define static void/IEnumerator Run(Action<string> log).");
                }

                routine = method.Invoke(null, new object[] { new Action<string>(Log) }) as IEnumerator;
            }
            catch (Exception ex)
            {
                Log("FAIL: " + typeName + ": " + Unwrap(ex));
            }

            if (routine != null)
            {
                // Unity does not propagate exceptions from nested coroutines to their parent.
                // Drive each enumerator here, while yielding Unity's wait instructions normally.
                var stack = new Stack<IEnumerator>();
                stack.Push(routine);
                while (stack.Count != 0)
                {
                    object current = null;
                    var moved = false;
                    try
                    {
                        moved = stack.Peek().MoveNext();
                        if (moved)
                        {
                            current = stack.Peek().Current;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("FAIL: " + typeName + ": " + Unwrap(ex));
                        DisposeStack(stack);
                        break;
                    }

                    if (!moved)
                    {
                        DisposeEnumerator(stack.Pop());
                    }
                    else if (current is IEnumerator nested)
                    {
                        stack.Push(nested);
                    }
                    else
                    {
                        yield return current;
                    }
                }
            }

            if (checksBefore == passed + failed + incomplete)
            {
                Log("FAIL: " + typeName + " returned without reporting any checks.");
            }
        }

        Finish();
    }

    private static bool IsUbEnabled()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x =>
            x.GetName().Name == "SolastaUnfinishedBusiness");
        var main = assembly?.GetType("SolastaUnfinishedBusiness.Main");
        return main?.GetProperty("Enabled", BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic)?.GetValue(null, null) is true;
    }

    [DataContract]
    private sealed class LoadedAssembly
    {
        [DataMember] public string location, fullName, moduleVersionId, sha256, error;
    }

    [DataContract]
    private sealed class AssemblyReceipt
    {
        [DataMember] public string capturedUtc, observationSource, expectedLocation, expectedSha256;
        [DataMember] public string modId, modAssemblyName, originLocation, originSha256, expectedCacheLocation, managerVersion, managerSha256;
        [DataMember] public bool modEntryAssemblyMatches;
        [DataMember] public int processId;
        [DataMember] public uint nativeThreadId;
        [DataMember] public bool matches;
        [DataMember] public LoadedAssembly[] assemblies;
    }

    private bool RecordAssemblyReceipt()
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(x =>
                x.GetName().Name == "SolastaUnfinishedBusiness").ToArray();
            var receipt = new AssemblyReceipt {
                capturedUtc = DateTime.UtcNow.ToString("O"), observationSource = "game-main-thread",
                processId = processId, nativeThreadId = UBVerification.DesktopReader.CurrentNativeThreadId(),
                expectedLocation = RuntimeVerificationConfiguration.ExpectedUbLocation,
                expectedSha256 = RuntimeVerificationConfiguration.ExpectedUbSha256,
                assemblies = loaded.Select(assembly => {
                    var row = new LoadedAssembly();
                    try {
                        row.fullName = assembly.FullName;
                        row.location = assembly.Location;
                        row.moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("D");
                        using (var stream = File.OpenRead(row.location))
                        using (var hash = SHA256.Create())
                            row.sha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
                    }
                    catch (Exception ex) { row.error = ex.GetType().Name + ": " + ex.Message; }
                    return row;
                }).ToArray()
            };
            var entry = UnityModManager.FindMod("SolastaUnfinishedBusiness");
            if (entry == null || entry.Info == null) throw new InvalidOperationException("The loaded UB has no UMM ModEntry.");
            receipt.modId = entry.Info.Id;
            receipt.modAssemblyName = entry.Info.AssemblyName;
            receipt.modEntryAssemblyMatches = loaded.Length == 1 && ReferenceEquals(entry.Assembly, loaded[0]);
            receipt.originLocation = Path.GetFullPath(Path.Combine(entry.Path, entry.Info.AssemblyName));
            receipt.originSha256 = AssemblyFileHash(receipt.originLocation);
            receipt.managerVersion = UnityModManager.version.ToString();
            receipt.managerSha256 = AssemblyFileHash(typeof(UnityModManager).Assembly.Location);
            // UMM 0.33.2 ModEntry.Load derives the first-load cache key in the game runtime.
            var cacheStamp = (long)new FileInfo(receipt.originLocation).LastWriteTimeUtc.GetHashCode() +
                UnityModManager.version.GetHashCode() + entry.ManagerVersion.GetHashCode();
            receipt.expectedCacheLocation = receipt.originLocation + "." +
                unchecked((ushort)cacheStamp.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".cache";
            receipt.matches = receipt.nativeThreadId == mainThreadId && receipt.assemblies.Length == 1 &&
                String.IsNullOrEmpty(receipt.assemblies[0].error) &&
                receipt.modId == "SolastaUnfinishedBusiness" && receipt.modAssemblyName == "SolastaUnfinishedBusiness.dll" &&
                receipt.modEntryAssemblyMatches &&
                String.Equals(receipt.originLocation, receipt.expectedLocation, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(receipt.originSha256, receipt.expectedSha256, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(receipt.managerSha256, RuntimeVerificationConfiguration.ExpectedManagerSha256, StringComparison.OrdinalIgnoreCase) &&
                (String.Equals(receipt.assemblies[0].location, receipt.expectedLocation, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(receipt.assemblies[0].location, receipt.expectedCacheLocation, StringComparison.OrdinalIgnoreCase)) &&
                String.Equals(receipt.assemblies[0].sha256, receipt.expectedSha256, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(receipt.assemblies[0].moduleVersionId, RuntimeVerificationConfiguration.ExpectedUbModuleVersionId, StringComparison.OrdinalIgnoreCase);
            using (var stream = File.Create(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "Runtime-Assembly.json")))
                new DataContractJsonSerializer(typeof(AssemblyReceipt)).WriteObject(stream, receipt);
            if (!receipt.matches) {
                Log("INCOMPLETE: The loaded UB assembly does not uniquely match the expected build and location.");
                return false;
            }
            Log("INFO: Loaded UB assembly verified; SHA256=" + receipt.assemblies[0].sha256 +
                "; MVID=" + receipt.assemblies[0].moduleVersionId);
            return true;
        }
        catch (Exception ex) {
            Log("INCOMPLETE: The loaded UB assembly could not be verified: " + ex.Message);
            return false;
        }
    }

    private static string AssemblyFileHash(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }

    private void DisposeStack(Stack<IEnumerator> stack)
    {
        while (stack.Count != 0)
        {
            DisposeEnumerator(stack.Pop());
        }
    }

    private void DisposeEnumerator(IEnumerator enumerator)
    {
        try
        {
            (enumerator as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log("FAIL: Probe cleanup: " + Unwrap(ex));
        }
    }

    private void Log(string message)
    {
        lock (LogLock)
        {
            if (message.StartsWith("PASS:", StringComparison.Ordinal))
            {
                passed++;
            }
            else if (message.StartsWith("FAIL:", StringComparison.Ordinal))
            {
                failed++;
            }
            else if (message.StartsWith("INCOMPLETE:", StringComparison.Ordinal))
            {
                incomplete++;
            }

            File.AppendAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "Probe.log"),
                DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
            // Late Unity errors must invalidate an already completed run as well.
            if (finished)
            {
                WriteResult();
            }
        }
    }

    private void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
        {
            Log("FAIL: Unity " + type + ": " + condition + Environment.NewLine + stackTrace);
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        Log("FAIL: Unhandled exception: " + args.ExceptionObject);
    }

    private void Finish()
    {
        RemoveSelectedStartupHooks();
        WriteExecution("finished");
        RestoreBackgroundExecution();
        lock (LogLock)
        {
            if (passed + failed + incomplete == 0)
            {
                failed++;
            }

            finished = true;
            WriteResult();
        }
    }

    private void WriteResult()
    {
        var status = failed > 0 ? "failed" : incomplete > 0 ? "incomplete" : "success";
        var path = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "HarnessResult.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, "{\"status\":\"" + status + "\",\"passed\":" + passed +
            ",\"failed\":" + failed + ",\"incomplete\":" + incomplete + "}");
        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, null);
        }
        else
        {
            File.Move(temporaryPath, path);
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException invocation && invocation.InnerException != null
            ? invocation.InnerException
            : exception;
    }

    private void OnDestroy()
    {
        WriteExecution("destroy");
        RemoveSelectedStartupHooks();
        RestoreBackgroundExecution();
        Application.logMessageReceived -= OnUnityLog;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        if (current == this) current = null;
    }

    private void RestoreBackgroundExecution()
    {
        if (!backgroundOverride) return;
        backgroundOverride = false;
        Application.runInBackground = previousRunInBackground;
        Log("INFO: Background execution restored; actual=" + Application.runInBackground);
    }

    [DataContract]
    private sealed class ExecutionSample
    {
        [DataMember] public string utc;
        [DataMember] public string eventName;
        [DataMember] public string phase;
        [DataMember] public int processId;
        [DataMember] public uint nativeThreadId;
        [DataMember] public int frame;
        [DataMember] public float realtime;
        [DataMember] public float gameTime;
        [DataMember] public float timeScale;
        [DataMember] public bool focused;
        [DataMember] public bool? focusCallbackValue;
        [DataMember] public long focusCallbackSequence, lastFocusLossSequence, focusLossBoundarySequence;
        [DataMember] public bool? pauseCallbackValue;
        [DataMember] public bool runInBackground;
        [DataMember] public bool batchMode;
        [DataMember] public string graphicsDeviceType;
        [DataMember] public string graphicsDeviceName;
        [DataMember] public bool graphicsDeviceAvailable;
        [DataMember] public int screenWidth, screenHeight;
    }

    internal static void RecordExecution(string phase)
    {
        if (!current) return;
        current.executionPhase = phase;
        current.WriteExecution("operation");
    }

    internal static long CaptureFocusCallbackSequence()
    {
        AssertMainThread();
        current.focusLossBoundarySequence = current.focusCallbackSequence;
        return current.focusLossBoundarySequence;
    }

    internal static bool ObservedFocusLossAfter(long sequence) =>
        current && sequence >= 0 && current.lastFocusLossSequence > sequence;

    private void Update()
    {
        if (!finished && Time.realtimeSinceStartup >= nextExecutionSample)
        {
            nextExecutionSample = Time.realtimeSinceStartup + 1f;
            WriteExecution("heartbeat");
        }
    }

    private void OnApplicationFocus(bool focused)
    {
        focusCallbackSequence++;
        if (!focused) lastFocusLossSequence = focusCallbackSequence;
        WriteExecution("focus-change", focused, null);
    }
    private void OnApplicationPause(bool paused) { WriteExecution("pause-change", null, paused); }

    private void WriteExecution(string eventName, bool? focusCallbackValue = null, bool? pauseCallbackValue = null)
    {
        if (!RuntimeVerificationConfiguration.RequireOtherAppForeground || executionObservationFailed) return;
        try
        {
            var sample = new ExecutionSample {
                utc = DateTime.UtcNow.ToString("O"), eventName = eventName, phase = executionPhase,
                processId = processId, frame = Time.frameCount, realtime = Time.realtimeSinceStartup,
                nativeThreadId = UBVerification.DesktopReader.CurrentNativeThreadId(),
                gameTime = Time.time, timeScale = Time.timeScale, focused = Application.isFocused,
                focusCallbackValue = focusCallbackValue, pauseCallbackValue = pauseCallbackValue,
                focusCallbackSequence = this.focusCallbackSequence, lastFocusLossSequence = this.lastFocusLossSequence,
                focusLossBoundarySequence = this.focusLossBoundarySequence,
                runInBackground = Application.runInBackground, batchMode = Application.isBatchMode,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceAvailable = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
                screenWidth = Screen.width, screenHeight = Screen.height
            };
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ExecutionSample)).WriteObject(stream, sample);
                File.AppendAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "Runtime-Execution.jsonl"),
                    System.Text.Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine);
            }
            var desktop = UBVerification.DesktopReader.ReadCurrent(RuntimeVerificationConfiguration.ExpectedDesktopName,
                "game-main-thread");
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(UBVerification.DesktopSnapshot)).WriteObject(stream, desktop);
                File.AppendAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "Runtime-Desktop.jsonl"),
                    System.Text.Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine);
            }
            if (!desktop.complete || !desktop.isolated || desktop.processId != processId || desktop.nativeThreadId != mainThreadId)
                throw new InvalidOperationException("The main-thread desktop observation is incomplete or no longer non-input.");
        }
        catch (Exception ex)
        {
            executionObservationFailed = true;
            Log("INCOMPLETE: Main-thread execution observation failed: " + ex.Message);
        }
    }

    private bool RecordDesktopReceipt()
    {
        try
        {
            var snapshot = UBVerification.DesktopReader.ReadCurrent(RuntimeVerificationConfiguration.ExpectedDesktopName,
                "game-main-thread");
            var path = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "Runtime-Desktop.json");
            using (var stream = File.Create(path + ".tmp"))
                new DataContractJsonSerializer(typeof(UBVerification.DesktopSnapshot)).WriteObject(stream, snapshot);
            File.Move(path + ".tmp", path);
            if (snapshot.processId != processId || snapshot.nativeThreadId != mainThreadId || !snapshot.complete ||
                !snapshot.isolated || snapshot.runnerReceivesInput != false ||
                snapshot.runnerDesktop != RuntimeVerificationConfiguration.ExpectedDesktopName)
            {
                Log("INCOMPLETE: The main-thread desktop does not match the non-input verification desktop.");
                return false;
            }
            Log("INFO: Main-thread desktop verified; desktop=" + snapshot.runnerDesktop + "; process=" + processId);
            return true;
        }
        catch (Exception ex)
        {
            Log("INCOMPLETE: The main-thread desktop could not be verified: " + ex.Message);
            return false;
        }
    }
}
