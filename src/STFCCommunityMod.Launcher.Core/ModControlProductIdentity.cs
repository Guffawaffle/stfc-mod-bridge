namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// Owns the product's public and on-disk identity.
/// </summary>
public static class ModControlProductIdentity
{
    public const string ProductName = "STFC Mod Control";
    public const string ShortName = "Mod Control";
    public const string Descriptor = "Install · Configure · Diagnose · Run";
    public const string Description =
        "A standalone Windows application for installing, updating, repairing, configuring, diagnosing, and running supported Star Trek Fleet Command community-mod distributions.";

    public const string ProgramDirectoryName = ProductName;
    public const string StateDirectoryName = ProductName;
    public const string ExecutableName = "STFCModControl.exe";
    public const string UpdaterExecutableName = "STFCModControl.Updater.exe";
    public const string SetupExecutableName = "STFCModControl.Setup.exe";
    public const string ProcessName = "STFCModControl";
    public const string UpdateArchiveName = "stfc-mod-control-win-x64.zip";
    public const string ReleaseManifestName = "stfc-mod-control-release-manifest.json";

    public const string StartMenuGroupName = ProductName;
    public const string ShortcutFileName = ProductName + ".lnk";
    public const string UninstallRegistryKeyName = "STFCModControl";
}
