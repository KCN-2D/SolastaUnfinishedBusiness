using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal sealed class ModManager<TSettings> where TSettings : UnityModManager.ModSettings, new()
{
    private Harmony _harmony;

    internal TSettings Settings { get; set; }

    internal TSettings SettingsForSave { get; set; }

    internal void Enable(UnityModManager.ModEntry modEntry, Type[] types)
    {
        Settings = UnityModManager.ModSettings.Load<TSettings>(modEntry);
        _harmony = new Harmony(modEntry.Info.Id);

        var failures = new List<Exception>();

        foreach (var type in types)
        {
            try
            {
                // Let the processor read the attributes once, on patch types only.
                if (type.IsDefined(typeof(HarmonyAttribute), inherit: true))
                {
                    _harmony.CreateClassProcessor(type).Patch();
                }
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException($"Harmony patch failed for {type.FullName}.", ex));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException($"Failed to apply Harmony patches for {failures.Count} type(s).", failures);
        }

        modEntry.OnSaveGUI += SaveSettings;
    }

    internal void Unload(UnityModManager.ModEntry modEntry)
    {
        modEntry.OnSaveGUI -= SaveSettings;

        if (_harmony == null || Main.IsApplicationQuitting)
        {
            return;
        }

        // Remove all of our patch methods on each original in a single rebuild.
        foreach (var original in _harmony.GetPatchedMethods().ToArray())
        {
            try
            {
                _harmony.Unpatch(original, HarmonyPatchType.All, _harmony.Id);
            }
            catch (Exception ex)
            {
                Main.Error($"Failed to remove Harmony patches from {original.FullDescription()}: {ex}");
            }
        }
    }

    private void SaveSettings(UnityModManager.ModEntry modEntry)
    {
        (SettingsForSave ?? Settings).Save(modEntry);
    }
}
