using System;
using System.Diagnostics;
using System.Reflection;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static class UmmCompatibility
{
    private static readonly Version LegacyMinimum = new(0, 24, 0);
    private static readonly Version LegacyMaximum = new(0, 27, 10);
    private static readonly Version SupportedModernFamily = new(0, 32, 4);

    internal static CheckResult CheckRuntime()
    {
        var assembly = typeof(UnityModManager).Assembly;
        var assemblyVersion = assembly.GetName().Version;
        var fileVersion = GetFileVersion(assembly);
        var productVersion = GetProductVersion(assembly);

        if (assemblyVersion == null)
        {
            return new CheckResult(
                false,
                "unknown",
                fileVersion,
                productVersion,
                "rejected",
                "runtime assembly version could not be read");
        }

        var assemblyVersionText = assemblyVersion.ToString();

        if (IsSameMajorMinorBuild(assemblyVersion, SupportedModernFamily))
        {
            return new CheckResult(
                true,
                assemblyVersionText,
                fileVersion,
                productVersion,
                "supported",
                "UMM 0.32.4 runtime family");
        }

        if (IsBetweenMajorMinorBuild(assemblyVersion, LegacyMinimum, LegacyMaximum))
        {
            return new CheckResult(
                true,
                assemblyVersionText,
                fileVersion,
                productVersion,
                "supported",
                "legacy UMM runtime");
        }

        var reason = CompareMajorMinorBuild(assemblyVersion, LegacyMinimum) < 0
            ? "runtime is older than UMM 0.24.0"
            : CompareMajorMinorBuild(assemblyVersion, SupportedModernFamily) > 0
                ? "runtime is newer than the verified UMM 0.32.4 family"
                : "runtime is outside the verified UMM 0.24.0-0.27.10 and 0.32.4 ranges";

        return new CheckResult(
            false,
            assemblyVersionText,
            fileVersion,
            productVersion,
            "rejected",
            reason);
    }

    private static bool IsSameMajorMinorBuild(Version version, Version expected)
    {
        return version.Major == expected.Major &&
               version.Minor == expected.Minor &&
               GetBuild(version) == GetBuild(expected);
    }

    private static bool IsBetweenMajorMinorBuild(Version version, Version minimum, Version maximum)
    {
        return CompareMajorMinorBuild(version, minimum) >= 0 &&
               CompareMajorMinorBuild(version, maximum) <= 0;
    }

    private static int CompareMajorMinorBuild(Version left, Version right)
    {
        var major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = left.Minor.CompareTo(right.Minor);
        if (minor != 0)
        {
            return minor;
        }

        return GetBuild(left).CompareTo(GetBuild(right));
    }

    private static int GetBuild(Version version)
    {
        return version.Build < 0 ? 0 : version.Build;
    }

    private static string GetFileVersion(Assembly assembly)
    {
        var attributeVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;

        if (!string.IsNullOrEmpty(attributeVersion))
        {
            return attributeVersion;
        }

        return GetVersionInfo(assembly)?.FileVersion ?? "unknown";
    }

    private static string GetProductVersion(Assembly assembly)
    {
        return GetVersionInfo(assembly)?.ProductVersion ?? "unknown";
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

    internal sealed class CheckResult
    {
        internal CheckResult(
            bool isSupported,
            string assemblyVersion,
            string fileVersion,
            string productVersion,
            string decision,
            string reason)
        {
            IsSupported = isSupported;
            AssemblyVersion = assemblyVersion;
            FileVersion = fileVersion;
            ProductVersion = productVersion;
            Decision = decision;
            Reason = reason;
        }

        internal bool IsSupported { get; }
        internal string AssemblyVersion { get; }
        internal string FileVersion { get; }
        internal string ProductVersion { get; }
        internal string Decision { get; }
        internal string Reason { get; }

        internal string LogMessage =>
            $"Unity Mod Manager runtime assembly={AssemblyVersion}, file={FileVersion}, product={ProductVersion}: {Decision} ({Reason}).";
    }
}
