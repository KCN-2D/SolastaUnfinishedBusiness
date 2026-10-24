using System;
using System.Collections.Generic;
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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SolastaUnfinishedBusiness.Models;

internal static partial class SpeechContext
{
    // The official CPU VVPP is a ZIP. Keep its resources and terms intact, outside the UB package.
    private const long VoiceVoxMaximumPackageSize = 8L * 1024 * 1024 * 1024;
    private const string VoiceVoxReleaseApi = "https://api.github.com/repos/VOICEVOX/voicevox_engine/releases/latest";
    private const string VoiceVoxAppReleaseApi = "https://api.github.com/repos/VOICEVOX/voicevox/releases/latest";
    internal const string VoiceVoxReleasesUrl = "https://github.com/VOICEVOX/voicevox_engine/releases";
    internal const string VoiceVoxAppReleasesUrl = "https://github.com/VOICEVOX/voicevox/releases/latest";
    private static readonly Regex VoiceVoxVersionPattern = new(
        @"^v?(\d+\.\d+\.\d+)(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled);
    private static CancellationTokenSource VoiceVoxUpdateCancellation;
    private static int VoiceVoxUpdateGeneration;
    private static DateTime VoiceVoxUpdateCheckedAt;
    private static bool VoiceVoxUpdateFailed;
    private static bool VoiceVoxAppUpdateFailed;
    internal static bool VoiceVoxUpdateInProgress { get; private set; }
    internal static string ConnectedVoiceVoxVersion { get; private set; }
    internal static string LatestVoiceVoxVersion { get; private set; }
    internal static string InstalledVoiceVoxAppVersion { get; private set; }
    internal static string LatestVoiceVoxAppVersion { get; private set; }
    private static int ManagedVoiceVoxPort;
    private const string ManagedVoiceVoxMarker = ".ub-managed-engine.json";
    private static readonly object VoiceVoxRuntimeLock = new();
    private static readonly string VoiceVoxRuntimeFolder = Path.Combine(Main.ModFolder, "voicevox_engine");
    private static VoiceVoxPackage LatestVoiceVoxPackage;
    private static volatile int VoiceVoxPort = 50021;
    private static Process ManagedVoiceVoxProcess;
    private static IntPtr ManagedVoiceVoxJob;
    private static Task ManagedVoiceVoxStartTask;
    private static int ManagedVoiceVoxRuntimeVersion;
    private static CancellationTokenSource VoiceVoxInstallCancellation;
    private static volatile VoiceVoxSetupState VoiceVoxSetup;
    private static long VoiceVoxDownloadedBytes;
    private static long VoiceVoxDownloadTotalBytes;

    private enum VoiceVoxSetupState
    {
        Idle,
        CheckingRelease,
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
                                            (LatestVoiceVoxPackage == null || !IsManagedVoiceVoxPackageReady(LatestVoiceVoxPackage) ||
                                             VoiceVoxSetup == VoiceVoxSetupState.Failed);

    internal static string GetVoiceVoxVersionStatus() => Gui.Format("ModUi/&SpeechVoiceVoxEngineVersion",
        ConnectedVoiceVoxVersion ?? Gui.Localize("ModUi/&SpeechVoiceVoxVersionUnknown"));

    internal static string GetVoiceVoxAppVersionStatus() => Gui.Format("ModUi/&SpeechVoiceVoxAppVersion",
        InstalledVoiceVoxAppVersion ?? Gui.Localize("ModUi/&SpeechVoiceVoxVersionUnknown"));

    internal static string GetVoiceVoxUpdateStatus() => GetVoiceVoxUpdateStatus(
        ConnectedVoiceVoxVersion, LatestVoiceVoxVersion, VoiceVoxUpdateFailed, "SpeechVoiceVox");

    internal static string GetVoiceVoxAppUpdateStatus() => GetVoiceVoxUpdateStatus(
        InstalledVoiceVoxAppVersion, LatestVoiceVoxAppVersion, VoiceVoxAppUpdateFailed, "SpeechVoiceVoxApp");

    private static string GetVoiceVoxUpdateStatus(string installed, string newest, bool failed, string keyPrefix)
    {
        if (VoiceVoxUpdateInProgress)
        {
            return Gui.Localize("ModUi/&" + keyPrefix + "UpdateChecking");
        }
        if (failed)
        {
            return Gui.Localize("ModUi/&" + keyPrefix + "UpdateFailed");
        }
        if (newest == null)
        {
            return Gui.Localize("ModUi/&" + keyPrefix + "UpdateNotChecked");
        }
        if (!TryParseVoiceVoxVersion(installed, out var connected) ||
            !TryParseVoiceVoxVersion(newest, out var latest))
        {
            return Gui.Format("ModUi/&" + keyPrefix + "LatestVersion", newest);
        }
        return connected < latest || connected == latest && installed.Contains("-")
            ? Gui.Format("ModUi/&" + keyPrefix + "UpdateAvailable", newest, installed)
            : Gui.Format("ModUi/&" + keyPrefix + "CurrentVersion", installed);
    }

    internal static void CheckVoiceVoxUpdates() => _ = RefreshVoiceVoxUpdateAsync(true);

    private static async Task RefreshVoiceVoxUpdateAsync(bool force)
    {
        if (Unloading || !Main.Settings.EnableVoiceVoxSpeech || VoiceVoxUpdateInProgress ||
            !force && DateTime.UtcNow - VoiceVoxUpdateCheckedAt <
            (VoiceVoxUpdateFailed || VoiceVoxAppUpdateFailed ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(6)))
        {
            return;
        }
        var generation = ++VoiceVoxUpdateGeneration;
        var cancellation = new CancellationTokenSource();
        VoiceVoxUpdateCancellation = cancellation;
        VoiceVoxUpdateInProgress = true;
        InstalledVoiceVoxAppVersion = FindInstalledVoiceVoxAppVersion();
        try
        {
            // Network/JSON work runs off-thread; the caller's Unity context owns published UI state.
            var releases = await Task.Run(() => Task.WhenAll(
                ReadVoiceVoxReleaseResultAsync(false, cancellation.Token),
                ReadVoiceVoxReleaseResultAsync(true, cancellation.Token)));
            if (generation == VoiceVoxUpdateGeneration && !Unloading && Main.Settings.EnableVoiceVoxSpeech)
            {
                VoiceVoxUpdateFailed = releases[0].Version == null;
                VoiceVoxAppUpdateFailed = releases[1].Version == null;
                LatestVoiceVoxVersion = releases[0].Version ?? LatestVoiceVoxVersion;
                if (releases[0].Version != null)
                {
                    LatestVoiceVoxPackage = releases[0].Package;
                }
                LatestVoiceVoxAppVersion = releases[1].Version ?? LatestVoiceVoxAppVersion;
            }
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            // Disabling the engine or unloading invalidates this request's UI result.
        }
        catch (Exception)
        {
            if (generation == VoiceVoxUpdateGeneration && !Unloading)
            {
                VoiceVoxUpdateFailed = true;
                VoiceVoxAppUpdateFailed = true;
            }
        }
        finally
        {
            if (generation == VoiceVoxUpdateGeneration)
            {
                VoiceVoxUpdateInProgress = false;
                VoiceVoxUpdateCancellation = null;
                VoiceVoxUpdateCheckedAt = DateTime.UtcNow;
            }
            cancellation.Dispose();
        }
    }

    private sealed class VoiceVoxReleaseResult
    {
        internal string Version;
        internal VoiceVoxPackage Package;
    }

    private sealed class VoiceVoxPackage
    {
        internal string Version, ReleaseTag, Name, Url, Sha256;
        internal long Size, AssetId;
    }

    private static async Task<VoiceVoxReleaseResult> ReadVoiceVoxReleaseResultAsync(
        bool desktop, CancellationToken cancellation)
    {
        try
        {
            var payload = await ReadVoiceVoxReleaseMetadataAsync(desktop, cancellation).ConfigureAwait(false);
            var result = new VoiceVoxReleaseResult { Version = ParseVoiceVoxLatestRelease(payload) };
            if (!desktop)
            {
                try { result.Package = ParseVoiceVoxPackage(payload); }
                catch (InvalidDataException) { /* A version can be checked without an installable CPU asset. */ }
            }
            return result;
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return new VoiceVoxReleaseResult();
        }
    }

    private static async Task<string> ReadVoiceVoxReleaseMetadataAsync(bool desktop, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var request = (HttpWebRequest)WebRequest.Create(desktop ? VoiceVoxAppReleaseApi : VoiceVoxReleaseApi);
        request.Method = "GET";
        request.UserAgent = "SolastaUnfinishedBusiness-VoiceVoxVersionCheck";
        request.Accept = "application/vnd.github+json";
        request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        request.AllowAutoRedirect = false;
        request.Proxy = null;
        request.Timeout = request.ReadWriteTimeout = 10000;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(10000);
        using var abort = timeout.Token.Register(request.Abort);
        using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > 1024 * 1024)
        {
            throw new InvalidDataException("Invalid official VOICEVOX release response.");
        }
        using var stream = response.GetResponseStream();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > 1024 * 1024)
            {
                throw new InvalidDataException("VOICEVOX release metadata exceeds its allowed size.");
            }
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string ParseVoiceVoxLatestRelease(string payload)
    {
        var release = JObject.Parse(payload);
        var tag = release["tag_name"]?.Value<string>();
        if (release["draft"]?.Value<bool>() != false || release["prerelease"]?.Value<bool>() != false ||
            tag == null || !Regex.IsMatch(tag, @"\Av?[0-9]+\.[0-9]+\.[0-9]+\z") ||
            !TryParseVoiceVoxVersion(tag, out var version))
        {
            throw new InvalidDataException("The official VOICEVOX release is not a stable engine version.");
        }
        return version.ToString(3);
    }

    private static VoiceVoxPackage ParseVoiceVoxPackage(string payload)
    {
        var version = ParseVoiceVoxLatestRelease(payload);
        var release = JObject.Parse(payload);
        var name = "voicevox_engine-windows-cpu-" + version + ".vvpp";
        var assets = (release["assets"] as JArray)?.OfType<JObject>()
            .Where(asset => asset["name"]?.Value<string>() == name).ToArray();
        if (assets == null || assets.Length != 1 || assets[0]["state"]?.Value<string>() != "uploaded" ||
            assets[0]["size"]?.Type != JTokenType.Integer || assets[0]["id"]?.Type != JTokenType.Integer)
        {
            throw new InvalidDataException("The official release has no unique uploaded Windows CPU VVPP.");
        }
        var asset = assets[0];
        var digest = asset["digest"]?.Value<string>();
        if (digest == null || !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
        {
            throw new InvalidDataException("The official CPU package has no SHA-256 digest.");
        }
        var package = new VoiceVoxPackage
        {
            Version = version, ReleaseTag = release["tag_name"].Value<string>(), Name = name,
            Url = asset["browser_download_url"]?.Value<string>(), Sha256 = digest.Substring(7).ToLowerInvariant(),
            Size = asset["size"].Value<long>(), AssetId = asset["id"].Value<long>()
        };
        ValidateVoiceVoxPackage(package);
        return package;
    }

    private static void ValidateVoiceVoxPackage(VoiceVoxPackage package)
    {
        if (package == null)
        {
            throw new InvalidDataException("A verified VOICEVOX CPU package is required.");
        }
        if (!TryParseVoiceVoxVersion(package.Version, out var version) || version.ToString(3) != package.Version ||
            package.ReleaseTag != package.Version && package.ReleaseTag != "v" + package.Version ||
            package.Name != "voicevox_engine-windows-cpu-" + package.Version + ".vvpp" ||
            package.Size < 1024 * 1024 || package.Size > VoiceVoxMaximumPackageSize || package.AssetId <= 0 ||
            package.Sha256 == null || !Regex.IsMatch(package.Sha256, @"\A[0-9a-fA-F]{64}\z") ||
            !Uri.TryCreate(package.Url, UriKind.Absolute, out var url) ||
            url.AbsoluteUri != "https://github.com/VOICEVOX/voicevox_engine/releases/download/" +
            package.ReleaseTag + "/" + package.Name)
        {
            throw new InvalidDataException("The official CPU package identity is invalid.");
        }
    }

    private static bool IsVoiceVoxDownloadUriAllowed(Uri url) => url != null && url.IsAbsoluteUri &&
        url.Scheme == Uri.UriSchemeHttps && url.Port == 443 && string.IsNullOrEmpty(url.UserInfo) &&
        string.IsNullOrEmpty(url.Fragment) && (url.Host == "github.com" ||
                                               url.Host == "release-assets.githubusercontent.com");

    private static async Task<VoiceVoxPackage> ReadVoiceVoxLatestPackageAsync(CancellationToken cancellation) =>
        ParseVoiceVoxPackage(await ReadVoiceVoxReleaseMetadataAsync(false, cancellation).ConfigureAwait(false));

    private static bool TryParseVoiceVoxVersion(string value, out Version version)
    {
        version = null;
        var match = value != null && value.Length <= 64 ? VoiceVoxVersionPattern.Match(value) : Match.Empty;
        return match.Success && Version.TryParse(match.Groups[1].Value, out version);
    }

    private static string FindInstalledVoiceVoxAppVersion()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VOICEVOX", "VOICEVOX.exe")
        };
        try
        {
            foreach (var process in Process.GetProcessesByName("VOICEVOX"))
            {
                using (process)
                {
                    try
                    {
                        candidates.Add(process.MainModule?.FileName);
                    }
                    catch (Exception)
                    {
                        // A different user's or an exiting process may not expose its module path.
                    }
                }
            }
        }
        catch (Exception)
        {
            // The normal installation locations remain available without process enumeration.
        }
        foreach (var path in candidates.Where(candidate => !string.IsNullOrEmpty(candidate))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                var info = FileVersionInfo.GetVersionInfo(path);
                if (string.Equals(info.ProductName, "VOICEVOX", StringComparison.OrdinalIgnoreCase) &&
                    Version.TryParse(info.ProductVersion, out var version) && version.Build >= 0)
                {
                    return version.ToString(3);
                }
            }
            catch (Exception)
            {
                // An unreadable desktop installation is unknown, independently of the engine API.
            }
        }
        return null;
    }

    private static async Task<string> ReadVoiceVoxEngineVersionAsync()
    {
        try
        {
            var bytes = await SendVoiceVoxRequestAsync("version", null, null, 1500);
            var token = JToken.Parse(Encoding.UTF8.GetString(bytes));
            var version = token.Type == JTokenType.String ? token.Value<string>() : null;
            return TryParseVoiceVoxVersion(version, out _) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CancelVoiceVoxUpdateCheck()
    {
        VoiceVoxUpdateGeneration++;
        VoiceVoxUpdateInProgress = false;
        var cancellation = VoiceVoxUpdateCancellation;
        VoiceVoxUpdateCancellation = null;
        cancellation?.Cancel();
    }

    internal static string GetVoiceVoxSetupStatus()
    {
        return VoiceVoxSetup switch
        {
            VoiceVoxSetupState.CheckingRelease => Gui.Localize("ModUi/&SpeechVoiceVoxUpdateChecking"),
            VoiceVoxSetupState.Downloading => Gui.Format("ModUi/&SpeechVoiceVoxDownloadProgress",
                (Interlocked.Read(ref VoiceVoxDownloadedBytes) /
                 (double)Math.Max(1, Interlocked.Read(ref VoiceVoxDownloadTotalBytes))).ToString("P1")),
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
        if (VoiceVoxSetup == VoiceVoxSetupState.Failed)
        {
            return Gui.Localize("ModUi/&SpeechVoiceVoxRetry");
        }
        if (LatestVoiceVoxPackage == null)
        {
            return Gui.Localize(IsManagedVoiceVoxInstalled
                ? "ModUi/&SpeechVoiceVoxCheckAndUpdate" : "ModUi/&SpeechVoiceVoxCheckAndInstall");
        }
        return Gui.Format(IsManagedVoiceVoxInstalled ? "ModUi/&SpeechVoiceVoxUpdateManaged" :
            "ModUi/&SpeechVoiceVoxInstall", LatestVoiceVoxPackage.Version);
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
            VoiceVoxSetup = VoiceVoxSetupState.CheckingRelease;
            var package = await Task.Run(() => ReadVoiceVoxLatestPackageAsync(cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (Unloading) throw new OperationCanceledException(cancellation.Token);
            // This continuation publishes release state on Unity's calling context.
            LatestVoiceVoxPackage = package;
            LatestVoiceVoxVersion = package.Version;
            VoiceVoxUpdateFailed = false;
            installed = await Task.Run(() => InstallVoiceVoxAsync(package, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            VoiceVoxSetup = VoiceVoxSetupState.Idle;
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
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
            bool restartManagedEngine;
            lock (VoiceVoxRuntimeLock)
            {
                restartManagedEngine = ManagedVoiceVoxProcess != null;
            }
            if (restartManagedEngine)
            {
                VoiceVoxSessionVersion++;
                CancelVoiceVoxSpeech();
                StopManagedVoiceVox();
            }
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

    private static async Task<bool> InstallVoiceVoxAsync(VoiceVoxPackage package, CancellationToken cancellation)
    {
        ValidateVoiceVoxPackage(package);
        cancellation.ThrowIfCancellationRequested();
        if (IsManagedVoiceVoxPackageReady(package))
        {
            return false;
        }

        var destination = Path.Combine(VoiceVoxRuntimeFolder, package.Version);
        if (Directory.Exists(destination) && !IsOwnedVoiceVoxFolder(destination))
        {
            throw new IOException("The VOICEVOX destination already contains an unowned installation.");
        }
        Directory.CreateDirectory(VoiceVoxRuntimeFolder);
        var archivePath = Path.Combine(VoiceVoxRuntimeFolder,
            package.Version + "-" + package.Sha256.Substring(0, 12) + ".vvpp.download");
        await DownloadVoiceVoxArchiveAsync(archivePath, package, cancellation).ConfigureAwait(false);
        VoiceVoxSetup = VoiceVoxSetupState.Verifying;
        if (!VerifyVoiceVoxArchive(archivePath, package.Sha256, cancellation))
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

            if (!File.Exists(Path.Combine(stage, "run.exe")))
            {
                throw new InvalidDataException("The downloaded archive has no Windows engine executable.");
            }
            cancellation.ThrowIfCancellationRequested();
            // The verified official archive owns the manifest identity. Keep that
            // identity in its receipt instead of pinning an engine UUID in source.
            var marker = CreateVoiceVoxManagedMarker(package, Path.Combine(stage, "engine_manifest.json"));
            File.WriteAllText(Path.Combine(stage, ManagedVoiceVoxMarker), marker.ToString());

            // Rename within the same parent. If promotion fails, put the owned old folder back.
            string previous = null;
            if (Directory.Exists(destination))
            {
                if (!IsOwnedVoiceVoxFolder(destination))
                {
                    throw new IOException("The VOICEVOX destination already contains an unowned installation.");
                }
                previous = Path.Combine(VoiceVoxRuntimeFolder, ".previous-" + Guid.NewGuid().ToString("N"));
                Directory.Move(destination, previous);
            }
            try { Directory.Move(stage, destination); }
            catch
            {
                if (previous != null) Directory.Move(previous, destination);
                throw;
            }
            if (previous != null)
            {
                // A running engine can hold a previous file open. Keep its backup
                // rather than turning a completed atomic promotion into a failed install.
                try { RemoveVoiceVoxStage(previous); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            File.Delete(archivePath);
        }
        finally
        {
            RemoveVoiceVoxStage(stage);
        }
        return true;
    }

    private static async Task DownloadVoiceVoxArchiveAsync(string path, VoiceVoxPackage package, CancellationToken cancellation)
    {
        Interlocked.Exchange(ref VoiceVoxDownloadTotalBytes, package.Size);
        VoiceVoxSetup = VoiceVoxSetupState.Downloading;
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (existing > package.Size)
        {
            File.Delete(path);
            existing = 0;
        }
        Interlocked.Exchange(ref VoiceVoxDownloadedBytes, existing);
        if (existing == package.Size)
        {
            return;
        }

        HttpWebRequest request = null;
        HttpWebResponse response = null;
        // HttpWebRequest.Timeout does not apply to async I/O. Bound stalled headers/reads,
        // while preserving the downloaded prefix for the next attempt.
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        idleTimeout.CancelAfter(30000);
        using (idleTimeout.Token.Register(() => request?.Abort()))
        {
            try
            {
                var address = new Uri(package.Url);
                for (var redirect = 0; ; redirect++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!IsVoiceVoxDownloadUriAllowed(address))
                        throw new InvalidDataException("The VOICEVOX download left its allowed HTTPS hosts.");
                    request = (HttpWebRequest)WebRequest.Create(address);
                    request.UserAgent = "SolastaUnfinishedBusiness";
                    request.AllowAutoRedirect = false;
                    request.Timeout = request.ReadWriteTimeout = 30000;
                    if (existing > 0) request.AddRange(existing);
                    idleTimeout.CancelAfter(30000);
                    response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
                    if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) break;
                    var location = response.GetResponseHeader("Location");
                    response.Dispose();
                    response = null;
                    if (redirect >= 5 || !Uri.TryCreate(address, location, out address))
                        throw new InvalidDataException("The VOICEVOX download returned an invalid redirect.");
                }
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var expectedPrefix = "bytes " + existing.ToString(CultureInfo.InvariantCulture) + "-";
                    var range = response.Headers["Content-Range"];
                    if (range == null || !range.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                        !range.EndsWith("/" + package.Size.ToString(CultureInfo.InvariantCulture),
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
                    if (output.Length + count > package.Size)
                    {
                        throw new InvalidDataException("The VOICEVOX download exceeded its expected size.");
                    }
                    await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                    Interlocked.Exchange(ref VoiceVoxDownloadedBytes, output.Length);
                }
                if (output.Length != package.Size)
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
            finally { response?.Dispose(); }
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

    private static JObject CreateVoiceVoxManagedMarker(VoiceVoxPackage package, string manifestPath)
    {
        ValidateVoiceVoxPackage(package);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = JObject.Parse(Encoding.UTF8.GetString(manifestBytes));
        if (manifest["version"]?.Value<string>() != package.Version ||
            !Guid.TryParse(manifest["uuid"]?.Value<string>(), out var uuid) || uuid == Guid.Empty)
        {
            throw new InvalidDataException("The verified archive has an invalid engine manifest identity.");
        }
        using var hash = SHA256.Create();
        return new JObject
        {
            ["schemaVersion"] = 2, ["owner"] = "SolastaUnfinishedBusiness",
            ["version"] = package.Version, ["sha256"] = package.Sha256,
            ["releaseTag"] = package.ReleaseTag, ["assetName"] = package.Name,
            ["downloadUrl"] = package.Url, ["size"] = package.Size, ["assetId"] = package.AssetId,
            ["engineUuid"] = uuid.ToString("D"),
            ["manifestSha256"] = BitConverter.ToString(hash.ComputeHash(manifestBytes)).Replace("-", string.Empty)
        };
    }

    private static bool IsOwnedVoiceVoxFolder(string folder)
    {
        try
        {
            var markerPath = Path.Combine(folder, ManagedVoiceVoxMarker);
            var manifestPath = Path.Combine(folder, "engine_manifest.json");
            if (new[] { folder, markerPath, manifestPath }.Any(path =>
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) return false;
            var marker = JObject.Parse(File.ReadAllText(markerPath));
            var version = marker["version"]?.Value<string>();
            if (marker["schemaVersion"]?.Value<int>() != 2 ||
                marker["owner"]?.Value<string>() != "SolastaUnfinishedBusiness" ||
                !TryParseVoiceVoxVersion(version, out var parsed) || parsed.ToString(3) != version ||
                Path.GetFileName(folder) != version ||
                !Guid.TryParse(marker["engineUuid"]?.Value<string>(), out var uuid) || uuid == Guid.Empty)
            {
                return false;
            }
            ValidateVoiceVoxPackage(new VoiceVoxPackage
            {
                Version = version, Sha256 = marker["sha256"]?.Value<string>(), ReleaseTag = marker["releaseTag"]?.Value<string>(),
                Name = marker["assetName"]?.Value<string>(), Url = marker["downloadUrl"]?.Value<string>(),
                Size = marker["size"]?.Value<long>() ?? 0, AssetId = marker["assetId"]?.Value<long>() ?? 0
            });
            var manifestHash = marker["manifestSha256"]?.Value<string>();
            if (manifestHash == null || !Regex.IsMatch(manifestHash, @"\A[0-9a-fA-F]{64}\z") ||
                !VerifyVoiceVoxArchive(manifestPath, manifestHash, CancellationToken.None)) return false;
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            return manifest["version"]?.Value<string>() == version &&
                   Guid.TryParse(manifest["uuid"]?.Value<string>(), out var actualUuid) && actualUuid == uuid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetManagedVoiceVoxExecutable(out string path)
    {
        if (Directory.Exists(VoiceVoxRuntimeFolder))
        {
            foreach (var folder in Directory.EnumerateDirectories(VoiceVoxRuntimeFolder)
                         .Select(folder => new { Folder = folder,
                             Version = TryParseVoiceVoxVersion(Path.GetFileName(folder), out var version) ? version : null })
                         .Where(item => item.Version != null).OrderByDescending(item => item.Version))
            {
                if (IsManagedVoiceVoxFolderReady(folder.Folder, out path))
                {
                    return true;
                }
            }
        }
        path = null;
        return false;
    }

    private static bool IsManagedVoiceVoxPackageReady(VoiceVoxPackage package)
    {
        var folder = Path.Combine(VoiceVoxRuntimeFolder, package.Version);
        if (!IsManagedVoiceVoxFolderReady(folder, out _)) return false;
        try
        {
            var marker = JObject.Parse(File.ReadAllText(Path.Combine(folder, ManagedVoiceVoxMarker)));
            return string.Equals(marker["sha256"]?.Value<string>(), package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static bool IsManagedVoiceVoxFolderReady(string folder, out string path)
    {
        path = Path.Combine(folder, "run.exe");
        return CanInstallVoiceVox && File.Exists(path) && IsOwnedVoiceVoxFolder(folder);
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
        string expectedUuid, expectedVersion;
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
                var marker = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable), ManagedVoiceVoxMarker)));
                expectedUuid = marker["engineUuid"].Value<string>();
                expectedVersion = marker["version"].Value<string>();
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
                            WorkingDirectory = Path.GetDirectoryName(executable),
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
                    var actualVersion = await ReadVoiceVoxEngineVersionAsync();
                    if (!Guid.TryParse(manifest["uuid"]?.Value<string>(), out var actualUuid) ||
                        actualUuid.ToString("D") != expectedUuid || actualVersion != expectedVersion)
                    {
                        throw new InvalidDataException("The local engine does not match its verified installation receipt.");
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
        CancelVoiceVoxUpdateCheck();
        ConnectedVoiceVoxVersion = null;
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
