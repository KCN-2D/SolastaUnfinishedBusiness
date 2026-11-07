using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit.Utility;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Models;

internal static class UpdateContext
{
    private static readonly string TempFolder = $"TEMP_UPDATE{Path.DirectorySeparatorChar}";
    private static readonly string ModFolder = $"SolastaUnfinishedBusiness{Path.DirectorySeparatorChar}";
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();
    private static InfoJson Info { get; set; }
    private static string BaseURL { get; set; }
    private static string VersionURL { get; set; }
    private static string InstalledVersion { get; set; }
    private static string LatestVersion { get; set; }
    private static string PreviousVersion { get; set; }
    internal static bool InProgress { get; private set; }
    internal static int Progress { get; private set; }

    private static UpdateDispatcher Dispatcher { get; set; }
    private static WebClient VersionWebClient { get; set; }
    private static WebClient UpdateWebClient { get; set; }
    private static DownloadStringCompletedEventHandler VersionDownloadCompletedHandler { get; set; }
    private static DownloadProgressChangedEventHandler UpdateProgressChangedHandler { get; set; }
    private static AsyncCompletedEventHandler UpdateDownloadCompletedHandler { get; set; }
    private static int UpdateGeneration { get; set; }
    private static bool Unloading { get; set; }

    private static bool ShouldUpdate;

    private static CancellationTokenSource _characterModelDownloadCancellation;
    private static readonly object CharacterModelDownloadGate = new();
    private static readonly SemaphoreSlim CharacterModelContentGate = new(1, 1);
    private static long _characterModelDownloadBytes, _characterModelDownloadTotal;
    private static volatile bool _characterModelsDownloading;
    internal static bool CharacterModelsDownloading => _characterModelsDownloading;
    internal static int CharacterModelsDownloadProgress => (int)Math.Min(100,
        100d * Interlocked.Read(ref _characterModelDownloadBytes) /
        Math.Max(1, Interlocked.Read(ref _characterModelDownloadTotal)));

    internal static void CancelCharacterModelDownload()
    {
        lock (CharacterModelDownloadGate) _characterModelDownloadCancellation?.Cancel();
    }

    private static string ContentSha(string path)
    {
        using var input = File.OpenRead(path);
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    private static void AssertContentLeaf(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.EndsWith(" "))
            throw new InvalidDataException("Invalid character content filename.");
    }

    private static void AssertContentPath(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Character content traverses a reparse point.");
    }

    private static string[] OwnedCharacterContentEntries(string directory)
    {
        AssertContentPath(directory);
        var marker = JObject.Parse(File.ReadAllText(Path.Combine(directory, "ContentOwner.json")));
        if ((string)marker["kind"] != "SolastaUnfinishedBusiness.CharacterModels" || (int?)marker["schemaVersion"] != 1)
            throw new InvalidDataException("Unknown character content directory.");
        var names = new HashSet<string>(((JArray)marker["files"]).Values<string>(), StringComparer.OrdinalIgnoreCase)
            { "ContentOwner.json", "ContentOwner.writing", "CharacterModels.manifest.json", "CharacterModels.packs.json", "Download.partial" };
        foreach (var name in names) AssertContentLeaf(name);
        var entries = Directory.GetFileSystemEntries(directory);
        foreach (var path in entries)
            if (!File.Exists(path) || !names.Contains(Path.GetFileName(path)) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unowned files occupy a character content directory.");
        return entries;
    }

    private static void RemoveOwnedCharacterContent(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in OwnedCharacterContentEntries(directory)) File.Delete(path);
        Directory.Delete(directory);
    }

