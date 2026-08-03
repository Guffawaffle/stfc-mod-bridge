namespace STFCCommunityMod.Launcher.Core;

public enum LauncherHealthDimensionCategory
{
    ProcessSafety,
    InstallationSelection,
    Discovery,
    ModInstallation,
    BinaryProvenance,
    ProviderCompatibility,
    UpdateAvailability,
    GameCompatibility,
    RuntimeActivation,
    NativeSupport,
    ProviderAvailability,
}

public enum LauncherHealthSeverity
{
    Healthy,
    Informational,
    ActionRequired,
    Unknown,
}

public sealed record LauncherHealthDimension(
    LauncherHealthDimensionCategory Category,
    LauncherHealthSeverity Severity,
    string Title,
    string Detail,
    string? TechnicalDetail = null);
