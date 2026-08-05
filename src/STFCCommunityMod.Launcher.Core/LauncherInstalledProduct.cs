using System.Reflection;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherSetupAction
{
    Install,
    Update,
    Repair,
}

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

    public static LauncherSetupAction DetermineSetupAction(
        bool programDirectoryExists,
        bool launcherExists,
        string? installedProductVersion,
        string setupVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupVersion);

        if (!programDirectoryExists && !launcherExists)
        {
            return LauncherSetupAction.Install;
        }
        if (!launcherExists)
        {
            return LauncherSetupAction.Repair;
        }

        var installedVersion = NormalizeVersion(installedProductVersion);
        var normalizedSetupVersion = NormalizeVersion(setupVersion);
        if (installedVersion is null
            || normalizedSetupVersion is null
            || string.Equals(installedVersion, normalizedSetupVersion, StringComparison.OrdinalIgnoreCase))
        {
            return LauncherSetupAction.Repair;
        }

        return CompareVersions(normalizedSetupVersion, installedVersion) > 0
            ? LauncherSetupAction.Update
            : LauncherSetupAction.Repair;
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

    private static int CompareVersions(string left, string right)
    {
        if (!TrySplitVersion(left, out var leftVersion, out var leftPrerelease)
            || !TrySplitVersion(right, out var rightVersion, out var rightPrerelease))
        {
            return 0;
        }

        var versionComparison = leftVersion.CompareTo(rightVersion);
        if (versionComparison != 0)
        {
            return versionComparison;
        }
        if (leftPrerelease is null)
        {
            return rightPrerelease is null ? 0 : 1;
        }
        if (rightPrerelease is null)
        {
            return -1;
        }

        var leftParts = leftPrerelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = rightPrerelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length)
            {
                return -1;
            }
            if (index >= rightParts.Length)
            {
                return 1;
            }

            var leftNumeric = int.TryParse(leftParts[index], out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[index], out var rightNumber);
            var comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric
                    ? -1
                    : rightNumeric
                        ? 1
                        : string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool TrySplitVersion(string value, out Version version, out string? prerelease)
    {
        var separator = value.IndexOf('-', StringComparison.Ordinal);
        var versionText = separator < 0 ? value : value[..separator];
        prerelease = separator < 0 ? null : value[(separator + 1)..];
        return Version.TryParse(versionText, out version!);
    }
}