    private static void WriteCharacterContentOwner(string directory, JObject value)
    {
        var path = Path.Combine(directory, "ContentOwner.json");
        var temporary = Path.Combine(directory, "ContentOwner.writing");
        File.WriteAllText(temporary, value.ToString());
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private static void DownloadCharacterModelPack(Uri uri, string path, long bytes, CancellationToken cancellation)
    {
        var request = (HttpWebRequest)WebRequest.Create(uri);
        request.UserAgent = "SolastaUnfinishedBusiness";
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;
        using var registration = cancellation.Register(request.Abort);
        using var response = request.GetResponse();
        using var input = response.GetResponseStream();
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 131072);
        var buffer = new byte[131072];
        long total = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            total += count;
            if (total > bytes) throw new InvalidDataException("A character content pack exceeds its declared size.");
            output.Write(buffer, 0, count);
            Interlocked.Add(ref _characterModelDownloadBytes, count);
        }
        if (total != bytes) throw new InvalidDataException("A character content pack is incomplete.");
    }

    // Runs on the catalog worker. Verified content survives UMM updates beside
    // the mod directory, and one complete generation is exposed to rendering.
    internal static string PrepareCharacterModelFiles(string directory, CancellationToken cancellation)
    {
        if (!File.Exists(Path.Combine(directory, "CharacterModels.packs.json"))) return directory;
        CancellationTokenSource source;
        lock (CharacterModelDownloadGate)
        {
            if (Unloading) throw new OperationCanceledException();
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _characterModelDownloadCancellation = source;
        }
        var entered = false;
        try
        {
            CharacterModelContentGate.Wait(source.Token);
            entered = true;
            return InstallCharacterModelFiles(directory, source.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            if (source.IsCancellationRequested) throw new OperationCanceledException(source.Token);
            QueueMainThread(() => ShowMessage(
                Gui.Format("ModUi/&CharacterModelsDownloadFailed", exception.Message), "Message/&MessageOkTitle",
                title: "Setting/&EnableCustomFacesTitle", severity: MessageModal.Severity.Serious3), UpdateGeneration);
            throw;
        }
        finally
        {
            if (entered)
            {
                _characterModelsDownloading = false;
                CharacterModelContentGate.Release();
            }
            lock (CharacterModelDownloadGate)
            {
                if (ReferenceEquals(_characterModelDownloadCancellation, source)) _characterModelDownloadCancellation = null;
                source.Dispose();
            }
        }
    }

    private static string InstallCharacterModelFiles(string directory, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        const string catalogName = "CharacterModels.manifest.json";
        const string metadataName = "CharacterModels.packs.json";
        directory = Path.GetFullPath(directory);
        AssertContentPath(directory);
        var metadataPath = Path.Combine(directory, metadataName);
        var specification = JObject.Parse(File.ReadAllText(metadataPath));
        var identity = ContentSha(metadataPath);
        var catalog = JObject.Parse(File.ReadAllText(Path.Combine(directory, catalogName)));
        if ((int?)specification["schemaVersion"] != 1 || (int?)catalog["schemaVersion"] != 3 ||
            ContentSha(Path.Combine(directory, catalogName)) != (string)specification["catalogSha256"])
            throw new InvalidDataException("Character content metadata and catalog differ.");
        var files = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var declarations = ((JArray)catalog["bundles"]).OfType<JObject>()
            .ToDictionary(row => (string)row["file"], row => (string)row["sha256"], StringComparer.OrdinalIgnoreCase);
        foreach (var row in ((JArray)specification["files"]).OfType<JObject>())
        {
            var name = (string)row["file"];
            AssertContentLeaf(name);
            if (!name.EndsWith(".bundle", StringComparison.Ordinal) || (long?)row["bytes"] is not > 0 ||
                !declarations.TryGetValue(name, out var hash) || hash != (string)row["sha256"] ||
                !Regex.IsMatch(hash, "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid character content declaration.");
            files.Add(name, row);
        }
        if (files.Count != declarations.Count || files.Count == 0)
            throw new InvalidDataException("Incomplete character content declarations.");
        var packs = ((JArray)specification["packs"]).OfType<JObject>().ToArray();
        var packedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            AssertContentLeaf((string)pack["file"]);
            if (!Regex.IsMatch((string)pack["file"], "^CharacterModels-[0-9]{2}\\.zip$") ||
                (long?)pack["bytes"] is not > 0 || (long?)pack["bytes"] > 1024L * 1024 * 1024 ||
                !Regex.IsMatch((string)pack["sha256"] ?? "", "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid character content pack.");
            foreach (var name in ((JArray)pack["files"]).Values<string>())
                if (!files.ContainsKey(name) || !packedFiles.Add(name))
                    throw new InvalidDataException("Missing or duplicate packed character file.");
        }
        if (!packedFiles.SetEquals(files.Keys)) throw new InvalidDataException("The character pack closure is incomplete.");
        var baseUri = new Uri((string)specification["baseUrl"], UriKind.Absolute);
        if (baseUri.Scheme != Uri.UriSchemeHttps || baseUri.Host != "github.com" ||
            !baseUri.AbsolutePath.StartsWith("/KCN-2D/SolastaUnfinishedBusiness/releases/download/", StringComparison.Ordinal) ||
            !baseUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal) || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid character content release address.");
        var modDirectory = Directory.GetParent(Directory.GetParent(directory)!.FullName)!.FullName;
        var cacheRoot = Path.Combine(Directory.GetParent(modDirectory)!.FullName, "SolastaUnfinishedBusinessContent");
        var installed = Path.Combine(cacheRoot, "CharacterModels");
        var staging = Path.Combine(cacheRoot, "CharacterModels.installing");
        var previous = Path.Combine(cacheRoot, "CharacterModels.previous");
        AssertContentPath(cacheRoot);
        var owner = new JObject { ["schemaVersion"] = 1, ["kind"] = "SolastaUnfinishedBusiness.CharacterModels",
            ["distributionSha256"] = identity, ["files"] = new JArray(files.Keys), ["completed"] = false };
        bool Complete(string folder)
        {
            var path = Path.Combine(folder, "ContentOwner.json");
            return File.Exists(path) && JObject.Parse(File.ReadAllText(path)) is var state &&
                   (string)state["kind"] == "SolastaUnfinishedBusiness.CharacterModels" &&
                   (string)state["distributionSha256"] == identity && (bool?)state["completed"] == true &&
                   File.Exists(Path.Combine(folder, catalogName)) &&
                   ContentSha(Path.Combine(folder, catalogName)) == (string)specification["catalogSha256"] &&
                   files.All(pair => File.Exists(Path.Combine(folder, pair.Key)) &&
                       new FileInfo(Path.Combine(folder, pair.Key)).Length == (long)pair.Value["bytes"]);
        }
        cancellation.ThrowIfCancellationRequested();
        if (Complete(installed)) return installed;
        if (!Directory.Exists(installed) && Directory.Exists(previous)) Directory.Move(previous, installed);
        if (Directory.Exists(staging))
        {
            _ = OwnedCharacterContentEntries(staging);
            var marker = JObject.Parse(File.ReadAllText(Path.Combine(staging, "ContentOwner.json")));
            if ((string)marker["distributionSha256"] != identity) RemoveOwnedCharacterContent(staging);
        }
        Directory.CreateDirectory(staging);
        WriteCharacterContentOwner(staging, owner);
        File.Copy(Path.Combine(directory, catalogName), Path.Combine(staging, catalogName), true);
        File.Copy(metadataPath, Path.Combine(staging, metadataName), true);
        bool Valid(string folder, string name) => File.Exists(Path.Combine(folder, name)) &&
            new FileInfo(Path.Combine(folder, name)).Length == (long)files[name]["bytes"] &&
            ContentSha(Path.Combine(folder, name)) == (string)files[name]["sha256"];
        foreach (var pair in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var destination = Path.Combine(staging, pair.Key);
            AssertContentPath(destination);
            if (Valid(staging, pair.Key)) continue;
            if (File.Exists(destination)) File.Delete(destination);
            foreach (var seed in new[] { directory, installed })
            {
                AssertContentPath(Path.Combine(seed, pair.Key));
                if (!Valid(seed, pair.Key)) continue;
                File.Copy(Path.Combine(seed, pair.Key), destination);
                break;
            }
        }
        _characterModelsDownloading = true;
        Interlocked.Exchange(ref _characterModelDownloadBytes, 0);
        Interlocked.Exchange(ref _characterModelDownloadTotal, packs.Sum(pack => (long)pack["bytes"]));
        var download = Path.Combine(staging, "Download.partial");
        try
        {
            foreach (var pack in packs)
            {
                cancellation.ThrowIfCancellationRequested();
                var expected = new HashSet<string>(((JArray)pack["files"]).Values<string>(), StringComparer.OrdinalIgnoreCase);
                if (expected.All(name => Valid(staging, name)))
                {
                    Interlocked.Add(ref _characterModelDownloadBytes, (long)pack["bytes"]);
                    continue;
                }
                DownloadCharacterModelPack(new Uri(baseUri, (string)pack["file"]), download, (long)pack["bytes"], cancellation);
                if (ContentSha(download) != (string)pack["sha256"]) throw new InvalidDataException("Character pack checksum differs.");
                using (var archive = ZipFile.OpenRead(download))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in archive.Entries)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        AssertContentLeaf(entry.FullName);
                        if (!expected.Contains(entry.FullName) || !seen.Add(entry.FullName) ||
                            entry.Length != (long)files[entry.FullName]["bytes"])
                            throw new InvalidDataException("Unexpected character pack entry.");
                        if (Valid(staging, entry.FullName)) continue;
                        var destination = Path.Combine(staging, entry.FullName);
                        if (File.Exists(destination)) File.Delete(destination);
                        using var input = entry.Open();
                        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        var buffer = new byte[131072];
                        long written = 0;
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            written += count;
                            if (written > entry.Length) throw new InvalidDataException("Oversized character pack entry.");
                            output.Write(buffer, 0, count);
                        }
                    }
                    if (!seen.SetEquals(expected)) throw new InvalidDataException("A character pack omits declared files.");
                }
                if (!expected.All(name => Valid(staging, name))) throw new InvalidDataException("Extracted character checksums differ.");
                File.Delete(download);
            }
            cancellation.ThrowIfCancellationRequested();
            owner["completed"] = true;
            WriteCharacterContentOwner(staging, owner);
            RemoveOwnedCharacterContent(previous);
            if (Directory.Exists(installed)) Directory.Move(installed, previous);
            try { Directory.Move(staging, installed); }
            catch
            {
                if (Directory.Exists(previous) && !Directory.Exists(installed)) Directory.Move(previous, installed);
                throw;
            }
            RemoveOwnedCharacterContent(previous);
            foreach (var name in files.Keys)
            {
                var legacy = Path.Combine(directory, name);
                AssertContentPath(legacy);
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            return installed;
        }
        finally { if (File.Exists(download)) File.Delete(download); }
    }

    internal static void Load()
    {
        Unloading = false;
        NextUpdateGeneration();

        EnsureDispatcher();

        var infoPayload = File.ReadAllText(Path.Combine(Main.ModFolder, "Info.json"));
        Info = JsonConvert.DeserializeObject<InfoJson>(infoPayload);

        BaseURL = Info.Repository + "/releases/download";
        VersionURL = Info.VersionURL;
        InstalledVersion = Info.Version;
        PreviousVersion = GetPreviousVersion();

        LatestVersion = InstalledVersion;
        ShouldUpdate = false;

        var displayWelcomeMessage = Main.Settings.DisplayModMessage == 0;

        // display mod message every 100 launches
        Main.Settings.DisplayModMessage = (Main.Settings.DisplayModMessage + 1) % 100;

        StartVersionCheck(displayWelcomeMessage);
    }

    private static string GetPreviousVersion()
    {
        var a1 = InstalledVersion.Split('.');
        var minor = int.Parse(a1[3]);

        a1[3] = (--minor).ToString();

        // ReSharper disable once AssignNullToNotNullAttribute
        return string.Join(".", a1);
    }

    internal static void Unload()
    {
        CancelCharacterModelDownload();
        Unloading = true;
        NextUpdateGeneration();
        InProgress = false;
        Progress = 0;

        ClearMainThreadActions();
        CancelAndDisposeVersionWebClient();
        CancelAndDisposeUpdateWebClient();

        if (Dispatcher)
        {
            if (!Main.IsApplicationQuitting)
            {
                UnityEngine.Object.Destroy(Dispatcher.gameObject);
            }

            Dispatcher = null;
        }
    }

    private static void StartVersionCheck(bool displayWelcomeMessage)
    {
        var generation = UpdateGeneration;

        CancelAndDisposeVersionWebClient();

        if (IsStale(generation) ||
            string.IsNullOrWhiteSpace(VersionURL) ||
            !Uri.TryCreate(VersionURL, UriKind.Absolute, out var versionUri))
        {
            QueueStartupMessages(displayWelcomeMessage, generation);
            return;
        }

        var webClient = new WebClient { Encoding = Encoding.UTF8 };

        VersionWebClient = webClient;
        VersionDownloadCompletedHandler = (_, e) =>
            OnVersionDownloadCompleted(webClient, displayWelcomeMessage, generation, e);
        webClient.DownloadStringCompleted += VersionDownloadCompletedHandler;

        try
        {
            webClient.DownloadStringAsync(versionUri);
        }
        catch
        {
            DisposeVersionWebClient(webClient);
            QueueStartupMessages(displayWelcomeMessage, generation);
        }
    }

    private static void OnVersionDownloadCompleted(
        WebClient webClient,
        bool displayWelcomeMessage,
        int generation,
        DownloadStringCompletedEventArgs e)
    {
        DisposeVersionWebClient(webClient);

        if (IsStale(generation) || e.Cancelled)
        {
            return;
        }

        if (e.Error == null && TryParseLatestVersion(e.Result, out var version, out var shouldUpdate))
        {
            LatestVersion = version;
            ShouldUpdate = shouldUpdate;
        }

        QueueStartupMessages(displayWelcomeMessage, generation);
    }

    private static bool TryParseLatestVersion(string infoPayload, out string version, out bool shouldUpdate)
    {
        version = InstalledVersion;
        shouldUpdate = false;

        try
        {
            var infoJson = JsonConvert.DeserializeObject<JObject>(infoPayload);

            // ReSharper disable once AssignNullToNotNullAttribute
            version = infoJson["Version"].Value<string>();

            var a1 = InstalledVersion.Split('.');
            var a2 = version.Split('.');
            var v1 = a1[0] + a1[1] + a1[2] + int.Parse(a1[3]).ToString("D3");
            var v2 = a2[0] + a2[1] + a2[2] + int.Parse(a2[3]).ToString("D3");

            shouldUpdate = string.Compare(v2, v1, StringComparison.Ordinal) > 0;

            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void UpdateMod(bool toLatest = true)
    {
        if (InProgress) { return; }

        if (!ShouldUpdate && toLatest)
        {
            ShowMessage("Mod version is already the latest or higher",
                "ChangeLog", OpenChangeLog);

            return;
        }

        InProgress = true;
        Progress = 0;

        var generation = UpdateGeneration;
        var version = toLatest ? LatestVersion : PreviousVersion;
        var zipFile = "SolastaUnfinishedBusiness.zip";
        var fullZipFile = Path.Combine(Main.ModFolder, zipFile);
        var fullZipFolder = Path.Combine(Main.ModFolder, TempFolder);
        var baseUrlByVersion = BaseURL.Replace("download", $"download/{version}");
        var url = new Uri($"{baseUrlByVersion}/{zipFile}");
        WebClient wc = null;

        try
        {
            wc = new WebClient();

            wc.Encoding = Encoding.UTF8;
            UpdateProgressChangedHandler = (_, e) =>
            {
                if (!IsStale(generation) && UpdateWebClient == wc)
                {
                    Progress = e.ProgressPercentage;
                }
            };
            wc.DownloadProgressChanged += UpdateProgressChangedHandler;

            UpdateDownloadCompletedHandler = (_, e) =>
                OnUpdateDownloadCompleted(wc, generation, url, fullZipFile, fullZipFolder, e);
            wc.DownloadFileCompleted += UpdateDownloadCompletedHandler;

            UpdateWebClient = wc;
            wc.DownloadFileAsync(url, fullZipFile);
        }
        catch (Exception ex)
        {
            DisposeUpdateWebClient(wc);
            InProgress = false;
            Main.Error($"Failed to update mod: {ex.Message}: {ex.StackTrace}");

            ShowMessage($"Cannot fetch update payload. Try again or download from:\r\n{url}.",
                "Open Download Url", () => OpenUrl(url.ToString()),
                severity: MessageModal.Severity.Serious3);
        }

    }

    private static void OnUpdateDownloadCompleted(
        WebClient webClient,
        int generation,
        Uri url,
        string fullZipFile,
        string fullZipFolder,
        AsyncCompletedEventArgs e)
    {
        if (IsStale(generation) || UpdateWebClient != webClient)
        {
            DisposeUpdateWebClient(webClient);
            InProgress = false;
            Progress = 0;
            return;
        }

        if (e.Cancelled)
        {
            DisposeUpdateWebClient(webClient);
            InProgress = false;
            Progress = 0;
            QueueMainThread(() => ShowMessage("Update was cancelled",
                "Open Download Url", () => OpenUrl(url.ToString()),
                severity: MessageModal.Severity.Serious3), generation);
            return;
        }

        if (e.Error != null)
        {
            DisposeUpdateWebClient(webClient);
            InProgress = false;
            Progress = 0;
            QueueMainThread(() => ShowMessage($"Cannot fetch update payload. Try again or download from:\r\n{url}.",
                "Open Download Url", () => OpenUrl(url.ToString()),
                severity: MessageModal.Severity.Serious3), generation);
            return;
        }

        try
        {
            if (Directory.Exists(fullZipFolder))
            {
                Directory.Delete(fullZipFolder, true);
            }

            ZipFile.ExtractToDirectory(fullZipFile, fullZipFolder);

            foreach (var sourceFile in Directory.GetFiles(fullZipFolder, "*", SearchOption.AllDirectories))
            {
                var destFile = sourceFile.ReplaceFirst(TempFolder, string.Empty);

                while (Regex.Matches(destFile, Regex.Escape(ModFolder)).Count > 1)
                {
                    destFile = destFile.ReplaceLastOccurrence(ModFolder, string.Empty);
                }

                var destFolder = Path.GetDirectoryName(destFile)!;

                Directory.CreateDirectory(destFolder);

                if (Checksum(destFile) != Checksum(sourceFile))
                {
                    File.Delete(destFile);
                    File.Move(sourceFile, destFile);
                }
            }

            QueueMainThread(() => ShowMessage("Mod update is successful. Please restart.", "ChangeLog", OpenChangeLog),
                generation);
        }
        catch (Exception err)
        {
            Main.Error($"Failed to update mod: {err.Message}: {err.StackTrace}");

            QueueMainThread(() => ShowMessage(
                $"Failed to unpack update. Try again or download and update manually from:\r\n{url}.",
                "Open Download Url", () => OpenUrl(url.ToString()),
                severity: MessageModal.Severity.Serious3), generation);
        }
        finally
        {
            DisposeUpdateWebClient(webClient);
            InProgress = false;
            Progress = 0;

            try
            {
                File.Delete(fullZipFile);
                Directory.Delete(fullZipFolder, true);
            }
            catch
            {
                /* ignored */
            }
        }
    }

    private static void DisposeVersionWebClient(WebClient webClient)
    {
        if (webClient == null)
        {
            return;
        }

        if (VersionDownloadCompletedHandler != null)
        {
            webClient.DownloadStringCompleted -= VersionDownloadCompletedHandler;
            VersionDownloadCompletedHandler = null;
        }

        if (VersionWebClient == webClient)
        {
            VersionWebClient = null;
        }

        webClient.Dispose();
    }

    private static void DisposeUpdateWebClient(WebClient webClient)
    {
        if (webClient == null)
        {
            return;
        }

        if (UpdateWebClient == webClient)
        {
            UpdateWebClient = null;
        }

        if (UpdateProgressChangedHandler != null)
        {
            webClient.DownloadProgressChanged -= UpdateProgressChangedHandler;
            UpdateProgressChangedHandler = null;
        }

        if (UpdateDownloadCompletedHandler != null)
        {
            webClient.DownloadFileCompleted -= UpdateDownloadCompletedHandler;
            UpdateDownloadCompletedHandler = null;
        }

        webClient.Dispose();
    }

    private static void CancelAndDisposeVersionWebClient()
    {
        var webClient = VersionWebClient;

        if (webClient == null)
        {
            return;
        }

        if (VersionDownloadCompletedHandler != null)
        {
            webClient.DownloadStringCompleted -= VersionDownloadCompletedHandler;
            VersionDownloadCompletedHandler = null;
        }

        if (VersionWebClient == webClient)
        {
            VersionWebClient = null;
        }

        try
        {
            webClient.CancelAsync();
        }
        finally
        {
            webClient.Dispose();
        }
    }

    private static void CancelAndDisposeUpdateWebClient()
    {
        var webClient = UpdateWebClient;

        if (webClient == null)
        {
            return;
        }

        if (UpdateProgressChangedHandler != null)
        {
            webClient.DownloadProgressChanged -= UpdateProgressChangedHandler;
            UpdateProgressChangedHandler = null;
        }

        if (UpdateDownloadCompletedHandler != null)
        {
            webClient.DownloadFileCompleted -= UpdateDownloadCompletedHandler;
            UpdateDownloadCompletedHandler = null;
        }

        if (UpdateWebClient == webClient)
        {
            UpdateWebClient = null;
        }

        try
        {
            webClient.CancelAsync();
        }
        finally
        {
            webClient.Dispose();
        }
    }

    private static void QueueStartupMessages(bool displayWelcomeMessage, int generation)
    {
        QueueMainThread(() =>
        {
            if (ShouldUpdate)
            {
                DisplayUpdateMessage();
                return;
            }

            CustomModels.AlertIfModelsNotFound();

            if (displayWelcomeMessage)
            {
                DisplayWelcomeMessage();
            }
        }, generation);
    }

    private static void QueueStartupMessages(bool displayWelcomeMessage)
    {
        QueueStartupMessages(displayWelcomeMessage, UpdateGeneration);
    }

    private static void EnsureDispatcher()
    {
        if (Dispatcher)
        {
            return;
        }

        var gameObject = new GameObject("SolastaUnfinishedBusinessUpdateContext");

        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        Dispatcher = gameObject.AddComponent<UpdateDispatcher>();
    }

    private static void QueueMainThread(Action action, int generation)
    {
        if (IsStale(generation))
        {
            return;
        }

        MainThreadActions.Enqueue(() =>
        {
            if (!IsStale(generation))
            {
                action();
            }
        });
    }

    private static void ProcessMainThreadActions()
    {
        while (MainThreadActions.TryDequeue(out var action))
        {
            if (Unloading)
            {
                continue;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Main.Error(ex);
            }
        }
    }

    private static void ClearMainThreadActions()
    {
        while (MainThreadActions.TryDequeue(out _))
        {
        }
    }

    private static int NextUpdateGeneration()
    {
        unchecked
        {
            UpdateGeneration++;
        }

        return UpdateGeneration;
    }

    private static bool IsStale(int generation)
    {
        return Unloading || generation != UpdateGeneration;
    }

    private sealed class UpdateDispatcher : MonoBehaviour
    {
        private void Update()
        {
            ProcessMainThreadActions();
        }
    }

    internal static void DisplayRollbackMessage()
    {
        if (InProgress) { return; }

        ShowMessage($"Would you like to rollback to {PreviousVersion}?",
            "Message/&MessageOkTitle", () => UpdateMod(false),
            "Message/&MessageCancelTitle");
    }

    private static void DisplayUpdateMessage()
    {
        ShowMessage($"Version {LatestVersion} is now available. Open Mod UI > Gameplay > General to update.",
            "Changelog", OpenChangeLog);
    }

    private static void DisplayWelcomeMessage()
    {
        ShowMessage("Message/&MessageModWelcomeDescription",
            "ChangeLog", OpenChangeLog);
    }

    private static void ShowMessage(
        string content,
        string validateCaption,
        [CanBeNull] MessageModal.MessageValidatedHandler onValidated = null,
        string cancelCaption = "Message/&MessageOkTitle",
        [CanBeNull] MessageModal.MessageCancelledHandler onCancelled = null,
        string title = "Message/&MessageModWelcomeTitle",
        MessageModal.Severity severity = MessageModal.Severity.Attention2
    )
    {
        onValidated ??= () => { };
        onCancelled ??= () => { };
        Gui.GuiService.ShowMessage(severity, title, content, validateCaption, cancelCaption, onValidated, onCancelled);

        UnityModManager.UI.Instance?.ToggleWindow(false);
    }

    internal static void OpenChangeLog()
    {
        OpenUrl(Info.Changelog);
    }

    internal static void OpenDocumentation(string filename)
    {
        OpenUrl($"file://{Main.ModFolder}/Documentation/{filename}");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(url);
        }
        catch
        {
            // because of this: https://github.com/dotnet/corefx/issues/10361
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                url = url.Replace("&", "^&");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                throw;
            }
        }
    }

    private static string Checksum(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var buffer = new BufferedStream(file);
        using var cryptoProvider = SHA1.Create();

        var hash = cryptoProvider.ComputeHash(buffer);

        var str = new StringBuilder();
        foreach (var b in hash)
        {
            str.Append(b.ToString("X2"));
        }

        return str.ToString();
    }
}
