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
        if (!InstallNativeUserDataIsolation()) return false;
        var host = new GameObject("UB Runtime Verification");
        DontDestroyOnLoad(host);
        host.AddComponent<RuntimeVerificationHarness>();
        return true;
    }

    [DataContract]
    private sealed class NativeUserDataReceipt
    {
        [DataMember] public int schemaVersion = 1, processId;
        [DataMember] public string utc, owner, originalDirectory, expectedOriginalRoot, isolatedDirectory, allowedRoot, actualRoot, error;
        [DataMember] public bool isolated, rootOverridden, instancePresent, ubLoadedBeforeIsolation;
        [DataMember] public NativePreferenceReceipt[] preferences;
        [DataMember] public List<NativeDirectoryReceipt> directories = new List<NativeDirectoryReceipt>();
    }

    [DataContract]
    private sealed class NativePreferenceReceipt
    {
        [DataMember] public string name, originalPath, originalSha256, ownedPath, ownedSha256, hashError;
        [DataMember] public bool originalExists, ownedExists, repositoryPresent, repositoryReplaced;
    }

    [DataContract]
    private sealed class NativeDirectoryReceipt
    {
        [DataMember] public string property, path;
        [DataMember] public bool existed, created, verified;
    }

    private static bool InstallNativeUserDataIsolation()
    {
        var receipt = new NativeUserDataReceipt {
            utc = DateTime.UtcNow.ToString("O"), owner = RuntimeVerificationConfiguration.NativeUserDataOwner,
            expectedOriginalRoot = RuntimeVerificationConfiguration.NativeOriginalGameDirectory,
            isolatedDirectory = RuntimeVerificationConfiguration.NativeUserDataDirectory,
            allowedRoot = RuntimeVerificationConfiguration.NativeUserDataRoot,
            preferences = new[] { new NativePreferenceReceipt { name = "UserPreferences" },
                new NativePreferenceReceipt { name = "PlatformPreferences" } }
        };
        using (var process = System.Diagnostics.Process.GetCurrentProcess()) receipt.processId = process.Id;
        try
        {
            if (String.IsNullOrEmpty(receipt.owner) || !String.Equals(
                    Environment.GetEnvironmentVariable("UB_RUNTIME_VERIFICATION_OWNER"), receipt.owner, StringComparison.Ordinal))
                throw new InvalidOperationException("This process does not own the temporary native user-data isolation lease.");
            var evidence = Path.GetFullPath(RuntimeVerificationConfiguration.EvidenceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Path.IsPathRooted(receipt.isolatedDirectory) || !Path.IsPathRooted(receipt.expectedOriginalRoot) ||
                !Path.IsPathRooted(receipt.allowedRoot))
                throw new InvalidOperationException("Native user-data roots must be absolute paths.");
            receipt.isolatedDirectory = Path.GetFullPath(receipt.isolatedDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            receipt.expectedOriginalRoot = Path.GetFullPath(receipt.expectedOriginalRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            receipt.allowedRoot = Path.GetFullPath(receipt.allowedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var ownedParent = Directory.GetParent(receipt.isolatedDirectory);
            if (ownedParent == null || !String.Equals(ownedParent.FullName.TrimEnd(Path.DirectorySeparatorChar), receipt.allowedRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                String.Equals(receipt.isolatedDirectory, receipt.expectedOriginalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native user data must use a separate owned directory directly inside the authorized short root.");
            for (var directory = new DirectoryInfo(receipt.isolatedDirectory); directory != null; directory = directory.Parent)
            {
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Native user-data ownership cannot cross missing directories or reparse points.");
                if (String.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), receipt.allowedRoot, StringComparison.OrdinalIgnoreCase)) break;
            }
            var marker = Path.Combine(receipt.isolatedDirectory, ".ub-runtime-userdata");
            if (String.IsNullOrEmpty(receipt.owner) || !File.Exists(marker) ||
                (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                !String.Equals(File.ReadAllText(marker), receipt.owner, StringComparison.Ordinal))
                throw new InvalidOperationException("Native user-data ownership marker does not match this verification process.");
            var evidenceMarker = Path.Combine(receipt.isolatedDirectory, ".ub-runtime-evidence");
            if (!File.Exists(evidenceMarker) || (File.GetAttributes(evidenceMarker) & FileAttributes.ReparsePoint) != 0 ||
                !String.Equals(File.ReadAllText(evidenceMarker), evidence, StringComparison.Ordinal))
                throw new InvalidOperationException("Native user data is not bound to this verification evidence directory.");
            receipt.ubLoadedBeforeIsolation = UnityModManager.modEntries.Any(mod =>
                mod.Info.Id == "SolastaUnfinishedBusiness" && mod.Loaded);
            if (receipt.ubLoadedBeforeIsolation)
                throw new InvalidOperationException("Native user data must be isolated before UB loads its preference caches.");

            const BindingFlags fields = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
            var application = typeof(TacticalAdventuresApplication);
            var rootField = application.GetField("gameDirectory", fields);
            var instanceField = application.GetField("instance", fields);
            if (rootField == null || rootField.FieldType != typeof(string) || !rootField.IsStatic || instanceField == null ||
                instanceField.FieldType != application || !instanceField.IsStatic)
                throw new MissingFieldException("Native user-data root and application instance fields were not resolved.");
            receipt.originalDirectory = Path.GetFullPath(TacticalAdventuresApplication.GameDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!String.Equals(receipt.originalDirectory, receipt.expectedOriginalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The initialized native user-data root differs from the archived original root.");

            // Keep this process-local redirect through native Quit, including its direct preference exports.
            rootField.SetValue(null, receipt.isolatedDirectory);
            receipt.actualRoot = TacticalAdventuresApplication.GameDirectory;
            receipt.rootOverridden = String.Equals(receipt.actualRoot, receipt.isolatedDirectory, StringComparison.OrdinalIgnoreCase);
            if (!receipt.rootOverridden) throw new InvalidOperationException("Native GameDirectory did not adopt the owned user-data root.");
            foreach (var name in new[] { "SaveGameDirectory", "MultiplayerFilesDirectory", "LostIronManSaveGameDirectory",
                "GameCharactersDirectory", "GameBuiltInCharactersDirectory", "GameCharactersTemporaryDirectory",
                "UserContentDirectory", "UserLocationsDirectory", "UserCampaignsDirectory", "ExportedCharacterSheetsDirectory",
                "EventLogsDirectory", "MarketingDirectory", "ProfilerLogsDirectory" })
            {
                var property = application.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null || property.PropertyType != typeof(string) || property.GetGetMethod(true) == null)
                    throw new MissingMemberException(application.FullName, name);
                var path = Path.GetFullPath((string)property.GetValue(null, null))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var directory = new NativeDirectoryReceipt { property = name, path = path, existed = Directory.Exists(path) };
                receipt.directories.Add(directory);
                if (!path.StartsWith(receipt.isolatedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Native derived user-data directory escapes the owned root: " + name);
                for (var ancestor = new DirectoryInfo(path); ancestor != null; ancestor = ancestor.Parent)
                {
                    if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Native derived user-data directory crosses a reparse point: " + name);
                    if (String.Equals(ancestor.FullName.TrimEnd(Path.DirectorySeparatorChar), receipt.isolatedDirectory,
                            StringComparison.OrdinalIgnoreCase)) break;
                }
                Directory.CreateDirectory(path);
                directory.created = !directory.existed && Directory.Exists(path);
                directory.verified = Directory.Exists(path) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
                if (!directory.verified) throw new IOException("Native derived user-data directory could not be prepared: " + name);
            }
            var instance = instanceField.GetValue(null);
            receipt.instancePresent = !ReferenceEquals(instance, null);
            if (!receipt.instancePresent) throw new InvalidOperationException("The native application instance must exist after its Start entry point.");
            foreach (var preference in receipt.preferences)
            {
                preference.originalPath = Path.Combine(receipt.expectedOriginalRoot, preference.name + ".xml");
                preference.ownedPath = Path.Combine(receipt.isolatedDirectory, preference.name + ".xml");
                preference.originalExists = File.Exists(preference.originalPath);
                preference.ownedExists = File.Exists(preference.ownedPath);
                if (preference.ownedExists && (File.GetAttributes(preference.ownedPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Owned native preferences cannot be reparse points.");
                if (preference.ownedExists)
                {
                    var settings = new System.Xml.XmlReaderSettings {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null
                    };
                    using (var reader = System.Xml.XmlReader.Create(preference.ownedPath, settings))
                    {
                        var document = new System.Xml.XmlDocument { XmlResolver = null };
                        document.Load(reader);
                        if (document.DocumentElement == null) throw new InvalidOperationException("Owned native preferences must contain an XML document element.");
                    }
                }
                try
                {
                    if (preference.originalExists) preference.originalSha256 = AssemblyFileHash(preference.originalPath);
                    if (preference.ownedExists) preference.ownedSha256 = AssemblyFileHash(preference.ownedPath);
                }
                catch (Exception ex) { preference.hashError = ex.Message; }
                var fieldName = preference.name == "UserPreferences" ? "userPreferences" : "platformPreferences";
                var repositoryField = application.GetField(fieldName, fields);
                if (repositoryField == null || repositoryField.FieldType != typeof(UserPreferencesRepository) || repositoryField.IsStatic)
                    throw new MissingFieldException("Native preference repository field was not resolved: " + fieldName);
                preference.repositoryPresent = !ReferenceEquals(repositoryField.GetValue(instance), null);
                if (!preference.repositoryPresent) continue;
                var replacement = new UserPreferencesRepository();
                replacement.Import(preference.ownedPath, true, null);
                repositoryField.SetValue(instance, replacement);
                preference.repositoryReplaced = ReferenceEquals(repositoryField.GetValue(instance), replacement);
                if (!preference.repositoryReplaced) throw new InvalidOperationException("Native preference repository replacement failed: " + fieldName);
            }
            receipt.isolated = receipt.rootOverridden && receipt.directories.Count == 13 && receipt.directories.All(directory => directory.verified) &&
                receipt.preferences.All(preference =>
                !preference.repositoryPresent || preference.repositoryReplaced);
            WriteNativeUserDataReceipt(receipt);
            return receipt.isolated;
        }
        catch (Exception ex)
        {
            receipt.error = ex.ToString();
            receipt.isolated = false;
            try { WriteNativeUserDataReceipt(receipt); } catch { }
            // Returning false alone does not stop UMM from loading a mod whose requirement failed.
            Environment.Exit(91);
            return false;
        }
    }

    private static void WriteNativeUserDataReceipt(NativeUserDataReceipt receipt)
    {
        using (var stream = File.Create(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "Native-UserData.json")))
            new DataContractJsonSerializer(typeof(NativeUserDataReceipt)).WriteObject(stream, receipt);
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
