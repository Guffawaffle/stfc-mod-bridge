namespace STFCCommunityMod.Launcher.Core;

public enum LauncherHealthDimensionCategory
{
    ProcessSafety,
    InstallationSelection,
    Discovery,
}

public enum LauncherHealthSeverity
{
    Healthy,
    Informational,
    ActionRequired,
}

public sealed record LauncherHealthDimension(
    LauncherHealthDimensionCategory Category,
    LauncherHealthSeverity Severity,
    string Title,
    string Detail);
