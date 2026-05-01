using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness;

internal static class Main
{
    internal static readonly string ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    private static ModManager<Core, Settings> Mod { get; set; }
    private static UnityModManager.ModEntry ModEntry { get; set; }

    internal static bool Enabled { get; private set; }

    internal static Action Enable { get; private set; }

    internal static string SettingsFolder => Path.Combine(ModFolder, "Settings");
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

                new MenuManager().Enable(modEntry, assembly);
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
