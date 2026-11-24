using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Models.TranslationServices;
using UnityEngine;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness;

internal static class Main
{
    internal static readonly string ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    private static MenuManager Menu { get; set; }
    private static ModManager<Settings> Mod { get; set; }
    private static UnityModManager.ModEntry ModEntry { get; set; }

    internal static bool Enabled { get; private set; }
    internal static bool IsApplicationQuitting { get; private set; }

    internal static Action Enable { get; private set; }

    internal static string SettingsFolder => Path.Combine(ModEntry?.Path ?? ModFolder, "Settings");
    internal static string Version => ModEntry?.Info?.Version?.ToString() ?? "<unavailable>";
    internal static string[] SettingsFiles { get; private set; }
    internal static Settings Settings => Mod.Settings;
    internal static bool SettingsRestartRequired => Mod?.SettingsForSave != null;
    internal static string SettingsOperationMessage { get; private set; }
    internal static bool CanImportSettings => !Gui.GameCampaign &&
        ServiceRepository.GetService<INetworkingService>()?.InOnlineRoom != true;

    [Conditional("DEBUG")]
    internal static void Log(string msg, bool console = false)
    {
        ModEntry.Logger.Log(msg);

        if (!console)
        {
            return;
        }

        var game = Gui.Game;

        if (!game)
        {
            return;
        }

        game.GameConsole?.LogSimpleLine(msg);
    }

    internal static void Error(Exception ex)
    {
        ModEntry.Logger.Error(ex.ToString());
    }

    internal static void Error(string msg, bool stack = false)
    {
        ModEntry.Logger.Error(msg + (stack ? Environment.StackTrace : ""));
    }

    internal static void Info(string msg)
    {
        ModEntry.Logger.Log(msg);
    }

    internal static void EnsureFolderExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    [UsedImplicitly]
    internal static bool Load([NotNull] UnityModManager.ModEntry modEntry)
    {
        ModEntry = modEntry;
        IsApplicationQuitting = false;

        var startup = Stopwatch.StartNew();
        var translationsStarted = false;

        try
        {
            UmmCompatibility.LogRuntime();
            EnsureFolderExists(SettingsFolder);
            PortraitsContext.EnsureFolderExists();
            SaveByLocationContext.EnsureFoldersExist();

            var types = typeof(Main).Assembly.GetTypes();
            Mod = new ModManager<Settings>();
            Mod.Enable(modEntry, types);
            LoadingContext.Initialize();

            Enable = () =>
            {
                LoadSettingFilenames();
                Menu = new MenuManager();
                Menu.Enable(modEntry, types);

                Enabled = true;
                Enable = null;
                Info($"enabled in {startup.Elapsed}.");
            };

            translationsStarted = true;
            TranslatorContext.EarlyLoad();

            modEntry.OnUnload = OnUnload;
            Application.quitting += OnApplicationQuitting;
        }
        catch (Exception ex)
        {
            TryCleanup(LoadingContext.Unload);
            if (translationsStarted)
            {
                TryCleanup(TranslatorContext.Unload);
            }

            TryCleanup(() => Mod?.Unload(modEntry));
            Enable = null;
            Enabled = false;

            Error(ex);
            throw;
        }

        return true;
    }

    private static void OnApplicationQuitting()
    {
        if (IsApplicationQuitting)
        {
            return;
        }

        IsApplicationQuitting = true;
        StopBackgroundWork(false);
    }

    private static void StopBackgroundWork(bool destroyUnityObjects)
    {
        TryCleanup(LoadingContext.Unload);
        TryCleanup(CooperativeExplorationContext.Reset);
        TryCleanup(BootContext.Unload);
        TryCleanup(() => SpeechContext.Unload(destroyUnityObjects));
        TryCleanup(UpdateContext.Unload);
        TryCleanup(() => CampaignTranslationExecutor.Unload(destroyUnityObjects));
        TryCleanup(TranslationServiceFactory.Unload);
    }

