using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SolastaUnfinishedBusiness.Models;

internal static partial class SpeechContext
{
    // The official CPU VVPP is a ZIP. Keep its resources and terms intact, outside the UB package.
    private const string VoiceVoxVersion = "0.25.2";
    private const string VoiceVoxDownloadUrl =
        "https://github.com/VOICEVOX/voicevox_engine/releases/download/0.25.2/voicevox_engine-windows-cpu-0.25.2.vvpp";
    private const long VoiceVoxDownloadSize = 1894411533;
    private const string VoiceVoxDownloadHash = "cae07cb718866708d8c6148988769966168a2282610f53f002b19778ebea38e9";
    private const string VoiceVoxEngineUuid = "074fc39e-678b-4c13-8916-ffca8d505d1d";
    private static int ManagedVoiceVoxPort;
    private const string ManagedVoiceVoxMarker = ".ub-managed-engine.json";
    private static readonly object VoiceVoxRuntimeLock = new();
    private static readonly string VoiceVoxRuntimeFolder = Path.Combine(Main.ModFolder, "voicevox_engine");
    private static readonly string ManagedVoiceVoxFolder = Path.Combine(VoiceVoxRuntimeFolder, VoiceVoxVersion);
    private static volatile int VoiceVoxPort = 50021;
    private static Process ManagedVoiceVoxProcess;
    private static IntPtr ManagedVoiceVoxJob;
    private static Task ManagedVoiceVoxStartTask;
    private static int ManagedVoiceVoxRuntimeVersion;
    private static CancellationTokenSource VoiceVoxInstallCancellation;
    private static volatile VoiceVoxSetupState VoiceVoxSetup;
    private static long VoiceVoxDownloadedBytes;

    private enum VoiceVoxSetupState
    {
        Idle,
        Downloading,
        Verifying,
        Extracting,
        Starting,
        Failed
    }

    internal static bool CanInstallVoiceVox => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                                              Environment.Is64BitProcess;
    internal static bool VoiceVoxSetupInProgress => Volatile.Read(ref VoiceVoxInstallCancellation) != null;
    internal static bool IsManagedVoiceVoxInstalled => TryGetManagedVoiceVoxExecutable(out _);
    internal static bool CanSetUpVoiceVox => CanInstallVoiceVox &&
                                            (!IsManagedVoiceVoxInstalled || VoiceVoxSetup == VoiceVoxSetupState.Failed);

    internal static string GetVoiceVoxSetupStatus()
    {
        return VoiceVoxSetup switch
        {
            VoiceVoxSetupState.Downloading => Gui.Format("ModUi/&SpeechVoiceVoxDownloadProgress",
                (Interlocked.Read(ref VoiceVoxDownloadedBytes) / (double)VoiceVoxDownloadSize).ToString("P1")),
            VoiceVoxSetupState.Verifying => Gui.Localize("ModUi/&SpeechVoiceVoxVerifying"),
            VoiceVoxSetupState.Extracting => Gui.Localize("ModUi/&SpeechVoiceVoxExtracting"),
            VoiceVoxSetupState.Starting => Gui.Localize("ModUi/&SpeechVoiceVoxStarting"),
            VoiceVoxSetupState.Failed => Gui.Localize("ModUi/&SpeechVoiceVoxInstallFailed"),
            _ => !CanInstallVoiceVox ? Gui.Localize("ModUi/&SpeechVoiceVoxUnsupported")
                : IsManagedVoiceVoxInstalled ? Gui.Localize("ModUi/&SpeechVoiceVoxInstalled") : string.Empty
        };
    }

    internal static string GetVoiceVoxInstallLabel()
    {
        return Gui.Localize(VoiceVoxSetup == VoiceVoxSetupState.Failed
            ? "ModUi/&SpeechVoiceVoxRetry" : "ModUi/&SpeechVoiceVoxInstall");
    }

