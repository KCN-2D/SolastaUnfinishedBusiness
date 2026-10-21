using System.Diagnostics;
using System.Reflection;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static class UmmCompatibility
{
    internal static void LogRuntime()
    {
        var assembly = typeof(UnityModManager).Assembly;
        var assemblyVersion = assembly.GetName().Version;
        var versionInfo = GetVersionInfo(assembly);
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ??
                          versionInfo?.FileVersion ?? "unknown";
        var productVersion = versionInfo?.ProductVersion ?? "unknown";

        // UMM checks Info.json's ManagerVersion before calling the mod entry point.
        Main.Info(
            $"Unity Mod Manager runtime assembly={assemblyVersion?.ToString() ?? "unknown"}, " +
            $"file={fileVersion}, product={productVersion}.");
    }

    private static FileVersionInfo GetVersionInfo(Assembly assembly)
    {
        try
        {
            return string.IsNullOrEmpty(assembly.Location)
                ? null
                : FileVersionInfo.GetVersionInfo(assembly.Location);
        }
        catch
        {
            return null;
        }
    }
}
