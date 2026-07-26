using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal interface IModEventHandler
{
    int Priority { get; }

    void HandleModEnable();
}

internal interface IModUnloadHandler
{
    int Priority { get; }

    void HandleModUnload();
}

internal sealed class ModManager<TCore, TSettings>
    where TCore : class, new()
    where TSettings : UnityModManager.ModSettings, new()
{
    #region Settings

    private void HandleSaveGUI(UnityModManager.ModEntry modEntry)
    {
        UnityModManager.ModSettings.Save(Settings, modEntry);
    }

    #endregion

    #region Toggle

    private Harmony _harmonyInstance;

    internal void Enable([NotNull] UnityModManager.ModEntry modEntry, Assembly assembly)
    {
        if (Enabled)
        {
            return;
        }

        try
        {
            var types = AssemblyTypeCache.GetTypes(assembly);

            LoadSettingsAndCore(modEntry);
            ApplyHarmonyPatches(modEntry, types);

            Enabled = true;

            if (!LoadedOnce)
            {
                CreateEventHandlers(types);
                NotifyEventHandlers();
            }

            LoadedOnce = true;
        }
        catch (Exception e)
        {
            Main.Error(e);
            throw;
        }
    }

    private void LoadSettingsAndCore(UnityModManager.ModEntry modEntry)
    {
        RegisterSaveGUI(modEntry);

        if (LoadedOnce)
        {
            return;
        }

        Settings = UnityModManager.ModSettings.Load<TSettings>(modEntry);
        Core = new TCore();
    }

    private void RegisterSaveGUI(UnityModManager.ModEntry modEntry)
    {
        if (SaveGuiRegistered)
        {
            return;
        }

        modEntry.OnSaveGUI += HandleSaveGUI;
        SaveGuiRegistered = true;
    }

    private void ApplyHarmonyPatches(UnityModManager.ModEntry modEntry, Type[] types)
    {
        if (Patched)
        {
            return;
        }

        _harmonyInstance ??= new Harmony(modEntry.Info.Id);

        var failures = new List<PatchFailure>();

        foreach (var type in types)
        {
            var harmonyMethods = HarmonyMethodExtensions.GetFromType(type);
            if (harmonyMethods == null || harmonyMethods.Count == 0)
            {
                continue;
            }

            try
            {
                var patchProcessor = _harmonyInstance.CreateClassProcessor(type);
                patchProcessor.Patch();
            }
            catch (Exception e)
            {
                failures.Add(new PatchFailure(type.FullName ?? type.Name, e));
            }
        }

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Main.Error(
                    $"Harmony patch failed for {failure.TypeName}:{Environment.NewLine}{failure.Exception}");
            }

            CleanupFailedPatchApplication(modEntry);

            var failureSummary = string.Join(
                Environment.NewLine,
                failures.Select(x => $"- {x.TypeName}: {x.Exception.GetType().Name}: {x.Exception.Message}"));

            throw new InvalidOperationException(
                $"Failed to apply Harmony patches for {failures.Count} type(s):{Environment.NewLine}{failureSummary}");
        }

        Patched = true;
    }

    private void CleanupFailedPatchApplication(UnityModManager.ModEntry modEntry)
    {
        try
        {
            _harmonyInstance?.UnpatchAll(modEntry.Info.Id);
        }
        catch (Exception rollbackException)
        {
            Main.Error(
                $"Failed to rollback Harmony patches for {modEntry.Info.Id}:{Environment.NewLine}{rollbackException}");
        }
        finally
        {
            Patched = false;
            Enabled = false;
        }

        try
        {
            if (SaveGuiRegistered)
            {
                modEntry.OnSaveGUI -= HandleSaveGUI;
            }
        }
        catch (Exception cleanupException)
        {
            Main.Error(
                $"Failed to cleanup SaveGUI after Harmony patch failure:{Environment.NewLine}{cleanupException}");
        }
        finally
        {
            SaveGuiRegistered = false;
        }
    }

    private void CreateEventHandlers(Type[] types)
    {
        var instances = types
            .Where(type => type != typeof(TCore) &&
                           !type.IsInterface && !type.IsAbstract &&
                           (typeof(IModEventHandler).IsAssignableFrom(type) ||
                            typeof(IModUnloadHandler).IsAssignableFrom(type)))
            .Select(type => Activator.CreateInstance(type, true))
            .Where(x => x != null)
            .ToList();

        _eventHandlers = instances
            .OfType<IModEventHandler>()
            .ToList();

        _unloadHandlers = instances
            .OfType<IModUnloadHandler>()
            .ToList();

        if (Core is IModEventHandler core)
        {
            _eventHandlers.Add(core);
        }

        if (Core is IModUnloadHandler unloadCore)
        {
            _unloadHandlers.Add(unloadCore);
        }

        _eventHandlers.Sort((x, y) => x.Priority.CompareTo(y.Priority));
        _unloadHandlers.Sort((x, y) => y.Priority.CompareTo(x.Priority));
    }

    private void NotifyEventHandlers()
    {
        foreach (var t in _eventHandlers)
        {
            t.HandleModEnable();
        }
    }

    internal bool Unload(UnityModManager.ModEntry modEntry)
    {
        foreach (var unloadHandler in _unloadHandlers)
        {
            TryCleanup(unloadHandler.HandleModUnload);
        }

        TryCleanup(() =>
        {
            if (!SaveGuiRegistered)
            {
                return;
            }

            modEntry.OnSaveGUI -= HandleSaveGUI;
            SaveGuiRegistered = false;
        });

        TryCleanup(() =>
        {
            if (!Patched || Main.IsApplicationQuitting)
            {
                return;
            }

            _harmonyInstance?.UnpatchAll(modEntry.Info.Id);
            Patched = false;
        });

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
            Main.Error(ex);
        }
    }

    #endregion

    #region Fields & Properties

    private List<IModEventHandler> _eventHandlers = [];
    private List<IModUnloadHandler> _unloadHandlers = [];

    private TCore Core { get; set; }

    internal TSettings Settings { get; set; }

    private bool Enabled { get; set; }

    private bool Patched { get; set; }
    private bool LoadedOnce { get; set; }
    private bool SaveGuiRegistered { get; set; }

    private sealed class PatchFailure
    {
        internal PatchFailure(string typeName, Exception exception)
        {
            TypeName = typeName;
            Exception = exception;
        }

        internal string TypeName { get; }
        internal Exception Exception { get; }
    }

    #endregion
}