    internal static async void InstallVoiceVox()
    {
        var cancellation = new CancellationTokenSource();
        var installed = false;
        lock (VoiceVoxRuntimeLock)
        {
            if (!CanInstallVoiceVox || Unloading || VoiceVoxSetupInProgress)
            {
                cancellation.Dispose();
                return;
            }
            VoiceVoxInstallCancellation = cancellation;
        }
        try
        {
            await Task.Run(() => InstallVoiceVoxAsync(cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            VoiceVoxSetup = VoiceVoxSetupState.Idle;
            installed = true;
        }
        catch (OperationCanceledException)
        {
            VoiceVoxSetup = VoiceVoxSetupState.Idle;
        }
        catch (Exception ex)
        {
            VoiceVoxSetup = VoiceVoxSetupState.Failed;
            Main.Info("VOICEVOX setup failed: " + ex.Message);
        }
        finally
        {
            lock (VoiceVoxRuntimeLock)
            {
                if (ReferenceEquals(VoiceVoxInstallCancellation, cancellation))
                {
                    VoiceVoxInstallCancellation = null;
                }
                cancellation.Dispose();
            }
        }
        // Let the connection path see installation completion before it decides to start the engine.
        if (installed && !Unloading && Main.Settings.EnableVoiceVoxSpeech)
        {
            RefreshAvailableVoices();
        }
    }

    internal static void CancelVoiceVoxSetup()
    {
        lock (VoiceVoxRuntimeLock)
        {
            VoiceVoxInstallCancellation?.Cancel();
        }
    }

    private static async Task InstallVoiceVoxAsync(CancellationToken cancellation)
    {
        if (TryGetManagedVoiceVoxExecutable(out _))
        {
            return;
        }

        Directory.CreateDirectory(VoiceVoxRuntimeFolder);
        var archivePath = Path.Combine(VoiceVoxRuntimeFolder, VoiceVoxVersion + ".vvpp.download");
        await DownloadVoiceVoxArchiveAsync(archivePath, cancellation).ConfigureAwait(false);
        VoiceVoxSetup = VoiceVoxSetupState.Verifying;
        if (!VerifyVoiceVoxArchive(archivePath, VoiceVoxDownloadHash, cancellation))
        {
            File.Delete(archivePath);
            throw new InvalidDataException("The official VOICEVOX archive checksum did not match.");
        }

        var stage = Path.Combine(VoiceVoxRuntimeFolder, ".install-" + Guid.NewGuid().ToString("N"));
        try
        {
            VoiceVoxSetup = VoiceVoxSetupState.Extracting;
            Directory.CreateDirectory(stage);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var required = archive.Entries.Sum(entry => entry.Length);
                var drive = new DriveInfo(Path.GetPathRoot(stage));
                if (drive.AvailableFreeSpace < required + 64L * 1024 * 1024)
                {
                    throw new IOException("There is not enough free space to extract VOICEVOX.");
                }
                ExtractVoiceVoxArchive(archive, stage, cancellation);
            }

            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(stage, "engine_manifest.json")));
            if (manifest["uuid"]?.Value<string>() != VoiceVoxEngineUuid || !File.Exists(Path.Combine(stage, "run.exe")))
            {
                throw new InvalidDataException("The downloaded archive is not the expected VOICEVOX engine.");
            }
            cancellation.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(stage, ManagedVoiceVoxMarker), new JObject
            {
                ["version"] = VoiceVoxVersion,
                ["sha256"] = VoiceVoxDownloadHash
            }.ToString());

