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
            var types = assembly.GetTypes();

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
        if (LoadedOnce)
        {
            return;
        }

        modEntry.OnSaveGUI += HandleSaveGUI;
        Settings = UnityModManager.ModSettings.Load<TSettings>(modEntry);
        Core = new TCore();
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
            var failureSummary = string.Join(
                Environment.NewLine,
                failures.Select(x => $"- {x.TypeName}: {x.Exception.GetType().Name}: {x.Exception.Message}"));

            throw new InvalidOperationException(
                $"Failed to apply Harmony patches for {failures.Count} type(s):{Environment.NewLine}{failureSummary}");
        }

        Patched = true;
    }

    private void CreateEventHandlers(Type[] types)
    {
        _eventHandlers = types.Where(type => type != typeof(TCore) &&
                                             !type.IsInterface && !type.IsAbstract &&
                                             typeof(IModEventHandler).IsAssignableFrom(type))
            .Select(type => Activator.CreateInstance(type, true) as IModEventHandler)
            .Where(x => x != null)
            .ToList();

        if (Core is IModEventHandler core)
        {
            _eventHandlers.Add(core);
        }

        _eventHandlers.Sort((x, y) => x.Priority - y.Priority);
    }

    private void NotifyEventHandlers()
    {
        foreach (var t in _eventHandlers)
        {
            t.HandleModEnable();
        }
    }

    #endregion

    #region Fields & Properties

    private List<IModEventHandler> _eventHandlers;

    private TCore Core { get; set; }

    internal TSettings Settings { get; set; }

    private bool Enabled { get; set; }

    private bool Patched { get; set; }
    private bool LoadedOnce { get; set; }

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