    private static bool OnUnload(UnityModManager.ModEntry modEntry)
    {
        var applicationQuitting = IsApplicationQuitting;

        if (!applicationQuitting)
        {
            StopBackgroundWork(true);
        }

        TryCleanup(() =>
        {
            Menu?.Unload(modEntry);
            Menu = null;
        });
        Application.quitting -= OnApplicationQuitting;
        modEntry.OnUnload -= OnUnload;
        TryCleanup(() => SaveByLocationContext.Unload(!applicationQuitting));
        TryCleanup(CombatAiContext.Unload);
        TryCleanup(PlayerControllerContext.Unload);
        TryCleanup(CivilianAnimationContext.SuspendCivilianReactions);
        TryCleanup(RacesContext.UnloadBodyShapes);
        TryCleanup(PortraitsContext.RestoreViewportLighting);
        TryCleanup(CombatAnimationContext.Unload);
        TryCleanup(MultiplayerSettingsContext.Invalidate);
        TryCleanup(GuiWrapperContext.Unload);
        TryCleanup(() => Tooltips.Unload(!applicationQuitting));
        TryCleanup(CampaignTranslationRuntimeRepairContext.Unload);

        if (!applicationQuitting)
        {
            TryCleanup(TranslatorContext.Unload);
            TryCleanup(CustomModels.Unload);
            TryCleanup(Sprites.Unload);
            TryCleanup(UI.Unload);
            TryCleanup(GUIHelper.Unload);
        }

        TryCleanup(Global.ResetTransientStateForUnload);
        TryCleanup(() => Mod?.Unload(modEntry));

        Enabled = false;
        Enable = null;

        return true;
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    internal static void LoadSettingFilenames()
    {
        EnsureFolderExists(SettingsFolder);

        SettingsFiles = Directory.EnumerateFiles(SettingsFolder, "*.xml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrEmpty(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryNormalizeSettingsFilename(string filename, out string normalizedFilename)
    {
        normalizedFilename = string.Empty;

        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        var fileName = Path.GetFileName(filename.Trim());

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        fileName = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));

        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName)))
        {
            return false;
        }

        normalizedFilename = fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : $"{fileName}.xml";

        return true;
    }

    internal static void SaveSettings(string filename)
    {
        if (!TryNormalizeSettingsFilename(filename, out var normalizedFilename))
        {
            return;
        }

        try
        {
            EnsureFolderExists(SettingsFolder);
            Settings.WriteTo(Path.Combine(SettingsFolder, normalizedFilename));
            LoadSettingFilenames();
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    internal static bool SaveSharedSettingsProfile(string xml)
    {
        try
        {
            if (!MultiplayerSettingsContext.TryValidateSharedSettingsXml(xml, out var normalizedXml))
            {
                SettingsOperationMessage = "ModUi/&SettingsImportFailed";
                return false;
            }

            var profile = MultiplayerSettingsContext.ReadSettingsProfile(normalizedXml, Settings);
            EnsureFolderExists(SettingsFolder);
            profile.WriteTo(Path.Combine(SettingsFolder, "MultiplayerHost.xml"));
            LoadSettingFilenames();
            SettingsOperationMessage = "ModUi/&MultiplayerSettingsSaved";
            return true;
        }
        catch (Exception ex)
        {
            Error(ex);
            SettingsOperationMessage = "ModUi/&SettingsImportFailed";
            return false;
        }
    }

    internal static void LoadSettings(string filename)
    {
        if (!CanImportSettings)
        {
            SettingsOperationMessage = "ModUi/&SettingsImportUnavailable";
            return;
        }

        if (SettingsRestartRequired ||
            !TryNormalizeSettingsFilename(filename, out var normalizedFilename))
        {
            return;
        }

        try
        {
            var path = Path.Combine(SettingsFolder, normalizedFilename);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MultiplayerSettingsContext.MaxXmlBytes)
            {
                throw new InvalidDataException("Missing or oversized settings profile.");
            }

            var profile = MultiplayerSettingsContext.ReadSettingsProfile(File.ReadAllText(path), Settings);
            var backupPath = Path.Combine(ModEntry.Path, "Settings.before-import.xml");

            // Back up the settings actually in use, including changes made since the last UMM save.
            Settings.WriteTo(backupPath);
            profile.Save(ModEntry);
            Mod.SettingsForSave = profile;
            SettingsOperationMessage = null;
        }
        catch (Exception ex)
        {
            Error(ex);
            SettingsOperationMessage = "ModUi/&SettingsImportFailed";
        }
    }

    internal static void CancelSettingsImport()
    {
        if (!SettingsRestartRequired)
        {
            return;
        }

        try
        {
            Settings.Save(ModEntry);
            Mod.SettingsForSave = null;
            SettingsOperationMessage = null;
        }
        catch (Exception ex)
        {
            Error(ex);
            SettingsOperationMessage = "ModUi/&SettingsImportFailed";
        }
    }

    internal static void RemoveSettings(string filename)
    {
        if (!TryNormalizeSettingsFilename(filename, out var normalizedFilename))
        {
            return;
        }

        EnsureFolderExists(SettingsFolder);
        filename = Path.Combine(SettingsFolder, normalizedFilename);

        if (File.Exists(filename))
        {
            File.Delete(filename);
        }

        LoadSettingFilenames();
    }
}
