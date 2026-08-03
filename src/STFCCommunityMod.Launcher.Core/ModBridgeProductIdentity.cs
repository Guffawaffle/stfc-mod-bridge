namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// Owns the product's public and on-disk identity.
/// </summary>
public static class ModBridgeProductIdentity
{
    public const string ProductName = "STFC Mod Bridge";
    public const string ShortName = "Mod Bridge";
    public const string Descriptor = "Install · Configure · Diagnose · Run";
    public const string Description =
        "A standalone Windows application for installing, updating, repairing, configuring, diagnosing, and running supported Star Trek Fleet Command community-mod distributions.";

    public const string ProgramDirectoryName = ProductName;
    public const string StateDirectoryName = ProductName;
    public const string ExecutableName = "STFCModBridge.exe";
    public const string UpdaterExecutableName = "STFCModBridge.Updater.exe";
    public const string SetupExecutableName = "STFCModBridge.Setup.exe";
    public const string ProcessName = "STFCModBridge";
    public const string UpdateArchiveName = "stfc-mod-bridge-win-x64.zip";
    public const string ReleaseManifestName = "stfc-mod-bridge-release-manifest.json";

    public const string StartMenuGroupName = ProductName;
    public const string ShortcutFileName = ProductName + ".lnk";
    public const string UninstallRegistryKeyName = "STFCModBridge";
}
