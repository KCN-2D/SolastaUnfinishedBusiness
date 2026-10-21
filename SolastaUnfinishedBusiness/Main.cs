using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;
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
    private static ModManager<Core, Settings> Mod { get; set; }
    private static UnityModManager.ModEntry ModEntry { get; set; }
    private static bool ApplicationQuitCleanupStarted { get; set; }

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
        ApplicationQuitCleanupStarted = false;

        var now = DateTime.Now;
        var assembly = Assembly.GetExecutingAssembly();
        var translationsStarted = false;

        UmmCompatibility.LogRuntime();

        try
        {
            EnsureFolderExists(SettingsFolder);
            PortraitsContext.EnsureFolderExists();
            SaveByLocationContext.EnsureFoldersExist();

            Mod = new ModManager<Core, Settings>();
            Mod.Enable(modEntry, assembly);
            LoadingContext.Initialize();

            modEntry.OnUnload = OnUnload;
            modEntry.OnShowGUI = _ =>
            {
                if (Settings.EnableHeroesControlledByComputer)
                {
                    PlayerControllerContext.RefreshGuiState();
                }
            };

            Enable = () =>
            {
                var finished = DateTime.Now;

                Menu = new MenuManager();
                Menu.Enable(modEntry, assembly);
                LoadSettingFilenames();
                ModEntry.Logger.Log($"enabled in {finished - now:T}.");

                Enabled = true;
            };

            translationsStarted = true;
            TranslatorContext.EarlyLoad();

            Application.quitting -= OnApplicationQuitting;
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
            modEntry.OnUnload = null;
            modEntry.OnShowGUI = null;
            Enable = null;
            Enabled = false;

            Error(ex);
            throw;
        }

        return true;
    }

    private static void OnApplicationQuitting()
    {
        IsApplicationQuitting = true;

        if (ApplicationQuitCleanupStarted)
        {
            return;
        }

        ApplicationQuitCleanupStarted = true;

        // Stop managed work before UMM starts tearing down Unity objects. These
        // cleanup paths are intentionally safe to invoke again from OnUnload.
        TryCleanup(LoadingContext.Unload);
        TryCleanup(BootContext.Unload);
        TryCleanup(() => SpeechContext.Unload(false));
        TryCleanup(UpdateContext.Unload);
        TryCleanup(() => CampaignTranslationExecutor.Unload(false));
        TryCleanup(TranslationServiceFactory.Unload);
    }

    private static bool OnUnload(UnityModManager.ModEntry modEntry)
    {
        var applicationQuitting = IsApplicationQuitting;

        TryCleanup(LoadingContext.Unload);
        TryCleanup(BootContext.Unload);
        TryCleanup(() =>
        {
            Menu?.Unload(modEntry);
            Menu = null;
        });
        TryCleanup(() => modEntry.OnShowGUI = null);
        TryCleanup(() => Application.quitting -= OnApplicationQuitting);
        TryCleanup(() => SpeechContext.Unload(!applicationQuitting));
        TryCleanup(UpdateContext.Unload);
        TryCleanup(() => SaveByLocationContext.Unload(!applicationQuitting));
        TryCleanup(CombatAiContext.Unload);
        TryCleanup(PlayerControllerContext.Unload);
        TryCleanup(CivilianAnimationContext.SuspendCivilianReactions);
        TryCleanup(CombatAnimationContext.Unload);
        TryCleanup(MultiplayerSettingsContext.Invalidate);
        TryCleanup(GuiWrapperContext.Unload);
        TryCleanup(() => Tooltips.Unload(!applicationQuitting));
        TryCleanup(() => CampaignTranslationExecutor.Unload(!applicationQuitting));
        TryCleanup(CampaignTranslationRuntimeRepairContext.Unload);
        TryCleanup(TranslationServiceFactory.Unload);

        if (!applicationQuitting)
        {
            TryCleanup(TranslatorContext.Unload);
            TryCleanup(CustomModels.Unload);
            TryCleanup(Sprites.Unload);
            TryCleanup(UI.Unload);
            TryCleanup(GUIHelper.Unload);
        }

        TryCleanup(Global.ResetTransientStateForUnload);
        TryCleanup(() => { Mod?.Unload(modEntry); });

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
            WriteSettingsFile(Path.Combine(SettingsFolder, normalizedFilename), Settings);
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
            WriteSettingsFile(Path.Combine(SettingsFolder, "MultiplayerHost.xml"), profile);
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
            var settingsPath = Path.Combine(ModEntry.Path, "Settings.xml");
            var backupPath = Path.Combine(ModEntry.Path, "Settings.before-import.xml");

            // Back up the settings actually in use, including changes made since the last UMM save.
            WriteSettingsFile(backupPath, Settings);
            WriteSettingsFile(settingsPath, profile);
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
            WriteSettingsFile(Path.Combine(ModEntry.Path, "Settings.xml"), Settings);
            Mod.SettingsForSave = null;
            SettingsOperationMessage = null;
        }
        catch (Exception ex)
        {
            Error(ex);
            SettingsOperationMessage = "ModUi/&SettingsImportFailed";
        }
    }

    private static void WriteSettingsFile(string path, Settings settings)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = XmlWriter.Create(temporaryPath, new XmlWriterSettings { Indent = true }))
            {
                new XmlSerializer(typeof(Settings)).Serialize(writer, settings);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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
