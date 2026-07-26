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
    private static ModManager<Core, Settings> Mod { get; set; }
    private static UnityModManager.ModEntry ModEntry { get; set; }

    internal static bool Enabled { get; private set; }
    internal static bool IsApplicationQuitting { get; private set; }

    internal static Action Enable { get; private set; }

    internal static string SettingsFolder => Path.Combine(ModFolder, "Settings");
    internal static string Version => ModEntry?.Info?.Version?.ToString() ?? "<unavailable>";
    internal static string[] SettingsFiles { get; private set; }
    internal static string SettingsFilename { get; private set; } = string.Empty;
    internal static Settings Settings => Mod.Settings;

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
        Application.wantsToQuit -= OnApplicationWantsToQuit;
        Application.wantsToQuit += OnApplicationWantsToQuit;
        Application.quitting -= OnApplicationQuitting;
        Application.quitting += OnApplicationQuitting;

        var now = DateTime.Now;
        var assembly = Assembly.GetExecutingAssembly();

        var compatibility = UmmCompatibility.CheckRuntime();
        Info(compatibility.LogMessage);

        if (!compatibility.IsSupported)
        {
            return false;
        }

        EnsureFolderExists(SettingsFolder);
        PortraitsContext.EnsureFolderExists();
        SaveByLocationContext.EnsureFoldersExist();

        try
        {
            Mod = new ModManager<Core, Settings>();
            Mod.Enable(modEntry, assembly);

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

            TranslatorContext.EarlyLoad();
        }
        catch (Exception ex)
        {
            Error(ex);
            throw;
        }

        return true;
    }

    private static bool OnApplicationWantsToQuit()
    {
        IsApplicationQuitting = true;

        return true;
    }

    private static void OnApplicationQuitting()
    {
        IsApplicationQuitting = true;
    }

    private static bool OnUnload(UnityModManager.ModEntry modEntry)
    {
        var applicationQuitting = IsApplicationQuitting;

        TryCleanup(() =>
        {
            Menu?.Unload(modEntry);
            Menu = null;
        });
        TryCleanup(() => modEntry.OnShowGUI = null);
        TryCleanup(() => Application.wantsToQuit -= OnApplicationWantsToQuit);
        TryCleanup(() => Application.quitting -= OnApplicationQuitting);
        TryCleanup(() => SpeechContext.Unload(!applicationQuitting));
        TryCleanup(UpdateContext.Unload);
        TryCleanup(() => SaveByLocationContext.Unload(!applicationQuitting));
        TryCleanup(CombatAiContext.Unload);
        TryCleanup(CombatAnimationContext.Unload);
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

        EnsureFolderExists(SettingsFolder);
        SettingsFilename = Path.Combine(SettingsFolder, normalizedFilename);

        try
        {
            UnityModManager.ModSettings.Save(Settings, ModEntry);
        }
        finally
        {
            SettingsFilename = string.Empty;
        }

        LoadSettingFilenames();
    }

    internal static void LoadSettings(string filename)
    {
        if (!TryNormalizeSettingsFilename(filename, out var normalizedFilename))
        {
            return;
        }

        EnsureFolderExists(SettingsFolder);
        SettingsFilename = Path.Combine(SettingsFolder, normalizedFilename);

        try
        {
            Mod.Settings = UnityModManager.ModSettings.Load<Settings>(ModEntry);
        }
        finally
        {
            SettingsFilename = string.Empty;
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
