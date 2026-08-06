using System.Reflection;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherInstalledProduct
{
    public static string DisplayVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var normalized = NormalizeVersion(informationalVersion);
        if (normalized is not null)
        {
            return normalized;
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null
            ? "Unknown"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }

    public static string? NormalizeVersion(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            return null;
        }

        var value = productVersion.Trim();
        var metadataSeparator = value.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparator >= 0)
        {
            value = value[..metadataSeparator];
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
