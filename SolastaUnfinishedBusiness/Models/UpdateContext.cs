using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
