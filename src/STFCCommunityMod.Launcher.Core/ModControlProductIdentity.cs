namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// Separates the product's public identity from identifiers retained for
/// compatibility with pre-rename installations and signed update artifacts.
/// </summary>
public static class ModControlProductIdentity
{
    public const string ProductName = "STFC Mod Control";
    public const string ShortName = "Mod Control";
    public const string Descriptor = "Install · Configure · Diagnose · Run";
    public const string Description =
        "A standalone Windows application for installing, updating, repairing, configuring, diagnosing, and running supported Star Trek Fleet Command community-mod distributions.";

    public const string LegacyProductName = "STFC Community Mod Launcher";
    public const string LegacyProgramDirectoryName = LegacyProductName;
    public const string LegacyStateDirectoryName = LegacyProductName;
    public const string LegacyExecutableName = "STFCCommunityMod.Launcher.exe";
    public const string LegacyUpdaterExecutableName = "STFCCommunityMod.Launcher.Updater.exe";
    public const string LegacyProcessName = "STFCCommunityMod.Launcher";

    public const string StartMenuGroupName = "STFC Community Mod";
    public const string ShortcutFileName = ProductName + ".lnk";
    public const string LegacyShortcutFileName = LegacyProductName + ".lnk";
    public const string UninstallRegistryKeyName = "STFCModControl";
}