            // Replace only an installation carrying our marker. Never overwrite an unowned directory.
            if (Directory.Exists(ManagedVoiceVoxFolder))
            {
                if (!IsOwnedVoiceVoxFolder(ManagedVoiceVoxFolder))
                {
                    throw new IOException("The VOICEVOX destination already contains an unowned installation.");
                }
                RemoveVoiceVoxStage(ManagedVoiceVoxFolder);
            }
            Directory.Move(stage, ManagedVoiceVoxFolder);
            File.Delete(archivePath);
        }
        finally
        {
            RemoveVoiceVoxStage(stage);
        }
    }

    private static async Task DownloadVoiceVoxArchiveAsync(string path, CancellationToken cancellation)
    {
        VoiceVoxSetup = VoiceVoxSetupState.Downloading;
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (existing > VoiceVoxDownloadSize)
        {
            File.Delete(path);
            existing = 0;
        }
        Interlocked.Exchange(ref VoiceVoxDownloadedBytes, existing);
        if (existing == VoiceVoxDownloadSize)
        {
            return;
        }

        var request = (HttpWebRequest)WebRequest.Create(VoiceVoxDownloadUrl);
        request.UserAgent = "SolastaUnfinishedBusiness";
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;
        if (existing > 0)
        {
            request.AddRange(existing);
        }
        // HttpWebRequest.Timeout does not apply to async I/O. Bound stalled headers/reads,
        // while preserving the downloaded prefix for the next attempt.
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        idleTimeout.CancelAfter(30000);
        using (idleTimeout.Token.Register(request.Abort))
        {
            try
            {
                using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var expectedPrefix = "bytes " + existing.ToString(CultureInfo.InvariantCulture) + "-";
                    var range = response.Headers["Content-Range"];
                    if (range == null || !range.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                        !range.EndsWith("/" + VoiceVoxDownloadSize.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("VOICEVOX resume returned an invalid content range.");
                    }
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    existing = 0;
                }
                else
                {
                    throw new IOException("VOICEVOX download returned an unsuccessful response.");
                }

                using var source = response.GetResponseStream();
                using var output = new FileStream(path, existing == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write, FileShare.Read, 131072, true);
                Interlocked.Exchange(ref VoiceVoxDownloadedBytes, existing);
                var buffer = new byte[131072];
                int count;
                while (true)
                {
                    idleTimeout.CancelAfter(30000);
                    count = await source.ReadAsync(buffer, 0, buffer.Length, idleTimeout.Token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }
                    if (output.Length + count > VoiceVoxDownloadSize)
                    {
                        throw new InvalidDataException("The VOICEVOX download exceeded its expected size.");
                    }
                    await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                    Interlocked.Exchange(ref VoiceVoxDownloadedBytes, output.Length);
                }
                if (output.Length != VoiceVoxDownloadSize)
                {
                    throw new EndOfStreamException("The VOICEVOX download is incomplete and can be resumed.");
                }
            }
            catch (WebException) when (cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellation);
            }
            catch (Exception ex) when (idleTimeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("The VOICEVOX download stopped responding and can be resumed.", ex);
            }
        }
    }

    private static bool VerifyVoiceVoxArchive(string path, string expectedHash, CancellationToken cancellation)
    {
        using var input = File.OpenRead(path);
        using var hash = SHA256.Create();
        var buffer = new byte[131072];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.TransformBlock(buffer, 0, count, buffer, 0);
        }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return string.Equals(BitConverter.ToString(hash.Hash).Replace("-", string.Empty), expectedHash,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractVoiceVoxArchive(ZipArchive archive, string destination, CancellationToken cancellation)
    {
        var prefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':') ||
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
            {
                throw new InvalidDataException("The VOICEVOX archive contains an unsafe path.");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var source = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[131072];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
        }
    }

    private static bool IsOwnedVoiceVoxFolder(string folder)
    {
        try
        {
            var marker = JObject.Parse(File.ReadAllText(Path.Combine(folder, ManagedVoiceVoxMarker)));
            return marker["version"]?.Value<string>() == VoiceVoxVersion &&
                   marker["sha256"]?.Value<string>() == VoiceVoxDownloadHash;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetManagedVoiceVoxExecutable(out string path)
    {
        path = Path.Combine(ManagedVoiceVoxFolder, "run.exe");
        return CanInstallVoiceVox && File.Exists(path) &&
               File.Exists(Path.Combine(ManagedVoiceVoxFolder, "engine_manifest.json")) &&
               IsOwnedVoiceVoxFolder(ManagedVoiceVoxFolder);
    }

    private static void RemoveVoiceVoxStage(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        var root = Path.GetFullPath(VoiceVoxRuntimeFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            new[] { path }.Concat(Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                .Any(folder => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("Refusing to remove an unsafe VOICEVOX staging directory.");
        }
        Directory.Delete(path, true);
    }

    private static async Task<byte[]> ConnectVoiceVoxAsync()
    {
        try
        {
            return await SendVoiceVoxRequestAsync("speakers", null, null, 3000);
        }
        catch (Exception) when (Main.Settings.EnableVoiceVoxSpeech && !Unloading &&
                                !VoiceVoxSetupInProgress && TryGetManagedVoiceVoxExecutable(out _))
        {
            Task startup;
            lock (VoiceVoxRuntimeLock)
            {
                if (ManagedVoiceVoxStartTask == null || ManagedVoiceVoxStartTask.IsFaulted ||
                    ManagedVoiceVoxStartTask.IsCanceled || ManagedVoiceVoxStartTask.IsCompleted &&
                    (ManagedVoiceVoxProcess == null || ManagedVoiceVoxProcess.HasExited))
                {
                    ManagedVoiceVoxStartTask = StartManagedVoiceVoxAsync(VoiceVoxSessionVersion,
                        ManagedVoiceVoxRuntimeVersion);
                }
                startup = ManagedVoiceVoxStartTask;
            }
            await startup;
            return await SendVoiceVoxRequestAsync("speakers", null, null, 3000);
        }
    }

    private static async Task StartManagedVoiceVoxAsync(int session, int runtimeVersion)
    {
        // Defer work until the task has been stored, even if process creation fails immediately.
        await Task.Yield();
        try
        {
            lock (VoiceVoxRuntimeLock)
            {
                if (Unloading || !Main.Settings.EnableVoiceVoxSpeech || session != VoiceVoxSessionVersion ||
                    runtimeVersion != ManagedVoiceVoxRuntimeVersion)
                {
                    throw new OperationCanceledException();
                }
                if (!TryGetManagedVoiceVoxExecutable(out var executable))
                {
                    throw new FileNotFoundException("The managed VOICEVOX engine is not installed.");
                }
                VoiceVoxSetup = VoiceVoxSetupState.Starting;
                if (ManagedVoiceVoxProcess == null || ManagedVoiceVoxProcess.HasExited)
                {
                    DisposeManagedVoiceVoxProcess();
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    try
                    {
                        listener.Start();
                        ManagedVoiceVoxPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                    }
                    finally
                    {
                        listener.Stop();
                    }
                    ManagedVoiceVoxJob = CreateVoiceVoxJob();
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo(executable,
                            "--host 127.0.0.1 --port " + ManagedVoiceVoxPort +
                            " --output_log_utf8 --disable_mutable_api --cors_policy_mode localapps")
                        {
                            WorkingDirectory = ManagedVoiceVoxFolder,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    ManagedVoiceVoxProcess = process;
                    if (!process.Start() || !AssignProcessToJobObject(ManagedVoiceVoxJob, process.Handle))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    // Drain both pipes without retaining a growing log in memory.
                    process.OutputDataReceived += (_, _) => { };
                    process.ErrorDataReceived += (_, _) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                VoiceVoxPort = ManagedVoiceVoxPort;
            }

            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(90))
            {
                if (Unloading || !Main.Settings.EnableVoiceVoxSpeech || session != VoiceVoxSessionVersion)
                {
                    throw new OperationCanceledException();
                }
                lock (VoiceVoxRuntimeLock)
                {
                    if (runtimeVersion != ManagedVoiceVoxRuntimeVersion)
                    {
                        throw new OperationCanceledException();
                    }
                    if (ManagedVoiceVoxProcess == null || ManagedVoiceVoxProcess.HasExited)
                    {
                        throw new IOException("The managed VOICEVOX process exited before it was ready.");
                    }
                }
                try
                {
                    var manifestBytes = await SendVoiceVoxRequestAsync("engine_manifest", null, null, 1500);
                    var manifest = JObject.Parse(System.Text.Encoding.UTF8.GetString(manifestBytes));
                    if (manifest["uuid"]?.Value<string>() != VoiceVoxEngineUuid)
                    {
                        throw new InvalidDataException("The local port is not served by the expected VOICEVOX engine.");
                    }
                    lock (VoiceVoxRuntimeLock)
                    {
                        if (runtimeVersion != ManagedVoiceVoxRuntimeVersion || Unloading ||
                            !Main.Settings.EnableVoiceVoxSpeech || session != VoiceVoxSessionVersion)
                        {
                            throw new OperationCanceledException();
                        }
                        if (ManagedVoiceVoxProcess == null || ManagedVoiceVoxProcess.HasExited)
                        {
                            throw new IOException("The managed VOICEVOX process exited during startup.");
                        }
                        VoiceVoxSetup = VoiceVoxSetupState.Idle;
                    }
                    return;
                }
                catch (WebException)
                {
                    await Task.Delay(200);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(200);
                }
            }
            throw new TimeoutException("The managed VOICEVOX engine did not finish starting.");
        }
        catch
        {
            lock (VoiceVoxRuntimeLock)
            {
                if (runtimeVersion == ManagedVoiceVoxRuntimeVersion)
                {
                    DisposeManagedVoiceVoxProcess();
                    VoiceVoxPort = 50021;
                    VoiceVoxSetup = Unloading || !Main.Settings.EnableVoiceVoxSpeech ||
                                    session != VoiceVoxSessionVersion
                        ? VoiceVoxSetupState.Idle : VoiceVoxSetupState.Failed;
                }
            }
            throw;
        }
    }

    private static void StopManagedVoiceVox()
    {
        CancelVoiceVoxSetup();
        lock (VoiceVoxRuntimeLock)
        {
            ManagedVoiceVoxRuntimeVersion++;
            DisposeManagedVoiceVoxProcess();
            ManagedVoiceVoxStartTask = null;
            VoiceVoxPort = 50021;
            if (!VoiceVoxSetupInProgress)
            {
                VoiceVoxSetup = VoiceVoxSetupState.Idle;
            }
        }
    }

    private static void DisposeManagedVoiceVoxProcess()
    {
        if (ManagedVoiceVoxJob != IntPtr.Zero)
        {
            CloseHandle(ManagedVoiceVoxJob);
            ManagedVoiceVoxJob = IntPtr.Zero;
        }
        var process = ManagedVoiceVoxProcess;
        ManagedVoiceVoxProcess = null;
        if (process == null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(1000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process creation may have failed before a handle was available.
        }
        catch (Win32Exception ex)
        {
            Main.Info("Unable to stop the managed VOICEVOX process: " + ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static IntPtr CreateVoiceVoxJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var limits = new VoiceVoxJobLimits { Basic = new VoiceVoxBasicJobLimits { LimitFlags = 0x2000 } };
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)))
        {
            return job;
        }
        var error = Marshal.GetLastWin32Error();
        CloseHandle(job);
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VoiceVoxBasicJobLimits
    {
        public long PerProcessUserTime;
        public long PerJobUserTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet;
        public UIntPtr MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VoiceVoxJobLimits
    {
        public VoiceVoxBasicJobLimits Basic;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
        ref VoiceVoxJobLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
