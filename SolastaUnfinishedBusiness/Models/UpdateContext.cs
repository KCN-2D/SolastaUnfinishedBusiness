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
    private static bool Unloading { get; set; }

    private static bool ShouldUpdate;

    internal static void Load()
    {
        Unloading = false;
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
        InProgress = false;
        Progress = 0;

        ClearMainThreadActions();
        CancelAndDisposeWebClient(VersionWebClient);
        CancelAndDisposeWebClient(UpdateWebClient);
        VersionWebClient = null;
        UpdateWebClient = null;

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
        CancelAndDisposeWebClient(VersionWebClient);

        var webClient = new WebClient { Encoding = Encoding.UTF8 };

        VersionWebClient = webClient;
        webClient.DownloadStringCompleted += OnDownloadStringCompleted;

        try
        {
            webClient.DownloadStringAsync(new Uri(VersionURL));
        }
        catch
        {
            if (VersionWebClient == webClient)
            {
                VersionWebClient = null;
            }

            webClient.Dispose();
            QueueStartupMessages(displayWelcomeMessage);
        }

        return;

        void OnDownloadStringCompleted(object _, DownloadStringCompletedEventArgs e)
        {
            if (VersionWebClient == webClient)
            {
                VersionWebClient = null;
            }

            webClient.DownloadStringCompleted -= OnDownloadStringCompleted;
            webClient.Dispose();

            if (Unloading || e.Cancelled)
            {
                return;
            }

            if (e.Error == null && TryParseLatestVersion(e.Result, out var version, out var shouldUpdate))
            {
                LatestVersion = version;
                ShouldUpdate = shouldUpdate;
            }

            QueueStartupMessages(displayWelcomeMessage);
        }
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
            wc.DownloadProgressChanged += (_, e) => Progress = e.ProgressPercentage;

            wc.DownloadFileCompleted += OnDownloadFileCompleted;

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

        return;

        void OnDownloadFileCompleted(object _, AsyncCompletedEventArgs e)
        {
            if (Unloading)
            {
                DisposeUpdateWebClient(wc);
                InProgress = false;
                Progress = 0;
                return;
            }

            if (e.Error != null)
            {
                DisposeUpdateWebClient(wc);
                InProgress = false;
                ShowMessage($"Cannot fetch update payload. Try again or download from:\r\n{url}.",
                    "Open Download Url", () => OpenUrl(url.ToString()),
                    severity: MessageModal.Severity.Serious3);
                return;
            }

            if (e.Cancelled)
            {
                DisposeUpdateWebClient(wc);
                InProgress = false;
                ShowMessage("Update was cancelled",
                    "Open Download Url", () => OpenUrl(url.ToString()),
                    severity: MessageModal.Severity.Serious3);
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

                ShowMessage("Mod update is successful. Please restart.", "ChangeLog", OpenChangeLog);
            }
            catch (Exception err)
            {
                Main.Error($"Failed to update mod: {err.Message}: {err.StackTrace}");

                ShowMessage($"Failed to unpack update. Try again or download and update manually from:\r\n{url}.",
                    "Open Download Url", () => OpenUrl(url.ToString()),
                    severity: MessageModal.Severity.Serious3);
            }
            finally
            {
                DisposeUpdateWebClient(wc);
                InProgress = false;

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

        webClient.Dispose();
    }

    private static void CancelAndDisposeWebClient(WebClient webClient)
    {
        if (webClient == null)
        {
            return;
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

    private static void QueueStartupMessages(bool displayWelcomeMessage)
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
        });
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

    private static void QueueMainThread(Action action)
    {
        if (!Unloading)
        {
            MainThreadActions.Enqueue(action);
        }
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
